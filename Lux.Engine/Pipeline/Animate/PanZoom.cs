using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Animate;

/// <summary>One camera position of a pan-and-zoom move.</summary>
/// <param name="Tx">Sideways travel of the camera, mm (<paramref name="Ty"/>: the same along the picture's rows).</param>
/// <param name="Tz">Forward travel, mm.</param>
/// <param name="Zoom">The crop zoom: 1 shows the whole picture.</param>
/// <param name="Cx">The middle of the crop window, source pixels.</param>
public readonly record struct PanZoomPose(double Tx, double Ty, double Tz, double Zoom, double Cx, double Cy);

/// <summary>
/// A depth-aware pan-and-zoom move: the crop zooms in towards a target while the camera slides sideways and moves forward. The camera
/// keeps aiming at the target, so content at the target's distance holds its place and nearer and farther content slides against it.
/// </summary>
public sealed record PanZoomMove
{
    /// <summary>The crop zoom at the start and at the end. The margin at the start hides the picture's edges as they move in.</summary>
    public const double ZoomStart = 1.10, ZoomEnd = 1.35;
    /// <summary>The camera moves forward this many times its sideways travel.</summary>
    public const double ForwardFactor = 2.0;
    /// <summary>The travels the preflight tries, as fractions of the nearest distance.</summary>
    public static readonly double[] Travels = { 0.01, 0.02, 0.04, 0.06, 0.08, 0.12 };

    /// <summary>The target as a fraction of the picture (0–1): where the push-in ends.</summary>
    public required double TargetX { get; init; }
    public required double TargetY { get; init; }
    /// <summary>The distance that holds its place, mm (the depth at the target).</summary>
    public required double HoldMm { get; init; }
    /// <summary>The nearest distance of the scene, mm (the 2nd percentile of the depth).</summary>
    public required double NearMm { get; init; }
    /// <summary>The sideways travel as a fraction of <see cref="NearMm"/>, end to end. 0 = the camera does not move.</summary>
    public double Travel { get; init; }
    /// <summary>The sideways travel runs along the picture's columns (a photo held turned by a quarter).</summary>
    public bool Vertical { get; init; }
    /// <summary>A flat move: every pixel at the hold distance, so nothing slides. The same window path as the depth-aware move.</summary>
    public bool Flat { get; init; }

    public double SidewaysMm => Travel * NearMm;
    public double ForwardMm => ForwardFactor * Travel * NearMm;

    /// <summary>The pose at time <paramref name="t"/> (0–1) for a source of <paramref name="w"/> × <paramref name="h"/> pixels.</summary>
    public PanZoomPose At(double t, int w, int h)
    {
        double e = LookMath.Ease(t);
        double zoom = ZoomStart + (ZoomEnd - ZoomStart) * e;
        double cx = w / 2.0 + (TargetX * w - w / 2.0) * e, cy = h / 2.0 + (TargetY * h - h / 2.0) * e;
        double hw = w / 2.0 / zoom, hh = h / 2.0 / zoom;
        cx = Math.Clamp(cx, hw, w - hw); cy = Math.Clamp(cy, hh, h - hh);
        double side = SidewaysMm * (e - 0.5);
        return new PanZoomPose(Vertical ? 0 : side, Vertical ? side : 0, ForwardMm * e, zoom, cx, cy);
    }

    /// <summary>How far the nearest content slides against the farthest in the last frame, as a share of the frame width (0–1), for a focal
    /// length of <paramref name="focalOverWidth"/> frame widths and a far distance of <paramref name="farMm"/>.</summary>
    public static double Parallax(double travel, double focalOverWidth, double nearMm, double farMm)
        => travel * (focalOverWidth * ZoomEnd * (1 - nearMm / Math.Max(farMm, nearMm)) + 0.25 * ForwardFactor);
}

