using System.Collections.Concurrent;
using System.Collections.Immutable;
using Lux.App.Develop;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>
/// The fake capture's depth (design time and tests): the fake scene painted again as element ids in capture coordinates (sky, sun, two hill
/// ridges, the ground, 26 trees, a house with windows, a sign, a resolution star — the same geometry and random sequence as
/// <see cref="FakeScene"/> and <see cref="FakeThumbnails"/>), a depth per element (the ground by its distance below the horizon), and an
/// "analysed" depth with the faults a real depth map has: nearer depth bleeding a little over edges (what refine edge fixes) and the house's
/// windows read as far away (what blend area fixes). One map pixel is <see cref="Divisor"/> capture pixels.
/// </summary>
internal sealed class FakeDepthMap
{
    public const int Divisor = 8;
    public const byte Sky = 0, Sun = 1, FarHill = 2, NearHill = 3, Ground = 4, TreeBase = 10, House = 40, Sign = 41, Star = 42, Windows = 43;

    public int W { get; }
    public int H { get; }
    public int CaptureW { get; }
    public int CaptureH { get; }
    public byte[] Ids { get; }
    /// <summary>The scene's own depth (mm, +∞ for the sky), exact at every edge.</summary>
    public float[] True { get; }
    /// <summary>The depth an analysis would find (with the faults described above).</summary>
    public float[] Base { get; }
    public float NearestMm { get; }
    public float FarthestMm { get; }
    public float DefaultFocusMm { get; }
    /// <summary>Centres of a few elements in map pixels (synthetic saved edits use them).</summary>
    public IReadOnlyDictionary<byte, (float X, float Y, float Rx, float Ry)> Elements { get; }

    FakeDepthMap(int w, int h, int cw, int ch, byte[] ids, float[] truth, float[] analysed, Dictionary<byte, (float, float, float, float)> elements)
    {
        W = w; H = h; CaptureW = cw; CaptureH = ch; Ids = ids; True = truth; Base = analysed; Elements = elements;
        float near = float.MaxValue, far = 0;
        foreach (var d in analysed) if (float.IsFinite(d)) { near = Math.Min(near, d); far = Math.Max(far, d); }
        NearestMm = near == float.MaxValue ? 500 : near;
        FarthestMm = far <= 0 ? 100_000 : far;
        // the camera's focus point: the house when there is one (the fake's subject), else the centre
        int houseAt = Array.IndexOf(ids, House);
        float centre = houseAt >= 0 ? truth[houseAt] : analysed[(h / 2) * w + w / 2];
        DefaultFocusMm = float.IsFinite(centre) ? centre : (NearestMm + FarthestMm) / 8;
    }

    static readonly ConcurrentDictionary<(int, bool, bool, int, int, int), FakeDepthMap> Made = new();

    /// <summary>The map of a fake capture (made once per capture and kept).</summary>
    public static FakeDepthMap For(int index, bool stacked, bool telephoto, int orientation, int captureW, int captureH) =>
        Made.GetOrAdd((index, stacked, telephoto, orientation, captureW, captureH), k => Build(k.Item1, k.Item3, k.Item4, k.Item5, k.Item6));

    public bool IsSky(int i) => Ids[i] is Sky or Sun;

