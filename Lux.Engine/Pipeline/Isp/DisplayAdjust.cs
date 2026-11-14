using System.Runtime.InteropServices;
using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// Live tone and colour editing on the display path: a grown linear tile (the tile that Lumen's display tile generator fetches from
/// the PipelineCache) plus <see cref="Adjustments"/> → the display tile, exactly as Lumen renders it after `setProperty(ParamFloat)`
/// and its property mapping. The output ISP runs over the whole grown tile with no runner growth (its ROI is the tile itself), so every
/// stage is a function of the previous stage's image and of its own parameters only: a <see cref="TileStageCache"/> keeps the
/// image after each stage and a change restarts at the first stage whose parameters differ (<see cref="Adjustments.FirstChangedStage"/>),
/// which gives the same floats as a full run.
/// </summary>
public sealed class DisplayAdjust
{
    /// <summary>The display stage list in execution order (slots 2, 10, 11, 12, 13, 14, 15).</summary>
    public static readonly StageName[] StageOrder =
    {
        StageName.Placeholder, StageName.ColorCorrection, StageName.PostProcessing, StageName.LensShading,
        StageName.ToneAdjust, StageName.ContrastAdjust, StageName.ToneMapping,
    };

    readonly CapturedFrame _frame; readonly LumenProfile _profile; readonly WhiteBalance.CaptureWb _wb; readonly float _captureEv;
    readonly Func<int, Tuning> _loadedTuning; readonly IspStats _loadedStats;
    readonly object _sync = new();
    readonly Dictionary<(int Level, Adjustments A), (IReadOnlyList<IStage> Stages, IspStats Stats, Tuning Tuning)> _isps = new();

    /// <param name="loadedTuningOfLevel">the output tuning `setInputDataStream` builds per level (<see cref="DisplayIspTuning.Build"/>)</param>
    /// <param name="loadedStats">the level-0 Stats of that tuning (shared by every level)</param>
    public DisplayAdjust(CapturedFrame frame, LumenProfile profile, WhiteBalance.CaptureWb wb, float captureEv, Func<int, Tuning> loadedTuningOfLevel, IspStats loadedStats)
    {
        _frame = frame; _profile = profile; _wb = wb; _captureEv = captureEv; _loadedTuning = loadedTuningOfLevel; _loadedStats = loadedStats;
    }

    /// <summary>The adjustments of a capture whose sliders are untouched (temperature and tint as loaded).</summary>
    public (float Temperature, float Tint) CaptureWhiteBalance => Adjustments.CaptureWhiteBalance(_wb);

    /// <summary>The stage list, the Stats and the tuning one level runs with for <paramref name="a"/>.</summary>
    public (IReadOnlyList<IStage> Stages, IspStats Stats, Tuning Tuning) IspFor(int level, Adjustments a)
    {
        lock (_sync)
        {
            if (_isps.TryGetValue((level, a), out var hit)) return hit;
            if (_isps.Count > 64) _isps.Clear();
            var neutral = a.WhiteBalanceNeutralOrNull(_wb, _profile);
            var tuning = a.ApplyTo(_loadedTuning(level), _captureEv, neutral);
            var stats = a.ApplyTo(_loadedStats, _wb, _profile);
            var stages = StageGraph.Build(PayloadDomain.Color, tuning);
            if (stages.Count != StageOrder.Length || !stages.Select(s => s.Stage).SequenceEqual(StageOrder))
                throw new InvalidOperationException($"unexpected display stage list: {string.Join(", ", stages.Select(s => s.Stage))}");
            return _isps[(level, a)] = (stages, stats, tuning);
        }
    }

    /// <summary>Indices (into <see cref="StageOrder"/>) of the stages that write into their input image instead of making a new one:
    /// PostProcessing (writes its result back into the working image), LensShading and ToneMapping. ÷ neutral, ColorCorrection,
    /// ToneAdjust and ContrastAdjust (when it runs) allocate their output, so their input survives them.</summary>
    static readonly bool[] InPlace = { false, false, true, true, false, false, true };

