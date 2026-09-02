using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The vec4 Gaussian/Laplacian pyramid of `lt::Internal::ColorNoiseReduction`: `ImageGaussianFilterAndSubSample`
/// (`1800147f0`, tile lambda `1800170c0`, vertical helper `FUN_180017660`) — 5-tap [0.05,0.25,0.4,0.25,0.05],
/// vertical then horizontal, edge-clamped, keeping even rows/columns — and `ImageGaussianUpscaleAndSubtract`
/// (`180014f60`, odd rows lambda `180018c10`, even rows `FUN_180019080`) — out = upscale(small) − target with the
/// folded 2× weights 0.64/0.08/0.01 (even row, even col), 0.4/0.05 (one odd index) and 0.25 (both odd).
/// Float association follows the SSE code path per region (edge vs. interior), see the comments.
/// </summary>
public static class CnrPyramid
{
    const float K0 = 0.05f, K1 = 0.25f, K2 = 0.4f;   // DAT_180681ea0 / eb0 / ec0
    const float W64 = 0.64000004f, W08 = 0.080000006f, W01 = 0.010000001f;   // DAT_180681f10 / f00 / ef0

    static Vec4F Mul(float k, Vec4F v) => new(k * v.R, k * v.G, k * v.B, k * v.A);
    static Vec4F Add(Vec4F a, Vec4F b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    static Vec4F Sub(Vec4F a, Vec4F b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);

    /// <summary>Down-sampled size ((w+1)/2, (h+1)/2) image of the 5-tap filtered even sites.</summary>
    public static Vec4F[] Downsample(Vec4F[] src, int w, int h, out int w2, out int h2) => Downsample(src, w, h, 0, 0, w, h, out w2, out h2);

    /// <summary>
    /// Down-sample the w×h view at (vx, vy) of an ew×eh extent image (row stride <paramref name="ew"/>). Lumen's tile lambda
    /// (`1800170c0`) clamps the horizontal taps to `[max(rect.x0, −2), min(rect.x1, w+2))` and the vertical helper
    /// (`FUN_180017660`) to the parent rect `[rect.y0, rect.y1)`, i.e. real data up to 2 px beyond the view is used when the
    /// parent image has it; the clamped ("edge") association applies only where the extent actually ends
    /// (`x &lt; xlo+2 || x ≥ xhi−2`, `y &lt; y0+2 || y ≥ y1−2`). The caller passes the extent = parent ∩ (view ± 2).
    /// </summary>
    public static Vec4F[] Downsample(Vec4F[] ext, int ew, int eh, int vx, int vy, int w, int h, out int w2, out int h2)
    {
        w2 = (w + 1) >> 1; h2 = (h + 1) >> 1;
        var dst = new Vec4F[w2 * h2];
        var tmp = new Vec4F[ew];
        int xlo = -vx, xhi = ew - vx, ylo = -vy, yhi = eh - vy;   // extent bounds in view coordinates
        for (int oy = 0; oy < h2; oy++)
        {
            int y = oy * 2;
            int r0 = Math.Clamp(y - 2, ylo, yhi - 1) + vy, r1 = Math.Clamp(y - 1, ylo, yhi - 1) + vy, r2 = Math.Clamp(y, ylo, yhi - 1) + vy, r3 = Math.Clamp(y + 1, ylo, yhi - 1) + vy, r4 = Math.Clamp(y + 2, ylo, yhi - 1) + vy;
            bool vEdge = y < ylo + 2 || y >= yhi - 2;   // FUN_180017660: clamped path when y < y0+2 or y >= y1−2
            for (int x = 0; x < ew; x++)
            {
                Vec4F s0 = Mul(K0, ext[r0 * ew + x]), s1 = Mul(K1, ext[r1 * ew + x]), s2 = Mul(K2, ext[r2 * ew + x]), s3 = Mul(K1, ext[r3 * ew + x]), s4 = Mul(K0, ext[r4 * ew + x]);
                // edge rows: k4s4 + ((k0s0 + k1s1) + (k2s2 + k3s3)); interior: (k4s4 + (k0s0 + k1s1)) + (k2s2 + k3s3)  [verified by brute force vs cp.dll]
                tmp[x] = vEdge ? Add(s4, Add(Add(s0, s1), Add(s2, s3))) : Add(Add(s4, Add(s0, s1)), Add(s2, s3));
            }
            for (int ox = 0; ox < w2; ox++)
            {
                int x = ox * 2;
                int c0 = Math.Clamp(x - 2, xlo, xhi - 1) + vx, c1 = Math.Clamp(x - 1, xlo, xhi - 1) + vx, c2 = Math.Clamp(x, xlo, xhi - 1) + vx, c3 = Math.Clamp(x + 1, xlo, xhi - 1) + vx, c4 = Math.Clamp(x + 2, xlo, xhi - 1) + vx;
                Vec4F s0 = Mul(K0, tmp[c0]), s1 = Mul(K1, tmp[c1]), s2 = Mul(K2, tmp[c2]), s3 = Mul(K1, tmp[c3]), s4 = Mul(K0, tmp[c4]);
                bool hEdge = x < xlo + 2 || x >= xhi - 2;   // lambda 1800170c0 loops 1/3 (clamped) vs the interior loop
                // edge: k4s4 + ((k0s0 + k1s1) + (k2s2 + k3s3)); interior: (k4s4 + (k0s0 + k2s2)) + (k1s1 + k3s3)
                dst[oy * w2 + ox] = hEdge ? Add(s4, Add(Add(s0, s1), Add(s2, s3))) : Add(Add(s4, Add(s0, s2)), Add(s1, s3));
            }
        }
        return dst;
    }

    /// <summary>out(x,y) = upscale(small)(x,y) − target(x,y) for a w×h target (small is ((w+1)/2)×((h+1)/2)).</summary>
    public static Vec4F[] UpscaleSubtract(Vec4F[] small, int ws, int hs, Vec4F[] target, int w, int h)
    {
        var dst = new Vec4F[w * h];
        // interior column range of the pair loop (x0 = 0, x1 = w, small.x0 = 0, small.x1 = ws): lambda L20–45
        int lo = ((0 + 1) & ~1) + 2 * (0 - Math.Min(((0 + 1) >> 1) - 1, 0));
        int hi = (w & ~1) + 2 * (ws - Math.Max((w >> 1) + 1, ws));
        if (w < lo) lo = w; if (hi < lo) hi = lo;
        for (int y = 0; y < h; y++)
        {
            int r = y >> 1;
            bool oddRow = (y & 1) != 0;
            int rm = Math.Max(r - 1, 0), rp = Math.Min(r + 1, hs - 1);
            int ra = oddRow ? r : rm, rb = oddRow ? rp : r, rc = rp;   // odd rows: r0 = r, r1 = r+1; even rows: rm, r, rp
            for (int x = 0; x < w; x++)
            {
                int c = x >> 1; bool oddCol = (x & 1) != 0;
                int cm = Math.Max(c - 1, 0), cp = Math.Min(c + 1, ws - 1);
                bool interior = x >= lo && x < hi;
                Vec4F t = target[y * w + x], v;
                if (!oddRow)
                {
                    Vec4F Rm(int cc) => small[rm * ws + cc]; Vec4F R0(int cc) => small[r * ws + cc]; Vec4F Rp(int cc) => small[rp * ws + cc];
                    if (!oddCol)
                    {
                        var diag = Add(Add(Add(Rm(cp), Rm(cm)), Rp(cm)), Rp(cp));
                        var cross = Add(Add(Add(Rp(c), Rm(c)), R0(cm)), R0(cp));
                        v = Sub(Add(Mul(W64, R0(c)), Add(Mul(W08, cross), Mul(W01, diag))), t);
                    }
                    else
                    {
                        var four = Add(Add(Add(Rm(cp), Rm(c)), Rp(c)), Rp(cp));
                        var pair = Add(R0(cp), R0(c));
                        v = interior ? Add(Sub(Mul(K0, four), t), Mul(K2, pair)) : Sub(Add(Mul(K2, pair), Mul(K0, four)), t);
                    }
                }
                else
                {
                    Vec4F R0(int cc) => small[r * ws + cc]; Vec4F R1(int cc) => small[rp * ws + cc];
                    if (!oddCol)
                    {
                        var four = Add(Add(Add(R0(cp), R0(cm)), R1(cm)), R1(cp));
                        var pair = Add(R1(c), R0(c));
                        v = interior ? Add(Sub(Mul(K0, four), t), Mul(K2, pair)) : Sub(Add(Mul(K2, pair), Mul(K0, four)), t);
                    }
                    else
                    {
                        var four = Add(Add(Add(R0(cp), R0(c)), R1(c)), R1(cp));
                        v = Sub(Mul(K1, four), t);
                    }
                }
                dst[y * w + x] = v;
            }
        }
        return dst;
    }

    static Vec4F Mul(float k, in Vec4F v, int _ = 0) => Mul(k, v);
}

/// <summary>Eigen 3.3 `JacobiSVD&lt;Matrix3d&gt;(ComputeFullU|ComputeFullV)` as compiled into cp.dll
/// (`FUN_1803c78b0`, 2×2 kernel `FUN_1803c8410`): scale by the max |coefficient| (multiply by its reciprocal),
/// one-sided Jacobi sweeps with `precision = 2ε`, `considerAsZero = DBL_MIN` and the running `maxDiagEntry`,
/// sign fix-up of U for negative diagonals, rescale, then a selection sort (first maximum) of the singular values.</summary>
public static class JacobiSvd3
{
    const double Precision = 4.440892098500626e-16, ConsiderAsZero = 2.2250738585072014e-308;

