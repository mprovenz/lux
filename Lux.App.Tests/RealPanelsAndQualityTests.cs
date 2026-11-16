using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Library;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Real-data checks of the Develop and Library pages: tool bars and side panels, the animation preview's size, the quality selector
/// around a quality change, before / after with no edits, the depth of an open photo when a job keeps or releases it, and the two
/// viewers of Photo + depth. They run only when <c>LUX_REAL_UI_CAPTURE</c> names a capture (.lri). Each
/// run links the capture (and its Lumen session when present) into a new folder under <c>LUX_REAL_UI_ROOT</c> (default: the test output
/// folder) and gives the app its own data there. The capture is only read. Results go to text files and screenshots in the same folder.
/// </summary>
public sealed class RealPanelsAndQualityTests(ITestOutputHelper output)
{
    static string? Capture => Environment.GetEnvironmentVariable("LUX_REAL_UI_CAPTURE") is { Length: > 0 } p && File.Exists(p) ? p : null;

    static string Root
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("LUX_REAL_UI_ROOT") is { Length: > 0 } r ? r : Path.Combine(AppContext.BaseDirectory, "real-ui");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static string Shots => Path.Combine(Root, "shots");

    readonly List<string> _lines = new();
    string _file = "real-ui.txt";
    void Say(string s) { output.WriteLine(s); _lines.Add($"{DateTime.Now:HH:mm:ss} {s}"); File.WriteAllLines(Path.Combine(Root, _file), _lines); }

    /// <summary>Remove a folder under <see cref="Root"/> without following links.</summary>
    static void Wipe(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("refusing to remove " + full);
        if (!Directory.Exists(full)) return;
        foreach (var e in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (e.LinkTarget != null || e is FileInfo) e.Delete();
            else Wipe(e.FullName);
        }
        Directory.Delete(full);
    }

