using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Export;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// Everything the DOFCache tile lambda computes around the builder and the blur for one tile, in the order it passes them on.
/// All rects are half-open pixel rects; "level" coordinates are pixels of the DOFCache level (= PipelineCache level) the tile belongs to.
/// </summary>
/// <param name="Level">Cache level.</param>
/// <param name="Tx">Tile column.</param>
/// <param name="Ty">Tile row.</param>
/// <param name="TileRect">The tile in level pixels: origin `(tx·tileW, ty·tileH)`, size as the tile cache gives it.</param>
/// <param name="Window">The 3×3-tile window around the tile's origin, clamped to the level — the rect of the depth-range query.</param>
/// <param name="DepthMin">Smallest depth (mm) that <see cref="DofGeometry.DepthRange"/> finds around <paramref name="Window"/>.</param>
/// <param name="DepthMax">Largest depth.</param>
/// <param name="LevelPitch">Pixel pitch of the level, mm: `((float)refW / (float)levelW) · pitch`.</param>
/// <param name="SyntheticFNumber">Nsyn — the f-number of the blur to ADD (1000 when the request is not below the native aperture).</param>
/// <param name="Margin">The blur margin in level pixels before clamping (<see cref="DofGeometry.Margin"/>).</param>
/// <param name="BlurRect">The tile grown by the margin, clamped to the level: the blur's input AND output tile rect.</param>
/// <param name="TileInBlur">The tile relative to <paramref name="BlurRect"/>: what is cut out of the blur output.</param>
/// <param name="FetchRect">`BlurRect` grown by up to 10 px per side, clamped to the level: the rect of the depth fetch,
/// the color `renderROI` and the edit-layer fetch, i.e. the builder's buffer.</param>
/// <param name="CropRect">`BlurRect` relative to <paramref name="FetchRect"/>: the rect to which the built
/// DualLayerBuffer is cropped before the blur.</param>
/// <param name="InFocusNear">The in-focus range (focus, f, SENSOR pitch, N = 2.2, blur = 0.7), near bound — the builder's last argument.</param>
/// <param name="InFocusFar">Its far bound.</param>
/// <param name="Focus">Current focus depth, mm.</param>
/// <param name="FocalLength">The physical focal length of the reference camera, mm.</param>
/// <param name="FocalLength35">The 35 mm-equivalent focal length of the reference camera.</param>
/// <param name="Device">DeviceType.</param>
public readonly record struct DofTileGeometry(int Level, int Tx, int Ty, RectI TileRect, RectI Window, float DepthMin, float DepthMax, float LevelPitch,
    float SyntheticFNumber, int Margin, RectI BlurRect, RectI TileInBlur, RectI FetchRect, RectI CropRect, float InFocusNear, float InFocusFar,
    float Focus, float FocalLength, float FocalLength35, int Device)
{
    /// <summary>The builder's parameter block `{focus, f, f35, level pitch}`.</summary>
    public (float Focus, float F, float F35, float Pitch) BuilderParams => (Focus, FocalLength, FocalLength35, LevelPitch);

    /// <summary>Whether a tile made with this geometry has the same pixels as one made with <paramref name="o"/> from the same color, depth
    /// and edit layer: every field the builder, the blur and the crops read is equal. <see cref="Window"/>, <see cref="DepthMin"/>,
    /// <see cref="DepthMax"/> and <see cref="Margin"/> are left out — they reach the pixels only through the rects (the margin sets
    /// <see cref="BlurRect"/>, <see cref="TileInBlur"/>, <see cref="FetchRect"/>, <see cref="CropRect"/>; the post stage's noise is seeded
    /// from positions inside the blur output, i.e. from the rects too). Floats compare by bit pattern.</summary>
    public bool SameOutputAs(in DofTileGeometry o)
        => Level == o.Level && Tx == o.Tx && Ty == o.Ty && TileRect.Equals(o.TileRect) && BlurRect.Equals(o.BlurRect) && TileInBlur.Equals(o.TileInBlur)
           && FetchRect.Equals(o.FetchRect) && CropRect.Equals(o.CropRect) && Device == o.Device
           && Bits(LevelPitch) == Bits(o.LevelPitch) && Bits(SyntheticFNumber) == Bits(o.SyntheticFNumber) && Bits(InFocusNear) == Bits(o.InFocusNear)
           && Bits(InFocusFar) == Bits(o.InFocusFar) && Bits(Focus) == Bits(o.Focus) && Bits(FocalLength) == Bits(o.FocalLength) && Bits(FocalLength35) == Bits(o.FocalLength35);
    static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    /// <summary>The final crop: the blur output's own rect (`lt::Image` rect of the image the blur returns, in that image's
    /// coordinates) intersected with <see cref="TileInBlur"/>; empty → an empty image goes to the half-float conversion. The pixels of the
    /// result are the blur output's pixels at these coordinates (data offset `stride·y0 + x0`).</summary>
    public RectI FinalCrop(RectI blurOutputRect)
    {
        var r = new RectI(Math.Max(blurOutputRect.X0, TileInBlur.X0), Math.Max(blurOutputRect.Y0, TileInBlur.Y0),
                          Math.Min(blurOutputRect.X1, TileInBlur.X1), Math.Min(blurOutputRect.Y1, TileInBlur.Y1));
        return r.X1 - r.X0 <= 0 || r.Y1 - r.Y0 <= 0 ? default : r;
    }
}

