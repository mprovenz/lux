namespace Lux.Engine.Pipeline;

public static partial class WineCrt
{
    // ---------------------------------------------------------------------------------------------------------------------------------
    // exp2 (double) — musl's exp2 (ARM optimized-routines, EXP_TABLE_BITS 7, 5-term polynomial, no FMA), which Wine's ucrtbase uses.
    // Lumen calls it in `AdjustContrast<vec4x32f>`: g = (float)exp2((double)contrast_adjust.value · 0.3).
    // The result gives the same bits as Wine's ucrtbase `exp2` on (float)exp2((double)a · 0.3) for every float a in [−4, 4]
    // (the whole range of the contrast slider, ±100 · 0.04).
    // ---------------------------------------------------------------------------------------------------------------------------------
    const int Exp2dTableBits = 7, Exp2dN = 1 << Exp2dTableBits;
    static readonly double Exp2dShift = BitConverter.UInt64BitsToDouble(0x42c8000000000000UL);   // 0x1.8p52 / N
    static readonly double Exp2dC1 = BitConverter.UInt64BitsToDouble(0x3fe62e42fefa39efUL), Exp2dC2 = BitConverter.UInt64BitsToDouble(0x3fcebfbdff82c424UL),
                           Exp2dC3 = BitConverter.UInt64BitsToDouble(0x3fac6b08d70cf4b5UL), Exp2dC4 = BitConverter.UInt64BitsToDouble(0x3f83b2abd24650ccUL),
                           Exp2dC5 = BitConverter.UInt64BitsToDouble(0x3f55d7e09b4e3a84UL);
    /// <summary>`__exp_data.tab`: 2^(k/N) ≈ H[k]·(1 + T[k]); tab[2k] = bits(T[k]), tab[2k+1] = bits(H[k]) − (k &lt;&lt; 52)/N.</summary>
    static readonly ulong[] Exp2dTab =
    {
        0x0UL, 0x3ff0000000000000UL, 0x3c9b3b4f1a88bf6eUL, 0x3feff63da9fb3335UL,
        0xbc7160139cd8dc5dUL, 0x3fefec9a3e778061UL, 0xbc905e7a108766d1UL, 0x3fefe315e86e7f85UL,
        0x3c8cd2523567f613UL, 0x3fefd9b0d3158574UL, 0xbc8bce8023f98efaUL, 0x3fefd06b29ddf6deUL,
        0x3c60f74e61e6c861UL, 0x3fefc74518759bc8UL, 0x3c90a3e45b33d399UL, 0x3fefbe3ecac6f383UL,
        0x3c979aa65d837b6dUL, 0x3fefb5586cf9890fUL, 0x3c8eb51a92fdeffcUL, 0x3fefac922b7247f7UL,
        0x3c3ebe3d702f9cd1UL, 0x3fefa3ec32d3d1a2UL, 0xbc6a033489906e0bUL, 0x3fef9b66affed31bUL,
        0xbc9556522a2fbd0eUL, 0x3fef9301d0125b51UL, 0xbc5080ef8c4eea55UL, 0x3fef8abdc06c31ccUL,
        0xbc91c923b9d5f416UL, 0x3fef829aaea92de0UL, 0x3c80d3e3e95c55afUL, 0x3fef7a98c8a58e51UL,
        0xbc801b15eaa59348UL, 0x3fef72b83c7d517bUL, 0xbc8f1ff055de323dUL, 0x3fef6af9388c8deaUL,
        0x3c8b898c3f1353bfUL, 0x3fef635beb6fcb75UL, 0xbc96d99c7611eb26UL, 0x3fef5be084045cd4UL,
        0x3c9aecf73e3a2f60UL, 0x3fef54873168b9aaUL, 0xbc8fe782cb86389dUL, 0x3fef4d5022fcd91dUL,
        0x3c8a6f4144a6c38dUL, 0x3fef463b88628cd6UL, 0x3c807a05b0e4047dUL, 0x3fef3f49917ddc96UL,
        0x3c968efde3a8a894UL, 0x3fef387a6e756238UL, 0x3c875e18f274487dUL, 0x3fef31ce4fb2a63fUL,
        0x3c80472b981fe7f2UL, 0x3fef2b4565e27cddUL, 0xbc96b87b3f71085eUL, 0x3fef24dfe1f56381UL,
        0x3c82f7e16d09ab31UL, 0x3fef1e9df51fdee1UL, 0xbc3d219b1a6fbffaUL, 0x3fef187fd0dad990UL,
        0x3c8b3782720c0ab4UL, 0x3fef1285a6e4030bUL, 0x3c6e149289cecb8fUL, 0x3fef0cafa93e2f56UL,
        0x3c834d754db0abb6UL, 0x3fef06fe0a31b715UL, 0x3c864201e2ac744cUL, 0x3fef0170fc4cd831UL,
        0x3c8fdd395dd3f84aUL, 0x3feefc08b26416ffUL, 0xbc86a3803b8e5b04UL, 0x3feef6c55f929ff1UL,
        0xbc924aedcc4b5068UL, 0x3feef1a7373aa9cbUL, 0xbc9907f81b512d8eUL, 0x3feeecae6d05d866UL,
        0xbc71d1e83e9436d2UL, 0x3feee7db34e59ff7UL, 0xbc991919b3ce1b15UL, 0x3feee32dc313a8e5UL,
        0x3c859f48a72a4c6dUL, 0x3feedea64c123422UL, 0xbc9312607a28698aUL, 0x3feeda4504ac801cUL,
        0xbc58a78f4817895bUL, 0x3feed60a21f72e2aUL, 0xbc7c2c9b67499a1bUL, 0x3feed1f5d950a897UL,
        0x3c4363ed60c2ac11UL, 0x3feece086061892dUL, 0x3c9666093b0664efUL, 0x3feeca41ed1d0057UL,
        0x3c6ecce1daa10379UL, 0x3feec6a2b5c13cd0UL, 0x3c93ff8e3f0f1230UL, 0x3feec32af0d7d3deUL,
        0x3c7690cebb7aafb0UL, 0x3feebfdad5362a27UL, 0x3c931dbdeb54e077UL, 0x3feebcb299fddd0dUL,
        0xbc8f94340071a38eUL, 0x3feeb9b2769d2ca7UL, 0xbc87deccdc93a349UL, 0x3feeb6daa2cf6642UL,
        0xbc78dec6bd0f385fUL, 0x3feeb42b569d4f82UL, 0xbc861246ec7b5cf6UL, 0x3feeb1a4ca5d920fUL,
        0x3c93350518fdd78eUL, 0x3feeaf4736b527daUL, 0x3c7b98b72f8a9b05UL, 0x3feead12d497c7fdUL,
        0x3c9063e1e21c5409UL, 0x3feeab07dd485429UL, 0x3c34c7855019c6eaUL, 0x3feea9268a5946b7UL,
        0x3c9432e62b64c035UL, 0x3feea76f15ad2148UL, 0xbc8ce44a6199769fUL, 0x3feea5e1b976dc09UL,
        0xbc8c33c53bef4da8UL, 0x3feea47eb03a5585UL, 0xbc845378892be9aeUL, 0x3feea34634ccc320UL,
        0xbc93cedd78565858UL, 0x3feea23882552225UL, 0x3c5710aa807e1964UL, 0x3feea155d44ca973UL,
        0xbc93b3efbf5e2228UL, 0x3feea09e667f3bcdUL, 0xbc6a12ad8734b982UL, 0x3feea012750bdabfUL,
        0xbc6367efb86da9eeUL, 0x3fee9fb23c651a2fUL, 0xbc80dc3d54e08851UL, 0x3fee9f7df9519484UL,
        0xbc781f647e5a3ecfUL, 0x3fee9f75e8ec5f74UL, 0xbc86ee4ac08b7db0UL, 0x3fee9f9a48a58174UL,
        0xbc8619321e55e68aUL, 0x3fee9feb564267c9UL, 0x3c909ccb5e09d4d3UL, 0x3feea0694fde5d3fUL,
        0xbc7b32dcb94da51dUL, 0x3feea11473eb0187UL, 0x3c94ecfd5467c06bUL, 0x3feea1ed0130c132UL,
        0x3c65ebe1abd66c55UL, 0x3feea2f336cf4e62UL, 0xbc88a1c52fb3cf42UL, 0x3feea427543e1a12UL,
        0xbc9369b6f13b3734UL, 0x3feea589994cce13UL, 0xbc805e843a19ff1eUL, 0x3feea71a4623c7adUL,
        0xbc94d450d872576eUL, 0x3feea8d99b4492edUL, 0x3c90ad675b0e8a00UL, 0x3feeaac7d98a6699UL,
        0x3c8db72fc1f0eab4UL, 0x3feeace5422aa0dbUL, 0xbc65b6609cc5e7ffUL, 0x3feeaf3216b5448cUL,
        0x3c7bf68359f35f44UL, 0x3feeb1ae99157736UL, 0xbc93091fa71e3d83UL, 0x3feeb45b0b91ffc6UL,
        0xbc5da9b88b6c1e29UL, 0x3feeb737b0cdc5e5UL, 0xbc6c23f97c90b959UL, 0x3feeba44cbc8520fUL,
        0xbc92434322f4f9aaUL, 0x3feebd829fde4e50UL, 0xbc85ca6cd7668e4bUL, 0x3feec0f170ca07baUL,
        0x3c71affc2b91ce27UL, 0x3feec49182a3f090UL, 0x3c6dd235e10a73bbUL, 0x3feec86319e32323UL,
        0xbc87c50422622263UL, 0x3feecc667b5de565UL, 0x3c8b1c86e3e231d5UL, 0x3feed09bec4a2d33UL,
        0xbc91bbd1d3bcbb15UL, 0x3feed503b23e255dUL, 0x3c90cc319cee31d2UL, 0x3feed99e1330b358UL,
        0x3c8469846e735ab3UL, 0x3feede6b5579fdbfUL, 0xbc82dfcd978e9db4UL, 0x3feee36bbfd3f37aUL,
        0x3c8c1a7792cb3387UL, 0x3feee89f995ad3adUL, 0xbc907b8f4ad1d9faUL, 0x3feeee07298db666UL,
        0xbc55c3d956dcaebaUL, 0x3feef3a2b84f15fbUL, 0xbc90a40e3da6f640UL, 0x3feef9728de5593aUL,
        0xbc68d6f438ad9334UL, 0x3feeff76f2fb5e47UL, 0xbc91eee26b588a35UL, 0x3fef05b030a1064aUL,
        0x3c74ffd70a5fddcdUL, 0x3fef0c1e904bc1d2UL, 0xbc91bdfbfa9298acUL, 0x3fef12c25bd71e09UL,
        0x3c736eae30af0cb3UL, 0x3fef199bdd85529cUL, 0x3c8ee3325c9ffd94UL, 0x3fef20ab5fffd07aUL,
        0x3c84e08fd10959acUL, 0x3fef27f12e57d14bUL, 0x3c63cdaf384e1a67UL, 0x3fef2f6d9406e7b5UL,
        0x3c676b2c6c921968UL, 0x3fef3720dcef9069UL, 0xbc808a1883ccb5d2UL, 0x3fef3f0b555dc3faUL,
        0xbc8fad5d3ffffa6fUL, 0x3fef472d4a07897cUL, 0xbc900dae3875a949UL, 0x3fef4f87080d89f2UL,
        0x3c74a385a63d07a7UL, 0x3fef5818dcfba487UL, 0xbc82919e2040220fUL, 0x3fef60e316c98398UL,
        0x3c8e5a50d5c192acUL, 0x3fef69e603db3285UL, 0x3c843a59ac016b4bUL, 0x3fef7321f301b460UL,
        0xbc82d52107b43e1fUL, 0x3fef7c97337b9b5fUL, 0xbc892ab93b470dc9UL, 0x3fef864614f5a129UL,
        0x3c74b604603a88d3UL, 0x3fef902ee78b3ff6UL, 0x3c83c5ec519d7271UL, 0x3fef9a51fbc74c83UL,
        0xbc8ff7128fd391f0UL, 0x3fefa4afa2a490daUL, 0xbc8dae98e223747dUL, 0x3fefaf482d8e67f1UL,
        0x3c8ec3bc41aa2008UL, 0x3fefba1bee615a27UL, 0x3c842b94c3a9eb32UL, 0x3fefc52b376bba97UL,
        0x3c8a64a931d185eeUL, 0x3fefd0765b6e4540UL, 0xbc8e37bae43be3edUL, 0x3fefdbfdad9cbe14UL,
        0x3c77893b4d91cd9dUL, 0x3fefe7c1819e90d8UL, 0x3c5305c14160cc89UL, 0x3feff3c22b8f71f1UL,
    };

