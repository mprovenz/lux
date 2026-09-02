using Lux.Engine.Lri;
using OpenCvSharp;

namespace Lux.Engine.Imaging;

/// <summary>
/// Faithful reconstruction of Lumen's per-module mirror-angle refinement (vault §11a). The movable-mirror B/C
/// modules' calibrated pose drifts per capture (the actuator hall-code → angle mapping is only nominal); Lumen
/// re-estimates the mirror angle θ (and an optical-center offset) so each module aligns to the wide reference.
/// This replaces the AKAZE-homography stand-in with the actual mechanism.
///
/// <para><b>Coarse</b> (<see cref="CoarseAngle"/>, <c>MirrorAngleOptimizer::optimize</c> @1802929c0): a 533-candidate
/// brute-force search — 13 angles (θ₀±0.6°, step 0.1°) × 41 optical-center offsets stepping perpendicular to the
/// angle-induced image flow — scored by mean L1 (SAD) image alignment of the warped module vs the reference at 1/8
/// resolution, argmin with a 0.95 hysteresis vs nominal.</para>
/// </summary>
public static class MirrorRefine
{
    /// <summary>Result of the coarse search: refined mirror angle θ (degrees) and optical-center pixel offsets
    /// (in module-image pixels at full module resolution), plus the aligned cost vs the nominal cost.</summary>
    public readonly record struct Coarse(double Theta, double CxOff, double CyOff, double Cost, double NominalCost, bool Moved);

    // Search constants (vault §11a, cp.dll .rdata).
    private const int KA = 6;              // angle index range −6..+6 (13)
    private const double DTH = 0.1;        // angle step (deg)
    private const double PROBE = 2.0;      // finite-diff angle probe for the flow direction
    private const int MO = 20;             // offset index range −20..+20 (41)
    private const double COFF = 8.0;       // offset scale on the perpendicular flow
    private const double SCALE = 0.125;    // 1/8-res scoring
    private const double HYST = 0.95;      // accept argmin only if ≤ 0.95·nominal
    private const int MINV = 100;          // need > 100 valid-overlap pixels

