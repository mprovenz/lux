using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lux.App.Views;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Registration;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Tests of the telephoto alignment check. The check runs on synthetic pictures with a known rotation. The analysis cache keeps the
/// checked geometry apart from the default one. The export panel offers the check, and a stored export request without the setting
/// still loads. A real capture is registered once and checked when <c>LUX_TEST_CAPTURES</c> names the capture folder.
/// </summary>
public sealed class TeleCheckTests
{
    readonly ITestOutputHelper _out;
    public TeleCheckTests(ITestOutputHelper output) { _out = output; }

    // ================================================================================================================ synthetic scene

    const int W = 480, H = 360, Cw = 440, Ch = 330;
    const float F = 800f, Plane = 3000f;

    /// <summary>A smooth texture with detail at several scales, from a fixed generator. Values are 30 to 220.</summary>
    static double Texture(double x, double y)
    {
        double s = 0; uint seed = 12345;
        for (int k = 0; k < 24; k++)
        {
            seed = seed * 1664525u + 1013904223u; double fx = ((seed >> 8) % 1000) / 1000.0 * 0.5 - 0.25;
            seed = seed * 1664525u + 1013904223u; double fy = ((seed >> 8) % 1000) / 1000.0 * 0.5 - 0.25;
            seed = seed * 1664525u + 1013904223u; double ph = ((seed >> 8) % 1000) / 1000.0 * 6.283;
            s += Math.Sin(fx * x + fy * y + ph);
        }
        return Math.Clamp(125 + s * 22, 30, 220);
    }

    static CalibData Cam(float cx, float cy, double[] r, double[] t) => new()
    {
        K = new[] { F, 0, cx, 0, F, cy, 0, 0, 1 }, R = r.Select(v => (float)v).ToArray(), T = t.Select(v => (float)v).ToArray(),
    };

    static double[] RotY(double degrees)
    {
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new[] { c, 0, s, 0, 1, 0, -s, 0, c };
    }

    static double[] Mul(double[] a, double[] b)
    {
        var o = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
        return o;
    }

