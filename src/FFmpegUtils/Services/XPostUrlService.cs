using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public sealed partial class XPostUrlService
{
    private const int MaxRedirects = 5;
    private static readonly HttpClient DefaultHttpClient = CreateDefaultHttpClient();
    private readonly HttpClient _httpClient;

    public XPostUrlService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public async Task<XPostUrl> NormalizeAsync(string input, CancellationToken cancellationToken = default)
    {
        var originalInput = input?.Trim() ?? string.Empty;
        var uri = ParseSafeHttpUri(originalInput);

        if (IsShortLinkHost(uri.IdnHost))
        {
            uri = await ResolveShortLinkAsync(uri, cancellationToken);
        }

        return NormalizeOfficialStatusUrl(originalInput, uri);
    }

    public static bool TryNormalizeOfficialStatusUrl(string input, out XPostUrl? result, out string? error)
    {
        try
        {
            result = NormalizeOfficialStatusUrl(input, ParseSafeHttpUri(input?.Trim() ?? string.Empty));
            error = null;
            return true;
        }
        catch (XDownloadException exception) when (exception.Kind == XDownloadErrorKind.InvalidUrl)
        {
            result = null;
            error = exception.Message;
            return false;
        }
    }

    private async Task<Uri> ResolveShortLinkAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect < MaxRedirects; redirect++)
        {
            ValidateRedirectHop(current);
            using var response = await SendRedirectProbeAsync(current, cancellationToken);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                if (IsOfficialHost(current.IdnHost))
                {
                    return current;
                }

                throw InvalidUrl("t.co 链接未指向 X 帖子。");
            }

            current = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
        }

        ValidateRedirectHop(current);
        if (IsOfficialHost(current.IdnHost))
        {
            return current;
        }

        throw InvalidUrl("t.co 链接重定向次数过多。");
    }

    private async Task<HttpResponseMessage> SendRedirectProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            var response = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                return await _httpClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XDownloadException(XDownloadErrorKind.Network, "解析 t.co 链接超时，请稍后重试。");
        }
        catch (HttpRequestException exception)
        {
            throw new XDownloadException(XDownloadErrorKind.Network, "无法访问 t.co 短链接，请检查网络。", exception.Message, exception);
        }
    }

    private static XPostUrl NormalizeOfficialStatusUrl(string originalInput, Uri uri)
    {
        ValidateRedirectHop(uri);
        if (!IsOfficialHost(uri.IdnHost))
        {
            throw InvalidUrl("仅支持 x.com、twitter.com 或 t.co 的帖子链接。");
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.StartsWith("i/spaces/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("i/broadcasts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("i/events/", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidUrl("当前版本不支持 X Spaces、直播或 Broadcast。");
        }

        var match = StatusPathRegex().Match(path);
        if (!match.Success)
        {
            throw InvalidUrl("请输入包含 /status/帖子ID 的 X 帖子链接。");
        }

        var postId = match.Groups["id"].Value;
        var account = match.Groups["account"].Success ? match.Groups["account"].Value : "post";
        var canonical = new Uri($"https://x.com/{account}/status/{postId}");
        return new XPostUrl(originalInput, canonical, postId, account);
    }

    private static Uri ParseSafeHttpUri(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw InvalidUrl("请输入 X 帖子链接。");
        }

        if (input.Length > 2048)
        {
            throw InvalidUrl("链接过长，请重新复制 X 帖子地址。");
        }

        var candidate = input.Contains("://", StringComparison.Ordinal) ? input : $"https://{input}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            throw InvalidUrl("链接格式不正确。");
        }

        ValidateRedirectHop(uri);
        return uri;
    }

    private static void ValidateRedirectHop(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidUrl("链接只能使用 HTTP 或 HTTPS。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidUrl("链接不能包含用户名或密码。");
        }

        if (!uri.IsDefaultPort)
        {
            throw InvalidUrl("链接不能使用非默认端口。");
        }

        if (string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            throw InvalidUrl("链接缺少有效主机名。");
        }

        if (!IsShortLinkHost(uri.IdnHost) && !IsOfficialHost(uri.IdnHost))
        {
            throw InvalidUrl("t.co 重定向只允许进入 X 官方主机。");
        }
    }

    private static bool IsOfficialHost(string host)
        => host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.x.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.x.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("mobile.x.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.twitter.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("mobile.twitter.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsShortLinkHost(string host)
        => host.Equals("t.co", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.t.co", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect
            or HttpStatusCode.MultipleChoices;

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FFmpegUtils", "1.0"));
        return client;
    }

    private static XDownloadException InvalidUrl(string message)
        => new(XDownloadErrorKind.InvalidUrl, message);

    [GeneratedRegex(@"^(?:(?<account>[A-Za-z0-9_]{1,15})/status|i/web/status|statuses)/(?<id>[0-9]+)(?:/(?:video|photo)/[0-9]+)?/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StatusPathRegex();
}
