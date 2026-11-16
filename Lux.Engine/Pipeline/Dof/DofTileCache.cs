using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Export;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// The `lt::TileCache&lt;Vec3&lt;Float16&gt;&gt;` base of the DOFCache: tile size (512×512), the level dims
/// (the PipelineCache's own list — level 0 the pipeline canvas, level 1 the module frame, then halves; strictly decreasing or
/// "TileCache pyramid not sorted!") and the tile grid per level: `max(1, (tile/2 + dim) / tile)` per axis, so the last tile of a
/// row or column absorbs a remainder of up to 1.5 tiles.
/// </summary>
public sealed class DofTileGrid
{
    public readonly (int W, int H) TileSize;
    public readonly (int W, int H)[] LevelDims;
    public readonly (int X, int Y)[] Grid;

    public DofTileGrid((int W, int H)[] levelDims, (int W, int H)? tileSize = null)
    {
        TileSize = tileSize ?? (512, 512); LevelDims = levelDims; Grid = new (int, int)[levelDims.Length];
        for (int l = 0; l < levelDims.Length; l++)
        {
            if (l != 0 && (levelDims[l - 1].W <= levelDims[l].W || levelDims[l - 1].H <= levelDims[l].H)) throw new ArgumentException("TileCache pyramid not sorted!");
            Grid[l] = (Math.Max(1, (TileSize.W / 2 + levelDims[l].W) / TileSize.W), Math.Max(1, (TileSize.H / 2 + levelDims[l].H) / TileSize.H));
        }
    }

    /// <summary>The tile's dims (cache, out, tile). A tile in the last column spans `[max(x0, 0), min(levelW, x0 + 2·tileW))`,
    /// any other column `tileW` — except in the last row, where a non-last column spans `[max(x0, 0), min(levelW, x0 + tileW))`; rows alike.</summary>
    public (int W, int H) TileDims(int level, int tx, int ty)
    {
        var (nx, ny) = Grid[level]; var (lw, lh) = LevelDims[level]; var (tw, th) = TileSize;
        bool lastX = tx == nx - 1, lastY = ty == ny - 1;
        if (!lastX && !lastY) return (tw, th);
        int x0 = tx * tw, y0 = ty * th;
        int w = Math.Min(lw, x0 + (lastX ? 2 * tw : tw)) - Math.Max(x0, 0);
        int h = Math.Min(lh, y0 + (lastY ? 2 * th : th)) - Math.Max(y0, 0);
        return (w, h);
    }

    /// <summary>The level rect of a tile.</summary>
    public RectI TileRect(int level, int tx, int ty) { var (w, h) = TileDims(level, tx, ty); return new RectI(tx * TileSize.W, ty * TileSize.H, tx * TileSize.W + w, ty * TileSize.H + h); }
}

