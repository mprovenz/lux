namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The float pyramid primitives of `lt::A::CreateAndBlendLaplacianPyramids`: `lt::ImageGaussianFilterAndSubSample&lt;float&gt;`
/// `180014620` (tile lambda `180016490`, vertical helper `180016af0`), `lt::ImageGaussianUpscaleAndSubtract&lt;float&gt;`
/// `180014e70` (tile lambda `1800181c0`, even-row helper `1800186c0`), and the three vector helpers
/// `FUN_180015160` / `FUN_180015720` / `FUN_180015aa0`. All borders replicate; every level is `(n+1) &gt;&gt; 1`.
/// The float associations differ between the SIMD bodies, the scalar tails and the clamped edges — each one is
/// transcribed from the disassembly and is load-bearing. Spec `a-display-isp.md` §10a.4(c).
/// </summary>
public static class LaplacianPyramid
{
    // analysis kernel 0x180681fc0..fd0
    static readonly float K0 = BitConverter.Int32BitsToSingle(0x3d4ccccd);   // 0.05
    static readonly float K1 = BitConverter.Int32BitsToSingle(0x3e800000);   // 0.25
    static readonly float K2 = BitConverter.Int32BitsToSingle(0x3ecccccd);   // 0.40
    static readonly float K3 = BitConverter.Int32BitsToSingle(0x3e800000);   // 0.25
    static readonly float K4 = BitConverter.Int32BitsToSingle(0x3d4ccccd);   // 0.05
    // synthesis constants 0x180681ed0/d4/d8 and 0x180681edc/ee0/ee4 (+ the 4-wide copies ef0/f00/f10, same bits)
    static readonly float U25 = BitConverter.Int32BitsToSingle(0x3e800000);  // 0.25
    static readonly float U05 = BitConverter.Int32BitsToSingle(0x3d4ccccd);  // 0.05
    static readonly float U40 = BitConverter.Int32BitsToSingle(0x3ecccccd);  // 0.40
    static readonly float U01 = BitConverter.Int32BitsToSingle(0x3c23d70b);  // 0.010000000707805157 — NOT (float)0.01
    static readonly float U08 = BitConverter.Int32BitsToSingle(0x3da3d70b);  // 0.08000000566244125
    static readonly float U64 = BitConverter.Int32BitsToSingle(0x3f23d70b);  // 0.64000004529953

    /// <summary>`(k4·a4) + ((k3·a3 + k2·a2) + (k1·a1 + k0·a0))` — the vertical 4-wide body and every clamped row,
    /// and the horizontal clamped head/tail.</summary>
    static float Assoc1(float a0, float a1, float a2, float a3, float a4)
        => (K4 * a4) + ((K3 * a3 + K2 * a2) + (K1 * a1 + K0 * a0));
    /// <summary>`((k4·a4) + (k1·a1 + k0·a0)) + (k3·a3 + k2·a2)` — the vertical scalar tail and the horizontal 8-wide body.</summary>
    static float Assoc2(float a0, float a1, float a2, float a3, float a4)
        => ((K4 * a4) + (K1 * a1 + K0 * a0)) + (K3 * a3 + K2 * a2);
    /// <summary>`((k4·v4) + (k2·v2 + k0·v0)) + (k3·v3 + k1·v1)` — the horizontal unclamped step-2 remainder.</summary>
    static float Assoc5(float a0, float a1, float a2, float a3, float a4)
        => ((K4 * a4) + (K2 * a2 + K0 * a0)) + (K3 * a3 + K1 * a1);

    public static Image<float> NewLevel(Image<float> src) => new Image<float>((src.Width + 1) >> 1, (src.Height + 1) >> 1);

