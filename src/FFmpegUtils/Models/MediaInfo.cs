namespace FFmpegUtils.Models;

public sealed record MediaInfo(
    string Path,
    int Width,
    int Height,
    double FrameRate,
    double DurationSeconds,
    long FileSizeBytes,
    bool HasAudio)
{
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "未知";
    public string DurationText => TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss\.f");
    public string FrameRateText => FrameRate > 0 ? $"{FrameRate:0.##} FPS" : "未知";
    public string FileSizeText => FormatBytes(FileSizeBytes);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