/// <summary>
/// Lumen's `lt::DOFCache`: a tile cache whose tiles are the PipelineCache image re-rendered
/// with the synthetic depth of field, stored as round-toward-zero half floats (`Vec3&lt;Float16&gt;`, 6 bytes per pixel).
///
/// A tile is produced by the tile lambda:
/// <code>
///   g      = DofGeometry.Tile(grid, state, depth image, depth level dims, level, tx, ty, device)
///   depth  = DofGeometry.FetchDepth(depth image, g.FetchRect, levelDims[level])   (inverse-depth round trip)
///   colour = PipelineCache renderROI of g.FetchRect at the level
///   buffer = DepthCache edit-layer fetch of g.FetchRect            (no depth edits: an empty DualLayerBuffer — no matte, no occluded depth)
///   DualLayerBuilder.Build(buffer, colour, depth, g.BuilderParams, 1, g.Device, (g.InFocusNear, g.InFocusFar))
///   sub    = buffer.Crop(g.CropRect)
///   blur   = new DefocusBlur(sub, g.BlurRect, g.BlurRect, f, g.LevelPitch, g.SyntheticFNumber, focus, g.Device);  out = blur.Run(post: true)
///   tile   = half(out restricted to g.FinalCrop(out.rect))         ImageConvertPixelType&lt;Vec3&lt;Float16&gt;, vec4x32f&gt;
/// </code>
/// The builder → crop → blur part is <see cref="Blur"/>, supplied by the integrator.
/// </summary>
public sealed class DofTileCache
{
    public DofTileGrid Grid { get; }
    public DofState State { get; }
    public int Device { get; }
    /// <summary>The depth-cache image and the DepthCache's level list. Replaced by
    /// <see cref="SwapDepth"/> (an editor's new depth snapshot).</summary>
    public DepthImageCache Depth => _in.Depth;
    /// <summary>The depth and the edit layer a tile reads, replaced together (a tile takes one consistent pair).</summary>
    sealed record Inputs(DepthImageCache Depth, Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer? Layer)
    {
        // block minima / maxima of the depth for the tiles' depth ranges (the depth is immutable while it is this cache's input)
        readonly Lazy<DepthSummary> _summary = new(() => new DepthSummary(Depth.Current), LazyThreadSafetyMode.ExecutionAndPublication);
        public DepthSummary Summary => _summary.Value;
    }
    volatile Inputs _in;
    public (int W, int H)[] DepthLevelDims { get; }
    /// <summary>`TileCache::renderROI&lt;vec4x32f&gt;` of the PipelineCache: (level, rect in level pixels) → float RGBA, `rect.Width` stride.</summary>
    public Func<int, RectI, float[]> Colour { get; }
    /// <summary>Builder + crop + blur for one tile: (geometry, color of <see cref="DofTileGeometry.FetchRect"/>, depth of the same rect) →
    /// the blur output as float RGBA with its `lt::Image` rect (in the output's own coordinates) and stride in pixels.</summary>
    public Func<DofTileGeometry, float[], DepthImage, (float[] Rgba, RectI Rect, int Stride)> Blur { get; }

    /// <summary>The depth cache's edit layer, when the capture carries depth edits: every tile asks it for the
    /// occluded depth and the matte of its fetch rect and hands them to the builder. Null = no depth edits.</summary>
    public Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer? EditLayer { get => _in.Layer; set => _in = _in with { Layer = value }; }

    // each tile with the geometry it was made with (SwapDepth compares it with the geometry of the new depth)
    TileStore<(int L, int Tx, int Ty), (ushort[] Px, DofTileGeometry Geo, int DepthW, int DepthH)> _tiles = new();

    public DofTileCache(DofTileGrid grid, DofState state, int device, DepthImageCache depth, (int W, int H)[] depthLevelDims,
        Func<int, RectI, float[]> colour, Func<DofTileGeometry, float[], DepthImage, (float[] Rgba, RectI Rect, int Stride)> blur)
    { Grid = grid; State = state; Device = device; _in = new Inputs(depth, null); DepthLevelDims = depthLevelDims; Colour = colour; Blur = blur; }

    /// <summary>
    /// The DOFCache of a loaded capture, as `setInputDataStream` builds it and the renderer's property mapping sets it before a render: the PipelineCache's
    /// level list and color, the depth-cache image, the capture's constants and device type, the requested f-number and the focus depth
    /// (null = DefaultFocusDepth, what the renderer puts into the focus setting when the depth is published).
    /// </summary>
    public static DofTileCache ForCapture(Lux.Engine.Lri.LriFile lri, PipelineCache colour, DepthImageCache depth, float fnum, float? focusDepth)
    {
        var state = new DofState(DofConstants.FromCapture(lri));
        state.Set(fnum, focusDepth ?? DofModel.DefaultFocusDepth(lri, depth.Current));
        return new DofTileCache(new DofTileGrid(colour.LevelDims), state, DofModel.DeviceType(lri), depth, colour.LevelDims, colour.RenderRoi, BuildAndBlur);
    }

    /// <summary>The builder → crop → blur steps of the tile lambda: build the two-layer buffer over the fetch rect (synthesis on), crop it to
    /// the blur rect, blur with the post stage.</summary>
    public static (float[] Rgba, RectI Rect, int Stride) BuildAndBlur(DofTileGeometry g, float[] colour, DepthImage depth)
        => BuildAndBlur(g, colour, depth, null, null);

