using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::SparseMirrorAngleOptimizer::optimize` (1802994a0 + lambdas 18029c930/cd80, costs `FUN_18029c560` epipolar /
/// `FUN_18029c060` reprojection): exhaustive grid over the mirror angle θ (step 0.05°), the in-plane roll δ (step 0.025°,
/// FreeParams &gt; 1) and a principal-point offset sliding along the θ-flow direction (FreeParams ≥ 1); every node = mirror
/// pose ∘ Rz(δ), principal-point shift, view-transform pipeline; accepted iff the outlier fraction ≤ 0.25, not worse than the
/// centre node and (FreeParams ≥ 1) cost ≤ 0.8 × centre cost. The accepted (raw) camera is written to the CURRENT slot.
/// </summary>
public static class SparseMirrorAngleOptimizer
{
    const double ThetaStep = 0.05;                        // DAT_1806b8f58
    const double DeltaStep = 4.3633230555555557e-4;       // DAT_1806b8f60
    const float EpiClamp = 15.0f, ReproClamp = 8.0f, OutFracMax = 0.25f, CostRatio = 0.8f;
    static readonly float NormEps = BitConverter.Int32BitsToSingle(0x33d6bf95);   // DAT_18068a51c ≈ 1e-7

    public sealed class Result { public bool Accepted; public CalibData? Written; public double Theta, Delta; public float Cx, Cy; public float Cost, OutFrac, CentreCost, CentreOutFrac; }

    /// <summary>The θ-flow direction (§4.3): image motion of the on-axis point at depth Z between θ and θ+2°, normalised by its max component.</summary>
    public static (float Gx, float Gy) Flow(MirrorSystem sys, CalibData baseCam, CalibData refCam, double thetaC, float Z)
    {
        var M1 = Mat4D.FlowMatrix(MirrorPose.NodePose(sys, baseCam, thetaC, 0.0), refCam);
        var M2 = Mat4D.FlowMatrix(MirrorPose.NodePose(sys, baseCam, thetaC + 2.0, 0.0), refCam);
        float cx = baseCam.K[2], cy = baseCam.K[5];
        float[] p = { 1f * (cx * Z), 1f * (cy * Z), Z, 1f };
        (float U, float V) Proj(float[] M)
        {
            var q = new float[4];
            for (int l = 0; l < 4; l++) q[l] = ((p[3] * M[12 + l] + p[2] * M[8 + l]) + (p[1] * M[4 + l] + p[0] * M[l]));
            float w = 1.0f / q[2];
            return (w * q[0], w * q[1]);
        }
        var p1 = Proj(M1); var p2 = Proj(M2);
        float dx = p2.U - p1.U, dy = p2.V - p1.V;
        float m = MathF.Abs(dy); if (!(MathF.Abs(dx) <= m)) m = MathF.Abs(dx);   // maxss(|dy|, |dx|)
        float inv = 1.0f / m;
        return (dx * inv, dy * inv);
    }

    /// <summary>`FUN_18029c060`: mean clamped reprojection distance of the triangulated points into the candidate camera.</summary>
    public static (float Cost, float OutFrac) ReprojectionCost(CalibData cam, ReadOnlySpan<float> matches, TriPoint[] pts)
    {
        float sum = 0f; int n = 0, outl = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            float mx = matches[2 * i], my = matches[2 * i + 1];
            if (mx <= 0f || my <= 0f) continue;
            float Z = pts[i].Z; if (Z <= 0f) continue;
            float X = pts[i].X, Y = pts[i].Y;
            float[] R = cam.R, K = cam.K, t = cam.T;
            float xc0 = ((R[2] * Z + R[1] * Y) + R[0] * X) + t[0];
            float xc1 = ((R[5] * Z + R[4] * Y) + R[3] * X) + t[1];
            float xc2 = ((R[8] * Z + R[7] * Y) + R[6] * X) + t[2];
            float w = 1.0f / ((K[8] * xc2 + K[7] * xc1) + K[6] * xc0);
            float u = w * ((K[2] * xc2 + K[1] * xc1) + K[0] * xc0) - mx;
            float v = w * ((K[5] * xc2 + K[4] * xc1) + K[3] * xc0) - my;
            float d2 = v * v + u * u;
            float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(d2)).ToScalar();
            float s = d2 * r;
            float d = d2 == 0f ? 0f : ((s * r + (-3.0f)) * (-0.5f)) * s;
            float dc = d; if (ReproClamp <= d) dc = ReproClamp;
            if (ReproClamp < d) outl++;
            sum += dc; n++;
        }
        float invN = 1.0f / ((float)n + NormEps);
        return (invN * sum, outl * invN);
    }

    /// <summary>`FUN_18029c560`: mean clamped epipolar distance of the matches (view-A cost, WIDE side).</summary>
    public static (float Cost, float OutFrac) EpipolarCost(CalibData refCam, CalibData cam, ReadOnlySpan<float> matches, TriPoint[] pts)
    {
        var F = Triangulator.Fundamental(refCam, cam);
        float sum = 0f; int n = 0, outl = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            float mx = matches[2 * i], my = matches[2 * i + 1];
            if (!(mx > 0f && my > 0f)) continue;
            float u = pts[i].U, v = pts[i].V;
            float l0 = (F[1] * v + F[0] * u) + F[2], l1 = (F[4] * v + F[3] * u) + F[5], l2 = (v * F[7] + u * F[6]) + F[8];
            float n2 = l1 * l1 + l0 * l0;
            float rr = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(n2)).ToScalar();
            float sc = (rr * (-0.5f)) * (((n2 * rr) * rr) + (-3.0f));
            float a = l0 * sc, b = l1 * sc, c = sc * l2;
            float d = MathF.Abs((a * mx + c) + b * my);
            float dc = d; if (EpiClamp <= d) dc = EpiClamp;
            if (EpiClamp < d) outl++;
            sum += dc; n++;
        }
        float invN = 1.0f / ((float)n + NormEps);
        return (invN * sum, outl * invN);
    }

    /// <summary>Run the grid search. `baseCam` = the module's CURRENT slot (`FUN_180307b30`) as a full CalibData (K2/off/scale
    /// pass through the view pipeline), `pose` = the module's view-transform pipeline, `refCam` = this+0x48.</summary>
    public static Result Optimize(MirrorSystem sys, CalibDataFull baseCam, ViewPose pose, CalibData refCam, ReadOnlySpan<float> matches, TriPoint[] pts,
        int freeParams, int costFunction, double seedTheta, (float X, float Y) seedC, float Z, bool wideFlag, ActuatorMapping? map = null, double hall = 0.0)
    {
        int nValid = 0; for (int i = 0; i < matches.Length / 2; i++) if (matches[2 * i] > 0f && matches[2 * i + 1] > 0f) nValid++;
        var res = new Result();
        if (nValid < 8) return res;
        double thetaC = seedTheta > 0 ? seedTheta : (map ?? throw new ArgumentException("unseeded optimize needs the actuator mapping (θc = FUN_18024daf0(mapping, hall))")).Angle(hall);   // spec §4 step 2
        var baseBasic = baseCam.Basic();
        var (gx, gy) = Flow(sys, baseBasic, refCam, thetaC, Z);
        int Na = (wideFlag ? 10 : 24) >> (seedTheta > 0 ? 1 : 0);
        int Nc = freeParams < 1 ? 0 : (seedTheta <= 0 ? 10 : 5);
        int Nd = freeParams > 1 ? 4 : 0;
        int nTheta = 2 * Na + 1, nDelta = 2 * Nd + 1, nC = 2 * Nc + 1, total = nTheta * nDelta * nC;
        var cost = new float[total]; var outf = new float[total];
        var thetas = new double[total]; var deltas = new double[total]; var cxs = new float[total]; var cys = new float[total];
        for (int i = -Na; i <= Na; i++)
            for (int k = -Nd; k <= Nd; k++)
                for (int j = -Nc; j <= Nc; j++)
                {
                    int idx = ((i + Na) * nDelta + (k + Nd)) * nC + (j + Nc);
                    thetas[idx] = (double)i * ThetaStep + thetaC;
                    cxs[idx] = seedC.X - (gy * (float)j);
                    cys[idx] = ((float)j * gx) + seedC.Y;
                    deltas[idx] = (double)k * DeltaStep;
                }
        int centre = ((0 + Na) * nDelta + (0 + Nd)) * nC + (0 + Nc);
        for (int idx = 0; idx < total; idx++)
        {
            var cam = MirrorPose.NodePose(sys, baseBasic, thetas[idx], deltas[idx]);
            var full = baseCam.Clone(); full.K = cam.K; full.R = cam.R; full.T = cam.T;
            var sh = ViewTransform.Shift(full, cxs[idx], cys[idx]);
            var c2 = ViewTransform.Apply(pose, sh).Basic();
            (cost[idx], outf[idx]) = costFunction == 0 ? EpipolarCost(refCam, c2, matches, pts) : ReprojectionCost(c2, matches, pts);
        }
        int best = 0; for (int idx = 1; idx < total; idx++) if (cost[idx] < cost[best]) best = idx;
        res.Theta = thetas[best]; res.Delta = deltas[best]; res.Cx = cxs[best]; res.Cy = cys[best]; res.Cost = cost[best]; res.OutFrac = outf[best]; res.CentreCost = cost[centre]; res.CentreOutFrac = outf[centre];
        bool accept = outf[best] <= OutFracMax && outf[best] <= outf[centre] && (freeParams < 1 || cost[best] <= cost[centre] * CostRatio);
        if (!accept) return res;
        var camB = MirrorPose.NodePose(sys, baseBasic, thetas[best], deltas[best]);
        var outc = MirrorPose.Shift(camB, cxs[best], cys[best]);
        res.Accepted = true; res.Written = outc;
        return res;
    }
}
