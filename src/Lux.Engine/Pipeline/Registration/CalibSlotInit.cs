using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `FUN_180251c40`: the load-time CURRENT/FACTORY calibration slot of a CapturedImage (spec `a95790d8748b8d281.md`): K from
/// the per-focus `k_mat`s interpolated/extrapolated in `focus_hall_code` by `CameraModule.lens_position` (`FUN_1802682b0`: fx/cx with a true
/// reciprocal, fy/cy with `rcpps` + one Newton step), scaled by `data_scale`; R/t from the canonical extrinsics (NONE/GLUED mirrors) or the mirror
/// pose at the actuator angle of `mirror_position` (MOVABLE); the full `CalibData` adds K2 from the polynomial distortion centre/normalisation and
/// the coefficient vector (`FUN_180307b40`).
/// </summary>
public static class CalibSlotInit
{
    /// <summary>One merged per-focus bundle (equal `focus_distance` keys re-assign the node; only present Optionals overwrite).</summary>
    sealed class Bundle { public float FocusDistance; public float? K0, K1, K2, K3, K4, K5, K6, K7, K8; public bool HasK; public float? HallCode; public GeometricCalibration.Types.Extrinsics? Ext; }

    static SortedDictionary<float, Bundle> Bundles(GeometricCalibration g)
    {
        var map = new SortedDictionary<float, Bundle>();
        foreach (var b in g.PerFocusCalibration)
        {
            if (!map.TryGetValue(b.FocusDistance, out var m)) { m = new Bundle { FocusDistance = b.FocusDistance }; map[b.FocusDistance] = m; }
            if (b.Intrinsics is { } it && it.KMat is { } k) { m.HasK = true; m.K0 = k.X00; m.K1 = k.X01; m.K2 = k.X02; m.K3 = k.X10; m.K4 = k.X11; m.K5 = k.X12; m.K6 = k.X20; m.K7 = k.X21; m.K8 = k.X22; }
            if (b.HasFocusHallCode) m.HallCode = b.FocusHallCode;
            if (b.Extrinsics is not null) m.Ext = b.Extrinsics;
        }
        return map;
    }

    static float Rcp(float d) { float r = Sse.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar(); return ((1.0f - d * r) * r) + r; }

    /// <summary>`FUN_180251a40` + `FUN_1802682b0`: the module K (double) for `lensPosition`.</summary>
    public static double[] Intrinsics(GeometricCalibration g, int lensPosition)
    {
        var Ks = new List<double[]>(); var halls = new List<int>(); bool anyHall = false, anyMissing = false;
        foreach (var b in Bundles(g).Values)
        {
            if (!b.HasK) continue;
            Ks.Add(new double[] { b.K0!.Value, b.K1!.Value, b.K2!.Value, b.K3!.Value, b.K4!.Value, b.K5!.Value, b.K6!.Value, b.K7!.Value, b.K8!.Value });
            if (b.HallCode is { } hc) { halls.Add((int)hc); anyHall = true; } else anyMissing = true;
        }
        if (Ks.Count == 0) throw new InvalidOperationException("Failed to get intrinsics!");
        var K = (double[])Ks[0].Clone();
        if (!anyHall) return K;
        if (anyMissing) throw new InvalidOperationException("Sizes do not match!");
        int n = Ks.Count; var xs = halls.Select(h => (float)h).ToArray(); var idx = Enumerable.Range(0, n).ToArray();
        for (bool swapped = true; swapped;) { swapped = false; for (int i = 0; i + 1 < n; i++) if (xs[i + 1] < xs[i]) { (xs[i], xs[i + 1]) = (xs[i + 1], xs[i]); (idx[i], idx[i + 1]) = (idx[i + 1], idx[i]); swapped = true; } }
        float x = (float)lensPosition;
        if (n == 1) return K;
        if (n == 2)
        {
            var K0 = Ks[idx[0]]; var K1 = Ks[idx[1]];
            float dx = xs[1] - xs[0]; if (Math.Abs((double)dx) < 0.001) throw new InvalidOperationException("x_1 and x_2 are very close. Slope close to infinity.");
            float inv = 1.0f / dx, t = x - xs[0];
            float a0 = (float)K0[0], a1 = (float)K1[0]; K[0] = (((a1 - a0) * t) * inv) + a0;
            float c0 = (float)K0[2], c1 = (float)K1[2]; K[2] = (((c1 - c0) * t) * inv) + c0;
            float y00 = (float)K0[4], y01 = (float)K0[5], y10 = (float)K1[4], y11 = (float)K1[5]; float r = Rcp(dx);
            K[4] = ((t * (y10 - y00)) * r) + y00; K[5] = ((t * (y11 - y01)) * r) + y01;
            return K;
        }
        if (!(xs[0] < xs[1] && xs[1] < xs[2])) throw new InvalidOperationException("x ordering wrong, need ascending order");
        foreach (int j in new[] { 0, 4, 2, 5 })
        {
            float xa, xb; double[] Ka, Kb;
            if (xs[1] <= x) { xa = xs[1]; xb = xs[2]; Ka = Ks[idx[1]]; Kb = Ks[idx[2]]; } else { xa = xs[0]; xb = xs[1]; Ka = Ks[idx[0]]; Kb = Ks[idx[1]]; }
            float ddx = xb - xa; if (Math.Abs((double)ddx) < 0.001) throw new InvalidOperationException("x_1 and x_2 are very close. Slope close to infinity.");
            K[j] = ((x - xa) * (((float)Kb[j] - (float)Ka[j]) / ddx)) + (float)Ka[j];
        }
        return K;
    }

