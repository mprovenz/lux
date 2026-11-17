using System.Numerics;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The telephoto alignment check. It is not part of Lumen. It runs after the registration, only on request.
/// <para>The registration finds the pose of each telephoto module from sparse matches. When the matches are few or noisy, the pose
/// can stay several pixels off, and the merge then loses the detail of that module. The check measures each module against the
/// reference picture and corrects the module with a small rotation when the measurement is clear.</para>
/// <para>Steps for one module:</para>
/// <list type="number">
/// <item>Warp the module's canvas image into the reference frame, with the module's final pose and the depth of the capture.</item>
/// <item>Blur both pictures (Gaussian, sigma 1.5). Match 31×31 patches of the reference on a 12 pixel grid against the warped
/// picture, with a zero-mean normalized correlation and a search of ±24 pixels. Keep a match when its peak is 0.6 or more and is
/// 0.02 above each peak more than 6 pixels away.</item>
/// <item>Each match gives the position that a reference point has in the canvas. Fit a rotation of the module about its center
/// to these positions, with a Huber loss of 1 pixel.</item>
/// <item>Apply the rotation only when there are 40 or more matches, the median residual is 4 pixels or more, and the rotation
/// lowers the residual of the other half by 0.5 pixel or more in 3 of 4 splits (fit on the top half and test on the bottom half,
/// the reverse, and the same for left and right).</item>
/// </list>
/// <para>Residuals are in pixels of the full reference frame. The stereo images have half that resolution.</para>
/// <para>The patch sums are integer sums, and the fit uses only IEEE operations and <see cref="WineCrt"/>. The result is the
/// same on every processor and with every thread count.</para>
/// </summary>
public static class TeleCheck
{
    /// <summary>Search radius in pixels of the stereo image.</summary>
    public const int Search = 24;
    /// <summary>Patch side.</summary>
    public const int Patch = 31;
    /// <summary>Grid step of the patches.</summary>
    public const int Step = 12;
    /// <summary>The smallest number of matches that allows a correction.</summary>
    public const int MinMatches = 40;
    /// <summary>The smallest median residual that allows a correction, in pixels of the full reference frame.</summary>
    public const double MinResidual = 4.0;
    /// <summary>The gain that a split must show on its test half, in pixels of the full reference frame.</summary>
    public const double SplitGain = 0.5;
    /// <summary>The number of splits, of four, that must show the gain.</summary>
    public const int SplitsNeeded = 3;
    /// <summary>The largest rotation that the check applies, as its cosine (1 degree). A larger rotation moves the picture farther than
    /// the search can see.</summary>
    public const double MinCosine = 0.99984769515639127;
    /// <summary>Pixels of the full reference frame for each pixel of a stereo image.</summary>
    public const double FrameScale = 2.0;

    const int Half = Patch / 2, Margin = Half + Search + 1, Window = Patch + 2 * Search, Positions = 2 * Search + 1;
    const int Quant = 4;                       // blurred gray values are kept as integers, in quarters of a gray level
    const double MinPeak = 0.6, PeakMargin = 0.02;
    const int Exclusion = 6;
    const int MinPatchVariance = 36;           // (1.5 gray levels × Quant)²
    const int MaxPatchValue = 250 * Quant;

    /// <summary>The Gaussian kernel for sigma 1.5, 13 taps.</summary>
    static readonly double[] Kernel =
    {
        8.922106863539231e-05, 0.0010281965781389086, 0.007597402196596928, 0.03599434807370883, 0.10934117495453051, 0.21296752854785403,
        0.2659642571610709, 0.21296752854785403, 0.10934117495453051, 0.03599434807370883, 0.007597402196596928, 0.0010281965781389086,
        8.922106863539231e-05,
    };

