using Avalonia.Headless.XUnit;
using Lux.App.ViewModels;
using Lux.Engine.Pipeline.Animate;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Library;
using Lux.Store;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>The zoom-through, the fog, the pan-and-zoom, the slice reveal and the tilt-shift sweep: their frame math on synthetic
/// pictures, their entries in the Animate panel on the fake library, and each effect once on a real capture at a small size (when
/// <c>LUX_TEST_CAPTURES</c> names the capture folder).</summary>
public sealed class AnimationLooksTests(ITestOutputHelper output, AnimationLooksTests.RealCapture real) : IClassFixture<AnimationLooksTests.RealCapture>
{
    readonly RealCapture _real = real;

    // ================================================================================================================ synthetic pictures

    static Rgba Picture(int w, int h, Func<int, int, (byte, byte, byte)> f)
    {
        var o = new Rgba(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var (r, g, b) = f(x, y); long p = ((long)y * w + x) * 4; o.P[p] = r; o.P[p + 1] = g; o.P[p + 2] = b; o.P[p + 3] = 255; }
        return o;
    }

    /// <summary>A repeatable noise value 0–255 for a position.</summary>
    static byte Noise(int x, int y) { uint v = (uint)(x * 73856093) ^ (uint)(y * 19349663); v ^= v >> 13; v *= 0x5bd1e995; v ^= v >> 15; return (byte)v; }

    static double MeanAbsDiff(Rgba a, Rgba b)
    {
        Assert.Equal((a.W, a.H), (b.W, b.H));
        double s = 0; for (long p = 0; p < a.P.LongLength; p++) if ((p & 3) != 3) s += Math.Abs(a.P[p] - b.P[p]);
        return s / (a.W * a.H * 3.0);
    }

    // ---------------------------------------------------------------------------------------------------------------- pyramid and path

