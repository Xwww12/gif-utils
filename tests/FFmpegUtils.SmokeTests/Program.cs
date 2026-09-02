using System.Text;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpegUtils.Models;
using FFmpegUtils.Services;
using FFmpegUtils.ViewModels;

var failures = new List<string>();

if (args is ["--gif-trim-integration", var trimFfmpeg, var trimDirectory])
{
    await GifTrimChecks.IntegrationAsync(trimFfmpeg, trimDirectory, Check);
    Console.WriteLine(failures.Count == 0 ? "GIF_TRIM_INTEGRATION_OK" : "GIF_TRIM_INTEGRATION_FAILED");
    return failures.Count == 0 ? 0 : 1;
}

if (args is ["--geocode-sample"])
{
    // Public landmark-area coordinates only; never reads user photos for this network diagnostic.
    var address = await ImageGeocodingService.Shared.ResolveAsync(new ImageCoordinates(39.916345, 116.397155), CancellationToken.None);
    Console.WriteLine(address.Region);
    Console.WriteLine(address.NearbyAddress);
    Console.WriteLine(address.Detail);
    return 0;
}

if (args is ["--image-info", var imageInfoInput])
{
    var info = await new ImageMetadataService().ReadAsync(imageInfoInput);
    foreach (var field in info.Dimensions.Concat(info.Shooting).Concat(info.Location))
        Console.WriteLine($"{field.Name}: {field.Value}");
    return 0;
}

if (args is ["--x-url", var xParseUrl])
{
    return await RunXParseIntegrationAsync(xParseUrl);
}

if (args is ["--x-download", var xDownloadUrl, var xOutputDirectory, var xFfmpegPath])
{
    return await RunXDownloadIntegrationAsync(xDownloadUrl, xOutputDirectory, xFfmpegPath);
}

if (args is ["--render-x", var xScreenshotPath])
{
    return RenderXPage(xScreenshotPath);
}

if (args is ["--render-x-quality", var xQualityScreenshotPath])
{
    return RenderXPage(xQualityScreenshotPath, includeQualitySample: true);
}

if (args is ["--render-image-info", var imageInfoScreenshotPath])
{
    return RenderXPage(imageInfoScreenshotPath, includeImageInfoSample: true);
}

CheckWindowConstruction();
var applicationAssembly = typeof(FFmpegUtils.MainWindow).Assembly;
Check(applicationAssembly.GetName().Name == "GIFUtils"
    && applicationAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title == "GIF Utils"
    && applicationAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product == "GIF Utils",
    "程序名称与文件属性统一为 GIF Utils");
await GifTrimChecks.RunAsync(Check);
await ImageMetadataChecks.RunAsync(Check);
await ImageGeocodingChecks.RunAsync(Check);
CheckNumericValidation();
CheckXUrlNormalization();
CheckXMediaJsonParsing();
CheckXFileNames();
CheckXProgressParsing();
CheckXFriendlyErrors();

Check(FfmpegProcessRunner.TryGetProgressSeconds("out_time_us", "2500000", out var seconds) && Math.Abs(seconds - 2.5) < 0.001,
    "进度微秒解析");
Check(FfmpegProcessRunner.TryGetProgressSeconds("out_time", "00:00:03.500000", out seconds) && Math.Abs(seconds - 3.5) < 0.001,
    "进度时间解析");

Check(SubtitleVideoEncoderCatalog.TryParseDisplayName(SubtitleVideoEncoderCatalog.NvidiaDisplayName, out var parsedEncoder)
      && parsedEncoder == SubtitleVideoEncoder.Nvidia,
    "字幕视频编码方式解析");
var cpuEncoderArguments = SubtitleBurnService.BuildVideoEncoderArguments(SubtitleVideoEncoder.Cpu, 20);
var nvencArguments = SubtitleBurnService.BuildVideoEncoderArguments(SubtitleVideoEncoder.Nvidia, 20);
var qsvArguments = SubtitleBurnService.BuildVideoEncoderArguments(SubtitleVideoEncoder.Intel, 20);
var amfArguments = SubtitleBurnService.BuildVideoEncoderArguments(SubtitleVideoEncoder.Amd, 20);
Check(cpuEncoderArguments.Contains("libx264") && cpuEncoderArguments.Contains("-crf"), "CPU 字幕编码参数");
Check(nvencArguments.Contains("h264_nvenc") && nvencArguments.Contains("-cq"), "NVIDIA 字幕编码参数");
Check(qsvArguments.Contains("h264_qsv") && qsvArguments.Contains("-global_quality"), "Intel 字幕编码参数");
Check(amfArguments.Contains("h264_amf") && amfArguments.Contains("-qp_i"), "AMD 字幕编码参数");

var escaped = FfmpegFilterEscaper.EscapePath(@"C:\测试 文件\a,b.srt");
Check(escaped.Contains("C\\:/", StringComparison.Ordinal) && escaped.Contains("a\\,b.srt", StringComparison.Ordinal),
    "字幕滤镜路径转义");

var reduced = GifSizeTuner.Reduce(new GifSizeParameters(720, 15, 192), 10_000_000, 4_000_000);
Check(reduced.Width < 720 && reduced.FrameRate <= 15 && reduced.Colors <= 192, "GIF 目标大小降级策略");
var smallSource = GifSizeTuner.Reduce(new GifSizeParameters(180, 10, 128), 1_000_000, 500_000, 180);
Check(smallSource.Width <= 180, "小尺寸视频不会被放大");

var utf8Path = Path.Combine(Path.GetTempPath(), $"ffmpegutils-utf8-{Guid.NewGuid():N}.srt");
try
{
    await File.WriteAllTextAsync(utf8Path, "1\n00:00:00,000 --> 00:00:01,000\n中文字幕\n", new UTF8Encoding(false));
    Check(SubtitleBurnService.DetectEncodingName(utf8Path) == "UTF-8", "字幕 UTF-8 检测");
}
finally
{
    if (File.Exists(utf8Path)) File.Delete(utf8Path);
}

if (args.Length >= 2 && File.Exists(args[0]) && File.Exists(args[1]))
{
    await RunIntegrationAsync(args[0], args[1]);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED: {string.Join("; ", failures)}");
    return 1;
}

Console.WriteLine("SMOKE_TESTS_OK");
return 0;

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"PASS: {name}");
    }
    else
    {
        failures.Add(name);
        Console.Error.WriteLine($"FAIL: {name}");
    }
}

