using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Fake;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Tests;

/// <summary>The Develop page through real input where it matters (slider drags, crop handles, keys, the value fields) and through the view
/// model elsewhere; effects are checked on the fake editor and the fake library.</summary>
public sealed class DevelopTests
{
    static void Key(Harness h, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        h.Window.KeyPress(key, mods, PhysicalKey.None, null);
        h.Window.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    static AdjustmentSlider SliderOf(Harness h, string key) =>
        h.DevelopView().GetVisualDescendants().OfType<AdjustmentSlider>().First(s => (s.DataContext as AdjustmentRowViewModel)?.Key == key);

    static TextBox ValueBoxOf(Harness h, string key) =>
        h.DevelopView().GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("valuefield") && (t.DataContext as AdjustmentRowViewModel)?.Key == key);

    static AdjustmentRowViewModel Row(Harness h, string key) => h.Develop().Adjustments.First(r => r.Key == key);

    static Point At(Visual v, Harness h, double fx, double fy) => v.TranslatePoint(new Point(v.Bounds.Width * fx, v.Bounds.Height * fy), h.Window)!.Value;

    /// <summary>Drag a slider from one position to another in <paramref name="steps"/> pointer moves; returns the longest move (ms) — the UI
    /// thread must never wait for a render.</summary>
    static double Drag(Harness h, string key, double from, double to, int steps = 8)
    {
        var s = SliderOf(h, key);
        // the thumb's track runs from 8 to width − 8
        Point P(double f) => s.TranslatePoint(new Point(8 + f * (s.Bounds.Width - 16), s.Bounds.Height / 2), h.Window)!.Value;
        double worst = 0;
        h.Window.MouseMove(P(from));
        h.Window.MouseDown(P(from), MouseButton.Left);
        for (int i = 1; i <= steps; i++)
        {
            var sw = Stopwatch.StartNew();
            h.Window.MouseMove(P(from + (to - from) * i / steps), RawInputModifiers.LeftMouseButton);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            Dispatcher.UIThread.RunJobs();
        }
        h.Window.MouseUp(P(to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        return worst;
    }

    static void Type(Harness h, string key, string text, Key end = Avalonia.Input.Key.Enter)
    {
        var box = ValueBoxOf(h, key);
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.Focus(), $"the {key} field took no focus");
        box.Text = text;
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused, $"the {key} field lost focus before {end}");
        Key(h, end);
    }

    // ================================================================================================================ opening

