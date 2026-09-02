using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `Denoising:hybrid` (slot 9; spec `a0349917a78884e46.md`): `Pipeline::lambda_34` glue (context ring of ≤ 63 px, σ image
/// `Sensor::forwardSTD`, threshold = threshold_multiplier·parameter_scale (×1.12 for the collapse demosaics)) around `lambda_48`: RGB→(Y,C1,C2)
/// rotation, a 5-level Gaussian pyramid, per level the trapezoid bilateral N×N (`BilateralGeneric&lt;N,0&gt;`, N = `bilateral_denoiser.window_size`
/// ∈ {3,5,7,9}) and the `SubtractUpscaleAndAdd`
/// reconstruction, a full-resolution jittered `ImageDenoisePatchNLM&lt;4&gt;`, rotation back. All arithmetic in the machine order; `rcpps` raw.
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
        if (demosaic is "collapse2" or "collapse4" or "collapse8") thr = (float)((double)thr * 1.12);   // DAT_1806d7640
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
        var std = HybridDenoise.ForwardStd(src, w, h, noise.ModelForGain(p.Frame.AnalogGain), black, white, p.Stats.Neutral);
        // lambda_34 180417110 (asm 180417374–1804175ae; spec a-std-plane.md): when the payload carries a STD plane (+0x40, data +0x60
        // non-null, +0x50/+0x54 > 0 — the level-1 FusionCacheBayer weight image) it is viewed exactly like the RGB source (∩ int rect, grown by
        // min(63, available) per side of ITS OWN rect), its size must equal the σ image ("std Image provided does not match image size!" /
        // FUN_18035ffc0 "image size mismatch!"), then FUN_18035fe00 multiplies the forwardSTD σ in place, per pixel by view-local index, the scalar
        // broadcast to all four lanes (movss+shufps 0, mulps): σ_c(x,y) = std(x,y)·σ_c(x,y). This happens BEFORE lambda_48 (rotation / ImageTransformSTD).
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
        var outp = HybridDenoise.Run(src, std, w, h, thr, nlm, bil);
        // lambda_34 swaps the denoised image into +0x70; the previous working image stays in the payload (+0xa0) and PostProcessing uses it as the LDiff companion
        var before = new Image<Vec4F>(img.Rect); for (int y = 0; y < img.Height; y++) img.Row(y).CopyTo(before.Row(y)); p.Companion = before;
        for (int y = 0; y < abs.Height; y++) outp.AsSpan((y + padT) * w + padL, abs.Width).CopyTo(img.Row(abs.Y0 - img.Rect.Y0 + y).Slice(abs.X0 - img.Rect.X0, abs.Width));
    }
}

public static class HybridDenoise
{
    /// <summary>Diagnostic: `LUX_HYB_DUMP=&lt;prefix&gt;` writes every intermediate as `{prefix}_hyb_{tag}.f32` (header w,h,stride,16) like cp.dll's hybrid-denoise intermediate hooks.</summary>
    static readonly string? DumpPrefix = Environment.GetEnvironmentVariable("LUX_HYB_DUMP");
    static void Dump(string tag, Vec4F[] img, int w, int h)
    {
        if (DumpPrefix is null) return;
        using var fo = File.Create($"{DumpPrefix}_hyb_{tag}.f32"); fo.Write(BitConverter.GetBytes(w)); fo.Write(BitConverter.GetBytes(h)); fo.Write(BitConverter.GetBytes(w)); fo.Write(BitConverter.GetBytes(16));
        var bytes = new byte[img.Length * 16]; System.Runtime.InteropServices.MemoryMarshal.AsBytes(img.AsSpan()).CopyTo(bytes); fo.Write(bytes);
    }
    internal static void DumpStd(Vec4F[] img, int w, int h) => Dump("stdmul_out", img, w, h);
    public sealed class NlmParams { public int WindowSize = 5, PatchSize = 5, StepSize = 2, PyramidSize = 5; public float ChromaBoost = 2f, MinLumaStd = 0f; }
    public sealed class BilateralParams { public int WindowSize = 5, PyramidSize = 5; public float ChromaBoost = 2f; }

