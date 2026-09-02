using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`A::LaplacianPyramidConfig` (pipeline+0x1b90, 0x38 B; ctor `FUN_180398b10`) — the only state the local-Laplacian
/// kernel reads. Ctor defaults 0 / 1 / 1 / 0.5 (`0x1806d46e0`), −8, 0.2, −1 and the 19 samples −8 … +1 step 0.5
/// (`0x1806d4770`). Spec `a-display-isp.md` §10a.4.</summary>
public sealed record LaplacianPyramidConfig(float Clarity, float Shadows, float Highlights, float Sigma,
                                            float LowerPercentile, float HigherPercentile, float MidPercentile, float[] Samples)
{
    public static readonly float[] DefaultSamples =
        { -8f, -7.5f, -7f, -6.5f, -6f, -5.5f, -5f, -4.5f, -4f, -3.5f, -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f };

    public static LaplacianPyramidConfig FromTuning(Tuning t)
    {
        float G(string k, float d) { try { return (float)t.Num("tone_adjust." + k); } catch (KeyNotFoundException) { return d; } }
        float[] samples = DefaultSamples;
        try { samples = t.Vec("tone_adjust.lpyr_samples").Select(v => (float)v).ToArray(); } catch (KeyNotFoundException) { }
        return new LaplacianPyramidConfig(G("lpyr_clarity", 0f), G("lpyr_shadows", 1f), G("lpyr_highlights", 1f), G("lpyr_sigma", 0.5f),
                                          G("lpyr_lower_percentile", -8f), G("lpyr_higher_percentile", 0.2f), G("lpyr_mid_percentile", -1f), samples);
    }
}

/// <summary>
/// `ToneAdjust:laplacian_pyramid` — slot 13 (`setToneAdjust` `18040a8f0` case 4 → lambda_65 `18041b450`, body
/// `18041b450` → `18039d4c0`; meta **pad 64** / align 1 / scale 1, the only non-trivial pad of the display pipeline
/// and exactly the per-level halo `setInputDataStream` pushes).
/// `Y = (B·0.114) + ((G·0.587) + (R·0.299))`, `Ys = Y·exposureRatio`,
/// `T = exp(CreateAndBlendLaplacianPyramids(ln(max(Ys, 1e-5))))`, `ratio = T·rcpss(Ys + 1e-8)` (a raw `rcpss`,
/// no Newton step), `out = src ⊙ ratio` with lane 3 taken from the source. Spec `a-display-isp.md` §10a.
/// </summary>
public sealed class ToneAdjustLaplacianStage : IStage
{
    public StageName Stage => StageName.ToneAdjust;
    public string TypeString => "laplacian_pyramid";
    public StageMeta Meta => new(64, 1, 1f);   // `movabs r15,0x100000040` @18040a9d8

