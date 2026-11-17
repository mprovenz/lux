namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>
/// A depth network: it takes a picture and returns one value per pixel that grows as the scene gets nearer. The values have no unit and
/// no fixed zero. The engine does not know how the network runs: the caller supplies it.
/// </summary>
public interface IDepthNetwork
{
    /// <summary>A name for the network and its weights. It is recorded with refined depth that is stored.</summary>
    string Id { get; }

    /// <summary>Run the network on one picture.</summary>
    /// <param name="planes">The picture as three planes (red, green, blue), each <paramref name="width"/> × <paramref name="height"/>,
    /// already normalized with <see cref="RefineConstants.NetworkMean"/> and <see cref="RefineConstants.NetworkStd"/>.</param>
    /// <param name="width">A multiple of <see cref="RefineConstants.NetworkPatch"/>.</param>
    /// <param name="height">A multiple of <see cref="RefineConstants.NetworkPatch"/>.</param>
    /// <returns>One value per pixel, row by row.</returns>
    float[] Run(float[] planes, int width, int height);
}

/// <summary>The network's view of a picture: the whole-picture pass and the tiled map, both on the picture's grid.</summary>
public sealed record DepthPriorMaps(float[] Whole, float[] Tiled, int Tiles);

/// <summary>
/// Runs a depth network on a picture at two scales. The whole picture, reduced, gives the layout of the scene. Overlapping tiles at twice
/// the scale give thin structures and sharp edges. Each tile is fitted to the whole-picture pass, the tiles are feathered together, and
/// the large-scale part of the result is taken from the whole-picture pass, so the tiles cannot drift apart.
/// </summary>
public static class DepthPrior
{
    /// <summary>The input size of the whole-picture pass for a picture of the given size.</summary>
    public static (int W, int H) WholeSize(int w, int h)
    {
        double k = (double)RefineConstants.NetworkSide / Math.Min(w, h);
        return (Snap(w * k), Snap(h * k));
    }

    static int Snap(double v)
    {
        int p = RefineConstants.NetworkPatch;
        return Math.Max(p, (int)Math.Round(v / p, MidpointRounding.ToEven) * p);
    }

    /// <summary>The tile origins along one side of the picture.</summary>
    public static int[] TileOrigins(int length, int tile)
    {
        int step = (int)(tile * (1 - RefineConstants.TileOverlap));
        var o = new List<int>();
        for (int x = 0; x <= Math.Max(length - tile, 0); x += step) o.Add(x);
        if (o[^1] + tile < length) o.Add(length - tile);
        return o.ToArray();
    }

    /// <summary>The number of network runs for a picture of the given size.</summary>
    public static int Runs(int w, int h)
    {
        int tile = RefineConstants.TileSide;
        return Math.Min(w, h) < tile ? 1 : 1 + TileOrigins(w, tile).Length * TileOrigins(h, tile).Length;
    }

    /// <summary>A plane turned clockwise by 90, 180 or 270 degrees. The turn moves values and does not change them. A turn by
    /// 360 − <paramref name="degrees"/> of the result gives the plane back.</summary>
    /// <returns>The turned plane. Its size is <paramref name="h"/> × <paramref name="w"/> for 90 and 270 degrees.</returns>
    public static float[] Turn(float[] plane, int w, int h, int degrees)
    {
        degrees = Quarter(degrees);
        if (degrees == 0) return plane;
        var o = new float[plane.Length];
        RefineOps.Rows(h, y =>
        {
            long s = (long)y * w;
            switch (degrees)
            {
                case 90: for (int x = 0; x < w; x++) o[(long)x * h + (h - 1 - y)] = plane[s + x]; break;
                case 180: { long d = (long)(h - 1 - y) * w + (w - 1); for (int x = 0; x < w; x++) o[d - x] = plane[s + x]; break; }
                default: for (int x = 0; x < w; x++) o[(long)(w - 1 - x) * h + y] = plane[s + x]; break;
            }
        });
        return o;
    }

    /// <summary>The tiles of a picture of the given size after the picture is turned clockwise by <paramref name="degrees"/>: the same
    /// areas of the picture as <see cref="TileOrigins"/> gives without the turn, as origins in the turned picture.</summary>
    public static (int[] Xs, int[] Ys) TurnedTileOrigins(int w, int h, int tile, int degrees)
    {
        var xs = TileOrigins(w, tile); var ys = TileOrigins(h, tile);
        static int[] FromFarEnd(int[] o, int length, int tile) => o.Select(v => Math.Max(length - tile, 0) - v).Reverse().ToArray();
        return Quarter(degrees) switch
        {
            90 => (FromFarEnd(ys, h, tile), xs),
            180 => (FromFarEnd(xs, w, tile), FromFarEnd(ys, h, tile)),
            270 => (ys, FromFarEnd(xs, w, tile)),
            _ => (xs, ys),
        };
    }

    static int Quarter(int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        return degrees % 90 == 0 ? degrees : 0;
    }