    /// <summary>The reference side of the check. It is the same for every module of a capture.</summary>
    public sealed class Reference
    {
        public int W, H;
        /// <summary>Gray reference picture.</summary>
        public byte[] Gray = null!;
        /// <summary>The blurred gray picture, in quarters of a gray level.</summary>
        public int[] Blurred = null!;
        /// <summary>Depth of each reference pixel, in millimeters.</summary>
        public float[] Depth = null!;
        /// <summary>The reference view: intrinsics, rotation and translation.</summary>
        public double[] K = null!, R = null!, T = null!;
    }

    /// <summary>What the check found for one module.</summary>
    public sealed class Result
    {
        /// <summary>Number of matches.</summary>
        public int Matches;
        /// <summary>Median residual of the matches before the correction, in pixels of the full reference frame. NaN without matches.</summary>
        public double Residual = double.NaN;
        /// <summary>Median residual of the matches after the fitted rotation. NaN when no rotation was fitted.</summary>
        public double ResidualAfter = double.NaN;
        /// <summary>Number of splits in which the rotation lowered the residual of the test half enough. −1 when a split had too few matches.</summary>
        public int Splits = -1;
        /// <summary>The fitted rotation in the canvas frame, row-major. Null when no rotation was fitted.</summary>
        public double[]? Rotation;
        /// <summary>Angle of the fitted rotation in degrees.</summary>
        public double AngleDegrees;
        /// <summary>True when the rotation passes the rule and is applied.</summary>
        public bool Apply;
        /// <summary>One short phrase that says why the module was corrected or left as it was.</summary>
        public string Reason = "";
    }

    /// <summary>Gray value of an RGB pixel: 0.299 R + 0.587 G + 0.114 B, rounded, in integer arithmetic.</summary>
    static byte GrayOf(byte r, byte g, byte b) => (byte)((r * 4899 + g * 9617 + b * 1868 + 8192) >> 14);

    /// <summary>The gray picture of an RGBA image.</summary>
    public static byte[] Gray(Rgba8Image img)
    {
        var g = new byte[img.W * img.H];
        for (int y = 0; y < img.H; y++)
        {
            int o = img.Offset(0, y);
            for (int x = 0; x < img.W; x++, o += 4) g[y * img.W + x] = GrayOf(img.Data[o], img.Data[o + 1], img.Data[o + 2]);
        }
        return g;
    }

    static double[] D(float[] a) { var d = new double[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i]; return d; }

