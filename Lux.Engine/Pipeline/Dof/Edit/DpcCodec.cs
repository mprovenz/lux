using System.Buffers.Binary;
using System.IO.Compression;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// Reader of Lumen's ".dpc" depth image — `lt::Internal::ParserDPC`. Lumen's writer is not ported.
///
/// Stream layout (little endian):
/// <code>
///   'D' 'h'                                         magic ("unrecognized file format")
///   chunk "head": tag[4], u32 0, u64 size = 0x14     ("invalid depth stream header!")
///       u32 width, u32 height, u32 near = 200, u32 far = 640000, u32 bits = 14   ("invalid near depth/far depth/bit depth setting!")
///   chunk "strp": size 0                             strips follow; without it one "data" chunk holds the whole image
///   n × chunk "data": size = the bytes of one PNG    n = <see cref="StripCount"/>(height); every strip is 128 rows, the last takes the rest
/// </code>
/// A strip is a complete PNG file — 16-bit greyscale, written and read with libpng 1.6.34's simplified API as `PNG_FORMAT_LINEAR_Y`
/// (`png_image_begin_read_from_memory`, format 4, `png_image_finish_read`); its samples are the depth codes, unchanged.
/// A code is an index into a table of 16384 depths that are <b>uniform in inverse depth</b> between 1/200 and 1/640000 mm⁻¹
/// (<see cref="Table"/>); code 0xffff stands for 0. The writer picks the nearest table entry in inverse depth, so the format is lossy:
/// depths below 0.001 become 0, below 200 become 200, above 640000 become 640000.
/// </summary>
public static class DpcCodec
{
    public const int Near = 200, Far = 640000, Bits = 14, StripRows = 0x80;

    /// <summary>
    /// The decode table of Lumen's ".dpc" reader (16384 floats on its stack), built exactly as there:
    /// entry 0 = 200.0f; two double pairs A = (1/200, 1/200 − d) and B =
    /// (1/200 − 2d, 1/200 − 3d), d = (1/200 − 1/640000) / 16383; per round entries i … i+3 = (float)(1.0 / (A + (−d))), (float)(1.0 / (B + (−d))),
    /// then i+4 … i+7 = (float)(1.0 / (A + (−5d))), (float)(1.0 / (B + (−5d))), then A and B += −8d
    /// — additions and divisions in double precision, then rounded to float, so the accumulated sums decide the last bits. The loop ends after entry 16380;
    /// entries 16381 … 16383 are the immediates 0x48e0bc27, 0x4902b96d, 0x491c4000 (= 640000.0f).
    /// </summary>
    public static readonly float[] Table = BuildTable();

    static float[] BuildTable()
    {
        static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
        var t = new float[16384];
        double a0 = D(0x3f747ae147ae147b), a1 = D(0x3f747a8f616f1581), b0 = D(0x3f747a3d7b301688), b1 = D(0x3f7479eb94f1178e);
        double m1 = D(0xbe94798fbfbe61b1), m5 = D(0xbeb997f3afadfa1d), m8 = D(0xbec4798fbfbe61b1);
        t[0] = BitConverter.UInt32BitsToSingle(0x43480000);
        for (int i = 5; ; i += 8)
        {
            t[i - 4] = (float)(1.0 / (a0 + m1)); t[i - 3] = (float)(1.0 / (a1 + m1));
            t[i - 2] = (float)(1.0 / (b0 + m1)); t[i - 1] = (float)(1.0 / (b1 + m1));
            if (i == 0x3ffd) break;
            t[i] = (float)(1.0 / (a0 + m5)); t[i + 1] = (float)(1.0 / (a1 + m5));
            t[i + 2] = (float)(1.0 / (b0 + m5)); t[i + 3] = (float)(1.0 / (b1 + m5));
            a0 += m8; a1 += m8; b0 += m8; b1 += m8;
        }
        t[16381] = BitConverter.UInt32BitsToSingle(0x48e0bc27); t[16382] = BitConverter.UInt32BitsToSingle(0x4902b96d);
        t[16383] = BitConverter.UInt32BitsToSingle(0x491c4000);
        return t;
    }

    /// <summary>The tile count of `lt::Tiler` (0, rows, 128): <c>rows / 128</c>, plus one when the remainder is more than
    /// half a strip (<c>128 &lt; 2·(rows % 128)</c>), at least 1. Every strip but the last is 128 rows.</summary>
    public static int StripCount(int rows)
    {
        int n = (StripRows < (rows % StripRows) * 2 ? 1 : 0) + rows / StripRows;
        return n > 0 ? n : 1;
    }