    /// <param name="a">3×3 column-major input (a[i + 3j] = A(i,j)).</param>
    public static void Compute(double[] a, double[] u, double[] v, double[] s)
    {
        var w = new double[9];
        double scale = 0; for (int i = 0; i < 9; i++) { double t = Math.Abs(a[i]); if (t > scale) scale = t; }
        if (scale == 0) scale = 1;
        double inv = 1.0 / scale;
        for (int i = 0; i < 9; i++) w[i] = a[i] * inv;
        for (int i = 0; i < 9; i++) { u[i] = i % 4 == 0 ? 1 : 0; v[i] = i % 4 == 0 ? 1 : 0; }
        double maxDiag = Math.Abs(w[4]); if (maxDiag <= Math.Abs(w[8])) maxDiag = Math.Abs(w[8]); if (Math.Abs(w[0]) <= maxDiag) { } else maxDiag = Math.Abs(w[0]);
        maxDiag = Max2(Math.Abs(w[0]), Max2(Math.Abs(w[4]), Math.Abs(w[8])));
        bool finished = false;
        while (!finished)
        {
            finished = true;
            for (int p = 1; p < 3; p++)
                for (int q = 0; q < p; q++)
                {
                    double threshold = Max2(ConsiderAsZero, Precision * maxDiag);
                    if (Math.Abs(w[p + 3 * q]) > threshold || Math.Abs(w[q + 3 * p]) > threshold)
                    {
                        finished = false;
                        Real2x2(w, p, q, out double cl, out double sl, out double cr, out double sr);
                        // W.applyOnTheLeft(p,q,j_left): rows
                        for (int k = 0; k < 3; k++) { double x = w[p + 3 * k], y = w[q + 3 * k]; w[p + 3 * k] = y * sl + x * cl; w[q + 3 * k] = y * cl - x * sl; }
                        // U.applyOnTheRight(p,q,j_left.transpose()): columns, rotation (c, s)
                        for (int k = 0; k < 3; k++) { double x = u[k + 3 * p], y = u[k + 3 * q]; u[k + 3 * p] = y * sl + x * cl; u[k + 3 * q] = y * cl - x * sl; }
                        // W.applyOnTheRight(p,q,j_right): columns, rotation (c, −s)
                        double nsr = -sr;
                        for (int k = 0; k < 3; k++) { double x = w[k + 3 * p], y = w[k + 3 * q]; w[k + 3 * p] = y * nsr + x * cr; w[k + 3 * q] = y * cr - x * nsr; }
                        for (int k = 0; k < 3; k++) { double x = v[k + 3 * p], y = v[k + 3 * q]; v[k + 3 * p] = y * nsr + x * cr; v[k + 3 * q] = y * cr - x * nsr; }
                        maxDiag = Max2(maxDiag, Max2(Math.Abs(w[p + 3 * p]), Math.Abs(w[q + 3 * q])));
                    }
                }
        }
        for (int i = 0; i < 3; i++)
        {
            double d = w[i + 3 * i];
            s[i] = Math.Abs(d);
            if (d < 0) { u[3 * i] = -u[3 * i]; u[3 * i + 1] = -u[3 * i + 1]; u[3 * i + 2] = -u[3 * i + 2]; }
        }
        for (int i = 0; i < 3; i++) s[i] = scale * s[i];
        for (int i = 0; i < 3; i++)
        {
            int pos = 0; double m = s[i];
            for (int j = i + 1; j < 3; j++) if (m < s[j]) { m = s[j]; pos = j - i; }
            if (m == 0) break;
            if (pos != 0)
            {
                pos += i;
                (s[i], s[pos]) = (s[pos], s[i]);
                for (int k = 0; k < 3; k++) { (u[k + 3 * i], u[k + 3 * pos]) = (u[k + 3 * pos], u[k + 3 * i]); (v[k + 3 * i], v[k + 3 * pos]) = (v[k + 3 * pos], v[k + 3 * i]); }
            }
        }
    }

