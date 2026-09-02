using System.Buffers.Binary;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Cli;

/// <summary>
/// The export configuration as data — everything `convert` can ask for, in one record.
///
/// <para>The grid group (<see cref="Level"/>/<see cref="Size"/>/<see cref="Origin"/>) picks the pixel grid and
/// nothing else. The adjustment groups depart from the 1:1 Lumen output: every one of them is <c>null</c>/0/false
/// by default, and a null means "the value Lumen itself would use", which for the colour-space property is the
/// per-format rule at `0x14003c827` (<see cref="ExportTuningOverride.ColorSpaceProperty"/>).</para>
///
/// <para><see cref="LensFrames"/> and <see cref="Parallax"/> are the Lux-only formats (`lens-frames`, `parallax-*`):
/// additive outputs that never change the Lumen ones, sharing the same state and render (<see cref="ExportSession"/>).</para>
/// </summary>
public sealed record ExportRequest
{
    // ---- what to write -------------------------------------------------------------------------------------
    /// <summary>The `ExportImageFormat`s this run produces. One <see cref="ExportState"/>, hence one PipelineCache,
    /// serves all of them; within that there are only two distinct renders (see <see cref="Exporter"/>).</summary>
    public IReadOnlyList<ExportImageFormat> Formats { get; init; } = ConvertCmd.DefaultFormats;

    /// <summary>Also write the stereo depth map (`&lt;stem&gt;_depth.f32` + `_depth.jpg`) — a Lux extra, not a
    /// Lumen `ExportImageFormat`. Forces the level-0 build exactly as `jpg+depth` does.</summary>
    public bool DepthMap { get; init; }

    /// <summary>`lens-frames`: every module as a display JPEG through the ported module ISP. Needs no export state.</summary>
    public LensRequest? LensFrames { get; init; }

    /// <summary>The `parallax-*` formats and their flags. `parallax-wiggle` needs only the module renders; every
    /// other one reads the metric depth and so forces the level-0 build like `jpg+depth`.</summary>
    public ParallaxRequest? Parallax { get; init; }

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
    /// <summary>Exif FNumber override; null = <see cref="ExportWindow.AllInFocusFNumber"/> from the capture.</summary>
    public float? FNumber { get; init; }
    /// <summary>Exif ISO override; null = 100·image_gain truncated, from the capture.</summary>
    public int? Iso { get; init; }
    /// <summary>Exif FocalLength override, mm; null = the header's `ImageFocalLength`.</summary>
    public int? FocalLengthMm { get; init; }

    // ---- DNG -----------------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the DNG; null = the per-format rule (0 = none, what Lumen.exe writes).</summary>
    public int? DngColorSpace { get; init; }
    /// <summary>`tone_mapping.type` written into the DNG tags; null = the renderer level tuning's own value.</summary>
    public string? ToneMappingType { get; init; }
    /// <summary>DNG Compression tag override.</summary>
    public int? Compression { get; init; }

    // ---- JPEG ----------------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the JPEG (and JPEG+GDepth); null = the per-format rule (4 = srgb).</summary>
    public int? JpegColorSpace { get; init; }
    /// <summary>libjpeg quality; null = the literal 0x62 = 98 in `exportImage`.</summary>
    public int? JpegQuality { get; init; }
    /// <summary>libjpeg subsampling id; null = the literal 2 = 4:2:0.</summary>
    public int? JpegSubsampling { get; init; }
    /// <summary>Select the `renderer+0x64` v2 tone-mapping gate (and its branch-B histogram).</summary>
    public bool JpegV2 { get; init; }
    /// <summary>Exif/COM ModifyDate override; null = now.</summary>
    public DateTime? JpegModifyTime { get; init; }
    /// <summary>COM marker text; null = "Created with LibCP &lt;version&gt;".</summary>
    public string? JpegComment { get; init; }
    /// <summary>Exif Software override.</summary>
    public string? JpegSoftware { get; init; }

    // ---- HDR / PPM -----------------------------------------------------------------------------------------
    /// <summary>Renderer property 0x13 for the `.hdr`; null = the per-format rule (1 = linear_srgb).</summary>
    public int? HdrColorSpace { get; init; }
    // There is no PPM equivalent on purpose: the property is measurably inert for fmt 1 (cp.dll's
    // o466_l3.ppm and its cs1/cs3/cs4 variants are md5-identical), because the `(fmt|4)==4` gate means a PPM
    // never reads the output tuning. The rule below still supplies the value cp.dll would write.

