using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The Animate panel's preview player on a real capture (L16_00493 from the folder that <c>LUX_TEST_CAPTURES</c> names; skipped without
/// it). The capture is read through a link in the UI output folder, with a library of its own there. The test checks that the player
/// plays only frames of the final preview quality, that every effect plays by itself, and that a new effect starts soon after the
/// zoom-through's long step is abandoned. It writes the pacing numbers and a screenshot of each progress state.
/// </summary>
public sealed class RealAnimatePlaybackTests(ITestOutputHelper output)
{
    const string Name = "L16_00493";

    [AvaloniaFact]
    public async Task Real_preview_plays_only_final_frames_and_every_effect_plays_by_itself()
    {
        string source = Path.Combine(Paths.Captures, Name + ".lri");
        if (Paths.Captures.Length == 0 || !File.Exists(source)) { output.WriteLine($"LUX_TEST_CAPTURES has no {Name}.lri: skipped"); return; }
        string root = Path.Combine(AnimatePlaybackTests.OutDir, "real");
        string lib = Path.Combine(root, "lib"), appData = Path.Combine(root, "appdata"), shots = Path.Combine(AnimatePlaybackTests.OutDir, "shots");
        Directory.CreateDirectory(lib); Directory.CreateDirectory(appData);
        string link = Path.Combine(lib, Name + ".lri");
        if (!File.Exists(link)) File.CreateSymbolicLink(link, source);
        var lines = new List<string>();
        void Say(string s) { output.WriteLine(s); lines.Add(s); File.WriteAllLines(Path.Combine(AnimatePlaybackTests.OutDir, "pacing-real.txt"), lines); }

        TestWindows.CloseStray();
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: m => { });
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        async Task<bool> Pump(Func<bool> until, int ms, Action? each = null)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs(); win.RenderFrame();
                each?.Invoke();
                if (until()) return true;
                await Task.Delay(15);
            }
            return until();
        }
        try
        {
            await vm.InitializeAsync();
            if (!s.Library.GetFolders().Any()) await s.Library.AddFolderAsync(lib);
            Assert.True(await Pump(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 300_000));
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            Assert.True(await Pump(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000));
            var item = vm.Library.Items.First(i => i.Name == Name);
            vm.Library.Click(item, false, false);
            await Pump(() => false, 500);

            vm.Library.OpenAnimate();
            var p = vm.Library.AnimatePanel!;
            var pl = p.Player;
            p.Size = p.Sizes.First(x => x.Value == 1040);
            p.Frames = 8;                                             // a small animation: eight frames, played forward and back

            // The rule, checked on every pass until the test ends: nothing moves and nothing plays before the preview is final, and a
            // preview never reports "complete and not sharpening" before it is final.
            int moved = 0, ranEarly = 0, gap = 0;
            var watched = new HashSet<IAnimationPreview>();
            void Rule()
            {
                if (pl.Preview is { } q && watched.Add(q))
                    q.Changed += (_, _) => { if (q.IsComplete && !q.IsSharpening && !q.IsFinal) Interlocked.Increment(ref gap); };
                if (pl.IsReady) return;
                if (pl.Position != 0) moved++;
                if (pl.IsRunning) ranEarly++;
            }

            // ---- rack focus at 1040 pixels: quick frames from export level 4, then the final frames from export level 3
            var sw = Stopwatch.StartNew();
            var stillSizes = new List<string>(); var texts = new List<string>(); bool shotFirst = false, shotFinal = false;
            void Watch()
            {
                Rule();
                if (pl.IsReady || pl.Preview is not { Options.Frames: 8 }) return;
                if (pl.Frame is { } b && (stillSizes.Count == 0 || stillSizes[^1] != $"{b.PixelSize.Width} × {b.PixelSize.Height}")) stillSizes.Add($"{b.PixelSize.Width} × {b.PixelSize.Height}");
                string text = pl.LoadingText;
                if (texts.Count == 0 || texts[^1] != text) texts.Add(text);
                if (!shotFirst && text.EndsWith("first pass", StringComparison.Ordinal) && pl.HasFrame) { shotFirst = true; Harness.Capture(win, "real-01-progress-first-pass", shots); }
                if (!shotFinal && text.EndsWith("final pass", StringComparison.Ordinal) && pl.LoadingFraction > 0.4) { shotFinal = true; Harness.Capture(win, "real-02-progress-final-pass", shots); }
            }
            Assert.True(await Pump(() => pl.Preview is { Options.Frames: 8 } && (pl.IsReady || pl.HasError), 600_000, Watch), "the rack focus preview never got ready");
            Assert.False(pl.HasError, pl.Status);
            var view = win.GetVisualDescendants().OfType<AnimationFrameView>().First(v => v.IsEffectivelyVisible);
            var rack = pl.Preview!;
            var f0 = rack.FinalFrame(0)!;
            Say($"rack focus, 8 frames, file 1040 px: ready after {sw.Elapsed.TotalSeconds:0.0} s; progress texts: {string.Join(" | ", texts)}");
            Say($"   still picture while waiting: {string.Join(" then ", stillSizes)}; final frames {f0.Width} × {f0.Height}; for the screen {pl.DisplayFrameSize.W} × {pl.DisplayFrameSize.H} "
              + $"({pl.DisplayBytes / 1e6:0.0} MB), drawn at {view.PictureRect.Width:0} × {view.PictureRect.Height:0}; detail note: {p.PreviewDetailNote ?? "(none)"}");
            Assert.True(rack.IsFinal);
            Assert.False(rack.IsSharpening);
            Assert.Contains(texts, t => t.EndsWith("first pass", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.EndsWith("final pass", StringComparison.Ordinal));
            for (int i = 0; i < rack.FrameCount; i++)
            {
                var f = rack.FinalFrame(i)!;
                Assert.Equal((f0.Width, f0.Height), (f.Width, f.Height));
                Assert.Same(f, rack.Frame(i));                        // the final frame is the sharpest one there is
            }
            Assert.True(Math.Max(f0.Width, f0.Height) > 900, $"the final frames are {f0.Width} × {f0.Height}: not the sharper pass");
            // the frames on screen are the final ones: as large as the picture area allows, never the 520-pixel quick frames enlarged
            Assert.True(Math.Max(pl.DisplayFrameSize.W, pl.DisplayFrameSize.H) >= Math.Min(Math.Max(f0.Width, f0.Height), (int)Math.Max(view.PictureRect.Width, view.PictureRect.Height) - 2));
            int at = pl.Position;
            Assert.True(await Pump(() => pl.Position != at, 5000, Rule), "the rack focus preview does not play by itself");
            Harness.Capture(win, "real-03-playing-final-frames", shots);
            Say("real rack focus (library): " + await AnimatePlaybackTests.PaceAsync(pl, () => Dispatcher.UIThread.RunJobs(), () => win.RenderFrame(), 8000));

            // ---- fog: one pass (export level 4); it plays by itself after the switch, also when the effect before was paused
            pl.IsPlaying = false;
            sw.Restart();
            p.Effects.First(e => e.Effect == AnimationEffect.Fog).SelectCommand.Execute(null);
            Assert.True(await Pump(() => pl.Preview?.Options.Effect == AnimationEffect.Fog && (pl.IsReady || pl.HasError), 600_000, Rule), "the fog preview never got ready");
            Assert.False(pl.HasError, pl.Status);
            var fog = pl.Preview!;
            Say($"fog, {fog.FrameCount} frames: ready after {sw.Elapsed.TotalSeconds:0.0} s; final frames {fog.FinalFrame(0)!.Width} × {fog.FinalFrame(0)!.Height}; for the screen "
              + $"{pl.DisplayFrameSize.W} × {pl.DisplayFrameSize.H} ({pl.DisplayBytes / 1e6:0.0} MB); detail note: {p.PreviewDetailNote ?? "(none)"}");
            Assert.True(pl.IsPlaying);
            at = pl.Position;
            Assert.True(await Pump(() => pl.Position != at, 5000, Rule), "the fog preview does not play by itself");
            Say("real fog (library): " + await AnimatePlaybackTests.PaceAsync(pl, () => Dispatcher.UIThread.RunJobs(), () => win.RenderFrame(), 8000));

            // ---- zoom-through: its final pass makes the full-size picture first (a minute or more). The player shows one still picture
            //      and the spinner for that step. The test does not wait for it: it chooses another effect, which must start soon.
            sw.Restart();
            p.Effects.First(e => e.Effect == AnimationEffect.ZoomThrough).SelectCommand.Execute(null);
            Assert.True(await Pump(() => pl.Preview?.Options.Effect == AnimationEffect.ZoomThrough && pl.LoadingText.StartsWith("Making the full-size picture", StringComparison.Ordinal), 600_000, Rule));
            var zoom = pl.Preview!;
            Assert.True(zoom.IsComplete);                             // the quick frames are all there …
            Assert.False(zoom.IsFinal);                               // … and they do not play
            Assert.False(pl.IsRunning);
            Assert.Equal(0, pl.Position);
            Assert.True(pl.IsLoadingIndeterminate);
            await Pump(() => false, 3000, Rule);
            Assert.Equal(0, pl.Position);
            Say($"zoom-through: quick frames after {sw.Elapsed.TotalSeconds:0.0} s; then \"{pl.LoadingText}\" with the first frame as a still ({pl.Frame!.PixelSize.Width} × {pl.Frame.PixelSize.Height}); nothing plays");
            Harness.Capture(win, "real-04-progress-full-size-picture", shots);

            sw.Restart();
            p.Effects.First(e => e.Effect == AnimationEffect.ApertureSweep).SelectCommand.Execute(null);
            p.Frames = 8;
            Assert.True(await Pump(() => pl.Preview is { Options.Effect: AnimationEffect.ApertureSweep, Options.Frames: 8, IsPlanned: true }, 600_000, Rule));
            Say($"aperture sweep chosen while the zoom-through made its full-size picture: planned after {sw.Elapsed.TotalSeconds:0.0} s (zoom-through preview: {zoom.Status})");
            Assert.True(await Pump(() => pl.IsReady || pl.HasError, 600_000, Rule));
            Assert.False(pl.HasError, pl.Status);
            Say($"   ready after {sw.Elapsed.TotalSeconds:0.0} s");
            at = pl.Position;
            Assert.True(await Pump(() => pl.Position != at, 5000, Rule), "the aperture sweep does not play by itself");
            Assert.Equal("Stopped", zoom.Status);

            // ---- back to the rack focus: it plays by itself on the second visit
            sw.Restart();
            p.Effects.First(e => e.Effect == AnimationEffect.RackFocus).SelectCommand.Execute(null);
            p.Frames = 8;
            Assert.True(await Pump(() => pl.Preview is { Options.Effect: AnimationEffect.RackFocus, Options.Frames: 8 } && (pl.IsReady || pl.HasError), 600_000, Rule));
            at = pl.Position;
            Assert.True(await Pump(() => pl.Position != at, 5000, Rule), "the rack focus does not play by itself on the second visit");
            Say($"rack focus again: ready after {sw.Elapsed.TotalSeconds:0.0} s");

            Assert.Equal(0, moved);
            Assert.Equal(0, ranEarly);
            Assert.Equal(0, gap);
            vm.Library.CloseAnimate();
            Assert.Null(pl.Frame);

            // ---- the export job is not part of the preview: a small rack focus written by the job, with its hash for comparison
            string outDir = Path.Combine(root, "out");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var job = s.Library.Animate(new[] { item.Id }, new AnimationOptions { Effect = AnimationEffect.RackFocus, Frames = 6, LongEdge = 520, Format = AnimationFormat.WebP, OutputFolder = outDir }).Single();
            var done = await s.Library.Jobs.WaitAsync(job);
            Assert.Equal(JobState.Done, done.State);
            Say($"export job: {Path.GetFileName(done.ResultPath)} {new FileInfo(done.ResultPath!).Length} bytes, SHA-256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(done.ResultPath!))).ToLowerInvariant()}");
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.DisposeAsync();
        }
    }
}
