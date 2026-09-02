using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>The per-capture facts the CalibDataProcessor reads from the CapturedImage / CaptureStack, derived from the LRI protos exactly as the
/// loader does (spec `a19a83f61a2580dcb.md`): the sparse-matching centre (`FUN_1802b39a0`/`FUN_1802b3c90`), the plane depth
/// `FUN_1802acc90`, the mono (uniform-weights) flag of `FUN_1801247b0`, the mirror type ("type 2" test) and the `CDP+0xfc` wide flag.</summary>
public static class CdpInputs
{
    /// <summary>`FUN_1802b39a0`: `roi_center` (default 0.5, 0.5) × the sensor surface size, single-precision.</summary>
    public static (float X, float Y) BasePoint(CameraModule m)
    {
        float x = 0.5f, y = 0.5f;
        if (m.AfInfo?.RoiCenter is { } roi) { x = roi.X; y = roi.Y; }
        int W = m.SensorDataSurface.Size.X, H = m.SensorDataSurface.Size.Y;
        return (x * (float)W, y * (float)H);
    }

    /// <summary>`FUN_1802b3c90`: the base point pushed through `Mat4D(Apply(P,u only) → Apply(pose))` at infinite depth, in pixels of the camera's view image.</summary>
    public static (float X, float Y) Centre(ViewPose pose, CalibDataFull slot, (float X, float Y) p)
    {
        var vFull = ViewTransform.Apply(pose, slot);
        var rel = ViewPose.Identity(); rel.P = (float[])pose.P.Clone(); rel.U = (float[])pose.U.Clone();
        var vRel = ViewTransform.Apply(rel, slot);
        var xf = Mat4D.FlowMatrix(vRel.Basic(), vFull.Basic());
        var v = new float[3];
        for (int r = 0; r < 3; r++) v[r] = (p.Y * xf[4 + r] + p.X * xf[r]) + xf[8 + r];
        float inv = 1.0f / v[2];
        return (v[0] * inv, inv * v[1]);
    }

    /// <summary>The WIDE `addImage` normalisation (`FUN_1802e6e20`): centre ÷ the stereo image size.</summary>
    public static (float X, float Y) WideCentre(CdpCamera c, CameraModule m)
    {
        var cc = Centre(c.Pose, c.Slot, BasePoint(m));
        return (cc.X / (float)c.Image.W, cc.Y / (float)c.Image.H);
    }

    /// <summary>`FUN_1802acc90`: Z from the reference module's AF focus distances (max of contrast / disparity), 1000 when absent or below zMin.</summary>
    public static float PlaneDepth(CameraModule refModule, float zMin)
    {
        float Z;
        var af = refModule.AfInfo;
        if (af is null) Z = 1000.0f;
        else
        {
            Z = af.HasContrastFocusDistance ? af.ContrastFocusDistance : 0.0f;
            if (af.HasDisparityFocusDistance) { float d = af.DisparityFocusDistance; Z = (Z > d) ? Z : d; }   // maxss: the memory operand wins on NaN
            else throw new NotSupportedException("Focus depth from the A1/A5 two-ray triangulation (FUN_180302420 path) is not wired");
        }
        return Z < zMin ? 1000.0f : Z;
    }

    /// <summary>`FUN_1801247b0` red-position rule → the uniform-weights ("gray") flag `(g0 | g1) >> 31`.</summary>
    public static bool Gray(LightHeader h, CameraModule m)
    {
        int type = 0;
        foreach (var cam in h.HwInfo.Camera) if (cam.Id == m.Id) { type = (int)cam.Sensor; break; }
        int g0, g1;
        if (type == 0) { g0 = 1; g1 = 0; }
        else
        {
            int t = type - 1; if (t > 4) throw new InvalidOperationException("unsupported sensor type!");
            g0 = new[] { 0, 1, -1, 1, -1 }[t]; g1 = new[] { 0, 0, -1, 0, -1 }[t];
            if (m.SensorBayerRedOverride is { } o) { g0 = o.X; g1 = o.Y; }
            if (type != 3 && type != 5)
            {
                if (m.SensorIsHorizontalFlip) g0 = g0 == 0 ? 1 : g0 == 1 ? 0 : g0;
                if (m.SensorIsVerticalFlip) g1 = g1 == 0 ? 1 : g1 == 1 ? 0 : g1;
            }
        }
        return ((uint)(g0 | g1) >> 31) != 0;
    }

    /// <summary>`ModuleDesc+0x00` = `GeometricCalibration.mirror_type` (0 NONE, 1 GLUED, 2 MOVABLE) — the "type 2" test of the optimiser states.</summary>
    public static int MirrorType(LightHeader h, CameraID id) => (int)(ModulePose.Geometry(h, id)?.MirrorType ?? GeometricCalibration.Types.MirrorType.None);

    /// <summary>`FUN_1802ad350` → `CDP+0xfc`: the reference module has a per-focus bundle carrying `focus_hall_code`.</summary>
    public static bool WideFlag(LightHeader h, CameraID refId) => ModulePose.Geometry(h, refId)?.PerFocusCalibration.Any(b => b.HasFocusHallCode) ?? false;

    /// <summary>`FUN_1802b42d0`: movable-mirror camera whose AF reported `mirror_timeout`.</summary>
    public static bool StoredResult(LightHeader h, CameraModule m) => MirrorType(h, m.Id) == 2 && m.AfInfo is { HasMirrorTimeout: true, MirrorTimeout: true };
}
