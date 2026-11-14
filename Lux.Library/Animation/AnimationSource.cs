using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Library.Core;
using Lux.Store;

namespace Lux.Library.Animation;

/// <summary>
/// <see cref="IAnimationSource"/>: one capture opened for animated effects. The renders use the photo's open editing session when it is open
/// (the Develop page: same state, same depth, same edits), else a session of its own. Preview frames are export level 4 (520 × 390 on a
/// 4:3 capture; the lens effects 520 px wide from a quicker lens render), made in an order that covers the animation early (first, last,
/// middle, quarters, …) and kept per set of frame-defining values for as long as the source is open. A depth effect on a photo that has
/// not been analysed asks the Queue for the analysis (so it shows there) and continues when it is done.
/// </summary>
internal sealed class AnimationSourceImpl : IAnimationSource
{
    readonly LibraryCore _lib;
    readonly string _path;
    readonly StoreCaptureContext _ctx;
    readonly object _gate = new();
    CaptureSession? _own;
    readonly Dictionary<string, FrameStore> _stores = new();
    PreviewImpl? _current;
    readonly SemaphoreSlim _renderGate = new(1, 1);
    volatile bool _disposed;

    public string CaptureId { get; }
    public CaptureItem Item { get; private set; }
    public IReadOnlyList<AnimationEffectInfo> Effects { get; }
    public float AllInFocusFNumber => Item.AllInFocusFNumber;

    internal AnimationSourceImpl(LibraryCore lib, CaptureItem item)
    {
        _lib = lib; Item = item; CaptureId = item.Id;
        _path = lib.PresentPath(item.Id);
        _ctx = lib.ContextFor(item.Id, _path, mayCreateFolder: false);
        Effects = AnimationEffects.For(item);
        lib.RegisterAnimation(this);
    }

    /// <summary>A job changed what the capture keeps: this source's own session uses the cache as it is now.</summary>
    internal void OnCacheChanged()
    {
        CaptureSession? own; lock (_gate) own = _own;
        own?.RefreshCache();
    }

