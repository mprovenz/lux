using Lux.Engine.Imaging;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Registration;
using Lux.Engine.Pipeline.ResAmp;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>A stroke / polygon vertex in normalised depth-map coordinates (`CIAPI::Point&lt;float&gt;`): (0,0) = top-left of the edited level,
/// (1,1) = bottom-right.</summary>
public readonly record struct EditPoint(float X, float Y);

/// <summary>
/// Lumen's `lt::DepthEditingCommand`: the transient object every tool builds — points, radius normalised to the level
/// width, hardness, the normalised ROI and "needs the colour guide".
/// <see cref="Execute"/> is the command's virtual execute: it edits a float depth image that covers <c>rect</c> of the level, in place.
/// </summary>
public abstract class DepthEditCommand
{
    public readonly EditPoint[] Points; public readonly float Radius, Hardness; public readonly bool NeedsGuide;
    /// <summary>Normalised ROI x0, y0, x1, y1.</summary>
    public readonly float Rx0, Ry0, Rx1, Ry1;

    protected DepthEditCommand(IReadOnlyList<EditPoint> points, float radius, float hardness, bool needsGuide)
    {
        Points = points.ToArray(); Radius = radius; Hardness = hardness; NeedsGuide = needsGuide;
        // min / max of the points seeded with 1.0 / −1.0; margin 1.5·radius in x
        // and radius + radius in y; clamped to [0, 1].
        float minX = 1.0f, minY = 1.0f, maxX = -1.0f, maxY = -1.0f;
        foreach (var p in Points)
        {
            if (p.X <= minX) minX = p.X;
            if (p.Y <= minY) minY = p.Y;
            if (maxX <= p.X) maxX = p.X;
            if (maxY <= p.Y) maxY = p.Y;
        }
        float x0 = minX - 1.5f * radius, y0 = minY - (radius + radius);
        if (x0 <= 0.0f) x0 = 0.0f;
        if (y0 <= 0.0f) y0 = 0.0f;
        float x1 = 1.5f * radius + maxX, y1 = radius + radius + maxY;
        if (1.0f <= x1) x1 = 1.0f;
        if (1.0f <= y1) y1 = 1.0f;
        Rx0 = x0; Ry0 = y0; Rx1 = x1; Ry1 = y1;
    }

    /// <summary>The ROI in level pixels: `cvttss2si((float)dim · roi)` per component (as both executors and every execute compute it).</summary>
    public RectI PixelRoi((int W, int H) level)
        => new(EditMath.Cvtt((float)level.W * Rx0), EditMath.Cvtt((float)level.H * Ry0), EditMath.Cvtt((float)level.W * Rx1), EditMath.Cvtt((float)level.H * Ry1));

    /// <summary>The virtual execute — `execute(depth, guide, rect, levelSize)`. <paramref name="depth"/> is a dense
    /// <c>rect.Width × rect.Height</c> float image, edited in place; <paramref name="guide"/> the vec4 colour of the same rect (4 floats per
    /// pixel) when <see cref="NeedsGuide"/>. Returns false when the command's ROI does not touch the rect.</summary>
    public abstract bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level);

    // stroke mask, the same for every tile of one command (it is built on the command's own pixel ROI)
    float[]? _stroke; RectI _strokeRoi;
    /// <summary>The stroke stamped into a float mask of the ROI's size (cmd, out, roi, levelSize).</summary>
    protected float[] StrokeMask(RectI roi, (int W, int H) level)
    {
        if (_stroke != null && _strokeRoi == roi) return _stroke;
        _stroke = BrushStroke.Mask(Points, roi, level, Radius, Hardness); _strokeRoi = roi;
        return _stroke;
    }

    protected static void CheckSize(float[] depth, RectI rect)
    {
        if (depth.Length != (long)rect.Width * rect.Height) throw new InvalidOperationException("Depth size should be the same as ROI size");
    }

    /// <summary>The `rect ∩ ROI` every execute starts with (plain max / min per component).</summary>
    protected static RectI Clip(RectI rect, RectI roi)
        => new(rect.X0 < roi.X0 ? roi.X0 : rect.X0, rect.Y0 < roi.Y0 ? roi.Y0 : rect.Y0, roi.X1 < rect.X1 ? roi.X1 : rect.X1, roi.Y1 < rect.Y1 ? roi.Y1 : rect.Y1);
}

