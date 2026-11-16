using Ltpb;

namespace Lux.Engine.Pipeline;

/// <summary>The five payload domains of <c>lt::Internal::Pipeline</c>.</summary>
public enum PayloadDomain { BayerFloat, Bayer, BayerToMono, Color, Mono }

/// <summary>
/// The 16 stage slots of every domain block, listed in Lumen's EXECUTION order (the order of its stage vector) — not the `set*`
/// call order. <c>Placeholder</c> is slot 2 (a no-op lambda).
/// </summary>
public enum StageName
{
    HotPixelLeakageRemoval, HotPixelRemoval, HighlightRestore, Placeholder, BayerPhaseFix, CrossTalkCorrection,
    Demosaicking, ColorNoiseReduction, AdaptiveDesaturation, Denoising, ColorCorrection, PostProcessing,
    LensShading, ToneAdjust, ContrastAdjust, ToneMapping,
}

public static class StageNames
{
    /// <summary>Tuning section per stage (the key prefix Lumen reads, e.g. "lens_shading").</summary>
    public static string TuningSection(StageName s) => s switch
    {
        StageName.HotPixelLeakageRemoval => "hot_pixel_leakage_removal",
        StageName.HotPixelRemoval => "hot_pixel_removal",
        StageName.HighlightRestore => "highlight_restore",
        StageName.BayerPhaseFix => "bayer_phase_fix",
        StageName.CrossTalkCorrection => "cross_talk_correction",
        StageName.Demosaicking => "demosaicking",
        StageName.ColorNoiseReduction => "color_noise_reduction",
        StageName.AdaptiveDesaturation => "adaptive_desaturation",
        StageName.Denoising => "denoising",
        StageName.ColorCorrection => "color_correction",
        StageName.LensShading => "lens_shading",
        StageName.ToneAdjust => "tone_adjust",
        StageName.ContrastAdjust => "contrast_adjust",
        StageName.ToneMapping => "tone_mapping",
        StageName.PostProcessing => "post_processing",   // Lumen: always installed by the ctor, no tuning type
        _ => "placeholder",
    };
    public static readonly StageName[] ExecutionOrder = Enum.GetValues<StageName>();
}

/// <summary>Per-stage slot metadata (pad, align, scale) used by the runner's padding pass.</summary>
public readonly record struct StageMeta(int Pad = 1, int Align = 1, float Scale = 1f);

public readonly record struct RectF(float X0, float Y0, float X1, float Y1);

/// <summary>Pipeline statistics (`SoftISP::Stats`): the WB neutral
/// (consumed by crosstalk / demosaic / CNR), temp/tint, the camera→output matrix (identity when
/// `output.color_space = none`), and the color profile object.</summary>
public sealed class IspStats
{
    public float[] Neutral { get; init; } = { 1f, 1f, 1f };
    public float Cct { get; init; }
    public float Tint { get; init; }
    /// <summary>The WB chromaticity (input of the xy → CCT conversion for the IR fit).</summary>
    public (float X, float Y) NeutralXy { get; init; }
    /// <summary>The IR-correction blend, NaN when not computed (crosstalk ≠ ir_correction).</summary>
    public float IrBlend { get; init; } = float.NaN;
    /// <summary>The `lt::Sensor` of the capture the stats were built from: per-frame black, white and the noise
    /// models. The stereo ISP builds its stats ONCE from the reference capture and reuses them for every module (`StereoAsyncAPI::start`),
    /// so non-reference modules are linearized with the REFERENCE frame's black.</summary>
    public float SensorBlack { get; init; } = float.NaN;
    public float SensorWhite { get; init; } = float.NaN;
    public Isp.SensorNoise? Noise { get; init; }
    public float[] CameraToOutput { get; init; } = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    /// <summary>The `lt::ColorSpace` the ColorCorrection Stats functor installs (`setColorCorrection` default: the profile's
    /// ForwardMatrix interpolated at the WB CCT, white point D50, space 0; none: identity/space 1; manual: `color_correction.matrix`).</summary>
    public Color.ColorSpace CcSpace { get; init; } = Color.ColorSpace.None();
    /// <summary>The output `lt::ColorSpace` (`output.color_space`/`output.white_point`,
    /// or the CC space / ProPhoto-D50 when one of them is `none`).</summary>
    public Color.ColorSpace OutSpace { get => _outSpace; init => _outSpace = value; }
    Color.ColorSpace _outSpace = Color.ColorSpace.None();
    /// <summary>A copy of these stats with another output color space. All other values stay the same.</summary>
    public IspStats WithOutSpace(Color.ColorSpace outSpace) { var c = (IspStats)MemberwiseClone(); c._outSpace = outSpace; return c; }
    /// <summary>The CCT-interpolated `lt::HSVMap` that the
    /// `color_correction = optimized` Stats functor builds from the profile's two
    /// HueSatMaps. Null/empty for every other `color_correction` type, in which case `optimized` degenerates to `default`.</summary>
    public Color.HsvMap? HsvMap { get; init; }
    public Color.LumenProfile? Profile { get; init; }
    /// <summary>Per-channel (R, G, B) noise σ tables indexed by raw DN for the frame's sensor gain.</summary>
    public float[][]? NoiseSigma { get; init; }
}

