using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Library;
using Lux.Library.Camera;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>The folder where these tests write. LUX_GAPS_SCRATCH sets it. Otherwise it is a folder in the workspace, or in the test output
/// folder when the tests run outside the workspace. The screenshots go into its <c>shots</c> folder.</summary>
public static class Gaps
{
    static string Here([CallerFilePath] string file = "") => file;

    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LUX_GAPS_SCRATCH");
            string dir;
            if (!string.IsNullOrEmpty(env)) dir = env;
            else
            {
                var workspace = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
                dir = Directory.Exists(Path.Combine(workspace, "scratch")) ? Path.Combine(workspace, "scratch", "gaps") : Path.Combine(AppContext.BaseDirectory, "gaps");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Shots { get { var d = Path.Combine(Root, "shots"); Directory.CreateDirectory(d); return d; } }

    /// <summary>Gives an empty folder under the <c>tests</c> folder of the root.</summary>
    public static string Fresh(string name)
    {
        var dir = Path.Combine(Root, "tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Record(string file, string line) => File.AppendAllText(Path.Combine(Root, file), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
}

/// <summary>
/// Tests on the fake camera. The camera thumbnails turn the way the camera was held. The service reads the turn from the first header of
/// each capture, keeps it beside the thumbnail and follows the setting. The list of the camera stays across restarts: it shows at once as
/// "checking for changes", then the service checks it against the camera. An import never copies a capture that is gone from the camera.
/// </summary>
public sealed class GapsCameraTests
{
    readonly ITestOutputHelper _out;
    public GapsCameraTests(ITestOutputHelper output) => _out = output;

    sealed record Rig(Harness H, FakeCamera Camera, CameraService Service, string Root) : IAsyncDisposable
    {
        public CameraViewModel Vm => H.Vm.Camera;
        public CameraView View => H.Window.FindControl<CameraView>("CameraPage")!;
        public async ValueTask DisposeAsync() => await H.DisposeAsync();
        public Task SettleThumbsAsync() => H.SettleAsync(() => Vm.Thumbnails.InFlight == 0, 8000);
    }

    /// <summary>Opens the app on the fake library with a camera service. Its thumbnails and kept lists are under <paramref name="root"/>. The
    /// same root in two calls acts as the same app data folder across a restart.</summary>
    static async Task<Rig> OpenAsync(string root, FakeCameraOptions? camera = null, Action<FakeCamera>? before = null, bool connect = true)
    {
        FakeCamera? fc = null; CameraService? svc = null;
        var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 }, camera: lib =>
        {
            fc = new FakeCamera(camera ?? new FakeCameraOptions(), lib);
            before?.Invoke(fc);
            svc = new CameraService(lib, new FakeMtpBackend(fc), new CameraServiceOptions
            {
                StartPolling = false, PreviewFolder = Path.Combine(root, "camera-previews"), ListingFolder = Path.Combine(root, "camera-listings"),
                ShrinkPreview = CameraPreviews.Shrink, DefaultDestination = Path.Combine(root, "Light"), ErrorRetry = TimeSpan.Zero,
                Log = line => Gaps.Record("camera-log.txt", line),
            });
            return svc;
        });
        var rig = new Rig(h, fc!, svc!, root);
        h.Vm.CurrentPage = Page.Camera;
        await h.SettleAsync();
        if (connect)
        {
            svc!.Poll();
            await h.SettleAsync(() => svc.Status.State == CameraState.Connected, 15_000);
            await rig.SettleThumbsAsync();
        }
        return rig;
    }

    static string Shot(Rig r, string name) => Harness.Capture(r.H.Window, name, Gaps.Shots);

    // ------------------------------------------------------------------------------------------------ thumbnails as held

    [AvaloniaFact]
    public async Task Camera_thumbnails_are_turned_as_held_lazily_and_the_turn_is_kept()
    {
        string root = Gaps.Fresh("held");
        int shown;
        await using (var r = await OpenAsync(root))
        {
            var vm = r.Vm;
            // the turns come after the thumbnails, because they have a lower priority than the listing; each turn is two small reads
            bool AllKnown() => vm.Visible.Where(v => vm.Thumbnails.IsCurrent(v.Key)).All(v => v.Capture.HeldDegrees is not null);
            await r.H.SettleAsync(AllKnown, 10_000);
            var withThumb = vm.Visible.Where(v => vm.Thumbnails.IsCurrent(v.Key)).ToList();
            shown = withThumb.Count;
            Assert.True(shown >= 8, $"{shown} thumbnails");
            Assert.True(AllKnown());
            foreach (var v in withThumb)
            {
                var f = r.Camera.Files.First(x => x.Name == v.Capture.FileName);
                Assert.Equal(f.Held, v.Capture.HeldDegrees);
                Assert.Equal(f.Held is 90 or 180 or 270 ? f.Held : 0, v.Rotation);
            }
            var turned = withThumb.Where(v => v.Rotation is 90 or 270).ToList();
            Assert.NotEmpty(turned);
            Assert.Contains(withThumb, v => v.Capture.HeldDegrees == -1 || v.Capture.HeldDegrees == 0);
            int known = r.Service.Captures.Count(c => c.HeldDegrees is not null);
            Assert.Equal(2 * known, r.Camera.RangeReads);   // the block header and the message: nothing else is read
            Assert.Equal(0, r.Camera.CaptureReads);          // never a whole capture
            // the thumbnail views draw them turned
            await r.H.SettleAsync(timeoutMs: 300);
            var views = r.View.GetVisualDescendants().OfType<CameraThumbnailView>().ToList();
            Assert.Contains(views, t => t.Orientation is 90 or 270);
            // kept beside the thumbnails
            var held = Directory.GetFiles(Path.Combine(root, "camera-previews"), "*.held", SearchOption.AllDirectories);
            Assert.Equal(known, held.Length);
            Shot(r, "gaps-01-camera-thumbnails-as-held");
            Gaps.Record("figures.txt", $"held turns: {shown} thumbnails on screen, {known} turns read with {r.Camera.RangeReads} partial reads, {turned.Count} shown turned");

            // the setting "show photos as held" off: shown as captured; on again: turned
            r.H.Lib.FakeSettings.ShowPhotosAsHeld = false;
            await r.H.SettleAsync(() => vm.Visible.All(v => v.Rotation == 0), 3000);
            Assert.All(vm.Visible, v => Assert.Equal(0, v.Rotation));
            Shot(r, "gaps-01b-camera-thumbnails-setting-off");
            r.H.Lib.FakeSettings.ShowPhotosAsHeld = true;
            await r.H.SettleAsync(() => turned.All(v => v.Rotation is 90 or 270), 3000);
            Assert.All(turned, v => Assert.True(v.Rotation is 90 or 270));
        }

        // the next run: the files beside the thumbnails give the turns, so the service makes no partial read
        File.Delete(Path.Combine(root, "camera-listings", "LFCL0000DEMO0001.json"));   // only the thumbnails' files this time
        await using (var r = await OpenAsync(root))
        {
            var vm = r.Vm;
            await r.H.SettleAsync(() => vm.Visible.Where(v => vm.Thumbnails.IsCurrent(v.Key)).All(v => v.Capture.HeldDegrees is not null), 10_000);
            Assert.Equal(0, r.Camera.RangeReads);
            Assert.Equal(0, r.Camera.PreviewReads);
            Assert.Contains(vm.Visible, v => v.Rotation is 90 or 270);
        }
    }

    // ------------------------------------------------------------------------------------------------ the list kept across restarts

    [AvaloniaFact]
    public async Task Camera_list_is_kept_across_restarts_shown_at_once_and_checked_against_the_camera()
    {
        string root = Gaps.Fresh("kept");
        int count;
        await using (var r = await OpenAsync(root, new FakeCameraOptions { SmallFiles = true }))
        {
            count = r.Service.Captures.Count;
            Assert.Equal(214, count);
            Assert.False(r.Service.Status.CheckingEarlierList);
        }
        string kept = Path.Combine(root, "camera-listings", "LFCL0000DEMO0001.json");
        Assert.True(File.Exists(kept), "the list is kept when Lux closes");

        // while Lux was closed: one capture deleted on the camera, one new, one replaced under the same name (another size)
        var sw = Stopwatch.StartNew();
        await using (var r = await OpenAsync(root, new FakeCameraOptions { SmallFiles = true }, fc =>
        {
            fc.Remove("L16_01001.lri");
            fc.Add(new FakeCameraFile(FakeCamera.CameraFolder, "L16_01100.lri", 52_000, new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.FromHours(2)), 1100, false, false));
            fc.Replace("L16_01002.lri", f => f with { Size = f.Size + 5_000 });
            fc.FileListGate.Reset();   // the camera's file list takes its time
        }, connect: false))
        {
            var vm = r.Vm;
            r.Service.Poll();
            // at once: the kept list, marked as being checked
            await r.H.SettleAsync(() => r.Service.Status.CheckingEarlierList && vm.ShowGrid, 5000);
            long shownMs = sw.ElapsedMilliseconds;
            Assert.True(r.Service.Status.CheckingEarlierList);
            Assert.Equal(count, r.Service.Captures.Count);
            Assert.Contains(r.Service.Captures, c => c.FileName == "L16_01001.lri");
            Assert.Contains("checking the camera for changes", vm.StateLine);
            Assert.Contains("showing the list from ", vm.StateLine);
            Assert.Equal("Light L16 connected · checking for changes", r.H.Vm.StatusDevice);
            Assert.False(vm.ShowWaiting);
            await r.SettleThumbsAsync();
            Shot(r, "gaps-02-camera-kept-list-checking");

            // the camera answers: the deleted one leaves, the new one appears, the replaced one is read again
            r.Camera.FileListGate.Set();
            await r.H.SettleAsync(() => r.Service.Status.State == CameraState.Connected, 15_000);
            var caps = r.Service.Captures;
            Assert.DoesNotContain(caps, c => c.FileName == "L16_01001.lri");
            Assert.Contains(caps, c => c.FileName == "L16_01100.lri" && c.Size == 52_000);
            var replaced = caps.Single(c => c.FileName == "L16_01002.lri");
            Assert.Equal(r.Camera.Files.Single(f => f.Name == "L16_01002.lri").Size, replaced.Size);
            Assert.Equal(count, caps.Count);
            Assert.False(r.Service.Status.CheckingEarlierList);
            Assert.DoesNotContain(vm.Visible, v => v.Capture.FileName == "L16_01001.lri");
            Assert.DoesNotContain("checking", vm.StateLine);
            await r.SettleThumbsAsync();
            Shot(r, "gaps-02b-camera-kept-list-checked");
            Gaps.Record("figures.txt", $"kept list: {count} captures shown {shownMs} ms after opening, before the camera's file list; reconciled: 1 gone, 1 new, 1 replaced");
        }
        Assert.Contains("L16_01100.lri", File.ReadAllText(kept));
        Assert.DoesNotContain("L16_01001.lri", File.ReadAllText(kept));
    }

