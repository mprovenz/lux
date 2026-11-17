using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Session;

/// <summary>What a build may take from earlier work on the same capture, and what it reports back. Nothing here changes a pixel, with one
/// exception: <see cref="Noise"/>.</summary>
public sealed class BuildOptions
{
    /// <summary>The registration geometry from the analysis cache: a level-0 state then never runs the registration chain.</summary>
    public CaptureGeometry? Geometry { get; init; }
    /// <summary>Called once, on the thread that ran it, when the registration chain has produced the geometry (to keep it).</summary>
    public Action<CaptureGeometry>? GeometryComputed { get; init; }
    /// <summary>Kept PipelineCache tiles (<see cref="PipelineCache.Stored"/>).</summary>
    public ITileSource? StoredTiles { get; init; }
    /// <summary>Keep the whole registration state (`StereoAsyncApi`, with its stereo images and dense cost volumes) on the state after the
    /// geometry has been taken out of it (<see cref="ExportState.Registration"/>). False lets it go as soon as the geometry is extracted —
    /// several GB at level 0 — and <see cref="ExportSession.Registration"/> then sets up the (identical) reference-group pairs on demand.</summary>
    public bool KeepRegistration { get; init; } = true;
    /// <summary>Cancels the registration chain between its states.</summary>
    public CancellationToken Cancel { get; init; }
    /// <summary>A Lumen session render: the state Lumen has after reopening a session file (`deserialize` before the depth exists), whose
    /// registration geometry and depth come from the file (<see cref="CaptureGeometry.FromSession"/>, passed as <see cref="Geometry"/>). The
    /// registration chain then never runs, so the first decode of each reference-group source module's frame 0 is the fusion's own stream
    /// loader, which estimates its black — in a fresh render the registration decodes those
    /// frames first and they keep the sensor's database black. The telephoto modules are decoded by the tele decoder
    /// either way and keep the database black.</summary>
    public bool SessionRender { get; init; }
    /// <summary>The Lux noise controls of every module ISP of this build (<see cref="NoiseControls"/>). Null = Lumen's tuning. This is the one
    /// option here that changes pixels: a build with other values than Lumen's must not read kept tiles (<see cref="StoredTiles"/>) and
    /// its tiles must not go into the analysis cache.</summary>
    public NoiseControls? Noise { get; init; }
    /// <summary>Run the telephoto alignment check after the registration
    /// (<see cref="Lux.Engine.Pipeline.Registration.TeleCheck"/>). It can change the pose of a telephoto module, and with it the
    /// full-size picture. A build with the check must not get a <see cref="Geometry"/> that was made without it, must not read kept
    /// tiles (<see cref="StoredTiles"/>), and its tiles must not go into the analysis cache. False (the default) gives Lumen's
    /// registration.</summary>
    public bool TeleCheck { get; init; }
}

public static class ExportBuild
{
    /// <summary>Build the cache from the `.lri` and nothing else. <paramref name="maxLevel"/> = the finest pipeline level that will be
    /// rendered (0 needs the registration state). The reference calibration is the ctor pair <c>Pairs[ref]</c> and the level-0 depth is the
    /// in-process dense stereo, both out of <see cref="Lux.Engine.Pipeline.Registration.StereoAsyncApi"/>.</summary>
    /// <param name="threads">Threads for the build's own concurrent parts (the fusion sources, the telephoto caches); the result is the same at any count.</param>
    public static ExportState Build(string lriPath, int maxLevel, Action<string>? log, int threads = 1, ProgressReporter? progress = null)
        => Build(lriPath, maxLevel, log, threads, progress, null);

    /// <summary>The same with <see cref="BuildOptions"/> (cached geometry and tiles, cancellation).</summary>
    public static ExportState Build(string lriPath, int maxLevel, Action<string>? log, int threads, ProgressReporter? progress, BuildOptions? options)
    {
        var pr = progress ?? ProgressReporter.None;
        pr.Begin("loading", 1);
        var lri = LriFile.Load(lriPath);
        pr.End();
        return Build(lri, lriPath, maxLevel, log, threads, pr, options);
    }

