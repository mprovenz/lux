using System.Runtime.Intrinsics;
using Ltpb;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>A module frame as the ISP consumes it (`CapturedImage`): the raw 16-bit-per-sample Bayer buffer plus the frame facts.</summary>
public sealed class CapturedFrame
{
    public required ushort[] Raw { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Stride => Width;
    public required ModuleFrameInfo Info { get; init; }
    public required CameraModule Module { get; init; }
    public required LightHeader Header { get; init; }

    public static CapturedFrame Load(LriFile lri, string moduleName) => Load(lri, lri.Modules[moduleName], moduleName);

    /// <summary>Load one specific module block — the frame selector a stacked capture needs (`LriFile.Frames`).
    /// `Modules[name]` is frame 0 of the stack, which is what the single-argument overload uses.</summary>
    public static CapturedFrame Load(LriFile lri, LriFile.ModuleRef mref) => Load(lri, mref, mref.Module.Id.ToString());

    static CapturedFrame Load(LriFile lri, LriFile.ModuleRef mref, string moduleName)
    {
        var raw = lri.Frame(mref, out int w, out int h);
        var info = ModuleFrameInfo.From(lri, mref);
        var red = mref.Module.SensorBayerRedOverride;
        if (info.IsColour && info.Sensor == SensorType.SensorAr1335 && info.Noise is not null)
        {
            var (_, shadow) = CaptureState.SiteStats(mref.Module, raw, w, h);
            // 18020b0b0 → FUN_180125d10(frame, neutral, 42.0, 1.2, 40): the neutral is the per-frame SoftISP's (FUN_18020aad0: manual_temp at the
            // capture (CCT, tint) through the CAMERA'S OWN colour profile), not the AsShot neutral — identical for the reference (A1 42.51 on 00466)
            // but different for the other modules (A5 42.36, A3 43.17, A4 42.75; fusion port note a-fusion-port.md item 3).
            var colour = Color.LumenProfile.Compute(lri);
            var wb = Color.WhiteBalance.CaptureWb.From(lri, colour);
            var neutral = Color.WhiteBalance.NeutralFromTempTint(wb.Cct, wb.Tint, global::Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.CameraProfile(lri, mref.Module.Id));
            info = info with { FrameBlack = CaptureState.EstimateFrameBlack(shadow, red?.X ?? 0, red?.Y ?? 0, neutral, info.Noise.Black) };
        }
        return new CapturedFrame { Raw = raw, Width = w, Height = h, Info = info, Module = mref.Module, Header = lri.Header };
    }
}

/// <summary>
/// The `lt::SoftISP` wrapper (0x78 B, ctor `1803d8360`): a tuning tree, the cached stats and the stage graph rebuilt
/// only when a tuning value changed (`FUN_1803d8cf0`), and the Bayer-ushort process entry `FUN_1803dcd90` →
/// `FUN_180411380` → runner. Stage bodies are S6; this class is the frame the kernels plug into.
/// </summary>
public sealed class SoftIsp
{
    private readonly Tuning _tuning;
    private readonly LumenProfile _profile;
    private bool _dirty = true;
    private readonly Dictionary<PayloadDomain, List<IStage>> _graphs = new();
    private IspStats? _stats;

    public SoftIsp(Tuning tuning, LumenProfile profile) { _tuning = tuning; _profile = profile; }
    public Tuning Tuning => _tuning;

    public SoftIsp Set(string key, object value) { _tuning.Set(key, value); _dirty = true; _stats = null; return this; }

    /// <summary>`FUN_180410ac0`: mono sensors get neutral (1,1,1); otherwise the AWB functor installed by
    /// `setWhiteBalance` — `manual_temp` (lambda_21) → neutral from (temp, tint) through the profile; `manual_color` →
    /// the `neutral_color` value. Camera→output is the identity for `output.color_space = none` (L88–199).</summary>
    public IspStats ComputeStats(ModuleFrameInfo f) => ComputeStats(f, null);

    /// <summary>`FUN_180410ac0` L200–213: with the frame, also the IR-correction blend estimate
    /// (`cross_talk_correction = ir_correction` and a valid red position).</summary>
    public IspStats ComputeStats(CapturedFrame frame) => ComputeStats(frame.Info, frame);

