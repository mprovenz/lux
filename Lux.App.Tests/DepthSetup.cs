using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Lux.App.Fake;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.Library;

namespace Lux.App.Tests;

/// <summary>Helpers for the depth interface on the fake: the fake's depth session, element positions of the fake scene, real pointer strokes on
/// the viewer at picture coordinates, keys.</summary>
public static class DepthHarness
{
    public static FakeDepthSession FakeDepth(this Harness h) => h.Session().FakeDepth;
    public static DepthViewModel DepthVm(this Harness h) => h.Develop().Depth;

    /// <summary>The center of an element of the fake scene (house, windows, star, sign, a tree) in the picture as shown; with an offset in
    /// element radii (dx, dy).</summary>
    internal static NormPoint Element(this Harness h, byte id, double dx = 0, double dy = 0)
    {
        var map = h.FakeDepth().Map ?? throw new InvalidOperationException("the depth map is not made");
        var e = map.Elements[id];
        var capture = new NormPoint((e.X + dx * e.Rx) / map.W, (e.Y + dy * e.Ry) / map.H);
        return h.Session().FakeTiles.CaptureToPicture(capture);
    }

    /// <summary>A point of the sky (no depth) near the top left.</summary>
    internal static NormPoint Sky(this Harness h)
    {
        var map = h.FakeDepth().Map!;
        for (int y = 2; y < map.H / 3; y++)
            for (int x = 2; x < map.W / 2; x++)
                if (map.IsSky(y * map.W + x) && map.IsSky((y + 3) * map.W + x + 3) && map.IsSky(Math.Max(0, y - 2) * map.W + x)
                    && float.IsInfinity(map.Base[y * map.W + x]))
                    return h.Session().FakeTiles.CaptureToPicture(new NormPoint((x + 1.5) / map.W, (y + 1.5) / map.H));
        return new NormPoint(0.05, 0.05);
    }

    /// <summary>A picture point in window coordinates (where the pointer has to be).</summary>
    public static Point At(this Harness h, NormPoint p, PhotoViewer? viewer = null)
    {
        var v = viewer ?? h.Viewer();
        var t = v.Transform() ?? throw new InvalidOperationException("no picture");
        return v.TranslatePoint(t.FrameToView(new Point(p.X, p.Y)), h.Window)!.Value;
    }

    /// <summary>A stroke with real pointer events through <paramref name="points"/> (interpolated in <paramref name="steps"/> moves per segment);
    /// with <paramref name="release"/> false the button stays down. Returns the longest move (ms).</summary>
    public static double Stroke(this Harness h, IReadOnlyList<NormPoint> points, int steps = 6, bool release = true, RawInputModifiers mods = RawInputModifiers.None)
    {
        double worst = 0;
        var first = h.At(points[0]);
        h.Window.MouseMove(first, mods);
        h.Window.MouseDown(first, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i < points.Count; i++)
            for (int k = 1; k <= steps; k++)
            {
                var a = points[i - 1]; var b = points[i];
                var p = new NormPoint(a.X + (b.X - a.X) * k / steps, a.Y + (b.Y - a.Y) * k / steps);
                var sw = Stopwatch.StartNew();
                h.Window.MouseMove(h.At(p), mods | RawInputModifiers.LeftMouseButton);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                Dispatcher.UIThread.RunJobs();
            }
        if (release)
        {
            h.Window.MouseUp(h.At(points[^1]), MouseButton.Left, mods);
            Dispatcher.UIThread.RunJobs();
        }
        return worst;
    }

    public static void Click(this Harness h, NormPoint p, RawInputModifiers mods = RawInputModifiers.None, PhotoViewer? viewer = null)
    {
        var at = h.At(p, viewer);
        h.Window.MouseMove(at, mods);
        h.Window.MouseDown(at, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
        h.Window.MouseUp(at, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Hover(this Harness h, NormPoint p, PhotoViewer? viewer = null)
    {
        h.Window.MouseMove(h.At(p, viewer));
        Dispatcher.UIThread.RunJobs();
    }

    public static void Key(this Harness h, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        h.Viewer().Focus();
        h.Window.KeyPress(key, mods, PhysicalKey.None, null);
        h.Window.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Let a depth job finish (the fake's tool times are scaled down in tests) and the picture settle.</summary>
    public static async Task SettleDepthAsync(this Harness h, Func<bool>? until = null, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await h.SettleAsync(timeoutMs: 120, waitThumbnails: false);
            bool ok = h.Develop().Session?.Depth is not { Activity.Running: true } && (until?.Invoke() ?? true);
            if (ok) break;
            await Task.Delay(15);
        }
        await h.SettlePictureAsync();
    }

    /// <summary>The first capture of the fake library that matches.</summary>
    public static CaptureItemViewModel? Find(this Harness h, Func<CaptureItem, bool> match) => h.Library.Items.FirstOrDefault(i => match(i.Item) && !i.HasProblem);

    /// <summary>Open a capture of the current scope in Develop and wait for its picture.</summary>
    public static async Task OpenItemAsync(this Harness h, CaptureItemViewModel item)
    {
        h.Library.Click(item, false, false);
        h.Vm.OpenInDevelop(item);
        await h.SettleAsync(() => h.Develop().Session != null, waitThumbnails: false);
        await h.SettlePictureAsync();
    }
}