    /// <summary>`lt::ImageGaussianFilterAndSubSample&lt;float&gt;` `180014620`: separable, **vertical first**, even-phase
    /// decimation. `Tiler::Run(0, src.h, 16)` bands of source rows; inside a band the x is walked in blocks of 32,
    /// the vertical helper fills a per-row scratch line (which persists across blocks) and the horizontal pass emits
    /// `dst[y&gt;&gt;1][x&gt;&gt;1]` in four segments: clamped head, 8-wide body, unclamped step-2 remainder, clamped tail.</summary>
    public static void PyrDown(Image<float> dst, Image<float> src)
    {
        int W = src.Width, H = src.Height;
        int rx0 = 0, rx1 = W, ry0 = 0, ry1 = H;              // src.rect relative to the buffer (no halo on the display path)
        int loX = Math.Max(rx0, -2);                          // uVar25
        int hiXExcl = Math.Min(rx1, W + 2);                   // local_90
        int clampHiX = hiXExcl - 1;
        // one scratch line per even row of the band (`lVar26 += scratch.stride` per row): a line PERSISTS across the
        // x-blocks of its row, which is what makes block `xb`'s reads of columns `xb−4 … xb−1` well defined.
        int slw = W + 8;
        var scratch = new float[16 * slw];                    // 2 floats of left padding (the `+8` of local_78)
        const int Pad = 2;
        foreach (var (by0, by1) in Tiler.Ranges(0, H, 16))
        {
            for (int xb = loX; xb < hiXExcl + 2; xb += 32)
            {
                int v0 = Math.Min(xb, hiXExcl), v1 = Math.Min(xb + 32, hiXExcl);
                int hi = Math.Min(Math.Max(xb + 30, 0), W);
                int xlo = Math.Min(Math.Max(xb - 2, 0), W);
                int headStart = Math.Min(xlo, hi);
                int headEnd = Math.Min(Math.Max(loX + 2, xlo), hi);
                int rem0 = Math.Min(Math.Max(hiXExcl - 2, xlo), hi);
                int tailEnd = Math.Min(Math.Max(xlo, hiXExcl), hi);
                int simdEnd = headEnd + ((rem0 - headEnd) & ~7);
                for (int y = by0; y < by1; y += 2)
                {
                    int b = ((y - by0) >> 1) * slw + Pad;
                    VerticalRow(scratch, b, src, v0, v1, y, ry0, ry1);
                    var drow = dst.Row(y >> 1);
                    int x = headStart;
                    for (; x < headEnd; x += 2) drow[x >> 1] = HClamped(scratch, b, x, loX, clampHiX);
                    if (x < simdEnd)
                        for (; x < simdEnd; x += 8)
                        {
                            float a0 = scratch[b + x - 2], a1 = scratch[b + x - 1], a2 = scratch[b + x], a3 = scratch[b + x + 1];
                            float b0 = scratch[b + x + 2], b1 = scratch[b + x + 3], b2 = scratch[b + x + 4], b3 = scratch[b + x + 5];
                            float c0 = scratch[b + x + 6], c1 = scratch[b + x + 7], c2 = scratch[b + x + 8];
                            int o = x >> 1;
                            drow[o] = Assoc2(a0, a1, a2, a3, b0);
                            drow[o + 1] = Assoc2(a2, a3, b0, b1, b2);
                            drow[o + 2] = Assoc2(b0, b1, b2, b3, c0);
                            drow[o + 3] = Assoc2(b2, b3, c0, c1, c2);
                        }
                    if (x < rem0)
                        for (; x < rem0; x += 2)
                            drow[x >> 1] = Assoc5(scratch[b + x - 2], scratch[b + x - 1], scratch[b + x], scratch[b + x + 1], scratch[b + x + 2]);
                    while (x < tailEnd) { drow[x >> 1] = HClamped(scratch, b, x, loX, clampHiX); x += 2; }
                }
            }
        }
    }

    static float HClamped(float[] s, int pad, int x, int lo, int hi)
    {
        int i0 = Math.Min(Math.Max(x - 2, lo), hi), i1 = Math.Min(Math.Max(x - 1, lo), hi), i2 = Math.Min(Math.Max(x, lo), hi);
        int i3 = Math.Min(Math.Max(x + 1, lo), hi), i4 = Math.Min(Math.Max(x + 2, lo), hi);
        return Assoc1(s[pad + i0], s[pad + i1], s[pad + i2], s[pad + i3], s[pad + i4]);
    }

    /// <summary>`FUN_180016af0(scratchRow, src, x0, x1, y, K)`: rows `y−2 … y+2`. A row whose taps all lie inside
    /// `[rect.y0, rect.y1)` uses the 4-wide association for the first `(x1−x0) &amp; ~3` columns and the tail
    /// association for the rest; a row needing a clamp is fully scalar with the 4-wide association.</summary>
    static void VerticalRow(float[] scratch, int pad, Image<float> src, int x0, int x1, int y, int ry0, int ry1)
    {
        if (x1 <= x0) return;
        if (ry0 + 2 > y || ry1 - 2 <= y)
        {
            int hi = ry1 - 1;
            int r0 = Math.Min(Math.Max(y - 2, ry0), hi), r1 = Math.Min(Math.Max(y - 1, ry0), hi), r2 = Math.Min(Math.Max(y, ry0), hi);
            int r3 = Math.Min(Math.Max(y + 1, ry0), hi), r4 = Math.Min(Math.Max(y + 2, ry0), hi);
            var s0 = src.Row(r0); var s1 = src.Row(r1); var s2 = src.Row(r2); var s3 = src.Row(r3); var s4 = src.Row(r4);
            for (int x = x0; x < x1; x++) scratch[pad + x] = Assoc1(s0[x], s1[x], s2[x], s3[x], s4[x]);
            return;
        }
        var a0 = src.Row(y - 2); var a1 = src.Row(y - 1); var a2 = src.Row(y); var a3 = src.Row(y + 1); var a4 = src.Row(y + 2);
        int n4 = (x1 - x0) & ~3, split = x0 + n4;
        for (int x = x0; x < split; x++) scratch[pad + x] = Assoc1(a0[x], a1[x], a2[x], a3[x], a4[x]);
        for (int x = split; x < x1; x++) scratch[pad + x] = Assoc2(a0[x], a1[x], a2[x], a3[x], a4[x]);
    }

