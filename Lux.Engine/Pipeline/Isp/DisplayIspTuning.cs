namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// The **display / output** ISP tuning `RendererPrivate::setInputDataStream` `180490d50` writes into
/// `renderer+0x650[level]` — the counterpart of <see cref="ModuleIspTuning"/> for the Color-domain pipeline that
/// paints the screen and the companion JPEG. Everything not listed keeps the `FUN_1803d61c0` default
/// (`output.color_space = srgb`, `output.white_point = native`, denoising / CNR / hot-pixel / highlight-restore /
/// bayer-phase-fix / cross-talk `none`, `pipeline.parameter_scale = 1`). Spec `a-display-isp.md` §3–§4.
/// </summary>
public static class DisplayIspTuning
{
    /// <summary>`FUN_18050c640(profile)` `18050c640`: profile 1/2 → 1, 0 → 4, 3 → `*(byte*)(profile+4) ^ 1`
    /// (0 on the L16 desktop renderer, measured on cp.dll). Used both as the PipelineCache level offset and as the
    /// `2^(−b)` factor of `tone_mapping.sharpening_scale`.</summary>
    public const int L16ProfileOffset = 0;

    /// <summary>`FUN_18050c720(profile)` = `(9 &gt;&gt; profile) &amp; 1` — true for profiles 0 and 3, i.e. the desktop
    /// renderer keeps `color_correction.type = "optimized"` (key 12 of §3.1 is only written when this is false).</summary>
    public static bool KeepsOptimized(int rendererProfile) => ((9 >> rendererProfile) & 1) != 0;

    /// <summary>`FUN_1804b20f0` `1804b20f0`: `base = allGroup0 ? 32 : 24`, `t = max(base·s − 1, 3)`,
    /// `filter_size = t + t + 1`. On a 16-module `.lri` `FUN_180111c40` is 0 (groups 0 and 1 both present, cp.dll's
    /// histogram `g0=5 g1=5`), so `base = 24` — level 3 gives `2·3 + 1 = 7`, the value the parameter dump shows.</summary>
    public static float FilterSize(float sharpeningScale, bool allCamerasGroup0)
    {
        float b = allCamerasGroup0 ? 32.0f : 24.0f;
        float t = b * sharpeningScale + -1.0f;
        if (t <= 3.0f) t = 3.0f;
        return t + t + 1.0f;
    }

    /// <summary>`tone_mapping.sharpening_scale` = `((float)exportDims[level].x / (float)exportDims[0].x) · 2^(−b)`
    /// — note `renderer+0x270` (the **export** dims), not the pipeline dims.</summary>
    public static float SharpeningScale(int exportW, int exportW0, int profileOffset)
        => ((float)exportW / (float)exportW0) * (1.0f / (float)(1 << profileOffset));

