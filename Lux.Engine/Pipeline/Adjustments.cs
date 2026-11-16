using System.Globalization;
using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline;

/// <summary>
/// Lumen's tone and color adjustments — the editor's seven sliders (exposure, contrast, temperature, tint, vibrance,
/// saturation, sharpening) and the three further renderer settings Lumen's interface never shows (shadows, highlights,
/// clarity) — as one plain value, with exactly what Lumen does with each.
///
/// <para>Each slider is a renderer `ParamFloat`; Lumen does no range check (the ranges are the editor's). Before every render the
/// renderer thread compares every setting with the value it last pushed (after loading, that value equals the loaded setting). Each
/// setting that differs is written into the output tuning of every display level by one mapper, and the display tile caches are
/// flushed. The mappers are pure functions of the setting, evaluated in float and stored as double; the pipeline reads
/// them back as float, so the stage parameter is exactly the float the mapper computed.</para>
///
/// <para>Only the 8-bit output path reads these: the display tiles and the JPG / JPG+depth exports
/// (fmt 0 / 4, which render through the same generator). DNG, PPM and HDR (fmt 1/2/3) take the linear tiles without the output
/// ISP, and the DNG's BaselineExposure and AsShotNeutral (the capture's) do not depend on any of them.</para>
/// </summary>
public sealed record Adjustments
{
    // ---- ranges: the editor's ImageNumberProperty limits (Lumen's `ImageProperties`); shadows, highlights and
    //      clarity are not in Lumen's editor at all — Lux gives them the ±100 of every other percentage slider (the mappers' 0.01 scale).
    public const float ExposureMin = -3f, ExposureMax = 3f;
    public const float TemperatureMin = 2000f, TemperatureMax = 20000f;
    public const float TintMin = -150f, TintMax = 150f;
    public const float PercentMin = -100f, PercentMax = 100f;

    /// <summary>ParamFloat 2, EV, [−3, 3]. `tone_mapping.ev_offset` = capture ev + value (slot 15, ToneMapping).</summary>
    public float Exposure { get; init; }
    /// <summary>ParamFloat 7, [−100, 100]. `contrast_adjust.value` = value · 0.04 (slot 14, ContrastAdjust).</summary>
    public float Contrast { get; init; }
    /// <summary>ParamFloat 3, kelvin, [2000, 20000]. Null = the capture's own (CCT of its AsShot white point, as Lumen loads it).</summary>
    public float? Temperature { get; init; }
    /// <summary>ParamFloat 4, [−150, 150]. Null = the capture's own.</summary>
    public float? Tint { get; init; }
    /// <summary>A renderer whose temperature or tint was ever moved (and perhaps moved back): Lumen then computes the neutral from the
    /// current pair even when it equals the loaded one (it compares with the pair it last pushed). That neutral differs from the
    /// AsShot neutral by a few ulps. False = a renderer that never saw a white-balance change (what a fresh export does).</summary>
    public bool WhiteBalancePushed { get; init; }
    /// <summary>ParamFloat 9, [−100, 100]. `tone_mapping.vibrance` = value · 0.01 + 1 (slot 11, PostProcessing).</summary>
    public float Vibrance { get; init; }
    /// <summary>ParamFloat 8, [−100, 100]. `tone_mapping.saturation` = value · 0.01 + 1 (slot 11, PostProcessing).</summary>
    public float Saturation { get; init; }
    /// <summary>ParamFloat 13, [−100, 100]. `tone_mapping.sharpening` = value / (value &gt; 0 ? 5 : 33) (slot 11, PostProcessing).
    /// Default 5 = what Lumen's renderers carry (`CIAPI::ApplyTuning(0)` writes it), i.e. sharpening 1.0.</summary>
    public float Sharpening { get; init; } = GuiSharpening;
    /// <summary>The response to <see cref="Sharpening"/>. False = Lumen's response at each value (the default; a Lumen session renders as in
    /// Lumen). True = the monotone response of Lux (<see cref="Isp.Stages.PostProcessingLumen.MonotoneSharpenSigmas"/>): a larger value never
    /// sharpens less, and a negative value softens at each image size.
    /// <para>The two responses give the same picture for 0 to 20 at each export level, for 20 to 100 at levels 0 and 1, and for negative
    /// values at level 0 (full size). They differ only where Lumen's response fails: above 20 at levels 2 to 4 (Lumen's Gaussian radii
    /// go below one pixel and the sharpening decreases), and below 0 at levels 1 to 4 (Lumen's blur radius decreases faster than the
    /// image).</para></summary>
    public bool MonotoneSharpening { get; init; }
    /// <summary>ParamFloat 5 (`shadow_boost`), [−100, 100]. `tone_adjust.lpyr_shadows` = value · (−0.01) + 1 (slot 13, ToneAdjust).</summary>
    public float Shadows { get; init; }
    /// <summary>ParamFloat 6 (`highlight_boost`), [−100, 100]. `tone_adjust.lpyr_highlights` = value · (−0.01) + 1 (slot 13).</summary>
    public float Highlights { get; init; }
    /// <summary>ParamFloat 10 (`clarity`), [−100, 100]. `tone_adjust.lpyr_clarity` = value · 0.01 (slot 13).</summary>
    public float Clarity { get; init; }
    /// <summary>Highlight recovery of Lux, [0, 100]. 0 = Lumen's tone curve, which clips each tone above its white. A larger value moves
    /// the white point up: the input that reaches white is <c>2^(value / 100)</c> times Lumen's, thus one stop more at 100. The curve does
    /// not change below the knee, so midtones and shadows stay as they are. The brightest tones also lose saturation toward white, in
    /// proportion to the value. See <see cref="Isp.HighlightRecovery"/>. The stage reads the value from
    /// <see cref="Isp.Stages.ToneMappingAcrStage.HighlightRecoveryKey"/>.</summary>
    public float HighlightRecovery { get; init; }
    /// <summary>Gamut compression of Lux. False = Lumen's rendering, which clips each channel of a color outside the output color space.
    /// True = such colors move to the gamut boundary and keep their hue and gradation. See <see cref="Color.GamutCompression"/>. The stage
    /// reads the setting from <see cref="Isp.Stages.ToneMappingAcrStage.GamutCompressKey"/>.</summary>
    public bool GamutCompress { get; init; }