    /// <summary>`lt::ImageGaussianUpscaleAndSubtract&lt;float&gt;` `180014e70`: `dst = up(low) − sub`, `dst` sized to `sub`.
    /// 2-D `Tiler::Run(rect, {256,256})`; rows are walked in (even, odd) pairs, a trailing even row alone.</summary>
    public static void PyrUpSub(Image<float> dst, Image<float> low, Image<float> sub)
    {
        foreach (var t in Tiler.Rects(new RectI(0, 0, dst.Width, dst.Height), 256, 256))
        {
            int yEven = t.Y1 & ~1;
            int y = t.Y0;
            for (; y < yEven; y += 2) { EvenRow(dst, low, sub, y, t.X0, t.X1); OddRow(dst, low, sub, y + 1, t.X0, t.X1); }
            if (yEven != t.Y1) EvenRow(dst, low, sub, yEven, t.X0, t.X1);
        }
    }

    /// <summary>The shared SIMD window: `start = ((x0+1) &amp; ~1) + (lowX0 − min(((x0+1)&gt;&gt;1) − 1, lowX0))·2`,
    /// `end = (x1 &amp; ~1) + (lowX1 − max((x1&gt;&gt;1) + 1, lowX1))·2`, clamped and truncated to a multiple of 8.</summary>
    static (int Start, int End) SimdRange(int x0, int x1, int lowX0, int lowX1)
    {
        int start = ((x0 + 1) & ~1) + (lowX0 - Math.Min(((x0 + 1) >> 1) - 1, lowX0)) * 2;
        int end = (x1 & ~1) + (lowX1 - Math.Max((x1 >> 1) + 1, lowX1)) * 2;
        if (start > x1) start = x1;
        if (end < start) end = start;
        return (start, start + ((end - start) & ~7));
    }

    static void EvenRow(Image<float> dst, Image<float> low, Image<float> sub, int Y, int x0, int x1)
    {
        int r = Y >> 1, lh = low.Height, lw = low.Width;
        var A = low.Row(Math.Min(Math.Max(r - 1, 0), lh - 1));
        var C = low.Row(r);
        var B = low.Row(Math.Min(r + 1, lh - 1));
        var d = dst.Row(Y); var s = sub.Row(Y);
        var (ss, se) = SimdRange(x0, x1, 0, lw);
        for (int X = x0; X < x1; X++)
        {
            if (X == ss && se > ss)
            {
                for (; X < se; X += 8)
                {
                    int c = X >> 1;
                    for (int j = 0; j < 4; j++)
                    {
                        int k = c + j;
                        float Sm = B[k - 1] + A[k - 1], Sk = B[k] + A[k], Sp = B[k + 1] + A[k + 1];
                        d[X + 2 * j] = (((C[k] * U64) + ((Sm + Sp) * U01)) + (((C[k - 1] + C[k + 1]) + Sk) * U08)) - s[X + 2 * j];
                        d[X + 2 * j + 1] = (((C[k + 1] + C[k]) * U40) + ((Sk + Sp) * U05)) - s[X + 2 * j + 1];
                    }
                }
                X--; continue;
            }
            int cc = X >> 1, cR = Math.Min(cc + 1, lw - 1);
            float u;
            if ((X & 1) == 0)
            {
                int cL = Math.Max(cc - 1, 0);
                u = (C[cc] * U64) + (((((B[cc] + A[cc]) + C[cL]) + C[cR]) * U08) + ((((A[cR] + A[cL]) + B[cL]) + B[cR]) * U01));
            }
            else u = ((C[cR] + C[cc]) * U40) + ((((A[cR] + A[cc]) + B[cc]) + B[cR]) * U05);
            d[X] = u - s[X];
        }
    }

