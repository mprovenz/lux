using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>`TileVersion`: one full copy of a tile, tagged with the edit that produced it — version, tile index,
/// size, float depth, float background depth and u8 layer mask. The last two are
/// null unless a layered edge heal produced the version.</summary>
public sealed class DepthTileVersion
{
    public uint Version; public int Tx, Ty, W, H;
    public float[] Depth = Array.Empty<float>();
    public float[]? Background; public byte[]? Mask;
    /// <summary>Lumen's "mask image or background image is not empty" test.</summary>
    public bool HasLayers => Mask != null || Background != null;
}

/// <summary>
/// Lumen's `lt::DepthEditorPrivate` (behind `CIAPI::DepthEditor`): the depth-edit history and its write-back into the renderer's depth cache.
///
/// The editor works on one pipeline level — index <c>min(1 − baseLevel, n − 1)</c>, level 1 on the L16 — whose size is
/// the export window at that level (4160×3120) and whose pixel (0,0) sits at <see cref="Origin"/>
/// on the depth-cache image (5216×3912). The level is cut into 512×512 tiles; the last column / row absorbs the
/// remainder. There is no command stack: history is the list of edit rects plus, per tile, the full tile copies
/// (<see cref="DepthTileVersion"/>) the edits produced; a cursor marks the current version, a base marks the undo floor.
///
/// A tile that has no version yet is read from the <b>source depth</b>, not from the cache: Lumen resamples the stereo depth
/// image through <c>1 / nearest(1 / x)</c> without the cache's 100 000 mm clip. Every push
/// rewrites its rect of the cache from tiles, so an edited (or undone) area differs from the initial fill in the last bits and above the clip.
/// </summary>
public sealed class DepthEditor
{
    /// <summary>The tile size: 512 × 512.</summary>
    public const int TileW = 512, TileH = 512;
    /// <summary>Once the rect list holds more than this many entries the oldest is dropped and the floor rises.</summary>
    public const int MaxHistory = 20;

    readonly DepthImageCache _cache; readonly DepthImage _source;
    public (int W, int H) LevelSize { get; }
    public (int X, int Y) Origin { get; }
    public int Nx { get; }
    public int Ny { get; }
    readonly List<DepthTileVersion>[,] _tiles;
    readonly List<RectI> _rois = new();
    public uint Cursor { get; private set; }
    public uint Base { get; private set; }
    public DepthEditLayer Layer { get; }
    /// <summary>The Gaussian lookup table `GaussianLut(256, 0.06)`.</summary>
    public GaussianLut Lut { get; } = new(256, 0.06f);

    /// <summary>The colour guide of the adaptive brush (and of the edge heal): vec4 float pixels (4 per pixel, row-major) of a rect given
    /// <b>in depth-cache coordinates</b> (level rect + <see cref="Origin"/>, i.e. the 5216×3912 pipeline-level frame). In Lumen:
    /// the PipelineCache's resampling ROI render at the pipeline-level dims,
    /// followed by the editor's colour conversion with these settings:
    /// demosaicking.type = none, color_correction.type = default, auto_white_balance.type = manual_color,
    /// auto_white_balance.neutral_color = the renderer's neutral colour, output.color_space = srgb.</summary>
    public Func<RectI, float[]>? GuideProvider { get; set; }
    /// <summary>The colour window of the adaptive brush's reference colour: vec4 float pixels of a rect given in <b>PipelineCache level-1
    /// coordinates</b> (<see cref="ColourLevelDims"/>, the whole canvas at 4160×3120). In Lumen: `TileCache::renderROI&lt;vec4x32f&gt;`
    /// of that level, then the same colour conversion.</summary>
    public Func<RectI, float[]>? ReferenceProvider { get; set; }
    /// <summary>Dims of PipelineCache level 1 (4160×3120): the frame the adaptive brush's reference window
    /// is computed in.</summary>
    public (int W, int H) ColourLevelDims { get; set; }
    /// <summary>A point normalised to the export window → normalised to the pipeline canvas
    /// (<see cref="DofModel.MapExportPoint"/>). Used for the adaptive brush's reference colour and by <see cref="DepthAtPoint"/>.</summary>
    public Func<float, float, (float X, float Y)>? PointMapper { get; set; }
    /// <summary>Raised after every write into the depth cache with the rewritten rect (cache coordinates) — Lumen invalidates the DOFCache
    /// and refreshes at this point (renderer request 0xc).</summary>
    public event Action<RectI>? DepthChanged;

    /// <param name="cache">the renderer's depth cache (its image is edited in place)</param>
    /// <param name="source">the stereo depth image the cache was filled from (`StereoAsyncApi.FullDepth`)</param>
    /// <param name="levelSize">export-window dims of the edited level (`ExportLevels.ExportDims[level]`)</param>
    /// <param name="origin">that level's origin on the cache image (`ExportLevels.Origins[level]`)</param>
    public DepthEditor(DepthImageCache cache, DepthImage source, (int W, int H) levelSize, (int X, int Y) origin)
    {
        _cache = cache; _source = source; LevelSize = levelSize; Origin = origin;
        Layer = new DepthEditLayer(cache.Current.W, cache.Current.H);
        // ceil(dim / tile), minus one when more than one tile and the grid overshoots
        int nx = levelSize.W / TileW; if (levelSize.W % TileW != 0) nx++;
        int ny = levelSize.H / TileH; if (levelSize.H % TileH != 0) ny++;
        if (nx > 1 && levelSize.W < TileW * nx) nx--;
        if (ny > 1 && levelSize.H < TileH * ny) ny--;
        Nx = nx; Ny = ny;
        _tiles = new List<DepthTileVersion>[nx, ny];
        for (int x = 0; x < nx; x++) for (int y = 0; y < ny; y++) _tiles[x, y] = new List<DepthTileVersion>();
    }

    /// <summary>The edited level: <c>min(1 − baseLevel, levels − 1)</c>.</summary>
    public static int EditLevel(ExportLevels lv) => Math.Min(1 - lv.BaseLevel, lv.PipelineDims.Length - 1);

    /// <summary>An editor wired to a capture's level tables. <paramref name="colourLevelDims"/> = the PipelineCache's level dims
    /// (`PipelineCache.LevelDims`: {10432×7824, 4160×3120, …} on the L16).</summary>
    public static DepthEditor Create(DepthImageCache cache, DepthImage source, ExportLevels lv, (int W, int H)[] colourLevelDims)
    {
        int l = EditLevel(lv);
        return new DepthEditor(cache, source, lv.ExportDims[l], lv.Origins[l])
        {
            Level = l, LevelDims = lv.ExportDims, LevelOrigins = lv.Origins,
            ColourLevelDims = colourLevelDims[Math.Min(1, colourLevelDims.Length - 1)],
            PointMapper = (x, y) => DofModel.MapExportPoint(x, y, lv.ExportDims[0], lv.Origins[0], colourLevelDims[lv.BaseLevel]),
        };
    }