    [AvaloniaFact]
    public async Task Import_never_copies_a_capture_that_is_no_longer_on_the_camera()
    {
        string root = Gaps.Fresh("gone");
        await using var r = await OpenAsync(root, new FakeCameraOptions { SmallFiles = true });
        var vm = r.Vm;
        string dest = Path.Combine(root, "Light");
        vm.Destination = dest;
        vm.OneFolderPerDay = false;
        vm.AfterIndex = 2;   // only copy
        var pick = vm.Visible.Take(3).ToList();
        foreach (var i in pick) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        Assert.True(vm.CanImport, vm.Problem);
        // deleted on the camera after the list was read (the list on screen still shows it)
        r.Camera.Remove(pick[1].Capture.FileName);
        vm.ImportCommand.Execute(null);
        await r.H.SettleAsync(waitThumbnails: false);
        var job = r.H.Lib.Jobs.GetJobs().Single(j => j.Kind == JobKind.Import);
        r.H.Lib.FakeJobs.Tick(0.1);
        var sw = Stopwatch.StartNew();
        while (!(r.H.Lib.Jobs.Get(job.Id)?.IsFinished ?? true) && sw.ElapsedMilliseconds < 30_000) { await r.H.SettleAsync(timeoutMs: 200, waitThumbnails: false); await Task.Delay(20); }
        var done = r.H.Lib.Jobs.Get(job.Id)!;
        _out.WriteLine($"{done.State}: {string.Join(" | ", done.Notes)} {done.Error}");
        Assert.Equal(JobState.Done, done.State);
        Assert.Contains($"{pick[1].Name} is no longer on the camera and was not copied", done.Notes);
        Assert.True(File.Exists(Path.Combine(dest, pick[0].Capture.FileName)));
        Assert.False(File.Exists(Path.Combine(dest, pick[1].Capture.FileName)));
        Assert.True(File.Exists(Path.Combine(dest, pick[2].Capture.FileName)));
        Assert.Equal(2, done.ResultFiles.Count);
        await r.H.SettleAsync(() => vm.Visible.All(v => v.Key != pick[1].Key), 3000, waitThumbnails: false);
        Assert.DoesNotContain(r.Service.Captures, c => c.Key == pick[1].Key);

        // every chosen photo gone: the job fails and says so plainly
        var two = vm.Visible.Where(v => !v.InLibrary && v.Key != pick[0].Key && v.Key != pick[2].Key).Take(2).ToList();
        foreach (var i in vm.Visible.Where(v => v.IsSelected).ToList()) vm.Toggle(i, false);
        foreach (var i in two) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        foreach (var i in two) r.Camera.Remove(i.Capture.FileName);
        vm.ImportCommand.Execute(null);
        await r.H.SettleAsync(waitThumbnails: false);
        var job2 = r.H.Lib.Jobs.GetJobs().Where(j => j.Kind == JobKind.Import).OrderByDescending(j => j.Id).First();
        r.H.Lib.FakeJobs.Tick(0.1);
        sw.Restart();
        while (!(r.H.Lib.Jobs.Get(job2.Id)?.IsFinished ?? true) && sw.ElapsedMilliseconds < 30_000) { await r.H.SettleAsync(timeoutMs: 200, waitThumbnails: false); await Task.Delay(20); }
        var failed = r.H.Lib.Jobs.Get(job2.Id)!;
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal("None of the 2 photos chosen are on the camera any more, so nothing was copied.", failed.Error);
    }
}

