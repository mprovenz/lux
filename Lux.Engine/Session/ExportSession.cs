using System.Buffers.Binary;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Session;

/// <summary>
/// One capture's renders, shared by every output of an <see cref="ExportRequest"/>: the grid, the two distinct
/// renders (`exportImage::lambda_2` gates the display/output ISP on `(fmt | 4) == 4`, so fmt 0/4
/// take an 8-bit output-ISP re-run and fmt 1/2/3 a `vec4x32f` float render), the metric depth warped onto the same
/// grid, and the parallax source built from those two. Every member is memoized, so the JPEG, the depth map and the
/// parallax formats cost one render between them — the point of putting them on one command.
/// </summary>
public sealed class ExportSession
{
    public ExportState State { get; }
    public ExportRequest Request { get; }
    public Exporter.ExportGrid Grid { get; }
    readonly bool _v; readonly Action<string>? _log, _eng;

    public ExportSession(ExportState st, ExportRequest req, Action<string>? log)
    {
        State = st; Request = req; _v = req.Verbose; _log = log;
        // the build's reporter when it has one (the caches still hold it), else a fresh one on the request's sink
        Progress = st.Progress.IsEnabled ? st.Progress : new ProgressReporter(req.Progress);
        // The engine trace (the renderers and the DNG writer). A verbose caller indents it by two spaces under its
        // own headline lines; `convert` hands its log through untouched.
        _eng = log is null ? null : (_v ? s => log("  " + s) : log);
        Grid = Exporter.Resolve(st, req);
    }

    public Action<string>? EngineLog => _eng;
    /// <summary>The export's progress channel (see <see cref="ExportRequest.Progress"/>).</summary>
    public ProgressReporter Progress { get; }
    ExportLevels Win => Grid.Window;
    (int W, int H) Size => Grid.Size;
    LriFile Lri => State.Lri;
    CapturedFrame Frame => State.Frame;

    // ---- the float render, shared by DNG, PPM and HDR (fmt 1 and 3 are the same image, differing only in the writer).
    float[]? _floatImg;
    (int Cols, int Rows, float[] Data)? _vign; readonly object _vignLock = new();
    public ExportRenderer NewFloatRenderer()
    {
        lock (_vignLock) _vign ??= Lux.Engine.Pipeline.Isp.Stages.LensShadingKernel.ModelGrid(Frame.Header, Frame.Module);
        // `exportImage` lambda_2 reads `lens_shading.multiplier` from the FIRST element of the renderer's per-level
        // tunings — the level-0 tuning, whose multiplier `setInputDataStream` sets to the reference capture's
        // histogram value for every level. That is the per-capture histogram value, not the module-ISP tuning's 1.0.
        float Mult(int _) => State.Capture.LensShadingMultiplier;
        var dof = DepthEffect();
        Ready(display: false);
        return new(State.Cache, Win, Grid.Transform, Size, forceLevel0: false, Mult, _vign.Value)
        { Log = _eng, Threads = Request.RenderThreads, Progress = Progress, TileSource = dof is null ? null : dof.Render, TileSourcePrefetch = dof is null ? null : dof.Prefetch, Cancel = Request.Cancel };
    }

    /// <summary>The linear render of a region of export level <paramref name="level"/> (export-level pixels): float RGBA, the fused image
    /// after the vignetting correction — the values the DNG / HDR / PPM writers start from (before their ×16384 / encoding), on the level's own
    /// grid (no resampling). Thread-safe; tiles are shared with every other render of the state.</summary>
    public float[] LinearRegion(int level, RectI rect) => NewFloatRenderer().RenderSource(level, rect);

    /// <summary>The display render of a region of export level <paramref name="level"/>: RGBA8 through the whole output ISP, exactly the
    /// pixels the JPEG export of that level encodes for that region. Thread-safe.</summary>
    public byte[] DisplayRegion(int level, RectI rect)
    {
        var dof = DepthEffect();
        Ready(display: true);
        var r = new JpegExportRenderer(State.Cache, Win, Grid.Transform, Size, forceLevel0: false, IspOfLevel, Frame)
        { Log = _eng, Threads = Request.RenderThreads, Progress = ProgressReporter.None, TileSource = dof is null ? null : dof.Render, Cancel = Request.Cancel };
        return r.RenderSource(level, rect);
    }
    /// <summary>The fused-tile source the display render reads (`lambda_2`'s fetch: the DOFCache with the depth effect, else the
    /// PipelineCache), with the generators of the requested level warmed — for renderers that run the output stage themselves
    /// (<see cref="CaptureSession"/>'s adjusted display path).</summary>
    public Func<int, RectI, (int W, int H), float[]> DisplayTileSource()
    {
        var dof = DepthEffect();
        Ready(display: true);
        return dof is not null ? dof.Render : State.Cache.Render;
    }

