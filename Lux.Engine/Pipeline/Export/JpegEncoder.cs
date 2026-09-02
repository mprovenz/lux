using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The JPEG encoder cp.dll links statically: **libjpeg-turbo**, driven by `FUN_1800fd680` (`a-display-isp.md` §12.3)
/// with the literal option struct `{ quality = 98, subsamplingId = 2 }` (`movabs rax, 0x200000062` at `0x1805295de`).
/// Everything that is not listed below stays at `jpeg_set_defaults`, so the stream is a **baseline sequential** JPEG
/// with the standard Annex-K Huffman tables (`optimize_coding = FALSE`), no restart interval, no smoothing, no
/// progression, no Adobe marker and **no ICC profile**.
///
/// What the port reproduces, function by function:
/// <list type="bullet">
/// <item>`jcparam.c` — `jpeg_set_quality(98, force_baseline = TRUE)` → `jpeg_quality_scaling(98) = 200 − 2·98 = 4`
///   → `jpeg_add_quant_table`: `q = clamp((basic·4 + 50)/100, 1, 255)` over the two Table-K.1/K.2 bases.</item>
/// <item>`jccolor.c` — `rgb_ycc_start` / `rgb_ycc_convert`: the 8×256 `JLONG` fixed-point table with
///   `FIX(x) = (long)(x·65536 + 0.5)`, `ONE_HALF`, `CBCR_OFFSET`, and the `>> 16` arithmetic shift.
///   `in_color_space = JCS_EXT_RGBX` (`0x1800fd6f6`) → red/green/blue at byte 0/1/2 of a 4-byte pixel, alpha ignored.</item>
/// <item>`jcsample.c` — `fullsize_downsample` (Y) and `h2v2_downsample` (Cb/Cr, subsampling map key 2 = 4:2:0) with the
///   alternating `bias = 1,2` rounding and `expand_right_edge`; `jcprepct.c`'s `expand_bottom_edge`.</item>
/// <item>`jfdctint.c` — `jpeg_fdct_islow` (JDCT_ISLOW, `cinfo+0x108 = 0` at `0x1800fd727`), then `jcdctmgr.c`'s
///   `quantize` with `divisors[i] = quantval[i] << 3` and round-half-up-of-magnitude division.</item>
/// <item>`jchuff.c` — `encode_one_block` + `emit_bits` with `0xFF → 0xFF 0x00` stuffing and the 1-padded final byte.</item>
/// <item>`jcmarker.c` — SOI, APP0(JFIF 1.1, `density_unit`/`X_density`/`Y_density` = 1/72/72 from `0x1800fd731`),
///   then the application markers (COM, APP1…) written between `jpeg_start_compress` and the first
///   `jpeg_write_scanlines`, then DQT×n, SOF0, DHT×n, SOS.</item>
/// </list>
///
/// libjpeg-turbo's SIMD kernels for `islow` FDCT, RGB→YCbCr and h2v2 downsampling are bit-identical to the C
/// reference (that identity is part of its own test suite), so this scalar port is the right target.
/// </summary>
public sealed class JpegEncoder
{
    /// <summary>`opts.quality` — the literal 0x62 at `0x1805295de`. Passed to `jpeg_set_quality(…, force_baseline = TRUE)`.</summary>
    public int Quality = 98;
    /// <summary>`opts.subsamplingId` — the literal 2 = 4:2:0 (`std::map` at `0x180831918`, key 2 → `2,2 / 1,1 / 1,1`).
    /// 0 = 4:4:4, 1 = 4:2:2. Ignored for grayscale (`0x1800fd75a` is inside the `fmt > 8` branch).</summary>
    public int SubsamplingId = 2;
    /// <summary>`jpeg_write_marker(0xFE, …)` at `0x1800fd98b` — `"Created with LibCP " + CIAPI::GetVersion()`. Empty → not written.</summary>
    public string? Comment;
    /// <summary>`jpeg_write_marker(0xE1, …)` at `0x1800fd9b6` — the whole APP1 payload, i.e. `"Exif\0\0"` + the TIFF block.</summary>
    public byte[]? ExifApp1;
    /// <summary>The APP1 deque drained at `0x1800fd9fe` — the GDepth XMP standard packet and its extension chunks, in FIFO order.</summary>
    public List<byte[]> ExtraApp1 = new();
    public int DensityUnit = 1, XDensity = 72, YDensity = 72;   // 0x1800fd731 / 0x1800fd738