    /// <summary>The depth cache the editor writes into and the source depth unedited tiles are read from (persistence reads both).</summary>
    public DepthImageCache Cache => _cache;
    public DepthImage Source => _source;

    public IReadOnlyList<RectI> Rois => _rois;
    public IReadOnlyList<DepthTileVersion> Versions(int tx, int ty) => _tiles[tx, ty];
    public bool CanUndo => Cursor != Base;
    public bool CanRedo => (ulong)(Cursor - Base) < (ulong)_rois.Count;

    // ------------------------------------------------------------------------------------------------ tile grid

    /// <summary>The tile index range {tx0, ty0, tx1, ty1} (inclusive) of a pixel rect; "No tiles in ROI!".</summary>
    public (int X0, int Y0, int X1, int Y1) TileRange(RectI r)
    {
        int x0 = r.X0 / TileW, y0 = r.Y0 / TileH, mx = Nx - 1, my = Ny - 1;
        if (mx <= x0) x0 = mx;
        if (my <= y0) y0 = my;
        int x1 = (r.X1 - 1) / TileW, y1 = (r.Y1 - 1) / TileH;
        if (x1 >= mx) x1 = mx;
        if (y1 >= my) y1 = my;
        if (x1 < x0 || y1 < y0) throw new InvalidOperationException("No tiles in ROI!");
        return (x0, y0, x1, y1);
    }

    /// <summary>Tile index → level rect. The last column is <c>min(x0 + 2·512, levelW) − x0</c> wide, the last row likewise.</summary>
    public RectI TileRect(int tx, int ty)
    {
        int x0 = tx * TileW, y0 = ty * TileH, w = TileW, h = TileH;
        if (tx == Nx - 1) w = Math.Min(x0 + 2 * TileW, LevelSize.W) - x0;
        if (ty == Ny - 1) h = Math.Min(y0 + 2 * TileH, LevelSize.H) - y0;
        return new RectI(x0, y0, x0 + w, y0 + h);
    }

    RectI ToCache(RectI r) => new(r.X0 + Origin.X, r.Y0 + Origin.Y, r.X1 + Origin.X, r.Y1 + Origin.Y);

    // ------------------------------------------------------------------------------------------------ depth sources

    /// <summary>
    /// A rect of the cache grid read from the <b>source</b> depth image (depthCache, out, rect):
    /// <c>s = (float)src.dim / (float)cache.dim</c>; source window <c>[cvtt(floor(s·x0)), cvtt(ceil(((float)x0 + (float)w)·s)))</c> ∩ the
    /// image rect; <c>inv = reciprocal(window, 1.0f)</c>; `ImageResample&lt;0, float&gt;` (16.16 nearest) with offset
    /// <c>(double)(s·x0 − floor(s·x0))</c> and scale <c>(double)s</c>; then the reciprocal again into a fresh image.
    /// The reciprocal = per row the first (w/32)·32 pixels <c>((1 − x·r)·r + r)·1.0f</c> with r = `rcpps`, the rest <c>1.0f / x</c>
    /// (neither call aliases). No far clip.
    /// </summary>
    public static float[] FetchSource(DepthImage src, (int W, int H) cacheDims, RectI rect)
    {
        float sx = (float)src.W / (float)cacheDims.W, sy = (float)src.H / (float)cacheDims.H;
        int rw = rect.X1 - rect.X0, rh = rect.Y1 - rect.Y0;
        float fx0 = (float)rect.X0, fy0 = (float)rect.Y0;
        float px = sx * fx0, py = sy * fy0;
        float flx = MathF.Floor(px), fly = MathF.Floor(py);
        double offX = (double)(px - flx), offY = (double)(py - fly);
        float cex = MathF.Ceiling((fx0 + (float)rw) * sx), cey = MathF.Ceiling((fy0 + (float)rh) * sy);
        int vx0 = Math.Max(src.Rect.X0, DofModel.Cvtt(flx)), vy0 = Math.Max(src.Rect.Y0, DofModel.Cvtt(fly));
        int vx1 = Math.Min(src.Rect.X1, DofModel.Cvtt(cex)), vy1 = Math.Min(src.Rect.Y1, DofModel.Cvtt(cey));
        if (vx1 <= vx0 || vy1 <= vy0 || rw <= 0 || rh <= 0) throw new InvalidOperationException("empty image!");
        int vw = vx1 - vx0, vh = vy1 - vy0;
        var inv = new float[(long)vw * vh];
        for (int y = 0; y < vh; y++) Invert(src.Data, src.Offset + (long)(vy0 + y) * src.Stride + vx0, inv, (long)y * vw, vw);
        var res = ImageResample0.Run(new DepthImage(inv, vw, vh, vw, new RectI(0, 0, vw, vh)), rw, rh, offX, offY, (double)sx, (double)sy);
        var o = new float[(long)rw * rh];
        for (int y = 0; y < rh; y++) Invert(res.Data, (long)y * rw, o, (long)y * rw, rw);
        return o;
    }

    /// <summary>One row of the reciprocal with scale 1.0f, source and destination distinct.</summary>
    static void Invert(float[] s, long so, float[] d, long dofs, int w)
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

    float[] Fetch(RectI levelRect) => FetchSource(_source, (_cache.Current.W, _cache.Current.H), ToCache(levelRect));

    /// <summary>A copy of the tile's newest version, or the source depth when it has none.</summary>
    public float[] TileDepth(int tx, int ty)
    {
        var v = _tiles[tx, ty];
        return v.Count == 0 ? Fetch(TileRect(tx, ty)) : (float[])v[^1].Depth.Clone();
    }

    /// <summary>Append a version to a tile; "Adding tile of incorrect size!", "trying to add an older tile in DepthTileCache".</summary>
    DepthTileVersion AddTile(int tx, int ty, int w, int h, uint version)
    {
        var r = TileRect(tx, ty);
        if (w != r.Width || h != r.Height) throw new InvalidOperationException("Adding tile of incorrect size!");
        var v = _tiles[tx, ty];
        if (v.Count != 0 && !(v[^1].Version < version)) throw new InvalidOperationException("trying to add an older tile in DepthTileCache");
        var t = new DepthTileVersion { Version = version, Tx = tx, Ty = ty, W = w, H = h };
        v.Add(t);
        return t;
    }