/// <summary>The scalar and rect arithmetic of the DOFCache tile lambda and the helpers it calls.</summary>
public static class DofGeometry
{
    /// <summary>The literals of the lambda's own in-focus range (`0x400ccccd` = 2.2f, `0x3f333333` = 0.7f).</summary>
    public const float BuilderRangeFNumber = 2.2f, BuilderRangeBlur = 0.7f;
    /// <summary>The ring added around the blur rect for the fetches.</summary>
    public const int FetchRing = 10;

    /// <summary>Level pixel pitch: `((float)refW / (float)levelW) · pitch` — the division first, then the multiplication, in single precision.</summary>
    public static float LevelPitch(DofConstants c, int levelWidth) => ((float)c.RefWidth / (float)levelWidth) * c.PixelPitch;

    /// <summary>
    /// The synthetic aperture: `Nreal = (fnum · f) / f35`. When `Nreal &gt;= N0` (ordered) the result is 1000.
    /// Otherwise both reciprocals come from one `rcpps` over `(Nreal, N0)` with one Newton step `(1 − x·r)·r + r`, and
    /// `Nsyn = 1 / (1/Nreal − 1/N0)` (true division); a result that is not &gt; 0 raises "Requested DOFCache render with no blur!".
    /// </summary>
    public static float SyntheticAperture(DofConstants c, float fnum)
    {
        float nreal = (fnum * c.FocalLength) / c.FocalLength35, n0 = c.NativeFNumber;
        if (nreal >= n0) return 1000.0f;
        float r0 = IntelApprox.Rcp(nreal), r1 = IntelApprox.Rcp(n0);
        float i0 = (1.0f - nreal * r0) * r0 + r0, i1 = (1.0f - n0 * r1) * r1 + r1;
        float nsyn = 1.0f / (i0 * 1.0f - i1 * 1.0f);                             // Lumen multiplies both by 1.0 before the subtraction
        if (!(nsyn > 0.0f)) throw new InvalidOperationException("Requested DOFCache render with no blur!");
        return nsyn;
    }

    /// <summary>The two entries of the DeviceType parameter block that the margin reads: the maximum blur radius in px
    /// (512 / 32) and the scale of the blur in front of the focus plane (0.5 / 0.25).</summary>
    static (int MaxRadius, float FrontScale) MarginParams(int device) => device switch
    {
        0 => (0x200, 0.5f),
        1 => (0x20, 0.25f),
        _ => throw new ArgumentException("Wrong device type"),
    };

