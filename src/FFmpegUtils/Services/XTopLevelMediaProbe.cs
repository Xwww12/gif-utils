using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FFmpegUtils.Services;

public enum XTopLevelMediaKind
{
    Video,
    AnimatedGif
}

public sealed record XTopLevelMediaProbeResult(
    bool IsVerified,
    IReadOnlyDictionary<string, XTopLevelMediaKind> MediaById,
    string? Warning = null);

public interface IXTopLevelMediaProbe
{
    Task<XTopLevelMediaProbeResult> ProbeAsync(string postId, CancellationToken cancellationToken = default);
}

public sealed class XTopLevelMediaProbe : IXTopLevelMediaProbe
{
    public const string BoundaryWarning = "无法通过 X 官方数据核验引用帖边界；解析组件可能同时列出引用帖中的 X 原生媒体，请下载前核对。";

    private const string BearerToken = "AAAAAAAAAAAAAAAAAAAAANRILgAAAAAAnNwIzUejRCOuH5E6I8xnZz4puTs%3D1Zv7ttfk8LF81IUq16cHjhLTvJu4FA33AGWWjCpTnA";
    private static readonly Uri GuestActivationUri = new("https://api.x.com/1.1/guest/activate.json");
    private static readonly Uri GraphQlBaseUri = new("https://x.com/i/api/graphql/2ICDjqPd81tulZcYrtpTuQ/TweetResultByRestId");
    private static readonly HttpClient DefaultHttpClient = CreateDefaultHttpClient();
    private readonly HttpClient _httpClient;

    public XTopLevelMediaProbe(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public async Task<XTopLevelMediaProbeResult> ProbeAsync(string postId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(postId) || postId.Any(character => !char.IsAsciiDigit(character)))
        {
            return Unverified();
        }

        try
        {
            var guestToken = await ActivateGuestAsync(cancellationToken);
            var requestUri = BuildGraphQlUri(postId);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            AddAuthorization(request.Headers);
            request.Headers.TryAddWithoutValidation("x-guest-token", guestToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Unverified();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseResponse(document.RootElement, postId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return Unverified();
        }
    }

    public static XTopLevelMediaProbeResult ParseResponse(JsonElement root, string postId)
    {
        if (!TryGetPropertyPath(root, out var result, "data", "tweetResult", "result")
            || result.ValueKind != JsonValueKind.Object)
        {
            return Unverified();
        }

        var typeName = GetString(result, "__typename");
        if (typeName.Equals("TweetWithVisibilityResults", StringComparison.Ordinal))
        {
            if (!result.TryGetProperty("tweet", out result) || result.ValueKind != JsonValueKind.Object)
            {
                return Unverified();
            }
        }

        if (typeName is "TweetUnavailable" or "TweetTombstone" || result.TryGetProperty("tombstone", out _))
        {
            return new XTopLevelMediaProbeResult(true, new Dictionary<string, XTopLevelMediaKind>());
        }

        if (!string.Equals(GetString(result, "rest_id"), postId, StringComparison.Ordinal))
        {
            return Unverified();
        }

        var media = new Dictionary<string, XTopLevelMediaKind>(StringComparer.Ordinal);
        if (TryGetPropertyPath(result, out var mediaArray, "legacy", "extended_entities", "media")
            && mediaArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in mediaArray.EnumerateArray())
            {
                AddMediaItem(media, item);
            }
        }

        AddCardMedia(media, result, postId);
        return new XTopLevelMediaProbeResult(true, media);
    }

    private async Task<string> ActivateGuestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GuestActivationUri)
        {
            Content = new ByteArrayContent([])
        };
        AddAuthorization(request.Headers);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var token = GetString(document.RootElement, "guest_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("X guest token response did not contain a token.");
        }