    static double Max2(double a, double b) => a <= b ? b : a;   // Eigen numext::maxi / (max)

    /// <summary>`internal::real_2x2_jacobi_svd` + `JacobiRotation::makeJacobi` (Eigen 3.3), as compiled.</summary>
    static void Real2x2(double[] w, int p, int q, out double cl, out double sl, out double cr, out double sr)
    {
        double m00 = w[p + 3 * p], m01 = w[p + 3 * q], m10 = w[q + 3 * p], m11 = w[q + 3 * q];
        double t = m00 + m11, d = m10 - m01;
        double c1, s1;
        if (Math.Abs(d) < ConsiderAsZero) { c1 = 1; s1 = 0; }
        else { double uu = t / d; double tmp = Math.Sqrt(uu * uu + 1.0); c1 = uu / tmp; s1 = 1.0 / tmp; }
        double n00 = m00, n01 = m01, n11 = m11;
        if (c1 != 1.0 || s1 != 0.0) { n00 = m00 * c1 + m10 * s1; n01 = s1 * m11 + c1 * m01; n11 = m11 * c1 - s1 * m01; }
        // makeJacobi(x = n00, y = n01, z = n11)
        double ay = Math.Abs(n01), deno = ay + ay;
        double c2 = 1, s2 = 0;
        if (!(deno < ConsiderAsZero))
        {
            double tau = (n00 - n11) / deno;
            double ww = Math.Sqrt(tau * tau + 1.0);
            double tt = 1.0 / ((0.0 < tau ? ww : -ww) + tau);
            double n = 1.0 / Math.Sqrt(tt * tt + 1.0);
            s2 = Math.Abs(tt) * (n01 / ay) * (0.0 < tt ? -1.0 : 1.0) * n;
            c2 = n;
        }
        cr = c2; sr = s2;
        double ns2 = -s2;
        cl = c2 * c1 - ns2 * s1;
        sl = c2 * s1 + ns2 * c1;
    }
}

/// <summary>
/// `lt::Internal::ColorNoiseReduction` (dispatcher `1803c59f0`, tile lambda `1803c6730`) = `ColorNoiseReduction:default`
/// on the Color domain (`setColorNoiseReduction` lambda_74 `18041c930`: args = the payload view, the STD image (may
/// be absent → alpha 1), the Stats neutral, the sensor noise model for the frame gain (a,b per channel), (int)black,
/// (int)white, `pipeline.parameter_scale`, `color_noise_reduction.color_denoise_multiplier`).
///
/// levels = (int)max(0, log2f(parameter_scale) + 5), 32×32 `Tiler` tiles (remainder rule, see <see cref="Tiler"/>). The input copy gets alpha := STD² (or 1), a pyramid is built
/// (level i = upscale(G_{i+1}) − G_i, coarsest Gaussian last), each level is processed in 32×32 tiles: second
/// moments of (R,G,B,α) over the tile, per-channel noise variance
/// var_c = (ᾱ·b_c + (ratio·ᾱ + Σ(α·c)·n_c·(1−ratio)/N)·a_c) · (2^(−2·level)·(ps·m)² / n_c²),
/// whitening C' = Dinv·C·Dinv with D = sqrt(var), Dinv = rsqrtps(var), Eigen JacobiSVD of C'ᵀ in double,
/// w_k = max(0, s_k − 1)/s_k, F = I − D·(I − U·diag(w)·Uᵀ)·Dinv (float), out = F·rgb (alpha → 0), then the pyramid
/// is collapsed (G_i = upscale(G_{i+1}) − level_i).
/// </summary>
public static class ColorNoiseReductionKernel
{
    public const float LevelOffset = 5f;   // DAT_1806d46f0

