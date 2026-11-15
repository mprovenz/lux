using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lux.App.Viewer;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>An affine map (x, y) → (A·x + B·y + C, D·x + E·y + F).</summary>
internal readonly record struct Affine2(double A, double B, double C, double D, double E, double F)
{
    public static Affine2 FromSk(SKMatrix m) => new(m.ScaleX, m.SkewX, m.TransX, m.SkewY, m.ScaleY, m.TransY);
    public static Affine2 Scale(double sx, double sy) => new(sx, 0, 0, 0, sy, 0);
    public (double X, double Y) Map(double x, double y) => (A * x + B * y + C, D * x + E * y + F);
    /// <summary>This map, then <paramref name="next"/>.</summary>
    public Affine2 Then(Affine2 n) => new(n.A * A + n.B * D, n.A * B + n.B * E, n.A * C + n.B * F + n.C, n.D * A + n.E * D, n.D * B + n.E * E, n.D * C + n.E * F + n.F);
    public Affine2 Invert()
    {
        double det = A * E - B * D;
        if (Math.Abs(det) < 1e-12) return this;
        double ia = E / det, ib = -B / det, id = -D / det, ie = A / det;
        return new(ia, ib, -(ia * C + ib * F), id, ie, -(id * C + ie * F));
    }
}

/// <summary>What a layer tile is painted from: the tile's level-pixel rectangle and the map from its level pixels to the depth map.</summary>
internal readonly record struct LayerTile(TileKey Key, int Width, int Height, Affine2 TileToMap);

/// <summary>
/// A fake tile layer with the picture's own pyramid and geometry (it follows <see cref="FakeTileSource"/>): the depth view, the focal-region
/// shade, the quick-select / lasso mask. Its tiles are painted from <see cref="State"/> (an immutable snapshot the owner swaps with
/// <see cref="Bump"/>). Requests: newest wins, a short per-level delay, a few workers.
/// </summary>
internal sealed class FakeLayerSource : ITileSource, IDisposable
{
    readonly FakeTileSource _base;
    readonly Func<object?, LayerTile, byte[]?> _paint;
    readonly object _gate = new();
    readonly Dictionary<TileKey, (long Version, CancellationTokenSource Cts)> _inflight = new();
    readonly CancellationTokenSource _life = new();
    readonly SemaphoreSlim _workers = new(3);
    readonly FakeTimings _t;
    long _version = 1, _floor = 1;
    object? _state;

    /// <param name="sharesRequest">The layer shares the picture's one request (the depth view, as in the library) when the picture keeps one
    /// request at a time: a request of either cancels the work of the other.</param>
    public FakeLayerSource(FakeTileSource baseSource, Func<object?, LayerTile, byte[]?> paint, FakeTimings timings, string name, bool sharesRequest = false)
    {
        _base = baseSource;
        _paint = paint;
        _t = timings;
        Name = name;
        _shares = sharesRequest && baseSource.KeepsOneRequest;
        _base.Changed += () => { CancelAll(); Changed?.Invoke(); };
        if (_shares) _base.SharedRequest += CancelAll;
    }

    readonly bool _shares;

    void CancelAll() { lock (_gate) { foreach (var f in _inflight.Values) f.Cts.Cancel(); _inflight.Clear(); } }

    public string Name { get; }
    public int LevelCount => _base.LevelCount;
    public PixelSize LevelSize(int level) => _base.LevelSize(level);
    public int TileSize => _base.TileSize;
    public long GeometryVersion => _base.GeometryVersion;
    public int FinestLevel => _base.FinestLevel;
    public bool HasBefore => false;
    public long Version(int channel) { lock (_gate) return _version; }
    public long Floor(int channel) { lock (_gate) return _floor; }
    public event Action<TileImage>? TileReady;
    public event Action? Changed;
    public int Painted;
    public object? State { get { lock (_gate) return _state; } }

    /// <summary>New content: a new version (a drag frame keeps the floor, so the layer keeps moving without gaps).</summary>
    public void Bump(object? state, bool drag = false)
    {
        lock (_gate)
        {
            _state = state;
            _version++;
            if (!drag) _floor = _version;
        }
        Changed?.Invoke();
    }

    public void Request(TileRequest request)
    {
        if (_shares) _base.CancelForSharedRequest();
        var start = new List<(TileKey Key, long Version, long Geometry, object? State, CancellationTokenSource Cts)>();
        lock (_gate)
        {
            var wanted = request.Needed.Where(k => k.Level >= _base.FinestLevel && k.Level < LevelCount).ToHashSet();
            foreach (var (k, f) in _inflight.ToList())
                if (!wanted.Contains(k) || f.Version != _version) { f.Cts.Cancel(); _inflight.Remove(k); }
            foreach (var k in wanted)
            {
                if (_inflight.ContainsKey(k)) continue;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                _inflight[k] = (_version, cts);
                start.Add((k, _version, _base.GeometryVersion, _state, cts));
            }
        }
        var c = request.ImageRect.Center;
        foreach (var s in start.OrderByDescending(s => s.Key.Level).ThenBy(s => Distance(s.Key, c)))
            _ = Task.Run(() => RenderAsync(s.Key, s.Version, s.Geometry, s.State, s.Cts));
    }

    double Distance(TileKey k, Point c)
    {
        var (p, _, _) = _base.Snapshot();
        var r = p.TileImageRect(k);
        return (r.Center - c).X * (r.Center - c).X + (r.Center - c).Y * (r.Center - c).Y;
    }

