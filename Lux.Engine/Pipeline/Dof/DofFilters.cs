using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>The image kernels the depth effect uses, with Lumen's summation order and its rect-clamped (sub-view) reads.
/// <para>The kernels run lane-parallel (<see cref="Vector128{T}"/> / <see cref="Vector256{T}"/>) over values that are
/// independent of each other — every lane performs exactly the scalar code's IEEE operations on its own value in the scalar code's order
/// (separate multiplies and adds, never a fused multiply-add), so the results are the same bits. Optional destination arrays let callers
/// reuse scratch buffers (<see cref="ScratchPool"/>).</para></summary>
public static class DofFilters
{
    /// <summary>Depth-effect tiles being made now (<see cref="DofTileCache"/>): the row kernels split a tile only when few are.</summary>
    internal static int ActiveTiles;

    /// <summary>Run <paramref name="body"/>(y0, y1) over row bands of [0, h) — on several threads when the image is large. Every
    /// kernel using it computes each output row from its inputs alone, so the pixels do not depend on how the rows are split.</summary>
    internal static void Rows(int h, long pixelsPerRow, Action<int, int> body, int minRows = 8)
    {
        int procs = Environment.ProcessorCount;
        // only when few tiles are being made at once (a coarse level, a drag frame): with many tiles in flight the threads are busy already
        if (h < 32 || (long)h * pixelsPerRow < 65536 || procs < 2 || Volatile.Read(ref ActiveTiles) > Math.Max(1, procs / 4)) { body(0, h); return; }
        int bands = Math.Min(procs * 2, Math.Max(1, h / minRows)), per = (h + bands - 1) / bands;
        if (bands < 2) { body(0, h); return; }
        Parallel.For(0, bands, b => { int y0 = b * per, y1 = Math.Min(h, y0 + per); if (y0 < y1) body(y0, y1); });
    }

    /// <summary>Gaussian kernel (n, σ, scale): k[i] = expf((i − c)²·(−0.5/σ²)), c = (n−1)·0.5, then k *= scale/Σk (Σ in index order).
    /// σ == 0 gives a delta. This is the scalar path (n &lt; 8).</summary>
    public static float[] GaussianKernel(int n, float sigma, float scale)
    {
        var k = new float[n];
        if (sigma == 0f) { k[n >> 1] = 1f; return k; }
        float f = -0.5f / (sigma * sigma), c = (float)(n - 1) * 0.5f, sum = 0f;
        for (int i = 0; i < n; i++) { float d = (float)i - c; k[i] = MuslMath.Expf(d * d * f); sum += k[i]; }
        float m = scale / sum;
        for (int i = 0; i < n; i++) k[i] *= m;
        return k;
    }

    // ---- ImageResample<4,…>: 64-phase uniform cubic B-spline in 16.16 fixed point ----

    /// <summary>The four tap weights at distances t+1, t, 1−t, 2−t of the cubic B-spline
    /// W(d) = d &lt; 1 ? ((d²·−6 + 4) + d³·3)·(1/6) : d &lt; 2 ? (((d·−12 + 8) + d²·6) − d³)·(1/6) : 0 (d³ = (d·d)·d).</summary>
    static float BSpline(float d)
    {
        float d2 = d * d, d3 = d2 * d;
        if (!(d >= 1f)) return ((d2 * -6f + 4f) + d3 * 3f) * 0.16666667f;
        if (!(d >= 2f)) return (((d * -12f + 8f) + d2 * 6f) - d3) * 0.16666667f;
        return 0f;
    }
    static readonly float[] Table4 = BuildTable4();
    static float[] BuildTable4()
    {
        var t = new float[64 * 4];
        for (int i = 0; i < 64; i++) { float p = (float)i * 0.015625f; t[i * 4] = BSpline(p + 1f); t[i * 4 + 1] = BSpline(p); t[i * 4 + 2] = BSpline(1f - p); t[i * 4 + 3] = BSpline(2f - p); }
        return t;
    }

