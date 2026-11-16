using Lux.Engine.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>`CIAPI::ExportImageFormat` — the 5 cases of Lumen's export switch
/// (`RendererPrivate::exportImage` throws "Unexpected export format!" outside 0..3).</summary>
public enum ExportImageFormat
{
    /// <summary>libjpeg-turbo JPEG from the 8-bit display pyramid (runs the output ISP).</summary>
    Jpeg = 0,
    /// <summary>binary PPM from the float render ×255.</summary>
    Ppm = 1,
    /// <summary>`Exporter::exportDNG` — handled before the switch.</summary>
    Dng = 2,
    /// <summary>Radiance `.hdr`, flat RGBE.</summary>
    Hdr = 3,
    /// <summary>JPEG plus the Google GDepth XMP depth map (unreachable from Lumen.exe's dialog).</summary>
    JpegGDepth = 4,
}

/// <summary>
/// The output tuning that Lumen's `RendererPrivate::exportImage` writes into the chosen export level's tuning
/// for the duration of the export and restores afterwards.
///
/// **These writes do not reach the fmt 1/2/3 pixels.** The `exportImage` render step branches on
/// `(fmt | 4) == 4`: only fmt 0 and 4 render through `renderForExport&lt;vec4x8ui&gt;` + the display tile generator (the output
/// ISP). fmt 1, 2 and 3 render through `renderForExport` + the DNG float tile lambda, whose only capture is the
/// `lens_shading.multiplier` float — no output ISP, so neither `output.color_space` nor `tone_mapping.type` is ever read on
/// the `.hdr` path. The port keeps the override because Lumen does it, not because it acts.
/// </summary>
public static class ExportTuningOverride
{
    /// <summary>Lumen's `ImageLocator::exportImageList` → `setProperty(ParamInt 19 = ExportColorSpace, …)`:
    /// `fmt &lt;= 1 ? 4 : fmt == 2 ? 0 : 1` — so a `.hdr` export selects **1 = linear sRGB**. The CIAPI default
    /// (what a caller that never sets the property gets) is 4.</summary>
    public static int ColorSpaceProperty(ExportImageFormat fmt) => (int)fmt <= 1 ? 4 : fmt == ExportImageFormat.Dng ? 0 : 1;

    /// <summary>`exportImage` L~150: renderer property `0x13` → the `output.color_space` tuning string.</summary>
    public static string ColorSpaceName(int property) => property switch
    {
        0 => "none",
        1 => "linear_srgb",
        2 => "linear_adobe_rgb",
        3 => "linear_prophoto_rgb",
        4 => "srgb",
        5 => "adobe_rgb",
        _ => throw new InvalidOperationException("Unexpected color space!"),
    };

    /// <summary>Applies the two `exportImage` writes to <paramref name="levelTuning"/> and returns the saved strings
    /// (`null` = the key was unset) so the caller can restore them, exactly as `exportImage` does around
    /// the export.</summary>
    public static (string? ColorSpace, string? ToneMapping) Apply(Tuning levelTuning, ExportImageFormat fmt, int colorSpaceProperty)
    {
        string? savedCs = levelTuning.Has("output.color_space") ? levelTuning.Str("output.color_space") : null;
        string? savedTm = levelTuning.Has("tone_mapping.type") ? levelTuning.Str("tone_mapping.type") : null;
        levelTuning.Set("output.color_space", ColorSpaceName(colorSpaceProperty));
        if (fmt == ExportImageFormat.Hdr) levelTuning.Set("tone_mapping.type", "linear");   // `LinearTMO` — the ACR curve is not applied to a .hdr
        return (savedCs, savedTm);
    }

    /// <summary>Restores what <see cref="Apply"/> saved.</summary>
    public static void Restore(Tuning levelTuning, (string? ColorSpace, string? ToneMapping) saved)
    {
        if (saved.ColorSpace is not null) levelTuning.Set("output.color_space", saved.ColorSpace);
        if (saved.ToneMapping is not null) levelTuning.Set("tone_mapping.type", saved.ToneMapping);
    }
}

