using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Cache;

/// <summary>
/// `PipelineCache` = `TileCache&lt;Vec3&lt;Float16&gt;&gt;` (spec `ab6d047c4aab9a904.md`): 512×512 tiles per level, generated on demand by
/// `PipelineCache::lambda_0` — levels 2–4 = the reference module's cache tiles (`ReferenceImageCache::processLevel` at cache level L−1 = module ISP +
/// aligned warp), multiplied by the AsShot neutral and stored as round-toward-zero halves; `RenderRoi` gathers tiles back to float RGBA (alpha 1).
/// Level 1 (`FusionCacheBayer` + undistort warp) comes from <see cref="Level1"/>; level 0 (`PipelineCache::processLevel0` = `lt::ImageResolutionAmp`
/// + the `FUN_1800fb0b0` square, spec `a-resamp.md` §0) from <see cref="Level0"/> — a `ResAmp.ImageResolutionAmp` built by the caller.
/// </summary>
public sealed class PipelineCache
{
    public const int TileSize = 512;
    public readonly (int W, int H)[] LevelDims;           // +0x08: [0] ResAmp canvas, [1] sensor, [2..4] reference cache levels 1..3
    public readonly (int X, int Y)[] Grid;                // +0x20: max(1, (256 + W) / 512)
    readonly Dictionary<(int L, int Tx, int Ty), ushort[]> _tiles = new();
    readonly Dictionary<(int, int, int), (int W, int H)> _tileDims = new();
    public Func<int, RectI, Image<Vec4F>> ReferenceLevel = null!;   // cache level L (1..3), rect in level pixels → float RGBA (processLevel)
    /// <summary>Level 1: `PipelineCache::processLevel1` (fusion cache render + inlined-map undistort warp), rect in sensor pixels → float RGBA.</summary>
    public Func<RectI, Image<Vec4F>>? Level1;
    /// <summary>Level 0: `lt::ImageResolutionAmp` over the canvas tile rect → √-domain RGBA (pre-square); normally `rect => amp.Run(rect)`
    /// with a <see cref="ResAmp.ImageResolutionAmp"/> whose generators are the +0x220/+0x230/+0x258 lambdas of `initResAmp`.</summary>
    public Func<RectI, ResAmp.ResImage>? Level0;
    public float[] Neutral = { 1f, 1f, 1f };
    public Action<string>? Log;

    public PipelineCache((int W, int H)[] levelDims)
    {
        LevelDims = levelDims; Grid = new (int, int)[levelDims.Length];
        for (int l = 0; l < levelDims.Length; l++) Grid[l] = (Math.Max(1, (TileSize / 2 + levelDims[l].W) / TileSize), Math.Max(1, (TileSize / 2 + levelDims[l].H) / TileSize));
    }

    /// <summary>`FUN_1804bcde0`: a tile is 512×512 unless it is the last of its row/column, which absorbs the remainder (`min(W, x0 + 1024) − x0`).</summary>
    public (int W, int H) TileDims(int level, int tx, int ty)
    {
        var (nx, ny) = Grid[level]; var (W, H) = LevelDims[level]; int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = tx == nx - 1 ? Math.Min(W, x0 + 2 * TileSize) - Math.Max(x0, 0) : TileSize;
        int h = ty == ny - 1 ? Math.Min(H, y0 + 2 * TileSize) - Math.Max(y0, 0) : TileSize;
        return (w, h);
    }