    // ---------------------------------------------------------------- jcparam.c: the Annex-K quantization bases

    /// <summary>Table K.1 (luminance), natural order — `std_luminance_quant_tbl` in `jcparam.c`.</summary>
    static readonly int[] StdLuminance =
    {
        16,  11,  10,  16,  24,  40,  51,  61,
        12,  12,  14,  19,  26,  58,  60,  55,
        14,  13,  16,  24,  40,  57,  69,  56,
        14,  17,  22,  29,  51,  87,  80,  62,
        18,  22,  37,  56,  68, 109, 103,  77,
        24,  35,  55,  64,  81, 104, 113,  92,
        49,  64,  78,  87, 103, 121, 120, 101,
        72,  92,  95,  98, 112, 100, 103,  99,
    };

    /// <summary>Table K.2 (chrominance), natural order — `std_chrominance_quant_tbl`.</summary>
    static readonly int[] StdChrominance =
    {
        17,  18,  24,  47,  99,  99,  99,  99,
        18,  21,  26,  66,  99,  99,  99,  99,
        24,  26,  56,  99,  99,  99,  99,  99,
        47,  66,  99,  99,  99,  99,  99,  99,
        99,  99,  99,  99,  99,  99,  99,  99,
        99,  99,  99,  99,  99,  99,  99,  99,
        99,  99,  99,  99,  99,  99,  99,  99,
        99,  99,  99,  99,  99,  99,  99,  99,
    };

    /// <summary>`jpeg_natural_order`: zigzag index → natural (row-major) index.</summary>
    internal static readonly int[] NaturalOrder =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>`jpeg_quality_scaling`: `q &lt; 50 ? 5000/q : 200 − 2q`, clamped to 1…100.</summary>
    public static int QualityScaling(int quality)
    {
        if (quality <= 0) quality = 1;
        if (quality > 100) quality = 100;
        return quality < 50 ? 5000 / quality : 200 - quality * 2;
    }

    /// <summary>`jpeg_add_quant_table(basic, scale, force_baseline = TRUE)`: `(basic·scale + 50)/100` clamped to 1…255.</summary>
    public static int[] ScaleQuantTable(int[] basic, int scaleFactor)
    {
        var q = new int[64];
        for (int i = 0; i < 64; i++)
        {
            long t = ((long)basic[i] * scaleFactor + 50L) / 100L;
            if (t <= 0L) t = 1L;
            if (t > 32767L) t = 32767L;
            if (t > 255L) t = 255L;          // force_baseline
            q[i] = (int)t;
        }
        return q;
    }

    // ---------------------------------------------------------------- jcparam.c: the Annex-K Huffman tables
    // Extracted byte-for-byte from the DHT markers of Lumen's own JPEGs; identical to `std_huff_tables()`.

    sealed class HuffTable
    {
        public readonly byte[] Bits = new byte[17];   // Bits[1..16]
        public byte[] Values = Array.Empty<byte>();
        public int[] Code = new int[256];
        public int[] Size = new int[256];

        /// <summary>`jpeg_make_c_derived_tbl`: canonical code assignment, `huffsize` then `huffcode`.</summary>
        public void Derive()
        {
            var huffsize = new int[257]; var huffcode = new int[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
                for (int i = 1; i <= Bits[l]; i++) huffsize[p++] = l;
            huffsize[p] = 0; int lastp = p;
            int code = 0, si = huffsize[0]; p = 0;
            while (huffsize[p] != 0)
            {
                while (huffsize[p] == si) { huffcode[p++] = code; code++; }
                code <<= 1; si++;
            }
            Array.Clear(Code); Array.Clear(Size);
            for (p = 0; p < lastp; p++) { int v = Values[p]; Code[v] = huffcode[p]; Size[v] = huffsize[p]; }
        }

        public static HuffTable Make(byte[] bits16, byte[] vals)
        {
            var t = new HuffTable();
            for (int i = 0; i < 16; i++) t.Bits[i + 1] = bits16[i];
            t.Values = vals; t.Derive(); return t;
        }
    }