    /// <summary>Prepare the reference side: the gray picture, its blurred copy, the depth and the reference view.</summary>
    public static Reference Prepare(byte[] gray, int w, int h, float[] depth, CalibData view, int threads = 1)
    {
        if (gray.Length != w * h || depth.Length != w * h) throw new ArgumentException("the reference picture and the depth must have the same size");
        var f = new float[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = gray[i];
        return new Reference { W = w, H = h, Gray = gray, Blurred = Blur(f, w, h, 0, 0, w, h, threads), Depth = depth, K = D(view.K), R = D(view.R), T = D(view.T) };
    }

    /// <summary>Gaussian blur of the rectangle [x0, x1) × [y0, y1) of a picture, rounded to quarters of a gray level. Pixels outside the
    /// picture repeat the edge. The result has the size of the whole picture; pixels outside the rectangle are 0.</summary>
    static int[] Blur(float[] src, int w, int h, int x0, int y0, int x1, int y1, int threads)
    {
        var tmp = new double[w * h]; var dst = new int[w * h + 64];
        int r = Kernel.Length / 2;
        int ya = Math.Max(0, y0 - r), yb = Math.Min(h, y1 + r);
        Parallel.For(ya, yb, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, y =>
        {
            for (int x = x0; x < x1; x++)
            {
                double s = 0;
                for (int k = -r; k <= r; k++) s += Kernel[k + r] * src[y * w + Math.Clamp(x + k, 0, w - 1)];
                tmp[y * w + x] = s;
            }
        });
        Parallel.For(y0, y1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, y =>
        {
            for (int x = x0; x < x1; x++)
            {
                double s = 0;
                for (int k = -r; k <= r; k++) s += Kernel[k + r] * tmp[Math.Clamp(y + k, 0, h - 1) * w + x];
                dst[y * w + x] = (int)Math.Floor(s * Quant + 0.5);
            }
        });
        return dst;
    }

    /// <summary>A pinhole camera in double precision.</summary>
    readonly struct Camera
    {
        public readonly double Fx, Fy, Skew, Cx, Cy; public readonly double[] R, T;
        public Camera(double[] k, double[] r, double[] t) { Fx = k[0]; Skew = k[1]; Cx = k[2]; Fy = k[4]; Cy = k[5]; R = r; T = t; }
    }

    /// <summary>The scene point of a reference pixel, in world coordinates.</summary>
    static void World(Reference rf, in Camera rc, int x, int y, double z, out double X, out double Y, out double Z)
    {
        double ry = (y - rc.Cy) / rc.Fy, rx = (x - rc.Cx - rc.Skew * ry) / rc.Fx;
        double cx = rx * z - rc.T[0], cy = ry * z - rc.T[1], cz = z - rc.T[2];
        var R = rc.R;
        X = R[0] * cx + R[3] * cy + R[6] * cz; Y = R[1] * cx + R[4] * cy + R[7] * cz; Z = R[2] * cx + R[5] * cy + R[8] * cz;
    }

    /// <summary>The scene point in the frame of a camera.</summary>
    static void ToCamera(in Camera c, double X, double Y, double Z, out double x, out double y, out double z)
    {
        var R = c.R;
        x = R[0] * X + R[1] * Y + R[2] * Z + c.T[0]; y = R[3] * X + R[4] * Y + R[5] * Z + c.T[1]; z = R[6] * X + R[7] * Y + R[8] * Z + c.T[2];
    }

    static bool ValidDepth(float z) => float.IsFinite(z) && z > 0f;

    /// <summary>The canvas position of a reference pixel through the depth. False when the pixel has no depth or is behind the camera.</summary>
    static bool Project(Reference rf, in Camera rc, in Camera cc, int x, int y, out double u, out double v)
    {
        u = v = 0;
        float z = rf.Depth[y * rf.W + x];
        if (!ValidDepth(z)) return false;
        World(rf, rc, x, y, z, out double X, out double Y, out double Z);
        ToCamera(cc, X, Y, Z, out double px, out double py, out double pz);
        if (!(pz > 0)) return false;
        double a = px / pz, b = py / pz;
        u = cc.Fx * a + cc.Skew * b + cc.Cx; v = cc.Fy * b + cc.Cy;
        return true;
    }

    /// <summary>The median as the mean of the two middle values for an even count. The array is sorted in place.</summary>
    static double Median(double[] a)
    {
        if (a.Length == 0) return double.NaN;
        Array.Sort(a);
        int n = a.Length;
        return (n & 1) == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }

    static double MedianOfHistogram(long[] hist, long count)
    {
        // the two middle values of the sorted list, from the histogram
        long lo = (count - 1) / 2, hi = count / 2, seen = 0; int a = -1, b = -1;
        for (int v = 0; v < hist.Length && b < 0; v++)
        {
            seen += hist[v];
            if (a < 0 && seen > lo) a = v;
            if (seen > hi) b = v;
        }
        return 0.5 * (a + b);
    }

    /// <summary>One match: a reference pixel, its scene point in the canvas camera frame, and the canvas position that the pictures give.</summary>
    struct Match { public double X, Y, Z, Tu, Tv; }

    /// <summary>Check one module. <paramref name="canvas"/> is the gray canvas image of the module (0 outside the module) and
    /// <paramref name="view"/> its canvas camera with the final pose.</summary>
    public static Result Check(Reference rf, byte[] canvas, int cw, int ch, CalibData view, int threads = 1)
    {
        var res = new Result();
        var matches = FindMatches(rf, canvas, cw, ch, view, threads);
        res.Matches = matches.Length;
        if (matches.Length == 0) { res.Reason = "no matches"; return res; }
        var cam = new Camera(D(view.K), D(view.R), D(view.T));
        var identity = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        int n = matches.Length;
        var all = new int[n]; for (int i = 0; i < n; i++) all[i] = i;
        var before = Residuals(cam, matches, identity);
        res.Residual = Median(Pick(before, all));
        if (n < MinMatches) { res.Reason = $"{n} matches, too few"; return res; }
        if (!(res.Residual >= MinResidual)) { res.Reason = "aligned"; return res; }

        // the four halves, by the canvas position that the present pose gives
        var top = new List<int>(); var bottom = new List<int>(); var left = new List<int>(); var right = new List<int>();
        for (int i = 0; i < n; i++)
        {
            Predict(cam, matches[i], identity, out double u, out double v);
            (v < ch / 2.0 ? top : bottom).Add(i); (u < cw / 2.0 ? left : right).Add(i);
        }
        var splits = new[] { (top, bottom), (bottom, top), (left, right), (right, left) };
        int good = 0; bool complete = true;
        foreach (var (fit, test) in splits)
        {
            if (fit.Count < 8 || test.Count < 8) { complete = false; break; }
            var rot = Fit(cam, matches, fit.ToArray());
            if (rot is null) { complete = false; break; }
            var ti = test.ToArray();
            double a = Median(Pick(before, ti)), b = Median(Pick(Residuals(cam, matches, rot), ti));
            if (b <= a - SplitGain) good++;
        }
        res.Splits = complete ? good : -1;
        var full = Fit(cam, matches, all);
        if (full is null) { res.Reason = "no stable fit"; return res; }
        res.Rotation = full;
        res.ResidualAfter = Median(Pick(Residuals(cam, matches, full), all));
        double cos = Math.Clamp((full[0] + full[4] + full[8] - 1.0) * 0.5, -1.0, 1.0);
        res.AngleDegrees = WineCrt.Acos(cos) * (180.0 / Math.PI);
        if (!complete) { res.Reason = "matches cover too little of the module"; return res; }
        if (good < SplitsNeeded) { res.Reason = $"correction holds in {good} of 4 splits"; return res; }
        if (cos < MinCosine) { res.Reason = "correction too large"; return res; }
        res.Apply = true; res.Reason = "corrected";
        return res;
    }

    static double[] Pick(double[] a, int[] idx) { var o = new double[idx.Length]; for (int i = 0; i < idx.Length; i++) o[i] = a[idx[i]]; return o; }

    /// <summary>The canvas position of a match after the rotation <paramref name="rot"/> of the module about its center.</summary>
    static void Predict(in Camera c, in Match m, double[] rot, out double u, out double v)
    {
        double x = rot[0] * m.X + rot[1] * m.Y + rot[2] * m.Z, y = rot[3] * m.X + rot[4] * m.Y + rot[5] * m.Z, z = rot[6] * m.X + rot[7] * m.Y + rot[8] * m.Z;
        double a = x / z, b = y / z;
        u = c.Fx * a + c.Skew * b + c.Cx; v = c.Fy * b + c.Cy;
    }

    /// <summary>The residual length of each match, in pixels of the full reference frame.</summary>
    static double[] Residuals(in Camera c, Match[] m, double[] rot)
    {
        var r = new double[m.Length];
        for (int i = 0; i < m.Length; i++)
        {
            Predict(c, m[i], rot, out double u, out double v);
            double du = u - m[i].Tu, dv = v - m[i].Tv;
            r[i] = Math.Sqrt(du * du + dv * dv) * FrameScale;
        }
        return r;
    }

    /// <summary>The rotation matrix of a rotation vector (Rodrigues).</summary>
    static double[] Rotation(double x, double y, double z)
    {
        double theta = Math.Sqrt(x * x + y * y + z * z);
        if (!(theta > 0)) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        x /= theta; y /= theta; z /= theta;
        double c = WineCrt.Cos(theta), s = WineCrt.Sin(theta), oc = 1 - c;
        return new[]
        {
            c + x * x * oc, x * y * oc - z * s, x * z * oc + y * s,
            y * x * oc + z * s, c + y * y * oc, y * z * oc - x * s,
            z * x * oc - y * s, z * y * oc + x * s, c + z * z * oc,
        };
    }

    static double[] Mul(double[] a, double[] b)
    {
        var o = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
        return o;
    }

    /// <summary>Fit the rotation of the module about its center to the matches <paramref name="idx"/>. The cost is the Huber loss
    /// (1 pixel of the canvas) of each coordinate residual. The method is iteratively reweighted least squares on small rotation
    /// steps. Returns null when the matches do not fix the rotation.</summary>
    static double[]? Fit(in Camera c, Match[] m, int[] idx)
    {
        var rot = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        for (int it = 0; it < 200; it++)
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, g0 = 0, g1 = 0, g2 = 0;
            foreach (int i in idx)
            {
                ref readonly Match p = ref m[i];
                double x = rot[0] * p.X + rot[1] * p.Y + rot[2] * p.Z, y = rot[3] * p.X + rot[4] * p.Y + rot[5] * p.Z, z = rot[6] * p.X + rot[7] * p.Y + rot[8] * p.Z;
                double a = x / z, b = y / z;
                double ru = c.Fx * a + c.Skew * b + c.Cx - p.Tu, rv = c.Fy * b + c.Cy - p.Tv;
                // derivatives of a and b for a small rotation step
                double da0 = -a * b, da1 = 1 + a * a, da2 = -b, db0 = -(1 + b * b), db1 = a * b, db2 = a;
                double ju0 = c.Fx * da0 + c.Skew * db0, ju1 = c.Fx * da1 + c.Skew * db1, ju2 = c.Fx * da2 + c.Skew * db2;
                double jv0 = c.Fy * db0, jv1 = c.Fy * db1, jv2 = c.Fy * db2;
                double wu = Math.Abs(ru) <= 1.0 ? 1.0 : 1.0 / Math.Abs(ru), wv = Math.Abs(rv) <= 1.0 ? 1.0 : 1.0 / Math.Abs(rv);
                a00 += wu * ju0 * ju0 + wv * jv0 * jv0; a01 += wu * ju0 * ju1 + wv * jv0 * jv1; a02 += wu * ju0 * ju2 + wv * jv0 * jv2;
                a11 += wu * ju1 * ju1 + wv * jv1 * jv1; a12 += wu * ju1 * ju2 + wv * jv1 * jv2; a22 += wu * ju2 * ju2 + wv * jv2 * jv2;
                g0 += wu * ju0 * ru + wv * jv0 * rv; g1 += wu * ju1 * ru + wv * jv1 * rv; g2 += wu * ju2 * ru + wv * jv2 * rv;
            }
            // solve the 3×3 normal equations by cofactors
            double c00 = a11 * a22 - a12 * a12, c01 = a02 * a12 - a01 * a22, c02 = a01 * a12 - a02 * a11;
            double det = a00 * c00 + a01 * c01 + a02 * c02;
            double scale = a00 * a11 * a22;
            if (!(Math.Abs(det) > 1e-12 * Math.Abs(scale)) || !double.IsFinite(det)) return null;
            double c11 = a00 * a22 - a02 * a02, c12 = a01 * a02 - a00 * a12, c22 = a00 * a11 - a01 * a01;
            double d0 = -(c00 * g0 + c01 * g1 + c02 * g2) / det, d1 = -(c01 * g0 + c11 * g1 + c12 * g2) / det, d2 = -(c02 * g0 + c12 * g1 + c22 * g2) / det;
            if (!double.IsFinite(d0) || !double.IsFinite(d1) || !double.IsFinite(d2)) return null;
            rot = Mul(Rotation(d0, d1, d2), rot);
            if (d0 * d0 + d1 * d1 + d2 * d2 < 1e-20) break;
        }
        return rot;
    }

