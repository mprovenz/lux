using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static Lux.Engine.Pipeline.Color.LumenColorTables;

namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// The float XYZ ⇄ CIE-Lab kernels of `lt::ColorSpace` conversion (`FUN_1800cfb70` → `FUN_1800cf800` → per-(to,from) kernel table
/// `FUN_1800cf5e0`: XYZ(7)→Lab(8) = `FUN_1800d2db0`, Lab(8)→XYZ(7) = `FUN_1800dc160`), as used by `FUN_18014b500` L105 (chart XYZ → Lab)
/// and `OptimizeHSVLut` L203/L221 (chart Lab → XYZ, the LSQ targets and the white-patch reference). Both spaces sit at the same white
/// (D50, `FUN_1800cef60(5)`), their matrices are the identity (`ColorSpace.Standard(7|8)`, native illuminant 0 → no adaptation) and the
/// kernel adaptation `FUN_1800ce6f0` is the identity, so the 4×4 pre-matrix of each kernel reduces exactly to the white-point diagonal:
/// XYZ→Lab multiplies by `FUN_1800c2a00(diag(Xw, 1, Zw))`, Lab→XYZ by `diag(Xw, 1, Zw)` (the other terms are products with exact 0/1).
/// Spec: a-fm-refit.md §3.
/// </summary>
public static class LumenLabKernels
{
    /// <summary>`Xw = x·(1/y)`, `Zw = ((1 − y) − x)·(1/y)` in float (both kernels, `DAT_180681c78 = 1.0`).</summary>
    public static (float Xw, float Zw) White(int illum)
    {
        float x = IlluminantX[illum], y = IlluminantY[illum];
        float invY = 1.0f / y;
        return (x * invY, ((1.0f - y) - x) * invY);
    }

    static float Rcp(float v) => Sse.Reciprocal(Vector128.Create(v)).ToScalar();   // rcpps lane

    /// <summary>`FUN_1800d2db0` per-lane cube root: bit-hack seed (`i/3 + 0x2a5137a0`) and two Newton steps with `rcpps` reciprocals
    /// (`DAT_180687580..5a0`: 709965728, 1/3, 2/3; `DAT_1806824a0 = 1.0`).</summary>
    static float CbrtApprox(float t)
    {
        int i = BitConverter.SingleToInt32Bits(t);
        int g = (i >> 2) + (i >> 4);
        g = (g >> 4) + g;
        float y0 = BitConverter.Int32BitsToSingle((g >> 8) + g + 709965728);
        float r0 = Rcp(y0 * y0);
        float s = (y0 + y0) + r0 * t;
        float y1 = s * 0.3333333432674408f;
        float y1sq = y1 * y1;
        float r1 = Rcp(y1sq);
        return ((s * 0.6666666865348816f) + (((1.0f - y1sq * r1) * r1 + r1) * t)) * 0.3333333432674408f;
    }

