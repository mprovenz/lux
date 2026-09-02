using System.Buffers.Binary;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>TIFF field types as cp.dll's `FUN_180147720` sizes them (`DAT_18069f880` table).</summary>
public enum TiffType { Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SByte = 6, Undefined = 7, SShort = 8, SLong = 9, SRational = 10, Float = 11, Double = 12 }

/// <summary>One IFD entry (`DNGWriter` map node: tag, type, count, inline value or out-of-line bytes, or a sub-IFD).</summary>
public sealed class TiffEntry
{
    public int Tag; public TiffType Type; public int Count; public byte[] Data = Array.Empty<byte>(); public TiffDirectory? Sub;
    public static int TypeSize(TiffType t) => t switch { TiffType.Byte or TiffType.Ascii or TiffType.SByte or TiffType.Undefined => 1, TiffType.Short or TiffType.SShort => 2, TiffType.Long or TiffType.SLong or TiffType.Float => 4, TiffType.Rational or TiffType.SRational or TiffType.Double => 8, _ => 0 };
    public bool Inline => Sub is null && Data.Length <= 4;   // FUN_180147720: `size < 5` keeps the bytes in the entry
}

/// <summary>A `std::map&lt;int, entry&gt;` IFD serialised exactly as `FUN_180144080` (entries by tag; out-of-line data and sub-IFDs placed in
/// tag order right after the directory, each rounded up to 4 bytes; `FUN_180148a50` sizes).</summary>
public sealed class TiffDirectory
{
    public readonly SortedDictionary<int, TiffEntry> Entries = new();

    public void Set(int tag, TiffType type, int count, byte[] data) => Entries[tag] = new TiffEntry { Tag = tag, Type = type, Count = count, Data = data };
    public void SetSub(int tag, TiffDirectory sub) => Entries[tag] = new TiffEntry { Tag = tag, Type = TiffType.Long, Count = 1, Sub = sub };
    public void Remove(int tag) => Entries.Remove(tag);

    public void Short(int tag, params int[] v) { var d = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(i * 2), (ushort)v[i]); Set(tag, TiffType.Short, v.Length, d); }
    public void Long(int tag, params uint[] v) { var d = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 4), v[i]); Set(tag, TiffType.Long, v.Length, d); }
    public void Bytes(int tag, params byte[] v) => Set(tag, TiffType.Byte, v.Length, v);
    public void Undefined(int tag, byte[] v) => Set(tag, TiffType.Undefined, v.Length, v);
    /// <summary>`FUN_18017d490`: ASCII with the terminating NUL (count = length + 1).</summary>
    public void Ascii(int tag, string s) { var b = Encoding.ASCII.GetBytes(s); var d = new byte[b.Length + 1]; b.CopyTo(d, 0); Set(tag, TiffType.Ascii, d.Length, d); }
    public void Rational(int tag, params (uint N, uint D)[] v) { var d = new byte[v.Length * 8]; for (int i = 0; i < v.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8), v[i].N); BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8 + 4), v[i].D); } Set(tag, TiffType.Rational, v.Length, d); }
    public void SRational(int tag, params (int N, int D)[] v) { var d = new byte[v.Length * 8]; for (int i = 0; i < v.Length; i++) { BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i * 8), v[i].N); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(i * 8 + 4), v[i].D); } Set(tag, TiffType.SRational, v.Length, d); }
    public void Floats(int tag, float[] v) { var d = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(i * 4), v[i]); Set(tag, TiffType.Float, v.Length, d); }

    public const int Den24 = 16777216;   // DAT_18068805c
    /// <summary>x86 `cvttss2si`: truncate toward zero, and yield the "integer indefinite" 0x80000000 when the value does not fit (or is NaN).
    /// C#'s own `(int)` cast has SATURATED since .NET Core 3.0, so it would give 0x7fffffff instead — visible on a 149 mm capture, whose
    /// FocalLength numerator `149 · 2²⁴` overflows: Lumen writes 2147483648, a saturating cast writes 2147483647.</summary>
    static int Cvtt(float f) => System.Runtime.Intrinsics.X86.Sse.IsSupported
        ? System.Runtime.Intrinsics.X86.Sse.ConvertToInt32WithTruncation(System.Runtime.Intrinsics.Vector128.CreateScalar(f))
        : (f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue);
    /// <summary>float → RATIONAL/SRATIONAL over 2²⁴ with truncation (`FUN_180142db0` for the matrices; `FUN_180149b70` types 5/10).</summary>
    public static (int, int) SRat24(float f) => (Cvtt(f * 16777216f), Den24);
    public static (uint, uint) Rat24(float f) => ((uint)Cvtt(f * 16777216f), (uint)Den24);

    static int Align4(int n) => (n + 3) & ~3;

    /// <summary>`FUN_180148a50`: 6 + 12·n + Σ align4(out-of-line / sub-IFD sizes).</summary>
    public int Size()
    {
        int s = 6 + 12 * Entries.Count;
        foreach (var e in Entries.Values)
        {
            if (e.Sub is not null) s += Align4(e.Sub.Size());
            else if (!e.Inline) s += Align4(e.Data.Length);
        }
        return s;
    }

    /// <summary>`FUN_180144080`: write at the stream's current position.</summary>
    public void Write(Stream s)
    {
        long start = s.Position;
        int dataOff = checked((int)start) + 6 + 12 * Entries.Count;
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Entries.Count); s.Write(b[..2]);
        foreach (var e in Entries.Values)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)e.Tag); s.Write(b[..2]);
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(int)e.Type); s.Write(b[..2]);
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)e.Count); s.Write(b);
            if (e.Sub is not null) { BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)dataOff); s.Write(b); dataOff += Align4(e.Sub.Size()); }
            else if (e.Inline) { Span<byte> v = stackalloc byte[4]; e.Data.AsSpan().CopyTo(v); s.Write(v); }
            else { BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)dataOff); s.Write(b); dataOff += Align4(e.Data.Length); }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0); s.Write(b);   // next IFD
        foreach (var e in Entries.Values)
        {
            if (e.Sub is not null) { int n = e.Sub.Size(); e.Sub.Write(s); for (int i = n; i < Align4(n); i++) s.WriteByte(0); }
            else if (!e.Inline) { s.Write(e.Data); for (int i = e.Data.Length; i < Align4(e.Data.Length); i++) s.WriteByte(0); }
        }
    }
}