    static readonly byte[] BitsDcLuminance = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] ValDcLuminance = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    static readonly byte[] BitsDcChrominance = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    static readonly byte[] ValDcChrominance = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    static readonly byte[] BitsAcLuminance = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    static readonly byte[] ValAcLuminance =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    static readonly byte[] BitsAcChrominance = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    static readonly byte[] ValAcChrominance =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    // ---------------------------------------------------------------- jccolor.c

    const int ScaleBits = 16;
    static readonly long[] RgbYccTab = BuildRgbYccTab();

    static long Fix(double x) => (long)(x * (1L << ScaleBits) + 0.5);

    /// <summary>`rgb_ycc_start`: the 8 × 256 entry `JLONG` table (R_Y, G_Y, B_Y, R_CB, G_CB, B_CB(=R_CR), G_CR, B_CR).</summary>
    static long[] BuildRgbYccTab()
    {
        const int max = 255;
        long oneHalf = 1L << (ScaleBits - 1);
        long cbcrOffset = 128L << ScaleBits;
        var t = new long[8 * 256];
        for (int i = 0; i <= max; i++)
        {
            t[i + 0 * 256] = Fix(0.29900) * i;
            t[i + 1 * 256] = Fix(0.58700) * i;
            t[i + 2 * 256] = Fix(0.11400) * i + oneHalf;
            t[i + 3 * 256] = -Fix(0.16874) * i;
            t[i + 4 * 256] = -Fix(0.33126) * i;
            t[i + 5 * 256] = Fix(0.50000) * i + cbcrOffset + oneHalf - 1;   // = R_CR too
            t[i + 6 * 256] = -Fix(0.41869) * i;
            t[i + 7 * 256] = -Fix(0.08131) * i;
        }
        return t;
    }

    // ---------------------------------------------------------------- jfdctint.c

    const int ConstBits = 13, Pass1Bits = 2;
    const long Fix0_298631336 = 2446, Fix0_390180644 = 3196, Fix0_541196100 = 4433, Fix0_765366865 = 6270;
    const long Fix0_899976223 = 7373, Fix1_175875602 = 9633, Fix1_501321110 = 12299, Fix1_847759065 = 15137;
    const long Fix1_961570560 = 16069, Fix2_053119869 = 16819, Fix2_562915447 = 20995, Fix3_072711026 = 25172;

    static long Descale(long x, int n) => (x + (1L << (n - 1))) >> n;

