using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Geometry;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// Lumen's `lt::Internal::PostProcessing` (tile kernel → no-vibrance kernel) as the
/// module ISP runs it (slot 11 of the Bayer block, wrapped by the `Pipeline` constructor): parameters = (analog gain,
/// a = grain_power, b = grain_sigma, c = sharpening, d = sharpening_scale), saturation, vibrance.
/// Without the payload's companion image (module ISP) a and b are forced to 0, so the kernel is: RGB → Lab in the
/// color-space-5 working space (ProPhoto primaries, D50 white; `LabLineFactory`, fast cube root),
/// a gain-dependent DoG sharpening of L (`SharpenLineFactory`, Gaussian taps <see cref="GaussianTaps"/>,
/// separable 3/5/7-tap convolutions `ConvLineFactory`), then Lab → RGB with the inverse of the white-scaled matrix.
/// All float operation orders follow Lumen's code.
/// </summary>
public static class PostProcessingLumen
{
    // color object of color space 5: ProPhoto RGB→XYZ and the D50 white xy
    private static readonly float[] M = {
        BitConverter.Int32BitsToSingle(0x3f4c346c), BitConverter.Int32BitsToSingle(0x3e0a6fb1), BitConverter.Int32BitsToSingle(0x3d006c6c),
        BitConverter.Int32BitsToSingle(0x3e937a01), BitConverter.Int32BitsToSingle(0x3f363d62), BitConverter.Int32BitsToSingle(0x38b3b9d6),
        0f, 0f, BitConverter.Int32BitsToSingle(0x3f5340f6) };
    private static readonly float WhiteX = BitConverter.Int32BitsToSingle(0x3eb0fb8d), WhiteY = BitConverter.Int32BitsToSingle(0x3eb78cd0);

    private const float Eps = 9.999999974752427e-07f;
    private const int CbrtMagic = 0x2a5137a0;
    private static readonly float Third = BitConverter.Int32BitsToSingle(0x3eaaaaab), TwoThird = BitConverter.Int32BitsToSingle(0x3f2aaaab);
    private static readonly float LabK = BitConverter.Int32BitsToSingle(0x40f92f69), LabOff = BitConverter.Int32BitsToSingle(0x3e0d3dcb), LabT0 = BitConverter.Int32BitsToSingle(0x3c111aa7);   // 7.787, 16/116, (6/29)³
    private static readonly float Inv116 = BitConverter.Int32BitsToSingle(0x3c0d3dcb), Cbrt6_29 = BitConverter.Int32BitsToSingle(0x3e53dcb1), LinK = BitConverter.Int32BitsToSingle(0x3e038027), LinOff = BitConverter.Int32BitsToSingle(unchecked((int)0xbc911aa7)), L8 = 8f, YLin = BitConverter.Int32BitsToSingle(0x3a911aa7);
    private static readonly float AScale = BitConverter.Int32BitsToSingle(0x3b03126f), BScale = BitConverter.Int32BitsToSingle(0x3ba3d70a);   // 0.002, 0.005
    /// <summary>Row·vector with Lumen's association `(m0·a + m1·b) + m2·c` (both the forward and the inverse product).</summary>
    private static float Dot3(float m0, float a, float m1, float b, float m2, float c) => (m0 * a + m1 * b) + m2 * c;
    private static float Rcp(float x) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    private static float Rsqrt(float x) => IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();

    /// <summary>White XYZ from xy (X = x·(1/y), Y = 1, Z = ((1 − y) − x)·(1/y)).</summary>
    public static (float X, float Y, float Z) WhiteXyz(float x, float y) { float iy = 1f / y; return (x * iy, 1f, ((1f - y) - x) * iy); }

    /// <summary>The Lab working matrix: row i of M scaled by 1/white_i (`LabLineFactory` init).</summary>
    public static float[] WhiteScaledMatrix()
    {
        var (wx, wy, wz) = WhiteXyz(WhiteX, WhiteY);
        float ix = 1f / wx, iy = 1f / wy, iz = 1f / wz;
        return new[] { M[0] * ix, M[1] * ix, M[2] * ix, M[3] * iy, M[4] * iy, M[5] * iy, M[6] * iz, M[7] * iz, M[8] * iz };
    }