/// <summary>The values `Exporter::exportDNG` (`18052d340`) and the Exif lambda (`180522ea0`) put into the writer.</summary>
public sealed class DngExportTags
{
    public int Compression = 1;                       // exporter +0x220: 0 uncompressed, 1 lossless JPEG (property 0xf)
    public int Illuminant1 = 2, Illuminant2 = 7;      // profile internal illuminant enums (2..8 → DAT_18069f860)
    public float[] ColorMatrix1 = new float[9], ColorMatrix2 = new float[9], ForwardMatrix1 = new float[9], ForwardMatrix2 = new float[9];
    public (int H, int S, int V) HueSatDims = (32, 32, 1);
    public float[]? HueSatMap1, HueSatMap2;            // FUN_18017bd30 output (h outer, s inner, (hue°, sat, val))
    public string ToneMappingType = "light_v2";        // exporter +0x200
    public float BaselineExposure;                     // exporter +0x1f0
    public float[] Neutral = { -1f, -1f, -1f };       // exporter +0x1f4.. (written only when all > 0)
    // Exif lambda_1
    public bool CameraIsLight = true;                  // FUN_180111c40 == 0 → Make/Model/UniqueCameraModel/Software
    public string Software = "Build 0.26.3 (libcp_v_0_26_1-9-g3c966)";
    public float FNumber;                              // renderer double property 0
    public int Iso;                                    // int property 0
    public int FocalLengthMm;                          // int property 1
    public byte[] UniqueId = new byte[16];             // LightHeader +0x30 (image_unique_id_low LE, image_unique_id_high LE)
    public (int Year, int Month, int Day, int Hour, int Minute, int Second, int TzOffsetMinutes)? TimeStamp;   // image_time_stamp Optional
    public DateTime ModifyTime = DateTime.Now;         // _time64(now) → FUN_180179620
    public int ModifyTzOffsetHours = 0;                // localtime − gmtime hour difference (FUN_180179620)
    public float ExposureTimeSeconds;                  // double property 0x11
    public float ExposureCompensation;                 // double property 0x12
    public int ColorSpaceProperty = 0;                 // int property 0x13 → FUN_18017fa10 case 0/1/2
    public int ExifImageWidth, ExifImageHeight;        // FUN_18017eff0

