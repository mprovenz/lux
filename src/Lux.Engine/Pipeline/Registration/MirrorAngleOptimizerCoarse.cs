using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The image helpers of the coarse `lt::MirrorAngleOptimizer` (spec `a79436a17a0f96822.md` §B): 1/8 Lanczos-3
/// resampler (`ImageResample&lt;5,vec4x8ui&gt;`), 16.16 bilinear resampler (`A::BilinearResample&lt;vec4x8ui&gt;`), nearest depth resize
/// (`FUN_1801d6120`), float conversions, the tiled running-sum 5×5 box filter (`ImageBoxFilter&lt;vec4x32f&gt;`), the Sobel L1 magnitude
/// (`ImageConvSeparable2D&lt;3,3,float,float&gt;`) and the ×0.125 calibration scale.
/// </summary>
public static class CoarseImageHelpers
{
    static float Sinf(float x) => MuslMath.Sinf(x);

    /// <summary>64-phase × 6-tap Lanczos-3 table (k = −2..3), normalised by the sequential sum (same as the stereo warp table).</summary>
    public static float[] Taps()
    {
        var t = new float[64 * 6];
        float pi = BitConverter.Int32BitsToSingle(0x40490fdb), pi3 = BitConverter.Int32BitsToSingle(0x3f860a92), pi2 = BitConverter.Int32BitsToSingle(0x411de9e7);
        for (int p = 0; p < 64; p++)
        {
            float f = (float)p * 0.015625f, sum = 0f;
            for (int k = -2; k <= 3; k++)
            {
                float x = f - (float)k, w;
                if (x == 0f) w = 1f;
                else if (MathF.Abs(x) < 3f) { float s1 = Sinf(x * pi); float tt = s1 * 3f; float s2 = Sinf(x * pi3); w = (s2 * tt) / ((x * x) * pi2); }
                else w = 0f;
                t[p * 6 + k + 2] = w; sum += w;
            }
            float inv = 1f / sum; for (int k = 0; k < 6; k++) t[p * 6 + k] *= inv;
        }
        return t;
    }

    /// <summary>`FUN_1802955f0`: reference image → (W8,H8) with the 6-tap table, 16.16 coordinates, replicate borders, RNE + saturate.</summary>
    public static Rgba8Image Resample5(Rgba8Image src, int W8, int H8)
    {
        if (W8 <= 0 || H8 <= 0) throw new InvalidOperationException("invalid size!");
        var taps = Taps();
        int sx = (int)((double)src.W / (double)W8 * 65536.0), sy = (int)((double)src.H / (double)H8 * 65536.0);
        var dst = new Rgba8Image(new byte[W8 * H8 * 4], W8, H8, W8);
        // horizontal pass cache per source row
        var hrow = new float[src.H][];
        float[] HRow(int row)
        {
            if (hrow[row] is not null) return hrow[row];
            var r = new float[W8 * 4];
            for (int x = 0; x < W8; x++)
            {
                int xf = x * sx, xi = xf >> 16, px = (xf >> 10) & 63;
                for (int c = 0; c < 4; c++)
                {
                    float P(int k) => src.Data[src.Offset(Math.Clamp(xi - 2 + k, 0, src.W - 1), row) + c];
                    float t0 = taps[px * 6], t1 = taps[px * 6 + 1], t2 = taps[px * 6 + 2], t3 = taps[px * 6 + 3], t4 = taps[px * 6 + 4], t5 = taps[px * 6 + 5];
                    r[x * 4 + c] = ((P(5) * t5) + (P(4) * t4)) + (((P(3) * t3) + (P(2) * t2)) + ((P(1) * t1) + (P(0) * t0)));
                }
            }
            hrow[row] = r; return r;
        }
        for (int y = 0; y < H8; y++)
        {
            int yf = y * sy, yi = yf >> 16, py = (yf >> 10) & 63;
            var R = new float[6][]; for (int k = 0; k < 6; k++) R[k] = HRow(Math.Clamp(yi - 2 + k, 0, src.H - 1));
            for (int x = 0; x < W8; x++)
                for (int c = 0; c < 4; c++)
                {
                    float t0 = taps[py * 6], t1 = taps[py * 6 + 1], t2 = taps[py * 6 + 2], t3 = taps[py * 6 + 3], t4 = taps[py * 6 + 4], t5 = taps[py * 6 + 5];
                    int i = x * 4 + c;
                    float V = ((R[5][i] * t5) + (R[4][i] * t4)) + (((R[3][i] * t3) + (R[2][i] * t2)) + ((R[1][i] * t1) + (R[0][i] * t0)));
                    int q = (int)MathF.Round(V, MidpointRounding.ToEven);
                    dst.Data[(y * W8 + x) * 4 + c] = (byte)Math.Clamp(q, 0, 255);
                }
        }
        return dst;
    }

