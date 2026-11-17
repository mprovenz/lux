using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Animate;

/// <summary>
/// A picture and its halved copies, for frames that show the picture at any scale. Level p is level 0 reduced 2^p times; each step is the
/// mean of 2 × 2 pixels. A frame reads the two levels around its scale and blends them (trilinear filtering), so the sharpness changes
/// evenly through a zoom and no frame differs in kind from the one before it.
/// </summary>
public sealed class ImagePyramid
{
    readonly List<Rgba> _levels = new();

    /// <summary>The levels: level 0 is the picture itself.</summary>
    public IReadOnlyList<Rgba> Levels => _levels;
    public int W => _levels[0].W;
    public int H => _levels[0].H;

    ImagePyramid() { }

    /// <summary>Build the pyramid of <paramref name="level0"/>. The picture is kept, not copied. No level is smaller than
    /// <paramref name="minSide"/> on its short side.</summary>
    public static ImagePyramid Build(Rgba level0, int minSide = 64, CancellationToken cancel = default)
    {
        var p = new ImagePyramid();
        p._levels.Add(level0);
        while (Math.Min(p._levels[^1].W, p._levels[^1].H) / 2 >= minSide)
        {
            cancel.ThrowIfCancellationRequested();
            p._levels.Add(Halve(p._levels[^1]));
        }
        return p;
    }

    /// <summary>A pyramid from given levels (level p must be level 0 reduced 2^p times). For sources that make each level by other means.</summary>
    public static ImagePyramid FromLevels(IEnumerable<Rgba> levels)
    {
        var p = new ImagePyramid();
        p._levels.AddRange(levels);
        if (p._levels.Count == 0) throw new ArgumentException("a pyramid needs one level at least");
        return p;
    }

    /// <summary>The picture at half size: each pixel is the rounded mean of 2 × 2 pixels. An odd last column or row is dropped.</summary>
    public static Rgba Halve(Rgba a)
    {
        int w = a.W / 2, h = a.H / 2;
        var o = new Rgba(w, h);
        var s = a.P; var d = o.P; int stride = a.W * 4;
        Parallel.For(0, h, y =>
        {
            long r0 = (long)(2 * y) * stride, r1 = r0 + stride, q = (long)y * w * 4;
            for (int x = 0; x < w; x++, r0 += 8, r1 += 8, q += 4)
            {
                d[q] = (byte)((s[r0] + s[r0 + 4] + s[r1] + s[r1 + 4] + 2) >> 2);
                d[q + 1] = (byte)((s[r0 + 1] + s[r0 + 5] + s[r1 + 1] + s[r1 + 5] + 2) >> 2);
                d[q + 2] = (byte)((s[r0 + 2] + s[r0 + 6] + s[r1 + 2] + s[r1 + 6] + 2) >> 2);
                d[q + 3] = 255;
            }
        });
        return o;
    }

    /// <summary>
    /// A frame of <paramref name="ow"/> × <paramref name="oh"/> pixels that shows the window of the picture starting at
    /// (<paramref name="x0"/>, <paramref name="y0"/>) in level-0 pixels, with <paramref name="scale"/> level-0 pixels per frame pixel.
    /// Each of the two levels around the scale is read with a bicubic (Catmull-Rom) filter, and the two results are blended by the
    /// fraction of log2(scale). At a scale of 1 with a whole-pixel start the frame is a copy of the picture's pixels.
    /// </summary>
    public Rgba Sample(double x0, double y0, double scale, int ow, int oh)
    {
        double lod = scale > 1 ? WineCrt.Log2(scale) : 0;
        int p = (int)Math.Floor(lod + 1e-9);
        if (p > _levels.Count - 1) p = _levels.Count - 1;
        float blend = (float)(lod - p);
        bool two = blend >= 1e-3f && p + 1 < _levels.Count;
        if (!two) blend = 0;
        var a = new Tap(_levels[p], x0, y0, scale, 1 << p, ow, oh);
        var b = two ? new Tap(_levels[p + 1], x0, y0, scale, 1 << (p + 1), ow, oh) : null;
        var o = new Rgba(ow, oh);
        Parallel.For(0, oh, () => (new float[ow * 3], new float[ow * 3], new float[ow * 3]), (y, _, buf) =>
        {
            var (rowA, rowB, tmp) = buf;
            a.Row(y, rowA, tmp);
            b?.Row(y, rowB, tmp);
            long q = (long)y * ow * 4;
            var d = o.P;
            for (int i = 0; i < ow * 3; i += 3, q += 4)
            {
                float r = rowA[i], g = rowA[i + 1], bl = rowA[i + 2];
                if (b is not null) { r += (rowB[i] - r) * blend; g += (rowB[i + 1] - g) * blend; bl += (rowB[i + 2] - bl) * blend; }
                d[q] = Clamp8(r); d[q + 1] = Clamp8(g); d[q + 2] = Clamp8(bl); d[q + 3] = 255;
            }
            return buf;
        }, _ => { });
        return o;
    }

