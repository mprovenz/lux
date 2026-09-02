using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Geometry;

/// <summary>3×3 float helpers with cp.dll's exact operation order (SoT A.8).</summary>
public static class Mat3F
{
    /// <summary>`FUN_1800c2a00`: cofactor inverse. det = (c2·m2 + c0·m0) − c1·m1; every element × (1/det); [3] negated by xor.</summary>
    public static float[] Inverse(ReadOnlySpan<float> m)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8];
        float c0 = m8 * m4 - m5 * m7;
        float c1 = m3 * m8 - m6 * m5;
        float c2 = m3 * m7 - m6 * m4;
        float inv = 1.0f / ((c2 * m2 + c0 * m0) - c1 * m1);
        return new[]
        {
            c0 * inv, (m2 * m7 - m1 * m8) * inv, (m1 * m5 - m2 * m4) * inv,
            -(c1 * inv), (m8 * m0 - m2 * m6) * inv, (m2 * m3 - m5 * m0) * inv,
            c2 * inv, (m6 * m1 - m7 * m0) * inv, (m4 * m0 - m3 * m1) * inv,
        };
    }

    /// <summary>Row-major product with the association `FUN_180185030` uses for both of its products:
    /// C[i][j] = (A[i][1]·B[1][j] + A[i][0]·B[0][j]) + A[i][2]·B[2][j].</summary>
    public static float[] Mul(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var c = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[3 * i + j] = (a[3 * i + 1] * b[3 + j] + a[3 * i] * b[j]) + a[3 * i + 2] * b[6 + j];
        return c;
    }

    /// <summary>`M = Ra·Rbᵀ` (CreateStereoImage `180326993–180326aeb`, disasm-verified): M[i][j] = (Ra[i][0]·Rb[j][0] + Ra[i][1]·Rb[j][1]) + Ra[i][2]·Rb[j][2].
    /// (The previous pairing from the decompile text differed at the ulp level for near-identity rotations — the A3 stereo image, 2026-08-27.)</summary>
    public static float[] MulABt(ReadOnlySpan<float> ra, ReadOnlySpan<float> rb)
    {
        var c = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[3 * i + j] = (ra[3 * i] * rb[3 * j] + ra[3 * i + 1] * rb[3 * j + 1]) + ra[3 * i + 2] * rb[3 * j + 2];
        return c;
    }
}

/// <summary>`SecondOrderRatPolyMapping` polynomial branch (`FUN_180011180` @0x180011322, set-up `FUN_180011070`/`FUN_180011430`):
/// Brown–Conrady forward map with the .lri `Distortion.Polynomial` (centre, normalisation, coeffs [k1,k2,p1,p2,k3]).
/// The normalisation is applied through the cofactor inverse of [nx 0 cx; 0 ny cy; 0 0 1] (rounding matters — the CRA LUT
/// amplifies it at small radii), exactly as cp.dll does.</summary>
public sealed class RatPolyMapping
{
    private readonly float _nx, _ny, _cx, _cy, _k1, _k2, _k3, _p1, _p2;
    private readonly float[] _n;

    public float CenterX => _cx;
    public float CenterY => _cy;

    public RatPolyMapping(float cx, float cy, float nx, float ny, ReadOnlySpan<float> coeffs)
    {
        if ((coeffs.Length & ~1) != 4) throw new ArgumentException("unsupported undistortion coefficients configuration!");
        _cx = cx; _cy = cy; _nx = nx; _ny = ny;
        _k1 = coeffs[0]; _k2 = coeffs[1]; _p1 = coeffs[2]; _p2 = coeffs[3];
        _k3 = coeffs.Length > 4 ? coeffs[4] : 0f;
        _n = Mat3F.Inverse(new[] { nx, 0f, cx, 0f, ny, cy, 0f, 0f, 1f });
    }

    public (float X, float Y) Map(float x, float y)
    {
        var n = _n;
        float xn = n[2] + (n[0] * x + n[1] * y);
        float yn = n[5] + (n[3] * x + n[4] * y);
        float den = (y * n[7] + x * n[6]) + n[8];
        float w = 1.0f / den;
        float u = w * xn, v = w * yn;
        float u2 = u * u, v2 = v * v;
        float r2 = v2 + u2;
        float uv2 = (v + v) * u;
        float s = (((_k3 * r2 + _k2) * r2 + _k1) * r2) + 1.0f;
        float tx = _p1 * uv2 + _p2 * (r2 + (u2 + u2));
        float ty = _p2 * uv2 + _p1 * (r2 + (v2 + v2));
        return (_cx + _nx * (tx + s * u), _cy + _ny * (ty + s * v));
    }
}

