using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Develop;
using Lux.App.ViewModels;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;

namespace Lux.App.Tests;

/// <summary>The Develop page rendered with the fake library and editor to PNG files (second UI output folder) for visual review.</summary>
public sealed class DevelopScreenshotTests
{
    static async Task Edit(Harness h, params (string Key, double Value)[] values)
    {
        foreach (var (k, v) in values) h.Session().SetValue(k, v);
        h.Develop().Refresh();
        await h.SettlePictureAsync();
    }

    [AvaloniaFact]
    public async Task Develop_single()
    {
        await using var h = await DevelopHarness.OpenAsync();
        Assert.True(h.Vm.IsDevelop);
        Assert.NotNull(h.Develop().Session);
        await Edit(h, (AdjustmentKeys.Exposure, 0.3), (AdjustmentKeys.Contrast, 12), (AdjustmentKeys.Highlights, -20), (AdjustmentKeys.Shadows, 15),
                   (AdjustmentKeys.Vibrance, 8));
        h.ShotM2("01-develop-single");
    }

    [AvaloniaFact]
    public async Task Develop_zoomed()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var v = h.Viewer();
        // the house's windows and the sign at 100 %, centred
        v.ZoomToActual(animate: false);
        var t = v.Transform()!;
        var target = t.FrameToView(new Point(0.668, 0.668));
        v.PanBy(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2) - target);
        await h.SettlePictureAsync();
        Assert.Equal(0, v.LevelOnScreen);
        h.ShotM2("02-develop-zoomed");
    }

    [AvaloniaFact]
    public async Task Develop_before_after()
    {
        await using var h = await DevelopHarness.OpenAsync();
        await Edit(h, (AdjustmentKeys.Exposure, 0.6), (AdjustmentKeys.Temperature, 7600), (AdjustmentKeys.Vibrance, 35), (AdjustmentKeys.Contrast, 25));
        h.Develop().SetModeCommand.Execute(DevelopMode.BeforeAfter);
        h.Develop().SplitPosition = 0.45;
        await h.SettlePictureAsync(() => h.Viewer().CacheOf(Lux.App.Viewer.Channels.Before).Count > 0);
        await h.SettlePictureAsync();
        h.ShotM2("03-develop-before-after");
    }

    [AvaloniaFact]
    public async Task Develop_crop()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        d.CropCommand.Execute(null);
        await h.SettlePictureAsync();
        d.CropAspectChoice = d.AspectChoices.First(c => c.Label == "3 : 2");
        h.Session().SetCropFrame(new Lux.Library.NormRect(0.14, 0.2, 0.7, 0.66), false);
        h.Session().SetCropFrame(h.Session().FitCropFrame(h.Session().CropFrame), false);
        d.Refresh();
        await h.SettlePictureAsync();
        Assert.True(d.IsCropping);
        h.ShotM2("04-develop-crop");
    }

    [AvaloniaFact]
    public async Task Develop_rotated()
    {
        await using var h = await DevelopHarness.OpenAsync();
        await Edit(h, (AdjustmentKeys.Exposure, 0.2), (AdjustmentKeys.Saturation, 15));
        h.Develop().RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Contains("6240 × 8320", h.Develop().HeaderText);
        h.Develop().InfoExpanded = true;
        await h.SettlePictureAsync();
        h.ShotM2("05-develop-rotated");
    }

    [AvaloniaFact]
    public async Task Develop_processing()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        await h.MockupSelectionAsync();
        var busy = h.Library.Items.First(i => i.Item.ActiveJob == JobKind.Analysis && i.Item.ActiveJobState == JobState.Running);
        h.Vm.OpenInDevelop(busy);
        await h.SettleAsync(() => h.Develop().Session != null && h.Viewer().CacheOf(0).Count > 1, waitThumbnails: false);
        await h.SettleAsync(timeoutMs: 400, waitThumbnails: false);
        Assert.True(h.Develop().IsProcessing);
        h.Viewer().InvalidateVisual();
        h.ShotM2("06-develop-processing");
    }

    [AvaloniaFact]
    public async Task Develop_quality_notice()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        await h.MockupSelectionAsync();
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.HasProblem && !i.IsBusy);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        await h.Develop().Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        Assert.True(h.Develop().Quality.HasNotice);
        await h.SettleAsync(waitThumbnails: false);
        h.ShotM2("07-develop-quality-notice");
    }

    [AvaloniaFact]
    public async Task Develop_presets()
    {
        await using var h = await DevelopHarness.OpenAsync();
        await Edit(h, (AdjustmentKeys.Exposure, 0.4), (AdjustmentKeys.Clarity, 20), (AdjustmentKeys.Temperature, 6200));
        h.Develop().SavePresetCommand.Execute(null);
        var d = (PatchDialogViewModel)h.Vm.Dialog!;
        d.Name = "Golden hour";
        d.Groups.First(g => g.Group == EditGroups.Sharpening).IsOn = false;
        await h.SettleAsync(waitThumbnails: false);
        h.ShotM2("08-develop-presets");
    }

    [AvaloniaFact]
    public async Task Develop_paste_dialog()
    {
        await using var h = await DevelopHarness.OpenAsync();
        await Edit(h, (AdjustmentKeys.Exposure, 0.5), (AdjustmentKeys.Vibrance, 20), (AdjustmentKeys.Shadows, 18));
        h.Develop().RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        h.Services.Develop.SetClipboard(h.Session().Snapshot(), "L16_00493", h.Develop().Item!.Id);
        await h.Develop().NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        h.Develop().PasteChooseCommand.Execute(null);
        Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        await h.SettleAsync(waitThumbnails: false);
        h.ShotM2("09-develop-paste-dialog");
    }

    [AvaloniaFact]
    public async Task Develop_history()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var s = h.Session();
        s.SetValue(AdjustmentKeys.Exposure, 0.3);
        s.SetValue(AdjustmentKeys.Contrast, 12);
        s.SetValue(AdjustmentKeys.Highlights, -20);
        s.SetValue(AdjustmentKeys.Shadows, 15);
        s.RotateRight();
        s.SetValue(AdjustmentKeys.Temperature, 6400);
        s.SetValue(AdjustmentKeys.Vibrance, 8);
        h.Develop().Refresh();
        h.Develop().UndoCommand.Execute(null);
        h.Develop().UndoCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(h.Develop().History.Count(r => r.IsUndone) == 2);
        h.ShotM2("10-develop-history");
    }
}
