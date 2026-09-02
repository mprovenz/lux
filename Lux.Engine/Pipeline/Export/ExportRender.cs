using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp.Stages;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// The `CIAPI::Transform::impl` fields `GetExportTransformOutput` (`180521d90`) reads: the 3×3 matrix (+0x10, cp.dll
/// storage order — `x' = m0·x + m3·y + m6`), the reduced aspect ratio (+0x8/+0xc) and the normalised crop rect (+0x34).
/// `FUN_180501380` converts the matrix "to a size": elements 6/7 are scaled by `W/aspectW` (throws when the size's reduced
/// aspect differs). The L16 default (cp.dll's export path) is the identity, crop (0,0,1,1), aspect 4:3.
/// </summary>
public sealed class ExportTransform
{
    public float[] Matrix = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
    public int AspectW = 4, AspectH = 3;
    public float CropX0, CropY0, CropX1 = 1f, CropY1 = 1f;

    public static ExportTransform Identity((int W, int H) dims0)
    {
        int g = Gcd(dims0.W, dims0.H);
        return new ExportTransform { AspectW = dims0.W / g, AspectH = dims0.H / g };
    }

    /// <summary>The rotation Lumen bakes into the DNG (`Orientation` stays 1). The matrix is inferred, not observed: cp.dll was only ever driven with the
    /// identity Transform, so this is the matrix that makes `GetExportTransformOutput`'s affine come out as the exact 90°/180°/270° remap of the
    /// level-0 export window — for 90° CW `u = y`, `v = H0 − x` (the mapping recovered from Lumen's own portrait export of L16_00466,
    /// `portrait[Y][X] = landscape[H0 − X][Y]`). `m6/m7` are stored pre-scaled by `aspectW/W` because `MatrixForSize` multiplies them by `W/aspectW`.</summary>
    public static ExportTransform Rotate(int degrees, (int W, int H) dims0)
    {
        var t = Identity(dims0);
        // size-independent: the affine is n·scale and scaleX = W0/sizeX, scaleY = H0/sizeY, so n1 = W0/H0, n3 = −H0/W0 give B = 1, D = −1 at the
        // native rotated size (sizeX = H0, sizeY = W0) and B = D = 1/k for a k-times-smaller request.
        float sx = (float)dims0.H / (float)dims0.W, sy = (float)dims0.W / (float)dims0.H;
        float s = (float)dims0.W / (float)t.AspectW;
        switch (((degrees % 360) + 360) % 360)
        {
            case 0: return t;
            case 90:  t.Matrix = new[] { 0f, -sx, 0f, sy, 0f, 0f, 0f, (float)dims0.H / s, 1f }; break;   // u = y, v = H0 − x
            case 180: t.Matrix = new[] { -1f, 0f, 0f, 0f, -1f, 0f, (float)dims0.W / s, (float)dims0.H / s, 1f }; break;
            case 270: t.Matrix = new[] { 0f, sx, 0f, -sy, 0f, 0f, (float)dims0.W / s, 0f, 1f }; break;   // u = W0 − y, v = x
            default: throw new ArgumentException("rotation must be a multiple of 90");
        }
        return t;
    }
    internal static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }   // FUN_18048aad0

    /// <summary>`FUN_180501380(impl, out, size)`: the 9 floats with [6],[7] × (W/aspectW).</summary>
    public float[] MatrixForSize((int W, int H) size)
    {
        int aw = 0, ah = 0;
        if (size.W != 0 && size.H != 0) { int g = Gcd(size.W, size.H); aw = size.W / g; ah = size.H / g; }
        if (aw != AspectW || ah != AspectH) throw new InvalidOperationException("Cannot convert matrix to a size of different aspect ratio!");
        float s = (float)size.W / (float)aw;
        var m = (float[])Matrix.Clone();
        m[6] = Matrix[6] * s; m[7] = Matrix[7] * s;
        return m;
    }
}

/// <summary>`lt::TransformOutput` (`GetExportTransformOutput` L196–216): level, source rect at that level (±2 px, clamped), per-axis
/// scale (source px per output px) and the affine lambda_5 `x' = B·y + A·x + C`, `y' = E·y + D·x + F` (output-rect-local → source-rect-local).</summary>
public readonly record struct TransformOutput(int Level, RectI Source, float ScaleX, float ScaleY, float A, float B, float C, float D, float E, float F)
{
    public (float U, float V) Map(float x, float y) => (B * y + A * x + C, y * E + x * D + F);   // lambda_5 180523880
}

/// <summary>The renderer's per-level export geometry (`setInputDataStream` L~360–430, verified against cp.dll on L16_00466): `+0x270` export dims
/// (8320×6240, halved per level), `+0x288` pipeline dims (the 10432×7824 ResAmp canvas, halved), `+0x2d0` level origins (the export window
/// inside the canvas, (1008,832) at level 0), `+0x490` = the reference ImageCaches dims (4160×3120 = the module frame; the vignetting crop rect
/// and frame are in these units) and the cache base level
/// (`FUN_18050c640(+0x90)`, 0 on the L16). The 512-grid render tiles (`+0x268` = (512,512), grid rule of L~430) are over the export dims.</summary>
public sealed record ExportLevels((int W, int H)[] ExportDims, (int W, int H)[] PipelineDims, (int X, int Y)[] Origins, (int W, int H) CacheDims, int BaseLevel)
{
    public const int Tile = 512;   // DAT_1808378b0 = (0x200, 0x200)