    /// <summary>`FUN_1802516b0`: the mirror system and actuator mapping of the first bundle (key order) carrying a movable mirror.</summary>
    public static (MirrorSystem Sys, ActuatorMapping Map)? Mirror(GeometricCalibration g)
    {
        foreach (var b in Bundles(g).Values)
        {
            if (b.Ext?.MoveableMirror is not { } mm) continue;
            var ms = mm.MirrorSystem; var s = new MirrorSystem
            {
                Axis = new double[] { ms.RotationAxis.X, ms.RotationAxis.Y, ms.RotationAxis.Z }, N0 = new double[] { ms.MirrorNormalAtZeroDegrees.X, ms.MirrorNormalAtZeroDegrees.Y, ms.MirrorNormalAtZeroDegrees.Z },
                D = ms.DistanceMirrorPlaneToPointOnRotationAxis, Cv = new double[] { ms.RealCameraLocation.X, ms.RealCameraLocation.Y, ms.RealCameraLocation.Z }, P0 = new double[] { ms.PointOnRotationAxis.X, ms.PointOnRotationAxis.Y, ms.PointOnRotationAxis.Z },
                Flip = ms.FlipImgAroundX,
            };
            var o = ms.RealCameraOrientation; s.R0 = new double[] { o.X00, o.X01, o.X02, o.X10, o.X11, o.X12, o.X20, o.X21, o.X22 };
            var am = mm.MirrorActuatorMapping; var q = am.QuadraticModel;
            var map = new ActuatorMapping { Coeffs = q.ModelCoeffs.Select(v => (double)v).ToArray(), UseRplusLeft = q.UseRplusForLeftSegment, UseRplusRight = q.UseRplusForRightSegment, Inflection = q.InflectionValue, MIn = am.ActuatorLengthOffset, SIn = am.ActuatorLengthScale, MOut = am.MirrorAngleOffset, SOut = am.MirrorAngleScale, XformType = (int)am.TransformationType };
            return (s, map);
        }
        return null;
    }

    /// <summary>`FUN_180251950`: the canonical extrinsics of the first bundle (key order) carrying one.</summary>
    public static (double[] R, double[] T)? Canonical(GeometricCalibration g)
    {
        foreach (var b in Bundles(g).Values)
        {
            if (b.Ext?.Canonical is not { } c) continue;
            var r = c.Rotation; var t = c.Translation;
            return (new double[] { r.X00, r.X01, r.X02, r.X10, r.X11, r.X12, r.X20, r.X21, r.X22 }, new double[] { t.X, t.Y, t.Z });
        }
        return null;
    }

    /// <summary>`FUN_180251c40` + `FUN_180307b40`: the module's load-time CalibData (CURRENT == FACTORY).</summary>
    public static CalibDataFull Build(LriFile lri, string module)
    {
        var m = lri.Modules[module].Module;
        var g = ModulePose.Geometry(lri.Header, m.Id) ?? throw new InvalidOperationException($"{module}: no geometry calibration");
        var Kd = Intrinsics(g, m.LensPosition);
        var K = new float[9]; for (int i = 0; i < 9; i++) K[i] = (float)Kd[i];
        float sx = m.SensorDataSurface.DataScale?.X ?? 0f, sy = m.SensorDataSurface.DataScale?.Y ?? 0f; if (sx == 0f && sy == 0f) { sx = 1f; sy = 1f; }
        K[0] *= sx; K[2] = sx * K[2]; K[4] *= sy; K[5] = sy * K[5];
        double[] Rd, Td;
        if ((int)g.MirrorType == 2)
        {
            var mir = Mirror(g) ?? throw new InvalidOperationException("Failed to get mirror extrinsics for movable module!");
            double theta = mir.Map.Angle((double)m.MirrorPosition);
            (Rd, Td) = MirrorPose.WorldToCamera(mir.Sys, theta);
        }
        else (Rd, Td) = Canonical(g) ?? throw new InvalidOperationException("Failed to get canonical extrinsics for non movable module!");
        var c = new CalibDataFull { K = K };
        for (int i = 0; i < 9; i++) c.R[i] = (float)Rd[i];
        for (int i = 0; i < 3; i++) c.T[i] = (float)Td[i];
        var poly = g.Distortion.Polynomial;
        c.Dist = poly.Coeffs.ToArray();
        c.K2 = new float[] { poly.Normalization.X * sx, 0f, sx * poly.DistortionCenter.X, 0f, poly.Normalization.Y * sy, sy * poly.DistortionCenter.Y, 0f, 0f, 1f };
        return c;
    }
}