    static readonly float LumaR = BitConverter.Int32BitsToSingle(0x3e991687);
    static readonly float LumaG = BitConverter.Int32BitsToSingle(0x3f1645a2);
    static readonly float LumaB = BitConverter.Int32BitsToSingle(0x3de978d5);
    static readonly float RatioEps = BitConverter.Int32BitsToSingle(0x322bcc77);   // 1e-8f immediate @18039d694

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ToneAdjust needs the RGB working image");
        var cfg = LaplacianPyramidConfig.FromTuning(p.Context.Tuning);
        float scale = p.Frame.ExposureRatio;                       // FUN_180126860(payload[+0x08])
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) return;
        var src = img.View(abs);
        // FUN_18039d4c0's apron clip (`padL = min(−rect.x0, 32)` …) only bites when the payload image carries a halo
        // beyond the stage region; on the display path the ISP's ROI is the whole grown tile, so the view is the image.
        var dst = new Image<Vec4F>(abs);
        Run(dst, src, scale, cfg);
        p.Rgb = dst;
    }

    /// <summary>`FUN_18039d4c0` — the RGBA wrapper (luma → local tone map → per-pixel ratio → recombine).</summary>
    public static void Run(Image<Vec4F> dst, Image<Vec4F> src, float scale, LaplacianPyramidConfig cfg)
    {
        int w = src.Width, h = src.Height;
        var ys = new Image<float>(w, h);
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y); var o = ys.Row(y);
            for (int x = 0; x < w; x++) { var v = s[x]; o[x] = ((v.B * LumaB) + ((v.G * LumaG) + (v.R * LumaR))) * scale; }
        }
        var t = LocalToneMapLog(ys, scale, cfg);
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y); var d = dst.Row(y); var a = ys.Row(y); var b = t.Row(y);
            for (int x = 0; x < w; x++)
            {
                float r = b[x] * Rcp(a[x] + RatioEps);
                var v = s[x];
                d[x] = new Vec4F(v.R * r, v.G * r, v.B * r, v.A);
            }
        }
    }

    static float Rcp(float x) => Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / x;

    static readonly float Ln2 = BitConverter.Int32BitsToSingle(0x3f317218);
    static readonly float Log2E = BitConverter.Int32BitsToSingle(0x3fb8aa3b);
    static readonly float Eps1e5 = BitConverter.Int32BitsToSingle(0x3727c5ac);

    /// <summary>`FUN_180398d00`: `exp(CreateAndBlend(ln(max(v, 1e-5))))`. Both passes vectorise `w &amp; ~3` columns of
    /// each row with the fast log2/exp2 pair and finish the last `w &amp; 3` with the **CRT** `logf`/`expf` — a real,
    /// reproducible per-row-end difference.</summary>
    public static Image<float> LocalToneMapLog(Image<float> src, float scale, LaplacianPyramidConfig cfg)
    {
        int w = src.Width, h = src.Height, n4 = w & ~3;
        var ln = new Image<float>(w, h);
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y); var d = ln.Row(y);
            for (int x = 0; x < n4; x++) { float v = s[x]; float xx = v > Eps1e5 ? v : Eps1e5; d[x] = FastLog2Exp2.Log2(xx) * Ln2; }
            for (int x = n4; x < w; x++) { float v = s[x]; float xx = v > Eps1e5 ? v : Eps1e5; d[x] = MuslMath.Logf(xx); }
        }
        var blended = CreateAndBlend(ln, scale, cfg);
        var outImg = new Image<float>(w, h);
        for (int y = 0; y < h; y++)
        {
            var s = blended.Row(y); var d = outImg.Row(y);
            for (int x = 0; x < n4; x++)
            {
                float v = s[x] * Log2E;
                v = v > -126.0f ? v : -126.0f;
                v = v < 128.0f ? v : 128.0f;
                d[x] = FastLog2Exp2.Exp2(v);
            }
            for (int x = n4; x < w; x++) d[x] = MuslMath.Expf(s[x]);
        }
        return outImg;
    }

    // remap-LUT constants
    static readonly float RemapStep = BitConverter.Int32BitsToSingle(0x3b824a4e);   // 0.003976142965257168
    static readonly float RemapOrigin = -16.0f;
    static readonly float[] SignTable = { -2.0f, 2.0f };                            // 0x1806d4728 / 0x1806d472c
    // tone-curve nodes 0x1806d483c (x) and 0x1806d4898 (y) — identical, an identity curve in ln units
    static readonly float[] CurveNodes =
    {
        -12f, -11f, -10f, -9f, -8f, -7f, -6f, -5f, -4f, -3f, -2f, -1.5f, -1f, -0.5f, 0f,
        0.33000001311302185f, 0.6600000262260437f, 1f, 1.3300000429153442f, 1.659999966621399f, 2f, 3f, 4f,
    };

    /// <summary>`lt::A::CreateAndBlendLaplacianPyramids` `180399590` (spec §10a.4).</summary>
    public static Image<float> CreateAndBlend(Image<float> src, float scale, LaplacianPyramidConfig cfg)
    {
        var samples = cfg.Samples;
        int ns = samples.Length;
        if (ns < 2) throw new InvalidOperationException("Number of samples to few!");
        if (ns != 2)
        {
            float prev = samples[1];
            for (int i = 2; i < ns; i++)
            {
                if (MathF.Abs(((samples[0] - samples[1]) + samples[i]) - prev) > 1e-4f) throw new InvalidOperationException("samples not uniformly spaced!");
                prev = samples[i];
            }
        }
        int m = Math.Min(src.Width, src.Height);
        int lv = (int)(Math.Log2((double)m) + -2.0);
        int levels = lv > 1 ? lv : 2;
        if (levels >= 7) levels = 6;
        var gauss = new Image<float>[levels - 1];
        var lap = new Image<float>[levels];
        LaplacianPyramid.BuildPyramids(gauss, lap, src);

        // (d) the 8049-entry remap LUT
        var lut = new float[0x1f71];
        float sigma = cfg.Sigma;
        for (int i = 0; i < 0x1f71; i++)
        {
            float x = (float)i * RemapStep + RemapOrigin;
            float y = x;
            if (x <= sigma + sigma && sigma * -2.0f <= x)
            {
                int b = 0.0f < x ? 1 : 0;
                float tt = MathF.Abs(x) / (sigma + sigma);
                if (tt <= 0.0f) tt = 0.0f;
                if (1.0f <= tt) tt = 1.0f;
                y = (SignTable[b] * sigma) * tt;
            }
            float prod = cfg.Clarity * x;
            double e = Math.Exp((double)(x * x) / (((double)sigma * (double)sigma) * -2.0));
            lut[i] = (float)((double)y + e * (double)prod);
        }
        float lo = lut[0], hi = lut[0x1f70];
        float invRange = 1.0f / (hi - lo);

        // (e) one remapped Laplacian pyramid per intensity sample
        var pyramids = new List<Image<float>[]>(ns);
        foreach (float g in samples)
        {
            var tmp = new Image<float>(src.Width, src.Height);
            for (int y = 0; y < src.Height; y++)
            {
                var s = src.Row(y); var o = tmp.Row(y);
                for (int x = 0; x < src.Width; x++)
                {
                    float v = s[x] - g;
                    if (!(v > lo)) v = lo;                       // maxss dst=v, src=lo
                    if (!(v < hi)) v = hi;                       // minss dst=v, src=hi
                    float t = (float)(lut.Length - 1) * ((v - lo) * invRange);
                    int i = (int)t;
                    if (i < 0) i = 0;
                    if (i > lut.Length - 2) i = lut.Length - 2;
                    o[x] = (lut[i] + g) + (t - (float)i) * (lut[i + 1] - lut[i]);
                }
            }
            var pyr = new Image<float>[levels];
            LaplacianPyramid.BuildLaplacian(pyr, tmp);
            pyramids.Add(pyr);
        }

        // (f) per-level blend, levels−2 … 0
        float invSpacing = 1.0f / (samples[1] - samples[0]);
        for (int level = levels - 2; level >= 0; level--)
        {
            var guide = level != 0 ? gauss[level - 1] : src;
            float wgt = level == 2 ? BitConverter.Int32BitsToSingle(0x3f100000) : MuslMath.Powf(BitConverter.Int32BitsToSingle(0x3f400000), (float)level);
            float omw = 1.0f - wgt;
            var target = lap[level];
            foreach (var tile in Tiler.Rects(new RectI(0, 0, target.Width, target.Height), 256, 256))
                for (int y = tile.Y0; y < tile.Y1; y++)
                {
                    var gr = guide.Row(y); var tr = target.Row(y);
                    for (int x = tile.X0; x < tile.X1; x++)
                    {
                        float g = gr[x];
                        int hiI = ns - 1;
                        float t = (g - samples[0]) * invSpacing;
                        t = t > 0.0f ? t : 0.0f;                     // MAXSS(t, +0)
                        float hiF = (float)hiI;
                        t = hiF < t ? hiF : t;                       // MINSS(hiF, t)
                        int i0 = (int)t;
                        int i1 = i0 + 1 <= hiI ? i0 + 1 : hiI;
                        float f = (g - samples[i0]) * invSpacing;
                        f = f > 0.0f ? f : 0.0f;
                        f = f < 1.0f ? f : 1.0f;
                        float l0 = pyramids[i0][level].Row(y)[x], l1 = pyramids[i1][level].Row(y)[x];
                        float acc = (f * l1) + ((1.0f - f) * l0);
                        tr[x] = (tr[x] * omw) + (acc * wgt);
                    }
                }
        }

        // (g) the global tone curve, applied at half resolution
        var curve = BuildCurve(scale, cfg);
        var tmp1 = LaplacianPyramid.Collapse(lap, 1);
        var two = new[] { lap[0], tmp1 };
        ApplyCurve(tmp1, curve.X, curve.Lut);
        return LaplacianPyramid.Collapse(two, 0);
    }

    /// <summary>`0x18039a07b–0x18039a8b2`: the 23 identity nodes lifted by a shadow Gaussian and a highlight ramp,
    /// then a monotone-ish Catmull slope set and a 1024-entry Hermite LUT. The mixed single/double precision of
    /// `h00`/`h10`/`h01` is load-bearing.</summary>
    public static (float[] X, float[] Lut) BuildCurve(float scale, LaplacianPyramidConfig cfg)
    {
        var x = (float[])CurveNodes.Clone();
        var y = (float[])CurveNodes.Clone();
        int n = x.Length;
        float Lo = cfg.LowerPercentile, Hi = cfg.HigherPercentile, Mid = cfg.MidPercentile;
        float p = x[n - 1];
        foreach (var v in x) if (v > Mid) { p = v; break; }          // *upper_bound(x, Mid)
        double S1 = (double)(float)(p * p) + 4.0, S2 = (double)p + 2.0, S3 = (double)(p * 0.05f) + 2.0;
        double det = 1.0 / ((S1 + S1) - S2 * S2 + 1e-15);
        float A = (float)(((S3 + S3) - 1.050000000745058 * S2) * det);
        float B = (float)((1.050000000745058 * S1 - S3 * S2) * det);
        float gw = Lo + 5.0f; gw = gw * gw;
        float t14 = ((Hi + -0.05000000074505806f) / (Hi - Mid)) * (2.0f - Hi) + Hi;
        float hlAmt = t14 * 0.8999999761581421f;
        float k1 = (Hi + 1.0f) * 0.6666666865348816f;
        if (k1 <= 0.11999999731779099f) k1 = 0.11999999731779099f;
        if (1.0f <= k1) k1 = 1.0f;
        float hiG = (1.0f - cfg.Highlights) * k1;
        float sh0 = 2.0f / scale;
        if (sh0 <= 0.0f) sh0 = 0.0f;
        if (1.0f <= sh0) sh0 = 1.0f;
        float sh1 = (8.0f - Lo) * 0.04545454680919647f;
        if (sh1 <= 0.11999999731779099f) sh1 = 0.11999999731779099f;
        if (1.2000000476837158f <= sh1) sh1 = 1.2000000476837158f;
        sh1 = sh1 * (1.0f - cfg.Shadows);
        float shG = (sh0 * 1.2999999523162842f) * sh1;
        for (int i = 0; i < n; i++)
        {
            float xi = x[i];
            float den = xi < -5.0f ? gw : 14.4399995803833f;
            float gpar = MuslMath.Expf((((xi + 5.0f) * (xi + 5.0f)) * -2.5649492740631104f) / den);
            float hpar = 0.0f;
            if (p <= xi)
            {
                float u = xi * A + B;
                if (u <= 0.0f) u = 0.0f;
                if (1.0f <= u) u = 1.0f;
                hpar = (u * u) * hlAmt;
            }
            y[i] += (gpar * shG) - (hpar * hiG);
            if (-6.0f < Lo && cfg.Shadows != 1.0f)
            {
                float v = (-6.0f - Lo) * 1.2999999523162842f;
                if (-6.0f < xi)
                {
                    if (xi >= 0.0f) continue;
                    float u = (xi + 6.0f) * 0.1666666716337204f;
                    if (u <= 0.0f) u = 0.0f;
                    if (1.0f <= u) u = 1.0f;
                    v = v * ((1.0f - u) * (1.0f - u));
                }
                y[i] += v;
            }
        }
        var mm = new float[n];
        mm[0] = (y[1] - y[0]) / (x[1] - x[0]);
        mm[n - 1] = (y[n - 1] - y[n - 2]) / (x[n - 1] - x[n - 2]);
        for (int i = 1; i <= n - 2; i++)
            mm[i] = ((y[i] - y[i - 1]) / (x[i] - x[i - 1]) + (y[i + 1] - y[i]) / (x[i + 1] - x[i])) * 0.5f;
        var lut = new float[1024];
        float step = (x[n - 1] - x[0]) * 0.0009775171056389809f;
        for (int k = 0; k < 1024; k++)
        {
            float xv = (float)k * step + x[0];
            int j = 0;
            while (j < n - 1 && (x[j] > xv || x[j + 1] < xv)) j++;
            if (j >= n - 1) { lut[k] = y[n - 1]; continue; }
            float D = x[j + 1] - x[j];
            float t = (xv - x[j]) / D;
            float h00 = (float)((double)((1.0f - t) * (1.0f - t)) * ((double)(t + t) + 1.0));
            float h10 = (float)((1.0 - (double)t) * (1.0 - (double)t) * (double)t);
            float h01 = (float)((3.0 - ((double)t + (double)t)) * (double)(t * t));
            float h11 = (t + -1.0f) * (t * t);
            lut[k] = ((h11 * mm[j + 1] + h10 * mm[j]) * D) + ((h01 * y[j + 1]) + (h00 * y[j]));
        }
        return (x, lut);
    }

    /// <summary>`ApplyCurveToImage::lambda_1` `18039ed00`, in place, 256×256 tiles, fully scalar. The index is clamped,
    /// the fraction is not, so values outside the node range extrapolate along the first/last LUT segment.</summary>
    public static void ApplyCurve(Image<float> img, float[] x, float[] lut)
    {
        int n = x.Length, N = lut.Length;
        float x0 = x[0], span = x[n - 1] - x[0];
        foreach (var tile in Tiler.Rects(new RectI(0, 0, img.Width, img.Height), 256, 256))
            for (int y = tile.Y0; y < tile.Y1; y++)
            {
                var row = img.Row(y);
                for (int xx = tile.X0; xx < tile.X1; xx++)
                {
                    float u = (row[xx] - x0) / span;
                    float pf = (float)(N - 1) * u;
                    int i = (int)pf;
                    if (i < 0) i = 0;
                    if (i > N - 2) i = N - 2;
                    row[xx] = ((pf - (float)i) * (lut[i + 1] - lut[i])) + lut[i];
                }
            }
    }
}