    /// <summary>The louder, per-command voice the retired `export-dng`/`export-jpg`/`export-hdr` had (their `export:`,
    /// `tags:`, `tuning:`, `display tuning`, `render` and `jpeg:` lines, and the two-space indent on the engine
    /// trace) in place of `convert`'s summary lines. The harness's `export-pinned` still speaks with it.</summary>
    public bool Verbose { get; init; }

    /// <summary>The colour-space property (renderer property 0x13) this run writes for <paramref name="f"/>.
    /// The default is Lumen.exe's own per-format rule at `0x14003c827` — `fmt &lt;= 1 → 4 (srgb)`,
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
        (Formats?.Contains(ExportImageFormat.JpegGDepth) ?? false) || DepthMap
        || (Parallax?.Any(ParallaxFormats.NeedsDepth) ?? false);

    /// <summary>Whether the run needs an <see cref="ExportState"/> at all. `lens-frames` and `parallax-wiggle` alone
    /// do not: they are module renders, with no registration and no PipelineCache.</summary>
    public bool NeedsState => (Formats?.Count ?? 0) > 0 || NeedsDepth;
}

/// <summary>An <see cref="ExportRequest"/> this capture cannot satisfy — as opposed to a bug in the pipeline, which
/// still comes out as whatever the engine throws. A command turns it into `error: …` and exit 2.</summary>
public sealed class ExportRequestException : InvalidOperationException
{
    public ExportRequestException(string message) : base(message) { }
}

/// <summary>
/// One capture's renders, shared by every output of an <see cref="ExportRequest"/>: the grid, the two distinct
/// renders (`exportImage::lambda_2` at `0x180523d60` gates the display/output ISP on `(fmt | 4) == 4`, so fmt 0/4
/// take an 8-bit output-ISP re-run and fmt 1/2/3 a `vec4x32f` float render), the metric depth warped onto the same
/// grid, and the parallax source built from those two. Every member is memoised, so the JPEG, the depth map and the
/// parallax formats cost one render between them — the point of putting them on one command.
/// </summary>
public sealed class ExportSession
{
    public ExportState State { get; }
    public ExportRequest Request { get; }
    public Exporter.ExportGrid Grid { get; }
    readonly bool _v; readonly Action<string>? _log, _eng;

    public ExportSession(ExportState st, ExportRequest req, Action<string>? log)
    {
        State = st; Request = req; _v = req.Verbose; _log = log;
        // The engine trace (the renderers and the DNG writer). A verbose caller indents it by two spaces under its
        // own headline lines; `convert` hands its log through untouched.
        _eng = log is null ? null : (_v ? s => log("  " + s) : log);
        Grid = Exporter.Resolve(st, req);
    }

    public Action<string>? EngineLog => _eng;
    ExportLevels Win => Grid.Window;
    (int W, int H) Size => Grid.Size;
    LriFile Lri => State.Lri;
    CapturedFrame Frame => State.Frame;

    // ---- the float render, shared by DNG, PPM and HDR (fmt 1 and 3 are the same image, differing only in the writer).
    float[]? _floatImg;
    (int Cols, int Rows, float[] Data)? _vign;
    public ExportRenderer NewFloatRenderer()
    {
        _vign ??= Lux.Engine.Pipeline.Isp.Stages.LensShadingKernel.ModelGrid(Frame.Header, Frame.Module);
        // `exportImage` lambda_2 (`180523d60` L~80) reads `lens_shading.multiplier` from `renderer+0x650`'s FIRST
        // element — the level-0 tuning of the *renderer's* per-level tunings, whose multiplier `setInputDataStream`
        // L965/L1161 sets to `FUN_18048a930(reference capture)` for every level. That is the per-capture histogram
        // value (SoT §3.5), not the module-ISP tuning's 1.0.
        float Mult(int _) => State.Capture.LensShadingMultiplier;
        return new(State.Cache, Win, Grid.Transform, Size, forceLevel0: false, Mult, _vign.Value) { Log = _eng };
    }
    public float[] FloatImage()
    {
        if (_floatImg is not null) return _floatImg;
        return _floatImg = ExportFloatImage.Render(NewFloatRenderer(), Grid.Transform, Win.ExportDims, forceLevel0: false, _eng);
    }