/// <summary>
/// Lumen's format 1 / format 3 export: unlike the DNG, the PPM and Radiance exports render the **whole** requested
/// output as one region — one `GetExportTransformOutput` over `rect = (0, 0, W, H)`, one `renderForExport`, one
/// pass of the export resampler — and there is **no ×16384** (only the DNG block writer applies it).
/// </summary>
public static class ExportFloatImage
{
    /// <summary>Renders the fmt 1/3 float image: `TransformOutput` for the full rect → <see cref="ExportRenderer.RenderSource"/>
    /// → the export resampler. Returns W·H·4 floats (RGBA, alpha carried through and dropped by the writer).</summary>
    public static float[] Render(ExportRenderer renderer, ExportTransform transform, (int W, int H)[] exportDims, bool forceLevel0, Action<string>? log = null)
    {
        var (W, H) = renderer.Size;
        if (W < 1 || H < 1 || W > 99999 || H > 99999) throw new ArgumentOutOfRangeException(nameof(renderer), "Invalid export size!");
        var rect = new RectI(0, 0, W, H);
        var to = ExportTransformOutput.Compute(transform, (W, H), rect, exportDims, forceLevel0);
        log?.Invoke($"export image ({W}x{H}): level {to.Level} src ({to.Source.X0},{to.Source.Y0},{to.Source.X1},{to.Source.Y1}) scale ({to.ScaleX:R},{to.ScaleY:R}) affine [{to.A:R} {to.B:R} {to.C:R} | {to.D:R} {to.E:R} {to.F:R}]");
        var src = renderer.RenderSource(to.Level, to.Source, reportProgress: true);
        int sw = to.Source.Width, sh = to.Source.Height;
        // the same resampler the DNG blocks use, over the whole image
        if (1.5f < to.ScaleX || 1.5f < to.ScaleY)
        {
            int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);
            var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
            var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
            var blurred = ExportResample.ConvSeparable(src, sw, sh, kernelX, kernelY);
            return ExportResample.WarpBilinear(blurred, sw, sh, W, H, to);
        }
        return ExportResample.WarpClamped2(src, sw, sh, W, H, to, renderer.Threads);
    }
}

/// <summary>
/// The Radiance `.hdr` / `.rgbe` writer — Lumen's `ImageWriter` for these extensions,
/// reached through Lumen's image-writer dispatcher (by extension:
/// `.bmp .ppm .pbm .pgm .png .jpg .jpeg .hdr .rgbe .dp .fst .dpc`, else "unsupported image format!").
///
/// Layout: the 35-byte literal signature, `sprintf("-Y %d +X %d\n", h, w)`, then **flat,
/// uncompressed** 4-byte RGBE scanlines top to bottom — despite the `_rle_` in the FORMAT line. File size is therefore
/// exactly `35 + len("-Y h +X w\n") + 4·w·h`.
///
/// Per row the writer runs the row converter (dst = vec3x32f, src = vec4x32f),
/// a plain 12-byte-per-pixel copy that drops alpha, into a `3·w` float scratch buffer, encodes into a
/// `w`-word scratch buffer, and writes `4·w` bytes. The encoder is **not** `frexp`: it splits the exponent by hand and
/// divides by the max channel two different ways —
/// <list type="bullet">
/// <item>pixels `[0, w &amp; ~3)` (only when `w &gt; 3`) through the SIMD body, which uses `rcpps` plus one
/// Newton step `r' = ((1 − m·r)·r) + r` and takes the max as `max(max(R, B), G)`;</item>
/// <item>the `w mod 4` tail through the scalar body, which uses an exact `divss` and takes the max as
/// `max(max(R, G), B)`.</item>
/// </list>
/// Both share: `mag = |m| bits`; finite-and-non-zero iff `(mag − 1) &lt;= 0x7f7ffffe`; normal iff also
/// `mag &gt;= 0x00800000`. Normal → mantissa `(bits &amp; 0x807fffff) | 0x3f000000` (signed, in [0.5, 1)) and the exponent word
/// `((bits &amp; 0x7f800000) &lt;&lt; 1) + 0x02000000` = `(biasedExp + 2) &lt;&lt; 24`, i.e. `E = exp + 128` — **truncated to 32 bits**, so a
/// biased exponent of 254 wraps the E byte to 0. Denormal → mantissa `0.0`; zero / ±Inf / NaN → mantissa `m` itself; in
/// both of those the exponent word is `0x80000000`, i.e. **E = 0x80, not 0** (a zero pixel is written `00 00 00 80`, and a
/// zero or infinite max makes the scale NaN so the color bytes clamp to 0). Each channel is
/// `cvttps2dq(min(max(c · scale, 0), 255))` — MAXPS/MINPS return their **second** operand for NaN, so NaN → 0 — and the
/// word is packed `R | G&lt;&lt;8 | B&lt;&lt;16 | E&lt;&lt;24` and stored little-endian, giving the R,G,B,E byte order.
///
/// **What the file contains, despite the name:** the pixels reaching this writer are the *same* values the DNG stores as
/// `PhotometricInterpretation = LinearRaw`, unscaled — scene-linear, **pre-white-balance camera space**, not
/// display-referred. Its channel means go neutral when divided by the DNG's `AsShotNeutral`, values above 1.0 occur
/// (real highlight headroom), and `hdr × 16384` reproduces the DNG's Linear-Raw samples to the precision of the
/// RGBE 8-bit mantissa. The `÷ neutral`, color-correction
/// and tone-mapping stages all live in the Color-domain output ISP that <see cref="ExportTuningOverride"/>'s remarks
/// show this path never runs.
/// </summary>
public static class RadianceHdrWriter
{
    /// <summary>The 35-byte literal (written as `strlen`-many bytes of a 36-byte NUL-terminated buffer).</summary>
    public static ReadOnlySpan<byte> Signature => "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n"u8;

