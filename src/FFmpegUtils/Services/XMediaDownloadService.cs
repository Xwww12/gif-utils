using System.Globalization;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class XMediaDownloadService
{
    public const string ProgressPrefix = "FFU_PROGRESS|";
    public const string AfterMovePrefix = "FFU_AFTER_MOVE|";
    public const string ProgressTemplate = "download:FFU_PROGRESS|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";
    public const string AfterMoveTemplate = "after_move:FFU_AFTER_MOVE|%(filepath)s";

    private readonly YtDlpToolService _toolService;
    private readonly XPostUrlService _urlService;
    private readonly IXTopLevelMediaProbe _topLevelMediaProbe;

    public XMediaDownloadService(
        YtDlpToolService? toolService = null,
        XPostUrlService? urlService = null,
        IXTopLevelMediaProbe? topLevelMediaProbe = null)
    {
        _toolService = toolService ?? new YtDlpToolService();
        _urlService = urlService ?? new XPostUrlService();
        _topLevelMediaProbe = topLevelMediaProbe ?? new XTopLevelMediaProbe();
    }

    public async Task<XParseResult> ParseAsync(
        string input,
        IProgress<XDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new XDownloadProgress(0, "正在验证 X 链接"));
        var source = await _urlService.NormalizeAsync(input, cancellationToken);

        var probeTask = _topLevelMediaProbe.ProbeAsync(source.PostId, cancellationToken);
        IProgress<XDownloadProgress>? toolProgress = progress is null
            ? null
            : new InlineProgress<XDownloadProgress>(value => progress.Report(value with
            {
                Percent = Math.Clamp(value.Percent, 0, 100) * 0.45
            }));
        var toolTask = _toolService.EnsureAvailableAsync(toolProgress, cancellationToken);
        await Task.WhenAll(probeTask, toolTask);
        var probe = await probeTask;
        if (probe.IsVerified && probe.MediaById.Count == 0)
        {
            throw new XDownloadException(XDownloadErrorKind.NoVideo, "该帖子本身没有可下载的视频。");
        }

        progress?.Report(new XDownloadProgress(50, "正在解析 X 媒体"));
        var arguments = new[]
        {
            "--ignore-config",
            "--dump-single-json",
            "--skip-download",
            "--yes-playlist",
            "--no-progress",
            "--no-color",
            "--socket-timeout", "30",
            "--extractor-args", "twitter:api=graphql",
            "--",
            source.CanonicalUri.AbsoluteUri
        };
        var processResult = await YtDlpProcessRunner.CaptureAsync(await toolTask, arguments, cancellationToken);
        if (processResult.ExitCode != 0)
        {
            throw CreateFriendlyException(processResult.StandardError, duringDownload: false);
        }

        if (string.IsNullOrWhiteSpace(processResult.StandardOutput))
        {
            throw new XDownloadException(XDownloadErrorKind.ParseFailed, "X 解析组件没有返回媒体信息。", processResult.StandardError);
        }

        var parsed = XMediaJsonParser.Parse(processResult.StandardOutput, source, probe);
        progress?.Report(new XDownloadProgress(100, "解析完成", $"找到 {parsed.Items.Count} 个媒体"));
        return parsed;
    }

    public async Task<XDownloadResult> DownloadAsync(
        XParseResult parseResult,
        IReadOnlyList<XMediaItem> selectedItems,
        string outputDirectory,
        string prefix,
        string ffmpegPath,
        IProgress<XDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(selectedItems);
        if (!XPostUrlService.TryNormalizeOfficialStatusUrl(
                parseResult.Source.CanonicalUri.AbsoluteUri,
                out var verifiedSource,
                out _)
            || verifiedSource is null
            || !string.Equals(verifiedSource.PostId, parseResult.Source.PostId, StringComparison.Ordinal))
        {
            throw new XDownloadException(XDownloadErrorKind.InvalidUrl, "当前 X 解析结果的来源链接无效，请重新解析。");
        }

        var items = selectedItems.Where(item => item.IsSelected).ToList();
        if (items.Count == 0)
        {
            throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "请至少选择一个媒体。");
        }

        if (items.Any(item => !parseResult.Items.Contains(item) || item.PlaylistIndex <= 0))
        {
            throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "下载列表与当前 X 解析结果不匹配，请重新解析。");
        }

        foreach (var item in items)
        {
            if (!item.QualityOptions.Contains(item.SelectedQuality))
            {
                throw new XDownloadException(XDownloadErrorKind.DownloadFailed, $"{item.DisplayName} 的画质选择已失效，请重新选择。");
            }
        }

        var destination = PrepareOutputDirectory(outputDirectory);
        var validatedFfmpeg = ValidateFfmpegPath(ffmpegPath);
        var toolPath = await _toolService.EnsureAvailableAsync(progress, cancellationToken);
        var safePrefix = XFileNameHelper.SanitizeBaseName(
            string.IsNullOrWhiteSpace(prefix) ? parseResult.SuggestedPrefix : prefix);
        var temporaryRoot = Path.Combine(destination, $".ffmpeg-utils-{Guid.NewGuid():N}");
        var outputPaths = new List<string>(items.Count);
        long totalOutputBytes = 0;

        try
        {
            Directory.CreateDirectory(temporaryRoot);
            TryMarkHidden(temporaryRoot);
            EnsureContainedPath(temporaryRoot, destination);

            for (var itemOffset = 0; itemOffset < items.Count; itemOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[itemOffset];
                var itemDirectory = Path.Combine(temporaryRoot, $"item-{itemOffset + 1:000}");
                Directory.CreateDirectory(itemDirectory);
                EnsureContainedPath(itemDirectory, temporaryRoot);
                var outputTemplate = Path.Combine(itemDirectory, "%(id)s.%(ext)s");
                string? afterMovePath = null;

                var arguments = BuildDownloadArguments(
                    parseResult,
                    item,
                    outputTemplate,
                    validatedFfmpeg);
                var itemNumber = itemOffset + 1;
                var result = await YtDlpProcessRunner.StreamAsync(
                    toolPath,
                    arguments,
                    line =>
                    {
                        if (line.StartsWith(AfterMovePrefix, StringComparison.Ordinal))
                        {
                            afterMovePath = line[AfterMovePrefix.Length..].Trim();
                            return;
                        }

                        if (!TryParseProgressLine(line, out var localProgress))
                        {
                            return;
                        }

                        var localFraction = localProgress.Percent / 100d;
                        var aggregate = (itemOffset + localFraction) / items.Count * 100d;
                        progress?.Report(localProgress with
                        {
                            Percent = aggregate,
                            Stage = $"正在下载 {itemNumber}/{items.Count}"
                        });
                    },
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw CreateFriendlyException(result.StandardError, duringDownload: true);
                }

                var downloadedPath = ResolveDownloadedPath(afterMovePath, itemDirectory);
                cancellationToken.ThrowIfCancellationRequested();
                var finalBaseName = $"{safePrefix}_{item.Index:00}";
                var finalPath = MoveWithoutOverwrite(downloadedPath, destination, finalBaseName);
                var fileSize = new FileInfo(finalPath).Length;
                totalOutputBytes += fileSize;
                outputPaths.Add(finalPath);
                progress?.Report(new XDownloadProgress(
                    itemNumber * 100d / items.Count,
                    $"已完成 {itemNumber}/{items.Count}",
                    $"{Path.GetFileName(finalPath)} · {FormatBytes(fileSize)}",
                    fileSize,
                    fileSize));
            }

            return new XDownloadResult(outputPaths, totalOutputBytes, parseResult.Warning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XDownloadException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.OutputDirectory,
                "无法写入保存目录，请检查目录权限。",
                exception.Message,
                exception);
        }
        catch (IOException exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.OutputDirectory,
                "保存文件失败，请检查磁盘空间、文件名或目录权限。",
                exception.Message,
                exception);
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryRoot, destination);
        }
    }

    public static bool TryParseProgressLine(string line, out XDownloadProgress progress)
    {
        progress = new XDownloadProgress(0, "正在下载");
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = line[ProgressPrefix.Length..].Split('|');
        if (fields.Length < 5)
        {
            return false;
        }

        var downloaded = ParseByteValue(fields[0]);
        var total = ParseByteValue(fields[1]) ?? ParseByteValue(fields[2]);
        var speed = ParseDouble(fields[3]);
        var percent = downloaded is not null && total is > 0
            ? Math.Clamp(downloaded.Value * 100d / total.Value, 0, 100)
            : 0;
        var details = new List<string>();
        if (downloaded is not null)
        {
            details.Add(total is > 0
                ? $"{FormatBytes(downloaded.Value)} / {FormatBytes(total.Value)}"
                : FormatBytes(downloaded.Value));
        }

        if (speed is > 0)
        {
            details.Add($"{FormatBytes((long)speed.Value)}/s");
        }

        progress = new XDownloadProgress(
            percent,
            "正在下载",
            string.Join(" · ", details),
            downloaded,
            total,
            speed);
        return true;
    }

    public static XDownloadException CreateFriendlyException(string details, bool duringDownload)
    {
        var text = details ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var tail = text.Length > 6000 ? text[^6000..] : text;

        if (ContainsAny(lower, "private", "protected", "not authorized", "login required", "authentication", "needs auth"))
        {
            return new XDownloadException(
                XDownloadErrorKind.AuthenticationRequired,
                "该帖子不能以公开方式访问，可能是私密内容或需要登录。",
                tail);
        }

        if (ContainsAny(lower, "age-restricted", "age restricted", "nsfw"))
        {
            return new XDownloadException(XDownloadErrorKind.AgeRestricted, "该帖子受年龄限制，公开访客模式无法解析。", tail);
        }

        if (ContainsAny(lower, "geo-restricted", "geo restricted", "not available in your country", "geoblocked"))
        {
            return new XDownloadException(XDownloadErrorKind.GeoRestricted, "该媒体存在地区限制，当前网络位置无法访问。", tail);
        }

        if (ContainsAny(lower, "rate-limit", "rate limit", "http error 429", "too many requests"))
        {
            return new XDownloadException(XDownloadErrorKind.RateLimited, "X 请求过于频繁，请稍后再试。", tail);
        }

        if (ContainsAny(lower, "no video could be found", "no formats", "requested format is not available"))
        {
            return new XDownloadException(XDownloadErrorKind.NoVideo, "该帖子没有可下载的视频，或所选画质已失效。", tail);
        }

        if (ContainsAny(lower, "ffmpeg not found", "ffmpeg is not installed", "postprocessing: ffmpeg"))
        {
            return new XDownloadException(XDownloadErrorKind.FfmpegMissing, "需要 FFmpeg 来合并或封装该媒体，请重新选择有效的 ffmpeg.exe。", tail);
        }

        if (ContainsAny(lower, "deleted", "unavailable", "not found", "http error 404", "does not exist", "suspended"))
        {
            return new XDownloadException(XDownloadErrorKind.Unavailable, "帖子不存在、已删除或当前不可用。", tail);
        }

        if (ContainsAny(lower, "unable to download", "timed out", "timeout", "temporary failure", "name resolution", "certificate", "connection", "network is unreachable", "http error 5"))
        {
            return new XDownloadException(XDownloadErrorKind.Network, "访问 X 或媒体 CDN 失败，请检查网络后重试。", tail);
        }

        return new XDownloadException(
            duringDownload ? XDownloadErrorKind.DownloadFailed : XDownloadErrorKind.ParseFailed,
            duringDownload ? "X 媒体下载失败。" : "X 链接解析失败。",
            tail);
    }

    private static IReadOnlyList<string> BuildDownloadArguments(
        XParseResult parseResult,
        XMediaItem item,
        string outputTemplate,
        string ffmpegPath)
        =>
        [
            "--ignore-config",
            "--yes-playlist",
            "--playlist-items", item.PlaylistIndex.ToString(CultureInfo.InvariantCulture),
            "--format", item.SelectedQuality.FormatSelector,
            "--output", outputTemplate,
            "--no-simulate",
            "--newline",
            "--no-color",
            "--progress-delta", "0.2",
            "--socket-timeout", "30",
            "--retries", "3",
            "--fragment-retries", "3",
            "--no-continue",
            "--abort-on-error",
            "--no-overwrites",
            "--windows-filenames",
            "--restrict-filenames",
            "--trim-filenames", "160",
            "--merge-output-format", "mp4",
            "--remux-video", "mp4",
            "--ffmpeg-location", ffmpegPath,
            "--progress-template", ProgressTemplate,
            "--print", AfterMoveTemplate,
            "--extractor-args", "twitter:api=graphql",
            "--",
            parseResult.Source.CanonicalUri.AbsoluteUri
        ];

    private static string PrepareOutputDirectory(string outputDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            {
                throw new XDownloadException(XDownloadErrorKind.OutputDirectory, "请选择有效的绝对保存路径。");
            }

            var fullPath = Path.GetFullPath(outputDirectory);
            if (File.Exists(fullPath))
            {
                throw new XDownloadException(XDownloadErrorKind.OutputDirectory, "保存路径必须是文件夹，不能是文件。");
            }

            Directory.CreateDirectory(fullPath);
            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (XDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException or IOException)
        {
            throw new XDownloadException(XDownloadErrorKind.OutputDirectory, "无法使用指定的保存目录。", exception.Message, exception);
        }
    }

    private static string ValidateFfmpegPath(string ffmpegPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !Path.IsPathFullyQualified(ffmpegPath))
            {
                throw new XDownloadException(XDownloadErrorKind.FfmpegMissing, "请先选择有效的 ffmpeg.exe。");
            }

            var fullPath = Path.GetFullPath(ffmpegPath);
            if (!File.Exists(fullPath))
            {
                throw new XDownloadException(XDownloadErrorKind.FfmpegMissing, "找不到所选的 ffmpeg.exe，请重新选择。");
            }

            return fullPath;
        }
        catch (XDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new XDownloadException(XDownloadErrorKind.FfmpegMissing, "FFmpeg 路径无效，请重新选择。", exception.Message, exception);
        }
    }

    private static string ResolveDownloadedPath(string? reportedPath, string itemDirectory)
    {
        string? path = null;
        if (!string.IsNullOrWhiteSpace(reportedPath))
        {
            try
            {
                var candidate = Path.GetFullPath(reportedPath);
                if (IsContainedPath(candidate, itemDirectory) && File.Exists(candidate))
                {
                    path = candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // yt-dlp stdout can use a different Windows code page for paths containing CJK text.
                // In that case, resolve the output only by enumerating the isolated item directory below.
            }
        }

        if (path is null)
        {
            var candidates = Directory.EnumerateFiles(itemDirectory, "*.mp4", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (candidates.Count != 1)
            {
                throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "下载完成后无法确定输出文件。");
            }

            path = Path.GetFullPath(candidates[0]);
        }

        EnsureContainedPath(path, itemDirectory);
        if (!File.Exists(path))
        {
            throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "下载组件未生成有效的 MP4 文件。");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
        {
            throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "拒绝移动无效的下载输出。");
        }

        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || new FileInfo(path).Length <= 0)
        {
            throw new XDownloadException(XDownloadErrorKind.DownloadFailed, "下载组件生成的 MP4 文件无效。");
        }

        return path;
    }

    private static string MoveWithoutOverwrite(string sourcePath, string destination, string baseName)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = XFileNameHelper.GetUniquePath(destination, baseName);
            try
            {
                File.Move(sourcePath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Another process won the filename race; calculate a new suffix.
            }
        }

        throw new XDownloadException(XDownloadErrorKind.OutputDirectory, "无法为下载文件分配不重复的文件名。");
    }

    private static void EnsureContainedPath(string childPath, string parentDirectory)
    {
        var child = Path.GetFullPath(childPath);
        var parent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!IsContainedPath(child, parentDirectory))
        {
            throw new XDownloadException(
                XDownloadErrorKind.DownloadFailed,
                "下载组件返回了临时目录之外的路径，已拒绝使用。",
                $"子路径：{child}{Environment.NewLine}允许目录：{parent}");
        }
    }

    private static bool IsContainedPath(string childPath, string parentDirectory)
    {
        var child = Path.GetFullPath(childPath);
        var parent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteTemporaryDirectory(string temporaryRoot, string destination)
    {
        try
        {
            if (!Directory.Exists(temporaryRoot))
            {
                return;
            }

            EnsureContainedPath(temporaryRoot, destination);
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch
        {
            // Cancellation/failure cleanup is best effort; the directory is isolated and hidden.
        }
    }

    private static void TryMarkHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch
        {
            // Dot-prefixed directory remains unobtrusive when the hidden attribute is unavailable.
        }
    }

    private static long? ParseByteValue(string value)
    {
        var number = ParseDouble(value);
        return number is >= 0 and <= long.MaxValue ? (long)number.Value : null;
    }

    private static double? ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number)
                ? number
                : null;

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
