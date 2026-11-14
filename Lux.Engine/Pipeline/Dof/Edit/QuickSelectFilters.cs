using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>Image kernels of quick select: grey conversion, the sRGB → CIELAB kernel behind the colour samples, the domain-transform
/// filter of the mask, the mask clean-up.</summary>
public static class QuickSelectFilters
{
    static float F(uint b) => BitConverter.UInt32BitsToSingle(b);
    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    static float FromBits(int b) => BitConverter.Int32BitsToSingle(b);

    // the fast log2 / exp2 polynomials shared by Lumen's vectorised pow kernels
    static readonly float L0 = F(0x3e511af3), L1 = F(0xbfa05375), L2 = F(0x40552f75), L3 = F(0xc0121769),
        E0 = F(0x3d9fcb52), E1 = F(0x3e677e26), E2 = F(0x3f322226), E3 = F(0x3f7ffb19);

    /// <summary><c>((m·L0 + L1)·m + L2)·m</c> with m = the mantissa of y in [1, 2), and the exponent term
    /// <c>(float)((bits(y) + 0xc0800000) >> 23)</c> — the two halves of the log2 approximation, combined by the caller in its own order.</summary>
    static void Log2Parts(float y, out float pm, out float e)
    {
        int b = Bits(y); float m = FromBits((b & 0x7fffff) | 0x3f800000);
        pm = ((m * L0 + L1) * m + L2) * m;
        e = (float)(unchecked(b + (int)0xc0800000) >> 23);
    }

    /// <summary>exp2 of a clamped exponent: <c>l = min(max(l, −126), 128)</c>, <c>i = cvtt(l) + (bits(l) >> 31)</c>, <c>f = l − i</c>,
    /// <c>bits = (i &lt;&lt; 23) + bits(((f·E0 + E1)·f + E2)·f + E3)</c>.</summary>
    static float Exp2(float l)
    {
        l = l > -126.0f ? l : -126.0f;
        l = l < 128.0f ? l : 128.0f;
        int i = (int)l + (Bits(l) >> 31);
        float f = l - (float)i;
        float p = ((f * E0 + E1) * f + E2) * f + E3;
        return FromBits(unchecked((i << 23) + Bits(p)));
    }

    /// <summary>vec4 → float through Lumen's grey pixel converter:
    /// <c>(g·0.587 + r·0.299) + b·0.114</c>.</summary>
    public static float[] Grey(float[] rgba, int w, int h)
    {
        var o = new float[(long)w * h]; float kr = F(0x3e991687), kg = F(0x3f1645a2), kb = F(0x3de978d5);
        Parallel.For(0, Bands(h), b =>
        {
            var (ya, yb) = Band(b, 0, h);
            for (long i = (long)ya * w; i < (long)yb * w; i++) o[i] = (rgba[i * 4 + 1] * kg + rgba[i * 4] * kr) + rgba[i * 4 + 2] * kb;
        });
        return o;
    }

    static float[]? _labT;
    /// <summary>The 3×3 of the sRGB → space-8 kernel for (sRGB, D65) → (space 8, D65): <c>inv(diag(Xw, 1, Zw)) · A · M</c> with
    /// <c>Xw = x·(1/y)</c>, <c>Zw = ((1 − y) − x)·(1/y)</c>, A = identity (equal white points) and M = the sRGB primaries, i.e.
    /// <c>T[i][j] = d_i · M[i][j]</c> with d the diagonal of the 3×3 inverse of the white matrix.</summary>
    static float[] LabMatrix()
    {
        if (_labT != null) return _labT;
        var wp = WhitePoint.Of(7);
        var from = ColorSpace.Standard(2, wp, 1);
        float invY = 1.0f / wp.Y, xw = wp.X * invY, zw = ((1.0f - wp.Y) - wp.X) * invY;
        var inv = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(new float[] { xw, 0, 0, 0, 1, 0, 0, 0, zw });
        var t = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                t[i * 3 + j] = (inv[i * 3 + 2] * from.M[6 + j]) + ((inv[i * 3 + 1] * from.M[3 + j]) + (inv[i * 3] * from.M[j]));
        return _labT = t;
    }