    /// <summary>Renderer tile grid of a level (`setInputDataStream` L432–456): `n = ceil(W/512)`, minus one when `n > 1` and the last tile would be partial.</summary>
    public static int GridCount(int w)
    {
        int n = w / Tile; if (w % Tile != 0) n += 1;
        if (n > 1 && w < Tile * n) n -= 1;
        return n;
    }

    /// <summary>The tile rect (export-level coords) of tile (tx,ty): 512-aligned, the last tile absorbs the remainder (render thread `1804a37f0` L~860).</summary>
    public RectI TileRect(int level, int tx, int ty)
    {
        var (W, H) = ExportDims[level]; int nx = GridCount(W), ny = GridCount(H);
        int x1 = tx == nx - 1 ? W : (tx + 1) * Tile, y1 = ty == ny - 1 ? H : (ty + 1) * Tile;
        return new RectI(tx * Tile, ty * Tile, x1, y1);
    }

    /// <summary>The L16 layout: export dims = the sensor dims halved per level; pipeline dims = the canvas halved; origins halved (cp.dll's values at
    /// level 0, `(o >> 1)` per level as cp.dll shows (1008,832) → (504,416) → (252,208) → (126,104) → (63,52)).</summary>
    public static ExportLevels L16((int W, int H) sensor, (int W, int H) canvas, (int X, int Y) origin0, int levels = 5)
    {
        var ed = new (int, int)[levels]; var pd = new (int, int)[levels]; var og = new (int, int)[levels];
        for (int l = 0; l < levels; l++) { ed[l] = (sensor.W >> l, sensor.H >> l); pd[l] = (canvas.W >> l, canvas.H >> l); og[l] = (origin0.X >> l, origin0.Y >> l); }
        return new ExportLevels(ed, pd, og, sensor, 0);
    }
}

public static class ExportTransformOutput
{
    const float LevelSlack = 1.1f;   // DAT_1806a0800

    /// <summary>`lt::A::GetExportTransformOutput(out, transform, size, rect, levelDims (+0x270 copy), force0 = renderer+0x500)` — `180521d90`, float-op order as compiled.</summary>
    public static TransformOutput Compute(ExportTransform t, (int W, int H) size, RectI rect, (int W, int H)[] dims, bool forceLevel0)
    {
        var m = t.MatrixForSize(dims[0]);
        float tx = (float)dims[0].W * t.CropX0, ty = (float)dims[0].H * t.CropY0;
        // L~120: the matrix re-based on the crop origin (initializer-list ctor FUN_18001b690)
        float n0 = m[0], n1 = m[3], n2 = m[3] * ty + tx * m[0] + m[6];
        float n3 = m[1], n4 = m[4], n5 = ty * m[4] + m[1] * tx + m[7];
        float cropW = t.CropX1 - t.CropX0, cropH = t.CropY1 - t.CropY0;
        float w0 = (float)dims[0].W, h0 = (float)dims[0].H;
        float sizeX = (float)size.W, sizeY = (float)size.H;
        float reqX = sizeX / (cropW * w0);
        if (!(0f < reqX)) throw new InvalidOperationException("Export Transform must have a positive scale!");
        float reqY = sizeY / (cropH * h0);
        if (!(0f < reqY)) throw new InvalidOperationException("Export Transform must have a positive scale!");
        int n = dims.Length, level = -1;
        if (n > 0)
        {
            float cw = cropW * LevelSlack, ch = cropH * LevelSlack;
            if (sizeX <= cw * w0 && sizeY <= ch * h0)
            {
                level = 0;
                for (int j = 1; j < n; j++)
                {
                    if ((float)dims[j].W * cw < sizeX) break;
                    if ((float)dims[j].H * ch < sizeY) break;
                    level = j;
                }
            }
        }
        if (level < 0) level = 0;
        if (n - 1 < level) level = n - 1;
        if (forceLevel0) level = 0;
        float lw = (float)dims[level].W, lh = (float)dims[level].H;
        float offX = (lw / w0) * n2, offY = (lh / h0) * n5;
        float scaleX = (float)(int)(((lw / w0) * sizeX) / reqX) / sizeX;
        float scaleY = (float)(int)(((lh / h0) * sizeY) / reqY) / sizeY;
        float x0f = (float)rect.X0, y0f = (float)rect.Y0;
        // the four transformed corners (L~150–166)
        float ysc = scaleY * y0f;
        float p1x = n1 * ysc;
        float p0x = n0 * scaleX * x0f + offX;
        float p0y = scaleX * x0f * n3 + offY;
        float p1y = ysc * n4;
        float c0x = p0x + p1x, c0y = p0y + p1y;
        float ysc1 = ((float)(rect.Y1 - rect.Y0) + y0f) * scaleY;
        float q1x = n1 * ysc1, q1y = n4 * ysc1;
        float c1x = p0x + q1x, c1y = p0y + q1y;
        float xsc1 = ((float)(rect.X1 - rect.X0) + x0f) * scaleX;
        float r0x = n0 * xsc1 + offX, r0y = n3 * xsc1 + offY;
        float c2x = p1x + r0x, c2y = p1y + r0y;
        float c3x = r0x + q1x, c3y = r0y + q1y;
        float minx = c0x, miny = c0y, maxx = c0x, maxy = c0y;
        foreach (var (px, py) in new[] { (c1x, c1y), (c2x, c2y), (c3x, c3y) })
        {
            if (px <= minx) minx = px; if (py <= miny) miny = py;
            if (maxx <= px) maxx = px; if (maxy <= py) maxy = py;
        }
        int sx0 = (int)MathF.Floor(minx) - 2, sy0 = (int)MathF.Floor(miny) - 2, sx1 = (int)MathF.Ceiling(maxx) + 2, sy1 = (int)MathF.Ceiling(maxy) + 2;
        if (sx0 < 0) sx0 = 0; if (sy0 < 0) sy0 = 0;
        if (dims[level].W < sx1) sx1 = dims[level].W; if (dims[level].H < sy1) sy1 = dims[level].H;
        float a = n0 * scaleX, d = n3 * scaleX, b = n1 * scaleY, e = n4 * scaleY;
        float c = b * (float)rect.Y0 + a * (float)rect.X0 + (offX - (float)sx0);
        float f = e * (float)rect.Y0 + d * (float)rect.X0 + (offY - (float)sy0);
        return new TransformOutput(level, new RectI(sx0, sy0, sx1, sy1), scaleX, scaleY, a, b, c, d, e, f);
    }
}

