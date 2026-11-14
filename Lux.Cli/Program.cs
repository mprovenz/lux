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

            // a photo's saved data lives next to it, or — when its folder cannot be written — in the desktop app's local context store;
            // read it from there too (LUX_CONTEXT_STORE, else the app's setting, else <app data>/contexts; LUX_APP_DATA moves <app data>)
            try { Lux.Store.LocalContextStore.Current ??= Lux.Store.LocalContextStore.FromEnvironment(); }
            catch (InvalidOperationException e) { Console.Error.WriteLine($"error: {e.Message}"); return 2; }

            // the device and version commands take no input files
            if (command == "version") return VersionCmd.Run(opts);
            if (command == "devices") return RunDevices();
            if (command == "pull") return RunPull(opts);
            if (command == "import-lumen-edits")
            {
                if (positionals.Count == 0) { Console.Error.WriteLine("error: no .lris file or folder given."); return 2; }
                return EditsCmd.ImportLumen(positionals);
            }
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
                "edits-info" => EditsCmd.Info(files, opts),
                "cache" => CacheCmd.Run(files, opts),
                "animate" => AnimateCmd.Run(files, opts),
                "inspect" => RunInspect(files),
                "profile" => RunProfile(files),
                "isp" => RunIsp(files, opts),
                "isp-run" => RunIspRun(files, opts),
                _ => UnknownCommand(command),
            };
        }

        /// <summary>The message for a camera that is on the bus but cannot be opened.</summary>
        private static string OpenFailure(MtpException e) => e.Kind switch
        {
            MtpErrorKind.Busy => "the camera is in use by another program (often the desktop's file manager has opened it): close it there or unmount the camera, then try again.",
            MtpErrorKind.NotFound or MtpErrorKind.Disconnected => "the camera went away while it was being opened; check the cable and try again.",
            _ => "the camera could not be opened (" + e.Message + ").",
        };

        /// <summary>Open the first camera on the bus; null (after printing why) when there is none or none opens.</summary>
        private static IMtpDevice? OpenFirst(IMtpBackend backend, string noneMessage)
        {
            var found = backend.Autodetect();
            if (found.Count == 0) { Console.Error.WriteLine(noneMessage); return null; }
            MtpException? last = null;
            foreach (var c in found)
            {
                try { return backend.Open(c); }
                catch (MtpException e) { last = e; }
            }
            Console.Error.WriteLine("error: " + OpenFailure(last!));
            return null;
        }

        private static int RunDevices()
        {
            var backend = MtpFactory.Backend;
            Console.WriteLine($"MTP backend: {backend.Name} (available: {backend.IsAvailable})");
            if (!backend.IsAvailable) { Console.Error.WriteLine("no usable MTP backend on this platform."); return 1; }
            var found = backend.Autodetect();
            if (found.Count == 0) { Console.WriteLine("no MTP devices detected (is the camera connected and in file-transfer mode?)"); return 1; }
            int opened = 0;
            foreach (var c in found)
            {
                IMtpDevice d;
                try { d = backend.Open(c); }
                catch (MtpException e) { Console.WriteLine($"  {c.Model}  @ {c.Port}  — {OpenFailure(e)}"); continue; }
                using (d)
                {
                    opened++;
                    Console.WriteLine($"  {d.Name}  @ {d.Port}");
                    var info = d.Info;
                    if (info.Serial.Length > 0 || info.BatteryPercent is not null)
                        Console.WriteLine($"    {info.DisplayName}" + (info.Serial.Length > 0 ? $", serial {info.Serial}" : "") + (info.BatteryPercent is int b ? $", battery {b} %" : ""));
                    foreach (var s in d.Storages)
                        Console.WriteLine($"    {s.Path}  ({s.Description})");
                }
            }
            return opened > 0 ? 0 : 1;
        }

        private static int RunPull(Options o)
        {
            var backend = MtpFactory.Backend;
            if (!backend.IsAvailable) { Console.Error.WriteLine($"MTP backend '{backend.Name}' not available on this platform."); return 1; }
            using var dev = OpenFirst(backend, "no MTP device detected (connect the camera in file-transfer mode).");
            if (dev is null) return 1;

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

            // the bytes still to copy must fit on the destination drive (files already there at the same size are skipped)
            long toCopy = matches.Where(m => o.Overwrite || !MtpPuller.IsComplete(m, Path.Combine(outDir, m.Name))).Sum(m => Math.Max(0, m.Size));
            if (MtpPuller.CheckFreeSpace(outDir, toCopy) is { } space) { Console.Error.WriteLine("error: " + space); return 1; }

            Console.WriteLine($"pulling -> {outDir}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = MtpPuller.Pull(dev, matches, outDir, o.Overwrite,
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
            // Threads inside each conversion: the whole machine for one input at a time, shared out when several run at once.
            int renderThreads = o.RenderThreads ?? Math.Max(1, Environment.ProcessorCount / Math.Max(1, Math.Min(o.Threads, total)));
            Lux.Engine.Pipeline.BayerFusion.BlockFlow.Threads = renderThreads;   // the block-flow searches of the fusion set-up (process-wide)
            Console.WriteLine($"lux-light convert: {total} file(s), {Math.Min(o.Threads, total)} at once, {renderThreads} render thread(s) each, Lumen export path"
                              + (o.Level != 0 ? $", level {o.Level}" : "") + (o.Size is { } s ? $", size {s.W}x{s.H}" : "")
                              + $", formats {plan.FormatsLabel}"
                              + $" -> {o.OutFile ?? o.OutDirectory ?? "<beside input>"}");
            foreach (var note in plan.Notes) Console.WriteLine($"  {note}");
            var swAll = Stopwatch.StartNew();
            int done = 0, failed = 0, refused = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = o.Threads };
            // one progress line per input in flight and a job line, redrawn in place on a terminal (see ProgressBoard)
            using var board = new ProgressBoard(total, Math.Min(o.Threads, total), ProgressBoard.CanDrawInPlace());
            Progress.Board = board;
            int buildLevel = Exporter.BuildLevelFor(request);
            Parallel.ForEach(files, po, path =>
            {
                var sw = Stopwatch.StartNew();
                string stem = Path.GetFileNameWithoutExtension(path);
                string outDir = o.OutDirectory ?? Path.Combine(Path.GetDirectoryName(path) ?? ".", "lux_convert");
                var fp = board.Start(stem, ProgressBoard.PlanFor(request, buildLevel), renderThreads);
                try
                {
                    Action<string>? flog = Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1"
                        ? m => Console.Error.WriteLine($"  [{stem}] {m}") : null;
                    Action<Lux.Engine.Pipeline.ProgressUpdate> onProgress = u => board.Update(fp, u);
                    var req = o.OutFile is string one
                        ? request with { OutFile = one, RenderThreads = renderThreads, Progress = onProgress }
                        : request with { OutDirectory = outDir, Stem = stem, RenderThreads = renderThreads, Progress = onProgress };
                    var sctx = new Lux.Store.StoreCaptureContext(path);
                    req = req with { Context = sctx };
                    string? uprightNote = null;
                    if (o.Upright) (req, uprightNote) = ConvertCmd.Upright(path, req, sctx);
                    var res = Exporter.Run(path, req, flog);
                    if (uprightNote is not null) res = res with { Notes = new[] { uprightNote }.Concat(res.Notes).ToList() };
                    board.Completed(fp);   // a full conversion: its phase timings become the estimates' baseline
                    board.Finish(fp);
                    int n = Interlocked.Increment(ref done);
                    // one entry per format: the Lumen rasters by extension and size, lens-frames as a count
                    var lens = res.Outputs.Where(x => x.Name == "lens-frames").ToList();
                    string what = string.Join(" + ", res.Outputs.Where(x => x.Name != "lens-frames")
                        .Select(x => $"{Path.GetExtension(x.Path).TrimStart('.').ToUpperInvariant()} {x.Bytes / 1e6:F1} MB")
                        .Concat(lens.Count > 0 ? new[] { $"{lens.Count} lens JPG" } : Array.Empty<string>()));
                    string size = res.Width > 0 ? $"{res.Width}x{res.Height} " : "";
                    Progress.Line(n, total, $"{stem}  ({size}{what}, {sw.Elapsed.TotalSeconds:F0}s)");
                    foreach (var note in res.Notes) Progress.Line(n, total, $"{stem}  {note}");
                    if (res.Refusals.Count > 0)
                    {
                        // a requested output this capture legitimately cannot produce — the other formats are on disk
                        Interlocked.Increment(ref refused);
                        foreach (var r in res.Refusals) Progress.Line(n, total, $"{stem}  refused: {r}", error: true);
                    }
                }
                catch (Exception ex)
                {
                    board.Finish(fp);
                    Interlocked.Increment(ref failed);
                    int n = done + failed;
                    Progress.Line(n, total, $"{stem}  FAILED: {ex.Message}", error: true);
                    if (Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1") Console.Error.WriteLine(ex);
                }
            });
            Progress.Board = null; board.Dispose();
            Console.WriteLine($"\ndone: {done}/{total} converted, {failed} failed{(refused > 0 ? $", {refused} with a refused format" : "")}, {swAll.Elapsed.TotalSeconds:F1}s");
            return failed == 0 && refused == 0 ? 0 : 1;
        }

        /// <summary>Print the load-time state Lumen derives from a capture: neutral, exposure ratio/ev,
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

        /// <summary>Compute Lumen's colour profile (CCT ordering, ΔE2000 matrix fit, ForwardMatrix, HueSatMap)
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

        /// <summary>Print the module-ISP configuration Lumen builds for the reference module: the tuning
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
                  version [--offline]         Print this build's version and check GitHub for a newer release
                  edits-info <input...>       Show a capture's saved depth edits: the variants, which one is active,
                                              how much is stored, and whether everything stored is intact.
                                              --set-active <variant|none> chooses the variant `convert` uses
                  import-lumen-edits <lris file or folder...>
                                              Bring Lumen session files (.lris) into Lux: the depth map and depth
                                              edits, the depth effect, tone and colour settings, crop / rotation
                                              and rating Lumen saved for each photo; convert then renders the photo
                                              as Lumen does. Each session's capture (.lri of the same name) must
                                              sit beside it
                  animate <input...> --anim-effect <effect>
                                              An animation of each capture, as the desktop app makes them:
                                              rack-focus and aperture-sweep (Lumen's depth effect, each frame
                                              exactly the still convert writes with --f-number/--focus-depth),
                                              wigglegram (the wide lenses' own pictures), parallax (a small camera
                                              move drawn from the photo and its depth), lens-tour (every lens in
                                              turn). GIF, APNG and WebP are written by Lux itself; MP4 and WebM
                                              need ffmpeg
                  cache <input...> [--level none|geometry|13mp|full]
                                              Show a capture's analysis cache, or raise / lower it to a level:
                                              geometry = the camera geometry and the depth map (later exports skip
                                              that analysis, about half of a full-size export); 13mp = also the
                                              13 MP image and the previews; full = also the full-size image (a
                                              full-size export then only runs the output stage). Kept in
                                              LuxImageContext/cache/ next to the capture; exports from the cache
                                              are byte-identical to exports computed from scratch

                INPUT:
                  convert, animate, inspect, mod-info, profile, isp, isp-run, edits-info and cache take one or more .lri files
                  and/or directories (directories are scanned for *.lri). import-lumen-edits takes .lris files
                  and/or directories. devices, pull and version take no input files.

                REQUIREMENTS:
                  - The .NET 10 runtime.
                  - ffmpeg on PATH only for AVIF (--parallax-format avif), --parallax-encoder ffmpeg, and MP4 / WebM video
                    (animate --anim-container mp4|webm). GIF, APNG and WebP animations are written by Lux itself.
                    The commands check for it before rendering anything and stop with exit 2 if it is missing.

                CONVERT OPTIONS:
                  With no options specified, `convert` replicates Lumen output exactly: DNG (fmt 2) + companion JPG
                  (fmt 0), full size, every value processed from the .lri. The flags marked * depart from that, and
                  the run echoes which were applied.

                  OUTPUT
                  -o, --out-directory <dir>   Write <stem>.<ext> per input (default: a lux_convert/ beside the .lri)
                      --out-file <path>       Name the output file (only when the run makes exactly one file)
                  -j, --threads <n>           Inputs converted in parallel (default: CPU count)
                      --render-threads <n>    Threads per input for the tile render (default: CPU
                                              count ÷ the inputs converted at once; 1 = sequential)
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
                    * --transform <m;c>       a crop / turn / flip exactly as Lux and Lumen save it: the nine matrix
                                              values, ';', the crop corners x0,y0,x1,y1 (decimal, or 0x + the float's
                                              32-bit pattern); the size follows Lumen's full-size rule. All four raster
                                              formats; not combinable with --rotate or the parallax formats
                    * --upright               turn a photo the way the camera was held (its accelerometer), with
                                              Lumen's own rotate — as the desktop app exports it by default. Only
                                              when the photo's crop / rotation was never edited (a saved one is
                                              followed anyway); without it convert writes the capture as recorded,
                                              exactly as Lumen does. All four raster formats; not combinable with
                                              --rotate, --transform or the parallax formats
                    * --fnum <f>              Exif FNumber [dng, jpg, jpg+depth]
                    * --iso <n>               Exif ISO [dng, jpg, jpg+depth]
                    * --focal <n>             Exif focal length in mm [dng, jpg, jpg+depth]

                  DEPTH EFFECT (Lumen's synthetic depth of field; dng, jpg, jpg+depth, hdr, ppm)
                    * --f-number <N>          blur the picture as a lens of aperture f/N would, using the capture's
                                              depth map: the smaller N, the shallower the depth of field. Values at
                                              or above the capture's own all-in-focus f-number (about f/15 for a
                                              28 mm capture) leave the image unchanged, exactly as in Lumen, whose
                                              slider runs from f/4 up to that value; smaller values are accepted.
                                              The Exif FNumber becomes N. Needs the depth map, so the full
                                              registration runs even at a reduced --level
                    * --focus-depth <mm>      distance that stays sharp, in millimetres (default: the distance the
                                              camera focused at, as Lumen chooses it). Needs --f-number
                    * --focus-point <x,y>     focus on whatever is at this point of the picture instead, like
                                              clicking with Lumen's focus tool: x and y from 0 to 1, 0,0 = top
                                              left. Needs --f-number; not combinable with --focus-depth or --rotate

                  TONE & COLOUR ADJUSTMENTS (the controls of Lumen's editor; jpg and jpg+depth only)
                    These change how the picture is rendered, like the sliders in Lumen's editor. They affect the
                    JPG and jpg+depth images (and the on-screen preview) only: a DNG, PPM or HDR keeps the image as
                    it is before this rendering and is written exactly as without them. A control left out keeps
                    the picture as Lumen renders it.
                    * --exposure <stops>      brighter (+) or darker (-), -3 to 3 stops
                    * --contrast <v>          more (+) or less (-) contrast, -100 to 100
                    * --temperature <K>       white balance: colour temperature in kelvin, 2000 to 20000 (default:
                                              as shot); lower values make the picture bluer, higher warmer
                    * --tint <v>              white balance: green (-) to magenta (+), -150 to 150 (default: as shot)
                    * --vibrance <v>          strengthen (+) or mute (-) colours, the duller ones most, sparing
                                              skin tones, -100 to 100
                    * --saturation <v>        strengthen (+) or weaken (-) all colours, -100 (black and white) to 100
                    * --sharpening <v>        sharpen (+) or soften (-), -100 to 100 (default 5, Lumen's own setting);
                                              the same control as --jpeg-sharpening, give one of the two
                    * --shadows <v>           lift (+) or deepen (-) the shadows, -100 to 100
                    * --highlights <v>        recover (+) or brighten (-) the highlights, -100 to 100
                    * --clarity <v>           more (+) or less (-) local contrast in the midtones, -100 to 100
                    --shadows, --highlights and --clarity are Lux controls: the processing is part of Lumen's
                    software, which never offered it as controls.

                  SAVED DEPTH EDITS (the LuxImageContext folder next to the captures)
                    A capture whose depth map was edited — in Lux, or in Lumen and brought over with
                    `import-lumen-edits` — keeps those edits in LuxImageContext/edits/. Wherever convert reads the
                    depth (--f-number, jpg+depth, depth, the depth-based parallax formats) it uses the edited
                    depth of the capture's active variant, and says so. A variant imported from Lumen also carries
                    the photo's settings (depth effect, tone and colour, crop / rotation): convert then renders it
                    as Lumen does after reopening it, and an option given here replaces only its own setting.
                    Captures without saved edits are not affected in any way.
                    * --no-edits              ignore saved depth edits and saved settings: use the depth computed
                                              from the capture
                    * --edits <variant>       use this variant instead of the active one (`edits-info` lists them)

                  ANALYSIS CACHE (LuxImageContext/cache next to the captures; see the cache command)
                    Whatever a capture's cache holds replaces the work that would produce it — the output is
                    byte-identical either way. When the capture already has a LuxImageContext folder, convert also
                    keeps the camera geometry and depth map it computed there, so the next export of that capture
                    skips that analysis. convert never creates a LuxImageContext folder by itself.
                      --no-cache              neither read nor write the analysis cache
                      --memory-budget <GB>    keep a full-size render within about this much memory (default: no
                                              limit, the fastest). The same pixels, more time; see the README

                  DNG
                    * --dng-cs <n>            colour-space property 0x13 (default 0 = none, the app path)
                    * --dng-tone <profile>    acr | light_v1 | light_v1_lowlight | light_v2
                    * --dng-comp <0|1>        0 uncompressed, 1 lossless JPEG (default)

                  JPEG   (each of these also applies to jpg+depth)
                    * --jpeg-cs <n>           colour-space property 0x13 (default 4 = srgb)
                    * --jpeg-quality <n>      libjpeg quality (default 98)
                    * --jpeg-sub <0|1|2>      chroma subsampling (default 2 = 4:2:0)
                    * --jpeg-v2               the renderer+0x64 v2 tone-mapping gate
                    * --jpeg-sharpening <v>   the renderer's sharpening property (ParamFloat 13, -100..100; default 5 =
                                              Lumen's export state, tone_mapping.sharpening 1.0; 0 = Lumen's headless API export);
                                              the same control as --sharpening
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
                                              only, no registration
                      parallax-wiggle-interp  N virtual viewpoints along the rig's axis, synthesised from the depth,
                                              disocclusions filled from the other real modules
                      parallax-orbit          the same synthesis on a closed circular path
                      parallax-single         single-view 2.5D: the sweep with no multi-view fill
                      parallax-rack           animated rack focus (the experimental layered blur; `animate --anim-effect
                                              rack-focus` uses Lumen's depth effect)
                      parallax-dolly          dolly zoom, pulling back while zooming in
                      parallax-dof            synthetic depth of field, one PNG still
                      parallax-anaglyph       red/cyan (Dubois) anaglyph of a synthesised stereo pair, PNG
                      parallax-crosseye       cross-eye side-by-side stereo pair, PNG
                      parallax-sbs            parallel-view side-by-side stereo pair, PNG
                      parallax-still          one synthesised viewpoint, PNG
                      Every format but parallax-wiggle forces the level-0 build (announced, as jpg+depth is) and
                      synthesises from this run's JPEG render at --level, downscaled to --parallax-size: --level 2
                      renders the base at 2608 px, plenty for a 1600 px animation and far faster than full size.
                      --parallax-format <c>   animation container: gif (default) | webp | avif | apng. GIF, APNG and WebP
                                              are Lux's own writers; AVIF goes through ffmpeg. WebP is now LOSSLESS by
                                              default (every pixel exact), so it is several times larger than the
                                              earlier lossy WebP (46 frames at 800 px: 14.7 MB); --parallax-encoder
                                              ffmpeg gives the old lossy WebP (quality: --parallax-quality)
                      --parallax-encoder <e>  lux (default) | ffmpeg: the earlier ffmpeg recipes for every container (GIF
                                              with one palette and Bayer dithering, lossy WebP)
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
                      --parallax-quality <n>  ffmpeg's lossy WebP quality (default 88) [--parallax-format webp --parallax-encoder ffmpeg]
                      --parallax-crf <n>      AVIF encoder crf (default 18) [--parallax-format avif]
                      --parallax-order <k>    parallax-wiggle frame order: sweep (default, along the rig's dominant
                                              axis from the factory extrinsics) | label (module-name order) | an
                                              explicit list, e.g. A5,A1,A3,A4
                      --parallax-pivot <x,y,w,h> parallax-wiggle: hold a region still so the scene swings around it;
                      integer shift only, no resampling. Native sensor pixels

                ANIMATE OPTIONS:
                      --anim-effect <e>       rack-focus | aperture-sweep | wigglegram | parallax | lens-tour (required)
                      --anim-container <c>    gif (default) | apng | webp (lossless) | mp4 | webm (these two need ffmpeg)
                  -o, --out-directory <dir>   write <stem>_<effect>.<ext> (default: a lux_animate/ beside the .lri)
                      --anim-size <px>        long edge of the frames (default 1040); the frames come from the smallest
                                              export level at least that size
                      --level <n>             render from this export level instead
                      --anim-frames <n>       distinct frames (default 24; the wigglegram and lens tour have one per lens)
                      --anim-ms <n>           time per frame in ms (default 70; wigglegram 100, parallax 60, lens tour 500)
                      --anim-loop <k>         pingpong (default; the lens tour and the orbit: forward) | forward
                      --anim-repeat <n>       times it plays (default 0 = forever)
                      --f-number <N>          rack-focus: the aperture; aperture-sweep: the widest it opens to (default 2,
                                              the widest Lumen's slider offers)
                      --anim-f-from <N>       aperture-sweep: where it starts (default: all in focus)
                      --anim-focus <m1,m2>    rack-focus: pull the focus from m1 to m2 metres (default: the 10th to the 90th
                                              percentile of the scene's depth)
                      --focus-depth <mm>      aperture-sweep: the focus distance (default: as the camera focused)
                      --anim-path <k>         parallax: sweep (default) | arc | line | orbit
                      --anim-baseline <mm>    parallax: how far the virtual camera travels (default 71.49)
                      --anim-converge <k>     parallax: the distance that stays still: auto (median depth) | metres
                      --anim-single-view      parallax from the main view only (gaps painted in)
                      --anim-gif-dither <k>   ordered (default) | diffusion | none
                      --anim-gif-palette <k>  global (default: one palette, no flicker) | frame
                      --anim-no-diff          store every frame whole (by default only what changed is stored)
                      --anim-keep-frames <d>  also write each distinct frame as an exact PNG, the plan (with the convert
                                              options of each depth-effect frame) and the frames a viewer shows
                      --no-edits, --edits <v> as for convert
                      --upright               as for convert: a photo held turned comes out upright (the desktop app's
                                              default); the parallax, wigglegram and lens-tour frames are turned the same way

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

                VERSION OPTIONS:
                      --offline               print the version only, without asking GitHub
                  Exit status: 0 up to date (or no release published yet), 1 a newer release is available,
                  2 the check could not be made (no network, GitHub error). The release page is
                  https://github.com/mprovenz/lux/releases

                GLOBAL OPTIONS:
                  -h, --help                  this help

                ENVIRONMENT VARIABLES:
                  * For diagnostic / development use only

                  LOGGING
                      LUX_VERBOSE=1           convert: per-file progress detail, and the full exception trace when a
                      LUX_NO_PROGRESS=1       convert: no in-place progress lines (they are also off when stderr is not a terminal or
                                              LUX_VERBOSE=1 is set)
                      LUX_PROGRESS_TRACE=1    convert: print every progress update as a line (phase, done/total) — for
                                              debugging the phase flow
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
                                              <prefix>_<module>_isp_<x0>_<y0>_<x1>_<y1>.bin (on a stacked capture also
                                              the runner's inputs, <prefix>_<module>_stkin_<rect>_{bayer,std}.bin);
                                              LUX_TELE_ISPDUMP_RECTS="x0,y0,x1,y1;..." limits it to those grown rects
                      LUX_REF_ISPDUMP=<pre>   stacked capture: the reference cache's level ISP output and input of the
                                              stacked branch, <prefix>_ref_L<level>_src_<rect>_{out,bayer}.bin;
                                              LUX_REF_ISPDUMP_RECTS limits it to those source rects
                      LUX_DENSE_DUMP=<pre>    dense stereo (level-0 builds): the six pyramid depths, the full depth, the
                                              layer-0 inputs and (layers <= LUX_DENSE_DUMP_CV, default 2) the cost volumes,
                                              as raw dumps (int32 {w,h,stride,bpp} header + rows; cost volumes packed as
                                              in StereoAsyncApi.DumpCostVolume), <prefix>_<stem>_dense_L<i>_*; plus the
                                              registration stages as raw binary records (<prefix>_<stem>_{refpts,
                                              drv<i>_match<l>, flt<i>_cam<c>_{pre,post}, tri_*_points, bac<i>_{in,out},
                                              final<i>_slots, calibwrite<i>_{before,after}, tdrv<i>_*}.bin; layouts in
                                              RegDump.cs)
                      LUX_DENSE_OVERRIDE=<f>  inject a Lumen layer-5 depth dump (<out>_dense_L5_depth.f32) instead of
                                              computing the dense stereo (race-closure test; output is Lumen's schedule)
                      LUX_HYB_DUMP=<pre>      module ISP hybrid denoiser: every intermediate of every call as raw RGBA float
                                              rows, <prefix>_hyb_<tag>_<w>x<h>.f32 (the size keeps calls apart)
                      LUX_PP_DUMP=<pre>       module ISP PostProcessing: input, companion and output of every call as raw
                                              RGBA float rows, <prefix>_pp_{src,comp,out}_er<ratio>_<x0>_<y0>_<w>x<h>.f32,
                                              plus the parameters on stderr
                      LUX_STAGE_DUMP=<pre>    module ISP: the RGB working image after every stage as raw RGBA float rows,
                                              <prefix>_st_er<ratio>_o<x>_<y>_<index>_<stage>_<w>x<h>.f32
                      LUX_FUSION_DUMP=<pre>   level-1 fusion: every per-source float frame (<prefix>_srcframe_cam<id>.bin),
                                              the mono sources (<prefix>_mono_src<i>.bin), the fusion tiles listed in
                                              LUX_FUSION_DUMP_TILES="x0,y0,x1,y1;..." (<prefix>_tile_<rect>_{out,w}.bin) and
                                              the render inputs of the first 8 renders — or of the grown rect(s) in
                                              LUX_FUSION_DUMP_RECT — (<prefix>_render_<rect>_{bayer,std,...}.bin); all in
                                              int32 {w,h,stride,bpp} header + rows
                """);
            return code;
        }
    }
}
