using Lux.Engine.Pipeline;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// The depth cache's edit layer: an <c>Image&lt;u8&gt;</c> layer mask,
/// an <c>Image&lt;float&gt;</c> background depth, a <c>vector&lt;Rect&gt;</c> of edited rects and the cache dims. All in
/// depth-cache pixels. The depth-effect tile builder turns the mask into the matte and the background depth into the
/// occluded depth of every tile that intersects one of the rects.
/// Only the layered edge heal writes it; brush, lasso, adaptive brush and surface heal leave it empty (their pushes carry empty layer images).
/// </summary>
public sealed class DepthEditLayer
{
    public readonly int W, H;
    /// <summary>Layer mask / background depth at cache size; null until the first write (Lumen: empty images).</summary>
    public byte[]? Mask; public float[]? Background;
    public readonly List<RectI> Rects = new();

    public DepthEditLayer(int w, int h) { W = w; H = h; }

    /// <summary>The write (rect, background, mask): nothing when the
    /// background block is empty. On the first rect both images are allocated at cache size and zero-filled. The rect is appended unless an
    /// existing rect contains it; then the two blocks are copied in at <paramref name="rect"/> ∩ image.</summary>
    public void Write(RectI rect, float[]? background, byte[]? mask)
    {
        if (background == null) return;
        if (Rects.Count == 0) { Mask = new byte[(long)W * H]; Background = new float[(long)W * H]; }
        bool contained = false;
        foreach (var r in Rects)
            if (r.X0 <= rect.X0 && rect.X0 < r.X1 && r.Y0 <= rect.Y0 && rect.Y0 < r.Y1 && r.X0 < rect.X1 && rect.X1 <= r.X1 && r.Y0 < rect.Y1 && rect.Y1 <= r.Y1)
            { contained = true; break; }
        if (!contained) Rects.Add(rect);
        int bw = rect.Width, bh = rect.Height;
        var c = rect.Intersect(new RectI(0, 0, W, H));
        if (c.IsEmpty) return;
        if (c.Width != bw || c.Height != bh) throw new InvalidOperationException("src / dst image size mismatch!");
        for (int y = 0; y < bh; y++)
        {
            if (mask != null) Array.Copy(mask, (long)y * bw, Mask!, (long)(c.Y0 + y) * W + c.X0, bw);
            Array.Copy(background, (long)y * bw, Background!, (long)(c.Y0 + y) * W + c.X0, bw);
        }
    }

