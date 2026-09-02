using System.Globalization;

namespace Lux.Engine.Pipeline;

/// <summary>
/// Lumen's tuning tree, reproduced verbatim: a flat map of Lumen's own keys ("demosaicking.type",
/// "lens_shading.multiplier", ...) to values. Implementations of every stage are selected by the
/// <c>&lt;stage&gt;.type</c> string exactly as cp.dll does (SoT §3.2 defaults, §4.1 string→enum tables), so a
/// new method is added by registering it under a new type string, never by an if/else in the pipeline.
/// </summary>
public sealed class Tuning
{
    private readonly Dictionary<string, object> _v = new(StringComparer.Ordinal);

    public Tuning() { }
    private Tuning(Dictionary<string, object> v) { _v = new Dictionary<string, object>(v, StringComparer.Ordinal); }
    public Tuning Clone() => new(_v);

    public IEnumerable<KeyValuePair<string, object>> All => _v;
    public bool Has(string key) => _v.ContainsKey(key);
    public Tuning Set(string key, object value) { _v[key] = value; return this; }

    public string Str(string key) => _v.TryGetValue(key, out var v) ? System.Convert.ToString(v, CultureInfo.InvariantCulture)! : throw Missing(key);
    public double Num(string key) => _v.TryGetValue(key, out var v) ? System.Convert.ToDouble(v, CultureInfo.InvariantCulture) : throw Missing(key);
    public bool Flag(string key) => _v.TryGetValue(key, out var v) && v is bool b ? b : throw Missing(key);
    public double[] Vec(string key) => _v.TryGetValue(key, out var v) && v is double[] a ? a : throw Missing(key);
    public string Type(string stage) => Str(stage + ".type");
    private static KeyNotFoundException Missing(string k) => new($"tuning key '{k}' is not set");

    /// <summary>Parse a CLI-style override "key=value" (numbers, true/false, comma vectors, else string).</summary>
    public Tuning Apply(string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0) throw new FormatException($"tuning override must be key=value: '{assignment}'");
        string key = assignment[..eq].Trim(), raw = assignment[(eq + 1)..].Trim();
        object val;
        if (raw is "true" or "false") val = raw == "true";
        else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) val = d;
        else if (raw.Contains(',') && raw.Split(',').All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            val = raw.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        else val = raw;
        return Set(key, val);
    }

    /// <summary>
    /// Lumen's compiled-in defaults (`FUN_1803d61c0`, SoT §3.2): every stage "none" except auto_white_balance /
    /// demosaicking / color_correction "default"; output.color_space "srgb", output.white_point "native".
    /// </summary>
    public static Tuning LumenDefaults()
    {
        var t = new Tuning();
        foreach (var s in new[] { "hot_pixel_leakage_removal", "hot_pixel_removal", "cross_talk_correction",
                                  "color_noise_reduction", "bayer_phase_fix", "adaptive_desaturation",
                                  "highlight_restore", "denoising", "lens_shading", "tone_adjust",
                                  "contrast_adjust", "tone_mapping" })
            t.Set(s + ".type", "none");
        t.Set("auto_white_balance.type", "default").Set("demosaicking.type", "default").Set("color_correction.type", "default");
        t.Set("color_noise_reduction.std_dev_multiplier", 1.0).Set("color_noise_reduction.color_denoise_multiplier", 1.0);
        t.Set("auto_white_balance.neutral_temp", 5000.0).Set("auto_white_balance.neutral_tint", 0.0);
        t.Set("adaptive_desaturation.shadow_cutoff", 0.01).Set("adaptive_desaturation.highlight_cutoff", 0.8);
        t.Set("denoising.threshold_multiplier", 1.0);
        // FUN_1803d61c0 L289–329 (tree defaults; spec a0349917a78884e46 §1.2)
        t.Set("bilateral_denoiser.window_size", 5.0).Set("bilateral_denoiser.chroma_boost", 2.0).Set("bilateral_denoiser.pyramid_size", 5.0)
         .Set("bilateral_denoiser.gradient_threshold", 0.0).Set("bilateral_denoiser.min_luma_std", 0.0025);
        t.Set("nlm_denoiser.window_size", 5.0).Set("nlm_denoiser.patch_size", 5.0).Set("nlm_denoiser.step_size", 2.0).Set("nlm_denoiser.chroma_boost", 2.0)
         .Set("nlm_denoiser.fast_search", true).Set("nlm_denoiser.pyramid_size", 5.0).Set("nlm_denoiser.min_luma_std", 0.0025);   // live value (hyb hook: tstd floor 0.0025)
        t.Set("lens_shading.multiplier", 1.0);
        t.Set("color_correction.matrix", new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 });
        t.Set("output.color_space", "srgb").Set("output.white_point", "native");
        t.Set("pipeline.parameter_scale", 1.0);
        t.Set("tone_mapping.ev_offset", 0.0).Set("tone_mapping.saturation", 1.0).Set("tone_mapping.vibrance", 1.0)
         .Set("tone_mapping.sharpening", 0.0).Set("tone_mapping.sharpening_scale", 1.0).Set("tone_mapping.grain_power", 0.0)
         .Set("tone_mapping.grain_sigma", 1.0);
        t.Set("tone_adjust.filter_epsilon", 0.0).Set("tone_adjust.filter_size", 0.0).Set("tone_adjust.fusion_black_point", 63.0)
         .Set("tone_adjust.fusion_detail_gain", 1.4648e-4).Set("tone_adjust.fusion_ev_minus", 0.0).Set("tone_adjust.fusion_ev_plus", 0.0)
         .Set("tone_adjust.fusion_noise_filter_scale", 0.015).Set("tone_adjust.fusion_noise_gain", 1.0)
         .Set("tone_adjust.fusion_shadow_detail", 1.0).Set("tone_adjust.fusion_sharpening", 0.0)
         .Set("tone_adjust.filter_epsilon2", -12.0);
        // `A::LaplacianPyramidConfig` ctor defaults (`FUN_180398b10`, pipeline+0x1b90; `0x1806d46e0` = 0/1/1/0.5,
        // percentiles −8 / 0.2 / −1, samples `0x1806d4770` = −8.0 … +1.0 step 0.5) — read by `ToneAdjust:laplacian_pyramid`.
        t.Set("tone_adjust.lpyr_clarity", 0.0).Set("tone_adjust.lpyr_shadows", 1.0).Set("tone_adjust.lpyr_highlights", 1.0)
         .Set("tone_adjust.lpyr_sigma", 0.5).Set("tone_adjust.lpyr_lower_percentile", -8.0)
         .Set("tone_adjust.lpyr_higher_percentile", 0.2).Set("tone_adjust.lpyr_mid_percentile", -1.0);
        t.Set("contrast_adjust.strength", 0.0);
        return t;
    }
}

