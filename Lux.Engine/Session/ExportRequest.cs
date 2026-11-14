using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Session;

/// <summary>
/// The export configuration as data — everything `convert` can ask for, in one record.
///
/// <para>The grid group (<see cref="Level"/>/<see cref="Size"/>/<see cref="Origin"/>) picks the pixel grid and
/// nothing else. The adjustment groups depart from the 1:1 Lumen output: every one of them is <c>null</c>/0/false
/// by default, and a null means "the value Lumen itself would use", which for the colour-space property is the
/// per-format rule of Lumen (<see cref="ExportTuningOverride.ColorSpaceProperty"/>).</para>
///
/// <para><see cref="LensFrames"/> and <see cref="Parallax"/> are the Lux-only formats (`lens-frames`, `parallax-*`):
/// additive outputs that never change the Lumen ones, sharing the same state and render (<see cref="ExportSession"/>).
/// The engine renders their inputs; the front end that offers them supplies the writer (<see cref="LuxFormats"/>).</para>
/// </summary>
public sealed record ExportRequest
{
    // ---- what to write -------------------------------------------------------------------------------------
    /// <summary>The `ExportImageFormat`s this run produces. One <see cref="ExportState"/>, hence one PipelineCache,
    /// serves all of them; within that there are only two distinct renders (see <see cref="Exporter"/>).</summary>
    public IReadOnlyList<ExportImageFormat> Formats { get; init; } = ExportFormats.DefaultFormats;

    /// <summary>Threads for the tile render inside this one export: the pipeline tiles, the fusion / reference /
    /// telephoto cache tiles behind them, and the output-ISP tiles of the JPEG. 1 = the sequential order. The pixels
    /// are the same at any count — every tile is a pure function of the capture, memoised once.</summary>
    public int RenderThreads { get; init; } = 1;

    /// <summary>Progress sink: one <see cref="ProgressUpdate"/> per phase start and per unit of work, from any thread.
    /// Null = no reporting (the default; reporting never changes the output).</summary>
    public Action<ProgressUpdate>? Progress { get; init; }

    /// <summary>Cancellation: checked between phases and tiles. A cancelled export throws <see cref="OperationCanceledException"/>
    /// and leaves no partial output it started writing.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>Also write the stereo depth map (`&lt;stem&gt;_depth.f32` + `_depth.jpg`) — a Lux extra, not a
    /// Lumen `ExportImageFormat`. Forces the level-0 build exactly as `jpg+depth` does.</summary>
    public bool DepthMap { get; init; }

    /// <summary>`lens-frames`: every module as a display JPEG through the ported module ISP. Needs no export state.</summary>
    public LensRequest? LensFrames { get; init; }

    /// <summary>The `parallax-*` formats and their flags. `parallax-wiggle` needs only the module renders; every
    /// other one reads the metric depth and so forces the level-0 build like `jpg+depth`.</summary>
    public ParallaxRequest? Parallax { get; init; }

    /// <summary>The writer of the Lux-only formats (<see cref="LensFrames"/>, <see cref="Parallax"/>). The engine produces their inputs
    /// (the module renders, the JPEG render, the depth on the grid); encoding them (animation containers) is the front end's.
    /// Required when either is requested.</summary>
    public LuxFormatsWriter? LuxFormats { get; init; }

    /// <summary>The capture's persistent data (saved depth edits, the analysis cache) — the <c>LuxImageContext</c> folder next to the
    /// capture, implemented outside the engine. Null = none: no saved edits are applied and no cache is read or written.</summary>
    public ICaptureContext? Context { get; init; }

    /// <summary>What the export may do with the analysis cache of <see cref="Context"/>. The cache never changes a pixel: an export
    /// from cached state is byte-identical to one computed from scratch.</summary>
    public CacheUse CacheUse { get; init; } = CacheUse.Read;

    /// <summary>Memory budget for the full-size (level-0) render, in bytes; null = unbounded (today's schedule, the fastest). Under a budget
    /// the level-0 inputs and tiles are generated, used and released in bands of the export window — the same pixels, more time.</summary>
    public long? MemoryBudgetBytes { get; init; }

