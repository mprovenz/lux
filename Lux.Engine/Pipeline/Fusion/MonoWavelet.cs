using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// The float 16×16 block transform of the mono merge kernel `FUN_1801ee0b0` (spec `a-monofusion.md` §6):
/// forward `FUN_1801ed1f0` (level-1 rows inline → `FUN_1801d9300` level-1 columns → `FUN_1801d8e90` = `FUN_1801d9590` level-2 rows +
/// level-2 columns → `FUN_1801f1e80` level-3 rows → `FUN_1801f20b0` level-3 columns → fused level-4 tail), inverse `FUN_1801ed670`
/// (fused level-4 head → `FUN_1801f22e0` → `FUN_1801f24e0` → `FUN_1801f26e0` (= `FUN_1801f2ad0` + level-2 columns) → level-1 rows inline →
/// `FUN_1801f2c00`). Same 5/3-lifting as <see cref="BayerWavelet"/> with these machine-form differences: every pass except the level-1 rows
/// computes the last detail as `(x[n−1] − x[n−2])·c1` and the inverse first sample as `(s0 − d0)·c1` (subtract first); level 4 uses the
/// fused forms with the folded constants 0x3f3504f2 / 0x3efffffe.
/// </summary>
public static class MonoWavelet
{
    static readonly float C0 = BitConverter.Int32BitsToSingle(0x3fb504f3);   // √2         DAT_180682460 / 1806aeb40
    static readonly float C1 = BitConverter.Int32BitsToSingle(0x3f3504f3);   // 1/√2       DAT_1806aeb90 / 1806aeb50
    static readonly float C3 = BitConverter.Int32BitsToSingle(0x3eb504f3);   // 1/(2√2)    DAT_1806aeb94 / 1806aeb60
    static readonly float C2 = BitConverter.Int32BitsToSingle(0x3effffff);   // 0.49999997 DAT_1806aeb98 / 1806aeb70
    static readonly float C4 = BitConverter.Int32BitsToSingle(0x3f7fffff);   // 0.99999994 DAT_1806aeb9c / 1806aeb80
    static readonly float C1f = BitConverter.Int32BitsToSingle(0x3f3504f2);  // DAT_1806aeba0 (folded c1·c4)
    static readonly float C2f = BitConverter.Int32BitsToSingle(0x3efffffe);  // DAT_1806aeba4
    const float Half = 0.5f;                                                  // DAT_180682404 / 180683140

    public static void Forward(float[] a)
    {
        if (a.Length != 256) throw new ArgumentException("16x16 block expected", nameof(a));
        for (int r = 0; r < 16; r++) ForwardLine(a, r * 16, 1, 16, false);        // 1801ed1f0 loop: vec4 forms
        for (int c = 0; c < 16; c++) ForwardLine(a, c, 16, 16, true);             // 1801d9300
        for (int r = 0; r < 16; r += 2) ForwardLine(a, r * 16, 2, 8, true);       // 1801d9590
        for (int c = 0; c < 16; c += 2) ForwardLine(a, c, 32, 8, true);           // 1801d8e90 (cols 0–6 SIMD, 8–14 scalar: same forms)
        for (int r = 0; r < 16; r += 4) ForwardLine(a, r * 16, 4, 4, true);       // 1801f1e80
        for (int c = 0; c < 16; c += 4) ForwardLine(a, c, 64, 4, true);           // 1801f20b0
        // level 4 fused tail (1801ed1f0 end)
        float d0 = a[8] - a[0];
        float s0 = d0 * C1f + a[0] * C0;
        float d1 = a[0x88] - a[0x80];
        float t = (a[0x80] * C0 - s0) + d1 * C1f;
        a[0x80] = C1 * t;
        a[0] = t * C1f + s0 * C0;
        d1 = d1 - d0;
        a[0x88] = C2 * d1;
        a[8] = d1 * C2f + d0 * C4;
    }