    static float Rcp(float d) => Sse.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar();
    static float Max(float a, float b) => a > b ? a : b;          // maxps: NaN → second operand… (a > b false → b)
    static Vec4F Add(Vec4F a, Vec4F b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    static float Abs(float a) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(a) & 0x7fffffff);
    static readonly float Eps6 = BitConverter.Int32BitsToSingle(0x358637BD), Eps5 = BitConverter.Int32BitsToSingle(0x3727C5AC);
    static readonly float M0 = BitConverter.Int32BitsToSingle(0x3F13CD36), M3 = BitConverter.Int32BitsToSingle(0x3F350529), M5 = BitConverter.Int32BitsToSingle(unchecked((int)0xBF350529)),
                          M6 = BitConverter.Int32BitsToSingle(0x3ED10625), M7 = BitConverter.Int32BitsToSingle(unchecked((int)0xBF510625));
    static readonly float[] M = { M0, M0, M0, M3, 0f, M5, M6, M7, M6 };   // rows of the RGB→YCC rotation (DAT_180835d68)
    static readonly float U64 = BitConverter.Int32BitsToSingle(0x3F23D70B), U08 = BitConverter.Int32BitsToSingle(0x3DA3D70B), U01 = BitConverter.Int32BitsToSingle(0x3C23D70B),
                          U05 = BitConverter.Int32BitsToSingle(0x3D4CCCCD), U40 = BitConverter.Int32BitsToSingle(0x3ECCCCCD), U25 = 0.25f, C001 = BitConverter.Int32BitsToSingle(0x3C23D70A);

    /// <summary>`Sensor::forwardSTD`: σ_c = sqrt(max(((src_c·k·n_c) + inv·black)·A_c + B_c, 1e-5)) with k = (white − black)/white; lane 3 with A = B = 1, n = 1.</summary>
    public static Vec4F[] ForwardStd(Vec4F[] src, int w, int h, SensorNoise.Model model, float black, float white, float[] neutral)
    {
        float inv = 1.0f / white, k = (white - black) * inv, o = inv * black;
        float kr = k * neutral[0], kg = k * neutral[1], kb = k * neutral[2], ka = k * 1.0f;
        float ar = model.R.A, ag = model.G.A, ab = model.Bl.A, br = model.R.B, bg = model.G.B, bb = model.Bl.B;
        var std = new Vec4F[w * h];
        for (int i = 0; i < src.Length; i++)
        {
            var s = src[i];
            std[i] = new Vec4F(MathF.Sqrt(Max(((s.R * kr) + o) * ar + br, Eps5)), MathF.Sqrt(Max(((s.G * kg) + o) * ag + bg, Eps5)), MathF.Sqrt(Max(((s.B * kb) + o) * ab + bb, Eps5)), MathF.Sqrt(Max(((s.A * ka) + o) * 1.0f + 1.0f, Eps5)));
        }
        return std;
    }

    /// <summary>`ImageApplyColorMatrix`: out = ((v.w·c3) + (v.z·c2)) + ((v.y·c1) + (v.x·c0)), columns of the 4×4 (rows m, last row (0,0,0,1)).</summary>
    static Vec4F[] ApplyMatrix(Vec4F[] src, float[] m)
    {
        var o = new Vec4F[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var v = src[i];
            o[i] = new Vec4F(((v.A * 0f) + (v.B * m[2])) + ((v.G * m[1]) + (v.R * m[0])), ((v.A * 0f) + (v.B * m[5])) + ((v.G * m[4]) + (v.R * m[3])), ((v.A * 0f) + (v.B * m[8])) + ((v.G * m[7]) + (v.R * m[6])), v.A);
        }
        return o;
    }
    static float[] Transpose(float[] m) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

    /// <summary>`ImageTransformSTD`: out = max(sqrt((q.z·c2) + ((q.y·c1) + (q.x·c0))), floor) with q = s², c_k = squared matrix columns; lane 3 = 0.</summary>
    static Vec4F[] TransformStd(Vec4F[] vst, float[] m, float floorY)
    {
        float c00 = m[0] * m[0], c01 = m[3] * m[3], c02 = m[6] * m[6], c10 = m[1] * m[1], c11 = m[4] * m[4], c12 = m[7] * m[7], c20 = m[2] * m[2], c21 = m[5] * m[5], c22 = m[8] * m[8];
        var o = new Vec4F[vst.Length];
        for (int i = 0; i < vst.Length; i++)
        {
            var s = vst[i]; float qx = s.R * s.R, qy = s.G * s.G, qz = s.B * s.B;
            o[i] = new Vec4F(Max(MathF.Sqrt((qz * c20) + ((qy * c10) + (qx * c00))), floorY), Max(MathF.Sqrt((qz * c21) + ((qy * c11) + (qx * c01))), 0f), Max(MathF.Sqrt((qz * c22) + ((qy * c12) + (qx * c02))), 0f), 0f);
        }
        return o;
    }

    static int CeilLog2(int v) { int l = 0; while ((1 << l) < v) l++; return l; }