    /// <summary>`jpeg_fdct_islow` (`jfdctint.c`): AAN-free LL&amp;M integer DCT, output scaled by 8.</summary>
    internal static void FdctIslow(long[] d)
    {
        // Pass 1: rows, results scaled up by 2^PASS1_BITS
        for (int c = 0, o = 0; c < 8; c++, o += 8)
        {
            long tmp0 = d[o + 0] + d[o + 7], tmp7 = d[o + 0] - d[o + 7];
            long tmp1 = d[o + 1] + d[o + 6], tmp6 = d[o + 1] - d[o + 6];
            long tmp2 = d[o + 2] + d[o + 5], tmp5 = d[o + 2] - d[o + 5];
            long tmp3 = d[o + 3] + d[o + 4], tmp4 = d[o + 3] - d[o + 4];

            long tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            d[o + 0] = (tmp10 + tmp11) << Pass1Bits;
            d[o + 4] = (tmp10 - tmp11) << Pass1Bits;

            long z1 = (tmp12 + tmp13) * Fix0_541196100;
            d[o + 2] = Descale(z1 + tmp13 * Fix0_765366865, ConstBits - Pass1Bits);
            d[o + 6] = Descale(z1 + tmp12 * -Fix1_847759065, ConstBits - Pass1Bits);

            z1 = tmp4 + tmp7; long z2 = tmp5 + tmp6, z3 = tmp4 + tmp6, z4 = tmp5 + tmp7;
            long z5 = (z3 + z4) * Fix1_175875602;

            tmp4 *= Fix0_298631336; tmp5 *= Fix2_053119869; tmp6 *= Fix3_072711026; tmp7 *= Fix1_501321110;
            z1 *= -Fix0_899976223; z2 *= -Fix2_562915447; z3 *= -Fix1_961570560; z4 *= -Fix0_390180644;
            z3 += z5; z4 += z5;

            d[o + 7] = Descale(tmp4 + z1 + z3, ConstBits - Pass1Bits);
            d[o + 5] = Descale(tmp5 + z2 + z4, ConstBits - Pass1Bits);
            d[o + 3] = Descale(tmp6 + z2 + z3, ConstBits - Pass1Bits);
            d[o + 1] = Descale(tmp7 + z1 + z4, ConstBits - Pass1Bits);
        }

        // Pass 2: columns, removing the PASS1_BITS scaling (overall factor 8 remains)
        for (int o = 0; o < 8; o++)
        {
            long tmp0 = d[o + 8 * 0] + d[o + 8 * 7], tmp7 = d[o + 8 * 0] - d[o + 8 * 7];
            long tmp1 = d[o + 8 * 1] + d[o + 8 * 6], tmp6 = d[o + 8 * 1] - d[o + 8 * 6];
            long tmp2 = d[o + 8 * 2] + d[o + 8 * 5], tmp5 = d[o + 8 * 2] - d[o + 8 * 5];
            long tmp3 = d[o + 8 * 3] + d[o + 8 * 4], tmp4 = d[o + 8 * 3] - d[o + 8 * 4];

            long tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            d[o + 8 * 0] = Descale(tmp10 + tmp11, Pass1Bits);
            d[o + 8 * 4] = Descale(tmp10 - tmp11, Pass1Bits);

            long z1 = (tmp12 + tmp13) * Fix0_541196100;
            d[o + 8 * 2] = Descale(z1 + tmp13 * Fix0_765366865, ConstBits + Pass1Bits);
            d[o + 8 * 6] = Descale(z1 + tmp12 * -Fix1_847759065, ConstBits + Pass1Bits);

            z1 = tmp4 + tmp7; long z2 = tmp5 + tmp6, z3 = tmp4 + tmp6, z4 = tmp5 + tmp7;
            long z5 = (z3 + z4) * Fix1_175875602;

            tmp4 *= Fix0_298631336; tmp5 *= Fix2_053119869; tmp6 *= Fix3_072711026; tmp7 *= Fix1_501321110;
            z1 *= -Fix0_899976223; z2 *= -Fix2_562915447; z3 *= -Fix1_961570560; z4 *= -Fix0_390180644;
            z3 += z5; z4 += z5;

            d[o + 8 * 7] = Descale(tmp4 + z1 + z3, ConstBits + Pass1Bits);
            d[o + 8 * 5] = Descale(tmp5 + z2 + z4, ConstBits + Pass1Bits);
            d[o + 8 * 3] = Descale(tmp6 + z2 + z3, ConstBits + Pass1Bits);
            d[o + 8 * 1] = Descale(tmp7 + z1 + z4, ConstBits + Pass1Bits);
        }
    }

    // ---------------------------------------------------------------- the compressor

    sealed class Component
    {
        public int Id, H, V, QuantTbl, DcTbl, AcTbl;
        public int WidthInBlocks, HeightInBlocks;
        public int McuWidth, McuHeight, LastColWidth, LastRowHeight;
        public byte[] Plane = Array.Empty<byte>();   // padded to WidthInBlocks*8 × (iMCU rows · V · 8)
        public int PlaneStride;
        public int LastDc;
    }

    Stream _s = Stream.Null;
    int _bitBuffer, _bitCount;

    /// <summary>An option set (the `{ quality, subsamplingId }` struct plus the markers cp.dll adds around it).</summary>
    public JpegEncoder() { }
    JpegEncoder(Stream s) { _s = s; }

    void Byte(int b) => _s.WriteByte((byte)b);
    void Word(int v) { Byte(v >> 8); Byte(v & 0xff); }
    void Marker(int m) { Byte(0xFF); Byte(m); }

    /// <summary>`emit_bits` — MSB first, `0xFF` byte-stuffed with a following `0x00`.</summary>
    void EmitBits(int code, int size)
    {
        if (size == 0) return;
        int put = _bitBuffer, cnt = _bitCount;
        put |= (code & ((1 << size) - 1)) << (24 - cnt - size);
        cnt += size;
        while (cnt >= 8)
        {
            int c = (put >> 16) & 0xFF;
            Byte(c);
            if (c == 0xFF) Byte(0);
            put = (put << 8) & 0xFFFFFF; cnt -= 8;
        }
        _bitBuffer = put; _bitCount = cnt;
    }

    /// <summary>`flush_bits`: pad the partial byte with 1 bits.</summary>
    void FlushBits() { EmitBits(0x7F, 7); _bitBuffer = 0; _bitCount = 0; }

