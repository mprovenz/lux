using Lux.Engine.Imaging;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// `lt::Image&lt;float&gt;` as the depth path uses it — a dense `W×H` grid with a stride, a data offset and the
/// **valid rectangle** in grid coordinates (the data always starts at grid pixel (0,0) of this view's own frame).
/// </summary>
public sealed class DepthImage
{
    public readonly float[] Data; public readonly int W, H, Stride, Offset; public readonly RectI Rect;
    public DepthImage(float[] data, int w, int h, int stride, RectI rect, int offset = 0)
    { Data = data; W = w; H = h; Stride = stride; Rect = rect; Offset = offset; }
    public DepthImage(int w, int h) : this(new float[(long)w * h], w, h, w, new RectI(0, 0, w, h)) { }
    public float this[int x, int y] => Data[Offset + (long)y * Stride + x];
    /// <summary>Dense copy in the view's own coordinates (row `y`, column `x` ∈ [0,W)×[0,H)).</summary>
    public float[] ToDense()
    {
        var d = new float[(long)W * H];
        for (int y = 0; y < H; y++) Array.Copy(Data, Offset + (long)y * Stride, d, (long)y * W, W);
        return d;
    }
}

/// <summary>
/// `lt::ImageResample&lt;0, float&gt;` — the 3-argument `(dst, src, size)` overload and its tile functor.
///
/// Mode 0 is **nearest neighbour in 16.16 fixed point**: `offset` and `scale` are `Vec2&lt;double&gt;`, each multiplied
/// by 65536.0 and truncated (`cvttsd2si`), then per destination pixel
/// <c>src[clamp((sy16·y + oy16) &gt;&gt; 16, rect.y0, rect.y1−1)][clamp((sx16·x + ox16) &gt;&gt; 16, rect.x0, rect.x1−1)]</c>
/// with `x`/`y` the **absolute** destination coordinates and the clamps against the *source view's* rect.
/// Lumen splits the x loop into clamped-left / unclamped / clamped-right
/// halves and caches the resampled row (block height **1**), which is
/// value-neutral. The destination is freshly allocated at `size` with rect `(0,0,size)`.
/// </summary>
public static class ImageResample0
{
    public static DepthImage Run(DepthImage src, int dw, int dh, double offX, double offY, double sx, double sy)
    {
        int ox16 = (int)(offX * 65536.0), oy16 = (int)(offY * 65536.0);
        int sx16 = (int)(sx * 65536.0), sy16 = (int)(sy * 65536.0);
        var dst = new DepthImage(dw, dh);
        int rx0 = src.Rect.X0, rx1 = src.Rect.X1 - 1, ry0 = src.Rect.Y0, ry1 = src.Rect.Y1 - 1;
        var xs = new int[dw];
        for (int x = 0; x < dw; x++) { int v = unchecked(sx16 * x + ox16) >> 16; xs[x] = v < rx0 ? rx0 : v > rx1 ? rx1 : v; }
        for (int y = 0; y < dh; y++)
        {
            int syi = unchecked(sy16 * y + oy16) >> 16;
            syi = syi < ry0 ? ry0 : syi > ry1 ? ry1 : syi;
            long ro = src.Offset + (long)syi * src.Stride, wo = (long)y * dw;
            for (int x = 0; x < dw; x++) dst.Data[wo + x] = src.Data[ro + xs[x]];
        }
        return dst;
    }

    /// <summary>The 3-argument overload: `offset = (0,0)`, `scale = (src.W / size.W, src.H / size.H)` in **double**;
    /// throws `"empty image!"` when the size is degenerate.</summary>
    public static DepthImage Run(DepthImage src, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0) throw new InvalidOperationException("empty image!");
        return Run(src, dw, dh, 0.0, 0.0, (double)src.W / dw, (double)src.H / dh);
    }
}

