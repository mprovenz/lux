using System.Runtime.Intrinsics;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `Demosaicking:collapse2` = `setDemosaicking` case 2 → lambda_26 (`180416360`) → `A::ImageDemosaickFilter&lt;3,float,0,0&gt;::operator()`
/// (`1800c4600`, tile lambdas `1800cc1d0/cc7f0/cce10/cd430`, `ImageConvSeparable2D&lt;6,6,vec4x32f,vec4x32f&gt;` `1800cb4d0` with
/// the pass helpers `1800cbb60` vertical / `1800cbdb0` horizontal). NOT the N×N site collapse (`FUN_1800c37e0`, only
/// collapse4/8): every 2×2 cell is packed into one vec4 [R, G_top, G_bottom, B] (lane sites fixed by `red`), the packed
/// half-res image is filtered per lane with the separable Catmull-Rom (t = ¼) kernel that resamples each colour plane from
/// its own site position to the cell centre — offset-0 lanes use taps (−1..+2) = [−9, 111, 29, −3]/128, offset-1 lanes
/// (−2..+1) = [−3, 29, 111, −9]/128 — vertical pass then horizontal, clamped (replicated) at the image rect; finally
/// `R = c0, G = 0.5·c1 + 0.5·c2, B = c3, A = 1`. Association trees copied from the `mulps/addps` streams: the vertical
/// fast path (rows with y−3 ≥ y0 and y+2 &lt; y1) is `(K5·s2 + (K3·s0 + K2·s−1)) + (K4·s1 + (K1·s−2 + K0·s−3))`, the vertical
/// border path and every horizontal region use `(K5·s2 + K4·s1) + ((K3·s0 + K2·s−1) + (K1·s−2 + K0·s−3))`.
/// </summary>
public static class Demosaic2xCatmull
{
    // 180685cb0..d3f: 111/128, 29/128, −9/128, −3/128 (exact binary fractions)
    const float W0 = 0.8671875f, W1 = 0.2265625f, Wm1 = -0.0703125f, W2 = -0.0234375f;
    // K[i] = coefficient of offset i−3 (i = 0..5), per site offset along the axis
    static readonly float[] K_off0 = { 0f, 0f, Wm1, W0, W1, W2 };
    static readonly float[] K_off1 = { 0f, W2, W1, W0, Wm1, 0f };

    public static Image<Vec4F> Run(float[] src, int stride, int offset, int w, int h, int redX, int redY) => Run(src, stride, offset, w, h, redX, redY, new RectI(0, 0, w, h));

