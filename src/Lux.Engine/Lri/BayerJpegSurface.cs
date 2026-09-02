using System.Buffers.Binary;

namespace Lux.Engine.Lri;

/// <summary>
/// `CameraModule.Surface.FormatType.RAW_BAYER_JPEG` (= 0) — the second raw surface encoding a `.lri` can carry, used by
/// 23 of the 417 captures in the corpus (all of them 4-frame stacks; see `a-module-coverage.md`). cp.dll decodes it in
/// **`FUN_180128550`**, reached from `lt::CaptureStack::CaptureStack::lambda_0` `FUN_180118bb0`, which dispatches on the
/// internal encoding tag: `0` → this function, `1` → the packed reader `FUN_180127670`, anything else →
/// *"Unsupported sensor data encoding!"*.
///
/// <para><b>Container.</b> The surface payload at <c>blockBase + surface.data_offset</c> is</para>
/// <code>
///   char[4]  'BJPG'                     // FUN_180128550 L~78: else "corrupted file header!"
///   u32      version                    // must be &lt; 2, else "unsupported encoding variant!"
///   u32[4]   planeSize                  // compressed byte length of each plane; version 1 uses only [0]
///   char[4]  'QDQT'                     // optional — see below
///   u32      qdqtVariant                // must be 0, else "unsupported encoding of q-dq table"
///   u32      quantBytes                 // 1024 on every file seen: the ENCODER's 10-bit → 8-bit table
///   u32      dequantBytes               // 512 = 256 × u16: the DECODER's 8-bit → 10-bit table
///   u8 [quantBytes]                     // read and immediately freed — the decoder never looks at it
///   u16[dequantBytes/2]                 // the dequantization LUT
///   … the planeSize[i] JPEG streams, back to back …
/// </code>
/// <para>If the four bytes after the header are not `QDQT`, cp.dll seeks back and builds a default table from the
/// sensor type instead (`FUN_180189370(table, *(u32*)(CapturedImage+0x100))`). No file in the corpus takes that branch,
/// so it is <b>not ported</b>: hitting it throws rather than inventing a table.</para>
///
/// <para><b>Planes.</b> Every plane is a **baseline grayscale JPEG** (JFIF APP0, one 8-bit component, 1×1 sampling,
/// the Annex-K Huffman tables) decoded by <see cref="BaselineJpegDecoder"/>. Version 0 carries four planes of
/// <c>(width/2) × (height/2)</c>, one per CFA site; version 1 carries a single full-size plane. The scatter is
/// `0x1801290f0`:</para>
/// <code>
///   dst[(plane/2 + 2·y)·stride + (plane%2) + 2·x] = dequant[plane_sample[y][x]]     // version 0
///   dst[y·stride + x]                             = dequant[sample[y][x]]           // version 1
/// </code>
/// <para>so plane 0/1/2/3 land on raster sites (0,0)/(0,1)/(1,0)/(1,1) — the natural raster order of the 2×2 cell.
/// A plane whose decoded size is not exactly half (version 0) or equal (version 1) to the surface size is rejected
/// with cp.dll's own *"corrupted bayer plane data!"*; a compressed plane at least as large as the uncompressed one is
/// *"invalid bayer plane size!"*.</para>
/// </summary>
public static class BayerJpegSurface
{
    /// <summary>Fixed header size: 'BJPG' + version + 4 plane sizes + 'QDQT' + variant + the two table lengths.</summary>
    const int HeaderBytes = 24 + 16;

    /// <summary>Decode one `RAW_BAYER_JPEG` surface into a row-major <c>ushort[height·width]</c> (stride = width, as
    /// `lt::Image&lt;unsigned short&gt;::resize` allocates it).</summary>
    public static ushort[] Decode(ReadOnlySpan<byte> file, long payloadOffset, int width, int height)
    {
        var pix = new ushort[(long)height * width];
        Decode(file, payloadOffset, width, height, pix, width);
        return pix;
    }

