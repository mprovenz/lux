namespace Lux.Engine.Pipeline.Isp;

/// <summary>`lt::Tiler::Run(Rectangle, Vec2 tile, fn)` (`180005bc0`, worker lambda_0 `1800067a0`): the ROI is cut into
/// nx×ny tiles with n = extent/tile + (tile &lt; 2·(extent % tile) ? 1 : 0) (min 1) per axis; the last tile per axis
/// absorbs a small remainder (its extent is 2·tile, clipped to the ROI). Matters for stages with per-tile
/// statistics (ColorNoiseReduction). The 1-D `Tiler::Run(start, end, step, fn)` (`180005e90`) follows the same rule.</summary>
public static class Tiler
{
    public static int Count(int extent, int tile) { int n = extent / tile + (tile < (extent % tile) * 2 ? 1 : 0); return n < 1 ? 1 : n; }

    public static IEnumerable<RectI> Rects(RectI roi, int tileW, int tileH)
    {
        int nx = Count(roi.Width, tileW), ny = Count(roi.Height, tileH);
        if (nx * ny == 1) { if (!roi.IsEmpty) yield return roi; yield break; }
        for (int i = 0; i < nx * ny; i++)
        {
            int ix = i % nx, iy = i / nx;
            int x0 = roi.X0 + tileW * ix, y0 = roi.Y0 + tileH * iy;
            int x1 = Math.Min(x0 + tileW * (ix == nx - 1 ? 2 : 1), roi.X1), y1 = Math.Min(y0 + tileH * (iy == ny - 1 ? 2 : 1), roi.Y1);
            yield return new RectI(x0, y0, x1, y1);
        }
    }

    public static IEnumerable<(int Start, int End)> Ranges(int start, int end, int step)
    {
        int n = Count(end - start, step);
        if (n < 2) { if (start < end) yield return (start, end); yield break; }
        for (int i = 0; i < n; i++) { int s = start + step * i; yield return (s, Math.Min(s + step * (i == n - 1 ? 2 : 1), end)); }
    }
}
