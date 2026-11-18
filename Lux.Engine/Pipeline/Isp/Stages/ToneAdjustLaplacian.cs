using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`A::LaplacianPyramidConfig` — the only state the local-Laplacian
/// kernel reads. Constructor defaults 0 / 1 / 1 / 0.5, −8, 0.2, −1 and the 19 samples −8 … +1 step 0.5.</summary>
public sealed record LaplacianPyramidConfig(float Clarity, float Shadows, float Highlights, float Sigma,
                                            float LowerPercentile, float HigherPercentile, float MidPercentile, float[] Samples)
{
    public static readonly float[] DefaultSamples =
        { -8f, -7.5f, -7f, -6.5f, -6f, -5.5f, -5f, -4.5f, -4f, -3.5f, -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f };

    public static LaplacianPyramidConfig FromTuning(Tuning t)
    {
        float G(string k, float d) => (float)t.NumOr("tone_adjust." + k, d);
        float[] samples = DefaultSamples;
        if (t.VecOrNull("tone_adjust.lpyr_samples") is { } set) samples = set.Select(v => (float)v).ToArray();
        return new LaplacianPyramidConfig(G("lpyr_clarity", 0f), G("lpyr_shadows", 1f), G("lpyr_highlights", 1f), G("lpyr_sigma", 0.5f),
                                          G("lpyr_lower_percentile", -8f), G("lpyr_higher_percentile", 0.2f), G("lpyr_mid_percentile", -1f), samples);
    }
}

/// <summary>
/// `ToneAdjust:laplacian_pyramid` — slot 13 (`setToneAdjust` case 4; meta **pad 64** / align 1 / scale 1, the only
/// non-trivial pad of the display pipeline
/// and exactly the per-level halo `setInputDataStream` pushes).
/// `Y = (B·0.114) + ((G·0.587) + (R·0.299))`, `Ys = Y·exposureRatio`,
/// `T = exp(CreateAndBlendLaplacianPyramids(ln(max(Ys, 1e-5))))`, `ratio = T·rcpss(Ys + 1e-8)` (a raw `rcpss`,
/// no Newton step), `out = src ⊙ ratio` with lane 3 taken from the source.
/// </summary>
public sealed class ToneAdjustLaplacianStage : IStage
{
    public StageName Stage => StageName.ToneAdjust;
    public string TypeString => "laplacian_pyramid";
    public StageMeta Meta => new(64, 1, 1f);   // 0x100000040 = pad 64, align 1