    /// <summary>Explicit output path. Only valid when the run makes exactly one file; otherwise use
    /// <see cref="OutDirectory"/> + <see cref="Stem"/>, which names `&lt;stem&gt;&lt;ext&gt;` per format.</summary>
    public string? OutFile { get; init; }
    /// <summary>Output directory (created if missing) for the `&lt;stem&gt;.&lt;ext&gt;` naming.</summary>
    public string? OutDirectory { get; init; }
    /// <summary>Filename stem for the <see cref="OutDirectory"/> naming — normally the `.lri`'s.</summary>
    public string? Stem { get; init; }

    // ---- the grid ------------------------------------------------------------------------------------------
    /// <summary>Export level; 0 (and null) = Lumen's own full-size export window.</summary>
    public int? Level { get; init; }
    /// <summary>Explicit output size, in place of the level's export-window dimensions.</summary>
    public (int W, int H)? Size { get; init; }
    /// <summary>Level-0 export-window origin override (`ExportLevels.L16`).</summary>
    public (int X, int Y)? Origin { get; init; }

    // ---- shared adjustments --------------------------------------------------------------------------------
    /// <summary>0, 90, 180 or 270 — baked into the pixels via <see cref="ExportTransform.Rotate"/>, upstream of every
    /// writer, so it reaches all four raster formats and the depth map.</summary>
    public int Rotate { get; init; }
    /// <summary>A photo transform as Lumen stores it per photo (crop, aspect, rotation / flip matrix — a session's Transform record), in place
    /// of <see cref="Rotate"/>; null = none. Without an explicit <see cref="Size"/> the output follows Lumen's full-size rule: the level's dims
    /// times the crop's width and height.</summary>
    public ExportTransform? Transform { get; init; }
    /// <summary>Exif FNumber override; null = <see cref="ExportWindow.AllInFocusFNumber"/> from the capture.</summary>
    public float? FNumber { get; init; }
    /// <summary>The depth effect: Lumen's f-number setting (ParamFloat 0). Below the capture's all-in-focus f-number
    /// (<see cref="ExportWindow.AllInFocusFNumber"/>) every fused tile comes from the depth-effect cache instead of the
    /// PipelineCache; at or above it the image is unchanged. Either way it is the Exif FNumber, as in
    /// Lumen, unless <see cref="FNumber"/> overrides the tag. Null = no depth effect.</summary>
    public float? DepthEffectFNumber { get; init; }
    /// <summary>The depth effect's focus distance in millimetres (ParamFloat 1); null = <see cref="FocusPoint"/>, else
    /// Lumen's DefaultFocusDepth.</summary>
    public float? FocusDepthMm { get; init; }
    /// <summary>Focus on the scene depth at this point, normalised to the export window — Lumen's click to focus
    /// (`getDepthAtPoint`, then the focus setting).</summary>
    public (float X, float Y)? FocusPoint { get; init; }
    /// <summary>Exif ISO override; null = 100·image_gain truncated, from the capture.</summary>
    public int? Iso { get; init; }
    /// <summary>Exif FocalLength override, mm; null = the header's `ImageFocalLength`.</summary>
    public int? FocalLengthMm { get; init; }

    // ---- DNG -----------------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the DNG; null = the per-format rule (0 = none, what Lumen writes).</summary>
    public int? DngColorSpace { get; init; }
    /// <summary>`tone_mapping.type` written into the DNG tags; null = the renderer level tuning's own value.</summary>
    public string? ToneMappingType { get; init; }
    /// <summary>DNG Compression tag override.</summary>
    public int? Compression { get; init; }

