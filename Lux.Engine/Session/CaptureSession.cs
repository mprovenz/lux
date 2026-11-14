using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Session;

/// <summary>How a <see cref="CaptureSession"/> is opened.</summary>
public sealed record SessionOptions
{
    /// <summary>The capture's persistent data (saved depth edits, analysis cache); null = none.</summary>
    public ICaptureContext? Context { get; init; }
    /// <summary>Threads for the tile renders and the registration's parallel parts. The pixels are the same at any count.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;
    /// <summary>Engine trace (verbose; null = silent).</summary>
    public Action<string>? Log { get; init; }
    /// <summary>Read the analysis cache (when <see cref="Context"/> has one).</summary>
    public bool UseCache { get; init; } = true;
    /// <summary>Keep the geometry + depth in the analysis cache as soon as <see cref="CaptureSession.EnsureAnalysisAsync"/> has computed them.</summary>
    public bool KeepGeometry { get; init; } = true;
    /// <summary>Progress of everything the session computes (any thread).</summary>
    public Action<ProgressUpdate>? Progress { get; init; }
    /// <summary>Memory budget for building the full-detail tier (<see cref="MemoryBudget"/>); null = unbounded.</summary>
    public long? MemoryBudgetBytes { get; init; }
    /// <summary>Memory the display render may keep per session for fast slider changes: each recently shown tile's linear input and its
    /// output-stage intermediates (about 25–32 MB per 512 tile). Least recently used tiles go first; 0 keeps nothing.</summary>
    public long DisplayCacheBytes { get; init; } = 1L << 30;
}

/// <summary>What a render returns.</summary>
public enum RenderOutput
{
    /// <summary>Float RGBA, linear: the fused image after the vignetting correction — what the DNG/HDR/PPM writers start from.</summary>
    Linear,
    /// <summary>RGBA8 through the output ISP: the pixels the JPEG export of that level encodes.</summary>
    Display,
}

/// <summary>
/// The per-render settings of <see cref="CaptureSession.RenderAsync"/>. Every default is Lumen's own value, so a default render is the
/// Lumen-identical one. <para>The tone and colour adjustments (exposure, contrast, temperature, tint, vibrance, saturation, sharpening,
/// shadows, highlights, clarity) are <see cref="Adjustments"/>: they act on the display output only (as on the JPG); the linear output
/// is the image before them.</para>
/// </summary>
public sealed record RenderSettings
{
    public RenderOutput Output { get; init; } = RenderOutput.Display;
    /// <summary>The depth effect's f-number (null = all in focus).</summary>
    public float? DepthEffectFNumber { get; init; }
    /// <summary>The depth effect's focus distance, mm (null = Lumen's default focus depth).</summary>
    public float? FocusDepthMm { get; init; }
    /// <summary>The display render's sharpening property (Lumen's export value by default). Ignored when <see cref="Adjustments"/> is set
    /// (its own <see cref="Pipeline.Adjustments.Sharpening"/> applies).</summary>
    public float Sharpening { get; init; } = DisplayIspTuning.GuiSharpeningProperty;
    /// <summary>Lumen's tone and colour settings for the display output; null = none (Lumen's own rendering, with <see cref="Sharpening"/>).</summary>
    public Adjustments? Adjustments { get; init; }
    /// <summary>The v2 tone-mapping gate of the display render.</summary>
    public bool ToneV2 { get; init; }
    /// <summary>Use the capture's saved depth edits wherever the depth is read.</summary>
    public bool UseSavedEdits { get; init; } = true;
    /// <summary>Display output only: drawn into each display tile's float RGBA after the output stage and before the 8-bit store — where Lumen's
    /// renderer draws its overlays (the focal region, the quick-select mask). Null = none. Never changes what is cached.</summary>
    public IDisplayOverlay? Overlay { get; init; }
    /// <summary>A short-lived setting (a slider being dragged): its depth-effect tiles are kept apart from the settled settings' (at most two
    /// such settings), so a drag never evicts the tiles of the settings it started from. No effect on pixels.</summary>
    public bool Transient { get; init; }
}

/// <summary>An overlay of the display render (<see cref="RenderSettings.Overlay"/>).</summary>
public interface IDisplayOverlay
{
    /// <summary>Blend into one display tile: <paramref name="tile"/> in pixels of export level <paramref name="level"/>'s window (the export's
    /// 512 grid), <paramref name="rgba"/> its float RGBA (width × height × 4, row-major, a copy the overlay may change).</summary>
    void Apply(int level, RectI tile, float[] rgba, int width, int height);
}

/// <summary>One rendered region.</summary>
public sealed record RenderedTile(int Level, RectI Rect, RenderOutput Output, float[]? Linear, byte[]? Rgba8)
{
    public int Width => Rect.Width;
    public int Height => Rect.Height;
}

/// <summary>Static facts of a capture, from its header.</summary>
public sealed record CaptureInfo(string ReferenceModule, IReadOnlyList<string> Modules, int StackFrames, int FocalLengthMm, float AllInFocusFNumber,
                                 DateTime? CaptureTime, (int W, int H) SensorSize);