void CheckWindowConstruction()
{
    Exception? windowError = null;
    var compactWindow = false;
    var wheelSuppressed = false;
    var comboWheelScrollsPage = false;
    var inputWheelScrollsPage = false;
    var popupAlignedAndEqualWidth = false;
    var gifFitsWithoutScrolling = false;
    var gifScrollableHeight = 0d;
    var gifFitsWithoutHorizontalOverflow = false;
    var gifScrollableWidth = 0d;
    var subtitleFitsWithoutScrolling = false;
    var subtitleFitsWithoutHorizontalOverflow = false;
    var subtitleScrollableWidth = 0d;
    var headerActionsMoved = false;
    var headerProgressMoved = false;
    var engineSelectorMoved = false;
    var engineSelectorsCentered = false;
    var mouseFocusCueHidden = false;
    var keyboardFocusCueVisible = false;
    var keyboardFocusCueDiagnostic = string.Empty;
    var emptyEngineErrorRowsCollapsed = false;
    var subtitleVideoEncoderSelectorPresent = false;
    var xTabPresent = false;
    var xFitsWithoutScrolling = false;
    var xScrollableHeight = 0d;
    var xFitsWithoutHorizontalOverflow = false;
    var xScrollableWidth = 0d;
    var xHeaderActionsPresent = false;
    var xHeaderProgressPresent = false;
    var xEngineSelectorCentered = false;
    var xMediaListOwnsScrolling = false;
    var xQualityTextRendered = false;
    var xQualityPopupNoHorizontalScroll = false;
    var xQualityWidthSufficient = false;
    var appIconPresent = false;
    var appNameMatches = false;
    var imageTabPresent = false;
    var imagePageFits = false;
    var imageFieldsReadOnly = false;
    var imagePageHasNoConversionControls = false;
    var imageValuesRendered = false;
    var imageWheelScrollsPage = false;
    var xBindingsClean = false;
    var xBindingError = string.Empty;
    var thread = new Thread(() =>
    {
        var bindingTrace = PresentationTraceSources.DataBindingSource;
        var previousBindingTraceLevel = bindingTrace.Switch.Level;
        var bindingListener = new CollectingTraceListener();
        try
        {
            bindingTrace.Listeners.Add(bindingListener);
            bindingTrace.Switch.Level = SourceLevels.Warning;
            var window = new FFmpegUtils.MainWindow();
            appIconPresent = window.Icon is not null;
            compactWindow = window.Width == 780 && window.Height == 540;
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            appNameMatches = window.Title == "GIF Utils"
                && FindVisualChildren<TextBlock>(window).Any(text => text.IsVisible && text.Text == "GIF Utils")
                && !FindVisualChildren<TextBlock>(window).Any(text => text.IsVisible && text.Text == "FFmpeg Utils");

            var tabs = (TabControl)window.FindName("MainTabs");
            var gifScrollViewer = (ScrollViewer)((TabItem)tabs.Items[0]).Content;
            gifScrollableHeight = gifScrollViewer.ScrollableHeight;
            gifFitsWithoutScrolling = gifScrollViewer.ScrollableHeight < 0.5;
            gifScrollableWidth = gifScrollViewer.ScrollableWidth;
            gifFitsWithoutHorizontalOverflow = gifScrollViewer.ScrollableWidth < 0.5;

            var visibleButtons = FindVisualChildren<Button>(window).Where(button => button.IsVisible).ToList();
            var startButton = visibleButtons.FirstOrDefault(button => Equals(button.Content, "开始转换"));
            var selectEngineButton = visibleButtons.FirstOrDefault(button => Equals(button.Content, "选择 FFmpeg"));
            headerActionsMoved = startButton is not null && FindVisualParent<ScrollViewer>(startButton) is null;
            engineSelectorMoved = selectEngineButton is not null && FindVisualParent<ScrollViewer>(selectEngineButton) == gifScrollViewer;
            var gifEngineSelectorCentered = selectEngineButton is not null
                && Grid.GetRowSpan(selectEngineButton) == 2
                && selectEngineButton.VerticalAlignment == VerticalAlignment.Center;
            if (selectEngineButton is not null)
            {
                selectEngineButton.ApplyTemplate();
                window.SetCurrentValue(FFmpegUtils.MainWindow.ShowKeyboardFocusCuesProperty, false);
                window.Activate();
                Keyboard.Focus(selectEngineButton);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var focusBorder = selectEngineButton.Template.FindName("FocusBorder", selectEngineButton) as Border;
                mouseFocusCueHidden = selectEngineButton.IsKeyboardFocused
                    && selectEngineButton.FocusVisualStyle is null
                    && focusBorder?.BorderBrush is SolidColorBrush { Color.A: 0 };

                window.SetCurrentValue(FFmpegUtils.MainWindow.ShowKeyboardFocusCuesProperty, true);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                keyboardFocusCueVisible = selectEngineButton.IsKeyboardFocused
                    && focusBorder?.BorderBrush is SolidColorBrush { Color.A: > 0 };
                keyboardFocusCueDiagnostic = $"focus={selectEngineButton.IsKeyboardFocused}, mode={window.ShowKeyboardFocusCues}, brush={focusBorder?.BorderBrush}";
                window.SetCurrentValue(FFmpegUtils.MainWindow.ShowKeyboardFocusCuesProperty, false);
            }
            headerProgressMoved = FindVisualChildren<ProgressBar>(window)
                .Any(progressBar => progressBar.IsVisible && FindVisualParent<ScrollViewer>(progressBar) is null);

            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var subtitleScrollViewer = (ScrollViewer)((TabItem)tabs.Items[1]).Content;
            subtitleFitsWithoutScrolling = subtitleScrollViewer.ScrollableHeight < 0.5;
            subtitleScrollableWidth = subtitleScrollViewer.ScrollableWidth;
            subtitleFitsWithoutHorizontalOverflow = subtitleScrollViewer.ScrollableWidth < 0.5;
            var subtitleEngineSelector = FindVisualChildren<Button>(window)
                .FirstOrDefault(button => button.IsVisible && Equals(button.Content, "选择 FFmpeg"));
            engineSelectorsCentered = gifEngineSelectorCentered
                && subtitleEngineSelector is not null
                && Grid.GetRowSpan(subtitleEngineSelector) == 2
                && subtitleEngineSelector.VerticalAlignment == VerticalAlignment.Center;
            emptyEngineErrorRowsCollapsed = window.FindName("GifEngineErrorText") is TextBlock { Visibility: Visibility.Collapsed }
                && window.FindName("SubtitleEngineErrorText") is TextBlock { Visibility: Visibility.Collapsed };
            var videoEncoderSelector = FindVisualChildren<ComboBox>(window)
                .FirstOrDefault(item => item.Items.Cast<object?>().Any(value => Equals(value, SubtitleVideoEncoderCatalog.NvidiaDisplayName)));
            subtitleVideoEncoderSelectorPresent = videoEncoderSelector is not null
                && videoEncoderSelector.Items.Count == SubtitleVideoEncoderCatalog.DisplayNames.Count
                && Equals(videoEncoderSelector.SelectedItem, SubtitleVideoEncoderCatalog.AutoDisplayName);

            xTabPresent = tabs.Items.Count == 4
                && tabs.Items[2] is TabItem { Name: "XDownloadTab" } xTab
                && Equals(xTab.Header, "X 下载");
            tabs.SelectedIndex = 2;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (tabs.Items[2] is TabItem { Content: ScrollViewer xScrollViewer })
            {
                xScrollableHeight = xScrollViewer.ScrollableHeight;
                xFitsWithoutScrolling = xScrollViewer.ScrollableHeight < 0.5;
                xScrollableWidth = xScrollViewer.ScrollableWidth;
                xFitsWithoutHorizontalOverflow = xScrollViewer.ScrollableWidth < 0.5;

                var xButtons = FindVisualChildren<Button>(window).Where(button => button.IsVisible).ToList();
                var startDownload = xButtons.FirstOrDefault(button => Equals(button.Content, "开始下载"));
                var cancelDownload = xButtons.FirstOrDefault(button => Equals(button.Content, "取消"));
                xHeaderActionsPresent = startDownload is not null
                    && cancelDownload is not null
                    && FindVisualParent<ScrollViewer>(startDownload) is null
                    && FindVisualParent<ScrollViewer>(cancelDownload) is null;
                xHeaderProgressPresent = FindVisualChildren<ProgressBar>(window)
                    .Any(progressBar => progressBar.IsVisible && FindVisualParent<ScrollViewer>(progressBar) is null);

                var xEngineSelector = xButtons.FirstOrDefault(button => Equals(button.Content, "选择 FFmpeg"));
                xEngineSelectorCentered = xEngineSelector is not null
                    && FindVisualParent<ScrollViewer>(xEngineSelector) == xScrollViewer
                    && Grid.GetRowSpan(xEngineSelector) == 2
                    && xEngineSelector.VerticalAlignment == VerticalAlignment.Center;
                engineSelectorsCentered = engineSelectorsCentered && xEngineSelectorCentered;
                emptyEngineErrorRowsCollapsed = emptyEngineErrorRowsCollapsed
                    && window.FindName("XEngineErrorText") is TextBlock { Visibility: Visibility.Collapsed };

                var mediaList = FindVisualChildren<ListBox>(window)
                    .FirstOrDefault(listBox => AutomationProperties.GetName(listBox) == "解析到的 X 媒体列表");
                var mediaScrollViewer = mediaList is null
                    ? null
                    : FindVisualChildren<ScrollViewer>(mediaList).FirstOrDefault();
                xMediaListOwnsScrolling = mediaList is not null
                    && mediaScrollViewer is not null
                    && mediaScrollViewer != xScrollViewer
                    && ScrollViewer.GetVerticalScrollBarVisibility(mediaList) == ScrollBarVisibility.Auto
                    && ScrollViewer.GetHorizontalScrollBarVisibility(mediaList) == ScrollBarVisibility.Disabled;

                if (mediaList is not null && window.DataContext is MainViewModel viewModel)
                {
                    const string qualityLabel = "720×814 · 2176 kbps · MP4 直链（最高）";
                    var quality = new XQualityOption(qualityLabel, "http-2176+bestaudio/best", 720, 814, 2176, false, true, "http-2176");
                    viewModel.XMediaItems.Add(new XMediaItem(1, 1, "test-video", "媒体 1", "", "视频", 6, [quality]));
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                    var qualityCombo = FindVisualChildren<ComboBox>(mediaList)
                        .FirstOrDefault(item => AutomationProperties.GetName(item) == "下载画质");
                    if (qualityCombo is not null)
                    {
                        qualityCombo.ApplyTemplate();
                        window.UpdateLayout();
                        xQualityWidthSufficient = qualityCombo.ActualWidth >= 284;
                        xQualityTextRendered = FindVisualChildren<TextBlock>(qualityCombo)
                            .Any(text => text.Text == qualityLabel);

                        qualityCombo.IsDropDownOpen = true;
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        if (qualityCombo.Template.FindName("PART_Popup", qualityCombo) is Popup { Child: FrameworkElement qualityPopupChild })
                        {
                            var popupScrollViewer = FindVisualChildren<ScrollViewer>(qualityPopupChild).FirstOrDefault();
                            xQualityPopupNoHorizontalScroll = popupScrollViewer is not null
                                && popupScrollViewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                                && Math.Abs(qualityPopupChild.ActualWidth - qualityCombo.ActualWidth) < 1;
                        }

                        qualityCombo.IsDropDownOpen = false;
                    }
                }
            }

            tabs.SelectedIndex = 3;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            imageTabPresent = tabs.Items[3] is TabItem { Name: "ImageInfoTab" } imageTab && Equals(imageTab.Header, "图片信息");
            var imageScroll = (ScrollViewer)window.FindName("ImageInfoScrollViewer");
            var imageSample = new ImageInfoViewModel(_ => Task.FromResult(ImageMetadataChecks.Sample()), (_, _) => Task.FromResult(ImageGeocodingChecks.Sample()));
            imageSample.LoadAsync("sample.jpg").GetAwaiter().GetResult();
            imageSample.ResolveAddressAsync().GetAwaiter().GetResult();
            imageScroll.DataContext = imageSample;
            window.UpdateLayout();
            imagePageFits = imageScroll.ScrollableHeight < 0.5 && imageScroll.ScrollableWidth < 0.5;
            var imageValues = FindVisualChildren<TextBox>(imageScroll).Where(box => box.IsVisible).ToArray();
            imageFieldsReadOnly = imageValues.Length == 22 && imageValues.All(box => box.IsReadOnly);
            imagePageHasNoConversionControls = FindVisualChildren<Button>(window).Where(button => button.IsVisible)
                .All(button => Equals(button.Content, "选择图片") || Equals(button.Content, "解析地址"));
            imageValuesRendered = imageValues.Any(box => box.Text == "Example Camera")
                && imageValues.Any(box => box.Text == "南纬 33.500000°")
                && imageValues.Any(box => box.Text == "3000 × 4000 px")
                && imageValues.Any(box => box.Text.Contains("北京市"));
            window.MinHeight = 260;
            window.Height = 300;
            window.Width = 700;
            window.UpdateLayout();
            var imageWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = imageValues[0] };
            imageValues[0].RaiseEvent(imageWheel);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            imageWheelScrollsPage = imageWheel.Handled && imageScroll.VerticalOffset > 0 && imageScroll.ScrollableWidth < 0.5;
            window.Width = 780;
            window.Height = 540;

            bindingTrace.Flush();
            xBindingError = bindingListener.Text;
            xBindingsClean = !ContainsBindingError(xBindingError);

            tabs.SelectedIndex = 0;
            window.MinHeight = 260;
            window.Height = 260;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var textBox = FindVisualChildren<TextBox>(window).First();
            if (FindVisualParent<ScrollViewer>(textBox) is { } textBoxScrollViewer)
            {
                textBoxScrollViewer.ScrollToTop();
                var textBoxWheelEvent = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = textBox
                };
                textBox.RaiseEvent(textBoxWheelEvent);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                inputWheelScrollsPage = textBoxWheelEvent.Handled && textBoxScrollViewer.VerticalOffset > 0;
                textBoxScrollViewer.ScrollToTop();
            }

            var combo = FindVisualChildren<ComboBox>(window).First(item => item.Items.Count > 0);
            combo.ApplyTemplate();
            combo.SelectedIndex = 0;
            var selectionBeforeWheel = combo.SelectedIndex;
            var wheelEvent = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = combo
            };
            combo.RaiseEvent(wheelEvent);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            wheelSuppressed = wheelEvent.Handled && combo.SelectedIndex == selectionBeforeWheel;
            comboWheelScrollsPage = FindVisualParent<ScrollViewer>(combo)?.VerticalOffset > 0;

            combo.IsDropDownOpen = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement popupChild } popup)
            {
                var comboLeft = combo.PointToScreen(new Point()).X;
                var popupLeft = popupChild.PointToScreen(new Point()).X;
                popupAlignedAndEqualWidth = popup.PlacementTarget == combo
                    && Math.Abs(popupChild.ActualWidth - combo.ActualWidth) < 1
                    && Math.Abs(popupLeft - comboLeft) < 2;
            }

            combo.IsDropDownOpen = false;
            window.Close();
        }
        catch (Exception ex)
        {
            windowError = ex;
        }
        finally
        {
            bindingTrace.Listeners.Remove(bindingListener);
            bindingTrace.Switch.Level = previousBindingTraceLevel;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    Check(windowError is null, windowError is null ? "主窗口 XAML 与绑定初始化" : $"主窗口初始化：{windowError.Message}");
    Check(appIconPresent, "主窗口加载应用图标");
    Check(appNameMatches, "窗口标题和页面标题统一为 GIF Utils");
    Check(imageTabPresent, "第四页为图片信息");
    Check(imagePageFits, "图片信息页默认无需纵向或横向滚动");
    Check(imageFieldsReadOnly, "图片信息包含 19 个原始只读字段、2 个地址字段与路径");
    Check(imagePageHasNoConversionControls, "图片信息页不显示 FFmpeg 选择或转换按钮");
    Check(imageValuesRendered, "图片尺寸、拍摄信息、地理位置真实绑定到页面");
    Check(imageWheelScrollsPage, "缩小图片页面后输入框滚轮可滚动，无横向溢出");
    Check(compactWindow, "默认窗口尺寸为 780×540");
    Check(gifFitsWithoutScrolling, gifFitsWithoutScrolling ? "GIF 页面默认无需纵向滚动" : $"GIF 页面溢出 {gifScrollableHeight:0.##} px");
    Check(gifFitsWithoutHorizontalOverflow, gifFitsWithoutHorizontalOverflow ? "GIF 页面默认无横向溢出" : $"GIF 页面横向溢出 {gifScrollableWidth:0.##} px");
    Check(subtitleFitsWithoutScrolling, "字幕页面默认无需纵向滚动");
    Check(subtitleFitsWithoutHorizontalOverflow, subtitleFitsWithoutHorizontalOverflow ? "字幕页面默认无横向溢出" : $"字幕页面横向溢出 {subtitleScrollableWidth:0.##} px");
    Check(headerActionsMoved, "开始与取消操作位于顶部");
    Check(headerProgressMoved, "进度条位于顶部标题下方");
    Check(engineSelectorMoved, "FFmpeg 选择位于页面内容顶部");
    Check(engineSelectorsCentered, "三页的 FFmpeg 选择按钮在状态框内垂直居中");
    Check(mouseFocusCueHidden, "鼠标切换页面时按钮不显示焦点框");
    Check(keyboardFocusCueVisible, keyboardFocusCueVisible ? "键盘导航时保留可见焦点框" : $"键盘焦点框诊断：{keyboardFocusCueDiagnostic}");
    Check(emptyEngineErrorRowsCollapsed, "无 FFmpeg 错误时折叠空错误行并居中状态内容");
    Check(subtitleVideoEncoderSelectorPresent, "字幕页提供自动、CPU、NVIDIA、Intel、AMD 编码方式");
    Check(xTabPresent, "WPF 第三页为 X 下载");
    Check(xFitsWithoutScrolling, xFitsWithoutScrolling ? "X 页面默认无需页面纵向滚动" : $"X 页面溢出 {xScrollableHeight:0.##} px");
    Check(xFitsWithoutHorizontalOverflow, xFitsWithoutHorizontalOverflow ? "X 页面默认无横向溢出" : $"X 页面横向溢出 {xScrollableWidth:0.##} px");
    Check(xHeaderActionsPresent, "X 开始下载与取消操作位于顶部");
    Check(xHeaderProgressPresent, "X 下载进度位于顶部标题下方");
    Check(xEngineSelectorCentered, "X 页 FFmpeg 选择按钮在状态框内居中");
    Check(xMediaListOwnsScrolling, "X 媒体列表使用独立纵向滚动且禁用横向滚动");
    Check(xQualityTextRendered, "X 画质下拉框使用 DisplayName 模板而不是对象名称");
    Check(xQualityWidthSufficient, "X 画质下拉框可容纳完整画质说明");
    Check(xQualityPopupNoHorizontalScroll, "X 画质下拉选项等宽且不显示横向滚动条");
    Check(xBindingsClean, xBindingsClean ? "X 页绑定无运行时错误" : $"X 页绑定错误：{FirstLine(xBindingError)}");
    Check(wheelSuppressed, "关闭的下拉框忽略滚轮切换");
    Check(inputWheelScrollsPage, "输入框上滚轮可滚动页面");
    Check(comboWheelScrollsPage, "关闭的下拉框上滚轮可滚动页面");
    Check(popupAlignedAndEqualWidth, "下拉选项左对齐且与下拉框等宽");
}

