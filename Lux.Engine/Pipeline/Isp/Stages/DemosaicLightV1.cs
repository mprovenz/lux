using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// Lumen's `lt::Internal::A::DemosaickLightV1&lt;rx,ry&gt;` (dispatcher, tile lambda for &lt;1,0&gt;,
/// row functions for the source rows × per-site neutral, for the guide planes and for
/// the color-difference plane). Halide-style pipeline, ported plane by plane with Lumen's
/// float operation order and SSE `rcpss/rcpps` approximations (`IntelApprox.Reciprocal*`, the reference CPU's table — Lumen's
/// own output is CPU-dependent at that level):
///   S(x,y)  = src(clamp-by-parity) · neutral[site]                       (source row function)
///   C       = 5×5 filter of S at G sites (weights 56, 6, −4, −2, 1; /64)   (guide row function, inner)
///   B       = C at G sites; at R/B sites the gradient-inverse blend of C±1 minus the S mid-points, eps₂
///   A       = B at R/B sites; at G sites the refined blend of (B − S) differences, eps₂
///   D       = raw − A (raw un-scaled; replicated by parity outside the source) (color-difference row function)
///   out     = A + directional gradient-inverse blend of D (eps₁), alpha 1  (tile lambda)
/// eps₁ = 0.009765625·max(neutral), eps₂ = 0.0009765625·max(neutral).
/// </summary>
public static class DemosaicLightV1
{
    public const float Eps1Scale = 0.009765625f, Eps2Scale = 0.0009765625f;
    private const float K56 = 56f, K6 = 6f, Km4 = -4f, K1_64 = 0.015625f, Half = 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static float Rcp(float x) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static Vector128<float> Rcp4(Vector128<float> v) => IntelApprox.Reciprocal(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static float Abs(float x) => MathF.Abs(x);

    /// <summary>Diagnostic: `LUX_DEMOSAIC_DUMP=&lt;dir&gt;` writes the C, B and A planes of each call as raw floats.</summary>
    static readonly string? DumpDir = Environment.GetEnvironmentVariable("LUX_DEMOSAIC_DUMP");

    /// <summary>Demosaic <paramref name="src"/> (w×h float Bayer, 0..1 normalized) over <paramref name="roi"/> into
    /// <paramref name="dst"/> (RGBA, roi-sized, stride roi width). Red at (rx, ry).</summary>
    public static void Run(float[] src, int w, int h, RectI roi, int rx, int ry, float[] neutral, Vec4F[] dst)
    {
        // The plane code needs eight-lane vectors and a region at least 16 pixels wide inside the source columns. Its range
        // checks: every source index of the clamped reads is inside src, and dst holds the region.
        if (!(Vector256.IsHardwareAccelerated && Simd.IsWideSupported && DumpDir is null && roi.Width >= 16 && roi.Height > 0
            && roi.X0 >= 0 && roi.X1 <= w && w >= 2 && h >= 2 && (long)w * h <= src.Length && (long)roi.Width * roi.Height <= dst.Length))
        {
            RunScalar(src, w, h, roi, rx, ry, neutral, dst);
            return;
        }
        // A plane value depends only on the source and its position, so a block of the region gives the same pixels as the whole
        // region. Large regions go in blocks of about Block × Block pixels: the planes of a block stay small enough for the caches.
        int nbx = (roi.Width + Block - 1) / Block, nby = (roi.Height + Block - 1) / Block;
        for (int by = 0; by < nby; by++)
            for (int bx = 0; bx < nbx; bx++)
            {
                var block = new RectI(roi.X0 + roi.Width * bx / nbx, roi.Y0 + roi.Height * by / nby, roi.X0 + roi.Width * (bx + 1) / nbx, roi.Y0 + roi.Height * (by + 1) / nby);
                RunPlanes(src, w, h, block, rx, ry, neutral, dst, roi.Width, (block.Y0 - roi.Y0) * roi.Width + (block.X0 - roi.X0));
            }
    }

    /// <summary>The block size of the plane code (pixels per side, at most): about the size of a pipeline tile.</summary>
    const int Block = 768;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vector256<float> Abs8(Vector256<float> v) => (v.AsUInt32() & Vector256.Create(0x7fffffffu)).AsSingle();
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vector256<float> Rcp8(Vector256<float> v) => IntelApprox.Reciprocal(v);
    /// <summary>Lane mask: the even lanes (0, 2, 4, 6) or the odd lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<float> Lanes(bool even) => (even ? Vector256.Create(-1, 0, -1, 0, -1, 0, -1, 0) : Vector256.Create(0, -1, 0, -1, 0, -1, 0, -1)).AsSingle();
    /// <summary>Eight floats of a plane from index <paramref name="i"/>. The plane loops keep every index inside the plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vector256<float> L(float[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static void St(Vector256<float> v, float[] a, int i) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);

    /// <summary>
    /// The planes of <see cref="RunScalar"/>, each computed once over the extended rect (the region ± 8, origin (ex0, ey0)): the scaled
    /// source S, then C, B and A, then the color-difference plane D over the region ± 1, then the output. Eight pixels per vector:
    /// each lane does the scalar operations of its pixel in the same order (the reciprocal model works per lane), and a lane mask
    /// picks each lane's site formula. Columns that do not fill a vector take the scalar formula on the same planes. The output
    /// pixel (x, y) goes to dst[dstOffset + (y − roi.Y0)·dstStride + (x − roi.X0)].
    /// <para>The planes are not cleared. Each step writes every element that a later step reads (plane ranges, rows and columns):
    /// S covers [0, size); C covers [2, size − 2), zero at the R/B sites; B covers [3, size − 3) and reads C in [2, size − 2);
    /// A covers [5, size − 5) and reads B in [3, size − 3); D reads A in [7, size − 7), which holds the clamped source columns
    /// 0, W − 2 and W − 1 because the region is inside the source columns; the output reads A in [7, size − 7) and all of D.</para>
    /// </summary>
    static void RunPlanes(float[] src, int w, int h, RectI roi, int rx, int ry, float[] neutral, Vec4F[] dst, int dstStride, int dstOffset)
    {
        if (neutral[0] <= 0f || neutral[1] <= 0f || neutral[2] <= 0f) throw new InvalidOperationException("invalid neutral white!");
        if (((w | h) & 1) != 0) throw new InvalidOperationException("invalid bayer image size!");
        float mx = neutral[0]; if (mx <= neutral[1]) mx = neutral[1]; if (mx <= neutral[2]) mx = neutral[2];
        float eps1 = mx * Eps1Scale, eps2 = Eps2Scale * mx;
        int gSum = 1 - ((rx + ry) & 1);   // (x+y)&1 of the G sites
        float[] nsite = new float[4];
        for (int p = 0; p < 4; p++)
        {
            int yp = p >> 1, xp = p & 1;
            nsite[p] = (xp == rx && yp == ry) ? neutral[0] : (xp != rx && yp != ry) ? neutral[2] : neutral[1];
        }
        int W = w, H = h;
        int ex0 = roi.X0 - 8, ey0 = roi.Y0 - 8, ew = roi.Width + 16, eh = roi.Height + 16, n = ew * eh;
        var Sp = ScratchPool.Rent<float>(n); var C = ScratchPool.Rent<float>(n); var B = ScratchPool.Rent<float>(n); var A = ScratchPool.Rent<float>(n);
        // ---- S: src (clamped by parity) · neutral[site] ----
        for (int Y = 0; Y < eh; Y++)
        {
            int y = ey0 + Y, cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            int row = cy * W, o = Y * ew; float n0 = nsite[(y & 1) << 1], n1 = nsite[((y & 1) << 1) | 1];
            for (int X = 0; X < ew; X++)
            {
                int x = ex0 + X, cx = x < 0 ? (x & 1) : x >= W ? W - 2 + (x & 1) : x;
                Sp[o + X] = src[row + cx] * ((x & 1) == 0 ? n0 : n1);
            }
        }
        var vKm4 = Vector256.Create(Km4); var vK6 = Vector256.Create(K6); var vK56 = Vector256.Create(K56); var vK164 = Vector256.Create(K1_64);
        var vHalf = Vector256.Create(Half); var vE2 = Vector256.Create(eps2); var vE1 = Vector256.Create(eps1);
        int exy = ex0 + ey0;   // the G lanes of a vector at plane position (X, Y): Lanes(((X + Y + exy) & 1) == gSum)
        // ---- C: 5×5 filter at G sites, zero at R/B sites ----
        for (int Y = 2; Y < eh - 2; Y++)
        {
            int X = 2;
            for (; X + 8 <= ew - 2; X += 8)
            {
                int i = Y * ew + X;
                var c = L(Sp, i);
                var diag = ((L(Sp, i - ew + 1) + L(Sp, i - ew - 1)) + L(Sp, i + ew - 1)) + L(Sp, i + ew + 1);
                var axial = ((L(Sp, i + ew) + L(Sp, i - ew)) + L(Sp, i - 1)) + L(Sp, i + 1);
                var far = ((L(Sp, i - 2) + L(Sp, i + 2)) + L(Sp, i - 2 * ew)) + L(Sp, i + 2 * ew);
                var v = ((diag * vKm4 + (axial * vK6 + c * vK56)) + L(Sp, i - 2 * ew - 1)) - (far + far);
                v = ((((((v + L(Sp, i - 2 * ew + 1)) + L(Sp, i - ew - 2)) + L(Sp, i - ew + 2)) + L(Sp, i + ew - 2)) + L(Sp, i + ew + 2)) + L(Sp, i + 2 * ew - 1)) + L(Sp, i + 2 * ew + 1);
                St(v * vK164 & Lanes(((X + Y + exy) & 1) == gSum), C, i);
            }
            for (; X < ew - 2; X++) C[Y * ew + X] = ((X + Y + exy) & 1) == gSum ? CAt(Sp, ew, Y * ew + X) : 0f;
        }
        // ---- B ----
        for (int Y = 3; Y < eh - 3; Y++)
        {
            int X = 3;
            for (; X + 8 <= ew - 3; X += 8)
            {
                int i = Y * ew + X;
                var s = L(Sp, i);
                var cl = L(C, i - 1); var cr = L(C, i + 1); var cu = L(C, i - ew); var cd = L(C, i + ew);
                var sl = L(Sp, i - 2); var sr = L(Sp, i + 2); var su = L(Sp, i - 2 * ew); var sd = L(Sp, i + 2 * ew);
                var gh = Abs8(cl - cr); var gv = Abs8(cu - cd);
                var w0 = Rcp8(gh + (Abs8(sl - s) + vE2)); var w1 = Rcp8(gh + (Abs8(sr - s) + vE2)); var w2 = Rcp8(gv + (Abs8(su - s) + vE2)); var w3 = Rcp8(gv + (Abs8(sd - s) + vE2));
                var norm = Rcp8((w3 + w1) + (w2 + w0));
                var t0 = (cl - (s + sl) * vHalf) * w0; var t1 = (cr - (s + sr) * vHalf) * w1; var t2 = (cu - (s + su) * vHalf) * w2; var t3 = (cd - (s + sd) * vHalf) * w3;
                var bv = ((t3 + t1) + (t2 + t0)) * norm + s;
                St(Vector256.ConditionalSelect(Lanes(((X + Y + exy) & 1) == gSum), L(C, i), bv), B, i);
            }
            for (; X < ew - 3; X++) { int i = Y * ew + X; B[i] = ((X + Y + exy) & 1) == gSum ? C[i] : BAt(Sp, C, ew, i, eps2); }
        }
        // ---- A ----
        for (int Y = 5; Y < eh - 5; Y++)
        {
            int X = 5;
            for (; X + 8 <= ew - 5; X += 8)
            {
                int i = Y * ew + X;
                var s = L(Sp, i); var b = L(B, i);
                var sl = L(Sp, i - 2); var sr = L(Sp, i + 2); var su = L(Sp, i - 2 * ew); var sd = L(Sp, i + 2 * ew);
                var bl2 = L(B, i - 2); var br2 = L(B, i + 2); var bu2 = L(B, i - 2 * ew); var bd2 = L(B, i + 2 * ew);
                var bl = L(B, i - 1); var br = L(B, i + 1); var bu = L(B, i - ew); var bd = L(B, i + ew);
                var w0 = Rcp8(Abs8(bl - b) + (Abs8(sl - s) + vE2)); var w1 = Rcp8(Abs8(br - b) + (Abs8(sr - s) + vE2));
                var w2 = Rcp8(Abs8(bu - b) + (Abs8(su - s) + vE2)); var w3 = Rcp8(Abs8(bd - b) + (Abs8(sd - s) + vE2));
                var norm = Rcp8((w3 + w1) + (w2 + w0));
                var d = b - s;
                var t0 = (w0 * vHalf) * (d + (bl2 - sl)); var t1 = (w1 * vHalf) * (d + (br2 - sr)); var t2 = (w2 * vHalf) * (d + (bu2 - su)); var t3 = (w3 * vHalf) * (d + (bd2 - sd));
                var av = ((t3 + t1) + (t2 + t0)) * norm + s;
                St(Vector256.ConditionalSelect(Lanes(((X + Y + exy) & 1) == gSum), av, b), A, i);
            }
            for (; X < ew - 5; X++) { int i = Y * ew + X; A[i] = ((X + Y + exy) & 1) != gSum ? B[i] : AAt(Sp, B, ew, i, eps2); }
        }
        // ---- D = raw source (clamped by parity, not neutral-scaled) − A over the region ± 1 ----
        int dw = roi.Width + 2, dh = roi.Height + 2; var Dp = ScratchPool.Rent<float>(dw * dh);
        for (int j = 0; j < dh; j++)
        {
            int y = roi.Y0 - 1 + j, cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            for (int k = 0; k < dw; k++)
            {
                int x = roi.X0 - 1 + k, cx = x < 0 ? 0 : x >= W ? W - 2 + (x & 1) : x;   // left margin: D[0]; right: by parity
                Dp[j * dw + k] = src[cy * W + cx] - A[(y - ey0) * ew + (cx - ex0)];
            }
        }
        // ---- output quads ----
        var one = Vector256.Create(1f);
        ref float dstF = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Vec4F, float>(dst.AsSpan()));
        for (int y = roi.Y0; y < roi.Y1; y++)
        {
            int Y = y - ey0; bool rowR = (y & 1) == ry;
            int x = roi.X0;
            for (; x + 8 <= roi.X1; x += 8)
            {
                int i = Y * ew + (x - ex0), jd = (y - roi.Y0 + 1) * dw + (x - roi.X0 + 1);
                var a = L(A, i); var d = L(Dp, jd);
                var aL = L(A, i - 1); var aR = L(A, i + 1); var aU = L(A, i - ew); var aD = L(A, i + ew);
                var dL = L(Dp, jd - 1); var dR = L(Dp, jd + 1); var dU = L(Dp, jd - dw); var dD = L(Dp, jd + dw);
                // G sites: horizontal and vertical blends
                var wl = Rcp8(Abs8(a - aL) + vE1); var wr = Rcp8(Abs8(a - aR) + vE1); var wu = Rcp8(Abs8(a - aU) + vE1); var wd = Rcp8(Abs8(a - aD) + vE1);
                var nh = Rcp8(wr + wl); var nv = Rcp8(wd + wu);
                var hv = nh * (dR * wr + dL * wl) + a; var vv = nv * (dD * wd + dU * wu) + a;
                var ad = a + d;
                // R/B sites: the axial blend gives G, the diagonal blend the other color
                var wa0 = Rcp8(Abs8(aL - a) + vE1); var wa1 = Rcp8(Abs8(aR - a) + vE1); var wa2 = Rcp8(Abs8(aU - a) + vE1); var wa3 = Rcp8(Abs8(aD - a) + vE1);
                var t0 = dL * wa0; var t1 = dR * wa1; var t2 = dU * wa2; var t3 = dD * wa3;
                var na = Rcp8((wa3 + wa1) + (wa2 + wa0));
                var gRB = ((t3 + t1) + (t2 + t0)) * na + a;
                var aUL = L(A, i - ew - 1); var aUR = L(A, i - ew + 1); var aDL = L(A, i + ew - 1); var aDR = L(A, i + ew + 1);
                var wd0 = Rcp8(Abs8(aUL - a) + vE1); var wd1 = Rcp8(Abs8(aUR - a) + vE1); var wd2 = Rcp8(Abs8(aDL - a) + vE1); var wd3 = Rcp8(Abs8(aDR - a) + vE1);
                var nd = Rcp8((wd3 + wd1) + (wd2 + wd0));
                var dv = ((L(Dp, jd + dw + 1) * wd3 + L(Dp, jd - dw + 1) * wd1) + (L(Dp, jd + dw - 1) * wd2 + L(Dp, jd - dw - 1) * wd0)) * nd + a;
                var gMask = Lanes(((x + y) & 1) == gSum);
                var rMask = rowR ? Lanes((x & 1) == rx) : Vector256<float>.Zero;   // the R sites
                var rRB = Vector256.ConditionalSelect(rMask, ad, dv); var bRB = Vector256.ConditionalSelect(rMask, dv, ad);
                var r = Vector256.ConditionalSelect(gMask, rowR ? hv : vv, rRB);
                var g = Vector256.ConditionalSelect(gMask, ad, gRB);
                var b = Vector256.ConditionalSelect(gMask, rowR ? vv : hv, bRB);
                Soa8.Store(ref Unsafe.Add(ref dstF, (dstOffset + (y - roi.Y0) * dstStride + (x - roi.X0)) * 4), r, g, b, one);
            }
            for (; x < roi.X1; x++)
            {
                int i = Y * ew + (x - ex0), jd = (y - roi.Y0 + 1) * dw + (x - roi.X0 + 1);
                float a = A[i], d = Dp[jd];
                float r, g, b;
                if (((x + y) & 1) == gSum)
                {
                    float wl = Rcp(Abs(a - A[i - 1]) + eps1), wr = Rcp(Abs(a - A[i + 1]) + eps1);
                    float wu = Rcp(Abs(a - A[i - ew]) + eps1), wd = Rcp(Abs(a - A[i + ew]) + eps1);
                    float nh = Rcp(wr + wl), nv = Rcp(wd + wu);
                    float hv = nh * (Dp[jd + 1] * wr + Dp[jd - 1] * wl) + a;
                    float vv = nv * (Dp[jd + dw] * wd + Dp[jd - dw] * wu) + a;
                    g = a + d;
                    if (rowR) { r = hv; b = vv; } else { b = hv; r = vv; }
                }
                else
                {
                    var wa = Rcp4(Vector128.Create(Abs(A[i - 1] - a) + eps1, Abs(A[i + 1] - a) + eps1, Abs(A[i - ew] - a) + eps1, Abs(A[i + ew] - a) + eps1));
                    float t0 = Dp[jd - 1] * wa[0], t1 = Dp[jd + 1] * wa[1], t2 = Dp[jd - dw] * wa[2], t3 = Dp[jd + dw] * wa[3];
                    float na = Rcp((wa[3] + wa[1]) + (wa[2] + wa[0]));
                    float gv = ((t3 + t1) + (t2 + t0)) * na + a;
                    var wd4 = Rcp4(Vector128.Create(Abs(A[i - ew - 1] - a) + eps1, Abs(A[i - ew + 1] - a) + eps1, Abs(A[i + ew - 1] - a) + eps1, Abs(A[i + ew + 1] - a) + eps1));
                    float nd = Rcp((wd4[3] + wd4[1]) + (wd4[2] + wd4[0]));
                    float dv = ((Dp[jd + dw + 1] * wd4[3] + Dp[jd - dw + 1] * wd4[1]) + (Dp[jd + dw - 1] * wd4[2] + Dp[jd - dw - 1] * wd4[0])) * nd + a;
                    g = gv;
                    if ((x & 1) == rx && rowR) { r = a + d; b = dv; } else { b = a + d; r = dv; }
                }
                dst[dstOffset + (y - roi.Y0) * dstStride + (x - roi.X0)] = new Vec4F { R = r, G = g, B = b, A = 1f };
            }
        }
        ScratchPool.Return(Sp); ScratchPool.Return(C); ScratchPool.Return(B); ScratchPool.Return(A); ScratchPool.Return(Dp);
    }