    /// <param name="rgba">The picture, 8 bits per channel, four bytes per pixel.</param>
    /// <param name="tick">Called after each network run.</param>
    /// <param name="turn">The degrees clockwise (90, 180, 270) that show the picture upright. The network then sees the upright picture:
    /// the whole-picture pass and every tile. The tiles cover the same areas of the picture as without the turn. The maps come back on
    /// the grid of <paramref name="rgba"/>. 0, or a negative value, means no turn.</param>
    public static DepthPriorMaps Run(byte[] rgba, int w, int h, IDepthNetwork net, Action? tick = null, CancellationToken cancel = default, int turn = 0)
    {
        if (Math.Min(w, h) < RefineConstants.NetworkSide) throw new ArgumentException("the picture is too small for the depth network");
        turn = turn < 0 ? 0 : Quarter(turn);
        long n = (long)w * h;
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            var p = planes[c] = new float[n]; int cc = c;
            RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) p[o + x] = rgba[(o + x) * 4 + cc] / 255f; });
        }
        var (xs, ys) = TurnedTileOrigins(w, h, RefineConstants.TileSide, turn);
        if (turn == 0) return Run(planes, w, h, xs, ys, net, tick, cancel);

        // the network sees the upright picture; its maps go back to the grid of the picture
        bool swap = turn != 180;
        int uw = swap ? h : w, uh = swap ? w : h;
        for (int c = 0; c < 3; c++) planes[c] = Turn(planes[c], w, h, turn);
        var upright = Run(planes, uw, uh, xs, ys, net, tick, cancel);
        return new DepthPriorMaps(Turn(upright.Whole, uw, uh, 360 - turn), Turn(upright.Tiled, uw, uh, 360 - turn), upright.Tiles);
    }

    /// <summary>The two passes on a picture that is already upright, with the given tile origins.</summary>
    static DepthPriorMaps Run(float[][] planes, int w, int h, int[] xs, int[] ys, IDepthNetwork net, Action? tick, CancellationToken cancel)
    {
        long n = (long)w * h;

        // the whole picture
        var (gw, gh) = WholeSize(w, h);
        var input = new float[3L * gw * gh];
        for (int c = 0; c < 3; c++) Normalize(RefineOps.ResizeArea(planes[c], w, h, gw, gh), input, (long)c * gw * gh, c);
        cancel.ThrowIfCancellationRequested();
        var whole = RefineOps.ResizeBilinear(Checked(net.Run(input, gw, gh), gw, gh), gw, gh, w, h);
        tick?.Invoke();

        int tile = RefineConstants.TileSide, side = RefineConstants.NetworkSide;
        if (Math.Min(w, h) < tile) return new DepthPriorMaps(whole, (float[])whole.Clone(), 0);

        // the tiles
        var window = new float[tile];
        for (int i = 0; i < tile; i++) window[i] = (float)(0.5 - 0.5 * WineCrt.Cos(2.0 * 3.141592653589793 * (i + 1) / (tile + 1)));
        var acc = new double[n]; var wacc = new float[n];
        var crop = new float[(long)tile * tile]; var tin = new float[3L * side * side];
        int fit = RefineConstants.TileFitStep;
        foreach (int ty in ys)
            foreach (int tx in xs)
            {
                cancel.ThrowIfCancellationRequested();
                for (int c = 0; c < 3; c++)
                {
                    var p = planes[c];
                    for (int y = 0; y < tile; y++) Array.Copy(p, (long)(ty + y) * w + tx, crop, (long)y * tile, tile);
                    Normalize(RefineOps.ResizeArea(crop, tile, tile, side, side), tin, (long)c * side * side, c);
                }
                var u = RefineOps.ResizeBilinear(Checked(net.Run(tin, side, side), side, side), side, side, tile, tile);
                // least squares fit of the tile to the whole-picture pass: whole ≈ s·tile + o
                double sa = 0, sb = 0; long m = 0;
                for (int y = 0; y < tile; y += fit)
                    for (int x = 0; x < tile; x += fit) { sa += u[(long)y * tile + x]; sb += whole[(long)(ty + y) * w + tx + x]; m++; }
                double ma = sa / m, mb = sb / m, va = 0, cab = 0;
                for (int y = 0; y < tile; y += fit)
                    for (int x = 0; x < tile; x += fit)
                    {
                        double a = u[(long)y * tile + x] - ma, b = whole[(long)(ty + y) * w + tx + x] - mb;
                        va += a * a; cab += a * b;
                    }
                double s = cab / Math.Max(va / m, 1e-12) / m, o = mb - s * ma;
                if (!(s > 0)) { s = 1.0; o = mb - ma; }
                int oy = ty, ox = tx;
                RefineOps.Rows(tile, y =>
                {
                    long po = (long)(oy + y) * w + ox, uo = (long)y * tile; float wy = window[y];
                    for (int x = 0; x < tile; x++)
                    {
                        float wt = wy * window[x] + RefineConstants.TileWindowFloor;
                        acc[po + x] += wt * (float)(s * u[uo + x] + o); wacc[po + x] += wt;
                    }
                });
                tick?.Invoke();
            }
        var tiled = new float[n];
        RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) tiled[o + x] = (float)(acc[o + x] / Math.Max(wacc[o + x], 1e-9f)); });

        // the large scales from the whole-picture pass
        int q = RefineConstants.LowPassReduction, qw = w / q, qh = h / q;
        if (qw >= 8 && qh >= 8)
        {
            var diff = new float[n];
            RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) diff[o + x] = whole[o + x] - tiled[o + x]; });
            var low = RefineOps.GaussianBlur(RefineOps.ResizeArea(diff, w, h, qw, qh), qw, qh, RefineOps.GaussianKernel(RefineConstants.LowPassSigma / q));
            var up = RefineOps.ResizeBilinear(low, qw, qh, w, h);
            RefineOps.Rows(h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) tiled[o + x] += up[o + x]; });
        }
        return new DepthPriorMaps(whole, tiled, xs.Length * ys.Length);
    }

    static void Normalize(float[] plane, float[] dst, long at, int channel)
    {
        float mean = RefineConstants.NetworkMean[channel], std = RefineConstants.NetworkStd[channel];
        for (long i = 0; i < plane.Length; i++) dst[at + i] = (plane[i] - mean) / std;
    }

    static float[] Checked(float[] output, int w, int h)
    {
        if (output.Length != (long)w * h) throw new InvalidOperationException($"the depth network returned {output.Length} values for a {w}x{h} picture");
        return output;
    }
}