    // ---- the 8-bit render, shared by JPEG, JPEG+GDepth and the parallax source (fmt 0 and 4 take the same output-ISP re-run).
    JpegExportRenderer? _jpegRenderer; byte[]? _rgba8;
    DisplayIspTuning.BranchBLpyr? _branchB; bool _branchBReady;
    readonly Dictionary<int, (SoftIsp, IspStats)> _ispCache = new();
    public (SoftIsp, IspStats) IspOfLevel(int level)
    {
        if (_ispCache.TryGetValue(level, out var hit)) return hit;
        var lri = Lri; var frame = Frame; var req = Request;
        if (req.JpegV2 && !_branchBReady)
        {
            var red = frame.Module.SensorBayerRedOverride;
            float hb = float.IsNaN(frame.Info.FrameBlack) ? (frame.Info.Noise?.Black ?? 42f) : frame.Info.FrameBlack;
            float hw = frame.Info.Noise?.White ?? 1023f;
            var hist = DisplayValueHistogram.Build(frame.Raw, frame.Width, frame.Height, frame.Stride,
                                                   new RectI(0, 0, frame.Width, frame.Height), hb, hw, lri.LumenNeutral, red?.X ?? -1, red?.Y ?? -1);
            _branchB = DisplayValueHistogram.BranchB(hist, hw, lri.LumenEvOffset);
            _branchBReady = true;
        }
        var tuning = DisplayIspTuning.Build(level, lri.LumenEvOffset, lri.LumenNeutral, State.Capture.LensShadingMultiplier,
                                            Win.ExportDims[level].W, Win.ExportDims[0].W, req.JpegV2, branchB: _branchB);
        var isp = new SoftIsp(tuning, State.Colour);
        var stats = isp.ComputeStats(frame);
        if (_v) _log?.Invoke($"  display tuning L{level}: tone_mapping.type {tuning.Type("tone_mapping")} sharpening_scale {tuning.Num("tone_mapping.sharpening_scale"):R} tone_adjust.filter_size {tuning.Num("tone_adjust.filter_size"):R} ev_offset {lri.LumenEvOffset:R} multiplier {State.Capture.LensShadingMultiplier:R}");
        return _ispCache[level] = (isp, stats);
    }
    public (JpegExportRenderer Renderer, byte[] Rgba) Rgba8()
    {
        if (_jpegRenderer is not null && _rgba8 is not null) return (_jpegRenderer, _rgba8);
        _jpegRenderer = new JpegExportRenderer(State.Cache, Win, Grid.Transform, Size, forceLevel0: false, IspOfLevel, Frame) { Log = _eng };
        var swr = System.Diagnostics.Stopwatch.StartNew();
        _rgba8 = _jpegRenderer.Render();
        if (_v) _log?.Invoke($"  render {Size.W}x{Size.H} RGBA8 in {swr.Elapsed.TotalSeconds:F1}s");
        return (_jpegRenderer, _rgba8);
    }

    // ---- the metric depth on the export grid, shared by the depth pair and the parallax source.
    float[]? _depth;
    /// <summary>`ExportState.FullDepth` (the same image `setInputDataStream` puts in the `renderer+0x480` cache)
    /// resampled onto this grid through exactly the `GetExportTransformOutput` transform the writers resolve.</summary>
    public float[] DepthOnGrid()
    {
        if (_depth is not null) return _depth;
        var (dsrc, dw0, dh0) = State.FullDepth ?? throw new ExportRequestException("the registration chain produced no full-frame depth for this capture");
        return _depth = DepthCmd.Warp(dsrc, dw0, dh0, Win, Grid.Transform, Size);
    }

    // ---- the registration state the parallax geometry reads (the ctor pairs). The level-0 build's own object when
    //      it ran one; otherwise Setup() alone, which is sub-second and produces the same reference-group pairs.
    StereoAsyncApi? _api;
    public StereoAsyncApi Registration() => _api ??= State.Registration ?? ModuleGrid.SetupApi(Lri);