    /// <summary>`A::BilinearResample&lt;vec4x8ui&gt;` (`1800fb210`): 16.16 fixed point on ×32 int16 values, `pmulhw` lerps, per 256×256 tile.</summary>
    public static Rgba8Image BilinearResample(Rgba8Image src, int W8, int H8)
    {
        if (W8 <= 0 || H8 <= 0) throw new InvalidOperationException("invalid size!");
        int sxU = (int)(uint)((double)src.W / (double)W8 * 65536.0), sy = (int)((double)src.H / (double)H8 * 65536.0);
        var dst = new Rgba8Image(new byte[W8 * H8 * 4], W8, H8, W8);
        int nx = W8 / 256 + (256 < 2 * (W8 % 256) ? 1 : 0), ny = H8 / 256 + (256 < 2 * (H8 % 256) ? 1 : 0); if (nx < 1) nx = 1; if (ny < 1) ny = 1;
        int fxStep = (sxU & 0xfffe) >> 1;
        for (int tj = 0; tj < ny; tj++)
            for (int ti = 0; ti < nx; ti++)
            {
                int x0t = 256 * ti, x1t = Math.Min(W8, x0t + 256 * (ti == nx - 1 ? 2 : 1)), y0t = 256 * tj, y1t = Math.Min(H8, y0t + 256 * (tj == ny - 1 ? 2 : 1));
                short[] HRow(int row)
                {
                    row = Math.Clamp(row, 0, src.H - 1);
                    var r = new short[(x1t - x0t) * 4];
                    int xf = x0t * sxU, fx = (xf & 0xfffe) >> 1;
                    for (int x = x0t; x < x1t; x++)
                    {
                        int xs = xf >> 16;
                        for (int c = 0; c < 4; c++)
                        {
                            int v;
                            if (xf < 0) v = src.Data[src.Offset(0, row) + c] << 5;
                            else if (xf >= (src.W - 1) << 16) v = src.Data[src.Offset(src.W - 1, row) + c] << 5;
                            else
                            {
                                int v0 = src.Data[src.Offset(xs, row) + c] << 5, v1 = src.Data[src.Offset(xs + 1, row) + c] << 5;
                                v = v0 + ((short)((v1 - v0) * 2) * fx >> 16);
                            }
                            r[(x - x0t) * 4 + c] = (short)v;
                        }
                        xf += sxU; fx = (fx + fxStep) & 0x7fff;
                    }
                    return r;
                }
                for (int y = y0t; y < y1t; y++)
                {
                    int yf = y * sy, ys = yf >> 16, fy = (yf & 0xfffe) >> 1;
                    var r0 = HRow(ys); var r1 = HRow(ys + 1);
                    for (int x = x0t; x < x1t; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            int i = (x - x0t) * 4 + c;
                            int o = r0[i] + ((short)((r1[i] - r0[i]) * 2) * fy >> 16);
                            o >>= 5;
                            dst.Data[(y * W8 + x) * 4 + c] = (byte)Math.Clamp(o, 0, 255);
                        }
                }
            }
        return dst;
    }

    /// <summary>`FUN_1801d6120`: nearest resize of a float image, 16.16 steps from double, truncating.</summary>
    public static float[] NearestResize(float[] src, int W, int H, int W8, int H8)
    {
        var dst = new float[W8 * H8];
        int xstep = (int)(((double)W * 65536.0) / (double)W8), ystep = (int)(((double)H * 65536.0) / (double)H8);
        int yf = 0;
        for (int y = 0; y < H8; y++)
        {
            int srow = (yf >> 16) * W, xf = 0;
            for (int x = 0; x < W8; x++) { dst[y * W8 + x] = src[srow + (xf >> 16)]; xf += xstep; }
            yf += ystep;
        }
        return dst;
    }

