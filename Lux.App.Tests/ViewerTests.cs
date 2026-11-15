using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lux.App.Viewer;
using Xunit;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Tests;

/// <summary>A tile source the test drives by hand: it records requests and delivers the tiles the test pushes (solid colours).</summary>
public sealed class ScriptedTileSource : ITileSource
{
    Pyramid _p;
    public ScriptedTileSource(PixelSize image, int levels = 5, int tile = 512, IReadOnlyList<PixelSize>? sizes = null)
    {
        _p = sizes != null ? new Pyramid(sizes, tile) : Pyramid.Halving(image, levels, tile);
    }

    public Pyramid Pyramid => _p;
    public int LevelCount => _p.LevelCount;
    public PixelSize LevelSize(int level) => _p.Size(level);
    public int TileSize => _p.TileSize;
    public long GeometryVersion { get; private set; } = 1;
    public int FinestLevel { get; set; }
    public bool HasBefore => true;
    public long[] Versions = { 1, 1 };
    public long[] Floors = { 1, 1 };
    public long Version(int channel) => Versions[channel];
    public long Floor(int channel) => Floors[channel];
    public List<TileRequest> Requests { get; } = new();
    public event Action<TileImage>? TileReady;
    public event Action? Changed;
    public void Request(TileRequest request) => Requests.Add(request);

    public void SetGeometry(Pyramid p) { _p = p; GeometryVersion++; Changed?.Invoke(); }
    public void RaiseChanged() => Changed?.Invoke();

    public static Bitmap Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), w * 4); }
        finally { handle.Free(); }
    }

    public void Deliver(TileKey k, Color c, long? version = null, int channel = 0, long? geometry = null)
    {
        var r = _p.TileLevelRect(k);
        TileReady?.Invoke(new TileImage
        {
            Channel = channel, Level = k.Level, Col = k.Col, Row = k.Row, Version = version ?? Versions[channel], GeometryVersion = geometry ?? GeometryVersion,
            Bitmap = Solid((int)r.Width, (int)r.Height, c),
        });
    }

    /// <summary>Deliver every tile the last request of a channel names.</summary>
    public int DeliverNeeded(Color c, int channel = 0, Func<TileKey, bool>? only = null)
    {
        var req = Requests.LastOrDefault(r => r.Channel == channel);
        if (req == null) return 0;
        int n = 0;
        foreach (var k in req.Needed) if (only?.Invoke(k) ?? true) { Deliver(k, c, channel: channel); n++; }
        return n;
    }
}

/// <summary>The viewer's arithmetic and behaviour: zoom / pan maths, clamping, high DPI, visible-rectangle requests, progressive swap without
/// gaps, stale tiles dropped, the memory bound, geometry changes.</summary>
public sealed class ViewerTests
{
    static (Window Window, PhotoViewer Viewer) Host(ITileSource? source, double w = 1000, double h = 750, double? scaling = null)
    {
        var v = new PhotoViewer { Background = Brushes.Gray, AnimateZoom = false, FitMargin = 0, RenderScalingOverride = scaling };
        var win = new Window { Width = w, Height = h, Content = v };
        win.Show();
        v.Source = source;
        Pump(win);
        return (win, v);
    }