/// <summary>
/// The runner payload (passed to every slot functor): the tile rectangles in the three
/// forms Lumen keeps (float, int, int × stage scale — all relative to the ROI origin of the
/// current stage input) and the image handles a domain carries (Bayer ushort/float source, RGB working image,
/// stack-STD plane), plus the stats and the frame.
/// </summary>
public sealed class IspPayload
{
    /// <summary>ROI-relative integer rect the stage must process/produce (grown by the runner's padding).</summary>
    public RectI IntRect;
    /// <summary>The same rect scaled by the stage's <see cref="StageMeta.Scale"/> (output coordinates).</summary>
    public RectI ScaledRect;
    /// <summary>The ROI as a float rect in the float coordinates the caller passed, grown like <see cref="IntRect"/>.</summary>
    public RectF FloatRect;
    /// <summary>Absolute origin of the ROI in the current stage's input coordinates.</summary>
    public int OriginX, OriginY;
    /// <summary>Bayer ushort source (Bayer domain) — a view of the whole frame; index with absolute coordinates via <see cref="ToView"/>.</summary>
    public Image<ushort>? Raw;
    /// <summary>Stacked float Bayer source (BayerFloat domain).</summary>
    public Image<float>? BayerFloat;
    /// <summary>Per-pixel noise STD plane of a stacked source (null for single captures).</summary>
    public Image<float>? Std;
    /// <summary>RGB working image (vec4x32f) — produced by demosaic, then processed in place.</summary>
    public Image<Vec4F>? Rgb;
    /// <summary>The previous RGB working image left in the payload by a stage that swapped in a new one (the hybrid denoiser);
    /// `PostProcessing` reads it as the LDiff companion (grain re-injection). Null when no such stage ran.</summary>
    public Image<Vec4F>? Companion;
    public required IspStats Stats;
    public required Isp.ModuleFrameInfo Frame;
    public required PipelineContext Context;

    /// <summary>Convert a ROI-relative rect to absolute input coordinates.</summary>
    public RectI ToAbsolute(RectI roiRelative) => new(roiRelative.X0 + OriginX, roiRelative.Y0 + OriginY, roiRelative.X1 + OriginX, roiRelative.Y1 + OriginY);
}

/// <summary>Everything a stage may read besides the pixels: the module, its calibration, the tuning, the level.</summary>
public sealed class PipelineContext
{
    public required LightHeader Header { get; init; }
    public required CameraModule Module { get; init; }
    public required Tuning Tuning { get; init; }
    /// <summary>Pyramid / config level of this pipeline instance (0 = sensor resolution).</summary>
    public int Level { get; init; }
    /// <summary>Source frame size (the CapturedImage rect the grid-based stages span).</summary>
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public Action<string>? Log { get; init; }
}

/// <summary>A stage body for one payload domain. Implementations are selected by (domain, stage, type string).</summary>
public interface IStage
{
    StageName Stage { get; }
    /// <summary>The Lumen tuning type string this implementation answers to ("light_v2", "inverse", …) or a new one.</summary>
    string TypeString { get; }
    StageMeta Meta { get; }
    /// <summary>Process the payload in place (Lumen's stages write back into the working images).</summary>
    void Apply(IspPayload payload);
}

