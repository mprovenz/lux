using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Mtp;
using Lux.Library;
using Lux.Library.Camera;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The Camera page and the camera service on the fake camera: states (not connected, reading the file list, reading details,
/// connected, busy), the New / All / Since filter, selection, the import as a Queue job with per-file progress, the copy into day folders
/// under <c>M5.Root</c>, the library scan and the analysis queued after it, a cable pulled in the middle of a copy and the import continued,
/// canceling, the already-imported marking, virtualization, the first-run card and the read-only guarantee. Screens: under <c>M5.Root</c>.
/// </summary>
public sealed class CameraTests
{
    readonly ITestOutputHelper _out;
    public CameraTests(ITestOutputHelper output) => _out = output;

    sealed record Rig(Harness H, FakeCamera Camera, CameraService Service, string Dest) : IAsyncDisposable
    {
        public CameraViewModel Vm => H.Vm.Camera;
        public CameraView View => H.Window.FindControl<CameraView>("CameraPage")!;
        public async ValueTask DisposeAsync() => await H.DisposeAsync();
        /// <summary>Wait until the visible camera thumbnails have arrived.</summary>
        public Task SettleThumbsAsync() => H.SettleAsync(() => Vm.Thumbnails.InFlight == 0, 8000);
    }

    static async Task<Rig> OpenAsync(string name, FakeCameraOptions? camera = null, bool plugged = true, bool connect = true, FakeLibraryOptions? library = null)
    {
        FakeCamera? fc = null; CameraService? svc = null;
        string dest = M5.Fresh(Path.Combine("ui", name));
        var h = await Harness.CreateAsync(library ?? new FakeLibraryOptions { SeedJobs = false, ThumbnailDelayMs = 0 }, camera: lib =>
        {
            fc = new FakeCamera((camera ?? new FakeCameraOptions()) with { Plugged = plugged }, lib);
            svc = new CameraService(lib, new FakeMtpBackend(fc), new CameraServiceOptions
            {
                StartPolling = false, PreviewFolder = Path.Combine(dest, "previews"), ShrinkPreview = CameraPreviews.Shrink,
                DefaultDestination = Path.Combine(dest, "Light"), ErrorRetry = TimeSpan.Zero,
                Log = line => M5.Record("ui-camera-log.txt", $"[{name}] {line}"),
            });
            return svc;
        });
        var rig = new Rig(h, fc!, svc!, Path.Combine(dest, "Light"));
        h.Vm.CurrentPage = Page.Camera;
        await h.SettleAsync();
        if (connect && plugged)
        {
            svc!.Poll();
            await h.SettleAsync(() => svc.Status.State == CameraState.Connected, 15_000);
            await rig.SettleThumbsAsync();
        }
        return rig;
    }

    string Shot(Rig r, string name) => Harness.Capture(r.H.Window, name, M5.Shots);

    // ------------------------------------------------------------------------------------------------ states

    [AvaloniaFact]
    public async Task States_from_no_camera_to_listing_to_connected()
    {
        await using var r = await OpenAsync("states", new FakeCameraOptions(), plugged: false);
        var vm = r.Vm;
        Assert.Equal(CameraState.NotConnected, vm.State);
        Assert.Equal("No camera connected", vm.StateTitle);
        Assert.True(vm.ShowConnect);
        Assert.Equal("No camera connected", r.H.Vm.StatusDevice);
        Shot(r, "m5-01-not-connected");

        // plugged in, the file list held (the camera sends it at once after ~17 s)
        r.Camera.FileListGate.Reset();
        r.Camera.HoldDetailsAfter = 48;
        r.Camera.DetailsGate.Reset();
        r.Camera.Plug();
        r.Service.Poll();
        await r.H.SettleAsync(() => vm.Status.Phase == CameraListingPhase.FileList, 5000);
        Assert.Equal(CameraState.Listing, vm.State);
        Assert.Contains("reading the camera's list of files", vm.StateLine);
        Assert.True(vm.ShowWaiting && vm.ListingIndeterminate);
        Assert.Equal("Light L16 connected · reading the camera", r.H.Vm.StatusDevice);
        Shot(r, "m5-02-listing-file-list");

        // the details: captures appear as they are read ("48 of 214")
        r.Camera.FileListGate.Set();
        await r.H.SettleAsync(() => vm.Status.Listed >= 48 && vm.Status.Phase == CameraListingPhase.Details, 5000);
        await Task.Delay(150);
        await r.H.SettleAsync(() => vm.Visible.Count > 0);
        Assert.Equal(214, vm.Status.ListTotal);
        Assert.Contains("reading photo details · 48 of 214", vm.StateLine);
        Assert.True(vm.ShowGrid);
        Assert.False(vm.ListingIndeterminate);
        Assert.Contains("(48 of 214)", r.H.Vm.StatusDevice);
        await r.SettleThumbsAsync();
        Shot(r, "m5-03-listing-details");

        r.Camera.DetailsGate.Set();
        await r.H.SettleAsync(() => vm.State == CameraState.Connected, 10_000);
        await r.SettleThumbsAsync();
        Assert.Equal(214, vm.Status.Captures);
        Assert.Equal(38, vm.Status.New);
        Assert.Equal("Light L16 · connected over USB", vm.StateTitle);
        Assert.Equal("LFCL0000DEMO0001 · 214 captures on the camera · 38 not yet imported · battery 80 %", vm.StateLine);
        Assert.Equal("Light L16 connected · 38 new", r.H.Vm.StatusDevice);
        Assert.Equal("New · 38", vm.NewLabel);
        Assert.Equal("All · 214", vm.AllLabel);
        // nothing starts selected
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanImport);
        Assert.StartsWith("Free space in the destination: ", r.H.Vm.StatusCache);
        Shot(r, "m5-04-connected-new");
        // "Select all" takes every new capture
        vm.SelectAllCommand.Execute(null);
        await vm.UpdatePlanAsync();
        await r.H.SettleAsync();
        Assert.Equal(38, vm.SelectedCount);
        Assert.Equal("38 of 38 new captures selected", r.H.Vm.StatusLeft);
        Assert.Equal("Import 38 captures", vm.ImportButtonText);
        Assert.True(vm.CanImport);

        // the design's state: 14 of them selected
        var keep = vm.Visible.Take(14).ToList();
        vm.SelectAllCommand.Execute(null);   // all → none
        Assert.Equal(0, vm.SelectedCount);
        foreach (var k in keep) vm.Toggle(k, false);
        await vm.UpdatePlanAsync();
        await r.H.SettleAsync();
        Assert.Equal("14 of 38 new captures selected", r.H.Vm.StatusLeft);
        Assert.StartsWith("Import 14 selected · ", vm.ImportTitle);
        Shot(r, "m5-04b-connected-14-selected");

        // the status bar on the Library page says it too
        r.H.Vm.CurrentPage = Page.Library;
        await r.H.SettleAsync();
        Assert.Equal("Light L16 connected · 38 new", r.H.Vm.StatusDevice);
        Shot(r, "m5-14-library-status-bar-camera");
    }

    [AvaloniaFact]
    public async Task Busy_camera_says_so_and_connects_when_free()
    {
        await using var r = await OpenAsync("busy", connect: false);
        r.Camera.Busy = true;
        r.Service.Poll();
        await r.H.SettleAsync(() => r.Vm.State == CameraState.Busy, 5000);
        Assert.Equal("The camera is in use by another program", r.Vm.StateTitle);
        Assert.Contains("file manager", r.Vm.Status.Message);
        Assert.Equal("Lux tries again every few seconds.", r.Vm.StateLine);
        Assert.True(r.Vm.ShowProblemCard && r.Vm.CanRetry);
        Assert.Equal("Camera in use by another program", r.H.Vm.StatusDevice);
        Shot(r, "m5-11-busy");
        // still held: trying again keeps the message; freed: it connects
        r.Vm.RetryCommand.Execute(null);
        await r.H.SettleAsync(() => r.Camera.Opens == 0 && r.Vm.State == CameraState.Busy, 3000);
        r.Camera.Busy = false;
        r.Vm.RetryCommand.Execute(null);
        await r.H.SettleAsync(() => r.Vm.State == CameraState.Connected, 10_000);
        Assert.Equal(214, r.Vm.Status.Captures);
    }

    // ------------------------------------------------------------------------------------------------ filter, marking, selection

    [AvaloniaFact]
    public async Task Filter_new_all_since_and_the_already_imported_marking()
    {
        await using var r = await OpenAsync("filter");
        var vm = r.Vm;
        Assert.All(vm.Visible, i => Assert.False(i.InLibrary));
        Assert.Equal(38, vm.Visible.Count);
        // calibration files and other files are never listed
        Assert.DoesNotContain(r.Service.Captures, c => c.Folder.Contains("lightcal") || !c.FileName.EndsWith(".lri"));

        vm.SetFilterCommand.Execute(CameraFilter.All);
        await r.H.SettleAsync();
        await r.SettleThumbsAsync();
        Assert.Equal(214, vm.Visible.Count);
        Assert.Equal(176, vm.Visible.Count(i => i.InLibrary));
        var imported = vm.Visible.First(i => i.InLibrary);
        var libItem = r.H.Lib.Get(imported.Capture.CaptureId!)!;
        Assert.Equal(imported.Name, libItem.Name);
        Assert.Equal(imported.Capture.Size, libItem.FileSize);
        // days newest first; the header says how many are new
        var headers = vm.Rows.OfType<CameraDayHeaderViewModel>().ToList();
        Assert.Equal("12 October 2026", headers[0].Title);
        Assert.Contains("16 new", headers[0].Sub);
        Assert.Equal("0 of 214 captures selected", r.H.Vm.StatusLeft);
        Shot(r, "m5-05-all-with-imported-badges");

        vm.SetFilterCommand.Execute(CameraFilter.Since);
        await r.H.SettleAsync();
        Assert.True(vm.SinceChoices.Count > 3);
        vm.SinceDay = vm.SinceChoices.First(d => d.Day == new DateOnly(2026, 10, 11));
        await r.H.SettleAsync();
        await r.SettleThumbsAsync();
        Assert.All(vm.Visible, i => Assert.True(i.Capture.Day >= new DateOnly(2026, 10, 11)));
        int expected = r.Service.Captures.Count(c => c.Day >= new DateOnly(2026, 10, 11));
        Assert.Equal(expected, vm.Visible.Count);
        Assert.StartsWith("Since 11 Oct · ", vm.SinceLabel);
        Shot(r, "m5-06-since-a-date");
    }

    [AvaloniaFact]
    public async Task Selection_by_click_shift_day_and_all()
    {
        await using var r = await OpenAsync("selection");
        var vm = r.Vm;
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Select all", vm.SelectAllLabel);
        Assert.False(vm.CanImport);
        // a click on a cell (through the view)
        var first = vm.Visible[0];
        await r.H.SettleAsync();
        var cell = r.View.CellOf(first);
        Assert.NotNull(cell);
        var p = cell!.TranslatePoint(new Avalonia.Point(cell.Bounds.Width / 2, cell.Bounds.Height / 3), r.H.Window)!.Value;
        r.H.Window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        r.H.Window.MouseUp(p, Avalonia.Input.MouseButton.Left);
        await r.H.SettleAsync();
        Assert.True(first.IsSelected);
        Assert.Equal(1, vm.SelectedCount);
        // shift-click: a range
        vm.Toggle(vm.Visible[5], shift: true);
        Assert.Equal(6, vm.SelectedCount);
        // a day: "Select day" then "Clear day"
        var day = vm.Rows.OfType<CameraDayHeaderViewModel>().Last();
        day.SelectDayCommand.Execute(null);
        await r.H.SettleAsync();
        day = vm.Rows.OfType<CameraDayHeaderViewModel>().Last();
        Assert.Equal("Clear day", day.SelectLabel);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(38, vm.SelectedCount);
        Assert.Equal("Select none", vm.SelectAllLabel);
        await vm.UpdatePlanAsync();
        Assert.True(vm.CanImport);
        Assert.NotNull(vm.Plan);
        Assert.Equal(vm.SelectedBytes, vm.Plan!.Bytes);
        Assert.Equal(2, vm.Plan.Folders.Count);   // two capture days
    }

    // ------------------------------------------------------------------------------------------------ the import

    async Task<JobInfo> RunImportAsync(Rig r, int timeoutMs = 30_000)
    {
        var job = r.H.Lib.Jobs.GetJobs().Where(j => j.Kind == JobKind.Import).OrderByDescending(j => j.Id).First();
        r.H.Lib.FakeJobs.Tick(0.1);   // the fake queue starts it; the camera service runs it
        var sw = Stopwatch.StartNew();
        while (!(r.H.Lib.Jobs.Get(job.Id)?.IsFinished ?? true) && sw.ElapsedMilliseconds < timeoutMs)
        {
            await r.H.SettleAsync(timeoutMs: 200);
            await Task.Delay(20);
        }
        return r.H.Lib.Jobs.Get(job.Id)!;
    }

    /// <summary>Let the fake queue's folder scan finish and the captures and analysis follow.</summary>
    async Task FinishScanAsync(Rig r, Func<bool> until)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000 && !until())
        {
            r.H.Lib.FakeJobs.Tick(1.0);
            await r.H.SettleAsync(timeoutMs: 300);
            await Task.Delay(50);
        }
    }

    [AvaloniaFact]
    public async Task Import_copies_into_day_folders_as_a_queue_job_then_library_and_analysis()
    {
        await using var r = await OpenAsync("import", new FakeCameraOptions { SmallFiles = true, BytesPerSecond = 2_000_000 });
        var vm = r.Vm;
        vm.Destination = r.Dest;
        vm.OneFolderPerDay = true;
        vm.AfterIndex = 0;
        // twelve of them: six from each day
        var pick = vm.Visible.Where(i => i.Capture.Day == new DateOnly(2026, 10, 12)).Take(6)
                     .Concat(vm.Visible.Where(i => i.Capture.Day == new DateOnly(2026, 10, 11)).Take(6)).ToList();
        foreach (var i in pick) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        Assert.True(vm.CanImport, vm.Problem);
        Assert.Equal("Import 12 captures", vm.ImportButtonText);
        Assert.Equal(2, vm.Plan!.Folders.Count);
        Assert.Contains("2 day folders", vm.FolderPreview);
        vm.ImportCommand.Execute(null);
        await r.H.SettleAsync();
        var job = r.H.Lib.Jobs.GetJobs().Single(j => j.Kind == JobKind.Import);
        Assert.Equal("Importing 12 photos from the camera", job.Title);
        Assert.Equal($"{pick.Min(i => i.Name)} – {pick.Max(i => i.Name)}", job.Subject);
        Assert.Contains("12 photos", job.Detail);
        Assert.Contains("a folder per day", job.Detail);
        Assert.True(vm.IsImporting);
        Assert.Equal("Importing…", vm.ImportButtonText);

        // running: per-file progress in the panel and the Queue
        r.H.Lib.FakeJobs.Tick(0.1);
        await r.H.SettleAsync(() => r.H.Lib.Jobs.Get(job.Id) is { State: JobState.Running, Progress: > 0.1 }, 10_000, waitThumbnails: false);
        var running = r.H.Lib.Jobs.Get(job.Id)!;
        Assert.StartsWith("Copying L16_", running.Status);
        Assert.Equal(12, running.ItemsTotal);
        await r.H.SettleAsync(waitThumbnails: false, timeoutMs: 300);
        Assert.Contains(" of 12 · ", vm.ImportProgressText);
        Shot(r, "m5-07-importing");
        r.H.Vm.CurrentPage = Page.Queue;
        await r.H.SettleAsync(waitThumbnails: false, timeoutMs: 400);
        var row = r.H.Vm.Queue.Jobs.Single(j => j.Info.Kind == JobKind.Import);
        Assert.Equal(job.Subject, row.Name);
        Shot(r, "m5-08-queue-import-job");
        r.H.Vm.CurrentPage = Page.Camera;

        var done = await RunImportAsync(r);
        Assert.Equal(JobState.Done, done.State);
        Assert.Equal(12, done.ResultFiles.Count);
        Assert.Contains("12 photos copied", done.Notes);
        // the files: in yyyy-MM-dd folders, byte for byte the camera's, no partial files, the camera's time kept
        foreach (var i in pick)
        {
            string path = Path.Combine(r.Dest, i.Capture.Day!.Value.ToString("yyyy-MM-dd"), i.Capture.FileName);
            Assert.True(File.Exists(path), path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(i.Capture.Size, bytes.Length);
            var f = r.Camera.Files.First(x => x.Name == i.Capture.FileName);
            for (int k = 0; k < bytes.Length; k += 997) Assert.Equal(FakeCamera.ByteAt(f.Scene, k), bytes[k]);
            Assert.Equal(i.Capture.Modified!.Value.UtcDateTime, File.GetLastWriteTimeUtc(path), TimeSpan.FromSeconds(2));
        }
        Assert.Empty(Directory.EnumerateFiles(r.Dest, "*.part", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.GetDirectories(r.Dest).Length);

        // the destination is added to the library and scanned; the captures appear, analysis is queued for each; they stop being "new"
        await FinishScanAsync(r, () => r.H.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Analysis && !j.IsFinished) >= 12);
        Assert.Contains(r.H.Lib.GetFolders(), f => f.Path == r.Dest);
        var ids = r.H.Lib.FindByPaths(done.ResultFiles);
        Assert.Equal(12, ids.Count);
        var analysis = r.H.Lib.Jobs.GetJobs().Where(j => j.Kind == JobKind.Analysis).Select(j => j.CaptureId).ToHashSet();
        _out.WriteLine($"analysis jobs: {analysis.Count}; imported ids: {ids.Count}; missing: {string.Join(", ", ids.Values.Where(v => !analysis.Contains(v)).Select(v => v[..8]))}");
        Assert.True(ids.Values.All(analysis.Contains), "analysis queued for every imported capture");
        await r.H.SettleAsync(() => r.Service.Status.New == 26, 5000, waitThumbnails: false);
        Assert.Equal(26, r.Service.Status.New);
        Assert.Equal("Light L16 connected · 26 new", r.H.Vm.StatusDevice);
        await r.H.SettleAsync(() => vm.LastImportAnalysis == "0 of 12", 5000, waitThumbnails: false);
        Assert.Equal("Complete", vm.LastImportState);
        Assert.Equal("12 of 12", vm.LastImportCopied);
        Assert.Equal("0 of 12", vm.LastImportAnalysis);
        Assert.Equal(0, vm.SelectedCount);   // the imported ones left the selection (the rest were cleared before)
        await r.SettleThumbsAsync();
        Shot(r, "m5-09-after-import-last-import");

        // the same twelve again (All filter): already there with the same size, skipped
        vm.SetFilterCommand.Execute(CameraFilter.All);
        await r.H.SettleAsync(waitThumbnails: false);
        foreach (var i in vm.Visible.Where(v => pick.Any(p => p.Key == v.Key))) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        Assert.Equal(12, vm.Plan!.AlreadyThere);
        Assert.Equal("12 are already in the folder and are skipped.", vm.PlanNote);
        vm.ImportCommand.Execute(null);
        var again = await RunImportAsync(r);
        Assert.Equal(JobState.Done, again.State);
        Assert.Contains("12 photos already there", again.Notes);
        Assert.Equal(12, r.Camera.CaptureReads);   // nothing was read twice
    }

    [AvaloniaFact]
    public async Task Unplugged_mid_transfer_stops_keeps_finished_files_removes_partial_and_continues()
    {
        await using var r = await OpenAsync("unplug", new FakeCameraOptions { SmallFiles = true, BytesPerSecond = 400_000 });
        var vm = r.Vm;
        vm.Destination = r.Dest;
        vm.OneFolderPerDay = false;
        vm.AfterIndex = 2;   // only copy
        var pick = vm.Visible.Take(6).ToList();
        foreach (var i in pick) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        // the cable comes out half-way through the fourth file
        var ordered = pick.Select(p => p.Capture).OrderBy(c => c.Modified).ThenBy(c => c.FileName).ToList();
        r.Camera.ResetReadCount();
        r.Camera.UnplugAfterBytes = ordered.Take(3).Sum(c => c.Size) + ordered[3].Size / 2;
        vm.ImportCommand.Execute(null);
        var job = await RunImportAsync(r);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("The camera was disconnected during the copy. 3 photos of 6 are copied and kept", job.Error);
        Assert.Equal(3, Directory.GetFiles(r.Dest, "*.lri").Length);
        Assert.Empty(Directory.GetFiles(r.Dest, "*.part"));
        foreach (var c in ordered.Take(3)) Assert.Equal(c.Size, new FileInfo(Path.Combine(r.Dest, c.FileName)).Length);
        await r.H.SettleAsync(() => vm.State == CameraState.NotConnected, 5000, waitThumbnails: false);
        Assert.Equal("No camera connected", r.H.Vm.StatusDevice);
        Assert.Equal("", r.H.Vm.StatusLeft);
        Assert.Equal("Stopped", vm.LastImportState);
        // polling resumed after the copy stopped
        int polls = r.Camera.AutodetectCalls;
        r.Service.Poll();
        await r.H.SettleAsync(() => r.Camera.AutodetectCalls > polls, 3000, waitThumbnails: false);
        Assert.True(r.Camera.AutodetectCalls > polls);
        Assert.True(vm.HasLastImportProblem);
        Assert.False(vm.CanContinue);   // not while the camera is away
        Shot(r, "m5-10-unplugged-mid-transfer");

        // plugged in again: the listing comes back (the cached one at once), the import is continued and copies the other three
        r.Camera.Plug();
        r.Service.Poll();
        await r.H.SettleAsync(() => vm.State == CameraState.Connected, 10_000, waitThumbnails: false);
        Assert.True(vm.CanContinue);
        int readsBefore = r.Camera.CaptureReads;
        vm.ContinueImportCommand.Execute(null);
        await r.H.SettleAsync(() => r.H.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Import) == 2, 5000, waitThumbnails: false);
        var cont = await RunImportAsync(r);
        Assert.Equal(JobState.Done, cont.State);
        Assert.Equal(6, Directory.GetFiles(r.Dest, "*.lri").Length);
        Assert.Equal(3, r.Camera.CaptureReads - readsBefore);
        Assert.Contains("3 photos already there", cont.Notes);
        Assert.Contains("3 photos copied", cont.Notes);
    }

    [AvaloniaFact]
    public async Task Cancelling_an_import_removes_the_partial_file_and_keeps_the_rest()
    {
        await using var r = await OpenAsync("cancel", new FakeCameraOptions { SmallFiles = true, BytesPerSecond = 60_000 });
        var vm = r.Vm;
        vm.Destination = r.Dest;
        vm.OneFolderPerDay = false;
        foreach (var i in vm.Visible.Take(5)) vm.Toggle(i, false);
        await vm.UpdatePlanAsync();
        vm.ImportCommand.Execute(null);
        await r.H.SettleAsync(waitThumbnails: false);
        var job = r.H.Lib.Jobs.GetJobs().Single(j => j.Kind == JobKind.Import);
        r.H.Lib.FakeJobs.Tick(0.1);
        await r.H.SettleAsync(() => r.H.Lib.Jobs.Get(job.Id)!.ItemsDone >= 1 && Directory.GetFiles(r.Dest, "*.part").Length == 1, 15_000, waitThumbnails: false);
        // the bus is not polled while files are copied
        int polls = r.Camera.AutodetectCalls;
        for (int k = 0; k < 5; k++) { r.Service.Poll(); await Task.Delay(20); }
        Assert.Equal(polls, r.Camera.AutodetectCalls);
        vm.CancelImportCommand.Execute(null);
        var end = await RunImportAsync(r);
        Assert.Equal(JobState.Cancelled, end.State);
        Assert.Empty(Directory.GetFiles(r.Dest, "*.part"));
        int kept = Directory.GetFiles(r.Dest, "*.lri").Length;
        Assert.InRange(kept, 1, 4);
        Assert.Contains(end.Notes, n => n.Contains("before it was cancelled"));
        await r.H.SettleAsync(() => vm.LastImportState == "Canceled", 3000, waitThumbnails: false);
    }

    [AvaloniaFact]
    public async Task Problems_are_said_before_import_folder_on_the_camera_and_no_space()
    {
        await using var r = await OpenAsync("problems");
        var vm = r.Vm;
        vm.SelectAllCommand.Execute(null);   // nothing starts selected; the checks need an import to plan
        vm.Destination = "/run/user/1000/gvfs/gphoto2:host=Light_L16/DCIM";
        await vm.UpdatePlanAsync();
        Assert.Equal("That folder is on the camera itself. Choose a folder on this computer.", vm.Problem);
        Assert.False(vm.CanImport);
        vm.Destination = "";
        await vm.UpdatePlanAsync();
        Assert.Equal("Choose a folder to copy to.", vm.Problem);
        // more than the drive holds: a capture of 400 TB
        r.Camera.Add(new FakeCameraFile(FakeCamera.CameraFolder, "L16_09999.lri", 400L << 40, new DateTimeOffset(new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Local)), 1500, true, false));
        r.Service.Reconnect();
        await r.H.SettleAsync(() => r.Service.Status.State == CameraState.Connected && r.Service.Captures.Count == 215, 10_000, waitThumbnails: false);
        await r.H.SettleAsync(() => vm.Visible.Any(i => i.Name == "L16_09999"), 10_000, waitThumbnails: false);
        var huge = vm.Visible.First(i => i.Name == "L16_09999");
        if (!huge.IsSelected) vm.Toggle(huge, false);
        vm.Destination = r.Dest;
        await vm.UpdatePlanAsync();
        Assert.StartsWith("Not enough free space in ", vm.Problem);
        Assert.Contains("the files need ", vm.Problem);
        Assert.False(vm.CanImport);
        await r.SettleThumbsAsync();
        Shot(r, "m5-13-not-enough-space");
    }

    // ------------------------------------------------------------------------------------------------ virtualization, first run, read-only

    [AvaloniaFact]
    public async Task Six_hundred_captures_stay_virtualised_and_previews_are_read_only_for_what_is_on_screen()
    {
        await using var r = await OpenAsync("virtual", new FakeCameraOptions { Imported = 0, New = 600, SmallFiles = true });
        var vm = r.Vm;
        Assert.Equal(600, vm.Visible.Count);
        await r.SettleThumbsAsync();
        int live = CameraThumbnailView.Live, reads = r.Camera.PreviewReads;
        _out.WriteLine($"on screen: {live} views, {reads} previews read");
        Assert.InRange(live, 8, 120);
        // while the list arrives (in name order, shown by day) cells briefly show other captures: a few previews more than the cells at rest
        Assert.True(reads <= 2 * live + 24, $"{reads} previews read for {live} views");
        var scroll = r.View.FindControl<ScrollViewer>("CameraScroll")!;
        var sw = Stopwatch.StartNew();
        int maxLive = 0;
        for (int k = 0; k < 20; k++)
        {
            scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + 600);
            await r.H.SettleAsync(timeoutMs: 300, waitThumbnails: false);
            maxLive = Math.Max(maxLive, CameraThumbnailView.Live);
        }
        _out.WriteLine($"scrolled 20 screens in {sw.ElapsedMilliseconds} ms; at most {maxLive} views; {r.Camera.PreviewReads} previews read");
        Assert.InRange(maxLive, 8, 140);
        Assert.True(r.Camera.PreviewReads < 600, "not every preview was read");
        M5.Record("ui-figures.txt", $"virtualization: 600 captures, {live} views realized at rest, at most {maxLive} while scrolling 20 screens, {r.Camera.PreviewReads} previews read");
    }

    [AvaloniaFact]
    public async Task First_run_card_opens_the_camera_page()
    {
        await using var r = await OpenAsync("firstrun", connect: false, plugged: false, library: new FakeLibraryOptions { Empty = true, SeedJobs = false });
        r.H.Vm.CurrentPage = Page.Library;
        await r.H.SettleAsync();
        Assert.True(r.H.Vm.ShowEmpty);
        var button = r.H.Window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Open Camera");
        Shot(r, "m5-12-first-run-card");
        button.Command!.Execute(null);
        await r.H.SettleAsync();
        Assert.True(r.H.Vm.IsCamera);
        Assert.True(r.Vm.ShowConnect);
    }

    [Fact]
    public void The_camera_code_offers_no_way_to_change_the_camera()
    {
        string[] words = { "Delete", "Upload", "Put", "Rename", "Remove", "Move", "Format", "Write", "Set" };
        foreach (var t in new[] { typeof(IMtpBackend), typeof(IMtpDevice), typeof(ICameraService) })
            foreach (var m in t.GetMethods())
                Assert.False(words.Any(w => m.Name.StartsWith(w, StringComparison.Ordinal)), $"{t.Name}.{m.Name}");
        // the libgphoto2 bindings: only reading calls (no gp_camera_file_delete / put / folder_delete / make_dir / set_config …)
        var gp = typeof(IMtpBackend).Assembly.GetType("Lux.Engine.Mtp.GPhoto2.Gp")!;
        var natives = gp.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name.StartsWith("gp_", StringComparison.Ordinal)).Select(m => m.Name).ToList();
        Assert.NotEmpty(natives);
        foreach (var n in natives)
            Assert.False(n.Contains("delete") || n.Contains("put") || n.Contains("upload") || n.Contains("make_dir") || n.Contains("remove_dir") || n.Contains("set_config")
                         || n.Contains("set_info") || n.Contains("capture") || n.Contains("trigger"), n);
    }

    [Fact]
    public async Task Previews_are_kept_in_the_app_data_folder_and_read_once()
    {
        var root = M5.Fresh("svc/previews");
        using var lib = new FakeLibrary(new FakeLibraryOptions { SeedJobs = false, AutoTick = false });
        var fc = new FakeCamera(new FakeCameraOptions(), lib);
        using var svc = new CameraService(lib, new FakeMtpBackend(fc), new CameraServiceOptions { StartPolling = false, PreviewFolder = Path.Combine(root, "previews"), ShrinkPreview = CameraPreviews.Shrink });
        svc.Poll();
        var sw = Stopwatch.StartNew();
        while (svc.Status.State != CameraState.Connected && sw.ElapsedMilliseconds < 10_000) await Task.Delay(20);
        Assert.Equal(CameraState.Connected, svc.Status.State);
        var c = svc.Captures.First(x => !x.InLibrary);
        var other = svc.Captures.Where(x => !x.InLibrary).Skip(1).First();
        var a = await svc.GetPreviewAsync(c);
        Assert.NotNull(a);
        Assert.Equal(1, fc.PreviewReads);
        var b = await svc.GetPreviewAsync(c);
        Assert.Equal(a, b);
        Assert.Equal(1, fc.PreviewReads);
        var files = Directory.GetFiles(Path.Combine(root, "previews"), "*.jpg", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.Contains(c.Name, Path.GetFileName(files[0]));
        using (var bmp = SkiaSharp.SKBitmap.Decode(files[0])) Assert.Equal(Math.Min(CameraPreviews.Width, FakeThumbnails.Width), bmp.Width);
        // unplugged: a kept preview is still there; one never read is not
        fc.Unplug();
        svc.Poll();
        sw.Restart();
        while (svc.Status.State != CameraState.NotConnected && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.NotNull(await svc.GetPreviewAsync(c));
        Assert.Equal(CameraState.NotConnected, svc.Status.State);
        Assert.Null(await svc.GetPreviewAsync(other));
    }
}