static class EditMath
{
    /// <summary>`cvttss2si`.</summary>
    public static int Cvtt(float v) => v >= -2147483648.0f && v < 2147483648.0f ? (int)v : int.MinValue;
}

/// <summary>The brush kernel and the stroke rasteriser shared by brush, adaptive brush, both heals and quick select.</summary>
public static class BrushStroke
{
    /// <summary>
    /// The brush kernel (out, n, hardness): a (2n+1)² float image, centre (n, n). Exponent <c>e = 0.4f / (1 − hardness)</c>
    /// (1e6 when <c>(double)(1 − hardness) &lt; 4e-7</c>). For i, j in 0…n:
    /// <c>d = (float)(sqrt((double)(j² + i²)) · (1.0 / (double)n))</c>; when d ≤ 1: <c>p = powf(d, e)</c>,
    /// <c>v = p &lt; 0.5 ? 1 − (p·p + p·p) : ((1 − p)·(1 − p))·2</c>, written to the four mirrored positions; the rest stays 0.
    /// `powf` is the CRT's (musl's algorithm).
    /// </summary>
    public static float[] Kernel(int n, float hardness)
    {
        if (!(0.0f <= hardness) || hardness > 1.0f) throw new ArgumentException("hardness should be within [0, 1]");
        float om = 1.0f - hardness;
        float e = (double)om >= 3.9999999999999998e-07 ? 0.4f / om : 1000000.0f;
        int size = 2 * n + 1; var k = new float[(long)size * size];
        if (n < 0) return k;
        double inv = 1.0 / (double)n;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                float d = (float)(Math.Sqrt((double)(j * j + i * i)) * inv);
                if (d > 1.0f) continue;                       // a NaN (n = 0) fails the compare and falls through
                float p = MuslMath.Powf(d, e), v;
                if (p >= 0.5f) { v = 1.0f - p; v *= v; v += v; }
                else { float q = p * p; q += q; v = 1.0f - q; }
                k[(n + i) * size + n + j] = v; k[(n - i) * size + n - j] = v; k[(n + i) * size + n - j] = v; k[(n - i) * size + n + j] = v;
            }
        return k;
    }

    /// <summary>
    /// The stroke mask: points → ROI pixels `cvtt(levelDim · p − (float)roiOrigin)`; brush object (r, hardness)
    /// with <c>r = cvtt(levelW · radius)</c> and kernel <c>Kernel(2r, hardness)</c>; a zero mask of the ROI's size, one
    /// segment stamp per segment with half width <c>r / 2</c> (signed division).
    /// </summary>
    public static float[] Mask(EditPoint[] pts, RectI roi, (int W, int H) level, float radius, float hardness)
    {
        float lw = level.W, lh = level.H;
        var px = new int[pts.Length]; var py = new int[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            px[i] = EditMath.Cvtt(lw * pts[i].X - (float)roi.X0);
            py[i] = EditMath.Cvtt(lh * pts[i].Y - (float)roi.Y0);
        }
        int r = EditMath.Cvtt(lw * radius);
        var kernel = Kernel(r * 2, hardness); int ks = 4 * r + 1;
        int w = roi.Width, h = roi.Height;
        var mask = new float[(long)Math.Max(w, 0) * Math.Max(h, 0)];
        for (int i = 1; i < pts.Length; i++)
            Stamp(mask, w, h, kernel, ks, (float)px[i - 1], (float)py[i - 1], (float)px[i], (float)py[i], r / 2);
        return mask;
    }

    /// <summary>The brush (r, hardness) and its stamp on pixel points (the quick-select stroke): kernel <c>Kernel(2r, hardness)</c>,
    /// a zero w × h mask, one stamp of half width <c>r / 2</c> per segment.</summary>
    public static float[] MaskPixels(IReadOnlyList<(int X, int Y)> pts, int w, int h, int r, float hardness)
    {
        var kernel = Kernel(r * 2, hardness); int ks = 4 * r + 1;
        var mask = new float[(long)Math.Max(w, 0) * Math.Max(h, 0)];
        for (int i = 1; i < pts.Count; i++)
            Stamp(mask, w, h, kernel, ks, (float)pts[i - 1].X, (float)pts[i - 1].Y, (float)pts[i].X, (float)pts[i].Y, r / 2);
        return mask;
    }

    /// <summary>
    /// The segment stamp (brush, mask, kernel, a, b, half): the segment a → b drawn as six textured triangles (start cap, body, end
    /// cap) with the kernel as the texture, composited with max.
    /// Direction <c>u = (a − b) · s</c>, <c>s = ((float)half · (rsqrtss(L) · −0.5)) · (L · r · r + −3)</c>, <c>L = dy·dy + dx·dx</c>
    /// (`rsqrtss` + one Newton step). Vertices are truncated to ints (`cvttss2si`); texture
    /// coordinates u ∈ {0, 0.5, 1} along the stroke, v ∈ {0, 1} across it. Each triangle is scanned top to bottom with incremental float
    /// edge and texture steps; the texel is <c>kernel[cvtt(v·(size−1))·size + cvtt(u·(size−1))]</c> and the pixel becomes
    /// <c>maxss(pixel, texel)</c>.
    /// </summary>
    static void Stamp(float[] mask, int mw, int mh, float[] kernel, int ks, float ax, float ay, float bx, float by, int half)
    {
        float dx = ax - bx, dy = ay - by;
        float len2 = dy * dy + dx * dx;
        float rs = IntelApprox.Rsqrt(len2);
        float t = len2 * rs * rs + -3.0f;
        float s = (float)half * (rs * -0.5f) * t;
        float ux = dx * s, uy = s * dy;
        float t0 = uy + ay;
        float bxu = bx - ux;
        float byu = by - uy;
        float axu = ux + ax;
        // vertex list: x, y, u, v
        Span<int> vx = stackalloc int[18]; Span<int> vy = stackalloc int[18]; Span<float> vu = stackalloc float[18]; Span<float> vv = stackalloc float[18];
        int p0x = EditMath.Cvtt(axu + uy), p0y = EditMath.Cvtt(t0 - ux);            // a + u + n
        int p1x = EditMath.Cvtt(axu - uy), p1y = EditMath.Cvtt(t0 + ux);            // a + u − n
        int p2x = EditMath.Cvtt(uy + ax), p2y = EditMath.Cvtt(ay - ux);             // a + n
        int p3x = EditMath.Cvtt(ax - uy), p3y = EditMath.Cvtt(ay + ux);             // a − n
        int p4x = EditMath.Cvtt(uy + bx), p4y = EditMath.Cvtt(by - ux);             // b + n
        int p5x = EditMath.Cvtt(bx - uy), p5y = EditMath.Cvtt(by + ux);             // b − n
        int p6x = EditMath.Cvtt(bxu + uy), p6y = EditMath.Cvtt(byu - ux);           // b − u + n
        int p7x = EditMath.Cvtt(bxu - uy), p7y = EditMath.Cvtt(byu + ux);           // b − u − n
        int q = 0;
        void V(Span<int> X, Span<int> Y, Span<float> U, Span<float> Vv, int x, int y, float u, float v) { X[q] = x; Y[q] = y; U[q] = u; Vv[q] = v; q++; }
        V(vx, vy, vu, vv, p0x, p0y, 0.0f, 1.0f); V(vx, vy, vu, vv, p1x, p1y, 0.0f, 0.0f); V(vx, vy, vu, vv, p2x, p2y, 0.5f, 1.0f);
        V(vx, vy, vu, vv, p1x, p1y, 0.0f, 0.0f); V(vx, vy, vu, vv, p2x, p2y, 0.5f, 1.0f); V(vx, vy, vu, vv, p3x, p3y, 0.5f, 0.0f);
        V(vx, vy, vu, vv, p2x, p2y, 0.5f, 1.0f); V(vx, vy, vu, vv, p3x, p3y, 0.5f, 0.0f); V(vx, vy, vu, vv, p4x, p4y, 0.5f, 1.0f);
        V(vx, vy, vu, vv, p3x, p3y, 0.5f, 0.0f); V(vx, vy, vu, vv, p4x, p4y, 0.5f, 1.0f); V(vx, vy, vu, vv, p5x, p5y, 0.5f, 0.0f);
        V(vx, vy, vu, vv, p4x, p4y, 0.5f, 1.0f); V(vx, vy, vu, vv, p5x, p5y, 0.5f, 0.0f); V(vx, vy, vu, vv, p6x, p6y, 1.0f, 1.0f);
        V(vx, vy, vu, vv, p5x, p5y, 0.5f, 0.0f); V(vx, vy, vu, vv, p6x, p6y, 1.0f, 1.0f); V(vx, vy, vu, vv, p7x, p7y, 1.0f, 0.0f);
        float fmax = (float)(ks - 1);
        for (int tri = 0; tri < 6; tri++)
        {
            int i0 = tri * 3, i1 = i0 + 1, i2 = i0 + 2;
            int x0 = vx[i0], y0 = vy[i0], x1 = vx[i1], y1 = vy[i1], x2 = vx[i2], y2 = vy[i2];
            float u0 = vu[i0], v0 = vv[i0], u1 = vu[i1], v1 = vv[i1], u2 = vu[i2], v2 = vv[i2];
            // sort by y: A = top, then B / C → Mid / Hi (Lumen's cmov ladder; ties keep the earlier vertex first)
            bool g = y0 > y1;
            int xA = g ? x1 : x0, yA = g ? y1 : y0, xB = g ? x0 : x1, yB = g ? y0 : y1;
            float uA = g ? u1 : u0, vA = g ? v1 : v0, uB = g ? u0 : u1, vB = g ? v0 : v1;
            bool g2 = yA > y2;
            int xC = g2 ? xA : x2, yC = g2 ? yA : y2; float uC = g2 ? uA : u2, vC = g2 ? vA : v2;
            if (g2) { xA = x2; yA = y2; uA = u2; vA = v2; }
            bool g3 = yB > yC;
            int xHi = g3 ? xB : xC, yHi = g3 ? yB : yC, xMid = g3 ? xC : xB, yMid = g3 ? yC : yB;
            float uHi = g3 ? uB : uC, vHi = g3 ? vB : vC, uMid = g3 ? uC : uB, vMid = g3 ? vC : vB;

            // integer differences wrap (the degenerate a == b segment gives 0x80000000 everywhere); the two tests are signed compares
            float invH = 1.0f / (float)unchecked(yHi - yA);
            float dxLong = (float)unchecked(xHi - xA) * invH, duLong = (uHi - uA) * invH, dvLong = (vHi - vA) * invH;
            float xLong = (float)xA, uLong = uA, vLong = vA;
            int y = yA;
            if (yMid > yA)
            {
                float inv = 1.0f / (float)unchecked(yMid - yA);
                float dxS = (float)unchecked(xMid - xA) * inv, duS = (uMid - uA) * inv, dvS = (vMid - vA) * inv;
                float xS = (float)xA, uS = uA, vS = vA;
                do
                {
                    Span(mask, mw, mh, kernel, ks, fmax, y, EditMath.Cvtt(xS), EditMath.Cvtt(xLong), uS, vS, uLong, vLong);
                    y++;
                    uS += duS; vS += dvS; uLong += duLong; vLong += dvLong; xS += dxS; xLong += dxLong;
                } while (y < yMid);
            }
            y = yMid;
            if (yHi > yMid)
            {
                float inv = 1.0f / (float)unchecked(yHi - yMid);
                float dxS = (float)unchecked(xHi - xMid) * inv, duS = (uHi - uMid) * inv, dvS = (vHi - vMid) * inv;
                float xS = (float)xMid, uS = uMid, vS = vMid;
                do
                {
                    Span(mask, mw, mh, kernel, ks, fmax, y, EditMath.Cvtt(xS), EditMath.Cvtt(xLong), uS, vS, uLong, vLong);
                    y++;
                    uS += duS; vS += dvS; uLong += duLong; vLong += dvLong; xS += dxS; xLong += dxLong;
                } while (y < yHi);
            }
        }
    }

    /// <summary>One scan line between the short edge (xs, us, vs) and the long edge (xl, ul, vl): pixels [min, max) with the texture
    /// coordinates stepped by <c>(end − start) · (1 / |n|)</c> from the left end.</summary>
    static void Span(float[] mask, int mw, int mh, float[] kernel, int ks, float fmax, int y, int xs, int xl, float us, float vs, float ul, float vl)
    {
        int n = unchecked(xl - xs);
        if (n == 0) return;
        int cnt = n < 0 ? unchecked(-n) : n;
        int xa, xb; float ua, va, ub, vb;
        if (n < 0) { xa = xl; xb = xs; ua = ul; va = vl; ub = us; vb = vs; }
        else { xa = xs; xb = xl; ua = us; va = vs; ub = ul; vb = vl; }
        if (xa >= xb) return;
        float invn = 1.0f / (float)cnt;
        float du = (ub - ua) * invn, dv = invn * (vb - va);
        for (int x = xa; x < xb; x++)
        {
            if (x >= 0 && x < mw && y >= 0 && y < mh)
            {
                int ti = EditMath.Cvtt(va * fmax) * ks + EditMath.Cvtt(ua * fmax);
                if ((uint)ti >= (uint)kernel.Length) throw new InvalidOperationException("brush texture coordinate outside the kernel");
                float m = mask[(long)y * mw + x], tex = kernel[ti];
                mask[(long)y * mw + x] = m > tex ? m : tex;       // MAXSS order: NaN gives the texel
            }
            ua += du; va += dv;
        }
    }
}