    static readonly float[][] ToneCurves = LoadToneCurves();
    static float[][] LoadToneCurves()
    {
        using var s = typeof(DngExportTags).Assembly.GetManifestResourceStream("ToneCurves.bin") ?? throw new InvalidOperationException("ToneCurves.bin resource missing");
        var b = new byte[4 * 0x1004]; s.ReadExactly(b);
        var r = new float[4][];
        for (int k = 0; k < 4; k++) { r[k] = new float[1025]; Buffer.BlockCopy(b, k * 0x1004, r[k], 0, 0x1004); }
        return r;
    }
    /// <summary>The four `.rdata` ProfileToneCurve tables (SoT §8.4): acr `DAT_1806f2bac`, light_v1 `DAT_1806f0ba4`, light_v1_lowlight `DAT_1806efba0`, light_v2 `DAT_1806f1ba8`.</summary>
    public static float[] ToneCurve(string type) => type switch
    {
        "acr" => ToneCurves[0], "light_v1" => ToneCurves[1], "light_v1_lowlight" => ToneCurves[2], "light_v2" => ToneCurves[3],
        _ => throw new InvalidOperationException("Unexpected tone curve"),
    };
}

/// <summary>`lt::DNGWriter` (`FUN_1801427e0` ctor, `FUN_1801454c0` write, `FUN_180143b00` IFD0, `FUN_180143fa0` tile tags, `FUN_1801472b0` block → tiles,
/// `FUN_180146d20`/`FUN_180146330` tile encoders) with the Exif object (`FUN_18017cf00` + `FUN_18017ce30` defaults).</summary>
public static class DngWriter
{
    public const int BlockSize = 2048;   // local_178 = 0x80000000800

    /// <summary>`FUN_1801454c0` L18–68: the largest {16…512}² tile whose padded area exceeds W·H by ≤ 5 % (`DAT_180681ed4`); ties → earlier (tw, th) pair of the loop.</summary>
    public static (int W, int H) TileSize(int w, int h)
    {
        int[] cand = { 16, 32, 64, 128, 256, 512 };
        int area = h * w; float inv = 1f / (float)area;
        int bestW = 16, bestH = 16;
        foreach (int th in cand)
        {
            int rows = (h - 1 + th) / th;
            foreach (int tw in cand)
            {
                int cols = (w + tw - 1) / tw;
                if (bestW * bestH < th * tw && (float)(cols * rows * th * tw - area) * inv <= 0.05f) { bestW = tw; bestH = th; }
            }
        }
        if (2048 % bestH != 0 || 2048 % bestW != 0) throw new InvalidOperationException("Bad DNG/ImageGenerator tiling");
        return (bestW, bestH);
    }

