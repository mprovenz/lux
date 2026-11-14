using Ltpb;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// Per-sensor / per-ISO module-ISP tuning overrides. Lumen keeps a static table: a map from an int key to a vector of rows.
/// Lumen looks up the row per frame and applies it to the tuning tree as the first step of the per-(sensor, level) tuning
/// (<see cref="ModuleIspTuning.ApplyLevelParameters"/>), so it runs before the per-(sensor, level) table for every config
/// level 0..5. Lumen's DirectRenderer ISP setup also calls it directly.
/// Key = by sensor type {2,2,2,4,4} (AR835 / AR1335 / AR1335_MONO → 2, IMX386 / IMX386_MONO → 4;
/// "Unknown sensor type" / "No tuning parameters for sensor type" otherwise). Row: `iso = (int)truncf(analogGain · 100.0f)`,
/// index = last i ≥ 1 with `row[i].Iso ≤ iso`, else (row[0].Iso > iso ? last row : row 0).
/// </summary>
public static class IsoTuning
{
    /// <summary>One row of the table; the field order is the order in Lumen's row.</summary>
    public readonly record struct Row(
        int Iso,
        int NlmWindow, int NlmPatch, int NlmStep, float NlmChroma, bool NlmFastSearch, int NlmPyramid, float NlmMinLumaStd,   // → nlm_denoiser.*
        int BilWindow, float BilChroma, int BilPyramid, float BilGradient, float BilMinLumaStd,                              // → bilateral_denoiser.* (denoising.type ≠ bilateral_420)
        int Bil420Window, float Bil420Chroma, int Bil420Pyramid, float Bil420Gradient, float Bil420MinLumaStd,               // → bilateral_denoiser.* (denoising.type == bilateral_420)
        float GrainPower, float GrainSigma,                                                   // → tone_mapping.grain_power (× scale), grain_sigma
        float Unused50, float Unused54,                                                       // Lumen never reads these two fields
        float ShadowCutoff, float HighlightCutoff,                                            // → adaptive_desaturation.shadow_cutoff / highlight_cutoff
        float ThresholdMultiplier, float ThresholdMultiplier420);                             // → denoising.threshold_multiplier (type ≠ / == bilateral_420)

    /// <summary>Key 2 (AR835, AR1335, AR1335_MONO).</summary>
    static readonly Row[] K2 =
    {
        new(100, 5, 5, 2, 2.0f, true, 5, 0.0025f, 5, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 2.5e-06f, 0.0025f, 0.5f,  0.5f,   0f, 1f, 0.01f,  0.8f, 1.4f, 1.4f),
        new(200, 5, 5, 2, 2.0f, true, 5, 0.0025f, 5, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 5e-06f,   0.0025f, 0.4f,  0.525f, 0f, 1f, 0.01f,  0.8f, 1.5f, 1.9f),
        new(400, 7, 5, 2, 2.0f, true, 5, 0.0025f, 7, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.55f,  0f, 1f, 0.01f,  0.8f, 1.6f, 2.2f),
        new(625, 7, 5, 2, 2.0f, true, 5, 0.0025f, 7, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.3f,  0.575f, 0f, 1f, 0.015f, 0.8f, 1.8f, 1.8f),
        new(775, 9, 7, 2, 1.5f, true, 5, 0.0025f, 9, 2.0f, 5, 0f, 0.0025f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f,  0.8f, 1.4f, 1.4f),
    };

    /// <summary>Key 4 (IMX386, IMX386_MONO). Differs from key 2 in the
    /// nlm/bilateral min_luma_std (0), the ISO breakpoints 800/1600 and the highest row's cutoffs.</summary>
    static readonly Row[] K4 =
    {
        new(100,  5, 5, 2, 2.0f, true, 5, 0f, 5, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 2.5e-06f, 0.0025f, 0.5f,  0.5f,   0f, 1f, 0.01f, 0.8f, 1.4f, 1.4f),
        new(200,  5, 5, 2, 2.0f, true, 5, 0f, 5, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 5e-06f,   0.0025f, 0.4f,  0.525f, 0f, 1f, 0.01f, 0.8f, 1.5f, 1.9f),
        new(400,  7, 5, 2, 2.0f, true, 5, 0f, 7, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.55f,  0f, 1f, 0.01f, 0.8f, 1.6f, 2.2f),
        new(800,  9, 7, 2, 1.5f, true, 5, 0f, 9, 2.0f, 5, 0f, 0f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f, 0.8f, 1.4f, 1.4f),
        new(1600, 9, 7, 2, 1.5f, true, 5, 0f, 9, 2.0f, 5, 0f, 0f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f, 0.8f, 1.4f, 1.4f),
    };