/// <summary>
/// The renderer's depth `ImageCache` — the object that `RendererPrivate::setInputDataStream`
/// allocates and that the `exportImage` lambda reads through <see cref="Fetch(DepthImage, RectI, ValueTuple{int, int})"/>.
///
/// It is double buffered: a published image and a pending one, with a "pending valid" flag; the publish step swaps the
/// two `lt::Image&lt;float&gt;` headers and clears the flag. The constructor allocates the published image
/// **zero-filled at the pipeline-level dims** `levels[min(1 − profileOffset, n−1)]` — level **1** on the L16, where
/// the profile offset is 0, i.e. 5216×3912 for a 10432×7824 canvas. That image's `(w,h)` is then reused as the
/// resample target size for every fill.
///
/// The fill is
/// <code>
///   inv   = lt::InverseDepth(src)                             — rcpps, no Newton step
///   inv'  = ImageResample&lt;0,float&gt;(inv, this-&gt;image.dims)     — nearest, offset 0
///   this-&gt;pending = lt::InverseDepthClip(inv', 100000.0f)      — min(rcpps(x), 1e5)
/// </code>
/// so the cache holds **metric depth in millimetres, clipped to 100 000 mm**, on the canvas grid at half canvas
/// resolution. `GDepth:Far="100000.0"` in every fmt-4 artefact is that clip, not a scene value; `GDepth:Near` is
/// the scene minimum, carried through the two `rcpps` round trips (≈ 2.4·10⁻⁴ relative).
/// </summary>
public sealed class DepthImageCache
{
    /// <summary>`min(rcpps(x), clip)` broadcast — the clip is 100000.0f.</summary>
    public const float Clip = 100000.0f;

    /// <summary>The published image.</summary>
    public DepthImage Current { get; private set; }

    /// <summary>The constructor: a zero image at `pipelineDims[min(1 − baseLevel, n−1)]`.</summary>
    public DepthImageCache((int W, int H) dims) { Current = new DepthImage(dims.W, dims.H); }

    /// <summary>The dims the constructor picks out of the pipeline-level list — index
    /// `min(1 − BaseLevel, n−1)` (BaseLevel = the renderer-profile level offset).</summary>
    public static (int W, int H) CacheDims(ExportLevels lv)
        => lv.PipelineDims[Math.Min(1 - lv.BaseLevel, lv.PipelineDims.Length - 1)];

    /// <summary>`lt::InverseDepth`: `rcpps` per lane, `rcpss` for the row tail —
    /// the raw 12-bit hardware approximation, no Newton step.</summary>
    public static float[] InverseDepth(float[] src, int w, int h, int stride)
    {
        var dst = new float[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            int so = y * stride, dofs = y * w, x = 0;
            for (; x + 4 <= w; x += 4)
                IntelApprox.Reciprocal(Vector128.Create(src[so + x], src[so + x + 1], src[so + x + 2], src[so + x + 3])).CopyTo(dst, dofs + x);
            for (; x < w; x++) dst[dofs + x] = IntelApprox.ReciprocalScalar(Vector128.CreateScalar(src[so + x])).ToScalar();
        }
        return dst;
    }

    /// <summary>`lt::InverseDepthClip(src, clip)`: `minps(rcpps(x), clip)` per
    /// 4 lanes, `minss(rcpss(x), clip)` for the row tail. The clip is broadcast into all four lanes.</summary>
    public static float[] InverseDepthClip(float[] src, int w, int h, int stride, float clip)
    {
        var dst = new float[(long)w * h]; var cv = Vector128.Create(clip);
        for (int y = 0; y < h; y++)
        {
            int so = y * stride, dofs = y * w, x = 0;
            for (; x + 4 <= w; x += 4)
                Sse.Min(IntelApprox.Reciprocal(Vector128.Create(src[so + x], src[so + x + 1], src[so + x + 2], src[so + x + 3])), cv).CopyTo(dst, dofs + x);
            for (; x < w; x++)
                dst[dofs + x] = Sse.MinScalar(IntelApprox.ReciprocalScalar(Vector128.CreateScalar(src[so + x])), Vector128.CreateScalar(clip)).ToScalar();
        }
        return dst;
    }

    /// <summary>The fill + the publish swap: inverse-depth, nearest-resample to the cache
    /// dims, back through `InverseDepthClip(1e5)`, then make it the current image.</summary>
    public void Set(DepthImage src)
    {
        var inv = InverseDepth(src.ToDense(), src.W, src.H, src.W);
        var invImg = new DepthImage(inv, src.W, src.H, src.W, new RectI(0, 0, src.W, src.H));
        var r = ImageResample0.Run(invImg, Current.W, Current.H);
        var d = InverseDepthClip(r.Data, r.W, r.H, r.Stride, Clip);
        Current = new DepthImage(d, r.W, r.H, r.W, new RectI(0, 0, r.W, r.H));
    }