    static void OddRow(Image<float> dst, Image<float> low, Image<float> sub, int Y, int x0, int x1)
    {
        int r = Y >> 1, lh = low.Height, lw = low.Width;
        var P = low.Row(r);
        var Q = low.Row(Math.Min(r + 1, lh - 1));
        var d = dst.Row(Y); var s = sub.Row(Y);
        var (ss, se) = SimdRange(x0, x1, 0, lw);
        for (int X = x0; X < x1; X++)
        {
            if (X == ss && se > ss)
            {
                for (; X < se; X += 8)
                {
                    int c = X >> 1;
                    for (int j = 0; j < 4; j++)
                    {
                        int k = c + j;
                        float Sm = Q[k - 1] + P[k - 1], Sk = Q[k] + P[k], Sp = Q[k + 1] + P[k + 1];
                        d[X + 2 * j] = ((Sk * U40) + ((Sm + Sp) * U05)) - s[X + 2 * j];
                        d[X + 2 * j + 1] = ((Sp + Sk) * U25) - s[X + 2 * j + 1];
                    }
                }
                X--; continue;
            }
            int cc = X >> 1, cR = Math.Min(cc + 1, lw - 1);
            float u;
            if ((X & 1) == 0)
            {
                int cL = Math.Max(cc - 1, 0);
                u = ((Q[cc] + P[cc]) * U40) + ((((P[cR] + P[cL]) + Q[cL]) + Q[cR]) * U05);
            }
            else u = (((P[cR] + P[cc]) + Q[cc]) + Q[cR]) * U25;
            d[X] = u - s[X];
        }
    }

    /// <summary>`FUN_180015160(gauss, lap, src)`: `gauss[i] = PyrDown(cur)`, `lap[i] = PyrUp(gauss[i]) − cur`
    /// (a **negated** Laplacian), stopping when `gauss` is full; `lap.back() = gauss.back()`.</summary>
    public static void BuildPyramids(Image<float>[] gauss, Image<float>[] lap, Image<float> src)
    {
        if (src.Width <= 0 || src.Height <= 0) throw new InvalidOperationException("empty input!");
        if (gauss.Length != lap.Length - 1) throw new InvalidOperationException("gaussian/laplacian pyramid size mismatch!");
        if (lap.Length == 1) { lap[0] = src.Copy(); return; }
        var cur = src; int i = 0, n;
        while (true)
        {
            gauss[i] = NewLevel(cur);
            PyrDown(gauss[i], cur);
            lap[i] = new Image<float>(cur.Width, cur.Height);
            PyrUpSub(lap[i], gauss[i], cur);
            n = i + 1;
            if (n >= gauss.Length) break;
            cur = gauss[i]; i = n;
            if (!(gauss[i - 1].Width > 1 || gauss[i - 1].Height > 1)) break;
        }
        lap[n] = gauss[n - 1].Copy();
    }

    /// <summary>`FUN_180015720(pyr, src)`: the level count comes from `pyr.size()`; in place —
    /// `pyr[k] = PyrDown(pyr[k−1])` then `pyr[k−1] = PyrUp(pyr[k]) − pyr[k−1]` (with `pyr[-1] ≡ src`),
    /// leaving the coarsest Gaussian in `pyr[n−1]`.</summary>
    public static void BuildLaplacian(Image<float>[] pyr, Image<float> src)
    {
        if (src.Width <= 0 || src.Height <= 0) throw new InvalidOperationException("empty input!");
        int n = pyr.Length;
        if (n == 1) { pyr[0] = src.Copy(); return; }
        if (n < 2) return;
        if (!(src.Width > 1 || src.Height >= 2)) return;
        var cur = src;
        for (int i = 1; i < n; i++)
        {
            pyr[i] = NewLevel(cur);
            PyrDown(pyr[i], cur);
            var lapLevel = new Image<float>(cur.Width, cur.Height);
            PyrUpSub(lapLevel, pyr[i], cur);
            pyr[i - 1] = lapLevel;
            if (i + 1 >= n) break;
            cur = pyr[i];
            if (!(pyr[i].Width > 1 || pyr[i].Height > 1)) break;
        }
    }

    /// <summary>`FUN_180015aa0(out, lap, start)`: `A = lap.back()`, `A = PyrUp(A) − lap[i]` for
    /// `i = size−2 … start+1`, `out = PyrUp(A) − lap[start]`.</summary>
    public static Image<float> Collapse(IReadOnlyList<Image<float>> lap, int start)
    {
        int n = lap.Count;
        if (n == 0) throw new InvalidOperationException("empty input pyramid!");
        if (start < 0 || start >= n) throw new InvalidOperationException("invalid integration stop level!");
        if (start == n - 1) return lap[n - 1].Copy();
        var a = lap[n - 1].Copy();
        for (int i = n - 2; i > start; i--)
        {
            var t = new Image<float>(lap[i].Width, lap[i].Height);
            PyrUpSub(t, a, lap[i]);
            a = t;
        }
        var outImg = new Image<float>(lap[start].Width, lap[start].Height);
        PyrUpSub(outImg, a, lap[start]);
        return outImg;
    }
}