    /// <summary>
    /// One display tile: <paramref name="grownLinear"/> covers <see cref="DisplayRender.TileGeometry.Grown"/> (the PipelineCache fetch of
    /// the display tile generator); the result is the cropped float RGBA tile (feed it to <see cref="DisplayOutput.ToRgba8Display"/> or the export store).
    /// The input image is never modified.
    /// <para>With a <paramref name="cache"/> (one per tile, same input image on every call) the render restarts at the nearest kept stage
    /// input at or before the first changed stage. The kept images are references to the stage outputs themselves, not copies: an image
    /// is copied only right before a stage that would write into it (<see cref="InPlace"/>) while the cache holds it — at most once per
    /// render for ToneMapping's input, plus once for PostProcessing's input when <see cref="KeepPostProcessingInput"/> is set.</para>
    /// </summary>
    public Image<Vec4F> Render(Image<Vec4F> grownLinear, DisplayRender.TileGeometry g, int level, Adjustments a, TileStageCache? cache = null)
    {
        var (stages, stats, tuning) = IspFor(level, a);
        int start = 0, changed = -1; bool streak = false;
        Image<Vec4F> img = grownLinear;   // ÷ neutral allocates its output: the input needs no copy
        if (cache is not null)
        {
            if (cache.Last is { } last && cache.Level == level && ReferenceEquals(cache.Input, grownLinear))
            {
                int k = Adjustments.FirstChangedStage(last, a, _wb);
                changed = k; streak = k != int.MaxValue && k == cache.LastChanged;
                if (k == int.MaxValue && cache.Final is not null) return Crop(cache.Final, g);
                for (int j = Math.Min(k, stages.Count - 1); j > 0; j--)
                    if (cache.StageInput[j] is { } kept) { start = j; img = kept; break; }
            }
            else cache.Clear();
            cache.Retire(start, grownLinear);   // the images beyond the restart point are stale: their buffers become copy targets

        }
        var ctx = new PipelineContext { Header = _frame.Header, Module = _frame.Module, Tuning = tuning, Level = level, FrameWidth = _frame.Width, FrameHeight = _frame.Height };
        var payload = new IspPayload { Rgb = img, Stats = stats, Frame = _frame.Info, Context = ctx };
        var run = new List<IStage>(stages.Count - start);
        // while one slider is being dragged (the same first changed stage as last time) only the inputs up to that stage are kept: the
        // later ones would cost copies that the next change of the same slider cannot use
        int keepUpTo = streak ? changed : int.MaxValue;
        for (int i = start; i < stages.Count; i++) run.Add(new CacheStage(stages[i], i, this, cache, grownLinear, keepUpTo));
        // the ROI is the source image (0,0,w,h): every stage sees the whole tile, no growth
        PipelineRunner.Run(run, payload, grownLinear.Rect, grownLinear.Rect, g.Float);
        if (cache is not null) { cache.Last = a; cache.Level = level; cache.Input = grownLinear; cache.Final = payload.Rgb; cache.LastChanged = changed; }
        return Crop(payload.Rgb!, g);
    }

    /// <summary>Keep PostProcessing's input (the ColorCorrection output) so saturation / vibrance / sharpening changes skip ÷ neutral and
    /// ColorCorrection. It costs one tile copy per render (PostProcessing writes in place); on by default.</summary>
    public bool KeepPostProcessingInput { get; init; } = true;

    bool Keeps(int stageIndex) => stageIndex switch { 2 => KeepPostProcessingInput, 4 or 5 or 6 => true, _ => false };

    /// <summary>The runner-facing wrapper: copy-on-write before an in-place stage, record the kept stage inputs.</summary>
    sealed class CacheStage : IStage
    {
        readonly IStage _s; readonly int _i; readonly DisplayAdjust _owner; readonly TileStageCache? _c; readonly Image<Vec4F> _input; readonly int _keepUpTo;
        public CacheStage(IStage s, int i, DisplayAdjust owner, TileStageCache? c, Image<Vec4F> input, int keepUpTo) { _s = s; _i = i; _owner = owner; _c = c; _input = input; _keepUpTo = keepUpTo; }
        public StageName Stage => _s.Stage; public string TypeString => _s.TypeString; public StageMeta Meta => _s.Meta;
        public void Apply(IspPayload p)
        {
            if (_c is not null && _owner.Keeps(_i) && _i <= _keepUpTo) _c.StageInput[_i] = p.Rgb;
            if (InPlace[_i] && p.Rgb is { } cur && (ReferenceEquals(cur, _input) || (_c is not null && _c.Holds(cur)))) p.Rgb = _c is null ? Clone(cur) : _c.Copy(cur);
            _s.Apply(p);
        }
    }

