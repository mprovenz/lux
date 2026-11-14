using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lux.App.Controls;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Viewer;

/// <summary>How the viewer compares the edited picture with the original.</summary>
public enum CompareMode
{
    /// <summary>The edited picture only.</summary>
    Off,
    /// <summary>Original on the left of a movable divider, edited on the right.</summary>
    Split,
    /// <summary>Before / after by toggling: the edited picture shown, the original kept ready.</summary>
    ToggleAfter,
    /// <summary>Before / after by toggling: the original shown.</summary>
    ToggleBefore,
}

/// <summary>How the zoom follows the view's size.</summary>
public enum ZoomMode { Fit, Fill, Free }

/// <summary>Frame-time and tile figures of a viewer.</summary>
public sealed class ViewerStats
{
    public List<double> RenderMs { get; } = new();
    public List<double> TileLatencyMs { get; } = new();
    public int Requests { get; set; }
    public int TilesDrawnLastFrame { get; set; }
    public int MaxTilesDrawn { get; set; }
    public int FramesWithGaps { get; set; }
    public int GeometryResets { get; set; }
    /// <summary>Frames that drew tiles of more than one content version side by side (what the tile-swap policy avoids).</summary>
    public int MixedFrames { get; set; }
    /// <summary>Frames that kept the previous picture while a new version was coming (the swap window, a drag).</summary>
    public int HeldFrames { get; set; }
    /// <summary>Whole-picture swaps to a new version.</summary>
    public int Swaps { get; set; }
    /// <summary>Tiles drawn cross-fading over the previous picture (the progressive delivery after the swap window).</summary>
    public int FadedTiles { get; set; }
    public void Reset() { RenderMs.Clear(); TileLatencyMs.Clear(); Requests = 0; MaxTilesDrawn = 0; FramesWithGaps = 0; GeometryResets = 0; MixedFrames = 0; HeldFrames = 0; Swaps = 0; FadedTiles = 0; }
}

/// <summary>
/// How a new version of the picture replaces the one on screen (never tile by tile in arbitrary order).
/// While a slider is dragged, and whenever every visible tile of the new version arrives within <see cref="WindowMs"/>, the whole visible picture
/// is swapped at once; otherwise, after the window, the new tiles are shown as they come — the source renders them from the centre of the view
/// outwards — each cross-fading over the previous picture for <see cref="FadeMs"/>.
/// </summary>
public sealed class TileSwapSettings
{
    public bool Enabled { get; set; } = true;
    public double WindowMs { get; set; } = 300;
    public double FadeMs { get; set; } = 120;
}

