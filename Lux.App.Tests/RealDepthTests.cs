using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;
using VersionKind = Lux.App.Develop.VersionKind;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The depth interface on the real library and its depth backend (DepthContracts.cs): the depth effect at f/2 with a live aperture drag, the
/// focus tool with the focal-region shade, Photo + depth, every tool applied through real pointer events, the quick-select selection, and a
/// Lumen import for one capture. Runs only when LUX_REAL_DEPTH is set. Every run starts clean: the linked library and the app data in the
/// third UI output folder (<c>Paths.M3Scratch</c>) are made again (links to L16_00493 and to a Lumen library capture, L16_00066, with its
/// session; analysis caches copied from the backend's test library); nothing is written next to the sources. Screens and timing figures go
/// to the same folder.
/// </summary>
public sealed class RealDepthTests
{
    readonly ITestOutputHelper _out;
    public RealDepthTests(ITestOutputHelper output) => _out = output;

    static string Workspace => Path.GetFullPath(Path.Combine(Paths.M3Scratch, "..", "..", ".."));

    [AvaloniaFact]
    public async Task Real_depth_page()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LUX_REAL_DEPTH"))) { _out.WriteLine("LUX_REAL_DEPTH not set: skipped"); return; }
        string m3 = Path.GetFullPath(Paths.M3Scratch);
        var folder = Path.Combine(m3, "lib", "real");
        var appData = Path.Combine(m3, "appdata", "real");
        foreach (var dir in new[] { folder, appData })
            if (Directory.Exists(dir)) { Assert.StartsWith(m3, Path.GetFullPath(dir)); Directory.Delete(dir, recursive: true); }   // links are removed, never followed
        Directory.CreateDirectory(Path.Combine(folder, "LuxImageContext", "cache"));
        Directory.CreateDirectory(appData);
        // the captures: a sample capture (read through a link) and a Lumen library capture with depth edits made in Lumen
        string Target(string link) { var f = new FileInfo(link); return f.LinkTarget is { } t ? Path.GetFullPath(t, Path.GetDirectoryName(link)!) : f.FullName; }
        var sources = new List<string>();
        var corpus = Path.Combine(Workspace, "scratch", "m2", "lib", "edit", "L16_00493.lri");
        if (File.Exists(corpus)) sources.Add(Target(corpus));
        var lumenLri = Path.Combine(Workspace, "assets", "lumen-library-2025", "2025-03-29", "L16_00066.lri");
        if (File.Exists(lumenLri)) { sources.Add(lumenLri); sources.Add(Path.ChangeExtension(lumenLri, ".lris")); }
        foreach (var src in sources) File.CreateSymbolicLink(Path.Combine(folder, Path.GetFileName(src)), src);
        foreach (var cacheDir in new[] { Path.Combine(Workspace, "scratch", "m3", "lib", "a", "LuxImageContext", "cache"), Path.Combine(Workspace, "scratch", "m2", "lib", "edit", "LuxImageContext", "cache") })
            if (Directory.Exists(cacheDir))
                foreach (var f in Directory.GetFiles(cacheDir, "*.luxcache"))
                {
                    var to = Path.Combine(folder, "LuxImageContext", "cache", Path.GetFileName(f));
                    if (!File.Exists(to)) File.Copy(f, to);
                }

