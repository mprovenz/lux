using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `Demosaicking:collapse4/8` (`setDemosaicking` lambdas 27/28 → `FUN_1800c37e0(out, in, N, red)`; collapse2 is NOT this — see Demosaic2xCatmull): every N×N
/// block of the float Bayer image becomes one RGBA pixel = (mean R, (mean G1 + mean G2)·½, mean B, 1) over the
/// block's sites (partial blocks at the right/bottom use what is there). Sums follow the SSE order: per site and
/// per sample row, four lanes over column groups of four (lane k = columns 2k, 2k+2 … within the group), combined as
/// `(l2 + l0) + (l3 + l1)`, the last 1–4 samples added sequentially; rows fewer than 5 samples wide are summed
/// sequentially. Means use `rcpss(count)`. The companion image at payload +0x40 is nearest-resized
/// (`FUN_1801d6120`, 16.16 steps from `(int)(w·65536/w')`) to the collapsed size (lambda_23 `180415e20`).
/// </summary>
public static class CollapseDemosaicKernel
{
    public static Image<Vec4F> Run(float[] src, int stride, int offset, int w, int h, int n, int redX, int redY)
    {
        if (n < 2 || (n & 1) != 0) throw new InvalidOperationException("Collapse block size must be even and at least 2!");
        if (redX < 0 || redY < 0) return RunMono(src, stride, offset, w, h, n);   // no CFA: see RunMono
        int ow = w / n, oh = h / n;
        var dst = new Image<Vec4F>(ow, oh);
        int rx = redX, ry = redY;
        float Sum(int by, int bx, int py, int px, int cols2, int rows2)
        {
            float total = 0f;
            for (int j = 0; j < rows2; j++)
            {
                int row = by * n + py + 2 * j; int baseIdx = offset + row * stride + bx * n + px;
                int tail = cols2 & 3; if (tail == 0) tail = 4;
                int simd = cols2 < 5 ? 0 : cols2 - tail;
                if (simd > 0)
                {
                    float l0 = total, l1 = 0f, l2 = 0f, l3 = 0f;
                    for (int i = 0; i < simd; i += 4) { l0 += src[baseIdx + 2 * i]; l1 += src[baseIdx + 2 * (i + 1)]; l2 += src[baseIdx + 2 * (i + 2)]; l3 += src[baseIdx + 2 * (i + 3)]; }
                    total = (l2 + l0) + (l3 + l1);
                }
                for (int i = simd; i < cols2; i++) total += src[baseIdx + 2 * i];
            }
            return total;
        }
        for (int by = 0; by < oh; by++)
            for (int bx = 0; bx < ow; bx++)
            {
                int cols = Math.Min(n, w - bx * n) >> 1, rows = Math.Min(n, h - by * n) >> 1;
                float sR = 0f, sG1 = 0f, sG2 = 0f, sB = 0f;
                if (rows >= 1)
                {
                    sR = Sum(by, bx, ry, rx, cols, rows); sG1 = Sum(by, bx, ry, 1 - rx, cols, rows);
                    sG2 = Sum(by, bx, 1 - ry, rx, cols, rows); sB = Sum(by, bx, 1 - ry, 1 - rx, cols, rows);
                }
                float inv = Sse.ReciprocalScalar(Vector128.CreateScalar((float)(rows * cols))).ToScalar();
                float tR = inv * sR, tG1 = inv * sG1, tG2 = inv * sG2, tB = inv * sB;
                dst.Data[by * ow + bx] = new Vec4F(tG1 * 0f + tR * 1f, tG2 * 0.5f + tG1 * 0.5f, tB * 1f + tG2 * 0f, 1f);
            }
        return dst;
    }

