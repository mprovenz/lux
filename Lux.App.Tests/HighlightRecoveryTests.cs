using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Develop;
using Lux.App.ViewModels;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Isp.Stages;
using Lux.Engine.Session;
using Lux.Library;
using Lux.Store;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>Highlight recovery, gamut compression and the DNG highlight option: the engine's curve and pixel operations, the saved
/// formats, and the two controls of the Develop page (on the fake editor).</summary>
public sealed class HighlightRecoveryTests(ITestOutputHelper output)
{
    static readonly float[][] Tables = { TmoAcrLuts.Acr, TmoAcrLuts.LightV1, TmoAcrLuts.LightV1Lowlight, TmoAcrLuts.LightV2 };

    // ================================================================================================================ the curve

    [Fact]
    public void The_curve_is_Lumens_table_up_to_the_knee_and_reaches_white_later()
    {
        foreach (var lut in Tables)
        {
            int k = HighlightRecovery.KneeIndex(lut);
            Assert.InRange(lut[k], HighlightRecovery.KneeOutput, HighlightRecovery.KneeOutput + 0.002f);
            float top = lut[^1];
            foreach (float amount in new[] { 1f, 25f, 50f, 75f, 100f })
            {
                var c = HighlightRecovery.Curve(lut, amount);
                Assert.Equal(2050, c.Length);
                Assert.Same(c, HighlightRecovery.Curve(lut, amount));                       // kept
                for (int i = 0; i <= k; i++) Assert.Equal(BitConverter.SingleToInt32Bits(lut[i]), BitConverter.SingleToInt32Bits(c[i]));
                for (int i = 1; i < c.Length; i++) Assert.True(c[i] >= c[i - 1], $"the curve decreases at {i} (amount {amount})");
                // white at 2^(amount / 100) times Lumen's white input, not before
                float white = HighlightRecovery.WhiteInput(amount);
                Assert.Equal(Math.Pow(2, amount / 100.0), white, 5);
                Assert.InRange(HighlightRecovery.Evaluate(c, white * 1.002f), top - 0.002f, top + 0.002f);
                Assert.True(HighlightRecovery.Evaluate(c, white * 0.9f) < top - 0.0003f, $"white before the white input (amount {amount})");
                // a lookup below the knee gives the bits of Lumen's table
                for (int i = 0; i < 200; i++)
                {
                    float t = (k - 0.5f) / 1024f * i / 200f;
                    Assert.Equal(BitConverter.SingleToInt32Bits(HighlightRecovery.Evaluate(HighlightRecovery.Curve(lut, 0f), t)),
                                 BitConverter.SingleToInt32Bits(HighlightRecovery.Evaluate(c, t)));
                }
            }
            // amount 100: no value above white, flat at the end
            var full = HighlightRecovery.Curve(lut, 100f);
            Assert.Equal(top, full[^1]);
            Assert.Equal(top, HighlightRecovery.Evaluate(full, 3.5f));
            // a small amount is close to Lumen's curve: the family is continuous
            var small = HighlightRecovery.Curve(lut, 1f); var zero = HighlightRecovery.Curve(lut, 0f);
            for (int i = 0; i < small.Length; i++) Assert.InRange(small[i] - zero[i], -0.01f, 0.0005f);
        }
    }