/// <summary>
/// The held turn and the kept list on the real Light L16. The test runs only with LUX_GAPS_REAL=1. It only reads from the camera: the list,
/// file information, previews and partial reads of capture headers. It writes its results to real-camera.txt in the test folder. It reads
/// local copies of the same captures (same name and size) to compare the first-header rule with the whole-file rule of the library.
/// </summary>
public sealed class GapsRealCameraTests
{
    readonly ITestOutputHelper _out;
    public GapsRealCameraTests(ITestOutputHelper output) => _out = output;
    void Say(string s) { _out.WriteLine(s); Gaps.Record("real-camera.txt", s); }
    static bool Real => Environment.GetEnvironmentVariable("LUX_GAPS_REAL") == "1";

    static async Task<bool> Until(Func<bool> cond, int timeoutMs, int stepMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; await Task.Delay(stepMs); }
        return cond();
    }

    [Fact]
    public async Task Real_held_turns_and_the_kept_list()
    {
        if (!Real) { _out.WriteLine("LUX_GAPS_REAL not set: skipped"); return; }
        var root = Gaps.Fresh("real");
        await using var lib = Lux.Library.LuxLibrary.Open(new Lux.Library.LibraryOpenOptions { DatabasePath = Path.Combine(root, "library.luxlib"), StartWorkers = false, WatchFolders = false });
        CameraServiceOptions Options() => new()
        {
            PreviewFolder = Path.Combine(root, "previews"), ListingFolder = Path.Combine(root, "listings"), ShrinkPreview = CameraPreviews.Shrink, Log = s => _out.WriteLine(s),
        };
        var sw = Stopwatch.StartNew();
        int count;
        var held = new Dictionary<string, (int Deg, long Size, double Ms)>();
        using (var cam = new CameraService(lib, Lux.Engine.Mtp.MtpFactory.Backend, Options()))
        {
            Assert.True(await Until(() => cam.Status.State == CameraState.Connected, 180_000), "listing did not finish: " + cam.Status.State + " " + cam.Status.Message);
            count = cam.Captures.Count;
            Say($"first run: {count} captures listed in {sw.Elapsed.TotalSeconds:0.0} s");
            // the thumbnails of the 30 newest captures with a preview, one after the other; each held turn follows its thumbnail
            // these are the 20 newest, and 10 that have a local copy of the same name and size for the comparison below
            static bool HasLocal(CameraCapture c) { var f = new FileInfo(Path.Combine(Paths.Captures, c.FileName)); return f.Exists && f.Length == c.Size; }
            var pick = cam.Captures.Where(c => c.PreviewFile is not null).Take(20)
                          .Concat(cam.Captures.Where(c => c.PreviewFile is not null && c.Size < 200_000_000 && HasLocal(c)).Take(10)).DistinctBy(c => c.Key).ToList();
            foreach (var c in pick)
            {
                var t0 = Stopwatch.StartNew();
                var thumb = await cam.GetPreviewAsync(c);
                double previewMs = t0.Elapsed.TotalMilliseconds;
                Assert.True(await Until(() => cam.Captures.First(x => x.Key == c.Key).HeldDegrees is not null, 10_000, 1), $"no held turn for {c.Name}");
                double heldMs = t0.Elapsed.TotalMilliseconds - previewMs;
                int deg = cam.Captures.First(x => x.Key == c.Key).HeldDegrees!.Value;
                held[c.FileName] = (deg, c.Size, heldMs);
                _out.WriteLine($"  {c.Name}: preview {previewMs:0} ms ({thumb?.Length ?? 0} B), held turn {deg} after {heldMs:0.0} ms more");
            }
            var ms = held.Values.Select(h => h.Ms).OrderBy(x => x).ToList();
            Say($"held turns of {held.Count} captures: median {ms[ms.Count / 2]:0.0} ms, max {ms[^1]:0.0} ms, mean {ms.Average():0.0} ms (after the thumbnail); " +
                $"turns: {string.Join(", ", held.Values.GroupBy(h => h.Deg).OrderBy(g => g.Key).Select(g => $"{g.Key}° × {g.Count()}"))}");
            Assert.True(ms[ms.Count / 2] < 150, "a held turn should cost well under 150 ms");
        }
        // the same rule on the whole file: local copies of the same captures (same name and size), read only
        int compared = 0;
        foreach (var (name, h) in held)
        {
            string local = Path.Combine(Paths.Captures, name);
            if (!File.Exists(local) || new FileInfo(local).Length != h.Size) continue;
            int whole = Lux.Store.HeldTurn.Degrees(Lux.Engine.Lri.LriFile.Load(local));
            Assert.Equal(whole, h.Deg);
            compared++;
        }
        Say($"first-header rule = whole-file rule on {compared} local copies");

        // the next start: the kept list at once, checked against the camera
        sw.Restart();
        double? shownAt = null; bool sawChecking = false;
        using (var cam = new CameraService(lib, Lux.Engine.Mtp.MtpFactory.Backend, Options()))
        {
            Assert.True(await Until(() =>
            {
                if (cam.Status.CheckingEarlierList) sawChecking = true;
                if (shownAt is null && cam.Captures.Count > 0) shownAt = sw.Elapsed.TotalSeconds;
                return cam.Status.State == CameraState.Connected;
            }, 180_000, 20), "second listing did not finish: " + cam.Status.State);
            Say($"second run: kept list of {count} shown after {shownAt:0.00} s (checking: {sawChecking}); checked and complete after {sw.Elapsed.TotalSeconds:0.0} s; " +
                $"{cam.Captures.Count} captures; held turns known for {cam.Captures.Count(c => c.HeldDegrees is not null)}");
            Assert.True(sawChecking);
            Assert.True(shownAt < 3, "the kept list shows within seconds");
            Assert.True(cam.Captures.Count(c => c.HeldDegrees is not null) >= held.Count);
        }
    }
}