    /// <summary>The photo's open editing session when there is one, else this source's own.</summary>
    internal CaptureSession Session()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var open = _preferOwn ? null : _lib.OpenSession(CaptureId);
        if (open is not null && !open.IsDisposed) return open.Engine;
        lock (_gate)
            return _own ??= CaptureSession.Open(_path, new SessionOptions
            {
                Context = _ctx, Threads = _lib.SettingsImpl.RenderThreads, DisplayCacheBytes = 0, KeepGeometry = false,
                Log = _lib.Log is null ? null : m => _lib.Log($"[anim {CaptureId[..8]}] {m}"),
            });
    }

    /// <summary>Run <paramref name="f"/> on the session; a borrowed editing session closed meanwhile is replaced by this source's own.</summary>
    internal T WithSession<T>(Func<CaptureSession, T> f)
    {
        try { return f(Session()); }
        catch (ObjectDisposedException) when (!_disposed) { lock (_gate) { } return f(OwnSession()); }
    }

    CaptureSession OwnSession()
    {
        lock (_gate)
            return _own ??= CaptureSession.Open(_path, new SessionOptions { Context = _ctx, Threads = _lib.SettingsImpl.RenderThreads, DisplayCacheBytes = 0, KeepGeometry = false });
    }

    /// <summary>The depth is available without computing it here: the photo is analysed (or its editing session has it). When not, the
    /// analysis is queued (it shows in the Queue) and awaited.</summary>
    /// <summary>After the depth was computed by a Queue job while the photo was open in Develop, its editing session may hold a render state
    /// made before the kept depth existed: the frames then come from this source's own session, which reads the kept depth.</summary>
    volatile bool _preferOwn;

    internal async Task EnsureDepthAsync(Action<string> status, CancellationToken cancel)
    {
        Item = _lib.Get(CaptureId) ?? Item;
        // the depth is at hand (the same rule as the Develop page's depth): the analysis is kept, the active version pins its own depth, an
        // imported Lumen session brings its geometry, or the photo's open editing session has its depth loaded already
        bool has = Item.CacheLevel >= CacheLevel.Geometry || Item.Edits.Variant is not null;
        var open = _lib.OpenSession(CaptureId);
        if (!has && open is not null && !open.IsDisposed)
            has = open.DepthImpl.Status.Availability == DepthAvailability.Ready || open.Engine.SavedState?.SessionGeometry is not null;
        if (has) return;
        if (Item.DepthState == DepthState.Failed) throw new LibraryException("This photo's depth map could not be computed.");
        // computed by a Queue job (named after the photo), whichever page asked — never inside the preview
        status("Computing depth (in the Queue)");
        var ids = _lib.AnalyseNow(new[] { CaptureId });
        foreach (var id in ids)
        {
            var info = await _lib.Jobs.WaitAsync(id, cancel).ConfigureAwait(false);
            if (info.State == JobState.Failed) throw new LibraryException(info.Error ?? "The analysis failed.");
            if (info.State == JobState.Cancelled) throw new OperationCanceledException("the analysis was cancelled");
        }
        Item = _lib.Get(CaptureId) ?? Item;
        if (open is not null) _preferOwn = true;
        // a session opened before the analysis finished has not read the kept depth yet: open again
        lock (_gate) { _own?.Dispose(); _own = null; }
        AnalysisFinished?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? AnalysisFinished;

    /// <summary>The photo's saved settings changed (a turn kept for exports): forget the frames made so far and read the settings again.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _current?.Stop(); _current = null; _stores.Clear();
            _own?.Dispose(); _own = null;
        }
        Item = _lib.Get(CaptureId) ?? Item;
        _lib.OpenSession(CaptureId)?.Engine.ReloadSavedState();
    }

    public async Task<float> DepthAtAsync(double x, double y, CancellationToken cancel = default)
    {
        await EnsureDepthAsync(_ => { }, cancel).ConfigureAwait(false);
        return await Task.Run(() => WithSession(s =>
        {
            // a point of a level-4 preview frame (the export's grid, after the saved crop / rotation, or the held turn the frames carry) → the
            // capture's normalised export window
            var spec = Oriented(new AnimationSpec(), _current?.Options ?? new AnimationOptions());
            var req = new ExportRequest { Formats = new[] { ExportImageFormat.Jpeg }, Level = 4 };
            if (spec.Transform is { } t) req = req with { Transform = t, OverriddenSettings = new HashSet<string> { "transform" } };
            var es = s.PrepareExportSession(req, cancel);
            var g = es.Grid;
            var to = TransformOutputOf(g);
            var (u, v) = to.Map((float)(x * g.Size.W), (float)(y * g.Size.H));
            var dl = g.Window.ExportDims[to.Level];
            return s.DepthAtAsync((float)((u + to.Source.X0) / dl.W), (float)((v + to.Source.Y0) / dl.H), cancel).GetAwaiter().GetResult();
        }), cancel).ConfigureAwait(false);
    }

    static TransformOutput TransformOutputOf(Exporter.ExportGrid g)
        => ExportTransformOutput.Compute(g.Transform, g.Size, new RectI(0, 0, g.Size.W, g.Size.H), g.Window.ExportDims, false);

    public async Task<(float Near, float Far, float DefaultFocus)> FocusDefaultsAsync(CancellationToken cancel = default)
    {
        await EnsureDepthAsync(_ => { }, cancel).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var s = Session();
            var (near, far) = await AnimationRenderer.DepthPercentilesAsync(s, cancel).ConfigureAwait(false);
            float dflt;
            try { dflt = Lux.Engine.Pipeline.Dof.DofModel.DefaultFocusDepth(s.Lri, s.DepthState().DepthCache(s.Window).Current); }
            catch (InvalidOperationException) { dflt = (near + far) / 2; }
            return (near, far, dflt);
        }, cancel).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ preview

    /// <summary>The values that decide a preview's frames (not their timing or order).</summary>
    internal static string FrameKey(AnimationOptions o)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(float? v) => v is float f ? f.ToString("R", inv) : "-";
        string u = o.Unturned is bool b ? (b ? "|u1" : "|u0") : "";
        return o.Effect switch
        {
            AnimationEffect.RackFocus => $"rack|{o.Frames}|{F(o.FNumber)}|{F(o.FocusFromMm)}|{F(o.FocusToMm)}|{o.UseSavedEdits}|{o.EditsVariant}{u}",
            AnimationEffect.ApertureSweep => $"aperture|{o.Frames}|{F(o.FNumber)}|{F(o.FNumberFrom)}|{F(o.FocusMm)}|{o.UseSavedEdits}|{o.EditsVariant}{u}",
            AnimationEffect.ParallaxSweep => $"parallax|{o.Frames}|{o.Path}|{o.AmplitudeMm.ToString("R", inv)}|{o.UseSavedEdits}|{o.EditsVariant}{u}",
            AnimationEffect.Wigglegram => "wiggle" + u,
            _ => "lenses" + u,
        };
    }

    /// <summary>The photo's orientation in the animation (exports follow the automatic upright view): the held turn as
    /// the depth-effect frames' transform (Lumen's rotate, exactly as "Rotate as the camera was held" saves it) when the photo is shown as held
    /// and the animation is not asked for unturned; the lens and parallax frames — which never read the saved transform — turned by the same
    /// quarter turn, or by the saved one of a photo whose geometry was edited.</summary>
    internal static AnimationSpec Oriented(LibraryCore lib, CaptureItem item, AnimationSpec spec, bool? unturned)
    {
        var t = lib.UprightTransform(item.Id, unturned, out int deg);
        if (t is not null) return spec with { Transform = t, OutputTurn = deg };
        int saved = item.GeometryDecided ? Edit.PhotoEditImpl.HeldDegrees(item.Orientation) : 0;
        return spec with { OutputTurn = saved };
    }

    internal AnimationSpec Oriented(AnimationSpec spec, AnimationOptions o) => Oriented(_lib, _lib.Get(CaptureId) ?? Item, spec, o.Unturned);

    /// <summary>The preview's engine spec: export level 4 for the depth effects and the parallax (from the main view alone: the other lenses'
    /// pictures cost seconds each), a quicker lens render 520 px wide for the lens effects.</summary>
    internal static AnimationSpec PreviewSpec(AnimationOptions o) => o.Effect is AnimationEffect.Wigglegram or AnimationEffect.LensTour
        ? o.ToSpec(level: null, longEdge: 520, lensIspLevel: 2)
        : o.ToSpec(level: 4, longEdge: 0) with { SingleView = true };

    /// <summary>The sharper pass of a depth-effect preview: the frames at the size the file will have, from export level 3 at most (a level-3
    /// frame is ~4× the work of a level-4 one; finer levels would take seconds a frame); null when the quick frames already are that size.</summary>
    internal static AnimationSpec? SharpSpec(AnimationOptions o) => o.Effect is AnimationEffect.RackFocus or AnimationEffect.ApertureSweep && o.LongEdge > 600
        ? o.ToSpec(level: 3, longEdge: o.LongEdge) with { SingleView = true }
        : null;

    internal static string SharpKey(AnimationOptions o) => $"{FrameKey(o)}|{o.LongEdge}";

    public IAnimationPreview Preview(AnimationOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FrameStore store;
        lock (_gate)
        {
            string key = FrameKey(options);
            if (!_stores.TryGetValue(key, out store!)) _stores[key] = store = new FrameStore();
            foreach (var (k, other) in _stores) if (k != key) other.DropSharp();
            _current?.Stop();
            _current = new PreviewImpl(this, options, store);
        }
        _current.Start();
        return _current;
    }

    internal SemaphoreSlim RenderGate => _renderGate;
    internal LibraryCore Lib => _lib;

    // ------------------------------------------------------------------------------------------------ estimates

    public async Task<AnimationEstimate> EstimateAsync(AnimationOptions o, CancellationToken cancel = default)
    {
        var item = Item;
        // ---- the frame size and count of the export
        int n = o.Effect is AnimationEffect.Wigglegram ? Math.Max(2, item.IsTelephoto ? 0 : 4) : o.Effect is AnimationEffect.LensTour ? Math.Max(1, item.ModuleCount) : o.Frames;
        bool ping = o.PingPong ?? AnimationSpec.DefaultPingPong(o.Effect, o.Path);
        int played = ping && n >= 3 ? 2 * n - 2 : n;
        var full = item.Width > 0 ? (item.Width, item.Height) : item.LevelSizes.Count > 0 ? (item.LevelSizes[0].Width, item.LevelSizes[0].Height) : (4160, 3120);
        if (o.Effect is AnimationEffect.Wigglegram or AnimationEffect.LensTour) full = (4160, 3120);
        // the orientation the frames carry: the held turn, or for the lens / parallax frames a saved quarter turn
        var oriented = Oriented(new AnimationSpec { Effect = o.Effect }, o);
        bool lensOrParallax = o.Effect is AnimationEffect.Wigglegram or AnimationEffect.LensTour or AnimationEffect.ParallaxSweep;
        if (oriented.OutputTurn is 90 or 270 && (lensOrParallax || oriented.Transform is not null)) full = (full.Item2, full.Item1);
        double k = Math.Min(1.0, (double)o.LongEdge / Math.Max(full.Item1, full.Item2));
        int w = Math.Max(1, (int)Math.Round(full.Item1 * k)), h = Math.Max(1, (int)Math.Round(full.Item2 * k));
        // ---- time: the depth first (none kept: the analysis), then the frames at the size they come out at — measured costs:
        //      depth-effect frame 0.15 s + 0.45 s per MP; parallax 8 s of set-up (the other lenses) + 0.35 s a frame;
        //      a lens render about 5 s, two at once
        double secs = 0;
        bool depth = o.Effect is not (AnimationEffect.Wigglegram or AnimationEffect.LensTour);
        if (depth) secs += item.CacheLevel == CacheLevel.None && item.Edits.Variant is null ? (item.IsStacked ? 100 : 35) : item.CacheLevel == CacheLevel.Geometry ? 5 : 3;
        double mp = (double)w * h / 1e6;
        secs += o.Effect switch
        {
            AnimationEffect.RackFocus or AnimationEffect.ApertureSweep => n * (0.15 + 0.45 * mp),
            AnimationEffect.ParallaxSweep => 8 + n * (0.15 + 0.4 * mp),
            AnimationEffect.Wigglegram => 10,
            _ => Math.Ceiling(n / 2.0) * 5,
        };
        secs += AnimationOptions.IsVideo(o.Format) ? 1 : o.Format == AnimationFormat.Gif ? 0.05 * played : 0.03 * played;
        // ---- size: the preview's frames encoded in the chosen format and scaled to the export's pixels, else typical figures
        long bytes; string basis;
        var store = _current is { } cur && FrameKey(cur.Options) == FrameKey(o) ? cur.Store : null;
        var ready = store?.All().OrderBy(f => f.Index).ToList();
        if (ready is { Count: >= 2 } && !AnimationOptions.IsVideo(o.Format))
        {
            var byIndex = ready.ToDictionary(f => f.Index);
            var seq = new List<Rgba>();
            var order = Enumerable.Range(0, n).ToList();
            if (ping && n >= 3) for (int i = n - 2; i >= 1; i--) order.Add(i);
            foreach (var i in order) if (byIndex.TryGetValue(i, out var pf)) seq.Add(new Rgba(pf.Width, pf.Height, pf.Rgba));
            if (seq.Count >= 2 && seq.All(x => x.W == seq[0].W && x.H == seq[0].H))
            {
                var c = o.Format switch { AnimationFormat.Apng => AnimationContainer.Apng, AnimationFormat.WebP => AnimationContainer.WebP, _ => AnimationContainer.Gif };
                var r = await Task.Run(() =>
                {
                    using var ms = new MemoryStream();
                    return AnimationEncoder.Write(ms, c, seq, Enumerable.Repeat(70, seq.Count).ToArray(),
                                                  new AnimationEncodeOptions { GifDither = o.GifDither, GifPalette = o.GifPalette, Cancel = cancel });
                }, cancel).ConfigureAwait(false);
                double scale = (double)w * h / ((double)seq[0].W * seq[0].H) * played / seq.Count;
                // the export's frames against the preview's (level 4) of the same animation, measured: GIF ×1.15–1.35,
                // WebP ×0.95–1.35, APNG ×0.9
                double kf = o.Format switch { AnimationFormat.Gif => 1.2, AnimationFormat.WebP => 1.1, _ => 0.9 };
                bytes = (long)(r.Bytes * scale * kf);
                basis = "from the preview";
                return new AnimationEstimate(bytes, TimeSpan.FromSeconds(secs), w, h, played, basis);
            }
        }
        // typical bytes per pixel of a played frame, measured on the samples
        bool lens = o.Effect is AnimationEffect.Wigglegram or AnimationEffect.LensTour;
        double bpp = o.Format switch
        {
            AnimationFormat.Gif => lens ? 0.75 : o.Effect == AnimationEffect.ParallaxSweep ? 0.25 : 0.15,
            AnimationFormat.Apng => lens ? 1.5 : 1.15,
            AnimationFormat.WebP => lens ? 1.2 : 0.75,
            AnimationFormat.Mp4 => lens ? 0.05 : 0.02,
            _ => lens ? 0.04 : 0.015,
        };
        bytes = (long)(bpp * w * h * played);
        basis = "typical size";
        return new AnimationEstimate(bytes, TimeSpan.FromSeconds(secs), w, h, played, basis);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lib.UnregisterAnimation(this);
        lock (_gate) { _current?.Stop(); _current = null; _stores.Clear(); }
        // wait for a frame in flight before the session goes (renders are short at the preview size)
        if (_renderGate.Wait(TimeSpan.FromSeconds(10))) _renderGate.Release();
        lock (_gate) { _own?.Dispose(); _own = null; }
    }

    internal bool IsDisposed => _disposed;
}