    /// <summary>The depth-cache getter on the published image — see <see cref="Fetch(DepthImage, RectI, ValueTuple{int, int})"/>.</summary>
    public DepthImage Fetch(RectI rect, (int W, int H) dims) => Fetch(Current, rect, dims);

    /// <summary>
    /// The depth-cache getter (cache, out, rect, dims) of the `exportImage` lambda (GDepth, the `depth` map),
    /// of the DOFCache tile lambda and of the display overlays (debug view).
    ///
    /// `scale = (image.W / dims.W, image.H / dims.H)` (double). When both are exactly 1.0, the result is a plain **view** of
    /// `rect ∩ image.rect`. Otherwise the source window is `[max(⌊x0·sx⌋,0), min(⌈x1·sx⌉, image.W)) × …` (floor and ceiling in double precision), the offset
    /// `((float)x0 + 0.5f)·sx − srcX0` (computed before the window is clamped to the image's valid rect), and the
    /// resample runs in **inverse depth**: a reciprocal pass over the window with scale 1.0 (per row the first `(w/32)·32` pixels by
    /// `rcpps` + one Newton step `(1 − x·r)·r + r`, times 1.0; the rest `divss`), `ImageResample&lt;0,float&gt;` into a fresh
    /// `rect.W × rect.H` image, then the same reciprocal pass on the output in place — source and destination alias, so every pixel
    /// takes the `divss` path. The round trip is not the identity in the last bit. On the L16 the cache is at pipeline level 1, so
    /// the scale is 1 only for level-1 requests. Lumen uses this one function for the DOF tiles too.
    /// </summary>
    public static DepthImage Fetch(DepthImage img, RectI rect, (int W, int H) dims)
    {
        int x0 = rect.X0, y0 = rect.Y0, x1 = rect.X1, y1 = rect.Y1, rw = x1 - x0, rh = y1 - y0;
        if (rw == 0 || x1 < x0 || rh == 0 || y1 < y0 || (y0 | x0) < 0 || x1 > dims.W || y1 > dims.H)
            throw new InvalidOperationException("invalid ROI requested!");
        double sx = (double)img.W / dims.W, sy = (double)img.H / dims.H;
        if (sx == 1.0 && sy == 1.0)
        {
            int ix0 = Math.Max(x0, img.Rect.X0), iy0 = Math.Max(y0, img.Rect.Y0), ix1 = Math.Min(x1, img.Rect.X1), iy1 = Math.Min(y1, img.Rect.Y1);
            if (ix1 <= ix0 || iy1 <= iy0) return new DepthImage(Array.Empty<float>(), 0, 0, 0, default);
            return new DepthImage(img.Data, ix1 - ix0, iy1 - iy0, img.Stride,
                                  new RectI(img.Rect.X0 - ix0, img.Rect.Y0 - iy0, img.Rect.X1 - ix0, img.Rect.Y1 - iy0), img.Offset + ix0 + img.Stride * iy0);
        }
        int sx0 = Math.Max((int)Math.Floor(x0 * sx), 0), sy0 = Math.Max((int)Math.Floor(y0 * sy), 0);
        int sx1 = Math.Min((int)Math.Ceiling((rw + (double)x0) * sx), img.W), sy1 = Math.Min((int)Math.Ceiling((rh + (double)y0) * sy), img.H);
        double offX = ((float)x0 + 0.5f) * sx - sx0, offY = ((float)y0 + 0.5f) * sy - sy0;
        int vx0 = Math.Max(sx0, img.Rect.X0), vy0 = Math.Max(sy0, img.Rect.Y0), vx1 = Math.Min(sx1, img.Rect.X1), vy1 = Math.Min(sy1, img.Rect.Y1);
        int vw = vx1 <= vx0 || vy1 <= vy0 ? 0 : vx1 - vx0, vh = vw == 0 ? 0 : vy1 - vy0;
        if (vw > 0 && (long)rw * rh * 2 < (long)vw * vh) return FetchSampled(img, vx0, vy0, vw, vh, rw, rh, offX, offY, sx, sy);
        return FetchFull(img, vx0, vy0, vw, vh, rw, rh, offX, offY, sx, sy);
    }

