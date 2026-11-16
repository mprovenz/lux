using System.Globalization;

namespace Lux.Cli;

/// <summary>
/// The global `--flag` set. A flag that configures one format or command carries that prefix (`--dng-tone`,
/// `--jpeg-quality`, `--lens-ev`, `--parallax-size`, `--isp-level`); the shared ones (`--level`, `--size`, `--rotate`, the Exif
/// trio) keep their bare names and document their scope. A flag that does not parse is rejected, not silently
/// reinterpreted — see <see cref="Errors"/>.
/// </summary>
public sealed class Options
{
    // ---- OUTPUT ---------------------------------------------------------------------------------------------
    public string? OutDirectory { get; private set; }
    public string? OutFile { get; private set; }
    public int Threads { get; private set; } = Environment.ProcessorCount;
    /// <summary>`--render-threads`: threads inside each conversion; null = CPU count ÷ the inputs converted at once.</summary>
    public int? RenderThreads { get; private set; }
    /// <summary>convert: output list, e.g. "dng,jpg,hdr,ppm,jpg+depth,depth" (default dng,jpg).</summary>
    public string? Formats { get; private set; }
    /// <summary>convert: shorthand for adding hdr to --formats.</summary>
    /// <summary>convert: alias for adding `depth` to --formats.</summary>

    // ---- GRID (convert / depth) -----------------------------------------------------------------------------
    public int Level { get; private set; }                  // export level, 0 = full size
    /// <summary>`--level` as given: `cache` takes a cache level by name (none, geometry, 13mp, full); `convert` only a number.</summary>
    public string? LevelText { get; private set; }
    /// <summary>`--level` was not a number.</summary>
    public bool LevelIsName { get; private set; }
    /// <summary>convert: neither read nor write the capture's analysis cache.</summary>
    public bool NoCache { get; private set; }
    /// <summary>convert / cache: memory budget for full-size rendering, in GB (null = unbounded, the fastest schedule).</summary>
    public double? MemoryBudgetGb { get; private set; }
    public (int W, int H)? Size { get; private set; }
    public (int X, int Y)? Origin { get; private set; }

    // ---- SHARED ADJUSTMENTS ---------------------------------------------------------------------------------
    public int? Rotate { get; private set; }
    /// <summary>`--transform m0,…,m8;x0,y0,x1,y1`: a photo transform exactly as Lux / Lumen save it (matrix in Lumen's order, crop corners).</summary>
    public Lux.Engine.Pipeline.Export.ExportTransform? Transform { get; private set; }
    public float? FNumber { get; private set; }
    /// <summary>convert: the depth effect's f-number (Lumen's "depth effect" slider).</summary>
    public float? DepthFNumber { get; private set; }
    /// <summary>convert: the depth effect's focus distance in millimeters.</summary>
    public float? FocusDepth { get; private set; }
    /// <summary>convert: the depth effect's focus point, normalized to the exported image (Lumen's click to focus).</summary>
    public (float X, float Y)? FocusPoint { get; private set; }
    /// <summary>convert: ignore the capture's saved depth edits.</summary>
    public bool NoEdits { get; private set; }
    /// <summary>`--upright` (convert, animate): a photo held turned whose crop / rotation was never edited is turned upright with Lumen's own
    /// rotate — as the desktop app exports it by default.</summary>
    public bool Upright { get; private set; }
    /// <summary>convert: the variant of the saved depth edits to use (default: the active one).</summary>
    public string? Edits { get; private set; }
    /// <summary>edits-info: make this variant the active one ("none" = no variant).</summary>
    public string? SetActive { get; private set; }
    public int? Iso { get; private set; }
    public int? Focal { get; private set; }

