using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `StereoAsyncAPI` (ctor `1804eb200` + `start` state machine, spec `ad108613f7a1daa2b.md` §1 and `a1f28d783d7f417c5.md` §1):
/// the whole online-calibration chain from an LRI — load-time slots (`CalibSlotInit`), ctor poses (P = R_ref, u = t_ref, A-cam crop → shift1/scale1,
/// ctor pairs), state 2 stereo images (`StereoImageBuilder`, scale2 = shift2 = 0.5), state 3 WIDE (`CalibDataProcessor`), states 4/5 dense stereo on
/// the post-BA views, state 6 canvas poses + B-cam stereo images + pairs, state 7 coarse init + TELE. Results: the final CURRENT slots, the pose map,
/// the renderer pairs/sizes and the dense depth.
/// </summary>
public sealed class StereoAsyncApi
{
    public LriFile Lri = null!; public Action<string>? Log;
    public CalibDataProcessor Cdp = new();
    public Dictionary<int, CdpCamera> Cams = new(); public Dictionary<int, string> Names = new();
    public Dictionary<int, (CameraCalib First, CameraCalib Second)> Pairs = new(); public Dictionary<int, (int W, int H)> Sizes = new();
    public float[]? RefYuv; public IspStats RefStats = null!; public Color.LumenProfile Profile = null!;
    public float[] Neutral = null!;                      // api+0x1b8 = FUN_18020be30(mgr): the capture AsShot neutral
    public Dictionary<int, (CameraCalib View, CameraCalib Module)> State2Views = new();
    public DenseLayer[]? Dense; public Dictionary<int, StereoImageBuilder.Distortion> Dist = new();
    /// <summary>`api+0x1f8`: the full-res reference guide of the `UpsampleLayer` (`StereoISP::GetReferenceImage`, state 1 `1804ed3d0`, port
    /// `Registration/ReferenceGuide.cs`). Built in <see cref="State1And2"/> unless preset (e.g. from a cp.dll reference dump) or `LUX_NO_GUIDE=1`; when null the
    /// upsample layer is skipped and `FullDepth` stays null.</summary>
    public Rgba8Image? ReferenceGuide;
    /// <summary>Layer 6 (`UpsampleLayer+0xa0`): the (W0, H0) metric depth read by the level-0 WarpFields (`a-resamp.md` §7.1).</summary>
    public DenseUpsampleLayer.Result? FullDepth;
    public int RefId;

    static int Group(int id) => id <= 4 ? 0 : id <= 9 ? 1 : 2;
    static CameraCalib Shifted(CameraCalib c, float dx, float dy) { var k = (float[])c.K.Clone(); k[2] -= dx; k[5] -= dy; return c with { K = k, ViewOffX = c.ViewOffX + dx, ViewOffY = c.ViewOffY + dy }; }
    /// <summary>`FUN_180111e00` pixel size by sensor type (`DAT_18068a548`): AR1335 (types 2/3) 0.0011, IMX386 0.0012, AR835 0.0014.</summary>
    static float PixMm(SensorType s) => s is SensorType.SensorAr1335 or SensorType.SensorAr1335Mono ? 0.0011f : s is SensorType.SensorAr835 ? 0.0014f : 0.0012f;

    /// <summary>Progress of <see cref="Run"/>: the "registration" phase, one unit per state or per camera image.</summary>
    public ProgressReporter Progress { get; set; } = ProgressReporter.None;

    /// <summary>Per-run dense-dump prefix (`LUX_DENSE_DUMP` + the capture stem, set by the CLI so a batch does not overwrite itself); null = no dumps.</summary>
    public string? DumpPrefix;
    /// <summary>Worker threads for the per-module and per-tile work of the registration (stereo images, guide tiles, sparse drivers, the
    /// optimisers, the dense matching costs). Every parallel section produces exactly the sequential result — results are applied and
    /// logged in the sequential order, the dense aggregation sweep itself stays serial — so 1 (the default) and N are bit-identical.</summary>
    public int Threads = 1;
    public static StereoAsyncApi Run(LriFile lri, Action<string>? log = null, bool runHigher = true, bool runDense = true, float[]? depthOverride = null, ProgressReporter? progress = null, string? dumpPrefix = null, int threads = 1)
    {
        var api = new StereoAsyncApi { Lri = lri, Log = log, DumpPrefix = dumpPrefix, Progress = progress ?? ProgressReporter.None, Threads = Math.Max(1, threads) }; api.Cdp.Threads = api.Threads; api.Setup();
        int refGroup = api.RefGroupCams().Count(), higher = api.Cdp.Higher.Count;
        // guide + one per reference-group image + sparse wide + dense + upsample + one per higher image + coarse/tele
        api.Progress.Begin("registration", 1 + refGroup + 1 + 2 + higher + 1);
        api.State1And2(); api.State3(); if (runDense || depthOverride is not null) api.State4And5(depthOverride); if (runHigher) { api.State6(); api.State7(depthOverride); }
        api.Progress.End();
        return api;
    }

