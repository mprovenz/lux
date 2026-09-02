using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The non-reference branch of `StereoISP::CreateStereoImage` (`180326320` L488–535, p13 = useRefCrop): the module's float
/// YUVA work image is colour-matched to a crop of the reference camera's YUVA image with the mean + Cholesky-covariance
/// linear transfer of the POD object built by `FUN_18044d690` (basis `FUN_18044d760` = identity in this path), statistics
/// `FUN_18044e150` (Welford over pixels with alpha &gt; 0.95, SSE lane order), solve `FUN_18044d7b0` (double: Σ/N + 0.001·I,
/// Cholesky with reciprocal off-diagonals, `A = L_ref·L_work⁻¹`, 4×4 chain `Minv·((T_ref·A)·T_work)·M` rounded to float once)
/// and apply `FUN_18044e4e0` (`o.yuv = p.w·(F·(y,u,v,1))`, `o.a = p.w`). Crop rect: `FUN_180301040/180301b10`.
/// </summary>
public static class StereoColorTransfer
{
    const float AlphaGate = 0.95f;   // DAT_1806b8900
    const double Tol = 0.001;        // 0x3f50624dd2f1a9fc

    /// <summary>`FUN_1800c2c10`: 3×3 double inverse, exact op list.</summary>
    public static double[] Inv3D(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double c00 = i * e - f * h, c10 = d * i - g * f, c20 = d * h - g * e;
        double det = ((c20 * c) + (c00 * a)) - (c10 * b);
        double inv = 1.0 / det;
        return new[] { c00 * inv, (c * h - b * i) * inv, (b * f - c * e) * inv, -(c10 * inv), (i * a - c * g) * inv, (c * d - f * a) * inv, c20 * inv, (g * b - h * a) * inv, (e * a - d * b) * inv };
    }