    public static void Inverse(float[] a)
    {
        if (a.Length != 256) throw new ArgumentException("16x16 block expected", nameof(a));
        // level 4 fused head (1801ed670 start)
        {
            float e = a[0] - a[8];
            float f = e * C1;
            float g = a[8] * C0;
            float h = a[0x80] - a[0x88];
            float i = a[0x88] * C0 + h * C1;
            e = (e - h) * C2;
            a[0] = e;
            a[0x80] = h * C4 + e;
            float j = ((g + f) - i) * C1;
            a[8] = j;
            a[0x88] = i * C0 + j;
        }
        for (int r = 0; r < 16; r += 4) InverseLine(a, r * 16, 4, 4, true);       // 1801f22e0
        for (int c = 0; c < 16; c += 4) InverseLine(a, c, 64, 4, true);           // 1801f24e0
        for (int r = 0; r < 16; r += 2) InverseLine(a, r * 16, 2, 8, true);       // 1801f2ad0
        for (int c = 0; c < 16; c += 2) InverseLine(a, c, 32, 8, true);           // 1801f26e0
        for (int r = 0; r < 16; r++) InverseLine(a, r * 16, 1, 16, false);        // 1801ed670 loop: vec4 forms
        for (int c = 0; c < 16; c++) InverseLine(a, c, 16, 16, true);             // 1801f2c00
    }

    /// <summary>Forward lifting on `x[k] = a[b + k·st]`: `d_last`, `d[k] = x[2k+1]·c1 − (x[2k+2] + x[2k])·c3`, `s[k] = (d[k−1] + d[k])·c2 + x[2k]·c0`,
    /// `s0 = d0·c4 + x0·c0`; `d_last = (x[n−1] − x[n−2])·c1` when <paramref name="subMul"/>, else `x[n−1]·c1 − x[n−2]·c1`.</summary>
    static void ForwardLine(float[] a, int b, int st, int n, bool subMul)
    {
        int h = n >> 1;
        int last = b + (n - 1) * st, prev = b + (n - 2) * st;
        a[last] = subMul ? (a[last] - a[prev]) * C1 : a[last] * C1 - a[prev] * C1;
        for (int k = 0; k < h - 1; k++)
            a[b + (2 * k + 1) * st] = a[b + (2 * k + 1) * st] * C1 - (a[b + (2 * k + 2) * st] + a[b + 2 * k * st]) * C3;
        for (int k = 1; k < h; k++)
            a[b + 2 * k * st] = (a[b + (2 * k - 1) * st] + a[b + (2 * k + 1) * st]) * C2 + a[b + 2 * k * st] * C0;
        a[b] = a[b + st] * C4 + a[b] * C0;
    }

    /// <summary>Inverse lifting: `x0 = (s0 − d0)·c1` (or the two-mul form), `x[2k] = s[k]·c1 − (d[k] + d[k−1])·c3`,
    /// `x[2k+1] = (x[2k] + x[2k+2])·0.5 + d[k]·c0`, `x[n−1] = d·c0 + x[n−2]`.</summary>
    static void InverseLine(float[] a, int b, int st, int n, bool subMul)
    {
        int h = n >> 1;
        a[b] = subMul ? (a[b] - a[b + st]) * C1 : a[b] * C1 - a[b + st] * C1;
        for (int k = 1; k < h; k++)
            a[b + 2 * k * st] = a[b + 2 * k * st] * C1 - (a[b + (2 * k + 1) * st] + a[b + (2 * k - 1) * st]) * C3;
        for (int k = 0; k < h - 1; k++)
            a[b + (2 * k + 1) * st] = (a[b + 2 * k * st] + a[b + (2 * k + 2) * st]) * Half + a[b + (2 * k + 1) * st] * C0;
        a[b + (n - 1) * st] = a[b + (n - 1) * st] * C0 + a[b + (n - 2) * st];
    }
}

