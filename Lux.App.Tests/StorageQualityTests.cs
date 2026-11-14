using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Settings from the library's limit descriptors, the local data folder, the interaction hint, storage markers, the
/// "Processing image" placeholder and the quality selector — tests and the screenshots 16–22.</summary>
public sealed class StorageQualityTests
{
    static async Task<SettingsViewModel> OpenSettings(Harness h)
    {
        h.Vm.CurrentPage = Page.Settings;
        var s = h.Vm.Settings!;
        await h.SettleAsync(() => s.Folders.Count > 0 && s.LocalCountText.Length > 0);
        return s;
    }

    static SettingsView SettingsPage(Harness h) => h.Window.GetVisualDescendants().OfType<SettingsView>().First();

    // ================================================================================================================ settings

    [AvaloniaFact]
    public async Task Every_limit_round_trips_and_invalid_values_are_refused()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        var s = await OpenSettings(h);
        var limits = h.Lib.Settings.GetLimits().Where(d => d.Kind != LimitKind.Folder).ToList();
        Assert.Equal(limits.Count, s.LimitRows.Count);
        Assert.Contains(s.LimitGroups, g => g.Title == "Jobs and threads");
        Assert.Contains(s.LimitGroups, g => g.Title == "Memory reserved per job type");
        Assert.Contains(s.LimitGroups, g => g.Title == "Priorities");
        Assert.Contains(s.LimitRows.Values, r => r.Name == "Hold heavy jobs while I work" && r.IsToggle);

        foreach (var d in limits)
        {
            var row = s.LimitRows[d.Key];
            double valid = d.Kind == LimitKind.Toggle ? 1 - d.Default : d.Default + 1 <= d.Max ? d.Default + 1 : d.Default - 1;
            if (d.Kind == LimitKind.Toggle) row.IsOn = valid > 0;
            else { row.Text = valid.ToString(System.Globalization.CultureInfo.InvariantCulture); row.Commit(); }
            await h.SettleAsync(() => Math.Abs(h.Lib.Settings.GetLimit(d.Key) - valid) < 1e-9, waitThumbnails: false);
            Assert.Equal(valid, h.Lib.Settings.GetLimit(d.Key), 9);
            Assert.False(row.IsDefault);
            Assert.Null(row.Error);
            if (d.Kind == LimitKind.Toggle) continue;

            // out of range: the library's message on the row, the value unchanged
            row.Text = (d.Max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.Commit();
            await h.SettleAsync(() => row.Error != null, waitThumbnails: false);
            Assert.Contains("must be", row.Error);
            Assert.Equal(valid, h.Lib.Settings.GetLimit(d.Key), 9);
            // not a number
            row.Text = "lots";
            row.Commit();
            Assert.Contains("type a number", row.Error);
            // reset
            row.ResetCommand.Execute(null);
            await h.SettleAsync(() => row.IsDefault, waitThumbnails: false);
            Assert.Equal(d.Default, h.Lib.Settings.GetLimit(d.Key), 9);
        }
        // a whole number is required where the limit counts things
        s.LimitRows["jobs.workers"].Text = "2.5";
        s.LimitRows["jobs.workers"].Commit();
        await h.SettleAsync(() => s.LimitRows["jobs.workers"].Error != null, waitThumbnails: false);
        Assert.Contains("whole number", s.LimitRows["jobs.workers"].Error);
        // reset all (through the confirmation)
        s.LimitRows["jobs.workers"].Text = "6"; s.LimitRows["jobs.workers"].Commit();
        s.LimitRows["priority.Export"].Text = "0"; s.LimitRows["priority.Export"].Commit();
        await h.SettleAsync(() => h.Lib.Settings.WorkerCount == 6 && h.Lib.Settings.PriorityOf(JobKind.Export) == 0, waitThumbnails: false);
        s.ResetAllLimitsCommand.Execute(null);
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Settings.WorkerCount == 4 && s.LimitRows.Values.All(r => r.IsDefault), waitThumbnails: false);
        Assert.All(s.LimitRows.Values, r => Assert.True(r.IsDefault, r.Key));
        Assert.Null(h.Vm.Dialog);
        // no ratings choice any more
        Assert.DoesNotContain(h.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Keep ratings with the captures") == true);
    }

