using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`ToneMapping:default` / `linear` — slot 15 (`setToneMapping` 18040cd30 cases 1/2: `lt::LinearTMO` (ctor `FUN_18038a450`, +8 = ev_offset)
/// at pipeline+0x1b80, Bayer/BayerFloat/Color lambda_70 → `LinearTMO::process(img, img, Stats+0x48)` (`18038a470`, 256×256 tiles, lambda_0 `18038a630`)):
/// per tile `v ⊙= (2^ev, 2^ev, 2^ev, 1)` (`FUN_18038a990`, exp2f of the ev_offset) then, unless the output space equals linear ProPhoto D50
/// (13-field compare), `ImageConvertColorSpace(tile, tile, ProPhotoD50, Stats+0x48, 1)` in place. The whole working image is processed (no crop).
/// Spec `a-reference-guide.md` §3.5.</summary>
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

    /// <summary>msvcrt `exp2f` (api-ms-win-crt-math exp2f); exact for integer arguments — the only value the guide path uses is 2⁰ = 1.
    /// Non-integer ev_offsets would need the Wine/msvcrt exp2f transcription [I].</summary>
    static float Exp2(float x)
    {
        if (x == 0f) return 1.0f;
        if (x == MathF.Floor(x) && MathF.Abs(x) < 127f) return BitConverter.Int32BitsToSingle(((int)x + 127) << 23);
        return MathF.Pow(2f, x);
    }
}
