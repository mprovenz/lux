using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Fake;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.Library;
using Xunit;
using HistoryState = Lux.App.Develop.HistoryState;
using VersionKind = Lux.App.Develop.VersionKind;

namespace Lux.App.Tests;

/// <summary>The depth interface of the Develop page on the fake: the depth effect controls, the focus tool, the shade, Photo + depth,
/// every tool's flow with real pointer events, the pending selection, the brush keys, the not-ready state, slow tools, history and versions,
/// the keyboard map.</summary>
public sealed class DepthTests
{
    static AdjustmentSlider Slider(Harness h, string name) => h.DevelopView().GetVisualDescendants().OfType<AdjustmentSlider>().First(s => s.Name == name);
    static TextBox Box(Harness h, string name) => h.DevelopView().GetVisualDescendants().OfType<TextBox>().First(t => t.Name == name);

    static void Type(Harness h, TextBox box, string text, Key end = Key.Enter)
    {
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.Focus());
        box.Text = text;
        Dispatcher.UIThread.RunJobs();
        h.Window.KeyPress(end, RawInputModifiers.None, PhysicalKey.None, null);
        h.Window.KeyRelease(end, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    static void DragSlider(Harness h, AdjustmentSlider s, double from, double to, int steps = 8)
    {
        s.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Point P(double f) => s.TranslatePoint(new Point(8 + f * (s.Bounds.Width - 16), s.Bounds.Height / 2), h.Window)!.Value;
        h.Window.MouseMove(P(from));
        h.Window.MouseDown(P(from), MouseButton.Left);
        for (int i = 1; i <= steps; i++) { h.Window.MouseMove(P(from + (to - from) * i / steps), RawInputModifiers.LeftMouseButton); Dispatcher.UIThread.RunJobs(); }
        h.Window.MouseUp(P(to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ================================================================================================================ depth effect

    [AvaloniaFact]
    public async Task Depth_effect_controls_off_state_typed_values_and_live_drag()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        Assert.True(d.IsReady);
        Assert.Equal("f/2.0", d.Aperture.ValueText);                    // the saved effect of L16_00493
        Assert.NotNull(s.FakeTiles.Effect);
        int steps = s.History.Count;
        // double-click the name: off (explicit "Off", the effect gone from the tiles, one step)
        var label = h.DevelopView().GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "ApertureLabel");
        label.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = label });
        await h.SettleDepthAsync();
        Assert.Equal("Off", d.Aperture.ValueText);
        Assert.Equal("Off", Box(h, "ApertureBox").Text);
        Assert.True(d.Aperture.IsOff);
        Assert.Null(s.FakeTiles.Effect);
        Assert.Equal("Everything", d.InFocusText);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.Equal("Depth effect off", s.History[^1]);
        // typed values: "2.8" → f/2.8; "f/4" → f/4.0; garbage → error border, value kept; "off" → off
        Type(h, Box(h, "ApertureBox"), "2.8");
        Assert.Equal("f/2.8", d.Aperture.ValueText);
        Assert.Equal(2.8, s.FakeDepth.FNumber!.Value, 6);
        Type(h, Box(h, "ApertureBox"), "f/4");
        Assert.Equal("f/4.0", d.Aperture.ValueText);
        Type(h, Box(h, "ApertureBox"), "wide");
        Assert.True(d.Aperture.HasError);
        Assert.Equal(4.0, s.FakeDepth.FNumber!.Value, 6);
        Type(h, Box(h, "ApertureBox"), "x", Key.Escape);
        Assert.False(d.Aperture.HasError);
        Type(h, Box(h, "ApertureBox"), "off");
        Assert.Null(s.FakeDepth.FNumber);
        // focus typed in metres / centimetres
        Type(h, Box(h, "FocusBox"), "3.5 m");
        Assert.Equal(3500, s.FakeDepth.FocusMm, 0);
        Assert.Equal("3.50 m", d.Focus.ValueText);
        Type(h, Box(h, "FocusBox"), "420 cm");
        Assert.Equal(4200, s.FakeDepth.FocusMm, 0);
        // a live drag of the aperture: the tiles follow at the drag level, no step until the release, one step after
        int before = s.History.Count;
        var slider = Slider(h, "ApertureSlider");
        Point P(double f) => slider.TranslatePoint(new Point(8 + f * (slider.Bounds.Width - 16), slider.Bounds.Height / 2), h.Window)!.Value;
        slider.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        h.Window.MouseDown(P(0.9), MouseButton.Left);
        for (int i = 1; i <= 5; i++) { h.Window.MouseMove(P(0.9 - 0.15 * i), RawInputModifiers.LeftMouseButton); await h.SettleAsync(timeoutMs: 60, waitThumbnails: false); }
        Assert.True(s.FakeTiles.IsDragging);
        Assert.NotNull(s.FakeTiles.Effect);
        Assert.Equal(before, s.History.Count);
        h.Window.MouseUp(P(0.15), MouseButton.Left);
        await h.SettleDepthAsync();
        Assert.False(s.FakeTiles.IsDragging);
        Assert.Equal(before + 1, s.History.Count);
        Assert.StartsWith("Depth effect f/", s.History[^1]);
        Assert.Contains(s.HistoryItems[^1].Kind, new[] { HistoryKind.DepthEffect });
        // the history row is marked as a depth step
        Assert.True(h.Develop().History[^1].IsDepth);
        // focus label double-click: the default focus; the section's Reset: off + default focus
        var focusLabel = h.DevelopView().GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "FocusLabel");
        focusLabel.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = focusLabel });
        await h.SettleDepthAsync();
        Assert.True(s.FakeDepth.FocusIsDefault);
        d.ResetEffectCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.Null(s.FakeDepth.FNumber);
        // undo brings the effect back (combined history)
        h.Develop().UndoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.NotNull(s.FakeDepth.FNumber);
        // saved with the photo (the library's summary mirrors the depth effect)
        await s.SaveNowAsync();
        Assert.NotNull(h.Lib.Get(s.Item.Id)!.Edits.SavedFNumber);
    }

    // ================================================================================================================ focus tool, shade

    [AvaloniaFact]
    public async Task Focus_tool_click_sets_focus_with_pin_invalid_click_explained_and_shade()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var view = h.DevelopView();
        bool shadeBefore = d.ShadeOutsideFocus;
        Assert.False(shadeBefore);
        h.Key(Key.F);
        Assert.Equal(PageTool.Focus, d.Tool);
        Assert.True(d.ShowHint);                                         // Lumen's five-second hint
        Assert.True(d.ShadeOutsideFocus);                                // auto-checked
        Assert.True(view.ShadeOverlay.IsVisible);
        Assert.True(view.PickOverlay.IsVisible);
        Assert.True(h.Develop().Tools[0].IsActive);
        await h.SettleDepthAsync(() => view.ShadeOverlay.Cache.Count > 0);
        h.Window.RenderFrame();
        Assert.True(view.ShadeOverlay.DrawnLastFrame > 0);
        // the cursor: allowed over the house, "not allowed" over the sky (from the depth read under the pointer, off the UI thread)
        var house = h.Element(FakeDepthMap.House);
        var sky = h.Sky();
        var t = h.Viewer().Transform()!;
        async Task<StandardCursorType?> CursorOver(NormPoint p)
        {
            h.Hover(p);
            await h.SettleAsync(() => d.LastPointerDepth?.Point == p, timeoutMs: 3000, waitThumbnails: false);
            return CursorType(view.PickOverlay.CursorAt(t.FrameToView(new Point(p.X, p.Y)), t));
        }
        Assert.Equal(StandardCursorType.Cross, await CursorOver(house));
        Assert.Equal(StandardCursorType.No, await CursorOver(sky));
        int steps = s.History.Count;
        // a valid click: focus at the house's depth, the pin there, one step
        h.Click(house);
        await h.SettleDepthAsync(() => s.History.Count > steps);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.StartsWith("Focus ", s.History[^1]);
        Assert.False(s.FakeDepth.FocusIsDefault);
        Assert.NotNull(view.PinOverlay.Focus);
        Assert.Equal(house.X, view.PinOverlay.Focus!.Value.X, 2);
        Assert.Equal(s.FakeDepth.DepthAt(house)!.Value, s.FakeDepth.FocusMm, 0);
        // an invalid click: the reason, no step, the shake does not run for a focus click (only depth edits shake)
        h.Click(sky);
        await h.SettleDepthAsync();
        Assert.Equal(steps + 1, s.History.Count);
        Assert.True(d.MessageIsWarning);
        Assert.Contains("cannot be brought into focus", d.MessageText);
        // H toggles the shade; F leaves the tool
        h.Key(Key.H);
        Assert.False(d.ShadeOutsideFocus);
        Assert.False(view.ShadeOverlay.IsVisible);
        h.Key(Key.H);
        Assert.True(view.ShadeOverlay.IsVisible);
        h.Key(Key.F);
        Assert.Equal(PageTool.None, d.Tool);
        Assert.False(view.PickOverlay.IsVisible);
        // leaving the tool puts the shade back as it was before entering it
        Assert.Equal(shadeBefore, d.ShadeOutsideFocus);
        Assert.False(view.ShadeOverlay.IsVisible);
        // and an explicit choice made before entering stays: on before → on after
        h.Key(Key.H);
        Assert.True(d.ShadeOutsideFocus);
        h.Key(Key.F); h.Key(Key.H); h.Key(Key.F);                        // in the tool the shade was switched off; leaving restores "on"
        Assert.Equal(PageTool.None, d.Tool);
        Assert.True(d.ShadeOutsideFocus);
    }

    static StandardCursorType? CursorType(Cursor? c) => c == null ? null : Enum.GetValues<StandardCursorType>().FirstOrDefault(x => ReferenceEquals(Cursors.Get(x), c));

    // ================================================================================================================ photo + depth

    [AvaloniaFact]
    public async Task Photo_plus_depth_two_viewers_stay_in_sync_and_read_out_depth()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var view = h.DevelopView();
        var main = h.Viewer();
        var depth = view.DepthPhotoViewer;
        h.Key(Key.D);
        Assert.True(h.Develop().IsPhotoDepth);
        await h.SettlePictureAsync(() => depth.LevelOnScreen >= 0);
        Assert.True(depth.IsVisible);
        Assert.Same(h.Session().FakeDepth.DepthView, depth.Source);
        Assert.Equal(main.Scale, depth.Scale, 9);
        Assert.Equal(main.Offset, depth.Offset);
        // zoom the photo: the depth view follows; pan the depth view: the photo follows
        main.AnimateZoom = false; depth.AnimateZoom = false;
        main.ZoomToActual(animate: false);
        await h.SettlePictureAsync();
        Assert.Equal(main.Scale, depth.Scale, 9);
        Assert.Equal(main.Offset, depth.Offset);
        depth.PanBy(new Vector(-120, -80));
        await h.SettlePictureAsync();
        Assert.Equal(depth.Offset, main.Offset);
        // the legend and the read-out
        Assert.Contains("near is light", view.LegendOverlay.Text);
        main.ZoomToFit();
        await h.SettlePictureAsync();
        var house = h.Element(FakeDepthMap.House);
        // the read-out: x and y at once, the depth read off the UI thread a moment later
        async Task<string> ReadOut(NormPoint p, PhotoViewer? on = null)
        {
            h.Hover(p, on);
            Assert.StartsWith("x ", h.Develop().StatusPointer);
            await h.SettleAsync(() => !h.Develop().StatusPointer.Contains('…') && h.DepthVm().LastPointerDepth?.Point == p, timeoutMs: 3000, waitThumbnails: false);
            return h.Develop().StatusPointer;
        }
        Assert.Matches(@"^x \d+  y \d+  depth \d+\.\d\d m$", await ReadOut(house));
        Assert.Contains("depth", h.Vm.StatusPointer);
        Assert.Matches(@"^x \d+  y \d+  depth \d+\.\d\d m$", await ReadOut(house, depth));
        Assert.EndsWith("depth: far", await ReadOut(h.Sky()));
        // depth only: the main viewer shows the depth map; Shift+D back
        h.Key(Key.D, RawInputModifiers.Shift);
        Assert.True(h.Develop().MainShowsDepth);
        Assert.Same(h.Session().FakeDepth.DepthView, main.Source);
        Assert.False(depth.IsVisible);
        h.Key(Key.D, RawInputModifiers.Shift);
        Assert.Same(h.Session().Tiles, main.Source);
        h.Key(Key.D);
        Assert.True(h.Develop().IsSingle);
    }

    // ================================================================================================================ refine edge, blend area

    [AvaloniaFact]
    public async Task Refine_edge_and_blend_area_strokes_apply_on_release_with_history_undo_redo_and_nothing_changed_explained()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var view = h.DevelopView();
        h.Key(Key.E);
        Assert.Equal(PageTool.RefineEdge, d.Tool);
        Assert.True(view.BrushOverlay.IsVisible);
        Assert.True(d.UsesBrush);
        int steps = s.History.Count;
        // a stroke along the house's left edge (both sides under the brush)
        var a = h.Element(FakeDepthMap.House, -1.75, -1.2);
        var b = h.Element(FakeDepthMap.House, -1.75, 1.6);
        double worst = h.Stroke(new[] { a, b }, steps: 8, release: false);
        Assert.True(view.BrushOverlay.IsDrawing);
        Assert.True(view.BrushOverlay.CurrentStroke.Count > 4);          // the stroke preview follows the pointer
        Assert.Equal(steps, s.History.Count);                              // nothing until the release
        h.Window.MouseUp(h.At(b), MouseButton.Left);
        await h.SettleDepthAsync(() => s.History.Count > steps);
        Assert.Equal("Refine edge", s.History[^1]);
        Assert.True(d.StrokeMoveMs.Max() < 30, $"a stroke move took {d.StrokeMoveMs.Max():0.0} ms on the UI thread");
        // undo / redo
        h.Key(Key.Z, RawInputModifiers.Control);
        await h.SettleDepthAsync();
        Assert.Equal(s.History.Count - 2, s.HistoryPosition);
        h.Key(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        await h.SettleDepthAsync();
        Assert.Equal(s.History.Count - 1, s.HistoryPosition);
        // a stroke in the open sky: nothing changes, the reason is shown, the picture shakes
        int shakes = h.Viewer().ShakeCount;
        var sky = h.Sky();
        h.Stroke(new[] { sky, new NormPoint(sky.X + 0.03, sky.Y) });
        await h.SettleDepthAsync(() => d.HasMessage);
        Assert.Contains("both sides of an edge", d.MessageText);
        Assert.True(d.MessageIsWarning);
        Assert.Equal(shakes + 1, h.Viewer().ShakeCount);
        // blend area over the house's windows (read as far away) — on the clean version (L16_00493's saved edits blended them already)
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Clean"));
        await h.SettleDepthAsync();
        int afterRefine = s.History.Count;
        h.Key(Key.B);
        Assert.Equal(PageTool.BlendArea, d.Tool);
        var w0 = h.Element(FakeDepthMap.Windows, -1.4, 0);
        var w1 = h.Element(FakeDepthMap.Windows, 1.4, 0);
        int FarWindows()
        {
            var map = s.FakeDepth.Map!; var depth = s.FakeDepth.CurrentDepth!;
            return Enumerable.Range(0, depth.Length).Count(i => map.Ids[i] == FakeDepthMap.Windows && depth[i] > 50_000);
        }
        int windowsBefore = FarWindows();
        Assert.True(windowsBefore > 0);
        h.Stroke(new[] { w0, w1 });
        await h.SettleDepthAsync(() => s.History.Count > afterRefine);
        Assert.Equal("Blend area", s.History[^1]);
        int windowsAfter = FarWindows();
        Assert.True(windowsAfter < windowsBefore / 2, $"window pixels read as far: {windowsBefore} → {windowsAfter}");
        // a stroke over the open sky: nothing changed (already even)
        var sky2 = h.Sky();
        h.Stroke(new[] { sky2, new NormPoint(sky2.X + 0.03, sky2.Y) });
        await h.SettleDepthAsync(() => d.HasMessage);
        Assert.Contains("already even", d.MessageText);
        Assert.Equal(afterRefine + 1, s.History.Count);
    }

    // ================================================================================================================ brush size keys

    [AvaloniaFact]
    public async Task Brush_size_keys_and_cursor()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var view = h.DevelopView();
        h.Key(Key.E);
        double size = d.SizeValue, px = d.BrushDiameterPx;
        Assert.Equal(20, size);
        Assert.Equal(Math.Round(2 * 20 * 0.001 * 8320), Math.Round(px));
        Assert.Equal($"{Math.Round(px):0} px", d.SizeText);
        h.Key(Key.OemCloseBrackets);
        Assert.Equal(25, d.SizeValue);
        Assert.Equal(d.BrushDiameterPx, view.BrushOverlay.BrushPx, 6);
        h.Key(Key.OemOpenBrackets);
        h.Key(Key.OemOpenBrackets);
        Assert.Equal(16, d.SizeValue);
        for (int i = 0; i < 30; i++) h.Key(Key.OemOpenBrackets);
        Assert.Equal(1, d.SizeValue);
        for (int i = 0; i < 40; i++) h.Key(Key.OemCloseBrackets);
        Assert.Equal(100, d.SizeValue);
        // the circle is drawn at the pointer, at the brush's size in the picture
        var house = h.Element(FakeDepthMap.House);
        h.Hover(house);
        Assert.NotNull(view.BrushOverlay.HoverPoint);
        var t = h.Viewer().Transform()!;
        Assert.Equal(d.BrushDiameterPx / 2 * t.Scale, BrushOverlay.ViewRadius(view.BrushOverlay.BrushPx, t), 6);
        // Ctrl+[ still rotates
        h.Key(Key.OemOpenBrackets, RawInputModifiers.Control);
        await h.SettleDepthAsync();
        Assert.Equal("Rotate left", h.Session().History[^1]);
    }

    // ================================================================================================================ match depth

    [AvaloniaFact]
    public async Task Match_depth_brush_lasso_and_pick_from_photo()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var view = h.DevelopView();
        h.Key(Key.M);
        Assert.Equal(PageTool.MatchDepth, d.Tool);
        Assert.True(d.IsMatchBrush);
        int steps = s.History.Count;
        // brush with no depth chosen: the stroke becomes the selection, Apply waits for a depth
        var star = h.Element(FakeDepthMap.Star);
        h.Stroke(new[] { h.Element(FakeDepthMap.Star, -1, 0), h.Element(FakeDepthMap.Star, 1, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        Assert.True(d.HasPending);
        Assert.False(d.CanApply);
        Assert.Contains("choose the depth", d.PendingText);
        Assert.Equal(steps, s.History.Count);
        // type the depth, Apply (Enter): one step
        Type(h, Box(h, "TargetBox"), "3.2");
        Assert.Equal("3.20 m", d.TargetText);
        Assert.True(d.CanApply);
        h.Key(Key.Enter);
        await h.SettleDepthAsync(() => s.History.Count > steps);
        Assert.Equal("Match depth 3.20 m", s.History[^1]);
        Assert.Equal(3200, s.FakeDepth.DepthAt(star)!.Value, 0);
        Assert.False(d.HasPending);
        // with a depth chosen, the brush paints it on release
        int steps2 = s.History.Count;
        Type(h, Box(h, "TargetBox"), "5 m");
        var sign = h.Element(FakeDepthMap.Sign);
        h.Stroke(new[] { h.Element(FakeDepthMap.Sign, -1, 0), h.Element(FakeDepthMap.Sign, 1, 0) });
        await h.SettleDepthAsync(() => s.History.Count > steps2);
        Assert.Equal("Match depth 5.00 m", s.History[^1]);
        // lasso: four corners around a tree → the selection (the mask over the picture), a picked depth, Enter
        d.SetModeCommand.Execute(MatchMode.Lasso);
        Assert.True(d.IsLasso);
        Assert.True(view.LassoOverlay.IsVisible);
        Assert.Equal("Feather", d.SizeLabel);
        var tree = h.Element(FakeDepthMap.TreeBase + 3);
        foreach (var (dx, dy) in new[] { (-2.5, -2.5), (2.5, -2.5), (2.5, 2.5), (-2.5, 2.5) }) h.Click(h.Element(FakeDepthMap.TreeBase + 3, dx, dy));
        await h.SettleDepthAsync(() => d.HasPending);
        Assert.Equal(4, view.LassoOverlay.Points.Count);
        Assert.True(d.HasPending);
        Assert.True(view.MaskOverlay.IsVisible);
        await h.SettleDepthAsync(() => view.MaskOverlay.Cache.Count > 0);
        // pick from photo: the next click picks the depth (the reference pin), Esc would leave picking
        d.PickCommand.Execute(null);
        Assert.True(d.IsPicking);
        Assert.True(view.PickOverlay.IsVisible);
        Assert.False(view.LassoOverlay.IsVisible);
        var house = h.Element(FakeDepthMap.House);
        h.Click(house);
        Assert.False(d.IsPicking);
        Assert.True(d.TargetIsPicked);
        Assert.NotNull(view.PinOverlay.Reference);
        Assert.Equal(DepthText.Distance(s.FakeDepth.DepthAt(house)!.Value), d.TargetText);
        int steps3 = s.History.Count;
        h.Key(Key.Enter);
        await h.SettleDepthAsync(() => s.History.Count > steps3);
        Assert.StartsWith("Match depth ", s.History[^1]);
        Assert.EndsWith("· lasso", s.History[^1]);
        Assert.Empty(view.LassoOverlay.Points);                          // the selection is gone after Apply
        // Alt + click picks too
        h.Click(star, RawInputModifiers.Alt);
        Assert.True(d.TargetIsPicked);
        // a freehand lasso: drag around
        var c = h.Element(FakeDepthMap.Star);
        h.Stroke(new[] { new NormPoint(c.X - 0.03, c.Y - 0.03), new NormPoint(c.X + 0.03, c.Y - 0.03), new NormPoint(c.X + 0.03, c.Y + 0.03), new NormPoint(c.X - 0.03, c.Y + 0.03) });
        await h.SettleDepthAsync(() => d.HasPending);
        Assert.True(view.LassoOverlay.IsClosed);
        Assert.True(d.HasPending);
        // Esc: the selection goes, then the tool
        h.Key(Key.Escape);
        Assert.False(d.HasPending);
        h.Key(Key.Escape);
        Assert.Equal(PageTool.None, d.Tool);
        Assert.True(h.Vm.IsDevelop);
    }

    [AvaloniaFact]
    public async Task Quick_select_add_remove_clear_apply_and_cancel()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var view = h.DevelopView();
        h.Key(Key.Q);
        Assert.True(d.IsQuickSelect);
        Assert.Equal("Quick select", d.ToolTitle);
        Assert.True(h.Develop().Tools[4].IsActive);
        Assert.False(h.Develop().Tools[3].IsActive);
        Assert.True(view.BrushOverlay.KeepStrokes);
        int steps = s.History.Count;
        // add the house: the selection grows along its outline (the mask layer), nothing in the depth map changes
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.5, 0), h.Element(FakeDepthMap.House, 0.5, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        double houseOnly = s.FakeDepth.Pending!.SelectedFraction;
        Assert.True(houseOnly > 0);
        Assert.Equal(1, view.BrushOverlay.KeptStrokes);
        Assert.Equal(steps, s.History.Count);
        await h.SettleDepthAsync(() => view.MaskOverlay.Cache.Count > 0);
        h.Window.RenderFrame();
        Assert.True(view.MaskOverlay.DrawnLastFrame > 0);
        // add the star, then remove it again (a dark stroke)
        h.Stroke(new[] { h.Element(FakeDepthMap.Star, -0.3, 0), h.Element(FakeDepthMap.Star, 0.3, 0) });
        await h.SettleDepthAsync(() => s.FakeDepth.Pending!.SelectedFraction > houseOnly);
        Assert.True(s.FakeDepth.Pending!.SelectedFraction > houseOnly);
        d.SetAddCommand.Execute("false");
        Assert.True(view.BrushOverlay.Dark);
        h.Stroke(new[] { h.Element(FakeDepthMap.Star, -0.3, 0), h.Element(FakeDepthMap.Star, 0.3, 0) });
        await h.SettleDepthAsync(() => Math.Abs(s.FakeDepth.Pending!.SelectedFraction - houseOnly) < houseOnly * 0.2);
        Assert.Equal(houseOnly, s.FakeDepth.Pending!.SelectedFraction, 2);
        Assert.Equal(3, view.BrushOverlay.KeptStrokes);
        // Clear: the selection and the strokes go, the tool stays
        d.ClearCommand.Execute(null);
        Assert.False(d.HasPending);
        Assert.Equal(0, view.BrushOverlay.KeptStrokes);
        Assert.True(d.IsQuickSelect);
        // again, a depth, Apply
        d.SetAddCommand.Execute("true");
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.5, 0), h.Element(FakeDepthMap.House, 0.5, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        Type(h, Box(h, "TargetBox"), "3.2");
        d.ApplyCommand.Execute(null);
        await h.SettleDepthAsync(() => s.History.Count > steps);
        Assert.Equal("Quick select · 3.20 m", s.History[^1]);
        Assert.Equal(3200, s.FakeDepth.DepthAt(h.Element(FakeDepthMap.House))!.Value, 0);
        Assert.False(d.HasPending);
        // undo / redo of the apply
        h.Develop().UndoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.NotEqual(3200, s.FakeDepth.DepthAt(h.Element(FakeDepthMap.House))!.Value, 0);
        h.Develop().RedoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.Equal(3200, s.FakeDepth.DepthAt(h.Element(FakeDepthMap.House))!.Value, 0);
        // a selection cancelled with Esc changes nothing
        int steps2 = s.History.Count;
        h.Stroke(new[] { h.Element(FakeDepthMap.Star, -0.3, 0), h.Element(FakeDepthMap.Star, 0.3, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        h.Key(Key.Escape);
        Assert.False(d.HasPending);
        Assert.Equal(steps2, s.History.Count);
    }

    [AvaloniaFact]
    public async Task Strokes_and_clicks_follow_the_picture_as_shown_after_a_turn_and_at_any_zoom()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        h.Develop().RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Contains("6240 × 8320", h.Develop().HeaderText);
        // the house where it is shown now (turned): a quick-select stroke there selects the house
        var view = h.DevelopView();
        h.Viewer().AnimateZoom = false;
        h.Viewer().ZoomToPercent(30, animate: false);
        await h.SettlePictureAsync();
        var t = h.Viewer().Transform()!;
        var house = h.Element(FakeDepthMap.House);
        h.Viewer().PanBy(new Point(h.Viewer().Bounds.Width / 2, h.Viewer().Bounds.Height / 2) - t.FrameToView(new Point(house.X, house.Y)));
        await h.SettlePictureAsync();
        h.Key(Key.Q);
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.4, 0), h.Element(FakeDepthMap.House, 0.4, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        var map = s.FakeDepth.Map!;
        var mask = (byte[])typeof(FakeDepthSession).GetField("_pendingMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s.FakeDepth)!;
        int onHouse = Enumerable.Range(0, mask.Length).Count(i => mask[i] > 127 && map.Ids[i] is FakeDepthMap.House or FakeDepthMap.Windows);
        int elsewhere = Enumerable.Range(0, mask.Length).Count(i => mask[i] > 127 && map.Ids[i] is not (FakeDepthMap.House or FakeDepthMap.Windows));
        Assert.True(onHouse > 100 && elsewhere < onHouse / 10, $"selected {onHouse} house pixels and {elsewhere} others");
        // the focus tool on the turned picture: the depth of the house
        h.Key(Key.Escape); h.Key(Key.Escape);
        h.Key(Key.F);
        var wall = h.Element(FakeDepthMap.House, 0.9, -0.9);
        double expected = s.FakeDepth.DepthAt(wall)!.Value;
        Assert.InRange(expected, 3000, 7000);                            // the house's depth, read where it is shown
        h.Click(wall);
        await h.SettleDepthAsync(() => s.History[^1].StartsWith("Focus"));
        Assert.Equal(expected, s.FakeDepth.FocusMm, 0);
    }

    [AvaloniaFact]
    public async Task Focus_slider_previews_live_and_makes_one_step()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        int steps = s.History.Count;
        var slider = Slider(h, "FocusSlider");
        slider.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Point P(double f) => slider.TranslatePoint(new Point(8 + f * (slider.Bounds.Width - 16), slider.Bounds.Height / 2), h.Window)!.Value;
        double focus0 = s.FakeTiles.Effect!.FocusMm;
        h.Window.MouseDown(P(0.3), MouseButton.Left);
        for (int i = 1; i <= 5; i++) { h.Window.MouseMove(P(0.3 + 0.08 * i), RawInputModifiers.LeftMouseButton); await h.SettleAsync(timeoutMs: 60, waitThumbnails: false); }
        Assert.True(s.FakeTiles.IsDragging);                             // live, at the drag level
        Assert.NotEqual(focus0, s.FakeTiles.Effect!.FocusMm, 0);
        Assert.Equal(steps, s.History.Count);
        h.Window.MouseUp(P(0.7), MouseButton.Left);
        await h.SettleDepthAsync();
        Assert.False(s.FakeTiles.IsDragging);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.StartsWith("Focus ", s.History[^1]);
    }

    [AvaloniaFact]
    public async Task Quick_select_strokes_queue_and_stopping_leaves_no_change()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var view = h.DevelopView();
        h.Key(Key.Q);
        s.FakeDepth.SlowFactor = 40000;                                   // a stroke ≈ 8 min: it is still running when the second stroke ends,
                                                                          // however slow the machine (at ≈ 5 s a loaded run drew the second
                                                                          // stroke after the first had finished); it is stopped below
        int steps = s.History.Count;
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.5, 0), h.Element(FakeDepthMap.House, 0.5, 0) });
        await h.SettleAsync(() => d.IsBusy, timeoutMs: 3000, waitThumbnails: false);
        h.Stroke(new[] { h.Element(FakeDepthMap.Star, -0.3, 0), h.Element(FakeDepthMap.Star, 0.3, 0) });
        await h.SettleAsync(() => d.ActivityText.Contains("1 more waiting"), timeoutMs: 3000, waitThumbnails: false);
        Assert.Contains("1 more waiting", d.ActivityText);              // the newer stroke waits its turn; the first is not abandoned
        Assert.Equal(2, view.BrushOverlay.KeptStrokes);
        Assert.True(d.CanCancelActivity);
        d.CancelActivityCommand.Execute(null);
        await h.SettleDepthAsync(() => !d.IsBusy && view.BrushOverlay.KeptStrokes == 0);
        Assert.False(d.IsBusy);
        Assert.Equal(0, view.BrushOverlay.KeptStrokes);                  // both strokes left the picture
        Assert.False(d.HasPending);                                      // the selection did not change
        Assert.Equal(steps, s.History.Count);
        // a stroke after stopping works as usual
        s.FakeDepth.SlowFactor = 1;
        h.Stroke(new[] { h.Element(FakeDepthMap.House, -0.5, 0), h.Element(FakeDepthMap.House, 0.5, 0) });
        await h.SettleDepthAsync(() => d.HasPending);
        Assert.True(d.HasPending);
    }

    [AvaloniaFact]
    public async Task Copy_and_paste_carry_the_depth_effect_as_a_group()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var dev = h.Develop();
        Assert.Equal(2.0, h.Session().FakeDepth.FNumber!.Value, 6);
        dev.CopyCommand.Execute(null);
        var dlg = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        var depthGroup = dlg.Groups.First(g => g.Group == EditGroups.DepthEffect);
        Assert.Equal("Depth effect", depthGroup.Name);
        Assert.True(depthGroup.IsAvailable);
        Assert.Equal("f/2.0 · focus 4.56 m", depthGroup.Detail);
        Assert.False(depthGroup.IsOn);                                   // not part of the default copy (Lumen's groups)
        depthGroup.IsOn = true;
        await dlg.OkCommand.ExecuteAsync(null);
        Assert.Null(h.Vm.Dialog);
        Assert.Contains("depth effect f/2.0", h.Develop().ClipboardText, StringComparison.OrdinalIgnoreCase);
        // another photo (no effect): paste brings f/2.0 and the focus, one step
        await dev.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        var s2 = h.Session();
        Assert.Null(s2.FakeDepth.FNumber);
        int steps = s2.History.Count;
        dev.PasteCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.Equal(2.0, s2.FakeDepth.FNumber!.Value, 6);
        Assert.Equal(4560, s2.FakeDepth.FocusMm, 0);
        Assert.Equal(steps + 1, s2.History.Count);
        Assert.True(dev.History[^1].IsDepth || s2.HistoryItems[^1].Kind == HistoryKind.Mixed);
        // a preset that keeps the depth effect
        dev.SavePresetCommand.Execute(null);
        var p = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        p.Name = "Portrait f/2";
        foreach (var g in p.Groups) g.IsOn = g.Group == EditGroups.DepthEffect;
        await p.OkCommand.ExecuteAsync(null);
        var preset = h.Services.Develop.Presets.First(x => x.Name == "Portrait f/2");
        Assert.Equal(EditGroups.DepthEffect, preset.Groups);
        Assert.Equal(2.0, preset.Set.DepthFNumber!.Value, 6);
    }

    // ================================================================================================================ not ready, slow, failing

    [AvaloniaFact]
    public async Task Depth_not_ready_tools_dimmed_explained_and_analyse_now()
    {
        await using var h = await Harness.CreateAsync();
        await h.SelectScopeAsync("all");
        var item = h.Find(i => i.DepthState == DepthState.NotAnalysed && i.ActiveJob == null)!;
        await h.OpenItemAsync(item);
        var d = h.DepthVm();
        Assert.False(d.IsReady);
        Assert.True(d.CanAnalyse);
        Assert.True(h.Develop().ShowDepthCard);
        Assert.All(h.Develop().Tools.Take(5), t => Assert.True(t.IsDimmed));
        Assert.False(h.Develop().Tools[5].IsDimmed);
        // a depth tool still opens, says why, and refuses strokes with the shake
        h.Key(Key.E);
        Assert.Equal(PageTool.RefineEdge, d.Tool);
        Assert.True(h.Develop().ShowDepthNotReady);
        Assert.Contains("isn't made yet", d.LineText);
        int shakes = h.Viewer().ShakeCount;
        h.Stroke(new[] { new NormPoint(0.4, 0.6), new NormPoint(0.5, 0.6) });
        Assert.Equal(shakes + 1, h.Viewer().ShakeCount);
        Assert.Contains("Analyse now", d.MessageText);
        // the effect controls are disabled; a typed value does nothing
        Assert.False(d.Aperture.IsEnabled);
        // Analyse now: the job runs; the depth becomes ready, the tools are no longer dimmed
        d.AnalyseNowCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.GetJobs().Any(j => j.Kind == JobKind.Analysis && j.CaptureId == item.Id), waitThumbnails: false);
        Assert.Contains(h.Lib.Jobs.GetJobs(), j => j.Kind == JobKind.Analysis && j.CaptureId == item.Id);
        for (int i = 0; i < 200 && !d.IsReady; i++) { h.Lib.FakeJobs.Tick(5); await h.SettleAsync(timeoutMs: 100, waitThumbnails: false); }
        Assert.True(d.IsReady);
        Assert.False(h.Develop().ShowDepthNotReady);
        Assert.All(h.Develop().Tools, t => Assert.False(t.IsDimmed));
        Assert.True(d.Aperture.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Slow_tool_shows_progress_can_be_cancelled_and_a_failure_is_explained()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        s.FakeDepth.SlowFactor = 2000;                                   // refine edge ≈ 3 min (stopped below; never ends first on a slow run)
        h.Key(Key.E);
        int steps = s.History.Count;
        var a = h.Element(FakeDepthMap.House, -1.75, -1.2);
        var b = h.Element(FakeDepthMap.House, -1.75, 1.6);
        h.Stroke(new[] { a, b });
        await h.SettleAsync(() => d.IsBusy && d.ActivityProgress > 0, timeoutMs: 4000, waitThumbnails: false);
        Assert.True(d.IsBusy);
        Assert.True(d.CanCancelActivity);
        Assert.StartsWith("Refining the edge · ", d.ActivityText);
        // the pointer stays free: another stroke can start while it runs (it waits its turn)
        Assert.True(h.Develop().Depth.Tool == PageTool.RefineEdge);
        d.CancelActivityCommand.Execute(null);
        await h.SettleDepthAsync(() => !d.IsBusy);
        Assert.False(d.IsBusy);
        Assert.Contains("Cancelled", d.MessageText);
        Assert.Equal(steps, s.History.Count);
        // a failure: the reason, the shake, no step
        s.FakeDepth.SlowFactor = 1;
        s.FakeDepth.FailNext = "The depth worker stopped: try again.";
        int shakes = h.Viewer().ShakeCount;
        h.Stroke(new[] { a, b });
        await h.SettleDepthAsync(() => d.HasMessage && d.MessageWarning);
        Assert.Equal("The depth worker stopped: try again.", d.MessageText);
        Assert.Equal(shakes + 1, h.Viewer().ShakeCount);
        Assert.Equal(steps, s.History.Count);
    }

    // ================================================================================================================ history, versions

    [AvaloniaFact]
    public async Task Combined_history_jump_rebuilding_and_unreliable_notice()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        var dev = h.Develop();
        // L16_00493 opens with its four saved depth steps after "Opened" (kept with the photo), being rebuilt
        s.HoldRebuild(1);
        await h.SettleDepthAsync();
        Assert.True(d.IsRebuilding);
        Assert.Equal("Rebuilding earlier steps · 1 of 4", d.RebuildText);
        Assert.Equal("Opened · with saved edits", dev.History[0].Label);   // the first row says what it is
        Assert.Contains("depth edits", dev.History[0].Tip);
        Assert.True(dev.History.Skip(1).Take(4).All(r => r.IsDepth));
        Assert.True(dev.History[1].IsReachable == false || dev.History[1].IsCurrent == false);
        Assert.False(dev.History[0].IsReachable);                        // going back needs the rebuild
        Assert.False(dev.CanUndo);
        int pos = s.HistoryPosition;
        dev.GoToHistoryCommand.Execute(dev.History[1]);
        Assert.Equal(pos, s.HistoryPosition);
        Assert.Contains("being rebuilt", h.Vm.Notice);
        s.HoldRebuild(null);
        await h.SettleDepthAsync(() => !d.IsRebuilding);
        Assert.True(dev.History.All(r => r.IsReachable));
        // tone, geometry and depth steps in one list; Ctrl+Z undoes the newest whatever its kind
        s.SetValue(AdjustmentKeys.Exposure, 0.4);
        s.FakeDepth.SetEffect(4.0, s.FakeDepth.FocusMm);
        s.RotateRight();
        dev.Refresh();
        Assert.Equal(new[] { "Exposure +0.40", "Depth effect f/4.0", "Rotate right" }, s.History.Skip(s.History.Count - 3));
        Assert.True(dev.History[^2].IsDepth);
        Assert.False(dev.History[^1].IsDepth);
        h.Key(Key.Z, RawInputModifiers.Control);
        h.Key(Key.Z, RawInputModifiers.Control);
        await h.SettleDepthAsync();
        Assert.Equal(2.0, s.FakeDepth.FNumber!.Value, 6);
        Assert.Equal(2, dev.History.Count(r => r.IsUndone));
        // jump back to "Opened": every depth edit undone, the depth map is the clean one
        dev.GoToHistoryCommand.Execute(dev.History[0]);
        await h.SettleDepthAsync();
        Assert.Equal(0, s.HistoryPosition);
        Assert.False(s.FakeDepth.HasDepthEdits);
        dev.GoToHistoryCommand.Execute(dev.History[5]);
        await h.SettleDepthAsync();
        Assert.True(s.FakeDepth.HasDepthEdits);
        // the unreliable state: a calm note with what still works, and the recovery
        s.MarkHistoryUnreliable();
        await h.SettleDepthAsync();
        Assert.True(d.IsUnreliable);
        Assert.Contains("kept exactly as saved", d.UnreliableText);
        Assert.False(dev.History[2].IsReachable);
        Assert.Contains("can't be undone one by one", dev.History[2].Tip);
        // new steps still undo
        s.SetValue(AdjustmentKeys.Contrast, 10);
        dev.Refresh();
        Assert.True(dev.CanUndo);
        await d.StartCleanVersionCommand.ExecuteAsync(null);
        await h.SettleDepthAsync();
        Assert.False(d.IsUnreliable);
        Assert.Equal("Lux edits 2", d.CurrentVersionName);
    }

    [AvaloniaFact]
    public async Task Versions_switch_start_clean_reset_and_import_lumen_for_one_photo()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        var s = h.Session();
        Assert.Equal(new[] { "Lux edits", "Clean" }, d.Versions.Select(v => v.Name));
        Assert.True(d.Versions[0].IsCurrent);
        Assert.Equal("4", d.Versions[0].CountText);
        long view0 = s.FakeDepth.DepthView.Version(0);
        // the clean version: the picture and the depth view change at once, the edits stay with "Lux edits"
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions[1]);
        await h.SettleDepthAsync();
        Assert.Equal("Clean", d.CurrentVersionName);
        Assert.False(s.FakeDepth.HasDepthEdits);
        Assert.True(s.FakeDepth.DepthView.Version(0) > view0);
        Assert.Equal(new[] { "Opened" }, s.History);                   // the history restarts with that version's steps
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Lux edits"));
        await h.SettleDepthAsync();
        Assert.Equal(4, s.FakeDepth.Versions.First(v => v.IsCurrent).Steps);
        // a tool on the clean version starts a Lux version of its own (the clean one stays clean)
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Clean"));
        h.Key(Key.B);
        h.Stroke(new[] { h.Element(FakeDepthMap.Windows, -1.4, 0), h.Element(FakeDepthMap.Windows, 1.4, 0) });
        await h.SettleDepthAsync(() => d.CurrentVersionName == "Lux edits 2");
        Assert.Equal(new[] { "Lux edits", "Clean", "Lux edits 2" }, d.Versions.Select(v => v.Name));
        Assert.Equal(0, s.FakeDepth.Versions.First(v => v.Name == "Clean").Steps);
        // start a clean version
        await d.StartCleanVersionCommand.ExecuteAsync(null);
        await h.SettleDepthAsync();
        Assert.Equal("Lux edits 3", d.CurrentVersionName);
        // reset depth edits asks first (it can't be undone) and offers the clean version instead
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Lux edits"));
        await h.SettleDepthAsync();
        h.Key(Key.R, RawInputModifiers.Control);
        var confirm = Assert.IsType<ConfirmViewModel>(h.Vm.Dialog);
        Assert.Contains("can't be undone", confirm.Text);
        confirm.OkCommand.Execute(null);
        await h.SettleDepthAsync(() => !s.FakeDepth.HasDepthEdits);
        Assert.Equal("Reset depth edits", s.History[^1]);
        Assert.False(h.Develop().CanUndo);
        // import this photo's Lumen edits: a capture with a session and no import yet
        h.Key(Key.Escape);
        h.Key(Key.Escape);
        await h.SelectScopeAsync("all");
        var withLumen = h.Find(i => i.HasLumenSession && i.Edits.Source == EditSource.None && i.DepthState == DepthState.Analysed)!;
        await h.OpenItemAsync(withLumen);
        Assert.True(h.DepthVm().CanImportLumen);
        h.Develop().ImportLumenEditsCommand.Execute(null);
        var dialog = Assert.IsType<LumenImportViewModel>(h.Vm.Dialog);
        await h.SettleAsync(() => dialog.IsReady, waitThumbnails: false);
        Assert.True(dialog.SingleCapture);
        Assert.Equal(1, dialog.Total);
        dialog.NewerToneStyle = true;
        dialog.ImportCommand.Execute(null);
        await h.SettleDepthAsync(() => h.DepthVm().CurrentVersionName == "From Lumen");
        Assert.Null(h.Vm.Dialog);
        Assert.Equal("From Lumen", h.DepthVm().CurrentVersionName);
        Assert.False(h.DepthVm().CanImportLumen);
        Assert.Contains(h.DepthVm().Versions, v => v.Name == "Clean");
        Assert.Equal("Imported from Lumen", h.Session().History[^1]);
    }

    // ================================================================================================================ keyboard map

    [AvaloniaFact]
    public async Task Depth_keyboard_map()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm();
        foreach (var (key, tool) in new[] { (Key.F, PageTool.Focus), (Key.E, PageTool.RefineEdge), (Key.B, PageTool.BlendArea), (Key.M, PageTool.MatchDepth) })
        {
            h.Key(key);
            Assert.Equal(tool, d.Tool);
        }
        h.Key(Key.Q);
        Assert.True(d.IsQuickSelect);
        h.Key(Key.M);
        Assert.True(d.IsMatchBrush);                                     // M from quick select: match depth with the brush
        h.Key(Key.M);
        Assert.Equal(PageTool.None, d.Tool);                             // the active tool's key leaves it
        // C (crop) leaves the depth tool; a depth tool applies an open crop first (Lumen)
        h.Key(Key.E);
        h.Key(Key.C);
        Assert.True(h.Develop().IsCropping);
        Assert.Equal(PageTool.None, d.Tool);
        h.Key(Key.E);
        Assert.False(h.Develop().IsCropping);
        Assert.Equal(PageTool.RefineEdge, d.Tool);
        // Esc: the tool, then the library
        h.Key(Key.Escape);
        Assert.Equal(PageTool.None, d.Tool);
        Assert.True(h.Vm.IsDevelop);
        // H, D
        bool shade = d.ShadeOutsideFocus;
        h.Key(Key.H);
        Assert.NotEqual(shade, d.ShadeOutsideFocus);
        h.Key(Key.D);
        Assert.True(h.Develop().IsPhotoDepth);
        h.Key(Key.D);
        Assert.False(h.Develop().IsPhotoDepth);
        h.Key(Key.Escape);
        Assert.True(h.Vm.IsLibrary);
    }
}