    /// <summary>C at plane index <paramref name="i"/> (a G site): the 5×5 filter of S.</summary>
    static float CAt(float[] Sp, int ew, int i)
    {
        float c = Sp[i];
        float diag = ((Sp[i - ew + 1] + Sp[i - ew - 1]) + Sp[i + ew - 1]) + Sp[i + ew + 1];
        float axial = ((Sp[i + ew] + Sp[i - ew]) + Sp[i - 1]) + Sp[i + 1];
        float far = ((Sp[i - 2] + Sp[i + 2]) + Sp[i - 2 * ew]) + Sp[i + 2 * ew];
        float v = ((diag * Km4 + (axial * K6 + c * K56)) + Sp[i - 2 * ew - 1]) - (far + far);
        v = ((((((v + Sp[i - 2 * ew + 1]) + Sp[i - ew - 2]) + Sp[i - ew + 2]) + Sp[i + ew - 2]) + Sp[i + ew + 2]) + Sp[i + 2 * ew - 1]) + Sp[i + 2 * ew + 1];
        return v * K1_64;
    }

    /// <summary>B at plane index <paramref name="i"/> (an R/B site).</summary>
    static float BAt(float[] Sp, float[] C, int ew, int i, float eps2)
    {
        float s = Sp[i];
        float cl = C[i - 1], cr = C[i + 1], cu = C[i - ew], cd = C[i + ew];
        float sl = Sp[i - 2], sr = Sp[i + 2], su = Sp[i - 2 * ew], sd = Sp[i + 2 * ew];
        float gh = Abs(cl - cr), gv = Abs(cu - cd);
        var wv = Rcp4(Vector128.Create(gh + (Abs(sl - s) + eps2), gh + (Abs(sr - s) + eps2), gv + (Abs(su - s) + eps2), gv + (Abs(sd - s) + eps2)));
        float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
        float norm = Rcp((w3 + w1) + (w2 + w0));
        float t0 = (cl - (s + sl) * Half) * w0, t1 = (cr - (s + sr) * Half) * w1, t2 = (cu - (s + su) * Half) * w2, t3 = (cd - (s + sd) * Half) * w3;
        return ((t3 + t1) + (t2 + t0)) * norm + s;
    }