    public static float[] ToFloat4(Rgba8Image img) { var o = new float[img.W * img.H * 4]; for (int y = 0; y < img.H; y++) for (int x = 0; x < img.W; x++) for (int c = 0; c < 4; c++) o[(y * img.W + x) * 4 + c] = img.Data[img.Offset(x, y) + c]; return o; }
    public static float[] Channel0(Rgba8Image img) { var o = new float[img.W * img.H]; for (int y = 0; y < img.H; y++) for (int x = 0; x < img.W; x++) o[y * img.W + x] = img.Data[img.Offset(x, y)]; return o; }

    /// <summary>`ImageBoxFilter&lt;vec4x32f&gt;` 5×5 (`1800eae10`, lambda `1800eedc0`): per 256×256 tile running column sums, in-image count normalisation.</summary>
    public static float[] Box5(float[] src, int W, int H)
    {
        const int kw = 5, kh = 5, hw = 2, hh = 2;
        var dst = new float[W * H * 4];
        int nx = W / 256 + (256 < 2 * (W % 256) ? 1 : 0), ny = H / 256 + (256 < 2 * (H % 256) ? 1 : 0); if (nx < 1) nx = 1; if (ny < 1) ny = 1;
        for (int tj = 0; tj < ny; tj++)
            for (int ti = 0; ti < nx; ti++)
            {
                int x0t = 256 * ti, x1t = Math.Min(W, x0t + 256 * (ti == nx - 1 ? 2 : 1)), y0t = 256 * tj, y1t = Math.Min(H, y0t + 256 * (tj == ny - 1 ? 2 : 1));
                int tw = x1t - x0t, nC = tw + kw;
                var C = new float[nC * 4]; float rows = 0f;
                int Cx(int i) => x0t - hw - 1 + i;
                bool ValidCol(int i) { int cx = Cx(i); return cx >= 0 && cx < W; }
                void AddRow(int ys, float sign)
                {
                    for (int i = 0; i < nC; i++) { if (!ValidCol(i)) continue; int sidx = (ys * W + Cx(i)) * 4; for (int c = 0; c < 4; c++) C[i * 4 + c] = sign > 0 ? C[i * 4 + c] + src[sidx + c] : C[i * 4 + c] - src[sidx + c]; }
                }
                for (int r = 0; r < kh; r++) { int ys = y0t - 1 - hh + r; if (ys >= 0 && ys < H) { AddRow(ys, +1f); rows += 1.0f; } }
                for (int y = y0t; y < y1t; y++)
                {
                    int yn = y - 1 + kh - hh, yo = y - 1 - hh; bool vn = yn >= 0 && yn < H, vo = yo >= 0 && yo < H;
                    if (vn && vo) { for (int i = 0; i < nC; i++) { if (!ValidCol(i)) continue; int a = (yn * W + Cx(i)) * 4, b = (yo * W + Cx(i)) * 4; for (int c = 0; c < 4; c++) C[i * 4 + c] = (src[a + c] - src[b + c]) + C[i * 4 + c]; } }
                    else if (vo) { AddRow(yo, -1f); rows += -1.0f; }
                    else if (vn) { AddRow(yn, +1f); rows += 1.0f; }
                    // initial window = the window of x0t − 1: C[i] for i in [ilo, min(kw, ihi)) with valid columns, in order
                    var S = new float[4];
                    int ilo = 0; while (ilo < nC && !ValidCol(ilo)) ilo++;
                    int ihi = nC; while (ihi > 0 && !ValidCol(ihi - 1)) ihi--;
                    int iEnd = Math.Min(kw, ihi); int n = Math.Max(0, iEnd - ilo); int head = n & 3;
                    for (int i = ilo; i < ilo + head; i++) for (int c = 0; c < 4; c++) S[c] = S[c] + C[i * 4 + c];
                    for (int i = ilo + head; i + 3 < iEnd; i += 4) for (int c = 0; c < 4; c++) S[c] = (((S[c] + C[i * 4 + c]) + C[(i + 1) * 4 + c]) + C[(i + 2) * 4 + c]) + C[(i + 3) * 4 + c];
                    float normI = 1.0f / (rows * (float)kw);
                    for (int x = 0; x < tw; x++)
                    {
                        int L = Math.Max(x0t - hw + x, 0), R = Math.Min(x0t - hw + x + kw, W);
                        bool leftClip = x0t - hw + x < 0, rightClip = x0t - hw + x + kw > W;
                        int o = ((y * W) + x0t + x) * 4;
                        if (leftClip) { float norm = 1.0f / ((float)(R - L) * rows); for (int c = 0; c < 4; c++) { S[c] = S[c] + C[(x + kw) * 4 + c]; dst[o + c] = norm * S[c]; } }
                        else if (rightClip) { float norm = 1.0f / ((float)(R - L) * rows); for (int c = 0; c < 4; c++) { S[c] = S[c] - C[x * 4 + c]; dst[o + c] = norm * S[c]; } }
                        else { for (int c = 0; c < 4; c++) { S[c] = S[c] + (C[(x + kw) * 4 + c] - C[x * 4 + c]); dst[o + c] = S[c] * normI; } }
                    }
                }
            }
        return dst;
    }

