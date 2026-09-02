using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public static partial class XMediaJsonParser
{
    public static XParseResult Parse(
        string json,
        XPostUrl source,
        XTopLevelMediaProbeResult? topLevelProbe = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, source, topLevelProbe);
        }
        catch (XDownloadException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new XDownloadException(
                XDownloadErrorKind.ParseFailed,
                "X 解析结果不完整，请稍后重试。",
                exception.Message,
                exception);
        }
    }

    public static XParseResult Parse(
        JsonElement root,
        XPostUrl source,
        XTopLevelMediaProbeResult? topLevelProbe = null)
    {
        var probe = topLevelProbe ?? new XTopLevelMediaProbeResult(
            false,
            new Dictionary<string, XTopLevelMediaKind>(),
            XTopLevelMediaProbe.BoundaryWarning);

        if (probe.IsVerified && probe.MediaById.Count == 0)
        {
            throw NoVideo("该帖子本身没有可下载的视频。");
        }

        EnsureTwitterExtractor(root);
        EnsureNotLive(root);
        EnsurePublic(root);

        var isPlaylist = GetString(root, "_type").Equals("playlist", StringComparison.OrdinalIgnoreCase);
        if (isPlaylist)
        {
            if (!string.Equals(GetString(root, "id"), source.PostId, StringComparison.Ordinal))
            {
                throw new XDownloadException(XDownloadErrorKind.ParseFailed, "X 解析结果与输入帖子 ID 不一致。");
            }
        }
        else if (!string.Equals(GetString(root, "display_id"), source.PostId, StringComparison.Ordinal))
        {
            throw new XDownloadException(XDownloadErrorKind.ParseFailed, "X 解析结果与输入帖子 ID 不一致。");
        }

        var candidates = new List<(JsonElement Element, int PlaylistIndex)>();
        if (isPlaylist)
        {
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                var arrayIndex = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    arrayIndex++;
                    if (entry.ValueKind != JsonValueKind.Object || !IsTwitterExtractor(entry))
                    {
                        // External/expanded_url card results are deliberately excluded.
                        continue;
                    }

                    if (!string.Equals(GetString(entry, "display_id"), source.PostId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    EnsureNotLive(entry);
                    EnsurePublic(entry);
                    candidates.Add((entry, GetInt32(entry, "playlist_index") ?? arrayIndex));
                }
            }
        }
        else
        {
            candidates.Add((root, 1));
        }

        var items = new List<XMediaItem>();
        foreach (var (entry, playlistIndex) in candidates)
        {
            var entryId = GetString(entry, "id");
            if (string.IsNullOrWhiteSpace(entryId))
            {
                continue;
            }

            XTopLevelMediaKind? verifiedKind = null;
            if (probe.IsVerified)
            {
                var matched = GetCandidateMediaIds(entry)
                    .Select(id => probe.MediaById.TryGetValue(id, out var kind) ? (Found: true, Kind: kind) : default)
                    .FirstOrDefault(value => value.Found);
                if (!matched.Found)
                {
                    continue;
                }

                verifiedKind = matched.Kind;
            }

            var qualities = BuildQualityOptions(entry);
            if (qualities.Count == 0)
            {
                continue;
            }

            var hasKnownAudio = qualities.Any(option => option.HasAudio);
            var mediaType = verifiedKind switch
            {
                XTopLevelMediaKind.AnimatedGif => "动图（MP4）",
                XTopLevelMediaKind.Video when !hasKnownAudio => "无音频 MP4",
                XTopLevelMediaKind.Video => "视频",
                _ when !hasKnownAudio => "无音频 MP4",
                _ => "视频/动图（MP4）"
            };
            var title = GetString(entry, "title");
            var displayIndex = items.Count + 1;
            items.Add(new XMediaItem(
                displayIndex,
                playlistIndex,
                entryId,
                $"媒体 {displayIndex}",
                title,
                mediaType,
                GetDouble(entry, "duration"),
                qualities));
        }

        if (items.Count == 0)
        {
            throw NoVideo(probe.IsVerified
                ? "该帖子本身没有可下载的视频。"
                : "该帖子没有可下载的 X 原生视频。");
        }

        var titleResult = GetString(root, "title");
        var uploaderId = GetString(root, "uploader_id");
        if (string.IsNullOrWhiteSpace(uploaderId))
        {
            uploaderId = GetString(candidates[0].Element, "uploader_id");
        }

        return new XParseResult(
            source,
            titleResult,
            uploaderId,
            items.ToArray(),
            isPlaylist,
            probe.IsVerified ? null : probe.Warning ?? XTopLevelMediaProbe.BoundaryWarning);
    }

    private static IReadOnlyList<XQualityOption> BuildQualityOptions(JsonElement entry)
    {
        if (!entry.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<FormatCandidate>();
        var hasSeparateAudio = formats.EnumerateArray().Any(format =>
        {
            var videoCodec = GetString(format, "vcodec");
            var audioCodec = GetString(format, "acodec");
            var audioExtension = GetString(format, "audio_ext");
            return videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !audioExtension.Equals("none", StringComparison.OrdinalIgnoreCase);
        });
        foreach (var format in formats.EnumerateArray())
        {
            if (format.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var formatId = GetString(format, "format_id");
            var url = GetString(format, "url");
            var protocol = GetString(format, "protocol");
            var extension = GetString(format, "ext");
            var videoCodec = GetString(format, "vcodec");
            if (string.IsNullOrWhiteSpace(formatId)
                || !SafeFormatIdRegex().IsMatch(formatId)
                || string.IsNullOrWhiteSpace(url)
                || videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isHls = protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            var isMp4 = extension.Equals("mp4", StringComparison.OrdinalIgnoreCase)
                || isHls
                || Uri.TryCreate(url, UriKind.Absolute, out var formatUri)
                    && formatUri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            if (!isMp4)
            {
                continue;
            }

            var audioCodec = GetString(format, "acodec");
            var audioExtension = GetString(format, "audio_ext");
            var containsAudio = !audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !audioExtension.Equals("none", StringComparison.OrdinalIgnoreCase);
            parsed.Add(new FormatCandidate(
                formatId,
                GetInt32(format, "width"),
                GetInt32(format, "height"),
                GetDouble(format, "tbr") ?? GetDouble(format, "vbr"),
                isHls,
                containsAudio || hasSeparateAudio,
                !containsAudio));
        }

        var direct = parsed.Where(format => !format.IsHls).ToList();
        var selectedProtocol = direct.Count > 0 ? direct : parsed.Where(format => format.IsHls).ToList();
        var ordered = selectedProtocol
            .OrderByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.Width ?? 0)
            .ThenByDescending(format => format.BitrateKbps ?? 0)
            .GroupBy(format => (format.Width, format.Height))
            .Select(group => group.First())
            .ToList();

        var options = new List<XQualityOption>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var format = ordered[index];
            var dimensions = format.Width is > 0 && format.Height is > 0
                ? $"{format.Width}×{format.Height}"
                : "未知分辨率";
            var bitrate = format.BitrateKbps is > 0 ? $" · {format.BitrateKbps:0} kbps" : string.Empty;
            var transport = format.IsHls ? "HLS" : "MP4 直链";
            var highest = index == 0 ? "（最高）" : string.Empty;
            options.Add(new XQualityOption(
                $"{dimensions}{bitrate} · {transport}{highest}",
                format.NeedsAudioMerge ? $"{format.FormatId}+bestaudio/best" : format.FormatId,
                format.Width,
                format.Height,
                format.BitrateKbps,
                format.IsHls,
                format.HasAudio,
                format.FormatId));
        }

        return options;
    }

    private static IEnumerable<string> GetCandidateMediaIds(JsonElement entry)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entryId = GetString(entry, "id");
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            ids.Add(entryId);
        }

        if (entry.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                var url = GetString(format, "url");
                var match = MediaIdInUrlRegex().Match(url);
                if (match.Success)
                {
                    ids.Add(match.Groups["id"].Value);
                }
            }
        }

        return ids;
    }

    private static void EnsureTwitterExtractor(JsonElement element)
    {
        if (!IsTwitterExtractor(element))
        {
            throw NoVideo("该链接没有返回目标帖子自身的 X 原生视频。");
        }
    }

    private static bool IsTwitterExtractor(JsonElement element)
        => GetString(element, "extractor_key").Equals("Twitter", StringComparison.Ordinal)
            && GetString(element, "extractor").Equals("twitter", StringComparison.OrdinalIgnoreCase);

    private static void EnsureNotLive(JsonElement element)
    {
        if (GetBoolean(element, "is_live") == true)
        {
            throw new XDownloadException(XDownloadErrorKind.Unavailable, "当前版本不支持 X 直播或 Spaces。");
        }

        var liveStatus = GetString(element, "live_status");
        if (!string.IsNullOrWhiteSpace(liveStatus)
            && !liveStatus.Equals("not_live", StringComparison.OrdinalIgnoreCase))
        {
            throw new XDownloadException(XDownloadErrorKind.Unavailable, "当前版本不支持 X 直播或 Spaces。");
        }
    }

    private static void EnsurePublic(JsonElement element)
    {
        var availability = GetString(element, "availability");
        if (availability is "private" or "premium_only" or "subscriber_only" or "needs_auth")
        {
            throw new XDownloadException(
                XDownloadErrorKind.AuthenticationRequired,
                "该帖子不能以公开方式访问，可能是私密、付费或需要登录的内容。");
        }
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static bool? GetBoolean(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static XDownloadException NoVideo(string message)
        => new(XDownloadErrorKind.NoVideo, message);

    private sealed record FormatCandidate(
        string FormatId,
        int? Width,
        int? Height,
        double? BitrateKbps,
        bool IsHls,
        bool HasAudio,
        bool NeedsAudioMerge);

    [GeneratedRegex(@"^[A-Za-z0-9._+\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFormatIdRegex();

    [GeneratedRegex(@"/(?:ext_tw_video|amplify_video|tweet_video)/(?<id>[0-9]+)(?:/|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MediaIdInUrlRegex();
}