/// <summary>What a warped frame left uncovered, and how far it stretched surfaces.</summary>
/// <param name="TearPct">Gaps inside one surface (both sides at a similar depth), % of the frame: depth noise pulls a surface apart.</param>
/// <param name="EdgePct">Gaps beside a depth step, % of the frame: background the picture never saw.</param>
/// <param name="GapPct">The widest gap away from the frame's border, % of the frame width.</param>
/// <param name="SmearPct">Stretch of surfaces near depth steps beyond the move's own scale, summed, % of the frame.</param>
public readonly record struct PanZoomStats(double HolesPct, double TearPct, double EdgePct, double GapPct, double SmearPct)
{
    /// <summary>The limits of an acceptable frame.</summary>
    public const double MaxTear = 0.3, MaxEdge = 1.0, MaxGap = 3.0, MaxSmear = 2.0;
    public bool Acceptable => TearPct <= MaxTear && EdgePct <= MaxEdge && GapPct <= MaxGap && SmearPct <= MaxSmear;

    /// <summary>What fails, in the user's words; empty when the frame is acceptable.</summary>
    public string Failures()
    {
        var f = new List<string>();
        if (TearPct > MaxTear) f.Add("surfaces tear");
        if (EdgePct > MaxEdge) f.Add("too much hidden background shows");
        if (GapPct > MaxGap) f.Add("a gap is too wide");
        if (SmearPct > MaxSmear) f.Add("edges smear");
        return string.Join(", ", f);
    }

    public static PanZoomStats Worst(PanZoomStats a, PanZoomStats b) =>
        new(Math.Max(a.HolesPct, b.HolesPct), Math.Max(a.TearPct, b.TearPct), Math.Max(a.EdgePct, b.EdgePct), Math.Max(a.GapPct, b.GapPct), Math.Max(a.SmearPct, b.SmearPct));
}

/// <summary>The result of the preflight: how far the camera can travel on this scene with this depth.</summary>
/// <param name="Limit">The largest acceptable travel (fraction of the nearest distance); 0 when the smallest one already fails.</param>
/// <param name="Tried">Each travel tried with its worst frame.</param>
public sealed record PanZoomPreflight(double Limit, IReadOnlyList<(double Travel, PanZoomStats Stats)> Tried)
{
    /// <summary>What fails at the first travel past the limit; empty when every travel passes.</summary>
    public string FirstFailure => Tried.FirstOrDefault(t => !t.Stats.Acceptable).Stats.Failures();
}

/// <summary>
/// The pan-and-zoom renderer: the picture and its depth are splatted into each camera position (<see cref="Dibr.Warp"/>). Gaps are filled
/// from a background layer first, which is made once and holds what lies behind each depth step as far as it can be guessed. What is still
/// empty is painted in from its surroundings.
/// </summary>
public sealed class PanZoomSource
{
    /// <summary>The working size the measures of the background layer and of the preflight refer to (long edge, pixels).</summary>
    const double ReferenceEdge = 2608;
    /// <summary>The baseline (mm) of the disparity the depth steps are measured in, with the focal length of export level 1.</summary>
    const double StepBaselineMm = 43.6;

    public Rgba Colour { get; }
    public Plane Depth { get; }
    /// <summary>Focal length in pixels of <see cref="Colour"/>.</summary>
    public double FocalPx { get; }
    readonly double _focalLevel1;
    Rgba? _bgColour; Plane? _bgDepth;
    readonly object _gate = new();

    public int W => Colour.W;
    public int H => Colour.H;

    /// <param name="focalLevel1">The focal length in pixels of export level 1 (depth steps are measured at that scale).</param>
    public PanZoomSource(Rgba colour, Plane depth, double focalPx, double focalLevel1)
    {
        Colour = colour; Depth = depth; FocalPx = focalPx; _focalLevel1 = focalLevel1;
    }

