using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// `ExportImageFormat` 0 (JPEG) and 4 (JPEG + GDepth) of `RendererPrivate::exportImage` — `a-display-isp.md` §12.2.
///
/// The JPEG path is **not** the DNG path with a different writer. Where the DNG renders `vec4x32f` tiles through the
/// bare tile callback `0x180526690` (cache fetch + vignetting, no ISP), fmt 0/4 hands `renderForExport&lt;vec4x8ui&gt;`
/// (`FUN_180524320`) the **display tile generator** `renderer+0x688` = `lambda_2` `0x18049fb10`, i.e. the whole output
/// ISP of §1–§10b, and writes the result into a `vec4x8ui` view of the display pyramid. Per tile:
/// <code>
///   grow the export-level tile rect by the halo 64 inside the PIPELINE level dims  (DisplayRender.Geometry)
///   fetch the fused tile from the PipelineCache                                    (FUN_1804bd710)
///   run the output SoftISP over the grown tile, crop back                          (DisplayRender.Run)
///   srcView *= (255,255,255,255)                                                   (FUN_1801ea0c0, in place)
///   dstView  = ImageConvertPixelType&lt;vec4x8ui, vec4x32f&gt;(srcView)                  (1804bb290 → 18004dcb0)
/// </code>
/// then the **8-bit** image is resampled to the requested size with the same two branches as the DNG
/// (`scale &gt; 1.5` → Lanczos-2 blur + bilinear `ImageWarp&lt;1,0&gt;`, else 64-phase Catmull-Rom `ImageWarpClamped&lt;2&gt;`),
/// instantiated for `vec4x8ui`: float arithmetic throughout, `cvtps2dq`/`packssdw`/`packuswb` (round-half-to-even +
/// saturate) on every store.
///
/// Unlike the DNG the whole image is one region: `exportImage` calls `GetExportTransformOutput` once for
/// `rect = (0,0,W,H)` and `lambda_3` allocates a single destination (`FUN_1805290f0` L242–330), so there are no
/// 2048² writer blocks here.
/// </summary>
public sealed class JpegExportRenderer
{
    readonly PipelineCache _cache; readonly ExportLevels _lv; readonly ExportTransform _tr;
    readonly (int W, int H) _size; readonly bool _force0;
    readonly Func<int, (SoftIsp Isp, IspStats Stats)> _ispOfLevel; readonly CapturedFrame _frame;

    public Action<string>? Log;
    /// <summary>Diagnostic knob `LUX_JPEG_ROUND=rne`: use the §12.1 *display* store (`cvtps2dq`, round-half-to-even)
    /// where the export path uses `ImageConvertPixelType` (round-half-away-from-zero). Only for showing that the two
    /// are distinguishable on real data — the export is always the half-away one.</summary>
    public static readonly bool UseDisplayRounding = Environment.GetEnvironmentVariable("LUX_JPEG_ROUND") == "rne";
    /// <summary>Diagnostic: the cropped float display tile of each render tile, before the ×255 / 8-bit convert.</summary>
    public Action<int, RectI, Image<Vec4F>>? TileHook;
    /// <summary>Diagnostic: ("r8in", the gathered 8-bit source) and ("r8out", the resampled 8-bit result).</summary>
    public Action<string, int, int, byte[]>? StageHook;

    public JpegExportRenderer(PipelineCache cache, ExportLevels levels, ExportTransform transform, (int W, int H) size,
                              bool forceLevel0, Func<int, (SoftIsp, IspStats)> ispOfLevel, CapturedFrame frame)
    {
        _cache = cache; _lv = levels; _tr = transform; _size = size; _force0 = forceLevel0; _ispOfLevel = ispOfLevel; _frame = frame;
    }

    /// <summary>The single `GetExportTransformOutput` this export makes (`exportImage` and `lambda_4` agree — one
    /// region, no 2048² blocks). fmt 4's depth map is warped with the very same object.</summary>
    public TransformOutput Transform => ExportTransformOutput.Compute(_tr, _size, new RectI(0, 0, _size.W, _size.H), _lv.ExportDims, _force0);