void CheckNumericValidation()
{
    var viewModel = new MainViewModel { GifFrameRate = "aaa", SubtitleCrf = "invalid" };
    var gifValidator = typeof(MainViewModel).GetMethod("HasValidGifSettings", BindingFlags.Instance | BindingFlags.NonPublic);
    var subtitleValidator = typeof(MainViewModel).GetMethod("HasValidSubtitleSettings", BindingFlags.Instance | BindingFlags.NonPublic);
    var rejectsInvalidGifValue = gifValidator?.Invoke(viewModel, null) is false;
    var rejectsInvalidSubtitleValue = subtitleValidator?.Invoke(viewModel, null) is false;
    Check(viewModel.GifFrameRate == "aaa" && rejectsInvalidGifValue && rejectsInvalidSubtitleValue,
        "无效数字不会回退到旧值");
}

void CheckXUrlNormalization()
{
    var canonicalInput = "https://mobile.twitter.com/Some_User/status/1891234567890123456/video/1?utm_source=test#fragment";
    var normalized = XPostUrlService.TryNormalizeOfficialStatusUrl(canonicalInput, out var post, out var error);
    Check(normalized
          && error is null
          && post is not null
          && post.CanonicalUri.AbsoluteUri == "https://x.com/Some_User/status/1891234567890123456"
          && post.PostId == "1891234567890123456"
          && post.AccountName == "Some_User",
        "X URL 规范化并移除跟踪参数");

    var hostConfusionRejected = !XPostUrlService.TryNormalizeOfficialStatusUrl(
        "https://x.com.evil.example/alice/status/1234567890",
        out _,
        out var maliciousError)
        && !string.IsNullOrWhiteSpace(maliciousError);
    var userInfoRejected = !XPostUrlService.TryNormalizeOfficialStatusUrl(
        "https://x.com@evil.example/alice/status/1234567890",
        out _,
        out _);
    var nonDefaultPortRejected = !XPostUrlService.TryNormalizeOfficialStatusUrl(
        "https://x.com:8443/alice/status/1234567890",
        out _,
        out _);
    Check(hostConfusionRejected && userInfoRejected && nonDefaultPortRejected, "X URL 拒绝恶意域名、用户信息和非默认端口");

    var oversizedId = new string('9', 80);
    var oversizedIdAccepted = XPostUrlService.TryNormalizeOfficialStatusUrl(
        $"x.com/i/web/status/{oversizedId}",
        out var oversizedPost,
        out _)
        && oversizedPost?.PostId == oversizedId;
    Check(oversizedIdAccepted, "X 超大数字帖子 ID 不发生整数溢出");

    var spacesRejected = !XPostUrlService.TryNormalizeOfficialStatusUrl(
        "https://x.com/i/spaces/1YqKDqExample",
        out _,
        out var spacesError)
        && spacesError?.Contains("Spaces", StringComparison.OrdinalIgnoreCase) == true;
    Check(spacesRejected, "X Spaces 与直播链接被明确拒绝");
}

