using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::UpsampleLayer(σ = 12.0f, scale = 2)` = layer 6 of the dense-stereo LayerStack (ctor `18030f0c0`, process = vtable slot 0x08 `18030f270`,
/// spec `a-upsample-layer.md`). Its `+0xa0` image (slot 0x88 → `1803117e0`) is the 4160×3120 metric depth the level-0
/// WarpFields read (`a-resamp.md` §7.1). Chain, 1:1 with `18030f270`:
///   d   = prev.depth (2080×1560 plane-quantised metric depth, StereoLayer&lt;0&gt; slot 0x88)
///   g   = ImageShift&lt;2,vec4x8ui&gt;(guide, {0.5f, 0.5f})            (`1803237a0`: separable 4-tap Catmull-Rom, clamp borders, RNE, saturate)
///   g2  = NearestResize(g, d.width, d.height)                        (`180324600`: 16.16 fixed-point nearest → g2(x,y) = g(2x, 2y))
///   inv = InverseDepth(d)                                            (`18030ac60`: raw `rcpps`)
///   u   = BilateralUpsampleFromCollapse&lt;2,float,vec4x8ui&gt;(inv, guide, g2, σ)   (`18034c7c0` / lambda `18034d000`)
///   +0xa0 = InverseDepth(u); size must equal setSize·scale else "Incorrect output depth size!"
///   +0xd0 = BilateralUpsampleFromCollapse(prev.confidence, guide, g2, σ) when the previous layer has one (never on the L16: DT path off).
/// The guide (`this+0x8`) is `StereoAsyncAPI+0x1f8` = `StereoISP::GetReferenceImage` (state 1, `1804ed3d0`): the reference capture through the
/// DISPLAY ISP (awb manual_color(neutral), tone_mapping/color_correction "default", output srgb, 8-bit) warped by the reference view — not the
/// stereo YUV image. Every float op is a separate SSE rounding (no FMA in cp.dll); `rcpps` is the hardware approximation (CPU-vendor specific).
/// </summary>
public static class DenseUpsampleLayer
{
    public const float Sigma = 12f;   // DAT_180687604
    public const int Scale = 2;

    public sealed class Result
    {
        public float[] Depth = null!; public int W, H;   // this+0xa0
        public float[]? Confidence;                      // this+0xd0
    }

    /// <summary>`UpsampleLayer::slot(0x08)(prev)` (`18030f270`). `setW/setH` = the size given by slot 0x48 (`FUN_18030be70`: (W0/2, H0/2) of the level-0
    /// reference image); the output must be exactly `(setW·scale, setH·scale)`.</summary>
    public static Result Run(float[] prevDepth, int pw, int ph, Rgba8Image guide, int setW, int setH, float sigma = Sigma, int scale = Scale, float[]? prevConfidence = null)
    {
        var shifted = ImageShift(guide, 0.5f, 0.5f);                       // 18030f2b2..f2e8: ImageShift<2,vec4x8ui>(this+0x8, {0.5f,0.5f})
        var g2 = NearestResize(shifted, pw, ph);                            // 18030f2fd: FUN_180324600(g, &g2, d.width, d.height)
        var inv = InverseDepth(prevDepth);                                  // 18030f31c
        var u = BilateralUpsampleFromCollapse(inv, pw, ph, guide, g2, sigma);   // 18030f355
        var depth = InverseDepth(u);                                        // 18030f365
        int W = guide.W, H = guide.H;                                       // output = guide size (loadImage_lambda_1(out, guide+0x10))
        if (W != setW * scale || H != setH * scale) throw new InvalidOperationException("Incorrect output depth size!");   // 18030f39b–f3b7 → 1803103b6
        var r = new Result { Depth = depth, W = W, H = H };
        if (prevConfidence is not null && pw > 0 && ph > 0)                 // 18030f3c3–f42d: prev->slot(0x90) non-empty → +0xd0 (no inverse)
            r.Confidence = BilateralUpsampleFromCollapse(prevConfidence, pw, ph, guide, g2, sigma);
        return r;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // FUN_180323e10: the 4-tap cubic kernel for fractional offset t — Catmull-Rom (a = −0.5) written as
    //   |x| < 1: (9x³ − 15x² + 6)/6,   1 ≤ |x| < 2: (−3x³ + 15x² − 24x + 12)/6,   else 0     (constants 9, −15, 6, −3, 15, −24, 12, 1/6 = 0x3e2aaaab)
    // taps (in order) at distances t+1, t, 1−t, 2−t → source pixels p−1, p, p+1, p+2 for output p (sample at p + t).
    // ------------------------------------------------------------------------------------------------------------------------------
    static readonly float Sixth = BitConverter.Int32BitsToSingle(0x3e2aaaab);
    static float CubicTap(float x)
    {
        float x2 = x * x, x3 = x2 * x;
        if (x < 1f) { float a = x3 * 9f; float b = x2 * (-15f); b = b + 6f; b = b + a; return b * Sixth; }
        if (x < 2f) { float a = x3 * (-3f); float b = x2 * 15f; float c = x * (-24f); c = c + 12f; c = c + b; c = c + a; return c * Sixth; }
        return 0f;
    }
    public static float[] CubicKernel(float t) => new[] { CubicTap(t + 1f), CubicTap(t), CubicTap(1f - t), CubicTap(2f - t) };

    /// <summary>`ImageShift&lt;2,vec4x8ui&gt;(out, src, {sx, sy})` (`1803237a0`, lambda `1803240f0`): out(x,y) = src(x + sx, y + sy) with the 4-tap kernel above,
    /// integer part `floor` (roundss mode 9), separable rows-then-columns in float (`RowCache` `FUN_1800c1cb0/FUN_1800c2760`: bytes → float, clamp-to-edge
    /// in both directions, 2-pixel margins), vertical `acc = r_i·ky_i + acc` from row −1, horizontal `((v₃kx₃ + v₂kx₂) + (v₁kx₁ + v₀kx₀))`, then
    /// `cvtps2dq` (round-nearest-even) → `packssdw` → `packuswb` (saturate 0..255). All four channels.</summary>
    public static Rgba8Image ImageShift(Rgba8Image src, float sx, float sy)
    {
        int W = src.W, H = src.H;
        int ix = (int)MathF.Floor(sx), iy = (int)MathF.Floor(sy);
        float tx = sx - (float)ix, ty = sy - (float)iy;
        var kx = CubicKernel(tx); var ky = CubicKernel(ty);
        var dst = new byte[W * H * 4];
        Parallel.For(0, H, () => new float[(W + 4) * 4], (y, _, rowBuf) =>
        {
            int r0 = y + iy - 1;
            var rows = new int[4]; for (int i = 0; i < 4; i++) { int ry = r0 + i; rows[i] = ry < 0 ? 0 : ry >= H ? H - 1 : ry; }
            // vertical pass over the 4 clamped source rows into the float row buffer (columns −2 .. W+1, clamp-to-edge)
            for (int c = -2; c < W + 2; c++)
            {
                int cx = c < 0 ? 0 : c >= W ? W - 1 : c; int ob = (c + 2) * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    float acc = src.Data[src.Offset(cx, rows[0]) + ch] * ky[0];
                    for (int i = 1; i < 4; i++) acc = src.Data[src.Offset(cx, rows[i]) + ch] * ky[i] + acc;
                    rowBuf[ob + ch] = acc;
                }
            }
            for (int x = 0; x < W; x++)
            {
                int b = (x + ix - 1 + 2) * 4;   // v0 = column x+ix−1
                for (int ch = 0; ch < 4; ch++)
                {
                    float v0 = rowBuf[b + ch], v1 = rowBuf[b + 4 + ch], v2 = rowBuf[b + 8 + ch], v3 = rowBuf[b + 12 + ch];
                    float s = (v3 * kx[3] + v2 * kx[2]) + (v1 * kx[1] + v0 * kx[0]);
                    int q = (int)MathF.Round(s, MidpointRounding.ToEven);            // cvtps2dq
                    dst[(y * W + x) * 4 + ch] = (byte)(q < 0 ? 0 : q > 255 ? 255 : q); // packssdw + packuswb
                }
            }
            return rowBuf;
        }, _ => { });
        return new Rgba8Image(dst, W, H, W);
    }

    /// <summary>`FUN_180324600(src, out, W, H)`: nearest-neighbour resize with 16.16 steps `step = (int)((double)src.dim · 65536.0 / dim)`,
    /// row = yAcc &gt;&gt; 16, col = xAcc &gt;&gt; 16 (accumulated per row / per pixel from 0).</summary>
    public static Rgba8Image NearestResize(Rgba8Image src, int W, int H)
    {
        var dst = new byte[W * H * 4];
        int n = W, m = H;   // min(out.w, W) / min(out.h, H) with out allocated as W×H
        if (n > 0 && m > 0)
        {
            int stepY = (int)((double)src.H * 65536.0 / (double)H);
            int stepX = (int)((double)src.W * 65536.0 / (double)W);
            int yAcc = 0;
            for (int y = 0; y < m; y++)
            {
                int sy = yAcc >> 16; int xAcc = 0;
                for (int x = 0; x < n; x++)
                {
                    int sx = xAcc >> 16;
                    Buffer.BlockCopy(src.Data, src.Offset(sx, sy), dst, (y * W + x) * 4, 4);
                    xAcc += stepX;
                }
                yAcc += stepY;
            }
        }
        return new Rgba8Image(dst, W, H, W);
    }

    /// <summary>`lt::InverseDepth` (`18030ac60`, lambda `18030b0b0`): `rcpps` per lane (scalar tail `rcpss`) — the raw hardware approximation, no Newton step.</summary>
    public static float[] InverseDepth(float[] src)
    {
        var dst = new float[src.Length]; int i = 0;
        for (; i + 4 <= src.Length; i += 4) Sse.Reciprocal(Vector128.Create(src[i], src[i + 1], src[i + 2], src[i + 3])).CopyTo(dst, i);
        for (; i < src.Length; i++) dst[i] = Sse.ReciprocalScalar(Vector128.CreateScalar(src[i])).ToScalar();
        return dst;
    }

    // BilateralUpsampleFromCollapse<2,float,vec4x8ui> constants (18034c7c0 / 18034d000)
    static readonly float Third = BitConverter.Int32BitsToSingle(0x3eaaaaab);        // spatial table {1.0f, 0.33333334f} = 0x3eaaaaab3f800000
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x322bcc77);          // DAT_1806c0430 = 1e-8f
    static readonly float C1 = BitConverter.Int32BitsToSingle(0x3d9fcb52), C2 = BitConverter.Int32BitsToSingle(0x3e677e26),
                          C3 = BitConverter.Int32BitsToSingle(0x3f322226), C4 = BitConverter.Int32BitsToSingle(0x3f7ffb19);   // DAT_1806bdfd8..fe4: 2^f cubic

    /// <summary>The fast `2^t` of `18034d2b0–18034d36e`: `t` clamped to [−126, 128] (minss 128, maxss −126), `n = (int)t + (bits(t) &gt;&gt; 31)`
    /// (cvttps2dq + psrad 31 + paddd), `f = t − (float)n`, `p = ((f·C1 + C2)·f + C3)·f + C4`, result bits = bits(p) + (n &lt;&lt; 23).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Exp2Fast(float t)
    {
        t = t < 128f ? t : 128f;          // minss xmm, 128.0 (second operand on unordered)
        t = t > -126f ? t : -126f;        // maxss xmm, −126.0
        int n = (int)t + (BitConverter.SingleToInt32Bits(t) >> 31);
        float f = t - (float)n;
        float p = ((f * C1 + C2) * f + C3) * f + C4;
        return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(p) + (n << 23));
    }

    /// <summary>`BilateralUpsampleFromCollapse&lt;2,float,vec4x8ui&gt;(out, inv, guide, g2, σ)` (`18034c7c0`, per-pixel lambda `18034d000`; 128×128 tiles are
    /// value-neutral). Per output pixel (x,y): `fx = (float)x − 0.5f`, `fy = (float)y − 0.5f`; low-res taps `ix0 = (x−1)/2`, `ix1 = ix0+1`, `iy0 = (y−1)/2`,
    /// `iy1 = iy0+1` (C division, clamped to [0, w−1]/[0, h−1]); spatial `s = tab[(int)min(|f − 2·i|, 1.0f)]`, `tab = {1, 1/3}`; range
    /// `t = −((d₁² + (d₂² + d₀²))·k)` with `d = guide(x,y) − g2(i,j)` on the RGB lanes, `k = 0.5f/(σ·σ)`; `w = (2^t + 1e-8f)·(sy·sx)`;
    /// accumulation per row j: `V = v₁w₁ + (v₀w₀ + V)`, `S = w₁ + (w₀ + S)`; `out = V / S` (divss).</summary>
    public static float[] BilateralUpsampleFromCollapse(float[] lo, int lw, int lh, Rgba8Image guide, Rgba8Image g2, float sigma)
    {
        float k = 0.5f / (sigma * sigma);   // DAT_180682404 / (σ·σ)
        int W = guide.W, H = guide.H; var outp = new float[W * H];
        int wm1 = lw - 1, hm1 = lh - 1;
        Parallel.For(0, H, y =>
        {
            int iy0 = (y - 1) / 2;                       // (y−1 + ((y−1)>>>31)) >> 1 = C division toward zero
            float fy = (float)y + (-0.5f);
            for (int x = 0; x < W; x++)
            {
                int o = guide.Offset(x, y);
                float hr = guide.Data[o], hg = guide.Data[o + 1], hb = guide.Data[o + 2];
                int ix0 = (x - 1) / 2; float fx = (float)x + (-0.5f);
                int ibx = ix0 < 0 ? 0 : ix0; if (ibx > wm1) ibx = wm1;
                float wx0 = SpatialW(fx, ibx);
                int ix1 = ix0 + 1; if (ix1 < 0) ix1 = 0; if (ix1 > wm1) ix1 = wm1;
                float wx1 = SpatialW(fx, ix1);
                float accV = 0f, accS = 0f;
                for (int j = 0; j < 2; j++)
                {
                    int iy = iy0 + j; if (iy < 0) iy = 0; if (iy > hm1) iy = hm1;
                    float wy = SpatialW(fy, iy);
                    int lrow = iy * lw;
                    // neighbour 0 (ibx)
                    int q = g2.Offset(ibx, iy);
                    float d0 = hr - g2.Data[q], d1 = hg - g2.Data[q + 1], d2 = hb - g2.Data[q + 2];
                    float s = d1 * d1 + (d2 * d2 + d0 * d0);
                    float t = -(s * k);
                    float w0 = (Exp2Fast(t) + Eps) * (wy * wx0);
                    // neighbour 1 (ix1)
                    q = g2.Offset(ix1, iy);
                    d0 = hr - g2.Data[q]; d1 = hg - g2.Data[q + 1]; d2 = hb - g2.Data[q + 2];
                    s = d1 * d1 + (d2 * d2 + d0 * d0);
                    t = -(s * k);
                    float w1 = (Exp2Fast(t) + Eps) * (wy * wx1);
                    float v0 = lo[lrow + ibx] * w0, v1 = lo[lrow + ix1] * w1;
                    accV = v1 + (v0 + accV); accS = w1 + (w0 + accS);
                }
                outp[y * W + x] = accV / accS;
            }
        });
        return outp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float SpatialW(float f, int i)
    {
        float d = MathF.Abs(f - (float)(i * 2));   // subss, andps 0x7fffffff
        d = d < 1f ? d : 1f;                        // minss d, 1.0
        return (int)d == 0 ? 1f : Third;            // cvttss2si → tab[0..1]
    }
}