    /// <summary>Ctor: slots, poses (P = R_ref, u = t_ref), A-cam crop views and the reference-group pairs; per-camera facts.</summary>
    public void Setup()
    {
        string refName = Lri.ReferenceModule; RefId = (int)Lri.Modules[refName].Module.Id;
        Profile = Color.LumenProfile.Compute(Lri);
        var refSlot = CalibSlotInit.Build(Lri, refName);
        var refInfo = ModuleFrameInfo.From(Lri, refName);
        foreach (var kv in Lri.Modules)   // capture (file) order = FUN_180112240 order
        {
            var m = kv.Value.Module; int id = (int)m.Id; Names[id] = kv.Key;
            var slot = CalibSlotInit.Build(Lri, kv.Key);
            var pose = ViewPose.Identity(); pose.P = (float[])refSlot.R.Clone(); pose.U = (float[])refSlot.T.Clone();
            var g = ModulePose.Geometry(Lri.Header, m.Id)!;
            var mir = CalibSlotInit.Mirror(g);
            var c = new CdpCamera
            {
                Id = id, Name = kv.Key, Slot = slot, FactorySlot = slot.Clone(), Pose = pose,
                SensorType = CdpInputs.MirrorType(Lri.Header, m.Id), Gray = CdpInputs.Gray(Lri.Header, m), StoredResult = CdpInputs.StoredResult(Lri.Header, m),
                FrameW = m.SensorDataSurface.Size.X, FrameH = m.SensorDataSurface.Size.Y, Hall = m.MirrorPosition, Mirror = mir?.Sys, Map = mir?.Map,
            };
            var d = StereoImageBuilder.DistortionOf(Lri, kv.Key); Dist[id] = d; c.Poly = d.Poly; c.PpX = d.PpX; c.PpY = d.PpY; c.Pix = d.Pix;
            Cams[id] = c;
        }
        // ctor loop 2: reference group ([ref] first, then the others in capture order): pair.second = Apply(P,u only), crop → shift1/scale1, pair.first = Apply(pose)
        foreach (var c in RefGroupCams())
        {
            Pairs[c.Id] = (null!, CdpCamera.ToCamera(c.View()));
            var info = ModuleFrameInfo.From(Lri, c.Name);
            var rect = AcamCrop.Compute(c.FrameW, c.FrameH, info.DataScaleX, c.Poly!, PixMm(info.Sensor), refInfo.DataScaleX);
            var (sh, sc) = AcamCrop.Pose(rect, c.FrameW, c.FrameH);
            c.Pose.Shift1 = sh; c.Pose.Scale1 = sc;
            Pairs[c.Id] = (CdpCamera.ToCamera(c.View()), Pairs[c.Id].Second);
            Log?.Invoke($"ctor cam {c.Id} ({c.Name}): crop ({rect.X0},{rect.Y0},{rect.X1},{rect.Y1}) shift1 {sh} scale1 {sc}");
        }
        Cdp.Log = Log; Cdp.CamsType = 0; Cdp.Ref = Cams[RefId]; Cdp.Dump = DumpPrefix is string rdp ? new RegDump(rdp) : null;
        foreach (var c in RefGroupCams()) if (c.Id != RefId) Cdp.RefGroup.Add(c);
        foreach (var c in Cams.Values) if (Group(c.Id) > Group(RefId)) Cdp.Higher.Add(c);
        Cdp.Z = CdpInputs.PlaneDepth(Lri.Modules[refName].Module, Cdp.ZRange.Min); Cdp.WideFlag = CdpInputs.WideFlag(Lri.Header, Lri.Modules[refName].Module.Id);
        Log?.Invoke($"setup: ref {RefId} ({refName}) refGroup [{string.Join(",", Cdp.RefGroup.Select(c => c.Id))}] higher [{string.Join(",", Cdp.Higher.Select(c => c.Id))}] Z {Cdp.Z} wideFlag {Cdp.WideFlag}");
    }