    /// <summary>The value `CIAPI::ApplyTuning(0)` writes into ParamFloat 13 (`setProperty(0xd, 5.0f)`), which every Lumen renderer carries.</summary>
    public const float GuiSharpening = 5f;
    /// <summary>Lumen's state: everything at its loaded value, sharpening 5.</summary>
    public static readonly Adjustments Default = new();
    /// <summary>A renderer that never had `ApplyTuning` (sharpening 0) — the state of the PPM/headless renders.</summary>
    public static readonly Adjustments Unsharpened = new() { Sharpening = 0f };

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside its slider range (NaN included).</summary>
    public Adjustments Validate()
    {
        static void R(string n, float v, float lo, float hi) { if (!(v >= lo && v <= hi)) throw new ArgumentOutOfRangeException(n, v, $"{n} must be in [{lo.ToString(CultureInfo.InvariantCulture)}, {hi.ToString(CultureInfo.InvariantCulture)}]"); }
        R(nameof(Exposure), Exposure, ExposureMin, ExposureMax);
        R(nameof(Contrast), Contrast, PercentMin, PercentMax);
        if (Temperature is float t) R(nameof(Temperature), t, TemperatureMin, TemperatureMax);
        if (Tint is float u) R(nameof(Tint), u, TintMin, TintMax);
        R(nameof(Vibrance), Vibrance, PercentMin, PercentMax);
        R(nameof(Saturation), Saturation, PercentMin, PercentMax);
        R(nameof(Sharpening), Sharpening, PercentMin, PercentMax);
        R(nameof(Shadows), Shadows, PercentMin, PercentMax);
        R(nameof(Highlights), Highlights, PercentMin, PercentMax);
        R(nameof(Clarity), Clarity, PercentMin, PercentMax);
        R(nameof(HighlightRecovery), HighlightRecovery, Isp.HighlightRecovery.AmountMin, Isp.HighlightRecovery.AmountMax);
        return this;
    }

    // ---- the setting mappers (float arithmetic, stored widened to double) ----