    /// <summary>
    /// The edit-layer part of a depth-effect tile's buffer.
    /// <paramref name="rect"/> is the tile's fetch rect on a level whose dims are <paramref name="levelDims"/> (the DepthCache's level list,
    /// the same rect the depth fetch uses). <c>scale = (double)cacheDim / levelDim</c>; when both are 1 the source rect is the rect itself;
    /// otherwise the source rect is <c>[max(floor(s·x0), 0), min(ceil(((double)w + x0)·s), cacheW))</c> (y alike) and the resample offset
    /// <c>(double)((float)x0 + 0.5f)·s − srcX0</c>. Returns nulls (Lumen: an empty DualLayerBuffer) when no edit rect intersects the
    /// source rect. Else the mask and the background depth of every intersecting rect (clipped to the source rect) are copied into zeroed
    /// images of the source rect's size, both are resampled nearest (16.16, `ImageResample&lt;0&gt;`) to the tile size when the scale is not 1,
    /// and become the buffer's occluded depth (the background depth) and matte (<c>(float)mask · (1.0f / 255.0f)</c>).
    /// </summary>
    public (float[]? OccDepth, float[]? Matte) TileBuffer(RectI rect, (int W, int H) levelDims)
    {
        int x0 = rect.X0, y0 = rect.Y0, rw = rect.X1 - rect.X0, rh = rect.Y1 - rect.Y0;
        if (rw <= 0 || rh <= 0 || (x0 | y0) < 0 || rect.X1 > levelDims.W || rect.Y1 > levelDims.H) throw new InvalidOperationException("invalid ROI requested!");
        double sx = (double)W / (double)levelDims.W, sy = (double)H / (double)levelDims.H;
        bool unit = sx == 1.0 && sy == 1.0; RectI src; double offX = 0.0, offY = 0.0;
        if (unit) src = rect;
        else
        {
            int a, b;
            a = CvttD(Math.Floor(sx * (double)x0)); b = CvttD(Math.Floor(sy * (double)y0));
            int c = CvttD(Math.Ceiling(((double)rw + (double)x0) * sx)), d = CvttD(Math.Ceiling(((double)rh + (double)y0) * sy));
            if (a < 0) a = 0;
            if (b < 0) b = 0;
            if (!(c < W)) c = W;
            if (!(d < H)) d = H;
            src = new RectI(a, b, c, d);
            offX = (double)((float)x0 + 0.5f) * sx - (double)a; offY = (double)((float)y0 + 0.5f) * sy - (double)b;
        }
        if (Rects.Count == 0 || Mask == null) return (null, null);
        var hit = new List<RectI>();
        foreach (var r in Rects)
        {
            var q = new RectI(Math.Max(src.X0, r.X0), Math.Max(src.Y0, r.Y0), Math.Min(src.X1, r.X1), Math.Min(src.Y1, r.Y1));
            if (q.X0 < q.X1 && q.Y0 < q.Y1) hit.Add(q);
        }
        if (hit.Count == 0) return (null, null);
        int sw = src.X1 - src.X0, sh = src.Y1 - src.Y0;
        var m = new byte[(long)sw * sh]; var bg = new float[(long)sw * sh];
        foreach (var q in hit)
            for (int y = q.Y0; y < q.Y1; y++)
            {
                Array.Copy(Mask, (long)y * W + q.X0, m, (long)(y - src.Y0) * sw + (q.X0 - src.X0), q.X1 - q.X0);
                Array.Copy(Background!, (long)y * W + q.X0, bg, (long)(y - src.Y0) * sw + (q.X0 - src.X0), q.X1 - q.X0);
            }
        if (!unit)
        {
            int ox = (int)(offX * 65536.0), oy = (int)(offY * 65536.0), fx = (int)(sx * 65536.0), fy = (int)(sy * 65536.0);
            var m2 = new byte[(long)rw * rh]; var bg2 = new float[(long)rw * rh];
            for (int y = 0; y < rh; y++)
            {
                int iy = unchecked(fy * y + oy) >> 16; iy = iy < 0 ? 0 : iy > sh - 1 ? sh - 1 : iy;
                for (int x = 0; x < rw; x++)
                {
                    int ix = unchecked(fx * x + ox) >> 16; ix = ix < 0 ? 0 : ix > sw - 1 ? sw - 1 : ix;
                    m2[(long)y * rw + x] = m[(long)iy * sw + ix]; bg2[(long)y * rw + x] = bg[(long)iy * sw + ix];
                }
            }
            m = m2; bg = bg2;
        }
        var matte = new float[m.Length]; float k = 1.0f / 255.0f;
        for (int i = 0; i < m.Length; i++) matte[i] = (float)m[i] * k;
        return (bg, matte);
    }

    static int CvttD(double v) => v > -2147483649.0 && v < 2147483648.0 ? (int)v : int.MinValue;

    /// <summary>The clear (rect): a non-empty rect removes every list entry equal to it and zeroes its area in both images;
    /// an empty rect clears the list and frees both images.</summary>
    public void Remove(RectI rect)
    {
        if (rect.X0 < rect.X1 && rect.Y0 < rect.Y1)
        {
            Rects.RemoveAll(r => r == rect);
            var c = rect.Intersect(new RectI(0, 0, W, H));
            if (c.IsEmpty || Mask == null) return;
            for (int y = c.Y0; y < c.Y1; y++)
            {
                Array.Clear(Background!, (int)((long)y * W + c.X0), c.Width);
                Array.Clear(Mask, (int)((long)y * W + c.X0), c.Width);
            }
        }
        else { Rects.Clear(); Mask = null; Background = null; }
    }
}
