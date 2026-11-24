using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Lumen's 3-component 16-bit lossless-JPEG tile encoder (constructor, encode, Huffman build, SOF3, DHT, SOS): predictor 1
/// (Ra; the first pixel of a row
/// predicts from Rb, the very first from 32768), per-component per-tile OPTIMAL Huffman tables (JPEG Annex K.2 with the
/// reserved code point, ties → last index, the K.3 length limit applied to the longest length only), MSB-first bit packing
/// with 0xFF stuffing and 1-padding of the final byte. Input = `Vec4&lt;u16&gt;` pixels (4 lanes per pixel, 3 encoded).
/// </summary>
public static class LosslessJpeg
{
    /// <summary>Category of |diff| for |diff| &lt; 256 (`cat = tbl[v >> 8] + 8` above).</summary>
    static readonly int[] CatTable = BuildCat();
    static int[] BuildCat()
    {
        var t = new int[256]; t[0] = 0; t[1] = 1; t[2] = 2; t[3] = 2;
        for (int i = 4; i < 8; i++) t[i] = 3;
        for (int i = 8; i < 16; i++) t[i] = 4;
        for (int i = 16; i < 32; i++) t[i] = 5;
        for (int i = 32; i < 64; i++) t[i] = 6;
        for (int i = 64; i < 128; i++) t[i] = 7;
        for (int i = 128; i < 256; i++) t[i] = 8;
        return t;
    }
    static int Category(int diff)
    {
        int s = diff >> 31; uint a = (uint)((diff ^ s) - s);
        return a < 256 ? CatTable[a] : CatTable[a >> 8] + 8;
    }

    public sealed class HuffTable
    {
        public byte[] Bits = new byte[16];   // counts for lengths 1..16 (DHT order)
        public List<byte> Vals = new();       // symbols by increasing code length, then symbol
        public int[] Code = new int[17], Size = new int[17];
    }

    /// <summary>The optimal Huffman table of one component (freq[17]).</summary>
    public static HuffTable BuildTable(long[] freq)
    {
        var syms = new List<int>(); var f = new List<long>();
        for (int s = 0; s < freq.Length; s++) if (freq[s] != 0) { syms.Add(s); f.Add(freq[s]); }
        f.Add(1);   // the reserved code point (K.2)
        int n = f.Count; var codesize = new int[n]; var others = new int[n]; Array.Fill(others, -1);
        while (true)
        {
            int v1 = -1;
            for (int i = 0; i < n; i++) if (f[i] != 0 && (v1 < 0 || f[i] <= f[v1])) v1 = i;
            if (v1 == -1) break;
            int v2 = -1;
            for (int i = 0; i < n; i++) if (f[i] != 0 && i != v1 && (v2 < 0 || f[i] <= f[v2])) v2 = i;
            if (v2 == -1) break;
            f[v1] += f[v2]; f[v2] = 0;
            int k = v1; while (true) { codesize[k]++; if (others[k] == -1) break; k = others[k]; }
            others[k] = v2;
            k = v2; while (true) { codesize[k]++; if (others[k] == -1) break; k = others[k]; }
        }
        int max = 0; for (int i = 0; i < n; i++) if (codesize[i] > max) max = codesize[i];
        var bits = new int[max + 1];
        for (int i = 0; i < n; i++) if (codesize[i] != 0) bits[codesize[i]]++;
        if (max >= 16)
        {   // K.3 on the longest length only (Lumen handles I = max, not the 32..17 walk)
            while (bits[max] > 0)
            {
                int j = max - 2;
                while (j > 0 && bits[j] == 0) j--;
                bits[max] -= 2; bits[max - 1] += 1; bits[j + 1] += 2; bits[j] -= 1;
            }
        }
        int nReal = n - 1;   // drop the reserved entry
        for (int i = bits.Length - 1; i >= 0; i--) if (bits[i] > 0) { bits[i]--; break; }
        var sortedIdx = new List<int>(); var sortedSize = new List<int>();
        for (int size = 1; size < bits.Length; size++)
            for (int i = 0; i < nReal; i++) if (codesize[i] == size) { sortedIdx.Add(i); sortedSize.Add(codesize[i]); }
        if (sortedIdx.Count != nReal) throw new InvalidOperationException("Symbol sort error");
        var t = new HuffTable();
        int code = 0, si = sortedSize.Count > 0 ? sortedSize[0] : 0, kk = 0;
        while (kk < sortedIdx.Count)
        {
            while (kk < sortedIdx.Count && sortedSize[kk] == si) { int sym = syms[sortedIdx[kk]]; t.Code[sym] = code; t.Size[sym] = si; t.Vals.Add((byte)sym); code++; kk++; }
            if (kk >= sortedIdx.Count) break;
            do { code <<= 1; si++; } while (sortedSize[kk] != si);
        }
        for (int l = 1; l <= 16; l++) t.Bits[l - 1] = (byte)(l < bits.Length ? bits[l] : 0);
        return t;
    }