    // ---- TONE & COLOR (convert: Lumen's editing controls, JPG / jpg+depth only) -----------------------------
    public float? Exposure { get; private set; }
    public float? Contrast { get; private set; }
    public float? Temperature { get; private set; }
    public float? Tint { get; private set; }
    public float? Vibrance { get; private set; }
    public float? Saturation { get; private set; }
    public float? Sharpening { get; private set; }
    public float? Shadows { get; private set; }
    public float? Highlights { get; private set; }
    public float? Clarity { get; private set; }
    /// <summary>`--highlight-recovery`: the amount of the highlight shoulder of Lux, 0 to 100 (0 = Lumen's tone curve).</summary>
    public float? HighlightRecovery { get; private set; }
    /// <summary>`--gamut-compress`: compress colors outside the output color space to its boundary.</summary>
    public bool GamutCompress { get; private set; }
    /// <summary>True when any tone & color control was given.</summary>
    public bool HasToneAdjustments => Exposure is not null || Contrast is not null || Temperature is not null || Tint is not null || Vibrance is not null
                                      || Saturation is not null || Sharpening is not null || Shadows is not null || Highlights is not null || Clarity is not null
                                      || HighlightRecovery is not null || GamutCompress;

    // ---- NOISE (convert: Lux controls of the module ISP, every raster format) -------------------------------------
    /// <summary>`--grain-strength`: scale of the grain that is added after the denoiser (1 = Lumen, 0 = none).</summary>
    public float? GrainStrength { get; private set; }
    /// <summary>`--denoise-threshold`: scale of the denoiser's threshold (1 = Lumen).</summary>
    public float? DenoiseThreshold { get; private set; }

    // ---- DNG ------------------------------------------------------------------------------------------------
    public int? DngCs { get; private set; }
    public string? DngTone { get; private set; }
    public int? DngComp { get; private set; }
    /// <summary>`--dng-highlights`: "clip" (Lumen's file) or "keep" (half-scale samples, BaselineExposure one stop higher).</summary>
    public string? DngHighlights { get; private set; }

    // ---- JPEG -----------------------------------------------------------------------------------------------
    public int? JpegCs { get; private set; }
    public int? JpegQuality { get; private set; }
    public int? JpegSub { get; private set; }
    public bool JpegV2 { get; private set; }
    public float? JpegSharpening { get; private set; }
    public DateTime? JpegModify { get; private set; }
    public string? JpegComment { get; private set; }
    public string? JpegSoftware { get; private set; }

    // ---- HDR ------------------------------------------------------------------------------------------------
    public int? HdrCs { get; private set; }
    // (no --ppm-cs: the property has no effect on fmt 1 — the `(fmt|4)==4` gate means a PPM never reads the output tuning.)

    // ---- TIFF (tiff16, tiff-linear) and the HDR gain-map JPEG (jpg-hdr) -------------------------------------
    /// <summary>`--tiff-cs`: srgb, p3 or adobergb.</summary>
    public string? TiffCs { get; private set; }
    /// <summary>`--tiff-compression`: deflate, lzw or none.</summary>
    public string? TiffCompression { get; private set; }
    /// <summary>`--tiff-depth`: f32 or f16.</summary>
    public string? TiffDepth { get; private set; }
    /// <summary>`--gainmap`: rgb or luminance.</summary>
    public string? GainMap { get; private set; }
    public int? GainMapScale { get; private set; }
    public int? GainMapQuality { get; private set; }
    /// <summary>`--hdr-max-size`: the largest width and height of the jpg-hdr file, in pixels.</summary>
    public int? HdrMaxSize { get; private set; }

    // ---- LENS-FRAMES (convert) ------------------------------------------------------------------------------
    public int LensQuality { get; private set; } = 92;
    public float LensEv { get; private set; } = 0.95f;
    public int LensLevel { get; private set; }
    public int LensProfile { get; private set; } = 3;
    public string[]? LensModules { get; private set; }
    public string? LensStack { get; private set; }