/// <summary>CRA radial LUT (`FUN_180184a30` + curve `FUN_1801855a0`): 4096 entries indexed by radius in pixels about the CRA
/// distortion centre; LUT[0] = 1, LUT[i] = 1 + δ(r)/r with δ = Y − X Catmull-Rom-interpolated over 30 uniform knots
/// X[i] = i·0.1 (mm), Y[i] = (poly.map(cx + i·0.1/pixCurve, cy).x − cx)·pixCurve, r = i·pixLut clamped to X[29].</summary>
internal static class CraLut
{
    public const int Size = 4096;
    private const float Step = 0.1f; // DAT_1806a30dc

    public static float[] Build(RatPolyMapping poly, float pixCurve, float pixLut)
    {
        const int n = 30;
        var x = new float[n]; var p = new float[n];
        float inv = 1.0f / pixCurve;
        for (int i = 0; i < n; i++)
        {
            float xi = ((float)i * Step) * inv + poly.CenterX;
            var (mx, _) = poly.Map(xi, poly.CenterY);
            float yi = (mx - poly.CenterX) * pixCurve;
            x[i] = (float)i * Step;
            p[i] = yi - x[i];
        }
        float x0 = x[0], xLast = x[n - 1];
        float step = (xLast - x0) / (float)(n - 1);
        var lut = new float[Size];
        lut[0] = 1.0f;
        for (int i = 1; i < Size; i++)
        {
            float r = (float)i * pixLut;
            if (xLast <= r) r = xLast;                      // minss
            float u = (r - x0) / step;
            int k = (int)u; if (k > n - 3) k = n - 3; if (k < 1) k = 1;
            float t = u - (float)k, t2 = t * t;
            float w = t * -0.5f + 1.0f;
            float a = ((t2 + t) * p[k + 1] + (1.0f - t2) * p[k]) * w;
            float b = (float)((double)((t2 + -1.0f) * t) * (1.0 / 6.0));
            float c = (float)((double)((t2 - t) * w) * (1.0 / 3.0));
            lut[i] = ((a + b * p[k + 2]) + c * p[k - 1]) / r + 1.0f;
        }
        return lut;
    }
}

/// <summary>Lumen's `lt::CalibData` (0xa8 bytes): intrinsics K (row-major), translation t, rotation R (X_cam = R·X + t),
/// view offset and crop (only the module's are used by the assembler: cx = crop·pp − viewOffset, sx = 1/(camScale·crop)).</summary>
public sealed record CameraCalib(float[] K, float[] T, float[] R, float ViewOffX, float ViewOffY, float CropX, float CropY)
{
    public static CameraCalib Identity(float[] k) => new(k, new float[3], new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, 0f, 0f, 1f, 1f);

    /// <summary>`FUN_1803081a0` scale path: K[0],K[2] × sx; K[4],K[5] × sy (identity when scale == (1,1)).</summary>
    public CameraCalib Scaled(float sx, float sy)
    {
        if (sx == 1.0f && sy == 1.0f) return this;
        if (sx <= 0f || sy <= 0f) throw new ArgumentException("Scale has to be a positive value.");
        var k = (float[])K.Clone();
        k[0] *= sx; k[2] *= sx; k[4] *= sy; k[5] *= sy;
        return this with { K = k, ViewOffX = ViewOffX * sx, ViewOffY = ViewOffY * sy, CropX = CropX * sx, CropY = CropY * sy };
    }
}

/// <summary>Per-module aligned calibration (the closure data of `ReferenceImageCache`'s per-level map functors):
/// `src = c + LUT[min((int)r,4095)]·d`, `d = H·[x,y,1] (projective) − c`, `r = √((sx·dx)² + (sy·dy)²)`.</summary>
public sealed class AlignedCalib
{
    public float Sx, Sy, Cx, Cy;
    public float[] H = new float[9];
    public float[] Lut = Array.Empty<float>();

