namespace Lux.Engine.Lri;

/// <summary>
/// The JPEG **decoder** cp.dll links statically — the same **libjpeg-turbo** build as the encoder port
/// (<see cref="Pipeline.Export.JpegEncoder"/>; the copyright banner at file offset 0x7325a0 of `cp.dll` reads
/// `Copyright (C) 1991-2015 The libjpeg-turbo Project and many others` / API `6b`), reached through the
/// **TurboJPEG** wrapper: the `.jpg` entry of the image-codec registry `FUN_18001c410` (extension `".jpg"`
/// `DAT_180682a25`, vtable `0x180682d38`) has load slot `FUN_1800b7190`, which is
/// <c>tjInitDecompress</c> (`0x180578030`) → <c>tjDecompressHeader3</c> (`0x180578160`) →
/// <c>tjDecompress2</c> (`0x180578470`) → <c>tjDestroy</c> (`0x180577730`).
///
/// <para>The two decode parameters are pinned by that call:</para>
/// <list type="bullet">
/// <item><b>flags = 0x1000</b> (`mov DWORD PTR [rsp+0x40],0x1000` at `0x1800b7329`) = <c>TJFLAG_ACCURATEDCT</c>, i.e.
///   `dinfo.dct_method = JDCT_ISLOW` — the `jidctint.c` integer IDCT. Both implementations are in this build: the C
///   `jpeg_idct_islow` at `0x1805b2ca0` (the `imul …,0x3b21` / `…,0x6254` constants 15137 / 25172) and the SSE2 one,
///   whose `jconst_idct_islow_sse2` table sits at `0x1807109f0` (`{10703,4433} {4433,−10704} {−6436,9633}
///   {9633,6437} {−4927,−7373} {−7373,4926}`, each `times 4 dw`), so `jsimd_can_idct_islow` succeeds and the SSE2
///   kernel is what actually runs. libjpeg-turbo's contract is that the two are bit-identical — the port below is the
///   C one, and it reproduces a libjpeg-turbo-with-SIMD decode sample for sample on 48 real surface planes.</item>
/// <item><b>pixelFormat = 6</b> = <c>TJPF_GRAY</c> (`mov r13d,0x6` at `0x1800b7289`), taken when the destination
///   `lt::Image` has 1 byte per pixel and the stream's subsampling is `TJSAMP_GRAY` — which is what every Bayer-JPEG
///   plane is. TurboJPEG then sets `out_color_space = JCS_GRAYSCALE` and decompresses straight into the image rows,
///   with no upsampling and no colour conversion. That is the only shape this class implements; anything else in a
///   `.lri` surface would be a file we have never seen, so it throws rather than guessing.</item>
/// </list>
///
/// <para>Reused from the encoder port: <see cref="Pipeline.Export.JpegEncoder.NaturalOrder"/> (`jpeg_natural_order`)
/// and the `jfdctint.c` constant set, which `jidctint.c` shares. Everything else here is the inverse of what
/// `JpegEncoder` already writes — `jdmarker.c` parsing, `jdhuff.c` decoding and `jddctmgr.c`/`jidctint.c`.</para>
/// </summary>
public static class BaselineJpegDecoder
{
    // ---------------------------------------------------------------- jidctint.c constants (shared with jfdctint.c)

    const int ConstBits = 13, Pass1Bits = 2;
    const int Fix0_298631336 = 2446, Fix0_390180644 = 3196, Fix0_541196100 = 4433, Fix0_765366865 = 6270;
    const int Fix0_899976223 = 7373, Fix1_175875602 = 9633, Fix1_501321110 = 12299, Fix1_847759065 = 15137;
    const int Fix1_961570560 = 16069, Fix2_053119869 = 16819, Fix2_562915447 = 20995, Fix3_072711026 = 25172;

    /// <summary>`DESCALE(x, n)` — round-half-up then arithmetic shift.</summary>
    static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;

    /// <summary>`jdmaster.c prepare_range_limit_table`, as the IDCT sees it (`IDCT_range_limit` = table + CENTERJSAMPLE):
    /// `P[k]` for `k = v &amp; 1023` gives `v + 128` for `v ∈ [−128, 127]`, `255` for `v ∈ [128, 511]` and `0` for
    /// `v ∈ [−512, −129]`. Built exactly the way libjpeg lays the five segments out, not by clamping, so the
    /// out-of-range wraparound of a corrupt stream would match too.</summary>
    static readonly byte[] IdctRangeLimit = BuildIdctRangeLimit();

