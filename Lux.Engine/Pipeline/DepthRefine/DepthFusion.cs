namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>What the fusion did to one depth map.</summary>
/// <param name="Applied">False when the capture was left as it is; <paramref name="Reason"/> then says why.</param>
/// <param name="Reason">Why the capture was left as it is, or why only the sky was corrected.</param>
/// <param name="Pixels">Pixels of the depth map.</param>
/// <param name="Rewritten">Pixels whose depth was written again (the sky and the places where camera and network disagree).</param>
/// <param name="Changed">Pixels whose depth moved by more than one depth step.</param>
/// <param name="Trusted">Pixels with trusted camera depth.</param>
/// <param name="TrustedChanged">Trusted pixels whose depth moved by more than one depth step.</param>
/// <param name="Sky">Pixels set to the far limit as sky.</param>
/// <param name="SkyOnly">True when only the sky was corrected.</param>
public sealed record FusionReport(bool Applied, string? Reason, long Pixels, long Rewritten, long Changed, long Trusted, long TrustedChanged, long Sky,
                                  bool SkyOnly = false);

/// <summary>
/// Joins the camera's depth with a depth network's view of the same picture.
/// <para>The camera's depth has real distances, but its depth steps sit beside the object outlines, it loses thin objects, it fills the
/// gaps between bars and it scatters the sky. The network sees outlines, thin objects, gaps and the sky, but it has no real distances.
/// The fusion keeps the camera's depth except in two places: in the sky, and where the local shape of the camera's depth contradicts
/// the network's. There each pixel takes the mean of the trusted camera depth nearby whose network value is like its own. Where no
/// such depth exists, the pixel takes the network value, mapped to distance by a curve fitted to the trusted camera depth.</para>
/// <para>The other cameras of the reference group check the camera's depth (<see cref="CrossViewCost"/>). Depth that they contradict is
/// not trusted. Depth that they confirm is never rewritten outside the sky, also where the network sees another shape: a printed
/// picture on a wall is flat, whatever the network sees in it.</para>
/// <para>Without that check, only the sky is corrected. This is the case for a capture whose reference camera has no group of cameras
/// with the same view (a telephoto capture), and for a capture with too little trusted depth to fit the curve.</para>
/// <para>All work is done in disparity: <c>focal length × baseline / depth</c>, in pixels.</para>
/// </summary>
public static class DepthFusion
{
    static float Step(float d) => Math.Max(RefineConstants.StepPixels, RefineConstants.StepRelative * d);

    /// <summary>The reason in the report of a capture without a cross-view cost.</summary>
    public const string NoGroupReason = "this capture has no group of cameras with the same view to check the depth against";
    /// <summary>The reason in the report of a capture with too little trusted depth.</summary>
    public const string NoCurveReason = "the camera's depth has too few reliable areas to compare with";

