using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

/// <summary>Explicit, user-initiated reverse lookup. Receives only coordinates, never a file or metadata.</summary>
public sealed class ImageGeocodingService
{
    public static ImageGeocodingService Shared { get; } = new(CreateClient());
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ImageCoordinates, (ImageAddress Result, DateTimeOffset Time)> _cache = new();
    private DateTimeOffset _lastRequest;
    private DateTimeOffset _retryAfter;

    public ImageGeocodingService(HttpClient client) => _client = client;

    private static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 256 * 1024
    };

    public async Task<ImageAddress> ResolveAsync(ImageCoordinates coordinates, CancellationToken cancellationToken)
    {
        if (!coordinates.IsValid || !coordinates.IsWgs84)
            throw new ArgumentException("需要有效的 WGS 84 经纬度。", nameof(coordinates));

        // One request at a time, at least one second apart. Keep only a small, memory-only cache.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _cache.Where(item => now - item.Value.Time > TimeSpan.FromMinutes(30)).Select(item => item.Key).ToArray())
                _cache.Remove(key);
            if (_cache.TryGetValue(coordinates, out var cached)) return cached.Result;
            if (now < _retryAfter) throw new ImageGeocodingException("地图服务请求过于频繁，请稍后重试。");
            var delay = TimeSpan.FromSeconds(1) - (now - _lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // No language override: Photon falls back to local place names (Chinese within China).
            var query = FormattableString.Invariant($"https://photon.komoot.io/reverse?lat={coordinates.Latitude:R}&lon={coordinates.Longitude:R}&limit=1&radius=1");
            using var request = new HttpRequestMessage(HttpMethod.Get, query);
            request.Headers.UserAgent.ParseAdd("FFmpegUtils/1.0 (user-initiated-photo-location)");
            request.Headers.Accept.ParseAdd("application/json");
            _lastRequest = DateTimeOffset.UtcNow;
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _retryAfter = response.Headers.RetryAfter?.Date
                    ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                throw new ImageGeocodingException("地图服务请求过于频繁，请稍后重试。");
            }
            if (!response.IsSuccessStatusCode)
                throw new ImageGeocodingException("地图服务暂不可用，请稍后重试；原始经纬度仍可查看。");
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = ParseResponse(json, coordinates);
            if (_cache.Count >= 64) _cache.Remove(_cache.MinBy(item => item.Value.Time).Key);
            _cache[coordinates] = (result, DateTimeOffset.UtcNow);
            return result;
        }
        finally { _gate.Release(); }
    }

    public static ImageAddress ParseResponse(string json, ImageCoordinates coordinates)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing features.");
        if (features.GetArrayLength() == 0)
            throw new ImageGeocodingException("周边 1 公里内未查到地址，可能是地图数据缺失或位于偏远区域。");
        var feature = features[0];
        if (feature.ValueKind != JsonValueKind.Object
            || !feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing properties.");
        string Value(string key) => properties.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? new string((value.GetString() ?? "").Where(character => !char.IsControl(character)).Take(1000).ToArray()).Trim() : "";

        var city = Value("city");
        var type = Value("type");
        if (city.Length == 0 && type == "city") city = Value("name");
        var region = Join(Value("country"), Value("state"), city, Value("county"), Value("district"));
        if (city.Length == 0) region = "城市未提供" + (region.Length > 0 ? " · " + region : "");
        var name = type is "country" or "state" or "county" or "city" or "district" ? "" : Value("name");
        var nearby = Join(Value("locality"), Value("street"), Value("housenumber"), name);
        if (nearby.Length == 0) nearby = "未提供街道或地标";

        var detail = "地图邻近匹配，不代表准确拍摄地点；精度取决于 GPS 和地图覆盖。";
        if (feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
            && geometry.TryGetProperty("coordinates", out var point) && point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2
            && point[0].ValueKind == JsonValueKind.Number && point[1].ValueKind == JsonValueKind.Number
            && point[0].TryGetDouble(out var longitude) && point[1].TryGetDouble(out var latitude)
            && new ImageCoordinates(latitude, longitude).IsValid)
        {
            var distance = DistanceMetres(coordinates.Latitude, coordinates.Longitude, latitude, longitude);
            detail = $"距地图匹配点约 {distance.ToString("0", CultureInfo.InvariantCulture)} 米（不是定位误差）。\n" + detail;
        }
        return new ImageAddress(region, nearby, detail);
    }

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(part => part.Length > 0).Distinct(StringComparer.Ordinal));

    private static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2)
            + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians) * Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6_371_008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}

public sealed class ImageGeocodingException(string message) : Exception(message);
