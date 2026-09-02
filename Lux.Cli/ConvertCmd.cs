using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Cli;

/// <summary>
/// `convert &lt;lri…&gt;` — what Lumen does when you open a capture and export it: the ported cp.dll pipeline, run once,
/// written out in any combination of Lumen's `ExportImageFormat`s, plus the Lux-only formats (the depth pair,
/// `lens-frames`, the `parallax-*` effects) that share the same render. Nothing is derived from a dump and nothing
/// Lux-specific is applied to the Lumen formats: the reference calibration and the level-0 dense depth come from the
/// `.lri` through the ported registration chain, the export window/size are Lumen's own (`setInputDataStream` →
/// `GetExportTransformOutput`), and every writer is the verified port.
///
/// <para>This is the command's **flag surface only** — `--formats`, the output naming, the grid, the adjustment
/// flags and the Lux-format flags. The export itself is <see cref="Exporter"/>.</para>
///
/// <para>With no options the output is Lumen's, exactly. Every flag in the ADJUSTMENTS groups departs from that,
/// and <see cref="Plan.Notes"/> echoes the ones that were applied. The Lux formats are additive: they never change
/// the Lumen files written beside them.</para>
/// </summary>
public static class ConvertCmd
{
    /// <summary>Lumen's default export pair: the DNG and its companion JPEG.</summary>
    public static readonly ExportImageFormat[] DefaultFormats = { ExportImageFormat.Dng, ExportImageFormat.Jpeg };

