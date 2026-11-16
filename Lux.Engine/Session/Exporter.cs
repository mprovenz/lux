using System.Buffers.Binary;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Session;

/// <summary>
/// **The** export: the ported Lumen path from a `.lri` to any combination of Lumen's `ExportImageFormat`s, plus
/// the Lux-only formats that share its render. `convert` is a thin adapter over this one body.
///
/// <para>All formats share one <see cref="ExportState"/>, so the PipelineCache (the level-0 ResAmp/fusion render,
/// ~84 % of the wall clock) is built once — exactly as one open document in Lumen serves several exports. Within
/// that there are only **two** distinct renders (<see cref="ExportSession"/>):</para>
/// <list type="bullet">
///   <item>fmt 0 / 4 (JPEG, JPEG+GDepth) re-run the whole output ISP into an 8-bit destination;</item>
///   <item>fmt 1 / 2 / 3 (PPM, DNG, HDR) take the `vec4x32f` float render — the DNG through the 2048² writer
///   blocks, PPM and HDR as one region, so those two share a single float image here.</item>
/// </list>
/// <para>The color-space property is per format, by Lumen's own rule
/// (<see cref="ExportRequest.ColorSpaceOf"/>). So a `convert` DNG carries `ColorSpace` 65535 and no Interop IFD
/// while its companion JPEG carries `ColorSpace` 1. (The output-tuning rewrite of the export is inert for fmt 1/2/3 — the
/// gate above means those never read the output tuning — but it is applied and restored anyway, as Lumen does.)</para>
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
        /// <summary>Things the caller should tell the user about this capture (saved depth edits were used, …).</summary>
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        public string? PathOf(ExportImageFormat f) => Outputs.FirstOrDefault(o => o.Format == f)?.Path;
    }

    /// <summary>The pixel grid an export lands on: the export window (from `setInputDataStream`), the level within it,
    /// the output size and the `GetExportTransformOutput` transform. Resolved
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
        var tr = rot == 0 ? ExportTransform.Identity(win.ExportDims[0]) : ExportTransform.Rotate(rot, win.ExportDims[0]);
        if (req.Transform is { } t)
        {
            if (rot != 0) throw new ExportRequestException("a stored photo transform and --rotate cannot be combined");
            var id0 = ExportTransform.Identity(win.ExportDims[0]);
            if ((t.AspectW, t.AspectH) != (id0.AspectW, id0.AspectH)) throw new ExportRequestException($"the photo transform has the aspect ratio {t.AspectW}:{t.AspectH}, this capture {id0.AspectW}:{id0.AspectH}");
            tr = t;
            // Lumen's full-size rule: QSize(level dims) then width × crop().w, height × crop().h, each (int)(float · float)
            float cw = t.CropX1 - t.CropX0, ch = t.CropY1 - t.CropY0;
            size = ((int)((float)baseDims.W * cw), (int)((float)baseDims.H * ch));
        }
        if (req.Size is { } so) size = so;
        return new ExportGrid(win, tr, size, lv);
    }

    /// <summary>The pipeline level a request needs built. `jpg+depth`, the depth map and the depth-based parallax
    /// formats read the renderer's depth ImageCache, which `setInputDataStream` fills from the
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
            LuxFormatsOf(req)(lri, req, null, req.OutDirectory, req.Stem, outputs, refusals, log);
            return new Result(0, 0, outputs, refusals);
        }
        // the depth effect reads the depth cache, which only the level-0 registration state fills — but only when the requested
        // f-number is below the capture's all-in-focus value; otherwise the image is the ordinary one and nothing extra is built
        req.Cancel.ThrowIfCancellationRequested();
        var pr = new ProgressReporter(req.Progress);
        pr.Begin("loading", 1);
        var lriFile = LriFile.Load(lriPath);
        pr.End();
        var notes = new List<string>();
        // the render settings saved with the capture's edits (an imported Lumen session): what the request does not set explicitly
        SavedRenderState? saved = req.UseSavedEdits ? req.Context?.LoadSavedState(req.EditsVariant) : null;
        if (saved is not null)
        {
            req = WithSavedSettings(req, saved);
            notes.Add(SavedStateNote(saved, req));
        }
        bool session = saved?.SessionGeometry is not null;
        if (req.DepthEffectFNumber is float dfn && !req.DepthEffectNeedsDepth && !(ExportWindow.AllInFocusFNumber(lriFile) <= dfn))
            req = req with { DepthEffectNeedsDepth = true };
        int buildLevel = BuildLevelFor(req);
        if (buildLevel == 0 && (req.Level ?? 0) != 0 && !v)
            log?.Invoke($"depth: building the level-0 registration state (the depth cache) even though the export level is {req.Level}");

        // ---- the analysis cache: whatever it holds replaces the work that would make it (the stored values are that work's own output)
        // a session render takes its geometry and depth from the session and differs from a fresh build in the fusion's source frames: the
        // analysis cache (computed geometry, fresh tiles) is neither read nor written for it
        var ctx = req.CacheUse != CacheUse.None && !session ? req.Context : null;
        IAnalysisCache? cache = null;
        try
        {
            cache = ctx?.OpenCache(AnalysisCache.EngineCacheVersion, create: false);
            bool geoKept = buildLevel == 0 && cache?.CompleteDescriptor(AnalysisCache.GeometryTier) is not null;
            CaptureGeometry? geo = buildLevel == 0 && cache is not null ? AnalysisCache.LoadGeometry(cache, log) : null;
            if (geoKept && geo is null) notes.Add("analysis cache: the kept geometry + depth was damaged; it was discarded and computed again");
            bool geoFromCache = geo is not null;
            if (session && buildLevel == 0) geo = saved!.SessionGeometry!(lriFile);
            // The noise controls change the image tiles. The kept tiles are Lumen's, so such an export does not read them. The geometry
            // and the depth do not depend on the module ISP: they are read and kept as usual.
            var noise = req.Noise is { IsLumen: false } nc ? nc.Validate() : null;
            var tiles = cache is null || noise is not null ? null : AnalysisCache.TileSource(cache, buildLevel, log);
            var st = ExportBuild.Build(lriFile, lriPath, buildLevel, eng, req.RenderThreads, pr,
                                       new BuildOptions { Geometry = geo, StoredTiles = tiles, KeepRegistration = false, Cancel = req.Cancel, SessionRender = session, Noise = noise });
            // saved depth edits: only a run that reads the depth looks for them, and only a capture with a context database has any
            string? editsNote = req.NeedsDepth && req.UseSavedEdits ? req.Context?.ApplySavedEdits(st, req.EditsVariant, log) : null;
            if (editsNote is not null) notes.Add(editsNote);
            var res = Run(st, req, log);

            // what the cache contributed, and what this run adds to it
            var used = new List<string>();
            if (geoFromCache) used.Add("geometry + depth");
            if (tiles is { Served: > 0 }) used.Add($"{tiles.Served} rendered tile{(tiles.Served == 1 ? "" : "s")}");
            if (used.Count > 0) notes.Add($"analysis cache: used {string.Join(" and ", used)} (the registration and rendering work they replace was skipped)");
            if (tiles is not null)
                foreach (var tier in tiles.Damaged)
                {
                    cache!.SetComplete(tier, null);   // no longer complete: `cache` raises it again
                    notes.Add($"analysis cache: a damaged {tier} block was found and recomputed for this export; the {tier} tier is marked incomplete");
                }
            if (req.CacheUse == CacheUse.ReadWriteGeometry && ctx is not null && geo is null && st.HasGeometry && st.Geometry is { } computed)
            {
                cache ??= ctx.OpenCache(AnalysisCache.EngineCacheVersion, create: true);
                if (cache is not null)
                {
                    AnalysisCache.SaveGeometry(cache, computed);
                    notes.Add("analysis cache: kept this capture's geometry + depth in its LuxImageContext, so a later export skips the registration (`--no-cache` turns this off)");
                }
            }
            return notes.Count == 0 ? res : res with { Notes = notes };
        }
        finally { cache?.Dispose(); }
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
            req.OutFile ?? Path.Combine(req.OutDirectory!, req.Stem + ExportFormats.Extension(f));

        if (wantGDepth && st.FullDepth is null)
            throw new ExportRequestException("jpg+depth: the registration chain produced no full-frame depth for this capture, so the renderer+0x480 cache cannot be filled");
        if ((req.Parallax?.Any(ParallaxRequest.NeedsDepthFormat) ?? false) && st.FullDepth is null)
            throw new ExportRequestException("parallax: the registration chain produced no full-frame depth for this capture");
        var lri = st.Lri; var frame = st.Frame;

        var s = new ExportSession(st, req, log);
        var eng = s.EngineLog;
        // a memory budget: the level-0 tiles first, in bands, then the inputs released, with a watchdog for the whole export (see MemoryBudget)
        using var budgetWatch = req.MemoryBudgetBytes is long budget0 ? MemoryBudget.Watch(budget0) : null;
        if (req.MemoryBudgetBytes is long budget
            && ExportTransformOutput.Compute(s.Grid.Transform, s.Grid.Size, new RectI(0, 0, s.Grid.Size.W, s.Grid.Size.H), s.Grid.Window.ExportDims, false).Level == 0
            && want.Length > 0)
            MemoryBudget.PrepareLevel0(s, budget, req.RenderThreads, req.Cancel, eng);
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
                      + (extras ? " + " + string.Join(" ", (req.LensFrames is null ? Array.Empty<string>() : new[] { "lens-frames" }).Concat((req.Parallax?.Formats ?? Array.Empty<ParallaxFormat>()).Select(ExportFormats.NameOf))) : ""));
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
                RowProgress = (row, rows) => { if (row == 1) s.Progress.Begin("jpeg encode", rows); s.Progress.Tick(); },
            };
            if (req.JpegEmbedIcc) enc.IccProfile = IccProfiles.ForColorSpaceProperty(t.ColorSpaceProperty);   // an option; off by default
            if (f == ExportImageFormat.JpegGDepth)
            {
                // The depth ImageCache: InverseDepthClip(ImageResample<0,float>(InverseDepth(fullDepth), cacheDims), 100000.0f)
                // at pipeline level 1. The depth comes from the capture's
                // own registration state — there is no other source.
                var (depth, dw, dh) = st.FullDepth!.Value;
                var cache = st.DepthCache(win);
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
                    r.Prefetch(new RectI(0, 0, size.W, size.H));   // threaded: every tile at once; the writer's blocks then hit the cache
                    var t = TagsFor(f);
                    if (v) log?.Invoke($"tags: illum {t.Illuminant1}/{t.Illuminant2} tone {t.ToneMappingType} ev {t.BaselineExposure:R} neutral ({string.Join(",", t.Neutral.Select(x => x.ToString("R")))}) fnum {t.FNumber:R} iso {t.Iso} focal {t.FocalLengthMm} exp {t.ExposureTimeSeconds:R} cs {t.ColorSpaceProperty} comp {t.Compression}");
                    using var fs = File.Create(path);
                    DngWriter.Write(fs, size.W, size.H, t, block => r.RenderBlock(block), eng, req.RenderThreads, s.Progress);
                    break;
                }
                case ExportImageFormat.Jpeg:
                case ExportImageFormat.JpegGDepth:
                    WriteJpeg(f, path);
                    break;
                case ExportImageFormat.Hdr:
                case ExportImageFormat.Ppm:
                {
                    // `exportImage`: the OUTPUT tuning of the chosen export level is
                    // rewritten for the duration of the export and restored afterwards — applied and restored as
                    // Lumen does even though the (fmt|4)==4 gate means neither format reads it, which is also why one
                    // float image serves both. This is the display/output ISP tuning, **not** the
                    // module-ISP tuning the PipelineCache tiles are generated with (`ExportState.TuningOfLevel`);
                    // mutating that one would change the render, which Lumen never does.
                    int csProp = req.ColorSpaceOf(f);
                    var outputTuning = DisplayIspTuning.Build(lv, lri.LumenEvOffset, lri.LumenNeutral, st.Capture.LensShadingMultiplier,
                                                              size.W, win.ExportDims[0].W, gateV2: false);
                    var saved = ExportTuningOverride.Apply(outputTuning, f, csProp);
                    if (v)
                    {
                        log?.Invoke($"tuning (renderer+0x650[{lv}], output ISP): property 0x13 = {csProp} → output.color_space = '{outputTuning.Str("output.color_space")}' (was '{saved.ColorSpace ?? "<unset>"}'), "
                                  + $"tone_mapping.type = '{outputTuning.Str("tone_mapping.type")}' (was '{saved.ToneMapping ?? "<unset>"}')");
                        log?.Invoke("        …neither is read on the HDR render path: Lumen runs the output ISP only for the JPG formats, "
                                  + "so the DNG and HDR formats take the linear float tiles. Lumen's .hdr files are the same for every color space.");
                    }
                    var img = s.FloatImage();
                    s.Progress.Begin(f == ExportImageFormat.Hdr ? "hdr write" : "ppm write", 1);
                    using (var fs = File.Create(path))
                    {
                        if (f == ExportImageFormat.Hdr) RadianceHdrWriter.Write(fs, size.W, size.H, img);
                        else PpmWriter.Write(fs, size.W, size.H, img);
                    }
                    s.Progress.End();
                    ExportTuningOverride.Restore(outputTuning, saved);
                    break;
                }
                default: throw new ArgumentOutOfRangeException(nameof(req), $"unsupported ExportImageFormat {f}");
            }
            long bytes = new FileInfo(path).Length;
            double secs = sw.Elapsed.TotalSeconds;
            outputs.Add(new Output(ExportFormats.NameOf(f), path, bytes, secs, f));
            if (!v) log?.Invoke($"{f} {size.W}x{size.H} -> {Path.GetFileName(path)} ({bytes} B) in {secs:F1}s");
        }

        // ---- the stereo depth map itself (a Lux extra, not a Lumen ExportImageFormat): the SAME depth the GDepth
        // block carries, written before the gray8 quantization. `_depth.f32` is metric millimeters at full float
        // precision with the project's {w,h,stride,bpp} header; `_depth.jpg` is the gray8 rendering, byte-for-byte
        // the image embedded in `GDepth:Data`, so it can just be looked at.
        if (req.DepthMap)
        {
            sw.Restart();
            s.Progress.Begin("depth", 1);
            var d = DepthMapExport.Write(s.DepthOnGrid(), size, req.OutDirectory!, req.Stem!);
            s.Progress.End();
            outputs.Add(new Output("depth", d.F32Path, new FileInfo(d.F32Path).Length, sw.Elapsed.TotalSeconds));
            outputs.Add(new Output("depth", d.PreviewPath, new FileInfo(d.PreviewPath).Length, 0, ExportImageFormat.JpegGDepth));
            log?.Invoke($"depth map {size.W}x{size.H} near {d.NearMm:F1}mm far {d.FarMm:F1}mm -> " +
                        $"{Path.GetFileName(d.F32Path)} + {Path.GetFileName(d.PreviewPath)} in {sw.Elapsed.TotalSeconds:F1}s");
        }

        // ---- the Lux-only formats, on the same session
        var refusals = new List<string>();
        if (extras)
            LuxFormatsOf(req)(lri, req, s, req.OutDirectory ?? Path.GetDirectoryName(req.OutFile!) ?? ".", req.Stem ?? Path.GetFileNameWithoutExtension(req.OutFile!),
                              outputs, refusals, log);

        return new Result(size.W, size.H, outputs, refusals);
    }

    /// <summary>The request with the saved render settings filled in wherever it does not set them itself (<see cref="ExportRequest.OverriddenSettings"/>):
    /// the f-number (`ParamFloat 0`, the depth effect and the Exif FNumber), the focus depth (`ParamFloat 1`; a stored value ≤ 0 means Lumen's
    /// default focus), the ten tone and color settings, the photo transform, and Lumen's newer tone style
    /// (the v2 tone gate: `JpegV2` + the `light_v2` DNG tone curve) when the variant was imported with it.</summary>
    public static ExportRequest WithSavedSettings(ExportRequest req, SavedRenderState s)
    {
        var o = req.OverriddenSettings; var r = req;
        if (s.ToneV2 && !o.Contains("tone-v2")) r = r with { JpegV2 = true, ToneMappingType = r.ToneMappingType ?? "light_v2" };
        if (s.Transform is { } t0 && !o.Contains("transform") && req.Rotate == 0 && req.Transform is null) r = r with { Transform = t0 };
        if (!s.HasRenderSettings)
        {
            if (r.FocusPoint is not null && r.Transform is not null)
                throw new ExportRequestException("--focus-point cannot be combined with the photo's saved crop or rotation; give --focus-depth instead");
            return r;
        }
        if (!o.Contains("f-number")) r = r with { DepthEffectFNumber = s.FNumber };
        if (!o.Contains("focus")) r = r with { FocusDepthMm = s.FocusDepth > 0f ? s.FocusDepth : null, FocusPoint = null };
        if (s.Adjustments is { } a)
        {   // (a Lumen session's settings always carry the tone values; a Lux recipe may hold only an f-number / focus)
            var g = req.Adjustments;
            bool O(int id) => o.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (g is not null)
                a = a with
                {
                    Exposure = O(2) ? g.Exposure : a.Exposure, Temperature = O(3) ? g.Temperature : a.Temperature, Tint = O(4) ? g.Tint : a.Tint,
                    Shadows = O(5) ? g.Shadows : a.Shadows, Highlights = O(6) ? g.Highlights : a.Highlights, Contrast = O(7) ? g.Contrast : a.Contrast,
                    Saturation = O(8) ? g.Saturation : a.Saturation, Vibrance = O(9) ? g.Vibrance : a.Vibrance, Clarity = O(10) ? g.Clarity : a.Clarity,
                    Sharpening = O(13) ? g.Sharpening : a.Sharpening, MonotoneSharpening = O(13) ? g.MonotoneSharpening : a.MonotoneSharpening,
                };
            else if (O(13)) a = a with { Sharpening = req.JpegSharpening, MonotoneSharpening = false };
            r = r with { Adjustments = a };
        }
        if (r.FocusPoint is not null && r.Transform is not null)
            throw new ExportRequestException("--focus-point cannot be combined with the photo's saved crop or rotation; give --focus-depth instead");
        return r;
    }

    public static string SavedStateNote(SavedRenderState s, ExportRequest r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Sv(string k) => r.OverriddenSettings.Contains(k) ? " (overridden)" : "";
        var parts = new List<string>();
        if (s.Settings is not null || s.FNumber is not null)
        {
            if (s.FNumber is not null) parts.Add(string.Format(inv, "f/{0:0.##}{1}", s.FNumber, Sv("f-number")));
            parts.Add(s.FocusDepth > 0f ? string.Format(inv, "focus {0:0} mm{1}", s.FocusDepth, Sv("focus")) : "default focus" + Sv("focus"));
        }
        if (s.Transform is { } t && !(t.CropX0 == 0f && t.CropY0 == 0f && t.CropX1 == 1f && t.CropY1 == 1f && t.Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f })))
            parts.Add("crop / rotation" + Sv("transform"));
        if (s.Rating is uint rt && rt > 0) parts.Add($"rating {rt}");
        if (s.Adjustments is { } adj) parts.Add($"tone {adj}");
        if (s.ToneV2) parts.Add("Lumen's newer tone style" + Sv("tone-v2"));
        return $"saved settings ({s.Source}, variant '{s.Variant}'): {string.Join(", ", parts)}" + (s.SessionGeometry is not null ? "; rendered from the session's own camera geometry and depth map" : "");
    }

    static LuxFormatsWriter LuxFormatsOf(ExportRequest req) =>
        req.LuxFormats ?? throw new ExportRequestException("the Lux-only formats (lens-frames, parallax-*) need a writer: ExportRequest.LuxFormats is not set");

    /// <summary>The exporter/Exif values from the `.lri` and the color profile (as `exportImage` sets them),
    /// with the <see cref="ExportRequest"/>'s overrides applied. <c>ColorSpaceProperty</c> is left at the
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
        // the exporter's tone type = the renderer level tuning's tone_mapping.type (Lumen profile 3 → 'light_v1'); the module-ISP tuning carries 'none'
        string tone = "light_v1";
        try { var tt = st.TuningOfLevel(0).Str("tone_mapping.type"); if (tt is "acr" or "light_v1" or "light_v1_lowlight" or "light_v2") tone = tt; } catch (Exception) { }
        t.ToneMappingType = req.ToneMappingType ?? tone;
        if (req.Compression is int comp) t.Compression = comp;
        t.ColorSpaceProperty = 4;   // CIAPI default (Lumen default: property 0x13 = 4 'srgb'); the caller overwrites it per format
        // ISO = int property 0 (100·image_gain truncated, samples 733 / 100); ExposureTime = property 0x11 (integration time, seconds)
        float gain = vp is not null && vp.HasImageGain ? vp.ImageGain : 1f;
        t.Iso = req.Iso ?? (int)(100f * gain);
        long ns = vp is not null && vp.HasImageIntegrationTimeNs ? (long)vp.ImageIntegrationTimeNs : (vp is not null && vp.HasDisplayIntegrationTimeNs ? (long)vp.DisplayIntegrationTimeNs : (long)st.Frame.Module.SensorExposure);
        t.ExposureTimeSeconds = (float)((float)ns * 1e-6f) / 1000f;
        t.ExposureCompensation = 0f;
        t.FNumber = req.FNumber ?? req.DepthEffectFNumber ?? ExportWindow.AllInFocusFNumber(lri);   // the f-number setting (ParamFloat 0) — the depth effect's when one is set
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