    /// <summary>
    /// The signed circle-of-confusion radius of the margin function, px: <c>K = (f·f) / ((N+N)·pitch)</c>,
    /// <c>r = (rcpss(d·(focus − f)) · (d − focus)) · (d &lt; focus ? front·K : K)</c>, clamped `maxss(r, −(front·maxR))` then `minss(r, maxR)`.
    /// Raw `rcpss`, no Newton step.
    /// </summary>
    public static float MarginRadius(float d, float f, float pitch, float n, float focus, int device)
    {
        var (maxR, front) = MarginParams(device);
        float a = focus - f;
        float k = (f * f) / ((n + n) * pitch);
        float hi = (float)maxR, lo = -(front * hi);
        float r = IntelApprox.Rcp(d * a) * (d - focus);
        r = r * (d < focus ? front * k : k);
        r = r > lo ? r : lo;                                                     // MAXSS order: NaN gives lo
        return r < hi ? r : hi;                                                  // MINSS order: NaN gives hi
    }

    /// <summary>
    /// The blur margin of a tile from the depth range around it (range, f, pitch, N, focus, device):
    /// <c>A = maxss(|r(range.max)|, |r(range.min)|)</c> (<see cref="MarginRadius"/>),
    /// <c>n = (int)((double)log2f(A) / log2(2.0)) + 1</c> (CRT `log2f`, `cvttsd2si`), <c>margin = (int)(ldexp(1.0, n) · 1.6000000238418579)</c>
    /// (1.6000000238418579 is the double of 1.6f). A = 0 gives 0.
    /// </summary>
    public static int Margin(float rangeMin, float rangeMax, float f, float pitch, float n, float focus, int device)
    {
        float rmax = MathF.Abs(MarginRadius(rangeMax, f, pitch, n, focus, device));
        float rmin = MathF.Abs(MarginRadius(rangeMin, f, pitch, n, focus, device));
        float a = rmax > rmin ? rmax : rmin;                                     // MAXSS order: NaN gives |r(min)|
        int e = unchecked(DofModel.Cvtt((double)DofMath.Log2f(a) / 1.0) + 1);    // log2(2.0) = 1.0 exactly
        return DofModel.Cvtt(Math.ScaleB(1.0, e) * BitConverter.Int64BitsToDouble(0x3ff99999a0000000));
    }

    /// <summary>
    /// The depth range around a level rect: the depth-cache query, then the scan of the image.
    /// The rect goes to depth-cache pixels in single precision, <c>(x0, y0, (float)w + (float)x0, (float)h + (float)y0) · (sx, sy, sx, sy)</c>
    /// with `sx = (float)cacheW / (float)levelW`, truncated (`cvttps2dq`); an empty, negative or out-of-cache result raises
    /// "invalid ROI requested!". The scan then grows that rect by ITS OWN width and height on every side, clamps to the image dims
    /// and to the image rect, and takes min and max (an empty region gives +FLT_MAX / −FLT_MAX). The scan order is reproduced (per row:
    /// 8-wide `minps`/`maxps` into two accumulators seeded from the running scalars, reduced, then a scalar tail) so that signed zeros and
    /// NaNs resolve the same way.
    /// </summary>
    public static (float Min, float Max) DepthRange(DepthImage cache, (int W, int H) levelDims, RectI rect, DepthSummary? summary = null)
    {
        float sx = (float)cache.W / (float)levelDims.W, sy = (float)cache.H / (float)levelDims.H;
        int cx0 = DofModel.Cvtt((float)rect.X0 * sx), cy0 = DofModel.Cvtt((float)rect.Y0 * sy);
        int cx1 = DofModel.Cvtt(((float)(rect.X1 - rect.X0) + (float)rect.X0) * sx), cy1 = DofModel.Cvtt(((float)(rect.Y1 - rect.Y0) + (float)rect.Y0) * sy);
        if (cy1 <= cy0 || cx1 <= cx0 || (cy0 | cx0) < 0 || cache.W < cx1 || cache.H < cy1) throw new InvalidOperationException("invalid ROI requested!");
        if (summary is not null && summary.Clean && ReferenceEquals(summary.Image, cache)) return summary.MinMaxGrown(new RectI(cx0, cy0, cx1, cy1));
        return MinMaxGrown(cache, new RectI(cx0, cy0, cx1, cy1));
    }

