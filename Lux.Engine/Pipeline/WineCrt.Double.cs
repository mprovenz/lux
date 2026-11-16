// The functions in this file are C# ports of the double-precision maths functions of musl libc, in the form that Wine's
// ucrtbase.dll contains them.
//
// sin, cos, atan, atan2, acos, log10, expm1, tanh and the pi/2 reduction (musl: FreeBSD msun, from fdlibm):
//   Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//   Developed at SunSoft, a Sun Microsystems, Inc. business (SunPro for some files).
//   Permission to use, copy, modify, and distribute this software is freely granted, provided that this notice is preserved.
//
// exp, pow, log, log2 (musl: ARM optimized-routines):
//   Copyright (c) 2018, Arm Limited.
//   SPDX-License-Identifier: MIT
//
// musl as a whole: Copyright © 2005-2020 Rich Felker, et al., MIT licence.

namespace Lux.Engine.Pipeline;

// The C runtime's double maths as Lumen gets it. The summary of the class is in WineCrt.cs.
// Each function is a statement-by-statement port of the musl function that Wine's ucrtbase contains. The operation order is the order
// of the C source. The code has no fused multiply-add, because the CRT has none. The results do not depend on the platform's maths
// library: the functions use only +, −, ×, ÷, sqrt, rounding to an integer, the integer conversions and bit operations.
// Each function gives the same bits as the CRT for every argument, the NaN results on an x86 processor included. On other processors
// the bits of a NaN result can be different (the result is still a NaN).
public static partial class WineCrt
{
    static double Bits(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
    static ulong Bits(double x) => BitConverter.DoubleToUInt64Bits(x);
    static uint HighWord(double x) => (uint)(BitConverter.DoubleToUInt64Bits(x) >> 32);
    static uint LowWord(double x) => (uint)BitConverter.DoubleToUInt64Bits(x);

    /// <summary>The NaN that the CRT makes from an invalid operation (0/0, inf − inf) on x86: sign bit set, quiet, no payload.</summary>
    static readonly double InvalidNaN = Bits(0xfff8000000000000UL);

    // ---------------------------------------------------------------------------------------------------------------------------------
    // sin / cos — musl sin.c, cos.c, __sin.c, __cos.c, __rem_pio2.c, __rem_pio2_large.c
    // ---------------------------------------------------------------------------------------------------------------------------------
    const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
                 S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
    const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
                 C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

    /// <summary>`__sin`: the sine kernel on [−π/4, π/4]. The argument is x + y; iy = 0 tells that y is 0.</summary>
    static double KernelSin(double x, double y, int iy)
    {
        double z = x * x;
        double w = z * z;
        double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
        double v = z * x;
        if (iy == 0) return x + v * (S1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * S1);
    }

    /// <summary>`__cos`: the cosine kernel on [−π/4, π/4]. The argument is x + y.</summary>
    static double KernelCos(double x, double y)
    {
        double z = x * x;
        double w = z * z;
        double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + (z * r - x * y));
    }

    static readonly double RpPio4 = Bits(0x3fe921fb54442d18UL);
    static readonly double RpInvPio2 = Bits(0x3FE45F306DC9C883UL), RpPio2_1 = Bits(0x3FF921FB54400000UL), RpPio2_1t = Bits(0x3DD0B4611A626331UL),
                           RpPio2_2 = Bits(0x3DD0B4611A600000UL), RpPio2_2t = Bits(0x3BA3198A2E037073UL),
                           RpPio2_3 = Bits(0x3BA3198A2E000000UL), RpPio2_3t = Bits(0x397B839A252049C1UL);

