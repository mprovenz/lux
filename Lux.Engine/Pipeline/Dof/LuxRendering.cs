using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>The renderer of the depth effect.</summary>
public enum DofRendering
{
    /// <summary>Lumen's renderer, bit for bit.</summary>
    Lumen = 0,
    /// <summary>The Lux renderer: <see cref="LuxRendering"/>.</summary>
    Lux = 1,
}

/// <summary>The parts of the Lux rendering. The defaults are the Lux rendering. Tests change one part at a time.</summary>
public sealed record LuxDofOptions
{
    /// <summary>The highlight expansion. A value that is display white above <see cref="HighlightStart"/> is multiplied by about this
    /// number before the blur. With the start at display white, a value one stop above display white is multiplied by exactly this number.
    /// 1 = no expansion.</summary>
    public float HighlightBoost { get; init; } = DefaultHighlightBoost;
    /// <summary>The value, in display-white units, where the highlight expansion starts. Values up to the start do not change. 1 = display
    /// white.</summary>
    public float HighlightStart { get; init; } = DefaultHighlightStart;
    /// <summary>Remove the purple cast of a clipped highlight before the expansion. Green is the first channel that clips, so a pixel above
    /// the start with green as its smallest channel and a weak color goes toward neutral. Without this, the expansion makes a purple disc
    /// from a light that the eye sees as white. A light with a strong color does not change.</summary>
    public bool HighlightPurpleGuard { get; init; } = DefaultHighlightPurpleGuard;
    /// <summary>Use the anti-aliased disc in place of Lumen's disc and Gaussian.</summary>
    public bool CrispDisc { get; init; } = true;
    /// <summary>Reduce large bands with an area filter in place of Lumen's point-sampled B-spline.</summary>
    public bool AreaDecimation { get; init; } = true;
    /// <summary>Bands with a disc diameter of this value or more are blurred at reduced size. A larger value gives sharper disc edges and
    /// takes more time.</summary>
    public int DownscaleDiameter { get; init; } = 25;
    /// <summary>The scale of the grain in blurred areas. 1 = the amount measured in the picture, 0 = no grain.</summary>
    public float Grain { get; init; } = 1f;

    /// <summary>The defaults of the highlight expansion: the boost, the start and the purple guard.</summary>
    public const float DefaultHighlightBoost = 2.5f, DefaultHighlightStart = 1f; public const bool DefaultHighlightPurpleGuard = true;

    public static readonly LuxDofOptions Default = new();
}

/// <summary>What the Lux rendering of one tile needs: the options, the gains that take a pixel to display white = 1, and the grain model of
/// the level (null = no grain).</summary>
public sealed record LuxDofTile(LuxDofOptions Options, float GainR, float GainG, float GainB, LuxNoiseModel? Noise)
{
    /// <summary>The tile has values above display white, so the output must be compressed after the blur.</summary>
    public bool Expanded { get; init; }

    /// <summary>A tile for synthetic images: display white is 1 on every channel.</summary>
    public static LuxDofTile Unit(LuxDofOptions? options = null, LuxNoiseModel? noise = null) => new(options ?? LuxDofOptions.Default, 1f, 1f, 1f, noise);
}

/// <summary>
/// The Lux rendering of the depth effect. It keeps Lumen's layer model (the radius bands, the band images, the composite and the edge
/// softening) and replaces four parts:
/// <list type="bullet">
/// <item>Highlight expansion. Values above display white are expanded before the blur and compressed with the inverse curve after it, so
/// that a clipped light makes a bright disc. A tile without values above the start of the expansion does not change.</item>
/// <item>Decimation. A large band is reduced with an area filter. The energy of a small light is the same at every sub-pixel position.</item>
/// <item>Kernel. An anti-aliased hard-edged disc replaces the disc and Gaussian.</item>
/// <item>Grain. The grain is measured in the picture and added only where the picture is blurred.</item>
/// </list>
/// All arithmetic is single operations on floats and integers in a fixed order, so the result is the same on every platform.
/// </summary>
public static class LuxRendering
{
    // ---- highlight expansion ----

    /// <summary>The expansion curve on m = the largest channel in display-white units. m ≤ start gives m. Above that, with t = m − start
    /// and s = min(t, 1.5): start + t·(1 + c·s²), c = 2·(boost − 1). With start = 1, the result at m = 2 is 2·boost.</summary>
    public static float Expand(float m, float boost, float start = 1f)
    {
        if (!(m > start)) return m;
        float c = (boost - 1f) * 2f, t = m - start, s = t < 1.5f ? t : 1.5f;
        return start + t * (1f + c * (s * s));
    }

