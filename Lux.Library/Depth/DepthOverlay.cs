using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;

namespace Lux.Library.Depth;

/// <summary>
/// What the photo's display tiles get drawn into them (<see cref="IDisplayOverlay"/>): Lumen's rendering mode 1 (the focal region —
/// <see cref="DofOverlay.RefocusPoint"/> with the depth fetch and the range exactly as <see cref="DofDisplay.RenderTile"/> computes them) and mode 4
/// (the quick-select mask — <see cref="DofOverlay.QuickSelect"/>), then Lux's pending-stroke preview with mode 4's blend. Immutable: a new
/// one is made for every change (the render settings compare it by reference).
/// </summary>
internal sealed class DepthOverlay : IDisplayOverlay
{
    readonly ExportLevels _lv;
    // mode 1
    readonly DepthImage? _depth; readonly float _near, _far; readonly float[] _shade;
    // mode 4: the selection, then the pending-stroke preview (both 0 / 1 bytes at the edited level)
    readonly byte[]? _selection, _preview; readonly int _mw, _mh; readonly float[] _colour;

    public bool FocalRegion => _depth is not null;
    public bool HasSelection => _selection is not null;
    public bool HasPreview => _preview is not null;

    DepthOverlay(ExportLevels lv, DepthImage? depth, float near, float far, float[] shade, byte[]? selection, byte[]? preview, int mw, int mh, float[] colour)
    {
        _lv = lv; _depth = depth; _near = near; _far = far; _shade = shade; _selection = selection; _preview = preview; _mw = mw; _mh = mh; _colour = colour;
    }

    /// <param name="depth">The depth-cache image of the focal region (null = no focal region); <paramref name="range"/> its band (Lumen's
    /// `ParamFloatArray 0` with the current MaximumInFocusBlurPixels).</param>
    public static DepthOverlay? Make(ExportLevels lv, DepthImage? depth, (float Near, float Far) range, IReadOnlyList<float> shade,
                                     byte[]? selection, byte[]? preview, (int W, int H) maskSize, IReadOnlyList<float> colour)
    {
        if (depth is null && selection is null && preview is null) return null;
        return new DepthOverlay(lv, depth, range.Near, range.Far, shade.ToArray(), selection, preview, maskSize.W, maskSize.H, colour.ToArray());
    }

    public void Apply(int level, RectI tile, float[] rgba, int width, int height)
    {
        if (_depth is not null)
        {
            // Lumen's depth fetch of the tile shifted by the level origin against the pipeline dims (DofDisplay.RenderTile, mode 1)
            var o = _lv.Origins[level];
            var d = DofGeometry.FetchDepth(_depth, new RectI(tile.X0 + o.X, tile.Y0 + o.Y, tile.X1 + o.X, tile.Y1 + o.Y), _lv.PipelineDims[level]);
            DofOverlay.RefocusPoint(rgba, width, height, width, d.ToDense(), d.W, _near, _far, _shade);
        }
        if (_selection is not null) DofOverlay.QuickSelect(rgba, width, height, width, tile.X0, tile.Y0, _selection, _mw, _mh, _mw, _lv.ExportDims[level], _colour);
        if (_preview is not null) DofOverlay.QuickSelect(rgba, width, height, width, tile.X0, tile.Y0, _preview, _mw, _mh, _mw, _lv.ExportDims[level], _colour);
    }
}