    /// <summary>`PipelineCache::lambda_0` for levels 2–4: reference processLevel(L−1) over the tile rect, × (neutral, 1), → RTZ half RGB.</summary>
    ushort[] Generate(int level, int tx, int ty)
    {
        var (w, h) = TileDims(level, tx, ty); var rect = new RectI(tx * TileSize, ty * TileSize, tx * TileSize + w, ty * TileSize + h);
        if (level is < 0 or > 4) throw new NotSupportedException($"PipelineCache level {level} generation is not ported");
        if (level == 1 && Level1 is null) throw new InvalidOperationException("PipelineCache level 1 needs the FusionCacheBayer path (Level1)");
        if (level == 0)
        {
            if (Level0 is null) throw new InvalidOperationException("Requested PipelineCache::processLevel0 before initResamp()!");
            var amp = Level0(rect);
            if (amp.W != w || amp.H != h) throw new InvalidOperationException("ImageResolutionAmp did not create image of correct size!");
            ResAmp.ImageResolutionAmp.Square(amp);                       // FUN_1800fb0b0: the √ decode, every lane squared
            var t0 = new ushort[w * h * 3]; float r0 = Neutral[0], g0 = Neutral[1], b0 = Neutral[2];
            for (int y = 0; y < h; y++)
            {
                int b = amp.Idx(0, y);
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3, i = b + x * 4;
                    t0[o] = Half16.FromFloat(amp.Data[i] * r0); t0[o + 1] = Half16.FromFloat(amp.Data[i + 1] * g0); t0[o + 2] = Half16.FromFloat(amp.Data[i + 2] * b0);
                }
            }
            Log?.Invoke($"pcache: generated L0 tile ({tx},{ty}) {w}x{h}");
            return t0;
        }
        var img = level == 1 ? Level1!(rect) : ReferenceLevel(level - 1, rect);
        bool viaHalfCache = level >= 2;   // levels 2–4 come out of the reference TileCache<Vec3<Float16>> (double rounding); level 1 is float straight from the warp
        if (img.Width != w || img.Height != h) throw new InvalidOperationException("reference cache tile size mismatch");
        var tile = new ushort[w * h * 3]; float nr = Neutral[0], ng = Neutral[1], nb = Neutral[2];
        for (int y = 0; y < h; y++)
        {
            var row = img.Row(y);
            for (int x = 0; x < w; x++)
            {
                var p = row[x]; int o = (y * w + x) * 3;
                // the reference TileCache<Vec3<Float16>> stores processLevel as RTZ halves; PipelineCache reads them back (exact) and multiplies by the neutral
                float r = p.R, g = p.G, b = p.B;
                if (viaHalfCache) { r = Half16.ToFloat(Half16.FromFloat(r)); g = Half16.ToFloat(Half16.FromFloat(g)); b = Half16.ToFloat(Half16.FromFloat(b)); }
                tile[o] = Half16.FromFloat(r * nr); tile[o + 1] = Half16.FromFloat(g * ng); tile[o + 2] = Half16.FromFloat(b * nb);
            }
        }
        Log?.Invoke($"pcache: generated L{level} tile ({tx},{ty}) {w}x{h}");
        return tile;
    }

    ushort[] Tile(int level, int tx, int ty)
    {
        var key = (level, tx, ty);
        if (!_tiles.TryGetValue(key, out var t)) { t = Generate(level, tx, ty); _tiles[key] = t; _tileDims[key] = TileDims(level, tx, ty); }
        return t;
    }

    /// <summary>`TileCache::renderROI&lt;vec4x32f&gt;` (1804bd050): gather the tiles overlapping `rect` (level pixels) as float RGBA with alpha 1.</summary>
    public float[] RenderRoi(int level, RectI rect)
    {
        var (W, H) = LevelDims[level];
        if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > W || rect.Y1 > H) throw new ArgumentException("Requested ROI is out-of-bounds!");
        var (nx, ny) = Grid[level];
        int tx0 = Math.Min(rect.X0 / TileSize, nx - 1), tx1 = Math.Min((rect.X1 - 1) / TileSize, nx - 1), ty0 = Math.Min(rect.Y0 / TileSize, ny - 1), ty1 = Math.Min((rect.Y1 - 1) / TileSize, ny - 1);
        if (tx1 < tx0 || ty1 < ty0) throw new InvalidOperationException("No tiles in ROI!");
        int rw = rect.Width, rh = rect.Height; var outp = new float[rw * rh * 4];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var t = Tile(level, tx, ty); var (tw, th) = _tileDims[(level, tx, ty)];
                int x0 = Math.Max(rect.X0, tx * TileSize), y0 = Math.Max(rect.Y0, ty * TileSize), x1 = Math.Min(rect.X1, tx * TileSize + tw), y1 = Math.Min(rect.Y1, ty * TileSize + th);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int si = ((y - ty * TileSize) * tw + (x - tx * TileSize)) * 3, di = ((y - rect.Y0) * rw + (x - rect.X0)) * 4;
                        outp[di] = Half16.ToFloat(t[si]); outp[di + 1] = Half16.ToFloat(t[si + 1]); outp[di + 2] = Half16.ToFloat(t[si + 2]); outp[di + 3] = 1.0f;
                    }
            }
        return outp;
    }

    /// <summary>`FUN_1804bd710(cache, out, rect, reqDims, level)`: `renderROI` when the stored dims equal the requested ones, else the 16.16 Catmull-Rom
    /// `ImageResample&lt;2&gt;` of the ±2-margin source rect (spec §2/§2.2).</summary>
    public float[] Render(int level, RectI rect, (int W, int H) reqDims)
    {
        var (Ws, Hs) = LevelDims[level];
        if ((Ws, Hs) == reqDims) return RenderRoi(level, rect);
        float fx = (float)Ws / (float)reqDims.W, fy = (float)Hs / (float)reqDims.H;
        int sx0 = Math.Max(0, (int)MathF.Floor(fx * rect.X0 + -2.0f)), sy0 = Math.Max(0, (int)MathF.Floor(fy * rect.Y0 + -2.0f));
        int sx1 = Math.Min(Ws, (int)MathF.Ceiling(((float)rect.Width + (float)rect.X0) * fx + 2.0f)), sy1 = Math.Min(Hs, (int)MathF.Ceiling(((float)rect.Height + (float)rect.Y0) * fy + 2.0f));
        var src = RenderRoi(level, new RectI(sx0, sy0, sx1, sy1));
        double offX = (double)((float)rect.X0 * fx - (float)sx0), offY = (double)((float)rect.Y0 * fy - (float)sy0);
        return ImageResample2.Run(src, sx1 - sx0, sy1 - sy0, rect.Width, rect.Height, offX, offY, fx, fy);
    }
}

