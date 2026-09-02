namespace Lux.Engine.Pipeline.Registration;

/// <summary>One `lt::FeaturePoint` (stride 0x24): integer-valued pixel position (−1 = none), normalised position
/// `((x − w/2)·2/w, (y − h/2)·2/w)`, the Harris response of the tile maximum, three zero floats and a flag byte
/// (1 = the tile had no feature / was outside the margin rect).</summary>
public struct FeaturePoint
{
    public float X, Y, Nx, Ny, Resp, Z0, Z1, Z2;
    public byte Flag;
    public static FeaturePoint None => new() { X = -1f, Y = -1f };
}

/// <summary>
/// `lt::FeatureDetector::findFeatures` (180302fd560 + tile lambda 1802fe280) and `cullFeatures` (1802fddb0 + lambda_2
/// 1802fec70), ported from the decompilation. Input is the 8-bit RGBA stereo image; the detection scalar is either the
/// integer luma `(9797R + 19234G + 3735B + 16384) >> 15` or the raw R byte. Per 16×16 tile: Sobel-like 3×3 separable
/// gradients (image-edge clamp, cross-tile neighbours), structure-tensor products, 5-tap σ 0.7 Gaussian smoothing
/// (clamped at the tile edge), Harris `R = det − 0.05·tr²`, one arg-max per tile if `R > 1e5`. Optional cull to
/// `maxCount` keeps 1–3 per 2×2 tile block by response. Output = the surviving records inside `[margin, w−margin) ×
/// [margin, h−margin)` in tile order.
/// </summary>
public static class FeatureDetector
{
    const float Threshold = 100000.0f;     // DAT_1806bbab0
    const float NegK = -0.05f;             // DAT_1806bcba4
    const float Sigma = 0.7f;              // DAT_1806bcba0

