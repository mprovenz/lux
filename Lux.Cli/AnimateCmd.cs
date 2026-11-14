using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Cli;

/// <summary>
/// `animate &lt;lri…&gt;` — the animated effects the desktop app makes, from the same engine code (<see cref="AnimationRenderer"/>) and the
/// same writers (<see cref="AnimationEncoder"/>; <see cref="VideoEncoder"/> through ffmpeg for MP4 / WebM when it is installed): a
/// wigglegram, a parallax sweep, a rack focus or an aperture sweep with Lumen's depth effect, or a tour of every lens. The depth-effect frames
/// are exactly the stills `convert --f-number … --focus-depth …` writes at the same level and size.
/// </summary>
internal static class AnimateCmd
{
    static readonly (string Name, AnimationEffect Effect)[] Effects =
    {
        ("wigglegram", AnimationEffect.Wigglegram), ("parallax", AnimationEffect.ParallaxSweep), ("rack-focus", AnimationEffect.RackFocus),
        ("aperture-sweep", AnimationEffect.ApertureSweep), ("lens-tour", AnimationEffect.LensTour),
    };

    public static string NameOf(AnimationEffect e) => Effects.First(x => x.Effect == e).Name;

    public static int Run(List<string> files, Options o)
    {
        if (o.AnimEffect is null) { Console.Error.WriteLine("error: animate needs --anim-effect (wigglegram, parallax, rack-focus, aperture-sweep or lens-tour)"); return 2; }
        var hit = Effects.FirstOrDefault(x => x.Name == o.AnimEffect);
        if (hit.Name is null) { Console.Error.WriteLine($"error: --anim-effect must be one of {string.Join(", ", Effects.Select(x => x.Name))}"); return 2; }
        string container = o.AnimContainer ?? "gif";
        bool video = container is "mp4" or "webm";
        if (!video && !AnimationEncoder.TryParse(container, out _)) { Console.Error.WriteLine("error: --anim-container must be gif, apng, webp, mp4 or webm"); return 2; }
        string? ffmpeg = video ? VideoEncoder.FindFfmpeg() : null;
        if (video && ffmpeg is null) { Console.Error.WriteLine("error: MP4 and WebM are written by ffmpeg, which is not installed (GIF, APNG and WebP need nothing extra). Nothing has been written."); return 2; }
        if (o.AnimLoop is not null and not ("pingpong" or "forward")) { Console.Error.WriteLine("error: --anim-loop must be pingpong or forward"); return 2; }
        if (o.AnimPath is not null and not ("sweep" or "arc" or "line" or "orbit")) { Console.Error.WriteLine("error: --anim-path must be sweep, arc, line or orbit"); return 2; }
        if (o.AnimGifDither is not null and not ("ordered" or "diffusion" or "none")) { Console.Error.WriteLine("error: --anim-gif-dither must be ordered, diffusion or none"); return 2; }
        if (o.AnimGifPalette is not null and not ("global" or "frame")) { Console.Error.WriteLine("error: --anim-gif-palette must be global or frame"); return 2; }
        if (o.OutFile is not null) { Console.Error.WriteLine("error: animate names its files <stem>_<effect>.<ext>; use -o <dir>"); return 2; }
        var spec = new AnimationSpec
        {
            Effect = hit.Effect, Frames = o.AnimFrames ?? 24, FrameMs = o.AnimMs ?? 0, PingPong = o.AnimLoop is null ? null : o.AnimLoop == "pingpong",
            LongEdge = o.AnimSize ?? 1040, Level = o.Given.Contains("--level") ? o.Level : null,
            FNumber = o.DepthFNumber ?? 2.0f, FNumberFrom = o.AnimFFrom,
            FocusFromMm = o.AnimFocus is { } af ? (float)(af.M1 * 1000) : null, FocusToMm = o.AnimFocus is { } at ? (float)(at.M2 * 1000) : null,
            FocusMm = o.FocusDepth,
            Path = o.AnimPath switch { "arc" => AnimationPath.Arc, "line" => AnimationPath.Line, "orbit" => AnimationPath.Orbit, _ => AnimationPath.Sweep },
            BaselineMm = o.AnimBaseline ?? 71.49, Converge = o.AnimConverge ?? "auto", SingleView = o.AnimSingleView,
            UseSavedEdits = !o.NoEdits, EditsVariant = o.Edits,
        };
        var enc = new AnimationEncodeOptions
        {
            Loops = o.AnimRepeat ?? 0, FrameDifferencing = !o.AnimNoDiff,
            GifDither = o.AnimGifDither switch { "none" => GifDither.None, "diffusion" => GifDither.Diffusion, _ => GifDither.Ordered },
            GifPalette = o.AnimGifPalette == "frame" ? GifPalette.PerFrame : GifPalette.Global,
        };
        int threads = o.RenderThreads ?? Environment.ProcessorCount;
        Lux.Engine.Pipeline.BayerFusion.BlockFlow.Threads = threads;
        Console.WriteLine($"lux-light animate: {files.Count} file(s), {hit.Name}, {container} -> {o.OutDirectory ?? "<beside input>/lux_animate"}");
        int failed = 0;
        foreach (var path in files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string outDir = o.OutDirectory ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", "lux_animate");
            var sw = Stopwatch.StartNew();
            try
            {
                Action<string>? log = Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1" ? m => Console.Error.WriteLine($"  [{stem}] {m}") : null;
                using var s = CaptureSession.Open(path, new SessionOptions
                {
                    Context = new Lux.Store.StoreCaptureContext(path), Threads = threads, KeepGeometry = false, UseCache = !o.NoCache, DisplayCacheBytes = 0, Log = log,
                });
                var fileSpec = spec;
                if (o.Upright)
                {
                    // as `convert --upright` (and the desktop app's default): the held turn of a photo whose crop / rotation was never edited;
                    // the lens and parallax frames are turned the same way (or by a saved rotation)
                    var ctx = new Lux.Store.StoreCaptureContext(path);
                    var (ur, note) = ConvertCmd.Upright(path, new ExportRequest { Formats = Array.Empty<Lux.Engine.Pipeline.Export.ExportImageFormat>(), UseSavedEdits = spec.UseSavedEdits }, ctx);
                    Console.WriteLine($"    {note}");
                    if (ur.Transform is { } ut) fileSpec = spec with { Transform = ut, OutputTurn = Lux.Store.HeldTurn.Degrees(s.Lri) };
                    else if (spec.UseSavedEdits && Lux.Store.SavedDepthEdits.LoadParts(ctx.Context) is var (v, rc) && Lux.Store.CaptureRecipe.Merge(v, rc)?.Transform is { } st)
                        fileSpec = spec with { OutputTurn = ConvertCmd.TurnOf(st.Matrix) };
                }
                var r = AnimationRenderer.CreateAsync(s, fileSpec, log).GetAwaiter().GetResult();
                var plan = r.Plan;
                Console.WriteLine($"  {stem}: {plan.Frames.Count} frames, {plan.Order.Count} played, {plan.Size.W}x{plan.Size.H}"
                                  + (plan.Level >= 0 ? $", from level {plan.Level}" : "") + $", {plan.Duration.TotalSeconds:0.##} s ({sw.Elapsed.TotalSeconds:F1}s to plan)");
                foreach (var n in plan.Notes) Console.WriteLine($"    {n}");
                var swr = Stopwatch.StartNew();
                var frames = new Rgba[plan.Frames.Count];
                for (int k = 0; k < frames.Length; k++)
                {
                    frames[k] = r.Render(k);
                    Progress.Line(k + 1, frames.Length, $"{stem}  frame {plan.Frames[k].Label}");
                }
                double renderS = swr.Elapsed.TotalSeconds;
                var played = plan.Order.Select(i => frames[i]).ToList();
                Directory.CreateDirectory(outDir);
                string ext = video ? VideoEncoder.Extension(container == "mp4" ? VideoContainer.Mp4 : VideoContainer.WebM)
                                   : AnimationEncoder.Extension(AnimationEncoder.TryParse(container, out var cc) ? cc : AnimationContainer.Gif);
                string file = Path.Combine(outDir, $"{stem}_{hit.Name}{ext}");
                var swe = Stopwatch.StartNew();
                long bytes; AnimationEncodeResult? er = null;
                if (video)
                    bytes = VideoEncoder.Write(file, container == "mp4" ? VideoContainer.Mp4 : VideoContainer.WebM, played, plan.DelaysMs[0], o.AnimRepeat ?? 0, default, ffmpeg);
                else
                {
                    AnimationEncoder.TryParse(container, out var c);
                    er = AnimationEncoder.WriteFile(file, c, played, plan.DelaysMs, enc, keepDisplayedFrames: o.AnimKeepFrames is not null);
                    bytes = er.Bytes;
                }
                double encS = swe.Elapsed.TotalSeconds;
                if (o.AnimKeepFrames is { } kd) KeepFrames(kd, stem, hit.Name, r, frames, played, er, enc, container);
                Console.WriteLine($"  {stem} -> {Path.GetFileName(file)} ({bytes / 1e6:F2} MB; frames {renderS:F1}s, encode {encS:F2}s, total {sw.Elapsed.TotalSeconds:F1}s)");
            }
            catch (Exception ex) when (ex is ExportRequestException or InvalidOperationException or IOException)
            {
                failed++;
                Console.Error.WriteLine($"  {stem}  FAILED: {ex.Message}");
                if (Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1") Console.Error.WriteLine(ex);
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>`--anim-keep-frames`: each distinct frame as a PNG (exact), the plan as JSON (the `convert` options of each depth-effect frame
    /// among them), and the frames a viewer shows with their timing (raw RGBA + JSON, for an independent decoder to compare against).</summary>
    static void KeepFrames(string dir, string stem, string effect, AnimationRenderer r, Rgba[] frames, IReadOnlyList<Rgba> played,
                           AnimationEncodeResult? er, AnimationEncodeOptions enc, string container)
    {
        Directory.CreateDirectory(dir);
        for (int k = 0; k < frames.Length; k++) Png.Write(Path.Combine(dir, $"{stem}_{effect}_{k:D3}.png"), frames[k]);
        bool depthEffect = r.Spec.Effect is AnimationEffect.RackFocus or AnimationEffect.ApertureSweep;
        File.WriteAllText(Path.Combine(dir, $"{stem}_{effect}.plan.json"), JsonSerializer.Serialize(new
        {
            effect, size = new[] { r.Plan.Size.W, r.Plan.Size.H }, level = r.Plan.Level, order = r.Plan.Order, delays_ms = r.Plan.DelaysMs,
            frames = r.Plan.Frames.Select(f => new
            {
                f.Index, f.Label, f.FNumber, f.FocusMm, f.Module,
                convert = depthEffect ? r.ConvertArgs(f.Index) : null,
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (er?.DisplayedFrames is { } shown)
        {
            using (var fs = File.Create(Path.Combine(dir, $"{stem}_{effect}.frames"))) foreach (var f in shown) fs.Write(f.P);
            File.WriteAllText(Path.Combine(dir, $"{stem}_{effect}.json"), JsonSerializer.Serialize(new
            {
                w = played[0].W, h = played[0].H, n = played.Count, delays_ms = er.WrittenDelaysMs, loops = enc.Loops, container = container == "png" ? "apng" : container,
            }));
        }
    }
}
