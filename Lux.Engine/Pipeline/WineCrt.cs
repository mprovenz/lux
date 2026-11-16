namespace Lux.Engine.Pipeline;

/// <summary>
/// The C runtime's float and double math as Lumen gets it. Lumen and its Ceres library import these functions from
/// `api-ms-win-crt-math`, which resolves to `ucrtbase.dll`. The reference output comes from Lumen on Wine 10.0, so that CRT is Wine's
/// ucrtbase. Each function here gives the same bits as that CRT; the comment of each function states the verified input domain. Use
/// these, not <c>Math</c> or <c>MathF</c>, wherever Lumen calls the CRT: the math libraries of the operating systems give different
/// last bits, and the output must not depend on the platform.
/// <para>Wine's ucrtbase uses musl's argument reduction for sinf / cosf, but plain Taylor kernels. For some inputs its results are one
/// ulp apart from musl's, so <c>MuslMath.Sinf/Cosf</c> do not give the same bits.</para>
/// </summary>
public static partial class WineCrt
{
    // ---------------------------------------------------------------------------------------------------------------------------------
    // sinf / cosf — Wine's ucrtbase kernels; argument reduction as musl (__rem_pio2f, medium path)
    // ---------------------------------------------------------------------------------------------------------------------------------
    const double Pio2 = 1.5707963267948966, Pi = 3.141592653589793, ThreePio2 = 4.71238898038469, TwoPi = 6.283185307179586;
    const double Pio2Hi = 1.57079631090164184570e+00, Pio2Lo = 1.58932547735281966916e-08, InvPio2 = 6.36619772367581382433e-01;
    const double ToInt = 6755399441055744.0;   // 1.5/DBL_EPSILON

    /// <summary>Kernel 0x8fe70: |x| &lt; 0.007817583158612251 → <c>(z·(−1/6) + 1)·x</c>; else with s = x·z, w = z·z:
    /// <c>(w·s)·(2.7557319223985893e-06·z − 0.0001984126984126984) + ((z·0.008333333333333333 − 0.16666666666666666)·s + x)</c>.</summary>
    static float Sindf(double x)
    {
        double z = x * x;
        if (x > -0.007817583158612251 && 0.007817583158612251 > x) return (float)((z * -0.16666666666666666 + 1.0) * x);
        double s = x * z, w = z * z;
        double r = 2.7557319223985893e-06 * z - 0.0001984126984126984, q = z * 0.008333333333333333 - 0.16666666666666666;
        return (float)((w * s) * r + (q * s + x));
    }

    /// <summary>Kernel 0x8e9b0: |x| &lt; 0.007816314697265625 → <c>z·(−0.5) + 1</c>; else Horner in z with the coefficients
    /// −2.755731922398589e-07, 2.48015873015873e-05, −0.001388888888888889, 0.041666666666666664, −0.5, 1.</summary>
    static float Cosdf(double x)
    {
        double z = x * x;
        if (x > -0.007816314697265625 && 0.007816314697265625 > x) return (float)(z * -0.5 + 1.0);
        double p = -2.755731922398589e-07 * z + 2.48015873015873e-05;
        p = p * z - 0.001388888888888889; p = p * z + 0.041666666666666664; p = p * z - 0.5;
        return (float)(p * z + 1.0);
    }

    /// <summary>musl `__rem_pio2f`, medium path (|x| &lt; 2^28·π/2): n = rint(x·2/π), y = x − n·pio2_1 − n·pio2_1t.</summary>
    static int RemPio2f(float x, out double y)
    {
        double fn = (double)x * InvPio2 + ToInt - ToInt;
        y = x - fn * Pio2Hi - fn * Pio2Lo;
        return (int)fn;
    }

    const uint TrigLimit = 0x4dc90fdb;   // |x| ~< 2^28·π/2: beyond it the CRT takes __rem_pio2_large, which is not ported (no caller comes near)