    /// <summary>The cube root of the Lab kernel: a bit-level first guess <c>bits = t + (t >> 8) + 0x2a5137a0</c> with
    /// <c>t = u + (u >> 4)</c>, <c>u = (b >> 2) + (b >> 4)</c>, then two Newton steps on `rcpps`:
    /// <c>y1 = (y0 + y0) + rcp(y0²)·x</c>;  <c>u = y1·(1/3)</c>;  <c>y2 = (y1·(2/3) + ((1 − u²·r)·r + r)·x)·(1/3)</c>, r = `rcpps(u²)`.</summary>
    static float Cbrt(float x)
    {
        int b = Bits(x);
        int u = (b >> 2) + (b >> 4);
        int t = u + (u >> 4);
        float y0 = FromBits(unchecked(t + (t >> 8) + 0x2a5137a0));
        float y1 = (y0 + y0) + IntelApprox.Rcp(y0 * y0) * x;
        float uu = y1 * Third; uu = uu * uu;
        float r = IntelApprox.Rcp(uu), nr = (1.0f - uu * r) * r + r;
        return (y1 * TwoThirds + nr * x) * Third;
    }
    static readonly float Third = F(0x3eaaaaab), TwoThirds = F(0x3f2aaaab);

    /// <summary>
    /// `ImageConvertColorSpace(dst, src, sRGB/D65, space 8/D65, Bradford)` (the sRGB → space-8 kernel),
    /// matrix branch. Per pixel, all four lanes:
    /// <c>v = max(0, s)</c>; sRGB decode <c>v &lt; 0.04045 ? v·0.0773993805 : exp2(((pm + (e + L3))·2.4)</c> of <c>(v + 0.055)·0.947867334</c>;
    /// <c>X = ((a·K) + (b·T2)) + ((g·T1) + (r·T0))</c> with K = (0, 0, 0, 1) and a the source alpha;
    /// <c>x = max(0, X)</c>; <c>f = x &lt; 0.008856452 ? x·7.787037 + 0.13793103 : cbrt(x)</c>;
    /// <c>L = (f1·116 + −16) + f0·0</c>, <c>a = f1·−500 + f0·500</c>, <c>b = f1·200 + f2·−200</c>, alpha = the matrix lane 3.
    /// </summary>
    public static float[] Lab(float[] rgba, int w, int h)
    {
        var T = LabMatrix(); var o = new float[(long)w * h * 4];
        // row bands over threads, eight pixels per step (LabRow8: each lane the operations below), the rest one by one
        Parallel.For(0, Bands(h), b =>
        {
            var (ya, yb) = Band(b, 0, h);
            for (int y = ya; y < yb; y++)
            {
                long i0 = (long)y * w; int x = Lux.Engine.Pipeline.Isp.Soa8.Supported ? LabRow8(rgba, o, i0, w, T) : 0;
                LabPixels(rgba, o, i0 + x, i0 + w, T);
            }
        });
        return o;
    }

    static readonly float c055 = F(0x3d6147ae), cInv = F(0x3f72a76f), c24 = F(0x4019999a), cLin = F(0x3d9e8391), cThr = F(0x3d25aee6),
        k7 = F(0x40f92f69), k16 = F(0x3e0d3dcb), kThr = F(0x3c111aa7);

