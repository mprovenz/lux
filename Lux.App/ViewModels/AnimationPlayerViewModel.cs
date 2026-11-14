using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>A focus point marked on the player ("Start", "End", "Focus"), normalised to the frame.</summary>
public sealed record PlayerMarker(double X, double Y, string Label);

/// <summary>
/// The animation preview player: shows the preview's frames as they arrive (the nearest ready frame stands in for one still being made;
/// before the first one the photo's thumbnail is shown blurred with "Processing image"), plays them with their own timing, scrubs, loops.
/// Never blocks: frames are copied into bitmaps on the UI thread as they arrive, one bitmap per frame.
/// </summary>
public sealed partial class AnimationPlayerViewModel : ObservableObject, IDisposable
{
    IAnimationPreview? _p;
    readonly Dictionary<int, WriteableBitmap> _bitmaps = new();
    readonly Dictionary<int, PreviewFrame> _made = new();
    readonly DispatcherTimer _timer;
    bool _refreshPosted;

    public AnimationPlayerViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) => Safe(Advance);
    }

    /// <summary>The photo's thumbnail (blurred under "Processing image" until the first frame).</summary>
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
    /// <summary>A click on the picture while picking (normalised x, y).</summary>
    public Action<double, double>? Picked { get; set; }

    public IAnimationPreview? Preview => _p;
    /// <summary>Playing was asked for but the next frame is not made yet: the player holds the frame it shows (with the spinner and "Preparing
    /// frames · n of m") and goes on by itself when it arrives — it never steps over a frame that is not ready.</summary>
    public bool IsPreparing { get; private set; }
    /// <summary>"Preparing frames · 6 of 24" while the player waits for frames, else empty.</summary>
    public string PreparingText => IsPreparing && _p is { IsPlanned: true } p ? $"Preparing frames · {ReadyCount} of {p.FrameCount}" : IsPreparing ? "Preparing frames" : "";
    int ReadyCount => _p is { IsPlanned: true } p ? Enumerable.Range(0, p.FrameCount).Count(i => p.Frame(i) != null) : 0;
    /// <summary>Per played position: its frame is ready (the scrubber shows the ready range).</summary>
    public IReadOnlyList<bool> ReadyPositions => _p is { IsPlanned: true } p ? p.Order.Select(i => p.Frame(i) != null).ToArray() : Array.Empty<bool>();
    /// <summary>The frame of a played position is made.</summary>
    public bool IsReadyAt(int position) => _p is { IsPlanned: true } p && p.Order.Count > 0 && p.Frame(p.Order[Math.Clamp(position, 0, p.Order.Count - 1)]) != null;
    public int Length => _p?.Order.Count ?? 0;
    public int MaxPosition => Math.Max(0, Length - 1);
    public bool HasFrame => Frame != null;
    public bool IsWaiting => Frame == null;
    public double Progress => _p?.Progress ?? 0;
    public bool IsComplete => _p?.IsComplete ?? false;
    public string PlayLabel => IsPlaying ? "Pause" : "Play";
    public string Counter => Length == 0 ? "" : $"{Position + 1} / {Length}";
    public string FrameLabel { get; private set; } = "";
    /// <summary>"Rendering frames · 6 of 24", "Computing depth (in the Queue)", or the reason it stopped.</summary>
    public string Status => _p?.Error is { } e ? e : IsPreparing && _p is { IsComplete: false } ? PreparingText : _p?.Status ?? "";
    public bool HasError => _p?.Error != null;
    public string WaitingLabel => _p?.Error != null ? "Preview stopped" : "Processing image";
    public string ProgressText => _p is { IsPlanned: true } p && !p.IsComplete ? $"{p.Order.Select(i => i).Distinct().Count(i => p.Frame(i) != null)} of {p.FrameCount} frames" : "";

    partial void OnIsPlayingChanged(bool value) { OnPropertyChanged(nameof(PlayLabel)); if (!value) SetPreparing(false); Schedule(); }

    void SetPreparing(bool on)
    {
        if (IsPreparing == on) return;
        IsPreparing = on;
        OnPropertyChanged(nameof(IsPreparing)); OnPropertyChanged(nameof(PreparingText)); OnPropertyChanged(nameof(Status));
    }
    partial void OnPositionChanged(int value) { ShowPosition(); OnPropertyChanged(nameof(Counter)); }
    partial void OnFrameChanged(Bitmap? value) { OnPropertyChanged(nameof(HasFrame)); OnPropertyChanged(nameof(IsWaiting)); }

    /// <summary>Show another preview (the old one's bitmaps go; its frames stay in the library's cache).</summary>
    public void Bind(IAnimationPreview? preview)
    {
        if (ReferenceEquals(preview, _p)) return;
        SetPreparing(false);
        if (_p != null) _p.Changed -= OnChanged;
        _p = preview;
        // the picture on screen stays until the new preview has a frame of its own (so a second focus point can be picked at once, and
        // a setting change does not flash the "Processing image" state)
        _stale = Frame as WriteableBitmap;
        foreach (var b in _bitmaps.Values) if (!ReferenceEquals(b, _stale)) b.Dispose();
        _bitmaps.Clear(); _made.Clear();
        if (_p != null) _p.Changed += OnChanged;
        _position = 0;
        Refresh();
    }

    bool _disposed;

    void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _p)) return;
        lock (_bitmaps) { if (_refreshPosted) return; _refreshPosted = true; }
        Dispatcher.UIThread.Post(() => { lock (_bitmaps) _refreshPosted = false; if (!_disposed) Safe(Refresh); }, DispatcherPriority.Background);
    }

    /// <summary>A refresh or tick running on the dispatcher on its own (no caller to report to): a preview that went away meanwhile (its frames
    /// or bitmaps released) only stops the player's update, never the app.</summary>
    void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine("animation player: " + e.Message);
            _timer.Stop();
        }
    }

    /// <summary>Pick up what the preview has now (plan, frames, status).</summary>
    public void Refresh()
    {
        if (_position > MaxPosition) _position = 0;
        ShowPosition();
        // waiting to play on: the next frame has come
        if (IsPreparing && IsPlaying && Length > 1 && IsReadyAt(NextPosition)) SetPreparing(false);
        else if (IsPlaying && Length > 1 && !IsReadyAt(NextPosition)) SetPreparing(true);
        foreach (var n in new[] { nameof(Length), nameof(MaxPosition), nameof(Progress), nameof(IsComplete), nameof(Counter), nameof(Status), nameof(HasError),
                                  nameof(WaitingLabel), nameof(ProgressText), nameof(Position), nameof(ReadyPositions), nameof(PreparingText) })
            OnPropertyChanged(n);
        Schedule();
    }

    int NextPosition => Position + 1 >= Length ? (Loop ? 0 : Position) : Position + 1;

    WriteableBitmap? _stale;
    /// <summary>The picture shown is the previous preview's (the new one has no frame yet).</summary>
    public bool IsUpdating => _stale != null && ReferenceEquals(Frame, _stale);

    void ShowPosition()
    {
        if (_p is not { IsPlanned: true } p || p.Order.Count == 0) { Frame = _stale; FrameLabel = _stale != null ? "Updating…" : ""; OnPropertyChanged(nameof(FrameLabel)); OnPropertyChanged(nameof(IsUpdating)); return; }
        int idx = p.Order[Math.Clamp(_position, 0, p.Order.Count - 1)];
        var f = p.Frame(idx) ?? p.NearestFrame(idx);
        if (f == null) { Frame = _stale; FrameLabel = _stale != null ? "Updating…" : ""; OnPropertyChanged(nameof(FrameLabel)); OnPropertyChanged(nameof(IsUpdating)); return; }
        if (_stale != null) { _stale.Dispose(); _stale = null; OnPropertyChanged(nameof(IsUpdating)); }
        if (_bitmaps.TryGetValue(f.Index, out var had) && (!_made.TryGetValue(f.Index, out var src) || !ReferenceEquals(src, f)))
        {
            // a sharper frame came for this index: the new bitmap replaces the old one (disposed after it is off screen)
            _bitmaps.Remove(f.Index);
            if (ReferenceEquals(Frame, had)) Dispatcher.UIThread.Post(had.Dispose, DispatcherPriority.Background); else had.Dispose();
        }
        if (!_bitmaps.TryGetValue(f.Index, out var bmp))
        {
            bmp = new WriteableBitmap(new Avalonia.PixelSize(f.Width, f.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
                for (int y = 0; y < f.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(f.Rgba, y * f.Width * 4, fb.Address + y * fb.RowBytes, f.Width * 4);
            _bitmaps[f.Index] = bmp;
            _made[f.Index] = f;
        }
        Frame = bmp;
        FrameLabel = f.Index == idx ? f.Label : f.Label + " (nearest ready)";
        OnPropertyChanged(nameof(FrameLabel));
    }

    void Schedule()
    {
        bool run = IsPlaying && Length > 1 && !IsPreparing;
        if (!run) { _timer.Stop(); return; }
        int ms = _p is { DelaysMs.Count: > 0 } p ? p.DelaysMs[Math.Clamp(_position, 0, p.DelaysMs.Count - 1)] : 70;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, ms));
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>The next frame (the timer's tick; tests call it directly).</summary>
    public void Advance()
    {
        if (Length == 0) return;
        int next = Position + 1;
        if (next >= Length) { if (!Loop) { IsPlaying = false; return; } next = 0; }
        // never over a frame that is not made yet: hold this one until it arrives (Refresh goes on)
        if (!IsReadyAt(next)) { SetPreparing(true); _timer.Stop(); return; }
        Position = next;
        Schedule();
    }

    [RelayCommand] void TogglePlay() => IsPlaying = !IsPlaying;
    [RelayCommand] void StepBack() { IsPlaying = false; Position = Position > 0 ? Position - 1 : MaxPosition; }
    [RelayCommand] void StepForward() { IsPlaying = false; Position = Position < MaxPosition ? Position + 1 : 0; }

    /// <summary>A click at a point of the frame (normalised, unturned — the view maps the turned picture back).</summary>
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
        _timer.Stop();
        _disposed = true;
        if (_p != null) _p.Changed -= OnChanged;
        _p = null;
        // nothing shown first, then the bitmaps go (a render never meets a disposed bitmap)
        var all = _bitmaps.Values.ToList();
        if (_stale != null) all.Add(_stale);
        Frame = null;
        _bitmaps.Clear(); _made.Clear(); _stale = null;
        foreach (var b in all.Distinct()) b.Dispose();
    }
}
