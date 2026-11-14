using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lux.App.Develop;
using Lux.App.Viewer;
using SkiaSharp;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Fake;

/// <summary>Simulated render times of the fake pyramid, ms per tile (coarse levels are quick, level 0 slow).</summary>
public sealed record FakeTimings
{
    public int[] TileDelayMs { get; init; } = { 260, 140, 70, 35, 18 };
    public int Workers { get; init; } = 3;
    /// <summary>Level the live picture comes at while a slider moves (finer requests are answered at this level).</summary>
    public int DragLevel { get; init; } = 3;
    /// <summary>Scale of the depth tools' simulated times (refine edge ~1.8 s, blend area ~0.7 s, a quick-select stroke ~0.25 s, the history
    /// rebuild 0.4 s a step, an analysis ~4 s at 1).</summary>
    public double ToolScale { get; init; } = 1;
    public static readonly FakeTimings Default = new();
    /// <summary>Fast enough for tests, slow enough that the order of arrivals is real.</summary>
    public static readonly FakeTimings Fast = new() { TileDelayMs = new[] { 12, 8, 5, 3, 1 }, Workers = 4, ToolScale = 0.05 };
    public static readonly FakeTimings Instant = new() { TileDelayMs = new[] { 0, 0, 0, 0, 0 }, Workers = 4, ToolScale = 0 };
}

/// <summary>
/// A synthetic picture pyramid for design time and tests, delivered like the library's: the picture as shown (turned, flipped, cropped —
/// or uncropped while the crop tool is open), 8320 × 6240 at level 0 for an uncropped wide capture (52 MP) halving down to level 4, 512-pixel
/// tiles painted by <see cref="FakeScene"/> after a per-level delay on a few workers. Requests: newest wins — work for tiles no longer needed
/// is cancelled, tiles still needed keep going. While a slider is dragged the edited channel answers at <see cref="FakeTimings.DragLevel"/>
/// (the lower-level picture); the refined level follows when the drag ends. The before channel has no adjustments.
/// </summary>
public sealed class FakeTileSource : ITileSource, IDisposable
{
    readonly object _gate = new();
    readonly int _index, _orientation;
    readonly bool _stacked, _telephoto;
    readonly PixelSize _capture;
    readonly IReadOnlyList<AdjustmentSpec> _specs;
    readonly FakeTimings _t;
    readonly SemaphoreSlim _workers;
    readonly Dictionary<(int Channel, TileKey Key), (long Version, long Geometry, CancellationTokenSource Cts)> _inflight = new();
    readonly CancellationTokenSource _life = new();
    readonly ConcurrentDictionary<TileKey, int[]> _histParts = new();
    IReadOnlyDictionary<string, double> _values;
    long _version = 1, _floor = 1, _geometry = 1, _beforeVersion = 1, _histogramVersion;
    bool _drag;
    int _finest;
    HistogramData? _histogram;
    Pyramid _pyramid;
    SKMatrix _captureToPicture = SKMatrix.Identity;

    public FakeTileSource(int index, bool stacked, bool telephoto, int orientation, PixelSize capture, int finestLevel,
                          IReadOnlyList<AdjustmentSpec> specs, IReadOnlyDictionary<string, double> values, FakeTimings? timings = null)
    {
        _index = index; _stacked = stacked; _telephoto = telephoto; _orientation = orientation;
        _capture = capture;
        _finest = finestLevel;
        _specs = specs;
        _values = values;
        _t = timings ?? FakeTimings.Default;
        _workers = new SemaphoreSlim(_t.Workers);
        _pyramid = Pyramid.Halving(capture, 5, 512);
    }