    /// <summary>`cosf`. Verified domain: every float with |x| &lt; 2^28·π/2 (bits &lt; 0x4dc90fdb), ±inf and every NaN (all 2^32 inputs
    /// minus the large-argument range, which throws).</summary>
    public static float Cosf(float x)
    {
        uint ix = BitConverter.SingleToUInt32Bits(x); bool sign = (ix >> 31) != 0; ix &= 0x7fffffff;
        if (ix <= 0x3f490fda) return ix <= 0x397fffff ? 1.0f : Cosdf(x);
        if (ix <= 0x407b53d1)
        {
            double d = x;
            if (ix > 0x4016cbe3) return -Cosdf(sign ? d + Pi : d - Pi);
            return sign ? Sindf(d + Pio2) : Sindf(Pio2 - d);
        }
        if (ix <= 0x40e231d5)
        {
            if (ix > 0x40afeddf) { double d = x; return Cosdf(sign ? d + TwoPi : d - TwoPi); }
            return Sindf((double)(sign ? -x : x) - ThreePio2);
        }
        if (ix >= 0x7f800000) return x - x;
        if (ix >= TrigLimit) throw new NotSupportedException("cosf: arguments beyond 2^28·π/2 are not ported");
        int n = RemPio2f(x, out double y);
        switch (n & 3) { case 0: return Cosdf(y); case 1: return Sindf(-y); case 2: return -Cosdf(y); default: return Sindf(y); }
    }