    static byte[] BuildIdctRangeLimit()
    {
        const int max = 255, centre = 128;                                  // MAXJSAMPLE, CENTERJSAMPLE
        var raw = new byte[5 * (max + 1) + centre];                         // the allocation, index 0 = subscript −256
        const int limit = max + 1;                                          // sample_range_limit = raw + 256
        for (int i = 0; i < limit; i++) raw[i] = 0;                         // MEMZERO(table − 256, 256)
        for (int i = 0; i <= max; i++) raw[limit + i] = (byte)i;            // table[0…255] = i
        int t2 = limit + centre;                                            // post-IDCT table
        for (int i = centre; i < 2 * (max + 1); i++) raw[t2 + i] = max;
        for (int i = 0; i < 2 * (max + 1) - centre; i++) raw[t2 + 2 * (max + 1) + i] = 0;
        for (int i = 0; i < centre; i++) raw[t2 + 4 * (max + 1) - centre + i] = raw[limit + i];
        var p = new byte[1024];
        Array.Copy(raw, t2, p, 0, 1024);
        return p;
    }

    // ---------------------------------------------------------------- jdhuff.c

    /// <summary>`d_derived_tbl` — `jpeg_make_d_derived_tbl`'s `maxcode` / `valoffset` / `pub->huffval`.</summary>
    sealed class DerivedTable
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public byte[] HuffVal = Array.Empty<byte>();