    static int LabRow8(float[] rgba, float[] o, long i0, int w, float[] T)
    {
        int x = 0;
        ref float sb = ref MemoryMarshal.GetArrayDataReference(rgba); ref float ob = ref MemoryMarshal.GetArrayDataReference(o);
        var zero = Vector256<float>.Zero; var one = Vector256.Create(1.0f);
        Vector256<float> Lin(Vector256<float> s)
        {
            var v = Vector256.ConditionalSelect(Vector256.GreaterThan(zero, s), zero, s);
            var y = (v + Vector256.Create(c055)) * Vector256.Create(cInv);
            var b = y.AsInt32();
            var m = ((b & Vector256.Create(0x7fffff)) | Vector256.Create(0x3f800000)).AsSingle();
            var pm = ((m * Vector256.Create(L0) + Vector256.Create(L1)) * m + Vector256.Create(L2)) * m;
            var ex = Vector256.ConvertToSingle(Vector256.ShiftRightArithmetic(b + Vector256.Create(unchecked((int)0xc0800000)), 23));
            var pw = Exp2V((pm + (ex + Vector256.Create(L3))) * Vector256.Create(c24));
            return Vector256.ConditionalSelect(Vector256.LessThan(v, Vector256.Create(cThr)), v * Vector256.Create(cLin), pw);
        }
        Vector256<float> Fc(Vector256<float> X)
        {
            var xx = Vector256.ConditionalSelect(Vector256.GreaterThan(zero, X), zero, X);
            return Vector256.ConditionalSelect(Vector256.LessThan(xx, Vector256.Create(kThr)), xx * Vector256.Create(k7) + Vector256.Create(k16), Cbrt8(xx));
        }
        for (; x + 8 <= w; x += 8)
        {
            ref float p = ref System.Runtime.CompilerServices.Unsafe.Add(ref sb, (nint)((i0 + x) * 4));
            Lux.Engine.Pipeline.Isp.Soa8.Load(ref p, out var R, out var G, out var B, out var A);
            var l0 = Lin(R); var l1 = Lin(G); var l2 = Lin(B);
            var f0 = Fc(((A * zero) + (l2 * Vector256.Create(T[2]))) + ((l1 * Vector256.Create(T[1])) + (l0 * Vector256.Create(T[0]))));
            var f1 = Fc(((A * zero) + (l2 * Vector256.Create(T[5]))) + ((l1 * Vector256.Create(T[4])) + (l0 * Vector256.Create(T[3]))));
            var f2 = Fc(((A * zero) + (l2 * Vector256.Create(T[8]))) + ((l1 * Vector256.Create(T[7])) + (l0 * Vector256.Create(T[6]))));
            var alpha = ((A * one) + (l2 * zero)) + ((l1 * zero) + (l0 * zero));
            var oL = (f1 * Vector256.Create(116.0f) + Vector256.Create(-16.0f)) + f0 * zero;
            var oa = (f1 * Vector256.Create(-500.0f) + zero) + f0 * Vector256.Create(500.0f);
            var ob2 = (f1 * Vector256.Create(200.0f) + zero) + f2 * Vector256.Create(-200.0f);
            Lux.Engine.Pipeline.Isp.Soa8.Store(ref System.Runtime.CompilerServices.Unsafe.Add(ref ob, (nint)((i0 + x) * 4)), oL, oa, ob2, alpha);
        }
        return x;
    }

    /// <summary><see cref="Cbrt"/> on eight lanes.</summary>
    static Vector256<float> Cbrt8(Vector256<float> x)
    {
        var b = x.AsInt32();
        var u = Vector256.ShiftRightArithmetic(b, 2) + Vector256.ShiftRightArithmetic(b, 4);
        var t = u + Vector256.ShiftRightArithmetic(u, 4);
        var y0 = (t + Vector256.ShiftRightArithmetic(t, 8) + Vector256.Create(0x2a5137a0)).AsSingle();
        var y1 = (y0 + y0) + IntelApprox.Reciprocal(y0 * y0) * x;
        var uu = y1 * Vector256.Create(Third); uu = uu * uu;
        var r = IntelApprox.Reciprocal(uu); var nr = (Vector256.Create(1.0f) - uu * r) * r + r;
        return (y1 * Vector256.Create(TwoThirds) + nr * x) * Vector256.Create(Third);
    }

    static void LabPixels(float[] rgba, float[] o, long from, long to, float[] T)
    {
        Span<float> lin = stackalloc float[3]; Span<float> f = stackalloc float[3];
        for (long i = from; i < to; i++)
        {
            for (int e = 0; e < 3; e++)
            {
                float s = rgba[i * 4 + e];
                float v = 0.0f > s ? 0.0f : s;                         // MAXPS order: a NaN s passes through
                float y = (v + c055) * cInv;
                Log2Parts(y, out float pm, out float ex);
                float pw = Exp2((pm + (ex + L3)) * c24);
                lin[e] = v < cThr ? v * cLin : pw;
            }
            float a = rgba[i * 4 + 3];
            for (int e = 0; e < 3; e++)
            {
                float X = ((a * 0.0f) + (lin[2] * T[e * 3 + 2])) + ((lin[1] * T[e * 3 + 1]) + (lin[0] * T[e * 3]));
                float x = 0.0f > X ? 0.0f : X;
                f[e] = x < kThr ? x * k7 + k16 : Cbrt(x);
            }
            float alpha = ((a * 1.0f) + (lin[2] * 0.0f)) + ((lin[1] * 0.0f) + (lin[0] * 0.0f));
            o[i * 4] = (f[1] * 116.0f + -16.0f) + f[0] * 0.0f;
            o[i * 4 + 1] = (f[1] * -500.0f + 0.0f) + f[0] * 500.0f;
            o[i * 4 + 2] = (f[1] * 200.0f + 0.0f) + f[2] * -200.0f;
            o[i * 4 + 3] = alpha;
        }
    }

