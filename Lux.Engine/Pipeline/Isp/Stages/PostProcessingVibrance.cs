using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Imaging;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The two vibrance kernels of Lumen's `lt::Internal::PostProcessing` (the tile kernel picks by the vibrance
/// v = `tone_mapping.vibrance`: v in [0x3f7fffef, 0x3f800008] → the no-vibrance kernel, above → <see cref="Up"/>,
/// below → <see cref="Down"/>). Both share the no-vibrance kernel's line factories (Lab line, LDiff, sharpen/blur) and its
/// Lab → RGB tail; they differ only in how the a/b chroma (and, for v &gt; 1, the lightness) of a pixel is rescaled before the
/// reconstruction, using its hue h = atan2(b, a) (a polynomial atan, `rcpps` without a Newton step) and the 361-entry per-degree table
/// that Lumen's static initializer copies from its constant data.
///
/// <para>Each kernel has a 4-wide body (columns `0 … (w &amp; ~3) − 1` of a 256-px Tiler column tile) and a scalar tail (the last
/// `w &amp; 3` columns) that compute the same formula with different instructions: the tail evaluates the atan polynomial in double,
/// truncates the degree index before the range fold (the body rounds to nearest after it), takes √ via `rsqrtss` + Newton, divides
/// exactly where the body multiplies by a Newton-refined `rcpps`, associates several products differently, and clamps the output
/// at 0. All of it follows Lumen's instruction order, operation by operation.</para>
/// </summary>
public static class PostProcessingVibrance
{
    static float B(uint u) => BitConverter.UInt32BitsToSingle(u);