/// <summary>
/// The export render path (SoT §7.3): per writer block → `GetExportTransformOutput` → `renderForExport` (`1805253c0`: render-thread 512 tiles
/// through the export tile callback `180526690` = `FUN_1804bd710` on the PipelineCache (mode 0, all-in-focus) + `RemoveVignettingGeneric&lt;vec4x32f,1&gt;`)
/// → `FUN_18052dfc0` (Lanczos-2 blur + `ImageWarp&lt;1,0&gt;` when a scale exceeds 1.5, else `ImageWarpClamped&lt;2&gt;`) → `FUN_18001b790` ×16384.
/// </summary>
public sealed class ExportRenderer
{
    readonly PipelineCache _cache; readonly ExportLevels _lv; readonly ExportTransform _tr; readonly (int W, int H) _size; readonly bool _force0;
    readonly Func<int, float> _multiplier; readonly int _cols, _rows; readonly float[] _grid;
    public Action<string>? Log;
    /// <summary>Diagnostic hook: every vignetted tile (export-level rect, shifted pipeline rect, float RGBA) — cp.dll's `t&lt;k&gt;_post` dump.</summary>
    public Action<int, RectI, RectI, float[]>? TileHook;
    /// <summary>Diagnostic hook: every cache tile BEFORE `RemoveVignettingGeneric` — cp.dll's `t&lt;k&gt;_pre` dump.</summary>
    public Action<int, RectI, RectI, float[]>? TilePreHook;
    /// <summary>Diagnostic hook per writer block: ("rsin", source image) before `FUN_18052dfc0`, ("rsout", warped) before the ×16384 and
    /// ("x16384", final) — cp.dll's `b&lt;k&gt;_rsin` / `_rsout` / `_x16384`.</summary>
    public Action<string, RectI, int, int, float[]>? BlockHook;

    /// <param name="multiplierOfLevel">`lens_shading.multiplier` of the level's tuning (`exportImage` lambda_2 L80–82 reads tuning[0]).</param>
    /// <param name="vignetting">the reference module's vignetting model grid (before the multiplier transform).</param>
    public ExportRenderer(PipelineCache cache, ExportLevels levels, ExportTransform transform, (int W, int H) size, bool forceLevel0,
                          Func<int, float> multiplierOfLevel, (int Cols, int Rows, float[] Data) vignetting)
    {
        _cache = cache; _lv = levels; _tr = transform; _size = size; _force0 = forceLevel0; _multiplier = multiplierOfLevel;
        (_cols, _rows, _grid) = vignetting;
    }

    public (int W, int H) Size => _size;