    /// <summary>`FUN_180301040` (xf = `(K_A·E_A)·(K_2·E_2)⁻¹`, column-major float) + `FUN_180301b10`: the reference rect mapped by
    /// the inverse of the upper-left 3×3 (double), four corners with `rcpps`+Newton, min/max, then C `(int)` truncation.</summary>
    public static RectI CropRect(CalibData calib2, CalibData calibA, RectI refRect)
    {
        var xf = Mat4D.FlowMatrix(calib2, calibA);
        var D = new double[9];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) D[3 * r + c] = xf[4 * c + r];
        var Di = Inv3D(D);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue; bool first = true;
        foreach (var (X, Y) in new[] { ((float)refRect.X0, (float)refRect.Y0), ((float)refRect.X0, (float)refRect.Y1), ((float)refRect.X1, (float)refRect.Y0), ((float)refRect.X1, (float)refRect.Y1) })
        {
            double w = Di[8] + (Di[7] * Y + Di[6] * X);
            float wf = (float)w;
            float r0 = Sse.Reciprocal(Vector128.Create(wf)).ToScalar();
            float r = ((1.0f - wf * r0) * r0 + r0) * 1.0f;
            float x = (float)(Di[2] + (Di[1] * Y + Di[0] * X)) * r;
            float y = (float)(Di[5] + (Di[4] * Y + Di[3] * X)) * r;
            if (first) { minX = maxX = x; minY = maxY = y; first = false; }
            else { if (x <= minX) minX = x; if (maxX <= x) maxX = x; if (y <= minY) minY = y; if (maxY <= y) maxY = y; }
        }
        return new RectI((int)minX, (int)minY, (int)maxX, (int)maxY);
    }

    public sealed class Stats { public Vector128<float> Mean, D, O; public float N; }

    /// <summary>`FUN_18044e150` for one image (identity basis): Welford in float over pixels with alpha &gt; 0.95.</summary>
    public static Stats Accumulate(float[] yuva, int w, int h, int stride, int offset, int step = 1)
    {
        var e3 = Vector128.Create(0f, 0f, 0f, 1f);
        var c0 = Vector128.Create(1f, 0f, 0f, 0f); var c1 = Vector128.Create(0f, 1f, 0f, 0f); var c2 = Vector128.Create(0f, 0f, 1f, 0f);
        Vector128<float> mean = default, D = default, O = default; float n = 0f;
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = (offset + y * stride + x) * 4;
                float px = yuva[i], py = yuva[i + 1], pz = yuva[i + 2], pw = yuva[i + 3];
                if (!(pw > AlphaGate)) continue;
                float n1 = n + 1.0f; var r = Vector128.Create(1.0f / n1);
                var d = ((Vector128.Create(pz) * c2 - mean) + Vector128.Create(pw) * e3) + (Vector128.Create(py) * c1 + Vector128.Create(px) * c0);
                var rd = r * d; var t = Vector128.Create(n) * rd;
                mean = rd + mean; D = t * d + D;
                var ds = Vector128.Create(d.GetElement(1), d.GetElement(2), d.GetElement(0), d.GetElement(3));   // shufps 0xC9
                O = ds * t + O; n = n1;
            }
        return new Stats { Mean = mean, D = D, O = O, N = n };
    }

    static double[] Chol(float[] C, out bool ok)
    {
        double U00 = C[0] + Tol, U01 = C[1], U02 = C[2], U11 = C[4] + Tol, U12 = C[5], U22 = C[8] + Tol; ok = true;
        double d0 = U00; if (!(d0 > 0)) { ok = false; } else { U00 = Math.Sqrt(d0); double q = 1.0 / U00; U01 *= q; U02 *= q;
            double d1 = U11 - U01 * U01; if (!(d1 > 0)) ok = false; else { U11 = Math.Sqrt(d1); U12 = U12 - U02 * U01; q = 1.0 / U11; U12 *= q;
                double d2 = U22 - (U02 * U02 + U12 * U12); if (!(d2 > 0)) ok = false; else U22 = Math.Sqrt(d2); } }
        return new[] { U00, 0.0, 0.0, U01, U11, 0.0, U02, U12, U22 };   // L = Uᵀ
    }

    static float[] Cov(Stats s)
    {
        float inv = 1.0f / s.N;
        var Dn = s.D * Vector128.Create(inv); var On = s.O * Vector128.Create(inv);
        return new[] { Dn.GetElement(0), On.GetElement(0), On.GetElement(2), On.GetElement(0), Dn.GetElement(1), On.GetElement(1), On.GetElement(2), On.GetElement(1), Dn.GetElement(2) };
    }

    /// <summary>`FUN_18044d7b0`: the row-major float 4×4 transfer (identity when either image has fewer than 100 gated pixels).</summary>
    public static float[] Solve(Stats work, Stats reference)
    {
        var F = new float[16]; F[0] = F[5] = F[10] = F[15] = 1f;
        if ((int)work.N < 100 || (int)reference.N < 100) return F;
        var Lr = Chol(Cov(reference), out _); var Lw = Chol(Cov(work), out _);
        var Li = Inv3D(Lw);
        var A4 = new double[16]; A4[15] = 1.0;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) A4[4 * i + j] = (Lr[3 * i] * Li[j] + Lr[3 * i + 1] * Li[3 + j]) + Lr[3 * i + 2] * Li[6 + j];
        double[] Tr = Ident(), Tw = Ident();
        for (int k = 0; k < 3; k++) { Tr[4 * k + 3] = reference.Mean.GetElement(k); Tw[4 * k + 3] = -(double)work.Mean.GetElement(k); }
        double[] M4 = Ident(), Minv4 = Ident(); Minv4[4] = -0.0;   // FUN_1800c2a00(I): −0.0 at (1,0), reaches only the alpha row
        var X = Mat4D.Mul(Mat4D.Mul(Minv4, Mat4D.Mul(Mat4D.Mul(Tr, A4), Tw)), M4);
        for (int k = 0; k < 16; k++) F[k] = (float)X[k];
        return F;
    }
    static double[] Ident() { var m = new double[16]; m[0] = m[5] = m[10] = m[15] = 1.0; return m; }

    /// <summary>`FUN_18044e4e0`: `o = p.w · ((1·c3 + z·c2) + (y·c1 + x·c0))`, `o.a = p.w` (columns of F).</summary>
    public static float[] Apply(float[] yuva, int w, int h, float[] F)
    {
        var c0 = Vector128.Create(F[0], F[4], F[8], F[12]); var c1 = Vector128.Create(F[1], F[5], F[9], F[13]);
        var c2 = Vector128.Create(F[2], F[6], F[10], F[14]); var c3 = Vector128.Create(F[3], F[7], F[11], F[15]);
        var outp = new float[yuva.Length];
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4; float x = yuva[i], y = yuva[i + 1], z = yuva[i + 2], a = yuva[i + 3];
            var acc = (Vector128.Create(1.0f) * c3 + Vector128.Create(z) * c2) + (Vector128.Create(y) * c1 + Vector128.Create(x) * c0);
            var o = Vector128.Create(a) * acc;
            outp[i] = o.GetElement(0); outp[i + 1] = o.GetElement(1); outp[i + 2] = o.GetElement(2); outp[i + 3] = a;
        }
        return outp;
    }

    /// <summary>The whole branch: crop the reference YUVA to `CropRect ∩ ref.rect`, accumulate both, solve, apply.</summary>
    public static (float[] Out, RectI Crop, float[] F, Stats Work, Stats Ref) Transfer(float[] work, int w, int h, float[] refYuv, int rw, int rh, CalibData calib2, CalibData calibA, Action<string>? log = null)
    {
        var rect = CropRect(calib2, calibA, new RectI(0, 0, rw, rh));
        int X0 = Math.Max(0, rect.X0), Y0 = Math.Max(0, rect.Y0), X1 = Math.Min(rw, rect.X1), Y1 = Math.Min(rh, rect.Y1);
        var sw = Accumulate(work, w, h, w, 0);
        var sr = (X1 - X0 <= 0 || Y1 - Y0 <= 0) ? new Stats() : Accumulate(refYuv, X1 - X0, Y1 - Y0, rw, Y0 * rw + X0);
        var F = Solve(sw, sr);
        log?.Invoke($"colour transfer: crop rect ({rect.X0},{rect.Y0},{rect.X1},{rect.Y1}) ∩ → ({X0},{Y0},{X1},{Y1}); N work {sw.N} ref {sr.N}; F [{string.Join(" ", F.Select(v => v.ToString("G9")))}]");
        log?.Invoke($"  work mean {sw.Mean} D {sw.D} O {sw.O}\n  ref  mean {sr.Mean} D {sr.D} O {sr.O}");
        return (Apply(work, w, h, F), new RectI(X0, Y0, X1, Y1), F, sw, sr);
    }

    /// <summary>`FUN_18010fc80`: exposure gain of the module relative to the reference camera —
    /// `((g_ref·(float)e_ref) / (g_mod·(float)e_mod)) · (rb_mod / rb_ref)` when both have a vignetting record and the module is colour.</summary>
    public static float ExposureGain(float gRef, ulong eRef, float? rbRef, float gMod, ulong eMod, float? rbMod, bool modColour)
    {
        float g = (gRef * (float)eRef) / (gMod * (float)eMod);
        if (rbMod is float vm && modColour && rbRef is float vr) g *= vm / vr;
        return g;
    }
}