    public const float IsoPerGain = 100.0f;
    public const int DenoiseBilateral420 = 5;               // StageEnums["denoising"]["bilateral_420"]
    static readonly float[] GrainPowerScale = { 1.0f, 0.25f };   // index = ((type & ~1) == 4) (bilateral / bilateral_420)

    /// <summary>The table key per sensor type, indexed by `sensorType − 1`.</summary>
    public static int SensorKey(SensorType t) => t switch
    {
        SensorType.SensorAr835 or SensorType.SensorAr1335 or SensorType.SensorAr1335Mono => 2,
        SensorType.SensorImx386 or SensorType.SensorImx386Mono => 4,
        _ => throw new InvalidOperationException("Unknown sensor type"),
    };

    /// <summary>Table by sensor key, row by `(int)(analogGain · 100)` — see the class summary.</summary>
    public static Row Select(SensorType sensor, float analogGain)
    {
        var rows = SensorKey(sensor) == 2 ? K2 : K4;
        int iso = (int)(analogGain * IsoPerGain);            // single-precision product, truncated
        int idx = 0;
        if (rows[0].Iso > iso) idx = rows.Length - 1;
        for (int i = 1; i < rows.Length; i++) if (rows[i].Iso <= iso) idx = i;
        return rows[idx];
    }

    /// <summary>Writes the selected row into the tuning tree.</summary>
    public static void Apply(Tuning t, SensorType sensor, float analogGain)
    {
        int type = StageEnums.Enum("denoising", t.Str("denoising.type"));   // the denoising type as its enum value
        var r = Select(sensor, analogGain);
        t.Set("nlm_denoiser.fast_search", r.NlmFastSearch);                  // bool node
        t.Set("nlm_denoiser.patch_size", (double)r.NlmPatch);
        t.Set("nlm_denoiser.step_size", (double)r.NlmStep);
        t.Set("nlm_denoiser.window_size", (double)r.NlmWindow);
        t.Set("nlm_denoiser.chroma_boost", (double)r.NlmChroma);
        t.Set("nlm_denoiser.pyramid_size", (double)r.NlmPyramid);
        t.Set("nlm_denoiser.min_luma_std", (double)r.NlmMinLumaStd);
        float scale = GrainPowerScale[(type & ~1) == 4 ? 1 : 0];
        t.Set("tone_mapping.grain_power", (double)(r.GrainPower * scale));
        t.Set("tone_mapping.grain_sigma", (double)r.GrainSigma);
        t.Set("adaptive_desaturation.shadow_cutoff", (double)r.ShadowCutoff);
        t.Set("adaptive_desaturation.highlight_cutoff", (double)r.HighlightCutoff);
        float thr;
        if (type == DenoiseBilateral420)
        {
            t.Set("bilateral_denoiser.window_size", (double)r.Bil420Window);
            t.Set("bilateral_denoiser.chroma_boost", (double)r.Bil420Chroma);
            t.Set("bilateral_denoiser.pyramid_size", (double)r.Bil420Pyramid);
            t.Set("bilateral_denoiser.gradient_threshold", (double)r.Bil420Gradient);
            t.Set("bilateral_denoiser.min_luma_std", (double)r.Bil420MinLumaStd);
            thr = r.ThresholdMultiplier420;
        }
        else
        {
            t.Set("bilateral_denoiser.window_size", (double)r.BilWindow);
            t.Set("bilateral_denoiser.chroma_boost", (double)r.BilChroma);
            t.Set("bilateral_denoiser.pyramid_size", (double)r.BilPyramid);
            t.Set("bilateral_denoiser.gradient_threshold", (double)r.BilGradient);
            t.Set("bilateral_denoiser.min_luma_std", (double)r.BilMinLumaStd);
            thr = r.ThresholdMultiplier;
        }
        t.Set("denoising.threshold_multiplier", (double)thr);
    }
}
