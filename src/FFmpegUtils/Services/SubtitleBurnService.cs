using System.Globalization;
using System.Text;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class SubtitleBurnService(FfmpegProcessRunner runner)
{
    private readonly Dictionary<string, SubtitleVideoEncoder> _encoderCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ConversionResult> BurnAsync(
        FfmpegInstallation installation,
        MediaInfo media,
        SubtitleBurnOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!installation.HasSubtitleFilter)
        {
            throw new InvalidOperationException("当前 FFmpeg 不包含 subtitles/libass 滤镜，无法烧录字幕。" );
        }

        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("保存目录不存在。" );
        }

        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        if (extension is not ".mp4" and not ".mkv")
        {
            throw new InvalidOperationException("字幕烧录当前支持保存为 MP4 或 MKV。" );
        }

        var temporaryOutput = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.ffmpegutils-{Guid.NewGuid():N}{extension}");

        try
        {
            var subtitleFilter = BuildSubtitleFilter(options.SubtitlePath, options.SubtitleEncoding);
            var encoder = await ResolveVideoEncoderAsync(
                installation,
                options.VideoEncoder,
                options.Crf,
                options.Preset,
                progress,
                cancellationToken);
            var arguments = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-progress", "pipe:1", "-stats_period", "0.2", "-nostats",
                "-i", options.InputPath,
                "-map", "0:v:0", "-map", "0:a?",
                "-vf", subtitleFilter
            };
            arguments.AddRange(BuildVideoEncoderArguments(encoder, options.Crf, options.Preset));
            arguments.AddRange(["-sn", "-map_metadata", "0"]);

            if (extension == ".mp4")
            {
                arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            }
            else
            {
                arguments.AddRange(["-c:a", "copy"]);
            }

            arguments.Add(temporaryOutput);
            var encoderName = SubtitleVideoEncoderCatalog.GetDisplayName(encoder);
            progress?.Report(new ConversionProgress(0, "正在烧录字幕", $"使用 {encoderName}"));
            await runner.RunAsync(
                installation.FfmpegPath,
                arguments,
                Math.Max(0.1, media.DurationSeconds),
                "正在烧录字幕",
                0,
                1,
                progress,
                cancellationToken);

            var size = new FileInfo(temporaryOutput).Length;
            File.Move(temporaryOutput, outputPath, overwrite: true);
            progress?.Report(new ConversionProgress(100, "烧录完成", $"{MediaInfo.FormatBytes(size)} · {encoderName}"));
            return new ConversionResult(outputPath, size, EncoderName: encoderName);
        }
        finally
        {
            TryDelete(temporaryOutput);
        }
    }

    public static IReadOnlyList<string> BuildVideoEncoderArguments(
        SubtitleVideoEncoder encoder,
        int quality,
        string cpuPreset = "medium")
    {
        var value = quality.ToString(CultureInfo.InvariantCulture);
        return encoder switch
        {
            SubtitleVideoEncoder.Cpu =>
                ["-c:v", "libx264", "-preset", cpuPreset, "-crf", value, "-pix_fmt", "yuv420p"],
            SubtitleVideoEncoder.Nvidia =>
                ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", value, "-b:v", "0", "-pix_fmt", "yuv420p"],
            SubtitleVideoEncoder.Intel =>
                ["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", value, "-pix_fmt", "nv12"],
            SubtitleVideoEncoder.Amd =>
                ["-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", value, "-qp_p", value, "-qp_b", value, "-pix_fmt", "nv12"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), "必须先将自动模式解析为具体编码器。")
        };
    }

    private async Task<SubtitleVideoEncoder> ResolveVideoEncoderAsync(
        FfmpegInstallation installation,
        SubtitleVideoEncoder requested,
        int quality,
        string cpuPreset,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{installation.FfmpegPath}|{requested}";
        if (_encoderCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (requested == SubtitleVideoEncoder.Cpu)
        {
            _encoderCache[cacheKey] = SubtitleVideoEncoder.Cpu;
            return SubtitleVideoEncoder.Cpu;
        }

        var candidates = requested == SubtitleVideoEncoder.Auto
            ? new[] { SubtitleVideoEncoder.Nvidia, SubtitleVideoEncoder.Intel, SubtitleVideoEncoder.Amd }
            : new[] { requested };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEncoderCompiled(installation, candidate))
            {
                if (requested != SubtitleVideoEncoder.Auto)
                {
                    throw new InvalidOperationException(
                        $"当前 FFmpeg 不包含 {SubtitleVideoEncoderCatalog.GetCodecName(candidate)} 编码器。请改用自动或 CPU，或更换 FFmpeg Full Build。");
                }

                continue;
            }

            var displayName = SubtitleVideoEncoderCatalog.GetDisplayName(candidate);
            progress?.Report(new ConversionProgress(0, "正在检测编码器", displayName));
            var probe = await ProbeVideoEncoderAsync(
                installation.FfmpegPath,
                candidate,
                quality,
                cpuPreset,
                cancellationToken);
            if (probe.Available)
            {
                _encoderCache[cacheKey] = candidate;
                return candidate;
            }

            if (requested != SubtitleVideoEncoder.Auto)
            {
                var reason = string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : $" 原因：{probe.Error}";
                throw new InvalidOperationException(
                    $"{displayName} 无法初始化，请检查显卡型号和驱动，或改用自动/CPU。{reason}");
            }
        }

        progress?.Report(new ConversionProgress(0, "正在检测编码器", "未检测到可用 GPU，回退到 CPU"));
        _encoderCache[cacheKey] = SubtitleVideoEncoder.Cpu;
        return SubtitleVideoEncoder.Cpu;
    }

    private static bool IsEncoderCompiled(FfmpegInstallation installation, SubtitleVideoEncoder encoder) => encoder switch
    {
        SubtitleVideoEncoder.Nvidia => installation.HasNvencEncoder,
        SubtitleVideoEncoder.Intel => installation.HasQsvEncoder,
        SubtitleVideoEncoder.Amd => installation.HasAmfEncoder,
        SubtitleVideoEncoder.Cpu => true,
        _ => false
    };

    private static async Task<(bool Available, string Error)> ProbeVideoEncoderAsync(
        string ffmpegPath,
        SubtitleVideoEncoder encoder,
        int quality,
        string cpuPreset,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "color=c=black:s=64x64:r=1:d=1", "-frames:v", "1", "-an"
        };
        arguments.AddRange(BuildVideoEncoderArguments(encoder, quality, cpuPreset));
        arguments.AddRange(["-f", "null", "-"]);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await ProcessCapture.RunAsync(ffmpegPath, arguments, timeout.Token);
            if (result.ExitCode == 0)
            {
                return (true, string.Empty);
            }

            return (false, LastUsefulLine(result.StandardError));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "编码器检测超时");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string LastUsefulLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        return line.Length <= 240 ? line : line[..240] + "…";
    }

    public static string BuildSubtitleFilter(string subtitlePath, string requestedEncoding)
    {
        var escapedPath = FfmpegFilterEscaper.EscapePath(subtitlePath);
        var encoding = requestedEncoding == "自动"
            ? DetectEncodingName(subtitlePath)
            : requestedEncoding;
        var filter = $"subtitles=filename='{escapedPath}':charenc={encoding}";

        var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
        if (extension is ".srt" or ".vtt")
        {
            filter += ":force_style='FontName=Microsoft YaHei,FontSize=22,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=0,Alignment=2,MarginV=24'";
        }

        return filter;
    }

    public static string DetectEncodingName(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return "UTF-8";
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            return "GB18030";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary cleanup is best effort.
        }
    }
}
