using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoISP::CreateStereoImage`, color-sensor path: the stereo SoftISP (`collapse2`, hot pixels, IR
/// crosstalk, lens shading; no WB/CC/tone), exposure-gain scaling, rotation+CRA warp into the half-resolution view with a
/// 6-tap Lanczos-3 resampler (64 phases, fill 0), `ConvertToYUV` with the sensor's luma basis divided by the AsShot neutral
/// (fast log2/exp2 polynomials, gamma 1/2.2, offsets 128 on U/V) and the 8-bit RGBA store (`cvtps2dq` + saturation, alpha 1).
/// </summary>
public static class StereoImage
{
    /// <summary>The Bayer tuning of the stereo images (defaults + stereo overrides).</summary>
    public static Tuning BuildTuning(bool hotPixelLeakage)
    {
        var t = Tuning.LumenDefaults();
        t.Set("demosaicking.type", Environment.GetEnvironmentVariable("LUX_STEREO_DEMOSAIC") ?? "collapse2");
        t.Set("hot_pixel_removal.type", "default");
        t.Set("hot_pixel_leakage_removal.type", hotPixelLeakage ? "default" : "none");
        t.Set("auto_white_balance.type", "none");
        t.Set("tone_mapping.type", "none");
        t.Set("color_correction.type", "none");
        t.Set("output.color_space", "none");
        t.Set("cross_talk_correction.type", "ir_correction");
        t.Set("lens_shading.type", "default");
        foreach (var k in (Environment.GetEnvironmentVariable("LUX_STEREO_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) t.Set(k + ".type", "none");   // diagnostic
        return t;
    }

    /// <summary>CreateStereoImage lambda_2: the stereo SoftISP runs per 256×256 tile of the half-res work image (source rect = tile·2 for
    /// `collapse2`); stage kernels that use the payload rect (lens shading, IR crosstalk) therefore see tile-relative rects — reproduce that.</summary>
    public static float[] RunIspTiled(SoftIsp isp, CapturedFrame frame, int div, out int w, out int h, Action<string>? log = null)
    {
        w = frame.Width / div; h = frame.Height / div;
        var outp = new float[w * h * 4];
        int tile = int.TryParse(Environment.GetEnvironmentVariable("LUX_STEREO_TILE"), out int tt) ? tt : 256;   // Lumen: 256×256 work-image tiles
        for (int ty = 0; ty < h; ty += tile)
            for (int tx = 0; tx < w; tx += tile)
            {
                int x1 = Math.Min(tx + tile, w), y1 = Math.Min(ty + tile, h);
                var roi = new RectI(tx * div, ty * div, x1 * div, y1 * div);
                var img = isp.ProcessBayer(frame, roi, 0, log);
                if (img.Width == (x1 - tx) * div)   // diagnostic: a full-resolution demosaic → average div×div blocks
                {
                    for (int y = 0; y < y1 - ty; y++)
                        for (int x = 0; x < x1 - tx; x++)
                        {
                            float r = 0, g = 0, b = 0;
                            for (int yy = 0; yy < div; yy++) for (int xx = 0; xx < div; xx++) { var v = img.Row(y * div + yy)[x * div + xx]; r += v.R; g += v.G; b += v.B; }
                            int o = ((ty + y) * w + tx + x) * 4; float inv = 1f / (div * div); outp[o] = r * inv; outp[o + 1] = g * inv; outp[o + 2] = b * inv; outp[o + 3] = 1f;
                        }
                    continue;
                }
                for (int y = 0; y < img.Height; y++)
                {
                    var row = img.Row(y);
                    for (int x = 0; x < img.Width; x++) { int o = ((ty + y) * w + tx + x) * 4; outp[o] = row[x].R; outp[o + 1] = row[x].G; outp[o + 2] = row[x].B; outp[o + 3] = row[x].A; }
                }
            }
        return outp;
    }

    /// <summary>The mono SoftISP (mono linearize, PostProcessingGray no-op,
    /// float lens shading) run per 256×256 tile of the FULL-resolution frame, then `× g` per tile. Returns the float image.</summary>
    public static float[] RunMonoIsp(Lux.Engine.Pipeline.Isp.CapturedFrame frame, float black, float white, float gain, float lensMultiplier = 1f)
    {
        int W = frame.Width, H = frame.Height; var outp = new float[W * H];
        var (cols, rows, grid0) = Lux.Engine.Pipeline.Isp.Stages.LensShadingKernel.ModelGrid(frame.Header, frame.Module);
        var grid = Lux.Engine.Pipeline.Isp.Stages.LensShadingKernel.Transform(grid0, lensMultiplier, false);
        float scale = 1.0f / (white - black);
        int nx = W / 256 + (W % 256 > 128 ? 1 : 0), ny = H / 256 + (H % 256 > 128 ? 1 : 0);   // Tiler::Run partition, last tile absorbs the remainder
        for (int tj = 0; tj < ny; tj++)
            for (int ti = 0; ti < nx; ti++)
            {
                int x0 = 256 * ti, x1 = ti == nx - 1 ? W : x0 + 256, y0 = 256 * tj, y1 = tj == ny - 1 ? H : y0 + 256;
                int w = x1 - x0, h = y1 - y0; var tile = new float[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) tile[y * w + x] = ((float)frame.Raw[(y0 + y) * frame.Stride + x0 + x] - black) * scale;
                Lux.Engine.Pipeline.Isp.Stages.LensShadingMono.Apply(tile, w, h, x0, y0, W, H, cols, rows, grid);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) outp[(y0 + y) * W + x0 + x] = tile[y * w + x] * gain;
            }
        return outp;
    }

    /// <summary>(v,v,v,1) → re-mosaic → `ImageDemosaickFilter&lt;3&gt;` with red (1,0): the half-res vec4 image of the mono path.</summary>
    public static float[] MonoToHalfRgba(float[] mono, int W, int H, out int w, out int h)
    {
        var bayer = new float[W * H];
        for (int y = 0; y + 1 < H; y += 2)
            for (int x = 0; x + 1 < W; x += 2)
            {   // lanes are all v: (y,x) lane1, (y,x+1) lane0, (y+1,x) lane2, (y+1,x+1) lane1
                bayer[y * W + x] = mono[y * W + x]; bayer[y * W + x + 1] = mono[y * W + x + 1];
                bayer[(y + 1) * W + x] = mono[(y + 1) * W + x]; bayer[(y + 1) * W + x + 1] = mono[(y + 1) * W + x + 1];
            }
        var img = Lux.Engine.Pipeline.Isp.Stages.Demosaic2xCatmull.Run(bayer, W, 0, W, H, 1, 0);
        w = img.Width; h = img.Height; var o = new float[w * h * 4];
        for (int y = 0; y < h; y++) { var row = img.Row(y); for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; o[i] = row[x].R; o[i + 1] = row[x].G; o[i + 2] = row[x].B; o[i + 3] = row[x].A; } }
        return o;
    }

    /// <summary>Luma `((0 + s·G) + (s·B + s·R))·0.33333`, `sign·255·powf(|Y|, 1/2.2)`, bytes `[b,b,b,1]` with
    /// `b = clamp(trunc(v + copysign(0.5, v)), 0, 255)`; returns the 8-bit image and the call's return value `sign(s)·255·powf(|s|, 1/2.2)`.</summary>
    public static (byte[] Rgba8, float Ret) MonoToBytes(float[] rgba, int w, int h, int sensorType)
    {
        float t3 = (sensorType == 2 || sensorType == 3 ? LumaAr1335 : LumaOther)[3];
        float s = 1.0f / t3, third = BitConverter.Int32BitsToSingle(0x3eaaaa3b), gammaE = BitConverter.Int32BitsToSingle(0x3ee8ba2e);
        var outp = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            float r = s * rgba[p * 4], g = s * rgba[p * 4 + 1], b = s * rgba[p * 4 + 2];
            float Y = ((0f + g) + (b + r)) * third;
            float sgn = (Y == 0f || float.IsNaN(Y)) ? 0f : BitConverter.Int32BitsToSingle((int)((BitConverter.SingleToInt32Bits(Y) & unchecked((int)0x80000000)) | 0x3f800000));
            float k = sgn * 255.0f;
            float v = MuslMath.Powf(MathF.Abs(Y), gammaE) * k;
            float tq = v + BitConverter.Int32BitsToSingle((int)((BitConverter.SingleToInt32Bits(v) & unchecked((int)0x80000000)) | 0x3f000000));
            tq = tq > 0f ? tq : 0f; tq = tq < 255f ? tq : 255f;   // NaN → 0
            byte bb = (byte)((int)tq & 0xff);
            outp[p * 4] = bb; outp[p * 4 + 1] = bb; outp[p * 4 + 2] = bb; outp[p * 4 + 3] = 1;
        }
        float ss = s, sgnS = ss == 0f ? 0f : BitConverter.Int32BitsToSingle((int)((BitConverter.SingleToInt32Bits(ss) & unchecked((int)0x80000000)) | 0x3f800000));
        return (outp, (sgnS * 255.0f) * MuslMath.Powf(MathF.Abs(ss), gammaE));
    }

    // sensor luma table per sensor type: AR1335 (types 2,3) and (4,5)
    static readonly float[] LumaAr1335 = { BitConverter.Int32BitsToSingle(0x3e5cb924), BitConverter.Int32BitsToSingle(0x3edd5758), BitConverter.Int32BitsToSingle(0x3eb44c16), BitConverter.Int32BitsToSingle(0x40145faf) };
    static readonly float[] LumaOther = { BitConverter.Int32BitsToSingle(0x3e9e39b4), BitConverter.Int32BitsToSingle(0x3e95ca4b), BitConverter.Int32BitsToSingle(0x3ecbfc22), BitConverter.Int32BitsToSingle(0x40351eb8) };

    static float InvSqrtNR(float x)
    {
        float rs = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
        float S = x * rs;
        return ((S * rs) + (-3.0f)) * (S * (-0.5f));   // = sqrt-style NR? no: this is the "kv" form: ((S·rs + (−3))·(S·(−0.5)))
    }

    /// <summary>The 4×4 YUV matrix rows (v, u', t') for the sensor type and AsShot neutral (Lumen's operation order).</summary>
    public static float[][] YuvMatrix(int sensorType, float[] neutral)
    {
        var tbl = sensorType is 2 or 3 ? LumaAr1335 : LumaOther;
        float a = tbl[0], b = tbl[1], c = tbl[2];
        float invx = 1.0f / neutral[0], invy = 1.0f / neutral[1], invz = 1.0f / neutral[2];
        float s = c + a;
        float u0 = (b * (-b) - s * c) * invx, u1 = ((a - c) * b) * invy, u2 = (s * a - b * (-b)) * invz;
        float t0 = invx * (-b), t1 = invy * s, t2 = invz * (-b);
        float v2 = c * c + (b * b + a * a);
        float rs = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(v2)).ToScalar();
        float S = v2 * rs;
        float kv = ((S * rs + (-3.0f)) * (S * (-0.5f)));
        if (v2 == 0f) kv = 0f;
        float u2n = u2 * u2 + (u1 * u1 + u0 * u0);
        float rsu = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(u2n)).ToScalar();
        float ku = ((rsu * (-0.5f)) * kv) * ((u2n * rsu) * rsu + (-3.0f));
        float t2n = t2 * t2 + (t1 * t1 + t0 * t0);
        float rst = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(t2n)).ToScalar();
        float kt = ((rst * (-0.5f)) * kv) * ((t2n * rst) * rst + (-3.0f));
        return new[] { new[] { a, b, c, 0f }, new[] { u0 * ku, u1 * ku, ku * u2, 0f }, new[] { t0 * kt, t1 * kt, kt * t2, 0f }, new float[4] };
    }

    /// <summary>Fast `255·sign·|x|^(1/2.2)` of `ConvertToYUV` (log2/exp2 polynomials with raw bit tricks).</summary>
    public static float GammaEncode(float m)
    {
        int bits = BitConverter.SingleToInt32Bits(m);
        int a = bits & 0x7fffffff;
        float f = BitConverter.Int32BitsToSingle((a & 0x007fffff) | 0x3f800000);
        float e = (float)(((a + unchecked((int)0xc0800000)) >> 23));
        float log2 = ((f * BitConverter.Int32BitsToSingle(0x3e511af3) + BitConverter.Int32BitsToSingle(unchecked((int)0xbfa05375))) * f + BitConverter.Int32BitsToSingle(0x40552f75)) * f + (e + BitConverter.Int32BitsToSingle(unchecked((int)0xc0121769)));
        float l = log2 * BitConverter.Int32BitsToSingle(0x3ee8ba2e);
        l = MathF.Min(MathF.Max(l, -126.0f), 128.0f);
        int i = (int)l + (BitConverter.SingleToInt32Bits(l) >> 31);   // −1 when the sign bit is set → floor
        float fr = l - (float)i;
        float p2 = ((fr * BitConverter.Int32BitsToSingle(0x3d9fcb52) + BitConverter.Int32BitsToSingle(0x3e677e26)) * fr + BitConverter.Int32BitsToSingle(0x3f322226)) * fr + BitConverter.Int32BitsToSingle(0x3f7ffb19);
        float pw = BitConverter.Int32BitsToSingle((i << 23) + BitConverter.SingleToInt32Bits(p2));
        float sgn = a == 0 ? 0f : BitConverter.Int32BitsToSingle((bits & unchecked((int)0x80000000)) | 0x3f800000);
        return pw * (sgn * 255.0f);
    }

    /// <summary>`ConvertToYUV` on an RGBA float image in place: (Y, 128+U, 128+V, 1).</summary>
    public static void ConvertToYuv(float[] rgba, int w, int h, float[][] M)
    {
        float[] M0 = M[0], M1 = M[1], M2 = M[2];
        float y0 = M0[0], y1 = M0[1], y2 = M0[2], y3 = M0[3];
        float u0 = M1[0], u1 = M1[1], u2 = M1[2], u3 = M1[3];
        float v0 = M2[0], v1 = M2[1], v2 = M2[2], v3 = M2[3];
        var px = rgba.AsSpan(0, w * h * 4);
        for (int o = 0; o < px.Length; o += 4)
        {
            float x = px[o], y = px[o + 1], z = px[o + 2], ww = px[o + 3];
            float my = (ww * y3 + z * y2) + (y * y1 + x * y0);
            float mu = (ww * u3 + z * u2) + (y * u1 + x * u0);
            float mv = (ww * v3 + z * v2) + (y * v1 + x * v0);
            px[o] = GammaEncode(my) + 0f; px[o + 1] = GammaEncode(mu) + 128.0f; px[o + 2] = GammaEncode(mv) + 128.0f; px[o + 3] = 1.0f;
        }
    }

    /// <summary>8-bit store: `cvtps2dq` (round-to-nearest-even) → `packssdw` → `packuswb`.</summary>
    public static byte[] ToRgba8(float[] rgba, int w, int h)
    {
        var o = new byte[w * h * 4];
        for (int i = 0; i < w * h * 4; i++)
        {
            float v = rgba[i];
            int iv = float.IsNaN(v) ? 0 : (int)MathF.Round(v, MidpointRounding.ToEven);
            if (v >= 2147483648f) iv = int.MinValue;   // cvtps2dq overflow → 0x80000000
            int sw = Math.Clamp(iv, short.MinValue, short.MaxValue);
            o[i] = (byte)Math.Clamp(sw, 0, 255);
        }
        return o;
    }

    /// <summary>Lanczos-3 tap table (64 phases × 6 taps), `sinc(x)·sinc(x/3)` normalized, as built per call by the warp.</summary>
    public static float[] LanczosTable()
    {
        var tbl = new float[64 * 6];
        float step = BitConverter.Int32BitsToSingle(0x3c800000), pi3 = BitConverter.Int32BitsToSingle(0x3f860a92), pi = BitConverter.Int32BitsToSingle(0x40490fdb), pi2 = BitConverter.Int32BitsToSingle(0x411de9e7), three = 3.0f;
        for (int k = 0; k < 64; k++)
        {
            float p = k * step; float sum = 0f;
            for (int j = 0; j < 6; j++)
            {
                float x = p - (float)(j - 2);
                float w;
                if (x == 0f) w = 1.0f;
                else if (MathF.Abs(x) < three)
                {
                    // s1 = sinf(x·π); t = s1·3; s2 = sinf(x·π/3); w = (s2·t) / ((x·x)·π²)  (CRT sinf)
                    float s1 = WineCrt.Sinf(x * pi), t = s1 * three, s2 = WineCrt.Sinf(x * pi3);
                    w = (s2 * t) / ((x * x) * pi2);
                }
                else w = 0f;
                tbl[k * 6 + j] = w; sum = sum + w;
            }
            float inv = 1.0f / sum;
            for (int j = 0; j < 6; j++) tbl[k * 6 + j] *= inv;
        }
        return tbl;
    }

    /// <summary>The tap orders of the warp. `Cols` is the default order. The other orders are diagnostic.</summary>
    enum TapOrder { Cols, Rows, Swap, Prod }

    /// <summary>The tap order from `LUX_WARP_ORDER`, read one time.</summary>
    static readonly TapOrder WarpOrder = Environment.GetEnvironmentVariable("LUX_WARP_ORDER") switch
    {
        "rows" => TapOrder.Rows, "swap" => TapOrder.Swap, "prod" => TapOrder.Prod, _ => TapOrder.Cols,
    };

    /// <summary>`ImageWarp&lt;5,0,vec4x32f,LensUndistortCRA&gt;`: dst (w×h) from src via `H` (row-major), CRA LUT, 6-tap Lanczos, fill 0.
    /// The vector form puts the four channels of a pixel in one vector. Each lane has the products and the sum order of the
    /// scalar form.</summary>
    public static unsafe float[] Warp(float[] src, int sw, int sh, int dw, int dh, float[] H, float[] lut, float cx, float cy, float sx, float sy)
    {
        // The vector form reads through pointers, so it needs a source that holds the whole sw×sh image.
        if (!Vector128.IsHardwareAccelerated || WarpOrder != TapOrder.Cols || sw < 1 || sh < 1 || src.Length < (long)sw * sh * 4)
            return WarpScalar(src, sw, sh, dw, dh, H, lut, cx, cy, sx, sy);
        var tbl = LanczosTable(); var dst = new float[dw * dh * 4];
        float h0 = H[0], h1 = H[1], h2 = H[2], h3 = H[3], h4 = H[4], h5 = H[5], h6 = H[6], h7 = H[7], h8 = H[8];
        int* ro = stackalloc int[6]; int* co = stackalloc int[6];
        fixed (float* ps = src, pd = dst, pt = tbl)
        {
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float Xn = h2 + (h0 * x + h1 * y), Yn = h5 + (h3 * x + h4 * y);
                    float den = (h7 * y + h6 * x) + h8, w = 1.0f / den;
                    float dx = w * Xn - cx, dy = w * Yn - cy;
                    float r2 = (sy * dy) * (sy * dy) + (sx * dx) * (sx * dx);
                    float r;
                    if (r2 == 0f) r = 0f; else { float rs = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar(); float S = r2 * rs; r = ((S * rs) + (-3.0f)) * (S * (-0.5f)); }
                    int idx = (int)r; if (idx >= 0x1000) idx = 0xfff;
                    float lu = lut[idx];
                    int px = (int)(((cx + (-2.0f)) + lu * dx) * 64.0f), py = (int)(((cy + (-2.0f)) + dy * lu) * 64.0f);
                    int i = px >> 6, fx = px & 63, j = py >> 6, fy = py & 63;
                    bool interior = i >= 0 && i <= sw - 6 && j >= 0 && j <= sh - 6;
                    if (!interior && !(j < sh && j + 6 > 0 && i < sw && i + 6 > 0)) continue;   // fill (0,0,0,0)
                    float* ty = pt + fy * 6, tx = pt + fx * 6;
                    Vector128<float> ty0 = Vector128.Create(ty[0]), ty1 = Vector128.Create(ty[1]), ty2 = Vector128.Create(ty[2]), ty3 = Vector128.Create(ty[3]), ty4 = Vector128.Create(ty[4]), ty5 = Vector128.Create(ty[5]);
                    Vector128<float> c0, c1, c2, c3, c4, c5;
                    if (interior)
                    {
                        long stride = (long)sw * 4; float* p0 = ps + ((long)j * sw + i) * 4;
                        float* p1 = p0 + stride, p2 = p1 + stride, p3 = p2 + stride, p4 = p3 + stride, p5 = p4 + stride;
                        c0 = WarpColumn(p0, p1, p2, p3, p4, p5, 0, ty0, ty1, ty2, ty3, ty4, ty5);
                        c1 = WarpColumn(p0, p1, p2, p3, p4, p5, 4, ty0, ty1, ty2, ty3, ty4, ty5);
                        c2 = WarpColumn(p0, p1, p2, p3, p4, p5, 8, ty0, ty1, ty2, ty3, ty4, ty5);
                        c3 = WarpColumn(p0, p1, p2, p3, p4, p5, 12, ty0, ty1, ty2, ty3, ty4, ty5);
                        c4 = WarpColumn(p0, p1, p2, p3, p4, p5, 16, ty0, ty1, ty2, ty3, ty4, ty5);
                        c5 = WarpColumn(p0, p1, p2, p3, p4, p5, 20, ty0, ty1, ty2, ty3, ty4, ty5);
                    }
                    else
                    {   // every row and column index clamped to the image
                        for (int k = 0; k < 6; k++) { ro[k] = Math.Clamp(j + k, 0, sh - 1) * sw * 4; co[k] = Math.Clamp(i + k, 0, sw - 1) * 4; }
                        float* p0 = ps + ro[0], p1 = ps + ro[1], p2 = ps + ro[2], p3 = ps + ro[3], p4 = ps + ro[4], p5 = ps + ro[5];
                        c0 = WarpColumn(p0, p1, p2, p3, p4, p5, co[0], ty0, ty1, ty2, ty3, ty4, ty5);
                        c1 = WarpColumn(p0, p1, p2, p3, p4, p5, co[1], ty0, ty1, ty2, ty3, ty4, ty5);
                        c2 = WarpColumn(p0, p1, p2, p3, p4, p5, co[2], ty0, ty1, ty2, ty3, ty4, ty5);
                        c3 = WarpColumn(p0, p1, p2, p3, p4, p5, co[3], ty0, ty1, ty2, ty3, ty4, ty5);
                        c4 = WarpColumn(p0, p1, p2, p3, p4, p5, co[4], ty0, ty1, ty2, ty3, ty4, ty5);
                        c5 = WarpColumn(p0, p1, p2, p3, p4, p5, co[5], ty0, ty1, ty2, ty3, ty4, ty5);
                    }
                    var acc = (c5 * Vector128.Create(tx[5]) + c4 * Vector128.Create(tx[4])) + ((c3 * Vector128.Create(tx[3]) + c2 * Vector128.Create(tx[2])) + (c1 * Vector128.Create(tx[1]) + c0 * Vector128.Create(tx[0])));
                    acc.Store(pd + ((long)y * dw + x) * 4);
                }
        }
        return dst;
    }

    /// <summary>The warp in scalar form, one channel at a time, for every tap order.</summary>
    static float[] WarpScalar(float[] src, int sw, int sh, int dw, int dh, float[] H, float[] lut, float cx, float cy, float sx, float sy)
    {
        var tbl = LanczosTable(); var dst = new float[dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float Xn = H[2] + (H[0] * x + H[1] * y), Yn = H[5] + (H[3] * x + H[4] * y);
                float den = (H[7] * y + H[6] * x) + H[8], w = 1.0f / den;
                float dx = w * Xn - cx, dy = w * Yn - cy;
                float r2 = (sy * dy) * (sy * dy) + (sx * dx) * (sx * dx);
                float r;
                if (r2 == 0f) r = 0f; else { float rs = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar(); float S = r2 * rs; r = ((S * rs) + (-3.0f)) * (S * (-0.5f)); }
                int idx = (int)r; if (idx >= 0x1000) idx = 0xfff;
                float lu = lut[idx];
                int px = (int)(((cx + (-2.0f)) + lu * dx) * 64.0f), py = (int)(((cy + (-2.0f)) + dy * lu) * 64.0f);
                int i = px >> 6, fx = px & 63, j = py >> 6, fy = py & 63;
                bool interior = i >= 0 && i <= sw - 6 && j >= 0 && j <= sh - 6;
                bool partial = !interior && j < sh && j + 6 > 0 && i < sw && i + 6 > 0;
                if (!interior && !partial) continue;   // fill (0,0,0,0)
                WarpPixelScalar(src, sw, sh, i, j, fx, fy, interior, tbl, dst, (y * dw + x) * 4);
            }
        return dst;
    }

    /// <summary>The y-tap sum of one source column for the four channels: `(P5·ty5 + P4·ty4) + ((P3·ty3 + P2·ty2) + (P1·ty1 + P0·ty0))`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe Vector128<float> WarpColumn(float* p0, float* p1, float* p2, float* p3, float* p4, float* p5, int o,
        Vector128<float> ty0, Vector128<float> ty1, Vector128<float> ty2, Vector128<float> ty3, Vector128<float> ty4, Vector128<float> ty5)
        => (Vector128.Load(p5 + o) * ty5 + Vector128.Load(p4 + o) * ty4) + ((Vector128.Load(p3 + o) * ty3 + Vector128.Load(p2 + o) * ty2) + (Vector128.Load(p1 + o) * ty1 + Vector128.Load(p0 + o) * ty0));

    /// <summary>One warp pixel in scalar form, for every tap order.</summary>
    static void WarpPixelScalar(float[] src, int sw, int sh, int i, int j, int fx, int fy, bool interior, float[] tbl, float[] dst, int o)
    {
        var order = WarpOrder;
        if (order == TapOrder.Swap) (fx, fy) = (fy, fx);
        Span<int> ro = stackalloc int[6], co = stackalloc int[6];
        for (int k = 0; k < 6; k++)
        {
            ro[k] = (interior ? j + k : Math.Clamp(j + k, 0, sh - 1)) * sw * 4;
            co[k] = (interior ? i + k : Math.Clamp(i + k, 0, sw - 1)) * 4;
        }
        var ty = tbl.AsSpan(fy * 6, 6); var tx = tbl.AsSpan(fx * 6, 6);
        Span<float> C = stackalloc float[6];
        for (int c = 0; c < 4; c++)
        {
            if (order == TapOrder.Prod)
            {
                float acc = 0f;
                for (int rr = 0; rr < 6; rr++) for (int cc = 0; cc < 6; cc++) acc += src[ro[rr] + co[cc] + c] * (ty[rr] * tx[cc]);
                dst[o + c] = acc;
            }
            else if (order != TapOrder.Rows)
            {
                for (int cc = 0; cc < 6; cc++)
                {
                    int k = co[cc] + c;
                    C[cc] = (src[ro[5] + k] * ty[5] + src[ro[4] + k] * ty[4]) + ((src[ro[3] + k] * ty[3] + src[ro[2] + k] * ty[2]) + (src[ro[1] + k] * ty[1] + src[ro[0] + k] * ty[0]));
                }
                dst[o + c] = (C[5] * tx[5] + C[4] * tx[4]) + ((C[3] * tx[3] + C[2] * tx[2]) + (C[1] * tx[1] + C[0] * tx[0]));
            }
            else
            {
                for (int rr = 0; rr < 6; rr++)
                {
                    int k = ro[rr] + c;
                    C[rr] = (src[k + co[5]] * tx[5] + src[k + co[4]] * tx[4]) + ((src[k + co[3]] * tx[3] + src[k + co[2]] * tx[2]) + (src[k + co[1]] * tx[1] + src[k + co[0]] * tx[0]));
                }
                dst[o + c] = (C[5] * ty[5] + C[4] * ty[4]) + ((C[3] * ty[3] + C[2] * ty[2]) + (C[1] * ty[1] + C[0] * ty[0]));
            }
        }
    }

    /// <summary>Rotation homography `H = (K_B · inv(M)) · inv(K_A)` with `M = R_A·R_Bᵀ` (product association `(a0·b0 + a1·b1) + a2·b2`).</summary>
    public static float[] RotationHomography(CameraCalib viewA, CameraCalib modB)
    {
        var M = Mat3F.MulABt(viewA.R, modB.R);
        var T = Mat3F.Mul(modB.K, Mat3F.Inverse(M));
        return Mat3F.Mul(T, Mat3F.Inverse(viewA.K));
    }
}