    /// <summary>XYZ → Lab (`FUN_1800d2db0`, scalar tail loop 1800d3b7c–): `t = max(0, XYZ·T)` with `T = FUN_1800c2a00(diag(Xw,1,Zw))`,
    /// `f = t &lt; 0.008856452 ? t·7.787037 + 0.13793103 : cbrt(t)` (`DAT_1806875b0/c0/d0`), then
    /// `L = ((fy·116) + (−16)) + (fx·0)`, `a = ((fy·(−500)) + 0) + (fx·500)`, `b = ((fy·200) + 0) + (fz·(−200))` (`DAT_1806875e0/f0/600`).</summary>
    public static void XyzToLab(ReadOnlySpan<float> xyz, Span<float> lab, int illum)
    {
        var (Xw, Zw) = White(illum);
        var T = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(new[] { Xw, 0f, 0f, 0f, 1f, 0f, 0f, 0f, Zw });
        Span<float> f = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            float t = xyz[i] * T[i * 4];
            t = MathF.Max(0f, t);
            float lin = t * 7.787037372589111f + 0.13793103396892548f;
            f[i] = t < 0.008856452070176601f ? lin : CbrtApprox(t);
        }
        lab[0] = ((f[1] * 116f) + (-16f)) + (f[0] * 0f);
        lab[1] = ((f[1] * -500f) + 0f) + (f[0] * 500f);
        lab[2] = ((f[1] * 200f) + 0f) + (f[2] * -200f);
    }

    /// <summary>Lab → XYZ (`FUN_1800dc160`): `fx = (a·0.002) + ((L + 16)·(1/116))`, `fy = (a·0) + ((L+16)·(1/116))`, `fz = (b·(−0.005)) + …`
    /// (`DAT_180687650..67c`), `f' = f &lt; 0.20689656 ? f·0.12841855 + (−0.017712904) : (f·f)·f` (`DAT_180687680..6ac`), then
    /// `X = fx'·Xw, Y = fy'·1, Z = fz'·Zw` (the remaining terms of the 4-lane product are exact zeros).</summary>
    public static void LabToXyz(ReadOnlySpan<float> lab, Span<float> xyz, int illum)
    {
        var (Xw, Zw) = White(illum);
        float L = lab[0], a = lab[1], b = lab[2];
        float lp = (L + 16f) * 0.008620689623057842f;
        Span<float> f = stackalloc float[3];
        f[0] = (a * 0.0020000000949949026f) + lp;
        f[1] = (a * 0f) + lp;
        f[2] = (b * -0.004999999888241291f) + lp;
        Span<float> fp = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            float cube = (f[i] * f[i]) * f[i];
            float lin = f[i] * 0.12841854989528656f + (-0.017712904140353203f);
            fp[i] = f[i] < 0.2068965584039688f ? lin : cube;
        }
        xyz[0] = fp[0] * Xw; xyz[1] = fp[1] * 1f; xyz[2] = fp[2] * Zw;
    }

    /// <summary>The combined 3×3 of a `lt::ColorSpace` conversion whose adaptation is the identity (`FUN_1800ce6f0` returns the identity when
    /// both whites are equal — always the case here, D50 → D50): `C = FUN_1800c2a00(dstPrimaries) · srcPrimaries`, where every product with an
    /// exact 0/1 of the identity is exact. For src = XYZ (type 7, identity primaries) `C = inv(P)`; for src = Lab (type 8) the kernel's own
    /// `diag(Xw, 1, Zw)` is the right factor, so `C(i,j) = inv(P)(i,j)·d_j` (`FUN_1800dc160` L107–160, `FUN_1800d1720` L92–130).</summary>
    public static float[] XyzToRgbMatrix(float[] primaries) => Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(primaries);

    /// <summary>XYZ(7) → RGB kernel `FUN_1800d1720`, pixel loop `1800d1ce0–1800d1d12`: with the translation column
    /// `DAT_180682470 = (0,0,0,1)` and alpha 1 the three lanes are `out_j = ((α·0 + (Z·C(j,2) + X·C(j,0))) + Y·C(j,1))`.</summary>
    public static void XyzToRgb(float[] C, float x, float y, float z, Span<float> rgb)
    {
        for (int j = 0; j < 3; j++) rgb[j] = (0f + (z * C[j * 3 + 2] + x * C[j * 3])) + y * C[j * 3 + 1];
    }

    /// <summary>Lab(8) → RGB kernel `FUN_1800dc160`, pixel loop `1800dc780–1800dc7ea`: the `f` values of <see cref="LabToXyz"/>, then
    /// `out_j = (α·0 + fz·C(j,2)) + (fy·C(j,1) + fx·C(j,0))` with `C(i,j) = inv(dstPrimaries)(i,j)·diag(Xw,1,Zw)_j`.</summary>
    public static void LabToRgb(float[] C, float L, float a, float b, Span<float> rgb)
    {
        float lp = (L + 16f) * 0.008620689623057842f;
        Span<float> f = stackalloc float[3];
        f[0] = (a * 0.0020000000949949026f) + lp;
        f[1] = (a * 0f) + lp;
        f[2] = (b * -0.004999999888241291f) + lp;
        Span<float> fp = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            float cube = (f[i] * f[i]) * f[i];
            float lin = f[i] * 0.12841854989528656f + (-0.017712904140353203f);
            fp[i] = f[i] < 0.2068965584039688f ? lin : cube;
        }
        for (int j = 0; j < 3; j++) rgb[j] = (0f + fp[2] * C[j * 3 + 2]) + (fp[1] * C[j * 3 + 1] + fp[0] * C[j * 3]);
    }

    /// <summary>`C` of the Lab → RGB kernel: `inv(primaries)` with its columns scaled by the src white `(Xw, 1, Zw)` (the kernel's
    /// second matrix product `B·diag`, whose off-diagonal terms are exact zeros).</summary>
    public static float[] LabToRgbMatrix(float[] primaries, int illum)
    {
        var inv = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(primaries);
        var (Xw, Zw) = White(illum);
        var c = new float[9];
        for (int i = 0; i < 3; i++) { c[i * 3] = inv[i * 3] * Xw; c[i * 3 + 1] = inv[i * 3 + 1] * 1f; c[i * 3 + 2] = inv[i * 3 + 2] * Zw; }
        return c;
    }

    /// <summary>The 24-patch reference chart (`DAT_1808320e0`) as Lumen sees it at fit time: Lab (float, `FUN_18014b500` L105) and the
    /// round-tripped XYZ (`OptimizeHSVLut` L203/L221) — both at D50.</summary>
    public static (float[] Lab, float[] XyzRoundTrip) ReferenceChart(int illum = IllumD50)
    {
        var lab = new float[24 * 3]; var xyz = new float[24 * 3];
        for (int i = 0; i < 24; i++)
        {
            XyzToLab(ReferenceChartXyz.AsSpan(i * 3, 3), lab.AsSpan(i * 3, 3), illum);
            LabToXyz(lab.AsSpan(i * 3, 3), xyz.AsSpan(i * 3, 3), illum);
        }
        return (lab, xyz);
    }
}
