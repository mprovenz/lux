using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>8-bit RGBA image view (dense or strided) as the matcher sees `lt::Image&lt;vec4x8ui&gt;`.</summary>
public readonly struct Rgba8Image
{
    public readonly byte[] Data; public readonly int W, H, Stride, Pad;   // stride in pixels; Pad = addressable zero ring
    public Rgba8Image(byte[] data, int w, int h, int stride, int pad = 0) { Data = data; W = w; H = h; Stride = stride; Pad = pad; }
    public int Offset(int x, int y) => ((y + Pad) * Stride + (x + Pad)) * 4;
}

/// <summary>`lt::MatchedPoint` (0x2c bytes).</summary>
public struct MatchedPoint
{
    public int RefIdx;
    public float Mx, My;          // matched pixel position (integer-valued), level coordinates
    public float PredX, PredY;    // predicted position (un-rounded)
    public float NmX, NmY;        // normalised matched position ((m − centreB) · 2/wB)
    public float Score;           // best SAD × 1/192
    public float Ratio;           // best / (second + 1e-7)
    public int Status;            // 0 low texture, 1 fail, 3 weak, 4 good, 5 RANSAC inlier
    public int Octave;            // 1 view A, 2 view B, 0 fail
    public static MatchedPoint Fail(int idx) => new() { RefIdx = idx, Mx = -1f, My = -1f, PredX = -1f, PredY = -1f, NmX = -1f, NmY = -1f, Score = -1f, Ratio = -1f, Status = 1, Octave = 0 };
}

/// <summary>A 3×3 homography view (`SparseLNR+0x310/+0x370`): id, enabled, row-major H.</summary>
public sealed class MatchView
{
    public int Id; public bool Enabled; public float[] H = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
}

/// <summary>
/// The per-level SAD matcher of `lt::SparseLNR` (`matchFeaturesPerLevelLowerCams&lt;8&gt;` 1802e98c0 / lambda_1 1802f1700,
/// core `FUN_1802f01a0`), ported from the decompilation: 8×8 RGBA templates, weighted byte-SAD `Σ_c ⌊w_c·sad_c/256⌋`
/// (w = (512,128,128,0)), radius search over an axis-aligned rect walked with Lumen's float iterator, strict best/second
/// update, saturation mask, 9×9 Welford texture gate (`rsqrtss`), Lowe ratio in double, bidirectional check.
/// </summary>
public static class SparseLnrMatch
{
    public static readonly ushort[] WeightsColour = { 512, 128, 128, 0 };    // packusdw(cvtps2dq((2,0.5,0.5,0)·256)), DAT_180835310
    public static ushort[] WeightsMono = { 256, 0, 0, 0 };                    // mono sensors (+0x240 != 0) in matchFeaturesPerLevelLowerCams: plain channel-0 SAD, scale 1/64 (verified on the live A2 records)
    public static ushort[] WeightsMonoInit = { 768, 0, 0, 0 };                // mono in initLowerACamera: DAT_180835300 (3,0,0,0) packed ×256, scale 1/64 (verified)
    static readonly float Scale192 = BitConverter.Int32BitsToSingle(0x3baaaaab);   // 1/192 (DAT_1806bbad0)
    static readonly float Scale64 = BitConverter.Int32BitsToSingle(0x3c800000);    // 1/64 (DAT_1806bba80), mono
    const float Ratio1 = 0.8f, Ratio2 = 0.95f, ScoreMax = 10.0f;
    const float StdMin = 4.0f;                                              // DAT_180682408
    const double RatioEps = 1e-7;                                           // DAT_1806b5b98

    /// <summary>Weighted SAD of the 8×8 template at (tx−4..tx+3, ty−4..ty+3) of `tmplImg` against the patch at (px, py) of `img`.</summary>
    public static int Sad8(Rgba8Image tmplImg, int tx, int ty, Rgba8Image img, int px, int py, ushort[] weights)
    {
        var acc = new int[8];   // (even px c0..c3, odd px c0..c3) — u16 lanes with saturating add (never reached here)
        for (int r = 0; r < 8; r++)
        {
            int to = tmplImg.Offset(tx - 4, ty - 4 + r), po = img.Offset(px - 4, py - 4 + r);
            for (int p = 0; p < 8; p++)
                for (int c = 0; c < 4; c++)
                {
                    int d = Math.Abs(tmplImg.Data[to + p * 4 + c] - img.Data[po + p * 4 + c]);
                    int lane = (p & 1) * 4 + c;
                    acc[lane] = Math.Min(acc[lane] + d, 65535);
                }
        }
        int score = 0;
        for (int c = 0; c < 4; c++)
        {
            int sad = Math.Min(acc[c] + acc[c + 4], 65535);
            score += (int)(((uint)weights[c] * (uint)sad) >> 8);
        }
        return score;
    }