    /// <summary>The per-degree table (361 floats, hue −179…180 folded to 0…360).</summary>
    public static readonly float[] HueTable = new uint[]
    {
        0x42a98000, 0x42a98000, 0x42a90000, 0x42a88000, 0x42a88000, 0x42a88000, 0x42a80000, 0x42a88000, 0x42a88000, 0x42a88000,
        0x42a88000, 0x42a90000, 0x42a98000, 0x42aa0000, 0x42aa0000, 0x42aa8000, 0x42ab0000, 0x42ab8000, 0x42ac8000, 0x42ad0000,
        0x42ad8000, 0x42ae8000, 0x42b00000, 0x42b10000, 0x42b20000, 0x42b30000, 0x42b40000, 0x42b58000, 0x42b70000, 0x42b88000,
        0x42ba8000, 0x42bc0000, 0x42bd8000, 0x42bf8000, 0x42c18000, 0x42c40000, 0x42c60000, 0x42c88000, 0x42cb0000, 0x42ce0000,
        0x42d10000, 0x42cd8000, 0x42ca0000, 0x42c70000, 0x42c48000, 0x42c20000, 0x42bf8000, 0x42bd8000, 0x42bb8000, 0x42b98000,
        0x42b80000, 0x42b58000, 0x42b48000, 0x42b28000, 0x42b10000, 0x42b00000, 0x42ae8000, 0x42ad8000, 0x42ac8000, 0x42ab8000,
        0x42aa8000, 0x42a98000, 0x42a90000, 0x42a80000, 0x42a78000, 0x42a70000, 0x42a68000, 0x42a60000, 0x42a58000, 0x42a50000,
        0x42a50000, 0x42a48000, 0x42a50000, 0x42a48000, 0x42a48000, 0x42a48000, 0x42a48000, 0x42a50000, 0x42a50000, 0x42a58000,
        0x42a58000, 0x42a60000, 0x42a68000, 0x42a70000, 0x42a78000, 0x42a80000, 0x42a90000, 0x42aa0000, 0x42aa8000, 0x42ab8000,
        0x42ac8000, 0x42ad8000, 0x42ae8000, 0x42b00000, 0x42b18000, 0x42b28000, 0x42b40000, 0x42b60000, 0x42b78000, 0x42b98000,
        0x42bb8000, 0x42bd8000, 0x42bf8000, 0x42c18000, 0x42c18000, 0x42c18000, 0x42c18000, 0x42c20000, 0x42c28000, 0x42c30000,
        0x42c38000, 0x42c38000, 0x42c48000, 0x42c50000, 0x42c60000, 0x42c70000, 0x42c78000, 0x42c90000, 0x42ca0000, 0x42ca8000,
        0x42cc0000, 0x42cd8000, 0x42cf0000, 0x42d08000, 0x42d20000, 0x42d40000, 0x42d58000, 0x42d78000, 0x42d98000, 0x42db8000,
        0x42de0000, 0x42e08000, 0x42e30000, 0x42e60000, 0x42e90000, 0x42ec0000, 0x42ef0000, 0x42e98000, 0x42e40000, 0x42de8000,
        0x42d90000, 0x42d48000, 0x42cf8000, 0x42cb0000, 0x42c60000, 0x42c20000, 0x42be0000, 0x42ba0000, 0x42b60000, 0x42b28000,
        0x42ae8000, 0x42ab0000, 0x42a78000, 0x42a48000, 0x42a18000, 0x429e8000, 0x429b8000, 0x42990000, 0x42968000, 0x42938000,
        0x42910000, 0x428f0000, 0x428c8000, 0x428a8000, 0x42880000, 0x42860000, 0x42848000, 0x42828000, 0x42808000, 0x427e0000,
        0x427b0000, 0x42780000, 0x42740000, 0x42710000, 0x426f0000, 0x426c0000, 0x42690000, 0x42670000, 0x42640000, 0x42620000,
        0x42600000, 0x425e0000, 0x425c0000, 0x425a0000, 0x42580000, 0x42570000, 0x42550000, 0x42530000, 0x42520000, 0x42500000,
        0x424f0000, 0x424e0000, 0x424c0000, 0x424b0000, 0x424a0000, 0x42490000, 0x42480000, 0x42470000, 0x42450000, 0x42430000,
        0x42410000, 0x42400000, 0x423e0000, 0x423d0000, 0x423c0000, 0x423a0000, 0x42390000, 0x42380000, 0x42370000, 0x42360000,
        0x42350000, 0x42340000, 0x42330000, 0x42330000, 0x42320000, 0x42310000, 0x42310000, 0x42300000, 0x42300000, 0x422f0000,
        0x422f0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422d0000, 0x422d0000, 0x422e0000,
        0x422d0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422e0000, 0x422f0000, 0x422f0000, 0x42300000, 0x42300000, 0x42310000,
        0x42310000, 0x42320000, 0x42330000, 0x42330000, 0x42340000, 0x42350000, 0x42360000, 0x42370000, 0x42380000, 0x42390000,
        0x423a0000, 0x423c0000, 0x423d0000, 0x423e0000, 0x42400000, 0x42410000, 0x42430000, 0x42450000, 0x42460000, 0x42480000,
        0x424a0000, 0x424c0000, 0x424f0000, 0x42510000, 0x42530000, 0x42550000, 0x42580000, 0x425b0000, 0x425d0000, 0x42600000,
        0x42630000, 0x42660000, 0x426a0000, 0x426d0000, 0x42710000, 0x42750000, 0x42790000, 0x427d0000, 0x42808000, 0x42830000,
        0x42850000, 0x42878000, 0x428a0000, 0x428d0000, 0x42900000, 0x42930000, 0x42960000, 0x42990000, 0x429c8000, 0x42a08000,
        0x42a48000, 0x42a80000, 0x42ac8000, 0x42b10000, 0x42b58000, 0x42ba8000, 0x42c00000, 0x42c58000, 0x42cb0000, 0x42d18000,
        0x42d80000, 0x42df0000, 0x42e60000, 0x42ee0000, 0x42f60000, 0x42ff0000, 0x43044000, 0x43040000, 0x43024000, 0x43008000,
        0x42fe0000, 0x42fb0000, 0x42f90000, 0x42f68000, 0x42f48000, 0x42f30000, 0x42f10000, 0x42ef8000, 0x42ee0000, 0x42ec8000,
        0x42ec0000, 0x42ea8000, 0x42ea0000, 0x42e90000, 0x42e88000, 0x42e80000, 0x42e70000, 0x42e70000, 0x42e68000, 0x42e38000,
        0x42df0000, 0x42db0000, 0x42d78000, 0x42d40000, 0x42d08000, 0x42cd8000, 0x42cb0000, 0x42c80000, 0x42c60000, 0x42c38000,
        0x42c10000, 0x42bf0000, 0x42bd0000, 0x42bb0000, 0x42b98000, 0x42b80000, 0x42b68000, 0x42b50000, 0x42b40000, 0x42b28000,
        0x42b18000, 0x42b00000, 0x42af0000, 0x42ae0000, 0x42ad8000, 0x42ac8000, 0x42ab8000, 0x42ab0000, 0x42ab0000, 0x42aa0000,
        0x42aa0000,
    }.Select(B).ToArray();

