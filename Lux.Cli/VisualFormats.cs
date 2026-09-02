using System.Diagnostics;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Cli;

/// <summary>
/// The picture-producing formats of `convert` that are Lux features rather than Lumen ones: `lens-frames` (one display
/// JPEG per module), `parallax-wiggle` (the physical wigglegram) and the depth-based `parallax-*` effects. One
/// instance runs per capture, after the Lumen formats, and does the least work the requested set allows:
///
/// <list type="bullet">
/// <item>the module frames are rendered **once** through <see cref="ModuleRender"/> and handed to every consumer
/// that wants them — the lens JPEG, the wigglegram frame (box-averaged from the same floats) and the parallax
/// donors (the native frame, quantised) — so an A-group module never goes through the ISP twice;</item>
/// <item>the parallax source (<see cref="ParallaxSource"/>) is the run's own JPEG render plus the metric depth on the
/// same grid, taken from the <see cref="ExportSession"/> — never a second pipeline build;</item>
/// <item>`lens-frames` and `parallax-wiggle` alone need no <see cref="ExportState"/> at all: <see cref="Run"/> is
/// then called with no session.</item>
/// </list>
/// </summary>
internal sealed class VisualFormats
{
    readonly LriFile _lri; readonly ExportRequest _req; readonly ExportSession? _session;
    readonly string _outDir, _stem; readonly Action<string>? _log;
    readonly List<Exporter.Output> _outputs; readonly List<string> _refusals;
    readonly Stopwatch _sw = new();

    LumenProfile? _colour; WhiteBalance.CaptureWb? _wb;

    public VisualFormats(LriFile lri, ExportRequest req, ExportSession? session, string outDir, string stem,
                         List<Exporter.Output> outputs, List<string> refusals, Action<string>? log)
    { _lri = lri; _req = req; _session = session; _outDir = outDir; _stem = stem; _outputs = outputs; _refusals = refusals; _log = log; }

    ParallaxRequest? P => _req.Parallax;
    LensRequest? L => _req.LensFrames;

    LumenProfile Colour => _colour ??= LumenProfile.Compute(_lri);
    WhiteBalance.CaptureWb Wb => _wb ??= WhiteBalance.CaptureWb.From(_lri, Colour);

    string PathFor(string name, string ext) => _req.OutFile ?? System.IO.Path.Combine(_outDir, _stem + name + ext);

    void Done(string name, string path)
    {
        long bytes = new FileInfo(path).Length;
        _outputs.Add(new Exporter.Output(name, path, bytes, _sw.Elapsed.TotalSeconds));
        _log?.Invoke($"{name} -> {System.IO.Path.GetFileName(path)} ({bytes} B) in {_sw.Elapsed.TotalSeconds:F1}s");
    }