    /// <summary>The next two bytes are 'D', 'h' (the stream position is restored).</summary>
    public static bool IsDpc(ReadOnlySpan<byte> src, int pos) => pos + 2 <= src.Length && src[pos] == (byte)'D' && src[pos + 1] == (byte)'h';

    /// <summary>The older depth stream formats "DP" / "DX" (never written by Lumen 2.3; not decoded here).</summary>
    public static bool IsLegacyDepth(ReadOnlySpan<byte> src, int pos)
        => pos + 2 <= src.Length && src[pos] == (byte)'D' && (src[pos + 1] == (byte)'P' || src[pos + 1] == (byte)'X');

    /// <summary>The dims of a ".dpc" stream without decoding it.</summary>
    public static (int W, int H) ReadSize(ReadOnlySpan<byte> src, int pos)
    {
        if (!IsDpc(src, pos)) throw new InvalidDataException("unrecognized file format");
        if (pos + 2 + 16 + 20 > src.Length) throw new InvalidDataException("Input stream is not sufficient to load DepthStreamHeader");
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(src[(pos + 18)..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(src[(pos + 22)..]));
    }

    /// <summary>
    /// Decode one ".dpc" stream starting at <paramref name="pos"/>; on return <paramref name="pos"/> is behind it.
    /// Depth = <see cref="Table"/>[code], 0 for code 0xffff. A code of 16384 (what the writer emits for a depth of `FLT_MAX` or +∞)
    /// indexes one float past the table, where Lumen's stack holds the image width as an integer — reproduced; larger codes have no
    /// defined value and are rejected.
    /// </summary>
    public static float[] Decode(ReadOnlySpan<byte> src, ref int pos, out int width, out int height)
    {
        int p = pos;
        if (src.Length - p < 2) throw new InvalidDataException("Input stream is not sufficient to load MagicBits");
        if (src[p] != 0x44 || src[p + 1] != 0x68) throw new InvalidDataException("unrecognized file format");
        p += 2;
        var (tag, size) = ChunkHeader(src, ref p);
        if (tag != 0x64616568u /* "head" */ || size != 0x14) throw new InvalidDataException("invalid depth stream header!");
        if (src.Length - p < 0x14) throw new InvalidDataException("Input stream is not sufficient to load DepthStreamHeader");
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]), h = BinaryPrimitives.ReadUInt32LittleEndian(src[(p + 4)..]);
        if (BinaryPrimitives.ReadInt32LittleEndian(src[(p + 8)..]) != Near || BinaryPrimitives.ReadInt32LittleEndian(src[(p + 12)..]) != Far
            || BinaryPrimitives.ReadInt32LittleEndian(src[(p + 16)..]) != Bits)
            throw new InvalidDataException("invalid near depth/far depth/bit depth setting!");
        p += 0x14;
        if (w == 0 || h == 0 || (ulong)w * h > int.MaxValue / 4) throw new InvalidDataException("invalid depth stream header!");
        var codes = new ushort[(int)(w * h)];
        (tag, size) = DataHeader(src, ref p);
        if (tag == 0x70727473u /* "strp" */)
        {
            int n = StripCount((int)h);
            for (int i = 0, row = 0; i < n && row < (int)h; i++, row += StripRows)
            {
                (_, size) = DataHeader(src, ref p);
                Strip(src, ref p, size, codes, (int)w, row, i == n - 1 ? (int)h - row : StripRows);
            }
        }
        else Strip(src, ref p, size, codes, (int)w, 0, (int)h);
        var o = new float[codes.Length]; var t = Table;
        for (int i = 0; i < o.Length; i++)
        {
            int c = codes[i];
            if (c == 0xffff) o[i] = 0f;
            else if (c < 16384) o[i] = t[c];
            else if (c == 16384) o[i] = BitConverter.Int32BitsToSingle((int)w);
            else throw new InvalidDataException($"depth code {c} lies outside the 14-bit table");
        }
        pos = p; width = (int)w; height = (int)h;
        return o;
    }

    static (uint Tag, long Size) ChunkHeader(ReadOnlySpan<byte> src, ref int p)
    {
        if (src.Length - p < 0x10) throw new InvalidDataException("Input stream is not sufficient to load DepthStreamTagHeader");
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]); long size = BinaryPrimitives.ReadInt64LittleEndian(src[(p + 8)..]);
        p += 0x10;
        return (tag, size);
    }

    /// <summary>A chunk header that must be "data" or "strp" ("invalid depth stream data!").</summary>
    static (uint Tag, long Size) DataHeader(ReadOnlySpan<byte> src, ref int p)
    {
        var c = ChunkHeader(src, ref p);
        if (c.Tag != 0x61746164u && c.Tag != 0x70727473u) throw new InvalidDataException("invalid depth stream data!");
        return c;
    }

    /// <summary>One "data" payload → <paramref name="rows"/> rows of codes from row <paramref name="row"/> on.</summary>
    static void Strip(ReadOnlySpan<byte> src, ref int p, long size, ushort[] codes, int w, int row, int rows)
    {
        if (size < 0 || src.Length - p < size) throw new InvalidDataException("Input stream is not sufficient to load ImageData");
        Png16(src.Slice(p, (int)size), codes, w, row, rows);
        p += (int)size;
    }

    /// <summary>
    /// The PNG subset the writer produces: 8-byte signature, IHDR (width, height, bit depth 16, colour type 0, no interlace), any ancillary
    /// chunks (gAMA 100000 and cHRM are present and change nothing: file and output are both linear), IDAT (zlib), IEND. Samples are
    /// big-endian 16-bit; the five row filters with a pixel stride of two bytes. Any other PNG (at best "failed to read compressed
    /// depth data" in Lumen) is rejected.
    /// </summary>
    static void Png16(ReadOnlySpan<byte> png, ushort[] codes, int w, int row, int rows)
    {
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        if (png.Length < 8 || !png[..8].SequenceEqual(sig)) throw new InvalidDataException("failed to read compressed depth data");
        using var idat = new MemoryStream();
        int q = 8; bool header = false, end = false;
        while (!end && q + 12 <= png.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png[q..]); uint type = BinaryPrimitives.ReadUInt32BigEndian(png[(q + 4)..]);
            if (len < 0 || q + 12 + len > png.Length) throw new InvalidDataException("failed to read compressed depth data");
            var body = png.Slice(q + 8, len);
            switch (type)
            {
                case 0x49484452: // IHDR
                    if (len != 13 || BinaryPrimitives.ReadUInt32BigEndian(body) != (uint)w || BinaryPrimitives.ReadUInt32BigEndian(body[4..]) != (uint)rows
                        || body[8] != 16 || body[9] != 0 || body[10] != 0 || body[11] != 0 || body[12] != 0)
                        throw new InvalidDataException("depth strip is not a 16-bit greyscale PNG of the expected size");
                    header = true; break;
                case 0x49444154: idat.Write(body); break; // IDAT
                case 0x49454e44: end = true; break;       // IEND
            }
            q += 12 + len;
        }
        if (!header) throw new InvalidDataException("failed to read compressed depth data");
        int rb = w * 2; var raw = new byte[(long)(rb + 1) * rows];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length) { int n = z.Read(raw, got, raw.Length - got); if (n <= 0) break; got += n; }
            if (got != raw.Length) throw new InvalidDataException("failed to read compressed depth data");
        }
        var prev = new byte[rb]; var cur = new byte[rb];
        for (int y = 0; y < rows; y++)
        {
            int o = y * (rb + 1); byte f = raw[o]; Buffer.BlockCopy(raw, o + 1, cur, 0, rb);
            switch (f)
            {
                case 0: break;
                case 1: for (int i = 2; i < rb; i++) cur[i] = (byte)(cur[i] + cur[i - 2]); break;
                case 2: for (int i = 0; i < rb; i++) cur[i] = (byte)(cur[i] + prev[i]); break;
                case 3:
                    for (int i = 0; i < rb; i++) cur[i] = (byte)(cur[i] + (((i >= 2 ? cur[i - 2] : 0) + prev[i]) >> 1));
                    break;
                case 4:
                    for (int i = 0; i < rb; i++)
                    {
                        int a = i >= 2 ? cur[i - 2] : 0, b = prev[i], c = i >= 2 ? prev[i - 2] : 0;
                        int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                        cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                    }
                    break;
                default: throw new InvalidDataException("failed to read compressed depth data");
            }
            long d = (long)(row + y) * w;
            for (int x = 0; x < w; x++) codes[d + x] = (ushort)((cur[2 * x] << 8) | cur[2 * x + 1]);
            (prev, cur) = (cur, prev);
        }
    }
}