    static void Pump(Window win)
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); win.RenderFrame(); }
        Dispatcher.UIThread.RunJobs();
    }

    // ------------------------------------------------------------------------------------------------ pure maths

    [Fact]
    public void Zoom_about_a_point_keeps_it_fixed_and_offsets_clamp()
    {
        var off = new Vector(-300, -100);
        var anchor = new Point(420, 260);
        double s0 = 0.5, s1 = 1.25;
        var img = (anchor - off) / s0;
        var off2 = ViewerMath.ZoomAbout(s0, off, s1, anchor);
        var back = (anchor - off2) / s1;
        Assert.Equal(img.X, back.X, 9);
        Assert.Equal(img.Y, back.Y, 9);
        // smaller than the view: centred; larger: no margin either side
        var c = ViewerMath.ClampOffset(new Vector(50, 50), 0.1, new Size(8320, 6240), new Size(1000, 750));
        Assert.Equal((1000 - 832) / 2.0, c.X, 6);
        Assert.Equal((750 - 624) / 2.0, c.Y, 6);
        var big = ViewerMath.ClampOffset(new Vector(100, -99999), 1, new Size(8320, 6240), new Size(1000, 750));
        Assert.Equal(0, big.X);
        Assert.Equal(750 - 6240, big.Y);
    }

    [Fact]
    public void Scale_range_and_high_dpi()
    {
        var (min, max) = ViewerMath.ScaleRange(new Size(1000, 750), new Size(8320, 6240), 2);
        Assert.Equal(1000 / 8320.0, min, 9);            // fit
        Assert.Equal(4, max, 9);                         // 8 physical px per image px at 2× = 4 view units
        Assert.Equal(100, ViewerMath.Percent(0.5, 2), 9); // 1:1 on a 2× screen is half a view unit per image pixel
        // a tiny picture: the range starts at 1:1, not below
        var (min2, _) = ViewerMath.ScaleRange(new Size(1000, 750), new Size(400, 300), 1);
        Assert.Equal(1, min2, 9);
    }

    [Fact]
    public void Level_choice_for_halving_and_export_rule_sizes()
    {
        var p = Pyramid.Halving(new PixelSize(8320, 6240), 5, 512);
        Assert.Equal(0, p.LevelFor(1.0, 0));
        Assert.Equal(0, p.LevelFor(0.51, 0));
        Assert.Equal(1, p.LevelFor(0.5, 0));
        Assert.Equal(3, p.LevelFor(0.12, 0));
        Assert.Equal(4, p.LevelFor(0.01, 0));
        Assert.Equal(1, p.LevelFor(1.0, 1));             // capped by the quality
        Assert.Equal(4, p.LevelFor(0.2, 0, bias: 2));    // biased coarser
        // the library's sizes are not exact halvings (export window × crop): choose by the sizes themselves
        var q = new Pyramid(new[] { new PixelSize(8000, 6000), new PixelSize(4160, 3120), new PixelSize(2080, 1560), new PixelSize(1040, 780), new PixelSize(520, 390) }, 512);
        Assert.Equal(1, q.LevelFor(0.5, 0));             // 4160 / 8000 = 0.52 ≥ 0.5
        Assert.Equal(0, q.LevelFor(0.53, 0));
        var (fx, fy) = q.ScaleOf(1);
        Assert.Equal(8000 / 4160.0, fx, 9);
        Assert.Equal(6000 / 3120.0, fy, 9);
    }

    [Fact]
    public void Tiles_cover_each_level_exactly_once()
    {
        var p = new Pyramid(new[] { new PixelSize(8001, 5999), new PixelSize(4001, 3000), new PixelSize(2000, 1500), new PixelSize(1000, 751), new PixelSize(500, 375) }, 512);
        for (int l = 0; l < p.LevelCount; l++)
        {
            var tiles = p.TilesIn(l, p.ImageRect).ToList();
            var (cols, rows) = p.Grid(l);
            Assert.Equal(cols * rows, tiles.Count);
            double area = tiles.Sum(k => p.TileImageRect(k).Width * p.TileImageRect(k).Height);
            Assert.Equal(p.Image.Width * (double)p.Image.Height, area, 0);
        }
        // a rectangle inside one tile names just that tile
        Assert.Single(p.TilesIn(0, new Rect(10, 10, 100, 100)));
    }

    [Fact]
    public void Turns_compose_like_the_buttons()
    {
        var t = Turn.Identity;
        for (int i = 0; i < 4; i++) t = t.RotateRight();
        Assert.Equal(Turn.Identity, t);
        Assert.Equal(Turn.Identity, Turn.Identity.FlipHorizontal().FlipHorizontal());
        Assert.Equal(Turn.Identity, Turn.Identity.FlipVertical().FlipVertical());
        // composition matches the matrices on a point
        var a = new Turn(1, false); var b = new Turn(0, true);
        var ab = a.Then(b);
        var p = new Point(10, 20);
        var viaMatrices = p.Transform(a.Matrix(100, 50)).Transform(b.Matrix(50, 100));
        var viaTurn = p.Transform(ab.Matrix(100, 50));
        Assert.Equal(viaMatrices, viaTurn);
        // a crop rectangle turned right and back is the same rectangle
        var r = new NormRect(0.1, 0.2, 0.3, 0.4);
        Assert.True(r.RotateRight().RotateLeft().Near(r));
        Assert.True(r.FlipHorizontal().FlipHorizontal().Near(r));
    }

    [Fact]
    public void Crop_drag_keeps_the_aspect_and_stays_inside()
    {
        var frame = new Size(8320, 6240);
        var start = new Rect(1000, 1000, 4000, 3000);
        double ratio = 16 / 9.0;
        foreach (var h in new[] { CropMath.Handle.TopLeft, CropMath.Handle.BottomRight, CropMath.Handle.Right, CropMath.Handle.Top })
        {
            var r = CropMath.Drag(start, h, new Vector(900, 700), ratio, frame, 64);
            Assert.Equal(ratio, r.Width / r.Height, 6);
            Assert.True(r.Left >= -1e-9 && r.Top >= -1e-9 && r.Right <= frame.Width + 1e-9 && r.Bottom <= frame.Height + 1e-9, $"{h}: {r}");
        }
        // free: a corner follows the pointer; never smaller than the minimum
        var free = CropMath.Drag(start, CropMath.Handle.BottomRight, new Vector(-5000, -5000), 0, frame, 64);
        Assert.Equal(64, free.Width, 6);
        Assert.Equal(64, free.Height, 6);
        // moving stays inside
        var moved = CropMath.Drag(start, CropMath.Handle.Move, new Vector(99999, 99999), 0, frame, 64);
        Assert.Equal(frame.Width, moved.Right, 6);
        Assert.Equal(frame.Height, moved.Bottom, 6);
        // the largest 1:1 inside a 4:3 crop
        var sq = CropMath.Fit(new Rect(0, 0, 400, 300), 1);
        Assert.Equal(new Rect(50, 0, 300, 300), sq);
        Assert.Equal(CropMath.Handle.TopLeft, CropMath.HitTest(new Rect(100, 100, 200, 100), new Point(104, 98), 12));
        Assert.Equal(CropMath.Handle.Move, CropMath.HitTest(new Rect(100, 100, 200, 100), new Point(200, 150), 12));
        Assert.Equal(CropMath.Handle.None, CropMath.HitTest(new Rect(100, 100, 200, 100), new Point(10, 10), 12));
    }

    // ------------------------------------------------------------------------------------------------ the control

    [AvaloniaFact]
    public void Fit_then_one_to_one_and_wheel_zoom_about_the_pointer()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        Assert.True(v.IsFitted);
        Assert.Equal(1000 / 8320.0, v.Scale, 9);
        Assert.Equal(new Rect(0, 0, 1000, 750), v.Transform()!.DisplayRect);
        v.ZoomToActual(new Point(500, 375));
        Assert.Equal(1, v.Scale, 9);
        Assert.Equal(100, v.ZoomPercent, 6);
        // the picture point under the pointer stays put through a wheel step
        var t0 = v.Transform()!;
        var under = t0.ToImage(new Point(300, 200));
        win.MouseWheel(new Point(300, 200), new Vector(0, 1));
        Pump(win);
        var t1 = v.Transform()!;
        Assert.Equal(1.25, t1.Scale, 9);
        var p = t1.ToView(under);
        Assert.Equal(300, p.X, 6);
        Assert.Equal(200, p.Y, 6);
        // clamped at 800 %
        for (int i = 0; i < 30; i++) v.ZoomBy(2);
        Assert.Equal(8, v.Scale, 9);
        // and back to fit
        v.ZoomToFit();
        Assert.True(v.IsFitted);
        win.Close();
    }

    [AvaloniaFact]
    public void High_dpi_one_to_one_is_one_physical_pixel()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src, scaling: 2);
        v.ZoomToActual();
        Assert.Equal(0.5, v.Scale, 9);
        Assert.Equal(100, v.ZoomPercent, 6);
        Assert.Equal(0, v.TargetLevel);
        v.ZoomToPercent(50);
        Assert.Equal(1, v.TargetLevel);
        win.Close();
    }

    [AvaloniaFact]
    public void Requests_name_the_visible_tiles_and_the_coarse_picture()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        // fitted: the coarse level whole (520 × 390 at level 4 = 2 × 1 tiles) and the target level 3 (1040 × 780 = 3 × 2 tiles)
        var first = src.Requests.Last();
        Assert.Equal(3, first.Level);
        Assert.Equal(2 + 6, first.Needed.Count);
        Assert.Contains(first.Needed, k => k.Level == 4);
        src.DeliverNeeded(Colors.Red);
        Pump(win);
        Assert.Equal(3, v.LevelOnScreen);
        // 1:1 at the centre: level 0, the visible 1000 × 750 picture pixels about the centre, only the tiles there
        v.ZoomToActual(new Point(500, 375));
        Pump(win);
        var r = src.Requests.Last();
        Assert.Equal(0, r.Level);
        Assert.Equal(1000, r.ImageRect.Width, 6);
        Assert.Equal(750, r.ImageRect.Height, 6);
        Assert.Equal(4160, r.ImageRect.Center.X, 6);
        Assert.All(r.Needed, k => Assert.Equal(0, k.Level));
        Assert.InRange(r.Needed.Count, 4, 9);
        Assert.Equal(1000, r.ScreenPixels.Width, 6);
        // a repeat of the same view asks nothing new
        int before = src.Requests.Count;
        v.RequestNow();
        Assert.Equal(before, src.Requests.Count);
        // after the tiles arrive, a small pan asks only for the newly visible column
        src.DeliverNeeded(Colors.Green);
        Pump(win);
        v.PanBy(new Vector(-600, 0));
        Pump(win);
        var after = src.Requests.Last();
        Assert.All(after.Needed, k => Assert.True(k.Level == 0 && !r.Needed.Contains(k)));
        win.Close();
    }

    [AvaloniaFact]
    public void Progressive_swap_has_no_gaps_and_sharper_tiles_replace_coarse_ones()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        v.ZoomToPercent(25, new Point(500, 375));          // level 2
        Pump(win);
        var req = src.Requests.Last();
        Assert.Equal(2, req.Level);
        // nothing yet: gaps (the stand-in or the stage shows)
        v.Stats.Reset();
        v.InvalidateVisual();
        Pump(win);
        Assert.True(v.Stats.FramesWithGaps > 0);
        // the coarse picture: from now on every frame is covered
        src.DeliverNeeded(Colors.Red, only: k => k.Level == 4);
        Pump(win);
        v.Stats.Reset();
        Assert.Equal(4, v.LevelOnScreen);
        // the target tiles one by one: never a gap, the level on screen moves to 2 once they are in
        foreach (var k in req.Needed.Where(k => k.Level == 2))
        {
            src.Deliver(k, Colors.Blue);
            Pump(win);
            Assert.Equal(0, v.Stats.FramesWithGaps);
        }
        Assert.Equal(2, v.LevelOnScreen);
        Assert.True(v.Stats.MaxTilesDrawn >= req.Needed.Count(k => k.Level == 2));
        Assert.All(v.Stats.TileLatencyMs, ms => Assert.InRange(ms, 0, 5000));
        win.Close();
    }

    [AvaloniaFact]
    public void Stale_tiles_are_dropped_and_newer_versions_win()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        var coarse = new TileKey(4, 0, 0);
        src.Versions[0] = 3; src.Floors[0] = 2;
        src.Deliver(coarse, Colors.Red, version: 3);
        src.Deliver(coarse, Colors.Blue, version: 2);    // late arrival of an older version: dropped
        src.Deliver(new TileKey(4, 1, 0), Colors.Blue, version: 1);   // older than the floor: dropped
        src.Deliver(new TileKey(4, 1, 0), Colors.Blue, version: 1, geometry: 0);   // an older geometry: dropped before the cache
        Pump(win);
        var cache = v.CacheOf(0);
        Assert.Equal(3, cache.Get(coarse)!.Version);
        Assert.Null(cache.Get(new TileKey(4, 1, 0)));
        Assert.Equal(2, cache.DroppedStale);
        // a drag in progress: newer versions above the floor are kept while the current one is made
        src.Versions[0] = 5;
        src.Deliver(coarse, Colors.Green, version: 4);
        Pump(win);
        Assert.Equal(4, cache.Get(coarse)!.Version);
        win.Close();
    }

    [AvaloniaFact]
    public void Tile_memory_stays_within_the_budget()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        v.TileBudget = 24L << 20;                      // 24 MB: about 24 full 512-pixel tiles
        v.ZoomToActual(new Point(0, 0));
        Pump(win);
        src.DeliverNeeded(Colors.Red);
        Pump(win);
        // sweep the whole 52 MP picture at 1:1 (every level-0 tile is delivered once)
        for (int y = 0; y < 6240; y += 700)
            for (int x = 0; x < 8320; x += 900)
            {
                v.PanBy(new Vector(-(x + v.Offset.X), -(y + v.Offset.Y)));
                Pump(win);
                src.DeliverNeeded(Colors.Green);
                Pump(win);
            }
        var cache = v.CacheOf(0);
        long tileBytes = 512L * 512 * 4;
        long visible = (long)v.Stats.MaxTilesDrawn * tileBytes;
        Assert.True(cache.Evicted > 0);
        Assert.True(cache.PeakBytes <= v.TileBudget + visible, $"peak {cache.PeakBytes >> 20} MB, budget {v.TileBudget >> 20} MB + visible {visible >> 20} MB");
        Assert.True(cache.Bytes <= v.TileBudget + visible);
        win.Close();
    }

    [AvaloniaFact]
    public void Geometry_change_drops_tiles_and_holds_the_old_picture_turned()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        src.DeliverNeeded(Colors.Red);
        Pump(win);
        Assert.True(v.CacheOf(0).Count > 0);
        // rotate right: the page warns the viewer, the source's picture becomes portrait
        v.ExpectGeometryChange(Turn.Identity.RotateRight());
        src.SetGeometry(Pyramid.Halving(new PixelSize(6240, 8320), 5, 512));
        Pump(win);
        Assert.Equal(0, v.CacheOf(0).Count);
        Assert.True(v.IsHolding);
        Assert.Equal(6240, v.Pyramid!.Image.Width);
        Assert.True(v.IsFitted);
        Assert.Equal(750 / 8320.0, v.Scale, 9);
        // the new coarse picture ends the hold
        src.DeliverNeeded(Colors.Blue, only: k => k.Level == 4);
        Pump(win);
        Assert.False(v.IsHolding);
        win.Close();
    }

    [AvaloniaFact]
    public void Crop_overlay_drags_handles_with_real_pointer_events()
    {
        var src = new ScriptedTileSource(new PixelSize(8000, 6000));
        var (win, v) = Host(src, 800, 600);
        var crop = new CropOverlay { Rect = NormRect.Full, Ratio = 0 };
        v.AddOverlay(crop);
        Pump(win);
        Assert.True(v.LocksView);
        // drag the bottom-right corner 200 view units up-left: the crop shrinks by a quarter
        win.MouseDown(new Point(799, 599), Avalonia.Input.MouseButton.Left);
        win.MouseMove(new Point(700, 500));
        win.MouseMove(new Point(599, 449));
        win.MouseUp(new Point(599, 449), Avalonia.Input.MouseButton.Left);
        Pump(win);
        Assert.Equal(0.75, crop.Rect.Width, 2);
        Assert.Equal(0.75, crop.Rect.Height, 2);
        // with a 1:1 lock a corner keeps the square
        crop.Ratio = 1;
        crop.Rect = new NormRect(0.1, 0.1, 0.6, 0.8);
        win.MouseDown(v.Transform()!.FrameToView(new Point(0.7, 0.9)), Avalonia.Input.MouseButton.Left);
        win.MouseMove(v.Transform()!.FrameToView(new Point(0.6, 0.7)));
        win.MouseUp(v.Transform()!.FrameToView(new Point(0.6, 0.7)), Avalonia.Input.MouseButton.Left);
        var px = crop.Rect.ToPixels(new Size(8000, 6000));
        Assert.Equal(1, px.Width / px.Height, 3);
        // the wheel does not zoom while the frame is up
        double s = v.Scale;
        win.MouseWheel(new Point(400, 300), new Vector(0, 3));
        Assert.Equal(s, v.Scale);
        win.Close();
    }

    [AvaloniaFact]
    public void Split_comparison_requests_both_channels_and_the_divider_drags()
    {
        var src = new ScriptedTileSource(new PixelSize(8320, 6240));
        var (win, v) = Host(src);
        v.Compare = CompareMode.Split;
        Pump(win);
        // one channel at a time (a source may keep one request at a time): the edited side first, the before side when it has its tiles
        Assert.Contains(src.Requests, r => r.Channel == Channels.After);
        Assert.DoesNotContain(src.Requests, r => r.Channel == Channels.Before);
        Assert.True(src.DeliverNeeded(Colors.Gray, Channels.After) > 0);
        Pump(win);
        Assert.Contains(src.Requests, r => r.Channel == Channels.Before);
        win.MouseDown(new Point(500, 300), Avalonia.Input.MouseButton.Left);
        win.MouseMove(new Point(250, 300));
        win.MouseUp(new Point(250, 300), Avalonia.Input.MouseButton.Left);
        Assert.Equal(0.25, v.SplitPosition, 2);
        win.Close();
    }
}