    /// <summary>Encode a `w×h` tile of `Vec4&lt;u16&gt;` pixels (`stride` in u16 units) as the complete JPEG stream (SOI … EOI).</summary>
    public static void Encode(Stream s, ushort[] img, int w, int h, int stride, int offset = 0) => s.Write(Encode(img, w, h, stride, offset));

    /// <summary><see cref="Encode(Stream, ushort[], int, int, int, int)"/> into a new array that holds exactly the stream.</summary>
    public static byte[] Encode(ushort[] img, int w, int h, int stride, int offset = 0)
    {
        var freq = new long[3][]; for (int c = 0; c < 3; c++) freq[c] = new long[17];
        for (int y = 0; y < h; y++)
        {
            int p0, p1, p2;
            if (y == 0) { p0 = p1 = p2 = -0x8000; } else { int o = offset + (y - 1) * stride; p0 = (short)img[o]; p1 = (short)img[o + 1]; p2 = (short)img[o + 2]; }
            for (int x = 0; x < w; x++)
            {
                int o = offset + y * stride + x * 4; int c0 = (short)img[o], c1 = (short)img[o + 1], c2 = (short)img[o + 2];
                freq[0][Category((short)(c0 - p0))]++; freq[1][Category((short)(c1 - p1))]++; freq[2][Category((short)(c2 - p2))]++;
                p0 = c0; p1 = c1; p2 = c2;
            }
        }
        var tables = new HuffTable[3]; for (int c = 0; c < 3; c++) tables[c] = BuildTable(freq[c]);
        // The exact length of the coded data: per category, its count times its code length plus its value bits.
        long bits = 0;
        for (int c = 0; c < 3; c++)
            for (int cat = 0; cat < 17; cat++) bits += freq[c][cat] * (tables[c].Size[cat] + ((cat | 0x10) != 0x10 ? cat : 0));
        long dataBytes = (bits + 7) / 8;
        // the headers, then the coded data with a 0x00 after each 0xFF byte (at most doubles it), then EOI
        long cap = 256 + 2 * dataBytes + 2;
        if (cap > Array.MaxLength) throw new ArgumentException("tile too large for one lossless JPEG stream");
        var buf = ScratchPool.Rent<byte>((int)cap);
        try
        {
            int pos = 0;
            void Put(ReadOnlySpan<byte> b) { b.CopyTo(buf.AsSpan(pos)); pos += b.Length; }
            Put([0xff, 0xd8]);                                                                        // SOI
            Put([0xff, 0xc3, 0, 0x11, 0x10, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3]);   // SOF3
            for (int c = 0; c < 3; c++) Put([(byte)c, 0x11, 0]);
            for (int c = 0; c < 3; c++)
            {   // DHT
                var t = tables[c]; int len = t.Vals.Count + 19;
                Put([0xff, 0xc4, (byte)(len >> 8), (byte)len, (byte)(c & 3)]);
                Put(t.Bits.AsSpan(0, 16));
                foreach (var v in t.Vals) buf[pos++] = v;
            }
            Put([0xff, 0xda, 0, 0x0c, 3]);                                                           // SOS
            for (int c = 0; c < 3; c++) Put([(byte)c, (byte)((c & 3) << 4)]);
            Put([1, 0, 0]);
            // each category's code and code length, packed as code << 8 | length
            var cs = new int[3 * 17];
            for (int c = 0; c < 3; c++) for (int cat = 0; cat < 17; cat++) cs[c * 17 + cat] = (tables[c].Code[cat] << 8) | tables[c].Size[cat];
            var bw = new BitBuffer(buf, pos);
            for (int y = 0; y < h; y++)
            {
                int p0, p1, p2;
                if (y == 0) { p0 = p1 = p2 = 0x8000; } else { int o = offset + (y - 1) * stride; p0 = img[o]; p1 = img[o + 1]; p2 = img[o + 2]; }
                var row = img.AsSpan(offset + y * stride, Math.Max(0, w * 4 - 1));   // lanes 0 to 2 of each pixel
                for (int x = 0; x < row.Length; x += 4)
                {
                    int c0 = row[x], c1 = row[x + 1], c2 = row[x + 2];
                    bw.Diff(cs, 0, (short)(c0 - p0));
                    bw.Diff(cs, 17, (short)(c1 - p1));
                    bw.Diff(cs, 34, (short)(c2 - p2));
                    p0 = c0; p1 = c1; p2 = c2;
                }
            }
            pos = bw.Finish();
            buf[pos++] = 0xff; buf[pos++] = 0xd9;                                                    // EOI
            return buf.AsSpan(0, pos).ToArray();
        }
        finally { ScratchPool.Return(buf); }
    }

    /// <summary>The bit packer: MSB-first bits gathered in a 64-bit word and written to the array 32 at a time, a 0x00 after every
    /// 0xFF byte, the last byte filled with 1 bits. The caller sizes the array for the stuffed stream.</summary>
    struct BitBuffer(byte[] buf, int pos)
    {
        ulong _acc; int _n;   // the low _n bits of _acc are pending

