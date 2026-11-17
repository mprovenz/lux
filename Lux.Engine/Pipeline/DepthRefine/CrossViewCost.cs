namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>
/// The cross-view cost of the camera's depth: how well the other cameras of the reference group agree with it.
/// <para>Each other camera sees a point of the scene shifted along its baseline, by an amount that depends on the depth of the point.
/// With the right depth, the picture of the other camera, shifted back, shows the same thing as the reference camera at every pixel.
/// The cost of a pixel is the brightness difference that remains, in gray levels. It is low where the depth is right and the picture
/// has texture. It says nothing where the picture has no texture.</para>
/// <para>All pictures are on one grid: the grid of the depth. The disparity is in pixels of the widest baseline of the group, and each
/// camera gives its baseline as a part of that baseline.</para>
/// </summary>
public sealed class CrossViewCost
{
    readonly int _w, _h;
    readonly float[] _reference, _disparity;
    readonly bool[] _texture;
    float[]? _best, _second;

    /// <summary>The number of other cameras added so far.</summary>
    public int Cameras { get; private set; }

    /// <param name="referenceRgba">The picture of the reference camera, 8 bits per channel, four bytes per pixel.</param>
    /// <param name="disparity">The camera's depth as disparity, row by row.</param>
    public CrossViewCost(byte[] referenceRgba, float[] disparity, int w, int h)
    {
        _w = w; _h = h; _disparity = disparity;
        _reference = Feature(referenceRgba, w, h);
        _texture = Texture(_reference, w, h);
    }