    /// <summary>The same source at a smaller size (long edge <paramref name="longEdge"/>); this source when it is not larger.</summary>
    public PanZoomSource Scaled(int longEdge)
    {
        if (longEdge <= 0 || Math.Max(W, H) <= longEdge) return this;
        double k = (double)longEdge / Math.Max(W, H);
        int ow = Math.Max(1, (int)Math.Round(W * k)), oh = Math.Max(1, (int)Math.Round(H * k));
        return new PanZoomSource(Colour.Resize(ow, oh), Depth.ResizeNearest(ow, oh), FocalPx * ow / W, _focalLevel1);
    }

    double SizeFactor => Math.Max(W, H) / ReferenceEdge;

    /// <summary>The nearest distance of the scene, mm: the 2nd percentile of the depth.</summary>
    public double NearMm() => DepthPercentile(0.02);
    public double DepthPercentile(double p)
    {
        var v = Depth.V.Where(z => z > 0f && float.IsFinite(z)).ToArray();
        if (v.Length == 0) throw new InvalidOperationException("the depth map is empty");
        Array.Sort(v);
        return v[Math.Clamp((int)(p * (v.Length - 1)), 0, v.Length - 1)];
    }

    /// <summary>The depth at a point, mm: the median of a small window around it.</summary>
    public double DepthAt(double x, double y)
        => Dibr.DepthAt(Depth, Math.Clamp((int)x, 0, W - 1), Math.Clamp((int)y, 0, H - 1), Math.Max(3, (int)Math.Round(25 * SizeFactor)));

    /// <summary>Disparity per pixel in pixels of export level 1 for the reference baseline: the unit depth steps are measured in.</summary>
    float[] StepDisparity()
    {
        var d = new float[Depth.V.LongLength]; float k = (float)(_focalLevel1 * StepBaselineMm);
        Parallel.For(0, H, y => { long o = (long)y * W; for (int x = 0; x < W; x++) { float z = Depth.V[o + x]; d[o + x] = z > 0f && float.IsFinite(z) ? k / Math.Max(z, 1f) : 0f; } });
        return d;
    }

