using Lux.Engine.Pipeline.Registration;
using Lux.Engine.Pipeline.ResAmp;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// `MattingLaplacianTiling(image vec4, trimap u8, MattingParams)` → alpha u8, as the edge heal calls it
/// (with `MattingParams` tiled = 1, tile overlap 0.1, window radius 2, epsilon 0.00025, known-pixel weight 10,
/// unknown-pixel weight 0.1 × confidence 0.01, no foreground / background color estimation).
///
/// The trimap is cut into tiles (<see cref="Tiles"/>); each tile is solved on its own — closed-form matting Laplacian over the unknown
/// band grown by 12 px, soft constraints, conjugate gradient (<see cref="SolveTile"/>) — and the u8 tile alphas are blended with separable
/// Hann windows. Every tile is independent of the others and the blend adds them in tile order, so the result does not depend on the
/// number of worker threads.
/// </summary>
public static class MattingLaplacian
{
    /// <summary>Window radius, epsilon, constraint weights, confidence fill and overlap of the `MattingParams`.</summary>
    public const int Radius = 2;
    public const float Epsilon = 0.00025f, KnownWeight = 10.0f, UnknownWeight = 0.1f, Confidence = 0.01f, Overlap = 0.1f;
    /// <summary>`TiledBase` constants: downscale target width 800, overlap cap 64 px, minimum tile side 200 px, 8 candidate
    /// split positions; minimum known-class fraction 0.1.</summary>
    public const int SmallWidth = 800, OverlapCap = 64, MinSide = 200, Candidates = 8;
    public const float MinFraction = 0.1f;
    /// <summary>Conjugate gradient: at most 500 iterations (`ConjugateGradient::setMaxIterations`), threshold = ‖b‖² · 9.9999994e-11
    /// (tolerance 1e-5 squared).</summary>
    public const int MaxIterations = 500;
    static readonly float Tol2 = BitConverter.UInt32BitsToSingle(0x2edbe6fe);

    public readonly record struct Tile(int X, int Y, int W, int H, bool Rows);

    /// <summary>Lux's own control of a long solve (an app's edge heal): a cancellation checked between tiles and every 16 conjugate-gradient
    /// iterations, and the progress (0–1: tiles done plus the share of the iteration cap of the tile being solved). Neither changes a value.</summary>
    public sealed class SolveControl
    {
        public CancellationToken Cancel { get; init; }
        public Action<double>? Progress { get; init; }
        internal int Tile, Tiles = 1;
        internal void Report(double inTile) => Progress?.Invoke(Math.Min(1.0, (Tile + Math.Min(1.0, inTile)) / Math.Max(1, Tiles)));
    }

    public static byte[] Run(float[] guide, byte[] trimap, int w, int h) => Run(guide, trimap, w, h, null);

    /// <summary><see cref="Run(float[], byte[], int, int)"/> with a <see cref="SolveControl"/> (same result).</summary>
    public static byte[] Run(float[] guide, byte[] trimap, int w, int h, SolveControl? control)
    {
        var tiles = Tiles(trimap, w, h);
        // per-tile weights and the reciprocal of their sum
        var total = new float[(long)w * h]; var weights = new float[tiles.Count][];
        for (int t = 0; t < tiles.Count; t++)
        {
            var tl = tiles[t]; var wx = Window(tl.W); var wy = Window(tl.H); var tw = weights[t] = new float[(long)tl.W * tl.H];
            for (int y = 0; y < tl.H; y++) for (int x = 0; x < tl.W; x++) tw[(long)y * tl.W + x] = wx[x] * wy[y];
            for (int y = 0; y < tl.H; y++) { long o = (long)(tl.Y + y) * w + tl.X; for (int x = 0; x < tl.W; x++) total[o + x] = total[o + x] + tw[(long)y * tl.W + x]; }
        }
        for (long i = 0; i < total.LongLength; i++) total[i] = 1.0f / total[i];
        // per tile: solve, accumulate
        var acc = new float[(long)w * h];
        if (control != null) control.Tiles = tiles.Count;
        for (int t = 0; t < tiles.Count; t++)
        {
            if (control != null) { control.Cancel.ThrowIfCancellationRequested(); control.Tile = t; control.Report(0); }
            var tl = tiles[t]; var g = new float[(long)tl.W * tl.H * 4]; var tr = new byte[(long)tl.W * tl.H];
            for (int y = 0; y < tl.H; y++)
            {
                Array.Copy(guide, ((long)(tl.Y + y) * w + tl.X) * 4, g, (long)y * tl.W * 4, tl.W * 4);
                Array.Copy(trimap, (long)(tl.Y + y) * w + tl.X, tr, (long)y * tl.W, tl.W);
            }
            var a = SolveTile(g, tr, tl.W, tl.H, null, control); var tw = weights[t];
            for (int y = 0; y < tl.H; y++) { long o = (long)(tl.Y + y) * w + tl.X; for (int x = 0; x < tl.W; x++) acc[o + x] = (float)a[(long)y * tl.W + x] * tw[(long)y * tl.W + x] + acc[o + x]; }
        }
        // (byte)cvtt(acc · 1/Σw); trimap 0xff stays 0xff
        var o8 = new byte[(long)w * h];
        for (long i = 0; i < o8.LongLength; i++) o8[i] = trimap[i] == 0xff ? (byte)0xff : (byte)EditMath.Cvtt(total[i] * acc[i]);
        return o8;
    }