    /// <summary>Coarse mirror-angle search for one movable-mirror module against the wide reference.
    /// <paramref name="refImg"/>/<paramref name="modImg"/> are single-channel luma at their own resolutions;
    /// <paramref name="Kref"/> is the reference (A1) intrinsics at the SAME resolution as refImg. planeZ = the
    /// alignment plane depth (mm). Returns the refined θ (deg) + optical-center offsets (module full-res px).</summary>
    public static Coarse CoarseAngle(ModulePose.MirrorGeom geom, double[] Kref, float[] refImg, int refW, int refH,
                                     double[] Kmod, float[] modImg, int modW, int modH, double planeZ)
    {
        // Work at 1/8 res (the decomp's coarse scoring resolution).
        int rw = Math.Max(8, (int)(refW * SCALE)), rh = Math.Max(8, (int)(refH * SCALE));
        int mw = Math.Max(8, (int)(modW * SCALE)), mh = Math.Max(8, (int)(modH * SCALE));
        var refS = Cv.Resize(refImg, refW, refH, rw, rh, InterpolationFlags.Area);
        var modS = Cv.Resize(modImg, modW, modH, mw, mh, InterpolationFlags.Area);
        double srx = (double)rw / refW, sry = (double)rh / refH;   // ref full→1/8 scale
        double smx = (double)mw / modW, smy = (double)mh / modH;   // module full→1/8 scale
        double[] KrefS = ScaleK(Kref, srx, sry);
        double[] invKrefS = Mat3.Inverse(KrefS);

        double th0 = geom.NominalTheta;

        // Flow direction: how a representative reference point moves in the module image when θ→θ+PROBE.
        double cxr = rw / 2.0, cyr = rh / 2.0;
        var (px0, py0) = ProjectRefToMod(geom, th0, Kmod, smx, smy, invKrefS, cxr, cyr, planeZ);
        var (px1, py1) = ProjectRefToMod(geom, th0 + PROBE, Kmod, smx, smy, invKrefS, cxr, cyr, planeZ);
        double dx = px1 - px0, dy = py1 - py0;
        double inv = 1.0 / Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), 1e-9);

        // Score all 533 candidates.
        double bestCost = double.MaxValue, nomCost = double.MaxValue; double bTh = th0, bCx = 0, bCy = 0;
        var costs = new double[(2 * KA + 1) * (2 * MO + 1)];
        int idx = 0;
        for (int k = -KA; k <= KA; k++)
        {
            double th = th0 + k * DTH;
            for (int m = -MO; m <= MO; m++, idx++)
            {
                double cxOff = -COFF * m * dy * inv;   // module-res px offsets to the principal point
                double cyOff = COFF * m * dx * inv;
                // shift the module intrinsics' principal point (scaled to 1/8 for scoring)
                double[] Km = (double[])Kmod.Clone();
                Km[2] -= cxOff; Km[5] -= cyOff;
                double[] KmS = ScaleK(Km, smx, smy);
                double c = Cost(geom, th, KmS, invKrefS, refS, rw, rh, modS, mw, mh, planeZ);
                costs[idx] = c;
                if (k == 0 && m == 0) nomCost = c;
                if (c < bestCost) { bestCost = c; bTh = th; bCx = cxOff; bCy = cyOff; }
            }
        }
        // Hysteresis: only move off nominal if clearly better.
        bool moved = bestCost <= HYST * nomCost;
        return moved ? new Coarse(bTh, bCx, bCy, bestCost, nomCost, true)
                     : new Coarse(th0, 0, 0, nomCost, nomCost, false);
    }

    /// <summary>Mean L1 (SAD) alignment cost: warp the reference grid into the module via the θ-pose homography
    /// at the alignment plane, sample the module, accumulate |ref − warpedModule| over the valid overlap.</summary>
    private static double Cost(ModulePose.MirrorGeom geom, double theta, double[] KmS, double[] invKrefS,
                               float[] refS, int rw, int rh, float[] modS, int mw, int mh, double planeZ)
    {
        var pose = ModulePose.MirrorPose(geom, theta);
        // M = KmS · R · invKrefS · Z ; q = KmS · t. Maps ref-grid px → module px (single plane at planeZ).
        double[] M = Mat3.MatMul(Mat3.MatMul(KmS, pose.R), invKrefS);
        for (int i = 0; i < 9; i++) M[i] *= planeZ;
        double[] q = Mat3.MatVec(KmS, pose.t);
        double acc = 0; long n = 0;
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                double pz = M[6] * x + M[7] * y + M[8] + q[2];
                if (pz <= 1e-9) continue;
                double u = (M[0] * x + M[1] * y + M[2] + q[0]) / pz;
                double v = (M[3] * x + M[4] * y + M[5] + q[1]) / pz;
                if (u < 0 || v < 0 || u >= mw - 1 || v >= mh - 1) continue;   // validity mask
                float mval = SampleBilin(modS, mw, mh, (float)u, (float)v);
                acc += Math.Abs(refS[(long)y * rw + x] - mval);
                n++;
            }
        }
        return n > MINV ? acc / n : double.MaxValue;   // guard the <100-overlap "cost 0 wins argmin" bug
    }

    private static (double, double) ProjectRefToMod(ModulePose.MirrorGeom geom, double theta, double[] Kmod,
                                                    double smx, double smy, double[] invKrefS, double x, double y, double planeZ)
    {
        var pose = ModulePose.MirrorPose(geom, theta);
        double[] KmS = ScaleK(Kmod, smx, smy);
        double[] M = Mat3.MatMul(Mat3.MatMul(KmS, pose.R), invKrefS);
        for (int i = 0; i < 9; i++) M[i] *= planeZ;
        double[] q = Mat3.MatVec(KmS, pose.t);
        double pz = M[6] * x + M[7] * y + M[8] + q[2];
        return ((M[0] * x + M[1] * y + M[2] + q[0]) / pz, (M[3] * x + M[4] * y + M[5] + q[1]) / pz);
    }

    private static double[] ScaleK(double[] K, double sx, double sy) => new[]
    { K[0] * sx, K[1] * sx, K[2] * sx, K[3] * sy, K[4] * sy, K[5] * sy, K[6], K[7], K[8] };

    private static float SampleBilin(float[] a, int w, int h, float fx, float fy)
    {
        int x0 = (int)fx, y0 = (int)fy; int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, ty = fy - y0;
        float a00 = a[(long)y0 * w + x0], a01 = a[(long)y0 * w + x1], a10 = a[(long)y1 * w + x0], a11 = a[(long)y1 * w + x1];
        return (a00 * (1 - tx) + a01 * tx) * (1 - ty) + (a10 * (1 - tx) + a11 * tx) * ty;
    }
}