    /// <summary>The scene: a textured plane at 3 m. The reference camera is at the origin. The module is 20 mm beside it. The canvas
    /// picture is made with the module turned by <paramref name="degrees"/> about its vertical axis, and the pose that the check
    /// gets has no such turn.</summary>
    static (TeleCheck.Reference Ref, byte[] Canvas, CalibData View) Scene(double degrees)
    {
        var gray = new byte[W * H]; var depth = new float[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { gray[y * W + x] = (byte)Math.Round(Texture(x, y)); depth[y * W + x] = Plane; }
        var identity = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        var rf = TeleCheck.Prepare(gray, W, H, depth, Cam(W / 2f, H / 2f, identity, new double[3]));
        var t = new double[] { -20, 0, 0 };
        var view = Cam(Cw / 2f, Ch / 2f, identity, t);
        // the true camera: x_cam = Rt (X + t), with the module turned by Rt
        var rt = RotY(degrees);
        var canvas = new byte[Cw * Ch];
        for (int v = 0; v < Ch; v++)
            for (int u = 0; u < Cw; u++)
            {
                // ray of the canvas pixel in the true camera, taken back to the world, cut with the plane
                double rx = (u - Cw / 2.0) / F, ry = (v - Ch / 2.0) / F;
                double dx = rt[0] * rx + rt[3] * ry + rt[6], dy = rt[1] * rx + rt[4] * ry + rt[7], dz = rt[2] * rx + rt[5] * ry + rt[8];
                double cx = -t[0], cy = -t[1], cz = -t[2];
                double s = (Plane - cz) / dz;
                double X = cx + s * dx, Y = cy + s * dy;
                double px = F * X / Plane + W / 2.0, py = F * Y / Plane + H / 2.0;
                if (px < 0 || py < 0 || px > W - 1 || py > H - 1) continue;
                canvas[v * Cw + u] = (byte)Math.Round(Texture(px, py));
            }
        return (rf, canvas, view);
    }

    [Fact]
    public void A_turned_module_is_measured_and_corrected()
    {
        var (rf, canvas, view) = Scene(0.4);
        var r = TeleCheck.Check(rf, canvas, Cw, Ch, view, threads: 2);
        _out.WriteLine($"matches {r.Matches}, residual {r.Residual:F2} px, after {r.ResidualAfter:F2} px, splits {r.Splits}, angle {r.AngleDegrees:F4} deg: {r.Reason}");
        Assert.True(r.Matches >= TeleCheck.MinMatches);
        // 0.4 degrees at a focal length of 800 pixels move the picture by 5.6 pixels of the stereo image, 11.2 of the full frame
        Assert.InRange(r.Residual, 10.2, 12.2);
        Assert.Equal(4, r.Splits);
        Assert.True(r.Apply);
        Assert.InRange(r.AngleDegrees, 0.38, 0.42);
        Assert.True(r.ResidualAfter < 0.5);
        // the fitted rotation is the turn of the module
        var want = RotY(0.4);
        for (int i = 0; i < 9; i++) Assert.Equal(want[i], r.Rotation![i], 3);
    }

    [Fact]
    public void An_aligned_module_is_left_as_it_is()
    {
        var (rf, canvas, view) = Scene(0);
        var r = TeleCheck.Check(rf, canvas, Cw, Ch, view);
        Assert.True(r.Matches >= TeleCheck.MinMatches);
        Assert.True(r.Residual < 0.5);
        Assert.False(r.Apply);
        Assert.Equal("aligned", r.Reason);
        // a turn below the 4 pixel limit is measured and also left alone
        (rf, canvas, view) = Scene(0.1);
        r = TeleCheck.Check(rf, canvas, Cw, Ch, view);
        Assert.InRange(r.Residual, 2.0, 3.6);
        Assert.False(r.Apply);
    }

    [Fact]
    public void A_module_without_texture_or_without_overlap_is_left_as_it_is()
    {
        var (rf, canvas, view) = Scene(0.4);
        // a canvas without detail gives no matches
        var flat = canvas.Select(v => v == 0 ? (byte)0 : (byte)128).ToArray();
        var r = TeleCheck.Check(rf, flat, Cw, Ch, view);
        Assert.Equal(0, r.Matches);
        Assert.False(r.Apply);
        // an empty canvas has no overlap
        r = TeleCheck.Check(rf, new byte[Cw * Ch], Cw, Ch, view);
        Assert.Equal(0, r.Matches);
        Assert.False(r.Apply);
        Assert.Equal("no matches", r.Reason);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_thread_count()
    {
        var (rf, canvas, view) = Scene(0.4);
        var a = TeleCheck.Check(rf, canvas, Cw, Ch, view, threads: 1);
        var b = TeleCheck.Check(rf, canvas, Cw, Ch, view, threads: 6);
        Assert.Equal(a.Matches, b.Matches);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Residual), BitConverter.DoubleToInt64Bits(b.Residual));
        Assert.Equal(a.Rotation!.Select(BitConverter.DoubleToInt64Bits), b.Rotation!.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void The_corrected_slot_is_the_rotated_canvas_camera()
    {
        var slot = new CalibDataFull
        {
            K = new[] { 3300f, 0, 2080, 0, 3300, 1560, 0, 0, 1 }, R = RotY(1.5).Select(v => (float)v).ToArray(), T = new[] { -31f, 4, 2 },
            K2 = new[] { 3300f, 0, 2080, 0, 3300, 1560, 0, 0, 1 },
        };
        var pose = ViewPose.Identity();
        pose.P = RotY(-0.7).Select(v => (float)v).ToArray(); pose.U = new[] { 1f, 2, 3 }; pose.Scale2 = (0.5f, 0.5f); pose.Shift2 = (0.5f, 0.5f);
        pose.Q = RotY(0.2).Select(v => (float)v).ToArray(); pose.Scale3 = (1.02f, 1.02f); pose.Shift3 = (3f, -2f);
        var rot = RotY(0.5);
        var before = ViewTransform.Apply(pose, slot);
        var after = ViewTransform.Apply(pose, TeleCheck.Corrected(pose, slot, rot));
        var wantR = Mul(rot, before.R.Select(v => (double)v).ToArray());
        for (int i = 0; i < 9; i++) Assert.Equal(wantR[i], after.R[i], 5);
        for (int i = 0; i < 3; i++) Assert.Equal(rot[3 * i] * before.T[0] + rot[3 * i + 1] * before.T[1] + rot[3 * i + 2] * before.T[2], after.T[i], 3);
        for (int i = 0; i < 9; i++) Assert.Equal(before.K[i], after.K[i], 2);
        // the slot keeps everything that the rotation does not change
        var same = TeleCheck.Corrected(pose, slot, new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });
        for (int i = 0; i < 9; i++) Assert.Equal(slot.R[i], same.R[i], 5);
        Assert.Equal(slot.K2, same.K2);
    }

    // ================================================================================================================ the analysis cache

    sealed class MemoryCache : IAnalysisCache
    {
        public readonly Dictionary<(string, string), byte[]> Blocks = new();
        public readonly Dictionary<string, string> Complete = new();
        public byte[]? Read(string tier, string key) => Blocks.TryGetValue((tier, key), out var b) ? b : null;
        public IReadOnlyCollection<string> Keys(string tier) => Blocks.Keys.Where(k => k.Item1 == tier).Select(k => k.Item2).ToList();
        public void Write(string tier, IReadOnlyList<(string Key, byte[] Data)> blocks, int elementSize) { foreach (var (k, d) in blocks) Blocks[(tier, k)] = d; }
        public void SetComplete(string tier, string? descriptor) { if (descriptor is null) Complete.Remove(tier); else Complete[tier] = descriptor; }
        public string? CompleteDescriptor(string tier) => Complete.TryGetValue(tier, out var d) ? d : null;
        public void Drop(string tier) { foreach (var k in Blocks.Keys.Where(k => k.Item1 == tier).ToList()) Blocks.Remove(k); Complete.Remove(tier); }
        public IReadOnlyList<CacheTierStats> Stats() => Array.Empty<CacheTierStats>();
        public string Location => "memory";
        public void Dispose() { }
    }

    static CaptureGeometry Geometry(float marker, IReadOnlyList<string>? report)
    {
        var k = new[] { 3300f, 0, 2080, 0, 3300, 1560, 0, 0, 1 };
        var slot = new CalibDataFull { K = (float[])k.Clone(), R = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = new[] { marker, 0, 0 } };
        var tele = new CaptureGeometry.TeleCamera(6, "B1", CameraCalib.Identity(k), CameraCalib.Identity(k), (100, 80), ViewPose.Identity(), slot);
        return new CaptureGeometry
        {
            RefId = 0, Names = new Dictionary<int, string> { [0] = "A1", [6] = "B1" }, RefPair = (CameraCalib.Identity(k), CameraCalib.Identity(k)),
            RefPose = ViewPose.Identity(), RefSlot = slot.Clone(), Tele = new[] { tele }, FullDepth = (new float[] { 1, 2, 3, 4 }, 2, 2), TeleCheckReport = report,
        };
    }

    [Fact]
    public void The_cache_keeps_the_checked_geometry_apart_from_the_default_one()
    {
        var cache = new MemoryCache();
        // a checked geometry alone: a default export finds nothing
        AnalysisCache.SaveGeometry(cache, Geometry(7f, new[] { "B1 corrected (12.0 px off, turned 0.30 degrees)" }));
        Assert.Null(AnalysisCache.LoadGeometry(cache));
        Assert.Null(cache.CompleteDescriptor(AnalysisCache.GeometryTier));
        Assert.Equal(CacheLevel.None, AnalysisCache.LevelOf(cache));
        var check = AnalysisCache.LoadGeometry(cache, null, teleCheck: true)!;
        Assert.Equal(7f, check.Tele[0].Slot.T[0]);
        Assert.Equal(new[] { "B1 corrected (12.0 px off, turned 0.30 degrees)" }, check.TeleCheckReport);
        // the default geometry goes into its own tier, and each reader gets its own
        AnalysisCache.SaveGeometry(cache, Geometry(3f, null));
        var plain = AnalysisCache.LoadGeometry(cache)!;
        Assert.Equal(3f, plain.Tele[0].Slot.T[0]);
        Assert.Null(plain.TeleCheckReport);
        Assert.Equal(7f, AnalysisCache.LoadGeometry(cache, null, teleCheck: true)!.Tele[0].Slot.T[0]);
        Assert.Equal(CacheLevel.Geometry, AnalysisCache.LevelOf(cache));
        // a default geometry alone: an export with the check finds nothing
        cache.Drop(AnalysisCache.TeleCheckGeometryTier);
        Assert.Null(AnalysisCache.LoadGeometry(cache, null, teleCheck: true));
        Assert.NotNull(AnalysisCache.LoadGeometry(cache));
    }

    [Fact]
    public void A_build_refuses_kept_tiles_and_a_geometry_of_the_other_kind()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00439.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00439.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        var cache = new MemoryCache();
        Assert.Throws<ArgumentException>(() => ExportBuild.Build(lri, 1, null, 2, null, new BuildOptions { TeleCheck = true, StoredTiles = AnalysisCache.TileSource(cache, 1) }));
        Assert.Throws<ArgumentException>(() => ExportBuild.Build(lri, 0, null, 2, null, new BuildOptions { TeleCheck = true, Geometry = Geometry(3f, null) }));
        Assert.Throws<ArgumentException>(() => ExportBuild.Build(lri, 0, null, 2, null, new BuildOptions { Geometry = Geometry(3f, new[] { "B1 unchanged" }) }));
    }

