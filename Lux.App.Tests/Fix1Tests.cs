using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Interface checks on the fake library (always run). Screenshots go to <c>Fix1.Shots</c>.</summary>
public class Fix1Tests
{
    static string Shot(Harness h, string name) => Harness.Capture(h.Window, name, Fix1.Shots);

    static async Task<Harness> OneSelectedAsync(string name = "L16_00493", int frameMs = 2)
    {
        var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = frameMs;
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        h.Library.Click(h.Item(name), false, false);
        await h.SettleAsync();
        return h;
    }

    /// <summary>The player never advances over a frame that is not made — it holds with the spinner and "Preparing frames ·
    /// n of m" — and plays on by itself when the frames come, also after another effect is chosen; the scrubber shows the ready range.</summary>
    [AvaloniaFact]
    public async Task Player_waits_for_frames_and_plays_on_by_itself()
    {
        await using var h = await OneSelectedAsync(frameMs: 150);
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        Assert.True(pl.IsPlaying);
        Dispatcher.UIThread.RunJobs(); h.Window.RenderFrame(); Dispatcher.UIThread.RunJobs();
        var view = h.Window.GetVisualDescendants().OfType<AnimationFrameView>().First(v => v.IsEffectivelyVisible);
        var strip = h.Window.GetVisualDescendants().OfType<FrameReadyStrip>().First(v => v.IsEffectivelyVisible);
        bool sawPreparing = false, shot = false; int badSteps = 0;
        async Task Run(Func<bool> until, int ms)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms && !until())
            {
                await h.Lib.FlushEventsAsync();
                Dispatcher.UIThread.RunJobs();
                pl.Advance();                     // as the timer would: it must refuse a frame that is not ready
                h.Window.RenderFrame();
                if (pl.Frame != null && !pl.IsUpdating && !pl.IsReadyAt(pl.Position)) badSteps++;   // the previous preview's picture may stay until the new one's first frame
                if (pl.IsPreparing && pl.HasFrame && pl.Preview is { IsComplete: false })
                {
                    sawPreparing = true;
                    Assert.StartsWith("Preparing frames · ", pl.PreparingText);
                    Assert.True(strip.ReadyCount < pl.Length);
                    if (!shot && strip.ReadyCount >= 3)
                    {
                        shot = true;
                        Assert.StartsWith("Preparing frames", view.PreparingDrawn);
                        Shot(h, "fix1-10a-player-preparing");
                    }
                }
                await Task.Delay(20);
            }
        }
        await Run(() => pl.IsComplete, 30_000);
        Assert.True(pl.IsComplete);
        Assert.True(sawPreparing, "the player never said it was preparing frames");
        Assert.Equal(0, badSteps);
        // all made: it plays on by itself
        int at = pl.Position;
        await Run(() => pl.Position != at && !pl.IsPreparing, 5000);
        Assert.False(pl.IsPreparing);
        Assert.NotEqual(at, pl.Position);
        Assert.Equal(pl.Length, strip.ReadyCount);
        Shot(h, "fix1-10b-player-playing");

        // another effect: back to preparing at once, then playing again without a click
        sawPreparing = false; shot = true;
        p.Effects.First(e => e.Effect == AnimationEffect.ApertureSweep).SelectCommand.Execute(null);
        await Run(() => pl.Preview?.Options.Effect == AnimationEffect.ApertureSweep && pl.IsComplete, 30_000);
        Assert.True(sawPreparing);
        Assert.True(pl.IsPlaying);
        at = pl.Position;
        await Run(() => pl.Position != at, 5000);
        Assert.NotEqual(at, pl.Position);
        Assert.Equal(0, badSteps);
        h.Library.CloseAnimate();
    }

    /// <summary>An effect that cannot change the picture says why in the panel.</summary>
    [AvaloniaFact]
    public async Task Animate_panel_says_why_an_effect_changes_nothing()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        await h.SettleAsync(() => p.Player.IsComplete, timeoutMs: 15000);
        Assert.Null(p.PreviewNote);
        // the same start and end: the focus does not move
        p.FocusFromText = "3"; p.FocusToText = "3";
        await h.SettleAsync(() => p.Player.Preview?.Options.FocusToMm == 3000f && p.Player.IsComplete && p.PreviewNote != null, timeoutMs: 15000);
        Assert.Contains("does not move", p.PreviewNote);
        await h.SettleAsync();
        Shot(h, "fix1-01-rack-focus-does-not-move");
        // an aperture sweep that opens to no more than the all-in-focus aperture
        p.FocusFromText = ""; p.FocusToText = "";
        p.Effects.First(e => e.Effect == AnimationEffect.ApertureSweep).SelectCommand.Execute(null);
        p.FNumber = 16;
        await h.SettleAsync(() => p.Player.Preview?.Options.Effect == AnimationEffect.ApertureSweep && p.Player.Preview.Options.FNumber == 16f && p.Player.IsComplete && p.PreviewNote != null, timeoutMs: 15000);
        Assert.Contains("all in focus", p.PreviewNote);
        h.Library.CloseAnimate();
    }

    /// <summary>Exactly one option reads as selected — the choice — with the kept copy's tick only during a change and the
    /// rest said in words. Screens of every state (details panel).</summary>
    [AvaloniaFact]
    public async Task Quality_selector_shows_one_choice_and_says_the_rest()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        var q = h.Library.Details.Quality;
        int Marks() => (q.IsPreview ? 1 : 0) + (q.IsStandard ? 1 : 0) + (q.IsFull ? 1 : 0);
        var preview = h.Library.Items.First(i => i.Item.CacheLevel <= CacheLevel.Geometry && !i.Missing && !i.IsBusy && !i.HasProblem);
        h.Library.Click(preview, false, false);
        await h.SettleAsync();
        Assert.True(q.IsPreview); Assert.Equal(1, Marks()); Assert.False(q.StandardKept || q.FullKept);
        Assert.StartsWith("Preview: made from the capture each time", q.Caption);
        Shot(h, "fix1-04a-quality-preview");
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.Missing && !i.IsBusy && !i.HasProblem);
        h.Library.Click(item, false, false);
        await h.SettleAsync();
        Assert.True(q.IsStandard); Assert.Equal(1, Marks());
        Assert.StartsWith("13 MP kept · ", q.Caption);
        Shot(h, "fix1-04b-quality-13mp-kept");
        await q.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        await h.SettleAsync(() => q.IsPending);
        for (int i = 0; i < 8; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => q.IsRunning && q.Progress > 0.2);
        Assert.True(q.IsFull); Assert.Equal(1, Marks()); Assert.True(q.StandardKept); Assert.False(q.FullKept);
        Assert.Matches(@"^Full detail is being made · \d+ % · 13 MP kept$", q.Caption);
        Shot(h, "fix1-04c-quality-full-being-made");
        for (int i = 0; i < 40; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => !q.IsPending && q.IsFull);
        Assert.Equal(1, Marks()); Assert.False(q.StandardKept || q.FullKept);
        Assert.StartsWith("Full detail kept · ", q.Caption);
        Shot(h, "fix1-04d-quality-full-kept");
        // back down: the confirmation, then the choice (13 MP) selected and the full-detail copy ticked until it is released
        await q.ChooseCommand.ExecuteAsync(ViewQuality.Linear13);
        await h.SettleAsync(() => q.ReleaseOpen);
        q.ConfirmReleaseCommand.Execute(null);
        await h.SettleAsync(() => q.IsPending);
        Assert.True(q.IsStandard); Assert.Equal(1, Marks()); Assert.True(q.FullKept);
        Assert.StartsWith("Going back to 13 MP", q.Caption);
        Shot(h, "fix1-04e-quality-going-back");
        for (int i = 0; i < 40; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => !q.IsPending);
        Assert.True(q.IsStandard); Assert.Equal(1, Marks());
    }

    /// <summary>In the viewer: the same selector (compact) and the render progress — moving with the tiles, quiet
    /// once the requested level is wholly on screen, the quality job's progress while it runs, and what it waits for when nothing moves.</summary>
    [AvaloniaFact]
    public async Task Viewer_shows_render_progress_and_what_it_waits_for()
    {
        var timings = new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 700, 500, 70, 35, 18 }, Workers = 2 };
        await using var h = await DevelopHarness.OpenAsync(new Lux.App.Fake.FakeDevelopOptions { Timings = timings, OpenDelayMs = 0, SaveDelayMs = 20 });
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        var d = h.Develop(); var v = h.Viewer();
        await h.SettlePictureAsync(() => v.TargetComplete);
        d.RefreshRender();
        Assert.False(d.IsRenderBusy);
        Assert.Contains("on screen", d.RenderProgressText);
        Shot(h, "fix1-05a-viewer-fit-on-screen");
        // 1:1: the finer tiles come one by one (0.5–0.7 s a tile on two workers here)
        d.ZoomActualCommand.Execute(null);
        v.CompleteAnimation(); Dispatcher.UIThread.RunJobs(); h.Window.RenderFrame(); Dispatcher.UIThread.RunJobs(); h.Window.RenderFrame();
        bool sawMoving = false;
        for (int i = 0; i < 600 && !(v.TargetComplete && i > 3); i++)
        {
            await h.SettleAsync(timeoutMs: 60, waitThumbnails: false);
            v.CompleteAnimation(); h.Window.RenderFrame(); d.RefreshRender();
            if (!sawMoving && v.TargetSlotsReady > 0 && v.TargetSlotsReady < v.TargetSlots && d.IsRenderBusy)
            {
                sawMoving = true;
                Assert.Matches(@"(→|Making) .* · \d+ %", d.RenderProgressText);
                Shot(h, "fix1-05b-viewer-render-progress");
            }
        }
        Assert.True(sawMoving);
        await h.SettlePictureAsync(() => v.TargetComplete, timeoutMs: 30000);
        d.RefreshRender();
        Assert.False(d.IsRenderBusy);
        Assert.Contains("on screen", d.RenderProgressText);
        // the quality being made (a photo with the 13 MP copy kept, upgraded): its job's progress
        var other = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.Missing && !i.IsBusy && !i.HasProblem);
        await d.OpenAsync(other);
        await h.SettlePictureAsync(timeoutMs: 30000);
        await d.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        await h.SettleAsync(() => d.Quality.IsPending, waitThumbnails: false);
        for (int i = 0; i < 8; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => d.Quality.IsRunning && d.Quality.Progress > 0.2, waitThumbnails: false);
        d.RefreshRender();
        Assert.Contains("being made", d.RenderProgressText);
        Assert.True(d.IsRenderBusy);
        Shot(h, "fix1-05c-viewer-full-detail-being-made");
        for (int i = 0; i < 40; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => !d.Quality.IsPending, waitThumbnails: false);
    }

    [AvaloniaFact]
    public async Task Viewer_says_what_it_waits_for_when_nothing_moves()
    {
        double was = DevelopViewModel.StuckAfterSeconds;
        try
        {
            DevelopViewModel.StuckAfterSeconds = 0.8;
            // level 0 / 1 tiles that take a minute each: the close-up never arrives during the test
            var slow = new Lux.App.Fake.FakeTimings { TileDelayMs = new[] { 60_000, 60_000, 5, 3, 1 }, Workers = 2 };
            await using var h = await DevelopHarness.OpenAsync(new Lux.App.Fake.FakeDevelopOptions { Timings = slow, OpenDelayMs = 0, SaveDelayMs = 20 });
            var d = h.Develop(); var v = h.Viewer();
            d.ZoomActualCommand.Execute(null);
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 4000 && !d.IsRenderStuck)
            {
                await h.SettleAsync(timeoutMs: 100, waitThumbnails: false);
                v.CompleteAnimation(); h.Window.RenderFrame(); d.RefreshRender();
            }
            Assert.True(d.IsRenderStuck);
            Assert.StartsWith("Waiting for ", d.RenderProgressText);
            Shot(h, "fix1-05d-viewer-waiting");
        }
        finally { DevelopViewModel.StuckAfterSeconds = was; }
    }

    /// <summary>In the Lumen import dialog every label sits on the first line of its value (same baseline, ±1 px).</summary>
    [AvaloniaFact]
    public async Task Import_dialog_facts_rows_line_up()
    {
        await using var h = await Harness.CreateAsync();
        h.Vm.OpenLumenImport(new CaptureQuery(), "~/Pictures/Light");
        var dlg = (LumenImportViewModel)h.Vm.Dialog!;
        await h.SettleAsync(() => !dlg.IsScanning);
        h.Window.RenderFrame();
        var grid = h.Window.GetVisualDescendants().OfType<Avalonia.Controls.Grid>().First(g => g.Name == "FactsGrid");
        var cells = grid.Children.OfType<Avalonia.Controls.TextBlock>().ToList();
        double Baseline(Avalonia.Controls.TextBlock tb) => tb.TranslatePoint(new Avalonia.Point(0, tb.TextLayout.Baseline), h.Window)!.Value.Y;
        for (int row = 0; row < 3; row++)
        {
            var label = cells.First(c => Avalonia.Controls.Grid.GetRow(c) == row && Avalonia.Controls.Grid.GetColumn(c) == 0);
            var value = cells.First(c => Avalonia.Controls.Grid.GetRow(c) == row && Avalonia.Controls.Grid.GetColumn(c) == 1);
            Assert.InRange(Math.Abs(Baseline(label) - Baseline(value)), 0, 1.0);
        }
        Shot(h, "fix1-11-import-dialog-aligned");
    }

    /// <summary>The first row says what it is; "Revert to original" is its own step and stays selected (the photo is
    /// the original then); "Clear history" keeps the picture and drops the steps; both are offered in the history panel.</summary>
    [AvaloniaFact]
    public async Task History_names_its_start_reverts_as_a_step_and_clears()
    {
        await using var h = await DevelopHarness.OpenAsync(new FakeDevelopOptions { Timings = FakeTimings.Fast, OpenDelayMs = 0, SaveDelayMs = 20 });
        var d = h.Develop(); var s = h.Session();
        // a photo with no saved edits: "Opened · original"
        var plain = h.Library.Items.First(i => !i.Item.Edits.IsEdited && !i.HasProblem && !i.IsBusy && i.Item.DisplayRotation == 0);
        await d.OpenAsync(plain);
        await h.SettlePictureAsync();
        s = h.Session();
        Assert.Equal("Opened · original", d.History[0].Label);
        Assert.Contains("no saved edits", d.History[0].Tip);
        Assert.False(d.CanRevert);
        Assert.False(d.CanClearHistory);
        // an edit, then revert: a step of its own, selected, and the photo is the original
        s.BeginDrag(AdjustmentKeys.Exposure); s.DragTo(AdjustmentKeys.Exposure, 0.7); s.EndDrag(AdjustmentKeys.Exposure);
        await h.SettlePictureAsync();
        Assert.True(d.CanRevert);
        d.RevertCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal("Revert to original", d.History[^1].Label);
        Assert.True(d.History[^1].IsCurrent);
        Assert.False(d.History[0].IsCurrent);
        Assert.True(s.IsOriginal);
        Assert.Equal(0, s.Get(AdjustmentKeys.Exposure), 6);
        Shot(h, "fix1-09a-history-revert-step");
        // undo brings the edit back; redo the revert
        d.UndoCommand.Execute(null); await h.SettlePictureAsync();
        Assert.Equal(0.7, s.Get(AdjustmentKeys.Exposure), 6);
        d.RedoCommand.Execute(null); await h.SettlePictureAsync();
        Assert.True(s.IsOriginal);
        // clear history: asks first, keeps the picture, drops the steps
        s.BeginDrag(AdjustmentKeys.Contrast); s.DragTo(AdjustmentKeys.Contrast, 25); s.EndDrag(AdjustmentKeys.Contrast);
        await h.SettlePictureAsync();
        Assert.True(d.CanClearHistory);
        d.ClearHistoryCommand.Execute(null);
        var dlg = Assert.IsType<ConfirmViewModel>(h.Vm.Dialog);
        await h.SettleAsync(waitThumbnails: false);
        Shot(h, "fix1-09b-history-clear-question");
        dlg.OkCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Single(d.History);
        Assert.Equal("History cleared · with edits", d.History[0].Label);
        Assert.Contains("contrast", d.History[0].Tip);
        Assert.Equal(25, s.Get(AdjustmentKeys.Contrast), 6);
        Assert.False(d.CanUndo);
        Shot(h, "fix1-09c-history-cleared");
        // a photo opened with saved edits: "Opened · with saved edits", what they are in the tip
        var edited = h.Library.Items.First(i => i.Item.Edits.HasSettings && !i.HasProblem && !i.IsBusy && i.Id != plain.Id);
        await d.OpenAsync(edited);
        await h.SettlePictureAsync();
        Assert.Equal("Opened · with saved edits", d.History[0].Label);
        Assert.StartsWith("Saved edits it opened with: ", d.History[0].Tip);
        Assert.True(d.CanRevert);
        d.RevertCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal("Revert to original", d.History[^1].Label);
        Assert.True(d.History[^1].IsCurrent);
        Assert.True(h.Session().IsOriginal);
        Shot(h, "fix1-09d-history-saved-edits-reverted");
    }

    /// <summary>Background work that is not a Queue job is listed first ("Working now"), named after its photo, with its
    /// progress and Stop where possible; the tab counts it.</summary>
    [AvaloniaFact]
    public async Task Queue_lists_activities_named_after_their_photo()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        var item = h.Library.Items.First(i => !i.HasProblem);
        bool stopped = false;
        long a1 = h.Lib.StartActivity(item.Id, $"Rebuilding the history of {item.Name}", "Rebuilding history 2 of 5");
        long a2 = h.Lib.StartActivity(item.Id, $"Animation preview of {item.Name} · Rack focus", "Preparing frames · 6 of 24", () => stopped = true);
        h.Lib.UpdateActivity(a2, "Preparing frames · 6 of 24", 0.25);
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync(() => h.Vm.Queue.Activities.Count == 2);
        var rows = h.Vm.Queue.Rows;
        Assert.Equal("Working now · 2", Assert.IsType<QueueGroupViewModel>(rows[0]).Label);
        var r2 = h.Vm.Queue.Activities.First(a => a.Info.Id == a2);
        Assert.Equal(item.Name, r2.Name);
        Assert.Equal("Preparing frames · 6 of 24 · 25 %", r2.StatusText);
        Assert.True(r2.CanCancel);
        Assert.False(h.Vm.Queue.Activities.First(a => a.Info.Id == a1).CanCancel);
        Assert.StartsWith("Queue · ", h.Vm.Queue.TabLabel);
        Shot(h, "fix1-06-queue-activities");
        r2.StopCommand.Execute(null);
        await h.SettleAsync(() => stopped);
        Assert.True(stopped);
        h.Lib.EndActivity(a1); h.Lib.EndActivity(a2);
        await h.SettleAsync(() => h.Vm.Queue.Activities.Count == 0);
        Assert.DoesNotContain(h.Vm.Queue.Rows, r => r is ActivityRowViewModel);
    }

    /// <summary>The library's measured notes: identical end frames, a gentle change, a clear change.</summary>
    [Fact]
    public void Preview_advice_from_the_end_frames()
    {
        PreviewFrame F(int k, byte v, int changed = 0)
        {
            var px = new byte[100 * 100 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 100; px[i + 3] = 255; }
            for (int i = 0; i < changed; i++) px[i * 4] = v;
            return new PreviewFrame(k, 100, 100, px, "");
        }
        var o = new AnimationOptions { Effect = AnimationEffect.RackFocus, FNumber = 2 };
        var plan = new AnimationPlan
        {
            Effect = AnimationEffect.RackFocus, Order = new[] { 0, 1 }, DelaysMs = new[] { 70, 70 }, Size = (100, 100),
            Frames = new[] { new AnimationFrameInfo(0, "a") { FNumber = 2, FocusMm = 1500 }, new AnimationFrameInfo(1, "b") { FNumber = 2, FocusMm = 9000 } },
        };
        Assert.Contains(AnimationPreviews.Advice(o, plan, 15f, F(0, 100), F(1, 100)), n => n.Contains("looks the same"));
        Assert.Contains(AnimationPreviews.Advice(o, plan, 15f, F(0, 100), F(1, 200, 150)), n => n.Contains("gentle effect") && n.Contains("about 2 %"));
        Assert.Empty(AnimationPreviews.Advice(o, plan, 15f, F(0, 100), F(1, 200, 2000)));
        var far = plan with { Frames = new[] { new AnimationFrameInfo(0, "a") { FNumber = 2, FocusMm = 60_000 }, new AnimationFrameInfo(1, "b") { FNumber = 2, FocusMm = 61_440 } } };
        Assert.Contains(AnimationPreviews.Advice(o, far, 15f, null, null), n => n.Contains("hardly moves"));
    }
}

