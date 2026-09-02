namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `Triangulator::refine3dPoints` (180288db0 + lambda_0 18028c150): per point a 1-DOF Ceres 1.12.0 problem (depth `d` along
/// the reference ray, bounds [near, far]) with one `AutoDiffCostFunction&lt;ReProjectionCost,2,1&gt;` + `CauchyLoss(1.0)` per
/// observing camera, solved with the trust-region Levenberg–Marquardt path of Ceres 1.12.0 (DENSE_QR, jacobi scaling,
/// nonmonotonic steps, projected Armijo line search for the bounded problem). The solver here is a transcription of that
/// path with the same arithmetic order; the per-camera 4×4 matrices `D` are `FUN_1803010a0` results rounded through float.
/// </summary>
public static class DepthRefine
{
    const int MaxIterations = 50;
    const double InitialRadius = 1e4, MaxRadius = 1e16, MinRadius = 1e-32, MinRelativeDecrease = 1e-3;
    const double MinLmDiagonal = 1e-6, MaxLmDiagonal = 1e32, FunctionTolerance = 1e-6, GradientTolerance = 1e-10, ParameterTolerance = 1e-8;
    const int MaxConsecutiveNonmonotonic = 5, MaxConsecutiveInvalid = 5;

    public sealed class Camera { public double[] D = new double[16]; public double ObsU, ObsV; }

    /// <summary>Eigen SSE2 linear-vectorised reduction order for a term list (packet size 2).</summary>
    public static double Sum(ReadOnlySpan<double> t)
    {
        int n = t.Length;
        if (n == 0) return 0.0;
        if (n < 4)
        {
            double s = t[0]; for (int i = 1; i < n; i++) s = s + t[i]; return s;
        }
        int alignedEnd = n & ~1, alignedEnd2 = n & ~3;
        double p00 = t[0], p01 = t[1], p10 = t[2], p11 = t[3];
        for (int i = 4; i < alignedEnd2; i += 4) { p00 += t[i]; p01 += t[i + 1]; p10 += t[i + 2]; p11 += t[i + 3]; }
        p00 += p10; p01 += p11;
        if (alignedEnd2 != alignedEnd) { p00 += t[alignedEnd2]; p01 += t[alignedEnd2 + 1]; }
        double res = p00 + p01;
        for (int i = alignedEnd; i < n; i++) res = res + t[i];
        return res;
    }

    /// <summary>Residuals (double path) and the Jet-path values + derivative for one camera.</summary>
    static void Residual(Camera c, double u, double v, double d, out double r0, out double r1)
    {
        var D = c.D; double ud = u * d, vd = v * d;
        double p0 = ((D[1] * vd + D[0] * ud) + D[2] * d) + D[3];
        double p1 = ((D[5] * vd + D[4] * ud) + D[6] * d) + D[7];
        double p2 = ((D[9] * vd + D[8] * ud) + D[10] * d) + D[11];
        double w = 1.0 / p2;
        r0 = p0 * w - c.ObsU; r1 = w * p1 - c.ObsV;
    }
    static void ResidualJet(Camera c, double u, double v, double d, out double r0, out double r1, out double j0, out double j1)
    {
        var D = c.D; double ud = u * d, vd = v * d;
        double p0 = D[2] * d + (D[0] * ud + (D[1] * vd + D[3] * 1.0)), p0v = D[0] * u + D[1] * v + D[2];
        double p1 = D[6] * d + (D[4] * ud + (D[5] * vd + D[7] * 1.0)), p1v = D[4] * u + D[5] * v + D[6];
        double p2 = D[10] * d + (D[8] * ud + (D[9] * vd + D[11] * 1.0)), p2v = D[8] * u + D[9] * v + D[10];
        double w = 1.0 / p2;
        r0 = p0 * w - c.ObsU; j0 = p0v * w - p0 * ((w * w) * p2v);
        r1 = w * p1 - c.ObsV; j1 = p1v * w - p1 * ((w * w) * p2v);
    }

    /// <summary>ProgramEvaluator::Evaluate — cost (Cauchy), corrected residuals / Jacobian column, gradient. `jet` selects the Jet-path values.</summary>
    static bool Evaluate(Camera[] cams, double u, double v, double d, bool jet, out double cost, double[]? residuals, double[]? jacobian, out double gradient)
    {
        double scost = 0.0, sgrad = 0.0; cost = 0; gradient = 0;
        for (int i = 0; i < cams.Length; i++)
        {
            double r0, r1, j0 = 0, j1 = 0;
            if (jet) ResidualJet(cams[i], u, v, d, out r0, out r1, out j0, out j1); else Residual(cams[i], u, v, d, out r0, out r1);
            if (!(double.IsFinite(r0) && double.IsFinite(r1)) || r0 == 1e302 || r1 == 1e302) return false;
            if (jet && !(double.IsFinite(j0) && double.IsFinite(j1))) return false;
            double sq = (r0 * r0) + (r1 * r1);
            double sum = 1.0 + sq * 1.0, inv = 1.0 / sum;
            double rho0 = 1.0 * Math.Log(sum), rho1 = Math.Max(2.2250738585072014e-308, inv);
            double bc = 0.5 * rho0;
            double sqrtRho1 = Math.Sqrt(rho1);
            double cr0 = r0 * sqrtRho1, cr1 = r1 * sqrtRho1, cj0 = j0 * sqrtRho1, cj1 = j1 * sqrtRho1;
            scost = scost + bc;
            if (residuals != null) { residuals[2 * i] = cr0; residuals[2 * i + 1] = cr1; }
            if (jacobian != null) { jacobian[2 * i] = cj0; jacobian[2 * i + 1] = cj1; }
            if (jet)
            {
                double tmp = 0.0; tmp = tmp + cj0 * cr0; tmp = tmp + cj1 * cr1;
                sgrad = sgrad + tmp;
            }
        }
        cost = 0.0 + scost; gradient = 0.0 + sgrad;
        return true;
    }

    static double Plus(double x, double delta, double lo, double hi) { double xp = x + delta; if (xp < lo) xp = lo; if (hi < xp) xp = hi; return xp; }

    /// <summary>Solve for the depth of one point; returns the final `d` (unchanged on FAILURE, as Ceres leaves the user parameter).</summary>
    public static double Solve(Camera[] cams, double u, double v, double d0, double near, double far)
    {
        int n = 2 * cams.Length;
        var residuals = new double[n]; var jac = new double[n];
        double x = Plus(d0, 0.0, near, far);
        double xNorm = Math.Sqrt(x * x);
        double xCost, gradient;
        if (!Evaluate(cams, u, v, x, true, out xCost, residuals, jac, out gradient)) return d0;
        double scaling = 1.0;
        double[] sq = new double[n + 1]; for (int i = 0; i < n; i++) sq[i] = jac[i] * jac[i]; sq[n] = 0.0;
        scaling = 1.0 / (1.0 + Math.Sqrt(Sum(sq)));
        for (int i = 0; i < n; i++) jac[i] = jac[i] * scaling;
        double pgs = Plus(x, -gradient, near, far);
        double gradientMaxNorm = Math.Abs(x - pgs);
        double parameters = x, minimumCost = double.MaxValue;
        // step evaluator
        double seMin = xCost, seCur = xCost, seRef = xCost, seCand = xCost, accCand = 0, accRef = 0; int nonmono = 0;
        double radius = InitialRadius, decreaseFactor = 2.0; bool reuseDiagonal = false; double diag = 0;
        int iteration = 0, consecutiveInvalid = 0; bool stepSuccessful = true;
        while (true)
        {
            // FinalizeIterationAndCheckIfMinimizerCanContinue
            if (stepSuccessful && xCost < minimumCost) { minimumCost = xCost; parameters = x; }
            if (iteration >= MaxIterations) break;
            if (stepSuccessful && gradientMaxNorm <= GradientTolerance) break;
            if (radius <= MinRadius) break;
            iteration++;
            stepSuccessful = false;
            // ComputeTrustRegionStep
            if (!reuseDiagonal)
            {
                for (int i = 0; i < n; i++) sq[i] = jac[i] * jac[i]; sq[n] = 0.0;
                diag = Sum(sq); diag = Math.Min(Math.Max(diag, MinLmDiagonal), MaxLmDiagonal);
            }
            double lm = Math.Sqrt(diag / radius);
            // HouseholderQR on a = [jac_0..jac_{n-1}, lm], rhs c = [res_0..res_{n-1}, 0]
            var tail = new double[n]; for (int i = 1; i < n; i++) tail[i - 1] = jac[i] * jac[i]; tail[n - 1] = lm * lm;
            double tailSq = Sum(tail);
            double c0 = jac[0];
            double beta = Math.Sqrt(c0 * c0 + tailSq); if (c0 >= 0) beta = -beta;
            var e = new double[n]; for (int i = 1; i < n; i++) e[i - 1] = jac[i] / (c0 - beta); e[n - 1] = lm / (c0 - beta);
            double tau = (beta - c0) / beta;
            var prods = new double[n]; for (int i = 1; i < n; i++) prods[i - 1] = e[i - 1] * residuals[i]; prods[n - 1] = e[n - 1] * 0.0;
            double tt = Sum(prods); tt = tt + residuals[0];
            double cc0 = residuals[0] - tau * tt;
            double y = cc0 / beta;
            double stepScaled = -y;
            reuseDiagonal = true;
            bool stepValid;
            double modelCostChange = 0, delta = 0;
            if (!double.IsFinite(y) || y == 1e302) { stepValid = false; }
            else
            {
                var terms = new double[n];
                for (int i = 0; i < n; i++) { double m = 0.0 + jac[i] * stepScaled; terms[i] = m * (residuals[i] + (m / 2.0)); }
                modelCostChange = -Sum(terms);
                stepValid = modelCostChange > 0.0;
                if (stepValid) { delta = stepScaled * scaling; consecutiveInvalid = 0; }
            }
            if (!stepValid)
            {
                if (++consecutiveInvalid >= MaxConsecutiveInvalid) return d0;   // FAILURE: user parameter untouched
                radius = radius / decreaseFactor; decreaseFactor *= 2.0; reuseDiagonal = true;
                continue;
            }
            // DoLineSearch (projected Armijo, cubic interpolation) — the full step is tested first
            delta = LineSearch(cams, u, v, x, xCost, gradient, delta, near, far);
            // candidate
            double candidateX = Plus(x, delta, near, far);
            double candidateCost;
            if (!Evaluate(cams, u, v, candidateX, false, out candidateCost, null, null, out _)) candidateCost = double.MaxValue;
            double stepNorm = Math.Abs(x - candidateX);
            if (stepNorm <= ParameterTolerance * (xNorm + ParameterTolerance)) break;
            double costChange = xCost - candidateCost;
            if (Math.Abs(costChange) <= FunctionTolerance * xCost) break;
            double rd = (seCur - candidateCost) / modelCostChange;
            double hrd = (seRef - candidateCost) / (accRef + modelCostChange);
            double relativeDecrease = Math.Max(rd, hrd);
            if (relativeDecrease > MinRelativeDecrease)
            {
                x = candidateX; xNorm = Math.Sqrt(x * x);
                if (!Evaluate(cams, u, v, x, true, out xCost, residuals, jac, out gradient)) return d0;
                for (int i = 0; i < n; i++) jac[i] = jac[i] * scaling;
                pgs = Plus(x, -gradient, near, far); gradientMaxNorm = Math.Abs(x - pgs);
                stepSuccessful = true;
                radius = radius / Math.Max(1.0 / 3.0, 1.0 - Math.Pow(2.0 * relativeDecrease - 1.0, 3));
                radius = Math.Min(MaxRadius, radius); decreaseFactor = 2.0; reuseDiagonal = false;
                // step evaluator accept
                seCur = candidateCost; accCand += modelCostChange; accRef += modelCostChange;
                if (seCur < seMin) { seMin = seCur; nonmono = 0; seCand = seCur; accCand = 0; }
                else { nonmono++; if (seCur > seCand) { seCand = seCur; accCand = 0; } }
                if (nonmono == MaxConsecutiveNonmonotonic) { seRef = seCand; accRef = accCand; }
            }
            else
            {
                radius = radius / decreaseFactor; decreaseFactor = decreaseFactor * 2.0; reuseDiagonal = true;
            }
        }
        return parameters;
    }

    /// <summary>Ceres `ArmijoLineSearch` with CUBIC interpolation as used by the constrained trust-region minimizer.</summary>
    static double LineSearch(Camera[] cams, double u, double v, double x, double f0, double gradient, double delta, double near, double far)
    {
        double g0 = gradient * delta, dirInf = Math.Abs(delta);
        (double F, double G, bool Valid) Eval(double s)
        {
            double xs = Plus(x, s * delta, near, far);
            bool ok = Evaluate(cams, u, v, xs, true, out double f, null, null, out double grad);
            double g = delta * grad;
            return (f, g, ok && double.IsFinite(f) && double.IsFinite(g));
        }
        double curX = 1.0; var cur = Eval(1.0);
        (double X, double F, double G, bool Valid)? prev = null;
        int iter = 0;
        while (!cur.Valid || cur.F > f0 + (1e-4 * g0) * curX)
        {
            if (++iter >= 20) return delta;
            double xmin = 1e-3 * curX, xmax = 0.6 * curX, step;
            if (!cur.Valid) step = Math.Min(Math.Max(0.5 * curX, xmin), xmax);
            else
            {
                var xs = new List<double> { 0.0, curX }; var fs = new List<double> { f0, cur.F }; var gs = new List<double> { g0, cur.G };
                if (prev != null && prev.Value.Valid) { xs.Add(prev.Value.X); fs.Add(prev.Value.F); gs.Add(prev.Value.G); }
                step = MinimizeInterpolant(xs, fs, gs, xmin, xmax);
            }
            if (step * dirInf < 1e-9) return delta;
            prev = (curX, cur.F, cur.G, cur.Valid);
            curX = step; cur = Eval(step);
        }
        return delta * curX;
    }

    /// <summary>Polynomial interpolation of (x, f, f') samples via full-pivot LU (Ceres `FindInterpolatingPolynomial`) and its constrained minimiser.</summary>
    static double MinimizeInterpolant(List<double> xs, List<double> fs, List<double> gs, double xmin, double xmax)
    {
        int m = xs.Count, deg = 2 * m - 1, N = deg + 1;
        var A = new double[N, N]; var b = new double[N];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < N; j++) A[2 * i, j] = Math.Pow(xs[i], deg - j);
            b[2 * i] = fs[i];
            for (int j = 0; j < N; j++) A[2 * i + 1, j] = j < deg ? (deg - j) * Math.Pow(xs[i], deg - j - 1) : 0.0;
            b[2 * i + 1] = gs[i];
        }
        var poly = FullPivLuSolve(A, b, N);
        double Horner(double[] p, double x) { double vv = 0; foreach (var c in p) vv = vv * x + c; return vv; }
        double bestX = (xmin + xmax) / 2.0, best = Horner(poly, bestX);
        if (Horner(poly, xmin) < best) { best = Horner(poly, xmin); bestX = xmin; }
        if (Horner(poly, xmax) < best) { best = Horner(poly, xmax); bestX = xmax; }
        var der = new double[N - 1]; for (int i = 0; i < N - 1; i++) der[i] = (deg - i) * poly[i];
        foreach (double r in RealRoots(der))
            if (r >= xmin && r <= xmax && Horner(poly, r) < best) { best = Horner(poly, r); bestX = r; }
        for (int i = 0; i < m; i++) if (xs[i] >= xmin && xs[i] <= xmax && Horner(poly, xs[i]) < best) { best = Horner(poly, xs[i]); bestX = xs[i]; }
        return bestX;
    }

    static IEnumerable<double> RealRoots(double[] p)
    {
        // strip leading zeros
        int s = 0; while (s < p.Length - 1 && p[s] == 0.0) s++;
        var q = p.Skip(s).ToArray();
        if (q.Length == 3)
        {
            double a = q[0], bb = q[1], c = q[2], D = bb * bb - 4 * a * c, sqrtD = Math.Sqrt(Math.Abs(D));
            if (D < 0) { yield return -bb / (2 * a); yield return -bb / (2 * a); yield break; }
            if (bb >= 0) { yield return (-bb - sqrtD) / (2 * a); yield return (2 * c) / (-bb - sqrtD); }
            else { yield return (2 * c) / (-bb + sqrtD); yield return (-bb + sqrtD) / (2 * a); }
            yield break;
        }
        if (q.Length == 2) { yield return -q[1] / q[0]; yield break; }
        if (q.Length <= 1) yield break;
        // higher degree (only on a second Armijo contraction): companion-matrix eigenvalues via a simple QR iteration — UNCERTAIN path
        foreach (var r in CompanionRealRoots(q)) yield return r;
    }

    static IEnumerable<double> CompanionRealRoots(double[] q)
    {
        int nn = q.Length - 1; var M = new double[nn, nn];
        for (int j = 0; j < nn; j++) M[0, j] = -q[j + 1] / q[0];
        for (int i = 1; i < nn; i++) M[i, i - 1] = 1.0;
        // unshifted QR iterations (adequate for root extraction; exact Eigen ordering not reproduced)
        for (int it = 0; it < 500; it++)
        {
            var Q = new double[nn, nn]; var R = new double[nn, nn];
            for (int j = 0; j < nn; j++)
            {
                var vcol = new double[nn]; for (int i = 0; i < nn; i++) vcol[i] = M[i, j];
                for (int k = 0; k < j; k++) { double dot = 0; for (int i = 0; i < nn; i++) dot += Q[i, k] * M[i, j]; R[k, j] = dot; for (int i = 0; i < nn; i++) vcol[i] -= dot * Q[i, k]; }
                double nrm = 0; for (int i = 0; i < nn; i++) nrm += vcol[i] * vcol[i]; nrm = Math.Sqrt(nrm); R[j, j] = nrm;
                for (int i = 0; i < nn; i++) Q[i, j] = nrm == 0 ? 0 : vcol[i] / nrm;
            }
            var M2 = new double[nn, nn];
            for (int i = 0; i < nn; i++) for (int j = 0; j < nn; j++) { double sum = 0; for (int k = 0; k < nn; k++) sum += R[i, k] * Q[k, j]; M2[i, j] = sum; }
            M = M2;
        }
        for (int i = 0; i < nn; i++) { bool complexPair = i + 1 < nn && Math.Abs(M[i + 1, i]) > 1e-9; if (complexPair) { i++; continue; } yield return M[i, i]; }
    }

    static double[] FullPivLuSolve(double[,] A, double[] b, int N)
    {
        var a = (double[,])A.Clone(); var rowP = new int[N]; var colP = new int[N];
        for (int i = 0; i < N; i++) { rowP[i] = i; colP[i] = i; }
        for (int k = 0; k < N; k++)
        {
            int pr = k, pc = k; double mx = -1;
            for (int j = k; j < N; j++) for (int i = k; i < N; i++) if (Math.Abs(a[i, j]) > mx) { mx = Math.Abs(a[i, j]); pr = i; pc = j; }
            if (pr != k) { for (int j = 0; j < N; j++) (a[k, j], a[pr, j]) = (a[pr, j], a[k, j]); (rowP[k], rowP[pr]) = (rowP[pr], rowP[k]); }
            if (pc != k) { for (int i = 0; i < N; i++) (a[i, k], a[i, pc]) = (a[i, pc], a[i, k]); (colP[k], colP[pc]) = (colP[pc], colP[k]); }
            if (a[k, k] == 0.0) continue;
            for (int i = k + 1; i < N; i++) a[i, k] = a[i, k] / a[k, k];
            for (int i = k + 1; i < N; i++) for (int j = k + 1; j < N; j++) a[i, j] = a[i, j] - (a[i, k] * a[k, j]);
        }
        var c = new double[N]; for (int i = 0; i < N; i++) c[i] = b[rowP[i]];
        for (int i = 0; i < N; i++) for (int j = i + 1; j < N; j++) c[j] -= c[i] * a[j, i];
        for (int i = N - 1; i >= 0; i--) { c[i] /= a[i, i]; for (int j = 0; j < i; j++) c[j] -= c[i] * a[j, i]; }
        var xsol = new double[N]; for (int i = 0; i < N; i++) xsol[colP[i]] = c[i];
        return xsol;
    }

    /// <summary>Whole `refine3dPoints`: for every point with Z &gt; 0 build the problem from the enabled observing cameras and solve.</summary>
    public static TriPoint[] Refine(TriPoint[] pts, CalibData refCam, CalibData[] cams, float[][] obs, float near, float far)
    {
        if (pts.Length <= 15) return pts;
        var Dmats = new double[cams.Length][];
        for (int c = 0; c < cams.Length; c++)
        {
            var M = Mat4D.FlowMatrix(refCam, cams[c]);   // column-major float
            var D = new double[16]; for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) D[4 * r + k] = (double)M[4 * k + r];
            Dmats[c] = D;
        }
        var Kinv = Triangulator.Inv3(refCam.K); var Rinv = Triangulator.Inv3(refCam.R); float[] tr = refCam.T, R = refCam.R;
        var outp = (TriPoint[])pts.Clone();
        for (int i = 0; i < pts.Length; i++)
        {
            var p = pts[i]; if (!(p.Z > 0f)) continue;
            double d0 = (double)((p.Z * R[8] + tr[2]) + (R[7] * p.Y + R[6] * p.X));
            var camsUsed = new List<Camera>();
            for (int c = 0; c < cams.Length; c++)
            {
                float ou = obs[c][2 * i], ov = obs[c][2 * i + 1];
                if (!(ou > 0f && ov > 0f)) continue;
                camsUsed.Add(new Camera { D = Dmats[c], ObsU = ou, ObsV = ov });
            }
            double d = Solve(camsUsed.ToArray(), p.U, p.V, d0, near, far);
            float df = (float)d;
            float rx = (Kinv[1] * p.V + Kinv[0] * p.U) + Kinv[2], ry = (Kinv[4] * p.V + Kinv[3] * p.U) + Kinv[5], rz = (Kinv[7] * p.V + Kinv[6] * p.U) + Kinv[8];
            float Xc = rx * df, Yc = ry * df, Zc = rz * df;
            float dx = Xc - tr[0], dy = Yc - tr[1], dz = Zc - tr[2];
            outp[i].X = Rinv[2] * dz + (Rinv[1] * dy + Rinv[0] * dx);
            outp[i].Y = Rinv[5] * dz + (Rinv[4] * dy + Rinv[3] * dx);
            outp[i].Z = Rinv[8] * dz + (Rinv[7] * dy + Rinv[6] * dx);
        }
        return outp;
    }
}