    // ================================================================================================================ requests and the panel

    [Fact]
    public void Stored_export_request_without_the_setting_loads_with_the_check_off()
    {
        // a request as the job table holds it from a version without the setting
        const string old = """{"CaptureId":"c1","Options":{"Formats":["dng","jpg"],"Level":1,"OutputFolder":"/tmp/out","FileNamePattern":"{name}","IfExists":0,"UseSavedEdits":true,"EditsVariant":null,"DepthEffectFNumber":null,"DepthEffectOff":false,"FocusDepthMm":null,"Adjustments":null,"UseCache":true,"LongEdge":null,"NewerToneStyle":null,"Unturned":null,"EmbedColorProfile":false,"TiffColorSpace":null},"Priority":null,"BatchId":null,"BatchTitle":null}""";
        var loaded = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, old));
        Assert.False(loaded.Options.TeleCheck);
        Assert.False(Lux.Library.Core.ExportJobs.BuildRequest(loaded.Options, "/tmp/out", "c1").TeleCheck);
        Assert.False(new ExportRequest { Formats = Array.Empty<Lux.Engine.Pipeline.Export.ExportImageFormat>() }.TeleCheck);
        Assert.False(new SessionOptions().TeleCheck);
        Assert.False(new BuildOptions().TeleCheck);
        // a request with the setting keeps it through the job table, and the engine request gets it
        var set = new ExportJobRequest("c1", loaded.Options with { TeleCheck = true });
        var back = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, Lux.Library.Core.JobRequests.Serialize(set)));
        Assert.True(back.Options.TeleCheck);
        Assert.True(Lux.Library.Core.ExportJobs.BuildRequest(back.Options, "/tmp/out", "c1").TeleCheck);
    }

    [AvaloniaFact]
    public async Task Export_panel_offers_the_telephoto_check_and_remembers_it()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        var view = h.Window.GetVisualDescendants().OfType<ExportPanel>().Single();
        var box = view.FindControl<CheckBox>("TeleCheckBox")!;
        var note = view.FindControl<TextBlock>("TeleCheckNote")!;
        await h.SettleAsync();
        Assert.Equal("Check telephoto alignment", box.Content);
        Assert.False(box.IsChecked);
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal("Changes the merged picture slightly and takes about a second.", note.Text);
        Assert.True(note.IsEffectivelyVisible);
        Assert.DoesNotContain("telephoto", panel.Describe());
        // the default export does not ask for the check
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.False(panel.LastOptions!.TeleCheck);

        h.Library.OpenExport();
        panel = h.Library.ExportPanel!;
        await h.SettleAsync();
        view = h.Window.GetVisualDescendants().OfType<ExportPanel>().Single();
        box = view.FindControl<CheckBox>("TeleCheckBox")!;
        Assert.False(box.IsChecked);
        box.IsChecked = true;
        await h.SettleAsync();
        Assert.True(panel.TeleCheck);
        Assert.EndsWith("· telephoto check", panel.Describe());
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Avalonia.Vector(0, 400);
        await h.SettleAsync();
        h.Shot("export-panel-tele-check");
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.True(panel.LastOptions!.TeleCheck);
        // the panel opens again with the same choice
        h.Library.OpenExport();
        Assert.True(h.Library.ExportPanel!.TeleCheck);
    }

    // ================================================================================================================ a real capture

    /// <summary>A capture whose B1 module ends far off after the registration. The check corrects B1 and leaves the other modules
    /// with their slots, bit for bit. Without the switch the registration does not run the check.</summary>
    [Fact]
    public void A_real_capture_gets_its_misaligned_module_corrected()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00439.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00439.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        int threads = Math.Min(8, Environment.ProcessorCount);
        var api = StereoAsyncApi.Run(Lux.Engine.Lri.LriFile.Load(lri), null, runHigher: true, runDense: true, null, null, null, threads, default);
        Assert.False(api.Cdp.TeleCheckEnabled);
        Assert.Empty(api.Cdp.TeleCheckResults);
        Assert.Null(CaptureGeometry.FromRegistration(api).TeleCheckReport);
        var before = api.Cdp.Higher.ToDictionary(c => c.Name, c => c.Slot.Clone());
        api.Cdp.CheckTele();
        foreach (var c in api.Cdp.Higher)
        {
            var r = api.Cdp.TeleCheckResults[c.Id];
            _out.WriteLine($"{c.Name}: {r.Matches} matches, {r.Residual:F1} px, splits {r.Splits}, {r.AngleDegrees:F3} deg, {r.Reason}");
            bool same = before[c.Name].R.SequenceEqual(c.Slot.R) && before[c.Name].T.SequenceEqual(c.Slot.T) && before[c.Name].K.SequenceEqual(c.Slot.K);
            Assert.Equal(c.Name != "B1", same);
            Assert.Equal(c.Name == "B1", r.Apply);
        }
        var b1 = api.Cdp.TeleCheckResults[api.Cdp.Higher.First(c => c.Name == "B1").Id];
        Assert.InRange(b1.Residual, 40, 50);
        Assert.InRange(b1.AngleDegrees, 0.6, 0.9);
        Assert.True(b1.ResidualAfter < 3);
        // a second check finds the corrected module aligned
        api.Cdp.CheckTele();
        var again = api.Cdp.TeleCheckResults[api.Cdp.Higher.First(c => c.Name == "B1").Id];
        _out.WriteLine($"B1 after the correction: {again.Residual:F1} px, {again.Reason}");
        Assert.True(again.Residual < 4);
        Assert.False(again.Apply);
    }

    /// <summary>The path of the app: a session with the check on runs it with the registration and says what it did. The export is
    /// small, and its depth map makes the session run the registration.</summary>
    [Fact]
    public async Task A_session_with_the_check_runs_it_and_reports_it()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00439.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00439.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        string dir = Path.Combine(Paths.UiScratch, "telecheck-session");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        using var s = CaptureSession.Open(lri, new SessionOptions { TeleCheck = true, Threads = Math.Min(8, Environment.ProcessorCount), DisplayCacheBytes = 0 });
        var res = await s.ExportAsync(new ExportRequest { Formats = new[] { Lux.Engine.Pipeline.Export.ExportImageFormat.Ppm }, DepthMap = true, Level = 3, OutDirectory = dir, Stem = "check", UseSavedEdits = false });
        foreach (var n in res.Notes) _out.WriteLine(n);
        Assert.Contains(res.Notes, n => n.StartsWith("telephoto check: ", StringComparison.Ordinal) && n.Contains("B1 corrected") && n.Contains("B4 unchanged"));
        Assert.False(s.HasKeptGeometry);          // no capture context: nothing is kept
        Assert.True(File.Exists(Path.Combine(dir, "check.ppm")));
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

    /// <summary>A small export does not use the telephoto modules. With the switch, <c>lux-light</c> says so and writes the same
    /// picture. The switch belongs to convert only.</summary>
    [Fact]
    public void A_small_export_with_the_switch_is_the_default_picture()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00439.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00439.lri: skipped"); return; }
        string root = Path.Combine(Paths.UiScratch, "telecheck");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        string a = Path.Combine(root, "default"), b = Path.Combine(root, "check");
        Assert.Equal(0, Cli($"convert \"{lri}\" --level 3 --formats ppm --no-cache -o \"{a}\"", out string logA));
        Assert.Equal(0, Cli($"convert \"{lri}\" --level 3 --formats ppm --no-cache --tele-check -o \"{b}\"", out string logB));
        Assert.DoesNotContain("telephoto check", logA);
        Assert.Contains("telephoto check: not needed", logB);
        Assert.Equal(File.ReadAllBytes(Path.Combine(a, "L16_00439.ppm")), File.ReadAllBytes(Path.Combine(b, "L16_00439.ppm")));
        Assert.Equal(2, Cli($"cache \"{lri}\" --tele-check", out string logC));
        Assert.Contains("--tele-check is an option of convert", logC);
    }
}