    /// <summary>Window function 1 (selected by the `MattingParams` window type = 1), mirrored:
    /// <c>(float)((double)((1 − cosf((float)i · 6.283f / (float)(n − 1))) · 0.5f) + 1e-5)</c>.</summary>
    public static float[] Window(int n)
    {
        var v = new float[Math.Max(n, 0)]; float k = BitConverter.UInt32BitsToSingle(0x40c90e56);
        if (n <= 0) return v;
        for (int i = 0, j = n; i < (n + 1) / 2 || i == 0; i++)
        {
            j--;
            float c = EditCrt.Cosf((float)i * k / (float)(n - 1));
            float f = (float)((double)((1.0f - c) * 0.5f) + 1.0000000000000001e-05);
            v[i] = f; v[j] = f;
        }
        return v;
    }

    // ---------------------------------------------------------------------------------------------- tiling

    /// <summary>
    /// `TiledTrimapImage` and its tiling.
    /// The trimap is reduced by <c>1 / f</c>, f = 1, 1.5, 2 … until <c>f · 800 ≥ width</c> (nearest); three summed-area
    /// tables count its 0xff, 0 and other pixels. A stack starts with the whole image; each popped rect is split at the best of 8 positions
    /// (fraction (i + 1) / 9, cost = max of the two parts' <see cref="Cost"/>, first minimum); if either part costs
    /// exactly 1 the rect becomes a tile; a part that costs 0 (one known class only) is dropped; the others go back on the stack
    /// (first part, then second: the second is processed first). Tiles are scaled back by <c>(float)W / (float)smallW</c> (truncation),
    /// grown by the overlap and clamped to the image.
    /// </summary>
    public static List<Tile> Tiles(byte[] trimap, int w, int h)
    {
        float f = 1.0f;
        if (w > SmallWidth) do { f += 0.5f; } while (f * 800.0f < (float)w);
        int sw = EditMath.Cvtt((float)w * (1.0f / f)), sh = EditMath.Cvtt((float)h * (1.0f / f));
        var small = EditResample.Nearest(trimap, w, h, 1, sw, sh);
        var c255 = new int[(long)sw * sh]; var c0 = new int[(long)sw * sh]; var cU = new int[(long)sw * sh];
        for (int y = 0; y < sh; y++)
        {
            int r255 = 0, r0 = 0, rU = 0;
            for (int x = 0; x < sw; x++)
            {
                byte b = small[(long)y * sw + x];
                if (b == 0xff) r255++; else if (b == 0) r0++; else rU++;
                long i = (long)y * sw + x, up = i - sw;
                c255[i] = r255 + (y > 0 ? c255[up] : 0); c0[i] = r0 + (y > 0 ? c0[up] : 0); cU[i] = rU + (y > 0 ? cU[up] : 0);
            }
        }
        int Count(int[] s, Tile t)
        {
            int x1 = t.X - 1 + t.W, y1 = t.Y - 1 + t.H, x0 = t.X - 1, y0 = t.Y - 1;
            int v = s[(long)y1 * sw + x1];
            if (t.X == 0 && t.Y == 0) return v;
            int a = 0, b = 0, c = 0;
            if (t.X == 0) c = s[(long)y0 * sw + x1];
            else if (t.Y == 0) b = s[(long)y1 * sw + x0];
            else { a = s[(long)y0 * sw + x0]; b = s[(long)y1 * sw + x0]; c = s[(long)y0 * sw + x1]; }
            return ((a + v) - b) - c;
        }
        // the cost of a rect of the small trimap
        float Cost(Tile t)
        {
            int min = EditMath.Cvtt((float)(sw * MinSide) / (float)w);
            if (t.H < min || t.W < min) return 1.0f;
            int n255 = Count(c255, t), nU = Count(cU, t), n0 = Count(c0, t);
            float total = (float)(nU + n255 + n0);
            if (t.W * t.H != EditMath.Cvtt(total)) throw new InvalidOperationException("counters in evalROI() are wrong!");
            float inv = 1.0f / total, f0 = (float)n0 * inv, f255 = inv * (float)n255;
            float mx = f0 > f255 ? f0 : f255;                       // MAXSS order: NaN gives f255
            if (mx == 1.0f) return 0.0f;
            if ((float)nU / total == 0.0f) return 1.0f;
            float mn = f0 < f255 ? f0 : f255;                       // MINSS order: NaN gives f255
            if (!(mn >= MinFraction)) return 1.0f;
            float v = MathF.Abs((f255 * total) / ((float)n0 + (float)n255) + -0.5f);
            v += v;
            return (float)((double)v + 9.9999999999999995e-07);
        }
        // split a tile at a fraction of its long side
        static void Split(Tile t, float frac, out Tile a, out Tile b)
        {
            bool rows = t.Rows ? t.W <= t.H / 2 : t.W < t.H * 2;
            if (rows)
            {
                int h1 = EditMath.Cvtt((float)t.H * frac);
                a = new Tile(t.X, t.Y, t.W, h1, true); b = new Tile(t.X, h1 + t.Y, t.W, t.H - h1, true);
            }
            else
            {
                int w1 = EditMath.Cvtt((float)t.W * frac);
                a = new Tile(t.X, t.Y, w1, t.H, false); b = new Tile(w1 + t.X, t.Y, t.W - w1, t.H, false);
            }
        }
        var result = new List<Tile>(); var stack = new List<Tile> { new(0, 0, sw, sh, true) };
        var costs = new float[Candidates];
        while (stack.Count != 0)
        {
            var t = stack[^1]; stack.RemoveAt(stack.Count - 1);
            for (int i = 0; i < Candidates; i++)
            {
                Split(t, ((float)i + 1.0f) / ((float)Candidates + 1.0f), out var a, out var b);
                float cb = Cost(b), ca = Cost(a);
                costs[i] = ca < cb ? cb : ca;
            }
            int best = 0;
            for (int i = 1; i < Candidates; i++) if (costs[i] < costs[best]) best = i;
            Split(t, ((float)best + 1.0f) / ((float)Candidates + 1.0f), out var pa, out var pb);
            float c1 = Cost(pa), c2 = Cost(pb);
            if (c1 == 1.0f || c2 == 1.0f) { pa = default; pb = default; }
            if (c1 == 0.0f) pa = default;
            if (c2 == 0.0f) pb = default;
            if (pa.W <= 0 && pb.W <= 0) { result.Add(t); continue; }
            if (pa.W > 0) stack.Add(pa);
            if (pb.W > 0) stack.Add(pb);
        }
        float fs = (float)w / (float)sw; float ov = Overlap; float fo = ov + ov + 1.0f;
        for (int i = 0; i < result.Count; i++)
        {
            var t = result[i];
            int x = EditMath.Cvtt((float)t.X * fs), y = EditMath.Cvtt((float)t.Y * fs), tw = EditMath.Cvtt((float)t.W * fs), th = EditMath.Cvtt((float)t.H * fs);
            // grow by the overlap
            float fw = (float)tw, fh = (float)th;
            int nw = fw * ov <= (float)OverlapCap ? EditMath.Cvtt(DofCrt.Roundf(fo * fw)) : tw + OverlapCap * 2;
            int nh = ov * fh <= (float)OverlapCap ? EditMath.Cvtt(DofCrt.Roundf(fo * fh)) : th + OverlapCap * 2;
            x = EditMath.Cvtt(DofCrt.Roundf((fw - (float)nw) * 0.5f + (float)x));
            y = EditMath.Cvtt(DofCrt.Roundf((fh - (float)nh) * 0.5f + (float)y));
            // clamp to the image
            int x0 = x > -1 ? x : 0, y0 = y > -1 ? y : 0;
            int x1 = x - 1 + nw; if (!(x1 <= w - 1)) x1 = w - 1;
            int y1 = y - 1 + nh; if (!(y1 <= h - 1)) y1 = h - 1;
            result[i] = new Tile(x0, y0, (1 - x0) + x1, (1 - y0) + y1, true);
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------- one tile

    /// <summary>Intermediate results of <see cref="SolveTile"/> for tests.</summary>
    public sealed class SolveTrace
    {
        public byte[]? Region; public float[]? Stats; public int N; public int[]? RowStart, Cols; public float[]? Vals, B, X0, X; public int Iterations;
    }

    /// <summary>
    /// One tile: build the Laplacian, then solve.
    /// 1. The image is padded by 2r = 4 px with a mirror that repeats the edge.
    /// 2. System region = the pixels within 12 px (box 25 × 25) of a trimap byte 0x7f / 0x80, numbered in raster order.
    /// 3. Window statistics: 5 × 5 box means of the color, of its squares and of its cross products on the padded image;
    ///    per pixel the inverse of the 3 × 3 covariance + (ε/25)·I.
    /// 4. Matrix: for pixels i ≤ j of the region within 4 px of each other, the sum over every 5 × 5 window that holds
    ///    both — and whose top-left pixel is in the region — of <c>δij + (−1 − (Ci − μ)ᵀ Σ⁻¹ (Cj − μ)) · (1/25)</c>.
    /// 5. Constraints: diagonal += c, <c>x0 = trimap/255</c>, <c>b = x0 · c</c>, c = 10 on known pixels, 0.1 · 0.01 on the rest.
    /// 6. Eigen's conjugate gradient without preconditioner, from x0 (<see cref="ConjugateGradient"/>).
    /// 7. Unknown pixels of the region ← <c>cvtt(x · 255)</c> clamped to [0, 255]; the rest keeps the trimap byte.
    /// </summary>
    public static byte[] SolveTile(float[] guide, byte[] tri, int w, int h, SolveTrace? trace = null, SolveControl? control = null)
    {
        int r = Radius, pad = 2 * r, pw = w + 2 * pad, ph = h + 2 * pad;
        // 1. padded image
        var p = new float[(long)pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            int sy = y - pad; if (sy < 0) sy = -1 - sy; else if (sy >= h) sy = 2 * h - 1 - sy;
            for (int xq = 0; xq < pw; xq++)
            {
                int sx = xq - pad; if (sx < 0) sx = -1 - sx; else if (sx >= w) sx = 2 * w - 1 - sx;
                Array.Copy(guide, ((long)sy * w + sx) * 4, p, ((long)y * pw + xq) * 4, 4);
            }
        }
        // 2. region and numbering
        var region = Region(tri, w, h, 6 * r);
        var map = new int[(long)pw * ph]; Array.Fill(map, -1); int n = 0;
        for (int y = 0; y < h; y++) for (int xq = 0; xq < w; xq++) if (region[(long)y * w + xq] != 0) map[(long)(y + pad) * pw + xq + pad] = n++;
        if (trace != null) { trace.Region = region; trace.N = n; }
        // 3. statistics
        var (mean, stats) = Statistics(p, pw, ph, map);
        if (trace != null) trace.Stats = stats;
        // 4. matrix rows (the matrix is symmetric bit for bit; a row is complete and sorted once its pixel is done)
        int side = 4 * r + 1, cap = side * side;
        var cols = new int[(long)n * cap]; var vals = new float[(long)n * cap]; var cnt = new int[n];
        float k25 = 1.0f / ((float)(2 * r + 1) * (float)(2 * r + 1));
        for (int py = 0; py < ph; py++)
            for (int px = 0; px < pw; px++)
            {
                int i = map[(long)py * pw + px];
                if (i < 0) continue;
                long ci = ((long)py * pw + px) * 4; float cr = p[ci], cg = p[ci + 1], cb = p[ci + 2];
                for (int qy = py - pad; qy <= py + pad; qy++)
                    for (int qx = px - pad; qx <= px + pad; qx++)
                    {
                        int j = map[(long)qy * pw + qx];
                        if (i > j) continue;
                        long cj = ((long)qy * pw + qx) * 4;
                        float sum = 0.0f, delta = i == j ? 1.0f : 0.0f;
                        int wyHi = Math.Min(Math.Min(py, qy), ph - 4 * r - 1), wxHi = Math.Min(Math.Min(px, qx), pw - 4 * r - 1);
                        for (int wy = Math.Max(py, qy) - pad; wy <= wyHi; wy++)
                            for (int wx = Math.Max(px, qx) - pad; wx <= wxHi; wx++)
                            {
                                if (map[(long)wy * pw + wx] < 0) continue;
                                long c = (long)(wy + r) * pw + wx + r, m = c * 4, s = c * 6;
                                float mr = mean[m], mg = mean[m + 1], mb = mean[m + 2];
                                float djr = p[cj] - mr, djg = p[cj + 1] - mg, djb = p[cj + 2] - mb;
                                float dir = cr - mr, dig = cg - mg, dib = cb - mb;
                                float s0 = stats[s], s1 = stats[s + 1], s2 = stats[s + 2], s3 = stats[s + 3], s4 = stats[s + 4], s5 = stats[s + 5];
                                float tr = (s2 * dib + s4 * dig) + s5 * dir;
                                float v = -1.0f - tr * djr;
                                float tg = (dib * s1 + s4 * dir) + s3 * dig;
                                v -= tg * djg;
                                float tb = s0 * dib + (s1 * dig + dir * s2);
                                v -= tb * djb;
                                v *= k25;
                                sum = (sum + delta) + v;
                            }
                        cols[(long)j * cap + cnt[j]] = i; vals[(long)j * cap + cnt[j]++] = sum;
                        if (i < j) { cols[(long)i * cap + cnt[i]] = j; vals[(long)i * cap + cnt[i]++] = sum; }
                    }
            }
        // compact (row i: lower part in ascending order, then the diagonal and the upper part — but the diagonal was appended first)
        var rowStart = new int[n + 1];
        for (int i = 0; i < n; i++) rowStart[i + 1] = rowStart[i] + cnt[i];
        var ccols = new int[rowStart[n]]; var cvals = new float[rowStart[n]];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(cols, (long)i * cap, ccols, rowStart[i], cnt[i]); Array.Copy(vals, (long)i * cap, cvals, rowStart[i], cnt[i]);
            Array.Sort(ccols, cvals, rowStart[i], cnt[i]);
        }
        // 5. constraints
        var b = new float[n]; var x = new float[n];
        for (int y = 0; y < h; y++)
            for (int xx = 0; xx < w; xx++)
            {
                int i = map[(long)(y + pad) * pw + xx + pad];
                if (i < 0) continue;
                byte t = tri[(long)y * w + xx];
                float c = (byte)(t + 1) > 1 ? Confidence * UnknownWeight : KnownWeight;
                float v = (float)t * 0.003921568859368563f;
                int d = Array.BinarySearch(ccols, rowStart[i], cnt[i], i);
                cvals[d] = cvals[d] + c;
                x[i] = v; b[i] = v * c;
            }
        if (trace != null) { trace.RowStart = rowStart; trace.Cols = ccols; trace.Vals = cvals; trace.B = (float[])b.Clone(); trace.X0 = (float[])x.Clone(); }
        // 6. solve
        int it = ConjugateGradient(rowStart, ccols, cvals, b, x, n, control);
        if (trace != null) { trace.X = x; trace.Iterations = it; }
        // 7. write back
        var o = (byte[])tri.Clone();
        for (int y = 0; y < h; y++)
            for (int xx = 0; xx < w; xx++)
            {
                int i = map[(long)(y + pad) * pw + xx + pad];
                if (i < 0 || (byte)(tri[(long)y * w + xx] + 1) <= 1) continue;
                int v = EditMath.Cvtt(x[i] * 255.0f);
                o[(long)y * w + xx] = v > 0xfe ? (byte)0xff : v < 1 ? (byte)0 : (byte)v;
            }
        return o;
    }

    /// <summary>The system region (lap, out, trimap, grow): 0xff where the trimap byte is in [0x7f, 0x80], as floats through
    /// `ImageBoxFilter&lt;float&gt;` of size 2·grow + 1, then &gt; 0 → 1.</summary>
    public static byte[] Region(byte[] tri, int w, int h, int grow)
    {
        var f = new float[(long)w * h];
        for (long i = 0; i < f.LongLength; i++) f[i] = tri[i] >= 0x7f && tri[i] <= 0x80 ? 255.0f : 0.0f;
        int size = grow * 2 + 1;
        var bx = ResAmpKernels.BoxFilter(new ResImage(f, 0, w, h, w, 1, 0, 0, w, h), size, size).Data;
        var o = new byte[(long)w * h];
        for (long i = 0; i < o.LongLength; i++) o[i] = bx[i] > 0.0f ? (byte)1 : (byte)0;
        return o;
    }

    /// <summary>
    /// The window statistics: μ = box5(P); var = box5(P∘P) − μ∘μ; cov = box5((P.b·P.r, P.r·P.g, P.g·P.b, P.a²)) − (μ.b·μ.r, μ.r·μ.g, μ.g·μ.b, μ.a²).
    /// For a numbered pixel, with R, G, B = var + ε/25 and rg, rb, gb = cov: the adjugate terms
    /// <c>d0 = R·G − rg², d1 = −0 − (R·gb − rg·rb), d2 = rg·gb − G·rb, d3 = R·B − rb²</c>, <c>det = (d2·rb + d1·gb) + d0·B</c> and the six
    /// stored values <c>(d0, d1, d2, d3, rb·gb − rg·B, G·B − gb²) / det</c> = Σ⁻¹ entries (BB, GB, RB, GG, RG, RR). Others: (1,0,0,1,0,1).
    /// </summary>
    public static (float[] Mean, float[] Stats) Statistics(float[] p, int pw, int ph, int[] map)
    {
        int size = 2 * Radius + 1; float e = (1.0f / ((float)size * (float)size)) * Epsilon;
        ResImage Img(float[] d) => new(d, 0, pw, ph, pw, 4, 0, 0, pw, ph);
        var mean = ResAmpKernels.BoxFilter(Img(p), size, size).Data;
        var sq = new float[p.Length]; var pp = new float[p.Length]; var mm = new float[p.Length];
        for (long i = 0; i < p.LongLength; i += 4)
        {
            sq[i] = p[i] * p[i]; sq[i + 1] = p[i + 1] * p[i + 1]; sq[i + 2] = p[i + 2] * p[i + 2]; sq[i + 3] = p[i + 3] * p[i + 3];
            pp[i] = p[i + 2] * p[i]; pp[i + 1] = p[i] * p[i + 1]; pp[i + 2] = p[i + 1] * p[i + 2]; pp[i + 3] = p[i + 3] * p[i + 3];
            mm[i] = mean[i + 2] * mean[i]; mm[i + 1] = mean[i] * mean[i + 1]; mm[i + 2] = mean[i + 1] * mean[i + 2]; mm[i + 3] = mean[i + 3] * mean[i + 3];
        }
        var bsq = ResAmpKernels.BoxFilter(Img(sq), size, size).Data; var bpp = ResAmpKernels.BoxFilter(Img(pp), size, size).Data;
        var st = new float[(long)pw * ph * 6];
        for (long q = 0; q < (long)pw * ph; q++)
        {
            long i = q * 4, s = q * 6;
            if (map[q] < 0) { st[s] = 1f; st[s + 3] = 1f; st[s + 5] = 1f; continue; }
            float B = (bsq[i + 2] - mean[i + 2] * mean[i + 2]) + e, G = (bsq[i + 1] - mean[i + 1] * mean[i + 1]) + e, R = (bsq[i] - mean[i] * mean[i]) + e;
            float gb = bpp[i + 2] - mm[i + 2], rb = bpp[i] - mm[i], rg = bpp[i + 1] - mm[i + 1];
            float d0 = R * G - rg * rg, d1 = -0.0f - (R * gb - rg * rb), d2 = rg * gb - G * rb, d3 = R * B - rb * rb;
            float inv = 1.0f / ((d2 * rb + d1 * gb) + d0 * B);
            st[s] = inv * d0; st[s + 1] = inv * d1; st[s + 2] = inv * d2; st[s + 3] = inv * d3;
            st[s + 4] = (rb * gb - rg * B) * inv; st[s + 5] = (G * B - gb * gb) * inv;
        }
        return (mean, st);
    }

    /// <summary>The reduction every dot product of the solve compiles to: the first product seeds lane 0; while 8 or more elements
    /// remain two 4-lane accumulators take alternate groups of four; they are added, reduced <c>(l2 + l0) + (l3 + l1)</c>, and the rest is
    /// added one by one.</summary>
    public static float Dot(float[] a, float[] b, int n)
    {
        float s = a[0] * b[0]; int m = n - 1, vc = m >= 8 ? m & ~7 : 0;
        if (vc > 0)
        {
            float a0 = s, a1 = 0f, a2 = 0f, a3 = 0f, b0 = 0f, b1 = 0f, b2 = 0f, b3 = 0f;
            for (int k = 1; k <= vc; k += 8)
            {
                a0 = a[k] * b[k] + a0; a1 = a[k + 1] * b[k + 1] + a1; a2 = a[k + 2] * b[k + 2] + a2; a3 = a[k + 3] * b[k + 3] + a3;
                b0 = a[k + 4] * b[k + 4] + b0; b1 = a[k + 5] * b[k + 5] + b1; b2 = a[k + 6] * b[k + 6] + b2; b3 = a[k + 7] * b[k + 7] + b3;
            }
            float t0 = a0 + b0, t1 = a1 + b1, t2 = a2 + b2, t3 = a3 + b3;
            s = (t2 + t0) + (t3 + t1);
        }
        for (int i = 1 + vc; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// The inlined Eigen `ConjugateGradient&lt;SparseMatrix&lt;float&gt;&gt;::solveWithGuess` of Lumen's solve (identity preconditioner):
    /// <c>rhsNorm2 = b·b</c> (0 → x = 0); <c>r = b; r[i] −= A[i,j]·x[j]</c> column by column; <c>threshold = rhsNorm2 · 9.9999994e-11</c>;
    /// done when <c>r·r &lt; threshold</c>; p = r; then at most 500 times: <c>t = A·p</c> (t[i] += A[i,j]·p[j]), <c>α = absNew / p·t</c>,
    /// <c>x += α·p</c>, <c>r −= α·t</c>, stop when <c>r·r &lt; threshold</c>, <c>β = r·r / absNew</c>, <c>p = r + β·p</c>.
    /// The matrix is given by rows; it is symmetric bit for bit, so the row order of the accumulation equals Eigen's column order.
    /// </summary>
    public static int ConjugateGradient(int[] rowStart, int[] cols, float[] vals, float[] b, float[] x, int n, SolveControl? control = null)
    {
        if (n == 0) return 0;
        float rhsNorm2 = Dot(b, b, n);
        if (rhsNorm2 == 0.0f) { Array.Clear(x, 0, n); return 0; }
        var res = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = b[i];
            for (int k = rowStart[i]; k < rowStart[i + 1]; k++) s -= vals[k] * x[cols[k]];
            res[i] = s;
        }
        float threshold = rhsNorm2 * Tol2;
        float resNorm2 = Dot(res, res, n);
        if (resNorm2 < threshold) return 0;
        var p = (float[])res.Clone(); var tmp = new float[n];
        float absNew = Dot(res, p, n);
        int it = 0;
        while (it < MaxIterations)
        {
            if (control != null && (it & 15) == 0) { control.Cancel.ThrowIfCancellationRequested(); control.Report((double)it / MaxIterations); }
            for (int i = 0; i < n; i++)
            {
                float s = 0.0f;
                for (int k = rowStart[i]; k < rowStart[i + 1]; k++) s += vals[k] * p[cols[k]];
                tmp[i] = s;
            }
            float alpha = absNew / Dot(p, tmp, n);
            for (int i = 0; i < n; i++) x[i] = x[i] + alpha * p[i];
            for (int i = 0; i < n; i++) res[i] = res[i] - alpha * tmp[i];
            resNorm2 = Dot(res, res, n);
            if (resNorm2 < threshold) break;
            float absOld = absNew;
            absNew = Dot(res, res, n);
            float beta = absNew / absOld;
            for (int i = 0; i < n; i++) p[i] = res[i] + beta * p[i];
            it++;
        }
        return it;
    }
}