    /// <summary>The no-CFA (monochrome) collapse: every site is luminance, so a block is its plain n×n mean, written to
    /// all three channels. Lumen never demosaics the mono module on its own (its mono path is the fusion one, which ISPs
    /// the reference colour frame), so this is Lux-defined behaviour for the lens-frames render at config levels 2–4:
    /// the colour kernels index sites by the red position, and the mono sentinel (−1,−1) has none.</summary>
    public static Image<Vec4F> RunMono(float[] src, int stride, int offset, int w, int h, int n)
    {
        if (n < 2 || (n & 1) != 0) throw new InvalidOperationException("Collapse block size must be even and at least 2!");
        int ow = w / n, oh = h / n;
        var dst = new Image<Vec4F>(ow, oh);
        float inv = 1f / (n * n);
        for (int by = 0; by < oh; by++)
            for (int bx = 0; bx < ow; bx++)
            {
                float total = 0f;
                for (int j = 0; j < n; j++)
                {
                    int baseIdx = offset + (by * n + j) * stride + bx * n;
                    for (int i = 0; i < n; i++) total += src[baseIdx + i];
                }
                float m = total * inv;
                dst.Data[by * ow + bx] = new Vec4F(m, m, m, 1f);
            }
        return dst;
    }

    /// <summary>`FUN_1801d6120(in, out, w', h')`: nearest resize, x step `(int)((w·65536.0)/w')`, y step
    /// `(int)((h·65536.0)/h')` accumulated in 16.16.</summary>
    public static T[] NearestResize<T>(T[] src, int stride, int offset, int w, int h, int ow, int oh) where T : unmanaged
    {
        var dst = new T[ow * oh];
        int cw = Math.Min(ow, ow), ch = Math.Min(oh, oh);
        double hh = (double)h * 65536.0;
        int xstep = (int)(((double)w * 65536.0) / (double)ow);
        int yacc = 0;
        for (int y = 0; y < ch; y++)
        {
            int srow = yacc >> 16; int xacc = 0;
            for (int x = 0; x < cw; x++) { dst[y * ow + x] = src[offset + srow * stride + (xacc >> 16)]; xacc += xstep; }
            yacc += (int)(hh / (double)oh);
        }
        return dst;
    }
}

/// <summary>Bayer-domain stage `Demosaicking:collapseN` (N = 2, 4, 8): block means → RGBA, then the STD plane is
/// nearest-resized to the collapsed size.</summary>
public sealed class CollapseDemosaicStage : IStage
{
    private readonly int _n;
    public CollapseDemosaicStage(int n) { _n = n; }
    public StageName Stage => StageName.Demosaicking;
    public string TypeString => $"collapse{_n}";
    public StageMeta Meta => new(9, 2, 1f / _n);   // setDemosaicking cases 2/3/4: 0x200000009 + scale 0.5/0.25/0.125
    public void Apply(IspPayload p)
    {
        var src = p.BayerFloat ?? throw new InvalidOperationException("collapse demosaic needs the float Bayer image (BayerToFloat stage)");
        var red = p.Context.Module.SensorBayerRedOverride;
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        int offset = src.Offset + (abs.Y0 - src.Rect.Y0) * src.Stride + (abs.X0 - src.Rect.X0);
        var o = CollapseDemosaicKernel.Run(src.Data, src.Stride, offset, abs.Width, abs.Height, _n, red?.X ?? 0, red?.Y ?? 0);
        int k = _n == 2 ? 1 : _n == 4 ? 2 : 3;
        p.Rgb = new Image<Vec4F>(new RectI(abs.X0 >> k, abs.Y0 >> k, (abs.X0 >> k) + o.Width, (abs.Y0 >> k) + o.Height), o.Data, o.Stride, 0);
        if (p.Std is not null)
        {
            var sv = p.Std.Rect.Intersect(abs);
            int so = p.Std.Offset + (sv.Y0 - p.Std.Rect.Y0) * p.Std.Stride + (sv.X0 - p.Std.Rect.X0);
            int ow = sv.Width >> k, oh = sv.Height >> k;
            var r = CollapseDemosaicKernel.NearestResize(p.Std.Data, p.Std.Stride, so, sv.Width, sv.Height, ow, oh);
            p.Std = new Image<float>(new RectI(sv.X0 >> k, sv.Y0 >> k, (sv.X0 >> k) + ow, (sv.Y0 >> k) + oh), r, ow, 0);
        }
    }
}