void CheckXMediaJsonParsing()
{
    const string postId = "1891234567890123456";
    var source = new XPostUrl(
        $"https://twitter.com/alice/status/{postId}",
        new Uri($"https://x.com/alice/status/{postId}"),
        postId,
        "alice");

    const string singleJson = """
        {
          "_type": "video",
          "id": "single-video",
          "display_id": "1891234567890123456",
          "extractor_key": "Twitter",
          "extractor": "twitter",
          "title": "Single video",
          "uploader_id": "alice",
          "duration": 12.5,
          "availability": "public",
          "formats": [
            {
              "format_id": "hls-1080",
              "url": "https://video.twimg.com/ext_tw_video/111/pu/pl/1080.m3u8",
              "protocol": "m3u8_native",
              "ext": "mp4",
              "vcodec": "h264",
              "acodec": "none",
              "audio_ext": "none",
              "width": 1920,
              "height": 1080,
              "tbr": 2200
            },
            {
              "format_id": "http-720",
              "url": "https://video.twimg.com/ext_tw_video/111/pu/vid/1280x720/720.mp4",
              "protocol": "https",
              "ext": "mp4",
              "vcodec": "h264",
              "acodec": "none",
              "audio_ext": "none",
              "width": 1280,
              "height": 720,
              "tbr": 900
            },
            {
              "format_id": "http-1080",
              "url": "https://video.twimg.com/ext_tw_video/111/pu/vid/1920x1080/1080.mp4",
              "protocol": "https",
              "ext": "mp4",
              "vcodec": "h264",
              "acodec": "none",
              "audio_ext": "none",
              "width": 1920,
              "height": 1080,
              "tbr": 1800
            },
            {
              "format_id": "audio-aac",
              "url": "https://video.twimg.com/ext_tw_video/111/pu/audio/audio.m4a",
              "protocol": "https",
              "ext": "m4a",
              "vcodec": "none",
              "acodec": "aac",
              "audio_ext": "m4a",
              "abr": 128
            }
          ]
        }
        """;

    var single = XMediaJsonParser.Parse(singleJson, source);
    Check(!single.IsPlaylist && single.Items.Count == 1 && single.Items[0].DurationSeconds == 12.5,
        "X 单媒体 JSON 解析");
    Check(single.Items[0].QualityOptions.Count == 2
          && single.Items[0].QualityOptions.All(option => !option.IsHls),
        "X 有 MP4 直链时不混入 HLS 画质");
    Check(single.Items[0].SelectedQuality == single.Items[0].QualityOptions[0]
          && single.Items[0].SelectedQuality.Height == 1080
          && single.Items[0].SelectedQuality.DisplayName.Contains("最高", StringComparison.Ordinal),
        "X 默认选择最高画质");
    Check(single.Items[0].QualityOptions.All(option => option.FormatSelector.EndsWith("+bestaudio/best", StringComparison.Ordinal)),
        "X 无音频视频格式选择器合并最佳音频并保留 best 回退");

    const string hlsOnlyJson = """
        {
          "id": "hls-video",
          "display_id": "1891234567890123456",
          "extractor_key": "Twitter",
          "extractor": "twitter",
          "title": "HLS only",
          "uploader_id": "alice",
          "availability": "public",
          "formats": [
            {
              "format_id": "hls-360",
              "url": "https://video.twimg.com/amplify_video/444/pl/360.m3u8",
              "protocol": "m3u8_native",
              "ext": "mp4",
              "vcodec": "h264",
              "acodec": "none",
              "audio_ext": "none",
              "width": 640,
              "height": 360,
              "tbr": 500
            },
            {
              "format_id": "hls-720",
              "url": "https://video.twimg.com/amplify_video/444/pl/720.m3u8",
              "protocol": "m3u8_native",
              "ext": "mp4",
              "vcodec": "h264",
              "acodec": "none",
              "audio_ext": "none",
              "width": 1280,
              "height": 720,
              "tbr": 1200
            }
          ]
        }
        """;
    var hlsOnly = XMediaJsonParser.Parse(hlsOnlyJson, source);
    Check(hlsOnly.Items[0].QualityOptions.Count == 2
          && hlsOnly.Items[0].QualityOptions.All(option => option.IsHls)
          && hlsOnly.Items[0].SelectedQuality.Height == 720,
        "X 无直链时回退 HLS 且仍默认最高画质");

    const string playlistJson = """
        {
          "_type": "playlist",
          "id": "1891234567890123456",
          "extractor_key": "Twitter",
          "extractor": "twitter",
          "title": "Playlist",
          "uploader_id": "alice",
          "availability": "public",
          "entries": [
            {
              "id": "top-video",
              "display_id": "1891234567890123456",
              "extractor_key": "Twitter",
              "extractor": "twitter",
              "uploader_id": "alice",
              "playlist_index": 1,
              "duration": 8,
              "formats": [
                { "format_id": "v1", "url": "https://video.twimg.com/ext_tw_video/111/pu/vid/720.mp4", "protocol": "https", "ext": "mp4", "vcodec": "h264", "acodec": "aac", "audio_ext": "m4a", "width": 1280, "height": 720, "tbr": 900 }
              ]
            },
            {
              "id": "top-gif",
              "display_id": "1891234567890123456",
              "extractor_key": "Twitter",
              "extractor": "twitter",
              "uploader_id": "alice",
              "playlist_index": 2,
              "duration": 3.5,
              "formats": [
                { "format_id": "gif1", "url": "https://video.twimg.com/tweet_video/333/gif.mp4", "protocol": "https", "ext": "mp4", "vcodec": "h264", "acodec": "none", "audio_ext": "none", "width": 480, "height": 270, "tbr": 300 }
              ]
            },
            {
              "id": "quoted-video",
              "display_id": "1891234567890123456",
              "extractor_key": "Twitter",
              "extractor": "twitter",
              "uploader_id": "quoted_account",
              "playlist_index": 3,
              "formats": [
                { "format_id": "q1", "url": "https://video.twimg.com/ext_tw_video/222/pu/vid/720.mp4", "protocol": "https", "ext": "mp4", "vcodec": "h264", "acodec": "aac", "audio_ext": "m4a", "width": 1280, "height": 720 }
              ]
            },
            {
              "id": "expanded-card",
              "display_id": "1891234567890123456",
              "extractor_key": "Youtube",
              "extractor": "youtube",
              "playlist_index": 4,
              "formats": [
                { "format_id": "yt", "url": "https://example.invalid/video.mp4", "protocol": "https", "ext": "mp4", "vcodec": "h264", "acodec": "aac", "audio_ext": "m4a", "width": 1920, "height": 1080 }
              ]
            }
          ]
        }
        """;
    var verifiedProbe = new XTopLevelMediaProbeResult(
        true,
        new Dictionary<string, XTopLevelMediaKind>
        {
            ["111"] = XTopLevelMediaKind.Video,
            ["333"] = XTopLevelMediaKind.AnimatedGif
        });
    var playlist = XMediaJsonParser.Parse(playlistJson, source, verifiedProbe);
    Check(playlist.IsPlaylist
          && playlist.Items.Count == 2
          && playlist.Items.Select(item => item.Id).SequenceEqual(["top-video", "top-gif"]),
        "X playlist 仅保留目标帖子顶层媒体并排除引用及外链条目");
    Check(playlist.Items[1].MediaTypeLabel == "动图（MP4）"
          && playlist.Items[1].Summary.Contains("动图", StringComparison.Ordinal),
        "X animated_gif 标注为循环 MP4 动图");
}

