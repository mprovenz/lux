using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Isp.Stages;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// `lt::ColorFusionBayer` / `PackedBayerFusion` (cp.dll Lumen 2.3; spec `a4ce3d1abcbdfdc45.md`, port notes
/// `a-fusion-port.md`): the level-1 multi-module Bayer fusion. `initialize` (`1801f6560`) builds, for the reference and every
/// same-group colour source (§0.1), the hot-pixel-patched, highlight-restored, black-subtracted, gain-scaled, vignetting-corrected
/// float frame (`1801f7a90`), the reference extras (block mean-reciprocal image `1801d7140`, 1/8-packed-res vignetting map,
/// noise lambda_1 `1801f9450`), the per-source block flow (<see cref="BlockFlow"/>) and the packed half images
/// (`PackBayerImageProtoType`). <see cref="Process"/> (`1801f5840`) runs the wavelet merge kernel `1801e6a20` on a packed ROI,
/// re-applies the reference vignetting (`RemoveVignettingGeneric&lt;vec4x32f,0&gt;`), adds the black back and unpacks the fused Bayer
/// frame and the weight image. All float arithmetic follows the machine association; SSE approximations (`rcpps`, `rsqrtss`) are
/// reproduced with the hardware instructions.
/// </summary>
public sealed class PackedBayerFusion
{
    // ---------------------------------------------------------------------------------------------------------------------------------
    // Constants (bit patterns read from cp.dll .rdata)
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly float NoiseMul = BitConverter.Int32BitsToSingle(0x41000000);   // DAT_180685d4c = 8.0 (kernel noise multiplier)
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x3727c5ac);        // DAT_18068b2e0 = 1e-5
    static readonly float Tenth = BitConverter.Int32BitsToSingle(0x3dcccccd);      // DAT_1806aeb30 = 0.1 (block mean-reciprocal floor)
    static readonly float One = BitConverter.Int32BitsToSingle(0x3f800000);        // DAT_180681c78 / DAT_1806824a0
    static readonly float Two = BitConverter.Int32BitsToSingle(0x40000000);        // DAT_180682414 (halo)
    static readonly float Four = BitConverter.Int32BitsToSingle(0x40800000);       // DAT_180682408 (halo)
    static readonly float Six = BitConverter.Int32BitsToSingle(0x40c00000);        // DAT_180687540 (halo)
    static readonly float C256 = BitConverter.Int32BitsToSingle(0x43800000);       // DAT_180685050 (weight → uint8)
    static readonly float MinusHalf = BitConverter.Int32BitsToSingle(unchecked((int)0xbf000000));   // 0x180681c7c
    static readonly float MinusThree = BitConverter.Int32BitsToSingle(unchecked((int)0xc0400000));  // 0x180681c80

    /// <summary>`DAT_18068be60[16]`: camera id → group (A = 0, B = 1, C = 2).</summary>
    public static int Group(int camId) => camId <= 4 ? 0 : camId <= 9 ? 1 : 2;

    /// <summary>`DAT_1806b5110[256]`: `[0] = 0`, `[i] = (float)sqrt((i + 1) / 256.0)` (all 256 entries verified against the .rdata bytes).</summary>
    public static readonly float[] StdTable = BuildStdTable();
    static float[] BuildStdTable() { var t = new float[256]; for (int i = 1; i < 256; i++) t[i] = (float)Math.Sqrt((i + 1) / 256.0); return t; }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // State (PackedBayerFusion object, §1)
    // ---------------------------------------------------------------------------------------------------------------------------------
    readonly LriFile _lri;
    public int RefCamId { get; }                       // +0x150
    public float Cct { get; }
    public float Tint { get; }
    public float[] A { get; } = new float[4];          // +0x10 noise slope per quad lane (R, G, B, G)
    public float[] B { get; } = new float[4];          // +0x20 noise offset
    public float Black { get; private set; }           // +0x30 (reference frame black)
    public float White { get; private set; }           // +0x34
    public int NStack { get; init; } = 1;              // FUN_180112250: frames per stack (1 for single captures)
    public bool StreamHalfScale { get; }               // stream+0x14 (data_scale == (0.5,0.5)); L16: false
    public List<int> SourceIds { get; } = new();       // +0x158
    public int PyramidLevels { get; }

    /// <summary>Packed reference (+0x80): 4 halves per pixel, quad order (TR, TL, BL, BR); size <see cref="Wp"/>×<see cref="Hp"/>.</summary>
    public ushort[] PackedRef { get; set; } = null!;
    public int Wp { get; set; }
    public int Hp { get; set; }
    /// <summary>+0xb0: per-16×16-full-res-block mean reciprocal (vec4), size BrW×BrH.</summary>
    public Vec4F[] BlockRcp { get; set; } = null!;
    public int BrW { get; set; }
    public int BrH { get; set; }
    /// <summary>+0xe0: the reference vignetting gain on the (ceil(Wp/8), ceil(Hp/8)) grid.</summary>
    public float[] VignMap { get; set; } = null!;
    public int VmW { get; set; }
    public int VmH { get; set; }
    public List<ushort[]> PackedSrc { get; } = new();            // +0x110
    public List<(int W, int H)> PackedSrcDims { get; } = new();
    public List<Vec2S[]> Flows { get; } = new();                 // +0x138
    public List<(int W, int H)> FlowDims { get; } = new();
    public bool Initialized { get; private set; }

    // Intermediates kept for comparison against the cp.dll reference
    public sealed record SourceFrame(int CamId, string Module, float[] Img, int W, int H, float Black, float White, float Gain, int RedX, int RedY, float[] Neutral, CapturedFrame Frame);
    /// <summary>The float frames in call order: the reference first (gain 1), then the sources.</summary>
    public List<SourceFrame> Frames { get; } = new();
    public ushort[] RefCollapsed { get; private set; } = null!;   // after the sqrt LUT
    public int Wc { get; private set; }
    public int Hc { get; private set; }
    public ushort[][] RefPyramid { get; private set; } = null!;
    public (int W, int H)[] PyramidDims { get; private set; } = null!;
    /// <summary>Collapsed (post-LUT) source of the last flow call, and its pre-LUT version.</summary>
    public ushort[]? LastCollapsedSrc { get; private set; }
    public ushort[]? LastCollapsedSrcPreLut { get; private set; }
    public (int W, int H) LastCollapsedDims { get; private set; }
    public List<RectI> SourceCrops { get; } = new();
    public List<(ushort[] Img, int W, int H)> CollapsedSources { get; } = new();
    public Action<string>? Log { get; set; }

    public int FrameW => Frames[0].W;
    public int FrameH => Frames[0].H;
    public CapturedFrame RefFrame => Frames[0].Frame;

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Construction (ctor 1801f55c0 + initialize 1801f6560)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <param name="refCamId">Reference camera id (`FUN_18020be10(stream)`; A1 = 0).</param>
    /// <param name="cct">(CCT, tint) of the AsShot neutral (`FusionCacheBayer+0xec/+0xf0`).</param>
    /// <summary>
    /// Non-reference source frames: `true` = the per-frame black estimate (`CapturedImage+0xb4`, `FUN_180125d10` with the camera's own profile neutral —
    /// the value Lumen has when the source's pixels are first loaded through the stacked-image / gain-map getters `FUN_18020a6d0`/`FUN_18020b0b0`, i.e. by
    /// the fusion itself in a level-1 render: A5 42.36 / A3 43.17 / A4 42.75 on 00466, cp.dll level-1 reference runs l1b/l1d); `false` = the sensor DB black (42.0) — what Lumen
    /// has in the LEVEL-0 (ResAmp) flow, where the sources were already loaded by an earlier path that does not estimate (cp.dll level-0 reference run l0f: implied black
    /// 42.000 ± 1e-5 for all three sources; the reference keeps 42.51 in both flows). The estimate is a lazy side effect of the first load, so it depends
    /// on the render order, not on the tuning. CLI override: `LUX_FUSION_SRC_BLACK=db|estimate`.
    /// </summary>
    public bool SourceFrameBlackEstimate { get; }

    public PackedBayerFusion(LriFile lri, int refCamId, float cct, float tint, Action<string>? log = null, bool initialize = true, bool sourceFrameBlackEstimate = true)
    {
        _lri = lri; RefCamId = refCamId; Cct = cct; Tint = tint; Log = log;
        // `FUN_180112250` — frames per stack. On a stacked capture every source frame of the LEVEL-1 fusion is the
        // `lt::StackFusion` result of that module (`FUN_18020a6d0` non-null), and `FUN_1801f7a90` then takes its other
        // branch, `out = FUN_1801f8780(stacked, gain, black)`, instead of the ushort hot-pixel/highlight-restore path
        // below. That branch is NOT ported (no cp.dll reference dump exercises it), so refuse rather than fuse the wrong frames.
        // The reference module's own stack fusion IS ported — see `StackFusion`, which is what a level-3 export needs.
        NStack = lri.StackFrames;
        if (NStack >= 2)
            throw new NotSupportedException(
                $"stacked capture ({NStack} frames per module): the level-1 fusion needs `FUN_1801f8780` (the stacked branch of "
              + "FUN_1801f7a90, which consumes lt::StackFusion's float frame per source module) and that is not ported. "
              + "Levels >= 2 render through StackFusion. See a-stack-fusion.md.");
        SourceFrameBlackEstimate = Environment.GetEnvironmentVariable("LUX_FUSION_SRC_BLACK") switch { "db" => false, "estimate" => true, _ => sourceFrameBlackEstimate };
        var refInfo = ModuleFrameInfo.From(lri, ModuleName(refCamId));
        StreamHalfScale = refInfo.IsHalfScale;
        // ctor 1801f55c0: k = stream+0x14 ? 0.25 : 1.0; model = lt::Sensor::modelForGain(analogGain(ref)); A = (aR, aG, aB, aG)·k, B = (bR, bG, bB, bG)·k
        float k = StreamHalfScale ? BitConverter.Int32BitsToSingle(0x3e800000) : One;
        var noise = refInfo.Noise ?? throw new InvalidOperationException("reference frame has no sensor noise model");
        var m = noise.ModelForGain(refInfo.AnalogGain);
        A[0] = m.R.A * k; A[1] = m.G.A * k; A[2] = m.Bl.A * k; A[3] = m.G.A * k;
        B[0] = m.R.B * k; B[1] = m.G.B * k; B[2] = m.Bl.B * k; B[3] = m.G.B * k;
        PyramidLevels = Group(refCamId) == 2 ? 5 : 4 - (StreamHalfScale ? 1 : 0);
        if (initialize) Initialize();
    }

    string ModuleName(int camId)
    {
        foreach (var kv in _lri.Modules) if ((int)kv.Value.Module.Id == camId) return kv.Key;
        throw new InvalidOperationException($"camera id {camId} not in the LRI");
    }

    /// <summary>`FUN_1801f3620`: unique camera ids of the stream in capture order; keep those that are enabled, not the reference,
    /// in the reference's group and colour (red position `(x|y) ≥ 0`).</summary>
    void BuildSourceList()
    {
        SourceIds.Clear();
        int refGroup = Group(RefCamId);
        foreach (var kv in _lri.Modules)
        {
            var mod = kv.Value.Module;
            int id = (int)mod.Id;
            if (!mod.IsEnabled || !mod.SensorDpcOn) continue;          // CapturedImage+0x30 (is_enabled / sensor_dpc_on — both default true; spec §8.1)
            if (id == RefCamId) continue;
            if (Group(id) != refGroup) continue;
            var red = mod.SensorBayerRedOverride;
            if (red is null || (red.X | red.Y) < 0) continue;
            SourceIds.Add(id);
        }
    }

    public void Initialize()
    {
        BuildSourceList();
        var refFrame = BuildFrame(RefCamId, One);
        Frames.Add(refFrame);
        int W = refFrame.W, H = refFrame.H;

        // §3.4 reference: float→ushort, FastCollapse, sqrt LUT, pyramid
        var refU16 = BlockFlow.ToUshort(refFrame.Img, W, H);
        RefCollapsed = BlockFlow.FastCollapse(refU16, W, H, out int wc, out int hc); Wc = wc; Hc = hc;
        BlockFlow.ApplySqrtLut(RefCollapsed);
        RefPyramid = BlockFlow.Pyramid(RefCollapsed, Wc, Hc, PyramidLevels, out var pdims); PyramidDims = pdims;

        // packed reference: × 1/(white − black) then PackBayerImageProtoType
        float sRef = One / (refFrame.White - refFrame.Black);
        PackedRef = Pack(Scale(refFrame.Img, sRef), W, H, out int wp, out int hp); Wp = wp; Hp = hp;

        // +0xe0 vignetting map: (ceil(Wp/8), ceil(Hp/8)) of 1.0, then RemoveVignettingGeneric<float,1>(map, map, rectF = ref image rect, ref, 1.0, false)
        VmW = CeilDiv(Wp, 8); VmH = CeilDiv(Hp, 8);
        VignMap = new float[VmW * VmH];
        Array.Fill(VignMap, One);
        {
            var (cols, rows, grid) = LensShadingKernel.ModelGrid(_lri.Header, refFrame.Frame.Module);
            VignettingFloat.Apply(VignMap, VmW, VmH, new RectF(0f, 0f, (float)W, (float)H), W, H, cols, rows, LensShadingKernel.Transform(grid, One, false));
        }
        var validity = BlockFlow.ValidityFromGainMap(VignMap, VmW, VmH, VmW);

        // per source (initialize L150–330)
        PackedSrc.Clear(); PackedSrcDims.Clear(); Flows.Clear(); FlowDims.Clear(); SourceCrops.Clear();
        foreach (int cam in SourceIds)
        {
            float gain = SourceGain(cam);
            var sf = BuildFrame(cam, gain);
            Frames.Add(sf);
            // CFA-phase crop: (dx, dy, w − dx, h − dy) with dx = red(src).x != red(ref).x
            int dx = sf.RedX != refFrame.RedX ? 1 : 0, dy = sf.RedY != refFrame.RedY ? 1 : 0;
            int cx0 = Math.Max(0, dx), cy0 = Math.Max(0, dy), cx1 = Math.Min(sf.W, sf.W - dx), cy1 = Math.Min(sf.H, sf.H - dy);
            var crop = (cx1 <= cx0 || cy1 <= cy0) ? new RectI(0, 0, 0, 0) : new RectI(cx0, cy0, cx1, cy1);
            SourceCrops.Add(crop);
            int cw = crop.Width, ch = crop.Height;
            var view = new float[cw * ch];
            for (int y = 0; y < ch; y++) Array.Copy(sf.Img, (y + crop.Y0) * sf.W + crop.X0, view, y * cw, cw);

            var u16 = BlockFlow.ToUshort(view, cw, ch);
            var col = BlockFlow.FastCollapse(u16, cw, ch, out int sw, out int sh);
            LastCollapsedSrcPreLut = (ushort[])col.Clone();
            BlockFlow.ApplySqrtLut(col);
            LastCollapsedSrc = col; LastCollapsedDims = (sw, sh);
            CollapsedSources.Add((col, sw, sh));
            var flow = BlockFlow.ComputeFlow(RefPyramid, PyramidDims, col, sw, sh, PyramidLevels, validity, out int fw, out int fh);
            Flows.Add(flow); FlowDims.Add((fw, fh));

            float sSrc = One / (sf.White - sf.Black);
            var packed = Pack(Scale(view, sSrc), cw, ch, out int pw, out int ph);
            PackedSrc.Add(packed); PackedSrcDims.Add((pw, ph));
            Log?.Invoke($"fusion: source cam {cam} gain {gain:R} crop ({crop.X0},{crop.Y0},{crop.X1},{crop.Y1}) flow {fw}x{fh} packed {pw}x{ph}");
        }
        Initialized = true;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // §3.2 per-source float frame FUN_1801f7a90
    // ---------------------------------------------------------------------------------------------------------------------------------
    SourceFrame BuildFrame(int camId, float gain)
    {
        string name = ModuleName(camId);
        var frame = CapturedFrame.Load(_lri, name);
        int w = frame.Width, h = frame.Height;
        var noise = frame.Info.Noise ?? throw new InvalidOperationException($"{name}: no sensor noise model");
        float white = noise.White;                                                                  // CapturedImage+0xb8
        if (frame.Info.HasHotPixelLeakageCalibration) throw new NotSupportedException("HotpixelCalibration::correctHotpixelLeakage is not ported");
        var red = frame.Module.SensorBayerRedOverride;
        int rx = red?.X ?? 0, ry = red?.Y ?? 0;
        var full = new RectI(0, 0, w, h);
        var neutral = NeutralForModule(frame.Module.Id);
        // CapturedImage+0xb4: the per-frame black estimate (`FUN_180125d10`, stream loader `18020b0b0`/`1802095e0`) is computed for EVERY
        // frame with that frame's own camera-profile neutral at the capture (CCT, tint) — not the AsShot neutral `CapturedFrame.Load` uses
        // (identical for the reference on 00466: 42.51; the sources give 42.36 / 43.17 / 42.75 vs 42 with the AsShot neutral — verified
        // against cp.dll's source frames, 2026-08-27).
        float black = noise.Black;
        if (frame.Info.IsColour && frame.Info.Sensor == SensorType.SensorAr1335 && (camId == RefCamId || SourceFrameBlackEstimate))
        {
            var (_, shadow) = CaptureState.SiteStats(frame.Module, frame.Raw, w, h);
            // 18020b0b0 → FUN_18020aad0 builds a SoftISP (manual_temp) for the frame: the estimate's neutral is the ISP one (lambda_21 / xy path)
            black = CaptureState.EstimateFrameBlack(shadow, rx, ry, WhiteBalance.NeutralFromTempTint(Cct, Tint, ProfileOf(frame.Module.Id)), noise.Black);
        }
        // FUN_18039f640 → ImagePatchHotPixels(out, src, redpos, analogGain, sensor, 1.0)
        var lut = noise.SigmaTables(frame.Info.AnalogGain);
        var hp = new ushort[w * h];
        HotPixelKernel.RunInto(frame.Raw, w, h, full, rx, ry, One, lut[0], lut[1], lut[2], hp, w, 0);
        // RestoreHighlightsBayer(hp, redpos, neutral(profile(cam), cct/tint), black, white)
        var hr = new ushort[w * h];
        HighlightRestoreKernel.Run(hp, w, 0, full, hr, w, 0, full, rx, ry, neutral, black, white);
        // FUN_1801216a0: ((float)raw − black) · gain
        var f = new float[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = ((float)hr[i] - black) * gain;
        if (camId == RefCamId)
        {
            Black = black; White = white;
            BlockRcp = BlockMeanRcp(f, w, h, out int bw, out int bh); BrW = bw; BrH = bh;
        }
        // RemoveVignettingGeneric<float,1>(out, out, rectF(0,0,w,h), img, 1.0, false)
        var (cols, rows, grid) = LensShadingKernel.ModelGrid(_lri.Header, frame.Module);
        VignettingFloat.Apply(f, w, h, new RectF(0f, 0f, (float)w, (float)h), w, h, cols, rows, LensShadingKernel.Transform(grid, One, false));
        return new SourceFrame(camId, name, f, w, h, black, white, gain, rx, ry, neutral, frame);
    }

    /// <summary>`FUN_180421170(hwInfo, camId)` → `FUN_18041eea0(profile, cctTint)`: the neutral from the camera's own colour calibration
    /// (falls back to the reference's when the camera has none), i.e. `WhiteBalance.NeutralFromTempTint` on a per-camera profile.
    /// Only the two colour matrices and illuminants of the profile enter the neutral, so the HSV fits are not built.</summary>
    float[] NeutralForModule(CameraID id) => NeutralFromTempTintDirect(Cct, Tint, ProfileOf(id));

    LumenProfile ProfileOf(CameraID id) => CameraProfile(_lri, id);

    /// <summary>`FUN_180421170(hwInfo, camId)`: the camera's own colour-calibration profile (only the illuminant entries; falls back to the
    /// reference camera's when the camera has fewer than two).</summary>
    public static LumenProfile CameraProfile(LriFile lri, CameraID id)
    {
        var entries = ProfileEntries(lri, id);
        if (entries.Count < 2) entries = ProfileEntries(lri, lri.Header.ImageReferenceCamera);
        if (entries.Count < 2) throw new InvalidOperationException("Color calibration must have at least 2 illuminants!");
        IlluminantEntry lo = entries[0], hi = entries[0];
        foreach (var e in entries) { if (e.Cct < lo.Cct) lo = e; if (e.Cct > hi.Cct) hi = e; }
        return new LumenProfile(lo, hi, entries);
    }

    /// <summary>`FUN_18041eea0(profile, out, (cct, tint))` (disasm 18041eeb0–18041ef83): `xy = FUN_1800d0cb0(cct, tint)`;
    /// `M = FUN_1800d13d0(cct, t1, t2, CM1, CM2)` — the matrix at the GIVEN cct (lambda_21 / `NeutralFromXy` re-derive the temperature
    /// from xy); `invY = 1/y; X = x·invY; Z = ((1 − y) − x)·invY; n0 = (M2·Z) + ((M0·X) + M1); den = (M5·Z) + ((M3·X) + M4);
    /// n2 = (Z·M8) + ((X·M6) + M7); g = 1/den; (n0·g, 1, g·n2)`.</summary>
    public static float[] NeutralFromTempTintDirect(float cct, float tint, LumenProfile p)
    {
        var (x, y) = WhiteBalance.CctTintToXy(cct, tint);
        var m = WhiteBalance.MatrixAtTemperature(cct, WhiteBalance.IlluminantCctF(p.Low.InternalIlluminant), WhiteBalance.IlluminantCctF(p.High.InternalIlluminant), p.Low.ColorMatrix, p.High.ColorMatrix);
        float invY = One / y;
        float Zt = (One - y) - x;
        float X = x * invY, Z = Zt * invY;
        float n0 = (m[2] * Z) + ((m[0] * X) + m[1]);
        float den = (m[5] * Z) + ((m[3] * X) + m[4]);
        float n2 = (Z * m[8]) + ((X * m[6]) + m[7]);
        float g = One / den;
        return new[] { n0 * g, One, g * n2 };
    }

    static List<IlluminantEntry> ProfileEntries(LriFile lri, CameraID id)
    {
        var entries = new List<IlluminantEntry>();
        foreach (var cal in Calibration.ForModule(lri.Header, id))
            foreach (var cc in cal.Color)
            {
                int ill = LumenColorTables.InternalIlluminant(cc.Type);
                var (cct, tint) = LumenColorTables.XyToCct(LumenColorTables.IlluminantX[ill], LumenColorTables.IlluminantY[ill]);
                entries.Add(new IlluminantEntry(cc.Type, ill, cct, tint, LumenProfile.M3(cc.ColorMatrix), LumenProfile.M3(cc.ForwardMatrix), null!));
            }
        return entries;
    }

    /// <summary>`FUN_18010fc80(hwInfo, &amp;camId)`: `g = (gain_ref·(float)exp_ref) / (gain_src·(float)exp_src)`; when the source has a
    /// vignetting model, is colour and the reference has one: `g = g · (rb_src / rb_ref)` with `rb = VignettingCharacterization+0x30`
    /// = `relative_brightness` (port note).</summary>
    public float SourceGain(int camId)
    {
        var refM = _lri.Modules[ModuleName(RefCamId)].Module; var srcM = _lri.Modules[ModuleName(camId)].Module;
        float g = (refM.SensorAnalogGain * (float)refM.SensorExposure) / (srcM.SensorAnalogGain * (float)srcM.SensorExposure);
        var vs = VignettingOf(srcM.Id); var vr = VignettingOf(refM.Id);
        var red = srcM.SensorBayerRedOverride;
        if (vs is not null && red is not null && (red.X | red.Y) >= 0 && vr is not null)
            g = g * (RelativeBrightness(vs) / RelativeBrightness(vr));
        return g;
    }

    VignettingCharacterization? VignettingOf(CameraID id)
    {
        VignettingCharacterization? vc = null;
        foreach (var cal in Calibration.ForModule(_lri.Header, id)) if (cal.Vignetting is not null) vc = cal.Vignetting;
        return vc;
    }
    static float RelativeBrightness(VignettingCharacterization vc) => vc.HasRelativeBrightness ? vc.RelativeBrightness : 0f;

    // ---------------------------------------------------------------------------------------------------------------------------------
    // §3.3 reference extras
    // ---------------------------------------------------------------------------------------------------------------------------------
    static int CeilDiv(int v, int d) => v >= 0 ? (v + d - 1) / d : -((-v) / d);
    static int FloorDiv(int v, int d) => v >= 0 ? v / d : -(((-v) + d - 1) / d);

    /// <summary>`FUN_1801d7140`: `(ceil(W/16), ceil(H/16))` vec4 image; per block `inv = 1/(validRows·validCols)`, sum over the 2×2 quads
    /// (rows 0,2,.. &lt; validRows; columns 0,2,.. &lt; validCols) of `rcpps(maxps((p[y][x+1], p[y][x], p[y+1][x], p[y+1][x+1]), 0.1))`,
    /// result `inv · sum` per lane.</summary>
    public static Vec4F[] BlockMeanRcp(float[] p, int W, int H, out int bw, out int bh)
    {
        if (((W | H) & 1) != 0) throw new InvalidOperationException("Bayer image must have even dimensions!");
        bw = CeilDiv(W, 16); bh = CeilDiv(H, 16);
        var o = new Vec4F[bw * bh];
        var tenth = Vector128.Create(Tenth);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int vc = 16 - Math.Max(0, (bx + 1) * 16 - W);
                int vr = 16 - Math.Max(0, by * 16 + 16 - H);
                float inv = One / (float)(vr * vc);
                float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
                if (vr > 0)
                {
                    for (int y = 0; y < vr; y += 2)
                    {
                        int r0 = (by * 16 + y) * W + bx * 16, r1 = r0 + W;
                        for (int x = 0; x < vc; x += 2)
                        {
                            var v = Vector128.Create(p[r0 + x + 1], p[r0 + x], p[r1 + x], p[r1 + x + 1]);
                            v = Sse.Reciprocal(Sse.Max(v, tenth));
                            s0 = s0 + v.GetElement(0); s1 = s1 + v.GetElement(1); s2 = s2 + v.GetElement(2); s3 = s3 + v.GetElement(3);
                        }
                    }
                }
                o[by * bw + bx] = new Vec4F(inv * s0, inv * s1, inv * s2, inv * s3);
            }
        return o;
    }

    /// <summary>`FUN_1801d7600(pos, map)`: mean of the up-to-4 samples (x,y),(x+1,y),(x,y+1),(x+1,y+1) present in the map, literal
    /// guard order of the decomp; 0 when none.</summary>
    public static float Mean2x2(float[] map, int w, int h, int stride, int x, int y)
    {
        float f = 0f; int cnt = 0;
        if ((x | y) >= 0) { f = map[y * stride + x]; cnt = 1; }
        if (y >= 0 && x + 1 < w) { f = f + map[y * stride + x + 1]; cnt++; }
        if (x >= 0 && y + 1 < h) { f = f + map[(y + 1) * stride + x]; cnt++; }
        if (y + 1 < h && x + 1 < w) { f = f + map[(y + 1) * stride + x + 1]; cnt++; }
        else if (cnt == 0) return 0f;
        return f / (float)cnt;
    }

    /// <summary>`StackFusion::lambda_3(pos, img)`: the same 2×2 mean on a vec4 image, `mean = ((1 − c·r)·r + r)·sum` with `r = rcpps(c)`.</summary>
    public static Vector128<float> Mean2x2Vec(Vec4F[] img, int w, int h, int stride, int x, int y)
    {
        var s = Vector128<float>.Zero; int cnt = 0;
        if ((x | y) >= 0) { s = Load(img[y * stride + x]); cnt = 1; }
        if (y >= 0 && x + 1 < w) { s = s + Load(img[y * stride + x + 1]); cnt++; }
        if (x >= 0 && y + 1 < h) { s = s + Load(img[(y + 1) * stride + x]); cnt++; }
        if (y + 1 < h && x + 1 < w) { s = s + Load(img[(y + 1) * stride + x + 1]); cnt++; }
        else if (cnt == 0) return Vector128<float>.Zero;
        var c = Vector128.Create((float)cnt);
        var r = Sse.Reciprocal(c);
        return ((Vector128.Create(One) - c * r) * r + r) * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vector128<float> Load(Vec4F v) => Vector128.Create(v.R, v.G, v.B, v.A);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vec4F Store(Vector128<float> v) => new(v.GetElement(0), v.GetElement(1), v.GetElement(2), v.GetElement(3));

    /// <summary>`initialize::lambda_1` (1801f9450, disasm 1801f9491–1801f94ea): `f = Mean2x2(map)`, `v = Mean2x2Vec(blockRcp)`,
    /// `r = rcpps(v); t = v·r; d = (1 − t)·r; s = (black + r) + d; s = s·A; s = invW·s; s = s + B; s = maxps(s, 1e-5)`;
    /// returns `((f·f)·W2)·s` with `invW = 1/white`, `W2 = white·white`.</summary>
    public Vec4F NoiseFn(int bx, int by)
    {
        float f = Mean2x2(VignMap, VmW, VmH, VmW, bx, by);
        var v = Mean2x2Vec(BlockRcp, BrW, BrH, BrW, bx, by);
        var r = Sse.Reciprocal(v);
        var t = v * r;
        var d = (Vector128.Create(One) - t) * r;
        var s = Vector128.Create(Black) + r;
        s = s + d;
        s = s * Vector128.Create(A[0], A[1], A[2], A[3]);
        s = Vector128.Create(One / White) * s;
        s = s + Vector128.Create(B[0], B[1], B[2], B[3]);
        s = Sse.Max(s, Vector128.Create(Eps));
        float f2 = f * f;
        var o = (Vector128.Create(f2) * Vector128.Create(White * White)) * s;
        return Store(o);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // §3.5 packing / unpacking
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`FUN_1801f8aa0`: `out = in · s` per element.</summary>
    static float[] Scale(float[] src, float s)
    {
        var o = new float[src.Length];
        for (int i = 0; i < o.Length; i++) o[i] = src[i] * s;
        return o;
    }

    /// <summary>`PackBayerImageProtoType&lt;vec4x16f,float&gt;` (1801e6610 / lambda_0 1801e9e90): "src needs to be even."; output `(W/2, H/2)`
    /// of 4 halves `(h(p[2y][2x+1]), h(p[2y][2x]), h(p[2y+1][2x]), h(p[2y+1][2x+1]))` with the software RTZ conversion `FUN_1800e8150`.</summary>
    public static ushort[] Pack(float[] src, int W, int H, out int wp, out int hp)
    {
        if (((W | H) & 1) != 0) throw new InvalidOperationException("src needs to be even.");
        wp = W >> 1; hp = H >> 1;
        var o = new ushort[wp * hp * 4];
        for (int y = 0; y < hp; y++)
        {
            int r0 = (2 * y) * W, r1 = r0 + W;
            for (int x = 0; x < wp; x++)
            {
                int q = (y * wp + x) * 4;
                o[q] = Half16.FromFloat(src[r0 + 2 * x + 1]);
                o[q + 1] = Half16.FromFloat(src[r0 + 2 * x]);
                o[q + 2] = Half16.FromFloat(src[r1 + 2 * x]);
                o[q + 3] = Half16.FromFloat(src[r1 + 2 * x + 1]);
            }
        }
        return o;
    }

    /// <summary>`FUN_1801e67b0`: quads → full-res float Bayer (`out[2y][2x+1] = q0, out[2y][2x] = q1, out[2y+1][2x] = q2, out[2y+1][2x+1] = q3`).</summary>
    public static float[] Unpack(Vec4F[] src, int wp, int hp)
    {
        int W = wp * 2; var o = new float[W * hp * 2];
        for (int y = 0; y < hp; y++)
            for (int x = 0; x < wp; x++)
            {
                var q = src[y * wp + x];
                int r0 = (2 * y) * W + 2 * x, r1 = r0 + W;
                o[r0 + 1] = q.R; o[r0] = q.G; o[r1] = q.B; o[r1 + 1] = q.A;
            }
        return o;
    }

    /// <summary>`FUN_1801e6900`: weight quads → 2×2 of `max(q0..q3)` (`shufpd 1 / maxps` → `(max(q2,q0), max(q3,q1))`, then
    /// `l0 &lt;= l1 ? l1 : l0`).</summary>
    public static float[] UnpackWeight(Vec4F[] src, int wp, int hp)
    {
        int W = wp * 2; var o = new float[W * hp * 2];
        for (int y = 0; y < hp; y++)
            for (int x = 0; x < wp; x++)
            {
                var q = src[y * wp + x];
                float l0 = q.B > q.R ? q.B : q.R, l1 = q.A > q.G ? q.A : q.G;
                float m = l0 <= l1 ? l1 : l0;
                int r0 = (2 * y) * W + 2 * x, r1 = r0 + W;
                o[r0 + 1] = m; o[r0] = m; o[r1] = m; o[r1 + 1] = m;
            }
        return o;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // §4 process FUN_1801f5840 + kernel FUN_1801e6a20
    // ---------------------------------------------------------------------------------------------------------------------------------
    public sealed record ProcessResult(RectI Roi, float[] Out, float[] Weight, Vec4F[] FusedPacked, Vec4F[] WeightPacked, RectI HalfRoi);

    /// <summary>`PackedBayerFusion::process(out, weightOut, refRoi, gain)`: fused full-res float Bayer of the ROI (black re-added, reference
    /// vignetting re-applied) and the weight image.</summary>
    public ProcessResult Process(RectI refRoi, float gain)
    {
        if (!Initialized) throw new InvalidOperationException("PackedBayerFusion::process called before successful initialization!");
        int x0 = refRoi.X0, y0 = refRoi.Y0, x1 = refRoi.X1, y1 = refRoi.Y1;
        if (!(x0 < x1 && y0 < y1)) { x0 = 0; y0 = 0; x1 = Wp * 2; y1 = Hp * 2; }
        if ((((y1 - y0) | (x1 - x0)) & 1) != 0) throw new InvalidOperationException("ref_roi needs to be even.");
        if (((y0 | x0) & 1) != 0) throw new InvalidOperationException("ref_roi needs to be evenly aligned.");
        var roi = new RectI(x0, y0, x1, y1);
        var half = new RectI(x0 >> 1, y0 >> 1, x1 >> 1, y1 >> 1);
        var (fused, wImg, rw, rh) = Merge(half, gain / (float)NStack, White - Black);
        // RemoveVignettingGeneric<vec4x32f,0>(fused, fused, rectF(roi), refImg, m = 0, inverse = true)
        {
            var refFrame = Frames[0];
            var (cols, rows, grid) = LensShadingKernel.ModelGrid(_lri.Header, refFrame.Frame.Module);
            VignettingVec4.Apply(fused, rw, rh, new RectF((float)x0, (float)y0, (float)x1, (float)y1), refFrame.W, refFrame.H, cols, rows, LensShadingKernel.Transform(grid, 0f, true));
        }
        // FUN_1801f5fa0: + black on all four lanes
        var blackV = Vector128.Create(Black);
        for (int i = 0; i < fused.Length; i++) fused[i] = Store(Load(fused[i]) + blackV);
        var outp = Unpack(fused, rw, rh);
        var weight = UnpackWeight(wImg, rw, rh);
        return new ProcessResult(roi, outp, weight, fused, wImg, half);
    }

    /// <summary>The merge kernel `FUN_1801e6a20(out, wOut, refPacked, srcs, flows, noiseFn, halfRoi, cN, range)` (§4.1).</summary>
    public (Vec4F[] Fused, Vec4F[] Weight, int W, int H) Merge(RectI halfRoi, float cN, float range)
    {
        if (PackedSrc.Count != Flows.Count) throw new InvalidOperationException("Number of src_images must match number of flow_fields!");
        cN = cN * NoiseMul;
        int rx0 = halfRoi.X0, ry0 = halfRoi.Y0, rx1 = halfRoi.X1, ry1 = halfRoi.Y1;
        if (!(rx0 < rx1 && ry0 < ry1)) { rx0 = 0; ry0 = 0; rx1 = Wp; ry1 = Hp; }
        int rw = rx1 - rx0, rh = ry1 - ry0;
        // ref = half→float of the ROI view (exact), then × range per lane
        var refImg = new Vec4F[rw * rh];
        var rangeV = Vector128.Create(range);
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int p = ((y + ry0) * Wp + (x + rx0)) * 4;
                var v = Vector128.Create(Half16.ToFloat(PackedRef[p]), Half16.ToFloat(PackedRef[p + 1]), Half16.ToFloat(PackedRef[p + 2]), Half16.ToFloat(PackedRef[p + 3]));
                refImg[y * rw + x] = Store(v * rangeV);
            }
        var wImg = new Vec4F[rw * rh];
        var hann = BayerMerge.Hann;
        var R = new Vec4F[256]; var Rw = new Vec4F[256]; var S = new Vec4F[256]; var acc = new Vec4F[256];
        var oneV = Vector128.Create(One);
        int byStart = FloorDiv(ry0, 8) * 8 - 8, byEnd = CeilDiv(ry1, 8) * 8 + 8;
        int bxStart = FloorDiv(rx0, 8) * 8 - 8, bxEnd = CeilDiv(rx1, 8) * 8 + 8;
        int nSrc = PackedSrc.Count;
        for (int by = byStart; by < byEnd; by += 8)
        {
            int bpy = by / 8;                    // truncating division (decomp: (v + (v<0 ? 7 : 0)) >> 3)
            int fyc = Math.Max(bpy, 0);
            for (int bx = bxStart; bx < bxEnd; bx += 8)
            {
                // B ∩ refPacked.rect (0,0,Wp,Hp) non-empty
                if (!(Math.Max(bx, 0) < Math.Min(bx + 16, Wp) && Math.Max(by, 0) < Math.Min(by + 16, Hp))) continue;
                int bpx = bx / 8;
                int fxc = Math.Max(bpx, 0);
                var noise = NoiseFn(bpx, bpy);
                BayerMerge.ExtractBlock(PackedRef, Wp, Hp, bx, by, range, R);
                Array.Copy(R, Rw, 256);
                BayerWavelet.Forward(Rw);
                Array.Clear(acc);
                var cnt = oneV; var q2 = Vector128<float>.Zero;
                for (int i = 0; i < nSrc; i++)
                {
                    var (fw, fh) = FlowDims[i];
                    int fy = Math.Min(fyc, fh - 1), fx = Math.Min(fxc, fw - 1);
                    var fl = Flows[i][fy * fw + fx];
                    int sx = bx + fl.X, sy = by + fl.Y;
                    var (sw, sh) = PackedSrcDims[i];
                    if (!(Math.Max(sx, 0) < Math.Min(sx + 16, sw) && Math.Max(sy, 0) < Math.Min(sy + 16, sh)))
                    {
                        for (int k = 0; k < 256; k++) acc[k] = Store(Load(acc[k]) + Load(R[k]));
                        cnt = cnt + oneV;
                        continue;
                    }
                    BayerMerge.ExtractBlock(PackedSrc[i], sw, sh, sx, sy, range, S);
                    BayerWavelet.Forward(S);
                    var nz = new Vec4F(noise.R * cN, noise.G * cN, noise.B * cN, noise.A * cN);
                    var q = Load(BayerMerge.Shrink(S, Rw, nz));
                    BayerWavelet.Inverse(S);
                    for (int k = 0; k < 256; k++) acc[k] = Store(Load(acc[k]) + Load(S[k]));
                    cnt = (cnt + oneV) - q;
                    q2 = q2 + q * q;
                }
                BayerMerge.AddHann(refImg, rw, rh, bx - rx0, by - ry0, acc, hann);
                var s = cnt * cnt + q2;
                BayerMerge.AddHannScalar(wImg, rw, rh, bx - rx0, by - ry0, Store(s), hann);
            }
        }
        float N = (float)(nSrc + 1);
        float rN = BayerMerge.RcpNR(N);
        for (int i = 0; i < refImg.Length; i++) { var v = refImg[i]; refImg[i] = new Vec4F(v.R * rN, v.G * rN, v.B * rN, v.A * rN); }
        long n2 = (long)(nSrc + 1) * (nSrc + 1);
        float rN2 = BayerMerge.RcpNR((float)(ulong)n2);
        for (int i = 0; i < wImg.Length; i++) { var v = wImg[i]; wImg[i] = new Vec4F(v.R * rN2, v.G * rN2, v.B * rN2, v.A * rN2); }
        return (refImg, wImg, rw, rh);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // §5 FusionCacheBayer: tiles, weight → uint8, STD plane, render geometry
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`FUN_1802092b0`: `v = (int)(w·256) − 1; if (v &lt; 1) v = 0; (byte)v`.</summary>
    public static byte[] WeightToByte(float[] w)
    {
        var o = new byte[w.Length];
        for (int i = 0; i < w.Length; i++) { int v = (int)(w[i] * C256) - 1; if (v < 1) v = 0; o[i] = unchecked((byte)v); }
        return o;
    }

    /// <summary>`FUN_180507b20` (nStack &lt; 2): `k = s == 0 ? 0 : ((t·r) + (−3))·((−0.5)·t)` with `r = rsqrtss(s), t = s·r`
    /// (disasm 180507f4c–180507f78); `std[i] = DAT_1806b5110[w8[i]]·k` (`FUN_180209010`).</summary>
    public static float StdK(float noiseScale)
    {
        float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(noiseScale)).ToScalar();
        float t = noiseScale * r;
        float k = ((t * r) + MinusThree) * (MinusHalf * t);
        return noiseScale == 0f ? 0f : k;
    }

    public static float[] StdPlane(byte[] w8, float noiseScale)
    {
        float k = StdK(noiseScale);
        var o = new float[w8.Length];
        for (int i = 0; i < o.Length; i++) o[i] = StdTable[w8[i]] * k;
        return o;
    }

    /// <summary>`FUN_18050cbf0(profile, analogGain)`: halo 17 / 33 / 65 / 129 for gain ≤ 2 / ≤ 4 / ≤ 6 / else.</summary>
    public static int Halo(float analogGain) => !(Two < analogGain) ? 0x11 : !(Four < analogGain) ? 0x21 : !(Six < analogGain) ? 0x41 : 0x81;

    /// <summary>`render` (180507b20) grown rect: `hl = halo&gt;&gt;1, hr = max(halo−1,0)&gt;&gt;1`, grow by `min(x0,hl)` left/top and
    /// `min(W−x1, hr)` right/bottom.</summary>
    public static RectI GrownRect(RectI r, int halo, int W, int H)
    {
        int hl = halo >> 1, hr = Math.Max(halo - 1, 0) >> 1;
        int gl = Math.Min(r.X0, hl), gt = Math.Min(r.Y0, hl), gr = Math.Min(W - r.X1, hr), gb = Math.Min(H - r.Y1, hr);
        return new RectI(r.X0 - gl, r.Y0 - gt, r.X1 + gr, r.Y1 + gb);
    }

    /// <summary>`TileCache` grid of the fusion caches (`FUN_1804be9f0`, tile 512×512): `n = max(1, (256 + extent)/512)`; the last tile absorbs
    /// the remainder.</summary>
    public const int TileSize = 512;
    public static (int Nx, int Ny) TileGrid(int W, int H) => (Math.Max(1, (TileSize / 2 + W) / TileSize), Math.Max(1, (TileSize / 2 + H) / TileSize));
    public static RectI TileRect(int tx, int ty, int W, int H)
    {
        var (nx, ny) = TileGrid(W, H);
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = tx == nx - 1 ? Math.Min(W, x0 + 2 * TileSize) - x0 : TileSize;
        int h = ty == ny - 1 ? Math.Min(H, y0 + 2 * TileSize) - y0 : TileSize;
        return new RectI(x0, y0, x0 + w, y0 + h);
    }

    readonly Dictionary<(int, int), (RectI Rect, byte[] W8)> _weightTiles = new();

    /// <summary>`FusionCacheBayer::lambda_0`: the uint8 weight tile of `process(tile rect, 1.0)`.</summary>
    public (RectI Rect, byte[] W8) WeightTile(int tx, int ty)
    {
        if (_weightTiles.TryGetValue((tx, ty), out var t)) return t;
        var rect = TileRect(tx, ty, FrameW, FrameH);
        var pr = Process(rect, One);
        t = (rect, WeightToByte(pr.Weight));
        _weightTiles[(tx, ty)] = t;
        Log?.Invoke($"fusion: weight tile ({tx},{ty}) {rect.Width}x{rect.Height}");
        return t;
    }

    /// <summary>`TileCache&lt;uint8&gt;::renderROI` over the grown rect (frame pixels).</summary>
    public byte[] RenderWeight8(RectI grown)
    {
        int W = FrameW, H = FrameH;
        if (grown.X0 < 0 || grown.Y0 < 0 || grown.X1 > W || grown.Y1 > H) throw new ArgumentException("Requested ROI is out-of-bounds!");
        var (nx, ny) = TileGrid(W, H);
        int tx0 = Math.Min(grown.X0 / TileSize, nx - 1), tx1 = Math.Min((grown.X1 - 1) / TileSize, nx - 1), ty0 = Math.Min(grown.Y0 / TileSize, ny - 1), ty1 = Math.Min((grown.Y1 - 1) / TileSize, ny - 1);
        int rw = grown.Width, rh = grown.Height; var o = new byte[rw * rh];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var (rect, w8) = WeightTile(tx, ty);
                var c = rect.Intersect(grown);
                for (int y = c.Y0; y < c.Y1; y++)
                    for (int x = c.X0; x < c.X1; x++) o[(y - grown.Y0) * rw + (x - grown.X0)] = w8[(y - rect.Y0) * rect.Width + (x - rect.X0)];
            }
        return o;
    }
}

/// <summary>`lt::A::RemoveVignettingGeneric&lt;float,1&gt;` (outer `180130760`, cell rects `FUN_180134090`, lambda `18013dc80`) for a float
/// tile of <paramref name="tileW"/>×<paramref name="tileH"/> covering the frame rectangle <paramref name="fr"/> (frame units): the same
/// geometry as `LensShadingKernel.Apply`, single lane, per-pixel gain in double rounded to float, `out = gain·in`.</summary>
public static class VignettingFloat
{
    public static void Apply(float[] img, int tileW, int tileH, RectF fr, int frameW, int frameH, int cols, int rows, float[] grid)
    {
        Cells(tileW, tileH, fr, frameW, frameH, cols, rows, out float hspace, out float vspace, out float xoff, out float yoff, out int ox, out int oy, out int col0, out int row0, out int col1, out int row1);
        float invH = 1f / MathF.Floor(hspace), invV = 1f / MathF.Floor(vspace);
        for (int row = row0; row < row1; row++)
        {
            int cy0 = Math.Max((int)((float)row * vspace) - oy, 0), cy1 = Math.Min((int)(vspace * (float)(row + 1)) - oy, tileH);
            for (int col = col0; col < col1; col++)
            {
                int cx0 = Math.Max((int)((float)col * hspace) - ox, 0), cx1 = Math.Min((int)(hspace * (float)(col + 1)) - ox, tileW);
                if (cy0 >= cy1 || cx0 >= cx1) continue;
                float g00 = G(grid, row * cols + col), g01 = G(grid, row * cols + col + 1), g10 = G(grid, (row + 1) * cols + col), g11 = G(grid, (row + 1) * cols + col + 1);
                float dyoff = yoff - (float)row * vspace, dxoff = xoff - (float)col * hspace;
                for (int y = cy0; y < cy1; y++)
                {
                    float fy = ((float)y + dyoff) * invV;
                    float left = fy * (g10 - g00) + g00, right = fy * (g11 - g01) + g01;
                    float slope = (right - left) * invH;
                    double slopeD = slope, leftD = left, xdD = dxoff;
                    int rowOff = y * tileW;
                    for (int x = cx0; x < cx1; x++)
                    {
                        float gain = (float)(((double)x + xdD) * slopeD + leftD);
                        img[rowOff + x] = gain * img[rowOff + x];
                    }
                }
            }
        }
    }

    /// <summary>Lumen indexes the node grid without clamping; keep the port's rule for the (never hit on a full-frame rect) overflow.</summary>
    internal static float G(float[] grid, int i) => i >= 0 && i < grid.Length ? grid[i] : grid[^1];

    /// <summary>Outer geometry (180130760 / 180131010): `sx = tileW/(x1−x0)`, `xoff = x0·sx`, `hspace = frameW·sx/(cols−1)`, `col0 = (int)(xoff·(1/hspace))`,
    /// `col1 = (int)ceil(sx·x1·(1/hspace))` (roundss 10), likewise vertically.</summary>
    internal static void Cells(int tileW, int tileH, RectF fr, int frameW, int frameH, int cols, int rows, out float hspace, out float vspace, out float xoff, out float yoff, out int ox, out int oy, out int col0, out int row0, out int col1, out int row1)
    {
        float sx = (float)tileW / (fr.X1 - fr.X0), sy = (float)tileH / (fr.Y1 - fr.Y0);
        xoff = fr.X0 * sx; yoff = fr.Y0 * sy;
        vspace = ((float)frameH * sy) / (float)(rows - 1);
        hspace = ((float)frameW * sx) / (float)(cols - 1);
        ox = (int)xoff; oy = (int)yoff;
        col0 = (int)(xoff * (1f / hspace)); row0 = (int)(yoff * (1f / vspace));
        col1 = (int)MathF.Ceiling(sx * fr.X1 * (1f / hspace)); row1 = (int)MathF.Ceiling(sy * fr.Y1 * (1f / vspace));
    }
}

/// <summary>`lt::A::RemoveVignettingGeneric&lt;vec4x32f,0&gt;` (outer `180131010`, lambda `18013dfc0`, disasm 18013e035–18013e221): the
/// vec4 variant used by `process()` with `m = 0, inverse = true` (grid `1/g`); identical geometry, per-pixel gain in double
/// (`cvtsi2sd x; addsd xdD; mulsd slope; addsd left; cvtsd2ss`) and **all four lanes** multiplied (`mulps`).</summary>
public static class VignettingVec4
{
    public static void Apply(Vec4F[] img, int tileW, int tileH, RectF fr, int frameW, int frameH, int cols, int rows, float[] grid)
    {
        VignettingFloat.Cells(tileW, tileH, fr, frameW, frameH, cols, rows, out float hspace, out float vspace, out float xoff, out float yoff, out int ox, out int oy, out int col0, out int row0, out int col1, out int row1);
        float invH = 1f / MathF.Floor(hspace), invV = 1f / MathF.Floor(vspace);
        for (int row = row0; row < row1; row++)
        {
            int cy0 = Math.Max((int)((float)row * vspace) - oy, 0), cy1 = Math.Min((int)(vspace * (float)(row + 1)) - oy, tileH);
            for (int col = col0; col < col1; col++)
            {
                int cx0 = Math.Max((int)((float)col * hspace) - ox, 0), cx1 = Math.Min((int)(hspace * (float)(col + 1)) - ox, tileW);
                if (cy0 >= cy1 || cx0 >= cx1) continue;
                float g00 = VignettingFloat.G(grid, row * cols + col), g01 = VignettingFloat.G(grid, row * cols + col + 1), g10 = VignettingFloat.G(grid, (row + 1) * cols + col), g11 = VignettingFloat.G(grid, (row + 1) * cols + col + 1);
                float dyoff = yoff - (float)row * vspace, dxoff = xoff - (float)col * hspace;
                for (int y = cy0; y < cy1; y++)
                {
                    float fy = ((float)y + dyoff) * invV;
                    float left = fy * (g10 - g00) + g00, right = fy * (g11 - g01) + g01;
                    float slope = (right - left) * invH;
                    double slopeD = slope, leftD = left, xdD = dxoff;
                    int rowOff = y * tileW;
                    for (int x = cx0; x < cx1; x++)
                    {
                        float gain = (float)(((double)x + xdD) * slopeD + leftD);
                        ref var p = ref img[rowOff + x];
                        p.R = gain * p.R; p.G = gain * p.G; p.B = gain * p.B; p.A = gain * p.A;
                    }
                }
            }
        }
    }
}