    // ---- shared constants (4-wide body / scalar tail use bit-identical values from different addresses)
    static readonly float AtanC0 = B(0xbd3e7316), AtanC1 = B(0x3e232344), AtanC2 = B(0xbea7be2c);
    const double AtanC0d = -0.046496474900000001, AtanC1d = 0.15931422000000001, AtanC2d = -0.32762276400000001;
    static readonly float HalfPi = B(0x3fc90fdb), Pi = B(0x40490fdb), RadToDeg = B(0x42652ee0);
    static readonly float ChromaFloor = B(0x3c23d70a);            // 0.01
    static readonly float K07 = B(0x3f333333), K0333 = B(0x3eaa7efa), K03 = B(0x3e99999a);
    static readonly float WaS = B(0x403758b3), WaO = B(0xc0700000);   // 2.86478877, −3.75
    static readonly float WbS = B(0x3fd189f1), WbO = B(0x3f36db6e);   // 1.63702214, 0.714285731
    static readonly float WcS = B(0x3d4ccccd), WcO = B(0xc0200000);   // 0.05, −2.5
    static readonly float WhS = B(0x3ff47645), WdO = B(0x40355556), WeO = B(0x40b00001);   // 1.9098593, 2.83333349, 5.50000048
    static readonly float WfS = B(0x3d088889), WfO = B(0xbe2aaaab);   // 0.0333333351, −0.166666672
    static readonly float L40 = B(0x42200000), WlS = B(0x3d23d70a);   // 40, 0.04
    static readonly float Eps6 = B(0x358637bd);                       // 1e-6 (body and tail)
    static readonly float L16 = 16f, Inv116 = B(0x3c0d3dcb), AScale = B(0x3b03126f), BScale = B(0x3ba3d70a), BScaleNeg = B(0xbba3d70a);
    static readonly float LinK = B(0x3e038027), LinOff = B(0xbc911aa7), Cbrt6_29 = B(0x3e53dcb1), YLin = B(0x3a911aa7), L8 = 8f;

    static float Rcp(float x) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    static float Rsqrt(float x) => IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
    static float Max0(float v) => v > 0f ? v : 0f;                       // MAXPS order: NaN → 0
    static float Clamp01(float v) { v = v > 0f ? v : 0f; return v < 1f ? v : 1f; }   // MAXPS order: NaN → 0
    static int Cvtps2dq(float v) => Simd.ConvertToVector128Int32(Vector128.CreateScalar(v)).ToScalar();
    static int Cvttss2si(float v) => Simd.ConvertToInt32WithTruncation(Vector128.CreateScalar(v));

    /// <summary>Hue in radians, 4-wide body: t = rcp(max(max|a|,|b|), 1e-6))·min, atan(t) = (t²·t)·((t²·c0 + c1)·t² + c2) + t,
    /// then the octant folds (|a| &lt; |b| → π/2 − ·, a &lt; 0 → π − ·, b &lt; 0 → −·).</summary>
    static float HueVec(float a, float b, float absA, float absB)
    {
        float mn = absA < absB ? absA : absB, mx = absA > absB ? absA : absB;   // absA is the first operand, so NaN gives absB
        mx = mx > Eps6 ? mx : Eps6;
        float t = Rcp(mx) * mn;
        float t2 = t * t;
        float p = (t2 * AtanC0 + AtanC1) * t2 + AtanC2;
        float h = (t2 * t) * p + t;
        if (absA < absB) h = HalfPi - h;
        if (a < 0f) h = Pi - h;
        if (b < 0f) h = -h;
        return h;
    }

