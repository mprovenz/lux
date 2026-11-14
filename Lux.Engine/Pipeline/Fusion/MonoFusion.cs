using Lux.Engine.Imaging;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ltpb;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Isp.Stages;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// `lt::MonoFusion` (Lumen 2.3; ctor, `initialize`, render side and merge kernel). Created by `FusionCacheBayer` when the reference camera's group holds
/// a mono module (red position (−1,−1), e.g. A2): the fused colour Bayer is rendered through this object's own SoftISP into camera-space
/// RGB, its luma `L = (c·rgb)·range + black` is merged with the mono frame(s) in a float 16×16 wavelet domain (full resolution, 5-level flow) and
/// the merged luma replaces the luma component of the RGB through the basis `M` / `N = M⁻¹`. Float arithmetic follows Lumen's operation order;
/// `rcpss`/`rsqrtss` approximations use the hardware instructions.
/// </summary>
public sealed class MonoFusion
{
    static readonly float One = BitConverter.Int32BitsToSingle(0x3f800000);
    static readonly float MinusOne = BitConverter.Int32BitsToSingle(unchecked((int)0xbf800000));
    static readonly float Third = BitConverter.Int32BitsToSingle(0x3eaaaaab);
    static readonly float Thirty = BitConverter.Int32BitsToSingle(0x41f00000);
    static readonly float C256 = BitConverter.Int32BitsToSingle(0x43800000);
    static readonly float GainTol = BitConverter.Int32BitsToSingle(0x3a83126f);                   // 0.001
    static readonly float HalfF = BitConverter.Int32BitsToSingle(0x3f000000);
    static readonly float Max4095 = BitConverter.Int32BitsToSingle(0x457ff000);
    static readonly float MinusHalf = BitConverter.Int32BitsToSingle(unchecked((int)0xbf000000));
    static readonly float MinusThree = BitConverter.Int32BitsToSingle(unchecked((int)0xc0400000));

    /// <summary>Luma coefficients (c0, c1, c2) and the mono/colour
    /// sensitivity ratio c3 per sensor type.</summary>
    public static (float C0, float C1, float C2, float C3) LumaCoefficients(SensorType t) => t switch
    {
        SensorType.SensorAr1335 or SensorType.SensorAr1335Mono => (BitConverter.Int32BitsToSingle(0x3e5cb924), BitConverter.Int32BitsToSingle(0x3edd5758), BitConverter.Int32BitsToSingle(0x3eb44c16), BitConverter.Int32BitsToSingle(0x40145faf)),
        SensorType.SensorImx386 or SensorType.SensorImx386Mono => (BitConverter.Int32BitsToSingle(0x3e9e39b4), BitConverter.Int32BitsToSingle(0x3e95ca4b), BitConverter.Int32BitsToSingle(0x3ecbfc22), BitConverter.Int32BitsToSingle(0x40351eb8)),
        _ => throw new InvalidOperationException("Unexpected sensor type!"),
    };

    readonly LriFile _lri;
    readonly bool _flag;                               // kernel variant flag; 0 on the desktop
    public int RefCamId { get; }
    public CapturedFrame RefFrame { get; }
    public SoftIsp Isp { get; }
    public IspStats? Stats { get; private set; }
    public float Range { get; }
    public float BlackRef { get; }
    public float WhiteRef { get; }
    public float C0 { get; } public float C1 { get; } public float C2 { get; } public float C3 { get; }
    public float[] M { get; } = new float[9];
    public float[] N { get; } = new float[9];
    public float ValidityScale { get; }
    public float[] Neutral { get; } = { MinusOne, MinusOne, MinusOne };
    public string DemosaicType { get; }
    public string CrossTalkType { get; }
    public bool Initialized { get; private set; }
    /// <summary>Progress: <see cref="Initialize"/> ticks the caller's current phase <see cref="InitializeTicks"/> times (it runs inside the level-0 inputs).</summary>
    public ProgressReporter Progress { get; set; } = ProgressReporter.None;
    public const int InitializeTicks = 3;
    /// <summary>The phase those ticks count in; outside it (a lazy first render) they are dropped.</summary>
    public string ProgressPhase { get; set; } = "level-0 inputs";
    public Action<string>? Log { get; set; }

