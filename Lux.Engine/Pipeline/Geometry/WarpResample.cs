using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Lux.Engine.Pipeline.Geometry;

/// <summary>Lumen's per-module aligned warp resampler — `ReferenceImageCache::processLevel` lambda_5 with the
/// 64-phase Catmull-Rom (a = −0.5) weight table of <c>Kernel</c>, clamped 4×4 gather at the source border, fill outside.
/// Output pixel (x,y) of the grid maps through the level-L aligned map: `s = map(origin + (x,y))`, `p = (int)((s − 1 − srcOff)·64)`,
/// base `p >> 6`, phase `p &amp; 63`. Positive and negative kernel lobes are accumulated separately and recombined as
/// `out = max(−0.25·P, N) + P` (P = same-sign products, N = cross-sign products).</summary>
public static class WarpResample
{
    /// <summary>The four tap weights k(t+1), k(t), k(1−t), k(2−t) for offset t.
    /// |x|&lt;1: ((x²·−15 + 6) + x³·9)/6; 1≤|x|&lt;2: (((x·−24 + 12) + x²·15) + x³·−3)/6; else 0.</summary>
    public static void Kernel(float t, Span<float> w)
    {
        w[0] = K(t + 1.0f); w[1] = K(t); w[2] = K(1.0f - t); w[3] = K(2.0f - t);
        static float K(float x)
        {
            float x2 = x * x;
            if (1.0f <= x)
                return x < 2.0f ? (((x * -24.0f + 12.0f) + x2 * 15.0f) + x2 * x * -3.0f) * (1.0f / 6.0f) : 0.0f;
            return ((x2 * -15.0f + 6.0f) + x2 * x * 9.0f) * (1.0f / 6.0f);
        }
    }

    /// <summary>64 phases × [pos0..pos3, neg0..neg3] (scalars; Lumen stores each broadcast to a vec4).</summary>
    public static float[] BuildTable()
    {
        var table = new float[64 * 8];
        Span<float> k = stackalloc float[4];
        for (int i = 0; i < 64; i++)
        {
            Kernel((float)i * 0.015625f, k);
            for (int t = 0; t < 4; t++)
            {
                table[i * 8 + t] = MathF.Max(k[t], 0f);       // maxps(0, k)
                table[i * 8 + 4 + t] = MathF.Min(k[t], 0f);   // minps(k, 0)
            }
        }
        return table;
    }

    /// <summary>A 4-channel float image region: pixel (x,y) with rect.X0 ≤ x &lt; rect.X1 lives at data[Base + ((y·stride + x)·4)].
    /// The rect is the clamp window of the kernel (`lt::Image` rect fields, relative to the data origin); it may start before the data
    /// origin (negative X0/Y0) when the source is a view into a larger image, which <paramref name="Base"/> (element offset of pixel (0,0)) allows.</summary>
    public readonly record struct Source(float[] Data, int Stride, int X0, int Y0, int X1, int Y1, int Base = 0);

    static readonly float[] SharedTable = BuildTable();

