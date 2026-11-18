using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.Library;
using SkiaSharp;

namespace Lux.App.ViewModels;

/// <summary>A focus point marked on the player ("Start", "End", "Focus"), normalized to the frame.</summary>
public sealed record PlayerMarker(double X, double Y, string Label);

/// <summary>
/// The animation preview player.
///
/// The player waits until every frame of the preview is ready at the final preview quality (<see cref="IAnimationPreview.IsFinal"/>).
/// While it waits it shows one still picture under a spinner, a text ("Frame 12 of 24") and a progress bar. The still picture is the
/// first frame of the new preview. Until that frame is made, the picture that was on screen stays, dimmed. Nothing plays while it waits.
///
/// When the last frame is in, the player converts every frame once into a bitmap for the screen (off the UI thread) and starts to play
/// at the first frame. Each new preview plays by itself, also when the last one was paused.
///
/// Playback follows a monotonic clock. Each tick shows the frame that is due at the elapsed time. A late tick skips frames. It never
/// makes the animation run slow. The view calls <see cref="Tick"/> once per rendered frame. A slow timer calls it too, so the player also
/// runs when no view asks.
/// </summary>
public sealed partial class AnimationPlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>The most memory the frames for the screen may use, bytes. A preview that needs more gets smaller frames.</summary>
    public const long MaxDisplayBytes = 256L << 20;

    IAnimationPreview? _p;
    bool _disposed, _refreshPosted;

    // ---- the frames for the screen, set when the preview is final
    Bitmap?[] _frames = Array.Empty<Bitmap?>();   // by frame index
    int[] _order = Array.Empty<int>();            // the played sequence
    double[] _starts = Array.Empty<double>();     // the time each position starts, ms
    string[] _labels = Array.Empty<string>();     // by frame index
    string[] _counters = Array.Empty<string>();   // by position
    double _total;
    bool _ready;
    double _builtScale = 1;
    CancellationTokenSource? _build;

    // ---- the still picture shown while the frames are made
    Bitmap? _still;
    PreviewFrame? _stillSource;
    Bitmap? _held;                                // the picture of the preview before this one

    // ---- the clock
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _origin;                               // the clock time at which position 0 starts
    bool _byClock;
    readonly DispatcherTimer _slowTimer;

    public AnimationPlayerViewModel()
    {
        _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _slowTimer.Tick += (_, _) => Safe(() => Tick());
    }

    /// <summary>The photo's thumbnail (blurred under the spinner until the first frame).</summary>
    [ObservableProperty] Bitmap? _placeholder;
    [ObservableProperty] Bitmap? _frame;
    [ObservableProperty] int _position;
    [ObservableProperty] bool _isPlaying = true;
    [ObservableProperty] bool _loop = true;
    [ObservableProperty] IReadOnlyList<PlayerMarker> _markers = Array.Empty<PlayerMarker>();
    /// <summary>Degrees clockwise the frames are drawn turned: the display-only "as the camera was held" turn of the library and Develop
    /// — the file itself follows only the saved turn, as stills do.</summary>
    [ObservableProperty] int _rotation;
    /// <summary>Degrees clockwise the thumbnail standing in before the first frame is turned (as the library shows the photo); the frames
    /// themselves carry their orientation.</summary>
    [ObservableProperty] int _placeholderRotation;
    /// <summary>Clicks on the picture pick a point (the panel's focus picking) — the crosshair shows meanwhile.</summary>
    [ObservableProperty] bool _isPicking;
    [ObservableProperty] string? _pickHint;
    /// <summary>The player stays on the first frame (a target is picked on it). Playback goes on when this is cleared.</summary>
    [ObservableProperty] bool _holdAtStart;
    /// <summary>A click on the picture while picking (normalized x, y).</summary>
    public Action<double, double>? Picked { get; set; }

    public IAnimationPreview? Preview => _p;
    /// <summary>Every frame is ready at the final preview quality and converted for the screen: the animation can play.</summary>
    public bool IsReady => _ready;
    /// <summary>The same as <see cref="IsReady"/>.</summary>
    public bool IsComplete => _ready;
    /// <summary>The frames are being made. The player shows one still picture with the progress and does not play.</summary>
    public bool IsLoading => _p is { Error: null } && !_ready;
    /// <summary>What the preview does now, for example "Frame 12 of 24". Empty when the player is not loading.</summary>
    public string LoadingText => IsLoading ? _p!.Work.Text : "";
    /// <summary>The part of the work that is done, 0 to 1 (0 when it is not known).</summary>
    public double LoadingFraction => !IsLoading ? 0 : _p!.IsFinal ? 1 : _p.Work.Fraction ?? 0;
    /// <summary>The amount of work is not known: the spinner shows alone, without the progress bar.</summary>
    public bool IsLoadingIndeterminate => IsLoading && !_p!.IsFinal && _p.Work.Fraction is null;
    /// <summary>The progress bar shows: the frames are being made and the amount of work is known.</summary>
    public bool ShowLoadingBar => IsLoading && !IsLoadingIndeterminate;
    /// <summary>The player plays now: the frames are ready, play is on and nothing holds the first frame.</summary>
    public bool IsRunning => _ready && IsPlaying && !HoldAtStart && _order.Length > 1;
    /// <summary>Per played position: its frame is ready at the final quality (the strip under the scrubber).</summary>
    public IReadOnlyList<bool> ReadyPositions => _p is { IsPlanned: true } p ? p.Order.Select(i => p.FinalFrame(i) != null).ToArray() : Array.Empty<bool>();
    /// <summary>The frame of a played position is made at the final quality.</summary>
    public bool IsReadyAt(int position) => _p is { IsPlanned: true } p && p.Order.Count > 0 && p.FinalFrame(p.Order[Math.Clamp(position, 0, p.Order.Count - 1)]) != null;
    public int Length => _p?.Order.Count ?? 0;
    public int MaxPosition => Math.Max(0, (_ready ? _order.Length : Length) - 1);
    public bool HasFrame => Frame != null;
    public bool IsWaiting => Frame == null;
    public double Progress => _p?.Progress ?? 0;
    public string PlayLabel => IsPlaying ? "Pause" : "Play";
    public string Counter => _ready && Position < _counters.Length ? _counters[Position] : "";
    public string FrameLabel { get; private set; } = "";
    /// <summary>The progress while the frames are made, then the preview's own status, or the reason it stopped.</summary>
    public string Status => _p?.Error is { } e ? e : IsLoading ? LoadingText : _p?.Status ?? "";
    public bool HasError => _p?.Error != null;
    public string WaitingLabel => _p?.Error != null ? "Preview stopped" : IsLoading ? LoadingText : "Processing image";
    /// <summary>The picture shown is the previous preview's (the new one has no frame yet).</summary>
    public bool IsUpdating => _held != null && ReferenceEquals(Frame, _held);
    /// <summary>The size of the frames made for the screen, pixels (tests and measurements).</summary>
    public (int W, int H) DisplayFrameSize => _ready && _frames.Length > 0 && _frames[0] is { } b ? (b.PixelSize.Width, b.PixelSize.Height) : (0, 0);
    /// <summary>The memory the frames for the screen use, bytes.</summary>
    public long DisplayBytes => _ready ? _frames.Sum(b => b is null ? 0L : (long)b.PixelSize.Width * b.PixelSize.Height * 4) : 0;
    /// <summary>Frames shown and frames skipped by the clock since the preview started to play (measurements).</summary>
    public int FramesShown { get; private set; }
    public int FramesSkipped { get; private set; }

    partial void OnIsPlayingChanged(bool value) { OnPropertyChanged(nameof(PlayLabel)); RunningChanged(); }
    partial void OnHoldAtStartChanged(bool value) { if (value && _ready && Position != 0) Position = 0; RunningChanged(); }
    partial void OnLoopChanged(bool value) => Anchor();
    partial void OnPositionChanged(int value)
    {
        if (!_byClock) Anchor();
        ShowPosition();
        OnPropertyChanged(nameof(Counter));
    }
    partial void OnFrameChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if ((oldValue is null) != (newValue is null)) { OnPropertyChanged(nameof(HasFrame)); OnPropertyChanged(nameof(IsWaiting)); }
    }

    /// <summary>The picture area's size in screen pixels. Frames larger than it are made smaller once, before they play.</summary>
    public void SetDisplaySize(double width, double height)
    {
        if (Math.Abs(width - _display.W) < 1 && Math.Abs(height - _display.H) < 1) return;
        _display = (width, height);
        // frames made for a smaller area look soft in a larger one: make them again when the area grows by more than a tenth
        if (_ready && _p is { } p && _build is null && ScaleFor(p) > _builtScale * 1.1) StartBuild(p);
    }
    (double W, double H) _display;

    /// <summary>Show another preview. The frames of the old one are released. The picture on screen stays as a still, dimmed, until the
    /// new preview has its first frame, so a click can pick a second point at once. The new preview plays by itself when it is ready.</summary>
    public void Bind(IAnimationPreview? preview)
    {
        if (ReferenceEquals(preview, _p)) return;
        if (_p != null) _p.Changed -= OnChanged;
        CancelBuild();
        var keep = Frame;
        foreach (var b in _frames) if (b != null && !ReferenceEquals(b, keep)) b.Dispose();
        if (_still != null && !ReferenceEquals(_still, keep)) _still.Dispose();
        if (_held != null && !ReferenceEquals(_held, keep)) _held.Dispose();
        _frames = Array.Empty<Bitmap?>(); _order = Array.Empty<int>(); _starts = Array.Empty<double>(); _labels = Array.Empty<string>(); _counters = Array.Empty<string>();
        _still = null; _stillSource = null; _held = keep; _ready = false; _total = 0;
        FramesShown = 0; FramesSkipped = 0;
        _p = preview;
        if (_p != null) _p.Changed += OnChanged;
        _byClock = true;
        try { Position = 0; } finally { _byClock = false; }
        if (!IsPlaying) IsPlaying = true;
        Refresh();
    }

    void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _p)) return;
        lock (_clock) { if (_refreshPosted) return; _refreshPosted = true; }
        Dispatcher.UIThread.Post(() => { lock (_clock) _refreshPosted = false; if (!_disposed) Safe(Refresh); }, DispatcherPriority.Background);
    }

    /// <summary>A refresh or tick that runs on the dispatcher with no caller to report to. A preview that went away meanwhile only stops
    /// this update. It never stops the app or the player.</summary>
    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            Trace.WriteLine("animation player: " + e.Message);
        }
    }

    /// <summary>Pick up what the preview has now (plan, frames, status).</summary>
    public void Refresh()
    {
        if (_p is { Error: null } p && !_ready)
        {
            UpdateStill(p);
            if (p.IsFinal && _build is null) StartBuild(p);
        }
        ShowPosition();
        RaiseState();
    }

    static readonly string[] StateNames =
    {
        nameof(Length), nameof(MaxPosition), nameof(Progress), nameof(IsComplete), nameof(IsReady), nameof(IsLoading), nameof(LoadingText), nameof(LoadingFraction),
        nameof(IsLoadingIndeterminate), nameof(ShowLoadingBar), nameof(Counter), nameof(Status), nameof(HasError), nameof(WaitingLabel), nameof(Position), nameof(ReadyPositions),
    };

    void RaiseState() { foreach (var n in StateNames) OnPropertyChanged(n); }

    /// <summary>The still picture while the frames are made: the first frame of this preview, in the best quality there is.</summary>
    void UpdateStill(IAnimationPreview p)
    {
        if (!p.IsPlanned || p.Frame(0) is not { } f || ReferenceEquals(f, _stillSource)) return;
        var old = _still; var held = _held;
        _still = MakeBitmap(f, f.Width, f.Height);
        _stillSource = f; _held = null;
        Frame = _still;
        old?.Dispose(); held?.Dispose();
        OnPropertyChanged(nameof(IsUpdating));
    }

    void ShowPosition()
    {
        string label = "";
        if (_ready && _order.Length > 0)
        {
            int idx = _order[Math.Clamp(Position, 0, _order.Length - 1)];
            Frame = _frames[idx];
            label = _labels[idx];
        }
        else Frame = _still ?? _held;
        if (FrameLabel != label) { FrameLabel = label; OnPropertyChanged(nameof(FrameLabel)); }
    }

    // ------------------------------------------------------------------------------------------------ the frames for the screen

    /// <summary>The factor on the frames' size: never larger than the picture area, never more memory than <see cref="MaxDisplayBytes"/>,
    /// never larger than the frames themselves.</summary>
    double ScaleFor(IAnimationPreview p)
    {
        if (p.FrameCount == 0 || p.FinalFrame(0) is not { } f) return 1;
        double k = 1;
        if (_display is { W: > 0, H: > 0 } d)
        {
            double fw = f.Width, fh = f.Height;
            if (((Rotation % 360) + 360) % 360 is 90 or 270) (fw, fh) = (fh, fw);
            k = Math.Min(1, Math.Min(d.W / fw, d.H / fh));
        }
        double bytes = (double)p.FrameCount * f.Width * f.Height * 4;
        if (bytes * k * k > MaxDisplayBytes) k = Math.Sqrt(MaxDisplayBytes / bytes);
        return k;
    }

    void CancelBuild()
    {
        if (_build is not { } b) return;
        _build = null;
        b.Cancel();
    }

    /// <summary>Convert every final frame into a bitmap for the screen, off the UI thread, then start to play.</summary>
    void StartBuild(IAnimationPreview p)
    {
        int n = p.FrameCount;
        var src = new PreviewFrame[n];
        for (int i = 0; i < n; i++)
        {
            if (p.FinalFrame(i) is not { } f) return;   // not all there yet: the next change asks again
            src[i] = f;
        }
        if (n == 0) return;
        double k = ScaleFor(p);
        var order = p.Order.ToArray();
        var delays = p.DelaysMs.ToArray();
        var cts = _build = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(() =>
        {
            var made = new Bitmap?[n];
            bool ok = false;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = src[i];
                    made[i] = MakeBitmap(f, Math.Max(1, (int)Math.Round(f.Width * k)), Math.Max(1, (int)Math.Round(f.Height * k)));
                }
                ok = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or ObjectDisposedException) { Trace.WriteLine("animation player: " + e.Message); }
            Dispatcher.UIThread.Post(() => Install(p, cts, ok ? made : null, made, src, order, delays, k));
        });
    }

    void Install(IAnimationPreview p, CancellationTokenSource cts, Bitmap?[]? made, Bitmap?[] all, PreviewFrame[] src, int[] order, int[] delays, double k)
    {
        bool current = ReferenceEquals(cts, _build);
        if (current) _build = null;
        if (!current || _disposed || made is null || !ReferenceEquals(p, _p))
        {
            foreach (var b in all) b?.Dispose();
            return;
        }
        var oldFrames = _frames; var oldStill = _still; var oldHeld = _held;
        bool first = !_ready;
        _frames = made; _order = order; _builtScale = k;
        _labels = src.Select(f => f.Label).ToArray();
        _counters = new string[order.Length];
        _starts = new double[order.Length];
        double t = 0;
        for (int i = 0; i < order.Length; i++)
        {
            _counters[i] = $"{i + 1} / {order.Length}";
            _starts[i] = t;
            t += Math.Max(1, i < delays.Length ? delays[i] : 70);
        }
        _total = t;
        _still = null; _stillSource = null; _held = null;
        _ready = true;
        if (first)
        {
            // the animation starts at its first frame
            _byClock = true;
            try { Position = 0; } finally { _byClock = false; }
        }
        ShowPosition();
        foreach (var b in oldFrames) b?.Dispose();
        oldStill?.Dispose(); oldHeld?.Dispose();
        OnPropertyChanged(nameof(IsUpdating));
        RaiseState();
        if (first) RunningChanged();   // frames made again for a larger picture area take the place of the old ones: the clock goes on
    }

    /// <summary>An immutable bitmap of a frame at a size. The renderer keeps an immutable bitmap on the graphics card after its first
    /// draw, so a frame costs nothing to show again.</summary>
    static Bitmap MakeBitmap(PreviewFrame f, int w, int h)
    {
        var pin = GCHandle.Alloc(f.Rgba, GCHandleType.Pinned);
        try
        {
            if (w == f.Width && h == f.Height)
                return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Opaque, pin.AddrOfPinnedObject(), new Avalonia.PixelSize(w, h), new Vector(96, 96), w * 4);
            using var src = new SKBitmap();
            src.InstallPixels(new SKImageInfo(f.Width, f.Height, SKColorType.Rgba8888, SKAlphaType.Opaque), pin.AddrOfPinnedObject(), f.Width * 4);
            using var small = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), SKFilterQuality.Medium)
                              ?? throw new InvalidOperationException("the frame could not be resized");
            return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Opaque, small.GetPixels(), new Avalonia.PixelSize(w, h), new Vector(96, 96), small.RowBytes);
        }
        finally { pin.Free(); }
    }

    // ------------------------------------------------------------------------------------------------ the clock

    /// <summary>Start the clock at the current position.</summary>
    void Anchor()
    {
        if (!_ready || _starts.Length == 0) return;
        _origin = _clock.Elapsed.TotalMilliseconds - _starts[Math.Clamp(Position, 0, _starts.Length - 1)];
    }

    void RunningChanged()
    {
        bool run = IsRunning;
        if (run) Anchor();
        if (run != _slowTimer.IsEnabled) { if (run) _slowTimer.Start(); else _slowTimer.Stop(); }
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>Show the frame that is due now. The view calls this once per rendered frame while <see cref="IsRunning"/>. Returns false
    /// when the player does not play.</summary>
    public bool Tick()
    {
        if (!IsRunning) return false;
        double t = _clock.Elapsed.TotalMilliseconds - _origin;
        if (t >= _total)
        {
            if (!Loop)
            {
                MoveTo(_order.Length - 1);
                IsPlaying = false;
                return false;
            }
            _origin += Math.Floor(t / _total) * _total;
            t = _clock.Elapsed.TotalMilliseconds - _origin;
        }
        int pos = Position < _starts.Length && _starts[Position] <= t ? Position : 0;
        while (pos + 1 < _starts.Length && _starts[pos + 1] <= t) pos++;
        if (pos != Position)
        {
            int step = pos > Position ? pos - Position : pos + _order.Length - Position;
            FramesShown++;
            FramesSkipped += step - 1;
            MoveTo(pos);
        }
        return true;
    }

    void MoveTo(int pos)
    {
        _byClock = true;
        try { Position = pos; } finally { _byClock = false; }
    }

    /// <summary>One frame forward by hand (tests). The clock goes on from the new position. Nothing moves while the frames are made.</summary>
    public void Advance()
    {
        if (!_ready || _order.Length == 0) return;
        int next = Position + 1;
        if (next >= _order.Length) { if (!Loop) { IsPlaying = false; return; } next = 0; }
        Position = next;
    }

    [RelayCommand] void TogglePlay() => IsPlaying = !IsPlaying;
    [RelayCommand] void StepBack() { if (!_ready) return; IsPlaying = false; Position = Position > 0 ? Position - 1 : MaxPosition; }
    [RelayCommand] void StepForward() { if (!_ready) return; IsPlaying = false; Position = Position < MaxPosition ? Position + 1 : 0; }

    /// <summary>A click at a point of the frame (normalized, unturned — the view maps the turned picture back).</summary>
    public void Click(double x, double y)
    {
        if (IsPicking) Picked?.Invoke(x, y);
    }

    /// <summary>A click at a point of the picture as shown (turned by <see cref="Rotation"/>) — what the view does for a pointer press.</summary>
    public void ClickShown(double u, double v)
    {
        var (x, y) = Controls.AnimationFrameView.ShownToFrame(u, v, Rotation);
        Click(x, y);
    }

    public void Dispose()
    {
        _slowTimer.Stop();
        _disposed = true;
        CancelBuild();
        if (_p != null) _p.Changed -= OnChanged;
        _p = null;
        // nothing shown first, then the bitmaps go (a render never meets a disposed bitmap)
        var all = _frames.Where(b => b != null).Select(b => b!).ToList();
        if (_still != null) all.Add(_still);
        if (_held != null) all.Add(_held);
        _ready = false;
        Frame = null;
        _frames = Array.Empty<Bitmap?>(); _still = null; _held = null; _stillSource = null;
        foreach (var b in all.Distinct()) b.Dispose();
    }
}
