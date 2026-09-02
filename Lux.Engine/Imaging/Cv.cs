using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Lux.Engine.Imaging;

/// <summary>
/// Thin float-image helpers over OpenCvSharp: the image operations the fusion needs that are hard to
/// hand-roll (Gaussian blur, pyramids, remap/resize, distance transform, Farneback flow, bilateral).
/// Everything is a row-major <c>float[]</c> of length <c>w*h</c>; heavy elementwise math stays in plain C#.
/// </summary>
public static class Cv
{
    /// <summary>Wrap a float[] as a CV_32FC1 Mat (no copy — keep <paramref name="a"/> alive while used).</summary>
    private static Mat F(float[] a, int w, int h) => Mat.FromPixelData(h, w, MatType.CV_32FC1, a);

    /// <summary>Copy a continuous single-channel float Mat back into a new float[].</summary>
    private static float[] Out(Mat m)
    {
        int n = (int)(m.Rows * m.Cols);
        var a = new float[n];
        Marshal.Copy(m.Data, a, 0, n);
        return a;
    }

    public static float[] GaussianBlur(float[] a, int w, int h, double sigma)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.GaussianBlur(src, dst, new Size(0, 0), sigma, sigma, BorderTypes.Reflect101);
        return Out(dst);
    }

    /// <summary>|a| elementwise then Gaussian blur — common in the flow/confidence math.</summary>
    public static float[] Resize(float[] a, int w, int h, int newW, int newH, InterpolationFlags interp)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.Resize(src, dst, new Size(newW, newH), 0, 0, interp);
        return Out(dst);
    }

    public static (float[] Data, int W, int H) PyrDown(float[] a, int w, int h)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.PyrDown(src, dst, new Size((w + 1) / 2, (h + 1) / 2));
        return (Out(dst), dst.Cols, dst.Rows);
    }

    public static float[] PyrUp(float[] a, int w, int h, int newW, int newH)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.PyrUp(src, dst, new Size(newW, newH));
        return Out(dst);
    }

    /// <summary>Bilinear remap: dst[i] = src(mapX[i], mapY[i]); out-of-range → 0 (constant border).</summary>
    public static float[] Remap(float[] src, int sw, int sh, float[] mapX, float[] mapY, int dw, int dh)
    {
        using var s = F(src, sw, sh);
        using var mx = F(mapX, dw, dh);
        using var my = F(mapY, dw, dh);
        using var dst = new Mat();
        Cv2.Remap(s, dst, mx, my, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        return Out(dst);
    }

    /// <summary>Remap with replicate border (used to fill warped tiles at their edges).</summary>
    public static float[] RemapReplicate(float[] src, int sw, int sh, float[] mapX, float[] mapY, int dw, int dh)
    {
        using var s = F(src, sw, sh);
        using var mx = F(mapX, dw, dh);
        using var my = F(mapY, dw, dh);
        using var dst = new Mat();
        Cv2.Remap(s, dst, mx, my, InterpolationFlags.Linear, BorderTypes.Replicate);
        return Out(dst);
    }

    /// <summary>Farneback dense optical flow a→b; returns (flowX, flowY) each length w*h.</summary>
    public static (float[] Fx, float[] Fy) Farneback(byte[] a, byte[] b, int w, int h)
    {
        using var ma = Mat.FromPixelData(h, w, MatType.CV_8UC1, a);
        using var mb = Mat.FromPixelData(h, w, MatType.CV_8UC1, b);
        using var flow = new Mat();
        Cv2.CalcOpticalFlowFarneback(ma, mb, flow, 0.5, 5, 25, 5, 7, 1.5, OpticalFlowFlags.FarnebackGaussian);
        int n = w * h;
        var interleaved = new float[n * 2];
        Marshal.Copy(flow.Data, interleaved, 0, n * 2);
        var fx = new float[n]; var fy = new float[n];
        for (int i = 0; i < n; i++) { fx[i] = interleaved[i * 2]; fy[i] = interleaved[i * 2 + 1]; }
        return (fx, fy);
    }

    /// <summary>Euclidean distance transform of a binary mask: distance (px) to the nearest zero.</summary>
    public static float[] DistanceTransform(bool[] mask, int w, int h)
    {
        var bytes = new byte[w * h];
        for (int i = 0; i < mask.Length; i++) bytes[i] = mask[i] ? (byte)255 : (byte)0;
        using var src = Mat.FromPixelData(h, w, MatType.CV_8UC1, bytes);
        using var dst = new Mat();
        Cv2.DistanceTransform(src, dst, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        return Out(dst);
    }

    public static float[] BilateralFilter(float[] a, int w, int h, int d, double sigmaColor, double sigmaSpace)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.BilateralFilter(src, dst, d, sigmaColor, sigmaSpace);
        return Out(dst);
    }

    public static float[] Filter2D(float[] a, int w, int h, float[] kernel, int kw, int kh)
    {
        using var src = F(a, w, h);
        using var k = Mat.FromPixelData(kh, kw, MatType.CV_32FC1, kernel);
        using var dst = new Mat();
        Cv2.Filter2D(src, dst, MatType.CV_32FC1, k);
        return Out(dst);
    }

    /// <summary>Normalised box (mean) filter over a (2·radius+1) window. Reflect border. Used for guided filter.</summary>
    public static float[] BoxFilter(float[] a, int w, int h, int radius)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        int k = 2 * radius + 1;
        Cv2.BoxFilter(src, dst, MatType.CV_32FC1, new Size(k, k), normalize: true, borderType: BorderTypes.Reflect101);
        return Out(dst);
    }

    /// <summary>3×3 median of a float image (matches scipy median_filter size=3). OpenCV supports 32F at ksize 3.</summary>
    public static float[] Median3(float[] a, int w, int h)
    {
        using var src = F(a, w, h);
        using var dst = new Mat();
        Cv2.MedianBlur(src, dst, 3);
        return Out(dst);
    }
}
