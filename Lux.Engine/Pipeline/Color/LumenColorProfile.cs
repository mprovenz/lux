using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.Color;

/// <summary>One ColorCalibration entry after Lumen's load-time processing of the entry.</summary>
public sealed record IlluminantEntry(ColorCalibration.Types.IlluminantType ProtoType, int InternalIlluminant, double Cct, double Tint,
    float[] ColorMatrix, float[] ProtoForwardMatrix, ColorFit Fit);

/// <summary>Lumen's color profile object: the lowest- and highest-CCT entries of the reference
/// module with their DNG-facing matrices and HSV maps.</summary>
public sealed record LumenProfile(IlluminantEntry Low, IlluminantEntry High, IReadOnlyList<IlluminantEntry> All)
{
    /// <summary>The profile of the capture. The fits depend on the capture header alone, so they run once per open
    /// <see cref="LriFile"/> (on first use, by one thread); every call gets its own copy of the arrays. With
    /// <paramref name="log"/> the fits run again, so that their log lines appear.</summary>
    public static LumenProfile Compute(LriFile lri, Action<string>? log = null)
    {
        if (log is not null) return Fit(lri, log);
        var made = Volatile.Read(ref lri.ColorProfileFit);
        if (made is null)
        {
            Interlocked.CompareExchange(ref lri.ColorProfileFit, new Lazy<LumenProfile>(() => Fit(lri, null), LazyThreadSafetyMode.ExecutionAndPublication), null);
            made = lri.ColorProfileFit!;
        }
        return made.Value.Copy();
    }

    /// <summary>A deep copy: no array is shared with this profile, and Low and High are entries of the copied list.</summary>
    LumenProfile Copy()
    {
        var all = new List<IlluminantEntry>(All.Count);
        IlluminantEntry? lo = null, hi = null;
        foreach (var e in All)
        {
            var f = e.Fit;
            var c = e with
            {
                ColorMatrix = (float[])e.ColorMatrix.Clone(), ProtoForwardMatrix = (float[])e.ProtoForwardMatrix.Clone(),
                Fit = f with { Matrix = (float[])f.Matrix.Clone(), InitialMatrix = (float[])f.InitialMatrix.Clone(), ForwardMatrix = (float[])f.ForwardMatrix.Clone(), Grid = (float[])f.Grid.Clone() },
            };
            all.Add(c);
            if (ReferenceEquals(e, Low)) lo = c;
            if (ReferenceEquals(e, High)) hi = c;
        }
        return new LumenProfile(lo!, hi!, all);
    }

    static LumenProfile Fit(LriFile lri, Action<string>? log)
    {
        var h = lri.Header;
        var entries = new List<IlluminantEntry>();
        foreach (var cal in Calibration.ForModule(h, h.ImageReferenceCamera))
            foreach (var cc in cal.Color)
            {
                int ill = LumenColorTables.InternalIlluminant(cc.Type);
                var (cct, tint) = LumenColorTables.XyToCct(LumenColorTables.IlluminantX[ill], LumenColorTables.IlluminantY[ill]);
                var fit = HsvLutOptimizer.Fit(cc.MacbethData, log);
                log?.Invoke($"profile: {cc.Type} CCT {cct:F1} fit {(fit.Converged ? "converged" : "NOT converged")} in {fit.Iterations} it, cost {fit.InitialCost:F4} → {fit.Cost:F4}");
                entries.Add(new IlluminantEntry(cc.Type, ill, cct, tint, M3(cc.ColorMatrix), M3(cc.ForwardMatrix), fit));
            }
        if (entries.Count < 2) throw new InvalidOperationException("Color calibration must have at least 2 illuminants!");
        IlluminantEntry lo = entries[0], hi = entries[0];
        foreach (var e in entries) { if (e.Cct < lo.Cct) lo = e; if (e.Cct > hi.Cct) hi = e; }
        return new LumenProfile(lo, hi, entries);
    }

    public static float[] M3(Matrix3x3F m) => new[] { m.X00, m.X01, m.X02, m.X10, m.X11, m.X12, m.X20, m.X21, m.X22 };
}