    private IspStats ComputeStats(ModuleFrameInfo f, CapturedFrame? frame)
    {
        if (_stats is not null) return _stats;
        float[] neutral = { 1f, 1f, 1f }; float cct = 0f, tint = 0f; (float X, float Y) xy = (0f, 0f);
        if (f.IsColour)
        {
            switch (_tuning.Type("auto_white_balance"))
            {
                case "manual_temp":
                    cct = (float)_tuning.Num("auto_white_balance.neutral_temp"); tint = (float)_tuning.Num("auto_white_balance.neutral_tint");
                    neutral = WhiteBalance.NeutralFromTempTint(cct, tint, _profile);
                    xy = WhiteBalance.CctTintToXy(cct, tint);
                    break;
                case "manual_color":
                    neutral = _tuning.Vec("auto_white_balance.neutral_color").Select(x => (float)x).ToArray();
                    xy = WhiteBalance.NeutralToXy(neutral, _profile);
                    break;
                case "none":
                    // Lumen's Stats keep the D50 white chromaticity (live stereo ISP with awb none: xy = (0.34566918, 0.358496189), neutral (1,1,1))
                    xy = (BitConverter.Int32BitsToSingle(0x3eb0fb8d), BitConverter.Int32BitsToSingle(0x3eb78cd0));
                    break;
                default: throw new NotSupportedException($"auto_white_balance '{_tuning.Type("auto_white_balance")}' (AWB statistics) is not built yet");
            }
        }
        float irBlend = float.NaN;
        var red = frame?.Module.SensorBayerRedOverride;
        if (frame is not null && red is not null && (red.X | red.Y) >= 0 && _tuning.Type("cross_talk_correction") == "ir_correction")
        {
            // FUN_180410ac0 L200–213 → FUN_180420e80 → FUN_180133db0: CCT of the WB xy, light = median DN of `CapturedImage+0x1d8`
            // index 1 × gain × exposure. That vector is stored in colour order (`FUN_180126b00`), so index 1 is the Gr site.
            var noise = f.Noise ?? throw new InvalidOperationException("ir_correction needs the sensor black/white levels");
            var raster = CaptureState.SiteStats(frame.Module, frame.Raw, frame.Width, frame.Height).Hists;
            var hists = CaptureState.LumenHistograms(raster, red.X, red.Y);
            float light = global::Lux.Engine.Pipeline.Isp.Stages.IrCorrection.LightLevel(hists[1], noise.Black, noise.White, f.AnalogGain, frame.Module.SensorExposure);
            float cctIr = WhiteBalance.XyToCctF(xy.X, xy.Y).Cct;
            var model = global::Lux.Engine.Pipeline.Isp.Stages.CrossTalkStage.Model(frame.Header, frame.Module);
            var (A, B, w2, h2) = global::Lux.Engine.Pipeline.Isp.Stages.IrCorrection.RatioMaps(frame.Raw, frame.Width, frame.Height, frame.Stride, red.X, red.Y);
            var nodes = global::Lux.Engine.Pipeline.Isp.Stages.IrCorrection.NodeRatios(A, B, w2, h2, model.Cols, model.Rows);
            irBlend = global::Lux.Engine.Pipeline.Isp.Stages.IrCorrection.FitBlend(nodes, model.Cols, model.Rows, (int)frame.Module.Id, cctIr, light, (int)f.Sensor, global::Lux.Engine.Pipeline.Isp.Stages.CrossTalkStage.SpectralFlag(frame.Header, frame.Module));
        }
        var (cc, outCs) = ColorSpaces(f.IsColour, xy);
        HsvMap? hsv = null;
        if (f.IsColour && _tuning.Type("color_correction") == "optimized")
        {   // lambda_60 `180419ed0`: FUN_18041eff0(profileCopy, &hsvOut, Stats+0xc) BEFORE the ForwardMatrix build
            float cctH = WhiteBalance.XyToCctF(xy.X, xy.Y).Cct;
            float th1 = WhiteBalance.IlluminantCctF(_profile.Low.InternalIlluminant), th2 = WhiteBalance.IlluminantCctF(_profile.High.InternalIlluminant);
            hsv = HsvMap.Interpolate(HsvMap.FromGrid(_profile.Low.Fit.Grid), HsvMap.FromGrid(_profile.High.Fit.Grid), th1, th2, cctH);
        }
        float sBlack = f.Noise is null ? float.NaN : (float.IsNaN(f.FrameBlack) ? f.Noise.Black : f.FrameBlack), sWhite = f.Noise?.White ?? float.NaN;
        return _stats = new IspStats { Neutral = neutral, Cct = cct, Tint = tint, NeutralXy = xy, IrBlend = irBlend, Profile = _profile, NoiseSigma = f.Noise?.SigmaTables(f.AnalogGain), SensorBlack = sBlack, SensorWhite = sWhite, Noise = f.Noise, CcSpace = cc, OutSpace = outCs, HsvMap = hsv };
    }

