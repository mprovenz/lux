using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The float pyramid primitives of Lumen's `lt::A::CreateAndBlendLaplacianPyramids`: `lt::ImageGaussianFilterAndSubSample&lt;float&gt;`
/// (tile lambda, vertical helper), `lt::ImageGaussianUpscaleAndSubtract&lt;float&gt;` (tile lambda, even-row helper), and three
/// vector helpers. All borders replicate; every level is `(n+1) &gt;&gt; 1`.
/// The float associations differ between the SIMD bodies, the scalar tails and the clamped edges — each one follows Lumen's
/// code and is load-bearing.
/// <para>The loops run lane-parallel over independent outputs (<see cref="Vector128{T}"/> / <see cref="Vector256{T}"/>) with
/// each segment's own association, and level images can come from the <see cref="ScratchPool"/> (an <see cref="Alloc"/> list collects them).</para>
/// </summary>
public static class LaplacianPyramid
{
    /// <summary>Where new level images come from: null = fresh arrays; otherwise pooled arrays, recorded here for the caller to give back.</summary>
    public sealed class Alloc
    {
        public readonly List<float[]> Rented = new();
        public Image<float> Image(int w, int h)
        {
            var a = ScratchPool.Rent<float>(Math.Max(1, w * h)); lock (Rented) Rented.Add(a);
            return new Image<float>(new RectI(0, 0, w, h), a, w, 0);
        }
        public void ReturnAll() { lock (Rented) { foreach (var a in Rented) ScratchPool.Return(a); Rented.Clear(); } }
    }
    static Image<float> New(int w, int h, Alloc? al) => al is null ? new Image<float>(w, h) : al.Image(w, h);

    // analysis kernel
    static readonly float K0 = BitConverter.Int32BitsToSingle(0x3d4ccccd);   // 0.05
    static readonly float K1 = BitConverter.Int32BitsToSingle(0x3e800000);   // 0.25
    static readonly float K2 = BitConverter.Int32BitsToSingle(0x3ecccccd);   // 0.40
    static readonly float K3 = BitConverter.Int32BitsToSingle(0x3e800000);   // 0.25
    static readonly float K4 = BitConverter.Int32BitsToSingle(0x3d4ccccd);   // 0.05
    // synthesis constants (the 4-wide copies have the same bits)
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
    static Image<float> NewLevel(Image<float> src, Alloc? al) => New((src.Width + 1) >> 1, (src.Height + 1) >> 1, al);

    /// <summary>`lt::ImageGaussianFilterAndSubSample&lt;float&gt;`: separable, **vertical first**, even-phase
    /// decimation. `Tiler::Run(0, src.h, 16)` bands of source rows; inside a band the x is walked in blocks of 32,
    /// the vertical helper fills a per-row scratch line (which persists across blocks) and the horizontal pass emits
    /// `dst[y&gt;&gt;1][x&gt;&gt;1]` in four segments: clamped head, 8-wide body, unclamped step-2 remainder, clamped tail.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void PyrDown(Image<float> dst, Image<float> src) => PyrDown(dst, src.Width, src.Height, y => src.Row(y));

    /// <summary>A source row by index (lets <see cref="PyrDown(Image{float}, int, int, RowSource)"/> read rows computed on the fly).</summary>
    public delegate ReadOnlySpan<float> RowSource(int y);