    /// <summary>Write the DNG. <paramref name="generator"/> = `exportDNG` lambda_0 for a 2048² block rect (unclamped) → (clamped rect, float RGBA ×16384).</summary>
    public static void Write(Stream s, int width, int height, DngExportTags t, Func<RectI, (RectI Rect, float[] Pixels)> generator, Action<string>? log = null)
    {
        var ifd0 = new TiffDirectory(); var exif = new TiffDirectory();
        // FUN_1801439b0: header with a placeholder IFD offset, patched after the tiles
        s.Write(new byte[] { 0x49, 0x49, 0x2a, 0, 8, 0, 0, 0 });
        var (tw, th) = TileSize(width, height);
        int tilesAcross = (width - 1 + tw) / tw, tilesDown = (height - 1 + th) / th;
        var offsets = new uint[tilesAcross * tilesDown]; var counts = new uint[tilesAcross * tilesDown];
        log?.Invoke($"dng: {width}x{height} tiles {tw}x{th} grid {tilesAcross}x{tilesDown} compression {t.Compression}");
        for (int by = 0; by < height; by += BlockSize)
            for (int bx = 0; bx < width; bx += BlockSize)
            {
                var (rect, px) = generator(new RectI(bx, by, bx + BlockSize, by + BlockSize));
                int w = rect.Width, h = rect.Height;
                int lw = Math.Min(width - bx, w), lh = Math.Min(height - by, h);
                var u16 = ToU16(px, w, h);   // FUN_1801471d0 (converter FUN_18004c880) — the whole block once
                for (int y = 0; y < lh; y += th)
                    for (int x = 0; x < lw; x += tw)
                    {
                        int idx = ((by + y) / th) * tilesAcross + (bx + x) / tw;
                        if (idx >= offsets.Length) throw new InvalidOperationException("DNGWriter: Tile Index overrun");
                        offsets[idx] = (uint)s.Position;
                        int vx1 = Math.Min(x + tw, w), vy1 = Math.Min(y + th, h);
                        int vw = vx1 - x, vh = vy1 - y;
                        if (vw > tw || vh > th) throw new InvalidOperationException("Write window too small");
                        if (t.Compression == 1)
                        {
                            if (vw == tw && vh == th) LosslessJpeg.Encode(s, u16, tw, th, w * 4, (y * w + x) * 4);   // view into the block image
                            else
                            {   // FUN_180146330: zero-filled tile with the view copied at (0,0)
                                var tile = new ushort[tw * th * 4];
                                for (int yy = 0; yy < vh; yy++) Array.Copy(u16, ((y + yy) * w + x) * 4, tile, yy * tw * 4, vw * 4);
                                LosslessJpeg.Encode(s, tile, tw, th, tw * 4);
                            }
                        }
                        else if (t.Compression == 0)
                        {   // FUN_180146d20: u16 = clamp(v + 0.5, 0, 65535) truncated, 6 B/px, rows zero-padded beyond the view
                            var row = new byte[tw * 6];
                            for (int yy = 0; yy < th; yy++)
                            {
                                Array.Clear(row);
                                if (yy < vh)
                                    for (int xx = 0; xx < vw; xx++)
                                        for (int c = 0; c < 3; c++)
                                        {
                                            float v = px[((y + yy) * w + x + xx) * 4 + c] + 0.5f; if (v <= 0f) v = 0f; if (65535f <= v) v = 65535f;
                                            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(xx * 6 + c * 2), (ushort)(short)(int)v);
                                        }
                                s.Write(row);
                            }
                        }
                        else throw new InvalidOperationException("Unhandled case");
                        counts[idx] = (uint)(s.Position - offsets[idx]);
                    }
                log?.Invoke($"dng: block ({bx},{by}) {w}x{h} written, pos {s.Position}");
            }
        // FUN_1801454c0 L~172–183: pad to 4, patch the header offset, write the IFD there
        while (s.Position % 4 != 0) s.WriteByte(0);
        uint ifdPos = (uint)s.Position;
        s.Position = 4; Span<byte> b4 = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b4, ifdPos); s.Write(b4); s.Position = ifdPos;
        // tags
        BuildExifDefaults(ifd0, exif);
        BuildExportTags(ifd0, exif, t, width, height);
        ifd0.Short(0x142, tw); ifd0.Short(0x143, th);   // FUN_180143fa0
        ifd0.Long(0x145, counts); ifd0.Long(0x144, offsets);
        ifd0.Remove(0x116); ifd0.Remove(0x117); ifd0.Remove(0x111);
        BuildIfd0(ifd0, t, width, height);              // FUN_180143b00(w, dims, 3)
        ifd0.Write(s);
    }

    /// <summary>`FUN_1801471d0` via the vec4x32f → Vec4&lt;u16&gt; converter `FUN_18004c880`: `u16 = (int)clamp(v + copysign(0.5, v), 0, 65535)`.</summary>
    public static ushort[] ToU16(float[] px, int w, int h)
    {
        var o = new ushort[w * h * 4];
        for (int i = 0; i < o.Length; i++)
        {
            float v = px[i];
            float r = BitConverter.Int32BitsToSingle((int)((BitConverter.SingleToInt32Bits(v) & unchecked((int)0x80000000)) | 0x3f000000)) + v;
            if (r <= 0f) r = 0f; if (65535f <= r) r = 65535f;
            o[i] = (ushort)((int)r & 0xffff);
        }
        return o;
    }

    /// <summary>`FUN_18017ce30`: Exif sub-IFD link, ExifVersion "0230", ResolutionUnit 2, X/YResolution 72 (`DAT_1806a2b78`), Orientation 1, Flash 0.</summary>
    static void BuildExifDefaults(TiffDirectory ifd0, TiffDirectory exif)
    {
        ifd0.SetSub(0x8769, exif);
        exif.Undefined(0x9000, Encoding.ASCII.GetBytes("0230"));
        ifd0.Short(0x128, 2);
        ifd0.Rational(0x11a, ((uint)(72.0 * 16777216.0), 16777216)); ifd0.Rational(0x11b, ((uint)(72.0 * 16777216.0), 16777216));
        ifd0.Short(0x112, 1);
        exif.Short(0x9209, 0);
    }

    static readonly int[] IlluminantCodes = { 17, 18, 19, 23, 20, 21, 22 };   // DAT_18069f860 for enum 2..8 (A, B, C, D50, D55, D65, D75)

    /// <summary>`Exporter::exportDNG` L57–278 + the Exif lambda_1 (`180522ea0`).</summary>
    static void BuildExportTags(TiffDirectory ifd0, TiffDirectory exif, DngExportTags t, int width, int height)
    {
        int Code(int e) { if ((uint)(e - 2) > 6) throw new InvalidOperationException("unsupported light source illuminant type!"); return IlluminantCodes[e - 2]; }
        ifd0.Short(0xc65a, Code(t.Illuminant1)); ifd0.Short(0xc65b, Code(t.Illuminant2));
        ifd0.SRational(0xc621, Mat(t.ColorMatrix1)); ifd0.SRational(0xc622, Mat(t.ColorMatrix2));
        ifd0.SRational(0xc714, Mat(t.ForwardMatrix1)); ifd0.SRational(0xc715, Mat(t.ForwardMatrix2));
        if (t.HueSatMap1 is not null && t.HueSatMap2 is not null)
        {
            ifd0.Long(0xc6f9, (uint)t.HueSatDims.H, (uint)t.HueSatDims.S, (uint)t.HueSatDims.V);
            ifd0.Floats(0xc6fa, t.HueSatMap1); ifd0.Floats(0xc6fb, t.HueSatMap2);
        }
        var lut = DngExportTags.ToneCurve(t.ToneMappingType);                   // FUN_18017b020 pairs + FUN_18017b1e0 clamp
        var pairs = new float[lut.Length * 2];
        for (int i = 0; i < lut.Length; i++)
        {
            float x = (float)i / (float)(lut.Length - 1), y = lut[i];
            if (x <= 0f) x = 0f; if (1f <= x) x = 1f; if (y <= 0f) y = 0f; if (1f <= y) y = 1f;
            pairs[i * 2] = x; pairs[i * 2 + 1] = y;
        }
        ifd0.Floats(0xc6fc, pairs);
        // BaselineExposure is written by FUN_180143b00 (BuildIfd0)
        if (0f < t.Neutral[0] && 0f < t.Neutral[1] && 0f < t.Neutral[2])
            ifd0.Rational(0xc628, TiffDirectory.Rat24(t.Neutral[0]), TiffDirectory.Rat24(t.Neutral[1]), TiffDirectory.Rat24(t.Neutral[2]));
        // Exif lambda_1
        if (t.CameraIsLight)
        {
            ifd0.Ascii(0x10f, "Light"); ifd0.Ascii(0x110, "L16"); ifd0.Ascii(0xc614, "Light L16"); ifd0.Ascii(0x131, t.Software);
        }
        exif.Rational(0x829d, TiffDirectory.Rat24(t.FNumber));                        // FUN_18017f290
        exif.Long(0x8833, (uint)t.Iso); exif.Short(0x8827, t.Iso); exif.Short(0x8830, 1); // FUN_18017f520
        exif.Rational(0x920a, TiffDirectory.Rat24((float)t.FocalLengthMm));          // FUN_18017f580
        exif.Ascii(0xa420, System.Convert.ToHexString(t.UniqueId).ToLowerInvariant());       // FUN_18017f5a0
        if (t.TimeStamp is { } ts)
        {   // FUN_18017e120
            string d = $"{ts.Year}:{ts.Month:D2}:{ts.Day:D2} {ts.Hour:D2}:{ts.Minute:D2}:{ts.Second:D2}";
            int off = Math.Abs(ts.TzOffsetMinutes);
            string o = $"{(ts.TzOffsetMinutes < 0 ? "-" : "+")}{off / 60:D2}:{off % 60:D2}";
            ifd0.Ascii(0x9003, d); ifd0.Ascii(0x9004, d); ifd0.Ascii(0x9011, o); ifd0.Ascii(0x9012, o);
        }
        {   // FUN_180179620 + FUN_18017d6d0: ModifyDate (local time) and OffsetTime (whole hours)
            var m = t.ModifyTime;
            ifd0.Ascii(0x132, $"{m.Year}:{m.Month:D2}:{m.Day:D2} {m.Hour:D2}:{m.Minute:D2}:{m.Second:D2}");
            int off = Math.Abs(t.ModifyTzOffsetHours * 60);
            ifd0.Ascii(0x9010, $"{(t.ModifyTzOffsetHours * 60 < 0 ? "-" : "+")}{off / 60:D2}:{off % 60:D2}");
        }
        {   // FUN_18017f490
            float et = t.ExposureTimeSeconds;
            if ((double)et < 0.01) exif.Rational(0x829a, (1u, (uint)(int)(1.0 / (double)et + 0.5)));
            else exif.Rational(0x829a, TiffDirectory.Rat24(et));
        }
        exif.SRational(0x9204, TiffDirectory.SRat24(t.ExposureCompensation));         // FUN_18017f500
        {   // FUN_18017fa10 with the lambda's mapping (1/4 → 1, 2/5 → 2, else 0)
            int cs = t.ColorSpaceProperty switch { 1 or 4 => 1, 2 or 5 => 2, _ => 0 };
            if (cs == 0) exif.Short(0xa001, 0xffff);
            else
            {
                var interop = new TiffDirectory();
                interop.Undefined(0x2, Encoding.ASCII.GetBytes("0100"));
                if (cs == 1) { exif.SetSub(0xa005, interop); exif.Short(0xa001, 1); interop.Ascii(0x1, "R98"); }
                else
                {
                    exif.SetSub(0xa005, interop); exif.Short(0xa001, 0xffff);
                    exif.Rational(0xa500, (0x2333334u, 16777216));                                   // gamma 2.2
                    ifd0.Rational(0x13e, (0x500d1bu, 16777216), (0x543958u, 16777216));                // DAT_1806a2b90
                    ifd0.Rational(0x13f, (0xa3d70au, 16777216), (0x547ae1u, 16777216), (0x35c28fu, 16777216), (0xb5c28fu, 16777216), (0x266666u, 16777216), (0x0f5c28u, 16777216));
                    ifd0.Rational(0x211, (0x4c8b43u, 16777216), (0x9645a2u, 16777216), (0x1d2f1au, 16777216));
                    interop.Ascii(0x1, "R03");
                }
            }
        }
        exif.Long(0xa002, (uint)width); exif.Long(0xa003, (uint)height);             // FUN_18017eff0
    }

    static (int, int)[] Mat(float[] m) { var r = new (int, int)[9]; for (int i = 0; i < 9; i++) r[i] = TiffDirectory.SRat24(m[i]); return r; }

    /// <summary>`FUN_180143b00(writer, dims, channels = 3)`.</summary>
    static void BuildIfd0(TiffDirectory ifd0, DngExportTags t, int width, int height)
    {
        if (t.Compression == 0) ifd0.Short(0x103, 1); else if (t.Compression == 1) ifd0.Short(0x103, 7);
        ifd0.Long(0xfe, 0);
        ifd0.Short(0x100, width); ifd0.Short(0x101, height);
        ifd0.Short(0x106, 0x884c);
        ifd0.Short(0x112, 1);
        ifd0.Short(0x115, 3);
        ifd0.Short(0x11c, 1);
        ifd0.Remove(0x828d); ifd0.Remove(0x828e);
        ifd0.Short(0x102, 16, 16, 16);          // DAT_18069f688
        ifd0.Short(0x153, 1, 1, 1);             // DAT_18069f694
        ifd0.Short(0xc61a, 0, 0, 0);            // black level ×3
        ifd0.Short(0xc61d, 0x4000, 0x4000, 0x4000);
        ifd0.Bytes(0xc612, 1, 3, 0, 0);         // DNGVersion
        ifd0.Bytes(0xc613, 1, 2, 0, 0);         // DNGBackwardVersion
        ifd0.Short(0xc619, 1, 1);               // BlackLevelRepeatDim
        ifd0.SRational(0xc62a, TiffDirectory.SRat24(t.BaselineExposure));
    }
}