    /// <summary>`FUN_180410ac0` L45–75 (mono) / L121–199 (colour): the ColorCorrection Stats functor (`setColorCorrection` lambda_56/57/58, pipeline +0x78)
    /// fills Stats+0x14, then the output space Stats+0x48 from `output.color_space` (+0x1bd8) / `output.white_point` (+0x1bdc):
    /// CC none (space 1): output none → both = `FUN_18038a230` (ProPhoto D50); else out = `Standard(space, wp, 1)` and CC = `FromMatrix(out.M, wp)`;
    /// CC real: output none → out = CC; else out = `Standard(space, wp, 1)`. Mono sensors: CC = `FromMatrix(I, wp(0))`, out = CC.</summary>
    (ColorSpace Cc, ColorSpace Out) ColorSpaces(bool colour, (float X, float Y) xy)
    {
        int outSpace = StageEnums.Enum("output.color_space", _tuning.Str("output.color_space"));
        int wpEnum = StageEnums.Enum("output.white_point", _tuning.Str("output.white_point"));
        if (wpEnum == 0) wpEnum = ColorSpace.NativeIlluminantOf(outSpace);   // factory 1803d8cf0 L263–271: native → FUN_1800ce580(space) (sRGB → D65)
        if (!colour)
        {
            var cs = ColorSpace.FromMatrix(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, WhitePoint.Of(0));
            return (cs, cs);
        }
        ColorSpace cc;
        switch (_tuning.Type("color_correction"))
        {
            case "none": cc = ColorSpace.None(); break;
            // lambda_60 (`optimized`) is lambda_56 (`default`) plus the HSVMap above — the camera→working matrix is bit-identical
            case "optimized":
            case "default":
            {   // lambda_56: FUN_18041efa0(profile, out, Stats+0xc): CCT of the WB xy (FUN_1800d0ef0) → FUN_1800d13d0(cct, T1, T2, FM1 (+0x58), FM2 (+0x7c)); wp = illuminant 5 (FUN_18041ebd0)
                float t = WhiteBalance.XyToCctF(xy.X, xy.Y).Cct;
                float[] fm1 = _profile.Low.Fit.ForwardMatrix, fm2 = _profile.High.Fit.ForwardMatrix;
                if (Environment.GetEnvironmentVariable("LUX_CC_FM") is string ov)
                {   // diagnostic: "fm1[9];fm2[9]" (e.g. the ForwardMatrix1/2 tags of a Lumen DNG = Lumen's own refit, exact floats) instead of the Lux Ceres refit
                    var parts = ov.Split(';'); fm1 = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray(); fm2 = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray();
                    Console.Error.WriteLine("[diagnostic] LUX_CC_FM: ForwardMatrix1/2 overridden");
                }
                float t1 = WhiteBalance.IlluminantCctF(_profile.Low.InternalIlluminant), t2 = WhiteBalance.IlluminantCctF(_profile.High.InternalIlluminant);
                if (Environment.GetEnvironmentVariable("LUX_CC_DEBUG") == "1") Console.Error.WriteLine($"[cc] xy ({xy.X:R},{xy.Y:R}) cct {t:R} ({BitConverter.SingleToInt32Bits(t):x8}) t1 {t1:R} ({BitConverter.SingleToInt32Bits(t1):x8}) t2 {t2:R} ({BitConverter.SingleToInt32Bits(t2):x8}) illum {_profile.Low.InternalIlluminant}/{_profile.High.InternalIlluminant}");
                var m = WhiteBalance.MatrixAtTemperature(t, t1, t2, fm1, fm2);
                if (Environment.GetEnvironmentVariable("LUX_CC_M") is string mo)
                {   // diagnostic: the 9 floats of a cp.dll Stats+0x14 matrix (bit patterns "hex hex …" or decimals) — bypasses the FM refit + interpolation entirely
                    var parts = mo.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    m = parts.Select(q => q.StartsWith("0x") ? BitConverter.Int32BitsToSingle(unchecked((int)System.Convert.ToUInt32(q, 16))) : float.Parse(q)).ToArray();
                    Console.Error.WriteLine("[diagnostic] LUX_CC_M: camera matrix overridden");
                }
                cc = ColorSpace.FromMatrix(m, WhitePoint.Of(5));
                break;
            }
            case "manual":   // lambda_58: pipeline +0x1ab8 = color_correction.matrix, wp D50
                cc = ColorSpace.FromMatrix(_tuning.Vec("color_correction.matrix").Select(v => (float)v).ToArray(), WhitePoint.Of(5)); break;
            default: throw new NotSupportedException($"color_correction '{_tuning.Type("color_correction")}' (Stats functor lambda_60, HSV maps) is not built yet");
        }
        if (cc.Space == 1)
        {
            if (outSpace == 1) return (ColorSpace.ProPhotoD50, ColorSpace.ProPhotoD50);
            var o = ColorSpace.Standard(outSpace, WhitePoint.Of(wpEnum), 1);
            return (ColorSpace.FromMatrix(o.M, WhitePoint.Of(wpEnum)), o);
        }
        if (outSpace == 1) return (cc, cc);
        return (cc, ColorSpace.Standard(outSpace, WhitePoint.Of(wpEnum), 1));
    }

