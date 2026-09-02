namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The degree-3 fast `log2`/`exp2` pair cp.dll inlines everywhere it needs a power (the sRGB transfer of
/// `FUN_1800d1da0`, `AdjustContrast&lt;vec4x32f&gt;::lambda_1` `180397300`, both passes of `FUN_180398d00`).
/// Genuinely approximate — `Log2(1.0) = −0.0042`, `Log2(2⁻) ≈ 1.0028` — so it must be replicated, never replaced
/// by `MathF.Log2`/`MathF.Pow`. Constants `0x180682490…0x180682560`. Spec `a-display-isp.md` §10.4.
/// </summary>
public static class FastLog2Exp2
{
    public static readonly float C0 = BitConverter.Int32BitsToSingle(0x3e511af3);              // 0.20420437
    public static readonly float C1 = BitConverter.Int32BitsToSingle(unchecked((int)0xbfa05375));   // −1.2525469
    public static readonly float C2 = BitConverter.Int32BitsToSingle(0x40552f75);              // 3.3310215
    public static readonly float C3 = BitConverter.Int32BitsToSingle(unchecked((int)0xc0121769));   // −2.2826788
    public static readonly float D0 = BitConverter.Int32BitsToSingle(0x3d9fcb52);              // 0.078024521
    public static readonly float D1 = BitConverter.Int32BitsToSingle(0x3e677e26);              // 0.22606716
    public static readonly float D2 = BitConverter.Int32BitsToSingle(0x3f322226);              // 0.69583356
    public static readonly float D3 = BitConverter.Int32BitsToSingle(0x3f7ffb19);              // 0.99992520

    /// <summary>`m = bitcast((bits(x) &amp; 0x007fffff) | 0x3f800000)`, `poly = ((m·C0 + C1)·m + C2)·m`,
    /// `e = (bits(x) + 0xc0800000) &gt;&gt;arith 23`, `ef = (float)e + C3` (formed FIRST), result `poly + ef`.</summary>
    public static float Log2(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        float m = BitConverter.Int32BitsToSingle((bits & 0x007fffff) | 0x3f800000);
        float poly = ((m * C0 + C1) * m + C2) * m;
        float ef = (float)((bits + unchecked((int)0xc0800000)) >> 23) + C3;
        return poly + ef;
    }

    /// <summary>`i = cvttps2dq(y) + (bits(y) &gt;&gt;arith 31)` (= floor), `f = y − (float)i`,
    /// `p = ((f·D0 + D1)·f + D2)·f + D3`, result bits = **integer** `(i &lt;&lt; 23) + bits(p)`.</summary>
    public static float Exp2(float y)
    {
        int i = (int)y + (BitConverter.SingleToInt32Bits(y) >> 31);
        float f = y - (float)i;
        float p = ((f * D0 + D1) * f + D2) * f + D3;
        return BitConverter.Int32BitsToSingle((i << 23) + BitConverter.SingleToInt32Bits(p));
    }
}
