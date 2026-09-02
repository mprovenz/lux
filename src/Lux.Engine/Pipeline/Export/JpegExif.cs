using System.Buffers.Binary;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The Exif APP1 block of the companion JPEG (`a-display-isp.md` §12.3). It is built by the **same** two functions the
/// DNG uses — `lt::Exif::Exif` `0x18017cc00` → the defaults builder `0x18017ce30`, then `exportImage::lambda_1`
/// `0x180522ea0` (Make/Model/Software/UniqueCameraModel, FNumber/ISO/focal/UniqueID, the timestamps, ExposureTime,
/// ExposureCompensation and the colour-space block from renderer property 0x13) — with three JPEG-only additions made
/// at `0x18052a0xx`:
/// <list type="bullet">
/// <item>`FUN_18017eff0(exif, &amp;image.w)` → Exif `0xA002` PixelXDimension / `0xA003` PixelYDimension (LONG)</item>
/// <item>`FUN_18017f040(exif, 1)` → Exif `0x9101` ComponentsConfiguration = `01 02 03 00` (Y,Cb,Cr)</item>
/// <item>`FUN_18017ef00(exif, 0)` → IFD0 `0x0213` YCbCrPositioning = 1 (centered)</item>
/// </list>
/// It carries **none** of the DNG-only tags (no illuminants, colour/forward matrices, HueSatMap, ProfileToneCurve or
/// AsShotNeutral) — those are written by `Exporter::exportDNG`, not by the Exif filler. Serialised by `FUN_18017d150`
/// as `"Exif\0\0" + "II" 2A 00 08 00 00 00 + IFD0` with `IFD::serialize` `0x180144080` (the same little-endian,
/// tag-ordered, align-4 serialiser as the DNG — reused here through <see cref="TiffDirectory"/>).
///
/// This duplicates the shared half of <c>DngWriter.BuildExportTags</c> deliberately: the DNG path is byte-verified
/// against Lumen and is not touched by this row.
/// </summary>
public static class JpegExif
{
    /// <summary>Build the whole APP1 payload (`"Exif\0\0"` + the TIFF block) for an image of
    /// <paramref name="width"/>×<paramref name="height"/>.</summary>
    public static byte[] Build(DngExportTags t, int width, int height, int componentsConfiguration = 1, int yCbCrPositioning = 0)
    {
        var ifd0 = new TiffDirectory(); var exif = new TiffDirectory();

        // ---- FUN_18017ce30 (the Exif ctor's defaults) — identical to the DNG's
        ifd0.SetSub(0x8769, exif);
        exif.Undefined(0x9000, Encoding.ASCII.GetBytes("0230"));           // ExifVersion
        ifd0.Short(0x128, 2);                                              // ResolutionUnit = inches
        ifd0.Rational(0x11a, ((uint)(72.0 * 16777216.0), 16777216));       // XResolution 72
        ifd0.Rational(0x11b, ((uint)(72.0 * 16777216.0), 16777216));       // YResolution 72
        ifd0.Short(0x112, 1);                                              // Orientation
        exif.Short(0x9209, 0);                                             // Flash

        // ---- exportImage::lambda_1 (`180522ea0`)
        if (t.CameraIsLight)
        {
            ifd0.Ascii(0x10f, "Light"); ifd0.Ascii(0x110, "L16");
            ifd0.Ascii(0xc614, "Light L16"); ifd0.Ascii(0x131, t.Software);
        }
        exif.Rational(0x829d, TiffDirectory.Rat24(t.FNumber));                            // FUN_18017f290 FNumber
        exif.Long(0x8833, (uint)t.Iso); exif.Short(0x8827, t.Iso); exif.Short(0x8830, 1);  // FUN_18017f520 ISO + SensitivityType
        exif.Rational(0x920a, TiffDirectory.Rat24((float)t.FocalLengthMm));               // FUN_18017f580
        exif.Ascii(0xa420, System.Convert.ToHexString(t.UniqueId).ToLowerInvariant());     // FUN_18017f5a0 ImageUniqueID
        if (t.TimeStamp is { } ts)
        {   // FUN_18017e120
            string d = $"{ts.Year}:{ts.Month:D2}:{ts.Day:D2} {ts.Hour:D2}:{ts.Minute:D2}:{ts.Second:D2}";
            int off = Math.Abs(ts.TzOffsetMinutes);
            string o = $"{(ts.TzOffsetMinutes < 0 ? "-" : "+")}{off / 60:D2}:{off % 60:D2}";
            ifd0.Ascii(0x9003, d); ifd0.Ascii(0x9004, d); ifd0.Ascii(0x9011, o); ifd0.Ascii(0x9012, o);
        }
        {   // FUN_180179620 + FUN_18017d6d0 — the wall-clock time of the export (the one field that cannot match a
            // different run of Lumen; the DNG's 0x0132 has exactly the same property)
            var m = t.ModifyTime;
            ifd0.Ascii(0x132, $"{m.Year}:{m.Month:D2}:{m.Day:D2} {m.Hour:D2}:{m.Minute:D2}:{m.Second:D2}");
            int off = Math.Abs(t.ModifyTzOffsetHours * 60);
            ifd0.Ascii(0x9010, $"{(t.ModifyTzOffsetHours * 60 < 0 ? "-" : "+")}{off / 60:D2}:{off % 60:D2}");
        }
        {   // FUN_18017f490 ExposureTime
            float et = t.ExposureTimeSeconds;
            if ((double)et < 0.01) exif.Rational(0x829a, (1u, (uint)(int)(1.0 / (double)et + 0.5)));
            else exif.Rational(0x829a, TiffDirectory.Rat24(et));
        }
        exif.SRational(0x9204, TiffDirectory.SRat24(t.ExposureCompensation));              // FUN_18017f500
        {   // FUN_18017fa10 — property 0x13; a JPEG export always sets it to 4 (`srgb`) → ColorSpace 1 + InteropIFD
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
                    exif.Rational(0xa500, (0x2333334u, 16777216));
                    ifd0.Rational(0x13e, (0x500d1bu, 16777216), (0x543958u, 16777216));
                    ifd0.Rational(0x13f, (0xa3d70au, 16777216), (0x547ae1u, 16777216), (0x35c28fu, 16777216), (0xb5c28fu, 16777216), (0x266666u, 16777216), (0x0f5c28u, 16777216));
                    ifd0.Rational(0x211, (0x4c8b43u, 16777216), (0x9645a2u, 16777216), (0x1d2f1au, 16777216));
                    interop.Ascii(0x1, "R03");
                }
            }
        }

        // ---- the three JPEG-only calls, in the order `FUN_1805290f0` makes them
        exif.Long(0xa002, (uint)width); exif.Long(0xa003, (uint)height);                   // FUN_18017eff0
        exif.Undefined(0x9101, componentsConfiguration == 1                                 // FUN_18017f040
            ? new byte[] { 1, 2, 3, 0 } : new byte[] { 4, 5, 6, 0 });
        ifd0.Short(0x213, yCbCrPositioning switch { 0 => 1, 1 => 2, _ => throw new InvalidOperationException("Unhandled case") });   // FUN_18017ef00

        // ---- FUN_18017d150: "Exif\0\0" + the TIFF header + IFD0
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Exif")); ms.WriteByte(0); ms.WriteByte(0);
        ms.Write(new byte[] { 0x49, 0x49, 0x2a, 0x00 });
        Span<byte> b4 = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b4, 8); ms.Write(b4);
        // TiffDirectory.Write bases its out-of-line offsets on the stream position, so serialise the IFD into its own
        // buffer (offset 8 = right after the TIFF header) and append it.
        using var im = new MemoryStream();
        im.Write(new byte[8]);                 // stand in for the TIFF header so positions are TIFF-relative
        ifd0.Write(im);
        ms.Write(im.GetBuffer(), 8, (int)im.Length - 8);
        return ms.ToArray();
    }
}