    // initialize() products
    public List<int> MonoIds { get; } = new();
    public int ColourCount { get; private set; }       // nColour (incl. the reference)
    public float[] VignMap { get; private set; } = null!;   // W×H reference vignetting gain
    public float W0 { get; private set; }
    public float Scale { get; private set; }
    public float NoiseA { get; private set; }
    public float NoiseB { get; private set; }
    public float BlackMono { get; private set; }
    public float WhiteMono { get; private set; }
    public Func<float, float, float> WeightFn { get; private set; } = null!;   // lambda_0
    public List<float[]> Sources { get; } = new();     // W×H float mono frames
    public List<Vec2S[]> Flows { get; } = new();
    public List<(int W, int H)> FlowDims { get; } = new();
    public List<float> SourceGains { get; } = new();   // gr per source (diagnostic)
    public int PyramidLevels { get; }
    public int Width => RefFrame.Width;
    public int Height => RefFrame.Height;
    /// <summary>Diagnostics of the last <see cref="Initialize"/>: the demosaicked reference luma (`Lref`, W×H) and the last flow-reference ushort image.</summary>
    public float[]? RefLuma { get; private set; }
    /// <summary>The stream's per-module `lt::StackFusion` results — set on a stacked capture; null (single-frame) otherwise.</summary>
    public StackProvider? Stacks { get; set; }
    public ushort[]? LastFlowRef16 { get; private set; }
    public ushort[]? LastMono16 { get; private set; }

    /// <param name="rowExtra">The 6th value of the sensor-tuning row.</param>
    /// <param name="flag">Selects the kernel variant (0 = the desktop case).</param>
    public MonoFusion(LriFile lri, int refCamId, RendererProfile profile, float rowExtra, bool flag, int nStack = 1, Action<string>? log = null)
    {
        _lri = lri; RefCamId = refCamId; _flag = flag; Log = log;
        DemosaicType = RendererProfiles.DemosaicType(profile);
        CrossTalkType = "ir_correction";
        RefFrame = CapturedFrame.Load(lri, ModuleName(refCamId));
        var noise = RefFrame.Info.Noise ?? throw new InvalidOperationException("reference frame has no sensor noise model");
        // the per-frame black estimate and the white level of the reference sensor
        BlackRef = float.IsNaN(RefFrame.Info.FrameBlack) ? noise.Black : RefFrame.Info.FrameBlack;
        WhiteRef = noise.White;
        Range = WhiteRef - BlackRef;
        (C0, C1, C2, C3) = LumaCoefficients(RefFrame.Info.Sensor);
        // M = basis((c0, c1, c2)); row 0 := (c0, c1, c2); N = Invert3(M)
        Basis(C0, C1, C2, M);
        M[0] = C0; M[1] = C1; M[2] = C2;
        Invert3(M, N);
        Isp = new SoftIsp(Tuning.LumenDefaults(), Color.LumenProfile.Compute(lri));
        // ValidityScale = clamp((gain + (−1))·(1/3), 0, 1)·30 + 30
        float s = (RefFrame.Info.AnalogGain + MinusOne) * Third;
        float sc = 0f; if (0f <= s) sc = s; if (One <= sc) sc = One;
        ValidityScale = sc * Thirty + Thirty;
        PyramidLevels = PackedBayerFusion.Group(refCamId) == 2 ? 6 : 5 - (RefFrame.Info.IsHalfScale ? 1 : 0);
        Scale = rowExtra / (float)nStack;                          // rowExtra / frames per stack
    }

    string ModuleName(int camId)
    {
        foreach (var kv in _lri.Modules) if ((int)kv.Value.Module.Id == camId) return kv.Key;
        throw new InvalidOperationException($"camera id {camId} not in the LRI");
    }

