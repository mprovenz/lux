using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Lux.Engine.Imaging;

/// <summary>Lumen's software Float16 conversions: **round-toward-zero** to half (finite overflow clamps to 65504, subnormals
/// truncated), exact half→float.</summary>
public static class Half16
{
    public static ushort FromFloat(float f)
    {
        uint u = (uint)BitConverter.SingleToInt32Bits(f);
        uint sign = (u >> 16) & 0x8000u, a = u & 0x7fffffffu;
        if (a <= 0x387fffffu)
        {   // |f| < 2^-14: subnormal half = trunc(|f| · 2^24)
            float m = BitConverter.Int32BitsToSingle((int)a) * 16777216.0f;
            return (ushort)(sign | (uint)(int)m);
        }
        uint cap = a >= 0x7f800000u ? 0x47800000u : 0x477fe000u;
        uint ap = a < cap ? a : cap;
        return (ushort)(sign | (((ap + 0x08000000u) >> 13) & 0xffffu));
    }

    /// <summary><see cref="FromFloat(float)"/> on four lanes. The steps are the integer steps of the scalar code. The multiply by 2^24
    /// is exact, and the truncated value is below 1024 in each lane that uses it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> FromFloat(Vector128<float> f)
    {
        var u = f.AsUInt32();
        var sign = (u >> 16) & Vector128.Create(0x8000u); var a = (u & Vector128.Create(0x7fffffffu)).AsInt32();
        var small = Vector128.LessThanOrEqual(a, Vector128.Create(0x387fffff));
        var m = Simd.ConvertToVector128Int32WithTruncation(a.AsSingle() * Vector128.Create(16777216.0f));
        var cap = Vector128.ConditionalSelect(Vector128.GreaterThan(a, Vector128.Create(0x7f7fffff)), Vector128.Create(0x47800000), Vector128.Create(0x477fe000));
        var big = (Vector128.Min(a, cap) + Vector128.Create(0x08000000)) >>> 13;
        return sign | (Vector128.ConditionalSelect(small, m, big).AsUInt32() & Vector128.Create(0xffffu));
    }

    /// <summary>`dst[i] = FromFloat(src[i])` for each element of <paramref name="src"/>.</summary>
    public static void FromFloat(ReadOnlySpan<float> src, Span<ushort> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("the destination is too short", nameof(dst));
        int i = 0, n = src.Length;
        if (Vector128.IsHardwareAccelerated)
        {
            ref float s = ref MemoryMarshal.GetReference(src); ref ushort d = ref MemoryMarshal.GetReference(dst);
            for (; i + 8 <= n; i += 8)
                Vector128.Narrow(FromFloat(Vector128.LoadUnsafe(ref s, (nuint)i)), FromFloat(Vector128.LoadUnsafe(ref s, (nuint)i + 4))).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) dst[i] = FromFloat(src[i]);
    }

    /// <summary>`pixels` float RGBA pixels, each lane times the same lane of <paramref name="mul"/>, to RGB half triples. Lane 3 is not
    /// stored.</summary>
    public static void FromFloatRgb(ReadOnlySpan<float> src, Vector128<float> mul, Span<ushort> dst, int pixels)
    {
        if (pixels < 0 || src.Length < (long)pixels * 4 || dst.Length < (long)pixels * 3) throw new ArgumentException("a span is too short for the pixel count");
        float m0 = mul.GetElement(0), m1 = mul.GetElement(1), m2 = mul.GetElement(2);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref float s = ref MemoryMarshal.GetReference(src); ref ushort d = ref MemoryMarshal.GetReference(dst);
            for (; i < pixels; i++)
            {
                var h = FromFloat(Vector128.LoadUnsafe(ref s, (nuint)(4 * i)) * mul);
                Unsafe.Add(ref d, 3 * i) = (ushort)h.GetElement(0); Unsafe.Add(ref d, 3 * i + 1) = (ushort)h.GetElement(1); Unsafe.Add(ref d, 3 * i + 2) = (ushort)h.GetElement(2);
            }
        }
        for (; i < pixels; i++)
        {
            dst[3 * i] = FromFloat(src[4 * i] * m0); dst[3 * i + 1] = FromFloat(src[4 * i + 1] * m1); dst[3 * i + 2] = FromFloat(src[4 * i + 2] * m2);
        }
    }

    /// <summary>`dst[i] = ToFloat(src[i])` for each element of <paramref name="src"/>.</summary>
    public static void ToFloat(ReadOnlySpan<ushort> src, Span<float> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("the destination is too short", nameof(dst));
        ref ushort s = ref MemoryMarshal.GetReference(src); ref float d = ref MemoryMarshal.GetReference(dst);
        ref float tab = ref MemoryMarshal.GetArrayDataReference(Table);
        for (int i = 0; i < src.Length; i++) Unsafe.Add(ref d, i) = Unsafe.Add(ref tab, Unsafe.Add(ref s, i));
    }

    /// <summary>`pixels` RGB half triples to float RGBA pixels with alpha 1.</summary>
    public static void ToFloatRgb1(ReadOnlySpan<ushort> src, Span<float> dst, int pixels)
    {
        if (pixels < 0 || src.Length < (long)pixels * 3 || dst.Length < (long)pixels * 4) throw new ArgumentException("a span is too short for the pixel count");
        ref ushort s = ref MemoryMarshal.GetReference(src); ref float d = ref MemoryMarshal.GetReference(dst);
        ref float tab = ref MemoryMarshal.GetArrayDataReference(Table);
        for (int i = 0; i < pixels; i++)
        {
            Unsafe.Add(ref d, 4 * i) = Unsafe.Add(ref tab, Unsafe.Add(ref s, 3 * i)); Unsafe.Add(ref d, 4 * i + 1) = Unsafe.Add(ref tab, Unsafe.Add(ref s, 3 * i + 1));
            Unsafe.Add(ref d, 4 * i + 2) = Unsafe.Add(ref tab, Unsafe.Add(ref s, 3 * i + 2)); Unsafe.Add(ref d, 4 * i + 3) = 1.0f;
        }
    }

    /// <summary>Half → float by table: the 65 536 results of <see cref="ToFloatCompute"/>, so the same bits.</summary>
    public static float ToFloat(ushort h) => Table[h];

    static readonly float[] Table = BuildTable();
    static float[] BuildTable() { var t = new float[65536]; for (int i = 0; i < 65536; i++) t[i] = ToFloatCompute((ushort)i); return t; }

    /// <summary>Exact half → float.</summary>
    public static float ToFloatCompute(ushort h)
    {
        uint a = (uint)h & 0x7fffu, sign = ((uint)h << 16) & 0x80000000u;
        uint bits;
        if (a <= 0x3ffu) return BitConverter.Int32BitsToSingle((int)(BitConverter.SingleToInt32Bits((float)a * 5.9604645e-8f) | (int)sign));
        bits = a > 0x7bffu ? ((a + 0x38000u) << 13) : ((a + 0x1c000u) << 13);
        return BitConverter.Int32BitsToSingle((int)(bits | sign));
    }
}