/// <summary>
/// Copies set aside after a conflict. The first test uses the real library on a link to one real capture in the test folder; it only reads
/// the original. It makes a conflict the way it happens: data in the local store, different data beside the photo, then a rescan. Then it
/// restores the copy, swaps it back, checks the refusal while work waits in the Queue, and removes the copy. The second test checks the
/// Settings section on the fake library: the list, restore, the refusal and the question before a remove.
/// </summary>
public sealed class GapsSetAsideTests
{
    readonly ITestOutputHelper _out;
    public GapsSetAsideTests(ITestOutputHelper output) => _out = output;

    string Original = Path.Combine(Paths.Captures, "L16_00540.lri");

    [Fact]
    public async Task Real_library_restores_a_set_aside_copy_and_can_swap_back()
    {
        if (!File.Exists(Original)) { _out.WriteLine("no local capture: skipped"); return; }
        string root = Gaps.Fresh("setaside");
        string photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        File.CreateSymbolicLink(Path.Combine(photos, "L16_00540.lri"), Original);   // a linked copy: nothing is written beside the original
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions
        {
            DatabasePath = Path.Combine(root, "lib", "library.luxlib"), WatchFolders = false, MakeThumbnailsAfterScan = false, TrashDirectoryOverride = Path.Combine(root, "trash"),
            Log = s => Gaps.Record("setaside-log.txt", s),
        });
        lib.Settings.AnalyseNewCaptures = false;
        var folder = await lib.AddFolderAsync(photos);
        if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
        var item = (await lib.QueryAsync(new CaptureQuery { Limit = 10 })).Items.Single();
        string id = item.Id;
        string beside = Path.Combine(photos, "LuxImageContext", "edits", id + ".luxedits");
        string store = lib.Settings.LocalContextFolder;
        string local = Path.Combine(store, id, "edits", id + ".luxedits");

        // the conflict as it happens: a rating kept in the local store (3), a different one beside the photo (5), then the library looks again
        await lib.SetRatingAsync(id, 3);
        Assert.True(File.Exists(beside), "the rating goes beside the photo");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.Copy(beside, local);
        await lib.SetRatingAsync(id, 5);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await lib.Jobs.WaitAsync(lib.RescanFolder(folder.Id));
        var copies = lib.GetSetAsideCopies();
        _out.WriteLine(string.Join("\n", copies.Select(c => $"{c.PhotoName} {c.SetAsideUtc:u} {c.Bytes} B edits {c.HasEdits} restore {c.CanRestore} {c.Path}")));
        var first = Assert.Single(copies);
        Assert.Equal("L16_00540", first.PhotoName);
        Assert.True(first.HasEdits && first.CanRestore);
        Assert.False(File.Exists(local));
        Assert.Equal(5, lib.Get(id)!.Rating);
        Assert.NotNull(lib.Get(id)!.SupersededContextPath);
        string keptSha = Lux.Store.LocalContextStore.Sha(Path.Combine(first.Path, "edits", id + ".luxedits"));
        string currentSha = Lux.Store.LocalContextStore.Sha(beside);

        // refused while work on the photo waits in the Queue
        lib.Jobs.PauseAll();
        long job = lib.Jobs.Enqueue(new AnalysisJobRequest(id, Lux.Engine.Session.CacheLevel.Geometry));
        var busy = await Assert.ThrowsAsync<LibraryException>(() => lib.RestoreSetAsideCopyAsync(first.Path));
        Assert.Equal("L16_00540 has work in the Queue. Restore the copy when that work is done, or cancel it in the Queue.", busy.Message);
        lib.Jobs.Cancel(job);
        lib.Jobs.ResumeAll();
        for (int i = 0; i < 50 && lib.Jobs.GetJobs(includeFinished: false).Any(j => j.CaptureId == id); i++) await Task.Delay(50);

        // restore: the kept copy (rating 3) is in use beside the photo; the one it replaced (rating 5) is set aside in its place
        await lib.RestoreSetAsideCopyAsync(first.Path);
        Assert.Equal(keptSha, Lux.Store.LocalContextStore.Sha(beside));
        Assert.Equal(3, lib.Get(id)!.Rating);
        Assert.False(Directory.Exists(first.Path));
        var second = Assert.Single(lib.GetSetAsideCopies());
        Assert.Equal(currentSha, Lux.Store.LocalContextStore.Sha(Path.Combine(second.Path, "edits", id + ".luxedits")));
        Assert.True(second.SetAsideUtc >= first.SetAsideUtc);

        // and back
        await lib.RestoreSetAsideCopyAsync(second.Path);
        Assert.Equal(currentSha, Lux.Store.LocalContextStore.Sha(beside));
        Assert.Equal(5, lib.Get(id)!.Rating);
        var third = Assert.Single(lib.GetSetAsideCopies());

        // remove: to the trash; the photo keeps its data
        await lib.RemoveSetAsideCopyAsync(third.Path);
        Assert.Empty(lib.GetSetAsideCopies());
        Assert.False(Directory.Exists(third.Path));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "trash")));
        Assert.Equal(5, lib.Get(id)!.Rating);
        Assert.Null(lib.Get(id)!.SupersededContextPath);
        // a path that is not a set-aside copy is refused plainly
        var bad = await Assert.ThrowsAsync<LibraryException>(() => lib.RestoreSetAsideCopyAsync(photos));
        Assert.Equal("That folder is not a set-aside copy.", bad.Message);
        Assert.Equal(Original, File.ResolveLinkTarget(Path.Combine(photos, "L16_00540.lri"), false)!.FullName);
        Gaps.Record("figures.txt", "set-aside copies: conflict made, listed, restore refused while queued, restored (rating 5 → 3), swapped back (3 → 5), removed to the trash");
    }

    [AvaloniaFact]
    public async Task Settings_lists_set_aside_copies_restores_and_asks_before_removing()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 });
        h.Vm.ShowSettings();
        await h.SettleAsync();
        var sa = h.Vm.Settings!.SetAside;
        await h.SettleAsync(() => sa.HasCopies, 3000);
        var row = Assert.Single(sa.Copies);
        Assert.Equal("1 copy is set aside.", sa.Summary);
        Assert.StartsWith("Set aside 11 Oct 2026 ", row.Detail);
        Assert.True(row.CanRestore);
        var view = h.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var block = view.FindControl<StackPanel>("SetAsideBlock")!;
        block.BringIntoView();
        await h.SettleAsync();
        Harness.Capture(h.Window, "gaps-03-settings-set-aside", Gaps.Shots);

        // refused while the photo is open: said plainly, nothing changes
        h.Lib.OpenForSetAside.Add(row.Copy.CaptureId);
        await row.RestoreCommand.ExecuteAsync(null);
        await h.SettleAsync();
        Assert.Equal($"{row.Name} is open in Lux. Close it (go back to the Library), then restore the copy.", sa.Error);
        block.Children.OfType<TextBlock>().Last().BringIntoView();
        await h.SettleAsync();
        Harness.Capture(h.Window, "gaps-03b-settings-set-aside-refused", Gaps.Shots);
        h.Lib.OpenForSetAside.Clear();

        // restore: the copy swaps in; the replaced data is the copy listed now (set aside just now)
        await row.RestoreCommand.ExecuteAsync(null);
        await h.SettleAsync();
        Assert.Null(sa.Error);
        var swapped = Assert.Single(sa.Copies);
        Assert.NotEqual(row.Copy.Path, swapped.Copy.Path);
        Assert.True(swapped.Copy.SetAsideUtc > row.Copy.SetAsideUtc);

        // remove asks first; Cancel keeps it, Remove copy removes it
        swapped.RemoveCommand.Execute(null);
        await h.SettleAsync();
        var confirm = Assert.IsType<ConfirmViewModel>(h.Vm.Dialog);
        Assert.Equal($"Remove the set-aside copy of {swapped.Name}?", confirm.Title);
        Assert.Contains("goes to the trash", confirm.Text);
        Harness.Capture(h.Window, "gaps-03c-settings-set-aside-remove-confirm", Gaps.Shots);
        confirm.CancelCommand.Execute(null);
        await h.SettleAsync();
        Assert.Single(sa.Copies);
        swapped.RemoveCommand.Execute(null);
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => !sa.HasCopies, 3000);
        Assert.False(sa.HasCopies);
        Assert.Equal("No copies are set aside.", sa.Summary);
    }
}

