using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// 10 000 captures in each view: scroll from top to bottom in steps of a quarter screen (a fast wheel / drag), then jump around; the
/// realised containers and thumbnail views must stay bounded, thumbnail requests that scroll away must be cancelled, and every step's
/// layout and render time is recorded (a text file in the first UI output folder).
/// </summary>
public sealed class VirtualisationTests
{
    readonly ITestOutputHelper _out;
    public VirtualisationTests(ITestOutputHelper output) => _out = output;

    sealed record Result(string View, int CellsBuilt, int RowsBuilt, int Steps, int Rows, double LayoutMedian, double LayoutP95, double LayoutMax, double FrameMedian, double FrameP95,
                         double FrameMax, int MaxRealised, int MaxThumbViews, int Requested, int Cancelled, int Delivered, double ExtentPx);

    static double Pct(List<double> v, double p) { var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(p * s.Count))]; }

    static async Task<Result> ScrollThrough(Harness h, BrowseMode mode)
    {
        h.Library.Mode = mode;
        await h.SettleAsync(timeoutMs: 15000);
        var sv = h.Browse.ActiveScroll;
        var items = h.Browse.ActiveItems;
        var cache = h.Services.Thumbnails;
        int req0 = cache.Requested, can0 = cache.CancelledAtLibrary, del0 = cache.Delivered, built0 = RowCells.Built, rows0 = RowsControl.ContainersBuilt;
        var layout = new List<double>(); var frame = new List<double>();
        int maxRealised = 0, maxThumbs = 0, steps = 0;
        var sw = new Stopwatch();
        void Step(double y)
        {
            sw.Restart();
            sv.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
            h.Window.UpdateLayout();
            double l = sw.Elapsed.TotalMilliseconds;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            double f = sw.Elapsed.TotalMilliseconds;
            layout.Add(l); frame.Add(f);
            maxRealised = Math.Max(maxRealised, items.GetRealizedContainers().Count());
            maxThumbs = Math.Max(maxThumbs, ThumbnailView.Live);
            steps++;
        }
        // warm-up (first use of templates, styles and code paths), not measured
        for (int i = 0; i < 120; i++) { sv.Offset = new Vector(0, i * sv.Viewport.Height / 4); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        sv.Offset = new Vector(0, 0);
        await h.SettleAsync(timeoutMs: 15000);
        layout.Clear(); frame.Clear(); maxRealised = 0; maxThumbs = 0;
        req0 = cache.Requested; can0 = cache.CancelledAtLibrary; del0 = cache.Delivered; built0 = RowCells.Built; rows0 = RowsControl.ContainersBuilt;
        double y = 0;
        while (y + sv.Viewport.Height < sv.Extent.Height - 1 && steps < 20000)
        {
            y += sv.Viewport.Height / 4;
            Step(y);
        }
        // jumps: end → start → middle → random
        var rnd = new Random(7);
        Step(sv.Extent.Height); Step(0); Step(sv.Extent.Height / 2);
        for (int i = 0; i < 30; i++) Step(rnd.NextDouble() * sv.Extent.Height);
        await h.SettleAsync(timeoutMs: 15000);
        return new Result(mode.ToString(), RowCells.Built - built0, RowsControl.ContainersBuilt - rows0, steps, items.ItemCount, Pct(layout, 0.5), Pct(layout, 0.95), layout.Max(), Pct(frame, 0.5), Pct(frame, 0.95),
                          frame.Max(), maxRealised, maxThumbs, cache.Requested - req0, cache.CancelledAtLibrary - can0, cache.Delivered - del0, sv.Extent.Height);
    }

    [AvaloniaFact]
    public async Task Ten_thousand_captures_scroll_bounded_in_all_views()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Captures = 10_000, SeedJobs = false, ThumbnailDelayMs = 150 }, 1440, 900);
        await h.SelectScopeAsync("all");
        Assert.Equal(10_000, h.Library.TotalCount);
        var results = new List<Result>();
        foreach (var mode in new[] { BrowseMode.Grid, BrowseMode.Timeline, BrowseMode.List })
            results.Add(await ScrollThrough(h, mode));

        // one capture in ten being processed (full detail queued): their cells are blurred under a spinner while on screen
        var ids = h.Library.Items.Where((i, k) => k % 10 == 3 && i.Item.BestQuality != Lux.Library.ViewQuality.FullDetail && !i.Missing && !i.Unreadable)
                   .Select(i => i.Id).ToList();
        foreach (var id in ids) h.Lib.Jobs.Enqueue(new Lux.Library.CacheJobRequest(id, Lux.Engine.Session.CacheLevel.Full));
        await h.SettleAsync(() => h.Library.Items.Count(i => i.IsProcessing) >= ids.Count, timeoutMs: 30000, waitThumbnails: false);
        int processing = h.Library.Items.Count(i => i.IsProcessing);
        int blurred0 = ImageStateView.BlurredMade;
        foreach (var mode in new[] { BrowseMode.Grid, BrowseMode.Timeline, BrowseMode.List })
            results.Add((await ScrollThrough(h, mode)) with { View = mode + " +proc" });
        int blurred = ImageStateView.BlurredMade - blurred0;

        var sb = new StringBuilder();
        sb.AppendLine($"Virtualisation, 10 000 fake captures, window 1440×900, headless Skia (CPU raster) on {Environment.ProcessorCount} threads — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("After an unmeasured warm-up of 120 steps, each step scrolls a quarter of the viewport, top to bottom, then 33 jumps (end, start, middle, random).");
        sb.AppendLine("layout = offset change + layout pass; frame = layout + one render tick of the window (Skia CPU raster). Times in ms.");
        sb.AppendLine();
        sb.AppendLine("Thumbnails: the fake library takes 150 ms to make each one (a capture whose thumbnail is not stored yet).");
        sb.AppendLine($"'+proc' rows: {processing} captures (one in ten) queued for full detail — their cells show the blurred thumbnail under a spinner;");
        sb.AppendLine($"blurred copies made during those three runs: {blurred} (one per thumbnail delivered while processing, never per frame).");
        sb.AppendLine("rows / cells built = row containers and grid / timeline cell controls created during the run (recycled rows keep theirs).");
        sb.AppendLine();
        sb.AppendLine("view            steps  rows   layout p50/p95/max      frame p50/p95/max       max realised rows  max thumb views  rows/cells built  thumbs requested/cancelled/delivered");
        foreach (var r in results)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.View,-15} {r.Steps,5}  {r.Rows,5}  {r.LayoutMedian,6:0.0}/{r.LayoutP95,6:0.0}/{r.LayoutMax,6:0.0}   {r.FrameMedian,6:0.0}/{r.FrameP95,6:0.0}/{r.FrameMax,6:0.0}   {r.MaxRealised,17}  {r.MaxThumbViews,15}  {$"{r.RowsBuilt}/{r.CellsBuilt}",16}  {r.Requested}/{r.Cancelled}/{r.Delivered}"));
        var text = sb.ToString();
        _out.WriteLine(text);
        Directory.CreateDirectory(Paths.UiScratch);
        File.WriteAllText(Path.Combine(Paths.UiScratch, "virtualisation.txt"), text);

        foreach (var r in results)
        {
            Assert.True(r.MaxRealised < 60, $"{r.View}: {r.MaxRealised} rows realised");
            Assert.True(r.MaxThumbViews < 400, $"{r.View}: {r.MaxThumbViews} thumbnail views alive");
            Assert.True(r.Cancelled > 0, $"{r.View}: no request was cancelled while scrolling");
        }
        Assert.True(processing >= ids.Count);
        Assert.True(blurred <= results.Where(r => r.View.EndsWith("+proc", StringComparison.Ordinal)).Sum(r => r.Delivered) + 64,
                    $"{blurred} blurred copies for the thumbnails delivered");
    }
}