    /// <summary>Hue in radians, scalar tail: the same t (float), the polynomial in double from (double)t and (double)(t·t).</summary>
    static float HueTail(float a, float b, float absA, float absB)
    {
        float mn = absA < absB ? absA : absB, mx = absA > absB ? absA : absB;   // the same operand order as HueVec
        mx = mx > Eps6 ? mx : Eps6;
        float t = Rcp(mx) * mn;
        double td = t, t2 = (double)(t * t);
        double p = (t2 * AtanC0d + AtanC1d) * t2 + AtanC2d;
        float h = (float)((t2 * td) * p + td);
        if (absA < absB) h = HalfPi - h;
        if (a < 0f) h = Pi - h;
        if (b < 0f) h = -h;
        return h;
    }

    /// <summary>Table index, body: d = h·57.2957764 + 0.5 clamped to [−179, 180], d ≤ 0 → d + 360, then `cvtps2dq` (round to nearest even).</summary>
    static int IndexVec(float h)
    {
        float d = h * RadToDeg + 0.5f;
        d = d > -179f ? d : -179f;
        d = d < 180f ? d : 180f;
        float f = 0f < d ? d : d + 360f;
        return Cvtps2dq(f);
    }

    /// <summary>Table index, tail: i = `cvttss2si`(h·57.2957764 + 0.5) clamped to [−179, 180] as an integer, i &lt; 0 → i + 360.</summary>
    static int IndexTail(float h)
    {
        int i = Cvttss2si(h * RadToDeg + 0.5f);
        if (i <= -180) i = -179;
        if (i >= 181) i = 180;
        return i < 0 ? i + 360 : i;
    }

    /// <summary>Lab → XYZ with the kernels' piecewise inverse (shared by every kernel and both column kinds).</summary>
    static (float X, float Y, float Z) LabToXyz(float lp, float fx, float fy, float fz)
    {
        float X = fx < Cbrt6_29 ? fx * LinK + LinOff : (fx * fx) * fx;
        float Y = lp < L8 ? lp * YLin : (fy * fy) * fy;
        float Z = fz < Cbrt6_29 ? fz * LinK + LinOff : (fz * fz) * fz;
        return (X, Y, Z);
    }

    /// <summary>Vibrance up (v &gt; 1) — the pre-loop scalars: v' = clamp(v, 0, 2), g = 1.5·v' − 0.5 (v' &gt; 1) or (1 − v')·0.3 + v',
    /// w = clamp((g − 1)·(2/3), 0, 1).</summary>
    public readonly struct Up
    {
        public readonly float Gain, LWeight, Sat;
        public Up(float vib, float sat)
        {
            float v = vib > 0f ? vib : 0f; v = v < 2f ? v : 2f;                       // NaN gives 0
            float g = v > 1f ? 1.5f * v : (1f - v) * K03;
            g += 1f < v ? -0.5f : v;
            float w = (-1f + g) * B(0x3f2aaaab);
            w = 0f > w ? 0f : w; w = w < 1f ? w : 1f;                                 // NaN gives 1
            Gain = g; LWeight = w; Sat = sat;
        }

        /// <summary>One pixel of the 4-wide body: returns (lightness, chroma ratio · saturation).</summary>
        public (float Lp, float R) Vec(float lp, float a, float b)
        {
            float absA = MathF.Abs(a), absB = MathF.Abs(b);
            float h = HueVec(a, b, absA, absB);
            float lut = HueTable[IndexVec(h)];
            float c = MathF.Sqrt(b * b + a * a);
            c = c > ChromaFloor ? c : ChromaFloor;
            float r0 = Rcp(c);
            float rc = (1f - c * r0) * r0 + r0;
            float lutK = lut * K07;
            float u = (c - lutK) * K0333 + lutK;
            u = u > c ? u : c;                                                         // MAXPS order: NaN gives c
            float cf = c * Gain;
            float wA = Clamp01(h * WaS + WaO), wB = Clamp01(h * WbS + WbO), wC = Clamp01(c * WcS + WcO);
            float x1 = (1f - wA) * wB;
            float ct = cf < u ? cf : u;                                                // MINPS order: NaN gives u
            float ratio = ct * rc;
            float x2 = (1f - wC) * x1;
            float z = ((K07 - ratio) + ratio * K03) * x2;
            float hh = h * WhS;
            float wD = Clamp01(hh + WdO);
            float wE = Clamp01(hh + WeO) * LWeight;
            float wF = Clamp01(c * WfS + WfO) * (1f - wD);
            float lt = (lp + -L40) * 0.5f + L40;
            lt = lt < lp ? lt : lp;                                                    // MINPS order: NaN gives lp
            float lp2 = ((lt - lp) * wE) * wF + lp;
            float wL = Clamp01(lp2 * WlS);
            float r2 = (((ratio + -1f) + z) * wL + 1f) * Sat;
            return (lp2, r2);
        }

        /// <summary>One pixel of the scalar tail.</summary>
        public (float Lp, float R) Tail(float lp, float a, float b)
        {
            float absA = MathF.Abs(a), absB = MathF.Abs(b);
            float h = HueTail(a, b, absA, absB);
            float lutK = HueTable[IndexTail(h)] * K07;
            float c2 = b * b + a * a;
            float rs = Rsqrt(c2), s = c2 * rs;
            float c = c2 == 0f ? 0f : (s * rs + -3f) * (s * -0.5f);
            c = c > ChromaFloor ? c : ChromaFloor;                                     // MAXSS order: NaN gives 0.01
            float u = (c - lutK) * K0333 + lutK;
            u = u > c ? u : c;
            float ct = c * Gain; ct = ct < u ? ct : u;
            float ratio = ct / c;
            float omA = 1f - Clamp01(h * WaS + WaO);
            float omC = 1f - Clamp01(c * WcS + WcO);
            float wB = Clamp01(h * WbS + WbO);
            float x = (omC * wB) * omA;
            float z = (K07 - ratio * K07) * x;
            float hh = h * WhS;
            float omD = 1f - Clamp01(hh + WdO);
            float wF = Clamp01(c * WfS + WfO);
            float lt = (lp + -L40) * 0.5f + L40;
            lt = lt < lp ? lt : lp;
            float wE = Clamp01(hh + WeO);
            float lp2 = (((((lt - lp) * LWeight) * wF) * wE) * omD) + lp;
            float wL = Clamp01(lp2 * WlS);
            float r2 = (((ratio + -1f) + z) * wL + 1f) * Sat;
            return (lp2, r2);
        }
    }

