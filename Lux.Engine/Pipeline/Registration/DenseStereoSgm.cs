using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoLayer&lt;0&gt;`: `compute`, the SGM kernel, the WTA, the range from the previous layer and the skip masks.
/// One layer = plane range per pixel → cost volume (u16 aggregate, u8 raw) → two wavefront SGM passes over sheared tiles with the
/// persistent double-buffered line buffers of the kernel → per-pixel first-minimum WTA → `depth = planes[start + arg]`.
/// </summary>
public sealed class DenseLayer
{
    public StereoParams P = null!;
    public int Index, Align = 8, W, H;
    public float[] Planes = null!;
    public Rgba8Image[] Images = null!; public bool[] Gray = null!; public IReadOnlyList<CalibData> Calibs = null!;
    public Rgba8Image Guidance;
    public byte[] Skip = null!;                     // 0 = computed, 0xff = skipped
    public ushort[] Start = null!, Count = null!, Cap = null!;
    public ushort[][] Agg = null!; public byte[][] Raw = null!;   // per pixel: agg[cap]; raw[cap] (computed pixels only)
    public ushort[][]? AggPass1;                     // diagnostic copy after the first pass (with KeepPass1)
    public int MinLo, MaxHi;                        // rounded
    public float[] Depth = null!;
    public DenseStereo.CostContext Ctx = null!;

    // ---- line buffers, persistent across both passes ----
    ushort[] _L = null!, _Min = null!; ushort[] _RangeS = null!, _RangeC = null!; Vector128<float>[] _Pix = null!;
    int _slotStride, _halfL, _halfM, _halfR;
    static readonly bool _xsMax = Environment.GetEnvironmentVariable("LUX_SGM_XSW") == "1";

    /// <summary>Layer set-up, for layer 0 or from the previous layer: planes, guidance, skip mask, ranges, cost volume.</summary>
    public static DenseLayer Init(StereoParams p, int index, Rgba8Image[] filtered, bool[] gray, IReadOnlyList<CalibData> calibs, float near, float far, int align, DenseLayer? prev)
    {
        var L = new DenseLayer { P = p, Index = index, Align = align, Images = filtered, Gray = gray, Calibs = calibs };
        L.W = (filtered[0].W + p.Scale - 1) / p.Scale; L.H = (filtered[0].H + p.Scale - 1) / p.Scale;
        L.Planes = DenseStereo.Planes(near, far, calibs, p.PlaneDensity, align);
        L.Guidance = DenseStereo.Guidance(filtered[0], L.W, L.H, p.Scale);
        L.Skip = SkipMask(p.SkipMode, L.W, L.H);
        int n = L.Planes.Length, npx = L.W * L.H;
        L.Start = new ushort[npx]; L.Count = new ushort[npx]; L.Cap = new ushort[npx];
        if (prev is null)
        {
            for (int i = 0; i < npx; i++) { L.Start[i] = 0; L.Count[i] = (ushort)n; L.Cap[i] = (ushort)n; }
            L.MinLo = 0; L.MaxHi = ((n + align - 1) / align) * align;
        }
        else
        {
            RangeFromPrevious(L, prev);
            L.MaxHi = ((L.MaxHi + align - 1) / align) * align;
        }
        L.Agg = new ushort[npx][]; L.Raw = new byte[npx][];
        for (int i = 0; i < npx; i++)
        {
            int cap = ((L.Count[i] + align - 1) / align) * align; L.Cap[i] = (ushort)cap;
            L.Agg[i] = new ushort[cap]; if (L.Skip[i] == 0) L.Raw[i] = new byte[cap];
        }
        L.Ctx = DenseStereo.BuildContext(filtered, gray, calibs, L.Planes);
        L.AllocLineBuffers();
        return L;
    }

    /// <summary>Skip mask: mode 0 → all computed; mode 2 → `GetSkippingMaskGrid&lt;1&gt;` (mt19937 default seed per 64×64 tile, one computed pixel per 2×2 cell).</summary>
    public static byte[] SkipMask(int mode, int w, int h)
    {
        var m = new byte[w * h];
        if (mode == 0) return m;
        if (mode != 2) throw new NotSupportedException("unrecognized sampling pattern");
        Array.Fill(m, (byte)0xff);
        int nx = w / 64 + (64 < 2 * (w % 64) ? 1 : 0), ny = h / 64 + (64 < 2 * (h % 64) ? 1 : 0);
        if (nx < 1) nx = 1; if (ny < 1) ny = 1;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int x0 = 64 * i, x1 = Math.Min(w, x0 + 64 * (i == nx - 1 ? 2 : 1)), y0 = 64 * j, y1 = Math.Min(h, y0 + 64 * (j == ny - 1 ? 2 : 1));
                var mt = new Mt19937(5489);
                for (int y = y0; y < y1; y += 2)
                    for (int x = x0; x < x1; x += 2)
                    {
                        int dy = (int)(mt.Next() & 1), dx = (int)(mt.Next() & 1);   // uniform_int(0,1): n = 2 never rejects
                        int yy = y + dy, xx = x + dx;
                        if (xx >= 0 && xx < w && yy >= 0 && yy < h) m[yy * w + xx] = 0;
                    }
            }
        return m;
    }

    /// <summary>Per-pixel plane range from the previous layer's depth (index map via reciprocal boundaries, 4-wide min/max
    /// filter restricted to computed pixels, ±1 plane margin).</summary>
    static void RangeFromPrevious(DenseLayer L, DenseLayer prev)
    {
        int n = L.Planes.Length, pw = prev.W, ph = prev.H;
        var b = new float[n];
        for (int i = 0; i < n - 1; i++) b[i] = (IntelApprox.ReciprocalScalar(Vector128.CreateScalar(L.Planes[i + 1])).ToScalar() + IntelApprox.ReciprocalScalar(Vector128.CreateScalar(L.Planes[i])).ToScalar()) * 0.5f;
        b[n - 1] = float.MaxValue;
        var idx = new ushort[pw * ph];
        for (int i = 0; i < pw * ph; i++)
        {
            float v = IntelApprox.ReciprocalScalar(Vector128.CreateScalar(prev.Depth[i])).ToScalar();
            int c = 0; while (c < n && b[c] <= v) c++;   // #{j : b[j] ≤ v}
            idx[i] = (ushort)c;
        }
        var valid = new byte[pw * ph]; for (int i = 0; i < pw * ph; i++) valid[i] = prev.Skip[i] > 0 ? (byte)0 : (byte)0xff;
        var hmin = new ushort[pw * ph]; var hmax = new ushort[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                ushort mn = 0xffff, mx = 0;
                for (int k = -1; k <= 2; k++)
                {
                    int xx = Math.Clamp(x + k, 0, pw - 1);
                    if (valid[y * pw + xx] == 0) continue;
                    ushort v = idx[y * pw + xx]; if (v < mn) mn = v; if (v > mx) mx = v;
                }
                hmin[y * pw + x] = mn; hmax[y * pw + x] = mx;
            }
        var vmin = new ushort[pw * ph]; var vmax = new ushort[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                ushort mn = 0xffff, mx = 0;
                for (int k = -1; k <= 2; k++) { int yy = Math.Clamp(y + k, 0, ph - 1); ushort a = hmin[yy * pw + x], c = hmax[yy * pw + x]; if (a < mn) mn = a; if (c > mx) mx = c; }
                vmin[y * pw + x] = mn; vmax[y * pw + x] = mx;
            }
        float fy = (float)(ph - 1) / (float)(L.H - 1), fx = (float)(pw - 1) / (float)(L.W - 1);
        int maxHi = 0, minLo = int.MaxValue;
        for (int y = 0; y < L.H; y++)
        {
            int py = (int)((float)y * fy);
            for (int x = 0; x < L.W; x++)
            {
                int px = (int)((float)x * fx);
                int lo = Math.Max(0, vmin[py * pw + px] - L.P.Margin), hi = Math.Min(n - 1, vmax[py * pw + px] + 1);
                int i = y * L.W + x; L.Start[i] = (ushort)lo; L.Count[i] = (ushort)(hi - lo);
                maxHi = Math.Max(maxHi, hi & 0xffff); minLo = Math.Min(minLo, lo & 0xffff);
            }
        }
        L.MaxHi = maxHi; L.MinLo = minLo;
    }

    /// <summary>Memory estimate of a layer, as Lumen computes it: bytes = costvol + w·h·4 + 20·(2w+4) + 2·((4·maxHi+5)·(2w+4) + 8w+16)
    /// + Σ images(h·stride·4) + w·h·8, with the cost volume sized from the range of the previous layer (Σ 8 + cap·(skipped ? 2 : 3)).</summary>
    public static long MemoryEstimate(StereoParams p, Rgba8Image[] images, IReadOnlyList<CalibData> calibs, float near, float far, int align, DenseLayer prev)
    {
        var L = new DenseLayer { P = p, Align = align, Images = images, Calibs = calibs };
        L.W = (images[0].W + p.Scale - 1) / p.Scale; L.H = (images[0].H + p.Scale - 1) / p.Scale;
        L.Planes = DenseStereo.Planes(near, far, calibs, p.PlaneDensity, align);
        L.Skip = SkipMask(p.SkipMode, L.W, L.H);
        int npx = L.W * L.H; L.Start = new ushort[npx]; L.Count = new ushort[npx]; L.Cap = new ushort[npx];
        RangeFromPrevious(L, prev);
        int maxHi = ((L.MaxHi + align - 1) / align) * align;
        long cost = 0; for (int i = 0; i < npx; i++) { int cap = ((L.Count[i] + align - 1) / align) * align; cost += 8 + (long)cap * (L.Skip[i] == 0 ? 3 : 2); }
        long w = L.W, h = L.H, imgs = 0; foreach (var im in images) imgs += (long)im.H * im.Stride * 4;
        return cost + w * h * 4 + 20 * (2 * w + 4) + 2 * ((4L * maxHi + 5) * (2 * w + 4) + 8 * w + 16) + imgs + w * h * 8;
    }

    void AllocLineBuffers()
    {
        int maxCnt = MaxHi; _slotStride = 4 * maxCnt + 5; int slots = W + 2;
        _halfL = slots * _slotStride; _L = new ushort[2 * _halfL]; Array.Fill(_L, (ushort)2000);
        _halfM = slots * 4; _Min = new ushort[2 * _halfM]; Array.Fill(_Min, (ushort)2000);
        _halfR = slots; _RangeS = new ushort[2 * slots]; _RangeC = new ushort[2 * slots]; Array.Fill(_RangeC, (ushort)Align);
        _Pix = new Vector128<float>[2 * slots];
    }
    int LIdx(int half, int slot, int path) => half * _halfL + slot * _slotStride + 1 + path * (MaxHi + 1);

    // The cubic of the fast exp2, highest power first.
    static readonly float ExpPoly3 = BitConverter.Int32BitsToSingle(0x3d9fcb52), ExpPoly2 = BitConverter.Int32BitsToSingle(0x3e677e26), ExpPoly1 = BitConverter.Int32BitsToSingle(0x3f322226), ExpPoly0 = BitConverter.Int32BitsToSingle(0x3f7ffb19);

    /// <summary>Workers for the raw matching costs (see <see cref="Compute"/>); the aggregation sweep is serial whatever the value.</summary>
    public int Threads = 1;
    /// <summary>Keep a copy of the aggregates after the first pass in <see cref="AggPass1"/>. It is for diagnostic dumps.</summary>
    public bool KeepPass1;
    bool _rawReady;

    /// <summary>`compute`: `runPass(+1)`, `runPass(−1)` (each = raster order over the sheared wavefront tiles), then WTA.
    /// The raw matching cost of a pixel depends only on the images, planes and the pixel's plane range, so with <see cref="Threads"/> &gt; 1
    /// the costs of every computed pixel are produced row-parallel first (into `Raw`, exactly the bytes pass 1 would store) and pass 1
    /// then reads them like pass 2 does — same values, same aggregation order.</summary>
    public void Compute()
    {
        var gw = Vector128.Create(P.Guidance[0], P.Guidance[1], P.Guidance[2], P.Guidance[3]);
        var costTmp = new ushort[Planes.Length]; var raw = new byte[Planes.Length + 8];
        int T = P.Tile;
        if (Threads > 1 && H > 1)
        {
            int W0 = Images[0].W, H0 = Images[0].H, sc = P.Scale, half = (sc + (sc >> 31)) >> 1, nOthers = Ctx.Others.Length;
            Parallel.For(0, H, new ParallelOptions { MaxDegreeOfParallelism = Threads },
                () => (cost: new ushort[Planes.Length], raw: new byte[Planes.Length + 8]),
                (y, _, scratch) =>
                {
                    for (int x = 0; x < W; x++)
                    {
                        int pi = y * W + x; if (Skip[pi] != 0) continue;
                        int start = Start[pi], count = Count[pi], cap = Cap[pi];
                        int xImg = Math.Min(sc * x + half, W0 - 1), yImg = Math.Min(sc * y + half, H0 - 1);
                        DenseStereo.MatchingCost(Ctx, xImg, yImg, start, count, scratch.cost);
                        DenseStereo.Normalise(scratch.cost, count, cap, nOthers, scratch.raw);
                        Array.Copy(scratch.raw, Raw[pi], cap);
                    }
                    return scratch;
                }, _ => { });
            _rawReady = true;
        }
        foreach (int dir in new[] { 1, -1 })
        {
            int gwN = (W + T + T - 1) / T, ghN = (H + T - 1) / T;
            for (int j = 0; j < ghN; j++)
                for (int i = 0; i < gwN; i++)
                {
                    int x0 = dir > 0 ? i * T : W - 1 - i * T, y0 = dir > 0 ? j * T : H - 1 - j * T;
                    int yEnd = Math.Clamp(y0 + dir * T, -1, H);
                    for (int r = 0; ; r++)
                    {
                        int y = y0 + dir * r; if (y == yEnd) break;
                        int xs = Math.Clamp(x0 - dir * r, 0, (_xsMax && dir > 0) ? W : W - 1), xe = Math.Clamp(x0 + dir * (T - r), -1, W);
                        if (xs == xe) continue;
                        int cur = (r & 1) == 0 ? 1 : 0, other = 1 - cur;   // tile row 0 always writes half B
                        for (int x = xs; x != xe; x += dir) Pixel(x, y, dir, cur, other, gw, costTmp, raw);
                    }
                }
            if (dir > 0 && KeepPass1) { AggPass1 = new ushort[Agg.Length][]; for (int i = 0; i < Agg.Length; i++) AggPass1[i] = (ushort[])Agg[i].Clone(); }
        }
        Depth = new float[W * H];
        for (int i = 0; i < W * H; i++)
        {
            var agg = Agg[i]; int cnt = Count[i]; ushort best = 0xffff; int arg = 0;
            for (int k = 0; k < cnt; k++) if (agg[k] < best) { best = agg[k]; arg = k; }
            Depth[i] = Planes[(ushort)(Start[i] + arg)];
        }
    }

    unsafe void Pixel(int x, int y, int dir, int cur, int other, Vector128<float> gw, ushort[] costTmp, byte[] raw)
    {
        int pi = y * W + x, slot = x + 1;
        var gb = Guidance.Data.AsSpan(Guidance.Offset(x, y), 4);
        var g = Vector128.Create((float)gb[0], (float)gb[1], (float)gb[2], (float)gb[3]);
        _Pix[cur * _halfR + slot] = g;
        int start = Start[pi], count = Count[pi], cap = Cap[pi];
        // raw costs for this pixel: pass 1 computes and stores the u8 copy, pass 2 reads it; skipped pixels are 0
        if (Skip[pi] != 0) { Array.Clear(raw, 0, cap); for (int k = count; k < cap; k++) raw[k] = 255; }
        else if (dir > 0 && !_rawReady)
        {
            int W0 = Images[0].W, H0 = Images[0].H, sc = P.Scale, half = (sc + (sc >> 31)) >> 1;
            int xImg = Math.Min(sc * x + half, W0 - 1), yImg = Math.Min(sc * y + half, H0 - 1);
            DenseStereo.MatchingCost(Ctx, xImg, yImg, start, count, costTmp);
            DenseStereo.Normalise(costTmp, count, cap, Ctx.Others.Length, raw);
            Array.Copy(raw, Raw[pi], cap);
        }
        else if ((cap & 7) == 0) raw = Raw[pi];   // whole 8-lane blocks: read the stored costs in place
        else Array.Copy(Raw[pi], raw, cap);
        // stale cleanup of the current half's slot for the previously stored range, then the four paths
        int sp = _RangeS[cur * _halfR + slot], cp = _RangeC[cur * _halfR + slot];
        var agg = Agg[pi];
        ushort[] L = _L; int P1 = P.P1; float p2Base = (float)P.P1 * P.P2Scale;
        fixed (ushort* pL = L, pAgg = agg) fixed (byte* pRaw = raw)
        for (int p = 0; p < 4; p++)
        {
            int baseCur = LIdx(cur, slot, p);
            {
                int q0 = sp - MinLo, k0 = Math.Max(0, -q0), k1 = Math.Min(cp, L.Length - baseCur - q0);
                if (k1 > k0) L.AsSpan(baseCur + q0 + k0, k1 - k0).Fill(2000);
            }
            int ns = slot + (p == 2 ? 0 : p == 3 ? dir : -dir); int half = p == 0 ? cur : other;   // the neighbor slot of the path
            int minPrev = _Min[half * _halfM + ns * 4 + p];
            var d = (g - _Pix[half * _halfR + ns]) * gw;
            float S = (MathF.Abs(d.GetElement(0)) + MathF.Abs(d.GetElement(2))) + MathF.Abs(d.GetElement(1));
            float e = MathF.Max(MathF.Min(-S, 128.0f), -126.0f);
            int ei = (int)e + (e < 0f ? -1 : 0); float f = e - (float)ei;
            float poly = ((ExpPoly3 * f + ExpPoly2) * f + ExpPoly1) * f + ExpPoly0;
            float E = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(poly) + (ei << 23));
            int P2 = (int)(p2Base * E);
            int P2p = Math.Min(P2 + minPrev, 65535);
            if (cap == 0) { _Min[cur * _halfM + slot * 4 + p] = 0xffff; continue; }
            int nb = LIdx(half, ns, p); int runMin = 0xffff;
            int q = start - MinLo;
            // The vector form does eight lanes in one step. It needs whole blocks, every neighbor read and every write inside
            // the buffer, a written range that does not touch the range read, and a P2 term that fits 16 bits.
            bool vector = Vector128.IsHardwareAccelerated && (cap & 7) == 0 && P2p >= 0
                && nb + q - 1 >= 0 && nb + q + cap + 1 <= L.Length && baseCur + q >= 0 && baseCur + q + cap <= L.Length
                && Math.Abs(baseCur - nb) >= cap + 16;
            if (vector)
            {
                var vP1 = Vector128.Create((ushort)P1); var vP2 = Vector128.Create((ushort)P2p); var vMinPrev = Vector128.Create((ushort)minPrev);
                var vRun = Vector128.Create((ushort)0xffff);
                ushort* pn = pL + nb + q, pw = pL + baseCur + q;
                for (int b = 0; b < cap; b += 8)
                {
                    var Lm = Vector128.Load(pn + b - 1); var L0 = Vector128.Load(pn + b); var Lp = Vector128.Load(pn + b + 1);
                    var m = Vector128.Min(Vector128.Min(Vector128.AddSaturate(Lm, vP1), L0), Vector128.Min(Vector128.AddSaturate(Lp, vP1), vP2));
                    var r16 = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(pRaw + b)).AsByte());
                    var lc = Vector128.SubtractSaturate(Vector128.AddSaturate(r16, m), vMinPrev);
                    lc.Store(pw + b);
                    vRun = Vector128.Min(vRun, lc);
                    Vector128.AddSaturate(Vector128.Load(pAgg + b), lc).Store(pAgg + b);
                }
                for (int l = 0; l < 8; l++) runMin = Math.Min(runMin, vRun.GetElement(l));
            }
            else
            {
                for (int b = 0; b < cap; b += 8)
                    for (int l = 0; l < 8; l++)
                    {
                        int qi = q + b + l;
                        int Lm = Rd(nb + qi - 1), Lp = Rd(nb + qi + 1), L0 = Rd(nb + qi);
                        int m = Math.Min(Math.Min(Math.Min(Lm + P1, 65535), L0), Math.Min(Math.Min(Lp + P1, 65535), P2p));
                        int lc = Math.Min(raw[b + l] + m, 65535) - minPrev; if (lc < 0) lc = 0;
                        L[baseCur + qi] = (ushort)lc;
                        if (lc < runMin) runMin = lc;
                        agg[b + l] = (ushort)Math.Min(agg[b + l] + lc, 65535);
                    }
            }
            _Min[cur * _halfM + slot * 4 + p] = (ushort)runMin;
        }
        _RangeS[cur * _halfR + slot] = (ushort)start; _RangeC[cur * _halfR + slot] = (ushort)cap;
    }
    // Neighbor-block reads run up to 8 lanes past a path block (`Lp = N[q+1..q+8]`, whatever is in the buffer). Inside the
    // allocation that is the next block / the next half (the array is laid out exactly like Lumen's, so the same words are read); past the
    // END of the allocation — the margin slot w+1 of half B, i.e. the up-right neighbor of the last column on an odd row whose padding
    // lanes (count..cap) reach beyond maxHi — Lumen reads the zero-filled pages behind its buffer, so `m = 0` and the lane's path cost
    // saturates to 0. With the 2000 guard there, `L = raw` instead (+255 per visit; pass 1 visits the last column twice), and the WTA
    // of the cells that the up-right path feeds changes.
    int Rd(int i) => i < 0 ? 2000 : i < _L.Length ? _L[i] : 0;
}

/// <summary>The six-layer pyramid driver, with the memory-mode choice.</summary>
public static class DenseStereoPyramid
{
    public const long Budget = 0x40000000;   // 1 GiB
    public static DenseLayer[] Run(Rgba8Image[] images, bool[] gray, IReadOnlyList<CalibData> calibs, float near = DenseStereo.Near, float far = DenseStereo.Far, Action<string>? log = null, int threads = 1, bool keepPass1 = false)
    {
        var pars = StereoParams.L16Pyramid();
        var filtered = new Rgba8Image[images.Length];
        for (int k = 0; k < images.Length; k++) filtered[k] = DenseStereo.BoxFilter3(images[k]);   // shared by all layers
        var layers = new DenseLayer[pars.Length]; DenseLayer? prev = null;
        for (int i = 0; i < pars.Length; i++)
        {
            int align = 8;
            if (prev is not null)
            {   // mode 1 (align 8) if the estimate fits the 1 GiB budget, else mode 2 (align 2), else upsample-only (unsupported here)
                long est8 = DenseLayer.MemoryEstimate(pars[i], filtered, calibs, near, far, 8, prev);
                if (est8 >= Budget) { long est2 = DenseLayer.MemoryEstimate(pars[i], filtered, calibs, near, far, 2, prev); if (est2 >= Budget) throw new NotSupportedException("StereoLayer memory budget exceeded: the BilateralUpsample fallback (mode 0) is not ported"); align = 2; }
            }
            var L = DenseLayer.Init(pars[i], i, filtered, gray, calibs, near, far, align, prev);
            L.Threads = threads; L.KeepPass1 = keepPass1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            L.Compute();
            log?.Invoke($"layer {i}: {L.W}x{L.H}, {L.Planes.Length} planes, align {align}, minLo {L.MinLo} maxHi {L.MaxHi}, {sw.Elapsed.TotalSeconds:F1}s");
            layers[i] = L; prev = L;
        }
        return layers;
    }

    /// <summary>Layer 6 = `UpsampleLayer(12.0f, 2)` run in mode 0 (its upsample step on `prev`) on the finest StereoLayer; `guide` =
    /// <c>StereoAsyncApi.ReferenceGuide</c> (the full-res `StereoISP::GetReferenceImage` of the reference capture), `(setW, setH)` = the LayerStack size (W0/2, H0/2).</summary>
    public static DenseUpsampleLayer.Result Upsample(DenseLayer top, Rgba8Image guide, int setW, int setH) => DenseUpsampleLayer.Run(top.Depth, top.W, top.H, guide, setW, setH);
}