    static FakeDepthMap Build(int index, bool telephoto, int orientation, int cw, int ch)
    {
        int w = Math.Max(16, cw / Divisor), h = Math.Max(12, ch / Divisor);
        var ids = new byte[w * h];
        SKMatrix total;
        Layout layout;
        using (var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(new SKColor(Sky, 0, 0, 255));
                bool portrait = orientation is 90 or 270;
                int sw = portrait ? FakeScene.SceneHeight : FakeScene.SceneWidth, sh = portrait ? FakeScene.SceneWidth : FakeScene.SceneHeight;
                canvas.Scale(w / (float)cw, h / (float)ch);
                if (portrait)
                {
                    canvas.Translate(cw / 2f, ch / 2f);
                    canvas.RotateDegrees(-orientation);
                    canvas.Scale(ch / (float)FakeScene.SceneHeight, cw / (float)FakeScene.SceneWidth);
                    canvas.Translate(-FakeScene.SceneHeight / 2f, -FakeScene.SceneWidth / 2f);
                }
                else canvas.Scale(cw / (float)FakeScene.SceneWidth, ch / (float)FakeScene.SceneHeight);
                total = canvas.TotalMatrix;
                layout = DrawIds(canvas, index, telephoto, sw, sh);
                canvas.Flush();
            }
            var px = bmp.GetPixelSpan();
            for (int i = 0; i < w * h; i++) ids[i] = px[i * 4];
        }
        total.TryInvert(out var inv);
        var truth = new float[w * h];
        var elements = new Dictionary<byte, (float, float, float, float)>();
        var acc = new Dictionary<byte, (double X, double Y, double X2, double Y2, int N)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var s = inv.MapPoint(x + 0.5f, y + 0.5f);
                byte id = ids[i];
                truth[i] = id switch
                {
                    Sky or Sun => float.PositiveInfinity,
                    FarHill => 150_000,
                    NearHill => 40_000,
                    Ground => layout.Ground(s.Y),
                    House or Windows => layout.Ground(layout.HouseBase),
                    Sign => layout.Ground(layout.SignBase),
                    Star => layout.Ground(layout.StarBase),
                    >= TreeBase and < TreeBase + 26 => layout.Ground(layout.TreeBases[id - TreeBase]),
                    _ => layout.Ground(s.Y),
                };
                var a = acc.GetValueOrDefault(id);
                acc[id] = (a.X + x, a.Y + y, a.X2 + (double)x * x, a.Y2 + (double)y * y, a.N + 1);
            }
        foreach (var (id, a) in acc)
        {
            double mx = a.X / a.N, my = a.Y / a.N;
            elements[id] = ((float)mx, (float)my, (float)Math.Sqrt(Math.Max(0, a.X2 / a.N - mx * mx)), (float)Math.Sqrt(Math.Max(0, a.Y2 / a.N - my * my)));
        }
        // the analysed depth: the windows read as far, nearer depth bleeds two map pixels over every edge
        var analysed = (float[])truth.Clone();
        for (int i = 0; i < analysed.Length; i++) if (ids[i] == Windows) analysed[i] = 150_000;
        analysed = MinFilter(analysed, w, h, 2);
        for (int i = 0; i < analysed.Length; i++) if (ids[i] == Windows) analysed[i] = 150_000;
        return new FakeDepthMap(w, h, cw, ch, ids, truth, analysed, elements);
    }

    static float[] MinFilter(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = float.PositiveInfinity;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) m = Math.Min(m, src[y * w + k]);
                tmp[y * w + x] = m;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = float.PositiveInfinity;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) m = Math.Min(m, tmp[k * w + x]);
                dst[y * w + x] = m;
            }
        return dst;
    }

    sealed record Layout(float Horizon, float Height, float[] TreeBases, float HouseBase, float SignBase, float StarBase)
    {
        /// <summary>The ground's distance at scene row <paramref name="y"/>: about 1.8 m at the bottom, 9 m just below the near ridge, the house at 4.56 m.</summary>
        public float Ground(float y) => 912f / Math.Max((y - Horizon) / Height, 0.006f);
    }

    /// <summary>The scene's geometry drawn with one id per element (no antialiasing), in the same order and with the same random numbers as
    /// <see cref="FakeThumbnails.DrawScene"/> and <see cref="FakeScene.DrawDetails"/>.</summary>
    static Layout DrawIds(SKCanvas canvas, int index, bool telephoto, int Width, int Height)
    {
        static SKPaint P(byte id) => new() { Color = new SKColor(id, 0, 0, 255), IsAntialias = false, Style = SKPaintStyle.Fill };
        var rnd = new Random(index * 7717 + 3);
        float horizonY = Height * (0.45f + (float)rnd.NextDouble() * 0.2f);
        using (var p = P(Sky)) canvas.DrawRect(0, 0, Width, horizonY + 1, p);
        float sunX = Width * (0.15f + (float)rnd.NextDouble() * 0.7f), sunY = horizonY * (0.25f + (float)rnd.NextDouble() * 0.4f);
        using (var p = P(Sun)) canvas.DrawCircle(sunX, sunY, telephoto ? 34 : 14, p);
        for (int layer = 0; layer < 2; layer++)
        {
            using var path = new SKPath();
            float baseY = horizonY + layer * 18;
            path.MoveTo(0, Height);
            path.LineTo(0, baseY);
            int n = 6;
            for (int k = 1; k <= n; k++)
            {
                float x = Width * k / (float)n;
                float y = baseY - (float)rnd.NextDouble() * (telephoto ? 70 : 40) * (layer == 0 ? 1 : 0.6f);
                path.QuadTo(x - Width / (2f * n), y - 20, x, y);
            }
            path.LineTo(Width, Height);
            path.Close();
            using var p = P(layer == 0 ? FarHill : NearHill);
            canvas.DrawPath(path, p);
        }
        using (var p = P(Ground)) canvas.DrawRect(0, horizonY + 30, Width, Height, p);

        // the details (FakeScene.DrawDetails)
        float wscale = Width / 400f;
        var r2 = new Random(index * 131 + 7);
        var trees = new float[26];
        for (int i = 0; i < 26; i++)
        {
            float x = (float)r2.NextDouble() * Width, s = 4 + (float)r2.NextDouble() * 9;
            float y = horizonY + 4 + (float)r2.NextDouble() * 22;
            using var p = P((byte)(TreeBase + i));
            canvas.DrawRect(x - s * 0.08f, y - s * 0.4f, s * 0.16f, s * 0.9f, p);
            canvas.DrawCircle(x, y - s * 0.8f, s * 0.55f, p);
            canvas.DrawCircle(x - s * 0.15f, y - s * 0.95f, s * 0.25f, p);
            trees[i] = y + s * 0.5f;
        }
        float hx = (40 + (float)r2.NextDouble() * 180) * wscale, hy = horizonY + 30, hw = 46, hh = 30;
        using (var p = P(House))
        {
            canvas.DrawRect(hx, hy, hw, hh, p);
            using var roof = new SKPath();
            roof.MoveTo(hx - 4, hy); roof.LineTo(hx + hw / 2, hy - 16); roof.LineTo(hx + hw + 4, hy); roof.Close();
            canvas.DrawPath(roof, p);
        }
        using (var p = P(Windows))
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 4; c++)
                    canvas.DrawRect(hx + 4 + c * 10.5f, hy + 4 + r * 13, 7, 8, p);
        float sx = hx + hw + 14, sy = hy + 8;
        using (var p = P(Sign))
        {
            canvas.DrawRect(sx + 9, sy + 6, 1.2f, 14, p);
            canvas.DrawRect(sx, sy, 20, 7, p);
        }
        float cx = (300 + (float)r2.NextDouble() * 60) * wscale, cy = Math.Min(Height - 22, horizonY + 50), rad = 16;
        using (var p = P(Star)) canvas.DrawCircle(cx, cy, rad, p);
        return new Layout(horizonY, Height, trees, hy + hh, sy + 20, cy + rad);
    }

    // ================================================================================================================ sampling

    /// <summary>Bilinear depth at map coordinates (pixel centres at +0.5); the nearest value where the sky is involved.</summary>
    public static float Sample(float[] d, int w, int h, double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1), ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
        float a = d[ya * w + xa], b = d[ya * w + xb], c = d[yb * w + xa], e = d[yb * w + xb];
        if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(e))
            return d[(fy < 0.5 ? ya : yb) * w + (fx < 0.5 ? xa : xb)];
        return (float)((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + e * fx) * fy);
    }

    public static float SampleMask(byte[] m, int w, int h, double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1), ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
        return (float)(((m[ya * w + xa] * (1 - fx) + m[ya * w + xb] * fx) * (1 - fy) + (m[yb * w + xa] * (1 - fx) + m[yb * w + xb] * fx) * fy) / 255.0);
    }
}

