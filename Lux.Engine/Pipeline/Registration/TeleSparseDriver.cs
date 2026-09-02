using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The TELE (higher-group) sparse matching of `lt::SparseLNR` — `FUN_1802ea1c0` + `matchFeaturesPerLevelHigherCams&lt;12&gt;` (`1802eb860`,
/// lambda `1802f2110`, core `FUN_1802f2c60`, ZSAD `FUN_1802f2ec0`), the depth-visibility mask `FUN_1802e7310` and the prior points
/// `FUN_1802b4c40` (spec `a521b464485b1074b.md`). Reuses the WIDE port's pyramids, RANSAC gate, view updates and finalize.
/// </summary>
public static class TeleSparseDriver
{
    static readonly float Sc432 = BitConverter.Int32BitsToSingle(0x3b17b426);
    const double RatioEps = 1e-7;

    static float Rcp(float x) { float r0 = Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar(); return ((1.0f - x * r0) * r0) + r0; }
    static float SqrtNR(float s) { if (s == 0f) return 0f; float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(s)).ToScalar(); float t = s * rs; return ((t * rs) + (-3.0f)) * ((-0.5f) * t); }

    /// <summary>WarpField projection `v = ((z·col2 + col3) + X·col0) + Y·col1`, `w = 1/v2` (column-major M).</summary>
    static (float X, float Y) Proj(float[] M, float X, float Y, float z)
    {
        var v = new float[3];
        for (int r = 0; r < 3; r++) v[r] = ((z * M[8 + r] + M[12 + r]) + X * M[r]) + Y * M[4 + r];
        float w = 1.0f / v[2];
        return (v[0] * w, w * v[1]);
    }

    /// <summary>`FUN_1802b4c40`: per triangulated WIDE point, its projection into the cam canvas (or (−1,−1)) gated by a 17×17 depth-consistency test.</summary>
    public static (float X, float Y)[] PriorPoints(TriPoint[] pts, float[] depth, int dW, int dH, float[] M, int imgW, int imgH)
    {
        const float sx = 1f, sy = 1f; var outp = new (float X, float Y)[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var p = pts[i]; outp[i] = (-1f, -1f);
            if (!(0.0f <= p.Z)) continue;
            float X0 = sx * p.U, Y0 = sy * p.V;
            int iy0 = (int)Y0, ix0 = (int)X0; if (ix0 < 0 || iy0 < 0 || ix0 >= dW || iy0 >= dH) continue;
            float z = depth[iy0 * dW + ix0];
            float Y = Y0 * z, X = X0 * z;
            var v = new float[3]; for (int r = 0; r < 3; r++) v[r] = ((z * M[8 + r] + M[12 + r]) + X * M[r]) + Y * M[4 + r];
            float w = 1.0f / v[2]; float px = v[0] * w, py = w * v[1];
            if (!(0 <= (int)px && (int)px < imgW && 0 <= (int)py && (int)py < imgH)) continue;
            int ix = (int)p.U, iy = (int)p.V;
            int bx0 = ix - 8, by0 = iy - 8, bx1 = ix + 9, by1 = iy + 9;
            bool inside = Math.Max(bx0, 0) < Math.Min(bx1, dW) && Math.Max(by0, 0) < Math.Min(by1, dH) && bx0 >= 0 && by0 >= 0 && bx1 <= dW && by1 <= dH;
            if (!inside) continue;
            float fx0 = (float)(ix - 8) * sx, fy0 = (float)(iy - 8) * sy, fx1 = (float)(ix + 8) * sx, fy2 = (float)(iy + 8) * sy;
            float[] C(float Xc, float Yc, float zc) { var o = new float[3]; for (int r = 0; r < 3; r++) o[r] = (M[12 + r] + zc * M[8 + r]) + (Yc * M[4 + r] + Xc * M[r]); return o; }
            float z0 = depth[(int)fy0 * dW + (int)fx0], z1 = depth[(int)fy0 * dW + (int)fx1], z2 = depth[(int)fy2 * dW + (int)fx0], z3 = depth[(int)fy2 * dW + (int)fx1];
            var v0 = C(z0 * fx0, z0 * fy0, z0); var v1 = C(z1 * fx1, fy0 * z1, z1); var v2 = C(fx0 * z2, z2 * fy2, z2); var v3 = C(fx1 * z3, fy2 * z3, z3);
            float w0 = 1.0f / v0[2], w3 = 1.0f / v3[2];
            float c0x = v0[0] * w0, c0y = v0[1] * w0, c3x = v3[0] * w3, c3y = v3[1] * w3;
            float r1 = Rcp(v1[2]), r2 = Rcp(v2[2]);   // rcpps + Newton on lanes (v1.z, v2.z)
            float c1x = v1[0] * r1, c1y = v1[1] * r1, c2x = v2[0] * r2, c2y = v2[1] * r2;
            float dx3 = c0x - c3x, dy3 = c0y - c3y; float s3 = dy3 * dy3 + dx3 * dx3; float d3 = SqrtNR(s3);
            float dx1 = c0x - c1x, dy1 = c0y - c1y, dx2 = c0x - c2x, dy2 = c0y - c2y;
            float s1 = dx1 * dx1 + dy1 * dy1, s2 = dx2 * dx2 + dy2 * dy2;
            float d1 = SqrtPs(s1), d2 = SqrtPs(s2);
            float m = MaxSs(MaxSs(d1, d3), d2);
            if (m < 32.0f) outp[i] = (px, py);
        }
        return outp;
    }
    static float SqrtPs(float s) { if (s == 0f) return 0f; float rs = Sse.ReciprocalSqrt(Vector128.Create(s)).ToScalar(); float t = s * rs; return ((t * rs) + (-3.0f)) * ((-0.5f) * t); }
    static float MaxSs(float a, float b) => (a > b) ? a : b;

    /// <summary>`lt::ImageCircleFilter&lt;float&gt;(r = 1)`: sign of the plus-shaped 5-tap sum (clamp-to-edge) is all that is consumed.</summary>
    static bool[] CirclePositive(float[] f, int w, int h)
    {
        var o = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0f;
                s += f[Math.Clamp(y - 1, 0, h - 1) * w + x]; s += f[y * w + Math.Clamp(x - 1, 0, w - 1)]; s += f[y * w + x]; s += f[y * w + Math.Clamp(x + 1, 0, w - 1)]; s += f[Math.Clamp(y + 1, 0, h - 1) * w + x];
                o[y * w + x] = (1.0f / 5f) * s > 0.0f;
            }
        return o;
    }

    /// <summary>`FUN_1802e7310`: z-buffered visibility of the reference depth in the cam canvas (¼ grid), opened (erode/dilate, r = 1), nearest-resampled to the depth size.</summary>
    public static byte[] VisibilityMask(float[] depth, int dW, int dH, float[] M, float sx, float sy, int wB, int hB, float[] rect)
    {
        int zw = wB / 4, zh = hB / 4, vw = dW / 4, vh = dH / 4;
        var zb = new float[zw * zh * 3]; for (int i = 0; i < zw * zh; i++) zb[i * 3] = -1f;
        var vis = new byte[vw * vh]; Array.Fill(vis, (byte)1);
        for (int iy = 0; iy < vh; iy++)
        {
            float Y0 = sy * (float)(4 * iy);
            for (int ix = 0; ix < vw; ix++)
            {
                float X0 = (float)(4 * ix) * sx;
                float z = depth[(int)Y0 * dW + (int)X0];
                float X = X0 * z, Y = Y0 * z;
                var v = new float[3]; for (int r = 0; r < 3; r++) v[r] = ((z * M[8 + r] + M[12 + r]) + X * M[r]) + Y * M[4 + r];
                float w = 1.0f / v[2];
                int px = (int)(w * v[0]); if (!((float)px >= rect[0] && rect[2] > (float)px)) { vis[iy * vw + ix] = 0; continue; }
                int py = (int)(w * v[1]); if (!((float)py >= rect[1] && rect[3] > (float)py)) { vis[iy * vw + ix] = 0; continue; }
                int qx = px / 4, qy = py / 4;
                float d = depth[(4 * iy) * dW + 4 * ix];
                int e = (qy * zw + qx) * 3;
                if (zb[e] < 0f) { zb[e] = d; zb[e + 1] = ix; zb[e + 2] = iy; }
                else if (zb[e] > d + 50.0f) { vis[(int)zb[e + 2] * vw + (int)zb[e + 1]] = 0; zb[e] = d; zb[e + 1] = ix; zb[e + 2] = iy; }
                else if (!(zb[e] >= d + (-50.0f))) vis[iy * vw + ix] = 0;
            }
        }
        var f = new float[vw * vh];
        for (int i = 0; i < f.Length; i++) f[i] = vis[i] > 0 ? 0f : 1f;
        var b = CirclePositive(f, vw, vh); for (int i = 0; i < f.Length; i++) vis[i] = b[i] ? (byte)0 : (byte)1;   // erode
        for (int i = 0; i < f.Length; i++) f[i] = vis[i];
        b = CirclePositive(f, vw, vh); for (int i = 0; i < f.Length; i++) vis[i] = b[i] ? (byte)1 : (byte)0;       // dilate
        // nearest resample to (dW, dH)
        var outp = new byte[dW * dH];
        int sxF = (int)((double)vw / (double)dW * 65536.0), syF = (int)((double)vh / (double)dH * 65536.0);
        for (int y = 0; y < dH; y++) { int syy = Math.Clamp((syF * y) >> 16, 0, vh - 1); for (int x = 0; x < dW; x++) outp[y * dW + x] = vis[syy * vw + Math.Clamp((sxF * x) >> 16, 0, vw - 1)]; }
        return outp;
    }

    // ---- 12×12 zero-mean weighted SAD (FUN_1802f2c60 / FUN_1802f2ec0) ----
    static int[] Mean12(Rgba8Image img, int x, int y)
    {
        var sum = new int[4];
        for (int r = -6; r < 6; r++) { int o = img.Offset(x - 6, y + r); for (int p = 0; p < 12; p++) for (int c = 0; c < 4; c++) sum[c] += img.Data[o + p * 4 + c]; }
        for (int c = 0; c < 4; c++) sum[c] = (sum[c] * 455) >> 16;
        return sum;
    }
    static int Zsad12(Rgba8Image tmpl, int tx, int ty, int[] tmean, Rgba8Image img, int cx, int cy, ushort[] weights)
    {
        var pmean = Mean12(img, cx, cy);
        var diff = new int[4]; for (int c = 0; c < 4; c++) diff[c] = Math.Clamp(tmean[c] - pmean[c], -32768, 32767);
        var acc = new int[8];
        for (int r = -6; r < 6; r++)
        {
            int to = tmpl.Offset(tx - 6, ty + r), po = img.Offset(cx - 6, cy + r);
            for (int p = 0; p < 12; p++)
                for (int c = 0; c < 4; c++)
                {
                    int d = tmpl.Data[to + p * 4 + c] - img.Data[po + p * 4 + c];
                    int term = Math.Abs(Math.Clamp(d - diff[c], -32768, 32767));
                    int lane = (p & 1) * 4 + c; acc[lane] = Math.Clamp(acc[lane] + term, -32768, 32767);
                }
        }
        int score = 0;
        for (int c = 0; c < 4; c++) { int sad = Math.Min(acc[c] + acc[c + 4], 65535); score += (int)(((uint)weights[c] * (uint)sad) >> 8); }
        return score;
    }
    static ((int X, int Y) Pos, int Best, int Second) Search12(Rgba8Image tmplImg, (int X, int Y) refPt, Rgba8Image img, (int X, int Y) pred, int r, ushort[] weights)
    {
        var tmean = Mean12(tmplImg, refPt.X, refPt.Y);
        int x0 = pred.X - r, y0 = pred.Y - r, x1 = x0 + 2 * r + 1, y1 = y0 + 2 * r + 1;
        if (x0 <= 5) x0 = 6; if (y0 <= 5) y0 = 6; x1 = Math.Min(img.W - 6, x1); y1 = Math.Min(img.H - 6, y1);
        int best = int.MaxValue, second = int.MaxValue; var pos = (-1, -1);
        foreach (var (cx, cy) in SparseLnrMatch.Region.Rect(x0, y0, x1, y1).Walk())
        {
            int s = Zsad12(tmplImg, refPt.X, refPt.Y, tmean, img, cx, cy, weights);
            if (s < second) { if (s < best) { second = best; best = s; pos = (cx, cy); } else second = s; }
        }
        return (pos, best, second);
    }

    /// <summary>13×13 Welford standard deviation of channel 0 (var = M2·(1/169), rsqrt + Newton).</summary>
    static float TextureStd13(Rgba8Image img, int x, int y)
    {
        float n = 0f, mean = 0f, m2 = 0f, inv = 0f;
        for (int yy = y - 6; yy <= y + 6; yy++)
            for (int xx = x - 6; xx <= x + 6; xx++)
            {
                float nOld = n, v = img.Data[img.Offset(xx, yy)];
                n = nOld + 1.0f; inv = 1.0f / n;
                float delta = v - mean, rr = delta * inv;
                mean += rr; m2 += (delta * nOld) * rr;
            }
        float var = m2 * inv;
        if (var == 0f) return 0f;
        float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(var)).ToScalar();
        float s = var * rs;
        return (-0.5f * s) * ((s * rs) - 3.0f);
    }

    /// <summary>`matchFeaturesPerLevelHigherCams&lt;12&gt;` for one level.</summary>
    public static MatchedPoint[] MatchLevel(int level, Rgba8Image A, Rgba8Image B, byte[] mask, int maskStride, FeaturePoint[] feats, bool[] skip,
        MatchView viewA, MatchView viewB, float[] depth, int dW, float[] M, float sx, float sy, float planeDepth, bool bidir, bool farScene)
    {
        var weights = SparseLnrMatch.WeightsColour;
        float wBl = (float)B.W, inv2 = 2.0f / wBl, cBx = wBl * 0.5f, cBy = (float)B.H * 0.5f, half = wBl * 0.5f;
        float scale = (float)(1 << level), invScale = 1.0f / scale;
        var outp = new MatchedPoint[feats.Length];
        for (int i = 0; i < feats.Length; i++)
        {
            outp[i] = default; outp[i].RefIdx = -1; outp[i].Mx = outp[i].My = outp[i].PredX = outp[i].PredY = -1f;
            var f = feats[i]; if (skip[i]) continue;
            var refPt = ((int)f.X, (int)f.Y);
            if (!(refPt.Item1 >= 6 && refPt.Item1 < A.W - 6 && refPt.Item2 >= 6 && refPt.Item2 < A.H - 6)) { outp[i] = MatchedPoint.Fail(i); continue; }
            float d = depth[(int)(scale * f.Y) * dW + (int)(f.X * scale)];
            bool useA = viewA.Enabled && (MathF.Abs(d - planeDepth) < planeDepth * 0.15f + 200.0f) && (d > 100.0f);
            int octave = 2 - (useA ? 1 : 0);
            var H = useA ? viewA.H : viewB.H;
            var pr = SparseLnrMatch.Project(H, f.Nx, f.Ny); (float X, float Y) pred = (pr.X * half + cBx, pr.Y * half + cBy);
            var ip = ((int)MathF.Round(pred.X, MidpointRounding.AwayFromZero), (int)MathF.Round(pred.Y, MidpointRounding.AwayFromZero));
            float X0 = (f.X * scale) * sx, Y0 = (f.Y * scale) * sy; float z = depth[(int)Y0 * dW + (int)X0];
            var dp = Proj(M, X0 * z, Y0 * z, z);
            float dfx = dp.X - scale * pred.X, dfy = dp.Y - scale * pred.Y; float s2 = dfy * dfy + dfx * dfx;
            float dist = SqrtNR(s2);
            if (dist > (0.05f * scale) * (float)A.W) { pred = (dp.X * invScale, dp.Y * invScale); ip = ((int)MathF.Round(pred.X, MidpointRounding.AwayFromZero), (int)MathF.Round(pred.Y, MidpointRounding.AwayFromZero)); }
            if (!(ip.Item1 >= 6 && ip.Item1 < B.W - 6 && ip.Item2 >= 6 && ip.Item2 < B.H - 6)) { outp[i] = MatchedPoint.Fail(i); continue; }
            int bo = B.Offset(ip.Item1, ip.Item2);
            if (B.Data[bo] == 0 && B.Data[bo + 1] == 0 && B.Data[bo + 2] == 0 && B.Data[bo + 3] == 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            if (mask[ip.Item2 * maskStride + ip.Item1] != 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            int r = farScene ? 3 : 8 + 2 * (useA ? 1 : 0);
            var (pos, best, second) = Search12(A, refPt, B, ip, r, weights);
            if (pos.X < 0) throw new InvalidOperationException("bug in higher cam matching");
            if (B.Data[B.Offset(pos.X, pos.Y)] == 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            float nmx = ((float)pos.X - cBx) * inv2, nmy = ((float)pos.Y - cBy) * inv2;
            float score = best * Sc432, sec = second * Sc432;
            float ratio = (float)((double)score / ((double)sec + RatioEps));
            bool ok = ratio < 0.8f && score < 10.0f;
            var rec = new MatchedPoint { RefIdx = i, Mx = pos.X, My = pos.Y, PredX = pred.X, PredY = pred.Y, NmX = nmx, NmY = nmy, Score = score, Ratio = ratio, Octave = octave };
            if (TextureStd13(B, pos.X, pos.Y) < 4.0f) rec.Status = 0;
            else if (!ok) rec.Status = 3;
            else if (!bidir) rec.Status = 4;
            else
            {
                var (rp, rb, rs) = Search12(B, pos, A, refPt, 3, weights);
                if (rp.X < 0) throw new InvalidOperationException("bug in higher cam matching bidirection");
                float rbf = rb * Sc432, rsf = rs * Sc432;
                int st = 3;
                if ((float)((double)rbf / ((double)rsf + RatioEps)) < 0.95f && rbf < 10.0f && Math.Abs(rp.X - refPt.Item1) < 3) st = 3 + (Math.Abs(rp.Y - refPt.Item2) < 3 ? 1 : 0);
                rec.Status = st;
            }
            outp[i] = rec;
        }
        return outp;
    }

    public sealed class Result { public MatchedPoint[][] PerLevel = null!; public (float X, float Y)[] Out = null!; public MatchView ViewA = null!, ViewB = null!; public bool[][] Skip = null!; }

    /// <summary>`FUN_1802ea1c0`: the whole TELE matching for one camera.</summary>
    public static Result Run(PaddedRgba8[] refPyr, FeaturePoint[][] feats, int nRefPts, byte[] B0, int wB, int hB, (float X, float Y)[] prior,
        float[] depth, int dW, int dH, float[] M, float sx, float sy, float planeDepth, bool farScene, float satLevel, Action<string>? log = null)
    {
        var res = new Result();
        int nLevels = feats.Length;
        if (nRefPts < 20) { res.Out = Enumerable.Repeat((-1f, -1f), nRefPts).ToArray(); res.PerLevel = new MatchedPoint[nLevels][]; return res; }
        var pyrB = SparseLnrPyramid.Build(B0, wB, hB, nLevels, 8);
        float wB0 = (float)wB, hB0 = (float)hB; float cx0 = 0.5f * wB0, cy0 = hB0 * 0.5f;
        var rectF = new[] { 6.0f, 6.0f, wB0 + (-6.0f), hB0 + (-6.0f) };
        var vis = VisibilityMask(depth, dW, dH, M, sx, sy, wB, hB, rectF);
        float inv2 = 2.0f / wB0;
        var srcA = new List<(float X, float Y)>(); var dstA = new List<(float X, float Y)>(); var srcB = new List<(float X, float Y)>(); var dstB = new List<(float X, float Y)>();
        res.Skip = new bool[nLevels][]; for (int l = 0; l < nLevels; l++) res.Skip[l] = new bool[feats[l].Length];
        int g = 0;
        var B0img = new Rgba8Image(B0, wB, hB, wB);
        for (int level = 0; level < 3; level++)
        {
            float scale = (float)Math.Pow(2.0, level);
            for (int j = 0; j < feats[level].Length; j++)
            {
                var f = feats[level][j]; int ix = (int)(f.X * scale), iy = (int)(f.Y * scale);
                if (vis[iy * dW + ix] == 0) { res.Skip[level][j] = true; g++; continue; }
                var p = prior[g];
                if (!(6.0f <= p.X && p.X < wB0 - 6 && 6.0f <= p.Y && p.Y < hB0 - 6)) { res.Skip[level][j] = true; g++; continue; }
                int bo = B0img.Offset((int)p.X, (int)p.Y);
                if (B0[bo] == 0 && B0[bo + 1] == 0 && B0[bo + 2] == 0 && B0[bo + 3] == 0) { res.Skip[level][j] = true; g++; continue; }
                res.Skip[level][j] = false;
                bool useA;
                if (6000.0f <= planeDepth) useA = false;
                else { float d = depth[iy * dW + ix]; useA = !(MathF.Abs(d - planeDepth) >= planeDepth * 0.15f + 200.0f) && (d > 100.0f); }
                var nm = ((p.X - cx0) * inv2, (p.Y - cy0) * inv2);
                if (useA) { srcA.Add((f.Nx, f.Ny)); dstA.Add(nm); } else { srcB.Add((f.Nx, f.Ny)); dstB.Add(nm); }
                g++;
            }
        }
        var viewA = new MatchView { Id = 1, Enabled = false }; var viewB = new MatchView { Id = 2, Enabled = false };
        var ptsA = new SparseLnrRansac.ViewPoints(nLevels); var ptsB = new SparseLnrRansac.ViewPoints(nLevels);
        if (srcA.Count >= 5) { viewA.H = Homography.LeastSquares(srcA.ToArray(), dstA.ToArray()); viewA.Enabled = true; }
        if (srcB.Count >= 5) { viewB.H = Homography.LeastSquares(srcB.ToArray(), dstB.ToArray()); viewB.Enabled = true; }
        log?.Invoke($"tele: prior pairs A {srcA.Count} B {srcB.Count}; viewA {(viewA.Enabled ? "on" : "off")} viewB {(viewB.Enabled ? "on" : "off")}");
        res.ViewA = viewA; res.ViewB = viewB; res.PerLevel = new MatchedPoint[nLevels][];
        if (!viewA.Enabled && !viewB.Enabled) { res.Out = Enumerable.Repeat((-1f, -1f), nRefPts).ToArray(); return res; }
        int top = nLevels - 1, minInl = 8;
        for (int level = 2; level >= 0; level--)
        {
            var A = refPyr[level].AsImage(); var Bimg = pyrB[level].AsImage();
            var mask = SparseLnrPyramid.SaturationMask(pyrB[level], satLevel, 13);
            var m = MatchLevel(level, A, Bimg, mask, pyrB[level].W, feats[level], res.Skip[level], viewA, viewB, depth, dW, M, sx, sy, planeDepth, true, farScene);
            res.PerLevel[level] = m;
            float wBl = (float)pyrB[level].W, thrA = 2.0f / wBl, thrB = farScene ? 0.6f / wBl : thrA;
            SparseLnrRansac.Gate(m, viewA, viewB, feats[level], thrA, thrB, minInl, allowDisable: false);
            if (level != 0)
            {
                SparseLnrRansac.UpdateView(viewA, ptsA, level, feats[level], m, level != top);
                SparseLnrRansac.UpdateView(viewB, ptsB, level, feats[level], m, level != top);
                if (!viewA.Enabled && !viewB.Enabled) { res.Out = Enumerable.Repeat((-1f, -1f), nRefPts).ToArray(); return res; }
            }
            minInl = (int)((double)minInl * 1.5);
        }
        for (int l = 3; l < nLevels; l++) res.PerLevel[l] = Array.Empty<MatchedPoint>();
        res.Out = SparseLnrRansac.Finalize(res.PerLevel, nRefPts);
        return res;
    }
}