    /// <summary>Vibrance down (v &lt; 1) — pre-loop: g as in <see cref="Up"/>, w = clamp((g − 0.2)·5, 0, 1); the chroma factor is
    /// min(table[hue]·(1/84)·0.7·(1 − w) + w, 1)·g·saturation, the lightness is untouched.</summary>
    public readonly struct Down
    {
        public readonly float W, GainSat, KVec, KTail, Rcp84;
        public Down(float vib, float sat)
        {
            float v = 0f > vib ? 0f : vib; v = v < 2f ? v : 2f;                       // NaN gives 2
            float g = v > 1f ? 1.5f * v : (1f - v) * K03;
            g += 1f < v ? -0.5f : v;
            float w = (B(0xbe4ccccd) + g) * 5f;                                        // (−0.2 + g)·5
            w = w > 0f ? w : 0f; w = w < 1f ? w : 1f;                                  // NaN gives 0
            float om = 1f - w;
            W = w; GainSat = g * sat;
            Rcp84 = Rcp(84f);
            KVec = Rcp84 * (om * K07);   // rcpss(84) broadcast · ((1 − w)·0.7 vec)
            KTail = K07 * om;            // 0.7·(1 − w)
        }
        public float Vec(float a, float b)
        {
            float h = HueVec(a, b, MathF.Abs(a), MathF.Abs(b));
            float f = HueTable[IndexVec(h)] * KVec + W;
            f = f < 1f ? f : 1f;
            return f * GainSat;
        }
        public float Tail(float a, float b)
        {
            float h = HueTail(a, b, MathF.Abs(a), MathF.Abs(b));
            float f = (HueTable[IndexTail(h)] * KTail) * Rcp84 + W;
            f = f < 1f ? f : 1f;
            return f * GainSat;
        }
    }

    // ---- the 4-wide body on eight lanes — each lane performs the body's operations above, in the same order ----

