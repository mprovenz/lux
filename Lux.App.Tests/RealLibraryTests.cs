using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The same screens on the real library (Lux.Library), with real thumbnails. Runs only when LUX_REAL_LIBRARY names a folder of captures
/// (a linked test library); the library database and the app's data go to a folder in the first UI output folder
/// (LUX_REAL_APPDATA overrides) — never the per-user folder.
/// </summary>
public sealed class RealLibraryTests
{
    readonly ITestOutputHelper _out;
    public RealLibraryTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public async Task Real_library_screens()
    {
        // one or more folders, separated by ';' (the first is the main test library)
        var folders = (Environment.GetEnvironmentVariable("LUX_REAL_LIBRARY") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                      .Select(Path.GetFullPath).Where(Directory.Exists).ToList();
        if (folders.Count == 0) { _out.WriteLine("LUX_REAL_LIBRARY not set: skipped"); return; }
        var folder = folders[0];
        var appData = Environment.GetEnvironmentVariable("LUX_REAL_APPDATA") is { Length: > 0 } a ? a : Paths.AppData("real");
        Directory.CreateDirectory(appData);
        var platform = new TestPlatform();
        bool existed = File.Exists(Path.Combine(appData, "library.luxlib"));
        var sw = Stopwatch.StartNew();
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, platform, log: m => { });
        Assert.False(s.IsFake);
        Assert.StartsWith(Path.GetFullPath(appData), s.Library.DatabasePath);
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        try
        {
            await vm.InitializeAsync();
            foreach (var f in folders)
                if (!s.Library.GetFolders().Any(x => string.Equals(x.Path.TrimEnd('/'), f.TrimEnd('/'), StringComparison.Ordinal)))
                    await s.Library.AddFolderAsync(f);
            _out.WriteLine($"opened in {sw.ElapsedMilliseconds} ms; db {s.Library.DatabasePath}; existing database {existed}");
            // a library that never set it picks up the library's default: photos shown as held
            _out.WriteLine($"show photos as held: {s.Library.Settings.ShowPhotosAsHeld}");
            Assert.True(s.Library.Settings.ShowPhotosAsHeld);

            async Task Settle(Func<bool> until, int timeoutMs)
            {
                var t = Stopwatch.StartNew();
                int quiet = 0;
                while (t.ElapsedMilliseconds < timeoutMs)
                {
                    Dispatcher.UIThread.RunJobs();
                    win.RenderFrame();
                    quiet = until() ? quiet + 1 : 0;
                    if (quiet >= 3) return;
                    await Task.Delay(100);
                }
                _out.WriteLine($"settle timed out after {timeoutMs} ms");
            }

            // scan
            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 600_000);
            _out.WriteLine($"scanned: {vm.Library.TotalCount} captures after {sw.Elapsed.TotalSeconds:0} s");
            await vm.Library.RefreshTreeAsync();
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");   // start from all captures, whatever was open last
            await Settle(() => !vm.Library.IsLoading, 30_000);

            // the visible thumbnails (made on demand, on screen first)
            await Settle(() => s.Thumbnails.InFlight == 0 && s.Thumbnails.Delivered > 0, 900_000);
            _out.WriteLine($"thumbnails delivered {s.Thumbnails.Delivered}, requested {s.Thumbnails.Requested}, after {sw.Elapsed.TotalSeconds:0} s");

            var items = vm.Library.Items;
            Assert.NotEmpty(items);
            vm.Library.Click(items[0], false, false);
            if (items.Count > 2) vm.Library.Click(items[2], true, false);
            if (items.Count > 5) vm.Library.Click(items[5], true, false);
            vm.Library.Click(items[0], true, false);
            vm.Library.Click(items[0], true, false);
            await Settle(() => s.Thumbnails.InFlight == 0, 120_000);
            Harness.Capture(win, "real-01-library-grid");

            Harness.Capture(win, "real-24-folder-tree-levels");

            // the selection bar's upgrade: the shared notice for the whole selection (cancelled — nothing is requested)
            await vm.Library.UpgradeSelectedCommand.ExecuteAsync(null);
            if (vm.Dialog is StorageNoticeViewModel notice)
            {
                _out.WriteLine($"selection notice: {notice.Title} — {notice.Text}");
                await Settle(() => true, 1_000);
                Harness.Capture(win, "real-26-selection-upgrade-notice");
                notice.CancelCommand.Execute(null);
            }
            Assert.Null(vm.Dialog);

            vm.Library.Mode = BrowseMode.Timeline;
            await Settle(() => s.Thumbnails.InFlight == 0, 600_000);
            Harness.Capture(win, "real-02-library-timeline");

            vm.Library.Mode = BrowseMode.List;
            await Settle(() => s.Thumbnails.InFlight == 0, 600_000);
            Harness.Capture(win, "real-03-library-list");

            vm.Library.Mode = BrowseMode.Grid;
            vm.Library.OpenExport();
            await Settle(() => s.Thumbnails.InFlight == 0, 120_000);
            Harness.Capture(win, "real-05-export-panel");
            vm.Library.CloseExport();

            vm.CurrentPage = Page.Queue;
            await Settle(() => true, 5_000);
            Harness.Capture(win, "real-06-queue");

            vm.CurrentPage = Page.Library;
            vm.OpenLumenImport(new CaptureQuery { PathPrefix = folder }, folder);
            var dlg = (LumenImportViewModel)vm.Dialog!;
            await Settle(() => !dlg.IsScanning, 300_000);
            _out.WriteLine($"lumen sessions: {dlg.Groups.Count} groups, {dlg.Total} sessions: " + string.Join(", ", dlg.Groups.Select(g => $"{g.Name} {g.Count}")));
            Harness.Capture(win, "real-07-lumen-import");
            dlg.CancelCommand.Execute(null);

            // quality selector and its storage notice (nothing is requested: the notice is cancelled)
            vm.Library.Click(items[0], false, false);
            await Settle(() => s.Thumbnails.InFlight == 0, 60_000);
            Harness.Capture(win, "real-19-quality-selector");
            var q = vm.Library.Details.Quality;
            var up = q.IsPreview ? ViewQuality.Linear13 : ViewQuality.FullDetail;
            await q.ChooseCommand.ExecuteAsync(up);
            await Settle(() => true, 2_000);
            _out.WriteLine($"quality: best {items[0].Item.BestQuality}, ask {q.AskOpen}: {q.AskText}");
            Harness.Capture(win, "real-20-quality-upgrade-notice");
            q.CancelAskCommand.Execute(null);

            // a read-only folder: a rating goes to the local store (in this run's app data) and the capture shows the marker
            if (folders.Count > 1)
            {
                var ro = folders[1];
                var roNode = vm.Library.Tree.FirstOrDefault(n => n.Path != null && n.Path.TrimEnd('/') == ro.TrimEnd('/'))
                             ?? vm.Library.Tree.FirstOrDefault(n => n.Kind == TreeNodeKind.Folder && n.Path != null && n.Path.StartsWith(ro, StringComparison.Ordinal));
                if (roNode != null)
                {
                    vm.Library.SelectedNode = roNode;
                    await Settle(() => !vm.Library.IsLoading && vm.Library.TotalCount > 0, 60_000);
                    // only a capture found in the read-only folder alone: its rating can go nowhere but this run's local store
                    // and only when that folder is not writable and holds no LuxImageContext: nothing can be written beside it
                    static bool Locked(string file)
                    {
                        var dir = Path.GetDirectoryName(file)!;
                        return !Directory.Exists(Path.Combine(dir, "LuxImageContext"))
                               && !File.GetUnixFileMode(dir).HasFlag(UnixFileMode.UserWrite);
                    }
                    var target = vm.Library.Items.FirstOrDefault(i => !i.Missing && !i.Unreadable && i.Item.LocationCount == 1
                                                                      && i.Item.Path.StartsWith(ro, StringComparison.Ordinal) && Locked(i.Item.Path));
                    if (target == null) { _out.WriteLine("read-only folder: no capture found there alone; not rated"); goto settings; }
                    vm.Library.Click(target, false, false);
                    vm.Library.SetRating(4);
                    await Settle(() => s.Library.Get(target.Id) is { Rating: 4 } c && c.ContextLocation != ContextLocation.None, 60_000);
                    var after = s.Library.Get(target.Id)!;
                    _out.WriteLine($"read-only folder: {target.Name} rating {after.Rating}, saved {after.ContextLocation}, not saved with photo {after.RatingNotSavedWithPhoto}");
                    await Settle(() => s.Thumbnails.InFlight == 0, 120_000);
                    Harness.Capture(win, "real-22-context-markers");
                }
            }

            settings:
            vm.CurrentPage = Page.Settings;
            await Settle(() => vm.Settings?.Folders.Count > 0 && vm.Settings.LocalCountText.Length > 0, 10_000);
            Harness.Capture(win, "real-08-settings");
            var page = win.GetVisualDescendants().OfType<SettingsView>().First();
            page.ShowSection("limits");
            await Settle(() => true, 1_000);
            Harness.Capture(win, "real-16-settings-limits");
            page.ShowSection("storage");
            await Settle(() => true, 1_000);
            Harness.Capture(win, "real-17-settings-storage");
            _out.WriteLine($"limits: {vm.Settings!.LimitRows.Count} descriptors in {vm.Settings.LimitGroups.Count} groups; local store {vm.Settings.LocalFolder}: {vm.Settings.LocalCountText}");
            _out.WriteLine($"done after {sw.Elapsed.TotalSeconds:0} s; stats: {vm.StatusLeft} | {vm.StatusCache}");
        }
        finally
        {
            win.SaveWindowState();
            win.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.DisposeAsync();
        }
    }
}