/// <summary>
/// One open capture — the engine API a front end (the desktop app, <c>lux-light</c>) works through. It owns the capture's render state
/// and hands out renders, depth, focus queries, exports and the analysis cache, all on the one verified path the exports use.
///
/// <para><b>Threading.</b> Every public member is thread-safe and may be called concurrently: renders share the capture's tile caches
/// (each tile is computed once, whoever asks first), the registration runs once however many callers wait for it, and the cache tiers are
/// raised and lowered under one lock. The async members run on the thread pool and never block the caller. Results never depend on the
/// schedule: every tile is a pure function of the capture.</para>
/// <para><b>Cancellation.</b> Every async member takes a token, checked between registration states, between tiles and between files. A
/// cancelled call throws <see cref="OperationCanceledException"/>; nothing it computed is lost (finished tiles stay cached) and nothing
/// half-made is kept — a later call resumes the work.</para>
/// <para><b>Levels.</b> Export levels 0 (full size, super-resolved) to 4; level 1 is the 13 MP image. Levels 2–4 come from the reference
/// module alone and are a few seconds from nothing; level 1 needs the fusion; level 0 and anything that reads the depth need the
/// registration chain (<see cref="EnsureAnalysisAsync"/>), which the analysis cache removes from every later open.</para>
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public string Path { get; }
    public LriFile Lri { get; }
    public SessionOptions Options { get; }
    public CaptureInfo Info { get; }

    readonly object _lock = new();
    readonly object _cacheLock = new();
    readonly ProgressReporter _progress;
    ExportState? _state0, _state1;   // built for level 0 (registration, depth, level 0) and for level 1 (the 13 MP render's fusion)
    AnalysisCache.CachedTiles? _tiles0, _tiles1;
    IAnalysisCache? _cache; bool _cacheOpened;
    bool _disposed;

    CaptureSession(string path, LriFile lri, SessionOptions options)
    {
        Path = path; Lri = lri; Options = options;
        _progress = new ProgressReporter(options.Progress);
        var h = lri.Header;
        DateTime? when = h.ImageTimeStamp is { } ts ? new DateTime((int)ts.Year, (int)ts.Month, (int)ts.Day, (int)ts.Hour, (int)ts.Minute, (int)ts.Second) : null;
        var refMod = lri.Modules[lri.ReferenceModule].Module;
        Info = new CaptureInfo(lri.ReferenceModule, lri.Modules.Keys.ToList(), lri.StackFrames, h.HasImageFocalLength ? h.ImageFocalLength : 0,
                               ExportWindow.AllInFocusFNumber(lri), when, (refMod.SensorDataSurface.Size.X, refMod.SensorDataSurface.Size.Y));
    }

    /// <summary>Open a capture: reads and parses the `.lri` (nothing is rendered or computed yet).</summary>
    public static CaptureSession Open(string lriPath, SessionOptions? options = null)
        => new(System.IO.Path.GetFullPath(lriPath), LriFile.Load(lriPath), options ?? new SessionOptions());

    public static Task<CaptureSession> OpenAsync(string lriPath, SessionOptions? options = null, CancellationToken cancel = default)
        => Task.Run(() => Open(lriPath, options), cancel);

    // ------------------------------------------------------------------------------------------------ states

    IAnalysisCache? Cache(bool create)
    {
        lock (_cacheLock)
        {
            if (_cache is null && Options.Context is { } ctx && (create || !_cacheOpened))
            {
                _cache = ctx.OpenCache(AnalysisCache.EngineCacheVersion, create);
                _cacheOpened = true;
            }
            return _cache;
        }
    }

    /// <summary>Use what another session (a library job) kept in or released from this capture's analysis cache since this session opened it:
    /// the cache is opened when it did not exist then, and the render states read their kept-tile lists again (a state built while no cache
    /// existed gets the cache as its tile source). Output is unchanged by construction: each tier holds the pipeline's own values and a tile
    /// that is not kept is generated. Without it, an upgrade to full detail made while the photo is open is never read.</summary>
    public void RefreshCache()
    {
        if (_disposed || !Options.UseCache) return;
        IAnalysisCache? c;
        lock (_cacheLock) { if (_cache is null) _cacheOpened = false; c = Cache(create: false); }
        if (c is null) return;
        lock (_lock)
        {
            if (_tiles0 is not null) _tiles0.Refresh();
            else if (_state0 is not null) { _tiles0 = AnalysisCache.TileSource(c, 0, Options.Log); _state0.Cache.Stored = _tiles0; }
            if (_tiles1 is not null) _tiles1.Refresh();
            else if (_state1 is not null) { _tiles1 = AnalysisCache.TileSource(c, 1, Options.Log); _state1.Cache.Stored = _tiles1; }
        }
    }

    /// <summary>The render state for <paramref name="buildLevel"/> 0 (level 0, depth) or 1 (level 1 and the previews), created on first use.</summary>
    ExportState State(int buildLevel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            ref ExportState? slot = ref buildLevel == 0 ? ref _state0 : ref _state1;
            if (slot is not null) return slot;
            var cache = Options.UseCache ? Cache(create: false) : null;
            var geo = buildLevel == 0 && cache is not null ? AnalysisCache.LoadGeometry(cache, Options.Log) : null;
            var tiles = cache is null ? null : AnalysisCache.TileSource(cache, buildLevel, Options.Log);
            if (buildLevel == 0) _tiles0 = tiles; else _tiles1 = tiles;
            slot = ExportBuild.Build(Lri, Path, buildLevel, Options.Log, Options.Threads, _progress,
                                     new BuildOptions { Geometry = geo, StoredTiles = tiles, KeepRegistration = false });
            if (buildLevel == 0 && Options.Context is { } ctx && _appliedEdits is { } ae && ae.Applied) ae.Note = ctx.ApplySavedEdits(slot, ae.Variant, Options.Log);
            return slot;
        }
    }

    /// <summary>The state a render of export level <paramref name="level"/> uses: level 0 and every depth reader the level-0 state; levels 1–4
    /// the level-0 state when it exists (its tiles are the same, and shared), else the lighter level-1 state.</summary>
    ExportState StateFor(int level, bool needsDepth) => needsDepth || level == 0 ? State(0) : (_state0 is not null ? State(0) : State(1));

    // ------------------------------------------------------------------------------------------------ geometry and levels

    /// <summary>Number of export levels (5 on the L16).</summary>
    public int LevelCount => Window.ExportDims.Length;
    /// <summary>The export window per level (sizes and origins inside the pipeline canvas).</summary>
    public ExportLevels Window => Exporter.Resolve(StateFor(2, false), new ExportRequest { Formats = Array.Empty<ExportImageFormat>() }).Window;
    /// <summary>The image size of an export level.</summary>
    public (int W, int H) LevelSize(int level) => Window.ExportDims[level];

    // ------------------------------------------------------------------------------------------------ analysis

    /// <summary>Whether the registration geometry and depth are available without computing anything.</summary>
    public bool IsAnalysed => State(0).HasGeometry || (Options.UseCache && Cache(false) is { } c && c.CompleteDescriptor(AnalysisCache.GeometryTier) is not null);

    /// <summary>Run the registration chain (or load its result from the analysis cache): the depth, the focus queries and level 0 then need
    /// nothing more. Keeps the result in the cache when <see cref="SessionOptions.KeepGeometry"/> is set.
    /// <para>Cancelling abandons the wait: the registration, once started, is shared by every caller and runs to its end in the
    /// background, and its result is kept for the next call.</para></summary>
    public async Task EnsureAnalysisAsync(CancellationToken cancel = default)
    {
        var work = Task.Run(() =>
        {
            var st = State(0);
            bool fromCache = st.HasGeometry;
            var g = st.Geometry ?? throw new InvalidOperationException("this capture has no registration geometry");
            if (!fromCache && Options.KeepGeometry && Options.Context is not null)
                lock (_cacheLock) if (Cache(create: true) is { } c && c.CompleteDescriptor(AnalysisCache.GeometryTier) is null) AnalysisCache.SaveGeometry(c, g);
        });
        await work.WaitAsync(cancel).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ rendering

    /// <summary>Render a region of an export level (export-level pixels, the level's own grid). Linear or display output per
    /// <paramref name="settings"/>; a default <see cref="RenderSettings"/> gives the Lumen-identical pixels of that level's export.</summary>
    /// <remarks>With <paramref name="settings"/> null the capture's saved settings apply (<see cref="SavedRenderSettings"/>: an imported
    /// session's depth effect, focus, tone and colour, tone style); explicit settings are used as they are.</remarks>
    public Task<RenderedTile> RenderAsync(int level, RectI rect, RenderSettings? settings = null, CancellationToken cancel = default)
        => Task.Run(() => Render(level, rect, settings, cancel), cancel);

    /// <summary><see cref="RenderAsync"/> on the calling thread (a front end that schedules its own tile work: one call per tile, any number at
    /// once). Same pixels.</summary>
    public RenderedTile Render(int level, RectI rect, RenderSettings? settings = null, CancellationToken cancel = default)
    {
        settings ??= SavedRenderSettings();
        if (level < 0 || level >= LevelCount) throw new ArgumentOutOfRangeException(nameof(level));
        var size = LevelSize(level);
        if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > size.W || rect.Y1 > size.H || rect.Width <= 0 || rect.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rect));
        bool dof = settings.DepthEffectFNumber is float f && f < Info.AllInFocusFNumber;
        var saved = settings.UseSavedEdits ? SavedState : null;
        ExportState st;
        if (saved?.SessionGeometry is not null)
        {   // the capture renders from an imported Lumen session's own geometry and depth (as `convert` does)
            st = SessionState(dof || level == 0 || _session0 is not null ? 0 : 1, saved);
            if (dof && st.EditedDepth is null) ApplySessionEdits(st, null);
        }
        else st = StateFor(level, dof);
        var req = new ExportRequest
        {
            Formats = Array.Empty<ExportImageFormat>(), Level = level, RenderThreads = Options.Threads, Cancel = cancel,
            DepthEffectFNumber = settings.DepthEffectFNumber, FocusDepthMm = settings.FocusDepthMm, DepthEffectNeedsDepth = dof,
            JpegSharpening = settings.Sharpening, JpegV2 = settings.ToneV2, UseSavedEdits = settings.UseSavedEdits,
        };
        long depthGen = 0;
        if (dof) { var (dc, gen) = DepthEffectCacheFor(st, req, settings.Transient); req = req with { DepthEffectCache = dc }; depthGen = gen; }
        var s = new ExportSession(st, req, Options.Log);
        using var _ = Diagnostics.PhaseLog.Time(() => $"display.render L{level}");
        return settings.Output == RenderOutput.Linear
            ? new RenderedTile(level, rect, RenderOutput.Linear, s.LinearRegion(level, rect), null)
            : new RenderedTile(level, rect, RenderOutput.Display, null, RenderDisplay(st, s, level, rect, settings, depthGen, cancel));
    }

    // ---- the depth-effect tile caches: one per (state, f-number, focus) setting, the last few kept, so that every render of a setting shares
    //      its tiles (an ExportSession alone makes one per session). `UpdateDepth` swaps a new depth snapshot into them, dropping exactly the
    //      tiles it changes; the generation counter keys the display memory of depth-effect tiles, so a display tile whose input may have
    //      changed is fetched again.
    sealed record DofEntry(ExportState State, float FNumber, float? Focus, float FocusUsed, Lux.Engine.Pipeline.Dof.DofTileCache Cache)
    {
        public bool Transient { get; set; }
    }
    readonly List<DofEntry> _dofCaches = new();
    long _depthGen = 1;
    /// <summary>How many depth-effect settings keep their tiles (each about 6 bytes per pixel of every level rendered with it).</summary>
    public int DepthEffectCacheCount { get; set; } = 3;

    (Lux.Engine.Pipeline.Dof.DofTileCache Cache, long Gen) DepthEffectCacheFor(ExportState st, ExportRequest req, bool transient = false)
    {
        float fnum = req.DepthEffectFNumber!.Value; float? focus = req.FocusDepthMm;
        lock (_dofCaches)
        {
            int i = _dofCaches.FindIndex(e => ReferenceEquals(e.State, st) && e.FNumber.Equals(fnum) && Nullable.Equals(e.Focus, focus));
            if (i >= 0)
            {
                var hit = _dofCaches[i]; _dofCaches.RemoveAt(i); _dofCaches.Add(hit);
                if (!transient && hit.Transient) { hit.Transient = false; TrimDofCaches(); }   // the drag ended on it: it is settled now
                return (hit.Cache, _depthGen);
            }
        }
        Lux.Engine.Pipeline.Dof.DofTileCache made;
        using (Diagnostics.PhaseLog.Time("dof.make-cache")) made = new ExportSession(st, req, Options.Log).DepthEffect() ?? throw new InvalidOperationException("no depth effect at this f-number");
        lock (_dofCaches)
        {
            int i = _dofCaches.FindIndex(e => ReferenceEquals(e.State, st) && e.FNumber.Equals(fnum) && Nullable.Equals(e.Focus, focus));
            if (i >= 0) return (_dofCaches[i].Cache, _depthGen);   // made concurrently by another render: use the first
            _dofCaches.Add(new DofEntry(st, fnum, focus, made.State.FocusDepth, made) { Transient = transient });
            TrimDofCaches();
            return (made, _depthGen);
        }
    }

    /// <summary>Keep the newest <see cref="DepthEffectCacheCount"/> settled settings and the newest two transient ones (under the lock).</summary>
    void TrimDofCaches()
    {
        int settled = _dofCaches.Count(e => !e.Transient), transient = _dofCaches.Count - settled;
        for (int i = 0; i < _dofCaches.Count && (settled > Math.Max(1, DepthEffectCacheCount) || transient > 2);)
        {
            var e = _dofCaches[i];
            if (e.Transient ? transient > 2 : settled > Math.Max(1, DepthEffectCacheCount)) { _dofCaches.RemoveAt(i); if (e.Transient) transient--; else settled--; }
            else i++;
        }
    }

    /// <summary>The state every depth reader of a render of this session uses now (the depth effect, the overlays, level 0, `jpg+depth`): the
    /// level-0 state, or the imported Lumen session's state when the active variant renders from its own geometry. Builds it when needed
    /// (cheap when the analysis cache holds the geometry; the registration otherwise).</summary>
    public ExportState DepthState()
    {
        var saved = SavedState;
        return saved?.SessionGeometry is not null ? SessionState(0, saved) : State(0);
    }

    /// <summary>The depth-effect tiles dropped by a depth change: (cache level, tile rect in that level's pixels), or every tile.</summary>
    public sealed record DepthUpdate(bool All, IReadOnlyList<(int Level, RectI Rect)> Tiles, int Kept = 0);

    /// <summary>
    /// Put a depth snapshot in place for every depth reader of <paramref name="st"/> (an editor's state after an edit: its depth cache and edit
    /// layer as <see cref="ExportState.EditedDepth"/>), and drop exactly what it changes: the depth-effect tiles whose inputs differ
    /// (<see cref="Lux.Engine.Pipeline.Dof.DofTileCache.SwapDepth"/>; a cache on Lumen's default focus is dropped whole when the default focus
    /// moves), and the display memory of depth-effect tiles. <paramref name="changedCacheRect"/> = the depth-cache pixels that differ from the
    /// snapshot in use before (null = all). The snapshot must not change afterwards (make a new one for the next edit). Pixels never depend on
    /// what is kept or dropped.
    /// </summary>
    public DepthUpdate UpdateDepth(ExportState st, AppliedDepthEdits? edits, RectI? changedCacheRect)
    {
        var tiles = new List<(int Level, RectI Rect)>(); bool all = false; int kept = 0;
        lock (_lock) st.EditedDepth = edits;
        lock (_dofCaches)
        {
            var mine = _dofCaches.Where(e => ReferenceEquals(e.State, st)).ToList();
            if (mine.Count == 0) { _depthGen++; return new DepthUpdate(changedCacheRect is null, tiles); }
            var cache = st.DepthCache(Window);
            float? dflt = null;
            foreach (var e in mine)
            {
                if (e.Focus is null)
                {
                    dflt ??= Lux.Engine.Pipeline.Dof.DofModel.DefaultFocusDepth(Lri, cache.Current);
                    if (!dflt.Value.Equals(e.FocusUsed)) { _dofCaches.Remove(e); all = true; continue; }
                }
                foreach (var (L, tx, ty) in e.Cache.SwapDepth(cache, edits?.Layer, changedCacheRect)) tiles.Add((L, e.Cache.Grid.TileRect(L, tx, ty)));
                kept += e.Cache.CachedTiles().Count();
                if (changedCacheRect is null) all = true;
            }
            _depthGen++;
        }
        return new DepthUpdate(all, tiles, kept);
    }

    /// <summary>Forget the depth of <paramref name="st"/> (a switch of the photo's depth version): its depth-effect caches are dropped, saved or live
    /// edits and a pinned depth are forgotten (<see cref="ExportState.ResetDepth"/>) and the saved settings are read again.</summary>
    public void ResetDepth(ExportState st)
    {
        lock (_dofCaches) { _dofCaches.RemoveAll(e => ReferenceEquals(e.State, st)); _depthGen++; }
        st.ResetDepth();
        lock (_lock) _appliedEdits = null;
    }

    // ---- the display output: the JPEG export's tile render (export grid, output stage, the export's 8-bit store), run through
    //      DisplayAdjust so that each tile keeps its linear input and output-stage intermediates (DisplayTileMemory) and a slider change
    //      re-runs only the stages after the first one it affects. Same pixels as ExportSession.DisplayRegion with the same settings.
    readonly Dictionary<(ExportState State, bool ToneV2), DisplayAdjust> _adjusters = new();
    DisplayTileMemory? _displayMemory;

    DisplayAdjust AdjusterFor(ExportState st, bool toneV2)
    {
        lock (_adjusters)
        {
            if (_adjusters.TryGetValue((st, toneV2), out var hit)) return hit;
            // the loaded output tuning of each level (the same builder the export uses, branch B included) with ParamFloat 13 = 0: the
            // adjustments' sharpening is applied on top of it
            var loaded = new ExportSession(st, new ExportRequest { Formats = Array.Empty<ExportImageFormat>(), JpegSharpening = 0f, JpegV2 = toneV2, RenderThreads = Options.Threads }, null);
            var a = new DisplayAdjust(st.Frame, st.Colour, st.Wb, st.Lri.LumenEvOffset, lv => loaded.IspOfLevel(lv).Item1.Tuning, loaded.IspOfLevel(0).Item2);
            return _adjusters[(st, toneV2)] = a;
        }
    }

    byte[] RenderDisplay(ExportState st, ExportSession s, int level, RectI src, RenderSettings settings, long depthGen, CancellationToken cancel)
    {
        var adj = (settings.Adjustments ?? (Adjustments.Default with { Sharpening = settings.Sharpening })).Validate();
        var da = AdjusterFor(st, settings.ToneV2);
        var source = s.DisplayTileSource();
        var win = s.Grid.Window;
        lock (_adjusters) _displayMemory ??= new DisplayTileMemory(Options.DisplayCacheBytes);
        var memory = _displayMemory;
        bool keep = Options.DisplayCacheBytes > 0;
        var (W, H) = win.ExportDims[level]; var pd = win.PipelineDims[level];
        int nx = ExportLevels.GridCount(W), ny = ExportLevels.GridCount(H);
        int tx0 = Math.Min(src.X0 / ExportLevels.Tile, nx - 1), tx1 = Math.Min((src.X1 - 1) / ExportLevels.Tile, nx - 1);
        int ty0 = Math.Min(src.Y0 / ExportLevels.Tile, ny - 1), ty1 = Math.Min((src.Y1 - 1) / ExportLevels.Tile, ny - 1);
        var dst = new byte[(long)src.Width * src.Height * 4];
        var tiles = new List<(int Tx, int Ty)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) tiles.Add((tx, ty));
        bool dof = settings.DepthEffectFNumber is float f && f < Info.AllInFocusFNumber;
        void One((int Tx, int Ty) t)
        {
            var tile = win.TileRect(level, t.Tx, t.Ty);
            Image<Vec4F> img;
            DisplayRender.TileGeometry geo;
            Image<Vec4F> Fetch(out DisplayRender.TileGeometry g)
            {
                g = DisplayRender.Geometry(tile, pd, win.Origins[level], win.CacheDims, level);
                var px = source(win.BaseLevel + g.CacheLevel, g.Grown, pd);
                var grown = new Image<Vec4F>(g.Grown.Width, g.Grown.Height);
                System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(grown.Data);
                return grown;
            }
            if (!keep) { var lin = Fetch(out geo); img = da.Render(lin, geo, level, adj); }
            else
            {
                var e = memory.Get(new DisplayTileMemory.Key(st, level, t.Tx, t.Ty, dof ? settings.DepthEffectFNumber : null, dof ? settings.FocusDepthMm : null,
                                                             dof && settings.UseSavedEdits, settings.ToneV2, dof ? depthGen : 0));
                lock (e.Gate)
                {
                    if (e.Linear is null) { using (Diagnostics.PhaseLog.Time(() => $"display.fetch L{level}")) e.Linear = Fetch(out e.Geometry); e.Stages.Clear(); }
                    geo = e.Geometry;
                    using (Diagnostics.PhaseLog.Time(() => $"display.stages L{level}")) img = da.Render(e.Linear, geo, level, adj, e.Stages);
                    // the export's 8-bit store reads the tile before the next render can reuse its images: copy the overlap out now
                    Store(img, tile);
                }
                memory.Account(e);
                return;
            }
            Store(img, tile);
        }
        void Store(Image<Vec4F> img, RectI tile)
        {
            if (settings.Overlay is { } ov) { StoreOverlaid(img, tile, ov); return; }
            var c = tile.Intersect(src);
            for (int y = c.Y0; y < c.Y1; y++)
            {
                var row = img.Row(y - tile.Y0);
                long o = ((long)(y - src.Y0) * src.Width + (c.X0 - src.X0)) * 4;
                for (int x = c.X0; x < c.X1; x++)
                {
                    var v = row[x - tile.X0];
                    if (JpegExportRenderer.UseDisplayRounding)
                    {
                        dst[o++] = DisplayOutput.DisplayByte(v.R * 255f); dst[o++] = DisplayOutput.DisplayByte(v.G * 255f);
                        dst[o++] = DisplayOutput.DisplayByte(v.B * 255f); dst[o++] = DisplayOutput.DisplayByte(v.A * 255f);
                    }
                    else
                    {   // ImageConvertPixelType<vec4x8ui, vec4x32f>: the JPEG export's store (round half away from zero)
                        dst[o++] = DisplayOutput.ExportByte(v.R * 255f); dst[o++] = DisplayOutput.ExportByte(v.G * 255f);
                        dst[o++] = DisplayOutput.ExportByte(v.B * 255f); dst[o++] = DisplayOutput.ExportByte(v.A * 255f);
                    }
                }
            }
        }
        // with an overlay: the whole display tile as float (a copy — the image may be the stage cache's), the overlay, then the same store
        void StoreOverlaid(Image<Vec4F> img, RectI tile, IDisplayOverlay ov)
        {
            int w = tile.Width, h = tile.Height;
            var f = new float[(long)w * h * 4];
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, float>(img.Row(y)).CopyTo(f.AsSpan(y * w * 4, w * 4));
            ov.Apply(level, tile, f, w, h);
            var c = tile.Intersect(src);
            for (int y = c.Y0; y < c.Y1; y++)
            {
                long o = ((long)(y - src.Y0) * src.Width + (c.X0 - src.X0)) * 4; int i = ((y - tile.Y0) * w + (c.X0 - tile.X0)) * 4;
                for (int x = c.X0; x < c.X1; x++, i += 4)
                {
                    if (JpegExportRenderer.UseDisplayRounding)
                    {
                        dst[o++] = DisplayOutput.DisplayByte(f[i] * 255f); dst[o++] = DisplayOutput.DisplayByte(f[i + 1] * 255f);
                        dst[o++] = DisplayOutput.DisplayByte(f[i + 2] * 255f); dst[o++] = DisplayOutput.DisplayByte(f[i + 3] * 255f);
                    }
                    else
                    {
                        dst[o++] = DisplayOutput.ExportByte(f[i] * 255f); dst[o++] = DisplayOutput.ExportByte(f[i + 1] * 255f);
                        dst[o++] = DisplayOutput.ExportByte(f[i + 2] * 255f); dst[o++] = DisplayOutput.ExportByte(f[i + 3] * 255f);
                    }
                }
            }
        }
        if (Options.Threads <= 1 || tiles.Count <= 1) { foreach (var t in tiles) { cancel.ThrowIfCancellationRequested(); One(t); } }
        else Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = Options.Threads, CancellationToken = cancel }, One);
        return dst;
    }

    // ------------------------------------------------------------------------------------------------ depth and focus

    /// <summary>The depth cache (metric mm on the renderer's depth grid): the edited one when saved edits are attached. Runs the analysis
    /// when needed.</summary>
    public async Task<DepthImageCache> GetDepthAsync(CancellationToken cancel = default)
    {
        await EnsureAnalysisAsync(cancel).ConfigureAwait(false);
        return State(0).DepthCache(Window);
    }

    /// <summary>The scene depth (mm) at a point of the image, normalised to the export window (0,0 = top left) — Lumen's click to focus
    /// (`getDepthAtPoint`: the 3×3 mean of the depth cache at the mapped canvas point).</summary>
    public async Task<float> DepthAtAsync(float x, float y, CancellationToken cancel = default)
    {
        var depth = await GetDepthAsync(cancel).ConfigureAwait(false);
        var win = Window; var st = State(0);
        var q = Lux.Engine.Pipeline.Dof.DofModel.MapExportPoint(x, y, win.ExportDims[0], win.Origins[0], st.Cache.LevelDims[0]);
        return Lux.Engine.Pipeline.Dof.DofModel.DepthAtPoint(depth.Current, q.X, q.Y);
    }

    /// <summary>Lumen's default focus distance for the depth effect (mm).</summary>
    public async Task<float> DefaultFocusDepthAsync(CancellationToken cancel = default)
        => Lux.Engine.Pipeline.Dof.DofModel.DefaultFocusDepth(Lri, (await GetDepthAsync(cancel).ConfigureAwait(false)).Current);

    // ------------------------------------------------------------------------------------------------ saved edits

    sealed class EditsAttachment { public bool Applied; public string? Variant; public string? Note; }
    EditsAttachment? _appliedEdits;

    /// <summary>Attach the capture's saved depth edits (a variant, or the active one): every depth reader of this session then uses them.
    /// Returns the note describing what was attached, or null when the capture has none. The registration chain is not run for this: the
    /// edits carry the depth they were made on.</summary>
    public string? AttachSavedEdits(string? variant = null)
    {
        lock (_lock)
        {
            if (Options.Context is not { } ctx) return null;
            _appliedEdits = new EditsAttachment { Applied = true, Variant = variant };
            var st = State(0);
            if (st.EditedDepth is null) _appliedEdits.Note = ctx.ApplySavedEdits(st, variant, Options.Log);
            return _appliedEdits.Note;
        }
    }

    // ------------------------------------------------------------------------------------------------ saved settings

    SavedRenderState? _saved; bool _savedLoaded;
    ExportState? _session0, _session1;   // states of a Lumen-session render (the session's geometry and depth), built on first use

    /// <summary>The render settings saved with the capture's active edits variant (an imported Lumen session's f-number, focus, tone and
    /// colour, crop / rotation, rating, tone style, and its own camera geometry); null when there are none. Read once per session.</summary>
    public SavedRenderState? SavedState
    {
        get
        {
            lock (_lock)
            {
                if (_savedLoaded) return _saved;
                try { _saved = Options.Context?.LoadSavedState(null); } catch (ExportRequestException) { _saved = null; }
                _savedLoaded = true;
                return _saved;
            }
        }
    }

    /// <summary>The render settings a viewer of the capture starts from: Lumen's defaults with the saved depth effect, focus, tone and colour
    /// settings and tone style on top (what <see cref="RenderAsync"/> uses when given no settings). The saved crop / rotation is
    /// <see cref="SavedTransform"/>; a render covers the uncropped level.</summary>
    public RenderSettings SavedRenderSettings()
    {
        var s = SavedState;
        if (s is null) return new RenderSettings();
        var r = new RenderSettings { ToneV2 = s.ToneV2 };
        if (s.HasRenderSettings)
            r = r with { DepthEffectFNumber = s.FNumber, FocusDepthMm = s.FocusDepth is float fd && fd > 0f ? fd : null, Adjustments = s.Adjustments };
        return r;
    }

    /// <summary>Read the saved settings again at their next use (a front end that saved new settings for this capture — the editor — so that
    /// <see cref="SavedState"/>, a render without explicit settings and <see cref="ExportAsync"/> follow them). The variant and its session
    /// geometry are not re-read here: changing the active variant needs a new session.</summary>
    public void ReloadSavedState()
    {
        lock (_lock)
        {
            if (!_savedLoaded) return;
            SavedRenderState? fresh;
            try { fresh = Options.Context?.LoadSavedState(null); } catch (ExportRequestException) { fresh = null; }
            // the session render states belong to the variant's geometry: keep them only while the variant stays the same
            if ((fresh?.SessionGeometry is null) != (_saved?.SessionGeometry is null) || fresh?.Variant != _saved?.Variant) { _session0 = null; _session1 = null; }
            _saved = fresh;
        }
    }

    /// <summary>The saved photo transform (crop / rotation / flips), or null.</summary>
    public Pipeline.Export.ExportTransform? SavedTransform => SavedState?.Transform;

    /// <summary>The state of a Lumen-session render for <paramref name="buildLevel"/> 0 or 1: built from the session's geometry (level 0) with the
    /// session's fusion sources (<see cref="BuildOptions.SessionRender"/>), never from or into the analysis cache — as `convert` builds it.</summary>
    ExportState SessionState(int buildLevel, SavedRenderState saved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            ref ExportState? slot = ref buildLevel == 0 ? ref _session0 : ref _session1;
            if (slot is not null) return slot;
            var geo = buildLevel == 0 ? saved.SessionGeometry!(Lri) : null;
            slot = ExportBuild.Build(Lri, Path, buildLevel, Options.Log, Options.Threads, _progress,
                                     new BuildOptions { Geometry = geo, StoredTiles = null, KeepRegistration = false, SessionRender = true });
            return slot;
        }
    }

    string? ApplySessionEdits(ExportState st, string? variant)
    {
        lock (_lock) return st.EditedDepth is null && Options.Context is { } ctx ? ctx.ApplySavedEdits(st, variant, Options.Log) : null;
    }

    // ------------------------------------------------------------------------------------------------ export

    /// <summary>Export through the one verified path (<see cref="Exporter"/>), on this session's state — so the analysis, the tiles already
    /// rendered and the cache serve the export. <paramref name="req"/> names the formats and files; its <c>RenderThreads</c>, progress and
    /// cancellation default to the session's.
    /// <para>The capture's saved settings apply exactly as in `convert` (<see cref="Exporter.WithSavedSettings"/>: what the request sets
    /// explicitly — <see cref="ExportRequest.OverriddenSettings"/> — wins), and a variant imported from a Lumen session renders from the
    /// session's own geometry and depth; <see cref="ExportRequest.UseSavedEdits"/> false ignores all of it.</para></summary>
    public Task<Exporter.Result> ExportAsync(ExportRequest req, CancellationToken cancel = default) => Task.Run(() =>
    {
        var (st, r, notes) = PrepareExport(req, cancel);
        if (st is null) return Exporter.Run(Path, r with { Context = Options.Context, CacheUse = CacheUse.None, UseSavedEdits = false }, Options.Log);
        var res = Exporter.Run(st, r, Options.Log);
        return notes.Count == 0 ? res : res with { Notes = notes.Concat(res.Notes).ToList() };
    }, cancel);

    /// <summary>The pixels the JPG export of <paramref name="req"/> encodes (its RGBA8 render after the export resampler, before the JPEG coding) —
    /// prepared exactly as <see cref="ExportAsync"/> (saved settings, session geometry, saved edits), on this session's state. For checks of a
    /// front end that shows parts of an export (the viewer). Writes nothing.</summary>
    public Task<(int Width, int Height, byte[] Rgba)> RenderExportRgba8Async(ExportRequest req, CancellationToken cancel = default) => Task.Run(() =>
    {
        var (st, r, _) = PrepareExport(req with { Formats = new[] { ExportImageFormat.Jpeg } }, cancel);
        if (st is null) throw new InvalidOperationException("this request renders no image");
        var s = new ExportSession(st, r, Options.Log);
        var (_, px) = s.Rgba8();
        return (s.Grid.Size.W, s.Grid.Size.H, px);
    }, cancel);

    /// <summary>An <see cref="ExportSession"/> on this session's state for <paramref name="req"/>, prepared exactly as <see cref="ExportAsync"/>
    /// prepares an export (saved settings, a Lumen session's geometry, saved edits) — for front ends that make the Lux-only outputs (the
    /// animations: depth-effect frames through <see cref="ExportSession.Rgba8"/>, the parallax source) from the export's own renders. Writes
    /// nothing. (Used by the animated exports.)</summary>
    public ExportSession PrepareExportSession(ExportRequest req, CancellationToken cancel = default)
    {
        var (st, r, _) = PrepareExport(req, cancel);
        if (st is null) throw new InvalidOperationException("this request renders no image");
        return new ExportSession(st, r, Options.Log);
    }

    /// <summary>The state and the completed request an export runs on (null state: the request needs none).</summary>
    (ExportState? State, ExportRequest Request, List<string> Notes) PrepareExport(ExportRequest req, CancellationToken cancel)
    {
        var notes = new List<string>();
        SavedRenderState? saved = null;
        if (req.UseSavedEdits && Options.Context is { } ctx0)
            saved = req.EditsVariant is null ? SavedState : ctx0.LoadSavedState(req.EditsVariant);
        if (saved is not null)
        {
            req = Exporter.WithSavedSettings(req, saved);
            notes.Add(Exporter.SavedStateNote(saved, req));
        }
        if (req.DepthEffectFNumber is float dfn && !req.DepthEffectNeedsDepth && !(Info.AllInFocusFNumber <= dfn)) req = req with { DepthEffectNeedsDepth = true };
        req = req with { Cancel = cancel, RenderThreads = req.RenderThreads <= 1 ? Options.Threads : req.RenderThreads };
        if (!req.NeedsState) return (null, req, notes);
        int buildLevel = Exporter.BuildLevelFor(req);
        ExportState st;
        if (saved?.SessionGeometry is not null)
        {
            st = SessionState(buildLevel == 0 || _session0 is not null ? 0 : 1, saved);
            if (req.NeedsDepth && ApplySessionEdits(st, req.EditsVariant) is { } n) notes.Add(n);
        }
        else
        {
            st = State(buildLevel == 0 || _state0 is not null ? 0 : 1);
            if (req.NeedsDepth && req.UseSavedEdits && st.EditedDepth is null && Options.Context is { HasSavedEdits: true } && AttachSavedEdits(req.EditsVariant) is { } n) notes.Add(n);
        }
        return (st, req, notes);
    }

    // ------------------------------------------------------------------------------------------------ the analysis cache

    /// <summary>The level the cache holds now (the highest complete tier with every tier below it).</summary>
    public CacheLevel CacheLevel { get { lock (_cacheLock) return AnalysisCache.LevelOf(Cache(false)); } }

    /// <summary>Per tier: blocks, raw and stored bytes, completeness.</summary>
    public IReadOnlyList<CacheTierStats> CacheSizes() { lock (_cacheLock) return Cache(false)?.Stats() ?? Array.Empty<CacheTierStats>(); }

    /// <summary>Where the cache lives, or null when there is none.</summary>
    public string? CacheLocation { get { lock (_cacheLock) return Cache(false)?.Location; } }

    /// <summary>Raise the cache to <paramref name="level"/> (and every level below it), computing what is missing. Never lowers it. Output is
    /// unchanged by construction: each tier stores the pipeline's own values.</summary>
    public Task EnsureCacheAsync(CacheLevel level, CancellationToken cancel = default) => Task.Run(() =>
    {
        if (level == CacheLevel.None) return;
        if (Options.Context is null) throw new InvalidOperationException("this session has no capture context to keep a cache in");
        IAnalysisCache c;
        lock (_cacheLock) c = Cache(create: true) ?? throw new IOException("the analysis cache cannot be created next to this capture");
        // geometry + depth
        if (c.CompleteDescriptor(AnalysisCache.GeometryTier) is null)
        {
            var st0 = State(0);
            cancel.ThrowIfCancellationRequested();
            var g = st0.Geometry ?? throw new InvalidOperationException("this capture has no registration geometry");
            lock (_cacheLock) AnalysisCache.SaveGeometry(c, g);
        }
        if (level >= CacheLevel.Linear13 && c.CompleteDescriptor(AnalysisCache.LinearTier) is null)
        {
            var st1 = _state0 is not null ? State(0) : State(1);   // either state's levels 1–4 are the same tiles
            int n = 0;
            for (int L = 1; L <= 4; L++)
            {
                var (W, H) = st1.Cache.LevelDims[L];
                st1.Cache.EnsureReady(L, new RectI(0, 0, W, H));   // the fusion set-up on this thread, not inside a worker
                st1.Cache.Prefetch(L, new RectI(0, 0, W, H), Options.Threads, cancel);
                lock (_cacheLock) n += AnalysisCache.SaveTiles(c, st1, L, AnalysisCache.AllTiles(st1.Cache, L));
            }
            int total = Enumerable.Range(1, 4).Sum(L => st1.Cache.Grid[L].X * st1.Cache.Grid[L].Y);
            lock (_cacheLock) if (c.Keys(AnalysisCache.LinearTier).Count >= total) c.SetComplete(AnalysisCache.LinearTier, AnalysisCache.Describe("levels 1-4", total));
        }
        if (level >= CacheLevel.Full && c.CompleteDescriptor(AnalysisCache.FullTier) is null)
        {
            var st0 = State(0);
            var tiles = AnalysisCache.FullDetailTiles(st0);
            int threads = Options.Threads;
            using var watch = Options.MemoryBudgetBytes is long b ? MemoryBudget.Watch(b) : null;
            if (Options.MemoryBudgetBytes is long b2) threads = MemoryBudget.Level0Threads(b2, threads);
            // generate in bands of tile rows and store each band before the next: the full-detail tiles never all sit in memory at once
            foreach (var band in tiles.GroupBy(t => t.Ty).OrderBy(gr => gr.Key))
            {
                cancel.ThrowIfCancellationRequested();
                int ty = band.Key, tx0 = band.Min(t => t.Tx), tx1 = band.Max(t => t.Tx);
                var r = new RectI(tx0 * Pipeline.Cache.PipelineCache.TileSize, ty * Pipeline.Cache.PipelineCache.TileSize, tx1 * Pipeline.Cache.PipelineCache.TileSize + 1, ty * Pipeline.Cache.PipelineCache.TileSize + 1);
                st0.Cache.Prefetch(0, r, threads, cancel);
                lock (_cacheLock) AnalysisCache.SaveTiles(c, st0, 0, band);
                if (_tiles0 is { } t0)
                {   // stored: drop them from memory, the cache now serves them
                    foreach (var (bx, by) in band) t0.Added(0, bx, by);
                    var inBand = band.ToHashSet();
                    st0.Cache.Evict(0, (ex, ey) => inBand.Contains((ex, ey)));
                }
            }
            lock (_cacheLock) if (c.Keys(AnalysisCache.FullTier).Count >= tiles.Count) c.SetComplete(AnalysisCache.FullTier, AnalysisCache.Describe("level 0 export window", tiles.Count));
            st0.ReleaseLevel0Inputs();   // the full-detail tiles are kept: what made them is not needed by anything that reads them
        }
    }, cancel);

    /// <summary>Lower the cache below <paramref name="level"/>: that tier and every tier above it are deleted (the space is returned).</summary>
    public void ReleaseCache(CacheLevel level)
    {
        if (level == CacheLevel.None) level = CacheLevel.Geometry;
        lock (_cacheLock)
        {
            if (Cache(false) is not { } c) return;
            for (var l = CacheLevel.Full; l >= level; l--) c.Drop(AnalysisCache.TierName(l));
        }
    }

    public void Dispose()
    {
        lock (_cacheLock) { _cache?.Dispose(); _cache = null; }
        _disposed = true;
    }
}
