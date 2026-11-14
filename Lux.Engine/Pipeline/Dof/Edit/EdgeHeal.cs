using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>The resamplers the edge heal uses that the rest of the depth-effect code does not: `ImageResample&lt;1, T&gt;` (bilinear, 64 phases,
/// 16.16 fixed point) for float and u8, and the plain 16.16 nearest pickers (u8 and vec4).</summary>
public static class EditResample
{
    /// <summary>Weight table: phase i → (1 − i/64, i/64).</summary>
    static readonly float[] W0 = Enumerable.Range(0, 64).Select(i => 1.0f - i * 0.015625f).ToArray(), W1 = Enumerable.Range(0, 64).Select(i => i * 0.015625f).ToArray();

    static int Clamp(int v, int lo, int hi) => v <= lo ? lo : (hi < v ? hi : v);

    /// <summary>`ImageResample&lt;1,float&gt;` through its wrapper
    /// (offset 0, scale = (double)src / dst): position <c>(int)(scale·65536)·x</c>; taps i and i + 1 clamped to the source; each pass
    /// <c>w1·s1 + w0·s0</c> with the phase = bits 10…15; horizontal first.</summary>
    public static float[] Bilinear(float[] src, int sw, int sh, int dw, int dh)
    {
        if (dw < 1 || dh < 1) throw new ArgumentException("invalid size!");
        int sx = (int)((double)sw / (double)dw * 65536.0), sy = (int)((double)sh / (double)dh * 65536.0);
        var dst = new float[(long)dw * dh]; var rows = new Dictionary<int, float[]>();
        float[] HRow(int iy)
        {
            int cy = Clamp(iy, 0, sh - 1);
            if (rows.TryGetValue(cy, out var r)) return r;
            r = new float[dw]; long b = (long)cy * sw;
            for (int x = 0; x < dw; x++)
            {
                int rx = unchecked(sx * x), ix = rx >> 16, ph = (rx >> 10) & 63;
                r[x] = W1[ph] * src[b + Clamp(ix + 1, 0, sw - 1)] + W0[ph] * src[b + Clamp(ix, 0, sw - 1)];
            }
            if (rows.Count > 8) rows.Clear();
            rows[cy] = r; return r;
        }
        for (int y = 0; y < dh; y++)
        {
            int ry = unchecked(sy * y), iy = ry >> 16, ph = (ry >> 10) & 63;
            var r0 = HRow(iy); var r1 = HRow(iy + 1); r0 = HRow(iy);
            float w0 = W0[ph], w1 = W1[ph]; long o = (long)y * dw;
            for (int x = 0; x < dw; x++) dst[o + x] = r1[x] * w1 + r0[x] * w0;
        }
        return dst;
    }

    /// <summary>`ImageResample&lt;1,unsigned char&gt;` through its wrapper: the same
    /// with float rows <c>(float)s1·w1 + (float)s0·w0</c> and the result truncated (`cvttss2si`, low byte).</summary>
    public static byte[] BilinearU8(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (dw < 1 || dh < 1) throw new ArgumentException("invalid size!");
        int sx = (int)((double)sw / (double)dw * 65536.0), sy = (int)((double)sh / (double)dh * 65536.0);
        var dst = new byte[(long)dw * dh]; var rows = new Dictionary<int, float[]>();
        float[] HRow(int iy)
        {
            int cy = Clamp(iy, 0, sh - 1);
            if (rows.TryGetValue(cy, out var r)) return r;
            r = new float[dw]; long b = (long)cy * sw;
            for (int x = 0; x < dw; x++)
            {
                int rx = unchecked(sx * x), ix = rx >> 16, ph = (rx >> 10) & 63;
                r[x] = (float)src[b + Clamp(ix + 1, 0, sw - 1)] * W1[ph] + (float)src[b + Clamp(ix, 0, sw - 1)] * W0[ph];
            }
            if (rows.Count > 8) rows.Clear();
            rows[cy] = r; return r;
        }
        for (int y = 0; y < dh; y++)
        {
            int ry = unchecked(sy * y), iy = ry >> 16, ph = (ry >> 10) & 63;
            var r0 = HRow(iy); var r1 = HRow(iy + 1); r0 = HRow(iy);
            float w0 = W0[ph], w1 = W1[ph]; long o = (long)y * dw;
            for (int x = 0; x < dw; x++) dst[o + x] = (byte)EditMath.Cvtt(r1[x] * w1 + r0[x] * w0);
        }
        return dst;
    }

    /// <summary>Nearest pick (u8; vec4 with <paramref name="c"/> = 4) with accumulated 16.16 steps
    /// <c>(int)(((double)src·65536) / (double)dst)</c> per axis, no clamp.</summary>
    public static T[] Nearest<T>(T[] src, int sw, int sh, int c, int dw, int dh)
    {
        var dst = new T[(long)dw * dh * c];
        if (dw <= 0 || dh <= 0) return dst;
        int stepX = (int)(((double)sw * 65536.0) / (double)dw), stepY = (int)(((double)sh * 65536.0) / (double)dh);
        for (int y = 0, ay = 0; y < dh; y++, ay = unchecked(ay + stepY))
        {
            long b = (long)(ay >> 16) * sw;
            for (int x = 0, ax = 0; x < dw; x++, ax = unchecked(ax + stepX))
                Array.Copy(src, (b + (ax >> 16)) * c, dst, ((long)y * dw + x) * c, c);
        }
        return dst;
    }

    /// <summary>One row of the reciprocal with scale 1.0f, source and destination distinct: the first (w/32)·32 pixels
    /// <c>((1 − x·r)·r + r)·1.0f</c> with r = `rcpps`, the rest <c>1.0f / x</c>.</summary>
    public static void InvertRow(float[] s, long so, float[] d, long dofs, int w)
    {
        int vec = (w / 32) * 32, x = 0; var one = Vector128.Create(1.0f);
        for (; x < vec; x += 4)
        {
            var v = Vector128.Create(s[so + x], s[so + x + 1], s[so + x + 2], s[so + x + 3]);
            var r = IntelApprox.Reciprocal(v);
            var q = ((one - v * r) * r + r) * one;
            d[dofs + x] = q.GetElement(0); d[dofs + x + 1] = q.GetElement(1); d[dofs + x + 2] = q.GetElement(2); d[dofs + x + 3] = q.GetElement(3);
        }
        for (; x < w; x++) d[dofs + x] = 1.0f / s[so + x];
    }
}

/// <summary>What the boundary refiner leaves in its object: alpha, the depth extrapolated from the in-interval
/// class ("foreground" — written as the visible depth) and from the out-of-interval class (written as the background depth).</summary>
public sealed class EdgeRefinement
{
    public byte[] Alpha = Array.Empty<byte>(); public float[] FgDepth = Array.Empty<float>(), BgDepth = Array.Empty<float>();
    /// <summary>Diagnostics: the trimap, and whether the half-resolution branch was taken.</summary>
    public byte[] Trimap = Array.Empty<byte>(); public bool HalfResolution; public int TrimapW, TrimapH;
}

/// <summary>
/// The edge heal's numeric stages. All images are dense, row-major, of the clipped ROI's size
/// (Lumen works on views of the gathered tiles; none of the stages reads outside the window).
/// </summary>
public static class EdgeHeal
{
    /// <summary>The blend threshold `0x50` every caller passes: mask values below 80 are background, above 175 foreground.</summary>
    public const byte Threshold = 0x50;

    /// <summary>The mask to bytes with scale 255.0f: <c>(byte)cvtt(m · 255.0f)</c>.</summary>
    public static byte[] StrokeBytes(float[] mask)
    {
        var s = new byte[mask.Length];
        for (int i = 0; i < s.Length; i++) s[i] = (byte)EditMath.Cvtt(mask[i] * 255.0f);
        return s;
    }

    /// <summary>The blend weights (t): 256 floats, zero except <c>w[a] = (float)(a − t) · (1.0f / (255.0f − (float)(2t)))</c> for a in [t, 255 − t].
    /// "blending weight has to be smaller than 128_u8!".</summary>
    public static float[] Weights(byte t)
    {
        if (t >= 128) throw new ArgumentException("blending weight has to be smaller than 128_u8!");
        var w = new float[256]; float k = 1.0f / (255.0f - (float)(t * 2));
        for (int a = t; a <= 255 - t; a++) w[a] = (float)(a - t) * k;
        return w;
    }

    /// <summary>An existing layered edit flattened to one depth (out, mask, depth, bg, t): <c>bg ≤ 0.01</c>
    /// → depth; mask &gt; 255 − t → depth; mask &lt; t → bg; else <c>(depth·bg) / ((1 − w)·depth + bg·w)</c>.</summary>
    public static float[] Flatten(byte[] mask, float[] depth, float[] bg, byte t = Threshold)
    {
        var w = Weights(t); var o = new float[depth.Length]; byte hi = (byte)~t;
        for (int i = 0; i < o.Length; i++)
        {
            float b = bg[i], d = depth[i];
            if (b <= 0.01f) { o[i] = d; continue; }
            byte m = mask[i];
            if (m <= hi) d = m < t ? b : (d * b) / ((1.0f - w[m]) * d + b * w[m]);
            o[i] = d;
        }
        return o;
    }

    /// <summary>
    /// The statistics test of the boundary refiner [A]: coefficient of variation of the depth over all pixels against 3 × that over the stroke
    /// pixels. Sums are single precision; per row the first (w &amp; ~3) pixels go through four lanes seeded with the
    /// running sums in lane 0 and are reduced <c>(l2 + l0) + (l3 + l1)</c>, the rest is added one by one. The mean over all pixels uses
    /// `rcpps` + Newton, the stroke mean a true division; both standard deviations use `rsqrtss` + Newton
    /// (<c>(v·r·r + −3)·(v·r·−0.5)</c>, 0 when the variance is exactly 0). True = the full-resolution branch.
    /// </summary>
    public static bool UseFullResolution(float[] d, byte[] s, int w, int h)
    {
        float sumAll = 0f, sqAll = 0f, sumS = 0f, sqS = 0f; int cnt = 0; int vec = w > 3 ? w & ~3 : 0;
        Span<float> a = stackalloc float[4], q = stackalloc float[4], ss = stackalloc float[4], qs = stackalloc float[4];
        for (int y = 0; y < h; y++)
        {
            long b = (long)y * w; int x = 0;
            if (vec > 0)
            {
                a.Clear(); q.Clear(); ss.Clear(); qs.Clear(); a[0] = sumAll; q[0] = sqAll; ss[0] = sumS; qs[0] = sqS;
                for (; x < vec; x += 4)
                    for (int k = 0; k < 4; k++)
                    {
                        float v = d[b + x + k], v2 = v * v;
                        a[k] += v; q[k] += v2;
                        if (s[b + x + k] != 0) { ss[k] = v + ss[k]; qs[k] = v2 + qs[k]; cnt++; }
                    }
                sqS = (qs[2] + qs[0]) + (qs[3] + qs[1]); sumS = (ss[2] + ss[0]) + (ss[3] + ss[1]);
                sqAll = (q[2] + q[0]) + (q[3] + q[1]); sumAll = (a[2] + a[0]) + (a[3] + a[1]);
            }
            for (; x < w; x++)
            {
                float v = d[b + x], v2 = v * v;
                if (s[b + x] != 0) { sumS += v; sqS += v2; cnt++; }
                sqAll += v2; sumAll += v;
            }
        }
        float nf = (float)(w * h), r = IntelApprox.Rcp(nf), k2 = (1.0f - nf * r) * r + r;
        float e2 = k2 * sqAll, mean = k2 * sumAll;
        float sd = Sd(e2 - mean * mean);
        float inv = 1.0f / (float)cnt;
        float meanS = sumS * inv, e2S = inv * sqS;
        float sdS = Sd(e2S - meanS * meanS);
        float lhs = sd / mean, rhs = (sdS / meanS) * 3.0f;
        return !(lhs > rhs);                                         // full resolution when lhs <= rhs or either is NaN
    }

    static float Sd(float v)
    {
        if (v == 0.0f) return 0.0f;
        float r = IntelApprox.Rsqrt(v), t = v * r;
        return (t * r + -3.0f) * (t * -0.5f);
    }

    /// <summary>The matting step: (vec4 guide, trimap, w, h) → alpha bytes (`MattingLaplacianTiling`).</summary>
    public delegate byte[] Matting(float[] guide, byte[] trimap, int w, int h);

    /// <summary>The known pixels the last <see cref="Refine"/> on this thread found (inside / outside the depth interval) — why a refinement
    /// was abandoned (for the app's message). No effect on the result.</summary>
    [ThreadStatic] public static (ulong In, ulong Out) LastCounts;

    /// <summary>
    /// The boundary refiner (obj, guide, depth, stroke, (lo, hi), eps). "Need margin for boundary refinement!" below 13 px.
    /// Full resolution: trimap = 0x7f on the stroke, else 0xff when <c>lo &lt; d &lt; hi</c>, else 0. Half resolution: a (w/2) × (h/2) trimap
    /// from the even pixels — 0x7f wherever the pixel is at least 4 px from every border, classified by depth on the border ring — and the
    /// "valid" image handed on is that interior flag instead of the stroke. The refinement is abandoned (null) unless both known classes are
    /// present and neither outnumbers the other by more than 50.
    /// Then: the two depth extrapolations, the matting on the guide (nearest-resampled to the trimap size in the half
    /// branch) and, in the half branch, the alpha bilinearly resampled back.
    /// </summary>
    public static EdgeRefinement? Refine(float[] guide, float[] depth, byte[] stroke, int w, int h, float lo, float hi, Matting matting)
    {
        LastCounts = (0, 0);
        if (w < 13 || h < 13) throw new InvalidOperationException("Need margin for boundary refinement!");
        var res = new EdgeRefinement(); byte[] tri, valid; int tw, th; ulong nOut = 0, nIn = 0;
        if (UseFullResolution(depth, stroke, w, h))
        {
            tw = w; th = h; tri = new byte[(long)w * h]; valid = stroke;
            for (long i = 0; i < tri.LongLength; i++)
            {
                if (stroke[i] != 0) { tri[i] = 0x7f; continue; }
                float d = depth[i];
                if (d <= lo || hi <= d) { tri[i] = 0; nOut++; } else { tri[i] = 0xff; nIn++; }
            }
        }
        else
        {
            res.HalfResolution = true; tw = w / 2; th = h / 2; tri = new byte[(long)tw * th]; valid = new byte[(long)w * h];
            for (int y = 0; y < h; y++)
            {
                int hy = y >> 1; if (th - 1 < hy) hy = th - 1;
                for (int x = 0; x < w; x++)
                {
                    bool interior = !(y < 4 || w - 5 < x || x < 4 || h - 5 < y);
                    valid[(long)y * w + x] = interior ? (byte)1 : (byte)0;
                    int hx = x >> 1; if (tw - 1 < hx) hx = tw - 1;
                    if (hy * 2 != y || hx * 2 != x) continue;
                    if (interior) { tri[(long)hy * tw + hx] = 0x7f; continue; }
                    float d = depth[(long)y * w + x];
                    if (d <= lo || hi <= d) { tri[(long)hy * tw + hx] = 0; nOut++; } else { tri[(long)hy * tw + hx] = 0xff; nIn++; }
                }
            }
        }
        res.Trimap = tri; res.TrimapW = tw; res.TrimapH = th;
        LastCounts = (nIn, nOut);
        if (nOut == 0 || nIn == 0) return null;
        if (!((float)nIn <= (float)nOut * 50.0f && (float)nOut <= (float)nIn * 50.0f)) return null;
        var inv = new byte[tri.Length];
        for (int i = 0; i < inv.Length; i++) inv[i] = tri[i] >= 0xff ? (byte)0 : (byte)0xff;      // a select, not 255 − T
        res.FgDepth = Extrapolate(depth, valid, w, h, inv, tw, th);
        res.BgDepth = Extrapolate(depth, valid, w, h, tri, tw, th);
        var g = tw == w ? guide : EditResample.Nearest(guide, w, h, 4, tw, th);
        var alpha = matting(g, tri, tw, th);
        res.Alpha = tw == w ? alpha : EditResample.BilinearU8(alpha, tw, th, w, h);
        return res;
    }

    /// <summary>
    /// The depth extrapolation (out, depth, valid, trimap): the depth at (w/4, h/4) (nearest), the trimap bilinearly resampled to
    /// that size; push-pull fill of the pixels whose trimap byte is not 0 with
    /// <c>levels = cvtt(ceil(log2f((float)min)))</c>; <c>1 / x</c>; bilinear resample to full size; the reciprocal
    /// again; then per pixel the result where <c>valid &gt; 0</c> and the original depth elsewhere.
    /// </summary>
    public static float[] Extrapolate(float[] depth, byte[] valid, int w, int h, byte[] tri, int tw, int th)
    {
        int qw = w / 4, qh = h / 4;
        var q = Export.ImageResample0.Run(new Export.DepthImage(depth, w, h, w, new RectI(0, 0, w, h)), qw, qh);
        var tq = EditResample.BilinearU8(tri, tw, th, qw, qh);
        int levels = EditMath.Cvtt(MathF.Ceiling(DofMath.Log2f((float)(qw <= qh ? qw : qh))));
        var fill = DualLayerBuilder.PushPullDepth(new DofImage(q.Data, 1, qw, qh), tq, levels).ToArray();
        var invq = new float[fill.Length];
        for (int i = 0; i < invq.Length; i++) invq[i] = 1.0f / fill[i];
        var up = EditResample.Bilinear(invq, qw, qh, w, h);
        var o = new float[(long)w * h];
        for (int y = 0; y < h; y++) EditResample.InvertRow(up, (long)y * w, o, (long)y * w, w);
        for (long i = 0; i < o.LongLength; i++) if (valid[i] == 0) o[i] = depth[i];
        return o;
    }

    /// <summary>
    /// The layer test (stroke, alpha): F = 1 where the stroke byte is 0, else 0; `ImageCircleFilter` radius 1;
    /// n1 = stroke pixels whose filtered value is &gt; 0 (the stroke's rim), n2 = alpha values in [77, 178]. True (single-layer write) when
    /// <c>n2 ≤ 2·n1</c>.
    /// </summary>
    public static bool SingleLayer(byte[] stroke, byte[] alpha, int w, int h)
    {
        var f = new float[stroke.Length];
        for (int i = 0; i < f.Length; i++) f[i] = stroke[i] == 0 ? 1.0f : 0.0f;
        var c = DofFilters.CircleFilter(new DofImage(f, 1, w, h), 1).ToArray();
        int n1 = 0, n2 = 0;
        for (int i = 0; i < f.Length; i++)
        {
            if (stroke[i] != 0 && 0.0f < c[i]) n1++;
            if ((byte)(alpha[i] + 0xb3) < 0x66) n2++;
        }
        return n2 <= n1 * 2;
    }

    /// <summary>The single-layer write (depth, bgDepth, fgDepth, stroke, alpha, t): on the stroke, alpha &gt; 255 − t → fg depth;
    /// alpha &lt; t → bg depth; else <c>(bg·fg) / ((1 − w)·fg + w·bg)</c>.</summary>
    public static void WriteSingle(float[] depth, EdgeRefinement r, byte[] stroke, byte t = Threshold)
    {
        var w = Weights(t); byte hi = (byte)~t;
        for (int i = 0; i < depth.Length; i++)
        {
            if (stroke[i] == 0) continue;
            byte a = r.Alpha[i]; float v = r.FgDepth[i];
            if (a <= hi)
            {
                float b = r.BgDepth[i];
                if (t <= a) b = (b * v) / ((1.0f - w[a]) * v + w[a] * b);
                v = b;
            }
            depth[i] = v;
        }
    }

    /// <summary>The layered write: on the stroke, depth = fg depth, background = bg depth, mask = alpha.</summary>
    public static void WriteLayered(float[] depth, float[] background, byte[] mask, EdgeRefinement r, byte[] stroke)
    {
        for (int i = 0; i < depth.Length; i++)
        {
            if (stroke[i] == 0) continue;
            depth[i] = r.FgDepth[i]; background[i] = r.BgDepth[i]; mask[i] = r.Alpha[i];
        }
    }
}

/// <summary>
/// Lumen's `lt::EdgeHealCommand`: hardness 1.0, needs the colour guide, holds the in-focus depth interval
/// (<see cref="DofModel.CurrentInFocusRange"/> with (2.2, 0.7)) and the focus depth. Only the layered execute
/// is reachable (executor B selects it by `dynamic_cast`); the plain virtual execute has no caller.
/// </summary>
public sealed class EdgeHealCommand : DepthEditCommand
{
    public readonly float Lo, Hi; public readonly EdgeHeal.Matting Matting;
    /// <summary>Set by the last <see cref="ExecuteLayered"/>: the refinement (null when abandoned) and which write was used.</summary>
    public EdgeRefinement? LastRefinement; public bool LastSingleLayer;

    public EdgeHealCommand(IReadOnlyList<EditPoint> points, float radius, float lo, float hi, EdgeHeal.Matting matting) : base(points, radius, 1.0f, true)
    { Lo = lo; Hi = hi; Matting = matting; }

    public override bool Execute(float[] depth, float[]? guide, RectI rect, (int W, int H) level)
        => throw new NotSupportedException("the edge heal runs through its layered execute");

    /// <summary>The layered execute (cmd, depth, background, mask, guide, rect, levelSize).</summary>
    public bool ExecuteLayered(float[] depth, ref float[]? background, ref byte[]? mask, float[]? guide, RectI rect, (int W, int H) level)
    {
        CheckSize(depth, rect);
        var roi = PixelRoi(level); var c = Clip(rect, roi);
        if (c.X1 == c.X0 || c.Y1 == c.Y0) return false;
        if (guide == null) throw new InvalidOperationException("the edge heal needs the colour guide");
        int w = c.Width, h = c.Height, rw = rect.Width, mw = roi.Width;
        var m = StrokeMask(roi, level);
        // the clipped views as dense images
        var mv = new float[(long)w * h]; var d = new float[(long)w * h]; var g = new float[(long)w * h * 4];
        float[]? bgv = background != null ? new float[(long)w * h] : null; byte[]? mkv = mask != null ? new byte[(long)w * h] : null;
        for (int y = 0; y < h; y++)
        {
            long so = (long)(y + c.Y0 - rect.Y0) * rw + (c.X0 - rect.X0);
            Array.Copy(m, (long)(y + c.Y0 - roi.Y0) * mw + (c.X0 - roi.X0), mv, (long)y * w, w);
            Array.Copy(depth, so, d, (long)y * w, w);
            Array.Copy(guide, so * 4, g, (long)y * w * 4, w * 4);
            if (bgv != null) Array.Copy(background!, so, bgv, (long)y * w, w);
            if (mkv != null) Array.Copy(mask!, so, mkv, (long)y * w, w);
        }
        var s = EdgeHeal.StrokeBytes(mv);
        var work = d;
        if (mkv != null)
        {
            if (bgv == null) throw new InvalidOperationException("Background depth should be available as well!");
            work = EdgeHeal.Flatten(mkv, d, bgv);
        }
        var r = LastRefinement = EdgeHeal.Refine(g, work, s, w, h, Lo, Hi, Matting);
        if (r == null) return false;
        LastSingleLayer = EdgeHeal.SingleLayer(s, r.Alpha, w, h);
        if (LastSingleLayer) EdgeHeal.WriteSingle(d, r, s);
        else
        {
            if (background == null) { background = new float[depth.Length]; mask = new byte[depth.Length]; bgv = new float[(long)w * h]; mkv = new byte[(long)w * h]; }
            EdgeHeal.WriteLayered(d, bgv!, mkv!, r, s);
        }
        for (int y = 0; y < h; y++)
        {
            long so = (long)(y + c.Y0 - rect.Y0) * rw + (c.X0 - rect.X0);
            Array.Copy(d, (long)y * w, depth, so, w);
            if (!LastSingleLayer) { Array.Copy(bgv!, (long)y * w, background!, so, w); Array.Copy(mkv!, (long)y * w, mask!, so, w); }
        }
        return true;
    }
}