/// <summary>
/// Error states of the real library: a missing capture, an unreadable (short) capture, a failed analysis, an export into a folder that Lux
/// cannot write or make, a full drive, a local data folder that Lux cannot use, and a Lumen session that cannot be read. The test uses links
/// and test files in the test folder and only reads the originals. It records every message in error-states.txt. Each message must be a
/// plain sentence, with no exception type, no stack and no error code.
/// </summary>
public sealed class GapsErrorStatesTests
{
    readonly ITestOutputHelper _out;
    public GapsErrorStatesTests(ITestOutputHelper output) => _out = output;

    static readonly string[] Raw = { "Exception", "   at ", "HRESULT", "errno", "System.", "(-", "SQLite Error", "Object reference" };

    void Record(string state, string? message)
    {
        _out.WriteLine($"{state}: {message}");
        Gaps.Record("error-states.txt", $"[real] {state}: {message}");
        Assert.False(string.IsNullOrWhiteSpace(message), state);
        foreach (var r in Raw) Assert.DoesNotContain(r, message!, StringComparison.Ordinal);
        Assert.EndsWith(".", message!.TrimEnd());
    }

    static async Task<string?> Record2(Func<Task> f)
    {
        try { await f(); return null; }
        catch (LibraryException e) { return e.Message; }
    }

