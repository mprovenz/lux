using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// `lt::Image&lt;float&gt;` as the depth path uses it — a dense `W×H` grid with a stride, a data offset and the
/// **valid rectangle** in grid coordinates (`lt::Image`'s rect member at +0x00; the dims are at +0x10, the data
/// pointer at +0x20 and always addresses grid pixel (0,0) of this view's own frame).
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
/// `lt::ImageResample&lt;0, float&gt;` — `FUN_180462f80` (the 3-argument `(dst, src, size)` overload) and the tile
/// functor `FUN_1804632e0` (`lambda_1` of vtable `0x1806dfbc8`, thunk `0x1804632a0`).
///
/// Mode 0 is **nearest neighbour in 16.16 fixed point**: `offset` and `scale` are `Vec2&lt;double&gt;`, each multiplied
/// by 65536.0 and truncated (`cvttsd2si`, `0x180688bb0` = 65536.0), then per destination pixel
/// <c>src[clamp((sy16·y + oy16) &gt;&gt; 16, rect.y0, rect.y1−1)][clamp((sx16·x + ox16) &gt;&gt; 16, rect.x0, rect.x1−1)]</c>
/// with `x`/`y` the **absolute** destination coordinates and the clamps against the *source view's* rect
/// (`0x180463475`/`0x18046349e`). The real code splits the x loop into clamped-left / unclamped / clamped-right
/// halves and caches the resampled row (`FUN_1804636f0` sets the block height to **1**, `0x18046371d`), which is
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

    /// <summary>`FUN_180462f80`: `offset = (0,0)`, `scale = (src.W / size.W, src.H / size.H)` in **double**
    /// (`0x180463013`–`0x18046302d`); throws `"empty image!"` (`0x1806b8e6d`) when the size is degenerate.</summary>
    public static DepthImage Run(DepthImage src, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0) throw new InvalidOperationException("empty image!");
        return Run(src, dw, dh, 0.0, 0.0, (double)src.W / dw, (double)src.H / dh);
    }
}

/// <summary>
/// The renderer's depth `ImageCache` at **`renderer+0x480`** — the object `RendererPrivate::setInputDataStream`
/// allocates at `0x180492046` (`operator new(0x248)`, payload at `+0x10`, ctor `FUN_1804c9f30`) and the
/// `exportImage` lambda `0x180526ec0` reads through `FUN_1804cd2d0`.
///
/// It is double buffered: the published image lives at `this+0x18` and the pending one at `this+0x48`, with the
/// "pending valid" flag at `this+0x78`; `FUN_1804cacf0` swaps the two 48-byte `lt::Image&lt;float&gt;` headers and
/// clears the flag. The constructor allocates the published image **zero-filled at the pipeline-level dims**
/// `levels[min(1 − FUN_18050c640(profile), n−1)]` (`0x1804ca0f1`–`0x1804ca12d`) — level **1** on the L16, where
/// `FUN_18050c640` is 0, i.e. 5216×3912 for a 10432×7824 canvas. That image's `(w,h)` is then reused as the
/// resample target size for every fill.
///
/// The fill `FUN_1804cad60(this, src)` is
/// <code>
///   inv   = lt::InverseDepth(src)                             18030ac60  — rcpps, no Newton step
///   inv'  = ImageResample&lt;0,float&gt;(inv, this-&gt;image.dims)     180462f80  — nearest, offset 0
///   this-&gt;pending = lt::InverseDepthClip(inv', 100000.0f)      18030adb0  — min(rcpps(x), 1e5)
/// </code>
/// so the cache holds **metric depth in millimetres, clipped to 100 000 mm**, on the canvas grid at half canvas
/// resolution. `GDepth:Far="100000.0"` in every fmt-4 artefact is that clip, not a scene value; `GDepth:Near` is
/// the scene minimum, carried through the two `rcpps` round trips (≈ 2.4·10⁻⁴ relative).
/// </summary>
public sealed class DepthImageCache
{
    /// <summary>`min(rcpps(x), clip)` broadcast — `_DAT_1806bbab0` = 100000.0f.</summary>
    public const float Clip = 100000.0f;