    [Fact]
    public void The_tone_pixel_does_not_change_below_the_knee_and_keeps_tones_above_Lumens_white()
    {
        var lut = TmoAcrLuts.LightV1; var curve = HighlightRecovery.Curve(lut, 100f);
        // midtones and shadows: the same bits
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var s = new Vec4F((float)rnd.NextDouble() * 0.6f, (float)rnd.NextDouble() * 0.6f, (float)rnd.NextDouble() * 0.6f, 1f);
            var a = ToneMappingAcrStage.Pixel(s, 1f, lut); var b = ToneMappingAcrStage.Pixel(s, 1f, curve);
            Assert.Equal((a.R, a.G, a.B), (b.R, b.G, b.B));
        }
        // a tone half a stop above Lumen's white: Lumen is at or above white, the shoulder is below it
        var over = new Vec4F(1.4f, 1.4f, 1.4f, 1f);
        Assert.True(ToneMappingAcrStage.Pixel(over, 1f, lut).G >= 1f);
        Assert.InRange(ToneMappingAcrStage.Pixel(over, 1f, curve).G, 0.9f, 0.999f);
        // two stops above: white, never more
        Assert.Equal(1f, ToneMappingAcrStage.Pixel(new Vec4F(4f, 4f, 4f, 1f), 1f, curve).G);
    }

    [Fact]
    public void Desaturation_starts_at_its_threshold_and_ends_in_neutral()
    {
        float r = 0.84f, g = 0.5f, b = 0.2f;
        HighlightRecovery.Desaturate(ref r, ref g, ref b, HighlightRecovery.ProPhotoLuminance(r, g, b));
        Assert.Equal((0.84f, 0.5f, 0.2f), (r, g, b));                                       // below the start: unchanged
        r = 1f; g = 0.7f; b = 0.4f;
        float y = HighlightRecovery.ProPhotoLuminance(r, g, b);
        HighlightRecovery.Desaturate(ref r, ref g, ref b, y);
        Assert.Equal((y, y, y), (r, g, b));                                                 // at the end: neutral, the same luminance
        r = 0.925f; g = 0.7f; b = 0.4f;
        HighlightRecovery.Desaturate(ref r, ref g, ref b, 0.7f, 1f);
        Assert.Equal(0.925f + 0.25f * (0.7f - 0.925f), r, 5);                               // halfway: weight 0.5²
        r = 1f; g = 0.7f; b = 0.4f;
        HighlightRecovery.Desaturate(ref r, ref g, ref b, 0.7f, 0f);
        Assert.Equal((1f, 0.7f, 0.4f), (r, g, b));                                          // strength 0: unchanged
        var row = new[] { new Vec4F(0.5f, 0.4f, 0.3f, 0.25f), new Vec4F(1f, 0.2f, 0.2f, 0.25f) };
        HighlightRecovery.DesaturateRow(row);
        Assert.Equal((0.5f, 0.4f, 0.3f, 0.25f), (row[0].R, row[0].G, row[0].B, row[0].A));
        Assert.True(row[1].R == row[1].G && row[1].G == row[1].B && row[1].A == 0.25f);
    }

    // ================================================================================================================ gamut compression

    [Fact]
    public void Gamut_compression_moves_colors_outside_the_gamut_inside_and_leaves_the_others()
    {
        // all distances below the threshold: the same bits
        float r = 0.6f, g = 0.3f, b = 0.2f;
        GamutCompression.Compress(ref r, ref g, ref b);
        Assert.Equal((0.6f, 0.3f, 0.2f), (r, g, b));
        // a channel below 0 (outside the gamut): inside afterwards, the largest channel and the order kept
        r = -0.1f; g = 0.2f; b = 0.9f;
        GamutCompression.Compress(ref r, ref g, ref b);
        Assert.Equal(0.9f, b);
        Assert.True(r > 0f && r < g && g < b, $"({r}, {g}, {b})");
        // the compression is continuous at the threshold, reaches the boundary at the limit, and keeps the order of the distances
        Assert.Equal(0.5f, GamutCompression.CompressDistance(0.5f));
        Assert.Equal(GamutCompression.Threshold, GamutCompression.CompressDistance(GamutCompression.Threshold), 6);
        Assert.Equal(1f, GamutCompression.CompressDistance(GamutCompression.Limit), 5);
        float last = 0f;
        for (float d = 0f; d < 1.6f; d += 0.01f) { float c = GamutCompression.CompressDistance(d); Assert.True(c >= last); last = c; }
        // black and a negative pixel: unchanged
        r = g = b = 0f; GamutCompression.Compress(ref r, ref g, ref b); Assert.Equal((0f, 0f, 0f), (r, g, b));
        // the conversion with compression gives the standard conversion's bits for a pixel that needs none
        var srgb = ColorSpace.Standard(2, WhitePoint.Of(7), 1);
        var a = new Image<Vec4F>(9, 1); var c2 = new Image<Vec4F>(9, 1);
        for (int x = 0; x < 9; x++) a.Row(0)[x] = c2.Row(0)[x] = new Vec4F(0.2f + 0.05f * x, 0.3f, 0.25f, 1f);
        ColorSpaceConvert.Convert(a, a, ColorSpace.ProPhotoD50, srgb, 1);
        GamutCompression.ConvertCompressed(c2, ColorSpace.ProPhotoD50, srgb, 1);
        for (int x = 0; x < 9; x++) Assert.Equal((a.Row(0)[x].R, a.Row(0)[x].G, a.Row(0)[x].B, a.Row(0)[x].A), (c2.Row(0)[x].R, c2.Row(0)[x].G, c2.Row(0)[x].B, c2.Row(0)[x].A));
        // a saturated ProPhoto blue is outside sRGB: the standard conversion clips red to 0, the compression keeps it above 0
        var p = new Image<Vec4F>(40, 1); var q = new Image<Vec4F>(40, 1);
        for (int x = 0; x < 40; x++) p.Row(0)[x] = q.Row(0)[x] = new Vec4F(0.005f * x, 0.005f * x, 0.6f, 1f);
        ColorSpaceConvert.Convert(p, p, ColorSpace.ProPhotoD50, srgb, 1);
        GamutCompression.ConvertCompressed(q, ColorSpace.ProPhotoD50, srgb, 1);
        int kept = 0;
        for (int x = 0; x < 40; x++)
        {
            var u = p.Row(0)[x]; var v = q.Row(0)[x];
            Assert.Equal(u.B, v.B);                                                         // the largest channel does not change
            if ((u.R == 0f && v.R > 0f) || (u.G == 0f && v.G > 0f)) kept++;
        }
        Assert.True(kept > 5, $"{kept} of 40 saturated blues keep a channel that the standard conversion clips");
    }

    // ================================================================================================================ settings

    [Fact]
    public void The_settings_reach_the_tone_stage_only_when_set_and_restart_the_render_there()
    {
        var loaded = new Tuning();
        var t0 = Adjustments.Default.ApplyTo(loaded, 1f);
        Assert.False(t0.Has(ToneMappingAcrStage.HighlightRecoveryKey));
        Assert.False(t0.Has(ToneMappingAcrStage.GamutCompressKey));
        var a = Adjustments.Default with { HighlightRecovery = 60f, GamutCompress = true };
        var t1 = a.ApplyTo(loaded, 1f);
        Assert.Equal(60.0, t1.Num(ToneMappingAcrStage.HighlightRecoveryKey));
        Assert.Equal(1.0, t1.Num(ToneMappingAcrStage.GamutCompressKey));
        Assert.Equal(60f, a.Validate().HighlightRecovery);
        Assert.Throws<ArgumentOutOfRangeException>(() => (Adjustments.Default with { HighlightRecovery = 101f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Adjustments.Default with { HighlightRecovery = -1f }).Validate());
        var wb = new WhiteBalance.CaptureWb(new[] { 1f, 1f, 1f }, 0.33f, 0.33f, 5000f, 0f, new[] { 1f, 1f, 1f });
        Assert.Equal(6, Adjustments.FirstChangedStage(Adjustments.Default, Adjustments.Default with { HighlightRecovery = 1f }, wb));
        Assert.Equal(6, Adjustments.FirstChangedStage(Adjustments.Default, Adjustments.Default with { GamutCompress = true }, wb));
        Assert.Equal(int.MaxValue, Adjustments.FirstChangedStage(a, a with { }, wb));
        var parsed = Adjustments.Parse("highlight-recovery=40,gamut-compress=1");
        Assert.Equal(40f, parsed.HighlightRecovery);
        Assert.True(parsed.GamutCompress);
        Assert.DoesNotContain("recovery", Adjustments.Default.ToString());
        Assert.Contains("highlight recovery 60", a.ToString());
    }

    [Fact]
    public void Saved_edits_and_presets_keep_the_settings_and_older_files_load_without_them()
    {
        // a recipe without the settings has the text of an earlier version, and that text loads with both off
        var plain = new CaptureRecipe { Adjustments = Adjustments.Default with { Exposure = 0.5f, Clarity = 20f } };
        string old = plain.ToJson();
        Assert.DoesNotContain("highlight_recovery", old);
        Assert.DoesNotContain("gamut_compress", old);
        var back = CaptureRecipe.FromJson(old);
        Assert.Equal(0f, back.Adjustments!.HighlightRecovery);
        Assert.False(back.Adjustments.GamutCompress);
        Assert.Equal(plain.Adjustments, back.Adjustments);
        // with the settings: a round trip keeps them
        var set = new CaptureRecipe { Adjustments = plain.Adjustments with { HighlightRecovery = 62.5f, GamutCompress = true } };
        var set2 = CaptureRecipe.FromJson(set.ToJson());
        Assert.Equal(set.Adjustments, set2.Adjustments);
        Assert.Equal(CaptureRecipe.Version, System.Text.Json.Nodes.JsonNode.Parse(set.ToJson())!["version"]!.GetValue<int>());
        // presets and the clipboard (the library's edit values)
        var values = new EditValues { Groups = EditGroups.ToneAndColour, Adjustments = set.Adjustments, Transform = new PhotoTransform() };
        string json = Lux.Library.Edit.EditValuesCodec.ToJson(values);
        Assert.Equal(set.Adjustments, Lux.Library.Edit.EditValuesCodec.FromJson(json).Adjustments);
        string oldPreset = Lux.Library.Edit.EditValuesCodec.ToJson(values with { Adjustments = plain.Adjustments });
        Assert.DoesNotContain("highlight_recovery", oldPreset);
        Assert.Equal(plain.Adjustments, Lux.Library.Edit.EditValuesCodec.FromJson(oldPreset).Adjustments);
        // the library's slider access: both keys read and write the engine's settings
        var adj = Lux.Library.Edit.PhotoEditImpl.With(Adjustments.Default, AdjustmentKey.HighlightRecovery, 35f);
        adj = Lux.Library.Edit.PhotoEditImpl.With(adj, AdjustmentKey.GamutCompress, 1f);
        Assert.Equal(35f, adj.HighlightRecovery);
        Assert.True(adj.GamutCompress);
        Assert.False(Lux.Library.Edit.PhotoEditImpl.With(adj, AdjustmentKey.GamutCompress, 0f).GamutCompress);
        Assert.Equal(Adjustments.Default, Lux.Library.Edit.PhotoEditImpl.With(Lux.Library.Edit.PhotoEditImpl.With(adj, AdjustmentKey.GamutCompress, null), AdjustmentKey.HighlightRecovery, null));
    }

    // ================================================================================================================ DNG

    static DngExportTags Tags(bool keep) => new()
    {
        ToneMappingType = "light_v1", BaselineExposure = 0.75f, KeepHighlights = keep, Compression = 0,
        ModifyTime = new DateTime(2020, 1, 2, 3, 4, 5), UniqueId = new byte[16],
    };

    static (double BaselineExposure, int[] White, ushort[] Samples) WriteDng(bool keep, string path)
    {
        const int w = 8, h = 4;
        var px = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = 100f + i * 1000f; px[i * 4 + 1] = 16384f; px[i * 4 + 2] = 30001f; px[i * 4 + 3] = 1f; }
        using (var fs = File.Create(path))
            DngWriter.Write(fs, w, h, Tags(keep), block => (new RectI(0, 0, w, h), px));
        var f = new DngFile(path);
        var be = f.Get(f.Ifd0, 0xc62a)!;
        double exposure = (double)BinaryPrimitives.ReadInt32LittleEndian(be.Data) / BinaryPrimitives.ReadInt32LittleEndian(be.Data.AsSpan(4));
        var white = Enumerable.Range(0, 3).Select(i => f.Int(f.Ifd0, 0xc61d, i)).ToArray();
        return (exposure, white, f.DecodeImage(out _, out _));
    }

    [Fact]
    public void The_DNG_highlight_option_halves_the_samples_and_adds_one_stop_of_baseline_exposure()
    {
        string dir = Path.Combine(Paths.UiScratch, "hr-dng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clip = WriteDng(false, Path.Combine(dir, "clip.dng"));
            var keep = WriteDng(true, Path.Combine(dir, "keep.dng"));
            Assert.Equal(0.75, clip.BaselineExposure, 6);
            Assert.Equal(1.75, keep.BaselineExposure, 6);
            Assert.Equal(new[] { 16384, 16384, 16384 }, clip.White);
            Assert.Equal(clip.White, keep.White);
            Assert.Equal(clip.Samples.Length, keep.Samples.Length);
            Assert.Equal(16384, clip.Samples[1]);
            Assert.Equal(8192, keep.Samples[1]);
            Assert.Equal(30001, clip.Samples[2]);
            Assert.Equal(15001, keep.Samples[2]);                                           // 15000.5 rounds up
            for (int i = 0; i < clip.Samples.Length; i++) Assert.InRange(keep.Samples[i] * 2 - clip.Samples[i], -1, 1);
            // the gain of 1 changes no sample
            var px = new float[] { 0.4f, 0.5f, 65535.4f, 70000f, -3f, float.NaN, 12345.5f, 1f };
            Assert.Equal(DngWriter.ToU16(px, 2, 1), DngWriter.ToU16(px, 2, 1, 1, 1f));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================================================================ a real capture

    static byte[] Render(ExportState st, int level, Adjustments? adj)
    {
        byte[]? px = null;
        var req = new ExportRequest
        {
            Formats = Array.Empty<ExportImageFormat>(), Level = level, OutDirectory = Paths.UiScratch, Stem = "x", RenderThreads = Environment.ProcessorCount,
            UseSavedEdits = false, CacheUse = CacheUse.None, LensFrames = new LensRequest { Modules = Array.Empty<string>() }, Adjustments = adj,
            LuxFormats = (l, r, s, od, sm, outputs, refusals, log) => px = (byte[])s!.Rgba8().Rgba.Clone(),
        };
        Exporter.Run(st, req, null);
        return px!;
    }

    /// <summary>A bright sky capture at a small size: the default rendering does not change, highlight recovery brings most of the white
    /// area back and leaves the dark half of the picture alone, and gamut compression changes few pixels.</summary>
    [Fact]
    public void A_real_capture_keeps_its_default_rendering_and_gets_its_highlights_back()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00459.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { output.WriteLine("LUX_TEST_CAPTURES has no L16_00459.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        var st = ExportBuild.Build(lri, 0, null, Environment.ProcessorCount, null, new BuildOptions());
        const int level = 3;
        var none = Render(st, level, null);
        Assert.Equal(none, Render(st, level, Adjustments.Default));                           // settings at their defaults: the same bytes
        var rec = Render(st, level, Adjustments.Default with { HighlightRecovery = 100f });
        var gamut = Render(st, level, Adjustments.Default with { GamutCompress = true });
        int n = none.Length / 4, white0 = 0, white1 = 0, darkChanged = 0, changed = 0, gamutChanged = 0;
        for (int i = 0; i < n; i++)
        {
            int m0 = Math.Max(none[i * 4], Math.Max(none[i * 4 + 1], none[i * 4 + 2])), m1 = Math.Max(rec[i * 4], Math.Max(rec[i * 4 + 1], rec[i * 4 + 2]));
            if (m0 >= 254) white0++;
            if (m1 >= 254) white1++;
            bool diff = none[i * 4] != rec[i * 4] || none[i * 4 + 1] != rec[i * 4 + 1] || none[i * 4 + 2] != rec[i * 4 + 2];
            if (diff) changed++;
            if (diff && m0 < 200) darkChanged++;                                              // well below the knee (about 232 after encoding)
            if (none[i * 4] != gamut[i * 4] || none[i * 4 + 1] != gamut[i * 4 + 1] || none[i * 4 + 2] != gamut[i * 4 + 2]) gamutChanged++;
        }
        output.WriteLine($"white {100.0 * white0 / n:F2} % -> {100.0 * white1 / n:F2} %, changed {100.0 * changed / n:F2} %, gamut compression changed {100.0 * gamutChanged / n:F3} %");
        Assert.True(white0 > 0.10 * n, "the capture has a large white area by default");
        Assert.True(white1 < 0.01 * n, "highlight recovery brings the white area back");
        Assert.True(changed > 0.10 * n);
        Assert.Equal(0, darkChanged);
        Assert.True(gamutChanged < 0.01 * n);
    }

    // ================================================================================================================ the Develop page

    static AdjustmentRowViewModel Row(Harness h, string key) => h.Develop().Adjustments.First(r => r.Key == key);

    static void Key(Harness h, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        h.Window.KeyPress(key, mods, PhysicalKey.None, null);
        h.Window.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    static void Type(Harness h, string key, string text)
    {
        var box = h.DevelopView().GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("valuefield") && (t.DataContext as AdjustmentRowViewModel)?.Key == key);
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.Focus(), $"the {key} field took no focus");
        box.Text = text;
        Dispatcher.UIThread.RunJobs();
        Key(h, Avalonia.Input.Key.Enter);
    }

    static CheckBox GamutBox(Harness h) =>
        h.DevelopView().GetVisualDescendants().OfType<CheckBox>().First(c => (c.DataContext as AdjustmentRowViewModel)?.Key == AdjustmentKeys.GamutCompress);

    [AvaloniaFact]
    public async Task The_panel_has_the_slider_under_Highlights_and_the_check_box_and_both_undo()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        var s = h.Session();
        // the slider: under Highlights, 0 to 100, default 0, no sign
        var keys = d.Adjustments.Select(r => r.Key).ToList();
        Assert.Equal(keys.IndexOf(AdjustmentKeys.Highlights) + 1, keys.IndexOf(AdjustmentKeys.HighlightRecovery));
        var row = Row(h, AdjustmentKeys.HighlightRecovery);
        Assert.Equal("Highlight recovery", row.Name);
        Assert.Equal((0d, 100d, 0d), (row.Spec.Min, row.Spec.Max, row.Spec.Default));
        Assert.True(row.IsSlider && row.IsDefault);
        Type(h, AdjustmentKeys.HighlightRecovery, "60");
        Assert.Equal(60, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.Equal("60", row.ValueText);
        Type(h, AdjustmentKeys.HighlightRecovery, "250");                                     // clamped
        Assert.Equal(100, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Type(h, AdjustmentKeys.HighlightRecovery, "-5");
        Assert.Equal(0, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Type(h, AdjustmentKeys.HighlightRecovery, "40");
        // the check box: one history step per change
        var gamut = Row(h, AdjustmentKeys.GamutCompress);
        Assert.True(gamut.IsToggle && !gamut.IsOn);
        var box = GamutBox(h);
        Assert.Equal("Keep saturated colors in gamut", box.Content);
        Assert.True(box.IsVisible);
        int steps = s.History.Count;
        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, s.Get(AdjustmentKeys.GamutCompress), 9);
        Assert.Equal(steps + 1, s.History.Count);
        await h.SettlePictureAsync();
        // undo twice: the check box off, then the slider back; redo brings both again
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        await h.SettlePictureAsync();
        Assert.Equal(0, s.Get(AdjustmentKeys.GamutCompress), 9);
        Assert.False(gamut.IsOn);
        Assert.False(box.IsChecked);
        Assert.Equal(40, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        await h.SettlePictureAsync();
        Assert.Equal(0, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.Equal(0, row.Value, 9);
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Key(h, Avalonia.Input.Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        await h.SettlePictureAsync();
        Assert.Equal(40, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.True(gamut.IsOn);
        Assert.True(box.IsChecked);
        // the section reset puts both back
        d.ResetToneCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal(0, s.Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.Equal(0, s.Get(AdjustmentKeys.GamutCompress), 9);
        Assert.False(box.IsChecked);
    }

    [AvaloniaFact]
    public async Task Copy_paste_presets_before_after_and_export_carry_both_settings()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.Develop();
        Type(h, AdjustmentKeys.HighlightRecovery, "70");
        Row(h, AdjustmentKeys.GamutCompress).IsOn = true;
        Dispatcher.UIThread.RunJobs();
        await h.SettlePictureAsync();
        // the engine settings of what is on screen, and the export
        var eng = h.Session().ExportSettings(new ExportOptions()).Adjustments!;
        Assert.Equal(70f, eng.HighlightRecovery);
        Assert.True(eng.GamutCompress);
        d.OpenExportCommand.Execute(null);
        var panel = d.ExportPanel!;
        int jobs = h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Export);
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null && h.Lib.Jobs.GetJobs().Count(j => j.Kind == JobKind.Export) > jobs, waitThumbnails: false);
        Assert.Equal(70f, panel.LastOptions!.Adjustments!.HighlightRecovery);
        Assert.True(panel.LastOptions.Adjustments.GamutCompress);
        // before / after: the edited picture is requested with the settings, the before picture is there too
        d.SetModeCommand.Execute(DevelopMode.BeforeAfter);
        await h.SettlePictureAsync();
        Assert.Contains(h.Session().FakeTiles.Requests, r => r.Channel == Lux.App.Viewer.Channels.Before);
        d.SetModeCommand.Execute(DevelopMode.Single);
        // copy: the dialog lists the slider under Tone and the check box under Color
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.C, RawInputModifiers.Control);
        var dlg = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        Assert.Equal("70", dlg.Groups.First(g => g.Group == EditGroups.Tone).Sliders.First(x => x.Key == AdjustmentKeys.HighlightRecovery).Detail);
        Assert.Equal("on", dlg.Groups.First(g => g.Group == EditGroups.Colour).Sliders.First(x => x.Key == AdjustmentKeys.GamutCompress).Detail);
        await dlg.OkCommand.ExecuteAsync(null);
        // a preset of the same settings
        d.SavePresetCommand.Execute(null);
        var pre = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        pre.Name = "Recovered sky";
        await pre.OkCommand.ExecuteAsync(null);
        await h.SettleAsync(() => d.Presets.Any(p => p.Name == "Recovered sky"), waitThumbnails: false);
        // paste on the next photo
        await d.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        var s2 = h.Session();
        Assert.Equal(0, s2.Get(AdjustmentKeys.HighlightRecovery), 9);
        h.Viewer().Focus();
        Key(h, Avalonia.Input.Key.V, RawInputModifiers.Control);
        Assert.Equal(70, s2.Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.Equal(1, s2.Get(AdjustmentKeys.GamutCompress), 9);
        Assert.True(Row(h, AdjustmentKeys.GamutCompress).IsOn);
        // the preset on a third photo
        await d.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        d.ApplyPresetCommand.Execute(d.Presets.First(p => p.Name == "Recovered sky"));
        Assert.Equal(70, h.Session().Get(AdjustmentKeys.HighlightRecovery), 9);
        Assert.Equal(1, h.Session().Get(AdjustmentKeys.GamutCompress), 9);
        d.DeletePresetCommand.Execute(d.Presets.First(p => p.Name == "Recovered sky"));
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => d.Presets.All(p => p.Name != "Recovered sky"), waitThumbnails: false);
    }

    [AvaloniaFact]
    public async Task Screenshot_of_the_panel_with_both_controls()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Session().SetValue(AdjustmentKeys.HighlightRecovery, 60);
        h.Session().SetValue(AdjustmentKeys.GamutCompress, 1);
        h.Develop().Refresh();
        await h.SettlePictureAsync();
        h.ShotM2("highlight-recovery-panel");
    }
}