/// <summary>Preview frames of one set of frame-defining values, kept while the source is open: the quick ones (export level 4, every frame
/// first) and, for the depth effects, sharper ones at the size the file will have (up to export level 3) that replace them as they come
/// (the level-4 frames drawn enlarged make the depth effect hard to see).</summary>
internal sealed class FrameStore
{
    readonly ConcurrentDictionary<int, PreviewFrame> _frames = new();
    readonly ConcurrentDictionary<int, PreviewFrame> _sharp = new();
    string? _sharpKey;
    /// <summary>The quick frame (the one every frame has first).</summary>
    public PreviewFrame? Get(int i) => _frames.TryGetValue(i, out var f) ? f : null;
    /// <summary>The sharpest frame there is.</summary>
    public PreviewFrame? Best(int i) => _sharp.TryGetValue(i, out var f) ? f : Get(i);
    public void Put(PreviewFrame f) => _frames[f.Index] = f;
    public IEnumerable<PreviewFrame> All() => _frames.Values;
    public int Count => _frames.Count;
    public bool HasSharp(int i, string key) { lock (_sharp) return _sharpKey == key && _sharp.ContainsKey(i); }
    public void PutSharp(PreviewFrame f, string key)
    {
        lock (_sharp) { if (_sharpKey != key) { _sharp.Clear(); _sharpKey = key; } _sharp[f.Index] = f; }
    }
    /// <summary>Drop the sharp frames (a preview of other values is on screen; ~3.5 MB each).</summary>
    public void DropSharp() { lock (_sharp) { _sharp.Clear(); _sharpKey = null; } }
    public int SharpCount(string key) { lock (_sharp) return _sharpKey == key ? _sharp.Count : 0; }
}