    // ---- PARALLAX (convert) ---------------------------------------------------------------------------------
    public string? ParallaxFormat { get; private set; }
    public int? ParallaxSize { get; private set; }
    public int? ParallaxMs { get; private set; }
    public int? ParallaxFrames { get; private set; }
    public string? ParallaxLoop { get; private set; }
    public string? ParallaxFill { get; private set; }
    public string? ParallaxPath { get; private set; }
    public double? ParallaxBaseline { get; private set; }
    public string? ParallaxConverge { get; private set; }
    public (int X, int Y)? ParallaxConvergeAt { get; private set; }
    public double? ParallaxIpd { get; private set; }
    public string? ParallaxAnaglyph { get; private set; }
    public double? ParallaxFocus { get; private set; }
    public (int X, int Y)? ParallaxFocusAt { get; private set; }
    public double? ParallaxAperture { get; private set; }
    public int? ParallaxLayers { get; private set; }
    public (double M1, double M2)? ParallaxRack { get; private set; }
    public ((int X, int Y) A, (int X, int Y) B)? ParallaxRackAt { get; private set; }
    public double? ParallaxSubject { get; private set; }
    public (int X, int Y)? ParallaxSubjectAt { get; private set; }
    public double? ParallaxDz { get; private set; }
    public (double X, double Y)? ParallaxT { get; private set; }
    public int? ParallaxQuality { get; private set; }
    public int? ParallaxCrf { get; private set; }
    public string? ParallaxOrder { get; private set; }
    /// <summary>`--parallax-encoder`: lux (default: Lux's own GIF / APNG / WebP writers) or ffmpeg (the earlier recipes; always for avif).</summary>
    public string? ParallaxEncoder { get; private set; }

    // ---- ANIMATE --------------------------------------------------------------------------------------------
    public string? AnimEffect { get; private set; }
    public string? AnimContainer { get; private set; }
    public int? AnimSize { get; private set; }
    public int? AnimFrames { get; private set; }
    public int? AnimMs { get; private set; }
    public string? AnimLoop { get; private set; }
    public int? AnimRepeat { get; private set; }
    public (double M1, double M2)? AnimFocus { get; private set; }
    public float? AnimFFrom { get; private set; }
    public string? AnimPath { get; private set; }
    public double? AnimBaseline { get; private set; }
    public string? AnimConverge { get; private set; }
    public bool AnimSingleView { get; private set; }
    public string? AnimGifDither { get; private set; }
    public string? AnimGifPalette { get; private set; }
    public bool AnimNoDiff { get; private set; }
    public string? AnimKeepFrames { get; private set; }
    public (int X, int Y, int W, int H)? ParallaxPivot { get; private set; }

    // ---- ISP ------------------------------------------------------------------------------------------------
    public int IspLevel { get; private set; }
    public int Profile { get; private set; } = 3;           // isp/isp-run: CIAPI RendererProfile (Lumen passes 3)

    // ---- PULL -----------------------------------------------------------------------------------------------
    public string[]? Extensions { get; private set; }
    public string? Glob { get; private set; }
    public DateTimeOffset? Since { get; private set; }
    public bool Overwrite { get; private set; }
    public bool ListOnly { get; private set; }
    public bool Offline { get; private set; }     // version: print the build's version without asking GitHub

    /// <summary>The flags actually present on the command line, so a command can reject one that names something
    /// it does not write (`--jpeg-quality` with `--formats dng`) or one that belongs to a different command.</summary>
    public HashSet<string> Given { get; } = new(StringComparer.Ordinal);

    /// <summary>Fatal parse errors — a value that does not parse. Reported before dispatch.</summary>
    public List<string> Errors { get; } = new();

    public bool Has(params string[] flags) => flags.Any(Given.Contains);

    static (int X, int Y) Pt(string s)
    {
        var p = s.Split(',');
        if (p.Length != 2) throw new FormatException($"'{s}' is not an x,y point");
        return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
    }

