using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FFmpegUtils.Controls;

public sealed class RangeSelectionEventArgs(double start, double end, double preview) : EventArgs
{
    public double Start { get; } = start;
    public double End { get; } = end;
    public double Preview { get; } = preview;
}

public sealed class RangeTimeline : Control
{
    private enum DragMode { None, Start, End, Position }
    private DragMode _drag;
    private Point _press;
    private double _initialBoundary;
    private double _snapPosition;
    private bool _moved;
    private bool _snapped;
    private DragMode _hover;
    private const double Gutter = 10;
    public const double KeyboardStep = 0.05;
    private const double SnapEnterDistance = 8;
    private const double SnapExitDistance = 12;
    public static readonly DependencyProperty DurationProperty = NumberProperty(nameof(Duration));
    public static readonly DependencyProperty StartProperty = NumberProperty(nameof(Start));
    public static readonly DependencyProperty EndProperty = NumberProperty(nameof(End));
    public static readonly DependencyProperty PositionProperty = NumberProperty(nameof(Position));
    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(nameof(Thumbnails), typeof(IEnumerable<ImageSource>), typeof(RangeTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ThumbnailsChanged));
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => (double)GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public IEnumerable<ImageSource>? Thumbnails { get => (IEnumerable<ImageSource>?)GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public event EventHandler<RangeSelectionEventArgs>? RangeSelectionChanged;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler<bool>? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public bool IsSnapped => _snapped;

    public RangeTimeline()
    {
        Focusable = true;
        Height = 82;
        AutomationProperties.SetName(this, "视频选区时间轴");
        AutomationProperties.SetHelpText(this, "拖动左右外侧手柄调整范围，靠近播放指针时吸附。缩略图与下方播放条只定位。←→定位，Shift+←→调起点，Ctrl+←→调终点，每次 0.05 秒。");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CompletePointer(); };
    }
    private static DependencyProperty NumberProperty(string name) => DependencyProperty.Register(name, typeof(double), typeof(RangeTimeline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static void ThumbnailsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (RangeTimeline)sender;
        if (args.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= control.ImagesChanged;
        if (args.NewValue is INotifyCollectionChanged current) current.CollectionChanged += control.ImagesChanged;
    }
    private void ImagesChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();
    private Brush Theme(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private bool HasDuration => double.IsFinite(Duration) && Duration > 0;
    private double TrackWidth => Math.Max(1, ActualWidth - Gutter * 2);
    private double X(double seconds) => Gutter + (HasDuration ? Math.Clamp(seconds / Duration, 0, 1) : 0) * TrackWidth;
    public double SecondsAt(double x) => HasDuration ? Math.Clamp((x - Gutter) / TrackWidth, 0, 1) * Duration : 0;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var border = Theme("StrongBorderBrush", Brushes.Gray);
        var accent = Theme("AccentBrush", Brushes.DodgerBlue);
        var text = Theme("TextBrush", Brushes.Black);
        var background = Theme("ControlBrush", Brushes.White);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var track = new Rect(Gutter, 4, TrackWidth, 46);
        dc.DrawRectangle(background, new Pen(border, 1), track);
        var images = Thumbnails?.Take(8).ToArray() ?? [];
        for (var i = 0; i < images.Length; i++)
            dc.DrawImage(images[i], new Rect(Gutter + i * TrackWidth / 8, 5, TrackWidth / 8, 44));
        if (HasDuration)
        {
            var left = X(Start);
            var right = X(End);
            var shade = background.Clone();
            shade.Opacity = 0.72;
            dc.DrawRectangle(shade, null, new Rect(Gutter, 4, Math.Max(0, left - Gutter), 46));
            dc.DrawRectangle(shade, null, new Rect(right, 4, Math.Max(0, Gutter + TrackWidth - right), 46));
            dc.DrawLine(new Pen(accent, 1), new Point(left, 4), new Point(right, 4));
            dc.DrawLine(new Pen(accent, 1), new Point(left, 50), new Point(right, 50));
            Handle(dc, left, DragMode.Start, accent, background);
            Handle(dc, right, DragMode.End, accent, background);
        }
        dc.DrawLine(new Pen(border, 3), new Point(Gutter, 66), new Point(Gutter + TrackWidth, 66));
        if (HasDuration)
        {
            dc.DrawLine(new Pen(accent, 4), new Point(X(Start), 66), new Point(X(End), 66));
            dc.DrawLine(new Pen(text, 1), new Point(X(Position), 2), new Point(X(Position), 73));
            dc.DrawEllipse(background, new Pen(text, 2), new Point(X(Position), 66), 5, 5);
            if (_snapped)
            {
                dc.DrawLine(new Pen(accent, 2), new Point(X(_snapPosition), 2), new Point(X(_snapPosition), 73));
                dc.DrawEllipse(accent, new Pen(background, 1), new Point(X(_snapPosition), 66), 4, 4);
            }
        }
        if (IsKeyboardFocused && MainWindow.GetShowKeyboardFocusCues(this))
            dc.DrawRectangle(null, new Pen(Theme("FocusBrush", Brushes.Blue), 1), new Rect(1, 1, Math.Max(0, ActualWidth - 2), ActualHeight - 2));
    }

    private void Handle(DrawingContext dc, double x, DragMode handle, Brush accent, Brush background)
    {
        var active = _drag == handle || _hover == handle;
        dc.DrawLine(new Pen(accent, 1.5), new Point(x, 4), new Point(x, 50));
        dc.DrawRectangle(active ? accent : background, new Pen(accent, 1.5),
            new Rect(handle == DragMode.Start ? x - 8 : x, 18, 8, 18));
    }

    private DragMode HitHandle(Point point)
    {
        if (!HasDuration || point.Y < 4 || point.Y > 50) return DragMode.Position;
        var left = X(Start);
        var right = X(End);
        // Extra hit area stays OUTSIDE the selection: image scrubbing must never grab a handle.
        var start = point.X <= left && (point.X >= left - 3 || (point.Y >= 16 && point.Y <= 38 && point.X >= left - 10));
        var end = point.X >= right && (point.X <= right + 3 || (point.Y >= 16 && point.Y <= 38 && point.X <= right + 10));
        if (start && end) return point.X <= (left + right) / 2 ? DragMode.Start : DragMode.End;
        return start ? DragMode.Start : end ? DragMode.End : DragMode.Position;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasDuration || !IsEnabled) return;
        BeginPointer(e.GetPosition(this));
        e.Handled = true;
    }

    private void BeginPointer(Point point)
    {
        if (!HasDuration || !IsEnabled) return;
        Focus();
        _press = point;
        _moved = false;
        _snapped = false;
        var mode = HitHandle(point);
        // CaptureMouse can synchronously raise MouseMove. Do not arm a drag until
        // capture and the fixed reference/offset have all been initialized.
        _drag = DragMode.None;
        if (!CaptureMouse()) return;
        InteractionStarted?.Invoke(this, mode != DragMode.Position);
        if (!IsMouseCaptured || !IsEnabled) return;
        _snapPosition = Math.Clamp(Position, 0, Duration);
        _initialBoundary = mode == DragMode.Start ? Start : End;
        _drag = mode;
        if (_drag == DragMode.Position) Seek(SecondsAt(point.X));
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MovePointer(e.GetPosition(this));
        if (_drag != DragMode.None) e.Handled = true;
    }

    private void MovePointer(Point point)
    {
        _hover = HitHandle(point);
        Cursor = (_drag == DragMode.None ? _hover : _drag) is DragMode.Start or DragMode.End ? Cursors.SizeWE : Cursors.Hand;
        InvalidateVisual();
        if (_drag == DragMode.None || !IsMouseCaptured) return;
        if (Math.Abs(point.X - _press.X) >= 2) _moved = true;
        if (_drag == DragMode.Position) { Seek(SecondsAt(point.X)); return; }
        if (!_moved) return;
        // Preserve the grab offset so a press on the outer tab doesn't jump the boundary.
        var seconds = Math.Clamp(_initialBoundary + (point.X - _press.X) / TrackWidth * Duration, 0, Duration);
        var canSnap = _drag == DragMode.Start ? _snapPosition <= End - 0.001 : _snapPosition >= Start + 0.001;
        _snapped = canSnap && Math.Abs(X(seconds) - X(_snapPosition)) <= (_snapped ? SnapExitDistance : SnapEnterDistance);
        if (_snapped) seconds = _snapPosition;
        switch (_drag)
        {
            case DragMode.Start: ChangeRange(Math.Min(seconds, End - 0.001), End, seconds); break;
            case DragMode.End: ChangeRange(Start, Math.Max(seconds, Start + 0.001), seconds); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        EndPointer(e.GetPosition(this));
        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }

    private void EndPointer(Point point)
    {
        if (_drag == DragMode.None) return;
        MovePointer(point);
        CompletePointer();
    }
    private void CompletePointer()
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _snapped = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { CompletePointer(); base.OnLostMouseCapture(e); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = DragMode.None; InvalidateVisual(); base.OnMouseLeave(e); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = HandleKey(e.Key, Keyboard.Modifiers);
    }
    private bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (!HasDuration || !IsEnabled || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        var delta = key == Key.Left ? -KeyboardStep : key == Key.Right ? KeyboardStep : 0;
        if (delta != 0)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift)) ChangeRange(Math.Clamp(Start + delta, 0, Math.Max(0, End - 0.001)), End, Start + delta);
            else if (modifiers.HasFlag(ModifierKeys.Control)) ChangeRange(Start, Math.Clamp(End + delta, Math.Min(Duration, Start + 0.001), Duration), End + delta);
            else Seek(Position + delta);
        }
        else if (key == Key.Home) Seek(0);
        else if (key == Key.End) Seek(Duration);
        else return false;
        InvalidateVisual();
        return true;
    }
    private void ChangeRange(double start, double end, double preview)
    {
        start = Math.Clamp(start, 0, Math.Max(0, Duration - 0.001));
        end = Math.Clamp(end, Math.Min(Duration, start + 0.001), Duration);
        RangeSelectionChanged?.Invoke(this, new RangeSelectionEventArgs(start, end, preview));
    }
    public void Seek(double seconds) { if (HasDuration && double.IsFinite(seconds) && IsEnabled) SeekRequested?.Invoke(this, Math.Clamp(seconds, 0, Duration)); }
    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);
    private sealed class TimelinePeer(RangeTimeline owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(RangeTimeline);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 1;
        public double SmallChange => KeyboardStep;
        public double Maximum => owner.HasDuration ? owner.Duration : 0;
        public double Minimum => 0;
        public double Value => owner.Position;
        public void SetValue(double value) => owner.Dispatcher.Invoke(() => owner.Seek(value));
    }
}