    static readonly float LumaR = BitConverter.Int32BitsToSingle(0x3e991687);
    static readonly float LumaG = BitConverter.Int32BitsToSingle(0x3f1645a2);
    static readonly float LumaB = BitConverter.Int32BitsToSingle(0x3de978d5);
    static readonly float RatioEps = BitConverter.Int32BitsToSingle(0x322bcc77);   // 1e-8f

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ToneAdjust needs the RGB working image");
        var cfg = LaplacianPyramidConfig.FromTuning(p.Context.Tuning);
        float scale = p.Frame.ExposureRatio;                       // the frame's exposure ratio
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) return;
        var src = img.View(abs);
        // Lumen's apron clip (`padL = min(−rect.x0, 32)` …) only bites when the payload image carries a halo
        // beyond the stage region; on the display path the ISP's ROI is the whole grown tile, so the view is the image.
        var dst = new Image<Vec4F>(abs);
        using (StageParallel.Enter()) Run(dst, src, scale, cfg);
        p.Rgb = dst;
    }

    /// <summary>The 19 sample pyramids of one run are spread over threads only when few stage runs are in progress (a drag frame, a coarse
    /// level).</summary>
    static bool SpreadSamples => StageParallel.Spread;

    /// <summary>The RGBA wrapper (luma → local tone map → per-pixel ratio → recombine).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void Run(Image<Vec4F> dst, Image<Vec4F> src, float scale, LaplacianPyramidConfig cfg)
    {
        int w = src.Width, h = src.Height;
        var al = new LaplacianPyramid.Alloc();
        var ys = al.Image(w, h);
        for (int y = 0; y < h; y++)
        {
            var s = MemoryMarshal.Cast<Vec4F, float>(src.Row(y)); var o = ys.Row(y);
            int x = 0;
            if (Simd.IsWideSupported)
            {
                // eight pixels: R, G, B lanes gathered (stride 4); the same three products and two sums per pixel
                ref float sb = ref MemoryMarshal.GetReference(s); ref float ob = ref MemoryMarshal.GetReference(o);
                var idx = Vector256.Create(0, 4, 8, 12, 16, 20, 24, 28); var lr = Vector256.Create(LumaR); var lg = Vector256.Create(LumaG); var lb = Vector256.Create(LumaB); var sc = Vector256.Create(scale);
                for (; x + 8 <= w; x += 8)
                    unsafe
                    {
                        fixed (float* p = &Unsafe.Add(ref sb, x * 4))
                        {
                            var R = Simd.GatherVector256(p, idx, 4); var G = Simd.GatherVector256(p + 1, idx, 4); var B = Simd.GatherVector256(p + 2, idx, 4);
                            (((B * lb) + ((G * lg) + (R * lr))) * sc).StoreUnsafe(ref ob, (nuint)x);
                        }
                    }
            }
            for (; x < w; x++) { float R = s[x * 4], G = s[x * 4 + 1], B = s[x * 4 + 2]; o[x] = ((B * LumaB) + ((G * LumaG) + (R * LumaR))) * scale; }
        }
        Image<float> t; using (Diagnostics.PhaseLog.Time("ta.log+blend+exp")) t = LocalToneMapLog(ys, scale, cfg, al);
        Span<float> rs = stackalloc float[8];
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y); var d = dst.Row(y); var a = ys.Row(y); var b = t.Row(y);
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float ab = ref MemoryMarshal.GetReference(a); ref float bb = ref MemoryMarshal.GetReference(b);
                ref float sb = ref Unsafe.As<Vec4F, float>(ref MemoryMarshal.GetReference(s)); ref float db = ref Unsafe.As<Vec4F, float>(ref MemoryMarshal.GetReference(d));
                var eps = Vector256.Create(RatioEps); var rgbMask = Vector128.Create(-1, -1, -1, 0).AsSingle();
                for (; x + 8 <= w; x += 8)
                {
                    // r = T·rcpss(Ys + 1e-8) for eight pixels (the eight-lane model of the same table), then each pixel's RGB · r, alpha kept
                    (Vector256.LoadUnsafe(ref bb, (nuint)x) * IntelApprox.Reciprocal(Vector256.LoadUnsafe(ref ab, (nuint)x) + eps)).CopyTo(rs);
                    for (int k = 0; k < 8; k++)
                    {
                        var v = Vector128.LoadUnsafe(ref sb, (nuint)((x + k) * 4));
                        Vector128.ConditionalSelect(rgbMask, v * Vector128.Create(rs[k]), v).StoreUnsafe(ref db, (nuint)((x + k) * 4));
                    }
                }
            }
            for (; x < w; x++)
            {
                float r = b[x] * Rcp(a[x] + RatioEps);
                var v = s[x];
                d[x] = new Vec4F(v.R * r, v.G * r, v.B * r, v.A);
            }
        }
        al.ReturnAll();
    }

    static float Rcp(float x) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();

    static readonly float Ln2 = BitConverter.Int32BitsToSingle(0x3f317218);
    static readonly float Log2E = BitConverter.Int32BitsToSingle(0x3fb8aa3b);
    static readonly float Eps1e5 = BitConverter.Int32BitsToSingle(0x3727c5ac);

    /// <summary>`exp(CreateAndBlend(ln(max(v, 1e-5))))`. Both passes vectorize `w &amp; ~3` columns of
    /// each row with the fast log2/exp2 pair and finish the last `w &amp; 3` with the **CRT** `logf`/`expf` — a real,
    /// reproducible per-row-end difference.</summary>
    public static Image<float> LocalToneMapLog(Image<float> src, float scale, LaplacianPyramidConfig cfg) => LocalToneMapLog(src, scale, cfg, null);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static Image<float> LocalToneMapLog(Image<float> src, float scale, LaplacianPyramidConfig cfg, LaplacianPyramid.Alloc? al)
    {
        int w = src.Width, h = src.Height, n4 = w & ~3;
        var ln = al is null ? new Image<float>(w, h) : al.Image(w, h);
        var eps = Vector256.Create(Eps1e5); var ln2 = Vector256.Create(Ln2);
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y); var d = ln.Row(y);
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float sb = ref MemoryMarshal.GetReference(s); ref float db = ref MemoryMarshal.GetReference(d);
                for (; x + 8 <= n4; x += 8)
                {
                    var v = Vector256.LoadUnsafe(ref sb, (nuint)x);
                    (Log2(Vector256.ConditionalSelect(Vector256.GreaterThan(v, eps), v, eps)) * ln2).StoreUnsafe(ref db, (nuint)x);
                }
            }
            for (; x < n4; x++) { float v = s[x]; float xx = v > Eps1e5 ? v : Eps1e5; d[x] = FastLog2Exp2.Log2(xx) * Ln2; }
            for (x = n4; x < w; x++) { float v = s[x]; float xx = v > Eps1e5 ? v : Eps1e5; d[x] = MuslMath.Logf(xx); }
        }
        var blended = CreateAndBlend(ln, scale, cfg, al);
        var outImg = al is null ? new Image<float>(w, h) : al.Image(w, h);
        var l2e = Vector256.Create(Log2E); var lo = Vector256.Create(-126.0f); var hi = Vector256.Create(128.0f);
        for (int y = 0; y < h; y++)
        {
            var s = blended.Row(y); var d = outImg.Row(y);
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float sb = ref MemoryMarshal.GetReference(s); ref float db = ref MemoryMarshal.GetReference(d);
                for (; x + 8 <= n4; x += 8)
                {
                    var v = Vector256.LoadUnsafe(ref sb, (nuint)x) * l2e;
                    v = Vector256.ConditionalSelect(Vector256.GreaterThan(v, lo), v, lo);
                    v = Vector256.ConditionalSelect(Vector256.LessThan(v, hi), v, hi);
                    Exp2(v).StoreUnsafe(ref db, (nuint)x);
                }
            }
            for (; x < n4; x++)
            {
                float v = s[x] * Log2E;
                v = v > -126.0f ? v : -126.0f;
                v = v < 128.0f ? v : 128.0f;
                d[x] = FastLog2Exp2.Exp2(v);
            }
            for (x = n4; x < w; x++) d[x] = MuslMath.Expf(s[x]);
        }
        return outImg;
    }

    /// <summary><see cref="FastLog2Exp2.Log2"/> on eight lanes (the same integer and float steps per lane).</summary>
    static Vector256<float> Log2(Vector256<float> x)
    {
        var bits = x.AsInt32();
        var m = ((bits & Vector256.Create(0x007fffff)) | Vector256.Create(0x3f800000)).AsSingle();
        var poly = ((m * Vector256.Create(FastLog2Exp2.C0) + Vector256.Create(FastLog2Exp2.C1)) * m + Vector256.Create(FastLog2Exp2.C2)) * m;
        var ef = Vector256.ConvertToSingle(Vector256.ShiftRightArithmetic(bits + Vector256.Create(unchecked((int)0xc0800000)), 23)) + Vector256.Create(FastLog2Exp2.C3);
        return poly + ef;
    }

    /// <summary><see cref="FastLog2Exp2.Exp2"/> on eight lanes (y in [−126, 128]: the truncation is in range).</summary>
    static Vector256<float> Exp2(Vector256<float> y)
    {
        var i = Vector256.ConvertToInt32(y) + Vector256.ShiftRightArithmetic(y.AsInt32(), 31);
        var f = y - Vector256.ConvertToSingle(i);
        var p = ((f * Vector256.Create(FastLog2Exp2.D0) + Vector256.Create(FastLog2Exp2.D1)) * f + Vector256.Create(FastLog2Exp2.D2)) * f + Vector256.Create(FastLog2Exp2.D3);
        return (Vector256.ShiftLeft(i, 23) + p.AsInt32()).AsSingle();
    }

    // remap-LUT constants
    static readonly float RemapStep = BitConverter.Int32BitsToSingle(0x3b824a4e);   // 0.003976142965257168
    static readonly float RemapOrigin = -16.0f;
    static readonly float[] SignTable = { -2.0f, 2.0f };
    // tone-curve nodes x and y — identical, an identity curve in ln units
    static readonly float[] CurveNodes =
    {
        -12f, -11f, -10f, -9f, -8f, -7f, -6f, -5f, -4f, -3f, -2f, -1.5f, -1f, -0.5f, 0f,
        0.33000001311302185f, 0.6600000262260437f, 1f, 1.3300000429153442f, 1.659999966621399f, 2f, 3f, 4f,
    };

    /// <summary>`lt::A::CreateAndBlendLaplacianPyramids`.</summary>
    public static Image<float> CreateAndBlend(Image<float> src, float scale, LaplacianPyramidConfig cfg) => CreateAndBlend(src, scale, cfg, null);

    /// <summary>The 8049-entry remap LUT of (sigma, clarity) — a pure function of the two, kept once made.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float[]> RemapLuts = new();
    static float[] RemapLut(float sigma, float clarity) => RemapLuts.GetOrAdd((BitConverter.SingleToInt32Bits(sigma), BitConverter.SingleToInt32Bits(clarity)), static k =>
    {
        float sigma = BitConverter.Int32BitsToSingle(k.Item1), clarity = BitConverter.Int32BitsToSingle(k.Item2);
        var lut = new float[0x1f71];
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
            float prod = clarity * x;
            double e = WineCrt.Exp((double)(x * x) / (((double)sigma * (double)sigma) * -2.0));
            lut[i] = (float)((double)y + e * (double)prod);
        }
        return lut;
    });

    /// <summary>
    /// `CreateAndBlendLaplacianPyramids`, memory-saving form. The blend of level L reads, per pixel, level L of two of the 19 remapped pyramids:
    /// sample i0 (from the guide value) and i1 = min(i0 + 1, n − 1). Instead of keeping all 19 pyramids, each sample's pyramid is built in turn
    /// (in any order, several at once when few runs are in progress) and its level values are copied to plane A where it is a pixel's i0 and
    /// to plane B where it is its i1 — every pixel's i0 and i1 are fixed by the guide, so each element of A and B is written once, by one
    /// sample, with exactly the value the full pyramid holds. The blend then reads A and B: the same floats in the same operations.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static Image<float> CreateAndBlend(Image<float> src, float scale, LaplacianPyramidConfig cfg, LaplacianPyramid.Alloc? al)
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
        if (ns > 255) throw new InvalidOperationException("too many samples");
        int m = Math.Min(src.Width, src.Height);
        int lv = (int)(WineCrt.Log2((double)m) + -2.0);
        int levels = lv > 1 ? lv : 2;
        if (levels >= 7) levels = 6;
        var own = al ?? new LaplacianPyramid.Alloc();
        var gauss = new Image<float>[levels - 1];
        var lap = new Image<float>[levels];
        var ph = Diagnostics.PhaseLog.Time("ta.pyramids+idx");
        LaplacianPyramid.BuildPyramids(gauss, lap, src, own);

        // (d) the 8049-entry remap LUT
        var lut = RemapLut(cfg.Sigma, cfg.Clarity);
        float lo = lut[0], hi = lut[0x1f70];
        float invRange = 1.0f / (hi - lo);

        // the guide's sample index per pixel and blended level (t = clamp((g − s0)·invSpacing, 0, n−1), i0 = (int)t)
        float invSpacing = 1.0f / (samples[1] - samples[0]);
        int nb = levels - 1;   // levels 0 … levels − 2 are blended
        int hiI = ns - 1; float hiF = (float)hiI;
        var idx = new byte[nb][]; var A = new Image<float>[nb]; var B = new Image<float>[nb];
        for (int level = 0; level < nb; level++)
        {
            var guide = level != 0 ? gauss[level - 1] : src; int gw = guide.Width, gh = guide.Height;
            var ix = idx[level] = new byte[gw * gh];
            for (int y = 0; y < gh; y++)
            {
                var gr = guide.Row(y);
                for (int x = 0; x < gw; x++)
                {
                    float t = (gr[x] - samples[0]) * invSpacing;
                    t = t > 0.0f ? t : 0.0f;
                    t = hiF < t ? hiF : t;
                    ix[y * gw + x] = (byte)(int)t;
                }
            }
            A[level] = own.Image(gw, gh); B[level] = own.Image(gw, gh);
        }

        ph.Dispose();
        // (e) one remapped Laplacian pyramid per intensity sample, its blended levels scattered into A / B
        // Each sample's pyramid without its full-resolution images: the remapped rows are made as the first decimation reads them (a ring of
        // rows), the levels from 1 down are built as before, and the level-0 Laplacian PyrUp(g1) − remap(src) goes row segment by row segment
        // straight into A / B. Every value is the one BuildLaplacian(remap(src)) gives (the same functions on the same inputs, row by row).
        void Sample(int si)
        {
            float g = samples[si];
            int W = src.Width, H = src.Height;
            var sal = new LaplacianPyramid.Alloc();
            const int Ring = 64;   // a decimation band reads at most 36 consecutive rows
            var ring = ScratchPool.Rent<float>(Ring * W); var tag = new int[Ring]; Array.Fill(tag, -1);
            ReadOnlySpan<float> RowOf(int y)
            {
                int k = y & (Ring - 1);
                var span = ring.AsSpan(k * W, W);
                if (tag[k] != y) { RemapRow(src.Row(y), span, g, lut, lo, hi, invRange); tag[k] = y; }
                return span;
            }
            var g1 = sal.Image((W + 1) >> 1, (H + 1) >> 1);
            LaplacianPyramid.PyrDown(g1, W, H, RowOf);
            var rest = new Image<float>[levels - 1];
            LaplacianPyramid.BuildLaplacian(rest, g1, sal);
            for (int level = 1; level < nb; level++) Scatter(rest[level - 1], idx[level], A[level], B[level], si, hiI);
            var seg = new float[W]; var ix0 = idx[0]; var a0 = A[0]; var b0 = B[0];
            LaplacianPyramid.PyrUpSub(W, H, g1,
                (y, x0, x1) => { RemapRow(src.Row(y).Slice(x0, x1 - x0), seg.AsSpan(x0, x1 - x0), g, lut, lo, hi, invRange); return seg; },
                (y, x0, x1, row) =>
                {
                    var ar = a0.Row(y); var br = b0.Row(y); int o = y * W;
                    for (int x = x0; x < x1; x++)
                    {
                        int i0 = ix0[o + x];
                        if (i0 == si) ar[x] = row[x];
                        int i1 = i0 + 1 <= hiI ? i0 + 1 : hiI;
                        if (i1 == si) br[x] = row[x];
                    }
                });
            ScratchPool.Return(ring); sal.ReturnAll();
        }
        using (Diagnostics.PhaseLog.Time("ta.samples"))
        {
            if (SpreadSamples) Parallel.For(0, ns, Sample);
            else for (int si = 0; si < ns; si++) Sample(si);
        }
        var phb = Diagnostics.PhaseLog.Time("ta.blend+collapse");

        // (f) per-level blend, levels−2 … 0
        for (int level = levels - 2; level >= 0; level--)
        {
            var guide = level != 0 ? gauss[level - 1] : src;
            float wgt = level == 2 ? BitConverter.Int32BitsToSingle(0x3f100000) : MuslMath.Powf(BitConverter.Int32BitsToSingle(0x3f400000), (float)level);
            float omw = 1.0f - wgt;
            var target = lap[level]; var ix = idx[level]; var a = A[level]; var b = B[level]; int tw = target.Width;
            for (int y = 0; y < target.Height; y++)
            {
                var gr = guide.Row(y); var tr = target.Row(y); var ar = a.Row(y); var br = b.Row(y);
                for (int x = 0; x < tw; x++)
                {
                    float g = gr[x];
                    int i0 = ix[y * tw + x];
                    float f = (g - samples[i0]) * invSpacing;
                    f = f > 0.0f ? f : 0.0f;
                    f = f < 1.0f ? f : 1.0f;
                    float acc = (f * br[x]) + ((1.0f - f) * ar[x]);
                    tr[x] = (tr[x] * omw) + (acc * wgt);
                }
            }
        }

        // (g) the global tone curve, applied at half resolution
        var curve = BuildCurve(scale, cfg);
        var tmp1 = LaplacianPyramid.Collapse(lap, 1, own);
        var two = new[] { lap[0], tmp1 };
        ApplyCurve(tmp1, curve.X, curve.Lut);
        phb.Dispose();
        if (al is not null) return LaplacianPyramid.Collapse(two, 0, al);
        var result = LaplacianPyramid.Collapse(two, 0);
        own.ReturnAll();
        return result;
    }

    /// <summary>The remap of one sample g: v = clamp(s − g, lo, hi) (maxss / minss), t = 8048·((v − lo)·invRange), i = (int)t clamped to
    /// [0, 8047], out = (lut[i] + g) + (t − i)·(lut[i+1] − lut[i]); eight lanes with two gathers.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void Remap(Image<float> src, Image<float> dst, float g, float[] lut, float lo, float hi, float invRange)
    {
        for (int y = 0; y < src.Height; y++) RemapRow(src.Row(y), dst.Row(y), g, lut, lo, hi, invRange);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static unsafe void RemapRow(ReadOnlySpan<float> s, Span<float> o, float g, float[] lut, float lo, float hi, float invRange)
    {
        int w = s.Length;
        float n1 = (float)(lut.Length - 1);
        fixed (float* lp = lut)
        {
            int x = 0;
            if (Simd.IsWideSupported)
            {
                ref float sb = ref MemoryMarshal.GetReference(s); ref float ob = ref MemoryMarshal.GetReference(o);
                var G = Vector256.Create(g); var LO = Vector256.Create(lo); var HI = Vector256.Create(hi); var INV = Vector256.Create(invRange); var N1 = Vector256.Create(n1);
                var zero = Vector256<int>.Zero; var top = Vector256.Create(lut.Length - 2); var one = Vector256.Create(1);
                for (; x + 8 <= w; x += 8)
                {
                    var v = Vector256.LoadUnsafe(ref sb, (nuint)x) - G;
                    v = Vector256.ConditionalSelect(Vector256.GreaterThan(v, LO), v, LO);
                    v = Vector256.ConditionalSelect(Vector256.LessThan(v, HI), v, HI);
                    var t = N1 * ((v - LO) * INV);
                    var i = Vector256.ConvertToInt32(t);
                    i = Vector256.Max(i, zero); i = Vector256.Min(i, top);
                    var l0 = Simd.GatherVector256(lp, i, 4); var l1 = Simd.GatherVector256(lp, i + one, 4);
                    ((l0 + G) + (t - Vector256.ConvertToSingle(i)) * (l1 - l0)).StoreUnsafe(ref ob, (nuint)x);
                }
            }
            for (; x < w; x++)
            {
                float v = s[x] - g;
                if (!(v > lo)) v = lo;                       // MAXSS order: NaN gives lo
                if (!(v < hi)) v = hi;                       // MINSS order: NaN gives hi
                float t = n1 * ((v - lo) * invRange);
                int i = (int)t;
                if (i < 0) i = 0;
                if (i > lut.Length - 2) i = lut.Length - 2;
                o[x] = (lut[i] + g) + (t - (float)i) * (lut[i + 1] - lut[i]);
            }
        }
    }

    /// <summary>A[p] = level[p] where idx[p] = si; B[p] = level[p] where min(idx[p] + 1, hi) = si.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void Scatter(Image<float> level, byte[] idx, Image<float> a, Image<float> b, int si, int hiI)
    {
        int w = level.Width;
        for (int y = 0; y < level.Height; y++)
        {
            var v = level.Row(y); var ar = a.Row(y); var br = b.Row(y); int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int i0 = idx[o + x];
                if (i0 == si) ar[x] = v[x];
                int i1 = i0 + 1 <= hiI ? i0 + 1 : hiI;
                if (i1 == si) br[x] = v[x];
            }
        }
    }

    /// <summary>The tone curve: the 23 identity nodes lifted by a shadow Gaussian and a highlight ramp,
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

    /// <summary>The `ApplyCurveToImage` tile lambda, in place, 256×256 tiles, fully scalar. The index is clamped,
    /// the fraction is not, so values outside the node range extrapolate along the first/last LUT segment.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
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