    public static float[] Sub4(float[] a, float[] b) { var o = new float[a.Length]; for (int i = 0; i < a.Length; i++) o[i] = a[i] - b[i]; return o; }

    /// <summary>`FUN_180292310` + `FUN_180295220`: |Sobel_y| + |Sobel_x| of the float channel-0 image (separable 3-tap, replicate borders).</summary>
    public static float[] SobelL1(float[] S, int W, int H)
    {
        float[] Conv(float[] v, float[] h)
        {
            var T = new float[W * H]; var o = new float[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int ym = Math.Max(y - 1, 0), yp = Math.Min(y + 1, H - 1);
                    T[y * W + x] = ((v[0] * S[ym * W + x]) + (v[1] * S[y * W + x])) + (v[2] * S[yp * W + x]);
                }
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int xm = Math.Max(x - 1, 0), xp = Math.Min(x + 1, W - 1);
                    o[y * W + x] = ((h[0] * T[y * W + xm]) + (h[1] * T[y * W + x])) + (h[2] * T[y * W + xp]);
                }
            return o;
        }
        var G1 = Conv(new[] { -1f, 0f, 1f }, new[] { 1f, 2f, 1f });
        var G2 = Conv(new[] { 1f, 2f, 1f }, new[] { -1f, 0f, 1f });
        var r = new float[W * H]; for (int i = 0; i < r.Length; i++) r[i] = MathF.Abs(G2[i]) + MathF.Abs(G1[i]);
        return r;
    }
}

/// <summary>
/// `lt::MirrorAngleOptimizer::optimize` (`1802929c0`, spec `a4d1c9bd686bab26e.md` §3): the coarse mirror-angle search on the dense depth —
/// 13 θ nodes (±0.6°) × 41 principal-point shifts along the θ-flow (×8 on the 1/8 grid), z-buffered reprojection cost of the 1/8 colour
/// (high-pass) + gradient images, `sel = cost[centre]·0.95 ≥ cost[best] ? best : centre`.
/// </summary>
public static class MirrorAngleOptimizerCoarse
{
    public sealed class Context
    {
        public Rgba8Image Ref8; public float[] Colour8 = null!, Scalar8 = null!, Depth8 = null!; public int W8, H8;
        public CalibDataFull RefCam8 = null!; public float Z; public int FrameW = 4160, FrameH = 3120;
    }

    /// <summary>`FUN_180291c40`: the optimizer's reference-side images from the reference stereo image and the layer-5 depth.</summary>
    public static Context Build(Rgba8Image refImg, float[] depth, int depthW, int depthH, CalibDataFull refCam8, float Z)
    {
        int W8 = (int)((float)refImg.W * 0.125f), H8 = (int)((float)refImg.H * 0.125f);
        var c = new Context { W8 = W8, H8 = H8, Z = Z };
        c.Ref8 = CoarseImageHelpers.Resample5(refImg, W8, H8);
        var F = CoarseImageHelpers.ToFloat4(c.Ref8);
        c.Colour8 = CoarseImageHelpers.Sub4(F, CoarseImageHelpers.Box5(F, W8, H8));
        c.Scalar8 = CoarseImageHelpers.SobelL1(CoarseImageHelpers.Channel0(c.Ref8), W8, H8);
        c.RefCam8 = refCam8;   // this+0xc0 = FUN_180308670(refCam, 0.125) — the caller scales
        c.Depth8 = CoarseImageHelpers.NearestResize(depth, depthW, depthH, W8, H8);
        return c;
    }