    public sealed record Args(float Ratio, float VarScale, float[] Neutral, float[] InvN2, float[] A, float[] B);

    /// <summary>levels = (int)max(0, log2f(parameter_scale) + 5) (asm `1803c5aa4`: the log2f argument is param_8).</summary>
    public static int Levels(float paramScale) { float l = MathF.Log2(paramScale) + LevelOffset; if (!(l >= 0f)) l = 0f; return (int)l; }

    /// <summary>The per-level argument block (dispatcher L60–110).</summary>
    public static Args MakeArgs(int level, int black, int white, float paramScale, float multiplier, ReadOnlySpan<float> neutral, float[] a3, float[] b3)
    {
        float pm = paramScale * multiplier; pm *= pm;
        float e = MathF.Pow(2f, (float)(level * -2));
        float varScale = e * pm;
        var n = new[] { neutral[0], neutral[1], neutral[2], 1f };
        var n2 = Vector128.Create(n[0] * n[0], n[1] * n[1], n[2] * n[2], n[3] * n[3]);
        var r = Sse.IsSupported ? Sse.Reciprocal(n2) : Vector128.Create(1f / n2[0], 1f / n2[1], 1f / n2[2], 1f / n2[3]);
        var one = Vector128.Create(1f);
        var rr = Sse.Add(Sse.Multiply(Sse.Subtract(one, Sse.Multiply(n2, r)), r), r);
        var inv = new[] { rr[0], rr[1], rr[2], rr[3] };
        return new Args((float)black / (float)white, varScale, n, inv, new[] { a3[0], a3[1], a3[2], 0f }, new[] { b3[0], b3[1], b3[2], 0f });
    }