    /// <summary>The same with the edit-layer part of the tile's buffer (<see cref="Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer.TileBuffer"/>):
    /// the occluded depth and the matte the builder finds already in the buffer; both null without depth edits here.</summary>
    public static (float[] Rgba, RectI Rect, int Stride) BuildAndBlur(DofTileGeometry g, float[] colour, DepthImage depth, float[]? occDepth, float[]? matte)
        => BuildAndBlur(g, colour, depth, occDepth, matte, pooledOutput: false);

    /// <summary>With <paramref name="pooledOutput"/> the returned array is a <see cref="ScratchPool"/> array (possibly longer than the image; the
    /// caller gives it back) and the builder's full-size planes are pooled too (given back here once the blur has read them).</summary>
    static (float[] Rgba, RectI Rect, int Stride) BuildAndBlur(DofTileGeometry g, float[] colour, DepthImage depth, float[]? occDepth, float[]? matte, bool pooledOutput)
    {
        int w = g.FetchRect.Width, h = g.FetchRect.Height;
        var buf = new DualLayerBuffer();
        if (occDepth != null && matte != null) { buf.OccDepth = new DofImage(occDepth, 1, w, h); buf.Matte = new DofImage(matte, 1, w, h); }
        var pooled = pooledOutput ? new List<float[]>() : null;
        using (Diagnostics.PhaseLog.Time("dof.builder"))
            DualLayerBuilder.Build(buf, new DofImage(colour, 4, w, h), new DofImage(depth.ToDense(), 1, w, h), g.Focus, g.FocalLength, g.FocalLength35, g.LevelPitch,
                synth: true, g.Device, g.InFocusNear, g.InFocusFar, null, pooled);
        var blur = new DefocusBlur(buf.Crop(g.CropRect), g.BlurRect, g.BlurRect, g.FocalLength, g.LevelPitch, g.SyntheticFNumber, g.Focus, g.Device);
        var o = blur.Run(true, null, pooledOutput);
        if (pooled != null) foreach (var a in pooled) ScratchPool.Return(a);
        return (pooledOutput ? o.D : o.ToArray(), new RectI(0, 0, o.W, o.H), o.W);
    }

    /// <summary>The built-in builder + blur (<see cref="BuildAndBlur(DofTileGeometry, float[], DepthImage)"/>) as the <see cref="Blur"/> delegate:
    /// then the tile's temporaries can be pooled.</summary>
    static readonly System.Reflection.MethodInfo DefaultBlurMethod = ((Func<DofTileGeometry, float[], DepthImage, (float[], RectI, int)>)BuildAndBlur).Method;

    /// <summary>The level rect a <see cref="Render"/> of <paramref name="rect"/> reads.</summary>
    public RectI SourceRect(int level, RectI rect, (int W, int H) reqDims)
    {
        var (ws, hs) = Grid.LevelDims[level];
        if ((ws, hs) == reqDims) return rect;
        float fx = (float)ws / (float)reqDims.W, fy = (float)hs / (float)reqDims.H;
        return new RectI(Math.Max(0, (int)MathF.Floor(fx * rect.X0 + -2.0f)), Math.Max(0, (int)MathF.Floor(fy * rect.Y0 + -2.0f)),
                         Math.Min(ws, (int)MathF.Ceiling(((float)rect.Width + (float)rect.X0) * fx + 2.0f)), Math.Min(hs, (int)MathF.Ceiling(((float)rect.Height + (float)rect.Y0) * fy + 2.0f)));
    }

