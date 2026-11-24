using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// Lumen's `lt::Internal::AdaptiveDesaturate` (dispatcher, tile lambda, mask builder)
/// = `AdaptiveDesaturation:default` on the Color domain (slot pad 1 / align 1). Arguments: the
/// tuning cutoffs (shadow 0.01, highlight 0.8) and the Stats neutral.
///
/// Per tile (grown by 1 px, clamped to the image): mask = 1.0 where R &gt; hl/n_R ∧ G &gt; hl/n_G ∧ B &gt; hl/n_B, else 0;
/// mask blurred by the 3-tap gaussian `GaussianKernel(3, σ=1, scale=1)` = [e^-½, 1, e^-½]/Σ
/// (`ImageConvSeparable2D&lt;3,3,float,float&gt;`, vertical then horizontal, edge-clamped); RGB blurred by the 3×3 box
/// (kernel (⅓,⅓,⅓), `ImageConvSeparable2D&lt;3,3,vec4,float&gt;`, same passes). Per pixel:
/// mean = (G + (R + B))·⅓ of the *input* pixel; x = max(0, max(R,G,B)_blur·(1/shadow)); s = min(1, sqrt(x)) with
/// sqrt(x) = ((x·r)·r + (−3))·((x·r)·(−½)), r = rsqrtss(x), 0 when x = 0; h = min(1, mask_blur·(1/k₁));
/// out = (h·(mean − in))·s + ((in − mean)·s + mean); alpha lane kept.
/// </summary>
public static class AdaptiveDesaturateKernel
{
    public const int Pad = 3, Align = 1;   // the slot meta of Lumen's live pipeline: pad 3, align 1
    public const float Third = 0.33333334f;   // 0x3eaaaaab

    /// <summary>Gaussian kernel (n, σ, scale): k[i] = expf((i − c)²·(−½/σ²)), c = (n−1)·½, then k *= scale/Σk
    /// (Σ accumulated in index order).</summary>
    public static float[] GaussianKernel(int n, float sigma, float scale)
    {
        var k = new float[n];
        float f = -0.5f / (sigma * sigma), c = (float)(n - 1) * 0.5f, sum = 0f;
        for (int i = 0; i < n; i++) { float d = (float)i - c; k[i] = WineCrt.Expf(d * d * f); sum += k[i]; }
        float m = scale / sum;
        for (int i = 0; i < n; i++) k[i] *= m;
        return k;
    }

    private static float RsqrtS(float x) => IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();