    /// <summary>
    /// `lt::ImageFilterDT&lt;unsigned char, float&gt;(dst, src, guide, σs, σr, iterations = 1)`: the mask as floats
    /// (<c>(float)byte</c>), one domain-transform pass (`Internal::DomainTransform&lt;float&gt;`), back to bytes
    /// (<c>cvtt(min(max(v + copysign(0.5, v), 0), 255))</c>).
    ///
    /// Feedback <c>a = expf(−√2 / (2^(n−1)·(σs·√3)·rsqrt(4^n − 1)))</c> (n = iterations; `rsqrtss` + one Newton step; CRT `expf`).
    /// Weights (per 256×256 tile): <c>wA[y][x] = a^(1 + |g[y][x] − g[y][x−1]|·σs/σr)</c>,
    /// <c>wB[y][x] = a^(1 + |g[y][x] − g[y−1][x]|·σs/σr)</c>, computed with the vectorised pow approximation
    /// <c>exp2(d·((pm + L3) + e))</c> — except, with the CRT `powf`, the last pixels of image row 0 of each tile that the 4-wide loop
    /// does not reach and wB in column 0. wA[·][0] = 0, wB[0][·] = 0.
    /// Passes (the tile wavefront reduces to whole-image passes): down the columns x ≥ 1, then along the rows
    /// left to right; up the columns x ≤ w − 2, then along the rows right to left; each <c>I = (prev − I)·w + I</c>.
    /// </summary>
    public static byte[] DomainTransform(byte[] mask, float[] guide, int w, int h, float sigmaS, float sigmaR, int iterations,
        Action<float[], float[], float[]>? trace = null)
    {
        var I = new float[(long)w * h];
        for (long i = 0; i < I.LongLength; i++) I[i] = (float)mask[i];
        float ratio = sigmaS / sigmaR;
        float t = (float)(1 << (2 * iterations)) + -1.0f;
        float rs = IntelApprox.Rsqrt(t);
        float nr = (rs * -0.5f) * ((t * rs) * rs + -3.0f);
        float den = ((float)(1 << (iterations - 1)) * (sigmaS * F(0x3fddb3d7))) * nr;
        float a = MuslMath.Expf(F(0xbfb504f3) / den);
        Log2Parts(a, out float pm, out float ex);
        float L = (pm + L3) + ex;
        float Fast(float d) => Exp2((MathF.Abs(d) * ratio + 1.0f) * L);
        float Crt(float d) => MuslMath.Powf(a, MathF.Abs(d) * ratio + 1.0f);
        var wA = new float[(long)w * h]; var wB = new float[(long)w * h];
        // row 0 as the 256-px tiles of the weight step make it (the 4-wide loop, then the CRT powf for the tile's last w & 3 pixels)
        for (int x0 = 0; x0 < w; x0 += 256)
        {
            int X1 = Math.Min(x0 + 256, w), x = x0;
            if (x0 == 0) { wA[0] = 0f; x = 1; }
            for (; x <= X1 - 4; x += 4)
                for (int k = 0; k < 4; k++) wA[x + k] = Fast(guide[x + k] - guide[x + k - 1]);
            for (; x < X1; x++) wA[x] = Crt(guide[x] - guide[x - 1]);
        }
        // rows ≥ 1: every pixel x ≥ 1 through the vectorised pow, column 0 wA = 0 and wB by the CRT powf — eight lanes and row bands
        var L8 = Vector256.Create(L); var R8 = Vector256.Create(ratio); var one8 = Vector256.Create(1.0f);
        Parallel.For(0, Bands(h - 1), b =>
        {
            var (ya, yb) = Band(b, 1, h);
            ref float gb = ref MemoryMarshal.GetArrayDataReference(guide); ref float ab = ref MemoryMarshal.GetArrayDataReference(wA); ref float bb = ref MemoryMarshal.GetArrayDataReference(wB);
            for (int y = ya; y < yb; y++)
            {
                long r = (long)y * w, ru = r - w;
                wA[r] = 0f; wB[r] = Crt(guide[r] - guide[ru]);
                int x = 1;
                if (Vector256.IsHardwareAccelerated)
                    for (; x + 8 <= w; x += 8)
                    {
                        var g = Vector256.LoadUnsafe(ref gb, (nuint)(r + x));
                        Exp2V((Vector256.Abs(g - Vector256.LoadUnsafe(ref gb, (nuint)(r + x - 1))) * R8 + one8) * L8).StoreUnsafe(ref ab, (nuint)(r + x));
                        Exp2V((Vector256.Abs(g - Vector256.LoadUnsafe(ref gb, (nuint)(ru + x))) * R8 + one8) * L8).StoreUnsafe(ref bb, (nuint)(r + x));
                    }
                for (; x < w; x++) { wA[r + x] = Fast(guide[r + x] - guide[r + x - 1]); wB[r + x] = Fast(guide[r + x] - guide[ru + x]); }
            }
        });
        // forward: down the columns (each column on its own; eight columns at a time, column blocks over threads), then along the rows
        // (each row on its own, rows over threads); backward likewise
        int colBlocks = (w + 255) / 256;
        Parallel.For(0, colBlocks, cb =>
        {
            int xa = Math.Max(1, cb * 256), xb = Math.Min(w, cb * 256 + 256);
            ref float ib = ref MemoryMarshal.GetArrayDataReference(I); ref float bb = ref MemoryMarshal.GetArrayDataReference(wB);
            for (int y = 1; y < h; y++)
            {
                long r = (long)y * w; int x = xa;
                if (Vector256.IsHardwareAccelerated)
                    for (; x + 8 <= xb; x += 8)
                    {
                        var c = Vector256.LoadUnsafe(ref ib, (nuint)(r + x));
                        ((Vector256.LoadUnsafe(ref ib, (nuint)(r - w + x)) - c) * Vector256.LoadUnsafe(ref bb, (nuint)(r + x)) + c).StoreUnsafe(ref ib, (nuint)(r + x));
                    }
                for (; x < xb; x++) I[r + x] = (I[r - w + x] - I[r + x]) * wB[r + x] + I[r + x];
            }
        });
        Parallel.For(0, Bands(h), b =>
        {
            var (ya, yb) = Band(b, 0, h);
            for (int y = ya; y < yb; y++) { long r = (long)y * w; float p = I[r]; for (int x = 1; x < w; x++) { p = (p - I[r + x]) * wA[r + x] + I[r + x]; I[r + x] = p; } }
        });
        Parallel.For(0, colBlocks, cb =>
        {
            int xa = cb * 256, xb = Math.Min(w - 1, cb * 256 + 256);
            ref float ib = ref MemoryMarshal.GetArrayDataReference(I); ref float bb = ref MemoryMarshal.GetArrayDataReference(wB);
            for (int y = h - 2; y >= 0; y--)
            {
                long r = (long)y * w; int x = xa;
                if (Vector256.IsHardwareAccelerated)
                    for (; x + 8 <= xb; x += 8)
                    {
                        var c = Vector256.LoadUnsafe(ref ib, (nuint)(r + x));
                        ((Vector256.LoadUnsafe(ref ib, (nuint)(r + w + x)) - c) * Vector256.LoadUnsafe(ref bb, (nuint)(r + w + x)) + c).StoreUnsafe(ref ib, (nuint)(r + x));
                    }
                for (; x < xb; x++) I[r + x] = (I[r + w + x] - I[r + x]) * wB[r + w + x] + I[r + x];
            }
        });
        Parallel.For(0, Bands(h), b =>
        {
            var (ya, yb) = Band(b, 0, h);
            for (int y = ya; y < yb; y++) { long r = (long)y * w; for (int x = w - 2; x >= 0; x--) I[r + x] = (I[r + x + 1] - I[r + x]) * wA[r + x + 1] + I[r + x]; }
        });
        trace?.Invoke(I, wA, wB);
        var o = new byte[(long)w * h];
        Parallel.For(0, Bands(h), b =>
        {
            var (ya, yb) = Band(b, 0, h);
            for (long i = (long)ya * w; i < (long)yb * w; i++)
            {
                float v = I[i];
                v = FromBits((Bits(v) & unchecked((int)0x80000000)) | 0x3f000000) + v;
                v = v > 0.0f ? v : 0.0f;
                v = v < 255.0f ? v : 255.0f;
                o[i] = (byte)(int)v;
            }
        });
        return o;
    }

