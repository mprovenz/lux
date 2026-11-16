using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`ToneMapping:default` / `linear` — slot 15 (`setToneMapping` cases 1/2: `lt::LinearTMO` with its ev_offset;
/// the Bayer/BayerFloat/Color stage lambda calls `LinearTMO::process(img, img, output space)` (256×256 tiles)):
/// per tile `v ⊙= (2^ev, 2^ev, 2^ev, 1)` (exp2f of the ev_offset) then, unless the output space equals linear ProPhoto D50
/// (13-field compare), `ImageConvertColorSpace(tile, tile, ProPhotoD50, output space, 1)` in place. The whole working image is processed (no crop).</summary>
public sealed class ToneMappingLinearStage : IStage
{
    readonly string _type;
    public ToneMappingLinearStage(string type) { _type = type; }
    public StageName Stage => StageName.ToneMapping;
    public string TypeString => _type;
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ToneMapping needs the RGB working image");
        float ev = 0f; try { ev = (float)p.Context.Tuning.Num("tone_mapping.ev_offset"); } catch (KeyNotFoundException) { }
        float g = Exp2(ev);
        var from = ColorSpace.ProPhotoD50; var to = p.Stats.OutSpace;
        bool convert = !from.SameAs(to);
        foreach (var tile in Tiler.Rects(new RectI(0, 0, img.Width, img.Height), 256, 256))
        {
            for (int y = tile.Y0; y < tile.Y1; y++)
            {
                var row = img.Row(y);
                for (int x = tile.X0; x < tile.X1; x++) { var v = row[x]; row[x] = new Vec4F(v.R * g, v.G * g, v.B * g, v.A * 1f); }
            }
            if (convert)
            {
                var view = img.View(new RectI(img.Rect.X0 + tile.X0, img.Rect.Y0 + tile.Y0, img.Rect.X0 + tile.X1, img.Rect.Y0 + tile.Y1));
                ColorSpaceConvert.Convert(view, view, from, to, 1);
            }
        }
    }

    /// <summary>The CRT's `exp2f`. The guide path uses only 2⁰ = 1. Integer arguments take the exact short path; the other arguments
    /// go to <see cref="WineCrt.Exp2f"/>.</summary>
    static float Exp2(float x)
    {
        if (x == 0f) return 1.0f;
        if (x == MathF.Floor(x) && MathF.Abs(x) < 127f) return BitConverter.Int32BitsToSingle(((int)x + 127) << 23);
        return WineCrt.Exp2f(x);
    }
}