    // ------------------------------------------------------------------------------------------------ write-back

    /// <summary>
    /// Lumen's depth write-back (renderer request 0xc):
    /// the block is copied into the cache image at rect ∩ image ("Edited depth has incorrect size!"
    /// when the sizes differ; an empty block is skipped), and when both layer blocks are present the edit layer is written.
    /// <paramref name="rect"/> is in cache coordinates.
    /// </summary>
    void SetEditedDepth(float[]? depth, float[]? background, byte[]? mask, RectI rect)
    {
        var img = _cache.Current; int bw = rect.X1 - rect.X0, bh = rect.Y1 - rect.Y0;
        if (depth != null && bw > 0 && bh > 0)
        {
            int x0 = Math.Max(img.Rect.X0, rect.X0), y0 = Math.Max(img.Rect.Y0, rect.Y0), x1 = Math.Min(img.Rect.X1, rect.X1), y1 = Math.Min(img.Rect.Y1, rect.Y1);
            int cw = x1 <= x0 || y1 <= y0 ? 0 : x1 - x0, ch = cw == 0 ? 0 : y1 - y0;
            if (cw != bw || ch != bh) throw new InvalidOperationException("Edited depth has incorrect size!");
            for (int y = 0; y < bh; y++) Array.Copy(depth, (long)y * bw, img.Data, img.Offset + (long)(y0 + y) * img.Stride + x0, bw);
        }
        if (background != null && mask != null && bw > 0 && bh > 0) Layer.Write(rect, background, mask);
        DepthChanged?.Invoke(rect);
    }

    /// <summary>Push the ROI block, advance the cursor, and enforce the history limit — with more than 20 rects the oldest
    /// is dropped, base rises and <see cref="PruneBelowBase"/> drops the tile versions below the new floor.</summary>
    public void Push(float[]? depth, float[]? background, byte[]? mask, RectI roi)
    {
        SetEditedDepth(depth, background, mask, ToCache(roi));
        Cursor++;
        if (_rois.Count > MaxHistory)
        {
            _rois.RemoveAt(0); Base++;
            PruneBelowBase();
        }
    }

    /// <summary>In every tile with two or more versions, keep = the newest version ≤ base among versions[1…] (0 when
    /// none); the leading versions older than keep are erased while more than one remains.</summary>
    void PruneBelowBase()
    {
        foreach (var v in _tiles)
        {
            if (v.Count < 2) continue;
            uint keep = v[1].Version; if (Base < keep) keep = 0;
            for (int k = 2; k < v.Count; k++)
            {
                uint u = v[k].Version, t = u;
                if (Base < u) t = keep;
                if (u < keep) t = keep;
                keep = t;
            }
            while (v.Count > 1 && v[0].Version < keep) v.RemoveAt(0);
        }
    }

    /// <summary>Discard the redo history — tile versions newer than the cursor, rects past it.</summary>
    void TruncateRedo()
    {
        if ((ulong)Cursor >= (ulong)_rois.Count + Base) return;
        foreach (var v in _tiles) while (v.Count != 0 && v[^1].Version > Cursor) v.RemoveAt(v.Count - 1);
        _rois.RemoveRange((int)(Cursor - Base), _rois.Count - (int)(Cursor - Base));
    }

    /// <summary>Copy <paramref name="src"/> (dense, covering <paramref name="srcRect"/>) ∩ <paramref name="dstRect"/> into
    /// <paramref name="dst"/> (dense, covering <paramref name="dstRect"/>) — Lumen's sub-view copies (executor A, redo, gather).</summary>
    static void CopyRect<T>(T[] src, RectI srcRect, T[] dst, RectI dstRect)
    {
        int x0 = Math.Max(srcRect.X0, dstRect.X0), y0 = Math.Max(srcRect.Y0, dstRect.Y0), x1 = Math.Min(srcRect.X1, dstRect.X1), y1 = Math.Min(srcRect.Y1, dstRect.Y1);
        if (x1 <= x0 || y1 <= y0) return;
        int sw = srcRect.X1 - srcRect.X0, dw = dstRect.X1 - dstRect.X0;
        for (int y = y0; y < y1; y++)
            Array.Copy(src, (long)(y - srcRect.Y0) * sw + (x0 - srcRect.X0), dst, (long)(y - dstRect.Y0) * dw + (x0 - dstRect.X0), x1 - x0);
    }

    /// <summary>The block of <paramref name="src"/> (dense, covering <paramref name="srcRect"/>) inside <paramref name="r"/>, or null when
    /// they do not intersect (Lumen: an empty image).</summary>
    static T[]? Crop<T>(T[]? src, RectI srcRect, RectI r, out RectI got)
    {
        got = new RectI(Math.Max(srcRect.X0, r.X0), Math.Max(srcRect.Y0, r.Y0), Math.Min(srcRect.X1, r.X1), Math.Min(srcRect.Y1, r.Y1));
        if (src == null || got.X1 <= got.X0 || got.Y1 <= got.Y0) return null;
        var o = new T[(long)got.Width * got.Height];
        CopyRect(src, srcRect, o, got);
        return o;
    }

    // ------------------------------------------------------------------------------------------------ executors

