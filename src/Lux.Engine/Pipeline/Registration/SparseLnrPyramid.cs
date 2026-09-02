namespace Lux.Engine.Pipeline.Registration;

/// <summary>An RGBA8 pyramid level as `FUN_1802e62f0` builds it: `Pad` zero pixels around the image are addressable
/// (rect (−pad, −pad, w+pad, h+pad)); level 0 is a view of the source (pad 0).</summary>
public sealed class PaddedRgba8
{
    public readonly byte[] Data; public readonly int W, H, Pad, Stride;   // stride in pixels of the padded buffer
    public PaddedRgba8(int w, int h, int pad) { W = w; H = h; Pad = pad; Stride = w + 2 * pad; Data = new byte[Stride * (h + 2 * pad) * 4]; }
    public PaddedRgba8(byte[] dense, int w, int h) { W = w; H = h; Pad = 0; Stride = w; Data = dense; }
    public int Offset(int x, int y) => ((y + Pad) * Stride + (x + Pad)) * 4;
    public bool InRect(int x, int y) => x >= -Pad && x < W + Pad && y >= -Pad && y < H + Pad;
    /// <summary>Dense w×h copy of the valid region.</summary>
    public byte[] Dense()
    {
        var d = new byte[W * H * 4];
        for (int y = 0; y < H; y++) Array.Copy(Data, Offset(0, y), d, y * W * 4, W * 4);
        return d;
    }
    public Rgba8Image AsImage() => new(Data, W, H, Stride, Pad);
}

/// <summary>
/// Image-side helpers of `lt::SparseLNR`: the Gaussian ½ pyramid (`ImageGaussianFilterAndSubSample&lt;vec4x8ui&gt;`
/// 1800149c0, taps 0.05/0.25/0.4, clamp-to-edge, two association variants), the saturation mask (`FUN_1802e7cc0`:
/// channel 0 → 9×9 in-bounds box mean truncated → `≥ (byte)(sat·0.9)`), and `PyramidAlignment` (`FUN_1802f5b10` /
/// `alignImage` 1802f4e70: coarse-to-fine integer offset search with weighted L1 costs; top level mixes a gradient cost).
/// </summary>
public static class SparseLnrPyramid
{
    static readonly float K0 = BitConverter.Int32BitsToSingle(0x3d4ccccd), K1 = BitConverter.Int32BitsToSingle(0x3e800000), K2 = BitConverter.Int32BitsToSingle(0x3ecccccd);

    /// <summary>`FUN_1802e62f0`: level 0 = the source view; each further level = ½ downsample with an 8-pixel zero ring.</summary>
    public static PaddedRgba8[] Build(byte[] level0, int w, int h, int nLevels, int pad = 8)
    {
        var pyr = new PaddedRgba8[nLevels];
        pyr[0] = new PaddedRgba8(level0, w, h);
        for (int l = 1; l < nLevels; l++) pyr[l] = Downsample(pyr[l - 1], pad);
        return pyr;
    }

