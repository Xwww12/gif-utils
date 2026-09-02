using FFmpegUtils.Infrastructure;

namespace FFmpegUtils.Models;

public enum XDownloadErrorKind
{
    InvalidUrl,
    NoVideo,
    Unavailable,
    AuthenticationRequired,
    AgeRestricted,
    GeoRestricted,
    RateLimited,
    Network,
    ToolUnavailable,
    ToolIntegrity,
    FfmpegMissing,
    OutputDirectory,
    ParseFailed,
    DownloadFailed
}

public sealed class XDownloadException : Exception
{
    public XDownloadException(XDownloadErrorKind kind, string message, string? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Details = details ?? string.Empty;
    }

    public XDownloadErrorKind Kind { get; }

    public string Details { get; }
}

public sealed record XPostUrl(
    string OriginalInput,
    Uri CanonicalUri,
    string PostId,
    string AccountName);

public sealed record XDownloadProgress(
    double Percent,
    string Stage,
    string Detail = "",
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null);

public sealed record XQualityOption(
    string DisplayName,
    string FormatSelector,
    int? Width,
    int? Height,
    double? BitrateKbps,
    bool IsHls,
    bool HasAudio,
    string FormatId);

public sealed class XMediaItem : ObservableObject
{
    private bool _isSelected = true;
    private XQualityOption _selectedQuality;

    public XMediaItem(
        int index,
        int playlistIndex,
        string id,
        string displayName,
        string title,
        string mediaTypeLabel,
        double? durationSeconds,
        IReadOnlyList<XQualityOption> qualityOptions)
    {
        if (qualityOptions.Count == 0)
        {
            throw new ArgumentException("媒体项必须至少有一个可下载画质。", nameof(qualityOptions));
        }

        Index = index;
        PlaylistIndex = playlistIndex;
        Id = id;
        DisplayName = displayName;
        Title = title;
        MediaTypeLabel = mediaTypeLabel;
        DurationSeconds = durationSeconds;
        QualityOptions = qualityOptions.ToArray();
        _selectedQuality = QualityOptions[0];
    }

    public int Index { get; }

    public int PlaylistIndex { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public string Title { get; }

    public string MediaTypeLabel { get; }

    public double? DurationSeconds { get; }

    public IReadOnlyList<XQualityOption> QualityOptions { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public XQualityOption SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (value is null || !QualityOptions.Contains(value))
            {
                return;
            }

            if (SetProperty(ref _selectedQuality, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string> { MediaTypeLabel };
            if (SelectedQuality.Width is > 0 && SelectedQuality.Height is > 0)
            {
                parts.Add($"{SelectedQuality.Width}×{SelectedQuality.Height}");
            }

            if (DurationSeconds is > 0)
            {
                parts.Add(DurationSeconds < 60
                    ? $"{DurationSeconds:0.#} 秒"
                    : TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"m\:ss"));
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record XParseResult(
    XPostUrl Source,
    string Title,
    string UploaderId,
    IReadOnlyList<XMediaItem> Items,
    bool IsPlaylist,
    string? Warning = null)
{
    public string SuggestedPrefix => XFileNameHelper.BuildDefaultPrefix(UploaderId, Source.PostId);
}

public sealed record XDownloadResult(
    IReadOnlyList<string> OutputPaths,
    long TotalBytes,
    string? Warning = null);

public static class XFileNameHelper
{
    private const int MaxBaseNameLength = 120;

    public static string BuildDefaultPrefix(string? uploaderId, string postId)
    {
        var account = string.IsNullOrWhiteSpace(uploaderId) ? "post" : uploaderId;
        return SanitizeBaseName($"X_{account}_{postId}");
    }

    public static string SanitizeBaseName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "X_video" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = source.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }

        if (result.Length > MaxBaseNameLength)
        {
            result = result[..MaxBaseNameLength].TrimEnd('.', ' ');
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            result = "X_video";
        }

        var reservedName = result.Split('.')[0];
        if (IsReservedWindowsName(reservedName))
        {
            result = $"_{result}";
        }

        return result;
    }

    public static string GetUniquePath(string directory, string baseName, string extension = ".mp4")
    {
        var safeBaseName = SanitizeBaseName(baseName);
        var candidate = Path.Combine(directory, safeBaseName + extension);
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{safeBaseName} ({suffix}){extension}");
        }

        return candidate;
    }

    private static bool IsReservedWindowsName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && name[3] is >= '1' and <= '9';
    }
}