    /// <summary>`sinf`. Verified domain: as <see cref="Cosf"/>.</summary>
    public static float Sinf(float x)
    {
        uint ix = BitConverter.SingleToUInt32Bits(x); bool sign = (ix >> 31) != 0; ix &= 0x7fffffff;
        if (ix <= 0x3f490fda) return ix < 0x39800000 ? x : Sindf(x);
        if (ix <= 0x407b53d1)
        {
            double d = x;
            if (ix <= 0x4016cbe3) return sign ? -Cosdf(d + Pio2) : Cosdf(d - Pio2);
            return Sindf(sign ? -(d + Pi) : -(d - Pi));
        }
        if (ix <= 0x40e231d5)
        {
            double d = x;
            if (ix <= 0x40afeddf) return sign ? Cosdf(d + ThreePio2) : -Cosdf(d - ThreePio2);
            return Sindf(sign ? d + TwoPi : d - TwoPi);
        }
        if (ix >= 0x7f800000) return x - x;
        if (ix >= TrigLimit) throw new NotSupportedException("sinf: arguments beyond 2^28·π/2 are not ported");
        int n = RemPio2f(x, out double y);
        switch (n & 3) { case 0: return Sindf(y); case 1: return Cosdf(y); case 2: return Sindf(-y); default: return -Cosdf(y); }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // log2f (−inf gives the CRT's NaN, not −inf; no caller passes −inf)
    // ---------------------------------------------------------------------------------------------------------------------------------

    // musl __log2f_data (LOG2F_TABLE_BITS = 4, OFF = 0x3f330000): {invc, logc} and the degree-4 polynomial
    static readonly ulong[] Log2Invc = { 0x3ff661ec79f8f3be, 0x3ff571ed4aaf883d, 0x3ff49539f0f010b0, 0x3ff3c995b0b80385, 0x3ff30d190c8864a5, 0x3ff25e227b0b8ea0, 0x3ff1bb4a4a1a343f, 0x3ff12358f08ae5ba, 0x3ff0953f419900a7, 0x3ff0000000000000, 0x3fee608cfd9a47ac, 0x3feca4b31f026aa0, 0x3feb2036576afce6, 0x3fe9c2d163a1aa2d, 0x3fe886e6037841ed, 0x3fe767dcf5534862 };
    static readonly ulong[] Log2Logc = { 0xbfdefec65b963019, 0xbfdb0b6832d4fca4, 0xbfd7418b0a1fb77b, 0xbfd39de91a6dcf7b, 0xbfd01d9bf3f2b631, 0xbfc97c1d1b3b7af0, 0xbfc2f9e393af3c9f, 0xbfb960cbbf788d5c, 0xbfaa6f9db6475fce, 0x0000000000000000, 0x3fb338ca9f24f53d, 0x3fc476a9543891ba, 0x3fce840b4ac4e4d2, 0x3fd40645f0c6651c, 0x3fd88e9c2c1b9ff8, 0x3fdce0a44eb17bcc };
    static readonly double L2A0 = BitConverter.UInt64BitsToDouble(0xbfd712b6f70a7e4d), L2A1 = BitConverter.UInt64BitsToDouble(0x3fdecabf496832e0),
        L2A2 = BitConverter.UInt64BitsToDouble(0xbfe715479ffae3de), L2A3 = BitConverter.UInt64BitsToDouble(0x3ff715475f35c8b8);

    /// <summary>`log2f` (musl's, with the table `__log2f_data` of ucrtbase.dll). Verified domain:
    /// all 2^32 floats.</summary>
    public static float Log2f(float x)
    {
        uint ix = BitConverter.SingleToUInt32Bits(x);
        if (ix == 0x3f800000) return 0f;
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000)
        {
            if (ix * 2 == 0) return float.NegativeInfinity;
            if (ix == 0x7f800000) return x;
            if (ix * 2 > 0xff000000) return x;                            // NaN: returned unchanged (payload and signaling bit kept)
            if ((ix & 0x80000000) != 0) return float.NaN;                 // negative, −inf included: the default NaN 0xffc00000
            ix = BitConverter.SingleToUInt32Bits(x * 8388608.0f);
            ix -= 23u << 23;
        }
        uint tmp = ix - 0x3f330000;
        int i = (int)((tmp >> (23 - 4)) % 16);
        uint top = tmp & 0xff800000;
        uint iz = ix - top;
        int k = (int)tmp >> 23;
        double invc = BitConverter.UInt64BitsToDouble(Log2Invc[i]), logc = BitConverter.UInt64BitsToDouble(Log2Logc[i]);
        double z = (double)BitConverter.UInt32BitsToSingle(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k;
        double r2 = r * r;
        double y = L2A1 * r + L2A2;
        y = L2A0 * r2 + y;
        double p = L2A3 * r + y0;
        y = y * r2 + p;
        return (float)y;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // roundf
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`roundf` (musl's: half away from zero). Verified domain: all 2^32 floats.</summary>
    public static float Roundf(float x)
    {
        const float toint = 8388608f;   // 1/FLT_EPSILON
        uint i = BitConverter.SingleToUInt32Bits(x); int e = (int)(i >> 23 & 0xff);
        if (e >= 0x7f + 23) return x;
        bool neg = (i >> 31) != 0;
        if (neg) x = -x;
        if (e < 0x7f - 1) return 0f * BitConverter.UInt32BitsToSingle(i);
        float y = x + toint - toint - x;
        if (y > 0.5f) y = y + x - 1f;
        else if (y <= -0.5f) y = y + x + 1f;
        else y = y + x;
        return neg ? -y : y;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // acosf — musl acosf.c (FreeBSD e_acosf.c: Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved. Developed at SunPro,
    // a Sun Microsystems, Inc. business. Permission to use, copy, modify, and distribute this software is freely granted, provided
    // that this notice is preserved.)
    // ---------------------------------------------------------------------------------------------------------------------------------
    const float AcfPio2Hi = 1.5707962513e+00f, AcfPio2Lo = 7.5497894159e-08f;
    const float AcfP0 = 1.66666672e-01f, AcfP1 = -5.11644611e-02f, AcfP2 = -1.21124933e-02f, AcfP3 = -3.58742251e-03f, AcfQ1 = -7.56982703e-01f;

    static float AcosfR(float z)
    {
        float p = z * (AcfP0 + z * (AcfP1 + z * (AcfP2 + z * AcfP3)));
        float q = 1.0f + z * AcfQ1;
        return p / q;
    }

    /// <summary>`acosf`. Verified domain: all 2^32 floats. An argument with |x| &gt; 1 gives the NaN 0xffc00000; a NaN argument comes
    /// back unchanged.</summary>
    public static float Acosf(float x)
    {
        float z, w, s, c, df;
        uint hx = BitConverter.SingleToUInt32Bits(x);
        uint ix = hx & 0x7fffffff;
        if (ix >= 0x3f800000)                               // |x| >= 1 or NaN
        {
            if (ix == 0x3f800000) return (hx >> 31) != 0 ? 3.14159274f : 0f;
            if (float.IsNaN(x)) return x;
            return BitConverter.UInt32BitsToSingle(0xffc00000);
        }
        if (ix < 0x3f000000)                                // |x| < 0.5
        {
            if (ix <= 0x32800000) return 1.57079637f;       // |x| < 2^−26
            return AcfPio2Hi - (x - (AcfPio2Lo - x * AcosfR(x * x)));
        }
        if ((hx >> 31) != 0)                                // x < −0.5
        {
            z = (1 + x) * 0.5f;
            s = MathF.Sqrt(z);
            return 2 * (AcfPio2Hi - (s + (AcosfR(z) * s - AcfPio2Lo)));
        }
        z = (1 - x) * 0.5f;                                 // x > 0.5
        s = MathF.Sqrt(z);
        df = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(s) & 0xfffff000);
        c = (z - df * df) / (s + df);
        w = AcosfR(z) * s + c;
        return 2 * (df + w);
    }

    // ---- powf (musl / ARM optimized-routines, as Wine's msvcrt powf; TOINT_INTRINSICS = 0, POWF_SCALE = 1) ----
    static readonly double[] PowInvc = { BitConverter.Int64BitsToDouble(unchecked((long)0x3ff661ec79f8f3be)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff571ed4aaf883d)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff49539f0f010b0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff3c995b0b80385)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff30d190c8864a5)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff25e227b0b8ea0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff1bb4a4a1a343f)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff12358f08ae5ba)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0953f419900a7)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0000000000000)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fee608cfd9a47ac)), BitConverter.Int64BitsToDouble(unchecked((long)0x3feca4b31f026aa0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3feb2036576afce6)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe9c2d163a1aa2d)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe886e6037841ed)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe767dcf5534862)) };
    static readonly double[] PowLogc = { BitConverter.Int64BitsToDouble(unchecked((long)0xbfdefec65b963019)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfdb0b6832d4fca4)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd7418b0a1fb77b)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd39de91a6dcf7b)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd01d9bf3f2b631)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfc97c1d1b3b7af0)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfc2f9e393af3c9f)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfb960cbbf788d5c)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfaa6f9db6475fce)), 0.0, BitConverter.Int64BitsToDouble(unchecked((long)0x3fb338ca9f24f53d)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fc476a9543891ba)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fce840b4ac4e4d2)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fd40645f0c6651c)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fd88e9c2c1b9ff8)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fdce0a44eb17bcc)) };
    static readonly double[] PowPoly = { BitConverter.Int64BitsToDouble(unchecked((long)0x3fd27616c9496e0b)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd71969a075c67a)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fdec70a6ca7badd)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfe7154748bef6c8)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff71547652ab82b)) };
    static readonly ulong[] Exp2Tab = { 0x3ff0000000000000, 0x3fefd9b0d3158574, 0x3fefb5586cf9890f, 0x3fef9301d0125b51, 0x3fef72b83c7d517b, 0x3fef54873168b9aa, 0x3fef387a6e756238, 0x3fef1e9df51fdee1, 0x3fef06fe0a31b715, 0x3feef1a7373aa9cb, 0x3feedea64c123422, 0x3feece086061892d, 0x3feebfdad5362a27, 0x3feeb42b569d4f82, 0x3feeab07dd485429, 0x3feea47eb03a5585, 0x3feea09e667f3bcd, 0x3fee9f75e8ec5f74, 0x3feea11473eb0187, 0x3feea589994cce13, 0x3feeace5422aa0db, 0x3feeb737b0cdc5e5, 0x3feec49182a3f090, 0x3feed503b23e255d, 0x3feee89f995ad3ad, 0x3feeff76f2fb5e47, 0x3fef199bdd85529c, 0x3fef3720dcef9069, 0x3fef5818dcfba487, 0x3fef7c97337b9b5f, 0x3fefa4afa2a490da, 0x3fefd0765b6e4540 };
    static readonly double[] Exp2Poly = { BitConverter.Int64BitsToDouble(unchecked((long)0x3fac6af84b912394)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fcebfce50fac4f3)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe62e42ff0c52d6)) };
    static readonly double Exp2ShiftScaled = BitConverter.Int64BitsToDouble(unchecked((long)0x4338000000000000)) / 32.0;

    static double Log2Inline(uint ix)
    {
        const uint OFF = 0x3f330000;
        uint tmp = ix - OFF;
        int i = (int)((tmp >> (23 - 4)) % 16);
        uint top = tmp & 0xff800000;
        uint iz = ix - top;
        int k = (int)top >> 23;
        double invc = PowInvc[i], logc = PowLogc[i];
        double z = (double)BitConverter.UInt32BitsToSingle(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k;
        double r2 = r * r;
        double y = PowPoly[0] * r + PowPoly[1];
        double p = PowPoly[2] * r + PowPoly[3];
        double r4 = r2 * r2;
        double q = PowPoly[4] * r + y0;
        q = p * r2 + q;
        y = y * r4 + q;
        return y;
    }

    static float Exp2Inline(double xd, uint signBias)
    {
        double kd = xd + Exp2ShiftScaled;
        ulong ki = BitConverter.DoubleToUInt64Bits(kd);
        kd -= Exp2ShiftScaled;
        double r = xd - kd;
        ulong t = Exp2Tab[ki % 32];
        ulong ski = ki + signBias;
        t += ski << (52 - 5);
        double s = BitConverter.UInt64BitsToDouble(t);
        double z = Exp2Poly[0] * r + Exp2Poly[1];
        double r2 = r * r;
        double y = Exp2Poly[2] * r + 1;
        y = z * r2 + y;
        y = y * s;
        return (float)y;
    }

    static int CheckInt(uint iy)
    {
        int e = (int)(iy >> 23 & 0xff);
        if (e < 0x7f) return 0;
        if (e > 0x7f + 23) return 2;
        if ((iy & ((1u << (0x7f + 23 - e)) - 1)) != 0) return 0;
        if ((iy & (1u << (0x7f + 23 - e))) != 0) return 1;
        return 2;
    }
    static bool ZeroInfNan(uint ix) => 2 * ix - 1 >= 2u * 0x7f800000 - 1;

    /// <summary>`powf` (musl / ARM optimized-routines, as Wine's ucrtbase). Verified domain: every x for y = 1/2.2 (0x3ee8ba2e, the stereo
    /// image's gamma), and the small fixed input sets of the other callers.</summary>
    public static float Powf(float x, float y)
    {
        uint signBias = 0;
        uint ix = BitConverter.SingleToUInt32Bits(x), iy = BitConverter.SingleToUInt32Bits(y);
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000 || ZeroInfNan(iy))
        {
            if (ZeroInfNan(iy))
            {
                if (2 * iy == 0) return 1.0f;
                if (ix == 0x3f800000) return 1.0f;
                if (2 * ix > 2u * 0x7f800000 || 2 * iy > 2u * 0x7f800000) return x + y;
                if (2 * ix == 2 * 0x3f800000) return 1.0f;
                if ((2 * ix < 2 * 0x3f800000) == ((iy & 0x80000000) == 0)) return 0.0f;
                return y * y;
            }
            if (ZeroInfNan(ix))
            {
                float x2 = x * x;
                if ((ix & 0x80000000) != 0 && CheckInt(iy) == 1) x2 = -x2;
                return (iy & 0x80000000) != 0 ? 1 / x2 : x2;
            }
            if ((ix & 0x80000000) != 0)
            {
                int yint = CheckInt(iy);
                if (yint == 0) return float.NaN;
                if (yint == 1) signBias = 1u << (5 + 11);
                ix &= 0x7fffffff;
            }
            if (ix < 0x00800000)
            {
                ix = BitConverter.SingleToUInt32Bits(x * 8388608.0f);
                ix &= 0x7fffffff;
                ix -= 23u << 23;
            }
        }
        double logx = Log2Inline(ix);
        double ylogx = y * logx;
        if (((BitConverter.DoubleToUInt64Bits(ylogx) >> 47) & 0xffff) >= (BitConverter.DoubleToUInt64Bits(126.0) >> 47))
        {
            if (ylogx > BitConverter.Int64BitsToDouble(unchecked((long)0x405fffffffd1d571))) return signBias != 0 ? float.NegativeInfinity : float.PositiveInfinity;
            if (ylogx <= -150.0) return signBias != 0 ? -0.0f : 0.0f;
        }
        return Exp2Inline(ylogx, signBias);
    }

    // ---- expf / exp2f / logf (musl, as Wine's msvcrt; the CRT calls of Lumen's display ISP) ----
    // __exp2f_data: tab = Exp2Tab, poly = Exp2Poly, shift = 0x1.8p+52, shift_scaled = shift/N, invln2_scaled = 0x1.71547652b82fep+0*N,
    // poly_scaled[k] = poly[k]/N^(3-k) with N = 32 (exact — all divisions by powers of two).
    static readonly double Exp2Shift = BitConverter.Int64BitsToDouble(unchecked((long)0x4338000000000000));   // 0x1.8p+52
    static readonly double InvLn2N = BitConverter.Int64BitsToDouble(unchecked((long)0x3ff71547652b82fe)) * 32.0;
    static readonly double[] ExpPolyScaled = { Exp2Poly[0] / 32768.0, Exp2Poly[1] / 1024.0, Exp2Poly[2] / 32.0 };
    static uint Top12(float x) => BitConverter.SingleToUInt32Bits(x) >> 20;

    /// <summary>`exp2f` — the `2^ev` pre-gain of `TMO_ACR::process`. Verified domain: all 2^32 floats.</summary>
    public static float Exp2f(float x)
    {
        uint abstop = Top12(x) & 0x7ff;
        if (abstop >= (Top12(128.0f) & 0x7ff))
        {
            if (BitConverter.SingleToUInt32Bits(x) == BitConverter.SingleToUInt32Bits(float.NegativeInfinity)) return 0.0f;
            if (abstop >= (Top12(float.PositiveInfinity) & 0x7ff)) return x + x;
            if (x > 0.0f) return float.PositiveInfinity;      // __math_oflowf
            if (x <= -150.0f) return 0.0f;                    // __math_uflowf
        }
        return Exp2Inline((double)x, 0);
    }

    /// <summary>`expf` — the scalar tail of Lumen's exp pass and the tone-curve Gaussian of
    /// `CreateAndBlendLaplacianPyramids`. Verified domain: all 2^32 floats.</summary>
    public static float Expf(float x)
    {
        double xd = (double)x;
        uint abstop = Top12(x) & 0x7ff;
        if (abstop >= (Top12(88.0f) & 0x7ff))
        {
            if (BitConverter.SingleToUInt32Bits(x) == BitConverter.SingleToUInt32Bits(float.NegativeInfinity)) return 0.0f;
            if (abstop >= (Top12(float.PositiveInfinity) & 0x7ff)) return x + x;
            if (x > 88.72283935546875f) return float.PositiveInfinity;      // 0x1.62e42ep6f
            if (x < -103.97207641601562f) return 0.0f;                      // -0x1.9fe368p6f
        }
        double z = InvLn2N * xd;
        double kd = z + Exp2Shift;
        ulong ki = BitConverter.DoubleToUInt64Bits(kd);
        kd -= Exp2Shift;
        double r = z - kd;
        ulong t = Exp2Tab[ki % 32];
        t += ki << (52 - 5);
        double s = BitConverter.UInt64BitsToDouble(t);
        double zz = ExpPolyScaled[0] * r + ExpPolyScaled[1];
        double r2 = r * r;
        double y = ExpPolyScaled[2] * r + 1;
        y = zz * r2 + y;
        y = y * s;
        return (float)y;
    }

    // __logf_data (LOGF_TABLE_BITS = 4, OFF = 0x3f330000)
    static readonly double[] LogfInvc = { BitConverter.Int64BitsToDouble(unchecked((long)0x3ff661ec79f8f3be)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff571ed4aaf883d)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff49539f0f010b0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff3c995b0b80385)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff30d190c8864a5)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff25e227b0b8ea0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff1bb4a4a1a343f)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff12358f08ae5ba)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0953f419900a7)), BitConverter.Int64BitsToDouble(unchecked((long)0x3ff0000000000000)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fee608cfd9a47ac)), BitConverter.Int64BitsToDouble(unchecked((long)0x3feca4b31f026aa0)), BitConverter.Int64BitsToDouble(unchecked((long)0x3feb2036576afce6)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe9c2d163a1aa2d)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe886e6037841ed)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fe767dcf5534862)) };
    static readonly double[] LogfLogc = { BitConverter.Int64BitsToDouble(unchecked((long)0xbfd57bf7808caade)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd2bef0a7c06ddb)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfd01eae7f513a67)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfcb31d8a68224e9)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfc6574f0ac07758)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfc1aa2bc79c8100)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfba4e76ce8c0e5e)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfb1973c5a611ccc)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfa252f438e10c1e)), 0.0, BitConverter.Int64BitsToDouble(unchecked((long)0x3faaa5aa5df25984)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fbc5e53aa362eb4)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fc526e57720db08)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fcbc2860d224770)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fd1058bc8a07ee1)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fd4043057b6ee09)) };
    static readonly double LogfLn2 = BitConverter.Int64BitsToDouble(unchecked((long)0x3fe62e42fefa39ef));
    static readonly double[] LogfPoly = { BitConverter.Int64BitsToDouble(unchecked((long)0xbfd00ea348b88334)), BitConverter.Int64BitsToDouble(unchecked((long)0x3fd5575b0be00b6a)), BitConverter.Int64BitsToDouble(unchecked((long)0xbfdffffef20a4123)) };

    /// <summary>`logf` — the scalar tail of Lumen's log pass. Verified domain: all 2^32 floats (musl's logf,
    /// except that a NaN argument comes back unchanged).</summary>
    public static float Logf(float x)
    {
        uint ix = BitConverter.SingleToUInt32Bits(x);
        if (ix == 0x3f800000) return 0f;
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000)
        {
            if (ix * 2 == 0) return float.NegativeInfinity;
            if (ix == 0x7f800000) return x;
            if (ix * 2 > 0xff000000) return x;                             // NaN: returned unchanged, payload and signaling bit kept (Wine's ucrtbase)
            if ((ix & 0x80000000) != 0) return float.NaN;                 // negative (−inf included): the default NaN 0xffc00000
            ix = BitConverter.SingleToUInt32Bits(x * 8388608.0f);
            ix -= 23u << 23;
        }
        uint tmp = ix - 0x3f330000;
        int i = (int)((tmp >> (23 - 4)) % 16);
        int k = (int)tmp >> 23;
        uint iz = ix - (tmp & (0x1ffu << 23));
        double invc = LogfInvc[i], logc = LogfLogc[i];
        double z = (double)BitConverter.UInt32BitsToSingle(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k * LogfLn2;
        double r2 = r * r;
        double y = LogfPoly[1] * r + LogfPoly[2];
        y = LogfPoly[0] * r2 + y;
        y = y * r2 + (y0 + r);
        return (float)y;
    }
}
