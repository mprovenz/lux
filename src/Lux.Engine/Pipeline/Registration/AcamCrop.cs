using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Geometry;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `FUN_180185b60(rect, CapturedImage*)` (spec `a2f2b4866a7ff17e1.md`): the A-camera "valid region" after undistortion —
/// the border of the distorted frame is mapped through the inverse radial distortion (30-knot LUT from `FUN_1801855a0`, the same
/// curve as the CRA LUT, in pixels) and the inscribed axis-aligned rect is returned; the StereoAsyncAPI ctor turns it into
/// `shift1 = (x0, y0)`, `scale1 = max(w/(x1−x0), h/(y1−y0))` (`FUN_180304720`, uniform). Op-exact per the disassembly notes.
/// </summary>
public static class AcamCrop
{
    static readonly float Inv90 = BitConverter.Int32BitsToSingle(0x3c360b61), Inv120 = BitConverter.Int32BitsToSingle(0x3c088889);
    const double Third = 1.0 / 3.0, Sixth = 1.0 / 6.0;

    /// <summary>`FUN_1801855a0` + the caller's post-processing: LUT abscissa `Yc` (distorted radius, px) and values `Dc = X − Y` (px).</summary>
    public static (float[] Yc, float[] Dc, float X0, float InvDx) Table(RatPolyMapping poly, float pixMm, float refScaleX, float scaleX)
    {
        double inv = refScaleX > 0f ? (double)(1.0f / refScaleX) : 1.0;
        float P = (float)((double)pixMm * inv);
        float k = P / scaleX, invk = 1.0f / k;
        const int n = 30; var X = new float[n]; var Y = new float[n];
        float cx = poly.CenterX, cy = poly.CenterY;
        for (int i = 0; i < n; i++)
        {
            float Xi = (float)i * 0.1f;
            float x = (Xi * invk) + cx;
            var (ox, _) = poly.Map(x, cy);
            Y[i] = (ox - cx) * k; X[i] = Xi;
        }
        float invP = 1.0f / P; var D = new float[n];
        for (int i = 0; i < n; i++) { X[i] = X[i] * invP; Y[i] = Y[i] * invP; D[i] = X[i] - Y[i]; }
        float x0 = Y[0], dx = (Y[n - 1] - Y[0]) / (float)(n - 1), invdx = 1.0f / dx;
        return (Y, D, x0, invdx);
    }

    /// <summary>§3: the radial factor q = r'/r for a squared radius r2 (rsqrt lane = scalar or packed — same per lane on one CPU).</summary>
    static float Q(float r2, float[] Dc, float x0, float invdx)
    {
        float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar();
        float a = r2 * rs, b = a * (-0.5f), c = (a * rs) + (-3.0f);
        float r = r2 != 0f ? (b * c) : 0f;
        float invr = (rs * (-0.5f)) * c;
        float u = (r - x0) * invdx;
        int k = (int)u; if (u != u || u >= 2147483648f || u < -2147483648f) k = int.MinValue;
        k = Math.Max(Math.Min(k, 27), 1);
        float t = u - (float)k, t2 = t * t, w = 1f - (t * 0.5f);
        float B = (Dc[k + 1] * (t2 + t)) + (Dc[k] * (1f - t2));
        float W3 = (float)((double)((t2 - t) * w) * Third), W2 = (float)((double)((t2 + (-1f)) * t) * Sixth);
        float rp = (((B * w) + r) + (Dc[k - 1] * W3)) + (Dc[k + 2] * W2);
        return rp * invr;
    }