    // ---- JPEG ----------------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the JPEG (and JPEG+GDepth); null = the per-format rule (4 = srgb).</summary>
    public int? JpegColorSpace { get; init; }
    /// <summary>Embeds the ICC profile of the JPEG colour space (<see cref="Lux.Engine.Pipeline.Export.IccProfiles"/>; sRGB by default). The
    /// default is off. Lumen's JPEGs have no profile, and the default output does not change.</summary>
    public bool JpegEmbedIcc { get; init; }
    /// <summary>libjpeg quality; null = the literal 0x62 = 98 in `exportImage`.</summary>
    public int? JpegQuality { get; init; }
    /// <summary>libjpeg subsampling id; null = the literal 2 = 4:2:0.</summary>
    public int? JpegSubsampling { get; init; }
    /// <summary>Select the v2 tone-mapping gate of the renderer (and its branch-B histogram).</summary>
    public bool JpegV2 { get; init; }
    /// <summary>The renderer's `ParamFloat 13` (sharpening slider) for the JPEG / JPEG+GDepth display render. Lumen's GUI export renderer
    /// carries 5.0 (the `CIAPI::ApplyTuning` mode-0 value) → `tone_mapping.sharpening = 1.0`, and that is what its JPGs contain; 0 reproduces
    /// Lumen's renderer without `CIAPI::ApplyTuning`. PPM/HDR keep 0: the GUI never exports them.</summary>
    public float JpegSharpening { get; init; } = DisplayIspTuning.GuiSharpeningProperty;
    /// <summary>Lumen's tone and colour settings for the 8-bit output render (JPEG, JPEG+GDepth, the parallax source); null = none
    /// (today's output). When set, its <see cref="Pipeline.Adjustments.Sharpening"/> replaces <see cref="JpegSharpening"/>. DNG, PPM and HDR
    /// never run the output stage, so they do not change (`Pipeline/Adjustments.cs`).</summary>
    public Pipeline.Adjustments? Adjustments { get; init; }
    /// <summary>Exif/COM ModifyDate override; null = now.</summary>
    public DateTime? JpegModifyTime { get; init; }
    /// <summary>COM marker text; null = the text Lumen writes (its library name and version).</summary>
    public string? JpegComment { get; init; }
    /// <summary>Exif Software override.</summary>
    public string? JpegSoftware { get; init; }

    // ---- HDR / PPM -----------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the `.hdr`; null = the per-format rule (1 = linear_srgb).</summary>
    public int? HdrColorSpace { get; init; }
    // There is no PPM equivalent on purpose: the property has no effect for fmt 1, because the `(fmt|4)==4` gate means a PPM
    // never reads the output tuning. The rule below still supplies the value Lumen would write.

    /// <summary>A louder, per-command log: `export:`, `tags:`, `tuning:`, `display tuning`, `render` and `jpeg:` lines, and a
    /// two-space indent on the engine log, in place of `convert`'s summary lines.</summary>
    public bool Verbose { get; init; }

    /// <summary>The colour-space property (renderer property 0x13) this run writes for <paramref name="f"/>.
    /// The default is Lumen's own per-format rule — `fmt &lt;= 1 → 4 (srgb)`,
    /// `fmt == 2 → 0 (none)`, `fmt == 3 → 1 (linear_srgb)`; fmt 4 never reaches that switch (it throws), so it
    /// inherits the JPEG value.</summary>
    public int ColorSpaceOf(ExportImageFormat f) => f switch
    {
        ExportImageFormat.Dng => DngColorSpace ?? ExportTuningOverride.ColorSpaceProperty(f),
        ExportImageFormat.Jpeg => JpegColorSpace ?? ExportTuningOverride.ColorSpaceProperty(f),
        ExportImageFormat.JpegGDepth => JpegColorSpace ?? 4,
        ExportImageFormat.Hdr => HdrColorSpace ?? ExportTuningOverride.ColorSpaceProperty(f),
        ExportImageFormat.Ppm => ExportTuningOverride.ColorSpaceProperty(f),
        _ => throw new ArgumentOutOfRangeException(nameof(f), $"unsupported ExportImageFormat {f}"),
    };

    /// <summary>Whether any requested output reads the metric depth (`jpg+depth`, the depth pair, the depth-based
    /// parallax formats) — and therefore needs the level-0 registration state.</summary>
    public bool NeedsDepth =>
        (Formats?.Contains(ExportImageFormat.JpegGDepth) ?? false) || DepthMap || DepthEffectNeedsDepth
        || (Parallax?.Any(ParallaxRequest.NeedsDepthFormat) ?? false);

    /// <summary>Saved depth edits (the capture's `LuxImageContext` database): false = ignore them and use the freshly computed
    /// depth. They only matter to outputs that read the depth.</summary>
    public bool UseSavedEdits { get; init; } = true;
    /// <summary>The variant of the saved edits to use; null = the database's active variant.</summary>
    public string? EditsVariant { get; init; }

    /// <summary>The settings this request sets explicitly, which the capture's saved render settings (<see cref="ICaptureContext.LoadSavedState"/>,
    /// e.g. an imported Lumen session) then do not replace: "f-number" (<see cref="DepthEffectFNumber"/>), "focus" (<see cref="FocusDepthMm"/> /
    /// <see cref="FocusPoint"/>), "transform" (<see cref="Rotate"/> / <see cref="Transform"/>), and the `ParamFloat` id ("2" … "13") of each tone
    /// or colour setting of <see cref="Adjustments"/> ("13" also stands for <see cref="JpegSharpening"/>). Everything else comes from the saved
    /// state when one applies; with <see cref="UseSavedEdits"/> false none does.</summary>
    public IReadOnlySet<string> OverriddenSettings { get; init; } = new HashSet<string>();

