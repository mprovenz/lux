using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::Internal::AdaptiveDesaturate` (dispatcher `180420d10`, tile lambda `1804223a0`, mask builder `FUN_180422d00`)
/// = `AdaptiveDesaturation:default` on the Color domain (setter `180404920`, slot pad 1 / align 1). Arguments: the
/// tuning cutoffs (pipeline+0x1b28: shadow 0.01, highlight 0.8) and the Stats neutral.
///
/// Per tile (grown by 1 px, clamped to the image): mask = 1.0 where R &gt; hl/n_R ∧ G &gt; hl/n_G ∧ B &gt; hl/n_B, else 0;
/// mask blurred by the 3-tap gaussian `FUN_1800bb720(3, σ=1, scale=1)` = [e^-½, 1, e^-½]/Σ
/// (`ImageConvSeparable2D&lt;3,3,float,float&gt;`, vertical then horizontal, edge-clamped); RGB blurred by the 3×3 box
/// (kernel (⅓,⅓,⅓), `ImageConvSeparable2D&lt;3,3,vec4,float&gt;`, same passes). Per pixel (asm `1804227f0–18042289e`):
/// mean = (G + (R + B))·⅓ of the *input* pixel; x = max(0, max(R,G,B)_blur·(1/shadow)); s = min(1, sqrt(x)) with
/// sqrt(x) = ((x·r)·r + (−3))·((x·r)·(−½)), r = rsqrtss(x), 0 when x = 0; h = min(1, mask_blur·(1/k₁));
/// out = (h·(mean − in))·s + ((in − mean)·s + mean); alpha lane kept.
/// </summary>
public static class AdaptiveDesaturateKernel
{
    public const int Pad = 3, Align = 1;   // live slot meta (cp.dll's live ISP-stage listing: slot 8 docall 416520 pad 3 align 1; runner rect dump 2026-08-27)
    public const float Third = 0.33333334f;   // 0x3eaaaaab (dispatcher local_38)

    /// <summary>`FUN_1800bb720(k, n, σ, scale)`: k[i] = expf((i − c)²·(−½/σ²)), c = (n−1)·½, then k *= scale/Σk
    /// (Σ accumulated in index order).</summary>
    public static float[] GaussianKernel(int n, float sigma, float scale)
    {
        var k = new float[n];
        float f = -0.5f / (sigma * sigma), c = (float)(n - 1) * 0.5f, sum = 0f;
        for (int i = 0; i < n; i++) { float d = (float)i - c; k[i] = MathF.Exp(d * d * f); sum += k[i]; }
        float m = scale / sum;
        for (int i = 0; i < n; i++) k[i] *= m;
        return k;
    }

    private static float RsqrtS(float x) => Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / MathF.Sqrt(x);

    /// <summary>Process <paramref name="rect"/> (parent coordinates) of <paramref name="src"/> into a new image of the
    /// same rect. Neighbourhood reads clamp at the source image's bounds (Lumen's grown tile view is clamped to the
    /// input image and the separable filters clamp at that view's allocation).</summary>
    public static Image<Vec4F> Run(Image<Vec4F> src, RectI rect, float shadowCutoff, float highlightCutoff, ReadOnlySpan<float> neutral)
    {
        rect = rect.Intersect(src.Rect);
        var dst = new Image<Vec4F>(rect);
        if (rect.IsEmpty) return dst;
        // grown region, clamped to the source (lambda L20–60)
        var g = rect.Inflate(1).Intersect(src.Rect);
        int gw = g.Width, gh = g.Height;
        float thrR = highlightCutoff * (1f / neutral[0]), thrG = highlightCutoff * (1f / neutral[1]), thrB = highlightCutoff * (1f / neutral[2]);
        // mask plane (FUN_180422d00): 1.0 where all three channels exceed their cutoff (the compare is `0 < c − thr`)
        var mask = new float[gw * gh];
        var rgb = new Vec4F[gw * gh];
        for (int y = 0; y < gh; y++)
        {
            var row = src.Row(g.Y0 + y - src.Rect.Y0);
            for (int x = 0; x < gw; x++)
            {
                var p = row[g.X0 + x - src.Rect.X0];
                rgb[y * gw + x] = p;
                mask[y * gw + x] = (0f < p.R - thrR && 0f < p.G - thrG && 0f < p.B - thrB) ? 1f : 0f;
            }
        }
        var k = GaussianKernel(3, 1f, 1f);
        var maskBlur = Conv3x3(mask, gw, gh, k);
        var rgbBlur = Conv3x3(rgb, gw, gh, new[] { Third, Third, Third });
        float invShadow = 1f / shadowCutoff, invK1 = 1f / k[1];
        for (int y = rect.Y0; y < rect.Y1; y++)
        {
            var drow = dst.Row(y - rect.Y0);
            int gy = y - g.Y0;
            for (int x = rect.X0; x < rect.X1; x++)
            {
                int gi = gy * gw + (x - g.X0);
                var p = rgb[gi]; var b = rgbBlur[gi];
                float mean = (p.G + (p.R + p.B)) * Third;
                float maxc = MathF.Max(MathF.Max(b.B, b.R), MathF.Max(b.B, b.G));
                float xx = maxc * invShadow; if (!(xx > 0f)) xx = 0f;   // maxss(x, 0)
                float s;
                if (xx == 0f) s = 0f;
                else { float r = RsqrtS(xx); float xr = xx * r; s = ((xr * r) + -3f) * (xr * -0.5f); }
                if (s > 1f) s = 1f;
                float h = maskBlur[gi] * invK1; if (h > 1f) h = 1f;
                float R = (h * (mean - p.R)) * s + ((p.R - mean) * s + mean);
                float G = (h * (mean - p.G)) * s + ((p.G - mean) * s + mean);
                float B = (h * (mean - p.B)) * s + ((p.B - mean) * s + mean);
                drow[x - rect.X0] = new Vec4F(R, G, B, p.A);
            }
        }
        return dst;
    }

    /// <summary>`ImageConvSeparable2D&lt;3,3,float,float&gt;` (helpers `1800ce030` vertical, `1800ce380` horizontal):
    /// v = (k0·above + k1·centre) + k2·below, then (k0·left + k1·centre) + k2·right, clamped at the image edge.</summary>
    public static float[] Conv3x3(float[] src, int w, int h, float[] k)
    {
        var tmp = new float[w * h]; var dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int ya = Math.Max(y - 1, 0), yb = Math.Min(y + 1, h - 1);
            for (int x = 0; x < w; x++) tmp[y * w + x] = (k[0] * src[ya * w + x] + k[1] * src[y * w + x]) + k[2] * src[yb * w + x];
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(x - 1, 0), xb = Math.Min(x + 1, w - 1);
                dst[y * w + x] = (k[0] * tmp[y * w + xa] + k[1] * tmp[y * w + x]) + k[2] * tmp[y * w + xb];
            }
        return dst;
    }

    /// <summary>`ImageConvSeparable2D&lt;3,3,vec4,float&gt;` (lambda `180350b20`, horizontal helper `FUN_180350fe0`):
    /// v = (k2·above + k1·centre) + k0·below, then (k2·left + k1·centre) + k0·right — the vec4 variant broadcasts
    /// the kernel reversed (equal to the float variant for symmetric kernels). Alpha is filtered too.</summary>
    public static Vec4F[] Conv3x3(Vec4F[] src, int w, int h, float[] k)
    {
        var tmp = new Vec4F[w * h]; var dst = new Vec4F[w * h];
        Vec4F F(float ka, Vec4F a, float kb, Vec4F b, float kc, Vec4F c) => new(
            (ka * a.R + kb * b.R) + kc * c.R, (ka * a.G + kb * b.G) + kc * c.G, (ka * a.B + kb * b.B) + kc * c.B, (ka * a.A + kb * b.A) + kc * c.A);
        for (int y = 0; y < h; y++)
        {
            int ya = Math.Max(y - 1, 0), yb = Math.Min(y + 1, h - 1);
            for (int x = 0; x < w; x++) tmp[y * w + x] = F(k[2], src[ya * w + x], k[1], src[y * w + x], k[0], src[yb * w + x]);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(x - 1, 0), xb = Math.Min(x + 1, w - 1);
                dst[y * w + x] = F(k[2], tmp[y * w + xa], k[1], tmp[y * w + x], k[0], tmp[y * w + xb]);
            }
        return dst;
    }
}

/// <summary>Color-domain stage `AdaptiveDesaturation:default`.</summary>
public sealed class AdaptiveDesaturateStage : IStage
{
    public StageName Stage => StageName.AdaptiveDesaturation;
    public string TypeString => "default";
    public StageMeta Meta => new(AdaptiveDesaturateKernel.Pad, AdaptiveDesaturateKernel.Align, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("AdaptiveDesaturation needs the RGB working image");
        var t = p.Context.Tuning;
        float shadow = (float)t.Num("adaptive_desaturation.shadow_cutoff"), highlight = (float)t.Num("adaptive_desaturation.highlight_cutoff");
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        p.Rgb = AdaptiveDesaturateKernel.Run(img, abs, shadow, highlight, p.Stats.Neutral);
    }
}
