using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lux.App.Develop;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.Library;
using Lux.App.Viewer;
using PixelSize = Avalonia.PixelSize;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// Photos shown the way the camera was held (contract v2.1's rule, which the fake follows): the photo opens upright; the first geometry edit
/// bakes the held turn into the saved transform as one step named for the action; rotate right four times cycles visibly; crop presets give
/// the named shape as shown; revert and the geometry reset return to the held view.
/// </summary>
public sealed class HeldOrientationTests
{
    static async Task<(Harness H, CaptureItemViewModel Item)> OpenHeldAsync(PhotoOrientation held = PhotoOrientation.Rotate90)
    {
        var h = await Harness.CreateAsync();
        await h.SettleAsync();
        await h.MockupSelectionAsync();
        await h.SelectScopeAsync("all");
        var item = h.Library.Items.First(i => i.Item.HeldOrientation == held && !i.HasProblem && !i.IsBusy);
        h.Library.Click(item, false, false);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        return (h, item);
    }

    static bool Portrait(Harness h) => h.Viewer().Pyramid!.Image.Height > h.Viewer().Pyramid!.Image.Width;

    [AvaloniaFact]
    public async Task Held_photo_opens_upright_with_no_steps()
    {
        var (h, item) = await OpenHeldAsync();
        await using var _ = h;
        Assert.True(h.Services.Library.Settings.ShowPhotosAsHeld);
        Assert.True(Portrait(h));
        Assert.True(h.Session().ShownAsHeld);
        Assert.Equal(new[] { "Opened" }, h.Session().History);
        Assert.True(h.Session().IsOriginal);
        // exports follow the view shown as held, so there is nothing to keep — no "Keep the turn" button, no step
        Assert.False(h.Develop().CanRotateAsHeld);
        Assert.Equal(new[] { "Opened" }, h.Session().History);
    }

    [AvaloniaFact]
    public async Task Rotate_right_four_times_cycles_visibly_one_step_each()
    {
        var (h, _) = await OpenHeldAsync();
        await using var __ = h;
        var start = h.Session().PictureTurn;
        var frames = new List<Bitmap>();
        bool portrait = Portrait(h);
        for (int i = 1; i <= 4; i++)
        {
            h.Develop().RotateRightCommand.Execute(null);
            await h.SettlePictureAsync();
            Assert.Equal(!portrait, Portrait(h));                       // every press turns what is shown
            portrait = Portrait(h);
            Assert.Equal(start.Then(new Lux.App.Viewer.Turn(i & 3, false)), h.Session().PictureTurn);
            frames.Add(h.Window.CaptureRenderedFrame()!);
        }
        Assert.Equal(start, h.Session().PictureTurn);                    // full circle: back where it started, still decided
        Assert.False(h.Session().ShownAsHeld);
        Assert.True(h.Develop().CanRevert);                               // the turn is saved now (exports follow it); revert is not a no-op
        Assert.Equal(new[] { "Opened", "Rotate right", "Rotate right", "Rotate right", "Rotate right" }, h.Session().History);
        SaveGrid(frames, "12-develop-held-rotate-cycle");
        foreach (var f in frames) f.Dispose();
    }

    [AvaloniaFact]
    public async Task Crop_presets_give_the_named_shape_as_shown_and_revert_returns_the_held_view()
    {
        var (h, _) = await OpenHeldAsync(PhotoOrientation.Rotate270);
        await using var __ = h;
        var d = h.Develop();
        var s = h.Session();
        Assert.True(Portrait(h));
        d.CropCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(Portrait(h));                                         // the uncropped picture as shown
        foreach (var (label, ratio) in new[] { ("3 : 2", 1.5), ("16 : 9", 16 / 9.0), ("1 : 1", 1.0) })
        {
            d.CropAspectChoice = d.AspectChoices.First(c => c.Label == label);
            await h.SettlePictureAsync();
            var px = s.CropFrame.ToPixels(new Size(h.Viewer().Pyramid!.Image.Width, h.Viewer().Pyramid!.Image.Height));
            Assert.Equal(ratio, px.Width / px.Height, 3);                // the named shape on screen
            Assert.Equal(ratio, h.DevelopView().CropOverlay.Ratio, 6);
        }
        d.SwapAspectCommand.Execute(null);                                // portrait presets: 2 : 3 …
        d.CropAspectChoice = d.AspectChoices.First(c => c.Label == "2 : 3");
        await h.SettlePictureAsync();
        var p23 = s.CropFrame.ToPixels(new Size(h.Viewer().Pyramid!.Image.Width, h.Viewer().Pyramid!.Image.Height));
        Assert.Equal(2 / 3.0, p23.Width / p23.Height, 3);
        h.ShotM2("13-develop-held-crop-preset");
        d.ApplyCrop();
        await h.SettlePictureAsync();
        var shown = h.Viewer().Pyramid!.Image;
        Assert.Equal(2 / 3.0, shown.Width / (double)shown.Height, 2);    // the picture is that shape
        Assert.Equal(new[] { "Opened", "Crop" }, s.History);             // one step, the held turn baked in
        // flip: one step
        d.FlipHorizontalCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal("Flip horizontal", s.History[^1]);
        // revert: the held view again (upright, uncropped, shown as held, undecided)
        d.RevertCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(Portrait(h));
        Assert.True(s.ShownAsHeld);
        Assert.True(s.IsOriginal);
        Assert.Equal("Revert to original", s.History[^1]);
        Assert.False(d.CanRotateAsHeld);   // shown as held again: nothing to keep (exports follow the view)
        // the geometry reset does the same after a rotation
        d.RotateLeftCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.False(Portrait(h));
        d.ResetGeometryCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(Portrait(h));
        Assert.True(s.ShownAsHeld);
    }

    [AvaloniaFact]
    public async Task Held_first_open_screen()
    {
        var (h, _) = await OpenHeldAsync();
        await using var __ = h;
        h.ShotM2("11-develop-held-first-open");
    }

    /// <summary>Four frames in a 2 × 2 grid, one PNG (in the second UI output folder's shots).</summary>
    internal static void SaveGridTo(IReadOnlyList<Bitmap> frames, string name) => SaveGrid(frames, name);

    static void SaveGrid(IReadOnlyList<Bitmap> frames, string name)
    {
        var size = frames[0].PixelSize;
        int w = size.Width / 2, hgt = size.Height / 2;
        using var rtb = new RenderTargetBitmap(new PixelSize(w * 2 + 8, hgt * 2 + 8));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#1c1c1c")), new Rect(0, 0, w * 2 + 8, hgt * 2 + 8));
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                for (int i = 0; i < frames.Count && i < 4; i++)
                    dc.DrawImage(frames[i], new Rect(frames[i].Size), new Rect((i % 2) * (w + 8), (i / 2) * (hgt + 8), w, hgt));
        }
        Directory.CreateDirectory(Paths.M2Shots);
        rtb.Save(Path.Combine(Paths.M2Shots, name + ".png"));
    }
}
