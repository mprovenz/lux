using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Lux.App.Controls;

/// <summary>What the slider reports to its row: a drag (begin, moves, end) or a single committed step (click on the track, a key).</summary>
public interface IAdjustmentSliderTarget
{
    /// <summary>Position 0–1 of the current value.</summary>
    double Position { get; }
    /// <summary>Position 0–1 of the default value (the fill starts there).</summary>
    double DefaultPosition { get; }
    /// <summary>Position change per key step (0–1).</summary>
    double StepPosition { get; }
    void BeginDrag();
    void DragTo(double position);
    void EndDrag();
}

/// <summary>
/// A tone / colour slider in the Studio style: a thin track, an accent fill from the default value to the current one, a round thumb. A press
/// starts a drag (the picture follows at a lower level, no history step), moves drag, the release commits one step. Keys: ← → one step,
/// Shift ten steps, Home / End the ends; key presses within 0.6 s count as one drag (one history step). Optional gradient track (temperature
/// blue → yellow, tint green → magenta, as Lumen's). Never waits for anything: it only calls its target.
/// </summary>
public sealed class AdjustmentSlider : Control
{
    public static readonly StyledProperty<IAdjustmentSliderTarget?> TargetProperty = AvaloniaProperty.Register<AdjustmentSlider, IAdjustmentSliderTarget?>(nameof(Target));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<AdjustmentSlider, double>(nameof(Position));
    public static readonly StyledProperty<IBrush?> TrackGradientProperty = AvaloniaProperty.Register<AdjustmentSlider, IBrush?>(nameof(TrackGradient));

    static readonly IBrush Track = new SolidColorBrush(Color.Parse("#555555"));
    static readonly IBrush TrackDisabled = new SolidColorBrush(Color.Parse("#4a4a4a"));
    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#a9dcff"));
    static readonly IBrush Thumb = new SolidColorBrush(Color.Parse("#a9dcff"));
    static readonly IBrush ThumbHover = new SolidColorBrush(Color.Parse("#d3edff"));
    static readonly IBrush ThumbDisabled = new SolidColorBrush(Color.Parse("#6a6a6a"));
    static readonly IPen FocusPen = new Pen(new SolidColorBrush(Color.Parse("#a9dcff")), 2);
    static readonly IPen DefaultTick = new Pen(new SolidColorBrush(Color.Parse("#8a8a8a")), 1);
    const double Pad = 8, ThumbRadius = 6;

    bool _dragging, _hover, _keyDrag;
    DispatcherTimer? _keyTimer;

    static AdjustmentSlider()
    {
        AffectsRender<AdjustmentSlider>(PositionProperty, TrackGradientProperty, IsEnabledProperty, IsEffectivelyEnabledProperty);
        FocusableProperty.OverrideDefaultValue<AdjustmentSlider>(true);
    }

    public IAdjustmentSliderTarget? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    /// <summary>Position 0–1 shown (bound to the row's position).</summary>
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public IBrush? TrackGradient { get => GetValue(TrackGradientProperty); set => SetValue(TrackGradientProperty, value); }
    public bool IsDragging => _dragging || _keyDrag;

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, 28);

    double X(double p) => Pad + Math.Clamp(p, 0, 1) * Math.Max(1, Bounds.Width - 2 * Pad);
    double P(double x) => Math.Clamp((x - Pad) / Math.Max(1, Bounds.Width - 2 * Pad), 0, 1);

    public override void Render(DrawingContext context)
    {
        double cy = Bounds.Height / 2;
        var track = new Rect(Pad, cy - 1.5, Math.Max(0, Bounds.Width - 2 * Pad), 3);
        context.FillRectangle(!IsEffectivelyEnabled ? TrackDisabled : TrackGradient ?? Track, track);
        double d = X(Target?.DefaultPosition ?? 0), v = X(Position);
        if (IsEffectivelyEnabled && TrackGradient == null) context.FillRectangle(Fill, new Rect(Math.Min(d, v), cy - 1.5, Math.Abs(v - d), 3));
        if (Target != null && Target.DefaultPosition > 0.001 && Target.DefaultPosition < 0.999) context.DrawLine(DefaultTick, new Point(d, cy - 6), new Point(d, cy + 6));
        var brush = !IsEffectivelyEnabled ? ThumbDisabled : _hover || _dragging ? ThumbHover : Thumb;
        context.DrawEllipse(brush, null, new Point(v, cy), ThumbRadius, ThumbRadius);
        if (IsFocused && IsEffectivelyEnabled && KeyboardFocusVisible) context.DrawEllipse(null, FocusPen, new Point(v, cy), ThumbRadius + 3, ThumbRadius + 3);
    }

    bool KeyboardFocusVisible => PseudoClasses.Contains(":focus-visible");

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || Target is not { } t || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2) return;   // the row resets on double-click of its label; a second click on the track is a new drag
        Focus();
        EndKeyDrag();
        _dragging = true;
        e.Pointer.Capture(this);
        t.BeginDrag();
        t.DragTo(P(e.GetPosition(this).X));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && Target is { } t) { t.DragTo(P(e.GetPosition(this).X)); e.Handled = true; }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Target?.EndDrag();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        Target?.EndDrag();
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = false; InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled || Target is not { } t || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        double step = t.StepPosition * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
        double? to = e.Key switch
        {
            Key.Left or Key.Down => t.Position - step,
            Key.Right or Key.Up => t.Position + step,
            Key.Home => 0,
            Key.End => 1,
            Key.PageDown => t.Position - step * 10,
            Key.PageUp => t.Position + step * 10,
            _ => null,
        };
        if (to is not double p) return;
        if (!_keyDrag) { _keyDrag = true; t.BeginDrag(); }
        t.DragTo(Math.Clamp(p, 0, 1));
        _keyTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Normal, (_, _) => EndKeyDrag());
        _keyTimer.Stop();
        _keyTimer.Start();
        e.Handled = true;
    }

    /// <summary>Commit a run of key presses (one history step).</summary>
    public void EndKeyDrag()
    {
        _keyTimer?.Stop();
        if (!_keyDrag) return;
        _keyDrag = false;
        Target?.EndDrag();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndKeyDrag();
        InvalidateVisual();
    }

    protected override void OnGotFocus(GotFocusEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
}