    /// <summary>The same from an already loaded capture (<paramref name="lriPath"/> only names the diagnostic dumps).</summary>
    public static ExportState Build(LriFile lri, string lriPath, int maxLevel, Action<string>? log, int threads, ProgressReporter? progress, BuildOptions? options)
    {
        var pr = progress ?? ProgressReporter.None;
        options ??= new BuildOptions();
        var noise = options.Noise is { IsLumen: false } nc ? nc.Validate() : null;
        if (noise is not null && options.StoredTiles is not null) throw new ArgumentException("a build with noise controls must not read kept tiles", nameof(options));
        if (options.TeleCheck && options.StoredTiles is not null) throw new ArgumentException("a build with the telephoto check must not read kept tiles", nameof(options));
        if (options.TeleCheck != (options.Geometry?.TeleCheckReport is not null) && options.Geometry is not null && !options.SessionRender)
            throw new ArgumentException("the geometry of a build must be made with the same telephoto check choice as the build", nameof(options));
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;

        // ---- the registration state, on first use. Level 0 needs the whole chain (tele pairs/poses + the dense depth); levels >= 1 need only
        // the reference AlignedCalib, i.e. the ctor pair `Pairs[ref]` that `Setup()` alone produces (slots/poses/crops — no images, no
        // bundle adjustment). The chain runs when the depth or the level-0 generator is first asked for — never for a render that reads
        // levels 1–4 or stored level-0 tiles only — and not at all when the analysis cache supplied its geometry.
        Once<(CaptureGeometry Geo, Lux.Engine.Pipeline.Registration.StereoAsyncApi? Api)>? geometry = null;
        if (maxLevel == 0)
        {
            if (options.Geometry is { } known)
            {
                geometry = new((known, null));
                log?.Invoke("export: registration geometry and depth from the analysis cache");
            }
            else geometry = new(() =>
            {
                log?.Invoke("export: dense L5 depth computed in-process (self-contained)");
                var swReg = System.Diagnostics.Stopwatch.StartNew();
                // LUX_DENSE_OVERRIDE=<file>: inject a top-layer (L5) dense depth from a `_dense_L5_depth.f32` dump instead of computing it. With
                // Lumen's own depth of a given run, the DNG must equal that run's DNG.
                float[]? depthOverride = null;
                if (Environment.GetEnvironmentVariable("LUX_DENSE_OVERRIDE") is string dov)
                {
                    var (d, dw, dh) = Lux.Engine.Pipeline.Registration.StereoAsyncApi.LoadF32(dov);
                    depthOverride = d; log?.Invoke($"export: dense depth OVERRIDE from {dov} ({dw}x{dh}) — output is Lumen's schedule, not Lux's");
                    Console.Error.WriteLine($"[diagnostic] LUX_DENSE_OVERRIDE: dense L5 depth injected from {dov} ({dw}x{dh})");
                }
                string? densePrefix = Environment.GetEnvironmentVariable("LUX_DENSE_DUMP") is string ddp ? ddp + "_" + Path.GetFileNameWithoutExtension(lriPath) : null;
                var api = Lux.Engine.Pipeline.Registration.StereoAsyncApi.Run(lri, log, runHigher: true, runDense: true, depthOverride, pr, densePrefix, threads, options.Cancel, options.TeleCheck);
                log?.Invoke($"export: registration state ready in {swReg.Elapsed.TotalSeconds:F1}s");
                if (densePrefix is not null)   // the telephoto pairs (Pairs[id].view/.module) as 0xa8-byte CalibData records (K at 0, t at 0x24, R at 0x30), dumped as tdrv<i>_calib{Ref,Cam}
                    foreach (var kv in api.Pairs)
                    {
                        void W(string path, Lux.Engine.Pipeline.Geometry.CameraCalib c) { var b = new byte[0xa8]; Buffer.BlockCopy(c.K, 0, b, 0, 36); Buffer.BlockCopy(c.T, 0, b, 0x24, 12); Buffer.BlockCopy(c.R, 0, b, 0x30, 36); File.WriteAllBytes(path, b); }
                        W($"{densePrefix}_tele{kv.Key}_view.bin", kv.Value.First); W($"{densePrefix}_tele{kv.Key}_module.bin", kv.Value.Second);
                    }
                var geo = CaptureGeometry.FromRegistration(api);
                options.GeometryComputed?.Invoke(geo);
                return (geo, options.KeepRegistration ? api : null);
            });
        }
        Lux.Engine.Pipeline.Geometry.CameraCalib view, module;
        Lux.Engine.Pipeline.Registration.StereoAsyncApi? setupApi = null;
        if (options.Geometry is { } g0)
        {
            (view, module) = g0.RefPair;   // the chain's own `Pairs[ref]` (the ctor pair: Setup() writes it and no later state touches it)
            log?.Invoke($"export: reference calibration from the cached registration geometry (api+0x3a8[{refId}])");
        }
        else
        {   // the reference pair straight out of the registration state (ctor loop 2 of `StereoAsyncAPI`)
            var s = new Lux.Engine.Pipeline.Registration.StereoAsyncApi { Lri = lri, Log = log }; s.Setup(); setupApi = s;
            (view, module) = s.Pairs[refId];
            log?.Invoke($"export: reference calibration from the registration state (api+0x3a8[{refId}])");
        }
        var dist = Lux.Engine.Pipeline.Registration.StereoImageBuilder.DistortionOf(lri, lri.ReferenceModule);
        var calib = Lux.Engine.Pipeline.Geometry.AlignedCalib.Build(view, module, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);
        var frame = CapturedFrame.Load(lri, lri.ReferenceModule);
        var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
        var tunings = new Dictionary<int, Tuning>();
        Tuning Tuning(int cfg) { lock (tunings) { if (!tunings.TryGetValue(cfg, out var t)) { t = ModuleIspTuning.Build(cfg, (RendererProfile)3, frame.Info, wb.Cct, wb.Tint, noise: noise); tunings[cfg] = t; } return t; } }
        var isps = new Dictionary<int, SoftIsp>();
        SoftIsp Isp(int L) { lock (isps) { if (!isps.TryGetValue(L, out var isp)) { isp = new SoftIsp(Tuning(new[] { 0, 2, 3, 4 }[L]), colour); isp.ComputeStats(frame); isps[L] = isp; } return isp; } }
        // `setInputDataStream`'s level vector: level 0 is the pipeline canvas (`(int)(sensor · canvas factor)`, reference-group dependent —
        // 10432×7824 for an A reference, 8896×6672 for a B one), level 1 is the module frame itself and levels 2–4 halve it.
        var canvas0 = Lux.Engine.Pipeline.Export.ExportWindow.Canvas(lri, (frame.Width, frame.Height));
        var dims = new (int W, int H)[] { canvas0, (frame.Width, frame.Height), (frame.Width / 2, frame.Height / 2), (frame.Width / 4, frame.Height / 4), (frame.Width / 8, frame.Height / 8) };
        var pc = new PipelineCache(dims) { Neutral = lri.LumenNeutral, Log = log, Progress = pr, Stored = options.StoredTiles };
        // Everything below that a render of levels 0–1 builds from the capture — the stacked-frame fusions, the level-1 fusion, the reference and
        // telephoto level-0 caches, the level-0 generator — lives in one replaceable set of parts, made on first use. `ReleaseInputs` swaps in a
        // fresh, empty set: memory comes back and anything asked for again is made again, identically (every part is a pure function of the capture).
        // a session render: the reference-group source modules' frame 0 is first decoded by the stream loader (BuildOptions.SessionRender)
        IReadOnlySet<string>? sessionEstimate = options.SessionRender
            ? lri.Modules.Where(kv => kv.Key != lri.ReferenceModule && Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Group((int)kv.Value.Module.Id) == Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Group(refId)).Select(kv => kv.Key).ToHashSet()
            : null;
        var parts = new BuildParts();
        int W = frame.Width, H = frame.Height;
        var (rnx, rny) = (Math.Max(1, (256 + W) / 512), Math.Max(1, (256 + H) / 512));
        void MakeParts()
        {
            var np = new BuildParts();
            // `ReferenceImageCache::processLevel`: the stacked frame is non-null on a stacked capture, and then the
            // level runs the BayerFloat runner on `lt::StackFusion`'s fused frame with the gain map as its STD plane.
            if (lri.StackFrames >= 2)
            {
                // the stream's per-module lt::StackFusion results, shared by every consumer below: the reference cache
                // levels, the level-1 color + mono fusions, the fusion render's STD plane and the telephoto level-0 caches
                // The non-reference frames keep the sensor DB black at every build level: Lumen's `exportImage` waits for the
                // depth before it renders a tile, so the registration has always decoded those frames first (the per-frame
                // estimate is a side effect of a FIRST load through the stream loader). The reference module uses its estimate regardless.
                var st = np.Stacks = new Lux.Engine.Pipeline.BayerFusion.StackProvider(lri, wb.Cct, wb.Tint, log, sourceFrameBlackEstimate: false, lri.ReferenceModule, sessionEstimate);
                np.Stacked = new(() =>
                {
                    var sf = st.Get(lri.ReferenceModule);
                    int margin = Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Halo(frame.Info.AnalogGain);   // the ISP tile margin of the reference image cache
                    if (Environment.GetEnvironmentVariable("LUX_STACK_MARGIN") is string mo && int.TryParse(mo, out int mv)) { margin = mv; Console.Error.WriteLine($"[diagnostic] LUX_STACK_MARGIN: ISP tile margin {margin} instead of {Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Halo(frame.Info.AnalogGain)}"); }
                    log?.Invoke($"export: stacked capture ({lri.StackFrames} frames) — reference {lri.ReferenceModule} fused by lt::StackFusion, ISP margin {margin}");
                    return new Lux.Engine.Pipeline.Geometry.AlignedWarp.StackedSource(sf.BayerImage(), sf.StdImage, margin);
                });
            }
            if (maxLevel <= 1)
            {
                // The fusion reference is the capture's OWN reference module, not a hardcoded A1: `refId` is 0 for every
                // A-reference capture but 8 for the B4-reference 149 mm captures. Built when the first level-1 tile is.
                var stacks = np.Stacks;
                np.Fc = new(() =>
                {
                    var fus = new Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion(lri, refId, wb.Cct, wb.Tint, log, sourceFrameBlackEstimate: options.SessionRender, threads: threads, progress: pr, stacks: stacks);   // DB black at every level (see the StackProvider above), the estimate in a session render (BuildOptions.SessionRender)
                    var f = new Lux.Engine.Pipeline.BayerFusion.FusionCacheBayer(lri, frame, fus, (RendererProfile)3, wb.Cct, wb.Tint, log, noise);
                    f.Progress = pr;
                    return f;
                });
            }
            if (maxLevel == 0)
            {
                np.RefTiles = new TileStore<(int, int), (RectI Rect, ushort[] Half)>();   // compute-once, safe for concurrent tiles
                var refTiles = np.RefTiles; var stacked = np.Stacked; var fcache = np.Fc!; var stacks = np.Stacks;
                (RectI Rect, ushort[] Half) RefTile(int tx, int ty) => refTiles.GetOrCreate((tx, ty), key =>
                {
                    int x0 = tx * 512, y0 = ty * 512, x1 = tx == rnx - 1 ? W : x0 + 512, y1 = ty == rny - 1 ? H : y0 + 512;
                    var rect = new RectI(x0, y0, x1, y1);
                    // ReferenceImageCache::processLevel takes its stacked branch at EVERY level, level 0 included
                    var img = Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel(Isp(0), frame, calib, 0, rect, new float[4], null, stacked?.Value);
                    var hbuf = new ushort[rect.Width * rect.Height * 3];
                    for (int y = 0; y < rect.Height; y++) { var row = img.Row(y); for (int x = 0; x < rect.Width; x++) { var q = row[x]; int i = (y * rect.Width + x) * 3; hbuf[i] = Lux.Engine.Imaging.Half16.FromFloat(q.R); hbuf[i + 1] = Lux.Engine.Imaging.Half16.FromFloat(q.G); hbuf[i + 2] = Lux.Engine.Imaging.Half16.FromFloat(q.B); } }
                    log?.Invoke($"ref cache: level-0 tile ({tx},{ty}) {rect.Width}x{rect.Height}");
                    return (rect, hbuf);
                });
                np.RefTile = RefTile;
                // The level-0 generator (ImageResolutionAmp over the reference, level-1 and telephoto generators): made when the first level-0
                // tile has to be generated — after the registration geometry and the fusion it depends on.
                np.Level0 = new(() =>
                {
                    var reg = geometry!.Value.Geo;
                    _ = fcache.Value;   // the fusion set-up first, as the build always ran it before the telephoto set-up
                    if (reg.FullDepth is not { } fullDepth) throw new InvalidOperationException("the registration chain produced no full-frame depth, which the level-0 warp fields need");
                    float[] depth = fullDepth.Depth; int depthW = fullDepth.W, depthH = fullDepth.H;
                    float scale = (float)dims[0].W / (float)dims[1].W; var sc = (scale, scale);
                    var refGen = Lux.Engine.Pipeline.ResAmp.ImageGenerator.SqrtOf(W, H, r =>
                    {
                        var o = new float[r.Width * r.Height * 4];
                        int tx0 = Math.Min(r.X0 / 512, rnx - 1), tx1 = Math.Min((r.X1 - 1) / 512, rnx - 1), ty0 = Math.Min(r.Y0 / 512, rny - 1), ty1 = Math.Min((r.Y1 - 1) / 512, rny - 1);
                        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++)
                        {
                            var (rect, hb) = RefTile(tx, ty); var c = rect.Intersect(r);
                            for (int y = c.Y0; y < c.Y1; y++) for (int x = c.X0; x < c.X1; x++)
                            {
                                int si = ((y - rect.Y0) * rect.Width + (x - rect.X0)) * 3, di = ((y - r.Y0) * r.Width + (x - r.X0)) * 4;
                                o[di] = Lux.Engine.Imaging.Half16.ToFloat(hb[si]); o[di + 1] = Lux.Engine.Imaging.Half16.ToFloat(hb[si + 1]); o[di + 2] = Lux.Engine.Imaging.Half16.ToFloat(hb[si + 2]); o[di + 3] = 1f;
                            }
                        }
                        return o;
                    });
                    var l1Gen = Lux.Engine.Pipeline.ResAmp.ImageGenerator.SqrtOf(W, H, r =>
                    {
                        var img = pc.Level1!(r); var o = new float[r.Width * r.Height * 4];
                        for (int y = 0; y < r.Height; y++) System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.Row(y)).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(o.AsSpan(y * r.Width * 4, r.Width * 4)));
                        return o;
                    });
                    // The telephoto set is the registration's higher group — every camera that has an online-calibration pair (Lumen's
                    // initResAmp builds one tele generator per `Pairs[id]`): B1–B5 for an A-reference capture, C1–C6 for a B-reference
                    // (75 mm) one. The geometry's telephoto list is filled per higher-group camera by State6, so it is exactly that set.
                    // A monochrome module (bayer red position (−1,−1), e.g. C6) cannot feed super-resolution — Lumen's SourceImageCache
                    // ctor throws "Super-res does not support mono modules!" — so it is left out of the telephoto set.
                    var camNames = reg.Names;
                    bool Colour(int id) { var red = lri.Modules[camNames[id]].Module.SensorBayerRedOverride; return red is null || (red.X | red.Y) >= 0; }
                    var tele = reg.Tele.Where(t => Colour(t.Id)).OrderBy(t => t.Id).ToArray();
                    // Each telephoto cache (its own module frame, ISP stats and warp field) is independent of the others, so they are built at
                    // once; the module list keeps the id order the sequential loop had, which is what ImageResolutionAmp merges in.
                    var teleCaches = new Lux.Engine.Pipeline.ResAmp.TeleLevel0Cache[tele.Length];
                    var teleModules = new Lux.Engine.Pipeline.ResAmp.ResAmpModule[tele.Length];
                    pr.Begin("telephoto set-up", tele.Length);
                    Parallel.For(0, tele.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, i =>
                    {
                        var t = tele[i];
                        // `Pairs[id].view/.module`: the online-calibration pair the renderer hands the tele SourceImageCache — always from the
                        // ported registration state.
                        var cache = new Lux.Engine.Pipeline.ResAmp.TeleLevel0Cache(lri, t.Name, t.PairFirst, t.PairSecond, t.Size, sc, (RendererProfile)3, colour, wb.Cct, wb.Tint, stacks?.Get(t.Name), noise);
                        var wf = Lux.Engine.Pipeline.ResAmp.TeleWarpFieldBuilder.BuildFromPoses(t.Pose, t.Slot, reg.RefPose, reg.RefSlot, sc, depth, depthW, depthH);
                        teleCaches[i] = cache; teleModules[i] = new Lux.Engine.Pipeline.ResAmp.ResAmpModule(cache.ToGenerator(), wf);
                        pr.Tick();
                    });
                    for (int i = 0; i < tele.Length; i++) log?.Invoke($"tele {tele[i].Name}: level-0 dims {teleCaches[i].Dims.W}x{teleCaches[i].Dims.H} gain {teleCaches[i].Gain:R}");
                    var modules = teleModules.ToList();
                    return (new Lux.Engine.Pipeline.ResAmp.ImageResolutionAmp(refGen, l1Gen, modules, scale), teleCaches);
                });
            }
            parts = np;
        }
        MakeParts();
        pc.ReferenceLevel = (L, r) => Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel(Isp(L), frame, calib, L, r, new float[4], log, parts.Stacked?.Value);
        if (maxLevel <= 1) pc.Level1 = r => Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel1(calib, r, frame.Width, frame.Height, parts.Fc!.Value.Render, log);
        if (maxLevel == 0)
        {
            pc.Level0 = r => parts.Level0!.Value.Amp.Run(r);
            // The level-0 tiles' inputs, generated ahead of them on every thread: the fusion tiles (the slowest, first), the mono
            // initialization the first level-1 render would otherwise do under a lock, the reference tiles and the telephoto tiles.
            pc.Level0Prefetch = threads =>
            {
                var p = parts;
                var teleCaches = p.Level0!.Value.Tele;
                var fcv = p.Fc!.Value; var refTile = p.RefTile!;
                var work = new List<Action>();
                var (fnx, fny) = fcv.TileGrid;
                for (int ty = 0; ty < fny; ty++) for (int tx = 0; tx < fnx; tx++) { int a = tx, b = ty; work.Add(() => { fcv.EnsureTile(a, b); pr.Tick(); }); }
                // the mono initialization ticks the phase itself (MonoFusion.InitializeTicks); without a mono module it is one no-op unit
                work.Add(() => { fcv.EnsureMonoInitialized(); if (!fcv.HasMono) pr.Tick(); });
                for (int ty = 0; ty < rny; ty++) for (int tx = 0; tx < rnx; tx++) { int a = tx, b = ty; work.Add(() => { refTile(a, b); pr.Tick(); }); }
                foreach (var tc in teleCaches)
                    for (int ty = 0; ty < tc.Grid.Y; ty++) for (int tx = 0; tx < tc.Grid.X; tx++) { int a = tx, b = ty; var c = tc; work.Add(() => { c.EnsureTile(a, b); pr.Tick(); }); }
                pr.Begin("level-0 inputs", work.Count - 1 + (fcv.HasMono ? Lux.Engine.Pipeline.BayerFusion.MonoFusion.InitializeTicks : 1));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(work, loadBalance: true), new ParallelOptions { MaxDegreeOfParallelism = threads }, a => a());
                log?.Invoke($"prefetch: {work.Count} level-0 inputs (fusion {fnx * fny}, reference {rnx * rny}, telephoto {work.Count - fnx * fny - rnx * rny - 1}) on {threads} threads in {sw.Elapsed.TotalSeconds:F1}s");
            };
        }
        // what the generator of each level needs before its first tile, made on the calling thread (see PipelineCache.Warm)
        pc.Warm = L =>
        {
            var p = parts;
            _ = p.Stacked?.Value;
            if (L == 1) _ = p.Fc?.Value;
            if (L == 0) _ = p.Level0?.Value;
        };
        var geoOnce = geometry;
        return new ExportState
        {
            Lri = lri, Cache = pc, Frame = frame, Capture = CaptureState.FromReference(lri), Colour = colour, Wb = wb, TuningOfLevel = Tuning, Dims = dims,
            BuildLevel = maxLevel, Progress = pr, Noise = noise ?? NoiseControls.Lumen,
            FullDepthSource = geoOnce is null ? null : () => geoOnce.Value.Geo.FullDepth,
            GeometrySource = geoOnce is null ? null : () => geoOnce.Value.Geo,
            GeometryKnown = geoOnce is null ? null : () => geoOnce.IsValueCreated,
            // the chain's own state once it has run (and is kept), else the Setup()-only state, whose reference-group pairs are the same
            RegistrationSource = () => geoOnce is { IsValueCreated: true } ? geoOnce.Value.Api ?? setupApi : setupApi,
            ReleaseInputs = MakeParts,
        };
    }

    /// <summary>The parts a build makes on first use (see <see cref="ExportState.ReleaseLevel0Inputs"/>).</summary>
    sealed class BuildParts
    {
        public Lux.Engine.Pipeline.BayerFusion.StackProvider? Stacks;
        public Once<Lux.Engine.Pipeline.Geometry.AlignedWarp.StackedSource>? Stacked;
        public Once<Lux.Engine.Pipeline.BayerFusion.FusionCacheBayer>? Fc;
        public TileStore<(int, int), (RectI Rect, ushort[] Half)>? RefTiles;
        public Func<int, int, (RectI Rect, ushort[] Half)>? RefTile;
        public Once<(Lux.Engine.Pipeline.ResAmp.ImageResolutionAmp Amp, Lux.Engine.Pipeline.ResAmp.TeleLevel0Cache[] Tele)>? Level0;
    }
}