    /// <summary>`FUN_1803de110(isp, &amp;stats)`: attach stats computed for another capture (the stereo ISP shares the reference capture's stats).</summary>
    public SoftIsp UseStats(IspStats stats) { _stats = stats; return this; }
    public IspStats? CurrentStats => _stats;

    public List<IStage> Stages(PayloadDomain domain)
    {
        if (_dirty) { _graphs.Clear(); _dirty = false; }
        if (!_graphs.TryGetValue(domain, out var g)) _graphs[domain] = g = StageGraph.Build(domain, _tuning);
        return g;
    }

    /// <summary>ROI clamp of `FUN_1803dc7b0` ("empty source RAW image!" / "invalid output ROI!").</summary>
    public static RectI ClampRoi(RectI roi, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException("empty source RAW image!");
        if (roi.X0 >= roi.X1 || roi.Y0 >= roi.Y1) throw new InvalidOperationException("invalid output ROI!");
        var c = new RectI(Math.Max(roi.X0, 0), Math.Max(roi.Y0, 0), Math.Min(roi.X1, w), Math.Min(roi.Y1, h));
        if (c.X0 >= c.X1 || c.Y0 >= c.Y1) throw new InvalidOperationException("invalid output ROI!");
        return c;
    }

    /// <summary>Bayer-ushort entry (`FUN_1803dcd90` → `FUN_180411380`): clamp the ROI, take stats, run the Bayer domain
    /// stage list over a whole-frame view, return the RGB result for the ROI.</summary>
    /// <summary>`FUN_1803dc980(isp, out, bayerFloat, refImg, rect, std)` → `FUN_180411b40`: the BayerFloat domain runner (pipeline index 5 of the
    /// level-1 fusion cache). <paramref name="bayer"/> (and <paramref name="std"/>, same rect) carry the halo'd render rect as their extents;
    /// <paramref name="roi"/> is the un-grown request (frame pixels); `stats` = the cache's Stats (reference capture, neutral = AsShot).</summary>
    public Image<Vec4F> ProcessBayerFloat(CapturedFrame refFrame, IspStats stats, Image<float> bayer, Image<float>? std, RectI roi, int level, Action<string>? log = null, Action<int, IStage, IspPayload>? afterStage = null)
    {
        var c = ClampRoi(roi, refFrame.Width, refFrame.Height);
        if (bayer.Rect.Intersect(c) != c) throw new InvalidOperationException("invalid source size!");
        if (std is not null && std.Rect != bayer.Rect) throw new InvalidOperationException("Bayer/STD image domain mismatch");
        var stages = Stages(PayloadDomain.BayerFloat);
        var ctx = new PipelineContext { Header = refFrame.Header, Module = refFrame.Module, Tuning = _tuning, Level = level, Log = log, FrameWidth = refFrame.Width, FrameHeight = refFrame.Height };
        var payload = new IspPayload { BayerFloat = bayer, Std = std, Stats = stats, Frame = refFrame.Info, Context = ctx };
        IReadOnlyList<IStage> run = stages;
        if (afterStage is not null) { var w = new List<IStage>(); for (int i = 0; i < stages.Count; i++) w.Add(new StageTap(stages[i], i, afterStage)); run = w; }
        PipelineRunner.Run(run, payload, c, bayer.Rect, new RectF(c.X0, c.Y0, c.X1, c.Y1));
        if (payload.Rgb is null) throw new InvalidOperationException("the BayerFloat stage list produced no RGB image (no demosaic stage ran)");
        float s = 1f; foreach (var st in stages) s *= st.Meta.Scale;
        var outRect = new RectI((int)(s * c.X0), (int)(s * c.Y0), (int)(s * c.X0) + (int)(s * c.Width), (int)(s * c.Y0) + (int)(s * c.Height));
        return payload.Rgb.Rect == outRect ? payload.Rgb : payload.Rgb.View(outRect);
    }

