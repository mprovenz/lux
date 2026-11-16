using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp.Stages;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Tests of the still formats of Lux: the 16-bit TIFF, the scene-linear TIFF and the HDR gain-map JPEG. The writers are checked with a
/// reader that is written here from the format specifications. A real capture is exported at a small level through the library and
/// through <c>lux-light</c>. The export panel offers the three formats, and the fake library accepts them.
/// </summary>
public sealed class StillFormatTests
{
    readonly ITestOutputHelper _out;
    public StillFormatTests(ITestOutputHelper output) { _out = output; }

    // ================================================================================================================ a TIFF reader

    sealed record Tiff(int Width, int Height, int Bits, int SampleFormat, int Compression, int Predictor, byte[] Samples, Dictionary<int, byte[]> Tags, Dictionary<int, byte[]> Exif);

    /// <summary>Reads a classic little-endian RGB TIFF in strips: no compression, LZW or Deflate, predictor 1, 2 or 3. The result holds the
    /// samples in little-endian byte order, row by row.</summary>
    static Tiff ReadTiff(byte[] f)
    {
        Assert.Equal(new byte[] { 0x49, 0x49, 0x2a, 0x00 }, f[..4]);
        Dictionary<int, byte[]> Ifd(int at)
        {
            var d = new Dictionary<int, byte[]>();
            int n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(at));
            int last = -1;
            for (int i = 0; i < n; i++)
            {
                var e = f.AsSpan(at + 2 + 12 * i, 12);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(e), type = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                Assert.True(tag > last, $"tag {tag} is not in ascending order");
                last = tag;
                int size = count * type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, _ => 8 };
                int from = size <= 4 ? at + 2 + 12 * i + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                if (size > 4) Assert.True(from % 2 == 0, $"the value of tag {tag} starts at an odd offset");
                d[tag] = f.AsSpan(from, size).ToArray();
            }
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at + 2 + 12 * n)));
            return d;
        }
        var t = Ifd((int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(4)));
        int U(int tag, int index = 0) => t[tag].Length / Math.Max(1, CountOf(tag)) == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(t[tag].AsSpan(2 * index)) : (int)BinaryPrimitives.ReadUInt32LittleEndian(t[tag].AsSpan(4 * index));
        int CountOf(int tag) => tag is 273 or 279 ? StripCount() : tag is 258 or 339 ? 3 : 1;
        int StripCount() => (H() + (int)BinaryPrimitives.ReadUInt32LittleEndian(t[278]) - 1) / (int)BinaryPrimitives.ReadUInt32LittleEndian(t[278]);
        int H() => (int)BinaryPrimitives.ReadUInt32LittleEndian(t[257]);
        int w = U(256), h = H(), bits = U(258), comp = U(259), rps = U(278), predictor = t.ContainsKey(317) ? U(317) : 1, format = U(339);
        Assert.Equal(2, U(262)); Assert.Equal(3, U(277)); Assert.Equal(1, U(284));
        int bps = bits / 8, rowBytes = w * 3 * bps;
        var samples = new byte[h * rowBytes];
        for (int s = 0; s < StripCount(); s++)
        {
            int off = U(273, s), len = U(279, s);
            Assert.True(off % 2 == 0, "a strip starts at an odd offset");
            var raw = comp switch
            {
                1 => f.AsSpan(off, len).ToArray(),
                8 => Inflate(f.AsSpan(off, len).ToArray()),
                5 => LzwDecode(f.AsSpan(off, len).ToArray()),
                _ => throw new InvalidDataException($"compression {comp}"),
            };
            int rows = Math.Min(rps, h - s * rps);
            Assert.Equal(rows * rowBytes, raw.Length);
            for (int r = 0; r < rows; r++)
            {
                var row = raw.AsSpan(r * rowBytes, rowBytes);
                if (predictor == 2)
                    for (int i = 3; i < w * 3; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(row[(2 * i)..], (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(row[(2 * i)..]) + BinaryPrimitives.ReadUInt16LittleEndian(row[(2 * (i - 3))..])));
                else if (predictor == 3)
                {
                    for (int i = 3; i < rowBytes; i++) row[i] = (byte)(row[i] + row[i - 3]);
                    var planes = row.ToArray(); int n = w * 3;
                    for (int c = 0; c < n; c++) for (int b = 0; b < bps; b++) row[bps * c + b] = planes[(bps - b - 1) * n + c];
                }
                row.CopyTo(samples.AsSpan((s * rps + r) * rowBytes));
            }
        }
        var exif = t.ContainsKey(34665) ? Ifd((int)BinaryPrimitives.ReadUInt32LittleEndian(t[34665])) : new Dictionary<int, byte[]>();
        return new Tiff(w, h, bits, format, comp, predictor, samples, t, exif);
    }

    static byte[] Inflate(byte[] z)
    {
        using var ms = new MemoryStream();
        using (var s = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress)) s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The LZW decoder of TIFF 6.0 section 13: codes with the most significant bit first, 9 to 12 bits, clear = 256, end = 257.</summary>
    static byte[] LzwDecode(byte[] d)
    {
        var o = new MemoryStream();
        var table = new List<byte[]>();
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i }); table.Add(Array.Empty<byte>()); table.Add(Array.Empty<byte>()); }
        Reset();
        int bit = 0, width = 9; byte[]? old = null;
        int Next()
        {
            int v = 0;
            for (int i = 0; i < width; i++, bit++) v = (v << 1) | (bit / 8 < d.Length ? (d[bit / 8] >> (7 - bit % 8)) & 1 : 0);
            return v;
        }
        while (true)
        {
            int code = Next();
            if (code == 257) break;
            if (code == 256) { Reset(); width = 9; old = null; continue; }
            byte[] s;
            if (old is null) s = table[code];
            else
            {
                s = code < table.Count ? table[code] : old.Append(old[0]).ToArray();
                table.Add(old.Append(s[0]).ToArray());
            }
            o.Write(s);
            old = s;
            if (table.Count == (1 << width) - 1 && width < 12) width++;
        }
        return o.ToArray();
    }

    // ================================================================================================================ the writers

    [Fact]
    public void Tiff_writer_round_trips_each_compression_and_sample_type()
    {
        int w = 150, h = 71;   // more than two strips, and a last strip that is not full
        var rnd = new Random(7);
        foreach (var (bits, isFloat) in new[] { (16, false), (32, true), (16, true) })
        {
            int bps = bits / 8;
            var src = new byte[w * h * 3 * bps];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w * 3; x++)
                {
                    var at = src.AsSpan((y * w * 3 + x) * bps);
                    float v = (x * 7 + y * 13) % 997 / 400f + (rnd.Next(100) == 0 ? rnd.NextSingle() * 5f : 0f);
                    if (!isFloat) BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)(x * 211 + y * 977 + rnd.Next(4)));
                    else if (bits == 32) BinaryPrimitives.WriteSingleLittleEndian(at, v - 0.1f);
                    else BinaryPrimitives.WriteHalfLittleEndian(at, (Half)v);
                }
            foreach (var comp in new[] { TiffCompression.Deflate, TiffCompression.Lzw, TiffCompression.None })
            {
                var image = new TiffImage { Width = w, Height = h, BitsPerSample = bits, FloatingPoint = isFloat, Compression = comp, IccProfile = IccProfiles.Srgb, Threads = 3 };
                using var ms = new MemoryStream();
                TiffImageWriter.Write(ms, image, (y, row) => src.AsSpan(y * w * 3 * bps, w * 3 * bps).CopyTo(row));
                var t = ReadTiff(ms.ToArray());
                Assert.Equal((w, h, bits), (t.Width, t.Height, t.Bits));
                Assert.Equal(isFloat ? 3 : 1, t.SampleFormat);
                Assert.Equal(comp switch { TiffCompression.Deflate => 8, TiffCompression.Lzw => 5, _ => 1 }, t.Compression);
                Assert.Equal(comp == TiffCompression.None ? 1 : isFloat ? 3 : 2, t.Predictor);
                Assert.Equal(IccProfiles.Srgb, t.Tags[34675]);
                Assert.True(src.AsSpan().SequenceEqual(t.Samples), $"{bits}-bit {(isFloat ? "float" : "integer")} {comp}: the samples that the reader decodes are not the samples written");
                _out.WriteLine($"{bits}-bit {(isFloat ? "float" : "integer")} {comp}: {ms.Length} B");
            }
        }
        Assert.Empty(LzwDecode(TiffLzw.Encode(Array.Empty<byte>())));
        // a long run crosses the 4094-entry limit of the LZW table several times
        var big = new byte[700_000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : i / 3);
        Assert.True(big.AsSpan().SequenceEqual(LzwDecode(TiffLzw.Encode(big))));
    }

    [Fact]
    public void Profiles_of_the_tiff_color_spaces_are_valid()
    {
        var p3 = SkiaSharp.SKColorSpace.CreateIcc(IccProfiles.DisplayP3);
        Assert.NotNull(p3);
        Assert.True(p3!.GetNumericalTransferFunction(out var fn));
        Assert.InRange(fn.G, 2.3f, 2.5f);   // the sRGB curve
        var m = p3.ToColorSpaceXyz();
        // the Display P3 colorants, adapted to D50 (the values of the common Display P3 profiles)
        float[,] want = { { 0.5151f, 0.2920f, 0.1571f }, { 0.2412f, 0.6922f, 0.0666f }, { -0.0011f, 0.0419f, 0.7841f } };
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Assert.True(Math.Abs(m[c, r] - want[r, c]) < 2e-3, $"Display P3 colorant [{r},{c}] {m[c, r]} vs {want[r, c]}");
        var linear = SkiaSharp.SKColorSpace.CreateIcc(IccProfiles.LinearProPhoto);
        Assert.NotNull(linear);
        Assert.True(linear!.GammaIsLinear);
        Assert.Same(IccProfiles.DisplayP3, IccProfiles.ForTiff(TiffColorSpace.DisplayP3));
        Assert.Same(IccProfiles.AdobeRgb, IccProfiles.ForTiff(TiffColorSpace.AdobeRgb));
        Assert.Same(IccProfiles.Srgb, IccProfiles.ForTiff(TiffColorSpace.Srgb));
        // white stays white in the Display P3 matrix of the engine, and the sRGB red lies inside Display P3
        var space = ColorSpace.Standard(ColorSpace.LinearDisplayP3, WhitePoint.Of(7), 1);
        var srgb = ColorSpace.Standard(4, WhitePoint.Of(7), 1);
        var t = ColorSpaceConvert.BuildTLinear(srgb, space, ColorSpace.Adaptation(srgb.Wp, space.Wp, 1));
        for (int r = 0; r < 3; r++) Assert.InRange(t[r * 3] + t[r * 3 + 1] + t[r * 3 + 2], 0.9999f, 1.0001f);
        Assert.InRange(t[0], 0.80f, 0.84f); Assert.InRange(t[3], 0.02f, 0.05f);
    }

    static List<(byte Marker, int Offset, int Length)> Segments(byte[] jpg, int start = 0)
    {
        Assert.Equal(0xFF, jpg[start]); Assert.Equal(0xD8, jpg[start + 1]);
        var list = new List<(byte, int, int)>();
        int p = start + 2;
        while (p + 4 <= jpg.Length && jpg[p] == 0xFF && jpg[p + 1] != 0xDA)
        {
            int len = (jpg[p + 2] << 8) | jpg[p + 3];
            list.Add((jpg[p + 1], p, 2 + len));
            p += 2 + len;
        }
        return list;
    }

    static bool Starts(byte[] f, (byte Marker, int Offset, int Length) s, string id) => s.Length >= 4 + id.Length && Encoding.ASCII.GetString(f, s.Offset + 4, id.Length) == id;

    /// <summary>What a gain-map JPEG holds, read back from its bytes.</summary>
    sealed record GainMapFile(byte[] PrimaryWithoutGainMapSegments, byte[] GainMapImage, string PrimaryXmp, string GainMapXmp, byte[] IsoPrimary, byte[] IsoGainMap, byte[] Icc);

    static GainMapFile ReadGainMapFile(byte[] f)
    {
        var segs = Segments(f);
        var mpf = segs.Single(s => s.Marker == 0xE2 && Starts(f, s, "MPF\0"));
        int b = mpf.Offset + 8;   // the byte-order mark of the index
        Assert.Equal("MM\0*", Encoding.ASCII.GetString(f, b, 4));
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(at));
        int ifd = b + (int)U32(b + 4);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(ifd)));
        Assert.Equal(0xB000, BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(ifd + 2)));
        Assert.Equal("0100", Encoding.ASCII.GetString(f, ifd + 2 + 8, 4));
        Assert.Equal(2u, U32(ifd + 2 + 12 + 8));
        int entries = b + (int)U32(ifd + 2 + 24 + 8);
        Assert.Equal(0x030000u, U32(entries));
        int primaryLength = (int)U32(entries + 4);
        Assert.Equal(0u, U32(entries + 8));
        int mapLength = (int)U32(entries + 16 + 4), mapOffset = b + (int)U32(entries + 16 + 8);
        Assert.Equal(primaryLength, mapOffset);
        Assert.Equal(f.Length, mapOffset + mapLength);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, f[(primaryLength - 2)..primaryLength]);
        var xmp = segs.Single(s => s.Marker == 0xE1 && Starts(f, s, "http://ns.adobe.com/xap/1.0/\0"));
        var iso = segs.Single(s => s.Marker == 0xE2 && Starts(f, s, "urn:iso:std:iso:ts:21496:-1\0"));
        var icc = segs.Single(s => s.Marker == 0xE2 && Starts(f, s, "ICC_PROFILE\0"));
        // the order of the inserted segments: XMP, ICC, ISO, MPF, directly before the first table
        Assert.True(xmp.Offset < icc.Offset && icc.Offset < iso.Offset && iso.Offset < mpf.Offset);
        Assert.Equal(0xDB, f[mpf.Offset + mpf.Length + 1]);
        var plain = new List<byte>();
        plain.AddRange(f.AsSpan(0, xmp.Offset).ToArray());
        plain.AddRange(f.AsSpan(mpf.Offset + mpf.Length, primaryLength - (mpf.Offset + mpf.Length)).ToArray());
        var map = f[mapOffset..];
        var msegs = Segments(map);
        var mxmp = msegs.Single(s => s.Marker == 0xE1 && Starts(map, s, "http://ns.adobe.com/xap/1.0/\0"));
        var miso = msegs.Single(s => s.Marker == 0xE2 && Starts(map, s, "urn:iso:std:iso:ts:21496:-1\0"));
        string X(byte[] d, (byte, int Offset, int Length) s) => Encoding.UTF8.GetString(d, s.Offset + 4 + 29, s.Length - 4 - 29);
        byte[] I(byte[] d, (byte, int Offset, int Length) s) => d.AsSpan(s.Offset + 4 + 28, s.Length - 4 - 28).ToArray();
        string pxmp = X(f, xmp);
        Assert.Contains($"Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{mapLength}\"", pxmp);
        Assert.Contains("Item:Semantic=\"Primary\"", pxmp);
        return new GainMapFile(plain.ToArray(), map, pxmp, X(map, mxmp), I(f, iso), I(map, miso), f.AsSpan(icc.Offset + 4 + 14, icc.Length - 4 - 14).ToArray());
    }

    static void CheckMetadata(GainMapFile g, out double min, out double max)
    {
        Assert.Equal(new byte[4], g.IsoPrimary);
        var r = g.IsoGainMap;
        Assert.Equal(5 + 14 * 4, r.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x40 }, r[..5]);
        double F(int k, bool signed) => (signed ? BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(5 + 8 * k)) : BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(5 + 8 * k))) / (double)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(9 + 8 * k));
        double Attr(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(g.GainMapXmp, $"hdrgm:{name}=\"([^\"]*)\"");
            Assert.True(m.Success, $"hdrgm:{name} is missing");
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Contains("hdrgm:Version=\"1.0\"", g.GainMapXmp);
        Assert.Contains("hdrgm:BaseRenditionIsHDR=\"False\"", g.GainMapXmp);
        // the XMP values and the ISO 21496-1 values are the same numbers
        Assert.Equal(Attr("HDRCapacityMin"), F(0, false), 6); Assert.Equal(Attr("HDRCapacityMax"), F(1, false), 6);
        Assert.Equal(Attr("GainMapMin"), F(2, true), 6); Assert.Equal(Attr("GainMapMax"), F(3, true), 6);
        Assert.Equal(Attr("Gamma"), F(4, false), 6);
        Assert.Equal(Attr("OffsetSDR"), F(5, true), 8); Assert.Equal(Attr("OffsetHDR"), F(6, true), 8);
        Assert.Equal(1.0 / 64, F(5, true), 8);
        min = F(2, true); max = F(3, true);
        Assert.True(max > min);
        Assert.Equal(IccProfiles.Srgb, g.Icc);
    }

    [Fact]
    public void Gain_map_container_keeps_the_primary_jpeg_and_lists_the_gain_map()
    {
        int w = 96, h = 64;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int k = (y * w + x) * 4; px[k] = (byte)(x * 2); px[k + 1] = (byte)(y * 3); px[k + 2] = (byte)(x + y); px[k + 3] = 255; }
        byte[] Encode(byte[] pixels, int ww, int hh, bool gray, JpegEncoder options)
        {
            using var ms = new MemoryStream();
            JpegEncoder.Encode(ms, pixels, ww, hh, gray ? ww : ww * 4, gray, options);
            return ms.ToArray();
        }
        var primary = Encode(px, w, h, false, new JpegEncoder { Quality = 98, SubsamplingId = 2, Comment = "a comment" });
        var mapPx = new byte[24 * 16];
        for (int i = 0; i < mapPx.Length; i++) mapPx[i] = (byte)(i * 5);
        var map = Encode(mapPx, 24, 16, true, new JpegEncoder { Quality = 90, SubsamplingId = 0 });
        var meta = new GainMapMetadata { GainMapMin = -0.25, GainMapMax = 1.75, HdrCapacityMin = 0, HdrCapacityMax = 1.75 };
        var file = GainMapJpeg.Assemble(primary, map, meta, IccProfiles.Srgb);
        var g = ReadGainMapFile(file);
        Assert.Equal(primary, g.PrimaryWithoutGainMapSegments);   // without the four inserted segments, the primary image is the JPEG as it was
        CheckMetadata(g, out double min, out double max);
        Assert.Equal(-0.25, min, 6); Assert.Equal(1.75, max, 6);
        // the gain-map image is the gain-map JPEG with two metadata segments after its SOI
        var msegs = Segments(g.GainMapImage);
        Assert.Equal(new byte[] { 0xE1, 0xE2 }, msegs.Take(2).Select(s => s.Marker));
        int after = msegs[1].Offset + msegs[1].Length;
        Assert.True(map.AsSpan(2).SequenceEqual(g.GainMapImage.AsSpan(after)));
        // both images decode, and the primary image decodes to the pixels of the plain JPEG
        using var a = SkiaSharp.SKBitmap.Decode(file); using var b = SkiaSharp.SKBitmap.Decode(primary); using var m = SkiaSharp.SKBitmap.Decode(g.GainMapImage);
        Assert.Equal((w, h), (a.Width, a.Height));
        Assert.True(a.Bytes.AsSpan().SequenceEqual(b.Bytes));
        Assert.Equal((24, 16), (m.Width, m.Height));
        // the checks of the options, and the size limit
        Assert.Throws<ArgumentOutOfRangeException>(() => new GainMapOptions { Scale = 3 }.Validate());
        Assert.Equal((8192, 6144), Lux.Engine.Session.StillExport.LimitedSize((10432, 7824), 8192));
        Assert.Equal((4160, 3120), Lux.Engine.Session.StillExport.LimitedSize((4160, 3120), 8192));
    }

    [Fact]
    public void Hdr_rendition_follows_the_tone_curve_then_rises_to_its_peak()
    {
        var lut = TmoAcrLuts.ForMode(1);
        var r = new HdrRendition(lut, 1f);
        float last = 0f;
        for (int i = 0; i <= 4000; i++)
        {
            float t = i / 100f, y = r.Curve(t);
            Assert.True(y >= last, $"the curve decreases at {t}");
            Assert.True(y <= HdrRendition.Peak);
            if (t <= HdrRendition.KneeInput) Assert.Equal(Lux.Engine.Pipeline.Isp.HighlightRecovery.Evaluate(lut, t), y);
            last = y;
        }
        Assert.True(r.Curve(1f) > lut[1024], "above the knee the rendition is brighter than the tone curve");
        Assert.InRange(r.Curve(40f), 3.9f, HdrRendition.Peak);
        // a dark pixel is the picture of the JPG; a bright pixel keeps its hue order
        Assert.True(r.IsBelowKnee(0.2f, 0.3f, 0.1f));
        Assert.False(r.IsBelowKnee(0.2f, 0.9f, 0.1f));
        r.Pixel(1.5f, 1.0f, 0.5f, out float R, out float G, out float B);
        Assert.True(R > G && G > B && R > 1f);
        // near the sensor limit the color changes to neutral; far below it nothing changes
        float r1 = R, g1 = G, b1 = B;
        float luminance = Lux.Engine.Pipeline.Isp.HighlightRecovery.ProPhotoLuminance(R, G, B);
        Assert.False(HdrRendition.ToNeutralAtLimit(ref r1, ref g1, ref b1, luminance * 2f));
        Assert.Equal((R, G, B), (r1, g1, b1));
        Assert.True(HdrRendition.ToNeutralAtLimit(ref r1, ref g1, ref b1, luminance * 0.9f));
        Assert.Equal(r1, g1, 5); Assert.Equal(g1, b1, 5);
        Assert.Equal(luminance, r1, 5);   // the luminance stays
    }

    [Fact]
    public void License_notice_of_the_gain_map_is_in_place()
    {
        const string Notice = "This product includes Gain Map technology under license by Adobe.";
        string root = RepoRoot();
        Assert.StartsWith("// " + Notice, File.ReadAllText(Path.Combine(root, "Lux.Engine", "Pipeline", "Export", "GainMapJpeg.cs")));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains(Notice, readme[readme.IndexOf("## License", StringComparison.Ordinal)..]);
        string notices = File.ReadAllText(Path.Combine(root, "Lux.App", "THIRD-PARTY-NOTICES.md"));
        Assert.Contains(Notice, notices[notices.IndexOf("## File formats", StringComparison.Ordinal)..]);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

    // ================================================================================================================ a real capture

    static int Cli(string args, out string log)
    {
        var psi = new ProcessStartInfo(Paths.LuxLight, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["LUX_FORBID_DEFAULT_APP_DATA"] = "1";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        log = o + err.Result;
        return p.ExitCode;
    }

    [Fact]
    public async Task Real_export_writes_the_three_formats_from_the_picture_of_the_jpg()
    {
        string original = Path.Combine(Paths.Captures, "L16_00540.lri");
        if (!File.Exists(original)) { _out.WriteLine("no local capture: skipped"); return; }
        string root = Gaps.Fresh("stills");
        string photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        string link = Path.Combine(photos, "L16_00540.lri");
        File.CreateSymbolicLink(link, original);
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "lib", "library.luxlib"), WatchFolders = false, MakeThumbnailsAfterScan = false });
        lib.Settings.AnalyseNewCaptures = false;
        var folder = await lib.AddFolderAsync(photos);
        if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
        string id = (await lib.QueryAsync(new CaptureQuery { Limit = 5 })).Items.Single().Id;
        var sw = Stopwatch.StartNew();
        async Task<string> Export(string dir, string? cs, params string[] formats)
        {
            var options = new ExportOptions { Formats = formats, Level = 3, OutputFolder = Path.Combine(root, dir), UseCache = false, TiffColorSpace = cs };
            var job = await lib.Jobs.WaitAsync(lib.Export(new[] { id }, options).Single());
            Assert.True(job.State == JobState.Done, job.Error);
            foreach (var f in lib.ExportTargets(new[] { id }, options).Single().Files) Assert.Contains(Path.GetFileName(f), new[] { "L16_00540.jpg", "L16_00540.tif", "L16_00540_linear.tif", "L16_00540_hdr.jpg" });
            return Path.Combine(root, dir);
        }
        string all = await Export("all", null, "jpg", "tiff16", "tiff-linear", "jpg-hdr");
        byte[] jpg = File.ReadAllBytes(Path.Combine(all, "L16_00540.jpg")), hdr = File.ReadAllBytes(Path.Combine(all, "L16_00540_hdr.jpg"));
        var t16 = ReadTiff(File.ReadAllBytes(Path.Combine(all, "L16_00540.tif")));
        var lin = ReadTiff(File.ReadAllBytes(Path.Combine(all, "L16_00540_linear.tif")));
        _out.WriteLine($"level-3 export of four formats in {sw.Elapsed.TotalSeconds:0.0} s: jpg {jpg.Length} B, tiff16 {new FileInfo(Path.Combine(all, "L16_00540.tif")).Length} B, tiff-linear {new FileInfo(Path.Combine(all, "L16_00540_linear.tif")).Length} B, jpg-hdr {hdr.Length} B");

        // ---- tiff16: the picture of the JPG at 16 bits, with the profile and the capture date in the Exif directory
        using var decoded = SkiaSharp.SKBitmap.Decode(jpg);
        int w = decoded.Width, h = decoded.Height;
        Assert.Equal((w, h, 16, 1, 8, 2), (t16.Width, t16.Height, t16.Bits, t16.SampleFormat, t16.Compression, t16.Predictor));
        Assert.Equal(IccProfiles.Srgb, t16.Tags[34675]);
        Assert.Equal("0300", Encoding.ASCII.GetString(t16.Exif[0x9000]));
        Assert.Matches(@"^\d{4}:\d\d:\d\d \d\d:\d\d:\d\d\0$", Encoding.ASCII.GetString(t16.Exif[0x9003]));
        Assert.True(t16.Exif.ContainsKey(0x9011), "the time zone of the capture date is in the Exif directory");
        Assert.False(t16.Tags.ContainsKey(0x9003) || t16.Tags.ContainsKey(0x9010), "no Exif date tag in the main directory");
        Assert.Equal("Light\0", Encoding.ASCII.GetString(t16.Tags[0x10f]));
        double sum = 0; int exact = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = decoded.GetPixel(x, y);
                int o = (y * w + x) * 6;
                int r = BinaryPrimitives.ReadUInt16LittleEndian(t16.Samples.AsSpan(o)), g = BinaryPrimitives.ReadUInt16LittleEndian(t16.Samples.AsSpan(o + 2)), b = BinaryPrimitives.ReadUInt16LittleEndian(t16.Samples.AsSpan(o + 4));
                sum += Math.Abs(r / 257.0 - c.Red) + Math.Abs(g / 257.0 - c.Green) + Math.Abs(b / 257.0 - c.Blue);
                if (r % 257 == 0 && g % 257 == 0 && b % 257 == 0) exact++;
            }
        double mean = sum / (3.0 * w * h);
        _out.WriteLine($"tiff16 against the decoded JPG: mean difference {mean:0.00} of 255");
        Assert.True(mean < 3.0, $"the 16-bit TIFF is not the picture of the JPG: mean difference {mean:0.00} of 255");
        Assert.True(exact < w * h / 2, "the 16-bit TIFF holds more than 8-bit values");

        // ---- tiff-linear: the tone curve of the engine gives the 16-bit TIFF again
        Assert.Equal((w, h, 32, 3, 8, 3), (lin.Width, lin.Height, lin.Bits, lin.SampleFormat, lin.Compression, lin.Predictor));
        Assert.Equal(IccProfiles.LinearProPhoto, lin.Tags[34675]);
        var lut = TmoAcrLuts.ForMode(1);   // light_v1, the tone curve of a default export
        var srgbSpace = ColorSpace.Standard(2, WhitePoint.Of(7), 1);
        int off1 = 0; float top = 0f;
        var rowImage = new Image<Vec4F>(w, 1);
        for (int y = 0; y < h; y++)
        {
            var row = rowImage.Row(0);
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 12;
                float r = BinaryPrimitives.ReadSingleLittleEndian(lin.Samples.AsSpan(o)), g = BinaryPrimitives.ReadSingleLittleEndian(lin.Samples.AsSpan(o + 4)), b = BinaryPrimitives.ReadSingleLittleEndian(lin.Samples.AsSpan(o + 8));
                Assert.True(float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b));
                top = Math.Max(top, Math.Max(r, Math.Max(g, b)));
                row[x] = ToneMappingAcrStage.Pixel(new Vec4F(r, g, b, 1f), 1f, lut);
            }
            ColorSpaceConvert.Convert(rowImage, rowImage, ColorSpace.ProPhotoD50, srgbSpace, 1);
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 6;
                var v = row[x];
                float[] f = { v.R, v.G, v.B };
                for (int c = 0; c < 3; c++)
                {
                    int want = BinaryPrimitives.ReadUInt16LittleEndian(t16.Samples.AsSpan(o + 2 * c));
                    int got = (int)Math.Clamp(f[c] * 65535f + 0.5f, 0f, 65535f);
                    if (Math.Abs(want - got) > 1) off1++;
                }
            }
        }
        _out.WriteLine($"tiff-linear through the tone curve against tiff16: {off1} of {w * h * 3} samples differ by more than one code; largest linear value {top:0.00}");
        Assert.True(off1 <= w * h * 3 / 1000, $"{off1} samples of the linear TIFF do not give the 16-bit TIFF through the tone curve");

        // ---- jpg-hdr: the JPG itself with four inserted segments, then the gain map
        var gm = ReadGainMapFile(hdr);
        Assert.Equal(jpg, gm.PrimaryWithoutGainMapSegments);
        CheckMetadata(gm, out double gmin, out double gmax);
        using var mapImage = SkiaSharp.SKBitmap.Decode(gm.GainMapImage);
        Assert.Equal(((w + 3) / 4, (h + 3) / 4), (mapImage.Width, mapImage.Height));
        using var hdrDecoded = SkiaSharp.SKBitmap.Decode(hdr);
        Assert.True(hdrDecoded.Bytes.AsSpan().SequenceEqual(decoded.Bytes), "a reader without gain-map support sees the pixels of the JPG");
        _out.WriteLine($"jpg-hdr: +{hdr.Length - jpg.Length} B, gain map {mapImage.Width}x{mapImage.Height}, log2 gain {gmin:0.###} to {gmax:0.###}");

        // ---- a second export gives the same pixels (the bytes of a Deflate stream can differ between platforms, the pixels cannot)
        string again = await Export("again", null, "tiff16");
        Assert.True(t16.Samples.AsSpan().SequenceEqual(ReadTiff(File.ReadAllBytes(Path.Combine(again, "L16_00540.tif"))).Samples));

        // ---- the other color spaces: the same picture where sRGB can hold the color, with their own profile
        foreach (var (cs, icc) in new[] { ("p3", IccProfiles.DisplayP3), ("adobergb", IccProfiles.AdobeRgb) })
        {
            var wide = ReadTiff(File.ReadAllBytes(Path.Combine(await Export(cs, cs, "tiff16"), "L16_00540.tif")));
            Assert.Equal(icc, wide.Tags[34675]);
            Assert.Equal(new byte[] { 0xff, 0xff }, wide.Exif[0xa001]);   // ColorSpace: uncalibrated
            using var space = SkiaSharp.SKColorSpace.CreateIcc(icc);
            Assert.True(space.GetNumericalTransferFunction(out var fn));
            var toXyz = space.ToColorSpaceXyz(); using var srgbCs = SkiaSharp.SKColorSpace.CreateSrgb(); var sToXyz = srgbCs.ToColorSpaceXyz();
            Assert.True(srgbCs.GetNumericalTransferFunction(out var sfn));
            double worst = 0, total = 0; int n = 0;
            for (int y = 0; y < h; y += 3)
                for (int x = 0; x < w; x += 3)
                {
                    int o = (y * w + x) * 6;
                    var a = new double[3]; var b = new double[3]; bool inside = true;
                    for (int c = 0; c < 3; c++)
                    {
                        int s16 = BinaryPrimitives.ReadUInt16LittleEndian(t16.Samples.AsSpan(o + 2 * c));
                        if (s16 < 2000 || s16 > 63000) inside = false;   // a channel near a limit of sRGB: the wide space can hold another color there
                        a[c] = sfn.Transform(s16 / 65535f);
                        b[c] = fn.Transform(BinaryPrimitives.ReadUInt16LittleEndian(wide.Samples.AsSpan(o + 2 * c)) / 65535f);
                    }
                    if (!inside) continue;
                    for (int r = 0; r < 3; r++)
                    {
                        double xa = sToXyz[0, r] * a[0] + sToXyz[1, r] * a[1] + sToXyz[2, r] * a[2], xb = toXyz[0, r] * b[0] + toXyz[1, r] * b[1] + toXyz[2, r] * b[2];
                        worst = Math.Max(worst, Math.Abs(xa - xb)); total += Math.Abs(xa - xb);
                    }
                    n++;
                }
            _out.WriteLine($"tiff16 in {cs} against sRGB, as XYZ through the two profiles: mean difference {total / (3.0 * n):0.00000}, largest {worst:0.0000} ({n} pixels inside sRGB)");
            Assert.True(n > 1000);
            Assert.True(total / (3.0 * n) < 0.001 && worst < 0.01, $"{cs}: the colors inside sRGB are not the colors of the sRGB file (mean {total / (3.0 * n)}, largest {worst})");
        }

        // ---- lux-light writes the same pixels, with its options
        string cliDir = Path.Combine(root, "cli");
        Assert.True(Cli($"convert \"{link}\" --formats jpg,tiff16,tiff-linear,jpg-hdr --level 3 --no-cache --no-edits --tiff-compression lzw --tiff-depth f16 -o \"{cliDir}\"", out string log) == 0, log);
        var c16 = ReadTiff(File.ReadAllBytes(Path.Combine(cliDir, "L16_00540.tif")));
        Assert.Equal(5, c16.Compression);
        Assert.True(t16.Samples.AsSpan().SequenceEqual(c16.Samples), "lux-light and the library write the same 16-bit TIFF pixels");
        var chalf = ReadTiff(File.ReadAllBytes(Path.Combine(cliDir, "L16_00540_linear.tif")));
        Assert.Equal((16, 3, 5, 3), (chalf.Bits, chalf.SampleFormat, chalf.Compression, chalf.Predictor));
        for (int i = 0; i < w * h * 3; i += 7)
            Assert.Equal((Half)BinaryPrimitives.ReadSingleLittleEndian(lin.Samples.AsSpan(4 * i)), BinaryPrimitives.ReadHalfLittleEndian(chalf.Samples.AsSpan(2 * i)));
        Assert.StartsWith("identical", M4ProofTests.Compare(Path.Combine(cliDir, "L16_00540_hdr.jpg"), Path.Combine(all, "L16_00540_hdr.jpg")));
        Assert.True(Cli($"convert \"{link}\" --formats jpg-hdr --level 3 --no-cache --no-edits --gainmap luminance --gainmap-scale 2 --hdr-max-size 400 -o \"{Path.Combine(root, "cli2")}\"", out log) == 0, log);
        var small = ReadGainMapFile(File.ReadAllBytes(Path.Combine(root, "cli2", "L16_00540_hdr.jpg")));
        using var smallImage = SkiaSharp.SKBitmap.Decode(small.PrimaryWithoutGainMapSegments); using var smallMap = SkiaSharp.SKBitmap.Decode(small.GainMapImage);
        Assert.Equal(400, Math.Max(smallImage.Width, smallImage.Height));
        Assert.Equal(((smallImage.Width + 1) / 2, (smallImage.Height + 1) / 2), (smallMap.Width, smallMap.Height));
        Assert.Equal(SkiaSharp.SKColorType.Gray8, smallMap.ColorType);
        // an option of a format that the run does not write is an error, before anything is rendered
        Assert.Equal(2, Cli($"convert \"{link}\" --formats jpg --tiff-cs p3 --level 3 --no-cache -o \"{Path.Combine(root, "cli3")}\"", out log));
        Assert.Contains("--tiff-cs configures a format this run does not write", log);
        Assert.False(Directory.Exists(Path.Combine(root, "cli3")));
    }

    // ================================================================================================================ the export panel

    [AvaloniaFact]
    public async Task Export_panel_offers_the_three_formats_and_the_tiff_color_space()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 });
        var item = h.Library.Items.First(i => !i.Item.Missing && !i.Item.Unreadable);
        var p = new ExportPanelViewModel(h.Services, new[] { item }, h.Vm.Queue, () => { }, _ => { });
        Assert.Equal(new[] { "JPG + HDR gain map", "TIFF 16-bit", "TIFF linear (HDR)" }, p.Formats.Where(f => f.Key is "jpg-hdr" or "tiff16" or "tiff-linear").Select(f => f.Name));
        Assert.All(p.Formats.Where(f => f.Key is "jpg-hdr" or "tiff16" or "tiff-linear"), f => Assert.False(f.IsOn));   // the default export does not change
        Assert.Equal(new[] { "sRGB", "Display P3", "Adobe RGB" }, p.TiffColorChoices.Select(c => c.Label));
        Assert.False(p.Tiff16Chosen);
        string before = p.Estimate;
        foreach (var f in p.Formats) f.IsOn = f.Key == "tiff16";
        Assert.True(p.Tiff16Chosen);
        Assert.NotEqual(before, p.Estimate);
        Assert.EndsWith(".tif", p.FileNamePreview);
        Assert.Equal("TIFF 16-bit · full size", p.Describe());
        p.TiffColorSpace = p.TiffColorChoices[1];
        Assert.Equal("TIFF 16-bit · full size · TIFF in Display P3", p.Describe());
        foreach (var f in p.Formats) f.IsOn = f.Key == "tiff-linear";
        Assert.False(p.Tiff16Chosen);
        Assert.EndsWith("_linear.tif", p.FileNamePreview);
        foreach (var f in p.Formats) f.IsOn = f.Key == "jpg-hdr";
        Assert.EndsWith("_hdr.jpg", p.FileNamePreview);
        Assert.False(p.JpgChosen);   // the profile option is for the plain JPG; the gain-map JPG always holds the profile
        Assert.Contains("JPG and TIFF", ExportPanelViewModel.Note);
    }

    [AvaloniaFact]
    public async Task Export_panel_queues_the_three_formats_and_remembers_the_color_space()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        var view = h.Window.GetVisualDescendants().OfType<ExportPanel>().Single();
        var combo = view.FindControl<ComboBox>("TiffColorChoice")!;
        Assert.False(combo.IsEffectivelyVisible);
        foreach (var f in panel.Formats) f.IsOn = f.Key is "tiff16" or "tiff-linear" or "jpg-hdr";
        await h.SettleAsync();
        Assert.True(combo.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "TIFF linear (HDR)" && t.IsEffectivelyVisible);
        panel.TiffColorSpace = panel.TiffColorChoices[2];
        panel.Size = panel.Sizes[1];
        var ids = h.Library.Targets();
        var targets = h.Lib.ExportTargets(ids, new ExportOptions { Formats = new[] { "tiff16", "tiff-linear", "jpg-hdr" } });
        Assert.All(targets, t => Assert.Equal(new[] { ".tif", "_linear.tif", "_hdr.jpg" }, t.Files.Select(f => f[f.LastIndexOf("L16_", StringComparison.Ordinal)..]).Select(n => n[9..])));
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Count(j => j.Id > maxBefore) == ids.Count);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.Equal(new[] { "jpg-hdr", "tiff16", "tiff-linear" }, panel.LastOptions!.Formats);
        Assert.Equal("adobergb", panel.LastOptions.TiffColorSpace);
        var job = h.Lib.Jobs.Of(JobKind.Export).First(j => j.Id > maxBefore);
        await h.SettleAsync(() => h.Vm.Queue.Jobs.Any(j => j.Id == job.Id && j.What.Length > 0));
        Assert.Equal("JPG + HDR gain map + TIFF 16-bit + TIFF linear (HDR) · 13 MP · TIFF in Adobe RGB", h.Vm.Queue.Jobs.First(j => j.Id == job.Id).What);
        Assert.Equal("JPG + HDR gain map + TIFF 16-bit + TIFF linear (HDR)", FakeJobQueue.FormatList(panel.LastOptions));
        // the panel opens again with the same choices
        h.Library.OpenExport();
        var again = h.Library.ExportPanel!;
        Assert.Equal(new[] { "jpg-hdr", "tiff16", "tiff-linear" }, again.Formats.Where(f => f.IsOn).Select(f => f.Key));
        Assert.Equal("Adobe RGB", again.TiffColorSpace.Label);
    }

    [AvaloniaFact]
    public async Task Export_panel_offers_keep_highlights_and_jpeg_quality_and_remembers_them()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        var view = h.Window.GetVisualDescendants().OfType<ExportPanel>().Single();
        var box = view.FindControl<CheckBox>("KeepDngHighlightsBox")!;
        var slider = view.FindControl<Slider>("JpegQualitySlider")!;
        var value = view.FindControl<TextBlock>("JpegQualityValue")!;
        // the defaults: DNG and JPG are ticked, so the two controls show; the option is off and the quality is Lumen's
        foreach (var f in panel.Formats) f.IsOn = f.Key is "dng" or "jpg";
        await h.SettleAsync();
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(slider.IsEffectivelyVisible);
        Assert.Equal("Keep highlights above white", box.Content);
        Assert.False(box.IsChecked);
        Assert.Contains("one stop darker", (string)ToolTip.GetTip(box)!);
        Assert.Equal((1d, 100d, 98d), (slider.Minimum, slider.Maximum, slider.Value));
        Assert.Equal("98", value.Text);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "JPEG quality" && t.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "98 is Lumen's value." && t.IsEffectivelyVisible);
        Assert.Equal("DNG + JPG · full size", panel.Describe());
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Avalonia.Vector(0, 300);
        await h.SettleAsync();
        h.Shot("export-panel-dng-jpg");
        // the default export sends neither setting
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.False(panel.LastOptions!.DngKeepHighlights);
        Assert.Null(panel.LastOptions.JpegQuality);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Any(j => j.Id > maxBefore));

        // each control shows only with its formats
        h.Library.OpenExport();
        panel = h.Library.ExportPanel!;
        await h.SettleAsync();
        view = h.Window.GetVisualDescendants().OfType<ExportPanel>().Single();
        box = view.FindControl<CheckBox>("KeepDngHighlightsBox")!;
        slider = view.FindControl<Slider>("JpegQualitySlider")!;
        value = view.FindControl<TextBlock>("JpegQualityValue")!;
        foreach (var f in panel.Formats) f.IsOn = f.Key is "tiff16";
        await h.SettleAsync();
        Assert.False(box.IsEffectivelyVisible);
        Assert.False(slider.IsEffectivelyVisible);
        foreach (var key in new[] { "jpg", "jpg+depth", "jpg-hdr" })
        {
            foreach (var f in panel.Formats) f.IsOn = f.Key == key;
            await h.SettleAsync();
            Assert.True(slider.IsEffectivelyVisible, key);
            Assert.False(box.IsEffectivelyVisible, key);
        }
        foreach (var f in panel.Formats) f.IsOn = f.Key is "dng";
        await h.SettleAsync();
        Assert.True(box.IsEffectivelyVisible);
        Assert.False(slider.IsEffectivelyVisible);

        // the controls change the options of the export
        foreach (var f in panel.Formats) f.IsOn = f.Key is "dng" or "jpg" or "jpg-hdr";
        box.IsChecked = true;
        slider.Value = 80;
        await h.SettleAsync();
        Assert.True(panel.KeepDngHighlights);
        Assert.Equal(80, panel.JpegQualityValue);
        Assert.Equal("80", value.Text);
        Assert.Equal("DNG + JPG + JPG + HDR gain map · full size · DNG keeps highlights · JPEG quality 80", panel.Describe());
        view.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Avalonia.Vector(0, 300);
        await h.SettleAsync();
        h.Shot("export-panel-dng-jpg-set");
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.True(panel.LastOptions!.DngKeepHighlights);
        Assert.Equal(80, panel.LastOptions.JpegQuality);

        // the panel opens again with the same choices
        h.Library.OpenExport();
        var again = h.Library.ExportPanel!;
        Assert.True(again.KeepDngHighlights);
        Assert.Equal(80, again.JpegQualityValue);
        // a setting of a format that is not ticked does not go into the options
        foreach (var f in again.Formats) f.IsOn = f.Key is "tiff16";
        again.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => again.LastOptions != null);
        Assert.False(again.LastOptions!.DngKeepHighlights);
        Assert.Null(again.LastOptions.JpegQuality);
    }

    [Fact]
    public void Stored_export_request_without_the_new_settings_loads_with_the_defaults()
    {
        // a request as the job table holds it from a version without the two settings
        const string old = """{"CaptureId":"c1","Options":{"Formats":["dng","jpg"],"Level":1,"OutputFolder":"/tmp/out","FileNamePattern":"{name}","IfExists":0,"UseSavedEdits":true,"EditsVariant":null,"DepthEffectFNumber":null,"DepthEffectOff":false,"FocusDepthMm":null,"Adjustments":null,"UseCache":true,"LongEdge":null,"NewerToneStyle":null,"Unturned":null,"EmbedColorProfile":false,"TiffColorSpace":null},"Priority":null,"BatchId":null,"BatchTitle":null}""";
        var loaded = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, old));
        Assert.Equal(new[] { "dng", "jpg" }, loaded.Options.Formats);
        Assert.False(loaded.Options.DngKeepHighlights);
        Assert.Null(loaded.Options.JpegQuality);
        var request = Lux.Library.Core.ExportJobs.BuildRequest(loaded.Options, "/tmp/out", "c1");
        Assert.False(request.DngKeepHighlights);
        Assert.Null(request.JpegQuality);   // the engine then uses the quality of Lumen

        // a request with the two settings keeps them through the job table, and the engine request gets them
        var set = new ExportJobRequest("c1", loaded.Options with { DngKeepHighlights = true, JpegQuality = 80, Formats = new[] { "dng", "jpg", "jpg-hdr" } });
        var back = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, Lux.Library.Core.JobRequests.Serialize(set)));
        Assert.True(back.Options.DngKeepHighlights);
        Assert.Equal(80, back.Options.JpegQuality);
        request = Lux.Library.Core.ExportJobs.BuildRequest(back.Options, "/tmp/out", "c1");
        Assert.True(request.DngKeepHighlights);
        Assert.Equal(80, request.JpegQuality);
        Assert.Throws<LibraryException>(() => Lux.Library.Core.ExportJobs.Validate(back.Options with { JpegQuality = 0 }));
        Assert.Throws<LibraryException>(() => Lux.Library.Core.ExportJobs.Validate(back.Options with { JpegQuality = 101 }));
    }

    [Fact]
    public async Task Real_export_applies_the_jpeg_quality_and_keeps_the_dng_highlights()
    {
        string original = Path.Combine(Paths.Captures, "L16_00540.lri");
        if (!File.Exists(original)) { _out.WriteLine("no local capture: skipped"); return; }
        string root = Gaps.Fresh("stills-quality");
        string photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        string link = Path.Combine(photos, "L16_00540.lri");
        File.CreateSymbolicLink(link, original);
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "lib", "library.luxlib"), WatchFolders = false, MakeThumbnailsAfterScan = false });
        lib.Settings.AnalyseNewCaptures = false;
        var folder = await lib.AddFolderAsync(photos);
        if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
        string id = (await lib.QueryAsync(new CaptureQuery { Limit = 5 })).Items.Single().Id;
        async Task<string> Export(string dir, bool keep, int? quality)
        {
            var options = new ExportOptions { Formats = new[] { "dng", "jpg", "jpg-hdr" }, Level = 3, OutputFolder = Path.Combine(root, dir), UseCache = false, DngKeepHighlights = keep, JpegQuality = quality };
            var job = await lib.Jobs.WaitAsync(lib.Export(new[] { id }, options).Single());
            Assert.True(job.State == JobState.Done, job.Error);
            return Path.Combine(root, dir);
        }
        string plain = await Export("plain", false, null), set = await Export("set", true, 80);
        byte[] File_(string dir, string name) => File.ReadAllBytes(Path.Combine(dir, name));

        // the quality applies to the JPG and to the primary image of the gain-map JPG; the gain map keeps its own quality
        byte[] jpg = File_(set, "L16_00540.jpg"), jpgPlain = File_(plain, "L16_00540.jpg");
        Assert.True(jpg.Length < jpgPlain.Length * 0.6, $"quality 80 gives {jpg.Length} B against {jpgPlain.Length} B");
        var gm = ReadGainMapFile(File_(set, "L16_00540_hdr.jpg")); var gmPlain = ReadGainMapFile(File_(plain, "L16_00540_hdr.jpg"));
        Assert.Equal(jpg, gm.PrimaryWithoutGainMapSegments);
        Assert.Equal(jpgPlain, gmPlain.PrimaryWithoutGainMapSegments);
        Assert.Equal(gmPlain.GainMapImage.Length, gm.GainMapImage.Length);
        // the DNG with its highlights kept is another file than the default DNG, of the same size
        Assert.StartsWith("DIFFERENT", M4ProofTests.Compare(Path.Combine(plain, "L16_00540.dng"), Path.Combine(set, "L16_00540.dng")));

        // lux-light writes the same files from the same settings
        string cliPlain = Path.Combine(root, "cli-plain"), cliSet = Path.Combine(root, "cli-set");
        Assert.True(Cli($"convert \"{link}\" --formats dng,jpg,jpg-hdr --level 3 --no-cache --no-edits -o \"{cliPlain}\"", out string log) == 0, log);
        Assert.True(Cli($"convert \"{link}\" --formats dng,jpg,jpg-hdr --level 3 --no-cache --no-edits --dng-highlights keep --jpeg-quality 80 -o \"{cliSet}\"", out log) == 0, log);
        foreach (var name in new[] { "L16_00540.dng", "L16_00540.jpg", "L16_00540_hdr.jpg" })
        {
            Assert.StartsWith("identical", M4ProofTests.Compare(Path.Combine(cliPlain, name), Path.Combine(plain, name)));
            Assert.StartsWith("identical", M4ProofTests.Compare(Path.Combine(cliSet, name), Path.Combine(set, name)));
        }
    }

    [AvaloniaFact]
    public async Task Settings_show_the_file_format_notices()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.ShowSettings();
        await h.SettleAsync();
        var texts = h.Window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("File formats", texts);
        Assert.Contains("This product includes DNG technology under license by Adobe.", texts);
        Assert.Contains("This product includes Gain Map technology under license by Adobe.", texts);
    }
}
