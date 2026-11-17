using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Tests of the stereo formats. The matcher, the convergence rule, the color fit and the crop run on synthetic pairs with known
/// offsets. The MPO file is read back with a reader that is written here from the format specification. The export panel offers the
/// four formats. A real capture is exported through <c>lux-light</c> when <c>LUX_TEST_CAPTURES</c> names the capture folder.
/// </summary>
public sealed class StereoTests
{
    readonly ITestOutputHelper _out;
    public StereoTests(ITestOutputHelper output) { _out = output; }

    // ================================================================================================================ synthetic pairs

    /// <summary>A texture with detail at several scales, from a fixed generator.</summary>
    static double Texture(double x, double y, int channel)
    {
        double s = 0; uint seed = 777u + (uint)channel * 31u;
        for (int k = 0; k < 20; k++)
        {
            seed = seed * 1664525u + 1013904223u; double fx = ((seed >> 8) % 1000) / 1000.0 * 0.4 - 0.2;
            seed = seed * 1664525u + 1013904223u; double fy = ((seed >> 8) % 1000) / 1000.0 * 0.4 - 0.2;
            seed = seed * 1664525u + 1013904223u; double ph = ((seed >> 8) % 1000) / 1000.0 * 6.283;
            s += Math.Sin(fx * x + fy * y + ph);
        }
        return Math.Clamp(125 + s * 20, 20, 235);
    }

    const int W = 960, H = 720;