    public static string Extension(ExportImageFormat f) => f switch
    {
        ExportImageFormat.Jpeg => ".jpg",
        ExportImageFormat.Ppm => ".ppm",
        ExportImageFormat.Dng => ".dng",
        ExportImageFormat.Hdr => ".hdr",
        ExportImageFormat.JpegGDepth => "_gdepth.jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>The parsed `--formats` list: the Lumen rasters, the depth pair, and the Lux formats.</summary>
    public sealed record FormatSet(ExportImageFormat[] Raster, bool Depth, bool LensFrames, ParallaxFormat[] Parallax)
    {
        public int Count => Raster.Length + (Depth ? 1 : 0) + (LensFrames ? 1 : 0) + Parallax.Length;
        /// <summary>Every name, in the `--formats` spelling.</summary>
        public IEnumerable<string> Names => Raster.Select(NameOf).Concat(Depth ? new[] { "depth" } : Array.Empty<string>())
            .Concat(LensFrames ? new[] { "lens-frames" } : Array.Empty<string>()).Concat(Parallax.Select(ParallaxFormats.Name));
    }

    public const string KnownFormats = "dng, jpg, hdr, ppm, jpg+depth, depth, all, lens-frames, parallax-wiggle, parallax-wiggle-interp, "
                                     + "parallax-orbit, parallax-single, parallax-rack, parallax-dolly, parallax-dof, parallax-anaglyph, "
                                     + "parallax-crosseye, parallax-sbs, parallax-still";

    /// <summary>Parse a `--formats` list. `all` is every format except `hdr` and `ppm` (aliases: jpeg, gdepth, jpg-depth, radiance).
    /// `depth` is not a Lumen `ExportImageFormat` — it is the stereo depth **pair** (`&lt;stem&gt;_depth.f32` +
    /// `_depth.jpg`) — and `lens-frames`/`parallax-*` are Lux features, so each comes back as its own member.</summary>
    public static FormatSet ParseFormats(string list)
    {
        var outp = new List<ExportImageFormat>(); bool depth = false, lens = false;
        var px = new List<ParallaxFormat>();
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string t = raw.ToLowerInvariant();
            if (t == "all")
            {   // every format except hdr and ppm (the two that only make sense on request)
                foreach (var f in new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }) if (!outp.Contains(f)) outp.Add(f);
                depth = true; lens = true;
                foreach (var pf0 in ParallaxFormats.All) if (!px.Contains(pf0)) px.Add(pf0);
                continue;
            }
            if (t == "depth") { depth = true; continue; }
            if (t == "lens-frames") { lens = true; continue; }
            if (ParallaxFormats.TryParse(t, out var pf)) { if (!px.Contains(pf)) px.Add(pf); continue; }
            ExportImageFormat v = t switch
            {
                "dng" => ExportImageFormat.Dng,
                "jpg" or "jpeg" => ExportImageFormat.Jpeg,
                "hdr" or "radiance" => ExportImageFormat.Hdr,
                "ppm" => ExportImageFormat.Ppm,
                "jpg+depth" or "jpg-depth" or "gdepth" or "jpegdepth" => ExportImageFormat.JpegGDepth,
                _ => throw new FormatException($"unknown output format '{raw}' (known: {KnownFormats})"),
            };
            if (!outp.Contains(v)) outp.Add(v);
        }
        var set = new FormatSet(outp.ToArray(), depth, lens, px.ToArray());
        if (set.Count == 0) throw new FormatException("--formats needs at least one format");
        return set;
    }

    /// <summary>A validated `convert` run: the shared <see cref="ExportRequest"/> (the per-input output naming is
    /// filled in by the batch loop) plus the lines the run must print because they depart from Lumen's output or
    /// silently change what the pipeline does.</summary>
    internal sealed record Plan(ExportRequest Request, IReadOnlyList<string> Notes, string FormatsLabel);

    /// <summary>Every flag whose name carries its own scope, and the formats that scope covers. A flag naming a
    /// format the run does not write is an error — the name is the contract, so there is no flag→format table to
    /// keep in sync elsewhere.</summary>
    static readonly (string Flag, ExportImageFormat[] Needs)[] Scoped =
    {
        ("--dng-cs",        new[] { ExportImageFormat.Dng }),
        ("--dng-tone",      new[] { ExportImageFormat.Dng }),
        ("--dng-comp",      new[] { ExportImageFormat.Dng }),
        ("--jpeg-cs",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-quality",  new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-sub",      new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-v2",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-modify",   new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-comment",  new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-software", new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--hdr-cs",        new[] { ExportImageFormat.Hdr }),
        ("--fnum",          new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--iso",           new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--focal",         new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        // rotation is baked by ExportTransform upstream of every writer, so it reaches all four raster formats
        // (verified: export-hdr rotate:90 writes -Y 1040 +X 780 against the default's -Y 780 +X 1040).
        ("--rotate",        new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
    };

    static readonly string[] LensFlags = { "--lens-quality", "--lens-ev", "--lens-level", "--lens-profile", "--lens-modules", "--lens-stack" };

    /// <summary>The `--parallax-*` flags and which parallax formats read each. Container/size/timing flags apply
    /// to the whole family; the rest are per effect.</summary>
    static readonly (string Flag, Func<ParallaxFormat, bool> Applies, string Scope)[] ParallaxScoped =
    {
        ("--parallax-format",      ParallaxFormats.IsAnimated, "the animated parallax formats"),
        ("--parallax-size",        _ => true, "every parallax format"),
        ("--parallax-ms",          ParallaxFormats.IsAnimated, "the animated parallax formats"),
        ("--parallax-loop",        f => ParallaxFormats.IsAnimated(f), "the animated parallax formats"),
        ("--parallax-frames",      f => f is ParallaxFormat.WiggleInterp or ParallaxFormat.Orbit or ParallaxFormat.Single or ParallaxFormat.Rack or ParallaxFormat.Dolly, "parallax-wiggle-interp, -orbit, -single, -rack, -dolly"),
        ("--parallax-fill",        f => ParallaxFormats.CanUseDonors(f) || f == ParallaxFormat.Single, "parallax-wiggle-interp, -orbit, -single, -anaglyph, -crosseye, -sbs, -still"),
        ("--parallax-path",        f => f is ParallaxFormat.WiggleInterp or ParallaxFormat.Single, "parallax-wiggle-interp, -single"),
        ("--parallax-baseline",    f => f is ParallaxFormat.WiggleInterp or ParallaxFormat.Orbit or ParallaxFormat.Single, "parallax-wiggle-interp, -orbit, -single"),
        ("--parallax-converge",    f => ParallaxFormats.CanUseDonors(f) || f == ParallaxFormat.Single, "parallax-wiggle-interp, -orbit, -single, -anaglyph, -crosseye, -sbs, -still"),
        ("--parallax-converge-at", f => ParallaxFormats.CanUseDonors(f) || f == ParallaxFormat.Single, "parallax-wiggle-interp, -orbit, -single, -anaglyph, -crosseye, -sbs, -still"),
        ("--parallax-ipd",         f => f is ParallaxFormat.Anaglyph or ParallaxFormat.CrossEye or ParallaxFormat.Sbs, "parallax-anaglyph, -crosseye, -sbs"),
        ("--parallax-anaglyph",    f => f == ParallaxFormat.Anaglyph, "parallax-anaglyph"),
        ("--parallax-focus",       f => f == ParallaxFormat.Dof, "parallax-dof"),
        ("--parallax-focus-at",    f => f == ParallaxFormat.Dof, "parallax-dof"),
        ("--parallax-aperture",    f => f is ParallaxFormat.Dof or ParallaxFormat.Rack, "parallax-dof, -rack"),
        ("--parallax-layers",      f => f is ParallaxFormat.Dof or ParallaxFormat.Rack, "parallax-dof, -rack"),
        ("--parallax-rack",        f => f == ParallaxFormat.Rack, "parallax-rack"),
        ("--parallax-rack-at",     f => f == ParallaxFormat.Rack, "parallax-rack"),
        ("--parallax-subject",     f => f == ParallaxFormat.Dolly, "parallax-dolly"),
        ("--parallax-subject-at",  f => f == ParallaxFormat.Dolly, "parallax-dolly"),
        ("--parallax-dz",          f => f == ParallaxFormat.Dolly, "parallax-dolly"),
        ("--parallax-t",           f => f == ParallaxFormat.Still, "parallax-still"),
        ("--parallax-quality",     ParallaxFormats.IsAnimated, "the animated parallax formats, webp container"),
        ("--parallax-crf",         ParallaxFormats.IsAnimated, "the animated parallax formats, avif container"),
        ("--parallax-order",       f => f == ParallaxFormat.Wiggle, "parallax-wiggle"),
        ("--parallax-pivot",       f => f == ParallaxFormat.Wiggle, "parallax-wiggle"),
    };

    /// <summary>Validate the flag combination and turn it into an <see cref="ExportRequest"/>. Returns null and
    /// sets <paramref name="error"/> when the combination is rejected (the caller exits 2). Nothing has been
    /// rendered or written by then — which is also where a missing ffmpeg is caught, before any file is touched.</summary>
    internal static Plan? TryPlan(Options o, int inputCount, out string? error)
    {
        error = null;
        FormatSet set;
        try
        {
            set = o.Formats is null ? new FormatSet(DefaultFormats, false, false, Array.Empty<ParallaxFormat>()) : ParseFormats(o.Formats);
        }
        catch (FormatException ex) { error = ex.Message; return null; }

        var formats = set.Raster;
        bool depth = set.Depth;
        set = set with { Raster = formats, Depth = depth };
        var px = set.Parallax;

        string label = string.Join("+", set.Names);

        // ---- a prefixed flag naming a format this run does not write
        foreach (var (flag, needs) in Scoped)
        {
            if (!o.Given.Contains(flag)) continue;
            if (needs.Any(formats.Contains)) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). "
                  + $"It applies to: {string.Join(", ", needs.Select(NameOf).Distinct())}";
            return null;
        }
        foreach (var flag in LensFlags)
        {
            if (!o.Given.Contains(flag) || set.LensFrames) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). It applies to: lens-frames";
            return null;
        }
        foreach (var (flag, applies, scope) in ParallaxScoped)
        {
            if (!o.Given.Contains(flag)) continue;
            if (px.Any(applies)) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). It applies to: {scope}";
            return null;
        }

        // ---- the parallax value checks, so a typo fails here rather than after a level-0 build
        string container = o.ParallaxFormat ?? "gif";
        if (!Animation.Containers.Contains(container)) { error = $"--parallax-format must be one of {string.Join(", ", Animation.Containers)}"; return null; }
        if (o.ParallaxQuality is not null && container != "webp") { error = "--parallax-quality is the WebP encoder quality; pass --parallax-format webp"; return null; }
        if (o.ParallaxCrf is not null && container != "avif") { error = "--parallax-crf is the AVIF encoder crf; pass --parallax-format avif"; return null; }
        if (o.ParallaxLoop is not null and not ("pingpong" or "forward")) { error = "--parallax-loop must be pingpong or forward"; return null; }
        if (o.ParallaxFill is not null and not ("donors" or "inpaint" or "none")) { error = "--parallax-fill must be donors, inpaint or none"; return null; }
        if (o.ParallaxPath is not null and not ("sweep" or "arc" or "line")) { error = "--parallax-path must be sweep, arc or line (the orbit is its own format, parallax-orbit)"; return null; }
        if (o.ParallaxAnaglyph is not null and not ("dubois" or "colour" or "color" or "grey" or "gray")) { error = "--parallax-anaglyph must be dubois, colour or grey"; return null; }
        if (o.ParallaxConverge is not null && o.ParallaxConverge is not ("auto" or "none" or "off") && !double.TryParse(o.ParallaxConverge, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        { error = "--parallax-converge must be metres, auto or none"; return null; }
        if (px.Length > 0 && o.Rotate is not null and not 0)
        { error = "--rotate cannot be combined with the parallax formats: the rig geometry is in the unrotated image's axes"; return null; }
        if (o.LensProfile is < 0 or > 3) { error = "--lens-profile must be 0-3"; return null; }

        // ---- ffmpeg, checked before anything renders
        if (px.Any(ParallaxFormats.IsAnimated) && Animation.Ffmpeg() is null)
        {
            error = "ffmpeg not found on PATH. The animated parallax formats ("
                  + string.Join(", ", px.Where(ParallaxFormats.IsAnimated).Select(ParallaxFormats.Name))
                  + ") are encoded by ffmpeg: the frames are produced by Lux, but GIF, animated WebP, AVIF and APNG are\n"
                  + "       VP8/AV1-class containers that cannot be hand-written the way this project's JPEG and DNG writers were.\n"
                  + "       Install ffmpeg (https://ffmpeg.org) and run again — nothing has been written.";
            return null;
        }

        // ---- --out-file: one input × one format × that format emitting one file
        int fileCount = formats.Length + (depth ? 2 : 0) + px.Length;   // `depth` writes the .f32 AND the .jpg preview
        if (o.OutFile is not null)
        {
            if (o.OutDirectory is not null) { error = "--out-file and --out-directory are alternatives; pass one"; return null; }
            if (inputCount != 1) { error = $"--out-file names one output file, but {inputCount} inputs were given — use --out-directory"; return null; }
            if (set.LensFrames) { error = "--out-file names one output file, but lens-frames writes one JPEG per module — use --out-directory"; return null; }
            if (fileCount != 1)
            {
                error = $"--out-file names one output file, but --formats {label} writes {fileCount}"
                      + (depth ? " (`depth` writes the .f32 and its .jpg preview)" : "") + " — use --out-directory";
                return null;
            }
        }

        // ---- the notes the run has to print
        var notes = new List<string>();
        var applied = new List<string>();
        void Say(string flag, object? v) { if (v is not null) applied.Add(v is bool ? flag : $"{flag} {v}"); }
        Say("--rotate", o.Rotate); Say("--fnum", o.FNumber); Say("--iso", o.Iso); Say("--focal", o.Focal);
        Say("--dng-cs", o.DngCs); Say("--dng-tone", o.DngTone); Say("--dng-comp", o.DngComp);
        Say("--jpeg-cs", o.JpegCs); Say("--jpeg-quality", o.JpegQuality); Say("--jpeg-sub", o.JpegSub);
        if (o.JpegV2) applied.Add("--jpeg-v2");
        Say("--jpeg-modify", o.JpegModify?.ToString("yyyy-MM-ddTHH:mm:ss")); Say("--jpeg-comment", o.JpegComment); Say("--jpeg-software", o.JpegSoftware);
        Say("--hdr-cs", o.HdrCs);
        if (applied.Count > 0)
            notes.Add($"adjustments: {string.Join(", ", applied)} — these depart from the values the .lri and Lumen's own rules supply");

        var extras = (set.LensFrames ? new[] { "lens-frames" } : Array.Empty<string>()).Concat(px.Select(ParallaxFormats.Name)).ToList();
        if (extras.Count > 0)
            notes.Add($"Lux formats: {string.Join(", ", extras)} — additive outputs beside the Lumen ones, which they never change"
                    + (px.Length > 0 ? "; the parallax formats are experimental and not held to the 1:1 bar" : ""));

        bool forcesLevel0 = depth || formats.Contains(ExportImageFormat.JpegGDepth) || px.Any(ParallaxFormats.NeedsDepth);
        if (forcesLevel0 && o.Level != 0)
        {
            string who = formats.Contains(ExportImageFormat.JpegGDepth) ? "jpg+depth" : depth ? "depth" : ParallaxFormats.Name(px.First(ParallaxFormats.NeedsDepth));
            notes.Add($"{who} reads the renderer's depth cache, which only the level-0 "
                    + $"registration state fills — so the level-0 build runs in full even though --level {o.Level} was given "
                    + $"(the exported grid is still level {o.Level}). Expect it to take as long as a level-0 export.");
        }
        if (px.Any(ParallaxFormats.NeedsDepth) && o.Level == 0 && o.Size is null)
            notes.Add("the parallax source is this run's own JPEG render, at full size here and then downscaled to --parallax-size; "
                    + "--level 2 renders the base at 2608 px, which is plenty for a 1600 px animation and much faster");

        var req = new ExportRequest
        {
            Formats = formats,
            DepthMap = depth,
            Level = o.Level,
            Size = o.Size,
            Origin = o.Origin,
            Rotate = o.Rotate ?? 0,
            FNumber = o.FNumber,
            Iso = o.Iso,
            FocalLengthMm = o.Focal,
            DngColorSpace = o.DngCs,
            ToneMappingType = o.DngTone,
            Compression = o.DngComp,
            JpegColorSpace = o.JpegCs,
            JpegQuality = o.JpegQuality,
            JpegSubsampling = o.JpegSub,
            JpegV2 = o.JpegV2,          // deliberately NOT LUX_DISPLAY_V2: the flag is the supported route and
                                        // `convert` stays immune to the environment variable.
            JpegModifyTime = o.JpegModify,
            JpegComment = o.JpegComment,
            JpegSoftware = o.JpegSoftware,
            HdrColorSpace = o.HdrCs,
            LensFrames = set.LensFrames ? new LensRequest
            {
                Ev = o.LensEv, Quality = o.LensQuality, Level = o.LensLevel, Profile = (RendererProfile)o.LensProfile,
                Modules = o.LensModules, Stack = o.LensStack,
            } : null,
            Parallax = px.Length > 0 ? new ParallaxRequest
            {
                Formats = px, Container = container,
                Size = o.ParallaxSize ?? 1600, Ms = o.ParallaxMs, Frames = o.ParallaxFrames ?? 24, Loop = o.ParallaxLoop,
                Fill = o.ParallaxFill ?? "donors", Path = o.ParallaxPath ?? "sweep", Baseline = o.ParallaxBaseline ?? 71.49,
                Converge = o.ParallaxConverge ?? "auto", ConvergeAt = o.ParallaxConvergeAt, Ipd = o.ParallaxIpd,
                Anaglyph = o.ParallaxAnaglyph ?? "dubois", FocusM = o.ParallaxFocus, FocusAt = o.ParallaxFocusAt,
                Aperture = o.ParallaxAperture ?? 20, Layers = o.ParallaxLayers ?? 8, RackM = o.ParallaxRack, RackAt = o.ParallaxRackAt,
                SubjectM = o.ParallaxSubject, SubjectAt = o.ParallaxSubjectAt, Dz = o.ParallaxDz ?? 400, T = o.ParallaxT ?? (40, 0),
                Quality = o.ParallaxQuality, Crf = o.ParallaxCrf, Order = o.ParallaxOrder, Pivot = o.ParallaxPivot,
            } : null,
        };
        return new Plan(req, notes, label);
    }

    public static string NameOf(ExportImageFormat f) => f switch
    {
        ExportImageFormat.Jpeg => "jpg",
        ExportImageFormat.JpegGDepth => "jpg+depth",
        ExportImageFormat.Dng => "dng",
        ExportImageFormat.Hdr => "hdr",
        ExportImageFormat.Ppm => "ppm",
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>One capture, the pre-flag entry point: options as an <see cref="ExportRequest"/>, handed to
    /// <see cref="Exporter"/>.</summary>
    public static Exporter.Result Run(string lriPath, string outDir, string stem, int? level, (int W, int H)? sizeOverride,
                                      Action<string>? log, IReadOnlyList<ExportImageFormat>? formats = null, bool depthMap = false)
        => Exporter.Run(lriPath, new ExportRequest
        {
            Formats = formats is null || formats.Count == 0 ? DefaultFormats : formats,
            DepthMap = depthMap,
            OutDirectory = outDir,
            Stem = stem,
            Level = level,
            Size = sizeOverride,
        }, log);
}