    /// <summary>`sprintf("-Y %d +X %d\n", h, w)`.</summary>
    public static byte[] ResolutionLine(int w, int h) => Encoding.ASCII.GetBytes($"-Y {h} +X {w}\n");

    /// <summary>Exact size of the file this writer produces.</summary>
    public static long FileSize(int w, int h) => Signature.Length + ResolutionLine(w, h).Length + 4L * w * h;

    /// <summary>The writer: writes <paramref name="rgba"/> (row-major `vec4x32f`, W·H·4 floats,
    /// alpha ignored) as a flat Radiance HDR.</summary>
    public static void Write(Stream stream, int w, int h, ReadOnlySpan<float> rgba)
    {
        // the image must have data and both dimensions positive, else "Failed to write image <ext>"
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"Failed to write image .hdr ({w}x{h})");
        if (rgba.Length < (long)w * h * 4) throw new ArgumentException("image is smaller than w·h·4 floats", nameof(rgba));
        stream.Write(Signature);
        stream.Write(ResolutionLine(w, h));
        var rgb = new float[3 * w];        // malloc(w·3·4) — the vec3x32f row
        var rgbe = new uint[w];            // malloc(w·4)
        var bytes = MemoryMarshal.AsBytes<uint>(rgbe);
        for (int y = 0; y < h; y++)
        {
            var row = rgba.Slice(y * w * 4, w * 4);
            for (int x = 0; x < w; x++) { rgb[3 * x] = row[4 * x]; rgb[3 * x + 1] = row[4 * x + 1]; rgb[3 * x + 2] = row[4 * x + 2]; }
            EncodeRow(rgb, rgbe, w);
            stream.Write(bytes);
        }
    }

    /// <summary>The per-row loop of the writer: the leading `w &amp; ~3` pixels through the
    /// SIMD body when `w &gt; 3`, the remainder through the scalar tail.</summary>
    public static void EncodeRow(ReadOnlySpan<float> rgb, Span<uint> rgbe, int w)
    {
        int i = 0;
        if (w > 3)
        {
            int n4 = w & ~3;
            for (; i < n4; i += 4) EncodeQuad(rgb.Slice(i * 3, 12), rgbe.Slice(i, 4));
        }
        for (; i < w; i++) rgbe[i] = EncodeScalar(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
    }

    // ---- constants, verbatim from Lumen ----------------------------------------------------------------------------
    const uint AbsMask = 0x7fffffffu;
    const uint SignBit = 0x80000000u;
    const uint MinNormal = 0x00800000u;
    const uint FiniteCmp = 0xff7fffffu;      // 0x7f7fffff biased into the signed domain
    const uint ExpByteMask = 0xff000000u;
    const uint ExpBias2 = 0x02000000u;
    const uint MantMask = 0x807fffffu;
    const uint MantExp = 0x3f000000u;        // 0.5f
    const float Two56 = 256.0f;
    const float Max255 = 255.0f;

    /// <summary>MAXSS/MAXPS: `SRC1 &gt; SRC2 ? SRC1 : SRC2`, so NaN in either operand and the ±0/∓0 tie both give SRC2.</summary>
    static float MaxSs(float a, float b) => a > b ? a : b;
    /// <summary>MINSS/MINPS: `SRC1 &lt; SRC2 ? SRC1 : SRC2`.</summary>
    static float MinSs(float a, float b) => a < b ? a : b;

    /// <summary>The exponent split shared by both bodies. Returns the mantissa and the 32-bit exponent word
    /// (`(biasedExp + 2) &lt;&lt; 24` for a normal max, `0x80000000` otherwise).</summary>
    static (float Mantissa, uint ExpWord) Split(float m)
    {
        uint bits = BitConverter.SingleToUInt32Bits(m);
        uint mag = bits & AbsMask;
        if (mag - 1u > 0x7f7ffffeu) return (m, SignBit);                    // zero / ±Inf / NaN (for zero, mag − 1 wraps to 0xffffffff)
        if (mag < MinNormal) return (0f, SignBit);                          // denormal
        uint ex = bits & 0x7f800000u;
        return (BitConverter.UInt32BitsToSingle((bits & MantMask) | MantExp),
                unchecked(ex + ex + ExpBias2));
    }

    /// <summary>Clamp, truncate and pack `R | G&lt;&lt;8 | B&lt;&lt;16 | E`.</summary>
    static uint Pack(float r, float g, float b, float scale, uint expWord)
    {
        uint R = (uint)(int)MinSs(MaxSs(r * scale, 0f), Max255);
        uint G = (uint)(int)MinSs(MaxSs(g * scale, 0f), Max255);
        uint B = (uint)(int)MinSs(MaxSs(b * scale, 0f), Max255);
        return ((R | expWord) | (B << 16)) | (G << 8);
    }

    /// <summary>The scalar tail: `m = max(max(r, g), b)`, `scale = (mantissa·256) / m` with an
    /// exact `divss`.</summary>
    public static uint EncodeScalar(float r, float g, float b)
    {
        float m = MaxSs(MaxSs(r, g), b);                     // this order: MAXSS returns the second operand on NaN
        var (mant, expWord) = Split(m);
        float scale = mant * Two56;
        scale = scale / m;                                   // exact division (DIVSS), not a reciprocal
        return Pack(r, g, b, scale, expWord);
    }

    /// <summary>The SIMD body, four pixels at a time: `m = max(max(R, B), G)` and
    /// `scale = (mantissa·256) · newton(rcpps(m))`. Lumen's deinterleave
    /// is pure data movement. This code does it by indexing.</summary>
    static void EncodeQuad(ReadOnlySpan<float> rgb12, Span<uint> dst)
    {
        var R = Vector128.Create(rgb12[0], rgb12[3], rgb12[6], rgb12[9]);
        var G = Vector128.Create(rgb12[1], rgb12[4], rgb12[7], rgb12[10]);
        var B = Vector128.Create(rgb12[2], rgb12[5], rgb12[8], rgb12[11]);
        if (!Simd.IsSupported)
        {
            for (int k = 0; k < 4; k++) dst[k] = EncodeQuadLaneFallback(R[k], G[k], B[k]);
            return;
        }
        var m = Simd.Max(Simd.Max(R, B), G);                                                          // this order: MAXPS returns the second operand on NaN
        var mi = m.AsInt32();
        var mag = Simd.And(mi, Vector128.Create(unchecked((int)AbsMask)));
        var geMin = Simd.CompareEqual(Simd.Max(mag.AsUInt32(), Vector128.Create(MinNormal)).AsInt32(), mag);
        var finite = Simd.CompareGreaterThan(
            Vector128.Create(unchecked((int)FiniteCmp)),
            Simd.Xor(Simd.Add(mag, Vector128.Create(-1)), Vector128.Create(unchecked((int)SignBit))));
        var isNormal = Simd.And(geMin, finite);
        var expWord = Simd.Add(Simd.And(Simd.Add(mi, mi), Vector128.Create(unchecked((int)ExpByteMask))),
                               Vector128.Create(unchecked((int)ExpBias2)));
        var E = Simd.BlendVariable(Vector128.Create(unchecked((int)SignBit)).AsSingle(), expWord.AsSingle(), isNormal.AsSingle()).AsInt32();
        var rcp = IntelApprox.Reciprocal(m);                                                                // approximate reciprocal (RCPPS)
        var isDenorm = Simd.And(Simd.CompareGreaterThan(Vector128.Create(unchecked((int)MinNormal)), mag), finite);
        var mant = Simd.BlendVariable(m, Vector128<float>.Zero, isDenorm.AsSingle());
        mant = Simd.BlendVariable(mant,
            Simd.Or(Simd.And(m, Vector128.Create(unchecked((int)MantMask)).AsSingle()), Vector128.Create(unchecked((int)MantExp)).AsSingle()),
            isNormal.AsSingle());
        // one Newton step: r' = ((1 − m·r)·r) + r
        var nr = Simd.Add(Simd.Multiply(Simd.Subtract(Vector128.Create(1f), Simd.Multiply(m, rcp)), rcp), rcp);
        var scale = Simd.Multiply(Simd.Multiply(mant, Vector128.Create(Two56)), nr);
        var zero = Vector128<float>.Zero; var c255 = Vector128.Create(Max255);
        var Ri = Simd.ConvertToVector128Int32WithTruncation(Simd.Min(Simd.Max(Simd.Multiply(R, scale), zero), c255));
        var o = Simd.Or(Ri, E);
        var Gp = Simd.Multiply(G, scale);
        var Bi = Simd.ConvertToVector128Int32WithTruncation(Simd.Min(Simd.Max(Simd.Multiply(scale, B), zero), c255));
        o = Simd.Or(Simd.ShiftLeftLogical(Bi, 16), o);
        var Gi = Simd.ConvertToVector128Int32WithTruncation(Simd.Min(Simd.Max(Gp, zero), c255));
        o = Simd.Or(Simd.ShiftLeftLogical(Gi, 8), o);
        o.AsUInt32().CopyTo(dst);
    }

    /// <summary>Lane-wise mirror of <see cref="EncodeQuad"/> for hosts without vector hardware. `rcpps` has no portable equivalent,
    /// so this substitutes an exact reciprocal: it is faithful in structure but **not** guaranteed bit-identical.</summary>
    static uint EncodeQuadLaneFallback(float r, float g, float b)
    {
        float m = MaxSs(MaxSs(r, b), g);
        var (mant, expWord) = Split(m);
        float r0 = IntelApprox.ReciprocalScalar(Vector128.CreateScalar(m)).ToScalar();
        float nr = ((1f - m * r0) * r0) + r0;
        return Pack(r, g, b, (mant * Two56) * nr, expWord);
    }

    /// <summary>Reads a flat Radiance file this writer (or Lumen) produced: the two header lines plus `h` rows of `w`
    /// RGBE words. Used by `hdr-diff`; it deliberately does not implement RLE, which Lumen never emits.</summary>
    public static (int W, int H, byte[] Rgbe, byte[] Header) Read(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < Signature.Length || !d.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("not a Radiance file written by Lumen or Lux (signature mismatch)");
        int nl = Array.IndexOf(d, (byte)'\n', Signature.Length);
        if (nl < 0) throw new InvalidDataException("truncated resolution line");
        string res = Encoding.ASCII.GetString(d, Signature.Length, nl - Signature.Length);
        var parts = res.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "-Y" || parts[2] != "+X") throw new InvalidDataException($"unexpected resolution line '{res}'");
        int h = int.Parse(parts[1]), w = int.Parse(parts[3]);
        int off = nl + 1;
        if (d.Length - off != 4L * w * h) throw new InvalidDataException($"{path}: {d.Length - off} pixel bytes, expected {4L * w * h} (run-length encoded files are not supported)");
        return (w, h, d.AsSpan(off).ToArray(), d.AsSpan(0, off).ToArray());
    }

    /// <summary>RGBE → linear float, the standard inverse of the encoding above (`v = c · 2^(E − 136)`), for diffing only.</summary>
    public static (float R, float G, float B) Decode(byte r, byte g, byte b, byte e)
    {
        if (e == 0) return (0f, 0f, 0f);
        float f = MathF.ScaleB(1f, e - (128 + 8));
        return (r * f, g * f, b * f);
    }
}
