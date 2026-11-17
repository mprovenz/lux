namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>
/// The image operations of the depth refinement on single float planes. Each output value is computed from the inputs alone, in a fixed
/// order, so the result does not depend on the number of threads. Exponentials come from <see cref="WineCrt"/>, so the result does not
/// depend on the operating system either.
/// </summary>
internal static class RefineOps
{
    internal static void Rows(int h, Action<int> row)
    {
        if (h < 32) { for (int y = 0; y < h; y++) row(y); return; }
        Parallel.For(0, h, row);
    }

    static int Reflect(int i, int n)
    {
        if (n == 1) return 0;
        while (i < 0 || i >= n) i = i < 0 ? -i : 2 * n - 2 - i;
        return i;
    }

    // ------------------------------------------------------------------------------------------------ resampling

    /// <summary>Area average to a smaller size: each output pixel is the mean of the source area it covers.</summary>
    public static float[] ResizeArea(float[] src, int sw, int sh, int dw, int dh)
    {
        var (xi, xw) = AreaTaps(sw, dw); var (yi, yw) = AreaTaps(sh, dh);
        var tmp = new float[(long)dw * sh];
        Rows(sh, y =>
        {
            long so = (long)y * sw, to = (long)y * dw;
            for (int x = 0; x < dw; x++)
            {
                double a = 0; int i0 = xi[x]; var w = xw[x];
                for (int k = 0; k < w.Length; k++) a += src[so + i0 + k] * w[k];
                tmp[to + x] = (float)a;
            }
        });
        var dst = new float[(long)dw * dh];
        Rows(dh, y =>
        {
            int i0 = yi[y]; var w = yw[y]; long o = (long)y * dw;
            for (int x = 0; x < dw; x++)
            {
                double a = 0;
                for (int k = 0; k < w.Length; k++) a += tmp[(long)(i0 + k) * dw + x] * w[k];
                dst[o + x] = (float)a;
            }
        });
        return dst;
    }

    static (int[] First, double[][] Weights) AreaTaps(int sn, int dn)
    {
        double scale = (double)sn / dn;
        var first = new int[dn]; var weights = new double[dn][];
        for (int i = 0; i < dn; i++)
        {
            double a = i * scale, b = (i + 1) * scale;
            int i0 = (int)Math.Floor(a), i1 = Math.Min((int)Math.Ceiling(b), sn);
            if (i1 <= i0) i1 = i0 + 1;
            var w = new double[i1 - i0];
            for (int k = i0; k < i1; k++) w[k - i0] = (Math.Min(b, k + 1) - Math.Max(a, k)) / scale;
            first[i] = i0; weights[i] = w;
        }
        return (first, weights);
    }

    /// <summary>Bilinear resampling with pixel centers at half positions and the border pixels repeated.</summary>
    public static float[] ResizeBilinear(float[] src, int sw, int sh, int dw, int dh)
    {
        var (x0, x1, fx) = LinearTaps(sw, dw); var (y0, y1, fy) = LinearTaps(sh, dh);
        var dst = new float[(long)dw * dh];
        Rows(dh, y =>
        {
            long a = (long)y0[y] * sw, b = (long)y1[y] * sw, o = (long)y * dw; float v = fy[y], u = 1f - v;
            for (int x = 0; x < dw; x++)
            {
                float f = fx[x], e = 1f - f; int p = x0[x], q = x1[x];
                dst[o + x] = (src[a + p] * e + src[a + q] * f) * u + (src[b + p] * e + src[b + q] * f) * v;
            }
        });
        return dst;
    }

    static (int[] I0, int[] I1, float[] F) LinearTaps(int sn, int dn)
    {
        double scale = (double)sn / dn;
        var i0 = new int[dn]; var i1 = new int[dn]; var f = new float[dn];
        for (int i = 0; i < dn; i++)
        {
            double s = (i + 0.5) * scale - 0.5; int k = (int)Math.Floor(s); float t = (float)(s - k);
            if (k < 0) { k = 0; t = 0f; }
            if (k >= sn - 1) { k = sn - 1; t = 0f; }
            i0[i] = k; i1[i] = Math.Min(k + 1, sn - 1); f[i] = t;
        }
        return (i0, i1, f);
    }