    /// <summary>
    /// One pyramid level's output tuning.
    /// <paramref name="gateV2"/> is `renderer+0x64` (§4.1): 0 → `light_v1` + the **neutralised** local-Laplacian set
    /// (what the headless renderer measurably does — branch A); non-zero → `light_v2` + the Stats-derived
    /// `lpyr_*` percentiles and samples (branch B, which the user's own GUI exports carry).
    /// </summary>
    public static Tuning Build(int level, float evOffset, float[] neutral, float lensShadingMultiplier,
                               int exportW, int exportW0, bool gateV2, bool lowLight = false,
                               bool allCamerasGroup0 = false, int rendererProfile = 3, int profileOffset = L16ProfileOffset,
                               BranchBLpyr? branchB = null, Tuning? baseTuning = null)
    {
        var t = (baseTuning ?? Tuning.LumenDefaults()).Clone();
        t.Set("demosaicking.type", "none");                                            // 1
        t.Set("lens_shading.type", "inverse");                                         // 2
        t.Set("color_correction.type", "optimized");                                   // 3
        t.Set("tone_mapping.ev_offset", (double)evOffset);                             // 4
        t.Set("tone_adjust.type", "laplacian_pyramid");                                // 5
        t.Set("contrast_adjust.type", "default");                                      // 6
        t.Set("auto_white_balance.type", "manual_color");                              // 7
        t.Set("auto_white_balance.neutral_color", neutral.Select(v => (double)v).ToArray());   // 8
        t.Set("adaptive_desaturation.type", "none");                                   // 9
        t.Set("lens_shading.multiplier", (double)lensShadingMultiplier);               // 10
        // 11 — FUN_1804aefa0 (SoT §3.6): !lowlight && gate == 0 → light_v1; lowlight && gate == 0 → light_v1_lowlight; else light_v2
        t.Set("tone_mapping.type", gateV2 ? "light_v2" : lowLight ? "light_v1_lowlight" : "light_v1");
        if (!KeepsOptimized(rendererProfile)) t.Set("color_correction.type", "default");   // 12
        float s = SharpeningScale(exportW, exportW0, profileOffset);                   // 14
        t.Set("tone_adjust.filter_size", (double)FilterSize(s, allCamerasGroup0));     // 13
        t.Set("tone_mapping.sharpening_scale", (double)s);
        // 16 — FUN_1804af2f0
        t.Set("tone_adjust.lpyr_sigma", 0.5);
        if (!gateV2)
        {
            t.Set("tone_adjust.lpyr_clarity", 0.0).Set("tone_adjust.lpyr_shadows", 1.0).Set("tone_adjust.lpyr_highlights", 1.0);
            foreach (var k in new[] { "fusion_detail_gain", "fusion_noise_gain", "fusion_shadow_detail", "fusion_sharpening",
                                      "fusion_black_point", "fusion_ev_minus", "fusion_ev_plus", "fusion_noise_filter_scale" })
                t.Set("tone_adjust." + k, 0.0);
            // the three lpyr_*_percentile keys and lpyr_samples are NOT written — the ctor defaults survive
            // (−8.0 / 0.2 / −1.0 and the 19 samples −8.0 … +1.0 step 0.5).
        }
        else
        {
            var b = branchB ?? throw new ArgumentException("gateV2 needs the Stats-derived lpyr percentiles/samples (§4.2)");
            t.Set("tone_adjust.lpyr_clarity", 1.0).Set("tone_adjust.lpyr_shadows", 0.0).Set("tone_adjust.lpyr_highlights", 0.0);
            t.Set("tone_adjust.lpyr_lower_percentile", (double)b.Lower).Set("tone_adjust.lpyr_higher_percentile", (double)b.Higher)
             .Set("tone_adjust.lpyr_mid_percentile", (double)b.Mid);
            t.Set("tone_adjust.lpyr_samples", b.Samples.Select(v => (double)v).ToArray());
        }
        // Pipeline-ctor / tuning-tree values the display path leaves alone but that the stages read
        t.Set("tone_mapping.saturation", 1.0).Set("tone_mapping.vibrance", 1.0);
        t.Set("tone_mapping.grain_power", 1.0).Set("tone_mapping.grain_sigma", 0.0).Set("tone_mapping.sharpening", 0.0);
        t.Set("contrast_adjust.value", 0.0);
        _ = level;
        return t;
    }

    /// <summary>`FUN_180398be0(&amp;vec, {lower, higher}, 0.5f)` — branch B's `lpyr_samples`:
    /// `n = clamp(span/0.5, 5, 15)`, `cnt = (int)n`, spacing `d = span/cnt`, then `cnt − 1` samples
    /// `lower + i·d`. 4…14 uniformly spaced values, which is what satisfies the kernel's spacing assertion.</summary>
    public static float[] BranchBSamples(float lower, float higher)
    {
        float span = higher - lower;
        float n = span / 0.5f;
        n = n < 15.0f ? n : 15.0f;      // minss(n, 15)
        n = n > 5.0f ? n : 5.0f;        // maxss(n, 5)
        int cnt = (int)n;
        if (cnt < 2) return Array.Empty<float>();
        float d = span / (float)cnt;
        cnt -= 1;
        var v = new float[cnt];
        for (int i = 0; i < cnt; i++) v[i] = lower + (float)i * d;
        return v;
    }

    /// <summary>The branch-B (`renderer+0x64 != 0`) local-Laplacian parameters of §4.2 — `logf` of the Stats value-histogram
    /// percentiles times `exp2f(ev)`, and 4…14 uniformly spaced samples between them. Not derived here: supply them
    /// (e.g. from a cp.dll parameter dump) when porting branch B.</summary>
    public sealed record BranchBLpyr(float Lower, float Higher, float Mid, float[] Samples);
}
