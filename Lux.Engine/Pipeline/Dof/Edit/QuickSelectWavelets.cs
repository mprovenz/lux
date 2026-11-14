using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// The edge-avoiding wavelets of quick select (Lumen's pyramid object, mode 1): a weighted red-black
/// lifting scheme on the grey guide. Per level two vec4 weight images are kept — <see cref="WH"/> (axial neighbours: lanes right, left,
/// down, up) and <see cref="WD"/> (diagonal neighbours: lanes down-left, up-left, down-right, up-right) — which hold the <b>predict</b>
/// weights (sum 1) at the predicted pixels and the <b>update</b> weights (sum 0.5) at the updated ones:
/// WH: (x + y) odd = predict, (x + y) even = update;  WD: (odd, odd) = predict, (even, even) = update.
/// Builder `ComputeDistanceTable` → one weight step per level (<see cref="Weights"/>); diffusion <see cref="Diffuse"/>.
/// </summary>
public sealed class EawPyramid
{
    public readonly int Levels;
    public readonly float[][] WH, WD;
    public readonly (int W, int H)[] Dims;
    /// <summary>The transformed level images, kept only when asked for (tests).</summary>
    public readonly float[][]? LevelImages;

    /// <summary>The LUT, type 0: <c>lut[i] = powf(0.001f + (float)i · (1/511.5f), −param)</c>, 1024 entries
    /// (0.001 = 0x3a83126f; CRT `powf`).</summary>
    public static float[] DistanceTable(float param)
    {
        var lut = new float[1024]; float step = BitConverter.Int32BitsToSingle(0x3b002008), eps = BitConverter.Int32BitsToSingle(0x3a83126f);
        for (int i = 0; i < 1024; i++) lut[i] = MuslMath.Powf(eps + (float)i * step, -param);
        return lut;
    }

    /// <summary>The builder (out, image, mode 1, levels, type, param): <c>n = min(floor(log2(min(w, h))) − 1, levels)</c> levels; level
    /// l + 1 works on the even pixels (<see cref="Downsample"/>) of level l's transformed image.</summary>
    public EawPyramid(float[] grey, int w, int h, int levels, float param, bool keepImages = false)
    {
        int n = Math.Min(System.Numerics.BitOperations.Log2((uint)Math.Min(w, h)) - 1, levels);
        if (n < 0) n = 0;
        Levels = n; WH = new float[n][]; WD = new float[n][]; Dims = new (int, int)[n + 1];
        LevelImages = keepImages ? new float[n + 1][] : null;
        var lut = DistanceTable(param);
        var cur = grey; int cw = w, ch = h;
        for (int l = 0; l < n; l++)
        {
            Dims[l] = (cw, ch);
            var q = Weights(cur, cw, ch, lut, out WH[l], out WD[l]);
            if (LevelImages != null) LevelImages[l] = q;
            cur = Downsample(q, cw, ch, out cw, out ch);
        }
        Dims[n] = (cw, ch);
        if (LevelImages != null) LevelImages[n] = cur;
    }

    /// <summary>One pixel of border, reflected without repeating the edge.</summary>
    static float[] Pad(float[] s, int w, int h)
    {
        int W = w + 2, H = h + 2; var p = new float[(long)W * H];
        Rows(0, h, (ya, yb) => { for (int y = ya; y < yb; y++) Array.Copy(s, (long)y * w, p, (long)(y + 1) * W + 1, w); });
        Mirror(p, W, H);
        return p;
    }

    /// <summary>Rewrite the one-pixel border of a padded image from its interior (row 0 = row 2, …, corners from the
    /// diagonal).</summary>
    static void Mirror(float[] p, int W, int H)
    {
        Array.Copy(p, (long)2 * W, p, 0, W);
        Array.Copy(p, (long)(H - 3) * W, p, (long)(H - 1) * W, W);
        for (int y = 1; y < H - 1; y++) { p[(long)y * W] = p[(long)y * W + 2]; p[(long)y * W + W - 1] = p[(long)y * W + W - 3]; }
        p[0] = p[(long)2 * W + 2];
        p[(long)(H - 1) * W] = p[(long)(H - 3) * W + 2];
        p[W - 1] = p[(long)2 * W + W - 3];
        p[(long)(H - 1) * W + W - 1] = p[(long)(H - 3) * W + W - 3];
    }

    static float[] Crop(float[] p, int W, int H)
    {
        int w = W - 2, h = H - 2; var o = new float[(long)w * h];
        Rows(0, h, (ya, yb) => { for (int y = ya; y < yb; y++) Array.Copy(p, (long)(y + 1) * W + 1, o, (long)y * w, w); });
        return o;
    }