    async Task RenderAsync(TileKey key, long version, long geometry, object? state, CancellationTokenSource cts)
    {
        var token = cts.Token;
        bool entered = false;
        try
        {
            await _workers.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            int delay = _t.TileDelayMs[Math.Clamp(key.Level, 0, _t.TileDelayMs.Length - 1)] / 3;
            if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
            var (p, m, g) = _base.Snapshot();
            if (g != geometry) return;
            var lr = p.TileLevelRect(key);
            int w = (int)lr.Width, h = (int)lr.Height;
            if (w <= 0 || h <= 0) return;
            var (fx, fy) = p.ScaleOf(key.Level);
            var toMap = Affine2.Scale(fx, fy).Then(new Affine2(1, 0, lr.X * fx, 0, 1, lr.Y * fy)).Then(Affine2.FromSk(m).Invert())
                .Then(Affine2.Scale(1.0 / FakeDepthMap.Divisor, 1.0 / FakeDepthMap.Divisor));
            var px = _paint(state, new LayerTile(key, w, h, toMap));
            token.ThrowIfCancellationRequested();
            if (px == null) return;
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            Bitmap bmp;
            try { bmp = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), w * 4); }
            finally { handle.Free(); }
            lock (_gate) if (_inflight.TryGetValue(key, out var cur) && cur.Cts == cts) _inflight.Remove(key);
            Interlocked.Increment(ref Painted);
            TileReady?.Invoke(new TileImage { Channel = Channels.After, Level = key.Level, Col = key.Col, Row = key.Row, Version = version, GeometryVersion = geometry, Bitmap = bmp });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (entered) _workers.Release();
            lock (_gate) if (_inflight.TryGetValue(key, out var cur) && cur.Cts == cts) _inflight.Remove(key);
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        lock (_gate) { foreach (var f in _inflight.Values) f.Cts.Cancel(); _inflight.Clear(); }
    }

    // ================================================================================================================ painters

    /// <summary>The depth view: near is light, far is dark (inverse depth between the legend's ends), the sky darkest.</summary>
    public static byte[] PaintDepth(float[] depth, FakeDepthMap map, double nearMm, double farMm, LayerTile t)
    {
        var px = new byte[t.Width * t.Height * 4];
        double inear = 1 / nearMm, ifar = 1 / farMm;
        for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++)
            {
                var (mx, my) = t.TileToMap.Map(x + 0.5, y + 0.5);
                float d = FakeDepthMap.Sample(depth, map.W, map.H, mx, my);
                byte g;
                if (!float.IsFinite(d)) g = 28;
                else
                {
                    double v = Math.Clamp((1 / d - ifar) / (inear - ifar), 0, 1);
                    g = (byte)(48 + Math.Pow(v, 0.8) * 194);
                }
                int o = (y * t.Width + x) * 4;
                px[o] = g; px[o + 1] = g; px[o + 2] = g; px[o + 3] = 255;
            }
        return px;
    }

    /// <summary>The focal-region shade: dark where the depth is outside [near, far].</summary>
    public static byte[]? PaintShade(float[] depth, FakeDepthMap map, double nearMm, double farMm, LayerTile t)
    {
        var px = new byte[t.Width * t.Height * 4];
        const byte A = 165;   // #262626 at 65 %, premultiplied
        byte c = (byte)(0x26 * A / 255);
        for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++)
            {
                var (mx, my) = t.TileToMap.Map(x + 0.5, y + 0.5);
                float d = FakeDepthMap.Sample(depth, map.W, map.H, mx, my);
                bool outside = !float.IsFinite(d) ? !double.IsInfinity(farMm) : d < nearMm || d > farMm;
                if (!outside) continue;
                int o = (y * t.Width + x) * 4;
                px[o] = c; px[o + 1] = c; px[o + 2] = c; px[o + 3] = A;
            }
        return px;
    }

    /// <summary>The selection mask: the accent colour, translucent inside, solid on the outline (S-Depth: "blue = selected").</summary>
    public static byte[]? PaintMask(byte[] mask, FakeDepthMap map, LayerTile t)
    {
        var px = new byte[t.Width * t.Height * 4];
        // #a9dcff
        const int R = 0xa9, G = 0xdc, B = 0xff;
        var (ex, _) = t.TileToMap.Map(1, 0);
        var (ox, _) = t.TileToMap.Map(0, 0);
        double step = Math.Max(0.35, Math.Abs(ex - ox) * 1.2);   // about one screen pixel in map pixels, for the outline
        for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++)
            {
                var (mx, my) = t.TileToMap.Map(x + 0.5, y + 0.5);
                float m = FakeDepthMap.SampleMask(mask, map.W, map.H, mx, my);
                if (m <= 0.02f) continue;
                bool edge = m >= 0.5f && (FakeDepthMap.SampleMask(mask, map.W, map.H, mx - step, my) < 0.5f || FakeDepthMap.SampleMask(mask, map.W, map.H, mx + step, my) < 0.5f
                                         || FakeDepthMap.SampleMask(mask, map.W, map.H, mx, my - step) < 0.5f || FakeDepthMap.SampleMask(mask, map.W, map.H, mx, my + step) < 0.5f);
                double a = edge ? 1.0 : 0.45 * m;
                int o = (y * t.Width + x) * 4;
                px[o] = (byte)(B * a); px[o + 1] = (byte)(G * a); px[o + 2] = (byte)(R * a); px[o + 3] = (byte)(255 * a);
            }
        return px;
    }
}