    /// <summary>Warp `src` into `dst` (dstW×dstH, 4 floats per pixel, row stride dstW) for grid pixels
    /// [rect.x0,rect.x1)×[rect.y0,rect.y1) (output-image coordinates; the tile order is irrelevant — the kernel is per pixel).
    /// Each row has two passes: the map of all pixels of the row, then the resample of all pixels of the row.</summary>
    public static void Warp(AlignedCalib calib, int level, in Source src, int srcOffX, int srcOffY, int originX, int originY,
                            float[] dst, int dstW, int dstH, ReadOnlySpan<float> fill, float[]? table = null, bool inlinedMap = false)
    {
        table ??= SharedTable;
        int sx0 = src.X0, sy0 = src.Y0, sx1 = src.X1, sy1 = src.Y1;
        int sxMax = sx1 - 1, syMax = sy1 - 1; int stride = src.Stride, sbase = src.Base;
        // The row passes read the source, the table and the destination without index checks. That is safe when the whole clamp
        // window is inside the source data and the other buffers have the necessary size. Other inputs take the checked code.
        long first = sbase + ((long)sy0 * stride + sx0) * 4, last = sbase + ((long)syMax * stride + sxMax) * 4 + 3;
        if (!Vector128.IsHardwareAccelerated || dstW <= 0 || dstH <= 0 || sx1 <= sx0 || sy1 <= sy0 || stride < 0 || first < 0 || last >= src.Data.Length
            || dst.Length < (long)dstW * dstH * 4 || table.Length < 64 * 8 || fill.Length < 4)
        {
            WarpChecked(calib, level, src, srcOffX, srcOffY, originX, originY, dst, dstW, dstH, fill, table, inlinedMap);
            return;
        }
        float fOffX = (float)srcOffX, fOffY = (float)srcOffY;
        float cx = calib.Cx, cy = calib.Cy, cxm1 = cx + -1.0f, cym1 = cy + -1.0f;
        float inv = 1.0f / (float)(1 << level);
        float f0 = fill[0], f1 = fill[1], f2 = fill[2], f3 = fill[3];
        Span<float> block = stackalloc float[64];
        Span<int> bx = stackalloc int[4]; Span<int> by = stackalloc int[4];
        ref float sd = ref MemoryMarshal.GetArrayDataReference(src.Data);
        ref float td = ref MemoryMarshal.GetArrayDataReference(table);
        ref float dd = ref MemoryMarshal.GetArrayDataReference(dst);
        var rowBuf = ScratchPool.Rent<float>(2 * dstW);
        try
        {
            var du = rowBuf.AsSpan(0, dstW); var dv = rowBuf.AsSpan(dstW, dstW);
            for (int y = 0; y < dstH; y++)
            {
                // map pass: the offsets of the mapped points from the distortion center
                calib.MapRowOffsets(originX, (float)originY + (float)y, inlinedMap ? 0 : level, du, dv);
                // resample pass
                for (int x = 0; x < dstW; x++)
                {
                    int px, py;
                    if (inlinedMap)
                    {   // PipelineCache::processLevel1 kernel: u = ((cx − 1) + lu·dx) − x0 (level 0 only)
                        px = (int)(((cxm1 + du[x]) - fOffX) * 64.0f);
                        py = (int)(((cym1 + dv[x]) - fOffY) * 64.0f);
                    }
                    else
                    {
                        float u = (du[x] + cx) * inv, v = (cy + dv[x]) * inv;
                        px = (int)(((u + -1.0f) - fOffX) * 64.0f);
                        py = (int)(((v + -1.0f) - fOffY) * 64.0f);
                    }
                    int ix = px >> 6, iy = py >> 6;
                    ref float o = ref Unsafe.Add(ref dd, (nint)(((long)y * dstW + x) * 4));
                    ref float ty = ref Unsafe.Add(ref td, (py & 63) * 8); ref float tx = ref Unsafe.Add(ref td, (px & 63) * 8);
                    if (ix < sx0 || ix > sx1 - 4 || iy < sy0 || iy > sy1 - 4)
                    {
                        if (!(ix < sx1 && sx0 < ix + 4 && iy < sy1 && sy0 < iy + 4))
                        {
                            o = f0; Unsafe.Add(ref o, 1) = f1; Unsafe.Add(ref o, 2) = f2; Unsafe.Add(ref o, 3) = f3;
                            continue;
                        }
                        // partial overlap: gather a 4×4 block with clamped indices
                        for (int k = 0; k < 4; k++)
                        {
                            int xx = ix + k; if (xx < sx0) xx = sx0; if (xx > sxMax) xx = sxMax; bx[k] = xx;
                            int yy = iy + k; if (yy < sy0) yy = sy0; if (yy > syMax) yy = syMax; by[k] = yy;
                        }
                        for (int r = 0; r < 4; r++)
                            for (int c = 0; c < 4; c++)
                                Vector128.LoadUnsafe(ref sd, (nuint)(sbase + ((long)by[r] * stride + bx[c]) * 4)).CopyTo(block.Slice((r * 4 + c) * 4, 4));
                        ResampleLanes(ref MemoryMarshal.GetReference(block), 16, ref ty, ref tx, ref o);
                    }
                    else
                        ResampleLanes(ref Unsafe.Add(ref sd, (nint)(sbase + ((long)iy * stride + ix) * 4)), (nint)stride * 4, ref ty, ref tx, ref o);
                }
            }
        }
        finally { ScratchPool.Return(rowBuf); }
    }

