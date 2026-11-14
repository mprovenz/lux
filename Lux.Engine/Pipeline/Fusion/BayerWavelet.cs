using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Imaging;

namespace Lux.Engine.Pipeline.BayerFusion;   // not `Lux.Engine.Pipeline.Fusion`: a namespace of that name would shadow the class `Fusion` for every `Lux.Engine.Pipeline.*` file

/// <summary>
/// The 16×16 block transform of the PackedBayerFusion merge kernel:
/// a 4-level 2-D lifting wavelet on vec4 samples, in place, interleaved layout (level ℓ works on the (0, 2^ℓ, 2·2^ℓ, …) grid; even
/// slots = approximation, odd slots = detail).
///
/// Lumen's forward and inverse transforms are straight-line vec4 code; every statement is one of four expression forms applied to one 1-D
/// line (a row or a column of the current grid), and every line's outputs depend only on that line's inputs, so the pass order
/// is what matters, not the statement order inside a pass. The pass order:
/// <list type="bullet">
/// <item>forward: level 1 (n = 16, step 1) rows 0..15 then columns 0..15; level 2 (n = 8, step 2) even rows then even columns;
/// level 3 (n = 4, step 4) rows/columns 0,4,8,12; level 4 (n = 2, step 8) rows/columns 0,8 — always x (rows) first, then y.</item>
/// <item>inverse: level 4, 3, 2, 1 — and at every level again x (rows) first, then y (columns). It is NOT the strict reverse
/// of the forward step order (the level-4 head reads `x[0,0]·c1 − x[0,8]·c1` before any column op); the port keeps
/// Lumen's order.</item>
/// </list>
/// Per-line forms (Lumen's association; `+`/`·` operand order is irrelevant — IEEE
/// add/mul are commutative — only the tree is kept): forward `d_last = x[n−1]·c1 − x[n−2]·c1`, `d[k] = x[2k+1]·c1 − (x[2k+2] + x[2k])·c3`,
/// `s[0] = d[0]·c4 + x[0]·c0`, `s[k] = (d[k−1] + d[k])·c2 + x[2k]·c0`; inverse `x[0] = s[0]·c1 − d[0]·c1`,
/// `x[2k] = s[k]·c1 − (d[k] + d[k−1])·c3`, `x[2k+1] = (x[2k] + x[2k+2])·0.5 + d[k]·c0`, `x[n−1] = d·c0 + x[n−2]`.
/// Level 3/4 of both directions keep intermediates in registers instead of memory; single-precision SSE either way, so the
/// values are identical.
/// </summary>
public static class BayerWavelet
{
    static readonly float C0 = BitConverter.Int32BitsToSingle(0x3fb504f3);   // 1.4142135f (√2)
    static readonly float C1 = BitConverter.Int32BitsToSingle(0x3f3504f3);   // 0.70710677f (1/√2)
    static readonly float C3 = BitConverter.Int32BitsToSingle(0x3eb504f3);   // 0.35355338f (1/(2√2))
    static readonly float C2 = BitConverter.Int32BitsToSingle(0x3effffff);   // 0.49999997f
    static readonly float C4 = BitConverter.Int32BitsToSingle(0x3f7fffff);   // 0.99999994f
    const float Half = 0.5f;                                                  // inverse only

    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static Vec4F Mul(Vec4F a, float c) => new(a.R * c, a.G * c, a.B * c, a.A * c);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static Vec4F Mul(Vec4F a, Vec4F b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static Vec4F Add(Vec4F a, Vec4F b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static Vec4F Sub(Vec4F a, Vec4F b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);

    /// <summary>Forward transform of a 16×16 row-major vec4 block, in place.</summary>
    public static void Forward(Vec4F[] block)
    {
        if (block.Length != 256) throw new ArgumentException("16x16 block expected", nameof(block));
        for (int level = 0; level < 4; level++)
        {
            int step = 1 << level, n = 16 >> level;
            for (int r = 0; r < 16; r += step) ForwardLine(block, r * 16, step, n);          // along x: rows of the grid
            for (int c = 0; c < 16; c += step) ForwardLine(block, c, step * 16, n);          // along y: columns of the grid
        }
    }

    /// <summary>Inverse transform, in place (levels 4→1, each level rows then columns — see the class remarks).</summary>
    public static void Inverse(Vec4F[] block)
    {
        if (block.Length != 256) throw new ArgumentException("16x16 block expected", nameof(block));
        for (int level = 3; level >= 0; level--)
        {
            int step = 1 << level, n = 16 >> level;
            for (int r = 0; r < 16; r += step) InverseLine(block, r * 16, step, n);
            for (int c = 0; c < 16; c += step) InverseLine(block, c, step * 16, n);
        }
    }

    /// <summary>
    /// One forward lifting step on the line `x[k] = a[b + k·st]`, k = 0..n−1 (n = 16, 8, 4, 2). Statement forms and order as in
    /// the row loop of Lumen's code: the last detail first (`x15·c1 − x14·c1`), then the interior details
    /// `x[2k+1]·c1 − (x[2k+2] + x[2k])·c3`, the approximations `(d[k−1] + d[k])·c2 + x[2k]·c0`, and last `s[0] = d[0]·c4 + x[0]·c0`.
    /// Details only read even (untouched) samples, approximations read their own even sample and the two details, so the
    /// in-place order is free of hazards for any interleaving.
    /// </summary>
    static void ForwardLine(Vec4F[] a, int b, int st, int n)
    {
        int h = n >> 1;
        // d_last = x[n−1]·c1 − x[n−2]·c1
        a[b + (n - 1) * st] = Sub(Mul(a[b + (n - 1) * st], C1), Mul(a[b + (n - 2) * st], C1));
        // d[k] = x[2k+1]·c1 − (x[2k+2] + x[2k])·c3
        for (int k = 0; k < h - 1; k++)
            a[b + (2 * k + 1) * st] = Sub(Mul(a[b + (2 * k + 1) * st], C1), Mul(Add(a[b + (2 * k + 2) * st], a[b + 2 * k * st]), C3));
        // s[k] = (d[k−1] + d[k])·c2 + x[2k]·c0
        for (int k = 1; k < h; k++)
            a[b + 2 * k * st] = Add(Mul(Add(a[b + (2 * k - 1) * st], a[b + (2 * k + 1) * st]), C2), Mul(a[b + 2 * k * st], C0));
        // s[0] = d[0]·c4 + x[0]·c0
        a[b] = Add(Mul(a[b + st], C4), Mul(a[b], C0));
    }

    /// <summary>
    /// One inverse lifting step on a line (forms: `x0 = s0·c1 − d0·c1`, `x[2k] = s[k]·c1 − (d[k] + d[k−1])·c3`,
    /// `x[2k+1] = (x[2k] + x[2k+2])·0.5 + d[k]·c0`, `x[n−1] = d·c0 + x[n−2]`). Even samples are rebuilt first (they read only
    /// the untouched odd slots), then the odd ones from the new evens and their own detail.
    /// </summary>
    static void InverseLine(Vec4F[] a, int b, int st, int n)
    {
        int h = n >> 1;
        // x0 = s0·c1 − d0·c1
        a[b] = Sub(Mul(a[b], C1), Mul(a[b + st], C1));
        // x[2k] = s[k]·c1 − (d[k] + d[k−1])·c3
        for (int k = 1; k < h; k++)
            a[b + 2 * k * st] = Sub(Mul(a[b + 2 * k * st], C1), Mul(Add(a[b + (2 * k + 1) * st], a[b + (2 * k - 1) * st]), C3));
        // x[2k+1] = (x[2k] + x[2k+2])·0.5 + d[k]·c0
        for (int k = 0; k < h - 1; k++)
            a[b + (2 * k + 1) * st] = Add(Mul(Add(a[b + 2 * k * st], a[b + (2 * k + 2) * st]), Half), Mul(a[b + (2 * k + 1) * st], C0));
        // x[n−1] = d·c0 + x[n−2]
        a[b + (n - 1) * st] = Add(Mul(a[b + (n - 1) * st], C0), a[b + (n - 2) * st]);
    }
}

/// <summary>
/// Helpers of the merge kernel: the per-coefficient shrink, the Hann window, the block extractor, the Hann overlap-add
/// writers and the `rcpps` + one-Newton-step reciprocal. Blocks are 16×16 row-major `Vec4F[256]`;
/// images are row-major with stride = width.
/// </summary>
public static class BayerMerge
{
    /// <summary>
    /// The 16×16 vec4 noise-gain table `T` of the wavelet coefficients (all four lanes are stored per coefficient and all four
    /// are equal). `BayerWaveletNoiseGain.bin` holds Lumen's table, 0x1000 bytes.
    /// </summary>
    public static readonly Vec4F[] NoiseGain = LoadNoiseGain();

    /// <summary>`hann[i] = (float)(0.5 − cos(((double)i + 0.5)·6.2831854820251465 / 16.0)·0.5)` (every step in double
    /// precision, with 2π first rounded to float; the caller rounds the result to float). The window is not bit-symmetric
    /// (e.g. hann[6] = 0x3f6a6d99, hann[9] = 0x3f6a6d98); every value is ≥ 10⁷ double-ulps away from a float rounding boundary, so the
    /// result does not depend on the C runtime's cos implementation.</summary>
    public static readonly float[] Hann = BuildHann();

    static readonly float Inv256 = BitConverter.Int32BitsToSingle(0x3b800000);   // 1/256

    static Vec4F[] LoadNoiseGain([CallerFilePath] string sourcePath = "")
    {
        const string name = "BayerWaveletNoiseGain.bin";
        byte[]? bytes = null;
        using (var s = typeof(BayerMerge).Assembly.GetManifestResourceStream(name))
            if (s is not null) { bytes = new byte[s.Length]; s.ReadExactly(bytes); }
        if (bytes is null)
        {
            // Not embedded (no csproj entry yet): next to the assembly, else next to this source file.
            string[] candidates = { Path.Combine(AppContext.BaseDirectory, name), Path.Combine(Path.GetDirectoryName(sourcePath) ?? "", name) };
            foreach (var c in candidates) if (File.Exists(c)) { bytes = File.ReadAllBytes(c); break; }
        }
        if (bytes is null || bytes.Length != 4096) throw new InvalidOperationException($"{name} missing or not 4096 bytes");
        return MemoryMarshal.Cast<byte, Vec4F>(bytes).ToArray();
    }

    static float[] BuildHann()
    {
        var h = new float[16];
        for (int i = 0; i < 16; i++) h[i] = (float)(0.5 - Math.Cos(((double)i + 0.5) * 6.2831854820251465 / 16.0) * 0.5);
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Rcp(float d) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(d)).ToScalar();
    /// <summary>`maxps`/`maxss` dst,src: dst if dst &gt; src else src (so src on NaN or equal).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float MaxPs(float dst, float src) => dst > src ? dst : src;

    /// <summary>
    /// `Shrink(S, q, Rw, T, nz)`, in place on the transformed source block `S` against the transformed
    /// reference `Rw`: per coefficient `d = Rw − S; d2 = d·d; den = (T·nz) + d2; r = rcpps(den)` (raw, no Newton step); `t = r·d2`
    /// (not `r·d·d`); `tm = max(max(t2, t0), max(t3, t1))` with MAXPS semantics,
    /// broadcast; `S = d·tm + S; acc = (1 − tm) + acc`. Returns `q = acc·(1/256)` (all lanes equal).
    /// </summary>
    public static Vec4F Shrink(Vec4F[] S, Vec4F[] Rw, Vec4F nz)
    {
        if (S.Length != 256 || Rw.Length != 256) throw new ArgumentException("16x16 blocks expected");
        var T = NoiseGain;
        float aR = 0f, aG = 0f, aB = 0f, aA = 0f;
        for (int i = 0; i < 256; i++)
        {
            Vec4F s = S[i];
            Vec4F d = BayerWavelet.Sub(Rw[i], s);
            Vec4F d2 = BayerWavelet.Mul(d, d);
            Vec4F den = BayerWavelet.Add(BayerWavelet.Mul(T[i], nz), d2);
            Vec4F r = new(Rcp(den.R), Rcp(den.G), Rcp(den.B), Rcp(den.A));
            Vec4F t = BayerWavelet.Mul(r, d2);
            float m0 = MaxPs(t.B, t.R), m1 = MaxPs(t.A, t.G);     // MAXPS order: NaN gives t0 or t1
            float tm = MaxPs(m0, m1);                              // MAXSS order: NaN gives m1
            S[i] = BayerWavelet.Add(BayerWavelet.Mul(d, tm), s);
            aR = (1f - tm) + aR; aG = (1f - tm) + aG; aB = (1f - tm) + aB; aA = (1f - tm) + aA;
        }
        return new Vec4F(aR * Inv256, aG * Inv256, aB * Inv256, aA * Inv256);
    }

    /// <summary>
    /// `ExtractBlock(out, packedImage, B = (bx, by, bx+16, by+16), range)`: the 16×16 vec4 block of a packed vec4x16f image
    /// (4 halves per pixel, `Half16.ToFloat`) times `range`, with edge replication of the *image*:
    /// rows `by + r` clamped to [0, h−1]; columns `bx + c &lt; 0` read column 0, `≥ w` read column w−1. Lumen's fast path
    /// (block fully inside) performs exactly the same half→float and multiply per pixel; the clipped path's first column
    /// loop runs `while (c &lt; −bx)`, so the rule is only defined for `bx &gt; −16` (`by &gt; −16` likewise) — which the kernel
    /// guarantees by only visiting blocks that intersect the image.
    /// </summary>
    public static Vec4F[] ExtractBlock(ushort[] packed, int w, int h, int bx, int by, float range)
    {
        var block = new Vec4F[256];
        ExtractBlock(packed, w, h, bx, by, range, block);
        return block;
    }

    public static void ExtractBlock(ushort[] packed, int w, int h, int bx, int by, float range, Vec4F[] block)
    {
        if (block.Length != 256) throw new ArgumentException("16x16 block expected", nameof(block));
        if (bx <= -16 || by <= -16 || bx >= w + 16 || by >= h + 16) throw new ArgumentOutOfRangeException(nameof(bx), "block must intersect the image");
        for (int r = 0; r < 16; r++)
        {
            int y = by + r;
            if (y < 0) y = 0;
            if (y > h - 1) y = h - 1;
            int row = y * w;
            for (int c = 0; c < 16; c++)
            {
                int x = bx + c;
                if (x < 0) x = 0;
                else if (x >= w) x = w - 1;
                int p = (row + x) * 4;
                block[r * 16 + c] = new Vec4F(Half16.ToFloat(packed[p]) * range, Half16.ToFloat(packed[p + 1]) * range, Half16.ToFloat(packed[p + 2]) * range, Half16.ToFloat(packed[p + 3]) * range);
            }
        }
    }

    /// <summary>
    /// `AddHann(dst, B, block, hann)`: `dst[y][x] += (hann[y − by]·block[y − by][x − bx])·hann[x − bx]` over the part of the
    /// 16×16 block inside the image (the fast path and the clipped path both use this operation order).
    /// </summary>
    public static void AddHann(Vec4F[] dst, int w, int h, int bx, int by, Vec4F[] block, float[] hann)
    {
        int x0 = Math.Max(bx, 0), y0 = Math.Max(by, 0), x1 = Math.Min(bx + 16, w), y1 = Math.Min(by + 16, h);
        for (int y = y0; y < y1; y++)
        {
            float hy = hann[y - by];
            for (int x = x0; x < x1; x++)
            {
                float hx = hann[x - bx];
                int di = y * w + x;
                dst[di] = BayerWavelet.Add(BayerWavelet.Mul(BayerWavelet.Mul(block[(y - by) * 16 + (x - bx)], hy), hx), dst[di]);
            }
        }
    }

    /// <summary>The weight-image writer, `dst[y][x] += (hann[y − by]·s)·hann[x − bx]` with one vec4 `s`
    /// per block, in this operation order.</summary>
    public static void AddHannScalar(Vec4F[] dst, int w, int h, int bx, int by, Vec4F s, float[] hann)
    {
        int x0 = Math.Max(bx, 0), y0 = Math.Max(by, 0), x1 = Math.Min(bx + 16, w), y1 = Math.Min(by + 16, h);
        for (int y = y0; y < y1; y++)
        {
            Vec4F hs = BayerWavelet.Mul(s, hann[y - by]);
            for (int x = x0; x < x1; x++)
            {
                int di = y * w + x;
                dst[di] = BayerWavelet.Add(BayerWavelet.Mul(hs, hann[x - bx]), dst[di]);
            }
        }
    }

    /// <summary>`rcpps` + one Newton step: `r' = ((1 − n·r)·r) + r`.</summary>
    public static float RcpNR(float n)
    {
        float r = Rcp(n);
        return ((1f - n * r) * r) + r;
    }

    public static Vec4F RcpNR(Vec4F n) => new(RcpNR(n.R), RcpNR(n.G), RcpNR(n.B), RcpNR(n.A));
}
