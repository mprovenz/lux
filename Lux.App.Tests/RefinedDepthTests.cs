using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lux.App.Develop;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.DepthModel;
using Lux.Engine.Pipeline.DepthRefine;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;
using VersionKind = Lux.App.Develop.VersionKind;

namespace Lux.App.Tests;

/// <summary>
/// Refined depth. The fusion runs on a synthetic scene with a synthetic "network" (no model needed): a box whose depth step the camera
/// draws too wide, a thin bar the camera misses, a gap between two bars the camera fills, and a sky with scattered depth. The check
/// against the other cameras runs on synthetic pictures. The Settings section and the first-use question run on the fake library. One
/// test runs the real network on a real capture; it needs <c>LUX_TEST_DEPTH_MODEL</c> (a folder with the installed files) and
/// <c>LUX_TEST_CAPTURES</c>, and is skipped without them.
/// </summary>
public sealed class RefinedDepthTests(ITestOutputHelper output)
{
    // ================================================================================================================ the fusion

    const int W = 960, H = 720;
    const float Near = 2000f, Far = 10000f, FocalBaseline = 3400f * 80f;

    /// <summary>The synthetic scene: the true depth, the camera's depth with its typical faults, the picture, and what a network sees.</summary>
    sealed class Scene
    {
        public readonly float[] Truth = new float[W * H], Camera = new float[W * H], Network = new float[W * H];
        public readonly byte[] Rgba = new byte[W * H * 4];
        public readonly bool[] Sky = new bool[W * H];
        // the box, the thin bar and the two bars with a gap between them
        public static bool Box(int x, int y) => x >= 300 && x < 520 && y >= 330 && y < 600;
        public static bool ThinBar(int x, int y) => x >= 640 && x < 648 && y >= 300 && y < 680;
        public static bool Bars(int x, int y) => y >= 300 && y < 680 && ((x >= 760 && x < 800) || (x >= 830 && x < 870));
        public static bool Gap(int x, int y) => y >= 300 && y < 680 && x >= 800 && x < 830;
        public static bool Fat(int x, int y) => x >= 520 && x < 532 && y >= 330 && y < 600;   // where the camera draws the box too wide
        // a flat poster on the far wall with the picture of a near object: the network sees the object, the cameras see the wall
        public static bool Poster(int x, int y) => x >= 60 && x < 200 && y >= 330 && y < 600;

        public Scene(bool skyIsARoomWall = false, bool poster = false)
        {
            uint seed = 12345;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    bool sky = y < 230;
                    bool near = Box(x, y) || ThinBar(x, y) || Bars(x, y);
                    Sky[i] = sky && !skyIsARoomWall;
                    Truth[i] = sky ? (skyIsARoomWall ? 4000f : 100000f) : near ? Near : Far;
                    // the camera: the box 12 px too wide on its right side, no thin bar, the gap filled, the sky scattered
                    float cam = sky ? (skyIsARoomWall ? 4000f : ((x / 60 + y / 50) % 4 == 0 ? 6000f : 60000f))
                              : Box(x, y) || Fat(x, y) || Bars(x, y) || Gap(x, y) ? Near : Far;
                    Camera[i] = cam;
                    // the network: no unit, not linear in 1/depth, zero in the sky and on the far wall of a room
                    float seen = poster && Poster(x, y) ? Near : Truth[i];
                    Network[i] = sky ? 0f : (float)(0.3 + 9.0 * Math.Pow(1000.0 / seen, 0.8));
                    // the picture: texture everywhere except in an open sky
                    seed = seed * 1664525u + 1013904223u;
                    int noise = (int)(seed >> 24) % 60;
                    byte g = (byte)(Sky[i] ? 200 : (near ? 60 : 120) + noise);
                    Rgba[i * 4] = g; Rgba[i * 4 + 1] = g; Rgba[i * 4 + 2] = g; Rgba[i * 4 + 3] = 255;
                }
        }

        public DepthPriorMaps Prior => new(Network, Network, 0);