/// <summary>A preview being made: the plan, then the frames in a coarse-to-fine order, one at a time (one source renders one preview at a
/// time; asking for another stops this one, keeping its frames).</summary>
internal sealed class PreviewImpl : IAnimationPreview
{
    readonly AnimationSourceImpl _src;
    readonly CancellationTokenSource _cts = new();
    AnimationPlan? _plan;
    volatile string _status = "Starting";
    volatile string? _error;
    volatile bool _complete;

    public AnimationOptions Options { get; }
    internal FrameStore Store { get; }

    public PreviewImpl(AnimationSourceImpl src, AnimationOptions options, FrameStore store) { _src = src; Options = options; Store = store; }

    volatile IReadOnlyList<string> _notes = Array.Empty<string>();

    public bool IsPlanned => _plan is not null;
    public int FrameCount => _plan?.Frames.Count ?? 0;
    public IReadOnlyList<int> Order => _plan?.Order ?? Array.Empty<int>();
    public IReadOnlyList<int> DelaysMs => _plan?.DelaysMs ?? Array.Empty<int>();
    public double Progress => _plan is { } p && p.Frames.Count > 0 ? Math.Min(1.0, Enumerable.Range(0, p.Frames.Count).Count(i => Store.Get(i) is not null) / (double)p.Frames.Count) : 0;
    public string Status => _status;
    public bool IsComplete => _complete;
    public string? Error => _error;
    /// <summary>The plan's notes, then what the frames show: why nothing (or little) changes on this photo with these settings.</summary>
    public IReadOnlyList<string> Notes => _notes;
    public event EventHandler? Changed;