    [AvaloniaFact]
    public async Task Opens_from_the_library_by_double_click_Enter_and_the_details_button()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.Mode = BrowseMode.Grid;
        await h.SettleAsync();
        // double-click a cell
        var item = h.Item("L16_00495");
        var cell = h.Browse.ActiveItems.GetVisualDescendants().OfType<Border>().First(b => b.DataContext == item && b.Classes.Contains("cell"));
        // a double click (the press with a click count of 2, as the input system raises it; headless input is too slow to time one)
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        cell.RaiseEvent(new PointerPressedEventArgs(cell, pointer, h.Window, new Point(cell.Bounds.Width / 2, 20), 0, props, KeyModifiers.None, 2));
        await h.SettlePictureAsync();
        Assert.True(h.Vm.IsDevelop);
        Assert.Equal("L16_00495", h.Develop().Item?.Name);
        Assert.NotNull(h.Develop().Session);
        Assert.Contains("L16_00495", h.Develop().HeaderText);
        // Esc goes back to the library
        Key(h, Avalonia.Input.Key.Escape);
        Assert.True(h.Vm.IsLibrary);
        // Enter on the browse area
        h.Library.Click(h.Item("L16_00500"), false, false);
        h.Browse.Focus();
        Key(h, Avalonia.Input.Key.Enter);
        await h.SettlePictureAsync();
        Assert.Equal("L16_00500", h.Develop().Item?.Name);
        // the details panel's button
        h.Vm.CurrentPage = Page.Library;
        h.Library.Click(h.Item("L16_00493"), false, false);
        h.Library.OpenInDevelopCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal("L16_00493", h.Develop().Item?.Name);
        Assert.Equal(1, h.FakeDevelop().Opened.Count(s => s.Item.Name == "L16_00493"));
    }

    // ================================================================================================================ sliders

    [AvaloniaFact]
    public async Task Slider_drag_is_live_at_a_lower_level_and_makes_one_history_step()
    {
        // L16_00494: no saved depth edits (L16_00493 opens with its four depth steps listed)
        await using var h = await DevelopHarness.OpenAsync(name: "L16_00494");
        var s = h.Session();
        var v = h.Viewer();
        v.ZoomToPercent(50, animate: false);                     // level 1
        await h.SettlePictureAsync();
        Assert.Equal(1, v.LevelOnScreen);
        int before = s.FakeTiles.Requests.Count;
        // press, move, and look at the picture while still dragging
        var slider = SliderOf(h, AdjustmentKeys.Exposure);
        Point P(double f) => slider.TranslatePoint(new Point(8 + f * (slider.Bounds.Width - 16), slider.Bounds.Height / 2), h.Window)!.Value;
        h.Window.MouseDown(P(0.5), MouseButton.Left);
        double worst = 0;
        var moves = new List<double>();
        for (int i = 1; i <= 6; i++)
        {
            var sw = Stopwatch.StartNew();
            h.Window.MouseMove(P(0.5 + 0.03 * i), RawInputModifiers.LeftMouseButton);
            moves.Add(sw.Elapsed.TotalMilliseconds);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            await h.SettleAsync(timeoutMs: 120, waitThumbnails: false);
        }
        Assert.True(s.FakeTiles.IsDragging);
        Assert.True(s.DragCalls >= 6);
        Assert.Equal(1, s.History.Count);                       // nothing committed yet
        await h.SettlePictureAsync(() => h.Viewer().LevelOnScreen == 3);
        Assert.Equal(3, v.LevelOnScreen);                       // the lower-level picture follows the slider
        h.Window.MouseUp(P(0.68), MouseButton.Left);
        await h.SettlePictureAsync(() => h.Viewer().LevelOnScreen == 1);
        Assert.False(s.FakeTiles.IsDragging);
        Assert.Equal(1, v.LevelOnScreen);                       // refined on release
        Assert.Equal(2, s.History.Count);
        Assert.StartsWith("Exposure +", s.History[1]);
        Assert.Equal(s.Get(AdjustmentKeys.Exposure), Row(h, AdjustmentKeys.Exposure).Value, 9);
        Assert.Equal(Row(h, AdjustmentKeys.Exposure).ValueText, ValueBoxOf(h, AdjustmentKeys.Exposure).Text);
        // the slider's own work per move (the headless input call also lays out and rasterizes the whole window on the CPU)
        Assert.True(Row(h, AdjustmentKeys.Exposure).LongestMoveMs < 30, $"a drag move took {Row(h, AdjustmentKeys.Exposure).LongestMoveMs:0.0} ms on the UI thread");
        Assert.True(s.FakeTiles.Requests.Count > before);
        // saved and mirrored in the library
        await h.SettleAsync(() => h.Lib.Get(s.Item.Id)!.Edits.AdjustmentsSummary != null);
        Assert.StartsWith("exposure +", h.Lib.Get(s.Item.Id)!.Edits.AdjustmentsSummary);
        // a second drag on another slider: one more step, not one per move
        Drag(h, AdjustmentKeys.Contrast, 0.5, 0.3, steps: 10);
        await h.SettlePictureAsync();
        Assert.Equal(3, s.History.Count);
        Assert.StartsWith("Contrast −", s.History[2]);
        // keys on a focused slider: a quick run of presses is one step (raised directly: headless key input renders between presses)
        var sat = SliderOf(h, AdjustmentKeys.Saturation);
        sat.Focus();
        for (int i = 0; i < 5; i++) sat.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Right, Source = sat });
        sat.EndKeyDrag();
        await h.SettlePictureAsync();
        Assert.Equal(4, s.History.Count);
        Assert.Equal(5, s.Get(AdjustmentKeys.Saturation), 6);
    }

    [AvaloniaFact]
    public async Task Typed_values_double_click_reset_and_section_reset()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var s = h.Session();
        Type(h, AdjustmentKeys.Exposure, "0.5");
        Assert.Equal(0.5, s.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal("+0.50", ValueBoxOf(h, AdjustmentKeys.Exposure).Text);
        Assert.Equal("Exposure +0.50", s.History[^1]);
        // not a number: the field shows the error, the value stays
        Type(h, AdjustmentKeys.Exposure, "bright");
        Assert.True(Row(h, AdjustmentKeys.Exposure).HasError);
        Assert.Equal(0.5, s.Get(AdjustmentKeys.Exposure), 9);
        // Esc puts the shown value back
        Type(h, AdjustmentKeys.Exposure, "2", Avalonia.Input.Key.Escape);
        Assert.Equal("+0.50", ValueBoxOf(h, AdjustmentKeys.Exposure).Text);
        Assert.False(Row(h, AdjustmentKeys.Exposure).HasError);
        // out of range: clamped; the minus sign and units are understood
        Type(h, AdjustmentKeys.Exposure, "9");
        Assert.Equal(3, s.Get(AdjustmentKeys.Exposure), 9);
        Type(h, AdjustmentKeys.Contrast, "−20");
        Assert.Equal(-20, s.Get(AdjustmentKeys.Contrast), 9);
        // a refresh from the session while typing (a save finishing) leaves the typed text alone
        var clarity = ValueBoxOf(h, AdjustmentKeys.Clarity);
        clarity.Focus();
        clarity.Text = "40";
        h.Develop().Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("40", clarity.Text);
        Key(h, Avalonia.Input.Key.Enter);
        Assert.Equal(40, s.Get(AdjustmentKeys.Clarity), 9);
        Type(h, AdjustmentKeys.Temperature, "6500 K");
        Assert.Equal(6500, s.Get(AdjustmentKeys.Temperature), 9);
        Assert.Equal("6500 K", ValueBoxOf(h, AdjustmentKeys.Temperature).Text);
        // double-click on the name: back to the default
        var label = h.DevelopView().GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("adjlabel") && (t.DataContext as AdjustmentRowViewModel)?.Key == AdjustmentKeys.Exposure);
        label.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, null!) { Source = label });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, s.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal("Reset exposure", s.History[^1]);
        // the section's reset: every slider, one step
        int steps = s.History.Count;
        h.Develop().ResetToneCommand.Execute(null);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.Equal("Reset tone and color", s.History[^1]);
        Assert.All(s.Specs, sp => Assert.Equal(sp.Default, s.Get(sp.Key), 9));
        Assert.All(h.Develop().Adjustments, r => Assert.True(r.IsDefault));
    }

    // ================================================================================================================ revert, undo

    [AvaloniaFact]
    public async Task Revert_to_original_and_undo_redo_and_history_jumps()
    {
        await using var h = await DevelopHarness.OpenAsync(name: "L16_00494");
        var d = h.Develop();
        var s = h.Session();
        Type(h, AdjustmentKeys.Exposure, "0.8");
        d.RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Contains("6240 × 8320", d.HeaderText);
        Assert.True(d.CanRevert);
        d.RevertCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(s.IsOriginal);
        Assert.Contains("8320 × 6240", d.HeaderText);
        Assert.Equal("Revert to original", s.History[^1]);
        Assert.True(d.CanRevert);   // still offered while there are steps (a revert is always a step of its own)
        // Ctrl+Z brings the edits back, Ctrl+Shift+Z reverts again
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        await h.SettlePictureAsync();
        Assert.Equal(0.8, s.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal(90, s.Geometry.TurnDegrees);
        Assert.Equal(0.8, Row(h, AdjustmentKeys.Exposure).Value, 9);
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        await h.SettlePictureAsync();
        Assert.True(s.IsOriginal);
        // the history list: the current step highlighted, steps after it dimmed; a click jumps
        Assert.Equal(4, d.History.Count);
        Assert.True(d.History[3].IsCurrent);
        d.GoToHistoryCommand.Execute(d.History[1]);
        await h.SettlePictureAsync();
        Assert.Equal(0.8, s.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal(0, s.Geometry.TurnDegrees);
        Assert.True(d.History[1].IsCurrent);
        Assert.True(d.History[2].IsUndone && d.History[3].IsUndone);
        // a new edit after an undo drops the steps after it
        Type(h, AdjustmentKeys.Contrast, "10");
        Assert.Equal(3, d.History.Count);
        Assert.False(d.CanRedo);
    }

    // ================================================================================================================ geometry

    [AvaloniaFact]
    public async Task Crop_with_aspect_presets_handles_apply_and_cancel()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var s = h.Session();
        var v = h.Viewer();
        Key(h, Avalonia.Input.Key.C);
        await h.SettlePictureAsync();
        Assert.True(d.IsCropping);
        Assert.True(h.DevelopView().CropOverlay.IsVisible);
        Assert.True(v.LocksView);
        // 16:9: the frame reshapes (same area, centered) and keeps the aspect in pixels
        d.CropAspectChoice = d.AspectChoices.First(c => c.Label == "16 : 9");
        await h.SettlePictureAsync();
        var frame = s.CropFrame.ToPixels(new Size(8320, 6240));
        Assert.Equal(16 / 9.0, frame.Width / frame.Height, 3);
        Assert.Equal(16 / 9.0, h.DevelopView().CropOverlay.Ratio, 6);
        // drag the bottom-right corner in: still 16:9, the session follows
        var t = v.Transform()!;
        var corner = v.TranslatePoint(t.FrameToView(new Point(s.CropFrame.Right, s.CropFrame.Bottom)), h.Window)!.Value;
        h.Window.MouseDown(corner, MouseButton.Left);
        h.Window.MouseMove(corner - new Vector(60, 10), RawInputModifiers.LeftMouseButton);
        h.Window.MouseMove(corner - new Vector(120, 20), RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(corner - new Vector(120, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var dragged = s.CropFrame.ToPixels(new Size(8320, 6240));
        Assert.True(dragged.Width < frame.Width);
        Assert.Equal(16 / 9.0, dragged.Width / dragged.Height, 3);
        // Enter applies: one step, the picture becomes the crop
        Key(h, Avalonia.Input.Key.Enter);
        await h.SettlePictureAsync();
        Assert.False(d.IsCropping);
        Assert.True(s.Geometry.HasCrop);
        Assert.Equal("Crop", s.History[^1]);
        Assert.InRange(v.Pyramid!.Image.Width, (int)Math.Round(dragged.Width) - 1, (int)Math.Round(dragged.Width) + 1);
        Assert.Contains("Cropped 16 : 9", d.GeometryText);
        // again: the portrait presets (X), then Esc leaves the crop as it was
        var applied = s.Geometry.Crop;
        Key(h, Avalonia.Input.Key.C);
        await h.SettlePictureAsync();
        Assert.True(d.IsCropping);
        Key(h, Avalonia.Input.Key.X);
        Assert.True(d.IsPortraitAspects);
        Assert.Equal("9 : 16", d.CropAspectChoice?.Label);
        Key(h, Avalonia.Input.Key.Escape);
        await h.SettlePictureAsync();
        Assert.False(d.IsCropping);
        Assert.True(s.Geometry.Crop.Near(applied));
        Assert.True(h.Vm.IsDevelop);                            // Esc canceled the crop; it did not leave the page
        // reset geometry
        d.ResetGeometryCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(s.Geometry.IsIdentity);
        Assert.Equal(8320, v.Pyramid!.Image.Width);
    }

    [AvaloniaFact]
    public async Task Rotate_and_flip_turn_the_picture_and_undo_them()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var s = h.Session();
        var v = h.Viewer();
        d.RotateRightCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await h.SettleAsync(timeoutMs: 100, waitThumbnails: false);
        await h.SettlePictureAsync();
        Assert.Equal(6240, v.Pyramid!.Image.Width);
        Assert.Contains("6240 × 8320", d.HeaderText);
        Assert.Equal("Rotate right", s.History[^1]);
        d.FlipHorizontalCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(s.Geometry.Mirrored);
        Assert.Contains("flipped", d.GeometryText);
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.OemOpenBrackets, RawInputModifiers.Control);   // Ctrl+[ rotate left
        await h.SettlePictureAsync();
        Assert.Equal("Rotate left", s.History[^1]);
        Assert.Equal(8320, v.Pyramid!.Image.Width);
        d.FlipVerticalCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal("Flip vertical", s.History[^1]);
        // undo all four: back to the photo as opened
        for (int i = 0; i < 4; i++) d.UndoCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(s.Geometry.IsIdentity);
        Assert.Equal(8320, v.Pyramid!.Image.Width);
    }

    [AvaloniaFact]
    public async Task Rotate_as_held_is_offered_only_when_the_camera_was_held_turned()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        var held = h.Library.Items.First(i => i.Item.HeldOrientation is PhotoOrientation.Rotate90 or PhotoOrientation.Rotate270 && !i.HasProblem);
        h.Vm.OpenInDevelop(held);
        await h.SettlePictureAsync();
        var d = h.Develop();
        // shown as held already (the display setting): upright, portrait — and exported that way: nothing offered
        Assert.True(h.Viewer().Pyramid!.Image.Height > h.Viewer().Pyramid!.Image.Width);
        Assert.False(d.CanRotateAsHeld);
        // with the display setting off the photo is shown sideways (as Lumen shows it): the button turns it, one step
        h.Services.Library.Settings.ShowPhotosAsHeld = false;
        await d.OpenAsync(h.Library.Items.First(i => i.Id == held.Id));
        await h.SettlePictureAsync();
        Assert.True(h.Viewer().Pyramid!.Image.Width > h.Viewer().Pyramid!.Image.Height);
        Assert.True(d.CanRotateAsHeld);
        Assert.Equal("Rotate as the camera was held", d.RotateAsHeldLabel);
        d.RotateAsHeldCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.False(d.CanRotateAsHeld);
        Assert.Equal(Turn.FromLibrary(held.Item.HeldOrientation)!.Value.Degrees, h.Session().Geometry.TurnDegrees);
        Assert.True(h.Viewer().Pyramid!.Image.Height > h.Viewer().Pyramid!.Image.Width);
        Assert.Equal("Rotate as the camera was held", h.Session().History[^1]);
        h.Services.Library.Settings.ShowPhotosAsHeld = true;
        // another held-turned photo, shown as held: Rotate right turns what is shown (the held turn is baked in, one step named for the action)
        var other = h.Library.Items.First(i => i != held && i.Item.HeldOrientation is PhotoOrientation.Rotate90 or PhotoOrientation.Rotate270 && !i.HasProblem);
        await d.OpenAsync(other);
        await h.SettlePictureAsync();
        Assert.True(h.Viewer().Pyramid!.Image.Height > h.Viewer().Pyramid!.Image.Width);   // upright, as held
        d.RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(h.Viewer().Pyramid!.Image.Width > h.Viewer().Pyramid!.Image.Height);   // turned a quarter from what was shown
        Assert.Equal(new[] { "Opened", "Rotate right" }, h.Session().History);
        Assert.False(d.CanRotateAsHeld);                     // the orientation is decided now
        // a photo held level: never offered
        var level = h.Library.Items.First(i => i.Item.HeldOrientation == PhotoOrientation.Normal && !i.HasProblem);
        await d.OpenAsync(level);
        await h.SettlePictureAsync();
        Assert.False(d.CanRotateAsHeld);
    }

    // ================================================================================================================ before / after

    [AvaloniaFact]
    public async Task Before_after_split_and_toggle()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var v = h.Viewer();
        Type(h, AdjustmentKeys.Exposure, "1");
        d.SetModeCommand.Execute(DevelopMode.BeforeAfter);
        await h.SettlePictureAsync();
        Assert.Equal(CompareMode.Split, v.Compare);
        Assert.True(v.Split.IsVisible);
        Assert.Contains(h.Session().FakeTiles.Requests, r => r.Channel == Channels.Before);
        Assert.True(v.CacheOf(Channels.Before).Count > 0);
        d.SetCompareStyleCommand.Execute(CompareStyle.Toggle);
        Assert.Equal(CompareMode.ToggleAfter, v.Compare);
        v.Focus();
        Key(h, Avalonia.Input.Key.OemBackslash);
        Assert.Equal(CompareMode.ToggleBefore, v.Compare);
        Assert.Contains("Showing before", d.BeforeAfterLabel);
        Key(h, Avalonia.Input.Key.OemBackslash);
        Assert.Equal(CompareMode.ToggleAfter, v.Compare);
        // Photo + depth: the photo beside its depth map, no comparison
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        Assert.Equal(DevelopMode.PhotoDepth, d.Mode);
        Assert.Equal(CompareMode.Off, v.Compare);
        d.SetModeCommand.Execute(DevelopMode.Single);
        Assert.Equal(CompareMode.Off, v.Compare);
    }

    // ================================================================================================================ quality, processing

    [AvaloniaFact]
    public async Task Quality_selector_upgrade_notice_processing_then_ready_and_release()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        var item = h.Library.Items.First(i => i.Item.CacheLevel == CacheLevel.Linear13 && !i.HasProblem && !i.IsBusy);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        var d = h.Develop();
        Assert.True(d.Quality.IsStandard);
        Assert.Equal(1, h.Viewer().Pyramid != null ? h.Session().Tiles.FinestLevel : -1);
        // one click on Full detail: the storage notice
        await d.Quality.ChooseCommand.ExecuteAsync(ViewQuality.FullDetail);
        Assert.True(d.Quality.HasNotice);
        Assert.True(d.Quality.Notice!.IsUpgrade);
        Assert.Contains("MB more on disk", d.Quality.Notice.Text);
        d.Quality.Notice.ConfirmCommand.Execute(null);
        await h.SettleAsync(() => d.IsProcessing, waitThumbnails: false);
        Assert.False(d.Quality.HasNotice);
        Assert.True(d.IsProcessing);
        Assert.Equal(Lux.App.Controls.ImageState.Processing, h.Viewer().State);
        h.Window.RenderFrame();
        Assert.StartsWith("Processing image", h.Viewer().ProcessingText);
        Assert.StartsWith("Processing image", d.StatusQuality);
        // the job runs: progress shows, then the picture is ready at full detail
        h.Lib.FakeJobs.Tick(5);
        await h.SettleAsync(waitThumbnails: false);
        for (int i = 0; i < 200 && d.IsProcessing; i++) { h.Lib.FakeJobs.Tick(5); await h.SettleAsync(timeoutMs: 100, waitThumbnails: false); }
        Assert.False(d.IsProcessing);
        Assert.True(d.Quality.IsFull);
        Assert.Equal(0, h.Session().Tiles.FinestLevel);
        h.Viewer().ZoomToActual(animate: false);
        await h.SettlePictureAsync();
        Assert.Equal(0, h.Viewer().LevelOnScreen);
        Assert.True(d.IsFullOnScreen);
        // back down: a confirmation, then the release
        await d.Quality.ChooseCommand.ExecuteAsync(ViewQuality.Linear13);
        Assert.True(d.Quality.HasNotice);
        Assert.False(d.Quality.Notice!.IsUpgrade);
        d.Quality.Notice.ConfirmCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs(false).Any(j => j.CaptureId == item.Id && j.Kind == JobKind.CacheRelease), waitThumbnails: false);
        Assert.Contains(h.Lib.Jobs.GetJobs(false), j => j.CaptureId == item.Id && j.Kind == JobKind.CacheRelease);
    }

    [AvaloniaFact]
    public async Task A_capture_being_analysed_opens_blurred_with_Processing_image()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        var busy = h.Library.Items.First(i => i.Item.ActiveJob == JobKind.Analysis && i.Item.ActiveJobState == JobState.Running);
        h.Vm.OpenInDevelop(busy);
        await h.SettleAsync(() => h.Develop().Session != null, waitThumbnails: false);
        var d = h.Develop();
        Assert.True(d.IsProcessing);
        Assert.Equal(Lux.App.Controls.ImageState.Processing, h.Viewer().State);
        await h.SettleAsync(timeoutMs: 600, waitThumbnails: false);
        h.Viewer().InvalidateVisual();
        h.Window.RenderFrame();
        Assert.Matches(@"^Processing image · \d+ %$", h.Viewer().ProcessingText);
        // editing still works meanwhile (it is saved; the picture follows when ready)
        Type(h, AdjustmentKeys.Exposure, "0.3");
        Assert.Equal(0.3, h.Session().Get(AdjustmentKeys.Exposure), 9);
    }

    // ================================================================================================================ copy / paste, presets

    [AvaloniaFact]
    public async Task Copy_paste_and_presets_in_Develop()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        Type(h, AdjustmentKeys.Exposure, "0.5");
        Type(h, AdjustmentKeys.Vibrance, "20");
        d.RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        // Ctrl+C: the dialog, tone and color chosen by default, rotation not
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.C, RawInputModifiers.Control);
        var dlg = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        Assert.Equal(PatchKind.Copy, dlg.Kind);
        Assert.True(dlg.Groups.First(g => g.Group == EditGroups.Tone).IsOn);
        Assert.False(dlg.Groups.First(g => g.Group == EditGroups.RotationAndMirror).IsOn);
        Assert.True(dlg.PerSlider);
        Assert.Equal("+0.50", dlg.Groups.First(g => g.Group == EditGroups.Tone).Sliders.First(x => x.Key == AdjustmentKeys.Exposure).Detail);
        await dlg.OkCommand.ExecuteAsync(null);
        Assert.Null(h.Vm.Dialog);
        Assert.NotNull(h.Services.Develop.Clipboard);
        Assert.Equal(EditGroups.ToneAndColour, h.Services.Develop.Clipboard!.Groups);
        // the next photo: Ctrl+V pastes everything copied (one step), nothing of the rotation
        await d.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        var s2 = h.Session();
        Assert.NotEqual("L16_00493", s2.Item.Name);
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.V, RawInputModifiers.Control);
        Assert.Equal(0.5, s2.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal(20, s2.Get(AdjustmentKeys.Vibrance), 9);
        Assert.Equal(0, s2.Geometry.TurnDegrees);
        Assert.Equal("Paste adjustments", s2.History[^1]);
        // Ctrl+Shift+V: choose; with nothing ticked the button is off; one slider only (Lumen's per-property ticks)
        Type(h, AdjustmentKeys.Exposure, "0");
        Type(h, AdjustmentKeys.Vibrance, "0");
        Key(h, Avalonia.Input.Key.V, RawInputModifiers.Control | RawInputModifiers.Shift);
        var paste = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        paste.AllOn = false;
        Assert.False(paste.CanOk);
        var tone = paste.Groups.First(g => g.Group == EditGroups.Tone);
        tone.Sliders.First(x => x.Key == AdjustmentKeys.Exposure).IsOn = true;
        Assert.Null(tone.IsOn);                                  // mixed
        Assert.True(paste.CanOk);
        await paste.OkCommand.ExecuteAsync(null);
        Assert.Equal(0.5, s2.Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal(0, s2.Get(AdjustmentKeys.Vibrance), 9);     // not ticked: left alone
        Assert.Equal(new HashSet<string> { AdjustmentKeys.Exposure }, paste.ChosenSet.Sliders);
        // save a preset (name + groups), then apply it on another photo
        d.SavePresetCommand.Execute(null);
        var pre = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        Assert.True(pre.IsPreset);
        Assert.False(pre.CanOk);                                 // a name is needed
        pre.Name = "Bright and lively";
        await pre.OkCommand.ExecuteAsync(null);
        await h.SettleAsync(() => d.Presets.Any(p => p.Name == "Bright and lively"), waitThumbnails: false);
        Assert.Contains(d.Presets, p => p.Name == "Bright and lively");
        // the same name again is refused with the library's message
        d.SavePresetCommand.Execute(null);
        var dup = (PatchDialogViewModel)h.Vm.Dialog!;
        dup.Name = "bright and lively";
        await dup.OkCommand.ExecuteAsync(null);
        Assert.Contains("exists already", dup.Error);
        dup.CancelCommand.Execute(null);
        await d.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        d.ApplyPresetCommand.Execute(d.Presets.First(p => p.Name == "Bright and lively"));
        Assert.Equal(0.5, h.Session().Get(AdjustmentKeys.Exposure), 9);
        Assert.Equal("Preset “Bright and lively”", h.Session().History[^1]);
        // delete it (confirmed)
        d.DeletePresetCommand.Execute(d.Presets.First(p => p.Name == "Bright and lively"));
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => d.Presets.All(p => p.Name != "Bright and lively"), waitThumbnails: false);
        Assert.DoesNotContain(d.Presets, p => p.Name == "Bright and lively");
    }

    [AvaloniaFact]
    public async Task Library_copy_from_one_and_paste_to_a_selection_with_undo()
    {
        await using var h = await DevelopHarness.OpenAsync();
        Type(h, AdjustmentKeys.Exposure, "0.4");
        await h.SettlePictureAsync();
        var source = h.Develop().Item!;
        h.Vm.CurrentPage = Page.Library;
        h.Library.Click(source, false, false);
        Assert.True(h.Library.CanCopyAdjustments);
        await h.Library.CopyAdjustmentsCommand.ExecuteAsync(null);
        var dlg = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        await dlg.OkCommand.ExecuteAsync(null);
        // three others: Paste to 3
        var targets = h.Library.Items.Where(i => i != source && !i.HasProblem).Take(3).ToList();
        h.Library.Click(targets[0], false, false);
        h.Library.Click(targets[1], true, false);
        h.Library.Click(targets[2], true, false);
        Assert.True(h.Library.CanPasteAdjustments);
        Assert.Equal("Paste to 3", h.Library.PasteLabel);
        await h.Library.PasteAdjustmentsCommand.ExecuteAsync(null);
        await h.SettleAsync(() => targets.All(t => h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary == "exposure +0.40"), waitThumbnails: false);
        Assert.All(targets, t => Assert.Equal("exposure +0.40", h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary));
        Assert.Equal("Undo", h.Vm.NoticeAction);
        Assert.Contains("pasted to 3 photos", h.Vm.Notice);
        // the notice's Undo puts them back
        h.Vm.RunNoticeActionCommand.Execute(null);
        await h.SettleAsync(() => targets.All(t => h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary == null), waitThumbnails: false);
        Assert.All(targets, t => Assert.Null(h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary));
    }

    [AvaloniaFact]
    public async Task Preset_rename_and_apply_to_a_library_selection_with_undo()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        // rename in Develop (the preset's context menu); a name in use is refused
        d.RenamePresetCommand.Execute(d.Presets.First(p => p.Name == "Crisp"));
        var prompt = Assert.IsType<PromptViewModel>(h.Vm.Dialog);
        Assert.Equal("Crisp", prompt.Value);
        prompt.Value = "warm evening";
        await prompt.OkCommand.ExecuteAsync(null);
        Assert.Contains("exists already", prompt.Error);
        prompt.Value = "Crisp and clear";
        await prompt.OkCommand.ExecuteAsync(null);
        await h.SettleAsync(() => d.Presets.Any(p => p.Name == "Crisp and clear") && h.Library.Presets.Any(p => p.Name == "Crisp and clear"), waitThumbnails: false);
        Assert.DoesNotContain(d.Presets, p => p.Name == "Crisp");
        // apply to a selection in the library: one batch, undo from the notice
        h.Vm.CurrentPage = Page.Library;
        var targets = h.Library.Items.Where(i => i.Id != d.Item!.Id && !i.HasProblem).Take(2).ToList();
        h.Library.Click(targets[0], false, false);
        h.Library.Click(targets[1], true, false);
        Assert.True(h.Library.HasPresets);
        await h.Library.ApplyPresetToSelectionCommand.ExecuteAsync(h.Library.Presets.First(p => p.Name == "Black and white"));
        await h.SettleAsync(() => targets.All(t => h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary?.Contains("saturation −100") == true), waitThumbnails: false);
        Assert.All(targets, t => Assert.Contains("saturation −100", h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary));
        Assert.Contains("Black and white", h.Vm.Notice);
        Assert.Equal("Undo", h.Vm.NoticeAction);
        h.Vm.RunNoticeActionCommand.Execute(null);
        await h.SettleAsync(() => targets.All(t => h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary == null), waitThumbnails: false);
        Assert.All(targets, t => Assert.Null(h.Lib.Get(t.Id)!.Edits.AdjustmentsSummary));
        // the open photo in a selection takes the preset as a step in its own history
        h.Library.Click(h.Library.Items.First(i => i.Id == d.Item!.Id), false, false);
        await h.Library.ApplyPresetToSelectionCommand.ExecuteAsync(h.Library.Presets.First(p => p.Name == "Warm evening"));
        Assert.Equal("Preset “Warm evening”", h.Session().History[^1]);
    }

    [AvaloniaFact]
    public async Task Edits_are_saved_and_the_library_thumbnail_is_made_again()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var item = h.Develop().Item!;
        int version = item.ThumbVersion;
        Type(h, AdjustmentKeys.Temperature, "8000");
        await h.SettleAsync(() => h.Session().Save.Status == SaveStatus.Saved, waitThumbnails: false);
        Assert.Equal("Edits saved · with the photo", h.Develop().SaveText);
        Assert.Equal(h.Develop().SaveText, h.Vm.StatusCache);
        await h.SettleAsync(() => item.ThumbVersion > version, waitThumbnails: false);
        Assert.True(item.ThumbVersion > version);
        Assert.Equal("temp 8000 K", h.Lib.Get(item.Id)!.Edits.AdjustmentsSummary);
    }

    // ================================================================================================================ filmstrip, rating, keys

    [AvaloniaFact]
    public async Task Filmstrip_next_previous_rating_and_the_arrow_keys()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var first = d.Item!;
        int index = h.Library.Items.ToList().IndexOf(first);
        h.Viewer().Focus();
        // at fit the arrows step through the captures
        Key(h, Avalonia.Input.Key.Right);
        await h.SettlePictureAsync();
        Assert.Equal(h.Library.Items[index + 1].Id, d.Item!.Id);
        Key(h, Avalonia.Input.Key.Left);
        await h.SettlePictureAsync();
        Assert.Equal(first.Id, d.Item!.Id);
        Key(h, Avalonia.Input.Key.PageDown);
        await h.SettlePictureAsync();
        Assert.Equal(h.Library.Items[index + 1].Id, d.Item!.Id);
        Assert.Equal(h.Library.Items[index + 1].Id, h.Library.CursorItem?.Id);   // the library follows
        // zoomed in, they pan instead
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.Z);
        await h.SettlePictureAsync();
        Assert.True(h.Viewer().CanPan);
        var off = h.Viewer().Offset;
        var before = d.Item!.Id;
        Key(h, Avalonia.Input.Key.Right);
        Assert.Equal(before, d.Item!.Id);
        Assert.NotEqual(off, h.Viewer().Offset);
        // rating keys rate the open capture (saved with the photo)
        Key(h, Avalonia.Input.Key.D3);
        await h.SettleAsync(() => h.Lib.Get(before)!.Rating == 3, waitThumbnails: false);
        Assert.Equal(3, h.Lib.Get(before)!.Rating);
        Assert.Equal(3, d.Rating);
        d.Rate("5");
        await h.SettleAsync(() => h.Lib.Get(before)!.Rating == 5, waitThumbnails: false);
        // a click on a filmstrip cell opens it
        await h.SettleAsync();
        var target = h.Library.Items[index + 3];
        var cell = h.DevelopView().GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("stripcell") && b.DataContext == target);
        Assert.NotNull(cell);
        var p = cell!.TranslatePoint(new Point(30, 25), h.Window)!.Value;
        h.Window.MouseDown(p, MouseButton.Left);
        h.Window.MouseUp(p, MouseButton.Left);
        await h.SettlePictureAsync();
        Assert.Equal(target.Id, d.Item!.Id);
        Assert.True(cell.Classes.Contains("current"));
        // Ctrl+F hides the filmstrip
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Assert.False(d.FilmstripVisible);
        Key(h, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Assert.True(d.FilmstripVisible);
    }

    [AvaloniaFact]
    public async Task Keyboard_map_zoom_info_tools_and_escape()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var v = h.Viewer();
        v.Focus();
        Key(h, Avalonia.Input.Key.OemPlus, RawInputModifiers.Control);
        v.CompleteAnimation();
        Assert.False(v.IsFitted);
        Key(h, Avalonia.Input.Key.D0, RawInputModifiers.Control);
        Assert.True(v.IsFitted);
        Key(h, Avalonia.Input.Key.D0, RawInputModifiers.Control | RawInputModifiers.Alt);
        v.CompleteAnimation();
        Assert.Equal(100, v.ZoomPercent, 3);
        Key(h, Avalonia.Input.Key.Z);
        Assert.True(v.IsFitted);
        Assert.False(d.InfoExpanded);
        Key(h, Avalonia.Input.Key.I, RawInputModifiers.Control);
        Assert.True(d.InfoExpanded);
        Assert.Contains(d.Info, r => r.Label == "Size" && r.Value == "8320 × 6240");
        // depth tools: E opens refine edge
        Key(h, Avalonia.Input.Key.E);
        Assert.Equal(PageTool.RefineEdge, d.Depth.Tool);
        Assert.All(d.Tools, t => Assert.True(t.IsAvailable));
        // Ctrl+E: the export side panel, which ends the depth tool; Esc closes it
        Key(h, Avalonia.Input.Key.E, RawInputModifiers.Control);
        Assert.True(d.IsExportOpen);
        Assert.Equal(PageTool.None, d.Depth.Tool);
        Key(h, Avalonia.Input.Key.Escape);
        Assert.False(d.IsExportOpen);
        Assert.True(h.Vm.IsDevelop);
        // with a depth tool on, Esc leaves the tool first, then the page
        v.Focus();
        Key(h, Avalonia.Input.Key.E);
        Assert.Equal(PageTool.RefineEdge, d.Depth.Tool);
        Key(h, Avalonia.Input.Key.Escape);                              // the depth tool
        Assert.Equal(PageTool.None, d.Depth.Tool);
        Assert.True(h.Vm.IsDevelop);
        Key(h, Avalonia.Input.Key.Escape);
        Assert.True(h.Vm.IsLibrary);
        // the Develop tab brings the open photo back
        h.Vm.ShowPageCommand.Execute(Page.Develop);
        Assert.True(h.Vm.IsDevelop);
        Assert.NotNull(d.Session);
    }

    // ================================================================================================================ export

    [AvaloniaFact]
    public async Task Export_from_Develop_takes_the_settings_on_screen()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        Type(h, AdjustmentKeys.Exposure, "0.7");
        Type(h, AdjustmentKeys.Clarity, "30");
        d.OpenExportCommand.Execute(null);
        var panel = d.ExportPanel!;
        Assert.Equal($"Export {d.Item!.Name}", panel.Title);
        int jobs = h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Export);
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null && h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Export) > jobs, waitThumbnails: false);
        var o = panel.LastOptions!;
        Assert.NotNull(o.Adjustments);
        Assert.Equal(0.7f, o.Adjustments!.Exposure, 6);
        Assert.Equal(30f, o.Adjustments.Clarity, 6);
        Assert.True(o.UseSavedEdits);
        Assert.Equal(h.Session().ExportSettings(new ExportOptions()).Adjustments, o.Adjustments);   // the same settings the viewer renders
        Assert.Equal(SaveStatus.Saved, h.Session().Save.Status);                                     // saved before the export
        Assert.False(d.IsExportOpen);
    }
}