    /// <summary>What the cost compares: the blurred brightness minus its local mean.</summary>
    static float[] Feature(byte[] rgba, int w, int h)
    {
        var l = DepthFusion.Brightness(rgba, w, h);
        var b = RefineOps.GaussianBlur(l, w, h, RefineOps.GaussianKernel(RefineConstants.CrossViewBlurSigma));
        var m = RefineOps.BoxMean(b, w, h, RefineConstants.CrossViewMeanWindow);
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) b[o + x] -= m[o + x]; });
        return b;
    }

    /// <summary>The pixels that the shift search may use: a strong brightness gradient, away from the border.</summary>
    static bool[] Texture(float[] f, int w, int h)
    {
        var t = new bool[(long)w * h]; int e = RefineConstants.AlignBorder;
        RefineOps.Rows(h, y =>
        {
            if (y < e || y >= h - e) return;
            long o = (long)y * w;
            for (int x = e; x < w - e; x++)
            {
                long i = o + x;
                float gx = f[i - w + 1] - f[i - w - 1] + 2f * (f[i + 1] - f[i - 1]) + f[i + w + 1] - f[i + w - 1];
                float gy = f[i + w - 1] - f[i - w - 1] + 2f * (f[i + w] - f[i - w]) + f[i + w + 1] - f[i - w + 1];
                t[i] = Math.Abs(gx) + Math.Abs(gy) > RefineConstants.AlignMinGradient;
            }
        });
        return t;
    }

    /// <summary>Add one other camera of the group.</summary>
    /// <param name="rgba">Its picture on the grid of the reference camera. A pixel with alpha 0 has no picture.</param>
    /// <param name="baselineX">Its baseline from the reference camera along the rows, as a part of the widest baseline.</param>
    /// <param name="baselineY">The same along the columns.</param>
    public void Add(byte[] rgba, double baselineX, double baselineY, CancellationToken cancel = default)
    {
        int w = _w, h = _h; long n = (long)w * h;
        float ux = (float)baselineX, uy = (float)baselineY;
        var f = Feature(rgba, w, h);
        cancel.ThrowIfCancellationRequested();
        var (rx, ry) = ResidualShift(f, ux, uy);
        cancel.ThrowIfCancellationRequested();

        // the other picture at the place the depth predicts, against the reference
        var diff = new float[n]; var seen = new bool[n];
        var reference = _reference; var disparity = _disparity;
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x; float d = disparity[i];
                float sx = x - d * ux + rx[i], sy = y - d * uy + ry[i];
                int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
                if (x0 < -1 || y0 < -1 || x0 >= w || y0 >= h) { diff[i] = Math.Abs(reference[i]); continue; }
                float tx = sx - x0, ty = sy - y0;
                // four neighbors; outside the grid there is no picture and the value is zero
                float v = 0f, a = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int xx = x0 + (k & 1), yy = y0 + (k >> 1);
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    float wt = ((k & 1) == 0 ? 1f - tx : tx) * ((k >> 1) == 0 ? 1f - ty : ty);
                    long p = (long)yy * w + xx;
                    v += wt * f[p];
                    if (rgba[p * 4 + 3] != 0) a += wt;
                }
                diff[i] = Math.Abs(reference[i] - v); seen[i] = a > 0.99f;
            }
        });
        var c = RefineOps.BoxMean(diff, w, h, RefineConstants.CrossViewWindow);
        var best = _best ??= Filled(n); var second = _second ??= Filled(n);
        RefineOps.Rows(h, y =>
        {
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = o + x; float v = seen[i] ? c[i] : RefineConstants.CrossViewNoPicture;
                if (v < best[i]) { second[i] = best[i]; best[i] = v; }
                else if (v < second[i]) second[i] = v;
            }
        });
        Cameras++;
    }

    static float[] Filled(long n)
    {
        var a = new float[n]; Array.Fill(a, float.PositiveInfinity);
        return a;
    }

    /// <summary>The cost of each pixel: the mean of the lowest differences. Null with fewer than
    /// <see cref="RefineConstants.CrossViewMinCameras"/> other cameras.</summary>
    public float[]? Result()
    {
        if (Cameras < RefineConstants.CrossViewMinCameras || _best is null || _second is null) return null;
        var best = _best; var second = _second; int w = _w;
        var cost = new float[best.Length];
        RefineOps.Rows(_h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) cost[o + x] = (best[o + x] + second[o + x]) * 0.5f; });
        return cost;
    }

    /// <summary>
    /// The factory calibration leaves each camera a small shift against the reference camera, and the shift changes slowly across the
    /// picture. This measures it. Each cell of a coarse grid tries every whole shift in a small range on a sample of its textured pixels,
    /// with the camera's depth, and keeps the shift with the lowest mean difference, refined between the whole steps. A cell without a
    /// clear lowest value takes the median of the others. The result is the shift at every pixel.
    /// </summary>
    (float[] X, float[] Y) ResidualShift(float[] f, float ux, float uy)
    {
        int w = _w, h = _h, nx = RefineConstants.AlignCellsX, ny = RefineConstants.AlignCellsY, r = RefineConstants.AlignSearch, side = 2 * r + 1;
        var sx = new float[nx * ny]; var sy = new float[nx * ny]; var has = new bool[nx * ny];
        var reference = _reference; var disparity = _disparity; var texture = _texture;
        Parallel.For(0, nx * ny, cell =>
        {
            int ty = cell / nx, tx = cell % nx;
            int y0 = (int)((long)ty * h / ny), y1 = (int)((long)(ty + 1) * h / ny), x0 = (int)((long)tx * w / nx), x1 = (int)((long)(tx + 1) * w / nx);
            int count = 0;
            for (int y = y0; y < y1; y++) { long o = (long)y * w; for (int x = x0; x < x1; x++) if (texture[o + x]) count++; }
            if (count < RefineConstants.AlignMinSamples) return;
            // an even sample of the textured pixels, in row order
            int m = Math.Min(RefineConstants.AlignSamples, count);
            var px = new float[m]; var py = new float[m]; var pa = new float[m];
            int seenCount = 0, k = 0;
            for (int y = y0; y < y1 && k < m; y++)
            {
                long o = (long)y * w;
                for (int x = x0; x < x1 && k < m; x++)
                {
                    if (!texture[o + x]) continue;
                    if (seenCount++ == (int)((long)k * count / m))
                    {
                        float d = disparity[o + x];
                        px[k] = x - d * ux; py[k] = y - d * uy; pa[k] = reference[o + x]; k++;
                    }
                }
            }
            var e = new double[side * side];
            for (int jy = -r; jy <= r; jy++)
                for (int jx = -r; jx <= r; jx++)
                {
                    double sum = 0;
                    for (int i = 0; i < m; i++) sum += Math.Min(Math.Abs(pa[i] - Sample(f, w, h, px[i] + jx, py[i] + jy)), RefineConstants.AlignMaxDifference);
                    e[(jy + r) * side + jx + r] = sum / m;
                }
            int at = 0;
            for (int i = 1; i < e.Length; i++) if (e[i] < e[at]) at = i;
            int by = at / side, bx = at % side;
            if (bx == 0 || by == 0 || bx == side - 1 || by == side - 1) return;      // the lowest value is at the end of the range
            var sorted = (double[])e.Clone(); Array.Sort(sorted);
            if (e[at] > RefineConstants.AlignMinGain * sorted[sorted.Length / 2]) return;
            sx[cell] = (float)(bx - r + Vertex(e[at - 1], e[at], e[at + 1]));
            sy[cell] = (float)(by - r + Vertex(e[at - side], e[at], e[at + side]));
            has[cell] = true;
        });
        // cells without a shift take the median of the others
        int filled = has.Count(x => x);
        if (filled > 0 && filled < has.Length)
        {
            var bufX = new float[filled]; var bufY = new float[filled]; int j = 0;
            for (int i = 0; i < has.Length; i++) if (has[i]) { bufX[j] = sx[i]; bufY[j++] = sy[i]; }
            float mx = (float)RefineOps.Median(bufX, filled), my = (float)RefineOps.Median(bufY, filled);
            for (int i = 0; i < has.Length; i++) if (!has[i]) { sx[i] = mx; sy[i] = my; }
        }
        return (RefineOps.ResizeCubic(RefineOps.Median3(sx, nx, ny), nx, ny, w, h), RefineOps.ResizeCubic(RefineOps.Median3(sy, nx, ny), nx, ny, w, h));
    }

    /// <summary>The place of the lowest point of the parabola through three values at −1, 0 and 1.</summary>
    static double Vertex(double before, double center, double after)
    {
        double den = before - 2 * center + after;
        return den <= 1e-9 ? 0.0 : 0.5 * (before - after) / den;
    }

    /// <summary>The value at a position between pixels, with the border pixels repeated.</summary>
    static float Sample(float[] f, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0f, w - 1); y = Math.Clamp(y, 0f, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = x - x0, ty = y - y0;
        long a = (long)y0 * w, b = (long)y1 * w;
        return (f[a + x0] * (1f - tx) + f[a + x1] * tx) * (1f - ty) + (f[b + x0] * (1f - tx) + f[b + x1] * tx) * ty;
    }
}