    public PreviewFrame? Frame(int index) => Store.Best(index);

    public PreviewFrame? NearestFrame(int index)
    {
        int n = Math.Max(FrameCount, 1);
        for (int d = 0; d < n; d++)
        {
            if (Store.Best(index - d) is { } a) return a;
            if (Store.Best(index + d) is { } b) return b;
        }
        return null;
    }

    /// <summary>The share of the picture whose colour changes visibly (more than 16 of 255 in a channel) between two frames of one size, and
    /// whether the two are the same picture (no channel differs by more than 2).</summary>
    internal static (double Visible, bool Same) Change(PreviewFrame a, PreviewFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (1, false);
        long n = (long)a.Width * a.Height, visible = 0, small = 0;
        var pa = a.Rgba; var pb = b.Rgba;
        for (long i = 0; i < n * 4; i += 4)
        {
            int d = Math.Max(Math.Abs(pa[i] - pb[i]), Math.Max(Math.Abs(pa[i + 1] - pb[i + 1]), Math.Abs(pa[i + 2] - pb[i + 2])));
            if (d > 16) visible++;
            if (d > 2) small++;
        }
        return ((double)visible / n, small == 0);
    }

    /// <summary>Plain reasons a depth-effect animation changes nothing or little, from its plan and its end frames (an effect that cannot
    /// produce a visible change says why).</summary>
    internal static List<string> Advice(AnimationOptions o, AnimationPlan plan, float aif, PreviewFrame? first, PreviewFrame? last)
    {
        var notes = new List<string>(plan.Notes);
        var inv = CultureInfo.InvariantCulture;
        if (o.Effect is not (AnimationEffect.RackFocus or AnimationEffect.ApertureSweep) || plan.Frames.Count < 2) return notes;
        var f0 = plan.Frames[0]; var fn = plan.Frames[^1];
        string M(float mm) => (mm / 1000f).ToString(mm >= 10_000 ? "0" : "0.00", inv) + " m";
        if (o.Effect == AnimationEffect.RackFocus && f0.FocusMm is float z1 && fn.FocusMm is float z2)
        {
            // the blur follows 1/distance: two ends that close in 1/distance give the same picture
            double span = Math.Abs(1.0 / z1 - 1.0 / z2) * 1000;   // dioptres
            if (span < 0.02)
                notes.Add(z1 == z2 ? $"The focus does not move: it starts and ends at {M(z1)}. Pick a nearer start and a farther end on the photo."
                                   : $"The focus hardly moves: from {M(z1)} to {M(z2)} looks the same to the depth effect (both are far away). Pick a nearer start on the photo.");
        }
        if (o.Effect == AnimationEffect.ApertureSweep && plan.Frames.All(f => f.FNumber is null))
            notes.Add(string.Format(inv, "Every frame is all in focus: f/{0:0.#} is not below this photo's all-in-focus f/{1:0.#}. Choose a smaller f-number to open to.", o.FNumber, aif));
        if (first is null || last is null || notes.Count > plan.Notes.Count) return notes;
        var (visible, same) = Change(first, last);
        string pct = (visible * 100).ToString(visible < 0.01 ? "0.0" : "0", inv);
        if (same)
            notes.Add("Every frame looks the same on this photo with these settings: the depth effect leaves this scene unchanged here.");
        else if (visible < 0.03)
        {
            string how = o.Effect == AnimationEffect.RackFocus
                ? (o.FNumber > 2.05f ? "A smaller f-number, or a start and end picked on near and far subjects, makes it stronger." : "Picking the start and end on near and far subjects makes it stronger.")
                : (o.FNumber > 2.05f ? "Opening to a smaller f-number makes it stronger." : "f/2 is the widest aperture the depth effect offers.");
            notes.Add($"A gentle effect on this photo: about {pct} % of the picture changes visibly between the first and last frame (the parts far from the focus). {how}");
        }
        return notes;
    }