/// <summary>The tile-swap policy: a new version replaces the picture as a whole while dragging and when it is quick;
/// otherwise centre-outwards with a short fade; never a patchwork of versions.</summary>
public sealed class TileSwapTests
{
    static async Task<(Harness H, Lux.App.Viewer.PhotoViewer V, Lux.App.Fake.FakeDevelopSession S)> Open(Lux.App.Fake.FakeTimings timings)
    {
        var h = await DevelopHarness.OpenAsync(new Lux.App.Fake.FakeDevelopOptions { Timings = timings, OpenDelayMs = 0, SaveDelayMs = 20 });
        var v = h.Viewer();
        v.AnimateZoom = false;
        v.ZoomToPercent(50, animate: false);                    // level 1: twelve-odd tiles in view
        h.Session().FakeDepth.SetEffect(null, h.Session().FakeDepth.FocusMm);   // the policy, not the fake's depth-effect blur
        await h.SettlePictureAsync();
        return (h, v, h.Session());
    }

    /// <summary>Render frames for <paramref name="ms"/> (a frame each UI turn), as a desktop would.</summary>
    static async Task Frames(Harness h, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Window.RenderFrame();
            await Task.Delay(8);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_quick_refine_swaps_the_whole_picture_at_once()
    {
        // level-1 tiles in 20 ms each on 4 workers: a dozen tiles arrive well inside the 300 ms window
        var (h, v, s) = await Open(new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 30, 20, 10, 5, 2 }, Workers = 4, ToolScale = 0.05 });
        await using var _ = h;
        v.Stats.Reset();
        s.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, 0.8);
        await Frames(h, 700);
        await h.SettlePictureAsync();
        var st = $"mixed {v.Stats.MixedFrames} swaps {v.Stats.Swaps} held {v.Stats.HeldFrames} faded {v.Stats.FadedTiles} level {v.LevelOnScreen}/{v.TargetLevel}";
        Xunit.Assert.True(v.Stats.MixedFrames == 0, st);
        Xunit.Assert.True(v.Stats.Swaps >= 1 && v.Stats.HeldFrames > 0, st);
        Xunit.Assert.True(v.Stats.Swaps >= 1);
        Xunit.Assert.True(v.Stats.HeldFrames > 0);
        Xunit.Assert.Equal(0, v.Stats.FadedTiles);
        Xunit.Assert.True(v.CacheOf(0).PreviousBytes == 0);              // the kept tiles are released after the swap
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_slow_refine_goes_centre_outwards_with_a_fade()
    {
        // 160 ms a tile on 2 workers: the window passes long before the dozen tiles are in
        var (h, v, s) = await Open(new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 260, 160, 60, 20, 5 }, Workers = 2, ToolScale = 0.05 });
        await using var _ = h;
        v.Swap.FadeMs = 400;                                             // long enough to catch in headless frames
        v.Stats.Reset();
        long before = s.Tiles.Version(0);
        s.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, 0.8);
        await Frames(h, 250);
        Xunit.Assert.True(v.Stats.HeldFrames > 0, $"mixed {v.Stats.MixedFrames} swaps {v.Stats.Swaps} held {v.Stats.HeldFrames} faded {v.Stats.FadedTiles}");   // the window: the old picture
        Xunit.Assert.Equal(0, v.Stats.FadedTiles);
        await Frames(h, 900);
        Xunit.Assert.True(v.Stats.FadedTiles > 0, $"mixed {v.Stats.MixedFrames} swaps {v.Stats.Swaps} held {v.Stats.HeldFrames} faded {v.Stats.FadedTiles}");   // then tiles fade in as they come
        // after the window the new version's whole coarse picture stands in where the sharp tiles have not come yet: never old next to new
        Xunit.Assert.True(v.Stats.MixedFrames == 0, $"mixed {v.Stats.MixedFrames}");
        // the fake renders the visible tiles from the centre outwards
        var req = s.FakeTiles.Requests.Last(r => r.Level == 1);
        await h.SettlePictureAsync(() => v.Stats.Swaps > 0, timeoutMs: 15000);
        Xunit.Assert.True(v.Stats.Swaps > 0);
        Xunit.Assert.True(s.Tiles.Version(0) > before);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task While_dragging_the_picture_changes_only_as_a_whole_and_the_policy_can_be_turned_off()
    {
        var (h, v, s) = await Open(new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 30, 20, 10, 5, 2 }, Workers = 4, ToolScale = 0.05 });
        await using var _ = h;
        v.Stats.Reset();
        v.SwapTrace = new List<string>();
        s.BeginDrag(Lux.App.Develop.AdjustmentKeys.Exposure);
        for (int i = 1; i <= 12; i++) { s.DragTo(Lux.App.Develop.AdjustmentKeys.Exposure, 0.1 * i); await Frames(h, 40); }
        s.EndDrag(Lux.App.Develop.AdjustmentKeys.Exposure);
        await Frames(h, 500);
        await h.SettlePictureAsync();
        var st = $"mixed {v.Stats.MixedFrames} swaps {v.Stats.Swaps} held {v.Stats.HeldFrames} faded {v.Stats.FadedTiles} level {v.LevelOnScreen}/{v.TargetLevel}";
        System.IO.File.WriteAllLines(System.IO.Path.Combine(Paths.M3Scratch, "swaptrace.txt"), v.SwapTrace!);
        v.SwapTrace = null;
        Xunit.Assert.True(v.Stats.MixedFrames == 0, st);
        Xunit.Assert.True(v.Stats.Swaps >= 2, st);
        // off: tiles shown as each arrives
        v.Swap.Enabled = false;
        v.Stats.Reset();
        s.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, -0.5);
        await Frames(h, 400);
        Xunit.Assert.Equal(0, v.Stats.Swaps);
        Xunit.Assert.Equal(0, v.Stats.HeldFrames);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Tiles_outside_a_changed_region_stay_and_only_the_region_is_asked_again()
    {
        var (h, v, s) = await Open(new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 30, 20, 10, 5, 2 }, Workers = 4, ToolScale = 0.05 });
        await using var _ = h;
        int kept0 = v.TilesKeptByRegions;
        int before = s.FakeTiles.Requests.Count;
        var region = new Lux.Library.NormRect(0, 0, 0.25, 0.25);
        s.FakeTiles.ChangeOnly(region);
        await Frames(h, 200);
        await h.SettlePictureAsync();
        Xunit.Assert.True(v.TilesKeptByRegions > kept0);
        var asked = s.FakeTiles.Requests.Skip(before).SelectMany(r => r.Needed).Where(k => k.Level < 4).ToList();
        var p = v.Pyramid!;
        foreach (var k in asked)
        {
            var r = p.TileImageRect(k);
            Xunit.Assert.True(r.X / p.Image.Width < region.Right && r.Y / p.Image.Height < region.Bottom, $"asked {k} outside the changed region");
        }
        Xunit.Assert.Equal(s.Tiles.Version(0), v.CacheOf(0).Entries.Where(e => e.Key.Level == v.TargetLevel).Min(e => e.Version));
    }
}