    /// <summary>body(y0, y1) over row bands of [0, h) on the thread pool (independent rows only).</summary>
    internal static void ForRows(int h, Action<int, int> body) => Parallel.For(0, Bands(h), b => { var (a, e) = Band(b, 0, h); if (a < e) body(a, e); });

    /// <summary>Row bands for the independent-row passes.</summary>
    internal static int Bands(int n) => Math.Max(1, Math.Min(Environment.ProcessorCount * 2, n / 16));
    internal static (int Y0, int Y1) Band(int b, int start, int end)
    {
        int n = end - start, k = Bands(n), per = (n + k - 1) / k;
        return (Math.Min(end, start + b * per), Math.Min(end, start + (b + 1) * per));
    }

    /// <summary><see cref="Exp2"/> on eight lanes (the same clamp, integer and float steps).</summary>
    static Vector256<float> Exp2V(Vector256<float> l)
    {
        var lo = Vector256.Create(-126.0f); var hi = Vector256.Create(128.0f);
        l = Vector256.ConditionalSelect(Vector256.GreaterThan(l, lo), l, lo);
        l = Vector256.ConditionalSelect(Vector256.LessThan(l, hi), l, hi);
        var i = Vector256.ConvertToInt32(l) + Vector256.ShiftRightArithmetic(l.AsInt32(), 31);
        var f = l - Vector256.ConvertToSingle(i);
        var p = ((f * Vector256.Create(E0) + Vector256.Create(E1)) * f + Vector256.Create(E2)) * f + Vector256.Create(E3);
        return (Vector256.ShiftLeft(i, 23) + p.AsInt32()).AsSingle();
    }