    /// <summary>`Exporter::exportDNG` lambda_0 (`1805342d0`) + `exportImage` lambda_2/lambda_3 + `FUN_18052dfc0` + `FUN_18001b790`: the writer's
    /// block (unclamped 2048² rect) → the clamped block as float RGBA ×16384 (row-major, 4 floats per pixel).</summary>
    public (RectI Rect, float[] Pixels) RenderBlock(RectI block)
    {
        var rect = new RectI(Math.Max(block.X0, 0), Math.Max(block.Y0, 0), Math.Min(block.X1, _size.W), Math.Min(block.Y1, _size.H));
        var to = ExportTransformOutput.Compute(_tr, _size, rect, _lv.ExportDims, _force0);
        Log?.Invoke($"export block ({rect.X0},{rect.Y0},{rect.X1},{rect.Y1}): level {to.Level} src ({to.Source.X0},{to.Source.Y0},{to.Source.X1},{to.Source.Y1}) scale ({to.ScaleX:R},{to.ScaleY:R}) affine [{to.A:R} {to.B:R} {to.C:R} | {to.D:R} {to.E:R} {to.F:R}]");
        var src = RenderSource(to.Level, to.Source);
        int sw = to.Source.Width, sh = to.Source.Height, dw = rect.Width, dh = rect.Height;
        BlockHook?.Invoke("rsin", rect, sw, sh, src);
        float[] outp;
        if (1.5f < to.ScaleX || 1.5f < to.ScaleY)   // DAT_180687524
        {
            int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);   // DAT_1806ef740
            var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
            var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
            var blurred = ExportResample.ConvSeparable(src, sw, sh, kernelX, kernelY);
            outp = ExportResample.WarpBilinear(blurred, sw, sh, dw, dh, to);
        }
        else outp = ExportResample.WarpClamped2(src, sw, sh, dw, dh, to);
        BlockHook?.Invoke("rsout", rect, dw, dh, outp);
        var scaled = new float[outp.Length];
        for (int i = 0; i < outp.Length; i++) scaled[i] = outp[i] * 16384f;   // FUN_18001b790 with _DAT_1806ef750 = (16384,16384,16384,16384)
        BlockHook?.Invoke("x16384", rect, dw, dh, scaled);
        return (rect, scaled);
    }

    /// <summary>`renderForExport`: the export-level source rect gathered from the render-thread tiles (each rendered whole by the tile callback:
    /// level origin shift → `FUN_1804bd710` → `RemoveVignettingGeneric&lt;vec4x32f,1&gt;` with the crop rect = shifted tile × cacheDims/pipelineDims).</summary>
    public float[] RenderSource(int level, RectI src)
    {
        var (W, H) = _lv.ExportDims[level];
        if (src.X0 < 0 || src.Y0 < 0 || src.X1 > W || src.Y1 > H) throw new ArgumentException("export source rect out of bounds");
        int nx = ExportLevels.GridCount(W), ny = ExportLevels.GridCount(H);
        int tx0 = Math.Min(src.X0 / ExportLevels.Tile, nx - 1), tx1 = Math.Min((src.X1 - 1) / ExportLevels.Tile, nx - 1);
        int ty0 = Math.Min(src.Y0 / ExportLevels.Tile, ny - 1), ty1 = Math.Min((src.Y1 - 1) / ExportLevels.Tile, ny - 1);
        var outp = new float[src.Width * src.Height * 4];
        var (ox, oy) = _lv.Origins[level]; var pd = _lv.PipelineDims[level];
        float m = _multiplier(level);
        var g = LensShadingKernel.Transform(_grid, m, inverse: true);
        float fx = (float)_lv.CacheDims.W / (float)pd.W, fy = (float)_lv.CacheDims.H / (float)pd.H;   // FUN_18049c5d0
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = _lv.TileRect(level, tx, ty);
                var shifted = new RectI(tile.X0 + ox, tile.Y0 + oy, tile.X1 + ox, tile.Y1 + oy);   // 180526690 L42–47
                var px = _cache.Render(_lv.BaseLevel + level, shifted, pd);                          // FUN_18048f2b0 → FUN_1804bd710 (mode 0, PipelineCache)
                int tw = tile.Width, th = tile.Height;
                TilePreHook?.Invoke(level, tile, shifted, px);
                var img = new Image<Vec4F>(tw, th);
                System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(img.Data);
                var floatRect = new RectF(fx * (float)shifted.X0, fy * (float)shifted.Y0, fx * (float)shifted.X1, fy * (float)shifted.Y1);
                LensShadingKernel.Apply(img, new RectI(0, 0, tw, th), floatRect, tw, th, _lv.CacheDims.W, _lv.CacheDims.H, _cols, _rows, g);
                System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, float>(img.Data.AsSpan()).CopyTo(px);
                TileHook?.Invoke(level, tile, shifted, px);
                var c = tile.Intersect(src);
                for (int y = c.Y0; y < c.Y1; y++)
                    Array.Copy(px, ((y - tile.Y0) * tw + (c.X0 - tile.X0)) * 4, outp, ((y - src.Y0) * src.Width + (c.X0 - src.X0)) * 4, c.Width * 4);
                Log?.Invoke($"  tile L{level} ({tx},{ty}) export ({tile.X0},{tile.Y0},{tile.X1},{tile.Y1}) pipeline ({shifted.X0},{shifted.Y0}) m {m:R}");
            }
        return outp;
    }
}

/// <summary>The export resamplers of `FUN_18052dfc0`.</summary>
public static class ExportResample
{
    static readonly float[] Table = WarpResample.BuildTable();

