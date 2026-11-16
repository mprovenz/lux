using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The Develop page on the real library and its edit backend (EditContracts.cs): real tiles, slider latency (drag frames, refine time), the
/// crop / rotate path, saving and the library thumbnail, export from Develop with the same settings. Runs only when LUX_REAL_DEVELOP names a
/// folder of linked captures (default: a library the test makes from LUX_REAL_DEVELOP_SOURCE links in the second UI output folder,
/// <c>Paths.M2Scratch</c>); the library database and the app's data go to the same folder — never the per-user folder; the edits are
/// written next to the links. Screens and timing figures go to the same folder.
/// </summary>
public sealed class RealDevelopTests
{
    readonly ITestOutputHelper _out;
    public RealDevelopTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public async Task Real_develop_page()
    {
        // LUX_REAL_DEVELOP = a folder of links to captures (read only). Every run starts clean: the linked library
        // and the app data in the output folder are made again from it (the source's analysis cache files are
        // copied so opening is quick; nothing is written to the source, to the captures or to the real library).
        var source = Environment.GetEnvironmentVariable("LUX_REAL_DEVELOP");
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) { _out.WriteLine("LUX_REAL_DEVELOP not set: skipped"); return; }
        source = Path.GetFullPath(source);
        string m2 = Path.GetFullPath(Paths.M2Scratch);
        var folder = Path.Combine(m2, "lib", "real");
        var appData = Path.Combine(m2, "appdata", "real");
        Assert.False(source.StartsWith(m2, StringComparison.Ordinal), "the source must not be the folder made from it");
        foreach (var dir in new[] { folder, appData, Path.Combine(m2, "out") })
            if (Directory.Exists(dir)) { Assert.StartsWith(m2, Path.GetFullPath(dir)); Directory.Delete(dir, recursive: true); }   // links are removed, never followed
        Directory.CreateDirectory(Path.Combine(folder, "LuxImageContext", "cache"));
        Directory.CreateDirectory(appData);
        var names = new[] { "L16_00493.lri", "L16_00428.lri" };
        foreach (var n in names)
        {
            var link = new FileInfo(Path.Combine(source, n));
            if (!link.Exists) continue;
            var target = link.LinkTarget is { } t ? Path.GetFullPath(t, source) : link.FullName;
            File.CreateSymbolicLink(Path.Combine(folder, n), target);
        }
        var cache = Path.Combine(source, "LuxImageContext", "cache");
        if (Directory.Exists(cache))
            foreach (var f in Directory.GetFiles(cache, "*.luxcache")) File.Copy(f, Path.Combine(folder, "LuxImageContext", "cache", Path.GetFileName(f)));
        var report = new StringBuilder();
        void Log(string m) { _out.WriteLine(m); report.AppendLine(m); }
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: _ => { });
        Assert.False(s.IsFake);
        Assert.IsType<ContractDevelopService>(s.Develop);
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        try
        {
            await vm.InitializeAsync();
            if (!s.Library.GetFolders().Any(f => f.Path.TrimEnd('/') == folder.TrimEnd('/'))) await s.Library.AddFolderAsync(folder);

            async Task<bool> Settle(Func<bool> until, int timeoutMs)
            {
                var t = Stopwatch.StartNew();
                int quiet = 0;
                while (t.ElapsedMilliseconds < timeoutMs)
                {
                    Dispatcher.UIThread.RunJobs();
                    win.RenderFrame();
                    quiet = until() ? quiet + 1 : 0;
                    if (quiet >= 2) return true;
                    await Task.Delay(40);
                }
                _out.WriteLine($"settle timed out after {timeoutMs} ms");
                return false;
            }

            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 300_000);
            await vm.Library.RefreshTreeAsync();
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await Settle(() => !vm.Library.IsLoading, 30_000);
            var item = vm.Library.Items.FirstOrDefault(i => i.Name == "L16_00493") ?? vm.Library.Items.First();
            Log($"# Real Develop page — {DateTime.Now:yyyy-MM-dd HH:mm} — {item.Name}, kept {item.Item.BestQuality}, cache {item.Item.CacheLevel}");

            var page = win.FindControl<DevelopView>("DevelopPage")!;
            var v = page.PhotoViewer;
            var d = vm.Develop;
            v.AnimateZoom = false;
            bool Ready() => d.Session != null && v.LevelOnScreen >= 0 && v.LevelOnScreen <= v.TargetLevel && !v.IsHolding;

            // open: the first picture (level 4) and the target level at fit
            var sw = Stopwatch.StartNew();
            vm.Library.Click(item, false, false);
            vm.OpenInDevelop(item);
            await Settle(() => d.Session != null, 120_000);
            double opened = sw.Elapsed.TotalMilliseconds;
            await Settle(() => v.LevelOnScreen >= 0, 120_000);
            double first = sw.Elapsed.TotalMilliseconds;
            await Settle(Ready, 120_000);
            double fitted = sw.Elapsed.TotalMilliseconds;
            Log(string.Create(CultureInfo.InvariantCulture, $"open: session {opened:0} ms · first picture {first:0} ms · fitted level {v.LevelOnScreen} complete {fitted:0} ms"));
            Assert.True(v.CacheOf(0).Count > 0);
            // first open of a clean photo held 90°: shown upright (portrait, the people standing), nothing in the history
            bool Portrait() => v.Pyramid!.Image.Height > v.Pyramid!.Image.Width;
            Log($"first open: {v.Pyramid!.Image.Width} × {v.Pyramid!.Image.Height} (held {item.Item.HeldOrientation}, display {item.Orientation}°), history {string.Join(" | ", d.Session!.History)}");
            Assert.True(Portrait());
            Assert.Equal(new[] { "Opened" }, d.Session!.History);
            win.RenderFrame();
            Harness.Capture(win, "real-01-develop-single", Paths.M2Shots);

            // 1:1 at the center
            sw.Restart();
            v.ZoomToActual();
            await Settle(Ready, 180_000);
            Log(string.Create(CultureInfo.InvariantCulture, $"zoom to 1:1: level {v.LevelOnScreen} on screen after {sw.Elapsed.TotalMilliseconds:0} ms ({v.Stats.TileLatencyMs.Count} tiles drawn so far)"));
            Harness.Capture(win, "real-02-develop-zoomed", Paths.M2Shots);
            v.ZoomToFit();
            await Settle(Ready, 60_000);

            // a slider drag: the frames while dragging, the refine after the release
            var row = d.Adjustments.First(r => r.Key == AdjustmentKeys.Exposure);
            var session = (ContractDevelopSession)d.Session!;
            var drag = new List<double>();
            var frames = new List<double>();
            int previews = 0;
            void Count(object? _, PictureTile t) { if (t.IsDragPreview) Interlocked.Increment(ref previews); }
            session.Native.View.TileReady += Count;
            int stepsBefore = session.History.Count;
            double start = row.Position, dir = start > 0.6 ? -1 : 1;
            row.BeginDrag();
            for (int i = 1; i <= 30; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                row.DragTo(start + dir * 0.004 * i);
                drag.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                Dispatcher.UIThread.RunJobs();
                long f0 = Stopwatch.GetTimestamp();
                win.RenderFrame();
                frames.Add(Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
                await Task.Delay(33);
            }
            await Settle(() => previews > 0, 5_000);
            sw.Restart();
            row.EndDrag();
            long version = session.Tiles.Version(0);
            await Settle(() => Ready() && v.CacheOf(0).Entries.Where(e => e.Key.Level == v.TargetLevel).All(e => e.Version >= version), 60_000);
            double refine = sw.Elapsed.TotalMilliseconds;
            session.Native.View.TileReady -= Count;
            Log(string.Create(CultureInfo.InvariantCulture,
                $"exposure drag, 30 moves at 30 Hz: UI work per move p50 {Median(drag):0.00} ms, max {drag.Max():0.00} ms; frame p50 {Median(frames):0.0} ms; drag-preview tiles {previews}; refined (target level {v.TargetLevel}, current version) {refine:0} ms after the release"));
            Assert.Equal(stepsBefore + 1, session.History.Count);
            Assert.True(previews > 0);
            Harness.Capture(win, "real-03-develop-after-drag", Paths.M2Shots);

            // before / after
            d.SetModeCommand.Execute(DevelopMode.BeforeAfter);
            await Settle(() => Ready() && v.CacheOf(1).Count > 0, 60_000);
            Harness.Capture(win, "real-04-develop-before-after", Paths.M2Shots);
            d.SetModeCommand.Execute(DevelopMode.Single);

            // a photo shown as held, contract v2.1's rule: rotate right ×4 cycles visibly, one step each
            Log($"before the geometry edits: geometry decided {d.Session!.GeometryDecided}, shown as held {d.Session.ShownAsHeld}");
            Assert.False(d.Session.GeometryDecided);
            int steps0 = d.Session.History.Count;
            var cycleFrames = new List<Avalonia.Media.Imaging.Bitmap>();
            var shapes = new List<string> { $"{v.Pyramid!.Image.Width} × {v.Pyramid!.Image.Height}" };
            bool portrait = Portrait(), cycled = true;
            for (int i = 0; i < 4; i++)
            {
                sw.Restart();
                d.RotateRightCommand.Execute(null);
                await Settle(Ready, 60_000);
                shapes.Add($"{v.Pyramid!.Image.Width} × {v.Pyramid!.Image.Height} ({sw.Elapsed.TotalMilliseconds:0} ms)");
                cycled &= Portrait() != portrait;
                portrait = Portrait();
                win.RenderFrame();
                cycleFrames.Add(win.CaptureRenderedFrame()!);
            }
            Log("rotate right ×4: " + string.Join(" → ", shapes) + $"; turned every time {cycled}; steps added {d.Session.History.Count - steps0}: {string.Join(" | ", d.Session.History.Skip(steps0))}");
            HeldOrientationTests.SaveGridTo(cycleFrames, "real-08-develop-held-rotate-cycle");
            Assert.True(cycled);
            Assert.Equal(Enumerable.Repeat("Rotate right", 4), d.Session.History.Skip(steps0));
            Assert.True(d.Session.GeometryDecided);
            // revert: the held view again
            d.RevertCommand.Execute(null);
            await Settle(Ready, 60_000);
            Log($"revert: {v.Pyramid!.Image.Width} × {v.Pyramid!.Image.Height}, original {d.Session.IsOriginal}, decided {d.Session.GeometryDecided}, shown as held {d.Session.ShownAsHeld}");
            Assert.True(Portrait());
            Assert.False(d.Session.GeometryDecided);
            Assert.True(d.Session.ShownAsHeld);
            Harness.Capture(win, "real-09-develop-held-after-revert", Paths.M2Shots);
            // crop presets in the picture as shown
            sw.Restart();
            d.CropCommand.Execute(null);
            await Settle(() => d.IsCropping && Ready(), 60_000);
            var shownSize = v.Pyramid!.Image;
            var presetShapes = new List<string>();
            bool named = true;
            foreach (var (label, ratio) in new[] { ("16 : 9", 16 / 9.0), ("1 : 1", 1.0), ("3 : 2", 1.5) })
            {
                d.CropAspectChoice = d.AspectChoices.First(c => c.Label == label);
                await Settle(Ready, 30_000);
                var f = d.Session.CropFrame;
                double shape = f.Width * shownSize.Width / (f.Height * shownSize.Height);
                presetShapes.Add($"{label} → {shape:0.000}");
                named &= Math.Abs(shape - ratio) < 0.01;
            }
            Log($"crop presets on the picture as shown ({shownSize.Width} × {shownSize.Height}): {string.Join(", ", presetShapes)}; named shape every time {named}");
            Assert.True(named);
            Harness.Capture(win, "real-05-develop-crop", Paths.M2Shots);
            int steps1 = d.Session.History.Count;
            d.ApplyCrop();
            await Settle(Ready, 60_000);
            Log(string.Create(CultureInfo.InvariantCulture, $"crop 3:2 entered, applied and re-rendered in {sw.Elapsed.TotalMilliseconds:0} ms: {d.HeaderText}; steps {string.Join(" | ", d.Session.History.Skip(steps1))}"));
            Assert.Equal(new[] { "Crop" }, d.Session.History.Skip(steps1));
            sw.Restart();
            d.RotateRightCommand.Execute(null);
            await Settle(Ready, 60_000);
            Log(string.Create(CultureInfo.InvariantCulture, $"rotate right re-rendered in {sw.Elapsed.TotalMilliseconds:0} ms: {d.HeaderText}"));
            Harness.Capture(win, "real-06-develop-rotated", Paths.M2Shots);

            // saved, mirrored in the library, thumbnail made again
            await session.SaveNowAsync();
            await Settle(() => s.Library.Get(item.Id)?.Edits is { HasSettings: true }, 30_000);
            var mirrored = s.Library.Get(item.Id)!;
            Log($"saved: {d.SaveText}; library summary \"{mirrored.Edits.AdjustmentsSummary}\", crop or rotation {mirrored.Edits.HasCropOrRotation}, thumbnail current {mirrored.ThumbnailCurrent}");
            Assert.True(mirrored.Edits.HasCropOrRotation);

            // export from Develop: the panel takes the session's own settings (13 MP JPG into this stream's scratch)
            d.OpenExportCommand.Execute(null);
            var panel = d.ExportPanel!;
            foreach (var f in panel.Formats) f.IsOn = f.Key == "jpg";
            panel.Size = panel.Sizes.First(x => x.Value == 1);
            panel.Folder = Path.Combine(Paths.M2Scratch, "out");
            panel.AddToQueueCommand.Execute(null);
            await Settle(() => panel.LastOptions != null, 30_000);
            Assert.Equal(session.Native.Edit.Current.Adjustments, panel.LastOptions!.Adjustments);
            Log($"export from Develop queued with the session's settings (exposure {panel.LastOptions.Adjustments!.Exposure:0.00})");
            var job = s.Library.Jobs.GetJobs().LastOrDefault(j => j.Kind == JobKind.Export && j.CaptureId == item.Id);
            if (job != null)
            {
                await Settle(() => s.Library.Jobs.Get(job.Id) is { IsFinished: true }, 300_000);
                var done = s.Library.Jobs.Get(job.Id)!;
                Log($"export job {done.State}: {done.ResultPath} {done.Error}");
            }

            Log("history of " + item.Name + ": " + string.Join(" | ", d.Session!.History));

            // the other held photo (270°): opens upright; rotate right turns what is shown
            if (vm.Library.Items.FirstOrDefault(i => i.Name == "L16_00428") is { } heldItem)
            {
                vm.Library.Click(heldItem, false, false);
                await d.OpenAsync(heldItem);
                await Settle(Ready, 180_000);
                var s0 = v.Pyramid!.Image;
                Assert.True(Portrait());
                d.RotateRightCommand.Execute(null);
                await Settle(Ready, 180_000);
                var s1 = v.Pyramid!.Image;
                Log($"{heldItem.Name} (held {heldItem.Item.HeldOrientation}, display {heldItem.Orientation}°): {s0.Width} × {s0.Height} → rotate right → {s1.Width} × {s1.Height}; history {string.Join(" | ", d.Session!.History)}");
                Assert.NotEqual(s0.Width > s0.Height, s1.Width > s1.Height);
                Assert.Equal(new[] { "Opened", "Rotate right" }, d.Session.History);
            }

            // the library page shows the edited thumbnail
            vm.CurrentPage = Page.Library;
            await Settle(() => s.Thumbnails.InFlight == 0, 120_000);
            Harness.Capture(win, "real-07-library-after-edit", Paths.M2Shots);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Paths.M2Scratch, "real-timings.txt"), report.ToString());
            win.Close();
            await vm.DisposeAsync();
        }
    }

    static double Median(List<double> x) { var s = x.OrderBy(v => v).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }
}