    // ---- the depth effect: the DOFCache the tile callbacks read instead of the PipelineCache when the f-number is below the
    //      capture's all-in-focus value. One cache per session, shared by every format.
    Lux.Engine.Pipeline.Dof.DofTileCache? _dof; bool _dofResolved;
    /// <summary>The focus distance the depth effect uses, mm (set once <see cref="DepthEffect"/> has resolved a cache).</summary>
    public float? DepthEffectFocusMm { get; private set; }
    public Lux.Engine.Pipeline.Dof.DofTileCache? DepthEffect()
    {
        if (_dofResolved) return _dof;
        lock (_dofLock) { if (!_dofResolved) { _dof = ResolveDepthEffect(); _dofResolved = true; } }
        return _dof;
    }
    readonly object _dofLock = new();

    Lux.Engine.Pipeline.Dof.DofTileCache? ResolveDepthEffect()
    {
        if (Request.DepthEffectFNumber is not float fnum) return null;
        if (!(fnum > 0f)) throw new ExportRequestException("the depth effect's f-number must be greater than 0");
        float allInFocus = ExportWindow.AllInFocusFNumber(Lri);
        if (allInFocus <= fnum)
        {
            _log?.Invoke(FormattableString.Invariant($"depth effect: f/{fnum:0.##} is not below this capture's all-in-focus f/{allInFocus:0.##} — the image is unchanged"));
            return null;
        }
        if (Request.DepthEffectCache is { } given) { DepthEffectFocusMm = given.State.FocusDepth; return given; }
        if (State.FullDepth is null) throw new ExportRequestException("depth effect: the registration chain produced no full-frame depth for this capture");
        var depth = State.DepthCache(Win);   // the edited cache when saved depth edits are in use
        float? focus = Request.FocusDepthMm;
        if (focus is float fm && !(fm > 0f)) throw new ExportRequestException("the depth effect's focus distance must be greater than 0");
        if (focus is null && Request.FocusPoint is { } fpt)
        {
            float px = fpt.X, py = fpt.Y;
            // getDepthAtPoint: export-window point → pipeline-canvas point → 3×3 mean of the depth cache
            var q = Lux.Engine.Pipeline.Dof.DofModel.MapExportPoint(px, py, Win.ExportDims[0], Win.Origins[0], State.Cache.LevelDims[0]);
            try { focus = Lux.Engine.Pipeline.Dof.DofModel.DepthAtPoint(depth.Current, q.X, q.Y); }
            catch (InvalidOperationException) { throw new ExportRequestException(FormattableString.Invariant($"depth effect: there is no depth at the focus point {px},{py}")); }
            var k = Lux.Engine.Pipeline.Dof.DofConstants.FromCapture(Lri);
            try
            {
                // Lumen's focus tool only accepts a click whose depth lies inside the range it reports (ParamFloatArray 0)
                var range = Lux.Engine.Pipeline.Dof.DofModel.DefaultInFocusRange(k, Lux.Engine.Pipeline.Dof.DofModel.FocusFromAf(Lri, 0), Lux.Engine.Pipeline.Dof.DofModel.FocusFromAf(Lri, 1),
                    () => { var p = Lux.Engine.Pipeline.Dof.DofModel.AfPoint(Lri); return Lux.Engine.Pipeline.Dof.DofModel.DepthAtPoint(depth.Current, p.X, p.Y); }, Lux.Engine.Pipeline.Dof.DofModel.DefaultMaxInFocusBlurPixels);
                if (focus < range.Near || focus > range.Far)
                    _log?.Invoke(FormattableString.Invariant($"depth effect: the focus point is at {focus:0} mm, outside the {range.Near:0}–{range.Far:0} mm Lumen's focus tool lets you pick; rendering it anyway"));
            }
            catch (InvalidOperationException) { }
        }
        var dof = Lux.Engine.Pipeline.Dof.DofTileCache.ForCapture(Lri, State.Cache, depth, fnum, focus);
        dof.EditLayer = State.EditedDepth?.Layer;   // layered edits: the builder's occluded depth + matte
        dof.Progress = Progress;
        DepthEffectFocusMm = dof.State.FocusDepth;
        // Lumen's slider stops at f/2.0: Lumen replaces ParamFloat 14 (4.0) by 2.0
        if (fnum < 2.0f)
            _log?.Invoke(FormattableString.Invariant($"depth effect: f/{fnum:0.##} is wider than the f/2.0 Lumen's slider stops at; rendering it anyway"));
        _log?.Invoke(FormattableString.Invariant($"depth effect: f/{fnum:0.##} (all in focus at f/{allInFocus:0.##}), focus {dof.State.FocusDepth:0} mm"));
        return dof;
    }