/// <summary>Minimal DNG/TIFF reader for `dng-diff`: IFD0 entries (in file order), the Exif sub-IFD and the tile streams.</summary>
public sealed class DngFile
{
    public readonly byte[] Bytes;
    public readonly List<TiffEntry> Ifd0 = new(), Exif = new(), Interop = new();
    public long Ifd0Offset, ExifOffset;

    public DngFile(string path)
    {
        Bytes = File.ReadAllBytes(path);
        if (Bytes[0] != (byte)'I' || Bytes[1] != (byte)'I') throw new InvalidDataException("not a little-endian TIFF");
        Ifd0Offset = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4));
        ReadIfd(Ifd0Offset, Ifd0);
        var ex = Ifd0.FirstOrDefault(e => e.Tag == 0x8769);
        if (ex is not null) { ExifOffset = BinaryPrimitives.ReadUInt32LittleEndian(ex.Data); ReadIfd(ExifOffset, Exif); }
        var io = Exif.FirstOrDefault(e => e.Tag == 0xa005);
        if (io is not null) ReadIfd(BinaryPrimitives.ReadUInt32LittleEndian(io.Data), Interop);
    }

    void ReadIfd(long off, List<TiffEntry> into)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan((int)off));
        for (int i = 0; i < n; i++)
        {
            int e = (int)off + 2 + i * 12;
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(e)), type = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(e + 2));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(e + 4));
            int size = TiffEntry.TypeSize((TiffType)type) * count;
            int dataOff = size <= 4 ? e + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(e + 8));
            var data = new byte[size]; Array.Copy(Bytes, dataOff, data, 0, size);
            into.Add(new TiffEntry { Tag = tag, Type = (TiffType)type, Count = count, Data = data });
        }
    }

    public TiffEntry? Get(List<TiffEntry> ifd, int tag) => ifd.FirstOrDefault(e => e.Tag == tag);
    public int Int(List<TiffEntry> ifd, int tag, int idx = 0)
    {
        var e = Get(ifd, tag) ?? throw new KeyNotFoundException($"tag {tag:x}");
        return e.Type switch { TiffType.Short => BinaryPrimitives.ReadUInt16LittleEndian(e.Data.AsSpan(idx * 2)), TiffType.Long => (int)BinaryPrimitives.ReadUInt32LittleEndian(e.Data.AsSpan(idx * 4)), _ => throw new InvalidDataException() };
    }

    /// <summary>Decode the whole image (3 × u16 per pixel) from the tiles (Compression 7 lossless JPEG or 1 uncompressed).</summary>
    public ushort[] DecodeImage(out int width, out int height)
    {
        width = Int(Ifd0, 0x100); height = Int(Ifd0, 0x101);
        int tw = Int(Ifd0, 0x142), th = Int(Ifd0, 0x143), comp = Int(Ifd0, 0x103);
        int across = (width + tw - 1) / tw, down = (height + th - 1) / th;
        var offs = Get(Ifd0, 0x144)!; var cnts = Get(Ifd0, 0x145)!;
        var img = new ushort[width * height * 3];
        for (int ty = 0; ty < down; ty++)
            for (int tx = 0; tx < across; tx++)
            {
                int idx = ty * across + tx;
                int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(offs.Data.AsSpan(idx * 4)), cnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(cnts.Data.AsSpan(idx * 4));
                ushort[] tile; int w, h;
                if (comp == 7) tile = LosslessJpeg.Decode(Bytes.AsSpan(off, cnt), out w, out h);
                else { w = tw; h = th; tile = new ushort[w * h * 3]; for (int i = 0; i < tile.Length; i++) tile[i] = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(off + i * 2)); }
                for (int y = 0; y < h && ty * th + y < height; y++)
                    for (int x = 0; x < w && tx * tw + x < width; x++)
                        for (int c = 0; c < 3; c++) img[((ty * th + y) * width + tx * tw + x) * 3 + c] = tile[(y * w + x) * 3 + c];
            }
        return img;
    }
}