        public static DerivedTable Make(byte[] bits, byte[] huffval)
        {
            var t = new DerivedTable { HuffVal = huffval };
            var huffsize = new int[257]; var huffcode = new int[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (bits[l] < 0 || p + bits[l] > 256) throw new InvalidDataException("JPEG: bogus Huffman table definition");
                for (int i = 1; i <= bits[l]; i++) huffsize[p++] = l;
            }
            huffsize[p] = 0;
            int code = 0, si = huffsize[0]; p = 0;
            while (huffsize[p] != 0)
            {
                while (huffsize[p] == si) { huffcode[p++] = code; code++; }
                if (code >= (1 << si)) throw new InvalidDataException("JPEG: bogus Huffman table definition");
                code <<= 1; si++;
            }
            p = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (bits[l] != 0)
                {
                    t.ValOffset[l] = p - huffcode[p];
                    p += bits[l];
                    t.MaxCode[l] = huffcode[p - 1];
                }
                else t.MaxCode[l] = -1;
            }
            t.ValOffset[17] = 0; t.MaxCode[17] = 0xFFFFF;
            return t;
        }
    }

    /// <summary>`jdhuff.c`'s bit buffer: MSB first, `0xFF 0x00` unstuffed to a data `0xFF`, and any real marker ends the
    /// entropy segment (libjpeg then feeds zero bits, which is what <see cref="Bit"/> does past the end).</summary>
    sealed class BitReader
    {
        readonly byte[] _d; int _p; readonly int _end;
        int _buf, _bits;
        public bool HitMarker;
        public BitReader(byte[] d, int pos, int end) { _d = d; _p = pos; _end = end; }
        public int Position => _p;

        int NextByte()
        {
            if (_p >= _end) { HitMarker = true; return -1; }
            int b = _d[_p++];
            if (b != 0xFF) return b;
            // skip fill bytes, then look at the marker
            int c = _p < _end ? _d[_p] : 0xD9;
            while (c == 0xFF) { _p++; c = _p < _end ? _d[_p] : 0xD9; }
            if (c == 0x00) { _p++; return 0xFF; }              // stuffed data byte
            _p--;                                              // leave the 0xFF for the marker scanner
            HitMarker = true; return -1;
        }

        public int Bit()
        {
            if (_bits == 0)
            {
                int b = NextByte();
                if (b < 0) return 0;                           // past the marker: libjpeg supplies zero bits
                _buf = b; _bits = 8;
            }
            _bits--;
            return (_buf >> _bits) & 1;
        }

        public int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v = (v << 1) | Bit(); return v; }

        /// <summary>`RESTART` — drop the partial byte and step over the RSTn marker.</summary>
        public void Restart()
        {
            _bits = 0; HitMarker = false;
            while (_p + 1 < _end && !(_d[_p] == 0xFF && _d[_p + 1] >= 0xD0 && _d[_p + 1] <= 0xD7)) _p++;
            if (_p + 1 < _end) _p += 2;
        }

        public int Decode(DerivedTable t)
        {
            int l = 1, code = Bit();
            while (code > t.MaxCode[l]) { code = (code << 1) | Bit(); l++; if (l > 16) return 0; }
            return t.HuffVal[code + t.ValOffset[l]];
        }
    }

    /// <summary>`HUFF_EXTEND(x, s)`.</summary>
    static int Extend(int x, int s) => x < (1 << (s - 1)) ? x + ((-1 << s) + 1) : x;

    // ---------------------------------------------------------------- the decoder

    /// <summary>
    /// Decode one **baseline, single-component (grayscale)** JPEG into 8-bit samples, row-major, stride =
    /// <paramref name="width"/>. This is `tjDecompress2(handle, buf, size, dst, w, pitch, h, TJPF_GRAY,
    /// TJFLAG_ACCURATEDCT)` for the case cp.dll's Bayer-JPEG surface decoder always hits.
    /// </summary>
    public static byte[] DecodeGray(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        var d = jpeg.ToArray();
        int p = 0;
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new InvalidDataException("JPEG: not a JPEG (no SOI)");
        p = 2;

        var quant = new int[4][];
        var dcTbl = new DerivedTable?[4];
        var acTbl = new DerivedTable?[4];
        int w = 0, h = 0, nComp = 0, compId = 0, compQ = 0, compH = 0, compV = 0;
        int restartInterval = 0;
        bool haveSof = false;

        while (p + 1 < d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            int marker = d[p + 1];
            p += 2;
            if (marker == 0xFF) { p--; continue; }                       // fill byte
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;   // TEM / RSTn: no payload
            if (marker == 0xD9) break;                                   // EOI
            if (p + 2 > d.Length) throw new InvalidDataException("JPEG: truncated marker");
            int len = (d[p] << 8) | d[p + 1];
            int seg = p + 2, segEnd = p + len;
            if (len < 2 || segEnd > d.Length) throw new InvalidDataException("JPEG: bad marker length");

            switch (marker)
            {
                case 0xDB:                                               // DQT
                    while (seg < segEnd)
                    {
                        int pq = d[seg] >> 4, tq = d[seg] & 15; seg++;
                        if (tq > 3) throw new InvalidDataException("JPEG: bogus DQT table id");
                        var q = new int[64];
                        for (int i = 0; i < 64; i++)
                        {
                            int v = pq != 0 ? (d[seg] << 8) | d[seg + 1] : d[seg];
                            seg += pq != 0 ? 2 : 1;
                            q[Pipeline.Export.JpegEncoder.NaturalOrder[i]] = v;   // stored in natural order
                        }
                        quant[tq] = q;
                    }
                    break;

                case 0xC4:                                               // DHT
                    while (seg < segEnd)
                    {
                        int tc = d[seg] >> 4, th = d[seg] & 15; seg++;
                        if (th > 3 || tc > 1) throw new InvalidDataException("JPEG: bogus DHT table id");
                        var bits = new byte[17]; int count = 0;
                        for (int i = 1; i <= 16; i++) { bits[i] = d[seg + i - 1]; count += bits[i]; }
                        seg += 16;
                        if (count > 256 || seg + count > segEnd) throw new InvalidDataException("JPEG: bogus Huffman table definition");
                        var vals = new byte[count];
                        Array.Copy(d, seg, vals, 0, count); seg += count;
                        if (tc == 0) dcTbl[th] = DerivedTable.Make(bits, vals); else acTbl[th] = DerivedTable.Make(bits, vals);
                    }
                    break;

                case 0xDD:                                               // DRI
                    restartInterval = (d[seg] << 8) | d[seg + 1];
                    break;

                case 0xC0: case 0xC1:                                    // SOF0 baseline / SOF1 extended sequential
                    if (d[seg] != 8) throw new NotSupportedException($"JPEG: {d[seg]}-bit samples are not supported");
                    h = (d[seg + 1] << 8) | d[seg + 2];
                    w = (d[seg + 3] << 8) | d[seg + 4];
                    nComp = d[seg + 5];
                    if (nComp != 1)
                        throw new NotSupportedException($"JPEG: {nComp} components — cp.dll's Bayer-JPEG surfaces are always TJSAMP_GRAY");
                    compId = d[seg + 6]; compH = d[seg + 7] >> 4; compV = d[seg + 7] & 15; compQ = d[seg + 8];
                    if (compH != 1 || compV != 1) throw new NotSupportedException("JPEG: subsampled single-component frame");
                    haveSof = true;
                    break;

                case 0xC2: case 0xC3: case 0xC5: case 0xC6: case 0xC7:
                case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    throw new NotSupportedException($"JPEG: unsupported process (SOF marker 0x{marker:X2}); libjpeg's baseline path is the only one cp.dll's surfaces use");

                case 0xDA:                                               // SOS — entropy-coded data follows
                {
                    if (!haveSof) throw new InvalidDataException("JPEG: SOS before SOF");
                    int ns = d[seg];
                    if (ns != 1) throw new NotSupportedException("JPEG: multi-component scan");
                    if (d[seg + 1] != compId) throw new InvalidDataException("JPEG: SOS names an unknown component");
                    int dcSel = d[seg + 2] >> 4, acSel = d[seg + 2] & 15;
                    var dt = dcTbl[dcSel] ?? throw new InvalidDataException("JPEG: DC table not defined");
                    var at = acTbl[acSel] ?? throw new InvalidDataException("JPEG: AC table not defined");
                    var qt = quant[compQ] ?? throw new InvalidDataException("JPEG: quantization table not defined");
                    width = w; height = h;
                    return Scan(d, segEnd, w, h, qt, dt, at, restartInterval);
                }

                default:                                                 // APPn, COM, anything else: skipped
                    break;
            }
            p = segEnd;
        }
        throw new InvalidDataException("JPEG: no scan found");
    }

    /// <summary>The non-interleaved single-component scan: one 8×8 block per MCU, `width_in_blocks` per row
    /// (`jdcoefct.c decompress_onepass` with `MCU_ctr` running over `compptr->width_in_blocks`).</summary>
    static byte[] Scan(byte[] d, int pos, int w, int h, int[] quant, DerivedTable dc, DerivedTable ac, int restartInterval)
    {
        int bw = (w + 7) / 8, bh = (h + 7) / 8;
        var outp = new byte[(long)w * h];
        var block = new int[64];
        var sample = new byte[64];
        var br = new BitReader(d, pos, d.Length);
        int lastDc = 0, toRestart = restartInterval;

        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                if (restartInterval != 0 && toRestart == 0) { br.Restart(); lastDc = 0; toRestart = restartInterval; }
                if (restartInterval != 0) toRestart--;

                Array.Clear(block);
                // ---- jdhuff.c decode_mcu (baseline)
                int t = br.Decode(dc);
                int s = t != 0 ? Extend(br.Bits(t), t) : 0;
                s += lastDc; lastDc = s;
                block[0] = s;
                for (int k = 1; k < 64; k++)
                {
                    int rs = br.Decode(ac);
                    int r = rs >> 4; s = rs & 15;
                    if (s != 0)
                    {
                        k += r;
                        // libjpeg pads `jpeg_natural_order` past 63 with 63, so a corrupt run length lands on the last
                        // coefficient and the `k < 64` test then ends the block. Same effect, without the padded table.
                        block[Pipeline.Export.JpegEncoder.NaturalOrder[Math.Min(k, 63)]] = Extend(br.Bits(s), s);
                    }
                    else
                    {
                        if (r != 15) break;
                        k += 15;
                    }
                }

                IdctIslow(block, quant, sample);

                int x0 = bx * 8, y0 = by * 8;
                int nx = Math.Min(8, w - x0), ny = Math.Min(8, h - y0);
                for (int y = 0; y < ny; y++)
                    Array.Copy(sample, y * 8, outp, (long)(y0 + y) * w + x0, nx);
            }
        }
        return outp;
    }

    /// <summary>`jidctint.c jpeg_idct_islow`, transcribed. `quant` is `jddctmgr.c`'s ISLOW multiplier table, which for
    /// ISLOW is the quantization table itself (`ISLOW_MULT_TYPE` = the raw `quantval`).</summary>
    static void IdctIslow(int[] coef, int[] quant, byte[] outp)
    {
        Span<int> ws = stackalloc int[64];

        // Pass 1: process columns from the input, store into the work array; results scaled up by PASS1_BITS.
        for (int c = 0; c < 8; c++)
        {
            if (coef[c + 8 * 1] == 0 && coef[c + 8 * 2] == 0 && coef[c + 8 * 3] == 0 && coef[c + 8 * 4] == 0 &&
                coef[c + 8 * 5] == 0 && coef[c + 8 * 6] == 0 && coef[c + 8 * 7] == 0)
            {
                int dcval = (coef[c] * quant[c]) << Pass1Bits;
                for (int r = 0; r < 8; r++) ws[c + 8 * r] = dcval;
                continue;
            }

            int z2 = coef[c + 8 * 2] * quant[c + 8 * 2];
            int z3 = coef[c + 8 * 6] * quant[c + 8 * 6];
            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 + z3 * -Fix1_847759065;
            int tmp3 = z1 + z2 * Fix0_765366865;

            z2 = coef[c + 8 * 0] * quant[c + 8 * 0];
            z3 = coef[c + 8 * 4] * quant[c + 8 * 4];
            int tmp0 = (z2 + z3) << ConstBits;
            int tmp1 = (z2 - z3) << ConstBits;

            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            tmp0 = coef[c + 8 * 7] * quant[c + 8 * 7];
            tmp1 = coef[c + 8 * 5] * quant[c + 8 * 5];
            tmp2 = coef[c + 8 * 3] * quant[c + 8 * 3];
            tmp3 = coef[c + 8 * 1] * quant[c + 8 * 1];

            z1 = tmp0 + tmp3; z2 = tmp1 + tmp2; z3 = tmp0 + tmp2; int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * Fix1_175875602;

            tmp0 *= Fix0_298631336; tmp1 *= Fix2_053119869; tmp2 *= Fix3_072711026; tmp3 *= Fix1_501321110;
            z1 *= -Fix0_899976223; z2 *= -Fix2_562915447; z3 *= -Fix1_961570560; z4 *= -Fix0_390180644;
            z3 += z5; z4 += z5;
            tmp0 += z1 + z3; tmp1 += z2 + z4; tmp2 += z2 + z3; tmp3 += z1 + z4;

            ws[c + 8 * 0] = Descale(tmp10 + tmp3, ConstBits - Pass1Bits);
            ws[c + 8 * 7] = Descale(tmp10 - tmp3, ConstBits - Pass1Bits);
            ws[c + 8 * 1] = Descale(tmp11 + tmp2, ConstBits - Pass1Bits);
            ws[c + 8 * 6] = Descale(tmp11 - tmp2, ConstBits - Pass1Bits);
            ws[c + 8 * 2] = Descale(tmp12 + tmp1, ConstBits - Pass1Bits);
            ws[c + 8 * 5] = Descale(tmp12 - tmp1, ConstBits - Pass1Bits);
            ws[c + 8 * 3] = Descale(tmp13 + tmp0, ConstBits - Pass1Bits);
            ws[c + 8 * 4] = Descale(tmp13 - tmp0, ConstBits - Pass1Bits);
        }

        // Pass 2: process rows from the work array, store into the output.
        for (int r = 0; r < 8; r++)
        {
            int o = r * 8;
            if (ws[o + 1] == 0 && ws[o + 2] == 0 && ws[o + 3] == 0 && ws[o + 4] == 0 &&
                ws[o + 5] == 0 && ws[o + 6] == 0 && ws[o + 7] == 0)
            {
                byte dcval = IdctRangeLimit[Descale(ws[o], Pass1Bits + 3) & 1023];
                for (int i = 0; i < 8; i++) outp[o + i] = dcval;
                continue;
            }

            int z2 = ws[o + 2], z3 = ws[o + 6];
            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 + z3 * -Fix1_847759065;
            int tmp3 = z1 + z2 * Fix0_765366865;

            int tmp0 = (ws[o + 0] + ws[o + 4]) << ConstBits;
            int tmp1 = (ws[o + 0] - ws[o + 4]) << ConstBits;

            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            tmp0 = ws[o + 7]; tmp1 = ws[o + 5]; tmp2 = ws[o + 3]; tmp3 = ws[o + 1];

            z1 = tmp0 + tmp3; z2 = tmp1 + tmp2; z3 = tmp0 + tmp2; int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * Fix1_175875602;

            tmp0 *= Fix0_298631336; tmp1 *= Fix2_053119869; tmp2 *= Fix3_072711026; tmp3 *= Fix1_501321110;
            z1 *= -Fix0_899976223; z2 *= -Fix2_562915447; z3 *= -Fix1_961570560; z4 *= -Fix0_390180644;
            z3 += z5; z4 += z5;
            tmp0 += z1 + z3; tmp1 += z2 + z4; tmp2 += z2 + z3; tmp3 += z1 + z4;

            outp[o + 0] = IdctRangeLimit[Descale(tmp10 + tmp3, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 7] = IdctRangeLimit[Descale(tmp10 - tmp3, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 1] = IdctRangeLimit[Descale(tmp11 + tmp2, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 6] = IdctRangeLimit[Descale(tmp11 - tmp2, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 2] = IdctRangeLimit[Descale(tmp12 + tmp1, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 5] = IdctRangeLimit[Descale(tmp12 - tmp1, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 3] = IdctRangeLimit[Descale(tmp13 + tmp0, ConstBits + Pass1Bits + 3) & 1023];
            outp[o + 4] = IdctRangeLimit[Descale(tmp13 - tmp0, ConstBits + Pass1Bits + 3) & 1023];
        }
    }
}