/// <summary>`ImageResample&lt;2, vec4x32f&gt;` (1804451f0): 64-phase Catmull-Rom in 16.16 fixed point, taps clamped to the source, no half-pixel offsets.</summary>
public static class ImageResample2
{
    static readonly float[] Table = BuildTable();
    static float[] BuildTable()
    {
        var t = new float[64 * 4]; Span<float> w = stackalloc float[4];
        for (int i = 0; i < 64; i++) { WarpResample.Kernel((float)i * 0.015625f, w); for (int k = 0; k < 4; k++) t[i * 4 + k] = w[k]; }
        return t;
    }
    public static float[] Run(float[] src, int sw, int sh, int dw, int dh, double offX, double offY, double scX, double scY)
    {
        int ox = (int)(offX * 65536.0), oy = (int)(offY * 65536.0), sx = (int)(scX * 65536.0), sy = (int)(scY * 65536.0);
        var rows = new Dictionary<int, float[]>();
        float[] HRow(int iy)
        {
            int cy = Math.Clamp(iy, 0, sh - 1);
            if (rows.TryGetValue(cy, out var r)) return r;
            r = new float[dw * 4];
            for (int x = 0; x < dw; x++)
            {
                int rx = sx * x + ox, ix = rx >> 16, ph = (rx >> 10) & 63;
                int i0 = Math.Clamp(ix - 1, 0, sw - 1), i1 = Math.Clamp(ix, 0, sw - 1), i2 = Math.Clamp(ix + 1, 0, sw - 1), i3 = Math.Clamp(ix + 2, 0, sw - 1);
                float w0 = Table[ph * 4], w1 = Table[ph * 4 + 1], w2 = Table[ph * 4 + 2], w3 = Table[ph * 4 + 3];
                for (int c = 0; c < 4; c++)
                {
                    float s0 = src[(cy * sw + i0) * 4 + c], s1 = src[(cy * sw + i1) * 4 + c], s2 = src[(cy * sw + i2) * 4 + c], s3 = src[(cy * sw + i3) * 4 + c];
                    r[x * 4 + c] = (w1 * s1 + w0 * s0) + (w3 * s3 + w2 * s2);
                }
            }
            rows[cy] = r; return r;
        }
        var outp = new float[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int ry = sy * y + oy, iy = ry >> 16, ph = (ry >> 10) & 63;
            var r0 = HRow(iy - 1); var r1 = HRow(iy); var r2 = HRow(iy + 1); var r3 = HRow(iy + 2);
            float v0 = Table[ph * 4], v1 = Table[ph * 4 + 1], v2 = Table[ph * 4 + 2], v3 = Table[ph * 4 + 3];
            for (int i = 0; i < dw * 4; i++) outp[y * dw * 4 + i] = (v1 * r1[i] + v0 * r0[i]) + (v3 * r3[i] + v2 * r2[i]);
        }
        return outp;
    }
}
