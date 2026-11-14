using System.Buffers.Binary;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The ICC profiles of the JPEG colour spaces, for exports that embed a profile. Embedding is an option and is off by default.
/// Lumen's JPEGs have no profile, only Exif ColorSpace = 1, so the default export does not change.
/// This class builds the profiles in code: ICC v2.1 display profiles (RGB to XYZ, a matrix and tone curves). They use the D50-adapted
/// (Bradford) colorants of the common profiles of these spaces. sRGB IEC 61966-2.1 uses the sRGB formula as a 1024-entry table.
/// Adobe RGB (1998) uses gamma 563/256 = 2.19921875. The renderer's output colour space property selects the profile: 4 'srgb' (the JPEG
/// default) and 5 'adobe_rgb'. The linear spaces (1–3) and 'none' (0) get no profile.
/// </summary>
public static class IccProfiles
{
    static byte[]? _srgb, _adobe;

    /// <summary>sRGB IEC 61966-2.1.</summary>
    public static byte[] Srgb => _srgb ??= Build("sRGB IEC61966-2.1",
        (0.436066, 0.222488, 0.013916), (0.385147, 0.716873, 0.097076), (0.143066, 0.060608, 0.714096), SrgbCurve());

    /// <summary>Adobe RGB (1998) compatible.</summary>
    public static byte[] AdobeRgb => _adobe ??= Build("Adobe RGB (1998) compatible",
        (0.609741, 0.311111, 0.019470), (0.205276, 0.625671, 0.060867), (0.149185, 0.063217, 0.744568), new ushort[] { 563 });

    /// <summary>Gets the profile of a renderer output colour space property (4 = srgb, 5 = adobe_rgb). Returns null for the other values.</summary>
    public static byte[]? ForColorSpaceProperty(int property) => property switch { 4 => Srgb, 5 => AdobeRgb, _ => null };

    static ushort[] SrgbCurve()
    {
        var t = new ushort[1024];
        for (int i = 0; i < t.Length; i++)
        {
            double v = i / (double)(t.Length - 1);
            double lin = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            t[i] = (ushort)Math.Round(lin * 65535.0);
        }
        return t;
    }

    static byte[] Build(string description, (double X, double Y, double Z) red, (double X, double Y, double Z) green, (double X, double Y, double Z) blue, ushort[] curve)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc(description)),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz((0.964203, 1.0, 0.824905))),
            ("rXYZ", Xyz(red)), ("gXYZ", Xyz(green)), ("bXYZ", Xyz(blue)),
            ("rTRC", Curve(curve)), ("gTRC", Curve(curve)), ("bTRC", Curve(curve)),
        };
        int tableSize = 4 + 12 * tags.Count;
        int offset = 128 + tableSize;
        var placed = new List<(string Sig, int Offset, byte[] Data)>();
        foreach (var (sig, data) in tags) { placed.Add((sig, offset, data)); offset += (data.Length + 3) & ~3; }
        var p = new byte[offset];
        var s = p.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)p.Length);
        Ascii(s[4..], "    ");                                  // preferred CMM: none
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 0x02100000);   // version 2.1
        Ascii(s[12..], "mntr"); Ascii(s[16..], "RGB "); Ascii(s[20..], "XYZ ");
        // a fixed date, so that the profile is the same bytes every time
        BinaryPrimitives.WriteUInt16BigEndian(s[24..], 2026); BinaryPrimitives.WriteUInt16BigEndian(s[26..], 11); BinaryPrimitives.WriteUInt16BigEndian(s[28..], 14);
        Ascii(s[36..], "acsp");
        S15(s[68..], 0.964203); S15(s[72..], 1.0); S15(s[76..], 0.824905);   // the PCS illuminant, D50
        Ascii(s[80..], "lux ");                                 // creator
        BinaryPrimitives.WriteUInt32BigEndian(s[128..], (uint)tags.Count);
        for (int i = 0; i < placed.Count; i++)
        {
            var e = s[(132 + 12 * i)..];
            Ascii(e, placed[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], (uint)placed[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)placed[i].Data.Length);
            placed[i].Data.CopyTo(s[placed[i].Offset..]);
        }
        return p;
    }

    static void Ascii(Span<byte> s, string four) { for (int i = 0; i < 4; i++) s[i] = (byte)four[i]; }
    static void S15(Span<byte> s, double v) => BinaryPrimitives.WriteInt32BigEndian(s, (int)Math.Round(v * 65536.0));

    static byte[] Xyz((double X, double Y, double Z) v)
    {
        var b = new byte[20];
        Ascii(b, "XYZ ");
        S15(b.AsSpan(8), v.X); S15(b.AsSpan(12), v.Y); S15(b.AsSpan(16), v.Z);
        return b;
    }

    static byte[] Curve(ushort[] entries)
    {
        var b = new byte[12 + 2 * entries.Length];
        Ascii(b, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12 + 2 * i), entries[i]);
        return b;
    }

    static byte[] Text(string text)
    {
        var a = Encoding.ASCII.GetBytes(text);
        var b = new byte[8 + a.Length + 1];
        Ascii(b, "text");
        a.CopyTo(b, 8);
        return b;
    }

    /// <summary>Writes a textDescriptionType (ICC v2): the ASCII description, and empty Unicode and ScriptCode parts.</summary>
    static byte[] Desc(string text)
    {
        var a = Encoding.ASCII.GetBytes(text);
        var b = new byte[12 + a.Length + 1 + 4 + 4 + 2 + 1 + 67];
        Ascii(b, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(a.Length + 1));
        a.CopyTo(b, 12);
        return b;
    }
}
