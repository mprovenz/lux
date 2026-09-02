namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// The display TileCache generator `RendererPrivate::RendererPrivate::lambda_2` `0x18049fb10` (mode 0): grow the
/// requested level tile by the per-level halo (**always 64** — `setInputDataStream` pushes the literal `0x40` for
/// every level, so 32 px left/top and 31 px right/bottom), fetch the fused tile from the PipelineCache at
/// `level + FUN_18050c640(profile)`, run the output SoftISP **in place** over the whole grown tile, then crop back to
/// the requested rect. The tile stays `vec4x32f`; the 8-bit conversion happens later (§12).
/// Spec `a-display-isp.md` §1.1.
/// </summary>
public static class DisplayRender
{
    public const int Halo = 64;   // renderer+0x2f8[L], literal 0x40 for every level

    /// <summary>The geometry of one display tile.</summary>
    /// <param name="Abs">the requested rect in level coordinates with the level origin added (`(ox,oy,ox,oy) + tileRect`, a `paddd`)</param>
    /// <param name="Grown">`Abs` grown by `gl/gt/gr/gb`</param>
    /// <param name="Float">`(sx·grown.x0, sy·grown.y0, sx·grown.x1, sy·grown.y1)` with `sx = cacheW/levelW` (a single `divss`)</param>
    public readonly record struct TileGeometry(RectI Abs, RectI Grown, int GrowL, int GrowT, int GrowR, int GrowB, RectF Float, int CacheLevel);

    /// <summary>`lambda_2` L1–L20. <paramref name="tileRect"/> is level-local; the level origin is added here.</summary>
    public static TileGeometry Geometry(RectI tileRect, (int W, int H) levelDims, (int X, int Y) levelOrigin,
                                        (int W, int H) cacheDims, int level, int profileOffset = DisplayIspTuning.L16ProfileOffset, int halo = Halo)
    {
        int W = levelDims.W, H = levelDims.H;
        int x0 = levelOrigin.X + tileRect.X0, y0 = levelOrigin.Y + tileRect.Y0;
        int x1 = levelOrigin.X + tileRect.X1, y1 = levelOrigin.Y + tileRect.Y1;
        int half = halo >> 1, hm = Math.Max(halo - 1, 0) >> 1;
        int gl = Math.Min(x0, half), gt = Math.Min(y0, half);
        int gr = Math.Min(W - x1, hm), gb = Math.Min(H - y1, hm);
        var grown = new RectI(x0 - gl, y0 - gt, x1 + gr, y1 + gb);
        float sx = (float)cacheDims.W / (float)W, sy = (float)cacheDims.H / (float)H;   // divss, no reciprocal
        var fr = new RectF(sx * grown.X0, sy * grown.Y0, sx * grown.X1, sy * grown.Y1);
        return new TileGeometry(new RectI(x0, y0, x1, y1), grown, gl, gt, gr, gb, fr, level + profileOffset);
    }

    /// <summary>
    /// Run the output ISP on an already-fetched fused tile and crop back, i.e. `FUN_1803dd0e0` + the tail of `lambda_2`.
    /// <paramref name="tile"/> must cover <see cref="TileGeometry.Grown"/> (`FUN_1804bd710` hands the ISP an image whose
    /// rect is `(0,0,w,h)` — no halo beyond the grown region), and it is processed **in place** in Lumen; here the stage
    /// list produces a new image, which is then cropped exactly as `lambda_2` does.
    /// </summary>
    public static Image<Vec4F> Run(SoftIsp isp, CapturedFrame refFrame, IspStats stats, Image<Vec4F> tile, TileGeometry g,
                                   int level, Action<string>? log = null, Action<int, IStage, IspPayload>? afterStage = null)
    {
        var outImg = isp.ProcessColorFloat(refFrame, stats, tile, null, g.Float, level, log, roi: tile.Rect, afterStage: afterStage);
        // lambda_2 L21–24: crop the result back to the requested tile inside the grown region
        var r = outImg.Rect;
        int ix0 = Math.Max(r.X0, g.GrowL), iy0 = Math.Max(r.Y0, g.GrowT);
        int ix1 = Math.Min(r.X1, g.Abs.Width + g.GrowL), iy1 = Math.Min(r.Y1, g.Abs.Height + g.GrowT);
        if (ix1 <= ix0 || iy1 <= iy0) throw new InvalidOperationException("display tile crop is empty");
        return outImg.View(new RectI(ix0, iy0, ix1, iy1));
    }
}
