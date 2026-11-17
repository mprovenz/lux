using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Session;

/// <summary>The stereo file formats.</summary>
public enum StereoFormat
{
    /// <summary>`stereo-sbs`: the two eyes side by side in one JPG, the left eye on the left.</summary>
    Sbs,
    /// <summary>`stereo-anaglyph`: one JPG for red-cyan glasses.</summary>
    Anaglyph,
    /// <summary>`stereo-wiggle`: an animation of two frames that alternates between the eyes.</summary>
    Wiggle,
    /// <summary>`stereo-mpo`: an MPO file, two JPG pictures with a multi-picture index, for 3D cameras, televisions and viewers.</summary>
    Mpo,
}

/// <summary>Where the two eyes of a stereo file come from.</summary>
public enum StereoSource
{
    /// <summary>The real lens pair when the capture has a usable one, else the pair made from the depth map.</summary>
    Auto,
    /// <summary>The two wide lenses A5 and A1 only. A capture without a usable pair writes no stereo file.</summary>
    Real,
    /// <summary>Two views made from the merged picture and the depth map.</summary>
    Depth,
}

/// <summary>The stereo formats of an export, as data.</summary>
public sealed record StereoRequest
{
    public IReadOnlyList<StereoFormat> Formats { get; init; } = Array.Empty<StereoFormat>();
    public StereoSource Source { get; init; } = StereoSource.Auto;
    /// <summary>`stereo-sbs`: squeeze each eye to half its width, so that the frame has the size of one eye (for 3D televisions).</summary>
    public bool HalfWidth { get; init; }
    /// <summary>Long edge of one eye in pixels. 0 = the size of the pair (the wiggle: 1040).</summary>
    public int Size { get; init; }
    /// <summary>Container of `stereo-wiggle`: gif, webp or png (APNG).</summary>
    public string Container { get; init; } = "gif";
    /// <summary>Time for each frame of `stereo-wiggle`, in ms.</summary>
    public int WiggleMs { get; init; } = 160;
    /// <summary>How the camera was held: the degrees clockwise (0, 90, 180, 270) that show the capture upright. 0 = held level, or not
    /// known. The front end sets it for each capture.</summary>
    public int HeldTurn { get; init; }
}

/// <summary>
/// The stereo formats of an export (<see cref="StereoRequest"/>). <see cref="Prepare"/> decides where the two eyes come from, before the
/// export decides what to build: the real lens pair needs only the capture, and the pair from the depth map needs the registration.
/// <see cref="Write"/> writes the files.
/// </summary>
public static class StereoExport
{
    /// <summary>The decision for one capture.</summary>
    public sealed class Prepared
    {
        /// <summary>The real pair, or null.</summary>
        public RealStereo.Pair? Pair;
        /// <summary>True when the eyes are made from the depth map: the export then builds the registration state.</summary>
        public bool UseDepth;
        /// <summary>Set when nothing can be written: the reason.</summary>
        public string? Refusal;
        /// <summary>What the user should know about the choice.</summary>
        public List<string> Notes = new();
        /// <summary>Lens pictures that are already rendered, by lens name (reused for the depth pair's fill).</summary>
        public Dictionary<string, Rgba> Native = new();
    }

    public static string NameOf(StereoFormat f) => f switch
    {
        StereoFormat.Sbs => "stereo-sbs", StereoFormat.Anaglyph => "stereo-anaglyph", StereoFormat.Wiggle => "stereo-wiggle", _ => "stereo-mpo",
    };

    public static bool TryParse(string name, out StereoFormat f)
    {
        foreach (var x in new[] { StereoFormat.Sbs, StereoFormat.Anaglyph, StereoFormat.Wiggle, StereoFormat.Mpo })
            if (NameOf(x) == name) { f = x; return true; }
        f = default; return false;
    }