        /// <summary>The code of the category of <paramref name="diff"/> (packed table <paramref name="cs"/> from <paramref name="c0"/>),
        /// then its value bits: the low `cat` bits of diff, or of diff − 1 when it is negative.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Diff(int[] cs, int c0, int diff)
        {
            int cat = Category(diff);
            int e = cs[c0 + cat], len = e & 0xff;
            _acc = (_acc << len) | (uint)(e >> 8); _n += len;
            if ((cat | 0x10) != 0x10)
            {
                _acc = (_acc << cat) | (uint)((diff < 0 ? diff - 1 : diff) & ((1 << cat) - 1)); _n += cat;
            }
            if (_n >= 32)
            {   // at most 31 + 16 + 15 bits are pending here
                _n -= 32;
                uint word = (uint)(_acc >> _n);
                if (((~word - 0x01010101u) & word & 0x80808080u) == 0)
                {   // no 0xFF byte in the word: the four bytes as they are
                    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos, 4), word);
                    pos += 4;
                }
                else { Byte((byte)(word >> 24)); Byte((byte)(word >> 16)); Byte((byte)(word >> 8)); Byte((byte)word); }
            }
        }

        void Byte(byte b)
        {
            buf[pos++] = b;
            if (b == 0xff) buf[pos++] = 0;
        }

        /// <summary>The pending whole bytes, then the partial byte filled with 1 bits; the position after the data.</summary>
        public int Finish()
        {
            while (_n >= 8) { _n -= 8; Byte((byte)(_acc >> _n)); }
            if (_n > 0) Byte((byte)((_acc << (8 - _n)) | ((1u << (8 - _n)) - 1)));
            _n = 0;
            return pos;
        }
    }

    /// <summary>Decode one tile stream (SOF3 3 components, 16-bit, predictor 1) back to `w×h×3` ushorts (for `dng-diff`).</summary>
    public static ushort[] Decode(ReadOnlySpan<byte> span, out int w, out int h)
    {
        var data = span.ToArray();
        int i = 0; w = h = 0; int nc = 0;
        var bits = new byte[4][]; var vals = new byte[4][];
        if (data[0] != 0xff || data[1] != 0xd8) throw new InvalidDataException("no SOI");
        i = 2;
        while (true)
        {
            if (data[i] != 0xff) throw new InvalidDataException("marker expected");
            int m = data[i + 1]; int len = (data[i + 2] << 8) | data[i + 3]; var seg = data.AsSpan(i + 4, len - 2);
            if (m == 0xc3) { h = (seg[1] << 8) | seg[2]; w = (seg[3] << 8) | seg[4]; nc = seg[5]; }
            else if (m == 0xc4)
            {
                int p = 0;
                while (p < seg.Length) { int tc = seg[p] & 0xf; bits[tc] = seg.Slice(p + 1, 16).ToArray(); int n = 0; foreach (var b in bits[tc]) n += b; vals[tc] = seg.Slice(p + 17, n).ToArray(); p += 17 + n; }
            }
            else if (m == 0xda) { i += 2 + len; break; }
            i += 2 + len;
        }
        if (nc != 3) throw new InvalidDataException("3 components expected");
        // Huffman lookup: (length, code) → symbol
        var lut = new Dictionary<(int, int), int>[3];
        for (int c = 0; c < 3; c++)
        {
            lut[c] = new(); int code = 0, k = 0;
            for (int l = 1; l <= 16; l++) { for (int j = 0; j < bits[c][l - 1]; j++) { lut[c][(l, code)] = vals[c][k++]; code++; } code <<= 1; }
        }
        var outp = new ushort[w * h * 3];
        int bitbuf = 0, nbits = 0; int pos = i;
        int ReadBit()
        {
            if (nbits == 0)
            {
                int b = data[pos++];
                if (b == 0xff) { int b2 = data[pos]; if (b2 == 0) pos++; }
                bitbuf = b; nbits = 8;
            }
            nbits--; return (bitbuf >> nbits) & 1;
        }
        int DecodeSym(int c) { int code = 0, l = 0; while (true) { code = (code << 1) | ReadBit(); l++; if (lut[c].TryGetValue((l, code), out var s)) return s; if (l > 16) throw new InvalidDataException("bad huffman code"); } }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int cat = DecodeSym(c), diff;
                    if (cat == 0) diff = 0;
                    else if (cat == 16) diff = 32768;
                    else { int v = 0; for (int k = 0; k < cat; k++) v = (v << 1) | ReadBit(); diff = v < (1 << (cat - 1)) ? v - (1 << cat) + 1 : v; }
                    int pred = x == 0 ? (y == 0 ? 32768 : outp[((y - 1) * w) * 3 + c]) : outp[(y * w + x - 1) * 3 + c];
                    outp[(y * w + x) * 3 + c] = (ushort)(pred + diff);
                }
        }
        return outp;
    }
}