    /// <summary>`tone_mapping.ev_offset = (double)(settings[2] + captureEv)` — the capture ev (log2f of the exposure ratio) plus the
    /// exposure setting, one single-precision addition.</summary>
    public static float EvOffsetKey(float exposure, float captureEv) => exposure + captureEv;
    /// <summary>`contrast_adjust.value = v · 0.04f` (0.04f = 0x3d23d70a).</summary>
    public static float ContrastKey(float v) => v * 0.04f;
    /// <summary>`tone_mapping.saturation = v · 0.01f + 1` (0.01f = 0x3c23d70a).</summary>
    public static float SaturationKey(float v) => v * 0.01f + 1f;
    /// <summary>`tone_mapping.vibrance = v · 0.01f + 1`.</summary>
    public static float VibranceKey(float v) => v * 0.01f + 1f;
    /// <summary>`tone_mapping.sharpening = v / table[0 &lt; v]`, table {33, 5}.</summary>
    public static float SharpeningKey(float v) => v / (0f < v ? 5f : 33f);
    /// <summary>`tone_adjust.lpyr_shadows = v · (−0.01f) + 1` (−0.01f = 0xbc23d70a).</summary>
    public static float ShadowsKey(float v) => v * -0.01f + 1f;
    /// <summary>`tone_adjust.lpyr_highlights = v · (−0.01f) + 1`.</summary>
    public static float HighlightsKey(float v) => v * -0.01f + 1f;
    /// <summary>`tone_adjust.lpyr_clarity = v · 0.01f`.</summary>
    public static float ClarityKey(float v) => v * 0.01f;

    /// <summary>
    /// The white-balance branch of the setting mappers: xy of (temp, tint), the ColorMatrix interpolated at <b>temp itself</b>
    /// (another Lumen path first converts the xy back to a CCT), `n = M·XYZ(xy)` normalized by its G.
    /// </summary>
    public static float[] NeutralFromTempTint(float temp, float tint, LumenProfile p)
    {
        var (x, y) = WhiteBalance.CctTintToXy(temp, tint);
        var m = WhiteBalance.MatrixAtTemperature(temp, WhiteBalance.IlluminantCctF(p.Low.InternalIlluminant), WhiteBalance.IlluminantCctF(p.High.InternalIlluminant),
                                                 p.Low.ColorMatrix, p.High.ColorMatrix);
        float invY = 1f / y;
        float X = x * invY, Z = ((1f - y) - x) * invY;
        float n0 = m[2] * Z + (m[0] * X + m[1]);
        float den = m[5] * Z + (m[3] * X + m[4]);
        float n2 = m[8] * Z + (X * m[6] + m[7]);
        float g = 1f / den;
        return new[] { n0 * g, 1f, g * n2 };
    }

    /// <summary>The capture's own temperature / tint — what `setInputDataStream` and `reset` load into
    /// ParamFloat 3/4: the CCT and tint of the stream's AsShot xy (= <see cref="WhiteBalance.CaptureWb"/>).</summary>
    public static (float Temperature, float Tint) CaptureWhiteBalance(WhiteBalance.CaptureWb wb) => (wb.Cct, wb.Tint);

    /// <summary>True when the white-balance branch of the setting mappers runs for these settings: temperature or tint differs from the
    /// loaded pair (float `!=`, as Lumen compares). Otherwise the display keeps the AsShot neutral it was loaded with.</summary>
    public bool PushesWhiteBalance(WhiteBalance.CaptureWb wb)
    {
        var (t0, u0) = CaptureWhiteBalance(wb);
        return WhiteBalancePushed || (Temperature ?? t0) != t0 || (Tint ?? u0) != u0;
    }