    /// <summary>A at plane index <paramref name="i"/> (a G site).</summary>
    static float AAt(float[] Sp, float[] B, int ew, int i, float eps2)
    {
        float s = Sp[i], b = B[i];
        float sl = Sp[i - 2], sr = Sp[i + 2], su = Sp[i - 2 * ew], sd = Sp[i + 2 * ew];
        float bl2 = B[i - 2], br2 = B[i + 2], bu2 = B[i - 2 * ew], bd2 = B[i + 2 * ew];
        float bl = B[i - 1], br = B[i + 1], bu = B[i - ew], bd = B[i + ew];
        var wv = Rcp4(Vector128.Create(Abs(bl - b) + (Abs(sl - s) + eps2), Abs(br - b) + (Abs(sr - s) + eps2), Abs(bu - b) + (Abs(su - s) + eps2), Abs(bd - b) + (Abs(sd - s) + eps2)));
        float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
        float norm = Rcp((w3 + w1) + (w2 + w0));
        float d = b - s;
        float t0 = (w0 * Half) * (d + (bl2 - sl)), t1 = (w1 * Half) * (d + (br2 - sr)), t2 = (w2 * Half) * (d + (bu2 - su)), t3 = (w3 * Half) * (d + (bd2 - sd));
        return ((t3 + t1) + (t2 + t0)) * norm + s;
    }

