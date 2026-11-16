using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Session;

/// <summary>The renderer state an export needs (the `pcache-test` level-0 construction made reusable): the PipelineCache with its level generators
/// (levels 2–4 reference cache, level 1 fusion, level 0 ResAmp from the registration state), the reference frame, the
/// color profile / WB and the per-level module-ISP tunings.
///
/// <para>Everything expensive behind the PipelineCache is made on first use (<see cref="ExportBuild"/>): the registration chain when the
/// depth or the level-0 generator is first needed, the fusion when the first level-1 tile is, the telephoto caches with the level-0
/// generator. A render that reads only cached tiles, or only levels 2–4, therefore never runs any of them. Each piece is a pure function
/// of the capture, so when it runs does not change a pixel.</para></summary>
public sealed class ExportState
{
    public required LriFile Lri; public required PipelineCache Cache; public required CapturedFrame Frame;
    /// <summary>Load-time state of the reference capture — notably `lens_shading.multiplier`, which the export's
    /// per-tile `RemoveVignettingGeneric&lt;vec4x32f,1&gt;` uses.</summary>
    public required CaptureState Capture;
    public required LumenProfile Colour; public required WhiteBalance.CaptureWb Wb;
    public required Func<int, Tuning> TuningOfLevel;
    public required (int W, int H)[] Dims;

    /// <summary>The finest pipeline level this state was built for (the <c>maxLevel</c> of <see cref="ExportBuild.Build(string,int,Action{string}?,int,ProgressReporter?)"/>):
    /// which generators exist (level 0 needs the registration, levels ≤ 1 the fusion). It does not change any tile's value.</summary>
    public int BuildLevel { get; init; } = -1;

    /// <summary>The Lux noise controls this state's module ISPs were built with (<see cref="BuildOptions.Noise"/>). With other values than
    /// Lumen's, the tiles of this state are not Lumen's tiles: they must not go into the analysis cache.</summary>
    public NoiseControls Noise { get; init; } = NoiseControls.Lumen;

    readonly object _lock = new();
    (float[] Depth, int W, int H)? _fullDepth; bool _fullDepthSet;
    /// <summary>Supplies <see cref="FullDepth"/> on first read when nobody has set it (the build's registration geometry).</summary>
    internal Func<(float[] Depth, int W, int H)?>? FullDepthSource;

    /// <summary>The upsampled full-frame depth (`StereoAsyncApi.FullDepth`), when the registration chain ran — the
    /// source `RendererPrivate::setInputDataStream` fills the renderer's depth ImageCache from, and therefore
    /// what `ExportImageFormat` 4 (JPEG + GDepth) needs. Null when only levels >= 1 were built.
    /// <para>On a level-0 state the first read runs the registration chain (or takes the geometry from the analysis cache). Setting it
    /// (saved depth edits put their pinned depth here) replaces it for every depth reader; the level-0 render keeps reading the
    /// registration's own depth (<see cref="Geometry"/>).</para></summary>
    public (float[] Depth, int W, int H)? FullDepth
    {
        get
        {
            if (_fullDepthSet) return _fullDepth;
            lock (_lock)
            {
                if (!_fullDepthSet) { _fullDepth = FullDepthSource?.Invoke(); _fullDepthSet = true; }
                return _fullDepth;
            }
        }
        set { lock (_lock) { _fullDepth = value; _fullDepthSet = true; } }
    }

    /// <summary>Whether <see cref="FullDepth"/> has been resolved or set (reading it would not run anything).</summary>
    public bool FullDepthResolved => _fullDepthSet;

    /// <summary>Forget saved or live depth edits and a pinned depth put here: <see cref="EditedDepth"/> becomes null and the next read of
    /// <see cref="FullDepth"/> resolves the build's own depth again (an app switching the photo's depth version).</summary>
    public void ResetDepth()
    {
        lock (_lock) { EditedDepth = null; _fullDepth = null; _fullDepthSet = false; }
    }

    /// <summary>Saved depth edits applied to this capture (<see cref="ICaptureContext.ApplySavedEdits"/>): the edited depth cache and its edit layer.
    /// When set, every reader of the depth cache (the depth effect, `jpg+depth`, the depth map, the parallax formats) uses it instead
    /// of the plain cache of <see cref="FullDepth"/>. Null = no saved edits in use.</summary>
    public AppliedDepthEdits? EditedDepth { get; set; }

