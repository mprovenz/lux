// This product includes Gain Map technology under license by Adobe.

using System.Globalization;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The values that tell a reader how to apply a gain map, as the Ultra HDR Image Format v1.1 and ISO 21496-1 define them. The same
/// values apply to each channel of the map. For a pixel with the linear base value <c>sdr</c> and the map value <c>m</c> (0 to 1):
/// <c>log2 gain = GainMapMin + (GainMapMax − GainMapMin) · m^(1 / Gamma)</c> and
/// <c>hdr = (sdr + OffsetSdr) · 2^(log2 gain · w) − OffsetHdr</c>. The weight <c>w</c> goes from 0 to 1 as the headroom of the display
/// (log2 of its peak white over its SDR white) goes from <see cref="HdrCapacityMin"/> to <see cref="HdrCapacityMax"/>.
/// </summary>
public sealed record GainMapMetadata
{
    /// <summary>The log2 gain of the map value 0.</summary>
    public double GainMapMin { get; init; }
    /// <summary>The log2 gain of the map value 1.</summary>
    public double GainMapMax { get; init; }
    public double Gamma { get; init; } = 1.0;
    public double OffsetSdr { get; init; } = 1.0 / 64;
    public double OffsetHdr { get; init; } = 1.0 / 64;
    public double HdrCapacityMin { get; init; }
    public double HdrCapacityMax { get; init; }