        var report = new StringBuilder();
        void Log(string m) { _out.WriteLine(m); report.AppendLine(m); }
        var s = CompositionRoot.Create(new AppOptions { AppDataDir = appData }, new TestPlatform(), log: _ => { });
        Assert.False(s.IsFake);
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        win.Show();
        var shots = Paths.M3Shots;
        try
        {
            await vm.InitializeAsync();
            await s.Library.AddFolderAsync(folder);
            var pointer = Path.Combine(appData, "context-store-folder.txt");

            async Task<bool> Settle(Func<bool> until, int timeoutMs)
            {
                var t = Stopwatch.StartNew();
                int quiet = 0;
                while (t.ElapsedMilliseconds < timeoutMs)
                {
                    Dispatcher.UIThread.RunJobs();
                    win.RenderFrame();
                    quiet = until() ? quiet + 1 : 0;
                    if (quiet >= 2) return true;
                    await Task.Delay(40);
                }
                _out.WriteLine($"settle timed out after {timeoutMs} ms");
                return false;
            }

            await Settle(() => !s.Library.GetFolders().Any(f => f.Scanning) && vm.Library.TotalCount > 0, 300_000);
            await vm.Library.RefreshTreeAsync();
            vm.Library.SelectedNode = vm.Library.Tree.First(n => n.Key == "all");
            await Settle(() => !vm.Library.IsLoading, 30_000);
            var page = win.FindControl<DevelopView>("DevelopPage")!;
            var v = page.PhotoViewer;
            var d = vm.Develop;
            v.AnimateZoom = false;
            long lastRelease = 0;   // the last stroke's or click's button-up
            bool Ready() => d.Session != null && v.LevelOnScreen >= 0 && v.LevelOnScreen <= v.TargetLevel && !v.IsHolding;
            // the first whole-picture (coarsest level) tiles at a version newer than pv: what the library sends first after a change
            bool CoarseNew(long pv) => v.Pyramid is { } pyr && pyr.TilesIn(pyr.Coarsest, pyr.ImageRect).All(k => v.CacheOf(0).Get(k) is { } e && e.Version > pv);
            bool TargetAt(long nv) => Ready() && v.CacheOf(0).Entries.Where(e => e.Key.Level == v.TargetLevel).All(e => e.Version >= nv);
            /// a change: (first whole picture at the new version c, whole visible picture w), ms
            // fromRelease: the clock starts at the stroke's release (the button-up event), not at the first pointer move
            async Task<(double C, double W)> Change(Func<Task> run, Func<bool> done, int timeoutMs = 180_000, bool fromRelease = false)
            {
                long pv = d.Session!.Tiles.Version(0);
                long t0 = Stopwatch.GetTimestamp();
                double Ms() => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                double c = -1;
                await run();
                if (fromRelease) t0 = lastRelease;
                await Settle(() => { if (c < 0 && CoarseNew(pv)) c = Ms(); return done(); }, timeoutMs);
                long nv = d.Session.Tiles.Version(0);
                await Settle(() => { if (c < 0 && CoarseNew(pv)) c = Ms(); return TargetAt(nv); }, timeoutMs);
                return (c, Ms());
            }
            // a still frame: tiles that just arrived fade in over the coarser picture for the viewer's fade time, so wait it out first
            async Task<Avalonia.Media.Imaging.Bitmap> Frame()
            {
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < v.Swap.FadeMs + 80) { Dispatcher.UIThread.RunJobs(); win.RenderFrame(); await Task.Delay(20); }
                int faded = v.Stats.FadedTiles;
                win.RenderFrame();
                var f = win.CaptureRenderedFrame()!;
                Assert.Equal(faded, v.Stats.FadedTiles);   // no tile was mid-fade in the frame kept
                return f;
            }
            // a screen: tiles that just arrived finish fading in first (the frames in between are still drawn)
            async Task Shot(string name)
            {
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < v.Swap.FadeMs + 80) { Dispatcher.UIThread.RunJobs(); win.RenderFrame(); await Task.Delay(20); }
                Harness.Capture(win, name, shots);
            }
            void SideBySide(Avalonia.Media.Imaging.Bitmap before, Avalonia.Media.Imaging.Bitmap after, string name, Rect inset)
            {
                var o = v.TranslatePoint(new Point(0, 0), win)!.Value;
                var crop = new Rect(o, v.Bounds.Size);
                int w = (int)crop.Width, hgt = (int)crop.Height;
                // the whole view side by side, and under it the stroke's surroundings at twice the size
                inset = inset.Intersect(new Rect(v.Bounds.Size));
                double k = Math.Min(2, (w - 4) / inset.Width);
                int ih = (int)(inset.Height * k), iw = (int)(inset.Width * k);
                var src = inset.Translate(new Vector(o.X, o.Y));
                using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(w * 2 + 8, hgt + 8 + ih));
                using (var dc = rtb.CreateDrawingContext())
                {
                    dc.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1c1c1c")), new Rect(0, 0, w * 2 + 8, hgt + 8 + ih));
                    dc.DrawImage(before, crop, new Rect(0, 0, w, hgt));
                    dc.DrawImage(after, crop, new Rect(w + 8, 0, w, hgt));
                    var pen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#a9dcff")), 1);
                    dc.DrawRectangle(null, pen, inset);
                    dc.DrawRectangle(null, pen, inset.Translate(new Vector(w + 8, 0)));
                    dc.DrawImage(before, src, new Rect((w - iw) / 2.0, hgt + 8, iw, ih));
                    dc.DrawImage(after, src, new Rect(w + 8 + (w - iw) / 2.0, hgt + 8, iw, ih));
                }
                Directory.CreateDirectory(shots);
                rtb.Save(Path.Combine(shots, name + ".png"));
            }
            byte[] Px(Avalonia.Media.Imaging.Bitmap b)
            {
                var o = v.TranslatePoint(new Point(0, 0), win)!.Value;
                var r = new Avalonia.PixelRect((int)o.X, (int)o.Y, (int)v.Bounds.Width, (int)v.Bounds.Height);
                var buf = new byte[r.Width * r.Height * 4];
                var hd = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { b.CopyPixels(r, hd.AddrOfPinnedObject(), buf.Length, r.Width * 4); } finally { hd.Free(); }
                return buf;
            }
            /// view pixels differing by more than 6 in a channel, and their bounds (view coordinates)
            (int Count, Rect Box) Diff(Avalonia.Media.Imaging.Bitmap a, Avalonia.Media.Imaging.Bitmap b)
            {
                byte[] pa = Px(a), pb = Px(b);
                int w = (int)v.Bounds.Width, n = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                for (int i = 0; i < pa.Length; i += 4)
                    if (Math.Abs(pa[i] - pb[i]) > 6 || Math.Abs(pa[i + 1] - pb[i + 1]) > 6 || Math.Abs(pa[i + 2] - pb[i + 2]) > 6)
                    {
                        n++; int x = i / 4 % w, y = i / 4 / w;
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
                return (n, n == 0 ? default : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
            }
            Point At(NormPoint p, Avalonia.Controls.Control? viewer = null)
            {
                var pv = (Viewer.PhotoViewer)(viewer ?? v);
                return pv.TranslatePoint(pv.Transform()!.FrameToView(new Point(p.X, p.Y)), win)!.Value;
            }
            var jobTimes = new List<double>();
            var refineProgress = new List<string>();
            vm.Develop.Depth.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DepthViewModel.ActivityText) && vm.Develop.Depth.ActivityText.Length > 0) refineProgress.Add(vm.Develop.Depth.ActivityText); };
            List<double> Stroke(IReadOnlyList<NormPoint> pts, int steps = 8)
            {
                var times = new List<double>();
                win.MouseMove(At(pts[0]));
                win.MouseDown(At(pts[0]), MouseButton.Left);
                for (int i = 1; i < pts.Count; i++)
                    for (int k = 1; k <= steps; k++)
                    {
                        var p = new NormPoint(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k / steps, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k / steps);
                        long t0 = Stopwatch.GetTimestamp();
                        win.MouseMove(At(p), RawInputModifiers.LeftMouseButton);
                        times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                        long j0 = Stopwatch.GetTimestamp();
                        Dispatcher.UIThread.RunJobs();
                        jobTimes.Add(Stopwatch.GetElapsedTime(j0).TotalMilliseconds);
                    }
                lastRelease = Stopwatch.GetTimestamp();
                win.MouseUp(At(pts[^1]), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                return times;
            }
            void Key(Key k, RawInputModifiers m = RawInputModifiers.None) { v.Focus(); win.KeyPress(k, m, PhysicalKey.None, null); win.KeyRelease(k, m, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs(); }
            static double Median(List<double> x) { var o = x.OrderBy(a => a).ToList(); return o.Count == 0 ? 0 : o[o.Count / 2]; }

            // ---------------------------------------------------------------- L16_00493: open, depth ready
            var item = vm.Library.Items.First(i => i.Name == "L16_00493");
            var sw = Stopwatch.StartNew();
            vm.Library.Click(item, false, false);
            vm.OpenInDevelop(item);
            await Settle(() => d.Session != null, 120_000);
            await Settle(Ready, 120_000);
            Log($"# Real depth page — {DateTime.Now:yyyy-MM-dd HH:mm} — {item.Name}: opened in {sw.ElapsedMilliseconds} ms; depth {d.Depth.Session?.Status.Readiness}");
            Assert.NotNull(d.Session!.Depth);
            Assert.IsType<ContractDepthSession>(d.Session.Depth);
            if (!d.Depth.IsReady)
            {
                sw.Restart();
                d.Depth.AnalyseNowCommand.Execute(null);
                await Settle(() => d.Depth.IsReady, 600_000);
                Log($"depth made in {sw.ElapsedMilliseconds} ms");
            }
            Assert.True(d.Depth.IsReady);
            var depth = d.Session.Depth!;
            Log(string.Create(CultureInfo.InvariantCulture, $"effect range f/{depth.EffectRange.WidestFNumber:0.0} … f/{depth.EffectRange.AllInFocusFNumber:0.0} (off); scene {DepthText.Distance(depth.EffectRange.NearestMm)} – {DepthText.Distance(depth.EffectRange.FarthestMm)}; focusable {depth.FocusableRange}; default focus {DepthText.Distance(depth.EffectRange.DefaultFocusMm)}; history {string.Join(" | ", d.Session.History)}"));

            // ---------------------------------------------------------------- depth effect at f/2: a live drag (frame time), then f/2 typed
            var ap = d.Depth.Aperture;
            var drag = new List<double>(); var frames = new List<double>();
            int previews = 0;
            var native = ((ContractDevelopSession)d.Session).Native;
            void Count(object? _, PictureTile t) { if (t.IsDragPreview) Interlocked.Increment(ref previews); }
            native.View.TileReady += Count;
            ap.BeginDrag();
            for (int i = 1; i <= 24; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                ap.DragTo(1 - 0.04 * i);
                drag.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                Dispatcher.UIThread.RunJobs();
                long f0 = Stopwatch.GetTimestamp();
                win.RenderFrame();
                frames.Add(Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
                await Task.Delay(33);
            }
            sw.Restart();
            ap.EndDrag();
            long version = d.Session.Tiles.Version(0);
            await Settle(() => Ready() && v.CacheOf(0).Entries.Where(e => e.Key.Level == v.TargetLevel).All(e => e.Version >= version), 120_000);
            double refine = sw.Elapsed.TotalMilliseconds;
            native.View.TileReady -= Count;
            Log(string.Create(CultureInfo.InvariantCulture, $"aperture drag, 24 moves at 30 Hz: UI work per move p50 {Median(drag):0.00} ms, max {drag.Max():0.00} ms; whole frame p50 {Median(frames):0.0} ms, max {frames.Max():0.0} ms; drag-preview tiles {previews}; refined {refine:0} ms after the release ({depth.FNumber:0.0})"));
            ap.EditText = "2";
            ap.CommitText();
            sw.Restart();
            version = d.Session.Tiles.Version(0);
            await Settle(() => Ready() && v.CacheOf(0).Entries.Where(e => e.Key.Level == v.TargetLevel).All(e => e.Version >= version), 120_000);
            Log(string.Create(CultureInfo.InvariantCulture, $"f/2 typed: picture refined in {sw.ElapsedMilliseconds} ms; in focus {d.Depth.InFocusText}; history {string.Join(" | ", d.Session.History)}"));
            Assert.Equal(2.0, depth.FNumber!.Value, 1);
            win.MouseMove(At(new NormPoint(0.5, 0.55)));
            await Settle(Ready, 10_000);
            Log($"read-out: {vm.StatusPointer}");
            await Shot("real-01-depth-effect");

            // ---------------------------------------------------------------- the focus tool and the focal-region shade
            Key(Avalonia.Input.Key.F);
            Assert.True(d.Depth.ShadeOutsideFocus);
            // the nearest focusable point to the middle of the picture (the people)
            var focusAt = Enumerable.Range(1, 19).SelectMany(y => Enumerable.Range(1, 19).Select(x => new NormPoint(x / 20.0, y / 20.0)))
                .Where(depth.CanFocusAt).OrderBy(p => Math.Abs(p.X - 0.5) + Math.Abs(p.Y - 0.45)).FirstOrDefault();
            int steps = d.Session.History.Count;
            var fp = focusAt.X > 0 ? focusAt : new NormPoint(0.5, 0.6);
            var (fc, fw) = await Change(() => { win.MouseMove(At(fp)); win.MouseDown(At(fp), MouseButton.Left); lastRelease = Stopwatch.GetTimestamp(); win.MouseUp(At(fp), MouseButton.Left); return Task.CompletedTask; },
                () => d.Session.History.Count > steps || d.Depth.HasMessage, fromRelease: true);
            Log(string.Create(CultureInfo.InvariantCulture, $"focus click at ({fp.X:0.00}, {fp.Y:0.00}): {(d.Session.History.Count > steps ? d.Session.History[^1] : d.Depth.MessageText)} — first whole picture {fc:0} ms, whole view {fw:0} ms; pin {page.PinOverlay.Focus}"));
            await Shot("real-02-focus-shading");
            Key(Avalonia.Input.Key.F);
            d.Depth.ShadeOutsideFocus = false;

            // ---------------------------------------------------------------- a live focus drag (contract v3.1, R1)
            {
                var fr = d.Depth.Focus;
                var fdrag = new List<double>(); var fframes = new List<double>();
                int fprev = 0;
                void FCount(object? _, PictureTile t) { if (t.IsDragPreview) Interlocked.Increment(ref fprev); }
                native.View.TileReady += FCount;
                int fsteps = d.Session.History.Count;
                double p0 = fr.Position;
                fr.BeginDrag();
                for (int i = 1; i <= 24; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    fr.DragTo(Math.Clamp(p0 + 0.012 * i, 0, 1));
                    fdrag.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    Dispatcher.UIThread.RunJobs();
                    long f0 = Stopwatch.GetTimestamp();
                    win.RenderFrame();
                    fframes.Add(Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
                    await Task.Delay(33);
                    if (i == 16) await Shot("real-15-focus-drag");
                }
                await Settle(() => fprev > 0, 5_000);
                int stepsWhileDragging = d.Session.History.Count - fsteps;
                var (dc, dw) = await Change(() => { fr.EndDrag(); return Task.CompletedTask; }, () => d.Session.History.Count > fsteps);
                native.View.TileReady -= FCount;
                Log(string.Create(CultureInfo.InvariantCulture, $"focus drag, 24 moves at 30 Hz: the page's work per move p50 {Median(fdrag):0.00} ms, max {fdrag.Max():0.00} ms; whole frame p50 {Median(fframes):0.0} ms; drag-preview tiles {fprev}; steps while dragging {stepsWhileDragging}; after the release: {d.Session.History[^1]} at {fr.ValueText}, first whole picture {dc:0} ms, whole view {dw:0} ms"));
            }

            // ---------------------------------------------------------------- Photo + depth
            sw.Restart();
            d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
            var dv = page.DepthPhotoViewer;
            await Settle(() => dv.LevelOnScreen >= 0 && dv.LevelOnScreen <= dv.TargetLevel && Ready(), 120_000);
            Log($"photo + depth: depth view on screen (level {dv.LevelOnScreen}) in {sw.ElapsedMilliseconds} ms; in sync {dv.Scale == v.Scale && dv.Offset == v.Offset}; legend “{page.LegendOverlay.Text}”");
            win.MouseMove(At(new NormPoint(0.5, 0.55)));
            await Settle(Ready, 5_000);
            await Shot("real-03-photo-plus-depth");

            // ---------------------------------------------------------------- the tools (each applied through pointer events)
            async Task<string> Tool(string name, Func<Task> run, string shot, bool stroke = true)
            {
                int before = d.Session!.History.Count;
                long t0 = Stopwatch.GetTimestamp();
                double outcome = -1;
                var (c, w) = await Change(async () => { await run(); if (stroke) t0 = lastRelease; }, () =>
                {
                    bool r = !d.Depth.IsBusy && (d.Session.History.Count > before || d.Depth.HasMessage);
                    if (r && outcome < 0) outcome = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    return r;
                }, fromRelease: stroke);
                string result = d.Session.History.Count > before ? d.Session.History[^1] : "no step: " + d.Depth.MessageText;
                // (after a stroke the coarse tiles change with the library's stroke preview, so their first new version is not the new depth: not shown)
                Log(string.Create(CultureInfo.InvariantCulture, $"{name}: {result}; {(stroke ? "" : $"→ first whole picture {c:0} ms, ")}→ result {outcome:0} ms, → whole view {w:0} ms (from the {(stroke ? "release" : "click")}); depth jobs {string.Join(", ", d.Depth.ApplyTimes.Skip(Math.Max(0, d.Depth.ApplyTimes.Count - 2)).Select(a => $"{a.Tool} {a.Ms:0} ms {a.Result}"))}"));
                if (shot.Length > 0) await Shot(shot);
                return result;
            }
            d.SetModeCommand.Execute(DevelopMode.Single);
            await Settle(Ready, 30_000);
            // the headless input's own cost: plain pointer moves (no button, no tool) over the same page
            var hover = new List<double>();
            for (int i = 0; i < 24; i++)
            {
                long h0 = Stopwatch.GetTimestamp();
                win.MouseMove(At(new NormPoint(0.3 + 0.01 * i, 0.5)));
                hover.Add(Stopwatch.GetElapsedTime(h0).TotalMilliseconds);
                Dispatcher.UIThread.RunJobs();
            }
            Log(string.Create(CultureInfo.InvariantCulture, $"plain pointer moves (no tool, headless input incl. its frame): p50 {Median(hover):0.0} ms, max {hover.Max():0.0} ms"));
            // refine edge, the known-good stroke (backend R3): focus 3000 mm, size 10, through three capture points (converted to the picture as shown)
            d.Depth.Focus.EditText = "3.0 m";
            d.Depth.Focus.CommitText();
            await Settle(() => Math.Abs(depth.FocusMm - 3000) < 1 && Ready(), 60_000);
            var good = new[] { (0.283, 0.288), (0.272, 0.327), (0.287, 0.365) }.Select(c => native.View.CaptureToPicture(new NormPoint(c.Item1, c.Item2))!.Value).ToArray();
            Log(string.Create(CultureInfo.InvariantCulture, $"refine edge stroke in the picture as shown: {string.Join(" ", good.Select(q => $"({q.X:0.000}, {q.Y:0.000})"))}"));
            var mid = good[1];
            v.ZoomToPercent(30, animate: false);
            await Settle(Ready, 60_000);
            v.PanBy(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2) - v.Transform()!.FrameToView(new Point(mid.X, mid.Y)));
            await Settle(Ready, 120_000);
            Key(Avalonia.Input.Key.E);
            d.Depth.SizeValue = 10;
            var away = new Point(win.Bounds.Width - 6, win.Bounds.Height - 4);   // the pointer on the status bar: no brush circle, no tooltip in the still frames
            win.MouseMove(away);
            await Settle(() => TargetAt(d.Session.Tiles.Version(0)), 120_000);
            var beforeFrame = await Frame();
            var goodResult = await Tool("refine edge, the known-good stroke (zoomed 30 %)", async () => { Stroke(good, steps: 6); await Task.Yield(); }, "");
            Log(string.Create(CultureInfo.InvariantCulture, $"refine edge progress seen: {string.Join(", ", refineProgress.Distinct().Take(12))}"));
            win.MouseMove(away);
            await Settle(() => TargetAt(d.Session.Tiles.Version(0)), 120_000);
            var afterFrame = await Frame();
            var strokeView = good.Select(q => v.Transform()!.FrameToView(new Point(q.X, q.Y))).ToArray();
            var strokeBox = new Rect(new Point(strokeView.Min(q => q.X), strokeView.Min(q => q.Y)), new Point(strokeView.Max(q => q.X), strokeView.Max(q => q.Y)));
            SideBySide(beforeFrame, afterFrame, "real-14-refine-edge-applied", strokeBox.Inflate(60));
            var (n1, b1) = Diff(beforeFrame, afterFrame);
            Log(string.Create(CultureInfo.InvariantCulture, $"refine edge on screen: {n1} view pixels changed, within x {b1.X:0} y {b1.Y:0} w {b1.Width:0} h {b1.Height:0} (the stroke's points span x {strokeBox.X:0} y {strokeBox.Y:0} w {strokeBox.Width:0} h {strokeBox.Height:0}; brush size 10)"));
            // what the viewer kept (tiles outside the changed regions, R7) is what a fresh render shows; undo and redo give back the same pictures
            async Task<Avalonia.Media.Imaging.Bitmap> Fresh()
            {
                v.CacheOf(0).Clear(); v.PanBy(new Point(1, 0)); await Settle(Ready, 5_000); v.PanBy(new Point(-1, 0));
                await Settle(() => TargetAt(d.Session.Tiles.Version(0)), 120_000);
                return await Frame();
            }
            var (nf, bf) = Diff(afterFrame, await Fresh());
            int at = d.Session.HistoryPosition;
            d.UndoCommand.Execute(null);
            await Settle(() => d.Session.HistoryPosition == at - 1 && !d.Depth.IsBusy, 60_000);
            await Settle(() => TargetAt(d.Session.Tiles.Version(0)), 120_000);
            var undone = await Frame();
            var (nu, bu) = Diff(beforeFrame, undone);
            var (nuf, buf) = Diff(undone, await Fresh());
            d.RedoCommand.Execute(null);
            await Settle(() => d.Session.HistoryPosition == at && !d.Depth.IsBusy, 60_000);
            await Settle(() => TargetAt(d.Session.Tiles.Version(0)), 120_000);
            var (nr, br) = Diff(afterFrame, await Frame());
            Log($"kept tiles vs a fresh render: {nf} pixels differ after the edit, {nuf} after its undo; undo vs before the edit: {nu}; redo vs after: {nr}{(nf + nuf + nu + nr > 0 ? $" (bounds {bf} / {buf} / {bu} / {br})" : "")}");
            Assert.Equal(0, nf);
            Assert.Equal(0, nuf);
            Assert.Equal("Refine edge", goodResult);
            v.ZoomToFit();
            await Settle(Ready, 60_000);
            var strokeTimes = new List<double>();
            long req0 = ContractTileSource.RequestTicks, ref0 = DevelopViewModel.RefreshTicks; int reqN0 = ContractTileSource.RequestCount, refN0 = DevelopViewModel.RefreshCount;
            int snap0 = ContractDepthSession.Snapshots;
            int frames0 = v.Stats.RenderMs.Count;
            await Tool("refine edge, a stroke with no edge under it (the reason is shown)", async () => { strokeTimes.AddRange(Stroke(new[] { new NormPoint(0.45, 0.3), new NormPoint(0.47, 0.5), new NormPoint(0.45, 0.7) })); await Task.Yield(); }, "real-04-refine-edge");
            Key(Avalonia.Input.Key.B);
            await Tool("blend area", async () => { strokeTimes.AddRange(Stroke(new[] { new NormPoint(0.3, 0.82), new NormPoint(0.7, 0.84) })); await Task.Yield(); }, "real-05-blend-area");
            Key(Avalonia.Input.Key.M);
            var pick = Enumerable.Range(3, 5).Select(y => new NormPoint(0.5, y / 10.0)).FirstOrDefault(p => depth.DepthAt(p) != null);
            d.Depth.PickCommand.Execute(null);
            win.MouseMove(At(pick)); win.MouseDown(At(pick), MouseButton.Left); win.MouseUp(At(pick), MouseButton.Left);
            await Settle(() => d.Depth.TargetIsPicked || d.Depth.HasMessage, 10_000);
            Log($"picked the depth at ({pick.X:0.0}, {pick.Y:0.0}): {d.Depth.TargetText}");
            await Tool("match depth, brush", async () => { strokeTimes.AddRange(Stroke(new[] { new NormPoint(0.25, 0.6), new NormPoint(0.32, 0.62) })); await Task.Yield(); }, "real-06-match-brush");
            d.Depth.SetModeCommand.Execute(MatchMode.Lasso);
            foreach (var p in new[] { new NormPoint(0.62, 0.42), new NormPoint(0.78, 0.42), new NormPoint(0.78, 0.58), new NormPoint(0.62, 0.58) })
            { win.MouseMove(At(p)); win.MouseDown(At(p), MouseButton.Left); win.MouseUp(At(p), MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
            await Settle(() => d.Depth.HasPending, 30_000);
            d.Depth.TargetEdit = "3.2"; d.Depth.CommitTarget();
            await Tool("match depth, lasso", () => d.Depth.ApplyCommand.ExecuteAsync(null), "real-07-match-lasso", stroke: false);
            d.Depth.SetModeCommand.Execute(MatchMode.QuickSelect);
            d.Depth.SizeValue = 4;
            strokeTimes.AddRange(Stroke(new[] { new NormPoint(0.48, 0.5), new NormPoint(0.55, 0.52) }));
            await Settle(() => !d.Depth.IsBusy && (d.Depth.HasPending || d.Depth.HasMessage), 120_000);
            double qsSel = Stopwatch.GetElapsedTime(lastRelease).TotalMilliseconds;
            await Settle(Ready, 60_000);
            Log(string.Create(CultureInfo.InvariantCulture, $"quick select stroke: selection {d.Depth.PendingText} {qsSel:0} ms after the release, picture {Stopwatch.GetElapsedTime(lastRelease).TotalMilliseconds:0} ms {(d.Depth.HasMessage ? "· " + d.Depth.MessageText : "")}"));
            await Shot("real-08-quick-select-mask");
            d.Depth.TargetEdit = "5"; d.Depth.CommitTarget();
            await Settle(() => d.Depth.CanApply, 10_000);
            if (d.Depth.CanApply) await Tool("quick select, apply", () => d.Depth.ApplyCommand.ExecuteAsync(null), "real-09-quick-select-applied", stroke: false);
            double tick = Stopwatch.Frequency / 1000.0;
            var renders = v.Stats.RenderMs.Skip(frames0).ToList();
            Log(string.Create(CultureInfo.InvariantCulture, $"during the tool runs ({strokeTimes.Count} pointer moves): the library's RequestViewport on the UI thread {(ContractTileSource.RequestCount - reqN0)} calls, {(ContractTileSource.RequestTicks - req0) / tick:0} ms in all ({(ContractTileSource.RequestTicks - req0) / tick / Math.Max(1, ContractTileSource.RequestCount - reqN0):0.0} ms each); depth snapshots {ContractDepthSession.Snapshots - snap0}; the page's Refresh {(DevelopViewModel.RefreshCount - refN0)} times, {(DevelopViewModel.RefreshTicks - ref0) / tick:0} ms in all; the viewer's own draw p50 {Median(renders):0.0} ms over {renders.Count} frames"));
            Log(string.Create(CultureInfo.InvariantCulture, $"pointer → stroke: UI time per move (headless input incl. layout and raster) p50 {Median(strokeTimes):0.0} ms, max {(strokeTimes.Count > 0 ? strokeTimes.Max() : 0):0.0} ms; the page's own forwarding p50 {Median(d.Depth.StrokeMoveMs):0.000} ms, max {(d.Depth.StrokeMoveMs.Count > 0 ? d.Depth.StrokeMoveMs.Max() : 0):0.000} ms; queued UI work after each move p50 {Median(jobTimes):0.0} ms, max {(jobTimes.Count > 0 ? jobTimes.Max() : 0):0.0} ms"));
            Key(Avalonia.Input.Key.Escape);
            Key(Avalonia.Input.Key.Escape);
            Log($"history: {string.Join(" | ", d.Session.History)}; status {d.Depth.HistoryStatus.State}");
            await Shot("real-10-history");
            // undo / redo of the last depth edit, a version switch and back
            {
                int pos = d.Session.HistoryPosition;
                var (uc, uw) = await Change(() => { d.UndoCommand.Execute(null); return Task.CompletedTask; }, () => d.Session.HistoryPosition < pos);
                var (rc, rw) = await Change(() => { d.RedoCommand.Execute(null); return Task.CompletedTask; }, () => d.Session.HistoryPosition == pos);
                Log(string.Create(CultureInfo.InvariantCulture, $"undo → first whole picture {uc:0} ms, whole view {uw:0} ms; redo → {rc:0} ms, {rw:0} ms"));
                var clean = d.Depth.Versions.First(x => x.Info.Kind == VersionKind.Clean);
                var lux = d.Depth.Versions.First(x => x.IsCurrent);
                var (sc, sw2) = await Change(() => d.Depth.SwitchVersionCommand.ExecuteAsync(clean), () => d.Depth.CurrentVersionName == "Clean" && !d.Depth.IsBusy);
                var (bc, bw) = await Change(() => d.Depth.SwitchVersionCommand.ExecuteAsync(d.Depth.Versions.First(x => x.Info.Id == lux.Info.Id)), () => d.Depth.CurrentVersionName == lux.Name && !d.Depth.IsBusy);
                Log(string.Create(CultureInfo.InvariantCulture, $"switch to Clean → first whole picture {sc:0} ms, whole view {sw2:0} ms; back to {lux.Name} → {bc:0} ms, {bw:0} ms; versions {string.Join(", ", d.Depth.Versions.Select(x => $"{x.Name} [{x.Info.Id}]"))}"));
            }

            // ---------------------------------------------------------------- reopen: the depth steps come back (rebuilt in the background)
            await d.Session.SaveNowAsync();
            var other = vm.Library.Items.FirstOrDefault(i => i.Name == "L16_00066");
            // ---------------------------------------------------------------- L16_00066: import its Lumen session
            if (other != null)
            {
                vm.Library.Click(other, false, false);
                await d.OpenAsync(other);
                await Settle(() => d.Session != null, 120_000);
                await Settle(Ready, 300_000);
                Log($"{other.Name}: depth {d.Depth.Session?.Status.Readiness}; can import Lumen {d.Depth.CanImportLumen} (session beside it: {((ContractDevelopSession)d.Session!).Native.DepthEdit.LumenSessionPath ?? "none"}); versions {string.Join(", ", d.Depth.Versions.Select(x => x.Name))}");
                if (d.Depth.CanImportLumen)
                {
                    d.ImportLumenEditsCommand.Execute(null);
                    var dialog = Assert.IsType<LumenImportViewModel>(vm.Dialog);
                    await Settle(() => dialog.IsReady, 60_000);
                    await Shot("real-11-import-lumen-dialog");
                    sw.Restart();
                    dialog.ImportCommand.Execute(null);
                    await Settle(() => d.Depth.CurrentVersionName == "From Lumen" || d.Depth.HasMessage || (vm.Notice?.Contains("imported") ?? false), 300_000);
                    await Settle(() => !d.Depth.IsBusy, 300_000);
                    await Settle(Ready, 300_000);
                    Log($"import: {sw.ElapsedMilliseconds} ms; version {d.Depth.CurrentVersionName}; effect {d.Depth.Aperture.ValueText} at {d.Depth.Focus.ValueText}; notice {vm.Notice}");
                    d.SetModeCommand.Execute(DevelopMode.PhotoDepth);
                    await Settle(() => page.DepthPhotoViewer.LevelOnScreen >= 0 && Ready(), 300_000);
                    await Shot("real-12-lumen-imported");
                }
            }
            // ---------------------------------------------------------------- L16_00493 again: its depth steps are listed (persistent)
            vm.Library.Click(item, false, false);
            await d.OpenAsync(item);
            await Settle(() => d.Session != null, 120_000);
            await Settle(Ready, 120_000);
            Log($"reopened {item.Name}: history {string.Join(" | ", d.Session!.History)}; state {d.Depth.HistoryStatus.State} {d.Depth.RebuildText}; versions {string.Join(", ", d.Depth.Versions.Select(x => $"{x.Name} ({x.Info.Steps})"))}");
            await Shot("real-13-reopened");
            {
                var seen = new List<string>();
                var rt = Stopwatch.StartNew();
                await Settle(() => { if (d.Depth.RebuildText is { Length: > 0 } x && !seen.Contains(x)) seen.Add(x); return d.Depth.HistoryStatus.State != HistoryState.Rebuilding; }, 300_000);
                Log($"rebuild finished {rt.ElapsedMilliseconds} ms after the screen above; the line said: {string.Join(" → ", seen)}; state {d.Depth.HistoryStatus.State}");
            }

            // ---------------------------------------------------------------- the local-store setting: the pointer file follows (isolation)
            Log($"pointer file at open: {(File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "missing")}");
            Assert.True(File.Exists(pointer));
            vm.ShowSettings();
            await Settle(() => vm.Settings != null, 10_000);
            var moved = Path.Combine(appData, "contexts-moved");
            await vm.Settings!.MoveLocalFolderAsync(moved);
            await Settle(() => vm.Settings.LocalError != null || (!vm.Settings.IsMoving && vm.Settings.LocalFolder.TrimEnd('/') == moved), 120_000);
            string after = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "missing";
            Log($"local store moved from Settings to {vm.Settings.LocalFolder}{(vm.Settings.LocalError is { } le ? " — error: " + le : "")}; pointer file now: {after}");
            Assert.Equal(moved, after.TrimEnd('/'));
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Lux");
            Log($"the default app-data folder {(Directory.Exists(home) ? "EXISTS" : "was not created")}");
            Assert.False(Directory.Exists(home));
        }
        finally
        {
            Directory.CreateDirectory(m3);
            File.WriteAllText(Path.Combine(m3, "real-timings.txt"), report.ToString());
            win.Close();
            await vm.DisposeAsync();
        }
    }
}