// ==================================================================================================================== operations

/// <summary>A depth edit of the fake (points and radii in map pixels).</summary>
internal abstract record FakeDepthOp(string Label);

/// <summary>The fake's freehand edits.</summary>
internal enum FakeStroke { RefineEdge, BlendArea, MatchBrush }

internal sealed record StrokeDepthOp(FakeStroke Tool, ImmutableArray<(float X, float Y)> Points, float Radius, float TargetMm, bool Follow, string Label)
    : FakeDepthOp(Label);

internal sealed record MaskDepthOp(byte[] Mask, float TargetMm, string Label) : FakeDepthOp(Label);

/// <summary>The fake's depth tools on a map: brush, adaptive brush, lasso / quick-select masks, edge heal (the scene's own edges), surface heal
/// (a median), with the "nothing changed" reasons the app shows.</summary>
internal static class FakeDepthOps
{
    public const string NeedsBothSides = "Nothing changed: refine edge needs both sides of an edge under the brush. Paint along the edge so a little of each side is covered.";
    public const string AlreadyFollows = "Nothing changed: the depth already follows the edge here.";
    public const string AlreadyEven = "Nothing changed: the depth here is already even.";
    public const string AlreadyThere = "Nothing changed: this part already has that depth.";

    public static float[] Replay(FakeDepthMap map, IEnumerable<FakeDepthOp> ops)
    {
        var d = (float[])map.Base.Clone();
        foreach (var op in ops) Apply(map, d, op);
        return d;
    }