/// <summary>Lumen's tuning string → enum tables (static-init block 0x1803f34d0–0x1803fb4b0; SoT §4.1).</summary>
public static class StageEnums
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Tables = new Dictionary<string, IReadOnlyDictionary<string, int>>
    {
        ["hot_pixel_leakage_removal"] = OnOff(), ["hot_pixel_removal"] = OnOff(), ["adaptive_desaturation"] = OnOff(),
        ["color_noise_reduction"] = OnOff(), ["highlight_restore"] = OnOff(), ["contrast_adjust"] = OnOff(),
        ["cross_talk_correction"] = D(("none", 0), ("default", 1), ("ir_correction", 2)),
        ["auto_white_balance"] = D(("none", 0), ("default", 1), ("manual_color", 2), ("manual_temp", 3)),
        ["demosaicking"] = D(("none", 0), ("default", 1), ("collapse2", 2), ("collapse4", 3), ("collapse8", 4), ("linear", 5), ("malvar", 6), ("light_v1", 7), ("light_v2", 8)),
        ["bayer_phase_fix"] = D(("none", 0), ("default", 1), ("ar1335", 2)),
        ["denoising"] = D(("none", 0), ("default", 1), ("nlm", 2), ("nlm_bayer", 3), ("bilateral", 4), ("bilateral_420", 5), ("hybrid", 6)),
        ["lens_shading"] = D(("none", 0), ("default", 1), ("inverse", 2)),
        ["color_correction"] = D(("none", 0), ("default", 1), ("manual", 2), ("optimized", 3)),
        ["output.color_space"] = D(("none", 1), ("srgb", 2), ("adobe_rgb", 3), ("linear_srgb", 4), ("linear_prophoto_rgb", 5), ("linear_adobe_rgb", 6)),
        ["output.white_point"] = D(("native", 0), ("d50", 5), ("d65", 7)),
        ["tone_adjust"] = D(("none", 0), ("default", 1), ("shadow_highlight", 2), ("exposure_fusion", 3), ("laplacian_pyramid", 4)),
        ["tone_mapping"] = D(("none", 0), ("default", 1), ("linear", 2), ("acr", 3), ("light_v1", 4), ("light_v1_lowlight", 5), ("light_v2", 6)),
    };
    private static IReadOnlyDictionary<string, int> OnOff() => D(("none", 0), ("default", 1));
    private static IReadOnlyDictionary<string, int> D(params (string, int)[] e) => e.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    public static int Enum(string stage, string typeString) =>
        Tables.TryGetValue(stage, out var t) && t.TryGetValue(typeString, out var e) ? e
        : throw new ArgumentException($"unknown {stage} type '{typeString}' (Lumen enum table)");
}
