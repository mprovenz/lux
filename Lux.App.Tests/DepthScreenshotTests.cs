using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Lux.App.Develop;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>The Develop page's depth interface on the fake, rendered to PNG files in the third UI output folder for visual review.</summary>
public sealed class DepthScreenshotTests
{
    /// <summary>Zoom to <paramref name="percent"/> with an element of the scene at the view's centre.</summary>
    static async Task ZoomOn(Harness h, byte element, double percent, double dx = 0, double dy = 0)
    {
        var v = h.Viewer();
        v.AnimateZoom = false;
        v.ZoomToPercent(percent, animate: false);
        await h.SettlePictureAsync();
        var t = v.Transform()!;
        var p = h.Element(element, dx, dy);
        var at = t.FrameToView(new Point(p.X, p.Y));
        v.PanBy(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2) - at);
        await h.SettlePictureAsync();
    }

    static async Task Target(Harness h, string text)
    {
        h.DepthVm().TargetEdit = text;
        h.DepthVm().CommitTarget();
        await h.SettleDepthAsync();
    }

    [AvaloniaFact]
    public async Task Depth_effect()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop().Depth;
        Assert.True(d.IsReady);
        h.Session().HoldRebuild(null);
        await h.SettleDepthAsync(() => !d.IsRebuilding);
        h.Hover(h.Element(FakeDepthMap.House, 0.2, 0.3));
        await h.SettlePictureAsync();
        Assert.Contains("depth", h.Vm.StatusPointer);
        h.ShotM3("01-depth-effect");
    }

    [AvaloniaFact]
    public async Task Focus_tool_shaded()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var view = h.DevelopView();
        h.Key(Key.F);
        h.Click(h.Element(FakeDepthMap.House, 0.3, 0.2));
        await h.SettleDepthAsync(() => view.ShadeOverlay.Cache.Count > 0 && h.Session().History[^1].StartsWith("Focus"));
        h.DepthVm().Message = null;
        h.Hover(h.Element(FakeDepthMap.Star));
        await h.SettlePictureAsync();
        Assert.True(h.DepthVm().ShowHint);
        h.ShotM3("02-focus-tool-shaded");
    }

    [AvaloniaFact]
    public async Task Photo_plus_depth()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Develop().SetModeCommand.Execute(DevelopMode.PhotoDepth);
        var depth = h.DevelopView().DepthPhotoViewer;
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0 && depth.LevelOnScreen <= depth.TargetLevel);
        h.Hover(h.Element(FakeDepthMap.House, 0.4, 0.5));
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0 && depth.LevelOnScreen <= depth.TargetLevel);
        h.ShotM3("03-photo-plus-depth");
    }

    [AvaloniaFact]
    public async Task Refine_edge_stroke()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Session().FakeDepth.SetEffect(2.0, h.Session().FakeDepth.DepthAt(h.Element(FakeDepthMap.House))!.Value);
        await ZoomOn(h, FakeDepthMap.House, 30, -1.2, 0);
        h.Key(Key.E);
        h.DepthVm().SizeValue = 8;
        var pts = new[] { h.Element(FakeDepthMap.House, -1.75, -1.9), h.Element(FakeDepthMap.House, -1.8, -0.4), h.Element(FakeDepthMap.House, -1.72, 1.2) };
        h.Stroke(pts, steps: 10, release: false);
        await h.SettleAsync(timeoutMs: 200, waitThumbnails: false);
        h.ShotM3("04-refine-edge-stroke");
        h.Window.MouseUp(h.At(pts[^1]), MouseButton.Left);
        await h.SettleDepthAsync(() => h.Session().History[^1] == "Refine edge");
    }

    [AvaloniaFact]
    public async Task Blend_area()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Clean"));
        h.Develop().SetModeCommand.Execute(DevelopMode.PhotoDepth);
        await h.SettleDepthAsync();
        await ZoomOn(h, FakeDepthMap.House, 16);
        h.Key(Key.B);
        d.SizeValue = 8;
        var pts = new[] { h.Element(FakeDepthMap.Windows, -1.5, -0.4), h.Element(FakeDepthMap.Windows, 0.4, -0.4) };
        h.Stroke(pts, steps: 10, release: false);
        var depth = h.DevelopView().DepthPhotoViewer;
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0);
        h.ShotM3("05-blend-area");
        h.Window.MouseUp(h.At(pts[^1]), MouseButton.Left);
        await h.SettleDepthAsync(() => h.Session().History[^1] == "Blend area");
    }

    [AvaloniaFact]
    public async Task Match_brush()
    {
        await using var h = await DevelopHarness.OpenAsync();
        await ZoomOn(h, FakeDepthMap.Star, 25);
        h.Key(Key.M);
        await Target(h, "3.2");
        h.DepthVm().SizeValue = 6;
        var pts = new[] { h.Element(FakeDepthMap.Star, -1.2, -1), h.Element(FakeDepthMap.Star, 1.2, -0.3), h.Element(FakeDepthMap.Star, -1, 0.8) };
        h.Stroke(pts, steps: 10, release: false);
        await h.SettleAsync(timeoutMs: 200, waitThumbnails: false);
        h.ShotM3("06-match-brush");
        h.Window.MouseUp(h.At(pts[^1]), MouseButton.Left);
        await h.SettleDepthAsync(() => h.Session().History[^1].StartsWith("Match depth"));
    }

    [AvaloniaFact]
    public async Task Match_lasso()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        await ZoomOn(h, FakeDepthMap.House, 22, 1.0, 0);
        h.Key(Key.M);
        d.SetModeCommand.Execute(MatchMode.Lasso);
        d.SizeValue = 12;
        foreach (var (dx, dy) in new[] { (-2.0, -1.6), (-0.2, -2.4), (1.9, -1.3), (2.1, 1.5), (-1.8, 1.7) }) h.Click(h.Element(FakeDepthMap.House, dx, dy));
        await h.SettleDepthAsync(() => d.HasPending && h.DevelopView().MaskOverlay.Cache.Count > 0);
        d.PickCommand.Execute(null);
        h.Click(h.Element(FakeDepthMap.Sign));
        h.Hover(h.Element(FakeDepthMap.House, 2.6, 0.2));
        await h.SettleDepthAsync();
        h.ShotM3("07-match-lasso");
    }

    [AvaloniaFact]
    public async Task Quick_select_mask()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        await ZoomOn(h, FakeDepthMap.House, 24, 1.2, 0);
        h.Key(Key.Q);
        d.SizeValue = 3;
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.8, -0.2), h.Element(FakeDepthMap.House, 0.6, 0.5) });
        await h.SettleDepthAsync(() => d.HasPending);
        h.Stroke(new[] { h.Element(FakeDepthMap.Sign, -0.5, 0), h.Element(FakeDepthMap.Sign, 0.5, 0.3) });
        await h.SettleDepthAsync();
        d.SetAddCommand.Execute("false");
        h.Stroke(new[] { h.Element(FakeDepthMap.Sign, -0.5, 0), h.Element(FakeDepthMap.Sign, 0.5, 0.3) });
        await h.SettleDepthAsync();
        d.SetAddCommand.Execute("true");
        await Target(h, "3.2");
        h.Hover(h.Element(FakeDepthMap.House, 1.6, 1.0));
        await h.SettleDepthAsync(() => h.DevelopView().MaskOverlay.Cache.Count > 0);
        h.ShotM3("08-quick-select-mask");
    }

    [AvaloniaFact]
    public async Task Quick_select_applied()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        h.Key(Key.Q);
        d.SizeValue = 6;
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.8, -0.2), h.Element(FakeDepthMap.House, 0.6, 0.5) });
        await h.SettleDepthAsync(() => d.HasPending);
        await Target(h, "3.2");
        await d.ApplyCommand.ExecuteAsync(null);
        h.Develop().SetModeCommand.Execute(DevelopMode.PhotoDepth);
        var depth = h.DevelopView().DepthPhotoViewer;
        await h.SettleDepthAsync(() => h.Session().History[^1].StartsWith("Quick select"));
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0 && depth.LevelOnScreen <= depth.TargetLevel);
        h.ShotM3("09-quick-select-applied");
    }

    [AvaloniaFact]
    public async Task History_combined()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var s = h.Session();
        s.HoldRebuild(null);
        s.SetValue(AdjustmentKeys.Exposure, 0.3);
        s.FakeDepth.SetEffect(2.8, s.FakeDepth.FocusMm);
        s.SetValue(AdjustmentKeys.Contrast, 12);
        s.SetValue(AdjustmentKeys.Vibrance, 8);
        s.FakeDepth.SetEffect(2.8, 3800);
        h.Develop().Refresh();
        h.Develop().UndoCommand.Execute(null);
        h.Develop().UndoCommand.Execute(null);
        await h.SettleDepthAsync(() => !h.DepthVm().IsRebuilding);
        h.ShotM3("10-history-combined");
    }

    [AvaloniaFact]
    public async Task History_rebuilding()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Session().HoldRebuild(3);
        await h.SettleDepthAsync();
        Assert.Equal("Rebuilding earlier steps · 3 of 4", h.DepthVm().RebuildText);
        h.ShotM3("11-history-rebuilding");
    }

    [AvaloniaFact]
    public async Task History_unreliable()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Session().MarkHistoryUnreliable();
        h.Session().SetValue(AdjustmentKeys.Exposure, 0.2);
        h.Develop().Refresh();
        await h.SettleDepthAsync();
        Assert.True(h.DepthVm().IsUnreliable);
        h.ShotM3("12-history-unreliable");
    }

    [AvaloniaFact]
    public async Task Versions()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        var item = h.Find(i => i.Name == "L16_00500")!;
        await h.OpenItemAsync(item);
        var d = h.DepthVm();
        await d.StartCleanVersionCommand.ExecuteAsync(null);
        h.Key(Key.B);
        h.Stroke(new[] { h.Element(FakeDepthMap.Windows, -1.4, 0), h.Element(FakeDepthMap.Windows, 1.4, 0) });
        await h.SettleDepthAsync(() => h.Session().History[^1] == "Blend area");
        h.Key(Key.Escape);
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "From Lumen"));
        h.Develop().SetModeCommand.Execute(DevelopMode.PhotoDepth);
        var depth = h.DevelopView().DepthPhotoViewer;
        await h.SettleDepthAsync();
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0 && depth.LevelOnScreen <= depth.TargetLevel);
        h.ShotM3("13-versions");
    }

    [AvaloniaFact]
    public async Task Import_lumen_single()
    {
        await using var h = await Harness.CreateAsync();
        await h.SelectScopeAsync("all");
        var item = h.Find(i => i.HasLumenSession && i.Edits.Source == EditSource.None && i.DepthState == DepthState.Analysed && i.LumenSession == LumenSessionState.SettingsChanged)
                   ?? h.Find(i => i.HasLumenSession && i.Edits.Source == EditSource.None && i.DepthState == DepthState.Analysed)!;
        await h.OpenItemAsync(item);
        h.Develop().ImportLumenEditsCommand.Execute(null);
        var dialog = Assert.IsType<LumenImportViewModel>(h.Vm.Dialog);
        await h.SettleAsync(() => dialog.IsReady, waitThumbnails: false);
        h.ShotM3("14-import-lumen-single");
    }

    [AvaloniaFact]
    public async Task Depth_not_ready()
    {
        await using var h = await Harness.CreateAsync();
        await h.SelectScopeAsync("all");
        var item = h.Find(i => i.DepthState == DepthState.NotAnalysed && i.ActiveJob == null)!;
        await h.OpenItemAsync(item);
        h.Key(Key.E);
        h.Stroke(new[] { new NormPoint(0.4, 0.6), new NormPoint(0.5, 0.62) });
        await h.SettleAsync(timeoutMs: 300, waitThumbnails: false);
        h.DepthVm().Message = null;
        await h.SettleAsync(timeoutMs: 100, waitThumbnails: false);
        h.ShotM3("15-depth-not-ready");
    }

    [AvaloniaFact]
    public async Task Tool_progress()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        h.Session().FakeDepth.SlowFactor = 300;
        h.Key(Key.E);
        d.SizeValue = 8;
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -1.75, -1.2), h.Element(FakeDepthMap.House, -1.75, 1.6) });
        await h.SettleAsync(() => d.IsBusy && d.ActivityProgress > 3, timeoutMs: 8000, waitThumbnails: false);
        h.Hover(h.Element(FakeDepthMap.House, 0.5, 0));
        await h.SettleAsync(timeoutMs: 150, waitThumbnails: false);
        h.ShotM3("16-tool-progress");
        d.CancelActivityCommand.Execute(null);
        await h.SettleDepthAsync(() => !d.IsBusy);
    }
}
