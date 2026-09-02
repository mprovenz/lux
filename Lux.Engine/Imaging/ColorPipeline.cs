namespace Lux.Engine.Imaging;

/// <summary>
/// The validated Lumen colour pass: camera-RGB-linear → display sRGB.
/// v = cam/AsShotNeutral · 2^ev ; rgb = (XYZ→sRGB · ForwardMatrix) · v ; TMO_ACR(§7) ; sRGB gamma.
/// ForwardMatrix (cam→XYZ D50) comes from the module's factory colour calibration.
/// </summary>
public static class ColorPipeline
{
    // XYZ(D50) → linear sRGB(D65), Bradford-adapted (Bruce Lindbloom).
    private static readonly float[] XyzToSrgbD50 =
    {
        3.1338561f, -1.6168667f, -0.4906146f,
       -0.9787684f,  1.9161415f,  0.0334540f,
        0.0719453f, -0.2289914f,  1.4052427f,
    };

    /// <summary>3×3 (row-major) matrix product A·B.</summary>
    public static float[] Mul3(float[] a, float[] b)
    {
        var m = new float[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3 + 0] * b[0 * 3 + c] + a[r * 3 + 1] * b[1 * 3 + c] + a[r * 3 + 2] * b[2 * 3 + c];
        return m;
    }

    /// <summary>
    /// Render interleaved linear camera-RGB (H·W·3) in place to display sRGB [0,1].
    /// <paramref name="fmCamToXyz"/> = 3×3 ForwardMatrix (cam→XYZ D50); <paramref name="asShot"/> = AsShotNeutral.
    /// </summary>
    public static void Render(float[] rgb, float[] fmCamToXyz, float[] asShot, float ev)
    {
        float[] m = Mul3(XyzToSrgbD50, fmCamToXyz);   // camera-linear → linear sRGB
        float g = MathF.Pow(2f, ev);
        float ir = g / asShot[0], ig = g / asShot[1], ib = g / asShot[2];
        int n = rgb.Length / 3;
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            float vr = rgb[o] * ir, vg = rgb[o + 1] * ig, vb = rgb[o + 2] * ib;
            float lr = MathF.Max(m[0] * vr + m[1] * vg + m[2] * vb, 0f);
            float lg = MathF.Max(m[3] * vr + m[4] * vg + m[5] * vb, 0f);
            float lb = MathF.Max(m[6] * vr + m[7] * vg + m[8] * vb, 0f);
            SuppressHighlightMagenta(ref lr, ref lg, ref lb);
            RgbTone(ref lr, ref lg, ref lb);
            rgb[o] = ToneCurve.Srgb(lr);
            rgb[o + 1] = ToneCurve.Srgb(lg);
            rgb[o + 2] = ToneCurve.Srgb(lb);
        }
    }

    /// <summary>Hue-preserving RGBTone (DNG-SDK style): tone the max &amp; min channels, interpolate the middle.
    /// A ratio-preserving highlight rolloff runs first: the whole triplet is scaled by rolloff(max)/max so a
    /// blown highlight compresses toward — not to — white, keeping its hue and saturation (warm sunset sky)
    /// instead of the naive clamp that flattens every highlight to neutral white.</summary>
    /// <summary>Suppress magenta blown highlights: at the sun the green channel saturates first and the
    /// camera→sRGB matrix can drive green below BOTH red and blue (an unnatural magenta the eye never sees in
    /// a real highlight). A warm sky is R&gt;G&gt;B (green not deficient) and is left untouched; only a bright,
    /// green-deficient pixel (G&lt;R and G&lt;B) has its green lifted toward min(R,B), scaled by how deficient and
    /// how bright it is — so the sun rolls to neutral-white instead of pink.</summary>
    private static void SuppressHighlightMagenta(ref float r, ref float g, ref float b)
    {
        float loRB = MathF.Min(r, b);
        if (g >= loRB) return;                        // green not deficient (e.g. warm sky) — leave it
        float mx = MathF.Max(r, MathF.Max(g, b));
        if (mx < 0.7f) return;                         // only highlights
        float bright = Math.Clamp((mx - 0.7f) / 0.5f, 0f, 1f);
        g += (loRB - g) * bright;                      // lift green up to min(R,B) as brightness → white
    }

    private static void RgbTone(ref float r, ref float g, ref float b)
    {
        float er = ToneCurve.Toe(r), eg = ToneCurve.Toe(g), eb = ToneCurve.Toe(b);
        float mx = MathF.Max(er, MathF.Max(eg, eb));
        if (mx > ToneCurve.HlKnee)
        {
            float scale = ToneCurve.HighlightRolloff(mx) / mx;   // same factor for all → ratios preserved
            er *= scale; eg *= scale; eb *= scale;
        }
        float hi = MathF.Max(er, MathF.Max(eg, eb));
        float lo = MathF.Min(er, MathF.Min(eg, eb));
        float mid = er + eg + eb - hi - lo;
        float tHi = ToneCurve.Lookup(hi), tLo = ToneCurve.Lookup(lo);
        float tMid = (hi - lo) > 1e-8f ? tLo + (mid - lo) / (hi - lo) * (tHi - tLo) : tLo;
        r = er >= hi ? tHi : (er <= lo ? tLo : tMid);
        g = eg >= hi ? tHi : (eg <= lo ? tLo : tMid);
        b = eb >= hi ? tHi : (eb <= lo ? tLo : tMid);
    }

    /// <summary>Greyscale tone (for no-CFA/monochrome modules): toe → LUT → gamma on a luminance value.</summary>
    public static float RenderMono(float y, float ev) => ToneCurve.Srgb(ToneCurve.Lookup(ToneCurve.HighlightRolloff(ToneCurve.Toe(y * MathF.Pow(2f, ev)))));
}