void CheckXFileNames()
{
    var safe = XFileNameHelper.SanitizeBaseName("  CON<>:\"/\\|?*  ");
    var reserved = XFileNameHelper.SanitizeBaseName("CON");
    var trailing = XFileNameHelper.SanitizeBaseName("clip.  ");
    var trimmed = XFileNameHelper.SanitizeBaseName(new string('a', 200));
    Check(safe.Length > 0
          && !safe.EndsWith('.')
          && !safe.EndsWith(' ')
          && safe.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
          && reserved == "_CON"
          && trailing == "clip"
          && trimmed.Length <= 120,
        "X 文件名前缀清理非法字符、保留名与过长输入");

    var directory = Path.Combine(Path.GetTempPath(), "FFmpegUtilsXFileNameSmoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllBytes(Path.Combine(directory, "X_alice_123_01.mp4"), [1]);
        Directory.CreateDirectory(Path.Combine(directory, "X_alice_123_01 (2).mp4"));
        var unique = XFileNameHelper.GetUniquePath(directory, "X_alice_123_01");
        Check(Path.GetFileName(unique) == "X_alice_123_01 (3).mp4" && !File.Exists(unique),
            "X 下载文件不覆盖现有文件或同名目录");

        var itemDirectory = Path.Combine(directory, "中文目录", "item-001");
        Directory.CreateDirectory(itemDirectory);
        var actualOutput = Path.Combine(itemDirectory, "video.mp4");
        File.WriteAllBytes(actualOutput, [1, 2, 3]);
        var garbledReportedPath = Path.Combine(directory, "����Ŀ¼", "item-001", "video.mp4");
        var resolver = typeof(XMediaDownloadService).GetMethod(
            "ResolveDownloadedPath",
            BindingFlags.Static | BindingFlags.NonPublic);
        var resolved = resolver?.Invoke(null, [garbledReportedPath, itemDirectory]) as string;
        Check(string.Equals(resolved, actualOutput, StringComparison.OrdinalIgnoreCase),
            "X 中文路径输出乱码时仅从隔离目录回退解析");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

void CheckXProgressParsing()
{
    var numericLine = XMediaDownloadService.ProgressPrefix + "1048576|2097152|NA|524288|2";
    var numeric = XMediaDownloadService.TryParseProgressLine(numericLine, out var numericProgress);
    Check(numeric
          && Math.Abs(numericProgress.Percent - 50) < 0.001
          && numericProgress.DownloadedBytes == 1_048_576
          && numericProgress.TotalBytes == 2_097_152
          && numericProgress.BytesPerSecond == 524_288
          && numericProgress.Detail.Contains("/s", StringComparison.Ordinal),
        "X 下载进度 sentinel 数字解析");

    var estimateLine = XMediaDownloadService.ProgressPrefix + "512|null|1024|NA|NA";
    var estimate = XMediaDownloadService.TryParseProgressLine(estimateLine, out var estimateProgress);
    Check(estimate && estimateProgress.TotalBytes == 1024 && Math.Abs(estimateProgress.Percent - 50) < 0.001,
        "X 下载进度缺少精确总量时使用估算值");

    var nullLine = XMediaDownloadService.ProgressPrefix + "NA|null|NA|null|NA";
    var nullSentinels = XMediaDownloadService.TryParseProgressLine(nullLine, out var nullProgress);
    Check(nullSentinels
          && nullProgress.Percent == 0
          && nullProgress.DownloadedBytes is null
          && nullProgress.TotalBytes is null
          && nullProgress.BytesPerSecond is null,
        "X 下载进度 null/NA sentinel 安全降级");
    Check(!XMediaDownloadService.TryParseProgressLine("ordinary yt-dlp output", out _),
        "X 下载进度忽略非 sentinel 输出");
}

void CheckXFriendlyErrors()
{
    var classifications = new (string Details, bool DuringDownload, XDownloadErrorKind Kind)[]
    {
        ("This tweet is private and login required", false, XDownloadErrorKind.AuthenticationRequired),
        ("This post is age-restricted", false, XDownloadErrorKind.AgeRestricted),
        ("Geo restricted: not available in your country", false, XDownloadErrorKind.GeoRestricted),
        ("HTTP Error 429: Too Many Requests", false, XDownloadErrorKind.RateLimited),
        ("No formats found", false, XDownloadErrorKind.NoVideo),
        ("HTTP Error 404: tweet deleted", false, XDownloadErrorKind.Unavailable),
        ("Postprocessing: ffmpeg not found", true, XDownloadErrorKind.FfmpegMissing),
        ("Unable to download: connection timed out", true, XDownloadErrorKind.Network),
        ("unexpected extractor response", false, XDownloadErrorKind.ParseFailed),
        ("unexpected downloader response", true, XDownloadErrorKind.DownloadFailed)
    };
    var mismatches = classifications
        .Select(test => (Test: test, Actual: XMediaDownloadService.CreateFriendlyException(test.Details, test.DuringDownload).Kind))
        .Where(result => result.Actual != result.Test.Kind)
        .Select(result => $"{result.Test.Kind}->{result.Actual} ({result.Test.Details})")
        .ToArray();
    Check(mismatches.Length == 0,
        mismatches.Length == 0
            ? "X 解析与下载错误映射为友好分类"
            : $"X 错误分类不匹配：{string.Join(", ", mismatches)}");

    var longDetail = new string('x', 7000) + " connection timed out";
    var friendly = XMediaDownloadService.CreateFriendlyException(longDetail, duringDownload: true);
    Check(friendly.Kind == XDownloadErrorKind.Network && friendly.Details.Length <= 6000,
        "X 技术错误详情截断且保留友好消息");
}

IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is T match)
        {
            yield return match;
        }

        foreach (var descendant in FindVisualChildren<T>(child))
        {
            yield return descendant;
        }
    }
}