    /// <summary>The neutral used by the own ISP (`manual_color`) and the camera-space multiply (set from `FusionCacheBase::setNeutral`).</summary>
    public void SetNeutral(float[] neutral) { Neutral[0] = neutral[0]; Neutral[1] = neutral[1]; Neutral[2] = neutral[2]; }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // basis and 3×3 inverse
    // ---------------------------------------------------------------------------------------------------------------------------------
    static float RsqrtNRs(float n) { float r = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(n)).ToScalar(); float t = n * r; return n == 0f ? 0f : ((t * r) + MinusThree) * ((t) * MinusHalf); }

    /// <summary>The basis: row0 = v·rcpNR(rsqrtNR(|v|²)), row1 = (−b·rcpNR(k2), (a+c)·k2, −b·rcpNR(k2)) with k2 = rsqrtNR(2b² + (a+c)²),
    /// row2 = (v × (−b, a+c, −b))·rsqrtNR(|·|²) (no zero guard). Lumen's exact operation sequence.</summary>
    public static void Basis(float a, float b, float c, float[] o)
    {
        float s = c + a;
        float nb2 = b * (-b);                 // b·(−b)
        float amc = (a - c) * b;
        float sa = s * a;
        float aabb = a * a + b * b;
        float ss = s * s;
        float n2 = (b * b + b * b) + ss;      // 2b² + s² (b² computed once, then added to itself)
        float r2 = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(n2)).ToScalar();
        float t2 = n2 * r2;
        float t2r = t2 * r2;                  // n2·r2·r2
        float x4 = (r2 * MinusHalf) * s;
        float sc = s * c;
        float z = sa - nb2;                   // s·a − (−b²)
        float x = nb2 - sc;                   // −b² − s·c
        float n1 = c * c + aabb;
        float r1 = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(n1)).ToScalar();
        float t1 = n1 * r1;
        float h1 = t1 * MinusHalf;
        float k1 = ((t1 * r1) + MinusThree) * h1;
        if (n1 == 0f) k1 = 0f;
        float h2 = t2 * MinusHalf;            // (n2·r2)·(−0.5)
        float m2 = t2r + MinusThree;
        float k2 = h2 * m2;
        if (n2 == 0f) k2 = 0f;
        // q = rcpps((k1,k1,k1,k2)); q = ((1 − k·q)·q) + q; row = q ⊙ (a, b, c, −b)
        var kv = Vector128.Create(k1, k1, k1, k2);
        var q0 = IntelApprox.Reciprocal(kv);
        var q = ((Vector128.Create(One) - kv * q0) * q0) + q0;
        var row = q * Vector128.Create(a, b, c, -b);
        float x4f = x4 * m2;                  // ((r2·(−0.5))·s)·(n2 r2² − 3) = s·k2
        float n3 = z * z + (amc * amc + x * x);
        float r3 = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(n3)).ToScalar();
        float m3 = (n3 * r3) * r3 + MinusThree;
        float k3 = (r3 * MinusHalf) * m3;
        o[0] = row.GetElement(0); o[1] = row.GetElement(1); o[2] = row.GetElement(2);
        o[3] = row.GetElement(3); o[4] = x4f; o[5] = row.GetElement(3);
        o[6] = x * k3; o[7] = amc * k3; o[8] = k3 * z;
    }

    /// <summary>Adjugate / determinant, machine association.</summary>
    public static void Invert3(float[] m, float[] o)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8];
        float c0 = m8 * m4 - m5 * m7;
        float c1 = m3 * m8 - m6 * m5;
        float c2 = m3 * m7 - m6 * m4;
        float inv = One / ((c2 * m2 + c0 * m0) - c1 * m1);
        o[0] = c0 * inv;
        o[1] = (m2 * m7 - m1 * m8) * inv;
        o[2] = (m1 * m5 - m2 * m4) * inv;
        o[3] = -(c1 * inv);
        o[4] = (m8 * m0 - m2 * m6) * inv;
        o[5] = (m2 * m3 - m5 * m0) * inv;
        o[6] = c2 * inv;
        o[7] = (m6 * m1 - m7 * m0) * inv;
        o[8] = (m4 * m0 - m3 * m1) * inv;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // initialize
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>The own ISP's tuning (defaults + the listed keys) and its Stats for the reference capture.</summary>
    public void ConfigureOwnIsp() => ConfigureIsp();
    /// <summary>Diagnostic: replace the own ISP's Stats IR blend (spatial IR-correction experiments).</summary>
    public void OverrideIrBlend(float blend) { var st = Stats!; Stats = new IspStats { Neutral = st.Neutral, Cct = st.Cct, Tint = st.Tint, NeutralXy = st.NeutralXy, IrBlend = blend, SensorBlack = st.SensorBlack, SensorWhite = st.SensorWhite, Noise = st.Noise, CameraToOutput = st.CameraToOutput, Profile = st.Profile, NoiseSigma = st.NoiseSigma }; }
    void ConfigureIsp()
    {
        if (Neutral[0] <= 0f || Neutral[1] <= 0f || Neutral[2] <= 0f) throw new InvalidOperationException("Neutral color not set yet");
        Isp.Set("hot_pixel_removal.type", "none").Set("tone_mapping.type", "none").Set("color_correction.type", "none").Set("output.color_space", "none")
           .Set("demosaicking.type", DemosaicType).Set("auto_white_balance.type", "manual_color")
           .Set("auto_white_balance.neutral_color", new[] { (double)Neutral[0], Neutral[1], Neutral[2] })
           .Set("cross_talk_correction.type", CrossTalkType);
        if (RefFrame.Info.IsHalfScale) Isp.Set("bayer_phase_fix.type", "default");
        Isp.Set("lens_shading.type", "default");
        Stats = Isp.ComputeStats(RefFrame);
        Log?.Invoke($"mono own ISP stats: neutral ({Stats.Neutral[0]:R},{Stats.Neutral[1]:R},{Stats.Neutral[2]:R}) xy ({Stats.NeutralXy.X:R},{Stats.NeutralXy.Y:R}) irBlend {Stats.IrBlend:R} black {Stats.SensorBlack:R} white {Stats.SensorWhite:R}");
    }

    /// <summary>Diagnostic: the own ISP on a fused Bayer image (grown rect), returning the RGB after stage <paramref name="stageIndex"/> (2 = demosaic).</summary>
    public Image<Vec4F>? RunOwnIsp(RectI grown, float[] fused, int stageIndex)
    {
        Image<Vec4F>? got = null;
        Isp.ProcessBayerFloat(RefFrame, Stats!, new Image<float>(grown, fused, grown.Width, 0), null, grown, 5, null, (i, st, p) => { if (i == stageIndex && p.Rgb is not null) got = p.Rgb.Copy(); });
        return got;
    }

    public void Initialize()
    {
        if (Initialized) throw new InvalidOperationException("Called MonoFusion::initialize() twice!");
        MonoIds.Clear(); Sources.Clear(); Flows.Clear(); FlowDims.Clear(); SourceGains.Clear();
        int refGroup = PackedBayerFusion.Group(RefCamId), nColour = 0;
        foreach (var kv in _lri.Modules)
        {
            var mod = kv.Value.Module; int id = (int)mod.Id;
            if (!mod.IsEnabled || !mod.SensorDpcOn) continue;
            if (PackedBayerFusion.Group(id) != refGroup) continue;
            var red = mod.SensorBayerRedOverride;
            if (red is not null && (red.X | red.Y) < 0) MonoIds.Add(id); else nColour++;
        }
        ColourCount = nColour;
        if (MonoIds.Count == 0) throw new InvalidOperationException("Gray sensor does not exist.");
        ConfigureIsp();
        int W = Width, H = Height;
        // vignetting map: 1.0 image → RemoveVignettingGeneric<float,1>(rectF(0,0,W,H), ref, 1.0, false)
        VignMap = new float[W * H]; Array.Fill(VignMap, One);
        var (cols, rows, grid) = LensShadingKernel.ModelGrid(_lri.Header, RefFrame.Module);
        VignettingFloat.Apply(VignMap, W, H, new RectF(0f, 0f, (float)W, (float)H), W, H, cols, rows, LensShadingKernel.Transform(grid, One, false));
        float nMono = (float)MonoIds.Count, nCol = (float)nColour, c3 = C3;
        float w0 = nCol / (c3 * nMono + nCol), w1 = One - w0;
        W0 = w0;
        Scale = (nCol / c3 + One) * Scale;
        float invN = One / nMono, K = (w1 * w1 * nCol) / (nMono * nMono * c3);
        WeightFn = (cnt, q2) => { float t = (w1 * cnt) * invN + w0; return K * q2 + t * t; };
        // first mono capture: noise model at its gain, A/B = channel R, scaled by 1/(c3·nColour)
        var m0 = CapturedFrame.Load(_lri, ModuleName(MonoIds[0]));
        var mn = m0.Info.Noise ?? throw new InvalidOperationException($"{ModuleName(MonoIds[0])}: no sensor noise model");
        var model = mn.ModelForGain(m0.Info.AnalogGain);
        float k = One / (c3 * nCol);
        NoiseA = model.R.A * k; NoiseB = k * model.R.B;
        BlackMono = mn.Black; WhiteMono = mn.White;
        // reference RGB: (raw − black)·rcpss(range) → DemosaickLightV1<0,0> with neutral (1,1,1)
        float rcpRange = IntelApprox.ReciprocalScalar(Vector128.CreateScalar(Range)).ToScalar();
        var norm = new float[W * H];
        // The reference frame: the stacked frame when there is one, else the raw frame (ushort → float), through
        // `(x − black_ref)·rcpss(range)`; on a stacked capture the input is the module's
        // lt::StackFusion float frame through the same kernel
        var stackedRef = Stacks?.GetIfStacked(ModuleName(RefCamId));
        if (stackedRef is null) { var raw = RefFrame.Raw; for (int i = 0; i < norm.Length; i++) norm[i] = ((float)raw[i] - BlackRef) * rcpRange; }
        else { var sf = stackedRef.Fused; for (int i = 0; i < norm.Length; i++) norm[i] = (sf[i] - BlackRef) * rcpRange; }
        var red0 = RefFrame.Module.SensorBayerRedOverride; int rx = red0?.X ?? 0, ry = red0?.Y ?? 0;
        var rgb = new Vec4F[W * H];
        DemosaicLightV1.Run(norm, W, H, new RectI(0, 0, W, H), rx, ry, new[] { One, One, One }, rgb);
        norm = null!;
        // Lref = ((a·0 + g·c1) + (b·c2 + r·c0))·range. This is Lumen's dot-product order; the a·0 term stays.
        var lref = new float[W * H];
        for (int i = 0; i < lref.Length; i++) { var p = rgb[i]; lref[i] = ((p.A * 0f + p.G * C1) + (p.B * C2 + p.R * C0)) * Range; }
        rgb = null!;
        RefLuma = lref;
        Progress.Tick(ProgressPhase);
        var validity = ValidityFromVignMap(VignMap, W, H, ValidityScale * C256);
        Progress.Tick(ProgressPhase);
        float gPrev = MinusOne;
        ushort[][]? refPyr = null; (int W, int H)[]? pdims = null;
        var refM = RefFrame.Module;
        foreach (int cam in MonoIds)
        {
            string name = ModuleName(cam);
            var cf = CapturedFrame.Load(_lri, name);
            int w = cf.Width, h = cf.Height;
            var noise = cf.Info.Noise ?? throw new InvalidOperationException($"{name}: no sensor noise model");
            float blackM = noise.Black;   // the sensor black of the mono module
            if (cf.Info.HasHotPixelLeakageCalibration) throw new NotSupportedException("HotpixelCalibration::correctHotpixelLeakage is not ported");
            var redM = cf.Module.SensorBayerRedOverride; int mrx = redM?.X ?? 0, mry = redM?.Y ?? 0;
            var img = new float[w * h];
            var stackedM = Stacks?.GetIfStacked(name);
            if (stackedM is null)
            {
                var lut = noise.SigmaTables(cf.Info.AnalogGain);
                var hp = new ushort[w * h];
                HotPixelKernel.RunInto(cf.Raw, w, h, new RectI(0, 0, w, h), mrx, mry, One, lut[0], lut.Length > 1 ? lut[1] : lut[0], lut.Length > 2 ? lut[2] : lut[0], hp, w, 0);
                for (int i = 0; i < img.Length; i++) img[i] = (float)hp[i] - blackM;
            }
            else
            {
                // `img` = the module's lt::StackFusion float frame (hot-pixel patched per frame by the stack fusion), else the hot-pixel-patched
                // raw frame, minus black; black = the black of frame 0 of the stack (the DB black of a mono module)
                var sf = stackedM.Fused;
                for (int i = 0; i < img.Length; i++) img[i] = sf[i] - blackM;
            }
            var (mc, mr, mg) = LensShadingKernel.ModelGrid(_lri.Header, cf.Module);
            VignettingFloat.Apply(img, w, h, new RectF(0f, 0f, (float)w, (float)h), w, h, mc, mr, LensShadingKernel.Transform(mg, One, false));
            // (gain_ref·exp_ref)/(gain_m·exp_m); no vignetting-model factor for a mono source
            float gm = (refM.SensorAnalogGain * (float)refM.SensorExposure) / (cf.Module.SensorAnalogGain * (float)cf.Module.SensorExposure);
            float gr = gm / c3;
            if (GainTol < MathF.Abs(gPrev - gr))
            {
                float sc = c3 / gm;
                var lr = new float[W * H];
                for (int i = 0; i < lr.Length; i++) { float v = lref[i] * sc; if (Range <= v) v = Range; lr[i] = v; }   // min(L·sc, range)
                VignettingFloat.Apply(lr, W, H, new RectF(0f, 0f, (float)W, (float)H), W, H, cols, rows, LensShadingKernel.Transform(grid, One, false));
                var lr16 = ToUshortSqrt(lr);
                LastFlowRef16 = lr16;
                refPyr = BlockFlow.Pyramid(lr16, W, H, PyramidLevels, out pdims);
            }
            var m16 = ToUshortSqrt(img);
            LastMono16 = m16;
            var flow = BlockFlow.ComputeFlow(refPyr!, pdims!, m16, w, h, PyramidLevels, validity, out int fw, out int fh);
            Flows.Add(flow); FlowDims.Add((fw, fh));
            for (int i = 0; i < img.Length; i++) img[i] = img[i] * gr + blackM;
            Sources.Add(img); SourceGains.Add(gr);
            if (Environment.GetEnvironmentVariable("LUX_FUSION_DUMP") is string dpre)   // diagnostic `mono_src<i>` (whole-frame mono source, img·gr + black)
                PackedBayerFusion.DumpFloat($"{dpre}_mono_src{Sources.Count - 1}.bin", img, w, h);
            gPrev = gr;
            Log?.Invoke($"mono fusion: source cam {cam} gain {gm:R} gr {gr:R} black {blackM:R} flow {fw}x{fh}");
        }
        Initialized = true;
        Progress.Tick(ProgressPhase);
        Log?.Invoke($"mono fusion: ids [{string.Join(",", MonoIds)}] nColour {nColour} w0 {W0:R} scale {Scale:R} A' {NoiseA:R} B' {NoiseB:R} black/white mono {BlackMono:R}/{WhiteMono:R} range {Range:R} coef ({C0:R},{C1:R},{C2:R},{C3:R})");
    }

    /// <summary>`v = min(max(x + 0.5, 1.0), 4095.0)`; `out = sqrtLUT[(int)v]`.</summary>
    public static ushort[] ToUshortSqrt(float[] src)
    {
        var o = new ushort[src.Length];
        for (int i = 0; i < o.Length; i++)
        {
            float v = src[i] + HalfF;
            if (v <= One) v = One;
            if (Max4095 <= v) v = Max4095;
            o[i] = unchecked((ushort)((int)v & 0xffff));
        }
        BlockFlow.ApplySqrtLut(o);
        return o;
    }

    /// <summary>`initialize::lambda_1`: `x = clamp((int)(W·p.x), 0, W−1)`, `y` likewise on the full-res map, `k = rsqrtNR(v)`; returns `k·thr &lt; score`.</summary>
    public static Func<float, (float X, float Y), bool> ValidityFromVignMap(float[] map, int w, int h, float thr)
    {
        return (score, p) =>
        {
            int x = (int)((float)w * p.X), y = (int)((float)h * p.Y);
            if (x < 0) x = 0; if (y < 0) y = 0;
            if (x > w - 1) x = w - 1; if (y > h - 1) y = h - 1;
            float v = map[y * w + x];
            float k = MonoMerge.RsqrtNR(v);
            return k * thr < score;
        };
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // render side
    // ---------------------------------------------------------------------------------------------------------------------------------
    public sealed record ProcessResult(Image<Vec4F> Rgb, float[] Weight, float[] Mono, float[] Luma, Image<Vec4F> RefC);

    /// <summary>Process (out, weightOut, F, grown): the fused colour Bayer <paramref name="fused"/> (grown-rect size, raw DN) → the RGB image of
    /// the grown rect with the luma replaced by the mono merge, plus the mono weight image.</summary>
    public ProcessResult Process(RectI grown, float[] fused)
    {
        if (!Initialized) throw new InvalidOperationException("MonoFusion not initialized");
        if (Sources.Count == 0) throw new InvalidOperationException("Empty mono!");
        int gw = grown.Width, gh = grown.Height;
        // the BayerFloat runner of the own ISP (Bayer view = the grown image, no halo; empty STD)
        var bayer = new Image<float>(grown, fused, gw, 0);
        Action<int, IStage, IspPayload>? tap = null;
        if (Environment.GetEnvironmentVariable("LUX_MONO_DUMP") is string dpre)
            tap = (i, st, p) =>
            {   // per-stage dumps of the own ISP: RGB working image (16 bpp) and the float Bayer (4 bpp), full extents
                void Dump<T>(string tag, Image<T> im, int bpp) where T : unmanaged { int w = im.Width, h = im.Height; int es = System.Runtime.InteropServices.Marshal.SizeOf<T>(); var b = new byte[16 + w * h * es]; BitConverter.GetBytes(w).CopyTo(b, 0); BitConverter.GetBytes(h).CopyTo(b, 4); BitConverter.GetBytes(w).CopyTo(b, 8); BitConverter.GetBytes(bpp).CopyTo(b, 12); for (int y = 0; y < h; y++) System.Runtime.InteropServices.MemoryMarshal.AsBytes(im.Row(y)).CopyTo(b.AsSpan(16 + y * w * es, w * es)); File.WriteAllBytes($"{dpre}_own_st{i}_{st.Stage}.{tag}.bin", b); Log?.Invoke($"own ISP stage {i} {st.Stage}/{st.TypeString}: {tag} rect {im.Rect}"); }
                if (p.Rgb is not null) Dump("rgb", p.Rgb, 16);
                if (p.BayerFloat is not null) Dump("bayer", p.BayerFloat, 4);
            };
        var refC = Isp.ProcessBayerFloat(RefFrame, Stats!, bayer, null, grown, 5, Log, tap);
        var refCopy = refC.Copy();
        // ⊙ (n0, n1, n2, 1)
        var nv = Vector128.Create(Neutral[0], Neutral[1], Neutral[2], One);
        var rgb = new Vec4F[gw * gh];
        for (int y = 0; y < gh; y++)
        {
            var row = refC.Row(y);
            for (int x = 0; x < gw; x++) { var p = row[x]; var v = Vector128.Create(p.R, p.G, p.B, p.A) * nv; rgb[y * gw + x] = new Vec4F(v.GetElement(0), v.GetElement(1), v.GetElement(2), v.GetElement(3)); }
        }
        // L = ((a·0 + g·c1) + (b·c2 + r·c0))·range + black, in Lumen's dot-product order
        var luma = new float[gw * gh];
        for (int i = 0; i < luma.Length; i++) { var p = rgb[i]; luma[i] = ((p.A * 0f + p.G * C1) + (p.B * C2 + p.R * C0)) * Range + BlackRef; }
        // the vignetting map is passed as a sub-VIEW (map ∩ grown): its rect fields still span the whole frame, so the kernel's per-block mean
        // is taken over block ∩ FRAME (not block ∩ ROI) — pass the full map
        if (_flag) throw new NotSupportedException("MonoFusion kernel variant with flag = 1 is not supported");
        var (mono, weight) = Merge(luma, VignMap, grown, Sources, Width, Height, Flows, FlowDims, W0, Scale, NoiseA, NoiseB, BlackMono, WhiteMono, WeightFn);
        // combine: rgb' = N·(l, (M·rgb)₁, (M·rgb)₂)
        float invR = One / Range;
        var n0 = Vector128.Create(N[0], N[3], N[6], 0f); var n1 = Vector128.Create(N[1], N[4], N[7], 0f); var n2 = Vector128.Create(N[2], N[5], N[8], 0f);
        var outImg = new Image<Vec4F>(grown);
        for (int y = 0; y < gh; y++)
        {
            var orow = outImg.Row(y);
            for (int x = 0; x < gw; x++)
            {
                var p = rgb[y * gw + x];
                // t = r·(M0,M3,M6); t = g·(M1,M4,M7) + t; t = b·(M2,M5,M8) + t → lanes 1,2 = M·rgb
                float m1 = p.B * M[5] + (p.G * M[4] + p.R * M[3]);
                float m2 = p.B * M[8] + (p.G * M[7] + p.R * M[6]);
                float l = (mono[y * gw + x] - BlackRef) * invR;
                var o = (Vector128.Create(m2) * n2 + Vector128.Create(m1) * n1) + Vector128.Create(l) * n0;
                orow[x] = new Vec4F(o.GetElement(0), o.GetElement(1), o.GetElement(2), p.A);
            }
        }
        return new ProcessResult(outImg, weight, mono, luma, refCopy);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // merge kernel
    // ---------------------------------------------------------------------------------------------------------------------------------
    static int FloorDiv8(int v) => (v + (v < 0 ? 7 : 0)) >> 3;   // (v + ((v >> 31) >>> 29)) >> 3
    static int CeilDiv8(int v) { int q = FloorDiv8(v); return (v & 7) != 0 ? q + (v >= 0 ? 1 : 0) : q; }

    /// <summary>The merge kernel (out, wOut, L, vign, srcs, flows, rect, prm): returns (mono, weight) images of the rect's size. `L` is the image of the
    /// rect itself (its extent == the ROI), as `MonoFusion::process` passes its grown-rect luma.</summary>
    public static (float[] Mono, float[] Weight) Merge(float[] L, float[]? vign /* W×H */, RectI rect, List<float[]> srcs, int W, int H, List<Vec2S[]> flows, List<(int W, int H)> flowDims,
        float w0, float scale, float A, float B, float black, float white, Func<float, float, float> fn)
        => MergeCore(L, rect.Width, rect, vign, rect, srcs, W, H, flows, flowDims, w0, scale, A, B, black, white, fn);

    /// <summary>The kernel with `L` as an image of its own extent: <paramref name="lRect"/> is L's rect in frame coordinates and <paramref name="lStride"/>
    /// its row stride; <paramref name="rect"/> is the ROI (frame coordinates). The per-block noise view (over `B ∩ L.rect`) and the block
    /// extract (edge replication at L's edge) clip to L's rect, the Hann accumulation (with its
    /// "block fully inside" fast path) and the final combine are on the ROI. `MonoFusion` passes L == ROI; the mono branch of `lt::StackFusion` passes
    /// the whole prepared frame with 16×16 tiles as the ROI (`StackFusion::lambda_1` → the ushort instantiation of this kernel — its
    /// helpers are the ushort twins of the float ones, reading
    /// `(float)v`, so the float kernel on the converted frame is the same arithmetic).</summary>
    public static (float[] Mono, float[] Weight) MergeCore(float[] L, int lStride, RectI lRect, float[]? vign /* W×H */, RectI rect, List<float[]> srcs, int W, int H,
        List<Vec2S[]> flows, List<(int W, int H)> flowDims, float w0, float scale, float A, float B, float black, float white, Func<float, float, float> fn)
    {
        if (srcs.Count == 0) throw new InvalidOperationException("No source images provided!");
        if (flows.Count != srcs.Count) throw new InvalidOperationException("Number of flow fields should match number of source images!");
        float w1 = One - w0;
        var hann = BayerMerge.Hann;
        int x0 = rect.X0, y0 = rect.Y0, x1 = rect.X1, y1 = rect.Y1;
        int w = x1 - x0, h = y1 - y0;
        var acc = new float[w * h]; var wOut = new float[w * h];
        var R = new float[256]; var Rw = new float[256]; var S = new float[256]; var accB = new float[256];
        var srcRect = new RectI(0, 0, W, H);
        int byStart = FloorDiv8(y0) * 8 - 8, byEnd = CeilDiv8(y1) * 8 + 8;
        int bxStart = FloorDiv8(x0) * 8 - 8, bxEnd = CeilDiv8(x1) * 8 + 8;
        for (int by = byStart; by < byEnd; by += 8)
        {
            int fyc = Math.Max(FloorDiv8(by), 0);
            for (int bx = bxStart; bx < bxEnd; bx += 8)
            {
                int rbx = bx - x0, rby = by - y0;
                // skip unless B ∩ L.rect is non-empty (frame coordinates)
                int ix0 = Math.Max(bx, lRect.X0), iy0 = Math.Max(by, lRect.Y0), ix1 = Math.Min(bx + 16, lRect.X1), iy1 = Math.Min(by + 16, lRect.Y1);
                if (!(ix0 < ix1 && iy0 < iy1)) continue;
                float g = One;
                if (vign is not null)
                {
                    // vign (the full W×H map, a sub-view whose rect spans the frame) ∩ block in absolute coordinates; mean = sum/(h·w) (0/0 = NaN when empty)
                    int vx0 = Math.Max(bx, 0), vy0 = Math.Max(by, 0), vx1 = Math.Min(bx + 16, W), vy1 = Math.Min(by + 16, H);
                    float sum = 0f; int vw = Math.Max(vx1 - vx0, 0), vh = Math.Max(vy1 - vy0, 0);
                    for (int y = vy0; y < vy1; y++) for (int x = vx0; x < vx1; x++) sum = sum + vign[y * W + x];
                    g = sum / (float)(vh * vw);
                }
                float sigma2 = MonoMerge.BlockNoise(L, lStride, ix0 - lRect.X0, iy0 - lRect.Y0, ix1 - ix0, iy1 - iy0, A, B, black, white, g);
                // the reference block: the kernel's L is a sub-view whose w/h are the ROI's (the tile for the stack kernel), so the clipped path clamps to the ROI window
                MonoMerge.ExtractBlockWin(L, lStride, lRect, rect, bx, by, R);
                Array.Copy(R, Rw, 256);
                MonoWavelet.Forward(Rw);
                Array.Clear(accB);
                float cnt = 0f, q2 = 0f;
                int fxc = Math.Max(FloorDiv8(bx), 0);
                for (int i = 0; i < srcs.Count; i++)
                {
                    var (fw, fh) = flowDims[i];
                    int fy = Math.Min(fyc, fh - 1), fx = Math.Min(fxc, fw - 1);
                    var fl = flows[i][fy * fw + fx];
                    int sx = bx + fl.X, sy = by + fl.Y;
                    if (Math.Max(sx, 0) < Math.Min(sx + 16, W) && Math.Max(sy, 0) < Math.Min(sy + 16, H))
                    {
                        MonoMerge.ExtractBlock(srcs[i], W, srcRect, sx, sy, S);
                        MonoWavelet.Forward(S);
                        float q = MonoMerge.Shrink(S, Rw, scale * sigma2);
                        MonoWavelet.Inverse(S);
                        for (int k = 0; k < 256; k++) accB[k] = accB[k] + S[k];
                        cnt = (cnt + One) - q;
                        q2 = q2 + q * q;
                    }
                    else
                    {
                        for (int k = 0; k < 256; k++) accB[k] = accB[k] + R[k];
                        cnt = cnt + One;
                    }
                }
                float wt = fn(cnt, q2);
                MonoMerge.AddHann(acc, w, h, rbx, rby, accB, hann);
                MonoMerge.AddHannScalar(wOut, w, h, rbx, rby, wt, hann);
            }
        }
        // out = acc·((1 − w0)/nSrc) + L·w0
        float k2 = w1 / (float)srcs.Count;
        var outp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int lrow = (y + y0 - lRect.Y0) * lStride + (x0 - lRect.X0), orow = y * w;
            for (int x = 0; x < w; x++) outp[orow + x] = acc[orow + x] * k2 + L[lrow + x] * w0;
        }
        return (outp, wOut);
    }
    /// <summary>`idx = max((((W8+1)·(m8+1)) &gt;&gt; 8) − 1, 0)`, `std = PackedBayerFusion.StdTable[idx]·k`.</summary>
    public static float[] StdPlaneMono(byte[] w8, byte[] m8, float k)
    {
        var o = new float[w8.Length];
        for (int i = 0; i < o.Length; i++)
        {
            int idx = ((w8[i] + 1) * (m8[i] + 1) >> 8) - 1;
            if (idx < 0) idx = 0;
            o[i] = PackedBayerFusion.StdTable[idx] * k;
        }
        return o;
    }
}
