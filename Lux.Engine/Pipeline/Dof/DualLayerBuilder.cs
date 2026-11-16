using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>`lt::RunTiledDefocusBlur(buffer, tile, f, pitch, N, focus, post, device)`: the defocus blur
/// over `Tiler` tiles of a whole buffer, each tile grown by the blur margin of the depth range around it.</summary>
public static class TiledDefocusBlur
{
    public static DofImage Run(DualLayerBuffer buf, int tile, float f, float pitch, float n, float focus, bool post, int deviceType)
    {
        var depth = buf.VisDepth!; int w = buf.Vis!.W, h = buf.Vis.H;
        var outp = new DofImage(w, h, 4);
        var dimg = new DepthImage(depth.ToArray(), depth.W, depth.H, depth.W, new RectI(0, 0, depth.W, depth.H));
        foreach (var t in Tiler.Rects(new RectI(0, 0, w, h), tile, tile))
        {
            // depth min / max over the tile grown by its own size (DofGeometry.MinMaxGrown), then the margin (DofGeometry.Margin)
            var (mn, mx) = DofGeometry.MinMaxGrown(dimg, t);
            int m = DofGeometry.Margin(mn, mx, f, pitch, n, focus, deviceType);
            int u = m * 2; if (u < 0) u = 0; u = (int)((uint)u >> 1);
            int left = t.X0 <= m ? t.X0 : m, top = t.Y0 <= m ? t.Y0 : m;
            int right = w - t.X1, bottom = h - t.Y1;
            if (u < right) right = u;
            if (u < bottom) bottom = u;
            var rin = new RectI(t.X0 - left, t.Y0 - top, t.X1 + right, t.Y1 + bottom);
            var blur = new DefocusBlur(buf.Crop(rin), rin, rin, f, pitch, n, focus, deviceType);
            var o = blur.Run(post);
            int tw = t.X1 - t.X0, th = t.Y1 - t.Y0;
            for (int y = 0; y < th; y++) Array.Copy(o.D, ((y + top) * o.W + left) * 4, outp.D, ((t.Y0 + y) * w + t.X0) * 4, tw * 4);
        }
        return outp;
    }
}

/// <summary>
/// The two-layer builder (buffer, color, depth, {focus, f, f35, pitch}, synth, device, rangeInFocus) — fills a <see cref="DualLayerBuffer"/>:
/// the visible layer is the color and depth handed in; the occluded layer (what blurred foreground edges reveal) is synthesized from a
/// quarter-resolution coverage test and a push-pull fill.
/// </summary>
public static class DualLayerBuilder
{
    static readonly float[] Pull = { 0.05f, 0.25f, 0.4f, 0.25f, 0.05f };   // the same values for the vec4 and the float fill
    static readonly float[] Push = { 0.1f, 0.5f, 0.8f, 0.5f, 0.1f };       // the same values for the vec4 and the float fill

    /// <summary>The resize wrappers (vec4x32f, float): `ImageResample&lt;4&gt;` with offset 0 and
    /// scale ((double)srcW/(double)dstW, (double)srcH/(double)dstH).</summary>
    public static DofImage Resize(DofImage src, int dw, int dh) => Resize(src, dw, dh, null);
    static DofImage Resize(DofImage src, int dw, int dh, float[]? dst)
    {
        if (dw < 1 || dh < 1) throw new ArgumentException("invalid size!");
        return DofFilters.Resample4(src, dw, dh, 0.0, 0.0, (double)src.W / (double)dw, (double)src.H / (double)dh, dst);
    }
    /// <summary>The same for bytes.</summary>
    public static byte[] ResizeU8(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (dw < 1 || dh < 1) throw new ArgumentException("invalid size!");
        return DofFilters.Resample4U8(src, sw, sh, dw, dh, 0.0, 0.0, (double)sw / (double)dw, (double)sh / (double)dh);
    }

    /// <summary>Test hook: receives the push-pull intermediates (kind, image): "cpp_pv" / "dpp_pv" / "dpp_pw" filtered level before decimation,
    /// "cpp_up" / "dpp_up" filtered upsampled level, "dpp_lv" depth level after the push blend.</summary>
    [ThreadStatic] public static Action<string, DofImage>? PushPullObserver;