    /// <summary>`FUN_18052f210(n, scale)`: `x = (i − n/2)/scale`; `w = 1` at 0, `2·sin(πx)·sin(πx/2)/(x²·π²)` for |x| &lt; 2, else 0; normalised by the sum.</summary>
    public static float[] LanczosKernel(int n, float scale)
    {
        var k = new float[n]; float inv = 1f / scale, sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float x = (float)(i - (n >> 1)) * inv, w;
            if (x == 0f) w = 1f;
            else if (MathF.Abs(x) < 2f) { float s1 = MathF.Sin(x * 3.1415927f), s2 = MathF.Sin(x * 1.5707964f); w = ((s2 + s2) * s1) / (x * x * 9.869605f); }
            else w = 0f;
            k[i] = w; sum = sum + w;
        }
        float norm = 1f / sum;
        for (int i = 0; i < n; i++) k[i] = k[i] * norm;
        return k;
    }

    /// <summary>`ImageConvSeparable2D&lt;vec4x32f&gt;` (`1800bc930` + lambda `1800c0cd0`): clamp-to-edge rows/columns (`FUN_1800c1710`), reversed kernel
    /// vectors, vertical pass then horizontal, sequential `acc = k·s + acc` accumulation from tap 0.</summary>
    public static float[] ConvSeparable(float[] src, int w, int h, float[] kx, float[] ky)
    {
        int nx = kx.Length, ny = ky.Length, hx = (nx - 1) / 2, hy = (ny - 1) / 2;
        var rkx = new float[nx]; for (int i = 0; i < nx; i++) rkx[i] = kx[nx - 1 - i];
        var rky = new float[ny]; for (int i = 0; i < ny; i++) rky[i] = ky[ny - 1 - i];
        var tmp = new float[w * h * 4]; var outp = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int k = 0; k < ny; k++)
            {
                int sy = Math.Clamp(y - hy + k, 0, h - 1); float kv = rky[k]; int so = sy * w * 4, to = y * w * 4;
                if (k == 0) for (int i = 0; i < w * 4; i++) tmp[to + i] = src[so + i] * kv;
                else for (int i = 0; i < w * 4; i++) tmp[to + i] = src[so + i] * kv + tmp[to + i];
            }
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    float acc = 0f;
                    for (int k = 0; k < nx; k++) { int sx = Math.Clamp(x - hx + k, 0, w - 1); float v = tmp[(y * w + sx) * 4 + c] * rkx[k]; acc = k == 0 ? v : v + acc; }
                    outp[(y * w + x) * 4 + c] = acc;
                }
        }
        return outp;
    }

    /// <summary>`ImageWarpClamped&lt;2,vec4x32f,std::function&gt;` lambda_1 (`180533040`): `p = (int)((map − 1)·64)`, 64-phase Catmull-Rom with the
    /// clamped 4×4 gather at the border, zero fill outside, `max(−0.25·P, N) + P` recombination (shared with the aligned warp).</summary>
    public static float[] WarpClamped2(float[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new float[dw * dh * 4]; Span<float> block = stackalloc float[64]; Span<float> fill = stackalloc float[4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)((u + -1f) * 64f), py = (int)((v + -1f) * 64f);
                int ix = px >> 6, iy = py >> 6, o = (y * dw + x) * 4;
                if (ix < 0 || sw - 4 < ix || iy < 0 || sh - 4 < iy)
                {
                    if (!(ix < sw && 0 < ix + 4 && iy < sh && 0 < iy + 4)) { dst[o] = fill[0]; dst[o + 1] = fill[1]; dst[o + 2] = fill[2]; dst[o + 3] = fill[3]; continue; }
                    Span<int> cx = stackalloc int[4]; Span<int> cy = stackalloc int[4];
                    for (int k = 0; k < 4; k++) { cx[k] = Math.Clamp(ix + k, 0, sw - 1); cy[k] = Math.Clamp(iy + k, 0, sh - 1); }
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { int si = (cy[r] * sw + cx[c]) * 4, bi = (r * 4 + c) * 4; block[bi] = src[si]; block[bi + 1] = src[si + 1]; block[bi + 2] = src[si + 2]; block[bi + 3] = src[si + 3]; }
                    WarpResample.Resample(block, 4, 0, Table, px & 63, py & 63, dst, o);
                }
                else WarpResample.Resample(src, sw, (iy * sw + ix) * 4, Table, px & 63, py & 63, dst, o);
            }
        return dst;
    }

    /// <summary>`ImageWarp&lt;1,0,vec4x32f,std::function&gt;` lambda_1 (`180533f30`): `p = (int)(map·64)`, weights `(1 − i/64, i/64)` (`1805339c0`), clamped 2×2 at the
    /// border, zero fill; `out = (wy1·p11 + wy0·p10)·wx1 + (wy1·p01 + wy0·p00)·wx0`.</summary>
    public static float[] WarpBilinear(float[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new float[dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)(u * 64f), py = (int)(v * 64f);
                int ix = px >> 6, iy = py >> 6, o = (y * dw + x) * 4;
                int x0, x1, y0, y1;
                if (ix < 0 || sw - 2 < ix || iy < 0 || sh - 2 < iy)
                {
                    if (!(ix < sw && 0 < ix + 2 && iy < sh && 0 < iy + 2)) continue;   // zero fill
                    x0 = Math.Clamp(ix, 0, sw - 1); x1 = Math.Clamp(ix + 1, 0, sw - 1); y0 = Math.Clamp(iy, 0, sh - 1); y1 = Math.Clamp(iy + 1, 0, sh - 1);
                }
                else { x0 = ix; x1 = ix + 1; y0 = iy; y1 = iy + 1; }
                float ty = (float)(py & 63) * 0.015625f, wy0 = 1f - ty, wy1 = ty;
                float tx = (float)(px & 63) * 0.015625f, wx0 = 1f - tx, wx1 = tx;
                int p00 = (y0 * sw + x0) * 4, p10 = (y0 * sw + x1) * 4, p01 = (y1 * sw + x0) * 4, p11 = (y1 * sw + x1) * 4;
                for (int c = 0; c < 4; c++)
                    dst[o + c] = (wy1 * src[p11 + c] + wy0 * src[p10 + c]) * wx1 + (wy1 * src[p01 + c] + wy0 * src[p00 + c]) * wx0;
            }
        return dst;
    }
}