    /// <summary>The published image (`this+0x18`).</summary>
    public DepthImage Current { get; private set; }

    /// <summary>`FUN_1804c9f30` L~0x1804ca0f1: a zero image at `pipelineDims[min(1 − baseLevel, n−1)]`.</summary>
    public DepthImageCache((int W, int H) dims) { Current = new DepthImage(dims.W, dims.H); }

    /// <summary>The dims the constructor picks out of the pipeline-level list (`renderer+0x288`) — index
    /// `min(1 − FUN_18050c640(renderer+0x90), n−1)`, i.e. `min(1 − BaseLevel, n−1)`.</summary>
    public static (int W, int H) CacheDims(ExportLevels lv)
        => lv.PipelineDims[Math.Min(1 - lv.BaseLevel, lv.PipelineDims.Length - 1)];

    /// <summary>`lt::InverseDepth` `0x18030ac60` (lambda `0x18030b0b0`): `rcpps` per lane, `rcpss` for the row tail —
    /// the raw 12-bit hardware approximation, no Newton step.</summary>
    public static float[] InverseDepth(float[] src, int w, int h, int stride)
    {
        var dst = new float[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            int so = y * stride, dofs = y * w, x = 0;
            for (; x + 4 <= w; x += 4)
                Sse.Reciprocal(Vector128.Create(src[so + x], src[so + x + 1], src[so + x + 2], src[so + x + 3])).CopyTo(dst, dofs + x);
            for (; x < w; x++) dst[dofs + x] = Sse.ReciprocalScalar(Vector128.CreateScalar(src[so + x])).ToScalar();
        }
        return dst;
    }

    /// <summary>`lt::InverseDepthClip(src, clip)` `0x18030adb0` (lambda `0x18030b2d0`): `minps(rcpps(x), clip)` per
    /// 4 lanes, `minss(rcpss(x), clip)` for the row tail. The clip is broadcast into all four lanes.</summary>
    public static float[] InverseDepthClip(float[] src, int w, int h, int stride, float clip)
    {
        var dst = new float[(long)w * h]; var cv = Vector128.Create(clip);
        for (int y = 0; y < h; y++)
        {
            int so = y * stride, dofs = y * w, x = 0;
            for (; x + 4 <= w; x += 4)
                Sse.Min(Sse.Reciprocal(Vector128.Create(src[so + x], src[so + x + 1], src[so + x + 2], src[so + x + 3])), cv).CopyTo(dst, dofs + x);
            for (; x < w; x++)
                dst[dofs + x] = Sse.MinScalar(Sse.ReciprocalScalar(Vector128.CreateScalar(src[so + x])), Vector128.CreateScalar(clip)).ToScalar();
        }
        return dst;
    }

    /// <summary>`FUN_1804cad60` + the publish swap `FUN_1804cacf0`: inverse-depth, nearest-resample to the cache
    /// dims, back through `InverseDepthClip(1e5)`, then make it the current image.</summary>
    public void Set(DepthImage src)
    {
        var inv = InverseDepth(src.ToDense(), src.W, src.H, src.W);
        var invImg = new DepthImage(inv, src.W, src.H, src.W, new RectI(0, 0, src.W, src.H));
        var r = ImageResample0.Run(invImg, Current.W, Current.H);
        var d = InverseDepthClip(r.Data, r.W, r.H, r.Stride, Clip);
        Current = new DepthImage(d, r.W, r.H, r.W, new RectI(0, 0, r.W, r.H));
    }

