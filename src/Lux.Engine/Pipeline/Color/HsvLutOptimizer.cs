using Ltpb;
using static Lux.Engine.Pipeline.Color.LumenColorTables;

namespace Lux.Engine.Pipeline.Color;

/// <summary>Result of the per-illuminant colour fit (SoT §9.3): the fitted camera-RGB→XYZ(D50) matrix, the DNG
/// ForwardMatrix derived from it, and the 32×32×1 HSV map stored Lumen-style as a padded 33×33 vec4 grid.</summary>
public sealed record ColorFit(
    float[] Matrix,          // row-major 3×3, xyz = M·rgb (Lumen M', column-major read L768–779 of OHL)
    float[] InitialMatrix,   // the weighted-LSQ start M₀ (row-major)
    float[] ForwardMatrix,   // row-major 3×3 = M'·diag(M'⁻¹·XYZ_D50)
    float[] Grid,            // 33×33 vec4, index (h + 33·s)·4, cell = (1+Δh, s_ratio, v_ratio, 0)
    bool Converged, int Iterations, double Cost, double InitialCost, string Termination)
{
    public const int HueDivisions = 32, SatDivisions = 32, ValDivisions = 1;

    /// <summary>DNG ProfileHueSatMapData encoding (`FUN_18017bd30`): hue outer, sat inner, per entry
    /// ((cell.x − 1)·360 in double → float, cell.y, cell.z).</summary>
    public float[] ToDngHueSatMap()
    {
        var o = new float[HueDivisions * SatDivisions * ValDivisions * 3];
        int k = 0;
        for (int h = 0; h < HueDivisions; h++)
            for (int s = 0; s < SatDivisions; s++)
            {
                int c = (h + (HueDivisions + 1) * s) * 4;
                o[k++] = (float)(((double)Grid[c] + -1.0) * 360.0);   // DAT_1806a29e0 = −1.0, DAT_1806a29e8 = 360.0
                o[k++] = Grid[c + 1];
                o[k++] = Grid[c + 2];
            }
        return o;
    }
}

/// <summary>
/// Port of `lt::A::OptimizeHSVLut` (`FUN_18014f4b0`) and the ForwardMatrix step of `FUN_18014b500` (SoT §9.3):
/// ColorChecker camera RGB → ΔE2000-optimal RGB→XYZ matrix (BFGS over Lab), ForwardMatrix, and the thin-plate-spline
/// HueSatMap. Constants are cited to their `.rdata` addresses.
/// </summary>
public static class HsvLutOptimizer
{
    public const float HueDeltaClamp = 0.027777778f;   // DAT_1806a07f0 / 07fc (±10° in turns)
    public const float SatRatioLo = 0.9f, SatRatioHi = 1.1f;        // DAT_1806a07f4 / 0800
    public const float ValRatioLo = 0.975f, ValRatioHi = 1.025f;    // DAT_1806a07f8 / 0804
    public const double LambdaScale = 0.001500000013038516;         // DAT_1806a0810 = (double)0.0015f
    public const double AnchorHueStep = 0.16666666666666666;        // DAT_1806a0808 = 1/6
    public const double LabEpsilon = 0.008856451679035631;          // DAT_1806a0900 (216/24389)
    public const double LabKappa = 7.787037037037037;               // DAT_1806a0910 (841/108)
    public const double LabOffset = 0.13793103448275862;            // DAT_1806a0918 (16/116)
    public const int WhitePatch = 18;                                // patch 19 (1-based), OHL L205–212
    public const double NeutralSatEpsilon = 0.0001;                  // DAT_1806a0ad8, the `_Do_call` valScale-1 threshold