T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
{
    for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
    {
        if (current is T match)
        {
            return match;
        }
    }

    return null;
}

bool ContainsBindingError(string trace)
    => trace.Contains("System.Windows.Data Error", StringComparison.OrdinalIgnoreCase)
       || trace.Contains("BindingExpression path error", StringComparison.OrdinalIgnoreCase)
       || trace.Contains("Cannot find source for binding", StringComparison.OrdinalIgnoreCase)
       || trace.Contains("property not found", StringComparison.OrdinalIgnoreCase)
       || trace.Contains("cannot retrieve value", StringComparison.OrdinalIgnoreCase);

string FirstLine(string value)
{
    var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未知绑定错误";
    return line.Length <= 240 ? line : line[..240] + "…";
}

async Task<int> RunXParseIntegrationAsync(string url)
{
    try
    {
        Console.WriteLine("X_PARSE_INTEGRATION_START");
        var progress = new SynchronousProgress<XDownloadProgress>(value =>
            Console.WriteLine($"PROGRESS {value.Percent:0.#}% | {value.Stage} | {value.Detail}"));
        var result = await new XMediaDownloadService().ParseAsync(url, progress, CancellationToken.None);
        PrintXParseResult(result);
        Console.WriteLine("X_PARSE_INTEGRATION_OK");
        return 0;
    }
    catch (XDownloadException exception)
    {
        Console.Error.WriteLine($"X_PARSE_INTEGRATION_FAILED [{exception.Kind}]: {exception.Message}");
        if (!string.IsNullOrWhiteSpace(exception.Details))
        {
            Console.Error.WriteLine(exception.Details);
        }

        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"X_PARSE_INTEGRATION_FAILED: {exception}");
        return 1;
    }
}

