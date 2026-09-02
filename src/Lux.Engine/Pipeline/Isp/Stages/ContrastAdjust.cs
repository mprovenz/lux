namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ContrastAdjust:default` — slot 14 (`setContrastAdjust` `18040c120` case 1 → lambda_66 `18041b7d0`, meta 1/1/1).
/// `a = contrast_adjust.value` (pipeline+0x1b78); `|a| &lt; 1e-6` (compared as a **double**) leaves the image untouched,
/// otherwise `AdjustContrast&lt;vec4x32f&gt;` `18038d970` maps `out = 0.217·(x/0.217)^g` with `g = (float)exp2((double)a·0.3)`
/// through the fast log2/exp2 pair, alpha copied from the source. Spec `a-display-isp.md` §10.
/// </summary>
public sealed class ContrastAdjustStage : IStage
{
    public StageName Stage => StageName.ContrastAdjust;
    public string TypeString => "default";
    public StageMeta Meta => new(1, 1, 1f);

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ContrastAdjust needs the RGB working image");
        float a = 0f;
        try { a = (float)p.Context.Tuning.Num("contrast_adjust.value"); } catch (KeyNotFoundException) { }
        if ((double)MathF.Abs(a) < 1e-6) return;                       // DAT_1806b5b80 = 1e-6 (double), ucomisd/jb
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) throw new InvalidOperationException("empty image data!");
        var src = img.View(abs);
        var dst = new Image<Vec4F>(abs);
        Run(dst, src, Anchor, a);
        p.Rgb = dst;
    }

    public static readonly float Anchor = BitConverter.Int32BitsToSingle(0x3e5e353f);   // DAT_1806d7648 = 0.217f
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x358637bd);             // _DAT_1806cac10 = 1e-6f

    /// <summary>`AdjustContrast&lt;vec4x32f&gt;` `18038d970` + tile kernel `180397300`. `g = (float)exp2((double)a·0.3)`
    /// (the UCRT **double** `exp2`, IAT 0x18067e4d0), `k = (1 − g)·log2f(anchor)` (UCRT `log2f`, IAT 0x18067e488);
    /// per pixel `x = maxps(eps, s)` (so a NaN source propagates), `y = g·fastlog2(x) + k`, `out = fastexp2(y)`,
    /// lane 3 blended from the source. Tiling (512×512) cannot change a pointwise kernel.</summary>
    public static void Run(Image<Vec4F> dst, Image<Vec4F> src, float anchor, float a)
    {
        float g = (float)Exp2Double((double)a * 0.3);
        float k = (1.0f - g) * Log2f(anchor);
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y); var d = dst.Row(y);
            for (int x = 0; x < s.Length; x++)
            {
                var v = s[x];
                d[x] = new Vec4F(Lane(v.R, g, k), Lane(v.G, g, k), Lane(v.B, g, k), v.A);
            }
        }
    }

    static float Lane(float s, float g, float k)
    {
        float x = Eps > s ? Eps : s;   // MAXPS dst=eps, src=s
        return FastLog2Exp2.Exp2(g * FastLog2Exp2.Log2(x) + k);
    }

    /// <summary>UCRT `exp2` (double). Only reached when `contrast_adjust.value ≠ 0`; the display tuning leaves it 0.</summary>
    static double Exp2Double(double x) => Math.Pow(2.0, x);
    /// <summary>UCRT `log2f`. `log2f(0.217f)` is a compile-time-constant argument here.</summary>
    static float Log2f(float x) => MathF.Log2(x);
}
