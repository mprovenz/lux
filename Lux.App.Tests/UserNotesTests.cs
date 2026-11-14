using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Six interface checks: adding folders from the Library page, the tree's levels, thumbnails while processing, the selection
/// bar's storage notice, "show as held" on by default, Background work last in Settings — tests and screenshots 23–26.</summary>
public sealed class UserNotesTests
{
    // ================================================================================================================ (1) add folder

    [AvaloniaFact]
    public async Task Folders_are_added_from_the_library_page_and_managed_from_the_tree()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        var lib = h.Library;
        // the tree header button
        var button = h.Window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddFolderButton");
        Assert.True(button.IsEffectivelyVisible);
        h.Platform.NextFolder = "/fake/Pictures/Holiday";
        button.Command!.Execute(null);
        await h.SettleAsync(() => h.Lib.GetFolders().Count == 2 && lib.Tree.Any(n => n.Kind == TreeNodeKind.Folder && n.Label == "Holiday"));
        Assert.Contains(lib.Tree, n => n.Kind == TreeNodeKind.Folder && n.Label == "Holiday");
        h.Shot("23-library-add-folder");

        // a folder dropped on the window (the drop handler calls this; the headless platform has no drag source)
        h.Vm.IsFolderDropTarget = true;
        await h.SettleAsync(waitThumbnails: false);
        h.Shot("23b-folder-drop-hint");
        h.Vm.IsFolderDropTarget = false;
        await h.Vm.AddFoldersAsync(new[] { "/fake/Dropped photos" });
        await h.SettleAsync(() => h.Lib.GetFolders().Count == 3);
        Assert.Equal(Page.Library, h.Vm.CurrentPage);

