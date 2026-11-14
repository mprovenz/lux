using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.App.Views;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Every page and state rendered with the fake library to PNG files (first UI output folder) for visual review.</summary>
public sealed class ScreenshotTests
{
    [AvaloniaFact]
    public async Task Library_grid_with_selection_and_details()
    {
        await using var h = await Harness.CreateAsync();
        h.Library.Mode = BrowseMode.Grid;
        await h.MockupSelectionAsync();
        Assert.Equal(3, h.Library.SelectedCount);
        Assert.Equal("L16_00493", h.Library.Details.Current?.Name);
        h.Shot("01-library-grid");
    }

    [AvaloniaFact]
    public async Task Library_timeline_with_selection_and_details()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.Mode = BrowseMode.Timeline;
        await h.SettleAsync();
        h.Shot("02-library-timeline");
    }

    [AvaloniaFact]
    public async Task Library_list_with_selection_and_details()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.Mode = BrowseMode.List;
        await h.SettleAsync();
        h.Shot("03-library-list");
    }

    [AvaloniaFact]
    public async Task First_run_empty_state()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Empty = true });
        Assert.True(h.Vm.IsFirstRun);
        h.Shot("04-first-run");
    }

    [AvaloniaFact]
    public async Task Export_side_panel()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        await h.SettleAsync();
        Assert.True(h.Library.IsExportOpen);
        h.Shot("05-export-panel");
    }

    [AvaloniaFact]
    public async Task Queue_with_mixed_jobs()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync();
        Assert.Contains("Queue · ", h.Vm.QueueTabLabel);
        h.Shot("06-queue");
    }

    [AvaloniaFact]
    public async Task Lumen_import_dialog()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.OpenLumenImport(new Lux.Library.CaptureQuery(), "~/Pictures/Light");
        var dlg = (LumenImportViewModel)h.Vm.Dialog!;
        await h.SettleAsync(() => !dlg.IsScanning);
        Assert.True(dlg.Total > 0);
        h.Shot("07-lumen-import");
    }

    [AvaloniaFact]
    public async Task Settings_page()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(() => h.Vm.Settings?.Folders.Count > 0);
        h.Shot("08-settings");
        var sv = h.Window.GetVisualDescendants().OfType<SettingsView>().First().GetVisualDescendants().OfType<ScrollViewer>().First();
        sv.Offset = new Avalonia.Vector(0, 10_000);
        await h.SettleAsync();
        h.Shot("08b-settings-lower");
    }

    [AvaloniaFact]
    public void Control_gallery()
    {
        var gallery = new GalleryView();
        var w = new Window { Width = 1440, Height = 1400, Content = gallery };
        w.Show();
        w.Activate();
        Dispatcher.UIThread.RunJobs();
        gallery.ShowFocusSample();
        Dispatcher.UIThread.RunJobs();
        Harness.Capture(w, "09-gallery");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Develop_and_camera_placeholders()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenInDevelopCommand.Execute(null);
        await h.SettleAsync();
        Assert.True(h.Vm.IsDevelop);
        h.Shot("10-develop-placeholder");
        h.Vm.CurrentPage = Page.Camera;
        await h.SettleAsync();
        h.Shot("11-camera-placeholder");
    }

    [AvaloniaFact]
    public async Task Missing_captures_and_large_thumbnails()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Captures = 600 });
        var missing = h.Library.Tree.FirstOrDefault(n => n.Key == "smart:missing");
        Assert.NotNull(missing);
        await h.SelectScopeAsync("smart:missing");
        Assert.True(h.Library.TotalCount > 0);
        Assert.All(h.Library.Items, i => Assert.True(i.Missing));
        h.Library.Click(h.Library.Items[0], false, false);
        await h.SettleAsync();
        h.Shot("12-library-missing");
        await h.SelectScopeAsync("all");
        h.Library.ThumbSize = 240;
        await h.SettleAsync();
        h.Shot("13-library-grid-large");
    }

    [AvaloniaFact]
    public async Task New_album_prompt_and_remove_confirmation()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.NewAlbum(h.Library.Targets());
        await h.SettleAsync();
        h.Shot("14-new-album");
        ((PromptViewModel)h.Vm.Dialog!).CancelCommand.Execute(null);
        h.Library.RemoveFromLibraryCommand.Execute(null);
        await h.SettleAsync();
        h.Shot("15-remove-confirm");
    }
}
