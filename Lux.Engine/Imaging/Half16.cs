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