    /// <summary>
    /// Encode one image. <paramref name="pixels"/> is either RGBX/RGBA (4 bytes per pixel, alpha ignored — the
    /// `JCS_EXT_RGBX` fast path at `0x1800fda2b` points libjpeg's `JSAMPARRAY` straight at the rows) or 8-bit
    /// grayscale (1 byte per pixel — `fmt &lt; 9` → `JCS_GRAYSCALE`, `input_components = 1`).
    /// </summary>
    public static void Encode(Stream stream, byte[] pixels, int width, int height, int strideBytes, bool grayscale,
                              JpegEncoder options)
    {
        if (width < 1 || height < 1) throw new ArgumentException("JPEG: empty image");
        var e = new JpegEncoder(stream)
        {
            Quality = options.Quality, SubsamplingId = options.SubsamplingId, Comment = options.Comment,
            ExifApp1 = options.ExifApp1, ExtraApp1 = options.ExtraApp1,
            DensityUnit = options.DensityUnit, XDensity = options.XDensity, YDensity = options.YDensity,
        };
        e.Run(pixels, width, height, strideBytes, grayscale);
    }

    void Run(byte[] pixels, int width, int height, int strideBytes, bool grayscale)
    {
        // ---- jpeg_set_colorspace + the subsampling map (`0x1800fd75a–944`)
        var comps = new List<Component>();
        if (grayscale) comps.Add(new Component { Id = 1, H = 1, V = 1, QuantTbl = 0, DcTbl = 0, AcTbl = 0 });
        else
        {
            var (h0, v0, h1, v1, h2, v2) = SubsamplingId switch
            {
                0 => (1, 1, 1, 1, 1, 1),   // 4:4:4
                1 => (2, 1, 1, 1, 1, 1),   // 4:2:2
                2 => (2, 2, 1, 1, 1, 1),   // 4:2:0
                _ => throw new ArgumentException("unknown subsampling id"),
            };
            comps.Add(new Component { Id = 1, H = h0, V = v0, QuantTbl = 0, DcTbl = 0, AcTbl = 0 });
            comps.Add(new Component { Id = 2, H = h1, V = v1, QuantTbl = 1, DcTbl = 1, AcTbl = 1 });
            comps.Add(new Component { Id = 3, H = h2, V = v2, QuantTbl = 1, DcTbl = 1, AcTbl = 1 });
        }
        int maxH = comps.Max(c => c.H), maxV = comps.Max(c => c.V);

        // ---- jcparam.c: quantization tables
        int scale = QualityScaling(Quality);
        var qtab = new List<int[]> { ScaleQuantTable(StdLuminance, scale) };
        if (!grayscale) qtab.Add(ScaleQuantTable(StdChrominance, scale));

        // ---- jcparam.c: the standard Huffman tables
        var dcTables = new[] { HuffTable.Make(BitsDcLuminance, ValDcLuminance), HuffTable.Make(BitsDcChrominance, ValDcChrominance) };
        var acTables = new[] { HuffTable.Make(BitsAcLuminance, ValAcLuminance), HuffTable.Make(BitsAcChrominance, ValAcChrominance) };

        // ---- jcmaster.c initial_setup
        int mcusPerRow = Ceil(width, maxH * 8), totalIMcuRows = Ceil(height, maxV * 8);
        foreach (var c in comps)
        {
            c.WidthInBlocks = Ceil(width * c.H, maxH * 8);
            c.HeightInBlocks = Ceil(height * c.V, maxV * 8);
            c.McuWidth = c.H; c.McuHeight = c.V;
            int t = c.WidthInBlocks % c.McuWidth; c.LastColWidth = t == 0 ? c.McuWidth : t;
            t = c.HeightInBlocks % c.McuHeight; c.LastRowHeight = t == 0 ? c.McuHeight : t;
        }

        BuildPlanes(comps, pixels, width, height, strideBytes, grayscale, maxH, maxV, totalIMcuRows);

        // ---- jcmarker.c write_file_header (jpeg_start_compress)
        Marker(0xD8);                                            // SOI
        // emit_jfif_app0 — write_JFIF_header is TRUE for both YCbCr and grayscale
        Marker(0xE0); Word(16);
        Byte('J'); Byte('F'); Byte('I'); Byte('F'); Byte(0);
        Byte(1); Byte(1);                                        // JFIF 1.1
        Byte(DensityUnit); Word(XDensity); Word(YDensity);
        Byte(0); Byte(0);                                        // no thumbnail

        // ---- the application markers, in cp.dll's order: COM, APP1(Exif), APP1(XMP)…
        if (!string.IsNullOrEmpty(Comment))
        {
            var b = Encoding.ASCII.GetBytes(Comment);
            Marker(0xFE); Word(b.Length + 2); _s.Write(b);
        }
        if (ExifApp1 is { Length: > 0 }) { Marker(0xE1); Word(ExifApp1.Length + 2); _s.Write(ExifApp1); }
        foreach (var app1 in ExtraApp1) { Marker(0xE1); Word(app1.Length + 2); _s.Write(app1); }

        // ---- write_frame_header (deferred to the first jpeg_write_scanlines)
        var sentQ = new bool[4];
        foreach (var c in comps)
            if (!sentQ[c.QuantTbl])
            {
                sentQ[c.QuantTbl] = true;
                Marker(0xDB); Word(64 + 1 + 2); Byte(c.QuantTbl);
                for (int i = 0; i < 64; i++) Byte(qtab[c.QuantTbl][NaturalOrder[i]]);
            }
        Marker(0xC0); Word(3 * comps.Count + 2 + 5 + 1);
        Byte(8); Word(height); Word(width); Byte(comps.Count);
        foreach (var c in comps) { Byte(c.Id); Byte((c.H << 4) + c.V); Byte(c.QuantTbl); }

        // ---- write_scan_header
        var sentDc = new bool[4]; var sentAc = new bool[4];
        foreach (var c in comps)
        {
            if (!sentDc[c.DcTbl]) { sentDc[c.DcTbl] = true; EmitDht(dcTables[c.DcTbl], c.DcTbl, false); }
            if (!sentAc[c.AcTbl]) { sentAc[c.AcTbl] = true; EmitDht(acTables[c.AcTbl], c.AcTbl, true); }
        }
        Marker(0xDA); Word(2 * comps.Count + 2 + 1 + 3);
        Byte(comps.Count);
        foreach (var c in comps) { Byte(c.Id); Byte((c.DcTbl << 4) + c.AcTbl); }
        Byte(0); Byte(63); Byte(0);

        // ---- jccoefct.c compress_data + jchuff.c encode_mcu_huff
        var divisors = qtab.Select(q => q.Select(v => (long)(v << 3)).ToArray()).ToList();
        var workspace = new long[64];
        var block = new int[64];
        int lastIMcuRow = totalIMcuRows - 1, lastMcuCol = mcusPerRow - 1;
        for (int iMcuRow = 0; iMcuRow < totalIMcuRows; iMcuRow++)
            for (int mcuCol = 0; mcuCol < mcusPerRow; mcuCol++)
                foreach (var c in comps)
                {
                    int blockcnt = mcuCol < lastMcuCol ? c.McuWidth : c.LastColWidth;
                    int xpos = mcuCol * c.McuWidth * 8;
                    var dc = dcTables[c.DcTbl]; var ac = acTables[c.AcTbl];
                    int ypos = 0;
                    for (int yindex = 0; yindex < c.McuHeight; yindex++)
                    {
                        bool real = iMcuRow < lastIMcuRow || yindex < c.LastRowHeight;
                        for (int bi = 0; bi < c.McuWidth; bi++)
                        {
                            if (real && bi < blockcnt)
                            {
                                Forward(c, iMcuRow * c.McuHeight * 8 + ypos, xpos + bi * 8, workspace, divisors[c.QuantTbl], block);
                                EncodeBlock(block, c, dc, ac);
                            }
                            else
                            {
                                // jccoefct.c: dummy blocks at the right (`blkn + bi − 1`) / bottom (`blkn − 1`) edge are all-zero
                                // except `[0][0] = the previous block's DC`. Since that previous block has just set the running
                                // predictor to the same value, the emitted DC difference is 0 and the predictor is unchanged —
                                // i.e. exactly "DC symbol 0 + EOB". (`bi = 0` is always a real block: `last_col_width ≥ 1`, and
                                // the bottom case cannot fire at `yindex = 0` because `last_row_height ≥ 1`.)
                                EmitBits(dc.Code[0], dc.Size[0]);
                                EmitBits(ac.Code[0], ac.Size[0]);
                            }
                        }
                        ypos += 8;
                    }
                }
        FlushBits();
        Marker(0xD9);   // EOI
    }