    /// <summary>A rectified pair of a ground plane: the offset (left minus right, in pixels) grows evenly from <paramref name="far"/> in
    /// the top row to <paramref name="near"/> in the bottom row. The right picture gets <paramref name="gain"/> on its colors.</summary>
    static (Rgba L, byte[] Lm, Rgba R, byte[] Rm) Pair(int far, int near, double gain = 1.0)
    {
        var l = new Rgba(W, H); var r = new Rgba(W, H); var lm = new byte[W * H]; var rm = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double d = far + (near - far) * (double)y / (H - 1); int p = (y * W + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    l.P[p + c] = (byte)Math.Round(Texture(x, y, c));
                    r.P[p + c] = (byte)Math.Clamp(Math.Round(Texture(x + d, y, c) * gain), 0, 255);
                }
                l.P[p + 3] = r.P[p + 3] = 255; lm[y * W + x] = rm[y * W + x] = 1;
            }
        return (l, lm, r, rm);
    }

    [Fact]
    public void The_matcher_measures_the_offsets_and_puts_the_far_end_behind_the_screen()
    {
        // offsets 4 px (far) and 28 px (near): a range of 2.5 % of 960 px
        var (l, lm, r, rm) = Pair(4, 28);
        var pair = RealStereo.Finish(l, lm, r, rm, focalPx: 800, baselineMm: 43.6, _out.WriteLine)!;
        var m = pair.Measured;
        _out.WriteLine($"{m.Matches} matches, far {m.FarPx:F2} px, near {m.NearPx:F2} px, range {m.RangePercent:F2} %, shift {m.ShiftPx:F2} px: {m.Rule}");
        Assert.True(m.Matches > 800);
        // the 1st and the 99th percentile sit just inside the two ends
        Assert.InRange(m.FarPx, 4.0, 6.0);
        Assert.InRange(m.NearPx, 26.0, 28.0);
        Assert.InRange(m.RangePercent, 2.1, 2.5);
        Assert.True(m.Comfortable);
        // the far end goes 1.5 % of the width behind the screen
        Assert.Equal(m.FarPx + 0.015 * W, m.ShiftPx, 6);
        Assert.Equal(1.5, m.UncrossedPercent, 6);
        Assert.InRange(m.CrossedPercent, -1.0, -0.6);
        // both eyes have the same size and the shape of the frame, and the crop removed the part that only one eye sees
        Assert.Equal((pair.Left.W, pair.Left.H), (pair.Right.W, pair.Right.H));
        Assert.Equal((double)W / H, (double)pair.Left.W / pair.Left.H, 2);
        Assert.InRange(pair.LostPercent, 1, 12);
    }

    [Fact]
    public void A_shallow_scene_puts_its_nearest_object_at_the_screen_and_a_deep_scene_is_not_comfortable()
    {
        var (l, lm, r, rm) = Pair(4, 12);                      // 0.83 % of the width
        var m = RealStereo.Finish(l, lm, r, rm, 800, 43.6)!.Measured;
        Assert.Equal("nearest object at the screen", m.Rule);
        Assert.Equal(m.NearPx, m.ShiftPx, 6);
        Assert.Equal(0, m.CrossedPercent, 6);
        Assert.True(m.Comfortable);
        (l, lm, r, rm) = Pair(4, 44);                          // 4.2 % of the width
        m = RealStereo.Finish(l, lm, r, rm, 800, 43.6)!.Measured;
        Assert.InRange(m.RangePercent, 3.7, 4.2);
        Assert.False(m.Comfortable);
        // the rule itself
        var c = RealStereo.Converge(farPx: 10, nearPx: 110, width: 4000, matches: 500);
        Assert.Equal(70, c.ShiftPx, 9);
        Assert.Equal(-1.0, c.CrossedPercent, 9);
    }

    [Fact]
    public void A_pair_without_detail_gives_no_measurement()
    {
        var l = new Rgba(W, H); var r = new Rgba(W, H); var mask = new byte[W * H];
        Array.Fill(l.P, (byte)128); Array.Fill(r.P, (byte)128); Array.Fill(mask, (byte)1);
        Assert.Null(RealStereo.Finish(l, mask, r, mask, 800, 43.6));
    }

    [Fact]
    public void The_right_eye_gets_the_colors_of_the_left_eye()
    {
        var (l, lm, r, rm) = Pair(4, 28, gain: 0.8);
        var pair = RealStereo.Finish(l, lm, r, rm, 800, 43.6)!;
        Assert.True(pair.ColorMatched);
        double Mean(Rgba img) { double s = 0; for (int i = 0; i < img.P.Length; i += 4) s += img.P[i] + img.P[i + 1] + img.P[i + 2]; return s / (img.P.Length / 4 * 3); }
        double ml = Mean(pair.Left), mr = Mean(pair.Right);
        _out.WriteLine($"mean left {ml:F2}, right {mr:F2}");
        Assert.InRange(mr / ml, 0.985, 1.015);          // 0.8 before the fit
        // the fit of a known change: a gain and an offset per channel
        var src = new List<double[]>(); var dst = new List<double[]>();
        for (int i = 0; i < 2000; i++) { double v = 20 + i % 200; src.Add(new[] { v, v * 0.9, v * 1.1 }); dst.Add(new[] { v * 1.2 + 3, v * 0.9, v * 1.1 - 2 }); }
        var fit = RealStereo.FitColor(src, dst)!;
        var o = fit.Apply(100, 90, 110);
        Assert.InRange(o[0], 122, 124); Assert.InRange(o[1], 89, 91); Assert.InRange(o[2], 107, 109);
        Assert.Null(RealStereo.FitColor(src.Take(50).ToList(), dst.Take(50).ToList()));
    }

    [Fact]
    public void The_result_does_not_depend_on_the_run()
    {
        var (l, lm, r, rm) = Pair(4, 28, 0.9);
        var a = RealStereo.Finish(l, lm, r, rm, 800, 43.6)!; var b = RealStereo.Finish(l, lm, r, rm, 800, 43.6)!;
        Assert.Equal(a.Left.P, b.Left.P);
        Assert.Equal(a.Right.P, b.Right.P);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Measured.ShiftPx), BitConverter.DoubleToInt64Bits(b.Measured.ShiftPx));
    }

    [Fact]
    public void The_rectifying_rotation_puts_the_baseline_on_the_x_axis_and_the_crop_is_the_largest_common_rectangle()
    {
        var b = new[] { 43.5, -1.5, 0.4 };
        var rot = RealStereo.RectifyingRotation(b);
        double n = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
        // R · baseline = (|baseline|, 0, 0), and R is a rotation
        for (int i = 0; i < 3; i++) Assert.Equal(i == 0 ? n : 0, rot[3 * i] * b[0] + rot[3 * i + 1] * b[1] + rot[3 * i + 2] * b[2], 9);
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.Equal(i == j ? 1 : 0, rot[3 * i] * rot[3 * j] + rot[3 * i + 1] * rot[3 * j + 1] + rot[3 * i + 2] * rot[3 * j + 2], 9);
        // a mask with a valid block from (160, 80) to (800, 560): the rectangle is inside it, set in by one sample, with the frame's shape
        var mask = new byte[W * H];
        for (int y = 80; y < 560; y++) for (int x = 160; x < 800; x++) mask[y * W + x] = 1;
        var (x0, y0, x1, y1) = RealStereo.LargestRectangle(mask, W, H, (double)W / H);
        Assert.True(x0 >= 160 && y0 >= 80 && x1 <= 800 && y1 <= 560);
        Assert.Equal(4.0 / 3, (double)(x1 - x0) / (y1 - y0), 1);
        Assert.True((x1 - x0) * (y1 - y0) > 0.9 * 624 * 468);
    }

    [Fact]
    public void Side_by_side_puts_the_left_eye_on_the_left_at_full_or_half_width()
    {
        var l = new Rgba(8, 4); var r = new Rgba(8, 4);
        for (int i = 0; i < l.P.Length; i += 4) { l.P[i] = 200; r.P[i + 2] = 100; }
        var full = RealStereo.SideBySide(l, r, halfWidth: false);
        Assert.Equal((16, 4), (full.W, full.H));
        Assert.Equal(200, full.P[0]); Assert.Equal(100, full.P[(8 * 4) + 2]);
        var half = RealStereo.SideBySide(l, r, halfWidth: true);
        Assert.Equal((8, 4), (half.W, half.H));
        Assert.Equal(200, half.P[0]); Assert.Equal(100, half.P[(4 * 4) + 2]);
    }

    // ================================================================================================================ MPO

    /// <summary>The APP segments of a JPEG picture: marker and payload.</summary>
    static List<(byte Marker, byte[] Data)> Segments(byte[] j)
    {
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, j[..2]);
        var o = new List<(byte, byte[])>(); int p = 2;
        while (j[p] == 0xFF && j[p + 1] != 0xDA)
        {
            int len = (j[p + 2] << 8) | j[p + 3];
            o.Add((j[p + 1], j[(p + 4)..(p + 2 + len)]));
            p += 2 + len;
        }
        return o;
    }

    /// <summary>The entries of a TIFF directory at <paramref name="at"/>: tag → (type, count, the four value bytes).</summary>
    static Dictionary<int, (int Type, int Count, uint Value)> Ifd(byte[] t, int at, out uint next)
    {
        var o = new Dictionary<int, (int, int, uint)>(); int n = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(at));
        for (int i = 0; i < n; i++)
        {
            int e = at + 2 + 12 * i;
            o[BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e))] = (BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e + 2)), (int)BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(e + 4)), BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(e + 8)));
        }
        next = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(at + 2 + 12 * n));
        return o;
    }

    [Fact]
    public void An_mpo_file_holds_two_pictures_with_the_index_and_the_attributes()
    {
        var (l, _, r, _) = Pair(4, 28);
        var mpo = MpoWriter.Encode(l, r, baselineMm: 43.57, quality: 85);
        var first = Segments(mpo);
        // Exif first, then the multi-picture segment
        Assert.Contains(first, s => s.Marker == 0xE1 && s.Data.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8));
        var mpf = first.Single(s => s.Marker == 0xE2 && s.Data.AsSpan(0, 4).SequenceEqual("MPF\0"u8)).Data[4..];
        Assert.Equal(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, mpf[..4]);
        var index = Ifd(mpf, (int)BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(4)), out uint attrAt);
        Assert.Equal("0100", System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(index[0xB000].Value)));
        Assert.Equal(2u, index[0xB001].Value);                                   // number of pictures
        Assert.Equal(32, index[0xB002].Count);                                   // two entries of 16 bytes
        int e = (int)index[0xB002].Value;
        uint attr1 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e)), size1 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e + 4)), off1 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e + 8));
        uint attr2 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e + 16)), size2 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e + 20)), off2 = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(e + 24));
        Assert.Equal(0x20020002u, attr1);                                        // representative picture, one view of a stereo pair
        Assert.Equal(0x00020002u, attr2);
        Assert.Equal(0u, off1);
        Assert.Equal((uint)mpo.Length, size1 + size2);
        // the offset of the second picture counts from the byte order mark of the multi-picture header
        int header = mpo.AsSpan().IndexOf("MPF\0"u8) + 4;
        Assert.Equal(size1, (uint)header + off2);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, mpo[(int)size1..((int)size1 + 2)]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, mpo[((int)size1 - 2)..(int)size1]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, mpo[^2..]);
        // the attributes of each picture: its number, the base viewpoint, the convergence angle and the baseline in meters
        var a1 = Ifd(mpf, (int)attrAt, out _);
        Assert.Equal(1u, a1[0xB101].Value); Assert.Equal(1u, a1[0xB204].Value);
        var (one, two) = MpoWriter.Split(mpo);
        Assert.Equal((int)size1, one.Length); Assert.Equal((int)size2, two.Length);
        var mpf2 = Segments(two).Single(s => s.Marker == 0xE2).Data[4..];
        var a2 = Ifd(mpf2, 8, out _);
        Assert.Equal(2u, a2[0xB101].Value); Assert.Equal(1u, a2[0xB204].Value);
        int bl = (int)a2[0xB206].Value;
        Assert.Equal(0.04357, BinaryPrimitives.ReadUInt32LittleEndian(mpf2.AsSpan(bl)) / (double)BinaryPrimitives.ReadUInt32LittleEndian(mpf2.AsSpan(bl + 4)), 6);
        int cv = (int)a2[0xB205].Value;
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(mpf2.AsSpan(cv)));
        // the same pair always gives the same file
        Assert.Equal(mpo, MpoWriter.Encode(l, r, 43.57, 85));
        Assert.Throws<ArgumentException>(() => MpoWriter.Encode(l, new Rgba(8, 8), 43.57));
    }

    // ================================================================================================================ requests and the panel

    [Fact]
    public void Format_names_files_and_requests()
    {
        foreach (var (name, f, file) in new[] { ("stereo-sbs", StereoFormat.Sbs, "_stereo-sbs.jpg"), ("stereo-anaglyph", StereoFormat.Anaglyph, "_stereo-anaglyph.jpg"),
                                                ("stereo-wiggle", StereoFormat.Wiggle, "_stereo-wiggle.gif"), ("stereo-mpo", StereoFormat.Mpo, "_stereo.mpo") })
        {
            Assert.True(StereoExport.TryParse(name, out var parsed));
            Assert.Equal(f, parsed);
            Assert.Equal(name, StereoExport.NameOf(f));
            Assert.Equal(file, StereoExport.Suffix(f, new StereoRequest()));
        }
        Assert.Equal("_stereo-wiggle.webp", StereoExport.Suffix(StereoFormat.Wiggle, new StereoRequest { Container = "webp" }));
        Assert.False(StereoExport.TryParse("stereo", out _));
        // a request without stereo formats needs what it needed before; the depth source needs the registration state
        var plain = new ExportRequest { Formats = Array.Empty<ExportImageFormat>() };
        Assert.Null(plain.Stereo);
        Assert.False(plain.NeedsDepth);
        Assert.False((plain with { Stereo = new StereoRequest { Formats = new[] { StereoFormat.Sbs } } }).NeedsDepth);
        Assert.True((plain with { Stereo = new StereoRequest { Formats = new[] { StereoFormat.Sbs }, Source = StereoSource.Depth } }).NeedsDepth);
        // the library takes the four names, and an old stored request still loads
        var o = new ExportOptions { Formats = new[] { "jpg", "stereo-sbs", "stereo-mpo" } };
        Lux.Library.Core.ExportJobs.Validate(o);
        var req = Lux.Library.Core.ExportJobs.BuildRequest(o, "/tmp/out", "c1");
        Assert.Equal(new[] { StereoFormat.Sbs, StereoFormat.Mpo }, req.Stereo!.Formats);
        Assert.Equal(StereoSource.Auto, req.Stereo.Source);
        Assert.Equal(new[] { ExportImageFormat.Jpeg }, req.Formats);
        Assert.Null(Lux.Library.Core.ExportJobs.BuildRequest(new ExportOptions(), "/tmp/out", "c1").Stereo);
        Assert.Equal("Stereo side by side", Lux.Library.Core.ExportJobs.DisplayName("stereo-sbs"));
        const string old = """{"CaptureId":"c1","Options":{"Formats":["dng","jpg"],"Level":1,"OutputFolder":"/tmp/out","FileNamePattern":"{name}","IfExists":0,"UseSavedEdits":true,"EditsVariant":null,"DepthEffectFNumber":null,"DepthEffectOff":false,"FocusDepthMm":null,"Adjustments":null,"UseCache":true,"LongEdge":null,"NewerToneStyle":null,"Unturned":null,"EmbedColorProfile":false,"TiffColorSpace":null},"Priority":null,"BatchId":null,"BatchTitle":null}""";
        var loaded = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, old));
        Assert.Null(Lux.Library.Core.ExportJobs.BuildRequest(loaded.Options, "/tmp/out", "c1").Stereo);
        var back = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, Lux.Library.Core.JobRequests.Serialize(new ExportJobRequest("c1", o))));
        Assert.Equal(o.Formats, back.Options.Formats);
    }

    [Fact]
    public void A_capture_without_a_lens_pair_is_refused_or_falls_back()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00443.lri");     // held turned
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00443.lri: skipped"); return; }
        var capture = Lux.Engine.Lri.LriFile.Load(lri);
        int held = Lux.Store.HeldTurn.Degrees(capture);
        Assert.True(held is 90 or 270);
        Assert.Null(RealStereo.Lenses(capture, held, out string? why));
        Assert.Contains("held turned", why);
        Assert.NotNull(RealStereo.Lenses(capture, 0, out _));
        var real = StereoExport.Prepare(capture, new StereoRequest { Formats = new[] { StereoFormat.Sbs }, Source = StereoSource.Real, HeldTurn = held }, null);
        Assert.NotNull(real.Refusal); Assert.False(real.UseDepth); Assert.Null(real.Pair);
        var auto = StereoExport.Prepare(capture, new StereoRequest { Formats = new[] { StereoFormat.Sbs }, HeldTurn = held }, null);
        Assert.Null(auto.Refusal); Assert.True(auto.UseDepth);
        Assert.Contains(auto.Notes, n => n.Contains("held turned") && n.Contains("depth map"));
        Assert.True(StereoExport.Prepare(capture, new StereoRequest { Formats = new[] { StereoFormat.Sbs }, Source = StereoSource.Depth, HeldTurn = held }, null).UseDepth);
    }

    [AvaloniaFact]
    public async Task Export_panel_offers_the_stereo_formats()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        var rows = panel.Formats.Where(f => f.Key.StartsWith("stereo-", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "stereo-sbs", "stereo-anaglyph", "stereo-wiggle", "stereo-mpo" }, rows.Select(f => f.Key));
        Assert.Equal(new[] { "Stereo side by side", "Stereo anaglyph", "Stereo wiggle", "Stereo MPO" }, rows.Select(f => f.Name));
        Assert.All(rows, f => { Assert.False(f.IsOn); Assert.True(f.IsAvailable); Assert.False(string.IsNullOrEmpty(f.Note)); });
        foreach (var f in panel.Formats) f.IsOn = f.Key is "stereo-sbs" or "stereo-mpo";
        await h.SettleAsync();
        Assert.True(panel.CanAdd);
        Assert.StartsWith("Stereo side by side + Stereo MPO", panel.Describe());
        Assert.EndsWith("_stereo-sbs.jpg", panel.FileNamePreview);
        h.Shot("export-panel-stereo");
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.Equal(new[] { "stereo-sbs", "stereo-mpo" }, panel.LastOptions!.Formats);
        Assert.Equal("Stereo side by side + Stereo MPO", Lux.App.Fake.FakeJobQueue.FormatList(panel.LastOptions));
    }

    // ================================================================================================================ a real capture

    /// <summary>The path of the app: a session export of a stereo format alone needs no render state and uses the lens pair.</summary>
    [Fact]
    public async Task A_session_exports_the_lens_pair()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00442.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00442.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        string dir = Path.Combine(Paths.UiScratch, "stereo-session");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        using var s = CaptureSession.Open(lri, new SessionOptions { Threads = Math.Min(8, Environment.ProcessorCount), DisplayCacheBytes = 0 });
        var res = await s.ExportAsync(new ExportRequest
        {
            Formats = Array.Empty<ExportImageFormat>(), OutDirectory = dir, Stem = "pair", UseSavedEdits = false,
            Stereo = new StereoRequest { Formats = new[] { StereoFormat.Sbs, StereoFormat.Mpo }, Size = 640, HeldTurn = Math.Max(0, Lux.Store.HeldTurn.Degrees(s.Lri)) },
        });
        foreach (var n in res.Notes) _out.WriteLine(n);
        Assert.Contains(res.Notes, n => n.StartsWith("stereo: the real lens pair A5 and A1", StringComparison.Ordinal));
        Assert.Equal(new[] { "stereo-sbs", "stereo-mpo" }, res.Outputs.Select(o => o.Name));
        Assert.Equal((0, 0), (res.Width, res.Height));
        Assert.True(new FileInfo(Path.Combine(dir, "pair_stereo-sbs.jpg")).Length > 10_000);
        var (one, two) = MpoWriter.Split(File.ReadAllBytes(Path.Combine(dir, "pair_stereo.mpo")));
        Assert.True(one.Length > 10_000 && two.Length > 10_000);
    }

    static int Cli(string args, out string log)
    {
        var psi = new ProcessStartInfo(Paths.LuxLight, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["LUX_FORBID_DEFAULT_APP_DATA"] = "1";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        log = o + err.Result;
        return p.ExitCode;
    }

    /// <summary>A capture held level with a scene inside the comfort budget: the lens pair is used, and the four files are written at a
    /// small size. The MPO holds the two pictures.</summary>
    [Fact]
    public void A_real_capture_gives_the_four_files_from_its_lens_pair()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00442.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00442.lri: skipped"); return; }
        string dir = Path.Combine(Paths.UiScratch, "stereo");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Assert.Equal(0, Cli($"convert \"{lri}\" --formats stereo-sbs,stereo-anaglyph,stereo-wiggle,stereo-mpo --stereo-size 800 --stereo-layout half --no-cache -o \"{dir}\"", out string log));
        _out.WriteLine(log);
        Assert.Contains("stereo: the real lens pair A5 and A1 (43.6 mm apart)", log);
        string F(string s) => Path.Combine(dir, "L16_00442" + s);
        foreach (var s in new[] { "_stereo-sbs.jpg", "_stereo-anaglyph.jpg", "_stereo-wiggle.gif", "_stereo.mpo" }) Assert.True(new FileInfo(F(s)).Length > 10_000, s);
        static (int W, int H) JpegSize(byte[] j)
        {
            int p = 2;
            while (true)
            {
                int len = (j[p + 2] << 8) | j[p + 3];
                if (j[p + 1] is 0xC0 or 0xC2) return ((j[p + 7] << 8) | j[p + 8], (j[p + 5] << 8) | j[p + 6]);
                p += 2 + len;
            }
        }
        var (one, two) = MpoWriter.Split(File.ReadAllBytes(F("_stereo.mpo")));
        var eye = JpegSize(one);
        Assert.Equal(eye, JpegSize(two));
        Assert.Equal(800, eye.W);
        Assert.Equal(4.0 / 3, (double)eye.W / eye.H, 2);
        Assert.Equal(eye, JpegSize(File.ReadAllBytes(F("_stereo-sbs.jpg"))));        // half width: the frame of one eye
        Assert.Equal(eye, JpegSize(File.ReadAllBytes(F("_stereo-anaglyph.jpg"))));
        // forcing the depth source is refused only by a wrong value
        Assert.Equal(2, Cli($"convert \"{lri}\" --formats stereo-sbs --stereo-source both --no-cache -o \"{dir}\"", out string bad));
        Assert.Contains("--stereo-source must be auto, real or depth", bad);
        Assert.Equal(2, Cli($"convert \"{lri}\" --formats jpg --stereo-layout half --no-cache -o \"{dir}\"", out bad));
        Assert.Contains("It applies to: stereo-sbs", bad);
    }
}
