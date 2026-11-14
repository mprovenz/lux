using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Lux.App.Viewer;

/// <summary>
/// A tile layer drawn over the picture (the quick-select mask, the focal-region shade): its own <see cref="ITileSource"/> with the picture's
/// tile grid and geometry, its own tile cache, the same level choice as the viewer (the coarsest level whole, then the visible tiles of the
/// target level). Tiles carry their own alpha; the layer is drawn with <see cref="Opacity"/>. Each screen slot is drawn once (its own tile or
/// the coarser tiles covering it), so a translucent layer never darkens twice. Nothing waits: arrivals are queued and applied once per UI turn.
/// </summary>
public sealed class TileOverlay : ViewerOverlay
{
    readonly OverlayLayer _layer;
    readonly TileCache _cache = new(96L << 20);
    readonly ConcurrentQueue<TileImage> _arrivals = new();
    readonly List<TileCache.Entry> _covering = new();
    ITileSource? _source;
    Pyramid? _pyramid;
    long _geometry, _signature, _frame, _requestId;
    int _arrivalPosted, _changedPosted;
    bool _requestPosted;
    double _opacity = 1;

    public TileOverlay(OverlayLayer layer = OverlayLayer.Mask) => _layer = layer;

    public override OverlayLayer Layer => _layer;
    public TileCache Cache => _cache;
    /// <summary>Requests made so far (tests).</summary>
    public int Requests { get; private set; }
    /// <summary>Tiles drawn in the last frame (tests).</summary>
    public int DrawnLastFrame { get; private set; }

    public double Opacity
    {
        get => _opacity;
        set { if (Math.Abs(_opacity - value) < 1e-9) return; _opacity = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    public ITileSource? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            if (_source != null) { _source.TileReady -= OnTile; _source.Changed -= OnChanged; }
            _source = value;
            _cache.Clear();
            while (_arrivals.TryDequeue(out var stale)) stale.Bitmap.Dispose();
            _signature = 0;
            _pyramid = null;
            if (_source != null)
            {
                _source.TileReady += OnTile;
                _source.Changed += OnChanged;
                _pyramid = Pyramid.Of(_source);
                _geometry = _source.GeometryVersion;
            }
            Invalidate();
        }
    }

    void OnTile(TileImage t)
    {
        _arrivals.Enqueue(t);
        if (Interlocked.Exchange(ref _arrivalPosted, 1) == 0) Dispatcher.UIThread.Post(ApplyArrivals, DispatcherPriority.Normal);
    }

