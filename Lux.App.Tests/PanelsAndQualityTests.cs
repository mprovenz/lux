using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Fake;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// The Develop tools against the side panels and page changes, the Library's bars against the Animate preview, the quality selector when
/// a quality job ends, before / after with no edits, the depth card when another job makes the depth map, and the two viewers of Photo +
/// depth on one request at a time — on the fake library.
/// </summary>
public sealed class PanelsAndQualityTests
{
    static bool Named(Control c, string name) => Avalonia.Automation.AutomationProperties.GetName(c) == name;

    static bool Shown(Control host, Func<Border, bool> match) =>
        host.GetVisualDescendants().OfType<Border>().Where(match).Any(b => b.IsEffectivelyVisible && b.Bounds.Height > 0);

    /// <summary>A bar of the Library page itself (the animation player's own bar has the same style).</summary>
    static bool LibraryBar(Border b, string style) => b.Classes.Contains(style) && b.FindAncestorOfType<AnimationPlayer>() == null;

    static async Task Use(Harness h, PageTool tool)
    {
        var d = h.Develop();
        if (d.Depth.Tool != tool) d.ToolCommand.Execute(d.Tools.First(t => t.Kind == tool && !t.IsQuickSelect));
        await h.SettlePictureAsync();
        Assert.Equal(tool, d.Depth.Tool);
    }

    /// <summary>A tool and a side panel are never open together: opening the Export or Animate panel ends the tool (an open crop is applied),
    /// choosing a tool closes the side panel, and leaving the Develop page ends the tool.</summary>
    [AvaloniaFact]
    public async Task A_tool_and_a_side_panel_are_never_open_together()
    {
        await using var h = await DevelopHarness.OpenAsync(new FakeDevelopOptions { Timings = FakeTimings.Fast, OpenDelayMs = 0, SaveDelayMs = 20 });
        var d = h.Develop();
        var page = h.DevelopView();
        bool FocusBar() => Shown(page, b => Named(b, "Focus options"));

        await Use(h, PageTool.Focus);
        Assert.True(FocusBar());
        d.OpenAnimateCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(d.IsAnimateOpen);
        Assert.Equal(PageTool.None, d.Depth.Tool);
        Assert.False(FocusBar());
        Assert.False(Shown(page, b => b.Classes.Contains("toolline")));

        // choosing a tool closes the side panel (its preview covers the picture)
        await Use(h, PageTool.Focus);
        Assert.False(d.IsAnimateOpen);
        Assert.True(FocusBar());

        // the crop tool: opening Export applies the open crop
        d.Depth.LeaveToolCommand.Execute(null);
        d.CropCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(d.IsCropping);
        d.CropFrameDragged(new NormRect(0.1, 0.1, 0.8, 0.8), false);
        int steps = h.Session().History.Count;
        d.OpenExportCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(d.IsExportOpen);
        Assert.False(d.IsCropping);
        Assert.False(Shown(page, b => Named(b, "Crop options")));
        Assert.Equal(steps + 1, h.Session().History.Count);
        Assert.True(h.Session().Geometry.HasCrop);
        d.CropCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.False(d.IsExportOpen);
        Assert.True(d.IsCropping);
        d.CancelCrop();

        // a change of page ends the tool: on return no option bar is left
        foreach (var other in new[] { Page.Library, Page.Queue, Page.Camera, Page.Settings })
        {
            await Use(h, PageTool.BlendArea);
            h.Vm.CurrentPage = other;
            await h.SettleAsync();
            h.Vm.CurrentPage = Page.Develop;
            await h.SettlePictureAsync();
            Assert.Equal(PageTool.None, d.Depth.Tool);
            Assert.False(Shown(page, b => Named(b, "Brush options")));
        }
    }