    /// <summary><see cref="PyrDown(Image{float}, Image{float})"/> of a W × H source whose rows come from <paramref name="src"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void PyrDown(Image<float> dst, int W, int H, RowSource src)
    {
        int rx0 = 0, rx1 = W, ry0 = 0, ry1 = H;              // src.rect relative to the buffer (no halo on the display path)
        int loX = Math.Max(rx0, -2);
        int hiXExcl = Math.Min(rx1, W + 2);
        int clampHiX = hiXExcl - 1;
        // one scratch line per even row of the band: a line PERSISTS across the
        // x-blocks of its row, which is what makes block `xb`'s reads of columns `xb−4 … xb−1` well defined.
        int slw = W + 8;
        var scratch = ScratchPool.Rent<float>(16 * slw);      // 2 floats of left padding; every read column is written first
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
                    {
                        if (Simd.IsSupported)
                        {
                            // the four outputs of a step at once: lane k = Assoc2(s[x+2k−2], s[x+2k−1], s[x+2k], s[x+2k+1], s[x+2k+2])
                            ref float sb = ref MemoryMarshal.GetArrayDataReference(scratch);
                            ref float db = ref MemoryMarshal.GetReference(drow);
                            var k0 = Vector128.Create(K0); var k1 = Vector128.Create(K1); var k2 = Vector128.Create(K2); var k3 = Vector128.Create(K3); var k4 = Vector128.Create(K4);
                            for (; x < simdEnd; x += 8)
                            {
                                var l0 = Vector128.LoadUnsafe(ref sb, (nuint)(b + x - 2)); var l1 = Vector128.LoadUnsafe(ref sb, (nuint)(b + x + 2)); var l2 = Vector128.LoadUnsafe(ref sb, (nuint)(b + x + 6));
                                var m0 = Vector128.LoadUnsafe(ref sb, (nuint)(b + x)); var m1 = Vector128.LoadUnsafe(ref sb, (nuint)(b + x + 4));
                                var e0 = Simd.Shuffle(l0, l1, 0b10_00_10_00); var o0 = Simd.Shuffle(l0, l1, 0b11_01_11_01);
                                var e1 = Simd.Shuffle(m0, m1, 0b10_00_10_00); var o1 = Simd.Shuffle(m0, m1, 0b11_01_11_01);
                                var e2 = Simd.Shuffle(l1, l2, 0b10_00_10_00);
                                (((k4 * e2) + (k1 * o0 + k0 * e0)) + (k3 * o1 + k2 * e1)).StoreUnsafe(ref db, (nuint)(x >> 1));
                            }
                        }
                        else
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
                    }
                    if (x < rem0)
                        for (; x < rem0; x += 2)
                            drow[x >> 1] = Assoc5(scratch[b + x - 2], scratch[b + x - 1], scratch[b + x], scratch[b + x + 1], scratch[b + x + 2]);
                    while (x < tailEnd) { drow[x >> 1] = HClamped(scratch, b, x, loX, clampHiX); x += 2; }
                }
            }
        }
        ScratchPool.Return(scratch);
    }

    static float HClamped(float[] s, int pad, int x, int lo, int hi)
    {
        int i0 = Math.Min(Math.Max(x - 2, lo), hi), i1 = Math.Min(Math.Max(x - 1, lo), hi), i2 = Math.Min(Math.Max(x, lo), hi);
        int i3 = Math.Min(Math.Max(x + 1, lo), hi), i4 = Math.Min(Math.Max(x + 2, lo), hi);
        return Assoc1(s[pad + i0], s[pad + i1], s[pad + i2], s[pad + i3], s[pad + i4]);
    }

    /// <summary>The vertical helper (scratchRow, src, x0, x1, y, K): rows `y−2 … y+2`. A row whose taps all lie inside
    /// `[rect.y0, rect.y1)` uses the 4-wide association for the first `(x1−x0) &amp; ~3` columns and the tail
    /// association for the rest; a row needing a clamp is fully scalar with the 4-wide association.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void VerticalRow(float[] scratch, int pad, RowSource src, int x0, int x1, int y, int ry0, int ry1)
    {
        if (x1 <= x0) return;
        if (ry0 + 2 > y || ry1 - 2 <= y)
        {
            int hi = ry1 - 1;
            int r0 = Math.Min(Math.Max(y - 2, ry0), hi), r1 = Math.Min(Math.Max(y - 1, ry0), hi), r2 = Math.Min(Math.Max(y, ry0), hi);
            int r3 = Math.Min(Math.Max(y + 1, ry0), hi), r4 = Math.Min(Math.Max(y + 2, ry0), hi);
            var s0 = src(r0); var s1 = src(r1); var s2 = src(r2); var s3 = src(r3); var s4 = src(r4);
            for (int x = x0; x < x1; x++) scratch[pad + x] = Assoc1(s0[x], s1[x], s2[x], s3[x], s4[x]);
            return;
        }
        var a0 = src(y - 2); var a1 = src(y - 1); var a2 = src(y); var a3 = src(y + 1); var a4 = src(y + 2);
        int n4 = (x1 - x0) & ~3, split = x0 + n4;
        int xx = x0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref float r0 = ref MemoryMarshal.GetReference(a0); ref float r1 = ref MemoryMarshal.GetReference(a1); ref float r2 = ref MemoryMarshal.GetReference(a2);
            ref float r3 = ref MemoryMarshal.GetReference(a3); ref float r4 = ref MemoryMarshal.GetReference(a4); ref float sb = ref MemoryMarshal.GetArrayDataReference(scratch);
            var k0 = Vector256.Create(K0); var k1 = Vector256.Create(K1); var k2 = Vector256.Create(K2); var k3 = Vector256.Create(K3); var k4 = Vector256.Create(K4);
            for (; xx + 8 <= split; xx += 8)
            {
                var v0 = Vector256.LoadUnsafe(ref r0, (nuint)xx); var v1 = Vector256.LoadUnsafe(ref r1, (nuint)xx); var v2 = Vector256.LoadUnsafe(ref r2, (nuint)xx);
                var v3 = Vector256.LoadUnsafe(ref r3, (nuint)xx); var v4 = Vector256.LoadUnsafe(ref r4, (nuint)xx);
                ((k4 * v4) + ((k3 * v3 + k2 * v2) + (k1 * v1 + k0 * v0))).StoreUnsafe(ref sb, (nuint)(pad + xx));
            }
        }
        for (int x = xx; x < split; x++) scratch[pad + x] = Assoc1(a0[x], a1[x], a2[x], a3[x], a4[x]);
        for (int x = split; x < x1; x++) scratch[pad + x] = Assoc2(a0[x], a1[x], a2[x], a3[x], a4[x]);
    }

    /// <summary>`lt::ImageGaussianUpscaleAndSubtract&lt;float&gt;`: `dst = up(low) − sub`, `dst` sized to `sub`.
    /// 2-D `Tiler::Run(rect, {256,256})`; rows are walked in (even, odd) pairs, a trailing even row alone.</summary>
    public static void PyrUpSub(Image<float> dst, Image<float> low, Image<float> sub)
    {
        foreach (var t in Tiler.Rects(new RectI(0, 0, dst.Width, dst.Height), 256, 256))
        {
            int yEven = t.Y1 & ~1;
            int y = t.Y0;
            for (; y < yEven; y += 2) { EvenRow(dst.Row(y), low, sub.Row(y), y, t.X0, t.X1); OddRow(dst.Row(y + 1), low, sub.Row(y + 1), y + 1, t.X0, t.X1); }
            if (yEven != t.Y1) EvenRow(dst.Row(yEven), low, sub.Row(yEven), yEven, t.X0, t.X1);
        }
    }

    /// <summary>A row segment source: the W-wide row <paramref name="y"/> with (at least) columns [x0, x1) valid.</summary>
    public delegate ReadOnlySpan<float> SegmentSource(int y, int x0, int x1);
    /// <summary>Receives row <paramref name="y"/>'s columns [x0, x1) of the result (in <paramref name="row"/>, indexed by column).</summary>
    public delegate void SegmentSink(int y, int x0, int x1, ReadOnlySpan<float> row);

    /// <summary><see cref="PyrUpSub"/> without a destination image: the same rows in the same order, each finished row segment handed to
    /// <paramref name="sink"/>, the subtrahend read through <paramref name="sub"/>.</summary>
    public static void PyrUpSub(int W, int H, Image<float> low, SegmentSource sub, SegmentSink sink)
    {
        var buf = new float[W];
        foreach (var t in Tiler.Rects(new RectI(0, 0, W, H), 256, 256))
        {
            int yEven = t.Y1 & ~1;
            int y = t.Y0;
            for (; y < yEven; y += 2)
            {
                EvenRow(buf, low, sub(y, t.X0, t.X1), y, t.X0, t.X1); sink(y, t.X0, t.X1, buf);
                OddRow(buf, low, sub(y + 1, t.X0, t.X1), y + 1, t.X0, t.X1); sink(y + 1, t.X0, t.X1, buf);
            }
            if (yEven != t.Y1) { EvenRow(buf, low, sub(yEven, t.X0, t.X1), yEven, t.X0, t.X1); sink(yEven, t.X0, t.X1, buf); }
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void EvenRow(Span<float> d, Image<float> low, ReadOnlySpan<float> s, int Y, int x0, int x1)
    {
        int r = Y >> 1, lh = low.Height, lw = low.Width;
        var A = low.Row(Math.Min(Math.Max(r - 1, 0), lh - 1));
        var C = low.Row(r);
        var B = low.Row(Math.Min(r + 1, lh - 1));
        var (ss, se) = SimdRange(x0, x1, 0, lw);
        for (int X = x0; X < x1; X++)
        {
            if (X == ss && se > ss)
            {
                if (Simd.IsSupported)
                {
                    ref float ra = ref MemoryMarshal.GetReference(A); ref float rb = ref MemoryMarshal.GetReference(B); ref float rc = ref MemoryMarshal.GetReference(C);
                    ref float rs = ref MemoryMarshal.GetReference(s); ref float rd = ref MemoryMarshal.GetReference(d);
                    var u64 = Vector128.Create(U64); var u01 = Vector128.Create(U01); var u08 = Vector128.Create(U08); var u40 = Vector128.Create(U40); var u05 = Vector128.Create(U05);
                    for (; X < se; X += 8)
                    {
                        // lanes j = 0…3 (k = X/2 + j): the even output X+2j and the odd output X+2j+1, interleaved on store
                        nuint c = (nuint)(X >> 1);
                        var Am = Vector128.LoadUnsafe(ref ra, c - 1); var A0 = Vector128.LoadUnsafe(ref ra, c); var Ap = Vector128.LoadUnsafe(ref ra, c + 1);
                        var Bm = Vector128.LoadUnsafe(ref rb, c - 1); var B0 = Vector128.LoadUnsafe(ref rb, c); var Bp = Vector128.LoadUnsafe(ref rb, c + 1);
                        var Cm = Vector128.LoadUnsafe(ref rc, c - 1); var C0 = Vector128.LoadUnsafe(ref rc, c); var Cp = Vector128.LoadUnsafe(ref rc, c + 1);
                        var Sm = Bm + Am; var Sk = B0 + A0; var Sp = Bp + Ap;
                        var s0 = Vector128.LoadUnsafe(ref rs, (nuint)X); var s1 = Vector128.LoadUnsafe(ref rs, (nuint)(X + 4));
                        var ev = (((C0 * u64) + ((Sm + Sp) * u01)) + (((Cm + Cp) + Sk) * u08)) - Simd.Shuffle(s0, s1, 0b10_00_10_00);
                        var od = (((Cp + C0) * u40) + ((Sk + Sp) * u05)) - Simd.Shuffle(s0, s1, 0b11_01_11_01);
                        Simd.UnpackLow(ev, od).StoreUnsafe(ref rd, (nuint)X); Simd.UnpackHigh(ev, od).StoreUnsafe(ref rd, (nuint)(X + 4));
                    }
                }
                else
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void OddRow(Span<float> d, Image<float> low, ReadOnlySpan<float> s, int Y, int x0, int x1)
    {
        int r = Y >> 1, lh = low.Height, lw = low.Width;
        var P = low.Row(r);
        var Q = low.Row(Math.Min(r + 1, lh - 1));
        var (ss, se) = SimdRange(x0, x1, 0, lw);
        for (int X = x0; X < x1; X++)
        {
            if (X == ss && se > ss)
            {
                if (Simd.IsSupported)
                {
                    ref float rp = ref MemoryMarshal.GetReference(P); ref float rq = ref MemoryMarshal.GetReference(Q);
                    ref float rs = ref MemoryMarshal.GetReference(s); ref float rd = ref MemoryMarshal.GetReference(d);
                    var u40 = Vector128.Create(U40); var u05 = Vector128.Create(U05); var u25 = Vector128.Create(U25);
                    for (; X < se; X += 8)
                    {
                        nuint c = (nuint)(X >> 1);
                        var Sm = Vector128.LoadUnsafe(ref rq, c - 1) + Vector128.LoadUnsafe(ref rp, c - 1);
                        var Sk = Vector128.LoadUnsafe(ref rq, c) + Vector128.LoadUnsafe(ref rp, c);
                        var Sp = Vector128.LoadUnsafe(ref rq, c + 1) + Vector128.LoadUnsafe(ref rp, c + 1);
                        var s0 = Vector128.LoadUnsafe(ref rs, (nuint)X); var s1 = Vector128.LoadUnsafe(ref rs, (nuint)(X + 4));
                        var ev = ((Sk * u40) + ((Sm + Sp) * u05)) - Simd.Shuffle(s0, s1, 0b10_00_10_00);
                        var od = ((Sp + Sk) * u25) - Simd.Shuffle(s0, s1, 0b11_01_11_01);
                        Simd.UnpackLow(ev, od).StoreUnsafe(ref rd, (nuint)X); Simd.UnpackHigh(ev, od).StoreUnsafe(ref rd, (nuint)(X + 4));
                    }
                }
                else
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

    /// <summary>`gauss[i] = PyrDown(cur)`, `lap[i] = PyrUp(gauss[i]) − cur`
    /// (a **negated** Laplacian), stopping when `gauss` is full; `lap.back() = gauss.back()`.</summary>
    public static void BuildPyramids(Image<float>[] gauss, Image<float>[] lap, Image<float> src, Alloc? al = null)
    {
        if (src.Width <= 0 || src.Height <= 0) throw new InvalidOperationException("empty input!");
        if (gauss.Length != lap.Length - 1) throw new InvalidOperationException("gaussian/laplacian pyramid size mismatch!");
        if (lap.Length == 1) { lap[0] = src.Copy(); return; }
        var cur = src; int i = 0, n;
        while (true)
        {
            gauss[i] = NewLevel(cur, al);
            PyrDown(gauss[i], cur);
            lap[i] = New(cur.Width, cur.Height, al);
            PyrUpSub(lap[i], gauss[i], cur);
            n = i + 1;
            if (n >= gauss.Length) break;
            cur = gauss[i]; i = n;
            if (!(gauss[i - 1].Width > 1 || gauss[i - 1].Height > 1)) break;
        }
        lap[n] = Copy(gauss[n - 1], al);
    }

    static Image<float> Copy(Image<float> src, Alloc? al)
    {
        if (al is null) return src.Copy();
        var o = al.Image(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++) src.Row(y).CopyTo(o.Row(y));
        return o;
    }

    /// <summary>The level count comes from `pyr.size()`; in place —
    /// `pyr[k] = PyrDown(pyr[k−1])` then `pyr[k−1] = PyrUp(pyr[k]) − pyr[k−1]` (with `pyr[-1] ≡ src`),
    /// leaving the coarsest Gaussian in `pyr[n−1]`.</summary>
    public static void BuildLaplacian(Image<float>[] pyr, Image<float> src, Alloc? al = null)
    {
        if (src.Width <= 0 || src.Height <= 0) throw new InvalidOperationException("empty input!");
        int n = pyr.Length;
        if (n == 1) { pyr[0] = src.Copy(); return; }
        if (n < 2) return;
        if (!(src.Width > 1 || src.Height >= 2)) return;
        var cur = src;
        for (int i = 1; i < n; i++)
        {
            pyr[i] = NewLevel(cur, al);
            PyrDown(pyr[i], cur);
            var lapLevel = New(cur.Width, cur.Height, al);
            PyrUpSub(lapLevel, pyr[i], cur);
            pyr[i - 1] = lapLevel;
            if (i + 1 >= n) break;
            cur = pyr[i];
            if (!(pyr[i].Width > 1 || pyr[i].Height > 1)) break;
        }
    }

    /// <summary>`A = lap.back()`, `A = PyrUp(A) − lap[i]` for
    /// `i = size−2 … start+1`, `out = PyrUp(A) − lap[start]`.</summary>
    public static Image<float> Collapse(IReadOnlyList<Image<float>> lap, int start, Alloc? al = null)
    {
        int n = lap.Count;
        if (n == 0) throw new InvalidOperationException("empty input pyramid!");
        if (start < 0 || start >= n) throw new InvalidOperationException("invalid integration stop level!");
        if (start == n - 1) return Copy(lap[n - 1], al);
        var a = Copy(lap[n - 1], al);
        for (int i = n - 2; i > start; i--)
        {
            var t = New(lap[i].Width, lap[i].Height, al);
            PyrUpSub(t, a, lap[i]);
            a = t;
        }
        var outImg = New(lap[start].Width, lap[start].Height, al);
        PyrUpSub(outImg, a, lap[start]);
        return outImg;
    }
}