    /// <summary>Generate every tile a <see cref="Render"/> of <paramref name="rect"/> reads that is not cached yet, up to
    /// <paramref name="threads"/> at once. A tile is a pure function of the capture, the f-number and the focus depth, memoized once, so the
    /// result does not depend on which thread makes it.</summary>
    public void Prefetch(int level, RectI rect, (int W, int H) reqDims, int threads)
    {
        var r = SourceRect(level, rect, reqDims); var (lw, lh) = Grid.LevelDims[level]; var (tw, th) = Grid.TileSize; var (nx, ny) = Grid.Grid[level];
        r = new RectI(Math.Max(r.X0, 0), Math.Max(r.Y0, 0), Math.Min(r.X1, lw), Math.Min(r.Y1, lh));
        if (r.Width <= 0 || r.Height <= 0) return;
        int tx0 = Math.Min(r.X0 / tw, nx - 1), tx1 = Math.Min((r.X1 - 1) / tw, nx - 1), ty0 = Math.Min(r.Y0 / th, ny - 1), ty1 = Math.Min((r.Y1 - 1) / th, ny - 1);
        var keys = new List<(int, int)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) keys.Add((tx, ty));
        Progress.Begin("depth-effect tiles", keys.Count);
        if (threads <= 1) { foreach (var (tx, ty) in keys) { Tile(level, tx, ty); Progress.Tick(); } return; }
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = threads }, k => { Tile(level, k.Item1, k.Item2); Progress.Tick(); });
    }

    /// <summary>Progress of <see cref="Prefetch"/>: the "depth-effect tiles" phase, one unit per tile.</summary>
    public ProgressReporter Progress { get; set; } = ProgressReporter.None;

    /// <summary>Validate and store (f-number, focus depth); with <paramref name="invalidate"/> every cached tile is dropped.</summary>
    public void SetFocus(float fnum, float focus, bool invalidate = true)
    {
        State.Set(fnum, focus);
        if (invalidate) _tiles = new();
    }

    /// <summary>The geometry of a tile with the cache's current state.</summary>
    public DofTileGeometry Geometry(int level, int tx, int ty) => DofGeometry.Tile(Grid, State, Depth.Current, DepthLevelDims, level, tx, ty, Device);

    /// <summary>The tile lambda for one tile → `w·h·3` halves.</summary>
    public ushort[] Generate(int level, int tx, int ty) => GenerateWithGeometry(level, tx, ty).Px;

    (ushort[] Px, DofTileGeometry Geo, int DepthW, int DepthH) GenerateWithGeometry(int level, int tx, int ty)
    {
        // one consistent pair of inputs for the whole tile (SwapDepth may replace them meanwhile)
        var inputs = _in; var depthCache = inputs.Depth; var layer = inputs.Layer;
        DofTileGeometry g; using (Diagnostics.PhaseLog.Time(() => $"dof.geometry L{level}")) g = DofGeometry.Tile(Grid, State, depthCache.Current, DepthLevelDims, level, tx, ty, Device, inputs.Summary);
        DepthImage depth; using (Diagnostics.PhaseLog.Time(() => $"dof.fetch-depth L{level}")) depth = DofGeometry.FetchDepth(depthCache.Current, g.FetchRect, DepthLevelDims[level]);
        float[] colour; using (Diagnostics.PhaseLog.Time(() => $"dof.colour L{level}")) colour = Colour(level, g.FetchRect);
        var (occ, matte) = layer?.TileBuffer(g.FetchRect, DepthLevelDims[level]) ?? (null, null);
        // a tile with an edit layer goes through the built-in builder (the Blur delegate has no layer inputs)
        float[] rgba; RectI rect; int stride;
        bool builtIn = occ != null || (Blur.Target is null && Blur.Method == DefaultBlurMethod);
        using (Diagnostics.PhaseLog.Time(() => $"dof.build+blur L{level}")) (rgba, rect, stride) = builtIn ? BuildAndBlur(g, colour, depth, occ, matte, pooledOutput: true) : Blur(g, colour, depth);
        ushort[] half; using (Diagnostics.PhaseLog.Time(() => $"dof.half L{level}")) half = ToHalf(rgba, stride, g.FinalCrop(rect), g.TileRect.Width, g.TileRect.Height);
        if (builtIn) ScratchPool.Return(rgba);
        return (half, g, depthCache.Current.W, depthCache.Current.H);
    }

    /// <summary>
    /// Put another depth (and edit layer) in place — a depth editor's new snapshot after an edit — and drop exactly the tiles whose pixels it can
    /// change. A tile is a pure function of the output-relevant part of its geometry (<see cref="DofTileGeometry.SameOutputAs"/>: the rects,
    /// which the depth range around the tile sets through the margin, and the scalars) and of the color, depth and edit layer of its fetch rect.
    /// So a cached tile is kept when (1) its fetch rect (in depth-cache pixels, as <see cref="DofGeometry.FetchDepth"/> and
    /// <see cref="Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer.TileBuffer"/> read it, plus one pixel) does not meet
    /// <paramref name="changedCacheRect"/> and (2) its geometry on the new depth gives the same output — recomputed only when the pixels its
    /// depth range reads (<see cref="DofGeometry.DepthRangeReads"/>) meet the change; otherwise the range, hence the geometry, is the same.
    /// Every other tile is dropped, and so is every tile still being made (it may have read either depth).
    /// <paramref name="changedCacheRect"/> = the depth-cache pixels whose depth or layer differ between the two (null = all: every tile is
    /// dropped). Returns the dropped completed tiles. Keeping or dropping a tile never changes a pixel: a dropped tile is made again from the
    /// new inputs.
    /// </summary>
    public List<(int L, int Tx, int Ty)> SwapDepth(DepthImageCache depth, Lux.Engine.Pipeline.Dof.Edit.DepthEditLayer? layer, RectI? changedCacheRect)
    {
        using var _ = Diagnostics.PhaseLog.Time("dof.swap");
        _in = new Inputs(depth, layer);
        var dropped = new List<(int L, int Tx, int Ty)>();
        var store = _tiles;
        if (changedCacheRect is not { } ch) { foreach (var k in store.CompletedKeys()) dropped.Add(k); _tiles = new(); return dropped; }
        var img = depth.Current; var inputs = _in;
        var keys = store.CompletedKeys().ToList();
        var drop = new bool[keys.Count];
        // independent per tile: in parallel (the geometry scans its window's depth)
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
        {
            var k = keys[i];
            if (!store.TryGet(k, out var t)) { drop[i] = false; return; }
            var dims = DepthLevelDims[k.L];
            if (Touches(t.Geo.FetchRect, dims, img.W, img.H, ch)) { drop[i] = true; return; }
            var reads = DofGeometry.DepthRangeReads(img, dims, t.Geo.Window);
            if (reads is { } rr && !(rr.X0 < ch.X1 && ch.X0 < rr.X1 && rr.Y0 < ch.Y1 && ch.Y0 < rr.Y1) && img.W == t.DepthW && img.H == t.DepthH) return;   // range unchanged
            var g = DofGeometry.Tile(Grid, State, img, DepthLevelDims, k.L, k.Tx, k.Ty, Device, inputs.Summary);
            drop[i] = !g.SameOutputAs(t.Geo) || Touches(g.FetchRect, dims, img.W, img.H, ch);
        });
        for (int i = 0; i < keys.Count; i++) if (drop[i]) dropped.Add(keys[i]);
        var set = dropped.ToHashSet();
        store.RemoveWhere(set.Contains);
        store.RemovePending();
        return dropped;
    }

    /// <summary>Whether the depth-cache pixels a fetch of <paramref name="rect"/> (a rect of a level with dims <paramref name="levelDims"/>) reads
    /// meet <paramref name="ch"/>: the source window of the depth-cache getter — [⌊x0·s⌋, ⌈x1·s⌉) with s = cacheDim / levelDim — grown
    /// by one pixel.</summary>
    static bool Touches(RectI rect, (int W, int H) levelDims, int cacheW, int cacheH, RectI ch)
    {
        double sx = (double)cacheW / levelDims.W, sy = (double)cacheH / levelDims.H;
        int x0 = (int)Math.Floor(rect.X0 * sx) - 1, y0 = (int)Math.Floor(rect.Y0 * sy) - 1;
        int x1 = (int)Math.Ceiling(rect.X1 * sx) + 1, y1 = (int)Math.Ceiling(rect.Y1 * sy) + 1;
        return x0 < ch.X1 && ch.X0 < x1 && y0 < ch.Y1 && ch.Y0 < y1;
    }

    /// <summary>The completed tiles (level, column, row) and their level rects — what is cached now.</summary>
    public IEnumerable<(int L, int Tx, int Ty)> CachedTiles() => _tiles.CompletedKeys();

    /// <summary>`ImageConvertPixelType&lt;Vec3&lt;Float16&gt;, vec4x32f&gt;`: the destination is (re)allocated at the source view's
    /// size and each pixel's R, G, B go through the software round-toward-zero float→half conversion; alpha is dropped.
    /// <paramref name="crop"/> is in the coordinates of the source image (pixel (x, y) at `rgba[(y·stride + x)·4]`).</summary>
    public static ushort[] ToHalf(float[] rgba, int stride, RectI crop, int tileW, int tileH)
    {
        int w = crop.X1 - crop.X0, h = crop.Y1 - crop.Y0;
        if (w != tileW || h != tileH) throw new InvalidOperationException("depth-effect tile does not cover its cache tile");
        var t = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int s = ((crop.Y0 + y) * stride + crop.X0) * 4, d = y * w * 3, x = 0;
            if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
            {
                // two pixels (eight lanes) per step through the same integer steps as Half16.FromFloat; alpha lanes dropped
                ref float rb = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(rgba);
                for (; x + 2 <= w; x += 2, s += 8, d += 6)
                {
                    var hv = Half8(System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref rb, (nuint)s));
                    t[d] = (ushort)hv.GetElement(0); t[d + 1] = (ushort)hv.GetElement(1); t[d + 2] = (ushort)hv.GetElement(2);
                    t[d + 3] = (ushort)hv.GetElement(4); t[d + 4] = (ushort)hv.GetElement(5); t[d + 5] = (ushort)hv.GetElement(6);
                }
            }
            for (; x < w; x++, s += 4, d += 3) { t[d] = Half16.FromFloat(rgba[s]); t[d + 1] = Half16.FromFloat(rgba[s + 1]); t[d + 2] = Half16.FromFloat(rgba[s + 2]); }
        }
        return t;
    }

    /// <summary><see cref="Half16.FromFloat"/> on eight lanes: |f| &lt; 2^−14 → trunc(|f|·2^24); else min(|bits|, cap) + 0x08000000 &gt;&gt; 13, cap 0x47800000
    /// for inf / NaN (→ inf) and 0x477fe000 otherwise (→ 65504); the sign on top.</summary>
    static System.Runtime.Intrinsics.Vector256<int> Half8(System.Runtime.Intrinsics.Vector256<float> f)
    {
        var u = f.AsInt32();
        var sign = System.Runtime.Intrinsics.Vector256.ShiftRightLogical(u, 16) & System.Runtime.Intrinsics.Vector256.Create(0x8000);
        var a = u & System.Runtime.Intrinsics.Vector256.Create(0x7fffffff);
        var sub = System.Runtime.Intrinsics.Vector256.ConvertToInt32(a.AsSingle() * System.Runtime.Intrinsics.Vector256.Create(16777216.0f));
        var cap = System.Runtime.Intrinsics.Vector256.ConditionalSelect(System.Runtime.Intrinsics.Vector256.GreaterThanOrEqual(a, System.Runtime.Intrinsics.Vector256.Create(0x7f800000)),
            System.Runtime.Intrinsics.Vector256.Create(0x47800000), System.Runtime.Intrinsics.Vector256.Create(0x477fe000));
        var ap = System.Runtime.Intrinsics.Vector256.Min(a, cap);
        var norm = System.Runtime.Intrinsics.Vector256.ShiftRightLogical(ap + System.Runtime.Intrinsics.Vector256.Create(0x08000000), 13) & System.Runtime.Intrinsics.Vector256.Create(0xffff);
        return sign | System.Runtime.Intrinsics.Vector256.ConditionalSelect(System.Runtime.Intrinsics.Vector256.LessThanOrEqual(a, System.Runtime.Intrinsics.Vector256.Create(0x387fffff)), sub, norm);
    }

    ushort[] Tile(int level, int tx, int ty) => _tiles.GetOrCreate((level, tx, ty), k =>
    {
        Interlocked.Increment(ref DofFilters.ActiveTiles);
        try { return GenerateWithGeometry(k.L, k.Tx, k.Ty); }
        finally { Interlocked.Decrement(ref DofFilters.ActiveTiles); }
    }).Px;

    /// <summary>`TileCache::renderROI&lt;vec4x32f&gt;` on this cache: the tiles overlapping `rect` as float RGBA with alpha 1.</summary>
    public float[] RenderRoi(int level, RectI rect)
    {
        var (lw, lh) = Grid.LevelDims[level]; var (tw, th) = Grid.TileSize;
        if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > lw || rect.Y1 > lh) throw new ArgumentException("Requested ROI is out-of-bounds!");
        var (nx, ny) = Grid.Grid[level];
        int tx0 = Math.Min(rect.X0 / tw, nx - 1), tx1 = Math.Min((rect.X1 - 1) / tw, nx - 1), ty0 = Math.Min(rect.Y0 / th, ny - 1), ty1 = Math.Min((rect.Y1 - 1) / th, ny - 1);
        if (tx1 < tx0 || ty1 < ty0) throw new InvalidOperationException("No tiles in ROI!");
        int rw = rect.Width, rh = rect.Height; var outp = new float[rw * rh * 4];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var t = Tile(level, tx, ty); var tr = Grid.TileRect(level, tx, ty); var c = tr.Intersect(rect);
                for (int y = c.Y0; y < c.Y1; y++)
                    for (int x = c.X0; x < c.X1; x++)
                    {
                        int si = ((y - tr.Y0) * tr.Width + (x - tr.X0)) * 3, di = ((y - rect.Y0) * rw + (x - rect.X0)) * 4;
                        outp[di] = Half16.ToFloat(t[si]); outp[di + 1] = Half16.ToFloat(t[si + 1]); outp[di + 2] = Half16.ToFloat(t[si + 2]); outp[di + 3] = 1.0f;
                    }
            }
        return outp;
    }

    /// <summary>The tile fetch (cache, out, rect, reqDims, level) on this cache — what the renderer calls in place of the PipelineCache's
    /// when the requested f-number is below the default: `renderROI` when the level dims equal the requested ones, else the 16.16
    /// Catmull-Rom `ImageResample&lt;2&gt;` of the ±2-margin source rect (same arithmetic as `PipelineCache.Render`).</summary>
    public float[] Render(int level, RectI rect, (int W, int H) reqDims)
    {
        var (ws, hs) = Grid.LevelDims[level];
        if ((ws, hs) == reqDims) return RenderRoi(level, rect);
        float fx = (float)ws / (float)reqDims.W, fy = (float)hs / (float)reqDims.H;
        int sx0 = Math.Max(0, (int)MathF.Floor(fx * rect.X0 + -2.0f)), sy0 = Math.Max(0, (int)MathF.Floor(fy * rect.Y0 + -2.0f));
        int sx1 = Math.Min(ws, (int)MathF.Ceiling(((float)rect.Width + (float)rect.X0) * fx + 2.0f)), sy1 = Math.Min(hs, (int)MathF.Ceiling(((float)rect.Height + (float)rect.Y0) * fy + 2.0f));
        var src = RenderRoi(level, new RectI(sx0, sy0, sx1, sy1));
        double offX = (double)((float)rect.X0 * fx - (float)sx0), offY = (double)((float)rect.Y0 * fy - (float)sy0);
        return ImageResample2.Run(src, sx1 - sx0, sy1 - sy0, rect.Width, rect.Height, offX, offY, fx, fy);
    }
}
