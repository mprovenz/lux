using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The HDR rendition of a picture: the image that an HDR display shows from a gain-map JPEG at full headroom. It is made from the same
/// linear image as the JPG, before the tone curve. Values are linear with SDR white at 1, and go up to <see cref="Peak"/>.
///
/// <para>The curve (<see cref="Curve"/>) is the tone curve of the JPG for the darker half of its input range, so shadows and midtones
/// are the same in both renditions. Above the input <see cref="KneeInput"/> the curve continues as a straight line with the slope that
/// the tone curve has there: the highlights keep the contrast of the scene, where the JPG compresses them and clips at white. From
/// <see cref="RollOffStart"/> the line bends and approaches <see cref="Peak"/>:
/// <c>y = start + (peak − start) · (1 − exp(−(line − start) / (peak − start)))</c>.</para>
///
/// <para>The three channels go through the curve in the same way as in the tone stage of the JPG: the largest and the smallest channel
/// take their value from the curve, and the middle channel keeps its relative position between them. This keeps the hue.</para>
///
/// <para>Where the sensor reached its limit, the image holds little or no detail and often a wrong color: a flat area with a visible
/// edge and a tint. For such tones the rendition changes to the neutral color of the same luminance (<see cref="ToNeutralAtLimit"/>, with the weight of
/// <see cref="HighlightRecovery.DesaturationWeight"/>): the change starts where the luminance is 85 % of the level that a white surface
/// has at the sensor limit, and is complete at that level. That level is not the same over the frame, because the correction of the
/// lens vignetting increases the values toward the corners. The caller gives it for each pixel. The luminance decides, not the largest
/// channel: a saturated color such as a blue sky has one large channel long before the sensor reaches its limit.</para>
/// </summary>
public sealed class HdrRendition
{
    /// <summary>The input of the tone curve up to which the rendition follows the curve.</summary>
    public const float KneeInput = 0.5f;
    /// <summary>The output level, as a multiple of SDR white, where the roll-off toward <see cref="Peak"/> starts.</summary>
    public const float RollOffStart = 2f;
    /// <summary>The largest output level, as a multiple of SDR white.</summary>
    public const float Peak = 4f;

    readonly float[] _lut; readonly float _gain, _kneeOutput, _slope;

    /// <param name="toneTable">The tone table of the JPG: 1025 values for the inputs 0 to 1.</param>
    /// <param name="gain">The exposure gain of the tone stage (2 to the power of its exposure offset).</param>
    public HdrRendition(float[] toneTable, float gain)
    {
        _lut = toneTable; _gain = gain;
        _kneeOutput = HighlightRecovery.Evaluate(toneTable, KneeInput);
        const float step = 1f / 1024f;
        _slope = (HighlightRecovery.Evaluate(toneTable, KneeInput + step) - HighlightRecovery.Evaluate(toneTable, KneeInput - step)) * 512f;
    }

    /// <summary>The output of the curve for the tone-curve input <paramref name="t"/>.</summary>
    public float Curve(float t)
    {
        if (t <= KneeInput) return HighlightRecovery.Evaluate(_lut, t);
        float line = _kneeOutput + _slope * (t - KneeInput);
        if (line <= RollOffStart) return line;
        const float range = Peak - RollOffStart;
        return RollOffStart + range * (1f - WineCrt.Expf(-(line - RollOffStart) / range));
    }

    // The exposure ramp of the tone stage (the function of the DNG specification that maps the black point to 0 with a quadratic toe):
    // black 0.005, toe radius 0.0025, white 1.
    const float Black = 0.005f, Radius = 0.0025f;
    static float Ramp(float v)
    {
        if (v <= Black - Radius) return 0f;
        if (v >= Black + Radius) return (v - Black) / (1f - Black);
        float d = v - (Black - Radius);
        return d * d / (4f * Radius * (1f - Black));
    }

    /// <summary>The output level of a white surface whose linear value before the exposure gain is <paramref name="linear"/>.</summary>
    public float LevelOf(float linear) => Curve(Ramp(linear * _gain));

    /// <summary>True when a pixel with these linear values (before the exposure gain) lies at or below <see cref="KneeInput"/> in each
    /// channel. The rendition of such a pixel is the picture of the JPG.</summary>
    public bool IsBelowKnee(float r, float g, float b) => Ramp(r * _gain) <= KneeInput && Ramp(g * _gain) <= KneeInput && Ramp(b * _gain) <= KneeInput;

    /// <summary>
    /// The rendition of one pixel, without the change to neutral at the sensor limit (<see cref="ToNeutralAtLimit"/>). Input and output
    /// are linear ProPhoto RGB; the input is the image before the tone stage and its exposure gain.
    /// </summary>
    public void Pixel(float r, float g, float b, out float outR, out float outG, out float outB)
    {
        float tr = Ramp(r * _gain), tg = Ramp(g * _gain), tb = Ramp(b * _gain);
        float R = Curve(tr), G = Curve(tg), B = Curve(tb);
        // the middle channel keeps its relative position between the smallest and the largest
        if (tg > tr)
        {
            if (tb > tr)
            {
                if (tb > tg) G = (tg - tr) / (tb - tr) * (B - R) + R;
                else B = (tb - tr) / (tg - tr) * (G - R) + R;
            }
            else R = (tr - tb) / (tg - tb) * (G - B) + B;
        }
        else if (tg > tb) G = (tg - tb) / (tr - tb) * (R - B) + B;
        else if (tb > tr) R = (tr - tg) / (tb - tg) * (B - G) + G;
        else if (tb > tg) B = (tb - tg) / (tr - tg) * (R - G) + G;
        else B = G;
        outR = R; outG = G; outB = B;
    }

    /// <summary>The part of <paramref name="limitLevel"/> where the change to neutral starts.</summary>
    public const float NeutralStart = HighlightRecovery.DesaturationStart / HighlightRecovery.DesaturationEnd;

    /// <summary>Moves a rendered pixel (linear ProPhoto RGB) toward the neutral color of the same luminance when its luminance is near
    /// <paramref name="limitLevel"/>, the output level of a white surface at the sensor limit at this pixel (<see cref="LevelOf"/>).
    /// A pixel whose luminance is at or below <see cref="NeutralStart"/> · <paramref name="limitLevel"/> does not change. At and above
    /// <paramref name="limitLevel"/> the pixel is neutral. The luminance stays the same. Returns true when the pixel changed.</summary>
    public static bool ToNeutralAtLimit(ref float r, ref float g, ref float b, float limitLevel)
    {
        // The luminance drives the weight, and the neutral color has no limit: this image has no white at 1.
        float luminance = HighlightRecovery.ProPhotoLuminance(r, g, b);
        return HighlightRecovery.BlendToNeutral(ref r, ref g, ref b, luminance, luminance, 1f, limitLevel * NeutralStart, limitLevel, clampNeutral: false);
    }
}
