using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`ColorCorrection:default` / `manual` — slot 10 (`setColorCorrection` 180408fa0 cases 1/2 → lambda_55 `180418e80`, meta 1/1/1):
/// `ImageConvertColorSpace(working ∩ int rect → new image, from = Stats+0x14 (the camera space the Stats functor lambda_56 / lambda_58 built),
/// to = FUN_18038a230 (linear ProPhoto D50), adaptation 1)`; the working image becomes that crop; the companion image (+0xa0, the pre-denoise
/// copy) is converted the same way when present. Spec `a-reference-guide.md` §3.4.</summary>
public sealed class ColorCorrectionDefaultStage : IStage
{
    readonly string _type;
    public ColorCorrectionDefaultStage(string type) { _type = type; }
    public StageName Stage => StageName.ColorCorrection;
    public string TypeString => _type;
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ColorCorrection needs the RGB working image");
        var from = p.Stats.CcSpace; var to = ColorSpace.ProPhotoD50;
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        p.Rgb = ConvertCrop(img, abs, from, to);
        if (p.Companion is { } ci && ci.Width > 0 && ci.Height > 0)
            p.Companion = ConvertCrop(ci, p.ToAbsolute(p.IntRect).Intersect(ci.Rect), from, to);
    }

    static Image<Vec4F> ConvertCrop(Image<Vec4F> img, RectI abs, ColorSpace from, ColorSpace to)
    {
        if (abs.IsEmpty) throw new InvalidOperationException("empty image data!");
        var dst = new Image<Vec4F>(abs);
        ColorSpaceConvert.Convert(dst, img.View(abs), from, to, 1);
        return dst;
    }
}