    /// <summary>The Library's bars act on the grid: they give way while the Animate panel's preview covers it, and come back after.</summary>
    [AvaloniaFact]
    public async Task The_library_bars_give_way_to_the_animate_preview()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        var lib = h.Window.FindControl<LibraryView>("LibraryPage")!;
        var item = h.Library.Items.First(i => !i.HasProblem);
        h.Library.Click(item, false, false);
        await h.SettleAsync();
        Assert.True(Shown(lib, b => LibraryBar(b, "selectionbar")));
        Assert.True(h.Vm.ShowLibraryTools);
        h.Library.OpenAnimate();
        await h.SettleAsync();
        Assert.True(h.Library.IsAnimateOpen);
        foreach (var bar in new[] { "selectionbar", "toolbar", "chipbar" }) Assert.False(Shown(lib, b => LibraryBar(b, bar)), bar);
        Assert.False(h.Vm.ShowLibraryTools);
        Assert.False(h.Window.FindControl<TextBox>("FilterBox")!.IsEffectivelyVisible);
        h.Library.CloseAnimate();
        await h.SettleAsync();
        foreach (var bar in new[] { "selectionbar", "toolbar", "chipbar" }) Assert.True(Shown(lib, b => LibraryBar(b, bar)), bar);
        Assert.True(h.Vm.ShowLibraryTools);
        // the Export panel keeps the grid on screen: its bars stay
        h.Library.OpenExport();
        await h.SettleAsync();
        Assert.True(Shown(lib, b => LibraryBar(b, "selectionbar")));
    }

    /// <summary>The selector on the Develop page follows the capture when a quality job ends, also when the library reports the capture's
    /// new state a moment after the job's own end: once full detail is kept, nothing marks 13 MP and nothing says a change runs. While the
    /// change runs, the compact selector's label says what is being made.</summary>
    [AvaloniaFact]
    public async Task The_develop_selector_follows_the_capture_when_its_job_ends()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false, CaptureEventDelayMs = 1500 });
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.IsBusy && !i.HasProblem);
        h.Library.Click(item, false, false);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        var d = h.Develop();
        var q = d.Quality;
        var shownLabel = h.DevelopView().GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "CompactLabelText");
        Assert.True(q.IsStandard);
        Assert.Equal("Quality", shownLabel.Text);

        // chosen on the Library page, followed on the Develop page
        await h.Library.Details.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        await h.SettleAsync(() => q.IsPending);
        for (int i = 0; i < 8; i++) h.Lib.FakeJobs.Tick(0.5);
        await h.SettleAsync(() => q.IsRunning && q.Progress > 0.2);
        Assert.True(q.IsFull);
        Assert.True(q.StandardKept);
        Assert.Matches(@"^Full detail is being made · \d+ %$", shownLabel.Text);

        // the job ends; the capture's new state is reported later
        var job = h.Lib.Jobs.GetJobs().First(j => j.CaptureId == item.Id && j.Kind == JobKind.CacheUpgrade);
        for (int i = 0; i < 80 && h.Lib.Jobs.Get(job.Id) is { IsFinished: false }; i++) h.Lib.FakeJobs.Tick(0.5);
        Assert.True(h.Lib.Jobs.Get(job.Id)!.IsFinished);
        await h.SettleAsync(timeoutMs: 400, waitThumbnails: false);
        Assert.Equal(ViewQuality.FullDetail, h.Lib.Get(item.Id)!.BestQuality);
        var buttons = h.DevelopView().GetVisualDescendants().OfType<QualitySelector>().First(c => c.Name == "ViewerQuality").GetVisualDescendants().OfType<Button>().ToList();
        bool Ticked(string option) => buttons.First(b => b.Name == option).GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.Classes.Contains("kepttick") && p.IsEffectivelyVisible);
        void AssertKeptFull()
        {
            Assert.True(q.IsFull);
            Assert.False(q.IsPending);
            Assert.False(q.StandardKept || q.FullKept);
            Assert.Contains("FullOption", buttons.Where(b => b.Classes.Contains("selected")).Select(b => b.Name));
            Assert.False(Ticked("StandardOption") || Ticked("FullOption"));
            Assert.Equal("Quality", shownLabel.Text);
            Assert.False(d.IsProcessing);
        }
        AssertKeptFull();
        // the late report changes nothing on screen
        await h.SettleAsync(timeoutMs: 2500, waitThumbnails: false);
        await h.SettleAsync(timeoutMs: 500, waitThumbnails: false);
        AssertKeptFull();
    }

    // ================================================================================================================ depth and the kept data

    /// <summary>Tick the fake queue until the capture has no unfinished job.</summary>
    static async Task RunJobsOf(Harness h, string id)
    {
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs().Any(j => j.CaptureId == id && !j.IsFinished), timeoutMs: 3000, waitThumbnails: false);
        for (int i = 0; i < 200 && h.Lib.Jobs.GetJobs().Any(j => j.CaptureId == id && !j.IsFinished); i++) h.Lib.FakeJobs.Tick(0.5);
        Assert.DoesNotContain(h.Lib.Jobs.GetJobs(), j => j.CaptureId == id && !j.IsFinished);
    }

    /// <summary>A depth map made while the photo is open by a job that did not start from the depth card (a quality chosen on the Develop
    /// page) reaches the page: the card goes, the tools and the depth effect become available and Photo + depth shows the depth. The unsaved
    /// edit and its step stay. A later release of the kept data leaves the page with the depth it holds.</summary>
    [AvaloniaFact]
    public async Task The_depth_card_follows_a_depth_map_made_by_another_job()
    {
        await using var h = await Harness.CreateAsync(new FakeLibraryOptions { SeedJobs = false });
        h.Lib.Settings.DontAskAboutExtraStorage = true;
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.None && i.Item.DepthState == DepthState.NotAnalysed && !i.IsBusy && !i.HasProblem);
        h.Library.Click(item, false, false);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        var d = h.Develop();
        Assert.True(d.ShowDepthCard);
        Assert.False(d.Depth.IsReady);
        Assert.Contains(d.Tools, t => t.IsDimmed);
        d.Session!.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, 0.5);
        await h.SettlePictureAsync();
        int steps = d.Session.History.Count;

        await d.Quality.ChooseCommand.ExecuteAsync(ViewQuality.Linear13);
        if (d.Quality.Notice is { IsUpgrade: true }) d.Quality.ConfirmAskCommand.Execute(null);
        await RunJobsOf(h, item.Id);
        await h.SettlePictureAsync(() => d.Depth.IsReady);
        Assert.True(d.Depth.IsReady, "the depth is ready after the quality job");
        Assert.False(d.ShowDepthCard);
        Assert.DoesNotContain(d.Tools, t => t.IsDimmed);
        Assert.True(d.Depth.Aperture.IsEnabled);
        Assert.Equal(0.5, d.Session.Get(Lux.App.Develop.AdjustmentKeys.Exposure), 6);
        Assert.Equal(steps, d.Session.History.Count);
        Assert.True(d.CanUndo);

        // Photo + depth shows the depth map
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        var dv = h.DevelopView().DepthPhotoViewer;
        await h.SettlePictureAsync(() => dv.TargetComplete);
        Assert.True(dv.TargetComplete, "the depth side of Photo + depth is on screen");
        d.SetModeCommand.Execute(DevelopMode.Single);

        // the kept data released to none while the photo is open: the page keeps the depth it holds, and that depth works
        h.Lib.SetCacheLevel(new[] { item.Id }, CacheLevel.None);
        await RunJobsOf(h, item.Id);
        await h.SettlePictureAsync();
        Assert.Equal(CacheLevel.None, h.Lib.Get(item.Id)!.CacheLevel);
        Assert.True(d.Depth.IsReady);
        Assert.NotNull(d.Depth.Session!.DepthAt(h.Element(FakeDepthMap.House)));
    }

    // ================================================================================================================ before / after

    /// <summary>Whether a channel holds every tile of the target level in its part of the view, at its current version.</summary>
    static bool Complete(PhotoViewer v, ITileSource src, int channel)
    {
        if (v.Transform() is not { } t || v.Pyramid is not { } p) return false;
        var photo = t.DisplayRect.Intersect(new Rect(v.Bounds.Size));
        double x = t.DisplayRect.X + t.DisplayRect.Width * v.SplitPosition;
        var clip = v.Compare != CompareMode.Split ? photo
            : channel == Channels.Before ? new Rect(photo.X, photo.Y, Math.Max(0, x - photo.X), photo.Height)
            : new Rect(x, photo.Y, Math.Max(0, photo.Right - x), photo.Height);
        long cur = src.Version(channel);
        return p.TilesIn(v.TargetLevel, ViewerMath.VisibleImageRect(t, clip)).All(k => v.CacheOf(channel).Get(k) is { } e && e.Version == cur);
    }

    /// <summary>The drawn pixels of the viewer (BGRA), without the bands at the top and the bottom where the page's controls lie over the
    /// picture; with <paramref name="leftOfCentre"/>, only the part left of the split's divider.</summary>
    static byte[] Pixels(Harness h, bool leftOfCentre = false)
    {
        Dispatcher.UIThread.RunJobs();
        var v = h.Viewer();
        using var frame = h.Window.CaptureRenderedFrame()!;
        var tl = v.TranslatePoint(new Point(0, 0), h.Window) ?? default;
        double k = frame.PixelSize.Width / h.Window.Bounds.Width;
        int band = (int)(64 * k);
        int x0 = (int)(tl.X * k), y0 = (int)(tl.Y * k) + band, w = (int)(v.Bounds.Width * k), hh = (int)(v.Bounds.Height * k) - 2 * band;
        if (leftOfCentre && v.Transform() is { } t)
        {
            // up to a little before the split's divider (the same columns are compared in every mode)
            double divider = t.DisplayRect.X + t.DisplayRect.Width * v.SplitPosition;
            w = Math.Clamp((int)((divider - 16) * k), 1, w);
        }
        using var fb = frame.Lock();
        var o = new byte[w * hh * 4];
        for (int y = 0; y < hh; y++) System.Runtime.InteropServices.Marshal.Copy(fb.Address + (y0 + y) * fb.RowBytes + x0 * 4, o, y * w * 4, w * 4);
        return o;
    }

    static int MaxDiff(byte[] a, byte[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(a[i + c] - b[i + c]));
        return max;
    }

    /// <summary>With no edits, the before side and the after side are the same picture at the same detail, at each quality and zoom — also
    /// from a source that keeps one request at a time, as the library's view does (a newer request cancels an older one of the other side).</summary>
    [AvaloniaFact]
    public async Task Before_and_after_match_with_no_edits_at_each_quality()
    {
        var timings = FakeTimings.Fast with { OneRequestAtATime = true };
        await using var h = await DevelopHarness.OpenAsync(new FakeDevelopOptions { Timings = timings, OpenDelayMs = 0, SaveDelayMs = 20 });
        var d = h.Develop(); var v = h.Viewer();
        v.AnimateZoom = false;
        d.ResetToneCommand.Execute(null);
        await h.SettlePictureAsync();
        var src = h.Session().FakeTiles;
        foreach (var (quality, finest) in new[] { ("preview", 2), ("13 MP", 1), ("full detail", 0) })
        {
            src.SetFinestLevel(finest);
            foreach (var zoom in new[] { "fit", "1:1" })
            {
                if (zoom == "fit") d.ZoomFitCommand.Execute(null); else d.ZoomActualCommand.Execute(null);
                d.SetModeCommand.Execute(DevelopMode.Single);
                await h.SettlePictureAsync(() => v.TargetComplete);
                var single = Pixels(h, leftOfCentre: true);
                // the split: each side holds the target level of its part of the view
                d.SetModeCommand.Execute(DevelopMode.BeforeAfter);
                d.SetCompareStyleCommand.Execute(CompareStyle.Split);
                await h.SettlePictureAsync(() => Complete(v, src, Channels.After) && Complete(v, src, Channels.Before));
                Assert.True(Complete(v, src, Channels.Before), $"{quality} {zoom}: the split's before side lacks tiles of level {v.TargetLevel}");
                int split = MaxDiff(Pixels(h, leftOfCentre: true), single);
                Assert.True(split <= 2, $"{quality} {zoom}: the split's before side differs from the picture by {split}");
                // the toggle: before and after are the same picture
                d.SetCompareStyleCommand.Execute(CompareStyle.Toggle);
                d.ShowingBefore = true;
                await h.SettlePictureAsync(() => v.TargetComplete);
                Assert.True(v.TargetComplete, $"{quality} {zoom}: the before picture never completes");
                var before = Pixels(h);
                d.ShowingBefore = false;
                await h.SettlePictureAsync(() => v.TargetComplete);
                int toggle = MaxDiff(before, Pixels(h));
                Assert.True(toggle <= 2, $"{quality} {zoom}: before differs from after by {toggle}");
            }
        }
        d.SetModeCommand.Execute(DevelopMode.Single);
    }

    /// <summary>Photo + depth: the two viewers share the one request the library's view keeps (a newer request cancels an older one, whatever
    /// the viewer). With no edits, both sides reach their target level at each quality and zoom, and again after a change of the photo alone.</summary>
    [AvaloniaFact]
    public async Task Photo_and_depth_both_complete_from_one_request_at_a_time()
    {
        var timings = FakeTimings.Fast with { OneRequestAtATime = true };
        await using var h = await DevelopHarness.OpenAsync(new FakeDevelopOptions { Timings = timings, OpenDelayMs = 0, SaveDelayMs = 20 });
        var d = h.Develop(); var pv = h.Viewer(); var dv = h.DevelopView().DepthPhotoViewer;
        pv.AnimateZoom = false; dv.AnimateZoom = false;
        Assert.True(d.Depth.IsReady);
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        await h.SettlePictureAsync(() => dv.IsVisible && dv.Bounds.Width > 0);
        var src = h.Session().FakeTiles;
        foreach (var (quality, finest) in new[] { ("preview", 2), ("13 MP", 1), ("full detail", 0) })
        {
            src.SetFinestLevel(finest);
            foreach (var zoom in new[] { "fit", "1:1" })
            {
                if (zoom == "fit") d.ZoomFitCommand.Execute(null); else d.ZoomActualCommand.Execute(null);
                await h.SettlePictureAsync(() => pv.TargetComplete && dv.TargetComplete);
                Assert.True(pv.TargetComplete, $"{quality} {zoom}: the photo side lacks tiles of level {pv.TargetLevel}");
                Assert.True(dv.TargetComplete, $"{quality} {zoom}: the depth side lacks tiles of level {dv.TargetLevel}");
                Assert.Equal(pv.TargetLevel, pv.LevelOnScreen);
                Assert.Equal(dv.TargetLevel, dv.LevelOnScreen);
            }
        }
        // a change of the photo alone: the photo side gets its new tiles, the depth side keeps its own
        d.Session!.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, 0.7);
        await h.SettlePictureAsync(() => pv.TargetComplete && dv.TargetComplete);
        Assert.True(pv.TargetComplete && dv.TargetComplete, "after an edit both sides are complete");
        d.SetModeCommand.Execute(DevelopMode.Single);
    }
}
