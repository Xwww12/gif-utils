using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FFmpegUtils.Models;
using FFmpegUtils.Services;
using FFmpegUtils.ViewModels;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;

internal static class ImageGeocodingChecks
{
    internal static ImageMetadataInfo PhotoSample()
    {
        var sample = ImageMetadataChecks.Sample();
        return sample with
        {
            Coordinates = new ImageCoordinates(39.916345, 116.397155),
            Location = [new("纬度", "北纬 39.916345°"), new("经度", "东经 116.397155°"),
                new("海拔", "未记录"), new("拍摄方向", "未记录")]
        };
    }

    // Synthetic fixture for UI/automated checks; no real photo location is sent over the network.
    internal static ImageAddress Sample() => new("中国 · 北京市 · 东城区", "智德社区 · 北池子二条",
        "距地图匹配点约 12 米（不是定位误差）。\n地图邻近匹配，不代表准确拍摄地点。");

    private const string Response = """
        {"features":[{"properties":{"type":"street","name":"北池子二条","street":"北池子二条",
        "locality":"智德社区","city":"北京市","state":"北京市","district":"东城区","country":"中国"},
        "geometry":{"type":"Point","coordinates":[116.3972744,39.9163028]}}]}
        """;

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var point = new ImageCoordinates(39.916345, 116.397155);
        var address = ImageGeocodingService.ParseResponse(Response, point);
        check(address.Region == "中国 · 北京市 · 东城区" && address.NearbyAddress == "智德社区 · 北池子二条"
            && address.Detail.Contains("不是定位误差"), "城市区县、社区街道去重，匹配距离不冒充 GPS 精度");
        var noCity = ImageGeocodingService.ParseResponse("""{"features":[{"properties":{"country":"中国","district":"示例区"}}]}""", point);
        check(noCity.Region.Contains("城市未提供") && noCity.NearbyAddress == "未提供街道或地标", "缺失城市或街道不进行猜测");
        var emptyFailed = false;
        try { ImageGeocodingService.ParseResponse("{\"features\":[]}", point); }
        catch (ImageGeocodingException exception) { emptyFailed = exception.Message.Contains("未查到"); }
        check(emptyFailed, "地图无覆盖有明确提示，不使用远处城市冒充拍摄地");
        var malformedFailed = false;
        try { ImageGeocodingService.ParseResponse("{\"features\":[null]}", point); }
        catch (JsonException) { malformedFailed = true; }
        check(malformedFailed, "地图异常响应安全失败");

        var metadata = ImageMetadataChecks.Sample();
        check(metadata.Coordinates is { Latitude: -33.5, Longitude: -120.25, IsValid: true, IsWgs84: true },
            "EXIF 南纬西经使用有符号原始坐标，而非解析界面字符串");
        var jpeg = new JpegDirectory();
        jpeg.Set(JpegDirectory.TagImageWidth, 10);
        jpeg.Set(JpegDirectory.TagImageHeight, 10);
        var gps = new GpsDirectory();
        gps.Set(GpsDirectory.TagLatitudeRef, "N");
        gps.Set(GpsDirectory.TagLongitudeRef, "E");
        gps.Set(GpsDirectory.TagLatitude, new[] { new Rational(33, 1), new Rational(30, 1), new Rational(1234567, 10000000) });
        gps.Set(GpsDirectory.TagLongitude, new[] { new Rational(120, 1), new Rational(15, 1), new Rational(0, 1) });
        var precise = ImageMetadataService.FromDirectories([jpeg, gps]);
        check(precise.Coordinates is { } raw && Math.Abs(raw.Latitude - (33.5 + 0.1234567 / 3600)) < 1e-12
            && raw.Latitude != Math.Round(raw.Latitude, 6), "地址解析保留 GPS 原始精度，不使用界面六位小数");
        gps.Set(GpsDirectory.TagMapDatum, "GCJ-02");
        check(ImageMetadataService.FromDirectories([jpeg, gps]).Coordinates is { IsWgs84: false }, "明确非 WGS 84 基准不会错当成标准 GPS");