    void Raise() { try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { } }

    internal void Start() => _ = Task.Run(RunAsync);
    internal void Stop() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() => Stop();

    /// <summary>First, last, middle, then the middles of what is left: the whole animation is covered early.</summary>
    internal static List<int> CoarseOrder(int n)
    {
        var o = new List<int>(); var seen = new HashSet<int>();
        void Add(int i) { if (i >= 0 && i < n && seen.Add(i)) o.Add(i); }
        Add(0); Add(n - 1);
        var q = new Queue<(int, int)>(); q.Enqueue((0, n - 1));
        while (q.Count > 0)
        {
            var (a, b) = q.Dequeue();
            if (b - a < 2) continue;
            int m = (a + b) / 2; Add(m);
            q.Enqueue((a, m)); q.Enqueue((m, b));
        }
        for (int i = 0; i < n; i++) Add(i);
        return o;
    }

    async Task RunAsync()
    {
        var ct = _cts.Token;
        // the preview's renders in the Queue as an activity named after the photo
        using var row = _src.Lib.ActivityLog.Start(_src.CaptureId, $"Animation preview of {_src.Item.Name} · {AnimationEffects.NameOf(Options.Effect)}", "Starting", Stop);
        void Show() => row.Update(_status, _plan is null ? null : Progress);
        Changed += (_, _) => Show();
        try
        {
            bool depth = Options.Effect is not (AnimationEffect.Wigglegram or AnimationEffect.LensTour);
            if (depth) await _src.EnsureDepthAsync(s => { _status = s; Raise(); }, ct).ConfigureAwait(false);
            _status = depth ? "Reading the depth" : "Reading the lenses"; Raise();
            await _src.RenderGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                var spec = _src.Oriented(AnimationSourceImpl.PreviewSpec(Options), Options);
                var r = _src.WithSession(s => AnimationRenderer.CreateAsync(s, spec, null, ct).GetAwaiter().GetResult());
                _plan = r.Plan;
                float aif = _src.AllInFocusFNumber;
                void Advise() { _notes = Advice(Options, r.Plan, aif, Store.Get(0), Store.Get(r.Plan.Frames.Count - 1)); }
                Advise();
                Raise();
                var order = CoarseOrder(r.Plan.Frames.Count);
                int done = order.Count(i => Store.Get(i) is not null);
                foreach (var k in order)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_src.IsDisposed) return;
                    if (Store.Get(k) is not null) continue;
                    _status = $"Preparing frames · {done} of {order.Count}"; Raise();
                    var img = r.Render(k, ct);
                    Store.Put(new PreviewFrame(k, img.W, img.H, img.P, r.Plan.Frames[k].Label));
                    done++;
                    if (k == 0 || k == r.Plan.Frames.Count - 1) Advise();
                    Raise();
                }
                Advise();
                _status = $"{order.Count} frames ready ({sw.Elapsed.TotalSeconds:0.0} s)";
                _complete = true;
                Raise();
                // the sharper pass (depth effects): the same frames at the size the file will have, replacing the quick ones as they come
                if (AnimationSourceImpl.SharpSpec(Options) is { } sharp0)
                {
                    var sharp = _src.Oriented(sharp0, Options);
                    string key = AnimationSourceImpl.SharpKey(Options);
                    _status = $"{order.Count} frames ready · sharpening {order.Count(i => Store.HasSharp(i, key))} of {order.Count}"; Raise();
                    var r2 = _src.WithSession(s => AnimationRenderer.CreateAsync(s, sharp, null, ct).GetAwaiter().GetResult());
                    if (r2.Plan.Frames.Count == r.Plan.Frames.Count)
                    {
                        int sharpDone = order.Count(i => Store.HasSharp(i, key));
                        foreach (var k in order)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (_src.IsDisposed) return;
                            if (Store.HasSharp(k, key)) continue;
                            _status = $"{order.Count} frames ready · sharpening {sharpDone} of {order.Count}"; Raise();
                            var img = r2.Render(k, ct);
                            Store.PutSharp(new PreviewFrame(k, img.W, img.H, img.P, r2.Plan.Frames[k].Label), key);
                            sharpDone++;
                            Raise();
                        }
                        _status = $"{order.Count} frames ready ({sw.Elapsed.TotalSeconds:0.0} s)";
                        Raise();
                    }
                }
            }
            finally { _src.RenderGate.Release(); }
        }
        catch (OperationCanceledException) { _status = "Stopped"; }
        catch (Exception e) when (e is LibraryException or ExportRequestException or InvalidOperationException or IOException)
        {
            _error = e.Message; _status = "Stopped"; Raise();
        }
    }
}
