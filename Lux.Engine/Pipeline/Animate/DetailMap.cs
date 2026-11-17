using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Animate;

/// <summary>
/// Where a full-size picture holds detail that only the telephoto lenses can give. The picture of export level 1 is made from the wide
/// lenses alone. The map enlarges it to the full size and compares the finest band of the two pictures on the same edges, block by block.
/// Where telephoto pixels were merged, the full-size picture carries more of that band. The grain of each picture is measured on the flat
/// part of each block and taken out first, so grain does not count as detail. The result is the excess over the level that a picture of
/// the wide lenses alone reaches, in dB. Blocks without edges report 0.
/// </summary>
public sealed class DetailMap
{
    /// <summary>Block size, full-size pixels.</summary>
    public const int Block = 64;
    /// <summary>The fine band of a full-size picture without telephoto pixels, relative to the enlarged level-1 picture (dB). The full-size
    /// render sharpens at its own scale, so this is above 0.</summary>
    public const double WideLevelDb = 1.9;
    /// <summary>A block needs this many edge pixels to be measured.</summary>
    public const int MinEdgePixels = 48;
    /// <summary>A block needs this many flat pixels to measure its own grain; else the picture's typical grain is used.</summary>
    const int MinFlatPixels = 1024;

    public int Bw { get; }
    public int Bh { get; }
    /// <summary>Telephoto detail per block, dB, 0–10. 0 on blocks without edges.</summary>
    public float[] Gain { get; }
    /// <summary>How much there is to look at per block, 0–1: fine texture and local contrast.</summary>
    public float[] Saliency { get; }

    readonly double[] _full, _wide;   // per block: the fine-band energy on its edge pixels, grain taken out, of the two pictures

    DetailMap(int bw, int bh, float[] gain, float[] sal, double[] full, double[] wide) { Bw = bw; Bh = bh; Gain = gain; Saliency = sal; _full = full; _wide = wide; }