    /// <summary>`FUN_1802f1440` / the patch-mean prologue of `FUN_1802f1030`: the per-channel mean of the 8×8 block at
    /// (x−4..x+3, y−4..y+3) as `(Σ_c · 1024) >> 16` (= `Σ >> 6`, `pmulhuw` against DAT_1806bbae0). The u16 lanes are the
    /// same (even px c0..c3 | odd px c0..c3) split the SAD uses, folded with `pshufd 0x4e; paddusw` before the multiply.</summary>
    public static void BlockMean(Rgba8Image img, int x, int y, Span<int> mean)
    {
        Span<int> acc = stackalloc int[8]; acc.Clear();
        for (int r = 0; r < 8; r++)
        {
            int o = img.Offset(x - 4, y - 4 + r);
            for (int p = 0; p < 8; p++)
                for (int c = 0; c < 4; c++)
                {
                    int lane = (p & 1) * 4 + c;
                    acc[lane] = Math.Min(acc[lane] + img.Data[o + p * 4 + c], 65535);
                }
        }
        for (int c = 0; c < 4; c++) { int s = Math.Min(acc[c] + acc[c + 4], 65535); mean[c] = (s * 1024) >> 16; }
    }

    static int Sat16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;

    /// <summary>Zero-mean weighted SAD (`FUN_1802f1030`, used by `initLowerBCamera` and the HigherCams core): with
    /// `diff[c] = tmean[c] − pmean[c]` (`psubsw`), accumulate `|(T − P) − diff[c]|` (`psubsw`/`psubsw`/`pabsw`/`paddsw`)
    /// over the 8×8×4 block, fold the even/odd halves and apply the same `Σ_c ⌊w_c·sad_c/256⌋` as the plain core.</summary>
    public static int Zsad8(Rgba8Image tmplImg, int tx, int ty, ReadOnlySpan<int> tmean, Rgba8Image img, int px, int py, ushort[] weights)
    {
        Span<int> pmean = stackalloc int[4]; BlockMean(img, px, py, pmean);
        Span<int> diff = stackalloc int[4]; for (int c = 0; c < 4; c++) diff[c] = Sat16(tmean[c] - pmean[c]);
        Span<int> acc = stackalloc int[8]; acc.Clear();
        for (int r = 0; r < 8; r++)
        {
            int to = tmplImg.Offset(tx - 4, ty - 4 + r), po = img.Offset(px - 4, py - 4 + r);
            for (int p = 0; p < 8; p++)
                for (int c = 0; c < 4; c++)
                {
                    int d = Sat16(Sat16(tmplImg.Data[to + p * 4 + c] - img.Data[po + p * 4 + c]) - diff[c]);
                    int lane = (p & 1) * 4 + c;
                    acc[lane] = Sat16(acc[lane] + (d < 0 ? -d : d));
                }
        }
        int score = 0;
        for (int c = 0; c < 4; c++)
        {
            int sad = Math.Min(acc[c] + acc[c + 4], 65535);
            score += (int)(((uint)weights[c] * (uint)sad) >> 8);
        }
        return score;
    }

    /// <summary>Search-region iterator of `FUN_1802f87d0`/core loop: `{s, d1, n1, d2, n2}` walked in float exactly like Lumen.</summary>
    public struct Region
    {
        public float Sx, Sy, D1x, D1y, D2x, D2y; public int N1, N2;
        public static Region Rect(int x0, int y0, int x1, int y1) => new() { Sx = x0, Sy = y0, D1x = 1, D1y = 0, D2x = 0, D2y = 1, N1 = x1 - 1 - x0, N2 = y1 - 1 - y0 };
        public IEnumerable<(int X, int Y)> Walk()
        {
            int endX = (int)(D2x * (N2 + 1) + Sx), endY = (int)(D2y * (N2 + 1) + Sy);
            int i = 0, j = 0;
            while (true)
            {
                int cx = (int)(D2x * j + (D1x * i + Sx)), cy = (int)(D2y * j + (D1y * i + Sy));
                if (cx == endX && cy == endY) yield break;
                yield return (cx, cy);
                if (i >= N1) { i = 0; j++; } else i++;
            }
        }
    }