    /// <summary>Whether `ImageResample&lt;4,unsigned char&gt;` of an image that is 255 everywhere gives 255 everywhere: every tap reads 255, so a
    /// destination byte depends only on its two phases (and on which of the two vertical groupings its column uses) — all 64 × 64 × 2 cases.</summary>
    static readonly bool ConstantStays255 = DofFilters.ConstantResampleU8(255) == 255;

    static int Levels(int w, int h) => (int)MathF.Ceiling(DofMath.Log2f((float)(h <= w ? h : w)));

    /// <summary>Push-pull fill of a vec4 image (image, mask, levels). Pixels with mask ≠ 0
    /// are set to (0,0,0,0). Pull, level k = 1…levels−1: the 5×5 separable {0.05, 0.25, 0.4, 0.25, 0.05} of level k−1 sampled at even
    /// positions into ((w+1)&gt;&gt;1, (h+1)&gt;&gt;1); a sample with 0 &lt; alpha &lt; 1 is multiplied by 1/alpha and its alpha set to 1.
    /// Push, k = levels−2…0: level k+1 placed at the even positions of a zero image of level k's size, filtered with
    /// {0.1, 0.5, 0.8, 0.5, 0.1}, copied into level k where level k's alpha == 0. levels is capped at ceil(log2f(min(w, h))).</summary>
    public static DofImage PushPull(DofImage img, byte[] mask, int levels)
    {
        int cap = Levels(img.W, img.H); if (cap <= levels) levels = cap;
        var l0 = img.Compact();
        for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) { int q = i * 4; l0.D[q] = 0f; l0.D[q + 1] = 0f; l0.D[q + 2] = 0f; l0.D[q + 3] = 0f; }
        if (levels < 2) return l0;
        var pyr = new DofImage[levels]; pyr[0] = l0;
        for (int k = 1; k < levels; k++)
        {
            var cv = DofFilters.Conv5(pyr[k - 1], Pull, Pull); int nw = (cv.W + 1) >> 1, nh = (cv.H + 1) >> 1;
            PushPullObserver?.Invoke("cpp_pv", cv);
            var lv = new DofImage(nw, nh, 4);
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int s = (y * 2 * cv.W + x * 2) * 4, d = (y * nw + x) * 4;
                    float r = cv.D[s], g = cv.D[s + 1], b = cv.D[s + 2], a = cv.D[s + 3];
                    if (0f < a && a < 1f) { float t = 1f / a; r = t * r; g = g * t; b = b * t; a = 1f; }
                    lv.D[d] = r; lv.D[d + 1] = g; lv.D[d + 2] = b; lv.D[d + 3] = a;
                }
            pyr[k] = lv;
        }
        for (int i = levels - 2; i >= 0; i--)
        {
            var lo = pyr[i]; var hi = pyr[i + 1]; var up = new DofImage(lo.W, lo.H, 4);
            for (int y = 0; y < hi.H; y++)
                for (int x = 0; x < hi.W; x++) Array.Copy(hi.D, (y * hi.W + x) * 4, up.D, (y * 2 * lo.W + x * 2) * 4, 4);
            up = DofFilters.Conv5(up, Push, Push);
            PushPullObserver?.Invoke("cpp_up", up);
            for (int q = 0; q < lo.D.Length; q += 4)
                if (lo.D[q + 3] == 0f) { lo.D[q] = up.D[q]; lo.D[q + 1] = up.D[q + 1]; lo.D[q + 2] = up.D[q + 2]; lo.D[q + 3] = up.D[q + 3]; }
        }
        return pyr[0];
    }

    /// <summary>The same scheme on a float image (image, mask, levels) with a separate weight image
    /// (value 0 / weight 0 where mask ≠ 0, weight 1 elsewhere). Pull: both filtered and decimated; where 0 &lt; weight &lt; 1 the value is
    /// divided by the weight (true division) and the weight set to 1. Push: the filtered upsampled value replaces level k's value where
    /// level k's weight == 0; weights are not updated on the way up.</summary>
    public static DofImage PushPullDepth(DofImage img, byte[] mask, int levels)
    {
        int cap = Levels(img.W, img.H); if (cap <= levels) levels = cap;
        if (levels < 1) return img.Compact();
        var v = new DofImage[levels]; var wt = new DofImage[levels];
        var src = img.Compact(); v[0] = new DofImage(img.W, img.H, 1); wt[0] = new DofImage(img.W, img.H, 1);
        for (int i = 0; i < mask.Length; i++) { if (mask[i] == 0) { v[0].D[i] = src.D[i]; wt[0].D[i] = 1f; } }
        for (int k = 1; k < levels; k++)
        {
            var cv = DofFilters.Conv5(v[k - 1], Pull, Pull); var cw = DofFilters.Conv5(wt[k - 1], Pull, Pull);
            int nw = (cv.W + 1) >> 1, nh = (cv.H + 1) >> 1;
            PushPullObserver?.Invoke("dpp_pv", cv); PushPullObserver?.Invoke("dpp_pw", cw);
            v[k] = new DofImage(nw, nh, 1); wt[k] = new DofImage(nw, nh, 1);
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    float val = cv.D[y * 2 * cv.W + x * 2], wv = cw.D[y * 2 * cv.W + x * 2];
                    if (wv < 1f && 0f < wv) { val = val / wv; wv = 1f; }
                    v[k].D[y * nw + x] = val; wt[k].D[y * nw + x] = wv;
                }
        }
        for (int i = levels - 2; i >= 0; i--)
        {
            var lo = v[i]; var hi = v[i + 1]; var up = new DofImage(lo.W, lo.H, 1);
            for (int y = 0; y < hi.H; y++)
                for (int x = 0; x < hi.W; x++) up.D[y * 2 * lo.W + x * 2] = hi.D[y * hi.W + x];
            up = DofFilters.Conv5(up, Push, Push);
            PushPullObserver?.Invoke("dpp_up", up);
            for (int q = 0; q < lo.D.Length; q++) if (wt[i].D[q] == 0f) lo.D[q] = up.D[q];
            PushPullObserver?.Invoke("dpp_lv", lo);
        }
        return v[0];
    }

    /// <summary>Stage outputs of <see cref="Build"/> for tests (all quarter resolution unless named Full).</summary>
    public sealed class Trace
    {
        public DofImage? QDepth, QBlur, QCol, QDep, QColFill, QDepFill, ColFull, DepFull; public float[]? QCov; public byte[]? QHole, QMask, HoleFull;
    }

    /// <summary>The coverage hole (cov, hole, depth, prm, 0, device): depth at (w/4, h/4) (toward zero); a temporary buffer with that depth and a
    /// zero color image goes through `RunTiledDefocusBlur(tile 512, f, pitch·4, N = (f/f35)·2.2, focus, no post)`; the alpha
    /// lane of the result is the coverage; hole = (coverage &lt; 0.99 &amp;&amp; depth &lt; focus) ? 0x00 : 0xff.
    /// Both stay at quarter resolution.</summary>
    public static (float[] Cov, byte[] Hole, int W, int H) CoverageHole(DofImage depth, float focus, float f, float f35, float pitch, int deviceType, Trace? trace = null)
    {
        int qw = depth.W / 4, qh = depth.H / 4;
        var qdepth = Resize(depth, qw, qh);
        var tmp = new DualLayerBuffer { Vis = new DofImage(qw, qh, 4), VisDepth = qdepth };
        var qblur = TiledDefocusBlur.Run(tmp, 0x200, f, pitch * 4f, (f / f35) * 2.2f, focus, false, deviceType);
        var cov = new float[qw * qh]; var hole = new byte[qw * qh];
        for (int i = 0; i < cov.Length; i++) { cov[i] = qblur.D[i * 4 + 3]; hole[i] = (0.99f > cov[i] && focus > qdepth.D[i]) ? (byte)0 : (byte)0xff; }
        if (trace != null) { trace.QDepth = qdepth; trace.QBlur = qblur; trace.QCov = cov; trace.QHole = hole; }
        return (cov, hole, qw, qh);
    }

    /// <summary>The occluded fill (out, buffer, depth, hole, cov, 0): quarter-size color and depth; mask = hole == 0;
    /// push-pull fill of both with ceil(log2f(min(w, h))) levels; hole mask, color and depth resized back to full size; where the full-size
    /// hole byte is 0xff the occluded color and depth are 0, elsewhere the depth is max(filled, visible) (`filled &lt;= visible ? visible : filled`).
    /// (Mode 1 — the exemplar inpainting — is never requested.)</summary>
    public static (DofImage Colour, DofImage Depth) FillOccluded(DofImage colour, DofImage depth, byte[] hole, int qw, int qh, Trace? trace = null) => FillOccluded(colour, depth, hole, qw, qh, trace, false);

    /// <summary>With <paramref name="pooled"/> the two full-size results live in <see cref="ScratchPool"/> arrays (the caller returns them).</summary>
    static (DofImage Colour, DofImage Depth) FillOccluded(DofImage colour, DofImage depth, byte[] hole, int qw, int qh, Trace? trace, bool pooled)
    {
        int w = colour.W, h = colour.H;
        // No hole at all (every quarter-size byte 0xff — every tile below ~8K pixels wide): the full-size hole mask is then 0xff everywhere
        // (a constant 255 resamples to 255 for every phase pair, checked once in ConstantStays255) and the loop below zeroes every pixel of
        // both planes, so the push-pull and the resizes cannot reach the result
        if (trace == null && ConstantStays255 && depth.W == w && depth.H == h && hole.AsSpan(0, qw * qh).IndexOfAnyExcept((byte)0xff) < 0)
        {
            var zc = pooled ? new DofImage(ScratchPool.RentZeroed<float>(w * h * 4), 4, w, h) : new DofImage(w, h, 4);
            var zd = pooled ? new DofImage(ScratchPool.RentZeroed<float>(depth.W * depth.H), 1, depth.W, depth.H) : new DofImage(depth.W, depth.H, 1);
            return (zc, zd);
        }
        var qcol = Resize(colour, qw, qh); var qdep = Resize(depth, qw, qh);
        var mask = new byte[qw * qh];
        for (int i = 0; i < mask.Length; i++) mask[i] = hole[i] == 0 ? (byte)1 : (byte)0;
        int levels = Levels(qw, qh);
        var fcol = PushPull(qcol, mask, levels); var fdep = PushPullDepth(qdep, mask, levels);
        var holeFull = ResizeU8(hole, qw, qh, w, h);
        var col = Resize(fcol, w, h, pooled ? ScratchPool.Rent<float>(w * h * 4) : null); var dep = Resize(fdep, depth.W, depth.H, pooled ? ScratchPool.Rent<float>(depth.W * depth.H) : null);
        if (trace != null) { trace.QCol = qcol; trace.QDep = qdep; trace.QMask = mask; trace.QColFill = fcol; trace.QDepFill = fdep; trace.HoleFull = holeFull; trace.ColFull = col.Compact(); trace.DepFull = dep.Compact(); }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (holeFull[i] == 0xff) { col.D[i * 4] = 0f; col.D[i * 4 + 1] = 0f; col.D[i * 4 + 2] = 0f; col.D[i * 4 + 3] = 0f; dep.D[i] = 0f; }
                else { float vd = depth.D[depth.Idx(x, y)], fd = dep.D[i]; dep.D[i] = fd <= vd ? vd : fd; }
            }
        return (col, dep);
    }

    /// <summary>The occluded color inside a depth-edited region (out, color, depth, E, rangeInFocus; only when the buffer has a
    /// matte). Quarter-size color; hole mask H = E &gt; 0 || (near &lt; depth &amp;&amp; depth &lt; far), reduced to quarter size by
    /// `ImageResample&lt;0,unsigned char&gt;` (nearest, 16.16, offset 0, scale src/dst); push-pull fill of the color over H with
    /// ceil(log2f(min(w, h))) levels; resized back; out = E == 0 ? 0 : filled.</summary>
    public static DofImage OccludedInEdit(DofImage colour, DofImage depth, byte[] e, float near, float far)
    {
        int w = colour.W, h = colour.H, qw = w / 4, qh = h / 4;
        var qcol = Resize(colour, qw, qh);
        int dw = depth.W, dh = depth.H; var hm = new byte[dw * dh];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                int i = y * dw + x; float d = depth.D[depth.Idx(x, y)];
                hm[i] = (0 < e[i] || (near < d && far > d)) ? (byte)1 : (byte)0;
            }
        int mw = dw / 4, mh = dh / 4;
        if (mw < 1 || mh < 1) throw new ArgumentException("invalid size!");
        if (mw != qw || mh != qh) throw new InvalidOperationException("image size mismatch!");
        int sx16 = (int)(((double)dw / (double)mw) * 65536.0), sy16 = (int)(((double)dh / (double)mh) * 65536.0);
        var hq = new byte[mw * mh];
        for (int y = 0; y < mh; y++)
        {
            int sy = Math.Min(Math.Max((sy16 * y) >> 16, 0), dh - 1);
            for (int x = 0; x < mw; x++) hq[y * mw + x] = hm[sy * dw + Math.Min(Math.Max((sx16 * x) >> 16, 0), dw - 1)];
        }
        var fill = PushPull(qcol, hq, Levels(mw, mh));
        var o = Resize(fill, w, h);
        for (int i = 0; i < w * h; i++) if (e[i] == 0) { o.D[i * 4] = 0f; o.D[i * 4 + 1] = 0f; o.D[i * 4 + 2] = 0f; o.D[i * 4 + 3] = 0f; }
        return o;
    }

    /// <summary>The two-layer builder. <paramref name="buf"/> may already carry the depth editor's planes (occluded depth = background depth, matte);
    /// the visible layer is set to <paramref name="colour"/> / <paramref name="depth"/>. With a matte: E = 0.01 &lt; occludedDepth
    /// and the occluded color comes from <see cref="OccludedInEdit"/>. With <paramref name="synth"/>: the generic synthesis
    /// (<see cref="CoverageHole"/>, <see cref="FillOccluded"/>) replaces the occluded color and depth — everywhere without a matte, only where
    /// E == 0 with one.</summary>
    public static void Build(DualLayerBuffer buf, DofImage colour, DofImage depth, float focus, float f, float f35, float pitch, bool synth, int deviceType,
        float rangeNear, float rangeFar, Trace? trace = null) => Build(buf, colour, depth, focus, f, f35, pitch, synth, deviceType, rangeNear, rangeFar, trace, null);

    /// <summary>With <paramref name="pooledOut"/> the synthesized full-size planes are <see cref="ScratchPool"/> arrays, listed there for the caller
    /// to give back once the buffer is no longer read.</summary>
    internal static void Build(DualLayerBuffer buf, DofImage colour, DofImage depth, float focus, float f, float f35, float pitch, bool synth, int deviceType,
        float rangeNear, float rangeFar, Trace? trace, List<float[]>? pooledOut)
    {
        buf.Vis = colour; buf.VisDepth = depth;
        byte[]? edited = null;
        if (buf.Matte != null)
        {
            var od = buf.OccDepth ?? throw new InvalidOperationException("no occluded layer in matting");
            edited = new byte[od.W * od.H];
            for (int y = 0; y < od.H; y++)
                for (int x = 0; x < od.W; x++) edited[y * od.W + x] = 0.01f < od.D[od.Idx(x, y)] ? (byte)1 : (byte)0;
            buf.Occ = OccludedInEdit(colour, depth, edited, rangeNear, rangeFar);
            buf.OccDepth = od.Compact();
        }
        if (!synth) return;
        var (_, hole, qw, qh) = CoverageHole(depth, focus, f, f35, pitch, deviceType, trace);
        var (col, dep) = FillOccluded(colour, depth, hole, qw, qh, trace, pooledOut != null);
        if (pooledOut != null) { pooledOut.Add(col.D); pooledOut.Add(dep.D); }
        if (edited == null) { buf.Occ = col; buf.OccDepth = dep; return; }
        var oc = buf.Occ!; var odp = buf.OccDepth!; int mw = buf.Matte!.W, mh = buf.Matte.H;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                int i = y * mw + x;
                if (edited[i] != 0) continue;
                Array.Copy(col.D, i * 4, oc.D, i * 4, 4); odp.D[i] = dep.D[i];
            }
    }
}