    /// <summary>The dummy-block DC rule of `jccoefct.c`: `MCU_buffer[blkn + bi][0][0] = MCU_buffer[blkn + bi − 1][0][0]`
    /// for the right edge, and `= MCU_buffer[blkn − 1][0][0]` (the last block of the previous row of this MCU) for the
    /// bottom edge. Both reduce to "the DC of the block encoded just before it in this component's MCU", which — since
    /// the DC of every block is the *difference* against the running predictor — makes the emitted difference 0.</summary>
    void EncodeBlock(int[] block, Component c, HuffTable dc, HuffTable ac)
    {
        int temp = block[0] - c.LastDc, temp2 = temp;
        c.LastDc = block[0];
        if (temp < 0) { temp = -temp; temp2--; }
        int nbits = 0;
        while (temp != 0) { nbits++; temp >>= 1; }
        EmitBits(dc.Code[nbits], dc.Size[nbits]);
        if (nbits != 0) EmitBits(temp2, nbits);

        int r = 0;
        for (int k = 1; k < 64; k++)
        {
            temp = block[NaturalOrder[k]];
            if (temp == 0) { r++; continue; }
            while (r > 15) { EmitBits(ac.Code[0xF0], ac.Size[0xF0]); r -= 16; }
            temp2 = temp;
            if (temp < 0) { temp = -temp; temp2--; }
            nbits = 1;
            while ((temp >>= 1) != 0) nbits++;
            int sym = (r << 4) + nbits;
            EmitBits(ac.Code[sym], ac.Size[sym]);
            EmitBits(temp2, nbits);
            r = 0;
        }
        if (r > 0) EmitBits(ac.Code[0], ac.Size[0]);
    }

