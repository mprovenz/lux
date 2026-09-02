using System.Text;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// `ExportImageFormat` 1 — the binary PPM writer (spec `a-display-isp.md` §12.5).
///
/// fmt 1 is a sibling of fmt 3, not of fmt 0: `exportImage::lambda_2` (`0x180523d60`) gates the display/output ISP on
/// `(fmt | 4) == 4`, so 1, 2 and 3 all render the **`vec4x32f` float image** through `FUN_1805253c0` + the DNG tile
/// lambda and the DNG resampler `FUN_18052dfc0`. fmt 1 then converts that float image to 8-bit inline at
/// `0x180529730` — `mulps [0x180682420]` (255.0) / `cvtps2dq` / `packssdw` / `packuswb`, i.e. **round-half-to-even
/// with saturation**, the same kernel as the display store (`DisplayOutput.DisplayByte`), NOT the export converter's
/// round-half-away-from-zero — and hands the `vec4x8ui` image to `FUN_18030d670(img8, ".ppm", stream)`.
///
/// Ghidra renders the conversion as `(int)(v * 255.0f)`, which reads as truncation; the disassembly is authoritative
/// and it is not.
/// </summary>
public static class PpmWriter
{
    /// <summary>`sprintf("P%c\n# Light image exporter\n%i %i\n%i\n", '0' + 5 + isColor, w, h, 255)` — `isColor` is 1
    /// for the RGB destination, giving `P6`.</summary>
    public static byte[] Header(int w, int h) => Encoding.ASCII.GetBytes($"P6\n# Light image exporter\n{w} {h}\n255\n");

    /// <summary>Exact size of the file this writer produces.</summary>
    public static long FileSize(int w, int h) => Header(w, h).Length + 3L * w * h;

    /// <summary>Writes <paramref name="rgba"/> (row-major `vec4x32f`, W·H·4 floats) as a binary PPM: top-to-bottom
    /// rows of `w·3` bytes through `getRowConverter(dst = 0x0b RGB u8, src)`, alpha dropped.</summary>
    public static void Write(Stream stream, int w, int h, ReadOnlySpan<float> rgba)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"Failed to write image .ppm ({w}x{h})");
        if (rgba.Length < (long)w * h * 4) throw new ArgumentException("image is smaller than w·h·4 floats", nameof(rgba));
        stream.Write(Header(w, h));
        var row = new byte[w * 3];
        for (int y = 0; y < h; y++)
        {
            var src = rgba.Slice(y * w * 4, w * 4);
            for (int x = 0; x < w; x++)
            {
                // `mulps [0x180682420]` (255.0) then `cvtps2dq`/`packssdw`/`packuswb` — DisplayByte is only the
                // convert-and-saturate half, so the ×255 belongs here, exactly as `ToRgba8Display` does it.
                row[3 * x] = DisplayOutput.DisplayByte(src[4 * x] * 255f);
                row[3 * x + 1] = DisplayOutput.DisplayByte(src[4 * x + 1] * 255f);
                row[3 * x + 2] = DisplayOutput.DisplayByte(src[4 * x + 2] * 255f);
            }
            stream.Write(row);
        }
    }
}
