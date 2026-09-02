namespace Lux.Engine.Pipeline.Registration;

/// <summary>musl `sinf` (the implementation behind Wine's msvcrt/UCRT `sinf`, which is what cp.dll calls when run under Wine):
/// double-precision kernel polynomials `__sindf`/`__cosdf` with the small-argument fast paths and `__rem_pio2f`.</summary>
public static class MuslMath
{
    const double S1 = -1.6666666641626524e-01, S2 = 8.3333293858894632e-03, S3 = -1.9839334836096632e-04, S4 = 2.7183114939898219e-06;
    const double C0 = -4.9999999725103100e-01, C1 = 4.1666623323739063e-02, C2 = -1.3886883727633e-03, C3 = 2.4390448796277409e-05;
    const double Pio2 = 1.57079632679489661923;
    const double Pio2Hi = 1.57079631090164184570e+00, Pio2Lo = 1.58932547735281966916e-08, InvPio2 = 6.36619772367581382433e-01;
    const double ToInt = 6755399441055744.0;   // 1.5/DBL_EPSILON

    static float Sindf(double x) { double z = x * x, w = z * z, r = S3 + z * S4, s = z * x; return (float)((x + s * (S1 + z * S2)) + s * w * r); }
    static float Cosdf(double x) { double z = x * x, w = z * z, r = C2 + z * C3; return (float)(((1.0 + z * C0) + w * C1) + (w * z) * r); }

    static int RemPio2f(float x, out double y)
    {
        uint ix = (uint)BitConverter.SingleToInt32Bits(x) & 0x7fffffff;
        if (ix < 0x4dc90fdb)   // |x| ~< 2^28·π/2 : medium size
        {
            double fn = (double)x * InvPio2 + ToInt - ToInt;
            int n = (int)fn;
            y = x - fn * Pio2Hi - fn * Pio2Lo;
            return n;
        }
        throw new NotSupportedException("rem_pio2f large arguments not needed here");
    }

    public static float Sinf(float x)
    {
        uint ix = (uint)BitConverter.SingleToInt32Bits(x); bool sign = (ix >> 31) != 0; ix &= 0x7fffffff;
        if (ix <= 0x3f490fda)   // |x| ~<= π/4
        {
            if (ix < 0x39800000) return x;   // |x| < 2^-12
            return Sindf(x);
        }
        if (ix <= 0x407b53d1)   // |x| ~<= 5π/4
        {
            if (ix <= 0x4016cbe3)   // |x| ~<= 3π/4
                return sign ? -Cosdf(x + Pio2) : Cosdf(x - Pio2);
            return Sindf(sign ? -(x + 2 * Pio2) : -(x - 2 * Pio2));
        }
        if (ix <= 0x40e231d5)   // |x| ~<= 9π/4
        {
            if (ix <= 0x40afeddf)   // |x| ~<= 7π/4
                return sign ? Cosdf(x + 3 * Pio2) : -Cosdf(x - 3 * Pio2);
            return Sindf(sign ? x + 4 * Pio2 : x - 4 * Pio2);
        }
        if (ix >= 0x7f800000) return x - x;
        int n = RemPio2f(x, out double yy);
        switch (n & 3) { case 0: return Sindf(yy); case 1: return Cosdf(yy); case 2: return Sindf(-yy); default: return -Cosdf(yy); }
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

    /// <summary>musl `powf` (Wine msvcrt), bit-exact port.</summary>
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

    // ---- expf / exp2f / logf (musl, as Wine's msvcrt; the CRT calls cp.dll's display ISP makes) ----
    // __exp2f_data: tab = Exp2Tab, poly = Exp2Poly, shift = 0x1.8p+52, shift_scaled = shift/N, invln2_scaled = 0x1.71547652b82fep+0*N,
    // poly_scaled[k] = poly[k]/N^(3-k) with N = 32 (exact — all divisions by powers of two).
    static readonly double Exp2Shift = BitConverter.Int64BitsToDouble(unchecked((long)0x4338000000000000));   // 0x1.8p+52
    static readonly double InvLn2N = BitConverter.Int64BitsToDouble(unchecked((long)0x3ff71547652b82fe)) * 32.0;
    static readonly double[] ExpPolyScaled = { Exp2Poly[0] / 32768.0, Exp2Poly[1] / 1024.0, Exp2Poly[2] / 32.0 };
    static uint Top12(float x) => BitConverter.SingleToUInt32Bits(x) >> 20;

    /// <summary>musl `exp2f` (UCRT/Wine `exp2f`, IAT 0x18067e4b0) — the `2^ev` pre-gain of `TMO_ACR::process`.</summary>
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

    /// <summary>musl `expf` (UCRT/Wine `expf`, IAT 0x18067e458) — the scalar tail of `FUN_180398d00`'s exp pass and the
    /// tone-curve Gaussian of `CreateAndBlendLaplacianPyramids`.</summary>
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

    /// <summary>musl `logf` (UCRT/Wine `logf`, IAT 0x18067e450) — the scalar tail of `FUN_180398d00`'s log pass.</summary>
    public static float Logf(float x)
    {
        uint ix = BitConverter.SingleToUInt32Bits(x);
        if (ix == 0x3f800000) return 0f;
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000)
        {
            if (ix * 2 == 0) return float.NegativeInfinity;
            if (ix == 0x7f800000) return x;
            if ((ix & 0x80000000) != 0 || ix * 2 >= 0xff000000) return float.NaN;
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
