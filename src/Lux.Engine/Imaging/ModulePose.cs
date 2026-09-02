using Ltpb;
using Lux.Engine.Lri;

namespace Lux.Engine.Imaging;

/// <summary>
/// Camera pose for a module: X_module = R · X_ref + t, plus the intrinsic matrix K. Handles the
/// reference camera (identity), canonical extrinsics, and the movable-mirror virtual camera (B/C
/// modules fold the scene off an actuated mirror). Ports the verified Python <c>module_pose.py</c>.
/// </summary>
public static class ModulePose
{
    public readonly record struct Pose(double[] K, double[] R, double[] t, string Kind);

    public static GeometricCalibration? Geometry(LightHeader h, CameraID id)
    {
        foreach (var c in Calibration.ForModule(h, id))
            if (c.Geometry is not null) return c.Geometry;
        return null;
    }

    public static Pose Compute(LriFile lri, string name, int focusIndex = 0)
    {
        var m = lri.Modules[name].Module;
        var g = Geometry(lri.Header, m.Id) ?? throw new InvalidOperationException($"module {name}: no geometry calibration");

        var withK = g.PerFocusCalibration.Where(p => p.Intrinsics is not null).ToList();
        double[] K = Mat3.FromM3(withK[focusIndex].Intrinsics.KMat);

        var withEx = g.PerFocusCalibration.Where(p => p.Extrinsics is not null).Select(p => p.Extrinsics).ToList();
        if (withEx.Count == 0)
            return new Pose(K, (double[])Mat3.I.Clone(), new double[3], "reference");

        var e = withEx[0];
        if (e.Canonical is not null)
        {
            var c = e.Canonical;
            return new Pose(K, Mat3.FromM3(c.Rotation),
                new double[] { c.Translation.X, c.Translation.Y, c.Translation.Z }, "canonical");
        }

        // movable mirror -> virtual camera. The pose is a function of the mirror ANGLE θ (nominally interpolated
        // from the actuator hall-code); the mirror-angle optimizer refines θ per capture (see MirrorGeom below).
        var mg = MirrorGeom.From(e.MoveableMirror, m.MirrorPosition, K);
        return MirrorPose(mg, mg.NominalTheta);
    }

    /// <summary>The movable-mirror geometry needed to build a virtual-camera pose from a mirror angle θ. Holds the
    /// mirror-system constants (rotation axis, zero-degree normal, plane distance, real camera location/orientation,
    /// axis point, handedness flip) plus the module's intrinsics K and the nominal θ (hall-code interpolated). The
    /// mirror-angle optimizer searches/refines θ; <see cref="MirrorPose"/> maps a θ to the (R,t) pose.</summary>
    public sealed record MirrorGeom(double[] Axis, double[] N0, double D, double[] C, double[] Rc, double[] Pax,
                                    bool FlipX, double[] K, double NominalTheta)
    {
        public static MirrorGeom From(GeometricCalibration.Types.Extrinsics.Types.MovableMirrorFormat mm,
                                      float mirrorPosition, double[] K)
        {
            var ms = mm.MirrorSystem;
            var am = mm.MirrorActuatorMapping;
            var pairs = am.ActuatorAnglePairVec.OrderBy(p => p.HallCode).ToList();
            double[] hall = pairs.Select(p => (double)p.HallCode).ToArray();
            double[] ang = pairs.Select(p => (double)p.Angle).ToArray();
            return new MirrorGeom(
                Mat3.Normalize(Mat3.FromP3(ms.RotationAxis)),
                Mat3.Normalize(Mat3.FromP3(ms.MirrorNormalAtZeroDegrees)),
                ms.DistanceMirrorPlaneToPointOnRotationAxis,
                Mat3.FromP3(ms.RealCameraLocation),
                Mat3.FromM3(ms.RealCameraOrientation),
                Mat3.FromP3(ms.PointOnRotationAxis),
                ms.FlipImgAroundX,
                K,
                Mat3.Interp(mirrorPosition, hall, ang));
        }
    }

    /// <summary>Movable-mirror virtual-camera pose for a given mirror angle θ (radians): reflect the real camera
    /// off the mirror plane whose normal is Rodrigues(axis,θ)·n0. This is the exact per-θ pose the mirror-angle
    /// optimizer evaluates (coarse search) and refines (fine Ceres). K is optionally overridden (refined optical
    /// center) via <paramref name="kOverride"/>.</summary>
    public static Pose MirrorPose(MirrorGeom g, double theta, double[]? kOverride = null)
    {
        double[] n = Mat3.MatVec(Mat3.Rodrigues(g.Axis, theta), g.N0);
        double[] Pp = { g.Pax[0] + g.D * n[0], g.Pax[1] + g.D * n[1], g.Pax[2] + g.D * n[2] };
        double[] Hm = Mat3.Reflection(n);
        double cDot = Mat3.Dot(new[] { g.C[0] - Pp[0], g.C[1] - Pp[1], g.C[2] - Pp[2] }, n);
        double[] Cv = { g.C[0] - 2 * cDot * n[0], g.C[1] - 2 * cDot * n[1], g.C[2] - 2 * cDot * n[2] };
        double[] Rv = Mat3.MatMul(Hm, g.Rc);

        // reflection makes det = -1; restore handedness by flipping one image axis (column of Rv).
        int col = g.FlipX ? 1 : 0;
        Rv[col] *= -1; Rv[3 + col] *= -1; Rv[6 + col] *= -1;

        double[] Rt = Mat3.Transpose(Rv);
        double[] t = Mat3.MatVec(Rt, Cv);
        t[0] = -t[0]; t[1] = -t[1]; t[2] = -t[2];
        return new Pose(kOverride ?? g.K, Rt, t, "mirror");
    }

    /// <summary>The movable-mirror geometry for a module (null if the module is not a movable-mirror camera).</summary>
    public static MirrorGeom? Mirror(LriFile lri, string name)
    {
        var m = lri.Modules[name].Module;
        var g = Geometry(lri.Header, m.Id);
        var e = g?.PerFocusCalibration.Where(p => p.Extrinsics is not null).Select(p => p.Extrinsics).FirstOrDefault();
        if (e?.MoveableMirror is null) return null;
        var withK = g!.PerFocusCalibration.Where(p => p.Intrinsics is not null).ToList();
        return MirrorGeom.From(e.MoveableMirror, m.MirrorPosition, Mat3.FromM3(withK[0].Intrinsics.KMat));
    }
}
