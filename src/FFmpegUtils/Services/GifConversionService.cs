using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class GifConversionService(FfmpegProcessRunner runner)
{
    public async Task<ConversionResult> ConvertAsync(
        FfmpegInstallation installation,
        MediaInfo media,
        GifConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!installation.HasGifFilters)
        {
            throw new InvalidOperationException("当前 FFmpeg 不包含 palettegen/paletteuse 滤镜，无法生成高质量 GIF。" );
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("保存目录不存在。" );
        }

        var targetBytes = options.TargetSizeMb is > 0
            ? (long)(options.TargetSizeMb.Value * 1024 * 1024)
            : 0;
        var maxAttempts = targetBytes > 0 ? 4 : 1;
        var minimumWidth = Math.Max(2, Math.Min(240, media.Width));
        var parameters = new GifSizeParameters(
            Math.Clamp(Math.Min(options.MaxWidth, media.Width), 2, Math.Max(2, media.Width)),
            Math.Clamp(options.FrameRate, 5, 30),
            Math.Clamp(options.Colors, 32, 256));

        var effectiveDuration = CalculateDuration(media.DurationSeconds, options.StartSeconds, options.EndSeconds);
        var workDirectory = Path.Combine(Path.GetTempPath(), "FFmpegUtils", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        string? latestCandidate = null;
        long latestSize = 0;
        var attemptsUsed = 0;

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptsUsed = attempt;

                if (latestCandidate is not null)
                {
                    TryDelete(latestCandidate);
                }

                var palettePath = Path.Combine(workDirectory, $"palette-{attempt}.png");
                latestCandidate = Path.Combine(
                    outputDirectory,
                    $".{Path.GetFileNameWithoutExtension(options.OutputPath)}.ffmpegutils-{Guid.NewGuid():N}.gif");

                var attemptStart = (attempt - 1d) / maxAttempts;
                var attemptEnd = attempt / (double)maxAttempts;
                var paletteEnd = attemptStart + (attemptEnd - attemptStart) * 0.35;
                var stageSuffix = maxAttempts > 1 ? $"（第 {attempt}/{maxAttempts} 轮）" : string.Empty;

                var baseVideoFilter = FormattableString.Invariant(
                    $"fps={parameters.FrameRate},scale='min({parameters.Width},iw)':-1:flags=lanczos");
                var paletteFilter = $"{baseVideoFilter},palettegen=max_colors={parameters.Colors}:stats_mode=diff";

                var paletteArguments = CreateProgressArguments();
                paletteArguments.AddRange(VideoTimeRange.InputArguments(options.InputPath, options.StartSeconds, options.EndSeconds));
                paletteArguments.AddRange(["-vf", paletteFilter, "-frames:v", "1", palettePath]);

                progress?.Report(new ConversionProgress(attemptStart * 100, $"正在分析颜色{stageSuffix}",
                    $"{parameters.Width}px · {parameters.FrameRate} FPS · {parameters.Colors} 色"));
                await runner.RunAsync(
                    installation.FfmpegPath,
                    paletteArguments,
                    effectiveDuration,
                    $"正在分析颜色{stageSuffix}",
                    attemptStart,
                    paletteEnd,
                    progress,
                    cancellationToken);

                var ditherOptions = BuildDitherOptions(options.Dither);
                var gifFilter = $"[0:v]{baseVideoFilter}[video];[video][1:v]paletteuse={ditherOptions}:diff_mode=rectangle";
                var gifArguments = CreateProgressArguments();
                gifArguments.AddRange(VideoTimeRange.InputArguments(options.InputPath, options.StartSeconds, options.EndSeconds));
                gifArguments.AddRange(["-i", palettePath]);
                gifArguments.AddRange(["-filter_complex", gifFilter, "-loop", "0", latestCandidate]);

                progress?.Report(new ConversionProgress(paletteEnd * 100, $"正在生成 GIF{stageSuffix}"));
                await runner.RunAsync(
                    installation.FfmpegPath,
                    gifArguments,
                    effectiveDuration,
                    $"正在生成 GIF{stageSuffix}",
                    paletteEnd,
                    attemptEnd,
                    progress,
                    cancellationToken);

                TryDelete(palettePath);
                latestSize = new FileInfo(latestCandidate).Length;
                if (targetBytes <= 0 || latestSize <= targetBytes)
                {
                    break;
                }

                parameters = GifSizeTuner.Reduce(parameters, latestSize, targetBytes, minimumWidth);
            }

            if (latestCandidate is null || !File.Exists(latestCandidate))
            {
                throw new InvalidOperationException("GIF 输出文件没有生成。" );
            }

            File.Move(latestCandidate, options.OutputPath, overwrite: true);
            latestCandidate = null;
            progress?.Report(new ConversionProgress(100, "转换完成", MediaInfo.FormatBytes(latestSize)));

            var warning = targetBytes > 0 && latestSize > targetBytes
                ? $"已尽量压缩，但当前内容在最低质量保护范围内仍为 {MediaInfo.FormatBytes(latestSize)}。可继续缩短时长或降低自定义参数。"
                : null;

            return new ConversionResult(options.OutputPath, latestSize, attemptsUsed, warning);
        }
        finally
        {
            if (latestCandidate is not null)
            {
                TryDelete(latestCandidate);
            }

            TryDeleteDirectory(workDirectory);
        }
    }

    private static List<string> CreateProgressArguments()
        => ["-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-stats_period", "0.2", "-nostats"];

    private static double CalculateDuration(double sourceDuration, double? startSeconds, double? endSeconds)
    {
        var start = Math.Clamp(startSeconds ?? 0, 0, Math.Max(0, sourceDuration));
        var end = endSeconds is > 0 ? Math.Min(endSeconds.Value, sourceDuration) : sourceDuration;
        return Math.Max(0.1, end - start);
    }

    private static string BuildDitherOptions(string dither)
        => dither switch
        {
            "bayer" => "dither=bayer:bayer_scale=3",
            "none" => "dither=none",
            _ => "dither=sierra2_4a"
        };

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary cleanup is best effort.
        }
    }
}