    // ---- the parallax source: the JPEG render + the depth, on this grid, with the rig geometry.
    ParallaxSource? _src;
    public ParallaxSource ParallaxSource()
    {
        if (_src is not null) return _src;
        var (_, rgba) = Rgba8();
        var depth = DepthOnGrid();
        var g = ParallaxGeometry.FromCapture(Lri, Registration(), (Frame.Width, Frame.Height), State.Dims[0], Win, Size);
        _log?.Invoke("geom: " + g.Describe());
        return _src = new ParallaxSource
        {
            Colour = new Rgba(Size.W, Size.H, rgba), Depth = new Plane(Size.W, Size.H, depth), Geometry = g,
            Stem = Request.Stem ?? Path.GetFileNameWithoutExtension(Request.OutFile ?? "capture"),
        };
    }
}

/// <summary>
/// **The** export: the ported cp.dll path from a `.lri` to any combination of Lumen's `ExportImageFormat`s, plus
/// the Lux-only formats that share its render. `convert` is a thin adapter over this one body.
///
/// <para>All formats share one <see cref="ExportState"/>, so the PipelineCache (the level-0 ResAmp/fusion render,
/// ~84 % of the wall clock) is built once — exactly as one open document in Lumen serves several exports. Within
/// that there are only **two** distinct renders (<see cref="ExportSession"/>):</para>
/// <list type="bullet">
///   <item>fmt 0 / 4 (JPEG, JPEG+GDepth) re-run the whole output ISP into an 8-bit destination via `renderer+0x688`;</item>
///   <item>fmt 1 / 2 / 3 (PPM, DNG, HDR) take the `vec4x32f` float render — the DNG through the 2048² writer
///   blocks, PPM and HDR as one region, so those two share a single float image here.</item>
/// </list>
/// <para>The colour-space property is per format, by Lumen.exe's own rule at `0x14003c827`
/// (<see cref="ExportRequest.ColorSpaceOf"/>). So a `convert` DNG carries `ColorSpace` 65535 and no Interop IFD
/// while its companion JPEG carries `ColorSpace` 1. (The §12.7 output-tuning rewrite is inert for fmt 1/2/3 — the
/// gate above means those never read the output tuning — but it is applied and restored anyway, as cp.dll does.)</para>
/// </summary>
public static class Exporter
{
    /// <summary>One written file. <see cref="Name"/> is the `--formats` spelling (`dng`, `depth`, `lens-frames`,
    /// `parallax-dof`, …); <see cref="Format"/> is set for the Lumen formats only.</summary>
    public sealed record Output(string Name, string Path, long Bytes, double Seconds, ExportImageFormat? Format = null);

    /// <summary><paramref name="Refusals"/>: requested outputs this capture legitimately cannot produce (a wigglegram
    /// of a telephoto capture), with the explanation — not pipeline failures.</summary>
    public sealed record Result(int Width, int Height, IReadOnlyList<Output> Outputs, IReadOnlyList<string> Refusals)
    {
        public string? PathOf(ExportImageFormat f) => Outputs.FirstOrDefault(o => o.Format == f)?.Path;
    }

    /// <summary>The pixel grid an export lands on: the export window (`setInputDataStream` L~340-430 →
    /// `FUN_1804b2520`), the level within it, the output size and the `GetExportTransformOutput` transform. Resolved
    /// from the state and the request alone, so a caller can know the output dimensions — or line another image up
    /// with them — without rendering anything.</summary>
    public sealed record ExportGrid(ExportLevels Window, ExportTransform Transform, (int W, int H) Size, int Level);

    /// <summary>The grid <paramref name="req"/> selects on <paramref name="st"/>. See <see cref="ExportGrid"/>.</summary>
    public static ExportGrid Resolve(ExportState st, ExportRequest req)
    {
        var frame = st.Frame;
        var sensor = (frame.Width, frame.Height); var canvas = st.Dims[0];
        var crop = ExportWindow.CropRect(st.Lri, sensor);
        var win = ExportWindow.Compute(canvas, sensor, crop, 5);
        if (req.Origin is { } org) win = ExportLevels.L16(win.ExportDims[0], canvas, (org.X, org.Y));
        int lv = req.Level is int l && l >= 0 && l < win.ExportDims.Length ? l : 0;
        int rot = req.Rotate;
        var baseDims = win.ExportDims[lv];
        (int W, int H) size = rot is 90 or 270 ? (baseDims.H, baseDims.W) : baseDims;
        if (req.Size is { } so) size = so;
        var tr = rot == 0 ? ExportTransform.Identity(win.ExportDims[0]) : ExportTransform.Rotate(rot, win.ExportDims[0]);
        return new ExportGrid(win, tr, size, lv);
    }

