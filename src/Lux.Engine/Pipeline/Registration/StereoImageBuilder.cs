using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoISP::CreateStereoImage` (spec `aafa23577277adef4.md`) assembled from the verified pieces: the module ISP at half
/// resolution with the shared reference Stats, the exposure gain (`FUN_1801ea0c0`), the Lanczos-3 warp through the (view, module) aligned
/// calibration into `size`, `ConvertToYUV`, then — reference (p12): store the YUV float image and 8-bit it; non-reference with the reference
/// crop (p13): the colour transfer onto the reference crop; higher group (p12 = p13 = 0): plain 8-bit. Mono sensors take the mono ISP path.
/// Returns the 8-bit image and the saturation level `val` (255, or the mono code value).
/// </summary>
public static class StereoImageBuilder
{
    public sealed class Distortion { public RatPolyMapping Poly = null!; public float PpX, PpY, Pix; }

    public static Distortion DistortionOf(LriFile lri, string module)
    {
        var geo = ModulePose.Geometry(lri.Header, lri.Modules[module].Module.Id) ?? throw new InvalidOperationException($"{module}: no geometry calibration");
        var poly = geo.Distortion.Polynomial; var cra = geo.Distortion.Cra;
        return new Distortion { Poly = new RatPolyMapping(poly.DistortionCenter.X, poly.DistortionCenter.Y, poly.Normalization.X, poly.Normalization.Y, poly.Coeffs.ToArray()), PpX = cra.DistortionCenter.X, PpY = cra.DistortionCenter.Y, Pix = cra.PixelSize };
    }

    /// <summary>`relative_brightness` of the module's vignetting calibration (first record), if present.</summary>
    public static float? RelativeBrightness(CapturedFrame f)
    {
        foreach (var c in Calibration.ForModule(f.Header, f.Module.Id)) if (c.Vignetting is not null) return c.Vignetting.HasRelativeBrightness ? c.Vignetting.RelativeBrightness : null;
        return null;
    }

    /// <summary>The Bayer SoftISP for one frame with the reference capture's Stats attached (`StereoAsyncAPI::start`: stats from the reference).</summary>
    public static SoftIsp Isp(CapturedFrame frame, Color.LumenProfile profile, IspStats? refStats)
    {
        var isp = new SoftIsp(StereoImage.BuildTuning(frame.Info.HasHotPixelLeakageCalibration), profile);
        if (refStats is not null) isp.UseStats(refStats);
        return isp;
    }

    public sealed class Result { public byte[] Rgba8 = null!; public float Val; public int W, H; public float[]? RefYuv; }

    /// <summary>One `CreateStereoImage` call. `view` = calibA (the aligned/cropped view), `module` = calibB (the module's own half-res record),
    /// `refView` = calib2 (only for the reference-crop colour transfer), `refYuv` = the reference YUV float image (in for non-ref, out for the ref).</summary>
    public static Result Create(CapturedFrame frame, CapturedFrame refFrame, SoftIsp isp, CameraCalib view, CameraCalib module, (int W, int H) size, float[] neutral,
                                float[]? refYuv, CameraCalib? refView, bool isReference, bool useRefCrop, Distortion dist, Action<string>? log = null)
    {
        int dw = size.W, dh = size.H;
        var ac = AlignedCalib.Build(view, module, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);
        float g = isReference ? 1f : StereoColorTransfer.ExposureGain(refFrame.Module.SensorAnalogGain, refFrame.Module.SensorExposure, RelativeBrightness(refFrame),
                                                                        frame.Module.SensorAnalogGain, frame.Module.SensorExposure, RelativeBrightness(frame), frame.Info.IsColour);
        if (!frame.Info.IsColour)
        {   // MONO path (lambda_3): mono ISP → half-res RGBA → warp → 8-bit code values; val = 255·|s|^(1/2.2)
            var noise = frame.Info.Noise ?? throw new InvalidOperationException("mono path needs the sensor noise model");
            var mono = StereoImage.RunMonoIsp(frame, noise.Black, noise.White, g);
            var half = StereoImage.MonoToHalfRgba(mono, frame.Width, frame.Height, out int mw, out int mh);
            var warpedM = StereoImage.Warp(half, mw, mh, dw, dh, ac.H, ac.Lut, ac.Cx, ac.Cy, ac.Sx, ac.Sy);
            var (rgba8m, ret) = StereoImage.MonoToBytes(warpedM, dw, dh, (int)frame.Info.Sensor);
            return new Result { Rgba8 = rgba8m, Val = ret, W = dw, H = dh };
        }
        var src = StereoImage.RunIspTiled(isp, frame, 2, out int w, out int h, log);
        if (g != 1f) for (int k = 0; k < src.Length; k++) src[k] *= g;
        var warped = StereoImage.Warp(src, w, h, dw, dh, ac.H, ac.Lut, ac.Cx, ac.Cy, ac.Sx, ac.Sy);
        var M = StereoImage.YuvMatrix((int)frame.Info.Sensor, neutral);
        StereoImage.ConvertToYuv(warped, dw, dh, M);
        if (isReference)
        {
            if (refYuv is not null) throw new InvalidOperationException("expect an empty reference image.");
            return new Result { Rgba8 = StereoImage.ToRgba8(warped, dw, dh), Val = 255f, W = dw, H = dh, RefYuv = warped };
        }
        if (useRefCrop)
        {
            if (refYuv is null || refView is null) throw new InvalidOperationException("Empty reference yuv.");
            CalibData Cd(CameraCalib c) => new() { K = c.K.ToArray(), T = c.T.ToArray(), R = c.R.ToArray() };
            var (outp, _, _, _, _) = StereoColorTransfer.Transfer(warped, dw, dh, refYuv, dw, dh, Cd(refView), Cd(view), log);
            return new Result { Rgba8 = StereoImage.ToRgba8(outp, dw, dh), Val = 255f, W = dw, H = dh };
        }
        return new Result { Rgba8 = StereoImage.ToRgba8(warped, dw, dh), Val = 255f, W = dw, H = dh };
    }
}