    static (float U, float V) Proj(float[] M, float p0, float p1, float p2, float p3)
    {
        var q = new float[3];
        for (int l = 0; l < 3; l++) q[l] = ((p3 * M[12 + l] + p2 * M[8 + l]) + (p1 * M[4 + l] + p0 * M[l]));
        float w = 1.0f / q[2];
        return (w * q[0], w * q[1]);
    }

    public sealed class Result { public double Theta; public float Cx, Cy; public int Sel, Best; public float[] Cost = null!; public CalibData Written = null!; }

    /// <summary>Run the search: `camBase` = the module's CURRENT slot (full-res K), `pipe` = its view pose, `img` = the module's canvas image.</summary>
    public static Result Optimize(Context ctx, MirrorSystem sys, CalibDataFull camBase, ViewPose pipe, Rgba8Image img, double theta0)
    {
        var baseBasic = camBase.Basic();
        // flow on the 1/8 reference
        var M1 = Mat4D.FlowMatrix(MirrorPose.NodePose(sys, baseBasic, theta0, 0.0), ctx.RefCam8.Basic());
        var M2 = Mat4D.FlowMatrix(MirrorPose.NodePose(sys, baseBasic, theta0 + 2.0, 0.0), ctx.RefCam8.Basic());
        float cx0 = baseBasic.K[2], cy0 = baseBasic.K[5], Z = ctx.Z;
        var p1 = Proj(M1, 1f * (cx0 * Z), 1f * (cy0 * Z), Z, 1f); var p2 = Proj(M2, 1f * (cx0 * Z), 1f * (cy0 * Z), Z, 1f);
        float dx = p2.U - p1.U, dy = p2.V - p1.V;
        float m = MathF.Abs(dy); if (!(MathF.Abs(dx) <= m)) m = MathF.Abs(dx);
        float inv = 1.0f / m;
        float fx = (dx * inv) * 8.0f, fy = (dy * inv) * (-8.0f);
        int nNodes = 13 * 41; var thetas = new double[nNodes]; var cxs = new float[nNodes]; var cys = new float[nNodes];
        for (int k = 0; k < 13; k++)
            for (int mi = 0; mi < 41; mi++)
            {
                int i = k * 41 + mi; double kd = -6.0 + k; int mm = mi - 20;
                thetas[i] = kd * 0.10000000149011612 + theta0; cxs[i] = fy * (float)mm; cys[i] = (float)mm * fx;
            }
        // module images
        int Wm8 = (int)((float)img.W * 0.125f), Hm8 = (int)((float)img.H * 0.125f);
        var I8 = CoarseImageHelpers.BilinearResample(img, Wm8, Hm8);
        var Fm = CoarseImageHelpers.ToFloat4(I8);
        var colourMod = CoarseImageHelpers.Sub4(Fm, CoarseImageHelpers.Box5(Fm, Wm8, Hm8));
        var scalarMod = CoarseImageHelpers.SobelL1(CoarseImageHelpers.Channel0(I8), Wm8, Hm8);
        var Mm = Mat4D.FlowMatrix(ctx.RefCam8.Basic(), baseBasic);   // ref 1/8 → module full-res (un-shifted current calib)
        var cost = new float[nNodes];
        for (int i = 0; i < nNodes; i++)
        {
            var cam = MirrorPose.NodePose(sys, baseBasic, thetas[i], 0.0);
            var full = camBase.Clone(); full.K = cam.K; full.R = cam.R; full.T = cam.T;
            var sh = ViewTransform.Shift(full, cxs[i], cys[i]);
            var c2 = ViewTransform.Apply(pipe, sh);
            var c3 = ViewTransform.Scale(c2, 0.125f, 0.125f);
            cost[i] = Score(ctx, Mm, Mat4D.FlowMatrix(ctx.RefCam8.Basic(), c3.Basic()), colourMod, scalarMod, Wm8, Hm8);
        }
        int best = 0; for (int i = 1; i < nNodes; i++) if (cost[i] < cost[best]) best = i;
        const int centre = 266;
        int sel = (cost[centre] * 0.95f >= cost[best]) ? best : centre;
        var camSel = MirrorPose.NodePose(sys, baseBasic, thetas[sel], 0.0);
        return new Result { Theta = thetas[sel], Cx = cxs[sel], Cy = cys[sel], Sel = sel, Best = best, Cost = cost, Written = MirrorPose.Shift(camSel, cxs[sel], cys[sel]) };
    }