    /// <summary>A new folder of links to the capture and its Lumen session. A cache file given in <c>LUX_REAL_UI_CACHE</c> is copied beside
    /// them when <paramref name="withCache"/>.</summary>
    static string MakeLibrary(string name, bool withCache)
    {
        var lri = Capture!;
        var dir = Path.Combine(Root, "lib", name);
        Wipe(dir);
        Directory.CreateDirectory(dir);
        File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(lri)), lri);
        var lris = Path.ChangeExtension(lri, ".lris");
        if (File.Exists(lris)) File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(lris)), lris);
        if (withCache && Environment.GetEnvironmentVariable("LUX_REAL_UI_CACHE") is { Length: > 0 } cache && File.Exists(cache))
        {
            Directory.CreateDirectory(Path.Combine(dir, "LuxImageContext", "cache"));
            File.Copy(cache, Path.Combine(dir, "LuxImageContext", "cache", Path.GetFileName(cache)));
        }
        return dir;
    }

    /// <summary>The app on a new library of the linked capture, headless.</summary>
    sealed class App : IAsyncDisposable
    {
        public required AppServices S { get; init; }
        public required MainViewModel Vm { get; init; }
        public required MainWindow Win { get; init; }
        public required Action<string> Say { get; init; }
        public string Id { get; set; } = "";

        public static async Task<App> OpenAsync(string name, bool withCache, Action<string> say)
        {
            TestWindows.CloseStray();
            var dir = MakeLibrary(name, withCache);
            var appData = Path.Combine(Root, "appdata", name);
            Wipe(appData);
            Directory.CreateDirectory(appData);
            var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: _ => { });
            ThumbnailView.Cache = s.Thumbnails;
            s.Library.Settings.DontAskAboutExtraStorage = true;
            var vm = new MainViewModel(s);
            var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            win.Show();
            var app = new App { S = s, Vm = vm, Win = win, Say = say };
            await vm.InitializeAsync();
            await s.Library.AddFolderAsync(dir);
            await app.Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 120_000);
            await vm.Library.RefreshTreeAsync();
            vm.CurrentPage = Page.Library;
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await app.Settle(() => !vm.Library.IsLoading && vm.Library.Items.Count > 0, 30_000);
            app.Id = vm.Library.Items[0].Id;
            return app;
        }

        public async Task<bool> Settle(Func<bool> until, int timeoutMs, int quietFrames = 3)
        {
            var t = Stopwatch.StartNew(); int quiet = 0;
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs(); Win.RenderFrame();
                quiet = until() ? quiet + 1 : 0;
                if (quiet >= quietFrames) return true;
                await Task.Delay(50);
            }
            Say($"settle timed out after {timeoutMs} ms");
            return false;
        }

        public CaptureItemViewModel Item => Vm.Library.Items.First(i => i.Id == Id);
        public DevelopView Develop => Win.GetVisualDescendants().OfType<DevelopView>().First();
        public LibraryView Library => Win.FindControl<LibraryView>("LibraryPage")!;
        public Lux.App.Viewer.PhotoViewer Viewer => Develop.PhotoViewer;
        public string Shot(string name) => Harness.Capture(Win, name, Shots);

        public async Task OpenInDevelopAsync()
        {
            Vm.Library.Click(Item, false, false);
            Vm.OpenInDevelop(Item);
            await Settle(() => Vm.Develop.HasSession, 120_000);
            Viewer.AnimateZoom = false;
            await Settle(() => Viewer.LevelOnScreen >= 0 && Viewer.TargetComplete, 120_000);
        }

        public async ValueTask DisposeAsync()
        {
            Win.Close();
            Dispatcher.UIThread.RunJobs();
            await Vm.DisposeAsync();
        }
    }

    /// <summary>Whether a control of the page is on screen: the first control of type <typeparamref name="T"/> under
    /// <paramref name="host"/> that <paramref name="match"/> picks.</summary>
    static bool Shown<T>(Control host, Func<T, bool> match) where T : Control =>
        host.GetVisualDescendants().OfType<T>().Where(match).Any(c => c.IsEffectivelyVisible && c.Bounds.Height > 0);

    static bool Named(Control c, string name) => Avalonia.Automation.AutomationProperties.GetName(c) == name;

    /// <summary>A bar of the Library page itself (the animation player's own bar has the same style).</summary>
    static bool LibraryBar(Border b, string style) => b.Classes.Contains(style) && b.FindAncestorOfType<AnimationPlayer>() == null;

    /// <summary>The bars of the Develop page and of the Library page that act on the picture or the grid.</summary>
    static string Bars(App a)
    {
        var d = a.Develop; var l = a.Library;
        bool focus = Shown<Border>(d, b => Named(b, "Focus options"));
        bool brush = Shown<Border>(d, b => Named(b, "Brush options"));
        bool crop = Shown<Border>(d, b => Named(b, "Crop options"));
        bool line = Shown<Border>(d, b => b.Classes.Contains("toolline"));
        bool sel = Shown<Border>(l, b => LibraryBar(b, "selectionbar"));
        bool tools = Shown<Border>(l, b => LibraryBar(b, "toolbar"));
        bool chips = Shown<Border>(l, b => LibraryBar(b, "chipbar"));
        return $"develop: focus bar {focus}, brush bar {brush}, crop bar {crop}, tool line {line}, tool {a.Vm.Develop.Depth.Tool}, cropping {a.Vm.Develop.IsCropping} · "
             + $"library: selection bar {sel}, tool bar {tools}, chip bar {chips} · page {a.Vm.CurrentPage}, develop animate {a.Vm.Develop.IsAnimateOpen}, export {a.Vm.Develop.IsExportOpen}, library animate {a.Vm.Library.IsAnimateOpen}";
    }

    /// <summary>Tool bars and side panels: a Develop tool and the Library's selection bar against the Animate and Export panels and page
    /// changes.</summary>
    [AvaloniaFact]
    public async Task Tool_bars_and_side_panels()
    {
        if (Capture == null) { output.WriteLine("LUX_REAL_UI_CAPTURE not set: skipped"); return; }
        _file = "panels.txt";
        await using var a = await App.OpenAsync("panels", withCache: true, Say);
        var vm = a.Vm; var d = vm.Develop;
        await a.OpenInDevelopAsync();
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); Say((ok ? "   ok: " : "   WRONG: ") + what); }

        // a tool from the list, made sure to be on (choosing the active tool again leaves it)
        async Task Use(PageTool tool)
        {
            if (d.Depth.Tool != tool) d.ToolCommand.Execute(d.Tools.First(t => t.Kind == tool && !t.IsQuickSelect));
            await a.Settle(() => true, 500);
            Assert.Equal(tool, d.Depth.Tool);
        }

        // ---- Develop: the focus tool, then Animate
        await Use(PageTool.Focus);
        Say("focus tool on: " + Bars(a));
        d.OpenAnimateCommand.Execute(null);
        await a.Settle(() => true, 1500);
        Say("develop, Animate opened with the focus tool on: " + Bars(a));
        a.Shot("panels-develop-animate-with-tool");
        Expect(d.Depth.Tool == PageTool.None && !Shown<Border>(a.Develop, b => Named(b, "Focus options")), "opening Animate in Develop ends the focus tool and closes its bar");
        d.AnimatePanel = null;
        await a.Settle(() => true, 500);

        // ---- Develop: the crop tool, then Animate
        d.CropCommand.Execute(null);
        await a.Settle(() => d.IsCropping, 5000);
        d.OpenAnimateCommand.Execute(null);
        await a.Settle(() => true, 1500);
        Say("develop, Animate opened while cropping: " + Bars(a));
        a.Shot("panels-develop-animate-while-cropping");
        Expect(!d.IsCropping && !Shown<Border>(a.Develop, b => Named(b, "Crop options")), "opening Animate in Develop ends the crop tool and closes its bar");
        d.AnimatePanel = null;
        await a.Settle(() => true, 500);

        // ---- Develop: a tool, then Export
        await Use(PageTool.Focus);
        d.OpenExportCommand.Execute(null);
        await a.Settle(() => true, 1000);
        Say("develop, Export opened with the focus tool on: " + Bars(a));
        a.Shot("panels-develop-export-with-tool");
        Expect(d.Depth.Tool == PageTool.None, "opening Export in Develop ends the focus tool");
        d.ExportPanel = null;
        await a.Settle(() => true, 500);

        // ---- Develop: a tool, then another page and back
        foreach (var page in new[] { Page.Library, Page.Queue, Page.Settings, Page.Camera })
        {
            await Use(PageTool.BlendArea);
            vm.CurrentPage = page;
            await a.Settle(() => true, 800);
            vm.CurrentPage = Page.Develop;
            await a.Settle(() => true, 800);
            Say($"develop, a tool on, to {page} and back: " + Bars(a));
            if (page == Page.Library) a.Shot("panels-develop-tool-after-page-change");
            Expect(d.Depth.Tool == PageTool.None && !Shown<Border>(a.Develop, b => Named(b, "Brush options")), $"going to {page} ends the Develop tool");
        }

        // ---- Develop: Animate open, then a tool is chosen
        d.OpenAnimateCommand.Execute(null);
        await a.Settle(() => true, 1000);
        d.ToolCommand.Execute(d.Tools.First(t => t.Kind == PageTool.Focus));
        await a.Settle(() => true, 1000);
        Say("develop, Animate open, then the focus tool: " + Bars(a));
        Expect(!(d.IsAnimateOpen && Shown<Border>(a.Develop, b => Named(b, "Focus options"))), "no tool bar over the Animate panel's preview");
        d.AnimatePanel = null;
        d.Depth.LeaveToolCommand.Execute(null);
        await a.Settle(() => true, 500);

        // ---- Library: a selection, then Animate
        vm.CurrentPage = Page.Library;
        vm.Library.Click(a.Item, false, false);
        await a.Settle(() => true, 800);
        Say("library, one selected: " + Bars(a));
        vm.Library.OpenAnimate();
        await a.Settle(() => true, 1500);
        Say("library, Animate opened: " + Bars(a));
        a.Shot("panels-library-animate-with-selection");
        Expect(!Shown<Border>(a.Library, b => LibraryBar(b, "selectionbar")), "no selection bar over the Library's Animate panel");
        Expect(!Shown<Border>(a.Library, b => LibraryBar(b, "toolbar")) && !Shown<Border>(a.Library, b => LibraryBar(b, "chipbar")),
               "no tool bar or filter chips over the Library's Animate panel");
        vm.Library.CloseAnimate();
        await a.Settle(() => true, 800);
        Say("library, Animate closed: " + Bars(a));
        Expect(Shown<Border>(a.Library, b => LibraryBar(b, "selectionbar")), "the selection bar is back when the Animate panel closes");
        // the Export panel keeps the grid on screen: its selection bar stays
        vm.Library.OpenExport();
        await a.Settle(() => true, 800);
        Say("library, Export opened: " + Bars(a));
        a.Shot("panels-library-export-with-selection");
        vm.Library.CloseExport();
        await a.Settle(() => true, 500);
        Say(problems.Count == 0 ? "all checks pass" : $"{problems.Count} wrong: {string.Join("; ", problems)}");
        Assert.Empty(problems);
    }

    // ================================================================================================================ animation preview

    /// <summary>Mean gradient of the luminance (|dx| + |dy|) of an RGBA8 picture: a plain measure of fine detail.</summary>
    static double Detail(int w, int h, byte[] rgba)
    {
        double s = 0; long n = 0;
        double L(int x, int y) { int i = (y * w + x) * 4; return 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2]; }
        for (int y = 1; y < h; y++)
            for (int x = 1; x < w; x++) { s += Math.Abs(L(x, y) - L(x - 1, y)) + Math.Abs(L(x, y) - L(x, y - 1)); n++; }
        return n == 0 ? 0 : s / n;
    }

    /// <summary>An RGBA8 picture scaled to <paramref name="w"/> × <paramref name="h"/> (bilinear, as the player draws an enlarged frame).</summary>
    static byte[] Scaled(int sw, int sh, byte[] rgba, int w, int h)
    {
        var info = new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var src = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, src.GetPixels(), rgba.Length);
        using var dst = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.Medium);
        var o = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(dst.GetPixels(), o, 0, o.Length);
        return o;
    }

    static void SavePng(int w, int h, byte[] rgba, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Frame <paramref name="index"/> of an animated file (GIF, WebP, APNG) as RGBA8.</summary>
    static (int W, int H, byte[] Rgba, int Frames) DecodeFrame(string path, int index)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException("cannot read " + path);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var r = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(Math.Min(index, Math.Max(0, codec.FrameCount - 1))));
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidOperationException($"frame {index}: {r}");
        var o = new byte[info.BytesSize];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), o, 0, o.Length);
        return (info.Width, info.Height, o, codec.FrameCount);
    }

    /// <summary>The animation preview of each effect on the page: frame size, source level and how much the player enlarges it; the cost of
    /// sharper parallax frames; the exported parallax file at the chosen size.</summary>
    [AvaloniaFact]
    public async Task Animation_preview_sizes_and_export()
    {
        if (Capture == null) { output.WriteLine("LUX_REAL_UI_CAPTURE not set: skipped"); return; }
        _file = "preview.txt";
        await using var a = await App.OpenAsync("preview", withCache: true, Say);
        var vm = a.Vm;
        vm.Library.Click(a.Item, false, false);
        await a.Settle(() => true, 500);
        vm.Library.OpenAnimate();
        var p = vm.Library.AnimatePanel!;
        p.Size = p.Sizes.First(x => x.Value == 1040);
        var results = new Dictionary<AnimationEffect, (int W, int H, double Shown, double Detail)>();
        (int W, int H, byte[] Rgba)? quickParallax = null;
        foreach (var effect in new[] { AnimationEffect.ParallaxSweep, AnimationEffect.Wigglegram, AnimationEffect.LensTour, AnimationEffect.RackFocus })
        {
            var choice = p.Effects.First(e => e.Effect == effect);
            if (!choice.IsAvailable) { Say($"{effect}: not available ({choice.Reason})"); continue; }
            var sw = Stopwatch.StartNew();
            choice.SelectCommand.Execute(null);
            await a.Settle(() => p.Player.Preview?.Options.Effect == effect && (p.Player.IsComplete || p.Player.HasError), 600_000);
            double quick = sw.Elapsed.TotalSeconds;
            var q0 = p.Player.Preview!.Frame(0)!;
            Say($"{effect}: quick frames ready: frame 0 is {q0.Width} × {q0.Height}; sharpening {p.Player.Preview.IsSharpening}; detail note \"{p.PreviewDetailNote}\"");
            if (effect == AnimationEffect.ParallaxSweep && p.Player.Preview.IsSharpening) a.Shot("preview-ParallaxSweep-sharpening");
            // the sharper frames, when the preview makes them
            await a.Settle(() => !p.Player.Status.Contains("sharpening", StringComparison.Ordinal), 600_000);
            p.Player.IsPlaying = false;
            p.Player.Position = 0;
            await a.Settle(() => true, 800);
            var f = p.Player.Preview!.Frame(0)!;
            var view = a.Library.GetVisualDescendants().OfType<AnimationFrameView>().First(v => v.IsEffectivelyVisible);
            double shown = view.PictureRect.Width / f.Width;
            double detail = Detail(f.Width, f.Height, f.Rgba);
            results[effect] = (f.Width, f.Height, shown, detail);
            Say($"{effect}: quick frames in {quick:0.0} s, all frames ready after {sw.Elapsed.TotalSeconds:0.0} s · frame {f.Width} × {f.Height} drawn at {view.PictureRect.Width:0} × {view.PictureRect.Height:0} (×{shown:0.00}) · detail {detail:0.00} · status \"{p.Player.Status}\" · note \"{p.PreviewNote}\" · detail note \"{p.PreviewDetailNote}\"");
            if (effect == AnimationEffect.ParallaxSweep)
            {
                // the quick frame enlarged to the file's size, for the detail measure at one scale
                SavePng(q0.Width, q0.Height, q0.Rgba, Path.Combine(Root, "preview", "ParallaxSweep-quick-frame0.png"));
                quickParallax = (q0.Width, q0.Height, q0.Rgba);
            }
            a.Shot($"preview-{effect}");
            SavePng(f.Width, f.Height, f.Rgba, Path.Combine(Root, "preview", $"{effect}-frame0.png"));
        }
        vm.Library.CloseAnimate();
        await a.Settle(() => true, 500);

        // ---- the same preview on the Develop page (a larger picture area)
        await a.OpenInDevelopAsync();
        vm.Develop.OpenAnimateCommand.Execute(null);
        var dp = vm.Develop.AnimatePanel!;
        dp.Size = dp.Sizes.First(x => x.Value == 1040);
        dp.Effects.First(e => e.Effect == AnimationEffect.ParallaxSweep).SelectCommand.Execute(null);
        await a.Settle(() => dp.Player.Preview?.Options.Effect == AnimationEffect.ParallaxSweep && dp.Player.IsComplete, 600_000);
        var dq = dp.Player.Preview!.Frame(0)!;
        var dview = a.Develop.GetVisualDescendants().OfType<AnimationFrameView>().First(v => v.IsEffectivelyVisible);
        Say($"develop, parallax quick frame {dq.Width} × {dq.Height} drawn at {dview.PictureRect.Width:0} × {dview.PictureRect.Height:0} (×{dview.PictureRect.Width / dq.Width:0.00})");
        await a.Settle(() => !dp.Player.Preview!.IsSharpening, 600_000);
        dp.Player.IsPlaying = false; dp.Player.Position = 0;
        await a.Settle(() => true, 800);
        var ds = dp.Player.Preview!.Frame(0)!;
        Say($"develop, parallax after the sharper frames: {ds.Width} × {ds.Height} drawn at {dview.PictureRect.Width:0} × {dview.PictureRect.Height:0} (×{dview.PictureRect.Width / ds.Width:0.00}); detail note \"{dp.PreviewDetailNote}\"");
        a.Shot("preview-develop-parallax-sharp");
        vm.Develop.AnimatePanel = null;
        await a.Settle(() => true, 500);

        // ---- the cost of sharper parallax frames on this computer: the export's own frames (1040 px: export level 3, the other wide lenses
        //      filling in) and the main view alone at the same level
        var lri = Path.Combine(Root, "lib", "preview", Path.GetFileName(Capture));
        using (var eng = CaptureSession.Open(lri, new SessionOptions { Context = new Lux.Store.StoreCaptureContext(lri, mayCreateFolder: false), DisplayCacheBytes = 0 }))
        {
            foreach (var (label, spec) in new[]
                     {
                         ("level 4, main view (the quick preview)", new AnimationOptions { Effect = AnimationEffect.ParallaxSweep }.ToSpec(level: 4, longEdge: 0) with { SingleView = true }),
                         ("1040 px, main view", new AnimationOptions { Effect = AnimationEffect.ParallaxSweep, LongEdge = 1040 }.ToSpec() with { SingleView = true }),
                         ("1040 px, as exported (other lenses fill in)", new AnimationOptions { Effect = AnimationEffect.ParallaxSweep, LongEdge = 1040 }.ToSpec()),
                     })
            {
                var sw = Stopwatch.StartNew();
                var r = await AnimationRenderer.CreateAsync(eng, spec);
                double plan = sw.Elapsed.TotalSeconds;
                var times = new List<double>();
                Rgba? first = null;
                for (int k = 0; k < r.Plan.Frames.Count; k++)
                {
                    var t = Stopwatch.StartNew();
                    var img = r.Render(k);
                    first ??= img;
                    times.Add(t.Elapsed.TotalSeconds);
                }
                Say($"parallax {label}: level {r.Plan.Level}, {r.Plan.Size.W} × {r.Plan.Size.H}, {r.Plan.Frames.Count} frames · plan {plan:0.00} s, first frame {times[0]:0.00} s (with the set-up), "
                    + $"then {times.Skip(1).DefaultIfEmpty(0).Average():0.00} s a frame · all {sw.Elapsed.TotalSeconds:0.0} s · detail {Detail(first!.W, first.H, first.P):0.00}");
                SavePng(first.W, first.H, first.P, Path.Combine(Root, "preview", $"parallax-{r.Plan.Level}-{r.Plan.Size.W}-{(spec.SingleView ? "single" : "lenses")}.png"));
            }
            // the lens pictures at the file's resolution (what a sharper wigglegram or lens tour would need)
            var wspec = new AnimationOptions { Effect = AnimationEffect.Wigglegram, LongEdge = 1040 }.ToSpec();
            var swW = Stopwatch.StartNew();
            var rw = await AnimationRenderer.CreateAsync(eng, wspec);
            for (int k = 0; k < rw.Plan.Frames.Count; k++) rw.Render(k);
            Say($"wigglegram at the file's resolution: {rw.Plan.Size.W} × {rw.Plan.Size.H}, {rw.Plan.Frames.Count} frames in {swW.Elapsed.TotalSeconds:0.0} s");
        }

        // ---- the exported parallax file at 1040 px
        var outDir = Path.Combine(Root, "out-parallax");
        Wipe(outDir); Directory.CreateDirectory(outDir);
        var ids = a.S.Library.Animate(new[] { a.Id }, new AnimationOptions { Effect = AnimationEffect.ParallaxSweep, LongEdge = 1040, Format = AnimationFormat.WebP, OutputFolder = outDir });
        var sw2 = Stopwatch.StartNew();
        var info = await a.S.Library.Jobs.WaitAsync(ids[0]);
        Say($"export job {info.State} in {sw2.Elapsed.TotalSeconds:0.0} s: {info.ResultPath} {info.Error}");
        Assert.Equal(JobState.Done, info.State);
        var (w, h, px, frames) = DecodeFrame(info.ResultPath!, 0);
        double fileDetail = Detail(w, h, px);
        SavePng(w, h, px, Path.Combine(Root, "preview", "export-parallax-frame0.png"));
        Say($"exported parallax: {w} × {h}, {frames} frames, {new FileInfo(info.ResultPath!).Length / 1024} KB · detail {fileDetail:0.00}");
        if (quickParallax is { } qp)
        {
            double enlarged = Detail(w, h, Scaled(qp.W, qp.H, qp.Rgba, w, h));
            Say($"   the quick preview frame is {qp.W} × {qp.H}: the file is {(double)w / qp.W:0.00}× its width; detail at the file's size: quick frame enlarged {enlarged:0.00}, the file {fileDetail:0.00}");
        }
        if (results.TryGetValue(AnimationEffect.ParallaxSweep, out var pq))
            Say($"   the preview's parallax frames on screen at the end: {pq.W} × {pq.H}, detail {pq.Detail:0.00}");
        Assert.True(Math.Max(w, h) == 1040, "the file has the chosen size");
        // the preview's parallax frames end at the file's size; the lens effects say that the preview has less detail
        Assert.True(results.TryGetValue(AnimationEffect.ParallaxSweep, out var end) && end.W == w && end.H == h, "the parallax preview ends at the file's size");
    }

    // ================================================================================================================ quality and before / after

    /// <summary>What the Develop page's selector shows on screen: the option drawn as selected, the ticks drawn, and its label.</summary>
    static (string Selected, bool Tick13, bool TickFull, string Label) OnScreen(App a)
    {
        Dispatcher.UIThread.RunJobs();
        var q = a.Develop.GetVisualDescendants().OfType<QualitySelector>().First(c => c.Name == "ViewerQuality");
        var buttons = q.GetVisualDescendants().OfType<Button>().ToList();
        string sel = buttons.FirstOrDefault(b => b.Classes.Contains("selected"))?.Name switch { "PreviewOption" => "Preview", "StandardOption" => "13 MP", "FullOption" => "Full detail", _ => "-" };
        bool Tick(string option) => buttons.First(b => b.Name == option).GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.Classes.Contains("kepttick") && p.IsEffectivelyVisible);
        var label = a.Develop.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "CompactLabelText")?.Text
                    ?? a.Develop.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "Quality" && t.IsEffectivelyVisible)?.Text ?? "";
        return (sel, Tick("StandardOption"), Tick("FullOption"), label);
    }

    static string Selector(QualitySelectorViewModel q) =>
        $"selected {(q.IsPreview ? "Preview" : q.IsStandard ? "13 MP" : q.IsFull ? "Full detail" : "-")}, tick 13 MP {q.StandardKept}, tick full {q.FullKept}, pending {q.IsPending} · \"{q.Caption}\"";

    /// <summary>The drawn pixels of the main viewer (BGRA rows of its bounds) without the bands at the top and the bottom, where the
    /// page's controls lie over the picture; with <paramref name="leftOfCentre"/>, only the part left of the split's divider.</summary>
    static byte[] ViewerPixels(App a, bool leftOfCentre = false)
    {
        Dispatcher.UIThread.RunJobs();
        var c = a.Viewer;
        using var frame = a.Win.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        var tl = c.TranslatePoint(new Point(0, 0), a.Win) ?? default;
        double k = frame.PixelSize.Width / a.Win.Bounds.Width;
        int band = (int)(64 * k);
        int x0 = (int)(tl.X * k), y0 = (int)(tl.Y * k) + band, w = (int)(c.Bounds.Width * k), h = (int)(c.Bounds.Height * k) - 2 * band;
        if (leftOfCentre && c.Transform() is { } t)
        {
            // up to a little before the split's divider (the same columns are compared in every mode)
            double divider = t.DisplayRect.X + t.DisplayRect.Width * c.SplitPosition;
            w = Math.Clamp((int)((divider - 16) * k), 1, w);
        }
        using var fb = frame.Lock();
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(fb.Address + (y0 + y) * fb.RowBytes + x0 * 4, o, y * w * 4, w * 4);
        return o;
    }

    static (double Mean, double Max) Diff(byte[] x, byte[] y)
    {
        long s = 0; int max = 0;
        for (int i = 0; i < x.Length; i += 4)
            for (int c = 0; c < 3; c++) { int d = Math.Abs(x[i + c] - y[i + c]); s += d; if (d > max) max = d; }
        return ((double)s / (x.Length / 4 * 3), max);
    }

    /// <summary>The levels a channel's cache holds at the current version, finest first ("L1 ×12, L4 ×1").</summary>
    static string Levels(App a, int channel)
    {
        var v = a.Viewer; var cache = v.CacheOf(channel);
        return string.Join(", ", cache.Entries.GroupBy(e => e.Key.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key} ×{g.Count()}"));
    }

    /// <summary>Before / after with no edits at the zoom given: the levels each side holds, then the toggle's before and after pictures compared.</summary>
    async Task<(double Mean, double Max)> CompareSides(App a, string quality, string zoom, List<string> problems)
    {
        var d = a.Vm.Develop; var v = a.Viewer;
        d.SetModeCommand.Execute(DevelopMode.BeforeAfter);
        d.SetCompareStyleCommand.Execute(CompareStyle.Split);
        await a.Settle(() => v.TargetComplete, 60_000);
        await a.Settle(() => true, 4000, quietFrames: 60);
        Say($"   {quality} {zoom}: split: target level {v.TargetLevel}; after holds {Levels(a, 0)}; before holds {Levels(a, 1)}");
        a.Shot($"compare-split-{quality}-{zoom}");
        var splitLeft = ViewerPixels(a, leftOfCentre: true);
        d.SetCompareStyleCommand.Execute(CompareStyle.Toggle);
        d.ShowingBefore = true;
        bool before = await a.Settle(() => v.TargetComplete, 60_000);
        var b = ViewerPixels(a);
        d.ShowingBefore = false;
        bool after = await a.Settle(() => v.TargetComplete, 60_000);
        var af = ViewerPixels(a);
        var diff = Diff(b, af);
        var afterLeft = ViewerPixels(a, leftOfCentre: true);
        var split = Diff(splitLeft, afterLeft);
        Say($"   {quality} {zoom}: toggle: before complete {before}, after complete {after}; before holds {Levels(a, 1)}; before vs after: mean {diff.Mean:0.000}, max {diff.Max}; "
            + $"split's before side vs after: mean {split.Mean:0.000}, max {split.Max}");
        if (!before || diff.Max > 2) problems.Add($"{quality} {zoom}: before differs from after (mean {diff.Mean:0.000}, max {diff.Max})");
        if (split.Max > 2) problems.Add($"{quality} {zoom}: the split's before side differs from after (mean {split.Mean:0.000}, max {split.Max})");
        d.SetModeCommand.Execute(DevelopMode.Single);
        await a.Settle(() => true, 300);
        return diff;
    }

    async Task CompareAtZooms(App a, string quality, List<string> problems)
    {
        if (Environment.GetEnvironmentVariable("LUX_REAL_UI_QUALITY_ONLY") == "1") return;
        a.Vm.Develop.ZoomFitCommand.Execute(null);
        await CompareSides(a, quality, "fit", problems);
        a.Vm.Develop.ZoomActualCommand.Execute(null);
        await CompareSides(a, quality, "1to1", problems);
        a.Vm.Develop.ZoomFitCommand.Execute(null);
    }

    /// <summary>What the selector, the status bar and the render badge say at fit, half size and 1:1.</summary>
    async Task Zooms(App a, string when)
    {
        var d = a.Vm.Develop; var v = a.Viewer;
        foreach (var (label, act) in new (string, Action)[] { ("fit", () => d.ZoomFitCommand.Execute(null)), ("50 %", () => v.ZoomToPercent(50)), ("1:1", () => d.ZoomActualCommand.Execute(null)) })
        {
            act();
            await a.Settle(() => v.TargetComplete, 120_000);
            await a.Settle(() => true, 1500);
            Say($"   {when}, {label}: viewer target level {v.TargetLevel}, on screen {v.LevelOnScreen} · status \"{a.Vm.StatusAnalysis}\" · badge {(d.IsRenderBusy ? $"\"{d.RenderProgressText}\"" : "(none)")} · develop selector {Selector(d.Quality)}");
        }
        d.ZoomFitCommand.Execute(null);
        await a.Settle(() => true, 500);
    }

    /// <summary>Quality changes chosen on the Library page, followed on the Develop page; before / after with no edits at each quality.</summary>
    [AvaloniaFact]
    public async Task Quality_changes_and_before_after()
    {
        if (Capture == null) { output.WriteLine("LUX_REAL_UI_CAPTURE not set: skipped"); return; }
        _file = "quality.txt";
        await using var a = await App.OpenAsync("quality", withCache: false, Say);
        var vm = a.Vm; var d = vm.Develop;
        var problems = new List<string>();
        Say($"opened: kept {a.Item.Item.BestQuality}");
        await a.OpenInDevelopAsync();
        await CompareAtZooms(a, "preview", problems);

        foreach (var (target, name) in new[] { (ViewQuality.Linear13, "13mp"), (ViewQuality.FullDetail, "full") })
        {
            // ---- chosen in the Library's details panel, then straight to Develop
            vm.CurrentPage = Page.Library;
            vm.Library.Click(a.Item, false, false);
            await a.Settle(() => true, 500);
            vm.Library.Details.Quality.ChooseCommand.Execute(target);
            await a.Settle(() => vm.Library.Details.Quality.IsPending || vm.Library.Details.Quality.Notice != null, 15_000);
            if (vm.Library.Details.Quality.Notice != null) vm.Library.Details.Quality.ConfirmAskCommand.Execute(null);
            Say($"{name} chosen in the library: details selector {Selector(vm.Library.Details.Quality)}");
            vm.CurrentPage = Page.Develop;
            var sw = Stopwatch.StartNew();
            bool shotRunning = false;
            string? last = null;
            JobInfo? Job() => a.S.Library.Jobs.GetJobs().Where(j => j.CaptureId == a.Id && j.Kind is JobKind.Analysis or JobKind.CacheBuild or JobKind.CacheUpgrade).OrderByDescending(j => j.Id).FirstOrDefault();
            int tick = 0;
            while (sw.Elapsed < TimeSpan.FromMinutes(10))
            {
                // the dispatcher runs as often as in the app (events are handled within milliseconds of their arrival)
                Dispatcher.UIThread.RunJobs();
                if (tick++ % 20 == 0) a.Win.RenderFrame();
                var j = Job();
                var shown = OnScreen(a);
                string now = $"job {j?.Kind} {j?.State} {j?.Progress:0.00} · develop selector on screen: {shown.Selected}, tick 13 MP {shown.Tick13}, tick full {shown.TickFull}, label \"{shown.Label}\" · item {a.Item.Quality.Cached}->{a.Item.Quality.Target}";
                if (now != last && (last == null || !now.Split('·')[1].Equals(last.Split('·')[1]) || j?.IsFinished == true)) { Say($"   {sw.Elapsed.TotalSeconds,5:0.0} s {now}"); last = now; }
                if (!shotRunning && j is { State: JobState.Running } && j.Progress > 0.2)
                {
                    a.Shot($"quality-{name}-being-made");
                    shotRunning = true;
                    if (!shown.Label.Contains("is being made", StringComparison.Ordinal)) problems.Add($"{name}: while it is being made the selector does not say so (\"{shown.Label}\")");
                }
                if (j is { IsFinished: true }) break;
                await Task.Delay(5);
            }
            // a few seconds after the job: the steady state
            await a.Settle(() => true, 5000, quietFrames: 80);
            var after = OnScreen(a);
            Say($"{name}: 5 s after the job: develop selector on screen: {after.Selected}, tick 13 MP {after.Tick13}, tick full {after.TickFull}, label \"{after.Label}\"; "
                + $"details selector {Selector(vm.Library.Details.Quality)}; item {a.Item.Quality} · kept {a.S.Library.Get(a.Id)!.BestQuality}");
            a.Shot($"quality-{name}-after-job");
            bool stale = after.Tick13 || after.TickFull || after.Selected != (target == ViewQuality.FullDetail ? "Full detail" : "13 MP") || after.Label != "Quality";
            if (stale) problems.Add($"{name}: the Develop selector still shows a change after the job ({after})");
            await Zooms(a, $"{name} kept");
            await CompareAtZooms(a, name, problems);
        }
        // Photo + depth: two viewers over the same photo (recorded, not checked)
        if (Environment.GetEnvironmentVariable("LUX_REAL_UI_QUALITY_ONLY") != "1")
        {
            d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
            d.ZoomActualCommand.Execute(null);
            var depthViewer = a.Develop.DepthPhotoViewer;
            bool both = await a.Settle(() => a.Viewer.TargetComplete && depthViewer.TargetComplete, 60_000);
            Say($"photo + depth at 1:1: both complete {both}; photo target {a.Viewer.TargetLevel} on screen {a.Viewer.LevelOnScreen} holds {Levels(a, 0)}; "
                + $"depth target {depthViewer.TargetLevel} on screen {depthViewer.LevelOnScreen} holds {string.Join(", ", depthViewer.CacheOf(0).Entries.GroupBy(e => e.Key.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key} ×{g.Count()}"))}");
            a.Shot("compare-photo-depth-1to1");
            d.SetModeCommand.Execute(DevelopMode.Single);
        }
        Say(problems.Count == 0 ? "all checks pass" : $"{problems.Count} wrong: {string.Join("; ", problems)}");
        Assert.Empty(problems);
    }

    // ================================================================================================================ depth and the kept data

    /// <summary>The newest unfinished job of the capture, or null.</summary>
    static JobInfo? OpenJob(App a) => a.S.Library.Jobs.GetJobs().Where(j => j.CaptureId == a.Id && !j.IsFinished && j.Kind != JobKind.Thumbnail).OrderByDescending(j => j.Id).FirstOrDefault();

    /// <summary>Wait until the capture has no unfinished job (the page keeps running meanwhile).</summary>
    async Task WaitForJobs(App a, string what)
    {
        var sw = Stopwatch.StartNew();
        await a.Settle(() => OpenJob(a) != null, 5000, quietFrames: 1);
        bool done = await a.Settle(() => OpenJob(a) == null, 600_000, quietFrames: 1);
        var last = a.S.Library.Jobs.GetJobs().Where(j => j.CaptureId == a.Id && j.Kind != JobKind.Thumbnail).OrderByDescending(j => j.Id).FirstOrDefault();
        Say($"{what}: job {last?.Kind} {last?.State} after {sw.Elapsed.TotalSeconds:0.0} s{(done ? "" : " (still running)")}");
    }

    /// <summary>What the Develop page says about the depth now.</summary>
    static string DepthState(App a)
    {
        var d = a.Vm.Develop; var item = a.S.Library.Get(a.Id)!;
        int dimmed = d.Tools.Count(t => t.IsDimmed);
        return $"item kept {item.CacheLevel} ({item.DepthState}) · depth card {d.ShowDepthCard} \"{d.Depth.ReadinessTitle}\" · ready {d.Depth.IsReady} · "
             + $"dimmed tools {dimmed} · aperture enabled {d.Depth.Aperture.IsEnabled} · in focus \"{d.Depth.InFocusText}\"";
    }

    /// <summary>Choose a quality on the Develop page's own selector (the storage notice confirmed) and wait for its job.</summary>
    async Task ChooseOnDevelop(App a, ViewQuality q, string name)
    {
        var sel = a.Vm.Develop.Quality;
        await sel.ChooseCommand.ExecuteAsync(q);
        await a.Settle(() => sel.IsPending || sel.Notice != null || OpenJob(a) != null, 15_000, quietFrames: 1);
        if (sel.Notice is { IsUpgrade: true }) sel.ConfirmAskCommand.Execute(null);
        else if (sel.Notice != null) sel.ConfirmReleaseCommand.Execute(null);
        await WaitForJobs(a, $"{name} chosen on the Develop page");
        await a.Settle(() => true, 5000, quietFrames: 80);
    }

    /// <summary>A depth map made while the photo is open — by a quality chosen on the Develop page, not by the depth card — reaches the open
    /// photo: the card goes, the depth tools and the depth effect work and Photo + depth shows the depth, with the unsaved edit kept. Then
    /// the kept data is released to none while the photo is open: the page keeps the depth it holds and that depth still works.</summary>
    [AvaloniaFact]
    public async Task Depth_follows_the_kept_data_while_the_photo_is_open()
    {
        if (Capture == null) { output.WriteLine("LUX_REAL_UI_CAPTURE not set: skipped"); return; }
        _file = "depth-follows.txt";
        await using var a = await App.OpenAsync("depth-follows", withCache: false, Say);
        var vm = a.Vm; var d = vm.Develop;
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); Say((ok ? "   ok: " : "   WRONG: ") + what); }
        await a.OpenInDevelopAsync();
        await a.Settle(() => true, 2000, quietFrames: 20);
        Say("opened: " + DepthState(a));
        Expect(d.ShowDepthCard && !d.Depth.IsReady, "at open the depth card says the depth map is not made yet");

        // an unsaved edit and its history step
        d.Session!.SetValue(Lux.App.Develop.AdjustmentKeys.Exposure, 0.5);
        await a.Settle(() => true, 800);
        int steps = d.Session.History.Count;
        Say($"exposure set to {d.Session.Get(Lux.App.Develop.AdjustmentKeys.Exposure):0.00}; history {steps} rows; can undo {d.CanUndo}");
        a.Shot("depth-follows-1-open");

        // 13 MP from the Develop page's selector: the analysis job makes the depth map too
        await ChooseOnDevelop(a, ViewQuality.Linear13, "13 MP");
        Say("after the 13 MP job: " + DepthState(a));
        a.Shot("depth-follows-2-after-13mp");
        Expect(!d.ShowDepthCard && d.Depth.IsReady, "after the job the card is gone and the depth is ready");
        Expect(d.Tools.All(t => !t.IsDimmed), "after the job no tool is dimmed");
        Expect(d.Depth.Aperture.IsEnabled, "after the job the aperture slider is enabled");
        Expect(Math.Abs(d.Session.Get(Lux.App.Develop.AdjustmentKeys.Exposure) - 0.5) < 1e-6 && d.Session.History.Count == steps && d.CanUndo,
               $"the unsaved exposure and its step stay (exposure {d.Session.Get(Lux.App.Develop.AdjustmentKeys.Exposure):0.00}, history {d.Session.History.Count} rows)");

        // Photo + depth shows the depth
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        await a.Settle(() => true, 600, quietFrames: 5);
        d.ZoomFitCommand.Execute(null);
        var dv = a.Develop.DepthPhotoViewer;
        bool depthShown = await a.Settle(() => dv.TargetComplete && dv.LevelOnScreen >= 0, 60_000);
        Say($"photo + depth: depth viewer complete {dv.TargetComplete}, level on screen {dv.LevelOnScreen}, holds {string.Join(", ", dv.CacheOf(0).Entries.GroupBy(e => e.Key.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key} ×{g.Count()}"))}");
        a.Shot("depth-follows-3-photo-depth");
        Expect(depthShown, "Photo + depth shows the depth map");
        d.SetModeCommand.Execute(DevelopMode.Single);
        await a.Settle(() => true, 500);

        // the depth effect
        d.Depth.Aperture.EditText = "2.8";
        bool committed = d.Depth.Aperture.CommitText();
        await a.Settle(() => d.Depth.IsEffectOn && a.Viewer.TargetComplete, 120_000);
        Say($"aperture f/2.8 committed {committed}: effect on {d.Depth.IsEffectOn}, in focus \"{d.Depth.InFocusText}\", picture complete {a.Viewer.TargetComplete}");
        a.Shot("depth-follows-4-effect");
        Expect(committed && d.Depth.IsEffectOn, "the depth effect can be set");

        // the reverse: the kept data released to none while the photo is open
        var ids = a.S.Library.SetCacheLevel(new[] { a.Id }, CacheLevel.None);
        Say($"release to none: {ids.Count} job(s)");
        await WaitForJobs(a, "release to none");
        await a.Settle(() => true, 3000, quietFrames: 40);
        Say("after the release: " + DepthState(a));
        var session = d.Depth.Session!;
        double? depthAt = session.DepthAt(new Lux.Library.NormPoint(0.5, 0.5));
        d.Depth.Aperture.EditText = "4";
        d.Depth.Aperture.CommitText();
        bool effectRenders = await a.Settle(() => a.Viewer.TargetComplete, 120_000);
        var focus = await session.FocusAtAsync(new Lux.Library.NormPoint(0.5, 0.5));
        Say($"after the release: depth at the center {depthAt?.ToString("0") ?? "none"} mm; f/4 rendered {effectRenders}; focus click ok {focus.Ok} ({focus.Reason})");
        d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
        await a.Settle(() => true, 600, quietFrames: 5);
        bool depthStill = await a.Settle(() => dv.TargetComplete && dv.LevelOnScreen >= 0, 60_000);
        a.Shot("depth-follows-5-after-release");
        d.SetModeCommand.Execute(DevelopMode.Single);
        Expect(!d.Depth.IsReady || (depthAt != null && effectRenders && focus.Ok && depthStill),
               "after the release the page claims depth only while that depth still works");
        Say(problems.Count == 0 ? "all checks pass" : $"{problems.Count} wrong: {string.Join("; ", problems)}");
        Assert.Empty(problems);
    }

    /// <summary>The levels a viewer's main channel holds at its current version ("L1 ×12, L4 ×1").</summary>
    static string Holds(Lux.App.Viewer.PhotoViewer v)
    {
        long cur = v.Source?.Version(0) ?? 0;
        return string.Join(", ", v.CacheOf(0).Entries.Where(e => e.Version == cur).GroupBy(e => e.Key.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key} ×{g.Count()}"));
    }

    /// <summary>Photo + depth: both viewers reach their target level at fit and at 1:1, at each quality with no edits, and with the depth
    /// effect on (the photo's tiles then take longer). The requests sent while nothing is missing are recorded.</summary>
    [AvaloniaFact]
    public async Task Photo_and_depth_both_complete_at_each_quality()
    {
        if (Capture == null) { output.WriteLine("LUX_REAL_UI_CAPTURE not set: skipped"); return; }
        _file = "photo-depth.txt";
        await using var a = await App.OpenAsync("photo-depth", withCache: false, Say);
        var vm = a.Vm; var d = vm.Develop;
        var problems = new List<string>();
        // the depth is made before the photo is opened (the photo reads it at open)
        var ids = a.S.Library.AnalyseNow(new[] { a.Id });
        Say($"analysis: {ids.Count} job(s)");
        await WaitForJobs(a, "analysis");
        a.S.Library.RequestQuality(a.Id, ViewQuality.Preview);
        await WaitForJobs(a, "preview (13 MP released)");
        Say($"kept {a.S.Library.Get(a.Id)!.CacheLevel}");
        await a.OpenInDevelopAsync();
        await a.Settle(() => d.Depth.IsReady, 60_000);
        Say("opened: " + DepthState(a));
        var pv = a.Viewer; var dv = a.Develop.DepthPhotoViewer;
        foreach (var (q, name) in new[] { (ViewQuality.Preview, "preview"), (ViewQuality.Linear13, "13mp"), (ViewQuality.FullDetail, "full") })
        {
            if (q != ViewQuality.Preview) await ChooseOnDevelop(a, q, name);
            d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
            await a.Settle(() => dv.IsVisible && dv.Bounds.Width > 0, 5000, quietFrames: 5);
            dv.AnimateZoom = false;
            foreach (var zoom in new[] { "fit", "1to1" })
            {
                if (zoom == "fit") d.ZoomFitCommand.Execute(null); else d.ZoomActualCommand.Execute(null);
                int r0 = Lux.App.Develop.ContractTileSource.RequestCount;
                var sw = Stopwatch.StartNew();
                bool both = await a.Settle(() => pv.TargetComplete && dv.TargetComplete, 90_000);
                double secs = sw.Elapsed.TotalSeconds;
                await a.Settle(() => true, 1500, quietFrames: 20);
                Say($"{name} {zoom}: both complete {both} after {secs:0.0} s ({Lux.App.Develop.ContractTileSource.RequestCount - r0} requests) · zoom {pv.ZoomPercent:0} % / {dv.ZoomPercent:0} % · "
                    + $"photo target {pv.TargetLevel} on screen {pv.LevelOnScreen} complete {pv.TargetComplete} ({pv.TargetSlotsReady}/{pv.TargetSlots}) holds {Holds(pv)} · "
                    + $"depth target {dv.TargetLevel} on screen {dv.LevelOnScreen} complete {dv.TargetComplete} ({dv.TargetSlotsReady}/{dv.TargetSlots}) holds {Holds(dv)}");
                a.Shot($"photo-depth-{name}-{zoom}");
                if (!pv.TargetComplete || pv.LevelOnScreen != pv.TargetLevel) problems.Add($"{name} {zoom}: the photo side does not reach level {pv.TargetLevel} (on screen {pv.LevelOnScreen})");
                if (!dv.TargetComplete || dv.LevelOnScreen != dv.TargetLevel) problems.Add($"{name} {zoom}: the depth side does not reach level {dv.TargetLevel} (on screen {dv.LevelOnScreen})");
                if (zoom == "1to1" && pv.TargetLevel != (q == ViewQuality.FullDetail ? 0 : q == ViewQuality.Linear13 ? 1 : 2)) problems.Add($"{name} 1:1: the photo's target level is {pv.TargetLevel}");
            }
            d.SetModeCommand.Execute(DevelopMode.Single);
            d.ZoomFitCommand.Execute(null);
            await a.Settle(() => true, 500);
        }

        // requests while nothing is missing, in each mode (recorded)
        foreach (var mode in new[] { DevelopMode.Single, DevelopMode.PhotoDepth })
        {
            d.SetModeCommand.Execute(mode);
            await a.Settle(() => pv.TargetComplete && (mode == DevelopMode.Single || dv.TargetComplete), 60_000);
            await a.Settle(() => true, 1000, quietFrames: 10);
            int r0 = Lux.App.Develop.ContractTileSource.RequestCount;
            await a.Settle(() => false, 2000);
            Say($"{mode}, nothing missing: {Lux.App.Develop.ContractTileSource.RequestCount - r0} requests in 2 s");
        }

        // the depth effect on: the photo side's tiles take longer
        d.Depth.Aperture.EditText = "2.8";
        d.Depth.Aperture.CommitText();
        foreach (var zoom in new[] { "fit", "1to1" })
        {
            if (zoom == "fit") d.ZoomFitCommand.Execute(null); else d.ZoomActualCommand.Execute(null);
            int r0 = Lux.App.Develop.ContractTileSource.RequestCount;
            var sw = Stopwatch.StartNew();
            bool both = await a.Settle(() => pv.TargetComplete && dv.TargetComplete, 120_000);
            Say($"f/2.8 {zoom}: both complete {both} after {sw.Elapsed.TotalSeconds:0.0} s ({Lux.App.Develop.ContractTileSource.RequestCount - r0} requests) · "
                + $"photo target {pv.TargetLevel} on screen {pv.LevelOnScreen} ({pv.TargetSlotsReady}/{pv.TargetSlots}) holds {Holds(pv)} · depth target {dv.TargetLevel} on screen {dv.LevelOnScreen} ({dv.TargetSlotsReady}/{dv.TargetSlots}) holds {Holds(dv)}");
            a.Shot($"photo-depth-effect-{zoom}");
            if (!both) problems.Add($"f/2.8 {zoom}: the two sides do not both complete");
        }
        d.SetModeCommand.Execute(DevelopMode.Single);
        Say(problems.Count == 0 ? "all checks pass" : $"{problems.Count} wrong: {string.Join("; ", problems)}");
        Assert.Empty(problems);
    }
}
