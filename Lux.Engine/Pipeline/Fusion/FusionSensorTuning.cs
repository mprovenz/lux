namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// `FusionCacheBase` ctor `1805018e0` step 3 (spec `a4ce3d1abcbdfdc45.md` §2.1): the per-sensor tuning row
/// {gain threshold, noise scale (+0xa4), nlm_denoiser.chroma_boost (+0xa8), bilateral_denoiser.chroma_boost (+0xac),
/// tone_mapping.grain_power (+0xb0), +0xb4 (MonoFusion only)}. Table choice: `stream+0x14` (data_scale (0.5,0.5)) → T0; else
/// `FUN_18050cbd0(profile)` = (`FUN_18050c640(profile) == 1`) selects M1 = {2 → T1a, 4 → T1b}, otherwise M2 = {2 → T2a, 4 → T2b};
/// key = `DAT_1806eca60[sensorType−1]` = {2,2,2,4,4} for AR835 / AR1335 / AR1335_MONO / IMX386 / IMX386_MONO. Row: `iso = analogGain·100`
/// (`DAT_18068b2d8`), the first row in table order with `row[0] ≤ iso`, else row 0. `FUN_18050c640`: profiles 1, 2 → 1; 0 → 4;
/// 3 → `byte(profile+4) ^ 1` (1 when level 0 = 2× ResAmp is enabled → 0 → M2 on the Lumen desktop).
/// </summary>
public static class FusionSensorTuning
{
    public readonly record struct Row(float Threshold, float NoiseScale, float NlmChroma, float BilateralChroma, float GrainPower, float Extra);

    static readonly Row[] T0 = { new(775, 1, 1, 1, 0.60f, 1), new(500, 1, 1, 1, 0.70f, 1), new(400, 1, 1, 1, 0.80f, 1), new(200, 1, 1, 1, 0.85f, 1), new(100, 1, 1, 1, 0.95f, 1) };
    static readonly Row[] T1a = { new(775, 1.7f, 3.0f, 3.0f, 0.60f, 1), new(500, 1.6f, 2.5f, 2.5f, 0.70f, 1), new(400, 1.5f, 2.5f, 2.5f, 0.80f, 1), new(200, 1.3f, 2.0f, 2.0f, 0.85f, 1), new(100, 1.2f, 2.0f, 2.0f, 0.90f, 1) };
    static readonly Row[] T1b = { new(1600, 1.8f, 4.5f, 4.5f, 0.80f, 8), new(800, 1.7f, 4.0f, 4.0f, 0.75f, 8), new(400, 1.25f, 3.5f, 3.5f, 0.70f, 8), new(200, 0.9f, 3.0f, 3.0f, 0.65f, 8), new(100, 0.7f, 3.0f, 3.0f, 0.70f, 8) };
    static readonly Row[] T2a = { new(775, 1.0f, 3.0f, 3.0f, 0.60f, 1), new(500, 1.0f, 2.5f, 2.5f, 0.70f, 1), new(400, 1.0f, 2.5f, 2.5f, 0.80f, 1), new(200, 1.0f, 2.0f, 2.0f, 0.85f, 1), new(100, 1.0f, 2.0f, 2.0f, 0.90f, 1) };
    static readonly Row[] T2b = { new(1600, 1.7f, 4.5f, 4.5f, 0.80f, 8), new(800, 1.35f, 4.0f, 4.0f, 0.75f, 8), new(400, 1.2f, 3.5f, 3.5f, 0.70f, 8), new(200, 0.75f, 3.0f, 3.0f, 0.70f, 8), new(100, 0.5f, 3.0f, 3.0f, 0.70f, 8) };

    /// <summary>`DAT_1806eca60[sensorType−1]`: sensor type 1..5 (AR835, AR1335, AR1335_MONO, IMX386, IMX386_MONO) → table key 2/4.</summary>
    public static int SensorKey(int sensorType) => sensorType is >= 1 and <= 5 ? (sensorType <= 3 ? 2 : 4) : throw new NotSupportedException($"Unhandled sensor type {sensorType}");

    /// <summary>`FUN_18050c640(profile)`: 1 for profiles 1/2, 4 for profile 0, `resAmpEnabled ^ 1` for profile 3.</summary>
    public static int ProfileCode(int profile, bool resAmpEnabled) => profile switch { 1 or 2 => 1, 0 => 4, 3 => resAmpEnabled ? 0 : 1, _ => throw new ArgumentOutOfRangeException(nameof(profile)) };

    public static Row Select(int profile, bool resAmpEnabled, bool halfScaleStream, int sensorType, float analogGain)
    {
        Row[] table;
        if (halfScaleStream) table = T0;
        else
        {
            int key = SensorKey(sensorType);
            bool m1 = ProfileCode(profile, resAmpEnabled) == 1;
            table = key == 2 ? (m1 ? T1a : T2a) : (m1 ? T1b : T2b);   // "Missing tuning parameters for sensor type" cannot happen for keys 2/4
        }
        float iso = analogGain * 100.0f;
        foreach (var r in table) if (r.Threshold <= iso) return r;
        return table[0];
    }

    /// <summary>`FUN_18050cbf0(profile, analogGain)` (`FusionCacheBase+0xe8` halo): gain ≤ 2 → 17, ≤ 4 → 33, ≤ 6 → 65, else 129.</summary>
    public static int Halo(float analogGain) => analogGain <= 2.0f ? 0x11 : analogGain <= 4.0f ? 0x21 : analogGain <= 6.0f ? 0x41 : 0x81;
}