    public IEnumerable<CdpCamera> RefGroupCams()
    {
        yield return Cams[RefId];
        foreach (var c in Cams.Values) if (c.Id != RefId && Group(c.Id) == Group(RefId)) yield return c;
    }

    /// <summary>States 1/2: the reference YUV image and the half-res stereo images of the reference group (`FUN_1804edb40`: scale2 = shift2 = 0.5).</summary>
    public void State1And2()
    {
        var refFrame = CapturedFrame.Load(Lri, Names[RefId]);
        Neutral = Lri.LumenNeutral; var neutral = Neutral;
        // state 1 (FUN_1804ed3d0): the guide from the reference capture through the ctor pose (scale2/shift2 still at their defaults)
        if (ReferenceGuide is null && Environment.GetEnvironmentVariable("LUX_NO_GUIDE") != "1") ReferenceGuide = BuildReferenceGuide(refFrame).Guide;
        Progress.Tick();
        RefStats = StereoImageBuilder.Isp(refFrame, Profile, null).ComputeStats(refFrame);
        // The reference image first (it yields RefYuv, which every other module's colour transfer reads), then the other reference-group
        // modules — independent of each other — on Threads workers; their views/images/logs are applied in capture order.
        var group = RefGroupCams().ToList();
        string StereoOne(CdpCamera c, CapturedFrame frame, Geometry.CameraCalib refView)
        {
            c.Pose.Scale2 = (0.5f, 0.5f); c.Pose.Shift2 = (0.5f, 0.5f);
            var view = CdpCamera.ToCamera(c.View());
            var mp = Clone(c.Pose); mp.Scale1 = (1f, 1f); mp.Shift1 = (0f, 0f);
            var module = CdpCamera.ToCamera(ViewTransform.Apply(mp, c.Slot));
            var size = (c.FrameW / 2, c.FrameH / 2); lock (State2Views) State2Views[c.Id] = (view, module);
            var isp = StereoImageBuilder.Isp(frame, Profile, RefStats);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = StereoImageBuilder.Create(frame, refFrame, isp, view, module, size, neutral, c.Id == RefId ? null : RefYuv, refView, c.Id == RefId, true, Dist[c.Id]);
            if (c.Id == RefId) RefYuv = r.RefYuv;
            c.Image = new Rgba8Image(r.Rgba8, r.W, r.H, r.W); c.SatLevel = r.Val;
            c.Centre = CdpInputs.WideCentre(c, Lri.Modules[c.Name].Module);
            Progress.Tick();
            return $"state 2 cam {c.Id} ({c.Name}): {r.W}x{r.H} val {r.Val:R} centre {c.Centre} {sw.Elapsed.TotalSeconds:F1}s";
        }
        // the reference's own view is taken BEFORE its pose gets scale2/shift2 (the sequential loop computed refView first), the others' after
        var refLine = StereoOne(group[0], refFrame, CdpCamera.ToCamera(Cams[RefId].View()));   // never inside `Log?.Invoke(...)`: a null Log would skip the call
        Log?.Invoke(refLine);
        var others = group.Skip(1).ToList(); var refViewAfter = CdpCamera.ToCamera(Cams[RefId].View());
        var lines = new string[others.Count];
        void One(int i) => lines[i] = StereoOne(others[i], CapturedFrame.Load(Lri, others[i].Name), refViewAfter);
        if (Threads > 1 && others.Count > 1) Parallel.For(0, others.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, One); else for (int i = 0; i < others.Count; i++) One(i);
        foreach (var l in lines) Log?.Invoke(l);
    }
    /// <summary>State 1 (`FUN_1804ed3d0`): `GetReferenceImage(img, img, FUN_180307b30(img) = the reference module's CURRENT slot, FUN_1802e1580(pose[ref], slot) = its view, api+0x1b8)`.
    /// Must run before state 2 sets scale2/shift2 on the pose.</summary>
    public ReferenceGuide.Result BuildReferenceGuide(CapturedFrame? refFrame = null, bool keepFloat = false, int maxTiles = int.MaxValue)
    {
        refFrame ??= CapturedFrame.Load(Lri, Names[RefId]);
        Neutral = Lri.LumenNeutral;
        var c = Cams[RefId];
        var module = CdpCamera.ToCamera(c.Slot);
        var view = CdpCamera.ToCamera(c.View());
        Log?.Invoke($"state 1 guide: view K [{string.Join(" ", view.K.Select(v => v.ToString("R")))}] off ({view.ViewOffX:R},{view.ViewOffY:R}) crop ({view.CropX:R},{view.CropY:R}); module K [{string.Join(" ", module.K.Select(v => v.ToString("R")))}]");
        return Registration.ReferenceGuide.Build(refFrame, Profile, Neutral, view, module, Dist[RefId], Log, keepFloat, maxTiles, Threads);
    }