    /// <summary>`FUN_1802946e0`: z-buffered visibility, then the mean absolute colour/gradient difference (N &gt; 100 else FLT_MAX).</summary>
    public static float Score(Context ctx, float[] Mm, float[] Mc, float[] colourMod, float[] scalarMod, int Wm8, int Hm8)
    {
        int W8 = ctx.W8, H8 = ctx.H8;
        var mask = new byte[W8 * H8]; Array.Fill(mask, (byte)0xff);
        var zb = new float[Wm8 * Hm8 * 4]; for (int i = 0; i < zb.Length; i++) zb[i] = -1f;
        for (int y = 0; y < H8; y++)
            for (int x = 0; x < W8; x++)
            {
                float d = ctx.Depth8[y * W8 + x];
                float px = (1f * (float)x) * d, py = (1f * (float)y) * d;
                var (u, v) = Proj(Mm, px, py, d, 1f);
                int ui = (int)u, vi = (int)v;
                bool ok = ui >= 40 && ui < ctx.FrameW - 40 && vi >= 40 && vi < ctx.FrameH - 40;
                if (ok)
                {
                    var (u2, v2) = Proj(Mc, px, py, d, 1f);
                    int u2i = (int)u2, v2i = (int)v2;
                    if (u2i >= 5 && u2i < Wm8 - 5 && v2i >= 5 && v2i < Hm8 - 5)
                    {
                        float z = d; int e = (v2i * Wm8 + u2i) * 4;
                        if (zb[e] < 0f) { zb[e] = z; zb[e + 1] = x; zb[e + 2] = y; }
                        else if (zb[e] > z + 50f) { mask[(int)zb[e + 2] * W8 + (int)zb[e + 1]] = 0; zb[e] = z; zb[e + 1] = x; zb[e + 2] = y; }
                        else if (zb[e] < z - 50f) mask[y * W8 + x] = 0;
                        continue;
                    }
                }
                mask[y * W8 + x] = 0;
            }
        float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f, acc1 = 0f; int N = 0;
        for (int y = 0; y < H8; y++)
            for (int x = 0; x < W8; x++)
            {
                if (mask[y * W8 + x] == 0) continue;
                float d = ctx.Depth8[y * W8 + x], xs = (float)x, ys = (float)y;
                var q = new float[3];
                for (int l = 0; l < 3; l++) q[l] = ((d * Mc[8 + l] + Mc[12 + l]) + (xs * d) * Mc[l]) + (ys * d) * Mc[4 + l];
                float w = 1.0f / q[2]; int u2 = (int)(w * q[0]), v2 = (int)(w * q[1]);
                int ir = (y * W8 + x) * 4, im = (v2 * Wm8 + u2) * 4;
                a0 += MathF.Abs(ctx.Colour8[ir] - colourMod[im]); a1 += MathF.Abs(ctx.Colour8[ir + 1] - colourMod[im + 1]);
                a2 += MathF.Abs(ctx.Colour8[ir + 2] - colourMod[im + 2]); a3 += MathF.Abs(ctx.Colour8[ir + 3] - colourMod[im + 3]);
                acc1 += MathF.Abs(ctx.Scalar8[y * W8 + x] - scalarMod[v2 * Wm8 + u2]); N++;
            }
        if (N <= 100) return float.MaxValue;
        float h = (0.5f * a1) + ((0.5f * a2) + (2.0f * a0));
        return ((acc1 * 0.25f) + (h * 0.75f)) / (float)N;
    }
}
