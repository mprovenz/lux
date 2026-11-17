using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Animate;

/// <summary>Small functions the animated looks share.</summary>
public static class LookMath
{
    /// <summary>Eased time: 0 at 0, 1 at 1, slow at both ends.</summary>
    public static double Ease(double t) => 0.5 - 0.5 * WineCrt.Cos(Math.PI * Math.Clamp(t, 0, 1));

    /// <summary>0 below <paramref name="e0"/>, 1 above <paramref name="e1"/>, a smooth ramp between.</summary>
    public static float Smoothstep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Rec. 709 luminance of linear RGB.</summary>
    public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>The value at fraction <paramref name="p"/> (0–1) of the sorted values.</summary>
    public static float Percentile(float[] sorted, double p) => sorted[Math.Clamp((int)(p * (sorted.Length - 1)), 0, sorted.Length - 1)];

    /// <summary>
    /// The inverse distance (1/m) of a depth map in mm, reduced to <paramref name="ow"/> × <paramref name="oh"/> by the mean over each
    /// output pixel's area. The mean of the inverse distance is what a smaller picture of the same scene would measure, and it smooths the
    /// steps between the depth map's planes. A pixel without depth counts as far.
    /// </summary>
    public static float[] InverseDepth(float[] depthMm, int sw, int sh, int ow, int oh)
    {
        const float far = 1000f / Export.DepthImageCache.Clip;
        var q = new float[(long)sw * sh];
        Parallel.For(0, sh, y =>
        {
            long o = (long)y * sw;
            for (int x = 0; x < sw; x++) { float z = depthMm[o + x]; q[o + x] = z > 0f && float.IsFinite(z) ? 1000f / Math.Max(z, 1f) : far; }
        });
        if (ow == sw && oh == sh) return q;
        var (ix, wx, nx) = AreaTaps(sw, ow);
        var (iy, wy, ny) = AreaTaps(sh, oh);
        var tmp = new float[(long)sh * ow];
        Parallel.For(0, sh, y =>
        {
            long o = (long)y * sw, d = (long)y * ow;
            for (int x = 0; x < ow; x++) { float a = 0; for (int k = 0; k < nx; k++) a += wx[x * nx + k] * q[o + ix[x * nx + k]]; tmp[d + x] = a; }
        });
        var res = new float[(long)ow * oh];
        Parallel.For(0, oh, y =>
        {
            long d = (long)y * ow;
            for (int x = 0; x < ow; x++) { float a = 0; for (int k = 0; k < ny; k++) a += wy[y * ny + k] * tmp[(long)iy[y * ny + k] * ow + x]; res[d + x] = a; }
        });
        return res;
    }

    /// <summary>The saturation and contrast factors of the miniature look.</summary>
    public const float MiniatureSaturation = 1.35f, MiniatureContrast = 1.08f;

    /// <summary>The blend of two pictures of one size as a miniature: the saturation raised (the brightest channel of a pixel is kept) and
    /// the contrast raised around mid gray.</summary>
    public static Rgba Miniature(Rgba a, Rgba b, float blend)
    {
        static byte Byte(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);
        var o = new Rgba(a.W, a.H);
        Parallel.For(0, a.H, y =>
        {
            long p = (long)y * a.W * 4;
            for (int x = 0; x < a.W; x++, p += 4)
            {
                float r = a.P[p] + (b.P[p] - a.P[p]) * blend, g = a.P[p + 1] + (b.P[p + 1] - a.P[p + 1]) * blend, bl = a.P[p + 2] + (b.P[p + 2] - a.P[p + 2]) * blend;
                float max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
                float k = max > min ? Math.Min(MiniatureSaturation, max / (max - min)) : 1f;
                r = max - (max - r) * k; g = max - (max - g) * k; bl = max - (max - bl) * k;
                o.P[p] = Byte((r - 128f) * MiniatureContrast + 128f); o.P[p + 1] = Byte((g - 128f) * MiniatureContrast + 128f); o.P[p + 2] = Byte((bl - 128f) * MiniatureContrast + 128f);
                o.P[p + 3] = 255;
            }
        });
        return o;
    }