/// <summary>
/// Lux's depth view ("Photo + depth", <see cref="ViewMode.Depth"/>): the depth on an export level's grid, as the overlays fetch it
/// (Lumen's depth fetch of the export-window rect shifted by the level origin, against the level's pipeline dims), mapped to gray by
/// <see cref="DepthViewMapping"/>: <c>t = clamp((1/d − 1/far) / (1/near − 1/far), 0, 1)</c>, <c>grey = round(255 · t^(1/2.2))</c>.
/// </summary>
internal static class DepthViewRender
{
    /// <summary>RGBA8 of <paramref name="rect"/> (pixels of export level <paramref name="level"/>'s window).</summary>
    public static byte[] Render(DepthImage depth, ExportLevels lv, int level, RectI rect, DepthViewMapping map)
    {
        var o = lv.Origins[level];
        var d = DepthImageCache.Fetch(depth, new RectI(rect.X0 + o.X, rect.Y0 + o.Y, rect.X1 + o.X, rect.Y1 + o.Y), lv.PipelineDims[level]);
        var dense = d.ToDense();
        int w = rect.Width, h = rect.Height;
        var px = new byte[(long)w * h * 4];
        double inf = 1.0 / map.FarMm, ins = 1.0 / map.NearMm - inf;
        var lut = Lut();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                float z = y < d.H && x < d.W ? dense[(long)y * d.W + x] : 0f;
                double t = z > 0 ? (1.0 / z - inf) / ins : 0;
                int k = (int)(Math.Clamp(t, 0, 1) * 4095 + 0.5);
                byte g = lut[k];
                px[i * 4] = g; px[i * 4 + 1] = g; px[i * 4 + 2] = g; px[i * 4 + 3] = 255;
            }
        return px;
    }

    static byte[]? _lut;
    static byte[] Lut()
    {
        if (_lut is { } l) return l;
        var t = new byte[4096];
        for (int i = 0; i < 4096; i++) t[i] = (byte)Math.Round(255.0 * Math.Pow(i / 4095.0, 1 / 2.2));
        return _lut = t;
    }

    /// <summary>The mapping of a depth: the 1st and 99th percentile of the depth-cache image inside the level-1 export window (sampled every
    /// 4th pixel), clamped to [200 mm, 100 m].</summary>
    public static DepthViewMapping MappingOf(DepthImage depth, ExportLevels lv)
    {
        int l = Math.Min(1, lv.ExportDims.Length - 1);
        var o = lv.Origins[l]; var (w, h) = lv.ExportDims[l];
        double sx = (double)depth.W / lv.PipelineDims[l].W, sy = (double)depth.H / lv.PipelineDims[l].H;
        int x0 = (int)(o.X * sx), y0 = (int)(o.Y * sy), x1 = Math.Min(depth.W, (int)((o.X + w) * sx)), y1 = Math.Min(depth.H, (int)((o.Y + h) * sy));
        var v = new List<float>();
        for (int y = y0; y < y1; y += 4) for (int x = x0; x < x1; x += 4) { float z = depth[x, y]; if (z > 0 && float.IsFinite(z)) v.Add(z); }
        if (v.Count == 0) return new DepthViewMapping(500, 50000);
        v.Sort();
        float near = v[(int)(0.01 * (v.Count - 1))], far = v[(int)(0.99 * (v.Count - 1))];
        near = Math.Clamp(near, 200f, 100000f); far = Math.Clamp(far, near * 1.01f, 100000f);
        return new DepthViewMapping(near, far);
    }

    /// <summary>The nearest and farthest depth inside the level-1 export window.</summary>
    public static DepthRange SceneRangeOf(DepthImage depth, ExportLevels lv)
    {
        int l = Math.Min(1, lv.ExportDims.Length - 1);
        var o = lv.Origins[l]; var (w, h) = lv.ExportDims[l];
        double sx = (double)depth.W / lv.PipelineDims[l].W, sy = (double)depth.H / lv.PipelineDims[l].H;
        int x0 = (int)(o.X * sx), y0 = (int)(o.Y * sy), x1 = Math.Min(depth.W, (int)((o.X + w) * sx)), y1 = Math.Min(depth.H, (int)((o.Y + h) * sy));
        float mn = float.MaxValue, mx = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float z = depth[x, y]; if (z > 0 && float.IsFinite(z)) { if (z < mn) mn = z; if (z > mx) mx = z; } }
        return mn > mx ? new DepthRange(0, 0) : new DepthRange(mn, mx);
    }
}
