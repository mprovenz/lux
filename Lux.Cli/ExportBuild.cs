using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Cli;

/// <summary>The renderer state an export needs (the `pcache-test` level-0 construction made reusable): the PipelineCache with its level generators
/// (levels 2–4 reference cache, level 1 fusion, level 0 ResAmp from the registration state), the reference frame, the
/// colour profile / WB and the per-level module-ISP tunings.</summary>
public sealed class ExportState
{
    public required LriFile Lri; public required PipelineCache Cache; public required CapturedFrame Frame;
    /// <summary>Load-time state of the reference capture (SoT §3) — notably `lens_shading.multiplier`, which the export's
    /// per-tile `RemoveVignettingGeneric&lt;vec4x32f,1&gt;` uses.</summary>
    public required CaptureState Capture;
    public required LumenProfile Colour; public required WhiteBalance.CaptureWb Wb;
    public required Func<int, Tuning> TuningOfLevel;
    public required (int W, int H)[] Dims;
    /// <summary>The upsampled full-frame depth (`StereoAsyncApi.FullDepth`), when the registration chain ran — the
    /// source `RendererPrivate::setInputDataStream` fills the `renderer+0x480` depth ImageCache from, and therefore
    /// what `ExportImageFormat` 4 (JPEG + GDepth) needs. Null when only levels >= 1 were built.</summary>
    public (float[] Depth, int W, int H)? FullDepth;
    /// <summary>The registration state the build ran (`StereoAsyncApi`: the ctor pairs `api+0x3a8[id]`, and at level 0
    /// the whole chain). The parallax geometry reads the reference-group pairs from it — a level-0 build leaves those
    /// exactly as `Setup()` made them — so no second setup is needed. Optional: a caller constructing its own state may
    /// leave it null, and <see cref="ExportSession.Registration"/> then sets one up itself.</summary>
    public Lux.Engine.Pipeline.Registration.StereoAsyncApi? Registration;
}