        /// <summary>The cross-view cost of the camera's depth: low where the depth is right, high where it is wrong.</summary>
        public float[] Cost(Func<int, int, bool>? alsoHigh = null)
        {
            var c = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) c[y * W + x] = Camera[y * W + x] == Truth[y * W + x] && alsoHigh?.Invoke(x, y) != true ? 1f : 12f;
            return c;
        }
    }

    static double Median(IEnumerable<float> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }

    static IEnumerable<int> Where(Func<int, int, bool> region, int inset = 0)
    {
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (region(x, y) && (inset == 0 || (region(x - inset, y) && region(x + inset, y) && region(x, y - inset) && region(x, y + inset)))) yield return y * W + x;
    }

    static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    [Fact]
    public void Rules_version_is_3() => Assert.Equal(3, RefineConstants.RulesVersion);

    [Fact]
    public void Fusion_corrects_edges_thin_objects_gaps_and_sky_and_keeps_the_rest()
    {
        var s = new Scene();
        var (z, r) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost());
        Assert.True(r.Applied);
        Assert.False(r.SkyOnly);
        output.WriteLine($"rewritten {100.0 * r.Rewritten / r.Pixels:0.0} %, changed {100.0 * r.Changed / r.Pixels:0.0} %, trusted changed {100.0 * r.TrustedChanged / Math.Max(1, r.Trusted):0.00} %, sky {100.0 * r.Sky / r.Pixels:0.0} %");

        // the camera's faults before
        Assert.Equal(Far, Median(Where(Scene.ThinBar).Select(i => s.Camera[i])));
        Assert.Equal(Near, Median(Where(Scene.Gap).Select(i => s.Camera[i])));
        // the thin bar exists now, the gap is open, and the box ends at its outline
        Assert.InRange(Median(Where(Scene.ThinBar, 1).Select(i => z[i])), Near * 0.6, Near * 1.6);
        Assert.InRange(Median(Where(Scene.Gap, 4).Select(i => z[i])), Far * 0.6, Far * 1.6);
        Assert.InRange(Median(Where(Scene.Fat, 2).Select(i => z[i])), Far * 0.6, Far * 1.6);
        // the sky: at the far limit, also where the camera had it near. The rows beside the skyline keep the camera's depth.
        var sky = Where((x, y) => y < 215).ToArray();
        Assert.True(sky.Count(i => s.Camera[i] < 20000f) > sky.Length / 5);
        Assert.True(sky.Count(i => z[i] >= 50000f) > 0.97 * sky.Length, "no near depth is left in the sky");
        Assert.True(sky.Count(i => z[i] > 90000f) > 0.6 * sky.Length, "the open sky is at the far limit");
        Assert.True(r.Sky > 0.25 * r.Pixels);

        // away from the faults the camera's depth stays, bit for bit: the left part of the box and the open background
        foreach (var region in new Func<int, int, bool>[] { (x, y) => x >= 340 && x < 400 && y >= 400 && y < 540, (x, y) => x >= 40 && x < 200 && y >= 380 && y < 640 })
            Assert.Equal(0, Where(region).Count(i => !SameBits(s.Camera[i], z[i])));
        // and close to them no correct depth is lost: the bars beside the gap and the box beside its edge keep their distance
        Assert.All(Where((x, y) => (x >= 770 && x < 790 && y >= 340 && y < 640) || (x >= 440 && x < 500 && y >= 370 && y < 560)), i => Assert.InRange(z[i], Near * 0.97f, Near * 1.03f));
        // a pixel either keeps its exact value or was rewritten; few trusted pixels change
        long same = 0; for (int i = 0; i < z.Length; i++) if (z[i] == s.Camera[i]) same++;
        Assert.True(same >= r.Pixels - r.Rewritten);
        Assert.True(r.TrustedChanged < 0.03 * r.Trusted, $"{r.TrustedChanged} of {r.Trusted} trusted pixels changed");
        Assert.True(r.Changed < 0.45 * r.Pixels);
    }

    [Fact]
    public void Fusion_gives_the_same_result_every_time()
    {
        var s = new Scene();
        var (a, _) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost());
        var (b, _) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan())));
    }

    /// <summary>The stereo guard. The network gives the far wall of a room the same value as the sky, but the camera's depth there is near.</summary>
    [Fact]
    public void The_far_wall_of_a_room_is_not_sky()
    {
        var s = new Scene(skyIsARoomWall: true);
        Assert.Equal(0f, s.Network[0]);
        var (z, r) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost());
        Assert.True(r.Applied);
        Assert.Equal(0, r.Sky);
        Assert.InRange(Median(Where((x, y) => y < 220).Select(i => z[i])), 3900f, 4100f);
    }

    /// <summary>The stereo guard works on each connected area: one picture with a sky and with a dark wall that the network takes for sky.</summary>
    [Fact]
    public void Each_sky_area_needs_far_camera_depth()
    {
        var s = new Scene();
        // a wall in the top right corner, set off from the sky by a post that the network sees
        static bool Wall(int x, int y) => x >= 700 && y < 230;
        static bool Post(int x, int y) => x >= 680 && x < 700 && y < 230;
        foreach (int i in Where(Wall)) s.Camera[i] = 5000f;
        foreach (int i in Where(Post)) { s.Camera[i] = 5000f; s.Network[i] = 3f; }
        var (z, r) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, null);
        Assert.True(r.Applied);
        Assert.All(Where(Wall), i => Assert.True(SameBits(s.Camera[i], z[i])));
        Assert.All(Where((x, y) => x < 640 && y < 200), i => Assert.Equal(RefineConstants.FarDepth, z[i]));
        Assert.Equal(r.Sky, z.Count(v => v == RefineConstants.FarDepth));
    }

    /// <summary>The line guard. The network does not see a wire in front of the sky. The picture shows it: the wire keeps the camera's
    /// depth, and the sky around it goes to the far limit. Single noisy pixels of a night sky do not count as lines.</summary>
    [Fact]
    public void A_thin_line_in_the_sky_keeps_its_depth()
    {
        var s = new Scene();
        static bool Wire(int x, int y) => x >= 100 && x < 800 && y >= 100 && y < 103;
        static bool Speck(int x, int y) => y < 200 && y % 40 == 30 && x % 50 == 25;
        foreach (int i in Where((x, y) => x >= 100 && x < 800 && y >= 96 && y < 107)) s.Camera[i] = 8000f;    // the camera draws the wire too wide
        foreach (int i in Where(Wire)) for (int c = 0; c < 3; c++) s.Rgba[i * 4 + c] = 40;
        foreach (int i in Where(Speck)) for (int c = 0; c < 3; c++) s.Rgba[i * 4 + c] = 160;
        var (z, r) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, null);
        Assert.True(r.Applied);
        Assert.All(Where(Wire), i => Assert.True(SameBits(s.Camera[i], z[i]), "the wire keeps the camera's depth"));
        Assert.All(Where((x, y) => x >= 100 && x < 800 && y >= 98 && y < 105), i => Assert.Equal(8000f, z[i]));
        // the sky away from the wire, the specks included
        Assert.All(Where((x, y) => y < 200 && (y < 70 || y >= 133)), i => Assert.Equal(RefineConstants.FarDepth, z[i]));
        Assert.True(Where(Speck).Count() > 50);
    }

    /// <summary>The band guard. A flat poster shows a near object. The network sees the object and the camera's depth is flat, so the two
    /// disagree. Where the other cameras confirm the camera's depth, it stays. Where they contradict it, the network's shape is used.</summary>
    [Fact]
    public void Depth_that_the_other_cameras_confirm_is_kept()
    {
        var s = new Scene(poster: true);
        static bool Rim(int x, int y) => x >= 66 && x < 90 && y >= 370 && y < 560;          // inside the poster, beside its left edge
        var (kept, r1) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost());
        Assert.True(r1.Applied);
        Assert.All(Where(Scene.Poster), i => Assert.True(SameBits(s.Camera[i], kept[i]), "confirmed depth is kept bit for bit"));

        var (moved, r2) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, s.Cost(alsoHigh: Scene.Poster));
        Assert.True(r2.Applied);
        Assert.Equal(Far, Median(Where(Rim).Select(i => s.Camera[i])));
        Assert.InRange(Median(Where(Rim).Select(i => moved[i])), Near * 0.6, Near * 1.6);
        Assert.True(r2.Rewritten > r1.Rewritten);
    }

    /// <summary>The telephoto case: no other cameras with the same view, so no cross-view cost. Only the sky is corrected.</summary>
    [Fact]
    public void Without_other_cameras_only_the_sky_is_corrected()
    {
        var s = new Scene();
        var (z, r) = DepthFusion.Fuse(s.Camera, s.Rgba, W, H, FocalBaseline, s.Prior, null);
        Assert.True(r.Applied);
        Assert.True(r.SkyOnly);
        Assert.Equal(DepthFusion.NoGroupReason, r.Reason);
        Assert.True(r.Sky > 0.25 * r.Pixels);
        Assert.Equal(r.Sky, r.Rewritten);
        Assert.Equal(0, r.TrustedChanged);
        // below the sky nothing changes, the camera's faults included; in the sky a pixel keeps its depth or goes to the far limit
        Assert.All(Where((x, y) => y >= 230), i => Assert.True(SameBits(s.Camera[i], z[i])));
        Assert.All(Where((x, y) => y < 230), i => Assert.True(SameBits(s.Camera[i], z[i]) || z[i] == RefineConstants.FarDepth));
        Assert.All(Where((x, y) => y < 215), i => Assert.Equal(RefineConstants.FarDepth, z[i]));
        Assert.Equal(r.Sky, Where((x, y) => y < 230).Count(i => z[i] == RefineConstants.FarDepth));

        // no other cameras and no sky: the capture is left as it is
        var room = new Scene(skyIsARoomWall: true);
        var (same, none) = DepthFusion.Fuse(room.Camera, room.Rgba, W, H, FocalBaseline, room.Prior, null);
        Assert.False(none.Applied);
        Assert.True(none.SkyOnly);
        Assert.Equal(DepthFusion.NoGroupReason + ", and the picture has no sky", none.Reason);
        Assert.Same(room.Camera, same);
    }

    [Fact]
    public void A_capture_without_reliable_depth_gets_only_its_sky_corrected()
    {
        var s = new Scene();
        var flat = new byte[s.Rgba.Length]; Array.Fill(flat, (byte)128);                // no texture anywhere: nothing to trust
        var (z, r) = DepthFusion.Fuse(s.Camera, flat, W, H, FocalBaseline, s.Prior, s.Cost());
        Assert.True(r.Applied);
        Assert.True(r.SkyOnly);
        Assert.Equal(DepthFusion.NoCurveReason, r.Reason);
        Assert.Equal(0, r.Trusted);
        Assert.All(Where((x, y) => y >= 230), i => Assert.True(SameBits(s.Camera[i], z[i])));
        Assert.All(Where((x, y) => y < 230), i => Assert.Equal(RefineConstants.FarDepth, z[i]));

        var room = new Scene(skyIsARoomWall: true);
        var (same, none) = DepthFusion.Fuse(room.Camera, flat, W, H, FocalBaseline, room.Prior, room.Cost());
        Assert.False(none.Applied);
        Assert.NotNull(none.Reason);
        Assert.Same(room.Camera, same);
    }

    // ================================================================================================================ the other cameras

    /// <summary>A textured picture, and the picture of another camera for a scene at one disparity: the same picture, moved along the
    /// camera's baseline. <paramref name="offX"/> and <paramref name="offY"/> add a calibration error of the other camera.</summary>
    static class Views
    {
        public const int Vw = 640, Vh = 480;
        public static byte[] Reference()
        {
            var p = new byte[Vw * Vh * 4]; uint seed = 99;
            for (int i = 0; i < Vw * Vh; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                byte g = (byte)(60 + (seed >> 24) % 120);
                p[i * 4] = g; p[i * 4 + 1] = g; p[i * 4 + 2] = g; p[i * 4 + 3] = 255;
            }
            return p;
        }

        public static byte[] Other(byte[] reference, int disparity, int ux, int uy, int offX = 0, int offY = 0)
        {
            var p = new byte[reference.Length];
            for (int y = 0; y < Vh; y++)
                for (int x = 0; x < Vw; x++)
                {
                    // the reference pixel (rx, ry) is at (rx − disparity·ux + offX, ry − disparity·uy + offY) in this picture
                    int rx = x + disparity * ux - offX, ry = y + disparity * uy - offY;
                    if (rx < 0 || ry < 0 || rx >= Vw || ry >= Vh) continue;
                    Array.Copy(reference, (ry * Vw + rx) * 4, p, (y * Vw + x) * 4, 4);
                }
            return p;
        }

        public static float[] Plane(float disparity) { var d = new float[Vw * Vh]; Array.Fill(d, disparity); return d; }

        public static double InnerMedian(float[] cost)
        {
            var v = new List<float>();
            for (int y = 60; y < Vh - 60; y++) for (int x = 60; x < Vw - 60; x++) v.Add(cost[y * Vw + x]);
            return Median(v);
        }
    }

    [Fact]
    public void Cross_view_cost_is_low_for_the_right_depth_and_high_for_a_wrong_one()
    {
        var reference = Views.Reference();
        float[] Cost(float assumed, int offX = 0, int offY = 0)
        {
            var c = new CrossViewCost(reference, Views.Plane(assumed), Views.Vw, Views.Vh);
            c.Add(Views.Other(reference, 8, 1, 0, offX, offY), 1, 0);
            c.Add(Views.Other(reference, 8, 0, 1, offX, offY), 0, 1);
            c.Add(Views.Other(reference, 8, 0, -1), 0, -1);
            Assert.Equal(3, c.Cameras);
            return c.Result()!;
        }
        var right = Cost(8f); var wrong = Cost(30f);
        output.WriteLine($"median cost: right depth {Views.InnerMedian(right):0.00}, wrong depth {Views.InnerMedian(wrong):0.00}");
        Assert.True(Views.InnerMedian(right) < 0.5);
        Assert.True(Views.InnerMedian(wrong) > 2 * RefineConstants.CostLimitMin);
        // a camera that does not see a pixel does not count: at the left border one camera has no picture, and the cost stays low
        Assert.True(right[240 * Views.Vw + 3] < 0.5);
        // a small calibration error of two cameras is measured and removed
        Assert.True(Views.InnerMedian(Cost(8f, offX: 3, offY: -2)) < 1.0);
        // the same result every time
        Assert.True(right.AsSpan().SequenceEqual(Cost(8f)));
    }

    [Fact]
    public void Cross_view_cost_needs_two_other_cameras()
    {
        var reference = Views.Reference();
        var c = new CrossViewCost(reference, Views.Plane(8f), Views.Vw, Views.Vh);
        Assert.Null(c.Result());
        c.Add(Views.Other(reference, 8, 1, 0), 1, 0);
        Assert.Null(c.Result());
        c.Add(Views.Other(reference, 8, 0, 1), 0, 1);
        Assert.NotNull(c.Result());
    }

    /// <summary>A stand-in network: the "depth" of a pixel is its red value. It records the sizes it is run at.</summary>
    sealed class RedNetwork : IDepthNetwork
    {
        public List<(int W, int H)> Runs { get; } = new();
        public string Id => "red";
        public float[] Run(float[] planes, int width, int height)
        {
            Runs.Add((width, height));
            var o = new float[width * height];
            for (int i = 0; i < o.Length; i++) o[i] = planes[i] * RefineConstants.NetworkStd[0] + RefineConstants.NetworkMean[0];
            return o;
        }
    }

    [Fact]
    public void The_network_runs_on_the_whole_picture_and_on_tiles()
    {
        const int w = 2600, h = 1950;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // a smooth ramp with fine stripes: the whole-picture pass loses the stripes, the tiles keep part of them
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(40 + 150 * x / w + (x / 3 % 2 == 0 ? 30 : 0)); rgba[i + 1] = 90; rgba[i + 2] = 30; rgba[i + 3] = 255;
            }
        var net = new RedNetwork(); int ticks = 0;
        var maps = DepthPrior.Run(rgba, w, h, net, () => ticks++);
        Assert.Equal((686, 518), DepthPrior.WholeSize(w, h));
        Assert.Equal((686, 518), net.Runs[0]);
        Assert.All(net.Runs.Skip(1), r => Assert.Equal((RefineConstants.NetworkSide, RefineConstants.NetworkSide), r));
        Assert.Equal(new[] { 0, 777, 1554, 1564 }, DepthPrior.TileOrigins(w, RefineConstants.TileSide));
        Assert.Equal(new[] { 0, 777, 914 }, DepthPrior.TileOrigins(h, RefineConstants.TileSide));
        Assert.Equal(4 * 3, maps.Tiles);
        Assert.Equal(DepthPrior.Runs(w, h), net.Runs.Count);
        Assert.Equal(net.Runs.Count, ticks);
        Assert.Equal(25, DepthPrior.Runs(4160, 3120));
        double errWhole = 0, errTiled = 0;
        for (int y = 100; y < h - 100; y += 7)
            for (int x = 100; x < w - 100; x += 7)
            {
                float truth = rgba[(y * w + x) * 4] / 255f;
                errWhole += Math.Abs(maps.Whole[y * w + x] - truth); errTiled += Math.Abs(maps.Tiled[y * w + x] - truth);
            }
        output.WriteLine($"mean error: whole {errWhole:0.0}, tiled {errTiled:0.0}");
        Assert.True(errTiled < 0.8 * errWhole, "the tiles carry finer detail than the whole-picture pass");
    }

    [Fact]
    public void A_turn_of_a_plane_is_exactly_reversible()
    {
        const int w = 37, h = 23;
        var rng = new Random(7);
        var plane = new float[w * h];
        for (int i = 0; i < plane.Length; i++) plane[i] = BitConverter.Int32BitsToSingle(rng.Next());      // every bit pattern, also NaN
        static int[] Bits(float[] a) => a.Select(BitConverter.SingleToInt32Bits).ToArray();
        Assert.Same(plane, DepthPrior.Turn(plane, w, h, 0));
        foreach (int degrees in new[] { 90, 180, 270 })
        {
            bool swap = degrees != 180;
            var turned = DepthPrior.Turn(plane, w, h, degrees);
            var back = DepthPrior.Turn(turned, swap ? h : w, swap ? w : h, 360 - degrees);
            Assert.Equal(Bits(plane), Bits(back));
            Assert.NotEqual(Bits(plane), Bits(turned));
        }
        // clockwise: the top left corner goes to the top right, then to the bottom right, then to the bottom left
        var mark = new float[w * h]; mark[0] = 1;
        Assert.Equal(1, DepthPrior.Turn(mark, w, h, 90)[h - 1]);
        Assert.Equal(1, DepthPrior.Turn(mark, w, h, 180)[w * h - 1]);
        Assert.Equal(1, DepthPrior.Turn(mark, w, h, 270)[(w - 1) * h]);
        // four quarter turns give the plane back
        var four = plane; int fw = w, fh = h;
        for (int k = 0; k < 4; k++) { four = DepthPrior.Turn(four, fw, fh, 90); (fw, fh) = (fh, fw); }
        Assert.Equal(Bits(plane), Bits(four));
    }

    [Fact]
    public void The_tiles_of_a_turned_picture_cover_the_same_areas()
    {
        const int w = 4160, h = 3120, tile = RefineConstants.TileSide;
        var plain = (from y in DepthPrior.TileOrigins(h, tile) from x in DepthPrior.TileOrigins(w, tile) select (x, y)).OrderBy(t => t).ToArray();
        var level = DepthPrior.TurnedTileOrigins(w, h, tile, 0);
        Assert.Equal(DepthPrior.TileOrigins(w, tile), level.Xs); Assert.Equal(DepthPrior.TileOrigins(h, tile), level.Ys);
        foreach (int degrees in new[] { 90, 180, 270 })
        {
            var (xs, ys) = DepthPrior.TurnedTileOrigins(w, h, tile, degrees);
            Assert.Equal(xs.OrderBy(v => v), xs); Assert.Equal(ys.OrderBy(v => v), ys);
            // each tile of the turned picture, as its area in the picture before the turn
            var areas = (from ty in ys from tx in xs select degrees switch
            {
                90 => (ty, h - tile - tx),
                180 => (w - tile - tx, h - tile - ty),
                _ => (w - tile - ty, tx),
            }).OrderBy(t => t).ToArray();
            Assert.Equal(plain, areas);
        }
    }

    /// <summary>A stand-in network that knows where the top of its input is: the value of a pixel is its row, from 0 at the top to 1 at
    /// the bottom, plus a small part of the red value.</summary>
    sealed class RowNetwork : IDepthNetwork
    {
        public List<(int W, int H)> Runs { get; } = new();
        public string Id => "row";
        public float[] Run(float[] planes, int width, int height)
        {
            Runs.Add((width, height));
            var o = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    o[y * width + x] = (y + 0.5f) / height + 0.01f * (planes[y * width + x] * RefineConstants.NetworkStd[0] + RefineConstants.NetworkMean[0]);
            return o;
        }
    }

    [Fact]
    public void The_network_sees_a_turned_picture_upright_and_its_maps_come_back()
    {
        const int w = 1500, h = 1100;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(30 + 100 * x / w + 90 * y / h); rgba[i + 1] = 90; rgba[i + 2] = 30; rgba[i + 3] = 255;
            }
        static int[] Bits(float[] a) => a.Select(BitConverter.SingleToInt32Bits).ToArray();

        // no turn: the same result as before, bit for bit; a turn that is not known counts as no turn
        var plain = DepthPrior.Run(rgba, w, h, new RowNetwork());
        foreach (int none in new[] { 0, -1, 360 })
        {
            var same = DepthPrior.Run(rgba, w, h, new RowNetwork(), turn: none);
            Assert.Equal(Bits(plain.Whole), Bits(same.Whole)); Assert.Equal(Bits(plain.Tiled), Bits(same.Tiled));
        }

        // a network that follows the picture, not the rows: the maps of a turned run are the maps of the plain run
        var red = DepthPrior.Run(rgba, w, h, new RedNetwork());
        foreach (int degrees in new[] { 90, 180, 270 })
        {
            var net = new RowNetwork(); int ticks = 0;
            var maps = DepthPrior.Run(rgba, w, h, net, () => ticks++, turn: degrees);
            Assert.Equal(plain.Tiles, maps.Tiles);
            Assert.Equal(DepthPrior.Runs(w, h), net.Runs.Count);
            Assert.Equal(net.Runs.Count, ticks);
            // the whole-picture pass runs at the size of the upright picture
            var (gw, gh) = DepthPrior.WholeSize(w, h);
            Assert.Equal(degrees == 180 ? (gw, gh) : (gh, gw), net.Runs[0]);
            Assert.Equal(w * h, maps.Whole.Length); Assert.Equal(w * h, maps.Tiled.Length);
            // the top of the upright picture is the left (90), the bottom (180) or the right (270) of the stored picture
            double worst = 0;
            for (int y = 50; y < h - 50; y += 11)
                for (int x = 50; x < w - 50; x += 11)
                {
                    double row = degrees switch { 90 => (x + 0.5) / w, 180 => 1 - (y + 0.5) / h, _ => 1 - (x + 0.5) / w };
                    double want = row + 0.01 * rgba[(y * w + x) * 4] / 255.0;
                    worst = Math.Max(worst, Math.Max(Math.Abs(maps.Whole[y * w + x] - want), Math.Abs(maps.Tiled[y * w + x] - want)));
                }
            output.WriteLine($"turn {degrees}: largest difference from the upright rows {worst:0.00000}");
            Assert.True(worst < 1e-3, $"turn {degrees}: {worst}");

            var turned = DepthPrior.Run(rgba, w, h, new RedNetwork(), turn: degrees);
            double diff = 0;
            for (int i = 0; i < w * h; i++) diff = Math.Max(diff, Math.Max(Math.Abs(turned.Whole[i] - red.Whole[i]), Math.Abs(turned.Tiled[i] - red.Tiled[i])));
            output.WriteLine($"turn {degrees}: largest difference of a network that follows the picture {diff:0.0000000}");
            Assert.True(diff < 1e-4, $"turn {degrees}: {diff}");
        }
    }

    // ================================================================================================================ the app on the fake library

    static DepthModelViewModel ModelVm(Harness h) => h.Vm.Settings!.DepthModel;

    [AvaloniaFact]
    public async Task Settings_section_downloads_cancels_and_removes()
    {
        await using var h = await Harness.CreateAsync();
        var model = h.Lib.FakeDepthModel; model.DownloadMs = 600;
        h.Vm.CurrentPage = Page.Settings;
        await h.SettleAsync(() => h.Vm.Settings?.Folders.Count > 0);
        var vm = ModelVm(h);
        var view = h.Window.GetVisualDescendants().OfType<SettingsView>().First();
        var section = view.FindControl<Border>("DepthModelSection")!;
        section.BringIntoView();
        await h.SettleAsync();
        Assert.Equal("Not installed.", vm.StateText);
        Assert.Equal("Download (about 107 MB)", vm.DownloadLabel);
        Assert.True(vm.CanDownload); Assert.False(vm.CanRemove); Assert.False(vm.IsDownloading);
        Assert.Contains("Depth Anything V2 Small (Apache-2.0)", vm.DescriptionText);
        Assert.Contains("ONNX Runtime (MIT)", vm.DescriptionText);
        Assert.Contains("not part of Lux", vm.DescriptionText);
        Assert.EndsWith(Path.Combine("models", "depth"), vm.FolderText);
        Assert.True(view.FindControl<Button>("DepthModelDownload")!.IsVisible);
        Assert.False(view.FindControl<Button>("DepthModelRemove")!.IsVisible);
        h.Shot("refined-01-settings-not-installed");

        // download, cancel in the middle
        var run = vm.DownloadCommand.ExecuteAsync(null);
        await h.SettleAsync(() => vm.IsDownloading && vm.Progress > 0);
        Assert.StartsWith("Downloading · ", vm.StateText);
        Assert.True(view.FindControl<Button>("DepthModelCancel")!.IsVisible);
        h.Shot("refined-02-settings-downloading");
        vm.CancelDownloadCommand.Execute(null);
        await run;
        await h.SettleAsync(() => !vm.IsDownloading);
        Assert.Equal("Not installed.", vm.StateText);

        // a failed download says why, and can be tried again
        model.FailWith = "The download failed: the connection closed.";
        await vm.DownloadCommand.ExecuteAsync(null);
        await h.SettleAsync(() => vm.StateText.StartsWith("The download failed"));
        Assert.True(vm.CanDownload);
        model.FailWith = null;
        await vm.DownloadCommand.ExecuteAsync(null);
        await h.SettleAsync(() => vm.CanRemove && !vm.CanDownload);
        Assert.Equal("Installed · Depth Anything V2 Small, ONNX Runtime 1.23.2 · 121 MB", vm.StateText);
        h.Shot("refined-03-settings-installed");

        // remove
        await vm.RemoveCommand.ExecuteAsync(null);
        await h.SettleAsync(() => vm.CanDownload);
        Assert.Equal("Not installed.", vm.StateText);

        // a computer that cannot run the model: the reason, and no buttons
        model.Set(new DepthModelStatus(DepthModelState.NotAvailable, "Refined depth is not available on this computer: there is no inference runtime for linux-riscv64."));
        await h.SettleAsync(() => !vm.CanDownload);
        Assert.Contains("not available on this computer", vm.StateText);
        Assert.False(vm.CanDownload); Assert.False(vm.CanRemove);
        Assert.False(view.FindControl<Button>("DepthModelDownload")!.IsVisible);
        h.Shot("refined-04-settings-not-available");
    }

    [AvaloniaFact]
    public async Task Refine_depth_asks_once_for_the_download_and_then_makes_the_version()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var model = h.Lib.FakeDepthModel; model.DownloadMs = 300;
        var d = h.DepthVm();
        await h.SettleDepthAsync(() => d.IsReady);
        Assert.True(d.CanRefineDepth);
        Assert.True(h.DevelopView().FindControl<Button>("RefineDepthButton")!.IsVisible);
        var before = d.Versions.Select(v => v.Name).ToArray();

        // the first use asks; "Cancel" changes nothing and downloads nothing
        _ = d.RefineDepthCommand.ExecuteAsync(null);
        var ask = Assert.IsType<ConfirmViewModel>(h.Vm.Dialog);
        Assert.Equal("Refined depth needs a one-time download of about 107 MB. Download now?", ask.Text);
        Assert.Equal("Download", ask.OkLabel);
        await h.SettleAsync();
        h.Shot("refined-05-develop-first-use");
        ask.CancelCommand.Execute(null);
        await h.SettleAsync();
        Assert.Null(h.Vm.Dialog);
        Assert.Equal(0, model.Downloads);
        Assert.Equal(before, d.Versions.Select(v => v.Name));

        // "Download": the download runs with its progress on the page, then the refinement runs as a depth job
        _ = d.RefineDepthCommand.ExecuteAsync(null);
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => d.IsModelDownloading);
        Assert.StartsWith("Downloading the depth model · ", d.ModelDownloadText);
        Assert.True(h.DevelopView().FindControl<Grid>("ModelDownloadRow")!.IsVisible);
        await h.SettleDepthAsync(() => d.Versions.Any(v => v.Info.Kind == VersionKind.Refined), timeoutMs: 20000);
        Assert.Equal(1, model.Downloads);
        Assert.Equal(DepthModelState.Installed, model.Status.State);
        Assert.Equal("Refined", d.CurrentVersionName);
        Assert.Equal(before.Append("Refined"), d.Versions.Select(v => v.Name));
        Assert.Contains("depth network", d.Versions.Last().Tip);
        Assert.False(d.CanRefineDepth);                                                  // made once: the version list switches to it from now on
        Assert.False(d.IsModelDownloading);
        h.Shot("refined-06-develop-refined-version");

        // the refined version is a version like the others: switch away and back
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Clean"));
        await h.SettleDepthAsync(() => d.CurrentVersionName == "Clean");
        await d.SwitchVersionCommand.ExecuteAsync(d.Versions.First(v => v.Name == "Refined"));
        await h.SettleDepthAsync(() => d.CurrentVersionName == "Refined");

        // another photo: the files are there, so nothing is asked
        var other = h.Library.Items.First(i => i.Name != "L16_00493" && !i.HasProblem && i.Item.CacheLevel >= Lux.Engine.Session.CacheLevel.Geometry);
        await h.OpenItemAsync(other);
        await h.SettleDepthAsync(() => d.IsReady);
        Assert.True(d.CanRefineDepth);
        var job = d.RefineDepthCommand.ExecuteAsync(null);
        Assert.Null(h.Vm.Dialog);
        await h.SettleDepthAsync(() => d.Versions.Any(v => v.Info.Kind == VersionKind.Refined), timeoutMs: 20000);
        await job;
        Assert.Equal(1, model.Downloads);
        Assert.Equal("Refined", d.CurrentVersionName);
    }

    [AvaloniaFact]
    public async Task Refine_depth_reports_a_failed_download_and_a_computer_without_a_runtime()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var model = h.Lib.FakeDepthModel; model.DownloadMs = 100;
        var d = h.DepthVm();
        await h.SettleDepthAsync(() => d.IsReady);
        model.FailWith = "The download failed: the server answered 503 for model.onnx";
        _ = d.RefineDepthCommand.ExecuteAsync(null);
        ((ConfirmViewModel)h.Vm.Dialog!).OkCommand.Execute(null);
        await h.SettleAsync(() => d.HasMessage);
        Assert.Contains("503", d.MessageText);
        Assert.DoesNotContain(d.Versions, v => v.Info.Kind == VersionKind.Refined);
        Assert.True(d.CanRefineDepth);                                                   // it can be tried again

        model.Set(new DepthModelStatus(DepthModelState.NotAvailable, "Refined depth is not available on this computer: there is no inference runtime for linux-riscv64."));
        await h.SettleAsync(() => !d.CanRefineDepth);
        Assert.False(h.DevelopView().FindControl<Button>("RefineDepthButton")!.IsVisible);
    }

    // ================================================================================================================ a real capture

    /// <summary>The real network on a real capture, through the library: "Refine depth" makes the version, the version is stored and comes
    /// back when the photo is opened again, and `lux-light` exports the same depth from the stored version as it computes with
    /// `--depth-refine`. Everything is written under the UI output folder; the capture is read through a link.</summary>
    [Fact]
    public async Task Real_capture_refines_stores_and_exports_the_same_depth()
    {
        string modelFolder = Environment.GetEnvironmentVariable("LUX_TEST_DEPTH_MODEL") ?? "";
        string lri = Path.Combine(Paths.Captures, "L16_00493.lri");
        if (modelFolder.Length == 0 || !Directory.Exists(modelFolder)) { output.WriteLine("LUX_TEST_DEPTH_MODEL names no folder with the depth model: skipped"); return; }
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { output.WriteLine("LUX_TEST_CAPTURES has no L16_00493.lri: skipped"); return; }

        string root = Path.Combine(Path.GetFullPath(Paths.UiScratch), "refined-real");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);            // links are removed, never followed
        string folder = Path.Combine(root, "lib"), appData = Path.Combine(root, "appdata"), outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(appData, "models")); Directory.CreateDirectory(outDir);
        File.CreateSymbolicLink(Path.Combine(folder, "L16_00493.lri"), lri);
        Directory.CreateSymbolicLink(Path.Combine(appData, "models", "depth"), Path.GetFullPath(modelFolder));   // where `lux-light` looks

        var sw = Stopwatch.StartNew();
        string id;
        await using (var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(appData, "library.luxlib"), DepthModelFolder = modelFolder, MakeThumbnailsAfterScan = false }))
        {
            Assert.Equal(DepthModelState.Installed, lib.DepthModel.Status.State);
            await lib.AddFolderAsync(folder);
            CaptureItem? item = null;
            for (int i = 0; i < 600 && (item = lib.Query(new CaptureQuery()).Items.FirstOrDefault()) is null; i++) await Task.Delay(100);
            Assert.NotNull(item);
            id = item!.Id;
            await using var photo = await lib.OpenPhotoAsync(id);
            await photo.Depth.EnsureDepthAsync();
            output.WriteLine($"depth ready after {sw.Elapsed.TotalSeconds:0} s");
            // the depth under a grid of picture points, before and after
            float?[] Grid() => Enumerable.Range(0, 400).Select(k => photo.Depth.DepthAt(new NormPoint((k % 20 + 0.5) / 20, (k / 20 + 0.5) / 20))).ToArray();
            var before = Grid();
            sw.Restart();
            var r = await photo.DepthEdit.RefineDepth().Completion;
            output.WriteLine($"refine: {r.Outcome} {r.Message} in {sw.Elapsed.TotalSeconds:0.0} s");
            Assert.Equal(DepthJobOutcome.Done, r.Outcome);
            var active = photo.DepthEdit.ActiveVersion;
            Assert.Equal(DepthVersionKind.Refined, active.Kind);
            Assert.Equal("Refined", active.Name);
            Assert.Equal(0, active.EditCount);
            var after = Grid();
            int kept = Enumerable.Range(0, 400).Count(k => before[k] == after[k]);
            int toSky = Enumerable.Range(0, 400).Count(k => before[k] < 80000f && after[k] > 95000f);
            output.WriteLine($"of 400 points: {kept} keep their depth, {toSky} went to the far limit");
            Assert.True(kept > 200, "most of the picture keeps the camera's depth");
            Assert.True(toSky >= 5, "sky that the camera had nearer is at the far limit");
            // made once: a second request changes nothing; the clean depth is still there
            Assert.Equal(DepthJobOutcome.NothingChanged, (await photo.DepthEdit.RefineDepth().Completion).Outcome);
            Assert.Equal(DepthJobOutcome.Done, (await photo.DepthEdit.SwitchVersion("clean").Completion).Outcome);
            Assert.Equal(before, Grid());
            Assert.Equal(DepthJobOutcome.Done, (await photo.DepthEdit.RefineDepth().Completion).Outcome);   // switches back
            Assert.Equal(DepthVersionKind.Refined, photo.DepthEdit.ActiveVersion.Kind);
            Assert.Equal(after, Grid());
        }

        // the command line: the stored version, the on-the-fly refinement and the camera's own depth
        string Depth(string name, params string[] args)
        {
            string dir = Path.Combine(outDir, name);
            var psi = new ProcessStartInfo(Paths.LuxLight) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "convert", Path.Combine(folder, "L16_00493.lri"), "--formats", "depth", "--level", "1", "-o", dir }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["LUX_APP_DATA"] = appData; psi.Environment["LUX_FORBID_DEFAULT_APP_DATA"] = "1";
            psi.Environment[DepthModelManifest.SourceVariable] = Path.Combine(root, "no-source");
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            output.WriteLine($"-- {name}: exit {p.ExitCode}\n{string.Join("\n", text.Split('\n').Where(l => l.Contains("depth") || l.Contains("error")))}");
            Assert.Equal(0, p.ExitCode);
            return Path.Combine(dir, "L16_00493_depth.f32");
        }
        byte[] stored = File.ReadAllBytes(Depth("stored"));
        byte[] fresh = File.ReadAllBytes(Depth("fresh", "--no-edits", "--depth-refine"));
        byte[] camera = File.ReadAllBytes(Depth("camera", "--no-edits"));
        byte[] both = File.ReadAllBytes(Depth("both", "--depth-refine"));
        Assert.True(stored.AsSpan().SequenceEqual(fresh), "the stored refined version and a fresh refinement give the same depth");
        Assert.True(stored.AsSpan().SequenceEqual(both), "with the refined version active, --depth-refine uses it as saved");
        Assert.False(stored.AsSpan().SequenceEqual(camera));
        // most of the depth map is the camera's, bit for bit
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(stored.AsSpan(16)); var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(camera.AsSpan(16));
        long same = 0; for (int i = 0; i < a.Length; i++) if (BitConverter.SingleToInt32Bits(a[i]) == BitConverter.SingleToInt32Bits(b[i])) same++;
        output.WriteLine($"{100.0 * same / a.Length:0.0} % of the exported depth is the camera's, bit for bit");
        Assert.InRange((double)same / a.Length, 0.5, 0.99);
    }
}