    /// <summary>The file name ending of a format.</summary>
    public static string Suffix(StereoFormat f, StereoRequest r) => f switch
    {
        StereoFormat.Sbs => "_stereo-sbs.jpg",
        StereoFormat.Anaglyph => "_stereo-anaglyph.jpg",
        StereoFormat.Wiggle => "_stereo-wiggle" + (AnimationEncoder.TryParse(r.Container, out var c) ? AnimationEncoder.Extension(c) : ".gif"),
        _ => "_stereo.mpo",
    };

    /// <summary>The distance between the eyes of the pair from the depth map, in mm: 25 for the anaglyph, whose two pictures share one
    /// frame, and 63 for the others.</summary>
    public static double DepthEyeDistance(StereoFormat f) => f == StereoFormat.Anaglyph ? 25 : 63;

    /// <summary>Decide the source for a capture, and make the real pair when it is the source.</summary>
    public static Prepared Prepare(LriFile lri, StereoRequest r, Action<string>? log)
    {
        var p = new Prepared();
        if (r.Source == StereoSource.Depth) { p.UseDepth = true; return p; }
        string? why;
        var lenses = RealStereo.Lenses(lri, r.HeldTurn, out why);
        if (lenses is { } l)
        {
            var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
            var exposure = ModuleRender.ExposureReference(lri, lri.ReferenceModule);
            Rgba Native(string name)
            {
                if (!p.Native.TryGetValue(name, out var img))
                    p.Native[name] = img = ModuleRender.ToRgba(ModuleRender.Render(lri, colour, wb, name, lri.Modules[name], exposure));
                return img;
            }
            var pair = RealStereo.Build(lri, ModuleGrid.SetupApi(lri), l, Native, r.HeldTurn, out why, log);
            if (pair is not null)
            {
                var m = pair.Measured;
                string measured = FormattableString.Invariant($"depth range {m.RangePercent:0.0} % of the width, {m.Rule}, nearest {-m.CrossedPercent:0.0} % in front and farthest {m.UncrossedPercent:0.0} % behind");
                if (m.Comfortable || r.Source == StereoSource.Real)
                {
                    p.Pair = pair;
                    p.Notes.Add($"stereo: the real lens pair {pair.LeftModule} and {pair.RightModule} ({FormattableString.Invariant($"{pair.BaselineMm:0.0}")} mm apart); {measured}"
                              + (m.Comfortable ? "" : FormattableString.Invariant($"; this is more than the {RealStereo.BudgetPercent:0} % that is comfortable to view")));
                    return p;
                }
                why = FormattableString.Invariant($"the subject is too near for the lens pair (depth range {m.RangePercent:0.0} % of the width, the comfortable limit is {RealStereo.BudgetPercent:0} %)");
            }
        }
        if (r.Source == StereoSource.Real) { p.Refusal = $"stereo: no real lens pair for this capture: {why}"; return p; }
        p.UseDepth = true;
        p.Notes.Add($"stereo: {why}; the two eyes are made from the depth map instead (`--stereo-source real` asks for the lens pair only)");
        return p;
    }

    static void WriteJpeg(string path, Rgba img, int quality)
    {
        using var fs = File.Create(path);
        JpegEncoder.Encode(fs, img.P, img.W, img.H, img.W * 4, grayscale: false, new JpegEncoder { Quality = quality });
    }

    static (Rgba L, Rgba R) Sized(Rgba l, Rgba r, int longEdge)
    {
        if (longEdge <= 0 || longEdge >= Math.Max(l.W, l.H)) return (l, r);
        double k = (double)longEdge / Math.Max(l.W, l.H);
        int w = Math.Max(1, (int)Math.Round(l.W * k)), h = Math.Max(1, (int)Math.Round(l.H * k));
        return (RealStereo.Downscale(l, w, h), RealStereo.Downscale(r, w, h));
    }