        // the tree's context menu actions: rescan, remove (with confirmation); the menu offers add everywhere
        var folder = lib.Tree.First(n => n.Kind == TreeNodeKind.Folder && n.Label == "Holiday");
        Assert.Contains("Add folder…", LibraryView.MenuHeadersFor(folder));
        Assert.Contains("Remove folder from library…", LibraryView.MenuHeadersFor(folder));
        Assert.Contains("Add folder…", LibraryView.MenuHeadersFor(lib.Tree[0]));
        int scans = h.Lib.Jobs.Of(JobKind.FolderScan).Count();
        lib.RescanNodeCommand.Execute(folder);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.FolderScan).Count() > scans, waitThumbnails: false);
        lib.RemoveFolderNodeCommand.Execute(folder);
        var confirm = Assert.IsType<ConfirmViewModel>(h.Vm.Dialog);
        confirm.OkCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.GetFolders().All(f => !f.Path.EndsWith("Holiday", StringComparison.Ordinal)) && lib.Tree.All(n => n.Label != "Holiday"));
        Assert.DoesNotContain(lib.Tree, n => n.Label == "Holiday");

        // Settings and the first-run page keep their own entries
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(waitThumbnails: false);
        h.Platform.NextFolder = "/fake/From settings";
        await h.Vm.Settings!.AddFolderCommand.ExecuteAsync(null);
        await h.SettleAsync(() => h.Vm.Settings.Folders.Any(f => f.Path == "/fake/From settings"), waitThumbnails: false);
        Assert.Contains(h.Vm.Settings.Folders, f => f.Path == "/fake/From settings");
    }

    // ================================================================================================================ (2) tree levels

    [AvaloniaFact]
    public async Task Tree_levels_indent_expand_and_collapse()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SecondFolder = true, SeedJobs = false });
        var tree = h.Library.Tree;
        var root = tree[0];
        Assert.True(root.IsRoot && root.Depth == 0 && root.HasChildren);
        var folders = tree.Where(n => n.Kind == TreeNodeKind.Folder).ToList();
        Assert.Equal(new[] { "Light", "Light backup" }, folders.Select(f => f.Label));
        Assert.All(folders, f => { Assert.Equal(1, f.Depth); Assert.Same(root, f.Parent); });
        var year = tree.First(n => n.Kind == TreeNodeKind.Subfolder && n.Label == "2026" && n.Parent == folders[0]);
        var autumn = tree.First(n => n.Label == "Autumn");
        Assert.Equal(2, year.Depth);
        Assert.Equal(3, autumn.Depth);
        Assert.Same(year, autumn.Parent);
        Assert.Equal(autumn.Padding.Left - year.Padding.Left, TreeNodeViewModel.IndentStep);    // 16 px per level
        var groups = tree.Where(n => n.IsGroup).ToList();
        Assert.Equal(new[] { "Albums", "Smart filters" }, groups.Select(g => g.Label));
        Assert.All(tree.Where(n => n.Parent != null && n.Parent.IsGroup), n => Assert.Equal(1, n.Depth));

        // collapse a folder with the chevron: its rows hide, the choice is remembered
        await h.SelectScopeAsync(autumn.Key);
        h.Library.ToggleNodeCommand.Execute(year);
        Assert.False(year.IsExpanded);
        Assert.False(autumn.IsShown);
        Assert.True(year.IsShown);
        Assert.Same(year, h.Library.SelectedNode);                      // the hidden selection moves to the collapsed row
        Assert.Contains(year.Key, h.Lib.Settings.GetValue("tree.collapsed"));
        await h.Library.RefreshTreeAsync();
        Assert.False(h.Library.Tree.First(n => n.Key == year.Key).IsExpanded);
        // a group heading collapses its items; the keyboard expands (Right) and collapses (Left) a focused row
        var smart = h.Library.Tree.First(n => n.Label == "Smart filters");
        h.Library.SelectNodeCommand.Execute(smart);                      // a heading toggles
        Assert.All(h.Library.Tree.Where(n => n.Parent == smart), n => Assert.False(n.IsShown));
        h.Library.SelectNodeCommand.Execute(smart);
        await h.SettleAsync(waitThumbnails: false);
        var yearRow = h.Window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tree") && b.DataContext is TreeNodeViewModel n && n.Key == year.Key);
        yearRow.Focus(NavigationMethod.Tab);
        h.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.True(h.Library.Tree.First(n => n.Key == year.Key).IsExpanded);
        h.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.False(h.Library.Tree.First(n => n.Key == year.Key).IsExpanded);
        await h.SettleAsync();
        h.Shot("24-folder-tree-levels");
    }

    // ================================================================================================================ (3) processing thumbnails

    [AvaloniaFact]
    public async Task Thumbnails_of_captures_being_processed_are_blurred_with_the_spinner()
    {
        await using var h = await Harness.CreateAsync();
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        // more work: three more captures queued for full detail (no notice: "don't ask again")
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        foreach (var name in new[] { "L16_00494", "L16_00496", "L16_00511" })
        {
            h.Library.Click(h.Item(name), false, false);
            await h.Library.Details.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        }
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheUpgrade).Count() == 3);
        h.Library.ClearSelection();
        await h.SettleAsync(() => h.Item("L16_00511").IsProcessing);
        var running = h.Item("L16_00501");     // analysis running at 35 %
        var waiting = h.Item("L16_00498");     // analysis waiting
        Assert.True(running.IsProcessing && waiting.IsProcessing && h.Item("L16_00494").IsProcessing);
        Assert.Equal(0.35, running.ProcessingProgress, 2);
        Assert.True(double.IsNaN(waiting.ProcessingProgress));
        Assert.False(h.Item("L16_00493").IsProcessing);

        foreach (var (mode, shot) in new[] { (BrowseMode.Grid, "25-thumbnails-processing-blur-grid"), (BrowseMode.Timeline, "25-thumbnails-processing-blur-timeline"),
                                            (BrowseMode.List, "25-thumbnails-processing-blur-list") })
        {
            h.Library.Mode = mode;
            await h.SettleAsync();
            h.Library.MoveTo(running.Index, false, false);                 // bring it into view
            await h.SettleAsync();
            var views = h.Browse.ActiveItems.GetVisualDescendants().OfType<ThumbnailView>().Where(v => v.IsEffectivelyVisible).ToList();
            var cell = views.First(v => v.CaptureId == running.Id);
            Assert.Equal(ImageState.Processing, cell.State);
            Assert.NotNull(cell.Source);                                   // the thumbnail underneath, blurred
            Assert.Equal(mode == BrowseMode.Timeline, cell.ShowsLabel);    // the label only from 120 px (the timeline cards)
            Assert.Contains(views, v => v.State == ImageState.Ready);
            // the blurred copy is made once per thumbnail, not per frame
            int made = ImageStateView.BlurredMade;
            for (int i = 0; i < 5; i++) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Equal(made, ImageStateView.BlurredMade);
            h.Shot(shot);
        }
        // finished: the sharp thumbnail again
        for (int i = 0; i < 60 && running.IsProcessing; i++) { h.Lib.FakeJobs.Tick(0.5); await h.SettleAsync(waitThumbnails: false); }
        Assert.False(running.IsProcessing);
        var done = h.Browse.ActiveItems.GetVisualDescendants().OfType<ThumbnailView>().First(v => v.CaptureId == running.Id);
        Assert.Equal(ImageState.Ready, done.State);
    }

    // ================================================================================================================ (4) selection notice

    [AvaloniaFact]
    public async Task Selection_bar_upgrade_and_release_use_the_shared_notice()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        var names = new[] { "L16_00494", "L16_00496", "L16_00511" };   // 13 MP kept
        h.Library.Click(h.Item(names[0]), false, false);
        h.Library.Click(h.Item(names[1]), true, false);
        h.Library.Click(h.Item(names[2]), true, false);
        await h.Library.UpgradeSelectedCommand.ExecuteAsync(null);
        var notice = Assert.IsType<StorageNoticeViewModel>(h.Vm.Dialog);
        Assert.True(notice.IsUpgrade);
        Assert.Equal(3, notice.Ids.Count);
        Assert.Contains("these 3 photos", notice.Text);
        Assert.Matches(@"about [\d.]+ (MB|GB) more on disk in all", notice.Text);
        await h.SettleAsync();
        h.Shot("26-selection-upgrade-notice");
        notice.CancelCommand.Execute(null);
        Assert.Null(h.Vm.Dialog);
        Assert.Empty(h.Lib.Jobs.Of(JobKind.CacheUpgrade));

        // again, with "Don't ask again" — the same preference as the quality selector
        await h.Library.UpgradeSelectedCommand.ExecuteAsync(null);
        notice = Assert.IsType<StorageNoticeViewModel>(h.Vm.Dialog);
        notice.DontAskAgain = true;
        notice.ConfirmCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheUpgrade).Count() == 3 && h.Lib.Settings.DontAskAboutExtraStorage, waitThumbnails: false);
        Assert.True(h.Lib.Settings.DontAskAboutExtraStorage);
        // the details panel's selector does not ask any more either
        var other = h.Library.Items.First(i => i.Item.BestQuality == ViewQuality.Linear13 && !names.Contains(i.Name) && !i.Missing);
        h.Library.Click(other, false, false);
        await h.Library.Details.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        Assert.False(h.Library.Details.Quality.AskOpen);
        for (int i = 0; i < 60; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => names.All(n => h.Item(n).IsFull), waitThumbnails: false);

        // release from the selection bar: the same confirmation as going down in the selector
        h.Library.Click(h.Item(names[0]), false, false);
        h.Library.Click(h.Item(names[1]), true, false);
        await h.Library.ReleaseSelectedCommand.ExecuteAsync(null);
        var release = Assert.IsType<StorageNoticeViewModel>(h.Vm.Dialog);
        Assert.False(release.IsUpgrade);
        Assert.Contains("Release the full-detail copies of these 2 photos", release.Text);
        Assert.Contains("can make them again", release.Text);
        release.ConfirmCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheRelease).Count() == 2, waitThumbnails: false);
        for (int i = 0; i < 10; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => h.Item(names[0]).Item.BestQuality == ViewQuality.Linear13, waitThumbnails: false);
        Assert.Equal(ViewQuality.Linear13, h.Item(names[1]).Item.BestQuality);
    }

    // ================================================================================================================ (5) and (6)

    [AvaloniaFact]
    public async Task Show_as_held_is_on_by_default_and_background_work_is_last()
    {
        await using var h = await Harness.CreateAsync();
        Assert.True(h.Lib.Settings.ShowPhotosAsHeld);
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(() => h.Vm.Settings!.Folders.Count > 0, waitThumbnails: false);
        Assert.True(h.Vm.Settings!.ShowAsHeld);
        Assert.True(h.Lib.Settings.ShowPhotosAsHeld);   // opening the page does not write it
        var page = h.Window.GetVisualDescendants().OfType<SettingsView>().First();
        var sections = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "Sections").Children.OfType<Border>().ToList();
        Assert.Equal("LimitsSection", sections[^1].Name);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("On by default", StringComparison.Ordinal) == true);
    }
}
