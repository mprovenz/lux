namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ContrastAdjust:default` — slot 14 (`setContrastAdjust` case 1, meta 1/1/1).
/// `a = contrast_adjust.value`; `|a| &lt; 1e-6` (compared as a **double**) leaves the image untouched,
/// otherwise `AdjustContrast&lt;vec4x32f&gt;` maps `out = 0.217·(x/0.217)^g` with `g = (float)exp2((double)a·0.3)`
/// through the fast log2/exp2 pair, alpha copied from the source.
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
        a = (float)p.Context.Tuning.NumOr("contrast_adjust.value", a);
        if ((double)MathF.Abs(a) < 1e-6) return;                       // 1e-6 as a double; a NaN does not return
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) throw new InvalidOperationException("empty image data!");
        var src = img.View(abs);
        var dst = new Image<Vec4F>(abs);
        Run(dst, src, Anchor, a);
        p.Rgb = dst;
    }

    public static readonly float Anchor = BitConverter.Int32BitsToSingle(0x3e5e353f);   // 0.217f
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x358637bd);             // 1e-6f

    /// <summary>`AdjustContrast&lt;vec4x32f&gt;` and its tile kernel. `g = (float)exp2((double)a·0.3)`
    /// (the UCRT **double** `exp2`), `k = (1 − g)·log2f(anchor)` (UCRT `log2f`);
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

    /// <summary>UCRT `exp2` (double) — <see cref="WineCrt.Exp2"/>, bit-exact on every float `a` in [−4, 4] (the contrast slider's ±100·0.04).
    /// Reached when `contrast_adjust.value ≠ 0`, i.e. the contrast setting (ParamFloat 7).</summary>
    static double Exp2Double(double x) => WineCrt.Exp2(x);
    /// <summary>UCRT `log2f`. `log2f(0.217f)` is a compile-time-constant argument here.</summary>
    static float Log2f(float x) => WineCrt.Log2f(x);   // CRT log2f; MathF.Log2 equals it on every non-NaN float (it quiets signaling NaNs)
}