    /// <summary>`FUN_1803dd0e0(isp, out, vec4Img, refImg, floatRect, std)` → `FUN_1804125c0`: the Color-domain runner (stage vector `pipeline+0x14c8`,
    /// spec a-monofusion §7). <paramref name="rgb"/> = the vec4 input placed in the RGB working slot (+0x70) with its halo'd rect as extents;
    /// <paramref name="std"/> (same rect, "Input/STD image domain mismatch") the STD plane; <paramref name="floatRect"/> the request (frame pixels; the int
    /// rect is its `(int)` conversion, no frame clamp). Returns the working image cropped to the request.</summary>
    /// <param name="roi">the runner's ROI = Lumen's `(0,0,src.w,src.h)`. `FUN_1804125c0` derives it from the source
    /// image, NOT from the float rect (which on the display path is in sensor coordinates, not level coordinates);
    /// the default keeps the fusion caller's behaviour of taking it from the float rect.</param>
    public Image<Vec4F> ProcessColorFloat(CapturedFrame refFrame, IspStats stats, Image<Vec4F> rgb, Image<float>? std, RectF floatRect, int level, Action<string>? log = null, RectI? roi = null, Action<int, IStage, IspPayload>? afterStage = null)
    {
        if (rgb.Width < 1 || rgb.Height < 1) throw new InvalidOperationException("empty source image!");
        if (refFrame.Width < 1 || refFrame.Height < 1) throw new InvalidOperationException("empty source RAW image!");
        if (std is not null && std.Width > 0 && std.Height > 0 && std.Rect != rgb.Rect) throw new InvalidOperationException("Input/STD image domain mismatch");
        RectI c = floatRect.X0 < floatRect.X1 && floatRect.Y0 < floatRect.Y1
            ? new RectI((int)floatRect.X0, (int)floatRect.Y0, (int)floatRect.X1, (int)floatRect.Y1)
            : new RectI(0, 0, refFrame.Width, refFrame.Height);
        if (!(floatRect.X0 < floatRect.X1 && floatRect.Y0 < floatRect.Y1)) floatRect = new RectF(0f, 0f, refFrame.Width, refFrame.Height);
        var stages = Stages(PayloadDomain.Color);
        var ctx = new PipelineContext { Header = refFrame.Header, Module = refFrame.Module, Tuning = _tuning, Level = level, Log = log, FrameWidth = refFrame.Width, FrameHeight = refFrame.Height };
        var payload = new IspPayload { Rgb = rgb, Std = std, Stats = stats, Frame = refFrame.Info, Context = ctx };
        if (roi is { } rr) c = rr;
        IReadOnlyList<IStage> run = stages;
        if (afterStage is not null) { var wrapped = new List<IStage>(); for (int i = 0; i < stages.Count; i++) wrapped.Add(new StageTap(stages[i], i, afterStage)); run = wrapped; }
        PipelineRunner.Run(run, payload, c, rgb.Rect, floatRect);
        if (payload.Rgb is null) throw new InvalidOperationException("the Color stage list lost the RGB image");
        float s = 1f; foreach (var st in stages) s *= st.Meta.Scale;
        var outRect = new RectI((int)(s * c.X0), (int)(s * c.Y0), (int)(s * c.X0) + (int)(s * c.Width), (int)(s * c.Y0) + (int)(s * c.Height));
        return payload.Rgb.Rect == outRect ? payload.Rgb : payload.Rgb.View(outRect);
    }

