using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Color;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ColorCorrection:optimized` — slot 10 (`setColorCorrection` case 3, one shared body,
/// meta 1/1/1). Identical to <see cref="ColorCorrectionDefaultStage"/> — the same camera→linear-ProPhoto-D50
/// `ImageConvertColorSpace` with the same Stats camera matrix — **plus**, when the `optimized` Stats functor
/// left a non-empty `HSVMap` in the Stats, a three-pass round trip in that ProPhoto space:
/// `ImageConvertRGBToHSV` → `HSVMap::apply` → `ImageConvertHSVToRGB`. All three are pointwise, so their 512×512 tiling
/// cannot change a pixel.
/// </summary>
public sealed class ColorCorrectionOptimizedStage : IStage
{
    public StageName Stage => StageName.ColorCorrection;
    public string TypeString => "optimized";
    public StageMeta Meta => new(1, 1, 1f);   // Lumen's slot meta: pad 1, align 1, scale 1

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
        using var running = StageParallel.Enter();
        ColorSpaceConvert.Convert(dst, img.View(abs), from, to, 1);
        if (map is null || map.IsEmpty) return dst;   // the HSVMap::isEmpty() test
        // The three pointwise passes fused per pixel, eight pixels at a time, in place — every pixel goes through exactly the
        // operations of RgbToHsv, ApplyMap and HsvToRgb below (which keep the reference form and finish each row's last pixels)
        StageParallel.For(dst.Height, dst.Width * 60, (ya, yb) =>
        {
            for (int y = ya; y < yb; y++)
            {
                var row = dst.Row(y);
                int x = Soa8.Supported ? HsvRoundTrip8(row, map) : 0;
                for (; x < row.Length; x++) row[x] = HsvToRgbPixel(ApplyMapPixel(RgbToHsvPixel(row[x]), map));
            }
        });
        return dst;
    }

    /// <summary>The fused round trip on eight pixels per step; returns the first pixel not done.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static unsafe int HsvRoundTrip8(Span<Vec4F> row, HsvMap map)
    {
        int w = row.Length, x = 0;
        ref float rb = ref Unsafe.As<Vec4F, float>(ref MemoryMarshal.GetReference(row));
        var zero = Vector256<float>.Zero; var one = Vector256.Create(1f); var sixth = Vector256.Create(0.16666667f); var two = Vector256.Create(2.0f); var four = Vector256.Create(4.0f);
        int stride = map.Nh + 1;
        float scaleHf = (float)(map.Nh - 1), scaleSf = (float)(map.Ns - 1);
        var scaleH = Vector256.Create(scaleHf); var scaleS = Vector256.Create(scaleSf); var vcap = Vector256.Create(1e20f); var strideV = Vector256.Create(stride);
        var six = Vector256.Create(6f); var m3 = Vector256.Create(-3f); var m2 = Vector256.Create(-2f); var m4 = Vector256.Create(-4f); var m1 = Vector256.Create(-1f);
        fixed (float* lut = map.Cells)
        for (; x + 8 <= w; x += 8)
        {
            ref float p = ref Unsafe.Add(ref rb, x * 4);
            Soa8.Load(ref p, out var R, out var G, out var B, out var A);
            // RgbToHsv
            var r = Max(R, zero); var g = Max(G, zero); var b = Max(B, zero); var a = Max(A, zero);
            var mn = Min(Min(r, g), b); var mx = Max(Max(r, g), b);
            var df = mx - mn;
            var S = Vector256.ConditionalSelect(~Vector256.Equals(mx, zero), df / mx, zero);
            var sZero = Vector256.Equals(S, zero);
            var inv = one / df;
            var isR = Vector256.Equals(r, mx); var isG = ~isR & Vector256.Equals(g, mx);
            var h = Vector256.ConditionalSelect(isR, inv * (g - b), Vector256.ConditionalSelect(isG, inv * (b - r) + two, inv * (r - g) + four));
            h = h * sixth;
            h = Vector256.ConditionalSelect(Vector256.LessThan(h, zero), h + one, h);
            var H = Vector256.ConditionalSelect(sZero, zero, h); var Sv = Vector256.ConditionalSelect(sZero, zero, S); var V = mx;
            // ApplyMap
            var qh = Min(Max(H * scaleH, zero), scaleH); var qs = Min(Max(Sv * scaleS, zero), scaleS);
            var idx = Vector256.ConvertToInt32(qh) + Vector256.ConvertToInt32(qs) * strideV;
            var fh = qh - Vector256.Floor(qh); var fs = qs - Vector256.Floor(qs);
            var a4 = idx * 4; var b4 = (idx + Vector256.Create(1)) * 4; var c4 = (idx + strideV) * 4; var d4 = (idx + strideV + Vector256.Create(1)) * 4;
            var e0 = Interp(lut, a4, b4, c4, d4, 0, fh, fs); var e1 = Interp(lut, a4, b4, c4, d4, 1, fh, fs); var e2 = Interp(lut, a4, b4, c4, d4, 2, fh, fs);
            var sum0 = e0 + H;
            var frac0 = sum0 - Vector256.Floor(sum0);
            var o0 = Min(Max(frac0, zero), one); var o1 = Min(Max(e1 * Sv, zero), one); var o2 = Min(Max(e2 * V, zero), vcap);
            // HsvToRgb on (o0, o1, o2, a)
            var t0 = o0 * six; var t1 = o0 * six; var t2 = o0 * six;
            t0 += m3; t1 += m2; t2 += m4;
            t0 = Vector256.Abs(t0); t1 = Vector256.Abs(t1); t2 = Vector256.Abs(t2);
            t0 += m1; t1 += m2; t2 += m2;
            t0 *= one; t1 *= m1; t2 *= m1;
            t0 = Min(Max(t0, zero), one); t1 = Min(Max(t1, zero), one); t2 = Min(Max(t2, zero), one);
            t0 += m1; t1 += m1; t2 += m1;
            t0 *= o1; t1 *= o1; t2 *= o1;
            t0 += one; t1 += one; t2 += one;
            t0 *= o2; t1 *= o2; t2 *= o2;
            Soa8.Store(ref p, t0, t1, t2, a);
        }
        return x;
    }
    /// <summary>One lane k of the map's bilinear cell value (ApplyMap's dx / dB / e).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe Vector256<float> Interp(float* lut, Vector256<int> ia, Vector256<int> ib, Vector256<int> ic, Vector256<int> id, int k, Vector256<float> fh, Vector256<float> fs)
    {
        var la = Simd.GatherVector256(lut + k, ia, 4); var lb = Simd.GatherVector256(lut + k, ib, 4);
        var lc = Simd.GatherVector256(lut + k, ic, 4); var ld = Simd.GatherVector256(lut + k, id, 4);
        var dx = ((lb - la) * fh) + la;
        var dB = (ld - lc) * fh;
        return (((lc - dx) + dB) * fs) + dx;
    }

    /// <summary>MAXPS dst, src: dst &gt; src ? dst : src.</summary>
    static Vector256<float> Max(Vector256<float> dst, Vector256<float> src) => Vector256.ConditionalSelect(Vector256.GreaterThan(dst, src), dst, src);
    /// <summary>MINPS dst, src: dst &lt; src ? dst : src.</summary>
    static Vector256<float> Min(Vector256<float> dst, Vector256<float> src) => Vector256.ConditionalSelect(Vector256.LessThan(dst, src), dst, src);

    static Vec4F RgbToHsvPixel(Vec4F v)
    {
        const float Sixth = 0.16666667f;
        float r = Max(v.R, 0f), g = Max(v.G, 0f), b = Max(v.B, 0f), a = Max(v.A, 0f);
        float mn = Min(Min(r, g), b);
        float mx = Max(Max(r, g), b);
        float df = mx - mn;
        float S = 0f;
        if (mx != 0f) S = df / mx;
        if (S == 0f) return new Vec4F(0f, 0f, mx, a);
        float inv = 1.0f / df;
        float h;
        if (r == mx) h = inv * (g - b);
        else if (g == mx) h = inv * (b - r) + 2.0f;
        else h = inv * (r - g) + 4.0f;
        h = h * Sixth;
        if (h < 0f) h = h + 1.0f;
        return new Vec4F(h, S, mx, a);
    }

    static Vec4F ApplyMapPixel(Vec4F p, HsvMap map)
    {
        int stride = map.Nh + 1;
        float scaleH = (float)(map.Nh - 1), scaleS = (float)(map.Ns - 1);
        var lut = map.Cells;
        float qh = Min(Max(p.R * scaleH, 0f), scaleH);
        float qs = Min(Max(p.G * scaleS, 0f), scaleS);
        int i0 = (int)qh, j0 = (int)qs;
        int idx = i0 + j0 * stride;
        float fh = qh - MathF.Floor(qh), fs = qs - MathF.Floor(qs);
        int a4 = idx * 4, b4 = (idx + 1) * 4, c4 = (idx + stride) * 4, d4 = (idx + stride + 1) * 4;
        float dx0 = ((lut[b4] - lut[a4]) * fh) + lut[a4];
        float dx1 = ((lut[b4 + 1] - lut[a4 + 1]) * fh) + lut[a4 + 1];
        float dx2 = ((lut[b4 + 2] - lut[a4 + 2]) * fh) + lut[a4 + 2];
        float dB0 = (lut[d4] - lut[c4]) * fh;
        float dB1 = (lut[d4 + 1] - lut[c4 + 1]) * fh;
        float dB2 = (lut[d4 + 2] - lut[c4 + 2]) * fh;
        float e0 = (((lut[c4] - dx0) + dB0) * fs) + dx0;
        float e1 = (((lut[c4 + 1] - dx1) + dB1) * fs) + dx1;
        float e2 = (((lut[c4 + 2] - dx2) + dB2) * fs) + dx2;
        float sum0 = e0 + p.R;
        float frac0 = sum0 - MathF.Floor(sum0);
        return new Vec4F(Min(Max(frac0, 0f), 1f), Min(Max(e1 * p.G, 0f), 1f), Min(Max(e2 * p.B, 0f), 1e20f), p.A);
    }

    static Vec4F HsvToRgbPixel(Vec4F p)
    {
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
        return new Vec4F(t0, t1, t2, p.A);
    }

    // ---- SSE scalar/packed primitives, operand order as the instructions have it ----
    static float Max(float dst, float src) => dst > src ? dst : src;   // MAXSS/MAXPS dst, src
    static float Min(float dst, float src) => dst < src ? dst : src;   // MINSS/MINPS dst, src

    /// <summary>`ImageConvertRGBToHSV` per pixel. H ∈ [0,1) turns, S ∈ [0,1], V = max lane
    /// (unbounded), A = max(srcA, 0). The leading `maxps(src, 0)` hard-clips out-of-gamut ProPhoto components —
    /// the one behavioral difference from `default`.</summary>
    public static void RgbToHsv(Image<Vec4F> dst, Image<Vec4F> src)
    {
        const float Sixth = 0.16666667f;   // 0x3e2aaaab
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

    /// <summary>`HSVMap::apply` per pixel: clamped bilinear over (hue, sat) only — the value
    /// axis is never indexed — then `H' = frac(H + cell.x)` (the builder's `+1` bias makes `frac` the modulo),
    /// `S' = clamp(S·cell.y, 0, 1)`, `V' = clamp(V·cell.z, 0, 1e20)`, alpha from the source.</summary>
    public static void ApplyMap(Image<Vec4F> dst, Image<Vec4F> src, HsvMap map)
    {
        int stride = map.Nh + 1;
        float scaleH = (float)(map.Nh - 1), scaleS = (float)(map.Ns - 1);
        var lut = map.Cells;
        const float VCap = 1e20f;   // the clamp vector (1, 1, 1e20, 1)
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y); var d = dst.Row(y);
            for (int x = 0; x < s.Length; x++)
            {
                var p = s[x];
                float qh = Min(Max(p.R * scaleH, 0f), scaleH);
                float qs = Min(Max(p.G * scaleS, 0f), scaleS);
                // lanes 2,3 of q are zero, so their floor/fraction is 0 — irrelevant below
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
                d[x] = new Vec4F(o0, o1, o2, p.A);   // lane 3 from the source
            }
        }
    }

    /// <summary>`ImageConvertHSVToRGB` per pixel:
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