public delegate IStage StageFactory(Tuning tuning);

/// <summary>
/// Registry of stage implementations keyed by (domain, stage, type string). Lumen's own implementations register
/// under Lumen's type strings; alternatives register under new strings and are chosen purely by tuning.
/// </summary>
public static class StageRegistry
{
    private static readonly Dictionary<(PayloadDomain, StageName, string), StageFactory> _reg = new();
    private static readonly object _lock = new();

    public static void Register(PayloadDomain domain, StageName stage, string typeString, StageFactory factory)
    {
        lock (_lock) _reg[(domain, stage, typeString)] = factory;
    }

    public static bool TryCreate(PayloadDomain domain, StageName stage, string typeString, Tuning tuning, out IStage? stageImpl)
    {
        StageFactory? f;
        lock (_lock) _reg.TryGetValue((domain, stage, typeString), out f);
        stageImpl = f?.Invoke(tuning);
        return stageImpl is not null;
    }

    public static IEnumerable<(PayloadDomain Domain, StageName Stage, string Type)> Registered
    {
        get { lock (_lock) return _reg.Keys.ToArray(); }
    }
}

/// <summary>
/// Builds the ordered stage list for a domain from the tuning (the stage factory + the domain block's
/// stage vector): a stage whose <c>&lt;section&gt;.type</c> is "none" (or unset) is skipped, exactly like an empty
/// Lumen slot; a type string with no registered implementation is an error (never a silent fallback).
/// </summary>
public static class StageGraph
{
    /// <summary>The (stage, type) pairs the tuning selects, in execution order — the implementation checklist.</summary>
    public static List<(StageName Stage, string Type)> Required(Tuning tuning)
    {
        var r = new List<(StageName, string)>();
        foreach (var s in StageNames.ExecutionOrder)
        {
            string section = StageNames.TuningSection(s);
            // slot 2 (Placeholder) and PostProcessing are installed by the Pipeline ctor, not by tuning
            string type = s is StageName.PostProcessing or StageName.Placeholder ? "default" : (tuning.Has(section + ".type") ? tuning.Type(section) : "none");
            if (type == "none") continue;
            r.Add((s, type));
        }
        return r;
    }

    // The BayerFloat block installs every stage its tuning names, like the other blocks. This includes crosstalk (slot 5) and lens
    // shading (slot 12): the MonoFusion's own ISP runs both. The MONO configuration sets those types to "none" through its tuning.

    public static List<IStage> Build(PayloadDomain domain, Tuning tuning)
    {
        var stages = new List<IStage>();
        var skip = Environment.GetEnvironmentVariable("LUX_SKIP_STAGE");   // diagnostic only: comma list of stage names to leave out
        foreach (var (s, type) in Required(tuning))
        {
            if (skip is not null && skip.Split(',').Contains(s.ToString()))
            {   // diagnostic: like a Lumen slot with a null impl, the slot keeps its pad/align/scale for the padding pass but is never run
                Console.Error.WriteLine($"[diagnostic] LUX_SKIP_STAGE: skipping {domain}/{s} — output is NOT Lumen-faithful");
                if (StageRegistry.TryCreate(domain, s, type, tuning, out var sk)) stages.Add(new SkippedStage(sk!));
                continue;
            }
            if (StageRegistry.TryCreate(domain, s, type, tuning, out var impl)) stages.Add(impl!);
            else if (Environment.GetEnvironmentVariable("LUX_SKIP_MISSING") == "1")
                Console.Error.WriteLine($"[diagnostic] LUX_SKIP_MISSING: skipping unimplemented {domain}/{s} type '{type}' — output is NOT Lumen-faithful");
            else throw new NotSupportedException($"no implementation registered for {domain}/{s} type '{type}'");
        }
        return stages;
    }
}

