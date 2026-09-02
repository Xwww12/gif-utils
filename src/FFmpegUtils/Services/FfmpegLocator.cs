using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class FfmpegLocator
{
    public async Task<FfmpegInstallation?> FindAsync(string? preferredPath, CancellationToken cancellationToken = default)
    {
        var candidates = BuildCandidateList(preferredPath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return await InspectAsync(candidate, cancellationToken);
            }
            catch
            {
                // Continue trying other installations.
            }
        }

        foreach (var root in CommonFfmpegRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = await Task.Run(() => FindUnderRoot(root), cancellationToken);
            if (discovered is null)
            {
                continue;
            }

            try
            {
                return await InspectAsync(discovered, cancellationToken);
            }
            catch
            {
                // Keep looking.
            }
        }

        return null;
    }

    public async Task<FfmpegInstallation> InspectAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(ffmpegPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg.exe。", fullPath);
        }

        var ffprobePath = Path.Combine(Path.GetDirectoryName(fullPath)!, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
        {
            throw new FileNotFoundException("ffmpeg.exe 同目录下缺少 ffprobe.exe。", ffprobePath);
        }

        var versionResult = await ProcessCapture.RunAsync(fullPath, ["-version"], cancellationToken);
        if (versionResult.ExitCode != 0)
        {
            throw new InvalidOperationException("FFmpeg 无法正常运行。" + Environment.NewLine + versionResult.StandardError.Trim());
        }

        var version = versionResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "FFmpeg";

        var filterResult = await ProcessCapture.RunAsync(fullPath, ["-hide_banner", "-filters"], cancellationToken);
        var filters = filterResult.StandardOutput + filterResult.StandardError;
        var hasGifFilters = filters.Contains("palettegen", StringComparison.OrdinalIgnoreCase)
                            && filters.Contains("paletteuse", StringComparison.OrdinalIgnoreCase);
        var hasSubtitleFilter = filters.Contains(" subtitles ", StringComparison.OrdinalIgnoreCase)
                                || filters.Contains("subtitles", StringComparison.OrdinalIgnoreCase);

        var encoderResult = await ProcessCapture.RunAsync(fullPath, ["-hide_banner", "-encoders"], cancellationToken);
        var encoders = encoderResult.StandardOutput + encoderResult.StandardError;
        var hasNvencEncoder = encoders.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
        var hasQsvEncoder = encoders.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase);
        var hasAmfEncoder = encoders.Contains("h264_amf", StringComparison.OrdinalIgnoreCase);

        return new FfmpegInstallation(
            fullPath,
            ffprobePath,
            version,
            hasGifFilters,
            hasSubtitleFilter,
            hasNvencEncoder,
            hasQsvEncoder,
            hasAmfEncoder);
    }

    private static IEnumerable<string> BuildCandidateList(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            yield return preferredPath;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");

        var configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Directory.Exists(configured) ? Path.Combine(configured, "ffmpeg.exe") : configured;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(part.Trim('"'), "ffmpeg.exe");
        }
    }

    private static IEnumerable<string> CommonFfmpegRoots()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            string root;
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                root = Path.Combine(drive.RootDirectory.FullName, "ffmpeg");
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    private static string? FindUnderRoot(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