    /// <summary>The pipeline level a request needs built. `jpg+depth`, the depth map and the depth-based parallax
    /// formats read the renderer's depth ImageCache (`renderer+0x480`), which `setInputDataStream` fills from the
    /// upsampled dense depth — so they need the whole registration chain even when the export level is 3 or 4,
    /// where an ordinary export needs none of it.</summary>
    public static int BuildLevelFor(ExportRequest req) => req.NeedsDepth ? 0 : req.Level ?? 0;

    /// <summary>One capture: build the state (when the request needs one), then write every requested format from it.</summary>
    public static Result Run(string lriPath, ExportRequest req, Action<string>? log)
    {
        bool v = req.Verbose;
        Action<string>? eng = log is null ? null : (v ? s => log("  " + s) : log);
        if (!req.NeedsState)
        {
            // lens-frames and/or parallax-wiggle alone: module renders only, no registration, no PipelineCache
            var lri = LriFile.Load(lriPath);
            if (req.OutDirectory is null || req.Stem is null) throw new ExportRequestException("the module formats need OutDirectory + Stem");
            Directory.CreateDirectory(req.OutDirectory);
            var outputs = new List<Output>(); var refusals = new List<string>();
            new VisualFormats(lri, req, null, req.OutDirectory, req.Stem, outputs, refusals, log).Run();
            return new Result(0, 0, outputs, refusals);
        }
        int buildLevel = BuildLevelFor(req);
        if (buildLevel == 0 && (req.Level ?? 0) != 0 && !v)
            log?.Invoke($"depth: building the level-0 registration state (the depth cache) even though the export level is {req.Level}");
        return Run(ExportBuild.Build(lriPath, buildLevel, eng), req, log);
    }