    /// <summary>For each output position, the source positions under it and the share of its area each one covers.</summary>
    static (int[] Index, float[] Weight, int Taps) AreaTaps(int src, int dst)
    {
        double s = (double)src / dst;
        int n = (int)Math.Ceiling(s) + 1;
        var ix = new int[dst * n]; var w = new float[dst * n];
        for (int o = 0; o < dst; o++)
        {
            double a = o * s, b = (o + 1) * s; int i0 = (int)Math.Floor(a);
            for (int k = 0; k < n; k++)
            {
                int i = i0 + k;
                double cover = Math.Min(b, i + 1) - Math.Max(a, i);
                ix[o * n + k] = Math.Clamp(i, 0, src - 1);
                w[o * n + k] = cover > 0 ? (float)(cover / s) : 0f;
            }
        }
        return (ix, w, n);
    }
}

/// <summary>A function of a positive number, tabulated at 64 points per doubling and read with linear interpolation. The table index comes
/// from the bits of the number, so a read needs no logarithm.</summary>
sealed class OctaveTable
{
    readonly float[] _v; readonly int _base; readonly float _lo, _hi;

    /// <param name="minExp">The table starts at 2^minExp and ends at 2^maxExp.</param>
    public OctaveTable(int minExp, int maxExp, Func<float, float> f)
    {
        _base = (minExp + 127) << 6;
        int n = (maxExp - minExp) * 64 + 1;
        _v = new float[n + 1];
        for (int i = 0; i < n; i++) _v[i] = f(BitConverter.Int32BitsToSingle((_base + i) << 17));
        _v[n] = _v[n - 1];
        _lo = BitConverter.Int32BitsToSingle(_base << 17); _hi = BitConverter.Int32BitsToSingle((_base + n - 1) << 17);
    }

    public float At(float x)
    {
        if (!(x > _lo)) return _v[0];
        if (x >= _hi) return _v[^1];
        int bits = BitConverter.SingleToInt32Bits(x);
        int i = (bits >> 17) - _base;
        float t = (bits & 0x1FFFF) * (1f / 131072f);
        return _v[i] + (_v[i + 1] - _v[i]) * t;
    }
}

/// <summary>The sRGB transfer functions as tables.</summary>
static class Srgb
{
    /// <summary>Code value (0–255) to linear light (0–1).</summary>
    public static readonly float[] ToLinear = BuildDecode();
    static float[] BuildDecode()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) { double x = i / 255.0; t[i] = (float)(x <= 0.04045 ? x / 12.92 : WineCrt.Pow((x + 0.055) / 1.055, 2.4)); }
        return t;
    }

    static readonly OctaveTable Encode = new(-12, 0, y => (float)((y <= 0.0031308 ? y * 12.92 : 1.055 * WineCrt.Pow(y, 1 / 2.4) - 0.055) * 255.0));

    /// <summary>Linear light to the rounded code value.</summary>
    public static byte FromLinear(float y)
    {
        if (!(y > 0f)) return 0;
        if (y >= 1f) return 255;
        float c = y < 1f / 4096f ? y * (12.92f * 255f) : Encode.At(y);
        return (byte)(c + 0.5f);
    }
}

/// <summary>
/// The picture's tone curve as one global curve: the luminance of the scene (the linear render) against the luminance the finished
/// picture shows. A look that works in scene light takes the finished picture back through the curve, changes the light, and comes
/// forward through it again. The picture's colors are kept: only the luminance passes through the curve.
/// </summary>
public sealed class ToneCurve
{
    readonly double[] _x, _y;   // log2 scene luminance, log2 display luminance; both rising
    readonly OctaveTable _ratio;

    ToneCurve(double[] x, double[] y)
    {
        _x = x; _y = y;
        _ratio = new OctaveTable(-24, 8, v => (float)(Forward(v) / v));
    }

    /// <summary>A curve that changes nothing (scene light = display light).</summary>
    public static ToneCurve Identity() => new(new[] { -20.0, 0.0 }, new[] { -20.0, 0.0 });