    public int TileSize => 512;
    public int LevelCount => 5;
    public PixelSize LevelSize(int level) { lock (_gate) return _pyramid.Size(level); }
    public long GeometryVersion { get { lock (_gate) return _geometry; } }
    /// <summary>The latest geometry is the previous picture turned by this (as the library reports it).</summary>
    public Turn? TurnFromPrevious { get; private set; }
    public int FinestLevel { get { lock (_gate) return _finest; } }
    public bool HasBefore => true;
    public long Version(int channel) { lock (_gate) return channel == Channels.Before ? _beforeVersion : _version; }
    public long Floor(int channel) { lock (_gate) return channel == Channels.Before ? _beforeVersion : _floor; }
    public event Action<TileImage>? TileReady;
    public event Action? Changed;
    /// <summary>The histogram of the current version (made from the coarsest level) changed.</summary>
    public event Action? HistogramChanged;
    public HistogramData? Histogram { get { lock (_gate) return _histogram; } }
    /// <summary>Tiles painted so far, per level (tests).</summary>
    public int[] Painted { get; } = new int[5];
    public int Cancelled;
    public List<TileRequest> Requests { get; } = new();
    public bool IsDragging { get { lock (_gate) return _drag; } }
    public PixelSize Capture => _capture;

    /// <summary>New adjustments: a new version; a committed one (not <paramref name="drag"/>) also raises the floor.</summary>
    public void SetAdjustments(IReadOnlyDictionary<string, double> values, bool drag)
    {
        lock (_gate)
        {
            _values = values;
            _version++;
            if (!drag) _floor = _version;
            _drag = drag;
        }
        Changed?.Invoke();
    }

    FakeEffect? _effect;

    /// <summary>The depth effect (null = off): both channels get a new version (the before picture keeps the depth effect, as the library's).</summary>
    internal void SetEffect(FakeEffect? effect, bool drag)
    {
        lock (_gate)
        {
            if (Equals(_effect, effect) && !drag && !_drag) return;
            _effect = effect;
            _version++;
            _beforeVersion++;
            if (!drag) _floor = _version;
            _drag = drag;
        }
        Changed?.Invoke();
    }

    internal FakeEffect? Effect { get { lock (_gate) return _effect; } }

    public event Action<TileRevalidation>? Revalidated;

    /// <summary>Tests: a change confined to <paramref name="changed"/> (normalised, every level): a new version (floor kept, as for a depth change)
    /// and the region report the library sends.</summary>
    public void ChangeOnly(Lux.Library.NormRect changed)
    {
        long v, g;
        lock (_gate) { _version++; v = _version; g = _geometry; }
        Revalidated?.Invoke(new TileRevalidation(Channels.After, v, g, new[] { (-1, changed) }));
        Changed?.Invoke();
    }

    /// <summary>The pyramid, the capture → picture map (full-size pixels) and the geometry version now.</summary>
    internal (Pyramid Pyramid, SKMatrix CaptureToPicture, long Geometry) Snapshot() { lock (_gate) return (_pyramid, _captureToPicture, _geometry); }

    /// <summary>A point of the picture as shown (normalised) in the capture's own coordinates (normalised, unrotated, uncropped).</summary>
    public Lux.Library.NormPoint PictureToCapture(Lux.Library.NormPoint p)
    {
        var (pyr, m, _) = Snapshot();
        var inv = Affine2.FromSk(m).Invert();
        var (cx, cy) = inv.Map(p.X * pyr.Image.Width, p.Y * pyr.Image.Height);
        return new Lux.Library.NormPoint(cx / _capture.Width, cy / _capture.Height);
    }

    /// <summary>A capture point (normalised) in the picture as shown (normalised; may fall outside 0–1 when cropped away).</summary>
    public Lux.Library.NormPoint CaptureToPicture(Lux.Library.NormPoint c)
    {
        var (pyr, m, _) = Snapshot();
        var (px, py) = Affine2.FromSk(m).Map(c.X * _capture.Width, c.Y * _capture.Height);
        return new Lux.Library.NormPoint(px / pyr.Image.Width, py / pyr.Image.Height);
    }