public static class LumenComponents
{
    private static bool _done;
    public static void EnsureRegistered()
    {
        if (_done) return; _done = true;
        StageRegistry.Register(PayloadDomain.Bayer, StageName.HotPixelRemoval, "default", _ => new Isp.Stages.HotPixelRemovalStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Placeholder, "default", _ => new Isp.Stages.BayerToFloatStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Demosaicking, "light_v1", _ => new Isp.Stages.DemosaicLightV1Stage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.LensShading, "default", _ => new Isp.Stages.LensShadingStage(false));
        StageRegistry.Register(PayloadDomain.Bayer, StageName.LensShading, "inverse", _ => new Isp.Stages.LensShadingStage(true));
        StageRegistry.Register(PayloadDomain.Bayer, StageName.AdaptiveDesaturation, "default", _ => new Isp.Stages.AdaptiveDesaturateStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.CrossTalkCorrection, "ir_correction", _ => new Isp.Stages.CrossTalkStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.HighlightRestore, "default", _ => new Isp.Stages.HighlightRestoreStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.PostProcessing, "default", _ => new Isp.Stages.PostProcessingStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Demosaicking, "collapse2", _ => new Isp.Stages.Demosaic2xCatmullStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Denoising, "hybrid", _ => new Isp.Stages.DenoiseHybridStage());
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Demosaicking, "collapse4", _ => new Isp.Stages.CollapseDemosaicStage(4));
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Demosaicking, "collapse8", _ => new Isp.Stages.CollapseDemosaicStage(8));
        StageRegistry.Register(PayloadDomain.Bayer, StageName.ColorNoiseReduction, "default", _ => new Isp.Stages.ColorNoiseReductionStage());
        // display-ISP stages: `setDemosaicking` case 1 (default) == case 7 (light_v1);
        // ColorCorrection default/manual and ToneMapping default/linear (LinearTMO) are the same bodies in every domain.
        StageRegistry.Register(PayloadDomain.Bayer, StageName.Demosaicking, "default", _ => new Isp.Stages.DemosaicLightV1Stage());
        foreach (var dom in new[] { PayloadDomain.Bayer, PayloadDomain.BayerFloat, PayloadDomain.Color })
        {
            StageRegistry.Register(dom, StageName.ColorCorrection, "default", _ => new Isp.Stages.ColorCorrectionDefaultStage("default"));
            StageRegistry.Register(dom, StageName.ColorCorrection, "manual", _ => new Isp.Stages.ColorCorrectionDefaultStage("manual"));
            StageRegistry.Register(dom, StageName.ToneMapping, "default", _ => new Isp.Stages.ToneMappingLinearStage("default"));
            StageRegistry.Register(dom, StageName.ToneMapping, "linear", _ => new Isp.Stages.ToneMappingLinearStage("linear"));
        }
        // BayerFloat block (pipeline index 5 of the level-1 fusion cache): the same kernels on the float
        // source; slot 3 = the float LinearizeAndColorScale delegate (normalization + WB scale of the fused raw-DN Bayer).
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Placeholder, "default", _ => new Isp.Stages.BayerFloatLinearizeStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.CrossTalkCorrection, "ir_correction", _ => new Isp.Stages.CrossTalkStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Demosaicking, "light_v1", _ => new Isp.Stages.DemosaicLightV1Stage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Demosaicking, "collapse2", _ => new Isp.Stages.Demosaic2xCatmullStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Demosaicking, "collapse4", _ => new Isp.Stages.CollapseDemosaicStage(4));
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Demosaicking, "collapse8", _ => new Isp.Stages.CollapseDemosaicStage(8));
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.ColorNoiseReduction, "default", _ => new Isp.Stages.ColorNoiseReductionStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.AdaptiveDesaturation, "default", _ => new Isp.Stages.AdaptiveDesaturateStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.Denoising, "hybrid", _ => new Isp.Stages.DenoiseHybridStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.PostProcessing, "default", _ => new Isp.Stages.PostProcessingStage());
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.LensShading, "default", _ => new Isp.Stages.LensShadingStage(false));
        StageRegistry.Register(PayloadDomain.BayerFloat, StageName.LensShading, "inverse", _ => new Isp.Stages.LensShadingStage(true));
        // Color block (vec4 → vec4; the mono branch of the level-1 fusion cache): slot 2 = the color scale
        // (÷ neutral), then the shared bodies of CNR, AdaptiveDesat, Denoise and PostProcessing.
        StageRegistry.Register(PayloadDomain.Color, StageName.Placeholder, "default", _ => new Isp.ColorScaleStage());
        StageRegistry.Register(PayloadDomain.Color, StageName.ColorNoiseReduction, "default", _ => new Isp.Stages.ColorNoiseReductionStage());
        StageRegistry.Register(PayloadDomain.Color, StageName.AdaptiveDesaturation, "default", _ => new Isp.Stages.AdaptiveDesaturateStage());
        StageRegistry.Register(PayloadDomain.Color, StageName.Denoising, "hybrid", _ => new Isp.Stages.DenoiseHybridStage());
        StageRegistry.Register(PayloadDomain.Color, StageName.PostProcessing, "default", _ => new Isp.Stages.PostProcessingStage());
        // display / output ISP: the Color-domain slots that Lumen's `setInputDataStream` populates —
        // 2 (÷ neutral, already above), 10 ColorCorrection `optimized`, 11 PostProcessing, 12 LensShading `inverse`,
        // 13 ToneAdjust `laplacian_pyramid`, 14 ContrastAdjust `default`, 15 ToneMapping `light_v1`/`light_v2`/….
        StageRegistry.Register(PayloadDomain.Color, StageName.LensShading, "default", _ => new Isp.Stages.LensShadingStage(false));
        StageRegistry.Register(PayloadDomain.Color, StageName.LensShading, "inverse", _ => new Isp.Stages.LensShadingStage(true));
        StageRegistry.Register(PayloadDomain.Color, StageName.ToneAdjust, "laplacian_pyramid", _ => new Isp.Stages.ToneAdjustLaplacianStage());
        StageRegistry.Register(PayloadDomain.Color, StageName.ContrastAdjust, "default", _ => new Isp.Stages.ContrastAdjustStage());
        foreach (var dom in new[] { PayloadDomain.Bayer, PayloadDomain.BayerFloat, PayloadDomain.Color })
        {
            StageRegistry.Register(dom, StageName.ColorCorrection, "optimized", _ => new Isp.Stages.ColorCorrectionOptimizedStage());
            foreach (var tm in new[] { "acr", "light_v1", "light_v1_lowlight", "light_v2" })
                StageRegistry.Register(dom, StageName.ToneMapping, tm, _ => new Isp.Stages.ToneMappingAcrStage(tm));
        }
    }
}