    /// <summary>Set by <see cref="Exporter.Run(string, ExportRequest, Action{string}?)"/> once the capture's all-in-focus
    /// f-number is known: the requested depth effect is below it, so the render reads the depth cache.</summary>
    public bool DepthEffectNeedsDepth { get; init; }

    /// <summary>A depth-effect tile cache already made for this state, f-number and focus (<see cref="CaptureSession"/> keeps one per setting so
    /// that its renders share the tiles); null = the session makes its own. Its tiles are the same either way.</summary>
    public Lux.Engine.Pipeline.Dof.DofTileCache? DepthEffectCache { get; init; }

    /// <summary>Whether the run needs an <see cref="ExportState"/> at all. `lens-frames` and `parallax-wiggle` alone
    /// do not: they are module renders, with no registration and no PipelineCache.</summary>
    public bool NeedsState => (Formats?.Count ?? 0) > 0 || NeedsDepth;
}

/// <summary>What an export may do with a capture's analysis cache (<see cref="ExportRequest.Context"/>).</summary>
public enum CacheUse
{
    /// <summary>Neither read nor write the cache.</summary>
    None = 0,
    /// <summary>Use whatever the cache holds; never write.</summary>
    Read = 1,
    /// <summary>Use what it holds and store the geometry + depth this export computes (never the image tiers).</summary>
    ReadWriteGeometry = 2,
}

/// <summary>Writes the Lux-only formats of a request (<see cref="ExportRequest.LensFrames"/>, <see cref="ExportRequest.Parallax"/>) from the
/// export's session — null when the request needs no <see cref="ExportState"/> — into <paramref name="outDir"/>, adding each written file
/// to <paramref name="outputs"/> and each format this capture cannot produce to <paramref name="refusals"/>.</summary>
public delegate void LuxFormatsWriter(LriFile lri, ExportRequest req, ExportSession? session, string outDir, string stem,
                                      List<Exporter.Output> outputs, List<string> refusals, Action<string>? log);

/// <summary>An <see cref="ExportRequest"/> this capture cannot satisfy — as opposed to a bug in the pipeline, which
/// still comes out as whatever the engine throws. A command turns it into `error: …` and exit 2.</summary>
public sealed class ExportRequestException : InvalidOperationException
{
    public ExportRequestException(string message) : base(message) { }
}