/// <summary>Lumen's `lt::DepthBrushCommand`: hardness 0.9; <c>depth = (value − depth) · M + depth</c>
/// with M the stroke mask.</summary>
public sealed class DepthBrushCommand : DepthEditCommand
{
    public readonly float Depth;
    public DepthBrushCommand(IReadOnlyList<EditPoint> points, float radius, float depth) : base(points, radius, 0.9f, false) { Depth = depth; }

    public override bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level)
    {
        CheckSize(depth, rect);
        var roi = PixelRoi(level); var c = Clip(rect, roi);
        if (c.X1 == c.X0 || c.Y1 == c.Y0) return false;
        var m = StrokeMask(roi, level); int rw = rect.Width, mw = roi.Width;
        for (int y = c.Y0; y < c.Y1; y++)
        {
            long d = (long)(y - rect.Y0) * rw + (c.X0 - rect.X0), s = (long)(y - roi.Y0) * mw + (c.X0 - roi.X0);
            for (int x = c.X0; x < c.X1; x++, d++, s++) { float v = depth[d]; depth[d] = (Depth - v) * m[s] + v; }
        }
        return true;
    }
}

/// <summary>`GaussianLut(n, sigma)`: <c>N = n − 1</c>, <c>k = 0.5f / ((float)N·sigma)²</c>,
/// <c>lut[i] = expf((float)(i · −i) · k)</c> (CRT `expf`). The editor holds one, (256, 0.06).</summary>
public sealed class GaussianLut
{
    public readonly int N; public readonly float[] Table;
    public GaussianLut(int n, float sigma)
    {
        N = n - 1; Table = new float[n];
        float s = (float)(n - 1) * sigma; float k = 0.5f / (s * s);
        for (int i = 0; i < n; i++) Table[i] = MuslMath.Expf((float)(i * -i) * k);
    }
}