    public static RectI Compute(int w, int h, float scaleX, RatPolyMapping poly, float pixMm, float refScaleX)
    {
        var (_, Dc, x0, invdx) = Table(poly, pixMm, refScaleX, scaleX);
        float cx = poly.CenterX, cy = poly.CenterY;
        float cx2 = cx * cx, cy2 = cy * cy, ex = (float)(w - 1) - cx, ex2 = ex * ex, ey = (float)(h - 1) - cy, ey2 = ey * ey;
        float H90 = (float)h * Inv90, W120 = (float)w * Inv120;
        // pass 1: left/right edges sampled in y, i = 0..90 (lanes 0..87 packed, tail 88..90) — per-lane math is identical, reductions below
        var xl = new float[91]; var xr = new float[91];
        for (int i = 0; i <= 90; i++)
        {
            float ty = (float)i * H90, dy = ty - cy, V2 = dy * dy;
            xl[i] = cx - (Q(V2 + cx2, Dc, x0, invdx) * cx);
            xr[i] = (Q(V2 + ex2, Dc, x0, invdx) * ex) + cx;
        }
        float maxL = ReduceMax(xl, 88, 0f), minR = ReduceMin(xr, 88, (float)(w - 1));
        for (int i = 88; i <= 90; i++) { maxL = MaxSs(maxL, xl[i]); minR = MinSs(minR, xr[i]); }
        // pass 2: top/bottom edges sampled in x, i = 0..119 packed + one tail sample at tx = (float)w
        var yt = new float[121]; var yb = new float[121];
        for (int i = 0; i <= 120; i++)
        {
            float tx = i < 120 ? (float)i * W120 : (float)w, dx = tx - cx, V2 = dx * dx;
            yt[i] = cy - (Q(cy2 + V2, Dc, x0, invdx) * cy);
            yb[i] = (Q(V2 + ey2, Dc, x0, invdx) * ey) + cy;
        }
        float maxT = ReduceMax(yt, 120, 0f), minB = ReduceMin(yb, 120, (float)(h - 1));
        maxT = MaxSs(yt[120], maxT); minB = MinSs(minB, yb[120]);
        int X0 = (int)maxL, Y0 = (int)maxT;
        int X1 = X0 + (int)((minR + 1.0f) - maxL), Y1 = Y0 + (int)((minB + 1.0f) - maxT);
        return new RectI(X0, Y0, X1, Y1);
    }

    // packed accumulation: 4 lanes (i mod 4), maxps/minps per lane from the initial value, then the horizontal reduction of the disassembly
    static float ReduceMax(float[] v, int nPacked, float init)
    {
        var m = new[] { init, init, init, init };
        for (int i = 0; i < nPacked; i++) m[i & 3] = MaxPs(m[i & 3], v[i]);
        float A = MaxPs(m[2], m[0]), B = MaxPs(m[3], m[1]);
        return (B < A) ? A : B;
    }
    static float ReduceMin(float[] v, int nPacked, float init)
    {
        var m = new[] { init, init, init, init };
        for (int i = 0; i < nPacked; i++) m[i & 3] = MinPs(m[i & 3], v[i]);
        float a = MinPs(m[2], m[0]), b = MinPs(m[3], m[1]);
        return (a < b) ? a : b;
    }
    static float MaxPs(float acc, float x) => (acc > x) ? acc : x;   // maxps dst,src: dst = (dst > src) ? dst : src
    static float MinPs(float acc, float x) => (acc < x) ? acc : x;
    static float MaxSs(float dst, float src) => (dst > src) ? dst : src;
    static float MinSs(float dst, float src) => (dst < src) ? dst : src;

    /// <summary>`FUN_180304720(rect, (w,h), uniform=1)`: shift1 = rect origin, scale1 = max(w/(x1−x0), h/(y1−y0)).</summary>
    public static ((float X, float Y) Shift1, (float X, float Y) Scale1) Pose(RectI r, int w, int h)
    {
        float sx = (float)w / (float)(r.X1 - r.X0), sy = (float)h / (float)(r.Y1 - r.Y0);
        float s = (sx > sy) ? sx : sy;   // maxss(sx, sy)
        return (((float)r.X0, (float)r.Y0), (s, s));
    }
}
