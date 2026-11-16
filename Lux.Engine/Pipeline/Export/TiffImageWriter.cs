using System.Buffers.Binary;
using System.IO.Compression;

namespace Lux.Engine.Pipeline.Export;

/// <summary>One RGB image for <see cref="TiffImageWriter"/>.</summary>
public sealed class TiffImage
{
    public int Width, Height;
    /// <summary>16 (unsigned integer or half float) or 32 (float).</summary>
    public int BitsPerSample = 16;
    /// <summary>True: the samples are IEEE floating-point values. False: unsigned integers.</summary>
    public bool FloatingPoint;
    public TiffCompression Compression = TiffCompression.Deflate;
    /// <summary>The ICC profile of the samples (tag 34675). Null = none.</summary>
    public byte[]? IccProfile;
    /// <summary>The descriptive tags of the main directory (Make, Model, Software, DateTime, the Exif directory). The writer adds the
    /// tags that describe the pixel data. Null = none.</summary>
    public TiffDirectory? Tags;
    /// <summary>The number of strips that are compressed at the same time.</summary>
    public int Threads = 1;
}

/// <summary>Fills one row of an image: 3 samples for each pixel (red, green, blue), each sample in little-endian byte order.</summary>
public delegate void TiffRowSource(int y, Span<byte> row);

/// <summary>
/// A writer for RGB TIFF files (TIFF 6.0, baseline plus the extensions that are named here). The file is a classic TIFF with 32-bit
/// offsets in little-endian byte order. The pixel data is in strips of <see cref="RowsPerStrip"/> rows, with the three samples of a
/// pixel next to each other. The directory comes after the strips.
///
/// <para>Compression is Deflate (code 8, "Adobe Deflate", TIFF Technical Note 2 and Adobe's Photoshop TIFF Technical Notes), LZW (code 5,
/// TIFF 6.0 section 13) or none. A compressed file uses a predictor: horizontal differencing (2, TIFF 6.0 section 14) for integer samples,
/// and the floating-point predictor (3, Adobe Photoshop TIFF Technical Note 3) for floating-point samples.</para>
///
/// <para>The Deflate stream comes from the zlib library of the platform. Its bytes can differ between platforms and versions. The pixels
/// that a reader decodes are the same. LZW and uncompressed files have the same bytes everywhere.</para>
/// </summary>
public static class TiffImageWriter
{
    public const int RowsPerStrip = 32;

    /// <summary>Writes the image to <paramref name="stream"/>, which must be seekable and at position 0.</summary>
    /// <param name="phase">The name of the progress phase: one unit for each strip.</param>
    public static void Write(Stream stream, TiffImage image, TiffRowSource rows, ProgressReporter? progress = null, string phase = "tiff write", CancellationToken cancel = default)
    {
        int w = image.Width, h = image.Height;
        if (w < 1 || h < 1) throw new ArgumentException("TIFF: empty image");
        if (image.BitsPerSample is not (16 or 32) || (image.BitsPerSample == 32 && !image.FloatingPoint)) throw new ArgumentException("TIFF: the samples must be 16-bit, or 32-bit floating point");
        int bytesPerSample = image.BitsPerSample / 8;
        long rowBytesLong = (long)w * 3 * bytesPerSample;
        if (rowBytesLong * RowsPerStrip > int.MaxValue) throw new ArgumentException("TIFF: the image is too wide");
        int rowBytes = (int)rowBytesLong;
        int predictor = image.Compression == TiffCompression.None ? 1 : image.FloatingPoint ? 3 : 2;
        int stripCount = (h + RowsPerStrip - 1) / RowsPerStrip;
        var offsets = new uint[stripCount]; var counts = new uint[stripCount];

        stream.Write(new byte[] { 0x49, 0x49, 0x2a, 0x00, 0, 0, 0, 0 });   // "II", 42, the directory offset (set at the end)
        progress?.Begin(phase, stripCount);
        int threads = Math.Max(1, image.Threads);
        int batch = Math.Max(1, threads * 2);
        var done = new byte[batch][];
        for (int first = 0; first < stripCount; first += batch)
        {
            cancel.ThrowIfCancellationRequested();
            int n = Math.Min(batch, stripCount - first);
            void Strip(int k)
            {
                int y0 = (first + k) * RowsPerStrip, y1 = Math.Min(h, y0 + RowsPerStrip);
                var raw = new byte[(y1 - y0) * rowBytes];
                for (int y = y0; y < y1; y++)
                {
                    var row = raw.AsSpan((y - y0) * rowBytes, rowBytes);
                    rows(y, row);
                    if (predictor == 2) HorizontalDifference(row);
                    else if (predictor == 3) FloatingPointDifference(row, w * 3, bytesPerSample);
                }
                done[k] = image.Compression switch
                {
                    TiffCompression.Deflate => Deflate(raw),
                    TiffCompression.Lzw => TiffLzw.Encode(raw),
                    _ => raw,
                };
            }
            if (threads > 1 && n > 1) Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, Strip);
            else for (int k = 0; k < n; k++) Strip(k);
            for (int k = 0; k < n; k++)
            {
                if (stream.Position + done[k].Length + 1 > uint.MaxValue) throw new InvalidOperationException("TIFF: the file would be larger than 4 GB, the limit of the format");
                offsets[first + k] = (uint)stream.Position; counts[first + k] = (uint)done[k].Length;
                stream.Write(done[k]);
                if ((stream.Position & 1) != 0) stream.WriteByte(0);   // each offset is even (TIFF 6.0 section 2)
                done[k] = Array.Empty<byte>();
                progress?.Tick();
            }
        }