/// <summary>Helpers of the mono merge kernel `FUN_1801ee0b0` (spec a-monofusion §5): float 16×16 blocks, row-major.</summary>
public static class MonoMerge
{
    /// <summary>The 16×16 float wavelet-noise table (.rdata 0x1806b2be0, referenced through the static `Image&lt;float&gt;` header at 0x1806b1910)
    /// = lane 0 of the vec4 table `BayerMerge.NoiseGain` (verified identical, 2026-08-27).</summary>
    public static readonly float[] NoiseGain = BayerMerge.NoiseGain.Select(v => v.R).ToArray();

    static readonly float Inv256 = BitConverter.Int32BitsToSingle(0x3b800000);   // DAT_1806aeb28
    static readonly float Tenth = BitConverter.Int32BitsToSingle(0x3dcccccd);    // DAT_1806a30dc
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x3727c5ac);      // DAT_180682620 = 1e-5
    static readonly float MinusHalf = BitConverter.Int32BitsToSingle(unchecked((int)0xbf000000));
    static readonly float MinusThree = BitConverter.Int32BitsToSingle(unchecked((int)0xc0400000));

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Rcp(float d) => Sse.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar();
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Rsqrt(float d) => Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(d)).ToScalar();
    /// <summary>`rsqrtss` + Newton: `t = n·r; k = ((t·r) + (−3))·((−0.5)·t)`; 0 when n == 0.</summary>
    public static float RsqrtNR(float n)
    {
        float r = Rsqrt(n), t = n * r;
        float k = ((t * r) + MinusThree) * (MinusHalf * t);
        return n == 0f ? 0f : k;
    }

    /// <summary>`FUN_1801d6ad0(S, &amp;q, Rw, T, nz)`: per element `d = Rw − S; d2 = d·d; den = T·nz + d2; r = rcpps(den); t = r·d2;
    /// S = Rw·t + (1 − t)·S`; `q = 256 − Σ t` (per group of 4: `(t3 + t1) + (t2 + t0)`, disasm 1801d6b26–1801d6b3f), returned as `q·(1/256)`.</summary>
    public static float Shrink(float[] S, float[] Rw, float nz)
    {
        var T = NoiseGain;
        float q = 256f;
        var nzV = Vector128.Create(nz);
        for (int i = 0; i < 256; i += 4)
        {
            var s = Vector128.Create(S[i], S[i + 1], S[i + 2], S[i + 3]);
            var rw = Vector128.Create(Rw[i], Rw[i + 1], Rw[i + 2], Rw[i + 3]);
            var d = rw - s;
            var d2 = d * d;
            var den = Vector128.Create(T[i], T[i + 1], T[i + 2], T[i + 3]) * nzV + d2;
            var r = Sse.Reciprocal(den);
            var t = r * d2;
            var o = rw * t + (Vector128.Create(1f) - t) * s;
            S[i] = o.GetElement(0); S[i + 1] = o.GetElement(1); S[i + 2] = o.GetElement(2); S[i + 3] = o.GetElement(3);
            q = q - ((t.GetElement(3) + t.GetElement(1)) + (t.GetElement(2) + t.GetElement(0)));   // shufpd 1 / addps / shufps 0xb1 / addps (1801d6b26–1801d6b3f)
        }
        return q * Inv256;
    }

    /// <summary>`FUN_1801d79d0(view, prm, g)`: `mean = Σ rcpss(x + 0.1)² / n; m = rcpss(mean); s = rsqrtNR-sqrt(m)`;
    /// `v = max(((s − black)/g + black)·(1/white), black/white)`; `σ² = max(v·A + B, 1e-5)`; returns `(white·g)²·σ²`.</summary>
    public static float BlockNoise(float[] img, int stride, int x0, int y0, int w, int h, float A, float B, float black, float white, float g)
    {
        float invW = 1f / white;
        float floor = black * invW;
        float sum = 0f;
        for (int y = 0; y < h; y++)
        {
            int row = (y0 + y) * stride + x0;
            for (int x = 0; x < w; x++) { float r = Rcp(img[row + x] + Tenth); sum = sum + r * r; }
        }
        float mean = sum / (float)(w * h);
        float m = Rcp(mean);
        float r2 = Rsqrt(m);
        float t = m * r2;
        float s = ((t * r2) + MinusThree) * (MinusHalf * t);   // 1801d7a9f–1801d7abb: xmm3 = (−0.5)·t; xmm6 = (t·r) + (−3); s = xmm6·xmm3
        if (m == 0f) s = 0f;
        float v = (((s - black) / g + black) * invW);
        if (v <= floor) v = floor;
        float sig = v * A + B;
        if (sig <= Eps) sig = Eps;
        float wg = white * g;
        return wg * wg * sig;
    }

    /// <summary>`FUN_1801ecee0(block, img, B)`: the 16×16 block at (bx, by) of an image indexed by <paramref name="rect"/> (the image's rect
    /// fields: pixel (0,0) of the buffer is at rect.X0/Y0 in block coordinates): rows clamped to the image, columns left of it read the first
    /// column, right of it the last one. Precondition (as in Lumen): the block intersects the image.</summary>
    public static void ExtractBlock(float[] img, int stride, RectI rect, int bx, int by, float[] block)
    {
        int w = rect.Width, h = rect.Height;
        for (int r = 0; r < 16; r++)
        {
            int y = by + r - rect.Y0;
            if (y < 0) y = 0;
            if (y > h - 1) y = h - 1;
            int row = y * stride;
            for (int c = 0; c < 16; c++)
            {
                int x = bx + c - rect.X0;
                if (x < 0) x = 0;
                else if (x >= w) x = w - 1;
                block[r * 16 + c] = img[row + x];
            }
        }
    }

    /// <summary>`FUN_1801d63c0`: block fully inside (1801d64fb–1801d6576): `dst += (block·hann_x)·hann_y` (mulps block,hannV; mulps ·,hy; addps);
    /// clipped (1801d664b–1801d6660): `dst += (hann_y·block)·hann_x` (mulss hy,block; mulss ·,hx; addss).</summary>
    public static void AddHann(float[] dst, int w, int h, int bx, int by, float[] block, float[] hann)
    {
        int x0 = Math.Max(bx, 0), y0 = Math.Max(by, 0), x1 = Math.Min(bx + 16, w), y1 = Math.Min(by + 16, h);
        bool full = x1 - x0 == 16 && y1 - y0 == 16;
        for (int y = y0; y < y1; y++)
        {
            float hy = hann[y - by];
            int row = y * w;
            if (full) for (int x = x0; x < x1; x++) dst[row + x] = (block[(y - by) * 16 + (x - bx)] * hann[x - bx]) * hy + dst[row + x];
            else for (int x = x0; x < x1; x++) dst[row + x] = (hy * block[(y - by) * 16 + (x - bx)]) * hann[x - bx] + dst[row + x];
        }
    }

    /// <summary>`FUN_1801d6750`: the weight writer. Fast path (1801d6884–1801d689b): `dst += (hann_x·s)·hann_y` (mulps hannV,s(bcast); mulps ·,hy; addps);
    /// clipped path (1801d69d4–1801d69e7): `dst += (hann_y·s)·hann_x`.</summary>
    public static void AddHannScalar(float[] dst, int w, int h, int bx, int by, float s, float[] hann)
    {
        int x0 = Math.Max(bx, 0), y0 = Math.Max(by, 0), x1 = Math.Min(bx + 16, w), y1 = Math.Min(by + 16, h);
        bool full = x1 - x0 == 16 && y1 - y0 == 16;
        for (int y = y0; y < y1; y++)
        {
            float hy = hann[y - by];
            int row = y * w;
            if (full) for (int x = x0; x < x1; x++) dst[row + x] = (hann[x - bx] * s) * hy + dst[row + x];
            else for (int x = x0; x < x1; x++) dst[row + x] = (hy * s) * hann[x - bx] + dst[row + x];
        }
    }
}
