using System.Diagnostics;
using Lux.Cli;
using Lux.Engine.Lri;
using Lux.Engine.Mtp;

// lux-light — Lux CLI: convert Light L16 .lri captures. Lightweight companion to the (future) GUI app.
return Cli.Run(args);

namespace Lux.Cli
{
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return Help();

            string command = args[0];
            var rest = args.Skip(1).ToArray();
            var opts = Options.Parse(rest, out var positionals);
            if (opts.Errors.Count > 0)
            {   // a malformed flag fails loudly — never reinterpreted as something else
                foreach (var e in opts.Errors) Console.Error.WriteLine($"error: {e}");
                return 2;
            }

            if (!CommandSets.IsProduction(command)) return UnknownCommand(command);

            // device commands take no input files
            if (command == "devices") return RunDevices();
            if (command == "pull") return RunPull(opts);
            Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();

            if (positionals.Count == 0)
            {
                Console.Error.WriteLine("error: no input .lri file or directory given.");
                return Help(2);
            }

            if (command == "mod-info") return ModInfo.Run(positionals);

            List<string> files = CliInput.CollectLri(positionals);
            if (files.Count == 0) { Console.Error.WriteLine("error: no .lri files found."); return 1; }

            return command switch
            {
                "convert" => RunConvert(files, opts),
                "inspect" => RunInspect(files),
                "profile" => RunProfile(files),
                "isp" => RunIsp(files, opts),
                "isp-run" => RunIspRun(files, opts),
                _ => UnknownCommand(command),
            };
        }

        private static int RunDevices()
        {
            var backend = MtpFactory.Backend;
            Console.WriteLine($"MTP backend: {backend.Name} (available: {backend.IsAvailable})");
            if (!backend.IsAvailable) { Console.Error.WriteLine("no usable MTP backend on this platform."); return 1; }
            var devices = backend.Detect();
            if (devices.Count == 0) { Console.WriteLine("no MTP devices detected (is the camera connected and in file-transfer mode?)"); return 1; }
            foreach (var d in devices)
            {
                using (d)
                {
                    Console.WriteLine($"  {d.Name}  @ {d.Port}");
                    foreach (var s in d.Storages)
                        Console.WriteLine($"    {s.Path}  ({s.Description})");
                }
            }
            return 0;
        }

        private static int RunPull(Options o)
        {
            var backend = MtpFactory.Backend;
            if (!backend.IsAvailable) { Console.Error.WriteLine($"MTP backend '{backend.Name}' not available on this platform."); return 1; }
            var devices = backend.Detect();
            if (devices.Count == 0) { Console.Error.WriteLine("no MTP device detected (connect the camera in file-transfer mode)."); return 1; }

            using var dev = devices[0];
            for (int i = 1; i < devices.Count; i++) devices[i].Dispose();

            var filter = new PullFilter
            {
                Extensions = o.Extensions ?? new[] { ".lri" },
                Glob = o.Glob,
                ModifiedSince = o.Since,
            };
            string outDir = o.OutDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "lux_pull");

            Console.WriteLine($"device: {dev.Name} @ {dev.Port}");
            var matches = MtpPuller.List(dev, filter);
            Console.WriteLine($"matched {matches.Count} file(s)  (filter: {string.Join(",", filter.Extensions ?? Array.Empty<string>())}{(filter.Glob is null ? "" : $" glob={filter.Glob}")}{(filter.ModifiedSince is null ? "" : $" since={filter.ModifiedSince:yyyy-MM-dd}")})");

            if (o.ListOnly)
            {
                foreach (var m in matches) Console.WriteLine($"  {m.FullPath}");
                return 0;
            }
            if (matches.Count == 0) return 0;

            Console.WriteLine($"pulling -> {outDir}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = MtpPuller.Pull(dev, filter, outDir, o.Overwrite,
                onFile: (item, n, total, skipped) =>
                {
                    string suffix = skipped ? "(exists, skipped)"
                        : File.Exists(Path.Combine(outDir, item.Name)) ? $"{new FileInfo(Path.Combine(outDir, item.Name)).Length / 1e6:F0} MB" : "failed";
                    Progress.Line(n, total, $"{item.Name}  {suffix}", error: suffix == "failed");
                });
            Console.WriteLine($"\ndone: {result.Downloaded} downloaded, {result.Skipped} skipped, {result.Failed} failed, " +
                              $"{result.BytesDownloaded / 1e9:F2} GB in {sw.Elapsed.TotalSeconds:F0}s -> {outDir}");
            return result.Failed == 0 ? 0 : 1;
        }

        /// <summary>`convert` — the ported Lumen path: open the `.lri` and export it. With no options that is a DNG
        /// and the companion JPG at full size with every value from the capture; the GRID flags only select a smaller
        /// output grid, and the ADJUSTMENTS flags (which the run echoes) are the only ones that change the pixels or
        /// the tags. <see cref="ConvertCmd.TryPlan"/> validates the combination and builds the request.</summary>
        private static int RunConvert(List<string> files, Options o)
        {
            int total = files.Count;
            var plan = ConvertCmd.TryPlan(o, total, out string? planError);
            if (plan is null) { Console.Error.WriteLine($"error: {planError}"); return 2; }
            var request = plan.Request;
            Console.WriteLine($"lux-light convert: {total} file(s), {o.Threads} thread(s), Lumen export path"
                              + (o.Level != 0 ? $", level {o.Level}" : "") + (o.Size is { } s ? $", size {s.W}x{s.H}" : "")
                              + $", formats {plan.FormatsLabel}"
                              + $" -> {o.OutFile ?? o.OutDirectory ?? "<beside input>"}");
            foreach (var note in plan.Notes) Console.WriteLine($"  {note}");
            var swAll = Stopwatch.StartNew();
            int done = 0, failed = 0, refused = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = o.Threads };
            Parallel.ForEach(files, po, path =>
            {
                var sw = Stopwatch.StartNew();
                string stem = Path.GetFileNameWithoutExtension(path);
                string outDir = o.OutDirectory ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", "lux_convert");
                try
                {
                    Action<string>? flog = Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1"
                        ? m => Console.Error.WriteLine($"  [{stem}] {m}") : null;
                    var req = o.OutFile is string one
                        ? request with { OutFile = one }
                        : request with { OutDirectory = outDir, Stem = stem };
                    var res = Exporter.Run(path, req, flog);
                    int n = Interlocked.Increment(ref done);
                    // one entry per format: the Lumen rasters by extension and size, lens-frames as a count
                    var lens = res.Outputs.Where(x => x.Name == "lens-frames").ToList();
                    string what = string.Join(" + ", res.Outputs.Where(x => x.Name != "lens-frames")
                        .Select(x => $"{Path.GetExtension(x.Path).TrimStart('.').ToUpperInvariant()} {x.Bytes / 1e6:F1} MB")
                        .Concat(lens.Count > 0 ? new[] { $"{lens.Count} lens JPG" } : Array.Empty<string>()));
                    string size = res.Width > 0 ? $"{res.Width}x{res.Height} " : "";
                    Progress.Line(n, total, $"{stem}  ({size}{what}, {sw.Elapsed.TotalSeconds:F0}s)");
                    if (res.Refusals.Count > 0)
                    {
                        // a requested output this capture legitimately cannot produce — the other formats are on disk
                        Interlocked.Increment(ref refused);
                        foreach (var r in res.Refusals) Progress.Line(n, total, $"{stem}  refused: {r}", error: true);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    int n = done + failed;
                    Progress.Line(n, total, $"{stem}  FAILED: {ex.Message}", error: true);
                    if (Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1") Console.Error.WriteLine(ex);
                }
            });
            Console.WriteLine($"\ndone: {done}/{total} converted, {failed} failed{(refused > 0 ? $", {refused} with a refused format" : "")}, {swAll.Elapsed.TotalSeconds:F1}s");
            return failed == 0 && refused == 0 ? 0 : 1;
        }

        /// <summary>Print the load-time state Lumen derives from a capture (SoT §3): neutral, exposure ratio/ev,
        /// histogram site means, lens-shading multiplier, tone-curve selection inputs.</summary>
        private static int RunInspect(List<string> files)
        {
            foreach (var path in files)
            {
                var lri = LriFile.Load(path);
                var st = Lux.Engine.Pipeline.CaptureState.FromReference(lri);
                var vp = lri.ViewPreferencesBlock;
                Console.WriteLine($"{Path.GetFileName(path)}: ref {st.Module} colour={st.IsColour} modules={string.Join(",", lri.Modules.Keys.OrderBy(k => k))}");
                Console.WriteLine($"  neutral        {string.Join(" ", st.Neutral.Select(x => x.ToString("F7")))}");
                Console.WriteLine($"  exposure ratio {st.ExposureRatio:F6}  ev_offset/BaselineExposure {st.EvOffset:F6}  (vp image_gain {vp?.ImageGain} time {vp?.ImageIntegrationTimeNs} ns)");
                Console.WriteLine($"  hist raster-site means {string.Join(" ", st.RawHistograms.Select(h => Lux.Engine.Pipeline.CaptureState.HistMean(h).ToString("F4")))}  (CapturedImage+0x1d8 order [R,Gr,Gb,B]: {string.Join(" ", st.Histograms.Select(h => Lux.Engine.Pipeline.CaptureState.HistMean(h).ToString("F4")))})");
                Console.WriteLine($"  multiplier site {st.MultiplierSite} (raster {st.MultiplierRasterSite}) mean {st.HistogramMean:R}");
                Console.WriteLine($"  lens_shading.multiplier {st.LensShadingMultiplier:F6} ({BitConverter.SingleToUInt32Bits(st.LensShadingMultiplier):x8})");
                Console.WriteLine($"  tone_mapping.type (Lumen 2.3 exports) {Lux.Engine.Pipeline.ToneMappingSelection.Select(rendererV2: true, lowLight: false)}");
            }
            return 0;
        }

        /// <summary>Compute Lumen's colour profile (SoT §9: CCT ordering, ΔE2000 matrix fit, ForwardMatrix, HueSatMap)
        /// and print it.</summary>
        private static int RunProfile(List<string> files)
        {
            foreach (var path in files)
            {
                var lri = LriFile.Load(path);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var p = Lux.Engine.Pipeline.Color.LumenProfile.Compute(lri, m => Console.WriteLine("  " + m));
                Console.WriteLine($"{Path.GetFileName(path)}: ref {lri.ReferenceModule}, {p.All.Count} illuminants, fit {sw.ElapsedMilliseconds} ms");
                foreach (var (tag, e) in new[] { ("1", p.Low), ("2", p.High) })
                {
                    Console.WriteLine($"  illuminant{tag} {e.ProtoType} (EXIF {Lux.Engine.Pipeline.Color.LumenColorTables.ExifLightSource(e.InternalIlluminant)}) CCT {e.Cct:F1} tint {e.Tint:F3}");
                    Console.WriteLine($"    FM{tag} fit   {Fmt(e.Fit.ForwardMatrix)}");
                    Console.WriteLine($"    |FM-proto| {MaxDiff(e.Fit.ForwardMatrix, e.ProtoForwardMatrix.Select(x => (double)x)):E2}   |M-M0| {MaxDiff(e.Fit.Matrix, e.Fit.InitialMatrix.Select(x => (double)x)):E2}   {e.Fit.Termination}");
                    var hs = e.Fit.ToDngHueSatMap();
                    Console.WriteLine($"    HueSat{tag} hue {Min(hs, 0):F3}..{Max(hs, 0):F3}°  sat {Min(hs, 1):F4}..{Max(hs, 1):F4}  val {Min(hs, 2):F4}..{Max(hs, 2):F4}");
                }
            }
            return 0;

            static string Fmt(float[] m) => string.Join(" ", m.Select(x => x.ToString("F7")));
            static double MaxDiff(float[] a, IEnumerable<double> b) { double d = 0; int i = 0; foreach (var v in b) { if (i >= a.Length) break; d = Math.Max(d, Math.Abs(a[i++] - v)); } return d; }
            static float Min(float[] a, int c) { float m = float.MaxValue; for (int i = c; i < a.Length; i += 3) m = Math.Min(m, a[i]); return m; }
            static float Max(float[] a, int c) { float m = float.MinValue; for (int i = c; i < a.Length; i += 3) m = Math.Max(m, a[i]); return m; }
        }

        /// <summary>Print the module-ISP configuration Lumen builds for the reference module (SoT §4.2): the tuning
        /// values that differ from the defaults per config level, the stage list the runner would execute (with the
        /// implementations still missing), and the white-balance state (AsShot neutral → xy → CCT/tint → ISP neutral).</summary>
        private static int RunIsp(List<string> files, Options opts)
        {
            if (opts.Has("--level")) { Console.Error.WriteLine("error: `isp` takes --isp-level (the module-ISP config level); --level is convert's export level"); return 2; }
            foreach (var path in files)
            {
                var lri = LriFile.Load(path);
                var profile = Lux.Engine.Pipeline.Color.LumenProfile.Compute(lri);
                var wb = Lux.Engine.Pipeline.Color.WhiteBalance.CaptureWb.From(lri, profile);
                var info = Lux.Engine.Pipeline.Isp.ModuleFrameInfo.From(lri, lri.ReferenceModule);
                var rp = (Lux.Engine.Pipeline.Isp.RendererProfile)opts.Profile;
                Console.WriteLine($"{Path.GetFileName(path)}: ref {lri.ReferenceModule} sensor {info.Sensor} colour={info.IsColour} data_scale=({info.DataScaleX},{info.DataScaleY}) hpLeak={info.HasHotPixelLeakageCalibration} stack={info.StackedFrameCount} expRatio={info.ExposureRatio:F4} profile={rp}");
                Console.WriteLine($"  wb: AsShot {Fmt(wb.AsShotNeutral)}  xy ({wb.X:F6},{wb.Y:F6})  CCT {wb.Cct:F2} tint {wb.Tint:F4}  ISP neutral {Fmt(wb.IspNeutral)}  ratio ISP/AsShot(G=1) {Fmt(new[] { wb.IspNeutral[0] / (wb.AsShotNeutral[0] / wb.AsShotNeutral[1]), 1f, wb.IspNeutral[2] / (wb.AsShotNeutral[2] / wb.AsShotNeutral[1]) })}");
                var defaults = Lux.Engine.Pipeline.Tuning.LumenDefaults();
                foreach (int level in opts.IspLevel >= 0 ? new[] { opts.IspLevel } : new[] { 0, 1, 2, 3, 4, 5 })
                {
                    var t = Lux.Engine.Pipeline.Isp.ModuleIspTuning.Build(level, rp, info, wb.Cct, wb.Tint);
                    var diff = t.All.Where(kv => !defaults.Has(kv.Key) || !Equals(Str(defaults, kv.Key), Str(t, kv.Key))).OrderBy(kv => kv.Key)
                        .Select(kv => $"{kv.Key}={Str(t, kv.Key)}");
                    Console.WriteLine($"  level {level}: {string.Join(" ", diff)}");
                    var req = Lux.Engine.Pipeline.StageGraph.Required(t);
                    var missing = req.Where(r => !Lux.Engine.Pipeline.StageRegistry.Registered.Any(k => k.Domain == Lux.Engine.Pipeline.PayloadDomain.Bayer && k.Stage == r.Stage && k.Type == r.Type)).ToList();
                    Console.WriteLine($"    stages: {string.Join(" → ", req.Select(r => $"{r.Stage}:{r.Type}"))}");
                    if (missing.Count > 0) Console.WriteLine($"    missing implementations: {string.Join(", ", missing.Select(r => $"{r.Stage}:{r.Type}"))}");
                }
            }
            return 0;

            static string Fmt(float[] m) => string.Join(" ", m.Select(x => x.ToString("F6")));
            static string Str(Lux.Engine.Pipeline.Tuning t, string k) { var v = t.All.First(kv => kv.Key == k).Value; return v is double[] a ? "[" + string.Join(",", a) + "]" : System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!; }
        }

        /// <summary>Run the module ISP on a centre ROI of the reference module at the requested level and write a
        /// gamma-encoded PPM next to the .lri (smoke test of the ported stage chain).</summary>
        private static int RunIspRun(List<string> files, Options opts)
        {
            if (opts.Has("--level")) { Console.Error.WriteLine("error: `isp-run` takes --isp-level (the module-ISP config level); --level is convert's export level"); return 2; }
            foreach (var path in files)
            {
                var lri = LriFile.Load(path);
                var profile = Lux.Engine.Pipeline.Color.LumenProfile.Compute(lri);
                var wb = Lux.Engine.Pipeline.Color.WhiteBalance.CaptureWb.From(lri, profile);
                var frame = Lux.Engine.Pipeline.Isp.CapturedFrame.Load(lri, lri.ReferenceModule);
                int level = Math.Max(opts.IspLevel, 0);
                var tuning = Lux.Engine.Pipeline.Isp.ModuleIspTuning.Build(level, (Lux.Engine.Pipeline.Isp.RendererProfile)opts.Profile, frame.Info, wb.Cct, wb.Tint);
                var isp = new Lux.Engine.Pipeline.Isp.SoftIsp(tuning, profile);
                int rw = 512, rh = 384; var roi = new Lux.Engine.Pipeline.RectI((frame.Width - rw) / 2 & ~1, (frame.Height - rh) / 2 & ~1, 0, 0); roi = new(roi.X0, roi.Y0, roi.X0 + rw, roi.Y0 + rh);
                double rawMean = 0; for (int y = roi.Y0; y < roi.Y1; y++) for (int x = roi.X0; x < roi.X1; x++) rawMean += frame.Raw[y * frame.Stride + x]; rawMean /= (double)rw * rh;
                var stats = isp.ComputeStats(frame);
                Console.WriteLine($"  raw ROI mean {rawMean:F1} DN; black {frame.Info.Noise?.Black} white {frame.Info.Noise?.White}; neutral {string.Join(",", stats.Neutral.Select(v => v.ToString("F4")))} irBlend {stats.IrBlend}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var img = isp.ProcessBayer(frame, roi, level, m => Console.WriteLine("  " + m));
                sw.Stop();
                string outp = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + $"_isp_l{level}.ppm");
                using (var f = File.Create(outp))
                {
                    var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{img.Width} {img.Height}\n255\n"); f.Write(hdr);
                    var buf = new byte[img.Width * 3];
                    for (int y = 0; y < img.Height; y++) { var row = img.Row(y); for (int x = 0; x < img.Width; x++) { buf[x * 3] = G(row[x].R); buf[x * 3 + 1] = G(row[x].G); buf[x * 3 + 2] = G(row[x].B); } f.Write(buf); }
                }
                float mn = float.MaxValue, mx = float.MinValue; int nan = 0;
                foreach (var v in img.Data) { if (float.IsNaN(v.R) || float.IsNaN(v.G) || float.IsNaN(v.B)) nan++; mn = Math.Min(mn, Math.Min(v.R, Math.Min(v.G, v.B))); mx = Math.Max(mx, Math.Max(v.R, Math.Max(v.G, v.B))); }
                Console.WriteLine($"{Path.GetFileName(path)}: level {level} ROI {roi} → {img.Width}x{img.Height} in {sw.Elapsed.TotalSeconds:F1}s; range [{mn:G4}, {mx:G4}] nan {nan}; wrote {outp}");
            }
            return 0;
            static byte G(float v) { v = Math.Clamp(v, 0f, 1f); return (byte)Math.Round(MathF.Pow(v, 1f / 2.2f) * 255f); }
        }

        private static int UnknownCommand(string cmd) { CommandSets.ReportUnknown(cmd); return Help(2); }

        private static int Help(int code = 0)
        {
            Console.WriteLine("""
                lux-light — A CLI to convert Light L16 .lri captures into a variety of formats

                USAGE:
                  lux-light <command> [options] [input...]
                  lux-light --help

                COMMANDS:
                  convert <input...>          Process a .lri and export it exactly as Lumen does: the image pipeline
                                              run once, written out in any combination of export formats (see
                                              below). Everything comes from the .lri, and no option changes the
                                              Lumen pixels unless you pass one from ADJUSTMENTS;
                                              --level/--size/--origin only pick the output grid.
                  inspect <input...>          Print the load-time state derived (neutral, ev, multiplier)
                  mod-info <input...>         Per-module CameraID, gain, exposure, frame black, relative_brightness
                  profile <input...>          compute and print the colour profile (matrix fit, ForwardMatrix,
                                              HueSatMap)
                  isp <input...>              Module-ISP tuning per config level, stage list, white-balance state
                  isp-run <input...>          Run the module ISP over a centre ROI, write a gamma-encoded PPM
                  devices                     List connected MTP cameras
                  pull [options]              Pull matching files off the camera

                INPUT:
                  convert, inspect, mod-info, profile, isp and isp-run take one or more .lri files and/or
                  directories (directories are scanned for *.lri). devices and pull take no input files.

                REQUIREMENTS:
                  - The .NET 10 runtime.
                  - ffmpeg on PATH for the ANIMATED parallax formats only (parallax-wiggle, parallax-wiggle-interp,
                    parallax-orbit, parallax-single, parallax-rack, parallax-dolly): the frames are produced by Lux,
                    the GIF/WebP/AVIF/APNG container is written by ffmpeg. Convert checks for it before rendering
                    anything and stops with exit 2 if it is missing.

                CONVERT OPTIONS:
                  With no options specified, `convert` replicates Lumen output exactly: DNG (fmt 2) + companion JPG
                  (fmt 0), full size, every value processed from the .lri. The flags marked * depart from that, and
                  the run echoes which were applied.

                  OUTPUT
                  -o, --out-directory <dir>   Write <stem>.<ext> per input (default: a lux_convert/ beside the .lri)
                      --out-file <path>       Name the output file (only when the run makes exactly one file)
                  -j, --threads <n>           Inputs converted in parallel (default: CPU count)
                      --formats <list>        Original (extended): dng, jpg, hdr, ppm, jpg+depth
                                              New:   depth, lens-frames, parallax-wiggle, parallax-wiggle-interp,
                                                     parallax-orbit, parallax-single, parallax-rack, parallax-dolly,
                                                     parallax-dof, parallax-anaglyph, parallax-crosseye, parallax-sbs,
                                                     parallax-still
                                              all:   every format above except hdr and ppm
                                              Comma-separated (default dng,jpg)
                                              `depth` is the metric-millimetre stereo pair (<stem>_depth.f32 +
                                              <stem>_depth.jpg) on the exported grid. The Lux formats are named
                                              <stem>_<format>.<ext>; lens-frames writes <stem>_<module>.jpg.

                  GRID (picks the pixel grid; leaves tone and colour alone)
                      --level <n>             export level, 0 = full size (default)
                      --size <w,h>            explicit output size (default: the level's export window)
                      --origin <x,y>          level-0 export window origin

                  SHARED ADJUSTMENTS
                    * --rotate <90|180|270>   bake the orientation (Orientation stays 1). All four raster formats; a
                                              depth map written alongside them follows the same rotation. Not
                                              combinable with the parallax formats (their rig geometry is in the
                                              unrotated frame)
                    * --fnum <f>              Exif FNumber [dng, jpg, jpg+depth]
                    * --iso <n>               Exif ISO [dng, jpg, jpg+depth]
                    * --focal <n>             Exif focal length in mm [dng, jpg, jpg+depth]

                  DNG
                    * --dng-cs <n>            colour-space property 0x13 (default 0 = none, the app path)
                    * --dng-tone <profile>    acr | light_v1 | light_v1_lowlight | light_v2
                    * --dng-comp <0|1>        0 uncompressed, 1 lossless JPEG (default)

                  JPEG   (each of these also applies to jpg+depth)
                    * --jpeg-cs <n>           colour-space property 0x13 (default 4 = srgb)
                    * --jpeg-quality <n>      libjpeg quality (default 98)
                    * --jpeg-sub <0|1|2>      chroma subsampling (default 2 = 4:2:0)
                    * --jpeg-v2               the renderer+0x64 v2 tone-mapping gate
                    * --jpeg-modify <ts>      Exif 0x0132 ModifyDate, YYYY-MM-DDTHH:MM:SS (default: now)
                    * --jpeg-comment <s>      the JPEG COM marker text
                    * --jpeg-software <s>     the Exif Software string

                  HDR
                    * --hdr-cs <n>            colour-space property 0x13 (default 1 = linear_srgb)

                  PPM
                      (no options)

                  DEPTH
                      (no options)

                  LENS-FRAMES   (Each module of the capture as a display JPG through the ported module ISP. A
                                 STACKED capture has several frames per module: `all` writes every one as
                                 <stem>_f<k>_<module>.jpg, an index picks one, and the default is frame 0, the frame
                                 Lumen's StackFusion references.)
                      --lens-quality <n>      JPEG quality 1-100 (default 92)
                      --lens-ev <float>       exposure adjust in stops (default 0.95)
                      --lens-level <n>        module-ISP config level (default 0 = full-res, no denoise)
                      --lens-profile <p>      CIAPI RendererProfile 0-3 (default 3 = Desktop)
                      --lens-modules <list>   comma-separated module filter, e.g. A1,B4
                      --lens-stack <all|n>    which frame of a stacked capture (default 0)

                  PARALLAX   (EXPERIMENTAL: The base imagery is the pipeline's module-ISP frames, or this run's JPEG
                              render and its metric depth. Only the 28 mm A array is a multi-view rig; a telephoto
                              capture has no A modules, so parallax-wiggle refuses it and the depth formats run
                              single-view.)

                      parallax-wiggle         A-group frames, colour-matched, swept in spatial order. Module renders
                                              only, no registration [ffmpeg]
                      parallax-wiggle-interp  N virtual viewpoints along the rig's axis, synthesised from the depth,
                                              disocclusions filled from the other real modules [ffmpeg]
                      parallax-orbit          the same synthesis on a closed circular path [ffmpeg]
                      parallax-single         single-view 2.5D: the sweep with no multi-view fill [ffmpeg]
                      parallax-rack           animated rack focus [ffmpeg]
                      parallax-dolly          dolly zoom, pulling back while zooming in [ffmpeg]
                      parallax-dof            synthetic depth of field, one PNG still
                      parallax-anaglyph       red/cyan (Dubois) anaglyph of a synthesised stereo pair, PNG
                      parallax-crosseye       cross-eye side-by-side stereo pair, PNG
                      parallax-sbs            parallel-view side-by-side stereo pair, PNG
                      parallax-still          one synthesised viewpoint, PNG
                      Every format but parallax-wiggle forces the level-0 build (announced, as jpg+depth is) and
                      synthesises from this run's JPEG render at --level, downscaled to --parallax-size: --level 2
                      renders the base at 2608 px, plenty for a 1600 px animation and far faster than full size.
                      --parallax-format <c>   animation container: gif (default) | webp | avif | apng
                      --parallax-size <n>     long edge of the working image in px (default 1600; 0 = native, for
                                              parallax-wiggle)
                      --parallax-ms <n>       per-frame duration in ms (default 100 for parallax-wiggle, 70 for
                                              -rack and -dolly, 60 otherwise)
                      --parallax-frames <n>   virtual viewpoints / animation frames (default 24)
                      --parallax-loop <k>     pingpong (default) | forward (the orbit is closed and plays forward)
                      --parallax-fill <k>     donors (default: disocclusions from the other real A modules, then
                                              inpaint) | inpaint | none (holes left black, to see where they are)
                      --parallax-path <k>     sweep (default, along the rig's dominant axis, 12 deg off horizontal
                                              from the factory extrinsics) | arc | line, for parallax-wiggle-interp
                                              and -single
                      --parallax-baseline <mm>
                                              peak-to-peak path extent (default 71.49, the widest physical colour
                                              baseline A4-A5; usable to about twice that)
                      --parallax-converge <k>
                                              convergence plane: auto (default, the median depth) | none | metres.
                                              Everything at that depth stays fixed and the scene swings around it
                      --parallax-converge-at <x,y> read the convergence depth off the depth map at that pixel of the
                      working image instead
                      --parallax-ipd <mm>     stereo interocular distance (default 25 for the anaglyph, 63 for the
                                              side-by-side pairs; 63 with a near foreground is hard to view)
                      --parallax-anaglyph <k>
                                              dubois (default, least-squares de-ghosting) | colour | grey
                      --parallax-focus <m>    parallax-dof focus distance in metres (default: the 10th-percentile
                                              depth)
                      --parallax-focus-at <x,y> read the focus depth off the depth map at that pixel instead
                      --parallax-aperture <mm>
                                              aperture DIAMETER for -dof and -rack (default 20 = f/1.4 on the A
                                              group's 28 mm equivalent)
                      --parallax-layers <n>   depth layers in the composite (default 8)
                      --parallax-rack <m1,m2>
                                              rack the focus from m1 to m2 metres (default: the 10th- to the
                                              90th-percentile depth)
                      --parallax-rack-at <x1,y1;x2,y2> rack between the depths at two pixels instead
                      --parallax-subject <m>  parallax-dolly: the depth held constant, metres (default: the 10th
                                              percentile)
                      --parallax-subject-at <x,y> read it off the depth map at that pixel instead
                      --parallax-dz <mm>      dolly travel (default 400; positive pulls back and zooms in, the one
                                              direction a single capture can carry — negative is clamped)
                      --parallax-t <tx,ty>    parallax-still: the virtual camera translation in mm (default 40,0)
                      --parallax-quality <n>  WebP encoder quality (default 88) [--parallax-format webp]
                      --parallax-crf <n>      AVIF encoder crf (default 18) [--parallax-format avif]
                      --parallax-order <k>    parallax-wiggle frame order: sweep (default, along the rig's dominant
                                              axis from the factory extrinsics) | label (module-name order) | an
                                              explicit list, e.g. A5,A1,A3,A4
                      --parallax-pivot <x,y,w,h> parallax-wiggle: hold a region still so the scene swings around it;
                      integer shift only, no resampling. Native sensor pixels

                INSPECT OPTIONS:
                      (none — inspect takes only input paths)

                MOD-INFO OPTIONS:
                      (none — mod-info takes only input paths)

                PROFILE OPTIONS:
                      (none — profile takes only input paths)

                ISP OPTIONS:
                      --isp-level <n>         module-ISP config level (default 0; -1 prints every level 0-5)
                      --profile <p>           CIAPI RendererProfile 0-3 (default 3 = Desktop)

                ISP-RUN OPTIONS:
                      --isp-level <n>         module-ISP config level (default 0)
                      --profile <p>           CIAPI RendererProfile 0-3 (default 3 = Desktop)
                      (the PPM is written beside the .lri as <stem>_isp_l<n>.ppm)

                DEVICES OPTIONS:
                      (none)

                PULL OPTIONS:
                  -o, --out-directory <dir>   file destination (default: ./lux_pull)
                      --ext <list>            extensions to include (default .lri)
                      --glob <pattern>        filename glob, e.g. "L16_004*"
                      --since <date>          only files modified on/after this date
                      --overwrite             re-download even if a same-size file exists locally
                      --list                  list matching files without downloading

                GLOBAL OPTIONS:
                  -h, --help                  this help

                ENVIRONMENT VARIABLES:
                  * For diagnostic / development use only

                  LOGGING
                      LUX_VERBOSE=1           convert: per-file progress detail, and the full exception trace when a
                                              file fails instead of just its message

                  BEHAVIOUR — these change what the pipeline computes
                      LUX_CC_FM=<fm1;fm2>     replace the fitted ForwardMatrix1/ForwardMatrix2 in colour correction
                                              with two space-separated 9-float matrices
                      LUX_CC_M=<m>            replace the camera→working matrix outright (9 floats, or 0x-prefixed
                                              bit patterns), bypassing the fit and the CCT interpolation
                      LUX_CNR_NEUTRAL=r,g,b   colour-noise reduction uses this neutral instead of the ISP stats'
                      LUX_CNR_NOEXT=1         colour-noise reduction reads no pixels outside its own region (drops
                                              the ±2-pixel ring)
                      LUX_FRAME_BLACK=<f>     linearize uses this black level instead of the stats'/frame's
                      LUX_FUSION_SRC_BLACK=db|estimate the black level for the fusion's non-reference source frames:
                      db = the sensor database value, estimate = the per-frame estimate
                      LUX_GUIDE_SKIP=<list>   set these comma-separated stages to type "none" in the reference-guide
                                              ISP tuning
                      LUX_JPEG_ROUND=rne      the JPEG export stores through the display path's round-half-to-even
                                              convert instead of the export path's round-half-away-from-zero
                      LUX_LU_RANK=rank        the Ceres line search's FullPivLU uses the Eigen 3.3 rank threshold
                                              instead of the 3.2 nonzero-pivot rule Lumen's ceres.dll behaves like
                      LUX_NO_GUIDE=1          registration runs without building the reference guide image
                      LUX_NO_MONO=1           force the colour fusion branch on a capture that has a monochrome
                                              module
                      LUX_SGM_XSW=1           the SGM cost sweep clamps its forward x start to W instead of W-1
                      LUX_SKIP_MISSING=1      an unimplemented stage is skipped with a warning instead of throwing
                      LUX_SKIP_STAGE=<list>   these comma-separated stages keep their padding and alignment but are
                                              never run
                      LUX_STACK_MARGIN=<n>    ISP tile margin for a stacked capture's reference fusion, instead of
                                              the halo the sensor gain selects
                      LUX_STEREO_DEMOSAIC=<t>
                                              the demosaicking type of the stereo ISP tuning (default collapse2)
                      LUX_STEREO_SKIP=<list>  set these comma-separated stages to type "none" in the stereo ISP
                                              tuning
                      LUX_STEREO_TILE=<n>     the stereo ISP work-image tile size (default 256, Lumen's)
                      LUX_TELE_CFG=<n>        the telephoto module's ISP config level (default 1, Lumen's)
                      LUX_TELE_ONLYRECT=x0,y0,x1,y1 run the telephoto ISP only for that one grown rect; every other
                      rect comes back zeroed
                      LUX_TELE_SET=k=v;k=v    apply these tuning overrides to the telephoto module's ISP tuning
                      LUX_WARP_ORDER=cols|rows|swap|prod accumulation order of the 6-tap warp interpolation (default
                      cols)

                  PRINTING — stderr only; the pixels are unaffected
                      LUX_BIL_DEBUG=1         the bilateral kernel window and size at each call
                      LUX_CC_DEBUG=1          the colour-correction CCT interpolation inputs, with bit patterns
                      LUX_CNR_DEBUG=<any>     the colour-noise-reduction per-tile statistics for the (0,0) tile
                      LUX_HP_DEBUG=x0,y0,x1,y1 per-pixel hot-pixel decisions inside that rect
                      LUX_ISP_DEBUG=1         the ISP runner's stage list and per-stage rects (once), plus the
                                              linearize black/white, the crosstalk blend and the hybrid-denoise
                                              thresholds
                      LUX_PP_DEBUG=x,y        the post-processing tile arithmetic around the pixel at x,y
                      LUX_TELE_DEBUG=1        per-telephoto-module white balance, black, IR blend and tone tuning

                  WRITING FILES — each writes raw intermediate images beside the prefix you give
                      LUX_CNR_DUMP=<prefix>   colour-noise reduction: the kernel input region, its arguments and
                                              every pyramid level, as <prefix>_cnr_*.f32 / _cnr_args.txt
                      LUX_DEMOSAIC_DUMP=<dir>
                                              the light_v1 demosaic's A/B/C planes, as A.f32/B.f32/C.f32 in <dir>
                      LUX_HP_DUMP=<prefix>    hot-pixel removal: the per-channel sensor σ tables, as
                                              <prefix>_sigma<c>.f32
                      LUX_HYB_DUMP=<prefix>   hybrid denoise: every intermediate, as <prefix>_hyb_<tag>.f32
                      LUX_MONO_DUMP=<prefix>  mono fusion: the per-stage RGB and float-Bayer images of the mono
                                              module's own ISP, as <prefix>_own_st<i>_<stage>.<kind>.bin
                      LUX_TELE_ISPDUMP=<pre>  the telephoto level-0 cache's whole grown-rect ISP output, as
                                              <prefix>_<module>_isp_<x0>_<y0>_<x1>_<y1>.bin
                """);
            return code;
        }
    }
}