    /// <summary>
    /// Fit the curve of a picture. <paramref name="rgba"/> is the finished 8-bit picture and <paramref name="linear"/> the linear render
    /// on the same grid (RGBA floats). The scene luminances between their 0.2nd and 99.8th percentile are cut into 48 bins of equal
    /// width in log2. Each bin with more than 30 pixels gives one point: the median display luminance. The points are made to rise.
    /// </summary>
    public static ToneCurve Fit(byte[] rgba, float[] linear, int w, int h)
    {
        long n = (long)w * h;
        var lh = new List<float>((int)Math.Min(n, int.MaxValue / 2)); var lj = new List<float>(lh.Capacity);
        for (long i = 0, p = 0; i < n; i++, p += 4)
        {
            float yh = LookMath.Luminance(linear[p], linear[p + 1], linear[p + 2]);
            float yj = LookMath.Luminance(Srgb.ToLinear[rgba[p]], Srgb.ToLinear[rgba[p + 1]], Srgb.ToLinear[rgba[p + 2]]);
            if (yh > 1e-6f && yj > 1e-6f) { lh.Add((float)WineCrt.Log2(yh)); lj.Add((float)WineCrt.Log2(yj)); }
        }
        if (lh.Count < 1000) return Identity();
        var sorted = lh.ToArray(); Array.Sort(sorted);
        double lo = LookMath.Percentile(sorted, 0.002), hi = LookMath.Percentile(sorted, 0.998);
        const int bins = 48;
        if (!(hi > lo)) return Identity();
        double step = (hi - lo) / (bins - 1);
        var per = new List<float>[bins];
        for (int i = 0; i < bins; i++) per[i] = new List<float>();
        for (int i = 0; i < lh.Count; i++) per[Math.Clamp((int)Math.Floor((lh[i] - lo) / step), 0, bins - 1)].Add(lj[i]);
        var xs = new List<double>(); var ys = new List<double>();
        double top = double.NegativeInfinity;
        for (int i = 0; i < bins; i++)
        {
            if (per[i].Count <= 30) continue;
            var v = per[i].ToArray(); Array.Sort(v);
            double med = (v.Length & 1) == 1 ? v[v.Length / 2] : 0.5 * (v[v.Length / 2 - 1] + v[v.Length / 2]);
            top = Math.Max(top, med);
            xs.Add(lo + i * step); ys.Add(top);
        }
        return xs.Count < 2 ? Identity() : new ToneCurve(xs.ToArray(), ys.ToArray());
    }

    static double Interp(double v, double[] x, double[] y)
    {
        if (v <= x[0]) return y[0];
        if (v >= x[^1]) return y[^1];
        int a = 0, b = x.Length - 1;
        while (b - a > 1) { int m = (a + b) / 2; if (x[m] <= v) a = m; else b = m; }
        return x[b] > x[a] ? y[a] + (y[b] - y[a]) * (v - x[a]) / (x[b] - x[a]) : y[a];
    }

    /// <summary>Scene luminance to display luminance. Past the bright end the curve rises with a slope of 0.3 (in log2); below the dark
    /// end it follows the scene (slope 1).</summary>
    public double Forward(double scene)
    {
        double l = WineCrt.Log2(Math.Max(scene, 1e-7));
        double o = Interp(l, _x, _y);
        if (l > _x[^1]) o += (l - _x[^1]) * 0.3;
        if (l < _x[0]) o += l - _x[0];
        return WineCrt.Exp2(o);
    }

    /// <summary>Display luminance to scene luminance: the inverse of <see cref="Forward"/>, past the ends of the curve too.</summary>
    public double Inverse(double display)
    {
        double l = WineCrt.Log2(Math.Max(display, 1e-7));
        // the display values can repeat (a flat part of the curve): read the first scene value of each
        if (l < _y[0]) return WineCrt.Exp2(_x[0] + (l - _y[0]));
        if (l > _y[^1]) return WineCrt.Exp2(_x[^1] + (l - _y[^1]) / 0.3);
        if (l == _y[^1]) { int k = _y.Length - 1; while (k > 0 && _y[k - 1] == _y[k]) k--; return WineCrt.Exp2(_x[k]); }
        int a = 0;
        while (a + 1 < _y.Length && _y[a + 1] <= l) a++;
        while (a > 0 && _y[a - 1] == _y[a]) a--;
        int b = a + 1;
        while (b + 1 < _y.Length && _y[b] == _y[a]) b++;
        return WineCrt.Exp2(_y[b] > _y[a] ? _x[a] + (_x[b] - _x[a]) * (l - _y[a]) / (_y[b] - _y[a]) : _x[a]);
    }

