using System.Collections.Concurrent;
using System.Diagnostics;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;

namespace Lux.Library.Edit;

/// <summary>
/// The progressive viewport renderer (<see cref="IPhotoView"/>). One worker takes the newest request: the whole level-4 picture first, then
/// the visible tiles of the target level center-out, several at once (each through <see cref="PictureRenderer"/> and the engine's stage cache);
/// a newer request cancels the pass between tiles. While a slider is dragged the pass renders the visible tiles at the drag level — the finest
/// level whose expected restart cost fits the frame budget (measured cost per megapixel and property class, updated from every drag frame) —
/// and the refined pass follows when the drag ends.
/// </summary>
internal sealed class PhotoViewImpl : IPhotoView
{
    readonly PhotoSessionImpl _s;
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    ViewportRequest? _req; long _reqId; CancellationTokenSource? _cts;
    long _doneId;   // the last request whose pass ran to its end (a refresh that is cut off by a newer one is carried into it)
    Task? _worker; volatile bool _stop;
    ViewStatus _status = new() { WaitingForFirstPicture = true };
    PictureGeometry _geometry;
    bool _anyDelivered;
    // drag cost model: ms per megapixel of the output stage, per property class (exposure, contrast, shadows/highlights/clarity,
    // saturation/vibrance/sharpening, temperature/tint) AND per level — the levels differ in how many display tiles run at once (level 4 is one
    // tile on one thread, level 2 twelve, level 1 forty-eight), so the cost per megapixel is not one number. Seeds: measured costs
    // (24 threads at levels 0–2, one thread for the single tile of level 4, half of it for level 3's two),
    // then the measured frames (exponential average)
    readonly double[,] _msPerMpx;
    // plans per (transform, level)
    readonly ConcurrentDictionary<(long GeometryVersion, int Level), PictureRenderer.Plan> _plans = new();
    // the stand-in (blurred level-4 picture)
    (long Version, StandInImage Image)? _standIn;
    Task? _histTask; int _histPending;

    public event EventHandler<PictureGeometry>? GeometryChanged;
    public event EventHandler<PictureTile>? TileReady;
    public event EventHandler<ViewMode>? VersionChanged;
    public event EventHandler<ViewStatus>? StatusChanged;

    public string CaptureId => _s.CaptureId;

    internal PhotoViewImpl(PhotoSessionImpl s)
    {
        _s = s;
        double k = Math.Max(1.0, 24.0 / Math.Max(1, s.Settings.Threads));
        // class 5 = the depth-effect slider (a new f-number re-makes the depth-effect tiles and runs the whole output stage)
        var at24 = new[] { 4.7, 5.4, 27.0, 38.0, 47.0, 900.0 }; var single = new[] { 26.5, 34.3, 138.0, 190.0, 200.0, 9000.0 };
        _msPerMpx = new double[6, 5];
        for (int c = 0; c < 6; c++)
            for (int l = 0; l < 5; l++)
                _msPerMpx[c, l] = l <= 2 ? Math.Min(single[c], at24[c] * k) : l == 3 ? Math.Min(single[c], single[c] / 2 * Math.Max(1, k / 12)) : single[c];
        // The depth-effect drag per level, measured at fit with 24 threads:
        // a level-2 frame 0.49 s over its 3.7 Mpx of display stage, level 3 0.38 s over 1.1, level 4 0.17 s over 0.34 — the coarse levels have
        // only one to four depth-effect tiles to share among the threads. Fewer threads scale the finer levels up
        double[] dof = { 130, 130, 130, 360, 500 };
        for (int l = 0; l < 5; l++) _msPerMpx[5, l] = dof[l] * (l <= 2 ? k : l == 3 ? Math.Max(1, k / 4) : Math.Max(1, k / 8));
        _geometry = MakeGeometry(s.EditImpl.Spec);
    }