    public void Run()
    {
        // ---- what the module renders have to feed
        bool wantWiggle = P?.Has(ParallaxFormat.Wiggle) ?? false;
        var depthFormats = P?.Formats.Where(ParallaxFormats.NeedsDepth).ToList() ?? new List<ParallaxFormat>();
        bool wantDonors = P is not null && P.Fill == "donors" && depthFormats.Any(ParallaxFormats.CanUseDonors);

        // The A-group colour modules: the wigglegram's frames and the donors. `Wigglegram.SelectModules` is the
        // sweep order; the donors are the same set (every colour A module, the reference included so the
        // wigglegram can use it — the donor build itself drops the reference).
        IReadOnlyList<string> group = Wigglegram.SelectModules(_lri, 0, includeMono: false);
        string[]? wiggleOrder = null;
        if (wantWiggle)
        {
            wiggleOrder = WiggleOrder(group, out string? refusal);
            if (refusal is not null) { _refusals.Add(refusal); wantWiggle = false; wiggleOrder = null; }
        }
        // A telephoto capture (no A modules) has nothing to donate; its depth formats run single-view.
        if (wantDonors && group.Count < 2) { wantDonors = false; _log?.Invoke("no A-group modules: the parallax disocclusions fall back to inpainting only"); }

        // ---- the module renders, one per (module, frame, ISP config), fed to every consumer
        var wiggleFrames = new Dictionary<string, Wigglegram.Frame>();
        var donorNative = new Dictionary<string, Rgba>();
        var jobs = new List<(string Name, LriFile.ModuleRef Ref, int Frame, int Level, RendererProfile Profile, float Ev, ulong ExpRef, bool Lens, bool Group)>();
        if (L is { } l)
        {
            // `lens-frames` normalises every module to A1's exposure (the longest in the capture when A1 is absent),
            // as `lenses` always did; the A-group formats use the reference module's, which is the same exposure on
            // every capture that has an A group at all.
            ulong expA1 = ModuleRender.ExposureReference(_lri, "A1");
            bool allF = string.Equals(l.Stack, "all", StringComparison.OrdinalIgnoreCase);
            int? oneF = !allF && l.Stack is not null ? int.Parse(l.Stack) : null;
            foreach (var (name, _) in _lri.Modules.OrderBy(k => k.Key))
            {
                if (l.Modules is not null && !l.Modules.Contains(name)) continue;
                var frames = _lri.Frames.TryGetValue(name, out var fs) ? fs : new[] { _lri.Modules[name] };
                var pick = allF ? Enumerable.Range(0, frames.Count).ToArray() : new[] { Math.Clamp(oneF ?? 0, 0, frames.Count - 1) };
                foreach (int fi in pick)
                    jobs.Add((name, frames[fi], pick.Length > 1 ? fi : -1, Math.Max(l.Level, 0), l.Profile, l.Ev, expA1, true, false));
            }
        }
        if (wantWiggle || wantDonors)
        {
            ulong expRef = ModuleRender.ExposureReference(_lri, _lri.ReferenceModule);
            var names = wantWiggle ? wiggleOrder!.Union(wantDonors ? group : Array.Empty<string>()) : group;
            foreach (var name in names)
            {
                var mref = _lri.Modules[name];
                // the same frame the lens job renders? then one render feeds both
                int i = jobs.FindIndex(j => j.Name == name && j.Ref.Equals(mref) && j.Level == 0 && j.Profile == RendererProfile.Desktop
                                            && j.Ev == 0.95f && j.ExpRef == expRef);
                if (i >= 0) jobs[i] = jobs[i] with { Group = true };
                else jobs.Add((name, mref, -1, 0, RendererProfile.Desktop, 0.95f, expRef, false, true));
            }
        }
        if (jobs.Count > 0)
        {
            if (_lri.StackFrames > 1 && (wantWiggle || wantDonors)) _log?.Invoke($"stacked capture: {_lri.StackFrames} firings per module, the A-group formats use frame 0 of each");
            int lensCount = 0; _sw.Restart();
            foreach (var j in jobs)
            {
                var img = ModuleRender.Render(_lri, Colour, Wb, j.Name, j.Ref, j.ExpRef, j.Ev, j.Level, j.Profile);
                if (j.Lens)
                {
                    // Only suffix when a stack is actually being spread out, so single-frame output keeps the unsuffixed name.
                    string path = System.IO.Path.Combine(_outDir, $"{_stem}{(j.Frame >= 0 ? $"_f{j.Frame}" : "")}_{j.Name}.jpg");
                    WriteLensJpeg(path, img, L!.Quality);
                    _outputs.Add(new Exporter.Output("lens-frames", path, new FileInfo(path).Length, 0));
                    lensCount++;
                }
                if (j.Group)
                {
                    if (wantWiggle && wiggleOrder!.Contains(j.Name)) wiggleFrames[j.Name] = ModuleRender.ToFrame(img, P!.Size);
                    if (wantDonors && j.Name != _lri.ReferenceModule && group.Contains(j.Name)) donorNative[j.Name] = ModuleRender.ToRgba(img);
                }
            }
            if (lensCount > 0) _log?.Invoke($"lens-frames: {lensCount} JPEG(s) in {_sw.Elapsed.TotalSeconds:F1}s");
        }

        // ---- the physical wigglegram
        if (wantWiggle) WriteWiggle(wiggleOrder!, wiggleFrames);

        // ---- the depth-based formats, all on one source (the run's JPEG render + its depth) and one donor set
        if (depthFormats.Count == 0) return;
        var session = _session ?? throw new InvalidOperationException("the depth-based parallax formats need the export state");
        _sw.Restart();
        var src = session.ParallaxSource().Scaled(P!.Size);
        _log?.Invoke($"parallax source {src.W}x{src.H}, f = {src.Geometry.FocalPx:F2} px ({_sw.Elapsed.TotalSeconds:F1}s)");
        IReadOnlyList<Donor> donors = Array.Empty<Donor>();
        if (wantDonors)
        {
            _sw.Restart();
            donors = Fill.Build(src, _lri, session.Registration(), donorNative, _log);
            _log?.Invoke($"{donors.Count} real donor viewpoints prepared in {_sw.Elapsed.TotalSeconds:F1}s");
        }
        var pairs = new Dictionary<double, (Rgba L, Rgba R)>();   // one synthesised stereo pair per interocular distance
        foreach (var f in depthFormats)
        {
            _sw.Restart();
            string name = ParallaxFormats.Name(f);
            string path = PathFor("_" + name, ParallaxFormats.Extension(f, P.Container));
            switch (f)
            {
                case ParallaxFormat.WiggleInterp:
                case ParallaxFormat.Orbit:
                case ParallaxFormat.Single:
                {
                    var fr = ParallaxRender.Sweep(src, P, f, f == ParallaxFormat.Single ? Array.Empty<Donor>() : donors, _log, out bool closed);
                    Encode(f, ParallaxRender.Played(fr, P, closed), path);
                    break;
                }
                case ParallaxFormat.Rack: Encode(f, ParallaxRender.Played(ParallaxRender.Rack(src, P, _log), P, false), path); break;
                case ParallaxFormat.Dolly: Encode(f, ParallaxRender.Played(ParallaxRender.Dolly(src, P, _log), P, false), path); break;
                case ParallaxFormat.Dof: Png.Write(path, ParallaxRender.Dof(src, P, _log)); break;
                case ParallaxFormat.Anaglyph:
                case ParallaxFormat.CrossEye:
                case ParallaxFormat.Sbs:
                {
                    // --parallax-ipd applies to all three; without it each keeps its own default (25 mm for the
                    // anaglyph, 63 for the pairs), so a run asking for both synthesises two pairs
                    double ipd = P.Ipd ?? ParallaxFormats.DefaultIpd(f);
                    if (!pairs.TryGetValue(ipd, out var pair)) pairs[ipd] = pair = ParallaxRender.StereoPair(src, P, ipd, donors, _log);
                    Rgba img = f switch
                    {
                        ParallaxFormat.Anaglyph => StereoView.Anaglyph(pair.L, pair.R, P.Anaglyph.ToLowerInvariant()),
                        ParallaxFormat.CrossEye => StereoView.SideBySide(pair.L, pair.R, cross: true),
                        _ => StereoView.SideBySide(pair.L, pair.R, cross: false),
                    };
                    Png.Write(path, img);
                    break;
                }
                case ParallaxFormat.Still: Png.Write(path, ParallaxRender.Still(src, P, donors, _log)); break;
                default: throw new ArgumentOutOfRangeException(nameof(f));
            }
            Done(name, path);
        }
    }