    /// <summary>The detection scalar image (`FUN_1802fdcd0` luma or `FUN_180292520` channel 0), integer-valued floats.</summary>
    public static float[] ToScalar(ReadOnlySpan<byte> rgba, int w, int h, bool channel0)
    {
        var f = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (channel0) f[i] = rgba[o];
            else f[i] = (float)((uint)(9797 * rgba[o] + 19234 * rgba[o + 1] + 3735 * rgba[o + 2] + 16384) >> 15);
        }
        return f;
    }

    public static List<FeaturePoint> FindFeatures(ReadOnlySpan<byte> rgba, int w, int h, int maxCount, int margin, bool channel0)
        => FindFeatures(ToScalar(rgba, w, h, channel0), w, h, maxCount, margin);

    public static List<FeaturePoint> FindFeatures(float[] img, int w, int h, int maxCount, int margin)
    {
        if (w < 16 || h < 16) throw new ArgumentException("FeatureDetector needs an image of at least 16×16 (Lumen has no guard)");
        int gw = w / 16, gh = h / 16;
        var feats = new FeaturePoint[gw * gh];
        for (int i = 0; i < feats.Length; i++) feats[i] = FeaturePoint.None;
        float cy = (float)h * 0.5f, cx = 0.5f * (float)w, scale = 2.0f / (float)w;
        float m = (float)margin, m2 = (float)margin - (float)(margin * 2);
        float rx0 = m, ry0 = m, rx1 = (float)w + m2, ry1 = m2 + (float)h;
        var g = Isp.Stages.PostProcessingLumen.GaussianTaps(5, Sigma);

        // gradients over the whole image (the tile views see their neighbours; only true image borders clamp)
        var gx = new float[w * h]; var gy = new float[w * h];
        var tA = new float[w]; var tB = new float[w];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(y - 1, 0), yp = Math.Min(y + 1, h - 1);
            var sm = img.AsSpan(ym * w, w); var s0 = img.AsSpan(y * w, w); var sp = img.AsSpan(yp * w, w);
            for (int x = 0; x < w; x++)
            {
                tA[x] = (1f * sm[x] + 2f * s0[x]) + 1f * sp[x];      // vertical {1,2,1}
                tB[x] = (1f * sm[x] + 0f * s0[x]) + -1f * sp[x];     // vertical {-1,0,1} reversed
            }
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(x - 1, 0), xp = Math.Min(x + 1, w - 1);
                gx[y * w + x] = (1f * tA[xm] + 0f * tA[x]) + -1f * tA[xp];
                gy[y * w + x] = (1f * tB[xm] + 2f * tB[x]) + 1f * tB[xp];
            }
        }

        var P = new float[16 * 16 * 3]; var T = new float[16 * 16 * 3]; var S = new float[16 * 16 * 3];
        for (int ty = 0; ty < gh; ty++)
            for (int tx = 0; tx < gw; tx++)
            {
                int px0 = tx * 16, py0 = ty * 16;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        float a = gx[(py0 + y) * w + px0 + x], b = gy[(py0 + y) * w + px0 + x];
                        int o = (y * 16 + x) * 3;
                        P[o] = a * a; P[o + 1] = b * b; P[o + 2] = a * b;
                    }
                // separable 5-tap, clamp at the tile edge, accumulate in ascending tap order
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            float acc = 0f;
                            for (int k = 0; k < 5; k++)
                            {
                                int yy = Math.Clamp(y + k - 2, 0, 15);
                                float v = P[(yy * 16 + x) * 3 + c] * g[k];
                                acc = k == 0 ? v : acc + v;
                            }
                            T[(y * 16 + x) * 3 + c] = acc;
                        }
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            float acc = 0f;
                            for (int k = 0; k < 5; k++)
                            {
                                int xx = Math.Clamp(x + k - 2, 0, 15);
                                float v = T[(y * 16 + xx) * 3 + c] * g[k];
                                acc = k == 0 ? v : acc + v;
                            }
                            S[(y * 16 + x) * 3 + c] = acc;
                        }
                float best = Threshold; int bx = -1, by = -1;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        int o = (y * 16 + x) * 3;
                        float ixx = S[o], iyy = S[o + 1], ixy = S[o + 2];
                        float det = ixx * iyy - ixy * ixy;
                        float tr = ixx + iyy;
                        float r = tr * tr * NegK + det;
                        if (r > best) { bx = x; by = y; best = r; }   // ucomiss/cmova + maxss(R, best): NaN never wins
                    }
                float fx, fy;
                if (best > Threshold) { fx = (float)(bx + px0); fy = (float)(by + py0); } else { fx = -1f; fy = -1f; }
                ref var rec = ref feats[tx + ty * gw];
                if (fx < rx0 || fx >= rx1 || fy < ry0 || fy >= ry1) rec = new FeaturePoint { X = -1f, Y = -1f, Nx = -1f, Ny = -1f, Flag = 1 };
                else rec = new FeaturePoint { X = fx, Y = fy, Nx = (fx - cx) * scale, Ny = (fy - cy) * scale, Resp = best };
            }

        if (maxCount > 0) CullFeatures(gw, gh, feats, maxCount);

        var outList = new List<FeaturePoint>();
        foreach (var f in feats)
            if (rx0 <= f.X && f.X < rx1 && ry0 <= f.Y && f.Y < ry1) outList.Add(f);
        return outList;
    }

    /// <summary>`cullFeatures`: keep 1–3 features per 2×2 tile block depending on `maxCount / (n + 1)`.</summary>
    public static void CullFeatures(int gw, int gh, FeaturePoint[] feats, int maxCount)
    {
        int n = 0;
        foreach (var f in feats) if (0.0f < f.X && 0.0f < f.Y) n++;
        float ratio = feats.Length == 0 ? 1.0f * (float)maxCount : (1.0f / (float)(n + 1)) * (float)maxCount;
        if (!(ratio < 1.0f)) return;
        int keep = ratio >= 0.75f ? 3 : ratio >= 0.5f ? 2 : 1;
        int bw = gw / 2, bh = gh / 2;   // Tiler with tile {2,2}: the odd last column/row is never visited by the lambda
        var cand = new List<(float Resp, int Idx)>(4);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int i0 = bx * 2 + by * 2 * gw;
                cand.Clear();
                foreach (int i in new[] { i0, i0 + 1, i0 + gw, i0 + gw + 1 })
                    if (0.0f < feats[i].X && 0.0f < feats[i].Y) cand.Add((feats[i].Resp, i));
                if (cand.Count > keep)
                {
                    // MSVC insertion sort (≤32 elements) with greater-by-resp: stable
                    for (int a = 1; a < cand.Count; a++)
                    {
                        var v = cand[a]; int b = a;
                        while (b > 0 && v.Resp > cand[b - 1].Resp) { cand[b] = cand[b - 1]; b--; }
                        cand[b] = v;
                    }
                    for (int j = keep; j < cand.Count; j++) { feats[cand[j].Idx].X = -1f; feats[cand[j].Idx].Y = -1f; }
                }
            }
    }
}