        return token;
    }

    private static Uri BuildGraphQlUri(string postId)
    {
        var variables = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tweetId"] = postId,
            ["withCommunity"] = false,
            ["includePromotedContent"] = false,
            ["withVoice"] = false
        });
        var features = JsonSerializer.Serialize(new Dictionary<string, bool>
        {
            ["creator_subscriptions_tweet_preview_api_enabled"] = true,
            ["tweetypie_unmention_optimization_enabled"] = true,
            ["responsive_web_edit_tweet_api_enabled"] = true,
            ["graphql_is_translatable_rweb_tweet_is_translatable_enabled"] = true,
            ["view_counts_everywhere_api_enabled"] = true,
            ["longform_notetweets_consumption_enabled"] = true,
            ["responsive_web_twitter_article_tweet_consumption_enabled"] = false,
            ["tweet_awards_web_tipping_enabled"] = false,
            ["freedom_of_speech_not_reach_fetch_enabled"] = true,
            ["standardized_nudges_misinfo"] = true,
            ["tweet_with_visibility_results_prefer_gql_limited_actions_policy_enabled"] = true,
            ["longform_notetweets_rich_text_read_enabled"] = true,
            ["longform_notetweets_inline_media_enabled"] = true,
            ["responsive_web_graphql_exclude_directive_enabled"] = true,
            ["verified_phone_label_enabled"] = false,
            ["responsive_web_media_download_video_enabled"] = false,
            ["responsive_web_graphql_skip_user_profile_image_extensions_enabled"] = false,
            ["responsive_web_graphql_timeline_navigation_enabled"] = true,
            ["responsive_web_enhance_cards_enabled"] = false
        });
        var fieldToggles = JsonSerializer.Serialize(new Dictionary<string, bool>
        {
            ["withArticleRichContentState"] = false
        });

        return new Uri($"{GraphQlBaseUri}?variables={Uri.EscapeDataString(variables)}&features={Uri.EscapeDataString(features)}&fieldToggles={Uri.EscapeDataString(fieldToggles)}");
    }

    private static void AddAuthorization(HttpRequestHeaders headers)
    {
        headers.TryAddWithoutValidation("Authorization", $"Bearer {BearerToken}");
        headers.TryAddWithoutValidation("x-twitter-active-user", "yes");
        headers.TryAddWithoutValidation("x-twitter-client-language", "en");
    }

    private static void AddMediaItem(IDictionary<string, XTopLevelMediaKind> media, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = GetString(item, "type");
        var kind = type switch
        {
            "video" => XTopLevelMediaKind.Video,
            "animated_gif" => XTopLevelMediaKind.AnimatedGif,
            _ => (XTopLevelMediaKind?)null
        };
        var id = GetId(item);
        if (kind is not null && !string.IsNullOrWhiteSpace(id))
        {
            media[id] = kind.Value;
        }
    }

    private static void AddCardMedia(IDictionary<string, XTopLevelMediaKind> media, JsonElement result, string postId)
    {
        if (!TryGetPropertyPath(result, out var bindings, "card", "legacy", "binding_values")
            || bindings.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var foundNativeCardMedia = false;
        foreach (var binding in bindings.EnumerateArray())
        {
            var key = GetString(binding, "key");
            if (!binding.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var stringValue = GetString(value, "string_value");
            if (key.Equals("unified_card", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(stringValue))
            {
                try
                {
                    using var unified = JsonDocument.Parse(stringValue);
                    if (unified.RootElement.TryGetProperty("media_entities", out var entities)
                        && entities.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entity in entities.EnumerateObject())
                        {
                            var before = media.Count;
                            AddMediaItem(media, entity.Value);
                            foundNativeCardMedia |= media.Count > before;
                        }
                    }
                }
                catch (JsonException)
                {
                    // A malformed optional card is ignored; ordinary tweet media remains authoritative.
                }
            }
            else if (key.EndsWith("_content_id", StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(stringValue))
            {
                media[stringValue] = XTopLevelMediaKind.Video;
                foundNativeCardMedia = true;
            }
        }

        // Some classic native video cards keep the tweet id on the resolved yt-dlp entry.
        if (foundNativeCardMedia)
        {
            media.TryAdd(postId, XTopLevelMediaKind.Video);
        }
    }

    private static string GetId(JsonElement element)
    {
        var id = GetString(element, "id_str");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        if (!element.TryGetProperty("id", out var idElement))
        {
            return string.Empty;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => idElement.GetRawText(),
            _ => string.Empty
        };
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static bool TryGetPropertyPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var propertyName in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static XTopLevelMediaProbeResult Unverified()
        => new(false, new Dictionary<string, XTopLevelMediaKind>(), BoundaryWarning);

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        return client;
    }
}
