using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Fake;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// Frame times while panning and zooming the fake 52-megapixel picture at 60 Hz in the Develop page (headless Skia, CPU raster — the
/// desktop draws on the GPU), and the time from a tile's arrival to the frame that draws it. Results: a text file in the second UI output folder.
/// </summary>
public sealed class ViewerTimingTests
{
    static string? _pageReport;
    static string Stats(string what, List<double> ms)
    {
        if (ms.Count == 0) return $"{what}: none";
        var s = ms.OrderBy(x => x).ToList();
        double P(double q) => s[Math.Min(s.Count - 1, (int)Math.Floor(q * (s.Count - 1)))];
        int over = s.Count(x => x > 1000 / 60.0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{what}: n {s.Count} · p50 {P(0.5):0.0} · p95 {P(0.95):0.0} · max {s[^1]:0.0} ms · over 16.7 ms: {over}");
    }

    [AvaloniaFact]
    public async Task Pan_and_zoom_a_52_MP_picture_at_60_Hz()
    {
        await using var h = await DevelopHarness.OpenAsync(new FakeDevelopOptions { Timings = FakeTimings.Default, OpenDelayMs = 0, SaveDelayMs = 20 });
        var v = h.Viewer();
        Assert.Equal(8320, v.Pyramid!.Image.Width);
        var report = new StringBuilder();
        report.AppendLine($"# Viewer timings — {DateTime.Now:yyyy-MM-dd HH:mm} — fake 8320 × 6240 (52 MP), 512-px tiles, Develop page 1440 × 900, headless Skia (CPU)");
        report.AppendLine("# fake render delays per tile (level 0…4): " + string.Join(", ", FakeTimings.Default.TileDelayMs) + $" ms on {FakeTimings.Default.Workers} workers");

        async Task<(List<double> Frame, List<double> Render)> Run(int frames, Action<int> step)
        {
            var frame = new List<double>();
            v.Stats.Reset();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                step(i);
                Dispatcher.UIThread.RunJobs();
                long t0 = Stopwatch.GetTimestamp();
                h.Window.RenderFrame();
                frame.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                // the next 60 Hz tick (arrivals land in between, as on screen)
                double next = (i + 1) * 1000 / 60.0, wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay((int)wait);
            }
            return (frame, v.Stats.RenderMs.ToList());
        }

        // 1:1 at the center, settled
        v.ZoomToActual(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), animate: false);
        await h.SettlePictureAsync();
        Assert.Equal(0, v.LevelOnScreen);

        // pan at 1:1: 2 s at 60 Hz, 18 view units a frame (about a screen width a second), back and forth
        var (panFrame, panRender) = await Run(120, i => v.PanBy(new Vector(i < 60 ? -18 : 18, i % 2 == 0 ? -4 : 4)));
        int panGaps = v.Stats.FramesWithGaps;
        var panLatency = v.Stats.TileLatencyMs.ToList();
        report.AppendLine();
        report.AppendLine("pan at 1:1 (level 0), 120 frames:");
        report.AppendLine("  " + Stats("whole frame", panFrame));
        report.AppendLine("  " + Stats("viewer render", panRender));
        report.AppendLine("  " + Stats("tile arrival → drawn", panLatency));
        report.AppendLine($"  frames with a slot not covered: {panGaps} · tiles drawn per frame max {v.Stats.MaxTilesDrawn} · requests {v.Stats.Requests}");

        // smooth zoom: fit → 200 % → fit in 90 frames about a point off center
        v.ZoomToFit();
        await h.SettlePictureAsync();
        var anchor = new Point(v.Bounds.Width * 0.6, v.Bounds.Height * 0.4);
        double fitPct = v.ZoomPercent;
        var (zoomFrame, zoomRender) = await Run(90, i =>
        {
            double t = i < 45 ? i / 44.0 : (89 - i) / 44.0;
            double pct = Math.Exp(Math.Log(fitPct) + t * (Math.Log(200) - Math.Log(fitPct)));
            v.ZoomToPercent(pct, anchor, animate: false);
        });
        int zoomGaps = v.Stats.FramesWithGaps;
        report.AppendLine();
        report.AppendLine($"zoom {fitPct:0} % → 200 % → fit about a point, 90 frames:");
        report.AppendLine("  " + Stats("whole frame", zoomFrame));
        report.AppendLine("  " + Stats("viewer render", zoomRender));
        report.AppendLine("  " + Stats("tile arrival → drawn", v.Stats.TileLatencyMs.ToList()));
        report.AppendLine($"  frames with a slot not covered: {zoomGaps} · tiles drawn per frame max {v.Stats.MaxTilesDrawn} · requests {v.Stats.Requests}");