    static byte Clamp8(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>The bicubic read of one level for one frame: the column positions and weights are the same for every row.</summary>
    sealed class Tap
    {
        readonly Rgba _img; readonly int _ow; readonly int[] _ix; readonly float[] _wx; readonly double _y0, _step; readonly int _factor;

        public Tap(Rgba img, double x0, double y0, double scale, int factor, int ow, int oh)
        {
            _img = img; _ow = ow; _factor = factor; _y0 = y0; _step = scale;
            _ix = new int[ow]; _wx = new float[ow * 4];
            for (int x = 0; x < ow; x++)
            {
                double u = (x0 + (x + 0.5) * scale) / factor - 0.5;
                double f = Math.Floor(u);
                _ix[x] = (int)f;
                Weights((float)(u - f), _wx.AsSpan(x * 4, 4));
            }
        }

        /// <summary>Catmull-Rom weights of the four pixels around a position <paramref name="t"/> (0–1) past the second one.</summary>
        static void Weights(float t, Span<float> w)
        {
            float t2 = t * t, t3 = t2 * t;
            w[0] = -0.5f * t3 + t2 - 0.5f * t;
            w[1] = 1.5f * t3 - 2.5f * t2 + 1f;
            w[2] = -1.5f * t3 + 2f * t2 + 0.5f * t;
            w[3] = 0.5f * t3 - 0.5f * t2;
        }

        public void Row(int y, float[] row, float[] tmp)
        {
            double v = (_y0 + (y + 0.5) * _step) / _factor - 0.5;
            double fy = Math.Floor(v);
            int iy = (int)fy;
            Span<float> wy = stackalloc float[4];
            Weights((float)(v - fy), wy);
            Array.Clear(row, 0, _ow * 3);
            int W = _img.W, H = _img.H; var s = _img.P;
            for (int k = 0; k < 4; k++)
            {
                float wk = wy[k];
                if (wk == 0f) continue;
                long r = (long)Math.Clamp(iy - 1 + k, 0, H - 1) * W * 4;
                for (int x = 0, i = 0; x < _ow; x++, i += 3)
                {
                    int cx = _ix[x] - 1;
                    float a0 = 0, a1 = 0, a2 = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float wj = _wx[x * 4 + j];
                        long q = r + (long)Math.Clamp(cx + j, 0, W - 1) * 4;
                        a0 += wj * s[q]; a1 += wj * s[q + 1]; a2 += wj * s[q + 2];
                    }
                    tmp[i] = a0; tmp[i + 1] = a1; tmp[i + 2] = a2;
                }
                for (int i = 0; i < _ow * 3; i++) row[i] += wk * tmp[i];
            }
        }
    }
}

/// <summary>
/// The path of a zoom from the whole picture onto a target. The scale changes by a constant factor per step of the eased time, so the
/// zoom looks even. The target moves from its place in the whole picture to the middle of the frame with the same easing, which gives a
/// slight pan. The window never leaves the picture.
/// </summary>
public sealed class ZoomPath
{
    readonly int _w, _h, _ow, _oh, _n;
    readonly double _s0, _s1, _tx, _ty, _q0x, _q0y;

    /// <param name="w">Picture width, level-0 pixels.</param>
    /// <param name="targetX">The target, level-0 pixels: the middle of the last frame.</param>
    /// <param name="endScale">Level-0 pixels per frame pixel in the last frame: 1 ends at the picture's own pixels.</param>
    public ZoomPath(int w, int h, int ow, int oh, double targetX, double targetY, int frames, double endScale = 1.0)
    {
        _w = w; _h = h; _ow = ow; _oh = oh; _n = Math.Max(2, frames);
        _s0 = Math.Max((double)w / ow, (double)h / oh);
        _s1 = Math.Min(endScale, _s0);
        // the last window stays inside the picture, on whole steps of the end scale: the last frame then reads pixels, not positions between them
        double hw = ow * _s1 / 2, hh = oh * _s1 / 2;
        double step = _s1 >= 1 ? Math.Floor(_s1) : 1;
        double x0 = Math.Clamp(targetX - hw, 0, Math.Max(0, w - 2 * hw)), y0 = Math.Clamp(targetY - hh, 0, Math.Max(0, h - 2 * hh));
        x0 = Math.Floor(x0 / step) * step; y0 = Math.Floor(y0 / step) * step;
        _tx = x0 + hw; _ty = y0 + hh;
        _q0x = (_tx - w / 2.0) / _s0; _q0y = (_ty - h / 2.0) / _s0;
    }

    public int Frames => _n;
    /// <summary>Level-0 pixels per frame pixel in the first and the last frame.</summary>
    public double StartScale => _s0;
    public double EndScale => _s1;
    /// <summary>How many times larger the last frame shows the scene than the first.</summary>
    public double Zoom => _s0 / _s1;
    /// <summary>The middle of the last frame, level-0 pixels.</summary>
    public (double X, double Y) Target => (_tx, _ty);

    /// <summary>The eased time of frame <paramref name="k"/>: 0 at the first frame, 1 at the last, slow at both ends.</summary>
    public double Ease(int k) => 0.5 - 0.5 * WineCrt.Cos(Math.PI * Math.Min(1.0, (double)k / (_n - 1)));

    /// <summary>Frame <paramref name="k"/>: the scale and the top-left corner of its window, level-0 pixels.</summary>
    public (double Scale, double X0, double Y0) At(int k)
    {
        if (k >= _n - 1) return (_s1, _tx - _ow * _s1 / 2, _ty - _oh * _s1 / 2);
        double e = Ease(k);
        double s = k <= 0 ? _s0 : WineCrt.Exp((1 - e) * WineCrt.Log(_s0) + e * WineCrt.Log(_s1));
        double cx = _tx - _q0x * (1 - e) * s, cy = _ty - _q0y * (1 - e) * s;
        double hw = _ow * s / 2, hh = _oh * s / 2;
        cx = hw * 2 >= _w ? _w / 2.0 : Math.Clamp(cx, hw, _w - hw);
        cy = hh * 2 >= _h ? _h / 2.0 : Math.Clamp(cy, hh, _h - hh);
        return (s, cx - hw, cy - hh);
    }
}