    static ViewPose Clone(ViewPose p) => new() { P = (float[])p.P.Clone(), U = (float[])p.U.Clone(), Q = (float[])p.Q.Clone(), Scale1 = p.Scale1, Shift1 = p.Shift1, Scale2 = p.Scale2, Shift2 = p.Shift2, Shift3 = p.Shift3, Scale3 = p.Scale3 };

    /// <summary>State 3: `runReferenceGroupCams`.</summary>
    public void State3() { Cdp.RunReferenceGroupCams(); Progress.Tick(); }

    /// <summary>States 4/5: dense stereo on the reference-group images with the post-BA views (`FUN_1804fa6f0` + `FUN_18030cd00` per layer); the depth for the TELE stage
    /// is the finest layer (2080×1560).</summary>
    public void State4And5(float[]? depthOverride = null)
    {
        var cams = RefGroupCams().ToList();
        if (depthOverride is null)
        {
            var images = cams.Select(c => c.Image).ToArray(); var gray = cams.Select(c => c.Gray).ToArray(); var calibs = cams.Select(c => c.View().Basic()).ToList();
            Dense = DenseStereoPyramid.Run(images, gray, calibs, Cdp.ZRange.Min, Cdp.ZRange.Max, Log, Threads);
            var top = Dense[^1]; Cdp.Depth = top.Depth; Cdp.DepthW = top.W; Cdp.DepthH = top.H;
            if (DumpPrefix is string dp)   // twins of the oracle's ORACLE_DEPTH `<out>_dense_L<i>_depth.f32` (WTA hook per layer)
            {
                for (int i = 0; i < Dense.Length; i++) DumpF32($"{dp}_dense_L{i}_depth.f32", Dense[i].Depth, Dense[i].W, Dense[i].H);
                // `_dense_calibs.bin`: the layer-0 CalibData vector in the oracle's 0xa8 layout (K @0x00, t @0x24, R @0x30, the rest zero — Lux's Basic() carries only these)
                { var cb = new byte[calibs.Count * 0xa8]; for (int c = 0; c < calibs.Count; c++) { Buffer.BlockCopy(calibs[c].K, 0, cb, c * 0xa8, 36); Buffer.BlockCopy(calibs[c].T, 0, cb, c * 0xa8 + 0x24, 12); Buffer.BlockCopy(calibs[c].R, 0, cb, c * 0xa8 + 0x30, 36); } File.WriteAllBytes($"{dp}_dense_calibs.bin", cb); }
                // the layer-0 inputs and the small layers' cost volumes in the oracle's exact layouts (sinputs_wrap / wta_wrap / pass2_wrap dumps)
                for (int k = 0; k < images.Length; k++) { DumpRgba8($"{dp}_dense_img{k}.img", images[k]); DumpRgba8($"{dp}_dense_filt{k}.img", Dense[0].Images[k]); }
                int cvMax = int.TryParse(Environment.GetEnvironmentVariable("LUX_DENSE_DUMP_CV"), out int cm) ? cm : 2;
                for (int i = 0; i < Dense.Length; i++)
                {
                    var L = Dense[i]; var pl = new byte[L.Planes.Length * 4]; Buffer.BlockCopy(L.Planes, 0, pl, 0, pl.Length); File.WriteAllBytes($"{dp}_dense_L{i}_planes.f32", pl);
                    var sk = new byte[16 + L.W * L.H]; BitConverter.GetBytes(L.W).CopyTo(sk, 0); BitConverter.GetBytes(L.H).CopyTo(sk, 4); BitConverter.GetBytes(L.W).CopyTo(sk, 8); BitConverter.GetBytes(1).CopyTo(sk, 12); Buffer.BlockCopy(L.Skip, 0, sk, 16, L.W * L.H); File.WriteAllBytes($"{dp}_dense_L{i}_skip.u8", sk);
                    if (i == 0) DumpRgba8($"{dp}_dense_L0_guidance.img", L.Guidance);
                    if (i <= cvMax) DumpCostVolume($"{dp}_dense_L{i}", L);
                }
            }
        }
        else { Cdp.Depth = depthOverride; Cdp.DepthW = Cams[RefId].Image.W; Cdp.DepthH = Cams[RefId].Image.H; }
        Progress.Tick();
        UpsampleFullDepth();
        if (FullDepth is not null && DumpPrefix is string dp2) DumpF32($"{dp2}_fulldepth.f32", FullDepth.Depth, FullDepth.W, FullDepth.H);
        Progress.Tick();
    }