    /// <summary>
    /// <paramref name="ext"/> = the source image's available extents relative to the view origin (Lumen `Image` rect fields: x0 ≤ 0, x1 ≥ w),
    /// even-aligned inward: the packed image covers the whole extent and the Catmull taps clamp at ITS edge, i.e. the kernel reads real
    /// data beyond the stage region when the previous stage produced it (runner rect dump 2026-08-27: demosaic region 1166 of a 1170
    /// crosstalk output — the last two output columns/rows use the extra data).
    /// </summary>
    public static Image<Vec4F> Run(float[] src, int stride, int offset, int w, int h, int redX, int redY, RectI ext)
    {
        if (((w | h) & 1) != 0) throw new InvalidOperationException("invalid bayer image size!");
        if ((uint)(redX | redY) >= 2) throw new InvalidOperationException("non-bayer red coordinate!");
        int ex0 = (ext.X0 + 1) & ~1, ey0 = (ext.Y0 + 1) & ~1, ex1 = ext.X1 & ~1, ey1 = ext.Y1 & ~1;   // even-aligned inward (CFA phase kept)
        if (ex0 > 0 || ey0 > 0 || ex1 < w || ey1 < h) throw new ArgumentException("extents must contain the view");
        int W = w / 2, H = h / 2;
        var dst = new Image<Vec4F>(W, H);
        if (W <= 0 || H <= 0) return dst;
        int PW = (ex1 - ex0) / 2, PH = (ey1 - ey0) / 2, VX = -ex0 / 2, VY = -ey0 / 2;   // packed extent size and the view's cell origin inside it
        offset += ey0 * stride + ex0;
        // lane sites within the cell: lane0 = red, lane1 = the G in row 0, lane2 = the G in row 1, lane3 = blue
        int g0 = (redX + redY + 1) & 1;
        int[] lx = { redX, g0, 1 - g0, 1 - redX }, ly = { redY, 0, 1, 1 - redY };
        var P = new Vector128<float>[PW * PH];
        for (int Y = 0; Y < PH; Y++)
        {
            int r0 = offset + (2 * Y) * stride, r1 = r0 + stride;
            for (int X = 0; X < PW; X++)
            {
                int c = 2 * X;
                P[Y * PW + X] = Vector128.Create(
                    src[(ly[0] == 0 ? r0 : r1) + c + lx[0]], src[r0 + c + lx[1]], src[r1 + c + lx[2]], src[(ly[3] == 0 ? r0 : r1) + c + lx[3]]);
            }
        }
        // per-lane kernels: vertical by the lane's row offset, horizontal by its column offset
        var Kv = new Vector128<float>[6]; var Kh = new Vector128<float>[6];
        for (int i = 0; i < 6; i++)
        {
            Kv[i] = Vector128.Create((ly[0] == 0 ? K_off0 : K_off1)[i], (ly[1] == 0 ? K_off0 : K_off1)[i], (ly[2] == 0 ? K_off0 : K_off1)[i], (ly[3] == 0 ? K_off0 : K_off1)[i]);
            Kh[i] = Vector128.Create((lx[0] == 0 ? K_off0 : K_off1)[i], (lx[1] == 0 ? K_off0 : K_off1)[i], (lx[2] == 0 ? K_off0 : K_off1)[i], (lx[3] == 0 ? K_off0 : K_off1)[i]);
        }
        var T = new Vector128<float>[PW * H];   // vertical pass: the view's rows, all packed columns of the extent
        for (int Y = 0; Y < H; Y++)
        {
            int PY = Y + VY;
            bool fast = PY - 3 >= 0 && PY + 2 < PH;   // rect-based (parent extents): rows with y−3 ≥ y0 and y+2 < y1
            int[] rows = new int[6];
            for (int i = 0; i < 6; i++) rows[i] = Math.Clamp(PY + i - 3, 0, PH - 1);
            for (int X = 0; X < PW; X++)
            {
                Vector128<float> s0 = P[rows[0] * PW + X], s1 = P[rows[1] * PW + X], s2 = P[rows[2] * PW + X], s3 = P[rows[3] * PW + X], s4 = P[rows[4] * PW + X], s5 = P[rows[5] * PW + X];
                Vector128<float> a = Kv[1] * s1 + Kv[0] * s0;   // K1 s[y−2] + K0 s[y−3]
                Vector128<float> b = Kv[3] * s3 + Kv[2] * s2;   // K3 s[y] + K2 s[y−1]
                if (fast)
                {
                    Vector128<float> c = Kv[4] * s4 + a;
                    Vector128<float> d = Kv[5] * s5 + b;
                    T[Y * PW + X] = d + c;
                }
                else
                {
                    Vector128<float> ba = b + a;
                    Vector128<float> e = Kv[5] * s5 + Kv[4] * s4;
                    T[Y * PW + X] = e + ba;
                }
            }
        }
        var m0 = Vector128.Create(1f, 0.5f, 0f, 0f); var m1 = Vector128.Create(0f, 0.5f, 1f, 0f);
        for (int Y = 0; Y < H; Y++)
        {
            int rb = Y * PW, ob = Y * W;
            for (int X = 0; X < W; X++)
            {
                int PX = X + VX;
                Vector128<float> t0 = T[rb + Math.Clamp(PX - 3, 0, PW - 1)], t1 = T[rb + Math.Clamp(PX - 2, 0, PW - 1)], t2 = T[rb + Math.Clamp(PX - 1, 0, PW - 1)];
                Vector128<float> t3 = T[rb + PX], t4 = T[rb + Math.Clamp(PX + 1, 0, PW - 1)], t5 = T[rb + Math.Clamp(PX + 2, 0, PW - 1)];
                Vector128<float> c = (Kh[5] * t5 + Kh[4] * t4) + ((Kh[3] * t3 + Kh[2] * t2) + (Kh[1] * t1 + Kh[0] * t0));
                // 1800cc570: c*[1,.5,0,0] + psrldq(c,4)*[0,.5,1,0]; lane3 := 1
                Vector128<float> sh = Vector128.Create(c.GetElement(1), c.GetElement(2), c.GetElement(3), 0f);
                Vector128<float> o = sh * m1 + c * m0;
                dst.Data[ob + X] = new Vec4F(o.GetElement(0), o.GetElement(1), o.GetElement(2), 1f);
            }
        }
        return dst;
    }
}

/// <summary>Bayer-domain stage `Demosaicking:collapse2` (`setDemosaicking` case 2: slot 0x200000009 = pad 9, align 2, scale ½);
/// the STD plane is nearest-resized to the half size as for the other collapse types.</summary>
public sealed class Demosaic2xCatmullStage : IStage
{
    public StageName Stage => StageName.Demosaicking;
    public string TypeString => "collapse2";
    public StageMeta Meta => new(9, 2, 0.5f);
    public void Apply(IspPayload p)
    {
        var src = p.BayerFloat ?? throw new InvalidOperationException("collapse2 demosaic needs the float Bayer image (BayerToFloat stage)");
        var red = p.Context.Module.SensorBayerRedOverride;
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        int offset = src.Offset + (abs.Y0 - src.Rect.Y0) * src.Stride + (abs.X0 - src.Rect.X0);
        var ext = new RectI(src.Rect.X0 - abs.X0, src.Rect.Y0 - abs.Y0, src.Rect.X1 - abs.X0, src.Rect.Y1 - abs.Y0);   // the source's extents around the region
        var o = Demosaic2xCatmull.Run(src.Data, src.Stride, offset, abs.Width, abs.Height, red?.X ?? 0, red?.Y ?? 0, ext);
        p.Rgb = new Image<Vec4F>(new RectI(abs.X0 >> 1, abs.Y0 >> 1, (abs.X0 >> 1) + o.Width, (abs.Y0 >> 1) + o.Height), o.Data, o.Stride, 0);
        if (p.Std is not null)
        {
            var sv = p.Std.Rect.Intersect(abs);
            int so = p.Std.Offset + (sv.Y0 - p.Std.Rect.Y0) * p.Std.Stride + (sv.X0 - p.Std.Rect.X0);
            int ow = sv.Width >> 1, oh = sv.Height >> 1;
            var r = CollapseDemosaicKernel.NearestResize(p.Std.Data, p.Std.Stride, so, sv.Width, sv.Height, ow, oh);
            p.Std = new Image<float>(new RectI(sv.X0 >> 1, sv.Y0 >> 1, (sv.X0 >> 1) + ow, (sv.Y0 >> 1) + oh), r, ow, 0);
        }
    }
}
