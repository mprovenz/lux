using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>Hardware-approximate SSE ops and Intel min/max semantics used verbatim by cp.dll's ResAmp code (spec `a-resamp.md` §10 item 2:
/// the CPU cp.dll was run on defines the truth — this port runs the same instructions through the .NET intrinsics).</summary>
internal static class SseOps
{
    public static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// <summary>`rcpss` — raw 12-bit reciprocal, no Newton step.</summary>
    public static float Rcpss(float x) => Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    /// <summary>`rsqrtss` — raw approximate reciprocal square root.</summary>
    public static float Rsqrtss(float x) => Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
    /// <summary>`rcpps` on 4 lanes.</summary>
    public static void Rcpps(ReadOnlySpan<float> src, Span<float> dst)
    {
        var v = Sse.Reciprocal(Vector128.Create(src[0], src[1], src[2], src[3]));
        dst[0] = v.GetElement(0); dst[1] = v.GetElement(1); dst[2] = v.GetElement(2); dst[3] = v.GetElement(3);
    }
    /// <summary>`rsqrtps` on 4 lanes.</summary>
    public static void Rsqrtps(ReadOnlySpan<float> src, Span<float> dst)
    {
        var v = Sse.ReciprocalSqrt(Vector128.Create(src[0], src[1], src[2], src[3]));
        dst[0] = v.GetElement(0); dst[1] = v.GetElement(1); dst[2] = v.GetElement(2); dst[3] = v.GetElement(3);
    }

    /// <summary>`maxss/maxps dst, src` = dst &gt; src ? dst : src (NaN / ±0 → src).</summary>
    public static float Max(float dst, float src) => dst > src ? dst : src;
    /// <summary>`minss/minps dst, src` = dst &lt; src ? dst : src.</summary>
    public static float Min(float dst, float src) => dst < src ? dst : src;

    /// <summary>`cvttss2si` (truncate toward zero; out-of-range/NaN → 0x80000000).</summary>
    public static int Cvtt(float x)
    {
        if (float.IsNaN(x) || x >= 2147483648.0f || x < -2147483648.0f) return int.MinValue;
        return (int)x;
    }
    /// <summary>`cvttsd2si`.</summary>
    public static int Cvtt(double x)
    {
        if (double.IsNaN(x) || x >= 2147483648.0 || x < -2147483648.0) return int.MinValue;
        return (int)x;
    }
    /// <summary>`roundss …, 9` (floor) then `cvttss2si`.</summary>
    public static int FloorI(float x) => Cvtt(MathF.Floor(x));
    /// <summary>`roundss …, 0xA` (ceil) then `cvttss2si`.</summary>
    public static int CeilI(float x) => Cvtt(MathF.Ceiling(x));
}