    /// <summary>
    /// The picture's geometry: the capture turned by <paramref name="turn"/> (the saved turn and flip plus the display rotation), cropped to
    /// <paramref name="crop"/> unless <paramref name="uncropped"/> (the crop tool). Every tile is new.
    /// </summary>
    public void SetGeometry(Turn turn, NormRect crop, bool uncropped, Turn? fromPrevious = null)
    {
        lock (_gate)
        {
            TurnFromPrevious = fromPrevious;
            var frame = turn.Apply(new Size(_capture.Width, _capture.Height));
            var m = turn.Matrix(_capture.Width, _capture.Height);
            double cx = uncropped ? 0 : crop.X * frame.Width, cy = uncropped ? 0 : crop.Y * frame.Height;
            int w = uncropped ? (int)frame.Width : Math.Max(1, (int)Math.Round(crop.Width * frame.Width));
            int h = uncropped ? (int)frame.Height : Math.Max(1, (int)Math.Round(crop.Height * frame.Height));
            _captureToPicture = new SKMatrix((float)m.M11, (float)m.M21, (float)(m.M31 - cx), (float)m.M12, (float)m.M22, (float)(m.M32 - cy), 0, 0, 1);
            _pyramid = Pyramid.Halving(new PixelSize(w, h), 5, 512);
            _geometry++;
            _version++;
            _floor = _version;
            _beforeVersion++;
            foreach (var f in _inflight.Values) f.Cts.Cancel();
            _inflight.Clear();
            _histParts.Clear();
        }
        Changed?.Invoke();
    }

    public void SetFinestLevel(int level)
    {
        lock (_gate) { if (_finest == level) return; _finest = level; }
        Changed?.Invoke();
    }

    public void Request(TileRequest request)
    {
        var start = new List<(TileKey Key, long Version, long Geometry, IReadOnlyDictionary<string, double> Values, CancellationTokenSource Cts, bool Drag)>();
        FakeEffect? effect;
        lock (_gate)
        {
            effect = _effect;
            Requests.Add(request);
            if (Requests.Count > 4000) Requests.RemoveRange(0, 2000);
            int ch = request.Channel;
            long version = ch == Channels.Before ? _beforeVersion : _version;
            var values = ch == Channels.Before ? (IReadOnlyDictionary<string, double>)new Dictionary<string, double>() : _values;
            bool drag = ch == Channels.After && _drag;
            var wanted = new HashSet<TileKey>();
            foreach (var k in request.Needed)
            {
                if (k.Level < _finest || k.Level >= LevelCount) continue;
                if (drag && k.Level < _t.DragLevel)
                    foreach (var parent in _pyramid.TilesIn(_t.DragLevel, _pyramid.TileImageRect(k).Deflate(0.01))) wanted.Add(parent);
                else wanted.Add(k);
            }
            foreach (var (id, f) in _inflight.ToList())
            {
                if (id.Channel != ch) continue;
                if (!wanted.Contains(id.Key) || f.Version != version)
                {
                    f.Cts.Cancel();
                    _inflight.Remove(id);
                    Interlocked.Increment(ref Cancelled);
                }
            }
            foreach (var key in wanted)
            {
                if (_inflight.ContainsKey((ch, key))) continue;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                _inflight[(ch, key)] = (version, _geometry, cts);
                start.Add((key, version, _geometry, values, cts, drag && key.Level == _t.DragLevel));
            }
        }
        // coarse first, so the picture is on screen at once and sharpens afterwards; within a level from the centre of the view outwards
        Pyramid pyr;
        lock (_gate) pyr = _pyramid;
        var centre = request.ImageRect.Center;
        double Dist(TileKey k) { var c = pyr.TileImageRect(k).Center - centre; return c.X * c.X + c.Y * c.Y; }
        // the workers are taken in that order (one task per tile racing for them let a loaded thread pool start a sharp tile before the coarse
        // ones, and the coarse picture came after the sharp tiles it should stand in for)
        var ordered = start.OrderByDescending(s => s.Key.Level).ThenBy(s => Dist(s.Key)).ToList();
        if (ordered.Count == 0) return;
        int channel = request.Channel;
        _ = Task.Run(async () =>
        {
            foreach (var (key, version, geometry, values, cts, drag) in ordered)
            {
                bool acquired = false;
                try { await _workers.WaitAsync(cts.Token).ConfigureAwait(false); acquired = true; }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                _ = Task.Run(() => RenderAsync(channel, key, version, geometry, values, cts, drag, effect, acquired));
            }
        });
    }

