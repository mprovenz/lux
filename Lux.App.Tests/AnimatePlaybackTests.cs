using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The Animate panel's preview player on the fake library:
/// it waits for every frame and shows the progress, it plays only frames of the final preview quality, every preview plays by itself,
/// playback follows a clock, and a view mode closes the panel. Screenshots and the pacing numbers go to the UI output folder.
/// </summary>
public class AnimatePlaybackTests(ITestOutputHelper output)
{
    public static string OutDir
    {
        get { var d = Path.Combine(Path.GetFullPath(Paths.UiScratch), "animate-playback"); Directory.CreateDirectory(d); return d; }
    }
    static string Shot(Harness h, string name) => Harness.Capture(h.Window, name, Path.Combine(OutDir, "shots"));

    static async Task<Harness> OneSelectedAsync(int frameMs)
    {
        var h = await Harness.CreateAsync();
        h.Lib.FakeFrameMs = frameMs;
        await h.SelectScopeAsync(h.Library.Tree.First(n => n.Label == "Autumn").Key);
        h.Library.Click(h.Item("L16_00493"), false, false);
        await h.SettleAsync();
        return h;
    }

    /// <summary>Run the dispatcher and render frames until <paramref name="until"/> holds. <paramref name="each"/> runs after every pass.</summary>
    static async Task<bool> PumpAsync(Harness h, Func<bool> until, int ms, Action? each = null)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < ms)
        {
            await h.Lib.FlushEventsAsync();
            Dispatcher.UIThread.RunJobs();
            h.Window.RenderFrame();
            each?.Invoke();
            if (until()) return true;
            await Task.Delay(5);
        }
        return until();
    }

    static T Visible<T>(Harness h) where T : Control => h.Window.GetVisualDescendants().OfType<T>().First(v => v.IsEffectivelyVisible);
    static ProgressBar LoadingBar(Harness h) => h.Window.GetVisualDescendants().OfType<AnimationPlayer>().First(v => v.IsEffectivelyVisible).GetVisualDescendants().OfType<ProgressBar>().First(b => b.Name == "LoadingBar");

    /// <summary>The player moves by itself: its position changes while the dispatcher runs and frames render.</summary>
    static async Task<bool> PlaysAsync(Harness h, AnimationPlayerViewModel pl, int ms = 5000)
    {
        int at = pl.Position;
        return await PumpAsync(h, () => pl.Position != at, ms);
    }

    // ---------------------------------------------------------------------------------------------------------------- 1. wait for all frames

    /// <summary>While frames are made the player shows one still picture with "Frame n of N" and a progress bar, and does not move. The
    /// count follows the frames that are made, in any order. When the last frame is in, it plays from the first frame by itself. A changed
    /// option starts again: the old preview stops, and only frames of the new preview play.</summary>
    [AvaloniaFact]
    public async Task Player_waits_for_every_frame_shows_progress_and_then_plays_from_the_start()
    {
        await using var h = await OneSelectedAsync(frameMs: 60);
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        await PumpAsync(h, () => pl.Preview?.IsPlanned == true, 5000);
        var first = pl.Preview!;
        var bar = LoadingBar(h);
        var view = Visible<AnimationFrameView>(h);
        var counts = new List<int>(); var fractions = new List<double>();
        int moved = 0, ranEarly = 0, madeOutOfOrder = 0; bool shot = false;
        var stills = new HashSet<Bitmap>();
        void Check()
        {
            if (pl.IsReady) return;
            if (pl.Position != 0) moved++;
            if (pl.IsRunning) ranEarly++;
            if (!ReferenceEquals(pl.Preview, first) || !first.IsPlanned) return;
            if (pl.Frame is { } f) stills.Add(f);
            var m = System.Text.RegularExpressions.Regex.Match(pl.LoadingText, @"^Frame (\d+) of 24$");
            if (!m.Success) return;
            int n = int.Parse(m.Groups[1].Value);
            double fraction = pl.LoadingFraction;
            // the text and the fraction are read one after the other while frames arrive: the fraction is the text's count or a little more
            Assert.InRange(fraction, n / 24.0 - 1e-6, (n + 3) / 24.0);
            counts.Add(n); fractions.Add(fraction);
            // frames are not made first to last: a later frame is ready while an earlier one is not
            if (pl.IsReadyAt(23) && !pl.IsReadyAt(1)) madeOutOfOrder++;
            if (!shot && n >= 9 && pl.HasFrame)
            {
                shot = true;
                Assert.True(pl.IsLoading);
                Assert.True(bar.IsEffectivelyVisible);
                Assert.InRange(bar.Value, (n - 3) / 24.0, (n + 3) / 24.0);
                Assert.StartsWith("Frame ", view.PreparingDrawn);
                Assert.False(Visible<Slider>(h).IsEnabled);
                Shot(h, "01-progress-frames");
            }
        }
        Assert.True(await PumpAsync(h, () => pl.IsReady, 30_000, Check));
        Assert.True(shot, "the progress state was never on screen");
        Assert.Equal(0, moved);
        Assert.Equal(0, ranEarly);
        Assert.True(madeOutOfOrder > 0, "the test expects frames that arrive out of order");
        Assert.True(counts.Count > 5 && counts.SequenceEqual(counts.OrderBy(x => x)), "the frame count went backward");
        Assert.True(fractions.SequenceEqual(fractions.OrderBy(x => x)));
        Assert.True(stills.Count <= 1, $"{stills.Count} different pictures were shown while the frames were made");   // one still, never a sequence
        // ready: the first frame, the bar gone, and it plays by itself
        Assert.Equal(0, pl.Position);
        Assert.True(pl.IsRunning);
        Assert.False(pl.IsLoading);
        Assert.Equal("", pl.LoadingText);
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsEffectivelyVisible);
        Assert.Null(view.Preparing is { Length: > 0 } ? view.Preparing : null);
        Assert.True(await PlaysAsync(h, pl));
        Shot(h, "02-playing");

        // a changed option: the player goes back to the progress state. A second change while those frames are made stops that preview:
        // none of its frames play. Only the frames of the last preview play.
        p.FNumber = 4.0;
        Assert.True(await PumpAsync(h, () => !ReferenceEquals(pl.Preview, first), 5000));
        var second = pl.Preview!;
        Assert.Equal(4.0f, second.Options.FNumber);
        Assert.False(pl.IsReady);
        Assert.False(pl.IsRunning);
        Assert.Equal(0, pl.Position);
        Assert.True(pl.IsLoading);
        moved = 0; ranEarly = 0;
        void Still() { if (!pl.IsReady) { if (pl.Position != 0) moved++; if (pl.IsRunning) ranEarly++; } }
        Assert.True(await PumpAsync(h, () => ReferenceEquals(pl.Preview, second) && pl.LoadingText.StartsWith("Frame 6 ", StringComparison.Ordinal), 10_000, Still));
        Assert.False(pl.IsUpdating);                                   // the still picture is the new preview's first frame
        p.FNumber = 5.6;
        Assert.True(await PumpAsync(h, () => !ReferenceEquals(pl.Preview, second) && pl.IsReady, 30_000, Still));
        Assert.Equal(0, moved);
        Assert.Equal(0, ranEarly);
        Assert.Equal("Stopped", second.Status);
        Assert.False(second.IsFinal);
        Assert.Equal(5.6f, pl.Preview!.Options.FNumber);
        Assert.True(await PlaysAsync(h, pl));
        pl.IsPlaying = false;
        for (int i = 0; i <= pl.MaxPosition; i++)
        {
            pl.Position = i;
            Assert.StartsWith("f/5.6 ", pl.FrameLabel);                // never a frame of the f/2 or the f/4 preview
        }
        h.Library.CloseAnimate();
    }

    // ---------------------------------------------------------------------------------------------------------------- 2. final quality only

    /// <summary>A preview with a second, sharper pass never plays its quick frames. One still picture shows under the progress until the
    /// last sharp frame is in. The frames that play are the sharp ones. The preview never reports "complete and not sharpening" between
    /// its two passes.</summary>
    [AvaloniaFact]
    public async Task Playback_uses_only_frames_of_the_final_preview_quality()
    {
        await using var h = await OneSelectedAsync(frameMs: 25);
        h.Lib.FakeSharpPass = true;
        h.Lib.FakeSharpSetUpMs = 400;
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        p.Size = p.Sizes.First(x => x.Value == 1040);
        Assert.True(await PumpAsync(h, () => pl.Preview is { IsPlanned: true } q && q.Options.LongEdge == 1040, 10_000) || pl.Preview!.Options.LongEdge == 1040);
        var preview = pl.Preview!;
        int gap = 0;   // "complete" seen without "sharpening" before the preview is final: the player would take quick frames for final
        preview.Changed += (_, _) => { if (preview.IsComplete && !preview.IsSharpening && !preview.IsFinal) Interlocked.Increment(ref gap); };
        var texts = new List<string>();
        int moved = 0, ranEarly = 0, quickComplete = 0; bool shot = false;
        void Check()
        {
            if (pl.IsReady || !ReferenceEquals(pl.Preview, preview)) return;
            if (pl.Position != 0) moved++;
            if (pl.IsRunning) ranEarly++;
            if (preview.IsComplete && !preview.IsFinal) quickComplete++;
            if (texts.Count == 0 || texts[^1] != pl.LoadingText) texts.Add(pl.LoadingText);
            if (!shot && pl.LoadingText.EndsWith("final pass", StringComparison.Ordinal) && pl.LoadingFraction > 0.5) { shot = true; Shot(h, "03-progress-final-pass"); }
        }
        Assert.True(await PumpAsync(h, () => pl.IsReady, 60_000, Check));
        Assert.Equal(0, moved);
        Assert.Equal(0, ranEarly);
        Assert.Equal(0, gap);
        Assert.True(quickComplete > 3, "the quick frames were complete for a while, and the player waited");
        Assert.Contains(texts, t => t.EndsWith("· first pass", StringComparison.Ordinal));
        Assert.Contains("Preparing the final pass", texts);
        Assert.Contains(texts, t => t.EndsWith("· final pass", StringComparison.Ordinal));
        output.WriteLine("progress texts: " + string.Join(" | ", texts.Where((t, i) => i % 6 == 0)));
        // the frames that play are the sharp ones (the fake's quick frames are 520 pixels wide, its sharp ones 1040)
        Assert.True(preview.IsFinal);
        for (int i = 0; i < preview.FrameCount; i++) Assert.Equal(1040, Math.Max(preview.FinalFrame(i)!.Width, preview.FinalFrame(i)!.Height));
        var view = Visible<AnimationFrameView>(h);
        var shown = pl.DisplayFrameSize;
        output.WriteLine($"final frames 1040 px, made for the screen at {shown.W} × {shown.H}, drawn at {view.PictureRect.Width:0} × {view.PictureRect.Height:0}");
        Assert.True(Math.Max(shown.W, shown.H) >= Math.Min(1040, (int)Math.Max(view.PictureRect.Width, view.PictureRect.Height) - 2), "the frames on screen are not the sharp ones");
        Assert.True(await PlaysAsync(h, pl));
        Assert.Null(p.PreviewDetailNote);   // the frames have the file's size: nothing to say
        Shot(h, "04-playing-final-quality");

        // the zoom-through: its second pass makes the full-size picture first. That step has no count: a spinner and a bar without a value.
        h.Lib.FakeSharpSetUpMs = 1500;
        p.Effects.First(e => e.Effect == AnimationEffect.ZoomThrough).SelectCommand.Execute(null);
        Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == AnimationEffect.ZoomThrough && pl.LoadingText.StartsWith("Making the full-size picture", StringComparison.Ordinal), 20_000));
        Assert.True(pl.IsLoadingIndeterminate);
        Assert.StartsWith("Making the full-size picture", pl.LoadingText);
        // no count: the spinner and the text alone, without the bar
        await PumpAsync(h, () => !LoadingBar(h).IsVisible && Visible<AnimationFrameView>(h).PreparingDrawn?.StartsWith("Making", StringComparison.Ordinal) == true, 1000);
        Assert.False(LoadingBar(h).IsVisible);
        Assert.StartsWith("Making the full-size picture", Visible<AnimationFrameView>(h).PreparingDrawn);
        Assert.False(pl.IsRunning);
        Assert.Equal(0, pl.Position);
        await PumpAsync(h, () => false, 150);
        Shot(h, "05-progress-full-size-picture");
        var zoom = pl.Preview!;
        Assert.True(await PumpAsync(h, () => pl.IsReady, 60_000, () => { if (!pl.IsReady && (pl.Position != 0 || pl.IsRunning)) moved++; }));
        Assert.Equal(0, moved);
        Assert.True(zoom.IsFinal);
        Assert.True(await PlaysAsync(h, pl));

        // a size at or below 600 pixels has no second pass: the quick frames are the final ones, and the preview is made again for it
        p.Effects.First(e => e.Effect == AnimationEffect.RackFocus).SelectCommand.Execute(null);
        Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == AnimationEffect.RackFocus && pl.IsReady, 60_000));
        var big = pl.Preview!;
        p.Size = p.Sizes.First(x => x.Value == 520);
        Assert.True(await PumpAsync(h, () => !ReferenceEquals(pl.Preview, big) && pl.IsReady, 60_000));
        Assert.Equal(520, Math.Max(pl.Preview!.FinalFrame(0)!.Width, pl.Preview.FinalFrame(0)!.Height));
        Assert.False(pl.Preview.IsSharpening);
        h.Library.CloseAnimate();
    }

    // ---------------------------------------------------------------------------------------------------------------- 3. always plays by itself

    /// <summary>Every effect plays by itself after it is chosen: on the first visit, on a second visit (its frames are kept), after the
    /// user paused or stepped the one before, after a target was picked or the pick was canceled, and when the panel opens again.</summary>
    [AvaloniaFact]
    public async Task Every_effect_plays_by_itself_after_a_switch()
    {
        await using var h = await OneSelectedAsync(frameMs: 1);
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        Assert.True(await PumpAsync(h, () => pl.IsReady, 30_000));
        Assert.True(await PlaysAsync(h, pl), "the first preview does not play");
        var effects = p.Effects.Where(e => e.IsAvailable).Select(e => e.Effect).ToList();
        Assert.Equal(AnimationEffects.All.Length, effects.Count);
        int round = 0;
        foreach (var pass in new[] { "first visit", "after a pause", "after a step", "second visit" })
        {
            foreach (var effect in round % 2 == 0 ? effects : Enumerable.Reverse(effects))
            {
                if (pl.Preview?.Options.Effect == effect) continue;
                if (pass == "after a pause") pl.TogglePlayCommand.Execute(null);
                if (pass == "after a step") pl.StepForwardCommand.Execute(null);
                var before = pl.Preview;
                p.Effects.First(e => e.Effect == effect).SelectCommand.Execute(null);
                Assert.True(await PumpAsync(h, () => !ReferenceEquals(pl.Preview, before) && pl.Preview?.Options.Effect == effect && pl.IsReady, 30_000), $"{effect} ({pass}): never ready");
                Assert.True(pl.IsPlaying, $"{effect} ({pass}): not set to play");
                Assert.True(pl.IsRunning, $"{effect} ({pass}): not running");
                Assert.True(await PlaysAsync(h, pl), $"{effect} ({pass}): does not move");
                Assert.Equal(AnimationEffects.NameOf(effect), p.Selected.Name);
            }
            round++;
        }

        // a target pick holds the first frame; the animation plays on after the pick and after a canceled pick
        p.Effects.First(e => e.Effect == AnimationEffect.ZoomThrough).SelectCommand.Execute(null);
        Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == AnimationEffect.ZoomThrough && pl.IsReady, 30_000));
        Assert.True(await PlaysAsync(h, pl));
        p.PickTargetPointCommand.Execute(null);
        Assert.True(pl.HoldAtStart);
        Assert.Equal(0, pl.Position);
        Assert.False(await PumpAsync(h, () => pl.Position != 0, 400));
        p.PickTargetPointCommand.Execute(null);                       // canceled
        Assert.False(pl.HoldAtStart);
        Assert.True(await PlaysAsync(h, pl), "a canceled pick left the player stopped");
        p.PickTargetPointCommand.Execute(null);
        var old = pl.Preview;
        p.ClickPicture(0.3, 0.6);
        Assert.NotNull(p.Target);
        Assert.True(await PumpAsync(h, () => !ReferenceEquals(pl.Preview, old) && pl.IsReady, 30_000));
        Assert.True(await PlaysAsync(h, pl), "the preview with the picked target does not play");
        // the next effect after a pick
        p.Effects.First(e => e.Effect == AnimationEffect.Fog).SelectCommand.Execute(null);
        Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == AnimationEffect.Fog && pl.IsReady, 30_000));
        Assert.True(await PlaysAsync(h, pl), "the effect chosen after a pick does not play");

        // the panel closed while paused, and opened again
        pl.IsPlaying = false;
        h.Library.CloseAnimate();
        h.Library.OpenAnimate();
        var p2 = h.Library.AnimatePanel!;
        Assert.NotSame(p, p2);
        Assert.True(await PumpAsync(h, () => p2.Player.IsReady, 30_000));
        Assert.True(await PlaysAsync(h, p2.Player), "the panel opened again does not play");
        h.Library.CloseAnimate();
    }

    // ---------------------------------------------------------------------------------------------------------------- 4. smooth playback

    /// <summary>Play for a time in a tight loop (the dispatcher's jobs, then a rendered frame) and report the time between frame changes
    /// against the frames' own times, the time of the two parts of a pass, and what was allocated.</summary>
    public static async Task<string> PaceAsync(AnimationPlayerViewModel pl, Action jobs, Action render, int ms)
    {
        pl.IsPlaying = true;
        var delays = pl.Preview!.DelaysMs;
        var sw = Stopwatch.StartNew();
        var gaps = new List<double>(); var errs = new List<double>(); var jobMs = new List<double>(); var renderMs = new List<double>();
        int last = pl.Position; double lastT = sw.Elapsed.TotalMilliseconds; int skipped = 0, changes = 0;
        int gc0 = GC.CollectionCount(0);
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        while (sw.ElapsedMilliseconds < ms)
        {
            double t0 = sw.Elapsed.TotalMilliseconds;
            jobs();
            double t1 = sw.Elapsed.TotalMilliseconds;
            bool changed = pl.Position != last;
            render();
            double now = sw.Elapsed.TotalMilliseconds;
            if (changed)
            {
                jobMs.Add(t1 - t0); renderMs.Add(now - t1);
                int len = Math.Max(1, pl.Length);
                int step = ((pl.Position - last) % len + len) % len;
                if (step > 1) skipped += step - 1;
                double nominal = 0;
                for (int i = 0; i < step; i++) nominal += delays[(last + i) % len];
                if (changes++ > 0) { gaps.Add(t1 - lastT); errs.Add(t1 - lastT - nominal); }
                last = pl.Position; lastT = t1;
            }
            await Task.Yield();
        }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        if (gaps.Count == 0) return "no frame change";
        gaps.Sort(); errs.Sort(); jobMs.Sort(); renderMs.Sort();
        static double P(List<double> l, double q) => l[Math.Min(l.Count - 1, (int)(q * l.Count))];
        return $"{gaps.Count} frame changes in {sw.Elapsed.TotalSeconds:0.0} s (the animation's own time for them: {errs.Count * delays[0] / 1000.0:0.0} s at {delays[0]} ms a frame), {skipped} skipped; "
             + $"interval mean {gaps.Average():0.0} ms, 95th percentile {P(gaps, 0.95):0.0}, worst {gaps[^1]:0.0}; "
             + $"against the frame's own time: mean {errs.Average():+0.0;-0.0} ms, 95th {P(errs, 0.95):+0.0;-0.0}, worst {errs[^1]:+0.0;-0.0}; "
             + $"UI thread per frame change: player and bindings mean {jobMs.Average():0.00} ms (95th {P(jobMs, 0.95):0.00}), render mean {renderMs.Average():0.0} ms (95th {P(renderMs, 0.95):0.0}); "
             + $"UI thread allocated {alloc / 1024.0 / Math.Max(1, changes):0.0} KB per frame change; gen-0 collections {GC.CollectionCount(0) - gc0}";
    }

    /// <summary>Playback follows the clock: it keeps the animation's own speed, a stall skips frames instead of slowing the animation, every
    /// frame is one bitmap made once (smaller than the picture area, inside the memory limit), and a frame change makes no new bitmap.</summary>
    [AvaloniaFact]
    public async Task Playback_follows_the_clock_with_frames_made_once()
    {
        var lines = new List<string>();
        void Say(string s) { output.WriteLine(s); lines.Add(s); File.WriteAllLines(Path.Combine(OutDir, "pacing-fake.txt"), lines); }
        foreach (var (effect, where) in new[] { (AnimationEffect.RackFocus, "library"), (AnimationEffect.Fog, "library"), (AnimationEffect.Fog, "develop") })
        {
            await using var h = where == "develop" ? await DevelopHarness.OpenAsync() : await OneSelectedAsync(frameMs: 1);
            h.Lib.FakeFrameMs = 1;
            AnimatePanelViewModel p;
            if (where == "develop") { h.Develop().OpenAnimateCommand.Execute(null); p = h.Develop().AnimatePanel!; }
            else { h.Library.OpenAnimate(); p = h.Library.AnimatePanel!; }
            var pl = p.Player;
            p.Effects.First(e => e.Effect == effect).SelectCommand.Execute(null);
            Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == effect && pl.IsReady, 30_000));
            var preview = pl.Preview!;
            var player = Visible<AnimationPlayer>(h);
            int driven = player.FramesDriven;
            Say($"fake {effect} ({where}): " + await PaceAsync(pl, () => Dispatcher.UIThread.RunJobs(), () => h.Window.RenderFrame(), 8000));
            // the view asks for the due frame once per rendered frame
            Say($"   the view drove the player on {player.FramesDriven - driven} rendered frames");
            Assert.True(player.FramesDriven - driven > 50, "the render loop does not drive the player");
            Say($"   {preview.FrameCount} frames of {preview.FinalFrame(0)!.Width} × {preview.FinalFrame(0)!.Height}; for the screen {pl.DisplayFrameSize.W} × {pl.DisplayFrameSize.H}, {pl.DisplayBytes / 1e6:0.0} MB");
            Assert.True(pl.DisplayBytes <= AnimationPlayerViewModel.MaxDisplayBytes);

            // the speed: a run of frame changes takes the animation's own time (the clock does not drift)
            pl.IsPlaying = false; pl.Position = 0; pl.IsPlaying = true;
            var sw = Stopwatch.StartNew();
            int target = Math.Min(40, pl.MaxPosition - 1);
            Assert.True(await PumpAsync(h, () => pl.Position >= target, 20_000));
            double nominal = preview.DelaysMs.Take(pl.Position).Sum();
            Say($"   {pl.Position} frames in {sw.Elapsed.TotalMilliseconds:0} ms; the animation's own time is {nominal:0} ms");
            Assert.InRange(sw.Elapsed.TotalMilliseconds, nominal - 5, nominal + preview.DelaysMs[0] + 120);

            // each position shows one bitmap, the same one in every loop, and it is not a writeable one
            pl.IsPlaying = false;
            var seen = new Dictionary<int, Bitmap>();
            for (int round = 0; round < 2; round++)
                for (int i = 0; i <= pl.MaxPosition; i++)
                {
                    pl.Position = i;
                    Assert.NotNull(pl.Frame);
                    Assert.IsNotType<WriteableBitmap>(pl.Frame);
                    if (seen.TryGetValue(i, out var b)) Assert.Same(b, pl.Frame); else seen[i] = pl.Frame!;
                }
            Assert.Equal(preview.FrameCount, seen.Values.Distinct().Count());

            // a stall of the UI thread: the next tick shows the frame that is due then. Frames are skipped; the animation is not late.
            pl.Position = 0; pl.IsPlaying = true;
            int skippedBefore = pl.FramesSkipped;
            double stall = 6.5 * preview.DelaysMs[0];
            Thread.Sleep((int)stall);
            pl.Tick();
            Assert.InRange(pl.Position, 6, 7);
            Assert.True(pl.FramesSkipped - skippedBefore >= 5);
            Say($"   after a stall of {stall:0} ms the player shows position {pl.Position} ({pl.FramesSkipped - skippedBefore} frames skipped)");

            // a tick that changes the frame allocates no bitmap and little else
            pl.IsPlaying = false; pl.Position = 0; pl.IsPlaying = true;
            long a0 = GC.GetAllocatedBytesForCurrentThread(); int changes = 0, at = pl.Position;
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 1500) { pl.Tick(); if (pl.Position != at) { changes++; at = pl.Position; } }
            double perChange = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)Math.Max(1, changes);
            Say($"   a frame change in the player (with its bindings) allocates {perChange / 1024:0.0} KB ({changes} changes)");
            Assert.True(changes > 10);
            Assert.True(perChange < 64 * 1024, $"{perChange} bytes per frame change");
            if (where == "develop") { h.Develop().AnimatePanel = null; await h.SettlePictureAsync(); } else h.Library.CloseAnimate();
            await PumpAsync(h, () => false, 200);
        }
    }

    /// <summary>The frames for the screen never use more than the limit: a long preview of large frames gets smaller frames. Frames larger
    /// than the picture area are made smaller once, before they play.</summary>
    [AvaloniaFact]
    public async Task Frames_for_the_screen_fit_the_picture_area_and_the_memory_limit()
    {
        await using var h = await OneSelectedAsync(frameMs: 0);
        h.Lib.FakeSharpPass = true;
        h.Library.OpenAnimate();
        var p = h.Library.AnimatePanel!;
        var pl = p.Player;
        p.Size = p.Sizes.First(x => x.Value == 1040);
        p.Effects.First(e => e.Effect == AnimationEffect.ZoomThrough).SelectCommand.Execute(null);
        Assert.True(await PumpAsync(h, () => pl.Preview?.Options.Effect == AnimationEffect.ZoomThrough && pl.IsReady, 120_000));
        var preview = pl.Preview!;
        var f = preview.FinalFrame(0)!;
        var view = Visible<AnimationFrameView>(h);
        long native = (long)preview.FrameCount * f.Width * f.Height * 4;
        output.WriteLine($"{preview.FrameCount} final frames of {f.Width} × {f.Height} = {native / 1e6:0} MB; picture area {view.Bounds.Width:0} × {view.Bounds.Height:0}; "
                       + $"for the screen {pl.DisplayFrameSize.W} × {pl.DisplayFrameSize.H} = {pl.DisplayBytes / 1e6:0} MB (limit {AnimationPlayerViewModel.MaxDisplayBytes / 1e6:0} MB)");
        Assert.True(native > AnimationPlayerViewModel.MaxDisplayBytes, "the test needs a preview larger than the limit");
        Assert.True(pl.DisplayBytes <= AnimationPlayerViewModel.MaxDisplayBytes);
        Assert.True(pl.DisplayFrameSize.W <= view.Bounds.Width + 1 && pl.DisplayFrameSize.H <= view.Bounds.Height + 1);
        Assert.True(pl.DisplayFrameSize.W < f.Width);
        Assert.Equal((double)f.Width / f.Height, (double)pl.DisplayFrameSize.W / pl.DisplayFrameSize.H, 2);
        Assert.True(await PlaysAsync(h, pl));
        // the panel closes: the frames are released
        h.Library.CloseAnimate();
        Assert.Equal(0, pl.DisplayBytes);
        Assert.Null(pl.Frame);
    }

    // ---------------------------------------------------------------------------------------------------------------- 5. view modes close the panel

    /// <summary>A view mode chosen while the Animate panel is open closes the panel, so the chosen view shows: the header's Single, Photo +
    /// depth and Before / after (also the one that is already current), their keys, and the switch on the picture. The preview stops.</summary>
    [AvaloniaFact]
    public async Task Choosing_a_view_mode_closes_the_animate_panel()
    {
        await using var h = await DevelopHarness.OpenAsync();
        h.Lib.FakeFrameMs = 30;
        var d = h.Develop();
        Button Tab(string text) => h.Window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tab") && b.Content as string == text && b.IsEffectivelyVisible);

        async Task<(AnimatePanelViewModel Panel, IAnimationPreview Preview)> OpenAsync()
        {
            d.OpenAnimateCommand.Execute(null);
            var p = d.AnimatePanel!;
            Assert.True(await PumpAsync(h, () => p.Player.Preview?.IsPlanned == true, 10_000));
            Assert.True(Visible<AnimationPlayer>(h).IsEffectivelyVisible);
            return (p, p.Player.Preview!);
        }
        async Task ClosedAsync((AnimatePanelViewModel Panel, IAnimationPreview Preview) a, string what)
        {
            Assert.False(d.IsAnimateOpen, what + ": the Animate panel stayed open");
            Assert.Null(d.AnimatePanel);
            Assert.Null(a.Panel.Player.Preview);                      // the player let its preview go
            Assert.Null(a.Panel.Player.Frame);
            Assert.False(a.Panel.Player.IsRunning);
            Assert.True(await PumpAsync(h, () => a.Preview.Status == "Stopped" || a.Preview.IsFinal, 5000), what + ": the preview still makes frames");
            Assert.Equal("Stopped", a.Preview.Status);                // stopped before its 24 frames were made
            Assert.DoesNotContain(h.Window.GetVisualDescendants().OfType<AnimationPlayer>(), v => v.IsEffectivelyVisible);
            Assert.True(h.Viewer().IsEffectivelyVisible);
        }

        // the header's tabs, clicked as the user does
        foreach (var (text, mode) in new[] { ("Photo + depth", DevelopMode.PhotoDepth), ("Before / after", DevelopMode.BeforeAfter), ("Single", DevelopMode.Single) })
        {
            var a = await OpenAsync();
            if (text == "Photo + depth") Shot(h, "06-develop-animate-open");
            var tab = Tab(text);
            tab.Command!.Execute(tab.CommandParameter);
            await ClosedAsync(a, text);
            Assert.Equal(mode, d.Mode);
            if (text == "Photo + depth") { await h.SettlePictureAsync(); Shot(h, "07-develop-photo-and-depth-after-the-tab"); }
        }
        // the mode that is already current
        {
            var a = await OpenAsync();
            Assert.Equal(DevelopMode.Single, d.Mode);
            var tab = Tab("Single");
            tab.Command!.Execute(tab.CommandParameter);
            await ClosedAsync(a, "Single (already current)");
        }
        // the keys and the switches on the picture
        foreach (var (what, act, mode) in new (string, Action, DevelopMode)[]
                 {
                     ("D", () => d.TogglePhotoDepthCommand.Execute(null), DevelopMode.PhotoDepth),
                     ("Shift+D", () => d.ToggleDepthOnlyCommand.Execute(null), DevelopMode.PhotoDepth),
                     ("D again", () => d.TogglePhotoDepthCommand.Execute(null), DevelopMode.Single),
                     ("backslash", d.BackslashKey, DevelopMode.BeforeAfter),
                     ("before / after toggle", () => d.ToggleBeforeCommand.Execute(null), DevelopMode.Single),
                 })
        {
            var a = await OpenAsync();
            act();
            await ClosedAsync(a, what);
            Assert.Equal(mode, d.Mode);
        }
        // the Export panel does not cover the picture: a view mode leaves it open
        d.OpenExportCommand.Execute(null);
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        Assert.True(d.IsExportOpen);
        d.ExportPanel = null;
        d.SetModeCommand.Execute(DevelopMode.Single);
    }

    /// <summary>On the Library page the browse view chosen while the Animate panel is open closes the panel.</summary>
    [AvaloniaFact]
    public async Task Choosing_a_browse_view_closes_the_animate_panel()
    {
        await using var h = await OneSelectedAsync(frameMs: 30);
        foreach (var mode in new[] { BrowseMode.Timeline, BrowseMode.List, BrowseMode.Grid })
        {
            h.Library.OpenAnimate();
            var p = h.Library.AnimatePanel!;
            Assert.True(await PumpAsync(h, () => p.Player.Preview?.IsPlanned == true, 10_000));
            var preview = p.Player.Preview!;
            h.Library.SetModeCommand.Execute(mode);
            Assert.False(h.Library.IsAnimateOpen);
            Assert.Equal(mode, h.Library.Mode);
            Assert.Null(p.Player.Preview);
            Assert.True(await PumpAsync(h, () => preview.Status == "Stopped", 5000));
        }
    }
}
