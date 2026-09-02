namespace Lux.Engine.Pipeline.Geometry;

/// <summary>Lumen's per-module aligned warp resampler — `ReferenceImageCache::processLevel` lambda_5 (`1804dad10`) with the
/// 64-phase Catmull-Rom (a = −0.5) weight table from `FUN_180305680`, clamped 4×4 gather at the source border, fill outside.
/// Output pixel (x,y) of the grid maps through the level-L aligned map: `s = map(origin + (x,y))`, `p = (int)((s − 1 − srcOff)·64)`,
/// base `p >> 6`, phase `p &amp; 63`. Positive and negative kernel lobes are accumulated separately and recombined as
/// `out = max(−0.25·P, N) + P` (P = same-sign products, N = cross-sign products).</summary>
public static class WarpResample
{
    /// <summary>`FUN_180305680`: the four tap weights k(t+1), k(t), k(1−t), k(2−t) for offset t.
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

    /// <summary>A 4-channel float image region: pixel (x,y) with rect.X0 ≤ x &lt; rect.X1 lives at data[((y·stride + x)·4)].</summary>
    public readonly record struct Source(float[] Data, int Stride, int X0, int Y0, int X1, int Y1);

    /// <summary>Warp `src` into `dst` (dstW×dstH, 4 floats per pixel, row stride dstW) for grid pixels
    /// [rect.x0,rect.x1)×[rect.y0,rect.y1) (output-image coordinates; the tile order is irrelevant — the kernel is per pixel).</summary>
    public static void Warp(AlignedCalib calib, int level, in Source src, int srcOffX, int srcOffY, int originX, int originY,
                            float[] dst, int dstW, int dstH, ReadOnlySpan<float> fill, float[]? table = null, bool inlinedMap = false)
    {
        table ??= BuildTable();
        int sx0 = src.X0, sy0 = src.Y0, sx1 = src.X1, sy1 = src.Y1;
        int sxMax = sx1 - 1, syMax = sy1 - 1;
        Span<float> block = stackalloc float[64];
        for (int y = 0; y < dstH; y++)
        {
            for (int x = 0; x < dstW; x++)
            {
                float mx = (float)originX + (float)x, my = (float)originY + (float)y;
                int px, py;
                if (inlinedMap)
                {   // PipelineCache::processLevel1 kernel 1804e49f0: u = ((cx − 1) + lu·dx) − x0 (level 0 only)
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
                float[] data; int stride; int baseIdx;
                if (ix < sx0 || ix > sx1 - 4 || iy < sy0 || iy > sy1 - 4)
                {
                    if (!(ix < sx1 && sx0 < ix + 4 && iy < sy1 && sy0 < iy + 4))
                    {
                        dst[o] = fill[0]; dst[o + 1] = fill[1]; dst[o + 2] = fill[2]; dst[o + 3] = fill[3];
                        continue;
                    }
                    // partial overlap: gather a 4×4 block with clamped indices
                    Span<int> cx = stackalloc int[4]; Span<int> cy = stackalloc int[4];
                    for (int k = 0; k < 4; k++)
                    {
                        int xx = ix + k; if (xx < sx0) xx = sx0; if (xx > sxMax) xx = sxMax; cx[k] = xx;
                        int yy = iy + k; if (yy < sy0) yy = sy0; if (yy > syMax) yy = syMax; cy[k] = yy;
                    }
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                        {
                            int si = (cy[r] * src.Stride + cx[c]) * 4, bi = (r * 4 + c) * 4;
                            block[bi] = src.Data[si]; block[bi + 1] = src.Data[si + 1]; block[bi + 2] = src.Data[si + 2]; block[bi + 3] = src.Data[si + 3];
                        }
                    data = null!; stride = 4; baseIdx = 0;
                    Resample(block, stride, baseIdx, table, px & 63, py & 63, dst, o);
                }
                else
                {
                    data = src.Data; stride = src.Stride; baseIdx = (iy * stride + ix) * 4;
                    Resample(data, stride, baseIdx, table, px & 63, py & 63, dst, o);
                }
            }
        }
    }

    internal static void Resample(ReadOnlySpan<float> s, int stride, int b, float[] table, int phx, int phy, float[] dst, int o)
    {
        int ty = phy * 8, tx = phx * 8;
        float py0 = table[ty], py1 = table[ty + 1], py2 = table[ty + 2], py3 = table[ty + 3];
        float ny0 = table[ty + 4], ny1 = table[ty + 5], ny2 = table[ty + 6], ny3 = table[ty + 7];
        float px0 = table[tx], px1 = table[tx + 1], px2 = table[tx + 2], px3 = table[tx + 3];
        float nx0 = table[tx + 4], nx1 = table[tx + 5], nx2 = table[tx + 6], nx3 = table[tx + 7];
        int r1 = stride * 4, r2 = 2 * r1, r3 = 3 * r1;
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
}