    /// <summary>The depth-cache pixels <see cref="DepthRange"/> of <paramref name="rect"/> reads (its cache rect grown by its own size on every
    /// side, clamped): a change outside them cannot change the range. Null when the rect is invalid (the range would throw).</summary>
    public static RectI? DepthRangeReads(DepthImage cache, (int W, int H) levelDims, RectI rect)
    {
        float sx = (float)cache.W / (float)levelDims.W, sy = (float)cache.H / (float)levelDims.H;
        int cx0 = DofModel.Cvtt((float)rect.X0 * sx), cy0 = DofModel.Cvtt((float)rect.Y0 * sy);
        int cx1 = DofModel.Cvtt(((float)(rect.X1 - rect.X0) + (float)rect.X0) * sx), cy1 = DofModel.Cvtt(((float)(rect.Y1 - rect.Y0) + (float)rect.Y0) * sy);
        if (cy1 <= cy0 || cx1 <= cx0 || (cy0 | cx0) < 0 || cache.W < cx1 || cache.H < cy1) return null;
        int w = cx1 - cx0, h = cy1 - cy0;
        return new RectI(Math.Max(cx0 - w, 0), Math.Max(cy0 - h, 0), Math.Min(cx0 - w + 3 * w, cache.W), Math.Min(cy0 - h + 3 * h, cache.H));
    }

    /// <summary>The depth-range scan (out, image, rect).</summary>
    public static (float Min, float Max) MinMaxGrown(DepthImage img, RectI rect)
    {
        int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
        int gx0 = rect.X0 - w, gy0 = rect.Y0 - h, gx1 = gx0 + 3 * w, gy1 = gy0 + 3 * h;
        if (gx0 < 0) gx0 = 0;
        if (gy0 < 0) gy0 = 0;
        if (gx1 > img.W) gx1 = img.W;
        if (gy1 > img.H) gy1 = img.H;
        if (gx0 < img.Rect.X0) gx0 = img.Rect.X0;
        if (gy0 < img.Rect.Y0) gy0 = img.Rect.Y0;
        if (gx1 > img.Rect.X1) gx1 = img.Rect.X1;
        if (gy1 > img.Rect.Y1) gy1 = img.Rect.Y1;
        float mn = float.MaxValue, mx = -float.MaxValue;
        int rw = gx1 - gx0, rh = gy1 - gy0;
        if (rw <= 0 || rh <= 0) return (mn, mx);
        int tail = rw & 7, vec = rw - tail;
        for (int y = 0; y < rh; y++)
        {
            int row = img.Offset + (gy0 + y) * img.Stride + gx0, i = 0;
            if (rw > 7)
            {
                var mn1 = Vector128.Create(mn); var mx0 = Vector128.Create(mx); var mn2 = mn1; var mx3 = mx0;
                // the same lanes in the same order as Lumen's 8-wide loop (direct loads)
                ref float r0 = ref img.Data[row];
                for (; i < vec; i += 8)
                {
                    var a = Vector128.LoadUnsafe(ref r0, (nuint)i);
                    var b = Vector128.LoadUnsafe(ref r0, (nuint)(i + 4));
                    mn2 = Simd.Min(mn2, a); mn1 = Simd.Min(mn1, b); mx3 = Simd.Max(mx3, a); mx0 = Simd.Max(mx0, b);
                }
                mx3 = Simd.Max(mx3, mx0); var hi = Simd.Max(Simd.MoveHighToLow(mx3, mx3), mx3);
                float h0 = hi.GetElement(0), h1 = hi.GetElement(1); mx = h1 < h0 ? h0 : h1;
                mn2 = Simd.Min(mn2, mn1); var lo = Simd.Min(Simd.MoveHighToLow(mn2, mn2), mn2);
                float l0 = lo.GetElement(0), l1 = lo.GetElement(1); mn = l0 < l1 ? l0 : l1;
            }
            for (; i < rw; i++)
            {
                float v = img.Data[row + i];
                mn = Simd.MinScalar(Vector128.CreateScalar(mn), Vector128.CreateScalar(v)).ToScalar();
                mx = Simd.MaxScalar(Vector128.CreateScalar(mx), Vector128.CreateScalar(v)).ToScalar();
            }
        }
        return (mn, mx);
    }