    /// <summary>The warp with the map and the resample of one pixel at a time, and an index check on each read and write.</summary>
    static void WarpChecked(AlignedCalib calib, int level, in Source src, int srcOffX, int srcOffY, int originX, int originY,
                            float[] dst, int dstW, int dstH, ReadOnlySpan<float> fill, float[] table, bool inlinedMap)
    {
        int sx0 = src.X0, sy0 = src.Y0, sx1 = src.X1, sy1 = src.Y1;
        int sxMax = sx1 - 1, syMax = sy1 - 1;
        Span<float> block = stackalloc float[64];
        Span<int> cx = stackalloc int[4]; Span<int> cy = stackalloc int[4];
        for (int y = 0; y < dstH; y++)
        {
            for (int x = 0; x < dstW; x++)
            {
                float mx = (float)originX + (float)x, my = (float)originY + (float)y;
                int px, py;
                if (inlinedMap)
                {   // PipelineCache::processLevel1 kernel: u = ((cx − 1) + lu·dx) − x0 (level 0 only)
                    var (u1, v1) = calib.MapInlinedMinus1(mx, my);
                    px = (int)((u1 - (float)srcOffX) * 64.0f);
                    py = (int)((v1 - (float)srcOffY) * 64.0f);
                }
                else
                {
                    var (u, v) = calib.Map(mx, my, level);
                    px = (int)(((u + -1.0f) - (float)srcOffX) * 64.0f);
                    py = (int)(((v + -1.0f) - (float)srcOffY) * 64.0f);
                }
                int ix = px >> 6, iy = py >> 6;
                int o = (y * dstW + x) * 4;
                if (ix < sx0 || ix > sx1 - 4 || iy < sy0 || iy > sy1 - 4)
                {
                    if (!(ix < sx1 && sx0 < ix + 4 && iy < sy1 && sy0 < iy + 4))
                    {
                        dst[o] = fill[0]; dst[o + 1] = fill[1]; dst[o + 2] = fill[2]; dst[o + 3] = fill[3];
                        continue;
                    }
                    // partial overlap: gather a 4×4 block with clamped indices
                    for (int k = 0; k < 4; k++)
                    {
                        int xx = ix + k; if (xx < sx0) xx = sx0; if (xx > sxMax) xx = sxMax; cx[k] = xx;
                        int yy = iy + k; if (yy < sy0) yy = sy0; if (yy > syMax) yy = syMax; cy[k] = yy;
                    }
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                        {
                            int si = src.Base + (cy[r] * src.Stride + cx[c]) * 4, bi = (r * 4 + c) * 4;
                            block[bi] = src.Data[si]; block[bi + 1] = src.Data[si + 1]; block[bi + 2] = src.Data[si + 2]; block[bi + 3] = src.Data[si + 3];
                        }
                    Resample(block, 4, 0, table, px & 63, py & 63, dst, o);
                }
                else
                    Resample(src.Data, src.Stride, src.Base + (iy * src.Stride + ix) * 4, table, px & 63, py & 63, dst, o);
            }
        }
    }