/// <summary>The held turn the CLI's <c>--upright</c> applies (constants in Lux.Store) is bit for bit Lumen's own rotate as the library
/// makes it ("Rotate as the camera was held" on the identity of a 4:3 capture — every L16 capture is 4:3).</summary>
public class UprightTransformTests
{
    [Fact]
    public void Store_constants_are_Lumens_rotate()
    {
        foreach (int deg in new[] { 90, 180, 270 })
        {
            var s = new Lux.Library.Edit.PhotoEditImpl.State { Geo = new Lux.Library.Edit.LumenGeometryEdits(Lux.Library.Edit.LumenTransform.Identity(4, 3)) };
            Lux.Library.Edit.PhotoEditImpl.Turn(s, deg);
            var lib = s.Geo.T.ToExport();
            string Bits(Lux.Engine.Pipeline.Export.ExportTransform t) => string.Join(",", t.Matrix.Select(v => BitConverter.SingleToInt32Bits(v).ToString("x8")))
                + ";" + string.Join(",", new[] { t.CropX0, t.CropY0, t.CropX1, t.CropY1 }.Select(v => BitConverter.SingleToInt32Bits(v).ToString("x8"))) + $" {t.AspectW}:{t.AspectH}";
            Console.WriteLine($"{deg}: {Bits(lib)}");
            if (Environment.GetEnvironmentVariable("LUX_PRINT_TURNS") == "1") continue;
            var store = Lux.Store.HeldTurn.Transform(4, 3, deg);
            Assert.NotNull(store);
            Assert.Equal(Bits(lib), Bits(store!));
        }
        Assert.Null(Lux.Store.HeldTurn.Transform(4, 3, 0));
        Assert.Null(Lux.Store.HeldTurn.Transform(16, 9, 90));
    }
}
