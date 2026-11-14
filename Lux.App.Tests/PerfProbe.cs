using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>Where a scroll step's time goes (diagnostic; runs only with LUX_PERF_PROBE=1).</summary>
public sealed class PerfProbe
{
    readonly ITestOutputHelper _out;
    public PerfProbe(ITestOutputHelper o) => _out = o;

    [AvaloniaFact]
    public async Task Probe()
    {
        if (Environment.GetEnvironmentVariable("LUX_PERF_PROBE") != "1") return;
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Captures = 10_000, SeedJobs = false, ThumbnailDelayMs = 150 }, 1440, 900);
        await h.SelectScopeAsync("all");
        foreach (var mode in new[] { BrowseMode.Timeline, BrowseMode.Grid })
        foreach (bool thumbs in new[] { false, true })
        {
            h.Library.Mode = mode;
            ThumbnailView.Cache = thumbs ? h.Services.Thumbnails : null;
            await h.SettleAsync(waitThumbnails: false);
            var sv = h.Browse.ActiveScroll;
            sv.Offset = new Vector(0, 0);
            await h.SettleAsync(waitThumbnails: false);
            var jobs = new List<double>(); var lay = new List<double>(); var ren = new List<double>(); var allocJ = new List<double>(); var allocR = new List<double>();
            var sw = new Stopwatch();
            GC.Collect();
            int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2), m0 = RowCells.Measures, r0 = RowCells.Rebinds;
            for (int i = 0; i < 400; i++)
            {
                sv.Offset = new Vector(0, sv.Offset.Y + sv.Viewport.Height / 4);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart(); Dispatcher.UIThread.RunJobs(); jobs.Add(sw.Elapsed.TotalMilliseconds);
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart(); h.Window.UpdateLayout(); lay.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); ren.Add(sw.Elapsed.TotalMilliseconds);
                long a2 = GC.GetAllocatedBytesForCurrentThread();
                allocJ.Add((a1 - a0) / 1024.0); allocR.Add((a2 - a1) / 1024.0);
            }
            string P(List<double> v) { var s = v.OrderBy(x => x).ToList(); return string.Create(CultureInfo.InvariantCulture, $"{s[s.Count / 2]:0.0}/{s[(int)(s.Count * 0.95)]:0.0}"); }
            _out.WriteLine($"{mode,-8} thumbs={thumbs,-5} jobs {P(jobs)}  layout {P(lay)}  render {P(ren)}  gc0 {GC.CollectionCount(0) - gc0} gc2 {GC.CollectionCount(2) - gc2}  alloc KB jobs {P(allocJ)} render {P(allocR)}  server GC {System.Runtime.GCSettings.IsServerGC}  rowcells measures/step {(RowCells.Measures - m0) / 400.0:0.0} rebinds/step {(RowCells.Rebinds - r0) / 400.0:0.0}");
        }
    }
}

public sealed class StatusProbe
{
    readonly ITestOutputHelper _out;
    public StatusProbe(ITestOutputHelper o) => _out = o;

    [AvaloniaFact]
    public async Task Status_text_remeasures()
    {
        if (Environment.GetEnvironmentVariable("LUX_PERF_PROBE") != "1") return;
        var sink = new ListSink();
        Avalonia.Logging.Logger.Sink = sink;
        await using var h = await Harness.CreateAsync();
        var tb = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(h.Window).OfType<Avalonia.Controls.TextBlock>().First(t => t.Text == h.Vm.StatusLeft);
        _out.WriteLine($"before: '{tb.Text}' desired {tb.DesiredSize} bounds {tb.Bounds} measureValid {tb.IsMeasureValid}");
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync();
        _out.WriteLine($"after:  '{tb.Text}' desired {tb.DesiredSize} bounds {tb.Bounds} measureValid {tb.IsMeasureValid} parentValid {((Avalonia.Layout.Layoutable)tb.Parent!).IsMeasureValid}");
        tb.InvalidateMeasure();
        await h.SettleAsync();
        _out.WriteLine($"forced: '{tb.Text}' desired {tb.DesiredSize} bounds {tb.Bounds} arrangeValid {tb.IsArrangeValid}");
        var sp = (Avalonia.Layout.Layoutable)tb.Parent!;
        _out.WriteLine($"parent {sp.GetType().Name} desired {sp.DesiredSize} bounds {sp.Bounds}; grandparent {((Avalonia.Layout.Layoutable)sp.Parent!).GetType().Name} bounds {((Avalonia.Layout.Layoutable)sp.Parent!).Bounds}");
        sp.InvalidateMeasure();
        await h.SettleAsync();
        _out.WriteLine($"parent forced: bounds {tb.Bounds}; parent bounds {sp.Bounds}");
        sp.InvalidateArrange();
        await h.SettleAsync();
        _out.WriteLine($"parent arrange forced: bounds {tb.Bounds}");
        foreach (var l in sink.Lines.Distinct().Take(30)) _out.WriteLine("LOG " + l);
        Avalonia.Logging.Logger.Sink = null;
    }

    sealed class ListSink : Avalonia.Logging.ILogSink
    {
        public readonly List<string> Lines = new();
        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) => level >= Avalonia.Logging.LogEventLevel.Warning;
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) => Lines.Add($"{level} {area} {source?.GetType().Name} {messageTemplate}");
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Lines.Add($"{level} {area} {source?.GetType().Name} {messageTemplate} | {string.Join(", ", propertyValues.Select(v => v?.ToString()))}");
    }
}

public sealed class LumenProbe
{
    readonly ITestOutputHelper _out;
    public LumenProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Session_values()
    {
        if (Environment.GetEnvironmentVariable("LUX_PERF_PROBE") != "1") return;
        var lib = Lux.Library.LuxLibrary.Open(new Lux.Library.LibraryOpenOptions { DatabasePath = Path.Combine(Paths.AppData("real"), "library.luxlib"), StartWorkers = false, WatchFolders = false });
        try
        {
            var page = lib.Query(new Lux.Library.CaptureQuery { HasLumenSession = true, Limit = 400 });
            var hist = new Dictionary<string, int>();
            foreach (var it in page.Items)
            {
                var s = lib.GetLumenSession(it.Id)!;
                var set = Lux.Engine.Pipeline.Dof.Edit.LumenSessionReader.Read(s.Path, decode: false).Settings;
                var v = set.Values;
                string key = v == null ? "no-values" : $"f={v.FNumber} present={v.Present[0]} focus={v.FocusDepth} tone={string.Join(",", Enumerable.Range(2, 12).Where(i => i != 3 && i != 4).Select(i => v.Values[i]))} wb={v.Values[3]}/{v.Values[4]}";
                key += $" tr={(set.Transform?.IsIdentity ?? true)} rating={set.Rating} aif={it.AllInFocusFNumber}";
                hist[key] = hist.GetValueOrDefault(key) + 1;
            }
            foreach (var (k, n) in hist.OrderByDescending(x => x.Value).Take(25)) _out.WriteLine($"{n,4}  {k}");
        }
        finally { lib.Dispose(); }
    }
}
