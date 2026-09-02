using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Lri;
using static Lux.Engine.Pipeline.Color.LumenColorTables;

namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// Lumen's white-balance state machinery (SoT §9.2, DNG-SDK ports in float as cp.dll does them):
/// xy ⇄ (CCT, tint) on the Robertson table, ColorMatrix interpolation in 1/T, the neutral→xy iteration
/// (`setWhiteBalance::lambda_20`, `18041ebe0`) that the data stream runs on the AsShot neutral, and the
/// (CCT, tint)→neutral step (`FUN_18041f4d0`/`18041eea0`) the module ISPs use under `auto_white_balance = manual_temp`.
/// </summary>
public static class WhiteBalance
{
    public const float TintScale = -3000f;               // DAT_180687544 (1/TintScale = DAT_180687520)
    public const float ConvergenceEps = 9.999999974752427e-07f;   // DAT_18069f08c (also the singular-determinant floor)
    public const int MaxPasses = 30;                     // uVar9: −1 … 0x1c → 30 passes, average on the last

    /// <summary>xy → (CCT, tint), float version of <see cref="LumenColorTables.XyToCct"/> (`FUN_1800d0ef0`).</summary>
    // SSE forms used by cp.dll's colour code (transcribed from the assembly): rsqrtss/rcpps + one Newton step.
    private static float RsqrtSs(float x) => System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.ReciprocalSqrtScalar(System.Runtime.Intrinsics.Vector128.CreateScalar(x)).ToScalar() : 1f / MathF.Sqrt(x);
    private static float RcpSs(float x) => System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.ReciprocalScalar(System.Runtime.Intrinsics.Vector128.CreateScalar(x)).ToScalar() : 1f / x;
    /// <summary>1/√x as `rs·(−0.5)·((x·rs)·rs + (−3))` (rsqrtss + Newton), 0 when x == 0.</summary>
    private static float InvSqrtNR(float x) { if (x == 0f) return 0f; float rs = RsqrtSs(x); float s = x * rs; return (s * rs + -3.0f) * -0.5f * rs; }
    /// <summary>1/√x in the variant `FUN_1800d0cb0` uses: `((x·rs)·rs + (−3))·(rs·(−0.5))` — same value, kept separate for op order.</summary>
    private static float InvSqrtNR2(float x) { float rs = RsqrtSs(x); float s = x * rs; return ((s * rs) + -3.0f) * (rs * -0.5f); }
    /// <summary>√x as `(s·rs + (−3))·(s·(−0.5))` with s = x·rs (`FUN_1800d0ef0`), 0 when x == 0.</summary>
    private static float SqrtNR(float x) { if (x == 0f) return 0f; float rs = RsqrtSs(x); float s = x * rs; return (s * rs + -3.0f) * (s * -0.5f); }
    /// <summary>1/x as `(1 − x·r)·r + r` (rcpps + Newton).</summary>
    private static float RcpNR(float x) { float r = RcpSs(x); return (1.0f - x * r) * r + r; }