    /// <summary>`lambda_48`.</summary>
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
            var den = Bilateral(bil.WindowSize, curL, vpyr[L].Img, pyr[L].W, pyr[L].H, tune); Dump($"bil{bi}_out", den, pyr[L].W, pyr[L].H);
            curL = UpscaleAdd(den, pyr[L].Img, pyr[L].W, pyr[L].H, pyr[L - 1].Img, pyr[L - 1].W, pyr[L - 1].H); Dump($"ups{bi}_out", curL, pyr[L - 1].W, pyr[L - 1].H);
        }
        float r = Rcp(2.0f); r = (1.0f - (r + r)) * r + r;
        Dump($"bil{N - 1}_src", curL, pyr[0].W, pyr[0].H); Dump($"bil{N - 1}_vst", vpyr[0].Img, pyr[0].W, pyr[0].H);
        var den0 = Bilateral(bil.WindowSize, curL, vpyr[0].Img, pyr[0].W, pyr[0].H, new Vec4F(hb.R * r, hb.G * r, hb.B * r, hb.A * r)); Dump($"bil{N - 1}_out", den0, pyr[0].W, pyr[0].H);
        var full = UpscaleAdd(den0, pyr[0].Img, pyr[0].W, pyr[0].H, srcT, w, h); Dump($"ups{N - 1}_out", full, w, h); Dump("nlm_src", full, w, h); Dump("nlm_vst", vstT, w, h);
        if (nlm.StepSize <= 0) throw new NotSupportedException("nlm_denoiser.step_size == 0 (ImageDenoiseNLM) is not ported");
        var outp = PatchNlm4(full, vstT, w, h, hn, nlm.WindowSize, nlm.StepSize); Dump("nlm_out", outp, w, h);
        var fin = transform ? ApplyMatrix(outp, Transpose(M)) : outp; Dump("mat1_out", fin, w, h);
        return fin;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;N,0&gt;` by `bilateral_denoiser.window_size`, i.e. the dispatcher `1803af4d0`
    /// (`switch (window − 3)`: case 0 → &lt;3,·&gt;, 2 → &lt;5,·&gt;, 4 → &lt;7,·&gt;, 6 → &lt;9,·&gt;, default → "Unsupported bilateral kernel size!").
    /// The hybrid core `lambda_48` (1803addf0) always passes the literal `0` for the second template argument, so only the
    /// `&lt;N,0&gt;` half of the family is reachable from this stage; the `&lt;N,1&gt;` half belongs to `setDenoising::lambda_38`
    /// (1803ac5b0 = `denoising.type` `bilateral`/`bilateral_420`, flag 0 on the pyramid levels and 1 on the full-res call),
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
        _ => throw new InvalidOperationException("Unsupported bilateral kernel size!"),   // 1803af4d0 default arm
    };

    /// <summary>`ImageDenoiseBilateralGeneric&lt;9,0&gt;::lambda_1` (1803b8d90, inner loops 1803b9730–1803b9792, transcribed from the
    /// disassembly). Same per-tap weight as the 3/5/7 kernels; tile grown by 4 → neighbours outside the image are (0,0,0,0)
    /// in `src` (the slow path memsets a (tw+8)×(th+8) buffer and copies the clipped source into it).
    /// **This is the one variant the compiler did NOT unroll**: `1803b9730` is a real row loop (ecx = −4…4) around a real tap
    /// loop (rdx = −0x40…0x40 step 0x10 = dx −4…+4), and both accumulators are live across all 81 taps
    /// (`xorps xmm7,xmm7` / `xorps xmm0,xmm0` sit *before* the row loop, `addps xmm7,xmm3` / `addps xmm0,xmm1` are the whole
    /// reduction). So there is no per-row association tree at all — acc and wsum are a straight left-to-right sequential sum
    /// in raster tap order, unlike `&lt;3,0&gt;`/`&lt;5,0&gt;`/`&lt;7,0&gt;` which each fold one unrolled row into the carry with a tree.</summary>
    static Vec4F[] Bilateral9(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = src[y * w + x]; var v = vst[y * w + x];
                float sR = tune.R * v.R, sG = tune.G * v.G, sB = tune.B * v.B, sA = tune.A * v.A;   // movaps tune; mulps vst
                float rR = Rcp(sR), rG = Rcp(sG), rB = Rcp(sB), rA = Rcp(1.0f);                     // insertps lane3 ← 1.0; rcpps
                float aR = 0f, aG = 0f, aB = 0f, aA = 0f, wR = 0f, wG = 0f, wB = 0f, wA = 0f;
                for (int dy = -4; dy <= 4; dy++)
                {
                    int yy = y + dy;
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int xx = x + dx; Vec4F n = (yy >= 0 && yy < h && xx >= 0 && xx < w) ? src[yy * w + xx] : default;
                        float dR = Abs(n.R - c.R), dG = Abs(n.G - c.G), dB = Abs(n.B - c.B);
                        float d = Max(Max(dB, dR), Max(dB, dG));   // pshufd 0x4a → (z,z,x,y); maxps dst,abs; movshdup; maxss
                        float kR = Max(1.0f - Max(d - sR, 0f) * rR, Eps6), kG = Max(1.0f - Max(d - sG, 0f) * rG, Eps6),
                              kB = Max(1.0f - Max(d - sB, 0f) * rB, Eps6), kA = Max(1.0f - Max(d - sA, 0f) * rA, Eps6);
                        aR = aR + n.R * kR; aG = aG + n.G * kG; aB = aB + n.B * kB; aA = aA + n.A * kA;   // addps xmm7, n·w
                        wR = wR + kR; wG = wG + kG; wB = wB + kB; wA = wA + kA;                          // addps xmm0, w
                    }
                }
                o[y * w + x] = new Vec4F(Rcp(wR) * aR, Rcp(wG) * aG, Rcp(wB) * aB, Rcp(wA) * aA);
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;7,0&gt;::lambda_1` (1803b77a0, inner loop 1803b8180–1803b83f8, transcribed from the disassembly): same per-tap
    /// weight as the 5-tap kernel (d = max(|n−c|.xyz), w = max(1 − max(d − σ, 0)·rcpps(σ|lane3=1), 1e-6), σ = tune·vst all 4 lanes); per row
    /// `acc = P₊₃ + ((P₊₂ + (P₊₁ + P₀)) + ((P₋₁ + P₋₂) + (P₋₃ + acc)))`, `wsum` likewise; tile grown by 3 → zero neighbours outside the image;
    /// out = rcpps(wsum)·acc.</summary>
    static Vec4F[] Bilateral7(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h];
        Span<float> nR = stackalloc float[7], nG = stackalloc float[7], nB = stackalloc float[7], nA = stackalloc float[7], kR = stackalloc float[7], kG = stackalloc float[7], kB = stackalloc float[7], kA = stackalloc float[7];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = src[y * w + x]; var v = vst[y * w + x];
                float sR = tune.R * v.R, sG = tune.G * v.G, sB = tune.B * v.B, sA = tune.A * v.A;
                float rR = Rcp(sR), rG = Rcp(sG), rB = Rcp(sB), rA = Rcp(1.0f);
                float aR = 0f, aG = 0f, aB = 0f, aA = 0f, wR = 0f, wG = 0f, wB = 0f, wA = 0f;
                for (int dy = -3; dy <= 3; dy++)
                {
                    int yy = y + dy;
                    for (int k = 0; k < 7; k++)
                    {
                        int xx = x + k - 3; Vec4F n = (yy >= 0 && yy < h && xx >= 0 && xx < w) ? src[yy * w + xx] : default;
                        float dR = Abs(n.R - c.R), dG = Abs(n.G - c.G), dB = Abs(n.B - c.B);
                        float d = Max(Max(dR, dB), Max(dG, dB));   // pshufd 0x4a/maxps/movshdup/maxss: max(max(x,z), max(y,z))
                        kR[k] = Max(1.0f - Max(d - sR, 0f) * rR, Eps6); kG[k] = Max(1.0f - Max(d - sG, 0f) * rG, Eps6); kB[k] = Max(1.0f - Max(d - sB, 0f) * rB, Eps6); kA[k] = Max(1.0f - Max(d - sA, 0f) * rA, Eps6);
                        nR[k] = n.R; nG[k] = n.G; nB[k] = n.B; nA[k] = n.A;
                    }
                    // taps k: 0 = −3 … 6 = +3
                    aR = nR[6] * kR[6] + ((nR[5] * kR[5] + (nR[4] * kR[4] + nR[3] * kR[3])) + ((nR[2] * kR[2] + nR[1] * kR[1]) + (nR[0] * kR[0] + aR)));
                    aG = nG[6] * kG[6] + ((nG[5] * kG[5] + (nG[4] * kG[4] + nG[3] * kG[3])) + ((nG[2] * kG[2] + nG[1] * kG[1]) + (nG[0] * kG[0] + aG)));
                    aB = nB[6] * kB[6] + ((nB[5] * kB[5] + (nB[4] * kB[4] + nB[3] * kB[3])) + ((nB[2] * kB[2] + nB[1] * kB[1]) + (nB[0] * kB[0] + aB)));
                    aA = nA[6] * kA[6] + ((nA[5] * kA[5] + (nA[4] * kA[4] + nA[3] * kA[3])) + ((nA[2] * kA[2] + nA[1] * kA[1]) + (nA[0] * kA[0] + aA)));
                    wR = kR[6] + ((kR[5] + (kR[4] + kR[3])) + ((kR[2] + kR[1]) + (kR[0] + wR)));
                    wG = kG[6] + ((kG[5] + (kG[4] + kG[3])) + ((kG[2] + kG[1]) + (kG[0] + wG)));
                    wB = kB[6] + ((kB[5] + (kB[4] + kB[3])) + ((kB[2] + kB[1]) + (kB[0] + wB)));
                    wA = kA[6] + ((kA[5] + (kA[4] + kA[3])) + ((kA[2] + kA[1]) + (kA[0] + wA)));
                }
                o[y * w + x] = new Vec4F(Rcp(wR) * aR, Rcp(wG) * aG, Rcp(wB) * aB, Rcp(wA) * aA);
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;5,0&gt;`: neighbours outside the image are (0,0,0,0); per tap w = max(1 − max(d − σ, 0)·rcp(σ), 1e-6) with d = max over R,G,B of |n − c|.</summary>
    static Vec4F[] Bilateral5(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h];
        Span<float> nR = stackalloc float[5], nG = stackalloc float[5], nB = stackalloc float[5], nA = stackalloc float[5], kR = stackalloc float[5], kG = stackalloc float[5], kB = stackalloc float[5], kA = stackalloc float[5];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = src[y * w + x]; var v = vst[y * w + x];
                float sR = tune.R * v.R, sG = tune.G * v.G, sB = tune.B * v.B, sA = tune.A * v.A;
                float rR = Rcp(sR), rG = Rcp(sG), rB = Rcp(sB), rA = Rcp(1.0f);
                float aR = 0f, aG = 0f, aB = 0f, aA = 0f, wR = 0f, wG = 0f, wB = 0f, wA = 0f;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int yy = y + dy;
                    for (int k = 0; k < 5; k++)
                    {
                        int xx = x + k - 2; Vec4F n = (yy >= 0 && yy < h && xx >= 0 && xx < w) ? src[yy * w + xx] : default;
                        float dR = Abs(n.R - c.R), dG = Abs(n.G - c.G), dB = Abs(n.B - c.B);
                        float d = Max(Max(dR, dG), dB);
                        kR[k] = Max(1.0f - Max(d - sR, 0f) * rR, Eps6); kG[k] = Max(1.0f - Max(d - sG, 0f) * rG, Eps6); kB[k] = Max(1.0f - Max(d - sB, 0f) * rB, Eps6); kA[k] = Max(1.0f - Max(d - sA, 0f) * rA, Eps6);
                        nR[k] = n.R; nG[k] = n.G; nB[k] = n.B; nA[k] = n.A;
                    }
                    aR = (nR[4] * kR[4] + nR[3] * kR[3]) + ((nR[2] * kR[2] + nR[1] * kR[1]) + (nR[0] * kR[0] + aR));
                    aG = (nG[4] * kG[4] + nG[3] * kG[3]) + ((nG[2] * kG[2] + nG[1] * kG[1]) + (nG[0] * kG[0] + aG));
                    aB = (nB[4] * kB[4] + nB[3] * kB[3]) + ((nB[2] * kB[2] + nB[1] * kB[1]) + (nB[0] * kB[0] + aB));
                    aA = (nA[4] * kA[4] + nA[3] * kA[3]) + ((nA[2] * kA[2] + nA[1] * kA[1]) + (nA[0] * kA[0] + aA));
                    wR = (kR[4] + kR[3]) + ((kR[2] + kR[1]) + (kR[0] + wR)); wG = (kG[4] + kG[3]) + ((kG[2] + kG[1]) + (kG[0] + wG));
                    wB = (kB[4] + kB[3]) + ((kB[2] + kB[1]) + (kB[0] + wB)); wA = (kA[4] + kA[3]) + ((kA[2] + kA[1]) + (kA[0] + wA));
                }
                o[y * w + x] = new Vec4F(Rcp(wR) * aR, Rcp(wG) * aG, Rcp(wB) * aB, Rcp(wA) * aA);
            }
        return o;
    }

    /// <summary>`ImageDenoiseBilateralGeneric&lt;3,0&gt;::lambda_1` (1803b5030, inner loop 1803b59c0–1803b5a8a, transcribed from the
    /// disassembly): same per-tap weight as the other sizes; the 3 taps of a row are fully unrolled inside a 3-iteration row
    /// loop (dy = −1…+1), tile grown by 1 → zero neighbours outside the image; out = rcpps(wsum)·acc.
    /// Row tree (`addps xmm7,xmm3` / `addps xmm3,xmm0` / `addps xmm3,xmm7`): `acc = (P₊₁ + P₀) + (P₋₁ + acc)`, `wsum` likewise —
    /// the same shape as the 5-tap tree with the outer pair dropped, so nothing is unusual here (unlike `&lt;9,0&gt;`).
    /// Unreachable from `IsoTuning` (no row carries `bilateral window` 3) but present in the dispatcher, so it is ported too.</summary>
    static Vec4F[] Bilateral3(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F tune)
    {
        var o = new Vec4F[w * h];
        Span<float> nR = stackalloc float[3], nG = stackalloc float[3], nB = stackalloc float[3], nA = stackalloc float[3], kR = stackalloc float[3], kG = stackalloc float[3], kB = stackalloc float[3], kA = stackalloc float[3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = src[y * w + x]; var v = vst[y * w + x];
                float sR = tune.R * v.R, sG = tune.G * v.G, sB = tune.B * v.B, sA = tune.A * v.A;
                float rR = Rcp(sR), rG = Rcp(sG), rB = Rcp(sB), rA = Rcp(1.0f);
                float aR = 0f, aG = 0f, aB = 0f, aA = 0f, wR = 0f, wG = 0f, wB = 0f, wA = 0f;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    for (int k = 0; k < 3; k++)
                    {
                        int xx = x + k - 1; Vec4F n = (yy >= 0 && yy < h && xx >= 0 && xx < w) ? src[yy * w + xx] : default;
                        float dR = Abs(n.R - c.R), dG = Abs(n.G - c.G), dB = Abs(n.B - c.B);
                        float d = Max(Max(dB, dR), Max(dB, dG));   // pshufd 0x4a → (z,z,x,y); maxps dst,abs; movshdup; maxss
                        kR[k] = Max(1.0f - Max(d - sR, 0f) * rR, Eps6); kG[k] = Max(1.0f - Max(d - sG, 0f) * rG, Eps6); kB[k] = Max(1.0f - Max(d - sB, 0f) * rB, Eps6); kA[k] = Max(1.0f - Max(d - sA, 0f) * rA, Eps6);
                        nR[k] = n.R; nG[k] = n.G; nB[k] = n.B; nA[k] = n.A;
                    }
                    // taps k: 0 = −1, 1 = 0, 2 = +1
                    aR = (nR[2] * kR[2] + nR[1] * kR[1]) + (nR[0] * kR[0] + aR);
                    aG = (nG[2] * kG[2] + nG[1] * kG[1]) + (nG[0] * kG[0] + aG);
                    aB = (nB[2] * kB[2] + nB[1] * kB[1]) + (nB[0] * kB[0] + aB);
                    aA = (nA[2] * kA[2] + nA[1] * kA[1]) + (nA[0] * kA[0] + aA);
                    wR = (kR[2] + kR[1]) + (kR[0] + wR); wG = (kG[2] + kG[1]) + (kG[0] + wG);
                    wB = (kB[2] + kB[1]) + (kB[0] + wB); wA = (kA[2] + kA[1]) + (kA[0] + wA);
                }
                o[y * w + x] = new Vec4F(Rcp(wR) * aR, Rcp(wG) * aG, Rcp(wB) * aB, Rcp(wA) * aA);
            }
        return o;
    }

    /// <summary>`ImageGaussianSubtractUpscaleAndAdd`: out = c + Up(a − b). Rows in pairs (2m, 2m+1): even rows use the 0.64/0.08/0.01 (x even) and
    /// 0.05/0.4 (x odd) stencils on small rows m−1..m+1; odd rows interpolate between small rows m and m+1 with 0.05/0.4 (x even) and 0.25 (x odd);
    /// the last column of an odd width uses the x-even formula; small-image taps clamped (row/column cache replication).</summary>
    static Vec4F[] UpscaleAdd(Vec4F[] a, Vec4F[] b, int sw, int sh, Vec4F[] c, int w, int h)
    {
        var s = new Vec4F[sw * sh]; for (int i = 0; i < s.Length; i++) s[i] = new Vec4F(a[i].R - b[i].R, a[i].G - b[i].G, a[i].B - b[i].B, a[i].A - b[i].A);
        var o = new Vec4F[w * h];
        Vec4F S(int m, int j) => s[Math.Clamp(m, 0, sh - 1) * sw + Math.Clamp(j, 0, sw - 1)];
        static float L4(float p, float q, float r, float t) => ((p + q) + r) + t;
        for (int y = 0; y < h; y++)
        {
            int m = y >> 1; bool odd = (y & 1) != 0;
            for (int x = 0; x < w; x += 2)
            {
                int j = x >> 1;
                Vec4F sm1jp = S(m - 1, j + 1), sm1jm = S(m - 1, j - 1), sp1jm = S(m + 1, j - 1), sp1jp = S(m + 1, j + 1), sp1j = S(m + 1, j), sm1j = S(m - 1, j), smjm = S(m, j - 1), smjp = S(m, j + 1), smj = S(m, j);
                Vec4F cc = c[y * w + x];
                if (!odd)
                {
                    o[y * w + x] = new Vec4F(
                        ((smj.R * U64) + (L4(sp1j.R, sm1j.R, smjm.R, smjp.R) * U08 + L4(sm1jp.R, sm1jm.R, sp1jm.R, sp1jp.R) * U01)) + cc.R,
                        ((smj.G * U64) + (L4(sp1j.G, sm1j.G, smjm.G, smjp.G) * U08 + L4(sm1jp.G, sm1jm.G, sp1jm.G, sp1jp.G) * U01)) + cc.G,
                        ((smj.B * U64) + (L4(sp1j.B, sm1j.B, smjm.B, smjp.B) * U08 + L4(sm1jp.B, sm1jm.B, sp1jm.B, sp1jp.B) * U01)) + cc.B,
                        ((smj.A * U64) + (L4(sp1j.A, sm1j.A, smjm.A, smjp.A) * U08 + L4(sm1jp.A, sm1jm.A, sp1jm.A, sp1jp.A) * U01)) + cc.A);
                    if (x + 1 < w)
                    {
                        Vec4F c1 = c[y * w + x + 1];
                        o[y * w + x + 1] = new Vec4F(
                            (L4(sm1jp.R, sm1j.R, sp1j.R, sp1jp.R) * U05 + c1.R) + (smjp.R + smj.R) * U40,
                            (L4(sm1jp.G, sm1j.G, sp1j.G, sp1jp.G) * U05 + c1.G) + (smjp.G + smj.G) * U40,
                            (L4(sm1jp.B, sm1j.B, sp1j.B, sp1jp.B) * U05 + c1.B) + (smjp.B + smj.B) * U40,
                            (L4(sm1jp.A, sm1j.A, sp1j.A, sp1jp.A) * U05 + c1.A) + (smjp.A + smj.A) * U40);
                    }
                }
                else
                {   // odd row: small rows m, m+1
                    o[y * w + x] = new Vec4F(
                        (L4(smjp.R, smjm.R, sp1jm.R, sp1jp.R) * U05 + cc.R) + (sp1j.R + smj.R) * U40,
                        (L4(smjp.G, smjm.G, sp1jm.G, sp1jp.G) * U05 + cc.G) + (sp1j.G + smj.G) * U40,
                        (L4(smjp.B, smjm.B, sp1jm.B, sp1jp.B) * U05 + cc.B) + (sp1j.B + smj.B) * U40,
                        (L4(smjp.A, smjm.A, sp1jm.A, sp1jp.A) * U05 + cc.A) + (sp1j.A + smj.A) * U40);
                    if (x + 1 < w)
                    {
                        Vec4F c1 = c[y * w + x + 1];
                        o[y * w + x + 1] = new Vec4F(
                            L4(smjp.R, smj.R, sp1j.R, sp1jp.R) * U25 + c1.R,
                            L4(smjp.G, smj.G, sp1j.G, sp1jp.G) * U25 + c1.G,
                            L4(smjp.B, smj.B, sp1j.B, sp1jp.B) * U25 + c1.B,
                            L4(smjp.A, smj.A, sp1j.A, sp1jp.A) * U25 + c1.A);
                    }
                }
            }
        }
        return o;
    }

    static byte[]? _jitter;
    /// <summary>Discard the cached jitter table so the next <see cref="PatchNlm4"/> call rebuilds it for its own
    /// <c>step</c>. The table depends only on <c>step</c>, which never changes within a render; a caller running the
    /// kernel on its own with a different step resets it first.</summary>
    public static void ResetJitterTable() => _jitter = null;
    static byte[] Jitter(int step)
    {
        var t = new byte[25106]; ulong x = 0x330E;
        for (int n = 0; n < t.Length; n++) { x = (0x5DEECE66DUL * x + 0xBUL) & 0xFFFFFFFFFFFFUL; t[n] = (byte)((uint)(x >> 16) % (uint)step); }
        return t;
    }

    /// <summary>`ImageDenoisePatchNLM&lt;4&gt;`: jittered 4×4 patch NLM over a quincunx window, four quadrant passes per 128×128 tile, weights per lane.</summary>
    public static Vec4F[] PatchNlm4(Vec4F[] src, Vec4F[] vst, int w, int h, Vec4F hn, int W, int step)
    {
        var tab = _jitter ??= Jitter(step);
        var outp = new Vec4F[w * h]; var wsum = new Vec4F[w * h];
        for (int i = 0; i < src.Length; i++) { outp[i] = new Vec4F(src[i].R * C001, src[i].G * C001, src[i].B * C001, src[i].A * C001); wsum[i] = new Vec4F(C001, C001, C001, C001); }
        float k16R = hn.R * 16.0f, k16G = hn.G * 16.0f, k16B = hn.B * 16.0f, k16A = hn.A * 16.0f;   // h16 = hn·16 (DAT_180687650), T = vst(q)·h16 per lane
        var h16 = new Vec4F[w * h]; for (int i = 0; i < vst.Length; i++) h16[i] = new Vec4F(vst[i].R * k16R, vst[i].G * k16G, vst[i].B * k16B, vst[i].A * k16A);
        var region = new RectI(2, 2, w - 1, h - 1);
        var tiles = Tiler.Rects(region, 128, 128).ToList();
        var acc = new Vec4F[16]; var refp = new Vec4F[16]; Span<Vec4F> A = stackalloc Vec4F[16]; Span<Vec4F> cand = stackalloc Vec4F[16];
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
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) refp[r * 4 + c] = src[(qy - 2 + r) * w + (qx - 2 + c)];
                        var T = h16[qy * w + qx]; float rTR = Rcp(T.R), rTG = Rcp(T.G), rTB = Rcp(T.B), rTA = Rcp(T.A);
                        int sx0 = Math.Max(qx - (W >> 1), 2); int sx1 = Math.Min(sx0 + W, w - 1); int sy0 = Math.Max(qy - (W >> 1), 2); int sy1 = Math.Min(sy0 + W, h - 1);
                        Array.Clear(acc); float waR = 0f, waG = 0f, waB = 0f, waA = 0f;
                        for (int cy = sy1 - W; cy < sy1; cy++)
                            for (int cx = (sx1 - W) + (cy & 1); cx < sx1; cx += 2)
                            {
                                Vec4F P, Q;
                                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { var cv = src[(cy - 2 + r) * w + (cx - 2 + c)]; cand[r * 4 + c] = cv; var rf = refp[r * 4 + c]; A[r * 4 + c] = new Vec4F(Abs(cv.R - rf.R), Abs(cv.G - rf.G), Abs(cv.B - rf.B), Abs(cv.A - rf.A)); }
                                P = Add(Add(A[3], A[2]), Add(A[1], A[0])); P = Add(A[7], P); P = Add(A[9], P); P = Add(A[11], P); P = Add(A[13], P); P = Add(A[15], P);
                                Q = Add(A[5], A[4]); Q = Add(A[6], Q); Q = Add(A[8], Q); Q = Add(A[10], Q); Q = Add(A[12], Q); Q = Add(A[14], Q);
                                var D = Add(P, Q); float d = Max(Max(D.R, D.G), D.B);
                                float wvR = Max(1.0f - Max(d - T.R, 0f) * rTR, 0f), wvG = Max(1.0f - Max(d - T.G, 0f) * rTG, 0f), wvB = Max(1.0f - Max(d - T.B, 0f) * rTB, 0f), wvA = Max(1.0f - Max(d - T.A, 0f) * rTA, 0f);
                                for (int k = 0; k < 16; k++) { var cv = cand[k]; acc[k] = new Vec4F(acc[k].R + cv.R * wvR, acc[k].G + cv.G * wvG, acc[k].B + cv.B * wvB, acc[k].A + cv.A * wvA); }
                                waR += wvR; waG += wvG; waB += wvB; waA += wvA;
                            }
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                            {
                                int i = (qy - 2 + r) * w + (qx - 2 + c); var o = outp[i]; var ac = acc[r * 4 + c]; var ws = wsum[i];
                                outp[i] = new Vec4F(o.R + ac.R, o.G + ac.G, o.B + ac.B, o.A + ac.A); wsum[i] = new Vec4F(ws.R + waR, ws.G + waG, ws.B + waB, ws.A + waA);
                            }
                    }
            }
        for (int i = 0; i < outp.Length; i++) { var o = outp[i]; var ws = wsum[i]; outp[i] = new Vec4F(Rcp(ws.R) * o.R, Rcp(ws.G) * o.G, Rcp(ws.B) * o.B, src[i].A); }
        return outp;
    }
}