    /// <summary>The even pixels (offset (0, 0)), size ((w + 1) / 2, (h + 1) / 2).</summary>
    public static float[] Downsample(float[] s, int w, int h, out int ow, out int oh)
    {
        ow = (w + 1) / 2; oh = (h + 1) / 2; var o = new float[(long)ow * oh];
        for (int y = 0, j = 0; y < h; y += 2, j++)
            for (int x = 0, i = 0; x < w; x += 2, i++) o[(long)j * ow + i] = s[(long)y * w + x];
        return o;
    }

    /// <summary>Rows [r0, r1) in bands over threads: inside one step every updated pixel reads only pixels that step does not write
    /// (the red-black / checkerboard classes), so the rows are independent and the bands give the same values in any order.</summary>
    static void Rows(int r0, int r1, Action<int, int> body)
    {
        int n = r1 - r0; if (n <= 0) return;
        int k = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, n / 16)), per = (n + k - 1) / k;
        if (k == 1) { body(r0, r1); return; }
        Parallel.For(0, k, b => { int a = r0 + b * per, e = Math.Min(r1, a + per); if (a < e) body(a, e); });
    }

    const float DMax = 2.0f, DScale = 511.5f;
    static float Lut(float[] lut, float nb, float c)
    {
        float d = MathF.Abs(nb - c);
        d = d > 0.0f ? d : 0.0f;                 // MAXSS order: NaN gives 0
        d = d < DMax ? d : DMax;                 // MINSS order: NaN gives 2
        return lut[EditMath.Cvtt(d * DScale)];
    }

    /// <summary>
    /// One level of the weighted red-black transform (levelOut, wH, wD, image, lut) on the reflect-padded image P, into
    /// a work image Q whose border is re-mirrored after each step:
    /// 1. (x + y) odd: weights <c>lut·((1 − s·r)·r + r)</c>, r = `rcpps(s)`, s = Σ lut;  <c>Q = P − Σ w·P[n]</c>.
    /// 2. (x + y) even: weights <c>lut·(0.5 / s)</c> (`divss`);  <c>Q = Σ w·Q[n] + P</c>.
    /// 3. (odd, odd): diagonal weights as in 1;  <c>Q −= Σ w·Q[n]</c>.   4. (even, even): diagonal weights as in 2;  <c>Q = Σ w·Q[n] + Q</c>.
    /// The lut index is <c>cvtt(min(max(|P[n] − P[c]|, 0), 2) · 511.5)</c>; every four-term sum is <c>(t0 + t2) + (t1 + t3)</c>.
    /// </summary>
    public static float[] Weights(float[] img, int w, int h, float[] lut, out float[] wh, out float[] wd)
    {
        int W = w + 2, H = h + 2;
        var P = Pad(img, w, h); var Q = new float[(long)W * H];
        wh = new float[(long)w * h * 4]; wd = new float[(long)w * h * 4];
        var whL = wh; var wdL = wd;
        // step 1: predict, (x + y) odd
        Rows(1, H - 1, (ra, rb) => {
        for (int r = ra; r < rb; r++)
        {
            int p = (r - 1) & 1;
            for (int x = 2 - p; x < W - 1; x += 2)
            {
                long c = (long)r * W + x; float pc = P[c];
                float l0 = Lut(lut, P[c + 1], pc), l1 = Lut(lut, P[c - 1], pc), l2 = Lut(lut, P[c + W], pc), l3 = Lut(lut, P[c - W], pc);
                float s = (l0 + l2) + (l1 + l3), rc = IntelApprox.Rcp(s), k = (1.0f - s * rc) * rc + rc;
                float w0 = k * l0, w1 = k * l1, w2 = k * l2, w3 = k * l3;
                long o = ((long)(r - 1) * w + (x - 1)) * 4; whL[o] = w0; whL[o + 1] = w1; whL[o + 2] = w2; whL[o + 3] = w3;
                Q[c] = pc - ((P[c + 1] * w0 + P[c + W] * w2) + (P[c - 1] * w1 + P[c - W] * w3));
            }
        }
        });
        Mirror(Q, W, H);
        // step 2: update, (x + y) even
        Rows(1, H - 1, (ra, rb) => {
        for (int r = ra; r < rb; r++)
        {
            int p = (r - 1) & 1;
            for (int x = 1 + p; x < W - 1; x += 2)
            {
                long c = (long)r * W + x; float pc = P[c];
                float l0 = Lut(lut, P[c + 1], pc), l1 = Lut(lut, P[c - 1], pc), l2 = Lut(lut, P[c + W], pc), l3 = Lut(lut, P[c - W], pc);
                float t = 0.5f / ((l0 + l2) + (l1 + l3));
                float w0 = t * l0, w1 = t * l1, w2 = t * l2, w3 = t * l3;
                long o = ((long)(r - 1) * w + (x - 1)) * 4; whL[o] = w0; whL[o + 1] = w1; whL[o + 2] = w2; whL[o + 3] = w3;
                Q[c] = ((Q[c + 1] * w0 + Q[c + W] * w2) + (Q[c - 1] * w1 + Q[c - W] * w3)) + pc;
            }
        }
        });
        Mirror(Q, W, H);
        // step 3: predict, (odd, odd) from the diagonal neighbours
        Rows(1, H / 2 + 1, (ra, rb) => {
        for (int r = 2 * ra; r < 2 * rb && r < H - 1; r += 2)
            for (int x = 2; x < W - 1; x += 2)
            {
                long c = (long)r * W + x; float pc = P[c];
                float l0 = Lut(lut, P[c + W - 1], pc), l1 = Lut(lut, P[c - W - 1], pc), l2 = Lut(lut, P[c + W + 1], pc), l3 = Lut(lut, P[c - W + 1], pc);
                float s = (l0 + l2) + (l1 + l3), rc = IntelApprox.Rcp(s), k = (1.0f - s * rc) * rc + rc;
                float w0 = k * l0, w1 = k * l1, w2 = k * l2, w3 = k * l3;
                long o = ((long)(r - 1) * w + (x - 1)) * 4; wdL[o] = w0; wdL[o + 1] = w1; wdL[o + 2] = w2; wdL[o + 3] = w3;
                Q[c] = Q[c] - ((Q[c + W - 1] * w0 + Q[c + W + 1] * w2) + (Q[c - W - 1] * w1 + Q[c - W + 1] * w3));
            }
        });
        Mirror(Q, W, H);
        // step 4: update, (even, even)
        Rows(0, (H - 1) / 2, (ra, rb) => {
        for (int r = 2 * ra + 1; r < 2 * rb + 1 && r < H - 1; r += 2)
            for (int x = 1; x < W - 1; x += 2)
            {
                long c = (long)r * W + x; float pc = P[c];
                float l0 = Lut(lut, P[c + W - 1], pc), l1 = Lut(lut, P[c - W - 1], pc), l2 = Lut(lut, P[c + W + 1], pc), l3 = Lut(lut, P[c - W + 1], pc);
                float t = 0.5f / ((l0 + l2) + (l1 + l3));
                float w0 = t * l0, w1 = t * l1, w2 = t * l2, w3 = t * l3;
                long o = ((long)(r - 1) * w + (x - 1)) * 4; wdL[o] = w0; wdL[o + 1] = w1; wdL[o + 2] = w2; wdL[o + 3] = w3;
                Q[c] = ((Q[c + W - 1] * w0 + Q[c + W + 1] * w2) + (Q[c - W - 1] * w1 + Q[c - W + 1] * w3)) + Q[c];
            }
        });
        return Crop(Q, W, H);
    }

    /// <summary>The smoothing half of a level (out, wH, wD, img): on the padded copy, every (x + y)-even pixel becomes
    /// <c>Σ w·T[axial] + 0.5·T</c> with its update weights, the border is re-mirrored, then every (even, even) pixel
    /// <c>Σ w·T[diagonal] + 0.5·T</c>.</summary>
    static float[] Analysis(float[] wh, float[] wd, float[] img, int w, int h)
    {
        int W = w + 2, H = h + 2; var T = Pad(img, w, h);
        Rows(1, H - 1, (ra, rb) => {
        for (int r = ra; r < rb; r++)
        {
            int p = (r - 1) & 1;
            for (int x = 1 + p; x < W - 1; x += 2)
            {
                long c = (long)r * W + x, o = ((long)(r - 1) * w + (x - 1)) * 4;
                T[c] = ((T[c + 1] * wh[o] + T[c + W] * wh[o + 2]) + (T[c - 1] * wh[o + 1] + T[c - W] * wh[o + 3])) + T[c] * 0.5f;
            }
        }
        });
        Mirror(T, W, H);
        Rows(0, (H - 1) / 2, (ra, rb) => {
        for (int r = 2 * ra + 1; r < 2 * rb + 1 && r < H - 1; r += 2)
            for (int x = 1; x < W - 1; x += 2)
            {
                long c = (long)r * W + x, o = ((long)(r - 1) * w + (x - 1)) * 4;
                T[c] = ((T[c + W - 1] * wd[o] + T[c + W + 1] * wd[o + 2]) + (T[c - W - 1] * wd[o + 1] + T[c - W + 1] * wd[o + 3])) + T[c] * 0.5f;
            }
        });
        return Crop(T, W, H);
    }

    /// <summary>Interpolation (out, wH, wD, img): (odd, odd) pixels are replaced by the predict-weighted mean of their
    /// diagonal neighbours, the border is re-mirrored, then the (x + y)-odd pixels by that of their axial neighbours. Only the
    /// (even, even) values of the input survive.</summary>
    static float[] Synthesis(float[] wh, float[] wd, float[] img, int w, int h)
    {
        int W = w + 2, H = h + 2; var T = Pad(img, w, h);
        Rows(1, H / 2 + 1, (ra, rb) => {
        for (int r = 2 * ra; r < 2 * rb && r < H - 1; r += 2)
            for (int x = 2; x < W - 1; x += 2)
            {
                long c = (long)r * W + x, o = ((long)(r - 1) * w + (x - 1)) * 4;
                T[c] = (T[c + W - 1] * wd[o] + T[c + W + 1] * wd[o + 2]) + (T[c - W - 1] * wd[o + 1] + T[c - W + 1] * wd[o + 3]);
            }
        });
        Mirror(T, W, H);
        Rows(1, H - 1, (ra, rb) => {
        for (int r = ra; r < rb; r++)
        {
            int p = (r - 1) & 1;
            for (int x = 2 - p; x < W - 1; x += 2)
            {
                long c = (long)r * W + x, o = ((long)(r - 1) * w + (x - 1)) * 4;
                T[c] = (T[c + 1] * wh[o] + T[c + W] * wh[o + 2]) + (T[c - 1] * wh[o + 1] + T[c - W] * wh[o + 3]);
            }
        }
        });
        return Crop(T, W, H);
    }

    /// <summary>
    /// The seed propagation (out, seeds, pyramid): stage 0 = the input; stage l + 1 = the even pixels of the smoothed
    /// stage l; every stage (0 … n) is scaled by <c>0.125^(l + 1)</c> (`powf`; 0.015625 for l + 1 = 2); then from the coarsest level
    /// down: the coarser result is added onto the even pixels of the stage and the stage is interpolated (<see cref="Synthesis"/>).
    /// "Wavelets data inconsistent!" when the pyramid's arrays disagree.
    /// </summary>
    public float[] Diffuse(float[] input, int w, int h)
    {
        int n = Levels;
        if (n < 1) return (float[])input.Clone();
        if (Dims[0].W != w || Dims[0].H != h) throw new InvalidOperationException("Wavelets data inconsistent!");
        var stage = new float[n + 1][];
        stage[0] = (float[])input.Clone();
        for (int l = 0; l < n; l++)
        {
            var (lw, lh) = Dims[l];
            stage[l + 1] = Downsample(Analysis(WH[l], WD[l], stage[l], lw, lh), lw, lh, out _, out _);
        }
        for (int l = 0; l <= n; l++)
        {
            float s = MuslMath.Powf(0.125f, (float)(l + 1));
            if (l + 1 == 2) s = 0.015625f;
            var st = stage[l]; int sw = l == 0 ? w : Dims[l].W, sh = (int)(st.LongLength / Math.Max(1, sw));
            if ((long)sw * sh != st.LongLength) { for (long i = 0; i < st.LongLength; i++) st[i] = st[i] * s; continue; }
            Rows(0, sh, (ya, yb) => { for (long i = (long)ya * sw; i < (long)yb * sw; i++) st[i] = st[i] * s; });
        }
        float[] c = stage[n];
        for (int l = n - 1; l >= 0; l--)
        {
            var (lw, lh) = Dims[l]; int cw = Dims[l + 1].W; var st = stage[l]; var cc = c;
            Rows(0, (lh + 1) / 2, (ja, jb) =>
            {
                for (int j = ja; j < jb; j++)
                {
                    int y = 2 * j;
                    for (int x = 0, i = 0; x < lw; x += 2, i++) st[(long)y * lw + x] = st[(long)y * lw + x] + cc[(long)j * cw + i];
                }
            });
            c = Synthesis(WH[l], WD[l], st, lw, lh);
        }
        return c;
    }
}