    /// <summary>`FUN_180185030` fed as the cache builders do (`FUN_1804f05f0`/`FUN_1804f0e80`): H = (K_mod·inv(R_view·R_modᵀ))·inv(K_view·scale),
    /// c = crop_mod·pp − viewOffset_mod, s = 1/(camScale·crop_mod), LUT from the module's polynomial distortion.</summary>
    public static AlignedCalib Build(CameraCalib view, CameraCalib module, float viewScaleX, float viewScaleY,
                                     float camScaleX, float camScaleY, float ppX, float ppY,
                                     RatPolyMapping poly, float pixCurve, float pixLut)
    {
        var a = view.Scaled(viewScaleX, viewScaleY);
        var c = new AlignedCalib
        {
            Sx = 1.0f / (camScaleX * module.CropX),
            Sy = 1.0f / (camScaleY * module.CropY),
            Cx = module.CropX * ppX - module.ViewOffX,
            Cy = module.CropY * ppY - module.ViewOffY,
        };
        var m = Mat3F.MulABt(a.R, module.R);
        var mi = Mat3F.Inverse(m);
        var ki = Mat3F.Inverse(a.K);
        var t = Mat3F.Mul(module.K, mi);
        c.H = Mat3F.Mul(t, ki);
        c.Lut = CraLut.Build(poly, pixCurve, pixLut);
        return c;
    }

    /// <summary>The per-level map (`ReferenceImageCache::ReferenceImageCache::lambda_0..3`): level-L coordinates in, level-L out.</summary>
    public (float X, float Y) Map(float x, float y, int level = 0)
    {
        float scale = 1 << level;
        x *= scale; y *= scale;
        float den = (y * H[7] + x * H[6]) + H[8];
        float w = 1.0f / den;
        float xn = H[2] + (y * H[1] + x * H[0]);   // lambda_2 / ReferenceImageCache::lambda_0: products first, then the constant
        float yn = H[5] + (y * H[4] + x * H[3]);
        float dx = w * xn - Cx, dy = w * yn - Cy;
        float a = Sx * dx, b = Sy * dy;
        float r2 = b * b + a * a;
        float r;
        if (r2 == 0f) r = 0f;
        else
        {
            float rs = Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar() : 1.0f / MathF.Sqrt(r2);
            float s = r2 * rs;
            r = ((s * rs + -3.0f) * -0.5f) * s;
        }
        int idx = (int)r; if (idx > 0xfff) idx = 0xfff;
        float lu = Lut[idx];
        float inv = 1.0f / scale;
        return ((lu * dx + Cx) * inv, (Cy + dy * lu) * inv);
    }

    /// <summary>`PipelineCache::processLevel1` inlined map (kernel `1804e49f0`, spec ab6d047c §3): the level-0 map with the final coordinate
    /// associated as `((cx + −1) + lu·dx)` / `((cy + −1) + dy·lu)` (the reference-cache kernel does `((lu·dx + cx) + −1)`); the caller
    /// subtracts the source offset.</summary>
    public (float X, float Y) MapInlinedMinus1(float x, float y)
    {
        float den = (y * H[7] + x * H[6]) + H[8];
        float w = 1.0f / den;
        float xn = H[2] + (y * H[1] + x * H[0]);
        float yn = H[5] + (y * H[4] + x * H[3]);
        float dx = w * xn - Cx, dy = w * yn - Cy;
        float a = Sx * dx, b = Sy * dy;
        float r2 = b * b + a * a;
        float r;
        if (r2 == 0f) r = 0f;
        else
        {
            float rs = Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar() : 1.0f / MathF.Sqrt(r2);
            float s = r2 * rs;
            r = ((s * rs + -3.0f) * -0.5f) * s;
        }
        int idx = (int)r; if (idx > 0xfff) idx = 0xfff;
        float lu = Lut[idx];
        return ((Cx + -1.0f) + lu * dx, (Cy + -1.0f) + dy * lu);
    }
}

/// <summary>
/// `GetAlignedCalibration(out pair, refView, camView, img, halfSize, scale)` (`0x180302b00`, spec
/// `a5db2cbe8ffbc9e6d.md`): the reference-like canvas of a higher-group camera. `base` = camView with
/// K,R := refView and t = (R_ref·R_camᵀ)·t_cam; `scaled = Scale(base, scale)`; the per-module warp closure is
/// `AlignedCalib.Build(view = base, module = camView, …)`. A seed box is projected from the `[0,hx]×[0,hy]` corners through
/// `W = (K_scaled·R_scaled)·inv(K_cam·R_cam)`, widened by ±200, and scanned on a step-2 lattice for the first/last x and y whose
/// mapped point lands inside `[0,hx)×[0,hy)`. Output: `Shift(scaled, minx, miny)` and the size `(maxx − minx, maxy − miny)`.
/// </summary>
public static class AlignedCalibrationScan
{
    public sealed record Result(CameraCalib First, int W, int H, int MinX, int MinY, int MaxX, int MaxY, int SeedX0, int SeedY0, int SeedX1, int SeedY1);