        var ifd = image.Tags ?? new TiffDirectory();
        int bits = image.BitsPerSample, format = image.FloatingPoint ? 3 : 1;
        ifd.Long(254, 0);                                    // NewSubfileType: the main image
        ifd.Long(256, (uint)w); ifd.Long(257, (uint)h);      // ImageWidth, ImageLength
        ifd.Short(258, bits, bits, bits);                    // BitsPerSample
        ifd.Short(259, image.Compression switch { TiffCompression.Deflate => 8, TiffCompression.Lzw => 5, _ => 1 });
        ifd.Short(262, 2);                                   // PhotometricInterpretation: RGB
        ifd.Long(273, offsets);                              // StripOffsets
        ifd.Short(274, 1);                                   // Orientation: top left
        ifd.Short(277, 3);                                   // SamplesPerPixel
        ifd.Long(278, RowsPerStrip);
        ifd.Long(279, counts);                               // StripByteCounts
        ifd.Rational(282, (72u, 1u)); ifd.Rational(283, (72u, 1u));   // XResolution, YResolution
        ifd.Short(284, 1);                                   // PlanarConfiguration: chunky
        ifd.Short(296, 2);                                   // ResolutionUnit: inch
        if (predictor != 1) ifd.Short(317, predictor);
        ifd.Short(339, format, format, format);              // SampleFormat
        if (image.IccProfile is not null) ifd.Undefined(34675, image.IccProfile);
        long at = stream.Position;
        if (at + ifd.Size() > uint.MaxValue) throw new InvalidOperationException("TIFF: the file would be larger than 4 GB, the limit of the format");
        ifd.Write(stream);
        stream.Position = 4;
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)at);
        stream.Write(b4);
        stream.Flush();
        progress?.End();
    }

    static byte[] Deflate(byte[] raw)
    {
        var ms = new MemoryStream(raw.Length / 2 + 64);
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Predictor 2 on a row of 16-bit RGB samples: each sample minus the same channel of the pixel before it.</summary>
    static void HorizontalDifference(Span<byte> row)
    {
        for (int i = row.Length / 2 - 1; i >= 3; i--)
        {
            int a = BinaryPrimitives.ReadUInt16LittleEndian(row[(2 * i)..]), b = BinaryPrimitives.ReadUInt16LittleEndian(row[(2 * (i - 3))..]);
            BinaryPrimitives.WriteUInt16LittleEndian(row[(2 * i)..], (ushort)(a - b));
        }
    }

    /// <summary>Predictor 3 on a row of floating-point RGB samples. The bytes of the row are put in planes: first the most significant byte
    /// of each sample, then the next byte of each sample, and so on. Then each byte becomes its difference from the byte 3 positions
    /// before it.</summary>
    static void FloatingPointDifference(Span<byte> row, int samples, int bytesPerSample)
    {
        var copy = row.ToArray();
        for (int s = 0; s < samples; s++)
            for (int b = 0; b < bytesPerSample; b++) row[(bytesPerSample - b - 1) * samples + s] = copy[bytesPerSample * s + b];
        for (int i = row.Length - 1; i >= 3; i--) row[i] = (byte)(row[i] - row[i - 3]);
    }
}

/// <summary>
/// The LZW compression of TIFF (TIFF 6.0 section 13). Codes are written with the most significant bit first. Code 256 clears the table
/// and code 257 ends the data. A code has 9 to 12 bits. The width increases one code before the table reaches the next power of two, as
/// each TIFF reader expects. When the table has 4094 entries, the encoder writes a clear code and starts a new table.
/// </summary>
public static class TiffLzw
{
    public static byte[] Encode(byte[] data)
    {
        var o = new MemoryStream(data.Length / 2 + 64);
        int bitBuffer = 0, bitCount = 0, width = 9, next = 258;
        void Emit(int code)
        {
            bitBuffer = (bitBuffer << width) | code; bitCount += width;
            while (bitCount >= 8) { o.WriteByte((byte)(bitBuffer >> (bitCount - 8))); bitCount -= 8; }
            bitBuffer &= (1 << bitCount) - 1;
        }
        var table = new Dictionary<int, int>(8192);   // (prefix code, next byte) → code
        Emit(256);
        if (data.Length > 0)
        {
            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int k = data[i], key = (prefix << 8) | k;
                if (table.TryGetValue(key, out int code)) { prefix = code; continue; }
                Emit(prefix);
                table[key] = next++;
                if (next == 4094) { Emit(256); table.Clear(); next = 258; width = 9; }
                else if (next > (1 << width) - 1) width++;
                prefix = k;
            }
            Emit(prefix);
            next++;
            if (next == 4094) { Emit(256); width = 9; }
            else if (next > (1 << width) - 1) width++;
        }
        Emit(257);
        if (bitCount > 0) o.WriteByte((byte)(bitBuffer << (8 - bitCount)));
        return o.ToArray();
    }
}
