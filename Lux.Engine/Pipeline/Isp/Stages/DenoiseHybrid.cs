using System.Runtime.CompilerServices;
using Lux.Engine.Imaging;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `Denoising:hybrid` (slot 9): Lumen's hybrid stage glue (context ring of ≤ 63 px, σ image
/// `Sensor::forwardSTD`, threshold = threshold_multiplier·parameter_scale (×1.12 for the collapse demosaics)) around the hybrid core: RGB→(Y,C1,C2)
/// rotation, a 5-level Gaussian pyramid, per level the trapezoid bilateral N×N (`BilateralGeneric&lt;N,0&gt;`, N = `bilateral_denoiser.window_size`
/// ∈ {3,5,7,9}) and the `SubtractUpscaleAndAdd`
/// reconstruction, a full-resolution jittered `ImageDenoisePatchNLM&lt;4&gt;`, rotation back. All arithmetic in Lumen's order; `rcpps` raw.
/// </summary>
public sealed class DenoiseHybridStage : IStage
{
    public StageName Stage => StageName.Denoising;
    public string TypeString => "hybrid";
    public StageMeta Meta => new(127, 1, 1f);   // setDenoising case 6: (pad 0x7f, align 1, scale 1)

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("Denoising:hybrid needs the RGB working image");
        var t = p.Context.Tuning;
        float Get(string k, float d) { try { return (float)t.Num(k); } catch (KeyNotFoundException) { return d; } }
        var nlm = new HybridDenoise.NlmParams { WindowSize = (int)Get("nlm_denoiser.window_size", 5), PatchSize = (int)Get("nlm_denoiser.patch_size", 5), StepSize = (int)Get("nlm_denoiser.step_size", 2), ChromaBoost = Get("nlm_denoiser.chroma_boost", 2f), PyramidSize = (int)Get("nlm_denoiser.pyramid_size", 5), MinLumaStd = Get("nlm_denoiser.min_luma_std", 0.0025f) };
        var bil = new HybridDenoise.BilateralParams { WindowSize = (int)Get("bilateral_denoiser.window_size", 5), ChromaBoost = Get("bilateral_denoiser.chroma_boost", 2f), PyramidSize = (int)Get("bilateral_denoiser.pyramid_size", 5) };
        float thr = Get("denoising.threshold_multiplier", 1f) * Get("pipeline.parameter_scale", 1f);
        string demosaic = t.Type("demosaicking");
        if (demosaic is "collapse2" or "collapse4" or "collapse8") thr = (float)((double)thr * 1.12);
        if (Environment.GetEnvironmentVariable("LUX_ISP_DEBUG") == "1") Console.Error.WriteLine($"[hybrid] thr {thr:R} gain {p.Frame.AnalogGain:R} black {(!float.IsNaN(p.Stats.SensorBlack) ? p.Stats.SensorBlack : -1):R} neutral {p.Stats.Neutral[0]:R} {p.Stats.Neutral[1]:R} {p.Stats.Neutral[2]:R} tuning thr_mult {p.Context.Tuning.Num("denoising.threshold_multiplier"):R} pscale {p.Context.Tuning.Num("pipeline.parameter_scale"):R} nlm.chroma {p.Context.Tuning.Num("nlm_denoiser.chroma_boost"):R} bil.chroma {p.Context.Tuning.Num("bilateral_denoiser.chroma_boost"):R} min_luma {p.Context.Tuning.Num("nlm_denoiser.min_luma_std"):R}");
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) return;
        int padL = Math.Min(63, abs.X0 - img.Rect.X0), padT = Math.Min(63, abs.Y0 - img.Rect.Y0), padR = Math.Min(63, img.Rect.X1 - abs.X1), padB = Math.Min(63, img.Rect.Y1 - abs.Y1);
        var view = new RectI(abs.X0 - padL, abs.Y0 - padT, abs.X1 + padR, abs.Y1 + padB);
        int w = view.Width, h = view.Height;
        var src = new Vec4F[w * h];
        for (int y = 0; y < h; y++) img.Row(view.Y0 - img.Rect.Y0 + y).Slice(view.X0 - img.Rect.X0, w).CopyTo(src.AsSpan(y * w, w));
        var noise = p.Stats.Noise ?? p.Frame.Noise ?? throw new InvalidOperationException("Denoising:hybrid needs the sensor noise model");
        float black = !float.IsNaN(p.Stats.SensorBlack) ? p.Stats.SensorBlack : noise.Black, white = !float.IsNaN(p.Stats.SensorWhite) ? p.Stats.SensorWhite : noise.White;
        Vec4F[] std; using (Diagnostics.PhaseLog.Time("hyb.fstd")) std = HybridDenoise.ForwardStd(src, w, h, noise.ModelForGain(p.Frame.AnalogGain), black, white, p.Stats.Neutral);
        // When the payload carries a STD plane (the level-1 FusionCacheBayer weight image), it is viewed exactly like the RGB source
        // (∩ int rect, grown by min(63, available) per side of ITS OWN rect). Its size must equal the σ image ("std Image provided does
        // not match image size!" / "image size mismatch!"). Then Lumen multiplies the forwardSTD σ in place, per pixel by view-local
        // index, the scalar broadcast to all four lanes: σ_c(x,y) = std(x,y)·σ_c(x,y). This happens BEFORE the
        // hybrid core (rotation / ImageTransformSTD).
        // No STD (single capture) → σ unchanged.
        if (p.Std is not null && p.Std.Width > 0 && p.Std.Height > 0)
        {
            var sabs = p.ToAbsolute(p.IntRect).Intersect(p.Std.Rect);
            int sw = 0, sh = 0; Image<float>? sview = null;
            if (!sabs.IsEmpty)
            {
                int sl = Math.Min(63, sabs.X0 - p.Std.Rect.X0), st = Math.Min(63, sabs.Y0 - p.Std.Rect.Y0), sr = Math.Min(63, p.Std.Rect.X1 - sabs.X1), sb = Math.Min(63, p.Std.Rect.Y1 - sabs.Y1);
                var srect = new RectI(sabs.X0 - sl, sabs.Y0 - st, sabs.X1 + sr, sabs.Y1 + sb);
                sview = p.Std.View(srect); sw = srect.Width; sh = srect.Height;
            }
            if (sw != w || sh != h) throw new InvalidOperationException("std Image provided does not match image size!");
            for (int y = 0; y < h; y++)
            {
                var srow = sview!.Row(y);
                for (int x = 0; x < w; x++) { float m = srow[x]; ref var q = ref std[y * w + x]; q = new Vec4F(m * q.R, m * q.G, m * q.B, m * q.A); }
            }
            HybridDenoise.DumpStd(std, w, h);
        }
        Vec4F[] outp; using (Diagnostics.PhaseLog.Time("hyb.run")) outp = HybridDenoise.Run(src, std, w, h, thr, nlm, bil);
        using var phc = Diagnostics.PhaseLog.Time("hyb.copies");
        // the stage swaps the denoised image into the working slot; the previous working image stays in the payload and PostProcessing uses it as the LDiff companion
        var before = new Image<Vec4F>(img.Rect); for (int y = 0; y < img.Height; y++) img.Row(y).CopyTo(before.Row(y)); p.Companion = before;
        for (int y = 0; y < abs.Height; y++) outp.AsSpan((y + padT) * w + padL, abs.Width).CopyTo(img.Row(abs.Y0 - img.Rect.Y0 + y).Slice(abs.X0 - img.Rect.X0, abs.Width));
    }
}