    /// <summary>The inverse of <see cref="Expand(float, float, float)"/>: Newton steps on c·t³ + t = y − start from above, where the steps
    /// only go down.</summary>
    public static float Compress(float y, float boost, float start = 1f)
    {
        if (!(y > start)) return y;
        float c = (boost - 1f) * 2f, u = y - start, lin = 1f + c * 2.25f;
        if (u >= 1.5f * lin) return start + u / lin;
        float t = u < 1.5f ? u : 1.5f;
        for (int i = 0; i < 48; i++)
        {
            float t2 = t * t, f = (c * t2) * t + t - u, d = (3f * c) * t2 + 1f, tn = t - f / d;
            if (!(tn < t)) break;
            t = tn;
        }
        return start + t;
    }

    /// <summary>The weight of the purple guard for a pixel in display-white units (r, g, b; m = the largest channel, above the start).
    /// The weight is the product of three ramps: the green deficit (min(r, b) − g)/m from 0 to 0.1, the color strength g/m from 0.2 to 0.5
    /// (a smaller ratio is a strong color, which stays), and m from the start to 1.5 times the start. So the weight changes without a step.</summary>
    public static float PurpleGuardWeight(float r, float g, float b, float m, float start)
    {
        float lo = r < b ? r : b, d = (lo - g) / m;
        if (!(d > 0f)) return 0f;
        float wd = d * 10f, wq = (g / m - 0.2f) * 3.3333333f, wm = (m - start) / (0.5f * start);
        if (!(wq > 0f) || !(wm > 0f)) return 0f;
        wd = wd < 1f ? wd : 1f; wq = wq < 1f ? wq : 1f; wm = wm < 1f ? wm : 1f;
        return (wd * wq) * wm;
    }

    /// <summary>Expand the first <paramref name="pixels"/> RGBA pixels in place. Each pixel keeps its color: the three channels take the
    /// same factor. With <see cref="LuxDofOptions.HighlightPurpleGuard"/>, a pixel first goes toward the neutral color of its largest
    /// channel by <see cref="PurpleGuardWeight"/>. Returns whether a pixel was above the start.</summary>
    public static bool Expand(float[] rgba, int pixels, LuxDofTile t)
    {
        float boost = t.Options.HighlightBoost, start = t.Options.HighlightStart, gr = t.GainR, gg = t.GainG, gb = t.GainB; bool any = false;
        if (!(boost > 1f)) return false;
        bool guard = t.Options.HighlightPurpleGuard;
        for (int q = 0, e = pixels * 4; q < e; q += 4)
        {
            float a = rgba[q] * gr, b = rgba[q + 1] * gg, c = rgba[q + 2] * gb, m = a > b ? a : b;
            m = m > c ? m : c;
            if (!(m > start)) continue;
            float s = Expand(m, boost, start) / m;
            if (guard)
            {
                float w = PurpleGuardWeight(a, b, c, m, start);
                if (w > 0f)
                {
                    // (the neutral color of the largest channel, in the units of the pixel)
                    float r0 = rgba[q], g0 = rgba[q + 1], b0 = rgba[q + 2];
                    rgba[q] = r0 + w * (m / gr - r0); rgba[q + 1] = g0 + w * (m / gg - g0); rgba[q + 2] = b0 + w * (m / gb - b0);
                }
            }
            rgba[q] *= s; rgba[q + 1] *= s; rgba[q + 2] *= s; any = true;
        }
        return any;
    }

    /// <summary>The inverse of the expansion curve on a blurred image: the three channels of a pixel take the same factor.</summary>
    public static void Compress(DofImage img, LuxDofTile t)
    {
        float boost = t.Options.HighlightBoost, start = t.Options.HighlightStart, gr = t.GainR, gg = t.GainG, gb = t.GainB; var d = img.D; int w = img.W;
        DofFilters.Rows(img.H, w * 2, (ya, yb) =>
        {
            for (int q = ya * w * 4, e = yb * w * 4; q < e; q += 4)
            {
                float a = d[q] * gr, b = d[q + 1] * gg, c = d[q + 2] * gb, m = a > b ? a : b;
                m = m > c ? m : c;
                if (!(m > start)) continue;
                float s = Compress(m, boost, start) / m;
                d[q] *= s; d[q + 1] *= s; d[q + 2] *= s;
            }
        });
    }

    // ---- the anti-aliased disc ----

    /// <summary>A disc kernel. The weight of a pixel is the part of its square inside the circle, from 16 × 16 integer samples. Each row has a
    /// run of full pixels around the center and a list of partial pixels.</summary>
    public sealed class DiscKernel
    {
        /// <summary>The kernel covers offsets −<see cref="R"/> … <see cref="R"/> on both axes.</summary>
        public readonly int R;
        /// <summary>Per row: the half-width of the run of full pixels, −1 when the row has none.</summary>
        public readonly int[] Full;
        /// <summary>Per row: the x offsets and the weights of the other pixels.</summary>
        public readonly int[][] PartX; public readonly float[][] PartW;
        /// <summary>The weight of one full pixel.</summary>
        public readonly float FullW;
        /// <summary>The sum of the squared weights: the part of the variance of white noise that the kernel keeps.</summary>
        public readonly float SumSq;

