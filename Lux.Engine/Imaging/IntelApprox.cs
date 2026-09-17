using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Imaging;

/// <summary>
/// Software model of Intel's `rcpps/rcpss` and `rsqrtps/rsqrtss` — the approximate reciprocal and reciprocal square root the
/// pipeline reproduces from cp.dll. The hardware result is a table function of the top 12 mantissa bits (one 4096-entry table for the
/// reciprocal, one per exponent parity for the reciprocal square root) scaled by an exact power of two, with these special cases:
/// ±0 and denormals (treated as zero) → ±∞, ±∞ → ±0, NaN → the quieted NaN, a negative rsqrt input → the indefinite QNaN
/// (0xffc00000), and a result that would be denormal → ±0. The tables are the ones this project's reference machine produces
/// (`IntelApprox.Tables.cs`); the model matches that hardware for every one of the 2³² inputs (checked 2026-09-15). The
/// approximation tables differ between CPU vendors (about half the entries differ from an AMD Zen 3's, each by one or two
/// table steps), so using the hardware instruction would make Lux's output depend on the machine; this class makes it the same
/// everywhere, and identical to the reference outputs.
/// </summary>
public static partial class IntelApprox
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RcpBits(uint b)
    {
        uint sign = b & 0x80000000u, e = (b >> 23) & 0xff, m = b & 0x7fffff;
        if (e == 0xff) return m != 0 ? (b | 0x400000u) : sign;          // NaN → quiet NaN of the input; ±inf → ±0
        if (e == 0) return sign | 0x7f800000u;                           // ±0 and denormals → ±inf
        uint r = RcpTable[(int)(m >> 11)]; int re = (int)((r >> 23) & 0xff) - ((int)e - 127);
        if (re <= 0) return sign;                                        // denormal result → ±0
        return sign | ((uint)re << 23) | (r & 0x7fffff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RsqrtBits(uint b)
    {
        uint sign = b & 0x80000000u, e = (b >> 23) & 0xff, m = b & 0x7fffff;
        if (e == 0xff && m != 0) return b | 0x400000u;                   // NaN → quiet NaN
        if (e == 0) return sign | 0x7f800000u;                           // ±0 and denormals → ±inf (sign kept)
        if (sign != 0) return 0xffc00000u;                               // negative → indefinite QNaN
        if (e == 0xff) return 0;                                         // +inf → +0
        int k = (int)e - 127; uint r; int re;
        if ((k & 1) == 0) { r = RsqrtOddTable[(int)(m >> 11)]; re = (int)((r >> 23) & 0xff) - k / 2; }          // same parity as exponent 127
        else { r = RsqrtEvenTable[(int)(m >> 11)]; re = (int)((r >> 23) & 0xff) - (k - 1) / 2; }               // same parity as exponent 128
        if (re <= 0) return 0;
        return ((uint)re << 23) | (r & 0x7fffff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rcp(float x) => BitConverter.UInt32BitsToSingle(RcpBits(BitConverter.SingleToUInt32Bits(x)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rsqrt(float x) => BitConverter.UInt32BitsToSingle(RsqrtBits(BitConverter.SingleToUInt32Bits(x)));

    static readonly Vector128<uint> SignMask = Vector128.Create(0x80000000u), ExpMask = Vector128.Create(0xffu), ManMask = Vector128.Create(0x7fffffu),
        Inf = Vector128.Create(0x7f800000u), Quiet = Vector128.Create(0x400000u), Ff = Vector128.Create(0xffu), One = Vector128.Create(1u),
        Indefinite = Vector128.Create(0xffc00000u);
    static readonly Vector128<int> Bias = Vector128.Create(127);

    /// <summary>`rcpps`: all four lanes. With AVX2 the table lookup is a gather and the special cases are lane masks;
    /// otherwise four scalar lookups.</summary>
    public static unsafe Vector128<float> Reciprocal(Vector128<float> v)
    {
        var u = v.AsUInt32();
        if (!Avx2.IsSupported)
            return Vector128.Create(RcpBits(u.GetElement(0)), RcpBits(u.GetElement(1)), RcpBits(u.GetElement(2)), RcpBits(u.GetElement(3))).AsSingle();
        var sign = u & SignMask; var e = (u >> 23) & ExpMask; var m = u & ManMask;
        Vector128<uint> r;
        fixed (uint* t = RcpTable) r = Avx2.GatherVector128(t, (m >> 11).AsInt32(), 4);
        var re = ((r >> 23) & Ff).AsInt32() + Bias - e.AsInt32();
        var res = sign | (re.AsUInt32() << 23) | (r & ManMask);
        res = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(re, Vector128<int>.Zero).AsUInt32(), sign, res);   // denormal result → ±0
        res = Vector128.ConditionalSelect(Vector128.Equals(e, Vector128<uint>.Zero), sign | Inf, res);                  // ±0, denormal → ±inf
        res = Vector128.ConditionalSelect(Vector128.Equals(e, Ff),
            Vector128.ConditionalSelect(Vector128.Equals(m, Vector128<uint>.Zero), sign, u | Quiet), res);              // ±inf → ±0, NaN → quiet
        return res.AsSingle();
    }
    /// <summary>`rcpss`: lane 0 approximated, lanes 1–3 passed through unchanged.</summary>
    public static Vector128<float> ReciprocalScalar(Vector128<float> v) => v.AsUInt32().WithElement(0, RcpBits(v.AsUInt32().GetElement(0))).AsSingle();
    /// <summary>`rsqrtps`: all four lanes (AVX2 gathers from both parity tables, else four scalar lookups).</summary>
    public static unsafe Vector128<float> ReciprocalSqrt(Vector128<float> v)
    {
        var u = v.AsUInt32();
        if (!Avx2.IsSupported)
            return Vector128.Create(RsqrtBits(u.GetElement(0)), RsqrtBits(u.GetElement(1)), RsqrtBits(u.GetElement(2)), RsqrtBits(u.GetElement(3))).AsSingle();
        var sign = u & SignMask; var e = (u >> 23) & ExpMask; var m = u & ManMask; var idx = (m >> 11).AsInt32();
        Vector128<uint> rOdd, rEven;
        fixed (uint* t = RsqrtOddTable) rOdd = Avx2.GatherVector128(t, idx, 4);
        fixed (uint* t = RsqrtEvenTable) rEven = Avx2.GatherVector128(t, idx, 4);
        var k = e.AsInt32() - Bias;
        var r = Vector128.ConditionalSelect(Vector128.Equals(e & One, One), rOdd, rEven);   // e odd ⇔ k even ⇔ the e=127-parity table
        var re = ((r >> 23) & Ff).AsInt32() - Vector128.ShiftRightArithmetic(k, 1);         // k/2 for even k, (k−1)/2 for odd k
        var res = (re.AsUInt32() << 23) | (r & ManMask);
        res = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(re, Vector128<int>.Zero).AsUInt32(), Vector128<uint>.Zero, res);
        res = Vector128.ConditionalSelect(Vector128.Equals(e, Ff), Vector128<uint>.Zero, res);                   // +inf → +0
        res = Vector128.ConditionalSelect(Vector128.Equals(sign, SignMask), Indefinite, res);                    // negative → 0xffc00000
        res = Vector128.ConditionalSelect(Vector128.Equals(e, Vector128<uint>.Zero), sign | Inf, res);          // ±0, denormal → ±inf
        var nan = Vector128.Equals(e, Ff) & Vector128.GreaterThan(m, Vector128<uint>.Zero);
        res = Vector128.ConditionalSelect(nan, u | Quiet, res);                                                   // NaN → quiet NaN
        return res.AsSingle();
    }
    /// <summary>`rsqrtss`: lane 0 approximated, lanes 1–3 passed through unchanged.</summary>
    public static Vector128<float> ReciprocalSqrtScalar(Vector128<float> v) => v.AsUInt32().WithElement(0, RsqrtBits(v.AsUInt32().GetElement(0))).AsSingle();
}