    /// <summary>The same export against a state the caller has already built — one <see cref="ExportState"/> can
    /// serve several requests, and a caller that builds its own (a GUI holding an open document, or a tool that
    /// substitutes part of the pipeline) writes through exactly this path.</summary>
    public static Result Run(ExportState st, ExportRequest req, Action<string>? log)
    {
        var want = (req.Formats ?? Array.Empty<ExportImageFormat>()).Distinct().ToArray();
        bool wantGDepth = want.Contains(ExportImageFormat.JpegGDepth) || req.DepthMap;
        bool extras = req.LensFrames is not null || (req.Parallax?.Formats.Count ?? 0) > 0;
        // An empty format list is legal only when something else is written (`--formats depth`, or the Lux formats).
        if (want.Length == 0 && !req.DepthMap && !extras) throw new ExportRequestException("no output format requested");
        bool v = req.Verbose;

        if (req.OutFile is null)
        {
            if (req.OutDirectory is null || req.Stem is null)
                throw new ExportRequestException("an export needs either OutFile or OutDirectory + Stem");
            Directory.CreateDirectory(req.OutDirectory);
        }
        else if (want.Length + (req.Parallax?.Formats.Count ?? 0) > 1 || req.DepthMap || req.LensFrames is not null)
            throw new ExportRequestException($"OutFile names one file, but this run writes {want.Length}{(req.DepthMap ? " + the depth map" : "")}");

        string PathFor(ExportImageFormat f) =>
            req.OutFile ?? Path.Combine(req.OutDirectory!, req.Stem + ConvertCmd.Extension(f));

        if (wantGDepth && st.FullDepth is null)
            throw new ExportRequestException("jpg+depth: the registration chain produced no full-frame depth for this capture, so the renderer+0x480 cache cannot be filled");
        if ((req.Parallax?.Any(ParallaxFormats.NeedsDepth) ?? false) && st.FullDepth is null)
            throw new ExportRequestException("parallax: the registration chain produced no full-frame depth for this capture");
        var lri = st.Lri; var frame = st.Frame;

        var s = new ExportSession(st, req, log);
        var eng = s.EngineLog;
        var win = s.Grid.Window; var size = s.Grid.Size; int lv = s.Grid.Level;
        var sensor = (frame.Width, frame.Height); var canvas = st.Dims[0];
        var crop = ExportWindow.CropRect(lri, sensor);

        string window = string.Join(" ", win.ExportDims.Select((d, i) => $"L{i}={d.W}x{d.H}@({win.Origins[i].X},{win.Origins[i].Y})"));
        if (v)
            log?.Invoke($"export: sensor {sensor.Item1}x{sensor.Item2} canvas {canvas.W}x{canvas.H} crop ({crop.X0:R},{crop.Y0:R},{crop.X1:R},{crop.Y1:R}) window {window} size {size.W}x{size.H}");
        else
        {
            log?.Invoke($"export window {window} size {size.W}x{size.H}");
            log?.Invoke($"formats: {string.Join(" ", want.Select(f => $"{f}({(int)f})").Concat(req.DepthMap ? new[] { "depth" } : Array.Empty<string>()))}"
                      + (extras ? " + " + string.Join(" ", (req.LensFrames is null ? Array.Empty<string>() : new[] { "lens-frames" }).Concat((req.Parallax?.Formats ?? Array.Empty<ParallaxFormat>()).Select(ParallaxFormats.Name))) : ""));
        }

        var outputs = new List<Output>();
        var sw = new System.Diagnostics.Stopwatch();

        DngExportTags TagsFor(ExportImageFormat f)
        {
            var t = BuildTags(st, req);
            t.ExifImageWidth = size.W; t.ExifImageHeight = size.H;
            t.ColorSpaceProperty = req.ColorSpaceOf(f);
            if (f is ExportImageFormat.Jpeg or ExportImageFormat.JpegGDepth)
            {
                if (req.JpegSoftware is string sfw) t.Software = sfw;
                if (req.JpegModifyTime is DateTime mt) t.ModifyTime = mt;
            }
            return t;
        }

        void WriteJpeg(ExportImageFormat f, string path)
        {
            var (renderer, px) = s.Rgba8();
            var t = TagsFor(f);
            string version = t.Software.StartsWith("Build ", StringComparison.Ordinal) ? t.Software[6..] : t.Software;
            var enc = new JpegEncoder
            {
                Quality = req.JpegQuality ?? 98,          // the literal 0x62 in exportImage
                SubsamplingId = req.JpegSubsampling ?? 2, // the literal 2 = 4:2:0
                Comment = req.JpegComment ?? "Created with LibCP " + version,
                ExifApp1 = JpegExif.Build(t, size.W, size.H),
            };
            if (f == ExportImageFormat.JpegGDepth)
            {
                // `renderer+0x480`: InverseDepthClip(ImageResample<0,float>(InverseDepth(fullDepth), cacheDims), 100000.0f)
                // at pipeline level 1, fetched through the `0x180526ec0` wrapper. The depth comes from the capture's
                // own registration state — there is no other source. Verified byte-identical to Lumen's fmt-4 artefacts.
                var (depth, dw, dh) = st.FullDepth!.Value;
                var cache = DepthImageCache.FromFullDepth(depth, dw, dh, win);
                enc.ExtraApp1 = GDepth.Build(cache, win, renderer.Transform, size);
                if (v) log?.Invoke($"  gdepth: depth cache from the capture's own registration state, {enc.ExtraApp1.Count} APP1 block(s), {enc.ExtraApp1.Sum(x => x.Length)} B");
                else
                {
                    var cd = DepthImageCache.CacheDims(win);
                    log?.Invoke($"gdepth: depth cache {cd.W}x{cd.H} from a {dw}x{dh} full-frame depth, {enc.ExtraApp1.Count} APP1 block(s), {enc.ExtraApp1.Sum(x => x.Length)} B");
                }
            }
            if (v) log?.Invoke($"  jpeg: quality {enc.Quality} subsampling {enc.SubsamplingId} exif {enc.ExifApp1!.Length} B com \"{enc.Comment}\" modify {t.ModifyTime:yyyy:MM:dd HH:mm:ss}");
            using var fs = File.Create(path);
            JpegEncoder.Encode(fs, px, size.W, size.H, size.W * 4, grayscale: false, enc);
        }

        foreach (var f in want)
        {
            sw.Restart();
            string path = PathFor(f);
            switch (f)
            {
                case ExportImageFormat.Dng:
                {
                    var r = s.NewFloatRenderer();
                    var t = TagsFor(f);
                    if (v) log?.Invoke($"tags: illum {t.Illuminant1}/{t.Illuminant2} tone {t.ToneMappingType} ev {t.BaselineExposure:R} neutral ({string.Join(",", t.Neutral.Select(x => x.ToString("R")))}) fnum {t.FNumber:R} iso {t.Iso} focal {t.FocalLengthMm} exp {t.ExposureTimeSeconds:R} cs {t.ColorSpaceProperty} comp {t.Compression}");
                    using var fs = File.Create(path);
                    DngWriter.Write(fs, size.W, size.H, t, block => r.RenderBlock(block), eng);
                    break;
                }
                case ExportImageFormat.Jpeg:
                case ExportImageFormat.JpegGDepth:
                    WriteJpeg(f, path);
                    break;
                case ExportImageFormat.Hdr:
                case ExportImageFormat.Ppm:
                {
                    // `exportImage` §12.7: the OUTPUT tuning of the chosen export level (`renderer+0x650[level]`) is
                    // rewritten for the duration of the export and restored afterwards — applied and restored as
                    // cp.dll does even though the (fmt|4)==4 gate means neither format reads it, which is also why one
                    // float image serves both. `renderer+0x650` is the display/output ISP tuning, **not** the
                    // module-ISP tuning the PipelineCache tiles are generated with (`ExportState.TuningOfLevel`);
                    // mutating that one would change the render, which cp.dll never does.
                    int csProp = req.ColorSpaceOf(f);
                    var outputTuning = DisplayIspTuning.Build(lv, lri.LumenEvOffset, lri.LumenNeutral, st.Capture.LensShadingMultiplier,
                                                              size.W, win.ExportDims[0].W, gateV2: false);
                    var saved = ExportTuningOverride.Apply(outputTuning, f, csProp);
                    if (v)
                    {
                        log?.Invoke($"tuning (renderer+0x650[{lv}], output ISP): property 0x13 = {csProp} → output.color_space = '{outputTuning.Str("output.color_space")}' (was '{saved.ColorSpace ?? "<unset>"}'), "
                                  + $"tone_mapping.type = '{outputTuning.Str("tone_mapping.type")}' (was '{saved.ToneMapping ?? "<unset>"}')");
                        log?.Invoke("        …neither is read on the fmt-3 render path: exportImage::lambda_2 0x180523d60 gates the output ISP on (fmt|4)==4, "
                                  + "so fmt 1/2/3 render through FUN_1805253c0 + the DNG float tile lambda. Verified: cp.dll's own CS=4 and CS=1 .hdr files are byte-identical.");
                    }
                    var img = s.FloatImage();
                    using (var fs = File.Create(path))
                    {
                        if (f == ExportImageFormat.Hdr) RadianceHdrWriter.Write(fs, size.W, size.H, img);
                        else PpmWriter.Write(fs, size.W, size.H, img);
                    }
                    ExportTuningOverride.Restore(outputTuning, saved);
                    break;
                }
                default: throw new ArgumentOutOfRangeException(nameof(req), $"unsupported ExportImageFormat {f}");
            }
            long bytes = new FileInfo(path).Length;
            double secs = sw.Elapsed.TotalSeconds;
            outputs.Add(new Output(ConvertCmd.NameOf(f), path, bytes, secs, f));
            if (!v) log?.Invoke($"{f} {size.W}x{size.H} -> {Path.GetFileName(path)} ({bytes} B) in {secs:F1}s");
        }

        // ---- the stereo depth map itself (a Lux extra, not a Lumen ExportImageFormat): the SAME depth the GDepth
        // block carries, written before the gray8 quantisation. `_depth.f32` is metric millimetres at full float
        // precision with the project's {w,h,stride,bpp} header; `_depth.jpg` is the gray8 rendering, byte-for-byte
        // the image embedded in `GDepth:Data`, so it can just be looked at.
        if (req.DepthMap)
        {
            sw.Restart();
            var d = DepthCmd.Write(s.DepthOnGrid(), size, req.OutDirectory!, req.Stem!);
            outputs.Add(new Output("depth", d.F32Path, new FileInfo(d.F32Path).Length, sw.Elapsed.TotalSeconds));
            outputs.Add(new Output("depth", d.PreviewPath, new FileInfo(d.PreviewPath).Length, 0, ExportImageFormat.JpegGDepth));
            log?.Invoke($"depth map {size.W}x{size.H} near {d.NearMm:F1}mm far {d.FarMm:F1}mm -> " +
                        $"{Path.GetFileName(d.F32Path)} + {Path.GetFileName(d.PreviewPath)} in {sw.Elapsed.TotalSeconds:F1}s");
        }

        // ---- the Lux-only formats, on the same session
        var refusals = new List<string>();
        if (extras)
            new VisualFormats(lri, req, s, req.OutDirectory ?? Path.GetDirectoryName(req.OutFile!) ?? ".", req.Stem ?? Path.GetFileNameWithoutExtension(req.OutFile!),
                              outputs, refusals, log).Run();

        return new Result(size.W, size.H, outputs, refusals);
    }

