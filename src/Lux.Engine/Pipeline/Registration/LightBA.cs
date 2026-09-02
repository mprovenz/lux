namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `LightBA` — the sparse bundle adjustment of `FUN_1802dc770` (ctor `FUN_180277810`, `Solve` `FUN_1802784c0`; spec
/// `a75927dbfc3c9d6b2.md`): cameras {K, angle-axis, t, mask} in double, constant 3-D points, Ceres 1.12 trust-region
/// Levenberg–Marquardt (SPARSE_SCHUR, nonmonotonic steps, 100 iterations, tolerances 1e-20) on the reprojection cost plus the optional
/// entrance-pupil and intrinsics priors. Points/cameras are normalised by the robust median scale first and de-normalised after; finally
/// every pose is re-expressed relative to camera 0. The linear solve is done exactly on the (Jacobi-scaled) normal equations instead of
/// Ceres' Schur eliminator + Eigen LDLT — results agree to ~1e-10 relative (the caller rounds to float).
/// </summary>
public sealed class LightBA
{
    public sealed class DoubleCam { public long Key; public double[] K = new double[9], Aa = new double[3], T = new double[3]; public int Mask; public DoubleCam Clone() => new() { Key = Key, K = (double[])K.Clone(), Aa = (double[])Aa.Clone(), T = (double[])T.Clone(), Mask = Mask }; }
    public sealed class Obs { public float[] X = new float[3]; public SortedDictionary<long, (float U, float V)> Uv = new(); }

    public List<DoubleCam> Cams;   // ascending key
    public List<Obs> Observations;
    public bool FixPoints, UseIntrinsicsPrior, UseEntrancePupilPrior;

    public LightBA(IEnumerable<DoubleCam> cams, IEnumerable<Obs> obs, bool fixPoints, bool useIntrinsics, bool useEp)
    {
        Cams = cams.Select(c => c.Clone()).OrderBy(c => c.Key).ToList(); Observations = obs.ToList();
        FixPoints = fixPoints; UseIntrinsicsPrior = useIntrinsics; UseEntrancePupilPrior = useEp;
        if (Cams.Count < 3) throw new InvalidOperationException("Very few cameras for BA reconstruction.");
    }

    // ---- internal camera record: f, aspect, cx, cy, aa[3], t[3] ----
    sealed class Rec { public double F, Aspect, Cx, Cy; public double[] Aa = new double[3], T = new double[3]; public int Mask; public long Key; }

    const double DblEps = 2.220446049250313e-16;

    /// <summary>ceres/rotation.h `AngleAxisRotatePoint` (double).</summary>
    static double[] RotatePoint(double[] a, double[] p)
    {
        double theta2 = (a[0] * a[0] + a[1] * a[1]) + a[2] * a[2];
        var r = new double[3];
        if (theta2 > DblEps)
        {
            double theta = Math.Sqrt(theta2), c = Math.Cos(theta), s = Math.Sin(theta), ti = 1.0 / theta;
            double w0 = a[0] * ti, w1 = a[1] * ti, w2 = a[2] * ti;
            double x0 = w1 * p[2] - w2 * p[1], x1 = w2 * p[0] - w0 * p[2], x2 = w0 * p[1] - w1 * p[0];
            double tmp = ((w0 * p[0] + w1 * p[1]) + w2 * p[2]) * (1.0 - c);
            r[0] = (p[0] * c + x0 * s) + w0 * tmp; r[1] = (p[1] * c + x1 * s) + w1 * tmp; r[2] = (p[2] * c + x2 * s) + w2 * tmp;
        }
        else { r[0] = p[0] + (a[1] * p[2] - a[2] * p[1]); r[1] = p[1] + (a[2] * p[0] - a[0] * p[2]); r[2] = p[2] + (a[0] * p[1] - a[1] * p[0]); }
        return r;
    }

    /// <summary>The hand-written Rodrigues of `FUN_18027ca80` / the final point transform (θ ≤ 0 → I).</summary>
    static double[] HandRodrigues(double[] a)
    {
        double theta = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (!(theta > 0.0)) return new[] { 1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0 };
        double x = a[0] / theta, y = a[1] / theta, z = a[2] / theta, s = Math.Sin(theta), c = 1.0 - Math.Cos(theta);
        return new[] {
            1 + ((-z * z) - y * y) * c, (-z) * s + (x * y) * c, (z * x) * c + y * s,
            (x * y) * c + z * s, 1 + ((-z * z) - x * x) * c, (z * y) * c - x * s,
            (z * x) * c - y * s, x * s + (z * y) * c, c * ((-y) * y - x * x) + 1 };
    }

    /// <summary>Eigen 3.3 `AngleAxis = Matrix3` (via `Quaternion` Shepperd conversion, then `AngleAxis(Quaternion)`), row-major input.</summary>
    public static double[] AngleAxisFromMatrix(double[] m)
    {
        double m00 = m[0], m01 = m[1], m02 = m[2], m10 = m[3], m11 = m[4], m12 = m[5], m20 = m[6], m21 = m[7], m22 = m[8];
        double qw, qx, qy, qz;
        double t = m00 + m11 + m22;
        if (t > 0.0) { t = Math.Sqrt(t + 1.0); qw = 0.5 * t; t = 0.5 / t; qx = (m21 - m12) * t; qy = (m02 - m20) * t; qz = (m10 - m01) * t; }
        else
        {
            int i = 0; if (m11 > m00) i = 1; if (m22 > (i == 0 ? m00 : m11)) i = 2;
            int j = (i + 1) % 3, k = (j + 1) % 3;
            double[,] M = { { m00, m01, m02 }, { m10, m11, m12 }, { m20, m21, m22 } };
            t = Math.Sqrt(M[i, i] - M[j, j] - M[k, k] + 1.0);
            var q = new double[3]; q[i] = 0.5 * t; t = 0.5 / t;
            qw = (M[k, j] - M[j, k]) * t; q[j] = (M[j, i] + M[i, j]) * t; q[k] = (M[k, i] + M[i, k]) * t;
            qx = q[0]; qy = q[1]; qz = q[2];
        }
        double n = Math.Sqrt(qx * qx + qy * qy + qz * qz);
        if (n != 0.0)
        {
            double angle = 2.0 * Math.Atan2(n, Math.Abs(qw));
            if (qw < 0.0) n = -n;
            return new[] { qx / n * angle, qy / n * angle, qz / n * angle };
        }
        return new[] { 0.0, 0.0, 0.0 };
    }

    /// <summary>`FUN_180226e60`: OpenCV-style `Rodrigues(mat→vec)` applied to the polar factor `U·Vᵀ` of R (Eigen JacobiSVD in Lumen; here the
    /// Newton polar iteration `X ← ½(X + X⁻ᵀ)`, identical to ~1e-15). Row-major input.</summary>
    public static double[] RotationVector(double[] R)
    {
        var X = (double[])R.Clone();
        for (int it = 0; it < 30; it++)
        {
            var Xi = Inv3(X); var Y = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Y[3 * i + j] = 0.5 * (X[3 * i + j] + Xi[3 * j + i]);
            double diff = 0; for (int k = 0; k < 9; k++) diff += Math.Abs(Y[k] - X[k]);
            X = Y; if (diff < 1e-16) break;
        }
        double M00 = X[0], M01 = X[1], M02 = X[2], M10 = X[3], M11 = X[4], M12 = X[5], M20 = X[6], M21 = X[7], M22 = X[8];
        double a = M10 - M01, b = M21 - M12, c = M02 - M20;
        double s = Math.Sqrt(((a * a + b * b) + c * c) * 0.25);
        double cc = ((M11 + M22) + (M00 + (-1.0))) * 0.5;
        cc = (1.0 < cc) ? 1.0 : Math.Max(-1.0, cc);
        double theta = Math.Acos(cc);
        if (s >= 1e-5) { double k = (0.5 / s) * theta; return new[] { k * b, k * c, k * a }; }
        if (!(cc > 0.0))
        {
            double rx2 = Math.Max((M00 + 1.0) * 0.5, 0.0), rx = Math.Sqrt(rx2);
            double ry = (M01 < 0 ? -1.0 : 1.0) * Math.Sqrt(Math.Max((M11 + 1.0) * 0.5, 0.0));
            double rz = (M02 < 0 ? -1.0 : 1.0) * Math.Sqrt(Math.Max((M22 + 1.0) * 0.5, 0.0));
            if (rx < Math.Abs(ry) && rx < Math.Abs(rz) && ((M12 > 0) != (rz * ry > 0))) rz = -rz;
            double n = Math.Sqrt((rz * rz) + (ry * ry + rx2));
            double k = theta / n; return new[] { k * rx, k * ry, k * rz };
        }
        return new[] { 0.0, 0.0, 0.0 };
    }
    static double[] Inv3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g, det = a * A + b * B + c * C, inv = 1.0 / det;
        return new[] { A * inv, -(b * i - c * h) * inv, (b * f - c * e) * inv, B * inv, (a * i - c * g) * inv, -(a * f - c * d) * inv, C * inv, -(a * h - b * g) * inv, (a * e - b * d) * inv };
    }

    static double Median(List<double> v) { var s = v.ToList(); s.Sort(); return s[s.Count / 2]; }   // nth_element at n/2 (upper median)

    /// <summary>`FUN_1802784c0`: normalise, build + solve the Ceres problem, de-normalise, write back, re-express relative to camera 0.</summary>
    public void Solve(Action<string>? log = null)
    {
        var recs = Cams.Select(c => new Rec { Key = c.Key, F = c.K[0], Aspect = c.K[4] / c.K[0], Cx = c.K[2], Cy = c.K[5], Aa = (double[])c.Aa.Clone(), T = (double[])c.T.Clone(), Mask = c.Mask }).ToList();
        int n = Observations.Count;
        var pts = Observations.Select(o => new[] { (double)o.X[0], (double)o.X[1], (double)o.X[2] }).ToList();
        var m = new double[3];
        for (int c = 0; c < 3; c++) m[c] = Median(pts.Select(p => p[c]).ToList());
        double med = Median(pts.Select(p => (Math.Abs(p[0] - m[0]) + Math.Abs(p[1] - m[1])) + Math.Abs(p[2] - m[2])).ToList());
        double s = 100.0 / med;
        foreach (var p in pts) for (int c = 0; c < 3; c++) p[c] = (p[c] - m[c]) * s;
        foreach (var r in recs) { var Rm = RotatePoint(r.Aa, m); r.T[0] = s * (r.T[0] + Rm[0]); r.T[1] = s * (r.T[1] + Rm[1]); r.T[2] = (Rm[2] + r.T[2]) * s; }
        SolveCeres(recs, pts, log);
        double invs = med * 0.01;
        foreach (var p in pts) for (int c = 0; c < 3; c++) p[c] = p[c] * invs + m[c];
        foreach (var r in recs) { var Rm = RotatePoint(r.Aa, m); for (int c = 0; c < 3; c++) r.T[c] = invs * r.T[c] - Rm[c]; }
        for (int i = 0; i < Cams.Count; i++)
        {
            var r = recs[i]; var c = Cams[i];
            c.K = new[] { r.F, 0, r.Cx, 0, r.Aspect * r.F, r.Cy, 0, 0, 1.0 }; c.Aa = (double[])r.Aa.Clone(); c.T = (double[])r.T.Clone();
        }
        for (int i = 0; i < Observations.Count; i++) for (int c = 0; c < 3; c++) Observations[i].X[c] = (float)pts[i][c];
        // FUN_18027b7a0: relative to camera 0
        if (Cams.Count == 0) throw new InvalidOperationException("Empty camera map.");
        if (Cams[0].Key != 0) throw new ArgumentOutOfRangeException("invalid map<K, T> key");
        var aaR = (double[])Cams[0].Aa.Clone(); var tR = (double[])Cams[0].T.Clone();
        var Rref = HandRodrigues(aaR);
        foreach (var c in Cams)
        {
            var Rcam = HandRodrigues(c.Aa);
            var M = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[3 * i + j] = (Rref[3 * j] * Rcam[3 * i] + Rref[3 * j + 1] * Rcam[3 * i + 1]) + Rref[3 * j + 2] * Rcam[3 * i + 2];   // R_cam·R_refᵀ
            c.Aa = RotationVector(M);   // FUN_180226e60 (polar + OpenCV Rodrigues)
            var t = new double[3];
            for (int i = 0; i < 3; i++) t[i] = c.T[i] - ((M[3 * i + 2] * tR[2] + M[3 * i] * tR[0]) + M[3 * i + 1] * tR[1]);
            c.T = t;
        }
        foreach (var o in Observations)
        {
            double x = o.X[0], y = o.X[1], z = o.X[2];
            for (int i = 0; i < 3; i++) o.X[i] = (float)(((Rref[3 * i + 1] * y + Rref[3 * i] * x) + tR[i]) + Rref[3 * i + 2] * z);
        }
    }

    // ---- Ceres problem: dual-number autodiff over the concatenated variable parameters ----
    sealed class Dual
    {
        public double A; public double[] V;
        public Dual(double a, int n) { A = a; V = new double[n]; }
        public Dual(double a, double[] v) { A = a; V = v; }
        public static Dual operator +(Dual x, Dual y) { var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = x.V[i] + y.V[i]; return new Dual(x.A + y.A, v); }
        public static Dual operator -(Dual x, Dual y) { var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = x.V[i] - y.V[i]; return new Dual(x.A - y.A, v); }
        public static Dual operator -(Dual x) { var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = -x.V[i]; return new Dual(-x.A, v); }
        public static Dual operator *(Dual x, Dual y) { var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = x.A * y.V[i] + x.V[i] * y.A; return new Dual(x.A * y.A, v); }
        public static Dual operator *(Dual x, double s) { var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = x.V[i] * s; return new Dual(x.A * s, v); }
        public static Dual operator *(double s, Dual x) => x * s;
        public static Dual operator +(Dual x, double s) => new(x.A + s, (double[])x.V.Clone());
        public static Dual operator -(Dual x, double s) => new(x.A - s, (double[])x.V.Clone());
        public static Dual operator -(double s, Dual x) => (-x) + s;
        public static Dual operator /(double s, Dual g) { double w = s / g.A; var v = new double[g.V.Length]; double k = -s / (g.A * g.A); for (int i = 0; i < v.Length; i++) v[i] = g.V[i] * k; return new Dual(w, v); }
        public static Dual operator /(Dual f, Dual g) { double inv = 1.0 / g.A, q = f.A * inv; var v = new double[f.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = (f.V[i] - q * g.V[i]) * inv; return new Dual(q, v); }
        public static Dual Sqrt(Dual x) { double s = Math.Sqrt(x.A); var v = new double[x.V.Length]; double k = 0.5 / s; for (int i = 0; i < v.Length; i++) v[i] = k * x.V[i]; return new Dual(s, v); }
        public static Dual Sin(Dual x) { double c = Math.Cos(x.A); var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = c * x.V[i]; return new Dual(Math.Sin(x.A), v); }
        public static Dual Cos(Dual x) { double s = -Math.Sin(x.A); var v = new double[x.V.Length]; for (int i = 0; i < v.Length; i++) v[i] = s * x.V[i]; return new Dual(Math.Cos(x.A), v); }
    }

    static Dual[] RotatePointD(Dual[] a, Dual[] p, int np)
    {
        Dual theta2 = (a[0] * a[0] + a[1] * a[1]) + a[2] * a[2];
        var r = new Dual[3];
        if (theta2.A > DblEps)
        {
            Dual theta = Dual.Sqrt(theta2), c = Dual.Cos(theta), s = Dual.Sin(theta), ti = 1.0 / theta;
            Dual w0 = a[0] * ti, w1 = a[1] * ti, w2 = a[2] * ti;
            Dual x0 = w1 * p[2] - w2 * p[1], x1 = w2 * p[0] - w0 * p[2], x2 = w0 * p[1] - w1 * p[0];
            Dual tmp = ((w0 * p[0] + w1 * p[1]) + w2 * p[2]) * (1.0 - c);
            r[0] = (p[0] * c + x0 * s) + w0 * tmp; r[1] = (p[1] * c + x1 * s) + w1 * tmp; r[2] = (p[2] * c + x2 * s) + w2 * tmp;
        }
        else { r[0] = p[0] + (a[1] * p[2] - a[2] * p[1]); r[1] = p[1] + (a[2] * p[0] - a[0] * p[2]); r[2] = p[2] + (a[0] * p[1] - a[1] * p[0]); }
        return r;
    }

    sealed class Block { public Rec Cam = null!; public int Kind; public int Index = -1; public int Size; }   // Kind: 0 f, 1 aspect, 2 pp, 3 aa, 4 t
    sealed class ResBlock { public int Kind; public Rec Cam = null!; public double[] Data = null!; public double[] Pt = null!; }

    void SolveCeres(List<Rec> recs, List<double[]> pts, Action<string>? log)
    {
        int n = Observations.Count;
        // variable parameter layout
        var blocks = new List<Block>(); int np = 0;
        foreach (var r in recs)
        {
            void Add(int kind, int size, bool variable) { var b = new Block { Cam = r, Kind = kind, Size = size }; if (variable) { b.Index = np; np += size; } blocks.Add(b); }
            Add(0, 1, (r.Mask & 1) != 0); Add(1, 1, (r.Mask & 2) != 0); Add(2, 2, (r.Mask & 8) != 0); Add(3, 3, (r.Mask & 0x10) != 0); Add(4, 3, (r.Mask & 0x20) != 0);
        }
        var byCam = recs.ToDictionary(r => r, r => blocks.Where(b => b.Cam == r).ToDictionary(b => b.Kind));
        // residual blocks in insertion order
        var res = new List<ResBlock>();
        var byKey = recs.ToDictionary(r => r.Key);
        for (int i = 0; i < n; i++)
            foreach (var kv in Observations[i].Uv)
            {
                if (!byKey.TryGetValue(kv.Key, out var cam)) throw new ArgumentOutOfRangeException("invalid map<K, T> key");
                res.Add(new ResBlock { Kind = 0, Cam = cam, Data = new[] { (double)kv.Value.U, (double)kv.Value.V }, Pt = pts[i] });
            }
        if (UseEntrancePupilPrior)
            foreach (var r in recs)
            {
                var neg = new[] { -r.Aa[0], -r.Aa[1], -r.Aa[2] }; var c = RotatePoint(neg, r.T);
                res.Add(new ResBlock { Kind = 1, Cam = r, Data = new[] { n * 0.01, n * 5.0, c[0], c[1], c[2] } });
            }
        if (UseIntrinsicsPrior)
            foreach (var r in recs) res.Add(new ResBlock { Kind = 2, Cam = r, Data = new[] { n * 0.00125, n * 0.00125, r.F, r.Cx, r.Cy } });
        // drop residual blocks whose parameters are all constant (RemoveFixedBlocksFromProgram); if nothing variable, nothing to do
        bool AnyVar(ResBlock rb) { var d = byCam[rb.Cam]; return rb.Kind switch { 0 => d[0].Index >= 0 || d[1].Index >= 0 || d[2].Index >= 0 || d[3].Index >= 0 || d[4].Index >= 0, 1 => d[3].Index >= 0 || d[4].Index >= 0, _ => d[0].Index >= 0 || d[2].Index >= 0 }; }
        res = res.Where(AnyVar).ToList();
        if (np == 0 || res.Count == 0) return;
        int nr = res.Sum(rb => rb.Kind == 0 ? 2 : 3);
        // parameter vector x
        double[] Pack() { var x = new double[np]; foreach (var b in blocks) { if (b.Index < 0) continue; switch (b.Kind) { case 0: x[b.Index] = b.Cam.F; break; case 1: x[b.Index] = b.Cam.Aspect; break; case 2: x[b.Index] = b.Cam.Cx; x[b.Index + 1] = b.Cam.Cy; break; case 3: for (int k = 0; k < 3; k++) x[b.Index + k] = b.Cam.Aa[k]; break; default: for (int k = 0; k < 3; k++) x[b.Index + k] = b.Cam.T[k]; break; } } return x; }
        void Unpack(double[] x) { foreach (var b in blocks) { if (b.Index < 0) continue; switch (b.Kind) { case 0: b.Cam.F = x[b.Index]; break; case 1: b.Cam.Aspect = x[b.Index]; break; case 2: b.Cam.Cx = x[b.Index]; b.Cam.Cy = x[b.Index + 1]; break; case 3: for (int k = 0; k < 3; k++) b.Cam.Aa[k] = x[b.Index + k]; break; default: for (int k = 0; k < 3; k++) b.Cam.T[k] = x[b.Index + k]; break; } } }
        Dual D(double a, int idx) { var d = new Dual(a, np); if (idx >= 0) d.V[idx] = 1.0; return d; }
        // evaluate residuals (and Jacobian)
        double Evaluate(double[] x, double[] r, double[,]? J)
        {
            Unpack(x); int row = 0; double cost = 0;
            foreach (var rb in res)
            {
                var d = byCam[rb.Cam]; var c = rb.Cam;
                Dual f = D(c.F, d[0].Index), asp = D(c.Aspect, d[1].Index), cx = D(c.Cx, d[2].Index), cy = D(c.Cy, d[2].Index < 0 ? -1 : d[2].Index + 1);
                var aa = new[] { D(c.Aa[0], d[3].Index), D(c.Aa[1], d[3].Index < 0 ? -1 : d[3].Index + 1), D(c.Aa[2], d[3].Index < 0 ? -1 : d[3].Index + 2) };
                var t = new[] { D(c.T[0], d[4].Index), D(c.T[1], d[4].Index < 0 ? -1 : d[4].Index + 1), D(c.T[2], d[4].Index < 0 ? -1 : d[4].Index + 2) };
                Dual[] outp;
                if (rb.Kind == 0)
                {
                    var X = new[] { D(rb.Pt[0], -1), D(rb.Pt[1], -1), D(rb.Pt[2], -1) };
                    var p = RotatePointD(aa, X, np);
                    Dual xx = t[0] + p[0], yy = t[1] + p[1], zz = p[2] + t[2];
                    Dual inv = 1.0 / zz;
                    outp = new[] { (cx - rb.Data[0]) + f * (xx * inv), (cy - rb.Data[1]) + asp * ((yy * f) * inv) };
                }
                else if (rb.Kind == 1)
                {
                    var neg = new[] { -aa[0], -aa[1], -aa[2] }; var p = RotatePointD(neg, t, np);
                    outp = new[] { (p[0] - rb.Data[2]) * rb.Data[0], (p[1] - rb.Data[3]) * rb.Data[0], (p[2] - rb.Data[4]) * rb.Data[1] };
                }
                else outp = new[] { (f - rb.Data[2]) * rb.Data[0], (cx - rb.Data[3]) * rb.Data[1], (cy - rb.Data[4]) * rb.Data[1] };
                foreach (var o in outp) { r[row] = o.A; cost += 0.5 * o.A * o.A; if (J is not null) for (int k = 0; k < np; k++) J[row, k] = o.V[k]; row++; }
            }
            return cost;
        }
        // ---- TrustRegionMinimizer (Ceres 1.12) ----
        const double InitialRadius = 1e4, MaxRadius = 1e16, MinRadius = 1e-32, MinRelativeDecrease = 1e-3, MinLmDiagonal = 1e-6, MaxLmDiagonal = 1e32, ParameterTolerance = 1e-8, FunctionTolerance = 1e-20, GradientTolerance = 1e-20;
        const int MaxIterations = 100, MaxConsecutiveInvalid = 5, MaxConsecutiveNonmonotonic = 5;
        var x0 = Pack(); var residuals = new double[nr]; var J = new double[nr, np];
        double xCost = Evaluate(x0, residuals, J);
        var scaling = new double[np];
        for (int k = 0; k < np; k++) { double ss = 0; for (int i = 0; i < nr; i++) ss += J[i, k] * J[i, k]; scaling[k] = 1.0 / (1.0 + Math.Sqrt(ss)); }
        void ScaleJ() { for (int i = 0; i < nr; i++) for (int k = 0; k < np; k++) J[i, k] *= scaling[k]; }
        double[] Gradient() { var g = new double[np]; for (int k = 0; k < np; k++) { double a = 0; for (int i = 0; i < nr; i++) a += J[i, k] * residuals[i]; g[k] = a; } return g; }
        var gradient = Gradient(); ScaleJ();
        double gradientMaxNorm = gradient.Max(v => Math.Abs(v));
        double xNorm = Math.Sqrt(x0.Sum(v => v * v));
        var x = (double[])x0.Clone(); var best = (double[])x0.Clone(); double minimumCost = double.MaxValue;
        double seMin = xCost, seCur = xCost, seRef = xCost, seCand = xCost, accCand = 0, accRef = 0; int nonmono = 0;
        double radius = InitialRadius, decreaseFactor = 2.0; bool reuseDiagonal = false; var diag = new double[np];
        int iteration = 0, consecutiveInvalid = 0; bool stepSuccessful = true;
        while (true)
        {
            if (stepSuccessful && xCost < minimumCost) { minimumCost = xCost; best = (double[])x.Clone(); }
            if (iteration >= MaxIterations) break;
            if (stepSuccessful && gradientMaxNorm <= GradientTolerance) break;
            if (radius <= MinRadius) break;
            iteration++; stepSuccessful = false;
            if (!reuseDiagonal) for (int k = 0; k < np; k++) { double ss = 0; for (int i = 0; i < nr; i++) ss += J[i, k] * J[i, k]; diag[k] = Math.Min(Math.Max(ss, MinLmDiagonal), MaxLmDiagonal); }
            // (JᵀJ + diag(diag/radius)) δ = −Jᵀr on the scaled Jacobian
            var A = new double[np, np]; var b = new double[np];
            for (int k = 0; k < np; k++) { for (int l = 0; l <= k; l++) { double a = 0; for (int i = 0; i < nr; i++) a += J[i, k] * J[i, l]; A[k, l] = a; A[l, k] = a; } double g = 0; for (int i = 0; i < nr; i++) g += J[i, k] * residuals[i]; b[k] = -g; A[k, k] += diag[k] / radius; }
            var stepScaled = Cholesky(A, b, np); reuseDiagonal = true;
            bool stepValid = stepScaled is not null; double modelCostChange = 0; var delta = new double[np];
            if (stepValid)
            {
                double mc = 0; for (int i = 0; i < nr; i++) { double mm = 0; for (int k = 0; k < np; k++) mm += J[i, k] * stepScaled![k]; mc += mm * (residuals[i] + mm / 2.0); }
                modelCostChange = -mc; stepValid = modelCostChange > 0.0;
                if (stepValid) { for (int k = 0; k < np; k++) delta[k] = stepScaled![k] * scaling[k]; consecutiveInvalid = 0; }
            }
            if (!stepValid) { if (++consecutiveInvalid >= MaxConsecutiveInvalid) break; radius /= decreaseFactor; decreaseFactor *= 2.0; reuseDiagonal = true; continue; }
            var cand = new double[np]; for (int k = 0; k < np; k++) cand[k] = x[k] + delta[k];
            var candRes = new double[nr]; double candidateCost = Evaluate(cand, candRes, null);
            double stepNorm = Math.Sqrt(delta.Sum(v => v * v));
            if (stepNorm <= ParameterTolerance * (xNorm + ParameterTolerance)) break;
            double costChange = xCost - candidateCost;
            if (Math.Abs(costChange) <= FunctionTolerance * xCost) break;
            double relativeDecrease = Math.Max((seCur - candidateCost) / modelCostChange, (seRef - candidateCost) / (accRef + modelCostChange));
            if (relativeDecrease > MinRelativeDecrease)
            {
                x = cand; xNorm = Math.Sqrt(x.Sum(v => v * v));
                xCost = Evaluate(x, residuals, J); gradient = Gradient(); ScaleJ(); gradientMaxNorm = gradient.Max(v => Math.Abs(v));
                stepSuccessful = true;
                radius = radius / Math.Max(1.0 / 3.0, 1.0 - Math.Pow(2.0 * relativeDecrease - 1.0, 3)); radius = Math.Min(MaxRadius, radius); decreaseFactor = 2.0; reuseDiagonal = false;
                seCur = candidateCost; accCand += modelCostChange; accRef += modelCostChange;
                if (seCur < seMin) { seMin = seCur; nonmono = 0; seCand = seCur; accCand = 0; }
                else { nonmono++; if (seCur > seCand) { seCand = seCur; accCand = 0; } }
                if (nonmono == MaxConsecutiveNonmonotonic) { seRef = seCand; accRef = accCand; }
            }
            else { radius /= decreaseFactor; decreaseFactor *= 2.0; reuseDiagonal = true; }
        }
        Unpack(best);
        log?.Invoke($"LightBA: {np} params, {nr} residuals, {iteration} iterations, cost {minimumCost:G9}");
    }

    static double[]? Cholesky(double[,] A, double[] b, int n)
    {
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = A[i, j]; for (int k = 0; k < j; k++) s -= L[i, k] * L[j, k];
                if (i == j) { if (!(s > 0)) return null; L[i, i] = Math.Sqrt(s); } else L[i, j] = s / L[j, j];
            }
        var y = new double[n]; for (int i = 0; i < n; i++) { double s = b[i]; for (int k = 0; k < i; k++) s -= L[i, k] * y[k]; y[i] = s / L[i, i]; }
        var x = new double[n]; for (int i = n - 1; i >= 0; i--) { double s = y[i]; for (int k = i + 1; k < n; k++) s -= L[k, i] * x[k]; x[i] = s / L[i, i]; }
        return x;
    }
}