    /// <summary>
    /// `FUN_1804cd2d0(this, out, rect, dims)` — the getter the `exportImage` lambda `0x180526ec0` calls after adding
    /// `renderer+0x2d0[level]` to the rect and passing `&amp;renderer+0x288[level]`.
    ///
    /// `scale = (image.W / dims.W, image.H / dims.H)`. When both are exactly 1.0 (`DAT_18067ec50`) the result is a
    /// plain **view** of `rect ∩ image.rect`. Otherwise the source window is
    /// `[max(⌊x0·sx⌋,0), min(⌈x1·sx⌉, image.W)) × …` (`roundsd` modes 9/10), the offset is
    /// `((float)x0 + 0.5f)·sx − srcX0` (`DAT_180682404` = 0.5f) computed **before** the window is clamped to the
    /// image's valid rect, and `ImageResample&lt;0,float&gt;` fills a fresh `rect.W × rect.H` destination.
    /// </summary>
    public DepthImage Fetch(RectI rect, (int W, int H) dims)
    {
        int x0 = rect.X0, y0 = rect.Y0, x1 = rect.X1, y1 = rect.Y1, rw = x1 - x0, rh = y1 - y0;
        if (rw == 0 || x1 < x0 || rh == 0 || y1 < y0 || (y0 | x0) < 0 || x1 > dims.W || y1 > dims.H)
            throw new InvalidOperationException("invalid ROI requested!");
        var img = Current;
        double sx = (double)img.W / dims.W, sy = (double)img.H / dims.H;
        if (sx == 1.0 && sy == 1.0)
        {
            int ix0 = Math.Max(x0, img.Rect.X0), iy0 = Math.Max(y0, img.Rect.Y0);
            int ix1 = Math.Min(x1, img.Rect.X1), iy1 = Math.Min(y1, img.Rect.Y1);
            if (ix1 <= ix0 || iy1 <= iy0) return new DepthImage(Array.Empty<float>(), 0, 0, 0, default);
            return new DepthImage(img.Data, ix1 - ix0, iy1 - iy0, img.Stride,
                                  new RectI(img.Rect.X0 - ix0, img.Rect.Y0 - iy0, img.Rect.X1 - ix0, img.Rect.Y1 - iy0),
                                  img.Offset + (ix0 - 0) + img.Stride * (iy0 - 0));
        }
        int sx0 = Math.Max((int)Math.Floor(x0 * sx), 0), sy0 = Math.Max((int)Math.Floor(y0 * sy), 0);
        int sx1 = Math.Min((int)Math.Ceiling((rw + (double)x0) * sx), img.W);
        int sy1 = Math.Min((int)Math.Ceiling((rh + (double)y0) * sy), img.H);
        double offX = ((float)x0 + 0.5f) * sx - sx0, offY = ((float)y0 + 0.5f) * sy - sy0;
        int vx0 = Math.Max(sx0, img.Rect.X0), vy0 = Math.Max(sy0, img.Rect.Y0);
        int vx1 = Math.Min(sx1, img.Rect.X1), vy1 = Math.Min(sy1, img.Rect.Y1);
        DepthImage view = vx1 <= vx0 || vy1 <= vy0
            ? new DepthImage(Array.Empty<float>(), 0, 0, 0, default)
            : new DepthImage(img.Data, vx1 - vx0, vy1 - vy0, img.Stride,
                             new RectI(img.Rect.X0 - vx0, img.Rect.Y0 - vy0, img.Rect.X1 - vx0, img.Rect.Y1 - vy0),
                             img.Offset + vx0 + img.Stride * vy0);
        return ImageResample0.Run(view, rw, rh, offX, offY, sx, sy);
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

    /// <summary>The rect `0x180526ec0` builds: the export-level source rect shifted by the level origin
    /// `renderer+0x2d0[level]`, fetched against the pipeline-level dims `renderer+0x288[level]`.</summary>
    public DepthImage FetchForExport(ExportLevels lv, int level, RectI sourceRect)
    {
        var o = lv.Origins[level];
        return Fetch(new RectI(sourceRect.X0 + o.X, sourceRect.Y0 + o.Y, sourceRect.X1 + o.X, sourceRect.Y1 + o.Y),
                     lv.PipelineDims[level]);
    }
}