        public DiscKernel(double radius)
        {
            if (!(radius > 0)) throw new ArgumentException("Invalid blur radius!");
            R = (int)Math.Ceiling(radius); int n = 2 * R + 1;
            double lim = (radius * 32.0) * (radius * 32.0);
            var cov = new int[n * n]; long total = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int c = 0;
                    for (int b = 0; b < 16; b++)
                    {
                        long y = 32L * (j - R) + 2 * b - 15;
                        for (int a = 0; a < 16; a++) { long x = 32L * (i - R) + 2 * a - 15; if ((double)(x * x + y * y) <= lim) c++; }
                    }
                    cov[j * n + i] = c; total += c;
                }
            if (total == 0) { cov[R * n + R] = 256; total = 256; }
            Full = new int[n]; PartX = new int[n][]; PartW = new float[n][];
            FullW = (float)(256.0 / total); double sq = 0;
            for (int j = 0; j < n; j++)
            {
                int f = -1;
                while (f < R && cov[j * n + R + f + 1] == 256 && cov[j * n + R - f - 1] == 256) f++;
                Full[j] = f;
                var xs = new List<int>(); var ws = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    int dx = i - R, c = cov[j * n + i];
                    double wgt = c / (double)total; sq += wgt * wgt;
                    if (c == 0 || Math.Abs(dx) <= f) continue;
                    xs.Add(dx); ws.Add((float)wgt);
                }
                PartX[j] = xs.ToArray(); PartW[j] = ws.ToArray();
            }
            SumSq = (float)sq;
        }
    }

    /// <summary>Rows [ya, yb) of the disc filter of a four-lane image, written at <paramref name="oOff"/> + y·w·4 of <paramref name="o"/>.
    /// Coordinates clamp to the source rect. One running sum per row holds the full pixels. The partial pixels are added for each
    /// output pixel.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void DiscRows4(DofImage src, DiscKernel k, float[] o, int oOff, int ya, int yb)
    {
        int r = k.R, n = 2 * r + 1, w = src.W, xlo = src.X0, xhi = src.X1 - 1, rowStride = src.Stride * 4;
        ref float sb = ref MemoryMarshal.GetArrayDataReference(src.D);
        ref float ob = ref MemoryMarshal.GetArrayDataReference(o);
        var rowBase = new int[n]; var full = k.Full; var fw = Vector128.Create(k.FullW);
        // the partial pixels as one list: row, x offset (in floats), weight
        int np = 0; for (int j = 0; j < n; j++) np += k.PartX[j].Length;
        var pj = new int[np]; var px = new int[np]; var pw = new Vector128<float>[np]; var po = new int[np];
        for (int j = 0, q = 0; j < n; j++) for (int i = 0; i < k.PartX[j].Length; i++, q++) { pj[q] = j; px[q] = k.PartX[j][i]; pw[q] = Vector128.Create(k.PartW[j][i]); }
        // columns whose taps need no clamping
        int xi0 = Math.Max(1, xlo + r + 1), xi1 = Math.Min(w, xhi - r + 1);
        for (int y = ya; y < yb; y++)
        {
            for (int j = 0; j < n; j++) rowBase[j] = src.Off + Math.Clamp(y - r + j, src.Y0, src.Y1 - 1) * rowStride;
            for (int q = 0; q < np; q++) po[q] = rowBase[pj[q]] + px[q] * 4;
            var sum = Vector128<float>.Zero;
            for (int j = 0; j < n; j++)
                for (int xx = -full[j]; xx <= full[j]; xx++) sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Math.Clamp(xx, xlo, xhi) * 4));
            int orow = oOff + y * w * 4;
            for (int x = 0; x < w; x++)
            {
                bool inner = x >= xi0 && x < xi1; int x4 = x * 4;
                if (x > 0)
                {
                    if (inner)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            int f = full[j]; if (f < 0) continue;
                            sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + (x + f) * 4)) - Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + (x - f - 1) * 4));
                        }
                    }
                    else
                    {
                        for (int j = 0; j < n; j++)
                        {
                            int f = full[j]; if (f < 0) continue;
                            sum += Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Math.Clamp(x + f, xlo, xhi) * 4)) - Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[j] + Math.Clamp(x - f - 1, xlo, xhi) * 4));
                        }
                    }
                }
                var acc = fw * sum;
                if (inner) for (int q = 0; q < np; q++) acc += pw[q] * Vector128.LoadUnsafe(ref sb, (nuint)(po[q] + x4));
                else for (int q = 0; q < np; q++) acc += pw[q] * Vector128.LoadUnsafe(ref sb, (nuint)(rowBase[pj[q]] + Math.Clamp(x + px[q], xlo, xhi) * 4));
                acc.StoreUnsafe(ref ob, (nuint)(orow + x4));
            }
        }
    }

    /// <summary>The disc filter of a whole four-lane image.</summary>
    public static DofImage DiscFilter(DofImage src, DiscKernel k, float[]? dstBuffer = null)
    {
        int w = src.W, h = src.H;
        var dst = dstBuffer is null ? new DofImage(w, h, 4) : new DofImage(dstBuffer, 4, w, h); var o = dst.D;
        DofFilters.Rows(h, (long)w * (2 * k.R + 1) * 3, (ya, yb) => DiscRows4(src, k, o, 0, ya, yb));
        return dst;
    }

    // ---- area decimation ----

    /// <summary>
    /// Reduce a four-lane image with an area filter. The geometry is the one of <see cref="DofFilters.Resample4"/>: destination pixel x has
    /// its center at the 16.16 source position <c>(int)(scale·65536)·x + (int)(offset·65536)</c>. Its value is the mean of the source over
    /// one scale step around that center, with a source pixel weighted by the part of it inside the step. The steps of neighboring
    /// destination pixels meet without a gap, so each source pixel has the same total weight. Coordinates clamp to the source rect.
    /// </summary>
    public static DofImage AreaReduce4(DofImage src, int dw, int dh, double offX, double offY, double scX, double scY, float[]? dstBuffer = null)
    {
        int ox = (int)(offX * 65536.0), oy = (int)(offY * 65536.0), sx = (int)(scX * 65536.0), sy = (int)(scY * 65536.0);
        if (src.C != 4 || sx < 65536 || sy < 65536) throw new ArgumentException("AreaReduce4 needs four lanes and a scale of 1 or more");
        var dst = dstBuffer is null ? new DofImage(dw, dh, 4) : new DofImage(dstBuffer, 4, dw, dh); var o = dst.D; var s = src.D;
        var (xs, xn, xw) = Steps(dw, ox, sx, src.X0, src.X1 - 1);
        var (ys, yn, yw) = Steps(dh, oy, sy, src.Y0, src.Y1 - 1);
        // the source rows that a destination row reads
        int r0 = int.MaxValue, r1 = int.MinValue;
        for (int i = 0; i < ys.Length; i++) { if (ys[i] < r0) r0 = ys[i]; if (ys[i] > r1) r1 = ys[i]; }
        int rows = r1 - r0 + 1, rowLen = dw * 4;
        var tmp = ScratchPool.Rent<float>(rows * rowLen);
        int maxX = 0; foreach (int c in xn) if (c > maxX) maxX = c;
        DofFilters.Rows(rows, (long)dw * (maxX + 1) * 2, (ya, yb) =>
        {
            ref float sb = ref MemoryMarshal.GetArrayDataReference(s);
            ref float tb = ref MemoryMarshal.GetArrayDataReference(tmp);
            for (int y = ya; y < yb; y++)
            {
                int rb = src.Off + (y + r0) * src.Stride * 4;
                for (int x = 0, q = 0; x < dw; x++)
                {
                    var acc = Vector128<float>.Zero;
                    for (int e = q + xn[x]; q < e; q++) acc += Vector128.Create(xw[q]) * Vector128.LoadUnsafe(ref sb, (nuint)(rb + xs[q] * 4));
                    acc.StoreUnsafe(ref tb, (nuint)(y * rowLen + x * 4));
                }
            }
        });
        var ystart = new int[dh]; for (int y = 0, q = 0; y < dh; y++) { ystart[y] = q; q += yn[y]; }
        DofFilters.Rows(dh, (long)dw * 16, (ya, yb) =>
        {
            ref float tb = ref MemoryMarshal.GetArrayDataReference(tmp);
            ref float ob = ref MemoryMarshal.GetArrayDataReference(o);
            for (int y = ya; y < yb; y++)
            {
                int q0 = ystart[y], q1 = q0 + yn[y];
                for (int i = 0; i < rowLen; i += 4)
                {
                    var acc = Vector128<float>.Zero;
                    for (int q = q0; q < q1; q++) acc += Vector128.Create(yw[q]) * Vector128.LoadUnsafe(ref tb, (nuint)((ys[q] - r0) * rowLen + i));
                    acc.StoreUnsafe(ref ob, (nuint)(y * rowLen + i));
                }
            }
        });
        ScratchPool.Return(tmp);
        return dst;
    }

    /// <summary>The source pixels (clamped to [lo, hi]) and weights of each destination pixel of <see cref="AreaReduce4"/> on one axis.</summary>
    static (int[] Src, int[] Count, float[] Weight) Steps(int n, int off, int step, int lo, int hi)
    {
        var src = new List<int>(); var wgt = new List<float>(); var count = new int[n];
        float inv = 1f / (float)step;
        for (int x = 0; x < n; x++)
        {
            long a = (long)step * x + off + 32768 - (step >> 1), b = a + step;
            for (long i = a >> 16; (i << 16) < b; i++)
            {
                long l = Math.Max(a, i << 16), u = Math.Min(b, (i + 1) << 16);
                src.Add((int)Math.Clamp(i, lo, hi)); wgt.Add((float)(u - l) * inv); count[x]++;
            }
        }
        return (src.ToArray(), count, wgt.ToArray());
    }

    /// <summary>
    /// The blur of a band at reduced size: the band is reduced by s = threshold / diameter (the same scale rule as Lumen's, with the
    /// threshold of the options), filtered with the disc of radius ((diameter − 1)/2)·s and enlarged with the B-spline resampler.
    /// </summary>
    public static DofImage BlurReduced(DofImage layer, RectI rin, int diameter, LuxDofOptions opt, float[]? dst)
    {
        float s = (float)opt.DownscaleDiameter / (float)diameter;
        float fw = (float)layer.W, fh = (float)layer.H;
        float sx = (fw < 10f ? fw : 10f) / fw, sy = (fh < 10f ? fh : 10f) / fh;
        s = sx > s ? sx : s;
        s = s > sy ? s : sy;
        int sw = (int)((float)(rin.X1 - rin.X0) * s), sh = (int)((float)(rin.Y1 - rin.Y0) * s);
        int ox = (int)((float)rin.X0 * s), oy = (int)((float)rin.Y0 * s);
        float inv = 1f / s;
        double offX = (double)((float)ox * inv - (float)rin.X0), offY = (double)((float)oy * inv - (float)rin.Y0);
        var bSmall = ScratchPool.Rent<float>(sw * sh * 4); var bDisc = ScratchPool.Rent<float>(sw * sh * 4);
        var small = opt.AreaDecimation ? AreaReduce4(layer, sw, sh, offX, offY, (double)inv, (double)inv, bSmall)
                                       : DofFilters.Resample4(layer, sw, sh, offX, offY, (double)inv, (double)inv, bSmall);
        var disc = DiscFilter(small, new DiscKernel((double)((diameter - 1) / 2) * (double)s), bDisc);
        var outp = DofFilters.Resample4(disc, layer.W, layer.H, (double)((float)rin.X0 * s - (float)ox), (double)((float)rin.Y0 * s - (float)oy), (double)s, (double)s, dst);
        ScratchPool.Return(bSmall); ScratchPool.Return(bDisc);
        return outp;
    }

    // ---- grain ----

    /// <summary>The 3-tap kernel that shapes the grain (applied on both axes).</summary>
    const float K0 = 0.58f, K1 = 0.21f;

    /// <summary>A uniform value in [−1, 1) for a pixel of a level and a plane (0 = shared by the channels, 1…3 = one channel).</summary>
    static float White(int x, int y, uint plane)
    {
        uint h = unchecked((uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ plane * 0xC2B2AE3Du);
        h ^= h >> 15; h = unchecked(h * 0x2C1B3C6Du); h ^= h >> 12; h = unchecked(h * 0x297A2D39u); h ^= h >> 15;
        return (float)(int)h * 4.656612873077393e-10f;
    }

    /// <summary>
    /// Add grain to the blurred pixels of a tile. <paramref name="weight"/> gives the grain weight of a pixel from its signed blur radius
    /// (0 where the renderer does not blur). The grain of a pixel is a function of its position in the level
    /// (<paramref name="originX"/>, <paramref name="originY"/> = the level position of the tile's first pixel), so tiles join without a seam
    /// and every run gives the same values. Per channel: out = max(0, out + grain·weight·amplitude(mean of RGB)·n), where n has unit variance
    /// and mixes one field shared by the channels with one field per channel.
    /// </summary>
    public static void AddGrain(DofImage img, float[] radius, Func<float, float> weight, LuxNoiseModel model, float grain, int originX, int originY)
    {
        if (!(grain > 0f)) return;
        int w = img.W, h = img.H; var d = img.D;
        // unit variance: a uniform value has variance 1/3; the 3 × 3 kernel keeps (K0² + 2·K1²)² of it
        float k2 = K0 * K0 + 2f * (K1 * K1), norm = MathF.Sqrt(3f) / k2;
        float luma = model.Luma * norm * grain, chroma = model.Chroma * norm * grain;
        DofFilters.Rows(h, w * 64, (ya, yb) =>
        {
            var wt = new float[w]; var hrow = new float[3][]; for (int i = 0; i < 3; i++) hrow[i] = new float[4 * w];
            var white = new float[w + 2]; var keys = new[] { int.MinValue, int.MinValue, int.MinValue };
            float[] Row(int yy)
            {
                int slot = ((yy % 3) + 3) % 3;
                if (keys[slot] == yy) return hrow[slot];
                var r = hrow[slot];
                for (uint p = 0; p < 4; p++)
                {
                    for (int x = 0; x < w + 2; x++) white[x] = White(originX + x - 1, originY + yy, p);
                    for (int x = 0; x < w; x++) r[x * 4 + (int)p] = (K1 * white[x] + K0 * white[x + 1]) + K1 * white[x + 2];
                }
                keys[slot] = yy; return r;
            }
            for (int y = ya; y < yb; y++)
            {
                bool any = false;
                for (int x = 0; x < w; x++) { float v = weight(radius[y * w + x]); wt[x] = v; any |= v > 0f; }
                if (!any) continue;
                var a = Row(y - 1); var b = Row(y); var c = Row(y + 1);
                for (int x = 0; x < w; x++)
                {
                    float g = wt[x]; if (!(g > 0f)) continue;
                    int q = (y * w + x) * 4, n = x * 4;
                    float r0 = d[q], g0 = d[q + 1], b0 = d[q + 2], m = ((r0 + g0) + b0) * 0.33333334f;
                    float nl = ((K1 * a[n] + K0 * b[n]) + K1 * c[n]) * luma;
                    model.Amplitude(m, out float ar, out float ag, out float ab);
                    float vr = r0 + (g * ar) * (nl + ((K1 * a[n + 1] + K0 * b[n + 1]) + K1 * c[n + 1]) * chroma);
                    float vg = g0 + (g * ag) * (nl + ((K1 * a[n + 2] + K0 * b[n + 2]) + K1 * c[n + 2]) * chroma);
                    float vb = b0 + (g * ab) * (nl + ((K1 * a[n + 3] + K0 * b[n + 3]) + K1 * c[n + 3]) * chroma);
                    d[q] = vr > 0f ? vr : 0f; d[q + 1] = vg > 0f ? vg : 0f; d[q + 2] = vb > 0f ? vb : 0f;
                }
            }
        });
    }

    /// <summary>The standard deviation that the measuring high-pass filter (<see cref="LuxNoiseModel.HighPass"/>) finds in grain of unit
    /// variance made by <see cref="AddGrain"/>: the norm of the two kernels convolved.</summary>
    internal static readonly float GrainHighPass = ComputeGrainHighPass();

    static float ComputeGrainHighPass()
    {
        var g = LuxNoiseModel.HighPass; double k2 = (double)K0 * K0 + 2.0 * K1 * K1;
        double[] f = { K1 / Math.Sqrt(k2), K0 / Math.Sqrt(k2), K1 / Math.Sqrt(k2) };
        // one axis: the grain kernel alone (the delta part) and the grain kernel convolved with the Gaussian
        var fd = new double[7]; var fg = new double[7];
        for (int i = 0; i < 3; i++) { fd[i + 2] += f[i]; for (int j = 0; j < 5; j++) fg[i + j] += f[i] * g[j]; }
        double sum = 0;
        for (int y = 0; y < 7; y++) for (int x = 0; x < 7; x++) { double v = fd[y] * fd[x] - fg[y] * fg[x]; sum += v * v; }
        return (float)Math.Sqrt(sum);
    }
}