/// <summary>The export window inside the level-0 pipeline canvas (`setInputDataStream` L~340–430 → `FUN_1804b2520`, crop from `FUN_180110790`):
/// `+0x270` export dims, `+0x288` pipeline dims, `+0x2d0` origins per level.</summary>
public static class ExportWindow
{
    /// <summary>`FUN_18012a380(out, camId)` — the camera → optical group table `DAT_18068be60` = {0×5, 1×5, 2×6}; ids above 15 raise
    /// "unknown camera group type!".</summary>
    public static int Group(int camId)
    {
        if ((uint)camId > 15) throw new NotSupportedException("unknown camera group type!");
        return camId <= 4 ? 0 : camId <= 9 ? 1 : 2;
    }

    /// <summary>`FUN_180110ba0(hdr, camId, param_3)` — a camera's focal length by optical group, ten `.rdata` floats. `param_3 != 0` gives the
    /// 35 mm equivalent: C (`0x18068a53c`) 150, B (`0x18068a538`) 70, A 35 on an AR835 (`0x18068a540`) and 28 on an AR1335 or IMX386
    /// (`0x18068a544`). `param_3 == 0` gives the physical focal: C (`0x18068a528`) 19.77, B (`0x18068a524`) 9.19, A 4.56 / 3.95 / 3.68 on an
    /// AR835 / IMX386 / AR1335 (`0x18068a52c` / `530` / `534`). NB the A branch keys on the sensor type of the header's REFERENCE camera
    /// (`FUN_180111a50(hdr, hdr+0x44)`), not of <paramref name="camId"/>; a mono or unknown sensor raises "Unexpected sensor size!".</summary>
    public static float Focal(Ltpb.LightHeader hdr, int camId, bool equiv35)
    {
        int g = Group(camId);
        if (g == 2) return equiv35 ? 150f : 19.77f;
        if (g == 1) return equiv35 ? 70f : 9.19f;
        var sensor = Ltpb.SensorType.SensorUnknown;
        if (hdr.HwInfo is not null) foreach (var c in hdr.HwInfo.Camera) if (c.Id == hdr.ImageReferenceCamera) { sensor = c.Sensor; break; }
        return sensor switch
        {
            Ltpb.SensorType.SensorAr835 => equiv35 ? 35f : 4.56f,
            Ltpb.SensorType.SensorImx386 => equiv35 ? 28f : 3.95f,
            Ltpb.SensorType.SensorAr1335 => equiv35 ? 28f : 3.68f,
            _ => throw new NotSupportedException("Unexpected sensor size!"),
        };
    }

    /// <summary>`FUN_180110ba0(hdr, camId, 1)` — the 35 mm-equivalent focal length (70/28 is the A→B canvas ratio).</summary>
    public static float Focal35(Ltpb.LightHeader hdr, int camId) => Focal(hdr, camId, true);

    /// <summary>`FUN_180111d20(hdr, camId)` — the per-group lens aperture, `DAT_18068ae4c` = {2.0, 2.0, 2.4}; a group above 2 raises
    /// "unknown camera!".</summary>
    public static float Aperture(int camId) => Group(camId) switch { 0 => 2f, 1 => 2f, _ => 2.4f };

    /// <summary>`FUN_1804b23e0(renderer)` — the pipeline-canvas magnification: the 35 mm-equivalent focal length of the group ABOVE the reference
    /// group over that of the reference group, both taken from the group table above at the group's FIRST camera. A reference → `focal(B1)/focal(A1)`
    /// = 70/28 = 2.5; B reference → `focal(C1)/focal(B1)` = 150/70 = 2.142857; a C reference raises "Superres not supported in C-mode!"
    /// (unreachable — a C reference forces <see cref="BaseLevelOffset"/> ≥ 1, which takes the literal 2.0f instead).</summary>
    public static float CanvasFactor(Ltpb.LightHeader hdr)
    {
        int g = Group((int)hdr.ImageReferenceCamera);
        if (g > 1) throw new NotSupportedException("Superres not supported in C-mode!");
        int den = g == 1 ? 5 : 0, num = g == 1 ? 10 : 5;
        float d = Focal35(hdr, den);
        return Focal35(hdr, num) / d;
    }