    public static (float Cct, float Tint) XyToCctF(float x, float y)
    {
        // FUN_1800d0ef0 (asm-exact): uv = (2x, 3y) · rcpNR((1.5 − x) + 6·y)
        float den = y * 6f + (1.5f - x);
        float inv = RcpNR(den);
        float u = 2f * x * inv, v = 3f * y * inv;
        var T = Robertson;
        float lastDt = 0f, lastDu = 0f, lastDv = 0f;
        for (int i = 0; i < 30; i++)
        {
            float t = T[(i + 1) * 4 + 3];
            float len = SqrtNR(t * t + 1f);
            float il = RcpNR(len);
            float dv = t * il, du = 1f * il;
            float uu = u - T[(i + 1) * 4 + 1], vv = v - T[(i + 1) * 4 + 2];
            float dt = vv * du - uu * dv;
            if (i == 29 || dt <= 0f)
            {
                float f = 0f;
                if (i != 0) { float m = MathF.Min(0f, dt); f = m / (m - lastDt); }
                float pu = T[i * 4 + 1], pv = T[i * 4 + 2], cu = T[(i + 1) * 4 + 1], cv = T[(i + 1) * 4 + 2];
                float duOff = (cu - pu) * f + uu;
                float dvOff = vv + (cv - pv) * f;
                float du3 = (lastDu - du) * f + du;
                float dv3 = (lastDv - dv) * f + dv;
                float l3 = dv3 * dv3 + du3 * du3;
                float rs = RsqrtSs(l3);
                float inv3 = (rs * -0.5f) * (l3 * rs * rs + -3.0f);
                float tint = ((dv3 * dvOff) * inv3 + (du3 * duOff) * inv3) * TintScale;
                float mired = (T[i * 4] - T[(i + 1) * 4]) * f + T[(i + 1) * 4];
                return (1.0e6f / mired, tint);
            }
            lastDt = dt; lastDu = du; lastDv = dv;
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>(CCT, tint) → xy: DNG-SDK `dng_temperature::Get_xy_coord` (`FUN_1800d0cb0`; constants 1.5 `DAT_180687524`,
    /// −4 `DAT_180682410`, 2 `DAT_180682414`, 1e6 `DAT_18067eca0`).</summary>
    public static (float X, float Y) CctTintToXy(float cct, float tint)
    {
        // FUN_1800d0cb0 (asm-exact)
        float r = 1.0e6f / cct;
        float tt = tint * (-0.00033333332976326346f);   // DAT_180687520 = 1/TintScale
        var T = Robertson;
        int i = 1;
        while (i < 30 && !(r < T[i * 4])) i++;
        float f = (T[i * 4] - r) / (T[i * 4] - T[(i - 1) * 4]);
        float t1 = T[(i - 1) * 4 + 3], uu1 = InvSqrtNR2(t1 * t1 + 1f); float vv1 = t1 * uu1;
        float t2 = T[i * 4 + 3], uu2 = InvSqrtNR2(t2 * t2 + 1f); float vv2 = t2 * uu2;
        float uu3 = (uu1 - uu2) * f + uu2, vv3 = (vv1 - vv2) * f + vv2;
        float l3 = vv3 * vv3 + uu3 * uu3;
        float inv3 = InvSqrtNR2(l3);
        uu3 *= inv3; vv3 = inv3 * vv3;
        float u = (T[(i - 1) * 4 + 1] - T[i * 4 + 1]) * f + T[i * 4 + 1];
        float v = (T[(i - 1) * 4 + 2] - T[i * 4 + 2]) * f + T[i * 4 + 2];
        u = uu3 * tt + u; v = vv3 * tt + v;
        float xn = 1.5f * u;
        float den = (v * -4f + u) + 2f;
        float inv = RcpNR(den);
        return (xn * inv, v * inv);
    }

    /// <summary>ColorMatrix at temperature T (`FUN_1800d13d0`): the entry with the smaller 1/T is the base, the other
    /// the target; w = (clamp(1/T) − 1/T_lo)/(1/T_hi − 1/T_lo); M = base + (target − base)·w. Row-major float[9].</summary>
    public static float[] MatrixAtTemperature(float t, float t1, float t2, float[] m1, float[] m2)
    {
        // FUN_1800d13d0 (asm-exact): 1/T by divss, 1/t1 and 1/t2 by rcpps + Newton
        float invT = 1f / t, inv1 = RcpNR(t1), inv2 = RcpNR(t2);
        float[] baseM, other; float lo, hi;
        if (inv1 <= inv2) { baseM = m1; other = m2; lo = inv1; hi = inv2; }
        else { baseM = m2; other = m1; lo = inv2; hi = inv1; }
        float c = MathF.Max(invT, lo); c = MathF.Min(c, hi);
        float w = (c - lo) / (hi - lo);
        var r = new float[9];
        for (int i = 0; i < 9; i++) r[i] = baseM[i] + (other[i] - baseM[i]) * w;
        return r;
    }

    /// <summary>`FUN_1800c3770` (asm-exact order).</summary>
    public static float Det3(float[] m) => ((m[8] * m[4] - m[5] * m[7]) * m[0] + m[2] * (m[3] * m[7] - m[6] * m[4])) - m[1] * (m[3] * m[8] - m[6] * m[5]);

    /// <summary>3×3 inverse in float (`FUN_1800c2a00`, "singular matrix found!").</summary>
    public static float[] Inverse3(float[] m)
    {
        float det = Det3(m);
        if (det == 0f) throw new InvalidOperationException("singular matrix found!");
        float id = 1f / det;
        return new[]
        {
            (m[4] * m[8] - m[5] * m[7]) * id, (m[2] * m[7] - m[1] * m[8]) * id, (m[1] * m[5] - m[2] * m[4]) * id,
            (m[5] * m[6] - m[3] * m[8]) * id, (m[0] * m[8] - m[2] * m[6]) * id, (m[2] * m[3] - m[0] * m[5]) * id,
            (m[3] * m[7] - m[4] * m[6]) * id, (m[1] * m[6] - m[0] * m[7]) * id, (m[0] * m[4] - m[1] * m[3]) * id,
        };
    }

    /// <summary>
    /// Neutral → xy (`setWhiteBalance::lambda_20`, DNG-SDK `NeutralToXY`): start at the D50 white, iterate
    /// xy → T → CM(T)⁻¹·neutral → xy until |Δx|+|Δy| &lt; 1e-6, at most 30 passes, averaging the last pair on the
    /// 30th (two-value oscillation guard, `_DAT_1806830c0 = 0.5`).
    /// </summary>
    public static (float X, float Y) NeutralToXy(float[] neutral, LumenProfile p)
    {
        // setWhiteBalance::lambda_20 (18041ebe0, asm-exact): start at the colour-space-5 white (D50), ≤ 30 passes
        float x = IlluminantX[IllumD50], y = IlluminantY[IllumD50];
        float t1 = IlluminantCctF(p.Low.InternalIlluminant), t2 = IlluminantCctF(p.High.InternalIlluminant);
        for (int pass = -1; ; pass++)
        {
            var (t, _) = XyToCctF(x, y);
            var m = MatrixAtTemperature(t, t1, t2, p.Low.ColorMatrix, p.High.ColorMatrix);
            if (Det3(m) <= ConvergenceEps) throw new InvalidOperationException("singular matrix found!");
            var inv = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(m);   // FUN_1800c2a00
            float n0 = neutral[0], n1 = neutral[1], n2 = neutral[2];
            float X = inv[2] * n2 + (inv[1] * n1 + inv[0] * n0);
            float Y = inv[5] * n2 + (inv[4] * n1 + inv[3] * n0);
            float z0 = n0 * inv[6], z1 = n1 * inv[7], z2 = n2 * inv[8];
            float sum = (Y + z1) + ((X + z0) + z2);
            float s = 1f / sum;
            float nx = X * s, ny = Y * s;
            float d = MathF.Abs(nx - x) + MathF.Abs(ny - y);
            if (d < ConvergenceEps) return (nx, ny);
            if (pass == 28) return ((nx + x) * 0.5f, (ny + y) * 0.5f);
            x = nx; y = ny;
        }
    }

    /// <summary>xy → G-normalised neutral (`FUN_18041f4d0`): T = CCT(xy), M = CM(T), n = M·XYZ(xy), n / n.G.</summary>
    /// <summary>Profile illuminant temperature as Lumen computes it (`FUN_1800d1190`): the illuminant's xy (`FUN_1800ce600`)
    /// renormalised through XYZ in float — X = x·(1/y), Z = ((1−y)−x)·(1/y), s = 1/((X + 1) + Z), (x', y') = (X·s, s) — then `XyToCctF`.</summary>
    public static float IlluminantCctF(int illum)
    {
        float x = IlluminantX[illum], y = IlluminantY[illum];
        float invY = 1f / y;
        float X = x * invY;
        float s = 1f / ((X + 1f) + ((1f - y) - x) * invY);
        return XyToCctF(X * s, 1f * s).Cct;
    }

    public static float[] NeutralFromXy(float x, float y, LumenProfile p)
    {
        // FUN_18041f4d0 (asm-exact)
        var (t, _) = XyToCctF(x, y);
        var m = MatrixAtTemperature(t, IlluminantCctF(p.Low.InternalIlluminant), IlluminantCctF(p.High.InternalIlluminant), p.Low.ColorMatrix, p.High.ColorMatrix);
        float invY = 1f / y;
        float Z = ((1f - y) - x) * invY, X = x * invY;
        float n0 = m[2] * Z + (m[0] * X + m[1]);
        float den = m[5] * Z + (m[3] * X + m[4]);
        float n2 = Z * m[8] + (X * m[6] + m[7]);
        float g = 1f / den;
        return new[] { n0 * g, 1f, g * n2 };
    }

    /// <summary>(CCT, tint) → neutral via xy (`setWhiteBalance::lambda_21` → `FUN_18041f4d0`).</summary>
    public static float[] NeutralFromTempTint(float cct, float tint, LumenProfile p)
    {
        var (x, y) = CctTintToXy(cct, tint);
        return NeutralFromXy(x, y, p);
    }

    /// <summary>The capture's white-balance state as Lumen derives it at load (`FUN_1802095e0` L330–371 →
    /// `1804d3150` L192–193): AsShot neutral → xy (stream +0x40) → (CCT, tint) → the module-ISP neutral.</summary>
    public sealed record CaptureWb(float[] AsShotNeutral, float X, float Y, float Cct, float Tint, float[] IspNeutral)
    {
        public static CaptureWb From(LriFile lri, LumenProfile profile)
        {
            var n = lri.LumenNeutral;
            var (x, y) = NeutralToXy(n, profile);
            var (cct, tint) = XyToCctF(x, y);
            return new CaptureWb(n, x, y, cct, tint, NeutralFromTempTint(cct, tint, profile));
        }
    }
}