    public static void Decode(ReadOnlySpan<byte> file, long payloadOffset, int width, int height, ushort[] dst, int dstStride)
    {
        var p = file[(int)payloadOffset..];
        if (p.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(p) != 0x47504A42u)   // 'BJPG' little-endian
            throw new InvalidDataException("corrupted file header!");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        if (version > 1) throw new NotSupportedException("unsupported encoding variant!");
        var planeSize = new uint[4];
        for (int i = 0; i < 4; i++) planeSize[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(8 + 4 * i)..]);

        if (BinaryPrimitives.ReadUInt32LittleEndian(p[24..]) != 0x54514451u)              // 'QDQT'
            throw new NotSupportedException(
                "RAW_BAYER_JPEG surface without a QDQT table: cp.dll falls back to FUN_180189370 (a table derived from " +
                "the sensor type); no capture in the corpus takes that branch, so it is not ported");
        if (BinaryPrimitives.ReadUInt32LittleEndian(p[28..]) != 0) throw new NotSupportedException("unsupported encoding of q-dq table");
        int quantBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[32..]);
        int dequantBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[36..]);
        if ((uint)quantBytes >= 0x1000) throw new InvalidDataException("RAW_BAYER_JPEG: q table too large");

        // The 1024-byte quantize table is read and freed unused (`free(local_70)` at FUN_180128550 L~150) — the decode
        // needs only the u16 dequantize table that follows it.
        int dqOff = HeaderBytes + quantBytes;
        int nDequant = dequantBytes >> 1;
        var dequant = new ushort[nDequant];
        for (int i = 0; i < nDequant; i++) dequant[i] = BinaryPrimitives.ReadUInt16LittleEndian(p[(dqOff + 2 * i)..]);

        int data = dqOff + dequantBytes;
        if (version == 0)
        {
            int pw = width / 2, ph = height / 2;
            uint biggest = Math.Max(Math.Max(planeSize[0], planeSize[1]), Math.Max(planeSize[2], planeSize[3]));
            if (biggest >= (uint)(pw * ph * 2)) throw new InvalidDataException("invalid bayer plane size!");
            for (int plane = 0; plane < 4; plane++)
            {
                var gray = BaselineJpegDecoder.DecodeGray(p.Slice(data, (int)planeSize[plane]), out int gw, out int gh);
                if (gw * 2 != width || gh * 2 != height) throw new InvalidDataException("corrupted bayer plane data!");
                int rowOff = plane / 2, colOff = plane % 2;
                for (int y = 0; y < gh; y++)
                {
                    long o = (long)(rowOff + 2 * y) * dstStride + colOff;
                    int s = y * gw;
                    for (int x = 0; x < gw; x++) dst[o + 2 * x] = dequant[gray[s + x]];
                }
                data += (int)planeSize[plane];
            }
        }
        else
        {
            if (planeSize[0] >= (uint)(width * height * 2)) throw new InvalidDataException("invalid bayer plane size!");
            var gray = BaselineJpegDecoder.DecodeGray(p.Slice(data, (int)planeSize[0]), out int gw, out int gh);
            if (gw != width || gh != height) throw new InvalidDataException("corrupted bayer plane data!");
            for (int y = 0; y < gh; y++)
            {
                long o = (long)y * dstStride;
                int s = y * gw;
                for (int x = 0; x < gw; x++) dst[o + x] = dequant[gray[s + x]];
            }
        }
    }

    /// <summary>The `QDQT` dequantization LUT of a surface (8-bit JPEG code → raw DN), for inspection.</summary>
    public static ushort[] DequantTable(ReadOnlySpan<byte> file, long payloadOffset)
    {
        var p = file[(int)payloadOffset..];
        int quantBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[32..]);
        int dequantBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[36..]);
        var t = new ushort[dequantBytes >> 1];
        for (int i = 0; i < t.Length; i++) t[i] = BinaryPrimitives.ReadUInt16LittleEndian(p[(HeaderBytes + quantBytes + 2 * i)..]);
        return t;
    }
}