    static Vector256<float> Sel(Vector256<float> m, Vector256<float> a, Vector256<float> b) => Vector256.ConditionalSelect(m, a, b);
    static Vector256<float> Clamp01(Vector256<float> v) { var z = Vector256<float>.Zero; var o = Vector256.Create(1f); v = Sel(Vector256.GreaterThan(v, z), v, z); return Sel(Vector256.LessThan(v, o), v, o); }
    static Vector256<float> Neg(Vector256<float> v) => (v.AsUInt32() ^ Vector256.Create(0x80000000u)).AsSingle();   // the sign flip of `-h`

    static Vector256<float> HueVec8(Vector256<float> a, Vector256<float> b, Vector256<float> absA, Vector256<float> absB)
    {
        var lt = Vector256.LessThan(absA, absB);
        var mn = Sel(lt, absA, absB); var mx = Sel(Vector256.GreaterThan(absA, absB), absA, absB);
        var e6 = Vector256.Create(Eps6); mx = Sel(Vector256.GreaterThan(mx, e6), mx, e6);
        var t = IntelApprox.Reciprocal(mx) * mn;
        var t2 = t * t;
        var p = (t2 * Vector256.Create(AtanC0) + Vector256.Create(AtanC1)) * t2 + Vector256.Create(AtanC2);
        var h = (t2 * t) * p + t;
        h = Sel(lt, Vector256.Create(HalfPi) - h, h);
        h = Sel(Vector256.LessThan(a, Vector256<float>.Zero), Vector256.Create(Pi) - h, h);
        return Sel(Vector256.LessThan(b, Vector256<float>.Zero), Neg(h), h);
    }

    static unsafe Vector256<float> TableVec8(Vector256<float> h)
    {
        var d = h * Vector256.Create(RadToDeg) + Vector256.Create(0.5f);
        var lo = Vector256.Create(-179f); var hi = Vector256.Create(180f);
        d = Sel(Vector256.GreaterThan(d, lo), d, lo);
        d = Sel(Vector256.LessThan(d, hi), d, hi);
        var f = Sel(Vector256.LessThan(Vector256<float>.Zero, d), d, d + Vector256.Create(360f));
        var i = Simd.ConvertToVector256Int32(f);   // cvtps2dq: round to nearest even (as the scalar model)
        fixed (float* tp = HueTable) return Simd.GatherVector256(tp, i, 4);
    }