    [Fact]
    public async Task Real_library_says_plainly_what_happened_and_what_to_do()
    {
        string src = Paths.Captures;
        if (!File.Exists(Path.Combine(src, "L16_00540.lri"))) { _out.WriteLine("no local captures: skipped"); return; }
        string root = Gaps.Fresh("errors");
        string photos = Path.Combine(root, "photos"), ro = Path.Combine(root, "read-only"), lumen = Path.Combine(root, "lumen");
        Directory.CreateDirectory(photos); Directory.CreateDirectory(ro); Directory.CreateDirectory(lumen);
        File.CreateSymbolicLink(Path.Combine(photos, "L16_00540.lri"), Path.Combine(src, "L16_00540.lri"));
        File.CreateSymbolicLink(Path.Combine(photos, "L16_00542.lri"), Path.Combine(src, "L16_00542.lri"));
        File.CreateSymbolicLink(Path.Combine(lumen, "L16_00544.lri"), Path.Combine(src, "L16_00544.lri"));
        // a short capture (the first 12 MB of a real one, written here) and a Lumen session file that is not a session
        using (var i = File.OpenRead(Path.Combine(src, "L16_00541.lri")))
        using (var o = File.Create(Path.Combine(photos, "L16_00541.lri")))
        {
            var buf = new byte[12 << 20]; int n = i.ReadAtLeast(buf, buf.Length); o.Write(buf, 0, n);
        }
        File.WriteAllBytes(Path.Combine(lumen, "L16_00544.lris"), Enumerable.Range(0, 4096).Select(k => (byte)(k * 37 + 11)).ToArray());
        File.SetUnixFileMode(ro, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await using var lib = LuxLibrary.Open(new LibraryOpenOptions
            {
                DatabasePath = Path.Combine(root, "lib", "library.luxlib"), WatchFolders = false, MakeThumbnailsAfterScan = false, TrashDirectoryOverride = Path.Combine(root, "trash"),
                Log = s => Gaps.Record("errors-log.txt", s),
            });
            lib.Settings.AnalyseNewCaptures = false;
            var folder = await lib.AddFolderAsync(photos);
            if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
            var lfolder = await lib.AddFolderAsync(lumen);
            if (lfolder.ScanJobId is long scan2) await lib.Jobs.WaitAsync(scan2);
            CaptureItem Item(string name) => lib.QueryAsync(new CaptureQuery { Limit = 50 }).Result.Items.Single(c => c.Name == name);

            // an unreadable capture: its scan error
            var broken = Item("L16_00541");
            Assert.True(broken.Unreadable);
            Record("unreadable capture (scan)", broken.Error);
            var e1 = await Assert.ThrowsAsync<LibraryException>(() => lib.OpenPhotoAsync(broken.Id));
            Record("unreadable capture (open)", e1.Message);
            var a1 = await lib.Jobs.WaitAsync(lib.Jobs.Enqueue(new AnalysisJobRequest(broken.Id, Lux.Engine.Session.CacheLevel.Geometry)));
            Assert.Equal(JobState.Failed, a1.State);
            Record("analysis failed (unreadable capture)", a1.Error);

            // a missing capture (its file went away)
            File.Delete(Path.Combine(photos, "L16_00542.lri"));
            await lib.Jobs.WaitAsync(lib.RescanFolder(folder.Id));
            var missing = Item("L16_00542");
            Assert.True(missing.Missing);
            var e2 = await Assert.ThrowsAsync<LibraryException>(() => lib.OpenPhotoAsync(missing.Id));
            Record("missing capture (open)", e2.Message);
            var a2 = await lib.Jobs.WaitAsync(lib.Jobs.Enqueue(new AnalysisJobRequest(missing.Id, Lux.Engine.Session.CacheLevel.Geometry)));
            Record("analysis failed (missing capture)", a2.Error);
            var x2 = await lib.Jobs.WaitAsync(lib.Export(new[] { missing.Id }, new ExportOptions { Formats = new[] { "jpg" }, OutputFolder = Path.Combine(root, "out") }).Single());
            Record("export failed (missing capture)", x2.Error);

            // exports: a folder Lux may not write, one it cannot create — refused at once, before any rendering
            var good = Item("L16_00540");
            var sw = Stopwatch.StartNew();
            var x3 = await lib.Jobs.WaitAsync(lib.Export(new[] { good.Id }, new ExportOptions { Formats = new[] { "jpg" }, OutputFolder = ro }).Single());
            Assert.Equal(JobState.Failed, x3.State);
            Record("export failed (no permission)", x3.Error);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"refused after {sw.ElapsedMilliseconds} ms");
            var x4 = await lib.Jobs.WaitAsync(lib.Export(new[] { good.Id }, new ExportOptions { Formats = new[] { "jpg" }, OutputFolder = Path.Combine(ro, "new") }).Single());
            Record("export failed (folder cannot be made)", x4.Error);
            // a full drive while writing: the full device of the system gives the real error
            IOException? full = null;
            try { using var f = new FileStream("/dev/full", FileMode.Open, FileAccess.Write); f.Write(new byte[1 << 16]); f.Flush(); }
            catch (IOException e) { full = e; }
            Assert.NotNull(full);
            _out.WriteLine($"/dev/full: HResult {full!.HResult}, '{full.Message}'");
            Record("drive full while writing (job message)", Lux.Library.Core.LibraryCore.Plain(full));
            Record("drive full while writing (app wording)", Lux.App.ViewModels.Text.Plain(full));

            // the folder for data of read-only photos: one Lux cannot write is refused
            var e5 = Assert.Throws<LibraryException>(() => lib.ChangeLocalContextFolder(ro));
            Record("local data folder cannot be written", e5.Message);

            // nowhere to save a rating: the photo's folder and the local data folder both read-only
            string store = lib.Settings.LocalContextFolder;
            Directory.CreateDirectory(store);
            File.SetUnixFileMode(photos, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(store, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Lux.Store.LocalContextStore.ForgetProbes();
            try
            {
                var rated = await Record2(() => lib.SetRatingAsync(good.Id, 4));
                Record("rating with nowhere to save it", rated ?? $"(no error; rating now {lib.Get(good.Id)!.Rating}, source {lib.Get(good.Id)!.RatingSource}, location {lib.Get(good.Id)!.ContextLocation}).");
            }
            finally
            {
                File.SetUnixFileMode(photos, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(store, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Lux.Store.LocalContextStore.ForgetProbes();
            }

            // a Lumen session that cannot be read: the import says so
            var lumenItem = Item("L16_00544");
            _out.WriteLine($"lumen session state: {lumenItem.LumenSession}, has session {lumenItem.HasLumenSession}");
            var jobs = lib.ImportLumenEdits(new[] { lumenItem.Id });
            foreach (var j in jobs)
            {
                var done = await lib.Jobs.WaitAsync(j);
                Record($"Lumen import of a damaged session ({done.State})", done.State == JobState.Failed ? done.Error : string.Join(" ", done.Notes));
            }
            if (jobs.Count == 0) Record("Lumen import of a damaged session (nothing queued)", "Nothing was queued.");
        }
        finally { File.SetUnixFileMode(ro, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
}

/// <summary>
/// Error and empty states in the app, on the fake library and the fake camera. Each test takes a screenshot of the state and records its
/// text. The states: no library folders, a folder whose drive is gone, a missing and an unreadable capture (details and Develop), failed
/// analysis, export and Lumen import jobs in the Queue (with the messages of the real library, see <see cref="GapsErrorStatesTests"/>), a
/// local data folder that Lux cannot write, and a camera that is busy, fails or is unplugged. Each state says what happened and gives a
/// way to go on.
/// </summary>
public sealed class GapsErrorStatesUiTests
{
    readonly ITestOutputHelper _out;
    public GapsErrorStatesUiTests(ITestOutputHelper output) => _out = output;

    static readonly string[] Raw = { "Exception", "   at ", "HRESULT", "(-1)", "(-7)", "(-53)", "System." };

    void Record(string state, string? text)
    {
        _out.WriteLine($"{state}: {text}");
        Gaps.Record("error-states.txt", $"[app] {state}: {text}");
        Assert.False(string.IsNullOrWhiteSpace(text), state);
        foreach (var r in Raw) Assert.DoesNotContain(r, text!, StringComparison.Ordinal);
    }

    static string Shot(Harness h, string name) => Harness.Capture(h.Window, name, Gaps.Shots);

    [AvaloniaFact]
    public async Task Library_empty_folder_gone_missing_and_unreadable_captures()
    {
        await using (var e = await Harness.CreateAsync(new FakeLibraryOptions { Empty = true, SeedJobs = false }))
        {
            Assert.True(e.Vm.ShowEmpty);
            Shot(e, "gaps-04a-no-library-folders");
            Record("no library folders", string.Join(" | ", e.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text?.Length > 30).Select(t => t.Text).Take(3)));
        }

        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 });
        // a folder whose drive is not connected: the tree says so and how to go on; its photos show Missing
        h.Lib.SetFolderAvailableForTests(1, false);
        await h.Library.RefreshTreeAsync();
        await h.SettleAsync();
        var node = h.Library.Tree.First(n => n.Key == "folder:1");
        Assert.Equal("not found", node.CountText);
        Assert.Contains("Connect it and choose Rescan", node.ToolTipText);
        Record("folder not found (tree)", node.ToolTipText);
        await h.SelectScopeAsync("folder:1");
        Shot(h, "gaps-04b-folder-not-found");
        h.Vm.ShowSettings();
        await h.SettleAsync();
        await h.Vm.Settings!.RefreshFoldersAsync();
        await h.SettleAsync();
        Record("folder not found (settings)", h.Vm.Settings.Folders.First(f => f.Folder.Id == 1).CountText);
        Assert.Contains("folder not found", h.Vm.Settings.Folders.First(f => f.Folder.Id == 1).CountText);
        Shot(h, "gaps-04c-settings-folder-not-found");
        h.Vm.ShowLibrary();
        await h.Library.RefreshTreeAsync();
        await h.SettleAsync();

        // a missing capture: the details say so; Develop says it cannot be opened, why and what to do; the tabs lead back
        await h.SelectScopeAsync("smart:missing");
        var missing = h.Library.Items.First();
        h.Library.Click(missing, false, false);
        await h.SettleAsync();
        Shot(h, "gaps-04d-missing-capture-details");
        h.Vm.OpenInDevelop(missing);
        await h.SettleAsync(() => h.Vm.Develop.OpenError != null, 5000);
        Record("missing capture (Develop)", h.Vm.Develop.OpenError);
        Shot(h, "gaps-04e-missing-capture-develop");
        // an unreadable capture
        h.Lib.SetFolderAvailableForTests(1, true);
        h.Vm.ShowLibrary();
        await h.Library.RefreshTreeAsync();
        await h.SelectScopeAsync("all");
        var unreadableId = (await h.Lib.QueryAsync(new CaptureQuery { Limit = 5000 })).Items.First(c => c.Unreadable).Id;
        await h.SettleAsync();
        var unreadable = h.Library.Items.FirstOrDefault(i => i.Id == unreadableId);
        if (unreadable is not null)
        {
            h.Library.Click(unreadable, false, false);
            await h.SettleAsync();
            Record("unreadable capture (details)", h.Vm.Library.Details.Current?.Item.Error);
            Shot(h, "gaps-04f-unreadable-capture-details");
            h.Vm.OpenInDevelop(unreadable);
            await h.SettleAsync(() => h.Vm.Develop.OpenError != null, 5000);
            Record("unreadable capture (Develop)", h.Vm.Develop.OpenError);
            Shot(h, "gaps-04g-unreadable-capture-develop");
        }
    }

    [AvaloniaFact]
    public async Task Failed_jobs_say_why_and_offer_retry_and_the_local_folder_refusal()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 });
        var items = (await h.Lib.QueryAsync(new CaptureQuery { Limit = 20 })).Items.Where(c => !c.Missing && !c.Unreadable).Take(3).ToList();
        // the real library's words for these failures (GapsErrorStatesTests checks them on real files)
        h.Lib.FakeJobs.FailRule = r => r switch
        {
            AnalysisJobRequest a when a.CaptureId == items[0].Id => "The photo’s file cannot be found (it was moved, deleted, or its drive is not connected). Connect its drive, or rescan its folder in Settings.",
            ExportJobRequest => "Lux is not allowed to write to /media/you/CARD/Export. Choose another export folder, or allow writing to this one.",
            LumenImportJobRequest => "Lumen’s session for this photo cannot be read: the .lris file beside it is damaged or not a Lumen session. Nothing was changed; Lux keeps its own edits of the photo.",
            _ => null,
        };
        h.Lib.Jobs.Enqueue(new AnalysisJobRequest(items[0].Id, Lux.Engine.Session.CacheLevel.Geometry));
        h.Lib.Export(new[] { items[1].Id }, new ExportOptions { Formats = new[] { "jpg" }, OutputFolder = "/media/you/CARD/Export" });
        h.Lib.ImportLumenEdits(new[] { items[2].Id });
        for (int i = 0; i < 40 && h.Lib.Jobs.GetJobs().Count(j => j.State == JobState.Failed) < 3; i++) { h.Lib.FakeJobs.Tick(1.0); await h.SettleAsync(timeoutMs: 200); }
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync();
        var failed = h.Vm.Queue.Jobs.Where(j => j.IsFailed).ToList();
        Assert.Equal(3, failed.Count);
        foreach (var j in failed)
        {
            Record($"failed job ({j.Info.Kind})", j.StatusText);
            Assert.StartsWith("Stopped: ", j.StatusText);
            Assert.Equal("Retry", j.PrimaryAction);
        }
        Shot(h, "gaps-04h-queue-failed-jobs");

        // the folder for data of read-only photos: one Lux cannot write is refused at once, with the reason
        h.Vm.ShowSettings();
        await h.SettleAsync();
        await h.Vm.Settings!.MoveLocalFolderAsync("/media/readonly/lux-data");
        await h.SettleAsync();
        Record("local data folder cannot be written", h.Vm.Settings.LocalError);
        h.Window.GetVisualDescendants().OfType<SettingsView>().Single().FindControl<Border>("StorageSection")!.BringIntoView();
        await h.SettleAsync();
        Shot(h, "gaps-04i-local-data-folder-refused");
    }