    /// <summary>The minimum (or maximum) over a square window of the given radius.</summary>
    internal static float[] Extreme(float[] a, int w, int h, int radius, bool min)
    {
        if (radius <= 0) return a;
        var t = new float[a.LongLength]; var o = new float[a.LongLength];
        Parallel.For(0, h, y =>
        {
            long r = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                float v = a[r + x0];
                for (int i = x0 + 1; i <= x1; i++) { float c = a[r + i]; if (min ? c < v : c > v) v = c; }
                t[r + x] = v;
            }
        });
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                float v = t[(long)y0 * w + x];
                for (int i = y0 + 1; i <= y1; i++) { float c = t[(long)i * w + x]; if (min ? c < v : c > v) v = c; }
                o[(long)y * w + x] = v;
            }
        });
        return o;
    }

    /// <summary>
    /// Make the background layer. On the near side of every depth step, within 48 pixels of it at the reference size, the color is painted
    /// in from the far side and the depth is the farthest depth near by. A gap that opens beside the step then shows the layer: pixels
    /// that stay in place from frame to frame.
    /// </summary>
    public void BuildBackground()
    {
        lock (_gate)
        {
            if (_bgColour is not null) return;
            int w = W, h = H; long n = (long)w * h;
            var d = StepDisparity();
            var far = Extreme(d, w, h, Math.Max(1, (int)Math.Round(48 * SizeFactor)), min: true);
            var mask = new float[n];
            Parallel.For(0, h, y => { long o = (long)y * w; for (int x = 0; x < w; x++) mask[o + x] = d[o + x] - far[o + x] > Math.Max(4.0f, 0.08f * d[o + x]) ? 1f : 0f; });
            mask = Extreme(mask, w, h, Math.Max(1, (int)Math.Round(2 * SizeFactor)), min: false);
            var bgz = new float[n]; var hole = new bool[n]; float k = (float)(_focalLevel1 * StepBaselineMm);
            for (long i = 0; i < n; i++)
            {
                hole[i] = mask[i] > 0f;
                bgz[i] = hole[i] ? k / Math.Max(far[i], 1e-3f) : Depth.V[i];
            }
            var view = new View { Colour = Colour.Clone(), Z = (float[])Depth.V.Clone(), Hole = hole };
            for (long i = 0; i < n; i++) if (hole[i]) view.Colour.P[i * 4 + 3] = 0;
            Fill.Inpaint(view, w, h);
            for (long i = 0; i < n; i++) view.Colour.P[i * 4 + 3] = 255;
            _bgDepth = new Plane(w, h, bgz);
            _bgColour = view.Colour;
        }
    }

    /// <summary>The map of a pose: where a source pixel at depth z lands in the frame (source-size pixels).</summary>
    Dibr.Map MapOf(PanZoomPose p, double holdMm)
    {
        double f = FocalPx, cxs = W * 0.5, cys = H * 0.5;
        double hold = Math.Max(holdMm - p.Tz, 1.0);
        double compX = f * p.Tx / hold, compY = f * p.Ty / hold;
        return (x, y, z) =>
        {
            double zz = z - p.Tz; if (zz < 1) zz = 1;
            double u = (x - cxs) * z / zz + cxs - f * p.Tx / zz + compX;
            double v = (y - cys) * z / zz + cys - f * p.Ty / zz + compY;
            return ((float)(cxs + p.Zoom * (u - p.Cx)), (float)(cys + p.Zoom * (v - p.Cy)));
        };
    }

    Plane FlatDepth(double holdMm)
    {
        var v = new float[Depth.V.LongLength]; Array.Fill(v, (float)holdMm);
        return new Plane(W, H, v);
    }
    Plane? _flat; double _flatHold;

    /// <summary>The frame of <paramref name="move"/> at time <paramref name="t"/> (0–1), at <paramref name="ow"/> × <paramref name="oh"/> pixels.</summary>
    public Rgba Frame(PanZoomMove move, double t, int ow, int oh)
    {
        var pose = move.At(t, W, H);
        var map = MapOf(pose, move.HoldMm);
        View view;
        if (move.Flat)
        {
            lock (_gate) { if (_flat is null || _flatHold != move.HoldMm) { _flat = FlatDepth(move.HoldMm); _flatHold = move.HoldMm; } }
            view = Dibr.Warp(Colour, _flat!, map);
        }
        else
        {
            view = Dibr.Warp(Colour, Depth, map);
            if (view.HoleCount > 0)
            {
                BuildBackground();
                var bv = Dibr.Warp(_bgColour!, _bgDepth!, map);
                long n = (long)W * H;
                for (long i = 0, p = 0; i < n; i++, p += 4)
                {
                    if (!view.Hole[i] || bv.Hole[i]) continue;
                    view.Colour.P[p] = bv.Colour.P[p]; view.Colour.P[p + 1] = bv.Colour.P[p + 1]; view.Colour.P[p + 2] = bv.Colour.P[p + 2]; view.Colour.P[p + 3] = 255;
                    view.Z[i] = bv.Z[i]; view.Hole[i] = false;
                }
            }
        }
        if (view.HoleCount > 0) Fill.Inpaint(view, W, H);
        var img = view.Colour.Resize(ow, oh);
        for (long p = 3; p < img.P.LongLength; p += 4) img.P[p] = 255;
        return img;
    }

    /// <summary>What the frame of <paramref name="move"/> at time <paramref name="t"/> leaves uncovered before any fill.</summary>
    public PanZoomStats Measure(PanZoomMove move, double t, byte[]? band = null)
    {
        var pose = move.At(t, W, H);
        var map = MapOf(pose, move.HoldMm);
        var view = Dibr.Warp(Colour, Depth, map);
        int w = W, h = H; double area = (double)w * h;
        long holes = 0, tears = 0, edge = 0;
        // every row's runs of empty pixels, sorted by the depths at their two ends: at the border, beside a depth step (the ends differ by
        // more than 25 %), or inside one surface
        for (int y = 0; y < h; y++)
        {
            int x = 0; long r = (long)y * w;
            while (x < w)
            {
                if (!view.Hole[r + x]) { x++; continue; }
                int x0 = x; while (x < w && view.Hole[r + x]) x++;
                int len = x - x0; holes += len;
                if (x0 == 0 || x == w) continue;
                float zl = view.Z[r + x0 - 1], zr = view.Z[r + x];
                if (Math.Max(zl, zr) > 1.25f * Math.Min(zl, zr)) edge += len; else tears += len;
            }
        }
        // the widest gap inside the inner 90 % of the frame: twice the largest distance from an empty pixel to a filled one (a chamfer
        // distance with steps of 3 along rows and columns and 4 along diagonals, in thirds of a pixel)
        int bx0 = w / 20, bx1 = w - w / 20, by0 = h / 20, by1 = h - h / 20;
        int iw = bx1 - bx0, ih = by1 - by0, far = 3 * (iw + ih);
        var dist = new int[iw * ih];
        for (int y = 0; y < ih; y++)
            for (int x = 0; x < iw; x++)
            {
                int i = y * iw + x;
                if (!view.Hole[(long)(y + by0) * w + x + bx0]) { dist[i] = 0; continue; }
                int d = far;
                if (x > 0) d = Math.Min(d, dist[i - 1] + 3);
                if (y > 0)
                {
                    d = Math.Min(d, dist[i - iw] + 3);
                    if (x > 0) d = Math.Min(d, dist[i - iw - 1] + 4);
                    if (x + 1 < iw) d = Math.Min(d, dist[i - iw + 1] + 4);
                }
                dist[i] = d;
            }
        int deepest = 0;
        for (int y = ih - 1; y >= 0; y--)
            for (int x = iw - 1; x >= 0; x--)
            {
                int i = y * iw + x, d = dist[i];
                if (d == 0) continue;
                if (x + 1 < iw) d = Math.Min(d, dist[i + 1] + 3);
                if (y + 1 < ih)
                {
                    d = Math.Min(d, dist[i + iw] + 3);
                    if (x + 1 < iw) d = Math.Min(d, dist[i + iw + 1] + 4);
                    if (x > 0) d = Math.Min(d, dist[i + iw - 1] + 4);
                }
                dist[i] = d;
                if (d < far && d > deepest) deepest = d;
            }
        double gap = 2.0 * deepest / 3.0;
        // smear: neighbors on one surface pulled apart by more than 1.5 pixels (at the reference size) beyond the move's own scale
        double limit = 1.5 * SizeFactor, smear = 0;
        var rows = new double[h];
        Parallel.For(0, h, y =>
        {
            double s = 0; long r = (long)y * w;
            for (int x = 0; x + 1 < w; x++)
            {
                long i = r + x;
                if (band is not null && band[i] == 0) continue;
                float z0 = Depth.V[i], z1 = Depth.V[i + 1];
                if (!(z0 > 0) || !(z1 > 0) || Math.Abs(z1 - z0) > z0 * Dibr.DepthTol * 4f) continue;
                var (u0, _) = map(x, y, z0); var (u1, _) = map(x + 1, y, z1);
                double nominal = pose.Zoom * z0 / Math.Max(z0 - pose.Tz, 1);
                double ex = Math.Abs(u1 - u0) - nominal;
                if (ex > limit) s += ex;
            }
            rows[y] = s;
        });
        foreach (var s in rows) smear += s;
        return new PanZoomStats(100.0 * holes / area, 100.0 * tears / area, 100.0 * edge / area, 100.0 * gap / w, 100.0 * smear / area / SizeFactor);
    }

    /// <summary>The pixels within 16 pixels (at the reference size) of a depth step: where smear is counted.</summary>
    public byte[] StepBand()
    {
        int w = W, h = H;
        var d = StepDisparity();
        int r9 = Math.Max(1, (int)Math.Round(4 * SizeFactor));
        var hi = Extreme(d, w, h, r9, min: false); var lo = Extreme(d, w, h, r9, min: true);
        var e = new float[d.LongLength];
        for (long i = 0; i < e.LongLength; i++) e[i] = hi[i] - lo[i] > Math.Max(4.0f, 0.08f * hi[i]) ? 1f : 0f;
        e = Extreme(e, w, h, Math.Max(1, (int)Math.Round(16 * SizeFactor)), min: false);
        var band = new byte[e.LongLength];
        for (long i = 0; i < e.LongLength; i++) band[i] = e[i] > 0f ? (byte)1 : (byte)0;
        return band;
    }

    /// <summary>
    /// The preflight: try each travel of <see cref="PanZoomMove.Travels"/> at the start, the middle and the end of the move, and find the
    /// largest one whose frames are all acceptable. Run it on a source of moderate size (<see cref="Scaled"/>): it makes three warps per
    /// travel.
    /// </summary>
    public PanZoomPreflight Preflight(PanZoomMove move, CancellationToken cancel = default)
    {
        var band = StepBand();
        var tried = new List<(double, PanZoomStats)>();
        double limit = 0; bool failed = false;
        foreach (double travel in PanZoomMove.Travels)
        {
            cancel.ThrowIfCancellationRequested();
            var m = move with { Travel = travel, Flat = false };
            var s = Measure(m, 0, band);
            s = PanZoomStats.Worst(s, Measure(m, 0.5, band));
            s = PanZoomStats.Worst(s, Measure(m, 1, band));
            tried.Add((travel, s));
            if (s.Acceptable && !failed) limit = travel; else failed = true;
            if (failed) break;
        }
        return new PanZoomPreflight(limit, tried);
    }
}