    static string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>The XMP packet of the gain-map image: the <c>hdrgm</c> attributes of the Ultra HDR Image Format (the namespace of Adobe's
    /// Gain Map specification).</summary>
    public string GainMapXmp() =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Lux\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\""
        + $" hdrgm:GainMapMin=\"{F(GainMapMin)}\" hdrgm:GainMapMax=\"{F(GainMapMax)}\" hdrgm:Gamma=\"{F(Gamma)}\""
        + $" hdrgm:OffsetSDR=\"{F(OffsetSdr)}\" hdrgm:OffsetHDR=\"{F(OffsetHdr)}\" hdrgm:HDRCapacityMin=\"{F(HdrCapacityMin)}\" hdrgm:HDRCapacityMax=\"{F(HdrCapacityMax)}\""
        + " hdrgm:BaseRenditionIsHDR=\"False\"/></rdf:RDF></x:xmpmeta>";

    /// <summary>The XMP packet of the primary image: the container directory of the Ultra HDR Image Format, with the primary image and the
    /// gain-map image of <paramref name="gainMapImageLength"/> bytes.</summary>
    public static string PrimaryXmp(int gainMapImageLength) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Lux\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\""
        + " xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\"><Container:Directory><rdf:Seq>"
        + "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>"
        + $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{gainMapImageLength.ToString(CultureInfo.InvariantCulture)}\"/></rdf:li>"
        + "</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";

    /// <summary>
    /// The ISO 21496-1 metadata record, big endian: minimum version (16 bits, 0), writer version (16 bits, 0), flags (8 bits), the HDR
    /// headroom of the base image and of the alternate image, then gain map minimum, gain map maximum, gamma, base offset and alternate
    /// offset. Each value is a fraction of two 32-bit integers; the numerators of the minimum, the maximum and the offsets are signed.
    /// The flags are 0x40: one set of values for all channels, and the gain applies in the color space of the base image.
    /// </summary>
    public byte[] IsoRecord()
    {
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        void U32(long v) { o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
        void Fraction(double v, long denominator) { U32((long)Math.Round(v * denominator)); U32(denominator); }
        const long Million = 1_000_000;
        U16(0); U16(0);
        o.Add(0x40);
        Fraction(HdrCapacityMin, Million);
        Fraction(HdrCapacityMax, Million);
        Fraction(GainMapMin, Million);
        Fraction(GainMapMax, Million);
        Fraction(Gamma, Million);
        Fraction(OffsetSdr, 64 * Million);
        Fraction(OffsetHdr, 64 * Million);
        return o.ToArray();
    }
}

/// <summary>
/// The container of an HDR gain-map JPEG. The file follows two public specifications, and holds the metadata of both:
/// the Ultra HDR Image Format v1.1 (Google; XMP with the <c>hdrgm</c> namespace of Adobe's Gain Map specification) and ISO 21496-1
/// (gain map metadata for image conversion). The gain-map image is a second JPEG after the primary image, listed in a Multi-Picture
/// Format index (CIPA DC-007).
///
/// <para>The primary image is an existing baseline JPEG. Its segments keep their bytes: this class only inserts metadata segments
/// before its first table and appends the gain-map image. A reader without gain-map support shows the primary image.</para>
///
/// <para>Layout:</para>
/// <code>
///   SOI, then the APPn and COM segments of the primary image (JFIF, Exif, comment)
///   APP1  XMP: the container directory (primary image, gain-map image and its length)
///   APP2  ICC profile
///   APP2  ISO 21496-1: "urn:iso:std:iso:ts:21496:-1", minimum version and writer version
///   APP2  MPF: the index of the two images
///   the tables, frame, scan and EOI of the primary image
///   SOI
///   APP1  XMP: the hdrgm values
///   APP2  ISO 21496-1: the metadata record
///   the segments of the gain-map JPEG after its SOI
/// </code>
/// </summary>
public static class GainMapJpeg
{
    const string IsoNamespace = "urn:iso:std:iso:ts:21496:-1";
    const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";

    /// <summary>Builds the file from the primary JPEG, the gain-map JPEG and the metadata. <paramref name="iccProfile"/> is the profile of
    /// the primary image; the primary JPEG must not already hold one.</summary>
    public static byte[] Assemble(byte[] primaryJpeg, byte[] gainMapJpeg, GainMapMetadata metadata, byte[] iccProfile)
    {
        int headerEnd = HeaderEnd(primaryJpeg);
        if (gainMapJpeg.Length < 4 || gainMapJpeg[0] != 0xFF || gainMapJpeg[1] != 0xD8) throw new InvalidDataException("the gain map is not a JPEG");
        if (iccProfile.Length > 65535 - 2 - 12 - 2) throw new ArgumentException("the ICC profile does not fit in one segment");
        var gainMapImage = Concat(new byte[] { 0xFF, 0xD8 },
            Segment(0xE1, Terminated(XmpNamespace), Encoding.UTF8.GetBytes(metadata.GainMapXmp())),
            Segment(0xE2, Terminated(IsoNamespace), metadata.IsoRecord()),
            gainMapJpeg.AsSpan(2).ToArray());
        var xmp = Segment(0xE1, Terminated(XmpNamespace), Encoding.UTF8.GetBytes(GainMapMetadata.PrimaryXmp(gainMapImage.Length)));
        var icc = Segment(0xE2, Terminated("ICC_PROFILE"), new byte[] { 1, 1 }, iccProfile);   // chunk 1 of 1
        var iso = Segment(0xE2, Terminated(IsoNamespace), new byte[] { 0, 0, 0, 0 });
        int mpfLength = MpfIndex(0, 0, 0).Length;
        // The offsets of the MPF index count from the byte-order mark of the index: marker (2), length (2) and "MPF\0" (4) come before it.
        long mpfBase = headerEnd + xmp.Length + icc.Length + iso.Length + 8;
        long primaryLength = headerEnd + xmp.Length + icc.Length + iso.Length + (4 + mpfLength) + (primaryJpeg.Length - headerEnd);
        if (primaryLength + gainMapImage.Length > int.MaxValue) throw new InvalidOperationException("the gain-map JPEG would be larger than 2 GB");
        var mpf = Segment(0xE2, MpfIndex((uint)primaryLength, (uint)gainMapImage.Length, (uint)(primaryLength - mpfBase)));
        return Concat(primaryJpeg.AsSpan(0, headerEnd).ToArray(), xmp, icc, iso, mpf, primaryJpeg.AsSpan(headerEnd).ToArray(), gainMapImage);
    }

    /// <summary>The position after the APPn and COM segments that follow the SOI marker: the first table of the image.</summary>
    public static int HeaderEnd(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("not a JPEG");
        int p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF)
        {
            byte marker = jpeg[p + 1];
            if (!((marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE)) break;
            p += 2 + ((jpeg[p + 2] << 8) | jpeg[p + 3]);
        }
        return p;
    }

    /// <summary>
    /// The MP index of the Multi-Picture Format (CIPA DC-007), big endian: the version "0100", the number of images (2) and one 16-byte
    /// entry for each image (attribute, size, offset, two dependent-image numbers). The primary image has the attribute 0x030000
    /// (baseline MP primary image) and the offset 0. The offset of the second image counts from the byte-order mark of this index.
    /// </summary>
    static byte[] MpfIndex(uint primaryLength, uint gainMapImageLength, uint gainMapImageOffset)
    {
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        void U32(uint v) { o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
        o.AddRange(Terminated("MPF"));
        o.AddRange(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }); U32(8);     // "MM", 42, the offset of the directory
        U16(3);                                                         // three entries
        U16(0xB000); U16(7); U32(4); o.AddRange(Encoding.ASCII.GetBytes("0100"));   // MPFVersion
        U16(0xB001); U16(4); U32(1); U32(2);                            // NumberOfImages
        U16(0xB002); U16(7); U32(32); U32(8 + 2 + 3 * 12 + 4);          // MPEntry: 2 × 16 bytes, right after the directory
        U32(0);                                                         // no next directory
        U32(0x030000); U32(primaryLength); U32(0); U16(0); U16(0);
        U32(0); U32(gainMapImageLength); U32(gainMapImageOffset); U16(0); U16(0);
        return o.ToArray();
    }

    static byte[] Segment(byte marker, params byte[][] parts)
    {
        int n = parts.Sum(p => p.Length);
        if (n + 2 > 65535) throw new InvalidOperationException("a JPEG segment cannot be longer than 65535 bytes");
        var s = new byte[4 + n];
        s[0] = 0xFF; s[1] = marker; s[2] = (byte)((n + 2) >> 8); s[3] = (byte)(n + 2);
        int o = 4;
        foreach (var p in parts) { p.CopyTo(s, o); o += p.Length; }
        return s;
    }

    static byte[] Terminated(string s) => Encoding.ASCII.GetBytes(s + "\0");

    static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => (long)p.Length)];
        long k = 0;
        foreach (var p in parts) { Array.Copy(p, 0, o, k, p.Length); k += p.Length; }
        return o;
    }
}