    /// <summary>Before the threaded tile render: make, on this thread, whatever the generator of each pipeline level this export reads needs
    /// (<see cref="Lux.Engine.Pipeline.Cache.PipelineCache.EnsureReady"/>) — unless every tile it reads is already in memory or kept, in which
    /// case nothing is made. The tiles are exactly those the renderers ask for: the float path the export tiles of the export level, the
    /// display path each tile's grown display rect (`DisplayRender.Geometry`). Changes the schedule only, never a pixel.</summary>
    void Ready(bool display)
    {
        if (display ? _readyDisplay : _readyFloat) return;
        var to = ExportTransformOutput.Compute(Grid.Transform, Size, new RectI(0, 0, Size.W, Size.H), Win.ExportDims, false);
        int lv = to.Level; var (W, H) = Win.ExportDims[lv]; var pd = Win.PipelineDims[lv]; var (ox, oy) = Win.Origins[lv];
        int nx = ExportLevels.GridCount(W), ny = ExportLevels.GridCount(H);
        int tx0 = Math.Min(to.Source.X0 / ExportLevels.Tile, nx - 1), tx1 = Math.Min((to.Source.X1 - 1) / ExportLevels.Tile, nx - 1);
        int ty0 = Math.Min(to.Source.Y0 / ExportLevels.Tile, ny - 1), ty1 = Math.Min((to.Source.Y1 - 1) / ExportLevels.Tile, ny - 1);
        var union = new Dictionary<int, RectI>();
        void Add(int L, RectI r) => union[L] = union.TryGetValue(L, out var u) ? new RectI(Math.Min(u.X0, r.X0), Math.Min(u.Y0, r.Y0), Math.Max(u.X1, r.X1), Math.Max(u.Y1, r.Y1)) : r;
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = Win.TileRect(lv, tx, ty);
                if (display)
                {
                    var g = DisplayRender.Geometry(tile, pd, Win.Origins[lv], Win.CacheDims, lv);
                    int L = Win.BaseLevel + g.CacheLevel; Add(L, State.Cache.SourceRect(L, g.Grown, pd));
                }
                else
                {
                    int L = Win.BaseLevel + lv; Add(L, State.Cache.SourceRect(L, new RectI(tile.X0 + ox, tile.Y0 + oy, tile.X1 + ox, tile.Y1 + oy), pd));
                }
            }
        foreach (var (L, r) in union) State.Cache.EnsureReady(L, r);
        if (display) _readyDisplay = true; else _readyFloat = true;
    }
    volatile bool _readyFloat, _readyDisplay;

    public float[] FloatImage()
    {
        if (_floatImg is not null) return _floatImg;
        return _floatImg = ExportFloatImage.Render(NewFloatRenderer(), Grid.Transform, Win.ExportDims, forceLevel0: false, _eng);
    }

    // ---- the 8-bit render, shared by JPEG, JPEG+GDepth and the parallax source (fmt 0 and 4 take the same output-ISP re-run).
    JpegExportRenderer? _jpegRenderer; byte[]? _rgba8;
    DisplayIspTuning.BranchBLpyr? _branchB; bool _branchBReady;
    readonly Dictionary<int, (SoftIsp, IspStats)> _ispCache = new();
    public (SoftIsp, IspStats) IspOfLevel(int level)
    {
        lock (_ispCache) return IspOfLevelLocked(level);
    }
    (SoftIsp, IspStats) IspOfLevelLocked(int level)
    {
        if (_ispCache.TryGetValue(level, out var hit)) return hit;
        var lri = Lri; var frame = Frame; var req = Request;
        if (req.JpegV2 && !_branchBReady)
        {
            var red = frame.Module.SensorBayerRedOverride;
            float hb = float.IsNaN(frame.Info.FrameBlack) ? (frame.Info.Noise?.Black ?? 42f) : frame.Info.FrameBlack;
            float hw = frame.Info.Noise?.White ?? 1023f;
            var hist = DisplayValueHistogram.Build(frame.Raw, frame.Width, frame.Height, frame.Stride,
                                                   new RectI(0, 0, frame.Width, frame.Height), hb, hw, lri.LumenNeutral, red?.X ?? -1, red?.Y ?? -1);
            _branchB = DisplayValueHistogram.BranchB(hist, hw, lri.LumenEvOffset);
            _branchBReady = true;
        }
        var tuning = DisplayIspTuning.Build(level, lri.LumenEvOffset, lri.LumenNeutral, State.Capture.LensShadingMultiplier,
                                            Win.ExportDims[level].W, Win.ExportDims[0].W, req.JpegV2, branchB: _branchB, sharpening: req.JpegSharpening);
        var isp = new SoftIsp(tuning, State.Colour);
        var stats = isp.ComputeStats(frame);
        if (req.Adjustments is { } adj)
        {   // the setting mappers on the loaded tuning; a white-balance change replaces only the Stats neutral / xy (Pipeline/Adjustments.cs)
            stats = adj.ApplyTo(stats, State.Wb, State.Colour);
            tuning = adj.ApplyTo(tuning, lri.LumenEvOffset, adj.WhiteBalanceNeutralOrNull(State.Wb, State.Colour));
            isp = new SoftIsp(tuning, State.Colour).UseStats(stats);
            if (_v) _log?.Invoke($"  adjustments L{level}: {adj}");
        }
        if (_v) _log?.Invoke($"  display tuning L{level}: tone_mapping.type {tuning.Type("tone_mapping")} sharpening {tuning.Num("tone_mapping.sharpening"):R} (ParamFloat 13 = {req.JpegSharpening:R}) sharpening_scale {tuning.Num("tone_mapping.sharpening_scale"):R} tone_adjust.filter_size {tuning.Num("tone_adjust.filter_size"):R} ev_offset {lri.LumenEvOffset:R} multiplier {State.Capture.LensShadingMultiplier:R}");
        return _ispCache[level] = (isp, stats);
    }
    public (JpegExportRenderer Renderer, byte[] Rgba) Rgba8()
    {
        if (_jpegRenderer is not null && _rgba8 is not null) return (_jpegRenderer, _rgba8);
        var dof = DepthEffect();
        Ready(display: true);
        _jpegRenderer = new JpegExportRenderer(State.Cache, Win, Grid.Transform, Size, forceLevel0: false, IspOfLevel, Frame)
        { Log = _eng, Threads = Request.RenderThreads, Progress = Progress, TileSource = dof is null ? null : dof.Render, Cancel = Request.Cancel };
        var swr = System.Diagnostics.Stopwatch.StartNew();
        _rgba8 = _jpegRenderer.Render();
        if (_v) _log?.Invoke($"  render {Size.W}x{Size.H} RGBA8 in {swr.Elapsed.TotalSeconds:F1}s");
        return (_jpegRenderer, _rgba8);
    }

    // ---- the metric depth on the export grid, shared by the depth pair and the parallax source.
    float[]? _depth;
    /// <summary>`ExportState.FullDepth` (the same image `setInputDataStream` puts in the renderer's depth cache)
    /// resampled onto this grid through exactly the `GetExportTransformOutput` transform the writers resolve.</summary>
    public float[] DepthOnGrid()
    {
        if (_depth is not null) return _depth;
        if (State.FullDepth is null) throw new ExportRequestException("the registration chain produced no full-frame depth for this capture");
        return _depth = DepthMapExport.Warp(State.DepthCache(Win), Win, Grid.Transform, Size);
    }

    // ---- the registration state the parallax geometry reads (the ctor pairs). The level-0 build's own object when
    //      it ran one; otherwise Setup() alone, which is sub-second and produces the same reference-group pairs.
    StereoAsyncApi? _api;
    public StereoAsyncApi Registration() => _api ??= State.Registration ?? ModuleGrid.SetupApi(Lri);

    // ---- the parallax source: the JPEG render + the depth, on this grid, with the rig geometry.
    ParallaxSource? _src;
    public ParallaxSource ParallaxSource()
    {
        if (_src is not null) return _src;
        var (_, rgba) = Rgba8();
        var depth = DepthOnGrid();
        var g = ParallaxGeometry.FromCapture(Lri, Registration(), (Frame.Width, Frame.Height), State.Dims[0], Win, Size);
        _log?.Invoke("geom: " + g.Describe());
        return _src = new ParallaxSource
        {
            Colour = new Rgba(Size.W, Size.H, rgba), Depth = new Plane(Size.W, Size.H, depth), Geometry = g,
            Stem = Request.Stem ?? Path.GetFileNameWithoutExtension(Request.OutFile ?? "capture"),
        };
    }
}