    public static Options Parse(string[] args, out List<string> positionals)
    {
        var o = new Options();
        positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            string Need(string what) => Next() ?? throw new FormatException($"{a} needs {what}");
            if (a.StartsWith('-') && a.Length > 1) o.Given.Add(a);
            try
            {
                switch (a)
                {
                    // ---- output
                    case "-o" or "--out-directory": o.OutDirectory = Need("a directory"); break;
                    case "--out-file": o.OutFile = Need("a path"); break;
                    case "-j" or "--threads": o.Threads = Math.Max(1, int.Parse(Need("a count"), CultureInfo.InvariantCulture)); break;
                    case "--render-threads": o.RenderThreads = Math.Max(1, int.Parse(Need("a count"), CultureInfo.InvariantCulture)); break;
                    case "--formats" or "--format":
                    {   // a comma list may be written with spaces (`dng, jpg, ppm`): absorb the tokens the shell split off
                        var list = Need("a list, e.g. dng,jpg");
                        while (i + 1 < args.Length && (list.EndsWith(',') || args[i + 1].StartsWith(','))) list += args[++i];
                        o.Formats = list; break;
                    }

                    // ---- grid
                    case "--level":
                    {
                        string lv = Need("a level"); o.LevelText = lv;
                        if (int.TryParse(lv, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) o.Level = n; else o.LevelIsName = true;
                        break;
                    }
                    case "--no-cache": o.NoCache = true; break;
                    case "--memory-budget": o.MemoryBudgetGb = double.Parse(Need("a size in GB"), CultureInfo.InvariantCulture); break;
                    case "--size": { var p = Need("w,h").Split(','); o.Size = (int.Parse(p[0]), int.Parse(p[1])); break; }
                    case "--origin": { var p = Need("x,y").Split(','); o.Origin = (int.Parse(p[0]), int.Parse(p[1])); break; }

                    // ---- shared adjustments
                    case "--rotate":
                    {
                        string r = Need("0, 90, 180 or 270");
                        if (r is not ("0" or "90" or "180" or "270")) { o.Errors.Add("--rotate must be 0, 90, 180 or 270"); break; }
                        o.Rotate = int.Parse(r, CultureInfo.InvariantCulture); break;
                    }
                    case "--transform":
                    {
                        // nine matrix values ; four crop corners — decimal (round-trip) or 0x + the 32-bit pattern
                        var parts = Need("m0,…,m8;x0,y0,x1,y1").Split(';');
                        static float Fl(string t) { t = t.Trim(); return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? BitConverter.UInt32BitsToSingle(Convert.ToUInt32(t[2..], 16)) : float.Parse(t, CultureInfo.InvariantCulture); }
                        string[] m = parts.Length == 2 ? parts[0].Split(',') : Array.Empty<string>(), c = parts.Length == 2 ? parts[1].Split(',') : Array.Empty<string>();
                        if (m.Length != 9 || c.Length != 4) { o.Errors.Add("--transform takes nine matrix values, a semicolon, and the four crop corners x0,y0,x1,y1"); break; }
                        try { o.Transform = new Lux.Engine.Pipeline.Export.ExportTransform { Matrix = m.Select(Fl).ToArray(), CropX0 = Fl(c[0]), CropY0 = Fl(c[1]), CropX1 = Fl(c[2]), CropY1 = Fl(c[3]) }; }
                        catch (FormatException) { o.Errors.Add("--transform: every value must be a number"); }
                        break;
                    }
                    case "--fnum": o.FNumber = float.Parse(Need("an f-number"), CultureInfo.InvariantCulture); break;
                    case "--f-number": o.DepthFNumber = float.Parse(Need("an f-number"), CultureInfo.InvariantCulture); break;
                    case "--focus-depth": o.FocusDepth = float.Parse(Need("a distance in millimeters"), CultureInfo.InvariantCulture); break;
                    case "--focus-point":
                    {
                        var q = Need("x,y (0..1)").Split(',');
                        if (q.Length != 2) { o.Errors.Add("--focus-point takes x,y, each between 0 and 1"); break; }
                        o.FocusPoint = (float.Parse(q[0], CultureInfo.InvariantCulture), float.Parse(q[1], CultureInfo.InvariantCulture)); break;
                    }
                    case "--no-edits": o.NoEdits = true; break;
                    case "--upright": o.Upright = true; break;
                    case "--edits": o.Edits = Need("a variant name"); break;
                    case "--set-active": o.SetActive = Need("a variant name, or none"); break;
                    case "--iso": o.Iso = int.Parse(Need("an ISO"), CultureInfo.InvariantCulture); break;
                    case "--focal": o.Focal = int.Parse(Need("a focal length in mm"), CultureInfo.InvariantCulture); break;

                    // ---- tone & color
                    case "--exposure": o.Exposure = float.Parse(Need("stops, -3..3"), CultureInfo.InvariantCulture); break;
                    case "--contrast": o.Contrast = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--temperature": o.Temperature = float.Parse(Need("kelvin, 2000..20000"), CultureInfo.InvariantCulture); break;
                    case "--tint": o.Tint = float.Parse(Need("-150..150"), CultureInfo.InvariantCulture); break;
                    case "--vibrance": o.Vibrance = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--saturation": o.Saturation = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--sharpening": o.Sharpening = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--shadows": o.Shadows = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--highlights": o.Highlights = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--clarity": o.Clarity = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--highlight-recovery": o.HighlightRecovery = float.Parse(Need("0..100"), CultureInfo.InvariantCulture); break;
                    case "--gamut-compress": o.GamutCompress = true; break;

                    // ---- dng
                    case "--dng-cs": o.DngCs = int.Parse(Need("a color space setting"), CultureInfo.InvariantCulture); break;
                    case "--dng-tone": o.DngTone = Need("a tone-mapping profile"); break;
                    case "--dng-comp": o.DngComp = int.Parse(Need("0 or 1"), CultureInfo.InvariantCulture); break;
                    case "--dng-highlights": o.DngHighlights = Need("clip or keep").ToLowerInvariant(); break;

                    // ---- jpeg
                    case "--jpeg-cs": o.JpegCs = int.Parse(Need("a color space setting"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-quality": o.JpegQuality = int.Parse(Need("1-100"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-sub": o.JpegSub = int.Parse(Need("0, 1 or 2"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-v2": o.JpegV2 = true; break;
                    case "--jpeg-sharpening": o.JpegSharpening = float.Parse(Need("-100..100"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-modify": o.JpegModify = DateTime.Parse(Need("YYYY-MM-DDTHH:MM:SS"), CultureInfo.InvariantCulture, DateTimeStyles.None); break;
                    case "--jpeg-comment": o.JpegComment = Need("text"); break;
                    case "--jpeg-software": o.JpegSoftware = Need("text"); break;

                    // ---- noise
                    case "--grain-strength": o.GrainStrength = float.Parse(Need("0..4"), CultureInfo.InvariantCulture); break;
                    case "--denoise-threshold": o.DenoiseThreshold = float.Parse(Need("0.25..4"), CultureInfo.InvariantCulture); break;

                    // ---- hdr
                    case "--hdr-cs": o.HdrCs = int.Parse(Need("a color space setting"), CultureInfo.InvariantCulture); break;

                    // ---- tiff / jpg-hdr
                    case "--tiff-cs": o.TiffCs = Need("srgb, p3 or adobergb").ToLowerInvariant(); break;
                    case "--tiff-compression": o.TiffCompression = Need("deflate, lzw or none").ToLowerInvariant(); break;
                    case "--tiff-depth": o.TiffDepth = Need("f32 or f16").ToLowerInvariant(); break;
                    case "--gainmap": o.GainMap = Need("rgb or luminance").ToLowerInvariant(); break;
                    case "--gainmap-scale": o.GainMapScale = int.Parse(Need("4, 2 or 1"), CultureInfo.InvariantCulture); break;
                    case "--gainmap-quality": o.GainMapQuality = int.Parse(Need("1-100"), CultureInfo.InvariantCulture); break;
                    case "--hdr-max-size": o.HdrMaxSize = int.Parse(Need("a size in pixels"), CultureInfo.InvariantCulture); break;

                    // ---- lens-frames
                    case "--lens-quality": o.LensQuality = Math.Clamp(int.Parse(Need("1-100"), CultureInfo.InvariantCulture), 1, 100); break;
                    case "--lens-ev": o.LensEv = float.Parse(Need("stops"), CultureInfo.InvariantCulture); break;
                    case "--lens-level": o.LensLevel = int.Parse(Need("a config level"), CultureInfo.InvariantCulture); break;
                    case "--lens-profile": o.LensProfile = int.Parse(Need("0-3"), CultureInfo.InvariantCulture); break;
                    case "--lens-modules": o.LensModules = Need("a module list").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--lens-stack": o.LensStack = Need("'all' or an index"); break;

                    // ---- parallax
                    case "--parallax-format": o.ParallaxFormat = Need("gif, webp, avif or apng").ToLowerInvariant(); break;
                    case "--parallax-size": o.ParallaxSize = int.Parse(Need("a long edge in px"), CultureInfo.InvariantCulture); break;
                    case "--parallax-ms": o.ParallaxMs = int.Parse(Need("milliseconds"), CultureInfo.InvariantCulture); break;
                    case "--parallax-frames": o.ParallaxFrames = int.Parse(Need("a frame count"), CultureInfo.InvariantCulture); break;
                    case "--parallax-loop": o.ParallaxLoop = Need("pingpong or forward").ToLowerInvariant(); break;
                    case "--parallax-fill": o.ParallaxFill = Need("donors, inpaint or none").ToLowerInvariant(); break;
                    case "--parallax-path": o.ParallaxPath = Need("sweep, arc or line").ToLowerInvariant(); break;
                    case "--parallax-baseline": o.ParallaxBaseline = double.Parse(Need("millimeters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-converge": o.ParallaxConverge = Need("meters, auto or none").ToLowerInvariant(); break;
                    case "--parallax-converge-at": o.ParallaxConvergeAt = Pt(Need("x,y")); break;
                    case "--parallax-ipd": o.ParallaxIpd = double.Parse(Need("millimeters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-anaglyph": o.ParallaxAnaglyph = Need("dubois, color or gray").ToLowerInvariant(); break;
                    case "--parallax-focus": o.ParallaxFocus = double.Parse(Need("meters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-focus-at": o.ParallaxFocusAt = Pt(Need("x,y")); break;
                    case "--parallax-aperture": o.ParallaxAperture = double.Parse(Need("millimeters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-layers": o.ParallaxLayers = int.Parse(Need("a layer count"), CultureInfo.InvariantCulture); break;
                    case "--parallax-rack": { var p = Need("m1,m2").Split(','); o.ParallaxRack = (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)); break; }
                    case "--parallax-rack-at": { var p = Need("x1,y1;x2,y2").Split(';'); o.ParallaxRackAt = (Pt(p[0]), Pt(p[1])); break; }
                    case "--parallax-subject": o.ParallaxSubject = double.Parse(Need("meters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-subject-at": o.ParallaxSubjectAt = Pt(Need("x,y")); break;
                    case "--parallax-dz": o.ParallaxDz = double.Parse(Need("millimeters"), CultureInfo.InvariantCulture); break;
                    case "--parallax-t": { var p = Need("tx,ty").Split(','); o.ParallaxT = (double.Parse(p[0], CultureInfo.InvariantCulture), p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : 0); break; }
                    case "--parallax-quality": o.ParallaxQuality = int.Parse(Need("0-100"), CultureInfo.InvariantCulture); break;
                    case "--parallax-crf": o.ParallaxCrf = int.Parse(Need("0-63"), CultureInfo.InvariantCulture); break;
                    case "--parallax-order": o.ParallaxOrder = Need("sweep, label or a module list"); break;
                    case "--parallax-encoder": o.ParallaxEncoder = Need("lux or ffmpeg").ToLowerInvariant(); break;

                    // ---- animate
                    case "--anim-effect": o.AnimEffect = Need("wigglegram, parallax, rack-focus, aperture-sweep or lens-tour").ToLowerInvariant(); break;
                    case "--anim-container": o.AnimContainer = Need("gif, apng, webp, mp4 or webm").ToLowerInvariant(); break;
                    case "--anim-size": o.AnimSize = int.Parse(Need("a long edge in px"), CultureInfo.InvariantCulture); break;
                    case "--anim-frames": o.AnimFrames = int.Parse(Need("a frame count"), CultureInfo.InvariantCulture); break;
                    case "--anim-ms": o.AnimMs = int.Parse(Need("milliseconds"), CultureInfo.InvariantCulture); break;
                    case "--anim-loop": o.AnimLoop = Need("pingpong or forward").ToLowerInvariant(); break;
                    case "--anim-repeat": o.AnimRepeat = int.Parse(Need("a play count (0 = forever)"), CultureInfo.InvariantCulture); break;
                    case "--anim-focus": { var p = Need("near,far in meters").Split(','); o.AnimFocus = (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)); break; }
                    case "--anim-f-from": o.AnimFFrom = float.Parse(Need("an f-number"), CultureInfo.InvariantCulture); break;
                    case "--anim-path": o.AnimPath = Need("sweep, arc, line or orbit").ToLowerInvariant(); break;
                    case "--anim-baseline": o.AnimBaseline = double.Parse(Need("millimeters"), CultureInfo.InvariantCulture); break;
                    case "--anim-converge": o.AnimConverge = Need("auto or meters").ToLowerInvariant(); break;
                    case "--anim-single-view": o.AnimSingleView = true; break;
                    case "--anim-gif-dither": o.AnimGifDither = Need("ordered, diffusion or none").ToLowerInvariant(); break;
                    case "--anim-gif-palette": o.AnimGifPalette = Need("global or frame").ToLowerInvariant(); break;
                    case "--anim-no-diff": o.AnimNoDiff = true; break;
                    case "--anim-keep-frames": o.AnimKeepFrames = Need("a directory"); break;
                    case "--parallax-pivot": { var p = Need("x,y,w,h").Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray(); if (p.Length != 4) throw new FormatException("--parallax-pivot needs x,y,w,h"); o.ParallaxPivot = (p[0], p[1], p[2], p[3]); break; }

                    // ---- isp / isp-run
                    case "--isp-level": o.IspLevel = int.Parse(Need("a config level"), CultureInfo.InvariantCulture); break;
                    case "--profile": o.Profile = int.Parse(Need("0-3"), CultureInfo.InvariantCulture); break;

                    // ---- pull
                    case "--ext": o.Extensions = Need("a list").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(e => e.StartsWith('.') ? e : "." + e).ToArray(); break;
                    case "--glob": o.Glob = Need("a pattern"); break;
                    case "--since": o.Since = DateTimeOffset.Parse(Need("a date"), CultureInfo.InvariantCulture); break;
                    case "--overwrite": o.Overwrite = true; break;
                    case "--list": o.ListOnly = true; break;

                    // ---- version
                    case "--offline": o.Offline = true; break;

                    default:
                        if (a.StartsWith('-')) o.Errors.Add($"unknown option '{a}' — `lux-light --help` lists every flag");
                        else positionals.Add(a);
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException or ArgumentNullException)
            {
                o.Errors.Add($"{a}: {ex.Message}");
            }
        }
        return o;
    }
}
