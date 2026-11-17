using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>
/// A stereo pair from two real lenses of the camera. It is not part of Lumen.
/// <para>The two wide lenses A5 and A1 sit about 44 mm apart, almost on one horizontal line when the camera is held level.
/// That is close to the distance between two eyes, so their two pictures form a real stereo pair: no depth map is involved,
/// and each eye sees what its lens saw.</para>
/// <para>Steps:</para>
/// <list type="number">
/// <item>Each lens picture is resampled with the capture's calibration onto the grid of one shared camera orientation, turned
/// so that the image rows run along the line between the two lenses. A point of the scene is then on the same row in both
/// pictures.</item>
/// <item>The horizontal offsets between the two pictures are measured with a block matcher at a quarter of the size. A match
/// counts when it holds in both directions and agrees with its neighbors. The 1st and the 99th percentile of the offsets give the far end and the near end of the scene.</item>
/// <item>The right picture is shifted so that the far end is 1.5 % of the width behind the screen. When the whole scene spans
/// less than that, the nearest object is put at the screen.</item>
/// <item>The right picture gets the colors of the left picture: a tone curve for each channel and a 3×3 matrix, fitted on the
/// matched blocks.</item>
/// <item>Both pictures are cropped to the largest common rectangle with the shape of the frame.</item>
/// </list>
/// <para>A scene whose offsets span more than 3 % of the width is too deep for comfortable viewing with this pair. With the
/// far end at infinity, that happens when the nearest object is closer than about 1.2 m.</para>
/// <para>All arithmetic is integer sums and IEEE operations in a fixed order, so the result is the same on every processor
/// and with every thread count.</para>
/// </summary>
public static class RealStereo
{
    /// <summary>The far end of the scene is put this far behind the screen, in percent of the picture width.</summary>
    public const double FarTargetPercent = 1.5;
    /// <summary>The largest span of offsets that is comfortable to view, in percent of the picture width.</summary>
    public const double BudgetPercent = 3.0;
    /// <summary>The fewest matched blocks that give a usable measurement.</summary>
    public const int MinMatches = 60;

    const int Q = 4;                 // the matcher works on pictures reduced by this factor
    const int Patch = 15, HalfPatch = Patch / 2, GridStep = 6;
    const double MinPeak = 0.75, PeakMargin = 0.03;
    const int MinPatchVariance = 9;  // (3 gray levels)²
    const double NearestMm = 300.0;  // the search covers objects from this distance to infinity

    /// <summary>What the matcher measured, and the convergence that follows from it.</summary>
    public sealed class Measurement
    {
        /// <summary>Number of matched blocks.</summary>
        public int Matches;
        /// <summary>The 1st and the 99th percentile of the horizontal offsets (left minus right), in pixels of the rectified picture.</summary>
        public double FarPx, NearPx;
        /// <summary>Width of the rectified picture.</summary>
        public int Width;
        /// <summary>Span of the offsets in percent of the width.</summary>
        public double RangePercent => 100.0 * (NearPx - FarPx) / Width;
        /// <summary>The shift of the right picture, in pixels.</summary>
        public double ShiftPx;
        /// <summary>The rule that gave the shift.</summary>
        public string Rule = "";
        /// <summary>Parallax of the nearest objects (in front of the screen, zero or negative) and of the farthest (behind, positive),
        /// in percent of the width.</summary>
        public double CrossedPercent => 100.0 * (ShiftPx - NearPx) / Width;
        public double UncrossedPercent => 100.0 * (ShiftPx - FarPx) / Width;
        /// <summary>True when the scene is within the comfort budget.</summary>
        public bool Comfortable => RangePercent <= BudgetPercent;
    }

    /// <summary>A finished pair.</summary>
    public sealed class Pair
    {
        public Rgba Left = null!, Right = null!;
        public string LeftModule = "", RightModule = "";
        /// <summary>Distance between the two lenses in mm.</summary>
        public double BaselineMm;
        public Measurement Measured = new();
        /// <summary>Share of the frame that the common crop removed, in percent.</summary>
        public double LostPercent;
        /// <summary>False when too few blocks matched for the color fit: the right picture then keeps its own colors.</summary>
        public bool ColorMatched;
    }

