using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using FFmpegUtils.Infrastructure;
using FFmpegUtils.Models;
using FFmpegUtils.Services;

namespace FFmpegUtils.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public ImageInfoViewModel ImageInfo { get; } = new();
    public GifTrimViewModel GifTrim { get; } = new();

    public MainViewModel()
    {
        GifTrim.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(GifTrimViewModel.StartText) or nameof(GifTrimViewModel.EndText) or nameof(GifTrimViewModel.RangeError))
            {
                OnPropertiesChanged(nameof(GifStartTimeText), nameof(GifEndTimeText));
                RaiseReadyStates();
            }
        };
    }

    private readonly AppSettingsService _settingsService = new();
    private readonly FfmpegLocator _locator = new();
    private readonly MediaProbeService _probe = new();
    private readonly GifConversionService _gifService = new(new FfmpegProcessRunner());
    private readonly SubtitleBurnService _subtitleService = new(new FfmpegProcessRunner());
    private readonly XMediaDownloadService _xDownloadService = new();

    private FfmpegInstallation? _installation;
    private MediaInfo? _gifMedia;
    private int _gifProbeVersion;
    private MediaInfo? _subtitleMedia;
    private CancellationTokenSource? _gifCancellation;
    private CancellationTokenSource? _subtitleCancellation;
    private CancellationTokenSource? _xCancellation;

    private string _engineStatus = "正在查找 FFmpeg…";
    private string _enginePath = string.Empty;
    private string _engineVersion = string.Empty;
    private string _engineError = string.Empty;

    private string _gifInputPath = string.Empty;
    private string _gifOutputPath = string.Empty;
    private string _gifMediaSummary = "未选择 MP4";
    private string _selectedGifPreset = "均衡";
    private string _gifMaxWidth = "720";
    private string _gifFrameRate = "15";
    private string _gifColors = "192";
    private string _selectedDither = "平滑（推荐）";
    private string _gifTargetSizeText = "5";
    private double _gifProgress;
    private string _gifStatus = "等待开始";
    private string _gifDetail = string.Empty;
    private string _gifError = string.Empty;
    private bool _isGifRunning;

    private string _subtitleVideoPath = string.Empty;
    private string _subtitleFilePath = string.Empty;
    private string _subtitleOutputPath = string.Empty;
    private string _subtitleMediaSummary = "未选择视频";
    private string _selectedSubtitleVideoEncoder = SubtitleVideoEncoderCatalog.AutoDisplayName;
    private string _selectedSubtitleEncoding = "自动";
    private string _subtitleCrf = "20";
    private double _subtitleProgress;
    private string _subtitleStatus = "等待开始";
    private string _subtitleDetail = string.Empty;
    private string _subtitleError = string.Empty;
    private bool _isSubtitleRunning;

    private XParseResult? _xParseResult;
    private readonly ObservableCollection<XMediaItem> _xMediaItems = [];
    private string _xUrl = string.Empty;
    private string _xParsedSourceText = string.Empty;
    private string _xOutputDirectory = GetDefaultXOutputDirectory();
    private string _xFilePrefix = string.Empty;
    private double _xDownloadProgress;
    private string _xDownloadStatus = "等待解析";
    private string _xDownloadDetail = string.Empty;
    private string _xWarning = string.Empty;
    private string _xError = string.Empty;
    private bool _isXBusy;
    private bool _isXDownloading;
    private bool _selectAllXMedia;
    private bool _updatingXSelection;
    private string _xLastOutputDirectory = string.Empty;
    private string _xResultSummary = string.Empty;

    public IReadOnlyList<string> GifPresetNames { get; } = ["高清", "均衡", "小体积", "极小", "指定目标大小", "自定义"];
    public IReadOnlyList<string> DitherOptions { get; } = ["平滑（推荐）", "规整（更小）", "无抖动"];
    public IReadOnlyList<string> SubtitleVideoEncoderOptions { get; } = SubtitleVideoEncoderCatalog.DisplayNames;
    public IReadOnlyList<string> SubtitleEncodingOptions { get; } = ["自动", "UTF-8", "GB18030"];

    public string EngineStatus { get => _engineStatus; private set => SetProperty(ref _engineStatus, value); }
    public string EnginePath { get => _enginePath; private set => SetProperty(ref _enginePath, value); }
    public string EngineVersion { get => _engineVersion; private set => SetProperty(ref _engineVersion, value); }
    public string EngineError { get => _engineError; private set => SetProperty(ref _engineError, value); }
    public bool EngineReady => _installation?.IsReady == true;

    public string GifInputPath { get => _gifInputPath; private set { if (SetProperty(ref _gifInputPath, value)) RaiseReadyStates(); } }
    public string GifOutputPath { get => _gifOutputPath; set { if (SetProperty(ref _gifOutputPath, value)) RaiseReadyStates(); } }
    public string GifMediaSummary { get => _gifMediaSummary; private set => SetProperty(ref _gifMediaSummary, value); }
    public string GifMaxWidth { get => _gifMaxWidth; set { if (SetProperty(ref _gifMaxWidth, value)) RaiseReadyStates(); } }
    public string GifFrameRate { get => _gifFrameRate; set { if (SetProperty(ref _gifFrameRate, value)) RaiseReadyStates(); } }
    public string GifColors { get => _gifColors; set { if (SetProperty(ref _gifColors, value)) RaiseReadyStates(); } }
    public string SelectedDither { get => _selectedDither; set => SetProperty(ref _selectedDither, value); }
    public string GifTargetSizeText { get => _gifTargetSizeText; set { if (SetProperty(ref _gifTargetSizeText, value)) RaiseReadyStates(); } }
    public string GifStartTimeText { get => GifTrim.StartText; set => GifTrim.StartText = value; }
    public string GifEndTimeText { get => GifTrim.EndText; set => GifTrim.EndText = value; }
    public double GifProgress { get => _gifProgress; private set => SetProperty(ref _gifProgress, value); }
    public string GifStatus { get => _gifStatus; private set => SetProperty(ref _gifStatus, value); }
    public string GifDetail { get => _gifDetail; private set => SetProperty(ref _gifDetail, value); }
    public string GifError { get => _gifError; private set => SetProperty(ref _gifError, value); }
    public bool CanChangeGifSource => !IsGifRunning;
    public bool IsGifRunning { get => _isGifRunning; private set { if (SetProperty(ref _isGifRunning, value)) { GifTrim.SetEnabled(!value); OnPropertyChanged(nameof(CanChangeGifSource)); RaiseReadyStates(); } } }

    public string SelectedGifPreset
    {
        get => _selectedGifPreset;
        set
        {
            if (SetProperty(ref _selectedGifPreset, value))
            {
                ApplyGifPreset(value);
                OnPropertyChanged(nameof(IsTargetSizeMode));
                RaiseReadyStates();
            }
        }
    }

    public bool IsTargetSizeMode => SelectedGifPreset == "指定目标大小";
    public bool IsGifReady => EngineReady && _installation?.HasGifFilters == true && _gifMedia is not null
                              && File.Exists(GifInputPath) && !string.IsNullOrWhiteSpace(GifOutputPath)
                              && HasValidGifSettings() && !IsGifRunning;

    public string SubtitleVideoPath { get => _subtitleVideoPath; private set { if (SetProperty(ref _subtitleVideoPath, value)) RaiseReadyStates(); } }
    public string SubtitleFilePath { get => _subtitleFilePath; private set { if (SetProperty(ref _subtitleFilePath, value)) RaiseReadyStates(); } }
    public string SubtitleOutputPath { get => _subtitleOutputPath; set { if (SetProperty(ref _subtitleOutputPath, value)) RaiseReadyStates(); } }
    public string SubtitleMediaSummary { get => _subtitleMediaSummary; private set => SetProperty(ref _subtitleMediaSummary, value); }
    public string SelectedSubtitleVideoEncoder { get => _selectedSubtitleVideoEncoder; set { if (SetProperty(ref _selectedSubtitleVideoEncoder, value)) RaiseReadyStates(); } }
    public string SelectedSubtitleEncoding { get => _selectedSubtitleEncoding; set => SetProperty(ref _selectedSubtitleEncoding, value); }
    public string SubtitleCrf { get => _subtitleCrf; set { if (SetProperty(ref _subtitleCrf, value)) RaiseReadyStates(); } }
    public double SubtitleProgress { get => _subtitleProgress; private set => SetProperty(ref _subtitleProgress, value); }
    public string SubtitleStatus { get => _subtitleStatus; private set => SetProperty(ref _subtitleStatus, value); }
    public string SubtitleDetail { get => _subtitleDetail; private set => SetProperty(ref _subtitleDetail, value); }
    public string SubtitleError { get => _subtitleError; private set => SetProperty(ref _subtitleError, value); }
    public bool IsSubtitleRunning { get => _isSubtitleRunning; private set { if (SetProperty(ref _isSubtitleRunning, value)) RaiseReadyStates(); } }
    public bool IsSubtitleReady => EngineReady && _installation?.HasSubtitleFilter == true && _subtitleMedia is not null
                                   && File.Exists(SubtitleVideoPath) && File.Exists(SubtitleFilePath)
                                   && !string.IsNullOrWhiteSpace(SubtitleOutputPath)
                                   && HasValidSubtitleSettings() && !IsSubtitleRunning;

    public ObservableCollection<XMediaItem> XMediaItems => _xMediaItems;
    public string XUrl
    {
        get => _xUrl;
        set
        {
            if (!SetProperty(ref _xUrl, value))
            {
                return;
            }

            if (_xParseResult is not null
                && !string.Equals(value.Trim(), _xParsedSourceText, StringComparison.OrdinalIgnoreCase))
            {
                ClearXMedia();
                _xParseResult = null;
                _xParsedSourceText = string.Empty;
                XDownloadStatus = "链接已更改，请重新解析";
            }

            RaiseReadyStates();
        }
    }

    public string XOutputDirectory { get => _xOutputDirectory; set { if (SetProperty(ref _xOutputDirectory, value)) RaiseReadyStates(); } }
    public string XFilePrefix { get => _xFilePrefix; set { if (SetProperty(ref _xFilePrefix, value)) RaiseReadyStates(); } }
    public double XDownloadProgress { get => _xDownloadProgress; private set => SetProperty(ref _xDownloadProgress, value); }
    public string XDownloadStatus { get => _xDownloadStatus; private set => SetProperty(ref _xDownloadStatus, value); }
    public string XDownloadDetail { get => _xDownloadDetail; private set => SetProperty(ref _xDownloadDetail, value); }
    public string XWarning { get => _xWarning; private set => SetProperty(ref _xWarning, value); }
    public string XError { get => _xError; private set => SetProperty(ref _xError, value); }
    public string XResultSummary { get => _xResultSummary; private set => SetProperty(ref _xResultSummary, value); }
    public bool IsXBusy { get => _isXBusy; private set { if (SetProperty(ref _isXBusy, value)) RaiseReadyStates(); } }
    public bool HasXMedia => XMediaItems.Count > 0;
    public string XMediaSummary => HasXMedia ? $"{XMediaItems.Count} 个" : "尚未解析";
    public bool CanParseXUrl => !IsXBusy && !string.IsNullOrWhiteSpace(XUrl);
    public bool IsXDownloadReady => EngineReady
                                    && _xParseResult is not null
                                    && HasXMedia
                                    && XMediaItems.Any(item => item.IsSelected)
                                    && Directory.Exists(XOutputDirectory)
                                    && !IsXBusy
                                    && string.Equals(XUrl.Trim(), _xParsedSourceText, StringComparison.OrdinalIgnoreCase);
    public bool HasXDownloadResult => !string.IsNullOrWhiteSpace(XLastOutputDirectory) && Directory.Exists(XLastOutputDirectory);
    public string XLastOutputDirectory { get => _xLastOutputDirectory; private set { if (SetProperty(ref _xLastOutputDirectory, value)) OnPropertyChanged(nameof(HasXDownloadResult)); } }

    public bool SelectAllXMedia
    {
        get => _selectAllXMedia;
        set
        {
            if (!SetProperty(ref _selectAllXMedia, value) || _updatingXSelection)
            {
                return;
            }

            _updatingXSelection = true;
            try
            {
                foreach (var item in XMediaItems)
                {
                    item.IsSelected = value;
                }
            }
            finally
            {
                _updatingXSelection = false;
            }

            RaiseReadyStates();
        }
    }

    public bool HasActiveJob => IsGifRunning || IsSubtitleRunning || IsXBusy;

    public async Task InitializeAsync()
    {
        EngineStatus = "正在查找 FFmpeg…";
        EngineError = string.Empty;
        var settings = await _settingsService.LoadAsync();
        _installation = await _locator.FindAsync(settings.FfmpegPath);

        if (_installation is null)
        {
            EngineStatus = "未找到 FFmpeg";
            EngineError = "请选择 ffmpeg.exe；其同目录下还需要 ffprobe.exe。";
        }
        else
        {
            UpdateInstallationDisplay();
        }

        RaiseReadyStates();
    }

    public async Task<bool> SelectFfmpegAsync(string path)
    {
        try
        {
            EngineStatus = "正在检查 FFmpeg…";
            EngineError = string.Empty;
            _installation = await _locator.InspectAsync(path);
            await _settingsService.SaveAsync(new AppSettings { FfmpegPath = _installation.FfmpegPath });
            UpdateInstallationDisplay();
            RaiseReadyStates();
            return true;
        }
        catch (Exception ex)
        {
            _installation = null;
            EngineStatus = "FFmpeg 不可用";
            EngineError = ex.Message;
            RaiseReadyStates();
            return false;
        }
    }

    public async Task SetGifInputAsync(string path)
    {
        if (IsGifRunning) return;
        GifError = string.Empty;
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            GifError = "GIF 转换输入请选择 MP4 文件。";
            return;
        }

        if (_installation is null)
        {
            GifError = "请先选择可用的 FFmpeg。";
            return;
        }

        var version = ++_gifProbeVersion;
        GifTrim.Pause();
        GifTrim.SetEnabled(false);
        if (!string.Equals(path, GifInputPath, StringComparison.OrdinalIgnoreCase)) GifTrim.SetSource(_installation.FfmpegPath, null);
        _gifMedia = null;
        RaiseReadyStates();
        try
        {
            GifStatus = "正在读取媒体信息…";
            var media = await _probe.ProbeAsync(_installation.FfprobePath, path);
            if (version != _gifProbeVersion) return;
            _gifMedia = media;
            GifInputPath = path;
            GifOutputPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".gif");
            GifMediaSummary = BuildMediaSummary(_gifMedia);
            GifStatus = "等待开始";
            GifTrim.SetSource(_installation.FfmpegPath, media);
        }
        catch (Exception ex)
        {
            if (version != _gifProbeVersion) return;
            _gifMedia = null;
            GifTrim.SetSource(_installation?.FfmpegPath ?? "", null);
            GifError = ex.Message;
            GifStatus = "读取失败";
        }
        finally { if (version == _gifProbeVersion) GifTrim.SetEnabled(true); }

        RaiseReadyStates();
    }

    public async Task SetSubtitleVideoAsync(string path)
    {
        SubtitleError = string.Empty;
        if (_installation is null)
        {
            SubtitleError = "请先选择可用的 FFmpeg。";
            return;
        }

        try
        {
            SubtitleStatus = "正在读取媒体信息…";
            _subtitleMedia = await _probe.ProbeAsync(_installation.FfprobePath, path);
            SubtitleVideoPath = path;
            SubtitleOutputPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_subtitled.mp4");
            SubtitleMediaSummary = BuildMediaSummary(_subtitleMedia);
            SubtitleStatus = "等待开始";
        }
        catch (Exception ex)
        {
            _subtitleMedia = null;
            SubtitleError = ex.Message;
            SubtitleStatus = "读取失败";
        }

        RaiseReadyStates();
    }

    public void SetSubtitleFile(string path)
    {
        var supported = new[] { ".srt", ".ass", ".ssa", ".vtt" };
        if (!supported.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            SubtitleError = "字幕文件支持 SRT、ASS、SSA 和 VTT。";
            return;
        }

        SubtitleFilePath = path;
        SubtitleError = string.Empty;
    }

    public async Task ConvertGifAsync()
    {
        if (_installation is null || _gifMedia is null)
        {
            GifError = "请先选择 MP4 和 FFmpeg。";
            return;
        }

        if (!TryBuildGifOptions(out var options, out var validationError))
        {
            GifError = validationError;
            return;
        }

        _gifCancellation = new CancellationTokenSource();
        IsGifRunning = true;
        GifProgress = 0;
        GifError = string.Empty;
        GifDetail = string.Empty;

        var progress = new Progress<ConversionProgress>(value =>
        {
            GifProgress = Math.Clamp(value.Percent, 0, 100);
            GifStatus = value.Stage;
            GifDetail = value.Detail;
        });

        try
        {
            var result = await _gifService.ConvertAsync(_installation, _gifMedia, options!, progress, _gifCancellation.Token);
            GifStatus = "转换完成";
            GifDetail = $"{MediaInfo.FormatBytes(result.FileSizeBytes)} · {result.Attempts} 轮编码";
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                GifError = result.Warning;
            }
        }
        catch (OperationCanceledException)
        {
            GifStatus = "已取消";
            GifDetail = "没有保留未完成的输出文件";
        }
        catch (Exception ex)
        {
            GifStatus = "转换失败";
            GifError = FriendlyError(ex);
        }
        finally
        {
            IsGifRunning = false;
            _gifCancellation.Dispose();
            _gifCancellation = null;
        }
    }

    public async Task BurnSubtitlesAsync()
    {
        if (_installation is null || _subtitleMedia is null)
        {
            SubtitleError = "请先选择视频、字幕和 FFmpeg。";
            return;
        }

        if (!File.Exists(SubtitleFilePath))
        {
            SubtitleError = "字幕文件不存在。";
            return;
        }

        if (!TryParseIntegerInRange(SubtitleCrf, 16, 30, out var subtitleCrf))
        {
            SubtitleError = "视频质量请输入 16–30 的整数。";
            return;
        }

        if (!SubtitleVideoEncoderCatalog.TryParseDisplayName(SelectedSubtitleVideoEncoder, out var subtitleVideoEncoder))
        {
            SubtitleError = "请选择有效的视频编码方式。";
            return;
        }

        if (Path.GetFullPath(SubtitleOutputPath).Equals(Path.GetFullPath(SubtitleVideoPath), StringComparison.OrdinalIgnoreCase))
        {
            SubtitleError = "保存路径不能与输入视频相同。";
            return;
        }

        _subtitleCancellation = new CancellationTokenSource();
        IsSubtitleRunning = true;
        SubtitleProgress = 0;
        SubtitleError = string.Empty;
        SubtitleDetail = string.Empty;

        var progress = new Progress<ConversionProgress>(value =>
        {
            SubtitleProgress = Math.Clamp(value.Percent, 0, 100);
            SubtitleStatus = value.Stage;
            SubtitleDetail = value.Detail;
        });

        try
        {
            var options = new SubtitleBurnOptions(
                SubtitleVideoPath,
                SubtitleFilePath,
                SubtitleOutputPath,
                SelectedSubtitleEncoding,
                subtitleCrf,
                VideoEncoder: subtitleVideoEncoder);
            var result = await _subtitleService.BurnAsync(_installation, _subtitleMedia, options, progress, _subtitleCancellation.Token);
            SubtitleStatus = "烧录完成";
            SubtitleDetail = string.IsNullOrWhiteSpace(result.EncoderName)
                ? MediaInfo.FormatBytes(result.FileSizeBytes)
                : $"{MediaInfo.FormatBytes(result.FileSizeBytes)} · {result.EncoderName}";
        }
        catch (OperationCanceledException)
        {
            SubtitleStatus = "已取消";
            SubtitleDetail = "没有保留未完成的输出文件";
        }
        catch (Exception ex)
        {
            SubtitleStatus = "烧录失败";
            SubtitleError = FriendlyError(ex);
        }
        finally
        {
            IsSubtitleRunning = false;
            _subtitleCancellation.Dispose();
            _subtitleCancellation = null;
        }
    }

    public void CancelGif() => _gifCancellation?.Cancel();
    public void CancelSubtitle() => _subtitleCancellation?.Cancel();

    public async Task ParseXUrlAsync()
    {
        if (IsXBusy)
        {
            return;
        }

        var input = XUrl.Trim();
        ClearXMedia();
        _xParseResult = null;
        _xParsedSourceText = string.Empty;
        _xCancellation = new CancellationTokenSource();
        _isXDownloading = false;
        IsXBusy = true;
        XDownloadProgress = 0;
        XDownloadStatus = "正在验证链接…";
        XDownloadDetail = string.Empty;
        XWarning = string.Empty;
        XError = string.Empty;
        XResultSummary = string.Empty;

        var progress = new Progress<XDownloadProgress>(UpdateXProgress);
        try
        {
            var result = await _xDownloadService.ParseAsync(input, progress, _xCancellation.Token);
            XUrl = result.Source.CanonicalUri.AbsoluteUri;
            _xParseResult = result;
            _xParsedSourceText = XUrl.Trim();
            XFilePrefix = result.SuggestedPrefix;
            foreach (var item in result.Items)
            {
                item.PropertyChanged += XMediaItem_PropertyChanged;
                XMediaItems.Add(item);
            }

            _selectAllXMedia = XMediaItems.Count > 0 && XMediaItems.All(item => item.IsSelected);
            OnPropertiesChanged(nameof(SelectAllXMedia), nameof(HasXMedia), nameof(XMediaSummary));
            XWarning = result.Warning ?? string.Empty;
            XDownloadStatus = "解析完成";
            XDownloadDetail = $"找到 {XMediaItems.Count} 个媒体";
            XDownloadProgress = 100;
        }
        catch (OperationCanceledException)
        {
            XDownloadStatus = "已取消解析";
            XDownloadDetail = "未开始下载媒体";
        }
        catch (XDownloadException ex)
        {
            XDownloadStatus = "解析失败";
            XError = ex.Message;
            XDownloadDetail = BuildTechnicalDetail(ex.Details);
        }
        catch (Exception ex)
        {
            XDownloadStatus = "解析失败";
            XError = ex.Message;
        }
        finally
        {
            IsXBusy = false;
            _xCancellation.Dispose();
            _xCancellation = null;
            RaiseReadyStates();
        }
    }

    public async Task DownloadXMediaAsync()
    {
        if (IsXBusy)
        {
            return;
        }

        if (_installation is null || !EngineReady)
        {
            XError = "请先选择有效的 FFmpeg。";
            return;
        }

        if (_xParseResult is null)
        {
            XError = "请先解析 X 帖子链接。";
            return;
        }

        var selectedItems = XMediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            XError = "请至少选择一个媒体。";
            return;
        }

        _xCancellation = new CancellationTokenSource();
        _isXDownloading = true;
        IsXBusy = true;
        XDownloadProgress = 0;
        XDownloadStatus = "准备下载…";
        XDownloadDetail = string.Empty;
        XWarning = _xParseResult.Warning ?? string.Empty;
        XError = string.Empty;
        XResultSummary = string.Empty;

        var progress = new Progress<XDownloadProgress>(UpdateXProgress);
        try
        {
            var prefix = string.IsNullOrWhiteSpace(XFilePrefix) ? _xParseResult.SuggestedPrefix : XFilePrefix;
            var result = await _xDownloadService.DownloadAsync(
                _xParseResult,
                selectedItems,
                XOutputDirectory,
                prefix,
                _installation.FfmpegPath,
                progress,
                _xCancellation.Token);
            XDownloadProgress = 100;
            XDownloadStatus = "下载完成";
            XDownloadDetail = $"{result.OutputPaths.Count} 个文件 · {MediaInfo.FormatBytes(result.TotalBytes)}";
            XWarning = result.Warning ?? string.Empty;
            XLastOutputDirectory = XOutputDirectory;
            XResultSummary = result.OutputPaths.Count == 1
                ? result.OutputPaths[0]
                : $"已保存 {result.OutputPaths.Count} 个文件到 {XOutputDirectory}";
        }
        catch (OperationCanceledException)
        {
            XDownloadStatus = "已取消";
            XDownloadDetail = "未完成的临时文件已清理";
        }
        catch (XDownloadException ex)
        {
            XDownloadStatus = "下载失败";
            XError = ex.Message;
            XDownloadDetail = BuildTechnicalDetail(ex.Details);
        }
        catch (Exception ex)
        {
            XDownloadStatus = "下载失败";
            XError = ex.Message;
        }
        finally
        {
            _isXDownloading = false;
            IsXBusy = false;
            _xCancellation.Dispose();
            _xCancellation = null;
            RaiseReadyStates();
        }
    }

    public void CancelXDownload()
    {
        if (_xCancellation is null)
        {
            return;
        }

        XDownloadStatus = "正在取消…";
        _xCancellation.Cancel();
    }

    private void UpdateXProgress(XDownloadProgress value)
    {
        var percent = Math.Clamp(value.Percent, 0, 100);
        XDownloadProgress = _isXDownloading ? Math.Max(XDownloadProgress, percent) : percent;
        XDownloadStatus = value.Stage;
        XDownloadDetail = value.Detail;
    }

    private void XMediaItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(XMediaItem.IsSelected) && !_updatingXSelection)
        {
            _updatingXSelection = true;
            try
            {
                var allSelected = XMediaItems.Count > 0 && XMediaItems.All(item => item.IsSelected);
                if (_selectAllXMedia != allSelected)
                {
                    _selectAllXMedia = allSelected;
                    OnPropertyChanged(nameof(SelectAllXMedia));
                }
            }
            finally
            {
                _updatingXSelection = false;
            }
        }

        if (e.PropertyName is nameof(XMediaItem.IsSelected) or nameof(XMediaItem.SelectedQuality))
        {
            RaiseReadyStates();
        }
    }

    private void ClearXMedia()
    {
        foreach (var item in XMediaItems)
        {
            item.PropertyChanged -= XMediaItem_PropertyChanged;
        }

        XMediaItems.Clear();
        _selectAllXMedia = false;
        XResultSummary = string.Empty;
        OnPropertiesChanged(nameof(SelectAllXMedia), nameof(HasXMedia), nameof(XMediaSummary));
        RaiseReadyStates();
    }

    private bool TryBuildGifOptions(out GifConversionOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        if (!File.Exists(GifInputPath))
        {
            error = "MP4 文件不存在。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GifOutputPath) || !Path.GetExtension(GifOutputPath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            error = "保存路径必须以 .gif 结尾。";
            return false;
        }

        if (!TryParseIntegerInRange(GifMaxWidth, 240, 3840, out var maxWidth)
            || !TryParseIntegerInRange(GifFrameRate, 5, 30, out var frameRate)
            || !TryParseIntegerInRange(GifColors, 32, 256, out var colors))
        {
            error = "自定义范围：宽度 240–3840、帧率 5–30、颜色 32–256。";
            return false;
        }

        double? targetSize = null;
        if (IsTargetSizeMode)
        {
            if (!TryParseNumber(GifTargetSizeText, out var size) || size < 0.1 || size > 2048)
            {
                error = "目标大小请输入 0.1–2048 MB。";
                return false;
            }

            targetSize = size;
        }

        if (!TryParseOptionalTime(GifStartTimeText, out var start) || !TryParseOptionalTime(GifEndTimeText, out var end))
        {
            error = "截取时间请输入秒数，或使用 mm:ss / hh:mm:ss。";
            return false;
        }

        if (start is < 0 || end is < 0 || (end.HasValue && end <= (start ?? 0)))
        {
            error = "结束时间必须大于开始时间。";
            return false;
        }

        if (start >= _gifMedia!.DurationSeconds || end > _gifMedia.DurationSeconds)
        {
            error = "截取时间不能超过视频时长。";
            return false;
        }

        var dither = SelectedDither switch
        {
            "规整（更小）" => "bayer",
            "无抖动" => "none",
            _ => "sierra2_4a"
        };

        options = new GifConversionOptions(
            GifInputPath,
            GifOutputPath,
            maxWidth,
            frameRate,
            colors,
            dither,
            targetSize,
            start,
            end);
        return true;
    }

    private void ApplyGifPreset(string preset)
    {
        switch (preset)
        {
            case "高清":
                GifMaxWidth = "960"; GifFrameRate = "20"; GifColors = "256"; SelectedDither = "平滑（推荐）";
                break;
            case "小体积":
                GifMaxWidth = "480"; GifFrameRate = "10"; GifColors = "128"; SelectedDither = "规整（更小）";
                break;
            case "极小":
                GifMaxWidth = "360"; GifFrameRate = "8"; GifColors = "64"; SelectedDither = "规整（更小）";
                break;
            case "均衡":
            case "指定目标大小":
                GifMaxWidth = "720"; GifFrameRate = "15"; GifColors = "192"; SelectedDither = "平滑（推荐）";
                break;
        }
    }

    private bool HasValidGifSettings()
    {
        if (!TryParseIntegerInRange(GifMaxWidth, 240, 3840, out _)
            || !TryParseIntegerInRange(GifFrameRate, 5, 30, out _)
            || !TryParseIntegerInRange(GifColors, 32, 256, out _))
        {
            return false;
        }

        if (IsTargetSizeMode
            && (!TryParseNumber(GifTargetSizeText, out var targetSize) || targetSize is < 0.1 or > 2048))
        {
            return false;
        }

        if (!TryParseOptionalTime(GifStartTimeText, out var start)
            || !TryParseOptionalTime(GifEndTimeText, out var end)
            || start is < 0
            || end is < 0
            || (end.HasValue && end <= (start ?? 0)))
        {
            return false;
        }

        return _gifMedia is null || ((!start.HasValue || start.Value < _gifMedia.DurationSeconds)
            && (!end.HasValue || end.Value <= _gifMedia.DurationSeconds));
    }

    private bool HasValidSubtitleSettings()
        => TryParseIntegerInRange(SubtitleCrf, 16, 30, out _)
           && SubtitleVideoEncoderCatalog.TryParseDisplayName(SelectedSubtitleVideoEncoder, out _);

    private void UpdateInstallationDisplay()
    {
        if (_installation is null)
        {
            return;
        }

        EnginePath = _installation.FfmpegPath;
        GifTrim.SetSource(_installation.FfmpegPath, _gifMedia);
        EngineVersion = _installation.Version;
        var missing = new List<string>();
        if (!_installation.HasGifFilters) missing.Add("GIF 调色板滤镜");
        if (!_installation.HasSubtitleFilter) missing.Add("字幕滤镜");

        EngineStatus = missing.Count == 0 ? "FFmpeg 已就绪" : "FFmpeg 功能不完整";
        EngineError = missing.Count == 0 ? string.Empty : "缺少：" + string.Join("、", missing);
        OnPropertyChanged(nameof(EngineReady));
    }

    private void RaiseReadyStates()
    {
        OnPropertiesChanged(
            nameof(EngineReady),
            nameof(IsGifReady),
            nameof(IsSubtitleReady),
            nameof(CanParseXUrl),
            nameof(IsXDownloadReady),
            nameof(HasActiveJob));
    }

    private static string BuildMediaSummary(MediaInfo media)
        => $"{media.ResolutionText}  ·  {media.DurationText}  ·  {media.FrameRateText}  ·  {media.FileSizeText}";

    private static string FriendlyError(Exception exception)
    {
        if (exception is FfmpegException ffmpeg && !string.IsNullOrWhiteSpace(ffmpeg.Details))
        {
            var lines = ffmpeg.Details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var tail = string.Join(Environment.NewLine, lines.TakeLast(8));
            return $"{ffmpeg.Message}{Environment.NewLine}{tail}";
        }

        return exception.Message;
    }

    private static string BuildTechnicalDetail(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        var lines = details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(6));
    }

    private static string GetDefaultXOutputDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
        {
            return desktop;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents;
    }

    private static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
           || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseIntegerInRange(string text, int minimum, int maximum, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
           && value >= minimum
           && value <= maximum;

    private static bool TryParseOptionalTime(string text, out double? seconds)
        => VideoTimeRange.TryParse(text, out seconds);
}