/// <summary>Lumen's `lt::DepthBrushAdaptiveCommand`: the brush weighted by the
/// colour distance to a reference colour. Per pixel with M &gt; 0: <c>d = max(|ref.r − g.r|, |ref.g − g.g|, |ref.b − g.b|)</c> (the 4th lane is
/// computed and dropped), <c>i = min(cvtt((float)N · d), N)</c>, <c>w = M · lut[i]</c>, <c>depth = (1 − w) · depth + w · value</c>.</summary>
public sealed class DepthBrushAdaptiveCommand : DepthEditCommand
{
    public readonly float Depth; public readonly float[] Reference; public readonly GaussianLut Lut;
    public DepthBrushAdaptiveCommand(IReadOnlyList<EditPoint> points, float[] reference, float depth, float radius, GaussianLut lut)
        : base(points, radius, 0.9f, true) { Depth = depth; Reference = reference; Lut = lut; }

    public override bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level)
    {
        CheckSize(depth, rect);
        var roi = PixelRoi(level); var c = Clip(rect, roi);
        if (c.X1 == c.X0 || c.Y1 == c.Y0) return false;
        if (guide == null) throw new InvalidOperationException("the adaptive brush needs the colour guide of the edited tile");
        var m = StrokeMask(roi, level); int rw = rect.Width, mw = roi.Width;
        float r0 = Reference[0], r1 = Reference[1], r2 = Reference[2]; int n = Lut.N; var lut = Lut.Table;
        for (int y = c.Y0; y < c.Y1; y++)
        {
            long d = (long)(y - rect.Y0) * rw + (c.X0 - rect.X0), s = (long)(y - roi.Y0) * mw + (c.X0 - roi.X0);
            for (int x = c.X0; x < c.X1; x++, d++, s++)
            {
                float mv = m[s];
                if (0.0f >= mv) continue;                                  // a NaN M does not skip
                long g = d * 4;
                float d0 = MathF.Abs(r0 - guide[g]), d1 = MathF.Abs(r1 - guide[g + 1]), d2 = MathF.Abs(r2 - guide[g + 2]);
                float a = d2 > d0 ? d2 : d0, b = d2 > d1 ? d2 : d1;       // MAXPS order: NaN gives d0 or d1
                float dist = a > b ? a : b;                                // MAXSS order: NaN gives b
                int i = EditMath.Cvtt((float)n * dist);
                if (n <= i) i = n;
                float w = mv * lut[i];
                depth[d] = (1.0f - w) * depth[d] + w * Depth;
            }
        }
        return true;
    }
}

