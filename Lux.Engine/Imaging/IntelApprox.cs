using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Imaging;

/// <summary>
/// Software model of Intel's `rcpps/rcpss` and `rsqrtps/rsqrtss` — the approximate reciprocal and reciprocal square root the
/// pipeline reproduces from Lumen. The hardware result is a table function of the top 12 mantissa bits (one 4096-entry table for the
/// reciprocal, one per exponent parity for the reciprocal square root) scaled by an exact power of two, with these special cases:
/// ±0 and denormals (treated as zero) → ±∞, ±∞ → ±0, NaN → the quieted NaN, a negative rsqrt input → the indefinite QNaN
/// (0xffc00000), and a result that would be denormal → ±0. The tables are the ones this project's reference machine produces
/// (`IntelApprox.Tables.cs`); the model matches that hardware for every one of the 2³² inputs. The
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

    // The vector forms write their lane constants as Vector128.Create / Vector256.Create calls at each use. The JIT turns these
    // into constant loads; a static readonly vector field is read from memory on every call, which costs several times more here.
    //
    // The reciprocal: a table entry holds the result for an input exponent of 127, with exponent 126 and the sign bit clear. Adding
    // 127 << 23 and subtracting the input's exponent field gives the result's exponent field in place. For a normal input (exponent
    // field 1 to 254) this value is the result when its exponent field is at least 1; a smaller result would be denormal and gives ±0.

    /// <summary>`rcpps`: all four lanes. With fast gathers (<see cref="Simd.IsWideSupported"/>) the table lookup is a gather and the special cases are lane masks;
    /// otherwise four scalar lookups. When all four lanes are normal inputs with normal results, the special cases are skipped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector128<float> Reciprocal(Vector128<float> v)
    {
        var u = v.AsUInt32();
        if (!Simd.IsWideSupported)
            return Vector128.Create(RcpBits(u.GetElement(0)), RcpBits(u.GetElement(1)), RcpBits(u.GetElement(2)), RcpBits(u.GetElement(3))).AsSingle();
        var expField = Vector128.Create(0x7f800000u); var man = Vector128.Create(0x7fffffu);
        var sign = u & Vector128.Create(0x80000000u); var ef = u & expField; var m = u & man;
        Vector128<uint> r;
        fixed (uint* t = RcpTable) r = Simd.GatherVector128(t, (m >> 11).AsInt32(), 4);
        var res = r + Vector128.Create(0x3f800000u) - ef;
        var normalOut = Vector128.GreaterThan(res.AsInt32(), Vector128.Create(0x7fffff)).AsUInt32();                // result exponent field ≥ 1
        var normalIn = Vector128.GreaterThan(ef.AsInt32(), Vector128<int>.Zero) & Vector128.LessThan(ef.AsInt32(), expField.AsInt32());
        if ((normalOut & normalIn.AsUInt32()) == Vector128<uint>.AllBitsSet) return (sign | res).AsSingle();
        res = Vector128.ConditionalSelect(normalOut, sign | res, sign);                                               // denormal result → ±0
        res = Vector128.ConditionalSelect(Vector128.Equals(ef, Vector128<uint>.Zero), sign | expField, res);           // ±0, denormal → ±inf
        res = Vector128.ConditionalSelect(Vector128.Equals(ef, expField),
            Vector128.ConditionalSelect(Vector128.Equals(m, Vector128<uint>.Zero), sign, u | Vector128.Create(0x400000u)), res);   // ±inf → ±0, NaN → quiet
        return res.AsSingle();
    }
    /// <summary>`rcpss`: lane 0 approximated, lanes 1–3 passed through unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ReciprocalScalar(Vector128<float> v) => v.AsUInt32().WithElement(0, RcpBits(v.AsUInt32().GetElement(0))).AsSingle();

    /// <summary>`rcpps` on eight lanes: each lane = <see cref="RcpBits"/> of that lane (the same table and special cases as
    /// the four-lane form, so the same bits for all 2³² inputs). Without fast gathers, two four-lane halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector256<float> Reciprocal(Vector256<float> v)
    {
        var u = v.AsUInt32();
        if (!Simd.IsWideSupported)
            return Vector256.Create(Reciprocal(v.GetLower()), Reciprocal(v.GetUpper()));
        var expField = Vector256.Create(0x7f800000u); var man = Vector256.Create(0x7fffffu);
        var sign = u & Vector256.Create(0x80000000u); var ef = u & expField; var m = u & man;
        Vector256<uint> r;
        fixed (uint* t = RcpTable) r = Simd.GatherVector256(t, (m >> 11).AsInt32(), 4);
        var res = r + Vector256.Create(0x3f800000u) - ef;
        var normalOut = Vector256.GreaterThan(res.AsInt32(), Vector256.Create(0x7fffff)).AsUInt32();                // result exponent field ≥ 1
        var normalIn = Vector256.GreaterThan(ef.AsInt32(), Vector256<int>.Zero) & Vector256.LessThan(ef.AsInt32(), expField.AsInt32());
        if ((normalOut & normalIn.AsUInt32()) == Vector256<uint>.AllBitsSet) return (sign | res).AsSingle();
        res = Vector256.ConditionalSelect(normalOut, sign | res, sign);                                               // denormal result → ±0
        res = Vector256.ConditionalSelect(Vector256.Equals(ef, Vector256<uint>.Zero), sign | expField, res);           // ±0, denormal → ±inf
        res = Vector256.ConditionalSelect(Vector256.Equals(ef, expField),
            Vector256.ConditionalSelect(Vector256.Equals(m, Vector256<uint>.Zero), sign, u | Vector256.Create(0x400000u)), res);   // ±inf → ±0, NaN → quiet
        return res.AsSingle();
    }
    /// <summary>`rsqrtps`: all four lanes (gathers from both parity tables when they are fast, else four scalar lookups).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector128<float> ReciprocalSqrt(Vector128<float> v)
    {
        var u = v.AsUInt32();
        if (!Simd.IsWideSupported)
            return Vector128.Create(RsqrtBits(u.GetElement(0)), RsqrtBits(u.GetElement(1)), RsqrtBits(u.GetElement(2)), RsqrtBits(u.GetElement(3))).AsSingle();
        var ff = Vector128.Create(0xffu); var man = Vector128.Create(0x7fffffu); var signMask = Vector128.Create(0x80000000u);
        var sign = u & signMask; var e = (u >> 23) & ff; var m = u & man; var idx = (m >> 11).AsInt32();
        Vector128<uint> rOdd, rEven;
        fixed (uint* t = RsqrtOddTable) rOdd = Simd.GatherVector128(t, idx, 4);
        fixed (uint* t = RsqrtEvenTable) rEven = Simd.GatherVector128(t, idx, 4);
        var k = e.AsInt32() - Vector128.Create(127);
        var one = Vector128.Create(1u);
        var r = Vector128.ConditionalSelect(Vector128.Equals(e & one, one), rOdd, rEven);   // e odd ⇔ k even ⇔ the e=127-parity table
        var re = ((r >> 23) & ff).AsInt32() - Vector128.ShiftRightArithmetic(k, 1);         // k/2 for even k, (k−1)/2 for odd k
        var res = (re.AsUInt32() << 23) | (r & man);
        res = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(re, Vector128<int>.Zero).AsUInt32(), Vector128<uint>.Zero, res);
        res = Vector128.ConditionalSelect(Vector128.Equals(e, ff), Vector128<uint>.Zero, res);                                  // +inf → +0
        res = Vector128.ConditionalSelect(Vector128.Equals(sign, signMask), Vector128.Create(0xffc00000u), res);                // negative → 0xffc00000
        res = Vector128.ConditionalSelect(Vector128.Equals(e, Vector128<uint>.Zero), sign | Vector128.Create(0x7f800000u), res);   // ±0, denormal → ±inf
        var nan = Vector128.Equals(e, ff) & Vector128.GreaterThan(m, Vector128<uint>.Zero);
        res = Vector128.ConditionalSelect(nan, u | Vector128.Create(0x400000u), res);                                          // NaN → quiet NaN
        return res.AsSingle();
    }
    /// <summary>`rsqrtps` on eight lanes (two four-lane halves of <see cref="ReciprocalSqrt(Vector128{float})"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> ReciprocalSqrt(Vector256<float> v) => Vector256.Create(ReciprocalSqrt(v.GetLower()), ReciprocalSqrt(v.GetUpper()));

    /// <summary>`rsqrtss`: lane 0 approximated, lanes 1–3 passed through unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ReciprocalSqrtScalar(Vector128<float> v) => v.AsUInt32().WithElement(0, RsqrtBits(v.AsUInt32().GetElement(0))).AsSingle();
}
