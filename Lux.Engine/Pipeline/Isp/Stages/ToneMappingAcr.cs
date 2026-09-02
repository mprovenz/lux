using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ToneMapping:acr` / `light_v1` / `light_v1_lowlight` / `light_v2` — slot 15 (`setToneMapping` `18040cd30` enums 3–6 →
/// `new TMO_ACR(enum − 3)` at pipeline+0x1b80, colour body `18041c360` → `TMO_ACR::process` `18038ac60`, tile kernel
/// `18038aed0`, meta 1/1/1). Per 256×256 tile: `exp2f(ev_offset)` once, then per pixel a DNG `dng_function_exposure_ramp`
/// (white 1, black 0.005, radius 0.0025), three truncating LUT lookups with the fraction taken against the **clamped**
/// index and no output clamp, and a hue-preserving `RGBTone` whose middle channel is rebuilt with a raw `rcpss`
/// (no Newton step). Lane 3 is the untouched source alpha. Finally, unless the output `ColorSpace` equals linear
/// ProPhoto D50 field-for-field, the tile is converted ProPhoto → `output.color_space`. Spec `a-display-isp.md` §10b.
/// </summary>
public sealed class ToneMappingAcrStage : IStage
{
    readonly string _type; readonly int _mode;
    public ToneMappingAcrStage(string type) { _type = type; _mode = StageEnums.Enum("tone_mapping", type) - 3; }
    public StageName Stage => StageName.ToneMapping;
    public string TypeString => _type;
    public StageMeta Meta => new(1, 1, 1f);

    // vector constants (4× broadcast), 0x1806c6910…0x1806c6960
    static readonly float ToeOffset = BitConverter.Int32BitsToSingle(unchecked((int)0xBB23D70B));   // −0.0025000001769512892
    static readonly float QScale = BitConverter.Int32BitsToSingle(0x42C90149);                      // 100.50251007080078
    static readonly float LowerKnee = BitConverter.Int32BitsToSingle(0x3B23D70B);                   // 0.0025000001769512892
    static readonly float BlackPoint = BitConverter.Int32BitsToSingle(unchecked((int)0xBBA3D70B));  // −0.005000000353902578
    static readonly float Slope = BitConverter.Int32BitsToSingle(0x3F80A4AA);                       // 1.0050251483917236
    static readonly float UpperKnee = BitConverter.Int32BitsToSingle(0x3BF5C290);                   // 0.007500000298023224
    const float LutScale = 1024.0f;                                                                 // 0x1806c6970

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ToneMapping needs the RGB working image");
        float ev = 0f; try { ev = (float)p.Context.Tuning.Num("tone_mapping.ev_offset"); } catch (KeyNotFoundException) { }
        var lut = TmoAcrLuts.ForMode(_mode);
        var from = ColorSpace.ProPhotoD50; var to = p.Stats.OutSpace;
        bool convert = !from.SameAs(to);
        foreach (var tile in Tiler.Rects(new RectI(0, 0, img.Width, img.Height), 256, 256))
        {
            float gain = MuslMath.Exp2f(ev);   // one UCRT exp2f per tile, then shufps …,0
            for (int y = tile.Y0; y < tile.Y1; y++)
            {
                var row = img.Row(y);
                for (int x = tile.X0; x < tile.X1; x++) row[x] = Pixel(row[x], gain, lut);
            }
            if (convert)
            {
                var view = img.View(new RectI(img.Rect.X0 + tile.X0, img.Rect.Y0 + tile.Y0, img.Rect.X0 + tile.X1, img.Rect.Y0 + tile.Y1));
                ColorSpaceConvert.Convert(view, view, from, to, 1);
            }
        }
    }

    static float Rcp(float x) => Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / x;

    /// <summary>The exposure ramp: `t = (0.0075 ≤ v) ? (v − 0.005)·slope : (v ≤ 0.0025) ? 0 : ((v − 0.0025)²)·qScale`
    /// — the square is `(v+c)·(v+c)` then `·qScale`, never re-associated. `cmpleps` is ordered, so NaN takes neither mask.</summary>
    static float Ramp(float v)
    {
        float q = ((v + ToeOffset) * (v + ToeOffset)) * QScale;
        if (v <= LowerKnee) q = 0.0f;                       // blendvps(q, 0, cmpleps(v, 0.0025))
        if (UpperKnee <= v) q = (v + BlackPoint) * Slope;    // blendvps(q, L, cmpleps(0.0075, v))
        return q;
    }

    /// <summary>Scalar LUT lookup: `f = t·1024`, `i = cvttss2si(f)` (truncate), clamp to [0, 1023],
    /// `out = (f − (float)i)·(lut[i+1] − lut[i]) + lut[i]` — the fraction is taken against the CLAMPED index,
    /// so `t &gt; 1` extrapolates along the last segment.</summary>
    static float Curve(float t, float[] lut)
    {
        float f = t * LutScale;
        int i = (int)f;
        if (i < 0) i = 0;
        if (i >= 1024) i = 1023;
        float a = lut[i], b = lut[i + 1];
        float d = b - a;
        float fr = f - (float)i;
        return (fr * d) + a;
    }

    public static Vec4F Pixel(Vec4F s, float gain, float[] lut)
    {
        float vr = s.R * gain, vg = s.G * gain, vb = s.B * gain;
        float tr = Ramp(vr), tg = Ramp(vg), tb = Ramp(vb);
        float R = Curve(tr, lut), G = Curve(tg, lut), B = Curve(tb, lut);
        // RGBTone (18038b25c–18038b384): the sort keys are the RAMP outputs; the middle channel is rebuilt from the
        // two extremes with a raw rcpss. `ucomiss; jae/jbe` ⇒ a NaN comparand takes the "less-than" branch.
        if (tg > tr)
        {
            if (tb > tr)
            {
                if (tb > tg) G = ((Rcp(tb - tr) * (tg - tr)) * (B - R)) + R;   // r < g < b
                else B = ((Rcp(tg - tr) * (tb - tr)) * (G - R)) + R;           // r < b <= g
            }
            else R = ((Rcp(tg - tb) * (tr - tb)) * (G - B)) + B;               // b <= r < g
        }
        else if (tg > tb) G = ((Rcp(tr - tb) * (tg - tb)) * (R - B)) + B;      // b < g <= r
        else if (tb > tr) R = ((Rcp(tb - tg) * (tr - tg)) * (B - G)) + G;      // g <= r < b
        else if (tb > tg) B = ((Rcp(tr - tg) * (tb - tg)) * (R - G)) + G;      // g < b <= r
        else B = G;                                                             // g == b (the ramp maps ≤0.0025 to exactly 0)
        return new Vec4F(R, G, B, s.A);
    }
}
