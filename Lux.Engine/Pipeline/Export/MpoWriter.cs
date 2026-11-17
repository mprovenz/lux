using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The writer of MPO files: a multi-picture file (CIPA DC-007) with the two pictures of a stereo pair. The file is two baseline JPEG
/// pictures, one after the other. Each has an Exif segment and an APP2 segment with the multi-picture data. The segment of the first
/// picture holds the index: the number of pictures, and the kind, the size and the place of each. Both segments hold the
/// attributes of their picture: its number, the picture that is the base viewpoint, the distance between the two viewpoints, and
/// the convergence angle. A reader that does not know the format shows the first picture.
/// </summary>
public static class MpoWriter
{
    /// <summary>The kind of a picture in the index: one view of a stereo pair.</summary>
    const uint DisparityImage = 0x020002;

    /// <summary>A small TIFF directory, little endian.</summary>
    sealed class Ifd
    {
        readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _e = new();
        public Ifd Long(ushort t, uint v) { _e.Add((t, 4, 1, BitConverter.GetBytes(v))); return this; }
        public Ifd Short(ushort t, ushort v) { _e.Add((t, 3, 1, new byte[] { (byte)v, (byte)(v >> 8), 0, 0 })); return this; }
        public Ifd Undefined(ushort t, byte[] v) { _e.Add((t, 7, (uint)v.Length, v)); return this; }
        public Ifd Ascii(ushort t, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); _e.Add((t, 2, (uint)b.Length, b)); return this; }
        public Ifd Rational(ushort t, uint n, uint d) { var b = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(b, n); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), d); _e.Add((t, 5, 1, b)); return this; }
        public Ifd SRational(ushort t, int n, int d) { var b = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(b, n); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), d); _e.Add((t, 10, 1, b)); return this; }
        public int Size => 2 + 12 * _e.Count + 4 + _e.Where(e => e.Data.Length > 4).Sum(e => (e.Data.Length + 1) & ~1);

        /// <summary>The bytes of the directory when it starts <paramref name="at"/> bytes after the TIFF header.</summary>
        public byte[] Bytes(int at, uint next)
        {
            var sorted = _e.OrderBy(e => e.Tag).ToList();
            var o = new byte[Size];
            BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)sorted.Count);
            int val = 2 + 12 * sorted.Count + 4;
            for (int i = 0; i < sorted.Count; i++)
            {
                var (tag, type, count, data) = sorted[i]; int p = 2 + 12 * i;
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p), tag);
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p + 2), type);
                BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(p + 4), count);
                if (data.Length <= 4) data.CopyTo(o, p + 8);
                else { BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(p + 8), (uint)(at + val)); data.CopyTo(o, val); val += (data.Length + 1) & ~1; }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(2 + 12 * sorted.Count), next);
            return o;
        }
    }

    static readonly byte[] TiffHeader = { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 };

    /// <summary>The Exif segment of one picture: maker, model, orientation, software, the pixel size and an image id.</summary>
    static byte[] Exif(int w, int h, string id, string software)
    {
        var ifd0 = new Ifd().Ascii(0x010F, "Light").Ascii(0x0110, "L16").Short(0x0112, 1).Ascii(0x0131, software).Long(0x8769, 0);
        int exifAt = 8 + ifd0.Size;
        ifd0 = new Ifd().Ascii(0x010F, "Light").Ascii(0x0110, "L16").Short(0x0112, 1).Ascii(0x0131, software).Long(0x8769, (uint)exifAt);
        var exif = new Ifd().Undefined(0x9000, Encoding.ASCII.GetBytes("0230")).Long(0xA002, (uint)w).Long(0xA003, (uint)h).Ascii(0xA420, id);
        using var ms = new MemoryStream();
        ms.Write("Exif\0\0"u8); ms.Write(TiffHeader); ms.Write(ifd0.Bytes(8, 0)); ms.Write(exif.Bytes(exifAt, 0));
        return ms.ToArray();
    }

    /// <summary>The attributes of one picture.</summary>
    static Ifd Attributes(uint number, double baselineM, double convergenceDeg) => new Ifd()
        .Undefined(0xB000, Encoding.ASCII.GetBytes("0100")).Long(0xB101, number).Long(0xB204, 1)
        .SRational(0xB205, (int)Math.Round(convergenceDeg * 10000), 10000)
        .Rational(0xB206, (uint)Math.Round(baselineM * 1_000_000), 1_000_000);

    /// <summary>The APP2 payload of the first picture: the index, then the attributes.</summary>
    static byte[] First(uint size1, uint size2, uint offset2)
    {
        var entries = new byte[32];
        // entry 1: the representative picture, one view of a stereo pair, at offset 0
        BinaryPrimitives.WriteUInt32LittleEndian(entries, (1u << 29) | DisparityImage);
        BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(4), size1);
        BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(16), DisparityImage);
        BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(20), size2);
        BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(24), offset2);
        var index = new Ifd().Undefined(0xB000, Encoding.ASCII.GetBytes("0100")).Long(0xB001, 2).Undefined(0xB002, entries);
        int attrAt = 8 + index.Size;
        using var ms = new MemoryStream();
        ms.Write("MPF\0"u8); ms.Write(TiffHeader); ms.Write(index.Bytes(8, (uint)attrAt)); ms.Write(Attributes(1, 0, 0).Bytes(attrAt, 0));
        return ms.ToArray();
    }

    static byte[] Other(uint number, double baselineM, double convergenceDeg)
    {
        using var ms = new MemoryStream();
        ms.Write("MPF\0"u8); ms.Write(TiffHeader); ms.Write(Attributes(number, baselineM, convergenceDeg).Bytes(8, 0));
        return ms.ToArray();
    }

    /// <summary>Put one APP segment after the last APP0 or APP1 segment of a JPEG picture.</summary>
    static byte[] InsertAfterApp1(byte[] jpg, byte marker, byte[] payload)
    {
        int p = 2, at = 2;
        while (p + 4 <= jpg.Length && jpg[p] == 0xFF)
        {
            byte m = jpg[p + 1];
            if (m is 0xDA or 0xDB or 0xC0 or 0xC4) break;
            p += 2 + ((jpg[p + 2] << 8) | jpg[p + 3]);
            if (m is 0xE0 or 0xE1) at = p;
        }
        var o = new byte[jpg.Length + 4 + payload.Length];
        Buffer.BlockCopy(jpg, 0, o, 0, at);
        o[at] = 0xFF; o[at + 1] = marker; o[at + 2] = (byte)((payload.Length + 2) >> 8); o[at + 3] = (byte)(payload.Length + 2);
        payload.CopyTo(o, at + 4);
        Buffer.BlockCopy(jpg, at, o, at + 4 + payload.Length, jpg.Length - at);
        return o;
    }

    /// <summary>The place of the multi-picture header (the byte order mark) in a JPEG picture with an MPF segment.</summary>
    static int HeaderOf(byte[] j)
    {
        for (int i = 2; i + 8 < j.Length; i++)
            if (j[i] == 0xFF && j[i + 1] == 0xE2 && j[i + 4] == 'M' && j[i + 5] == 'P' && j[i + 6] == 'F' && j[i + 7] == 0) return i + 8;
        throw new InvalidOperationException("no multi-picture segment");
    }

    static byte[] Jpeg(Rgba img, int quality, string software)
    {
        using var plain = new MemoryStream();
        JpegEncoder.Encode(plain, img.P, img.W, img.H, img.W * 4, grayscale: false, new JpegEncoder { Quality = quality });
        // the image id is made from the coded picture, so the same pair always gives the same file
        string id = Convert.ToHexString(SHA256.HashData(plain.ToArray()), 0, 16).ToLowerInvariant();
        using var ms = new MemoryStream();
        JpegEncoder.Encode(ms, img.P, img.W, img.H, img.W * 4, grayscale: false, new JpegEncoder { Quality = quality, ExifApp1 = Exif(img.W, img.H, id, software) });
        return ms.ToArray();
    }

    /// <summary>The bytes of an MPO file with the left picture first. <paramref name="baselineMm"/> is the distance between the two
    /// viewpoints. The pair is parallel: the convergence angle is 0.</summary>
    public static byte[] Encode(Rgba left, Rgba right, double baselineMm, int quality = 92, string software = "Lux")
    {
        if (left.W != right.W || left.H != right.H) throw new ArgumentException("the two pictures of a stereo pair must have the same size");
        double meters = baselineMm / 1000.0;
        var j2 = InsertAfterApp1(Jpeg(right, quality, software), 0xE2, Other(2, meters, 0));
        var plain1 = Jpeg(left, quality, software);
        // the size of the index does not depend on its values: place it once to learn the sizes, then write the values
        var j1 = InsertAfterApp1(plain1, 0xE2, First(0, 0, 0));
        int header = HeaderOf(j1);
        j1 = InsertAfterApp1(plain1, 0xE2, First((uint)j1.Length, (uint)j2.Length, (uint)(j1.Length - header)));
        var o = new byte[j1.Length + j2.Length];
        j1.CopyTo(o, 0); j2.CopyTo(o, j1.Length);
        return o;
    }

    /// <summary>The two pictures of an MPO file, as the bytes of two JPEG files, read from the index. For checks.</summary>
    public static (byte[] First, byte[] Second) Split(byte[] mpo)
    {
        int header = HeaderOf(mpo);
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(mpo.AsSpan(header + 4));
        int at = header + (int)ifd, n = BinaryPrimitives.ReadUInt16LittleEndian(mpo.AsSpan(at));
        for (int i = 0; i < n; i++)
        {
            int e = at + 2 + 12 * i;
            if (BinaryPrimitives.ReadUInt16LittleEndian(mpo.AsSpan(e)) != 0xB002) continue;
            int entries = header + (int)BinaryPrimitives.ReadUInt32LittleEndian(mpo.AsSpan(e + 8));
            int size1 = (int)BinaryPrimitives.ReadUInt32LittleEndian(mpo.AsSpan(entries + 4));
            int size2 = (int)BinaryPrimitives.ReadUInt32LittleEndian(mpo.AsSpan(entries + 20)), off2 = header + (int)BinaryPrimitives.ReadUInt32LittleEndian(mpo.AsSpan(entries + 24));
            return (mpo[..size1], mpo[off2..(off2 + size2)]);
        }
        throw new InvalidOperationException("the multi-picture index has no entries");
    }
}