    /// <summary>The two lenses of the pair and the vector from the left one to the right one in mm, or a reason why this capture has
    /// no such pair. <paramref name="heldTurn"/> is the clockwise turn that shows the capture upright (0, 90, 180, 270).</summary>
    public static (string Left, string Right, double[] Baseline)? Lenses(LriFile lri, int heldTurn, out string? reason)
    {
        reason = null;
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;
        if (Wigglegram.Group(refId) != 0) { reason = "a telephoto capture has no two lenses with the same view"; return null; }
        if (!lri.Modules.ContainsKey("A5") || !lri.Modules.ContainsKey("A1")) { reason = "the capture does not hold both wide lenses A5 and A1"; return null; }
        int turn = ((heldTurn % 360) + 360) % 360;
        if (turn is 90 or 270) { reason = "the camera was held turned, so the two lenses are one above the other"; return null; }
        var a = Wigglegram.CameraCentre(lri, lri.Modules["A5"].Module.Id); var b = Wigglegram.CameraCentre(lri, lri.Modules["A1"].Module.Id);
        if (a is null || b is null) { reason = "the capture has no calibration for the lenses A5 and A1"; return null; }
        // the left eye is the lens with the smaller x in the axes of the recorded picture
        var (l, r, cl, cr) = a.Value.X <= b.Value.X ? ("A5", "A1", a.Value, b.Value) : ("A1", "A5", b.Value, a.Value);
        return (l, r, new[] { cr.X - cl.X, cr.Y - cl.Y, cr.Z - cl.Z });
    }

    /// <summary>The rotation whose rows are the axes of the rectified camera: x along the line between the lenses, y across it in
    /// the image plane.</summary>
    public static double[] RectifyingRotation(double[] baseline)
    {
        double n = Math.Sqrt(baseline[0] * baseline[0] + baseline[1] * baseline[1] + baseline[2] * baseline[2]);
        double r1x = baseline[0] / n, r1y = baseline[1] / n, r1z = baseline[2] / n;
        // r2 = z × r1, normalized
        double r2x = -r1y, r2y = r1x, m = Math.Sqrt(r2x * r2x + r2y * r2y);
        r2x /= m; r2y /= m;
        // r3 = r1 × r2
        double r3x = r1y * 0 - r1z * r2y, r3y = r1z * r2x - r1x * 0, r3z = r1x * r2y - r1y * r2x;
        return new[] { r1x, r1y, r1z, r2x, r2y, 0, r3x, r3y, r3z };
    }