    /// <summary>`FUN_1802f8810`: a band of half-width `hw` along p0→p1 (unit steps in the major axis); end points outside the
    /// rect are walked inwards along the (rsqrtss-normalised) direction; empty region if that fails.</summary>
    public static Region Band((int X0, int Y0, int X1, int Y1) rect, (int X, int Y) p0, (int X, int Y) p1, int hw)
    {
        bool Inside((int X, int Y) p) => rect.Y0 <= p.Y && p.Y < rect.Y1 && rect.X0 <= p.X && p.X < rect.X1;
        if (!Inside(p0) || !Inside(p1))
        {
            float dx = (float)(p1.X - p0.X), dy = (float)(p1.Y - p0.Y), q = dy * dy + dx * dx;
            float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(q)).ToScalar();
            float t = (q * r) * r + (-3.0f), inv = (r * -0.5f) * t, ux = dx * inv, uy = inv * dy;
            int n = q == 0f ? 0 : (int)(((q * r) * -0.5f) * t);
            if (!Inside(p0))
            {
                bool found = false;
                if (n > 0) { float fy = p0.Y, fx = p0.X; for (int cnt = n + 1; cnt > 1; cnt--) { int iy = (int)fy, ix = (int)fx; if (rect.Y0 <= iy && iy < rect.Y1 && rect.X0 <= ix && ix < rect.X1) { p0 = (ix, iy); found = true; break; } fx += ux; fy += uy; } }
                if (!found) return default;
            }
            if (!Inside(p1))
            {
                bool found = false;
                if (n > 0) { float fy = p1.Y, fx = p1.X; for (int cnt = n + 1; cnt > 1; cnt--) { int iy = (int)fy, ix = (int)fx; if (rect.Y0 <= iy && iy < rect.Y1 && rect.X0 <= ix && ix < rect.X1) { p1 = (ix, iy); found = true; break; } fx -= ux; fy -= uy; } }
                if (!found) return default;
            }
        }
        if (hw < 0) return default;
        float ax = MathF.Abs((float)(p1.X - p0.X)), ay = MathF.Abs((float)(p1.Y - p0.Y));
        float m = ax <= ay ? ay : ax;
        float d2x = (float)(p1.X - p0.X) * (1.0f / m), d2y = (1.0f / m) * (float)(p1.Y - p0.Y);
        float d1x = 0f, d1y = 1f; if (ax < ay) { d1x = 1f; d1y = 0f; }
        return new Region { Sx = (float)p0.X - d1x * (float)hw, Sy = (float)p0.Y - d1y * (float)hw, D1x = d1x, D1y = d1y, D2x = d2x, D2y = d2y, N1 = hw * 2, N2 = (int)m };
    }

    /// <summary>Generic template search over a region (`FUN_1802f01a0` core loop).</summary>
    public static ((int X, int Y) Pos, int Best, int Second) RegionSearch(Rgba8Image refImg, (int X, int Y) refPt, Rgba8Image tgtImg, Region region, ushort[] weights)
    {
        int best = int.MaxValue, second = int.MaxValue; var pos = (-1, -1);
        foreach (var (cx, cy) in region.Walk())
        {
            int s = Sad8(refImg, refPt.X, refPt.Y, tgtImg, cx, cy, weights);
            if (s < second) { if (s < best) { second = best; best = s; pos = (cx, cy); } else second = s; }
        }
        return (pos, best, second);
    }

    /// <summary>`FUN_1802f1030` driven over a region: the 8×8 zero-mean template from `refImg` at `refPt` scanned over
    /// `region` in `tgtImg`, with the same strict best/second update as `RegionSearch`.</summary>
    public static ((int X, int Y) Pos, int Best, int Second) RegionSearchZ(Rgba8Image refImg, (int X, int Y) refPt, Rgba8Image tgtImg, Region region, ushort[] weights)
    {
        Span<int> tmean = stackalloc int[4]; BlockMean(refImg, refPt.X, refPt.Y, tmean);
        int best = int.MaxValue, second = int.MaxValue; var pos = (-1, -1);
        foreach (var (cx, cy) in region.Walk())
        {
            int s = Zsad8(refImg, refPt.X, refPt.Y, tmean, tgtImg, cx, cy, weights);
            if (s < second) { if (s < best) { second = best; best = s; pos = (cx, cy); } else second = s; }
        }
        return (pos, best, second);
    }

    /// <summary>`WarpField` (0x50 bytes): 4×4 float column-major `M` (module → target at depth), scales sx, sy.</summary>
    public sealed class WarpField
    {
        public float[] M = new float[16]; public float Sx = 1f, Sy = 1f;
        public static WarpField FromBytes(byte[] b) { var w = new WarpField(); for (int k = 0; k < 16; k++) w.M[k] = BitConverter.ToSingle(b, 4 * k); w.Sx = BitConverter.ToSingle(b, 0x48); w.Sy = BitConverter.ToSingle(b, 0x4c); return w; }
        /// <summary>Project the full-resolution reference pixel (fx, fy) at depth z into level pixels (× invScale), as in lambda_1 of initLowerACamera.</summary>
        public (float X, float Y) Project(float fx, float fy, float z, float invScale)
        {
            float X = (z * Sx) * fx, Y = (z * Sy) * fy;
            var v = new float[4];
            for (int l = 0; l < 4; l++) v[l] = (Y * M[4 + l] + M[12 + l]) + (z * M[8 + l] + X * M[l]);
            float w = 1.0f / v[2];
            return ((v[0] * invScale) * w, (v[1] * invScale) * w);
        }
        /// <summary>The same projection without the level scaling (`initLowerBCamera::lambda_1` divides once and applies
        /// `invScale` later, to the near−far difference and to the stored prediction: disasm 1802f0792–1802f0817).</summary>
        public (float X, float Y) ProjectRaw(float fx, float fy, float z)
        {
            float X = (z * Sx) * fx, Y = (z * Sy) * fy;
            var v = new float[4];
            for (int l = 0; l < 4; l++) v[l] = (Y * M[4 + l] + M[12 + l]) + (z * M[8 + l] + X * M[l]);
            float w = 1.0f / v[2];
            return (v[0] * w, w * v[1]);
        }
    }

    /// <summary>`initLowerACamera&lt;8&gt;` (1802ef2d0) for the top pyramid level: predictions from the WarpField at depths 100 /
    /// 100000 (near / far), SAD search along the epipolar band between them (half-width 2). Depth-based octave selection
    /// (view A, near scenes) is not ported yet — only the far-scene / view-B-only path is supported.</summary>
    public static MatchedPoint[] InitLowerA(Rgba8Image A, Rgba8Image B, byte[] mask, int maskStride, (int X, int Y) offs, FeaturePoint[] feats, int level,
        WarpField W, MatchView viewA, MatchView viewB, bool bidir, bool mono = false, CalibData? calibA = null, CalibData? calibB = null, float planeDepth = 1500f)
    {
        if (viewA.Enabled && (calibA is null || calibB is null)) throw new ArgumentException("initLowerACamera with view A enabled needs calibA/calibB for the two-ray depth (FUN_180302420)");
        float depthTol = planeDepth * BitConverter.Int32BitsToSingle(0x3e19999a) + 200.0f;   // DAT_1806bba8c = 0.15, DAT_1806bba90 = 200
        var weights = mono ? WeightsMonoInit : WeightsColour; float Scale60 = mono ? Scale64 : Scale192;
        int rrx0 = Math.Max(offs.X, 4), rry0 = Math.Max(offs.Y, 4), rrx1 = Math.Min(B.W + offs.X, A.W - 4), rry1 = Math.Min(B.H + offs.Y, A.H - 4);
        var rt = (Math.Max(-offs.X, 4), Math.Max(-offs.Y, 4), Math.Min(A.W - offs.X, B.W - 4), Math.Min(A.H - offs.Y, B.H - 4));
        float cBx = (float)B.W * 0.5f, cBy = (float)B.H * 0.5f, sNorm = 2.0f / (float)B.W;
        float scale = (float)(1 << level), invScale = 1.0f / scale;
        var outp = new MatchedPoint[feats.Length];
        for (int i = 0; i < feats.Length; i++)
        {
            var f = feats[i]; var refPt = ((int)f.X, (int)f.Y);
            if (!(refPt.Item1 >= rrx0 && refPt.Item1 < rrx1 && refPt.Item2 >= rry0 && refPt.Item2 < rry1)) { outp[i] = MatchedPoint.Fail(i); continue; }
            float fx = (float)refPt.Item1 * scale, fy = (float)refPt.Item2 * scale;
            var pn = W.Project(fx, fy, 100.0f, invScale); var pf = W.Project(fx, fy, 100000.0f, invScale);
            var near = ((int)pn.X, (int)pn.Y); var far = ((int)pf.X, (int)pf.Y);
            bool In((int X, int Y) p) => p.X >= rt.Item1 && p.X < rt.Item3 && p.Y >= rt.Item2 && p.Y < rt.Item4;
            if (!(In(near) || In(far))) { outp[i] = MatchedPoint.Fail(i); continue; }
            var (pos, best, second) = RegionSearch(A, refPt, B, Band(rt, far, near, 2), weights);
            if (!(pos.X > 0 && pos.Y > 0) || mask[pos.Y * maskStride + pos.X] != 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            float score = best * Scale60, sec = second * Scale60;
            int octave = 2;
            if (viewA.Enabled)
            {   // §5 step 5: depth = FUN_180302420(calibA, calibB, fx, fy, pos·scale); view A when within 15 % + 200 of the plane depth (and > 100)
                float depth = Triangulator.RayScale(calibA!, calibB!, fx, fy, (float)pos.X * scale, (float)pos.Y * scale);
                if (Math.Abs(depth - planeDepth) < depthTol) octave = depth > 100.0f ? 1 : 2;
            }
            float nmx = ((float)pos.X - cBx) * sNorm, nmy = ((float)pos.Y - cBy) * sNorm;
            float ratio = Ratio(score, sec);
            bool ok = ratio < Ratio1 && score < ScoreMax;
            if (bidir)
            {
                var (rp, rb, rs) = RadiusSearch(B, pos, A, refPt, 3, weights);
                // FUN_1802f01a0 scales its own float outputs by DAT_1806bbad0 = 1/192 (disasm 1802f04cc), so the reverse
                // gate uses 1/192 even when the forward score used the mono 1/64 — exactly as in initLowerBCamera.
                float rbf = rb * Scale192, rsf = rs * Scale192;
                ok &= Ratio(rbf, rsf) < Ratio2 && rbf < ScoreMax && Math.Abs(rp.X - refPt.Item1) < 2 && Math.Abs(rp.Y - refPt.Item2) < 2;
            }
            outp[i] = new MatchedPoint { RefIdx = i, Mx = pos.X, My = pos.Y, PredX = pf.X, PredY = pf.Y, NmX = nmx, NmY = nmy, Score = score, Ratio = ratio, Status = ok ? 4 : 3, Octave = octave };
        }
        PostPass(outp, viewA, viewB);
        return outp;
    }

    /// <summary>
    /// `initLowerBCamera&lt;8&gt;` (outer 1802e94c0, per-feature `lambda_1` 1802f0610) — the top-level initialiser used when
    /// `SparseLNR+0x244` (mode) is 1 or 2, i.e. the reference module is **B4 (id 8) or C5 (id 14)** (`1802be477–1802be48d`).
    /// Unlike `initLowerACamera` it takes no calibration and no plane depth: the prediction is simply `ref − offs`, and the
    /// search is a band of half-width 5 along the epipolar direction `(near − far)·invScale` through it, extending
    /// ±`wB/4` px. Scoring is the **zero-mean** SAD core `FUN_1802f1030`; the octave is chosen from the pixel distance
    /// `|(offs + match) − ref|` (&lt; 8 → view A). No texture gate, no status 0 — as in `initLowerACamera`.
    /// The reverse (bidirectional) check always scales by 1/192, never by the mono 1/64 (disasm 1802f0bfa).
    /// </summary>
    public static MatchedPoint[] InitLowerB(Rgba8Image A, Rgba8Image B, byte[] mask, int maskStride, (int X, int Y) offs, FeaturePoint[] feats, int level,
        WarpField W, MatchView viewA, MatchView viewB, bool bidir, bool mono = false)
    {
        var weights = mono ? WeightsMonoInit : WeightsColour; float Scale60 = mono ? Scale64 : Scale192;
        int rrx0 = Math.Max(offs.X, 4), rry0 = Math.Max(offs.Y, 4), rrx1 = Math.Min(B.W + offs.X, A.W - 4), rry1 = Math.Min(B.H + offs.Y, A.H - 4);
        int rtx0 = Math.Max(-offs.X, 4), rty0 = Math.Max(-offs.Y, 4), rtx1 = Math.Min(A.W - offs.X, B.W - 4), rty1 = Math.Min(A.H - offs.Y, B.H - 4);
        float cBx = (float)B.W * 0.5f, cBy = (float)B.H * 0.5f, sNorm = 2.0f / (float)B.W;
        float scale = (float)Math.ScaleB(1.0, level), invScale = 1.0f / scale;
        float half = (float)(B.W / 4);                                    // (int) B.w / 4, signed, then cvtsi2ss
        var outp = new MatchedPoint[feats.Length];
        for (int i = 0; i < feats.Length; i++)
        {
            var f = feats[i]; int rx = (int)f.X, ry = (int)f.Y;
            if (!(rx >= rrx0 && rx < rrx1 && ry >= rry0 && ry < rry1)) { outp[i] = MatchedPoint.Fail(i); continue; }
            int px = rx - offs.X, py = ry - offs.Y;                       // pred = ref − offs
            if (!(px >= rtx0 && px < rtx1 && py >= rty0 && py < rty1)) { outp[i] = MatchedPoint.Fail(i); continue; }
            float fx = (float)rx * scale, fy = (float)ry * scale;
            var pn = W.ProjectRaw(fx, fy, 100.0f); var pf = W.ProjectRaw(fx, fy, 100000.0f);
            float dirx = (pn.X - pf.X) * invScale, diry = (pn.Y - pf.Y) * invScale;
            float q = diry * diry + dirx * dirx;
            float rq = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(q)).ToScalar();
            float invLen = (rq * -0.5f) * ((q * rq) * rq + (-3.0f));
            float ox = (dirx * half) * invLen, oy = (diry * half) * invLen;
            var p0 = ((int)((float)px - ox), (int)((float)py - oy));
            var p1 = ((int)(ox + (float)px), (int)(oy + (float)py));
            var (pos, best, second) = RegionSearchZ(A, (rx, ry), B, Band((rtx0, rty0, rtx1, rty1), p0, p1, 5), weights);
            if (!(pos.X > 0 && pos.Y > 0) || mask[pos.Y * maskStride + pos.X] != 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            float score = best * Scale60, sec = (float)second * Scale60;
            float dx = (float)((offs.X + pos.X) - rx), dy = (float)((offs.Y + pos.Y) - ry);
            float dq = dy * dy + dx * dx;
            float rd = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(dq)).ToScalar(), sd = dq * rd;
            float dist = dq == 0f ? 0f : ((sd * rd) + (-3.0f)) * (-0.5f * sd);
            int octave = dist < 8.0f ? 1 : 2;                             // ucomiss; sbb: 2 − (dist < 8.0), DAT_180685d4c
            float nmx = ((float)pos.X - cBx) * sNorm, nmy = ((float)pos.Y - cBy) * sNorm;
            float ratio = Ratio(score, sec);
            bool ok = ratio < Ratio1 && score < ScoreMax;
            float predX = pf.X * invScale, predY = pf.Y * invScale;       // the far projection, scaled to level coords
            if (bidir)
            {
                int bx0 = Math.Max(rx - 3, 4), by0 = Math.Max(ry - 3, 4), bx1 = Math.Min(A.W - 4, rx + 4), by1 = Math.Min(A.H - 4, ry + 4);
                var (rp, rb, rs) = RegionSearchZ(B, pos, A, Region.Rect(bx0, by0, bx1, by1), weights);
                float rbf = rb * Scale192, rsf = (float)rs * Scale192;    // DAT_1806bbad0 regardless of the mono scale
                ok &= Ratio(rbf, rsf) < Ratio2 && rbf < ScoreMax && Math.Abs(rp.X - rx) < 2 && Math.Abs(rp.Y - ry) < 2;
            }
            outp[i] = new MatchedPoint { RefIdx = i, Mx = pos.X, My = pos.Y, PredX = predX, PredY = predY, NmX = nmx, NmY = nmy, Score = score, Ratio = ratio, Status = ok ? 4 : 3, Octave = octave };
        }
        PostPass(outp, viewA, viewB);
        return outp;
    }

    /// <summary>`FUN_1802f01a0`: template from `refImg` at `refPt`, radius-r search in `tgtImg` around `pred` (margin 4).</summary>
    public static ((int X, int Y) Pos, int Best, int Second) RadiusSearch(Rgba8Image refImg, (int X, int Y) refPt, Rgba8Image tgtImg, (int X, int Y) pred, int r, ushort[] weights)
    {
        int x0 = pred.X - r; if (x0 <= 3) x0 = 4;
        int y0 = pred.Y - r; if (y0 <= 3) y0 = 4;
        int x1 = Math.Min(tgtImg.W - 4, pred.X + r + 1), y1 = Math.Min(tgtImg.H - 4, pred.Y + r + 1);
        int best = int.MaxValue, second = int.MaxValue; var pos = (-1, -1);
        foreach (var (cx, cy) in Region.Rect(x0, y0, x1, y1).Walk())
        {
            int s = Sad8(refImg, refPt.X, refPt.Y, tgtImg, cx, cy, weights);
            if (s < second) { if (s < best) { second = best; best = s; pos = (cx, cy); } else second = s; }
        }
        return (pos, best, second);
    }

    /// <summary>`FUN_1802ffc70`: `H·(x,y,1)` with a reciprocal multiply.</summary>
    public static (float X, float Y) Project(float[] H, float x, float y)
    {
        float nx = (H[1] * y + H[0] * x) + H[2], ny = (H[4] * y + H[3] * x) + H[5], d = (H[7] * y + H[6] * x) + H[8];
        float r = 1.0f / d;
        return (r * nx, ny * r);
    }

    /// <summary>9×9 Welford standard deviation of channel 0 around (x,y) (`rsqrtss` + one Newton step), as in lambda_1.</summary>
    public static float TextureStd(Rgba8Image img, int x, int y)
    {
        float n = 0f, mean = 0f, m2 = 0f, inv = 0f;
        for (int yy = y - 4; yy <= y + 4; yy++)
            for (int xx = x - 4; xx <= x + 4; xx++)
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

    static float Ratio(float best, float second) => (float)((double)best / ((double)second + RatioEps));

    /// <summary>`matchFeaturesPerLevelLowerCams&lt;8&gt;` for one level: A = reference pyramid level, B = target level, `mask`
    /// (1 byte/px, non-zero = saturated), `offs` = alignment offset (pointB = pointA − offs), `farScene` selects radius 3/8.</summary>
    public static MatchedPoint[] MatchLevel(Rgba8Image A, Rgba8Image B, byte[] mask, int maskStride, (int X, int Y) offs, FeaturePoint[] feats,
        MatchView viewA, MatchView viewB, bool farScene, bool bidir, bool mono = false)
    {
        var weights = mono ? WeightsMono : WeightsColour; float Scale60 = mono ? Scale64 : Scale192;
        int rrx0 = Math.Max(offs.X, 4), rry0 = Math.Max(offs.Y, 4), rrx1 = Math.Min(B.W + offs.X, A.W - 4), rry1 = Math.Min(B.H + offs.Y, A.H - 4);
        int rtx0 = Math.Max(-offs.X, 4), rty0 = Math.Max(-offs.Y, 4), rtx1 = Math.Min(A.W - offs.X, B.W - 4), rty1 = Math.Min(A.H - offs.Y, B.H - 4);
        float cBx = (float)B.W * 0.5f, cBy = (float)B.H * 0.5f, sPix = (float)B.W * 0.5f, sNorm = 2.0f / (float)B.W;
        int radiusB = farScene ? 3 : 8;
        var outp = new MatchedPoint[feats.Length];
        for (int i = 0; i < feats.Length; i++)
        {
            var f = feats[i];
            var refPt = ((int)f.X, (int)f.Y);
            if (!(refPt.Item1 >= rrx0 && refPt.Item1 < rrx1 && refPt.Item2 >= rry0 && refPt.Item2 < rry1)) { outp[i] = MatchedPoint.Fail(i); continue; }
            bool okA = false, okB = false; (int X, int Y) posA = (-1, -1), posB = (-1, -1); int bestA = 0, secA = 0, bestB = 0, secB = 0;
            (float X, float Y) predA = default, predB = default;
            if (viewA.Enabled)
            {
                var p = Project(viewA.H, f.Nx, f.Ny); predA = (p.X * sPix + cBx, p.Y * sPix + cBy);
                var ia = ((int)MathF.Round(predA.X, MidpointRounding.AwayFromZero), (int)MathF.Round(predA.Y, MidpointRounding.AwayFromZero));
                if (ia.Item1 >= rtx0 && ia.Item1 < rtx1 && ia.Item2 >= rty0 && ia.Item2 < rty1)
                { (posA, bestA, secA) = RadiusSearch(A, refPt, B, ia, 10, weights); okA = posA.X > 0; }
            }
            if (viewB.Enabled)
            {
                var p = Project(viewB.H, f.Nx, f.Ny); predB = (p.X * sPix + cBx, p.Y * sPix + cBy);
                var ib = ((int)MathF.Round(predB.X, MidpointRounding.AwayFromZero), (int)MathF.Round(predB.Y, MidpointRounding.AwayFromZero));
                if (ib.Item1 >= rtx0 && ib.Item1 < rtx1 && ib.Item2 >= rty0 && ib.Item2 < rty1)
                { (posB, bestB, secB) = RadiusSearch(A, refPt, B, ib, radiusB, weights); okB = posB.X > 0; }
            }
            if (!((!viewA.Enabled || okA) && (!viewB.Enabled || okB))) { outp[i] = MatchedPoint.Fail(i); continue; }
            bool useA;
            if (okA && !okB) useA = true; else if (okB && !okA) useA = false; else useA = bestA < bestB;
            var pos = useA ? posA : posB; int best = useA ? bestA : bestB, second = useA ? secA : secB; var pred = useA ? predA : predB; int octave = useA ? 1 : 2;
            if (mask[pos.Y * maskStride + pos.X] != 0) { outp[i] = MatchedPoint.Fail(i); continue; }
            float nmx = ((float)pos.X - cBx) * sNorm, nmy = ((float)pos.Y - cBy) * sNorm;
            float score = best * Scale60, sec = second * Scale60;
            float ratio = Ratio(score, sec);
            bool ok = ratio < Ratio1 && score < ScoreMax;
            var rec = new MatchedPoint { RefIdx = i, Mx = pos.X, My = pos.Y, PredX = pred.X, PredY = pred.Y, NmX = nmx, NmY = nmy, Score = score, Ratio = ratio, Octave = octave };
            if (TextureStd(B, pos.X, pos.Y) < StdMin) rec.Status = 0;
            else if (!ok) rec.Status = 3;
            else if (bidir)
            {
                var (rp, rb, rs) = RadiusSearch(B, pos, A, refPt, 3, weights);
                float rbf = rb * Scale60, rsf = rs * Scale60;
                bool good = Ratio(rbf, rsf) < Ratio2 && rbf < ScoreMax && Math.Abs(rp.X - refPt.Item1) < 2 && Math.Abs(rp.Y - refPt.Item2) < 2;
                rec.Status = good ? 4 : 3;
            }
            else rec.Status = 4;
            outp[i] = rec;
        }
        PostPass(outp, viewA, viewB);
        return outp;
    }

    /// <summary>`FUN_1802e7200`: per enabled view, if ≤ 7 good matches promote every weak one.</summary>
    public static void PostPass(MatchedPoint[] m, MatchView viewA, MatchView viewB)
    {
        foreach (var v in new[] { viewA, viewB })
        {
            if (!v.Enabled) continue;
            int good = 0; foreach (var r in m) if (r.Status == 4 && r.Octave == v.Id) good++;
            if (good <= 7) for (int i = 0; i < m.Length; i++) if (m[i].Status == 3 && m[i].Octave == v.Id) m[i].Status = 4;
        }
    }
}