    /// <summary>Crop the processed grown tile back to the requested tile (the tail of <see cref="DisplayRender.Run"/>).</summary>
    static Image<Vec4F> Crop(Image<Vec4F> outImg, DisplayRender.TileGeometry g)
    {
        var r = outImg.Rect;
        int ix0 = Math.Max(r.X0, g.GrowL), iy0 = Math.Max(r.Y0, g.GrowT);
        int ix1 = Math.Min(r.X1, g.Abs.Width + g.GrowL), iy1 = Math.Min(r.Y1, g.Abs.Height + g.GrowT);
        if (ix1 <= ix0 || iy1 <= iy0) throw new InvalidOperationException("display tile crop is empty");
        return outImg.View(new RectI(ix0, iy0, ix1, iy1));
    }

    static Image<Vec4F> Clone(Image<Vec4F> src)
    {
        var dst = new Image<Vec4F>(src.Rect);
        for (int y = 0; y < src.Height; y++) src.Row(y).CopyTo(dst.Row(y));
        return dst;
    }

    /// <summary>The grown linear tile of a display tile: the display tile generator's PipelineCache fetch at `level + profile offset` (pipeline-level
    /// coordinates, halo 64), as float RGBA.</summary>
    public static Image<Vec4F> FetchLinear(Cache.PipelineCache cache, Export.ExportLevels lv, int level, RectI tile, out DisplayRender.TileGeometry g)
    {
        var pd = lv.PipelineDims[level];
        g = DisplayRender.Geometry(tile, pd, lv.Origins[level], lv.CacheDims, level);
        var px = cache.Render(lv.BaseLevel + g.CacheLevel, g.Grown, pd);
        var grown = new Image<Vec4F>(g.Grown.Width, g.Grown.Height);
        MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(grown.Data);
        return grown;
    }
}

/// <summary>Per-tile memory of <see cref="DisplayAdjust.Render"/>: the settings of the last render, its input, the kept stage inputs
/// (references to the images the stages produced: PostProcessing's, ToneAdjust's, ContrastAdjust's and ToneMapping's inputs) and the
/// final image. One per tile; not thread-safe (render one tile from one thread at a time).</summary>
public sealed class TileStageCache
{
    public Adjustments? Last;
    public int Level = -1;
    /// <summary>The first changed stage of the last render (−1 = a fresh render), to recognise a slider being dragged.</summary>
    public int LastChanged = -1;
    public Image<Vec4F>? Input;
    public readonly Image<Vec4F>?[] StageInput = new Image<Vec4F>?[DisplayAdjust.StageOrder.Length];
    public Image<Vec4F>? Final;
    readonly List<Image<Vec4F>> _spare = new();
    public void Clear() { Last = null; Input = null; Final = null; LastChanged = -1; Array.Clear(StageInput); _spare.Clear(); }

    /// <summary>Drop the kept images after stage <paramref name="start"/> and the final image; those no longer referenced become spare
    /// buffers for <see cref="Copy"/> (a stage overwrites every pixel of its copy, so a recycled buffer gives the same result).</summary>
    internal void Retire(int start, Image<Vec4F> input)
    {
        var drop = new List<Image<Vec4F>>();
        for (int j = start + 1; j < StageInput.Length; j++) { if (StageInput[j] is { } k) drop.Add(k); StageInput[j] = null; }
        if (Final is { } f) drop.Add(f);
        Final = null;
        foreach (var d in drop)
            if (!ReferenceEquals(d, input) && !Holds(d) && !_spare.Any(x => ReferenceEquals(x, d)) && _spare.Count < 4) _spare.Add(d);
    }

    /// <summary>A copy of <paramref name="src"/> in a spare buffer of the same extent when one is free, else in a new one.</summary>
    internal Image<Vec4F> Copy(Image<Vec4F> src)
    {
        Image<Vec4F>? dst = null;
        for (int i = 0; i < _spare.Count; i++)
            if (_spare[i].Rect == src.Rect && _spare[i].Width == src.Width) { dst = _spare[i]; _spare.RemoveAt(i); break; }
        dst ??= new Image<Vec4F>(src.Rect);
        for (int y = 0; y < src.Height; y++) src.Row(y).CopyTo(dst.Row(y));
        return dst;
    }
    internal bool Holds(Image<Vec4F> img)
    {
        if (ReferenceEquals(img, Final)) return true;
        foreach (var k in StageInput) if (ReferenceEquals(k, img)) return true;
        return false;
    }
    /// <summary>Bytes held (distinct images incl. spare buffers, 16 B per pixel), the input excluded.</summary>
    public long Bytes
    {
        get
        {
            var seen = new HashSet<Image<Vec4F>>(ReferenceEqualityComparer.Instance);
            long b = 0;
            foreach (var k in StageInput.Append(Final).Concat(_spare)) if (k is not null && seen.Add(k)) b += (long)k.Width * k.Height * 16;
            return b;
        }
    }
}
