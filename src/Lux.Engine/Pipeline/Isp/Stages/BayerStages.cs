using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The constructor-installed slot 2 of the Bayer-ushort block (`Pipeline::Pipeline` lambda_4, `18041f600`):
/// `float = ((float)raw − Sensor.black) · (1 / (Sensor.white − Sensor.black))` over the payload rect
/// (`ImageParallelAssign&lt;RowExprMulScalar&lt;RowExprSubScalar&gt;&gt;`), producing the float Bayer image the
/// crosstalk/demosaic stages consume. Not tunable — always present, like Lumen's slot.
/// </summary>
public sealed class BayerToFloatStage : IStage
{
    static bool _dbgOnce;
    public StageName Stage => StageName.Placeholder;
    public string TypeString => "default";
    public StageMeta Meta => new(1, 2, 1f);   // live stereo pipeline slot 3: pad 1, align 2, scale 1 (cp.dll stereo-tile reference run, 2026-08-26)
    public void Apply(IspPayload p)
    {
        var src = p.Raw ?? throw new InvalidOperationException("BayerToFloat needs the ushort source");
        var noise = p.Frame.Noise ?? throw new InvalidOperationException("BayerToFloat needs the sensor black/white levels (SensorNoise)");
        // LinearizeAndColorScaleImageDelegate<unsigned short> (18041fe90, called by the ctor's slot-2 lambda_0 18041fd50;
        // row kernel lambda_1 180421b40): per CFA site  float = ((float)raw − black) · 1/(neutral_c · (white − black)),
        // c = R at the red site, B at the opposite-parity site, G elsewhere. Verified live (cp.dll's ISP-stage listing, flat raw):
        // the module ISP's float Bayer image is white-balanced camera RGB.
        // the linearize reads Stats+0x198 (the Sensor of the capture the stats were built from), not this frame's own record
        float black = !float.IsNaN(p.Stats.SensorBlack) ? p.Stats.SensorBlack : (float.IsNaN(p.Frame.FrameBlack) ? noise.Black : p.Frame.FrameBlack);
        float white = !float.IsNaN(p.Stats.SensorWhite) ? p.Stats.SensorWhite : noise.White, range = white - black;
        if (Environment.GetEnvironmentVariable("LUX_FRAME_BLACK") is string fb) { black = float.Parse(fb, System.Globalization.CultureInfo.InvariantCulture); range = noise.White - black; }   // diagnostic
        if (Environment.GetEnvironmentVariable("LUX_ISP_DEBUG") == "1" && !_dbgOnce) { _dbgOnce = true; Console.Error.WriteLine($"[linearize] black {black:R} (frame {p.Frame.FrameBlack:R}, sensor {noise.Black:R}) white {noise.White:R}"); }
        var n = p.Stats.Neutral;
        float sR = 1f / (n[0] * range), sG = 1f / (n[1] * range), sB = 1f / (range * n[2]);
        var red = p.Context.Module.SensorBayerRedOverride; int rx = red?.X ?? 0, ry = red?.Y ?? 0;
        if (((src.Rect.Width | src.Rect.Height) & 1) != 0) throw new InvalidOperationException("invalid input size!");
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        var dst = new Image<float>(abs);
        for (int y = abs.Y0; y < abs.Y1; y++)
        {
            var row = src.Row(y - src.Rect.Y0); var drow = dst.Row(y - abs.Y0);
            bool redRow = (y & 1) == (ry & 1);
            float sEven = redRow ? ((rx & 1) == 0 ? sR : sG) : ((rx & 1) == 0 ? sG : sB);
            float sOdd = redRow ? ((rx & 1) == 0 ? sG : sR) : ((rx & 1) == 0 ? sB : sG);
            for (int x = abs.X0; x < abs.X1; x++)
                drow[x - abs.X0] = ((float)row[x - src.Rect.X0] - black) * ((x & 1) == 0 ? sEven : sOdd);
        }
        p.BayerFloat = dst;
    }
}