    /// <summary>The block matches of one module.</summary>
    static Match[] FindMatches(Reference rf, byte[] canvas, int cw, int ch, CalibData view, int threads)
    {
        int w = rf.W, h = rf.H;
        var rc = new Camera(rf.K, rf.R, rf.T); var cc = new Camera(D(view.K), D(view.R), D(view.T));
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };

        // canvas pixels that are inside the module, 4 pixels away from its edge
        var cval = Erode(canvas, cw, ch, 4);

        // the canvas warped into the reference frame, and where it is valid
        var warped = new byte[w * h]; var valid = new byte[w * h];
        Parallel.For(0, h, po, y =>
        {
            for (int x = 0; x < w; x++)
            {
                if (!Project(rf, rc, cc, x, y, out double u, out double v)) continue;
                if (!(u >= 2 && v >= 2 && u < cw - 3 && v < ch - 3)) continue;
                int iu = (int)u, iv = (int)v; double fu = u - iu, fv = v - iv;
                int o = iv * cw + iu;
                double g = (canvas[o] * (1 - fu) + canvas[o + 1] * fu) * (1 - fv) + (canvas[o + cw] * (1 - fu) + canvas[o + cw + 1] * fu) * fv;
                warped[y * w + x] = (byte)Math.Floor(g + 0.5);
                int nu = (int)Math.Round(u), nv = (int)Math.Round(v);
                valid[y * w + x] = cval[nv * cw + nu];
            }
        });