    /// <summary>The scalar form: each plane value is computed where it is needed, from the clamped source.</summary>
    static void RunScalar(float[] src, int w, int h, RectI roi, int rx, int ry, float[] neutral, Vec4F[] dst)
    {
        if (neutral[0] <= 0f || neutral[1] <= 0f || neutral[2] <= 0f) throw new InvalidOperationException("invalid neutral white!");
        if (((w | h) & 1) != 0) throw new InvalidOperationException("invalid bayer image size!");
        float mx = neutral[0]; if (mx <= neutral[1]) mx = neutral[1]; if (mx <= neutral[2]) mx = neutral[2];
        float eps1 = mx * Eps1Scale, eps2 = Eps2Scale * mx;
        int gSum = 1 - ((rx + ry) & 1);   // (x+y)&1 of the G sites (R/B sites share the red position parity)

        // per-site neutral: [rowParity][colParity]
        float[] nsite = new float[4];
        for (int p = 0; p < 4; p++)
        {
            int yp = p >> 1, xp = p & 1;
            nsite[p] = (xp == rx && yp == ry) ? neutral[0] : (xp != rx && yp != ry) ? neutral[2] : neutral[1];
        }
        int W = w, H = h;
        float S(int x, int y)
        {
            int cx = x < 0 ? (x & 1) : x >= W ? W - 2 + (x & 1) : x;
            int cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            return src[cy * W + cx] * nsite[((y & 1) << 1) | (x & 1)];
        }
        // extended rect for the planes
        int ex0 = roi.X0 - 8, ey0 = roi.Y0 - 8, ew = roi.Width + 16, eh = roi.Height + 16;
        var C = new float[ew * eh]; var B = new float[ew * eh]; var A = new float[ew * eh];
        float Cg(int x, int y) => C[(y - ey0) * ew + (x - ex0)];
        float Bg(int x, int y) => B[(y - ey0) * ew + (x - ex0)];
        float Ag(int x, int y) => A[(y - ey0) * ew + (x - ex0)];
        // ---- C: 5×5 filter at G sites ----
        for (int y = ey0 + 2; y < ey0 + eh - 2; y++)
            for (int x = ex0 + 2; x < ex0 + ew - 2; x++)
            {
                if (((x + y) & 1) != gSum) continue;
                float c = S(x, y);
                float diag = ((S(x + 1, y - 1) + S(x - 1, y - 1)) + S(x - 1, y + 1)) + S(x + 1, y + 1);
                float axial = ((S(x, y + 1) + S(x, y - 1)) + S(x - 1, y)) + S(x + 1, y);
                float far = ((S(x - 2, y) + S(x + 2, y)) + S(x, y - 2)) + S(x, y + 2);
                float v = ((diag * Km4 + (axial * K6 + c * K56)) + S(x - 1, y - 2)) - (far + far);   // Lumen's operation order
                v = ((((((v + S(x + 1, y - 2)) + S(x - 2, y - 1)) + S(x + 2, y - 1)) + S(x - 2, y + 1)) + S(x + 2, y + 1)) + S(x - 1, y + 2)) + S(x + 1, y + 2);
                C[(y - ey0) * ew + (x - ex0)] = v * K1_64;
            }
        // ---- B ----
        for (int y = ey0 + 3; y < ey0 + eh - 3; y++)
            for (int x = ex0 + 3; x < ex0 + ew - 3; x++)
            {
                if (((x + y) & 1) == gSum) { B[(y - ey0) * ew + (x - ex0)] = Cg(x, y); continue; }
                float s = S(x, y);
                float cl = Cg(x - 1, y), cr = Cg(x + 1, y), cu = Cg(x, y - 1), cd = Cg(x, y + 1);
                float sl = S(x - 2, y), sr = S(x + 2, y), su = S(x, y - 2), sd = S(x, y + 2);
                float gh = Abs(cl - cr), gv = Abs(cu - cd);
                // den = |ΔC| + (|S_d − S| + eps); hsum = (l3 + l1) + (l2 + l0)
                var wv = Rcp4(Vector128.Create(gh + (Abs(sl - s) + eps2), gh + (Abs(sr - s) + eps2), gv + (Abs(su - s) + eps2), gv + (Abs(sd - s) + eps2)));
                float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
                float norm = Rcp((w3 + w1) + (w2 + w0));
                float t0 = (cl - (s + sl) * Half) * w0, t1 = (cr - (s + sr) * Half) * w1, t2 = (cu - (s + su) * Half) * w2, t3 = (cd - (s + sd) * Half) * w3;
                B[(y - ey0) * ew + (x - ex0)] = ((t3 + t1) + (t2 + t0)) * norm + s;
            }
        // ---- A ----
        for (int y = ey0 + 5; y < ey0 + eh - 5; y++)
            for (int x = ex0 + 5; x < ex0 + ew - 5; x++)
            {
                if (((x + y) & 1) != gSum) { A[(y - ey0) * ew + (x - ex0)] = Bg(x, y); continue; }   // R/B sites keep B; G sites are refined
                float s = S(x, y), b = Bg(x, y);
                float sl = S(x - 2, y), sr = S(x + 2, y), su = S(x, y - 2), sd = S(x, y + 2);
                float bl2 = Bg(x - 2, y), br2 = Bg(x + 2, y), bu2 = Bg(x, y - 2), bd2 = Bg(x, y + 2);
                float bl = Bg(x - 1, y), br = Bg(x + 1, y), bu = Bg(x, y - 1), bd = Bg(x, y + 1);
                // den = |B_near − B| + (|S_far − S| + eps); t = (w·0.5)·(d + (B_far − S_far)); hsum = (t3 + t1) + (t2 + t0)
                var wv = Rcp4(Vector128.Create(Abs(bl - b) + (Abs(sl - s) + eps2), Abs(br - b) + (Abs(sr - s) + eps2), Abs(bu - b) + (Abs(su - s) + eps2), Abs(bd - b) + (Abs(sd - s) + eps2)));
                float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
                float norm = Rcp((w3 + w1) + (w2 + w0));
                float d = b - s;
                float t0 = (w0 * Half) * (d + (bl2 - sl)), t1 = (w1 * Half) * (d + (br2 - sr)), t2 = (w2 * Half) * (d + (bu2 - su)), t3 = (w3 * Half) * (d + (bd2 - sd));
                A[(y - ey0) * ew + (x - ex0)] = ((t3 + t1) + (t2 + t0)) * norm + s;
            }
        // ---- D = S − A with parity replication outside the source ----
        // D = raw source (row/col clamped by parity, NOT neutral-scaled) − A at the requested row
        float Dg(int x, int y)
        {
            // left margin (1 px) replicates D[0] (Lumen's pair fill writes D[0],D[1] at x=−1,0); right by parity
            int cx = x < 0 ? 0 : x >= W ? W - 2 + (x & 1) : x;
            int cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            return src[cy * W + cx] - Ag(cx, y);
        }
        if (DumpDir is string dumpDir)
        {
            File.WriteAllBytes(Path.Combine(dumpDir, "C.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(C).ToArray());
            File.WriteAllBytes(Path.Combine(dumpDir, "B.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(B).ToArray());
            File.WriteAllBytes(Path.Combine(dumpDir, "A.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(A).ToArray());
        }
        // ---- output quads ----
        int rw = roi.Width;
        for (int y = roi.Y0; y < roi.Y1; y++)
            for (int x = roi.X0; x < roi.X1; x++)
            {
                float a = Ag(x, y), d = Dg(x, y);
                bool isG = ((x + y) & 1) == gSum;
                float r, g, b;
                if (isG)
                {
                    // G site: horizontal neighbors are R when the row parity is ry
                    float wl = Rcp(Abs(a - Ag(x - 1, y)) + eps1), wr = Rcp(Abs(a - Ag(x + 1, y)) + eps1);
                    float wu = Rcp(Abs(a - Ag(x, y - 1)) + eps1), wd = Rcp(Abs(a - Ag(x, y + 1)) + eps1);
                    float nh = Rcp(wr + wl), nv = Rcp(wd + wu);
                    float hv = nh * (Dg(x + 1, y) * wr + Dg(x - 1, y) * wl) + a;
                    float vv = nv * (Dg(x, y + 1) * wd + Dg(x, y - 1) * wu) + a;
                    g = a + d;
                    if ((y & 1) == ry) { r = hv; b = vv; } else { b = hv; r = vv; }
                }
                else
                {
                    bool isR = (x & 1) == rx && (y & 1) == ry;
                    // axial 4 taps: (x−1,y), (x+1,y), (x,y−1), (x,y+1)
                    var wa = Rcp4(Vector128.Create(Abs(Ag(x - 1, y) - a) + eps1, Abs(Ag(x + 1, y) - a) + eps1, Abs(Ag(x, y - 1) - a) + eps1, Abs(Ag(x, y + 1) - a) + eps1));
                    float t0 = Dg(x - 1, y) * wa[0], t1 = Dg(x + 1, y) * wa[1], t2 = Dg(x, y - 1) * wa[2], t3 = Dg(x, y + 1) * wa[3];
                    float na = Rcp((wa[3] + wa[1]) + (wa[2] + wa[0]));
                    float gv = ((t3 + t1) + (t2 + t0)) * na + a;
                    // diagonal 4 taps: (x−1,y−1), (x+1,y−1), (x−1,y+1), (x+1,y+1)
                    var wd4 = Rcp4(Vector128.Create(Abs(Ag(x - 1, y - 1) - a) + eps1, Abs(Ag(x + 1, y - 1) - a) + eps1, Abs(Ag(x - 1, y + 1) - a) + eps1, Abs(Ag(x + 1, y + 1) - a) + eps1));
                    float nd = Rcp((wd4[3] + wd4[1]) + (wd4[2] + wd4[0]));
                    float dv = ((Dg(x + 1, y + 1) * wd4[3] + Dg(x + 1, y - 1) * wd4[1]) + (Dg(x - 1, y + 1) * wd4[2] + Dg(x - 1, y - 1) * wd4[0])) * nd + a;
                    g = gv;
                    if (isR) { r = a + d; b = dv; } else { b = a + d; r = dv; }
                }
                dst[(y - roi.Y0) * rw + (x - roi.X0)] = new Vec4F { R = r, G = g, B = b, A = 1f };
            }
    }
}
