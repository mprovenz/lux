using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>What the module-ISP tuning needs to know about one captured frame (`CapturedImage` fields read by
/// `FUN_18050cc30` / `FUN_180510410`).</summary>
public sealed record ModuleFrameInfo(
    SensorType Sensor,                       // CapturedImage +0x100, from hw_info.camera[id].sensor
    bool IsColour,                           // bayer red position ≥ 0 (mono modules have −1)
    float DataScaleX, float DataScaleY,      // CapturedImage +0x124/+0x128 = sensor_data_surface.data_scale, (1,1) when zero
    bool HasHotPixelLeakageCalibration,      // owner module-info Optional (+0xa0 flag) — assumed = hot_pixel_map present [?]
    int StackedFrameCount,                   // frames of this camera id in the stream (FUN_180510410 √N rule); 1 for single captures
    float ExposureRatio,                     // FUN_180126860(img) (SoT §3.7): ViewPreferences gain·time / THIS module gain·exposure; level-1 grain rule
    float AnalogGain,                        // CapturedImage +0x40 = sensor_analog_gain → noise-model ISO lookup
    SensorNoise? Noise)                      // lt::Sensor tables for this sensor type (header sensor_data)
{
    /// <summary>Per-frame black level (Lumen `CapturedImage+0xb4`, `CaptureState.EstimateFrameBlack`); NaN until the frame is loaded.</summary>
    public float FrameBlack { get; init; } = float.NaN;

    /// <summary>`FUN_180112250(FUN_180125620(cap))` = frames per stack (`LriFile.StackFrames`): 1 for an ordinary capture, 4 for a
    /// stacked one. `FUN_18050cc30` L597 keys the hot-pixel / highlight-restore block on it, and `lt::StackFusion` on ≥ 2.</summary>
    public int NStack { get; init; } = 1;

    public static ModuleFrameInfo From(LriFile lri, string moduleName) => From(lri, lri.Modules[moduleName]);

    /// <summary>The facts of one specific module block — the frame selector a stacked capture needs. `sensor_analog_gain` and
    /// `sensor_exposure` are per frame in Lumen (`CapturedImage+0x40`), so each frame of a stack gets its own noise tables.</summary>
    public static ModuleFrameInfo From(LriFile lri, LriFile.ModuleRef mref)
    {
        var h = lri.Header;
        var m = mref.Module;
        var sensor = SensorType.SensorUnknown;
        foreach (var c in h.HwInfo.Camera) if (c.Id == m.Id) sensor = c.Sensor;
        bool colour = m.SensorBayerRedOverride is null || (m.SensorBayerRedOverride.X | m.SensorBayerRedOverride.Y) >= 0;
        float sx = 1f, sy = 1f;
        if (m.SensorDataSurface.DataScale is not null && !(m.SensorDataSurface.DataScale.X == 0f && m.SensorDataSurface.DataScale.Y == 0f))
        { sx = m.SensorDataSurface.DataScale.X; sy = m.SensorDataSurface.DataScale.Y; }
        bool hp = false;
        foreach (var cal in Calibration.ForModule(h, m.Id)) if (cal.HotPixelMap is not null) hp = true;
        // FUN_180510410 L166–221: when this module is in the reference camera's group, N = number of captures (frame 0, with data) in that group
        // (5 for the L16 wide group → threshold_multiplier ×= √5); other groups keep 1. Verified via the live hybrid-denoiser threshold (hn = 0.31305).
        static int Group(int id) => id <= 4 ? 0 : id <= 9 ? 1 : 2;
        int refGroup = Group((int)h.ImageReferenceCamera), n = 1;
        if (Group((int)m.Id) == refGroup) { n = 0; foreach (var kv in lri.Modules) if (Group((int)kv.Value.Module.Id) == refGroup) n++; }
        return new ModuleFrameInfo(sensor, colour, sx, sy, hp, n, lri.ExposureRatioOf(m), m.SensorAnalogGain, SensorNoise.FromHeader(h, sensor)) { NStack = lri.StackFrames };   // FUN_180126860 is per CapturedImage (own gain·exposure)
    }
    /// <summary>`FUN_180126ae0`: data_scale == (0.5, 0.5) (`DAT_180682404`).</summary>
    public bool IsHalfScale => DataScaleX == 0.5f && DataScaleY == 0.5f;
}

/// <summary>
/// Port of `FUN_18050cc30` (module-ISP tuning per config level, SoT §4.2) and `FUN_180510410` (the per-(sensor, level)
/// parameter table + stacking/grain rules). Config levels: 0–4 = ReferenceImageCache levels 0..3 use 0,2,3,4;
/// SourceImageCache uses 1; FusionCacheBayer uses 5.
/// </summary>
public static class ModuleIspTuning
{
    /// <summary>`DAT_1806edcb0`: SensorType 1..5 → table key (AR835/AR1335/AR1335_MONO → 2, IMX386/IMX386_MONO → 4).</summary>
    public static int SensorCode(SensorType t) => t switch
    {
        SensorType.SensorAr835 or SensorType.SensorAr1335 or SensorType.SensorAr1335Mono => 2,
        SensorType.SensorImx386 or SensorType.SensorImx386Mono => 4,
        _ => throw new InvalidOperationException("Unhandled sensor type"),
    };

    /// <summary>`DAT_180838b10`, built by the static initializer `FUN_180512300` L1496–2100: (sensor code, config level) →
    /// ordered (key, float) assignments applied by `FUN_180510410` after the type selection.</summary>
    public static readonly IReadOnlyDictionary<(int SensorCode, int Level), (string Key, float Value)[]> LevelParameters =
        new Dictionary<(int, int), (string, float)[]>
        {
            [(2, 0)] = new[] { ("tone_mapping.sharpening", 0f), ("tone_mapping.grain_power", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
            [(2, 1)] = new[] { ("tone_mapping.sharpening", 0.5f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
            [(2, 2)] = new[] { ("tone_mapping.sharpening", 1.5f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.5f) },
            [(2, 3)] = new[] { ("tone_mapping.sharpening", 1.5f), ("tone_mapping.sharpening_scale", 0.5f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.25f) },
            [(2, 4)] = new[] { ("tone_mapping.sharpening", 1.5f), ("tone_mapping.sharpening_scale", 0.25f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.125f) },
            [(2, 5)] = new[] { ("tone_mapping.sharpening", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
            [(4, 0)] = new[] { ("tone_mapping.sharpening", 0f), ("tone_mapping.grain_power", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
            [(4, 1)] = new[] { ("tone_mapping.sharpening", 0.5f), ("tone_mapping.grain_power", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
            [(4, 2)] = new[] { ("tone_mapping.sharpening", 1.5f), ("denoising.threshold_multiplier", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.5f) },
            [(4, 3)] = new[] { ("tone_mapping.sharpening", 1.5f), ("tone_mapping.sharpening_scale", 0.5f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.25f) },
            [(4, 4)] = new[] { ("tone_mapping.sharpening", 1.5f), ("tone_mapping.sharpening_scale", 0.25f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("denoising.threshold_multiplier", 1f), ("pipeline.parameter_scale", 0.125f) },
            [(4, 5)] = new[] { ("tone_mapping.sharpening", 1f), ("color_noise_reduction.color_denoise_multiplier", 1f), ("pipeline.parameter_scale", 1f) },
        };

    public const float HalfScaleThresholdFactor = 0.5f;   // DAT_18067ec68 (double 0.5)
    public const float GrainPowerFloor = 0.5f;            // DAT_180682404

    /// <summary>The module-ISP tuning for one config level, starting from Lumen's defaults (`FUN_1803d61c0`).</summary>
    public static Tuning Build(int configLevel, RendererProfile profile, ModuleFrameInfo f, float neutralTemp, float neutralTint, Tuning? baseTuning = null)
    {
        var t = (baseTuning ?? Tuning.LumenDefaults()).Clone();
        if (configLevel == 5)
        {
            // FusionCacheBayer path (`FUN_18050cc30` L118–390): profile demosaic, lens shading default, ir crosstalk,
            // manual_color with neutral (1,1,1), highlight/hot-pixel/output/colour-correction none, adaptive-desat default,
            // profile denoise/CNR, bayer_phase_fix default on the Desktop profile when data_scale == 0.5.
            t.Set("demosaicking.type", RendererProfiles.DemosaicType(profile));
            t.Set("lens_shading.type", "default");
            t.Set("cross_talk_correction.type", "ir_correction");
            t.Set("auto_white_balance.type", "manual_color");
            t.Set("auto_white_balance.neutral_color", new[] { 1.0, 1.0, 1.0 });
            t.Set("highlight_restore.type", "none").Set("hot_pixel_removal.type", "none").Set("hot_pixel_leakage_removal.type", "none");
            t.Set("output.color_space", "none").Set("color_correction.type", "none");
            t.Set("adaptive_desaturation.type", "default");
            t.Set("denoising.type", RendererProfiles.DenoiseType(profile));
            t.Set("color_noise_reduction.type", RendererProfiles.ColorNoiseReductionType(profile));
            if (RendererProfiles.IsDesktop(profile) && f.IsHalfScale) t.Set("bayer_phase_fix.type", "default");
        }
        if (configLevel != 5)   // level 5 jumps straight to FUN_180510410 (`goto LAB_18050e8d0` at L390; spec a4ce3d1abcbdfdc45 §5.4): no common part
        {
        // common part (L397–700)
        t.Set("auto_white_balance.type", "manual_temp");
        t.Set("auto_white_balance.neutral_temp", (double)neutralTemp);
        t.Set("auto_white_balance.neutral_tint", (double)neutralTint);
        t.Set("output.color_space", "none");
        t.Set("color_correction.type", "none");
        t.Set("tone_mapping.type", "none");
        t.Set("color_noise_reduction.type", RendererProfiles.ColorNoiseReductionType(profile));
        t.Set("adaptive_desaturation.type", "default");
        t.Set("lens_shading.type", "default");
        t.Set("cross_talk_correction.type", "ir_correction");   // DAT_180838af0
        t.Set("denoising.type", RendererProfiles.DenoiseType(profile));
        // L597: `FUN_180112250(FUN_180125620(cap)) < 2` — the frames-per-stack count, NOT the sensor colour (settled from the
        // decomp 2026-08-27; the earlier `[?]` reading as "colour sensor" agrees on every single-frame colour capture, which is
        // every capture verified so far). A STACKED capture takes the else branch: `lt::StackFusion` already ran
        // ImagePatchHotPixels + RestoreHighlightsBayer per frame (`FUN_1802067f0`), so the module ISP must not repeat them.
        if (f.NStack < 2)
        {
            if (f.HasHotPixelLeakageCalibration) t.Set("hot_pixel_leakage_removal.type", "default");
            t.Set("hot_pixel_removal.type", "default");
            t.Set("highlight_restore.type", "default");
        }
        else
        {
            t.Set("hot_pixel_removal.type", "none");
            t.Set("highlight_restore.type", "none");
        }
        if (f.IsHalfScale) t.Set("bayer_phase_fix.type", "default");   // FUN_180126ae0
        switch (configLevel)   // L719–792
        {
            case 0: t.Set("demosaicking.type", RendererProfiles.DemosaicType(profile)); t.Set("denoising.type", "none"); break;
            case 1: t.Set("demosaicking.type", RendererProfiles.DemosaicType(profile)); break;
            case 2: t.Set("demosaicking.type", "collapse2"); break;
            case 3: t.Set("demosaicking.type", "collapse4"); break;
            case 4: t.Set("demosaicking.type", "collapse8"); break;
            default: break;   // 5: profile demosaic already set above
        }
        }
        ApplyLevelParameters(t, configLevel, f);
        if (configLevel == 5)
        {
            // FusionCacheBase ctor 1805018e0 step 4 — AFTER FUN_18050cc30 (i.e. after the ISO row + level-5 table above): the
            // per-sensor fusion tuning row's overrides (spec a4ce3d1abcbdfdc45 §2.1) replace the ISO row's chroma_boost / grain_power.
            var row = global::Lux.Engine.Pipeline.BayerFusion.FusionSensorTuning.Select((int)profile, resAmpEnabled: RendererProfiles.IsDesktop(profile), f.IsHalfScale, (int)f.Sensor, f.AnalogGain);
            t.Set("nlm_denoiser.chroma_boost", (double)row.NlmChroma);
            t.Set("tone_mapping.grain_power", (double)row.GrainPower);
            t.Set("bilateral_denoiser.chroma_boost", (double)row.BilateralChroma);
        }
        return t;
    }

    /// <summary>`FUN_180510410(tuning, level, frame)` in statement order: (1) `FUN_1803de650` = the per-sensor / per-ISO row
    /// (<see cref="IsoTuning"/>, spec a-iso-tuning.md: nlm/bilateral denoiser parameters, grain_power/grain_sigma, adaptive-desat
    /// cutoffs, denoising.threshold_multiplier); (2) the (sensor code, level) table; (3) ×0.5 threshold for half-scale surfaces;
    /// (4) level 5 returns here; (5) the level-1 grain rule `max(0.5, √exposureRatio)`; (6) `denoising.threshold_multiplier ×= √(stacked frames)`.</summary>
    public static void ApplyLevelParameters(Tuning t, int configLevel, ModuleFrameInfo f)
    {
        IsoTuning.Apply(t, f.Sensor, f.AnalogGain);   // L0x180510448 — the grain_power 0.5/0.5 (gain 1) vs 0.4/0.525 (gain 3.6–3.9) readings come from here
        if (LevelParameters.TryGetValue((SensorCode(f.Sensor), configLevel), out var ps))
            foreach (var (k, v) in ps) t.Set(k, (double)v);
        if (f.IsHalfScale) t.Set("denoising.threshold_multiplier", t.Num("denoising.threshold_multiplier") * HalfScaleThresholdFactor);
        if (configLevel == 5) return;   // `if (param_2 == 5) return;` — pipeline 5 keeps the ISO row's threshold_multiplier (its level table has no such entry), no grain rule, no √N
        if (configLevel == 1)
        {   // asm 0x18051067c–0x18051069c: rsqrtss + one Newton step (−0.5·r·((x·r)·r − 3)) = 1/√x, then maxss 0.5 — NOT √x
            float g = global::Lux.Engine.Pipeline.Registration.Homography.Rsqrt(f.ExposureRatio);
            if (g <= GrainPowerFloor) g = GrainPowerFloor;
            t.Set("tone_mapping.grain_power", (double)g);
        }
        float n = MathF.Sqrt(f.StackedFrameCount);
        t.Set("denoising.threshold_multiplier", t.Num("denoising.threshold_multiplier") * (double)n);
    }
}
