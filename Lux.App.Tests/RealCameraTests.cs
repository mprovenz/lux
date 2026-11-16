using System.Diagnostics;
using Lux.Engine.Mtp;
using Lux.Library;
using Lux.Library.Camera;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The camera service on the real Light L16 over USB (LUX_M5_REAL=1; skipped otherwise). Read-only towards the camera: listing, file
/// information, previews and copies of three ordinary captures into <c>M5.Root</c>. Results are appended to a text file there.
/// The library databases and app data live under <c>M5.Root</c> too (never the per-user folder).
/// </summary>
public sealed class RealCameraTests
{
    readonly ITestOutputHelper _out;
    public RealCameraTests(ITestOutputHelper output) => _out = output;

    void Say(string s) { _out.WriteLine(s); M5.Record("real-camera.txt", s); }

    static async Task<bool> Until(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    [Fact]
    public async Task Real_listing_time_count_and_states()
    {
        if (!M5.Real) { _out.WriteLine("LUX_M5_REAL not set: skipped"); return; }
        var root = M5.Fresh("real/listing");
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "library.luxlib"), StartWorkers = false, WatchFolders = false });
        var states = new List<(double T, string S)>();
        var sw = Stopwatch.StartNew();
        using var cam = new CameraService(lib, MtpFactory.Backend, new CameraServiceOptions { PreviewFolder = Path.Combine(root, "previews"), Log = s => _out.WriteLine(s) });
        cam.StatusChanged += (_, _) =>
        {
            var s = cam.Status;
            lock (states)
            {
                string line = $"{s.State} {s.Phase} {s.Listed}/{s.ListTotal}";
                if (states.Count == 0 || states[^1].S != line) states.Add((sw.Elapsed.TotalSeconds, line));
            }
        };
        Assert.True(await Until(() => cam.Status.IsOpen, 15_000), "the camera did not open: " + cam.Status.State + " " + cam.Status.Message);
        double opened = sw.Elapsed.TotalSeconds;
        double? firstItems = null;
        Assert.True(await Until(() => { if (firstItems is null && cam.Captures.Count > 0) firstItems = sw.Elapsed.TotalSeconds; return cam.Status.State == CameraState.Connected; }, 120_000),
                    "listing did not finish: " + cam.Status.State);
        var st = cam.Status;
        var caps = cam.Captures;
        Say($"listing: opened {opened:0.00} s, first captures {firstItems:0.0} s, complete {sw.Elapsed.TotalSeconds:0.0} s; {caps.Count} captures, {st.New} new; " +
            $"{st.DeviceName} serial {st.Serial} battery {st.BatteryPercent} %");
        lock (states) foreach (var (t, s) in states.Where((_, i) => i < 6 || i % 10 == 0 || i == states.Count - 1)) _out.WriteLine($"  {t,6:0.0} s  {s}");
        Assert.Equal(caps.Count, st.Captures);
        Assert.True(caps.Count >= 400, "expected the camera's ~430 captures");
        Assert.DoesNotContain(caps, c => c.Folder.Contains("lightcal", StringComparison.OrdinalIgnoreCase));
        Assert.All(caps, c => Assert.EndsWith(".lri", c.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.All(caps, c => Assert.True(c.Size > 0 && c.Modified is not null, c.FileName));
        int withPreview = caps.Count(c => c.PreviewFile is not null);
        var bySize = caps.GroupBy(c => c.Size).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} B × {g.Count()}");
        Say($"  previews beside {withPreview} of {caps.Count}; days {caps.Select(c => c.Day).Distinct().Count()}; commonest sizes: {string.Join(", ", bySize)}; " +
            $"largest {caps.Max(c => c.Size) / 1e6:0} MB");
        // the date filter: captures since a date are those whose day is on or after it
        var days = caps.Where(c => c.Day is not null).Select(c => c.Day!.Value).Distinct().OrderByDescending(d => d).ToList();
        var since = days[Math.Min(3, days.Count - 1)];
        int n = caps.Count(c => c.Day >= since);
        Say($"  since {since:yyyy-MM-dd}: {n} captures ({string.Join(", ", days.Take(4).Select(d => $"{d:MM-dd} {caps.Count(c => c.Day == d)}"))})");
    }

    /// <summary>Open the camera service on a fresh library and wait for the complete listing.</summary>
    async Task<CameraService> OpenListedAsync(ILibrary lib, string root)
    {
        var cam = new CameraService(lib, MtpFactory.Backend, new CameraServiceOptions
        {
            PreviewFolder = Path.Combine(root, "previews"), ShrinkPreview = Lux.App.Services.CameraPreviews.Shrink, Log = s => _out.WriteLine(s),
        });
        Assert.True(await Until(() => cam.Status.State == CameraState.Connected, 150_000), "listing did not finish: " + cam.Status.State + " " + cam.Status.Message);
        return cam;
    }

    [Fact]
    public async Task Real_thumbnails_from_the_camera_previews()
    {
        if (!M5.Real) { _out.WriteLine("LUX_M5_REAL not set: skipped"); return; }
        var root = M5.Fresh("real/thumbnails");
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "library.luxlib"), StartWorkers = false, WatchFolders = false });
        using var cam = await OpenListedAsync(lib, root);
        var caps = cam.Captures.Where(c => c.PreviewFile is not null).OrderByDescending(c => c.Modified).Take(16).ToList();
        var times = new List<double>(); var sizes = new List<int>();
        foreach (var c in caps)
        {
            var sw = Stopwatch.StartNew();
            var jpg = await cam.GetPreviewAsync(c);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotNull(jpg);
            sizes.Add(jpg!.Length);
            using var bmp = SkiaSharp.SKBitmap.Decode(jpg);
            Assert.True(bmp.Width <= Lux.App.Services.CameraPreviews.Width && bmp.Width >= 200, $"{c.Name}: {bmp.Width} px");
        }
        var again = Stopwatch.StartNew();
        foreach (var c in caps) Assert.NotNull(await cam.GetPreviewAsync(c));
        double cached = again.Elapsed.TotalMilliseconds / caps.Count;
        int files = Directory.GetFiles(Path.Combine(root, "previews"), "*.jpg", SearchOption.AllDirectories).Length;
        Say($"thumbnails: {caps.Count} previews from the camera, {times.Average():0} ms each (min {times.Min():0}, max {times.Max():0}); thumbnail files " +
            $"{sizes.Average() / 1024:0} KB on average; from the app-data folder {cached:0.0} ms each; {files} files kept");
        Assert.Equal(caps.Count, files);
        var none = cam.Captures.FirstOrDefault(c => c.PreviewFile is null);
        if (none is not null) { Assert.Null(await cam.GetPreviewAsync(none)); Say($"  {none.Name} has no preview on the camera: none shown"); }
    }

    [Fact]
    public async Task Real_busy_camera_then_free()
    {
        if (!M5.Real) { _out.WriteLine("LUX_M5_REAL not set: skipped"); return; }
        var root = M5.Fresh("real/busy");
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "library.luxlib"), StartWorkers = false, WatchFolders = false });
        var backend = MtpFactory.Backend;
        var cand = backend.Autodetect().First();
        CameraService? cam = null;
        // a second program holding the camera: here, a second connection of this process
        using (var holder = backend.Open(cand))
        {
            cam = new CameraService(lib, backend, new CameraServiceOptions { PollInterval = TimeSpan.FromSeconds(1), Log = s => _out.WriteLine(s) });
            Assert.True(await Until(() => cam.Status.State == CameraState.Busy, 10_000), "expected busy, got " + cam.Status.State + " " + cam.Status.Message);
            Say($"busy: {cam.Status.Message}");
        }
        try
        {
            Assert.True(await Until(() => cam.Status.IsOpen, 10_000), "did not connect after the camera was freed: " + cam.Status.State);
            Say($"  freed: {cam.Status.State} within a poll");
        }
        finally { cam.Dispose(); }
    }

    [Fact]
    public async Task Real_import_three_captures_into_day_folders_then_library_and_analysis()
    {
        if (!M5.Real) { _out.WriteLine("LUX_M5_REAL not set: skipped"); return; }
        var root = M5.Fresh("real/import");
        var dest = Path.Combine(root, "Light");
        // the scan stays light (one file at a time) and heavy jobs (the analysis) wait while "interacting": the test checks that the analysis is
        // queued, then cancels it; no thumbnails are made
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions
        {
            DatabasePath = Path.Combine(root, "library.luxlib"), StartWorkers = true, WatchFolders = false, MakeThumbnailsAfterScan = false, RenderThreads = 6,
        });
        lib.Settings.PauseHeavyJobsWhileInteracting = true;
        lib.Jobs.SetInteractive(true);
        using var cam = await OpenListedAsync(lib, root);
        // three ordinary captures (about 163 MB; never the stacked ones of 650 MB – 1.3 GB), from two days when the camera has them
        var ordinary = cam.Captures.Where(c => c.Size is > 100_000_000 and < 200_000_000 && c.Day is not null).OrderByDescending(c => c.Modified).ToList();
        var days = ordinary.Select(c => c.Day!.Value).Distinct().Take(2).ToList();
        var pick = days.Count >= 2 ? ordinary.Where(c => c.Day == days[0]).Take(2).Concat(ordinary.Where(c => c.Day == days[1]).Take(1)).ToList() : ordinary.Take(3).ToList();
        Assert.Equal(3, pick.Count);
        var options = new CameraImportOptions(dest, OneFolderPerDay: true, CameraAfterImport.Analyse13Mp);
        var plan = cam.PlanImport(pick, options);
        Say($"import plan: {plan.Count} captures, {plan.Bytes / 1e6:0} MB, {plan.Folders.Count} folders, free {plan.FreeBytes / 1e9:0} GB, problem {plan.Problem ?? "none"}");
        Assert.True(plan.CanImport, plan.Problem);
        var sw = Stopwatch.StartNew();
        long id = cam.StartImport(pick, options);
        var job = await lib.Jobs.WaitAsync(id).WaitAsync(TimeSpan.FromMinutes(5));
        double secs = sw.Elapsed.TotalSeconds;
        Say($"  job \"{job.Title}\" [{job.Subject}] {job.State} in {secs:0.0} s ({plan.Bytes / 1e6 / secs:0} MB/s); notes: {string.Join("; ", job.Notes)}; error {job.Error ?? "none"}");
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(3, job.ResultFiles.Count);
        foreach (var c in pick)
        {
            string path = Path.Combine(dest, c.Day!.Value.ToString("yyyy-MM-dd"), c.FileName);
            Assert.True(File.Exists(path), path);
            Assert.Equal(c.Size, new FileInfo(path).Length);
        }
        Assert.Empty(Directory.EnumerateFiles(dest, "*.part", SearchOption.AllDirectories));

        // (byte-identity with the camera is checked with lux-light pull and a second read)
        M5.Record("real-import-files.txt", string.Join(" ", job.ResultFiles));

        // the destination joins the library; the scan identifies the captures; the analysis is queued for each (held, then canceled)
        var ids = new List<string>();
        Assert.True(await Until(() =>
        {
            lib.Jobs.SetInteractive(true);
            ids = lib.FindByPaths(job.ResultFiles).Values.ToList();
            return ids.Count == 3 && ids.All(i => lib.Jobs.GetJobs().Any(j => j.Kind == JobKind.Analysis && j.CaptureId == i));
        }, 180_000), $"captures found {ids.Count}; analysis jobs {lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Analysis)}");
        var folders = lib.GetFolders();
        Assert.Contains(folders, f => f.Path == dest);
        var items = ids.Select(i => lib.Get(i)!).ToList();
        foreach (var it in items)
        {
            var c = pick.First(p => p.FileName == Path.GetFileName(it.Path));
            // the camera's day of the file and the capture's own time (camera clock, from its header)
            Say($"  in the library: {it.Name} id {it.Id[..12]}… capture time {it.CaptureTime:yyyy-MM-dd HH:mm:ss}, camera file time {c.Modified?.ToLocalTime():yyyy-MM-dd HH:mm:ss}, " +
                $"{it.FocalLengthMm} mm, {it.ModuleCount} modules, unreadable {it.Unreadable}");
            Assert.False(it.Unreadable, it.Error);
        }
        var analysis = lib.Jobs.GetJobs().Where(j => j.Kind == JobKind.Analysis && !j.IsFinished).ToList();
        Say($"  analysis queued: {analysis.Count} ({string.Join(", ", analysis.Select(a => $"{a.Title} [{a.State}, {a.Status}]"))})");
        Assert.All(analysis, a => Assert.NotEqual(JobState.Running, a.State));
        // the camera marks them as in the library now
        Assert.True(await Until(() => pick.All(p => cam.Captures.First(c => c.Key == p.Key).InLibrary), 10_000));
        var last = cam.LastImport!;
        Say($"  last import: {last.State}, copied {last.Copied}, skipped {last.Skipped}, analysis {cam.LastImportAnalysis()}");
        Assert.Equal(CameraImportState.Complete, last.State);
        lib.Jobs.CancelAll(JobKind.Analysis);

        // again: already there with the same size, nothing copied
        var again = await lib.Jobs.WaitAsync(cam.StartImport(pick, options)).WaitAsync(TimeSpan.FromMinutes(2));
        Say($"  the same three again: {again.State}, {string.Join("; ", again.Notes)}");
        Assert.Contains("3 photos already there", again.Notes);
        lib.Jobs.CancelAll(JobKind.Analysis);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Real_camera_page_screens()
    {
        if (!M5.Real) { _out.WriteLine("LUX_M5_REAL not set: skipped"); return; }
        var appData = M5.Fresh("real/app");
        var s = Lux.App.Services.CompositionRoot.Create(new Lux.App.Services.AppOptions { AppDataDir = appData }, new TestPlatform(), log: m => _out.WriteLine(m));
        Assert.False(s.IsFake);
        Assert.StartsWith(appData, s.Library.DatabasePath);
        Lux.App.Controls.ThumbnailView.Cache = s.Thumbnails;
        var vm = new Lux.App.ViewModels.MainViewModel(s);
        var win = new Lux.App.Views.MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        try
        {
            await vm.InitializeAsync();
            vm.CurrentPage = Lux.App.ViewModels.Page.Camera;
            // the three captures the import test copied, in this library: the camera marks them as imported
            var imported = Path.Combine(M5.Root, "real", "import", "Light");
            if (Directory.Exists(imported)) await s.Library.AddFolderAsync(imported);
            var sw = Stopwatch.StartNew();
            async Task Settle(Func<bool> until, int timeoutMs)
            {
                var t = Stopwatch.StartNew();
                int quiet = 0;
                while (t.ElapsedMilliseconds < timeoutMs)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    win.RenderFrame();
                    quiet = until() ? quiet + 1 : 0;
                    if (quiet >= 3) return;
                    await Task.Delay(100);
                }
                _out.WriteLine($"settle timed out after {timeoutMs} ms");
            }
            await Settle(() => vm.Camera.Status.Phase == CameraListingPhase.FileList || vm.Camera.State == CameraState.Connected, 20_000);
            Harness.Capture(win, "m5-real-01-reading-file-list", M5.Shots);
            await Settle(() => vm.Camera.State == CameraState.Connected && !s.Library.GetFolders().Any(f => f.Scanning), 150_000);
            double listed = sw.Elapsed.TotalSeconds;
            await Settle(() => vm.Camera.Thumbnails.InFlight == 0, 60_000);
            Say($"real camera page: listed in {listed:0.0} s; {vm.Camera.StateLine}; status bar \"{vm.StatusDevice}\"; {vm.Camera.Thumbnails.Delivered} thumbnails on screen after {sw.Elapsed.TotalSeconds:0.0} s");
            Harness.Capture(win, "m5-real-02-camera-new", M5.Shots);
            vm.Camera.SetFilterCommand.Execute(Lux.App.ViewModels.CameraFilter.All);
            await Settle(() => vm.Camera.Thumbnails.InFlight == 0, 60_000);
            int marked = vm.Camera.Visible.Count(i => i.InLibrary);
            Say($"  All: {vm.Camera.Visible.Count} captures, {marked} marked as in the library");
            if (Directory.Exists(imported)) Assert.True(marked >= 3);
            Harness.Capture(win, "m5-real-03-camera-all", M5.Shots);
            vm.Camera.SetFilterCommand.Execute(Lux.App.ViewModels.CameraFilter.Since);
            await Settle(() => vm.Camera.Thumbnails.InFlight == 0, 60_000);
            Say($"  Since {vm.Camera.SinceDay?.Label}: {vm.Camera.Visible.Count} captures");
            Harness.Capture(win, "m5-real-04-camera-since", M5.Shots);
        }
        finally
        {
            win.Close();
            await vm.DisposeAsync();
        }
    }
}