        // tile memory after the run
        var cache = v.CacheOf(0);
        report.AppendLine();
        report.AppendLine($"tile memory: {cache.Bytes >> 20} MB held, peak {cache.PeakBytes >> 20} MB, budget {v.TileBudget >> 20} MB, {cache.Evicted} evicted, {cache.DroppedStale} stale dropped");
        Directory.CreateDirectory(Paths.M2Scratch);
        File.WriteAllText(Path.Combine(Paths.M2Scratch, "viewer-timings.txt"), report.ToString());
        _pageReport = report.ToString();

        // the coarse picture covers every frame after it has arrived (no flicker, no hole)
        Assert.Equal(0, panGaps);
        Assert.Equal(0, zoomGaps);
        Assert.True(cache.PeakBytes <= v.TileBudget + (long)v.Stats.MaxTilesDrawn * 512 * 512 * 4 * 2);
    }

    /// <summary>The picture alone (a window the size of the page's stage): what drawing the tiles costs per frame.</summary>
    [AvaloniaFact]
    public async Task Viewer_alone_pan_and_zoom_frame_times()
    {
        var specs = Lux.App.Develop.AdjustmentSpec.Standard();
        using var src = new FakeTileSource(0, false, false, 0, new PixelSize(8320, 6240), 0, specs, specs.ToDictionary(x => x.Key, x => x.Default), FakeTimings.Default);
        var v = new Lux.App.Viewer.PhotoViewer { Background = Avalonia.Media.Brushes.Gray, AnimateZoom = false };
        var win = new Avalonia.Controls.Window { Width = 910, Height = 690, Content = v };
        win.Show();
        v.Source = src;
        async Task Settle()
        {
            for (int i = 0; i < 200; i++)
            {
                Dispatcher.UIThread.RunJobs(); win.RenderFrame();
                if (v.LevelOnScreen == v.TargetLevel) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); win.RenderFrame(); if (v.LevelOnScreen == v.TargetLevel) return; }
                await Task.Delay(20);
            }
        }
        async Task<List<double>> Run(int frames, Action<int> step)
        {
            var frame = new List<double>();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                step(i);
                Dispatcher.UIThread.RunJobs();
                long t0 = Stopwatch.GetTimestamp();
                win.RenderFrame();
                frame.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                double wait = (i + 1) * 1000 / 60.0 - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay((int)wait);
            }
            return frame;
        }
        await Settle();
        v.ZoomToActual(new Point(455, 345), animate: false);
        await Settle();
        v.Stats.Reset();
        var pan = await Run(120, i => v.PanBy(new Vector(i < 60 ? -18 : 18, i % 2 == 0 ? -4 : 4)));
        int panGaps = v.Stats.FramesWithGaps;
        var panLat = v.Stats.TileLatencyMs.ToList();
        v.ZoomToFit();
        await Settle();
        double fit = v.ZoomPercent;
        v.Stats.Reset();
        var zoom = await Run(90, i =>
        {
            double t = i < 45 ? i / 44.0 : (89 - i) / 44.0;
            v.ZoomToPercent(Math.Exp(Math.Log(fit) + t * (Math.Log(200) - Math.Log(fit))), new Point(546, 276), animate: false);
        });
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("the picture alone (910 × 690 window = the page's stage), same fake:");
        sb.AppendLine("  pan at 1:1:  " + Stats("frame", pan) + $" · gaps {panGaps}");
        sb.AppendLine("               " + Stats("tile arrival → drawn", panLat));
        sb.AppendLine("  zoom fit ↔ 200 %: " + Stats("frame", zoom) + $" · gaps {v.Stats.FramesWithGaps}");
        sb.AppendLine("               " + Stats("tile arrival → drawn", v.Stats.TileLatencyMs.ToList()));
        Directory.CreateDirectory(Paths.M2Scratch);
        File.AppendAllText(Path.Combine(Paths.M2Scratch, "viewer-timings.txt"), sb.ToString());
        win.Close();
        Assert.Equal(0, panGaps);
    }
}