    void Encode(ParallaxFormat f, IReadOnlyList<Rgba> played, string path)
    {
        int ms = P!.Ms ?? ParallaxFormats.DefaultMs(f);
        _log?.Invoke($"{played.Count} frames played, {ms} ms each -> {System.IO.Path.GetFileName(path)}");
        Animation.Encode(Animation.Ffmpeg() ?? throw new InvalidOperationException("ffmpeg not found on PATH"), played, path, ms, P.Quality, P.Crf);
    }

    // ---- lens-frames ----------------------------------------------------------------------------------------

    /// <summary>Baseline JPEG through Lux's own encoder (the one the Lumen-identical JPG uses): 4:2:0, JFIF header,
    /// no Exif or comment. The encoder takes RGBX rows, so the packed RGB is widened with an ignored fourth byte.</summary>
    static void WriteLensJpeg(string path, ModuleRender.Image img, int quality)
    {
        var rgb = ModuleRender.ToRgb8(img);
        var rgbx = new byte[(long)img.Width * img.Height * 4];
        for (long i = 0, o = 0; i < rgb.LongLength; i += 3, o += 4)
        { rgbx[o] = rgb[i]; rgbx[o + 1] = rgb[i + 1]; rgbx[o + 2] = rgb[i + 2]; rgbx[o + 3] = 255; }
        using var fs = File.Create(path);
        JpegEncoder.Encode(fs, rgbx, img.Width, img.Height, img.Width * 4, grayscale: false,
                           new JpegEncoder { Quality = quality });
    }