/// <summary>
/// The stage runner of Lumen's pipeline:
/// backward pass — <c>need[last] = 0; need[k−1] = c + c % align[k−1]</c> with
/// <c>c = ceil((pad[k] &gt;&gt; 1) + need[k] · (1 / scale[k−1]))</c> (so need[j] is the margin stage j must produce for the
/// stages after it); forward pass — for every stage grow the ROI-relative rect by <c>min(need[j], available)</c> on each
/// side, hand the stage the int/scaled/float rects, then shrink the available region to what the stage produced
/// (× its scale). The available region starts as the whole frame relative to the ROI origin.
/// </summary>
/// <summary>An installed-but-empty slot (meta 1/1/1, no work) for domains where a slot has no stage of that name.</summary>
public sealed class EmptySlotStage : IStage
{
    public EmptySlotStage(StageName name) { Stage = name; }
    public StageName Stage { get; }
    public string TypeString => "default";
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p) { }
}

/// <summary>Diagnostic wrapper (LUX_SKIP_STAGE): the slot's meta stays in the padding pass, the runner skips the stage entirely
/// (Lumen skips a slot with a null impl, including its grow/shrink bookkeeping).</summary>
public sealed class SkippedStage : IStage
{
    public IStage Inner { get; }
    public SkippedStage(IStage inner) { Inner = inner; }
    public StageName Stage => Inner.Stage;
    public string TypeString => Inner.TypeString;
    public StageMeta Meta => Inner.Meta;
    public void Apply(IspPayload p) { }
}

public static class PipelineRunner
{
    static bool _dbgOnce;
    public static int[] PaddingPass(IReadOnlyList<IStage> stages)
    {
        // The backward pass runs over ALL 16 slots of the domain block in execution order (empty slots keep the default
        // meta {1, 1, 1.0}), so a pad introduced after a scaling stage is divided by that scale only when the pass reaches
        // the scaling slot (e.g. stereo pipeline: PostProcessing pad 7 → need 3 through the empty slots 7–10 → 3/0.5 = 6 at
        // the collapse2 demosaic). Installed stages are then read back by slot.
        var order = StageNames.ExecutionOrder;
        int m = order.Length;
        var meta = new StageMeta[m]; var idx = new int[m];
        for (int i = 0; i < m; i++) { meta[i] = new StageMeta(1, 1, 1f); idx[i] = -1; }
        for (int j = 0; j < stages.Count; j++)
        {
            int slot = Array.IndexOf(order, stages[j].Stage);
            if (slot < 0 || idx[slot] >= 0) throw new InvalidOperationException($"stage {stages[j].Stage} has no unique slot in the execution order");
            meta[slot] = stages[j].Meta; idx[slot] = j;
        }
        var needAll = new int[m];
        needAll[m - 1] = 0;
        for (int k = m - 1; k >= 1; k--)
        {
            var prev = meta[k - 1];
            float v = (float)(meta[k].Pad >> 1) + (float)needAll[k] * (1f / prev.Scale);
            int c = (int)MathF.Ceiling(v);
            int align = Math.Max(prev.Align, 1);
            needAll[k - 1] = c + c % align;
        }
        var need = new int[Math.Max(stages.Count, 1)];
        for (int slot = 0; slot < m; slot++) if (idx[slot] >= 0) need[idx[slot]] = needAll[slot];
        return need;
    }

    /// <summary>Run the stages on a payload whose sources cover the frame; <paramref name="roi"/> is the clamped ROI in
    /// frame coordinates, <paramref name="frameW"/>/<paramref name="frameH"/> the source dimensions,
    /// <paramref name="floatRoi"/> the caller's float rect (Lumen: the ROI itself).</summary>
    public static void Run(IReadOnlyList<IStage> stages, IspPayload p, RectI roi, int frameW, int frameH, RectF floatRoi)
        => Run(stages, p, roi, new RectI(0, 0, frameW, frameH), floatRoi);