    /// <summary>State 5, last layer (`FUN_18030cd00` mode 0): `UpsampleLayer::slot(0x08)(layers[5])` → `FullDepth` (W0×H0) when the guide (`api+0x1f8`) is available.
    /// LayerStack size = (W0/2, H0/2) with (W0, H0) = the level-0 reference frame (`FUN_18030be70`, `StereoAsyncAPI::start`).</summary>
    /// <summary>Oracle `dump_image` format for an RGBA8 image: int32 {w, h, stride(px), 4} then `h` rows of `w·4` bytes.</summary>
    public static void DumpRgba8(string path, Rgba8Image im)
    {
        var b = new byte[16 + (long)im.W * im.H * 4];
        BitConverter.GetBytes(im.W).CopyTo(b, 0); BitConverter.GetBytes(im.H).CopyTo(b, 4); BitConverter.GetBytes(im.W).CopyTo(b, 8); BitConverter.GetBytes(4).CopyTo(b, 12);
        for (int y = 0; y < im.H; y++) Buffer.BlockCopy(im.Data, im.Offset(0, y), b, 16 + y * im.W * 4, im.W * 4);
        File.WriteAllBytes(path, b);
    }
    /// <summary>The oracle's cost-volume layout (`layer+0x118` buffer + `layer+0x128` offsets image): per pixel an 8-byte header
    /// `{start u16, count u16, 1, cap u16}` then `cap` u16 aggregates then (computed pixels only) `cap` u8 raw costs. Writes
    /// `<prefix>_offsets.u32` (int32 header + u32 per pixel), `<prefix>_costvol.bin` (final aggregates) and `<prefix>_costvol_pass1.bin`
    /// (the pass-1 copy, when kept).</summary>
    public static void DumpCostVolume(string prefix, DenseLayer L)
    {
        int npx = L.W * L.H; var off = new uint[npx]; long total = 0;
        for (int i = 0; i < npx; i++) { off[i] = (uint)total; total += 8 + (long)L.Cap[i] * (L.Skip[i] == 0 ? 3 : 2); }
        var ob = new byte[16 + npx * 4]; BitConverter.GetBytes(L.W).CopyTo(ob, 0); BitConverter.GetBytes(L.H).CopyTo(ob, 4); BitConverter.GetBytes(L.W).CopyTo(ob, 8); BitConverter.GetBytes(4).CopyTo(ob, 12);
        Buffer.BlockCopy(off, 0, ob, 16, npx * 4); File.WriteAllBytes($"{prefix}_offsets.u32", ob);
        void Write(string path, ushort[][] agg)
        {
            var buf = new byte[total];
            for (int i = 0; i < npx; i++)
            {
                int o = (int)off[i]; int cap = L.Cap[i];
                BitConverter.GetBytes(L.Start[i]).CopyTo(buf, o); BitConverter.GetBytes(L.Count[i]).CopyTo(buf, o + 2); BitConverter.GetBytes((ushort)1).CopyTo(buf, o + 4); BitConverter.GetBytes(L.Cap[i]).CopyTo(buf, o + 6);
                Buffer.BlockCopy(agg[i], 0, buf, o + 8, cap * 2);
                if (L.Skip[i] == 0 && L.Raw[i] is not null) Buffer.BlockCopy(L.Raw[i], 0, buf, o + 8 + cap * 2, cap);
            }
            File.WriteAllBytes(path, buf);
        }
        Write($"{prefix}_costvol.bin", L.Agg);
        if (L.AggPass1 is not null) Write($"{prefix}_costvol_pass1.bin", L.AggPass1);
    }
    /// <summary>Diagnostic dump in the oracle's image format: int32 {w, h, stride, bpp=4} then the rows.</summary>
    public static void DumpF32(string path, float[] data, int w, int h)
    {
        var b = new byte[16 + (long)w * h * 4];
        BitConverter.GetBytes(w).CopyTo(b, 0); BitConverter.GetBytes(h).CopyTo(b, 4); BitConverter.GetBytes(w).CopyTo(b, 8); BitConverter.GetBytes(4).CopyTo(b, 12);
        Buffer.BlockCopy(data, 0, b, 16, w * h * 4); File.WriteAllBytes(path, b);
    }
    /// <summary>Read a dump written by <see cref="DumpF32"/> / the oracle (`int32 {w, h, stride, bpp}` + rows of `w` floats).</summary>
    public static (float[] Data, int W, int H) LoadF32(string path)
    {
        var b = File.ReadAllBytes(path); int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4), bpp = BitConverter.ToInt32(b, 12);
        if (bpp != 4) throw new InvalidOperationException($"{path}: expected a 4-byte float dump, bpp {bpp}");
        var d = new float[w * h]; Buffer.BlockCopy(b, 16, d, 0, w * h * 4); return (d, w, h);
    }
    public void UpsampleFullDepth()
    {
        if (ReferenceGuide is null || Cdp.Depth.Length == 0) return;
        var c = Cams[RefId]; var sw = System.Diagnostics.Stopwatch.StartNew();
        FullDepth = DenseUpsampleLayer.Run(Cdp.Depth, Cdp.DepthW, Cdp.DepthH, ReferenceGuide.Value, c.FrameW / 2, c.FrameH / 2);
        Log?.Invoke($"layer 6 (UpsampleLayer): {FullDepth.W}x{FullDepth.H} {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>State 6: per higher-group camera the canvas pose, the size/pair maps and the canvas stereo image (p12 = p13 = 0).</summary>
    public void State6()
    {
        var refFrame = CapturedFrame.Load(Lri, Names[RefId]);
        // the canvas poses in order (they log through the processor), then the canvas stereo images — independent per module — on Threads workers
        var poses = new (Geometry.CameraCalib Aligned, Geometry.CameraCalib Module, (int W, int H) Size)[Cdp.Higher.Count];
        for (int i = 0; i < Cdp.Higher.Count; i++)
        {
            var c = Cdp.Higher[i]; poses[i] = Cdp.CanvasPose(c); var (aligned, module, size) = poses[i];
            Sizes[c.Id] = (2 * size.W, 2 * size.H);
            Pairs[c.Id] = (Shifted(aligned.Scaled(2f, 2f), -0.5f, -0.5f), Shifted(module.Scaled(2f, 2f), -0.5f, -0.5f));
        }
        var lines = new string[Cdp.Higher.Count];
        void One(int i)
        {
            var c = Cdp.Higher[i]; var (aligned, module, size) = poses[i];
            var frame = CapturedFrame.Load(Lri, c.Name);
            var isp = StereoImageBuilder.Isp(frame, Profile, RefStats);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = StereoImageBuilder.Create(frame, refFrame, isp, aligned, module, size, Neutral, RefYuv, null, false, false, Dist[c.Id]);
            c.Image = new Rgba8Image(r.Rgba8, r.W, r.H, r.W); c.SatLevel = r.Val;
            lines[i] = $"state 6 cam {c.Id} ({c.Name}): canvas {r.W}x{r.H} val {r.Val:R} {sw.Elapsed.TotalSeconds:F1}s";
            Progress.Tick();
        }
        if (Threads > 1 && Cdp.Higher.Count > 1) Parallel.For(0, Cdp.Higher.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, One); else for (int i = 0; i < Cdp.Higher.Count; i++) One(i);
        foreach (var l in lines) Log?.Invoke(l);
    }

    /// <summary>State 7: coarse optimizer init on the depth image, then `runHigherGroupCams`.</summary>
    public void State7(float[]? depthOverride = null)
    {
        if (Cdp.Depth.Length == 0) throw new InvalidOperationException("no depth image (run the dense stage or pass one)");
        Cdp.InitCoarse();
        Cdp.RunHigherGroupCams();
        Progress.Tick();
    }
}