    /// <summary>`FUN_1803041d0(camView, refView)`: camView's off/crop copied, K,R from refView, t = M·t_cam with M = R_ref·R_camᵀ.</summary>
    public static CameraCalib BaseCalib(CameraCalib refView, CameraCalib camView)
    {
        var M = Mat3F.MulABt(refView.R, camView.R); var tc = camView.T; var t = new float[3];
        for (int i = 0; i < 3; i++) t[i] = (M[3 * i] * tc[0] + M[3 * i + 1] * tc[1]) + M[3 * i + 2] * tc[2];
        return camView with { K = (float[])refView.K.Clone(), R = (float[])refView.R.Clone(), T = t };
    }

    static float Rcp(float d)
    {
        float r0 = Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar() : 1.0f / d;
        return ((1.0f - d * r0) * r0) + r0;   // rcpps + one Newton step (P2/P3 corners only)
    }

    public static Result Compute(CameraCalib refView, CameraCalib camView, (int X, int Y) halfSize, (float X, float Y) scale,
                                 float camScaleX, float camScaleY, float ppX, float ppY, RatPolyMapping poly, float pixCurve, float pixLut)
    {
        var baseC = BaseCalib(refView, camView);
        var scaled = baseC.Scaled(scale.X, scale.Y);
        var closure = AlignedCalib.Build(baseC, camView, scale.X, scale.Y, camScaleX, camScaleY, ppX, ppY, poly, pixCurve, pixLut);
        // seed box (§3)
        var P = Mat3F.Mul(camView.K, camView.R); var Pinv = Mat3F.Inverse(P);
        var Q = Mat3F.Mul(scaled.K, scaled.R); var W = Mat3F.Mul(Q, Pinv);
        int hx = halfSize.X, hy = halfSize.Y; float fx = (float)hx, fy = (float)hy;
        float inv0 = 1.0f / W[8]; int x0 = (int)(W[2] * inv0), y0 = (int)(inv0 * W[5]);
        float nx1 = fy * W[1] + W[2], ny1 = fy * W[4] + W[5], d1 = fy * W[7] + W[8]; float inv1 = 1.0f / d1;
        int x1 = (int)(nx1 * inv1), y1 = (int)(inv1 * ny1);
        float nx2 = fx * W[0] + W[2], ny2 = fx * W[3] + W[5], d2 = fx * W[6] + W[8];
        float nx3 = W[2] + (fx * W[0] + fy * W[1]), ny3 = W[5] + (fx * W[3] + fy * W[4]), d3 = W[8] + (fx * W[6] + fy * W[7]);
        float inv2 = Rcp(d2), inv3 = Rcp(d3);
        int x2 = (int)(nx2 * inv2), y2 = (int)(inv2 * ny2), x3 = (int)(nx3 * inv3), y3 = (int)(ny3 * inv3);
        int minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        int minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)), maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        int minx0 = minX - 200, miny0 = minY - 200, xEnd = maxX + 200, yEnd = maxY + 200;
        bool Inside(int x, int y)
        {
            var (px, py) = closure.Map((float)x, (float)y, 0);
            int ix = (int)px, iy = (int)py;
            return ix >= 0 && ix < hx && iy >= 0 && iy < hy;
        }
        int minx = 0, miny = 0, maxx = 0, maxy = 0;
        if (minx0 < xEnd) { bool f = false; for (int x = minx0; x < xEnd && !f; x += 2) for (int y = miny0; y < yEnd; y += 2) if (Inside(x, y)) { minx = x; f = true; break; } if (!f) minx = 0; }
        if (miny0 < yEnd) { bool f = false; for (int y = miny0; y < yEnd && !f; y += 2) for (int x = minx0; x < xEnd; x += 2) if (Inside(x, y)) { miny = y; f = true; break; } }
        if (minx0 < xEnd) { bool f = false; for (int x = maxX + 199; x >= minx0 && !f; x -= 2) for (int y = miny0; y < yEnd; y += 2) if (Inside(x, y)) { maxx = x; f = true; break; } if (!f) maxx = 0; }
        if (miny0 < yEnd) { bool f = false; for (int y = maxY + 199; y >= miny0 && !f; y -= 2) for (int x = minx0; x < xEnd; x += 2) if (Inside(x, y)) { maxy = y; f = true; break; } if (!f) maxy = 0; }
        // output (§5): Shift(scaled, minx, miny)
        var k = (float[])scaled.K.Clone(); float dx = (float)minx, dy = (float)miny; k[2] -= dx; k[5] -= dy;
        var first = scaled with { K = k, ViewOffX = scaled.ViewOffX + dx, ViewOffY = scaled.ViewOffY + dy };
        return new Result(first, maxx - minx, maxy - miny, minx, miny, maxx, maxy, minx0, miny0, xEnd, yEnd);
    }
}
