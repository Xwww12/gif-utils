using System.Globalization;

namespace FFmpegUtils.Models;

public static class VideoTimeRange
{
    public static bool TryParse(string text, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Split(':');
        bool Number(string value, out double number) =>
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
             || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) && double.IsFinite(number);
        if (parts.Length == 1)
        {
            if (!Number(parts[0], out var number)) return false;
            seconds = number;
            return true;
        }
        if (parts.Length is not (2 or 3) || !Number(parts[^1], out var last) || last is < 0 or >= 60) return false;
        double total = last;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < 0 || (parts.Length == 3 && i == 1 && value >= 60)) return false;
            total += value * (i == parts.Length - 2 ? 60d : 3600d);
        }
        seconds = total;
        return true;
    }

    public static bool TryGet(string startText, string endText, double duration, out double start, out double end, out string error)
    {
        start = 0;
        end = duration;
        error = "";
        if (!TryParse(startText, out var parsedStart) || !TryParse(endText, out var parsedEnd))
            error = "时间请输入秒数、分:秒或时:分:秒，可带小数。";
        else
        {
            start = parsedStart ?? 0;
            end = parsedEnd ?? duration;
            if (!double.IsFinite(duration) || duration <= 0) error = "请先选择具有有效时长的 MP4。";
            else if (start < 0 || end < 0) error = "截取时间不能小于零。";
            else if (end <= start) error = "结束时间必须大于开始时间。";
            else if (start >= duration || end > duration + 0.000001) error = "截取时间不能超过视频时长。";
        }
        return error.Length == 0;
    }

    public static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return seconds >= 3600
            ? $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    // Input-side accurate seek/limit applies to the video only, before palette generation or playback filters.
    public static List<string> InputArguments(string path, double? startSeconds, double? endSeconds)
    {
        var start = startSeconds ?? 0;
        if (!double.IsFinite(start) || start < 0 || (endSeconds.HasValue && (!double.IsFinite(endSeconds.Value) || endSeconds <= start)))
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        var arguments = new List<string>();
        if (start > 0) arguments.AddRange(["-ss", start.ToString("0.######", CultureInfo.InvariantCulture)]);
        if (endSeconds.HasValue) arguments.AddRange(["-t", (endSeconds.Value - start).ToString("0.######", CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", path]);
        return arguments;
    }
}