    /// <summary>Full fit for one ColorCalibration entry's 24 `macbeth_data` points. Spec a-fm-refit.md.</summary>
    public static ColorFit Fit(IReadOnlyList<Point3F> macbeth, Action<string>? log = null, CeresLineSearchOptions? options = null)
    {
        if (macbeth.Count != 24) throw new InvalidOperationException("Missing calibration data points");   // 18014b500 L80
        options ??= new CeresLineSearchOptions();
        var prep = Prepare(macbeth);
        var res = CeresLineSearchMinimizer.Minimize(prep.Evaluate, prep.X0, options);
        var p = res.Termination == CeresTermination.Convergence ? res.X : prep.X0;   // OHL L571: keep M₀ unless Ceres reports CONVERGENCE
        if (res.Termination != CeresTermination.Convergence) log?.Invoke($"colour fit did not converge ({res.Message}); using the linear LSQ matrix");
        return Finish(prep, p, res);
    }

    /// <summary>Everything `OptimizeHSVLut` builds before `ceres::Solve`: the float reference chart (Lab + round-tripped XYZ), the white-scaled
    /// camera RGB (design matrix A, 25×3 with the 1e-6 row), the Lab white reciprocals, the LSQ start M₀ and the Ceres-side evaluator.</summary>
    public sealed class Prepared
    {
        public float[] Cam = new float[24 * 3];        // white-scaled camera RGB (float, OHL L205–212)
        public double[] A = new double[25 * 3];        // design matrix, row-major here (Lumen stores it column-major)
        public double[] RefLab = new double[25 * 3];   // reference Lab (float → double, row 24 = 1e-6)
        public double[] Weights = new double[25];      // 24 × 1.0, then 0
        public float[] RefXyz = new float[24 * 3];     // the Lab→XYZ round trip = LSQ targets
        public float RecipXn, RecipZn;                 // functor +0x18 / +0x20
        public double[] X0 = new double[9];            // weighted-LSQ start (column-major M₀: x[r + 3c] = M(r, c))
        public bool Evaluate(double[] x, out double cost, double[] gradient) => HsvLutOptimizer.Evaluate(this, x, out cost, gradient, null, null);
    }