    /// <summary><paramref name="available"/> = the source image's extents in frame coordinates (the whole frame for a raw capture; the halo'd
    /// render rect for the BayerFloat path, whose Bayer/STD views carry the halo in their rect fields).</summary>
    public static void Run(IReadOnlyList<IStage> stages, IspPayload p, RectI roi, RectI available, RectF floatRoi)
    {
        var need = PaddingPass(stages);
        if (Environment.GetEnvironmentVariable("LUX_ISP_DEBUG") == "1" && !_dbgOnce) { _dbgOnce = true; for (int j = 0; j < stages.Count; j++) Console.Error.WriteLine($"[runner] {j}: {stages[j].Stage}/{stages[j].TypeString} meta pad {stages[j].Meta.Pad} align {stages[j].Meta.Align} scale {stages[j].Meta.Scale} need {need[j]}"); }
        // available region relative to the ROI origin (image view rect)
        int ax0 = available.X0 - roi.X0, ay0 = available.Y0 - roi.Y0, ax1 = available.X1 - roi.X0, ay1 = available.Y1 - roi.Y0;
        int w = roi.Width, h = roi.Height;
        int ox = roi.X0, oy = roi.Y0;
        float fx0 = floatRoi.X0, fy0 = floatRoi.Y0, fx1 = floatRoi.X1, fy1 = floatRoi.Y1;
        for (int j = 0; j < stages.Count; j++)
        {
            if (stages[j] is SkippedStage) continue;   // null impl: no grow, no region update (Lumen skips the whole body)
            int n = need[j];
            int gl = Math.Min(n, -ax0), gt = Math.Min(n, -ay0);
            int gr = Math.Min(n, ax1 - w), gb = Math.Min(n, ay1 - h);
            float s = stages[j].Meta.Scale;
            p.IntRect = new RectI(-gl, -gt, w + gr, h + gb);
            // The scaled rect: the un-grown ROI placed inside the grown output region, in output coordinates: (s·gl, s·gt, s·gl + s·w, s·gt + s·h),
            // with (int)(s·gl) and (int)(s·w) truncated separately.
            p.ScaledRect = new RectI((int)(s * gl), (int)(s * gt), (int)(s * gl) + (int)(s * w), (int)(s * gt) + (int)(s * h));
            float px = (fx1 - fx0) / w, py = (fy1 - fy0) / h;
            p.FloatRect = new RectF(fx0 - px * gl, fy0 - py * gt, fx1 + px * gr, fy1 + py * gb);
            p.OriginX = ox; p.OriginY = oy;
            if (Environment.GetEnvironmentVariable("LUX_ISP_DEBUG") == "1") Console.Error.WriteLine($"[runner rect] {j} {stages[j].Stage} need {n} int ({p.IntRect.X0} {p.IntRect.Y0} {p.IntRect.X1} {p.IntRect.Y1}) scaled ({p.ScaledRect.X0} {p.ScaledRect.Y0} {p.ScaledRect.X1} {p.ScaledRect.Y1}) float ({p.FloatRect.X0} {p.FloatRect.Y0} {p.FloatRect.X1} {p.FloatRect.Y1}) origin ({ox} {oy})");
            using (Diagnostics.PhaseLog.On ? Diagnostics.PhaseLog.Time("isp." + stages[j].Stage + ":" + stages[j].TypeString) : default) stages[j].Apply(p);
            // Diagnostic: `LUX_STAGE_DUMP=<prefix>` writes the RGB working image after every stage as raw RGBA float rows,
            // `{prefix}_st_er<exposureRatio>_o<x>_<y>_<j>_<stage>_<w>x<h>.f32`.
            if (Environment.GetEnvironmentVariable("LUX_STAGE_DUMP") is string sdp && p.Rgb is { } rimg)
            {
                var bytes = new byte[rimg.Width * rimg.Height * 16];
                for (int y = 0; y < rimg.Height; y++) System.Runtime.InteropServices.MemoryMarshal.AsBytes(rimg.Row(y)).CopyTo(bytes.AsSpan(y * rimg.Width * 16, rimg.Width * 16));
                System.IO.File.WriteAllBytes($"{sdp}_st_er{p.Frame.ExposureRatio:R}_o{ox}_{oy}_{j:D2}_{stages[j].Stage}_{rimg.Width}x{rimg.Height}.f32", bytes);
            }
            // what the stage produced, in its output coordinates, relative to the (scaled) ROI origin
            ax0 = -(int)(s * gl); ay0 = -(int)(s * gt);
            w = (int)(s * w); h = (int)(s * h);
            ax1 = (int)(s * gr) + w; ay1 = (int)(s * gb) + h;
            ox = (int)(s * ox); oy = (int)(s * oy);
        }
    }
}