    /// <summary>Gaussian taps (n, sigma, scale 1.0): k[i] = expf((i − (n−1)/2)²·(−0.5/σ²)), then × (1/Σk). sigma == 0 → delta.</summary>
    public static float[] GaussianTaps(int n, float sigma)
    {
        var k = new float[n];
        if (sigma == 0f) { k[n >> 1] = 1f; return k; }
        float a = -0.5f / (sigma * sigma), c = (float)(n - 1) * 0.5f, sum = 0f;
        for (int i = 0; i < n; i++) { float d = (float)i - c; k[i] = WineCrt.Expf(d * d * a); sum += k[i]; }   // CRT expf, the n ≤ 7 scalar loop
        float norm = 1.0f / sum;
        for (int i = 0; i < n; i++) k[i] *= norm;
        return k;
    }

    /// <summary>Kernel size by sigma (≤1 → 3, ≤1.3 → 5, else 7).</summary>
    public static int TapsFor(float sigma) => sigma <= 1.0f ? 3 : sigma <= 1.3f ? 5 : 7;

    /// <summary>Sharpen parameters: gain-dependent piecewise (σ₁ = f17·d, σ₂ = d·√(f17² + f14²), amount = f13 + f16).</summary>
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
        return (g17 * d, d * len, f13 + f16);   // NOTE: the DoG amount used by getLine is c (params[3]); f13+f16 is not consumed there
    }

    /// <summary>The radius, in pixels, that the wide Gaussian of the monotone sharpening response does not go below at a large amount.
    /// Below approximately half a pixel a sampled Gaussian is almost a unit impulse, and the difference of the two Gaussians disappears.</summary>
    public const float MonotoneWideSigma = 0.5f;

    /// <summary>
    /// The two Gaussian radii of the monotone sharpening response (Lux). Lumen makes both radii smaller when the amount increases
    /// from 4 to 16. At a reduced image scale the radii are then below one pixel, both Gaussians become unit impulses, and a larger
    /// amount sharpens less. This response uses Lumen's radii with one change to the wide radius for an amount above 4:
    /// <list type="bullet">
    /// <item>If Lumen's wide radius at amount 4 is <see cref="MonotoneWideSigma"/> pixels or more, the wide radius does not shrink
    /// below that limit.</item>
    /// <item>If it is smaller, the wide radius increases linearly to the limit while the amount goes from 4 to 16.</item>
    /// </list>
    /// The result is equal to Lumen's radii for each amount up to 4 at each image scale, and for each amount at full and at half size.
    /// </summary>
    public static (float Sigma1, float Sigma2) MonotoneSharpenSigmas(float gain, float c, float d)
    {
        var (s1, s2, _) = SharpenParams(gain, c, d);
        if (!(c > 4.0f)) return (s1, s2);
        var (_, wide4, _) = SharpenParams(gain, 4.0f, d);
        float floor = MonotoneWideSigma;
        if (wide4 < MonotoneWideSigma)
        {
            float u = (c + -4.0f) * 0.0833333358168602f; if (1.0f <= u) u = 1.0f;
            floor = (MonotoneWideSigma - wide4) * u + wide4;
        }
        if (s2 < floor) s2 = floor;
        return (s1, s2);
    }

    /// <summary>The smallest image scale that the blur radius of the monotone sharpening response follows.</summary>
    public const float MonotoneBlurMinScale = 0.25f;

    /// <summary>The blur radius of the monotone sharpening response (Lux) for a negative amount: |amount| · scale, in pixels. The
    /// blur then covers the same part of the picture at full, half and quarter size. Below a quarter the radius stays at the quarter-size
    /// value, because a smaller radius has no visible effect on the pixels. At full size (scale 1) the radius is equal to Lumen's
    /// radius. Lumen divides |amount| by e^log2(1 / scale), which removes almost all of the blur at a reduced image scale.</summary>
    public static float MonotoneBlurSigma(float c, float d) => MathF.Abs(c) * (d < MonotoneBlurMinScale ? MonotoneBlurMinScale : d);

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
    /// Lab line (SSE loop + scalar tail, image-edge replicate), H/V convolutions with Lumen's tap associations and scalar tails
    /// in Lumen's order.</summary>
    /// <param name="comp">The payload's companion image (the pre-denoise working image left by the denoiser's swap) or null: with it and
    /// |grain_power| ≥ 1e-6 the LDiff path adds `grain_power · blur(L(comp) − L(src))` to the sharpened L.</param>
    public static void Run(float[] rgba, int w, int h, float gain, float a, float b, float c, float d, float sat, float vib, float[]? comp = null) => Run(rgba, w, h, gain, a, b, c, d, sat, vib, comp, null, null);

    /// <summary>
    /// <paramref name="ext"/>/<paramref name="compExt"/>: the source (and companion) as extent buffers = parent image ∩ (region ± 3) with the
    /// region at (VX, VY). The Lab/LDiff lines are computed over the extent rows/columns and only replicated where the parent has no data
    /// (Lumen's PP reads its input image beyond the stage region; e.g. a PP region of 516 inside a 519 denoiser output).
    /// </summary>
    /// <param name="monotoneSharpening">False: Lumen's sharpening response. True: the monotone response of Lux
    /// (<see cref="MonotoneSharpenSigmas"/>, <see cref="MonotoneBlurSigma"/>).</param>
    public static void Run(float[] rgba, int w, int h, float gain, float a, float b, float c, float d, float sat, float vib, float[]? comp, (float[] Data, int W, int H, int VX, int VY)? ext, float[]? compExt, bool monotoneSharpening = false)
    {
        if (comp is null || MathF.Abs(a) < Eps) { a = 0f; b = 0f; }
        bool ldiffPath = MathF.Abs(a) > Eps;
        bool run = MathF.Abs(a) >= Eps || MathF.Abs(c) >= Eps || MathF.Abs(1.0f - sat) >= Eps || MathF.Abs(1.0f - vib) >= Eps;
        if (!run) return;
        // the tile kernel picks by v: v in [0x3f7fffef, 0x3f800008] → no vibrance, v above → vibrance up, below (or NaN) → vibrance down
        int kernel = 0.9999989867210388f <= vib ? (vib <= 1.0000009536743164f ? 0 : 1) : 2;
        var up = new PostProcessingVibrance.Up(vib, sat); var down = new PostProcessingVibrance.Down(vib, sat);

        var mw = WhiteScaledMatrix();
        var minv = Mat3F.Inverse(mw);
        // ---- Tiler::Run(rect, {256,256}) column tiles: each tile computes its own Lab lines (SSE loop + scalar tail
        // with the exact-division cube root) over the parent image, replicates the first/last computed sample into the 3-sample margin
        // at the image edges, and runs the H/V convolutions with Lumen's tap associations and scalar tails. Row tiling has no effect.
        const int margin = 3;
        float[]? pooledSrc = ext is null ? ScratchPool.Rent<float>(w * h * 4) : null;
        if (pooledSrc is not null) Array.Copy(rgba, pooledSrc, w * h * 4);
        var src = ext?.Data ?? pooledSrc!;   // Lumen writes a separate output image: tiles read the untouched input (incl. their margins)
        int ew = ext?.W ?? w, eh = ext?.H ?? h, vx = ext?.VX ?? 0, vy = ext?.VY ?? 0;
        if (ext is not null && comp is not null) comp = compExt ?? throw new ArgumentException("compExt required with ext");   // no companion (denoiser absent/skipped) ⇒ no LDiff path
        int rs = Math.Max(-margin, -vy), re = Math.Min(h + margin, eh - vy), hh = re - rs;   // rows with parent data (region coordinates)
        int nx = w / 256 + ((w % 256) * 2 > 256 ? 1 : 0); if (nx < 1) nx = 1;
        float[]? k1 = null, k2 = null, kb = null; int n1 = 0, n2 = 0, nb = 0; float amount = 0f;
        if (c > Eps) { float s1, s2; if (monotoneSharpening) (s1, s2) = MonotoneSharpenSigmas(gain, c, d); else (s1, s2, _) = SharpenParams(gain, c, d); n1 = TapsFor(s1); k1 = GaussianTaps(n1, s1); n2 = TapsFor(s2); k2 = GaussianTaps(n2, s2); amount = c; }
        else if (c < -Eps)
        {   // the blur path: σ = (float)((double)|c| / exp(log2(1.0 / (double)d))) with the
            // CRT double log2 / exp, a 7-tap ConvLineFactory when σ ≥ 1.3, else 5-tap, over the Lab line's L; it replaces L
            float sb = monotoneSharpening ? MonotoneBlurSigma(c, d) : (float)((double)MathF.Abs(c) / WineCrt.Exp(WineCrt.Log2(1.0 / (double)d)));
            nb = sb >= 1.29999995f ? 7 : 5; kb = GaussianTaps(nb, sb);
        }
        // The column tiles are independent (each reads the untouched input and writes its own columns) — spread over threads when
        // few stage runs are in progress; inside a tile the Lab line, both blur passes and the reconstruction run lane-parallel and by row bands
        using var running = StageParallel.Enter();
        bool spread = StageParallel.Spread && nx > 1;
        var dbg = Environment.GetEnvironmentVariable("LUX_PP_DEBUG");
        void Tile(int ti)
        {
            int x0 = 256 * ti, x1 = Math.Min(x0 + (ti == nx - 1 ? 512 : 256), w), tw = x1 - x0, lw = tw + 2 * margin;
            var L = ScratchPool.Rent<float>(hh * lw); var A = ScratchPool.Rent<float>(hh * tw); var B = ScratchPool.Rent<float>(hh * tw);
            int st = Math.Max(-margin, -x0 - vx), en = Math.Min(tw + margin, ew - vx - x0), n4 = (en - st) & ~3;
            var phl = Diagnostics.PhaseLog.Time("pp.lab");
            StageParallel.For(re - rs, (long)(en - st) * 40, (ya, yb) =>
            {
                for (int yy = rs + ya; yy < rs + yb; yy++)
                {
                    int y = yy - rs;
                    LabRow(src, ((yy + vy) * ew + x0 + vx) * 4, st, en, n4, mw, L, y * lw + margin, A, B, y * tw, tw);
                    for (int x = -margin; x < st; x++) L[y * lw + margin + x] = L[y * lw + margin + st];
                    for (int x = en; x < tw + margin; x++) L[y * lw + margin + x] = L[y * lw + margin + en - 1];
                }
            });
            phl.Dispose();
            var phg = Diagnostics.PhaseLog.Time("pp.blur");
            float[]? blur1 = null, blur2 = null;
            if (k1 is not null) { blur1 = SeparableGaussian(L, tw, hh, lw, margin, k1!, n1); blur2 = SeparableGaussian(L, tw, hh, lw, margin, k2!, n2); }
            float[]? lblur = kb is not null ? SeparableGaussian(L, tw, hh, lw, margin, kb, nb) : null;
            float[]? ld = null;   // LDiff line per row (tw values) (+ optional 5-tap blur)
            if (ldiffPath)
            {
                var LD = ScratchPool.Rent<float>(hh * lw);
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
                else { ld = ScratchPool.Rent<float>(hh * tw); for (int y = 0; y < hh; y++) Array.Copy(LD, y * lw + margin, ld, y * tw, tw); }
                ScratchPool.Return(LD);
            }
            if (dbg is string dbgs && dbgs.Split(',') is { Length: 2 } dd && int.Parse(dd[0]) >= x0 && int.Parse(dd[0]) < x1)
            {
                int qx = int.Parse(dd[0]) - x0, qy = int.Parse(dd[1]) - rs;
                Console.Error.WriteLine($"[pp] tile {ti} [{x0},{x1}) st {st} en {en} n4 {n4} taps {n1}/{n2} x {qx}: L[-3..+3] {string.Join(" ", Enumerable.Range(-3, 7).Select(k => L[qy * lw + margin + qx + k].ToString("R")))} blur1 {blur1?[qy * tw + qx]:R} blur2 {blur2?[qy * tw + qx]:R} A {A[qy * tw + qx]:R} B {B[qy * tw + qx]:R}");
            }
            // ---- reconstruction (row loop of the three kernels)
            phg.Dispose();
            using var phr = Diagnostics.PhaseLog.Time("pp.recon");
            float aS = AScale * sat, bS = BScale * sat;   // the no-vibrance tail's 0.002·sat / 0.005·sat
            StageParallel.For(h, (long)tw * 30, (ya, yb) =>
            {
            float[]? lpRow = kernel != 0 ? new float[tw] : null;
            for (int y = ya; y < yb; y++)
            {
                int xv = 0;
                if (kernel == 0) xv = ReconstructBody(rgba, (y * w + x0) * 4, L, (y - rs) * lw + margin, A, B, (y - rs) * tw, blur1, blur2, amount, lblur, ld, a, sat, minv, tw & ~3);
                else
                {
                    // the vibrance kernels: the sharpened lightness of the body columns first (as below), then their 4-wide body eight at a time
                    int end = tw & ~3;
                    for (int x = 0; x < end; x++)
                    {
                        int i = (y - rs) * tw + x; float ls = L[(y - rs) * lw + margin + x], lsharp = ls;
                        if (blur1 is not null) { float dog = (blur1[i] - blur2![i]) * amount; if (dog < -20f) dog = -20f; if (dog > 20f) dog = 20f; lsharp = dog + ls; }
                        else if (lblur is not null) lsharp = lblur[i];
                        float lp = ld is not null ? ld[i] * a + lsharp : lsharp; if (!(lp > 0f)) lp = 0f;
                        lpRow![x] = lp;
                    }
                    xv = PostProcessingVibrance.BodyRow(kernel, up, down, lpRow!, A, B, (y - rs) * tw, minv, rgba, (y * w + x0) * 4, end);
                }
                for (int x = xv; x < tw; x++)
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
                    else if (lblur is not null) lsharp = lblur[i];
                    // `maxps` (4-wide body) / `maxss` (tw&3 tail), zero in the second operand ⇒ `v > 0 ? v : 0`
                    float lp = ld is not null ? ld[i] * a + lsharp : lsharp; if (!(lp > 0f)) lp = 0f;
                    bool tailCol = x >= (tw & ~3);
                    if (kernel != 0)
                    {   // the vibrance kernels (PostProcessingVibrance): chroma ratio (v > 1 also the lightness), then the shared reconstruction
                        float rr; float lq = lp;
                        if (kernel == 1) (lq, rr) = tailCol ? up.Tail(lp, A[i], B[i]) : up.Vec(lp, A[i], B[i]);
                        else rr = tailCol ? down.Tail(A[i], B[i]) : down.Vec(A[i], B[i]);
                        var (vr, vg, vb) = PostProcessingVibrance.Reconstruct(lq, A[i], B[i], rr, minv, tailCol);
                        rgba[o] = vr; rgba[o + 1] = vg; rgba[o + 2] = vb; rgba[o + 3] = 1f;
                        continue;
                    }
                    float fy = (lp + 16.0f) * Inv116;
                    // 4-wide body: (a·sat)·0.002 / (b·sat)·0.005; tail: a·(0.002·sat) / b·(0.005·sat)
                    float fx = tailCol ? A[i] * aS + fy : A[i] * sat * AScale + fy;
                    float fz = tailCol ? fy - B[i] * bS : fy - B[i] * sat * BScale;
                    float X = fx < Cbrt6_29 ? fx * LinK + LinOff : fx * fx * fx;
                    float Y = lp < L8 ? lp * YLin : fy * fy * fy;
                    float Z = fz < Cbrt6_29 ? fz * LinK + LinOff : fz * fz * fz;
                    float r = Dot3(minv[0], X, minv[1], Y, minv[2], Z);
                    float g = Dot3(minv[3], X, minv[4], Y, minv[5], Z);
                    float bb = Dot3(minv[6], X, minv[7], Y, minv[8], Z);
                    // The no-vibrance kernel's 4-wide row loop stores the RGBA quads unclamped — Lumen keeps −2.3e-7 for an
                    // out-of-gamut R. Its `tw & 3` scalar tail instead assembles the pixel lane by lane,
                    // takes alpha = 1.0 and finishes with `maxps` against zero — so the
                    // last `tw % 4` columns of every 256-px column tile, and only those, are clamped at 0. Only the remainder-absorbing
                    // last tile can have `tw % 4 ≠ 0`, so in practice this is the tile ending on the frame's right edge.
                    if (x >= (tw & ~3)) { if (!(r > 0f)) r = 0f; if (!(g > 0f)) g = 0f; if (!(bb > 0f)) bb = 0f; }
                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = bb; rgba[o + 3] = 1f;
                }
            }
            });
            ScratchPool.Return(L); ScratchPool.Return(A); ScratchPool.Return(B);
            ScratchPool.Return(blur1); ScratchPool.Return(blur2); ScratchPool.Return(lblur); ScratchPool.Return(ld);
        }
        if (spread) Parallel.For(0, nx, Tile);
        else for (int ti = 0; ti < nx; ti++) Tile(ti);
        ScratchPool.Return(pooledSrc);
    }

    /// <summary>One row of the Lab line: x in [st, en) of the column tile, the first <paramref name="n4"/> through the vector-path cube root
    /// (eight lanes at a time, then one by one), the rest through the scalar-tail one. L at lb + x, A / B at ab + x for 0 ≤ x &lt; tw.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static unsafe void LabRow(float[] src, int o0, int st, int en, int n4, float[] mw, float[] L, int lb, float[] A, float[] B, int ab, int tw)
    {
        int x = st, vecEnd = st + n4;
        if (Simd.IsWideSupported)
        {
            var idx = Vector256.Create(0, 4, 8, 12, 16, 20, 24, 28); var zero = Vector256<float>.Zero;
            var m0 = Vector256.Create(mw[0]); var m1 = Vector256.Create(mw[1]); var m2 = Vector256.Create(mw[2]); var m3 = Vector256.Create(mw[3]); var m4 = Vector256.Create(mw[4]);
            var m5 = Vector256.Create(mw[5]); var m6 = Vector256.Create(mw[6]); var m7 = Vector256.Create(mw[7]); var m8 = Vector256.Create(mw[8]);
            fixed (float* sp = src)
            for (; x + 8 <= vecEnd; x += 8)
            {
                float* p = sp + o0 + x * 4;
                var r = Simd.GatherVector256(p, idx, 4); var g = Simd.GatherVector256(p + 1, idx, 4); var bl = Simd.GatherVector256(p + 2, idx, 4);
                var X = (m0 * r + m1 * g) + m2 * bl; var Y = (m3 * r + m4 * g) + m5 * bl; var Z = (m6 * r + m7 * g) + m8 * bl;
                X = Vector256.ConditionalSelect(Vector256.LessThan(X, zero), zero, X); Y = Vector256.ConditionalSelect(Vector256.LessThan(Y, zero), zero, Y); Z = Vector256.ConditionalSelect(Vector256.LessThan(Z, zero), zero, Z);
                var fx = LabF8(X); var fy = LabF8(Y); var fz = LabF8(Z);
                (fy * Vector256.Create(116.0f) + Vector256.Create(-16.0f)).CopyTo(L, lb + x);
                var av = (fx - fy) * Vector256.Create(500.0f); var bv = (fy - fz) * Vector256.Create(200.0f);
                if (x >= 0 && x + 8 <= tw) { av.CopyTo(A, ab + x); bv.CopyTo(B, ab + x); }
                else for (int k = 0; k < 8; k++) if (x + k >= 0 && x + k < tw) { A[ab + x + k] = av.GetElement(k); B[ab + x + k] = bv.GetElement(k); }
            }
        }
        for (; x < en; x++)
        {
            bool scalar = x >= vecEnd;
            int o = o0 + x * 4;
            float r = src[o], g = src[o + 1], bl = src[o + 2];
            float X = Dot3(mw[0], r, mw[1], g, mw[2], bl);
            float Y = Dot3(mw[3], r, mw[4], g, mw[5], bl);
            float Z = Dot3(mw[6], r, mw[7], g, mw[8], bl);
            if (X < 0f) X = 0f; if (Y < 0f) Y = 0f; if (Z < 0f) Z = 0f;
            float fx = scalar ? LabFBitsScalar(X) : LabFBits(X), fy = scalar ? LabFBitsScalar(Y) : LabFBits(Y), fz = scalar ? LabFBitsScalar(Z) : LabFBits(Z);
            L[lb + x] = fy * 116.0f + -16.0f;
            if (x >= 0 && x < tw) { A[ab + x] = (fx - fy) * 500.0f; B[ab + x] = (fy - fz) * 200.0f; }
        }
    }

    /// <summary><see cref="LabFBits"/> on eight lanes (the same integer seed, the rcpps model, the same float steps).</summary>
    static Vector256<float> LabF8(Vector256<float> t)
    {
        var bits = t.AsInt32();
        var i = Vector256.ShiftRightArithmetic(bits, 2) + Vector256.ShiftRightArithmetic(bits, 4);
        i = Vector256.ShiftRightArithmetic(i, 4) + i;
        var y0 = (i + Vector256.Create(CbrtMagic) + Vector256.ShiftRightArithmetic(i, 8)).AsSingle();
        var r0 = IntelApprox.Reciprocal(y0 * y0);
        var s1 = y0 + y0 + r0 * t;
        var y1 = s1 * Vector256.Create(Third);
        var y1sq = y1 * y1;
        var r1 = IntelApprox.Reciprocal(y1sq);
        var cb = (s1 * Vector256.Create(TwoThird) + ((Vector256.Create(1.0f) - y1sq * r1) * r1 + r1) * t) * Vector256.Create(Third);
        var lin = t * Vector256.Create(LabK) + Vector256.Create(LabOff);
        return Vector256.ConditionalSelect(Vector256.LessThan(t, Vector256.Create(LabT0)), lin, cb);
    }

    /// <summary>The no-vibrance reconstruction of the body columns [0, <paramref name="end"/>) (end = tw &amp; ~3) of one row, eight at a time with the
    /// 4-wide body's associations ((a·sat)·0.002, no clamp); returns the first column it did not do.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static int ReconstructBody(float[] rgba, int o0, float[] L, int lb, float[] A, float[] B, int ab, float[]? blur1, float[]? blur2, float amount, float[]? lblur, float[]? ld, float a, float sat, float[] minv, int end)
    {
        if (!Simd.IsWideFloatSupported) return 0;
        int x = 0;
        var zero = Vector256<float>.Zero; var one = Vector256.Create(1f);
        var c16 = Vector256.Create(16.0f); var inv116 = Vector256.Create(Inv116); var satv = Vector256.Create(sat); var asc = Vector256.Create(AScale); var bsc = Vector256.Create(BScale);
        var cb = Vector256.Create(Cbrt6_29); var lk = Vector256.Create(LinK); var lo = Vector256.Create(LinOff); var l8 = Vector256.Create(L8); var yl = Vector256.Create(YLin);
        var am = Vector256.Create(amount); var lim = Vector256.Create(20f); var nlim = Vector256.Create(-20f); var av = Vector256.Create(a);
        var n0 = Vector256.Create(minv[0]); var n1 = Vector256.Create(minv[1]); var n2 = Vector256.Create(minv[2]); var n3 = Vector256.Create(minv[3]); var n4 = Vector256.Create(minv[4]);
        var n5 = Vector256.Create(minv[5]); var n6 = Vector256.Create(minv[6]); var n7 = Vector256.Create(minv[7]); var n8 = Vector256.Create(minv[8]);
        ref float ob = ref MemoryMarshal.GetArrayDataReference(rgba);
        for (; x + 8 <= end; x += 8)
        {
            var ls = Vector256.Create(L.AsSpan(lb + x, 8));
            var lsharp = ls;
            if (blur1 is not null)
            {
                var dog = (Vector256.Create(blur1.AsSpan(ab + x, 8)) - Vector256.Create(blur2!.AsSpan(ab + x, 8))) * am;
                dog = Vector256.ConditionalSelect(Vector256.LessThan(dog, nlim), nlim, dog);
                dog = Vector256.ConditionalSelect(Vector256.GreaterThan(dog, lim), lim, dog);
                lsharp = dog + ls;
            }
            else if (lblur is not null) lsharp = Vector256.Create(lblur.AsSpan(ab + x, 8));
            var lp = ld is not null ? Vector256.Create(ld.AsSpan(ab + x, 8)) * av + lsharp : lsharp;
            lp = Vector256.ConditionalSelect(Vector256.GreaterThan(lp, zero), lp, zero);
            var fy = (lp + c16) * inv116;
            var fx = Vector256.Create(A.AsSpan(ab + x, 8)) * satv * asc + fy;
            var fz = fy - Vector256.Create(B.AsSpan(ab + x, 8)) * satv * bsc;
            var X = Vector256.ConditionalSelect(Vector256.LessThan(fx, cb), fx * lk + lo, fx * fx * fx);
            var Y = Vector256.ConditionalSelect(Vector256.LessThan(lp, l8), lp * yl, fy * fy * fy);
            var Z = Vector256.ConditionalSelect(Vector256.LessThan(fz, cb), fz * lk + lo, fz * fz * fz);
            var r = (n0 * X + n1 * Y) + n2 * Z; var g = (n3 * X + n4 * Y) + n5 * Z; var bb = (n6 * X + n7 * Y) + n8 * Z;
            // interleave to RGBA (alpha 1): pure data movement
            var t0 = Simd.UnpackLow(r, g); var t1 = Simd.UnpackHigh(r, g); var t2 = Simd.UnpackLow(bb, one); var t3 = Simd.UnpackHigh(bb, one);
            var u0 = Simd.Shuffle(t0, t2, 0b01_00_01_00); var u1 = Simd.Shuffle(t0, t2, 0b11_10_11_10); var u2 = Simd.Shuffle(t1, t3, 0b01_00_01_00); var u3 = Simd.Shuffle(t1, t3, 0b11_10_11_10);
            nuint q = (nuint)(o0 + x * 4);
            Simd.Permute2x128(u0, u1, 0x20).StoreUnsafe(ref ob, q); Simd.Permute2x128(u2, u3, 0x20).StoreUnsafe(ref ob, q + 8);
            Simd.Permute2x128(u0, u1, 0x31).StoreUnsafe(ref ob, q + 16); Simd.Permute2x128(u2, u3, 0x31).StoreUnsafe(ref ob, q + 24);
        }
        return x;
    }

    /// <summary>The scalar tail of the Lab line: the same cube-root seed and first step, but the second step uses an exact
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

    /// <summary>Separable Gaussian (H then V) of one tile line set with the `ConvLineFactory` associations: 3-tap `k0·i₁ + k1·c`,
    /// 5-tap `k1·i₁ + (k0·o₂ + k2·c)`, 7-tap vector `(k2·i₁ + k1·o₂) + (k0·o₃ + k3·c)` and the sequential scalar tail
    /// `((k3·c + k0·o₃) + k1·o₂) + k2·i₁` on the last `4 + tw%4` columns (H) / `tw%4` columns (V); rows clamp at the image edges.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static float[] SeparableGaussian(float[] L, int w, int h, int lw, int margin, float[] k, int n)
    {
        int vecEnd = n == 5 ? (w & ~3) : (w & ~3) - 4; if (vecEnd < 0) vecEnd = 0;
        var hbuf = ScratchPool.Rent<float>(h * w);
        var K0 = Vector256.Create(k[0]); var K1 = Vector256.Create(k[1]); var K2 = Vector256.Create(n > 2 ? k[2] : 0f); var K3 = Vector256.Create(n > 3 ? k[3] : 0f);
        // lane-parallel over x with each column range's own association (the 7-tap vector form below vecEnd / vTail, the sequential tail after)
        StageParallel.For(h, (long)w * n, (ya, yb) =>
        {
            ref float lb = ref MemoryMarshal.GetArrayDataReference(L); ref float hb = ref MemoryMarshal.GetArrayDataReference(hbuf);
            for (int y = ya; y < yb; y++)
            {
                int x = 0, rowEnd = n == 7 ? Math.Min(vecEnd, w) : w;
                if (Vector256.IsHardwareAccelerated)
                    for (; x + 8 <= rowEnd; x += 8)
                    {
                        nuint c0 = (nuint)(y * lw + margin + x);
                        var cc = Vector256.LoadUnsafe(ref lb, c0); var i1 = Vector256.LoadUnsafe(ref lb, c0 + 1) + Vector256.LoadUnsafe(ref lb, c0 - 1);
                        Vector256<float> v;
                        if (n == 3) v = K0 * i1 + K1 * cc;
                        else
                        {
                            var o2 = Vector256.LoadUnsafe(ref lb, c0 - 2) + Vector256.LoadUnsafe(ref lb, c0 + 2);
                            if (n == 5) v = K1 * i1 + (K0 * o2 + K2 * cc);
                            else { var o3 = Vector256.LoadUnsafe(ref lb, c0 - 3) + Vector256.LoadUnsafe(ref lb, c0 + 3); v = (K2 * i1 + K1 * o2) + (K0 * o3 + K3 * cc); }
                        }
                        v.StoreUnsafe(ref hb, (nuint)(y * w + x));
                    }
                for (; x < w; x++)
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
            }
        });
        var vbuf = ScratchPool.Rent<float>(h * w);
        int Row(int yy) => yy < 0 ? 0 : yy >= h ? h - 1 : yy;
        int vTail = w & ~3;
        StageParallel.For(h, (long)w * n, (ya, yb) =>
        {
            ref float hb = ref MemoryMarshal.GetArrayDataReference(hbuf); ref float vb = ref MemoryMarshal.GetArrayDataReference(vbuf);
            for (int y = ya; y < yb; y++)
            {
                int r0 = Row(y) * w, rp1 = Row(y + 1) * w, rm1 = Row(y - 1) * w, rp2 = Row(y + 2) * w, rm2 = Row(y - 2) * w, rp3 = Row(y + 3) * w, rm3 = Row(y - 3) * w;
                int x = 0, rowEnd = n == 7 ? Math.Min(vTail, w) : w;
                if (Vector256.IsHardwareAccelerated)
                    for (; x + 8 <= rowEnd; x += 8)
                    {
                        nuint ux = (nuint)x;
                        var cc = Vector256.LoadUnsafe(ref hb, (nuint)r0 + ux); var i1 = Vector256.LoadUnsafe(ref hb, (nuint)rp1 + ux) + Vector256.LoadUnsafe(ref hb, (nuint)rm1 + ux);
                        Vector256<float> v;
                        if (n == 3) v = K0 * i1 + K1 * cc;
                        else
                        {
                            var o2 = Vector256.LoadUnsafe(ref hb, (nuint)rp2 + ux) + Vector256.LoadUnsafe(ref hb, (nuint)rm2 + ux);
                            if (n == 5) v = K1 * i1 + (K0 * o2 + K2 * cc);
                            else { var o3 = Vector256.LoadUnsafe(ref hb, (nuint)rp3 + ux) + Vector256.LoadUnsafe(ref hb, (nuint)rm3 + ux); v = (K2 * i1 + K1 * o2) + (K0 * o3 + K3 * cc); }
                        }
                        v.StoreUnsafe(ref vb, (nuint)(y * w + x));
                    }
                for (; x < w; x++)
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
            }
        });
        ScratchPool.Return(hbuf);
        return vbuf;
    }
}