    /// <summary>`jcdctmgr.c` `forward_DCT`: level-shift by −128, `jpeg_fdct_islow`, then `quantize` with
    /// `divisors[i] = quantval[i] &lt;&lt; 3` and round-half-away division of the magnitude.</summary>
    void Forward(Component c, int row, int col, long[] ws, long[] divisors, int[] outBlock)
    {
        for (int y = 0; y < 8; y++)
        {
            int o = (row + y) * c.PlaneStride + col;
            for (int x = 0; x < 8; x++) ws[y * 8 + x] = c.Plane[o + x] - 128;
        }
        FdctIslow(ws);
        for (int i = 0; i < 64; i++)
        {
            long t = ws[i], q = divisors[i];
            if (t < 0) { t = -t; t += q >> 1; t /= q; t = -t; }
            else { t += q >> 1; t /= q; }
            outBlock[i] = (int)(short)t;
        }
    }

    void EmitDht(HuffTable t, int index, bool isAc)
    {
        int len = 0; for (int i = 1; i <= 16; i++) len += t.Bits[i];
        Marker(0xC4); Word(len + 2 + 1 + 16);
        Byte(index + (isAc ? 0x10 : 0));
        for (int i = 1; i <= 16; i++) Byte(t.Bits[i]);
        for (int i = 0; i < len; i++) Byte(t.Values[i]);
    }

    static int Ceil(long a, long b) => (int)((a + b - 1) / b);