    /// <summary>The body of one row of the vibrance kernels: columns [0, <paramref name="end"/>) eight at a time (kernel 1 = <see cref="Up.Vec"/>,
    /// 2 = <see cref="Down.Vec"/>, then <see cref="Reconstruct"/> with tail = false) from the sharpened lightness <paramref name="lp"/> (≥ 0 already);
    /// returns the first column not done.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int BodyRow(int kernel, in Up up, in Down down, float[] lpRow, float[] A, float[] B, int ab, float[] minv, float[] rgba, int o0, int end)
    {
        if (!Simd.IsWideSupported) return 0;
        int x = 0;
        ref float ob = ref MemoryMarshal.GetArrayDataReference(rgba);
        var one = Vector256.Create(1f);
        for (; x + 8 <= end; x += 8)
        {
            var lp = Vector256.Create(lpRow.AsSpan(x, 8));
            var a = Vector256.Create(A.AsSpan(ab + x, 8)); var b = Vector256.Create(B.AsSpan(ab + x, 8));
            var absA = Vector256.Abs(a); var absB = Vector256.Abs(b);
            var h = HueVec8(a, b, absA, absB);
            var lut = TableVec8(h);
            Vector256<float> lq, rr;
            if (kernel == 1)
            {
                var c = Vector256.Sqrt(b * b + a * a);
                var cfl = Vector256.Create(ChromaFloor); c = Sel(Vector256.GreaterThan(c, cfl), c, cfl);
                var r0 = IntelApprox.Reciprocal(c);
                var rc = (one - c * r0) * r0 + r0;
                var k07 = Vector256.Create(K07);
                var lutK = lut * k07;
                var u = (c - lutK) * Vector256.Create(K0333) + lutK;
                u = Sel(Vector256.GreaterThan(u, c), u, c);
                var cf = c * Vector256.Create(up.Gain);
                var wA = Clamp01(h * Vector256.Create(WaS) + Vector256.Create(WaO)); var wB = Clamp01(h * Vector256.Create(WbS) + Vector256.Create(WbO)); var wC = Clamp01(c * Vector256.Create(WcS) + Vector256.Create(WcO));
                var x1 = (one - wA) * wB;
                var ct = Sel(Vector256.LessThan(cf, u), cf, u);
                var ratio = ct * rc;
                var x2 = (one - wC) * x1;
                var z = ((k07 - ratio) + ratio * Vector256.Create(K03)) * x2;
                var hh = h * Vector256.Create(WhS);
                var wD = Clamp01(hh + Vector256.Create(WdO));
                var wE = Clamp01(hh + Vector256.Create(WeO)) * Vector256.Create(up.LWeight);
                var wF = Clamp01(c * Vector256.Create(WfS) + Vector256.Create(WfO)) * (one - wD);
                var l40 = Vector256.Create(L40);
                var lt = (lp + Vector256.Create(-L40)) * Vector256.Create(0.5f) + l40;
                lt = Sel(Vector256.LessThan(lt, lp), lt, lp);
                var lp2 = ((lt - lp) * wE) * wF + lp;
                var wL = Clamp01(lp2 * Vector256.Create(WlS));
                rr = (((ratio + Vector256.Create(-1f)) + z) * wL + one) * Vector256.Create(up.Sat);
                lq = lp2;
            }
            else
            {
                var f = lut * Vector256.Create(down.KVec) + Vector256.Create(down.W);
                f = Sel(Vector256.LessThan(f, one), f, one);
                rr = f * Vector256.Create(down.GainSat);
                lq = lp;
            }
            // Reconstruct (body): fx = (a·r)·0.002 + fy, fz = fy − (r·b)·0.005; rows m2·Z + (m1·Y + m0·X); no clamp
            var fy = (lq + Vector256.Create(L16)) * Vector256.Create(Inv116);
            var fx = (a * rr) * Vector256.Create(AScale) + fy;
            var fz = fy - (rr * b) * Vector256.Create(BScale);
            var cb = Vector256.Create(Cbrt6_29); var lk = Vector256.Create(LinK); var lo = Vector256.Create(LinOff);
            var X = Sel(Vector256.LessThan(fx, cb), fx * lk + lo, (fx * fx) * fx);
            var Y = Sel(Vector256.LessThan(lq, Vector256.Create(L8)), lq * Vector256.Create(YLin), (fy * fy) * fy);
            var Z = Sel(Vector256.LessThan(fz, cb), fz * lk + lo, (fz * fz) * fz);
            var R = Vector256.Create(minv[2]) * Z + (Vector256.Create(minv[1]) * Y + Vector256.Create(minv[0]) * X);
            var G = Vector256.Create(minv[5]) * Z + (Vector256.Create(minv[4]) * Y + Vector256.Create(minv[3]) * X);
            var Bc = Vector256.Create(minv[8]) * Z + (Vector256.Create(minv[7]) * Y + Vector256.Create(minv[6]) * X);
            Isp.Soa8.Store(ref Unsafe.Add(ref ob, o0 + x * 4), R, G, Bc, one);
        }
        return x;
    }

    /// <summary>The Lab → camera reconstruction of the vibrance kernels for one pixel. Body: fx = (a·r)·0.002 + fy, fz = fy − (r·b)·0.005;
    /// tail: fx = (a·0.002)·r + fy, fz = (b·(−0.005))·r + fy, output clamped at 0. <paramref name="minv"/> = the inverse white-scaled
    /// matrix; each row is m2·Z + (m1·Y + m0·X).</summary>
    public static (float R, float G, float B) Reconstruct(float lp, float a, float b, float r, float[] minv, bool tail)
    {
        float fy = (lp + L16) * Inv116;
        float fx, fz;
        if (!tail) { fx = (a * r) * AScale + fy; fz = fy - (r * b) * BScale; }
        else { fx = (a * AScale) * r + fy; fz = (b * BScaleNeg) * r + fy; }
        var (X, Y, Z) = LabToXyz(lp, fx, fy, fz);
        float R = minv[2] * Z + (minv[1] * Y + minv[0] * X);
        float G = minv[5] * Z + (minv[4] * Y + minv[3] * X);
        float Bc = minv[8] * Z + (minv[7] * Y + minv[6] * X);
        if (tail) { R = Max0(R); G = Max0(G); Bc = Max0(Bc); }
        return (R, G, Bc);
    }
}