    /// <summary>The camera's depth as disparity. A value that is not a positive number gets the disparity of the far limit.</summary>
    public static float[] Disparity(float[] depth, int w, int h, double focalBaseline)
    {
        float fb = (float)focalBaseline, dFar = fb / RefineConstants.FarDepth;
        var d0 = new float[(long)w * h];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++) { float z = depth[o + x]; d0[o + x] = z > 0f && !float.IsInfinity(z) ? fb / Math.Max(z, 1f) : dFar; }
        });
        return d0;
    }

    /// <param name="depth">The camera's depth in mm, row by row. Values that are not positive numbers are never changed.</param>
    /// <param name="rgba">The picture on the same grid, 8 bits per channel, four bytes per pixel.</param>
    /// <param name="focalBaseline">Focal length (pixels) × baseline (mm): disparity = <paramref name="focalBaseline"/> / depth.</param>
    /// <param name="prior">The network's maps on the same grid.</param>
    /// <param name="crossView">The cross-view cost of <paramref name="depth"/> on the same grid (<see cref="CrossViewCost"/>), made with
    /// the disparity of <see cref="Disparity"/>. Null = the capture has none: only the sky is corrected.</param>
    /// <returns>The refined depth in mm, and the report. A pixel the fusion does not rewrite keeps its exact value.</returns>
    public static (float[] Depth, FusionReport Report) Fuse(float[] depth, byte[] rgba, int w, int h, double focalBaseline, DepthPriorMaps prior,
                                                          float[]? crossView, CancellationToken cancel = default)
    {
        long n = (long)w * h;
        float fb = (float)focalBaseline, dFar = fb / RefineConstants.FarDepth;
        var d0 = Disparity(depth, w, h, focalBaseline); var valid = new bool[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) { float z = depth[o + x]; valid[o + x] = z > 0f && !float.IsInfinity(z); } });
        var brightness = Brightness(rgba, w, h);
        var textured = Textured(brightness, w, h);

        // ---- the sky
        var (candidates, sky) = Sky(depth, brightness, valid, prior, w, h);
        long skyCount = sky is null ? 0 : Count(sky);
        cancel.ThrowIfCancellationRequested();

        // only the sky: every sky pixel goes to the far limit, all other depth stays
        (float[], FusionReport) SkyOnly(string why, long trustedCount)
        {
            if (sky is null || skyCount == 0) return (depth, new FusionReport(false, why + ", and the picture has no sky", n, 0, 0, trustedCount, 0, 0, true));
            var only = (float[])depth.Clone(); long moved = 0;
            for (long i = 0; i < n; i++)
                if (sky[i]) { only[i] = RefineConstants.FarDepth; if (Math.Abs(dFar - d0[i]) > Step(d0[i])) moved++; }
            return (only, new FusionReport(true, why, n, skyCount, moved, trustedCount, 0, skyCount, true));
        }
        if (crossView is null) return SkyOnly(NoGroupReason, 0);

        // ---- trusted camera depth: textured, no depth step close by, and the other cameras agree
        var (trusted, confirmed) = Trusted(d0, valid, textured, crossView, candidates, w, h);
        long trustedCount = Count(trusted);
        cancel.ThrowIfCancellationRequested();

        // ---- network values to disparity: one rising curve for the picture, then a local shift
        if (Curve(prior.Tiled, d0, trusted, w, h) is not { } curve) return SkyOnly(NoCurveReason, trustedCount);
        var (ku, kd) = curve;
        var mapped = ApplyCurve(prior.Tiled, ku, kd, w, h);
        var local = LocalShift(mapped, d0, trusted, w, h);
        if (sky is not null) RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) if (sky[o + x]) local[o + x] = dFar; });
        cancel.ThrowIfCancellationRequested();

        // ---- where the relief of the camera's depth contradicts the network's, and the other cameras do not confirm the camera's depth
        var consistent = ReliefConsistent(d0, mapped, w, h);
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) if (confirmed[o + x]) consistent[o + x] = true; });
        cancel.ThrowIfCancellationRequested();

        // ---- the band that is rewritten, and the camera samples that may fill it
        var band = new bool[n]; var source = new bool[n];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x; float d = d0[i];
                bool agree = Math.Abs(local[i] - d) <= Math.Max(RefineConstants.AgreePixels, RefineConstants.AgreeRelative * d);
                source[i] = trusted[i] && agree && consistent[i];
                band[i] = valid[i] && (!consistent[i] || (sky is not null && sky[i]));
            }
        });
        long rewritten = Count(band);
        if (rewritten == 0) return (depth, new FusionReport(true, null, n, 0, 0, trustedCount, 0, 0));
        var fused = FillBand(d0, source, band, local, w, h, cancel);

        // ---- back to depth
        var result = (float[])depth.Clone();
        long changed = 0, trustedChanged = 0; object sum = new();
        Parallel.For(0, h, () => (C: 0L, T: 0L), (y, _, acc) =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x;
                if (!band[i]) continue;
                float d = Math.Max(fused[i], 0f);
                if (d == d0[i]) continue;
                result[i] = fb / Math.Max(d, dFar);
                if (Math.Abs(d - d0[i]) > Step(d0[i])) { acc.C++; if (trusted[i]) acc.T++; }
            }
            return acc;
        }, acc => { lock (sum) { changed += acc.C; trustedChanged += acc.T; } });
        return (result, new FusionReport(true, null, n, rewritten, changed, trustedCount, trustedChanged, skyCount));
    }

    static long Count(bool[] m)
    {
        long c = 0;
        for (long i = 0; i < m.Length; i++) if (m[i]) c++;
        return c;
    }

    // ------------------------------------------------------------------------------------------------ trusted camera depth

    /// <summary>The brightness of the picture, 0–255 (the usual weights of red, green and blue, rounded to whole levels).</summary>
    internal static float[] Brightness(byte[] rgba, int w, int h)
    {
        var l = new float[(long)w * h];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++) { long p = (o + x) * 4; l[o + x] = (rgba[p] * 4899 + rgba[p + 1] * 9617 + rgba[p + 2] * 1868 + 8192) >> 14; }
        });
        return l;
    }

    /// <summary>The picture has texture where the standard deviation of the brightness in a window of
    /// <see cref="RefineConstants.TextureWindow"/> pixels is above <see cref="RefineConstants.TextureMinDeviation"/> gray levels.</summary>
    internal static bool[] Textured(float[] l, int w, int h)
    {
        long n = (long)w * h;
        var l2 = new float[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) l2[o + x] = l[o + x] * l[o + x]; });
        var m = RefineOps.BoxMean(l, w, h, RefineConstants.TextureWindow);
        var m2 = RefineOps.BoxMean(l2, w, h, RefineConstants.TextureWindow);
        var t = new bool[n]; float minVar = RefineConstants.TextureMinDeviation * RefineConstants.TextureMinDeviation;
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) t[o + x] = m2[o + x] - m[o + x] * m[o + x] > minVar; });
        return t;
    }

    /// <summary>The largest cross-view cost of trusted camera depth: a floor, or a multiple of the median cost of the textured pixels
    /// that enough cameras see. A picture with much noise or blur has a higher cost everywhere, and the limit follows it.</summary>
    internal static float CostLimit(float[] cost, bool[] textured, int w, int h)
    {
        int step = RefineConstants.CostMedianStep;
        var s = new float[(long)((w + step - 1) / step) * ((h + step - 1) / step)]; int k = 0;
        for (int y = 0; y < h; y += step)
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x += step) if (textured[o + x] && cost[o + x] < RefineConstants.CrossViewKnownBelow) s[k++] = cost[o + x];
        }
        return k == 0 ? RefineConstants.CostLimitMin : Math.Max(RefineConstants.CostLimitMin, RefineConstants.CostLimitScene * (float)RefineOps.Median(s, k));
    }

    /// <summary>
    /// Trusted: the camera's depth where the picture has texture, the other cameras agree with the depth, and no depth step is close:
    /// none within <see cref="RefineConstants.NearSideReach"/> pixels on the near side of a step (the camera draws its steps outside the
    /// object, on the background), and none within <see cref="RefineConstants.FarSideReach"/> pixels on the far side.
    /// <para>Confirmed: the camera's depth where the picture has texture and the other cameras agree with it clearly, whether a depth
    /// step is close or not.</para>
    /// <para>A sky candidate is neither.</para>
    /// </summary>
    internal static (bool[] Trusted, bool[] Confirmed) Trusted(float[] d0, bool[] valid, bool[] textured, float[] cost, bool[]? skyCandidates, int w, int h)
    {
        long n = (long)w * h;
        var farther = RefineOps.Disk(d0, w, h, RefineConstants.NearSideReach, max: false);
        var nearer = RefineOps.Disk(d0, w, h, RefineConstants.FarSideReach, max: true);
        float limit = CostLimit(cost, textured, w, h), clear = RefineConstants.ConfirmedCostPart * limit;
        var t = new bool[n]; var c = new bool[n];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x; float d = d0[i];
                if (!valid[i] || !textured[i] || (skyCandidates is not null && skyCandidates[i])) continue;
                bool nearSide = d - farther[i] > Step(d), farSide = nearer[i] - d > Step(nearer[i]);
                t[i] = !nearSide && !farSide && cost[i] <= limit;
                c[i] = cost[i] <= clear && cost[i] < RefineConstants.CrossViewKnownBelow;
            }
        });
        return (t, c);
    }

    // ------------------------------------------------------------------------------------------------ the sky

    /// <summary>
    /// The sky, from the network and three checks.
    /// <para>A sky candidate is a pixel where the whole-picture pass sees the farthest thing a picture can hold: a value close to zero
    /// against the near end of the picture. The candidates lose every pixel where the tiled map sees something clearly nearer than the
    /// sky, and every pixel on a thin line of the picture (a wire, a mast, an antenna), which keeps its depth. Then each connected area
    /// that is left is sky only when the median of the camera's depth on it is far: the network gives a far wall, a floor or a dark
    /// background of a room the same value as the sky, but the camera's depth there is near.</para>
    /// </summary>
    /// <returns>The candidates (null when there are none) and the sky (null when there is none).</returns>
    internal static (bool[]? Candidates, bool[]? Sky) Sky(float[] depth, float[] brightness, bool[] valid, DepthPriorMaps prior, int w, int h)
    {
        long n = (long)w * h;
        float near = (float)Percentile(prior.Whole, w, h, 4, RefineConstants.SkyNearPercentile);
        float limit = RefineConstants.SkyNetworkMax * near;
        var candidates = new bool[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) candidates[o + x] = prior.Whole[o + x] < limit; });
        // the tiled value of the sky: the median over the candidates
        var ct = new List<float>();
        foreach (int step in new[] { 4, 1 })
        {
            for (int y = 0; y < h; y += step) { long o = (long)y * w; for (int x = 0; x < w; x += step) if (candidates[o + x]) ct.Add(prior.Tiled[o + x]); }
            if (ct.Count > 0) break;
        }
        if (ct.Count == 0) return (null, null);
        var ta = ct.ToArray();
        float skyValue = (float)RefineOps.Median(ta, ta.Length);
        float trim = skyValue + RefineConstants.SkyTrimRange * ((float)Percentile(prior.Tiled, w, h, 4, RefineConstants.SkyTrimPercentile) - skyValue);
        var sky = new bool[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) sky[o + x] = candidates[o + x] && valid[o + x] && !(prior.Tiled[o + x] > trim); });

        // thin lines of the picture are never sky
        var lines = ThinLines(brightness, w, h);
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) if (lines[o + x]) sky[o + x] = false; });

        // each connected area needs far camera depth
        var (labels, sizes) = RefineOps.Components(sky, w, h);
        int count = sizes.Length - 1;
        if (count == 0) return (candidates, null);
        var start = new long[count + 2];
        for (int k = 1; k <= count; k++) start[k + 1] = start[k] + sizes[k];
        var values = new float[start[count + 1]]; var at = (long[])start.Clone();
        for (long i = 0; i < n; i++) if (labels[i] != 0) values[at[labels[i]]++] = Math.Min(depth[i], RefineConstants.FarDepth);
        var keep = new bool[count + 1];
        Parallel.For(1, count + 1, k =>
        {
            int len = sizes[k]; var v = new float[len];
            Array.Copy(values, start[k], v, 0, len);
            keep[k] = RefineOps.Median(v, len) >= RefineConstants.SkyMinMedianDepth;
        });
        bool any = false;
        for (long i = 0; i < n; i++) { if (sky[i] && !keep[labels[i]]) sky[i] = false; any |= sky[i]; }
        return (candidates, any ? sky : null);
    }

    /// <summary>The pixels on thin, sharp structures of the picture, grown by <see cref="RefineConstants.SkyLineGrow"/> pixels: where the
    /// brightness differs from its blur, in connected groups of some size. A soft cloud edge has too little contrast, and the noise of
    /// a night sky makes only small groups.</summary>
    internal static bool[] ThinLines(float[] brightness, int w, int h)
    {
        long n = (long)w * h;
        var blur = RefineOps.GaussianBlur(brightness, w, h, RefineOps.GaussianKernel(RefineConstants.SkyLineSigma));
        var sharp = new bool[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) sharp[o + x] = Math.Abs(brightness[o + x] - blur[o + x]) > RefineConstants.SkyLineContrast; });
        var (labels, sizes) = RefineOps.Components(sharp, w, h);
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) sharp[o + x] = labels[o + x] != 0 && sizes[labels[o + x]] >= RefineConstants.SkyLineMinPixels; });
        return RefineOps.Grow(sharp, w, h, RefineConstants.SkyLineGrow);
    }

    /// <summary>A percentile of a plane, from every <paramref name="step"/>-th pixel of every <paramref name="step"/>-th row.</summary>
    static double Percentile(float[] v, int w, int h, int step, double p)
    {
        var s = new float[(long)((w + step - 1) / step) * ((h + step - 1) / step)]; int k = 0;
        for (int y = 0; y < h; y += step) { long o = (long)y * w; for (int x = 0; x < w; x += step) s[k++] = v[o + x]; }
        Array.Sort(s, 0, k);
        return RefineOps.PercentileSorted(s, k, p);
    }

    // ------------------------------------------------------------------------------------------------ network values to disparity

    /// <summary>One rising curve from network value to disparity: bins of equal count over the trusted pixels, the median disparity of
    /// each bin, made non-decreasing (neighbors that fall are pooled by weight). Null with too few trusted pixels.</summary>
    internal static (double[] U, double[] D)? Curve(float[] u, float[] d0, bool[] trusted, int w, int h)
    {
        int step = RefineConstants.CurveStep, bins = RefineConstants.CurveBins;
        var a = new List<float>(); var b = new List<float>();
        for (int y = 0; y < h; y += step)
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x += step) if (trusted[o + x]) { a.Add(u[o + x]); b.Add(d0[o + x]); }
        }
        int n = a.Count;
        if (n < RefineConstants.CurveMinSamples) return null;
        var ua = a.ToArray(); var da = b.ToArray();
        Array.Sort(ua, da);
        var ku = new double[bins]; var kd = new double[bins]; var kw = new double[bins];
        int at = 0; var scratch = new float[n / bins + 1];
        for (int i = 0; i < bins; i++)
        {
            int len = n / bins + (i < n % bins ? 1 : 0);
            ku[i] = (len & 1) == 1 ? ua[at + len / 2] : 0.5 * ((double)ua[at + len / 2 - 1] + ua[at + len / 2]);
            Array.Copy(da, at, scratch, 0, len);
            kd[i] = RefineOps.Median(scratch, len); kw[i] = len; at += len;
        }
        // pool neighbors that fall
        var vals = new List<double>(); var wts = new List<double>(); var cnt = new List<int>();
        for (int i = 0; i < bins; i++)
        {
            vals.Add(kd[i]); wts.Add(kw[i]); cnt.Add(1);
            while (vals.Count > 1 && vals[^2] > vals[^1])
            {
                double v2 = vals[^1], w2 = wts[^1]; int c2 = cnt[^1];
                vals.RemoveAt(vals.Count - 1); wts.RemoveAt(wts.Count - 1); cnt.RemoveAt(cnt.Count - 1);
                vals[^1] = (vals[^1] * wts[^1] + v2 * w2) / (wts[^1] + w2); wts[^1] += w2; cnt[^1] += c2;
            }
        }
        int k = 0;
        for (int i = 0; i < vals.Count; i++) for (int j = 0; j < cnt[i]; j++) kd[k++] = vals[i];
        return (ku, kd);
    }

    /// <summary>The curve applied to a plane: straight lines between the bins, and beyond the ends the slope of the outer quarter.</summary>
    internal static float[] ApplyCurve(float[] u, double[] ku, double[] kd, int w, int h)
    {
        int nb = ku.Length, q = Math.Max(nb / 4, 2);
        double hi = (kd[nb - 1] - kd[nb - q]) / Math.Max(ku[nb - 1] - ku[nb - q], 1e-9), lo = (kd[q - 1] - kd[0]) / Math.Max(ku[q - 1] - ku[0], 1e-9);
        var dst = new float[(long)w * h];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                double v = u[o + x], r;
                if (v > ku[nb - 1]) r = kd[nb - 1] + hi * (v - ku[nb - 1]);
                else if (v < ku[0]) r = kd[0] + lo * (v - ku[0]);
                else
                {
                    int a = 0, b = nb - 1;   // the last bin at or below v
                    while (a < b) { int m = (a + b + 1) >> 1; if (ku[m] <= v) a = m; else b = m - 1; }
                    r = a == nb - 1 || ku[a + 1] == ku[a] ? kd[a] : kd[a] + (kd[a + 1] - kd[a]) * (v - ku[a]) / (ku[a + 1] - ku[a]);
                }
                dst[o + x] = (float)Math.Max(r, 0.0);
            }
        });
        return dst;
    }

    /// <summary>The curve cannot follow the network's drift across the picture. A shift per cell corrects it: the median difference between
    /// the camera's disparity and the mapped network over the trusted pixels of the cell. It is a median, so a cell where the camera fills
    /// the gaps between bars still gets the shift of its correct majority. Returns mapped + shift.</summary>
    internal static float[] LocalShift(float[] mapped, float[] d0, bool[] trusted, int w, int h)
    {
        int cell = RefineConstants.ShiftCell, st = cell / 2, sub = RefineConstants.ShiftStep;
        int ny = (h + st - 1) / st, nx = (w + st - 1) / st;
        var c = new float[nx * ny]; var has = new bool[nx * ny];
        Parallel.For(0, ny, j =>
        {
            var buf = new float[(cell / sub + 1) * (cell / sub + 1)];
            for (int i = 0; i < nx; i++)
            {
                int cy = j * st + st / 2, cx = i * st + st / 2;
                int y0 = Math.Max(cy - cell / 2, 0), y1 = Math.Min(cy + cell / 2, h), x0 = Math.Max(cx - cell / 2, 0), x1 = Math.Min(cx + cell / 2, w);
                int k = 0;
                for (int y = y0; y < y1; y += sub)
                {
                    long o = (long)y * w;
                    for (int x = x0; x < x1; x += sub) if (trusted[o + x]) buf[k++] = d0[o + x] - mapped[o + x];
                }
                if (k < RefineConstants.ShiftMinSamples) continue;
                c[j * nx + i] = (float)RefineOps.Median(buf, k); has[j * nx + i] = true;
            }
        });
        // an empty cell takes the shift of the nearest filled one; with no filled cell the shift is zero
        if (has.Any(x => x))
            while (has.Any(x => !x))
            {
                var c2 = (float[])c.Clone(); var h2 = (bool[])has.Clone();
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int p = j * nx + i;
                        if (has[p]) continue;
                        if (i > 0 && has[p - 1]) { c2[p] = c[p - 1]; h2[p] = true; }
                        else if (i < nx - 1 && has[p + 1]) { c2[p] = c[p + 1]; h2[p] = true; }
                        else if (j > 0 && has[p - nx]) { c2[p] = c[p - nx]; h2[p] = true; }
                        else if (j < ny - 1 && has[p + nx]) { c2[p] = c[p + nx]; h2[p] = true; }
                    }
                c = c2; has = h2;
            }
        var field = RefineOps.ResizeCubic(RefineOps.Median3(c, nx, ny), nx, ny, nx * st, ny * st);
        var dst = new float[(long)w * h]; int fw = nx * st;
        RefineOps.Rows(h, y => { long o = (long)y * w, f = (long)y * fw; for (int x = 0; x < w; x++) dst[o + x] = Math.Max(mapped[o + x] + field[f + x], 0f); });
        return dst;
    }

    // ------------------------------------------------------------------------------------------------ relief check

    /// <summary>Compares the relief of the camera's depth with the network's inside a window, without regard to their absolute values:
    /// the place of each pixel between the local minimum and maximum. A pixel is inconsistent where the network has relief (a range above
    /// one depth step) and the two places differ by more than <see cref="RefineConstants.ReliefMaxDifference"/> of the range, or the
    /// camera's depth is flat while the pixel sits in the near part of the network's relief (a near object the camera's depth does not
    /// contain).</summary>
    internal static bool[] ReliefConsistent(float[] d0, float[] mapped, int w, int h)
    {
        int q = RefineConstants.ReliefReduction, r = Math.Max(RefineConstants.ReliefRadius / q, 1), qw = w / q, qh = h / q;
        float[] Local(float[] src, bool max) => RefineOps.ResizeBilinear(RefineOps.Disk(RefineOps.Pool(src, w, h, q, max), qw, qh, r, max), qw, qh, w, h);
        var gmin = Local(mapped, false); var gmax = Local(mapped, true); var dmin = Local(d0, false); var dmax = Local(d0, true);
        var ok = new bool[(long)w * h];
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x;
                float grng = gmax[i] - gmin[i], drng = dmax[i] - dmin[i];
                float en = (mapped[i] - gmin[i]) / Math.Max(grng, 1e-3f), es = (d0[i] - dmin[i]) / Math.Max(drng, 1e-3f);
                bool relief = grng > Step(gmax[i]), flat = drng < Step(dmax[i]);
                bool bad = relief && (flat ? en > RefineConstants.ReliefFlatMaxPosition : Math.Abs(en - es) > RefineConstants.ReliefMaxDifference);
                ok[i] = !bad;
            }
        });
        return ok;
    }

    // ------------------------------------------------------------------------------------------------ the values in the band

    /// <summary>Each band pixel takes the mean of the camera samples nearby whose mapped network disparity is like its own: a joint
    /// bilateral filter, computed in layers of the logarithm of the mapped disparity on a reduced grid. Where the samples give little
    /// support, the mapped network disparity takes over in proportion.</summary>
    static float[] FillBand(float[] d0, bool[] source, bool[] band, float[] local, int w, int h, CancellationToken cancel)
    {
        long n = (long)w * h; int ds = RefineConstants.FilterReduction, qw = w / ds, qh = h / ds;
        float sr = RefineConstants.FilterRangeSigma;
        var logd = new float[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) logd[o + x] = WineCrt.Logf(Math.Max(local[o + x], RefineConstants.MinLogDisparity)); });
        var wsrc = new float[n]; var vsrc = new float[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) if (source[o + x]) { wsrc[o + x] = 1f; vsrc[o + x] = d0[o + x]; } });
        var ls = RefineOps.BlockMean(logd, w, h, ds); var ws = RefineOps.BlockMean(wsrc, w, h, ds); var vs = RefineOps.BlockMean(vsrc, w, h, ds);
        for (int i = 0; i < vs.Length; i++) vs[i] /= Math.Max(ws[i], 1e-6f);

        // the band pixels and their layer
        var index = new List<long>();
        for (long i = 0; i < n; i++) if (band[i]) index.Add(i);
        int nb = index.Count;
        var sorted = new float[nb];
        for (int i = 0; i < nb; i++) sorted[i] = logd[index[i]];
        Array.Sort(sorted);
        float lo = (float)RefineOps.PercentileSorted(sorted, nb, RefineConstants.FilterLowPercentile) - sr;
        float hi = (float)RefineOps.PercentileSorted(sorted, nb, RefineConstants.FilterHighPercentile) + sr;
        int levels = Math.Max(2, (int)Math.Ceiling((hi + sr - lo) / sr));
        var level = new float[levels];
        for (int k = 0; k < levels; k++) level[k] = lo + k * sr;
        var layer = new int[nb]; var frac = new float[nb]; var cell = new int[nb];
        var count = new int[levels + 1];
        for (int i = 0; i < nb; i++)
        {
            long p = index[i]; float l = logd[p];
            int k = Math.Clamp((int)((l - lo) / sr), 0, levels - 2);
            layer[i] = k; frac[i] = Math.Clamp((l - level[k]) / sr, 0f, 1f);
            cell[i] = Math.Min((int)(p / w) / ds, qh - 1) * qw + Math.Min((int)(p % w) / ds, qw - 1);
            count[k + 1]++;
        }
        for (int k = 0; k < levels; k++) count[k + 1] += count[k];
        var order = new int[nb]; var fill = (int[])count.Clone();
        for (int i = 0; i < nb; i++) order[fill[layer[i]]++] = i;

        // each band pixel reads two neighboring layers; each layer is two Gaussian blurs on the reduced grid
        var num0 = new float[nb]; var den0 = new float[nb]; var num1 = new float[nb]; var den1 = new float[nb];
        var kernel = RefineOps.GaussianKernel(RefineConstants.FilterSpatialSigma);
        Parallel.For(0, levels, new ParallelOptions { CancellationToken = cancel }, k =>
        {
            bool own = count[k + 1] > count[k], below = k > 0 && count[k] > count[k - 1];
            if (!own && !below) return;
            var rk = new float[ls.Length]; var rv = new float[ls.Length];
            for (int i = 0; i < ls.Length; i++)
            {
                float t = (ls[i] - level[k]) / sr;
                rk[i] = WineCrt.Expf(-0.5f * t * t) * ws[i]; rv[i] = rk[i] * vs[i];
            }
            var nk = RefineOps.GaussianBlur(rv, qw, qh, kernel, parallel: false); var dk = RefineOps.GaussianBlur(rk, qw, qh, kernel, parallel: false);
            for (int j = count[k]; j < count[k + 1]; j++) { int i = order[j]; num0[i] = nk[cell[i]]; den0[i] = dk[cell[i]]; }
            if (below) for (int j = count[k - 1]; j < count[k]; j++) { int i = order[j]; num1[i] = nk[cell[i]]; den1[i] = dk[cell[i]]; }
        });

        var fused = new float[n];
        Parallel.For(0, nb, i =>
        {
            float f = frac[i];
            float num = (1f - f) * num0[i] + f * num1[i], den = (1f - f) * den0[i] + f * den1[i];
            float val = num / Math.Max(den, 1e-9f), a = den / (den + RefineConstants.FilterMinSupport);
            fused[index[i]] = a * val + (1f - a) * local[index[i]];
        });
        return fused;
    }
}