    static float MaxSs(float a, float b) => a > b ? a : b;   // maxss a,b

    /// <summary>Tile lambda `1803c6730`: statistics from <paramref name="stats"/> (the Gaussian image of the level:
    /// the source copy at level 0, else the sub-sampled image — `_Do_call` `1803c66b0`), the 3×3 remap applied to
    /// <paramref name="img"/> (the residual level) in place; both share the tile rect and stride.</summary>
    public static void ProcessTile(Vec4F[] img, int stride, RectI t, Args a) => ProcessTile(img, img, stride, t, a);
    public static void ProcessTile(Vec4F[] img, Vec4F[] stats, int stride, RectI t, Args a)
    {
        int n = (t.Y1 - t.Y0) * (t.X1 - t.X0);
        float invN = 1f / (float)n;
        float sRR = 0, sGG = 0, sBB = 0, sAA = 0, sRG = 0, sBR = 0, sGB = 0, sAR = 0, sAG = 0, sAB = 0, sAA2 = 0, sA = 0;
        for (int y = t.Y0; y < t.Y1; y++)
            for (int x = t.X0; x < t.X1; x++)
            {
                var p = stats[y * stride + x];
                sRR += p.R * p.R; sGG += p.G * p.G; sBB += p.B * p.B; sAA += p.A * p.A;
                sRG += p.R * p.G; sBR += p.B * p.R; sGB += p.G * p.B;
                sAR += p.A * p.R; sAG += p.A * p.G; sAB += p.A * p.B; sAA2 += p.A * p.A;
                sA += p.A;
            }
        float mRR = sRR * invN, mGG = sGG * invN, mBB = sBB * invN;
        float mRG = invN * sRG, mBR = invN * sBR, mGB = invN * sGB;
        float meanA = sA * invN;
        float sc = (1f - a.Ratio) * invN;
        float ra = a.Ratio * meanA;
        float[] var = new float[4]; float[] ax = { sAR, sAG, sAB, sAA2 };
        for (int c = 0; c < 4; c++)
        {
            float x = (ax[c] * a.Neutral[c]) * sc;
            var[c] = (meanA * a.B[c] + (ra + x) * a.A[c]) * (a.VarScale * a.InvN2[c]);
        }
        var vv = Vector128.Create(var[0], var[1], var[2], var[3]);
        var sq = Sse.IsSupported ? Sse.Sqrt(vv) : Vector128.Create(MathF.Sqrt(var[0]), MathF.Sqrt(var[1]), MathF.Sqrt(var[2]), MathF.Sqrt(var[3]));
        var rs = Sse.IsSupported ? Sse.ReciprocalSqrt(vv) : Vector128.Create(1f / MathF.Sqrt(var[0]), 1f / MathF.Sqrt(var[1]), 1f / MathF.Sqrt(var[2]), 1f / MathF.Sqrt(var[3]));
        var D = Diag(sq[0], sq[1], sq[2]); var Dinv = Diag(rs[0], rs[1], rs[2]);
        var C = new float[] { mRR, mRG, mBR, mRG, mGG, mGB, mBR, mGB, mBB };   // column-major, symmetric
        var M1 = Mul3(C, Dinv); var M2 = Mul3(Dinv, M1);
        // JacobiSVD input W(i,j) = M2(j,i) (the lambda packs M2 row-wise into the column-major Matrix3d)
        var W = new double[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) W[i + 3 * j] = M2[j + 3 * i];
        var U = new double[9]; var V = new double[9]; var S = new double[3];
        JacobiSvd3.Compute(W, U, V, S);
        float s0 = (float)S[0], s1 = (float)S[1], s2 = (float)S[2];
        float w0 = MaxSs(s0 + -1f, 0f) / s0, w1 = MaxSs(s1 + -1f, 0f) / s1, w2 = MaxSs(-1f + s2, 0f) / s2;
        var Wd = Diag(w0, w1, w2);
        if (Environment.GetEnvironmentVariable("LUX_CNR_DEBUG") is not null && t.X0 == 0 && t.Y0 == 0) Console.Error.WriteLine($"tile {t} n={n} meanA={meanA} ratio={a.Ratio} varScale={a.VarScale} invN2=[{string.Join(',', a.InvN2)}] A=[{string.Join(',', a.A)}] B=[{string.Join(',', a.B)}] var=[{string.Join(',', var)}] s=[{s0},{s1},{s2}] w=[{w0},{w1},{w2}] C=[{string.Join(',', C)}] M2=[{string.Join(',', M2)}]");
        var Uf = new float[9]; for (int i = 0; i < 9; i++) Uf[i] = (float)U[i];
        var Ut = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Ut[i + 3 * j] = Uf[j + 3 * i];
        var P = Mul3(Wd, Ut); var Q = Mul3(Uf, P);
        var G = new float[9]; for (int i = 0; i < 9; i++) G[i] = i % 4 == 0 ? 1f - Q[i] : -Q[i];
        var H = Mul3(G, Dinv); var DH = Mul3(D, H);
        var F = new float[9]; for (int i = 0; i < 9; i++) F[i] = i % 4 == 0 ? 1f - DH[i] : -DH[i];
        // apply: out = (B·col2 + R·col0) + G·col1, alpha lane of the columns = 0
        for (int y = t.Y0; y < t.Y1; y++)
            for (int x = t.X0; x < t.X1; x++)
            {
                ref var p = ref img[y * stride + x];
                float R = p.R, Gc = p.G, B = p.B;
                p = new Vec4F((B * F[6] + R * F[0]) + Gc * F[3], (B * F[7] + R * F[1]) + Gc * F[4], (B * F[8] + R * F[2]) + Gc * F[5], (B * 0f + R * 0f) + Gc * 0f);
            }
    }

