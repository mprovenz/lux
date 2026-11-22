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
            if (command == "depth-model") return DepthModelCmd.Run(positionals);
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
            if (opts.TeleCheck && command is not ("convert" or "animate")) { Console.Error.WriteLine("error: --tele-check is an option of convert and animate."); return 2; }

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
            var planNotes = plan.Notes.ToList();
            if (o.DepthRefine)
            {
                // refined depth: the depth network must be installed already; this command never downloads it
                if (!request.NeedsDepth && request.DepthEffectFNumber is null) planNotes.Add("--depth-refine: none of the requested outputs reads the depth, so it changes nothing");
                else
                {
                    var network = DepthModelCmd.OpenForConvert(out string? modelError);
                    if (network is null) { Console.Error.WriteLine($"error: {modelError}"); return 2; }
                    request = request with { DepthRefine = network };
                    planNotes.Add("--depth-refine: the depth is refined with a depth network for the outputs that read it; captures with saved depth edits keep those");
                }
            }
            // Threads inside each conversion: the whole machine for one input at a time, shared out when several run at once.
            int renderThreads = o.RenderThreads ?? Math.Max(1, Environment.ProcessorCount / Math.Max(1, Math.Min(o.Threads, total)));
            Lux.Engine.Pipeline.BayerFusion.BlockFlow.Threads = renderThreads;   // the block-flow searches of the fusion set-up (process-wide)
            Console.WriteLine($"lux-light convert: {total} file(s), {Math.Min(o.Threads, total)} at once, {renderThreads} render thread(s) each, Lumen export path"
                              + (o.Level != 0 ? $", level {o.Level}" : "") + (o.Size is { } s ? $", size {s.W}x{s.H}" : "")
                              + $", formats {plan.FormatsLabel}"
                              + $" -> {o.OutFile ?? o.OutDirectory ?? "<beside input>"}");
            foreach (var note in planNotes) Console.WriteLine($"  {note}");
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
                    if (req.Stereo is { } stereo) req = req with { Stereo = stereo with { HeldTurn = ConvertCmd.HeldTurnOf(path) } };
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

        /// <summary>Compute Lumen's color profile (CCT ordering, ΔE2000 matrix fit, ForwardMatrix, HueSatMap)
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

        /// <summary>Run the module ISP on a center ROI of the reference module at the requested level and write a
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
                lux-light: a CLI that converts Light L16 .lri captures into a variety of formats

                USAGE:
                  lux-light <command> [options] [input...]
                  lux-light --help

                COMMANDS:
                  convert <input...>          Process a .lri and export it as Lumen does. The image pipeline runs once,
                                              and convert writes the result in any combination of export formats (see
                                              below). All values come from the .lri. Only the options marked * change
                                              Lumen's output. --level, --size and --origin only select the output grid
                  inspect <input...>          Print the state that Lux derives when it loads a capture (neutral, ev,
                                              multiplier)
                  mod-info <input...>         Print the CameraID, gain, exposure, frame black and relative_brightness of
                                              each module
                  profile <input...>          Compute and print the color profile (matrix fit, ForwardMatrix,
                                              HueSatMap)
                  isp <input...>              Print the module-ISP tuning per config level, the stage list and the
                                              white-balance state
                  isp-run <input...>          Run the module ISP on a center region and write a gamma-encoded PPM
                  devices                     List the connected MTP cameras
                  pull [options]              Copy matching files from the camera
                  version [--offline]         Print the version of this build and check GitHub for a newer release
                  edits-info <input...>       Show the saved depth edits of a capture: the variants, the active variant,
                                              the amount of stored data, and whether all stored data is intact.
                                              --set-active <variant|none> selects the variant that `convert` uses
                  import-lumen-edits <lris file or folder...>
                                              Import Lumen session files (.lris) into Lux. An import contains what Lumen
                                              saved for the photo: the depth map and depth edits, the depth effect, the
                                              tone and color settings, the crop / rotation and the rating. convert then
                                              renders the photo as Lumen does. The capture of each session (the .lri of
                                              the same name) must be beside it
                  animate <input...> --anim-effect <effect>
                                              Make an animation of each capture, as the desktop app does. The effects
                                              are:
                                                rack-focus and aperture-sweep: Lumen's depth effect. Each frame is
                                                exactly the still that convert writes with --f-number and --focus-depth.
                                                wigglegram: the pictures of the wide lenses.
                                                parallax: a small camera move made from the photo and its depth.
                                                lens-tour: each lens in turn.
                                                zoom-through: a zoom from the whole picture onto telephoto detail.
                                                fog: depth fog that moves through the scene.
                                                pan-zoom: a push-in with a moving camera, made from the photo and its
                                                depth.
                                                slice-reveal: the colors come in slice by slice of distance.
                                                tilt-shift: a miniature look with the focus swept through the scene.
                                              Lux writes GIF, APNG and WebP itself. MP4 and WebM need ffmpeg
                  cache <input...> [--level none|geometry|13mp|full]
                                              Show the analysis cache of a capture, or change it to a level:
                                                geometry: the camera geometry and the depth map. Later exports skip that
                                                analysis, which is about half of a full-size export.
                                                13mp: also the 13 MP image and the previews.
                                                full: also the full-size image. A full-size export then runs only the
                                                output stage.
                                              The cache is in LuxImageContext/cache/ next to the capture. Exports from
                                              the cache are byte-identical to exports computed from scratch
                  depth-model status|install|remove
                                              Show, download or delete the files that refined depth needs
                                              (convert --depth-refine): the depth network and the runtime that runs it.
                                              They are not part of Lux

                INPUT:
                  convert, animate, inspect, mod-info, profile, isp, isp-run, edits-info and cache take one or more .lri
                  files, directories, or both. Directories are scanned for *.lri. import-lumen-edits takes .lris files,
                  directories, or both. devices, pull, version and depth-model take no input files.

                REQUIREMENTS:
                  - The .NET 10 runtime, for a build from source only. The release builds contain it.
                  - ffmpeg on PATH, only for AVIF (--parallax-format avif), --parallax-encoder ffmpeg, and MP4 / WebM
                    video (animate --anim-container mp4|webm). Lux writes GIF, APNG and WebP animations itself. A
                    command that needs ffmpeg checks for it before it renders anything. If ffmpeg is missing, the
                    command stops with exit status 2.

                CONVERT OPTIONS:
                  With no options, `convert` replicates Lumen's output pixel for pixel: DNG (fmt 2) + companion JPG (fmt
                  0), full size, every value processed from the .lri. The options marked * change that output. The run
                  prints which of them it applied.

                  OUTPUT
                  -o, --out-directory <dir>   Write <stem>.<ext> for each input (default: a lux_convert/ folder beside
                                              the .lri)
                      --out-file <path>       Name the output file (only when the run makes exactly one file)
                  -j, --threads <n>           Number of inputs converted in parallel (default: CPU count)
                      --render-threads <n>    Threads per input for the tile render (default: CPU count ÷ the inputs
                                              converted at once; 1 = sequential)
                      --formats <list>        Comma-separated list of formats (default dng,jpg)
                                                Lumen formats: dng, jpg, hdr, ppm, jpg+depth
                                                Lux formats: tiff16, tiff-linear, jpg-hdr, depth, lens-frames,
                                                parallax-wiggle, parallax-wiggle-interp, parallax-orbit, parallax-single,
                                                parallax-rack, parallax-dolly, parallax-dof, parallax-anaglyph,
                                                parallax-crosseye, parallax-sbs, parallax-still, stereo-sbs,
                                                stereo-anaglyph, stereo-wiggle, stereo-mpo, mesh-glb, pointcloud-ply
                                                all: every format above except hdr, ppm, tiff16, tiff-linear and jpg-hdr
                                              `depth` is the stereo depth pair in metric millimeters (<stem>_depth.f32 +
                                              <stem>_depth.jpg) on the exported grid. The Lux formats are named
                                              <stem>_<format>.<ext>. lens-frames writes <stem>_<module>.jpg. tiff16 writes
                                              <stem>.tif, tiff-linear writes <stem>_linear.tif and jpg-hdr writes
                                              <stem>_hdr.jpg

                  GRID (picks the pixel grid; leaves tone and color alone)
                      --level <n>             Export level, 0 = full size (default)
                      --size <w,h>            Explicit output size (default: the export window of the level)
                      --origin <x,y>          Origin of the export window at level 0

                  SHARED ADJUSTMENTS
                    * --rotate <90|180|270>   Bake the orientation into the pixels (Orientation stays 1). Applies to all
                                              four raster formats. A depth map written with them gets the same rotation.
                                              Not combinable with the parallax formats, because their rig geometry is in
                                              the unrotated frame
                    * --transform <m;c>       Apply a crop / turn / flip exactly as Lux and Lumen save it: the nine
                                              matrix values, ';', then the crop corners x0,y0,x1,y1 (decimal, or 0x +
                                              the 32-bit pattern of the float). The size follows Lumen's full-size rule.
                                              Applies to all four raster formats. Not combinable with --rotate or the
                                              parallax formats
                    * --upright               Turn the photo to the way the camera was held (from its accelerometer),
                                              with Lumen's own rotate. The desktop app exports this way by default.
                                              Applies only when the crop / rotation of the photo was never edited; a
                                              saved crop / rotation is always used. Without --upright, convert writes
                                              the capture as recorded, exactly as Lumen does. Applies to all four raster
                                              formats. Not combinable with --rotate, --transform or the parallax formats
                    * --fnum <f>              Exif FNumber [dng, jpg, jpg+depth]
                    * --iso <n>               Exif ISO [dng, jpg, jpg+depth]
                    * --focal <n>             Exif focal length in mm [dng, jpg, jpg+depth]

                  DEPTH EFFECT (Lumen's synthetic depth of field; dng, jpg, jpg+depth, hdr, ppm)
                    * --f-number <N>          Blur the picture as a lens of aperture f/N does, using the depth map of
                                              the capture. A smaller N gives a shallower depth of field. A value at or
                                              above the all-in-focus f-number of the capture (about f/15 for a 28 mm
                                              capture) does not change the image, exactly as in Lumen. Lumen's slider
                                              goes from f/2 to that value; convert also accepts smaller values. The Exif
                                              FNumber becomes N. This option needs the depth map, so the full
                                              registration runs even at a reduced --level
                    * --focus-depth <mm>      Distance that stays sharp, in millimeters (default: the distance the
                                              camera focused at, as Lumen chooses it). Needs --f-number
                    * --focus-point <x,y>     Focus on the object at this point of the picture, as a click with Lumen's
                                              focus tool does. x and y go from 0 to 1, and 0,0 is the top left. Needs
                                              --f-number. Not combinable with --focus-depth or --rotate
                    * --dof-rendering <r>     Renderer of the depth effect: lumen or lux (default lumen). lumen is
                                              Lumen's renderer, bit for bit. lux is the Lux rendering: clipped lights
                                              become bright discs, small lights keep their brightness, the blur discs
                                              have a sharp edge, and grain is added only where the picture is blurred

                  TONE & COLOR ADJUSTMENTS (the controls of Lumen's editor; jpg and jpg+depth only)
                    These options change how the picture is rendered, like the sliders in Lumen's editor. They affect
                    only the jpg and jpg+depth images (and the on-screen preview). A DNG, PPM or HDR keeps the image as
                    it is before this rendering, and is written exactly as without these controls. A control that you do
                    not give keeps the picture as Lumen renders it.
                    * --exposure <stops>      Brighter (+) or darker (-), -3 to 3 stops
                    * --contrast <v>          More (+) or less (-) contrast, -100 to 100
                    * --temperature <K>       White balance: color temperature in kelvin, 2000 to 20000 (default: as
                                              shot). Lower values make the picture bluer, higher values warmer
                    * --tint <v>              White balance: green (-) to magenta (+), -150 to 150 (default: as shot)
                    * --vibrance <v>          Strengthen (+) or mute (-) colors, -100 to 100. The effect is strongest
                                              on the duller colors and weakest on skin tones
                    * --saturation <v>        Strengthen (+) or weaken (-) all colors, -100 (black and white) to 100
                    * --sharpening <v>        Sharpen (+) or soften (-), -100 to 100 (default 5, Lumen's own setting). A
                                              larger value never sharpens less, and a negative value softens at every
                                              --level. The result is Lumen's for 0 to 20 at every level, for 20 to 100
                                              at levels 0 and 1, and for negative values at full size. Outside those
                                              ranges Lumen's own response fails (see --jpeg-sharpening) and Lux uses its
                                              own. Give this or --jpeg-sharpening, not both
                    * --shadows <v>           Lift (+) or deepen (-) the shadows, -100 to 100
                    * --highlights <v>        Darken (+) or brighten (-) the brightest tones, -100 to 100. The effect is
                                              small: Lumen's curve acts mainly on values above white, which the JPG
                                              clips. At +100 a tone at white becomes about 2 % darker and a tone at 60 %
                                              of white does not change. Clipped areas stay clipped: this control does
                                              not recover highlights
                    * --clarity <v>           More (+) or less (-) local contrast in the midtones, -100 to 100
                    * --highlight-recovery <v>
                                              Bring back highlights that Lumen's tone curve clips, 0 to 100 (default 0 =
                                              Lumen's rendering). The picture holds about one stop of tones above
                                              Lumen's white. A larger value moves the white point up: the input that
                                              reaches white is 2^(v/100) times Lumen's, one stop more at 100. Tones
                                              below 80 % of white do not change. The brightest tones lose color toward
                                              white. Areas that the sensors clipped stay white
                    * --gamut-compress        Keep colors outside the output color space at its boundary, with their hue
                                              and gradation. Without this option each channel clips, as in Lumen. Very
                                              saturated colors inside the color space become slightly less saturated.
                                              Other colors do not change
                    --shadows, --highlights and --clarity are Lux controls. The processing is part of Lumen's software,
                    but Lumen never offered it as controls.
                    --highlight-recovery and --gamut-compress are Lux processing that Lumen does not have.

                  NOISE (Lux controls of the per-lens processing; dng, jpg, jpg+depth, hdr, ppm)
                    Lumen denoises each lens image before it merges the lenses. Then it adds a blurred copy of the
                    removed luminance noise to the image again, as grain. These two controls change that step, so they
                    change the merged image and every format written from it. A control that you do not give keeps
                    Lumen's value. With neither control, the output is Lumen's, pixel for pixel. With one of them,
                    convert does not read the image tiles kept in the analysis cache, because those tiles are Lumen's.
                    It still uses the kept geometry and depth map. These controls do not apply to lens-frames or
                    parallax-wiggle.
                    * --grain-strength <v>    Scale of the grain added after the denoiser, 0 to 4: 1 = Lumen's amount
                                              (default), 0 = no grain
                    * --denoise-threshold <v> Scale of the denoiser's threshold, 0.25 to 4: 1 = Lumen's (default). A
                                              larger value removes more noise and more fine detail. A smaller value
                                              removes less

                  SAVED DEPTH EDITS (the LuxImageContext folder next to the captures)
                    A capture with an edited depth map keeps those edits in LuxImageContext/edits/. The edits come from
                    Lux, or from Lumen through `import-lumen-edits`. convert reads the depth for --f-number, jpg+depth,
                    depth and the depth-based parallax formats. In each of these cases, it uses the edited depth of the
                    capture's active variant and says so in its output. A variant imported from Lumen also has the
                    photo's settings (depth effect, tone and color, crop / rotation). convert then renders the photo as
                    Lumen does after it reopens the session. Each option that you give replaces only its own setting.
                    Captures without saved edits are not affected.
                    * --no-edits              Ignore saved depth edits and saved settings: use the depth computed from
                                              the capture
                    * --edits <variant>       Use this variant instead of the active one (`edits-info` lists them)

                  REFINED DEPTH (needs the depth model; see the depth-model command)
                    The camera's depth map has real distances, but its steps sit beside the outlines of objects, it
                    loses thin objects, it fills the gaps between bars and it scatters the sky. A depth network sees
                    these things in the picture, but it gives no real distances. Refined depth joins the two: it keeps
                    the camera's depth, except in the sky and where the shape of the camera's depth contradicts the
                    network. The other cameras with the same view check the camera's depth first: depth that they
                    confirm is kept. A telephoto capture has no such cameras, and gets only its sky corrected. The
                    outputs that read the depth then use the refined depth: --f-number, jpg+depth, depth and the
                    depth-based parallax formats. The picture itself is not changed. Without this option nothing
                    changes.
                    * --depth-refine          Refine the depth map with the depth network. This adds about 20 to 50
                                              seconds per capture. A capture with saved depth edits keeps those: add
                                              --no-edits to refine the camera's depth instead. The result can differ in
                                              the last digits between computers, because the network runs on each
                                              computer's own processor. convert stops when the depth model is not
                                              installed: it never downloads it by itself

                  TELEPHOTO CHECK
                    Lux registers each telephoto module to the wide picture as Lumen does. In some captures, mostly
                    near, dark or stacked scenes, a module stays several pixels off. The merged picture then has no
                    telephoto detail in the area of that module. The telephoto check measures each module against the
                    reference picture after the registration. It corrects a module with a small rotation, but only
                    when the measurement is clear. The full-size picture changes where a module was off. It can also
                    change slightly where the check moves a module that was almost aligned. Without this option
                    nothing changes.
                    * --tele-check            Check the alignment of the telephoto modules and correct each module
                                              that is off by 4 pixels or more. This adds 1 to 3 seconds per capture.
                                              The run says what it did for each module. The check does not help when
                                              the depth of a near scene is wrong, or when a module shows too little
                                              texture. The analysis cache keeps the checked geometry apart from the
                                              default one, and convert does not read kept image tiles for this run

                  ANALYSIS CACHE (LuxImageContext/cache next to the captures; see the cache command)
                    convert uses what a capture's analysis cache holds and skips the work that would produce it. The
                    output is byte-identical with and without the cache. When the capture already has a LuxImageContext
                    folder, convert also keeps the camera geometry and depth map that it computed in that folder. The
                    next export of that capture then skips that analysis. convert never creates a LuxImageContext folder
                    by itself.
                      --no-cache              Do not read or write the analysis cache
                      --memory-budget <GB>    Keep a full-size render within about this much memory (default: no limit,
                                              the fastest). The output is the same and the run takes more time; see the
                                              README

                  DNG
                    * --dng-cs <n>            Color space setting of Lumen's export, for the Exif ColorSpace tag
                                              (default 0 = none, Lumen's value for a DNG)
                    * --dng-tone <profile>    Tone curve written to the DNG: acr | light_v1 | light_v1_lowlight |
                                              light_v2
                    * --dng-comp <0|1>        0 = uncompressed, 1 = lossless JPEG (default)
                    * --dng-highlights <clip|keep>
                                              keep = write the samples at half their value and BaselineExposure one stop
                                              higher, so that a raw developer does not clip the tones up to one stop
                                              above white. A developer that reads BaselineExposure shows the same
                                              brightness. A developer that ignores it shows the picture one stop darker.
                                              clip = Lumen's file (default)

                  JPEG   (each of these also applies to jpg+depth)
                    * --jpeg-cs <n>           Color space setting of Lumen's export, for the Exif ColorSpace tag
                                              (default 4 = srgb)
                    * --jpeg-quality <n>      JPEG quality on the libjpeg scale (default 98)
                    * --jpeg-sub <0|1|2>      Chroma subsampling (default 2 = 4:2:0)
                    * --jpeg-v2               Use Lumen's second tone-mapping variant (light_v2)
                    * --jpeg-sharpening <v>   Sharpening amount of Lumen's JPG renderer, -100 to 100, with Lumen's
                                              response at every value (default 5, the value in Lumen's exports; 0 = no
                                              sharpening). Lumen scales its filter radii with the image. At --level 2 a
                                              value above 30 sharpens less than a smaller value. At levels 3 and 4 no
                                              value sharpens. Negative values soften less at each smaller level.
                                              --sharpening is the same setting without these faults
                    * --jpeg-modify <ts>      Exif ModifyDate, YYYY-MM-DDTHH:MM:SS (default: now)
                    * --jpeg-comment <s>      Text of the JPEG COM marker
                    * --jpeg-software <s>     Exif Software string

                  HDR
                    * --hdr-cs <n>            Color space setting of Lumen's export (default 1 = linear_srgb). The .hdr
                                              file does not depend on it

                  TIFF
                      tiff16 is the picture of the JPG with 16 bits per channel. tiff-linear is the picture before the tone
                      curve, as floating-point values in linear ProPhoto RGB. In tiff-linear a value of 1 is the level that
                      the JPG shows as white. Brighter values are kept, so an editor can bring back highlights that the JPG
                      clips. Both files are classic TIFF with an ICC profile and Exif data. Both come from the render of the
                      JPG: the tone and color adjustments, the depth effect, the noise controls, --rotate, --transform,
                      --upright, --jpeg-v2 and --jpeg-sharpening apply to them as they apply to the JPG.
                      --tiff-cs <name>        Color space of tiff16: srgb (default), p3 (Display P3) or adobergb (Adobe RGB)
                      --tiff-compression <c>  deflate (default), lzw or none. The bytes of a Deflate-compressed file can
                                              differ between platforms. The pixels are the same
                      --tiff-depth <d>        Sample type of tiff-linear: f32 (32-bit floating point, default) or f16
                                              (16-bit floating point)

                  JPG-HDR
                      jpg-hdr is the JPG with a gain map. An HDR display shows brighter highlights. Other displays and
                      programs show the JPG. The compressed picture data of the JPG is the same, byte for byte. The file
                      follows the Ultra HDR Image Format v1.1 and ISO 21496-1 and has an sRGB ICC profile. Each option that
                      applies to the JPG also applies to jpg-hdr, except --jpeg-cs.
                      --gainmap <kind>        rgb (default): one gain for each color channel. luminance: one gain for the
                                              brightness, which gives a smaller file and less accurate highlight colors
                      --gainmap-scale <n>     The gain map has 1/n of the width and height of the picture: 4 (default), 2
                                              or 1
                      --gainmap-quality <n>   JPEG quality of the gain map, 1-100 (default 90)
                      --hdr-max-size <px>     Scale the picture down so that neither side is larger than this (default: no
                                              limit). Some decoders refuse a gain-map JPEG with a side above 8192 px, and
                                              a full-size export is up to 10432 px wide

                  PPM
                      (no options)

                  DEPTH
                      (no options)

                  LENS-FRAMES
                      This format writes each module of the capture as a display JPG through the ported module ISP. A
                      stacked capture has several frames per module. `all` writes every frame as
                      <stem>_f<k>_<module>.jpg. An index picks one frame. The default is frame 0, the frame that Lumen's
                      StackFusion uses as its reference. Every module is brought to the exposure of A1: its exposure
                      time and its analog gain.
                      --lens-quality <n>      JPEG quality 1-100 (default 92)
                      --lens-ev <float>       Exposure adjustment in stops (default 0.95)
                      --lens-level <n>        Module-ISP config level (default 0 = full resolution, no denoise)
                      --lens-profile <p>      Lumen's renderer profile, 0-3 (default 3 = Desktop). It selects the
                                              module-ISP kernels
                      --lens-modules <list>   Comma-separated module filter, e.g. A1,B4
                      --lens-stack <all|n>    Frame of a stacked capture to write (default 0)

                  STEREO
                      A stereo pair from the two wide lenses A5 and A1. They sit 44 mm apart, close to the distance
                      between two eyes, so each eye gets the picture of a real lens: no depth map is involved. The two
                      pictures are aligned with the capture's calibration, matched in color, shifted so that the scene
                      sits behind the screen, and cropped to their common area. About 10 % of the frame is lost. The
                      lens pair needs a wide capture, held level, whose depth spans at most 3 % of the picture width:
                      with a far background, a subject that is not nearer than about 1.2 m. For every other capture,
                      convert makes the two eyes from the merged picture and the depth map, as parallax-sbs does, and
                      says so in its output. The files are named <stem>_stereo-sbs.jpg, <stem>_stereo-anaglyph.jpg,
                      <stem>_stereo-wiggle.<ext> and <stem>_stereo.mpo.

                      stereo-sbs              The two eyes side by side, the left eye on the left, JPG
                      stereo-anaglyph         One picture for red-cyan glasses (Dubois), JPG
                      stereo-wiggle           An animation of two frames that switches between the eyes
                      stereo-mpo              MPO file: the two JPG pictures with a multi-picture index, for 3D cameras,
                                              televisions and viewers. Other programs show the left eye
                      --stereo-source <s>     Where the two eyes come from: auto (default) | real | depth. real writes
                                              the lens pair only: also for a scene that is deeper than is comfortable,
                                              and no file when the capture has no lens pair. depth always uses the depth
                                              map
                      --stereo-layout <l>     Width of each eye in stereo-sbs: full (default) | half. half squeezes each
                                              eye to half its width, for 3D televisions
                      --stereo-size <px>      Long edge of one eye in pixels (default: the size of the pair; 1040 for
                                              stereo-wiggle)
                      --stereo-format <c>     Container of stereo-wiggle: gif (default) | webp | apng

                  3D   (one capture gives a relief)
                      3D files from the picture and the depth map of one capture. One capture sees the scene from one
                      place, so the result is a relief, not a full 3D scene: it looks right inside a small viewing
                      angle, about 0.5 to 3 degrees, and shows holes and stretched edges outside it. Both formats use
                      the depth as exported, on the 13 MP picture or on a smaller --level. --depth-refine is
                      recommended: the refined depth gives cleaner outlines and fewer terraces. The files are named
                      <stem>_3d.glb and <stem>_cloud.ply. Positions are in meters.

                      mesh-glb                A 3D photo: a textured mesh in a binary glTF file, for 3D viewers, game
                                              engines and the web. One vertex for each 4 pixels, no triangle across a
                                              step in the depth, then simplified. The texture is the picture
                      pointcloud-ply          A colored point cloud in a binary PLY file (position, normal, color), with
                                              x to the right, y down and z forward from the camera. Points at depth
                                              steps, beyond 30 m and in the sky are left out
                      --mesh-triangles <n>    Number of triangles of mesh-glb after the simplification (default 250000)
                      --mesh-depth <d>        Depth of mesh-glb: relief (default) | metric. relief caps the depth at 4
                                              times the distance of the subject, so that a far background stays close
                                              behind the subject. metric keeps the real distances and leaves out content
                                              beyond 60 m
                      --pc-step <n>           Every n-th pixel becomes a point of pointcloud-ply: 1 | 2 | 4 (default) |
                                              8

                  PARALLAX   (experimental)
                      The base imagery is the pipeline's module-ISP frames, or this run's JPEG render and its metric
                      depth. Only the 28 mm A array is a multi-view rig. A telephoto capture has no A modules, so
                      parallax-wiggle refuses it and the depth formats run single-view.

                      parallax-wiggle         A-group frames, color-matched, swept in spatial order. Module renders
                                              only, no registration
                      parallax-wiggle-interp  N virtual viewpoints along the rig's axis, synthesized from the depth.
                                              Disocclusions are filled from the other real modules
                      parallax-orbit          The same synthesis on a closed circular path
                      parallax-single         Single-view 2.5D: the sweep with no multi-view fill
                      parallax-rack           Animated rack focus with the experimental layered blur (`animate
                                              --anim-effect rack-focus` uses Lumen's depth effect)
                      parallax-dolly          Dolly zoom: the camera pulls back while it zooms in
                      parallax-dof            Synthetic depth of field, one PNG still
                      parallax-anaglyph       Red/cyan (Dubois) anaglyph of a synthesized stereo pair, PNG
                      parallax-crosseye       Cross-eye side-by-side stereo pair, PNG
                      parallax-sbs            Parallel-view side-by-side stereo pair, PNG
                                              The two eyes of these three formats are offset along the viewer's
                                              horizontal. Thus, for a photo taken with the camera held turned (from its
                                              accelerometer), the pair is written upright
                      parallax-still          One synthesized viewpoint, PNG
                      Every format except parallax-wiggle forces the level-0 build. The run announces this, as it does
                      for jpg+depth. These formats synthesize from this run's JPEG render at --level, downscaled to
                      --parallax-size. For example, --level 2 renders the base at 2608 px. That is sufficient for a 1600
                      px animation and much faster than full size.
                      --parallax-format <c>   Animation container: gif (default) | webp | avif | apng. Lux writes GIF,
                                              APNG and WebP itself. AVIF goes through ffmpeg. WebP is lossless by
                                              default (every pixel exact), so its files are several times larger than
                                              lossy WebP (46 frames at 800 px: 14.7 MB). --parallax-encoder ffmpeg
                                              writes lossy WebP (quality: --parallax-quality)
                      --parallax-encoder <e>  Encoder: lux (default) | ffmpeg. With ffmpeg, every container goes through
                                              ffmpeg (GIF with one palette and Bayer dithering, lossy WebP)
                      --parallax-size <n>     Long edge of the working image in px (default 1600; 0 = native, for
                                              parallax-wiggle)
                      --parallax-ms <n>       Duration of each frame in ms (default 100 for parallax-wiggle, 70 for
                                              -rack and -dolly, 60 otherwise)
                      --parallax-frames <n>   Number of virtual viewpoints / animation frames (default 24)
                      --parallax-loop <k>     Loop mode: pingpong (default) | forward. The orbit is closed and plays
                                              forward
                      --parallax-fill <k>     Hole filling: donors (default: disocclusions from the other real A
                                              modules, then inpaint) | inpaint | none (holes stay black, to show where
                                              they are)
                      --parallax-path <k>     Camera path for parallax-wiggle-interp and -single: sweep (default: along
                                              the rig's dominant axis, 12 deg off horizontal from the factory
                                              extrinsics) | arc | line (along the viewer's horizontal, also when the
                                              camera was held turned)
                      --parallax-baseline <mm>
                                              Peak-to-peak extent of the path (default 71.49, the widest physical color
                                              baseline A4-A5; usable to about twice that)
                      --parallax-converge <k> Convergence plane: auto (default, the median depth) | none | meters.
                                              Everything at that depth stays fixed and the scene swings around it
                      --parallax-converge-at <x,y>
                                              Read the convergence depth from the depth map at that pixel of the working
                                              image instead
                      --parallax-ipd <mm>     Stereo interocular distance (default 25 for the anaglyph, 63 for the
                                              side-by-side pairs; 63 with a near foreground is hard to view)
                      --parallax-anaglyph <k> Anaglyph method: dubois (default, least-squares de-ghosting) | color |
                                              gray. The spellings colour and grey are also accepted
                      --parallax-focus <m>    Focus distance of parallax-dof in meters (default: the 10th-percentile
                                              depth)
                      --parallax-focus-at <x,y>
                                              Read the focus depth from the depth map at that pixel instead
                      --parallax-aperture <mm>
                                              Aperture diameter for -dof and -rack (default 20 = f/1.4 on the A group's
                                              28 mm equivalent)
                      --parallax-layers <n>   Number of depth layers in the composite (default 8)
                      --parallax-rack <m1,m2> Rack the focus from m1 to m2 meters (default: the 10th- to the
                                              90th-percentile depth)
                      --parallax-rack-at <x1,y1;x2,y2>
                                              Rack between the depths at two pixels instead
                      --parallax-subject <m>  Depth that parallax-dolly holds constant, in meters (default: the 10th
                                              percentile)
                      --parallax-subject-at <x,y>
                                              Read that depth from the depth map at that pixel instead
                      --parallax-dz <mm>      Dolly travel (default 400). A positive value pulls back and zooms in, the
                                              one direction that a single capture supports. A negative value is clamped
                      --parallax-t <tx,ty>    Translation of the virtual camera for parallax-still, in mm (default 40,0)
                      --parallax-quality <n>  Quality of ffmpeg's lossy WebP (default 88) [--parallax-format webp
                                              --parallax-encoder ffmpeg]
                      --parallax-crf <n>      AVIF encoder crf (default 18) [--parallax-format avif]
                      --parallax-order <k>    Frame order of parallax-wiggle: sweep (default, along the rig's dominant
                                              axis from the factory extrinsics) | label (module-name order) | an
                                              explicit list, e.g. A5,A1,A3,A4
                      --parallax-pivot <x,y,w,h>
                                              Hold a region of parallax-wiggle still so that the scene swings around it.
                                              Integer shift only, no resampling. Native sensor pixels

                ANIMATE OPTIONS:
                      --anim-effect <e>       Effect: rack-focus | aperture-sweep | wigglegram | parallax | lens-tour |
                                              zoom-through | fog | pan-zoom | slice-reveal | tilt-shift (required)
                      --anim-container <c>    Container: gif (default) | apng | webp (lossless) | mp4 | webm. mp4 and
                                              webm need ffmpeg. zoom-through, fog, pan-zoom, slice-reveal and tilt-shift
                                              are long clips: their default is mp4 when ffmpeg is installed
                  -o, --out-directory <dir>   Write <stem>_<effect>.<ext> (default: a lux_animate/ folder beside the
                                              .lri)
                      --anim-size <px>        Long edge of the frames (default 1040). The frames come from the smallest
                                              export level that is at least that size
                      --level <n>             Render from this export level instead
                      --anim-frames <n>       Number of distinct frames (default 24; zoom-through, fog, slice-reveal and
                                              tilt-shift 100; pan-zoom 125). The wigglegram and the lens tour have one
                                              frame per lens
                      --anim-ms <n>           Time per frame in ms (default 70; wigglegram 100, parallax 60, lens tour
                                              500; zoom-through, fog, pan-zoom, slice-reveal and tilt-shift 40)
                      --anim-loop <k>         Loop mode: pingpong (default) | forward. The default for the lens tour,
                                              the orbit, zoom-through, fog, pan-zoom and slice-reveal is forward
                      --anim-hold <ms>        Time the last frame stays in the forward loop mode (default 1000 for zoom-
                                              through and slice-reveal, else 0)
                      --anim-repeat <n>       Number of times the animation plays (default 0 = forever)
                      --f-number <N>          Aperture of rack-focus, or the widest aperture of aperture-sweep (default
                                              2, the widest that Lumen's slider offers). Aperture of tilt-shift
                                              (default: from the depth range of the scene, about 0.03 for a street and
                                              0.1 for a subject at 3 m)
                      --anim-f-from <N>       Start aperture of aperture-sweep (default: all in focus)
                      --anim-focus <m1,m2>    Pull the focus of rack-focus from m1 to m2 meters (default: the 10th to
                                              the 90th percentile of the scene's depth)
                      --focus-depth <mm>      Focus distance of aperture-sweep (default: as the camera focused)
                      --anim-path <k>         Camera path of parallax: sweep (default) | arc | line | orbit
                      --anim-baseline <mm>    Travel of the virtual camera for parallax (default 71.49)
                      --anim-converge <k>     Distance that stays still in parallax: auto (median depth) | meters
                      --anim-single-view      Make parallax from the main view only (gaps are painted in)
                      --anim-zoom-at <x,y>    Point that zoom-through ends on, each from 0 to 1 of the picture (default:
                                              where the telephoto lenses add the most detail). zoom-through stops with a
                                              reason when the photo has no such place: low light, a subject nearer than
                                              1 m, or no telephoto detail. With a point given it zooms anyway, and ends
                                              at half the full size
                      --anim-fog <k>          Movement of fog: bank (default, a bank of fog comes forward from the far
                                              part of the scene) | rise (fog thickens and thins again)
                      --anim-fog-density <k>  Factor on the fog density (default 1)
                      --anim-fog-color <c>    Fog color: auto (default, from the far part of the scene) | rrggbb
                      --anim-pan-at <x,y>     Point that pan-zoom pushes in on, each from 0 to 1 of the picture
                                              (default: where the camera focused)
                      --anim-travel <k>       Camera travel of pan-zoom, as a fraction of the nearest distance of the
                                              scene: auto (default) | a fraction. pan-zoom first checks how far the
                                              camera can travel before the picture tears, and reports that limit. auto
                                              stays inside it. When even the smallest travel fails, auto makes a flat
                                              move
                      --anim-flat             Make pan-zoom a flat move: nothing in the scene slides
                      --anim-no-smoothing     Make pan-zoom, fog and slice-reveal from the depth map as it is. By
                                              default they smooth the depth along the picture first: far surfaces then
                                              stay in one piece when the camera moves, and fog lies evenly on them
                      --anim-slice <k>        Order of slice-reveal: near-far (default) | far-near | from-focus
                      --anim-softness <k>     Softness of the slice edge of slice-reveal, as a share of the depth range
                                              of the scene (default 0.08; 0 = a hard edge)
                      --anim-tilt <k>         Tilt of the focal plane of tilt-shift (default 0.8: the focus lies farther
                                              away towards the bottom of the picture; 0 = no tilt)
                      --depth-refine          As for convert: refine the depth map with the depth network before the
                                              frames are made. The effects that read the depth then use the refined
                                              depth
                      --tele-check            As for convert: check the alignment of the telephoto modules before the
                                              frames are made
                      --anim-gif-dither <k>   GIF dithering: ordered (default) | diffusion | none
                      --anim-gif-palette <k>  GIF palette: global (default: one palette, no flicker) | frame
                      --anim-no-diff          Store every frame whole (by default only the changes are stored)
                      --anim-keep-frames <d>  Also write each distinct frame as an exact PNG, the plan (with the convert
                                              options of each depth-effect frame) and the frames that a viewer shows
                      --no-edits, --edits <v> As for convert
                      --upright               As for convert: a photo held turned comes out upright (the default of the
                                              desktop app). The parallax, wigglegram and lens-tour frames are turned the
                                              same way

                INSPECT OPTIONS:
                      (none: inspect takes only input paths)

                MOD-INFO OPTIONS:
                      (none: mod-info takes only input paths)

                PROFILE OPTIONS:
                      (none: profile takes only input paths)

                ISP OPTIONS:
                      --isp-level <n>         Module-ISP config level (default 0; -1 prints every level 0-5)
                      --profile <p>           Lumen's renderer profile, 0-3 (default 3 = Desktop). It selects the
                                              module-ISP kernels

                ISP-RUN OPTIONS:
                      --isp-level <n>         Module-ISP config level (default 0)
                      --profile <p>           Lumen's renderer profile, 0-3 (default 3 = Desktop). It selects the
                                              module-ISP kernels
                      (the PPM is written beside the .lri as <stem>_isp_l<n>.ppm)

                DEVICES OPTIONS:
                      (none)

                PULL OPTIONS:
                  -o, --out-directory <dir>   Destination folder (default: ./lux_pull)
                      --ext <list>            Extensions to include (default .lri)
                      --glob <pattern>        Filename glob, e.g. "L16_004*"
                      --since <date>          Only files modified on or after this date
                      --overwrite             Download again even if a file of the same size exists locally
                      --list                  List the matching files without downloading

                VERSION OPTIONS:
                      --offline               Print the version only, without asking GitHub
                  Exit status: 0 up to date (or no release published yet), 1 a newer release is available, 2 the check
                  could not be made (no network, GitHub error). The release page is
                  https://github.com/mprovenz/lux/releases

                DEPTH-MODEL:
                  lux-light depth-model status|install|remove manages the files of refined depth. install downloads
                  them once (about 105 to 110 MB) into the folder models/depth of the app's data folder, and checks
                  each file against a fixed SHA-256. remove deletes them. The desktop app uses the same files
                  (Settings, Refined depth).
                  - The depth network is Depth Anything V2 Small (Apache-2.0), as an ONNX file from the
                    onnx-community/depth-anything-v2-small repository on Hugging Face.
                  - The runtime is ONNX Runtime 1.23.2 (MIT). There are builds for Linux, Windows and macOS on x64 and
                    arm64 processors. On another computer the command says that refined depth is not available.
                    install downloads the one library of your computer, 5 to 12 MB, from the release files of the Lux
                    project (github.com/mprovenz/lux-depth-support). These files hold the unchanged libraries of the
                    Microsoft.ML.OnnxRuntime 1.23.2 package. When that download fails, install takes the same library
                    from the package on nuget.org. An error names the servers that were tried.
                  - A download sends the file's address, the byte range and a generic client name, nothing else.
                  - LUX_DEPTH_MODEL_SOURCE=<folder or URL> makes install read the files from there instead: the model
                    under the name depth-anything-v2-small.onnx, and the runtime under one of the names
                    onnxruntime-1.23.2-<platform>.zip (the Lux project's file; <platform> is linux-x64, linux-arm64,
                    win-x64, win-arm64, osx-x64 or osx-arm64) and microsoft.ml.onnxruntime.1.23.2.nupkg (the package).
                    A folder can also hold the ONNX Runtime release archive of your computer,
                    onnxruntime-<platform>-1.23.2.tgz (.zip on Windows). The files must be the same: the SHA-256
                    check stays.
                  Exit status: 0 done (status: installed), 1 status: not installed, 2 an error.

                GLOBAL OPTIONS:
                  -h, --help                  Show this help

                ENVIRONMENT VARIABLES:
                  * For diagnostic / development use only

                  LOGGING
                      LUX_VERBOSE=1           Print per-file progress detail in convert, and the full exception trace
                                              when a file fails (not only its message)
                      LUX_NO_PROGRESS=1       Do not draw in-place progress lines in convert. They are also off when
                                              stderr is not a terminal or LUX_VERBOSE=1 is set
                      LUX_PROGRESS_TRACE=1    Print every progress update of convert as a line (phase, done/total), to
                                              debug the phase flow

                  BEHAVIOR: these change what the pipeline computes
                      LUX_CC_FM=<fm1;fm2>     Replace the fitted ForwardMatrix1/ForwardMatrix2 in color correction with
                                              two space-separated 9-float matrices
                      LUX_CC_M=<m>            Replace the camera→working matrix (9 floats, or 0x-prefixed bit patterns).
                                              This bypasses the fit and the CCT interpolation
                      LUX_CNR_NEUTRAL=r,g,b   Color-noise reduction uses this neutral instead of the neutral from the
                                              ISP stats
                      LUX_CNR_NOEXT=1         Color-noise reduction reads no pixels outside its own region (drops the
                                              ±2-pixel ring)
                      LUX_FRAME_BLACK=<f>     Linearize uses this black level instead of the level from the stats or the
                                              frame
                      LUX_FUSION_SRC_BLACK=db|estimate
                                              Black level for the non-reference source frames of the fusion: db = the
                                              sensor database value, estimate = the per-frame estimate
                      LUX_GUIDE_SKIP=<list>   Set these comma-separated stages to type "none" in the reference-guide ISP
                                              tuning
                      LUX_JPEG_ROUND=rne      The JPEG export rounds half to even, as the display path does, instead of
                                              half away from zero, as the export path does
                      LUX_LU_RANK=rank        The Ceres line search's FullPivLU uses the Eigen 3.3 rank threshold
                                              instead of the Eigen 3.2 nonzero-pivot rule that Lumen's ForwardMatrix fit
                                              follows
                      LUX_NO_GUIDE=1          Registration does not build the reference guide image
                      LUX_NO_MONO=1           Force the color fusion branch on a capture that has a monochrome module
                      LUX_SGM_XSW=1           The SGM cost sweep clamps its forward x start to W instead of W-1
                      LUX_SKIP_MISSING=1      Skip an unimplemented stage with a warning instead of an exception
                      LUX_SKIP_STAGE=<list>   These comma-separated stages keep their padding and alignment but never
                                              run
                      LUX_STACK_MARGIN=<n>    ISP tile margin for the reference fusion of a stacked capture, instead of
                                              the halo that the sensor gain selects
                      LUX_STEREO_DEMOSAIC=<t> Demosaicking type of the stereo ISP tuning (default collapse2)
                      LUX_STEREO_SKIP=<list>  Set these comma-separated stages to type "none" in the stereo ISP tuning
                      LUX_STEREO_TILE=<n>     Tile size of the stereo ISP work image (default 256, Lumen's)
                      LUX_TELE_CFG=<n>        ISP config level of the telephoto module (default 1, Lumen's)
                      LUX_TELE_ONLYRECT=x0,y0,x1,y1
                                              Run the telephoto ISP only for that one grown rect. Every other rect comes
                                              back zeroed
                      LUX_TELE_SET=k=v;k=v    Apply these tuning overrides to the ISP tuning of the telephoto module
                      LUX_WARP_ORDER=cols|rows|swap|prod
                                              Accumulation order of the 6-tap warp interpolation (default cols)

                  PRINTING: stderr only; the pixels are unaffected
                      LUX_BIL_DEBUG=1         The bilateral kernel window and size at each call
                      LUX_CC_DEBUG=1          The color-correction CCT interpolation inputs, with bit patterns
                      LUX_CNR_DEBUG=<any>     The color-noise-reduction per-tile statistics for the (0,0) tile
                      LUX_HP_DEBUG=x0,y0,x1,y1
                                              The per-pixel hot-pixel decisions inside that rect
                      LUX_ISP_DEBUG=1         The ISP runner's stage list and per-stage rects (once), plus the linearize
                                              black/white, the crosstalk blend and the hybrid-denoise thresholds
                      LUX_PP_DEBUG=x,y        The post-processing tile arithmetic around the pixel at x,y
                      LUX_TELE_DEBUG=1        The white balance, black, IR blend and tone tuning of each telephoto
                                              module

                  WRITING FILES: each writes raw intermediate images beside the prefix you give
                      LUX_CNR_DUMP=<prefix>   Color-noise reduction: the kernel input region, its arguments and every
                                              pyramid level, as <prefix>_cnr_*.f32 / _cnr_args.txt
                      LUX_DEMOSAIC_DUMP=<dir> The A/B/C planes of the light_v1 demosaic, as A.f32/B.f32/C.f32 in <dir>
                      LUX_HP_DUMP=<prefix>    Hot-pixel removal: the per-channel sensor σ tables, as
                                              <prefix>_sigma<c>.f32
                      LUX_HYB_DUMP=<prefix>   Hybrid denoise: every intermediate, as <prefix>_hyb_<tag>.f32
                      LUX_MONO_DUMP=<prefix>  Mono fusion: the per-stage RGB and float-Bayer images of the mono module's
                                              own ISP, as <prefix>_own_st<i>_<stage>.<kind>.bin
                      LUX_TELE_ISPDUMP=<pre>  The whole grown-rect ISP output of the telephoto level-0 cache, as
                                              <prefix>_<module>_isp_<x0>_<y0>_<x1>_<y1>.bin. On a stacked capture also
                                              the runner's inputs, <prefix>_<module>_stkin_<rect>_{bayer,std}.bin.
                                              LUX_TELE_ISPDUMP_RECTS="x0,y0,x1,y1;..." limits it to those grown rects
                      LUX_REF_ISPDUMP=<pre>   Stacked capture: the level ISP output and input of the reference cache's
                                              stacked branch, as <prefix>_ref_L<level>_src_<rect>_{out,bayer}.bin.
                                              LUX_REF_ISPDUMP_RECTS limits it to those source rects
                      LUX_DENSE_DUMP=<pre>    Dense stereo (level-0 builds): the six pyramid depths, the full depth, the
                                              layer-0 inputs and (layers <= LUX_DENSE_DUMP_CV, default 2) the cost
                                              volumes, as raw dumps <prefix>_<stem>_dense_L<i>_* (int32 {w,h,stride,bpp}
                                              header + rows; cost volumes packed as in StereoAsyncApi.DumpCostVolume).
                                              Also the registration stages as raw binary records:
                                              <prefix>_<stem>_{refpts, drv<i>_match<l>, flt<i>_cam<c>_{pre,post},
                                              tri_*_points, bac<i>_{in,out}, final<i>_slots,
                                              calibwrite<i>_{before,after}, tdrv<i>_*}.bin (layouts in RegDump.cs)
                      LUX_DENSE_OVERRIDE=<f>  Read the layer-5 depth from this dump file (<out>_dense_L5_depth.f32)
                                              instead of computing the dense stereo. For tests of the dense-stereo race
                      LUX_HYB_DUMP=<pre>      Module ISP hybrid denoiser: every intermediate of every call as raw RGBA
                                              float rows, <prefix>_hyb_<tag>_<w>x<h>.f32 (the size keeps calls apart)
                      LUX_PP_DUMP=<pre>       Module ISP PostProcessing: input, companion and output of every call as
                                              raw RGBA float rows,
                                              <prefix>_pp_{src,comp,out}_er<ratio>_<x0>_<y0>_<w>x<h>.f32, plus the
                                              parameters on stderr
                      LUX_STAGE_DUMP=<pre>    Module ISP: the RGB working image after every stage as raw RGBA float
                                              rows, <prefix>_st_er<ratio>_o<x>_<y>_<index>_<stage>_<w>x<h>.f32
                      LUX_FUSION_DUMP=<pre>   Level-1 fusion: every per-source float frame
                                              (<prefix>_srcframe_cam<id>.bin), the mono sources
                                              (<prefix>_mono_src<i>.bin), the fusion tiles listed in
                                              LUX_FUSION_DUMP_TILES="x0,y0,x1,y1;..." (<prefix>_tile_<rect>_{out,w}.bin)
                                              and the render inputs of the first 8 renders, or of the grown rect(s) in
                                              LUX_FUSION_DUMP_RECT (<prefix>_render_<rect>_{bayer,std,...}.bin). All
                                              files have an int32 {w,h,stride,bpp} header + rows
                """);
            return code;
        }
    }
}
