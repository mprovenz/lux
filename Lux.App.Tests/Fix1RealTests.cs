using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>Real-data reproductions and checks (env-gated: LUX_FIX1_REAL=1). Libraries of links and app data under
/// <see cref="Fix1.Root"/>, rebuilt every run; results in text files there.</summary>
public sealed class Fix1RealTests(ITestOutputHelper output)
{
    static bool Enabled => Environment.GetEnvironmentVariable("LUX_FIX1_REAL") == "1";

    List<string> _lines = new();
    string _file = "fix1.txt";
    void Say(string s) { output.WriteLine(s); _lines.Add(s); File.WriteAllLines(Path.Combine(Fix1.Root, _file), _lines); }

    static string Slug(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

    /// <summary>Rack focus and aperture sweep previews on L16_00049 in each depth version, with and without analysis.</summary>
    [Fact]
    public async Task Item1_depth_effect_previews_on_00049()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item1-repro.txt";
        var lri = Fix1.Lumen2025("2025-03-29", "L16_00049.lri");
        string only = Environment.GetEnvironmentVariable("LUX_FIX1_CASES") ?? "user,open,clean,lumen,fresh";

        async Task RunCase(string label, ILibrary lib, string id, bool openPhoto)
        {
            IPhotoSession? photo = openPhoto ? await lib.OpenPhotoAsync(id) : null;
            try
            {
                var item = lib.Get(id)!;
                Say($"== {label}: cache {item.CacheLevel}, variant {item.Edits.Variant ?? "(clean)"} source {item.Edits.Source}, photo open {openPhoto}" +
                    (photo != null ? $", active version {photo.DepthEdit.ActiveVersion.Id}" : ""));
                using var src = await lib.OpenAnimationAsync(id);
                Say($"   all-in-focus f/{src.AllInFocusFNumber:0.##}");
                try { var fd = await src.FocusDefaultsAsync(); Say($"   focus defaults near {fd.Near:0} mm far {fd.Far:0} mm default {fd.DefaultFocus:0} mm"); }
                catch (Exception e) { Say($"   focus defaults failed: {e.GetType().Name}: {e.Message}"); }
                foreach (var effect in new[] { AnimationEffect.RackFocus, AnimationEffect.ApertureSweep })
                {
                    var sw = Stopwatch.StartNew();
                    var p = src.Preview(new AnimationOptions { Effect = effect, Frames = 6, LongEdge = 520 });
                    while (!p.IsComplete && p.Error == null && sw.Elapsed < TimeSpan.FromMinutes(6)) await Task.Delay(100);
                    Say($"   {effect}: complete {p.IsComplete} in {sw.Elapsed.TotalSeconds:0.0} s, status \"{p.Status}\", error {p.Error ?? "-"}, notes [{string.Join(" | ", p.Notes)}]");
                    var f0 = p.Frame(0);
                    for (int k = 0; k < p.FrameCount; k++)
                    {
                        var f = p.Frame(k);
                        if (f == null) { Say($"     frame {k}: missing"); continue; }
                        if (k == 0 || k == p.FrameCount - 1) Fix1.SavePpm(f, Path.Combine(Fix1.Root, "item1", $"{Slug(label)}-{effect}-{k}.ppm"));
                        Say($"     frame {k}: {f.Width}×{f.Height} \"{f.Label}\" Δ(frame 0) {Fix1.MeanAbsDiff(f0!.Rgba, f.Rgba):0.000}" +
                            (k > 0 && p.Frame(k - 1) is { } prev ? $" Δ(prev) {Fix1.MeanAbsDiff(prev.Rgba, f.Rgba):0.000}" : ""));
                    }
                }
            }
            finally { if (photo != null) await photo.DisposeAsync(); }
        }

        // ---- 1. a real-data state: a copied context (a Lux version with quick-select strokes only, depth effect off, full detail kept)
        if (only.Contains("user") || only.Contains("open") || only.Contains("clean") || only.Contains("lumen"))
        {
            var dir = Fix1.MakeLib("r49", new[] { lri }, withSessions: true);
            var ev = Path.Combine(Fix1.Root, "evidence", "ctx49");
            string hash = "f5d5b07125a3e83a1913811ba4e5f0ea50a4e479b3a9182b2c8de5c0c8cd18ba";
            var userCache = Path.Combine(Fix1.Workspace, "assets", "lumen-library-2025", "2025-03-29", "LuxImageContext", "cache", hash + ".luxcache");
            Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "edits"));
            Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "cache"));
            File.Copy(Path.Combine(ev, hash + ".luxedits"), Path.Combine(dir, "LuxImageContext", "edits", hash + ".luxedits"));
            if (File.Exists(userCache)) File.Copy(userCache, Path.Combine(dir, "LuxImageContext", "cache", hash + ".luxcache"));   // read only: a copy
            var appData = Fix1.MakeAppData("r49");
            await using var lib = await Fix1.OpenLibraryAsync(appData, dir);
            var id = Fix1.Item(lib, "L16_00049").Id;
            if (only.Contains("user")) await RunCase("user state (Lux version), not open in Develop", lib, id, openPhoto: false);
            if (only.Contains("open")) await RunCase("user state (Lux version), open in Develop", lib, id, openPhoto: true);
            if (only.Contains("clean"))
            {
                await using (var ph = await lib.OpenPhotoAsync(id)) { var r = await ph.DepthEdit.SwitchVersion("clean").Completion; Say($"switch to clean: {r.Outcome} {r.Message}"); }
                await RunCase("clean version (analyzed)", lib, id, openPhoto: false);
                await RunCase("clean version (analyzed), open", lib, id, openPhoto: true);
            }
            if (only.Contains("lumen"))
            {
                foreach (var j in lib.ImportLumenEdits(new[] { id })) { var info = await lib.Jobs.WaitAsync(j); Say($"Lumen import job: {info.State} {info.Error} {string.Join(" · ", info.Notes)}"); }
                await RunCase("from Lumen (imported session)", lib, id, openPhoto: false);
                await RunCase("from Lumen (imported session), open", lib, id, openPhoto: true);
            }
        }
        // ---- 2. a fresh library: nothing analyzed (the analysis is queued by the preview)
        if (only.Contains("fresh"))
        {
            var dir = Fix1.MakeLib("r49fresh", new[] { lri }, withSessions: true);
            var appData = Fix1.MakeAppData("r49fresh");
            await using var lib = await Fix1.OpenLibraryAsync(appData, dir);
            var id = Fix1.Item(lib, "L16_00049").Id;
            foreach (var j in lib.ImportLumenEdits(new[] { id })) await lib.Jobs.WaitAsync(j);
            await RunCase("fresh, not analyzed, from Lumen (session render)", lib, id, openPhoto: false);
            await RunCase("fresh, not analyzed, from Lumen (session render), open", lib, id, openPhoto: true);
            await using (var ph = await lib.OpenPhotoAsync(id)) { var r = await ph.DepthEdit.SwitchVersion("clean").Completion; Say($"switch to clean: {r.Outcome} {r.Message}"); }
            await RunCase("fresh, not analyzed, clean (the preview queues the analysis)", lib, id, openPhoto: false);
            Say("jobs: " + string.Join("; ", lib.Jobs.GetJobs().Select(j => $"{j.Kind} \"{j.Title}\" {j.State}")));
        }
    }

    /// <summary>A real-data state of L16_00049 (a copied context: a Lux version, depth effect off, full detail kept) linked into a new
    /// library (<c>lib/&lt;name&gt;</c> under <see cref="Fix1.Root"/>).</summary>
    static string MakeR49(string name)
    {
        var lri = Fix1.Lumen2025("2025-03-29", "L16_00049.lri");
        var dir = Fix1.MakeLib(name, new[] { lri }, withSessions: true);
        string hash = "f5d5b07125a3e83a1913811ba4e5f0ea50a4e479b3a9182b2c8de5c0c8cd18ba";
        var userCache = Path.Combine(Fix1.Workspace, "assets", "lumen-library-2025", "2025-03-29", "LuxImageContext", "cache", hash + ".luxcache");
        Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "edits"));
        Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "cache"));
        File.Copy(Path.Combine(Fix1.Root, "evidence", "ctx49", hash + ".luxedits"), Path.Combine(dir, "LuxImageContext", "edits", hash + ".luxedits"));
        if (File.Exists(userCache)) File.Copy(userCache, Path.Combine(dir, "LuxImageContext", "cache", hash + ".luxcache"));
        return dir;
    }

    /// <summary>The pixels drawn by <paramref name="c"/> in the window's current frame (BGRA rows of its bounds).</summary>
    static byte[] DrawnPixels(Window win, Control c)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        var tl = c.TranslatePoint(new Point(0, 0), win) ?? default;
        double k = frame.PixelSize.Width / win.Bounds.Width;
        int x0 = (int)(tl.X * k), y0 = (int)(tl.Y * k), w = (int)(c.Bounds.Width * k), h = (int)(c.Bounds.Height * k);
        using var fb = frame.Lock();
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + (y0 + y) * fb.RowBytes + x0 * 4, o, y * w * 4, w * 4);
        return o;
    }

    [AvaloniaFact]
    public async Task Item1_animate_player_on_00049()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item1-ui.txt";
        TestWindows.CloseStray();
        var dir = MakeR49("r49ui");
        var appData = Fix1.MakeAppData("r49ui");
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: m => { });
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        async Task Settle(Func<bool> until, int timeoutMs)
        {
            var t = Stopwatch.StartNew(); int quiet = 0;
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs(); win.RenderFrame();
                quiet = until() ? quiet + 1 : 0;
                if (quiet >= 3) return;
                await Task.Delay(100);
            }
            Say($"settle timed out after {timeoutMs} ms");
        }
        try
        {
            await vm.InitializeAsync();
            await s.Library.AddFolderAsync(dir);
            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 120_000);
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await Settle(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000);
            var item = vm.Library.Items.First(i => i.Name == "L16_00049");
            vm.Library.Click(item, false, false);
            await Settle(() => true, 1500);

            async Task Measure(string where, AnimatePanelViewModel p, Control host)
            {
                foreach (var effect in new[] { AnimationEffect.RackFocus, AnimationEffect.ApertureSweep })
                {
                    var sw = Stopwatch.StartNew();
                    p.Effects.First(e => e.Effect == effect).SelectCommand.Execute(null);
                    await Settle(() => p.Player.Preview?.Options.Effect == effect && (p.Player.IsComplete || p.Player.HasError), 300_000);
                    Say($"{where} {effect}: complete {p.Player.IsComplete} in {sw.Elapsed.TotalSeconds:0.0} s, status {p.Player.Status}, length {p.Player.Length}");
                    await Settle(() => !p.Player.Status.Contains("sharpening", StringComparison.Ordinal), 300_000);
                    var f0 = p.Player.Preview!.Frame(0)!;
                    Say($"   sharpened after {sw.Elapsed.TotalSeconds:0.0} s: frames {f0.Width} × {f0.Height}; note: {p.PreviewNote ?? "(none)"}");
                    p.Player.IsPlaying = false;
                    var view = host.GetVisualDescendants().OfType<AnimationFrameView>().First(v => v.IsEffectivelyVisible);
                    p.Player.Position = 0; await Settle(() => true, 600);
                    var a = DrawnPixels(win, view);
                    Harness.Capture(win, $"item1-{where}-{effect}-first", Fix1.Shots);
                    p.Player.Position = p.Player.Preview!.FrameCount - 1; await Settle(() => true, 600);
                    var b = DrawnPixels(win, view);
                    Harness.Capture(win, $"item1-{where}-{effect}-last", Fix1.Shots);
                    Say($"   drawn: frame view {view.Bounds.Width:0}×{view.Bounds.Height:0}, Δ first→last {Fix1.MeanAbsDiff(a, b):0.000} (label {p.Player.FrameLabel})");
                    // playing: the picture moves
                    p.Player.Position = 0; p.Player.IsPlaying = true;
                    var seen = new HashSet<int>();
                    var t = Stopwatch.StartNew();
                    while (t.ElapsedMilliseconds < 3000) { Dispatcher.UIThread.RunJobs(); win.RenderFrame(); seen.Add(p.Player.Position); await Task.Delay(30); }
                    p.Player.IsPlaying = false;
                    Say($"   playing 3 s: {seen.Count} positions shown");
                }
            }

            vm.Library.OpenAnimate();
            await Measure("library", vm.Library.AnimatePanel!, win.FindControl<LibraryView>("LibraryPage")!);
            vm.Library.CloseAnimate();

            vm.OpenInDevelop(item);
            await Settle(() => vm.Develop.HasSession, 60_000);
            await Settle(() => true, 3000);
            vm.Develop.OpenAnimateCommand.Execute(null);
            await Measure("develop", vm.Develop.AnimatePanel!, win.GetVisualDescendants().OfType<DevelopView>().First());
            vm.Develop.AnimatePanel = null;
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.DisposeAsync();
        }
    }

    /// <summary>L16_00493 with the 13 MP copy kept, open in Develop at 1:1, upgraded to full detail through the quality
    /// selector: the viewer must then show level-0 tiles, read from the new full-detail copy, equal to the export's pixels.</summary>
    [AvaloniaFact]
    public async Task Item2_upgrade_while_open_shows_full_detail()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item2-upgrade.txt";
        TestWindows.CloseStray();
        const string hash = "6c77a71f7f3de00a5d645eabc22cfa65000cd48fbafb776bd774ff6058dbfa28";
        var dir = Fix1.MakeLib("q493", new[] { Path.Combine(Paths.Captures, "L16_00493.lri") });
        Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "cache"));
        // the 13 MP copy kept by another test library (geometry + 13 MP tiers only) — a copy
        File.Copy(Path.Combine(Fix1.Workspace, "scratch", "m6", "lib", "real", "LuxImageContext", "cache", hash + ".luxcache"),
                  Path.Combine(dir, "LuxImageContext", "cache", hash + ".luxcache"));
        var appData = Fix1.MakeAppData("q493");
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: m => { });
        ThumbnailView.Cache = s.Thumbnails;
        s.Library.Settings.ShowPhotosAsHeld = false;   // the tiles compare with the export's pixels unturned
        s.Library.Settings.DontAskAboutExtraStorage = true;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        async Task<bool> Settle(Func<bool> until, int timeoutMs)
        {
            var t = Stopwatch.StartNew(); int quiet = 0;
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs(); win.RenderFrame();
                quiet = until() ? quiet + 1 : 0;
                if (quiet >= 3) return true;
                await Task.Delay(50);
            }
            Say($"settle timed out after {timeoutMs} ms");
            return false;
        }
        try
        {
            await vm.InitializeAsync();
            await s.Library.AddFolderAsync(dir);
            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 120_000);
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await Settle(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000);
            var item = vm.Library.Items.First(i => i.Name == "L16_00493");
            Say($"kept before: {item.Item.CacheLevel} ({item.Item.BestQuality})");
            vm.OpenInDevelop(item);
            await Settle(() => vm.Develop.HasSession, 60_000);
            var viewer = win.GetVisualDescendants().OfType<Lux.App.Viewer.PhotoViewer>().First(v => v.Name == "Viewer");
            await Settle(() => viewer.LevelOnScreen >= 0 && viewer.LevelOnScreen == viewer.TargetLevel, 60_000);
            vm.Develop.ZoomActualCommand.Execute(null);
            await Settle(() => viewer.TargetComplete, 120_000);
            Say($"at 1:1 before the upgrade: target level {viewer.TargetLevel}, on screen {viewer.LevelOnScreen}, status: {vm.Develop.StatusQuality}");
            Harness.Capture(win, "item2-a-13mp-at-1to1", Fix1.Shots);

            var ps = ((Lux.App.Develop.ContractDevelopSession)vm.Develop.Session!).Native;
            // as on a photo opened with its analysis kept: the depth is attached (the engine's level-0 state exists) before the upgrade
            await Settle(() => ps.Depth.Status.Availability == DepthAvailability.Ready, 120_000);
            Say($"depth attached: {ps.Depth.Status.Availability}");
            var level0 = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), PictureTile>();
            ps.View.TileReady += (_, t) => { if (t.Id.Level == 0 && t.Mode == ViewMode.Edited) level0[(t.Id.Col, t.Id.Row)] = t; };

            // ---- the upgrade, as a user makes it: the selector (the storage question is off)
            var sw = Stopwatch.StartNew();
            vm.Develop.Quality.ChooseCommand.Execute(ViewQuality.FullDetail);
            await Settle(() => vm.Develop.Quality.Notice != null || s.Library.Jobs.GetJobs().Any(j => j.Kind == JobKind.CacheUpgrade), 10_000);
            if (vm.Develop.Quality.Notice != null) vm.Develop.Quality.ConfirmAskCommand.Execute(null);
            await Settle(() => s.Library.Jobs.GetJobs().Any(j => j.Kind == JobKind.CacheUpgrade && j.IsFinished), 600_000);
            var job = s.Library.Jobs.GetJobs().First(j => j.Kind == JobKind.CacheUpgrade);
            Say($"upgrade job {job.State} after {sw.Elapsed.TotalSeconds:0.0} s: {string.Join(" · ", job.Notes)}");
            var done = Stopwatch.StartNew();
            bool sharp = await Settle(() => viewer.TargetComplete && viewer.TargetLevel == 0, 240_000);
            Say($"level 0 wholly on screen: {sharp} after {done.Elapsed.TotalSeconds:0.0} s (target {viewer.TargetLevel}, slots {viewer.TargetSlotsReady}/{viewer.TargetSlots}); status: {vm.Develop.StatusQuality}");
            await Settle(() => true, 1500);
            Harness.Capture(win, "item2-b-full-detail-at-1to1", Fix1.Shots);
            Say($"level-0 tiles delivered: {level0.Count}");
            Assert.True(sharp, "the viewer never showed level 0 after the upgrade");
            // ---- panning at 1:1: new level-0 tiles (from the kept copy, or made on the fly)
            async Task<double> PanTime(string what, double dx)
            {
                var pt = Stopwatch.StartNew();
                viewer.PanBy(new Avalonia.Vector(dx, 0));
                await Settle(() => viewer.TargetComplete && viewer.TargetLevel == 0, 240_000);
                Say($"   pan {what}: level 0 on screen after {pt.Elapsed.TotalSeconds:0.00} s");
                return pt.Elapsed.TotalSeconds;
            }
            double panOpen = await PanTime("after the upgrade, same open photo", -700);

            // ---- the tiles are the export's pixels (a separate engine session of the saved state, level-0 JPG render before encoding)
            using var eng = CaptureSession.Open(Path.Combine(dir, "L16_00493.lri"), new SessionOptions { Context = new Lux.Store.StoreCaptureContext(Path.Combine(dir, "L16_00493.lri"), mayCreateFolder: false), DisplayCacheBytes = 0 });
            var (W, H, px) = await eng.RenderExportRgba8Async(new ExportRequest { Formats = new[] { Lux.Engine.Pipeline.Export.ExportImageFormat.Jpeg }, Level = 0 });
            int same = 0, diff = 0;
            foreach (var t in level0.Values)
            {
                bool ok = true;
                for (int y = 0; y < t.Rect.Height && ok; y++)
                    for (int x = 0; x < t.Rect.Width; x++)
                    {
                        int i = (y * t.Rect.Width + x) * 4, j = ((t.Rect.Y + y) * W + t.Rect.X + x) * 4;
                        bool bgra = t.Order == PixelOrder.Bgra;
                        if (t.Pixels[i + (bgra ? 2 : 0)] != px[j] || t.Pixels[i + 1] != px[j + 1] || t.Pixels[i + (bgra ? 0 : 2)] != px[j + 2]) { ok = false; break; }
                    }
                if (ok) same++; else diff++;
            }
            Say($"level-0 tiles equal to the export: {same} of {same + diff} (export {W} × {H})");
            Assert.True(same > 0 && diff == 0);
            // ---- for comparison: the photo opened again (its session reads the full-detail copy from the start), the same pan
            vm.CurrentPage = Page.Library;
            await Settle(() => true, 1000);
            vm.OpenInDevelop(item);
            await Settle(() => vm.Develop.HasSession, 60_000);
            viewer = win.GetVisualDescendants().OfType<Lux.App.Viewer.PhotoViewer>().First(v => v.Name == "Viewer");
            var ro = Stopwatch.StartNew();
            vm.Develop.ZoomActualCommand.Execute(null);
            await Settle(() => viewer.TargetComplete && viewer.TargetLevel == 0, 240_000);
            Say($"   reopened, 1:1: level 0 wholly on screen after {ro.Elapsed.TotalSeconds:0.00} s");
            double panReopened = await PanTime("photo opened again", -700);
            Say($"pan at 1:1: {panOpen:0.00} s in the session open during the upgrade vs {panReopened:0.00} s reopened");
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.DisposeAsync();
        }
    }

    /// <summary>The app on a real library (a new linked folder and app data under <see cref="Fix1.Root"/>), headless, with a settle helper.</summary>
    sealed class RealApp : IAsyncDisposable
    {
        public required AppServices S { get; init; }
        public required MainViewModel Vm { get; init; }
        public required MainWindow Win { get; init; }
        public Action<string> Say { get; init; } = _ => { };

        public static async Task<RealApp> OpenAsync(string name, string dir, Action<string> say, Action<ILibrarySettings>? settings = null)
        {
            TestWindows.CloseStray();
            var appData = Fix1.MakeAppData(name);
            var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: m => { });
            ThumbnailView.Cache = s.Thumbnails;
            settings?.Invoke(s.Library.Settings);
            var vm = new MainViewModel(s);
            var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            win.Show();
            var app = new RealApp { S = s, Vm = vm, Win = win, Say = say };
            await vm.InitializeAsync();
            await s.Library.AddFolderAsync(dir);
            await app.Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 120_000);
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await app.Settle(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000);
            return app;
        }

        public async Task<bool> Settle(Func<bool> until, int timeoutMs)
        {
            var t = Stopwatch.StartNew(); int quiet = 0;
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs(); Win.RenderFrame();
                quiet = until() ? quiet + 1 : 0;
                if (quiet >= 3) return true;
                await Task.Delay(50);
            }
            Say($"settle timed out after {timeoutMs} ms");
            return false;
        }

        public CaptureItemViewModel Item(string name) => Vm.Library.Items.First(i => i.Name == name);
        public Lux.App.Viewer.PhotoViewer Viewer => Win.GetVisualDescendants().OfType<Lux.App.Viewer.PhotoViewer>().First(v => v.Name == "Viewer");
        public string Shot(string name) => Harness.Capture(Win, name, Fix1.Shots);

        public async ValueTask DisposeAsync()
        {
            Win.Close();
            Dispatcher.UIThread.RunJobs();
            await Vm.DisposeAsync();
        }
    }

    /// <summary>A photo shown from its imported Lumen session (rendered on the fly from Lumen's geometry, never from the kept copies):
    /// choosing 13 MP makes it sharper at 1:1, so it does not stay at the preview levels.</summary>
    [AvaloniaFact]
    public async Task Item2_lumen_photo_follows_the_chosen_quality()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item2-lumen.txt";
        var dir = Fix1.MakeLib("q49lumen", new[] { Fix1.Lumen2025("2025-03-29", "L16_00049.lri") }, withSessions: true);
        await using var app = await RealApp.OpenAsync("q49lumen", dir, Say, st => { st.ShowPhotosAsHeld = false; st.DontAskAboutExtraStorage = true; });
        var lib = app.S.Library;
        var item = app.Item("L16_00049");
        foreach (var j in lib.ImportLumenEdits(new[] { item.Id })) await lib.Jobs.WaitAsync(j);
        await app.Settle(() => app.Item("L16_00049").Item.Edits.Source == EditSource.Lumen, 30_000);
        item = app.Item("L16_00049");
        app.Vm.OpenInDevelop(item);
        await app.Settle(() => app.Vm.Develop.HasSession, 60_000);
        var viewer = app.Viewer;
        app.Vm.Develop.ZoomActualCommand.Execute(null);
        await app.Settle(() => viewer.TargetComplete, 180_000);
        Say($"from Lumen, nothing kept, 1:1: target level {viewer.TargetLevel}, slots {viewer.TargetSlotsReady}/{viewer.TargetSlots}; {app.Vm.Develop.StatusQuality}");
        app.Shot("item2-c-lumen-preview-at-1to1");
        var ps = ((Lux.App.Develop.ContractDevelopSession)app.Vm.Develop.Session!).Native;
        var level1 = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), PictureTile>();
        ps.View.TileReady += (_, t) => { if (t.Id.Level == 1 && t.Mode == ViewMode.Edited) level1[(t.Id.Col, t.Id.Row)] = t; };
        var sw = Stopwatch.StartNew();
        app.Vm.Develop.Quality.ChooseCommand.Execute(ViewQuality.Linear13);
        await app.Settle(() => app.Vm.Develop.Quality.Notice != null || lib.Jobs.GetJobs().Any(j => j.Kind is JobKind.CacheBuild or JobKind.Analysis && j.CaptureId == item.Id), 10_000);
        if (app.Vm.Develop.Quality.Notice != null) app.Vm.Develop.Quality.ConfirmAskCommand.Execute(null);
        await app.Settle(() => lib.Jobs.GetJobs().Any(j => j.Kind is JobKind.CacheBuild or JobKind.Analysis && j.CaptureId == item.Id && j.IsFinished), 600_000);
        Say($"13 MP job finished after {sw.Elapsed.TotalSeconds:0.0} s");
        var done = Stopwatch.StartNew();
        bool sharper = await app.Settle(() => viewer.TargetLevel == 1 && viewer.TargetComplete, 300_000);
        Say($"level 1 wholly on screen: {sharper} after {done.Elapsed.TotalSeconds:0.0} s (target {viewer.TargetLevel}, slots {viewer.TargetSlotsReady}/{viewer.TargetSlots}); {app.Vm.Develop.StatusQuality}");
        app.Shot("item2-d-lumen-13mp-at-1to1");
        Assert.True(sharper);
        using var eng = CaptureSession.Open(Path.Combine(dir, "L16_00049.lri"), new SessionOptions { Context = new Lux.Store.StoreCaptureContext(Path.Combine(dir, "L16_00049.lri"), mayCreateFolder: false), DisplayCacheBytes = 0 });
        var (W, H, px) = await eng.RenderExportRgba8Async(new ExportRequest { Formats = new[] { Lux.Engine.Pipeline.Export.ExportImageFormat.Jpeg }, Level = 1 });
        int same = 0, diff = 0;
        foreach (var t in level1.Values)
        {
            bool ok = true; bool bgra = t.Order == PixelOrder.Bgra;
            for (int y = 0; y < t.Rect.Height && ok; y++)
                for (int x = 0; x < t.Rect.Width; x++)
                {
                    int i = (y * t.Rect.Width + x) * 4, j = ((t.Rect.Y + y) * W + t.Rect.X + x) * 4;
                    if (t.Pixels[i + (bgra ? 2 : 0)] != px[j] || t.Pixels[i + 1] != px[j + 1] || t.Pixels[i + (bgra ? 0 : 2)] != px[j + 2]) { ok = false; break; }
                }
            if (ok) same++; else diff++;
        }
        Say($"level-1 tiles equal to the export of the imported session: {same} of {same + diff} (export {W} × {H})");
        Assert.True(same > 0 && diff == 0);
    }

    static string LuxLight => Paths.LuxLight;

    static string? _cliAppData;
    static int Cli(string args, out string log)
    {
        var psi = new ProcessStartInfo(LuxLight, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (_cliAppData != null) psi.Environment["LUX_APP_DATA"] = _cliAppData;   // the test library's app data (its context store)
        psi.Environment["LUX_FORBID_DEFAULT_APP_DATA"] = "1";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        log = o + err.Result;
        return p.ExitCode;
    }

    /// <summary>Exports follow the automatic upright view. Checks on real data (library export jobs vs the "Keep the turn for exports"
    /// path and vs `lux-light convert`): held 90° L16_00493 — default = keep-the-turn + export (DNG, JPG, one animation), "as captured" =
    /// `convert`, `convert --upright` = the app's default; upright L16_00421 — default = `convert`.</summary>
    [Fact]
    public async Task Item3_exports_follow_the_upright_view()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item3-proof.txt";
        const string hash493 = "6c77a71f7f3de00a5d645eabc22cfa65000cd48fbafb776bd774ff6058dbfa28";
        var dir = Fix1.MakeLib("u3", new[] { Path.Combine(Paths.Captures, "L16_00493.lri"), Path.Combine(Paths.Captures, "L16_00421.lri") });
        Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "cache"));
        File.Copy(Path.Combine(Fix1.Workspace, "scratch", "m6", "lib", "real", "LuxImageContext", "cache", hash493 + ".luxcache"),
                  Path.Combine(dir, "LuxImageContext", "cache", hash493 + ".luxcache"));
        var outRoot = Path.Combine(Fix1.Root, "item3"); Fix1.Wipe(outRoot); Directory.CreateDirectory(outRoot);
        string Out(string name) { var d = Path.Combine(outRoot, name); Directory.CreateDirectory(d); return d; }
        var appData = Fix1.MakeAppData("u3");
        _cliAppData = appData;   // lux-light reads the same context store (beside the links here)
        await using var lib = await Fix1.OpenLibraryAsync(appData, dir);
        var i493 = Fix1.Item(lib, "L16_00493"); var i421 = Fix1.Item(lib, "L16_00421");
        Say($"L16_00493: held {i493.HeldOrientation}, display turn {i493.DisplayRotation}°, decided {i493.GeometryDecided}; L16_00421: held {i421.HeldOrientation}");
        Assert.Equal(90, i493.DisplayRotation);
        async Task<IReadOnlyList<string>> Export(string id, ExportOptions o)
        {
            var ids = lib.Export(new[] { id }, o);
            var info = await lib.Jobs.WaitAsync(ids[0]);
            Say($"   export {info.Title}: {info.State} {info.Error} · {string.Join(" · ", info.Notes)}");
            Assert.Equal(JobState.Done, info.State);
            return info.ResultFiles;
        }
        var baseOpts = new ExportOptions { Formats = new[] { "dng", "jpg" }, Level = 0 };
        var rows = new List<string>();
        void Cmp(string what, string a, string b)
        {
            string r = M4ProofTests.Compare(a, b);
            rows.Add($"{what}\t{Path.GetFileName(a)}\t{r}");
            Say($"   {what}: {Path.GetFileName(a)} → {r}");
            Assert.StartsWith("identical", r);
        }
        var sw = Stopwatch.StartNew();
        // (1) the new default: as shown (turned upright)
        var shown = await Export(i493.Id, baseOpts with { OutputFolder = Out("493-default") });
        // (2) as captured: today's default (Lumen's)
        var captured = await Export(i493.Id, baseOpts with { OutputFolder = Out("493-as-captured"), Unturned = true });
        // (3) `lux-light convert` (default: unturned) and `--upright`
        Assert.Equal(0, Cli($"convert \"{Path.Combine(dir, "L16_00493.lri")}\" --formats dng,jpg -o \"{Out("493-cli")}\"", out var log1));
        Assert.Equal(0, Cli($"convert \"{Path.Combine(dir, "L16_00493.lri")}\" --formats dng,jpg --upright -o \"{Out("493-cli-upright")}\"", out var log2));
        Say("   " + string.Join(" | ", log2.Split('\n').Where(l => l.Contains("upright", StringComparison.Ordinal)).Select(l => l.Trim())));
        // (4) one animation, default (as shown)
        var anim = new AnimationOptions { Effect = AnimationEffect.RackFocus, Frames = 6, LongEdge = 520, Format = AnimationFormat.Gif };
        var aj = lib.Animate(new[] { i493.Id }, anim with { OutputFolder = Out("493-anim-default") });
        var ainfo = await lib.Jobs.WaitAsync(aj[0]); Assert.Equal(JobState.Done, ainfo.State);
        Say($"   animation default: {ainfo.ResultPath} · {string.Join(" · ", ainfo.Notes)}");
        // (5) the "Keep the turn for exports" path (the held turn saved as the photo's rotation), then the default export / animation
        Assert.Equal(1, await lib.KeepHeldTurnAsync(new[] { i493.Id }));
        Assert.True(lib.Get(i493.Id)!.GeometryDecided);
        var kept = await Export(i493.Id, baseOpts with { OutputFolder = Out("493-keep-turn") });
        var aj2 = lib.Animate(new[] { i493.Id }, anim with { OutputFolder = Out("493-anim-keep-turn") });
        var ainfo2 = await lib.Jobs.WaitAsync(aj2[0]); Assert.Equal(JobState.Done, ainfo2.State);
        // put the library back: geometry reset (undecided again)
        await using (var ph = await lib.OpenPhotoAsync(i493.Id)) { ph.Edit.ResetGeometry(); await ph.Edit.SaveAsync(); }
        // (6) upright L16_00421: default export = convert
        var up = await Export(i421.Id, baseOpts with { OutputFolder = Out("421-default") });
        Assert.Equal(0, Cli($"convert \"{Path.Combine(dir, "L16_00421.lri")}\" --formats dng,jpg -o \"{Out("421-cli")}\"", out var log3));
        Say($"exports and runs took {sw.Elapsed.TotalSeconds:0} s");

        string F(IReadOnlyList<string> files, string ext) => files.First(f => f.EndsWith(ext, StringComparison.Ordinal));
        foreach (var ext in new[] { ".dng", ".jpg" })
        {
            Cmp("default (as shown) = keep the turn + export", F(shown, ext), F(kept, ext));
            Cmp("as captured = convert (Lumen's)", F(captured, ext), Path.Combine(outRoot, "493-cli", "L16_00493" + ext));
            Cmp("convert --upright = the app's default", Path.Combine(outRoot, "493-cli-upright", "L16_00493" + ext), F(shown, ext));
            Cmp("upright 00421: default = convert", F(up, ext), Path.Combine(outRoot, "421-cli", "L16_00421" + ext));
        }
        Cmp("animation: default = keep the turn + animation", ainfo.ResultPath!, ainfo2.ResultPath!);
        // the turned files really are turned (portrait)
        using (var st = File.OpenRead(F(shown, ".jpg"))) { var (w, h) = JpegSize(st); Say($"   default JPG {w} × {h}"); Assert.True(h > w); }
        using (var st = File.OpenRead(F(captured, ".jpg"))) { var (w, h) = JpegSize(st); Say($"   as captured JPG {w} × {h}"); Assert.True(w > h); }
        File.WriteAllLines(Path.Combine(outRoot, "item3-proof.tsv"), rows);
    }

    /// <summary>A JPEG's frame size (SOF0 / SOF2).</summary>
    static (int W, int H) JpegSize(Stream s)
    {
        var b = new byte[s.Length]; s.ReadExactly(b);
        for (int i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte m = b[i + 1]; int len = (b[i + 2] << 8) | b[i + 3];
            if (m is 0xC0 or 0xC1 or 0xC2) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + len;
        }
        return (0, 0);
    }

    /// <summary>The history around "Revert to original" on L16_00049 (saved exposure −0.8), real library.</summary>
    [Fact]
    public async Task Item9_history_and_revert_on_real_photo()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item9-history.txt";
        var dir = MakeR49("h49");
        var appData = Fix1.MakeAppData("h49");
        await using var lib = await Fix1.OpenLibraryAsync(appData, dir);
        var id = Fix1.Item(lib, "L16_00049").Id;
        await using var ph = await lib.OpenPhotoAsync(id);
        async Task Show(string when)
        {
            await Task.Delay(400);
            var info = ph.History.Info;
            Say($"{when}: position {info.Position} of {info.Entries.Count}: [{string.Join(" | ", info.Entries.Select((e, i) => (i < info.Position ? "" : "~") + e.Label + $"({e.Kind}{(e.FromEarlierSession ? ",earlier" : "")})"))}]");
            Say($"   edit history {ph.Edit.HistoryPosition}/{ph.Edit.History.Count}: [{string.Join(" | ", ph.Edit.History.Select(h => h.Label))}]; exposure {ph.Edit.GetValue(AdjustmentKey.Exposure)}; opened exposure {ph.Edit.Opened.Adjustments.Exposure}");
        }
        await Show("opened");
        ph.Edit.SetValue(AdjustmentKey.Contrast, 20);
        await Show("contrast +20");
        ph.Edit.RevertToOriginal();
        await Show("revert to original");
        ph.Edit.RevertToOriginal();
        await Show("revert again (already the original: still its own step)");
        Assert.Equal("Revert to original", ph.History.Info.Entries[^1].Label);
        Assert.Equal(ph.History.Info.Entries.Count, ph.History.Info.Position);
        var r = await ph.History.ClearHistory().Completion;
        await Show($"clear history ({r.Outcome})");
        Assert.Empty(ph.History.Info.Entries);
        Assert.True(ph.History.Cleared);
        Assert.Equal(0f, ph.Edit.GetValue(AdjustmentKey.Exposure));
        Assert.False(ph.History.CanUndo);
    }

    /// <summary>Background work shows in the Queue named after its file — the depth map asked for by the open photo is
    /// a Queue job; reading the depth, a Lumen import through the photo, a version switch and an animation preview are activities; the
    /// thumbnail job names the photo it makes.</summary>
    [Fact]
    public async Task Item6_background_work_shows_in_the_queue()
    {
        if (!Enabled) { output.WriteLine("LUX_FIX1_REAL not set: skipped"); return; }
        _file = "item6-queue.txt";
        var dir = Fix1.MakeLib("w49", new[] { Fix1.Lumen2025("2025-03-29", "L16_00049.lri") }, withSessions: true);
        var appData = Fix1.MakeAppData("w49");
        await using var lib = await Fix1.OpenLibraryAsync(appData, dir);
        var id = Fix1.Item(lib, "L16_00049").Id;
        var titles = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        void Record() { foreach (var a in lib.Activities) titles[a.Title] = a.Status; }
        lib.ActivitiesChanged += (_, _) => Record();
        // thumbnails: the job names the photo it makes
        var thumbJob = lib.PrefetchThumbnails(new[] { id });
        await lib.GetThumbnailAsync(id);
        await Task.Delay(500);
        var tj = lib.Jobs.GetJobs(includeFinished: true).Where(j => j.Kind == JobKind.Thumbnail).OrderByDescending(j => j.Id).FirstOrDefault();
        Say($"thumbnail job: \"{tj?.Title}\" status \"{tj?.Status}\"");
        // the depth asked for by the open photo ("Analyze now", a depth tool, the depth effect): a Queue job named after the photo
        await using var ph = await lib.OpenPhotoAsync(id);
        var sw = Stopwatch.StartNew();
        var ensure = ph.Depth.EnsureDepthAsync();
        JobInfo? aj = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && (aj = lib.Jobs.GetJobs().FirstOrDefault(j => j.Kind == JobKind.Analysis && j.CaptureId == id)) is null) await Task.Delay(100);
        Say($"open photo's depth: Queue job after {sw.Elapsed.TotalSeconds:0.0} s: \"{aj?.Title}\" ({aj?.Detail}), state {aj?.State}");
        Assert.NotNull(aj);
        Assert.Contains("L16_00049", aj!.Title);
        await ensure;
        Say($"depth ready after {sw.Elapsed.TotalSeconds:0.0} s; job {lib.Jobs.Get(aj.Id)?.State}");
        Record();
        // a Lumen import through the open photo, then a version switch
        var r1 = await ph.DepthEdit.ImportLumenSession(false).Completion;
        var r2 = await ph.DepthEdit.SwitchVersion("clean").Completion;
        Say($"import {r1.Outcome}, switch {r2.Outcome}");
        // an animation preview
        using (var src = await lib.OpenAnimationAsync(id))
        {
            var p = src.Preview(new AnimationOptions { Effect = AnimationEffect.RackFocus, Frames = 6, LongEdge = 520 });
            var t = Stopwatch.StartNew();
            while (!p.IsComplete && t.Elapsed < TimeSpan.FromMinutes(3)) { Record(); await Task.Delay(50); }
        }
        await Task.Delay(300);
        Say("activities seen:");
        foreach (var (title, status) in titles.OrderBy(k => k.Key)) Say($"   {title} — {status}");
        Assert.Contains(titles.Keys, t => t.StartsWith("Reading the depth of L16_00049", StringComparison.Ordinal));
        Assert.Contains(titles.Keys, t => t.StartsWith("Importing the Lumen edits of L16_00049", StringComparison.Ordinal));
        Assert.Contains(titles.Keys, t => t.StartsWith("Switching L16_00049", StringComparison.Ordinal));
        Assert.Contains(titles.Keys, t => t.StartsWith("Animation preview of L16_00049", StringComparison.Ordinal));
        Assert.Contains("L16_00049", tj?.Status ?? "");
        Assert.Empty(lib.Activities.Where(a => !a.Title.StartsWith("Rebuilding", StringComparison.Ordinal)));   // all ended
    }
}
