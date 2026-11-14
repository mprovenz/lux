using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Fake;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Interaction tests through real input where it matters (mouse clicks on cells, keys on the browse view) and through the view
/// models elsewhere; library effects are checked on the fake library.</summary>
public sealed class InteractionTests
{
    static Border CellOf(Harness h, CaptureItemViewModel item) =>
        h.Browse.ActiveItems.GetVisualDescendants().OfType<Border>()
            .First(b => b.DataContext == item && (b.Classes.Contains("cell") || b.Classes.Contains("card2") || b.Classes.Contains("listrow")));

    static void ClickCell(Harness h, CaptureItemViewModel item, RawInputModifiers mods = RawInputModifiers.None)
    {
        var cell = CellOf(h, item);
        var p = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, 12), h.Window)!.Value;
        h.Window.MouseDown(p, MouseButton.Left, mods);
        h.Window.MouseUp(p, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
    }

    static void Key(Harness h, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        h.Window.KeyPress(key, mods, PhysicalKey.None, null);
        h.Window.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task View_switch_is_remembered_per_folder()
    {
        await using var h = await Harness.CreateAsync();
        var autumn = h.Library.Tree.First(n => n.Label == "Autumn").Key;
        var summer = h.Library.Tree.First(n => n.Label == "Summer").Key;
        await h.SelectScopeAsync(autumn);
        Assert.Equal(BrowseMode.Grid, h.Library.Mode);
        h.Library.SetModeCommand.Execute(BrowseMode.Timeline);
        await h.SettleAsync();
        Assert.True(h.Browse.TimelineScroll.IsVisible);
        Assert.False(h.Browse.GridScroll.IsVisible);
        await h.SelectScopeAsync(summer);
        Assert.Equal(BrowseMode.Grid, h.Library.Mode);
        h.Browse.Focus();
        Key(h, Avalonia.Input.Key.L);   // keyboard: L = list
        Assert.Equal(BrowseMode.List, h.Library.Mode);
        await h.SelectScopeAsync(autumn);
        Assert.Equal(BrowseMode.Timeline, h.Library.Mode);
        await h.SelectScopeAsync(summer);
        Assert.Equal(BrowseMode.List, h.Library.Mode);
        Assert.Equal("List", h.Lib.Settings.GetValue("view:" + summer));
    }

    [AvaloniaFact]
    public async Task Mouse_selection_click_ctrl_shift()
    {
        await using var h = await Harness.CreateAsync();
        var items = h.Library.Items;
        ClickCell(h, items[1]);
        Assert.Equal(1, h.Library.SelectedCount);
        Assert.True(items[1].IsSelected);
        ClickCell(h, items[3], RawInputModifiers.Control);
        Assert.Equal(2, h.Library.SelectedCount);
        ClickCell(h, items[6], RawInputModifiers.Shift);   // range from the anchor (3) to 6
        Assert.Equal(new[] { 3, 4, 5, 6 }, items.Where(i => i.IsSelected).Select(i => i.Index).ToArray());
        ClickCell(h, items[4], RawInputModifiers.Control);   // toggle one off
        Assert.Equal(3, h.Library.SelectedCount);
        ClickCell(h, items[0]);
        Assert.Equal(1, h.Library.SelectedCount);
        Assert.Equal(items[0], h.Library.Details.Current);

        // the same in the list view
        h.Library.Mode = BrowseMode.List;
        await h.SettleAsync();
        ClickCell(h, items[2], RawInputModifiers.Shift);
        Assert.Equal(3, h.Library.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Keyboard_cursor_selection_and_rating_keys()
    {
        await using var h = await Harness.CreateAsync();
        var lib = h.Library;
        h.Browse.Focus();
        Key(h, Avalonia.Input.Key.Right);          // cursor to the first item
        Key(h, Avalonia.Input.Key.Right);
        Assert.Equal(1, lib.CursorIndex);
        Assert.Equal(1, lib.SelectedCount);
        Key(h, Avalonia.Input.Key.Down);           // a row down = the column count
        Assert.Equal(1 + lib.GridMetrics.Columns, lib.CursorIndex);
        Key(h, Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        Assert.Equal(2, lib.SelectedCount);
        Key(h, Avalonia.Input.Key.D4);
        var rated = lib.Items.Where(i => i.IsSelected).ToList();
        Assert.All(rated, i => Assert.Equal(4, i.Rating));
        await h.SettleAsync(() => rated.All(i => h.Lib.Get(i.Id)!.Rating == 4));
        Assert.All(rated, i => Assert.Equal(4, h.Lib.Get(i.Id)!.Rating));
        Key(h, Avalonia.Input.Key.D0);
        await h.SettleAsync(() => rated.All(i => h.Lib.Get(i.Id)!.Rating == 0));
        Assert.All(rated, i => Assert.Equal(0, h.Lib.Get(i.Id)!.Rating));
        Key(h, Avalonia.Input.Key.A, RawInputModifiers.Control);
        Assert.Equal(lib.TotalCount, lib.SelectedCount);
        Key(h, Avalonia.Input.Key.Escape);
        Assert.Equal(0, lib.SelectedCount);
        Key(h, Avalonia.Input.Key.End);
        Assert.Equal(lib.TotalCount - 1, lib.CursorIndex);
        Key(h, Avalonia.Input.Key.Home);
        Assert.Equal(0, lib.CursorIndex);

        // timeline: down goes to the same column of the next card row
        lib.Mode = BrowseMode.Timeline;
        await h.SettleAsync();
        h.Browse.Focus();
        Key(h, Avalonia.Input.Key.Down);
        Assert.True(lib.CursorIndex > 0);
    }

    [AvaloniaFact]
    public async Task Sort_menu_and_list_headers()
    {
        await using var h = await Harness.CreateAsync();
        var lib = h.Library;
        lib.ApplySortCommand.Execute(lib.SortChoices.First(c => c.Sort == CaptureSort.Name));
        await h.SettleAsync();
        var names = lib.Items.Select(i => i.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        Assert.Equal("Sort: name ↑", lib.SortLabel);
        lib.Mode = BrowseMode.List;
        lib.SortByCommand.Execute("rating");
        await h.SettleAsync();
        var ratings = lib.Items.Select(i => i.Rating).ToList();
        Assert.Equal(ratings.OrderByDescending(r => r).ToList(), ratings);
        Assert.Equal("Rating ↓", lib.HeaderRating);
        lib.SortByCommand.Execute("rating");     // again: reverse
        await h.SettleAsync();
        Assert.Equal(lib.Items.Select(i => i.Rating).OrderBy(r => r).ToList(), lib.Items.Select(i => i.Rating).ToList());
        // the timeline always groups by day, newest first by default
        lib.ApplySortCommand.Execute(lib.SortChoices[0]);
        lib.Mode = BrowseMode.Timeline;
        await h.SettleAsync();
        Assert.IsType<TimelineHeaderViewModel>(lib.TimelineRows[0]);
    }

    [AvaloniaFact]
    public async Task Filter_text_and_chips()
    {
        await using var h = await Harness.CreateAsync();
        var lib = h.Library;
        int all = lib.TotalCount;
        lib.FilterText = "00500";
        await lib.ApplyFilterNowAsync();
        await h.SettleAsync();
        Assert.Equal(1, lib.TotalCount);
        Assert.Equal("L16_00500", lib.Items[0].Name);
        lib.FilterText = "Autumn";      // matches the folder path
        await lib.ApplyFilterNowAsync();
        await h.SettleAsync();
        Assert.True(lib.TotalCount > 0 && lib.TotalCount < all);
        lib.FilterText = "";
        await lib.ApplyFilterNowAsync();
        lib.SelectChipCommand.Execute(lib.Chips.First(c => c.MinRating == 4));
        await h.SettleAsync();
        Assert.True(lib.TotalCount > 0);
        Assert.All(lib.Items, i => Assert.True(i.Rating >= 4));
        lib.SelectChipCommand.Execute(lib.Chips.First(c => c.Filter == SmartFilter.NotAnalysed));
        await h.SettleAsync();
        Assert.All(lib.Items, i => Assert.NotEqual(DepthState.Analysed, i.Item.DepthState));
        Assert.StartsWith("Not analysed · ", lib.Chips.First(c => c.Filter == SmartFilter.NotAnalysed).Label);
    }

    [AvaloniaFact]
    public async Task Export_panel_enqueues_one_job_per_capture()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        var ids = h.Library.Targets();
        int before = h.Lib.Jobs.Of(JobKind.Export).Count();
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        Assert.Equal("Export 3 captures", panel.Title);
        panel.Size = panel.Sizes[1];
        panel.Formats.First(f => f.Key == "jpg+depth").IsOn = true;
        panel.AddToQueueCommand.Execute(null);
        Assert.False(h.Library.IsExportOpen);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Count() == before + 3);
        var jobs = h.Lib.Jobs.Of(JobKind.Export).Where(j => j.Id > maxBefore).ToList();
        Assert.All(jobs, j => Assert.Contains(j.CaptureId!, ids));
        Assert.Equal(3, jobs.Count);
        var vm = h.Vm.Queue.Jobs.First(j => j.Id == jobs[0].Id);
        Assert.Equal("DNG + JPG + JPG + depth · 13 MP", vm.What);

        // depth effect off = the capture's all-in-focus f-number
        h.Library.OpenExport();
        panel = h.Library.ExportPanel!;
        panel.DepthEffect = panel.DepthChoices[1];
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Count() == before + 6);
        Assert.Equal(3, h.Lib.Jobs.Of(JobKind.Export).Count(j => j.Title.Contains("DNG + JPG", StringComparison.Ordinal) && j.State == JobState.Waiting && j.Id > jobs.Max(x => x.Id)));
    }

    [AvaloniaFact]
    public async Task Queue_cancel_retry_clear_pause()
    {
        await using var h = await Harness.CreateAsync();
        var q = h.Vm.Queue;
        Assert.Equal("Queue · 4", h.Vm.QueueTabLabel);
        var running = q.Jobs.First(j => j.IsRunning && j.Info.Kind == JobKind.Export);
        Assert.Equal("Cancel", running.PrimaryAction);
        running.PrimaryCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Get(running.Id)!.State == JobState.Cancelled);
        Assert.Equal(JobState.Cancelled, h.Lib.Jobs.Get(running.Id)!.State);
        await h.SettleAsync(() => q.Jobs.First(j => j.Id == running.Id).IsCancelled);
        Assert.Equal("Queue · 3", h.Vm.QueueTabLabel);
        var cancelled = q.Jobs.First(j => j.Id == running.Id);
        Assert.Equal("Retry", cancelled.PrimaryAction);
        cancelled.PrimaryCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs().Any(j => j.RetryOf == running.Id));
        Assert.Contains(h.Lib.Jobs.GetJobs(), j => j.RetryOf == running.Id && j.State == JobState.Waiting);

        // run the queue: jobs tick to the end
        for (int i = 0; i < 400 && h.Lib.Jobs.Status.Running + h.Lib.Jobs.Status.Waiting > 0; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => q.UnfinishedCount == 0);
        Assert.Equal("Queue", h.Vm.QueueTabLabel);
        Assert.Contains(q.Jobs, j => j.IsDone && j.PrimaryAction == "Show file");
        q.Jobs.First(j => j.IsDone && j.PrimaryAction == "Show file").PrimaryCommand.Execute(null);
        Assert.Single(h.Platform.Revealed);

        q.ClearFinishedCommand.Execute(null);
        await h.SettleAsync(() => q.IsEmpty);
        Assert.True(q.IsEmpty);

        q.TogglePauseCommand.Execute(null);
        await h.SettleAsync(() => q.IsPaused);
        Assert.Equal("Resume", q.PauseLabel);
        h.Library.Click(h.Library.Items[0], false, false);
        h.Library.AnalyseSelectedCommand.Execute(null);
        await h.Library.UpgradeSelectedCommand.ExecuteAsync(null);
        if (h.Vm.Dialog is StorageNoticeViewModel notice) notice.ConfirmCommand.Execute(null);   // the extra-storage notice
        await h.SettleAsync(() => h.Lib.Jobs.Status.Waiting > 0);
        h.Lib.FakeJobs.Tick(1);
        Assert.Equal(0, h.Lib.Jobs.Status.Running);   // paused: nothing starts
        q.TogglePauseCommand.Execute(null);
        await h.SettleAsync(() => !q.IsPaused);
        h.Lib.FakeJobs.Tick(0.1);
        Assert.True(h.Lib.Jobs.Status.Running > 0);
    }

    [AvaloniaFact]
    public async Task Upgrade_to_full_detail_shows_in_the_grid()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.Missing);
        h.Library.Click(item, false, false);
        Assert.True(h.Library.Details.Quality.IsStandard);
        await h.Library.Details.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheUpgrade).Any());
        for (int i = 0; i < 40; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => item.IsFull && h.Library.Details.Quality.IsFull);
        Assert.Equal("FULL", item.CacheTag);
        Assert.StartsWith("Full detail kept", h.Library.Details.Quality.StatusText);
    }

    [AvaloniaFact]
    public async Task Albums_drop_and_new_album()
    {
        await using var h = await Harness.CreateAsync();
        var lib = h.Library;
        var portfolio = lib.Tree.First(n => n.Label == "Portfolio");
        int before = h.Lib.GetAlbums().First(a => a.Name == "Portfolio").CaptureCount;
        var add = lib.Items.Where(i => !h.Lib.GetAlbumsOf(i.Id).Any(a => a.Name == "Portfolio")).Take(2).Select(i => i.Id).ToList();
        lib.DropOnAlbum(portfolio, add);
        await h.SettleAsync(() => h.Lib.GetAlbums().First(a => a.Name == "Portfolio").CaptureCount == before + 2);
        Assert.Equal(before + 2, h.Lib.GetAlbums().First(a => a.Name == "Portfolio").CaptureCount);
        await h.SettleAsync(() => lib.Tree.First(n => n.Label == "Portfolio").CountText == (before + 2).ToString());

        lib.Click(lib.Items[0], false, false);
        lib.NewAlbum(lib.Targets());
        var prompt = (PromptViewModel)h.Vm.Dialog!;
        prompt.Value = "Portfolio";
        await prompt.OkCommand.ExecuteAsync(null);
        Assert.NotNull(prompt.Error);                 // names are unique
        prompt.Value = "Walks";
        await prompt.OkCommand.ExecuteAsync(null);
        await h.SettleAsync(() => lib.Tree.Any(n => n.Label == "Walks"));
        Assert.Null(h.Vm.Dialog);
        Assert.Equal("1", lib.Tree.First(n => n.Label == "Walks").CountText);
    }

    [AvaloniaFact]
    public async Task Lumen_import_queues_the_chosen_groups()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        h.Vm.OpenLumenImport(new CaptureQuery(), "/fake/Pictures/Light");
        var dlg = (LumenImportViewModel)h.Vm.Dialog!;
        await h.SettleAsync(() => !dlg.IsScanning);
        Assert.Equal(3, dlg.Groups.Count);
        Assert.False(dlg.Groups[2].IsOn);
        int expected = dlg.Groups[0].Count + dlg.Groups[1].Count;
        Assert.Equal($"Import {expected} captures", dlg.ImportLabel);
        dlg.Groups[1].IsOn = false;
        expected = dlg.Groups[0].Count;
        dlg.NewerToneStyle = true;
        var chosen = dlg.Groups[0].Ids.ToList();
        dlg.ImportCommand.Execute(null);
        Assert.Null(h.Vm.Dialog);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.LumenImport).Count() == expected);
        Assert.Equal(expected, h.Lib.Jobs.Of(JobKind.LumenImport).Count());
        Assert.All(chosen, id => Assert.True(h.Lib.NewerToneStyleChosen[id]));   // the tone-style option reaches the library
    }

    [AvaloniaFact]
    public async Task First_run_add_folder_flow()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Empty = true });
        Assert.True(h.Vm.ShowEmpty);
        h.Platform.NextFolder = "/fake/Pictures/New";
        await h.Vm.AddFolderCommand.ExecuteAsync(null);
        await h.SettleAsync(() => !h.Vm.IsFirstRun);
        Assert.False(h.Vm.IsFirstRun);
        Assert.Single(h.Lib.GetFolders());
        Assert.Contains(h.Lib.Jobs.GetJobs(), j => j.Kind == JobKind.FolderScan);
    }

    [AvaloniaFact]
    public async Task Settings_write_through_to_the_library()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.CurrentPage = Page.Settings;
        var s = h.Vm.Settings!;
        await h.SettleAsync(() => s.Folders.Count == 1);
        s.LimitRows["jobs.workers"].Text = "4";
        s.LimitRows["jobs.workers"].Commit();
        s.DefaultLevel = s.AppChoices.First(c => c.Level == CacheLevel.Full);
        s.Folders[0].Level = s.FolderChoices.First(c => c.Level == CacheLevel.Geometry);
        await h.SettleAsync(() => h.Lib.Settings.DefaultCacheLevel == CacheLevel.Full && h.Lib.Settings.GetFolderCacheLevel(1) == CacheLevel.Geometry);
        Assert.Equal(4, h.Lib.Settings.WorkerCount);
        Assert.Equal(CacheLevel.Full, h.Lib.Settings.DefaultCacheLevel);
        Assert.Equal(CacheLevel.Geometry, h.Lib.Settings.EffectiveCacheLevel(1));
    }

    [AvaloniaFact]
    public async Task Window_state_is_kept_in_the_settings()
    {
        await using var h = await Harness.CreateAsync();
        h.Window.Width = 1234;
        h.Window.Height = 777;
        h.Window.SaveWindowState();
        var again = new MainWindow { DataContext = h.Vm };
        Assert.Equal(1234, again.Width);
        Assert.Equal(777, again.Height);
        Assert.Equal("1234", h.Lib.Settings.GetValue("window.width"));
    }
}