    /// <summary>`ImageResample&lt;4,T&gt;(dst, src, offset, scale)` for float lanes (the vec4x32f and float instantiations):
    /// destination pixel (x,y) samples the source at 16.16 position `(int)(scale·65536)·x + (int)(offset·65536)`,
    /// taps i−1…i+2 clamped to the source rect fields, phase = bits 10…15; each pass sums (w1·s1 + w0·s0) + (w3·s3 + w2·s2), horizontal first.
    /// <paramref name="dstBuffer"/>: an optional array (at least dw·dh·c long) to write into.</summary>
    public static DofImage Resample4(DofImage src, int dw, int dh, double offX, double offY, double scX, double scY, float[]? dstBuffer = null)
    {
        int ox = (int)(offX * 65536.0), oy = (int)(offY * 65536.0), sx = (int)(scX * 65536.0), sy = (int)(scY * 65536.0), c = src.C;
        var dst = dstBuffer is null ? new DofImage(dw, dh, c) : new DofImage(dstBuffer, c, dw, dh); var o = dst.D; var s = src.D;
        // per destination column: the four clamped source offsets (in floats, relative to a row) and the phase's weights
        var xi = new int[dw * 4]; var xw = new float[dw * 4];
        for (int x = 0; x < dw; x++)
        {
            int rx = sx * x + ox, ix = rx >> 16, ph = ((rx >> 10) & 63) * 4;
            xi[x * 4] = Clamp(ix - 1, src.X0, src.X1 - 1) * c; xi[x * 4 + 1] = Clamp(ix, src.X0, src.X1 - 1) * c;
            xi[x * 4 + 2] = Clamp(ix + 1, src.X0, src.X1 - 1) * c; xi[x * 4 + 3] = Clamp(ix + 2, src.X0, src.X1 - 1) * c;
            xw[x * 4] = Table4[ph]; xw[x * 4 + 1] = Table4[ph + 1]; xw[x * 4 + 2] = Table4[ph + 2]; xw[x * 4 + 3] = Table4[ph + 3];
        }
        int rowLen = dw * c;
        Rows(dh, (long)dw * 8, (ya, yb) =>
        {
            // a small cache of horizontally filtered source rows (the source rows a destination row reads only move forward)
            const int Slots = 8;
            var keys = new int[Slots]; var bufs = new float[Slots][]; var used = new long[Slots]; long tick = 0;
            for (int k = 0; k < Slots; k++) keys[k] = int.MinValue;
            float[] HRow(int iy)
            {
                int cy = Math.Min(Math.Max(iy, src.Y0), src.Y1 - 1);
                int victim = 0;
                for (int k = 0; k < Slots; k++)
                {
                    if (keys[k] == cy) { used[k] = ++tick; return bufs[k]; }
                    if (used[k] < used[victim]) victim = k;
                }
                var r = bufs[victim] ??= ScratchPool.Rent<float>(rowLen);
                keys[victim] = cy; used[victim] = ++tick;
                HorizontalRow(s, src.Off + cy * src.Stride * c, c, dw, xi, xw, r);
                return r;
            }
            for (int y = ya; y < yb; y++)
            {
                int ry = sy * y + oy, iy = ry >> 16, ph = ((ry >> 10) & 63) * 4;
                var r0 = HRow(iy - 1); var r1 = HRow(iy); var r2 = HRow(iy + 1); var r3 = HRow(iy + 2);
                VerticalRow(r0, r1, r2, r3, Table4[ph], Table4[ph + 1], Table4[ph + 2], Table4[ph + 3], o, y * rowLen, rowLen);
            }
            foreach (var b in bufs) ScratchPool.Return(b);
        });
        return dst;
    }

    /// <summary>One horizontally filtered row: r[x·c + k] = (w1·s1 + w0·s0) + (w3·s3 + w2·s2).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void HorizontalRow(float[] s, int rowBase, int c, int dw, int[] xi, float[] xw, float[] r)
    {
        ref float sb = ref MemoryMarshal.GetArrayDataReference(s);
        ref float rb = ref MemoryMarshal.GetArrayDataReference(r);
        if (c == 4)
        {
            for (int x = 0; x < dw; x++)
            {
                int q = x * 4;
                var w0 = Vector128.Create(xw[q]); var w1 = Vector128.Create(xw[q + 1]); var w2 = Vector128.Create(xw[q + 2]); var w3 = Vector128.Create(xw[q + 3]);
                var s0 = Vector128.LoadUnsafe(ref sb, (nuint)(rowBase + xi[q])); var s1 = Vector128.LoadUnsafe(ref sb, (nuint)(rowBase + xi[q + 1]));
                var s2 = Vector128.LoadUnsafe(ref sb, (nuint)(rowBase + xi[q + 2])); var s3 = Vector128.LoadUnsafe(ref sb, (nuint)(rowBase + xi[q + 3]));
                ((w1 * s1 + w0 * s0) + (w3 * s3 + w2 * s2)).StoreUnsafe(ref rb, (nuint)q);
            }
            return;
        }
        for (int x = 0; x < dw; x++)
        {
            int q = x * 4;
            float w0 = xw[q], w1 = xw[q + 1], w2 = xw[q + 2], w3 = xw[q + 3];
            int i0 = rowBase + xi[q], i1 = rowBase + xi[q + 1], i2 = rowBase + xi[q + 2], i3 = rowBase + xi[q + 3];
            for (int k = 0; k < c; k++) r[x * c + k] = (w1 * s[i1 + k] + w0 * s[i0 + k]) + (w3 * s[i3 + k] + w2 * s[i2 + k]);
        }
    }