    [Fact]
    public void Pyramid_levels_are_means_and_whole_pixel_reads_are_copies()
    {
        var img = Picture(256, 192, (x, y) => (Noise(x, y), Noise(y, x), (byte)(x ^ y)));
        var pyr = ImagePyramid.Build(img, 16);
        Assert.Equal(new[] { (256, 192), (128, 96), (64, 48), (32, 24) }, pyr.Levels.Select(l => (l.W, l.H)));
        var l1 = pyr.Levels[1];
        for (int c = 0; c < 3; c++)
            Assert.Equal((img.P[c] + img.P[4 + c] + img.P[256 * 4 + c] + img.P[257 * 4 + c] + 2) >> 2, l1.P[c]);

        // one picture pixel per frame pixel, from a whole pixel: the frame is that part of the picture
        var a = pyr.Sample(40, 24, 1.0, 64, 48);
        for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++) for (int c = 0; c < 3; c++)
            Assert.Equal(img.P[((y + 24) * 256 + x + 40) * 4 + c], a.P[(y * 64 + x) * 4 + c]);
        // two picture pixels per frame pixel, from an even pixel: the frame is that part of level 1
        var b = pyr.Sample(32, 16, 2.0, 48, 40);
        for (int y = 0; y < 40; y++) for (int x = 0; x < 48; x++) for (int c = 0; c < 3; c++)
            Assert.Equal(l1.P[((y + 8) * 128 + x + 16) * 4 + c], b.P[(y * 48 + x) * 4 + c]);
        // between two levels the frame lies between them: a flat picture stays flat at every scale
        var flat = ImagePyramid.Build(Picture(256, 192, (_, _) => (90, 120, 200)), 16);
        foreach (double s in new[] { 1.0, 1.3, 2.0, 2.9, 3.7 })
        {
            var f = flat.Sample(3.3, 1.7, s, 40, 30);
            Assert.All(Enumerable.Range(0, 40 * 30), i => Assert.Equal((90, 120, 200), (f.P[i * 4], f.P[i * 4 + 1], f.P[i * 4 + 2])));
        }
    }

    [Fact]
    public void Trilinear_frames_change_evenly_through_the_scales()
    {
        // a zoom over fine texture: the frame-to-frame sharpness never jumps where the pyramid level changes
        var img = Picture(1024, 768, (x, y) => { byte v = (byte)((Noise(x / 3, y / 3) + Noise(x, y)) / 2); return (v, v, v); });
        var pyr = ImagePyramid.Build(img, 16);
        var path = new ZoomPath(1024, 768, 128, 96, 600, 400, 60);
        double Sharp(Rgba f) { double s = 0; for (int y = 0; y < f.H; y++) for (int x = 1; x < f.W; x++) { double d = f.P[(y * f.W + x) * 4] - f.P[(y * f.W + x - 1) * 4]; s += d * d; } return Math.Sqrt(s / (f.W * f.H)); }
        var sharp = Enumerable.Range(0, 60).Select(k => { var (s, x0, y0) = path.At(k); return Sharp(pyr.Sample(x0, y0, s, 128, 96)); }).ToArray();
        double worst = Enumerable.Range(1, 59).Max(k => Math.Abs(20 * Math.Log10(sharp[k] / sharp[k - 1])));
        output.WriteLine($"largest sharpness step {worst:0.00} dB");
        Assert.True(worst < 1.5, $"sharpness steps by {worst:0.00} dB between two frames");
    }

    [Fact]
    public void Zoom_path_starts_on_the_whole_picture_and_ends_on_the_target_pixels()
    {
        var path = new ZoomPath(8000, 6000, 1000, 750, 5210.4, 1777.7, 100);
        var first = path.At(0);
        Assert.Equal(8.0, first.Scale, 9);
        Assert.Equal(0, first.X0, 6); Assert.Equal(0, first.Y0, 6);
        var last = path.At(99);
        Assert.Equal(1.0, last.Scale, 12);
        Assert.Equal(Math.Floor(last.X0), last.X0); Assert.Equal(Math.Floor(last.Y0), last.Y0);
        Assert.InRange(last.X0 + 500, 5209, 5211); Assert.InRange(last.Y0 + 375, 1776, 1778);
        Assert.Equal(8.0, path.Zoom, 9);
        double prev = double.MaxValue;
        for (int k = 0; k < 100; k++)
        {
            var (s, x0, y0) = path.At(k);
            Assert.True(s <= prev + 1e-12, "the zoom never goes back");
            Assert.True(x0 >= -1e-6 && y0 >= -1e-6 && x0 + 1000 * s <= 8000 + 1e-6 && y0 + 750 * s <= 6000 + 1e-6, $"frame {k} leaves the picture");
            prev = s;
        }
        // eased: the scale changes least at the two ends
        Assert.True(path.At(0).Scale / path.At(1).Scale < path.At(49).Scale / path.At(50).Scale);
        // a target in a corner: the last window stays inside the picture
        var corner = new ZoomPath(8000, 6000, 1000, 750, 7990, 5990, 10).At(9);
        Assert.Equal(7000, corner.X0); Assert.Equal(5250, corner.Y0);
        // an early end: two picture pixels per frame pixel, from an even pixel
        var early = new ZoomPath(8000, 6000, 1000, 750, 4001, 3001, 10, 2.0).At(9);
        Assert.Equal(2.0, early.Scale); Assert.Equal(0, early.X0 % 2); Assert.Equal(0, early.Y0 % 2);
    }

    [Fact]
    public void Detail_map_finds_the_part_with_real_fine_detail()
    {
        // a pattern of blocks with edges one pixel sharp. The level-1 picture is a soft, half-size copy of it. The full-size picture: its
        // left half is the level-1 picture enlarged (no detail of its own); its right half is the sharp pattern
        const int w = 1280, h = 640;
        var sharp = Picture(w, h, (x, y) => { byte v = (byte)(Noise(x / 48, y / 48) > 128 ? 190 : 70); return (v, v, v); });
        var half = ImagePyramid.FromLevels(new[] { ImagePyramid.Halve(ImagePyramid.Halve(sharp)) }).Sample(0, 0, 0.5, w / 2, h / 2);
        var big = ImagePyramid.FromLevels(new[] { half }).Sample(0, 0, 0.5, w, h);
        var img = Picture(w, h, (x, y) => { long p = ((long)y * w + x) * 4; var from = x < w / 2 ? big : sharp; return (from.P[p], from.P[p + 1], from.P[p + 2]); });
        var map = DetailMap.Compute(img, half, 4);
        Assert.Equal((w / 64, h / 64), (map.Bw, map.Bh));
        double left = map.PooledGain(64, 64, w / 2 - 128, h - 128), right = map.PooledGain(w / 2 + 64, 64, w / 2 - 128, h - 128);
        output.WriteLine($"detail: enlarged part {left:0.0} dB, sharp part {right:0.0} dB");
        Assert.True(right > left + 2.5, "the sharp part carries more of the fine band");
        Assert.True(left < 2.3, "an enlarged picture is below the telephoto threshold");
        var pick = map.ChooseTarget(256, 192);
        Assert.NotNull(pick);
        Assert.True(pick!.Value.X > w / 2 + 128, "the target lies in the sharp part");
        // a flat picture has no edges: no detail anywhere
        var none = DetailMap.Compute(Picture(256, 256, (_, _) => (128, 128, 128)), Picture(128, 128, (_, _) => (128, 128, 128)), 2);
        Assert.All(none.Gain, g => Assert.Equal(0f, g));
        Assert.Equal(0, none.PooledGain(0, 0, 256, 256));
    }

    // ---------------------------------------------------------------------------------------------------------------- tone curve, fog

    /// <summary>A picture whose display values follow a known tone curve of a linear ramp scene: display = scene^0.6 (in linear light).</summary>
    static (Rgba Picture, float[] Linear) Toned(int w, int h)
    {
        var lin = new float[w * h * 4];
        var pic = Picture(w, h, (x, y) =>
        {
            double scene = 0.002 + 0.9 * ((x + y * w) % 997) / 996.0;
            int i = (y * w + x) * 4; lin[i] = lin[i + 1] = lin[i + 2] = (float)scene; lin[i + 3] = 1;
            double disp = Math.Pow(scene, 0.6);
            byte v = (byte)Math.Round(255 * (disp <= 0.0031308 ? disp * 12.92 : 1.055 * Math.Pow(disp, 1 / 2.4) - 0.055));
            return (v, v, v);
        });
        return (pic, lin);
    }

    [Fact]
    public void Tone_curve_is_fitted_and_inverts()
    {
        var (pic, lin) = Toned(400, 300);
        var tone = ToneCurve.Fit(pic.P, lin, 400, 300);
        foreach (double scene in new[] { 0.01, 0.05, 0.18, 0.4, 0.8 })
        {
            double disp = tone.Forward(scene);
            Assert.InRange(disp / Math.Pow(scene, 0.6), 0.9, 1.1);
            Assert.InRange(tone.Inverse(disp) / scene, 0.85, 1.15);
            Assert.InRange(tone.Ratio((float)scene) / (disp / scene), 0.99, 1.01);
        }
        // past the bright end the curve keeps rising, slowly
        Assert.True(tone.Forward(4.0) > tone.Forward(1.0));
        var id = ToneCurve.Identity();
        Assert.Equal(0.25, id.Forward(0.25), 6); Assert.Equal(0.25, id.Inverse(0.25), 6);
    }

    [Fact]
    public void Fog_thickens_with_distance_and_clear_frames_are_the_picture()
    {
        const int w = 200, h = 120;
        var (pic, lin) = Toned(w, h);
        // near at the bottom (2 m), far at the top (60 m)
        var q = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) q[y * w + x] = 1f / (60f - 58f * y / (h - 1));
        var tone = ToneCurve.Fit(pic.P, lin, w, h);

        var rise = new FogLook(pic, q, tone, FogMode.Rise);
        Assert.InRange(rise.MedianM, 28, 34);
        Assert.Equal(-Math.Log(0.35) / rise.MedianM, rise.Density, 6);
        // no fog at the two ends of the loop: the picture comes back through the tone curve within a code value
        foreach (double t in new[] { 0.0, 1.0 })
        {
            var clear = rise.Frame(t);
            double worst = 0; for (long p = 0; p < clear.P.LongLength; p++) worst = Math.Max(worst, Math.Abs(clear.P[p] - pic.P[p]));
            Assert.True(worst == 0, $"a clear frame differs from the picture by {worst}");
        }
        // at the peak the far rows move towards the fog's light far more than the near rows
        var peak = rise.Frame(0.5);
        double Change(Rgba f, int y0, int y1) { double s = 0; for (int y = y0; y < y1; y++) for (int x = 0; x < w; x++) s += Math.Abs(f.P[(y * w + x) * 4] - pic.P[(y * w + x) * 4]); return s / ((y1 - y0) * w); }
        Assert.True(Change(peak, 0, 10) > 3 * Change(peak, h - 10, h), "far rows fog more than near rows");
        // twice the density fogs more
        var dense = new FogLook(pic, q, tone, FogMode.Rise, 2.0).Frame(0.5);
        Assert.True(Change(dense, 40, 80) > Change(peak, 40, 80));

        // the bank: its front starts at the far distance and stops at 30 % of the median distance; what is nearer stays clear
        var bank = new FogLook(pic, q, tone, FogMode.Bank);
        Assert.Equal(bank.FarM, bank.At(0).Front, 6);
        Assert.Equal(0.3 * bank.MedianM, bank.At(1).Front, 6);
        Assert.Equal(2.5 * bank.Density, bank.At(0.3).Density, 9);
        var start = bank.Frame(0);
        Assert.True(Change(start, h / 2, h) <= 1.0, "the near half is clear before the bank arrives");
        Assert.True(Change(bank.Frame(1), 20, 60) > 5 * Math.Max(Change(start, 20, 60), 0.2));
        // a given color: thick fog shows it
        var red = new FogLook(pic, q, tone, FogMode.Rise, 40.0, (200, 40, 40)).Frame(0.5);
        long top = (5L * w + 100) * 4;
        Assert.InRange(red.P[top], 195, 205); Assert.InRange(red.P[top + 1], 35, 45);
    }

    [Fact]
    public void Inverse_depth_is_the_area_mean()
    {
        var d = new float[8 * 4];
        for (int i = 0; i < d.Length; i++) d[i] = i % 8 < 4 ? 1000f : 4000f;       // 1 m left, 4 m right
        var q = LookMath.InverseDepth(d, 8, 4, 2, 2);
        Assert.Equal(new[] { 1f, 0.25f, 1f, 0.25f }, q);
        var mixed = LookMath.InverseDepth(d, 8, 4, 1, 1);
        Assert.Equal(0.625f, mixed[0], 5);
        // a pixel without depth counts as far
        d[0] = 0;
        Assert.True(LookMath.InverseDepth(d, 8, 4, 8, 4)[0] <= 0.011f);
    }

    // ---------------------------------------------------------------------------------------------------------------- slice reveal

    [Fact]
    public void Slices_reveal_in_order_with_a_soft_edge()
    {
        const int w = 100, h = 60;
        var pic = Picture(w, h, (x, y) => ((byte)(40 + x), (byte)(200 - y), 90));
        var q = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) q[y * w + x] = 0.02f + 0.98f * y / (h - 1);   // near at the bottom
        var near = new SliceLook(pic, q, SliceOrder.NearToFar);
        // the end shows the picture; the start hides nearly all of it
        Assert.True(MeanAbsDiff(near.Frame(1), pic) < 0.01);
        var first = near.Frame(0);
        long p0 = (10L * w + 50) * 4;
        double gray = 0.55 * (0.299 * pic.P[p0] + 0.587 * pic.P[p0 + 1] + 0.114 * pic.P[p0 + 2]) + 12.8;
        Assert.InRange(first.P[p0], gray - 1, gray + 1); Assert.Equal(first.P[p0], first.P[p0 + 1]); Assert.Equal(first.P[p0], first.P[p0 + 2]);
        // in between: near rows shown, far rows hidden, a soft band between; each pixel only ever gains
        Assert.Equal(1f, near.Revealed(0.95f, 0.5)); Assert.Equal(0f, near.Revealed(0.05f, 0.5));
        float mid = near.Revealed(0.51f, 0.5);
        Assert.InRange(mid, 0.2f, 0.8f);
        foreach (float qq in new[] { 0.05f, 0.3f, 0.6f, 0.9f })
        {
            float last = 0;
            for (int k = 0; k <= 20; k++) { float a = near.Revealed(qq, k / 20.0); Assert.True(a >= last - 1e-6f); last = a; }
            Assert.Equal(1f, last);
        }
        // a hard edge has no band
        var hard = new SliceLook(pic, q, SliceOrder.NearToFar, 0);
        Assert.All(new[] { 0.2f, 0.49f, 0.53f, 0.8f }, v => Assert.Contains(hard.Revealed(v, 0.5), new[] { 0f, 1f }));
        // the other orders
        var far = new SliceLook(pic, q, SliceOrder.FarToNear);
        Assert.Equal(1f, far.Revealed(0.05f, 0.5)); Assert.Equal(0f, far.Revealed(0.95f, 0.5)); Assert.Equal(1f, far.Revealed(0.95f, 1));
        var focus = new SliceLook(pic, q, SliceOrder.FromFocus, SliceLook.DefaultSoftness, focusM: 2.0);   // 0.5 per meter
        Assert.Equal(0f, focus.Revealed(0.8f, 0));
        Assert.True(focus.Revealed(0.5f, 0.2) > focus.Revealed(0.8f, 0.2));
        Assert.True(focus.Revealed(0.5f, 0.2) > focus.Revealed(0.2f, 0.2));
        Assert.Equal(1f, focus.Revealed(0.03f, 1)); Assert.Equal(1f, focus.Revealed(0.99f, 1));
    }

    // ---------------------------------------------------------------------------------------------------------------- pan-and-zoom

    static PanZoomSource Scene(int w, int h, Func<int, int, float> depth)
    {
        var col = Picture(w, h, (x, y) => (Noise(x / 4, y / 4), (byte)(x * 255 / w), (byte)(y * 255 / h)));
        var d = new Plane(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) d[x, y] = depth(x, y);
        return new PanZoomSource(col, d, focalPx: w * 1.03, focalLevel1: 4275);
    }

    [Fact]
    public void Pan_and_zoom_move_follows_its_rules()
    {
        var m = new PanZoomMove { TargetX = 0.3, TargetY = 0.6, HoldMm = 5000, NearMm = 2000, Travel = 0.04 };
        var a = m.At(0, 1000, 750); var mid = m.At(0.5, 1000, 750); var b = m.At(1, 1000, 750);
        Assert.Equal(1.10, a.Zoom, 9); Assert.Equal(1.35, b.Zoom, 9);
        Assert.Equal((500.0, 375.0), (a.Cx, a.Cy));
        Assert.Equal(1000 / 2.0 / 1.35, b.Cx, 6);            // the window stops at the picture's edge before it reaches the target
        Assert.Equal(450.0, b.Cy, 6);
        Assert.Equal(-40, a.Tx, 9); Assert.Equal(0, mid.Tx, 9); Assert.Equal(40, b.Tx, 9);
        Assert.Equal(0, a.Tz, 9); Assert.Equal(160, b.Tz, 9);
        Assert.Equal(80, m.SidewaysMm, 9); Assert.Equal(160, m.ForwardMm, 9);
        Assert.Equal(0, b.Ty);
        var turned = (m with { Vertical = true }).At(1, 1000, 750);
        Assert.Equal(0, turned.Tx); Assert.Equal(40, turned.Ty, 9);
        // 2 % of the nearest distance on a wide photo with a far background: between 3 and 4 % of the frame width
        Assert.InRange(PanZoomMove.Parallax(0.02, 1.03, 2350, 100000), 0.03, 0.04);
    }

    [Fact]
    public void Pan_and_zoom_preflight_passes_clean_depth_and_stops_noisy_depth()
    {
        // a wall at one distance: no travel can tear it
        var wall = Scene(480, 360, (_, _) => 4000f);
        var move = new PanZoomMove { TargetX = 0.5, TargetY = 0.5, HoldMm = 4000, NearMm = wall.NearMm() };
        Assert.Equal(4000, wall.NearMm());
        var clean = wall.Preflight(move);
        Assert.Equal(PanZoomMove.Travels[^1], clean.Limit);
        Assert.Equal("", clean.FirstFailure);
        Assert.All(clean.Tried, t => Assert.True(t.Stats.Acceptable));

        // far content whose depth jumps between planes from pixel to pixel: it tears at the smallest travel
        var noisy = Scene(480, 360, (x, y) => 12000f + 6000f * (Noise(x, y) % 4));
        var torn = noisy.Preflight(move with { HoldMm = 15000, NearMm = noisy.NearMm() });
        output.WriteLine($"noisy depth: limit {torn.Limit}, first failure: {torn.FirstFailure}; tears {torn.Tried[0].Stats.TearPct:0.00} %");
        Assert.Equal(0, torn.Limit);
        Assert.NotEqual("", torn.FirstFailure);

        // frames: the right size, no empty pixel; a flat move without travel keeps a flat picture
        var step = Scene(480, 360, (x, y) => x is > 180 and < 300 && y > 120 ? 1500f : 9000f);   // a near box in front of a far wall
        var mv = new PanZoomMove { TargetX = 0.5, TargetY = 0.5, HoldMm = 9000, NearMm = step.NearMm(), Travel = 0.08 };
        var f0 = step.Frame(mv, 0, 320, 240); var f1 = step.Frame(mv, 1, 320, 240);
        Assert.Equal((320, 240), (f1.W, f1.H));
        for (long p = 3; p < f1.P.LongLength; p += 4) Assert.Equal(255, f1.P[p]);
        Assert.True(MeanAbsDiff(f0, f1) > 1, "the frames move");
        Assert.Equal(step.Frame(mv, 0.37, 160, 120).P, step.Frame(mv, 0.37, 160, 120).P);          // the same frame every time
        // the depth-aware move differs from the flat one only by what slides
        var flat = step.Frame(mv with { Flat = true }, 1, 320, 240);
        Assert.True(MeanAbsDiff(flat, f1) > 0.2);
        // smaller source, same scene
        Assert.Equal((240, 180), (step.Scaled(240).W, step.Scaled(240).H));
        Assert.Equal(step.FocalPx / 2, step.Scaled(240).FocalPx, 6);
    }

    [Fact]
    public void Depth_smoother_evens_out_surfaces_and_keeps_steps_at_picture_edges()
    {
        // left: a gray wall whose depth jumps between two far planes from pixel to pixel; right: a red box, nearer, with clean depth
        const int w = 320, h = 200;
        var guide = Picture(w, h, (x, _) => x < 200 ? ((byte)120, (byte)120, (byte)120) : ((byte)200, (byte)40, (byte)40));
        var depth = new Plane(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) depth[x, y] = x < 200 ? (Noise(x, y) % 2 == 0 ? 12000f : 18000f) : 3000f;
        var smooth = DepthSmoother.Smooth(guide, depth, 4275 * 43.6);
        double Spread(Plane p, int x0, int x1) { var v = new List<double>(); for (int y = 20; y < h - 20; y++) for (int x = x0; x < x1; x++) v.Add(1.0 / p[x, y]); double m = v.Average(); return Math.Sqrt(v.Average(q => (q - m) * (q - m))) / m; }
        double before = Spread(depth, 20, 180), after = Spread(smooth, 20, 180);
        output.WriteLine($"spread of the wall's inverse depth: {before:0.000} -> {after:0.000}");
        Assert.True(after < before / 20, "the wall becomes one surface");
        Assert.InRange(smooth[100, 100], 13000f, 16500f);                         // between its two planes
        Assert.InRange(smooth[260, 100], 2900f, 3100f);                           // the box keeps its distance
        Assert.True(smooth[190, 100] > 3 * smooth[210, 100], "the step stays at the picture's edge");
        // the smoothed wall no longer tears under a camera move
        var col = Picture(w, h, (x, y) => (Noise(x / 4, y / 4), (byte)x, (byte)y));
        var move = new PanZoomMove { TargetX = 0.3, TargetY = 0.5, HoldMm = 14000, NearMm = 3000, Travel = 0.04 };
        double Tears(Plane p) => new PanZoomSource(col, p, w * 1.03, 4275).Measure(move, 1).TearPct;
        Assert.True(Tears(smooth) < Tears(depth) / 5, $"tears {Tears(depth):0.00} % -> {Tears(smooth):0.00} %");
        // a pixel without depth stays without
        depth[5, 5] = 0;
        Assert.Equal(0f, DepthSmoother.Smooth(guide, depth, 4275 * 43.6)[5, 5]);
    }

    [Fact]
    public void Miniature_look_raises_color_and_contrast()
    {
        var a = Picture(4, 1, (x, _) => x switch { 0 => ((byte)128, (byte)128, (byte)128), 1 => ((byte)200, (byte)100, (byte)100), 2 => ((byte)10, (byte)10, (byte)10), _ => ((byte)255, (byte)0, (byte)0) });
        var b = Picture(4, 1, (_, _) => (0, 0, 0));
        var m = LookMath.Miniature(a, a, 0);
        Assert.Equal((128, 128, 128), (m.P[0], m.P[1], m.P[2]));                 // mid gray stays
        Assert.True(m.P[4] > 200 && m.P[5] < 100);                               // a color gets stronger
        Assert.Equal(m.P[5], m.P[6]);
        Assert.True(m.P[8] < 10);                                                // dark gets darker
        Assert.Equal((255, 0, 0), (m.P[12], m.P[13], m.P[14]));                  // a full color cannot grow
        var half = LookMath.Miniature(a, b, 0.5f);
        Assert.InRange(half.P[0], 58, 60);                                       // the blend, then the contrast: (64 − 128) × 1.08 + 128
    }

    // ================================================================================================================ the plan defaults

    [Fact]
    public void New_effects_have_their_own_defaults()
    {
        foreach (var e in new[] { AnimationEffect.ZoomThrough, AnimationEffect.Fog, AnimationEffect.PanZoom, AnimationEffect.SliceReveal, AnimationEffect.TiltShift })
        {
            Assert.True(AnimationSpec.PrefersVideo(e));
            Assert.Equal(40, AnimationSpec.DefaultFrameMs(e));
            Assert.Equal(e == AnimationEffect.PanZoom ? 125 : 100, AnimationSpec.DefaultFrames(e));
            Assert.Equal(AnimationFormat.Mp4, AnimationEffects.PreferredFormat(e, canWriteVideo: true));
            Assert.Null(AnimationEffects.PreferredFormat(e, canWriteVideo: false));
            Assert.Contains(e, AnimationEffects.All);
        }
        Assert.False(AnimationSpec.DefaultPingPong(AnimationEffect.ZoomThrough, AnimationPath.Sweep));
        Assert.True(AnimationSpec.DefaultPingPong(AnimationEffect.TiltShift, AnimationPath.Sweep));
        Assert.Equal(1000, AnimationSpec.DefaultHoldMs(AnimationEffect.ZoomThrough));
        Assert.Equal(0, AnimationSpec.DefaultHoldMs(AnimationEffect.Fog));
        // the effects there were keep theirs
        Assert.Equal(24, AnimationSpec.DefaultFrames(AnimationEffect.RackFocus));
        Assert.Equal(70, AnimationSpec.DefaultFrameMs(AnimationEffect.RackFocus));
        Assert.Equal(60, AnimationSpec.DefaultFrameMs(AnimationEffect.ParallaxSweep));
        Assert.False(AnimationSpec.PrefersVideo(AnimationEffect.ParallaxSweep));
        Assert.Null(AnimationEffects.PreferredFormat(AnimationEffect.RackFocus, true));
        Assert.Equal(new[] { "zoom-through", "fog", "pan-zoom", "slice-reveal", "tilt-shift" }, AnimationEffects.All.Skip(5).Select(AnimationEffects.Key));
        // previews of different settings do not share frames
        var o = new AnimationOptions { Effect = AnimationEffect.Fog, Frames = 100 };
        Assert.NotEqual(AnimationPreviews.FrameKey(o), AnimationPreviews.FrameKey(o with { FogMode = FogMode.Rise }));
        Assert.NotEqual(AnimationPreviews.FrameKey(o), AnimationPreviews.FrameKey(o with { FogDensity = 2 }));
        var z = o with { Effect = AnimationEffect.ZoomThrough };
        Assert.NotEqual(AnimationPreviews.FrameKey(z), AnimationPreviews.FrameKey(z with { Target = (0.2, 0.3) }));
        Assert.Equal(AnimationPreviews.FrameKey(z), AnimationPreviews.FrameKey(z with { Format = AnimationFormat.WebP }));
    }

    // ================================================================================================================ the Animate panel

    static async Task<Harness> OneSelectedAsync()
    {
        var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = 1;
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        h.Library.Click(h.Item("L16_00493"), false, false);
        await h.SettleAsync();
        return h;
    }

    static async Task SelectAsync(Harness h, AnimatePanelViewModel p, AnimationEffect e)
    {
        p.Effects.First(x => x.Effect == e).SelectCommand.Execute(null);
        await h.SettleAsync(() => p.Player.Preview?.Options.Effect == e && p.Player.IsComplete && p.Player.HasFrame, timeoutMs: 30000);
    }

    [AvaloniaFact]
    public async Task Panel_lists_the_new_effects_with_their_settings()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        Assert.Equal(new[] { "Rack focus", "Aperture sweep", "Wigglegram", "Parallax", "Lens tour", "Zoom-through", "Fog", "Pan and zoom", "Slice reveal", "Tilt-shift" },
                     p.Effects.Select(e => e.Name));
        Assert.Equal(24, p.Frames);
        Assert.Equal((int)AnimationFormat.Gif, p.Format.Value);

        // ---- zoom-through: 100 frames forward, a hold on the last frame, MP4
        await SelectAsync(h, p, AnimationEffect.ZoomThrough);
        Assert.True(p.IsZoom && p.HasTargetPick && p.IsLongClip && p.HasFrameCount);
        Assert.False(p.IsRack || p.IsParallax || p.IsFog);
        Assert.Equal(100, p.Frames); Assert.Equal(240, p.FramesMax);
        Assert.False(p.PingPong);
        Assert.Equal((int)AnimationFormat.Mp4, p.Format.Value);
        Assert.Equal(125, p.Player.Length);                                   // 100 frames + 1 s on the last one
        Assert.StartsWith("zoom 1.0", p.Player.Preview!.Frame(0)!.Label);
        Assert.Equal("Automatic: the most telephoto detail", p.TargetText);
        Assert.Null(p.Options().Target);
        // the target is picked on the first frame
        p.Player.Position = 60;
        p.PickTargetPointCommand.Execute(null);
        Assert.True(p.IsPickingTarget && p.Player.IsPicking);
        Assert.Equal(0, p.Player.Position);
        Assert.Equal("Click the photo where the zoom ends", p.Player.PickHint);
        p.ClickPicture(0.25, 0.7);
        await h.SettleAsync(() => p.Target is not null);
        Assert.Equal(0.25, p.Target!.Value.X, 2); Assert.Equal(0.7, p.Target!.Value.Y, 2);
        Assert.Equal("25 % from the left, 70 % from the top", p.TargetText);
        Assert.False(p.Player.IsPicking);
        Assert.Equal(p.Target, p.Options().Target);
        await h.SettleAsync(() => p.Player.Preview?.Options.Target is not null && p.Player.IsComplete, timeoutMs: 30000);
        Harness.Capture(h.Window, "looks-01-zoom-through", Paths.Shots);
        p.ClearTargetCommand.Execute(null);
        Assert.Null(p.Target);

        // ---- fog
        await SelectAsync(h, p, AnimationEffect.Fog);
        Assert.True(p.IsFog && !p.HasTargetPick);
        Assert.Equal(100, p.Player.Length);
        p.FogMode = p.FogModes.First(x => x.Value == (int)FogMode.Rise);
        p.FogDensity = 1.5;
        Assert.Equal("150 %", p.FogDensityText);
        var fog = p.Options();
        Assert.Equal((FogMode.Rise, 1.5, false), (fog.FogMode, fog.FogDensity, fog.PingPong));

        // ---- pan-and-zoom: 125 frames; the travel choices
        await SelectAsync(h, p, AnimationEffect.PanZoom);
        Assert.True(p.IsPan && p.HasTargetPick);
        Assert.Equal(125, p.Frames);
        Assert.Equal((null, false), (p.Options().Travel, p.Options().Flat));
        p.Travel = p.Travels.First(x => x.Value == 40);
        Assert.Equal(0.04, p.Options().Travel);
        p.Travel = p.Travels.First(x => x.Value == 0);
        Assert.True(p.Options().Flat); Assert.Null(p.Options().Travel);
        p.PickTargetPointCommand.Execute(null);
        p.ClickPicture(0.5 + 0.11, 0.5);                                     // the first frame shows the middle enlarged 1.1 times
        await h.SettleAsync(() => p.Target is not null);
        Assert.Equal(0.6, p.Target!.Value.X, 3);

        // ---- slice reveal and tilt-shift
        await SelectAsync(h, p, AnimationEffect.SliceReveal);
        Assert.Null(p.Target);                                                // a target belongs to its effect
        Assert.Equal(125, p.Player.Length);
        p.SliceOrder = p.SliceOrders.First(x => x.Value == (int)SliceOrder.FromFocus);
        p.Softness = 0;
        Assert.Equal("hard", p.SoftnessText);
        Assert.Equal((SliceOrder.FromFocus, 0.0), (p.Options().SliceOrder, p.Options().SliceSoftness));
        await SelectAsync(h, p, AnimationEffect.TiltShift);
        Assert.True(p.IsTilt && p.PingPong);
        Assert.Equal(198, p.Player.Length);                                   // forward and back
        Assert.Null(p.Options().MiniatureFNumber);
        p.Miniature = p.Miniatures.First(x => x.Value == 50);
        p.Tilt = 0;
        Assert.Equal((0.05f, 0.0), (p.Options().MiniatureFNumber, p.Options().Tilt));
        Assert.Equal("none", p.TiltText);
        Harness.Capture(h.Window, "looks-02-tilt-shift", Paths.Shots);

        // ---- back to an effect of the other kind: its frame count and the file type from before
        await SelectAsync(h, p, AnimationEffect.RackFocus);
        Assert.Equal(24, p.Frames); Assert.Equal(48, p.FramesMax);
        Assert.True(p.PingPong);
        Assert.Equal((int)AnimationFormat.Gif, p.Format.Value);
        Assert.Equal(46, p.Player.Length);
    }

    [AvaloniaFact]
    public async Task New_effect_is_queued_as_video_or_as_the_chosen_type_without_ffmpeg()
    {
        await using var h = await OneSelectedAsync();
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        await SelectAsync(h, p, AnimationEffect.Fog);
        long maxBefore = h.Lib.Jobs.GetJobs().Max(j => j.Id);
        p.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => h.Lib.Jobs.Of(JobKind.Animation).Any(j => j.Id > maxBefore));
        var job = h.Lib.Jobs.Of(JobKind.Animation).First(j => j.Id > maxBefore);
        Assert.EndsWith("_fog.mp4", job.PlannedFile);
        Assert.Equal(AnimationEffect.Fog, p.LastOptions!.Effect);
        Assert.Equal(100, p.LastOptions.Frames);
        await h.DisposeAsync();

        // without ffmpeg there is no video type: the file type stays as it was
        await using var h2 = await Harness.CreateAsync();
        h2.Lib.FakeFrameMs = 1; h2.Lib.FakeVideo = false;
        await h2.SelectScopeAsync(h2.Library.Tree.First(n => n.Label == "Autumn").Key);
        h2.Library.Click(h2.Item("L16_00493"), false, false);
        await h2.SettleAsync();
        h2.Library.OpenAnimate();
        var p2 = h2.Library.AnimatePanel!;
        await SelectAsync(h2, p2, AnimationEffect.PanZoom);
        Assert.Equal((int)AnimationFormat.Gif, p2.Format.Value);
        Assert.True(p2.HasVideoNote);
    }

    // ================================================================================================================ a real capture

    /// <summary>One capture opened once for the real-capture tests (read through a link in the UI output folder).</summary>
    public sealed class RealCapture : IDisposable
    {
        CaptureSession? _s;
        public const string Name = "L16_00493";
        public static string Source => Path.Combine(Paths.Captures, Name + ".lri");
        public static bool Present => Paths.Captures.Length > 0 && File.Exists(Source);
        public CaptureSession Session()
        {
            if (_s is not null) return _s;
            Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
            string root = Path.Combine(Path.GetFullPath(Paths.UiScratch), "looks-real");
            Directory.CreateDirectory(root);
            string link = Path.Combine(root, Name + ".lri");
            if (!File.Exists(link)) File.CreateSymbolicLink(link, Source);
            return _s = CaptureSession.Open(link, new SessionOptions { Context = new StoreCaptureContext(link), Threads = Math.Min(8, Environment.ProcessorCount), DisplayCacheBytes = 0 });
        }
        public void Dispose() => _s?.Dispose();
    }

    async Task<(AnimationPlan Plan, List<Rgba> Frames)?> RealAsync(AnimationSpec spec)
    {
        if (!RealCapture.Present) { output.WriteLine($"LUX_TEST_CAPTURES has no {RealCapture.Name}.lri: skipped"); return null; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await AnimationRenderer.CreateAsync(_real.Session(), spec);
        var frames = Enumerable.Range(0, r.Plan.Frames.Count).Select(k => r.Render(k)).ToList();
        output.WriteLine($"{spec.Effect}: {frames.Count} frames of {r.Plan.Size.W} × {r.Plan.Size.H} from level {r.Plan.Level} in {sw.Elapsed.TotalSeconds:0.0} s");
        foreach (var n in r.Plan.Notes) output.WriteLine("  " + n);
        Assert.All(frames, f => Assert.Equal(r.Plan.Size, (f.W, f.H)));
        // the same frame again: the same pixels
        Assert.Equal(frames[1].P, r.Render(1).P);
        return (r.Plan, frames);
    }

    [Fact]
    public async Task Real_zoom_through_quick_look_zooms_onto_the_point()
    {
        // from export level 3: the path and the frames, without the full-size picture
        var got = await RealAsync(new AnimationSpec { Effect = AnimationEffect.ZoomThrough, Frames = 6, Level = 3, LongEdge = 0, ZoomEndEdge = 1040, Target = (0.6, 0.4) });
        if (got is not { } r) return;
        Assert.Equal(6 + 25, r.Plan.Order.Count);                             // the last frame stays for a second
        Assert.Equal(5, r.Plan.Order[^1]);
        Assert.StartsWith("zoom 1.0", r.Plan.Frames[0].Label);
        double z = double.Parse(r.Plan.Frames[^1].Label.Split(' ')[1].TrimEnd('×'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(z, 7.5, 10.5);                                         // the full-size picture over 1040 pixels
        Assert.True(MeanAbsDiff(r.Frames[0], r.Frames[^1]) > 3);
        // the first frame is the whole picture: the level's own render
        var es = _real.Session().PrepareExportSession(new ExportRequest { Formats = new[] { Lux.Engine.Pipeline.Export.ExportImageFormat.Jpeg }, Level = 3 });
        Assert.True(MeanAbsDiff(r.Frames[0], new Rgba(es.Grid.Size.W, es.Grid.Size.H, es.Rgba8().Rgba)) < 0.5);
    }

    [Fact]
    public async Task Real_fog_clears_at_the_ends_of_the_loop_and_covers_the_far_part()
    {
        var got = await RealAsync(new AnimationSpec { Effect = AnimationEffect.Fog, Frames = 5, LongEdge = 520, FogMode = FogMode.Rise });
        if (got is not { } r) return;
        Assert.Equal(5, r.Plan.Order.Count);
        Assert.True(MeanAbsDiff(r.Frames[0], r.Frames[4]) < 0.01, "the loop starts and ends on the same clear picture");
        double fogged = MeanAbsDiff(r.Frames[0], r.Frames[2]);
        output.WriteLine($"mean change at the peak: {fogged:0.0} code values");
        Assert.True(fogged > 5);
        var bank = await RealAsync(new AnimationSpec { Effect = AnimationEffect.Fog, Frames = 4, LongEdge = 520 });
        Assert.StartsWith("fog from ", bank!.Value.Plan.Frames[0].Label);
        Assert.True(MeanAbsDiff(bank.Value.Frames[0], bank.Value.Frames[3]) > 2);
    }

    [Fact]
    public async Task Real_pan_and_zoom_reports_its_travel_limit()
    {
        var got = await RealAsync(new AnimationSpec { Effect = AnimationEffect.PanZoom, Frames = 5, LongEdge = 520 });
        if (got is not { } r) return;
        Assert.Contains(r.Plan.Notes, n => n.Contains("camera travel", StringComparison.OrdinalIgnoreCase) || n.Contains("flat pan and zoom", StringComparison.Ordinal));
        Assert.StartsWith("zoom 1.10", r.Plan.Frames[0].Label);
        Assert.StartsWith("zoom 1.35", r.Plan.Frames[^1].Label);
        Assert.True(MeanAbsDiff(r.Frames[0], r.Frames[^1]) > 2);
        Assert.All(r.Frames, f => { for (long p = 3; p < f.P.LongLength; p += 4) if (f.P[p] != 255) Assert.Fail("an empty pixel is left in a frame"); });
        // a travel past the limit is made as asked, with a warning
        var far = await RealAsync(new AnimationSpec { Effect = AnimationEffect.PanZoom, Frames = 3, LongEdge = 520, Travel = 0.3 });
        Assert.Contains(far!.Value.Plan.Notes, n => n.Contains("past that limit", StringComparison.Ordinal));
        // the flat move differs from it
        var flat = await RealAsync(new AnimationSpec { Effect = AnimationEffect.PanZoom, Frames = 3, LongEdge = 520, Flat = true });
        Assert.True(MeanAbsDiff(flat!.Value.Frames[2], far.Value.Frames[2]) > 0.5);
    }

    [Fact]
    public async Task Real_slice_reveal_ends_on_the_picture()
    {
        var got = await RealAsync(new AnimationSpec { Effect = AnimationEffect.SliceReveal, Frames = 5, LongEdge = 520 });
        if (got is not { } r) return;
        Assert.Equal(5 + 25, r.Plan.Order.Count);
        // the last frame is the photo's own render at that size
        var s = _real.Session();
        var es = s.PrepareExportSession(new ExportRequest { Formats = new[] { Lux.Engine.Pipeline.Export.ExportImageFormat.Jpeg }, Level = r.Plan.Level, Size = r.Plan.Size });
        Assert.True(MeanAbsDiff(r.Frames[^1], new Rgba(r.Plan.Size.W, r.Plan.Size.H, es.Rgba8().Rgba)) < 0.05);
        // the reveal only ever adds color
        double Sat(Rgba f) { double v = 0; for (long p = 0; p < f.P.LongLength; p += 4) v += Math.Max(f.P[p], Math.Max(f.P[p + 1], f.P[p + 2])) - Math.Min(f.P[p], Math.Min(f.P[p + 1], f.P[p + 2])); return v / (f.W * f.H); }
        var sat = r.Frames.Select(Sat).ToArray();
        output.WriteLine("color per frame: " + string.Join(" ", sat.Select(v => v.ToString("0.0"))));
        for (int k = 1; k < sat.Length; k++) Assert.True(sat[k] >= sat[k - 1] - 0.05);
        Assert.True(sat[^1] > sat[0] + 2);
    }

    [Fact]
    public async Task Real_tilt_shift_blurs_outside_a_moving_band()
    {
        var got = await RealAsync(new AnimationSpec { Effect = AnimationEffect.TiltShift, Frames = 5, Level = 4, LongEdge = 0 });
        if (got is not { } r) return;
        Assert.Equal(8, r.Plan.Order.Count);                                  // forward and back
        Assert.Contains(r.Plan.Notes, n => n.StartsWith("Depth effect at f/", StringComparison.Ordinal));
        Assert.StartsWith("f/", r.Plan.Frames[0].Label);
        Assert.True(MeanAbsDiff(r.Frames[0], r.Frames[^1]) > 1, "the focus moves through the scene");
        // without the tilt the frames differ from the tilted ones
        var plain = await RealAsync(new AnimationSpec { Effect = AnimationEffect.TiltShift, Frames = 3, Level = 4, LongEdge = 0, Tilt = 0 });
        Assert.True(MeanAbsDiff(plain!.Value.Frames[1], (await RealAsync(new AnimationSpec { Effect = AnimationEffect.TiltShift, Frames = 3, Level = 4, LongEdge = 0 }))!.Value.Frames[1]) > 0.2);
    }
}