    /// <summary>Display luminance over scene luminance, for a scene luminance (a fast read of <see cref="Forward"/>).</summary>
    public float Ratio(float scene) => _ratio.At(scene);

    /// <summary>The finished picture as scene-linear RGB (3 floats per pixel): its display-linear colors scaled to the scene luminance.</summary>
    public float[] ToScene(Rgba picture)
    {
        long n = (long)picture.W * picture.H;
        var s = new float[n * 3];
        // one inverse read per display luminance step is enough: tabulate it over the luminance
        var inv = new OctaveTable(-24, 1, y => (float)(Inverse(y) / y));
        Parallel.For(0, picture.H, yy =>
        {
            long p = (long)yy * picture.W * 4, q = (long)yy * picture.W * 3;
            for (int x = 0; x < picture.W; x++, p += 4, q += 3)
            {
                float r = Srgb.ToLinear[picture.P[p]], g = Srgb.ToLinear[picture.P[p + 1]], b = Srgb.ToLinear[picture.P[p + 2]];
                float k = inv.At(Math.Max(LookMath.Luminance(r, g, b), 1e-7f));
                s[q] = r * k; s[q + 1] = g * k; s[q + 2] = b * k;
            }
        });
        return s;
    }
}

/// <summary>How the fog moves.</summary>
public enum FogMode
{
    /// <summary>A bank of fog comes forward: its front moves from the far part of the scene to the near part.</summary>
    Bank,
    /// <summary>Fog over the whole scene thickens and thins again. The first and last frames are clear, so it loops.</summary>
    Rise,
}

/// <summary>
/// Depth fog in scene light. Each pixel keeps the share exp(−density × distance through the fog) of its own light, and the rest is the
/// fog's light (attenuation plus airlight). The picture goes back through its tone curve first and forward again after, so thick fog
/// turns to bright haze as it does in a photograph, not to gray paint.
/// </summary>
public sealed class FogLook
{
    /// <summary>The share of light that reaches the camera from the median distance, at the base density.</summary>
    public const double TransmissionAtMedian = 0.35;
    /// <summary>The bank is this many times denser than the base density.</summary>
    public const double BankDensity = 2.5;
    /// <summary>The bank's front stops at this share of the median distance.</summary>
    public const double BankNearStop = 0.3;

    readonly int _w, _h; readonly float[] _scene, _z; readonly ToneCurve _tone; readonly float[] _air = new float[3]; readonly Rgba _picture;
    readonly FogMode _mode;

    /// <summary>The base density, 1/m.</summary>
    public double Density { get; }
    /// <summary>The median and the far (90th percentile) distance, m.</summary>
    public double MedianM { get; }
    public double FarM { get; }

