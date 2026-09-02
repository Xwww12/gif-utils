using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFmpegUtils.ViewModels;
using FFmpegUtils.Services;
using Microsoft.Win32;

namespace FFmpegUtils;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty ShowKeyboardFocusCuesProperty = DependencyProperty.RegisterAttached(
        nameof(ShowKeyboardFocusCues),
        typeof(bool),
        typeof(MainWindow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".ts", ".mts", ".m4v"];
    private readonly MainViewModel _viewModel = new();

    public bool ShowKeyboardFocusCues
    {
        get => GetShowKeyboardFocusCues(this);
        private set => SetShowKeyboardFocusCues(this, value);
    }

    public static bool GetShowKeyboardFocusCues(DependencyObject element)
        => (bool)element.GetValue(ShowKeyboardFocusCuesProperty);

    public static void SetShowKeyboardFocusCues(DependencyObject element, bool value)
        => element.SetValue(ShowKeyboardFocusCuesProperty, value);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        ApplyTheme();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
        => await _viewModel.InitializeAsync();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        => ShowKeyboardFocusCues = true;

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => ShowKeyboardFocusCues = false;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.HasActiveJob)
        {
            _viewModel.ImageInfo.CancelAddressLookup();
            _viewModel.GifTrim.SetActive(false);
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
            return;
        }

        var result = MessageBox.Show(
            "转换仍在进行。关闭窗口会取消当前任务，确定要关闭吗？",
            "GIF Utils",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }

        _viewModel.CancelGif();
        _viewModel.CancelSubtitle();
        _viewModel.CancelXDownload();
        _viewModel.ImageInfo.CancelAddressLookup();
        _viewModel.GifTrim.SetActive(false);
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
    }

    private async void SelectFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "FFmpeg 可执行文件|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SelectFfmpegAsync(dialog.FileName);
        }
    }

    private async void BrowseGifInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 MP4 文件",
            Filter = "MP4 视频|*.mp4",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SetGifInputAsync(dialog.FileName);
        }
    }

    private void GifTrimExpander_Expanded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!GifTrimExpander.IsExpanded) return;
            GifScrollViewer.UpdateLayout();
            var top = GifTrimExpander.TranslatePoint(new Point(0, 0), GifScrollViewer).Y;
            GifScrollViewer.ScrollToVerticalOffset(Math.Max(0, GifScrollViewer.VerticalOffset + top));
        }));
    }

    private async void BrowseImageInfo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.tif;*.tiff;*.bmp;*.gif;*.heic;*.heif;*.avif",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.ImageInfo.LoadAsync(dialog.FileName);
    }

    private void ImageInfo_DragOver(object sender, DragEventArgs e)
    {
        var accepted = _viewModel.ImageInfo.CanSelect
            && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths
            && File.Exists(paths[0]) && ImageMetadataService.IsSupportedPath(paths[0]);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ResolveImageAddress_Click(object sender, RoutedEventArgs e)
    {
        var imageInfo = _viewModel.ImageInfo;
        if (imageInfo.IsResolving)
        {
            imageInfo.CancelAddressLookup();
            return;
        }
        if (!imageInfo.CanResolve) return;
        var answer = MessageBox.Show(this,
            "将把这张图片的经纬度发送到 Photon（photon.komoot.io）查询城市和附近地址。\n\n"
            + "不会上传图片、文件名或拍摄信息。坐标可能暴露拍摄位置，请勿查询不愿分享的位置。\n\n"
            + "地图匹配不保证是准确拍摄地点。是否继续？",
            "联网解析地址", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await imageInfo.ResolveAddressAsync();
    }

    private void OpenMapAttribution_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.openstreetmap.org/copyright") { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "无法打开浏览器。地图数据许可：https://www.openstreetmap.org/copyright", "地图数据来源"); }
    }

    private async void ImageInfo_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.ImageInfo.CanSelect && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
            await _viewModel.ImageInfo.LoadAsync(paths[0]);
    }

    private void BrowseGifOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 GIF",
            Filter = "GIF 动画|*.gif",
            AddExtension = true,
            DefaultExt = ".gif",
            FileName = SuggestedName(_viewModel.GifOutputPath, "output.gif")
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.GifOutputPath = dialog.FileName;
        }
    }

    private async void StartGif_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmOverwrite(_viewModel.GifOutputPath))
        {
            return;
        }

        await _viewModel.ConvertGifAsync();
    }

    private void CancelGif_Click(object sender, RoutedEventArgs e) => _viewModel.CancelGif();

    private async void BrowseSubtitleVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.ts;*.mts;*.m4v|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SetSubtitleVideoAsync(dialog.FileName);
        }
    }

    private void BrowseSubtitleFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetSubtitleFile(dialog.FileName);
        }
    }

    private void BrowseSubtitleOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存烧录后的视频",
            Filter = "MP4 视频|*.mp4|Matroska 视频|*.mkv",
            AddExtension = true,
            DefaultExt = ".mp4",
            FileName = SuggestedName(_viewModel.SubtitleOutputPath, "output_subtitled.mp4")
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SubtitleOutputPath = dialog.FileName;
        }
    }

    private async void StartSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmOverwrite(_viewModel.SubtitleOutputPath))
        {
            return;
        }

        await _viewModel.BurnSubtitlesAsync();
    }

    private void CancelSubtitle_Click(object sender, RoutedEventArgs e) => _viewModel.CancelSubtitle();

    private void PasteXUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Text))
            {
                _viewModel.XUrl = Clipboard.GetText(TextDataFormat.Text).Trim();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法读取剪贴板：{ex.Message}", "GIF Utils", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ParseXUrl_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ParseXUrlAsync();

    private void BrowseXOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 X 视频保存目录",
            Multiselect = false
        };
        if (Directory.Exists(_viewModel.XOutputDirectory))
        {
            dialog.InitialDirectory = _viewModel.XOutputDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.XOutputDirectory = dialog.FolderName;
        }
    }

    private async void StartXDownload_Click(object sender, RoutedEventArgs e)
        => await _viewModel.DownloadXMediaAsync();

    private void CancelXDownload_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelXDownload();

    private void OpenXOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var directory = _viewModel.XLastOutputDirectory;
        if (!Directory.Exists(directory))
        {
            MessageBox.Show("保存目录不存在或已被移动。", "GIF Utils", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开保存目录：{ex.Message}", "GIF Utils", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void IntegerInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(character => character is < '0' or > '9');

    private void IntegerInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            || e.DataObject.GetData(DataFormats.UnicodeText) is not string text
            || text.Any(character => character is < '0' or > '9'))
        {
            e.CancelCommand();
        }
    }

    private void ScrollableControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (sender is DependencyObject source && FindVisualAncestor<ScrollViewer>(source) is { } scrollViewer)
        {
            var targetOffset = Math.Clamp(scrollViewer.VerticalOffset - e.Delta / 3.0, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
            return;
        }

        e.Handled = sender is ComboBox;
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(source); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void GifInput_Drop(object sender, DragEventArgs e)
    {
        if (FirstDroppedFile(e, extension => extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) is { } path)
        {
            await _viewModel.SetGifInputAsync(path);
        }
    }

    private async void SubtitleVideo_Drop(object sender, DragEventArgs e)
    {
        if (FirstDroppedFile(e, extension => VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) is { } path)
        {
            await _viewModel.SetSubtitleVideoAsync(path);
        }
    }

    private void SubtitleFile_Drop(object sender, DragEventArgs e)
    {
        if (FirstDroppedFile(e, extension => new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) is { } path)
        {
            _viewModel.SetSubtitleFile(path);
        }
    }

    private static string? FirstDroppedFile(DragEventArgs e, Func<string, bool> acceptsExtension)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(path => File.Exists(path) && acceptsExtension(Path.GetExtension(path)));
    }

    private bool ConfirmOverwrite(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        return MessageBox.Show(
            $"文件已存在：\n{path}\n\n转换成功后将替换该文件，是否继续？",
            "确认覆盖",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static string SuggestedName(string currentPath, string fallback)
        => string.IsNullOrWhiteSpace(currentPath) ? fallback : Path.GetFileName(currentPath);

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatcher.Invoke(ApplyTheme);
        }
    }

    private void ApplyTheme()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["BackgroundBrush"] = SystemColors.WindowBrush;
            Resources["SurfaceBrush"] = SystemColors.ControlBrush;
            Resources["SurfaceRaisedBrush"] = SystemColors.ControlLightBrush;
            Resources["ControlBrush"] = SystemColors.WindowBrush;
            Resources["ControlHoverBrush"] = SystemColors.ControlLightBrush;
            Resources["ControlPressedBrush"] = SystemColors.HighlightBrush;
            Resources["DisabledBrush"] = SystemColors.ControlBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedTextBrush"] = SystemColors.ControlTextBrush;
            Resources["BorderBrush"] = SystemColors.ActiveBorderBrush;
            Resources["StrongBorderBrush"] = SystemColors.WindowTextBrush;
            Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["AccentHoverBrush"] = SystemColors.HotTrackBrush;
            Resources["FocusBrush"] = SystemColors.HighlightBrush;
            Resources["SelectionBrush"] = SystemColors.HighlightBrush;
            Resources["ErrorBrush"] = SystemColors.WindowTextBrush;
            Resources["ToolTipBrush"] = SystemColors.InfoBrush;
            Resources["ScrollThumbBrush"] = SystemColors.ControlDarkBrush;
            Resources["ScrollThumbHoverBrush"] = SystemColors.HighlightBrush;
            return;
        }

        Resources["BackgroundBrush"] = BrushFrom("#F0F0F0");
        Resources["SurfaceBrush"] = BrushFrom("#F7F7F7");
        Resources["SurfaceRaisedBrush"] = BrushFrom("#E1E1E1");
        Resources["ControlBrush"] = BrushFrom("#FFFFFF");
        Resources["ControlHoverBrush"] = BrushFrom("#E5F1FB");
        Resources["ControlPressedBrush"] = BrushFrom("#CCE4F7");
        Resources["DisabledBrush"] = BrushFrom("#EBEBEB");
        Resources["TextBrush"] = BrushFrom("#111111");
        Resources["MutedTextBrush"] = BrushFrom("#555555");
        Resources["BorderBrush"] = BrushFrom("#C7C7C7");
        Resources["StrongBorderBrush"] = BrushFrom("#8A8A8A");
        Resources["AccentBrush"] = BrushFrom("#0078D4");
        Resources["AccentHoverBrush"] = BrushFrom("#005A9E");
        Resources["FocusBrush"] = BrushFrom("#005FB8");
        Resources["SelectionBrush"] = BrushFrom("#0078D7");
        Resources["ErrorBrush"] = BrushFrom("#C42B1C");
        Resources["ToolTipBrush"] = BrushFrom("#FFFFE1");
        Resources["ScrollThumbBrush"] = BrushFrom("#C5C5C5");
        Resources["ScrollThumbHoverBrush"] = BrushFrom("#A6A6A6");
    }

    private static SolidColorBrush BrushFrom(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