    /// <summary>The `CIAPI::Transform` flag `setInputDataStream` L~265–320 builds and hands to `FUN_18050c4a0(transform, profile, flag)`: a scan of
    /// every module sets `hasB` / `hasC`, then the reference group picks one — A reference → `hasB`, B reference → `hasC`, C reference → 0.
    /// So it is "the capture contains a module of the group immediately above the reference's", i.e. is super-resolution possible at all.</summary>
    public static bool HasNextGroup(Lux.Engine.Lri.LriFile lri)
    {
        int g = Group((int)lri.Header.ImageReferenceCamera);
        if (g > 1) return false;
        foreach (var m in lri.Modules.Values) if (Group((int)m.Module.Id) == g + 1) return true;
        return false;
    }

    /// <summary>`FUN_18050c640(Transform{int profile, byte flag})` — the renderer's base pipeline level: profile 1 or 2 → 1, profile 0 → 4,
    /// profile 3 (the desktop/export profile) → `flag ^ 1`, anything else → "Invalid Renderer profile!". The level count is `5 − offset`
    /// (`renderer+0x264`). Every L16 capture in the corpus is profile 3 with a higher group present, so the offset is 0 and there are 5 levels.</summary>
    public static int BaseLevelOffset(int profile, bool hasNextGroup) => profile switch
    {
        1 or 2 => 1,
        0 => 4,
        3 => (hasNextGroup ? 1 : 0) ^ 1,
        _ => throw new NotSupportedException("Invalid Renderer profile!"),
    };

    /// <summary>The level-0 pipeline canvas (`setInputDataStream` L347–361): `f = (FUN_18050c640(renderer+0x90) &gt;= 1) ? 2.0f : FUN_1804b23e0(renderer)`,
    /// canvas = `((int)(sensorW·f), (int)(sensorH·f))`, which `FUN_1804b2520` then rounds onto the sensor's aspect grid and writes back through its
    /// in/out `param_3` — so the value the renderer stores in `+0x288`/`+0x2a0` is the ROUNDED one. On the L16: 4160×3120 × 2.5 → 10400×7800 →
    /// 10432×7824 for an A reference, × 150/70 → 8914×6685 → 8896×6672 for a B reference.</summary>
    public static (int W, int H) Canvas(Lux.Engine.Lri.LriFile lri, (int W, int H) sensor, int profile = 3)
    {
        int offset = BaseLevelOffset(profile, HasNextGroup(lri));
        if (offset != 0)
            throw new NotSupportedException(
                $"renderer base pipeline level {offset} (profile {profile}, no module above the reference group): the export/pipeline/origin\n"
              + "level vectors then start at that level and the canvas factor is the literal 2.0f. No capture in the corpus reaches it and no\n"
              + "cp.dll reference dump exists for it, so it is left unported rather than guessed.");
        float f = offset >= 1 ? 2f : CanvasFactor(lri.Header);
        return SnapCanvas(((int)((float)sensor.W * f), (int)((float)sensor.H * f)), sensor);
    }

    /// <summary>The aspect grid of `FUN_1804b2520` L1–30: `aw = 16·W/g`, `ah = 16·H/g` with `g = gcd(sensor)` (64 × 48 for 4160×3120; both fall back to
    /// `gcd(16, g)` when either exceeds a sixteenth of the sensor), then the canvas rounded to a whole number of grid cells — the same count on both
    /// axes unless `aw == ah`. Idempotent, so applying it to an already-snapped canvas is a no-op.</summary>
    public static (int W, int H) SnapCanvas((int W, int H) canvas, (int W, int H) sensor)
    {
        var (iaw, iah) = AspectGrid(sensor);
        int cx = (canvas.W + (iaw >> 1)) / iaw, cy = (canvas.H + (iah >> 1)) / iah;
        int mx = Math.Max(cx, cy), nx = mx, ny = mx;
        if (iaw == iah) { nx = cx; ny = cy; }
        return (nx * iaw, ny * iah);
    }

    static (int W, int H) AspectGrid((int W, int H) sensor)
    {
        int g = ExportTransform.Gcd(sensor.W, sensor.H);
        long ah = (long)(sensor.H << 4) / g, aw = (long)(sensor.W << 4) / g;
        if ((sensor.H >> 4) < (int)ah || (sensor.W >> 4) < (int)aw) { int gg = ExportTransform.Gcd(16, g); aw = ah = gg; }
        return ((int)aw, (int)ah);
    }

    /// <summary>`renderer+0x264` = `5 − FUN_18050c640(renderer+0x90)`: the number of pipeline/export levels.</summary>
    public static int LevelCount(Lux.Engine.Lri.LriFile lri, int profile = 3) => 5 - BaseLevelOffset(profile, HasNextGroup(lri));