    [AvaloniaFact]
    public async Task Camera_busy_failing_and_unplugged()
    {
        string root = Gaps.Fresh("camera-states");
        FakeCamera? fc = null; CameraService? svc = null;
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 }, camera: lib =>
        {
            fc = new FakeCamera(new FakeCameraOptions { SmallFiles = true }, lib);
            svc = new CameraService(lib, new FakeMtpBackend(fc), new CameraServiceOptions
            {
                StartPolling = false, PreviewFolder = Path.Combine(root, "previews"), ShrinkPreview = CameraPreviews.Shrink, DefaultDestination = Path.Combine(root, "Light"), ErrorRetry = TimeSpan.Zero,
            });
            return svc;
        });
        var vm = h.Vm.Camera;
        h.Vm.CurrentPage = Page.Camera;
        // busy: says who holds it and that Lux connects by itself; Try again
        fc!.Busy = true;
        svc!.Poll();
        await h.SettleAsync(() => vm.State == CameraState.Busy, 5000);
        Record("camera busy", vm.Status.Message);
        Assert.True(vm.CanRetry);
        Shot(h, "gaps-04j-camera-busy");
        // the camera fails while listing (still on the bus): a plain sentence, no library error code; Lux tries again
        fc.Busy = false;
        fc.FailNextList = true;
        svc.Reconnect();
        await h.SettleAsync(() => vm.State == CameraState.Error, 5000);
        Record("camera stopped answering", vm.Status.Message);
        Assert.Equal("The camera stopped answering while Lux was reading its list of files. Lux tries again in a moment; switching the camera off and on, or plugging it in again, often helps.", vm.Status.Message);
        Assert.True(vm.CanRetry);
        Shot(h, "gaps-04k-camera-error");
        // tried again: connected; then unplugged: back to "connect the camera"
        svc.Reconnect();
        await h.SettleAsync(() => vm.State == CameraState.Connected, 10_000);
        fc.Unplug();
        svc.Poll();
        await h.SettleAsync(() => vm.State == CameraState.NotConnected, 5000);
        Record("camera unplugged", vm.ConnectText);
        Assert.True(vm.ShowConnect);
        Shot(h, "gaps-04l-camera-unplugged");
    }
}

/// <summary>
/// The JPG color profile option. It is off by default, and then nothing in the file changes. When it is on, the only addition is the sRGB
/// profile (APP2 ICC_PROFILE after the Exif block), and decoders read the file as sRGB. The tests: the encoder on a test image, a small
/// library export of a real capture (through a link) with the option off and on, and the checkbox of the export panel.
/// </summary>
public sealed class GapsColourProfileTests
{
    readonly ITestOutputHelper _out;
    public GapsColourProfileTests(ITestOutputHelper output) => _out = output;