/// <summary>Lumen's `lt::DepthLassoCommand`: radius field = feather · 0.05, hardness 0.
/// Per tile: polygon mask of the clipped rect grown by F = cvtt(levelW · radius) on every side (even-odd crossing test
/// against integer vertices), `ImageBoxFilter&lt;float&gt;` of size F × F when F &gt; 1, cropped back; then the brush's lerp.</summary>
public sealed class DepthLassoCommand : DepthEditCommand
{
    public readonly float Depth;
    public DepthLassoCommand(IReadOnlyList<EditPoint> points, float depth, float feather) : base(points, feather * 0.05f, 0.0f, false) { Depth = depth; }

    /// <summary>The polygon mask (out, points, clip, {F, F}) before the crop: the (clip.W + 2F) × (clip.H + 2F) mask, box filtered.</summary>
    public static float[] PolygonMask(int[] px, int[] py, RectI clip, int f, out int w, out int h)
    {
        w = clip.Width + f * 2; h = clip.Height + f * 2;
        var mask = new float[(long)w * h]; int n = px.Length;
        int bx = clip.X0 - f, by = clip.Y0 - f;
        for (int x = 0; x < w; x++)
        {
            float fx = (float)(bx + x);
            for (int y = 0; y < h; y++)
            {
                if (n == 0) continue;
                float fy = (float)(by + y);
                bool inside = false; int prev = n - 1;
                for (int k = 0; k < n; prev = k, k++)
                {
                    float cy = (float)py[k], pyv = (float)py[prev];
                    if (((cy < fy && fy <= pyv) || (fy <= cy && pyv < fy))
                        && (float)(px[prev] - px[k]) * ((fy - cy) / (float)(py[prev] - py[k])) + (float)px[k] < fx)
                        inside = !inside;
                }
                if (inside) mask[(long)y * w + x] = 1.0f;
            }
        }
        if (f > 1)
        {
            var img = new ResImage(mask, 0, w, h, w, 1, 0, 0, w, h);
            mask = ResAmpKernels.BoxFilter(img, f, f).Data;
        }
        return mask;
    }