    static float[] Diag(float a, float b, float c) => new[] { a, 0f, 0f, 0f, b, 0f, 0f, 0f, c };
    /// <summary>Eigen lazy 3×3 product, column-major: (a(i,0)·b(0,j) + a(i,1)·b(1,j)) + a(i,2)·b(2,j).</summary>
    static float[] Mul3(float[] a, float[] b)
    {
        var r = new float[9];
        for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) r[i + 3 * j] = (a[i] * b[3 * j] + a[i + 3] * b[1 + 3 * j]) + a[i + 6] * b[2 + 3 * j];
        return r;
    }

    /// <summary>Diagnostic (env LUX_CNR_DUMP): dump every pyramid level (headerless vec4 f32, `<prefix>_cnr_ds{i}/us{i}.f32`).</summary>
    public static string? DumpPrefix = Environment.GetEnvironmentVariable("LUX_CNR_DUMP");
    static void Dump(string path, Vec4F[] d, int w, int h)
    {
        var bytes = new byte[w * h * 16];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(d.AsSpan(0, w * h)).CopyTo(bytes);
        File.WriteAllBytes(path, bytes); Console.Error.WriteLine($"[cnr dump] {path}: {w}x{h}");
    }

    /// <summary>Build the pyramid: returns the `levels` residual images (each w_i×h_i) and the coarsest Gaussian.</summary>
    public static (List<(Vec4F[] Data, int W, int H)> Levels, (Vec4F[] Data, int W, int H) Top) BuildPyramid(Vec4F[] src, int w, int h, int levels)
    { var (lv, top, _) = BuildPyramidFull(src, w, h, levels); return (lv, top); }