/// <summary>
/// Smooths a depth map for a camera move. The camera's depth is made of planes, and far away one plane is many meters from the next: a
/// surface that lies between two planes is a patchwork of both, and a moving camera pulls the patches apart. The smoother works on
/// disparity and follows the picture: it evens the disparity out inside regions of one color and keeps the steps where the picture has
/// edges. Disparity beside a depth step is trusted little, because the step of the camera's depth sits a few pixels off the true outline.
/// </summary>
public static class DepthSmoother
{
    /// <summary>The strength of the smoothing and the color difference that stops it, for a picture of about 2600 pixels.</summary>
    public const double Lambda = 100.0, ColorSigma = 6.0;
    /// <summary>The baseline (mm) of the disparity that depth steps are measured in, with the focal length of export level 1.</summary>
    public const double StepBaselineMm = 43.6;
    /// <summary>The trust in disparity near a depth step.</summary>
    const float EdgeTrust = 0.05f;
    /// <summary>How far the low trust reaches on the near side and on the far side of a depth step, pixels.</summary>
    const int NearReach = 8, FarReach = 2;
    const int Passes = 3;

    /// <summary>The smoothed depth (mm) of <paramref name="depth"/>, guided by <paramref name="guide"/> (the picture on the same grid).</summary>
    /// <param name="focalBaseline">Focal length × baseline that turns depth into the disparity the depth steps are measured in
    /// (pixels of export level 1 × mm).</param>
    public static Plane Smooth(Rgba guide, Plane depth, double focalBaseline, CancellationToken cancel = default)
    {
        int w = depth.W, h = depth.H; long n = (long)w * h;
        if (guide.W != w || guide.H != h) throw new ArgumentException("the guide and the depth differ in size");
        float fb = (float)focalBaseline;
        var d = new float[n];
        for (long i = 0; i < n; i++) { float z = depth.V[i]; d[i] = z > 0f && float.IsFinite(z) ? fb / Math.Max(z, 1f) : 0f; }
        // trust: low where something clearly farther lies within the near reach, or something clearly nearer within the far reach
        var farther = PanZoomSource.Extreme(d, w, h, NearReach, min: true); var nearer = PanZoomSource.Extreme(d, w, h, FarReach, min: false);
        var c = new float[n]; var dc = new float[n];
        for (long i = 0; i < n; i++)
        {
            bool nearSide = d[i] - farther[i] > Math.Max(4.0f, 0.08f * d[i]), farSide = nearer[i] - d[i] > Math.Max(4.0f, 0.08f * nearer[i]);
            c[i] = nearSide || farSide ? EdgeTrust : 1f;
            dc[i] = d[i] * c[i];
        }
        // the weights between neighbors: exp(−color distance / sigma), along rows and along columns
        var wx = new float[n]; var wy = new float[n];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x, p = i * 4;
                if (x + 1 < w) wx[i] = Weight(guide.P, p, p + 4);
                if (y + 1 < h) wy[i] = Weight(guide.P, p, p + (long)w * 4);
            }
        });
        cancel.ThrowIfCancellationRequested();
        Solve(dc, wx, wy, w, h); Solve(c, wx, wy, w, h);
        var o = new float[n]; float floor = fb / Export.DepthImageCache.Clip;
        for (long i = 0; i < n; i++)
        {
            float z = depth.V[i];
            o[i] = z > 0f && float.IsFinite(z) ? fb / Math.Max(dc[i] / Math.Max(c[i], 1e-4f), floor) : z;
        }
        return new Plane(w, h, o);
    }

    static float Weight(byte[] g, long a, long b)
    {
        int dr = g[a] - g[b], dg = g[a + 1] - g[b + 1], db = g[a + 2] - g[b + 2];
        return WineCrt.Expf(-(float)(Math.Sqrt(dr * dr + dg * dg + db * db) / ColorSigma));
    }

    /// <summary>The global smoother, in place: each pass solves every row, then every column, as a weighted least-squares problem in one
    /// dimension. The strength falls to a quarter from pass to pass.</summary>
    static void Solve(float[] v, float[] wx, float[] wy, int w, int h)
    {
        double lambda = Lambda;
        for (int pass = 0; pass < Passes; pass++, lambda *= 0.25)
        {
            float lam = (float)lambda;
            Parallel.For(0, h, () => (new float[w], new float[w]), (y, _, buf) => { Line(v, wx, (long)y * w, 1, w, lam, buf.Item1, buf.Item2); return buf; }, _ => { });
            Parallel.For(0, w, () => (new float[h], new float[h]), (x, _, buf) => { Line(v, wy, x, w, h, lam, buf.Item1, buf.Item2); return buf; }, _ => { });
        }
    }

    /// <summary>One line: solve (1 + λ·weights) v = v for the <paramref name="n"/> values from <paramref name="start"/> every
    /// <paramref name="step"/> (a tridiagonal system).</summary>
    static void Line(float[] v, float[] wgt, long start, int step, int n, float lam, float[] cp, float[] dp)
    {
        // row i: a·v[i−1] + b·v[i] + c·v[i+1] = v[i], with a = −λ·w[i−1], c = −λ·w[i], b = 1 − a − c
        float c0 = n > 1 ? -lam * wgt[start] : 0f, b0 = 1f - c0;
        cp[0] = c0 / b0; dp[0] = v[start] / b0;
        for (int i = 1; i < n; i++)
        {
            long q = start + (long)i * step;
            float a = -lam * wgt[q - step], c = i + 1 < n ? -lam * wgt[q] : 0f, b = 1f - a - c;
            float m = b - a * cp[i - 1];
            cp[i] = c / m; dp[i] = (v[q] - a * dp[i - 1]) / m;
        }
        v[start + (long)(n - 1) * step] = dp[n - 1];
        for (int i = n - 2; i >= 0; i--) { long q = start + (long)i * step; v[q] = dp[i] - cp[i] * v[q + step]; }
    }
}
