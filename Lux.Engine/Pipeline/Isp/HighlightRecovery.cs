namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// Highlight recovery of Lux: a tone curve with a shoulder, and a desaturation of the brightest tones toward white.
///
/// <para>Lumen's tone curve is a table of 1025 values for the inputs 0 to 1. An input above 1 clips to white. The merged image holds
/// about one more stop above that white, because the reference lens of a capture is exposed for half the time. The shoulder makes that
/// stop visible.</para>
///
/// <para>The curve of <see cref="Curve"/> is Lumen's table up to the knee: the first entry with an output of at least
/// <see cref="KneeOutput"/>. Below the knee each lookup gives the same bits as Lumen's table, so midtones and shadows do not move. Above
/// the knee the curve rises more slowly and reaches white later. The white input is <c>2^(amount / 100)</c> times Lumen's white input
/// (<see cref="WhiteInput"/>): amount 0 is Lumen's curve, amount 50 gives half a stop, amount 100 gives one stop.</para>
///
/// <para>At amount 100 the part above the knee is
/// <c>y = yk + (top − yk) · (1 − exp(−a · (t − tk))) / (1 − exp(−a · (2 − tk)))</c>, with (tk, yk) the knee, top the last table value
/// and <c>a = slope / (top − yk)</c>, where slope is the slope of Lumen's table at the knee. Thus the curve keeps its slope at the knee.
/// For an amount between 0 and 100, each output level between the knee and white takes its input from both curves:
/// <c>t = t0^(1 − s) · t1^s</c> with <c>s = amount / 100</c>, t0 the input of that level on Lumen's curve and t1 its input on the
/// amount-100 curve. The curve thus changes continuously from Lumen's curve to the full shoulder. Above its white input, Lumen's
/// lookup continues the last segment of the table, and the output clips later. The curve keeps <c>1 − s</c> of that slope above its
/// white input, so that it is flat at amount 100.</para>
///
/// <para>Typical use for one pixel in a linear RGB space: put each channel through <see cref="Evaluate"/> with a curve from
/// <see cref="Curve"/>, then call <see cref="Desaturate(ref float, ref float, ref float, float, float)"/> with the luminance of the
/// result. All results are the same on each platform: the tables are built in double precision with the <see cref="WineCrt"/> functions,
/// and the per-pixel operations use only float addition, multiplication and division.</para>
/// </summary>
public static class HighlightRecovery
{
    /// <summary>The smallest and largest amount.</summary>
    public const float AmountMin = 0f, AmountMax = 100f;
    /// <summary>The output level of Lumen's curve where the shoulder starts.</summary>
    public const float KneeOutput = 0.80f;
    /// <summary>The desaturation starts at this value of the largest channel and is complete at <see cref="DesaturationEnd"/>.</summary>
    public const float DesaturationStart = 0.85f, DesaturationEnd = 1f;
    /// <summary>The number of table steps for one unit of input (the same as Lumen's table).</summary>
    public const int StepsPerUnit = 1024;
    /// <summary>The largest white input of a curve from <see cref="Curve"/>. The table has one more segment above it. A larger input
    /// continues that segment.</summary>
    public const float CurveInputMax = 2f;

    /// <summary>The input that reaches white, as a multiple of Lumen's white input: <c>2^(amount / 100)</c>, from 1 to 2.</summary>
    public static float WhiteInput(float amount) => (float)WineCrt.Exp2(Math.Clamp(amount, AmountMin, AmountMax) / 100.0);

    /// <summary>The index of the knee in a tone table: the first entry with a value of at least <see cref="KneeOutput"/>. A lookup with an
    /// input below <c>index / 1024</c> gives the same result with the table and with each curve made from it.</summary>
    public static int KneeIndex(float[] lut)
    {
        int n = lut.Length - 1;
        for (int k = 1; k < n; k++) if (lut[k] >= KneeOutput) return k;
        return n;
    }

    static readonly object Sync = new();
    static readonly Dictionary<(float[] Lut, float Amount), float[]> Cache = new();

    /// <summary>
    /// The tone curve with the shoulder for one amount, as a table with <see cref="StepsPerUnit"/> steps per unit of input. It covers the
    /// inputs 0 to <see cref="CurveInputMax"/> and one more step (2050 values for a table of 1025). Read it with <see cref="Evaluate"/>.
    /// </summary>
    /// <param name="lut">Lumen's tone table: 1025 values for the inputs 0 to 1 that do not decrease.</param>
    /// <param name="amount">0 to 100. Values outside that range are clamped. 0 gives Lumen's curve: the table itself, with its last
    /// segment continued for the inputs above 1.</param>
    /// <returns>A table that the caller must not change. The method keeps it and returns it again for the same arguments.</returns>
    public static float[] Curve(float[] lut, float amount)
    {
        amount = Math.Clamp(amount, AmountMin, AmountMax);
        lock (Sync)
        {
            if (Cache.TryGetValue((lut, amount), out var hit)) return hit;
            if (Cache.Count >= 512) Cache.Clear();
            return Cache[(lut, amount)] = Build(lut, amount);
        }
    }

