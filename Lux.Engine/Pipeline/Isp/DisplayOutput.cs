namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// The two float → 8-bit converters that sit after the display ISP. They round **differently** and that is
/// load-bearing: the on-screen tile store uses `cvtps2dq` (round-half-to-even), the JPEG/PPM export path uses
/// `x + copysign(0.5, x)` then `cvttss2si` (round-half-away-from-zero).
/// The CIAPI output pyramid that these converters feed is RGBA8 (Lumen's load path: format 0, one image per level,
/// with the export dims 8320×6240 halved per level).
///
/// **Which applies where**:
/// <list type="bullet">
/// <item><see cref="ToRgba8Display"/> (RNE) is the *display* store — the tag-0x0d tile write that paints the screen.</item>
/// <item>`ImageConvertPixelType&lt;vec4x8ui, vec4x32f&gt;` (half-away) is what `renderForExport&lt;vec4x8ui&gt;`
///   uses, i.e. the one the **companion JPEG** goes through.</item>
/// <item>Every later `vec4x8ui` *image-expression* store — the two u8 resamplers — is RNE again, because
///   those are `cvtps2dq`/`packssdw`/`packuswb` like the display store.</item>
/// </list>
/// </summary>
public static class DisplayOutput
{
    /// <summary>The display store: multiply each lane by 255,
    /// `cvtps2dq` (**round-half-to-even**, the MXCSR default), `packssdw`/`packuswb` (saturate), so
    /// `byte = clamp(RNE(255·v), 0, 255)` with NaN → 0. `order` 0 = RGBA, 1 = BGRA (the swap comes after the
    /// multiply, before the convert); ≥ 2 throws `"Unsupported channel order!"`.</summary>
    public static byte[] ToRgba8Display(Image<Vec4F> src, int order = 0)
    {
        if (order >= 2) throw new InvalidOperationException("Unsupported channel order!");
        var dst = new byte[(long)src.Width * src.Height * 4];
        int k = 0;
        for (int y = 0; y < src.Height; y++)
        {
            var row = src.Row(y);
            for (int x = 0; x < src.Width; x++)
            {
                var v = row[x];
                float a = v.R * 255f, b = v.G * 255f, c = v.B * 255f, d = v.A * 255f;
                if (order == 1) (a, c) = (c, a);   // BGRA: swap R and B
                dst[k++] = DisplayByte(a); dst[k++] = DisplayByte(b); dst[k++] = DisplayByte(c); dst[k++] = DisplayByte(d);
            }
        }
        return dst;
    }

    /// <summary>`cvtps2dq` + `packssdw` + `packuswb`: convert-to-int32 with the current rounding mode
    /// (round-half-to-even), then saturate through int16 to uint8. NaN converts to `0x80000000` → 0.</summary>
    public static byte DisplayByte(float v)
    {
        int i = float.IsNaN(v) || v >= 2147483648f || v <= -2147483649f ? unchecked((int)0x80000000) : (int)MathF.Round(v, MidpointRounding.ToEven);
        short s = i > short.MaxValue ? short.MaxValue : i < short.MinValue ? short.MinValue : (short)i;
        return s > 255 ? (byte)255 : s < 0 ? (byte)0 : (byte)s;
    }

    /// <summary>`ImageConvertPixelType&lt;vec4x8ui, vec4x32f&gt;` and its row kernel — what the
    /// **JPEG** export path (`renderForExport&lt;vec4x8ui&gt;`) uses after multiplying the tile by 255 in
    /// place: `t = x + copysign(0.5f, x)`, `clamp(t, 0, 255)`, then `cvttss2si` = **round-half-away-from-zero**.
    /// Ties therefore differ from the display store; NaN → 0 in both.</summary>
    public static byte[] ToRgba8Export(Image<Vec4F> src)
    {
        var dst = new byte[(long)src.Width * src.Height * 4];
        int k = 0;
        for (int y = 0; y < src.Height; y++)
        {
            var row = src.Row(y);
            for (int x = 0; x < src.Width; x++)
            {
                var v = row[x];
                dst[k++] = ExportByte(v.R * 255f); dst[k++] = ExportByte(v.G * 255f);
                dst[k++] = ExportByte(v.B * 255f); dst[k++] = ExportByte(v.A * 255f);
            }
        }
        return dst;
    }

    /// <summary>`t = x + copysign(0.5f, x)`, `maxss(t, 0)`,
    /// `minss(t, 255)`, `cvttss2si` — round-half-away-from-zero with NaN → 0.</summary>
    public static byte ExportByte(float x)
    {
        float t = x + MathF.CopySign(0.5f, x);
        t = t > 0f ? t : 0f;                 // MAXSS order: NaN → 0
        t = t < 255f ? t : 255f;
        return (byte)(int)t;                 // truncate
    }
}