/// <summary>The BayerFloat-block slot 3: `LinearizeAndColorScaleImageDelegate&lt;float&gt;` on the stacked/fused float Bayer (raw DN incl. black) —
/// the same per-site `(x − black)·1/(neutral_c·(white − black))` as the ushort delegate, reading Stats+0x198 (the reference capture's sensor).</summary>
public sealed class BayerFloatLinearizeStage : IStage
{
    public StageName Stage => StageName.Placeholder;
    public string TypeString => "default";
    public StageMeta Meta => new(1, 2, 1f);
    public void Apply(IspPayload p)
    {
        var src = p.BayerFloat ?? throw new InvalidOperationException("BayerFloat linearize needs the float Bayer source");
        var noise = p.Frame.Noise ?? throw new InvalidOperationException("BayerFloat linearize needs the sensor black/white levels (SensorNoise)");
        float black = !float.IsNaN(p.Stats.SensorBlack) ? p.Stats.SensorBlack : (float.IsNaN(p.Frame.FrameBlack) ? noise.Black : p.Frame.FrameBlack);
        float white = !float.IsNaN(p.Stats.SensorWhite) ? p.Stats.SensorWhite : noise.White, range = white - black;
        var n = p.Stats.Neutral;
        float sR = 1f / (n[0] * range), sG = 1f / (n[1] * range), sB = 1f / (range * n[2]);
        var red = p.Context.Module.SensorBayerRedOverride; int rx = red?.X ?? 0, ry = red?.Y ?? 0;
        if (((src.Rect.Width | src.Rect.Height) & 1) != 0) throw new InvalidOperationException("invalid input size!");
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        var dst = new Image<float>(abs);
        for (int y = abs.Y0; y < abs.Y1; y++)
        {
            var row = src.Row(y - src.Rect.Y0); var drow = dst.Row(y - abs.Y0);
            bool redRow = (y & 1) == (ry & 1);
            float sEven = redRow ? ((rx & 1) == 0 ? sR : sG) : ((rx & 1) == 0 ? sG : sB);
            float sOdd = redRow ? ((rx & 1) == 0 ? sG : sR) : ((rx & 1) == 0 ? sB : sG);
            for (int x = abs.X0; x < abs.X1; x++)
                drow[x - abs.X0] = (row[x - src.Rect.X0] - black) * ((x & 1) == 0 ? sEven : sOdd);
        }
        p.BayerFloat = dst;
    }
}

/// <summary>Bayer-domain `Demosaicking:light_v1` (`setDemosaicking` lambda_23 → the functor at pipeline+0x1a70 =
/// `DemosaickLightV1` dispatcher; slot pad/align/scale from the setter). Consumes the float Bayer image, produces the
/// RGBA working image for the payload rect. The WB neutral is the Stats neutral (applied per CFA site inside).</summary>
public sealed class DemosaicLightV1Stage : IStage
{
    public StageName Stage => StageName.Demosaicking;
    public string TypeString => "light_v1";
    public StageMeta Meta => new(11, 2, 1f);   // setDemosaicking case 1: slot 0x20000000b = pad 11, align 2, scale 1
    public void Apply(IspPayload p)
    {
        var src = p.BayerFloat ?? throw new InvalidOperationException("Demosaic needs the float Bayer image (BayerToFloat stage)");
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        var red = p.Context.Module.SensorBayerRedOverride;
        var dst = new Image<Vec4F>(abs);
        // the kernel takes a whole-frame source view; pass the float image's own frame
        var roi = new RectI(abs.X0 - src.Rect.X0, abs.Y0 - src.Rect.Y0, abs.X1 - src.Rect.X0, abs.Y1 - src.Rect.Y0);
        DemosaicLightV1.Run(src.Data, src.Stride, src.Data.Length / src.Stride, roi, red?.X ?? 0, red?.Y ?? 0, p.Stats.Neutral, dst.Data);
        p.Rgb = dst;
    }
}