    /// <summary>`FUN_180110790`: the header `ViewPreferences.crop` (start, size → x1 = x0 + w) when its width/height are in (1e-7, 1.0000001); else the
    /// digital-zoom crop `((1 − r)/2, (1 − r)/2, (1 + r)/2, (1 + r)/2)` with `r = refFocal/effectiveFocal` (full frame when no effective focal length).</summary>
    public static RectF CropRect(Lux.Engine.Lri.LriFile lri, (int W, int H) sensor)
    {
        var h = lri.Header;
        // The guard around both branches: a C reference or a zero effective focal length is the full frame. (`hdr+0x78` also carries an
        // Optional<bool> whose stored value `false` short-circuits to the full frame before this; which ViewPreferences field it is has not
        // been identified, and on every capture seen it is either absent or true, so it is not read here.)
        if (Group((int)h.ImageReferenceCamera) < 2 && h.ImageFocalLength != 0)
        {
            var crop = h.ViewPreferences?.Crop;
            if (crop?.Start is not null && crop.Size is not null)
            {
                float x0 = crop.Start.X, y0 = crop.Start.Y, x1 = x0 + crop.Size.X, y1 = y0 + crop.Size.Y;
                float w = x1 - x0, hh = y1 - y0;
                if (1.0000000e-7f < w && hh < 1.0000001f && w < 1.0000001f && 1.0000000e-7f < hh) return new RectF(x0, y0, x1, y1);
            }
            // the digital zoom: r = the reference GROUP's 35 mm-equivalent focal over the capture's effective focal length, centred
            float r = Focal35(h, Group((int)h.ImageReferenceCamera) * 5), eff = (float)h.ImageFocalLength;
            if (eff < r) throw new NotSupportedException("Effective focal length must be larger than reference focal length!");
            r /= eff;
            float m = (1f - r) * 0.5f;
            return new RectF(m, m, m + r, m + r);
        }
        return new RectF(0f, 0f, 1f, 1f);
    }

    /// <summary>`FUN_1804b2520(out, canvas (in/out), sensor, levels, crop)`: aspect grid `aw = 16·W/g`, `ah = 16·H/g` (g = gcd(sensor)), canvas rounded to
    /// the grid, export size `(int)(cropW·canvasW)` rounded to the grid (both axes take the max count unless aw == ah), origin
    /// `(int)(x0·0.0625·canvasW)·16`; per level everything `&gt;&gt; l`.</summary>
    public static ExportLevels Compute((int W, int H) canvas, (int W, int H) sensor, RectF crop, int levels)
    {
        var (iaw, iah) = AspectGrid(sensor); int hw = iaw >> 1, hh = iah >> 1;
        var (canvasW, canvasH) = SnapCanvas(canvas, sensor);
        float cwf = (float)canvasW, chf = (float)canvasH;
        int ex = ((int)((crop.X1 - crop.X0) * cwf) + hw) / iaw, ey = ((int)((crop.Y1 - crop.Y0) * chf) + hh) / iah;
        int m2 = Math.Max(ex, ey), ewx = m2, ewy = m2;
        if (iaw == iah) { ewx = ex; ewy = ey; }
        int x0 = (int)(crop.X0 * 0.0625f * cwf) * 16, y0 = (int)(0.0625f * crop.Y0 * chf) * 16;
        int x1 = ewx * iaw + x0, y1 = ewy * iah + y0;
        var ed = new (int, int)[levels]; var pd = new (int, int)[levels]; var og = new (int, int)[levels];
        for (int l = 0; l < levels; l++) { ed[l] = ((x1 >> l) - (x0 >> l), (y1 >> l) - (y0 >> l)); og[l] = (x0 >> l, y0 >> l); pd[l] = (canvasW >> l, canvasH >> l); }
        // +0x490 ImageCaches dims (FUN_1804d4720) = the CapturedImage (module frame) size, 4160×3120 on the L16 (cp.dll: tile (1008,832) → vignetting crop 401.963 = ×4160/10432)
        return new ExportLevels(ed, pd, og, sensor, 0);
    }

    /// <summary>The all-in-focus f-number `DOFCache::fmax = (p[0x90]·p[0x8c])/p[0x88]` (`FUN_1804e7f40`), whose parameters `FUN_1804957c0` fills from
    /// the REFERENCE camera: `p[0x88] = FUN_180110ba0(hdr, ref, 0)` (physical focal), `p[0x8c] = FUN_180110ba0(hdr, ref, 1)` (35 mm equivalent),
    /// `p[0x90] = FUN_180111d20(hdr, ref)` (aperture). So `f = aperture · equiv35 / physical`: 2·28/3.68 = 15.217391 for an AR1335 A reference
    /// (255305456/2²⁴, the value every A-reference Lumen export carries) and 2·70/9.19 = 15.23395 for a B reference (255583280/2²⁴, verified
    /// against `o552_l3.dng`). `fnum:` overrides it.</summary>
    public static float AllInFocusFNumber(Lux.Engine.Lri.LriFile lri)
    {
        var h = lri.Header; int refId = (int)h.ImageReferenceCamera;
        return (Aperture(refId) * Focal(h, refId, true)) / Focal(h, refId, false);
    }
}