    /// <param name="picture">The finished picture.</param>
    /// <param name="inverseDepth">Inverse distance per pixel of the picture, 1/m (<see cref="LookMath.InverseDepth"/>).</param>
    /// <param name="density">A factor on the density (1 = the standard fog).</param>
    /// <param name="color">The fog's color as an sRGB color; null = from the far part of the scene, pulled towards gray.</param>
    public FogLook(Rgba picture, float[] inverseDepth, ToneCurve tone, FogMode mode, double density = 1.0, (byte R, byte G, byte B)? color = null)
    {
        _w = picture.W; _h = picture.H; _tone = tone; _mode = mode; _picture = picture;
        long n = (long)_w * _h;
        _z = new float[n];
        for (long i = 0; i < n; i++) _z[i] = 1f / Math.Max(inverseDepth[i], 1e-3f);
        var sorted = (float[])_z.Clone(); Array.Sort(sorted);
        MedianM = LookMath.Percentile(sorted, 0.5); FarM = LookMath.Percentile(sorted, 0.9);
        Density = density * -WineCrt.Log(TransmissionAtMedian) / Math.Max(MedianM, 0.5);
        _scene = tone.ToScene(picture);

        if (color is { } c)
        {
            float r = Srgb.ToLinear[c.R], g = Srgb.ToLinear[c.G], b = Srgb.ToLinear[c.B];
            float y = Math.Max(LookMath.Luminance(r, g, b), 1e-7f), k = (float)(tone.Inverse(y) / y);
            _air[0] = r * k; _air[1] = g * k; _air[2] = b * k;
        }
        else
        {
            // the median scene color of the farthest 3 % (sky, far haze), 40 % of the way to gray, and at least as bright as the
            // 90th percentile of the scene
            float z97 = LookMath.Percentile(sorted, 0.97);
            var ch = new List<float>[] { new(), new(), new() }; var lum = new float[n];
            for (long i = 0, q = 0; i < n; i++, q += 3)
            {
                lum[i] = LookMath.Luminance(_scene[q], _scene[q + 1], _scene[q + 2]);
                if (_z[i] >= z97) { ch[0].Add(_scene[q]); ch[1].Add(_scene[q + 1]); ch[2].Add(_scene[q + 2]); }
            }
            var a = new float[3];
            for (int k = 0; k < 3; k++) { var v = ch[k].ToArray(); Array.Sort(v); a[k] = v.Length == 0 ? 0.18f : v[v.Length / 2]; }
            float ya = LookMath.Luminance(a[0], a[1], a[2]);
            Array.Sort(lum);
            float bright = LookMath.Percentile(lum, 0.9), gain = Math.Max(1f, bright / Math.Max(ya, 1e-6f));
            for (int k = 0; k < 3; k++) _air[k] = (0.6f * a[k] + 0.4f * ya) * gain;
        }
    }

    /// <summary>The density (1/m) and the distance where the fog starts (m) at time <paramref name="t"/> (0–1).</summary>
    public (double Density, double Front) At(double t)
    {
        if (_mode == FogMode.Rise) { double s = WineCrt.Sin(Math.PI * Math.Clamp(t, 0, 1)); return (Density * s * s, 0); }
        double e = LookMath.Ease(t);
        return (Density * BankDensity, FarM * (1 - e) + BankNearStop * MedianM * e);
    }

    /// <summary>The frame at time <paramref name="t"/> (0–1).</summary>
    public Rgba Frame(double t)
    {
        var (density, front) = At(t);
        float beta = (float)density, f0 = (float)front;
        var o = new Rgba(_w, _h);
        Parallel.For(0, _h, y =>
        {
            long i = (long)y * _w, q = i * 3, p = i * 4;
            for (int x = 0; x < _w; x++, i++, q += 3, p += 4)
            {
                float tr = WineCrt.Expf(-beta * Math.Max(_z[i] - f0, 0f)), ar = 1f - tr;
                // no fog on this pixel: the picture's own value
                if (tr >= 1f) { o.P[p] = _picture.P[p]; o.P[p + 1] = _picture.P[p + 1]; o.P[p + 2] = _picture.P[p + 2]; o.P[p + 3] = 255; continue; }
                float r = _scene[q] * tr + _air[0] * ar, g = _scene[q + 1] * tr + _air[1] * ar, b = _scene[q + 2] * tr + _air[2] * ar;
                float k = _tone.Ratio(Math.Max(LookMath.Luminance(r, g, b), 1e-7f));
                o.P[p] = Srgb.FromLinear(r * k); o.P[p + 1] = Srgb.FromLinear(g * k); o.P[p + 2] = Srgb.FromLinear(b * k); o.P[p + 3] = 255;
            }
        });
        return o;
    }
}