    /// <summary>Render one tile on a worker taken in request order (<paramref name="acquired"/>; false = it was cancelled while waiting, and only
    /// the bookkeeping runs).</summary>
    async Task RenderAsync(int channel, TileKey key, long version, long geometry, IReadOnlyDictionary<string, double> values, CancellationTokenSource cts, bool drag,
                           FakeEffect? effect, bool acquired)
    {
        var token = cts.Token;
        bool entered = acquired;
        try
        {
            if (!acquired) token.ThrowIfCancellationRequested();
            if (!acquired) { await _workers.WaitAsync(token).ConfigureAwait(false); entered = true; }
            int delay = _t.TileDelayMs[Math.Clamp(key.Level, 0, _t.TileDelayMs.Length - 1)];
            if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Pyramid p; SKMatrix m;
            lock (_gate) { if (geometry != _geometry) return; p = _pyramid; m = _captureToPicture; }
            var lr = p.TileLevelRect(key);
            var (fx, _) = p.ScaleOf(key.Level);
            int w = (int)lr.Width, h = (int)lr.Height;
            if (w <= 0 || h <= 0) return;
            using var filter = FakeScene.Filter(values, _specs);
            var tilePicture = new SKRect((float)(lr.X * fx), (float)(lr.Y * fx), (float)(lr.Right * fx), (float)(lr.Bottom * fx));
            using var sk = effect is { IsActive: true }
                ? FakeEffectRender.Paint(effect, _index, _stacked, _telephoto, _orientation, _capture, m, fx, lr, w, h, filter, FakeScene.OctavesForLevel(key.Level), token, p.Size(key.Level))
                : FakeScene.PaintTile(_index, _stacked, _telephoto, _orientation, _capture.Width, _capture.Height, m, (float)fx, tilePicture, w, h,
                    filter, FakeScene.OctavesForLevel(key.Level));
            token.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, sk.GetPixels(), new PixelSize(w, h), new Vector(96, 96), sk.RowBytes);
            if (channel == Channels.After && key.Level == LevelCount - 1) AddToHistogram(p, key, version, sk);
            lock (_gate)
            {
                if (_inflight.TryGetValue((channel, key), out var cur) && cur.Cts == cts) _inflight.Remove((channel, key));
                Painted[key.Level]++;
            }
            TileReady?.Invoke(new TileImage
            {
                Channel = channel, Level = key.Level, Col = key.Col, Row = key.Row, Version = version, GeometryVersion = geometry, Bitmap = bitmap, IsDragPreview = drag,
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (entered) _workers.Release();
            lock (_gate) if (_inflight.TryGetValue((channel, key), out var cur) && cur.Cts == cts) _inflight.Remove((channel, key));
            cts.Dispose();
        }
    }

    /// <summary>The histogram from the coarsest level's tiles of one version (published when they are all in).</summary>
    void AddToHistogram(Pyramid p, TileKey key, long version, SKBitmap sk)
    {
        var bins = new int[768];
        var span = sk.GetPixelSpan();
        for (int y = 0; y < sk.Height; y++)
        {
            int row = y * sk.RowBytes;
            for (int x = 0; x < sk.Width; x++)
            {
                int o = row + x * 4;
                bins[span[o + 2]]++;        // BGRA
                bins[256 + span[o + 1]]++;
                bins[512 + span[o]]++;
            }
        }
        var (cols, rows) = p.Grid(p.Coarsest);
        lock (_gate)
        {
            if (version < _histogramVersion) return;
            if (version > _histogramVersion) { _histParts.Clear(); _histogramVersion = version; }
            _histParts[key] = bins;
            if (_histParts.Count < cols * rows) return;
            var r = new int[256]; var g = new int[256]; var b = new int[256];
            foreach (var part in _histParts.Values)
                for (int i = 0; i < 256; i++) { r[i] += part[i]; g[i] += part[256 + i]; b[i] += part[512 + i]; }
            _histogram = new HistogramData(r, g, b);
        }
        HistogramChanged?.Invoke();
    }

    public void Dispose()
    {
        _life.Cancel();
        lock (_gate) { foreach (var f in _inflight.Values) f.Cts.Cancel(); _inflight.Clear(); }
    }
}
