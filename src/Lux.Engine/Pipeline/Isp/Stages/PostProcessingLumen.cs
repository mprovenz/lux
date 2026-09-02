using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Geometry;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::Internal::PostProcessing` (`180428810`, tile kernel lambda_0 `180435b40` → no-vibrance kernel `FUN_180429fe0`) as the
/// module ISP runs it (slot 11 of the Bayer block, wrapper `Pipeline::lambda_1` `180413ea0`): parameters = (analog gain
/// `CapturedImage+0x40`, `pipe+0x1bc8..d4` = (a, b, sharpening c, sharpening_scale d)), saturation/vibrance `pipe+0x1b88/8c`.
/// Without the payload's companion image (module ISP) a and b are forced to 0, so the kernel is: RGB → Lab in the
/// colour-space-5 working space (ProPhoto primaries, D50 white; `LabLineFactory` `1804343b0`/`180434890`, fast cube root),
/// a gain-dependent DoG sharpening of L (`SharpenLineFactory` `180430c10`/`180431250`, Gaussian taps `FUN_1800bb720`,
/// separable 3/5/7-tap convolutions `ConvLineFactory`), then Lab → RGB with the inverse of the white-scaled matrix.
/// All float operation orders follow the decompiles; verified against cp.dll's post-processing stage run in isolation.
/// </summary>
public static class PostProcessingLumen
{
    // colour object `FUN_18038a230` (colour space 5): ProPhoto RGB→XYZ and the D50 white xy
    private static readonly float[] M = {
        BitConverter.Int32BitsToSingle(0x3f4c346c), BitConverter.Int32BitsToSingle(0x3e0a6fb1), BitConverter.Int32BitsToSingle(0x3d006c6c),
        BitConverter.Int32BitsToSingle(0x3e937a01), BitConverter.Int32BitsToSingle(0x3f363d62), BitConverter.Int32BitsToSingle(0x38b3b9d6),
        0f, 0f, BitConverter.Int32BitsToSingle(0x3f5340f6) };
    private static readonly float WhiteX = BitConverter.Int32BitsToSingle(0x3eb0fb8d), WhiteY = BitConverter.Int32BitsToSingle(0x3eb78cd0);

    private const float Eps = 9.999999974752427e-07f;        // DAT_18069f08c
    private const int CbrtMagic = 0x2a5137a0;                  // DAT_180687580
    private static readonly float Third = BitConverter.Int32BitsToSingle(0x3eaaaaab), TwoThird = BitConverter.Int32BitsToSingle(0x3f2aaaab);
    private static readonly float LabK = BitConverter.Int32BitsToSingle(0x40f92f69), LabOff = BitConverter.Int32BitsToSingle(0x3e0d3dcb), LabT0 = BitConverter.Int32BitsToSingle(0x3c111aa7);   // 7.787, 16/116, (6/29)³
    private static readonly float Inv116 = BitConverter.Int32BitsToSingle(0x3c0d3dcb), Cbrt6_29 = BitConverter.Int32BitsToSingle(0x3e53dcb1), LinK = BitConverter.Int32BitsToSingle(0x3e038027), LinOff = BitConverter.Int32BitsToSingle(unchecked((int)0xbc911aa7)), L8 = 8f, YLin = BitConverter.Int32BitsToSingle(0x3a911aa7);
    private static readonly float AScale = BitConverter.Int32BitsToSingle(0x3b03126f), BScale = BitConverter.Int32BitsToSingle(0x3ba3d70a);   // 0.002, 0.005
    /// <summary>Row·vector with Lumen's association `(m0·a + m1·b) + m2·c` (both the forward and the inverse product; verified bit-exact).</summary>
    private static float Dot3(float m0, float a, float m1, float b, float m2, float c) => (m0 * a + m1 * b) + m2 * c;
    private static float Rcp(float x) => Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / x;
    private static float Rsqrt(float x) => Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / MathF.Sqrt(x);

    /// <summary>`FUN_1800ce5a0`: white XYZ from xy (X = x·(1/y), Y = 1, Z = ((1 − y) − x)·(1/y)).</summary>
    public static (float X, float Y, float Z) WhiteXyz(float x, float y) { float iy = 1f / y; return (x * iy, 1f, ((1f - y) - x) * iy); }

    /// <summary>The Lab working matrix: row i of M scaled by 1/white_i (`LabLineFactory` init).</summary>
    public static float[] WhiteScaledMatrix()
    {
        var (wx, wy, wz) = WhiteXyz(WhiteX, WhiteY);
        float ix = 1f / wx, iy = 1f / wy, iz = 1f / wz;
        return new[] { M[0] * ix, M[1] * ix, M[2] * ix, M[3] * iy, M[4] * iy, M[5] * iy, M[6] * iz, M[7] * iz, M[8] * iz };
    }

    /// <summary>`FUN_1800bb720(out, n, sigma, 1.0)`: k[i] = expf((i − (n−1)/2)²·(−0.5/σ²)), then × (1/Σk). sigma == 0 → delta.</summary>
    public static float[] GaussianTaps(int n, float sigma)
    {
        var k = new float[n];
        if (sigma == 0f) { k[n >> 1] = 1f; return k; }
        float a = -0.5f / (sigma * sigma), c = (float)(n - 1) * 0.5f, sum = 0f;
        for (int i = 0; i < n; i++) { float d = (float)i - c; k[i] = MathF.Exp(d * d * a); sum += k[i]; }
        float norm = 1.0f / sum;
        for (int i = 0; i < n; i++) k[i] *= norm;
        return k;
    }

    /// <summary>`FUN_180431710`: kernel size by sigma (≤1 → 3, ≤1.3 → 5, else 7).</summary>
    public static int TapsFor(float sigma) => sigma <= 1.0f ? 3 : sigma <= 1.3f ? 5 : 7;

    /// <summary>Sharpen parameters (`FUN_180430c10`): gain-dependent piecewise (σ₁ = f17·d, σ₂ = d·√(f17² + f14²), amount = f13 + f16).</summary>
    public static (float Sigma1, float Sigma2, float Amount) SharpenParams(float gain, float c, float d)
    {
        float f17 = 1.0f, f16 = 0.5f, f14 = 1.1f, f13 = 0.5f;
        if (gain < 7.75f)
        {
            if (4.0f <= gain)
            {
                float t = (gain + -4.0f) * 0.2857142984867096f; if (t < 0f) t = 0f; if (t > 1f) t = 1f;
                f17 = -0.10000002384185791f * t + 1.100000023841858f;
                f14 = ((-0.4000000059604645f * t) + 0.5f) + f17;
                f16 = t * -0.050000011920928955f + 0.550000011920929f;
            }
            else if (2.0f <= gain)
            {
                float t = (gain + -2.0f) * 0.5f; if (t < 0f) t = 0f; if (t > 1f) t = 1f;
                float p = 0.10000002384185791f * t;
                f17 = 1.0f + p;
                f14 = p + 1.5f;
                f16 = 0.050000011920928955f * t + 0.5f;
                f13 = t * -0.10000002384185791f + 0.6000000238418579f;
            }
            else
            {
                f14 = 1.2999999523162842f;
                if (1.0f <= gain)
                {
                    float t = gain + -1.0f; if (t < 0f) t = 0f; if (t > 1f) t = 1f;
                    f14 = 0.19999998807907104f * t + 1.2999999523162842f;
                    f13 = t * 0.10000002384185791f + 0.5f;
                }
            }
        }
        float u = (c + -4.0f) * 0.0833333358168602f; if (u <= 0f) u = 0f; if (1.0f <= u) u = 1.0f;
        float g17 = (f16 - f17) * u + f17;
        float g14 = ((f13 + f16) - f14) * u + f14;
        float l2 = g17 * g17 + g14 * g14;
        float len = 0f;
        if (l2 != 0f) { float rs = Rsqrt(l2); float s = l2 * rs; len = (s * rs + -3.0f) * -0.5f * s; }
        return (g17 * d, d * len, f13 + f16);   // NOTE: the DoG amount used by getLine is `this+0x70` = c (params[3]); f13+f16 is not consumed there
    }

    /// <summary>Fast cube root of t ≥ 0 as `LabLineFactory` computes it (bit-magic seed on the float's bit pattern, one raw-rcp Newton step,
    /// one refined step), then the Lab f(t): t &lt; (6/29)³ → 7.787·t + 16/116, else the cube root.</summary>
    private static float LabFBits(float t)
    {
        int bits = BitConverter.SingleToInt32Bits(t);
        int i = (bits >> 2) + (bits >> 4);
        i = (i >> 4) + i;
        float y0 = BitConverter.Int32BitsToSingle(i + CbrtMagic + (i >> 8));
        float r0 = Rcp(y0 * y0);
        float s1 = y0 + y0 + r0 * t;
        float y1 = s1 * Third;
        float y1sq = y1 * y1;
        float r1 = Rcp(y1sq);
        float cb = (s1 * TwoThird + ((1.0f - y1sq * r1) * r1 + r1) * t) * Third;
        float lin = t * LabK + LabOff;
        return t < LabT0 ? lin : cb;
    }


    /// <summary>Debug: the Lab chain for one RGB triple.</summary>
    public static string DebugPixel(float r, float g, float bl, float sat)
    {
        var mw = WhiteScaledMatrix(); var minv = Mat3F.Inverse(mw);
        float X = mw[2] * bl + mw[1] * g + mw[0] * r, Y = mw[5] * bl + mw[4] * g + mw[3] * r, Z = mw[8] * bl + mw[7] * g + mw[6] * r;
        float fx = LabFBits(X), fy = LabFBits(Y), fz = LabFBits(Z);
        float L = fy * 116.0f + -16.0f, A = (fx - fy) * 500.0f, B = (fy - fz) * 200.0f;
        float fy2 = (L + 16.0f) * Inv116, fx2 = A * sat * AScale + fy2, fz2 = fy2 - B * sat * BScale;
        float X2 = fx2 < Cbrt6_29 ? fx2 * LinK + LinOff : fx2 * fx2 * fx2, Y2 = L < L8 ? L * YLin : fy2 * fy2 * fy2, Z2 = fz2 < Cbrt6_29 ? fz2 * LinK + LinOff : fz2 * fz2 * fz2;
        float r2 = minv[2] * Z2 + minv[1] * Y2 + minv[0] * X2, g2 = minv[5] * Z2 + minv[4] * Y2 + minv[3] * X2, b2 = minv[8] * Z2 + minv[7] * Y2 + minv[6] * X2;
        return $"XYZ {X:R} {Y:R} {Z:R} f {fx:R} {fy:R} {fz:R} (cbrt check {MathF.Cbrt(X):R} {MathF.Cbrt(Y):R} {MathF.Cbrt(Z):R}) Lab {L:R} {A:R} {B:R} -> f2 {fx2:R} {fy2:R} {fz2:R} XYZ2 {X2:R} {Y2:R} {Z2:R} RGB2 {r2:R} {g2:R} {b2:R} | mw {string.Join(",", mw.Select(v => v.ToString("G5")))} minv {string.Join(",", minv.Select(v => v.ToString("G5")))}";
    }

    /// <summary>Run PostProcessing over an RGBA float image exactly as `Tiler::Run(rect, {256,256})` does: 256-px column tiles, each with its own
    /// Lab line (SSE loop + scalar tail, image-edge replicate), H/V convolutions with the machine tap associations and scalar tails
    /// (spec `aec4163130d308564.md`; bit-exact on the 520×390 and 4160×3120 A1 frames of L16_00466).</summary>
    /// <param name="comp">The payload's companion image (+0xa0 = the pre-denoise working image left by the denoiser's swap) or null: with it and
    /// |grain_power| ≥ 1e-6 the LDiff path adds `grain_power · blur(L(comp) − L(src))` to the sharpened L (spec a8f84ea4fa52292a1).</param>
    public static void Run(float[] rgba, int w, int h, float gain, float a, float b, float c, float d, float sat, float vib, float[]? comp = null) => Run(rgba, w, h, gain, a, b, c, d, sat, vib, comp, null, null);

    /// <summary>
    /// <paramref name="ext"/>/<paramref name="compExt"/>: the source (and companion) as extent buffers = parent image ∩ (region ± 3) with the
    /// region at (VX, VY). The Lab/LDiff lines are computed over the extent rows/columns and only replicated where the parent has no data
    /// (Lumen's PP reads its input image beyond the stage region; runner rect dump 2026-08-27: PP region 516 of a 519 denoiser output).
    /// </summary>
    public static void Run(float[] rgba, int w, int h, float gain, float a, float b, float c, float d, float sat, float vib, float[]? comp, (float[] Data, int W, int H, int VX, int VY)? ext, float[]? compExt)
    {
        if (comp is null || MathF.Abs(a) < Eps) { a = 0f; b = 0f; }
        bool ldiffPath = MathF.Abs(a) > Eps;
        bool run = MathF.Abs(a) >= Eps || MathF.Abs(c) >= Eps || MathF.Abs(1.0f - sat) >= Eps || MathF.Abs(1.0f - vib) >= Eps;
        if (!run) return;
        if (!(vib >= 0.9999989867210388f && vib <= 1.0000009536743164f)) throw new NotSupportedException("PostProcessing with vibrance ≠ 1 (FUN_18042ba00/FUN_18042da20) is not ported yet");

        var mw = WhiteScaledMatrix();
        var minv = Mat3F.Inverse(mw);
        // ---- Tiler::Run(rect, {256,256}) column tiles (spec aec4163130d308564): each tile computes its own Lab lines (SSE loop + scalar tail
        // with the exact-division cube root) over the parent image, replicates the first/last computed sample into the 3-sample margin
        // at the image edges, and runs the H/V convolutions with the machine tap associations and scalar tails. Row tiling has no effect.
        const int margin = 3;
        var src = ext?.Data ?? (float[])rgba.Clone();   // Lumen writes a separate output image: tiles read the untouched input (incl. their margins)
        int ew = ext?.W ?? w, eh = ext?.H ?? h, vx = ext?.VX ?? 0, vy = ext?.VY ?? 0;
        if (ext is not null && comp is not null) comp = compExt ?? throw new ArgumentException("compExt required with ext");   // no companion (denoiser absent/skipped) ⇒ no LDiff path
        int rs = Math.Max(-margin, -vy), re = Math.Min(h + margin, eh - vy), hh = re - rs;   // rows with parent data (region coordinates)
        int nx = w / 256 + ((w % 256) * 2 > 256 ? 1 : 0); if (nx < 1) nx = 1;
        float[]? k1 = null, k2 = null; int n1 = 0, n2 = 0; float amount = 0f;
        if (c > Eps) { var (s1, s2, _) = SharpenParams(gain, c, d); n1 = TapsFor(s1); k1 = GaussianTaps(n1, s1); n2 = TapsFor(s2); k2 = GaussianTaps(n2, s2); amount = c; }
        else if (c < -Eps) throw new NotSupportedException("PostProcessing blur path (sharpening < 0) is not ported yet");
        for (int ti = 0; ti < nx; ti++)
        {
            int x0 = 256 * ti, x1 = Math.Min(x0 + (ti == nx - 1 ? 512 : 256), w), tw = x1 - x0, lw = tw + 2 * margin;
            var L = new float[hh * lw]; var A = new float[hh * tw]; var B = new float[hh * tw];
            int st = Math.Max(-margin, -x0 - vx), en = Math.Min(tw + margin, ew - vx - x0), n4 = (en - st) & ~3;
            for (int yy = rs; yy < re; yy++)
            {
                int y = yy - rs;
                for (int x = st; x < en; x++)
                {
                    bool scalar = x >= st + n4;
                    int o = ((yy + vy) * ew + x0 + x + vx) * 4;
                    float r = src[o], g = src[o + 1], bl = src[o + 2];
                    float X = Dot3(mw[0], r, mw[1], g, mw[2], bl);
                    float Y = Dot3(mw[3], r, mw[4], g, mw[5], bl);
                    float Z = Dot3(mw[6], r, mw[7], g, mw[8], bl);
                    if (X < 0f) X = 0f; if (Y < 0f) Y = 0f; if (Z < 0f) Z = 0f;
                    float fx = scalar ? LabFBitsScalar(X) : LabFBits(X), fy = scalar ? LabFBitsScalar(Y) : LabFBits(Y), fz = scalar ? LabFBitsScalar(Z) : LabFBits(Z);
                    L[y * lw + margin + x] = fy * 116.0f + -16.0f;
                    if (x >= 0 && x < tw) { A[y * tw + x] = (fx - fy) * 500.0f; B[y * tw + x] = (fy - fz) * 200.0f; }
                }
                for (int x = -margin; x < st; x++) L[y * lw + margin + x] = L[y * lw + margin + st];
                for (int x = en; x < tw + margin; x++) L[y * lw + margin + x] = L[y * lw + margin + en - 1];
            }
            float[]? blur1 = null, blur2 = null;
            if (k1 is not null) { blur1 = SeparableGaussian(L, tw, hh, lw, margin, k1!, n1); blur2 = SeparableGaussian(L, tw, hh, lw, margin, k2!, n2); }
            float[]? ld = null;   // LDiff line per row (tw values), FUN_180435460 (+ optional 5-tap blur)
            if (ldiffPath)
            {
                var LD = new float[hh * lw];
                for (int yy = rs; yy < re; yy++)
                {
                    int y = yy - rs;
                    for (int x = st; x < en; x++)
                    {
                        bool scalar = x >= st + n4; int o = ((yy + vy) * ew + x0 + x + vx) * 4;
                        float Y = Dot3(mw[3], comp![o], mw[4], comp[o + 1], mw[5], comp[o + 2]); if (Y < 0f) Y = 0f;
                        float f = scalar ? LabFBitsScalar(Y) : LabFBits(Y);
                        LD[y * lw + margin + x] = (f * 116.0f) + (-16.0f - L[y * lw + margin + x]);
                    }
                    for (int x = -margin; x < st; x++) LD[y * lw + margin + x] = LD[y * lw + margin + st];
                    for (int x = en; x < tw + margin; x++) LD[y * lw + margin + x] = LD[y * lw + margin + en - 1];
                }
                if (b > Eps) ld = SeparableGaussian(LD, tw, hh, lw, margin, GaussianTaps(5, b), 5);
                else { ld = new float[hh * tw]; for (int y = 0; y < hh; y++) Array.Copy(LD, y * lw + margin, ld, y * tw, tw); }
            }
            if (Environment.GetEnvironmentVariable("LUX_PP_DEBUG") is string dbg && dbg.Split(',') is { Length: 2 } dd && int.Parse(dd[0]) >= x0 && int.Parse(dd[0]) < x1)
            {
                int qx = int.Parse(dd[0]) - x0, qy = int.Parse(dd[1]) - rs;
                Console.Error.WriteLine($"[pp] tile {ti} [{x0},{x1}) st {st} en {en} n4 {n4} taps {n1}/{n2} x {qx}: L[-3..+3] {string.Join(" ", Enumerable.Range(-3, 7).Select(k => L[qy * lw + margin + qx + k].ToString("R")))} blur1 {blur1?[qy * tw + qx]:R} blur2 {blur2?[qy * tw + qx]:R} A {A[qy * tw + qx]:R} B {B[qy * tw + qx]:R}");
            }
            // ---- reconstruction (row loop of FUN_180429fe0, LDiff term = 0)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < tw; x++)
                {
                    int i = (y - rs) * tw + x, o = (y * w + x0 + x) * 4;
                    float ls = L[(y - rs) * lw + margin + x];
                    float lsharp = ls;
                    if (blur1 is not null)
                    {
                        float dog = (blur1[i] - blur2![i]) * amount;
                        if (dog < -20f) dog = -20f; if (dog > 20f) dog = 20f;
                        lsharp = dog + ls;
                    }
                    // `maxps xmm1, xmm14` 0x42a7e9 (4-wide body) / `maxss xmm0, xmm1` 0x42a9d1 (tw&3 tail), zero in the second operand ⇒ `v > 0 ? v : 0`
                    float lp = ld is not null ? ld[i] * a + lsharp : lsharp; if (!(lp > 0f)) lp = 0f;
                    float fy = (lp + 16.0f) * Inv116;
                    float fx = A[i] * sat * AScale + fy;
                    float fz = fy - B[i] * sat * BScale;
                    float X = fx < Cbrt6_29 ? fx * LinK + LinOff : fx * fx * fx;
                    float Y = lp < L8 ? lp * YLin : fy * fy * fy;
                    float Z = fz < Cbrt6_29 ? fz * LinK + LinOff : fz * fz * fz;
                    float r = Dot3(minv[0], X, minv[1], Y, minv[2], Z);
                    float g = Dot3(minv[3], X, minv[4], Y, minv[5], Z);
                    float bb = Dot3(minv[6], X, minv[7], Y, minv[8], Z);
                    // `FUN_180429fe0`'s 4-wide row loop (0x42a7d0..0x42a963) stores the RGBA quads unclamped — Lumen keeps −2.3e-7 for an
                    // out-of-gamut R (verified). Its `tw & 3` scalar tail (0x42a9c0..0x42aafb) instead assembles the pixel with three
                    // `insertps`, takes alpha from DAT_180681c78 = 1.0 and finishes with `maxps xmm3, xmm14` (zero) at 0x42aae6 — so the
                    // last `tw % 4` columns of every 256-px column tile, and only those, are clamped at 0. Only the remainder-absorbing
                    // last tile can have `tw % 4 ≠ 0`, so in practice this is the tile ending on the frame's right edge.
                    if (x >= (tw & ~3)) { if (!(r > 0f)) r = 0f; if (!(g > 0f)) g = 0f; if (!(bb > 0f)) bb = 0f; }
                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = bb; rgba[o + 3] = 1f;
                }
        }
    }

    /// <summary>`FUN_180434e60` (the scalar tail of the Lab line): the same cube-root seed and first step, but the second step uses an exact
    /// division `t / (y1·y1)` instead of `rcpps` + Newton.</summary>
    private static float LabFBitsScalar(float t)
    {
        int bits = BitConverter.SingleToInt32Bits(t);
        int i = (bits >> 2) + (bits >> 4);
        i = (i >> 4) + i;
        float y0 = BitConverter.Int32BitsToSingle(i + CbrtMagic + (i >> 8));
        float r0 = Rcp(y0 * y0);
        float s1 = (y0 + y0) + r0 * t;
        float y1 = s1 * Third;
        float cb = ((s1 * TwoThird) + t / (y1 * y1)) * Third;
        float lin = t * LabK + LabOff;
        return t < LabT0 ? lin : cb;
    }

    /// <summary>Separable Gaussian (H then V) of one tile line set with the `ConvLineFactory` machine associations: 3-tap `k0·i₁ + k1·c`,
    /// 5-tap `k1·i₁ + (k0·o₂ + k2·c)`, 7-tap vector `(k2·i₁ + k1·o₂) + (k0·o₃ + k3·c)` and the sequential scalar tail
    /// `((k3·c + k0·o₃) + k1·o₂) + k2·i₁` on the last `4 + tw%4` columns (H) / `tw%4` columns (V); rows clamp at the image edges.</summary>
    private static float[] SeparableGaussian(float[] L, int w, int h, int lw, int margin, float[] k, int n)
    {
        int vecEnd = n == 5 ? (w & ~3) : (w & ~3) - 4; if (vecEnd < 0) vecEnd = 0;
        var hbuf = new float[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c0 = y * lw + margin + x;
                float cc = L[c0], i1 = L[c0 + 1] + L[c0 - 1];
                float v;
                if (n == 3) v = k[0] * i1 + k[1] * cc;
                else if (n == 5) { float o2 = L[c0 - 2] + L[c0 + 2]; v = k[1] * i1 + (k[0] * o2 + k[2] * cc); }
                else
                {
                    float o2 = L[c0 - 2] + L[c0 + 2], o3 = L[c0 - 3] + L[c0 + 3];
                    if (x < vecEnd) v = (k[2] * i1 + k[1] * o2) + (k[0] * o3 + k[3] * cc);
                    else { float t = k[3] * cc; t = k[0] * o3 + t; t = k[1] * o2 + t; v = k[2] * i1 + t; }
                }
                hbuf[y * w + x] = v;
            }
        var vbuf = new float[h * w];
        int Row(int yy) => yy < 0 ? 0 : yy >= h ? h - 1 : yy;
        int vTail = w & ~3;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float P(int dy) => hbuf[Row(y + dy) * w + x];
                float cc = P(0), i1 = P(1) + P(-1);
                float v;
                if (n == 3) v = k[0] * i1 + k[1] * cc;
                else if (n == 5) { float o2 = P(2) + P(-2); v = k[1] * i1 + (k[0] * o2 + k[2] * cc); }
                else
                {
                    float o2 = P(2) + P(-2), o3 = P(3) + P(-3);
                    if (x < vTail) v = (k[2] * i1 + k[1] * o2) + (k[0] * o3 + k[3] * cc);
                    else { float t = k[3] * cc; t = k[0] * o3 + t; t = k[1] * o2 + t; v = k[2] * i1 + t; }
                }
                vbuf[y * w + x] = v;
            }
        return vbuf;
    }
}