    /// <summary>The clean-up: one raster pass over the interior (1 ≤ x &lt; w − 2, 1 ≤ y &lt; h − 2) that removes isolated pixels and
    /// one- / two-pixel spurs and fills the matching holes (writes 0 / 1; the pass sees its own earlier writes).</summary>
    public static void Cleanup(byte[] m, int w, int h)
    {
        if (h <= 3) return;
        for (int y = 1; y < h - 2; y++)
        {
            if (w <= 3) continue;
            for (int x = 1; x < w - 2; x++)
            {
                long c = (long)y * w + x;
                byte U = m[c - w], Lf = m[c - 1];
                if (U == 0)
                {
                    if (Lf == 0 && m[c + 1] == 0 && m[c + w] == 0) m[c] = 0;
                    else if (m[c - w + 1] == 0)
                    {
                        if (Lf == 0)
                        {
                            if (m[c + 2] != 0 || m[c + w] != 0 || m[c + w + 1] != 0) goto spurOff;
                            m[c + 1] = 0; m[c] = 0; x++;
                        }
                    }
                    else if (Lf == 0) goto spurOff;
                    continue;
                spurOff:
                    if (m[c + 1] == 0 && m[c + w - 1] == 0 && m[c + w + 1] == 0 && m[c + 2 * w] == 0) { m[c + w] = 0; m[c] = 0; }
                }
                else if (Lf == 0 || m[c + 1] == 0 || m[c + w] == 0)
                {
                    if (m[c - w + 1] == 0)
                    {
                        if (Lf != 0) goto spurOn;
                    }
                    else if (Lf != 0)
                    {
                        if (m[c + 2] == 0 || m[c + w] == 0 || m[c + w + 1] == 0) goto spurOn;
                        m[c + 1] = 1; m[c] = 1; x++;
                    }
                    continue;
                spurOn:
                    if (m[c + 1] != 0 && m[c + w - 1] != 0 && m[c + w + 1] != 0 && m[c + 2 * w] != 0) { m[c + w] = 1; m[c] = 1; }
                }
                else m[c] = 1;
            }
        }
    }
}