    /// <summary>`__rem_pio2`: y0 + y1 = x − n·π/2 with |y0 + y1| ≤ π/4. The return value is n (only its low bits are significant for
    /// large arguments).</summary>
    static int RemPio2(double x, out double y0, out double y1)
    {
        ulong ui = Bits(x);
        bool sign = (ui >> 63) != 0;
        uint ix = (uint)(ui >> 32) & 0x7fffffff;
        double z, w, t, r, fn;
        int n;
        if (ix <= 0x400f6a7a)                               // |x| ~<= 5π/4
        {
            if ((ix & 0xfffff) == 0x921fb) goto medium;     // |x| ~= π/2 or π: cancellation
            if (ix <= 0x4002d97c)                           // |x| ~<= 3π/4
            {
                if (!sign) { z = x - RpPio2_1; y0 = z - RpPio2_1t; y1 = (z - y0) - RpPio2_1t; return 1; }
                z = x + RpPio2_1; y0 = z + RpPio2_1t; y1 = (z - y0) + RpPio2_1t; return -1;
            }
            if (!sign) { z = x - 2 * RpPio2_1; y0 = z - 2 * RpPio2_1t; y1 = (z - y0) - 2 * RpPio2_1t; return 2; }
            z = x + 2 * RpPio2_1; y0 = z + 2 * RpPio2_1t; y1 = (z - y0) + 2 * RpPio2_1t; return -2;
        }
        if (ix <= 0x401c463b)                               // |x| ~<= 9π/4
        {
            if (ix <= 0x4015fdbc)                           // |x| ~<= 7π/4
            {
                if (ix == 0x4012d97c) goto medium;          // |x| ~= 3π/2
                if (!sign) { z = x - 3 * RpPio2_1; y0 = z - 3 * RpPio2_1t; y1 = (z - y0) - 3 * RpPio2_1t; return 3; }
                z = x + 3 * RpPio2_1; y0 = z + 3 * RpPio2_1t; y1 = (z - y0) + 3 * RpPio2_1t; return -3;
            }
            if (ix == 0x401921fb) goto medium;              // |x| ~= 2π
            if (!sign) { z = x - 4 * RpPio2_1; y0 = z - 4 * RpPio2_1t; y1 = (z - y0) - 4 * RpPio2_1t; return 4; }
            z = x + 4 * RpPio2_1; y0 = z + 4 * RpPio2_1t; y1 = (z - y0) + 4 * RpPio2_1t; return -4;
        }
        if (ix < 0x413921fb) goto medium;                   // |x| ~< 2^20·π/2
        if (ix >= 0x7ff00000) { y0 = y1 = x - x; return 0; }
        {
            // large arguments: z = scalbn(|x|, −ilogb(x) + 23), cut into three 24-bit pieces
            ui &= ulong.MaxValue >> 12;
            ui |= (ulong)(0x3ff + 23) << 52;
            z = Bits(ui);
            Span<double> tx = stackalloc double[3];
            Span<double> ty = stackalloc double[2];
            int i;
            for (i = 0; i < 2; i++) { tx[i] = (double)(int)z; z = (z - tx[i]) * 16777216.0; }
            tx[i] = z;
            while (tx[i] == 0.0) i--;
            n = RemPio2Large(tx, ty, (int)(ix >> 20) - (0x3ff + 23), i + 1);
            if (sign) { y0 = -ty[0]; y1 = -ty[1]; return -n; }
            y0 = ty[0]; y1 = ty[1]; return n;
        }
    medium:
        fn = Math.Round(x * RpInvPio2);                     // rint: ties to even
        n = (int)fn;
        r = x - fn * RpPio2_1;
        w = fn * RpPio2_1t;                                 // first round, good to 85 bits
        if (r - w < -RpPio4) { n--; fn--; r = x - fn * RpPio2_1; w = fn * RpPio2_1t; }
        else if (r - w > RpPio4) { n++; fn++; r = x - fn * RpPio2_1; w = fn * RpPio2_1t; }
        y0 = r - w;
        int ey = (int)(Bits(y0) >> 52 & 0x7ff);
        int ex = (int)(ix >> 20);
        if (ex - ey > 16)                                   // second round, good to 118 bits
        {
            t = r;
            w = fn * RpPio2_2;
            r = t - w;
            w = fn * RpPio2_2t - ((t - r) - w);
            y0 = r - w;
            ey = (int)(Bits(y0) >> 52 & 0x7ff);
            if (ex - ey > 49)                               // third round, good to 151 bits
            {
                t = r;
                w = fn * RpPio2_3;
                r = t - w;
                w = fn * RpPio2_3t - ((t - r) - w);
                y0 = r - w;
            }
        }
        y1 = (r - y0) - w;
        return n;
    }

    /// <summary>`PIo2`: π/2 in 24-bit pieces.</summary>
    static readonly double[] RpPIo2 = FromBits(new ulong[]
    {
        0x3FF921FB40000000UL, 0x3E74442D00000000UL, 0x3CF8469880000000UL, 0x3B78CC5160000000UL,
        0x39F01B8380000000UL, 0x387A252040000000UL, 0x36E3822280000000UL, 0x3569F31D00000000UL,
    });