    /// <summary>o[b + i] = (v1·r1[i] + v0·r0[i]) + (v3·r3[i] + v2·r2[i]) for i in [0, n).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void VerticalRow(float[] r0, float[] r1, float[] r2, float[] r3, float v0, float v1, float v2, float v3, float[] o, int b, int n)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var V0 = Vector256.Create(v0); var V1 = Vector256.Create(v1); var V2 = Vector256.Create(v2); var V3 = Vector256.Create(v3);
            ref float a0 = ref MemoryMarshal.GetArrayDataReference(r0); ref float a1 = ref MemoryMarshal.GetArrayDataReference(r1);
            ref float a2 = ref MemoryMarshal.GetArrayDataReference(r2); ref float a3 = ref MemoryMarshal.GetArrayDataReference(r3);
            ref float ob = ref MemoryMarshal.GetArrayDataReference(o);
            for (; i + 8 <= n; i += 8)
                ((V1 * Vector256.LoadUnsafe(ref a1, (nuint)i) + V0 * Vector256.LoadUnsafe(ref a0, (nuint)i)) + (V3 * Vector256.LoadUnsafe(ref a3, (nuint)i) + V2 * Vector256.LoadUnsafe(ref a2, (nuint)i)))
                    .StoreUnsafe(ref ob, (nuint)(b + i));
        }
        for (; i < n; i++) o[b + i] = (v1 * r1[i] + v0 * r0[i]) + (v3 * r3[i] + v2 * r2[i]);
    }

    /// <summary>`ImageResample&lt;4,unsigned char&gt;`: the same B-spline
    /// resample on bytes. Rows are filtered horizontally into floats ((w1·s1 + w0·s0) + (w3·s3 + w2·s2)); the vertical sum is rounded
    /// v + copysign(0.5, v), clamped to [0, 255] and truncated. The lambda runs per 256×256 `Tiler` tile of the destination and
    /// vectorizes four pixels at a time: those use (w0·r0 + w1·r1) + (w2·r2 + w3·r3), the last (tile width &amp; 3) pixels of each tile row use
    /// (w0·r0 + w2·r2) + (w1·r1 + w3·r3) (the horizontal-add order of Lumen's tail). Float results depend on these orders.</summary>
    public static byte[] Resample4U8(byte[] src, int sw, int sh, int dw, int dh, double offX, double offY, double scX, double scY)
    {
        int ox = (int)(offX * 65536.0), oy = (int)(offY * 65536.0), sx = (int)(scX * 65536.0), sy = (int)(scY * 65536.0);
        var dst = new byte[dw * dh];
        var xi = new int[dw * 4]; var xw = new float[dw * 4];
        for (int x = 0; x < dw; x++)
        {
            int rx = sx * x + ox, ix = rx >> 16, ph = ((rx >> 10) & 63) * 4;
            xi[x * 4] = Clamp(ix - 1, 0, sw - 1); xi[x * 4 + 1] = Clamp(ix, 0, sw - 1); xi[x * 4 + 2] = Clamp(ix + 1, 0, sw - 1); xi[x * 4 + 3] = Clamp(ix + 2, 0, sw - 1);
            xw[x * 4] = Table4[ph]; xw[x * 4 + 1] = Table4[ph + 1]; xw[x * 4 + 2] = Table4[ph + 2]; xw[x * 4 + 3] = Table4[ph + 3];
        }
        // every filtered source row once (the 256-px tiles of the destination revisit them)
        var rowOf = new float[]?[sh];
        float[] HRow(int iy)
        {
            int cy = Clamp(iy, 0, sh - 1);
            if (rowOf[cy] is { } r) return r;
            r = new float[dw]; int b = cy * sw;
            for (int x = 0; x < dw; x++)
            {
                int q = x * 4;
                float s0 = (float)src[b + xi[q]], s1 = (float)src[b + xi[q + 1]], s2 = (float)src[b + xi[q + 2]], s3 = (float)src[b + xi[q + 3]];
                r[x] = (xw[q + 1] * s1 + xw[q] * s0) + (xw[q + 3] * s3 + xw[q + 2] * s2);
            }
            return rowOf[cy] = r;
        }
        foreach (var t in Isp.Tiler.Rects(new RectI(0, 0, dw, dh), 256, 256))
        {
            int tw = t.X1 - t.X0, vec = tw - (tw & 3);
            for (int y = t.Y0; y < t.Y1; y++)
            {
                int ry = sy * y + oy, iy = ry >> 16, ph = ((ry >> 10) & 63) * 4;
                var r0 = HRow(iy - 1); var r1 = HRow(iy); var r2 = HRow(iy + 1); var r3 = HRow(iy + 2);
                float v0 = Table4[ph], v1 = Table4[ph + 1], v2 = Table4[ph + 2], v3 = Table4[ph + 3];
                for (int i = 0; i < tw; i++)
                {
                    int x = t.X0 + i;
                    float v = i < vec ? (v1 * r1[x] + v0 * r0[x]) + (v3 * r3[x] + v2 * r2[x]) : (v2 * r2[x] + v0 * r0[x]) + (v3 * r3[x] + v1 * r1[x]);
                    float q = MathF.CopySign(0.5f, v) + v;
                    q = q > 0f ? q : 0f;
                    q = q < 255f ? q : 255f;
                    dst[y * dw + x] = (byte)(int)q;
                }
            }
        }
        return dst;
    }

    /// <summary>The byte <see cref="Resample4U8"/> gives for an image that is <paramref name="value"/> everywhere when that byte is the same for
    /// every phase pair and both vertical groupings (the function of a constant image), else −1.</summary>
    internal static int ConstantResampleU8(byte value)
    {
        int result = -2; float s = (float)value;
        for (int px = 0; px < 64; px++)
        {
            int hx = px * 4;
            float r = (Table4[hx + 1] * s + Table4[hx] * s) + (Table4[hx + 3] * s + Table4[hx + 2] * s);
            for (int py = 0; py < 64; py++)
            {
                int hy = py * 4; float v0 = Table4[hy], v1 = Table4[hy + 1], v2 = Table4[hy + 2], v3 = Table4[hy + 3];
                foreach (float v in new[] { (v1 * r + v0 * r) + (v3 * r + v2 * r), (v2 * r + v0 * r) + (v3 * r + v1 * r) })
                {
                    float q = MathF.CopySign(0.5f, v) + v;
                    q = q > 0f ? q : 0f;
                    q = q < 255f ? q : 255f;
                    int b = (byte)(int)q;
                    if (result == -2) result = b; else if (result != b) return -1;
                }
            }
        }
        return result;
    }

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);   // max(lo, v) then min(hi, ·), Lumen's cmov order

    // ---- ImageCircleFilter<T> ----

    /// <summary>`ImageCircleFilter&lt;T&gt;(dst, src, radius)` (the vec4x32f and float instantiations): mean over a disc whose row j
    /// (dy = j − r) spans ±(int)sqrt((double)(r² − dy²)). Each output row keeps one running sum:
    /// at x = 0 the disc is summed row by row, left to right; then for every x the sum takes, for j = 0…2r in order, (entering − leaving) pixel.
    /// Coordinates clamp to the source rect fields. out = (1/(float)count)·sum. Four lanes run as one <see cref="Vector128{T}"/> (the same
    /// per-lane adds and subtracts in the same order).</summary>
    public static DofImage CircleFilter(DofImage src, int radius, float[]? dstBuffer = null)
    {
        if (radius <= 0) throw new ArgumentException("Invalid blur radius!");
        int r = radius, n = 2 * r + 1, c = src.C, w = src.W, h = src.H;
        var hw = new int[n]; int count = 0;
        for (int j = 0; j < n; j++) { int dy = j - r; hw[j] = (int)Math.Sqrt((double)(r * r - dy * dy)); count += 2 * hw[j] + 1; }
        float weight = 1f / (float)count;
        var dst = dstBuffer is null ? new DofImage(w, h, c) : new DofImage(dstBuffer, c, w, h); var o = dst.D; var s = src.D;
        int xlo = src.X0, xhi = src.X1 - 1;
        if (c == 4)
        {
            Rows(h, (long)w * n, (ya, yb) => CircleRows4(src, r, n, hw, weight, o, 0, ya, yb));
            return dst;
        }
        Rows(h, (long)w * n, (ya, yb) =>
        {
        var sum = new float[c]; var rowBase = new int[n];
        for (int y = ya; y < yb; y++)
        {
            for (int j = 0; j < n; j++) rowBase[j] = src.Off + Clamp(y - r + j, src.Y0, src.Y1 - 1) * src.Stride * c;
            Array.Clear(sum);
            for (int j = 0; j < n; j++)
                for (int xx = -hw[j]; xx <= hw[j]; xx++) { int p = rowBase[j] + Clamp(xx, xlo, xhi) * c; for (int k = 0; k < c; k++) sum[k] += s[p + k]; }
            int ob = y * w * c;
            for (int k = 0; k < c; k++) o[ob + k] = weight * sum[k];
            for (int x = 1; x < w; x++)
            {
                for (int j = 0; j < n; j++)
                {
                    int a = rowBase[j] + Clamp(x + hw[j], xlo, xhi) * c, b = rowBase[j] + Clamp(x - hw[j] - 1, xlo, xhi) * c;
                    for (int k = 0; k < c; k++) sum[k] += s[a + k] - s[b + k];
                }
                for (int k = 0; k < c; k++) o[ob + x * c + k] = weight * sum[k];
            }
        }
        });
        return dst;
    }

    /// <summary>The disc's half-widths and weight for <paramref name="radius"/> (as <see cref="CircleFilter"/> computes them).</summary>
    internal static (int[] Hw, float Weight) CircleDisc(int radius)
    {
        int r = radius, n = 2 * r + 1; var hw = new int[n]; int count = 0;
        for (int j = 0; j < n; j++) { int dy = j - r; hw[j] = (int)Math.Sqrt((double)(r * r - dy * dy)); count += 2 * hw[j] + 1; }
        return (hw, 1f / (float)count);
    }

    /// <summary>Rows [ya, yb) of the four-lane <see cref="CircleFilter"/>, written at <paramref name="oOff"/> + y·w·4 of <paramref name="o"/>
    /// (the source may be a window of rows of a taller image: its offset is then negative and only the rows the disc reads exist).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void CircleRows4(DofImage src, int r, int n, int[] hw, float weight, float[] o, int oOff, int ya, int yb)
    {
        int w = src.W, xlo = src.X0, xhi = src.X1 - 1, rowStride = src.Stride * 4;
        ref float sb = ref MemoryMarshal.GetArrayDataReference(src.D);
        ref float ob = ref MemoryMarshal.GetArrayDataReference(o);
        var rowBase = new int[n]; var pa = new int[n]; var pb = new int[n];
        var W = Vector128.Create(weight);
        // columns whose taps need no clamping: x − hw − 1 ≥ xlo and x + hw ≤ xhi for every row of the disc (hw ≤ r)
        int xi0 = Math.Max(1, xlo + r + 1), xi1 = Math.Min(w, xhi - r + 1);
        for (int y = ya; y < yb; y++)
        {
            for (int j = 0; j < n; j++)
            {
                rowBase[j] = src.Off + Clamp(y - r + j, src.Y0, src.Y1 - 1) * rowStride;
                pa[j] = rowBase[j] + hw[j] * 4; pb[j] = rowBase[j] - (hw[j] + 1) * 4;
            }
            var sum = Vector128<float>.Zero;
            for (int j = 0; j < n; j++)
                for (int xx = -hw[j]; xx <= hw[j]; xx++) sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Clamp(xx, xlo, xhi) * 4));
            int orow = oOff + y * w * 4;
            (W * sum).StoreUnsafe(ref ob, (nuint)orow);
            int x = 1;
            int e0 = Math.Min(xi0, w);
            for (; x < e0; x++)
            {
                for (int j = 0; j < n; j++)
                    sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Clamp(x + hw[j], xlo, xhi) * 4)) - Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Clamp(x - hw[j] - 1, xlo, xhi) * 4));
                (W * sum).StoreUnsafe(ref ob, (nuint)(orow + x * 4));
            }
            for (; x < xi1; x++)
            {
                int x4 = x * 4;
                for (int j = 0; j < n; j++)
                    sum += Vector128.LoadUnsafe(ref sb, (nuint)(pa[j] + x4)) - Vector128.LoadUnsafe(ref sb, (nuint)(pb[j] + x4));
                (W * sum).StoreUnsafe(ref ob, (nuint)(orow + x4));
            }
            for (; x < w; x++)
            {
                for (int j = 0; j < n; j++)
                    sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Clamp(x + hw[j], xlo, xhi) * 4)) - Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Clamp(x - hw[j] - 1, xlo, xhi) * 4));
                (W * sum).StoreUnsafe(ref ob, (nuint)(orow + x * 4));
            }
        }
    }

    // ---- ImageConvSeparable2D<5,5,T,float> ----

    /// <summary>`ImageConvSeparable2D&lt;5,5,vec4x32f,float&gt;(dst, src, kx, ky)` (vertical row pass, then horizontal pass).
    /// Both kernels are applied reversed (v[i] = k[4−i]). Per output row a temporary row is built over
    /// x ∈ [max(rect.x0, −2), min(rect.x1, w+2)) from source rows y−2…y+2 (clamped to the rect near its top/bottom), then filtered horizontally with
    /// taps clamped to that range. The grouping of the five products differs between the clamped and the direct code paths and is reproduced here.
    /// One lane = the float instantiation `ImageConvSeparable2D&lt;5,5,float,float&gt;`: identical except that its direct vertical pass
    /// is a 4-wide loop over each 32-pixel chunk that uses the
    /// clamped rows' grouping, with only the chunk's last (n &amp; 3) pixels on the scalar grouping.</summary>
    public static DofImage Conv5(DofImage src, float[] kx, float[] ky, float[]? dstBuffer = null)
    {
        int c = src.C, w = src.W, h = src.H;
        var dst = dstBuffer is null ? new DofImage(w, h, c) : new DofImage(dstBuffer, c, w, h); var o = dst.D;
        Rows(h, (long)w * 10, (ya, yb) =>
        {
            var tmp = ScratchPool.Rent<float>((Math.Min(src.X1, w + 2) - Math.Max(src.X0, -2)) * c);
            for (int y = ya; y < yb; y++) Conv5Row(src, kx, ky, y, tmp, o, y * w * c);
            ScratchPool.Return(tmp);
        });
        return dst;
    }

    /// <summary><see cref="Conv5"/> of a four-lane image composited straight into <paramref name="acc"/>: per pixel
    /// acc = (1 − v.alpha)·acc + v on all four lanes, in single precision — the blurred band image never stored.</summary>
    public static void Conv5Composite(DofImage src, float[] kx, float[] ky, float[] acc)
    {
        int w = src.W, h = src.H;
        if (src.C != 4) throw new ArgumentException("Conv5Composite needs four lanes");
        Rows(h, (long)w * 12, (ya, yb) =>
        {
            var tmp = ScratchPool.Rent<float>((Math.Min(src.X1, w + 2) - Math.Max(src.X0, -2)) * 4);
            var row = ScratchPool.Rent<float>(w * 4);
            for (int y = ya; y < yb; y++) { Conv5Row(src, kx, ky, y, tmp, row, 0); Composite(acc, y * w * 4, row, 0, w * 4); }
            ScratchPool.Return(tmp); ScratchPool.Return(row);
        });
    }

    /// <summary>acc[a + i] = (1 − b[pixel alpha])·acc[a + i] + b[bi + i] for the four lanes of each pixel in [0, n) floats.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void Composite(float[] acc, int a, float[] b, int bi, int n)
    {
        ref float ab = ref MemoryMarshal.GetArrayDataReference(acc);
        ref float bb = ref MemoryMarshal.GetArrayDataReference(b);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var one = Vector256.Create(1f); var alpha = Vector256.Create(3, 3, 3, 3, 7, 7, 7, 7);
            for (; i + 8 <= n; i += 8)
            {
                var B = Vector256.LoadUnsafe(ref bb, (nuint)(bi + i));
                var t = one - Vector256.Shuffle(B, alpha);
                (t * Vector256.LoadUnsafe(ref ab, (nuint)(a + i)) + B).StoreUnsafe(ref ab, (nuint)(a + i));
            }
        }
        for (; i < n; i += 4)
        {
            var B = Vector128.LoadUnsafe(ref bb, (nuint)(bi + i));
            var t = Vector128.Create(1f - b[bi + i + 3]);
            (t * Vector128.LoadUnsafe(ref ab, (nuint)(a + i)) + B).StoreUnsafe(ref ab, (nuint)(a + i));
        }
    }

    /// <summary>The composite of an all-+0 band: acc = (1 − 0)·acc + 0, i.e. 1·acc + (+0) per lane (turns −0 into +0, keeps everything else).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void CompositeZero(float[] acc, int a, int n)
    {
        ref float ab = ref MemoryMarshal.GetArrayDataReference(acc);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var one = Vector256.Create(1f);
            for (; i + 8 <= n; i += 8) (one * Vector256.LoadUnsafe(ref ab, (nuint)(a + i)) + Vector256<float>.Zero).StoreUnsafe(ref ab, (nuint)(a + i));
        }
        for (; i < n; i++) acc[a + i] = 1f * acc[a + i] + 0f;
    }

    /// <summary>One output row of <see cref="Conv5"/> into <paramref name="o"/> at <paramref name="ob"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void Conv5Row(DofImage src, float[] kx, float[] ky, int y, float[] tmp, float[] o, int ob)
    {
        int c = src.C, w = src.W;
        float vx0 = kx[4], vx1 = kx[3], vx2 = kx[2], vx3 = kx[1], vx4 = kx[0], vy0 = ky[4], vy1 = ky[3], vy2 = ky[2], vy3 = ky[1], vy4 = ky[0];
        int xa = Math.Max(src.X0, -2), xb = Math.Min(src.X1, w + 2), tw = xb - xa;
        var s = src.D;
        bool edgeY = y < src.Y0 + 2 || src.Y1 - 2 <= y;
        int b0, b1, b2, b3, b4;
        int rs = src.Stride * c;
        if (edgeY)
        {
            b0 = src.Off + Clamp(y - 2, src.Y0, src.Y1 - 1) * rs; b1 = src.Off + Clamp(y - 1, src.Y0, src.Y1 - 1) * rs; b2 = src.Off + Clamp(y, src.Y0, src.Y1 - 1) * rs;
            b3 = src.Off + Clamp(y + 1, src.Y0, src.Y1 - 1) * rs; b4 = src.Off + Clamp(y + 2, src.Y0, src.Y1 - 1) * rs;
        }
        else { b0 = src.Off + (y - 2) * rs; b1 = b0 + rs; b2 = b1 + rs; b3 = b2 + rs; b4 = b3 + rs; }
        // vertical: tmp[(x − xa)·c + k] from the five rows at float offset x·c + k
        int f0 = xa * c;
        if (edgeY || c != 1) VerticalSpan(s, b0 + f0, b1 + f0, b2 + f0, b3 + f0, b4 + f0, vy0, vy1, vy2, vy3, vy4, tmp, 0, tw * c, edgeY);
        else
        {
            // float instantiation, direct rows: per 32-px chunk the first (n & ~3) pixels are a 4-wide loop with the clamped rows' grouping
            for (int start = 0; start < tw; start += 32)
            {
                int nn = Math.Min(32, tw - start), split = start + (nn & ~3);
                VerticalSpan(s, b0 + f0 + start, b1 + f0 + start, b2 + f0 + start, b3 + f0 + start, b4 + f0 + start, vy0, vy1, vy2, vy3, vy4, tmp, start, split - start, true);
                VerticalSpan(s, b0 + f0 + split, b1 + f0 + split, b2 + f0 + split, b3 + f0 + split, b4 + f0 + split, vy0, vy1, vy2, vy3, vy4, tmp, split, start + nn - split, false);
            }
        }
        // horizontal: clamped taps at both ends, the direct grouping in between
        int d0 = Math.Min(w, Math.Max(0, xa + 2)), d1 = Math.Max(d0, Math.Min(w, xb - 2));
        for (int x = 0; x < d0; x++) HClamped(tmp, x, xa, xb, c, vx0, vx1, vx2, vx3, vx4, o, ob);
        HDirect(tmp, (d0 - 2 - xa) * c, (d1 - d0) * c, c, vx0, vx1, vx2, vx3, vx4, o, ob + d0 * c);
        for (int x = d1; x < w; x++) HClamped(tmp, x, xa, xb, c, vx0, vx1, vx2, vx3, vx4, o, ob);
    }

    /// <summary>tmp[t + i] = grouping(v0·s[a0 + i] … v4·s[a4 + i]) for i in [0, n): pairs = ((p0 + p1) + (p2 + p3)) + p4, else ((p0 + p1) + p4) + (p2 + p3).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void VerticalSpan(float[] s, int a0, int a1, int a2, int a3, int a4, float v0, float v1, float v2, float v3, float v4, float[] tmp, int t, int n, bool pairs)
    {
        if (n <= 0) return;
        ref float sb = ref MemoryMarshal.GetArrayDataReference(s);
        ref float tb = ref MemoryMarshal.GetArrayDataReference(tmp);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 8)
        {
            var V0 = Vector256.Create(v0); var V1 = Vector256.Create(v1); var V2 = Vector256.Create(v2); var V3 = Vector256.Create(v3); var V4 = Vector256.Create(v4);
            for (; i + 8 <= n; i += 8)
            {
                var p0 = V0 * Vector256.LoadUnsafe(ref sb, (nuint)(a0 + i)); var p1 = V1 * Vector256.LoadUnsafe(ref sb, (nuint)(a1 + i));
                var p2 = V2 * Vector256.LoadUnsafe(ref sb, (nuint)(a2 + i)); var p3 = V3 * Vector256.LoadUnsafe(ref sb, (nuint)(a3 + i));
                var p4 = V4 * Vector256.LoadUnsafe(ref sb, (nuint)(a4 + i));
                (pairs ? ((p0 + p1) + (p2 + p3)) + p4 : ((p0 + p1) + p4) + (p2 + p3)).StoreUnsafe(ref tb, (nuint)(t + i));
            }
        }
        for (; i < n; i++)
        {
            float p0 = v0 * s[a0 + i], p1 = v1 * s[a1 + i], p2 = v2 * s[a2 + i], p3 = v3 * s[a3 + i], p4 = v4 * s[a4 + i];
            tmp[t + i] = pairs ? ((p0 + p1) + (p2 + p3)) + p4 : ((p0 + p1) + p4) + (p2 + p3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void HClamped(float[] tmp, int x, int xa, int xb, int c, float vx0, float vx1, float vx2, float vx3, float vx4, float[] o, int ob)
    {
        int t0 = (Clamp(x - 2, xa, xb - 1) - xa) * c, t1 = (Clamp(x - 1, xa, xb - 1) - xa) * c, t2 = (Clamp(x, xa, xb - 1) - xa) * c, t3 = (Clamp(x + 1, xa, xb - 1) - xa) * c, t4 = (Clamp(x + 2, xa, xb - 1) - xa) * c;
        for (int k = 0; k < c; k++) o[ob + x * c + k] = ((vx0 * tmp[t0 + k] + vx1 * tmp[t1 + k]) + (vx2 * tmp[t2 + k] + vx3 * tmp[t3 + k])) + vx4 * tmp[t4 + k];
    }

    /// <summary>o[ob + i] = ((vx0·t[i] + vx2·t[i + 2c]) + vx4·t[i + 4c]) + (vx1·t[i + c] + vx3·t[i + 3c]) for i in [0, n) (t = tmp from <paramref name="t"/>).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void HDirect(float[] tmp, int t, int n, int c, float vx0, float vx1, float vx2, float vx3, float vx4, float[] o, int ob)
    {
        if (n <= 0) return;
        ref float tb = ref MemoryMarshal.GetArrayDataReference(tmp);
        ref float obr = ref MemoryMarshal.GetArrayDataReference(o);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 8)
        {
            var V0 = Vector256.Create(vx0); var V1 = Vector256.Create(vx1); var V2 = Vector256.Create(vx2); var V3 = Vector256.Create(vx3); var V4 = Vector256.Create(vx4);
            for (; i + 8 <= n; i += 8)
            {
                int q = t + i;
                var a = V0 * Vector256.LoadUnsafe(ref tb, (nuint)q) + V2 * Vector256.LoadUnsafe(ref tb, (nuint)(q + 2 * c));
                var b = V1 * Vector256.LoadUnsafe(ref tb, (nuint)(q + c)) + V3 * Vector256.LoadUnsafe(ref tb, (nuint)(q + 3 * c));
                ((a + V4 * Vector256.LoadUnsafe(ref tb, (nuint)(q + 4 * c))) + b).StoreUnsafe(ref obr, (nuint)(ob + i));
            }
        }
        for (; i < n; i++)
        {
            int q = t + i;
            o[ob + i] = ((vx0 * tmp[q] + vx2 * tmp[q + 2 * c]) + vx4 * tmp[q + 4 * c]) + (vx1 * tmp[q + c] + vx3 * tmp[q + 3 * c]);
        }
    }

    // ---- ImageConvSeparable2D<T> (generic kernel vectors) ----

    /// <summary>`ImageConvSeparable2D&lt;T&gt;(dst, src, kx, ky)` (vec4x32f and float) on a plain image of
    /// <paramref name="c"/> lanes: clamp-to-edge, reversed kernel vectors, vertical pass then horizontal, sequential acc = k·s + acc from tap 0
    /// (same algorithm as <c>ExportResample.ConvSeparable</c>). <paramref name="dstBuffer"/>: an optional array (at least w·h·c) to write into.
    /// Rows for which <paramref name="skipRow"/> says the caller does not need the result are left unwritten.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static float[] ConvSeparable(float[] src, int w, int h, int c, float[] kx, float[] ky, float[]? dstBuffer = null, Func<int, bool>? skipRow = null)
    {
        int nx = kx.Length, ny = ky.Length, hx = (nx - 1) / 2, hy = (ny - 1) / 2;
        var rkx = new float[nx]; for (int i = 0; i < nx; i++) rkx[i] = kx[nx - 1 - i];
        var rky = new float[ny]; for (int i = 0; i < ny; i++) rky[i] = ky[ny - 1 - i];
        var outp = dstBuffer ?? new float[w * h * c];
        int rowLen = w * c;
        // columns whose taps need no clamping
        int xi0 = Math.Min(w, hx), xi1 = Math.Max(xi0, w - (nx - 1 - hx));
        Rows(h, (long)w * c * (nx + ny), (ya, yb) =>
        {
            var tmp = ScratchPool.Rent<float>(rowLen);
            ref float sb = ref MemoryMarshal.GetArrayDataReference(src);
            ref float tb = ref MemoryMarshal.GetArrayDataReference(tmp);
            ref float ob = ref MemoryMarshal.GetArrayDataReference(outp);
            for (int y = ya; y < yb; y++)
            {
                if (skipRow is not null && skipRow(y)) continue;
                for (int k = 0; k < ny; k++)
                {
                    int sy = Math.Clamp(y - hy + k, 0, h - 1); float kv = rky[k]; int so = sy * rowLen;
                    int i = 0;
                    if (Vector256.IsHardwareAccelerated)
                    {
                        var K = Vector256.Create(kv);
                        if (k == 0) for (; i + 8 <= rowLen; i += 8) (Vector256.LoadUnsafe(ref sb, (nuint)(so + i)) * K).StoreUnsafe(ref tb, (nuint)i);
                        else for (; i + 8 <= rowLen; i += 8) (Vector256.LoadUnsafe(ref sb, (nuint)(so + i)) * K + Vector256.LoadUnsafe(ref tb, (nuint)i)).StoreUnsafe(ref tb, (nuint)i);
                    }
                    if (k == 0) for (; i < rowLen; i++) tmp[i] = src[so + i] * kv;
                    else for (; i < rowLen; i++) tmp[i] = src[so + i] * kv + tmp[i];
                }
                int orow = y * rowLen;
                for (int x = 0; x < xi0; x++) HSeq(tmp, x, w, c, hx, nx, rkx, outp, orow);
                // interior: every float of [xi0·c, xi1·c) gets acc = t[i + (k − hx)·c]·rk[k] (+ acc) for k = 0…nx−1
                {
                    int i = xi0 * c, end = xi1 * c;
                    if (Vector256.IsHardwareAccelerated)
                        for (; i + 8 <= end; i += 8)
                        {
                            var acc = Vector256.LoadUnsafe(ref tb, (nuint)(i - hx * c)) * Vector256.Create(rkx[0]);
                            for (int k = 1; k < nx; k++) acc = Vector256.LoadUnsafe(ref tb, (nuint)(i + (k - hx) * c)) * Vector256.Create(rkx[k]) + acc;
                            acc.StoreUnsafe(ref ob, (nuint)(orow + i));
                        }
                    for (; i < end; i++)
                    {
                        float acc = 0f;
                        for (int k = 0; k < nx; k++) { float v = tmp[i + (k - hx) * c] * rkx[k]; acc = k == 0 ? v : v + acc; }
                        outp[orow + i] = acc;
                    }
                }
                for (int x = xi1; x < w; x++) HSeq(tmp, x, w, c, hx, nx, rkx, outp, orow);
            }
            ScratchPool.Return(tmp);
        });
        return outp;
    }

    static void HSeq(float[] tmp, int x, int w, int c, int hx, int nx, float[] rkx, float[] outp, int orow)
    {
        for (int l = 0; l < c; l++)
        {
            float acc = 0f;
            for (int k = 0; k < nx; k++) { int sx = Math.Clamp(x - hx + k, 0, w - 1); float v = tmp[sx * c + l] * rkx[k]; acc = k == 0 ? v : v + acc; }
            outp[orow + x * c + l] = acc;
        }
    }
}
