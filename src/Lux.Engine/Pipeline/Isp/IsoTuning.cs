using Ltpb;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// Per-sensor / per-ISO module-ISP tuning overrides (spec `a-iso-tuning.md`). The table
/// `DAT_180836ca8` = map&lt;int key, vector&lt;Row (0x68 B)&gt;&gt; is built by the static initializer `FUN_180425bb0`
/// (rows from the .rdata constant pool at 0x1806dc860..0x1806dc95f + immediates), looked up by `FUN_180424890(out, frame)`
/// and applied to the tuning tree by `FUN_1803de650(tuning, frame)` — the first statement of `FUN_180510410`, so it runs
/// before the per-(sensor, level) table for every config level 0..5 (also called directly by the DirectRenderer ISP setup
/// `FUN_1804b6140` L0x1804b6d90 and by `FUN_18053fea0`).
/// Key = `DAT_1806dcda0[sensorType−1]` = {2,2,2,4,4} (AR835 / AR1335 / AR1335_MONO → 2, IMX386 / IMX386_MONO → 4;
/// "Unknown sensor type" / "No tuning parameters for sensor type" otherwise). Row: `iso = (int)truncf(analogGain · 100.0f)`
/// (`DAT_18068b2d8`), index = last i ≥ 1 with `row[i].Iso ≤ iso`, else (row[0].Iso > iso ? last row : row 0).
/// </summary>
public static class IsoTuning
{
    /// <summary>One 0x68-byte row; field order = memory order (offsets in the spec §1).</summary>
    public readonly record struct Row(
        int Iso,                                                                              // +0x00
        int NlmWindow, int NlmPatch, int NlmStep, float NlmChroma, bool NlmFastSearch, int NlmPyramid, float NlmMinLumaStd,   // +0x04..+0x1c → nlm_denoiser.*
        int BilWindow, float BilChroma, int BilPyramid, float BilGradient, float BilMinLumaStd,                              // +0x20..+0x30 → bilateral_denoiser.* (denoising.type ≠ bilateral_420)
        int Bil420Window, float Bil420Chroma, int Bil420Pyramid, float Bil420Gradient, float Bil420MinLumaStd,               // +0x34..+0x44 → bilateral_denoiser.* (denoising.type == bilateral_420)
        float GrainPower, float GrainSigma,                                                   // +0x48/+0x4c → tone_mapping.grain_power (× scale), grain_sigma
        float Unused50, float Unused54,                                                       // +0x50/+0x54 — not read by FUN_1803de650 (the only consumer)
        float ShadowCutoff, float HighlightCutoff,                                            // +0x58/+0x5c → adaptive_desaturation.shadow_cutoff / highlight_cutoff
        float ThresholdMultiplier, float ThresholdMultiplier420);                             // +0x60/+0x64 → denoising.threshold_multiplier (type ≠ / == bilateral_420)

    /// <summary>Key 2 (AR835, AR1335, AR1335_MONO): `FUN_180425bb0` L1046–1176 (local_138).</summary>
    static readonly Row[] K2 =
    {
        new(100, 5, 5, 2, 2.0f, true, 5, 0.0025f, 5, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 2.5e-06f, 0.0025f, 0.5f,  0.5f,   0f, 1f, 0.01f,  0.8f, 1.4f, 1.4f),
        new(200, 5, 5, 2, 2.0f, true, 5, 0.0025f, 5, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 5e-06f,   0.0025f, 0.4f,  0.525f, 0f, 1f, 0.01f,  0.8f, 1.5f, 1.9f),
        new(400, 7, 5, 2, 2.0f, true, 5, 0.0025f, 7, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.55f,  0f, 1f, 0.01f,  0.8f, 1.6f, 2.2f),
        new(625, 7, 5, 2, 2.0f, true, 5, 0.0025f, 7, 2.0f, 5, 0f, 0.0025f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.3f,  0.575f, 0f, 1f, 0.015f, 0.8f, 1.8f, 1.8f),
        new(775, 9, 7, 2, 1.5f, true, 5, 0.0025f, 9, 2.0f, 5, 0f, 0.0025f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f,  0.8f, 1.4f, 1.4f),
    };

