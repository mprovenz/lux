using Avalonia.Headless.XUnit;
using Lux.App.ViewModels;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>Export and animation on the fake library: the Animate panel (effects with reasons, parameters, focus picked on the photo, preview player,
/// estimate, Add to queue → one job per photo named after its file, batches as one group), the export panel's additions (PPM, custom long
/// edge, tone style, file names, the collision question, the per-session memory) and the Queue's batch groups. Screenshots go to the
/// workspace's output folder.</summary>
public class AnimateTests
{
    static string Shots => Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "ui", "shots");
    static string Shot(Harness h, string name) => Harness.Capture(h.Window, name, Shots);

    static async Task<Harness> OneSelectedAsync(string name = "L16_00493")
    {
        var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 2;
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        h.Library.Click(h.Item(name), false, false);
        await h.SettleAsync();
        return h;
    }

    static async Task WaitPreviewAsync(Harness h, AnimatePanelViewModel p) =>
        await h.SettleAsync(() => p.Player.IsComplete && p.Player.HasFrame && !p.Estimate.StartsWith("Estimating", StringComparison.Ordinal), timeoutMs: 15000);

    [AvaloniaFact]
    public async Task Animate_panel_opens_with_effects_preview_and_estimate()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        Assert.True(h.Library.IsAnimateOpen);
        Assert.False(h.Library.IsExportOpen);
        Assert.Equal("Animate L16_00493", p.Title);
        Assert.Equal(new[] { "Rack focus", "Aperture sweep", "Wigglegram", "Parallax", "Lens tour" }, p.Effects.Select(e => e.Name));
        Assert.True(p.IsRack);
        await WaitPreviewAsync(h, p);
        Assert.True(p.Player.Length >= 24);                  // 24 frames played forward and back
        Assert.Equal(46, p.Player.Length);
        Assert.StartsWith("About ", p.Estimate);
        p.Player.IsPlaying = false;
        p.Player.Position = 12;
        await h.SettleAsync();
        Shot(h, "01-animate-panel");

        // another effect: its own settings and a new preview
        p.Effects.First(e => e.Effect == AnimationEffect.ApertureSweep).SelectCommand.Execute(null);
        Assert.True(p.IsAperture);
        Assert.Equal(2.0, p.FNumber, 3);
        await h.SettleAsync(() => p.Player.Preview?.Options.Effect == AnimationEffect.ApertureSweep && p.Player.IsComplete, timeoutMs: 15000);
        Assert.Equal("all in focus", p.Player.Preview!.Frame(0)!.Label);
        // Escape-equivalent close: the panel goes, the details come back
        h.Library.CloseAnimate();
        Assert.False(h.Library.IsAnimateOpen);
    }

    [AvaloniaFact]
    public async Task Rack_focus_start_and_end_are_picked_on_the_photo()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        await WaitPreviewAsync(h, p);
        p.PickFromCommand.Execute(null);
        Assert.Equal(PickTarget.FocusFrom, p.Pick);
        Assert.True(p.Player.IsPicking);
        Assert.Equal("Click the photo where the focus starts", p.Player.PickHint);
        p.ClickPicture(0.5, 0.85);                            // the near ground: 10 − 9 × 0.85 = 2.35 m in the fake scene
        await h.SettleAsync(() => p.FocusFromText.Length > 0);
        Assert.Equal("2.35", p.FocusFromText);
        Assert.False(p.Player.IsPicking);
        p.PickToCommand.Execute(null);
        await h.SettleAsync();
        Shot(h, "02-rack-focus-picking");
        p.ClickPicture(0.5, 0.15);                            // the far sky: 8.65 m
        await h.SettleAsync(() => p.FocusToText.Length > 0);
        Assert.Equal("8.65", p.FocusToText);
        Assert.Equal(new[] { "Start", "End" }, p.Player.Markers.Select(m => m.Label));
        var o = p.Options();
        Assert.Equal(2350f, o.FocusFromMm!.Value, 1);
        Assert.Equal(8650f, o.FocusToMm!.Value, 1);
        // a typed value works too, and a bad one is refused at Add to queue
        p.FocusToText = "4.5";
        Assert.Equal(4500f, p.Options().FocusToMm!.Value, 1);
        p.FocusToText = "far";
        p.AddToQueueCommand.Execute(null);
        Assert.Equal("Type distances in metres, e.g. 1.5", p.Error);
        Assert.True(h.Library.IsAnimateOpen);
    }

    [AvaloniaFact]
    public async Task Preview_player_plays_scrubs_and_loops()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        await WaitPreviewAsync(h, p);
        pl.IsPlaying = false;
        pl.Position = 0;
        pl.Advance(); pl.Advance();
        Assert.Equal(2, pl.Position);
        Assert.Equal("3 / 46", pl.Counter);
        pl.StepBackCommand.Execute(null);
        Assert.Equal(1, pl.Position);
        pl.Position = pl.MaxPosition;
        pl.Loop = true; pl.Advance();
        Assert.Equal(0, pl.Position);                          // loops
        pl.Position = pl.MaxPosition; pl.Loop = false; pl.IsPlaying = true; pl.Advance();
        Assert.False(pl.IsPlaying);                            // stops at the end without loop
        Assert.Equal(pl.MaxPosition, pl.Position);
        pl.Loop = true;
        pl.Position = 30;
        Assert.StartsWith("f/2 · focus", pl.FrameLabel);
        await h.SettleAsync();
        Shot(h, "03-preview-playing");
    }

    [AvaloniaFact]
    public async Task Preview_shows_processing_until_the_first_frame()
    {
        await using var h = await OneSelectedAsync();
        h.Lib.FakeFrameMs = 5000;   // the first frame takes 5 s: long enough to look at the waiting state on a loaded machine
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        await h.SettleAsync(() => p.Player.Preview?.IsPlanned == true, timeoutMs: 3000);
        Assert.True(p.Player.IsWaiting);
        Assert.Equal("Processing image", p.Player.WaitingLabel);
        Shot(h, "05-preview-processing");
        await h.SettleAsync(() => p.Player.HasFrame, timeoutMs: 15000);
        Assert.True(p.Player.HasFrame);
    }

    [AvaloniaFact]
    public async Task Add_to_queue_makes_one_job_per_photo_in_one_group_named_after_files()
    {
        await using var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 2;
        await h.MockupSelectionAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        Assert.Equal("Animate 3 photos", p.Title);
        await WaitPreviewAsync(h, p);
        p.Format = p.Formats.First(f => f.Value == (int)AnimationFormat.WebP);
        p.Size = p.Sizes.First(s => s.Value == 1600);
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        p.AddToQueueCommand.Execute(null);
        Assert.False(h.Library.IsAnimateOpen);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Animation).Count(j => j.Id > maxBefore) == 3);
        var jobs = h.Lib.Jobs.Of(JobKind.Animation).Where(j => j.Id > maxBefore).ToList();
        Assert.Single(jobs.Select(j => j.BatchId).Distinct());
        Assert.NotNull(jobs[0].BatchId);
        Assert.All(jobs, j => Assert.EndsWith("_rack-focus.webp", j.PlannedFile));
        Assert.Equal(1600, p.LastOptions!.LongEdge);
        h.Vm.CurrentPage = Page.Queue;
        await h.SettleAsync(() => h.Vm.Queue.Batches.Any(b => b.Jobs.Count == 3 && b.Title.StartsWith("Animation of 3 photos", StringComparison.Ordinal)));
        var batch = h.Vm.Queue.Batches.First(b => b.Title.StartsWith("Animation of 3", StringComparison.Ordinal));
        Assert.All(batch.Jobs, j => Assert.Contains("_rack-focus.webp", j.FileLine));
        Assert.All(batch.Jobs, j => Assert.True(j.InBatch));
        Assert.Equal("Cancel all", batch.PrimaryAction);
        // run it: the batch reports its files done
        h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync();
        Shot(h, "04-queue-animation-job");
        for (int i = 0; i < 400 && h.Lib.Jobs.Status.Running + h.Lib.Jobs.Status.Waiting > 0; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => batch.IsFinished || h.Vm.Queue.Batches.First(b => b.Id == batch.Id).IsFinished);
        var done = h.Vm.Queue.Batches.First(b => b.Id == batch.Id);
        Assert.StartsWith("3 of 3 done", done.Summary);
        Assert.Equal("Remove", done.PrimaryAction);
    }

    [AvaloniaFact]
    public async Task Telephoto_photo_cannot_wiggle_and_says_why()
    {
        await using var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 2;
        var tele = h.Library.Items.FirstOrDefault(i => i.Item.IsTelephoto);
        if (tele == null) { await h.SelectScopeAsync(h.Library.Tree.First(n => n.Key.StartsWith("root", StringComparison.Ordinal) || n.Label.Contains("All", StringComparison.Ordinal)).Key); tele = h.Library.Items.First(i => i.Item.IsTelephoto); }
        h.Library.Click(tele, false, false);
        await h.SettleAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var w = p.Effects.First(e => e.Effect == AnimationEffect.Wigglegram);
        Assert.False(w.IsAvailable);
        Assert.Contains("Wigglegrams need a wide (28 mm) photo", w.Reason);
        w.SelectCommand.Execute(null);                         // not selectable
        Assert.NotEqual(AnimationEffect.Wigglegram, p.Selected.Effect);
        var px = p.Effects.First(e => e.Effect == AnimationEffect.ParallaxSweep);
        Assert.True(px.IsAvailable);
        Assert.Contains("drawn from one view", px.Note);
        await h.SettleAsync();
        Shot(h, "06-telephoto-effects");
    }

    [AvaloniaFact]
    public async Task Video_is_offered_only_with_ffmpeg()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        Assert.Contains(h.Library.AnimatePanel!.Formats, f => f.Value == (int)AnimationFormat.Mp4);
        Assert.False(h.Library.AnimatePanel.HasVideoNote);
        h.Library.CloseAnimate();
        h.Lib.FakeVideo = false;
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        Assert.DoesNotContain(p.Formats, f => f.Value is (int)AnimationFormat.Mp4 or (int)AnimationFormat.WebM);
        Assert.True(p.HasVideoNote);
        Assert.Contains("ffmpeg", p.VideoNote);
    }

    [AvaloniaFact]
    public async Task Animate_from_Develop_plays_over_the_picture()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Lib.FakeFrameMs = 2;
        var d = h.Develop();
        d.OpenAnimateCommand.Execute(null);
        Assert.True(d.IsAnimateOpen);
        Assert.True(d.IsSidePanelOpen);
        var p = d.AnimatePanel!;
        await h.SettleAsync(() => p.Player.IsComplete && p.Player.HasFrame, timeoutMs: 15000, waitThumbnails: false);
        p.Effects.First(e => e.Effect == AnimationEffect.Wigglegram).SelectCommand.Execute(null);
        await h.SettleAsync(() => p.Player.Preview?.Options.Effect == AnimationEffect.Wigglegram && p.Player.IsComplete, timeoutMs: 15000, waitThumbnails: false);
        Assert.Equal(6, p.Player.Length);                      // four lenses, forward and back
        p.Player.IsPlaying = false; p.Player.Position = 2;
        await h.SettleAsync(waitThumbnails: false);
        Shot(h, "07-animate-develop-wigglegram");
        int before = h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Animation);
        p.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Animation) == before + 1, waitThumbnails: false);
        Assert.False(d.IsAnimateOpen);
        Assert.Contains(h.Session().Save.Status, new[] { Lux.App.Develop.SaveStatus.Saved, Lux.App.Develop.SaveStatus.Nothing });   // saved (or nothing to save) before the job
    }

    // ================================================================================================================ export panel

    [AvaloniaFact]
    public async Task Export_panel_offers_ppm_custom_size_tone_style_names_and_remembers()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        Assert.Equal(new[] { "dng", "jpg", "jpg+depth", "hdr", "ppm", "depth" }, panel.Formats.Select(f => f.Key));
        Assert.DoesNotContain(panel.Formats, f => f.Key is "wiggle" or "rack");
        Assert.True(panel.CanAnimate);
        panel.Formats.First(f => f.Key == "dng").IsOn = false;
        panel.Formats.First(f => f.Key == "ppm").IsOn = true;
        panel.Size = panel.Sizes.First(s => s.Value == -1);
        Assert.True(panel.IsCustomSize);
        panel.CustomEdge = "1600";
        panel.ToneStyle = panel.ToneChoices[1];
        panel.FileName = "{date}_{name}";
        Assert.StartsWith("e.g. ", panel.FileNamePreview);
        Assert.Matches(@"^e\.g\. \d{4}-\d\d-\d\d_L16_\d+\.jpg$", panel.FileNamePreview);
        Assert.Contains("about", panel.Estimate);
        h.Platform.NextFolder = "/home/you/Pictures/Holiday";
        panel.ChooseFolderCommand.Execute(null);
        await h.SettleAsync(() => panel.Folder == "/home/you/Pictures/Holiday");
        Shot(h, "10-export-panel-m4");
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Count(j => j.Id > maxBefore) == 3);
        var o = panel.LastOptions!;
        Assert.Equal(new[] { "jpg", "ppm" }, o.Formats);
        Assert.Equal(1600, o.LongEdge);
        Assert.True(o.NewerToneStyle);
        Assert.Equal("{date}_{name}", o.FileNamePattern);
        Assert.Equal("/home/you/Pictures/Holiday", o.OutputFolder);
        Assert.Equal(ExistingFilePolicy.Rename, o.IfExists);
        // the batch: one group in the Queue, a row per photo
        var jobs = h.Lib.Jobs.Of(JobKind.Export).Where(j => j.Id > maxBefore).ToList();
        Assert.Single(jobs.Select(j => j.BatchId).Distinct());
        Assert.StartsWith("Export of 3 photos", jobs[0].BatchTitle);
        // reopened in this run: the same choices
        h.Library.OpenExport();
        var again = h.Library.ExportPanel!;
        Assert.Equal("/home/you/Pictures/Holiday", again.Folder);
        Assert.True(again.IsCustomSize);
        Assert.Equal("1600", again.CustomEdge);
        Assert.False(again.Formats.First(f => f.Key == "dng").IsOn);
        Assert.True(again.Formats.First(f => f.Key == "ppm").IsOn);
        Assert.Equal("{date}_{name}", again.FileName);
        // Animate… switches panels with the same photos
        again.AnimateCommand.Execute(null);
        Assert.False(h.Library.IsExportOpen);
        Assert.True(h.Library.IsAnimateOpen);
        Assert.Equal("Animate 3 photos", h.Library.AnimatePanel!.Title);
    }

    [AvaloniaFact]
    public async Task Existing_files_are_never_replaced_silently()
    {
        await using var h = await OneSelectedAsync();
        h.Lib.FakeSettings.ExportIfExists = ExportCollisionSetting.Ask;
        h.Lib.ExistingExportFiles.Add("/home/you/Pictures/Lux Export/L16_00493.jpg");
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        panel.Folder = "/home/you/Pictures/Lux Export";
        foreach (var f in panel.Formats) f.IsOn = f.Key == "jpg";
        Assert.Contains("asked first", panel.CollisionNote);
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        // the real dialog: shown over the window, answered with "Replace"
        var add = panel.AddToQueueCommand.ExecuteAsync(null);
        await h.SettleAsync(() => h.Vm.Dialog is ChoiceDialogViewModel);
        var dialog = (ChoiceDialogViewModel)h.Vm.Dialog!;
        Assert.Equal("This photo's files are already in the folder", dialog.Title);
        Assert.Equal(new[] { "Skip those", "Replace", "Keep both" }, dialog.Choices.Select(c => c.Label));
        Shot(h, "11-export-collision-question");
        dialog.Choices.First(c => c.Label == "Replace").ChooseCommand.Execute(null);
        await add;
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Export).Any(j => j.Id > maxBefore));
        Assert.Equal(ExistingFilePolicy.Overwrite, panel.LastOptions!.IfExists);
        Assert.Null(h.Vm.Dialog);
        // cancelling the question queues nothing
        h.Library.OpenExport();
        var p2 = h.Library.ExportPanel!;
        p2.Folder = "/home/you/Pictures/Lux Export";
        long max2 = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        var add2 = p2.AddToQueueCommand.ExecuteAsync(null);
        await h.SettleAsync(() => h.Vm.Dialog is ChoiceDialogViewModel);
        ((ChoiceDialogViewModel)h.Vm.Dialog!).CancelCommand.Execute(null);
        await add2;
        await h.SettleAsync();
        Assert.DoesNotContain(h.Lib.Jobs.GetJobs(), j => j.Id > max2);
        Assert.True(h.Library.IsExportOpen);
        // the default setting adds a number, never asks
        h.Lib.FakeSettings.ExportIfExists = ExportCollisionSetting.AddNumber;
        p2.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs().Any(j => j.Id > max2));
        Assert.Equal(ExistingFilePolicy.Rename, p2.LastOptions!.IfExists);
    }

    // ================================================================================================================ follow-ups (held turn, queue names)

    /// <summary>Exports follow the automatic upright view: a photo shown the way the camera was held is
    /// previewed — and animated — upright with nothing to keep or save; "As captured" gives the frames as the camera recorded them.</summary>
    [AvaloniaFact]
    public async Task Preview_of_a_photo_held_turned_is_upright_and_can_be_animated_as_captured()
    {
        await using var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 2;
        var held = h.Library.Items.FirstOrDefault(i => i.Item.DisplayRotation == 90 && !i.HasProblem && !i.IsBusy)
                   ?? h.Library.Items.First(i => i.Item.DisplayRotation != 0 && !i.HasProblem && !i.IsBusy);
        int turn = held.Item.DisplayRotation;
        h.Library.Click(held, false, false);
        await h.SettleAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        await WaitPreviewAsync(h, p);
        // the frames carry the turn: drawn as they are, upright (portrait for a quarter turn); the orientation choice is offered
        Assert.Equal(0, p.Player.Rotation);
        Assert.Equal(turn, p.Player.PlaceholderRotation);
        Assert.True(p.HasTurnedPhotos);
        Assert.Equal(0, p.Orientation.Value);
        var f = p.Player.Preview!.Frame(0)!;
        if (turn is 90 or 270) Assert.True(f.Height > f.Width);
        // picking on the upright picture: a point near the bottom is near the camera (1 m at the bottom, 10 m at the top)
        p.PickFromCommand.Execute(null);
        p.ClickPicture(0.5, 0.85);
        await h.SettleAsync(() => p.FocusFromText.Length > 0);
        Assert.Equal("2.35", p.FocusFromText);
        var m = p.Player.Markers.Single();
        Assert.Equal(0.5, m.X, 6); Assert.Equal(0.85, m.Y, 6);
        p.Player.IsPlaying = false; p.Player.Position = 8;
        await h.SettleAsync();
        Shot(h, "08-animate-held-upright");
        // as captured: the frames as the camera recorded them (sideways), nothing saved
        p.Orientation = p.OrientationChoices[1];
        await h.SettleAsync(() => p.Player.Preview?.Options.Unturned == true && p.Player.IsComplete && p.Player.HasFrame, timeoutMs: 15000);
        var g = p.Player.Preview!.Frame(0)!;
        Assert.True(g.Width > g.Height);
        Assert.Equal(turn, h.Lib.Get(held.Id)!.DisplayRotation);
        Assert.False(h.Lib.Get(held.Id)!.GeometryDecided);
        p.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => p.LastOptions != null);
        Assert.True(p.LastOptions!.Unturned);
        Shot(h, "09-animate-held-as-captured");
    }

    [AvaloniaFact]
    public async Task Effect_notes_follow_the_photo_after_its_analysis()
    {
        await using var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 2;
        var raw = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.None && !i.Item.IsTelephoto && !i.HasProblem && !i.IsBusy);
        h.Library.Click(raw, false, false);
        await h.SettleAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        Assert.Contains("computed first", p.EffectNote);
        // the analysis finishes elsewhere; the panel re-reads the photo when its preview completes
        h.Lib.SetCacheLevelForTests(raw.Id, CacheLevel.Linear13);
        p.FNumber = 3.2;   // a new preview
        await h.SettleAsync(() => p.Player.IsComplete && p.EffectNote == null, timeoutMs: 15000);
        Assert.Null(p.EffectNote);
    }
}