    static uint Top12(double x) => (uint)(BitConverter.DoubleToUInt64Bits(x) >> 52);

    /// <summary>`exp2` (double), musl's algorithm and operation order.</summary>
    public static double Exp2(double x)
    {
        uint abstop = Top12(x) & 0x7ff;
        if (abstop - 0x3c9u >= 0x408u - 0x3c9u)
        {
            if (abstop - 0x3c9u >= 0x80000000) return 1.0 + x;      // tiny x (WANT_ROUNDING)
            if (abstop >= 0x409u)
            {
                ulong ux = BitConverter.DoubleToUInt64Bits(x);
                if (ux == BitConverter.DoubleToUInt64Bits(double.NegativeInfinity)) return 0.0;
                if (abstop >= 0x7ffu) return 1.0 + x;
                if ((ux >> 63) == 0) return double.PositiveInfinity;           // __math_oflow
                else if (ux >= BitConverter.DoubleToUInt64Bits(-1075.0)) return 0.0;   // __math_uflow
            }
            if (2 * BitConverter.DoubleToUInt64Bits(x) > 2 * BitConverter.DoubleToUInt64Bits(928.0)) abstop = 0;
        }
        double kd = x + Exp2dShift;
        ulong ki = BitConverter.DoubleToUInt64Bits(kd);
        kd -= Exp2dShift;
        double r = x - kd;
        ulong idx = 2 * (ki % Exp2dN);
        ulong top = ki << (52 - Exp2dTableBits);
        double tail = BitConverter.UInt64BitsToDouble(Exp2dTab[idx]);
        ulong sbits = Exp2dTab[idx + 1] + top;
        double r2 = r * r;
        double tmp = tail + r * Exp2dC1 + r2 * (Exp2dC2 + r * Exp2dC3) + r2 * r2 * (Exp2dC4 + r * Exp2dC5);
        if (abstop == 0) return Exp2Special(tmp, sbits, ki);
        double scale = BitConverter.UInt64BitsToDouble(sbits);
        return scale + scale * tmp;
    }

    static double Exp2Special(double tmp, ulong sbits, ulong ki)
    {
        double scale, y;
        if ((ki & 0x80000000) == 0)
        {
            sbits -= 1UL << 52;
            scale = BitConverter.UInt64BitsToDouble(sbits);
            return 2 * (scale + scale * tmp);
        }
        sbits += 1022UL << 52;
        scale = BitConverter.UInt64BitsToDouble(sbits);
        y = scale + scale * tmp;
        if (y < 1.0)
        {
            double lo = scale - y + scale * tmp;
            double hi = 1.0 + y;
            lo = 1.0 - hi + y + lo;
            y = (hi + lo) - 1.0;
            if (y == 0.0) y = 0.0;
        }
        return BitConverter.UInt64BitsToDouble(0x0010000000000000UL) * y;   // 0x1p-1022
    }
}