    /// <summary>Bicubic enlargement (kernel parameter −0.75) with the border values repeated.</summary>
    public static float[] ResizeCubic(float[] src, int sw, int sh, int dw, int dh)
    {
        var (xi, xw) = CubicTaps(sw, dw); var (yi, yw) = CubicTaps(sh, dh);
        var tmp = new float[(long)dw * sh];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < dw; x++)
            {
                double a = 0;
                for (int k = 0; k < 4; k++) a += src[(long)y * sw + Math.Clamp(xi[x] + k, 0, sw - 1)] * xw[x * 4 + k];
                tmp[(long)y * dw + x] = (float)a;
            }
        var dst = new float[(long)dw * dh];
        Rows(dh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                double a = 0;
                for (int k = 0; k < 4; k++) a += tmp[(long)Math.Clamp(yi[y] + k, 0, sh - 1) * dw + x] * yw[y * 4 + k];
                dst[(long)y * dw + x] = (float)a;
            }
        });
        return dst;
    }

    static (int[] First, double[] Weights) CubicTaps(int sn, int dn)
    {
        const double A = -0.75;
        double scale = (double)sn / dn;
        var first = new int[dn]; var w = new double[dn * 4];
        for (int i = 0; i < dn; i++)
        {
            double s = (i + 0.5) * scale - 0.5; int k = (int)Math.Floor(s); double t = s - k;
            first[i] = k - 1;
            w[i * 4] = ((A * (t + 1) - 5 * A) * (t + 1) + 8 * A) * (t + 1) - 4 * A;
            w[i * 4 + 1] = ((A + 2) * t - (A + 3)) * t * t + 1;
            w[i * 4 + 2] = ((A + 2) * (1 - t) - (A + 3)) * (1 - t) * (1 - t) + 1;
            w[i * 4 + 3] = 1.0 - w[i * 4] - w[i * 4 + 1] - w[i * 4 + 2];
        }
        return (first, w);
    }

    /// <summary>The minimum or the maximum of each block of <paramref name="q"/> × <paramref name="q"/> pixels. Pixels beyond the last
    /// whole block are left out.</summary>
    public static float[] Pool(float[] src, int w, int h, int q, bool max)
    {
        int pw = w / q, ph = h / q; var dst = new float[(long)pw * ph];
        Rows(ph, y =>
        {
            for (int x = 0; x < pw; x++)
            {
                float m = src[(long)y * q * w + x * q];
                for (int j = 0; j < q; j++)
                {
                    long o = (long)(y * q + j) * w + x * q;
                    for (int i = 0; i < q; i++) { float v = src[o + i]; if (max ? v > m : v < m) m = v; }
                }
                dst[(long)y * pw + x] = m;
            }
        });
        return dst;
    }

    /// <summary>The mean of each block of <paramref name="q"/> × <paramref name="q"/> pixels.</summary>
    public static float[] BlockMean(float[] src, int w, int h, int q)
    {
        int pw = w / q, ph = h / q; var dst = new float[(long)pw * ph]; double n = q * q;
        Rows(ph, y =>
        {
            for (int x = 0; x < pw; x++)
            {
                double a = 0;
                for (int j = 0; j < q; j++)
                {
                    long o = (long)(y * q + j) * w + x * q;
                    for (int i = 0; i < q; i++) a += src[o + i];
                }
                dst[(long)y * pw + x] = (float)(a / n);
            }
        });
        return dst;
    }

    // ------------------------------------------------------------------------------------------------ filters

    /// <summary>The mean over a square window of side <paramref name="k"/> (odd), with the image mirrored at its borders.</summary>
    public static float[] BoxMean(float[] src, int w, int h, int k)
    {
        int r = k / 2; double n = (double)k * k;
        var tmp = new double[(long)w * h];
        Rows(h, y =>
        {
            long o = (long)y * w; double a = 0;
            for (int i = -r; i <= r; i++) a += src[o + Reflect(i, w)];
            for (int x = 0; x < w; x++)
            {
                tmp[o + x] = a;
                a += src[o + Reflect(x + r + 1, w)] - (double)src[o + Reflect(x - r, w)];
            }
        });
        var dst = new float[(long)w * h];
        // columns: a running sum per column block, each column summed in a fixed order
        Parallel.For(0, (w + 255) / 256, b =>
        {
            int x0 = b * 256, x1 = Math.Min(x0 + 256, w);
            var a = new double[x1 - x0];
            for (int i = -r; i <= r; i++) { long o = (long)Reflect(i, h) * w; for (int x = x0; x < x1; x++) a[x - x0] += tmp[o + x]; }
            for (int y = 0; y < h; y++)
            {
                long o = (long)y * w, oa = (long)Reflect(y + r + 1, h) * w, os = (long)Reflect(y - r, h) * w;
                for (int x = x0; x < x1; x++) { dst[o + x] = (float)(a[x - x0] / n); a[x - x0] += tmp[oa + x] - tmp[os + x]; }
            }
        });
        return dst;
    }

    /// <summary>The Gaussian kernel of radius 4 sigma, normalized to sum 1.</summary>
    public static double[] GaussianKernel(double sigma)
    {
        int r = ((int)Math.Round(sigma * 8 + 1) | 1) / 2;
        var k = new double[2 * r + 1]; double sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = WineCrt.Exp(-(double)(i * i) / (2 * sigma * sigma)); sum += k[i + r]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return k;
    }

    /// <summary>A separable Gaussian blur with the image mirrored at its borders. <paramref name="parallel"/> false keeps the work on the
    /// calling thread (the caller already runs many blurs at once).</summary>
    public static float[] GaussianBlur(float[] src, int w, int h, double[] kernel, bool parallel = true)
    {
        int r = kernel.Length / 2;
        var tmp = new float[(long)w * h]; var dst = new float[(long)w * h];
        void Horizontal(int y)
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                double a = 0;
                if (x >= r && x + r < w) for (int i = -r; i <= r; i++) a += src[o + x + i] * kernel[i + r];
                else for (int i = -r; i <= r; i++) a += src[o + Reflect(x + i, w)] * kernel[i + r];
                tmp[o + x] = (float)a;
            }
        }
        void Vertical(int y)
        {
            long o = (long)y * w;
            Span<long> rows = stackalloc long[kernel.Length];
            for (int i = -r; i <= r; i++) rows[i + r] = (long)Reflect(y + i, h) * w;
            for (int x = 0; x < w; x++)
            {
                double a = 0;
                for (int i = 0; i < rows.Length; i++) a += tmp[rows[i] + x] * kernel[i];
                dst[o + x] = (float)a;
            }
        }
        if (parallel) { Rows(h, Horizontal); Rows(h, Vertical); }
        else { for (int y = 0; y < h; y++) Horizontal(y); for (int y = 0; y < h; y++) Vertical(y); }
        return dst;
    }

    /// <summary>The median of each 3 × 3 neighborhood, with the border values repeated.</summary>
    public static float[] Median3(float[] src, int w, int h)
    {
        var dst = new float[src.Length]; Span<float> v = stackalloc float[9];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int n = 0;
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++) v[n++] = src[Math.Clamp(y + j, 0, h - 1) * w + Math.Clamp(x + i, 0, w - 1)];
                v.Sort();
                dst[y * w + x] = v[4];
            }
        return dst;
    }

    // ------------------------------------------------------------------------------------------------ morphology

    /// <summary>The half widths of a disk of radius <paramref name="r"/>, row by row from −r to r.</summary>
    public static int[] DiskHalfWidths(int r)
    {
        var hw = new int[2 * r + 1];
        double inv = r > 0 ? 1.0 / ((double)r * r) : 0.0;
        for (int dy = -r; dy <= r; dy++) hw[dy + r] = (int)Math.Round(r * Math.Sqrt(((double)r * r - (double)dy * dy) * inv), MidpointRounding.ToEven);
        return hw;
    }

    /// <summary>The minimum (erosion) or the maximum (dilation) over a disk of radius <paramref name="r"/>. Pixels outside the image do
    /// not take part.</summary>
    public static float[] Disk(float[] src, int w, int h, int r, bool max)
    {
        var hw = DiskHalfWidths(r);
        var widths = hw.Distinct().OrderBy(x => x).ToArray();
        var slot = new int[2 * r + 1];
        for (int i = 0; i < hw.Length; i++) slot[i] = Array.IndexOf(widths, hw[i]);
        // the row-wise extremum for each distinct half width, kept for a band of rows at a time
        var dst = new float[(long)w * h];
        int band = Math.Max(64, 4 * r);
        Parallel.For(0, (h + band - 1) / band, b =>
        {
            int y0 = b * band, y1 = Math.Min(y0 + band, h), s0 = Math.Max(y0 - r, 0), s1 = Math.Min(y1 + r, h);
            var rows = new float[widths.Length][];
            for (int k = 0; k < widths.Length; k++) rows[k] = new float[(long)(s1 - s0) * w];
            var g = new float[w]; var hb = new float[w];
            for (int y = s0; y < s1; y++)
                for (int k = 0; k < widths.Length; k++)
                    RowExtremum(src, (long)y * w, w, widths[k], max, rows[k], (long)(y - s0) * w, g, hb);
            for (int y = y0; y < y1; y++)
            {
                long o = (long)y * w; bool first = true;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= h) continue;
                    var row = rows[slot[dy + r]]; long ro = (long)(yy - s0) * w;
                    if (first) { Array.Copy(row, ro, dst, o, w); first = false; continue; }
                    if (max) { for (int x = 0; x < w; x++) { float v = row[ro + x]; if (v > dst[o + x]) dst[o + x] = v; } }
                    else { for (int x = 0; x < w; x++) { float v = row[ro + x]; if (v < dst[o + x]) dst[o + x] = v; } }
                }
            }
        });
        return dst;
    }

    /// <summary>The extremum of one row over the window [x − half, x + half], in linear time (two running extrema per block).</summary>
    static void RowExtremum(float[] src, long so, int w, int half, bool max, float[] dst, long d, float[] g, float[] hb)
    {
        if (half == 0) { Array.Copy(src, so, dst, d, w); return; }
        int k = 2 * half + 1;
        for (int x = 0; x < w; x++)
        {
            float v = src[so + x];
            g[x] = x % k == 0 ? v : Pick(g[x - 1], v, max);
        }
        for (int x = w - 1; x >= 0; x--)
        {
            float v = src[so + x];
            hb[x] = x == w - 1 || (x + 1) % k == 0 ? v : Pick(hb[x + 1], v, max);
        }
        for (int x = 0; x < w; x++)
        {
            int a = x - half, b = x + half;
            if (a < 0) a = 0;
            if (b > w - 1) b = w - 1;
            // [a, b] spans at most two blocks: the suffix extremum from a and the prefix extremum up to b
            if (a / k != b / k) dst[d + x] = Pick(hb[a], g[b], max);
            else if (a % k == 0) dst[d + x] = g[b];
            else if ((b + 1) % k == 0 || b == w - 1) dst[d + x] = hb[a];
            else dst[d + x] = Scan(src, so, a, b, max);
        }
    }

    static float Scan(float[] src, long so, int a, int b, bool max)
    {
        float m = src[so + a];
        for (int i = a + 1; i <= b; i++) m = Pick(m, src[so + i], max);
        return m;
    }

    static float Pick(float a, float b, bool max) => max ? (b > a ? b : a) : (b < a ? b : a);

    /// <summary>The connected groups of the set pixels of a mask. Pixels that touch at a side or at a corner are in one group.</summary>
    /// <returns>The group number of each pixel (0 = not set; the groups are numbered from 1 in the order of their first pixel, row by
    /// row), and the number of pixels of each group (index 0 is not used).</returns>
    public static (int[] Labels, int[] Sizes) Components(bool[] mask, int w, int h)
    {
        var labels = new int[(long)w * h];
        var parent = new List<int> { 0 };
        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        // join the group of a neighbor with the group m (0 = none yet); the lower number stays the root
        int Join(int m, int v)
        {
            if (v == 0) return m;
            int r = Find(v);
            if (m == 0 || r == m) return r;
            if (r < m) { parent[m] = r; return r; }
            parent[r] = m; return m;
        }
        for (int y = 0; y < h; y++)
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                if (!mask[o + x]) continue;
                // the neighbors already visited: left, and the three above
                int l = x > 0 ? labels[o + x - 1] : 0;
                int a = 0, b = 0, c = 0;
                if (y > 0) { a = x > 0 ? labels[o - w + x - 1] : 0; b = labels[o - w + x]; c = x + 1 < w ? labels[o - w + x + 1] : 0; }
                int m = l != 0 ? Find(l) : 0;
                m = Join(m, a); m = Join(m, b); m = Join(m, c);
                if (m == 0) { m = parent.Count; parent.Add(m); }
                labels[o + x] = m;
            }
        }
        // the roots in the order of their first pixel
        var number = new int[parent.Count]; int count = 0;
        for (int i = 1; i < parent.Count; i++) if (parent[i] == i) number[i] = ++count;
        for (int i = 1; i < parent.Count; i++) if (parent[i] != i) number[i] = number[Find(i)];
        var sizes = new int[count + 1];
        for (long i = 0; i < labels.Length; i++) if (labels[i] != 0) { int k = number[labels[i]]; labels[i] = k; sizes[k]++; }
        return (labels, sizes);
    }

    /// <summary>The mask grown by a disk of radius <paramref name="r"/>.</summary>
    public static bool[] Grow(bool[] mask, int w, int h, int r)
    {
        var f = new float[mask.Length];
        for (long i = 0; i < f.Length; i++) if (mask[i]) f[i] = 1f;
        var g = Disk(f, w, h, r, max: true);
        var dst = new bool[mask.Length];
        for (long i = 0; i < f.Length; i++) dst[i] = g[i] > 0f;
        return dst;
    }

    // ------------------------------------------------------------------------------------------------ statistics

    /// <summary>The percentile <paramref name="p"/> (0–100) of sorted values, with linear interpolation between neighbors.</summary>
    public static double PercentileSorted(float[] sorted, int n, double p)
    {
        if (n == 0) return 0;
        double pos = p / 100.0 * (n - 1); int i = (int)Math.Floor(pos); double f = pos - i;
        return i + 1 < n ? sorted[i] + (sorted[i + 1] - (double)sorted[i]) * f : sorted[n - 1];
    }

    /// <summary>The median of the first <paramref name="n"/> values. The values are sorted in place.</summary>
    public static double Median(float[] v, int n)
    {
        if (n == 0) return 0;
        Array.Sort(v, 0, n);
        return (n & 1) == 1 ? v[n / 2] : 0.5 * ((double)v[n / 2 - 1] + v[n / 2]);
    }
}