async Task<int> RunXDownloadIntegrationAsync(string url, string outputDirectory, string ffmpegPath)
{
    try
    {
        Console.WriteLine("X_DOWNLOAD_INTEGRATION_START");
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var progress = new SynchronousProgress<XDownloadProgress>(value =>
            Console.WriteLine($"PROGRESS {value.Percent:0.#}% | {value.Stage} | {value.Detail}"));
        var service = new XMediaDownloadService();
        var parsed = await service.ParseAsync(url, progress, CancellationToken.None);
        PrintXParseResult(parsed);
        var first = parsed.Items.First();
        foreach (var item in parsed.Items)
        {
            item.IsSelected = ReferenceEquals(item, first);
        }

        first.SelectedQuality = first.QualityOptions[0];
        Console.WriteLine($"SELECTED {first.DisplayName} | {first.SelectedQuality.DisplayName} | {first.SelectedQuality.FormatSelector}");
        var downloaded = await service.DownloadAsync(
            parsed,
            [first],
            outputDirectory,
            parsed.SuggestedPrefix,
            ffmpegPath,
            progress,
            CancellationToken.None);
        var outputPath = downloaded.OutputPaths.SingleOrDefault();
        if (outputPath is null
            || !Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(outputPath)
            || new FileInfo(outputPath).Length <= 0)
        {
            Console.Error.WriteLine("X_DOWNLOAD_INTEGRATION_FAILED: 未生成非空 MP4。文件将保留供检查。");
            return 1;
        }

        Console.WriteLine($"OUTPUT {outputPath}");
        Console.WriteLine($"BYTES {new FileInfo(outputPath).Length}");
        Console.WriteLine("X_DOWNLOAD_INTEGRATION_OK");
        return 0;
    }
    catch (XDownloadException exception)
    {
        Console.Error.WriteLine($"X_DOWNLOAD_INTEGRATION_FAILED [{exception.Kind}]: {exception.Message}");
        if (!string.IsNullOrWhiteSpace(exception.Details))
        {
            Console.Error.WriteLine(exception.Details);
        }

        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"X_DOWNLOAD_INTEGRATION_FAILED: {exception}");
        return 1;
    }
}