public static class ExportBuild
{
    /// <summary>Build the cache from the `.lri` and nothing else. <paramref name="maxLevel"/> = the finest pipeline level that will be
    /// rendered (0 needs the registration state). The reference calibration is the ctor pair `api+0x3a8[ref]` and the level-0 depth is the
    /// in-process dense stereo, both out of <see cref="Lux.Engine.Pipeline.Registration.StereoAsyncApi"/>.</summary>
    public static ExportState Build(string lriPath, int maxLevel, Action<string>? log)
    {
        var lri = LriFile.Load(lriPath);
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;
        // The registration state. Level 0 needs the whole chain (tele pairs/poses + the dense depth); levels >= 1 need only the reference
        // AlignedCalib, i.e. the ctor pair `api+0x3a8[ref]` that `Setup()` alone produces (slots/poses/crops — no images, no bundle adjustment).
        Lux.Engine.Pipeline.Registration.StereoAsyncApi? api = null;
        if (maxLevel == 0)
        {
            log?.Invoke("export: dense L5 depth computed in-process (self-contained)");
            var swReg = System.Diagnostics.Stopwatch.StartNew();
            api = Lux.Engine.Pipeline.Registration.StereoAsyncApi.Run(lri, log, runHigher: true, runDense: true, depthOverride: null);
            log?.Invoke($"export: registration state ready in {swReg.Elapsed.TotalSeconds:F1}s");
        }
        Lux.Engine.Pipeline.Geometry.CameraCalib view, module;
        {   // the reference pair straight out of the registration state (ctor loop 2 of `StereoAsyncAPI`)
            if (api is null) { var s = new Lux.Engine.Pipeline.Registration.StereoAsyncApi { Lri = lri, Log = log }; s.Setup(); api = s; }
            (view, module) = api.Pairs[refId];
            log?.Invoke($"export: reference calibration from the registration state (api+0x3a8[{refId}])");
        }
        var dist = Lux.Engine.Pipeline.Registration.StereoImageBuilder.DistortionOf(lri, lri.ReferenceModule);
        var calib = Lux.Engine.Pipeline.Geometry.AlignedCalib.Build(view, module, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);
        var frame = CapturedFrame.Load(lri, lri.ReferenceModule);
        var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
        var tunings = new Dictionary<int, Tuning>();
        Tuning Tuning(int cfg) { if (!tunings.TryGetValue(cfg, out var t)) { t = ModuleIspTuning.Build(cfg, (RendererProfile)3, frame.Info, wb.Cct, wb.Tint); tunings[cfg] = t; } return t; }
        var isps = new Dictionary<int, SoftIsp>();
        SoftIsp Isp(int L) { if (!isps.TryGetValue(L, out var isp)) { isp = new SoftIsp(Tuning(new[] { 0, 2, 3, 4 }[L]), colour); isp.ComputeStats(frame); isps[L] = isp; } return isp; }
        // `setInputDataStream`'s `renderer+0x2a0` vector: level 0 is the pipeline canvas (`(int)(sensor · FUN_1804b23e0)`, reference-group dependent —
        // 10432×7824 for an A reference, 8896×6672 for a B one), level 1 is the module frame itself and levels 2–4 halve it.
        var canvas0 = Lux.Engine.Pipeline.Export.ExportWindow.Canvas(lri, (frame.Width, frame.Height));
        var dims = new (int W, int H)[] { canvas0, (frame.Width, frame.Height), (frame.Width / 2, frame.Height / 2), (frame.Width / 4, frame.Height / 4), (frame.Width / 8, frame.Height / 8) };
        var pc = new PipelineCache(dims) { Neutral = lri.LumenNeutral, Log = log };
        // `ReferenceImageCache::processLevel` L145: `FUN_18020a6d0(stream, refCam)` — non-null on a stacked capture, and then the
        // level runs the BayerFloat runner on `lt::StackFusion`'s fused frame with the gain map as its STD plane.
        Lux.Engine.Pipeline.Geometry.AlignedWarp.StackedSource? stacked = null;
        if (lri.StackFrames >= 2)
        {
            var sf = new Lux.Engine.Pipeline.BayerFusion.StackFusion(lri, lri.ReferenceModule, wb.Cct, wb.Tint, log);
            int margin = Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Halo(frame.Info.AnalogGain);   // ReferenceImageCache+0xbc = FUN_18050cbf0
            if (Environment.GetEnvironmentVariable("LUX_STACK_MARGIN") is string mo && int.TryParse(mo, out int mv)) { margin = mv; Console.Error.WriteLine($"[diagnostic] LUX_STACK_MARGIN: ISP tile margin {margin} instead of {Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.Halo(frame.Info.AnalogGain)}"); }
            stacked = new Lux.Engine.Pipeline.Geometry.AlignedWarp.StackedSource(sf.BayerImage(), sf.StdImage, margin);
            log?.Invoke($"export: stacked capture ({lri.StackFrames} frames) — reference {lri.ReferenceModule} fused by lt::StackFusion, ISP margin {margin}");
        }
        pc.ReferenceLevel = (L, r) => Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel(Isp(L), frame, calib, L, r, new float[4], log, stacked);
        if (maxLevel <= 1)
        {
            // The fusion reference is the capture's OWN reference module, not a hardcoded A1: `refId` is 0 for every
            // A-reference capture (the whole verified corpus) but 8 for the B4-reference 149 mm captures.
            var fus = new Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion(lri, refId, wb.Cct, wb.Tint, log, sourceFrameBlackEstimate: maxLevel != 0);
            var fc = new Lux.Engine.Pipeline.BayerFusion.FusionCacheBayer(lri, frame, fus, (RendererProfile)3, wb.Cct, wb.Tint, log);
            pc.Level1 = r => Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel1(calib, r, frame.Width, frame.Height, fc.Render, log);
        }
        if (maxLevel == 0)
        {
            var reg = api!;   // level 0 always ran the whole registration chain above
            if (reg.FullDepth is null) reg.UpsampleFullDepth();
            float[] depth = reg.FullDepth!.Depth; int depthW = reg.FullDepth.W, depthH = reg.FullDepth.H;
            float scale = (float)dims[0].W / (float)dims[1].W; var sc = (scale, scale);
            int W = frame.Width, H = frame.Height;
            var refTiles = new Dictionary<(int, int), (RectI Rect, ushort[] Half)>();
            var (rnx, rny) = (Math.Max(1, (256 + W) / 512), Math.Max(1, (256 + H) / 512));
            (RectI Rect, ushort[] Half) RefTile(int tx, int ty)
            {
                if (refTiles.TryGetValue((tx, ty), out var t)) return t;
                int x0 = tx * 512, y0 = ty * 512, x1 = tx == rnx - 1 ? W : x0 + 512, y1 = ty == rny - 1 ? H : y0 + 512;
                var rect = new RectI(x0, y0, x1, y1);
                var img = Lux.Engine.Pipeline.Geometry.AlignedWarp.ProcessLevel(Isp(0), frame, calib, 0, rect, new float[4]);
                var hbuf = new ushort[rect.Width * rect.Height * 3];
                for (int y = 0; y < rect.Height; y++) { var row = img.Row(y); for (int x = 0; x < rect.Width; x++) { var q = row[x]; int i = (y * rect.Width + x) * 3; hbuf[i] = Lux.Engine.Imaging.Half16.FromFloat(q.R); hbuf[i + 1] = Lux.Engine.Imaging.Half16.FromFloat(q.G); hbuf[i + 2] = Lux.Engine.Imaging.Half16.FromFloat(q.B); } }
                log?.Invoke($"ref cache: level-0 tile ({tx},{ty}) {rect.Width}x{rect.Height}");
                return refTiles[(tx, ty)] = (rect, hbuf);
            }
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
            var camNames = new[] { "A1", "A2", "A3", "A4", "A5", "B1", "B2", "B3", "B4", "B5" };
            var modules = new List<Lux.Engine.Pipeline.ResAmp.ResAmpModule>();
            foreach (int id in reg.Sizes.Keys.Where(k => k >= 5 && k <= 9).OrderBy(k => k))
            {
                // `api+0x3a8[id].view/.module`: the online-calibration pair the renderer hands the tele SourceImageCache — always from the
                // ported registration state.
                var (v2, m2) = reg.Pairs[id];
                var cache = new Lux.Engine.Pipeline.ResAmp.TeleLevel0Cache(lri, camNames[id], v2, m2, reg.Sizes[id], sc, (RendererProfile)3, colour, wb.Cct, wb.Tint);
                var wf = Lux.Engine.Pipeline.ResAmp.TeleWarpFieldBuilder.BuildFromPoses(reg.Cams[id].Pose, reg.Cams[id].Slot, reg.Cams[refId].Pose, reg.Cams[refId].Slot, sc, depth, depthW, depthH);
                modules.Add(new Lux.Engine.Pipeline.ResAmp.ResAmpModule(cache.ToGenerator(), wf));
                log?.Invoke($"tele {camNames[id]}: level-0 dims {cache.Dims.W}x{cache.Dims.H} gain {cache.Gain:R}");
            }
            var amp = new Lux.Engine.Pipeline.ResAmp.ImageResolutionAmp(refGen, l1Gen, modules, scale);
            pc.Level0 = r => amp.Run(r);
        }
        (float[], int, int)? full = null;
        if (api?.FullDepth is not null) full = (api.FullDepth.Depth, api.FullDepth.W, api.FullDepth.H);
        return new ExportState { Lri = lri, Cache = pc, Frame = frame, Capture = CaptureState.FromReference(lri), Colour = colour, Wb = wb, TuningOfLevel = Tuning, Dims = dims, FullDepth = full, Registration = api };
    }
}