    static float[] Build(float[] lut, float amount)
    {
        int n = lut.Length - 1;                       // the index of input 1
        int m = (int)(CurveInputMax * n) + 1;         // one segment above the largest white input: it gives the slope above white
        var tab = new float[m + 1];
        Array.Copy(lut, tab, n + 1);
        // Lumen's lookup continues the last segment of its table for an input above 1.
        double top = lut[n], endSlope = ((double)lut[n] - lut[n - 1]) * n;
        for (int j = n + 1; j <= m; j++) tab[j] = (float)(top + endSlope * ((double)j / n - 1.0));
        int k = KneeIndex(lut);
        if (amount <= 0f || k >= n) return tab;

        double s = amount / 100.0, tk = (double)k / n, yk = lut[k];
        double slope = ((double)lut[k + 1] - lut[k - 1]) * (n / 2.0);
        if (!(top > yk) || !(slope > 0)) return tab;
        double a = slope / (top - yk);
        double span = 1.0 - WineCrt.Exp(-a * (CurveInputMax - tk));

        // The input of an output level on the amount-100 curve.
        double FullInput(double y) => y >= top ? CurveInputMax : tk - WineCrt.Log(1.0 - (y - yk) * span / (top - yk)) / a;
        // The smallest input of an output level on Lumen's curve, at or above the knee.
        double LumenInput(double y)
        {
            int lo = k, hi = n;                       // lut[lo] <= y, lut[hi] >= y
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (lut[mid] < y) lo = mid; else hi = mid; }
            double d = (double)lut[hi] - lut[lo];
            return (lo + (d > 0 ? Math.Clamp((y - lut[lo]) / d, 0.0, 1.0) : 0.0)) / n;
        }
        double LogInput(double y) => (1.0 - s) * WineCrt.Log(LumenInput(y)) + s * WineCrt.Log(FullInput(y));