/// <summary>
/// The grain of a picture at one level, measured in flat areas: per channel, the grain amplitude as a table over the brightness, and how
/// much of the grain the channels share.
/// </summary>
public sealed class LuxNoiseModel
{
    public const int Bins = 24;
    /// <summary>Bin position = sqrt(brightness) · <see cref="Scale"/>, with the brightness = the mean of RGB.</summary>
    public readonly float Scale;
    /// <summary>The grain amplitude at the center of each bin, three channels per bin.</summary>
    public readonly float[] Amp;
    /// <summary>The weights of the shared field and of the per-channel fields (their squares add up to 1).</summary>
    public readonly float Luma, Chroma;
    /// <summary>The model came from the picture (false = the fallback).</summary>
    public readonly bool Measured;

    /// <summary>The 5-tap Gaussian (σ 1.2) that the measurement takes away to keep the high-pass part.</summary>
    internal static readonly float[] HighPass = DofFilters.GaussianKernel(5, 1.2f, 1f);

    public LuxNoiseModel(float scale, float[] amp, float luma, float chroma, bool measured) { Scale = scale; Amp = amp; Luma = luma; Chroma = chroma; Measured = measured; }

    /// <summary>The amplitudes at brightness <paramref name="m"/>: linear between the bin centers, constant below the first center, and
    /// growing with sqrt(brightness) above the last one.</summary>
    public void Amplitude(float m, out float r, out float g, out float b)
    {
        float s = MathF.Sqrt(m > 0f ? m : 0f) * Scale, x = s - 0.5f;
        if (!(x > 0f)) { r = Amp[0]; g = Amp[1]; b = Amp[2]; return; }
        if (x >= Bins - 1) { int e = (Bins - 1) * 3; float k = s / ((float)Bins - 0.5f); r = Amp[e] * k; g = Amp[e + 1] * k; b = Amp[e + 2] * k; return; }
        int i = (int)x; float t = x - (float)i; int q = i * 3;
        r = Amp[q] + (Amp[q + 3] - Amp[q]) * t; g = Amp[q + 1] + (Amp[q + 4] - Amp[q + 1]) * t; b = Amp[q + 2] + (Amp[q + 5] - Amp[q + 2]) * t;
    }

