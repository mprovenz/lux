namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// Gamut compression of Lux: colors outside the output color space move to its boundary and keep their hue and gradation. Without it a
/// channel below 0 clips to 0, which changes the hue and flattens saturated lights.
///
/// <para>The method is that of the ACES reference gamut compression. For each pixel the largest channel is the achromatic value. The
/// distance of a channel is <c>(max − channel) / max</c>: 0 on the neutral axis, 1 on the gamut boundary, more than 1 outside the gamut.
/// A distance below <see cref="Threshold"/> does not change. A distance from <see cref="Threshold"/> to <see cref="Limit"/> is compressed
/// into the range from <see cref="Threshold"/> to 1 with a power curve of exponent <see cref="Power"/>. The largest channel never
/// changes, so the brightness of the pixel stays almost the same.</para>
///
/// <para>Saturated colors inside the gamut (a distance from <see cref="Threshold"/> to 1) also move a little toward neutral: that is the
/// room the compressed colors need. A pixel with all distances below <see cref="Threshold"/> keeps its bits.</para>
/// </summary>
public static class GamutCompression
{
    /// <summary>The distance where the compression starts.</summary>
    public const float Threshold = 0.8f;
    /// <summary>The distance that the compression moves to the gamut boundary.</summary>
    public const float Limit = 1.25f;
    /// <summary>The exponent of the compression curve.</summary>
    public const float Power = 1.2f;

    const float InvPower = 1f / Power;
    static readonly float Scale = (float)((Limit - (double)Threshold)
        / WineCrt.Pow(WineCrt.Pow((1.0 - Threshold) / (Limit - (double)Threshold), -(double)Power) - 1.0, 1.0 / Power));

    /// <summary>The compressed distance: the distance itself below <see cref="Threshold"/>, 1 at <see cref="Limit"/>.</summary>
    public static float CompressDistance(float distance)
    {
        if (!(distance >= Threshold)) return distance;
        float x = (distance - Threshold) / Scale;
        return Threshold + (Scale * x) / WineCrt.Powf(1f + WineCrt.Powf(x, Power), InvPower);
    }

    /// <summary>Compresses one linear RGB pixel into the gamut of its color space. A pixel that needs no compression keeps its bits.</summary>
    public static void Compress(ref float r, ref float g, ref float b)
    {
        float max = r > g ? r : g; if (b > max) max = b;
        if (!(max > 0f)) return;
        float limit = max - (Threshold * max);       // a channel above this value has a distance below the threshold
        if (r < limit) r = One(r, max);
        if (g < limit) g = One(g, max);
        if (b < limit) b = One(b, max);
    }

    static float One(float c, float max)
    {
        float d = (max - c) / max;
        if (!(d >= Threshold)) return c;
        return max - (CompressDistance(d) * max);
    }

    /// <summary>
    /// Converts an image from a linear color space to the output color space, as <see cref="ColorSpaceConvert.Convert"/> does, and
    /// compresses each pixel into the output gamut before the output encoding. Pixels that need no compression get the same bits as from
    /// <see cref="ColorSpaceConvert.Convert"/>. The image is changed in place.
    /// </summary>
    /// <param name="convert">False when the two color spaces are the same: the pixels are only compressed.</param>
    public static void ConvertCompressed(Image<Vec4F> img, ColorSpace from, ColorSpace to, int adaptation, bool convert = true)
    {
        int w = img.Width, h = img.Height;
        if (!convert || to.Space != 2)
        {   // a linear output: the standard conversion, then the compression
            if (convert) ColorSpaceConvert.Convert(img, img, from, to, adaptation);
            for (int y = 0; y < h; y++)
            {
                var row = img.Row(y);
                for (int x = 0; x < w; x++) { ref var p = ref row[x]; Compress(ref p.R, ref p.G, ref p.B); }
            }
            return;
        }
        // the sRGB output: the same matrix and the same order of operations as the standard conversion, the compression before the encoding
        var T = ColorSpaceConvert.BuildTSrgb(from, to, ColorSpace.Adaptation(from.Wp, to.Wp, adaptation));
        bool ident = ColorSpaceConvert.IsIdentity(T);
        for (int y = 0; y < h; y++)
        {
            var row = img.Row(y);
            for (int x = 0; x < w; x++)
            {
                var v = row[x]; float r = v.R, g = v.G, b = v.B, a = v.A;
                if (!ident)
                {
                    r = ((v.A * 0f) + ((v.B * T[2]) + (v.R * T[0]))) + (v.G * T[1]);
                    g = ((v.A * 0f) + ((v.B * T[5]) + (v.R * T[3]))) + (v.G * T[4]);
                    b = ((v.A * 0f) + ((v.B * T[8]) + (v.R * T[6]))) + (v.G * T[7]);
                    a = ((v.A * 1f) + ((v.B * 0f) + (v.R * 0f))) + (v.G * 0f);
                }
                Compress(ref r, ref g, ref b);
                row[x] = new Vec4F(ColorSpaceConvert.SrgbEncode(r), ColorSpaceConvert.SrgbEncode(g), ColorSpaceConvert.SrgbEncode(b), a);
            }
        }
    }
}