    /// <summary>
    /// `jccolor.c` + `jcsample.c` + `jcprepct.c`, materialised as whole planes. The edge rules libjpeg applies through
    /// its row-group buffers reduce exactly to clamped sampling of the source: `expand_right_edge` replicates column
    /// `W−1`, `expand_bottom_edge` (both the `prep` one and the iMCU-row one) replicates row `H−1`.
    /// </summary>
    void BuildPlanes(List<Component> comps, byte[] pixels, int width, int height, int strideBytes, bool grayscale,
                     int maxH, int maxV, int totalIMcuRows)
    {
        int px = grayscale ? 1 : 4;
        foreach (var c in comps)
        {
            c.PlaneStride = c.WidthInBlocks * 8;
            c.Plane = new byte[(long)c.PlaneStride * totalIMcuRows * c.V * 8];
        }
        if (grayscale)
        {   // grayscale_convert: the sample goes straight through
            var c = comps[0];
            int rows = totalIMcuRows * 8;
            for (int y = 0; y < rows; y++)
            {
                int sy = Math.Min(y, height - 1);
                int so = sy * strideBytes, dof = y * c.PlaneStride;
                for (int x = 0; x < c.PlaneStride; x++) c.Plane[dof + x] = pixels[so + Math.Min(x, width - 1)];
            }
            return;
        }

        // Full-resolution Y/Cb/Cr of the padded source (`rgb_ycc_convert` on the expanded rows).
        // the widest right-edge expansion any component's downsampler asks for (`expand_right_edge(…, output_cols · hf)`)
        int fullW = comps.Max(c => c.WidthInBlocks * 8 * (maxH / c.H));
        int fullH = totalIMcuRows * maxV * 8;
        var yPlane = new byte[(long)fullW * fullH];
        var cbPlane = new byte[(long)fullW * fullH];
        var crPlane = new byte[(long)fullW * fullH];
        var tab = RgbYccTab;
        for (int y = 0; y < fullH; y++)
        {
            int sy = Math.Min(y, height - 1);
            long so = (long)sy * strideBytes, dof = (long)y * fullW;
            for (int x = 0; x < fullW; x++)
            {
                long sx = so + (long)Math.Min(x, width - 1) * px;
                int r = pixels[sx], g = pixels[sx + 1], b = pixels[sx + 2];
                yPlane[dof + x] = (byte)((tab[r + 0 * 256] + tab[g + 1 * 256] + tab[b + 2 * 256]) >> ScaleBits);
                cbPlane[dof + x] = (byte)((tab[r + 3 * 256] + tab[g + 4 * 256] + tab[b + 5 * 256]) >> ScaleBits);
                crPlane[dof + x] = (byte)((tab[r + 5 * 256] + tab[g + 6 * 256] + tab[b + 7 * 256]) >> ScaleBits);
            }
        }

        for (int ci = 0; ci < comps.Count; ci++)
        {
            var c = comps[ci];
            var src = ci == 0 ? yPlane : ci == 1 ? cbPlane : crPlane;
            int rows = totalIMcuRows * c.V * 8, cols = c.PlaneStride;
            int hf = maxH / c.H, vf = maxV / c.V;
            if (hf == 1 && vf == 1)
            {   // fullsize_downsample: jcopy_sample_rows then expand_right_edge
                for (int y = 0; y < rows; y++)
                    Array.Copy(src, (long)y * fullW, c.Plane, (long)y * cols, cols);
            }
            else if (hf == 2 && vf == 2)
            {   // h2v2_downsample: 2×2 box with the alternating bias 1,2.
                // The bottom edge is subtle: `jcprepct.c` pads the *row groups* it actually converted
                // (`expand_bottom_edge(output_buf[ci], …, out_row_group_ctr·v_samp, out_row_groups_avail·v_samp)`),
                // so rows past `ceil(H/2)` replicate the **downsampled** row `ceil(H/2) − 1` — which is the average of
                // two DIFFERENT luma rows — and NOT `h2v2(row H−1, row H−1)`. (The two coincide only when H is odd or
                // a multiple of 16, which is why the bug hides on most test sizes.)
                int realRows = (height + 1) / 2;
                for (int y = 0; y < rows; y++)
                {
                    int sy = Math.Min(y, realRows - 1);
                    long r0 = (long)(sy * 2) * fullW, r1 = (long)(sy * 2 + 1) * fullW, dof = (long)y * cols;
                    int bias = 1;
                    for (int x = 0; x < cols; x++)
                    {
                        int i0 = x * 2;
                        c.Plane[dof + x] = (byte)((src[r0 + i0] + src[r0 + i0 + 1] + src[r1 + i0] + src[r1 + i0 + 1] + bias) >> 2);
                        bias ^= 3;
                    }
                }
            }
            else if (hf == 2 && vf == 1)
            {   // h2v1_downsample: 1×2 box, bias 0,1 alternating
                for (int y = 0; y < rows; y++)
                {
                    long r0 = (long)y * fullW, dof = (long)y * cols;
                    int bias = 0;
                    for (int x = 0; x < cols; x++)
                    {
                        int i0 = x * 2;
                        c.Plane[dof + x] = (byte)((src[r0 + i0] + src[r0 + i0 + 1] + bias) >> 1);
                        bias ^= 1;
                    }
                }
            }
            else throw new NotSupportedException($"downsample {hf}x{vf} not ported");
        }
    }
}
