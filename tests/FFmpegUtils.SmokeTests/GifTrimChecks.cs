using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpegUtils.Controls;
using FFmpegUtils.Models;
using FFmpegUtils.Services;
using FFmpegUtils.ViewModels;

internal static class GifTrimChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(VideoTimeRange.TryParse("01:30", out var seconds) && seconds == 90, "分:秒按 90 秒解析，不误判成 1 小时 30 分");
        check(VideoTimeRange.TryParse("01:02:03.125", out seconds) && seconds == 3723.125, "时:分:秒支持毫秒");
        check(VideoTimeRange.TryParse("", out seconds) && seconds is null && VideoTimeRange.TryParse("1.25", out seconds) && seconds == 1.25, "空时间表示默认边界，支持小数秒");
        foreach (var invalid in new[] { "aaa", "NaN", "Infinity", "00:99", "1:60:00", "1:2:3:4" })
            check(!VideoTimeRange.TryParse(invalid, out _), $"拒绝非法时间 {invalid}");
        check(!VideoTimeRange.TryGet("2", "1", 10, out _, out _, out _)
              && !VideoTimeRange.TryGet("0", "11", 10, out _, out _, out _)
              && !VideoTimeRange.TryGet("10", "", 10, out _, out _, out _), "拒绝倒置、越界与空选区");
        check(VideoTimeRange.TryGet("", "", 10, out var start, out var end, out _) && start == 0 && end == 10, "重置/留空代表整段视频");
        check(VideoTimeRange.Format(90061.125) == "25:01:01.125", "长视频时间格式不在 24 小时处回绕");
        var args = VideoTimeRange.InputArguments("中文 路径.mp4", 2.25, 3.75);
        check(args.SequenceEqual(new[] { "-ss", "2.25", "-t", "1.5", "-i", "中文 路径.mp4" }), "截取限制在视频输入前设置，预览与 GIF 两轮使用相同选区");

        var fake = new FakePreview();
        var vm = new GifTrimViewModel(fake);
        var media = new MediaInfo("first.mp4", 640, 360, 25, 10, 100, false);
        vm.SetSource("ffmpeg.exe", media);
        check(fake.Frames == 0 && vm.Start == 0 && vm.End == 10, "折叠时不启动解码，默认选择全片");
        vm.SetRange(2.125, 4.25, 2.125);
        check(vm.Start == 2.125 && vm.End == 4.25 && vm.StartText == "2.125", "时间轴选区同步到精确时间文本");
        vm.StartText = "00:03";
        check(vm.Start == 3 && vm.End == 4.25, "手动时间与时间轴双向同步");
        vm.EndText = "aaa";
        check(vm.RangeError.Length > 0 && !vm.CanPlay, "非法时间禁止预览，不回退到旧选区");
        vm.EndText = "4.25";
        vm.SetActive(true);
        await Task.Delay(20);
        check(vm.Frame is not null && vm.Thumbnails.Count == 8 && vm.CanPlay, "展开后加载原视频帧和八张缩略图");
        await vm.SeekAsync(3.5);
        var playing = vm.PlayAsync();
        check(fake.LastStart == 3 && vm.IsPlaying, "首次播放始终从选区起点开始");
        vm.Pause();
        await playing;
        check(!vm.IsPlaying && vm.PlayText == "继续播放", "暂停后保留播放位置并提供继续播放");
        playing = vm.PlayAsync();
        check(fake.LastStart >= 3 && fake.LastStart < 4.25, "继续播放仅在选区内恢复");
        vm.SetActive(false);
        await playing;
        check(!vm.IsPlaying && !vm.IsBusy && vm.Start == 3 && vm.End == 4.25, "收起或切页停止解码并保留选区");
        vm.SetSource("ffmpeg.exe", media);
        check(vm.Start == 3 && vm.End == 4.25, "重新读取同一视频保留选择范围");
        vm.SetSource("ffmpeg.exe", media with { Path = "second.mp4", DurationSeconds = 5 });
        check(vm.Start == 0 && vm.End == 5 && vm.Frame is null && vm.Thumbnails.Count == 0, "换视频清空旧帧、缩略图与范围");
        vm.SetActive(true);
        vm.SetEnabled(false);
        check(!vm.CanPlay && !vm.IsPlaying, "转换时禁用预览并停止后台播放");
        vm.SetEnabled(true);
        vm.SetRange(1, 2, 1);
        vm.ResetRange();
        check(vm.Start == 0 && vm.End == 5 && vm.StartText == "" && vm.EndText == "", "一键重置全片并同步文本");
        vm.SetActive(false);

        var pending = new PendingPreview();
        var stale = new GifTrimViewModel(pending);
        stale.SetSource("ffmpeg.exe", media);
        stale.SetActive(true);
        stale.SetSource("ffmpeg.exe", null);
        pending.Frame.SetResult(FakePreview.SampleFrame(0));
        await Task.Delay(20);
        check(stale.Frame is null && !stale.HasVideo && !stale.IsBusy, "旧定位迟到结果不能覆盖新的视频状态");
        stale.SetActive(false);

        var finite = new FinitePreview();
        var loop = new GifTrimViewModel(finite);
        loop.SetSource("ffmpeg.exe", media);
        loop.SetRange(2, 4, 2);
        loop.SetActive(true);
        loop.Loop = true;
        finite.OnPlay = () => { if (finite.Starts.Count == 3) loop.Loop = false; };
        await loop.PlayAsync();
        check(finite.Starts.SequenceEqual(new[] { 2d, 2d, 2d }) && loop.Position == 4 && !loop.IsPlaying,
            "循环只重播选区，关闭循环后停在选区终点");
        loop.SetActive(false);
        await CheckScrubQueueAsync(media, check);
    }

    private static async Task CheckScrubQueueAsync(MediaInfo media, Action<bool, string> check)
    {
        var service = new ControlledScrubPreview();
        var vm = new GifTrimViewModel(service);
        vm.SetSource("ffmpeg.exe", media);
        vm.SetActive(true);
        vm.BeginInteraction(false);
        var first = vm.SeekAsync(1.013);
        var skipped = vm.SeekAsync(1.234);
        var latest = vm.SeekAsync(2.123);
        check(service.Requests.Count == 1 && skipped.IsCompleted && vm.Position == 2.123,
            "拖动立即更新播放指针，只保留一个进行中请求和一个最新位置");
        service.Requests[0].Result.SetResult(FakePreview.SampleFrame(1));
        await UntilAsync(() => service.Requests.Count == 2);
        check(vm.PreviewPosition == 1 && vm.Position == 2.123 && !service.Requests[0].Token.IsCancellationRequested,
            "持续拖动期间仍显示已解码画面，不因每个鼠标事件取消取帧或倒退播放指针");
        check(Math.Abs(service.Requests[1].Seconds - 2.1) < 0.0001, "跳过过时的中间位置，快览缓存按 0.05 秒组织");
        service.Requests[1].Result.SetResult(FakePreview.SampleFrame(2.1));
        await Task.WhenAll(first, latest);
        var cached = vm.Frame;
        var hit = vm.SeekAsync(2.112);
        check(hit.IsCompleted && ReferenceEquals(cached, vm.Frame) && service.Requests.Count == 2,
            "重复拖过缓存时间段立即显示，不再等待 120 毫秒或重新启动解码");
        var pending = vm.SeekAsync(3.147);
        await UntilAsync(() => service.Requests.Count == 3);
        await vm.EndInteractionAsync();
        var exact = vm.Frame;
        check(service.Requests[2].Token.IsCancellationRequested && Math.Abs(vm.PreviewPosition - 3.147) < 0.00001,
            "松开即取消快览并精确定位，不把 0.05 秒快览取整用于最终位置");
        service.Requests[2].Result.SetResult(FakePreview.SampleFrame(9));
        await pending;
        await Task.Delay(20);
        check(ReferenceEquals(exact, vm.Frame), "迟到的低清快览不能覆盖松开后的精确画面");
        vm.BeginInteraction(true);
        vm.SetRange(2, 4, 2);
        check(vm.Position == 3.147 && vm.Start == 2 && vm.End == 4, "调整边界不移动播放指针，吸附参考位置稳定");
        await vm.EndInteractionAsync();
        check(vm.PreviewTimeText.StartsWith("边界") && vm.Position == 3.147, "边界画面时间独立显示，不冒充播放指针时间");
        vm.SetActive(false);
        foreach (var request in service.Requests) request.Result.TrySetResult(FakePreview.SampleFrame(request.Seconds));

        var thumbnails = new BlockingThumbnailPreview();
        var priority = new GifTrimViewModel(thumbnails);
        priority.SetSource("ffmpeg.exe", media);
        priority.SetActive(true);
        check(thumbnails.ThumbnailToken.CanBeCanceled, "后台缩略图任务已开始");
        priority.BeginInteraction(false);
        await priority.SeekAsync(1);
        check(thumbnails.ThumbnailToken.IsCancellationRequested && thumbnails.Scrubbed,
            "用户拖动优先取消后台缩略图，不让缩略图抢占预览解码");
        priority.SetActive(false);
    }

    private static async Task UntilAsync(Func<bool> condition, int timeout = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Preview assertion timed out.");
            await Task.Delay(10);
        }
    }

    internal static async Task IntegrationAsync(string ffmpeg, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "中文 选区测试.mp4");
        await RunFfmpegAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "color=c=red:s=320x180:r=25:d=2",
            "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=2", "-f", "lavfi", "-i", "color=c=green:s=320x180:r=25:d=2",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-g", "250", "-sc_threshold", "0", "-pix_fmt", "yuv420p", video]);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(video));
        var preview = new VideoPreviewService();
        var red = await preview.GetFrameAsync(ffmpeg, video, 0.75, true, CancellationToken.None);
        var blue = await preview.GetFrameAsync(ffmpeg, video, 2.25, false, CancellationToken.None);
        var green = await preview.GetFrameAsync(ffmpeg, video, 4.5, false, CancellationToken.None);
        check(IsColor(red, 2) && IsColor(blue, 0) && IsColor(green, 1), "真实 FFmpeg 在非关键帧处准确跳转并提取对应红/蓝/绿画面");
        var fast = await preview.GetScrubFrameAsync(ffmpeg, video, 2.25, CancellationToken.None);
        check(fast.Width == 320 && fast.Height == 180 && IsColor(fast, 0) && blue.Width == 640,
            "真实 FFmpeg 拖动快览为 320×180，精确定位仍为 640×360");
        var positions = new List<double>();
        var blueOnly = true;
        await preview.PlayAsync(ffmpeg, video, 2.25, 2.75, frame => { positions.Add(frame.Seconds); blueOnly &= IsColor(frame, 0); }, CancellationToken.None);
        check(positions.Count >= 8 && positions.All(position => position >= 2.25 && position < 2.75) && blueOnly, "真实播放只返回选区内的蓝色视频帧");
        using var cancellation = new CancellationTokenSource();
        var cancelClock = Stopwatch.StartNew();
        try { await preview.PlayAsync(ffmpeg, video, 0, 6, _ => cancellation.Cancel(), cancellation.Token); }
        catch (OperationCanceledException) { }
        check(cancelClock.Elapsed < TimeSpan.FromSeconds(5), "播放取消快速终止 FFmpeg 并回收资源");
        var afterCancel = await preview.GetFrameAsync(ffmpeg, video, 2.25, false, CancellationToken.None);
        check(IsColor(afterCancel, 0), "取消后立即定位可用，无进程/信号量阻塞");
        var installation = await new FfmpegLocator().InspectAsync(ffmpeg);
        var media = await new MediaProbeService().ProbeAsync(installation.FfprobePath, video);
        var gifPath = Path.Combine(directory, "选中的蓝色片段.gif");
        await new GifConversionService(new FfmpegProcessRunner()).ConvertAsync(installation, media,
            new GifConversionOptions(video, gifPath, 320, 20, 128, "none", null, 2.25, 3.25), null, CancellationToken.None);
        var gifInfo = await new MediaProbeService().ProbeAsync(installation.FfprobePath, gifPath);
        var gifFrame = await preview.GetFrameAsync(ffmpeg, gifPath, 0, true, CancellationToken.None);
        check(Math.Abs(gifInfo.DurationSeconds - 1) < 0.12 && IsColor(gifFrame, 0), "实际 GIF 与选区一致：非零起点、1 秒时长且仅含蓝色画面");
        var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(video));
        check(hash.SequenceEqual(afterHash), "预览与转换不修改源视频");
        CheckUi(video, ffmpeg, Path.Combine(directory, "timeline-preview.png"), check);
    }

    private static void CheckUi(string video, string ffmpeg, string screenshot, Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                FFmpegUtils.MainWindow? window = null;
                var bindingTrace = PresentationTraceSources.DataBindingSource;
                var previousLevel = bindingTrace.Switch.Level;
                var listener = new CollectingTraceListener();
                try
                {
                    bindingTrace.Listeners.Add(listener);
                    bindingTrace.Switch.Level = SourceLevels.Warning;
                    window = new FFmpegUtils.MainWindow { ShowInTaskbar = false };
                    var vm = (MainViewModel)window.DataContext;
                    window.Show();
                    for (var i = 0; i < 100 && vm.EngineStatus.Contains("查找"); i++) await Task.Delay(50);
                    // Inject the test engine without writing the user's saved preferences.
                    typeof(MainViewModel).GetField("_installation", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(vm, await new FfmpegLocator().InspectAsync(ffmpeg));
                    typeof(MainViewModel).GetMethod("UpdateInstallationDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
                    await vm.SetGifInputAsync(video);
                    var expander = (Expander)window.FindName("GifTrimExpander");
                    var editor = (GifTrimEditor)window.FindName("GifTrimPanel");
                    var scroll = (ScrollViewer)window.FindName("GifScrollViewer");
                    check(!expander.IsExpanded && !editor.IsVisible, "真实界面默认折叠且不另开预览窗口");
                    expander.IsExpanded = true;
                    for (var i = 0; i < 200 && (vm.GifTrim.Frame is null || vm.GifTrim.Thumbnails.Count < 8); i++) await Task.Delay(50);
                    check(vm.GifTrim.Frame is not null && vm.GifTrim.Thumbnails.Count == 8, "真实 WPF 页面显示 FFmpeg 视频画面与缩略图");
                    vm.GifTrim.SetRange(2.25, 3.25, 2.25);
                    await vm.GifTrim.SeekAsync(2.25);
                    window.UpdateLayout();
                    scroll.ScrollToBottom();
                    await Task.Delay(100);
                    var timeline = (RangeTimeline)editor.FindName("Timeline");
                    check(timeline.ActualWidth > 300 && scroll.ScrollableWidth < 0.5 && scroll.ScrollableHeight > 0,
                        "展开后允许纵向滚动，时间轴和视频区域无横向溢出");
                    check(vm.GifStartTimeText == "2.25" && vm.GifEndTimeText == "3.25", "内嵌选区直接同步主页面的实际转换参数");
                    void PointerAt(string method, double x, double y = 27)
                    {
                        typeof(RangeTimeline).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(timeline, [new Point(x, y)]);
                    }
                    double X(double seconds) => 10 + (timeline.ActualWidth - 20) * seconds / vm.GifTrim.Duration;
                    PointerAt("BeginPointer", X(1.2));
                    PointerAt("MovePointer", X(4.2));
                    PointerAt("EndPointer", X(4.2));
                    check(vm.GifTrim.Start == 2.25 && vm.GifTrim.End == 3.25 && Math.Abs(vm.GifTrim.Position - 4.2) < 0.002,
                        "缩略图内外拖动只定位播放位置，不再重新框选范围");
                    vm.GifTrim.SetRange(1.2, 4.2, 1.2);
                    await vm.GifTrim.SeekAsync(3);
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(1.2) - 5);
                    PointerAt("MovePointer", X(1.2) - 5);
                    check(vm.GifTrim.Start == 1.2, "按住外侧小手柄时边界不跳到鼠标位置");
                    PointerAt("MovePointer", X(1.8) - 5);
                    PointerAt("EndPointer", X(1.8) - 5);
                    check(vm.GifTrim.Start == 1.8 && vm.GifTrim.End == 4.2 && vm.GifTrim.Position == 3,
                        "拖动起点保留抓取偏移，只调整起点且播放指针不动");
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(4.2) + 5);
                    PointerAt("MovePointer", X(4.8) + 5);
                    PointerAt("EndPointer", X(4.8) + 5);
                    check(vm.GifTrim.Start == 1.8 && vm.GifTrim.End == 4.8 && vm.GifTrim.Position == 3, "拖动终点只调整终点，播放指针不动");
                    PointerAt("BeginPointer", X(0.6), 66);
                    PointerAt("MovePointer", X(2.4), 66);
                    PointerAt("EndPointer", X(2.4), 66);
                    check(Math.Abs(vm.GifTrim.Position - 2.4) < 0.002 && Math.Abs(vm.GifTrim.Start - 1.8) < 0.002,
                        "播放指针可单独定位，不改变选区范围");
                    await vm.GifTrim.SeekAsync(3);
                    vm.GifTrim.SetRange(1, 5, 1);
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(1) - 5);
                    PointerAt("MovePointer", X(3) - 5 - 6);
                    check(timeline.IsSnapped && vm.GifTrim.Start == 3 && vm.GifTrim.Position == 3, "起点在距播放指针 8 像素内吸附");
                    PointerAt("MovePointer", X(3) - 5 + 10);
                    check(timeline.IsSnapped && vm.GifTrim.Start == 3, "吸附后在 12 像素内保持，避免临界抖动");
                    PointerAt("MovePointer", X(3) - 5 + 15);
                    check(!timeline.IsSnapped && vm.GifTrim.Start > 3, "继续拖远可解除吸附");
                    PointerAt("EndPointer", X(3) - 5 + 15);
                    vm.GifTrim.SetRange(1, 5, 1);
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(5) + 5);
                    PointerAt("MovePointer", X(3) + 5 + 6);
                    check(timeline.IsSnapped && vm.GifTrim.End == 3, "终点同样可吸附到固定播放指针");
                    PointerAt("EndPointer", X(3) + 5 + 6);
                    vm.GifTrim.SetRange(1, 5, 1);
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(1) + 1);
                    PointerAt("MovePointer", X(1) + 6);
                    PointerAt("EndPointer", X(1) + 6);
                    check(vm.GifTrim.Start == 1 && vm.GifTrim.End == 5, "紧贴边界内侧的缩略图仍可定位，不被手柄热区抢占");
                    await vm.GifTrim.SeekAsync(0);
                    PointerAt("BeginPointer", X(5) + 5);
                    PointerAt("MovePointer", X(0) + 5);
                    check(!timeline.IsSnapped && vm.GifTrim.End > vm.GifTrim.Start, "非法吸附目标不产生倒置或空选区");
                    PointerAt("EndPointer", X(0) + 5);
                    vm.GifTrim.SetRange(3, 3.001, 3);
                    window.UpdateLayout();
                    PointerAt("BeginPointer", X(3.001) + 5);
                    PointerAt("MovePointer", X(3.5) + 5);
                    PointerAt("EndPointer", X(3.5) + 5);
                    check(vm.GifTrim.Start == 3 && vm.GifTrim.End == 3.5, "极窄选区的外侧终点手柄仍能独立抓取");
                    vm.GifTrim.SetRange(1, 5, 1);
                    await vm.GifTrim.SeekAsync(3);
                    window.UpdateLayout();
                    void KeyStep(Key key, ModifierKeys modifiers) => typeof(RangeTimeline)
                        .GetMethod("HandleKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timeline, [key, modifiers]);
                    KeyStep(Key.Right, ModifierKeys.None);
                    check(Math.Abs(vm.GifTrim.Position - 3.05) < 0.00001, "左右键每次定位 0.05 秒");
                    KeyStep(Key.Right, ModifierKeys.Shift);
                    KeyStep(Key.Left, ModifierKeys.Control);
                    check(vm.GifTrim.Start == 1.05 && vm.GifTrim.End == 4.95, "Shift / Ctrl 组合键同样按 0.05 秒微调起止");
                    var peer = UIElementAutomationPeer.CreatePeerForElement(timeline)!;
                    check(((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!).SmallChange == 0.05, "无障碍时间轴步长与键盘一致");
                    check(timeline.ToolTip is null && ((Grid)editor.FindName("PreviewViewport")).ToolTip is null, "时间轴与预览画面均无悬浮提示");
                    var playButton = (Button)editor.FindName("PlaySelectionButton");
                    var loopButton = (ToggleButton)editor.FindName("LoopSelectionButton");
                    check(playButton.Content is Grid && playButton.ActualWidth == 28 && AutomationProperties.GetName(playButton).Length > 0
                          && ((Button)editor.FindName("ResetSelectionButton")).Content is Grid, "紧凑矢量图标按钮具有可访问名称");
                    loopButton.IsChecked = true;
                    check(vm.GifTrim.Loop && vm.GifTrim.LoopText.Contains("已开启"), "循环图标切换开启状态并提供明确反馈");
                    loopButton.IsChecked = false;
                    var framesDuringDrag = 0;
                    System.ComponentModel.PropertyChangedEventHandler countFrame = (_, e) => { if (e.PropertyName == nameof(GifTrimViewModel.Frame)) framesDuringDrag++; };
                    vm.GifTrim.PropertyChanged += countFrame;
                    PointerAt("BeginPointer", X(0.4), 66);
                    for (var i = 0; i < 24; i++)
                    {
                        PointerAt("MovePointer", X(0.4 + i * 0.11), 66);
                        await Task.Delay(30);
                    }
                    check(framesDuringDrag >= 2, "真实连续拖动期间已多次更新画面，不必停下鼠标");
                    vm.GifTrim.PropertyChanged -= countFrame;
                    var release = Stopwatch.StartNew();
                    PointerAt("EndPointer", X(3.3438), 66);
                    await UntilAsync(() => Math.Abs(vm.GifTrim.PreviewPosition - 3.344) < 0.00001 && vm.GifTrim.Frame is BitmapSource { PixelWidth: 640 }, 10000);
                    Console.WriteLine($"SCRUB_SAMPLE: 持续拖动期间 {framesDuringDrag} 次画面更新；松开到精确画面 {release.ElapsedMilliseconds} ms（测试视频）");
                    vm.GifEndTimeText = "999";
                    check(!vm.IsGifReady && !vm.GifTrim.CanPlay, "结束时间越界时同时禁止预览和 GIF 转换");
                    vm.GifTrim.SetRange(2.25, 3.25, 2.25);
                    await vm.GifTrim.SeekAsync(2.8);
                    playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    check(vm.GifTrim.IsPlaying && ((System.Windows.Shapes.Path)editor.FindName("PlayPauseIcon")).StrokeThickness == 2.5,
                        "点击播放图标开始播放，并切换为暂停图标");
                    playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(!vm.GifTrim.IsPlaying, "再次点击暂停图标可立即暂停");
                    await vm.GifTrim.SeekAsync(2.8);
                    ((Button)editor.FindName("RestartSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => vm.GifTrim.IsPlaying && !vm.GifTrim.IsBusy, 10000);
                    check(vm.GifTrim.Position >= 2.25 && vm.GifTrim.Position < 2.8, "重新播放图标确实从选区起点播放");
                    playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((Button)editor.FindName("ResetSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(vm.GifTrim.Start == 0 && vm.GifTrim.End == 6 && vm.GifTrim.Position == 0, "重置图标恢复整段视频");
                    vm.GifTrim.SetRange(2.25, 3.25, 2.25);
                    await vm.GifTrim.SeekAsync(2.25);
                    timeline.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                    window.UpdateLayout();
                    scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + expander.TranslatePoint(new Point(0, 0), scroll).Y));
                    await Task.Delay(100);
                    check(expander.TranslatePoint(new Point(0, 0), scroll).Y >= -1
                          && expander.TranslatePoint(new Point(0, expander.ActualHeight), scroll).Y <= scroll.ViewportHeight + 1,
                        "默认窗口可同时看到展开标题、视频画面、时间轴和时间输入");
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(screenshot)) encoder.Save(output);
                    var play = vm.GifTrim.PlayAsync();
                    await Task.Delay(200);
                    expander.IsExpanded = false;
                    await play;
                    check(!vm.GifTrim.IsPlaying && vm.GifTrim.Start == 2.25 && vm.GifTrim.End == 3.25, "真实折叠事件暂停播放并保留选区");
                    expander.IsExpanded = true;
                    await Task.Delay(150);
                    play = vm.GifTrim.PlayAsync(restart: true);
                    await Task.Delay(100);
                    ((TabControl)window.FindName("MainTabs")).SelectedIndex = 1;
                    await play;
                    check(!vm.GifTrim.IsPlaying, "切换子页面停止隐藏的视频预览");
                    bindingTrace.Flush();
                    check(!listener.Text.Contains("Error:", StringComparison.OrdinalIgnoreCase), "内嵌视频预览及时间轴无 WPF 绑定错误");
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    window?.Close();
                    bindingTrace.Listeners.Remove(listener);
                    bindingTrace.Switch.Level = previousLevel;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("UI preview test timed out.");
        if (failure is not null) throw new InvalidOperationException("UI preview test failed.", failure);
    }

    private static bool IsColor(VideoPreviewFrame frame, int channel)
    {
        var offset = ((frame.Height / 2) * frame.Width + frame.Width / 2) * 4;
        return frame.Pixels[offset + channel] > 80 && Enumerable.Range(0, 3).Where(index => index != channel).All(index => frame.Pixels[offset + index] < 40);
    }
    private static async Task RunFfmpegAsync(string ffmpeg, string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-loglevel"); process.StartInfo.ArgumentList.Add("error");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
        await error;
    }

    internal sealed class FakePreview : IVideoPreviewService
    {
        internal int Frames;
        internal double LastStart;
        internal static VideoPreviewFrame SampleFrame(double seconds) => new([255, 0, 0, 255], 1, 1, seconds);
        public Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frames++; return Task.FromResult(SampleFrame(seconds)); }
        public async Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token)
        { LastStart = start; frame(SampleFrame(start + 0.1)); await Task.Delay(Timeout.Infinite, token); }
    }
    private sealed class PendingPreview : IVideoPreviewService
    {
        internal TaskCompletionSource<VideoPreviewFrame> Frame = new();
        public Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token) => Frame.Task;
        public Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class FinitePreview : IVideoPreviewService
    {
        internal List<double> Starts = [];
        internal Action? OnPlay;
        public Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token)
            => Task.FromResult(FakePreview.SampleFrame(seconds));
        public async Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token)
        {
            Starts.Add(start);
            frame(FakePreview.SampleFrame(start));
            await Task.Delay(5, token);
            OnPlay?.Invoke();
        }
    }

    private sealed class ControlledScrubPreview : IVideoPreviewService
    {
        internal sealed record Request(double Seconds, CancellationToken Token)
        {
            internal TaskCompletionSource<VideoPreviewFrame> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal List<Request> Requests = [];
        public Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token)
            => Task.FromResult(FakePreview.SampleFrame(seconds));
        public Task<VideoPreviewFrame> GetScrubFrameAsync(string ffmpeg, string path, double seconds, CancellationToken token)
        {
            var request = new Request(seconds, token);
            Requests.Add(request);
            return request.Result.Task; // Deliberately allow late completion to test stale-result protection.
        }
        public Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class BlockingThumbnailPreview : IVideoPreviewService
    {
        internal CancellationToken ThumbnailToken;
        internal bool Scrubbed;
        public async Task<VideoPreviewFrame> GetFrameAsync(string ffmpeg, string path, double seconds, bool thumbnail, CancellationToken token)
        {
            if (thumbnail) { ThumbnailToken = token; await Task.Delay(Timeout.Infinite, token); }
            return FakePreview.SampleFrame(seconds);
        }
        public Task<VideoPreviewFrame> GetScrubFrameAsync(string ffmpeg, string path, double seconds, CancellationToken token)
        { Scrubbed = true; return Task.FromResult(FakePreview.SampleFrame(seconds)); }
        public Task PlayAsync(string ffmpeg, string path, double start, double end, Action<VideoPreviewFrame> frame, CancellationToken token) => Task.CompletedTask;
    }
}
