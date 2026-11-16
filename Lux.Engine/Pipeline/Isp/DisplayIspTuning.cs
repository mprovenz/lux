namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// The **display / output** ISP tuning that Lumen's `RendererPrivate::setInputDataStream` writes for each pyramid level —
/// the counterpart of <see cref="ModuleIspTuning"/> for the Color-domain pipeline that
/// paints the screen and the companion JPEG. Everything not listed keeps Lumen's pipeline default
/// (`output.color_space = srgb`, `output.white_point = native`, denoising / CNR / hot-pixel / highlight-restore /
/// bayer-phase-fix / cross-talk `none`, `pipeline.parameter_scale = 1`).
/// </summary>
public static class DisplayIspTuning
{
    /// <summary>Lumen's renderer-profile level offset: profile 1/2 → 1, 0 → 4, 3 → a byte flag of the profile object XOR 1
    /// (0 on the L16 desktop renderer). Used both as the PipelineCache level offset and as the
    /// `2^(−b)` factor of `tone_mapping.sharpening_scale`.</summary>
    public const int L16ProfileOffset = 0;

    /// <summary>`(9 &gt;&gt; profile) &amp; 1` — true for profiles 0 and 3, i.e. the desktop
    /// renderer keeps `color_correction.type = "optimized"` (<see cref="Build"/> writes key 12 only when this is false).</summary>
    public static bool KeepsOptimized(int rendererProfile) => ((9 >> rendererProfile) & 1) != 0;

    /// <summary>`base = allGroup0 ? 32 : 24`, `t = max(base·s − 1, 3)`,
    /// `filter_size = t + t + 1`. A 16-module `.lri` has modules in both group 0 and group 1, so `allGroup0` is false
    /// and `base = 24` — level 3 gives `2·3 + 1 = 7`.</summary>
    public static float FilterSize(float sharpeningScale, bool allCamerasGroup0)
    {
        float b = allCamerasGroup0 ? 32.0f : 24.0f;
        float t = b * sharpeningScale + -1.0f;
        if (t <= 3.0f) t = 3.0f;
        return t + t + 1.0f;
    }

    /// <summary>`tone_mapping.sharpening_scale` = `((float)exportDims[level].x / (float)exportDims[0].x) · 2^(−b)`
    /// — note: the **export** dims of the renderer, not the pipeline dims.</summary>
    public static float SharpeningScale(int exportW, int exportW0, int profileOffset)
        => ((float)exportW / (float)exportW0) * (1.0f / (float)(1 << profileOffset));

    /// <summary>
    /// One pyramid level's output tuning.
    /// <paramref name="gateV2"/> is Lumen's v2 renderer gate: false → `light_v1` + the **neutralized** local-Laplacian set
    /// (branch A); true → `light_v2` + the Stats-derived
    /// `lpyr_*` percentiles and samples (branch B, which Lumen's GUI exports carry).
    /// </summary>
    public static Tuning Build(int level, float evOffset, float[] neutral, float lensShadingMultiplier,
                               int exportW, int exportW0, bool gateV2, bool lowLight = false,
                               bool allCamerasGroup0 = false, int rendererProfile = 3, int profileOffset = L16ProfileOffset,
                               BranchBLpyr? branchB = null, Tuning? baseTuning = null, float sharpening = 0f)
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
        // 11 — !lowlight && gate == 0 → light_v1; lowlight && gate == 0 → light_v1_lowlight; else light_v2
        t.Set("tone_mapping.type", gateV2 ? "light_v2" : lowLight ? "light_v1_lowlight" : "light_v1");
        if (!KeepsOptimized(rendererProfile)) t.Set("color_correction.type", "default");   // 12
        float s = SharpeningScale(exportW, exportW0, profileOffset);                   // 14
        t.Set("tone_adjust.filter_size", (double)FilterSize(s, allCamerasGroup0));     // 13
        t.Set("tone_mapping.sharpening_scale", (double)s);
        // 16 — the local-Laplacian set
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
            var b = branchB ?? throw new ArgumentException("gateV2 needs the Stats-derived lpyr percentiles/samples");
            t.Set("tone_adjust.lpyr_clarity", 1.0).Set("tone_adjust.lpyr_shadows", 0.0).Set("tone_adjust.lpyr_highlights", 0.0);
            t.Set("tone_adjust.lpyr_lower_percentile", (double)b.Lower).Set("tone_adjust.lpyr_higher_percentile", (double)b.Higher)
             .Set("tone_adjust.lpyr_mid_percentile", (double)b.Mid);
            t.Set("tone_adjust.lpyr_samples", b.Samples.Select(v => (double)v).ToArray());
        }
        // Pipeline-ctor / tuning-tree values the display path leaves alone but that the stages read
        t.Set("tone_mapping.saturation", 1.0).Set("tone_mapping.vibrance", 1.0);
        t.Set("tone_mapping.grain_power", 1.0).Set("tone_mapping.grain_sigma", 0.0);
        // Before a render or an export, Lumen maps each renderer property onto every level tuning. For ParamFloat 13 (the editor's
        // sharpening slider): tone_mapping.sharpening = (double)(v / {33.0, 5.0}[0 < v]).
        // The GUI's export renderer carries v = 5.0 — CIAPI::ApplyTuning's mode-0 value (`setProperty(0xd, 5.0f)`) —
        // so its JPEGs are sharpened with 1.0. A renderer that never had ApplyTuning has v = 0 → 0.0.
        t.Set("tone_mapping.sharpening", (double)SharpeningFromProperty(sharpening));
        t.Set("contrast_adjust.value", 0.0);
        _ = level;
        return t;
    }

    /// <summary>`ParamFloat 13` (sharpening slider, −100..100) → `tone_mapping.sharpening = v / (0 &lt; v ? 5 : 33)` in float.</summary>
    public static float SharpeningFromProperty(float v) => v / (0f < v ? 5f : 33f);
    /// <summary>The value `CIAPI::ApplyTuning(0)` writes into `ParamFloat 13` (`setProperty(0xd, 5.0f)`) and the state Lumen's GUI exports carry.</summary>
    public const float GuiSharpeningProperty = 5f;

    /// <summary>Branch B's `lpyr_samples` between `lower` and `higher` with the step 0.5:
    /// `n = clamp(span/0.5, 5, 15)`, `cnt = (int)n`, spacing `d = span/cnt`, then `cnt − 1` samples
    /// `lower + i·d`. 4…14 uniformly spaced values, which is what satisfies the kernel's spacing assertion.</summary>
    public static float[] BranchBSamples(float lower, float higher)
    {
        float span = higher - lower;
        float n = span / 0.5f;
        n = n < 15.0f ? n : 15.0f;      // MINSS order: NaN gives 15
        n = n > 5.0f ? n : 5.0f;        // MAXSS order: NaN gives 5
        int cnt = (int)n;
        if (cnt < 2) return Array.Empty<float>();
        float d = span / (float)cnt;
        cnt -= 1;
        var v = new float[cnt];
        for (int i = 0; i < cnt; i++) v[i] = lower + (float)i * d;
        return v;
    }

    /// <summary>The branch-B (<c>gateV2</c>) local-Laplacian parameters — `logf` of the Stats value-histogram
    /// percentiles times `exp2f(ev)`, and 4…14 uniformly spaced samples between them. This record does not derive them:
    /// <see cref="DisplayValueHistogram.BranchB"/> computes them from the value histogram.</summary>
    public sealed record BranchBLpyr(float Lower, float Higher, float Mid, float[] Samples);
}