    /// <summary>The depth cache of this capture: the edited one when saved edits are in use, else the plain cache of
    /// <see cref="FullDepth"/> for the given level tables.</summary>
    public Lux.Engine.Pipeline.Export.DepthImageCache DepthCache(Lux.Engine.Pipeline.Export.ExportLevels win)
    {
        if (EditedDepth is { } e)
        {
            var want = Lux.Engine.Pipeline.Export.DepthImageCache.CacheDims(win);
            if ((e.Cache.Current.W, e.Cache.Current.H) != want)
                throw new ExportRequestException($"the saved depth edits are stored on a {e.Cache.Current.W}x{e.Cache.Current.H} depth cache, this export needs {want.W}x{want.H}; use --no-edits");
            return e.Cache;
        }
        var (d, dw, dh) = FullDepth ?? throw new ExportRequestException("the registration chain produced no full-frame depth for this capture");
        return Lux.Engine.Pipeline.Export.DepthImageCache.FromFullDepth(d, dw, dh, win);
    }

    Lux.Engine.Pipeline.Registration.StereoAsyncApi? _registration;
    /// <summary>Supplies <see cref="Registration"/> when nobody has set it: the build's registration state if the chain has run (never runs it).</summary>
    internal Func<Lux.Engine.Pipeline.Registration.StereoAsyncApi?>? RegistrationSource;

    /// <summary>The registration state the build ran (`StereoAsyncApi`: the ctor pairs `Pairs[id]`, and at level 0
    /// the whole chain). The parallax geometry reads the reference-group pairs from it — a level-0 build leaves those
    /// exactly as `Setup()` made them — so no second setup is needed. Optional: null when the chain has not run (or its geometry
    /// came from the analysis cache), and <see cref="ExportSession.Registration"/> then sets one up itself.</summary>
    public Lux.Engine.Pipeline.Registration.StereoAsyncApi? Registration
    {
        get => _registration ?? RegistrationSource?.Invoke();
        set => _registration = value;
    }

    /// <summary>Resolves <see cref="Geometry"/> (runs the registration chain unless the geometry is known); null on a state built for levels ≥ 1.</summary>
    internal Func<CaptureGeometry?>? GeometrySource;
    /// <summary>Whether the geometry is already known (reading <see cref="Geometry"/> would not run the registration chain).</summary>
    internal Func<bool>? GeometryKnown;

    /// <summary>The registration geometry a level-0 state renders with (computed on first use, or loaded from the analysis cache); null on a
    /// state built for levels ≥ 1.</summary>
    public CaptureGeometry? Geometry => GeometrySource?.Invoke();
    /// <summary>Whether <see cref="Geometry"/> is available without running the registration chain.</summary>
    public bool HasGeometry => GeometryKnown?.Invoke() ?? false;

    /// <summary>Replaces the build's lazily made parts with a fresh, empty set (<see cref="ReleaseLevel0Inputs"/>).</summary>
    internal Action? ReleaseInputs;

    /// <summary>Let go of everything the build made to generate PipelineCache tiles — the stacked-frame fusions, the level-1 fusion, the
    /// reference and telephoto level-0 caches and the level-0 generator (about 1 GB on a full-size render, more on a stacked capture) — while
    /// keeping the PipelineCache tiles already made and the registration geometry. A tile asked for later is generated from freshly made
    /// parts: the same values, at the cost of making them again. Call only while no render of this state is running.</summary>
    public void ReleaseLevel0Inputs() => ReleaseInputs?.Invoke();

    /// <summary>The progress channel the build reported through; the caches keep it for the inputs they make lazily, and the
    /// export session reports through the same one so a file has a single phase sequence.</summary>
    public ProgressReporter Progress { get; init; } = ProgressReporter.None;
}

/// <summary>Saved depth edits put in place of the plain depth cache: the variant, its kind (`lux` / `lumen-import`), the edited depth
/// cache and edit layer every depth reader of an export then uses, and how many tiles were edited.</summary>
public sealed record AppliedDepthEdits(string Variant, string Kind, Lux.Engine.Pipeline.Export.DepthImageCache Cache, Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer Layer, int Tiles);

/// <summary>A value computed once on first use and shared by every caller, which — unlike <see cref="Lazy{T}"/> — does not keep an
/// exception: a canceled or failed computation runs again on the next read. Concurrent readers wait for the one computation.</summary>
public sealed class Once<T>
{
    readonly object _lock = new();
    Func<T>? _factory; T _value = default!; volatile bool _done;
    public Once(Func<T> factory) { _factory = factory; }
    public Once(T value) { _value = value; _done = true; }
    public bool IsValueCreated => _done;
    public T Value
    {
        get
        {
            if (_done) return _value;
            lock (_lock)
            {
                if (!_done) { _value = _factory!(); _done = true; _factory = null; }
                return _value;
            }
        }
    }
}
