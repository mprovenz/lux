using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// A library folder that is not there when the app starts (a drive that is not connected) and comes back while the app runs. The tests
/// use the real library and real captures: they link a few captures from <c>LUX_TEST_CAPTURES</c> into subfolders, scan them, close the
/// app, rename the folder away, start the app again, rename the folder back and select its subfolders in the tree. Without
/// <c>LUX_TEST_CAPTURES</c> they do nothing.
/// </summary>
public sealed class FolderReturnsTests
{
    readonly ITestOutputHelper _out;
    public FolderReturnsTests(ITestOutputHelper output) => _out = output;

    /// <summary>The folder was not scanned while it was away: its photos are not marked missing, and their thumbnails are made when the
    /// user selects the subfolders.</summary>
    [AvaloniaFact]
    public Task Folder_that_comes_back_works_without_restart() => Run("folder-returns", scanWhileAway: false);

    /// <summary>The folder was scanned while it was away, so its photos are marked missing. When it comes back, a scan starts by itself and
    /// the photos are no longer missing.</summary>
    [AvaloniaFact]
    public Task Photos_marked_missing_come_back_with_the_folder() => Run("folder-returns-missing", scanWhileAway: true);

    async Task Run(string name, bool scanWhileAway)
    {
        var captures = Directory.Exists(Paths.Captures)
            ? Directory.EnumerateFiles(Paths.Captures, "*.lri").OrderBy(p => p, StringComparer.Ordinal).Take(12).ToList()
            : new List<string>();
        if (captures.Count < 12) { _out.WriteLine("LUX_TEST_CAPTURES is not set, or it has fewer than 12 captures: skipped"); return; }
        var sources = captures.Take(9).ToList();
        string extra = captures[9];

        string root = Path.Combine(Paths.UiScratch, name);
        string drive = Path.Combine(root, "drive"), away = Path.Combine(root, "drive-away"), photos = Path.Combine(drive, "Photos");
        string appData = Path.Combine(root, "appdata"), local = Path.Combine(root, "local");
        foreach (var d in new[] { drive, away, appData, local }) RemoveLinksAndFolders(d);
        string[] subs = { "2024", Path.Combine("2025", "spring"), Path.Combine("2025", "summer") };
        for (int i = 0; i < sources.Count; i++)
        {
            string dir = Path.Combine(photos, subs[i % subs.Length]);
            Directory.CreateDirectory(dir);
            File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(sources[i])), sources[i]);
        }
        // a second folder that is always there: the app starts on it, so no photo of the first folder is on screen at the start
        Directory.CreateDirectory(local);
        foreach (var f in captures.Skip(10)) File.CreateSymbolicLink(Path.Combine(local, Path.GetFileName(f)), f);
        int all = sources.Count + 2;
        Directory.CreateDirectory(appData);

        // exceptions that nothing handles: none may come from this path, on any thread
        var unhandled = new List<string>();
        UnhandledExceptionEventHandler onUnhandled = (_, e) => { lock (unhandled) unhandled.Add(e.ExceptionObject.ToString() ?? ""); };
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) => { lock (unhandled) unhandled.Add(e.Exception.ToString()); };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        TaskScheduler.UnobservedTaskException += onUnobserved;

        async Task Settle(MainWindow win, Func<bool> until, int timeoutMs, string what)
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
            Assert.Fail($"{what}: not settled after {timeoutMs} ms");
        }

        try
        {
            // the first run of the app: both folders are added and scanned; the app closes on the second folder
            {
                var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: _ => { });
                ThumbnailView.Cache = s.Thumbnails;
                var vm = new MainViewModel(s);
                var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
                win.Show();
                try
                {
                    await vm.InitializeAsync();
                    await s.Library.AddFolderAsync(photos);
                    await s.Library.AddFolderAsync(local);
                    await Settle(win, () => !s.Library.GetFolders().Any(f => f.Scanning) && s.Library.Count(new CaptureQuery()) == all, 600_000, "first scan");
                    await vm.Library.RefreshTreeAsync();
                    vm.Library.SelectNodeCommand.Execute(vm.Library.Tree.Last(n => n.Kind == TreeNodeKind.Folder));
                    await Settle(win, () => !vm.Library.IsLoading, 30_000, "select the second folder");
                }
                finally { win.Close(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync(); }
            }

            // the drive is not connected at the next start
            Directory.Move(drive, away);
            {
                var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: _ => { });
                ThumbnailView.Cache = s.Thumbnails;
                var vm = new MainViewModel(s);
                var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
                win.Show();
                try
                {
                    await vm.InitializeAsync();
                    var lib = vm.Library;
                    long folderId = s.Library.GetFolders().First(f => f.Path == photos).Id;
                    TreeNodeViewModel FolderNode() => lib.Tree.First(n => n.Kind == TreeNodeKind.Folder && n.FolderId == folderId);
                    await Settle(win, () => !lib.IsLoading && lib.Tree.Count > 0, 30_000, "start without the folder");
                    Assert.Equal("not found", FolderNode().CountText);
                    Assert.Equal(0, s.Library.GetStats().Missing);   // nothing scans at the start: the photos are not marked missing yet
                    if (scanWhileAway)
                    {
                        s.Library.RescanFolder(folderId);
                        await Settle(win, () => !s.Library.GetFolders().Any(f => f.Scanning) && s.Library.GetStats().Missing == sources.Count, 60_000, "scan without the folder");
                    }

                    // the drive is connected while the app runs
                    Directory.Move(away, drive);

                    // the user selects the subfolders one after the other and a photo in each
                    var keys = lib.Tree.Where(n => n.Kind == TreeNodeKind.Subfolder && n.FolderId == folderId).Select(n => n.Key).ToList();
                    Assert.True(keys.Count >= 3);
                    foreach (var key in keys)
                    {
                        lib.SelectNodeCommand.Execute(lib.Tree.First(n => n.Key == key));
                        await Settle(win, () => !lib.IsLoading, 30_000, "select " + key);
                        Assert.Equal(key, lib.SelectedNode?.Key);
                        Assert.NotEmpty(lib.Items);
                        lib.Click(lib.Items[0], false, false);
                        await Settle(win, () => s.Thumbnails.InFlight == 0, 600_000, "thumbnails of " + key);
                    }
                    // then quickly back and forth between two of them
                    for (int k = 0; k < 12; k++)
                    {
                        lib.SelectNodeCommand.Execute(lib.Tree.First(n => n.Key == keys[k % 2 + 1]));
                        Dispatcher.UIThread.RunJobs();
                        win.RenderFrame();
                        await Task.Delay(30);
                    }
                    await Settle(win, () => !lib.IsLoading && s.Thumbnails.InFlight == 0, 600_000, "quick selection changes");

                    // the folder is seen again without a restart: the tree shows its count, no photo is missing, every photo has a thumbnail
                    await Settle(win, () => FolderNode().CountText == Text.Number(sources.Count), 60_000, "the tree shows the folder as found");
                    await Settle(win, () => !s.Library.GetFolders().Any(f => f.Scanning) && s.Library.GetStats().Missing == 0, 120_000, "no photo is missing");
                    await Settle(win, () => s.Library.GetStats().Thumbnails == all && s.Thumbnails.InFlight == 0, 600_000, "every photo has a thumbnail");
                    Assert.All(s.Library.Query(new CaptureQuery { FolderId = folderId, Limit = 100 }).Items, i => { Assert.False(i.Missing); Assert.True(i.HasThumbnail); });
                    Assert.All(lib.Items, i => Assert.False(i.Item.Missing));
                    _out.WriteLine($"thumbnails: requested {s.Thumbnails.Requested}, delivered {s.Thumbnails.Delivered}, stored {s.Library.GetStats().Thumbnails}");

                    // the folder is watched again: a capture that appears in it is found by itself
                    File.CreateSymbolicLink(Path.Combine(photos, "2024", Path.GetFileName(extra)), extra);
                    await Settle(win, () => s.Library.Count(new CaptureQuery()) == all + 1 && !s.Library.GetFolders().Any(f => f.Scanning), 120_000, "a new capture in the folder is found");
                }
                finally { win.Close(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers();   // a task whose exception nobody read reports it when it is collected
            lock (unhandled) Assert.True(unhandled.Count == 0, "exceptions that nothing handled:\n" + string.Join("\n---\n", unhandled));
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    /// <summary>Removes a test folder without following links: a link is removed, never its target.</summary>
    static void RemoveLinksAndFolders(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (e.LinkTarget != null || e is FileInfo) e.Delete();
            else RemoveLinksAndFolders(e.FullName);
        }
        Directory.Delete(dir);
    }
}