    /// <summary>`ImageGaussianFilterAndSubSample&lt;vec4x8ui&gt;`: output (X,Y) ← centre (2X,2Y), 5 taps, source-rect clamp.</summary>
    public static PaddedRgba8 Downsample(PaddedRgba8 src, int pad)
    {
        int w = src.W, h = src.H, w2 = (w + 1) / 2, h2 = (h + 1) / 2;
        var dst = new PaddedRgba8(w2, h2, pad);
        var t = new float[w * 4];
        for (int Y = 0; Y < h2; Y++)
        {
            int y = 2 * Y;
            bool vfast = y - 2 >= 0 && y + 2 <= h - 1;
            int r0 = Math.Clamp(y - 2, 0, h - 1), r1 = Math.Clamp(y - 1, 0, h - 1), r2 = Math.Clamp(y, 0, h - 1), r3 = Math.Clamp(y + 1, 0, h - 1), r4 = Math.Clamp(y + 2, 0, h - 1);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    float s0 = K0 * src.Data[src.Offset(x, r0) + c], s1 = K1 * src.Data[src.Offset(x, r1) + c], s2 = K2 * src.Data[src.Offset(x, r2) + c];
                    float s3 = K1 * src.Data[src.Offset(x, r3) + c], s4 = K0 * src.Data[src.Offset(x, r4) + c];
                    t[x * 4 + c] = vfast ? (s4 + (s0 + s1)) + (s2 + s3) : s4 + ((s2 + s3) + (s0 + s1));
                }
            for (int X = 0; X < w2; X++)
            {
                int x = 2 * X;
                bool hfast = x >= 2 && x < w - 2;
                int c0 = Math.Clamp(x - 2, 0, w - 1), c1 = Math.Clamp(x - 1, 0, w - 1), c2 = Math.Clamp(x, 0, w - 1), c3 = Math.Clamp(x + 1, 0, w - 1), c4 = Math.Clamp(x + 2, 0, w - 1);
                for (int c = 0; c < 4; c++)
                {
                    float u0 = K0 * t[c0 * 4 + c], u1 = K1 * t[c1 * 4 + c], u2 = K2 * t[c2 * 4 + c], u3 = K1 * t[c3 * 4 + c], u4 = K0 * t[c4 * 4 + c];
                    float o = hfast ? (u4 + (u0 + u2)) + (u1 + u3) : u4 + ((u2 + u3) + (u0 + u1));
                    int v = (int)MathF.Round(o, MidpointRounding.ToEven);   // cvtps2dq (RNE) → packssdw/packuswb saturation
                    dst.Data[dst.Offset(X, Y) + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>`FUN_1802e7cc0(this, level, 9)`: mask byte = 0xff where the 9×9 in-bounds box mean of channel 0 ≥ (byte)(int)(sat·0.9f).</summary>
    public static byte[] SaturationMask(PaddedRgba8 img, float satLevel, int box = 9)
    {
        int w = img.W, h = img.H; int hb = box / 2;
        byte thr = (byte)(int)(satLevel * BitConverter.Int32BitsToSingle(0x3f666666));
        var mask = new byte[w * h];
        var col = new int[w];   // column sums over the in-bounds rows of the current window
        for (int y = 0; y < h; y++)
        {
            int ry0 = Math.Max(y - hb, 0), ry1 = Math.Min(y + hb + 1, h);
            float rows = ry1 - ry0;
            for (int x = 0; x < w; x++) { int s = 0; for (int r = ry0; r < ry1; r++) s += img.Data[img.Offset(x, r)]; col[x] = s; }
            for (int x = 0; x < w; x++)
            {
                int cx0 = Math.Max(x - hb, 0), cx1 = Math.Min(x + hb + 1, w);
                int S = 0; for (int c = cx0; c < cx1; c++) S += col[c];
                int cols = cx1 - cx0;
                float recip = 1.0f / ((float)cols * rows);
                byte blur = (byte)(int)((float)S * recip);
                mask[y * w + x] = thr > blur ? (byte)0 : (byte)0xff;
            }
        }
        return mask;
    }

    /// <summary>Channel-0 gradient magnitude `|A| + |B|` (3×3 separable Sobel-like, clamp-to-edge; integer-valued floats).</summary>
    public static float[] GradientImage(PaddedRgba8 img)
    {
        int w = img.W, h = img.H; var g = new float[w * h];
        int S(int x, int y) => img.Data[img.Offset(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1))];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // A: kx = (1,2,1), ky = (−1,0,1); B: kx = (−1,0,1), ky = (1,2,1)
                int a = (S(x - 1, y + 1) + 2 * S(x, y + 1) + S(x + 1, y + 1)) - (S(x - 1, y - 1) + 2 * S(x, y - 1) + S(x + 1, y - 1));
                int b = (S(x + 1, y - 1) + 2 * S(x + 1, y) + S(x + 1, y + 1)) - (S(x - 1, y - 1) + 2 * S(x - 1, y) + S(x - 1, y + 1));
                g[y * w + x] = (float)Math.Abs(a) + (float)Math.Abs(b);
            }
        return g;
    }

    public static readonly float[] AlignWeightsColour = { 2.0f, 0.5f, 0.5f, 0.0f };   // DAT_180835310
    public static float[] AlignWeightsMono = { 3.0f, 0.0f, 0.0f, 0.0f };               // DAT_180835300

    /// <summary>`FUN_1802f5b10`: coarse-to-fine offsets (pointB = pointA − offs) per level; returns (offsets, ok = mean score &lt; 100).</summary>
    public static ((int X, int Y)[] Offs, bool Ok) Align(PaddedRgba8[] refPyr, PaddedRgba8[] srcPyr, (float X, float Y) center, (float X, float Y) guessNorm, float[] weights)
    {
        int n = refPyr.Length; if (srcPyr.Length != n) throw new InvalidOperationException("ref/src pyr size mismatch!");
        var res = new (int X, int Y)[n];
        int top = n - 1;
        var refGrad = GradientImage(refPyr[top]); var srcGrad = GradientImage(srcPyr[top]);
        var guess = ((int)((float)srcPyr[top].W * guessNorm.X), (int)((float)srcPyr[top].H * guessNorm.Y));
        int radius = (int)((float)refPyr[top].W * BitConverter.Int32BitsToSingle(0x3dcccccd));
        var (r, score) = AlignImage(refPyr[top], srcPyr[top], refGrad, srcGrad, center, guess, radius, weights, true);
        res[top] = r; float sum = score;
        for (int l = top - 1; l >= 0; l--)
        {
            guess = (2 * res[l + 1].X, 2 * res[l + 1].Y);
            (r, score) = AlignImage(refPyr[l], srcPyr[l], null, null, center, guess, 1, weights, false);
            res[l] = r; sum += score;
        }
        return (res, sum / (float)n < 100.0f);
    }

    static ((int X, int Y) Res, float Score) AlignImage(PaddedRgba8 refL, PaddedRgba8 srcL, float[]? refGrad, float[]? srcGrad, (float X, float Y) center, (int X, int Y) guess, int radius, float[] weights, bool top)
    {
        int W = refL.W, H = refL.H;
        int margin = (int)((float)W * BitConverter.Int32BitsToSingle(0x3da3d70a));
        int x0 = (int)(center.X * (float)W) - margin, y0 = (int)(center.Y * (float)H) - margin, x1 = x0 + 2 * margin, y1 = y0 + 2 * margin;
        int cx0 = Math.Max(x0, 0), cy0 = Math.Max(y0, 0), cx1 = Math.Min(x1, W), cy1 = Math.Min(y1, H);
        int wc = Math.Max(cx1 - cx0, 0), hc = Math.Max(cy1 - cy0, 0);
        float best = float.MaxValue; int bdx = 0, bdy = 0;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var offs = (guess.X + dx, guess.Y + dy);
                float cost = CostRgba(refL, srcL, cx0, cy0, wc, hc, x0, y0, offs, weights);
                if (top) cost = CostGrad(refGrad!, srcGrad!, refL.W, srcL.W, srcL.H, cx0, cy0, wc, hc, x0, y0, offs) * BitConverter.Int32BitsToSingle(0x3f666666) + cost * BitConverter.Int32BitsToSingle(0x3dccccd0);
                if (cost < best) { best = cost; bdx = dx; bdy = dy; }
            }
        return ((bdx + guess.X, bdy + guess.Y), best);
    }

    /// <summary>`FUN_1802f6470`: weighted L1 of the ref crop against the src window at (R.x0 − offs.x, R.y0 − offs.y); the src rect includes the zero ring.</summary>
    static float CostRgba(PaddedRgba8 refL, PaddedRgba8 srcL, int cx0, int cy0, int wc, int hc, int rx0, int ry0, (int X, int Y) offs, float[] w)
    {
        int ox = rx0 - offs.X, oy = ry0 - offs.Y;
        bool tl = srcL.InRect(ox, oy), br = srcL.InRect(ox + wc - 1, oy + hc - 1);
        if (!tl && !br) return float.MaxValue;
        float sum = 0f; int count = 0;
        for (int y = 0; y < hc; y++)
            for (int x = 0; x < wc; x++)
            {
                int sx = ox + x, sy = oy + y;
                if (!(tl && br) && !srcL.InRect(sx, sy)) continue;
                int ro = refL.Offset(cx0 + x, cy0 + y), so = srcL.Offset(sx, sy);
                float l0 = w[0] * MathF.Abs((float)refL.Data[ro] - srcL.Data[so]), l1 = w[1] * MathF.Abs((float)refL.Data[ro + 1] - srcL.Data[so + 1]), l2 = w[2] * MathF.Abs((float)refL.Data[ro + 2] - srcL.Data[so + 2]);
                sum += l1 + (l0 + l2); count++;
            }
        if (tl && br) return sum / (float)(wc * hc);
        return count < (wc * hc) / 2 ? float.MaxValue : sum / (float)count;
    }

    /// <summary>`FUN_1802f6900`: plain L1 of the gradient images (rect (0,0,w,h), no ring); integer-valued so the summation order is immaterial.</summary>
    static float CostGrad(float[] refG, float[] srcG, int refW, int srcW, int srcH, int cx0, int cy0, int wc, int hc, int rx0, int ry0, (int X, int Y) offs)
    {
        int ox = rx0 - offs.X, oy = ry0 - offs.Y;
        bool In(int x, int y) => x >= 0 && x < srcW && y >= 0 && y < srcH;
        bool tl = In(ox, oy), br = In(ox + wc - 1, oy + hc - 1);
        if (!tl && !br) return float.MaxValue;
        double sum = 0; int count = 0;
        for (int y = 0; y < hc; y++)
            for (int x = 0; x < wc; x++)
            {
                int sx = ox + x, sy = oy + y;
                if (!(tl && br) && !In(sx, sy)) continue;
                sum += Math.Abs(refG[(cy0 + y) * refW + cx0 + x] - srcG[sy * srcW + sx]); count++;
            }
        if (tl && br) return (float)sum / (float)(wc * hc);
        return count < (wc * hc) / 2 ? float.MaxValue : (float)sum / (float)count;
    }
}