    /// <summary>
    /// The depth fetch of the tile lambda: the depth-cache getter with the dims of the tile's level, complete.
    /// `scale = (image.W / dims.W, image.H / dims.H)` in double. Both exactly 1.0 → a plain view of
    /// `rect ∩ image.rect`. Otherwise (every DOFCache level: the depth cache is at half the pipeline canvas, the levels are the module
    /// frame and its halves) the resample runs in INVERSE depth:
    /// <list type="number">
    /// <item>source window `[max(⌊x0·sx⌋, 0), min(⌈x1·sx⌉, image.W))` (floor and ceiling in double precision), offset `((float)x0 + 0.5f)·sx − srcX0`, the window
    /// then clamped to the image rect;</item>
    /// <item>a reciprocal pass (window, 1.0f) — `tmp = 1.0f / window` into a fresh image: per row the first `(w / 32)·32` pixels through
    /// `rcpps` + one Newton step `((1 − x·r)·r + r)·1.0f`, the rest by true division;</item>
    /// <item>`ImageResample&lt;0, float&gt;` (16.16 nearest) of `tmp` to `rect.W × rect.H`, clamped to `tmp`'s own bounds;</item>
    /// <item>the same reciprocal pass (out, 1.0f) in place — source and destination alias, so every pixel takes the true-division path:
    /// `out = 1.0f / out`.</item>
    /// </list>
    /// The round trip is not the identity in the last bit, so the fetched depth differs from the cache pixels it samples.
    /// </summary>
    public static DepthImage FetchDepth(DepthImage img, RectI rect, (int W, int H) dims) => DepthImageCache.Fetch(img, rect, dims);

