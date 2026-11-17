using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Cli;

/// <summary>
/// `convert &lt;lri…&gt;` — what Lumen does when you open a capture and export it: the ported Lumen pipeline, run once,
/// written out in any combination of Lumen's `ExportImageFormat`s, plus the Lux-only formats (the depth pair,
/// `lens-frames`, the `parallax-*` effects) that share the same render. Nothing is taken from Lumen's output and nothing
/// Lux-specific is applied to the Lumen formats: the reference calibration and the level-0 dense depth come from the
/// `.lri` through the ported registration chain, the export window/size are Lumen's own (`setInputDataStream` →
/// `GetExportTransformOutput`), and every writer is the ported one.
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
    public static readonly ExportImageFormat[] DefaultFormats = ExportFormats.DefaultFormats;

    public static string Extension(ExportImageFormat f) => ExportFormats.Extension(f);

    /// <summary>The parsed `--formats` list: the Lumen rasters, the depth pair, and the Lux formats.</summary>
    public sealed record FormatSet(ExportImageFormat[] Raster, bool Depth, bool LensFrames, ParallaxFormat[] Parallax)
    {
        /// <summary>The still formats of Lux: tiff16, tiff-linear, jpg-hdr.</summary>
        public StillFormat[] Still { get; init; } = Array.Empty<StillFormat>();
        /// <summary>The stereo formats: stereo-sbs, stereo-anaglyph, stereo-wiggle, stereo-mpo.</summary>
        public StereoFormat[] Stereo { get; init; } = Array.Empty<StereoFormat>();
        /// <summary>The 3D formats: mesh-glb, pointcloud-ply.</summary>
        public SceneFormat[] Scene { get; init; } = Array.Empty<SceneFormat>();
        public int Count => Raster.Length + Still.Length + (Depth ? 1 : 0) + (LensFrames ? 1 : 0) + Parallax.Length + Stereo.Length + Scene.Length;
        /// <summary>Every name, in the `--formats` spelling.</summary>
        public IEnumerable<string> Names => Raster.Select(NameOf).Concat(Still.Select(StillFormats.NameOf)).Concat(Depth ? new[] { "depth" } : Array.Empty<string>())
            .Concat(LensFrames ? new[] { "lens-frames" } : Array.Empty<string>()).Concat(Parallax.Select(ParallaxFormats.Name)).Concat(Stereo.Select(StereoExport.NameOf)).Concat(Scene.Select(SceneExport.NameOf));
    }

    public const string KnownFormats = "dng, jpg, hdr, ppm, jpg+depth, tiff16, tiff-linear, jpg-hdr, depth, all, lens-frames, parallax-wiggle, parallax-wiggle-interp, "
                                     + "parallax-orbit, parallax-single, parallax-rack, parallax-dolly, parallax-dof, parallax-anaglyph, "
                                     + "parallax-crosseye, parallax-sbs, parallax-still, stereo-sbs, stereo-anaglyph, stereo-wiggle, stereo-mpo, "
                                     + "mesh-glb, pointcloud-ply";

    /// <summary>Parse a `--formats` list. `all` is every format except `hdr` and `ppm` (aliases: jpeg, gdepth, jpg-depth, radiance).
    /// `depth` is not a Lumen `ExportImageFormat` — it is the stereo depth **pair** (`&lt;stem&gt;_depth.f32` +
    /// `_depth.jpg`) — and `lens-frames`/`parallax-*` are Lux features, so each comes back as its own member.</summary>
    public static FormatSet ParseFormats(string list)
    {
        var outp = new List<ExportImageFormat>(); bool depth = false, lens = false;
        var px = new List<ParallaxFormat>(); var still = new List<StillFormat>(); var stereo = new List<StereoFormat>(); var scene = new List<SceneFormat>();
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
            if (StillFormats.TryParse(t, out StillFormat sf)) { if (!still.Contains(sf)) still.Add(sf); continue; }
            if (ParallaxFormats.TryParse(t, out var pf)) { if (!px.Contains(pf)) px.Add(pf); continue; }
            if (StereoExport.TryParse(t, out var stf)) { if (!stereo.Contains(stf)) stereo.Add(stf); continue; }
            if (SceneExport.TryParse(t, out var scf)) { if (!scene.Contains(scf)) scene.Add(scf); continue; }
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
        var set = new FormatSet(outp.ToArray(), depth, lens, px.ToArray()) { Still = still.ToArray(), Stereo = stereo.ToArray(), Scene = scene.ToArray() };
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
        ("--dng-highlights", new[] { ExportImageFormat.Dng }),
        ("--jpeg-cs",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-quality",  new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-sub",      new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-v2",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-sharpening", new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-modify",   new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        // tone & color: the output stage the JPG (and jpg+depth) runs; DNG, PPM and HDR never run it
        ("--exposure",      new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--contrast",      new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--temperature",   new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--tint",          new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--vibrance",      new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--saturation",    new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--sharpening",    new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--shadows",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--highlights",    new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--clarity",       new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--highlight-recovery", new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--gamut-compress", new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-comment",  new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--jpeg-software", new[] { ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        // the noise controls change the merged image, which every raster format is written from
        ("--grain-strength",    new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--denoise-threshold", new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--hdr-cs",        new[] { ExportImageFormat.Hdr }),
        ("--fnum",          new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        // the depth effect replaces the fused image under every raster format (the tile source is shared by all of them)
        ("--f-number",      new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--focus-depth",   new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--focus-point",   new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--dof-rendering", new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--iso",           new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        ("--focal",         new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth }),
        // rotation is baked by ExportTransform upstream of every writer, so it reaches all four raster formats
        // (export-hdr rotate:90 writes -Y 1040 +X 780 against the default's -Y 780 +X 1040).
        ("--rotate",        new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--transform",     new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
        ("--upright",       new[] { ExportImageFormat.Dng, ExportImageFormat.Jpeg, ExportImageFormat.JpegGDepth, ExportImageFormat.Hdr, ExportImageFormat.Ppm }),
    };

    /// <summary>Whether a flag of <see cref="Scoped"/> also applies to a still format of Lux. These formats are written from the render
    /// of the JPG, so they take each flag of the JPG that changes the render or the Exif values. `jpg-hdr` holds the JPG itself and also
    /// takes the flags of its encoder. `--jpeg-cs` applies to none: `jpg-hdr` needs sRGB, and the TIFF formats have `--tiff-cs`.</summary>
    static bool AppliesToStill(string flag, ExportImageFormat[] needs, StillFormat f)
    {
        if (!needs.Contains(ExportImageFormat.Jpeg) || flag == "--jpeg-cs") return false;
        return f == StillFormat.JpegHdr || flag is not ("--jpeg-quality" or "--jpeg-sub" or "--jpeg-comment");
    }

    /// <summary>The flags of the still formats of Lux, and the formats that read each.</summary>
    static readonly (string Flag, StillFormat[] Needs)[] StillScoped =
    {
        ("--tiff-cs",          new[] { StillFormat.Tiff16 }),
        ("--tiff-compression", new[] { StillFormat.Tiff16, StillFormat.TiffLinear }),
        ("--tiff-depth",       new[] { StillFormat.TiffLinear }),
        ("--gainmap",          new[] { StillFormat.JpegHdr }),
        ("--gainmap-scale",    new[] { StillFormat.JpegHdr }),
        ("--gainmap-quality",  new[] { StillFormat.JpegHdr }),
        ("--hdr-max-size",     new[] { StillFormat.JpegHdr }),
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
        ("--parallax-encoder",     ParallaxFormats.IsAnimated, "the animated parallax formats"),
        ("--parallax-pivot",       f => f == ParallaxFormat.Wiggle, "parallax-wiggle"),
    };

    /// <summary>Validate the flag combination and turn it into an <see cref="ExportRequest"/>. Returns null and
    /// sets <paramref name="error"/> when the combination is rejected (the caller exits 2). Nothing has been
    /// rendered or written by then — which is also where a missing ffmpeg is caught, before any file is touched.</summary>
    internal static Plan? TryPlan(Options o, int inputCount, out string? error)
    {
        error = null;
        if (o.LevelIsName) { error = $"--level takes an export level number (0 = full size … 4) for convert, not '{o.LevelText}'"; return null; }
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

        // ---- the animate command's own flags
        if (o.Given.FirstOrDefault(g => g.StartsWith("--anim-", StringComparison.Ordinal)) is { } af)
        { error = $"{af} is an option of `lux-light animate`, not of convert"; return null; }

        // ---- a prefixed flag naming a format this run does not write
        foreach (var (flag, needs) in Scoped)
        {
            if (!o.Given.Contains(flag)) continue;
            if (needs.Any(formats.Contains)) continue;
            if (set.Still.Any(f => AppliesToStill(flag, needs, f))) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). "
                  + $"It applies to: {string.Join(", ", needs.Select(NameOf).Distinct().Concat(StillFormats.All.Where(f => AppliesToStill(flag, needs, f)).Select(StillFormats.NameOf)))}";
            return null;
        }
        foreach (var (flag, needs) in StillScoped)
        {
            if (!o.Given.Contains(flag) || needs.Any(set.Still.Contains)) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). It applies to: {string.Join(", ", needs.Select(StillFormats.NameOf))}";
            return null;
        }

        // ---- the values of the TIFF and gain-map flags
        var tiff = new TiffOptions(); var gainMap = new GainMapOptions();
        if (o.TiffCs is not null)
        {
            if (!StillFormats.TryParseColorSpace(o.TiffCs, out var tcs)) { error = "--tiff-cs must be srgb, p3 or adobergb"; return null; }
            tiff = tiff with { ColorSpace = tcs };
        }
        if (o.TiffCompression is not null)
        {
            if (o.TiffCompression is not ("deflate" or "lzw" or "none")) { error = "--tiff-compression must be deflate, lzw or none"; return null; }
            tiff = tiff with { Compression = o.TiffCompression switch { "lzw" => TiffCompression.Lzw, "none" => TiffCompression.None, _ => TiffCompression.Deflate } };
        }
        if (o.TiffDepth is not null)
        {
            if (o.TiffDepth is not ("f32" or "f16")) { error = "--tiff-depth must be f32 or f16"; return null; }
            tiff = tiff with { LinearDepth = o.TiffDepth == "f16" ? TiffFloatDepth.Float16 : TiffFloatDepth.Float32 };
        }
        if (o.GainMap is not null and not ("rgb" or "luminance")) { error = "--gainmap must be rgb or luminance"; return null; }
        if (o.GainMapScale is not null and not (4 or 2 or 1)) { error = "--gainmap-scale must be 4, 2 or 1"; return null; }
        if (o.GainMapQuality is int gq && gq is < 1 or > 100) { error = "--gainmap-quality must be between 1 and 100"; return null; }
        if (o.HdrMaxSize is int hm && hm < 16) { error = "--hdr-max-size must be at least 16 (pixels)"; return null; }
        gainMap = gainMap with { Rgb = o.GainMap != "luminance", Scale = o.GainMapScale ?? gainMap.Scale, Quality = o.GainMapQuality ?? gainMap.Quality, MaxSize = o.HdrMaxSize };
        if (set.Still.Contains(StillFormat.JpegHdr) && o.JpegCs is not null and not 4)
        { error = "jpg-hdr needs the sRGB color space for the JPG; it cannot be combined with another --jpeg-cs"; return null; }
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

        // ---- the stereo flags
        foreach (var (flag, applies) in new (string, Func<StereoFormat, bool>)[] { ("--stereo-source", _ => true), ("--stereo-size", _ => true),
                                                                                   ("--stereo-layout", f => f == StereoFormat.Sbs), ("--stereo-format", f => f == StereoFormat.Wiggle) })
        {
            if (!o.Given.Contains(flag) || set.Stereo.Any(applies)) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). It applies to: {(flag == "--stereo-layout" ? "stereo-sbs" : flag == "--stereo-format" ? "stereo-wiggle" : "the stereo formats")}";
            return null;
        }
        if (o.StereoSource is not null and not ("auto" or "real" or "depth")) { error = "--stereo-source must be auto, real or depth"; return null; }
        if (o.StereoLayout is not null and not ("full" or "half")) { error = "--stereo-layout must be full or half"; return null; }
        if (o.StereoFormat is not null and not ("gif" or "webp" or "apng")) { error = "--stereo-format must be gif, webp or apng"; return null; }
        if (o.StereoSize is int ss && ss < 64) { error = "--stereo-size must be at least 64 (pixels)"; return null; }
        if (set.Stereo.Length > 0 && (o.Transform is not null || (o.Rotate ?? 0) != 0 || o.Upright))
        { error = "--rotate, --transform and --upright cannot be combined with the stereo formats: a stereo pair is written upright by itself"; return null; }
        if (set.Stereo.Length > 0 && o.OutFile is not null) { error = "--out-file cannot be combined with the stereo formats; give -o"; return null; }

        // ---- the 3D flags
        foreach (var (flag, format) in new[] { ("--mesh-triangles", SceneFormat.MeshGlb), ("--mesh-depth", SceneFormat.MeshGlb), ("--pc-step", SceneFormat.PointCloudPly) })
        {
            if (!o.Given.Contains(flag) || set.Scene.Contains(format)) continue;
            error = $"{flag} configures a format this run does not write (--formats {label}). It applies to: {SceneExport.NameOf(format)}";
            return null;
        }
        if (o.MeshDepth is not null and not ("relief" or "metric")) { error = "--mesh-depth must be relief or metric"; return null; }
        if (o.MeshTriangles is int mt && mt is < 1000 or > 5_000_000) { error = "--mesh-triangles must be between 1000 and 5000000"; return null; }
        if (o.PcStep is not null and not (1 or 2 or 4 or 8)) { error = "--pc-step must be 1, 2, 4 or 8"; return null; }
        if (set.Scene.Length > 0 && o.OutFile is not null) { error = "--out-file cannot be combined with the 3D formats; give -o"; return null; }

        // ---- the parallax value checks, so a typo fails here rather than after a level-0 build
        string container = o.ParallaxFormat ?? "gif";
        if (!Animation.Containers.Contains(container)) { error = $"--parallax-format must be one of {string.Join(", ", Animation.Containers)}"; return null; }
        if (o.ParallaxEncoder is not null and not ("lux" or "ffmpeg")) { error = "--parallax-encoder must be lux or ffmpeg"; return null; }
        bool ffmpegEncoder = o.ParallaxEncoder == "ffmpeg" || container == "avif";
        if (o.ParallaxQuality is not null && container != "webp") { error = "--parallax-quality is the WebP encoder quality; pass --parallax-format webp"; return null; }
        if (o.ParallaxQuality is not null && !ffmpegEncoder) { error = "Lux's own WebP writer is lossless; --parallax-quality is the quality of ffmpeg's lossy WebP — add --parallax-encoder ffmpeg"; return null; }
        if (o.ParallaxCrf is not null && container != "avif") { error = "--parallax-crf is the AVIF encoder crf; pass --parallax-format avif"; return null; }
        if (o.ParallaxLoop is not null and not ("pingpong" or "forward")) { error = "--parallax-loop must be pingpong or forward"; return null; }
        if (o.ParallaxFill is not null and not ("donors" or "inpaint" or "none")) { error = "--parallax-fill must be donors, inpaint or none"; return null; }
        if (o.ParallaxPath is not null and not ("sweep" or "arc" or "line")) { error = "--parallax-path must be sweep, arc or line (the orbit is its own format, parallax-orbit)"; return null; }
        if (o.ParallaxAnaglyph is not null and not ("dubois" or "colour" or "color" or "grey" or "gray")) { error = "--parallax-anaglyph must be dubois, color or gray"; return null; }
        if (o.ParallaxConverge is not null && o.ParallaxConverge is not ("auto" or "none" or "off") && !double.TryParse(o.ParallaxConverge, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        { error = "--parallax-converge must be meters, auto or none"; return null; }
        if (px.Length > 0 && o.Rotate is not null and not 0)
        { error = "--rotate cannot be combined with the parallax formats: the rig geometry is in the unrotated image's axes"; return null; }
        if (o.Transform is not null && (o.Rotate ?? 0) != 0) { error = "--transform and --rotate cannot be combined"; return null; }
        if (px.Length > 0 && o.Transform is not null) { error = "--transform cannot be combined with the parallax formats"; return null; }
        if (o.Upright && (o.Transform is not null || (o.Rotate ?? 0) != 0)) { error = "--upright cannot be combined with --transform or --rotate"; return null; }
        if (o.Upright && px.Length > 0) { error = "--upright cannot be combined with the parallax formats: the rig geometry is in the unrotated image's axes"; return null; }
        if (o.LensProfile is < 0 or > 3) { error = "--lens-profile must be 0-3"; return null; }

        // ---- ffmpeg (AVIF, or asked for), checked before anything renders; GIF, APNG and WebP are Lux's own writers
        VisualFormats.UseFfmpeg = o.ParallaxEncoder == "ffmpeg";
        if (px.Any(ParallaxFormats.IsAnimated) && ffmpegEncoder && Animation.Ffmpeg() is null)
        {
            error = "ffmpeg not found on PATH. " + (container == "avif" ? "AVIF animations are encoded by ffmpeg" : "--parallax-encoder ffmpeg was given")
                  + " (the animated parallax formats: " + string.Join(", ", px.Where(ParallaxFormats.IsAnimated).Select(ParallaxFormats.Name)) + ").\n"
                  + "       GIF, APNG and WebP need no ffmpeg: Lux writes them itself (the default). Nothing has been written.";
            return null;
        }

        // ---- --out-file: one input × one format × that format emitting one file
        int fileCount = formats.Length + set.Still.Length + (depth ? 2 : 0) + px.Length;   // `depth` writes the .f32 AND the .jpg preview
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
        // ---- the depth effect
        if (o.DepthFNumber is float dfn && !(dfn > 0f)) { error = "--f-number must be greater than 0"; return null; }
        if (o.FocusDepth is float fdm && !(fdm > 0f)) { error = "--focus-depth must be greater than 0 (millimeters)"; return null; }
        if (o.FocusDepth is not null && o.FocusPoint is not null) { error = "--focus-depth and --focus-point both set the focus distance — give one"; return null; }
        if (o.FocusPoint is { } fpt && !(fpt.X >= 0f && fpt.X <= 1f && fpt.Y >= 0f && fpt.Y <= 1f)) { error = "--focus-point takes x,y, each between 0 and 1 (0,0 = top left of the exported image)"; return null; }
        if ((o.FocusDepth is not null || o.FocusPoint is not null) && o.DepthFNumber is null)
        { error = "--focus-depth / --focus-point choose where the depth effect focuses; they need --f-number"; return null; }
        if (o.DepthFNumber is not null && (o.Rotate ?? 0) != 0 && o.FocusPoint is not null)
        { error = "--focus-point is given in the unrotated image; it cannot be combined with --rotate"; return null; }
        if (o.DofRendering is not null and not ("lumen" or "lux")) { error = "--dof-rendering takes lumen or lux"; return null; }

        if (o.MemoryBudgetGb is double mb && !(mb > 0)) { error = "--memory-budget takes a size in GB greater than 0"; return null; }
        if (o.DngHighlights is not null and not ("clip" or "keep")) { error = "--dng-highlights takes clip or keep"; return null; }
        bool keepDngHighlights = o.DngHighlights == "keep";
        // ---- tone & color
        if (o.Sharpening is not null && o.JpegSharpening is not null) { error = "--sharpening and --jpeg-sharpening set the same thing — give one"; return null; }
        Adjustments? adjustments = null;
        if (o.HasToneAdjustments)
        {
            string? Range(string flag, float? v, float lo, float hi, string unit = "")
                => v is float x && !(x >= lo && x <= hi) ? FormattableString.Invariant($"{flag} must be between {lo} and {hi}{unit}") : null;
            error = Range("--exposure", o.Exposure, Adjustments.ExposureMin, Adjustments.ExposureMax, " (stops)")
                 ?? Range("--contrast", o.Contrast, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--temperature", o.Temperature, Adjustments.TemperatureMin, Adjustments.TemperatureMax, " (kelvin)")
                 ?? Range("--tint", o.Tint, Adjustments.TintMin, Adjustments.TintMax)
                 ?? Range("--vibrance", o.Vibrance, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--saturation", o.Saturation, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--sharpening", o.Sharpening, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--shadows", o.Shadows, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--highlights", o.Highlights, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--clarity", o.Clarity, Adjustments.PercentMin, Adjustments.PercentMax)
                 ?? Range("--highlight-recovery", o.HighlightRecovery, HighlightRecovery.AmountMin, HighlightRecovery.AmountMax);
            if (error is not null) return null;
            adjustments = new Adjustments
            {
                Exposure = o.Exposure ?? 0f, Contrast = o.Contrast ?? 0f, Temperature = o.Temperature, Tint = o.Tint,
                Vibrance = o.Vibrance ?? 0f, Saturation = o.Saturation ?? 0f, Sharpening = o.Sharpening ?? o.JpegSharpening ?? Adjustments.GuiSharpening,
                Shadows = o.Shadows ?? 0f, Highlights = o.Highlights ?? 0f, Clarity = o.Clarity ?? 0f,
                HighlightRecovery = o.HighlightRecovery ?? 0f, GamutCompress = o.GamutCompress,
                // --sharpening is the monotone control of Lux. --jpeg-sharpening is Lumen's renderer property with Lumen's response.
                MonotoneSharpening = o.Sharpening is not null,
            }.Validate();
        }
        // ---- noise
        NoiseControls? noise = null;
        if (o.GrainStrength is not null || o.DenoiseThreshold is not null)
        {
            if (o.GrainStrength is float gs && !(gs >= NoiseControls.GrainStrengthMin && gs <= NoiseControls.GrainStrengthMax))
            { error = FormattableString.Invariant($"--grain-strength must be between {NoiseControls.GrainStrengthMin} and {NoiseControls.GrainStrengthMax} (1 = Lumen's grain, 0 = none)"); return null; }
            if (o.DenoiseThreshold is float dt && !(dt >= NoiseControls.DenoiseThresholdMin && dt <= NoiseControls.DenoiseThresholdMax))
            { error = FormattableString.Invariant($"--denoise-threshold must be between {NoiseControls.DenoiseThresholdMin} and {NoiseControls.DenoiseThresholdMax} (1 = Lumen's threshold)"); return null; }
            noise = new NoiseControls { GrainStrength = o.GrainStrength ?? 1f, DenoiseThreshold = o.DenoiseThreshold ?? 1f };
            if (!noise.IsLumen) notes.Add("--grain-strength / --denoise-threshold: the image tiles kept in a capture's analysis cache are Lumen's, so they are not read for this run; the kept geometry and depth map are");
        }
        if (set.Scene.Length > 0) notes.Add("3D formats: one capture gives a relief that looks right inside a viewing angle of a few degrees, not a full 3D scene" + (o.DepthRefine ? "" : "; --depth-refine gives cleaner results"));
        if (o.TeleCheck) notes.Add("--tele-check: each telephoto module is checked against the reference picture after the registration and corrected when it is clearly off; the kept image tiles of the analysis cache are not read for this run");
        if (o.NoCache) notes.Add("--no-cache: the capture's analysis cache is neither read nor written");
        if (o.NoEdits && o.Edits is not null) { error = "--no-edits and --edits are alternatives; pass one"; return null; }
        if (o.NoEdits) notes.Add("--no-edits: saved depth edits are ignored; the depth is the one computed from the capture");
        else if (o.Edits is not null) notes.Add($"--edits {o.Edits}: that variant of each capture's saved depth edits is used wherever the depth is read");

        Say("--rotate", o.Rotate); Say("--fnum", o.FNumber); Say("--iso", o.Iso); Say("--focal", o.Focal);
        Say("--dng-cs", o.DngCs); Say("--dng-tone", o.DngTone); Say("--dng-comp", o.DngComp);
        Say("--jpeg-cs", o.JpegCs); Say("--jpeg-quality", o.JpegQuality); Say("--jpeg-sub", o.JpegSub);
        if (o.JpegV2) applied.Add("--jpeg-v2");
        Say("--jpeg-sharpening", o.JpegSharpening);
        Say("--exposure", o.Exposure); Say("--contrast", o.Contrast); Say("--temperature", o.Temperature); Say("--tint", o.Tint);
        Say("--vibrance", o.Vibrance); Say("--saturation", o.Saturation); Say("--sharpening", o.Sharpening);
        Say("--shadows", o.Shadows); Say("--highlights", o.Highlights); Say("--clarity", o.Clarity);
        Say("--highlight-recovery", o.HighlightRecovery); if (o.GamutCompress) applied.Add("--gamut-compress");
        if (keepDngHighlights) applied.Add("--dng-highlights keep");
        Say("--jpeg-modify", o.JpegModify?.ToString("yyyy-MM-ddTHH:mm:ss")); Say("--jpeg-comment", o.JpegComment); Say("--jpeg-software", o.JpegSoftware);
        Say("--grain-strength", o.GrainStrength?.ToString(System.Globalization.CultureInfo.InvariantCulture)); Say("--denoise-threshold", o.DenoiseThreshold?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Say("--hdr-cs", o.HdrCs);
        Say("--dof-rendering", o.DofRendering == "lux" ? "lux" : null);
        Say("--f-number", o.DepthFNumber); Say("--focus-depth", o.FocusDepth); if (o.FocusPoint is { } sfp) applied.Add(FormattableString.Invariant($"--focus-point {sfp.X},{sfp.Y}"));
        if (applied.Count > 0)
            notes.Add($"adjustments: {string.Join(", ", applied)} — these depart from the values the .lri and Lumen's own rules supply");

        var extras = set.Still.Select(StillFormats.NameOf).Concat(set.LensFrames ? new[] { "lens-frames" } : Array.Empty<string>()).Concat(px.Select(ParallaxFormats.Name)).ToList();
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

        // the settings given here override the ones a capture's saved edits carry (an imported Lumen session); the rest come from there
        var overridden = new HashSet<string>();
        if (o.DepthFNumber is not null) overridden.Add("f-number");
        if (o.FocusDepth is not null || o.FocusPoint is not null) overridden.Add("focus");
        if (o.DofRendering is not null) overridden.Add("dof-rendering");
        if (o.Rotate is not null || o.Transform is not null) overridden.Add("transform");
        foreach (var (v, id) in new (object?, string)[] { (o.Exposure, "2"), (o.Temperature, "3"), (o.Tint, "4"), (o.Shadows, "5"), (o.Highlights, "6"), (o.Contrast, "7"),
                                                          (o.Saturation, "8"), (o.Vibrance, "9"), (o.Clarity, "10"), (o.Sharpening ?? o.JpegSharpening, "13") })
            if (v is not null) overridden.Add(id);
        if (o.HighlightRecovery is not null) overridden.Add("highlight-recovery");
        if (o.GamutCompress) overridden.Add("gamut-compress");

        var req = new ExportRequest
        {
            OverriddenSettings = overridden,
            Formats = formats,
            DepthMap = depth,
            Level = o.Level,
            Size = o.Size,
            Origin = o.Origin,
            Rotate = o.Rotate ?? 0,
            Transform = o.Transform,
            FNumber = o.FNumber,
            DepthEffectFNumber = o.DepthFNumber,
            FocusDepthMm = o.FocusDepth,
            FocusPoint = o.FocusPoint,
            DepthEffectRendering = o.DofRendering == "lux" ? Lux.Engine.Pipeline.Dof.DofRendering.Lux : Lux.Engine.Pipeline.Dof.DofRendering.Lumen,
            UseSavedEdits = !o.NoEdits,
            TeleCheck = o.TeleCheck,
            CacheUse = o.NoCache ? CacheUse.None : CacheUse.ReadWriteGeometry,
            MemoryBudgetBytes = o.MemoryBudgetGb is double gb ? (long)(gb * (1L << 30)) : null,
            EditsVariant = o.Edits,
            Iso = o.Iso,
            FocalLengthMm = o.Focal,
            DngColorSpace = o.DngCs,
            ToneMappingType = o.DngTone,
            Compression = o.DngComp,
            DngKeepHighlights = keepDngHighlights,
            JpegColorSpace = o.JpegCs,
            JpegQuality = o.JpegQuality,
            JpegSubsampling = o.JpegSub,
            JpegSharpening = o.JpegSharpening ?? DisplayIspTuning.GuiSharpeningProperty,
            Adjustments = adjustments,          // null unless a tone & color flag was given: Lumen's bytes
            JpegV2 = o.JpegV2,          // deliberately NOT LUX_DISPLAY_V2: the flag is the supported route and
                                        // `convert` stays immune to the environment variable.
            JpegModifyTime = o.JpegModify,
            JpegComment = o.JpegComment,
            JpegSoftware = o.JpegSoftware,
            Noise = noise,
            HdrColorSpace = o.HdrCs,
            Stills = set.Still, Tiff = tiff, GainMap = gainMap,
            LuxFormats = set.LensFrames || px.Length > 0 ? VisualFormats.Writer : null,
            Scene = set.Scene.Length > 0 ? new SceneRequest
            {
                Formats = set.Scene, MeshTriangles = o.MeshTriangles ?? 250_000, MeshRelief = o.MeshDepth != "metric", PointStep = o.PcStep ?? 4,
            } : null,
            Stereo = set.Stereo.Length > 0 ? new StereoRequest
            {
                Formats = set.Stereo, HalfWidth = o.StereoLayout == "half", Size = o.StereoSize ?? 0, Container = o.StereoFormat ?? "gif",
                Source = o.StereoSource switch { "real" => StereoSource.Real, "depth" => StereoSource.Depth, _ => StereoSource.Auto },
            } : null,
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

    public static string NameOf(ExportImageFormat f) => ExportFormats.NameOf(f);

    /// <summary>How the camera was held for a capture file: the degrees clockwise (0, 90, 180, 270) that show it upright; 0 when the
    /// camera recorded no orientation. Reads the first block of the file only.</summary>
    public static int HeldTurnOf(string lriPath)
    {
        using var fs = File.OpenRead(lriPath);
        var header = Lux.Engine.Lri.LriFile.ReadFirstBlockHeader((offset, count) =>
        {
            var b = new byte[(int)Math.Max(0, Math.Min(count, fs.Length - offset))];
            fs.Position = offset; fs.ReadExactly(b);
            return b;
        }, fs.Length);
        return header is null ? 0 : Math.Max(0, Lux.Store.HeldTurn.Degrees(header));
    }

    /// <summary>The quarter turn of a saved transform's matrix (0 for none or a mirror).</summary>
    public static int TurnOf(float[]? m)
    {
        if (m is null || m.Length < 5) return 0;
        int a = (int)MathF.Round(m[0]), b = (int)MathF.Round(m[1]), d = (int)MathF.Round(m[3]), e = (int)MathF.Round(m[4]);
        if (a * e - b * d < 0) return 0;
        return (a, b, d, e) switch { (-1, 0, 0, -1) => 180, (0, -1, 1, 0) => 90, (0, 1, -1, 0) => 270, _ => 0 };
    }

    /// <summary>`--upright`: the held turn of a capture whose crop / rotation was never edited (no saved transform and no
    /// geometry decision — `CaptureRecipe.IsGeometryDecided`), as Lumen's own rotate right / left — bit for bit the transform the desktop app
    /// saves for "Rotate as the camera was held" and applies to its default exports. Returns the request unchanged otherwise (held upright, no
    /// gravity recorded, or a saved crop / rotation, which the export follows anyway), with the reason.</summary>
    public static (ExportRequest Request, string Note) Upright(string lriPath, ExportRequest req, Lux.Store.StoreCaptureContext? ctx)
    {
        var lri = Lux.Engine.Lri.LriFile.Load(lriPath);
        int held = Lux.Store.HeldTurn.Degrees(lri);
        if (held <= 0) return (req, held == 0 ? "--upright: held upright, nothing to turn" : "--upright: the camera recorded no orientation, nothing turned");
        if (req.UseSavedEdits && ctx is not null)
        {
            var (variant, recipe) = Lux.Store.SavedDepthEdits.LoadParts(ctx.Context);
            if (Lux.Store.CaptureRecipe.IsGeometryDecided(recipe, variant?.Transform))
                return (req, "--upright: the photo's saved crop / rotation decides its orientation; nothing added");
        }
        var id = ExportTransform.Identity(Lux.Store.LumenEditImport.Levels(lri).ExportDims[0]);
        var t = Lux.Store.HeldTurn.Transform(id.AspectW, id.AspectH, held);
        if (t is null) return (req, $"--upright: aspect {id.AspectW}:{id.AspectH} is not one Lumen's rotate is known for; nothing turned");
        var over = new HashSet<string>(req.OverriddenSettings) { "transform" };
        return (req with { Transform = t, OverriddenSettings = over }, $"--upright: turned {held}° clockwise as the camera was held (Lumen's rotate)");
    }

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