    /// <summary>One lens picture on the rectified grid. <paramref name="k"/> is the shared camera matrix, <paramref name="rotation"/>
    /// the rectifying rotation. The mask is 1 where the lens saw the pixel.</summary>
    public static (Rgba Image, byte[] Mask) Rectify(Rgba module, AlignedCalib calib, float[] k, double[] rotation, int w, int h)
    {
        // shared camera pixel = K Rᵀ K⁻¹ · rectified pixel
        double fx = k[0], fy = k[4], cx = k[2], cy = k[5];
        var o = new Rgba(w, h); var mask = new byte[w * h];
        int mw = module.W, mh = module.H;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double ax = (x - cx) / fx, ay = (y - cy) / fy;
                double bx = rotation[0] * ax + rotation[3] * ay + rotation[6], by = rotation[1] * ax + rotation[4] * ay + rotation[7], bz = rotation[2] * ax + rotation[5] * ay + rotation[8];
                double px = fx * bx / bz + cx, py = fy * by / bz + cy;
                var (mx, my) = calib.Map((float)px, (float)py, 0);
                int x0 = (int)Math.Floor(mx), y0 = (int)Math.Floor(my);
                if (!(mx >= 0 && my >= 0) || x0 + 1 >= mw || y0 + 1 >= mh) continue;
                double tx = mx - x0, ty = my - y0;
                long pa = ((long)y0 * mw + x0) * 4, pb = pa + 4, pc = pa + (long)mw * 4, pd = pc + 4, d = ((long)y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = (module.P[pa + c] * (1 - tx) + module.P[pb + c] * tx) * (1 - ty) + (module.P[pc + c] * (1 - tx) + module.P[pd + c] * tx) * ty;
                    o.P[d + c] = (byte)Math.Clamp(v + 0.5, 0, 255);
                }
                o.P[d + 3] = 255; mask[y * w + x] = 1;
            }
        });
        return (o, mask);
    }

    /// <summary>A picture reduced by the matcher's factor: mean color of each block in sixteenths of a level, the gray value, and a
    /// mask that is 1 where the whole block is valid.</summary>
    sealed class Small
    {
        public int W, H; public int[] Rgb = null!; public byte[] Gray = null!, Mask = null!;
    }

    static Small Reduce(Rgba img, byte[] mask)
    {
        int w = img.W / Q, h = img.H / Q;
        var s = new Small { W = w, H = h, Rgb = new int[w * h * 3], Gray = new byte[w * h], Mask = new byte[w * h] };
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, ok = 1;
                for (int j = 0; j < Q; j++)
                    for (int i = 0; i < Q; i++)
                    {
                        int sx = x * Q + i, sy = y * Q + j; long p = ((long)sy * img.W + sx) * 4;
                        r += img.P[p]; g += img.P[p + 1]; b += img.P[p + 2]; ok &= mask[sy * img.W + sx];
                    }
                int o = y * w + x;
                s.Rgb[o * 3] = r; s.Rgb[o * 3 + 1] = g; s.Rgb[o * 3 + 2] = b;
                // gray = 0.299 R + 0.587 G + 0.114 B of the mean color, in integer arithmetic
                s.Gray[o] = (byte)((r * 4899L + g * 9617L + b * 1868L + (8192L * Q * Q)) / (16384L * Q * Q));
                s.Mask[o] = (byte)ok;
            }
        });
        return s;
    }

    /// <summary>One matched block: its position in the reduced left picture and the offset to the right picture (left minus right).</summary>
    readonly record struct Match(int X, int Y, int D, double Sub);

    /// <summary>Sums of a 15×15 patch for the normalized correlation.</summary>
    static void PatchSums(byte[] g, int w, int x, int y, out long s, out long ss)
    {
        s = 0; ss = 0;
        for (int j = -HalfPatch; j <= HalfPatch; j++)
        {
            int o = (y + j) * w + x - HalfPatch;
            for (int i = 0; i < Patch; i++) { int v = g[o + i]; s += v; ss += v * v; }
        }
    }

    static long Cross(byte[] a, int ax, int ay, byte[] b, int bx, int by, int w)
    {
        long s = 0;
        for (int j = -HalfPatch; j <= HalfPatch; j++)
        {
            int oa = (ay + j) * w + ax - HalfPatch, ob = (by + j) * w + bx - HalfPatch; int row = 0;
            for (int i = 0; i < Patch; i++) row += a[oa + i] * b[ob + i];
            s += row;
        }
        return s;
    }

    /// <summary>The best position of patch (ax, ay) of <paramref name="a"/> in <paramref name="b"/> on the same row, over the
    /// offsets bx = ax − d·sign for d in [dMin, dMax]. Returns false when no clear peak exists.</summary>
    static bool Search(Small a, Small b, int[] validB, int ax, int ay, int sign, int dMin, int dMax, out int best, out double sub, double[] score)
    {
        best = 0; sub = 0;
        const int n = Patch * Patch; int w = a.W;
        PatchSums(a.Gray, w, ax, ay, out long sa, out long saa);
        long varA = n * saa - sa * sa;
        if (varA < (long)MinPatchVariance * n * n) return false;
        double top = double.NegativeInfinity; int count = dMax - dMin + 1;
        for (int k = 0; k < count; k++)
        {
            int d = dMin + k, bx = ax - sign * d;
            score[k] = double.NegativeInfinity;
            if (bx - HalfPatch < 0 || bx + HalfPatch >= w) continue;
            if (BoxSum(validB, w + 1, bx - HalfPatch, ay - HalfPatch, Patch) != n) continue;
            PatchSums(b.Gray, w, bx, ay, out long sb, out long sbb);
            long varB = n * sbb - sb * sb;
            double den = Math.Sqrt((double)varA * (double)varB);
            double sc = den > 0 ? (double)(n * Cross(a.Gray, ax, ay, b.Gray, bx, ay, w) - sa * sb) / den : 0.0;
            score[k] = sc;
            if (sc > top) { top = sc; best = k; }
        }
        if (!(top >= MinPeak)) return false;
        for (int k = 0; k < count; k++)
            if (Math.Abs(k - best) > 2 && score[k] > top - PeakMargin) return false;
        if (best > 0 && best < count - 1 && double.IsFinite(score[best - 1]) && double.IsFinite(score[best + 1]))
        {
            double l = score[best - 1], m = score[best], r = score[best + 1], dd = l - 2 * m + r;
            sub = dd != 0 ? 0.5 * (l - r) / dd : 0.0;
        }
        best += dMin;
        return true;
    }

    static int[] Integral(byte[] m, int w, int h)
    {
        var s = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int row = 0;
            for (int x = 0; x < w; x++) { row += m[y * w + x]; s[(y + 1) * (w + 1) + x + 1] = s[y * (w + 1) + x + 1] + row; }
        }
        return s;
    }

    static int BoxSum(int[] s, int stride, int x, int y, int side) => s[(y + side) * stride + x + side] - s[y * stride + x + side] - s[(y + side) * stride + x] + s[y * stride + x];

    /// <summary>The percentile of a sorted list, with linear interpolation between neighbors.</summary>
    static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double pos = p / 100.0 * (sorted.Length - 1); int i = (int)Math.Floor(pos); double f = pos - i;
        return i + 1 < sorted.Length ? sorted[i] * (1 - f) + sorted[i + 1] * f : sorted[i];
    }

    static double Median(List<double> v)
    {
        var a = v.ToArray(); Array.Sort(a);
        return (a.Length & 1) == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    /// <summary>The convergence rule for a measured range of offsets.</summary>
    public static Measurement Converge(double farPx, double nearPx, int width, int matches)
    {
        var m = new Measurement { FarPx = farPx, NearPx = nearPx, Width = width, Matches = matches };
        if (m.RangePercent <= FarTargetPercent) { m.ShiftPx = nearPx; m.Rule = "nearest object at the screen"; }
        else { m.ShiftPx = farPx + FarTargetPercent / 100.0 * width; m.Rule = FormattableString.Invariant($"far end {FarTargetPercent} % behind the screen"); }
        return m;
    }

    /// <summary>The largest rectangle of set pixels in a mask, sampled every 8 pixels and set in by one sample, then shrunk about its
    /// center to the shape <paramref name="aspect"/> (width / height). Returns (x0, y0, x1, y1), the end exclusive.</summary>
    public static (int X0, int Y0, int X1, int Y1) LargestRectangle(byte[] mask, int w, int h, double aspect)
    {
        const int step = 8;
        int mw = (w + step - 1) / step, mh = (h + step - 1) / step;
        var heights = new int[mw];
        long bestArea = 0; int bx0 = 0, by0 = 0, bx1 = 0, by1 = 0;
        var stackX = new int[mw + 1]; var stackH = new int[mw + 1];
        for (int y = 0; y < mh; y++)
        {
            for (int x = 0; x < mw; x++) heights[x] = mask[y * step * w + x * step] != 0 ? heights[x] + 1 : 0;
            int top = 0;
            for (int x = 0; x <= mw; x++)
            {
                int hh = x < mw ? heights[x] : 0, start = x;
                while (top > 0 && stackH[top - 1] >= hh)
                {
                    top--; int sx = stackX[top], sh = stackH[top];
                    long area = (long)sh * (x - sx);
                    if (area > bestArea) { bestArea = area; bx0 = sx; by0 = y - sh + 1; bx1 = x; by1 = y + 1; }
                    start = sx;
                }
                stackX[top] = start; stackH[top] = hh; top++;
            }
        }
        double x0 = bx0 * step + step, y0 = by0 * step + step, x1 = bx1 * step - step, y1 = by1 * step - step;
        if (x1 > x0 && y1 > y0)
        {
            double rw = x1 - x0, rh = y1 - y0;
            if (rw / rh > aspect) { double nw = rh * aspect, c = (x0 + x1) / 2; x0 = c - nw / 2; x1 = c + nw / 2; }
            else { double nh = rw / aspect, c = (y0 + y1) / 2; y0 = c - nh / 2; y1 = c + nh / 2; }
        }
        return ((int)x0, (int)y0, (int)x1, (int)y1);
    }

    /// <summary>The tone curve and matrix that give a picture the colors of another, fitted on pairs of mean block colors. Null when
    /// the pairs are too few.</summary>
    public sealed class ColorFit
    {
        public double[][] Curve = null!;      // three curves of 256 values
        public double[] Matrix = null!;       // 4 rows × 3 columns: out = [curve(r), curve(g), curve(b), 1] · Matrix
        public byte[] Apply(byte r, byte g, byte b)
        {
            double a = Curve[0][r], c = Curve[1][g], d = Curve[2][b];
            var o = new byte[3];
            for (int k = 0; k < 3; k++) o[k] = (byte)Math.Clamp(a * Matrix[k] + c * Matrix[3 + k] + d * Matrix[6 + k] + Matrix[9 + k] + 0.5, 0, 255);
            return o;
        }
    }

    /// <summary>Fit the colors of <paramref name="src"/> to <paramref name="dst"/>: each list holds one mean color (R, G, B, 0 to 255)
    /// for each matched block.</summary>
    public static ColorFit? FitColor(IReadOnlyList<double[]> src, IReadOnlyList<double[]> dst)
    {
        int n = src.Count;
        if (n < 200) return null;
        var fit = new ColorFit { Curve = new double[3][] };
        for (int c = 0; c < 3; c++)
        {
            // the median of the target for each bin of 8 source levels, made monotone
            var cen = new List<double>(); var med = new List<double>();
            for (int lo = 0; lo < 256; lo += 8)
            {
                var sv = new List<double>(); var dv = new List<double>();
                for (int i = 0; i < n; i++) if (src[i][c] >= lo && src[i][c] < lo + 8) { sv.Add(src[i][c]); dv.Add(dst[i][c]); }
                if (sv.Count > 30) { cen.Add(Median(sv)); med.Add(Median(dv)); }
            }
            for (int i = 1; i < med.Count; i++) if (med[i] < med[i - 1]) med[i] = med[i - 1];
            var curve = new double[256];
            for (int v = 0; v < 256; v++)
            {
                if (cen.Count <= 2) { curve[v] = v; continue; }
                if (v <= cen[0]) { curve[v] = med[0]; continue; }
                if (v >= cen[^1]) { curve[v] = med[^1]; continue; }
                int k = 0; while (cen[k + 1] < v) k++;
                double t = cen[k + 1] > cen[k] ? (v - cen[k]) / (cen[k + 1] - cen[k]) : 0;
                curve[v] = med[k] * (1 - t) + med[k + 1] * t;
            }
            fit.Curve[c] = curve;
        }
        // least squares for [curve(r), curve(g), curve(b), 1] → target, by the normal equations
        var ata = new double[16]; var atb = new double[12];
        var row = new double[4];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++) row[c] = fit.Curve[c][(int)Math.Clamp(src[i][c], 0, 255)];
            row[3] = 1;
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++) ata[a * 4 + b] += row[a] * row[b];
                for (int c = 0; c < 3; c++) atb[a * 3 + c] += row[a] * dst[i][c];
            }
        }
        var m = Solve4(ata, atb);
        fit.Matrix = m ?? new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
        return fit;
    }

    /// <summary>Solve A·X = B for a 4×4 matrix A and three right sides, by elimination with row pivoting. Null when A is singular.</summary>
    static double[]? Solve4(double[] a, double[] b)
    {
        a = (double[])a.Clone(); b = (double[])b.Clone();
        for (int c = 0; c < 4; c++)
        {
            int p = c;
            for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r * 4 + c]) > Math.Abs(a[p * 4 + c])) p = r;
            if (!(Math.Abs(a[p * 4 + c]) > 1e-9)) return null;
            if (p != c)
            {
                for (int k = 0; k < 4; k++) (a[c * 4 + k], a[p * 4 + k]) = (a[p * 4 + k], a[c * 4 + k]);
                for (int k = 0; k < 3; k++) (b[c * 3 + k], b[p * 3 + k]) = (b[p * 3 + k], b[c * 3 + k]);
            }
            for (int r = 0; r < 4; r++)
            {
                if (r == c) continue;
                double f = a[r * 4 + c] / a[c * 4 + c];
                for (int k = c; k < 4; k++) a[r * 4 + k] -= f * a[c * 4 + k];
                for (int k = 0; k < 3; k++) b[r * 3 + k] -= f * b[c * 3 + k];
            }
        }
        var x = new double[12];
        for (int r = 0; r < 4; r++) for (int k = 0; k < 3; k++) x[r * 3 + k] = b[r * 3 + k] / a[r * 4 + r];
        return x;
    }

    /// <summary>Measure, converge, match the colors and crop a rectified pair. <paramref name="focalPx"/> and
    /// <paramref name="baselineMm"/> set the search range. Returns null when too few blocks match.</summary>
    public static Pair? Finish(Rgba left, byte[] leftMask, Rgba right, byte[] rightMask, double focalPx, double baselineMm, Action<string>? log = null)
    {
        int w = left.W, h = left.H;
        var a = Reduce(left, leftMask); var b = Reduce(right, rightMask);
        int sw = a.W, sh = a.H;
        var validA = Integral(a.Mask, sw, sh); var validB = Integral(b.Mask, sw, sh);
        int dMin = -8 / Q * 2, dMax = (int)Math.Ceiling(focalPx * baselineMm / NearestMm / Q);
        int count = dMax - dMin + 1;
        int rows = Math.Max(0, (sh - 2 * HalfPatch - 1 + GridStep - 1) / GridStep);
        var perRow = new List<Match>[rows];
        Parallel.For(0, rows, row =>
        {
            int y = HalfPatch + row * GridStep; var list = new List<Match>(); var score = new double[count];
            for (int x = HalfPatch; x + HalfPatch < sw; x += GridStep)
            {
                if (BoxSum(validA, sw + 1, x - HalfPatch, y - HalfPatch, Patch) != Patch * Patch) continue;
                if (!Search(a, b, validB, x, y, 1, dMin, dMax, out int d, out double sub, score)) continue;
                // the match must hold in the other direction too
                int bx = x - d;
                if (!Search(b, a, validA, bx, y, -1, dMin, dMax, out int back, out _, score) || Math.Abs(back - d) > 1) continue;
                list.Add(new Match(x, y, d, sub));
            }
            perRow[row] = list;
        });
        // a match must agree with its neighbors: a lone match with another offset is a false one (a repeated pattern, a reflection)
        int cols = (sw - 1) / GridStep + 1;
        var grid = new double[rows * cols]; Array.Fill(grid, double.NaN);
        foreach (var l in perRow) foreach (var m in l) grid[((m.Y - HalfPatch) / GridStep) * cols + m.X / GridStep] = m.D + m.Sub;
        var matches = new List<Match>();
        foreach (var l in perRow)
            foreach (var m in l)
            {
                int gy = (m.Y - HalfPatch) / GridStep, gx = m.X / GridStep, agree = 0; double d = m.D + m.Sub;
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        if ((i | j) == 0 || gy + j < 0 || gy + j >= rows || gx + i < 0 || gx + i >= cols) continue;
                        double o = grid[(gy + j) * cols + gx + i];
                        if (Math.Abs(o - d) <= 1.5) agree++;
                    }
                if (agree >= 2) matches.Add(m);
            }
        log?.Invoke($"stereo: {matches.Count} matched blocks, search {dMin * Q} to {dMax * Q} px");
        if (matches.Count < MinMatches) return null;
        var offsets = matches.Select(m => (m.D + m.Sub) * Q).ToArray(); Array.Sort(offsets);
        var meas = Converge(Percentile(offsets, 1), Percentile(offsets, 99), w, matches.Count);
        log?.Invoke(FormattableString.Invariant($"stereo: offsets p1 {Percentile(offsets, 1):F2} p5 {Percentile(offsets, 5):F2} p25 {Percentile(offsets, 25):F2} p50 {Percentile(offsets, 50):F2} p75 {Percentile(offsets, 75):F2} p99 {Percentile(offsets, 99):F2} px"));

        // the colors of the right picture, fitted on the matched blocks
        var src = new List<double[]>(matches.Count); var dst = new List<double[]>(matches.Count);
        foreach (var m in matches)
        {
            var cs = new double[3]; var cd = new double[3];
            for (int j = -HalfPatch; j <= HalfPatch; j++)
                for (int i = -HalfPatch; i <= HalfPatch; i++)
                {
                    int oa = ((m.Y + j) * sw + m.X + i) * 3, ob = ((m.Y + j) * sw + m.X - m.D + i) * 3;
                    for (int c = 0; c < 3; c++) { cd[c] += a.Rgb[oa + c]; cs[c] += b.Rgb[ob + c]; }
                }
            double norm = 1.0 / (Patch * Patch * Q * Q);
            for (int c = 0; c < 3; c++) { cd[c] *= norm; cs[c] *= norm; }
            src.Add(cs); dst.Add(cd);
        }
        var fit = FitColor(src, dst);

        // shift the right picture, then crop both to the common area
        int shift = (int)Math.Round(meas.ShiftPx);
        var common = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xr = x - shift;
                common[y * w + x] = (byte)(xr >= 0 && xr < w ? leftMask[y * w + x] & rightMask[y * w + xr] : 0);
            }
        var (x0, y0, x1, y1) = LargestRectangle(common, w, h, (double)w / h);
        if (x1 - x0 < 64 || y1 - y0 < 64) return null;
        int cw = x1 - x0, ch = y1 - y0;
        var lo = new Rgba(cw, ch); var ro = new Rgba(cw, ch);
        Parallel.For(0, ch, y =>
        {
            for (int x = 0; x < cw; x++)
            {
                long d = ((long)y * cw + x) * 4, pl = ((long)(y + y0) * w + x + x0) * 4, pr = ((long)(y + y0) * w + x + x0 - shift) * 4;
                lo.P[d] = left.P[pl]; lo.P[d + 1] = left.P[pl + 1]; lo.P[d + 2] = left.P[pl + 2]; lo.P[d + 3] = 255;
                if (fit is null) { ro.P[d] = right.P[pr]; ro.P[d + 1] = right.P[pr + 1]; ro.P[d + 2] = right.P[pr + 2]; }
                else { var c = fit.Apply(right.P[pr], right.P[pr + 1], right.P[pr + 2]); ro.P[d] = c[0]; ro.P[d + 1] = c[1]; ro.P[d + 2] = c[2]; }
                ro.P[d + 3] = 255;
            }
        });
        return new Pair { Left = lo, Right = ro, BaselineMm = baselineMm, Measured = meas, LostPercent = 100.0 * (1 - (double)cw * ch / ((double)w * h)), ColorMatched = fit is not null };
    }

    /// <summary>The whole pair from the two rendered lens pictures. <paramref name="native"/> gives the display picture of a lens at
    /// its own size. Returns null with a reason when the matcher finds too little.</summary>
    public static Pair? Build(LriFile lri, StereoAsyncApi api, (string Left, string Right, double[] Baseline) lenses, Func<string, Rgba> native, int heldTurn,
                              out string? reason, Action<string>? log = null)
    {
        reason = null;
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;
        var k = api.Pairs[refId].First.K;
        var rot = RectifyingRotation(lenses.Baseline);
        double baseline = Math.Sqrt(lenses.Baseline[0] * lenses.Baseline[0] + lenses.Baseline[1] * lenses.Baseline[1] + lenses.Baseline[2] * lenses.Baseline[2]);
        var nl = native(lenses.Left); var nr = native(lenses.Right);
        int w = nl.W, h = nl.H;
        var (l, lm) = Rectify(nl, ModuleGrid.CalibFor(lri, api, lenses.Left), k, rot, w, h);
        var (r, rm) = Rectify(nr, ModuleGrid.CalibFor(lri, api, lenses.Right), k, rot, w, h);
        var pair = Finish(l, lm, r, rm, k[0], baseline, log);
        if (pair is null) { reason = "the two lens pictures have too little matching detail to measure the depth of the scene"; return null; }
        pair.LeftModule = lenses.Left; pair.RightModule = lenses.Right;
        // a camera held upside down: both pictures are turned, and the lenses change sides
        if ((((heldTurn % 360) + 360) % 360) == 180)
        {
            (pair.Left, pair.Right) = (Turn180(pair.Right), Turn180(pair.Left));
            (pair.LeftModule, pair.RightModule) = (pair.RightModule, pair.LeftModule);
        }
        return pair;
    }

    static Rgba Turn180(Rgba img)
    {
        var o = new Rgba(img.W, img.H); long n = (long)img.W * img.H;
        for (long i = 0; i < n; i++) { long s = i * 4, d = (n - 1 - i) * 4; o.P[d] = img.P[s]; o.P[d + 1] = img.P[s + 1]; o.P[d + 2] = img.P[s + 2]; o.P[d + 3] = img.P[s + 3]; }
        return o;
    }

    /// <summary>Reduce a picture to <paramref name="ow"/> × <paramref name="oh"/> with the mean of the source area of each pixel.</summary>
    public static Rgba Downscale(Rgba img, int ow, int oh)
    {
        if (ow >= img.W && oh >= img.H) return img;
        var o = new Rgba(ow, oh);
        double sx = (double)img.W / ow, sy = (double)img.H / oh;
        Parallel.For(0, oh, y =>
        {
            double ya = y * sy, yb = (y + 1) * sy; int j0 = (int)Math.Floor(ya), j1 = Math.Min(img.H, (int)Math.Ceiling(yb));
            var acc = new double[3];
            for (int x = 0; x < ow; x++)
            {
                double xa = x * sx, xb = (x + 1) * sx; int i0 = (int)Math.Floor(xa), i1 = Math.Min(img.W, (int)Math.Ceiling(xb));
                acc[0] = acc[1] = acc[2] = 0; double area = 0;
                for (int j = j0; j < j1; j++)
                {
                    double wy = Math.Min(yb, j + 1) - Math.Max(ya, j);
                    for (int i = i0; i < i1; i++)
                    {
                        double wgt = wy * (Math.Min(xb, i + 1) - Math.Max(xa, i)); long p = ((long)j * img.W + i) * 4;
                        acc[0] += wgt * img.P[p]; acc[1] += wgt * img.P[p + 1]; acc[2] += wgt * img.P[p + 2]; area += wgt;
                    }
                }
                long d = ((long)y * ow + x) * 4;
                for (int c = 0; c < 3; c++) o.P[d + c] = (byte)Math.Clamp(acc[c] / area + 0.5, 0, 255);
                o.P[d + 3] = 255;
            }
        });
        return o;
    }

    /// <summary>Two pictures side by side, the left eye on the left. With <paramref name="halfWidth"/> each eye is squeezed to half
    /// its width, so the frame has the size of one eye.</summary>
    public static Rgba SideBySide(Rgba left, Rgba right, bool halfWidth)
    {
        if (halfWidth) { int hw = Math.Max(1, left.W / 2); left = Downscale(left, hw, left.H); right = Downscale(right, hw, right.H); }
        int w = left.W, h = left.H;
        var o = new Rgba(2 * w, h);
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(left.P, y * w * 4, o.P, y * 2 * w * 4, w * 4);
            Buffer.BlockCopy(right.P, y * w * 4, o.P, (y * 2 * w + w) * 4, w * 4);
        }
        return o;
    }
}