    /// <summary>`FUN_180015440`: residual levels (`local_a8[0..n)`), the coarsest Gaussian (`local_a8[n]`) and the
    /// Gaussian images of each level (`Gauss[0]` = the source, `Gauss[i]` = `local_c8[i−1]`).</summary>
    public static (List<(Vec4F[] Data, int W, int H)> Levels, (Vec4F[] Data, int W, int H) Top, List<(Vec4F[] Data, int W, int H)> Gauss) BuildPyramidFull(Vec4F[] src, int w, int h, int levels, (Vec4F[] Data, int W, int H, int VX, int VY)? ext = null)
    {
        var list = new List<(Vec4F[], int, int)>(); var gauss = new List<(Vec4F[], int, int)> { (src, w, h) };
        var g = src; int gw = w, gh = h;
        if (DumpPrefix is not null) Dump($"{DumpPrefix}_cnr_src.f32", src, w, h);
        for (int i = 0; i < levels; i++)
        {
            var sub = i == 0 && ext is { } e ? CnrPyramid.Downsample(e.Data, e.W, e.H, e.VX, e.VY, w, h, out int sw, out int sh) : CnrPyramid.Downsample(g, gw, gh, out sw, out sh);
            var res = CnrPyramid.UpscaleSubtract(sub, sw, sh, g, gw, gh);
            list.Add((res, gw, gh));
            if (DumpPrefix is not null) { Dump($"{DumpPrefix}_cnr_ds{i}.f32", sub, sw, sh); Dump($"{DumpPrefix}_cnr_us{i}.f32", res, gw, gh); }
            g = sub; gw = sw; gh = sh; gauss.Add((sub, sw, sh));
        }
        return (list, ((Vec4F[])g.Clone(), gw, gh), gauss);
    }

    public static Vec4F[] Reconstruct(List<(Vec4F[] Data, int W, int H)> levels, (Vec4F[] Data, int W, int H) top)
    {
        var cur = top;
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            var l = levels[i];
            cur = (CnrPyramid.UpscaleSubtract(cur.Data, cur.W, cur.H, l.Data, l.W, l.H), l.W, l.H);
        }
        return cur.Data;
    }

    /// <summary>The dispatcher on a whole image (rect = the image). <paramref name="std"/> = the STD plane (same size) or null.</summary>
    public static Vec4F[] Run(Vec4F[] input, int w, int h, float[]? std, ReadOnlySpan<float> neutral, float[] a3, float[] b3, int black, int white, float paramScale, float multiplier)
        => Run(input, w, h, std, neutral, a3, b3, black, white, paramScale, multiplier, null);

    /// <summary><paramref name="ext"/>: the source's extent image (parent ∩ view±2, alpha already set) with the view's origin inside it — the
    /// level-0 down-sample reads it beyond the view (see <see cref="CnrPyramid.Downsample(Vec4F[], int, int, int, int, int, int, out int, out int)"/>).</summary>
    public static Vec4F[] Run(Vec4F[] input, int w, int h, float[]? std, ReadOnlySpan<float> neutral, float[] a3, float[] b3, int black, int white, float paramScale, float multiplier, (Vec4F[] Data, int W, int H, int VX, int VY)? ext)
    {
        var src = new Vec4F[w * h];
        for (int i = 0; i < src.Length; i++) { var p = input[i]; src[i] = new Vec4F(p.R, p.G, p.B, std is null ? 1f : std[i] * std[i]); }
        int levels = Levels(paramScale);
        if (levels == 0) return src;
        var (lv, top, gauss) = BuildPyramidFull(src, w, h, levels, ext);
        for (int level = levels - 1; level >= 0; level--)
        {
            var args = MakeArgs(level, black, white, paramScale, multiplier, neutral, a3, b3);
            var (data, lw, lh) = lv[level];
            var stats = gauss[level].Data;
            foreach (var tile in Tiler.Rects(new RectI(0, 0, lw, lh), 32, 32)) ProcessTile(data, stats, lw, tile, args);
        }
        return Reconstruct(lv, top);
    }
}

