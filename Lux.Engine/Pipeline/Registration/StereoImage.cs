using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoISP::CreateStereoImage` (180326320), colour-sensor path: the stereo SoftISP (`collapse2`, hot pixels, IR
/// crosstalk, lens shading; no WB/CC/tone), exposure-gain scaling, rotation+CRA warp into the half-resolution view with a
/// 6-tap Lanczos-3 resampler (64 phases, fill 0), `ConvertToYUV` with the sensor's luma basis divided by the AsShot neutral
/// (fast log2/exp2 polynomials, gamma 1/2.2, offsets 128 on U/V) and the 8-bit RGBA store (`cvtps2dq` + saturation, alpha 1).
/// </summary>
public static class StereoImage
{
    /// <summary>The Bayer tuning `FUN_1804ee960` installs on `StereoAsyncAPI+0xc8` (defaults + stereo overrides).</summary>
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

    /// <summary>The mono SoftISP (`FUN_1803dd670` → `FUN_180413290`, domain block +0x14e0: mono linearize `18041f600`, PostProcessingGray no-op,
    /// float lens shading) run per 256×256 tile of the FULL-resolution frame, then `× g` per tile (`FUN_18032b4b0`). Returns the float image.</summary>
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

    /// <summary>`FUN_180328ff0` (v,v,v,1) → `FUN_180324c00` re-mosaic → `ImageDemosaickFilter&lt;3&gt;` with red (1,0): the half-res vec4 image of the mono path.</summary>
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

    /// <summary>lambda_4 `18032c940` + `FUN_1803291a0`: luma `((0 + s·G) + (s·B + s·R))·0.33333`, `sign·255·powf(|Y|, 1/2.2)`, bytes `[b,b,b,1]` with
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
            tq = tq > 0f ? tq : 0f; tq = tq < 255f ? tq : 255f;   // maxps/minps (NaN → 0)
            byte bb = (byte)((int)tq & 0xff);
            outp[p * 4] = bb; outp[p * 4 + 1] = bb; outp[p * 4 + 2] = bb; outp[p * 4 + 3] = 1;
        }
        float ss = s, sgnS = ss == 0f ? 0f : BitConverter.Int32BitsToSingle((int)((BitConverter.SingleToInt32Bits(ss) & unchecked((int)0x80000000)) | 0x3f800000));
        return (outp, (sgnS * 255.0f) * MuslMath.Powf(MathF.Abs(ss), gammaE));
    }

    // sensor luma table PTR_DAT_18068b850[type-2]: AR1335 (types 2,3) and (4,5)
    static readonly float[] LumaAr1335 = { BitConverter.Int32BitsToSingle(0x3e5cb924), BitConverter.Int32BitsToSingle(0x3edd5758), BitConverter.Int32BitsToSingle(0x3eb44c16), BitConverter.Int32BitsToSingle(0x40145faf) };
    static readonly float[] LumaOther = { BitConverter.Int32BitsToSingle(0x3e9e39b4), BitConverter.Int32BitsToSingle(0x3e95ca4b), BitConverter.Int32BitsToSingle(0x3ecbfc22), BitConverter.Int32BitsToSingle(0x40351eb8) };

    static float InvSqrtNR(float x)
    {
        float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
        float S = x * rs;
        return ((S * rs) + (-3.0f)) * (S * (-0.5f));   // = sqrt-style NR? no: this is the "kv" form: ((S·rs + (−3))·(S·(−0.5)))
    }

    /// <summary>The 4×4 YUV matrix rows (v, u', t') for the sensor type and AsShot neutral (§3.1 of the spec, machine order).</summary>
    public static float[][] YuvMatrix(int sensorType, float[] neutral)
    {
        var tbl = sensorType is 2 or 3 ? LumaAr1335 : LumaOther;
        float a = tbl[0], b = tbl[1], c = tbl[2];
        float invx = 1.0f / neutral[0], invy = 1.0f / neutral[1], invz = 1.0f / neutral[2];
        float s = c + a;
        float u0 = (b * (-b) - s * c) * invx, u1 = ((a - c) * b) * invy, u2 = (s * a - b * (-b)) * invz;
        float t0 = invx * (-b), t1 = invy * s, t2 = invz * (-b);
        float v2 = c * c + (b * b + a * a);
        float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(v2)).ToScalar();
        float S = v2 * rs;
        float kv = ((S * rs + (-3.0f)) * (S * (-0.5f)));
        if (v2 == 0f) kv = 0f;
        float u2n = u2 * u2 + (u1 * u1 + u0 * u0);
        float rsu = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(u2n)).ToScalar();
        float ku = ((rsu * (-0.5f)) * kv) * ((u2n * rsu) * rsu + (-3.0f));
        float t2n = t2 * t2 + (t1 * t1 + t0 * t0);
        float rst = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(t2n)).ToScalar();
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
        int i = (int)l + (BitConverter.SingleToInt32Bits(l) >> 31);   // psrad: −1 for negative l → floor
        float fr = l - (float)i;
        float p2 = ((fr * BitConverter.Int32BitsToSingle(0x3d9fcb52) + BitConverter.Int32BitsToSingle(0x3e677e26)) * fr + BitConverter.Int32BitsToSingle(0x3f322226)) * fr + BitConverter.Int32BitsToSingle(0x3f7ffb19);
        float pw = BitConverter.Int32BitsToSingle((i << 23) + BitConverter.SingleToInt32Bits(p2));
        float sgn = a == 0 ? 0f : BitConverter.Int32BitsToSingle((bits & unchecked((int)0x80000000)) | 0x3f800000);
        return pw * (sgn * 255.0f);
    }

    /// <summary>`ConvertToYUV` on an RGBA float image in place: (Y, 128+U, 128+V, 1).</summary>
    public static void ConvertToYuv(float[] rgba, int w, int h, float[][] M)
    {
        for (int p = 0; p < w * h; p++)
        {
            int o = p * 4; float x = rgba[o], y = rgba[o + 1], z = rgba[o + 2], ww = rgba[o + 3];
            var outv = new float[4];
            for (int k = 0; k < 3; k++)
            {
                float m = (ww * M[k][3] + z * M[k][2]) + (y * M[k][1] + x * M[k][0]);
                outv[k] = GammaEncode(m) + (k == 0 ? 0f : 128.0f);
            }
            rgba[o] = outv[0]; rgba[o + 1] = outv[1]; rgba[o + 2] = outv[2]; rgba[o + 3] = 1.0f;
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

    /// <summary>Lanczos-3 tap table (64 phases × 6 taps), `sinc(x)·sinc(x/3)` normalised, as built per call by the warp.</summary>
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
                    // 180295800: s1 = sinf(x·π); t = s1·3; s2 = sinf(x·π/3); w = (s2·t) / ((x·x)·π²)  (Wine's msvcrt sinf = musl)
                    float s1 = MuslMath.Sinf(x * pi), t = s1 * three, s2 = MuslMath.Sinf(x * pi3);
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

    /// <summary>`ImageWarp&lt;5,0,vec4x32f,LensUndistortCRA&gt;`: dst (w×h) from src via `H` (row-major), CRA LUT, 6-tap Lanczos, fill 0.</summary>
    public static float[] Warp(float[] src, int sw, int sh, int dw, int dh, float[] H, float[] lut, float cx, float cy, float sx, float sy)
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
                if (r2 == 0f) r = 0f; else { float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar(); float S = r2 * rs; r = ((S * rs) + (-3.0f)) * (S * (-0.5f)); }
                int idx = (int)r; if (idx >= 0x1000) idx = 0xfff;
                float lu = lut[idx];
                int px = (int)(((cx + (-2.0f)) + lu * dx) * 64.0f), py = (int)(((cy + (-2.0f)) + dy * lu) * 64.0f);
                int i = px >> 6, fx = px & 63, j = py >> 6, fy = py & 63;
                int o = (y * dw + x) * 4;
                bool interior = i >= 0 && i <= sw - 6 && j >= 0 && j <= sh - 6;
                bool partial = !interior && j < sh && j + 6 > 0 && i < sw && i + 6 > 0;
                if (!interior && !partial) continue;   // fill (0,0,0,0)
                string order = Environment.GetEnvironmentVariable("LUX_WARP_ORDER") ?? "cols"; bool rowsFirst = order == "rows";
                if (order == "swap") { (fx, fy) = (fy, fx); }
                for (int c = 0; c < 4; c++)
                {
                    var C = new float[6];
                    float Px(int rr, int cc) => src[(interior ? j + rr : Math.Clamp(j + rr, 0, sh - 1)) * sw * 4 + (interior ? i + cc : Math.Clamp(i + cc, 0, sw - 1)) * 4 + c];
                    if (order == "prod")
                    {
                        float acc = 0f;
                        for (int rr = 0; rr < 6; rr++) for (int cc = 0; cc < 6; cc++) acc += Px(rr, cc) * (tbl[fy * 6 + rr] * tbl[fx * 6 + cc]);
                        dst[o + c] = acc;
                    }
                    else if (!rowsFirst)
                    {
                        for (int cc = 0; cc < 6; cc++)
                            C[cc] = (Px(5, cc) * tbl[fy * 6 + 5] + Px(4, cc) * tbl[fy * 6 + 4]) + ((Px(3, cc) * tbl[fy * 6 + 3] + Px(2, cc) * tbl[fy * 6 + 2]) + (Px(1, cc) * tbl[fy * 6 + 1] + Px(0, cc) * tbl[fy * 6]));
                        dst[o + c] = (C[5] * tbl[fx * 6 + 5] + C[4] * tbl[fx * 6 + 4]) + ((C[3] * tbl[fx * 6 + 3] + C[2] * tbl[fx * 6 + 2]) + (C[1] * tbl[fx * 6 + 1] + C[0] * tbl[fx * 6]));
                    }
                    else
                    {
                        for (int rr = 0; rr < 6; rr++)
                            C[rr] = (Px(rr, 5) * tbl[fx * 6 + 5] + Px(rr, 4) * tbl[fx * 6 + 4]) + ((Px(rr, 3) * tbl[fx * 6 + 3] + Px(rr, 2) * tbl[fx * 6 + 2]) + (Px(rr, 1) * tbl[fx * 6 + 1] + Px(rr, 0) * tbl[fx * 6]));
                        dst[o + c] = (C[5] * tbl[fy * 6 + 5] + C[4] * tbl[fy * 6 + 4]) + ((C[3] * tbl[fy * 6 + 3] + C[2] * tbl[fy * 6 + 2]) + (C[1] * tbl[fy * 6 + 1] + C[0] * tbl[fy * 6]));
                    }
                }
            }
        return dst;
    }

    /// <summary>Rotation homography `H = (K_B · inv(M)) · inv(K_A)` with `M = R_A·R_Bᵀ` (`FUN_180185030`, product association `(a0·b0 + a1·b1) + a2·b2`).</summary>
    public static float[] RotationHomography(CameraCalib viewA, CameraCalib modB)
    {
        var M = Mat3F.MulABt(viewA.R, modB.R);
        var T = Mat3F.Mul(modB.K, Mat3F.Inverse(M));
        return Mat3F.Mul(T, Mat3F.Inverse(viewA.K));
    }
}