    [AvaloniaFact]
    public async Task Local_data_folder_moves_as_a_queue_job()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        var s = await OpenSettings(h);
        Assert.Equal("/fake/Lux/contexts", s.LocalFolder);
        Assert.True(s.LocalIsDefault);
        Assert.Matches(@"^\d+ photos? keeps? (its|their) data here now\.$", s.LocalCountText);
        // a folder Lux cannot use: refused at once
        h.Platform.NextFolder = "/fake/readonly/data";
        await s.ChooseLocalFolderCommand.ExecuteAsync(null);
        Assert.NotNull(s.LocalError);
        Assert.Empty(h.Lib.Jobs.Of(JobKind.ContextStoreMove));
        // a good one: a job with progress, then the setting switches
        h.Platform.NextFolder = "/fake/Other drive/Lux data";
        await s.ChooseLocalFolderCommand.ExecuteAsync(null);
        Assert.Null(s.LocalError);
        await h.SettleAsync(() => s.MoveJob != null, waitThumbnails: false);
        h.Lib.FakeJobs.Tick(0.1);
        h.Lib.FakeJobs.Tick(1.5);
        await h.SettleAsync(() => s.MoveJob is { IsRunning: true, Progress: > 0 }, waitThumbnails: false);
        Assert.True(s.IsMoving);
        Assert.Contains("of 12 files", s.MoveJob!.StatusText);
        SettingsPage(h).ShowSection("storage");
        await h.SettleAsync(waitThumbnails: false);
        h.Shot("17-settings-storage");
        for (int i = 0; i < 20; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => !s.IsMoving && s.LocalFolder == "/fake/Other drive/Lux data", waitThumbnails: false);
        Assert.Equal("/fake/Other drive/Lux data", h.Lib.Settings.LocalContextFolder);
        Assert.False(s.LocalIsDefault);
        Assert.StartsWith("Default: ", s.LocalDefaultText);
        Assert.Contains(h.Vm.Queue.Jobs, j => j.Info.Kind == JobKind.ContextStoreMove && j.IsDone);
    }

    [AvaloniaFact]
    public async Task Settings_limits_screenshot()
    {
        await using var h = await Harness.CreateAsync();
        var s = await OpenSettings(h);
        s.LimitRows["jobs.workers"].Text = "40";            // shows a validation message
        s.LimitRows["jobs.workers"].Commit();
        s.LimitRows["jobs.pause_heavy_while_interacting"].IsOn = true;
        await h.SettleAsync(() => s.LimitRows["jobs.workers"].Error != null && h.Lib.Settings.PauseHeavyJobsWhileInteracting, waitThumbnails: false);
        SettingsPage(h).ShowSection("limits");
        await h.SettleAsync(waitThumbnails: false);
        h.Shot("16-settings-limits");
    }

    // ================================================================================================================ interaction hint

    [AvaloniaFact]
    public async Task Real_interaction_tells_the_queue_and_heavy_jobs_wait()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        h.Lib.Settings.PauseHeavyJobsWhileInteracting = true;
        var cell = h.Browse.ActiveItems.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("cell"));
        var p = cell.TranslatePoint(new Point(20, 20), h.Window)!.Value;
        h.Window.MouseDown(p, MouseButton.Left);
        h.Window.MouseUp(p, MouseButton.Left);
        await h.SettleAsync(() => h.Lib.FakeJobs.Interactive, waitThumbnails: false);
        Assert.Contains(true, h.Lib.FakeJobs.InteractiveCalls);
        Assert.True(h.Library.Interaction.IsActive);
        // a heavy job waits while the user works
        h.Library.Click(h.Library.Items.First(i => i.Item.DepthState != DepthState.Analysed && !i.Missing && !i.Unreadable), false, false);
        h.Library.AnalyseSelectedCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Analysis).Any(), waitThumbnails: false);
        h.Lib.FakeJobs.Tick(0.1);
        Assert.Equal("Waiting while you work", h.Lib.Jobs.Of(JobKind.Analysis).First().Status);
        // idle: false, and the job starts
        h.Library.Interaction.End();
        await h.SettleAsync(() => !h.Lib.FakeJobs.Interactive, waitThumbnails: false);
        h.Lib.FakeJobs.Tick(0.1);
        Assert.Equal(JobState.Running, h.Lib.Jobs.Of(JobKind.Analysis).First().State);
        // a programmatic scroll is not interaction
        int n = h.Lib.FakeJobs.InteractiveCalls.Count;
        h.Browse.ActiveScroll.Offset = new Vector(0, 300);
        await h.SettleAsync(waitThumbnails: false);
        Assert.Equal(n, h.Lib.FakeJobs.InteractiveCalls.Count);
    }

    // ================================================================================================================ markers

    [AvaloniaFact]
    public async Task Storage_markers_and_the_kept_aside_notice()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        var local = h.Item("L16_00496");          // its folder cannot be written: data on this computer
        var notSaved = h.Item("L16_00499");       // neither place writable: rating in the library only
        var superseded = h.Item("L16_00494");     // a local copy kept aside after a conflict
        Assert.True(local.InLocalStore && local.HasStorageMarker && !local.NotSavedWithPhoto);
        Assert.Contains("can't be written", local.StorageMarkerTip);
        Assert.True(notSaved.NotSavedWithPhoto && notSaved.HasStorageMarker);
        Assert.Equal("Not saved with the photo yet", notSaved.StorageText);
        Assert.False(h.Item("L16_00493").HasStorageMarker);
        Assert.Equal("With the photo", h.Item("L16_00493").StorageText);
        // drawn in the grid cell, with its tooltip
        var markers = h.Browse.ActiveItems.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("marker") && e.IsEffectivelyVisible).ToList();
        Assert.Contains(markers, m => m.DataContext == local);
        Assert.Contains(markers, m => m.DataContext == notSaved && m.Classes.Contains("notsaved"));
        // rating a capture without data saves it with the photo (no marker)
        var fresh = h.Library.Items.First(i => i.Item.ContextLocation == ContextLocation.None && i.Item.Rating == 0 && i.Name != "L16_00499");
        h.Library.Click(fresh, false, false);
        h.Library.SetRating(3);
        await h.SettleAsync(() => h.Lib.Get(fresh.Id)!.ContextLocation == ContextLocation.BesideCapture, waitThumbnails: false);
        Assert.Equal(RatingSource.Capture, h.Lib.Get(fresh.Id)!.RatingSource);
        // the kept-aside copy: a notice with Show
        h.Library.Click(superseded, false, false);
        await h.SettleAsync();
        Assert.True(h.Library.Details.HasSuperseded);
        h.Library.Details.ShowSupersededCommand.Execute(null);
        Assert.Contains(h.Platform.Revealed, p => p.Contains("contexts-superseded", StringComparison.Ordinal));
        h.Library.Click(notSaved, true, false);
        h.Library.Click(local, true, false);           // the cursor (details) on the photo kept on this computer
        await h.SettleAsync();
        Assert.Equal("On this computer", h.Library.Details.Current!.StorageText);
        h.Shot("22-context-markers");
    }

    // ================================================================================================================ placeholder

    [AvaloniaFact]
    public async Task Processing_image_placeholder_states()
    {
        await using var h = await Harness.CreateAsync();
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        // waiting in the Queue (not started): placeholder, no progress figure
        var waiting = h.Item("L16_00498");        // seeded: analysis waiting
        h.Library.Click(waiting, false, false);
        await h.SettleAsync();
        var preview = h.Window.GetVisualDescendants().OfType<ThumbnailView>().First(t => t.Name == "Preview");
        Assert.True(h.Library.Details.PreviewProcessing);
        Assert.Equal(ImageState.Processing, preview.State);
        Assert.True(double.IsNaN(preview.Progress));
        Assert.True(preview.ShowsLabel);
        Assert.NotNull(preview.Source);            // the blurred thumbnail underneath

        // running with progress
        var running = h.Item("L16_00501");        // seeded: analysis running at 35 %
        h.Library.Click(running, false, false);
        await h.SettleAsync();
        Assert.Equal(ImageState.Processing, preview.State);
        Assert.Equal(0.35, preview.Progress, 2);
        h.Shot("18-details-processing");
        h.Lib.FakeJobs.Tick(1);
        await h.SettleAsync(() => preview.Progress > 0.36);
        Assert.True(preview.Progress > 0.36);

        // ready: the sharp picture replaces the placeholder
        for (int i = 0; i < 60 && h.Lib.Get(running.Id)!.DepthState != DepthState.Analysed; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => !h.Library.Details.PreviewProcessing);
        Assert.Equal(ImageState.Ready, preview.State);
        Assert.Equal(ViewQuality.Linear13, running.Item.BestQuality);
    }

    [AvaloniaFact]
    public async Task Grid_thumbnails_being_made_show_the_spinner_only()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { PendingThumbnailsEvery = 3 });
        await h.SettleAsync(() => h.Services.Thumbnails.InFlight > 0 && h.Services.Thumbnails.Delivered > 10, waitThumbnails: false);
        var views = h.Browse.ActiveItems.GetVisualDescendants().OfType<ThumbnailView>().Where(t => t.IsEffectivelyVisible).ToList();
        var pending = views.Where(v => v.State == ImageState.Processing && !v.Made).ToList();   // thumbnails not made yet
        Assert.NotEmpty(pending);
        Assert.All(pending, v => { Assert.Null(v.Source); Assert.False(v.ShowsLabel); });   // small: spinner only
        Assert.Contains(views, v => v.State == ImageState.Ready);
        h.Shot("21-grid-thumbnails-processing");
        // large cells show the label too
        h.Library.ThumbSize = 240;
        await h.SettleAsync(waitThumbnails: false);
        var big = h.Browse.ActiveItems.GetVisualDescendants().OfType<ThumbnailView>().First(v => v.IsEffectivelyVisible && v.State == ImageState.Processing && !v.Made);
        Assert.True(big.ShowsLabel);
    }

    // ================================================================================================================ quality selector

    [AvaloniaFact]
    public async Task Quality_selector_upgrade_notice_dont_ask_again_and_release()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        var q = h.Library.Details.Quality;
        var item = h.Item("L16_00494");            // 13 MP kept
        h.Library.Click(item, false, false);
        await h.SettleAsync();
        Assert.True(q.IsStandard);
        Assert.StartsWith("13 MP kept · ", q.StatusText);
        h.Shot("19-quality-selector");

        // up: the notice with the extra storage
        await q.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        Assert.True(q.AskOpen);
        Assert.Matches(@"about \d+ MB more on disk", q.AskText);
        await h.SettleAsync();
        h.Shot("20-quality-upgrade-notice");
        q.CancelAskCommand.Execute(null);
        Assert.False(q.AskOpen);
        Assert.Empty(h.Lib.Jobs.Of(JobKind.CacheUpgrade));

        // up again, ticking "Don't ask again"
        await q.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        q.DontAskAgain = true;
        q.ConfirmAskCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheUpgrade).Any(), waitThumbnails: false);
        await h.SettleAsync(() => h.Lib.Settings.DontAskAboutExtraStorage, waitThumbnails: false);
        Assert.True(h.Lib.Settings.DontAskAboutExtraStorage);
        // the choice is selected at once (one mark), the kept 13 MP copy carries the tick until the change finishes
        await h.SettleAsync(() => q.IsPending && q.IsFull, waitThumbnails: false);
        Assert.True(q.IsFull);
        Assert.False(q.IsStandard);
        Assert.True(q.StandardKept);
        Assert.False(q.CanChange);
        h.Lib.FakeJobs.Tick(0.1); h.Lib.FakeJobs.Tick(2);
        await h.SettleAsync(() => q.IsRunning && q.Progress > 0, waitThumbnails: false);
        Assert.StartsWith("Full detail is being made · ", q.StatusText);
        Assert.EndsWith("13 MP kept", q.StatusText);
        for (int i = 0; i < 40; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => q.IsFull && !q.IsPending, waitThumbnails: false);
        Assert.False(q.StandardKept);
        Assert.StartsWith("Full detail kept", q.StatusText);

        // the next capture goes up without the question
        var other = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.Missing && i != item);
        h.Library.Click(other, false, false);
        await q.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        Assert.False(q.AskOpen);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheUpgrade).Count() == 2, waitThumbnails: false);

        // the question comes back from Settings
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(waitThumbnails: false);
        Assert.False(h.Vm.Settings!.AskExtraStorage);
        h.Vm.Settings.AskExtraStorage = true;
        await h.SettleAsync(() => !h.Lib.Settings.DontAskAboutExtraStorage, waitThumbnails: false);
        h.Vm.CurrentPage = Page.Library;

        // down: confirmation that it can be made again, then the release
        h.Library.Click(item, false, false);
        await h.SettleAsync(waitThumbnails: false);
        await q.ChooseCommand.ExecuteAsync(ViewQuality.Linear13);
        Assert.True(q.ReleaseOpen);
        Assert.Contains("can make it again", q.ReleaseText);
        q.ConfirmReleaseCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.CacheRelease).Any(), waitThumbnails: false);
        for (int i = 0; i < 10; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => q.IsStandard, waitThumbnails: false);
        Assert.Equal(CacheLevel.Linear13, h.Lib.Get(item.Id)!.CacheLevel);
    }

    // ================================================================================================================ fonts

    [AvaloniaFact]
    public void Bundled_faces_resolve_each_weight_to_its_own_file()
    {
        object? text = null, mono = null;
        Assert.True(Avalonia.Application.Current!.TryGetResource("LuxTextFont", null, out text));
        Assert.True(Avalonia.Application.Current!.TryGetResource("LuxMonoFont", null, out mono));
        var cases = new (Avalonia.Media.FontFamily Family, Avalonia.Media.FontWeight Weight, string Name)[]
        {
            ((Avalonia.Media.FontFamily)text!, Avalonia.Media.FontWeight.Normal, "Archivo"), ((Avalonia.Media.FontFamily)text!, Avalonia.Media.FontWeight.Medium, "Archivo"),
            ((Avalonia.Media.FontFamily)text!, Avalonia.Media.FontWeight.SemiBold, "Archivo"), ((Avalonia.Media.FontFamily)mono!, Avalonia.Media.FontWeight.Normal, "JetBrains Mono"),
            ((Avalonia.Media.FontFamily)mono!, Avalonia.Media.FontWeight.Medium, "JetBrains Mono"),
        };
        foreach (var (family, weight, name) in cases)
        {
            Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(family, Avalonia.Media.FontStyle.Normal, weight), out var gt), $"{name} {weight}");
            Assert.StartsWith(name, gt!.FamilyName);
            Assert.Equal(weight, gt.Weight);
            Assert.Equal(Avalonia.Media.FontSimulations.None, gt.FontSimulations);
        }
    }

    // ================================================================================================================ orientation, filters, queue

    [AvaloniaFact]
    public async Task Portrait_captures_lumen_filters_and_queue_thumbnails()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { Captures = 600 });
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        Assert.Equal(90, h.Item("L16_00497").Orientation);             // "show as held" is on by default
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(waitThumbnails: false);
        h.Vm.Settings!.ShowAsHeld = false;                              // as Lumen: sideways
        await h.SettleAsync(() => !h.Lib.Settings.ShowPhotosAsHeld && h.Item("L16_00497").Orientation == 0);
        Assert.Equal(0, h.Item("L16_00497").Orientation);
        h.Vm.Settings.ShowAsHeld = true;
        await h.SettleAsync(() => h.Lib.Settings.ShowPhotosAsHeld && h.Item("L16_00497").Orientation == 90);
        h.Vm.CurrentPage = Page.Library;
        await h.SettleAsync();
        var portrait = h.Item("L16_00497");
        Assert.Equal(90, portrait.Orientation);
        var cellView = h.Browse.ActiveItems.GetVisualDescendants().OfType<ThumbnailView>().First(t => t.CaptureId == portrait.Id);
        Assert.Equal(90, cellView.Orientation);
        Assert.Contains(h.Library.Tree, n => n.Label == "Has Lumen session");
        Assert.DoesNotContain(h.Library.Tree, n => n.Label == "Has Lumen edits");
        var edited = h.Library.Tree.First(n => n.Label == "Edited in Lumen");
        await h.SelectScopeAsync(edited.Key);
        Assert.Equal(edited.CountText, h.Library.TotalCount.ToString());
        Assert.All(h.Library.Items, i => Assert.True(i.Item.LumenSession >= LumenSessionState.SettingsChanged));
        // cache jobs in the Queue carry the capture's thumbnail
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync();
        var analysis = h.Vm.Queue.Jobs.First(j => j.Info.Kind == JobKind.Analysis);
        Assert.True(analysis.HasCapture);
        Assert.Contains(h.Window.GetVisualDescendants().OfType<ThumbnailView>(), t => t.CaptureId == analysis.CaptureId && t.State == ImageState.Ready);
    }
}