    /// <summary>Write the stereo files of a request. <paramref name="session"/> is needed for the pair from the depth map only.</summary>
    public static void Write(Prepared p, LriFile lri, ExportRequest req, ExportSession? session, string outDir, string stem,
                             List<Exporter.Output> outputs, List<string> refusals, List<string> notes, Action<string>? log)
    {
        var r = req.Stereo!;
        notes.AddRange(p.Notes);
        if (p.Refusal is not null) { refusals.Add(p.Refusal); return; }
        int quality = req.JpegQuality ?? 92;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var depthPairs = new Dictionary<double, (Rgba L, Rgba R)>();
        ParallaxSource? src = null; IReadOnlyList<Donor>? donors = null;

        (Rgba L, Rgba R, double Baseline) PairFor(StereoFormat f)
        {
            if (p.Pair is { } real) return (real.Left, real.Right, real.BaselineMm);
            var s = session ?? throw new InvalidOperationException("the stereo pair from the depth map needs the export state");
            // the pair is made at a working size from the picture as recorded: the 13 MP picture, or a smaller level that the request names
            s = SceneExport.SourceSession(s, req, log);
            double eye = DepthEyeDistance(f);
            if (!depthPairs.TryGetValue(eye, out var pair))
            {
                var pr = new ParallaxRequest { Formats = new[] { ParallaxFormat.Sbs }, HeldTurn = r.HeldTurn, Size = r.Size > 0 ? r.Size : 1600 };
                src ??= s.ParallaxSource().Scaled(pr.Size);
                if (donors is null)
                {
                    // the other wide color lenses fill what the new viewpoint uncovers; a telephoto capture has none
                    var group = Wigglegram.SelectModules(lri, 0, includeMono: false);
                    var native = new Dictionary<string, Rgba>();
                    if (group.Count >= 2)
                    {
                        var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
                        var exposure = ModuleRender.ExposureReference(lri, lri.ReferenceModule);
                        foreach (var name in group)
                        {
                            if (name == lri.ReferenceModule) continue;
                            native[name] = p.Native.TryGetValue(name, out var have) ? have : ModuleRender.ToRgba(ModuleRender.Render(lri, colour, wb, name, lri.Modules[name], exposure));
                        }
                    }
                    donors = native.Count > 0 ? Fill.Build(src, lri, s.Registration(), native, log) : Array.Empty<Donor>();
                }
                depthPairs[eye] = pair = ParallaxRender.StereoPair(src, pr, eye, donors, log);
            }
            return (pair.L, pair.R, eye);
        }

        foreach (var f in r.Formats.Distinct())
        {
            req.Cancel.ThrowIfCancellationRequested();
            sw.Restart();
            var (l0, r0, baseline) = PairFor(f);
            string path = req.OutFile ?? Path.Combine(outDir, stem + Suffix(f, r));
            switch (f)
            {
                case StereoFormat.Sbs:
                {
                    var (l, rr) = Sized(l0, r0, r.Size);
                    WriteJpeg(path, RealStereo.SideBySide(l, rr, r.HalfWidth), quality);
                    break;
                }
                case StereoFormat.Anaglyph:
                {
                    var (l, rr) = Sized(l0, r0, r.Size);
                    WriteJpeg(path, StereoView.Anaglyph(l, rr, "dubois"), quality);
                    break;
                }
                case StereoFormat.Wiggle:
                {
                    var (l, rr) = Sized(l0, r0, r.Size > 0 ? r.Size : 1040);
                    AnimationEncoder.TryParse(r.Container, out var c);
                    AnimationEncoder.WriteFile(path, c, new[] { l, rr }, new[] { r.WiggleMs, r.WiggleMs }, new AnimationEncodeOptions { Loops = 0 });
                    break;
                }
                default:
                {
                    var (l, rr) = Sized(l0, r0, r.Size);
                    File.WriteAllBytes(path, MpoWriter.Encode(l, rr, baseline, quality));
                    break;
                }
            }
            outputs.Add(new Exporter.Output(NameOf(f), path, new FileInfo(path).Length, sw.Elapsed.TotalSeconds));
            log?.Invoke($"{NameOf(f)} -> {Path.GetFileName(path)} in {sw.Elapsed.TotalSeconds:F1}s");
        }
    }
}