    /// <summary>Process <paramref name="rect"/> (parent coordinates) of <paramref name="src"/> into a new image of the
    /// same rect. Neighborhood reads clamp at the source image's bounds (Lumen's grown tile view is clamped to the
    /// input image and the separable filters clamp at that view's allocation).</summary>
    public static Image<Vec4F> Run(Image<Vec4F> src, RectI rect, float shadowCutoff, float highlightCutoff, ReadOnlySpan<float> neutral)
    {
        rect = rect.Intersect(src.Rect);
        var dst = new Image<Vec4F>(rect);
        if (rect.IsEmpty) return dst;
        // grown region, clamped to the source
        var g = rect.Inflate(1).Intersect(src.Rect);
        int gw = g.Width, gh = g.Height;
        float thrR = highlightCutoff * (1f / neutral[0]), thrG = highlightCutoff * (1f / neutral[1]), thrB = highlightCutoff * (1f / neutral[2]);
        // mask plane: 1.0 where all three channels exceed their cutoff (the compare is `0 < c − thr`)
        var mask = ScratchPool.Rent<float>(gw * gh);
        var rgb = ScratchPool.Rent<Vec4F>(gw * gh);
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
        // Eight pixels at a time as lane vectors: each lane does the scalar operations of its pixel. Vector256.Max is the IEEE
        // maximum of MathF.Max. The loads stay inside rgb, rgbBlur, maskBlur and the destination row (xs + 8 ≤ rect.X1 ≤ g.X1).
        bool wide = Soa8.Supported && Vector256.IsHardwareAccelerated;
        ref float rgbF = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Vec4F, float>(rgb.AsSpan()));
        ref float blurF = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Vec4F, float>(rgbBlur.AsSpan()));
        ref float maskF = ref MemoryMarshal.GetArrayDataReference(maskBlur);
        for (int y = rect.Y0; y < rect.Y1; y++)
        {
            var drow = dst.Row(y - rect.Y0);
            int gy = y - g.Y0;
            int xs = rect.X0;
            if (wide)
            {
                ref float dstF = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Vec4F, float>(drow));
                var third = Vector256.Create(Third); var vInvShadow = Vector256.Create(invShadow); var vInvK1 = Vector256.Create(invK1); var one = Vector256.Create(1f);
                for (; xs + 8 <= rect.X1; xs += 8)
                {
                    int gi = gy * gw + (xs - g.X0);
                    Soa8.Load(ref Unsafe.Add(ref rgbF, gi * 4), out var pR, out var pG, out var pB, out var pA);
                    Soa8.Load(ref Unsafe.Add(ref blurF, gi * 4), out var bR, out var bG, out var bB, out _);
                    var mean = (pG + (pR + pB)) * third;
                    var maxc = Vector256.Max(Vector256.Max(bB, bR), Vector256.Max(bB, bG));
                    var xx = maxc * vInvShadow; xx = Vector256.ConditionalSelect(Vector256.GreaterThan(xx, Vector256<float>.Zero), xx, Vector256<float>.Zero);
                    var r = IntelApprox.ReciprocalSqrt(xx); var xr = xx * r;
                    var s = ((xr * r) + Vector256.Create(-3f)) * (xr * Vector256.Create(-0.5f));
                    s = Vector256.ConditionalSelect(Vector256.Equals(xx, Vector256<float>.Zero), Vector256<float>.Zero, s);
                    s = Vector256.ConditionalSelect(Vector256.GreaterThan(s, one), one, s);
                    var h = Vector256.LoadUnsafe(ref maskF, (nuint)gi) * vInvK1; h = Vector256.ConditionalSelect(Vector256.GreaterThan(h, one), one, h);
                    var R = (h * (mean - pR)) * s + ((pR - mean) * s + mean);
                    var G = (h * (mean - pG)) * s + ((pG - mean) * s + mean);
                    var B = (h * (mean - pB)) * s + ((pB - mean) * s + mean);
                    Soa8.Store(ref Unsafe.Add(ref dstF, (xs - rect.X0) * 4), R, G, B, pA);
                }
            }
            for (int x = xs; x < rect.X1; x++)
            {
                int gi = gy * gw + (x - g.X0);
                var p = rgb[gi]; var b = rgbBlur[gi];
                float mean = (p.G + (p.R + p.B)) * Third;
                float maxc = MathF.Max(MathF.Max(b.B, b.R), MathF.Max(b.B, b.G));
                float xx = maxc * invShadow; if (!(xx > 0f)) xx = 0f;   // MAXSS order: NaN gives 0
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
        ScratchPool.Return(mask); ScratchPool.Return(rgb); ScratchPool.Return(maskBlur); ScratchPool.Return(rgbBlur);
        return dst;
    }

    /// <summary>`ImageConvSeparable2D&lt;3,3,float,float&gt;` (vertical pass, then horizontal pass):
    /// v = (k0·above + k1·center) + k2·below, then (k0·left + k1·center) + k2·right, clamped at the image edge.
    /// The result is a <see cref="ScratchPool"/> array of at least w·h values.</summary>
    public static float[] Conv3x3(float[] src, int w, int h, float[] k)
    {
        var tmp = ScratchPool.Rent<float>(w * h); var dst = ScratchPool.Rent<float>(w * h);
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
        ScratchPool.Return(tmp);
        return dst;
    }

    /// <summary>`ImageConvSeparable2D&lt;3,3,vec4,float&gt;`:
    /// v = (k2·above + k1·center) + k0·below, then (k2·left + k1·center) + k0·right — the vec4 variant broadcasts
    /// the kernel reversed (equal to the float variant for symmetric kernels). Alpha is filtered too.
    /// A pixel's four lanes are one vector, and each lane does the scalar operations in the same order.
    /// The result is a <see cref="ScratchPool"/> array of at least w·h pixels.</summary>
    public static Vec4F[] Conv3x3(Vec4F[] src, int w, int h, float[] k)
    {
        var tmp = ScratchPool.Rent<Vec4F>(w * h); var dst = ScratchPool.Rent<Vec4F>(w * h);
        var S = MemoryMarshal.Cast<Vec4F, Vector128<float>>(src.AsSpan());
        var Tm = MemoryMarshal.Cast<Vec4F, Vector128<float>>(tmp.AsSpan());
        var D = MemoryMarshal.Cast<Vec4F, Vector128<float>>(dst.AsSpan());
        var ka = Vector128.Create(k[2]); var kb = Vector128.Create(k[1]); var kc = Vector128.Create(k[0]);
        for (int y = 0; y < h; y++)
        {
            int ya = Math.Max(y - 1, 0) * w, yb = Math.Min(y + 1, h - 1) * w, yc = y * w;
            for (int x = 0; x < w; x++) Tm[yc + x] = (ka * S[ya + x] + kb * S[yc + x]) + kc * S[yb + x];
        }
        for (int y = 0; y < h; y++)
        {
            int yc = y * w;
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(x - 1, 0), xb = Math.Min(x + 1, w - 1);
                D[yc + x] = (ka * Tm[yc + xa] + kb * Tm[yc + x]) + kc * Tm[yc + xb];
            }
        }
        ScratchPool.Return(tmp);
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