    static float[] Kernel(double sigma)
    {
        int r = (int)Math.Ceiling(3 * sigma);
        var k = new float[2 * r + 1]; double sum = 0;
        for (int i = -r; i <= r; i++) { double v = WineCrt.Exp(-0.5 * i * i / (sigma * sigma)); k[i + r] = (float)v; sum += v; }
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    static readonly float[] K05 = Kernel(0.5), K12 = Kernel(1.2), K15 = Kernel(1.5), K40 = Kernel(4.0);
    const int Halo = 12;

    /// <summary>The luma of rows y0 − halo … y1 + halo of a picture, with the edge columns and rows repeated outside it.</summary>
    static float[] LumaOf(Rgba img, int y0, int rows)
    {
        int w = img.W, h = img.H, ext = rows + 2 * Halo, wp = w + 2 * Halo;
        var luma = new float[(long)ext * wp];
        for (int r = 0; r < ext; r++)
        {
            long p = (long)Math.Clamp(y0 - Halo + r, 0, h - 1) * w * 4, o = (long)r * wp + Halo;
            for (int x = 0; x < w; x++, p += 4) luma[o + x] = 0.299f * img.P[p] + 0.587f * img.P[p + 1] + 0.114f * img.P[p + 2];
            for (int x = 1; x <= Halo; x++) { luma[o - x] = luma[o]; luma[o + w - 1 + x] = luma[o + w - 1]; }
        }
        return luma;
    }

    /// <summary>The same rows of the smaller picture enlarged to <paramref name="w"/> × <paramref name="h"/> (bicubic).</summary>
    static float[] EnlargedLumaOf(Rgba small, int w, int h, int y0, int rows)
    {
        int ext = rows + 2 * Halo, wp = w + 2 * Halo, sw = small.W, sh = small.H;
        double kx = (double)sw / w, ky = (double)sh / h;
        // the rows of the smaller picture that the band reads, as luma
        int ya = (int)Math.Floor((Math.Max(y0 - Halo, 0) + 0.5) * ky - 0.5) - 1, yb = (int)Math.Floor((Math.Min(y0 + rows + Halo, h) - 0.5) * ky - 0.5) + 2;
        ya = Math.Clamp(ya, 0, sh - 1); yb = Math.Clamp(yb, ya, sh - 1);
        var src = new float[(long)(yb - ya + 1) * sw];
        for (int y = ya; y <= yb; y++)
        {
            long p = (long)y * sw * 4, o = (long)(y - ya) * sw;
            for (int x = 0; x < sw; x++, p += 4) src[o + x] = 0.299f * small.P[p] + 0.587f * small.P[p + 1] + 0.114f * small.P[p + 2];
        }
        static void Weights(float t, Span<float> wt)
        {
            float t2 = t * t, t3 = t2 * t;
            wt[0] = -0.5f * t3 + t2 - 0.5f * t; wt[1] = 1.5f * t3 - 2.5f * t2 + 1f; wt[2] = -1.5f * t3 + 2f * t2 + 0.5f * t; wt[3] = 0.5f * t3 - 0.5f * t2;
        }
        var ix = new int[w]; var wx = new float[w * 4];
        for (int x = 0; x < w; x++)
        {
            double u = (x + 0.5) * kx - 0.5, f = Math.Floor(u);
            ix[x] = (int)f; Weights((float)(u - f), wx.AsSpan(x * 4, 4));
        }
        var luma = new float[(long)ext * wp];
        Span<float> wy = stackalloc float[4];
        for (int r = 0; r < ext; r++)
        {
            int y = Math.Clamp(y0 - Halo + r, 0, h - 1);
            double v = (y + 0.5) * ky - 0.5, fy = Math.Floor(v);
            int iy = (int)fy; Weights((float)(v - fy), wy);
            long o = (long)r * wp + Halo;
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int j = 0; j < 4; j++)
                {
                    long row = (long)(Math.Clamp(iy - 1 + j, ya, yb) - ya) * sw;
                    float b = 0;
                    for (int i = 0; i < 4; i++) b += wx[x * 4 + i] * src[row + Math.Clamp(ix[x] - 1 + i, 0, sw - 1)];
                    a += wy[j] * b;
                }
                luma[o + x] = a;
            }
            for (int x = 1; x <= Halo; x++) { luma[o - x] = luma[o]; luma[o + w - 1 + x] = luma[o + w - 1]; }
        }
        return luma;
    }

    /// <summary>The blur of the middle rows of a haloed luma band with <paramref name="k"/>.</summary>
    static float[] Blur(float[] luma, int w, int rows, float[] k, float[] tmp)
    {
        int rk = k.Length / 2, ext = rows + 2 * Halo, wp = w + 2 * Halo;
        for (int r = 0; r < ext; r++)
        {
            long o = (long)r * w, q = (long)r * wp + Halo;
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -rk; i <= rk; i++) a += k[i + rk] * luma[q + x + i];
                tmp[o + x] = a;
            }
        }
        var res = new float[(long)rows * w];
        for (int r = 0; r < rows; r++)
        {
            long o = (long)r * w;
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -rk; i <= rk; i++) a += k[i + rk] * tmp[(long)(r + Halo + i) * w + x];
                res[o + x] = a;
            }
        }
        return res;
    }

    static float[] Band(float[] luma, int w, int rows, float[] fine, float[] coarse)
    {
        var tmp = new float[(long)(rows + 2 * Halo) * w];
        var a = Blur(luma, w, rows, fine, tmp); var b = Blur(luma, w, rows, coarse, tmp);
        for (long i = 0; i < a.LongLength; i++) a[i] -= b[i];
        return a;
    }

    /// <summary>
    /// Measure a full-size picture against the picture of export level 1 of the same view. Works in bands of rows,
    /// <paramref name="threads"/> at a time.
    /// </summary>
    /// <param name="full">The full-size picture.</param>
    /// <param name="wide">The level-1 picture: the same view at about half the size.</param>
    public static DetailMap Compute(Rgba full, Rgba wide, int threads = 0, CancellationToken cancel = default)
    {
        int w = full.W, h = full.H, bw = w / Block, bh = h / Block;
        if (bw < 1 || bh < 1) throw new ArgumentException("the picture is smaller than one block");
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount, CancellationToken = cancel };

        // the edge threshold: 4 × the robust noise level of the coarse band of the enlarged picture, from every eighth band of blocks
        var samples = new List<float>[bh];
        Parallel.For(0, bh, po, by =>
        {
            if (by % 8 != 0) return;
            var mid = Band(EnlargedLumaOf(wide, w, h, by * Block, Block), w, Block, K15, K40);
            var list = new List<float>();
            for (int y = 0; y < Block; y += 7)
                for (int x = 0; x < w; x += 7) list.Add(Math.Abs(mid[(long)y * w + x]));
            samples[by] = list;
        });
        var all = samples.Where(l => l is not null).SelectMany(l => l).ToArray();
        Array.Sort(all);
        float thr = Math.Max(4 * 1.4826f * (all.Length == 0 ? 0 : all[all.Length / 2]), 1.0f);

        int nb = bw * bh;
        var count = new int[nb];
        double[] edgeFull = new double[nb], edgeWide = new double[nb], allFull = new double[nb], allWide = new double[nb], std = new double[nb];
        Parallel.For(0, bh, po, by =>
        {
            var lumaFull = LumaOf(full, by * Block, Block);
            var lumaWide = EnlargedLumaOf(wide, w, h, by * Block, Block);
            var hiFull = Band(lumaFull, w, Block, K05, K12);
            var hiWide = Band(lumaWide, w, Block, K05, K12);
            var mid = Band(lumaWide, w, Block, K15, K40);
            int wp = w + 2 * Halo;
            for (int bx = 0; bx < bw; bx++)
            {
                int n = 0; double ef = 0, ew = 0, tf = 0, tw = 0, sy = 0, syy = 0;
                for (int y = 0; y < Block; y++)
                {
                    long o = (long)y * w + bx * Block, q = (long)(y + Halo) * wp + Halo + bx * Block;
                    for (int x = 0; x < Block; x++)
                    {
                        float a = hiFull[o + x], b = hiWide[o + x], l = lumaFull[q + x];
                        tf += a * a; tw += b * b; sy += l; syy += l * l;
                        if (Math.Abs(mid[o + x]) >= thr) { n++; ef += a * a; ew += b * b; }
                    }
                }
                int i = by * bw + bx;
                double np = Block * Block, mean = sy / np;
                count[i] = n; edgeFull[i] = ef; edgeWide[i] = ew; allFull[i] = tf; allWide[i] = tw;
                std[i] = Math.Sqrt(Math.Max(syy / np - mean * mean, 0));
            }
        });

        // the grain of each picture per block: the fine-band energy per flat pixel; the picture's median where a block has too few
        double[] Grain(double[] total, double[] edge)
        {
            var g = new double[nb]; var known = new List<double>();
            for (int i = 0; i < nb; i++)
            {
                int flat = Block * Block - count[i];
                g[i] = flat >= MinFlatPixels ? (total[i] - edge[i]) / flat : double.NaN;
                if (!double.IsNaN(g[i])) known.Add(g[i]);
            }
            known.Sort();
            double med = known.Count == 0 ? 0 : known[known.Count / 2];
            for (int i = 0; i < nb; i++) if (double.IsNaN(g[i])) g[i] = med;
            return g;
        }
        var grainFull = Grain(allFull, edgeFull); var grainWide = Grain(allWide, edgeWide);
        var gain = new float[nb]; var cleanFull = new double[nb]; var cleanWide = new double[nb];
        for (int i = 0; i < nb; i++)
        {
            cleanFull[i] = Math.Max(edgeFull[i] - count[i] * grainFull[i], 0);
            cleanWide[i] = Math.Max(edgeWide[i] - count[i] * grainWide[i], 0);
            if (count[i] >= MinEdgePixels && cleanWide[i] > 0)
                gain[i] = (float)Math.Clamp(10 * WineCrt.Log10((cleanFull[i] + 1e-3) / (cleanWide[i] + 1e-3)) - WideLevelDb, 0, 10);
        }

        // saliency: fine texture (log energy between its 5th and 98th percentile) and local contrast (against its 98th percentile)
        var tex = allFull.Select(e => WineCrt.Log10(e / (Block * Block) + 0.05)).ToArray();
        double t5 = Percentile(tex, 0.05), t98 = Percentile(tex, 0.98), c98 = Percentile(std, 0.98);
        var sal = new float[nb];
        for (int i = 0; i < nb; i++)
            sal[i] = (float)(0.6 * Math.Clamp((tex[i] - t5) / (t98 - t5 + 1e-6), 0, 1) + 0.4 * Math.Clamp(std[i] / (c98 + 1e-6), 0, 1));
        return new DetailMap(bw, bh, gain, sal, cleanFull, cleanWide);
    }

    static double Percentile(double[] v, double p)
    {
        var s = (double[])v.Clone(); Array.Sort(s);
        return s[Math.Clamp((int)(p * (s.Length - 1)), 0, s.Length - 1)];
    }

    (int X0, int Y0, int X1, int Y1) Blocks(double x0, double y0, double w, double h)
    {
        int bx0 = Math.Clamp((int)Math.Floor(x0 / Block), 0, Bw - 1), by0 = Math.Clamp((int)Math.Floor(y0 / Block), 0, Bh - 1);
        return (bx0, by0, Math.Clamp((int)Math.Ceiling((x0 + w) / Block), bx0 + 1, Bw), Math.Clamp((int)Math.Ceiling((y0 + h) / Block), by0 + 1, Bh));
    }

    /// <summary>The telephoto detail (dB, 0–10) of the window starting at (<paramref name="x0"/>, <paramref name="y0"/>), full-size pixels,
    /// as a whole: the fine band of the two pictures over all its edge pixels. 0 when the window has no edges.</summary>
    public double PooledGain(double x0, double y0, double w, double h)
    {
        var (bx0, by0, bx1, by1) = Blocks(x0, y0, w, h);
        double f = 0, c = 0;
        for (int y = by0; y < by1; y++) for (int x = bx0; x < bx1; x++) { f += _full[y * Bw + x]; c += _wide[y * Bw + x]; }
        if (!(c > 0)) return 0;
        return Math.Clamp(10 * WineCrt.Log10((f + 1e-3) / (c + 1e-3)) - WideLevelDb, 0, 10);
    }

    /// <summary>The best target for a zoom that ends on a window of <paramref name="winW"/> × <paramref name="winH"/> full-size pixels: the
    /// window with the highest mean of detail × (0.3 + 0.7 × saliency), where blocks without edges count as 0. Returns the middle of that
    /// window, or null when the picture is smaller than the window.</summary>
    public (double X, double Y)? ChooseTarget(double winW, double winH)
    {
        int ww = Math.Max(1, (int)Math.Round(winW / Block)), wh = Math.Max(1, (int)Math.Round(winH / Block));
        if (ww > Bw || wh > Bh) return null;
        // summed-area table of the score
        var ii = new double[(Bw + 1) * (Bh + 1)];
        for (int y = 0; y < Bh; y++)
        {
            double row = 0;
            for (int x = 0; x < Bw; x++)
            {
                row += Gain[y * Bw + x] / 10.0 * (0.3 + 0.7 * Saliency[y * Bw + x]);
                ii[(y + 1) * (Bw + 1) + x + 1] = ii[y * (Bw + 1) + x + 1] + row;
            }
        }
        double best = double.NegativeInfinity; int bxBest = 0, byBest = 0;
        for (int y = 0; y + wh <= Bh; y++)
            for (int x = 0; x + ww <= Bw; x++)
            {
                double s = ii[(y + wh) * (Bw + 1) + x + ww] - ii[y * (Bw + 1) + x + ww] - ii[(y + wh) * (Bw + 1) + x] + ii[y * (Bw + 1) + x];
                if (s > best) { best = s; bxBest = x; byBest = y; }
            }
        return ((bxBest + ww / 2.0) * Block, (byBest + wh / 2.0) * Block);
    }
}