/// <summary>The `--formats` spellings and file extensions of Lumen's export formats.</summary>
public static class ExportFormats
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

    public static string NameOf(ExportImageFormat f) => f switch
    {
        ExportImageFormat.Jpeg => "jpg",
        ExportImageFormat.JpegGDepth => "jpg+depth",
        ExportImageFormat.Dng => "dng",
        ExportImageFormat.Hdr => "hdr",
        ExportImageFormat.Ppm => "ppm",
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>The `--formats` spelling of a parallax format (`parallax-wiggle`, …).</summary>
    public static string NameOf(ParallaxFormat f) => "parallax-" + f switch
    {
        ParallaxFormat.Wiggle => "wiggle",
        ParallaxFormat.WiggleInterp => "wiggle-interp",
        ParallaxFormat.Orbit => "orbit",
        ParallaxFormat.Single => "single",
        ParallaxFormat.Rack => "rack",
        ParallaxFormat.Dolly => "dolly",
        ParallaxFormat.Dof => "dof",
        ParallaxFormat.Anaglyph => "anaglyph",
        ParallaxFormat.CrossEye => "crosseye",
        ParallaxFormat.Sbs => "sbs",
        ParallaxFormat.Still => "still",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}

/// <summary>The `parallax-*` formats of `convert`. All EXPERIMENTAL: Lumen has no animated or synthesised output, so
/// none of these is held to the 1:1 bar — the base imagery is the pipeline's (the run's own JPEG render and
/// its metric depth, or the module ISP's frames), and everything after that is Lux's own.</summary>
public enum ParallaxFormat
{
    /// <summary>The physical wigglegram: the four real A-group frames, colour-matched, played in spatial order.</summary>
    Wiggle,
    /// <summary>The interpolated wigglegram: N virtual viewpoints along the rig's own axis, DIBR + disocclusion fill.</summary>
    WiggleInterp,
    /// <summary>The same synthesis on a closed circular path.</summary>
    Orbit,
    /// <summary>Single-view 2.5D: the sweep with no multi-view fill, every disocclusion inpainted.</summary>
    Single,
    /// <summary>Animated rack focus.</summary>
    Rack,
    /// <summary>Dolly zoom (pulling back while zooming in).</summary>
    Dolly,
    /// <summary>Synthetic depth of field, one still.</summary>
    Dof,
    /// <summary>Red/cyan anaglyph (Dubois) of a synthesised stereo pair.</summary>
    Anaglyph,
    /// <summary>Cross-eye side-by-side stereo pair.</summary>
    CrossEye,
    /// <summary>Parallel-view side-by-side stereo pair.</summary>
    Sbs,
    /// <summary>One synthesised viewpoint, as a still.</summary>
    Still,
}

/// <summary>The `lens-frames` configuration: each module of the capture as a display JPEG through the ported module ISP.</summary>
public sealed record LensRequest
{
    public float Ev { get; init; } = 0.95f;
    public int Quality { get; init; } = 92;
    /// <summary>Module-ISP config level (0 = full-res, no denoise).</summary>
    public int Level { get; init; }
    public RendererProfile Profile { get; init; } = RendererProfile.Desktop;
    /// <summary>Module filter, e.g. A1,B4; null = every module.</summary>
    public string[]? Modules { get; init; }
    /// <summary>Which firing of a stacked capture: `all`, an index, or null = frame 0 (the frame StackFusion references).</summary>
    public string? Stack { get; init; }
}

/// <summary>Every `--parallax-*` value, as data. Null means the per-format default.</summary>
public sealed record ParallaxRequest
{
    public IReadOnlyList<ParallaxFormat> Formats { get; init; } = Array.Empty<ParallaxFormat>();
    /// <summary>Animation container: gif (default), webp, avif, apng.</summary>
    public string Container { get; init; } = "gif";
    /// <summary>Long edge of the working image in px (0 = native for `parallax-wiggle`).</summary>
    public int Size { get; init; } = 1600;
    public int? Ms { get; init; }
    public int Frames { get; init; } = 24;
    /// <summary>pingpong | forward; null = pingpong, except for the closed orbit.</summary>
    public string? Loop { get; init; }
    /// <summary>donors | inpaint | none.</summary>
    public string Fill { get; init; } = "donors";
    /// <summary>sweep | arc | line — the path of `parallax-wiggle-interp` and `parallax-single` (orbit is its own format).</summary>
    public string Path { get; init; } = "sweep";
    public double Baseline { get; init; } = 71.49;
    /// <summary>auto (median depth) | none | metres.</summary>
    public string Converge { get; init; } = "auto";
    public (int X, int Y)? ConvergeAt { get; init; }
    public double? Ipd { get; init; }
    /// <summary>dubois | colour | grey.</summary>
    public string Anaglyph { get; init; } = "dubois";
    public double? FocusM { get; init; }
    public (int X, int Y)? FocusAt { get; init; }
    public double Aperture { get; init; } = 20;
    public int Layers { get; init; } = 8;
    public (double M1, double M2)? RackM { get; init; }
    public ((int X, int Y) A, (int X, int Y) B)? RackAt { get; init; }
    public double? SubjectM { get; init; }
    public (int X, int Y)? SubjectAt { get; init; }
    public double Dz { get; init; } = 400;
    public (double X, double Y) T { get; init; } = (40, 0);
    public int? Quality { get; init; }
    public int? Crf { get; init; }
    /// <summary>`parallax-wiggle` frame order: sweep (default) | label | an explicit module list.</summary>
    public string? Order { get; init; }
    /// <summary>`parallax-wiggle` convergence pin, native sensor pixels.</summary>
    public (int X, int Y, int W, int H)? Pivot { get; init; }

    public bool Has(ParallaxFormat f) => Formats.Contains(f);
    public bool Any(Func<ParallaxFormat, bool> p) => Formats.Any(p);

    /// <summary>Every format but the physical wigglegram synthesises from the metric depth, and so needs the level-0 build.</summary>
    public static bool NeedsDepthFormat(ParallaxFormat f) => f != ParallaxFormat.Wiggle;
}