    public override bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level)
    {
        float lw = level.W, lh = level.H;
        var px = new int[Points.Length]; var py = new int[Points.Length];
        for (int i = 0; i < Points.Length; i++) { px[i] = EditMath.Cvtt(lw * Points[i].X - 0.0f); py[i] = EditMath.Cvtt(lh * Points[i].Y - 0.0f); }
        var roi = PixelRoi(level); var c = Clip(rect, roi);
        if (c.X1 <= c.X0 || c.Y1 <= c.Y0) return false;
        int f = EditMath.Cvtt((float)level.W * Radius);
        var m = PolygonMask(px, py, c, f, out int mw, out _);
        int rw = rect.Width;
        for (int y = c.Y0; y < c.Y1; y++)
        {
            long d = (long)(y - rect.Y0) * rw + (c.X0 - rect.X0), s = (long)(y - c.Y0 + f) * mw + f;
            for (int x = c.X0; x < c.X1; x++, d++, s++) { float v = depth[d]; depth[d] = (Depth - v) * m[s] + v; }
        }
        return true;
    }
}

/// <summary>Lumen's `lt::SurfaceHealCommand`: hardness 1.0 (a flat disc). Inside the clipped ROI the
/// stroke pixels (M &gt; 0 → 0xff) are holes of the push-pull depth fill with
/// <c>levels = cvtt(ceil(log2f((float)min(w, h))))</c>; the fill is clamped <c>minss(maxss(fill, 200), 640000)</c>
/// and blended <c>depth = (fill − depth) · M + depth</c>.</summary>
public sealed class SurfaceHealCommand : DepthEditCommand
{
    public SurfaceHealCommand(IReadOnlyList<EditPoint> points, float radius) : base(points, radius, 1.0f, false) { }

