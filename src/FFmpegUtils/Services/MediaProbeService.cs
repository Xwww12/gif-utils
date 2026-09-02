using System.Globalization;
using System.Text.Json;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed class MediaProbeService
{
    public async Task<MediaInfo> ProbeAsync(string ffprobePath, string mediaPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("找不到媒体文件。", mediaPath);
        }

        string[] arguments =
        [
            "-v", "error",
            "-show_streams",
            "-show_format",
            "-of", "json",
            mediaPath
        ];

        var result = await ProcessCapture.RunAsync(ffprobePath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("无法读取媒体信息。" + Environment.NewLine + result.StandardError.Trim());
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamsElement)
            ? streamsElement.EnumerateArray().ToArray()
            : [];

        var video = streams.FirstOrDefault(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");

        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("所选文件不包含视频轨道。" );
        }

        var width = GetInt(video, "width");
        var height = GetInt(video, "height");
        var frameRate = ParseRate(GetString(video, "avg_frame_rate") ?? GetString(video, "r_frame_rate"));
        var streamDuration = ParseDouble(GetString(video, "duration"));
        var hasAudio = streams.Any(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");

        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var formatDuration = format.ValueKind == JsonValueKind.Object ? ParseDouble(GetString(format, "duration")) : 0;
        var formatSize = format.ValueKind == JsonValueKind.Object ? ParseLong(GetString(format, "size")) : 0;

        var duration = formatDuration > 0 ? formatDuration : streamDuration;
        var fileSize = formatSize > 0 ? formatSize : new FileInfo(mediaPath).Length;
        return new MediaInfo(mediaPath, width, height, frameRate, duration, fileSize, hasAudio);
    }

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    private static double ParseRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return ParseDouble(value);
    }

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