    /// <summary>Key 4 (IMX386, IMX386_MONO): `FUN_180425bb0` L1229–1346 (local_118). Differs from key 2 in the
    /// nlm/bilateral min_luma_std (0), the ISO breakpoints 800/1600 and the highest row's cutoffs.</summary>
    static readonly Row[] K4 =
    {
        new(100,  5, 5, 2, 2.0f, true, 5, 0f, 5, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 2.5e-06f, 0.0025f, 0.5f,  0.5f,   0f, 1f, 0.01f, 0.8f, 1.4f, 1.4f),
        new(200,  5, 5, 2, 2.0f, true, 5, 0f, 5, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 5e-06f,   0.0025f, 0.4f,  0.525f, 0f, 1f, 0.01f, 0.8f, 1.5f, 1.9f),
        new(400,  7, 5, 2, 2.0f, true, 5, 0f, 7, 2.0f, 5, 0f, 0f, 5, 2.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.55f,  0f, 1f, 0.01f, 0.8f, 1.6f, 2.2f),
        new(800,  9, 7, 2, 1.5f, true, 5, 0f, 9, 2.0f, 5, 0f, 0f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f, 0.8f, 1.4f, 1.4f),
        new(1600, 9, 7, 2, 1.5f, true, 5, 0f, 9, 2.0f, 5, 0f, 0f, 5, 4.0f, 3, 1.2e-05f, 0.0025f, 0.35f, 0.575f, 0f, 1f, 0.02f, 0.8f, 1.4f, 1.4f),
    };

    public const float IsoPerGain = 100.0f;                 // DAT_18068b2d8
    public const int DenoiseBilateral420 = 5;               // StageEnums["denoising"]["bilateral_420"] (map DAT_1808369c8)
    static readonly float[] GrainPowerScale = { 1.0f, 0.25f };   // DAT_1806d6988[2], index = ((type & ~1) == 4) (bilateral / bilateral_420)

    /// <summary>`DAT_1806dcda0[sensorType−1]`.</summary>
    public static int SensorKey(SensorType t) => t switch
    {
        SensorType.SensorAr835 or SensorType.SensorAr1335 or SensorType.SensorAr1335Mono => 2,
        SensorType.SensorImx386 or SensorType.SensorImx386Mono => 4,
        _ => throw new InvalidOperationException("Unknown sensor type"),
    };

    /// <summary>`FUN_180424890(out, frame)`: table by sensor key, row by `(int)(analogGain · 100)` — see the class summary.</summary>
    public static Row Select(SensorType sensor, float analogGain)
    {
        var rows = SensorKey(sensor) == 2 ? K2 : K4;
        int iso = (int)(analogGain * IsoPerGain);            // mulss + cvttss2si (truncation)
        int idx = 0;
        if (rows[0].Iso > iso) idx = rows.Length - 1;        // cmovg rax, rdi (rdi = count − 1)
        for (int i = 1; i < rows.Length; i++) if (rows[i].Iso <= iso) idx = i;   // cmovle
        return rows[idx];
    }

    /// <summary>`FUN_1803de650(tuning, frame)`: writes the selected row into the tuning tree (key globals resolved in the spec §3).</summary>
    public static void Apply(Tuning t, SensorType sensor, float analogGain)
    {
        int type = StageEnums.Enum("denoising", t.Str("denoising.type"));   // FUN_1803ddb70(tuning["denoising"]["type"])
        var r = Select(sensor, analogGain);
        t.Set("nlm_denoiser.fast_search", r.NlmFastSearch);                  // bool node (FUN_18000dd10(node, 0))
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