/// <summary>
/// The picture viewer (tile pyramid on a neutral stage): pan by dragging, scrolling or keys; zoom with the wheel around the pointer, pinch,
/// or the page's controls (fit, fill, 1:1, slider); smooth and clamped. It asks its <see cref="ITileSource"/> for the tiles of the level the
/// zoom needs within the visible rectangle and composites what arrives: the coarsest level is always held, so something is on screen at
/// once, and each screen slot draws the finest tile of the current version it has — else the newest worth showing, else coarser ones — so
/// sharper tiles replace blurrier ones without a gap or a flash. Tiles older than the source's floor are dropped on arrival; tile memory is
/// bounded. The source delivers the picture as shown (turned, flipped, cropped); when its geometry changes every tile is dropped, and the
/// old picture is held, turned or re-framed as the page says (<see cref="ExpectGeometryChange"/>), until the new one is in. Overlays
/// (<see cref="ViewerOverlay"/>) draw above in layers. While the requested quality is being made (<see cref="State"/> = Processing) it shows
/// the best picture it has, blurred, with the spinner and "Processing image". Nothing waits on a render: arrivals are queued and applied
/// once per UI turn.
/// </summary>
public sealed class PhotoViewer : Control, IViewerCommands
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<PhotoViewer, IBrush?>(nameof(Background));
    public static readonly StyledProperty<CompareMode> CompareProperty = AvaloniaProperty.Register<PhotoViewer, CompareMode>(nameof(Compare));
    public static readonly StyledProperty<double> SplitPositionProperty = AvaloniaProperty.Register<PhotoViewer, double>(nameof(SplitPosition), 0.5);
    public static readonly StyledProperty<ImageState> StateProperty = AvaloniaProperty.Register<PhotoViewer, ImageState>(nameof(State), ImageState.Ready);
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<PhotoViewer, double>(nameof(Progress), double.NaN);
    public static readonly StyledProperty<Bitmap?> StandInProperty = AvaloniaProperty.Register<PhotoViewer, Bitmap?>(nameof(StandIn));
    public static readonly StyledProperty<Turn> StandInTurnProperty = AvaloniaProperty.Register<PhotoViewer, Turn>(nameof(StandInTurn));
    public static readonly StyledProperty<string> ProcessingLabelProperty = AvaloniaProperty.Register<PhotoViewer, string>(nameof(ProcessingLabel), "Processing image");
    public static readonly StyledProperty<FontFamily?> MonoFontProperty = AvaloniaProperty.Register<PhotoViewer, FontFamily?>(nameof(MonoFont));

    static readonly IBrush NeutralFill = new SolidColorBrush(Color.Parse("#4c4c4c"));

    readonly TileCache[] _caches;
    readonly ConcurrentQueue<TileImage> _arrivals = new();
    readonly List<ViewerOverlay> _overlays = new();
    readonly ProcessingIndicator _indicator = new();
    readonly long[] _lastSignature = new long[2];
    readonly List<(TileKey Slot, Rect SlotImage, TileCache.Entry? Single, List<TileCache.Entry>? Several)> _slots = new();
    int _frameSlots, _frameSlotsReady;
    ITileSource? _source;
    Pyramid? _pyramid;
    long _geometryVersion;
    int _arrivalPosted, _changedPosted;
    bool _requestPosted, _framePending, _interactive;
    long _frame, _requestId;
    double _scale = 1;
    Vector _offset;
    ZoomMode _mode = ZoomMode.Fit;
    Size _lastViewport;
    PixelSize _lastImage;
    // pointer
    ViewerOverlay? _captured;
    bool _panning, _spaceHeld;
    Point _panStart;
    Vector _offsetStart;
    double _pinchStartScale;
    // zoom animation
    bool _animating;
    double _animFrom, _animTo;
    Point _animAnchor;
    long _animStart;
    // processing blur and the picture held through a geometry change
    RenderTargetBitmap? _blur;
    int _coarseChanges, _blurBuiltAt = -1;
    (Bitmap Bitmap, Turn Turn, NormRect Source, NormRect Target)? _hold;
    (Turn Turn, NormRect Source, NormRect Target)? _expected;
    bool _lastLocks;

    static PhotoViewer()
    {
        AffectsRender<PhotoViewer>(BackgroundProperty, CompareProperty, SplitPositionProperty, StateProperty, ProgressProperty, StandInProperty, StandInTurnProperty);
        FocusableProperty.OverrideDefaultValue<PhotoViewer>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PhotoViewer>(true);
    }

    public PhotoViewer()
    {
        _caches = new[] { new TileCache(256L << 20), new TileCache(128L << 20) };
        Split = new SplitOverlay();
        AddOverlay(Split);
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(Gestures.PinchEvent, OnPinch);
        AddHandler(Gestures.PinchEndedEvent, (_, _) => _pinchStartScale = 0);
        AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, OnMagnify);
    }

    // ============================================================================================================ properties

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public CompareMode Compare { get => GetValue(CompareProperty); set => SetValue(CompareProperty, value); }
    /// <summary>The divider of the split comparison, 0–1 across the photo.</summary>
    public double SplitPosition { get => GetValue(SplitPositionProperty); set => SetValue(SplitPositionProperty, value); }
    public ImageState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    /// <summary>A picture to show before any tile arrives (the thumbnail), drawn over the whole photo area.</summary>
    public Bitmap? StandIn { get => GetValue(StandInProperty); set => SetValue(StandInProperty, value); }
    /// <summary>How the stand-in must be turned to match the picture as shown.</summary>
    public Turn StandInTurn { get => GetValue(StandInTurnProperty); set => SetValue(StandInTurnProperty, value); }
    public string ProcessingLabel { get => GetValue(ProcessingLabelProperty); set => SetValue(ProcessingLabelProperty, value); }
    public FontFamily? MonoFont { get => GetValue(MonoFontProperty); set => SetValue(MonoFontProperty, value); }

    /// <summary>Tile memory of the edited channel, bytes (the original's gets half).</summary>
    public long TileBudget
    {
        get => _caches[0].BudgetBytes;
        set { _caches[0].BudgetBytes = value; _caches[1].BudgetBytes = value / 2; }
    }

    /// <summary>Smooth zoom steps (off in tests).</summary>
    public bool AnimateZoom { get; set; } = true;
    /// <summary>Space kept around the fitted picture, view units (the stage shows around it).</summary>
    public double FitMargin { get; set; } = 16;
    Size FitViewport => new(Math.Max(1, Viewport.Width - 2 * FitMargin), Math.Max(1, Viewport.Height - 2 * FitMargin));
    double FitScaleNow => ViewerMath.FitScale(FitViewport, ImageSizeD);
    /// <summary>Overrides the window's render scaling (tests of high-DPI maths).</summary>
    public double? RenderScalingOverride { get; set; }
    /// <summary>Level bias: positive values ask for coarser levels (faster, softer).</summary>
    public double LevelBias { get; set; }
    public ViewerStats Stats { get; } = new();
    /// <summary>The tile-swap policy's thresholds (settings).</summary>
    public TileSwapSettings Swap { get; } = new();
    public SplitOverlay Split { get; }
    public IReadOnlyList<ViewerOverlay> Overlays => _overlays;
    public TileCache CacheOf(int channel) => _caches[channel];
    public Pyramid? Pyramid => _pyramid;
    /// <summary>Whether the old picture is being held through a geometry change (tests).</summary>
    public bool IsHolding => _hold != null;

    public ITileSource? Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            if (_source != null) { _source.TileReady -= OnTileReady; _source.Changed -= OnSourceChanged; _source.Revalidated -= OnRevalidated; }
            _source = value;
            DropTiles();
            _hold = null; _expected = null;
            if (_source != null)
            {
                _source.TileReady += OnTileReady;
                _source.Changed += OnSourceChanged;
                _source.Revalidated += OnRevalidated;
                _pyramid = Pyramid.Of(_source);
                _geometryVersion = _source.GeometryVersion;
            }
            else _pyramid = null;
            _mode = ZoomMode.Fit;
            _animating = false;
            UpdateView();
        }
    }

    void DropTiles()
    {
        foreach (var c in _caches) { c.Clear(); c.KeepVersion = 0; }
        Array.Clear(_lastRevalid);
        Array.Clear(_shown);
        Array.Clear(_pendingSince);
        while (_arrivals.TryDequeue(out var stale)) stale.Bitmap.Dispose();
        _blur = null; _blurBuiltAt = -1; _coarseChanges = 0;
        Array.Clear(_lastSignature);
    }

    public double RenderScaling => RenderScalingOverride ?? (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    public double Scale => _scale;
    public Vector Offset => _offset;
    public ZoomMode Mode => _mode;
    /// <summary>Zoom in percent (physical pixels per image pixel × 100).</summary>
    public double ZoomPercent => ViewerMath.Percent(_scale, RenderScaling);
    public bool IsFitted => _mode == ZoomMode.Fit;
    /// <summary>The finest level of the current version drawn in the last frame (−1 = none yet).</summary>
    public int LevelOnScreen { get; private set; } = -1;
    /// <summary>The visible slots of the target level in the last frame (the main channel) and how many of them show their own tile at the current
    /// version — the render progress the page reports ("Showing 13 MP → Full detail · 60 %"); equal counts = the requested picture is wholly on
    /// screen.</summary>
    public int TargetSlots { get; private set; }
    public int TargetSlotsReady { get; private set; }
    /// <summary>Every visible slot shows its own target-level tile at the current version (and the view is not processing).</summary>
    public bool TargetComplete => TargetSlots > 0 && TargetSlotsReady == TargetSlots && State != ImageState.Processing;
    /// <summary>The level the zoom asks for now.</summary>
    public int TargetLevel => _source == null || _pyramid == null ? -1 : _pyramid.LevelFor(_scale * RenderScaling, _source.FinestLevel, LevelBias);
    /// <summary>Whether the view is locked by an overlay (crop mode).</summary>
    public bool LocksView => _overlays.Any(o => o.IsVisible && o.LocksView);

    /// <summary>Zoom, pan, level on screen or fit changed.</summary>
    public event Action? ViewChanged;
    /// <summary>Real pointer, wheel or key activity (the page tells the job queue the user is working).</summary>
    public event Action? Interacted;

    // ============================================================================================================ overlays

    public void AddOverlay(ViewerOverlay overlay)
    {
        overlay.Viewer = this;
        _overlays.Add(overlay);
        _overlays.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        OverlayVisibilityChanged();
    }

    public void RemoveOverlay(ViewerOverlay overlay)
    {
        if (!_overlays.Remove(overlay)) return;
        overlay.Viewer = null;
        if (_captured == overlay) _captured = null;
        OverlayVisibilityChanged();
    }

    internal void OverlayVisibilityChanged()
    {
        bool locks = LocksView;
        if (locks == _lastLocks) { InvalidateVisual(); return; }
        _lastLocks = locks;
        if (locks) { _mode = ZoomMode.Fit; _animating = false; }
        UpdateView();
    }

    // ============================================================================================================ geometry

    Size Viewport => Bounds.Size;
    Size ImageSizeD => _pyramid == null ? default : new Size(_pyramid.Image.Width, _pyramid.Image.Height);

    /// <summary>The coordinate systems now; null without a source or a size.</summary>
    public ViewTransform? Transform()
    {
        if (_pyramid == null || Viewport.Width <= 0 || Viewport.Height <= 0) return null;
        return new ViewTransform(_pyramid.Image, _scale, _offset, RenderScaling);
    }

    /// <summary>Recompute the zoom and offset after a change: fitted / filled views follow the view's size; a new picture size (a turn, a
    /// crop, crop mode) fits again; a free zoom is clamped to its range and the picture kept in view.</summary>
    void UpdateView()
    {
        var display = ImageSizeD;
        var vp = Viewport;
        if (_pyramid == null || vp.Width <= 0 || display.Width <= 0) { InvalidateVisual(); return; }
        bool sizeChanged = _pyramid.Image != _lastImage;
        _lastImage = _pyramid.Image;
        _lastViewport = vp;
        if (LocksView || (sizeChanged && _mode == ZoomMode.Free)) _mode = ZoomMode.Fit;
        if (_mode != ZoomMode.Free)
        {
            _scale = _mode == ZoomMode.Fill ? ViewerMath.FillScale(vp, display) : FitScaleNow;
            _offset = new Vector((vp.Width - display.Width * _scale) / 2, (vp.Height - display.Height * _scale) / 2);
        }
        else
        {
            var (min, max) = ScaleRange;
            _scale = Math.Clamp(_scale, min, max);
        }
        _offset = ViewerMath.ClampOffset(_offset, _scale, display, vp);
        ScheduleRequest();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_mode == ZoomMode.Free && _lastViewport.Width > 0)
            _offset += new Vector((e.NewSize.Width - _lastViewport.Width) / 2, (e.NewSize.Height - _lastViewport.Height) / 2);   // keep the centre
        UpdateView();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompareProperty) { Split.IsVisible = Compare == CompareMode.Split; Array.Clear(_lastSignature); ScheduleRequest(); }
        else if (change.Property == StateProperty) { _framePending = false; if (State == ImageState.Processing) BuildBlur(); InvalidateVisual(); }
    }

    /// <summary>
    /// The page is about to change the picture's geometry: hold the current picture, turned by <paramref name="turn"/>, drawing its region
    /// <paramref name="source"/> (normalised in the old picture) into <paramref name="target"/> (normalised in the new one), until the new
    /// picture's coarsest level is in. Rotate right = (RotateRight, full, full); apply a crop = (identity, the frame, full); open the crop
    /// tool = (identity, full, the saved crop in the uncropped picture).
    /// </summary>
    public void ExpectGeometryChange(Turn turn, NormRect? source = null, NormRect? target = null)
    {
        _expected = (turn, source ?? NormRect.Full, target ?? NormRect.Full);
        _expectedAt = Stopwatch.GetTimestamp();
    }

    long _expectedAt;

    void OnGeometryChanged()
    {
        Stats.GeometryResets++;
        // hold the old coarse picture through the change when the page said how it maps (a warning older than 3 s is stale); a second
        // change before the new picture arrives (two operations in a row) keeps the picture already held
        var expected = _expected is { } e && Stopwatch.GetElapsedTime(_expectedAt).TotalSeconds < 3 ? e
            : _source?.TurnFromPrevious is { } turn ? (turn, NormRect.Full, NormRect.Full) : ((Turn, NormRect, NormRect)?)null;
        if (expected is { } ex && ComposeCoarse(fullResolution: true) is { } held) _hold = (held, ex.Item1, ex.Item2, ex.Item3);
        else if (expected == null || _hold == null) _hold = null;
        _expected = null;
        DropTiles();
        _pyramid = Pyramid.Of(_source!);
        _geometryVersion = _source!.GeometryVersion;
        UpdateView();
    }

    // ============================================================================================================ zoom and pan (public)

    public void ZoomToFit() { _animating = false; _mode = ZoomMode.Fit; UpdateView(); }
    public void ZoomToFill() { if (LocksView) return; _animating = false; _mode = ZoomMode.Fill; UpdateView(); }

    /// <summary>1:1 — one image pixel per physical pixel — about <paramref name="anchor"/> (default: the view's centre).</summary>
    public void ZoomToActual(Point? anchor = null, bool? animate = null) => ZoomToScale(1 / RenderScaling, anchor, animate);

    /// <summary>Zoom to <paramref name="percent"/> (physical) about <paramref name="anchor"/>.</summary>
    public void ZoomToPercent(double percent, Point? anchor = null, bool? animate = null) => ZoomToScale(percent / 100 / RenderScaling, anchor, animate);

    public void ZoomBy(double factor, Point? anchor = null, bool? animate = null) => ZoomToScale((_animating ? _animTo : _scale) * factor, anchor, animate);

    /// <summary>Double-click / Z: fitted ↔ 1:1 about the pointer.</summary>
    public void ToggleFitActual(Point? anchor = null)
    {
        if (_mode == ZoomMode.Fit) ZoomToActual(anchor);
        else ZoomToFit();
    }

    /// <summary>The zoom range now, view units per image pixel.</summary>
    public (double Min, double Max) ScaleRange => ViewerMath.ScaleRange(FitViewport, ImageSizeD, RenderScaling);
    public (double Min, double Max) PercentRange
    {
        get { var (a, b) = ScaleRange; return (ViewerMath.Percent(a, RenderScaling), ViewerMath.Percent(b, RenderScaling)); }
    }

    public void ZoomToScale(double scale, Point? anchor = null, bool? animate = null)
    {
        if (_pyramid == null || LocksView) return;
        var (min, max) = ScaleRange;
        scale = Math.Clamp(scale, min, max);
        var a = anchor ?? new Point(Viewport.Width / 2, Viewport.Height / 2);
        MarkInteractive();
        if (animate ?? AnimateZoom)
        {
            _animFrom = _scale; _animTo = scale; _animAnchor = a; _animStart = Stopwatch.GetTimestamp();
            if (!_animating) { _animating = true; RequestFrame(); }
            return;
        }
        _animating = false;
        ApplyScale(scale, a);
    }

    void ApplyScale(double scale, Point anchor)
    {
        _offset = ViewerMath.ZoomAbout(_scale, _offset, scale, anchor);
        _scale = scale;
        var display = ImageSizeD;
        _mode = Math.Abs(scale - FitScaleNow) < 1e-9 ? ZoomMode.Fit : ZoomMode.Free;
        if (_mode == ZoomMode.Free) _offset = ViewerMath.ClampOffset(_offset, _scale, display, Viewport);
        UpdateView();
    }

    /// <summary>Move the picture by <paramref name="delta"/> view units (positive = the picture moves right / down).</summary>
    public void PanBy(Vector delta)
    {
        if (_pyramid == null || LocksView) return;
        _offset += delta;
        if (_mode != ZoomMode.Free) _mode = ZoomMode.Free;
        MarkInteractive();
        UpdateView();
    }

    /// <summary>Centre the picture without changing the zoom.</summary>
    public void CenterImage()
    {
        var display = ImageSizeD;
        _offset = new Vector((Viewport.Width - display.Width * _scale) / 2, (Viewport.Height - display.Height * _scale) / 2);
        UpdateView();
    }

    /// <summary>Whether the picture is larger than the view (arrow keys pan then; at fit they step through captures).</summary>
    public bool CanPan
    {
        get
        {
            var d = ImageSizeD;
            return !LocksView && (d.Width * _scale > Viewport.Width + 0.5 || d.Height * _scale > Viewport.Height + 0.5);
        }
    }

    /// <summary>Finish a running zoom animation at once (tests).</summary>
    public void CompleteAnimation()
    {
        if (!_animating) return;
        _animating = false;
        ApplyScale(_animTo, _animAnchor);
    }

    void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else CompleteAnimation();
    }

    void OnFrame(TimeSpan _)
    {
        if (!_animating) return;
        double t = Math.Clamp(Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds / 140.0, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        double s = Math.Exp(Math.Log(_animFrom) + (Math.Log(_animTo) - Math.Log(_animFrom)) * e);
        if (t >= 1) { _animating = false; s = _animTo; }
        ApplyScale(s, _animAnchor);
        if (_animating) RequestFrame();
    }

    DispatcherTimer? _interactiveTimer;

    /// <summary>The user pans or zooms: requests say so until a quarter second after the last move (the source then skips redoing the
    /// coarse picture and tells the job queue), and the view asks once more when it settles.</summary>
    void MarkInteractive()
    {
        _interactive = true;
        if (_interactiveTimer == null)
        {
            _interactiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _interactiveTimer.Tick += (_, _) =>
            {
                _interactiveTimer!.Stop();
                _interactive = false;
                Array.Clear(_lastSignature);
                ScheduleRequest();
            };
        }
        _interactiveTimer.Stop();
        _interactiveTimer.Start();
    }

    // ============================================================================================================ requests and arrivals

    void ScheduleRequest()
    {
        if (_requestPosted) return;
        _requestPosted = true;
        Dispatcher.UIThread.Post(() => { _requestPosted = false; RequestNow(); }, DispatcherPriority.Normal);
    }

    /// <summary>Ask the source for what the view needs now (normally posted once per UI turn after a change).</summary>
    public void RequestNow()
    {
        if (_source == null || _pyramid == null || Transform() is not { } t) return;
        var bounds = new Rect(Viewport);
        foreach (var (channel, clip, _) in ChannelsFor(t, bounds, forRequest: true))
            RequestChannel(t, channel, clip);
    }

    void RequestChannel(ViewTransform t, int channel, Rect clip)
    {
        var src = _source!;
        var p = _pyramid!;
        if (channel == Lux.App.Viewer.Channels.Before && !src.HasBefore) return;
        int level = p.LevelFor(t.PhysicalScale, src.FinestLevel, LevelBias);
        var vis = ViewerMath.VisibleImageRect(t, clip);
        var cache = _caches[channel];
        long cur = src.Version(channel);
        var needed = new List<TileKey>();
        // the coarsest level, whole: the base that is always on screen (and the blurred stand-in)
        foreach (var k in p.TilesIn(p.Coarsest, p.ImageRect))
            if (cache.Get(k) is not { } e || e.Version < cur) needed.Add(k);
        if (level < p.Coarsest)
            foreach (var k in p.TilesIn(level, vis))
                if (cache.Get(k) is not { } e || e.Version < cur) needed.Add(k);
        // the same level, the same needs at the same version: nothing new to say
        long sig = Signature(needed, cur, level, src.GeometryVersion);
        if (_lastSignature[channel] == sig) return;
        _lastSignature[channel] = sig;
        Stats.Requests++;
        var screen = new Size(vis.Width * t.PhysicalScale, vis.Height * t.PhysicalScale);
        src.Request(new TileRequest(channel, level, vis, needed, ++_requestId) { ScreenPixels = screen, Interactive = _interactive || _panning });
    }

    static long Signature(List<TileKey> keys, long version, int level, long geometry)
    {
        long h = version * 1_000_003 + level * 7 + geometry * 99_991;
        foreach (var k in keys) h = h * 31 + (k.Level * 7919L + k.Col) * 104729L + k.Row;
        return h == 0 ? 1 : h;
    }

    void OnTileReady(TileImage tile)
    {
        _arrivals.Enqueue(tile);
        if (Interlocked.Exchange(ref _arrivalPosted, 1) == 0)
            Dispatcher.UIThread.Post(ApplyArrivals, DispatcherPriority.Normal);
    }

    /// <summary>Take the queued tiles into the caches (UI thread; normally posted once per batch).</summary>
    public void ApplyArrivals()
    {
        Interlocked.Exchange(ref _arrivalPosted, 0);
        var src = _source;
        bool any = false;
        if (src != null && src.GeometryVersion != _geometryVersion) OnGeometryChanged();
        while (_arrivals.TryDequeue(out var t))
        {
            if (src == null || _pyramid == null || t.Channel is < 0 or > 1 || t.GeometryVersion != _geometryVersion || t.Level >= _pyramid.LevelCount)
            {
                t.Bitmap.Dispose();
                continue;
            }
            if (_caches[t.Channel].Add(t, src.Floor(t.Channel), _frame))
            {
                any = true;
                if (t.Level == _pyramid.Coarsest && t.Channel == Lux.App.Viewer.Channels.After) _coarseChanges++;
            }
        }
        if (!any) return;
        if (_hold != null && HasWholeCoarse(Lux.App.Viewer.Channels.After)) _hold = null;
        if (State == ImageState.Processing) BuildBlur();
        InvalidateVisual();
    }

    bool HasWholeCoarse(int channel)
    {
        var p = _pyramid;
        if (p == null) return false;
        foreach (var k in p.TilesIn(p.Coarsest, p.ImageRect)) if (_caches[channel].Get(k) == null) return false;
        return true;
    }

    readonly ConcurrentQueue<TileRevalidation> _revalidations = new();
    readonly long[] _lastRevalid = new long[2];
    int _revalidatePosted;
    /// <summary>Held tiles kept valid by revalidations (tests, measurements).</summary>
    public int TilesKeptByRegions { get; private set; }

    void OnRevalidated(TileRevalidation r)
    {
        _revalidations.Enqueue(r);
        if (Interlocked.Exchange(ref _revalidatePosted, 1) == 0) Dispatcher.UIThread.Post(ApplyRevalidations, DispatcherPriority.Normal);
    }

    /// <summary>Raise the version of held tiles that meet no changed region of their level (they stay on screen and are not asked again).</summary>
    public void ApplyRevalidations()
    {
        Interlocked.Exchange(ref _revalidatePosted, 0);
        bool any = false;
        while (_revalidations.TryDequeue(out var r))
        {
            if (_pyramid is not { } p || _source is not { } src || r.GeometryVersion != _geometryVersion || r.Channel is < 0 or > 1) continue;
            var img = p.Image;
            // only tiles that saw every earlier change: at least the previous reported version and the source's floor (a tone change raises it)
            long eligible = Math.Max(_lastRevalid[r.Channel], src.Floor(r.Channel));
            _lastRevalid[r.Channel] = Math.Max(_lastRevalid[r.Channel], r.Version);
            foreach (var e in _caches[r.Channel].Entries)
            {
                if (e.Version >= r.Version || e.Version < eligible) continue;
                var rect = p.TileImageRect(e.Key);
                double x0 = rect.X / img.Width, y0 = rect.Y / img.Height, x1 = rect.Right / img.Width, y1 = rect.Bottom / img.Height;
                bool hit = false;
                foreach (var (level, c) in r.Changed)
                    if ((level < 0 || level == e.Key.Level) && c.X < x1 && c.Right > x0 && c.Y < y1 && c.Bottom > y0) { hit = true; break; }
                if (hit) continue;
                e.Version = r.Version;
                TilesKeptByRegions++;
                any = true;
            }
        }
        if (!any) return;
        Array.Clear(_lastSignature);
        ScheduleRequest();
        InvalidateVisual();
    }

    void OnSourceChanged()
    {
        if (Interlocked.Exchange(ref _changedPosted, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _changedPosted, 0);
                if (_source == null) return;
                if (_source.GeometryVersion != _geometryVersion) { OnGeometryChanged(); return; }
                Array.Clear(_lastSignature);
                UpdateView();
            }, DispatcherPriority.Normal);
    }

    // ============================================================================================================ drawing

    /// <summary>The channels to draw (or request) and their clip in view coordinates.</summary>
    IEnumerable<(int Channel, Rect Clip, bool Draw)> ChannelsFor(ViewTransform t, Rect bounds, bool forRequest)
    {
        var photo = t.DisplayRect.Intersect(bounds);
        bool before = _source?.HasBefore == true;
        switch (before ? Compare : CompareMode.Off)
        {
            case CompareMode.Split:
            {
                double x = t.DisplayRect.X + t.DisplayRect.Width * Math.Clamp(SplitPosition, 0, 1);
                yield return (Lux.App.Viewer.Channels.Before, new Rect(photo.X, photo.Y, Math.Max(0, x - photo.X), photo.Height), true);
                yield return (Lux.App.Viewer.Channels.After, new Rect(x, photo.Y, Math.Max(0, photo.Right - x), photo.Height), true);
                break;
            }
            case CompareMode.ToggleAfter:
                yield return (Lux.App.Viewer.Channels.After, photo, true);
                if (forRequest) yield return (Lux.App.Viewer.Channels.Before, photo, false);
                break;
            case CompareMode.ToggleBefore:
                yield return (Lux.App.Viewer.Channels.Before, photo, true);
                if (forRequest) yield return (Lux.App.Viewer.Channels.After, photo, false);
                break;
            default:
                yield return (Lux.App.Viewer.Channels.After, photo, true);
                break;
        }
    }

    public override void Render(DrawingContext context)
    {
        long start = Stopwatch.GetTimestamp();
        _frame++;
        var bounds = new Rect(Bounds.Size);
        if (Background is { } bg) context.FillRectangle(bg, bounds);
        var t = Transform();
        int drawn = 0;
        bool gaps = false;
        if (t == null)
        {
            // no source yet (opening): the stand-in or a neutral 4:3 area
            var size = StandIn is { } si ? StandInTurn.Apply(si.Size) : new Size(4, 3);
            double s = ViewerMath.FitScale(bounds.Size, size);
            var r = new Rect((bounds.Width - size.Width * s) / 2, (bounds.Height - size.Height * s) / 2, size.Width * s, size.Height * s);
            DrawStandIn(context, r, blurred: State == ImageState.Processing);
            if (State == ImageState.Processing) DrawProcessing(context, r);
            Record(start, 0, false);
            return;
        }

        var photo = t.DisplayRect;
        if (State == ImageState.Processing)
        {
            using (context.PushClip(photo.Intersect(bounds)))
            {
                if (_blur != null)
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        context.DrawImage(_blur, new Rect(_blur.Size), photo);
                else DrawStandIn(context, photo, blurred: true);
            }
            DrawProcessing(context, photo);
            Record(start, 0, false);
            return;
        }

        int finest = int.MaxValue;
        _frameSlots = 0; _frameSlotsReady = 0;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality, EdgeMode = EdgeMode.Aliased }))
        {
            foreach (var (channel, clip, draw) in ChannelsFor(t, bounds, forRequest: false))
            {
                if (!draw || clip.Width <= 0 || clip.Height <= 0) continue;
                using (context.PushClip(clip))
                {
                    var (n, f, g) = DrawChannel(context, t, channel, clip);
                    drawn += n;
                    gaps |= g;
                    if (channel == Lux.App.Viewer.Channels.After || Compare == CompareMode.ToggleBefore) finest = Math.Min(finest, f);
                }
            }
        }
        foreach (var o in _overlays)
            if (o.IsVisible) o.Render(context, t, bounds);

        int onScreen = finest == int.MaxValue ? -1 : finest;
        if (onScreen != LevelOnScreen || _frameSlots != TargetSlots || _frameSlotsReady != TargetSlotsReady)
        {
            LevelOnScreen = onScreen; TargetSlots = _frameSlots; TargetSlotsReady = _frameSlotsReady;
            Dispatcher.UIThread.Post(() => ViewChanged?.Invoke(), DispatcherPriority.Background);
        }
        foreach (var c in _caches) c.Trim(_frame);
        Record(start, drawn, gaps);
    }

    void Record(long start, int drawn, bool gaps)
    {
        Stats.RenderMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (Stats.RenderMs.Count > 20000) Stats.RenderMs.RemoveRange(0, 10000);
        Stats.TilesDrawnLastFrame = drawn;
        Stats.MaxTilesDrawn = Math.Max(Stats.MaxTilesDrawn, drawn);
        if (gaps) Stats.FramesWithGaps++;
    }

    // ---- the tile-swap policy: per channel, the version fully on screen and since when a newer one is coming
    readonly long[] _shown = new long[2];
    readonly bool[] _coarseCur = new bool[2];
    readonly long[] _pendingSince = new long[2];

    enum SwapState { Normal, Hold, Progressive }
    /// <summary>Diagnostics: one line per frame and channel while a newer version is pending (null = off).</summary>
    public List<string>? SwapTrace { get; set; }

    /// <summary>
    /// Decide how a channel is drawn this frame. Nothing shown yet (a new picture, a new geometry): normal. A newer version than the one on
    /// screen: while dragging (the floor stays below the version), hold the picture until every visible slot has a newer tile, then swap;
    /// after a committed change, swap as soon as every slot has its tile at the current version, hold until the window passes, then progressive.
    /// </summary>
    SwapState SwapFor(int channel, ViewTransform t, Rect clip)
    {
        var src = _source!;
        var p = _pyramid!;
        var cache = _caches[channel];
        long cur = src.Version(channel), floor = src.Floor(channel);
        if (!Swap.Enabled) { cache.KeepVersion = 0; return SwapState.Normal; }
        int level = p.LevelFor(t.PhysicalScale, src.FinestLevel, LevelBias);
        var vis = ViewerMath.VisibleImageRect(t, clip);
        bool complete = true, targetComplete = true, newerEverywhere = true;
        long newest = 0;
        // the whole coarse picture at the new version (the library sends it first, ~0.1–0.2 s after a change)
        bool coarseCur = true;
        foreach (var k in p.TilesIn(p.Coarsest, p.ImageRect)) if (cache.Get(k) is not { } ce || ce.Version != cur) { coarseCur = false; break; }
        _coarseCur[channel] = coarseCur;
        foreach (var slot in p.TilesIn(level, vis))
        {
            var slotImg = p.TileImageRect(slot);
            if (cache.Get(slot) is not { } own || own.Version != cur) targetComplete = false;
            var (single, several, ok) = Find(cache, p, slot, slotImg, cur, floor, null);
            if (!ok) { complete = false; newerEverywhere = false; continue; }
            long v = single?.Version ?? several!.Min(e => e.Version);
            if (v != cur) complete = false;
            // newer than the picture on screen at some level (a drag's coarser tiles count)
            long shownNow = _shown[channel];
            var (ns, nm, nok) = Find(cache, p, slot, slotImg, cur, shownNow + 1, k => cache.Get(k) is { } e && e.Version > shownNow ? e : null);
            if (!nok) newerEverywhere = false;
            else newest = Math.Max(newest, ns?.Version ?? nm!.Max(e => e.Version));
        }
        if (_shown[channel] == 0)
        {
            if (complete) { _shown[channel] = cur; cache.KeepVersion = cur; }
            return SwapState.Normal;
        }
        if (cur <= _shown[channel]) { _pendingSince[channel] = 0; return SwapState.Normal; }
        if (_pendingSince[channel] == 0) _pendingSince[channel] = Stopwatch.GetTimestamp();
        bool dragging = floor < cur;
        SwapTrace?.Add($"cur {cur} floor {floor} shown {_shown[channel]} complete {complete} target {targetComplete} newer {newerEverywhere} newest {newest} level {level}");
        // a drag shows its coarser drag-level picture as a whole (or, when its renders fall behind the window, the newest it has); a committed
        // change waits for the target level itself (or the window)
        bool late = Stopwatch.GetElapsedTime(_pendingSince[channel]).TotalMilliseconds >= Swap.WindowMs;
        if (dragging ? complete || newerEverywhere || late : targetComplete || (complete && level == p.Coarsest))
        {
            _shown[channel] = complete ? cur : Math.Max(newest, _shown[channel] + 1);
            _pendingSince[channel] = 0;
            cache.ReleasePrevious();
            cache.KeepVersion = _shown[channel];
            Stats.Swaps++;
            return SwapState.Normal;
        }
        if (dragging || !late) { KeepAsking(); return SwapState.Hold; }
        return SwapState.Progressive;
    }

    bool _askPosted;

    /// <summary>A held or fading picture needs frames even when no tile arrives (the window runs out, a fade goes on).</summary>
    void KeepAsking()
    {
        if (_askPosted || TopLevel.GetTopLevel(this) is not { } top) return;
        _askPosted = true;
        top.RequestAnimationFrame(_ => { _askPosted = false; InvalidateVisual(); });
    }

    /// <summary>The tile of the generation on screen for a slot (the new one when none was kept: better than a gap).</summary>
    TileCache.Entry? Held(TileCache cache, TileKey key, long shown) => cache.Get(key) is { } e ? (e.Version <= shown ? e : e.Previous ?? e) : null;

    /// <summary>
    /// Draw one channel's visible slots at the target level. Per slot, in this order: its own tile at the current version; else the coarser
    /// tiles covering it, all at the current version (finest level first); the same two at a version the source still finds worth showing;
    /// the same two at any version; else whatever coarser tiles are held (a gap remains only before the coarsest level has arrived — the
    /// stand-in or the held picture is drawn underneath then). Returns the tiles drawn, the finest current level drawn, and whether a gap
    /// remained.
    /// </summary>
    (int Drawn, int Finest, bool Gaps) DrawChannel(DrawingContext context, ViewTransform t, int channel, Rect clip)
    {
        var src = _source!;
        var p = _pyramid!;
        var cache = _caches[channel];
        int level = p.LevelFor(t.PhysicalScale, src.FinestLevel, LevelBias);
        long cur = src.Version(channel), floor = src.Floor(channel);
        var vis = ViewerMath.VisibleImageRect(t, clip);
        int drawn = 0, finest = int.MaxValue;
        bool gaps = false;
        long now = Stopwatch.GetTimestamp();
        var swap = SwapFor(channel, t, clip);
        long shown = _shown[channel];
        Func<TileKey, TileCache.Entry?>? held = swap == SwapState.Hold ? k => Held(cache, k, shown) : null;
        if (swap == SwapState.Hold) Stats.HeldFrames++;
        _slots.Clear();
        long minV = long.MaxValue, maxV = long.MinValue;
        foreach (var slot in p.TilesIn(level, vis))
        {
            var slotImg = p.TileImageRect(slot);
            (TileCache.Entry? single, List<TileCache.Entry>? several, bool complete) found;
            if (held != null) found = Find(cache, p, slot, slotImg, shown, Math.Min(floor, shown), held);
            else if (swap == SwapState.Progressive && !(cache.Get(slot) is { } own && own.Version == cur))
                // not here yet: the new version's coarse picture when it is whole (the change shows at once), else the old picture
                found = _coarseCur[channel] ? Find(cache, p, slot, slotImg, cur, floor, k => k == slot ? null : cache.Get(k))
                    : Find(cache, p, slot, slotImg, shown, Math.Min(floor, shown), k => Held(cache, k, shown));
            else found = Find(cache, p, slot, slotImg, cur, floor, null);
            var (single, several, complete) = found;
            gaps |= !complete;
            if (channel == Lux.App.Viewer.Channels.After || Compare == CompareMode.ToggleBefore)
            {
                _frameSlots++;
                if (cache.Get(slot) is { } mine && mine.Version == cur) _frameSlotsReady++;
            }
            _slots.Add((slot, slotImg, single, several));
            if (single != null) { minV = Math.Min(minV, single.Version); maxV = Math.Max(maxV, single.Version); }
            else if (several != null) foreach (var e in several) { minV = Math.Min(minV, e.Version); maxV = Math.Max(maxV, e.Version); }
        }
        if (maxV > minV) Stats.MixedFrames++;
        // a slot not fully covered (only before the coarsest level arrives): the held picture or the stand-in underneath
        if (gaps)
        {
            if (_hold is { } h) DrawHold(context, t, h);
            else if (StandIn != null) DrawStandIn(context, t.DisplayRect, blurred: false);
        }
        foreach (var (slot, slotImg, single, several) in _slots)
        {
            if (single != null)
            {
                // progressive delivery: a tile newer than the picture on screen fades in over the one it replaces
                if (swap == SwapState.Progressive && single.Version > shown && (single.Previous != null || _coarseCur[channel]))
                {
                    double a = Swap.FadeMs <= 0 ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(single.AcceptedTicks, now).TotalMilliseconds / Swap.FadeMs, 0, 1);
                    if (a < 1)
                    {
                        // under it what was shown a moment ago: the new coarse picture, else the old tile
                        var under = _coarseCur[channel] ? Find(cache, p, slot, slotImg, cur, floor, k => k == slot ? null : cache.Get(k)) : (null, null, false);
                        if (under.Complete)
                        {
                            if (under.Single is { } us) DrawTilePart(context, t, p, us, slotImg);
                            else using (context.PushClip(ViewerMath.Snap(slotImg.TransformToAABB(t.ImageToView), t.RenderScaling)))
                                foreach (var ue in under.Several!) DrawTilePart(context, t, p, ue, slotImg);
                        }
                        else if (single.Previous is { } prev) DrawTilePart(context, t, p, prev, slotImg);
                        using (context.PushOpacity(a)) DrawTilePart(context, t, p, single, slotImg);
                        Stats.FadedTiles++;
                        KeepAsking();
                        drawn++;
                        Seen(cache, single, now);
                        continue;
                    }
                }
                DrawTilePart(context, t, p, single, slotImg);
                drawn++;
                Seen(cache, single, now);
                if (single.Version == cur) finest = Math.Min(finest, single.Key.Level);
                continue;
            }
            if (several == null) continue;
            using (context.PushClip(ViewerMath.Snap(slotImg.TransformToAABB(t.ImageToView), t.RenderScaling)))
                foreach (var e in several)
                {
                    DrawTilePart(context, t, p, e, slotImg);
                    drawn++;
                    Seen(cache, e, now);
                    if (e.Version == cur) finest = Math.Min(finest, e.Key.Level);
                }
        }
        return (drawn, finest, gaps);
    }

    void Seen(TileCache cache, TileCache.Entry e, long now)
    {
        cache.Touch(e, _frame);
        if (e.Drawn) return;
        e.Drawn = true;
        Stats.TileLatencyMs.Add(Stopwatch.GetElapsedTime(e.ReadyTicks, now).TotalMilliseconds);
        if (Stats.TileLatencyMs.Count > 20000) Stats.TileLatencyMs.RemoveRange(0, 10000);
    }

    readonly List<TileCache.Entry> _covering = new();

    (TileCache.Entry? Single, List<TileCache.Entry>? Several, bool Complete) Find(TileCache cache, Pyramid p, TileKey slot, Rect slotImg, long cur, long floor,
                                                                                 Func<TileKey, TileCache.Entry?>? get)
    {
        get ??= cache.Get;
        // a hair inside the slot, so tiles that only touch its edge are not counted
        var inner = slotImg.Deflate(0.01);
        for (int pass = 0; pass < 3; pass++)
        {
            bool Ok(TileCache.Entry? e) => e != null && (pass == 0 ? e.Version == cur : pass == 1 ? e.Version >= floor : true);
            var own = get(slot);
            if (Ok(own)) return (own, null, true);
            for (int k = slot.Level + 1; k <= p.Coarsest; k++)
            {
                _covering.Clear();
                bool all = true;
                foreach (var key in p.TilesIn(k, inner))
                {
                    var e = get(key);
                    if (!Ok(e)) { all = false; break; }
                    _covering.Add(e!);
                }
                if (all && _covering.Count > 0)
                    return _covering.Count == 1 ? (_covering[0], null, true) : (null, new List<TileCache.Entry>(_covering), true);
            }
        }
        // partial: whatever coarse tiles are held
        var partial = new List<TileCache.Entry>();
        foreach (var key in p.TilesIn(p.Coarsest, inner)) if (get(key) is { } e) partial.Add(e);
        return (null, partial.Count > 0 ? partial : null, false);
    }

    /// <summary>Draw the part of tile <paramref name="e"/> that lies in <paramref name="slotImg"/> (image pixels), snapped to device pixels so
    /// adjacent tiles meet without seams.</summary>
    static void DrawTilePart(DrawingContext context, ViewTransform t, Pyramid p, TileCache.Entry e, Rect slotImg)
    {
        var tileImg = p.TileImageRect(e.Key);
        var part = tileImg.Intersect(slotImg);
        if (part.Width <= 0 || part.Height <= 0) return;
        var (fx, fy) = p.ScaleOf(e.Key.Level);
        var tileLevel = p.TileLevelRect(e.Key);
        var srcRect = new Rect((part.X - tileImg.X) / fx, (part.Y - tileImg.Y) / fy, part.Width / fx, part.Height / fy)
            .Intersect(new Rect(0, 0, Math.Min(tileLevel.Width, e.Bitmap.PixelSize.Width), Math.Min(tileLevel.Height, e.Bitmap.PixelSize.Height)));
        if (srcRect.Width <= 0 || srcRect.Height <= 0) return;
        var dest = ViewerMath.Snap(part.TransformToAABB(t.ImageToView), t.RenderScaling);
        if (dest.Width <= 0 || dest.Height <= 0) return;
        context.DrawImage(e.Bitmap, srcRect, dest);
    }

    /// <summary>The stand-in over the photo area, turned to match; blurred while processing.</summary>
    void DrawStandIn(DrawingContext context, Rect r, bool blurred)
    {
        if (StandIn is not { } si) { context.FillRectangle(NeutralFill, r); return; }
        DrawTurned(context, blurred ? ImageStateView.BlurredOf(si) : si, null, r, StandInTurn);
    }

    /// <summary>The old picture held through a geometry change: its source region, turned, into the target region of the new picture.</summary>
    static void DrawHold(DrawingContext context, ViewTransform t, (Bitmap Bitmap, Turn Turn, NormRect Source, NormRect Target) h)
    {
        var b = h.Bitmap.Size;
        var src = new Rect(h.Source.X * b.Width, h.Source.Y * b.Height, h.Source.Width * b.Width, h.Source.Height * b.Height);
        var dest = t.FrameToView(h.Target);
        DrawTurned(context, h.Bitmap, src, dest, h.Turn);
    }

    /// <summary>Draw <paramref name="src"/> of <paramref name="bmp"/> (all of it when null) turned by <paramref name="turn"/> into
    /// <paramref name="dest"/> (view units).</summary>
    static void DrawTurned(DrawingContext context, Bitmap bmp, Rect? src, Rect dest, Turn turn)
    {
        double uw = turn.Swaps ? dest.Height : dest.Width, uh = turn.Swaps ? dest.Width : dest.Height;
        var m = turn.Matrix(uw, uh) * Matrix.CreateTranslation(dest.X, dest.Y);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        using (context.PushTransform(m))
            context.DrawImage(bmp, src ?? new Rect(bmp.Size), new Rect(0, 0, uw, uh));
    }

    void DrawProcessing(DrawingContext context, Rect photo)
    {
        context.FillRectangle(ProcessingIndicator.Scrim, photo);
        _indicator.Draw(context, photo, photo.Width >= 120 ? ProcessingLabel : null, Progress, Avalonia.Controls.Documents.TextElement.GetFontFamily(this), maxRadius: 20);
        if (!_framePending && TopLevel.GetTopLevel(this) is { } top)
        {
            _framePending = true;
            top.RequestAnimationFrame(_ => { _framePending = false; if (State == ImageState.Processing) InvalidateVisual(); });
        }
    }

    /// <summary>The label drawn by the processing indicator last (tests).</summary>
    public string? ProcessingText => _indicator.LastLabel;

    /// <summary>The coarsest level of the edited picture composed into one bitmap: small (a sixth) for the blurred stand-in, or at the level's
    /// own size for the picture held through a geometry change. Null until the level is complete.</summary>
    RenderTargetBitmap? ComposeCoarse(bool fullResolution)
    {
        var p = _pyramid;
        if (p == null) return null;
        var cache = _caches[Lux.App.Viewer.Channels.After];
        var keys = p.TilesIn(p.Coarsest, p.ImageRect).ToList();
        if (keys.Count == 0 || keys.Any(k => cache.Get(k) == null)) return null;
        var ls = p.Size(p.Coarsest);
        int w = fullResolution ? ls.Width : Math.Max(8, ls.Width / 6), h = fullResolution ? ls.Height : Math.Max(6, ls.Height / 6);
        var rtb = new RenderTargetBitmap(new PixelSize(w, h));
        using (var dc = rtb.CreateDrawingContext())
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
        {
            double sx = w / (double)ls.Width, sy = h / (double)ls.Height;
            foreach (var k in keys)
            {
                var e = cache.Get(k)!;
                var r = p.TileLevelRect(k);
                dc.DrawImage(e.Bitmap, new Rect(0, 0, Math.Min(r.Width, e.Bitmap.PixelSize.Width), Math.Min(r.Height, e.Bitmap.PixelSize.Height)),
                             new Rect(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy));
            }
        }
        return rtb;
    }

    /// <summary>A small composite of the coarsest level drawn large: the blurred best picture while a quality is being made.</summary>
    void BuildBlur()
    {
        if (_blurBuiltAt == _coarseChanges && _blur != null) return;
        if (ComposeCoarse(fullResolution: false) is { } b) { _blur = b; _blurBuiltAt = _coarseChanges; }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateView();
    }

    // ============================================================================================================ input

    ViewerPointer PointerOf(PointerEventArgs e, ViewTransform t, int clicks = 0)
    {
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        return new ViewerPointer(p, t.ToImage(p), t.ViewToFrame(p), e.KeyModifiers, props.IsLeftButtonPressed, props.IsRightButtonPressed, clicks);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Interacted?.Invoke();
        Focus();
        if (Transform() is not { } t) return;
        var props = e.GetCurrentPoint(this).Properties;
        var vp = PointerOf(e, t, e.ClickCount);
        if (!_spaceHeld && props.IsLeftButtonPressed)
        {
            for (int i = _overlays.Count - 1; i >= 0; i--)
            {
                var o = _overlays[i];
                if (o.IsVisible && o.OnPressed(vp, t)) { _captured = o; e.Pointer.Capture(this); e.Handled = true; return; }
            }
        }
        if (e.ClickCount == 2 && props.IsLeftButtonPressed && !LocksView) { ToggleFitActual(vp.View); e.Handled = true; return; }
        if ((props.IsLeftButtonPressed || props.IsMiddleButtonPressed) && !LocksView)
        {
            _panning = true;
            _animating = false;
            _panStart = vp.View;
            _offsetStart = _offset;
            e.Pointer.Capture(this);
            Cursor = Cursors.Get(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Transform() is not { } t) return;
        var vp = PointerOf(e, t);
        Hovered?.Invoke(t.DisplayRect.Contains(vp.View) ? vp : null);
        if (_captured != null) { _captured.OnMoved(vp, t); e.Handled = true; return; }
        if (!_panning)
            foreach (var o in _overlays)
                if (o.IsVisible) o.OnHover(vp.View, t);
        if (_panning)
        {
            Interacted?.Invoke();
            _offset = _offsetStart + (vp.View - _panStart);
            _mode = ZoomMode.Free;
            MarkInteractive();
            UpdateView();
            e.Handled = true;
            return;
        }
        Cursor? cursor = null;
        for (int i = _overlays.Count - 1; i >= 0 && cursor == null; i--)
            if (_overlays[i].IsVisible) cursor = _overlays[i].CursorAt(vp.View, t);
        Cursor = cursor ?? (CanPan ? Cursors.Get(StandardCursorType.Hand) : Avalonia.Input.Cursor.Default);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Transform() is { } t && _captured != null)
        {
            var o = _captured;
            _captured = null;
            o.OnReleased(PointerOf(e, t), t);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            Cursor = CanPan ? Cursors.Get(StandardCursorType.Hand) : Avalonia.Input.Cursor.Default;
            Array.Clear(_lastSignature);
            ScheduleRequest();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false;
        if (_captured is { } o && Transform() is { } t)
        {
            // a stroke cut short (the window lost the pointer): end it where it was
            _captured = null;
            o.OnReleased(new ViewerPointer(default, default, default, KeyModifiers.None, false, false, 0), t);
        }
        _captured = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hovered?.Invoke(null);
        if (Transform() is { } t)
            foreach (var o in _overlays)
                if (o.IsVisible) o.OnHover(null, t);
    }

    /// <summary>The pointer over the picture (null: off the picture or out of the viewer) — the status bar's x, y and depth read-out.</summary>
    public event Action<ViewerPointer?>? Hovered;

    // ============================================================================================================ view sync, shake

    /// <summary>The zoom and pan now (the other viewer of "Photo + depth" follows it).</summary>
    public (ZoomMode Mode, double Scale, Vector Offset) ViewState => (_mode, _scale, _offset);

    /// <summary>Take another viewer's zoom and pan (both show pictures of the same size): no animation, no interactive marker of its own.</summary>
    public void SetViewState((ZoomMode Mode, double Scale, Vector Offset) state)
    {
        if (_pyramid == null) return;
        _animating = false;
        _mode = state.Mode;
        if (_mode == ZoomMode.Free) { _scale = state.Scale; _offset = state.Offset; }
        UpdateView();
    }

    TranslateTransform? _shake;
    long _shakeStart;
    bool _shaking;

    /// <summary>Lumen's "anomaly": a short damped shake of the picture when a depth edit could not be made.</summary>
    public void Shake()
    {
        _shake ??= new TranslateTransform();
        RenderTransform = _shake;
        _shakeStart = Stopwatch.GetTimestamp();
        ShakeCount++;
        if (_shaking) return;
        _shaking = true;
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(ShakeFrame);
        else { _shaking = false; }
    }

    /// <summary>Shakes started (tests).</summary>
    public int ShakeCount { get; private set; }

    void ShakeFrame(TimeSpan _)
    {
        double t = Stopwatch.GetElapsedTime(_shakeStart).TotalMilliseconds / 450.0;
        if (t >= 1 || _shake == null) { if (_shake != null) _shake.X = 0; _shaking = false; return; }
        _shake.X = 9 * Math.Sin(t * Math.PI * 2 * 4) * (1 - t);
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(ShakeFrame);
        else { _shake.X = 0; _shaking = false; }
    }

    /// <summary>Wheel: zoom about the pointer; Shift + wheel pans sideways, Ctrl + wheel pans up and down; a sideways (touchpad) scroll pans.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Interacted?.Invoke();
        if (_pyramid == null || LocksView) return;
        var p = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) PanBy(new Vector(e.Delta.Y * 80, 0));
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) PanBy(new Vector(0, e.Delta.Y * 80));
        else if (Math.Abs(e.Delta.X) > 1e-6) PanBy(new Vector(e.Delta.X * 40, e.Delta.Y * 40));
        else ZoomBy(Math.Pow(1.25, e.Delta.Y), p);
        e.Handled = true;
    }

    void OnPinch(object? sender, PinchEventArgs e)
    {
        if (_pyramid == null || LocksView) return;
        Interacted?.Invoke();
        if (_pinchStartScale <= 0) _pinchStartScale = _scale;
        ZoomToScale(_pinchStartScale * e.Scale, e.ScaleOrigin, animate: false);
        e.Handled = true;
    }

    void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (_pyramid == null || LocksView) return;
        Interacted?.Invoke();
        ZoomToScale(_scale * (1 + e.Delta.X), e.GetPosition(this), animate: false);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        Interacted?.Invoke();
        for (int i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i].IsVisible && _overlays[i].OnKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Space) { _spaceHeld = true; return; }
        if (!CanPan || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        double step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.5 : 0.1) * Math.Min(Viewport.Width, Viewport.Height);
        switch (e.Key)
        {
            case Key.Left: PanBy(new Vector(step, 0)); e.Handled = true; break;
            case Key.Right: PanBy(new Vector(-step, 0)); e.Handled = true; break;
            case Key.Up: PanBy(new Vector(0, step)); e.Handled = true; break;
            case Key.Down: PanBy(new Vector(0, -step)); e.Handled = true; break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) _spaceHeld = false;
    }
}