/// <summary>Color-domain stage `ColorNoiseReduction:default` (slot pad 1 / align 1, `setColorNoiseReduction` `18040fe90`).</summary>
public sealed class ColorNoiseReductionStage : IStage
{
    public StageName Stage => StageName.ColorNoiseReduction;
    public string TypeString => "default";
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ColorNoiseReduction needs the RGB working image");
        var noise = p.Frame.Noise ?? throw new InvalidOperationException("ColorNoiseReduction needs the sensor noise model (SensorNoise)");
        var model = noise.ModelForGain(p.Frame.AnalogGain);
        var t = p.Context.Tuning;
        float ps = (float)t.Num("pipeline.parameter_scale"), mult = (float)t.Num("color_noise_reduction.color_denoise_multiplier");
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        var view = img.View(abs); int w = abs.Width, h = abs.Height;
        var src = new Vec4F[w * h]; for (int y = 0; y < h; y++) view.Row(y).CopyTo(src.AsSpan(y * w, w));
        float[]? std = null;
        if (p.Std is not null && !p.Std.Rect.Intersect(abs).IsEmpty)
        {
            var sv = p.Std.View(abs); std = new float[w * h];
            for (int y = 0; y < h; y++) sv.Row(y).CopyTo(std.AsSpan(y * w, w));
        }
        // the extent the pyramid may read beyond the region: parent ∩ (region ± 2); alpha = STD² inside the region (the working copy), the image's own alpha outside
        // A side of the parent is used only when it carries the full 2-px ring (`xlo = max(rect.x0, −2)` in the tile lambda 1800170c0 partitions the
        // clamped/interior loops on even x; with a single extra pixel the border columns/rows take the clamped path and read nothing beyond the view):
        // verified on the tele ISP (demosaic output = CNR region + 1 px on every open side) — cp.dll's tele-ISP run t9 with stages 8, 9, 11, 12 skipped is bit-exact only without
        // that 1-px ring, and cp.dll's CNR stage run in isolation equals the Lux kernel on the bare region (2026-08-27). The ≥2-px case keeps the ±2 reads.
        var er = new RectI(img.Rect.X0 <= abs.X0 - 2 ? abs.X0 - 2 : abs.X0, img.Rect.Y0 <= abs.Y0 - 2 ? abs.Y0 - 2 : abs.Y0,
                           img.Rect.X1 >= abs.X1 + 2 ? abs.X1 + 2 : abs.X1, img.Rect.Y1 >= abs.Y1 + 2 ? abs.Y1 + 2 : abs.Y1);
        (Vec4F[] Data, int W, int H, int VX, int VY)? ext = null;
        if (Environment.GetEnvironmentVariable("LUX_CNR_NOEXT") == "1") er = abs;   // diagnostic: no reads beyond the region
        if (er != abs)
        {
            var ev = img.View(er); var ed = new Vec4F[er.Width * er.Height];
            for (int y = 0; y < er.Height; y++) ev.Row(y).CopyTo(ed.AsSpan(y * er.Width, er.Width));
            int vx = abs.X0 - er.X0, vy = abs.Y0 - er.Y0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { ref var q = ref ed[(y + vy) * er.Width + x + vx]; q = new Vec4F(q.R, q.G, q.B, std is null ? 1f : std[y * w + x] * std[y * w + x]); }
            ext = (ed, er.Width, er.Height, vx, vy);
        }
        if (Environment.GetEnvironmentVariable("LUX_CNR_DUMP") is string cd)   // diagnostic: the kernel input region (+ext) and args, 16-byte header f32 images
        {
            void W(string tag, Vec4F[] d, int ww, int hh) { using var fo = File.Create($"{cd}_cnr_{tag}.f32"); fo.Write(BitConverter.GetBytes(ww)); fo.Write(BitConverter.GetBytes(hh)); fo.Write(BitConverter.GetBytes(ww)); fo.Write(BitConverter.GetBytes(16)); fo.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(d.AsSpan())); }
            W("src", src, w, h); if (ext is not null) W("ext", ext.Value.Data, ext.Value.W, ext.Value.H);
            File.WriteAllText($"{cd}_cnr_args.txt", $"region ({abs.X0} {abs.Y0} {abs.X1} {abs.Y1}) ext ({er.X0} {er.Y0} {er.X1} {er.Y1}) neutral {string.Join(" ", p.Stats.Neutral.Select(v => v.ToString("R")))} a3 {model.R.A:R} {model.G.A:R} {model.Bl.A:R} b3 {model.R.B:R} {model.G.B:R} {model.Bl.B:R} black {(int)(float.IsNaN(p.Frame.FrameBlack) ? noise.Black : p.Frame.FrameBlack)} white {(int)noise.White} paramScale {ps:R} multiplier {mult:R} gain {p.Frame.AnalogGain:R}\n");
        }
        var neutralUsed = p.Stats.Neutral;
        if (Environment.GetEnvironmentVariable("LUX_CNR_NEUTRAL") is string cn) neutralUsed = cn.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();   // diagnostic
        var outp = ColorNoiseReductionKernel.Run(src, w, h, std, neutralUsed, new[] { model.R.A, model.G.A, model.Bl.A }, new[] { model.R.B, model.G.B, model.Bl.B }, (int)(float.IsNaN(p.Frame.FrameBlack) ? noise.Black : p.Frame.FrameBlack), (int)noise.White, ps, mult, ext);   // lambda_74 18041c930: (int) of the capture's sensor black/white (FUN_180125630(img)+4/+8 = the per-frame black, 43.05 → 43 on L16_00405)
        var dst = new Image<Vec4F>(abs);
        for (int y = 0; y < h; y++) outp.AsSpan(y * w, w).CopyTo(dst.Row(y));
        p.Rgb = dst;
    }
}