    /// <summary>The resample of one pixel: the 4×4 block of pixels that starts at `s[b]` (row stride `stride` pixels), the weights
    /// of phase (`phx`, `phy`), the result in `dst[o..o+3]`.</summary>
    internal static void Resample(ReadOnlySpan<float> s, int stride, int b, float[] table, int phx, int phy, float[] dst, int o)
    {
        int ty = phy * 8, tx = phx * 8;
        int r1 = stride * 4, r2 = 2 * r1, r3 = 3 * r1;
        if (Vector128.IsHardwareAccelerated && b >= 0 && r1 >= 0 && (long)b + 3L * r1 + 16 <= s.Length
            && ty >= 0 && tx >= 0 && ty + 8 <= table.Length && tx + 8 <= table.Length && o >= 0 && (long)o + 4 <= dst.Length)
        {
            ref float td = ref MemoryMarshal.GetArrayDataReference(table);
            ResampleLanes(ref Unsafe.Add(ref MemoryMarshal.GetReference(s), b), r1, ref Unsafe.Add(ref td, ty), ref Unsafe.Add(ref td, tx),
                          ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dst), o));
            return;
        }
        float py0 = table[ty], py1 = table[ty + 1], py2 = table[ty + 2], py3 = table[ty + 3];
        float ny0 = table[ty + 4], ny1 = table[ty + 5], ny2 = table[ty + 6], ny3 = table[ty + 7];
        float px0 = table[tx], px1 = table[tx + 1], px2 = table[tx + 2], px3 = table[tx + 3];
        float nx0 = table[tx + 4], nx1 = table[tx + 5], nx2 = table[tx + 6], nx3 = table[tx + 7];
        for (int ch = 0; ch < 4; ch++)
        {
            int i0 = b + ch;
            // rows R0..R3 of the 4 columns
            float r00 = s[i0], r01 = s[i0 + 4], r02 = s[i0 + 8], r03 = s[i0 + 12];
            float r10 = s[i0 + r1], r11 = s[i0 + r1 + 4], r12 = s[i0 + r1 + 8], r13 = s[i0 + r1 + 12];
            float r20 = s[i0 + r2], r21 = s[i0 + r2 + 4], r22 = s[i0 + r2 + 8], r23 = s[i0 + r2 + 12];
            float r30 = s[i0 + r3], r31 = s[i0 + r3 + 4], r32 = s[i0 + r3 + 8], r33 = s[i0 + r3 + 12];
            // vertical pass per column: P_c = (py3·R3c + py2·R2c) + (py1·R1c + py0·R0c); N_c likewise with the negative lobes
            float P0 = (py3 * r30 + py2 * r20) + (py1 * r10 + py0 * r00);
            float P1 = (py3 * r31 + py2 * r21) + (py1 * r11 + py0 * r01);
            float P2 = (py3 * r32 + py2 * r22) + (py1 * r12 + py0 * r02);
            float P3 = (py3 * r33 + py2 * r23) + (py1 * r13 + py0 * r03);
            float N0 = (ny3 * r30 + ny2 * r20) + (ny1 * r10 + ny0 * r00);
            float N1 = (ny3 * r31 + ny2 * r21) + (ny1 * r11 + ny0 * r01);
            float N2 = (ny3 * r32 + ny2 * r22) + (ny1 * r12 + ny0 * r02);
            float N3 = (ny3 * r33 + ny2 * r23) + (ny1 * r13 + ny0 * r03);
            // horizontal pass: same-sign products (Ptot) and cross-sign products (Ntot)
            float Ptot = nx3 * N3 + ((px3 * P3 + (nx2 * N2 + px2 * P2)) + ((nx1 * N1 + px1 * P1) + (nx0 * N0 + px0 * P0)));
            float Ntot = ((N3 * px3 + (N2 * px2 + P2 * nx2)) + ((N1 * px1 + P1 * nx1) + (N0 * px0 + P0 * nx0))) + P3 * nx3;
            float clamped = MathF.Max(Ptot * -0.25f, Ntot);
            dst[o + ch] = clamped + Ptot;
        }
    }

    /// <summary>The resample of one pixel with its four channels in one vector. Each lane does the operations of the scalar code in
    /// the same order. `s` is the first float of the 4×4 block, `rowFloats` the distance between two rows in floats, `ty` and `tx`
    /// the eight weights of the vertical and the horizontal phase. The caller makes sure that all 64 source floats, the 16 weights
    /// and the four result floats are inside their buffers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ResampleLanes(ref float s, nint rowFloats, ref float ty, ref float tx, ref float dst)
    {
        var py0 = Vector128.Create(ty); var py1 = Vector128.Create(Unsafe.Add(ref ty, 1)); var py2 = Vector128.Create(Unsafe.Add(ref ty, 2)); var py3 = Vector128.Create(Unsafe.Add(ref ty, 3));
        var ny0 = Vector128.Create(Unsafe.Add(ref ty, 4)); var ny1 = Vector128.Create(Unsafe.Add(ref ty, 5)); var ny2 = Vector128.Create(Unsafe.Add(ref ty, 6)); var ny3 = Vector128.Create(Unsafe.Add(ref ty, 7));
        ref float s1 = ref Unsafe.Add(ref s, rowFloats); ref float s2 = ref Unsafe.Add(ref s1, rowFloats); ref float s3 = ref Unsafe.Add(ref s2, rowFloats);
        var r00 = Vector128.LoadUnsafe(ref s); var r01 = Vector128.LoadUnsafe(ref s, 4); var r02 = Vector128.LoadUnsafe(ref s, 8); var r03 = Vector128.LoadUnsafe(ref s, 12);
        var r10 = Vector128.LoadUnsafe(ref s1); var r11 = Vector128.LoadUnsafe(ref s1, 4); var r12 = Vector128.LoadUnsafe(ref s1, 8); var r13 = Vector128.LoadUnsafe(ref s1, 12);
        var r20 = Vector128.LoadUnsafe(ref s2); var r21 = Vector128.LoadUnsafe(ref s2, 4); var r22 = Vector128.LoadUnsafe(ref s2, 8); var r23 = Vector128.LoadUnsafe(ref s2, 12);
        var r30 = Vector128.LoadUnsafe(ref s3); var r31 = Vector128.LoadUnsafe(ref s3, 4); var r32 = Vector128.LoadUnsafe(ref s3, 8); var r33 = Vector128.LoadUnsafe(ref s3, 12);
        var P0 = (py3 * r30 + py2 * r20) + (py1 * r10 + py0 * r00);
        var P1 = (py3 * r31 + py2 * r21) + (py1 * r11 + py0 * r01);
        var P2 = (py3 * r32 + py2 * r22) + (py1 * r12 + py0 * r02);
        var P3 = (py3 * r33 + py2 * r23) + (py1 * r13 + py0 * r03);
        var N0 = (ny3 * r30 + ny2 * r20) + (ny1 * r10 + ny0 * r00);
        var N1 = (ny3 * r31 + ny2 * r21) + (ny1 * r11 + ny0 * r01);
        var N2 = (ny3 * r32 + ny2 * r22) + (ny1 * r12 + ny0 * r02);
        var N3 = (ny3 * r33 + ny2 * r23) + (ny1 * r13 + ny0 * r03);
        var px0 = Vector128.Create(tx); var px1 = Vector128.Create(Unsafe.Add(ref tx, 1)); var px2 = Vector128.Create(Unsafe.Add(ref tx, 2)); var px3 = Vector128.Create(Unsafe.Add(ref tx, 3));
        var nx0 = Vector128.Create(Unsafe.Add(ref tx, 4)); var nx1 = Vector128.Create(Unsafe.Add(ref tx, 5)); var nx2 = Vector128.Create(Unsafe.Add(ref tx, 6)); var nx3 = Vector128.Create(Unsafe.Add(ref tx, 7));
        var Ptot = nx3 * N3 + ((px3 * P3 + (nx2 * N2 + px2 * P2)) + ((nx1 * N1 + px1 * P1) + (nx0 * N0 + px0 * P0)));
        var Ntot = ((N3 * px3 + (N2 * px2 + P2 * nx2)) + ((N1 * px1 + P1 * nx1) + (N0 * px0 + P0 * nx0))) + P3 * nx3;
        // Vector128.Max is the IEEE maximum, as MathF.Max in the scalar code: a NaN operand gives a NaN, and +0 is above −0.
        var clamped = Vector128.Max(Ptot * Vector128.Create(-0.25f), Ntot);
        (clamped + Ptot).StoreUnsafe(ref dst);
    }
}