    /// <summary>
    /// The output tuning of one display level with these settings, from the tuning `setInputDataStream` built
    /// (<see cref="Isp.DisplayIspTuning.Build"/>; its sharpening argument is replaced here). A mapper runs only for a setting that
    /// differs from the loaded one (0 for every id but 3/4; ParamFloat 13 is 0 in a renderer without `ApplyTuning`), so the tuning
    /// of a default setting is untouched — which also keeps the branch-B lpyr values when those are left alone.
    /// </summary>
    public Tuning ApplyTo(Tuning loaded, float captureEv, float[]? neutral = null)
    {
        var t = loaded.Clone();
        if (Exposure != 0f) t.Set("tone_mapping.ev_offset", (double)EvOffsetKey(Exposure, captureEv));
        if (Saturation != 0f) t.Set("tone_mapping.saturation", (double)SaturationKey(Saturation));
        if (Vibrance != 0f) t.Set("tone_mapping.vibrance", (double)VibranceKey(Vibrance));
        if (Contrast != 0f) t.Set("contrast_adjust.value", (double)ContrastKey(Contrast));
        if (Highlights != 0f) t.Set("tone_adjust.lpyr_highlights", (double)HighlightsKey(Highlights));
        if (Shadows != 0f) t.Set("tone_adjust.lpyr_shadows", (double)ShadowsKey(Shadows));
        if (Clarity != 0f) t.Set("tone_adjust.lpyr_clarity", (double)ClarityKey(Clarity));
        t.Set("tone_mapping.sharpening", (double)SharpeningKey(Sharpening));
        if (MonotoneSharpening) t.Set(Isp.Stages.PostProcessingStage.MonotoneSharpeningKey, 1.0);   // a Lux key; absent = Lumen's response
        // Two more Lux keys. When they are absent, the tone stage gives Lumen's result.
        if (HighlightRecovery != 0f) t.Set(Isp.Stages.ToneMappingAcrStage.HighlightRecoveryKey, (double)HighlightRecovery);
        if (GamutCompress) t.Set(Isp.Stages.ToneMappingAcrStage.GamutCompressKey, 1.0);
        if (neutral is not null) t.Set("auto_white_balance.neutral_color", neutral.Select(v => (double)v).ToArray());
        return t;
    }

    /// <summary>
    /// The SoftISP Stats the display path runs with. Without a white-balance change: the loaded Stats. With one, Lumen
    /// overwrites, in the shared level-0 Stats object, only the neutral (slot 2's divisor) and the xy (of the new neutral); the camera
    /// matrix and the HueSatMap that the ColorCorrection functor derived from the
    /// <b>loaded</b> white point stay as they are (Lumen never recomputes the Stats; it reuses them).
    /// </summary>
    public IspStats ApplyTo(IspStats loaded, WhiteBalance.CaptureWb wb, LumenProfile profile)
    {
        if (!PushesWhiteBalance(wb)) return loaded;
        var n = WhiteBalanceNeutral(wb, profile);
        return CopyWithNeutral(loaded, n, WhiteBalance.NeutralToXy(n, profile));
    }

    /// <summary>Both halves of the setting push for one display level: the adjusted output tuning (mappers + the white-balance neutral in
    /// `auto_white_balance.neutral_color`) and the Stats to run it with. <paramref name="loadedStats"/> must be the Stats of the loaded
    /// tuning (never recompute them from the adjusted one: Lumen keeps the load-time camera matrix and HueSatMap).</summary>
    public (Tuning Tuning, IspStats Stats) Apply(Tuning loadedTuning, IspStats loadedStats, float captureEv, WhiteBalance.CaptureWb wb, LumenProfile profile)
        => (ToTuning(loadedTuning, captureEv, wb, profile), ApplyTo(loadedStats, wb, profile));

    /// <summary>The adjusted output tuning of one level (see <see cref="ApplyTo(Tuning, float, float[])"/>), with the white-balance neutral.</summary>
    public Tuning ToTuning(Tuning loadedTuning, float captureEv, WhiteBalance.CaptureWb wb, LumenProfile profile)
        => ApplyTo(loadedTuning, captureEv, WhiteBalanceNeutralOrNull(wb, profile));

    /// <summary>The neutral that the white-balance push installs for these settings (null when the white balance is not pushed).</summary>
    public float[]? WhiteBalanceNeutralOrNull(WhiteBalance.CaptureWb wb, LumenProfile profile)
        => PushesWhiteBalance(wb) ? WhiteBalanceNeutral(wb, profile) : null;

    float[] WhiteBalanceNeutral(WhiteBalance.CaptureWb wb, LumenProfile profile)
    {
        var (t0, u0) = CaptureWhiteBalance(wb);
        return NeutralFromTempTint(Temperature ?? t0, Tint ?? u0, profile);
    }