    /// <summary>A model without a measurement: the amplitude of Lumen's own noise (a uniform value times 0.0067·sqrt(brightness)) on the
    /// channel mean, shared by the channels. Each channel takes it in proportion to its weight (the channel's share of a neutral color), so
    /// that the grain has no color.</summary>
    public static LuxNoiseModel Fallback(float wr = 1f, float wg = 1f, float wb = 1f)
    {
        float scale = Bins, mean = ((wr + wg) + wb) / 3f; var amp = new float[Bins * 3];
        if (!(mean > 0f)) { wr = wg = wb = 1f; mean = 1f; }
        for (int i = 0; i < Bins; i++)
        {
            float s = ((float)i + 0.5f) / scale, a = 0.0067f * 0.57735026f * s / mean;
            amp[i * 3] = a * wr; amp[i * 3 + 1] = a * wg; amp[i * 3 + 2] = a * wb;
        }
        return new LuxNoiseModel(scale, amp, 1f, 0f, false);
    }

    /// <summary>
    /// Measure the model on the color of a level. <paramref name="colour"/> gives the RGBA pixels of a rect of the level. The samples are up
    /// to twelve rects on a fixed 4 × 3 grid. Each rect is cut into 8 × 8 blocks. A block gives its mean brightness and the variance of the
    /// high-pass part (pixel minus the 5 × 5 Gaussian) of each channel and of the channel mean. Per brightness bin, the lower quartile of
    /// the block variances, corrected for that choice, is the grain of the bin: flat blocks have the least variance. Blocks with a pixel
    /// at or below zero are left out. <paramref name="wr"/>, <paramref name="wg"/>, <paramref name="wb"/> are the channel weights of the
    /// fallback.
    /// </summary>
    public static LuxNoiseModel Measure(Func<RectI, float[]> colour, (int W, int H) dims, float wr = 1f, float wg = 1f, float wb = 1f)
    {
        const int B = 8, Gx = 4, Gy = 3;
        int rw = Math.Min(512, dims.W / Gx), rh = Math.Min(512, dims.H / Gy);
        if (rw < 64 || rh < 64) return Fallback(wr, wg, wb);
        var blocks = new List<(float M, float Vr, float Vg, float Vb, float Vm)>();
        var k = HighPass;
        for (int gy = 0; gy < Gy; gy++)
            for (int gx = 0; gx < Gx; gx++)
            {
                int cx = (2 * gx + 1) * dims.W / (2 * Gx), cy = (2 * gy + 1) * dims.H / (2 * Gy);
                int x0 = Math.Clamp(cx - rw / 2, 0, dims.W - rw), y0 = Math.Clamp(cy - rh / 2, 0, dims.H - rh);
                var px = colour(new RectI(x0, y0, x0 + rw, y0 + rh));
                var low = DofFilters.ConvSeparable(px, rw, rh, 4, k, k);
                for (int by = 4; by + B <= rh - 4; by += B)
                    for (int bx = 4; bx + B <= rw - 4; bx += B)
                    {
                        double sm = 0, s0 = 0, s1 = 0, s2 = 0, s3 = 0, q0 = 0, q1 = 0, q2 = 0, q3 = 0; bool ok = true;
                        for (int y = by; y < by + B && ok; y++)
                            for (int x = bx; x < bx + B; x++)
                            {
                                int q = (y * rw + x) * 4;
                                float r = px[q], g = px[q + 1], b = px[q + 2];
                                if (!(r > 0f) || !(g > 0f) || !(b > 0f) || float.IsInfinity(r + g + b)) { ok = false; break; }
                                double hr = (double)r - low[q], hg = (double)g - low[q + 1], hb = (double)b - low[q + 2], hm = ((hr + hg) + hb) / 3.0;
                                sm += ((double)r + g + b) / 3.0;
                                s0 += hr; q0 += hr * hr; s1 += hg; q1 += hg * hg; s2 += hb; q2 += hb * hb; s3 += hm; q3 += hm * hm;
                            }
                        if (!ok) continue;
                        const double N = B * B;
                        blocks.Add(((float)(sm / N), (float)((q0 - s0 * s0 / N) / (N - 1)), (float)((q1 - s1 * s1 / N) / (N - 1)), (float)((q2 - s2 * s2 / N) / (N - 1)),
                                    (float)((q3 - s3 * s3 / N) / (N - 1))));
                    }
            }
        if (blocks.Count < 200) return Fallback(wr, wg, wb);
        // the brightness bins are even steps of sqrt(brightness) up to the 98th percentile of the blocks; brighter blocks go to the last bin
        var means = new float[blocks.Count]; for (int i = 0; i < means.Length; i++) means[i] = blocks[i].M;
        Array.Sort(means);
        float mmax = means[(int)((long)means.Length * 98 / 100)];
        if (!(mmax > 0f)) return Fallback(wr, wg, wb);
        float scale = Bins / MathF.Sqrt(mmax);
        var bins = new List<(float M, float Vr, float Vg, float Vb, float Vm)>[Bins];
        for (int i = 0; i < Bins; i++) bins[i] = new();
        foreach (var b in blocks) bins[Math.Min(Bins - 1, (int)(MathF.Sqrt(b.M) * scale))].Add(b);
        // the lower quartile of a sample variance with 63 degrees of freedom is 0.875 of the true variance
        const double Selection = 0.875;
        var v = new double[Bins * 4]; var binM = new double[Bins]; var wgt = new double[Bins]; var valid = new bool[Bins];
        for (int i = 0; i < Bins; i++)
        {
            var l = bins[i]; if (l.Count < 40) continue;
            double mean = 0; foreach (var b in l) mean += b.M;
            var one = new float[l.Count];
            for (int c = 0; c < 4; c++)
            {
                for (int j = 0; j < one.Length; j++) one[j] = c == 0 ? l[j].Vr : c == 1 ? l[j].Vg : c == 2 ? l[j].Vb : l[j].Vm;
                Array.Sort(one);
                v[i * 4 + c] = Math.Max(0, one[one.Length / 4] / Selection);
            }
            binM[i] = mean / l.Count; wgt[i] = l.Count; valid[i] = true;
        }
        int first = Array.IndexOf(valid, true), last = Array.LastIndexOf(valid, true);
        if (first < 0) return Fallback(wr, wg, wb);
        // The measured bin with the most blocks is the reference. A bin of textured blocks can only be too high. So a brighter bin is
        // limited to 1.15 times the reference scaled by sqrt(brightness): real grain does not grow faster. A darker bin is limited to
        // 1.15 times the reference: grain does not fall when the brightness rises.
        int anchor = first; for (int i = first; i <= last; i++) if (valid[i] && wgt[i] > wgt[anchor]) anchor = i;
        for (int c = 0; c < 4; c++)
            for (int i = first; i <= last; i++)
            {
                double lim = 1.15 * Math.Sqrt(v[anchor * 4 + c]) * (i > anchor ? ((double)i + 0.5) / ((double)anchor + 0.5) : 1.0);
                if (valid[i] && v[i * 4 + c] > lim * lim) v[i * 4 + c] = lim * lim;
            }
        // A bin without a measurement takes the line between its measured neighbors. Outside the measured bins the grain follows
        // sqrt(brightness).
        var sd = new double[Bins * 4];
        for (int c = 0; c < 4; c++)
            for (int i = 0; i < Bins; i++)
            {
                if (valid[i]) { sd[i * 4 + c] = Math.Sqrt(v[i * 4 + c]); continue; }
                if (i < first) { sd[i * 4 + c] = Math.Sqrt(v[first * 4 + c]) * (((double)i + 0.5) / ((double)first + 0.5)); continue; }
                if (i > last) { sd[i * 4 + c] = Math.Sqrt(v[last * 4 + c]) * (((double)i + 0.5) / ((double)last + 0.5)); continue; }
                int lo = i, hi = i; while (!valid[lo]) lo--; while (!valid[hi]) hi++;
                double t = (double)(i - lo) / (hi - lo);
                sd[i * 4 + c] = Math.Sqrt(v[lo * 4 + c]) * (1 - t) + Math.Sqrt(v[hi * 4 + c]) * t;
            }
        var amp = new float[Bins * 3]; float cal = LuxRendering.GrainHighPass; double sumM = 0, sumFull = 0, sumInd = 0;
        for (int i = 0; i < Bins; i++)
        {
            double r = sd[i * 4], g = sd[i * 4 + 1], bl = sd[i * 4 + 2], mm = sd[i * 4 + 3];
            amp[i * 3] = (float)(r / cal); amp[i * 3 + 1] = (float)(g / cal); amp[i * 3 + 2] = (float)(bl / cal);
            double mean = ((r + g) + bl) / 3.0;
            if (!valid[i]) continue;
            sumM += mm * mm * wgt[i]; sumFull += mean * mean * wgt[i]; sumInd += ((r * r + g * g) + bl * bl) / 9.0 * wgt[i];
        }
        // the variance of the channel mean lies between the one of equal fields (sumFull) and the one of independent fields (sumInd)
        double shared = sumFull > sumInd ? Math.Clamp((sumM - sumInd) / (sumFull - sumInd), 0.0, 1.0) : 1.0;
        return new LuxNoiseModel(scale, amp, (float)Math.Sqrt(shared), (float)Math.Sqrt(1.0 - shared), true);
    }
}