    // ---- parallax-wiggle ------------------------------------------------------------------------------------

    /// <summary>The wigglegram's module order: the sweep (default), label order, or an explicit list. Null with a
    /// refusal when there is no wigglegram to make — see <see cref="Wigglegram"/> for why the A group only.</summary>
    string[]? WiggleOrder(IReadOnlyList<string> sweep, out string? refusal)
    {
        refusal = null;
        string? ord = P!.Order;
        string[] chosen = ord is not null && ord is not ("sweep" or "label")
            ? ord.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ord == "label" ? sweep.OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : sweep.ToArray();
        foreach (var n in chosen)
            if (!_lri.Modules.ContainsKey(n)) { refusal = $"parallax-wiggle: module '{n}' is not in this capture"; return null; }
        if (chosen.Length < 2)
        {
            // A telephoto capture (B4 reference) carries no A-group modules at all. There is no wigglegram to make
            // from what remains: the B and C modules are mosaic tiles aimed at different parts of the scene to be
            // stitched, not alternative viewpoints of one framing, so sweeping them yields no parallax.
            bool noA = !_lri.Modules.Any(m => Wigglegram.Group((int)m.Value.Module.Id) == 0);
            refusal = $"parallax-wiggle: need at least 2 colour modules in group A, found {chosen.Length}."
                    + (noA ? $"\n       This capture has no A-group modules (reference {_lri.ReferenceModule}, "
                           + $"{_lri.Header.ImageFocalLength} mm) — it is a telephoto frame, whose B/C modules are mosaic\n"
                           + "       tiles of different parts of the scene rather than offset views of the same one.\n"
                           + "       Wigglegrams need the 28 mm A array; only wide captures can produce one." : "");
            return null;
        }
        return chosen;
    }

    void WriteWiggle(string[] chosen, Dictionary<string, Wigglegram.Frame> rendered)
    {
        _sw.Restart();
        int dropped = _lri.Modules.Count(m => Wigglegram.Group((int)m.Value.Module.Id) == 0) - chosen.Length;
        _log?.Invoke($"parallax-wiggle: {chosen.Length} frames [{string.Join(" → ", chosen)}]" + (dropped > 0 ? $"  ({dropped} excluded: monochrome)" : ""));
        var frames = chosen.Select(n => rendered[n]).ToList();
        if (P!.Pivot is { } pv)
        {
            // Pivot coordinates are given in NATIVE sensor pixels (what you would read off a full-resolution frame),
            // because that is the image a person picks a subject from — the frames here are already downscaled.
            double k = frames[0].Width / (double)_lri.Modules[chosen[0]].Module.SensorDataSurface.Size.X;
            int px = (int)Math.Round(pv.X * k), py = (int)Math.Round(pv.Y * k);
            int pw = Math.Max(16, (int)Math.Round(pv.W * k)), ph = Math.Max(16, (int)Math.Round(pv.H * k));
            px = Math.Clamp(px, 0, Math.Max(0, frames[0].Width - 16));
            py = Math.Clamp(py, 0, Math.Max(0, frames[0].Height - 16));
            pw = Math.Min(pw, frames[0].Width - px); ph = Math.Min(ph, frames[0].Height - py);
            var r = new RectI(px, py, px + pw, py + ph);
            int before = frames[0].Width;
            frames = Wigglegram.PinPivot(frames, r).ToList();
            _log?.Invoke($"pivot pinned on native ({pv.X},{pv.Y},{pv.W}x{pv.H}) = frame ({r.X0},{r.Y0},{r.Width}x{r.Height}) → cropped to {frames[0].Width}x{frames[0].Height} "
                       + $"({100.0 * frames[0].Width / before:F1}% of width)");
        }
        Wigglegram.MatchColour(frames);
        bool pingpong = (P.Loop ?? "pingpong") != "forward";
        var played = (pingpong ? Wigglegram.Boomerang(frames) : frames).Select(Animation.ToRgba).ToList();
        string path = PathFor("_parallax-wiggle", "." + P.Container);
        Encode(ParallaxFormat.Wiggle, played, path);
        Done("parallax-wiggle", path);
    }
}