    /// <summary>The exporter/Exif values from the `.lri` and the colour profile (SoT §8.1/§8.3, `exportImage`
    /// L216–235), with the <see cref="ExportRequest"/>'s overrides applied. <c>ColorSpaceProperty</c> is left at the
    /// CIAPI default here and set per format by the caller from <see cref="ExportRequest.ColorSpaceOf"/>.</summary>
    public static DngExportTags BuildTags(ExportState st, ExportRequest req)
    {
        var lri = st.Lri; var h = lri.Header; var p = st.Colour; var vp = lri.ViewPreferencesBlock;
        var t = new DngExportTags
        {
            Illuminant1 = p.Low.InternalIlluminant, Illuminant2 = p.High.InternalIlluminant,
            ColorMatrix1 = p.Low.ColorMatrix, ColorMatrix2 = p.High.ColorMatrix,
            ForwardMatrix1 = p.Low.Fit.ForwardMatrix, ForwardMatrix2 = p.High.Fit.ForwardMatrix,
            HueSatMap1 = p.Low.Fit.ToDngHueSatMap(), HueSatMap2 = p.High.Fit.ToDngHueSatMap(),
            BaselineExposure = lri.LumenEvOffset, Neutral = lri.LumenNeutral,
            FocalLengthMm = h.HasImageFocalLength ? h.ImageFocalLength : 0,
        };
        // exporter+0x200 = the renderer level tuning's tone_mapping.type (SoT §3.6; cp.dll profile 3 → 'light_v1'); the module-ISP tuning carries 'none'
        string tone = "light_v1";
        try { var tt = st.TuningOfLevel(0).Str("tone_mapping.type"); if (tt is "acr" or "light_v1" or "light_v1_lowlight" or "light_v2") tone = tt; } catch (Exception) { }
        t.ToneMappingType = req.ToneMappingType ?? tone;
        if (req.Compression is int comp) t.Compression = comp;
        t.ColorSpaceProperty = 4;   // CIAPI default (cp.dll default: property 0x13 = 4 'srgb'); the caller overwrites it per format
        // ISO = int property 0 (100·image_gain truncated, samples 733 / 100); ExposureTime = property 0x11 (integration time, seconds)
        float gain = vp is not null && vp.HasImageGain ? vp.ImageGain : 1f;
        t.Iso = req.Iso ?? (int)(100f * gain);
        long ns = vp is not null && vp.HasImageIntegrationTimeNs ? (long)vp.ImageIntegrationTimeNs : (vp is not null && vp.HasDisplayIntegrationTimeNs ? (long)vp.DisplayIntegrationTimeNs : (long)st.Frame.Module.SensorExposure);
        t.ExposureTimeSeconds = (float)((float)ns * 1e-6f) / 1000f;
        t.ExposureCompensation = 0f;
        t.FNumber = req.FNumber ?? ExportWindow.AllInFocusFNumber(lri);
        if (req.FocalLengthMm is int focal) t.FocalLengthMm = focal;
        var id = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(id, h.ImageUniqueIdLow); BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), h.ImageUniqueIdHigh);
        t.UniqueId = id;
        if (h.ImageTimeStamp is { } ts) t.TimeStamp = ((int)ts.Year, (int)ts.Month, (int)ts.Day, (int)ts.Hour, (int)ts.Minute, (int)ts.Second, ts.HasTzOffset ? ts.TzOffset : 0);
        var now = DateTime.Now; t.ModifyTime = now;
        int lh = now.Hour, gh = now.ToUniversalTime().Hour; int d = lh - gh; int m = d % 24; if (m > 12) m -= 24; if (m < -12) m += 24; t.ModifyTzOffsetHours = m;
        return t;
    }
}
