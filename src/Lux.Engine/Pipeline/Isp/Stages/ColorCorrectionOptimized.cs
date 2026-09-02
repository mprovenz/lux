using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ColorCorrection:optimized` — slot 10 (`setColorCorrection` `180408fa0` case 3 → lambda_59, one shared body
/// `180419750`, meta 1/1/1). Identical to <see cref="ColorCorrectionDefaultStage"/> — the same camera→linear-ProPhoto-D50
/// `ImageConvertColorSpace` with the same `Stats+0x14` matrix — **plus**, when the Stats functor lambda_60 `180419ed0`
/// left a non-empty `HSVMap` at `Stats+0x80`, a three-pass round trip in that ProPhoto space:
/// `ImageConvertRGBToHSV` (`1800cfcc0`, lambda_1 `1800e79b0`) → `HSVMap::apply` (`18017bee0`, lambda_0 `18017c120`)
/// → `ImageConvertHSVToRGB` (`1800cfee0`, lambda_2 `1800e7c10`). All three are pointwise, so their 512×512 tiling
/// cannot change a pixel. Spec `a-display-isp.md` §8.
/// </summary>
public sealed class ColorCorrectionOptimizedStage : IStage
{
    public StageName Stage => StageName.ColorCorrection;
    public string TypeString => "optimized";
    public StageMeta Meta => new(1, 1, 1f);   // pipeline+0x1328/132c/1330, written at 1804098fd..180409905

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ColorCorrection needs the RGB working image");
        var from = p.Stats.CcSpace; var to = ColorSpace.ProPhotoD50;
        var map = p.Stats.HsvMap;
        p.Rgb = Convert(img, p.ToAbsolute(p.IntRect).Intersect(img.Rect), from, to, map);
        if (p.Companion is { } ci && ci.Width > 0 && ci.Height > 0)
            p.Companion = Convert(ci, p.ToAbsolute(p.IntRect).Intersect(ci.Rect), from, to, map);
    }

    static Image<Vec4F> Convert(Image<Vec4F> img, RectI abs, ColorSpace from, ColorSpace to, HsvMap? map)
    {
        if (abs.IsEmpty) throw new InvalidOperationException("empty image data!");
        var dst = new Image<Vec4F>(abs);
        ColorSpaceConvert.Convert(dst, img.View(abs), from, to, 1);
        if (map is null || map.IsEmpty) return dst;   // 180419829: `if (FUN_18017bd10(map) == 0)`
        var hsv = new Image<Vec4F>(abs);
        RgbToHsv(hsv, dst);
        var mapped = new Image<Vec4F>(abs);
        ApplyMap(mapped, hsv, map);
        var rgb = new Image<Vec4F>(abs);
        HsvToRgb(rgb, mapped);
        return rgb;
    }

    // ---- SSE scalar/packed primitives, operand order as the instructions have it ----
    static float Max(float dst, float src) => dst > src ? dst : src;   // MAXSS/MAXPS dst, src
    static float Min(float dst, float src) => dst < src ? dst : src;   // MINSS/MINPS dst, src

    /// <summary>`ImageConvertRGBToHSV::lambda_1` `1800e79b0` (spec §8.5). H ∈ [0,1) turns, S ∈ [0,1], V = max lane
    /// (unbounded), A = max(srcA, 0). The leading `maxps(src, 0)` hard-clips out-of-gamut ProPhoto components —
    /// the one behavioural difference from `default`.</summary>
    public static void RgbToHsv(Image<Vec4F> dst, Image<Vec4F> src)
    {
        const float Sixth = 0.16666667f;   // DAT_1806874c0 = 3e2aaaab
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y); var d = dst.Row(y);
            for (int x = 0; x < s.Length; x++)
            {
                var v = s[x];
                float r = Max(v.R, 0f), g = Max(v.G, 0f), b = Max(v.B, 0f), a = Max(v.A, 0f);
                float mn = Min(Min(r, g), b);
                float mx = Max(Max(r, g), b);
                float df = mx - mn;
                float S = 0f;
                if (mx != 0f) S = df / mx;
                if (S == 0f) { d[x] = new Vec4F(0f, 0f, mx, a); continue; }
                float inv = 1.0f / df;
                float h;
                if (r == mx) h = inv * (g - b);
                else if (g == mx) h = inv * (b - r) + 2.0f;
                else h = inv * (r - g) + 4.0f;
                h = h * Sixth;
                if (h < 0f) h = h + 1.0f;
                d[x] = new Vec4F(h, S, mx, a);
            }
        }
    }

    /// <summary>`HSVMap::apply::lambda_0` `18017c120` (spec §8.6): clamped bilinear over (hue, sat) only — the value
    /// axis is never indexed — then `H' = frac(H + cell.x)` (the builder's `+1` bias makes `frac` the modulo),
    /// `S' = clamp(S·cell.y, 0, 1)`, `V' = clamp(V·cell.z, 0, 1e20)`, alpha from the source.</summary>
    public static void ApplyMap(Image<Vec4F> dst, Image<Vec4F> src, HsvMap map)
    {
        int stride = map.Nh + 1;
        float scaleH = (float)(map.Nh - 1), scaleS = (float)(map.Ns - 1);
        var lut = map.Cells;
        const float VCap = 1e20f;   // _DAT_1806a29f0 = (1, 1, 1e20, 1)
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y); var d = dst.Row(y);
            for (int x = 0; x < s.Length; x++)
            {
                var p = s[x];
                float qh = Min(Max(p.R * scaleH, 0f), scaleH);
                float qs = Min(Max(p.G * scaleS, 0f), scaleS);
                // lanes 2,3 of q are zeroed by the insertps 0x1c, so their floor/fraction is 0 — irrelevant below
                int i0 = (int)qh, j0 = (int)qs;
                int idx = i0 + j0 * stride;
                float fh = qh - MathF.Floor(qh), fs = qs - MathF.Floor(qs);
                int a4 = idx * 4, b4 = (idx + 1) * 4, c4 = (idx + stride) * 4, d4 = (idx + stride + 1) * 4;
                float dx0 = ((lut[b4] - lut[a4]) * fh) + lut[a4];
                float dx1 = ((lut[b4 + 1] - lut[a4 + 1]) * fh) + lut[a4 + 1];
                float dx2 = ((lut[b4 + 2] - lut[a4 + 2]) * fh) + lut[a4 + 2];
                float dx3 = ((lut[b4 + 3] - lut[a4 + 3]) * fh) + lut[a4 + 3];
                float dB0 = (lut[d4] - lut[c4]) * fh;
                float dB1 = (lut[d4 + 1] - lut[c4 + 1]) * fh;
                float dB2 = (lut[d4 + 2] - lut[c4 + 2]) * fh;
                float dB3 = (lut[d4 + 3] - lut[c4 + 3]) * fh;
                float e0 = (((lut[c4] - dx0) + dB0) * fs) + dx0;
                float e1 = (((lut[c4 + 1] - dx1) + dB1) * fs) + dx1;
                float e2 = (((lut[c4 + 2] - dx2) + dB2) * fs) + dx2;
                float e3 = (((lut[c4 + 3] - dx3) + dB3) * fs) + dx3;
                float sum0 = e0 + p.R;
                float frac0 = sum0 - MathF.Floor(sum0);
                float o0 = Min(Max(frac0, 0f), 1f);
                float o1 = Min(Max(e1 * p.G, 0f), 1f);
                float o2 = Min(Max(e2 * p.B, 0f), VCap);
                _ = e3;
                d[x] = new Vec4F(o0, o1, o2, p.A);   // blendps imm 0x8: lane 3 from the source
            }
        }
    }

    /// <summary>`ImageConvertHSVToRGB::lambda_2` `1800e7c10` (spec §8.7):
    /// `RGB_c = ((clamp(|6H − k_c| + b_c, 0, 1) − 1)·S + 1)·V`, no output clamp, alpha from the source.</summary>
    public static void HsvToRgb(Image<Vec4F> dst, Image<Vec4F> src)
    {
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y); var d = dst.Row(y);
            for (int x = 0; x < s.Length; x++)
            {
                var p = s[x];
                float h = p.R;
                float t0 = h * 6f, t1 = h * 6f, t2 = h * 6f;
                t0 += -3f; t1 += -2f; t2 += -4f;
                t0 = MathF.Abs(t0); t1 = MathF.Abs(t1); t2 = MathF.Abs(t2);
                t0 += -1f; t1 += -2f; t2 += -2f;
                t0 *= 1f; t1 *= -1f; t2 *= -1f;
                t0 = Min(Max(t0, 0f), 1f); t1 = Min(Max(t1, 0f), 1f); t2 = Min(Max(t2, 0f), 1f);
                t0 += -1f; t1 += -1f; t2 += -1f;
                t0 *= p.G; t1 *= p.G; t2 *= p.G;
                t0 += 1f; t1 += 1f; t2 += 1f;
                t0 *= p.B; t1 *= p.B; t2 *= p.B;
                d[x] = new Vec4F(t0, t1, t2, p.A);
            }
        }
    }
}