    /// <summary>`FUN_1805290f0` L242–441 for `(fmt | 4) == 4`: render the whole ROI as RGBA8, then resample to
    /// <c>size</c>. The result is `size.W · size.H · 4` bytes, row-major RGBA.</summary>
    public byte[] Render()
    {
        if (_size.W < 1 || _size.H < 1 || _size.W > 99999 || _size.H > 99999) throw new InvalidOperationException("Invalid export size!");
        var rect = new RectI(0, 0, _size.W, _size.H);
        var to = ExportTransformOutput.Compute(_tr, _size, rect, _lv.ExportDims, _force0);
        Log?.Invoke($"jpeg export: size {_size.W}x{_size.H} -> level {to.Level} src ({to.Source.X0},{to.Source.Y0},{to.Source.X1},{to.Source.Y1}) scale ({to.ScaleX:R},{to.ScaleY:R}) affine [{to.A:R} {to.B:R} {to.C:R} | {to.D:R} {to.E:R} {to.F:R}]");
        var src = RenderSource(to.Level, to.Source);
        int sw = to.Source.Width, sh = to.Source.Height;
        StageHook?.Invoke("r8in", sw, sh, src);
        byte[] outp;
        if (1.5f < to.ScaleX || 1.5f < to.ScaleY)   // DAT_180687524
        {
            int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);   // DAT_1806ef740
            var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
            var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
            // ImageConvSeparable2D<vec4x8ui> (1800bd6b0): u8 -> float, float intermediate (loadImage bpp 0x10), u8 store
            var blurred = ToU8(ExportResample.ConvSeparable(ToFloat(src), sw, sh, kernelX, kernelY));
            outp = WarpBilinearU8(blurred, sw, sh, _size.W, _size.H, to);
        }
        else outp = WarpClamped2U8(src, sw, sh, _size.W, _size.H, to);
        StageHook?.Invoke("r8out", _size.W, _size.H, outp);
        return outp;
    }

    /// <summary>`renderForExport&lt;vec4x8ui&gt;` `FUN_180524320`: every 512-grid tile of the export level that overlaps
    /// <paramref name="src"/> is produced whole by the display tile generator, scaled by 255 and converted to RGBA8;
    /// the overlap with <paramref name="src"/> is copied into the destination.</summary>
    public byte[] RenderSource(int level, RectI src)
    {
        var (W, H) = _lv.ExportDims[level];
        if (src.X0 < 0 || src.Y0 < 0 || src.X1 > W || src.Y1 > H) throw new ArgumentException("export source rect out of bounds");
        int nx = ExportLevels.GridCount(W), ny = ExportLevels.GridCount(H);
        int tx0 = Math.Min(src.X0 / ExportLevels.Tile, nx - 1), tx1 = Math.Min((src.X1 - 1) / ExportLevels.Tile, nx - 1);
        int ty0 = Math.Min(src.Y0 / ExportLevels.Tile, ny - 1), ty1 = Math.Min((src.Y1 - 1) / ExportLevels.Tile, ny - 1);
        var dst = new byte[(long)src.Width * src.Height * 4];
        var (isp, stats) = _ispOfLevel(level);
        var pd = _lv.PipelineDims[level];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = _lv.TileRect(level, tx, ty);
                var geo = DisplayRender.Geometry(tile, pd, _lv.Origins[level], _lv.CacheDims, level);
                var px = _cache.Render(_lv.BaseLevel + geo.CacheLevel, geo.Grown, pd);       // FUN_1804bd710
                var grown = new Image<Vec4F>(geo.Grown.Width, geo.Grown.Height);
                System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(grown.Data);
                var img = DisplayRender.Run(isp, _frame, stats, grown, geo, level);          // the whole output ISP + lambda_2's crop
                TileHook?.Invoke(level, tile, img);
                Log?.Invoke($"  jpeg tile L{level} ({tx},{ty}) export ({tile.X0},{tile.Y0},{tile.X1},{tile.Y1}) grown ({geo.Grown.X0},{geo.Grown.Y0},{geo.Grown.X1},{geo.Grown.Y1}) float ({geo.Float.X0:R} {geo.Float.Y0:R} {geo.Float.X1:R} {geo.Float.Y1:R})");
                var c = tile.Intersect(src);
                if (c.IsEmpty) throw new InvalidOperationException("renderForExport: Unxpected TileUpdate with no overlap!");
                for (int y = c.Y0; y < c.Y1; y++)
                {
                    var row = img.Row(y - tile.Y0);
                    long o = ((long)(y - src.Y0) * src.Width + (c.X0 - src.X0)) * 4;
                    for (int x = c.X0; x < c.X1; x++)
                    {
                        var v = row[x - tile.X0];
                        if (UseDisplayRounding)
                        {   // diagnostic only (LUX_JPEG_ROUND=rne): the §12.1 display store, to show that the two
                            // converters are distinguishable on real data and that the JPEG uses the §12.2 one.
                            dst[o++] = DisplayOutput.DisplayByte(v.R * 255f); dst[o++] = DisplayOutput.DisplayByte(v.G * 255f);
                            dst[o++] = DisplayOutput.DisplayByte(v.B * 255f); dst[o++] = DisplayOutput.DisplayByte(v.A * 255f);
                        }
                        else
                        {
                            dst[o++] = DisplayOutput.ExportByte(v.R * 255f); dst[o++] = DisplayOutput.ExportByte(v.G * 255f);
                            dst[o++] = DisplayOutput.ExportByte(v.B * 255f); dst[o++] = DisplayOutput.ExportByte(v.A * 255f);
                        }
                    }
                }
            }
        return dst;
    }

    static float[] ToFloat(byte[] b) { var f = new float[b.Length]; for (int i = 0; i < b.Length; i++) f[i] = b[i]; return f; }

    static readonly float[] WarpTable = Lux.Engine.Pipeline.Geometry.WarpResample.BuildTable();

    /// <summary>`ImageWarpClamped&lt;2, vec4x8ui&gt;` (`18052f580`, tile lambda `18052fc00`): identical geometry and kernel to
    /// the `vec4x32f` instantiation — `p = (int)((map − 1)·64)`, 64-phase Catmull-Rom, clamped 4×4 gather at the border,
    /// zero fill outside, `max(−0.25·P, N) + P` recombination — with the samples widened from u8 and the result stored
    /// through `cvtps2dq`/`packssdw`/`packuswb`. Kept byte-in/byte-out so a full-size (8320×6240) export does not need
    /// two 830 MB float buffers.</summary>
    public static byte[] WarpClamped2U8(byte[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new byte[(long)dw * dh * 4];
        var res = new float[4];
        Span<float> block = stackalloc float[64];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)((u + -1f) * 64f), py = (int)((v + -1f) * 64f);
                int ix = px >> 6, iy = py >> 6;
                long o = ((long)y * dw + x) * 4;
                if (!(ix < sw && 0 < ix + 4 && iy < sh && 0 < iy + 4)) continue;   // zero fill
                for (int r = 0; r < 4; r++)
                {
                    int cy = Math.Clamp(iy + r, 0, sh - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int cx = Math.Clamp(ix + c, 0, sw - 1);
                        long si = ((long)cy * sw + cx) * 4; int bi = (r * 4 + c) * 4;
                        block[bi] = src[si]; block[bi + 1] = src[si + 1]; block[bi + 2] = src[si + 2]; block[bi + 3] = src[si + 3];
                    }
                }
                Lux.Engine.Pipeline.Geometry.WarpResample.Resample(block, 4, 0, WarpTable, px & 63, py & 63, res, 0);
                dst[o] = DisplayOutput.DisplayByte(res[0]); dst[o + 1] = DisplayOutput.DisplayByte(res[1]);
                dst[o + 2] = DisplayOutput.DisplayByte(res[2]); dst[o + 3] = DisplayOutput.DisplayByte(res[3]);
            }
        return dst;
    }

    /// <summary>`ImageWarp&lt;1, 0, vec4x8ui&gt;` (`1805304e0`, lambda `180530a50`): the bilinear half of the `scale &gt; 1.5`
    /// branch — `p = (int)(map·64)`, weights `(1 − i/64, i/64)`, clamped 2×2 at the border, zero fill.</summary>
    public static byte[] WarpBilinearU8(byte[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new byte[(long)dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)(u * 64f), py = (int)(v * 64f);
                int ix = px >> 6, iy = py >> 6;
                long o = ((long)y * dw + x) * 4;
                int x0, x1, y0, y1;
                if (ix < 0 || sw - 2 < ix || iy < 0 || sh - 2 < iy)
                {
                    if (!(ix < sw && 0 < ix + 2 && iy < sh && 0 < iy + 2)) continue;
                    x0 = Math.Clamp(ix, 0, sw - 1); x1 = Math.Clamp(ix + 1, 0, sw - 1);
                    y0 = Math.Clamp(iy, 0, sh - 1); y1 = Math.Clamp(iy + 1, 0, sh - 1);
                }
                else { x0 = ix; x1 = ix + 1; y0 = iy; y1 = iy + 1; }
                float ty = (float)(py & 63) * 0.015625f, wy0 = 1f - ty, wy1 = ty;
                float tx = (float)(px & 63) * 0.015625f, wx0 = 1f - tx, wx1 = tx;
                long p00 = ((long)y0 * sw + x0) * 4, p10 = ((long)y0 * sw + x1) * 4, p01 = ((long)y1 * sw + x0) * 4, p11 = ((long)y1 * sw + x1) * 4;
                for (int c = 0; c < 4; c++)
                    dst[o + c] = DisplayOutput.DisplayByte((wy1 * src[p11 + c] + wy0 * src[p10 + c]) * wx1 + (wy1 * src[p01 + c] + wy0 * src[p00 + c]) * wx0);
            }
        return dst;
    }

    /// <summary>`cvtps2dq` + `packssdw` + `packuswb` — round-half-to-even then saturate, the store every `vec4x8ui`
    /// image expression uses (§12.1, and the two u8 resamplers of §12.2).</summary>
    static byte[] ToU8(float[] f) { var b = new byte[f.Length]; for (int i = 0; i < f.Length; i++) b[i] = DisplayOutput.DisplayByte(f[i]); return b; }
}