/// <summary>The order a slice reveal moves through the scene.</summary>
public enum SliceOrder
{
    /// <summary>From the nearest part of the scene to the farthest.</summary>
    NearToFar,
    /// <summary>From the farthest part to the nearest.</summary>
    FarToNear,
    /// <summary>Outward from the focus distance, both ways.</summary>
    FromFocus,
}

/// <summary>
/// The soft depth-slice reveal: the picture starts dim and without color, and its own colors come in slice by slice of distance. The
/// edge of the revealed part is a soft band in inverse distance, which hides the steps and small errors of the depth map.
/// </summary>
public sealed class SliceLook
{
    /// <summary>The standard width of the soft band, as a share of the scene's range of inverse distance.</summary>
    public const double DefaultSoftness = 0.08;

    readonly Rgba _picture; readonly float[] _q; readonly SliceOrder _order; readonly float _soft, _qMax, _qMin, _qFocus;

    /// <param name="inverseDepth">Inverse distance per pixel of the picture, 1/m.</param>
    /// <param name="softness">Half the width of the soft band, as a share of the range of inverse distance (0 = a hard edge).</param>
    /// <param name="focusM">The distance <see cref="SliceOrder.FromFocus"/> starts at, m.</param>
    public SliceLook(Rgba picture, float[] inverseDepth, SliceOrder order, double softness = DefaultSoftness, double focusM = 0)
    {
        _picture = picture; _q = inverseDepth; _order = order; _soft = (float)Math.Clamp(softness, 0, 0.5);
        var sorted = (float[])inverseDepth.Clone(); Array.Sort(sorted);
        _qMax = LookMath.Percentile(sorted, 0.995); _qMin = LookMath.Percentile(sorted, 0.01);
        _qFocus = focusM > 0 ? (float)(1.0 / focusM) : LookMath.Percentile(sorted, 0.5);
    }

    /// <summary>How much of a pixel at inverse distance <paramref name="q"/> shows at time <paramref name="t"/> (0–1): 0 hidden, 1 revealed.</summary>
    public float Revealed(float q, double t) => RevealedAt(q, (float)LookMath.Ease(t));

    float RevealedAt(float q, float e)
    {
        float range = _qMax - _qMin, band = _soft * range + 1e-6f;
        switch (_order)
        {
            case SliceOrder.NearToFar:
            {
                float front = _qMax - range * (1 + 2 * _soft) * e + range * _soft;
                return _soft > 0 ? LookMath.Smoothstep(front - band, front + band, q) : q >= front ? 1f : 0f;
            }
            case SliceOrder.FarToNear:
            {
                float front = _qMin + range * (1 + 2 * _soft) * e - range * _soft;
                return _soft > 0 ? 1f - LookMath.Smoothstep(front - band, front + band, q) : q <= front ? 1f : 0f;
            }
            default:
            {
                // the radius runs from just below 0 to just past the farthest end, so the reveal starts hidden and ends complete
                float r = (Math.Max(_qMax - _qFocus, _qFocus - _qMin) + 2 * band) * e - band, d = Math.Abs(q - _qFocus);
                return _soft > 0 ? 1f - LookMath.Smoothstep(r - band, r + band, d) : d <= r ? 1f : 0f;
            }
        }
    }

    /// <summary>The frame at time <paramref name="t"/> (0–1). A hidden pixel shows 55 % of its gray value plus a little light.</summary>
    public Rgba Frame(double t)
    {
        int w = _picture.W, h = _picture.H;
        var o = new Rgba(w, h);
        var s = _picture.P;
        float e = (float)LookMath.Ease(t);
        Parallel.For(0, h, y =>
        {
            long i = (long)y * w, p = i * 4;
            for (int x = 0; x < w; x++, i++, p += 4)
            {
                float a = RevealedAt(_q[i], e);
                float hidden = 0.55f * (0.299f * s[p] + 0.587f * s[p + 1] + 0.114f * s[p + 2]) + 12.8f;
                for (int c = 0; c < 3; c++) o.P[p + c] = (byte)(Math.Clamp(s[p + c] * a + hidden * (1 - a), 0f, 255f) + 0.5f);
                o.P[p + 3] = 255;
            }
        });
        return o;
    }
}
