using Ltpb;

namespace Lux.Engine.Imaging;

/// <summary>Per-module factory calibration access: WB neutral, ForwardMatrix, vignetting gain grid.</summary>
public static class Calibration
{
    /// <summary>Scene white balance = 1/AWB gains (AsShotNeutral), shared across modules.</summary>
    public static float[] Neutral(LightHeader h)
    {
        var awb = h.ViewPreferences?.AwbGains;
        if (awb is null) return new[] { 1f, 1f, 1f };
        return new[] { awb.R != 0 ? 1f / awb.R : 1f, 1f, awb.B != 0 ? 1f / awb.B : 1f };
    }

    /// <summary>Green VST noise model (var(I)=A·I+B on normalised intensity, σ floored at Threshold), for the
    /// gain bucket nearest this module's analog gain. Drives the noise-adaptive denoise. Null if absent.</summary>
    public static (float A, float B, float Threshold)? GreenVst(LightHeader h, float analogGain)
    {
        var sc = h.SensorData.Count > 0 ? h.SensorData[0].Data : null;
        if (sc is null || sc.VstModel.Count == 0) return null;
        float target = analogGain * 100f;                          // VST 'gain' field ≈ analog_gain·100
        var vm = sc.VstModel.OrderBy(v => Math.Abs(v.Gain - target)).First();
        return ((float)vm.Green.A, (float)vm.Green.B, (float)vm.Threshold);
    }

    /// <summary>
    /// All calibration records for a module. A module's calibration can be SPLIT across several
    /// <c>module_calibration</c> entries (colour in one, geometry/vignetting in others), so callers
    /// must scan all of them (matches the Python <c>lri_meta.calib</c> which gathers across entries).
    /// </summary>
    public static IEnumerable<FactoryModuleCalibration> ForModule(LightHeader h, CameraID id)
    {
        foreach (var c in h.ModuleCalibration)
            if (c.CameraId == id) yield return c;
    }

    /// <summary>
    /// The module's canonical factory extrinsics — rotation (row-major float[9]) and translation (mm, float[3])
    /// relative to the canonical camera, taken from the first focus bundle that carries them. The reference module
    /// is identity/zero by construction. Null when the module has no canonical extrinsics (the movable-mirror B and
    /// C modules carry a mirror format instead on some bundles).
    /// </summary>
    public static (float[] R, float[] T)? CanonicalExtrinsics(LightHeader h, CameraID id)
    {
        foreach (var c in ForModule(h, id))
            foreach (var b in c.Geometry?.PerFocusCalibration ?? Enumerable.Empty<Ltpb.GeometricCalibration.Types.CalibrationFocusBundle>())
            {
                var e = b.Extrinsics?.Canonical;
                if (e is null || e.Rotation is null || e.Translation is null) continue;
                var m = e.Rotation;
                return (new[] { m.X00, m.X01, m.X02, m.X10, m.X11, m.X12, m.X20, m.X21, m.X22 },
                        new[] { e.Translation.X, e.Translation.Y, e.Translation.Z });
            }
        return null;
    }

    /// <summary>ForwardMatrix (camera→XYZ D50) as row-major float[9]; prefers D65, then D50, then A. Null if none.</summary>
    public static float[]? ForwardMatrix(LightHeader h, CameraID id)
    {
        foreach (var pref in new[] { ColorCalibration.Types.IlluminantType.D65, ColorCalibration.Types.IlluminantType.D50, ColorCalibration.Types.IlluminantType.A })
            foreach (var cal in ForModule(h, id))
                foreach (var cc in cal.Color)
                    if (cc.Type == pref)
                    {
                        var m = cc.ForwardMatrix;
                        return new[] { m.X00, m.X01, m.X02, m.X10, m.X11, m.X12, m.X20, m.X21, m.X22 };
                    }
        return null;
    }

    /// <summary>Vignetting gain grid (row-major, width×height) for the first mirror model, or null.</summary>
    public static (int W, int H, float[] Data)? VignetteGrid(LightHeader h, CameraID id)
    {
        foreach (var cal in ForModule(h, id))
        {
            var vc = cal.Vignetting;
            if (vc is not null && vc.Vignetting.Count > 0)
            {
                var vm = vc.Vignetting[0].Vignetting;
                return ((int)vm.Width, (int)vm.Height, vm.Data.ToArray());
            }
        }
        return null;
    }

    /// <summary>Bilinearly upsample a (gw×gh) grid to (W×H), grid nodes spanning the full frame.</summary>
    public static float[] UpsampleGrid(float[] grid, int gw, int gh, int W, int H)
    {
        var outp = new float[(long)W * H];
        for (int y = 0; y < H; y++)
        {
            float fy = gh == 1 ? 0f : (float)y / (H - 1) * (gh - 1);
            int y0 = (int)MathF.Floor(fy); int y1 = Math.Min(y0 + 1, gh - 1); float wy = fy - y0;
            for (int x = 0; x < W; x++)
            {
                float fx = gw == 1 ? 0f : (float)x / (W - 1) * (gw - 1);
                int x0 = (int)MathF.Floor(fx); int x1 = Math.Min(x0 + 1, gw - 1); float wx = fx - x0;
                outp[(long)y * W + x] =
                    grid[y0 * gw + x0] * (1 - wy) * (1 - wx) + grid[y0 * gw + x1] * (1 - wy) * wx +
                    grid[y1 * gw + x0] * wy * (1 - wx) + grid[y1 * gw + x1] * wy * wx;
            }
        }
        return outp;
    }
}