    public Image<Vec4F> ProcessBayer(CapturedFrame frame, RectI roi, int level, Action<string>? log = null)
    {
        var c = ClampRoi(roi, frame.Width, frame.Height);
        var stats = ComputeStats(frame);
        var stages = Stages(PayloadDomain.Bayer);
        var ctx = new PipelineContext { Header = frame.Header, Module = frame.Module, Tuning = _tuning, Level = level, Log = log, FrameWidth = frame.Width, FrameHeight = frame.Height };
        var payload = new IspPayload
        {
            Raw = new Image<ushort>(new RectI(0, 0, frame.Width, frame.Height), frame.Raw, frame.Stride, 0),
            Stats = stats, Frame = frame.Info, Context = ctx,
        };
        PipelineRunner.Run(stages, payload, c, frame.Width, frame.Height, new RectF(c.X0, c.Y0, c.X1, c.Y1));
        if (payload.Rgb is null) throw new InvalidOperationException("the Bayer stage list produced no RGB image (no demosaic stage ran)");
        // FUN_180411380 copies the (scaled) ROI out of the last stage's image; the padding grown for the stages is dropped
        float s = 1f; foreach (var st in stages) s *= st.Meta.Scale;
        var outRect = new RectI((int)(s * c.X0), (int)(s * c.Y0), (int)(s * c.X0) + (int)(s * c.Width), (int)(s * c.Y0) + (int)(s * c.Height));
        return payload.Rgb.Rect == outRect ? payload.Rgb : payload.Rgb.View(outRect);
    }
}

/// <summary>The constructor-installed slot 2 of the Color block (`Pipeline::Pipeline` lambda_6, `180420140`): `rgb ⊙= (1/n0, 1/n1, 1/n2, 1)` with the
/// Stats neutral (`DAT_180681c78 / n` per component, `ImageParallelAssign&lt;ExprMulScalar&lt;vec4x32f&gt;&gt;`), then the working image is cut to the
/// payload int rect. Pad 1 / align 1 / scale 1.</summary>
public sealed class ColorScaleStage : IStage
{
    public StageName Stage => StageName.Placeholder;
    public string TypeString => "default";
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ColorScale needs the RGB working image");
        var n = p.Stats.Neutral;
        var g = System.Runtime.Intrinsics.Vector128.Create(1f / n[0], 1f / n[1], 1f / n[2], 1f);
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        var dst = new Image<Vec4F>(abs);
        for (int y = abs.Y0; y < abs.Y1; y++)
        {
            var row = img.Row(y - img.Rect.Y0); var drow = dst.Row(y - abs.Y0);
            for (int x = abs.X0; x < abs.X1; x++)
            {
                var v = row[x - img.Rect.X0];
                var o = System.Runtime.Intrinsics.Vector128.Create(v.R, v.G, v.B, v.A) * g;
                drow[x - abs.X0] = new Vec4F(o.GetElement(0), o.GetElement(1), o.GetElement(2), o.GetElement(3));
            }
        }
        p.Rgb = dst;
    }
}

/// <summary>Diagnostic wrapper: runs the inner stage, then hands the payload to a callback (per-stage dumps for comparison against cp.dll).</summary>
public sealed class StageTap : IStage
{
    readonly IStage _inner; readonly int _index; readonly Action<int, IStage, IspPayload> _after;
    public StageTap(IStage inner, int index, Action<int, IStage, IspPayload> after) { _inner = inner; _index = index; _after = after; }
    public StageName Stage => _inner.Stage;
    public string TypeString => _inner.TypeString;
    public StageMeta Meta => _inner.Meta;
    public void Apply(IspPayload p) { _inner.Apply(p); _after(_index, _inner, p); }
}