    static IspStats CopyWithNeutral(IspStats s, float[] neutral, (float X, float Y) xy) => new()
    {
        Neutral = neutral, NeutralXy = xy, Cct = s.Cct, Tint = s.Tint, IrBlend = s.IrBlend, SensorBlack = s.SensorBlack, SensorWhite = s.SensorWhite,
        Noise = s.Noise, CameraToOutput = s.CameraToOutput, CcSpace = s.CcSpace, OutSpace = s.OutSpace, HsvMap = s.HsvMap, Profile = s.Profile,
        NoiseSigma = s.NoiseSigma,
    };

    // ---- what a change invalidates (the display stage order: slot 2 ÷ neutral, 10 CC, 11 PostProcessing, 12 LensShading,
    //      13 ToneAdjust, 14 ContrastAdjust, 15 ToneMapping) ----

    /// <summary>The first display stage whose parameters differ between two settings, as an index into the display stage list
    /// (<see cref="Isp.DisplayAdjust.StageOrder"/>); <see cref="int.MaxValue"/> when nothing differs. Every stage before it produces
    /// exactly the same image, so its output can be reused.</summary>
    public static int FirstChangedStage(Adjustments a, Adjustments b, WhiteBalance.CaptureWb wb)
    {
        var (t0, u0) = CaptureWhiteBalance(wb);
        bool wbA = a.PushesWhiteBalance(wb), wbB = b.PushesWhiteBalance(wb);
        if (wbA != wbB || (wbA && ((a.Temperature ?? t0) != (b.Temperature ?? t0) || (a.Tint ?? u0) != (b.Tint ?? u0)))) return 0;   // slot 2
        if (a.Saturation != b.Saturation || a.Vibrance != b.Vibrance || SharpeningKey(a.Sharpening) != SharpeningKey(b.Sharpening) || a.MonotoneSharpening != b.MonotoneSharpening) return 2;   // slot 11
        if (a.Shadows != b.Shadows || a.Highlights != b.Highlights || a.Clarity != b.Clarity) return 4;   // slot 13
        if (a.Contrast != b.Contrast) return 5;   // slot 14
        if (a.Exposure != b.Exposure || a.HighlightRecovery != b.HighlightRecovery || a.GamutCompress != b.GamutCompress) return 6;   // slot 15
        return int.MaxValue;
    }

    public override string ToString()
    {
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        return $"exposure {F(Exposure)} contrast {F(Contrast)} temperature {(Temperature is float t ? F(t) : "as shot")} tint {(Tint is float u ? F(u) : "as shot")}{(WhiteBalancePushed ? " (white balance pushed)" : "")} " +
               $"vibrance {F(Vibrance)} saturation {F(Saturation)} sharpening {F(Sharpening)}{(MonotoneSharpening ? " (monotone)" : "")} shadows {F(Shadows)} highlights {F(Highlights)} clarity {F(Clarity)}" +
               (HighlightRecovery != 0f ? $" highlight recovery {F(HighlightRecovery)}" : "") + (GamutCompress ? " gamut compression" : "");
    }

    /// <summary>Parse "exposure=1,contrast=-20,temperature=4500,…" (the names of the properties, case-insensitive; also the
    /// ParamFloat ids "2=1,7=-20"). Unnamed properties keep their value in <paramref name="baseline"/>.</summary>
    public static Adjustments Parse(string spec, Adjustments? baseline = null)
    {
        var a = baseline ?? Default;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('='); if (eq <= 0) throw new FormatException($"adjustment must be name=value: '{part}'");
            string k = part[..eq].Trim().ToLowerInvariant(); float v = float.Parse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
            a = k switch
            {
                "exposure" or "2" => a with { Exposure = v }, "temperature" or "temp" or "3" => a with { Temperature = v }, "tint" or "4" => a with { Tint = v },
                "shadows" or "5" => a with { Shadows = v }, "highlights" or "6" => a with { Highlights = v }, "contrast" or "7" => a with { Contrast = v },
                "saturation" or "8" => a with { Saturation = v }, "vibrance" or "9" => a with { Vibrance = v }, "clarity" or "10" => a with { Clarity = v },
                "sharpening" or "13" => a with { Sharpening = v },
                "highlight-recovery" or "highlight_recovery" => a with { HighlightRecovery = v },
                "gamut-compress" or "gamut_compress" => a with { GamutCompress = v != 0f },
                _ => throw new FormatException($"unknown adjustment '{k}'"),
            };
        }
        return a;
    }
}