public static class HybridDenoise
{
    /// <summary>Diagnostic: `LUX_HYB_DUMP=&lt;prefix&gt;` writes every intermediate as `{prefix}_hyb_{tag}_{w}x{h}.f32`: raw 32-bit floats, four per pixel, no header.</summary>
    static readonly string? DumpPrefix = Environment.GetEnvironmentVariable("LUX_HYB_DUMP");
    static void Dump(string tag, Vec4F[] img, int w, int h)
    {
        if (DumpPrefix is null) return;
        using var fo = File.Create($"{DumpPrefix}_hyb_{tag}_{w}x{h}.f32");   // the size keeps calls of different ISPs (tiles vs a telephoto grown rect) from overwriting each other
        var bytes = new byte[img.Length * 16]; System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.AsSpan()).CopyTo(bytes); fo.Write(bytes);
    }
    internal static void DumpStd(Vec4F[] img, int w, int h) => Dump("stdmul_out", img, w, h);
    public sealed class NlmParams { public int WindowSize = 5, PatchSize = 5, StepSize = 2, PyramidSize = 5; public float ChromaBoost = 2f, MinLumaStd = 0f; }
    public sealed class BilateralParams { public int WindowSize = 5, PyramidSize = 5; public float ChromaBoost = 2f; }

    static float Rcp(float d) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar();
    static float Max(float a, float b) => a > b ? a : b;          // MAXPS semantics: NaN gives b
    static Vec4F Add(Vec4F a, Vec4F b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    static float Abs(float a) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(a) & 0x7fffffff);

    // ---- the four lanes of a pixel as one Vector128 — every lane does exactly its scalar operations in the same order ----
    static Vector128<float> V(in Vec4F v) => Vector128.Create(v.R, v.G, v.B, v.A);
    static Vec4F F4(Vector128<float> v) => new(v.GetElement(0), v.GetElement(1), v.GetElement(2), v.GetElement(3));
    static Vector128<float> MaxV(Vector128<float> a, Vector128<float> b) => Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);   // a > b ? a : b
    static Vector128<float> AbsV(Vector128<float> a) => (a.AsInt32() & Vector128.Create(0x7fffffff)).AsSingle();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]   // always inlined, so the shuffle control stays an immediate
    static Vector128<float> Bc(Vector128<float> v, [System.Diagnostics.CodeAnalysis.ConstantExpected] byte lane) => Sse.Shuffle(v, v, lane);   // broadcast one lane (0x00, 0x55, 0xAA, 0xFF)
    /// <summary>Per-tap bilateral weight on all four lanes: max(1 − max(d − σ, 0)·r, 1e-6) with d broadcast.</summary>
    static Vector128<float> TapWeight(Vector128<float> d, Vector128<float> s, Vector128<float> r)
    {
        var z = Vector128<float>.Zero; var t = MaxV(d - s, z);
        return MaxV(Vector128.Create(1.0f) - t * r, Vector128.Create(Eps6));
    }
    static ReadOnlySpan<Vector128<float>> AsV(Vec4F[] a) => System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, Vector128<float>>(a);
    static Span<Vector128<float>> AsVW(Vec4F[] a) => System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, Vector128<float>>(a.AsSpan());
    static readonly float Eps6 = BitConverter.Int32BitsToSingle(0x358637BD), Eps5 = BitConverter.Int32BitsToSingle(0x3727C5AC);
    static readonly float M0 = BitConverter.Int32BitsToSingle(0x3F13CD36), M3 = BitConverter.Int32BitsToSingle(0x3F350529), M5 = BitConverter.Int32BitsToSingle(unchecked((int)0xBF350529)),
                          M6 = BitConverter.Int32BitsToSingle(0x3ED10625), M7 = BitConverter.Int32BitsToSingle(unchecked((int)0xBF510625));
    static readonly float[] M = { M0, M0, M0, M3, 0f, M5, M6, M7, M6 };   // rows of the RGB→YCC rotation
    static readonly float U64 = BitConverter.Int32BitsToSingle(0x3F23D70B), U08 = BitConverter.Int32BitsToSingle(0x3DA3D70B), U01 = BitConverter.Int32BitsToSingle(0x3C23D70B),
                          U05 = BitConverter.Int32BitsToSingle(0x3D4CCCCD), U40 = BitConverter.Int32BitsToSingle(0x3ECCCCCD), U25 = 0.25f, C001 = BitConverter.Int32BitsToSingle(0x3C23D70A);

    /// <summary>`Sensor::forwardSTD`: σ_c = sqrt(max(((src_c·k·n_c) + inv·black)·A_c + B_c, 1e-5)) with k = (white − black)/white; lane 3 with A = B = 1, n = 1.</summary>
    public static Vec4F[] ForwardStd(Vec4F[] src, int w, int h, SensorNoise.Model model, float black, float white, float[] neutral)
    {
        float inv = 1.0f / white, k = (white - black) * inv, o = inv * black;
        float kr = k * neutral[0], kg = k * neutral[1], kb = k * neutral[2], ka = k * 1.0f;
        var K = Vector128.Create(kr, kg, kb, ka); var Ov = Vector128.Create(o); var Av = Vector128.Create(model.R.A, model.G.A, model.Bl.A, 1.0f); var Bv = Vector128.Create(model.R.B, model.G.B, model.Bl.B, 1.0f);
        var e5 = Vector128.Create(Eps5);
        var std = new Vec4F[w * h]; var S = AsV(src); var O = AsVW(std);
        for (int i = 0; i < src.Length; i++) O[i] = Vector128.Sqrt(MaxV(((S[i] * K) + Ov) * Av + Bv, e5));
        return std;
    }

    /// <summary>`ImageApplyColorMatrix`: out = ((v.w·c3) + (v.z·c2)) + ((v.y·c1) + (v.x·c0)), columns of the 4×4 (rows m, last row (0,0,0,1)).</summary>
    static Vec4F[] ApplyMatrix(Vec4F[] src, float[] m)
    {
        var o = new Vec4F[src.Length]; var S = AsV(src); var O = AsVW(o);
        var c0 = Vector128.Create(m[0], m[3], m[6], 0f); var c1 = Vector128.Create(m[1], m[4], m[7], 0f); var c2 = Vector128.Create(m[2], m[5], m[8], 0f); var z = Vector128<float>.Zero;
        for (int i = 0; i < src.Length; i++)
        {
            var v = S[i];   // ((A·0) + (B·c2)) + ((G·c1) + (R·c0)) on lanes 0–2, lane 3 = the source alpha
            O[i] = (((Bc(v, 0xFF) * z) + (Bc(v, 0xAA) * c2)) + ((Bc(v, 0x55) * c1) + (Bc(v, 0x00) * c0))).WithElement(3, src[i].A);
        }
        return o;
    }
    static float[] Transpose(float[] m) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

    /// <summary>`ImageTransformSTD`: out = max(sqrt((q.z·c2) + ((q.y·c1) + (q.x·c0))), floor) with q = s², c_k = squared matrix columns; lane 3 = 0.</summary>
    static Vec4F[] TransformStd(Vec4F[] vst, float[] m, float floorY)
    {
        float c00 = m[0] * m[0], c01 = m[3] * m[3], c02 = m[6] * m[6], c10 = m[1] * m[1], c11 = m[4] * m[4], c12 = m[7] * m[7], c20 = m[2] * m[2], c21 = m[5] * m[5], c22 = m[8] * m[8];
        var o = new Vec4F[vst.Length]; var S = AsV(vst); var O = AsVW(o);
        var k0 = Vector128.Create(c00, c01, c02, 0f); var k1 = Vector128.Create(c10, c11, c12, 0f); var k2 = Vector128.Create(c20, c21, c22, 0f); var fl = Vector128.Create(floorY, 0f, 0f, 0f);
        for (int i = 0; i < vst.Length; i++)
        {
            var q = S[i] * S[i];   // (qx, qy, qz, ·); lane c: max(sqrt((qz·c2c) + ((qy·c1c) + (qx·c0c))), floor_c), lane 3 = 0
            O[i] = MaxV(Vector128.Sqrt((Bc(q, 0xAA) * k2) + ((Bc(q, 0x55) * k1) + (Bc(q, 0x00) * k0))), fl).WithElement(3, 0f);
        }
        return o;
    }

    static int CeilLog2(int v) { int l = 0; while ((1 << l) < v) l++; return l; }

    /// <summary>The hybrid core: RGB→YCC rotation, pyramid bilateral, full-resolution NLM, rotation back.</summary>
    public static Vec4F[] Run(Vec4F[] src, Vec4F[] vst, int w, int h, float thr, NlmParams nlm, BilateralParams bil)
    {
        if (nlm.PyramidSize != bil.PyramidSize) throw new InvalidOperationException("pyramid size mismatch!");
        bool transform = Math.Abs((double)nlm.ChromaBoost - 1.0) > 1e-4 || Math.Abs((double)bil.ChromaBoost - 1.0) > 1e-4;
        Dump("fstd_out", vst, w, h);
        var srcT = transform ? ApplyMatrix(src, M) : src; Dump("mat0_out", srcT, w, h);
        var vstT = transform ? TransformStd(vst, M, nlm.MinLumaStd) : vst; Dump("tstd_out", vstT, w, h);
        int maxLevels = Math.Max(CeilLog2(w), CeilLog2(h)) + 1;
        int N = Math.Min(nlm.PyramidSize, maxLevels);
        var pyr = new List<(Vec4F[] Img, int W, int H)>(); var vpyr = new List<(Vec4F[] Img, int W, int H)>();
        {
            Vec4F[] cur = srcT, vcur = vstT; int cw = w, ch = h;
            using var php = Diagnostics.PhaseLog.Time("hyb.pyramid");
            for (int l = 0; l < N; l++)
            {
                var d = CnrPyramid.Downsample(cur, cw, ch, out int w2, out int h2); var vd = CnrPyramid.Downsample(vcur, cw, ch, out _, out _);
                pyr.Add((d, w2, h2)); vpyr.Add((vd, w2, h2)); cur = d; vcur = vd; cw = w2; ch = h2;
                if (w2 == 1 && h2 == 1) break;
            }
            N = pyr.Count;
        }
        var hb = new Vec4F(1.0f * thr, bil.ChromaBoost * thr, bil.ChromaBoost * thr, 1.0f * thr);
        var hn = new Vec4F(1.0f * thr, nlm.ChromaBoost * thr, nlm.ChromaBoost * thr, 1.0f * thr);
        var curL = (Vec4F[])pyr[N - 1].Img.Clone();
        for (int L = N - 1; L >= 1; L--)
        {
            float e2 = MathF.ScaleB(1.0f, -(L + 1));
            var tune = new Vec4F(e2 * hb.R, e2 * hb.G, e2 * hb.B, e2 * hb.A);
            int bi = N - 1 - L; Dump($"bil{bi}_src", curL, pyr[L].W, pyr[L].H); Dump($"bil{bi}_vst", vpyr[L].Img, pyr[L].W, pyr[L].H);
            Vec4F[] den; using (Diagnostics.PhaseLog.Time("hyb.bilateral")) den = Bilateral(bil.WindowSize, curL, vpyr[L].Img, pyr[L].W, pyr[L].H, tune); Dump($"bil{bi}_out", den, pyr[L].W, pyr[L].H);
            using (Diagnostics.PhaseLog.Time("hyb.ups")) curL = UpscaleAdd(den, pyr[L].Img, pyr[L].W, pyr[L].H, pyr[L - 1].Img, pyr[L - 1].W, pyr[L - 1].H); Dump($"ups{bi}_out", curL, pyr[L - 1].W, pyr[L - 1].H);
        }
        float r = Rcp(2.0f); r = (1.0f - (r + r)) * r + r;
        Dump($"bil{N - 1}_src", curL, pyr[0].W, pyr[0].H); Dump($"bil{N - 1}_vst", vpyr[0].Img, pyr[0].W, pyr[0].H);
        Vec4F[] den0; using (Diagnostics.PhaseLog.Time("hyb.bilateral0")) den0 = Bilateral(bil.WindowSize, curL, vpyr[0].Img, pyr[0].W, pyr[0].H, new Vec4F(hb.R * r, hb.G * r, hb.B * r, hb.A * r)); Dump($"bil{N - 1}_out", den0, pyr[0].W, pyr[0].H);
        Vec4F[] full; using (Diagnostics.PhaseLog.Time("hyb.ups0")) full = UpscaleAdd(den0, pyr[0].Img, pyr[0].W, pyr[0].H, srcT, w, h); Dump($"ups{N - 1}_out", full, w, h); Dump("nlm_src", full, w, h); Dump("nlm_vst", vstT, w, h);
        if (nlm.StepSize <= 0) throw new NotSupportedException("nlm_denoiser.step_size == 0 (ImageDenoiseNLM) is not ported");
        Vec4F[] outp; using (Diagnostics.PhaseLog.Time("hyb.nlm")) outp = PatchNlm4(full, vstT, w, h, hn, nlm.WindowSize, nlm.StepSize); Dump("nlm_out", outp, w, h);
        var fin = transform ? ApplyMatrix(outp, Transpose(M)) : outp; Dump("mat1_out", fin, w, h);
        return fin;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;N,0&gt;` by `bilateral_denoiser.window_size`, i.e. Lumen's dispatcher
    /// (`switch (window − 3)`: case 0 → &lt;3,·&gt;, 2 → &lt;5,·&gt;, 4 → &lt;7,·&gt;, 6 → &lt;9,·&gt;, default → "Unsupported bilateral kernel size!").
    /// The hybrid core always passes the literal `0` for the second template argument, so only the
    /// `&lt;N,0&gt;` half of the family is reachable from this stage. The `&lt;N,1&gt;` half belongs to Lumen's `bilateral`/`bilateral_420`
    /// denoising stage (flag 0 on the pyramid levels and 1 on the full-res call),
    /// a stage Lux does not implement. Sizes `IsoTuning` can select: 5 (ISO 100/200 rows), 7 (ISO 400 and, key 2, ISO 625)
    /// and 9 (key 2 ISO 775; key 4 ISO 800/1600). 3 appears in the tuning rows only as `Bil420Pyramid` (the bilateral_420
    /// pyramid size), never as a window — `&lt;3,0&gt;` is ported for completeness of the dispatcher.</summary>
    public static Vec4F[] Bilateral(int window, Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        if (BilDebug) Console.Error.WriteLine($"[bilateral] window {window} {w}x{h}");
        return BilateralN(window, src, vst, w, h, tune);
    }
    static readonly bool BilDebug = Environment.GetEnvironmentVariable("LUX_BIL_DEBUG") == "1";
    static Vec4F[] BilateralN(int window, Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune) => window switch
    {
        3 => Bilateral3(src, vst, w, h, tune),
        5 => Bilateral5(src, vst, w, h, tune),
        7 => Bilateral7(src, vst, w, h, tune),
        9 => Bilateral9(src, vst, w, h, tune),
        _ => throw new InvalidOperationException("Unsupported bilateral kernel size!"),   // the dispatcher's default arm
    };

    /// <summary>`ImageDenoiseBilateralGeneric&lt;9,0&gt;` tile lambda. Same per-tap weight as the 3/5/7 kernels; tile grown by 4 → neighbours
    /// outside the image are (0,0,0,0) in `src` (the slow path memsets a (tw+8)×(th+8) buffer and copies the clipped source into it).
    /// **Lumen's compiler did NOT unroll this variant**: it is a real row loop (dy = −4…4) around a real tap loop (dx = −4…+4),
    /// and both accumulators are live across all 81 taps. So there is no per-row association tree at all — acc and wsum are a
    /// straight left-to-right sequential sum in raster tap order, unlike `&lt;3,0&gt;`/`&lt;5,0&gt;`/`&lt;7,0&gt;` which each fold one
    /// unrolled row into the carry with a tree.</summary>
    static Vec4F[] Bilateral9(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h]; var S = AsV(src); var Vs = AsV(vst); var O = AsVW(o); var T = V(tune); var z = Vector128<float>.Zero;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = S[y * w + x]; var sg = T * Vs[y * w + x];
                var r = IntelApprox.Reciprocal(sg.WithElement(3, 1.0f));          // lane 3 = 1.0, then the RCPPS estimate
                var a = z; var ws = z;
                for (int dy = -4; dy <= 4; dy++)
                {
                    int yy = y + dy; bool rowIn = yy >= 0 && yy < h;
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int xx = x + dx; var n = rowIn && xx >= 0 && xx < w ? S[yy * w + xx] : z;
                        var ad = AbsV(n - c);
                        var d = MaxV(MaxV(Bc(ad, 0xAA), Bc(ad, 0x00)), MaxV(Bc(ad, 0xAA), Bc(ad, 0x55)));   // max(max(dB, dR), max(dB, dG))
                        var k = TapWeight(d, sg, r);
                        a = a + n * k; ws = ws + k;
                    }
                }
                O[y * w + x] = IntelApprox.Reciprocal(ws) * a;
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;7,0&gt;` tile lambda: same per-tap
    /// weight as the 5-tap kernel (d = max(|n−c|.xyz), w = max(1 − max(d − σ, 0)·rcpps(σ|lane3=1), 1e-6), σ = tune·vst all 4 lanes); per row
    /// `acc = P₊₃ + ((P₊₂ + (P₊₁ + P₀)) + ((P₋₁ + P₋₂) + (P₋₃ + acc)))`, `wsum` likewise; tile grown by 3 → zero neighbours outside the image;
    /// out = rcpps(wsum)·acc.</summary>
    static Vec4F[] Bilateral7(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h]; var S = AsV(src); var Vs = AsV(vst); var O = AsVW(o); var T = V(tune); var z = Vector128<float>.Zero;
        Span<Vector128<float>> n = stackalloc Vector128<float>[7]; Span<Vector128<float>> kk = stackalloc Vector128<float>[7];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = S[y * w + x]; var sg = T * Vs[y * w + x];
                var r = IntelApprox.Reciprocal(sg.WithElement(3, 1.0f));
                var a = z; var ws = z;
                for (int dy = -3; dy <= 3; dy++)
                {
                    int yy = y + dy; bool rowIn = yy >= 0 && yy < h;
                    for (int k = 0; k < 7; k++)
                    {
                        int xx = x + k - 3; var nb = rowIn && xx >= 0 && xx < w ? S[yy * w + xx] : z;
                        var ad = AbsV(nb - c);
                        var d = MaxV(MaxV(Bc(ad, 0x00), Bc(ad, 0xAA)), MaxV(Bc(ad, 0x55), Bc(ad, 0xAA)));   // max(max(x,z), max(y,z))
                        kk[k] = TapWeight(d, sg, r); n[k] = nb;
                    }
                    a = n[6] * kk[6] + ((n[5] * kk[5] + (n[4] * kk[4] + n[3] * kk[3])) + ((n[2] * kk[2] + n[1] * kk[1]) + (n[0] * kk[0] + a)));
                    ws = kk[6] + ((kk[5] + (kk[4] + kk[3])) + ((kk[2] + kk[1]) + (kk[0] + ws)));
                }
                O[y * w + x] = IntelApprox.Reciprocal(ws) * a;
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;5,0&gt;`: neighbours outside the image are (0,0,0,0); per tap w = max(1 − max(d − σ, 0)·rcp(σ), 1e-6) with d = max over R,G,B of |n − c|.</summary>
    static Vec4F[] Bilateral5(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h]; var S = AsV(src); var Vs = AsV(vst); var O = AsVW(o); var T = V(tune); var z = Vector128<float>.Zero;
        Span<Vector128<float>> n = stackalloc Vector128<float>[5]; Span<Vector128<float>> kk = stackalloc Vector128<float>[5];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = S[y * w + x]; var sg = T * Vs[y * w + x];
                var r = IntelApprox.Reciprocal(sg.WithElement(3, 1.0f));
                var a = z; var ws = z;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int yy = y + dy; bool rowIn = yy >= 0 && yy < h;
                    for (int k = 0; k < 5; k++)
                    {
                        int xx = x + k - 2; var nb = rowIn && xx >= 0 && xx < w ? S[yy * w + xx] : z;
                        var ad = AbsV(nb - c);
                        var d = MaxV(MaxV(Bc(ad, 0x00), Bc(ad, 0x55)), Bc(ad, 0xAA));   // max(max(dR, dG), dB)
                        kk[k] = TapWeight(d, sg, r); n[k] = nb;
                    }
                    a = (n[4] * kk[4] + n[3] * kk[3]) + ((n[2] * kk[2] + n[1] * kk[1]) + (n[0] * kk[0] + a));
                    ws = (kk[4] + kk[3]) + ((kk[2] + kk[1]) + (kk[0] + ws));
                }
                O[y * w + x] = IntelApprox.Reciprocal(ws) * a;
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;3,0&gt;` tile lambda: same per-tap weight as the other sizes; the 3 taps of a
    /// row are fully unrolled inside a 3-iteration row
    /// loop (dy = −1…+1), tile grown by 1 → zero neighbours outside the image; out = rcpps(wsum)·acc.
    /// Row tree: `acc = (P₊₁ + P₀) + (P₋₁ + acc)`, `wsum` likewise —
    /// the same shape as the 5-tap tree with the outer pair dropped, so nothing is unusual here (unlike `&lt;9,0&gt;`).
    /// Unreachable from `IsoTuning` (no row carries `bilateral window` 3) but present in the dispatcher, so it is ported too.</summary>
    static Vec4F[] Bilateral3(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h]; var S = AsV(src); var Vs = AsV(vst); var O = AsVW(o); var T = V(tune); var z = Vector128<float>.Zero;
        Span<Vector128<float>> n = stackalloc Vector128<float>[3]; Span<Vector128<float>> kk = stackalloc Vector128<float>[3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = S[y * w + x]; var sg = T * Vs[y * w + x];
                var r = IntelApprox.Reciprocal(sg.WithElement(3, 1.0f));
                var a = z; var ws = z;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; bool rowIn = yy >= 0 && yy < h;
                    for (int k = 0; k < 3; k++)
                    {
                        int xx = x + k - 1; var nb = rowIn && xx >= 0 && xx < w ? S[yy * w + xx] : z;
                        var ad = AbsV(nb - c);
                        var d = MaxV(MaxV(Bc(ad, 0xAA), Bc(ad, 0x00)), MaxV(Bc(ad, 0xAA), Bc(ad, 0x55)));   // max(max(dB, dR), max(dB, dG))
                        kk[k] = TapWeight(d, sg, r); n[k] = nb;
                    }
                    a = (n[2] * kk[2] + n[1] * kk[1]) + (n[0] * kk[0] + a);
                    ws = (kk[2] + kk[1]) + (kk[0] + ws);
                }
                O[y * w + x] = IntelApprox.Reciprocal(ws) * a;
            }
        return o;
    }

    /// <summary>`ImageGaussianSubtractUpscaleAndAdd`: out = c + Up(a − b). Rows in pairs (2m, 2m+1): even rows use the 0.64/0.08/0.01 (x even) and
    /// 0.05/0.4 (x odd) stencils on small rows m−1..m+1; odd rows interpolate between small rows m and m+1 with 0.05/0.4 (x even) and 0.25 (x odd);
    /// the last column of an odd width uses the x-even formula; small-image taps clamped (row/column cache replication).</summary>
    static Vec4F[] UpscaleAdd(Vec4F[] a, Vec4F[] b, int sw, int sh, Vec4F[] c, int w, int h)
    {
        var s = new Vec4F[sw * sh]; var A = AsV(a); var B = AsV(b); var Sv = AsVW(s); for (int i = 0; i < s.Length; i++) Sv[i] = A[i] - B[i];
        var o = new Vec4F[w * h]; var O = AsVW(o); var C = AsV(c); var SS = AsV(s);
        var u64 = Vector128.Create(U64); var u08 = Vector128.Create(U08); var u01 = Vector128.Create(U01); var u05 = Vector128.Create(U05); var u40 = Vector128.Create(U40); var u25 = Vector128.Create(U25);
        for (int y = 0; y < h; y++)
        {
            int m = y >> 1; bool odd = (y & 1) != 0;
            int rm1 = Math.Clamp(m - 1, 0, sh - 1) * sw, rm = Math.Clamp(m, 0, sh - 1) * sw, rp1 = Math.Clamp(m + 1, 0, sh - 1) * sw;
            for (int x = 0; x < w; x += 2)
            {
                int j = x >> 1, jm = Math.Clamp(j - 1, 0, sw - 1), j0 = Math.Clamp(j, 0, sw - 1), jp = Math.Clamp(j + 1, 0, sw - 1);
                var sm1jp = SS[rm1 + jp]; var sm1jm = SS[rm1 + jm]; var sp1jm = SS[rp1 + jm]; var sp1jp = SS[rp1 + jp]; var sp1j = SS[rp1 + j0]; var sm1j = SS[rm1 + j0];
                var smjm = SS[rm + jm]; var smjp = SS[rm + jp]; var smj = SS[rm + j0];
                var cc = C[y * w + x];
                if (!odd)
                {
                    O[y * w + x] = ((smj * u64) + (((sp1j + sm1j) + smjm + smjp) * u08 + ((sm1jp + sm1jm) + sp1jm + sp1jp) * u01)) + cc;
                    if (x + 1 < w) O[y * w + x + 1] = (((sm1jp + sm1j) + sp1j + sp1jp) * u05 + C[y * w + x + 1]) + (smjp + smj) * u40;
                }
                else
                {   // odd row: small rows m, m+1
                    O[y * w + x] = (((smjp + smjm) + sp1jm + sp1jp) * u05 + cc) + (sp1j + smj) * u40;
                    if (x + 1 < w) O[y * w + x + 1] = ((smjp + smj) + sp1j + sp1jp) * u25 + C[y * w + x + 1];
                }
            }
        }
        return o;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _jitter = new();
    /// <summary>Discard the cached jitter tables so the next <see cref="PatchNlm4"/> call rebuilds one for its own
    /// <c>step</c>. The table depends only on <c>step</c> (one is kept per step, so concurrent tiles never observe a
    /// half-built table); resetting exists for callers that drive the kernel on its own.</summary>
    public static void ResetJitterTable() => _jitter.Clear();
    static byte[] Jitter(int step)
    {
        var t = new byte[25106]; ulong x = 0x330E;
        for (int n = 0; n < t.Length; n++) { x = (0x5DEECE66DUL * x + 0xBUL) & 0xFFFFFFFFFFFFUL; t[n] = (byte)((uint)(x >> 16) % (uint)step); }
        return t;
    }

    /// <summary>`ImageDenoisePatchNLM&lt;4&gt;`: jittered 4×4 patch NLM over a quincunx window, four quadrant passes per 128×128 tile, weights per lane.</summary>
    public static Vec4F[] PatchNlm4(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F hn, int W, int step)
    {
        var tab = _jitter.GetOrAdd(step, Jitter);
        var outp = new Vec4F[w * h]; var wsum = new Vec4F[w * h];
        var S = AsV(src); var Vs = AsV(vst); var O = AsVW(outp); var WS = AsVW(wsum);
        var c001 = Vector128.Create(C001);
        for (int i = 0; i < src.Length; i++) { O[i] = S[i] * c001; WS[i] = c001; }
        var k16 = Vector128.Create(hn.R * 16.0f, hn.G * 16.0f, hn.B * 16.0f, hn.A * 16.0f);   // h16 = hn·16, T = vst(q)·h16 per lane
        var h16 = new Vec4F[w * h]; var H16 = AsVW(h16); for (int i = 0; i < vst.Length; i++) H16[i] = Vs[i] * k16;
        var region = new RectI(2, 2, w - 1, h - 1);
        var tiles = Tiler.Rects(region, 128, 128).ToList();
        Span<Vector128<float>> acc = stackalloc Vector128<float>[16]; Span<Vector128<float>> refp = stackalloc Vector128<float>[16];
        var z = Vector128<float>.Zero; var one = Vector128.Create(1.0f);
        for (int pass = 0; pass < 4; pass++)
            foreach (var tile in tiles)
            {
                int half = (tile.X1 - tile.X0) >> 1, halfy = (tile.Y1 - tile.Y0) >> 1;
                int xs = tile.X0 + ((pass & 1) != 0 ? half : 0); int xe = Math.Min(xs + half, tile.X1); xs = Math.Min(xs, tile.X1);
                int ys = tile.Y0 + ((pass & 2) != 0 ? halfy : 0); int ye = Math.Min(ys + halfy, tile.Y1); ys = Math.Min(ys, tile.Y1);
                if (ys >= ye) continue;
                uint idx = unchecked((uint)((w * ys + xs) * unchecked((int)0xDEADBEEF))) % 12553u;
                for (int y0 = ys; y0 < ye; y0 += step)
                    for (int x0 = xs; x0 < xe; x0 += step)
                    {
                        int bx = tab[2 * idx], by = tab[2 * idx + 1]; idx = idx + 1 == 12553u ? 0u : idx + 1;
                        int qx = Math.Min(x0 + bx, xe - 1), qy = Math.Min(y0 + by, ye - 1);
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) refp[r * 4 + c] = S[(qy - 2 + r) * w + (qx - 2 + c)];
                        var T = H16[qy * w + qx]; var rT = IntelApprox.Reciprocal(T);
                        int sx0 = Math.Max(qx - (W >> 1), 2); int sx1 = Math.Min(sx0 + W, w - 1); int sy0 = Math.Max(qy - (W >> 1), 2); int sy1 = Math.Min(sy0 + W, h - 1);
                        acc.Clear(); var wa = z;
                        ref Vector128<float> sb = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(S);
                        ref Vector128<float> ab = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(acc);
                        ref Vector128<float> rb = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(refp);
                        for (int cy = sy1 - W; cy < sy1; cy++)
                            for (int cx = (sx1 - W) + (cy & 1); cx < sx1; cx += 2)
                            {
                                // |cand − ref| per pixel of the 4×4 patch; P = ((A3 + A2) + (A1 + A0)), then + A7, A9, A11, A13, A15; Q = A5 + A4, then + A6 … A14
                                ref Vector128<float> p0 = ref Unsafe.Add(ref sb, (cy - 2) * w + (cx - 2)); ref Vector128<float> p1 = ref Unsafe.Add(ref p0, w);
                                ref Vector128<float> p2 = ref Unsafe.Add(ref p1, w); ref Vector128<float> p3 = ref Unsafe.Add(ref p2, w);
                                var a0 = AbsV(p0 - rb); var a1 = AbsV(Unsafe.Add(ref p0, 1) - Unsafe.Add(ref rb, 1)); var a2 = AbsV(Unsafe.Add(ref p0, 2) - Unsafe.Add(ref rb, 2)); var a3 = AbsV(Unsafe.Add(ref p0, 3) - Unsafe.Add(ref rb, 3));
                                var a4 = AbsV(p1 - Unsafe.Add(ref rb, 4)); var a5 = AbsV(Unsafe.Add(ref p1, 1) - Unsafe.Add(ref rb, 5)); var a6 = AbsV(Unsafe.Add(ref p1, 2) - Unsafe.Add(ref rb, 6)); var a7 = AbsV(Unsafe.Add(ref p1, 3) - Unsafe.Add(ref rb, 7));
                                var a8 = AbsV(p2 - Unsafe.Add(ref rb, 8)); var a9 = AbsV(Unsafe.Add(ref p2, 1) - Unsafe.Add(ref rb, 9)); var a10 = AbsV(Unsafe.Add(ref p2, 2) - Unsafe.Add(ref rb, 10)); var a11 = AbsV(Unsafe.Add(ref p2, 3) - Unsafe.Add(ref rb, 11));
                                var a12 = AbsV(p3 - Unsafe.Add(ref rb, 12)); var a13 = AbsV(Unsafe.Add(ref p3, 1) - Unsafe.Add(ref rb, 13)); var a14 = AbsV(Unsafe.Add(ref p3, 2) - Unsafe.Add(ref rb, 14)); var a15 = AbsV(Unsafe.Add(ref p3, 3) - Unsafe.Add(ref rb, 15));
                                var P = (a3 + a2) + (a1 + a0); P = a7 + P; P = a9 + P; P = a11 + P; P = a13 + P; P = a15 + P;
                                var Q = a5 + a4; Q = a6 + Q; Q = a8 + Q; Q = a10 + Q; Q = a12 + Q; Q = a14 + Q;
                                var D = P + Q;
                                var d = MaxV(MaxV(Bc(D, 0x00), Bc(D, 0x55)), Bc(D, 0xAA));   // max(max(D.R, D.G), D.B)
                                var wv = MaxV(one - MaxV(d - T, z) * rT, z);
                                for (int k = 0; k < 4; k++)
                                {
                                    Unsafe.Add(ref ab, k) = Unsafe.Add(ref ab, k) + Unsafe.Add(ref p0, k) * wv;
                                    Unsafe.Add(ref ab, 4 + k) = Unsafe.Add(ref ab, 4 + k) + Unsafe.Add(ref p1, k) * wv;
                                    Unsafe.Add(ref ab, 8 + k) = Unsafe.Add(ref ab, 8 + k) + Unsafe.Add(ref p2, k) * wv;
                                    Unsafe.Add(ref ab, 12 + k) = Unsafe.Add(ref ab, 12 + k) + Unsafe.Add(ref p3, k) * wv;
                                }
                                wa = wa + wv;
                            }
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                            {
                                int i = (qy - 2 + r) * w + (qx - 2 + c);
                                O[i] = O[i] + acc[r * 4 + c]; WS[i] = WS[i] + wa;
                            }
                    }
            }
        for (int i = 0; i < outp.Length; i++) O[i] = (IntelApprox.Reciprocal(WS[i]) * O[i]).WithElement(3, src[i].A);
        return outp;
    }
}
