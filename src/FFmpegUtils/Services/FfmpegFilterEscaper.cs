namespace FFmpegUtils.Services;

public static class FfmpegFilterEscaper
{
    public static string EscapePath(string path)
    {
        return Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }
}