    /// <summary>Apply an edit in place; returns null when it changed the depth, else the reason nothing changed.</summary>
    public static string? Apply(FakeDepthMap map, float[] d, FakeDepthOp op) => op switch
    {
        StrokeDepthOp s => ApplyStroke(map, d, s),
        MaskDepthOp m => ApplyMask(map, d, m.Mask, m.TargetMm),
        _ => null,
    };

    /// <summary>The soft disc mask of a stroke over its bounding box (hardness 0.9, as Lumen's brush).</summary>
    public static (float[] Mask, int X0, int Y0, int Bw, int Bh) StrokeMask(FakeDepthMap map, IReadOnlyList<(float X, float Y)> pts, float r)
    {
        float minX = pts.Min(p => p.X) - r - 1, maxX = pts.Max(p => p.X) + r + 1, minY = pts.Min(p => p.Y) - r - 1, maxY = pts.Max(p => p.Y) + r + 1;
        int x0 = Math.Clamp((int)Math.Floor(minX), 0, map.W - 1), x1 = Math.Clamp((int)Math.Ceiling(maxX), 0, map.W - 1);
        int y0 = Math.Clamp((int)Math.Floor(minY), 0, map.H - 1), y1 = Math.Clamp((int)Math.Ceiling(maxY), 0, map.H - 1);
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        var mask = new float[bw * bh];
        for (int s = 0; s < pts.Count; s++)
        {
            var a = pts[s];
            var b = s + 1 < pts.Count ? pts[s + 1] : a;
            int sx0 = Math.Max(x0, (int)Math.Floor(Math.Min(a.X, b.X) - r - 1)), sx1 = Math.Min(x1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r + 1));
            int sy0 = Math.Max(y0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r - 1)), sy1 = Math.Min(y1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r + 1));
            double vx = b.X - a.X, vy = b.Y - a.Y, len2 = vx * vx + vy * vy;
            for (int y = sy0; y <= sy1; y++)
                for (int x = sx0; x <= sx1; x++)
                {
                    double px = x + 0.5 - a.X, py = y + 0.5 - a.Y;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp((px * vx + py * vy) / len2, 0, 1);
                    double dx = px - t * vx, dy = py - t * vy, dist = Math.Sqrt(dx * dx + dy * dy);
                    float m = dist <= r * 0.9 ? 1f : dist >= r ? 0f : (float)((r - dist) / (r * 0.1));
                    ref float slot = ref mask[(y - y0) * bw + (x - x0)];
                    if (m > slot) slot = m;
                }
        }
        return (mask, x0, y0, bw, bh);
    }

    static string? ApplyStroke(FakeDepthMap map, float[] d, StrokeDepthOp s)
    {
        float r = Math.Max(0.75f, s.Radius);
        var (mask, x0, y0, bw, bh) = StrokeMask(map, s.Points, r);
        switch (s.Tool)
        {
            case FakeStroke.RefineEdge:
            {
                var ids = new HashSet<float>();
                double worst = 0;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        if (mask[y * bw + x] < 0.5f) continue;
                        int i = (y + y0) * map.W + x + x0;
                        ids.Add(float.IsFinite(map.True[i]) ? (float)Math.Round(Math.Log(map.True[i]), 1) : float.PositiveInfinity);
                        worst = Math.Max(worst, RelDiff(d[i], map.True[i]));
                    }
                if (ids.Count < 2) return NeedsBothSides;
                if (worst < 0.01) return AlreadyFollows;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        float m = mask[y * bw + x];
                        if (m <= 0) continue;
                        int i = (y + y0) * map.W + x + x0;
                        d[i] = Lerp(d[i], map.True[i], m);
                    }
                return null;
            }
            case FakeStroke.BlendArea:
            {
                int R = (int)Math.Ceiling(Math.Max(3, r * 2));
                var updates = new List<(int I, float V, float M)>();
                var window = new List<float>(4 * R * R);
                double worst = 0;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        float m = mask[y * bw + x];
                        if (m <= 0) continue;
                        int cx = x + x0, cy = y + y0, i = cy * map.W + cx;
                        window.Clear();
                        for (int yy = Math.Max(0, cy - R); yy <= Math.Min(map.H - 1, cy + R); yy += 2)
                            for (int xx = Math.Max(0, cx - R); xx <= Math.Min(map.W - 1, cx + R); xx += 2)
                                window.Add(d[yy * map.W + xx]);
                        window.Sort();
                        float med = window[window.Count / 2];
                        worst = Math.Max(worst, RelDiff(d[i], med) * m);
                        updates.Add((i, med, m));
                    }
                if (worst < 0.01) return AlreadyEven;
                foreach (var (i, v, m) in updates) d[i] = Lerp(d[i], v, m);
                return null;
            }
            case FakeStroke.MatchBrush:
            {
                byte id0 = 255;
                if (s.Follow && s.Points.Length > 0)
                    id0 = map.Ids[Math.Clamp((int)s.Points[0].Y, 0, map.H - 1) * map.W + Math.Clamp((int)s.Points[0].X, 0, map.W - 1)];
                double worst = 0;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        float m = mask[y * bw + x];
                        int i = (y + y0) * map.W + x + x0;
                        if (s.Follow && map.Ids[i] != id0) m = 0;
                        mask[y * bw + x] = m;
                        if (m > 0) worst = Math.Max(worst, RelDiff(d[i], s.TargetMm) * m);
                    }
                if (worst < 0.01) return AlreadyThere;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        float m = mask[y * bw + x];
                        if (m <= 0) continue;
                        int i = (y + y0) * map.W + x + x0;
                        d[i] = Lerp(d[i], s.TargetMm, m);
                    }
                return null;
            }
            default:
                return null;
        }
    }

    static string? ApplyMask(FakeDepthMap map, float[] d, byte[] mask, float target)
    {
        double worst = 0;
        for (int i = 0; i < d.Length; i++) if (mask[i] > 0) worst = Math.Max(worst, RelDiff(d[i], target) * mask[i] / 255.0);
        if (worst < 0.01) return AlreadyThere;
        for (int i = 0; i < d.Length; i++) if (mask[i] > 0) d[i] = Lerp(d[i], target, mask[i] / 255f);
        return null;
    }

    static double RelDiff(float a, float b)
    {
        if (!float.IsFinite(a) && !float.IsFinite(b)) return 0;
        if (!float.IsFinite(a) || !float.IsFinite(b)) return 1;
        return Math.Abs(a - b) / Math.Max(1, Math.Min(a, b));
    }

    /// <summary>Interpolate in inverse depth (so the sky blends as "very far").</summary>
    static float Lerp(float a, float b, float m)
    {
        if (m >= 1) return b;
        if (m <= 0) return a;
        double ia = float.IsFinite(a) ? 1.0 / a : 0, ib = float.IsFinite(b) ? 1.0 / b : 0;
        double v = ia + (ib - ia) * m;
        return v <= 1e-9 ? float.PositiveInfinity : (float)(1 / v);
    }

    // ================================================================================================================ selections

    /// <summary>Quick select's fake: each add stroke selects the elements its brush touches (flood fill of the same element, kept near the
    /// stroke), each remove stroke takes them away again; the edge is softened by a pixel.</summary>
    public static byte[] QuickSelect(FakeDepthMap map, IReadOnlyList<(bool Add, IReadOnlyList<(float X, float Y)> Points, float Radius)> strokes)
    {
        var sel = new bool[map.W * map.H];
        int limit = (int)(Math.Max(map.W, map.H) * 0.22);
        foreach (var (add, pts, radius) in strokes)
        {
            if (pts.Count == 0) continue;
            var (mask, x0, y0, bw, bh) = StrokeMask(map, pts, Math.Max(0.75f, radius));
            int lx0 = Math.Max(0, x0 - limit), lx1 = Math.Min(map.W - 1, x0 + bw + limit), ly0 = Math.Max(0, y0 - limit), ly1 = Math.Min(map.H - 1, y0 + bh + limit);
            var seeds = new Queue<int>();
            var seen = new bool[map.W * map.H];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    if (mask[y * bw + x] >= 0.5f) { int i = (y + y0) * map.W + x + x0; seen[i] = true; seeds.Enqueue(i); }
            while (seeds.Count > 0)
            {
                int i = seeds.Dequeue();
                sel[i] = add;
                int x = i % map.W, y = i / map.W;
                byte id = map.Ids[i];
                void Visit(int nx, int ny)
                {
                    if (nx < lx0 || nx > lx1 || ny < ly0 || ny > ly1) return;
                    int j = ny * map.W + nx;
                    if (seen[j] || map.Ids[j] != id) return;
                    // the windows belong to the house for selecting
                    seen[j] = true;
                    seeds.Enqueue(j);
                }
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
        }
        // the windows inside a selected house come with it (a real quick select follows the outline, not the panes)
        for (int i = 0; i < sel.Length; i++)
            if (!sel[i] && map.Ids[i] == FakeDepthMap.Windows)
            {
                int x = i % map.W, y = i / map.W;
                if (x > 0 && sel[i - 1] || x < map.W - 1 && sel[i + 1] || y > 0 && sel[i - map.W] || y < map.H - 1 && sel[i + map.W]) sel[i] = true;
            }
        return Soften(sel, map.W, map.H, 1);
    }

    /// <summary>Match depth's brush as a selection (full map, 0–255): the stroke's soft disc mask; with <paramref name="follow"/> only where the
    /// element is the one under the stroke's first point (the fake's adaptive brush).</summary>
    public static byte[] BrushMask(FakeDepthMap map, IReadOnlyList<(float X, float Y)> pts, float r, bool follow)
    {
        var o = new byte[map.W * map.H];
        if (pts.Count == 0) return o;
        var (mask, x0, y0, bw, bh) = StrokeMask(map, pts, Math.Max(0.75f, r));
        byte id0 = map.Ids[Math.Clamp((int)pts[0].Y, 0, map.H - 1) * map.W + Math.Clamp((int)pts[0].X, 0, map.W - 1)];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int i = (y + y0) * map.W + x + x0;
                if (follow && map.Ids[i] != id0) continue;
                o[i] = (byte)Math.Clamp(Math.Round(mask[y * bw + x] * 255), 0, 255);
            }
        return o;
    }

    /// <summary>A filled polygon (map pixels), feathered by a box of <paramref name="featherPx"/> map pixels (Lumen: width · feather · 0.05).</summary>
    public static byte[] Polygon(FakeDepthMap map, IReadOnlyList<(float X, float Y)> poly, float featherPx)
    {
        var sel = new bool[map.W * map.H];
        if (poly.Count >= 3)
            for (int y = 0; y < map.H; y++)
            {
                double cy = y + 0.5;
                var xs = new List<double>();
                for (int k = 0; k < poly.Count; k++)
                {
                    var a = poly[k];
                    var b = poly[(k + 1) % poly.Count];
                    if ((a.Y <= cy && b.Y > cy) || (b.Y <= cy && a.Y > cy)) xs.Add(a.X + (cy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                    for (int x = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5)); x <= Math.Min(map.W - 1, (int)Math.Floor(xs[k + 1] - 0.5)); x++)
                        sel[y * map.W + x] = true;
            }
        return Soften(sel, map.W, map.H, Math.Max(1, (int)Math.Round(featherPx)));
    }

    /// <summary>A box blur of radius <paramref name="r"/> (running sums: the cost does not grow with the feather).</summary>
    static byte[] Soften(bool[] sel, int w, int h, int r)
    {
        var a = new float[sel.Length];
        for (int i = 0; i < sel.Length; i++) a[i] = sel[i] ? 1 : 0;
        var b = new float[sel.Length];
        var line = new double[Math.Max(w, h) + 1];
        for (int y = 0; y < h; y++)
        {
            line[0] = 0;
            for (int x = 0; x < w; x++) line[x + 1] = line[x] + a[y * w + x];
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                b[y * w + x] = (float)((line[x1 + 1] - line[x0]) / (x1 - x0 + 1));
            }
        }
        var o = new byte[sel.Length];
        for (int x = 0; x < w; x++)
        {
            line[0] = 0;
            for (int y = 0; y < h; y++) line[y + 1] = line[y] + b[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                o[y * w + x] = (byte)Math.Clamp(Math.Round((line[y1 + 1] - line[y0]) / (y1 - y0 + 1) * 255), 0, 255);
            }
        }
        return o;
    }

    /// <summary>The saved edits of a fake capture that has some (deterministic, plausible): blend the house's windows, refine the house's edge,
    /// match a tree's depth, quick-select the star …</summary>
    public static ImmutableList<FakeDepthOp> Synthetic(FakeDepthMap map, int count, double baseTargetMm)
    {
        var list = ImmutableList.CreateBuilder<FakeDepthOp>();
        var el = map.Elements;
        string target = DepthText.Distance(baseTargetMm);
        for (int k = 0; k < count; k++)
        {
            switch (k % 5)
            {
                case 0 when el.TryGetValue(FakeDepthMap.House, out var h):
                    list.Add(new StrokeDepthOp(FakeStroke.RefineEdge, ImmutableArray.Create((h.X - h.Rx * 1.7f, h.Y - h.Ry), (h.X - h.Rx * 1.7f, h.Y + h.Ry * 1.6f)), 3, 0, false, "Refine edge"));
                    break;
                case 1 when el.TryGetValue(FakeDepthMap.Windows, out var w):
                    list.Add(new StrokeDepthOp(FakeStroke.BlendArea, ImmutableArray.Create((w.X - w.Rx * 1.2f, w.Y), (w.X + w.Rx * 1.2f, w.Y)), Math.Max(3, w.Ry * 1.6f), 0, false, "Blend area"));
                    break;
                case 2 when el.TryGetValue(FakeDepthMap.Star, out var s):
                    list.Add(new MaskDepthOp(QuickSelect(map, new[] { (true, (IReadOnlyList<(float, float)>)new[] { (s.X, s.Y) }, 2f) }), (float)baseTargetMm, $"Match depth {target}"));
                    break;
                case 3 when el.TryGetValue(FakeDepthMap.Sign, out var g):
                    list.Add(new MaskDepthOp(QuickSelect(map, new[] { (true, (IReadOnlyList<(float, float)>)new[] { (g.X, g.Y) }, 1.5f) }), (float)baseTargetMm, $"Quick select · {target}"));
                    break;
                default:
                    if (el.TryGetValue((byte)(FakeDepthMap.TreeBase + k % 26), out var t))
                        list.Add(new StrokeDepthOp(FakeStroke.RefineEdge, ImmutableArray.Create((t.X - t.Rx * 2, t.Y), (t.X + t.Rx * 2, t.Y)), 2, 0, false, "Refine edge"));
                    else list.Add(new StrokeDepthOp(FakeStroke.BlendArea, ImmutableArray.Create((map.W * 0.5f, map.H * 0.8f)), 4, 0, false, "Blend area"));
                    break;
            }
        }
        return list.ToImmutable();
    }
}