    void OnChanged()
    {
        if (Interlocked.Exchange(ref _changedPosted, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _changedPosted, 0);
                if (_source == null) return;
                if (_source.GeometryVersion != _geometry) { _cache.Clear(); _pyramid = Pyramid.Of(_source); _geometry = _source.GeometryVersion; }
                _signature = 0;
                Invalidate();
            }, DispatcherPriority.Normal);
    }

    /// <summary>Take queued tiles (UI thread).</summary>
    public void ApplyArrivals()
    {
        Interlocked.Exchange(ref _arrivalPosted, 0);
        var src = _source;
        bool any = false;
        if (src != null && src.GeometryVersion != _geometry) { _cache.Clear(); _pyramid = Pyramid.Of(src); _geometry = src.GeometryVersion; _signature = 0; }
        while (_arrivals.TryDequeue(out var t))
        {
            if (src == null || _pyramid == null || t.GeometryVersion != _geometry || t.Level >= _pyramid.LevelCount) { t.Bitmap.Dispose(); continue; }
            any |= _cache.Add(t, src.Floor(Channels.After), _frame);
        }
        if (any) Invalidate();
    }

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        _frame++;
        DrawnLastFrame = 0;
        var src = _source;
        if (src == null) return;
        if (src.GeometryVersion != _geometry || _pyramid == null) { _cache.Clear(); _pyramid = Pyramid.Of(src); _geometry = src.GeometryVersion; _signature = 0; }
        var p = _pyramid;
        if (p.Image != t.ImageSize) return;   // another geometry than the picture's (a change in flight): draw nothing rather than misplace
        int level = p.LevelFor(t.PhysicalScale, src.FinestLevel, Viewer?.LevelBias ?? 0);
        var vis = ViewerMath.VisibleImageRect(t, bounds);
        if (vis.Width <= 0 || vis.Height <= 0) return;
        long cur = src.Version(Channels.After), floor = src.Floor(Channels.After);
        // ask for what is missing (posted: never inside the frame)
        var needed = new List<TileKey>();
        foreach (var k in p.TilesIn(p.Coarsest, p.ImageRect)) if (_cache.Get(k) is not { } e || e.Version < cur) needed.Add(k);
        if (level < p.Coarsest) foreach (var k in p.TilesIn(level, vis)) if (_cache.Get(k) is not { } e || e.Version < cur) needed.Add(k);
        long sig = cur * 1_000_003 + level * 7 + _geometry * 99_991;
        foreach (var k in needed) sig = sig * 31 + (k.Level * 7919L + k.Col) * 104729L + k.Row;
        if (sig == 0) sig = 1;
        if (sig != _signature && !_requestPosted)
        {
            _signature = sig;
            _requestPosted = true;
            var req = new TileRequest(Channels.After, level, vis, needed, ++_requestId) { ScreenPixels = new Size(vis.Width * t.PhysicalScale, vis.Height * t.PhysicalScale) };
            Dispatcher.UIThread.Post(() => { _requestPosted = false; Requests++; _source?.Request(req); }, DispatcherPriority.Normal);
        }
        using (context.PushOpacity(_opacity))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality, EdgeMode = EdgeMode.Aliased }))
            foreach (var slot in p.TilesIn(level, vis))
            {
                var slotImg = p.TileImageRect(slot);
                if (Pick(p, slot, slotImg, floor) is not { } chosen) continue;
                if (chosen.Count == 1) { Draw(context, t, p, chosen[0], slotImg); continue; }
                using (context.PushClip(ViewerMath.Snap(slotImg.TransformToAABB(t.ImageToView), t.RenderScaling)))
                    foreach (var e in chosen) Draw(context, t, p, e, slotImg);
            }
        _cache.Trim(_frame);
    }

    /// <summary>The tiles to draw for a slot: its own (newest worth showing), else the finest complete set of coarser tiles covering it.</summary>
    List<TileCache.Entry>? Pick(Pyramid p, TileKey slot, Rect slotImg, long floor)
    {
        var own = _cache.Get(slot);
        if (own != null && own.Version >= floor) return new List<TileCache.Entry>(1) { own };
        var inner = slotImg.Deflate(0.01);
        for (int k = slot.Level + 1; k <= p.Coarsest; k++)
        {
            _covering.Clear();
            bool all = true;
            foreach (var key in p.TilesIn(k, inner))
            {
                var e = _cache.Get(key);
                if (e == null || e.Version < floor) { all = false; break; }
                _covering.Add(e);
            }
            if (all && _covering.Count > 0) return new List<TileCache.Entry>(_covering);
        }
        return null;
    }

    void Draw(DrawingContext context, ViewTransform t, Pyramid p, TileCache.Entry e, Rect slotImg)
    {
        _cache.Touch(e, _frame);
        var tileImg = p.TileImageRect(e.Key);
        var part = tileImg.Intersect(slotImg);
        if (part.Width <= 0 || part.Height <= 0) return;
        var (fx, fy) = p.ScaleOf(e.Key.Level);
        var lr = p.TileLevelRect(e.Key);
        var srcRect = new Rect((part.X - tileImg.X) / fx, (part.Y - tileImg.Y) / fy, part.Width / fx, part.Height / fy)
            .Intersect(new Rect(0, 0, Math.Min(lr.Width, e.Bitmap.PixelSize.Width), Math.Min(lr.Height, e.Bitmap.PixelSize.Height)));
        if (srcRect.Width <= 0 || srcRect.Height <= 0) return;
        var dest = ViewerMath.Snap(part.TransformToAABB(t.ImageToView), t.RenderScaling);
        if (dest.Width <= 0 || dest.Height <= 0) return;
        context.DrawImage(e.Bitmap, srcRect, dest);
        DrawnLastFrame++;
    }
}