    /// <summary>
    /// The lambda's geometry for one tile, up to (not including) the fetches. <paramref name="depth"/> is the depth-cache image
    /// (`DepthImageCache.Current`), <paramref name="depthLevelDims"/> the DepthCache's own level list (the same list as
    /// the tile cache's), <paramref name="device"/> = <see cref="DofModel.DeviceType"/>.
    /// </summary>
    public static DofTileGeometry Tile(DofTileGrid grid, DofState state, DepthImage depth, (int W, int H)[] depthLevelDims, int level, int tx, int ty, int device, DepthSummary? summary = null)
    {
        var (tw, th) = grid.TileSize; var (lw, lh) = grid.LevelDims[level];
        var (w, h) = grid.TileDims(level, tx, ty);
        int x0 = tx * tw, y0 = ty * th;
        // the 3×3-tile window around the tile origin, clamped to the level
        var window = new RectI(Math.Max(x0 - tw, 0), Math.Max(y0 - th, 0), Math.Min(x0 - tw + 3 * tw, lw), Math.Min(y0 - th + 3 * th, lh));
        var (dmin, dmax) = DepthRange(depth, depthLevelDims[level], window, summary);
        var c = state.Constants;
        float pl = LevelPitch(c, lw);
        float nsyn = SyntheticAperture(c, state.FNumber);
        int right = x0 + w, bottom = y0 + h;
        int m = Margin(dmin, dmax, c.FocalLength, pl, nsyn, state.FocusDepth, device);
        // the far-side margins use max(2m, 0) >> 1 (logical), the near-side ones the raw m
        int m2 = unchecked(m * 2); int mpos = (int)((uint)(m2 >= 0 ? m2 : 0) >> 1);
        int ml = x0 <= m ? x0 : m, mt = y0 <= m ? y0 : m;
        int mr = lw - right; if (mr > mpos) mr = mpos;
        int mb = lh - bottom; if (mb > mpos) mb = mpos;
        var blur = new RectI(x0 - ml, y0 - mt, right + mr, bottom + mb);
        var tileInBlur = new RectI(ml, mt, ml + w, mt + h);
        // a ring of up to 10 px, clamped to the level
        int rl = blur.X0 < 11 ? blur.X0 : FetchRing, rt = blur.Y0 < 11 ? blur.Y0 : FetchRing;
        int rr = lw - blur.X1; if (rr >= 11) rr = FetchRing;
        int rb = lh - blur.Y1; if (rb >= 11) rb = FetchRing;
        var fetch = new RectI(blur.X0 - rl, blur.Y0 - rt, blur.X1 + rr, blur.Y1 + rb);
        var crop = new RectI(rl, rt, rl - blur.X0 + blur.X1, rt - blur.Y0 + blur.Y1);
        var (near, far) = DofModel.InFocusRange(state.FocusDepth, c.FocalLength, c.PixelPitch, BuilderRangeFNumber, BuilderRangeBlur);
        return new DofTileGeometry(level, tx, ty, new RectI(x0, y0, right, bottom), window, dmin, dmax, pl, nsyn, m, blur, tileInBlur, fetch, crop,
            near, far, state.FocusDepth, c.FocalLength, c.FocalLength35, device);
    }
}

/// <summary>
/// Block minima and maxima of an immutable depth image, for <see cref="DofGeometry.DepthRange"/>: the range of a rect is the
/// minimum / maximum over the blocks inside it and an exact scan of the partial blocks at its border. Used only when the image holds no NaN and
/// no negative zero (<see cref="Clean"/>): then the minimum and maximum of a set of floats do not depend on the order they are compared in, so
/// the result is bit for bit the one of Lumen's own scan.
/// </summary>
public sealed class DepthSummary
{
    public const int Block = 16;
    public readonly DepthImage Image;
    public readonly bool Clean;
    readonly int _bw, _bh;
    readonly float[] _min, _max;