    /// <summary>`__rem_pio2_large` for double precision (prec = 1, jk = 4). x holds the nx 24-bit pieces of the argument with the
    /// exponent e0; y gets the two parts of the remainder. The return value is the quadrant modulo 8.</summary>
    static int RemPio2Large(Span<double> x, Span<double> y, int e0, int nx)
    {
        const int jk = 4, jp = jk;
        Span<int> iq = stackalloc int[20];
        Span<double> f = stackalloc double[20];
        Span<double> fq = stackalloc double[20];
        Span<double> q = stackalloc double[20];
        int jz, jx, jv, carry, n, i, j, k, m, q0, ih;
        double z, fw;

        jx = nx - 1;
        jv = (e0 - 3) / 24; if (jv < 0) jv = 0;
        q0 = e0 - 24 * (jv + 1);

        j = jv - jx; m = jx + jk;
        for (i = 0; i <= m; i++, j++) f[i] = j < 0 ? 0.0 : (double)Ipio2[j];

        for (i = 0; i <= jk; i++)
        {
            for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
            q[i] = fw;
        }

        jz = jk;
        while (true)
        {
            // distill q[] into iq[] in reverse order
            for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
            {
                fw = (double)(int)(5.9604644775390625e-08 * z);        // 2^−24
                iq[i] = (int)(z - 16777216.0 * fw);
                z = q[j - 1] + fw;
            }

            z = Math.ScaleB(z, q0);
            z -= 8.0 * Math.Floor(z * 0.125);
            n = (int)z;
            z -= (double)n;
            ih = 0;
            if (q0 > 0)
            {
                i = iq[jz - 1] >> (24 - q0); n += i;
                iq[jz - 1] -= i << (24 - q0);
                ih = iq[jz - 1] >> (23 - q0);
            }
            else if (q0 == 0) ih = iq[jz - 1] >> 23;
            else if (z >= 0.5) ih = 2;

            if (ih > 0)
            {
                n += 1; carry = 0;
                for (i = 0; i < jz; i++)
                {
                    j = iq[i];
                    if (carry == 0)
                    {
                        if (j != 0) { carry = 1; iq[i] = 0x1000000 - j; }
                    }
                    else iq[i] = 0xffffff - j;
                }
                if (q0 > 0)
                {
                    switch (q0)
                    {
                        case 1: iq[jz - 1] &= 0x7fffff; break;
                        case 2: iq[jz - 1] &= 0x3fffff; break;
                    }
                }
                if (ih == 2)
                {
                    z = 1.0 - z;
                    if (carry != 0) z -= Math.ScaleB(1.0, q0);
                }
            }

            if (z == 0.0)
            {
                j = 0;
                for (i = jz - 1; i >= jk; i--) j |= iq[i];
                if (j == 0)
                {
                    for (k = 1; iq[jk - k] == 0; k++) { }
                    for (i = jz + 1; i <= jz + k; i++)
                    {
                        f[jx + i] = (double)Ipio2[jv + i];
                        for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                        q[i] = fw;
                    }
                    jz += k;
                    continue;
                }
            }
            break;
        }

        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0) { jz--; q0 -= 24; }
        }
        else
        {
            z = Math.ScaleB(z, -q0);
            if (z >= 16777216.0)
            {
                fw = (double)(int)(5.9604644775390625e-08 * z);
                iq[jz] = (int)(z - 16777216.0 * fw);
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else iq[jz] = (int)z;
        }

        fw = Math.ScaleB(1.0, q0);
        for (i = jz; i >= 0; i--) { q[i] = fw * (double)iq[i]; fw *= 5.9604644775390625e-08; }

        for (i = jz; i >= 0; i--)
        {
            for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++) fw += RpPIo2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        fw = 0.0;
        for (i = jz; i >= 0; i--) fw += fq[i];
        y[0] = ih == 0 ? fw : -fw;
        fw = fq[0] - fw;
        for (i = 1; i <= jz; i++) fw += fq[i];
        y[1] = ih == 0 ? fw : -fw;
        return n & 7;
    }

    /// <summary>`sin`. Verified domain: all doubles.</summary>
    public static double Sin(double x)
    {
        uint ix = HighWord(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)                               // |x| ~< π/4
        {
            if (ix < 0x3e500000) return x;                  // |x| < 2^−26
            return KernelSin(x, 0.0, 0);
        }
        if (ix >= 0x7ff00000) return double.IsNaN(x) ? x - x : InvalidNaN;
        int n = RemPio2(x, out double y0, out double y1);
        switch (n & 3)
        {
            case 0: return KernelSin(y0, y1, 1);
            case 1: return KernelCos(y0, y1);
            case 2: return -KernelSin(y0, y1, 1);
            default: return -KernelCos(y0, y1);
        }
    }

    /// <summary>`cos`. Verified domain: all doubles.</summary>
    public static double Cos(double x)
    {
        uint ix = HighWord(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)                               // |x| ~< π/4
        {
            if (ix < 0x3e46a09e) return 1.0;                // |x| < 2^−27·√2
            return KernelCos(x, 0);
        }
        if (ix >= 0x7ff00000) return double.IsNaN(x) ? x - x : InvalidNaN;
        int n = RemPio2(x, out double y0, out double y1);
        switch (n & 3)
        {
            case 0: return KernelCos(y0, y1);
            case 1: return -KernelSin(y0, y1, 1);
            case 2: return -KernelCos(y0, y1);
            default: return KernelSin(y0, y1, 1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // atan / atan2 / acos — musl atan.c, atan2.c, acos.c
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly double[] AtanHi = FromBits(new ulong[] { 0x3FDDAC670561BB4FUL, 0x3FE921FB54442D18UL, 0x3FEF730BD281F69BUL, 0x3FF921FB54442D18UL });
    static readonly double[] AtanLo = FromBits(new ulong[] { 0x3C7A2B7F222F65E2UL, 0x3C81A62633145C07UL, 0x3C7007887AF0CBBDUL, 0x3C91A62633145C07UL });
    static readonly double[] AtanT = FromBits(new ulong[]
    {
        0x3FD555555555550DUL, 0xBFC999999998EBC4UL, 0x3FC24924920083FFUL, 0xBFBC71C6FE231671UL, 0x3FB745CDC54C206EUL, 0xBFB3B0F2AF749A6DUL,
        0x3FB10D66A0D03D51UL, 0xBFADDE2D52DEFD9AUL, 0x3FA97B4B24760DEBUL, 0xBFA2B4442C6A6C2FUL, 0x3F90AD3AE322DA11UL,
    });

    /// <summary>`atan`. Verified domain: all doubles.</summary>
    public static double Atan(double x)
    {
        double w, s1, s2, z;
        uint ix = HighWord(x);
        bool sign = (ix >> 31) != 0;
        int id;
        ix &= 0x7fffffff;
        if (ix >= 0x44100000)                               // |x| >= 2^66
        {
            if (double.IsNaN(x)) return x;
            z = AtanHi[3];
            return sign ? -z : z;
        }
        if (ix < 0x3fdc0000)                                // |x| < 0.4375
        {
            if (ix < 0x3e400000) return x;                  // |x| < 2^−27
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)                            // |x| < 1.1875
            {
                if (ix < 0x3fe60000) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }      // 7/16 <= |x| < 11/16
                else { id = 1; x = (x - 1.0) / (x + 1.0); }                            // 11/16 <= |x| < 19/16
            }
            else
            {
                if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }      // |x| < 2.4375
                else { id = 3; x = -1.0 / x; }                                         // 2.4375 <= |x| < 2^66
            }
        }
        z = x * x;
        w = z * z;
        s1 = z * (AtanT[0] + w * (AtanT[2] + w * (AtanT[4] + w * (AtanT[6] + w * (AtanT[8] + w * AtanT[10])))));
        s2 = w * (AtanT[1] + w * (AtanT[3] + w * (AtanT[5] + w * (AtanT[7] + w * AtanT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - (x * (s1 + s2) - AtanLo[id] - x);
        return sign ? -z : z;
    }

    const double PiHi = 3.1415926535897931160E+00;
    static readonly double PiLo = Bits(0x3CA1A62633145C07UL);

    /// <summary>`atan2(y, x)`. Verified domain: all pairs of doubles.</summary>
    public static double Atan2(double y, double x)
    {
        double z;
        if (double.IsNaN(x) || double.IsNaN(y)) return x + y;
        uint ix = HighWord(x), lx = LowWord(x), iy = HighWord(y), ly = LowWord(y);
        if (((ix - 0x3ff00000) | lx) == 0) return Atan(y);  // x = 1.0
        uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2);       // 2·sign(x) + sign(y)
        ix &= 0x7fffffff;
        iy &= 0x7fffffff;
        if ((iy | ly) == 0)                                 // y = 0
        {
            switch (m)
            {
                case 0: case 1: return y;
                case 2: return PiHi;
                default: return -PiHi;
            }
        }
        if ((ix | lx) == 0) return (m & 1) != 0 ? -PiHi / 2 : PiHi / 2;      // x = 0
        if (ix == 0x7ff00000)                               // x is infinite
        {
            if (iy == 0x7ff00000)
            {
                switch (m)
                {
                    case 0: return PiHi / 4;
                    case 1: return -PiHi / 4;
                    case 2: return 3 * PiHi / 4;
                    default: return -3 * PiHi / 4;
                }
            }
            switch (m)
            {
                case 0: return 0.0;
                case 1: return -0.0;
                case 2: return PiHi;
                default: return -PiHi;
            }
        }
        if (ix + (64u << 20) < iy || iy == 0x7ff00000) return (m & 1) != 0 ? -PiHi / 2 : PiHi / 2;   // |y/x| > 2^64
        if ((m & 2) != 0 && iy + (64u << 20) < ix) z = 0;   // |y/x| < 2^−64, x < 0
        else z = Atan(Math.Abs(y / x));
        switch (m)
        {
            case 0: return z;
            case 1: return -z;
            case 2: return PiHi - (z - PiLo);
            default: return (z - PiLo) - PiHi;
        }
    }

    static readonly double AcPio2Hi = Bits(0x3FF921FB54442D18UL), AcPio2Lo = Bits(0x3C91A62633145C07UL);
    static readonly double AcP0 = Bits(0x3FC5555555555555UL), AcP1 = Bits(0xBFD4D61203EB6F7DUL), AcP2 = Bits(0x3FC9C1550E884455UL),
                           AcP3 = Bits(0xBFA48228B5688F3BUL), AcP4 = Bits(0x3F49EFE07501B288UL), AcP5 = Bits(0x3F023DE10DFDF709UL),
                           AcQ1 = Bits(0xC0033A271C8A2D4BUL), AcQ2 = Bits(0x40002AE59C598AC8UL), AcQ3 = Bits(0xBFE6066C1B8D0159UL),
                           AcQ4 = Bits(0x3FB3B8C5B12E9282UL);

    static double AcosR(double z)
    {
        double p = z * (AcP0 + z * (AcP1 + z * (AcP2 + z * (AcP3 + z * (AcP4 + z * AcP5)))));
        double q = 1.0 + z * (AcQ1 + z * (AcQ2 + z * (AcQ3 + z * AcQ4)));
        return p / q;
    }

    /// <summary>`acos`. Verified domain: all doubles.</summary>
    public static double Acos(double x)
    {
        double z, w, s, c, df;
        uint hx = HighWord(x);
        uint ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)                               // |x| >= 1 or NaN
        {
            uint lx = LowWord(x);
            if (((ix - 0x3ff00000) | lx) == 0)
            {
                if ((hx >> 31) != 0) return 2 * AcPio2Hi;   // acos(−1) = π
                return 0;
            }
            if (double.IsNaN(x)) return x;
            return InvalidNaN;
        }
        if (ix < 0x3fe00000)                                // |x| < 0.5
        {
            if (ix <= 0x3c600000) return AcPio2Hi;          // |x| < 2^−57
            return AcPio2Hi - (x - (AcPio2Lo - x * AcosR(x * x)));
        }
        if ((hx >> 31) != 0)                                // x < −0.5
        {
            z = (1.0 + x) * 0.5;
            s = Math.Sqrt(z);
            w = AcosR(z) * s - AcPio2Lo;
            return 2 * (AcPio2Hi - (s + w));
        }
        z = (1.0 - x) * 0.5;                                // x > 0.5
        s = Math.Sqrt(z);
        df = Bits(Bits(s) & 0xffffffff00000000UL);
        c = (z - df * df) / (s + df);
        w = AcosR(z) * s + c;
        return 2 * (df + w);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // exp / pow — musl exp.c, pow.c (ARM optimized-routines; EXP_TABLE_BITS 7, POW_LOG_TABLE_BITS 7; the code path without FMA).
    // The CRT rounds k with round(), not with the shift constant.
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly double ExpInvLn2N = Bits(0x40671547652b82feUL), ExpNegLn2HiN = Bits(0xbf762e42fefa0000UL), ExpNegLn2LoN = Bits(0xbd0cf79abc9e3b3aUL);
    static readonly double ExpC2 = Bits(0x3fdffffffffffdbdUL), ExpC3 = Bits(0x3fc555555555543cUL), ExpC4 = Bits(0x3fa55555cf172b91UL), ExpC5 = Bits(0x3f81111167a4d017UL);
    static readonly double Two1009 = Bits(0x7f00000000000000UL), TwoM1022 = Bits(0x0010000000000000UL), Two1023 = Bits(0x7fe0000000000000UL);

    /// <summary>`specialcase` of exp.c: the result when scale·(1 + tmp) can overflow or underflow.</summary>
    static double ExpSpecial(double tmp, ulong sbits, ulong ki)
    {
        double scale, y;
        if ((ki & 0x80000000) == 0)
        {
            sbits -= 1009UL << 52;                          // k > 0: the exponent of scale can be too large by up to 460
            scale = Bits(sbits);
            return Two1009 * (scale + scale * tmp);
        }
        sbits += 1022UL << 52;                              // k < 0: the result can be subnormal
        scale = Bits(sbits);
        y = scale + scale * tmp;
        if (y < 1.0)
        {
            double hi, lo;
            lo = scale - y + scale * tmp;
            hi = 1.0 + y;
            lo = 1.0 - hi + y + lo;
            y = (hi + lo) - 1.0;
            if (y == 0.0) y = 0.0;
        }
        return TwoM1022 * y;
    }

    /// <summary>`exp`. Verified domain: all doubles.</summary>
    public static double Exp(double x)
    {
        uint abstop = Top12(x) & 0x7ff;
        if (abstop - 0x3c9u >= 0x408u - 0x3c9u)
        {
            if (abstop - 0x3c9u >= 0x80000000) return 1.0 + x;                 // |x| < 2^−54
            if (abstop >= 0x409u)                                              // |x| >= 1024, inf, NaN
            {
                if (Bits(x) == Bits(double.NegativeInfinity)) return 0.0;
                if (abstop >= 0x7ffu) return 1.0 + x;
                return (Bits(x) >> 63) != 0 ? 0.0 : double.PositiveInfinity;
            }
            abstop = 0;                                                        // |x| >= 512: the special case below
        }
        double z = ExpInvLn2N * x;
        double kd = Math.Round(z, MidpointRounding.AwayFromZero);
        ulong ki = (ulong)(long)kd;
        double r = x + kd * ExpNegLn2HiN + kd * ExpNegLn2LoN;
        ulong idx = 2 * (ki % Exp2dN);
        ulong top = ki << (52 - Exp2dTableBits);
        double tail = Bits(Exp2dTab[idx]);
        ulong sbits = Exp2dTab[idx + 1] + top;
        double r2 = r * r;
        double tmp = tail + r + r2 * (ExpC2 + r * ExpC3) + r2 * r2 * (ExpC4 + r * ExpC5);
        if (abstop == 0) return ExpSpecial(tmp, sbits, ki);
        double scale = Bits(sbits);
        return scale + scale * tmp;
    }

    static readonly double PowLn2Hi = Bits(0x3fe62e42fefa3800UL), PowLn2Lo = Bits(0x3d2ef35793c76730UL);
    const ulong PowOff = 0x3fe6955500000000UL;
    const uint PowSignBias = 0x800u << Exp2dTableBits;

    /// <summary>`log_inline` of pow.c: y + tail = log(x). ix holds the bits of x, normalised when x is subnormal.</summary>
    static double PowLog(ulong ix, out double tail)
    {
        ulong tmp = ix - PowOff;
        int i = (int)((tmp >> (52 - 7)) % 128);
        int k = (int)((long)tmp >> 52);
        ulong iz = ix - (tmp & 0xfffUL << 52);
        double z = Bits(iz);
        double kd = (double)k;

        double invc = PowdInvc[i], logc = PowdLogc[i], logctail = PowdLogcTail[i];

        // r = z/c − 1 is exact: z is cut so that rhi, rlo and rhi·rhi are exact
        double zhi = Bits((iz + (1UL << 31)) & (ulong.MaxValue << 32));
        double zlo = z - zhi;
        double rhi = zhi * invc - 1.0;
        double rlo = zlo * invc;
        double r = rhi + rlo;

        double t1 = kd * PowLn2Hi + logc;
        double t2 = t1 + r;
        double lo1 = kd * PowLn2Lo + logctail;
        double lo2 = t1 - t2 + r;

        double ar = PowdPoly[0] * r;
        double ar2 = r * ar;
        double ar3 = r * ar2;
        double arhi = PowdPoly[0] * rhi;
        double arhi2 = rhi * arhi;
        double hi = t2 + arhi2;
        double lo3 = rlo * (ar + arhi);
        double lo4 = t2 - hi + arhi2;
        double p = ar3 * (PowdPoly[1] + r * PowdPoly[2] + ar2 * (PowdPoly[3] + r * PowdPoly[4] + ar2 * (PowdPoly[5] + r * PowdPoly[6])));
        double lo = lo1 + lo2 + lo3 + lo4 + p;
        double y = hi + lo;
        tail = hi - y + lo;
        return y;
    }

    /// <summary>`specialcase` of pow.c: as <see cref="ExpSpecial"/>, with the sign of the result in sbits.</summary>
    static double PowSpecial(double tmp, ulong sbits, ulong ki)
    {
        double scale, y;
        if ((ki & 0x80000000) == 0)
        {
            sbits -= 1009UL << 52;
            scale = Bits(sbits);
            return Two1009 * (scale + scale * tmp);
        }
        sbits += 1022UL << 52;
        scale = Bits(sbits);
        y = scale + scale * tmp;
        if (Math.Abs(y) < 1.0)
        {
            double hi, lo, one = 1.0;
            if (y < 0.0) one = -1.0;
            lo = scale - y + scale * tmp;
            hi = one + y;
            lo = one - hi + y + lo;
            y = (hi + lo) - one;
            if (y == 0.0) y = Bits(sbits & 0x8000000000000000UL);              // the sign of zero
        }
        return TwoM1022 * y;
    }

    /// <summary>`exp_inline` of pow.c: sign·exp(x + xtail).</summary>
    static double PowExp(double x, double xtail, uint signBias)
    {
        uint abstop = Top12(x) & 0x7ff;
        if (abstop - 0x3c9u >= 0x408u - 0x3c9u)
        {
            if (abstop - 0x3c9u >= 0x80000000)
            {
                double one = 1.0 + x;
                return signBias != 0 ? -one : one;
            }
            if (abstop >= 0x409u)
            {
                if ((Bits(x) >> 63) != 0) return signBias != 0 ? -0.0 : 0.0;
                return signBias != 0 ? double.NegativeInfinity : double.PositiveInfinity;
            }
            abstop = 0;
        }
        double z = ExpInvLn2N * x;
        double kd = Math.Round(z, MidpointRounding.AwayFromZero);
        ulong ki = (ulong)(long)kd;
        double r = x + kd * ExpNegLn2HiN + kd * ExpNegLn2LoN;
        r += xtail;
        ulong idx = 2 * (ki % Exp2dN);
        ulong top = (ki + signBias) << (52 - Exp2dTableBits);
        double tail = Bits(Exp2dTab[idx]);
        ulong sbits = Exp2dTab[idx + 1] + top;
        double r2 = r * r;
        double tmp = tail + r + r2 * (ExpC2 + r * ExpC3) + r2 * r2 * (ExpC4 + r * ExpC5);
        if (abstop == 0) return PowSpecial(tmp, sbits, ki);
        double scale = Bits(sbits);
        return scale + scale * tmp;
    }

    /// <summary>0: y is not an integer; 1: an odd integer; 2: an even integer. iy holds the bits of a finite y that is not 0.</summary>
    static int CheckInt(ulong iy)
    {
        int e = (int)(iy >> 52 & 0x7ff);
        if (e < 0x3ff) return 0;
        if (e > 0x3ff + 52) return 2;
        if ((iy & ((1UL << (0x3ff + 52 - e)) - 1)) != 0) return 0;
        if ((iy & (1UL << (0x3ff + 52 - e))) != 0) return 1;
        return 2;
    }

    static bool ZeroInfNan(ulong i) => 2 * i - 1 >= 2 * 0x7ff0000000000000UL - 1;

    /// <summary>`pow`. Verified domain: all pairs of doubles.</summary>
    public static double Pow(double x, double y)
    {
        const ulong One = 0x3ff0000000000000UL, Inf = 0x7ff0000000000000UL;
        uint signBias = 0;
        ulong ix = Bits(x), iy = Bits(y);
        uint topx = Top12(x), topy = Top12(y);
        if (topx - 0x001u >= 0x7ffu - 0x001u || (topy & 0x7ff) - 0x3beu >= 0x43eu - 0x3beu)
        {
            // x is 0, subnormal, negative, infinite or NaN; or |y| < 2^−65, |y| >= 2^63 or y is NaN
            if (ZeroInfNan(iy))
            {
                if (2 * iy == 0) return 1.0;
                if (ix == One) return 1.0;
                if (2 * ix > 2 * Inf || 2 * iy > 2 * Inf) return x + y;
                if (2 * ix == 2 * One) return 1.0;
                if ((2 * ix < 2 * One) == ((iy >> 63) == 0)) return 0.0;       // |x| < 1 and y = inf, or |x| > 1 and y = −inf
                return y * y;
            }
            if (ZeroInfNan(ix))
            {
                double x2 = x * x;
                if ((ix >> 63) != 0 && CheckInt(iy) == 1) x2 = -x2;
                return (iy >> 63) != 0 ? 1 / x2 : x2;
            }
            if ((ix >> 63) != 0)                                               // finite x < 0
            {
                int yint = CheckInt(iy);
                if (yint == 0) return InvalidNaN;
                if (yint == 1) signBias = PowSignBias;
                ix &= 0x7fffffffffffffffUL;
                topx &= 0x7ff;
            }
            if ((topy & 0x7ff) - 0x3beu >= 0x43eu - 0x3beu)
            {
                if (ix == One) return 1.0;
                if ((topy & 0x7ff) < 0x3be) return ix > One ? 1.0 + y : 1.0 - y;           // |y| < 2^−65
                return (ix > One) == (topy < 0x800) ? double.PositiveInfinity : 0.0;
            }
            if (topx == 0)
            {
                ix = Bits(x * 4503599627370496.0);                             // subnormal x: normalise with 2^52
                ix &= 0x7fffffffffffffffUL;
                ix -= 52UL << 52;
            }
        }

        double hi = PowLog(ix, out double lo);
        double yhi = Bits(iy & (ulong.MaxValue << 27));
        double ylo = y - yhi;
        double lhi = Bits(Bits(hi) & (ulong.MaxValue << 27));
        double llo = hi - lhi + lo;
        double ehi = yhi * lhi;
        double elo = ylo * lhi + y * llo;
        return PowExp(ehi, elo, signBias);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // log / log2 / log10 — musl log.c, log2.c (ARM optimized-routines, the code path without FMA) and log10.c (FreeBSD)
    // ---------------------------------------------------------------------------------------------------------------------------------
    const ulong LogOff = 0x3fe6000000000000UL;

    /// <summary>`log`. Verified domain: all doubles.</summary>
    public static double Log(double x)
    {
        double w, z, r, r2, r3, y, invc, logc, kd, hi, lo;
        ulong ix = Bits(x);
        uint top = (uint)(ix >> 48);
        const ulong Lo = 0x3fee000000000000UL, Hi = 0x3ff1090000000000UL;      // 1 − 2^−4, 1 + 0x1.09p−4
        if (ix - Lo < Hi - Lo)
        {
            if (ix == 0x3ff0000000000000UL) return 0;
            r = x - 1.0;
            r2 = r * r;
            r3 = r * r2;
            y = r3 * (LogdPoly1[1] + r * LogdPoly1[2] + r2 * LogdPoly1[3]
                      + r3 * (LogdPoly1[4] + r * LogdPoly1[5] + r2 * LogdPoly1[6]
                              + r3 * (LogdPoly1[7] + r * LogdPoly1[8] + r2 * LogdPoly1[9] + r3 * LogdPoly1[10])));
            w = r * 134217728.0;                                               // 2^27
            double rhi = r + w - w;
            double rlo = r - rhi;
            w = rhi * rhi * LogdPoly1[0];
            hi = r + w;
            lo = r - hi + w;
            lo += LogdPoly1[0] * rlo * (rhi + r);
            y += lo;
            y += hi;
            return y;
        }
        if (top - 0x0010u >= 0x7ff0u - 0x0010u)
        {
            if (ix * 2 == 0) return double.NegativeInfinity;
            if (ix == 0x7ff0000000000000UL) return x;
            if ((top & 0x7ff0) == 0x7ff0 && (ix & 0xfffffffffffffUL) != 0) return x;
            if ((top & 0x8000) != 0) return InvalidNaN;
            ix = Bits(x * 4503599627370496.0);                                 // subnormal x: normalise with 2^52
            ix -= 52UL << 52;
        }
        ulong tmp = ix - LogOff;
        int i = (int)((tmp >> (52 - 7)) % 128);
        int k = (int)((long)tmp >> 52);
        ulong iz = ix - (tmp & 0xfffUL << 52);
        invc = LogdInvc[i];
        logc = LogdLogc[i];
        z = Bits(iz);
        r = (z - LogdChi[i] - LogdClo[i]) * invc;
        kd = (double)k;
        w = kd * PowLn2Hi + logc;
        hi = w + r;
        lo = w - hi + r + kd * PowLn2Lo;
        r2 = r * r;
        y = lo + r2 * LogdPoly[0] + r * r2 * (LogdPoly[1] + r * LogdPoly[2] + r2 * (LogdPoly[3] + r * LogdPoly[4])) + hi;
        return y;
    }

    static readonly double Log2InvLn2Hi = Bits(0x3ff7154765200000UL), Log2InvLn2Lo = Bits(0x3de705fc2eefa200UL);

    /// <summary>`log2`. Verified domain: all doubles.</summary>
    public static double Log2(double x)
    {
        double z, r, r2, r4, y, invc, logc, kd, hi, lo, t1, t2, t3, p, rhi, rlo;
        ulong ix = Bits(x);
        uint top = (uint)(ix >> 48);
        const ulong Lo = 0x3feea4af00000000UL, Hi = 0x3ff0b55900000000UL;      // 1 − 0x1.5b51p−5, 1 + 0x1.6ab2p−5
        if (ix - Lo < Hi - Lo)
        {
            if (ix == 0x3ff0000000000000UL) return 0;
            r = x - 1.0;
            rhi = Bits(Bits(r) & (ulong.MaxValue << 32));
            rlo = r - rhi;
            hi = rhi * Log2InvLn2Hi;
            lo = rlo * Log2InvLn2Hi + r * Log2InvLn2Lo;
            r2 = r * r;
            r4 = r2 * r2;
            p = r2 * (Log2dPoly1[0] + r * Log2dPoly1[1]);
            y = hi + p;
            lo += hi - y + p;
            lo += r4 * (Log2dPoly1[2] + r * Log2dPoly1[3] + r2 * (Log2dPoly1[4] + r * Log2dPoly1[5])
                        + r4 * (Log2dPoly1[6] + r * Log2dPoly1[7] + r2 * (Log2dPoly1[8] + r * Log2dPoly1[9])));
            y += lo;
            return y;
        }
        if (top - 0x0010u >= 0x7ff0u - 0x0010u)
        {
            if (ix * 2 == 0) return double.NegativeInfinity;
            if (ix == 0x7ff0000000000000UL) return x;
            if ((top & 0x7ff0) == 0x7ff0 && (ix & 0xfffffffffffffUL) != 0) return x;
            if ((top & 0x8000) != 0) return InvalidNaN;
            ix = Bits(x * 4503599627370496.0);
            ix -= 52UL << 52;
        }
        ulong tmp = ix - LogOff;
        int i = (int)((tmp >> (52 - 6)) % 64);
        int k = (int)((long)tmp >> 52);
        ulong iz = ix - (tmp & 0xfffUL << 52);
        invc = Log2dInvc[i];
        logc = Log2dLogc[i];
        z = Bits(iz);
        kd = (double)k;
        r = (z - Log2dChi[i] - Log2dClo[i]) * invc;
        rhi = Bits(Bits(r) & (ulong.MaxValue << 32));
        rlo = r - rhi;
        t1 = rhi * Log2InvLn2Hi;
        t2 = rlo * Log2InvLn2Hi + r * Log2InvLn2Lo;
        t3 = kd + logc;
        hi = t3 + t1;
        lo = t3 - hi + t1 + t2;
        r2 = r * r;
        r4 = r2 * r2;
        p = Log2dPoly[0] + r * Log2dPoly[1] + r2 * (Log2dPoly[2] + r * Log2dPoly[3]) + r4 * (Log2dPoly[4] + r * Log2dPoly[5]);
        y = lo + r2 * p + hi;
        return y;
    }

    static readonly double L10IvLn10Hi = Bits(0x3fdbcb7b15200000UL), L10IvLn10Lo = Bits(0x3dbb9438ca9aadd5UL),
                           L10Log2Hi = Bits(0x3FD34413509F6000UL), L10Log2Lo = Bits(0x3D59FEF311F12B36UL),
                           Lg1 = Bits(0x3FE5555555555593UL), Lg2 = Bits(0x3FD999999997FA04UL), Lg3 = Bits(0x3FD2492494229359UL),
                           Lg4 = Bits(0x3FCC71C51D8E78AFUL), Lg5 = Bits(0x3FC7466496CB03DEUL), Lg6 = Bits(0x3FC39A09D078C69FUL),
                           Lg7 = Bits(0x3FC2F112DF3E5244UL);

    /// <summary>`log10`. Verified domain: all doubles.</summary>
    public static double Log10(double x)
    {
        double hfsq, f, s, z, R, w, t1, t2, dk, y, hi, lo, valHi, valLo;
        ulong ui = Bits(x);
        uint hx = (uint)(ui >> 32);
        int k = 0;
        if (hx < 0x00100000 || (hx >> 31) != 0)
        {
            if (ui << 1 == 0) return double.NegativeInfinity;
            if ((ui & ~(1UL << 63)) > 0x7ff0000000000000UL) return x;
            if ((hx >> 31) != 0) return InvalidNaN;
            k -= 54;                                                           // subnormal x: scale with 2^54
            x *= 18014398509481984.0;
            ui = Bits(x);
            hx = (uint)(ui >> 32);
        }
        else if (hx >= 0x7ff00000) return x;
        else if (hx == 0x3ff00000 && ui << 32 == 0) return 0;

        // reduce x into [√2/2, √2]
        hx += 0x3ff00000 - 0x3fe6a09e;
        k += (int)(hx >> 20) - 0x3ff;
        hx = (hx & 0x000fffff) + 0x3fe6a09e;
        ui = (ulong)hx << 32 | (ui & 0xffffffff);
        x = Bits(ui);

        f = x - 1.0;
        hfsq = 0.5 * f * f;
        s = f / (2.0 + f);
        z = s * s;
        w = z * z;
        t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        R = t2 + t1;

        hi = f - hfsq;
        hi = Bits(Bits(hi) & (ulong.MaxValue << 32));
        lo = f - hi - hfsq + s * (hfsq + R);

        valHi = hi * L10IvLn10Hi;
        dk = k;
        y = dk * L10Log2Hi;
        valLo = dk * L10Log2Lo + (lo + hi) * L10IvLn10Lo + lo * L10IvLn10Hi;

        w = y + valHi;
        valLo += (y - w) + valHi;
        valHi = w;
        return valLo + valHi;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // expm1 / tanh — musl expm1.c, tanh.c
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly double Em1OThreshold = Bits(0x40862E42FEFA39EFUL), Em1Ln2Hi = Bits(0x3fe62e42fee00000UL), Em1Ln2Lo = Bits(0x3dea39ef35793c76UL),
                           Em1InvLn2 = Bits(0x3ff71547652b82feUL),
                           Em1Q1 = Bits(0xBFA11111111110F4UL), Em1Q2 = Bits(0x3F5A01A019FE5585UL), Em1Q3 = Bits(0xBF14CE199EAADBB7UL),
                           Em1Q4 = Bits(0x3ED0CFCA86E65239UL), Em1Q5 = Bits(0xBE8AFDB76E09C32DUL);

    /// <summary>`expm1`. Verified domain: all doubles.</summary>
    public static double Expm1(double x)
    {
        double y, hi, lo, c, t, e, hxs, hfx, r1, twopk;
        ulong ui = Bits(x);
        uint hx = (uint)(ui >> 32) & 0x7fffffff;
        int k;
        bool sign = (ui >> 63) != 0;

        if (hx >= 0x4043687A)                               // |x| >= 56·ln2
        {
            if (double.IsNaN(x)) return x;
            if (double.IsInfinity(x)) return sign ? -1 : x;
            if (sign) return -1;
            if (x > Em1OThreshold) return x * Two1023;                         // overflow
        }

        if (hx > 0x3fd62e42)                                // |x| > 0.5·ln2
        {
            if (hx < 0x3FF0A2B2)                            // |x| < 1.5·ln2
            {
                if (!sign) { hi = x - Em1Ln2Hi; lo = Em1Ln2Lo; k = 1; }
                else { hi = x + Em1Ln2Hi; lo = -Em1Ln2Lo; k = -1; }
            }
            else
            {
                k = (int)(Em1InvLn2 * x + (sign ? -0.5 : 0.5));
                t = k;
                hi = x - t * Em1Ln2Hi;
                lo = t * Em1Ln2Lo;
            }
            x = hi - lo;
            c = (hi - x) - lo;
        }
        else if (hx < 0x3c900000) return x;                 // |x| < 2^−54
        else { k = 0; c = 0; }

        hfx = 0.5 * x;
        hxs = x * hfx;
        r1 = 1.0 + hxs * (Em1Q1 + hxs * (Em1Q2 + hxs * (Em1Q3 + hxs * (Em1Q4 + hxs * Em1Q5))));
        t = 3.0 - r1 * hfx;
        e = hxs * ((r1 - t) / (6.0 - x * t));
        if (k == 0) return x - (x * e - hxs);
        e = x * (e - c) - c;
        e -= hxs;
        if (k == -1) return 0.5 * (x - e) - 0.5;
        if (k == 1)
        {
            if (x < -0.25) return -2.0 * (e - (x + 0.5));
            return 1.0 + 2.0 * (x - e);
        }
        twopk = Bits((ulong)(0x3ff + k) << 52);             // 2^k
        if (k < 0 || k > 56)
        {
            y = x - e + 1.0;
            if (k == 1024) y = y * 2.0 * Two1023;
            else y = y * twopk;
            return y - 1.0;
        }
        double twomk = Bits((ulong)(0x3ff - k) << 52);      // 2^−k
        if (k < 20) y = (x - e + (1 - twomk)) * twopk;
        else y = (x - (e + twomk) + 1) * twopk;
        return y;
    }

    /// <summary>`tanh`. Verified domain: all doubles.</summary>
    public static double Tanh(double x)
    {
        ulong ui = Bits(x);
        ulong sign = ui & 0x8000000000000000UL;
        double t;
        ui &= ulong.MaxValue / 2;
        x = Bits(ui);
        uint w = (uint)(ui >> 32);
        if (w > 0x3fe193ea)                                 // |x| > log(3)/2 or NaN
        {
            if (w > 0x40340000)                             // |x| > 20 or NaN
            {
                if (w > 0x7ff00000) return Bits(ui | sign | 0x0008000000000000UL);
                t = 1 - 0 / x;
            }
            else
            {
                t = Expm1(2 * x);
                t = 1 - 2 / (t + 2);
            }
        }
        else if (w > 0x3fd058ae)                            // |x| > log(5/3)/2
        {
            t = Expm1(2 * x);
            t = t / (t + 2);
        }
        else if (w >= 0x00100000)                           // |x| >= 2^−1022
        {
            t = Expm1(-2 * x);
            t = -t / (t + 2);
        }
        else t = x;                                         // |x| is subnormal
        return sign != 0 ? -t : t;
    }
}