    public static Prepared Prepare(IReadOnlyList<Point3F> macbeth)
    {
        var pr = new Prepared();
        // reference chart: XYZ → Lab (18014b500 L105) and Lab → XYZ (OHL L203/L221), both float kernels at D50
        var (refLabF, refXyz) = LumenLabKernels.ReferenceChart(IllumD50);
        pr.RefXyz = refXyz;
        for (int i = 0; i < 24 * 3; i++) pr.RefLab[i] = refLabF[i];
        for (int k = 0; k < 3; k++) pr.RefLab[24 * 3 + k] = 1e-6;

        // camera RGB scaled so that patch 19's G equals the round-tripped reference Y (OHL L205–212: `xyz[0xdc/4] / cam[0x37]`, float)
        for (int i = 0; i < 24; i++) { pr.Cam[i * 3] = macbeth[i].X; pr.Cam[i * 3 + 1] = macbeth[i].Y; pr.Cam[i * 3 + 2] = macbeth[i].Z; }
        float scale = refXyz[WhitePatch * 3 + 1] / pr.Cam[WhitePatch * 3 + 1];
        for (int i = 0; i < 24; i++) { pr.Cam[i * 3] = pr.Cam[i * 3] * scale; pr.Cam[i * 3 + 1] = pr.Cam[i * 3 + 1] * scale; pr.Cam[i * 3 + 2] = pr.Cam[i * 3 + 2] * scale; }
        for (int i = 0; i < 24 * 3; i++) pr.A[i] = pr.Cam[i];
        for (int k = 0; k < 3; k++) pr.A[24 * 3 + k] = 1e-6;   // 0x3eb0c6f7a0b5ed8d
        for (int i = 0; i < 24; i++) pr.Weights[i] = 1.0;
        pr.Weights[24] = 0.0;

        // Lab white reciprocals stored in the functor as floats (OHL: `invY = 1/y; +0x20 = 1/(((1 − y) − x)·invY); +0x18 = 1/(x·invY)`)
        {
            float x = IlluminantX[IllumD50], y = IlluminantY[IllumD50];
            float invY = 1.0f / y;
            pr.RecipZn = 1.0f / (((1.0f - y) - x) * invY);
            pr.RecipXn = 1.0f / (x * invY);
        }

        // initial M₀ (OHL L396–441): `LDLT(Aᵀ·W·A).solve(Aᵀ·W·XYZ′)` per XYZ column, W = diag(weights) (Eigen 3.2 semantics, EigenLdlt);
        // the products are summed in index order (Eigen's GEMM accumulation order is not reproduced — x0 agrees with ceres.dll to ~1e-13)
        {
            const int N = 25;
            var AtWA = new double[9];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) { double acc = 0.0; for (int i = 0; i < N; i++) acc += (pr.A[i * 3 + r] * pr.Weights[i]) * pr.A[i * 3 + c]; AtWA[r * 3 + c] = acc; }
            var ldlt = new EigenLdlt(AtWA, 3);
            for (int col = 0; col < 3; col++)
            {
                var rhs = new double[3];
                for (int r = 0; r < 3; r++) { double acc = 0.0; for (int i = 0; i < N; i++) acc += (pr.A[i * 3 + r] * pr.Weights[i]) * (i < 24 ? (double)refXyz[i * 3 + col] : 1e-6); rhs[r] = acc; }
                var sol = ldlt.Solve(rhs);
                for (int k = 0; k < 3; k++) pr.X0[k * 3 + col] = sol[k];
            }
        }
        return pr;
    }

    /// <summary>The Ceres side of one evaluation (`ProgramEvaluator` + `ResidualBlock::Evaluate` + `AutoDiffCostFunction::Evaluate`):
    /// residuals/Jacobian from the Jet functor (`FUN_180160420`), `cost = 0.5·‖r‖²` (Eigen squaredNorm) and `gradient = Jᵀ·r`
    /// (small_blas `MatrixTransposeVectorMultiply`: per column, sequential over the rows). Optional copies of r (25) and J (25×9 row-major).</summary>
    public static bool Evaluate(Prepared pr, double[] x, out double cost, double[] gradient, double[]? residualsOut, double[]? jacobianOut)
    {
        var pj = new Jet9[9];
        for (int k = 0; k < 9; k++) pj[k] = Jet9.Param(x[k], k);
        var r = new double[25];
        var J = new double[25 * 9];
        double rXn = pr.RecipXn, rYn = 1.0f, rZn = pr.RecipZn;
        bool ok = true;
        for (int i = 0; i < 25; i++)
        {
            double a0 = pr.A[i * 3], a1 = pr.A[i * 3 + 1], a2 = pr.A[i * 3 + 2];
            // xyz_r = Σ_k A(i,k)·x[r + 3k] (x = M′ column-major, M′(r,k) = x[r + 3k]); Eigen's dynamic inner-product order (one odd term, then pairs)
            Jet9 X = (a2 * pj[6] + a1 * pj[3]) + (a0 * pj[0]);
            Jet9 Y = (a2 * pj[7] + a1 * pj[4]) + (a0 * pj[1]);
            Jet9 Z = (a2 * pj[8] + a1 * pj[5]) + (a0 * pj[2]);
            // Lab with the float white reciprocals (functor +0x18/+0x1c/+0x20), f(t) = pow(t, 1/3) / 7.787·t + 16/116 (DAT_180681c88, 1806a0900/0980/0990)
            Jet9 fx = LabF(rXn * X), fy = LabF(rYn * Y), fz = LabF(rZn * Z);
            Jet9 L = fy * 116.0 + (-16.0), a = (fx - fy) * 500.0, b = (fy - fz) * 200.0;
            Jet9 de = DeltaE2000(pr.RefLab[i * 3], pr.RefLab[i * 3 + 1], pr.RefLab[i * 3 + 2], L, a, b);
            Jet9 res = Math.Sqrt(pr.Weights[i]) * de;
            r[i] = res.V;
            for (int k = 0; k < 9; k++) { double d = res[k]; J[i * 9 + k] = d; if (!double.IsFinite(d)) ok = false; }
            if (!double.IsFinite(res.V)) ok = false;
        }
        cost = 0.5 * EigenRedux.SquaredNorm(r);
        for (int c = 0; c < 9; c++) { double tmp = 0.0; for (int row = 0; row < 25; row++) tmp += J[row * 9 + c] * r[row]; gradient[c] = tmp; }
        if (residualsOut is not null) Array.Copy(r, residualsOut, 25);
        if (jacobianOut is not null) Array.Copy(J, jacobianOut, 225);
        return ok;
    }

    /// <summary>After the solve: M′ = the parameters as floats (OHL L768–779, column-major read), ForwardMatrix (18014b500) and the HSV map.</summary>
    public static ColorFit Finish(Prepared pr, double[] p, CeresSummary res)
    {
        var cam = pr.Cam;
        var M = new float[9]; var M0 = new float[9];
        for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) { M[r * 3 + k] = (float)p[k * 3 + r]; M0[r * 3 + k] = (float)pr.X0[k * 3 + r]; }

        // --- ForwardMatrix = M'·diag(M'⁻¹·XYZ_D50) in float (18014b500 L224–311) ---
        var FM = ForwardMatrixFrom(M);

        // --- HSV deltas (OHL L811–945) ---
        // corrected XYZ = the double product A·M (`FUN_18016a5c0`, small-product path: `((A0·M0c + A1·M1c) + A2·M2c)`),
        // conservative-resized to 24 rows and stored as floats (OHL L751–766) — not float(M)·float(cam);
        // reference = the float Lab of the chart converted straight to ProPhoto by the Lab kernel (OHL L811: src space 8, dst space 5).
        var proXyz = LumenLabKernels.XyzToRgbMatrix(ProPhotoToXyz);
        var proLab = LumenLabKernels.LabToRgbMatrix(ProPhotoToXyz, IllumD50);
        var refHsv = new float[24 * 3]; var corHsv = new float[24 * 3];
        Span<float> rgb = stackalloc float[3];
        for (int i = 0; i < 24; i++)
        {
            LumenLabKernels.LabToRgb(proLab, (float)pr.RefLab[i * 3], (float)pr.RefLab[i * 3 + 1], (float)pr.RefLab[i * 3 + 2], rgb);
            RgbToHsv(rgb, refHsv, i * 3);
            double a0 = pr.A[i * 3], a1 = pr.A[i * 3 + 1], a2 = pr.A[i * 3 + 2];
            float X = (float)((a0 * p[0] + a1 * p[3]) + a2 * p[6]);
            float Y = (float)((a0 * p[1] + a1 * p[4]) + a2 * p[7]);
            float Z = (float)((a0 * p[2] + a1 * p[5]) + a2 * p[8]);
            LumenLabKernels.XyzToRgb(proXyz, X, Y, Z, rgb);
            RgbToHsv(rgb, corHsv, i * 3);
        }
        var pts = new List<(float H, float S)>(); var vals = new List<(float Dh, float Sr, float Vr)>();
        int chroma = Math.Min(24, 18);   // OHL: min(count, 0x12) — the 18 chromatic patches
        for (int i = 0; i < chroma; i++)
        {
            float hc = corHsv[i * 3], sc = corHsv[i * 3 + 1], vc = corHsv[i * 3 + 2];
            float dh = refHsv[i * 3] - hc; if (dh <= -HueDeltaClamp) dh = -HueDeltaClamp; if (HueDeltaClamp <= dh) dh = HueDeltaClamp;
            float sr = refHsv[i * 3 + 1] / sc; if (sr <= SatRatioLo) sr = SatRatioLo; if (SatRatioHi <= sr) sr = SatRatioHi;
            float vr = refHsv[i * 3 + 2] / vc; if (vr <= ValRatioLo) vr = ValRatioLo; if (ValRatioHi <= vr) vr = ValRatioHi;
            pts.Add((hc, sc)); vals.Add((dh, sr, vr));
        }
        // anchors: h = 0, 1/6, … (accumulated in double, rounded to float each step), s ∈ {0, 0.15, 0.95, 1}
        {
            float hf = 0f; double hd = 0;
            do
            {
                foreach (var s in new[] { 0f, 0.15f, 0.95f, 1f }) { pts.Add((hf, s)); vals.Add((0f, 1f, 1f)); }
                hf = (float)(hd + AnchorHueStep); hd = hf;
            } while (hf < 1f);
        }
        int nb = pts.Count;
        for (int i = 0; i < nb; i++)   // wrap duplicates at h+1 and h−1 (DAT_180681c90 = −1)
        {
            pts.Add((pts[i].H + 1f, pts[i].S)); vals.Add(vals[i]);
            pts.Add((pts[i].H + -1f, pts[i].S)); vals.Add(vals[i]);
        }

        // --- thin-plate splines, one per channel (OHL L955–1280) ---
        var wDh = TpsSolve(pts, vals.Select(v => (double)v.Dh).ToArray());
        var wSr = TpsSolve(pts, vals.Select(v => (double)v.Sr).ToArray());
        var wVr = TpsSolve(pts, vals.Select(v => (double)v.Vr).ToArray());

        // --- grid (FUN_18017b500): dims (32,32,1) stored 33×33 with the last row/col duplicated ---
        int hd_ = ColorFit.HueDivisions, sd = ColorFit.SatDivisions;
        // `18017b6*`: step = 1 / max((float)(n − 1), 1.0f), sample index = min(i, n − 1)
        float hStep = 1f / MathF.Max((float)(hd_ - 1), 1f), sStep = 1f / MathF.Max((float)(sd - 1), 1f);
        var grid = new float[(hd_ + 1) * (sd + 1) * 4];
        for (int j = 0; j <= sd; j++)
        {
            float s = (float)Math.Min(j, sd - 1) * sStep;
            for (int i = 0; i <= hd_; i++)
            {
                float h = (float)Math.Min(i, hd_ - 1) * hStep;
                float dh = TpsEval(pts, wDh, h, s), sr = TpsEval(pts, wSr, h, s), vr = TpsEval(pts, wVr, h, s);
                // `_Func_impl<lambda_0>::_Do_call` (0x18016a350) — the std::function thunk the grid builder calls, not the
                // lambda itself: it hands the lambda the (h, s) pair and then, on return, forces the **value** lane to 1
                // when the queried saturation is below 1e-4 (`ucomisd (double)s, DAT_1806a0ad8 (1e-4)` / `jae`), i.e. the
                // neutral row of the map never scales value. With s = j/31 only j = 0 is affected.
                if ((double)s < NeutralSatEpsilon) vr = 1.0f;
                int c = (i + (hd_ + 1) * j) * 4;
                grid[c] = Math.Clamp(dh, -1f, 1f) + 1f;      // clamp (−1,0,0,0)…(1,2,2,0): DAT_180681c90 / _DAT_1806a29d0
                grid[c + 1] = Math.Clamp(sr, 0f, 2f);
                grid[c + 2] = Math.Clamp(vr, 0f, 2f);
                grid[c + 3] = 0f;
            }
        }
        return new ColorFit(M, M0, FM, grid, res.Termination == CeresTermination.Convergence, res.NumIterations, res.FinalCost, res.InitialCost, res.Message);
    }

    /// <summary>FM = M'·diag(M'⁻¹·w), w = XYZ of the D50 white (float math, `18014b500` L224–311).</summary>
    public static float[] ForwardMatrixFrom(float[] M)
    {
        // w = (x·(1/y), 1, ((1 − y) − x)·(1/y)) in float; inv = FUN_1800c2a00(M'); n_r = ((inv[r][0]·Xw) + inv[r][1]) + (inv[r][2]·Zw) (disasm 18014b8a8–);
        // FM(r, c) = M'(r, c)·n_c (the other lanes of the SSE product are exact zeros)
        float wx = IlluminantX[IllumD50], wy = IlluminantY[IllumD50];
        float invY = 1.0f / wy;
        float Xw = wx * invY, Zw = ((1.0f - wy) - wx) * invY;
        var inv = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(M);
        var n = new float[3];
        for (int r = 0; r < 3; r++) n[r] = ((inv[r * 3] * Xw) + inv[r * 3 + 1]) + (inv[r * 3 + 2] * Zw);
        var fm = new float[9];
        for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) fm[r * 3 + k] = M[r * 3 + k] * n[k];
        return fm;
    }

    // ---------------- colour math ----------------

    private static Jet9 LabF(Jet9 t) => t < LabEpsilon ? t * LabKappa + LabOffset : Jet9.Pow(t, 1.0 / 3.0);

    private const double Pow25_7 = 6103515625.0;   // DAT_1806a0818
    private const double TwoPi = 6.283185307179586, Pi = 3.141592653589793;

    /// <summary>CIEDE2000 exactly as `FUN_180161c80` (double instantiation; the Jet twin `FUN_180162140` has the same structure) computes it
    /// between the reference Lab (`param_2`) and the candidate (`param_3`) — every association, the hue conventions (radians, `h ≤ 0 → h + 2π`,
    /// `|Δh| > π` corrections with `DAT_1806a09c0 = (−2π, +2π)` indexed by `h2 &lt; h1`, `H̄ = (h1 + h2)/D + π`, D ∈ {1, 2} by the 1e-8 chroma tests)
    /// and the constants (`DAT_1806a0868 = −30°, 0870 = −0.17, 0878 = 0.24, 09d0/09e0 = 3/6°, 09d8/09e8 = 4/−63°, 09f0 = 0.32, 09f8 = −0.2,
    /// 0a00..0a18 = the (H° − 275)²/25² factors, 08d0 = 60°, 08d8 = −50, 0a20 = 0.0075, 0a28 = −0.75, 08e8 = 20, 0a30 = 0.0225, 08c0 = −2`).</summary>
    public static Jet9 DeltaE2000(double L1, double a1, double b1, Jet9 L2, Jet9 a2, Jet9 b2)
    {
        Jet9 Cb = (Jet9.Sqrt(b2 * b2 + a2 * a2) + Math.Sqrt(a1 * a1 + b1 * b1)) * 0.5;
        Jet9 Cb2 = Cb * Cb;
        Jet9 Cb7 = Cb2 * Cb2 * Cb * Cb2;
        Jet9 g = (1.0 - Jet9.Sqrt(Cb7 / (Pow25_7 + Cb7))) * 0.5 + 1.0;
        Jet9 a1p = a1 * g, a2p = a2 * g;
        Jet9 C1p = Jet9.Sqrt(a1p * a1p + b1 * b1);
        Jet9 C2p = Jet9.Sqrt(a2p * a2p + b2 * b2);
        Jet9 h1 = 1e-8 <= C1p.V ? Jet9.Atan2((Jet9)b1, a1p) : 0.0;
        Jet9 h2 = 1e-8 <= C2p.V ? Jet9.Atan2(b2, a2p) : 0.0;
        Jet9 sumC = C2p + C1p;
        Jet9 Cbp = sumC * 0.5;
        Jet9 dC = C2p - C1p, dL = L2 - L1;
        h2 = (h2.V <= 0.0 ? TwoPi : 0.0) + h2;
        h1 = (h1.V <= 0.0 ? TwoPi : 0.0) + h1;
        double adh = Math.Abs(h2.V - h1.V);
        double corr = Pi < adh ? (h2.V < h1.V ? TwoPi : -TwoPi) : 0.0;
        Jet9 sinh = Jet9.Sin((corr + (h2 - h1)) * 0.5);
        double D = 1e-8 < C1p.V ? (1e-8 < C2p.V ? 2.0 : 1.0) : 1.0;
        Jet9 Hbar = (Pi < adh ? Pi : 0.0) + (h2 + h1) / D;
        Jet9 c1 = Jet9.Cos(-0.5235987755982988 + Hbar) * -0.17;
        Jet9 c2 = Jet9.Cos(Hbar + Hbar) * 0.24;
        Jet9 c3 = Jet9.Cos(3.0 * Hbar + 0.10471975511965977) * 0.32;
        Jet9 c4 = Jet9.Cos(4.0 * Hbar + -1.0995574287564276) * -0.2;
        Jet9 Cbp2 = Cbp * Cbp;
        Jet9 Cbp7 = Cbp2 * Cbp2 * Cbp * Cbp2;
        Jet9 den7 = Pow25_7 + Cbp7;
        Jet9 e = Jet9.Exp((Hbar * 57.29577951308232 + -275.0) * (Hbar * -0.09167324722093172 + 0.44));
        Jet9 s2 = Jet9.Sin(e * 1.0471975511965976);
        Jet9 sumL = L1 + L2;
        Jet9 Lm = 0.5 * sumL + -50.0;
        Jet9 SL = ((sumL * 0.0075 + -0.75) * Lm) / Jet9.Sqrt(Lm * Lm + 20.0) + 1.0;
        Jet9 SC = 0.0225 * sumC + 1.0;
        Jet9 tC = dC / SC, tL = dL / SL;
        Jet9 T = c4 + c2 + 1.0 + c1 + c3;
        Jet9 tH = ((sinh + sinh) * Jet9.Sqrt(C2p * C1p)) / (sumC * 0.0075 * T + 1.0);
        return Jet9.Sqrt(tL * tL + tC * tC + (tC * -2.0 * s2 * Jet9.Sqrt(Cbp7 / den7) + tH) * tH);
    }

    /// <summary>RGB → HSV, hue in turns [0,1) (`FUN_1800d0100` loop `1800d02a0–1800d0364`, same body as the image lambda `1800e79b0`):
    /// the lanes are first clamped to ≥ 0 (`maxps`), `v = max`, `s = max == 0 ? 0 : (max−min)/max`, and when `s ≠ 0` the hue uses the
    /// **reciprocal** `1/(max−min)` times the difference, plus 2/4 (`DAT_180682414/408`), scaled by `1/6` (`DAT_1806874c0`, a multiply —
    /// not a division by 6) and wrapped by `+1` when negative.</summary>
    public static void RgbToHsv(ReadOnlySpan<float> rgbIn, float[] dst, int o)
    {
        float r = MathF.Max(rgbIn[0], 0f), g = MathF.Max(rgbIn[1], 0f), b = MathF.Max(rgbIn[2], 0f);
        float mn = MathF.Min(MathF.Min(r, g), b), mx = MathF.Max(MathF.Max(r, g), b);
        float d = mx - mn;
        float s = mx != 0f ? d / mx : 0f;
        float h = 0f;
        if (s != 0f)
        {
            float inv = 1.0f / d;
            if (r == mx) h = inv * (g - b);
            else if (g == mx) h = inv * (b - r) + 2f;
            else h = inv * (r - g) + 4f;
            h = h * 0.1666666716337204f;
            if (h < 0f) h = h + 1f;
        }
        dst[o] = h; dst[o + 1] = s; dst[o + 2] = mx;
    }

    // ---------------- thin-plate spline ----------------

    private static double Phi(double r) => r > 0 ? r * r * Math.Log10(r) : 0.0;   // φ(r) = r²·log10 r, as `log10(r)·r·r`

    /// <summary>`sqrt(x)` as the binary computes every point distance: `rsqrtss` + one Newton step
    /// (`((x·rs)·rs + (−3))·(x·rs)·(−0.5)`, `DAT_180681c7c = −0.5`, `DAT_180681c80 = −3`), masked to 0 when `x == 0`.
    /// This is **not** a correctly-rounded square root (≈1e-7 relative error) and the λ/φ values depend on it.</summary>
    public static float RsqrtSqrt(float x)
    {
        if (x == 0f) return 0f;
        float rs = System.Runtime.Intrinsics.Vector128.ToScalar(System.Runtime.Intrinsics.X86.Sse.ReciprocalSqrt(System.Runtime.Intrinsics.Vector128.CreateScalar(x)));
        float t = x * rs;
        return ((t * rs + -3.0f) * t) * -0.5f;
    }

    /// <summary>Distance between two TPS points, exactly as `OHL` L1069+ / the evaluator compute it: float differences,
    /// `r² = dy² + dx²`, then <see cref="RsqrtSqrt"/>.</summary>
    private static float Dist((float H, float S) a, (float H, float S) b)
    {
        float dx = a.H - b.H, dy = a.S - b.S;
        return RsqrtSqrt(dy * dy + dx * dx);
    }

    /// <summary>Solve [[K+λI, P],[Pᵀ, 0]]·[w; a] = [v; 0] with P = [1, h, s], λ = (mean of all n² pairwise
    /// distances, zero diagonal included)² × 0.0015 (`DAT_1806a0810`). Returns w[0..n−1], a0, a1, a2.
    /// The mean is accumulated in the binary's order (`OHL` L1069–1157): a 4-wide inner loop over j with four
    /// double accumulators (lane 0 being the running total), combined per row as `((s2 + total) + s3) + s1`,
    /// then a scalar tail. The system is solved by Eigen's `ColPivHouseholderQR` (`FUN_180164940`/`FUN_180168190`).</summary>
    private static double[] TpsSolve(List<(float H, float S)> pts, double[] v, double[]? lambdaOut = null)
    {
        int n = pts.Count, m = n + 3;
        double total = 0.0;
        for (int i = 0; i < n; i++)
        {
            int j = 0;
            if (n > 3)
            {
                double s1 = 0.0, s2 = 0.0, s3 = 0.0;
                for (; j != (n & ~3); j += 4)
                {
                    total += Dist(pts[i], pts[j]);
                    s1 += Dist(pts[i], pts[j + 1]);
                    s2 += Dist(pts[i], pts[j + 2]);
                    s3 += Dist(pts[i], pts[j + 3]);
                }
                total = ((s2 + total) + s3) + s1;
            }
            for (; j != n; j++) total += Dist(pts[i], pts[j]);
        }
        double mean = total / (double)((long)n * n), lambda = mean * mean * LambdaScale;
        if (lambdaOut is not null) lambdaOut[0] = lambda;
        var S = new double[m * m]; var rhs = new double[m];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                double val = Phi(Dist(pts[i], pts[j])) + (i == j ? lambda : 0.0);
                S[i * m + j] = val; S[j * m + i] = val;
            }
        for (int i = 0; i < n; i++)
        {
            S[i * m + n] = 1.0; S[i * m + n + 1] = pts[i].H; S[i * m + n + 2] = pts[i].S;
            S[n * m + i] = 1.0; S[(n + 1) * m + i] = pts[i].H; S[(n + 2) * m + i] = pts[i].S;
            rhs[i] = v[i];
        }
        return new EigenColPivQr(S, m, m).Solve(rhs);
    }

    /// <summary>Evaluator `1801644d0` (OptimizeHSVLut lambda_0): float query/points, double weights, the affine part
    /// and the running sum rounded to float after every term exactly as the binary does.</summary>
    private static float TpsEval(List<(float H, float S)> pts, double[] w, float h, float s)
    {
        int n = pts.Count;
        double acc = (double)(float)((double)(float)w[n] + w[n + 1] * (double)h) + w[n + 2] * (double)s;
        for (int i = 0; i < n; i++)
        {
            float dx = pts[i].H - h, dy = pts[i].S - s;
            float r = RsqrtSqrt(dy * dy + dx * dx);
            double phi = 0.0;
            if (r > 0f) { double rd = r; phi = Math.Log10(rd) * rd * rd; }
            acc = (double)(float)acc + w[i] * phi;
        }
        return (float)acc;
    }

    // ---------------- small linear algebra ----------------


}