    public override bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level)
    {
        CheckSize(depth, rect);
        var roi = PixelRoi(level); var c = Clip(rect, roi);
        if (c.X1 == c.X0 || c.Y1 == c.Y0) return false;
        var m = StrokeMask(roi, level); int rw = rect.Width, mw = roi.Width, w = c.Width, h = c.Height;
        var view = new DofImage(depth, 1, w, h, rw, (c.Y0 - rect.Y0) * rw + (c.X0 - rect.X0), rect.X0 - c.X0, rect.Y0 - c.Y0, rect.X1 - c.X0, rect.Y1 - c.Y0);
        var holes = new byte[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            long s = (long)(y + c.Y0 - roi.Y0) * mw + (c.X0 - roi.X0);
            for (int x = 0; x < w; x++) holes[(long)y * w + x] = m[s + x] > 0.0f ? (byte)0xff : (byte)0;
        }
        int levels = EditMath.Cvtt(MathF.Ceiling(DofMath.Log2f((float)(w <= h ? w : h))));
        var fill = DualLayerBuilder.PushPullDepth(view, holes, levels);
        for (int y = 0; y < h; y++)
        {
            long d = (long)(y + c.Y0 - rect.Y0) * rw + (c.X0 - rect.X0), s = (long)(y + c.Y0 - roi.Y0) * mw + (c.X0 - roi.X0);
            for (int x = 0; x < w; x++)
            {
                float fv = fill.D[(long)y * w + x];
                fv = fv > 200.0f ? fv : 200.0f;                  // MAXSS order: NaN gives 200
                fv = fv < 640000.0f ? fv : 640000.0f;            // MINSS order: NaN gives 640000
                float v = depth[d + x];
                depth[d + x] = (fv - v) * m[s + x] + v;
            }
        }
        return true;
    }
}