    internal void Start()
    {
        _worker = Task.Factory.StartNew(WorkerLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        RequestHistogram();
    }

    internal void Stop()
    {
        _stop = true;
        lock (_lock) _cts?.Cancel();
        _signal.Release();
        try { _worker?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { _histTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    // ------------------------------------------------------------------------------------------------ geometry

    public PictureGeometry Geometry { get { lock (_lock) return _geometry; } }

    PictureGeometry MakeGeometry(RenderSpec spec)
    {
        var sizes = new List<PixelSize>();
        for (int l = 0; l < _s.Pictures.Window.ExportDims.Length; l++)
        {
            var sz = PictureRenderer.Rotated(_s.Pictures.SizeOf(spec.PictureTransform, l), spec.DisplayRotation);
            sizes.Add(new PixelSize(sz.W, sz.H));
        }
        return new PictureGeometry(sizes, _s.Options.TileSize, spec.DisplayRotation, spec.Layout, spec.GeometryVersion) { Cause = spec.Cause, TurnFromPrevious = spec.TurnFromPrevious };
    }

    /// <summary>The edit changed: new versions (and maybe a new geometry); the last request runs again.</summary>
    internal void OnSpecChanged(bool geometry, bool committed)
    {
        var spec = _s.EditImpl.Spec;
        PictureGeometry? g = null;
        if (geometry)
        {
            g = MakeGeometry(spec);
            lock (_lock) _geometry = g;
            foreach (var k in _plans.Keys) if (k.GeometryVersion < spec.GeometryVersion - 1) _plans.TryRemove(k, out _);
            try { GeometryChanged?.Invoke(this, g); } catch (Exception e) { _s.Lib.Trace("view listener: " + e.Message); }
        }
        try
        {
            VersionChanged?.Invoke(this, ViewMode.Edited); if (geometry || committed) VersionChanged?.Invoke(this, ViewMode.Original);
            if (geometry) VersionChanged?.Invoke(this, ViewMode.Depth);
        }
        catch (Exception e) { _s.Lib.Trace("view listener: " + e.Message); }
        ViewportRequest? last;
        lock (_lock) last = _req;
        if (last is null) return;
        if (geometry)
        {
            // a new geometry: the held tiles are void; ask again for the same visible part (clamped to the new picture)
            last = last with { Needed = null, HasCoarse = false, Level = null };
        }
        else
        {
            // a change of the photo modes never touches the depth view (and the reverse): a depth request is re-run only for geometry
            if (last.Mode == ViewMode.Depth) return;
            last = last with { Needed = null, HasCoarse = last.HasCoarse && !committed };
        }
        RequestViewport(last with { RefreshOnly = null });
    }

    /// <summary>What the capture keeps changed (a quality made or released while the photo is open): the pixels do not change, the finest level
    /// may — the last request runs again with the level left to the library (the screen's need, capped by the new finest level), so the status
    /// reports the new finest level and the viewer asks for it.</summary>
    internal void OnCacheChanged()
    {
        ViewportRequest? last;
        lock (_lock) last = _req;
        if (last is null || last.Mode == ViewMode.Depth) return;
        RequestViewport(last with { Level = null, Needed = null, HasCoarse = true, RefreshOnly = null, RefreshAreas = null });
    }

    /// <summary>Parts of the picture changed without a geometry change and without a floor raise (a depth edit, a stroke preview, the
    /// selection after a stroke): the versions are already new (<see cref="PhotoEditImpl.BumpForDepth"/>); the last request runs again for the
    /// tiles that meet the changed region of its mode only (the coarse level too when the viewer holds it).</summary>
    internal void OnRegionsChanged(NormRect? photo, NormRect? depth, IReadOnlyList<PictureRegion>? photoAreas = null)
    {
        try
        {
            if (photo is not null) { VersionChanged?.Invoke(this, ViewMode.Edited); VersionChanged?.Invoke(this, ViewMode.Original); }
            if (depth is not null) VersionChanged?.Invoke(this, ViewMode.Depth);
        }
        catch (Exception e) { _s.Lib.Trace("view listener: " + e.Message); }
        ViewportRequest? last; bool unfinished;
        lock (_lock) { last = _req; unfinished = _doneId != _reqId; }
        if (last is null) return;
        var rect = last.Mode == ViewMode.Depth ? depth : photo;
        if (rect is not { } r) return;
        var areas = last.Mode == ViewMode.Depth ? null : photoAreas;
        if (unfinished)
        {
            // the last request has not finished: a full pass stays full; a refresh still to finish is merged with this one (nothing it was to
            // re-render is lost when this request cuts it off)
            if (last.RefreshOnly is not { } prev) { RequestViewport(last with { Needed = null }); return; }
            var box = Union(prev, r);
            // a region given without per-level areas counts at every level
            var merged = (last.RefreshAreas ?? new[] { new PictureRegion(-1, prev) }).Concat(areas ?? new[] { new PictureRegion(-1, r) }).ToList();
            r = box; areas = merged;
        }
        RequestViewport(last with { Needed = null, RefreshOnly = r, RefreshAreas = areas });
    }

    static NormRect Union(NormRect a, NormRect b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.Right, b.Right), y1 = Math.Max(a.Bottom, b.Bottom);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }

    // ------------------------------------------------------------------------------------------------ versions and status

    public long Version(ViewMode mode) => _s.EditImpl.Versions(mode).Version;
    public long Floor(ViewMode mode) => _s.EditImpl.Versions(mode).Floor;
    public ViewStatus Status { get { lock (_lock) return _status; } }

    long _statusRaisedTicks;
    bool _statusTrailing;
    /// <summary>Update the status; listeners hear of it at most every 50 ms whatever changed, and always of the latest state (a trailing event
    /// after the window). Without this, the passes a tool run starts — thousands of viewport requests, each a status change — would reach
    /// the page at render rate.</summary>
    void SetStatus(Func<ViewStatus, ViewStatus> f)
    {
        ViewStatus s; long wait;
        lock (_lock)
        {
            var old = _status; _status = f(_status); s = _status;
            if (old == s) return;
            long now = Environment.TickCount64;
            wait = 50 - (now - _statusRaisedTicks);
            if (wait <= 0) _statusRaisedTicks = now;
            else if (_statusTrailing) return;   // a trailing event is coming and will carry this state
            else _statusTrailing = true;
        }
        if (wait <= 0) { RaiseStatus(s); return; }
        _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ =>
        {
            ViewStatus cur;
            lock (_lock) { _statusTrailing = false; _statusRaisedTicks = Environment.TickCount64; cur = _status; }
            RaiseStatus(cur);
        }, TaskScheduler.Default);
    }

    void RaiseStatus(ViewStatus s)
    {
        try { StatusChanged?.Invoke(this, s); } catch (Exception e) { _s.Lib.Trace("view listener: " + e.Message); }
    }

    static ViewQuality QualityOfLevel(int level) => level switch { 0 => ViewQuality.FullDetail, 1 => ViewQuality.Linear13, _ => ViewQuality.Preview };
    static int LevelOfQuality(ViewQuality q) => q switch { ViewQuality.FullDetail => 0, ViewQuality.Linear13 => 1, _ => 2 };

    /// <summary>The finest level a request may reach: the quality cap when the request gives one (the user asked), else what the capture keeps
    /// (a photo rendered from an imported Lumen session renders its own geometry and never reads the cache: the preview levels then).</summary>
    int FinestLevel(ViewportRequest r)
    {
        if (r.QualityCap is ViewQuality q) return LevelOfQuality(q);
        var item = _s.Lib.Get(_s.CaptureId) ?? _s.Item;
        // a photo rendered from an imported Lumen session follows the quality chosen for the capture as well (its finer
        // levels are made on the fly from the session's geometry: the cache does not serve them)
        return LevelOfQuality(item.BestQuality);
    }

    // ------------------------------------------------------------------------------------------------ requests

    /// <summary>The visible part of the last request (the quick-select default region).</summary>
    internal NormRect? LastVisible { get { lock (_lock) return _req is { } r ? Clip(r.Visible) : null; } }

    public long RequestViewport(ViewportRequest request)
    {
        if (request.ScreenWidth <= 0 || request.ScreenHeight <= 0) throw new ArgumentException("the screen size must be positive");
        long id;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _req = request; id = ++_reqId;
        }
        if (request.Interactive) _s.Lib.Queue.SetInteractive(true);
        _signal.Release();
        return id;
    }

    async Task WorkerLoop()
    {
        while (!_stop)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            if (_stop) return;
            ViewportRequest? req; long id; CancellationToken tok;
            lock (_lock)
            {
                if (_req is null || _cts is null) continue;
                req = _req; id = _reqId; tok = _cts.Token;
            }
            // several releases may be queued for the same newest request: drain them
            while (_signal.CurrentCount > 0) _signal.Wait(0);
            try { Execute(req, id, tok); }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _s.Lib.Trace($"view {CaptureId[..8]}: {e}");
                SetStatus(s => s with { Rendering = false, Error = "The picture could not be made: " + Core.LibraryCore.Plain(e) });
            }
        }
    }

    /// <summary>The coarsest level whose picture has at least the screen's pixels across the visible rectangle.</summary>
    static int LevelForScreen(PictureGeometry g, ViewportRequest r)
    {
        var v = Clip(r.Visible);
        for (int l = g.LevelCount - 1; l >= 0; l--)
        {
            var s = g.LevelSizes[l];
            if (v.Width * s.Width >= r.ScreenWidth - 0.5 && v.Height * s.Height >= r.ScreenHeight - 0.5) return l;
        }
        return 0;
    }

    static NormRect Clip(NormRect r)
    {
        double x0 = Math.Clamp(r.X, 0, 1), y0 = Math.Clamp(r.Y, 0, 1), x1 = Math.Clamp(r.Right, 0, 1), y1 = Math.Clamp(r.Bottom, 0, 1);
        return new NormRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    static int ClassOf(AdjustmentKey k) => k switch
    {
        AdjustmentKey.Exposure => 0, AdjustmentKey.Contrast => 1, AdjustmentKey.Shadows or AdjustmentKey.Highlights or AdjustmentKey.Clarity => 2,
        AdjustmentKey.Saturation or AdjustmentKey.Vibrance or AdjustmentKey.Sharpening => 3, _ => 4,
    };

    /// <summary>Megapixels the engine runs the output stage on for a set of picture tiles (each display tile is processed whole with a 64-pixel
    /// halo: the area of the 512-grid tiles under them, grown).</summary>
    static double StageMpx(PictureGeometry g, int level, NormRect v)
    {
        var s = g.LevelSizes[level];
        double w = Math.Max(1, v.Width * s.Width), h = Math.Max(1, v.Height * s.Height);
        // whole 512 display tiles under the rectangle, each grown by the halo
        double tx = Math.Ceiling(w / 512) + 1, ty = Math.Ceiling(h / 512) + 1;
        double tw = Math.Min(s.Width, 512) + 128, th = Math.Min(s.Height, 512) + 128;
        return Math.Min(tx * tw, s.Width + 128.0) * Math.Min(ty * th, s.Height + 128.0) / 1e6;
    }

    /// <summary>Display tiles (the export's 512 grid) under the visible part of a level — the parallelism of its output stage.</summary>
    static int DisplayTiles(PictureGeometry g, int level, NormRect v)
    {
        var s = g.LevelSizes[level];
        static int Grid(int w) { int n = w / 512; if (w % 512 != 0) n++; if (n > 1 && w < 512 * n) n--; return Math.Max(1, n); }
        int nx = Math.Min(Grid(s.Width), (int)Math.Ceiling(Math.Max(1, v.Width * s.Width) / 512) + 1);
        int ny = Math.Min(Grid(s.Height), (int)Math.Ceiling(Math.Max(1, v.Height * s.Height) / 512) + 1);
        return nx * ny;
    }

    /// <summary>The expected time of a drag frame at a level: the class's cost per megapixel at that level × the megapixels its output stage runs on.</summary>
    double DragEstimate(PictureGeometry g, int level, NormRect v, int cls)
    {
        double perMpx; lock (_msPerMpx) perMpx = _msPerMpx[cls, Math.Min(level, 4)];
        return perMpx * StageMpx(g, level, v);
    }

    /// <summary>The drag level: from the target towards the coarsest allowed, the first whose expected frame fits the budget; when none does, the
    /// fastest (the finer one when within 10 %) — a coarse level with a single display tile is not faster than a finer one spread over all threads.</summary>
    int DragLevel(PictureGeometry g, int target, NormRect v, int cls)
    {
        int coarsest = Math.Max(target, Math.Clamp(_s.Settings.DragCoarsestLevel, 2, g.LevelCount - 1));
        double budget = _s.Settings.DragBudgetMs;
        int best = target; double bestEst = double.MaxValue;
        for (int l = target; l <= coarsest; l++)
        {
            double est = DragEstimate(g, l, v, cls);
            if (est <= budget) return l;
            if (est < bestEst * 0.9) { best = l; bestEst = est; }
        }
        return best;
    }

    /// <summary>The measured drag cost (exponential average).</summary>
    internal double[,] DragCosts { get { lock (_msPerMpx) return (double[,])_msPerMpx.Clone(); } }
    /// <summary>The last drag frames (level, ms, Mpx) — tests and timings.</summary>
    internal readonly ConcurrentQueue<(AdjustmentKey Key, int Level, double Ms, double Mpx, int Tiles)> DragFrames = new();
    long _passCount;
    (int Class, int Level) _lastDrag = (-1, -1);
    /// <summary>Passes completed so far (tests).</summary>
    internal long PassCount => Interlocked.Read(ref _passCount);
    /// <summary>The last passes (request id, level, ms to first tile, ms total, tiles) — tests and timings.</summary>
    internal readonly ConcurrentQueue<(long Request, int Level, double FirstMs, double TotalMs, int Tiles, bool Drag)> Passes = new();

    void Execute(ViewportRequest req, long id, CancellationToken tok)
    {
        var spec = _s.EditImpl.Spec;
        var g = Geometry;
        if (g.Version != spec.GeometryVersion) { g = MakeGeometry(spec); lock (_lock) _geometry = g; }
        var mode = req.Mode;
        var vis = Clip(req.Visible);
        if (vis.Width <= 0 || vis.Height <= 0) vis = NormRect.Full;
        bool drag = spec.Dragging && mode == ViewMode.Edited;
        // the depth effect reads the photo's depth: make sure it is attached (with the photo's depth edits) before rendering
        if (mode != ViewMode.Depth && spec.For(mode).DepthEffectFNumber is float dfn && dfn < _s.Engine.Info.AllInFocusFNumber && !_s.DepthImpl.WaitForDepth(tok))
            tok.ThrowIfCancellationRequested();
        if (mode == ViewMode.Depth && !_s.DepthImpl.DepthViewReady)
        {
            SetStatus(s => s with { Rendering = false, TilesDone = 0, TilesPlanned = 0, Error = null });
            _s.DepthImpl.EnsureDepthInBackground();
            return;
        }
        int finest = Math.Clamp(drag && _anyDelivered ? Status.FinestLevel : FinestLevel(req), 0, g.LevelCount - 1);
        int target = Math.Clamp(req.Level ?? LevelForScreen(g, req), 0, g.LevelCount - 1);
        target = Math.Max(target, finest);
        int dragClass = spec.DepthDrag ? 5 : spec.DragKey is { } dk ? ClassOf(dk) : 0;
        int level = drag ? DragLevel(g, target, vis, dragClass) : target;
        int coarse = g.LevelCount - 1;

        var plan = new List<TileId>();
        if (req.RefreshOnly is { } only)
        {
            // only the tiles meeting the changed region (the coarse picture's too: the viewer holds it)
            if (level != coarse) plan.AddRange(CentreOut(g, coarse, NormRect.Full).Where(t => MeetsAny(g, t, only, req.RefreshAreas)));
        }
        else if (!drag && !req.HasCoarse && !req.Interactive && level != coarse) plan.AddRange(CentreOut(g, coarse, NormRect.Full));
        var main = CentreOut(g, level, vis);
        if (req.RefreshOnly is { } only2) main = main.Where(t => MeetsAny(g, t, only2, req.RefreshAreas)).ToList();
        else if (!drag && req.Needed is { } need && req.Level == level)
        {
            // the tiles in the order the viewer gives them (its own center-out / swap policy)
            var mine = main.ToHashSet(); main = need.Where(mine.Remove).ToList();
        }
        plan.AddRange(main);
        // the readiness of the requested quality (library reads): not on every drag frame
        var quality = drag ? Status.Quality : SafeQualityState(QualityOfLevel(target));
        bool lumen = _s.Engine.SavedState?.SessionGeometry is not null;
        string? note = lumen && finest >= 2
            ? "This photo shows the version imported from Lumen, which uses Lumen's own camera geometry and depth: choose 13 MP or Full detail to see it sharper close up (those pictures are made while you look, not taken from kept copies)."
            : lumen ? "This photo shows the version imported from Lumen: its 13 MP and full-detail pictures are made while you look, not taken from kept copies, so they take longer the first time."
            : null;
        SetStatus(s => s with { Rendering = true, TargetLevel = target, FinestLevel = finest, TilesDone = 0, TilesPlanned = plan.Count, Quality = quality, Error = null,
                                RendersFromLumenSession = lumen, QualityNote = note });

        var sw = Stopwatch.StartNew(); double firstMs = -1; int done = 0, finestDone = int.MaxValue;
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _s.Settings.Threads), CancellationToken = tok };
        void Run(IReadOnlyList<TileId> tiles)
        {
            Parallel.ForEach(tiles, po, t =>
            {
                tok.ThrowIfCancellationRequested();
                var tile = RenderTile(t, spec, g, mode, id, drag && t.Level == level, tok);
                lock (_lock)
                {
                    done++; if (firstMs < 0) firstMs = sw.Elapsed.TotalMilliseconds;
                    if (t.Level < finestDone) finestDone = t.Level;
                    _anyDelivered = true;
                }
                try { TileReady?.Invoke(this, tile); } catch (Exception e) { _s.Lib.Trace("view listener: " + e.Message); }
                SetStatus(s => s with { TilesDone = done, OnScreen = QualityOfLevel(Math.Min(finestDone, coarse)), WaitingForFirstPicture = false });
            });
        }
        // coarse first (all of it), then the main level — the coarse picture is on screen before any fine tile
        var coarseTiles = plan.Where(t => t.Level == coarse && level != coarse).ToList();
        double t0 = Depth.DepthProfile.Now;
        if (coarseTiles.Count > 0) { Run(coarseTiles); Depth.DepthProfile.Add($"view.coarse L{coarse} {coarseTiles.Count} tiles", t0, Depth.DepthProfile.Now - t0); }
        Run(main);
        double total = sw.Elapsed.TotalMilliseconds;
        if (Depth.DepthProfile.On) Depth.DepthProfile.Add($"view.pass L{level} {main.Count} tiles{(drag ? " drag" : "")}{(req.RefreshOnly is null ? "" : " refresh")}", t0, Depth.DepthProfile.Now - t0);
        Passes.Enqueue((id, level, firstMs, total, plan.Count, drag)); while (Passes.Count > 200) Passes.TryDequeue(out _);
        Interlocked.Increment(ref _passCount);
        if (drag && main.Count > 0)
        {
            double mpx = StageMpx(g, level, vis);
            int c = dragClass, lv = Math.Min(level, 4);
            // the first frame at a level also fetches its tiles' linear input (never shown before): it does not teach the restart cost
            lock (_msPerMpx) { if (_lastDrag == (c, level)) _msPerMpx[c, lv] = 0.7 * _msPerMpx[c, lv] + 0.3 * (total / Math.Max(mpx, 1e-3)); _lastDrag = (c, level); }
            DragFrames.Enqueue((spec.DragKey ?? AdjustmentKey.Exposure, level, total, mpx, main.Count)); while (DragFrames.Count > 500) DragFrames.TryDequeue(out _);
        }
        lock (_lock) if (id > _doneId) _doneId = id;
        SetStatus(s => s with { Rendering = false });
        if (req.Interactive) _s.Lib.Queue.SetInteractive(false);
    }

    QualityState? SafeQualityState(ViewQuality q)
    {
        try { return _s.Lib.GetQualityState(_s.CaptureId, q); } catch (LibraryException) { return null; }
    }

    /// <summary>Whether a tile meets the refresh region: one of <paramref name="areas"/> when given, else <paramref name="r"/>.</summary>
    static bool MeetsAny(PictureGeometry g, TileId t, NormRect r, IReadOnlyList<PictureRegion>? areas)
    {
        if (areas is null) return Meets(g, t, r);
        foreach (var a in areas) if ((a.Level < 0 || a.Level == t.Level) && Meets(g, t, a.Rect)) return true;
        return false;
    }

    /// <summary>Whether a tile meets a normalized rectangle.</summary>
    static bool Meets(PictureGeometry g, TileId t, NormRect r)
    {
        var s = g.LevelSizes[t.Level]; var rc = g.TileRect(t.Level, t.Col, t.Row);
        double x0 = (double)rc.X / s.Width, y0 = (double)rc.Y / s.Height, x1 = (double)rc.Right / s.Width, y1 = (double)rc.Bottom / s.Height;
        return x0 < r.Right && r.X < x1 && y0 < r.Bottom && r.Y < y1;
    }

    /// <summary>The tiles of a level covering a rectangle, center-out.</summary>
    static List<TileId> CentreOut(PictureGeometry g, int level, NormRect r)
    {
        var tiles = g.TilesCovering(level, r);
        var s = g.LevelSizes[level];
        double cx = (r.X + r.Width / 2) * s.Width, cy = (r.Y + r.Height / 2) * s.Height;
        return tiles.OrderBy(t => { var rc = g.TileRect(level, t.Col, t.Row); double dx = rc.X + rc.Width / 2.0 - cx, dy = rc.Y + rc.Height / 2.0 - cy; return dx * dx + dy * dy; }).ToList();
    }

    PictureRenderer.Plan PlanOf(RenderSpec spec, int level)
        => _plans.GetOrAdd((spec.GeometryVersion, level), _ => _s.Pictures.PlanFor(spec.PictureTransform, level));

    /// <summary>Render one displayed tile: the picture rect under it (the display rotation undone), the export's pixels, turned back.</summary>
    PictureTile RenderTile(TileId t, RenderSpec spec, PictureGeometry g, ViewMode mode, long reqId, bool dragPreview, CancellationToken tok)
    {
        var rect = g.TileRect(t.Level, t.Col, t.Row);
        var px = RenderDisplayed(t.Level, rect, spec, mode, tok);
        return new PictureTile
        {
            Mode = mode, Id = t, Rect = rect, Version = spec.Version(mode), GeometryVersion = spec.GeometryVersion, Request = reqId, Pixels = px,
            Order = _s.Options.PixelOrder, IsDragPreview = dragPreview,
        };
    }

    /// <summary>A displayed rect of a level (display rotation applied), in the session's pixel order.</summary>
    byte[] RenderDisplayed(int level, PixelRect rect, RenderSpec spec, ViewMode mode, CancellationToken tok)
    {
        var p = PlanOf(spec, level);
        var d = new RectI(rect.X, rect.Y, rect.Right, rect.Bottom);
        var pr = PictureRenderer.PictureRectOf(d, spec.DisplayRotation, p.Size);
        var set = spec.For(mode);
        if (spec.Dragging && mode == ViewMode.Edited) set = set with { Transient = true };   // drag frames keep their depth-effect tiles apart
        var px = mode == ViewMode.Depth ? _s.DepthImpl.RenderDepthView(p, pr, tok) : _s.Pictures.Render(p, pr, set, tok);
        px = PictureRenderer.Turn(px, pr.Width, pr.Height, spec.DisplayRotation);
        if (_s.Options.PixelOrder == PixelOrder.Bgra) PictureRenderer.SwapRb(px);
        return px;
    }

    public Task<byte[]> RenderRegionAsync(int level, PixelRect rect, ViewMode mode = ViewMode.Edited, CancellationToken cancel = default) => Task.Run(() =>
    {
        var spec = _s.EditImpl.Spec;
        if (mode != ViewMode.Depth && spec.For(mode).DepthEffectFNumber is float dfn && dfn < _s.Engine.Info.AllInFocusFNumber) _s.DepthImpl.WaitForDepth(cancel);
        else if (mode == ViewMode.Depth && !_s.DepthImpl.WaitForDepth(cancel)) throw new InvalidOperationException("the depth of this photo is not available");
        spec = _s.EditImpl.Spec;
        return RenderDisplayed(level, rect, spec, mode, cancel);
    }, cancel);

    // ------------------------------------------------------------------------------------------------ histogram and stand-in

    /// <summary>Recompute the histogram (whole edited picture at level 4, as cut and turned) for the current version, in the background.</summary>
    internal void RequestHistogram()
    {
        if (Interlocked.Exchange(ref _histPending, 1) == 1) return;
        _histTask = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _histPending, 0) == 1 && !_stop)
                {
                    var spec = _s.EditImpl.Spec;
                    if (spec.Dragging) { Thread.Sleep(30); Interlocked.Exchange(ref _histPending, 1); continue; }
                    // a picture with the depth effect reads the photo's depth (with its edits): wait for it
                    if (spec.Edited.DepthEffectFNumber is float hf && hf < _s.Engine.Info.AllInFocusFNumber && !_s.DepthImpl.WaitForDepth(CancellationToken.None)) continue;
                    spec = _s.EditImpl.Spec;
                    int lv = _s.Pictures.Window.ExportDims.Length - 1;
                    // the histogram is of the picture as exported (cropped), also while the crop tool shows the uncropped one
                    var t = spec.Layout == PictureLayout.Picture ? spec.PictureTransform : _s.EditImpl.CurrentState.Geo.T.ToExport();
                    var p = _s.Pictures.PlanFor(t, lv);
                    var px = _s.Pictures.Render(p, new RectI(0, 0, p.Size.W, p.Size.H), spec.Edited, CancellationToken.None);
                    var (r, gg, b, y) = PhotoEditImpl.Count(px);
                    _s.EditImpl.SetHistogram(new Histogram(r, gg, b, y, lv, new PixelSize(p.Size.W, p.Size.H), spec.EditedVersion));
                    // the stand-in from the same picture (display rotation applied)
                    var turned = PictureRenderer.Turn(px, p.Size.W, p.Size.H, spec.DisplayRotation);
                    var (tw, th) = PictureRenderer.Rotated(p.Size, spec.DisplayRotation);
                    var blurred = Blur(turned, tw, th, 6);
                    if (_s.Options.PixelOrder == PixelOrder.Bgra) PictureRenderer.SwapRb(blurred);
                    lock (_lock) _standIn = (spec.EditedVersion, new StandInImage(tw, th, blurred, _s.Options.PixelOrder, null));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { _s.Lib.Trace($"histogram {CaptureId[..8]}: {e.Message}"); }
        });
    }

    public async Task<StandInImage?> GetStandInAsync(CancellationToken cancel = default)
    {
        lock (_lock) if (_standIn is { } s) return s.Image;
        var thumb = _s.Lib.TryGetStoredThumbnail(_s.CaptureId);
        if (thumb is not null) return new StandInImage(thumb.Width, thumb.Height, null, _s.Options.PixelOrder, thumb.Data);
        // nothing yet: wait briefly for the first level-4 picture
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !cancel.IsCancellationRequested)
        {
            lock (_lock) if (_standIn is { } s2) return s2.Image;
            await Task.Delay(50, cancel).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Three box-blur passes (radius <paramref name="r"/>) — an approximately Gaussian blur for the placeholder.</summary>
    static byte[] Blur(byte[] src, int w, int h, int r)
    {
        var a = (byte[])src.Clone(); var b = new byte[src.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int x = -r; x <= r; x++) if (x >= 0 && x < w) { sum += a[(y * w + x) * 4 + c]; n++; }
                    for (int x = 0; x < w; x++)
                    {
                        b[(y * w + x) * 4 + c] = (byte)(sum / Math.Max(1, n));
                        int xo = x - r, xi = x + r + 1;
                        if (xo >= 0) { sum -= a[(y * w + xo) * 4 + c]; n--; }
                        if (xi < w) { sum += a[(y * w + xi) * 4 + c]; n++; }
                    }
                }
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int y = -r; y <= r; y++) if (y >= 0 && y < h) { sum += b[(y * w + x) * 4 + c]; n++; }
                    for (int y = 0; y < h; y++)
                    {
                        a[(y * w + x) * 4 + c] = (byte)(sum / Math.Max(1, n));
                        int yo = y - r, yi = y + r + 1;
                        if (yo >= 0) { sum -= b[(yo * w + x) * 4 + c]; n--; }
                        if (yi < h) { sum += b[(yi * w + x) * 4 + c]; n++; }
                    }
                }
        }
        return a;
    }

    // ------------------------------------------------------------------------------------------------ points

    PictureRenderer.Plan PictureLevel0()
    {
        var t = _s.EditImpl.CurrentState.Geo.T.ToExport();
        return _s.Pictures.PlanFor(t, 0);
    }

    (double X, double Y) UnrotateDisplay(NormPoint p, int rot) => rot switch
    {
        90 => (p.Y, 1 - p.X), 180 => (1 - p.X, 1 - p.Y), 270 => (1 - p.Y, p.X), _ => (p.X, p.Y),
    };

    NormPoint RotateDisplay(double x, double y, int rot) => rot switch
    {
        90 => new NormPoint(1 - y, x), 180 => new NormPoint(1 - x, 1 - y), 270 => new NormPoint(y, 1 - x), _ => new NormPoint(x, y),
    };

    public NormPoint? PictureToCapture(NormPoint picture)
    {
        if (picture.X < 0 || picture.X > 1 || picture.Y < 0 || picture.Y > 1) return null;
        var spec = _s.EditImpl.Spec;
        var p = PictureLevel0();
        var (x, y) = UnrotateDisplay(picture, spec.DisplayRotation);
        var to = p.To;
        double u = to.B * (y * p.Size.H) + to.A * (x * p.Size.W) + to.C, v = to.E * (y * p.Size.H) + to.D * (x * p.Size.W) + to.F;
        var dims0 = _s.Pictures.Window.ExportDims[0];
        return new NormPoint((u + to.Source.X0) / dims0.W, (v + to.Source.Y0) / dims0.H);
    }

    public NormPoint? CaptureToPicture(NormPoint capture)
    {
        var spec = _s.EditImpl.Spec;
        var p = PictureLevel0();
        var to = p.To;
        var dims0 = _s.Pictures.Window.ExportDims[0];
        double u = capture.X * dims0.W - to.Source.X0, v = capture.Y * dims0.H - to.Source.Y0;
        double det = (double)to.A * to.E - (double)to.B * to.D;
        if (Math.Abs(det) < 1e-12) return null;
        double du = u - to.C, dv = v - to.F;
        double x = (to.E * du - to.B * dv) / det, y = (-to.D * du + to.A * dv) / det;
        double nx = x / p.Size.W, ny = y / p.Size.H;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
        return RotateDisplay(nx, ny, spec.DisplayRotation);
    }

    // ---- the mapping the depth tools use: the picture as shown now (the layout's transform and the display rotation), never clamped — a stroke
    //      may leave the picture (Lumen handles points outside the image). Same arithmetic as PictureToCapture for points inside.

    /// <summary>Picture point (normalized, as shown) → the capture's normalized export-window coordinates, unclamped (double).</summary>
    internal (double X, double Y) ToCapture(NormPoint picture, RenderSpec? spec = null)
    {
        spec ??= _s.EditImpl.Spec;
        var p = PlanOf(spec, 0);
        var (x, y) = UnrotateDisplay(picture, spec.DisplayRotation);
        var to = p.To;
        double u = to.B * (y * p.Size.H) + to.A * (x * p.Size.W) + to.C, v = to.E * (y * p.Size.H) + to.D * (x * p.Size.W) + to.F;
        var dims0 = _s.Pictures.Window.ExportDims[0];
        return (Snap((u + to.Source.X0) / dims0.W), Snap((v + to.Source.Y0) / dims0.H));
    }

    /// <summary>A capture coordinate on the 2^-48 grid: removes the double round-off of a picture → capture round trip (a few 2^-52), so a float
    /// coordinate shown in the picture comes back as the same float (every float ≥ 2^-25 in magnitude lies on the grid; a change of at most
    /// 2^-49 of the width — 3·10^-11 pixels — otherwise).</summary>
    static double Snap(double v) => Math.Round(v * 281474976710656.0) / 281474976710656.0;

    /// <summary>Capture point (normalized export window) → picture point as shown, unclamped (the exact inverse of <see cref="ToCapture"/> in double).</summary>
    internal (double X, double Y) ToPicture(double cx, double cy, RenderSpec? spec = null)
    {
        spec ??= _s.EditImpl.Spec;
        var p = PlanOf(spec, 0);
        var to = p.To;
        var dims0 = _s.Pictures.Window.ExportDims[0];
        double u = cx * dims0.W - to.Source.X0, v = cy * dims0.H - to.Source.Y0;
        double det = (double)to.A * to.E - (double)to.B * to.D;
        double du = u - to.C, dv = v - to.F;
        double x = (to.E * du - to.B * dv) / det, y = (-to.D * du + to.A * dv) / det;
        var r = RotateDisplay(x / p.Size.W, y / p.Size.H, spec.DisplayRotation);
        return (r.X, r.Y);
    }

    /// <summary>A capture rectangle (normalized export window) → the bounding picture rectangle (normalized, clamped to the picture); null when
    /// it lies outside the picture.</summary>
    internal NormRect? CaptureRectToPicture(double x0, double y0, double x1, double y1, RenderSpec? spec = null)
    {
        spec ??= _s.EditImpl.Spec;
        double px0 = double.MaxValue, py0 = double.MaxValue, px1 = double.MinValue, py1 = double.MinValue;
        foreach (var (cx, cy) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
        {
            var (px, py) = ToPicture(cx, cy, spec);
            px0 = Math.Min(px0, px); py0 = Math.Min(py0, py); px1 = Math.Max(px1, px); py1 = Math.Max(py1, py);
        }
        px0 = Math.Max(0, px0); py0 = Math.Max(0, py0); px1 = Math.Min(1, px1); py1 = Math.Min(1, py1);
        if (px1 <= px0 || py1 <= py0) return null;
        return new NormRect(px0, py0, px1 - px0, py1 - py0);
    }

    public async Task<float?> DepthAtAsync(NormPoint picture, bool analyseIfNeeded = false, CancellationToken cancel = default)
    {
        var c = PictureToCapture(picture);
        if (c is null) return null;
        var item = _s.Lib.Get(_s.CaptureId) ?? _s.Item;
        if (!analyseIfNeeded && item.CacheLevel < CacheLevel.Geometry) return null;
        return await _s.Engine.DepthAtAsync((float)c.Value.X, (float)c.Value.Y, cancel).ConfigureAwait(false);
    }
}