    /// <summary>
    /// Executor A (brush, adaptive brush, lasso): truncate redo; append the command's pixel ROI; for every tile of its
    /// range take the tile's current depth, run the command on the <b>whole tile</b>, store the result as version cursor + 1 and copy
    /// tile ∩ ROI into the ROI block; push the block. The command's return value is ignored: every tile of the range gets
    /// a version.
    /// </summary>
    void ExecutePerTile(DepthEditCommand cmd)
    {
        TruncateRedo();
        var roi = cmd.PixelRoi(LevelSize);
        if (roi.X1 < roi.X0 || roi.Y1 < roi.Y0) throw new ArgumentException("the stroke lies outside the depth map");
        _rois.Add(roi);
        var (tx0, ty0, tx1, ty1) = TileRange(roi);
        var block = new float[(long)roi.Width * roi.Height];
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty);
                var depth = TileDepth(tx, ty);
                float[]? guide = null;
                if (cmd.NeedsGuide)
                {
                    if (GuideProvider == null) throw new InvalidOperationException("this tool needs a colour guide provider");
                    guide = GuideProvider(ToCache(rect));
                }
                cmd.Execute(depth, guide, rect, LevelSize);
                var t = AddTile(tx, ty, rect.Width, rect.Height, Cursor + 1);
                t.Depth = depth;
                CopyRect(depth, rect, block, roi);
            }
        Push(block, null, null, roi);
    }

    /// <summary>
    /// The gather: the depth (and, for the edge heal, layer mask + background depth) of the <b>whole tiles</b> the ROI touches,
    /// as one image. Returns the tile-aligned rect; the layer images are allocated (zero-filled) only when some tile's newest version
    /// carries a mask, and filled from the tiles that do.
    /// </summary>
    public RectI Gather(RectI roi, bool wantLayers, out float[] depth, out byte[]? mask, out float[]? background)
    {
        var (tx0, ty0, tx1, ty1) = TileRange(roi);
        var first = TileRect(tx0, ty0); int w = 0, h = 0;
        for (int tx = tx0; tx <= tx1; tx++) w += TileRect(tx, ty0).Width;
        for (int ty = ty0; ty <= ty1; ty++) h += TileRect(tx0, ty).Height;
        var full = new RectI(first.X0, first.Y0, first.X0 + w, first.Y0 + h);
        depth = new float[(long)w * h]; mask = null; background = null;
        if (wantLayers)
            for (int tx = tx0; tx <= tx1 && mask == null; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    var v = _tiles[tx, ty];
                    if (v.Count != 0 && v[^1].Mask != null) { mask = new byte[(long)w * h]; background = new float[(long)w * h]; break; }
                }
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty);
                CopyRect(TileDepth(tx, ty), rect, depth, full);
                var v = _tiles[tx, ty];
                if (mask != null && v.Count != 0 && v[^1].Mask != null)
                {
                    CopyRect(v[^1].Mask!, rect, mask, full);
                    if (v[^1].Background != null) CopyRect(v[^1].Background!, rect, background!, full);
                }
            }
        return full;
    }

    /// <summary>The layered form a command may produce (edge heal): the depth is edited in place; the layer images are
    /// allocated by the command when the tiles had none.</summary>
    public delegate bool LayeredExecute(float[] depth, ref float[]? background, ref byte[]? mask, float[]? guide, RectI rect, (int W, int H) level);

    /// <summary>
    /// Executor B (surface heal, edge heal): append the pixel ROI; gather the whole tiles under it (the gather above,
    /// which also <b>replaces the working rect by the tile-aligned one</b>); fetch the colour guide of that rect when the command needs it;
    /// run the command once on the gathered image; on success store every tile as version cursor + 1 (with the layer blocks when the
    /// command produced them) and push the ROI part. On failure nothing is stored and the cursor stays, but the rect remains in the list.
    /// The caller truncates the redo history first.
    /// </summary>
    public bool ExecuteOnRoi(DepthEditCommand cmd, LayeredExecute? layered = null)
    {
        var roi = cmd.PixelRoi(LevelSize);
        if (roi.X1 < roi.X0 || roi.Y1 < roi.Y0) throw new ArgumentException("the stroke lies outside the depth map");
        _rois.Add(roi);
        var full = Gather(roi, layered != null, out var depth, out var mask, out var background);
        float[]? guide = null;
        if (cmd.NeedsGuide)
        {
            if (GuideProvider == null) throw new InvalidOperationException("this tool needs a colour guide provider");
            guide = GuideProvider(ToCache(full));
        }
        bool ok = layered != null ? layered(depth, ref background, ref mask, guide, full, LevelSize) : cmd.Execute(depth, guide, full, LevelSize);
        if (!ok) return false;
        bool layers = layered != null && background != null;
        var (tx0, ty0, tx1, ty1) = TileRange(full);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty);
                var t = AddTile(tx, ty, rect.Width, rect.Height, Cursor + 1);
                t.Depth = Crop(depth, full, rect, out _)!;
                if (layers) { t.Background = Crop(background, full, rect, out _); t.Mask = Crop(mask, full, rect, out _); }
            }
        var last = _rois[^1];
        Push(Crop(depth, full, last, out _), layers ? Crop(background, full, last, out _) : null, layers ? Crop(mask, full, last, out _) : null, last);
        return true;
    }

    // ------------------------------------------------------------------------------------------------ public tools (CIAPI::DepthEditor)

    static void CheckPoints(IReadOnlyList<EditPoint> points, string error) { if (points.Count < 2) throw new ArgumentException(error); }

    /// <summary>`pushBrushDepthEdit`: ≥ 2 points, 0 &lt; radius &lt; 1 (fraction of the level width), depth &gt; 100 mm.
    /// The adaptive form samples its reference colour at the first point (<see cref="ReferenceColour"/>).</summary>
    public void PushBrush(IReadOnlyList<EditPoint> points, float radius, float depth, bool adaptive = false)
    {
        if (!(points.Count >= 2 && radius < 1.0f && 0.0f < radius && 100.0f < depth)) throw new ArgumentException("Invalid BrushDepthEditingParams!");
        if (!adaptive) { ExecutePerTile(new DepthBrushCommand(points, radius, depth)); return; }
        var p = PointMapper != null ? PointMapper(points[0].X, points[0].Y) : (points[0].X, points[0].Y);
        ExecutePerTile(new DepthBrushAdaptiveCommand(points, ReferenceColour(p.Item1, p.Item2), depth, radius, Lut));
    }

    /// <summary>`pushLassoDepthEdit`: ≥ 2 points, 0 ≤ feather ≤ 1, depth &gt; 100 mm.</summary>
    public void PushLasso(IReadOnlyList<EditPoint> points, float depth, float feather)
    {
        if (!(points.Count >= 2 && 0.0f <= feather && feather <= 1.0f && 100.0f < depth)) throw new ArgumentException("Invalid LassoDepthEditingParams!");
        ExecutePerTile(new DepthLassoCommand(points, depth, feather));
    }

    /// <summary>`pushSurfaceHealDepthEdit`: ≥ 2 points, 0 &lt; radius &lt; 1.</summary>
    public void PushSurfaceHeal(IReadOnlyList<EditPoint> points, float radius)
    {
        if (!(points.Count >= 2 && radius < 1.0f && 0.0f < radius)) throw new ArgumentException("Invalid HealDepthEditingParams!");
        TruncateRedo();
        ExecuteOnRoi(new SurfaceHealCommand(points, radius));
    }

    /// <summary>The matting step of the edge heal; defaults to the port of `MattingLaplacianTiling`.</summary>
    public EdgeHeal.Matting Matting { get; set; } = MattingLaplacian.Run;

    /// <summary>`pushEdgeHealDepthEdit`: ≥ 2 points, 0 &lt; radius &lt; 1. <paramref name="lo"/> / <paramref name="hi"/> = the
    /// in-focus depth interval around the renderer's current focus depth, <see cref="DofModel.CurrentInFocusRange"/> with (2.2, 0.7)
    /// (the DOFCache's focus depth is 0 until a render has copied it there, and the heal then does nothing).
    /// Returns false when the refinement was abandoned (the edit rect stays in the history, as in Lumen).</summary>
    public bool PushEdgeHeal(IReadOnlyList<EditPoint> points, float radius, float lo, float hi)
    {
        if (!(points.Count >= 2 && radius < 1.0f && 0.0f < radius)) throw new ArgumentException("Invalid HealDepthEditingParams!");
        TruncateRedo();
        var cmd = new EdgeHealCommand(points, radius, lo, hi, Matting);
        return ExecuteOnRoi(cmd, cmd.ExecuteLayered);
    }

    /// <summary>Redo truncation for tools that call <see cref="ExecuteOnRoi"/> themselves (the edge heal entry).</summary>
    public void DiscardRedo() => TruncateRedo();

    /// <summary>Building blocks for tools that run their own tile loop (quick select): append an edit rect to the history
    /// …</summary>
    public void AppendRoi(RectI roi) => _rois.Add(roi);

    /// <summary>… and store a tile as version cursor + 1. Finish with <see cref="Push"/>.</summary>
    public DepthTileVersion StoreTile(int tx, int ty, float[] depth, float[]? background = null, byte[]? mask = null)
    {
        var r = TileRect(tx, ty);
        var t = AddTile(tx, ty, r.Width, r.Height, Cursor + 1);
        t.Depth = depth; t.Background = background; t.Mask = mask;
        return t;
    }

    /// <summary>
    /// The mean colour (editor, out, point, level 1) of the window <c>[x − 1, x + 2) × [y − 1, y + 2)</c> ∩ the level,
    /// <c>x = cvtt((float)levelW · px)</c> with the dims of PipelineCache level 1 and the point normalised to the canvas
    /// (<see cref="PointMapper"/>), of the reference provider's image; the four sums are multiplied by `rcpps((float)count)` refined
    /// with one Newton step.
    /// </summary>
    public float[] ReferenceColour(float px, float py)
    {
        if (ReferenceProvider == null) throw new InvalidOperationException("the adaptive brush needs a reference colour provider");
        int w = ColourLevelDims.W, h = ColourLevelDims.H;
        int x = DofModel.Cvtt((float)w * px), y = DofModel.Cvtt((float)h * py);
        int x0 = x - 1, y0 = y - 1, x1 = x + 2, y1 = y + 2;
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (w < x1) x1 = w;
        if (h < y1) y1 = h;
        var win = ReferenceProvider(new RectI(x0, y0, x1, y1));
        float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f; int n = win.Length / 4;
        for (int i = 0; i < n; i++) { s0 += win[i * 4]; s1 += win[i * 4 + 1]; s2 += win[i * 4 + 2]; s3 += win[i * 4 + 3]; }
        float c = (float)n, r = IntelApprox.Rcp(c);
        float k = (1.0f - c * r) * r + r;
        return new[] { k * s0, k * s1, k * s2, k * s3 };
    }

    /// <summary>`getDepthAtPoint`: the 3×3 mean of the cache image at the mapped point.</summary>
    public float DepthAtPoint(float px, float py)
    {
        var p = PointMapper != null ? PointMapper(px, py) : (px, py);
        return DofModel.DepthAtPoint(_cache.Current, p.Item1, p.Item2);
    }

    // ------------------------------------------------------------------------------------------------ undo / redo / reset

    /// <summary>
    /// `undoDepthEdit`: false at the floor. Takes the rect of the current edit; if the version being undone in the
    /// <b>first</b> tile of its range carries layers, that rect is removed from the edit layer. Then per tile:
    /// the newest version below the cursor is pushed, clipped to tile ∩ rect — or, when the tile has
    /// none, the <b>whole tile</b> is re-read from the source depth and pushed; with layers, every older layered
    /// version's layer blocks are pushed again, newest first. Versions are kept for redo.
    /// </summary>
    public bool Undo()
    {
        if (Cursor == Base) return false;
        var roi = _rois[(int)(Cursor - 1 - Base)];
        var (tx0, ty0, tx1, ty1) = TileRange(roi);
        bool layers = false;
        var fv = _tiles[tx0, ty0];
        for (int i = fv.Count - 1; i >= 0; i--)
            if (fv[i].Version == Cursor)
            {
                if (fv[i].HasLayers) { Layer.Remove(ToCache(roi)); layers = true; }
                break;
            }
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty); var v = _tiles[tx, ty];
                int prev = -1;
                for (int i = v.Count - 1; i >= 0; i--) if (v[i].Version < Cursor) { prev = i; break; }
                if (prev >= 0)
                {
                    var d = Crop(v[prev].Depth, rect, roi, out var got);
                    SetEditedDepth(d, null, null, ToCache(got));
                    if (!layers) continue;
                }
                else SetEditedDepth(Fetch(rect), null, null, ToCache(rect));
                if (!layers) continue;
                for (int i = v.Count - 1; i >= 0; i--)
                    if (v[i].Version < Cursor && v[i].HasLayers)
                    {
                        var b = Crop(v[i].Background, rect, roi, out var got); var m = Crop(v[i].Mask, rect, roi, out _);
                        SetEditedDepth(null, b, m, ToCache(got));
                    }
            }
        Cursor--;
        return true;
    }

    /// <summary>
    /// `redoDepthEdit`: false when nothing was undone. The rect at the cursor is reassembled (zero-filled, then
    /// tile ∩ rect of each tile's version cursor + 1; "Did not find a tile to REDO!", "Depth tile types are different")
    /// and pushed; the cursor advances.
    /// </summary>
    public bool Redo()
    {
        if (!CanRedo) return false;
        var roi = _rois[(int)(Cursor - Base)];
        var (tx0, ty0, tx1, ty1) = TileRange(roi);
        int w = roi.Width, h = roi.Height;
        float[]? depth = null, background = null; byte[]? mask = null; bool layered = false;
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty);
                var t = _tiles[tx, ty].Find(q => q.Version == Cursor + 1) ?? throw new InvalidOperationException("Did not find a tile to REDO!");
                if (depth == null)
                {
                    depth = new float[(long)w * h]; layered = t.HasLayers;
                    if (layered) { background = new float[(long)w * h]; mask = new byte[(long)w * h]; }
                }
                if (layered != t.HasLayers) throw new InvalidOperationException("Depth tile types are different");
                CopyRect(t.Depth, rect, depth, roi);
                if (layered)
                {
                    if (t.Background != null) CopyRect(t.Background, rect, background!, roi);
                    if (t.Mask != null) CopyRect(t.Mask, rect, mask!, roi);
                }
            }
        SetEditedDepth(depth, background, mask, ToCache(roi));
        Cursor++;
        return true;
    }

    /// <summary>`reset`: cursor and base to 0, the rect list and every tile version dropped, the <b>whole level window</b>
    /// of the cache rewritten from the source depth (the source read of (origin, origin + size)), the edit layer cleared.</summary>
    public void Reset()
    {
        Cursor = 0; Base = 0; _rois.Clear();
        foreach (var v in _tiles) v.Clear();
        var all = new RectI(0, 0, LevelSize.W, LevelSize.H);
        SetEditedDepth(Fetch(all), null, null, ToCache(all));
        Layer.Remove(default);
    }

    /// <summary>The depth-ready step — run when the depth becomes available again (after a saved state was loaded): every tile's single
    /// version is written into the cache and, when it has layers, into the edit layer; "there should be only one edit per tile".</summary>
    public bool ReapplyTiles()
    {
        bool any = false;
        for (int tx = 0; tx < Nx; tx++)
            for (int ty = 0; ty < Ny; ty++)
            {
                var v = _tiles[tx, ty];
                if (v.Count == 0) continue;
                if (v.Count != 1) throw new InvalidOperationException("there should be only one edit per tile");
                var rect = ToCache(TileRect(v[0].Tx, v[0].Ty));
                if (v[0].Depth.Length != 0) SetEditedDepth(v[0].Depth, null, null, rect);
                if (v[0].HasLayers) Layer.Write(rect, v[0].Background, v[0].Mask);
                any = true;
            }
        return any;
    }

    // ------------------------------------------------------------------------------------------------ quick select

    /// <summary>The index of the edited level in the renderer's level tables.</summary>
    public int Level { get; init; } = 1;
    /// <summary>The export-window dims of every level, and their
    /// origins in the pipeline-level frames. Quick select addresses its ROI on any of them.</summary>
    public (int W, int H)[]? LevelDims { get; init; }
    public (int X, int Y)[]? LevelOrigins { get; init; }
    /// <summary>The colour ROI of quick select: vec4 float pixels of a rect of the given level, in that level's pipeline frame
    /// (level rect + <see cref="LevelOrigins"/>[level]). In Lumen: the PipelineCache's resampling ROI render at the pipeline dims of the
    /// level, followed by the colour conversion (the settings of <see cref="GuideProvider"/>).</summary>
    public Func<int, RectI, float[]>? LevelGuideProvider { get; set; }
    /// <summary>Lumen seeds quick select's k-means from `std::random_device`, so its mask is not reproducible from run to run; this
    /// delegate stands for the device (four calls per k-means run: the 2nd and the 4th value are the seeds).</summary>
    public Func<uint> RandomDevice { get; set; } = () => BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
    /// <summary>Keep the transformed wavelet level images of the quick-select pyramid (verification only).</summary>
    public bool QuickKeepPyramidImages { get; set; }

    QuickSelect? _quick; RectI _quickRoi; float _quickScale;
    /// <summary>The quick-select object (null before the first stroke and after <see cref="ResetQuickSelect"/>).</summary>
    public QuickSelect? Quick => _quick;

    /// <summary>
    /// `addQuickSelectStrokes`. <paramref name="rect"/> = the ROI in pixels of <paramref name="level"/>'s export window
    /// (x0, y0, x1, y1 of QuickSelectDepthEditingParams), <paramref name="radius"/> a fraction of the working width,
    /// <paramref name="positive"/> = the params' positive flag. Valid: ≥ 2 points, 0 &lt; radius &lt; 1, x0 ≥ 0, y0 ≥ 0, x1 ≤ level width, y1 ≤ level height
    /// ("Invalid QuickSelectDepthEditingParams!").
    /// A new ROI (compared with the cached one) loads the guide: <c>scale = (float)editLevelW / (float)strokeLevelW</c>; the
    /// colour ROI of the stroke level; when scale &lt; 1 (stroke level finer than the edited one) it is resampled to
    /// <c>(cvtt(w·scale), cvtt(h·scale))</c>, the working rect becomes <c>(cvtt(x0·scale), cvtt(y0·scale)) + that size</c> and the
    /// scale handed on is 1. Points become working pixels <c>cvtt(dimW·p.x − (float)originX)</c> with (dim, origin) = (edited level,
    /// scaled ROI origin) for a finer stroke level, (stroke level, ROI origin) otherwise; brush radius <c>cvtt(dimW · radius)</c>,
    /// hardness 0.9.
    /// </summary>
    public void AddQuickSelectStrokes(IReadOnlyList<EditPoint> points, float radius, RectI rect, int level, bool positive = true, QuickSelectTrace? trace = null)
    {
        var dims = LevelDims ?? throw new InvalidOperationException("quick select needs the level tables");
        if (!(points.Count > 1 && 0.0f < radius && radius < 1.0f && rect.X0 > -1 && rect.Y0 > -1 && level >= 0 && level < dims.Length
              && rect.X1 <= dims[level].W && rect.Y1 <= dims[level].H))
            throw new ArgumentException("Invalid QuickSelectDepthEditingParams!");
        if (_quick == null) { _quick = new QuickSelect(LevelSize, () => RandomDevice()) { KeepPyramidImages = QuickKeepPyramidImages }; _quickScale = 0f; _quickRoi = default; }
        _quick.Cancel = QuickCancel;
        QuickCancel.ThrowIfCancellationRequested();
        if (rect != _quickRoi)
        {
            float scale = (float)dims[Level].W / (float)dims[level].W;
            _quickRoi = rect; _quickScale = scale;
            if (LevelGuideProvider == null) throw new InvalidOperationException("quick select needs a colour guide provider");
            var org = LevelOrigins![level];
            int gw = rect.X1 - rect.X0, gh = rect.Y1 - rect.Y0;
            var guide = LevelGuideProvider(level, new RectI(rect.X0 + org.X, rect.Y0 + org.Y, rect.X1 + org.X, rect.Y1 + org.Y));
            QuickCancel.ThrowIfCancellationRequested();
            var work = rect;
            if (scale < 1.0f)
            {
                int w = EditMath.Cvtt((float)(rect.X1 - rect.X0) * scale), h = EditMath.Cvtt((float)(rect.Y1 - rect.Y0) * scale);
                int x0 = EditMath.Cvtt((float)rect.X0 * scale), y0 = EditMath.Cvtt((float)rect.Y0 * scale);
                work = new RectI(x0, y0, x0 + w, y0 + h);
                guide = QuickSelect.ResampleGuide(guide, gw, gh, w, h); gw = w; gh = h;
                scale = 1.0f;
            }
            trace?.OnGuide?.Invoke(guide, gw, gh);
            _quick.Init(guide, gw, gh, work, scale);
        }
        (int W, int H) d; int ox, oy;
        if (level < Level) { d = dims[Level]; ox = EditMath.Cvtt((float)_quickRoi.X0 * _quickScale); oy = EditMath.Cvtt((float)_quickRoi.Y0 * _quickScale); }
        else { d = dims[level]; ox = _quickRoi.X0; oy = _quickRoi.Y0; }
        var px = new (int X, int Y)[points.Count];
        for (int i = 0; i < px.Length; i++)
            px[i] = (EditMath.Cvtt((float)d.W * points[i].X - (float)ox), EditMath.Cvtt((float)d.H * points[i].Y - (float)oy));
        _quick.AddStrokes(px, EditMath.Cvtt((float)d.W * radius), 0.9f, positive, trace);
    }

    /// <summary>`resetQuickSelect`: the object is dropped; the cached ROI is only reset by the next stroke.</summary>
    public void ResetQuickSelect() => _quick = null;

    /// <summary>`clearQuickSelectMask`: mask and seeds zeroed, the working rects reset to the whole
    /// level — while the guide and the cached ROI stay. Lumen then runs the next stroke of the <i>same</i> ROI on the whole level with
    /// the old guide (reads outside its buffers); here that stroke throws. A stroke with a different ROI reloads the guide.</summary>
    public void ClearQuickSelectMask() => (_quick ?? throw new InvalidOperationException("no quick selection")).Clear();

    /// <summary>`quickSelectMask`: the selection as a byte image of the edited level's size (CIAPI pixel format 2;
    /// 0 / 1), or null when there is no quick-select object.</summary>
    public byte[]? QuickSelectMask => _quick?.Mask;

    /// <summary>
    /// `pushQuickSelectDepthEdit(depth)`: discard redo; the edit rect is the bounding box of the
    /// non-zero mask pixels ("No tiles in ROI!" for an empty mask); per tile of its range the tile's depth becomes
    /// <c>mask &gt; 0 ? depth : old</c> inside the rect, is stored whole as version cursor + 1 and
    /// its part of the rect goes into the pushed block. The value is not validated.
    /// </summary>
    public void PushQuickSelect(float depth)
    {
        TruncateRedo();
        var qs = _quick ?? throw new InvalidOperationException("no quick selection");
        int w = qs.LevelW, h = qs.LevelH, x0 = w - 1, y0 = h - 1, x1 = 0, y1 = 0; var m = qs.Mask;
        for (int y = 0; y < h; y++)
        {
            long r = (long)y * w;
            for (int x = 0; x < w; x++)
                if (m[r + x] != 0)
                {
                    if (x1 <= x) x1 = x;
                    if (y1 <= y) y1 = y;
                    if (x <= x0) x0 = x;
                    if (y <= y0) y0 = y;
                }
        }
        var roi = new RectI(x0, y0, x1 + 1, y1 + 1);
        var (tx0, ty0, tx1, ty1) = TileRange(roi);
        _rois.Add(roi);
        var block = new float[(long)roi.Width * roi.Height];
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var rect = TileRect(tx, ty); var d = TileDepth(tx, ty);
                var clip = rect.Intersect(roi);
                for (int y = clip.Y0; y < clip.Y1; y++)
                    for (int x = clip.X0; x < clip.X1; x++)
                        if (m[(long)y * w + x] > 0) d[(long)(y - rect.Y0) * rect.Width + (x - rect.X0)] = depth;
                var t = AddTile(tx, ty, rect.Width, rect.Height, Cursor + 1);
                t.Depth = d;
                CopyRect(d, rect, block, roi);
            }
        Push(block, null, null, roi);
    }

    /// <summary>Load one flattened tile of a saved state: the tile gets a version 0, so it must
    /// not have a version yet ("trying to add an older tile in DepthTileCache"). A null image stays empty, as for the tile types that
    /// carry no depth or no layers.</summary>
    public DepthTileVersion LoadTile(int tx, int ty, float[]? depth, float[]? background, byte[]? mask)
    {
        var r = TileRect(tx, ty);
        var t = AddTile(tx, ty, r.Width, r.Height, 0);
        t.Depth = depth ?? Array.Empty<float>(); t.Background = background; t.Mask = mask;
        return t;
    }

    /// <summary>
    /// Apply the depth edits of a Lumen session — what Lumen has after reloading it: the record reader followed by the
    /// depth-ready step (<see cref="ReapplyTiles"/>; in Lumen it runs when the capture's depth arrives, here the depth
    /// exists already).
    /// The reader does nothing and reports failure (false) unless the record's tile size equals the editor's and its depth size the edited
    /// level's size. Otherwise every stored tile becomes the tile's <b>version 0</b>, in record order; cursor, base and the edit-rect list
    /// are not touched, so the imported state is the undo floor (nothing to undo, <see cref="Reset"/> discards it). Then each loaded tile
    /// is written whole into the depth cache and, when it has layers, into the edit layer (one layer rect per layered tile).
    /// Lumen loads into a fresh editor; a tile that already has a version makes its reader throw after earlier tiles were added — here
    /// that is checked before anything changes.
    /// </summary>
    public bool ImportLumenState(LumenDepthEditorState state)
    {
        if (state.Tiles.Count < 1) return false;
        if (state.TileSize != (TileW, TileH) || state.DepthSize != LevelSize) return false;
        var seen = new HashSet<(int, int)>();
        foreach (var t in state.Tiles)
        {
            if (t.Tx < 0 || t.Ty < 0 || t.Tx >= Nx || t.Ty >= Ny) throw new InvalidOperationException("Adding tile of incorrect size!");
            if (_tiles[t.Tx, t.Ty].Count != 0 || !seen.Add((t.Tx, t.Ty))) throw new InvalidOperationException("trying to add an older tile in DepthTileCache");
            var r = TileRect(t.Tx, t.Ty);
            if (t.W != r.Width || t.H != r.Height) throw new InvalidOperationException("Adding tile of incorrect size!");
        }
        foreach (var t in state.Tiles)
            LoadTile(t.Tx, t.Ty, t.Depth == null ? null : (float[])t.Depth.Clone(), t.Background == null ? null : (float[])t.Background.Clone(),
                     t.Mask == null ? null : (byte[])t.Mask.Clone());
        ReapplyTiles();
        return true;
    }

    // ------------------------------------------------------------------------------------------------ persistence hooks (Lux's own store)
    // Nothing below exists in Lumen: Lumen saves a flattened state only (see ImportLumenState). Lux.Store uses these to put a saved
    // snapshot back, to rebuild the history by replaying the operation log on a twin editor, and to take the twin's state over.

    /// <summary>Put one saved tile back with the version number it had (<see cref="LoadTile"/> with a version). The tile must be empty.</summary>
    public DepthTileVersion RestoreTile(int tx, int ty, uint version, float[]? depth, float[]? background, byte[]? mask)
    {
        if (_tiles[tx, ty].Count != 0) throw new InvalidOperationException("trying to add an older tile in DepthTileCache");
        var r = TileRect(tx, ty);
        var t = AddTile(tx, ty, r.Width, r.Height, version);
        t.Depth = depth ?? Array.Empty<float>(); t.Background = background; t.Mask = mask;
        return t;
    }

    /// <summary>The cursor of a restored snapshot. The snapshot carries no history, so the undo floor is the cursor itself (as after
    /// <see cref="ImportLumenState"/>) until the history has been rebuilt and taken over with <see cref="AdoptState"/>.</summary>
    public void RestoreCursor(uint cursor) { Cursor = cursor; Base = cursor; _rois.Clear(); }

    /// <summary>A second editor with this one's geometry, colour providers and settings on another depth cache / source depth, in the
    /// initial state — the stage an operation log is replayed on.</summary>
    public DepthEditor Fork(DepthImageCache cache, DepthImage source) => new(cache, source, LevelSize, Origin)
    {
        Level = Level, LevelDims = LevelDims, LevelOrigins = LevelOrigins, ColourLevelDims = ColourLevelDims, PointMapper = PointMapper,
        GuideProvider = GuideProvider, ReferenceProvider = ReferenceProvider, LevelGuideProvider = LevelGuideProvider,
        RandomDevice = RandomDevice, Matting = Matting, QuickKeepPyramidImages = QuickKeepPyramidImages,
    };

    /// <summary>Take over the whole state of <paramref name="from"/> (an editor of the same geometry, normally a <see cref="Fork"/> the
    /// log was replayed on): cache pixels, tile versions, edit rects, cursor, floor, edit layer and the quick-select object.
    /// <see cref="DepthChanged"/> is not raised — the caller adopts only a state whose pixels it has verified to equal the present ones.</summary>
    public void AdoptState(DepthEditor from)
    {
        if (from.LevelSize != LevelSize || from.Origin != Origin || from.Nx != Nx || from.Ny != Ny
            || from._cache.Current.W != _cache.Current.W || from._cache.Current.H != _cache.Current.H)
            throw new InvalidOperationException("the two editors have different geometry");
        var s = from._cache.Current; var d = _cache.Current;
        for (int y = 0; y < d.H; y++) Array.Copy(s.Data, s.Offset + (long)y * s.Stride, d.Data, d.Offset + (long)y * d.Stride, d.W);
        for (int x = 0; x < Nx; x++) for (int y = 0; y < Ny; y++) { _tiles[x, y].Clear(); _tiles[x, y].AddRange(from._tiles[x, y]); }
        _rois.Clear(); _rois.AddRange(from._rois);
        Cursor = from.Cursor; Base = from.Base;
        Layer.Mask = from.Layer.Mask; Layer.Background = from.Layer.Background; Layer.Rects.Clear(); Layer.Rects.AddRange(from.Layer.Rects);
        _quick = from._quick; _quickRoi = from._quickRoi; _quickScale = from._quickScale;
        if (_quick != null) _quick.RandomDevice = () => RandomDevice();
    }

    /// <summary>The bookkeeping an operation changes before it writes anything (Lux's own, for stopping a slow tool): the edit-rect list and each
    /// tile's version list. Cursor, floor, the depth cache and the edit layer change only when an operation writes (<see cref="Push"/>, undo,
    /// redo, reset), so a checkpoint taken before an operation and restored after it stopped before writing gives back exactly the editor
    /// before it — redo history included.</summary>
    public sealed class Checkpoint
    {
        internal RectI[] Rois = Array.Empty<RectI>();
        internal DepthTileVersion[]?[,] Lists = new DepthTileVersion[0, 0][]; internal uint Cursor, Base;
        internal QuickSelect? Quick; internal (float[]? Grey, float[]? Lab, EawPyramid? Pyramid, int W, int H, RectI Work, RectI Level, int State)? QuickState;
        internal RectI QuickRoi; internal float QuickScale;
    }

    /// <summary>Stops a running quick-select stroke (the persistence then rolls the editor back with its checkpoint).</summary>
    public CancellationToken QuickCancel { get; set; }

    /// <summary>Take a <see cref="Checkpoint"/> (cheap: the rect list and the version lists are copied, the tile images are shared).</summary>
    public Checkpoint TakeCheckpoint()
    {
        var c = new Checkpoint { Rois = _rois.ToArray(), Cursor = Cursor, Base = Base, Lists = new DepthTileVersion[]?[Nx, Ny],
                                 Quick = _quick, QuickState = _quick?.Save(), QuickRoi = _quickRoi, QuickScale = _quickScale };
        for (int x = 0; x < Nx; x++) for (int y = 0; y < Ny; y++) c.Lists[x, y] = _tiles[x, y].ToArray();
        return c;
    }

    /// <summary>Restore a <see cref="Checkpoint"/> taken before an operation that stopped (an <see cref="OperationCanceledException"/> from a
    /// provider) before it wrote anything. Throws when the operation did write (the cursor moved): such a state cannot be rolled back.</summary>
    public void RollBack(Checkpoint c)
    {
        if (c.Cursor != Cursor || c.Base != Base) throw new InvalidOperationException("the operation already wrote its result; it cannot be rolled back");
        _rois.Clear(); _rois.AddRange(c.Rois);
        for (int x = 0; x < Nx; x++) for (int y = 0; y < Ny; y++) { _tiles[x, y].Clear(); _tiles[x, y].AddRange(c.Lists[x, y]!); }
        // a quick-select stroke stopped before it wrote: the object, its guide and the cached region as they were
        _quick = c.Quick; if (c.Quick is not null && c.QuickState is { } qs) c.Quick.Restore(qs);
        _quickRoi = c.QuickRoi; _quickScale = c.QuickScale;
    }

    /// <summary>The newest version of a tile at or below the cursor — the tile's present content — or null when it has none.</summary>
    public DepthTileVersion? CurrentVersion(int tx, int ty)
    {
        var v = _tiles[tx, ty];
        for (int i = v.Count - 1; i >= 0; i--) if (v[i].Version <= Cursor) return v[i];
        return null;
    }
}