    /// <summary>The steps as Lumen runs them: the reciprocal of the whole source window, the nearest resample, the reciprocal back.</summary>
    public static DepthImage FetchFull(DepthImage img, int vx0, int vy0, int vw, int vh, int rw, int rh, double offX, double offY, double sx, double sy)
    {
        // reciprocal pass (window, 1.0f): rcpps + Newton in blocks of 32 (8 × 4 lanes), divss for the row tail
        var inv = new float[vw * vh]; int vec = (vw / 32) * 32; var one = Vector128.Create(1.0f);
        for (int y = 0; y < vh; y++)
        {
            int so = img.Offset + (vy0 + y) * img.Stride + vx0, d = y * vw, x = 0;
            for (; x < vec; x += 4)
            {
                var v = Vector128.Create(img.Data[so + x], img.Data[so + x + 1], img.Data[so + x + 2], img.Data[so + x + 3]);
                var r = IntelApprox.Reciprocal(v);
                (((one - v * r) * r + r) * one).CopyTo(inv, d + x);
            }
            for (; x < vw; x++) inv[d + x] = 1.0f / img.Data[so + x];
        }
        var res = ImageResample0.Run(new DepthImage(inv, vw, vh, vw, new RectI(0, 0, vw, vh)), rw, rh, offX, offY, sx, sy);
        // reciprocal pass (out, 1.0f) in place: aliasing source and destination → divss on every pixel
        for (int i = 0; i < res.Data.Length; i++) res.Data[i] = 1.0f / res.Data[i];
        return res;
    }

    /// <summary>
    /// The same pixels as <see cref="FetchFull"/> when the result is much smaller than the source window (a coarse level reading the whole depth
    /// cache): the nearest resample reads one source pixel per output pixel, so only those pixels' reciprocals are made — each exactly as the
    /// full pass makes it (columns below <c>(vw / 32)·32</c>: the four-lane <c>rcpps</c> model + one Newton step, lane by lane; the rest: true
    /// division) — then the reciprocal back (bit for bit equal to <see cref="FetchFull"/>).
    /// </summary>
    public static DepthImage FetchSampled(DepthImage img, int vx0, int vy0, int vw, int vh, int rw, int rh, double offX, double offY, double sx, double sy)
    {
        int ox16 = (int)(offX * 65536.0), oy16 = (int)(offY * 65536.0), sx16 = (int)(sx * 65536.0), sy16 = (int)(sy * 65536.0);
        int vec = (vw / 32) * 32;
        var xs = new int[rw];
        for (int x = 0; x < rw; x++) { int v = unchecked(sx16 * x + ox16) >> 16; xs[x] = v < 0 ? 0 : v > vw - 1 ? vw - 1 : v; }
        var dst = new DepthImage(rw, rh);
        for (int y = 0; y < rh; y++)
        {
            int syi = unchecked(sy16 * y + oy16) >> 16; syi = syi < 0 ? 0 : syi > vh - 1 ? vh - 1 : syi;
            long so = img.Offset + (long)(vy0 + syi) * img.Stride + vx0, wo = (long)y * rw;
            for (int x = 0; x < rw; x++)
            {
                int px = xs[x]; float v = img.Data[so + px], inv;
                if (px < vec) { float r = Lux.Engine.Imaging.IntelApprox.Rcp(v); inv = ((1.0f - v * r) * r + r) * 1.0f; }
                else inv = 1.0f / v;
                dst.Data[wo + x] = 1.0f / inv;
            }
        }
        return dst;
    }

    /// <summary>
    /// Build the cache the way the renderer does for a finished capture: the stereo pipeline's full-resolution
    /// depth (`StereoAsyncApi.FullDepth` = `DenseUpsampleLayer.Result.Depth`, 4160×3120 covering the whole
    /// 10432×7824 ResAmp canvas — the same image the ResAmp warpfield builder samples with
    /// `sx = 4160/10432 = 0.398773015`) handed to `Set`.
    /// </summary>
    public static DepthImageCache FromFullDepth(float[] depth, int w, int h, ExportLevels lv)
    {
        var c = new DepthImageCache(CacheDims(lv));
        c.Set(new DepthImage(depth, w, h, w, new RectI(0, 0, w, h)));
        return c;
    }

    /// <summary>The rect the `exportImage` lambda builds: the export-level source rect shifted by the level origin,
    /// fetched against the pipeline-level dims.</summary>
    public DepthImage FetchForExport(ExportLevels lv, int level, RectI sourceRect)
    {
        var o = lv.Origins[level];
        return Fetch(new RectI(sourceRect.X0 + o.X, sourceRect.Y0 + o.Y, sourceRect.X1 + o.X, sourceRect.Y1 + o.Y),
                     lv.PipelineDims[level]);
    }
}