void PrintXParseResult(XParseResult result)
{
    Console.WriteLine($"CANONICAL {result.Source.CanonicalUri.AbsoluteUri}");
    Console.WriteLine($"TITLE {result.Title}");
    Console.WriteLine($"ITEMS {result.Items.Count}");
    if (!string.IsNullOrWhiteSpace(result.Warning))
    {
        Console.WriteLine($"WARNING {result.Warning}");
    }

    foreach (var item in result.Items)
    {
        Console.WriteLine($"ITEM {item.Index} | playlist={item.PlaylistIndex} | {item.MediaTypeLabel} | {item.Summary} | id={item.Id}");
        foreach (var quality in item.QualityOptions)
        {
            Console.WriteLine($"QUALITY {item.Index} | {quality.DisplayName} | selector={quality.FormatSelector}");
        }
    }
}

int RenderXPage(string screenshotPath, bool includeQualitySample = false, bool includeImageInfoSample = false)
{
    Exception? renderError = null;
    var savedPath = Path.GetFullPath(screenshotPath);
    var thread = new Thread(() =>
    {
        FFmpegUtils.MainWindow? window = null;
        try
        {
            var directory = Path.GetDirectoryName(savedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            window = new FFmpegUtils.MainWindow
            {
                Width = 780,
                Height = 540,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0
            };
            window.Show();
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedIndex = includeImageInfoSample ? 3 : 2;
            if (includeImageInfoSample)
            {
                var imageViewModel = new ImageInfoViewModel(_ => Task.FromResult(ImageGeocodingChecks.PhotoSample()), (_, _) => Task.FromResult(ImageGeocodingChecks.Sample()));
                imageViewModel.LoadAsync(@"C:\示例图片\拍摄信息示例.jpg").GetAwaiter().GetResult();
                imageViewModel.ResolveAddressAsync().GetAwaiter().GetResult();
                ((ScrollViewer)window.FindName("ImageInfoScrollViewer")).DataContext = imageViewModel;
                ((TextBlock)window.FindName("ImageInfoStatusText")).Text = imageViewModel.Status;
            }
            if (includeQualitySample && window.DataContext is MainViewModel viewModel)
            {
                var quality = new XQualityOption(
                    "720×814 · 2176 kbps · MP4 直链（最高）",
                    "http-2176+bestaudio/best",
                    720,
                    814,
                    2176,
                    false,
                    true,
                    "http-2176");
                viewModel.XMediaItems.Add(new XMediaItem(1, 1, "sample", "媒体 1", "", "视频", 6, [quality]));
            }
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var dpi = VisualTreeHelper.GetDpi(window);
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(savedPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
        }
        catch (Exception exception)
        {
            renderError = exception;
        }
        finally
        {
            window?.Close();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (renderError is not null)
    {
        Console.Error.WriteLine($"X_RENDER_FAILED: {renderError}");
        return 1;
    }

    if (!File.Exists(savedPath) || new FileInfo(savedPath).Length <= 0)
    {
        Console.Error.WriteLine("X_RENDER_FAILED: 未生成 PNG。\n");
        return 1;
    }

    Console.WriteLine($"X_RENDER_OK {savedPath}");
    return 0;
}

async Task RunIntegrationAsync(string ffmpegPath, string inputVideo)
{
    var locator = new FfmpegLocator();
    var installation = await locator.InspectAsync(ffmpegPath);
    Check(installation.HasGifFilters, "FFmpeg GIF 滤镜可用");
    Check(installation.HasSubtitleFilter, "FFmpeg 字幕滤镜可用");

    var media = await new MediaProbeService().ProbeAsync(installation.FfprobePath, inputVideo);
    Check(media.DurationSeconds > 0 && media.Width > 0, "FFprobe 媒体信息");

    var integrationRoot = Path.Combine(Path.GetTempPath(), "FFmpegUtilsSmoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(integrationRoot);
    try
    {
        var runner = new FfmpegProcessRunner();
        var gifPath = Path.Combine(integrationRoot, "smoke.gif");
        var gifOptions = new GifConversionOptions(inputVideo, gifPath, Math.Min(480, media.Width), 15, 192, "bayer", 0.08, 0, Math.Min(1.5, media.DurationSeconds));
        var gif = await new GifConversionService(runner).ConvertAsync(installation, media, gifOptions, null, CancellationToken.None);
        Check(File.Exists(gif.OutputPath) && gif.FileSizeBytes > 0, "实际 GIF 转换");
        Check(gif.Attempts >= 2, "目标大小多轮压缩");

        var subtitlePath = Path.Combine(integrationRoot, "中文字幕.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,000 --> 00:00:01,000\nGIF Utils 测试\n", new UTF8Encoding(false));
        var burnedPath = Path.Combine(integrationRoot, "burned.mp4");
        var burned = await new SubtitleBurnService(runner).BurnAsync(
            installation,
            media,
            new SubtitleBurnOptions(inputVideo, subtitlePath, burnedPath, "自动", 24, "veryfast", SubtitleVideoEncoder.Auto),
            null,
            CancellationToken.None);
        Check(File.Exists(burned.OutputPath) && burned.FileSizeBytes > 0, "实际字幕烧录");
        Check(!string.IsNullOrWhiteSpace(burned.EncoderName)
              && burned.EncoderName != SubtitleVideoEncoderCatalog.AutoDisplayName,
            $"自动编码器解析为 {burned.EncoderName ?? "未知"}");
    }
    finally
    {
        if (Directory.Exists(integrationRoot)) Directory.Delete(integrationRoot, recursive: true);
    }
}

sealed class CollectingTraceListener : TraceListener
{
    private readonly StringBuilder _builder = new();

    public string Text
    {
        get
        {
            lock (_builder)
            {
                return _builder.ToString();
            }
        }
    }

    public override void Write(string? message)
    {
        lock (_builder)
        {
            _builder.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_builder)
        {
            _builder.AppendLine(message);
        }
    }
}

sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