        double logWhite = LogInput(top), white = WineCrt.Exp(logWhite);
        float prev = lut[k];
        for (int j = k + 1; j <= m; j++)
        {
            double t = (double)j / n, y;
            if (s >= 1.0) y = Math.Min(yk + (top - yk) * (1.0 - WineCrt.Exp(-a * (t - tk))) / span, top);
            else
            {
                double lt = WineCrt.Log(t);
                if (lt >= logWhite) y = top + (1.0 - s) * endSlope * (t - white);   // above white: flat at amount 100
                else
                {
                    double lo = yk, hi = top;
                    for (int it = 0; it < 60; it++) { double mid = 0.5 * (lo + hi); if (LogInput(mid) < lt) lo = mid; else hi = mid; }
                    y = 0.5 * (lo + hi);
                }
            }
            float v = (float)y;
            if (v < prev) v = prev;                   // the table never decreases
            tab[j] = prev = v;
        }
        return tab;
    }

    /// <summary>
    /// The value of a curve from <see cref="Curve"/> at input <paramref name="t"/>: linear interpolation between the two nearest table
    /// values. An input below 0 uses the first segment. An input above the table continues the last segment, which is flat (white) at
    /// amount 100.
    /// </summary>
    public static float Evaluate(float[] curve, float t)
    {
        float f = t * StepsPerUnit;
        int i = (int)f, top = curve.Length - 2;
        if (i < 0) i = 0;
        if (i > top) i = top;
        float a = curve[i], b = curve[i + 1];
        return ((f - (float)i) * (b - a)) + a;
    }

    /// <summary>The weight of the desaturation for one pixel: 0 below <paramref name="start"/>, then the square of the position between
    /// <paramref name="start"/> and <paramref name="end"/>, 1 at and above <paramref name="end"/>.</summary>
    /// <param name="max">The value that drives the weight: the largest of the three channels, or the luminance.</param>
    public static float DesaturationWeight(float max, float start = DesaturationStart, float end = DesaturationEnd)
    {
        if (!(max > start)) return 0f;
        float u = (max - start) / (end - start);
        if (u > 1f) u = 1f;
        return u * u;
    }

    /// <summary>
    /// Moves a bright pixel toward the neutral color of the same luminance, so that the brightest tones end in white and not in a
    /// saturated color. A pixel whose largest channel is at or below <see cref="DesaturationStart"/> does not change. The weight
    /// increases to <paramref name="strength"/> at <see cref="DesaturationEnd"/>.
    /// </summary>
    /// <param name="r">Red, linear, with white at 1. Green and blue likewise.</param>
    /// <param name="luminance">The luminance of the pixel in the same scale (for linear ProPhoto: <see cref="ProPhotoLuminance"/>).</param>
    /// <param name="strength">0 to 1. 1 is the full effect. 0 changes nothing.</param>
    public static void Desaturate(ref float r, ref float g, ref float b, float luminance, float strength = 1f)
        => Desaturate(ref r, ref g, ref b, luminance, strength, DesaturationStart, DesaturationEnd);

    /// <summary>
    /// <see cref="Desaturate(ref float, ref float, ref float, float, float)"/> with its own range. Use it for an image whose white is not
    /// at 1 (an HDR rendition): the desaturation starts at <paramref name="start"/> and is complete at <paramref name="end"/>, and the
    /// neutral color is not brighter than <paramref name="end"/>.
    /// </summary>
    public static void Desaturate(ref float r, ref float g, ref float b, float luminance, float strength, float start, float end)
    {
        float max = r > g ? r : g; if (b > max) max = b;
        BlendToNeutral(ref r, ref g, ref b, max, luminance, strength, start, end, clampNeutral: true);
    }

    /// <summary>
    /// The blend that all desaturation of highlights uses. It moves a pixel toward the neutral color of its luminance. The weight is
    /// <see cref="DesaturationWeight"/> of <paramref name="driver"/> times <paramref name="strength"/>. Returns true when the pixel changed.
    ///
    /// <para>The caller selects the value that drives the weight. The largest channel reacts to each saturated channel: use it where a
    /// channel can reach the white of the output. The luminance reacts only to bright tones: a saturated color such as a blue sky keeps
    /// its color.</para>
    /// </summary>
    /// <param name="driver">The value that the weight comes from: the largest channel or the luminance of the pixel.</param>
    /// <param name="luminance">The luminance of the pixel. The neutral color has this value in each channel.</param>
    /// <param name="strength">0 to 1. 1 is the full effect. 0 changes nothing.</param>
    /// <param name="start">The value of <paramref name="driver"/> where the change starts.</param>
    /// <param name="end">The value of <paramref name="driver"/> where the change is complete.</param>
    /// <param name="clampNeutral">True: the neutral color is not brighter than <paramref name="end"/>, and a pixel at full weight gets
    /// exactly that color, so that no channel goes above <paramref name="end"/>. False: the neutral color is the luminance without a
    /// limit, and each pixel goes through the blend <c>c + w · (neutral − c)</c>, so the luminance of the pixel stays the same.</param>
    public static bool BlendToNeutral(ref float r, ref float g, ref float b, float driver, float luminance, float strength, float start, float end, bool clampNeutral)
    {
        float w = DesaturationWeight(driver, start, end) * strength;
        if (!(w > 0f)) return false;
        float gray = luminance;
        if (clampNeutral)
        {
            if (!(gray < end)) gray = end;
            if (w >= 1f) { r = g = b = gray; return true; }
        }
        r += w * (gray - r); g += w * (gray - g); b += w * (gray - b);
        return true;
    }

    /// <summary>The luminance (CIE Y) of a linear ProPhoto RGB color, the working space of Lumen's tone curve.</summary>
    public static float ProPhotoLuminance(float r, float g, float b)
    {
        var m = Color.ColorSpace.ProPhotoD50.M;
        return ((m[3] * r) + (m[4] * g)) + (m[5] * b);
    }

    /// <summary>
    /// <see cref="Desaturate(ref float, ref float, ref float, float, float)"/> for a row of linear ProPhoto pixels. Alpha is not changed.
    /// </summary>
    public static void DesaturateRow(Span<Vec4F> row, float strength = 1f)
    {
        if (!(strength > 0f)) return;
        var m = Color.ColorSpace.ProPhotoD50.M;
        float yr = m[3], yg = m[4], yb = m[5];
        for (int x = 0; x < row.Length; x++)
        {
            ref var p = ref row[x];
            if (!(p.R > DesaturationStart || p.G > DesaturationStart || p.B > DesaturationStart)) continue;
            Desaturate(ref p.R, ref p.G, ref p.B, ((yr * p.R) + (yg * p.G)) + (yb * p.B), strength);
        }
    }
}