        var requests = new List<(Uri Uri, bool NoBody, string Agent, long Time)>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add((request.RequestUri!, request.Content is null, request.Headers.UserAgent.ToString(), Stopwatch.GetTimestamp()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") });
        }));
        var service = new ImageGeocodingService(http);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await service.ResolveAsync(metadata.Coordinates!, CancellationToken.None);
            await service.ResolveAsync(metadata.Coordinates!, CancellationToken.None);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        check(requests.Count == 1 && requests[0].Uri.Host == "photon.komoot.io"
            && requests[0].Uri.Query.Contains("lat=-33.5&lon=-120.25&") && requests[0].NoBody
            && requests[0].Agent.StartsWith("FFmpegUtils/"), "单次查询只发送坐标，使用不受区域设置影响的小数和明确 UA，重复查询走内存缓存");
        await service.ResolveAsync(point, CancellationToken.None);
        check(Stopwatch.GetElapsedTime(requests[0].Time, requests[1].Time) >= TimeSpan.FromMilliseconds(950), "地图请求至少间隔一秒");
        foreach (var invalid in new[] { new ImageCoordinates(double.NaN, 1), new ImageCoordinates(91, 1), new ImageCoordinates(1, 181), new ImageCoordinates(1, 1, "GCJ02") })
        {
            try { await service.ResolveAsync(invalid, CancellationToken.None); }
            catch (ArgumentException) { }
        }
        check(requests.Count == 2, "无效或不支持的坐标不会触发网络请求");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await service.ResolveAsync(new ImageCoordinates(0, 0), canceled.Token); }
        catch (OperationCanceledException) { }
        check(requests.Count == 2, "已取消查询不会发送请求");
        check(new ImageCoordinates(0, 0).IsValid && new ImageCoordinates(1, 1, "WGS-84").IsWgs84,
            "零坐标有效，WGS 84 基准常见写法兼容");

        var limitedRequests = 0;
        using var limitedHttp = new HttpClient(new Handler((_, _) =>
        {
            limitedRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var limited = new ImageGeocodingService(limitedHttp);
        for (var i = 0; i < 2; i++)
        {
            try { await limited.ResolveAsync(point, CancellationToken.None); }
            catch (ImageGeocodingException) { }
        }
        check(limitedRequests == 1, "服务限流后进入冷却，不自动重复请求");

        await CheckViewModelAsync(check, metadata);
    }

    private static async Task CheckViewModelAsync(Action<bool, string> check, ImageMetadataInfo metadata)
    {
        var calls = 0;
        var pending = new TaskCompletionSource<ImageAddress>();
        CancellationToken requestToken = default;
        var vm = new ImageInfoViewModel(_ => Task.FromResult(metadata), (_, token) =>
        {
            calls++;
            requestToken = token;
            return pending.Task;
        });
        await vm.LoadAsync("example.jpg");
        check(calls == 0 && vm.CanResolve, "选图只读本地元信息，不自动联网发送 GPS");
        var lookup = vm.ResolveAddressAsync();
        await vm.ResolveAddressAsync();
        check(calls == 1 && vm.IsResolving && vm.CanSelect && vm.AddressActionText == "取消查询", "查询中禁止重复请求，仍可换图或取消");
        await vm.LoadAsync("new.jpg");
        pending.SetResult(Sample());
        await lookup;
        check(requestToken.IsCancellationRequested && vm.Region == "—" && !vm.IsResolving && vm.InputPath == "new.jpg", "换图取消旧查询，迟到结果不会污染新图片");

        pending = new TaskCompletionSource<ImageAddress>();
        lookup = vm.ResolveAddressAsync();
        vm.CancelAddressLookup();
        pending.SetResult(Sample());
        await lookup;
        check(!vm.IsResolving && vm.Region == "—" && vm.AddressStatus == "已取消查询", "主动取消后不接受迟到结果");
        pending = new TaskCompletionSource<ImageAddress>();
        lookup = vm.ResolveAddressAsync();
        pending.SetResult(Sample());
        await lookup;
        check(vm.Region.Contains("北京市") && vm.NearbyAddress.Contains("北池子") && vm.CanResolve, "成功显示城市和附近地址，支持再次查询");
        var missing = new ImageInfoViewModel(_ => Task.FromResult(ImageMetadataInfo.Empty), (_, _) => { calls++; return Task.FromResult(Sample()); });
        await missing.LoadAsync("no-gps.jpg");
        var before = calls;
        await missing.ResolveAddressAsync();
        check(!missing.CanResolve && calls == before && missing.AddressStatus.Contains("无法解析"), "缺失 GPS 时禁用解析且不发出请求");
        var timeout = new ImageInfoViewModel(_ => Task.FromResult(metadata), (_, _) => Task.FromException<ImageAddress>(new TaskCanceledException()));
        await timeout.LoadAsync("timeout.jpg");
        await timeout.ResolveAddressAsync();
        check(timeout.AddressStatus.Contains("超时") && timeout.CanResolve && ReferenceEquals(timeout.Details, metadata), "断网超时保留本地信息，并可手动重试");
        var offline = new ImageInfoViewModel(_ => Task.FromResult(metadata), (_, _) => Task.FromException<ImageAddress>(new HttpRequestException()));
        await offline.LoadAsync("offline.jpg");
        await offline.ResolveAddressAsync();
        check(offline.AddressStatus.Contains("检查网络") && !offline.IsResolving, "连接失败显示恢复建议且退出忙碌状态");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