        // bounding box of the valid area
        int bx0 = w, by0 = h, bx1 = 0, by1 = 0; long count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (valid[y * w + x] != 0) { count++; if (x < bx0) bx0 = x; if (x >= bx1) bx1 = x + 1; if (y < by0) by0 = y; if (y >= by1) by1 = y + 1; }
        if (count < 5000) return Array.Empty<Match>();

        // one gain for the module: the ratio of the medians of the two pictures over the valid, well exposed pixels
        var hr = new long[256]; var hw = new long[256]; long nOk = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (valid[i] == 0) continue;
            int a = rf.Gray[i], b = warped[i];
            if (a > 8 && a < 245 && b > 8 && b < 245) { hr[a]++; hw[b]++; nOk++; }
        }
        double gain = nOk > 500 ? MedianOfHistogram(hr, nOk) / Math.Max(MedianOfHistogram(hw, nOk), 1.0) : 1.0;

        var wf = new float[w * h];
        for (int i = 0; i < wf.Length; i++) wf[i] = (float)Math.Min(warped[i] * gain, 255.0);
        var wb = Blur(wf, w, h, bx0, by0, bx1, by1, threads);
        var refB = rf.Blurred;

        // sums over the box: valid count, warped value and warped value squared (for the window sums of the correlation)
        int bw = bx1 - bx0, bh = by1 - by0, sw = bw + 1;
        var sv = new int[(bh + 1) * sw]; var s1 = new long[(bh + 1) * sw]; var s2 = new long[(bh + 1) * sw];
        for (int y = 0; y < bh; y++)
        {
            int rv = 0; long r1 = 0, r2 = 0;
            for (int x = 0; x < bw; x++)
            {
                int i = (y + by0) * w + x + bx0; long q = wb[i];
                rv += valid[i]; r1 += q; r2 += q * q;
                int o = (y + 1) * sw + x + 1;
                sv[o] = sv[o - sw] + rv; s1[o] = s1[o - sw] + r1; s2[o] = s2[o - sw] + r2;
            }
        }
        long Box(long[] s, int x, int y, int side) { int ax = x - bx0, ay = y - by0; return s[(ay + side) * sw + ax + side] - s[ay * sw + ax + side] - s[(ay + side) * sw + ax] + s[ay * sw + ax]; }
        int BoxV(int x, int y, int side) { int ax = x - bx0, ay = y - by0; return sv[(ay + side) * sw + ax + side] - sv[ay * sw + ax + side] - sv[(ay + side) * sw + ax] + sv[ay * sw + ax]; }

        // the grid rows, each on its own worker; the matches are joined in grid order
        int rows = (h - 2 * Margin + Step - 1) / Step;
        if (rows <= 0) return Array.Empty<Match>();
        var perRow = new List<Match>[rows];
        Parallel.For(0, rows, po, row =>
        {
            int y = Margin + row * Step;
            var list = new List<Match>();
            var patch = new int[Patch * 32]; var score = new double[Positions * Positions];
            for (int x = Margin; x < w - Margin; x += Step)
            {
                int wx = x - Half - Search, wy = y - Half - Search;
                if (wx < bx0 || wy < by0 || wx + Window > bx1 || wy + Window > by1) continue;
                if (BoxV(wx, wy, Window) != Window * Window) continue;
                // the reference patch and its sums
                long sa = 0, saa = 0; int max = 0;
                for (int j = 0; j < Patch; j++)
                    for (int i = 0; i < Patch; i++)
                    {
                        int q = refB[(y - Half + j) * w + x - Half + i];
                        patch[j * 32 + i] = q; sa += q; saa += (long)q * q; if (q > max) max = q;
                    }
                const int N = Patch * Patch;
                // variance × N² as integers: N·Σa² − (Σa)²
                long varA = N * saa - sa * sa;
                if (varA < (long)MinPatchVariance * N * N || max >= MaxPatchValue) continue;
                double best = double.NegativeInfinity; int bi = 0, bj = 0;
                for (int dj = 0; dj < Positions; dj++)
                    for (int di = 0; di < Positions; di++)
                    {
                        long sab = Dot(patch, wb, (wy + dj) * w + wx + di, w);
                        long sb = Box(s1, wx + di, wy + dj, Patch), sbb = Box(s2, wx + di, wy + dj, Patch);
                        long varB = N * sbb - sb * sb;
                        double den = Math.Sqrt((double)varA * (double)varB);
                        double sc = den > 0 ? (double)(N * sab - sa * sb) / den : 0.0;
                        score[dj * Positions + di] = sc;
                        if (sc > best) { best = sc; bi = di; bj = dj; }
                    }
                if (best < MinPeak) continue;
                double second = double.NegativeInfinity;
                for (int dj = 0; dj < Positions; dj++)
                    for (int di = 0; di < Positions; di++)
                    {
                        int ex = di - bi, ey = dj - bj;
                        if (ex * ex + ey * ey <= Exclusion * Exclusion) continue;
                        double sc = score[dj * Positions + di]; if (sc > second) second = sc;
                    }
                if (best - second < PeakMargin) continue;
                if (bi <= 0 || bj <= 0 || bi >= Positions - 1 || bj >= Positions - 1) continue;
                double l = score[bj * Positions + bi - 1], m0 = score[bj * Positions + bi], r = score[bj * Positions + bi + 1];
                double u0 = score[(bj - 1) * Positions + bi], d0 = score[(bj + 1) * Positions + bi];
                double dx = l - 2 * m0 + r, dy = u0 - 2 * m0 + d0;
                double offX = bi + (dx != 0 ? 0.5 * (l - r) / dx : 0.0) - Search, offY = bj + (dy != 0 ? 0.5 * (u0 - d0) / dy : 0.0) - Search;

                // the canvas position of the matched point: the projection, interpolated at the matched position
                double qx = x + offX, qy = y + offY; int ix = (int)Math.Floor(qx), iy = (int)Math.Floor(qy); double fx = qx - ix, fy = qy - iy;
                if (!Project(rf, rc, cc, ix, iy, out double u00, out double v00) || !Project(rf, rc, cc, ix + 1, iy, out double u10, out double v10)
                    || !Project(rf, rc, cc, ix, iy + 1, out double u01, out double v01) || !Project(rf, rc, cc, ix + 1, iy + 1, out double u11, out double v11)) continue;
                double tu = (u00 * (1 - fx) + u10 * fx) * (1 - fy) + (u01 * (1 - fx) + u11 * fx) * fy;
                double tv = (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
                float z = rf.Depth[y * w + x];
                if (!ValidDepth(z)) continue;
                World(rf, rc, x, y, z, out double X, out double Y, out double Z);
                ToCamera(cc, X, Y, Z, out double mx, out double my, out double mz);
                if (!(mz > 0) || !double.IsFinite(tu) || !double.IsFinite(tv)) continue;
                list.Add(new Match { X = mx, Y = my, Z = mz, Tu = tu, Tv = tv });
            }
            perRow[row] = list;
        });
        var all = new List<Match>();
        foreach (var l in perRow) if (l is not null) all.AddRange(l);
        return all.ToArray();
    }

    /// <summary>Sum of products of a 31×31 patch (rows of 32, the last value 0) and the picture at <paramref name="offset"/>. The sum
    /// is an exact integer, so the vector width does not change it.</summary>
    static long Dot(int[] patch, int[] img, int offset, int stride)
    {
        int vc = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated && vc <= 32 && (32 % vc) == 0)
        {
            var acc = Vector<int>.Zero;
            for (int j = 0; j < Patch; j++)
            {
                int po = j * 32, io = offset + j * stride;
                for (int i = 0; i < 32; i += vc) acc += new Vector<int>(patch, po + i) * new Vector<int>(img, io + i);
            }
            long s = 0;
            for (int i = 0; i < vc; i++) s += acc[i];
            return s;
        }
        long sum = 0;
        for (int j = 0; j < Patch; j++)
        {
            int po = j * 32, io = offset + j * stride; int rowSum = 0;
            for (int i = 0; i < Patch; i++) rowSum += patch[po + i] * img[io + i];
            sum += rowSum;
        }
        return sum;
    }

    /// <summary>1 where the picture is not 0 in the whole (2r+1)² window around the pixel. Pixels outside the picture do not count.</summary>
    static byte[] Erode(byte[] img, int w, int h, int r)
    {
        var a = new byte[w * h]; var b = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte ok = 1;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) if (img[y * w + k] == 0) { ok = 0; break; }
                a[y * w + x] = ok;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte ok = 1;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) if (a[k * w + x] == 0) { ok = 0; break; }
                b[y * w + x] = ok;
            }
        return b;
    }

    /// <summary>The slot of a module after the rotation <paramref name="rotation"/> in its canvas frame: the canvas camera is rotated
    /// about its center, then taken back through the view pose, as the bundle adjustment writes its result.</summary>
    public static CalibDataFull Corrected(ViewPose pose, CalibDataFull slot, double[] rotation)
    {
        var c = ViewTransform.Apply(pose, slot);
        var c2 = c.Clone();
        for (int i = 0; i < 3; i++)
        {
            for (int k = 0; k < 3; k++) c2.R[3 * i + k] = (float)(rotation[3 * i] * c.R[k] + rotation[3 * i + 1] * c.R[3 + k] + rotation[3 * i + 2] * c.R[6 + k]);
            c2.T[i] = (float)(rotation[3 * i] * c.T[0] + rotation[3 * i + 1] * c.T[1] + rotation[3 * i + 2] * c.T[2]);
        }
        var raw = SparseBaCaller.InverseApply(pose, c2);
        var o = slot.Clone(); o.K = raw.K; o.R = raw.R; o.T = raw.T;
        return o;
    }
}
