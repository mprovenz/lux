using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>The Animate panel on a real library (env-gated: LUX_M6_REAL = a folder of linked captures, e.g. with
/// L16_00493): the depth computed through the Queue, the real preview (export level 4), focus picked on the photo, an animation job run to the
/// end; screenshots real-01 … and a timings file go to the workspace's output folder, the timings also to the test output.</summary>
public sealed class RealAnimateTests(ITestOutputHelper output)
{
    static string Shots => Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "ui", "shots");

    [AvaloniaFact]
    public async Task Real_animate_screens()
    {
        var folder = Environment.GetEnvironmentVariable("LUX_M6_REAL");
        if (string.IsNullOrEmpty(folder)) { output.WriteLine("LUX_M6_REAL not set: skipped"); return; }
        folder = Path.GetFullPath(folder);
        string appData = Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "ui", "appdata", "real");
        Directory.CreateDirectory(appData);
        var lines = new List<string>();
        void Say(string s) { output.WriteLine(s); lines.Add(s); File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "ui", "real-animate.txt"), lines); }
        var platform = new TestPlatform();
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, platform, log: m => { });
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        var sw = Stopwatch.StartNew();
        async Task Settle(Func<bool> until, int timeoutMs)
        {
            var t = Stopwatch.StartNew(); int quiet = 0;
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs();
                win.RenderFrame();
                quiet = until() ? quiet + 1 : 0;
                if (quiet >= 3) return;
                await Task.Delay(100);
            }
            Say($"settle timed out after {timeoutMs} ms");
        }
        string Shot(string name) => Harness.Capture(win, name, Shots);
        try
        {
            await vm.InitializeAsync();
            if (!s.Library.GetFolders().Any(f => f.Path.TrimEnd('/') == folder.TrimEnd('/'))) await s.Library.AddFolderAsync(folder);
            else foreach (var f in s.Library.GetFolders()) s.Library.RescanFolder(f.Id);   // captures linked in since the last run
            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 300_000);
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;   // the app reopens on the last page used
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await Settle(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000);
            var item = vm.Library.Items.First(i => i.Name == "L16_00493");
            vm.Library.Click(item, false, false);
            await Settle(() => true, 2000);

            // ---- the panel, rack focus: the depth (through the Queue when not kept yet), then the preview
            sw.Restart();
            vm.Library.OpenAnimate();
            var p = vm.Library.AnimatePanel!;
            await Settle(() => p.Player.Preview?.IsPlanned == true || p.Player.HasError, 300_000);
            Say($"rack focus: plan after {sw.Elapsed.TotalSeconds:0.0} s ({p.Player.Status})");
            await Settle(() => p.Player.HasFrame, 120_000);
            Say($"first frame after {sw.Elapsed.TotalSeconds:0.0} s");
            await Settle(() => p.Player.IsComplete, 300_000);
            Say($"all {p.Player.Preview!.FrameCount} preview frames after {sw.Elapsed.TotalSeconds:0.0} s; estimate: {p.Estimate}");
            await Settle(() => !p.Estimate.StartsWith("Estimating", StringComparison.Ordinal), 30_000);
            p.Player.IsPlaying = false; p.Player.Position = 10;
            await Settle(() => true, 1500);
            // the frames carry the held turn (exports follow the view shown as held), drawn unturned
            var f0 = p.Player.Preview!.Frame(0)!;
            Say($"display turn {p.Player.Rotation}°, frame {f0.Width} × {f0.Height}, orientation choice shown {p.HasTurnedPhotos}");
            Assert.Equal(0, p.Player.Rotation);
            Assert.Equal(item.Item.DisplayRotation != 0, p.HasTurnedPhotos);
            if (item.Item.DisplayRotation is 90 or 270) Assert.True(f0.Height > f0.Width);
            Shot("real-01-animate-panel");

            // ---- picking the ends on the photo
            p.PickFromCommand.Execute(null);
            p.ClickPicture(0.30, 0.80);
            await Settle(() => p.FocusFromText.Length > 0, 60_000);
            p.PickToCommand.Execute(null);
            await Settle(() => true, 800);
            Shot("real-02-rack-focus-picking");
            p.ClickPicture(0.70, 0.25);
            await Settle(() => p.FocusToText.Length > 0, 60_000);
            Say($"picked: from {p.FocusFromText} m, to {p.FocusToText} m");
            sw.Restart();
            await Settle(() => p.Player.Preview?.Options.FocusToMm != null && p.Player.IsComplete, 300_000);
            Say($"new preview (picked ends) in {sw.Elapsed.TotalSeconds:0.0} s; estimate: {p.Estimate}");
            p.Player.Position = 6;
            p.Player.IsPlaying = true;
            await Settle(() => true, 1200);
            p.Player.IsPlaying = false;
            await Settle(() => true, 500);
            Shot("real-03-preview-playing");

            // ---- queue a small one and run it
            p.Size = p.Sizes.First(x => x.Value == 520);
            p.Frames = 12;
            await Settle(() => p.Player.IsComplete, 300_000);
            string outDir = Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "ui", "out");
            p.Folder = Path.GetFullPath(outDir);
            long before = s.Library.Jobs.GetJobs().Where(j => j.Kind == JobKind.Animation).Select(j => j.Id).DefaultIfEmpty(0).Max();
            sw.Restart();
            p.AddToQueueCommand.Execute(null);
            await Settle(() => s.Library.Jobs.GetJobs().Any(j => j.Kind == JobKind.Animation && j.Id > before), 20_000);
            var job = s.Library.Jobs.GetJobs().First(j => j.Kind == JobKind.Animation && j.Id > before);
            Say($"queued row names its file at once: {Path.GetFileName(job.PlannedFile)}");
            vm.CurrentPage = Page.Queue;
            await Settle(() => s.Library.Jobs.Get(job.Id)!.State == JobState.Running && s.Library.Jobs.Get(job.Id)!.Progress > 0.4, 300_000);
            Shot("real-04-queue-animation-job");
            await Settle(() => s.Library.Jobs.Get(job.Id)!.IsFinished, 600_000);
            var done = s.Library.Jobs.Get(job.Id)!;
            Say($"job: {done.State} in {sw.Elapsed.TotalSeconds:0.0} s → {done.ResultPath} ({(done.ResultPath is { } rp && File.Exists(rp) ? new FileInfo(rp).Length : 0)} bytes); {string.Join(" · ", done.Notes)}");
            Assert.Equal(JobState.Done, done.State);
            Assert.Equal(done.PlannedFile, done.ResultPath);   // the name on the waiting row was the name written
            await Settle(() => true, 1000);
            Shot("real-05-queue-finished");

            // ---- the wigglegram preview of the same photo
            vm.CurrentPage = Page.Library;
            vm.Library.OpenAnimate();
            p = vm.Library.AnimatePanel!;
            sw.Restart();
            p.Effects.First(e => e.Effect == AnimationEffect.Wigglegram).SelectCommand.Execute(null);
            await Settle(() => p.Player.Preview?.Options.Effect == AnimationEffect.Wigglegram && p.Player.IsComplete, 300_000);
            Say($"wigglegram preview in {sw.Elapsed.TotalSeconds:0.0} s ({p.Player.Length} played)");
            p.Player.IsPlaying = false; p.Player.Position = 1;
            await Settle(() => true, 1000);
            Shot("real-06-wigglegram-preview");

            vm.Library.CloseAnimate();

            // ---- a photo with a saved rotation (Lumen session of L16_00056: turned 90°): exported and shown turned by the saved turn alone
            var turned = vm.Library.Items.FirstOrDefault(i => i.Name == "L16_00056");
            if (turned != null)
            {
                if (turned.Item.Edits.Source != EditSource.Lumen)
                    foreach (var id in s.Library.ImportLumenEdits(new[] { turned.Id })) await s.Library.Jobs.WaitAsync(id);
                await Settle(() => vm.Library.Items.First(i => i.Name == "L16_00056").Item.GeometryDecided, 30_000);
                turned = vm.Library.Items.First(i => i.Name == "L16_00056");
                vm.Library.Click(turned, false, false);
                await Settle(() => true, 1000);
                sw.Restart();
                vm.Library.OpenAnimate();
                p = vm.Library.AnimatePanel!;
                await Settle(() => p.Player.IsComplete || p.Player.HasError, 300_000);
                var f = p.Player.Preview!.Frame(0)!;
                Say($"saved rotation (L16_00056): preview in {sw.Elapsed.TotalSeconds:0.0} s, display turn {p.Player.Rotation}°, orientation choice shown {p.HasTurnedPhotos}, frame {f.Width} × {f.Height}");
                Assert.Equal(0, p.Player.Rotation);
                Assert.False(p.HasTurnedPhotos);
                Assert.True(f.Height > f.Width);
                p.Player.IsPlaying = false; p.Player.Position = 12;
                await Settle(() => true, 1000);
                Shot("real-08-saved-rotation");
                vm.Library.CloseAnimate();
            }

            // ---- from Develop, on a photo without depth: the depth is a Queue job named after the photo, not work inside the preview
            var tele = vm.Library.Items.FirstOrDefault(i => i.Name == "L16_04364");
            if (tele != null && tele.Item.CacheLevel != CacheLevel.None)
            {   // an earlier run kept its depth: let it go, so this run starts from a photo without depth again
                foreach (var id in s.Library.SetCacheLevel(new[] { tele.Id }, CacheLevel.None)) await s.Library.Jobs.WaitAsync(id);
                await Settle(() => vm.Library.Items.First(i => i.Name == "L16_04364").Item.CacheLevel == CacheLevel.None, 30_000);
                tele = vm.Library.Items.First(i => i.Name == "L16_04364");
            }
            if (tele != null && tele.Item.CacheLevel == CacheLevel.None)
            {
                vm.OpenInDevelop(tele);
                await Settle(() => vm.Develop.HasSession, 60_000);
                sw.Restart();
                long jobsBefore = s.Library.Jobs.GetJobs().Select(j => j.Id).DefaultIfEmpty(0).Max();
                vm.Develop.OpenAnimateCommand.Execute(null);
                var dp = vm.Develop.AnimatePanel!;
                await Settle(() => s.Library.Jobs.GetJobs().Any(j => j.Kind == JobKind.Analysis && j.CaptureId == tele.Id && j.Id > jobsBefore), 60_000);
                var aj = s.Library.Jobs.GetJobs().First(j => j.Kind == JobKind.Analysis && j.CaptureId == tele.Id && j.Id > jobsBefore);
                Say($"Develop: the depth of L16_04364 queued as job {aj.Id} \"{aj.Title}\" after {sw.Elapsed.TotalSeconds:0.0} s; preview status: {dp.Player.Status}");
                vm.CurrentPage = Page.Queue;
                await Settle(() => s.Library.Jobs.Get(aj.Id)!.State == JobState.Running, 60_000);
                await Settle(() => true, 1500);
                Shot("real-09-develop-depth-in-queue");
                vm.CurrentPage = Page.Develop;
                await Settle(() => dp.Player.IsComplete || dp.Player.HasError, 600_000);
                Say($"Develop preview after the queued analysis: {sw.Elapsed.TotalSeconds:0.0} s, {dp.Player.Status}; note now: {dp.EffectNote ?? "(none)"}");
                Assert.True(dp.Player.IsComplete);
                Assert.DoesNotContain("computed first", dp.EffectNote ?? "");
                vm.Develop.AnimatePanel = null;
                vm.CurrentPage = Page.Library;
            }
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.DisposeAsync();
        }
    }
}
