using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The sparse bundle-adjustment caller `FUN_1802dc770` and its helpers (spec `ae0fffee71d1e9f71.md`): DoubleCam map from the
/// view-transformed stage-1 calibrations (rotation vector = OpenCV Rodrigues of the polar factor, `FUN_180226e60`), observation records (points
/// with Z &gt; 0, per-camera observations &gt; 0), `LightBA` when ≥ 10 points, and the per-camera write-back for cameras with ≥ 26 usable
/// observations: `FUN_1802dc320` (doubles → CalibData, double Rodrigues) followed by `FUN_1802e29f0` (the exact inverse of `Apply`) into the
/// module's CURRENT slot. Also the reprojection evaluator `FUN_1802d87f0`, the camera centre `FUN_180126650` and the WIDE/TELE acceptance rules.
/// </summary>
public static class SparseBaCaller
{
    public sealed class CamInput { public int Cam; public ViewPose Pose = null!; public CalibDataFull Slot = null!; }

    /// <summary>`FUN_1802dc320`: K/t from the doubles, R = Rodrigues(aa) in double (θ ≤ 0 → I), the rest copied from `base`.</summary>
    public static CalibDataFull DoubleCamToCalib(LightBA.DoubleCam dc, CalibDataFull baseCalib)
    {
        var o = baseCalib.Clone();
        double x = dc.Aa[0], y = dc.Aa[1], z = dc.Aa[2];
        double theta2 = ((x * x) + (y * y)) + (z * z), theta = Math.Sqrt(theta2);
        var R = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        if (theta > 0.0)
        {
            double inv = 1.0 / theta; x *= inv; y *= inv; z *= inv;
            double nz = -z, ny = -y, zz = z * nz, s = Math.Sin(theta), c1 = 1.0 - Math.Cos(theta);
            double xy = (x * y) * c1, xz = (x * z) * c1, yz = (y * z) * c1;
            R[0] = 1.0 + (zz - y * y) * c1; R[1] = xy + nz * s; R[2] = xz + y * s;
            R[3] = xy + z * s; R[4] = (zz - x * x) * c1 + 1.0; R[5] = yz - x * s;
            R[6] = xz - y * s; R[7] = yz + x * s; R[8] = c1 * (ny * y - x * x) + 1.0;
        }
        for (int i = 0; i < 9; i++) { o.K[i] = (float)dc.K[i]; o.R[i] = (float)R[i]; }
        for (int i = 0; i < 3; i++) o.T[i] = (float)dc.T[i];
        return o;
    }

    /// <summary>`FUN_1802e29f0`: the exact inverse of `ViewTransform.Apply` (Qᵀ·R, reciprocal scales, negated shifts, R·P and t + (R'·Pᵀ)·u).</summary>
    public static CalibDataFull InverseApply(ViewPose p, CalibDataFull inp)
    {
        var o = inp.Clone();
        var Rp = new float[9]; var Q = p.Q; var R = inp.R;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Rp[3 * i + j] = (Q[i] * R[j] + Q[3 + i] * R[3 + j]) + Q[6 + i] * R[6 + j];
        o.R = Rp;
        o = ViewTransform.Scale(o, 1.0f / p.Scale3.X, 1.0f / p.Scale3.Y);
        o = ViewTransform.Shift(o, -p.Shift3.X, -p.Shift3.Y);
        o = ViewTransform.Scale(o, 1.0f / p.Scale2.X, 1.0f / p.Scale2.Y);
        o = ViewTransform.Shift(o, -p.Shift2.X, -p.Shift2.Y);
        o = ViewTransform.Scale(o, 1.0f / p.Scale1.X, 1.0f / p.Scale1.Y);
        o = ViewTransform.Shift(o, -p.Shift1.X, -p.Shift1.Y);
        var P = p.P; var N = new float[9];
        N[0] = (Rp[2] * P[6] + Rp[0] * P[0]) + Rp[1] * P[3];
        N[1] = (Rp[2] * P[7] + Rp[0] * P[1]) + Rp[1] * P[4];
        N[2] = (Rp[0] * P[2] + Rp[1] * P[5]) + Rp[2] * P[8];
        for (int i = 1; i < 3; i++) for (int j = 0; j < 3; j++) N[3 * i + j] = (Rp[3 * i] * P[j] + Rp[3 * i + 1] * P[3 + j]) + Rp[3 * i + 2] * P[6 + j];
        var M = new float[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[3 * i + j] = (N[3 * i] * P[3 * j] + N[3 * i + 1] * P[3 * j + 1]) + N[3 * i + 2] * P[3 * j + 2];
        var t = inp.T; var u = p.U; var tn = new float[3];
        for (int i = 0; i < 3; i++) tn[i] = (t[i] + M[3 * i + 2] * u[2]) + (M[3 * i] * u[0] + M[3 * i + 1] * u[1]);
        o.T = tn; o.R = N;
        return o;
    }

    /// <summary>`FUN_180126650(img, stage)`: camera centre `−(Rᵀ·t)` of a slot.</summary>
    public static float[] Centre(float[] R, float[] t) => new[] { -((R[0] * t[0] + R[3] * t[1]) + R[6] * t[2]), -((R[1] * t[0] + R[4] * t[1]) + R[7] * t[2]), -((R[2] * t[0] + R[5] * t[1]) + R[8] * t[2]) };

    /// <summary>`FUN_1802d87f0`: mean reprojection distance of the points with positive observations through `Apply(pose, slot)` (view K only).</summary>
    public static float MeanReprojection(ViewPose pose, CalibDataFull slot, TriPoint[] points, (float X, float Y)[] obs)
    {
        var v = ViewTransform.Apply(pose, slot); float[] R = v.R, K = v.K, t = v.T;
        float err = 0f; int n = 0;
        for (int i = 0; i < points.Length; i++)
        {
            float u = obs[i].X, w = obs[i].Y; var pt = points[i];
            if (!(0f < u && 0f < w && 0f < pt.Z)) continue;
            float X = pt.X, Y = pt.Y, Z = pt.Z;
            float p0 = ((Y * R[1] + X * R[0]) + R[2] * Z) + t[0], p1 = ((Y * R[4] + X * R[3]) + R[5] * Z) + t[1], p2 = ((Y * R[7] + X * R[6]) + R[8] * Z) + t[2];
            float nu = (K[0] * p0 + K[1] * p1) + K[2] * p2, nv = (K[3] * p0 + K[4] * p1) + K[5] * p2, ww = (K[6] * p0 + K[7] * p1) + K[8] * p2;
            float inv = 1.0f / ww;
            float du = nu * inv - u, dv = inv * nv - w;
            float d2 = dv * dv + du * du;
            float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(d2)).ToScalar(), h = d2 * rs;
            float d = d2 == 0f ? 0f : ((h * rs) + (-3.0f)) * (h * (-0.5f));
            err += d; n++;
        }
        return (float)((double)err / ((double)n + 1e-7));
    }