    /// <summary>The file without its APP2 ICC_PROFILE segments.</summary>
    static byte[] WithoutIcc(byte[] jpg, out int segments)
    {
        var o = new List<byte>(jpg.Length);
        segments = 0;
        int i = 2;
        o.AddRange(jpg.AsSpan(0, 2).ToArray());
        while (i + 4 <= jpg.Length && jpg[i] == 0xFF && jpg[i + 1] != 0xDA)
        {
            int len = (jpg[i + 2] << 8) | jpg[i + 3];
            bool icc = jpg[i + 1] == 0xE2 && len > 14 && System.Text.Encoding.ASCII.GetString(jpg, i + 4, 11) == "ICC_PROFILE";
            if (icc) segments++; else o.AddRange(jpg.AsSpan(i, 2 + len).ToArray());
            i += 2 + len;
        }
        o.AddRange(jpg.AsSpan(i).ToArray());
        return o.ToArray();
    }

    [Fact]
    public void Jpeg_with_the_profile_differs_only_by_the_profile_and_reads_as_srgb()
    {
        int w = 96, h = 64;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int k = (y * w + x) * 4; px[k] = (byte)(x * 2); px[k + 1] = (byte)(y * 3); px[k + 2] = (byte)(x + y); px[k + 3] = 255; }
        byte[] Encode(byte[]? icc)
        {
            using var ms = new MemoryStream();
            Lux.Engine.Pipeline.Export.JpegEncoder.Encode(ms, px, w, h, w * 4, false, new Lux.Engine.Pipeline.Export.JpegEncoder
            {
                Quality = 98, SubsamplingId = 2, Comment = "Created with LibCP test", ExifApp1 = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 1, 2, 3, 4 }, IccProfile = icc,
            });
            return ms.ToArray();
        }
        var plain = Encode(null);
        var withIcc = Encode(Lux.Engine.Pipeline.Export.IccProfiles.Srgb);
        Assert.Equal(plain, WithoutIcc(withIcc, out int n));
        Assert.Equal(1, n);
        Assert.Equal(plain, WithoutIcc(plain, out int none));
        Assert.Equal(0, none);
        // decoders read it: the profile's color space is sRGB's (matrix and curve)
        using var data = SkiaSharp.SKData.CreateCopy(withIcc);
        using var codec = SkiaSharp.SKCodec.Create(data);
        var cs = codec.Info.ColorSpace;
        Assert.NotNull(cs);
        Assert.True(cs!.ToColorSpaceXyz(out var m));
        Assert.True(SkiaSharp.SKColorSpace.CreateSrgb().ToColorSpaceXyz(out var srgb));
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Assert.True(Math.Abs(m[c, r] - srgb[c, r]) < 2e-3, $"matrix [{r},{c}] {m[c, r]} vs {srgb[c, r]}");
        Assert.True(cs.GetNumericalTransferFunction(out var fn));
        _out.WriteLine($"transfer g {fn.G} a {fn.A} b {fn.B} c {fn.C} d {fn.D}; profile {Lux.Engine.Pipeline.Export.IccProfiles.Srgb.Length} B");
        Assert.InRange(fn.G, 2.3f, 2.5f);
        // Adobe RGB parses too (gamma 563/256)
        var adobe = SkiaSharp.SKColorSpace.CreateIcc(Lux.Engine.Pipeline.Export.IccProfiles.AdobeRgb);
        Assert.NotNull(adobe);
        Assert.True(adobe!.GetNumericalTransferFunction(out var afn));
        Assert.InRange(afn.G, 2.19f, 2.21f);
        Assert.Null(Lux.Engine.Pipeline.Export.IccProfiles.ForColorSpaceProperty(1));
        Assert.Same(Lux.Engine.Pipeline.Export.IccProfiles.Srgb, Lux.Engine.Pipeline.Export.IccProfiles.ForColorSpaceProperty(4));
    }

    [Fact]
    public async Task Real_export_with_the_option_off_is_unchanged_and_on_adds_only_the_profile()
    {
        string Original = Path.Combine(Paths.Captures, "L16_00540.lri");
        if (!File.Exists(Original)) { _out.WriteLine("no local capture: skipped"); return; }
        string root = Gaps.Fresh("icc");
        string photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        File.CreateSymbolicLink(Path.Combine(photos, "L16_00540.lri"), Original);
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(root, "lib", "library.luxlib"), WatchFolders = false, MakeThumbnailsAfterScan = false });
        lib.Settings.AnalyseNewCaptures = false;
        var folder = await lib.AddFolderAsync(photos);
        if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
        string id = (await lib.QueryAsync(new CaptureQuery { Limit = 5 })).Items.Single().Id;
        var sw = Stopwatch.StartNew();
        async Task<byte[]> Export(string dir, bool icc)
        {
            var job = await lib.Jobs.WaitAsync(lib.Export(new[] { id }, new ExportOptions { Formats = new[] { "jpg" }, Level = 3, OutputFolder = Path.Combine(root, dir), EmbedColorProfile = icc, UseCache = false }).Single());
            Assert.True(job.State == JobState.Done, job.Error);
            return File.ReadAllBytes(Path.Combine(root, dir, "L16_00540.jpg"));
        }
        var off = await Export("off", false);
        var off2 = await Export("off2", false);
        var on = await Export("on", true);
        _out.WriteLine($"three level-3 exports in {sw.Elapsed.TotalSeconds:0.0} s: {off.Length} / {on.Length} B");
        // the default file is the default file (the only bytes that may differ between two runs are the Exif time stamps)
        Assert.Equal(off.Length, off2.Length);
        int diff = off.Zip(off2).Count(p => p.First != p.Second);
        Assert.True(diff <= 4, $"{diff} bytes differ between two default exports");
        var stripped = WithoutIcc(on, out int n);
        Assert.Equal(1, n);
        Assert.Equal(off.Length, stripped.Length);
        Assert.True(off.Zip(stripped).Count(p => p.First != p.Second) <= 4, "with the profile removed, the file is the default one");
        Assert.Equal(off.Length + Lux.Engine.Pipeline.Export.IccProfiles.Srgb.Length + 18, on.Length);
        Gaps.Record("figures.txt", $"color profile: level-3 JPG {off.Length} B default, {on.Length} B with the sRGB profile (+{on.Length - off.Length} B = APP2), otherwise equal; no profile in the default file");
    }

    [AvaloniaFact]
    public async Task Export_panel_offers_the_profile_for_jpg_off_by_default()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 });
        var item = h.Library.Items.First(i => !i.Item.Missing && !i.Item.Unreadable);
        var p = new ExportPanelViewModel(h.Services, new[] { item }, h.Vm.Queue, () => { }, _ => { });
        Assert.False(p.EmbedColorProfile);
        Assert.True(p.JpgChosen);
        foreach (var f in p.Formats) f.IsOn = f.Key == "dng";
        Assert.False(p.JpgChosen);
        foreach (var f in p.Formats) f.IsOn = f.Key == "jpg";
        Assert.True(p.JpgChosen);
        p.EmbedColorProfile = true;
        Assert.Contains("color profile", p.Describe());
    }
}