/// <summary>The divider of the split comparison: draggable, with "Before" and "After" labels.</summary>
public sealed class SplitOverlay : ViewerOverlay
{
    static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#f2f2f2")), 2);
    static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 4);
    static readonly IBrush Knob = new SolidColorBrush(Color.Parse("#f2f2f2"));
    static readonly IBrush KnobMark = new SolidColorBrush(Color.Parse("#1c1c1c"));
    static readonly IBrush BadgeGround = new SolidColorBrush(Color.Parse("#262626"));
    static readonly IBrush BadgeText = new SolidColorBrush(Color.Parse("#f2f2f2"));
    bool _dragging;

    public SplitOverlay() => IsVisible = false;
    public override OverlayLayer Layer => OverlayLayer.Guides;
    public bool IsDragging => _dragging;

    double X(ViewTransform t) => t.DisplayRect.X + t.DisplayRect.Width * Math.Clamp(Viewer?.SplitPosition ?? 0.5, 0, 1);

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        var photo = t.DisplayRect.Intersect(bounds);
        double x = X(t);
        context.DrawLine(Outline, new Point(x, photo.Top), new Point(x, photo.Bottom));
        context.DrawLine(Line, new Point(x, photo.Top), new Point(x, photo.Bottom));
        var c = new Point(x, photo.Center.Y);
        context.DrawEllipse(Knob, null, c, 14, 14);
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X - 9, c.Y), true); s.LineTo(new Point(c.X - 3, c.Y - 5)); s.LineTo(new Point(c.X - 3, c.Y + 5)); s.EndFigure(true);
            s.BeginFigure(new Point(c.X + 9, c.Y), true); s.LineTo(new Point(c.X + 3, c.Y - 5)); s.LineTo(new Point(c.X + 3, c.Y + 5)); s.EndFigure(true);
        }
        context.DrawGeometry(KnobMark, null, g);
        var font = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(Viewer!));
        void Badge(string text, bool right)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, 12, BadgeText);
            double bx = right ? x + 8 : x - 8 - ft.Width - 12;
            var r = new Rect(bx, photo.Top + 8, ft.Width + 12, ft.Height + 4);
            context.FillRectangle(BadgeGround, r);
            context.DrawText(ft, new Point(r.X + 6, r.Y + 2));
        }
        Badge("Before", false);
        Badge("After", true);
    }

    public override bool OnPressed(ViewerPointer p, ViewTransform t)
    {
        if (Math.Abs(p.View.X - X(t)) > 10 || !t.DisplayRect.Contains(p.View)) return false;
        _dragging = true;
        return true;
    }

    public override void OnMoved(ViewerPointer p, ViewTransform t)
    {
        if (!_dragging || Viewer == null) return;
        Viewer.SplitPosition = Math.Clamp((p.View.X - t.DisplayRect.X) / Math.Max(1, t.DisplayRect.Width), 0.02, 0.98);
    }

    public override void OnReleased(ViewerPointer p, ViewTransform t) => _dragging = false;

    public override Cursor? CursorAt(Point view, ViewTransform t) =>
        Math.Abs(view.X - X(t)) <= 10 && t.DisplayRect.Contains(view) ? Cursors.Get(StandardCursorType.SizeWestEast) : null;
}