    public sealed class Result { public List<LightBA.DoubleCam> DoubleCams = null!; public LightBA? Ba; public Dictionary<int, CalibDataFull> Written = new(); public int UsablePoints; }

    /// <summary>`FUN_1802dc770`. `slots[cam]` = the module's CURRENT-slot CalibData (fresh), `poses[cam]` its view pose; `obsMap` = per-camera observation vectors.</summary>
    public static Result Run(TriPoint[] points, SortedDictionary<int, (float X, float Y)[]> obsMap, int refCam, Dictionary<int, CamInput> cams, bool isHigher, uint mask, HashSet<int> exclusion, bool b8, bool b9, Action<string>? log = null)
    {
        int Group(int c) => c <= 4 ? 0 : c <= 9 ? 1 : 2;
        var dcams = new SortedDictionary<long, LightBA.DoubleCam>();
        LightBA.DoubleCam Build(int cam, long key)
        {
            var ci = cams[cam]; var view = ViewTransform.Apply(ci.Pose, ci.Slot);
            var Rd = new double[9]; for (int i = 0; i < 9; i++) Rd[i] = view.R[i];
            var dc = new LightBA.DoubleCam { Key = key, Aa = LightBA.RotationVector(Rd) };
            for (int i = 0; i < 9; i++) dc.K[i] = view.K[i];
            for (int i = 0; i < 3; i++) dc.T[i] = view.T[i];
            return dc;
        }
        dcams[0] = Build(refCam, 0); dcams[0].Mask = 0;
        foreach (var cam in obsMap.Keys)
        {
            var dc = Build(cam, cam); dcams[cam] = dc;
            int m = exclusion.Contains(cam) ? 0 : (!isHigher ? (Group(cam) == Group(refCam) ? (int)mask : 0) : (Group(cam) == Group(refCam) ? 0 : (int)mask));
            dc.Mask = m;
        }
        var obs = new List<LightBA.Obs>();
        for (int i = 0; i < points.Length; i++)
        {
            if (!(0.0f < points[i].Z)) continue;
            var rec = new LightBA.Obs { X = new[] { points[i].X, points[i].Y, points[i].Z } };
            rec.Uv[0] = (points[i].U, points[i].V);
            foreach (var kv in obsMap) { var (x, y) = kv.Value[i]; if (0.0f < x && 0.0f < y) rec.Uv[kv.Key] = (x, y); }
            obs.Add(rec);
        }
        var res = new Result { DoubleCams = dcams.Values.Select(c => c.Clone()).ToList(), UsablePoints = obs.Count };
        if (obs.Count <= 9) { log?.Invoke($"BA caller: only {obs.Count} points — no solve"); return res; }
        var ba = new LightBA(dcams.Values, obs, true, b8, b9);
        ba.Solve(log); res.Ba = ba;
        var solved = ba.Cams.ToDictionary(c => c.Key);
        foreach (var kv in obsMap)
        {
            int cam = kv.Key; int usable = 0;
            for (int i = 0; i < points.Length; i++) if (0.0f < points[i].Z && 0.0f < kv.Value[i].X && 0.0f < kv.Value[i].Y) usable++;
            if (usable <= 0x19) continue;
            var calibF = DoubleCamToCalib(solved[cam], cams[cam].Slot);
            res.Written[cam] = InverseApply(cams[cam].Pose, calibF);
        }
        return res;
    }
}