    public DepthSummary(DepthImage img)
    {
        Image = img; _bw = (img.W + Block - 1) / Block; _bh = (img.H + Block - 1) / Block;
        _min = new float[_bw * _bh]; _max = new float[_bw * _bh];
        int dirty = 0;
        Parallel.For(0, _bh, by =>
        {
            int y0 = by * Block, y1 = Math.Min(img.H, y0 + Block); bool bad = false;
            var negZero = System.Runtime.Intrinsics.Vector256.Create(unchecked((int)0x80000000));
            for (int bx = 0; bx < _bw; bx++)
            {
                int x0 = bx * Block, x1 = Math.Min(img.W, x0 + Block);
                float mn = float.MaxValue, mx = -float.MaxValue;
                if (x1 - x0 == Block && System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
                {
                    // A full block eight lanes at a time. The summary is only used when the whole image is clean (no NaN, no −0), and
                    // then a minimum / maximum does not depend on the order it is taken in; NaN and −0 are detected per lane
                    var vmn = System.Runtime.Intrinsics.Vector256.Create(float.MaxValue); var vmx = System.Runtime.Intrinsics.Vector256.Create(-float.MaxValue);
                    var badv = System.Runtime.Intrinsics.Vector256<int>.Zero;
                    for (int y = y0; y < y1; y++)
                    {
                        long row = img.Offset + (long)y * img.Stride + x0;
                        for (int k = 0; k < Block; k += 8)
                        {
                            var v = System.Runtime.Intrinsics.Vector256.Create(img.Data.AsSpan((int)(row + k), 8));
                            badv |= ~System.Runtime.Intrinsics.Vector256.Equals(v, v).AsInt32() | System.Runtime.Intrinsics.Vector256.Equals(v.AsInt32(), negZero);
                            vmn = System.Runtime.Intrinsics.Vector256.Min(vmn, v); vmx = System.Runtime.Intrinsics.Vector256.Max(vmx, v);
                        }
                    }
                    if (badv != System.Runtime.Intrinsics.Vector256<int>.Zero) bad = true;
                    for (int k = 0; k < 8; k++) { float a = vmn.GetElement(k), b = vmx.GetElement(k); if (a < mn) mn = a; if (b > mx) mx = b; }
                }
                else
                for (int y = y0; y < y1; y++)
                {
                    long row = img.Offset + (long)y * img.Stride;
                    for (int x = x0; x < x1; x++)
                    {
                        float v = img.Data[row + x];
                        if (float.IsNaN(v) || (v == 0f && float.IsNegative(v))) bad = true;
                        if (v < mn) mn = v; if (v > mx) mx = v;
                    }
                }
                _min[by * _bw + bx] = mn; _max[by * _bw + bx] = mx;
            }
            if (bad) Interlocked.Exchange(ref dirty, 1);
        });
        Clean = dirty == 0 && img.Rect.X0 == 0 && img.Rect.Y0 == 0 && img.Rect.X1 == img.W && img.Rect.Y1 == img.H;
    }

    /// <summary><see cref="DofGeometry.MinMaxGrown"/> of a rect of <see cref="Image"/> (only for a <see cref="Clean"/> image).</summary>
    public (float Min, float Max) MinMaxGrown(RectI rect)
    {
        var img = Image;
        int w = rect.X1 - rect.X0, h = rect.Y1 - rect.Y0;
        int gx0 = Math.Max(rect.X0 - w, 0), gy0 = Math.Max(rect.Y0 - h, 0), gx1 = Math.Min(rect.X0 - w + 3 * w, img.W), gy1 = Math.Min(rect.Y0 - h + 3 * h, img.H);
        float mn = float.MaxValue, mx = -float.MaxValue;
        if (gx1 <= gx0 || gy1 <= gy0) return (mn, mx);
        int bx0 = (gx0 + Block - 1) / Block, by0 = (gy0 + Block - 1) / Block, bx1 = gx1 / Block, by1 = gy1 / Block;   // whole blocks inside
        if (bx1 <= bx0 || by1 <= by0) { Scan(gx0, gy0, gx1, gy1, ref mn, ref mx); return (mn, mx); }
        for (int by = by0; by < by1; by++)
            for (int bx = bx0; bx < bx1; bx++) { float a = _min[by * _bw + bx], b = _max[by * _bw + bx]; if (a < mn) mn = a; if (b > mx) mx = b; }
        int ix0 = bx0 * Block, iy0 = by0 * Block, ix1 = bx1 * Block, iy1 = by1 * Block;
        Scan(gx0, gy0, gx1, iy0, ref mn, ref mx);          // above
        Scan(gx0, iy1, gx1, gy1, ref mn, ref mx);          // below
        Scan(gx0, iy0, ix0, iy1, ref mn, ref mx);          // left
        Scan(ix1, iy0, gx1, iy1, ref mn, ref mx);          // right
        return (mn, mx);
    }

    void Scan(int x0, int y0, int x1, int y1, ref float mn, ref float mx)
    {
        var img = Image;
        for (int y = y0; y < y1; y++)
        {
            long row = img.Offset + (long)y * img.Stride;
            for (int x = x0; x < x1; x++) { float v = img.Data[row + x]; if (v < mn) mn = v; if (v > mx) mx = v; }
        }
    }
}

/// <summary>The DOF path's CRT `log2f`. It forwards to <see cref="WineCrt"/>.</summary>
public static class DofMath
{
    /// <summary><see cref="WineCrt.Log2f"/>.</summary>
    public static float Log2f(float x) => WineCrt.Log2f(x);
}
