using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Imaging;

/// <summary>
/// The vector operations of the engine that have x86 instruction semantics. Each operation gives the result of the x86
/// instruction for all inputs, on every processor. On an x86 processor an operation is the x86 instruction itself. On other
/// processors it is ARM vector instructions or portable vector code that gives the same bits: the second operand for a NaN
/// or a tie in `maxps` / `minps`, 0x80000000 for a NaN or an out-of-range value in the float to integer conversions,
/// saturation in the packs, and the sign bit as the selector in `blendvps`. The eight-lane operations work on two four-lane
/// halves, with the lane rules of AVX. A gather is one load for each lane.
/// <para>The float add, subtract, multiply and square root are IEEE operations. They give the same bits on all processors
/// when the result is not a NaN. No operation of this class joins a multiply and an add into one fused operation.</para>
/// <para>The build constant `LUX_SIMD_PORTABLE` makes all operations use the portable code. It is for tests.</para>
/// </summary>
public static class Simd
{
#if LUX_SIMD_PORTABLE
    static bool X86 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
    static bool Arm { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }
#else
    static bool X86 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => true; }
    static bool Arm { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => true; }
#endif

    /// <summary>True when the four-lane operations run on vector hardware.</summary>
    public static bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => X86 && Sse2.IsSupported || Arm && AdvSimd.Arm64.IsSupported || !X86 && Vector128.IsHardwareAccelerated;
    }

    /// <summary>True when the eight-lane float operations are fast: AVX on x86, two four-lane halves on ARM.</summary>
    public static bool IsWideFloatSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => X86 && Avx.IsSupported || Arm && AdvSimd.Arm64.IsSupported || !X86 && Vector128.IsHardwareAccelerated;
    }

    /// <summary>True when all eight-lane operations are fast: AVX2 on x86, two four-lane halves on ARM.</summary>
    public static bool IsWideSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => X86 && Avx2.IsSupported || Arm && AdvSimd.Arm64.IsSupported || !X86 && Vector128.IsHardwareAccelerated;
    }

    // ---- float arithmetic (IEEE) ----

    /// <summary>`addps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Add(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Add(a, b) : Portable.Add(a, b);

    /// <summary>`subps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Subtract(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Subtract(a, b) : Portable.Subtract(a, b);

    /// <summary>`mulps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Multiply(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Multiply(a, b) : Portable.Multiply(a, b);

    /// <summary>`sqrtps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Sqrt(Vector128<float> a) => X86 && Sse.IsSupported ? Sse.Sqrt(a) : Portable.Sqrt(a);

    // ---- minimum and maximum with the x86 operand rule ----

    /// <summary>`maxps`: each lane is a &gt; b ? a : b. The result is b when a lane of a or b is a NaN and when the two are equal (also +0 and −0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Max(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Max(a, b) : Portable.Max(a, b);

    /// <summary>`minps`: each lane is a &lt; b ? a : b. The result is b when a lane of a or b is a NaN and when the two are equal (also +0 and −0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Min(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Min(a, b) : Portable.Min(a, b);

    /// <summary>`maxss`: lane 0 as <see cref="Max(Vector128{float}, Vector128{float})"/>, lanes 1 to 3 from a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MaxScalar(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.MaxScalar(a, b) : Portable.MaxScalar(a, b);

    /// <summary>`minss`: lane 0 as <see cref="Min(Vector128{float}, Vector128{float})"/>, lanes 1 to 3 from a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MinScalar(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.MinScalar(a, b) : Portable.MinScalar(a, b);

    /// <summary>`pmaxud`: the unsigned maximum of each lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Max(Vector128<uint> a, Vector128<uint> b) => X86 && Sse41.IsSupported ? Sse41.Max(a, b) : Portable.Max(a, b);

    // ---- float to integer conversions with the x86 overflow value ----

    /// <summary>`cvtps2dq`: round to the nearest integer, ties to even. A NaN and a value that does not fit give 0x80000000.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ConvertToVector128Int32(Vector128<float> v) => X86 && Sse2.IsSupported ? Sse2.ConvertToVector128Int32(v) : Portable.ConvertToVector128Int32(v);

    /// <summary>`cvttps2dq`: truncate toward zero. A NaN and a value that does not fit give 0x80000000.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ConvertToVector128Int32WithTruncation(Vector128<float> v) => X86 && Sse2.IsSupported ? Sse2.ConvertToVector128Int32WithTruncation(v) : Portable.ConvertToVector128Int32WithTruncation(v);

    /// <summary>`vcvtps2dq` on eight lanes: each lane as <see cref="ConvertToVector128Int32"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> ConvertToVector256Int32(Vector256<float> v) => X86 && Avx.IsSupported ? Avx.ConvertToVector256Int32(v) : Portable.ConvertToVector256Int32(v);

    /// <summary>`cvttss2si` of lane 0: truncate toward zero. A NaN and a value that does not fit give 0x80000000.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ConvertToInt32WithTruncation(Vector128<float> v) => X86 && Sse.IsSupported ? Sse.ConvertToInt32WithTruncation(v) : Portable.ConvertToInt32WithTruncation(v);

    // ---- integer operations ----

    /// <summary>`paddd`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Add(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.Add(a, b) : Portable.Add(a, b);

    /// <summary>`pand`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> And(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.And(a, b) : Portable.And(a, b);

    /// <summary>`por`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Or(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.Or(a, b) : Portable.Or(a, b);

    /// <summary>`pxor`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Xor(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.Xor(a, b) : Portable.Xor(a, b);

    /// <summary>`andps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> And(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.And(a, b) : Portable.And(a, b);

    /// <summary>`orps`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Or(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.Or(a, b) : Portable.Or(a, b);

    /// <summary>`pcmpeqd`: all bits of a lane set when a = b.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> CompareEqual(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.CompareEqual(a, b) : Portable.CompareEqual(a, b);

    /// <summary>`pcmpgtd`: all bits of a lane set when a &gt; b (signed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> CompareGreaterThan(Vector128<int> a, Vector128<int> b) => X86 && Sse2.IsSupported ? Sse2.CompareGreaterThan(a, b) : Portable.CompareGreaterThan(a, b);

    /// <summary>`pslld`: each lane shifted left by <paramref name="count"/> bits. A count above 31 gives zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftLeftLogical(Vector128<int> v, [ConstantExpected] byte count) => X86 && Sse2.IsSupported ? Sse2.ShiftLeftLogical(v, count) : Portable.ShiftLeftLogical(v, count);

    /// <summary>`packssdw`: the four lanes of a then the four lanes of b, each saturated to a signed 16-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> PackSignedSaturate(Vector128<int> a, Vector128<int> b)
    {
        if (X86 && Sse2.IsSupported) return Sse2.PackSignedSaturate(a, b);
        if (Arm && AdvSimd.IsSupported) return AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(a), b);
        return Portable.PackSignedSaturate(a, b);
    }

    /// <summary>`packuswb`: the eight signed lanes of a then the eight of b, each saturated to an unsigned 8-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> PackUnsignedSaturate(Vector128<short> a, Vector128<short> b)
    {
        if (X86 && Sse2.IsSupported) return Sse2.PackUnsignedSaturate(a, b);
        if (Arm && AdvSimd.IsSupported) return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(a), b);
        return Portable.PackUnsignedSaturate(a, b);
    }

    /// <summary>`psadbw`: the sum of the absolute differences of bytes 0 to 7 in the low 64-bit lane, of bytes 8 to 15 in the high lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> SumAbsoluteDifferences(Vector128<byte> a, Vector128<byte> b)
    {
        if (X86 && Sse2.IsSupported) return Sse2.SumAbsoluteDifferences(a, b);
        if (Arm && AdvSimd.IsSupported)
            return AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AbsoluteDifference(a, b)))).AsUInt16();
        return Portable.SumAbsoluteDifferences(a, b);
    }

    // ---- selection and data movement ----

    /// <summary>`blendvps`: each lane is b when the sign bit of the mask lane is set, else a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> BlendVariable(Vector128<float> a, Vector128<float> b, Vector128<float> mask) => X86 && Sse41.IsSupported ? Sse41.BlendVariable(a, b, mask) : Portable.BlendVariable(a, b, mask);

    /// <summary>`shufps`: (a[c &amp; 3], a[(c &gt;&gt; 2) &amp; 3], b[(c &gt;&gt; 4) &amp; 3], b[c &gt;&gt; 6]).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Shuffle(Vector128<float> a, Vector128<float> b, [ConstantExpected] byte control)
    {
        if (X86 && Sse.IsSupported) return Sse.Shuffle(a, b, control);
        if (Arm && AdvSimd.Arm64.IsSupported)
        {
            if (control == 0b10_00_10_00) return AdvSimd.Arm64.UnzipEven(a, b);
            if (control == 0b11_01_11_01) return AdvSimd.Arm64.UnzipOdd(a, b);
        }
        return Portable.Shuffle(a, b, control);
    }

    /// <summary>`unpcklps`: (a0, b0, a1, b1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnpackLow(Vector128<float> a, Vector128<float> b)
    {
        if (X86 && Sse.IsSupported) return Sse.UnpackLow(a, b);
        if (Arm && AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipLow(a, b);
        return Portable.UnpackLow(a, b);
    }

    /// <summary>`unpckhps`: (a2, b2, a3, b3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> UnpackHigh(Vector128<float> a, Vector128<float> b)
    {
        if (X86 && Sse.IsSupported) return Sse.UnpackHigh(a, b);
        if (Arm && AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipHigh(a, b);
        return Portable.UnpackHigh(a, b);
    }

    /// <summary>`movhlps`: (b2, b3, a2, a3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MoveHighToLow(Vector128<float> a, Vector128<float> b) => X86 && Sse.IsSupported ? Sse.MoveHighToLow(a, b) : Portable.MoveHighToLow(a, b);

    /// <summary>`vshufps`: <see cref="Shuffle(Vector128{float}, Vector128{float}, byte)"/> in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Shuffle(Vector256<float> a, Vector256<float> b, [ConstantExpected] byte control)
    {
        if (X86 && Avx.IsSupported) return Avx.Shuffle(a, b, control);
        return Vector256.Create(Shuffle(a.GetLower(), b.GetLower(), control), Shuffle(a.GetUpper(), b.GetUpper(), control));
    }

    /// <summary>`vunpcklps`: <see cref="UnpackLow(Vector128{float}, Vector128{float})"/> in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> UnpackLow(Vector256<float> a, Vector256<float> b)
    {
        if (X86 && Avx.IsSupported) return Avx.UnpackLow(a, b);
        return Vector256.Create(UnpackLow(a.GetLower(), b.GetLower()), UnpackLow(a.GetUpper(), b.GetUpper()));
    }

    /// <summary>`vunpckhps`: <see cref="UnpackHigh(Vector128{float}, Vector128{float})"/> in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> UnpackHigh(Vector256<float> a, Vector256<float> b)
    {
        if (X86 && Avx.IsSupported) return Avx.UnpackHigh(a, b);
        return Vector256.Create(UnpackHigh(a.GetLower(), b.GetLower()), UnpackHigh(a.GetUpper(), b.GetUpper()));
    }

    /// <summary>`vpermilps`: (v[c &amp; 3], v[(c &gt;&gt; 2) &amp; 3], v[(c &gt;&gt; 4) &amp; 3], v[c &gt;&gt; 6]) in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Permute(Vector256<float> v, [ConstantExpected] byte control)
    {
        if (X86 && Avx.IsSupported) return Avx.Permute(v, control);
        return Vector256.Create(Portable.Shuffle(v.GetLower(), v.GetLower(), control), Portable.Shuffle(v.GetUpper(), v.GetUpper(), control));
    }

    /// <summary>`vperm2f128`: each half of the result is one half of a or b. Bits 0 and 1 of the control select the low half,
    /// bits 4 and 5 the high half (0 = low a, 1 = high a, 2 = low b, 3 = high b). Bit 3 or bit 7 makes that half zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Permute2x128(Vector256<float> a, Vector256<float> b, [ConstantExpected] byte control) => X86 && Avx.IsSupported ? Avx.Permute2x128(a, b, control) : Portable.Permute2x128(a, b, control);

    // ---- gathers: one load for each lane, at base + index · scale bytes (the index is a signed 32-bit value) ----

    /// <summary>`vgatherdps` on eight lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector256<float> GatherVector256(float* baseAddress, Vector256<int> index, [ConstantExpected(Min = (byte)1, Max = (byte)8)] byte scale) => X86 && Avx2.IsSupported ? Avx2.GatherVector256(baseAddress, index, scale) : Portable.GatherVector256(baseAddress, index, scale);

    /// <summary>`vpgatherdd` on eight lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector256<uint> GatherVector256(uint* baseAddress, Vector256<int> index, [ConstantExpected(Min = (byte)1, Max = (byte)8)] byte scale) => X86 && Avx2.IsSupported ? Avx2.GatherVector256(baseAddress, index, scale) : Portable.GatherVector256(baseAddress, index, scale);

    /// <summary>`vpgatherdd` on four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector128<uint> GatherVector128(uint* baseAddress, Vector128<int> index, [ConstantExpected(Min = (byte)1, Max = (byte)8)] byte scale) => X86 && Avx2.IsSupported ? Avx2.GatherVector128(baseAddress, index, scale) : Portable.GatherVector128(baseAddress, index, scale);

    /// <summary>The same operations in portable vector code. This code gives the bits of the x86 instruction on every processor.
    /// The processors without an x86 instruction for an operation use it. The tests compare it with the x86 instructions.</summary>
    public static class Portable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Add(Vector128<float> a, Vector128<float> b) => a + b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Subtract(Vector128<float> a, Vector128<float> b) => a - b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Multiply(Vector128<float> a, Vector128<float> b) => a * b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Sqrt(Vector128<float> a) => Vector128.Sqrt(a);

        // A compare with a NaN is false, and a compare of equal values is false: the select then takes b, as the instruction does.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Max(Vector128<float> a, Vector128<float> b) => Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Min(Vector128<float> a, Vector128<float> b) => Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> MaxScalar(Vector128<float> a, Vector128<float> b) { float x = a.ToScalar(), y = b.ToScalar(); return a.WithElement(0, x > y ? x : y); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> MinScalar(Vector128<float> a, Vector128<float> b) { float x = a.ToScalar(), y = b.ToScalar(); return a.WithElement(0, x < y ? x : y); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Max(Vector128<uint> a, Vector128<uint> b) => Vector128.Max(a, b);

        // The portable conversion saturates and gives 0 for a NaN. The x86 result is 0x80000000 for a NaN and for each value
        // that is not below 2^31. A value below −2^31 saturates to 0x80000000, which is also the x86 result.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> ConvertToVector128Int32(Vector128<float> v)
            => Vector128.ConditionalSelect(Vector128.LessThan(v, Vector128.Create(2147483648f)).AsInt32(), Vector128.ConvertToInt32(Vector128.Round(v)), Vector128.Create(int.MinValue));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> ConvertToVector128Int32WithTruncation(Vector128<float> v)
            => Vector128.ConditionalSelect(Vector128.LessThan(v, Vector128.Create(2147483648f)).AsInt32(), Vector128.ConvertToInt32(v), Vector128.Create(int.MinValue));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> ConvertToVector256Int32(Vector256<float> v) => Vector256.Create(ConvertToVector128Int32(v.GetLower()), ConvertToVector128Int32(v.GetUpper()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ConvertToInt32WithTruncation(Vector128<float> v) { float f = v.ToScalar(); return f < 2147483648f ? (int)f : int.MinValue; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> Add(Vector128<int> a, Vector128<int> b) => a + b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> And(Vector128<int> a, Vector128<int> b) => a & b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> Or(Vector128<int> a, Vector128<int> b) => a | b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> Xor(Vector128<int> a, Vector128<int> b) => a ^ b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> And(Vector128<float> a, Vector128<float> b) => a & b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Or(Vector128<float> a, Vector128<float> b) => a | b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> CompareEqual(Vector128<int> a, Vector128<int> b) => Vector128.Equals(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> CompareGreaterThan(Vector128<int> a, Vector128<int> b) => Vector128.GreaterThan(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> ShiftLeftLogical(Vector128<int> v, byte count) => count > 31 ? Vector128<int>.Zero : v << count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> PackSignedSaturate(Vector128<int> a, Vector128<int> b)
        {
            var lo = Vector128.Create((int)short.MinValue); var hi = Vector128.Create((int)short.MaxValue);
            return Vector128.Narrow(Vector128.Min(Vector128.Max(a, lo), hi), Vector128.Min(Vector128.Max(b, lo), hi));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> PackUnsignedSaturate(Vector128<short> a, Vector128<short> b)
        {
            var hi = Vector128.Create((short)255);
            return Vector128.Narrow(Vector128.Min(Vector128.Max(a, Vector128<short>.Zero), hi).AsUInt16(), Vector128.Min(Vector128.Max(b, Vector128<short>.Zero), hi).AsUInt16());
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<ushort> SumAbsoluteDifferences(Vector128<byte> a, Vector128<byte> b)
        {
            var d = Vector128.Max(a, b) - Vector128.Min(a, b);
            var (lo, hi) = Vector128.Widen(d);
            return Vector128.Create((ulong)Vector128.Sum(lo), (ulong)Vector128.Sum(hi)).AsUInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> BlendVariable(Vector128<float> a, Vector128<float> b, Vector128<float> mask)
            => Vector128.ConditionalSelect(Vector128.ShiftRightArithmetic(mask.AsInt32(), 31).AsSingle(), b, a);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Shuffle(Vector128<float> a, Vector128<float> b, byte control)
        {
            if (control == 0b01_00_01_00) return Vector128.Create(a.GetLower(), b.GetLower());
            if (control == 0b11_10_11_10) return Vector128.Create(a.GetUpper(), b.GetUpper());
            return Vector128.Create(a.GetElement(control & 3), a.GetElement((control >> 2) & 3), b.GetElement((control >> 4) & 3), b.GetElement(control >> 6));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> UnpackLow(Vector128<float> a, Vector128<float> b) => Vector128.Create(a.GetElement(0), b.GetElement(0), a.GetElement(1), b.GetElement(1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> UnpackHigh(Vector128<float> a, Vector128<float> b) => Vector128.Create(a.GetElement(2), b.GetElement(2), a.GetElement(3), b.GetElement(3));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> MoveHighToLow(Vector128<float> a, Vector128<float> b) => Vector128.Create(b.GetUpper(), a.GetUpper());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Shuffle(Vector256<float> a, Vector256<float> b, byte control) => Vector256.Create(Shuffle(a.GetLower(), b.GetLower(), control), Shuffle(a.GetUpper(), b.GetUpper(), control));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> UnpackLow(Vector256<float> a, Vector256<float> b) => Vector256.Create(UnpackLow(a.GetLower(), b.GetLower()), UnpackLow(a.GetUpper(), b.GetUpper()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> UnpackHigh(Vector256<float> a, Vector256<float> b) => Vector256.Create(UnpackHigh(a.GetLower(), b.GetLower()), UnpackHigh(a.GetUpper(), b.GetUpper()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Permute(Vector256<float> v, byte control) => Vector256.Create(Shuffle(v.GetLower(), v.GetLower(), control), Shuffle(v.GetUpper(), v.GetUpper(), control));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Permute2x128(Vector256<float> a, Vector256<float> b, byte control) => Vector256.Create(Half(a, b, control), Half(a, b, (byte)(control >> 4)));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<float> Half(Vector256<float> a, Vector256<float> b, byte sel)
        {
            if ((sel & 8) != 0) return Vector128<float>.Zero;
            return (sel & 3) switch { 0 => a.GetLower(), 1 => a.GetUpper(), 2 => b.GetLower(), _ => b.GetUpper() };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe T At<T>(void* baseAddress, int index, byte scale) where T : unmanaged => Unsafe.ReadUnaligned<T>((byte*)baseAddress + (nint)index * scale);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Vector256<float> GatherVector256(float* p, Vector256<int> i, byte scale) => Vector256.Create(
            At<float>(p, i.GetElement(0), scale), At<float>(p, i.GetElement(1), scale), At<float>(p, i.GetElement(2), scale), At<float>(p, i.GetElement(3), scale),
            At<float>(p, i.GetElement(4), scale), At<float>(p, i.GetElement(5), scale), At<float>(p, i.GetElement(6), scale), At<float>(p, i.GetElement(7), scale));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Vector256<uint> GatherVector256(uint* p, Vector256<int> i, byte scale) => Vector256.Create(
            At<uint>(p, i.GetElement(0), scale), At<uint>(p, i.GetElement(1), scale), At<uint>(p, i.GetElement(2), scale), At<uint>(p, i.GetElement(3), scale),
            At<uint>(p, i.GetElement(4), scale), At<uint>(p, i.GetElement(5), scale), At<uint>(p, i.GetElement(6), scale), At<uint>(p, i.GetElement(7), scale));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Vector128<uint> GatherVector128(uint* p, Vector128<int> i, byte scale) => Vector128.Create(
            At<uint>(p, i.GetElement(0), scale), At<uint>(p, i.GetElement(1), scale), At<uint>(p, i.GetElement(2), scale), At<uint>(p, i.GetElement(3), scale));
    }

    // ---- byte average ----

    /// <summary>`pavgb`: the rounded average `(a + b + 1) >> 1` of each pair of bytes, without overflow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Average(Vector128<byte> a, Vector128<byte> b)
    {
        if (X86 && Sse2.IsSupported) return Sse2.Average(a, b);
        if (Arm && AdvSimd.IsSupported) return AdvSimd.FusedAddRoundedHalving(a, b);
        return (a | b) - ((a ^ b) >>> 1);
    }
}
