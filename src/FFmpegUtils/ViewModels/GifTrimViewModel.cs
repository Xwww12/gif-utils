using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FFmpegUtils.Infrastructure;
using FFmpegUtils.Models;
using FFmpegUtils.Services;

namespace FFmpegUtils.ViewModels;

public sealed class GifTrimViewModel : ObservableObject
{
    private readonly IVideoPreviewService _preview;
    private string _ffmpeg = "";
    private MediaInfo? _media;
    private bool _active;
    private bool _enabled = true;
    private string _startText = "";
    private string _endText = "";
    private double _start;
    private double _end;
    private double _position;
    private string _rangeError = "";
    private string _status = "选择 MP4 后展开预览";
    private string _previewError = "";
    private bool _playing;
    private bool _canResume;
    private bool _busy;
    private bool _loop;
    private ImageSource? _frame;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private int _operationVersion;
    private int _sourceVersion;
    private readonly Dictionary<double, ImageSource> _frameCache = new();
    private readonly Dictionary<double, ImageSource> _scrubCache = new();
    private bool _interacting;
    private bool _seekRunning;
    private bool _requestedBoundary;
    private bool _showBoundaryTime;
    private double _requestedPreview;
    private double _previewPosition;
    private long _requestId;
    private long _displayedRequestId;
    private long _lastScrubStart;
    private SeekRequest? _pendingSeek;
    private SeekRequest? _currentSeek;
    private sealed record SeekRequest(long Id, double Seconds, bool Fast, bool Boundary)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public GifTrimViewModel(IVideoPreviewService? preview = null) => _preview = preview ?? new VideoPreviewService();
    public ObservableCollection<ImageSource> Thumbnails { get; } = [];
    public string StartText { get => _startText; set { if (SetProperty(ref _startText, value)) ValidateRange(true); } }
    public string EndText { get => _endText; set { if (SetProperty(ref _endText, value)) ValidateRange(true, previewEnd: true); } }
    public double Duration => _media?.DurationSeconds ?? 0;
    public double Start => _start;
    public double End => _end;
    public double Position { get => _position; private set { if (SetProperty(ref _position, value)) OnPropertiesChanged(nameof(PositionText), nameof(PreviewTimeText)); } }
    public string PositionText => $"{VideoTimeRange.Format(Position)} / {VideoTimeRange.Format(Duration)}";
    public double PreviewPosition => _previewPosition;
    public string PreviewTimeText => _showBoundaryTime ? $"边界 {VideoTimeRange.Format(PreviewPosition)} / {VideoTimeRange.Format(Duration)}" : PositionText;
    public string SelectionText => RangeError.Length == 0 && HasVideo ? $"选中 {VideoTimeRange.Format(End - Start)}" : "选区无效";
    public string RangeError { get => _rangeError; private set => SetProperty(ref _rangeError, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PreviewError { get => _previewError; private set => SetProperty(ref _previewError, value); }
    public ImageSource? Frame { get => _frame; private set => SetProperty(ref _frame, value); }
    public bool Loop { get => _loop; set { if (SetProperty(ref _loop, value)) OnPropertyChanged(nameof(LoopText)); } }
    public string LoopText => Loop ? "循环已开启，点击关闭" : "循环播放选区";
    public bool HasVideo => _media is not null && double.IsFinite(Duration) && Duration > 0;
    public bool CanEdit => HasVideo && _enabled;
    public bool CanPlay => CanEdit && _active && RangeError.Length == 0 && !string.IsNullOrWhiteSpace(_ffmpeg);
    public bool IsPlaying { get => _playing; private set { if (SetProperty(ref _playing, value)) OnPropertyChanged(nameof(PlayText)); } }
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string PlayText => IsPlaying ? "暂停" : _canResume ? "继续播放" : "播放选区";

    public void SetSource(string ffmpeg, MediaInfo? media)
    {
        var sameVideo = media is not null && _media is not null
            && string.Equals(media.Path, _media.Path, StringComparison.OrdinalIgnoreCase)
            && media.FileSizeBytes == _media.FileSizeBytes && media.DurationSeconds == _media.DurationSeconds;
        Pause();
        _canResume = false;
        CancelThumbnails();
        ++_sourceVersion;
        _ffmpeg = ffmpeg;
        _media = media;
        _frameCache.Clear();
        _scrubCache.Clear();
        Thumbnails.Clear();
        Frame = null;
        _showBoundaryTime = false;
        _previewPosition = 0;
        PreviewError = "";
        if (!sameVideo)
        {
            _startText = "";
            _endText = "";
            _start = 0;
            _end = Math.Max(0, Duration);
            Position = 0;
            OnPropertiesChanged(nameof(StartText), nameof(EndText));
        }
        ValidateRange(false);
        Position = Math.Clamp(Position, 0, Math.Max(0, Duration));
        Status = HasVideo ? "拖动外侧手柄裁剪；拖动缩略图定位" : "选择 MP4 后展开预览";
        OnPropertiesChanged(nameof(Duration), nameof(HasVideo), nameof(CanEdit), nameof(CanPlay), nameof(PositionText), nameof(PreviewTimeText), nameof(PreviewPosition));
        if (_active && CanEdit) Refresh();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active) { Pause(); CancelThumbnails(); }
        else if (CanEdit) Refresh();
        OnPropertyChanged(nameof(CanPlay));
    }

    public void SetEnabled(bool enabled)
    {
        var changed = _enabled != enabled;
        _enabled = enabled;
        if (!enabled) { Pause(); CancelThumbnails(); }
        OnPropertiesChanged(nameof(CanEdit), nameof(CanPlay));
        if (changed && enabled && _active && CanEdit) Refresh();
    }

    public void SetRange(double start, double end, double previewPosition)
    {
        if (!CanEdit || !double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(previewPosition)) return;
        if (!_interacting) Pause();
        _canResume = false;
        start = Math.Clamp(Math.Round(start, 3), 0, Math.Max(0, Duration - 0.001));
        end = Math.Clamp(Math.Round(end, 3), Math.Min(Duration, start + 0.001), Duration);
        _startText = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _endText = end.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        _start = start;
        _end = end;
        RangeError = "";
        OnPropertiesChanged(nameof(StartText), nameof(EndText), nameof(Start), nameof(End), nameof(SelectionText), nameof(CanPlay), nameof(PlayText));
        // Preview the boundary without moving the independent playhead / snap reference.
        _ = RequestFrameAsync(Math.Clamp(previewPosition, start, LastSelectedFrame(start, end)), boundary: true);
    }

    public void ResetRange()
    {
        if (!CanEdit) return;
        _startText = "";
        _endText = "";
        ValidateRange(false);
        OnPropertiesChanged(nameof(StartText), nameof(EndText));
        _ = SeekAsync(0);
    }

    private double LastSelectedFrame(double start, double end) => Math.Max(start, end - 1 / Math.Max(1, _media?.FrameRate ?? 25));

    private void ValidateRange(bool preview, bool previewEnd = false)
    {
        Pause();
        _canResume = false;
        OnPropertyChanged(nameof(PlayText));
        if (VideoTimeRange.TryGet(StartText, EndText, Duration, out var start, out var end, out var error))
        {
            _start = start;
            _end = end;
            RangeError = "";
            if (preview && _active) _ = RequestFrameAsync(previewEnd ? LastSelectedFrame(start, end) : start, boundary: true);
        }
        else RangeError = HasVideo ? error : "";
        OnPropertiesChanged(nameof(Start), nameof(End), nameof(SelectionText), nameof(CanPlay));
    }

    private void Refresh()
    {
        _ = SeekAsync(Position);
    }

    public void BeginInteraction(bool editingRange)
    {
        Pause();
        CancelThumbnails();
        if (!CanEdit || !_active) return;
        _interacting = true;
        _requestedPreview = Position;
        _requestedBoundary = editingRange;
        _canResume = false;
        OnPropertyChanged(nameof(PlayText));
    }

    public Task EndInteractionAsync()
    {
        if (!_interacting) return Task.CompletedTask;
        var seconds = _requestedPreview;
        var boundary = _requestedBoundary;
        Pause(); // Invalidate the in-flight fast frame; it must not replace the precise release frame.
        return RequestFrameAsync(seconds, boundary);
    }

    public Task SeekAsync(double seconds)
    {
        if (!CanEdit || !double.IsFinite(seconds)) return Task.CompletedTask;
        if (IsPlaying) Pause();
        _canResume = false;
        OnPropertyChanged(nameof(PlayText));
        Position = Math.Clamp(seconds, 0, Duration);
        return RequestFrameAsync(Position, boundary: false);
    }

    private double PreviewSeek(double seconds, bool fast)
    {
        var lastFrame = Math.Max(0, Duration - Math.Max(0.04, 1 / Math.Max(1, _media?.FrameRate ?? 25)));
        // Only fast visual frames use a 50 ms cache grid; never quantize the actual GIF range.
        var seek = fast ? Math.Round(seconds / 0.05) * 0.05 : seconds;
        return Math.Round(Math.Clamp(seek, 0, lastFrame), 3);
    }

    private Task RequestFrameAsync(double seconds, bool boundary)
    {
        _requestedPreview = seconds;
        _requestedBoundary = boundary;
        if (!CanEdit || !_active) return Task.CompletedTask;
        CancelThumbnails();
        var request = new SeekRequest(++_requestId, PreviewSeek(seconds, _interacting), _interacting, boundary);
        _pendingSeek?.Completion.TrySetResult();
        _pendingSeek = null;
        PreviewError = "";
        // Cache hits are immediate, even if an older decode is still cleaning up.
        if (TryCachedFrame(request, out var cached))
        {
            PublishFrame(request, cached!);
            if (!_seekRunning && !_interacting) _ = LoadThumbnailsAsync();
            return Task.CompletedTask;
        }
        _pendingSeek = request;
        IsBusy = true;
        Status = "正在定位画面…";
        if (_seekRunning) return request.Completion.Task;
        _seekRunning = true;
        var version = ++_operationVersion;
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _ = ProcessSeekQueueAsync(version, cancellation, _media!, _ffmpeg);
        return request.Completion.Task;
    }

    private bool TryCachedFrame(SeekRequest request, out ImageSource? bitmap)
    {
        if (_frameCache.TryGetValue(request.Seconds, out bitmap)) return true;
        return request.Fast && _scrubCache.TryGetValue(request.Seconds, out bitmap);
    }

    private void PublishFrame(SeekRequest request, ImageSource bitmap)
    {
        if (request.Id < _displayedRequestId) return;
        _displayedRequestId = request.Id;
        Frame = bitmap;
        _previewPosition = request.Seconds;
        _showBoundaryTime = request.Boundary;
        OnPropertiesChanged(nameof(PreviewPosition), nameof(PreviewTimeText));
        Status = request.Boundary ? "边界画面 · 播放指针保持不变" : "原视频画面 · 无声预览";
        IsBusy = false;
    }

    private async Task ProcessSeekQueueAsync(int version, CancellationTokenSource cancellation, MediaInfo source, string ffmpeg)
    {
        var token = cancellation.Token;
        SeekRequest? request = null;
        try
        {
            // One running decode plus one replaceable pending position. Moving the mouse
            // does NOT restart the delay or cancel a useful in-flight frame on every pixel.
            while (_pendingSeek is not null && version == _operationVersion)
            {
                request = _pendingSeek;
                _pendingSeek = null;
                _currentSeek = request;
                if (request.Fast)
                {
                    var delay = 75 - (Environment.TickCount64 - _lastScrubStart);
                    if (delay > 0) await Task.Delay((int)delay, token);
                    if (version != _operationVersion) return;
                    if (_pendingSeek is not null)
                    {
                        request.Completion.TrySetResult();
                        request = _pendingSeek;
                        _pendingSeek = null;
                        _currentSeek = request;
                    }
                }
                if (!TryCachedFrame(request, out var bitmap))
                {
                    if (request.Fast) _lastScrubStart = Environment.TickCount64;
                    var frame = request.Fast
                        ? await _preview.GetScrubFrameAsync(ffmpeg, source.Path, request.Seconds, token)
                        : await _preview.GetFrameAsync(ffmpeg, source.Path, request.Seconds, false, token);
                    if (version != _operationVersion || token.IsCancellationRequested) return;
                    bitmap = ToBitmap(frame);
                    var cache = request.Fast ? _scrubCache : _frameCache;
                    var capacity = request.Fast ? 64 : 16;
                    if (cache.Count >= capacity) cache.Remove(cache.Keys.First());
                    cache[request.Seconds] = bitmap;
                }
                if (version != _operationVersion) return;
                PublishFrame(request, bitmap!);
                request.Completion.TrySetResult();
                request = null;
                _currentSeek = null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (version == _operationVersion) SetError(exception); }
        finally
        {
            request?.Completion.TrySetResult();
            cancellation.Dispose();
            if (version == _operationVersion)
            {
                _pendingSeek?.Completion.TrySetResult();
                _pendingSeek = null;
                _currentSeek = null;
                _seekRunning = false;
                IsBusy = false;
                _operationCancellation = null;
                if (!_interacting) _ = LoadThumbnailsAsync();
            }
        }
    }

    public async Task PlayAsync(bool restart = false)
    {
        if (IsPlaying && !restart) { Pause(); return; }
        if (!CanPlay) return;
        var resume = _canResume && !restart;
        Pause();
        _canResume = false;
        CancelThumbnails();
        var version = ++_operationVersion;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        var token = cancellation.Token;
        var source = _media!;
        var ffmpeg = _ffmpeg;
        var start = Start;
        var end = End;
        var playFrom = !resume || Position < start || Position >= end ? start : Position;
        IsPlaying = true;
        IsBusy = true;
        PreviewError = "";
        Status = "正在准备播放…";
        try
        {
            do
            {
                await _preview.PlayAsync(ffmpeg, source.Path, playFrom, end, frame =>
                {
                    if (version != _operationVersion || token.IsCancellationRequested) return;
                    Position = Math.Clamp(frame.Seconds, start, end);
                    Frame = ToBitmap(frame);
                    _previewPosition = frame.Seconds;
                    _showBoundaryTime = false;
                    OnPropertiesChanged(nameof(PreviewPosition), nameof(PreviewTimeText));
                    IsBusy = false;
                    Status = "正在播放选区 · 无声";
                }, token);
                if (version != _operationVersion || token.IsCancellationRequested) return;
                Position = end;
                playFrom = start;
            } while (Loop && _active && CanEdit);
            Status = "已到选区终点";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (version == _operationVersion) SetError(exception); }
        finally
        {
            if (version == _operationVersion)
            {
                IsPlaying = false;
                IsBusy = false;
                _operationCancellation = null;
                if (_active) _ = LoadThumbnailsAsync();
            }
        }
    }

    public void Pause()
    {
        ++_operationVersion;
        var operation = _operationCancellation;
        _operationCancellation = null;
        operation?.Cancel();
        _pendingSeek?.Completion.TrySetResult();
        _currentSeek?.Completion.TrySetResult();
        _pendingSeek = null;
        _currentSeek = null;
        _seekRunning = false;
        _interacting = false;
        if (IsPlaying) { Status = "已暂停"; _canResume = true; }
        IsPlaying = false;
        IsBusy = false;
    }

    private async Task LoadThumbnailsAsync()
    {
        if (!CanEdit || !_active || _interacting || _seekRunning || IsPlaying || _thumbnailCancellation is not null || Thumbnails.Count == 8) return;
        var version = _sourceVersion;
        using var cancellation = new CancellationTokenSource();
        _thumbnailCancellation = cancellation;
        var token = cancellation.Token;
        var source = _media!;
        var ffmpeg = _ffmpeg;
        try
        {
            for (var i = Thumbnails.Count; i < 8; i++)
            {
                var seconds = Math.Max(0, source.DurationSeconds - 0.1) * i / 7;
                var frame = await _preview.GetFrameAsync(ffmpeg, source.Path, seconds, true, token);
                if (version != _sourceVersion || token.IsCancellationRequested) return;
                Thumbnails.Add(ToBitmap(frame));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (version == _sourceVersion && !token.IsCancellationRequested) PreviewError = "部分缩略图读取失败；仍可调整时间或重试播放。"; }
        finally { if (ReferenceEquals(_thumbnailCancellation, cancellation)) _thumbnailCancellation = null; }
    }

    private void CancelThumbnails()
    {
        var cancellation = _thumbnailCancellation;
        _thumbnailCancellation = null;
        cancellation?.Cancel();
    }

    private static BitmapSource ToBitmap(VideoPreviewFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void SetError(Exception exception)
    {
        Status = "预览失败";
        PreviewError = exception is FileNotFoundException or TimeoutException ? exception.Message : "无法预览此位置，请重试或重新选择视频；也可手动输入截取时间。";
    }
}
