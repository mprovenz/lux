using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// Lumen's `lt::ImagePatchHotPixels` (with its tile lambda), the body of `HotPixelRemoval = default` on the
/// Bayer-ushort domain (`setHotPixelRemoval`: slot pad 9, align 2, scale 1; the stage lambda calls it
/// with the CapturedImage red position, the frame's sensor gain, the `lt::Sensor` σ-tables and k = 1.0).
///
/// Pass 1 (per pixel, rows/cols of the rect ±4): take the 8 same-color neighbors — the 4 axial ones at distance 2
/// and, on R/B sites, the 4 diagonals at distance 2 or, on G sites, the 4 diagonals at distance 1 — run Lumen's
/// min/max network on them (an order statistic near the 2nd maximum), `excess = sat(centre − stat)`,
/// `base = centre − excess`; the pixel is a candidate when `excess > σ[base] · k · 4`, stored as
/// `base | 0x8000`. Pass 2 (rect): a candidate is patched to `base` unless its flagged neighbors say it is part of
/// a structure — R/B sites: with exactly one flagged same-color ring neighbor, patch only if no near neighbor and
/// no ring→far (distance 4) pair is flagged; with none, patch if fewer than 2 near neighbors are flagged; G sites:
/// the analogous rule with the axial-4 near set and the knight-position pairs. σ is the per-CFA-channel noise table
/// selected by sensor gain (<see cref="SensorNoise.SigmaTables"/>), indexed by the raw DN.
/// </summary>
public static class HotPixelKernel
{
    public const int Pad = 9, Align = 2;
    static readonly int[]? _dbg = Environment.GetEnvironmentVariable("LUX_HP_DEBUG") is string d ? d.Split(',').Select(int.Parse).ToArray() : null;
    public const float ThresholdScale = 4f;

    /// <summary>Run on a whole frame; returns a frame-sized buffer with the rect patched (zeros elsewhere), like Lumen's
    /// tile lambda writing into a freshly allocated output image.</summary>
    public static ushort[] Run(ushort[] raw, int w, int h, RectI rect, int redX, int redY, float k, float[] lutR, float[] lutG, float[] lutB, out int patched)
    {
        var outp = new ushort[(long)w * h];
        patched = RunInto(raw, w, h, rect, redX, redY, k, lutR, lutG, lutB, outp, w, rect.Y0 * w + rect.X0);
        return outp;
    }

    /// <summary>Patch <paramref name="rect"/> (frame coordinates) of <paramref name="raw"/> into <paramref name="dst"/>
    /// (row stride <paramref name="dstStride"/>, <paramref name="dstOffset"/> = index of the rect's top-left).</summary>
    public static int RunInto(ushort[] raw, int w, int h, RectI rect, int redX, int redY, float k, float[] lutR, float[] lutG, float[] lutB, ushort[] dst, int dstStride, int dstOffset)
    {
        // LUT per CFA site: row parity → (col parity 0, col parity 1), from the red position
        float[][] site = new float[4][];
        int i6, i8, i7, i9;
        if (redY == 0) { i8 = redX == 0 ? 1 : 0; i6 = redX != 0 ? 1 : 0; i7 = 2 - i8; i9 = i8 + 1; }
        else { i7 = redX != 0 ? 1 : 0; i9 = redX == 0 ? 1 : 0; i6 = 2 - i9; i8 = i9 + 1; }
        float[] Pick(int i) => i == 0 ? lutR : i == 1 ? lutG : lutB;
        site[0] = Pick(i6); site[1] = Pick(i8); site[2] = Pick(i7); site[3] = Pick(i9);
        float k4 = k * ThresholdScale;
        int rbClass = (redX ^ redY) & 1;

        int bx0 = rect.X0 - 4, by0 = rect.Y0 - 4, bw = rect.Width + 8, bh = rect.Height + 8;
        var flags = new ushort[bw * bh];
        // Frame-border rule: the per-tile source view is padded by 6 with a
        // parity-preserving edge extension — an out-of-frame pixel (X,Y) is the median-ish (`sorted[n>>1]`) of the in-frame taps of
        // {Xs±{0,2}} × {Ys±{0,2}} around the nearest in-frame pixel of the same parity; in-frame pixels are read verbatim. No clamping.
        var padCache = new Dictionary<long, ushort>();
        ushort Raw(int x, int y)
        {
            if (x >= 0 && y >= 0 && x < w && y < h) return raw[(long)y * w + x];
            long key = ((long)y << 32) ^ (uint)x;
            if (padCache.TryGetValue(key, out var cached)) return cached;
            int xs = x < 0 ? (x & 1) : x >= w ? w - 2 + ((x - w) & 1) : x;
            int ys = y < 0 ? (y & 1) : y >= h ? h - 2 + ((y - h) & 1) : y;
            Span<int> taps = stackalloc int[9]; int n = 0;
            for (int j = -2; j <= 2; j += 2) for (int i = -2; i <= 2; i += 2)
            {
                int tx = xs + i, ty = ys + j;
                if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                int v = raw[(long)ty * w + tx]; int k = n++;   // insertion sort ascending
                while (k > 0 && taps[k - 1] > v) { taps[k] = taps[k - 1]; k--; }
                taps[k] = v;
            }
            ushort r = (ushort)(n == 0 ? 0 : taps[n >> 1]);
            padCache[key] = r; return r;
        }
        // ---- pass 1: candidate flags ----
        // The vector code does the interior pixels of a row (all taps inside the frame) 16 or 8 at a time. The scalar code does
        // the edge pixels, the row ends, and the rest of a row from a group with a base beyond the σ table.
        int lanes = _dbg is not null || raw.Length < (long)w * h ? 0 : Vector256.IsHardwareAccelerated ? 16 : Vector128.IsHardwareAccelerated ? 8 : 0;
        float[]? pairs0 = null, pairs1 = null;
        if (lanes != 0 && site[0].Length == site[1].Length && site[2].Length == site[3].Length)
        { pairs0 = Interleave(site[0], site[1]); pairs1 = Interleave(site[2], site[3]); }
        else lanes = 0;
        for (int by = 0; by < bh; by++)
        {
            int y = by0 + by;
            var lutPair0 = site[(y & 1) * 2]; var lutPair1 = site[(y & 1) * 2 + 1];
            int xv0 = bx0, xv1 = bx0;   // vector range: even start, whole groups, taps at x ± 2 and y ± 2 inside the frame
            if (lanes != 0 && y >= 2 && y + 2 < h)
            {
                int s = Math.Max(bx0, 2); s += s & 1;
                int groups = (Math.Min(bx0 + bw, w - 2) - s) / lanes;
                if (groups > 0) { xv0 = s; xv1 = s + groups * lanes; }
            }
            for (int x = bx0; x < xv0; x++) Flag(x, y, by, lutPair0, lutPair1);
            if (xv1 > xv0)
            {
                var pairs = (y & 1) == 0 ? pairs0! : pairs1!;
                int rbParity = (y & 1) ^ rbClass;   // column parity of the R/B sites in this row
                xv1 = lanes == 16
                    ? FlagRow256(raw, w, y, xv0, xv1, pairs, rbParity, k4, flags, by * bw - bx0)
                    : FlagRow128(raw, w, y, xv0, xv1, pairs, rbParity, k4, flags, by * bw - bx0);
            }
            for (int x = xv1; x < bx0 + bw; x++) Flag(x, y, by, lutPair0, lutPair1);
        }
        void Flag(int x, int y, int by, float[] lutPair0, float[] lutPair1)
        {
            uint up = Raw(x, y - 2), down = Raw(x, y + 2), left = Raw(x - 2, y), right = Raw(x + 2, y);
            uint d1, d2, d3, d4;
            if (((x ^ y) & 1) == rbClass) { d1 = Raw(x - 2, y - 2); d2 = Raw(x + 2, y - 2); d3 = Raw(x - 2, y + 2); d4 = Raw(x + 2, y + 2); }
            else { d1 = Raw(x - 1, y - 1); d2 = Raw(x + 1, y - 1); d3 = Raw(x - 1, y + 1); d4 = Raw(x + 1, y + 1); }
            uint stat = Network(up, down, left, right, d1, d2, d3, d4);
            uint centre = Raw(x, y);
            uint excess = centre > stat ? centre - stat : 0;
            uint bse = centre - excess;
            float sigma = ((x & 1) == 0 ? lutPair0 : lutPair1)[bse] * k4;
            flags[by * bw + (x - bx0)] = (float)excess <= sigma ? (ushort)0 : (ushort)(bse | 0x8000);
            if (_dbg is not null && x >= _dbg[0] && x < _dbg[2] && y >= _dbg[1] && y < _dbg[3] && excess > 0 && (float)excess > sigma * 0.7f)
                Console.Error.WriteLine($"[hp1] ({x},{y}) center {centre} stat {stat} excess {excess} sigma4k {sigma:R} lut[{bse}] {((x & 1) == 0 ? lutPair0 : lutPair1)[bse]:R} k4 {k4:R} flagged {(float)excess > sigma}");
        }
        // ---- pass 2: decision + patch ----
        int patched = 0;
        int F(int x, int y) => (flags[(y - by0) * bw + (x - bx0)] & 0x8000) != 0 ? 1 : 0;
        for (int y = rect.Y0; y < rect.Y1; y++)
        {
            int flagRow = (y - by0) * bw - bx0, srcRow = y * w, dstRow = dstOffset + (y - rect.Y0) * dstStride - rect.X0;
            for (int x = rect.X0; x < rect.X1; x++)
            {
                // copy the run of pixels without a candidate that starts here
                int run = flags.AsSpan(flagRow + x, rect.X1 - x).IndexOfAnyExcept((ushort)0);
                if (run != 0)
                {
                    if (run < 0) run = rect.X1 - x;
                    raw.AsSpan(srcRow + x, run).CopyTo(dst.AsSpan(dstRow + x, run));
                    x += run;
                    if (x == rect.X1) break;
                }
                ushort v = raw[srcRow + x];
                ushort f = flags[flagRow + x];
                bool patch = false;
                if (f != 0)
                {
                    if (((x ^ y) & 1) == rbClass)
                    {
                        int ring = F(x - 2, y - 2) + F(x, y - 2) + F(x + 2, y - 2) + F(x - 2, y) + F(x + 2, y) + F(x - 2, y + 2) + F(x, y + 2) + F(x + 2, y + 2);
                        int near = F(x, y + 1) + F(x + 1, y) + F(x - 1, y) + F(x, y - 1) + F(x + 1, y + 1) + F(x - 1, y + 1) + F(x + 1, y - 1) + F(x - 1, y - 1);
                        if (ring == 1)
                        {
                            int n = near
                                + (F(x + 2, y + 2) & F(x + 4, y + 4)) + (F(x, y + 2) & F(x, y + 4)) + (F(x - 2, y + 2) & F(x - 4, y + 4))
                                + (F(x + 2, y) & F(x + 4, y)) + (F(x - 2, y) & F(x - 4, y))
                                + (F(x + 2, y - 2) & F(x + 4, y - 4)) + (F(x, y - 2) & F(x, y - 4)) + (F(x - 2, y - 2) & F(x - 4, y - 4));
                            patch = n == 0;
                        }
                        else if (ring == 0) patch = near < 2;
                    }
                    else
                    {
                        int axial = F(x, y + 1) + F(x, y - 1) + F(x + 1, y) + F(x - 1, y);
                        int same = F(x, y - 2) + F(x - 1, y - 1) + F(x + 1, y - 1) + F(x - 2, y) + F(x + 2, y) + F(x - 1, y + 1) + F(x + 1, y + 1) + F(x, y + 2);
                        if (same == 1)
                        {
                            int n = axial
                                + (F(x + 1, y - 2) & (F(x + 1, y - 1) | F(x, y - 2))) + (F(x - 1, y - 2) & (F(x - 1, y - 1) | F(x, y - 2)))
                                + (F(x - 1, y + 2) & (F(x, y + 2) | F(x - 1, y + 1))) + (F(x + 1, y + 2) & (F(x, y + 2) | F(x + 1, y + 1)))
                                + (F(x + 2, y - 1) & (F(x + 2, y) | F(x + 1, y - 1))) + (F(x + 2, y + 1) & (F(x + 1, y + 1) | F(x + 2, y)))
                                + (F(x - 2, y - 1) & (F(x - 2, y) | F(x - 1, y - 1))) + (F(x - 2, y + 1) & (F(x - 1, y + 1) | F(x - 2, y)))
                                + (F(x + 2, y) & F(x + 4, y)) + (F(x + 1, y - 1) & F(x + 2, y - 2)) + (F(x + 1, y + 1) & F(x + 2, y + 2))
                                + (F(x - 1, y - 1) & F(x - 2, y - 2)) + (F(x - 1, y + 1) & F(x - 2, y + 2)) + (F(x, y - 2) & F(x, y - 4))
                                + (F(x, y + 2) & F(x, y + 4)) + (F(x - 2, y) & F(x - 4, y));
                            patch = n == 0;
                        }
                        else if (same == 0)
                        {
                            int n = axial
                                + (F(x, y + 1) & F(x + 1, y + 2)) + (F(x - 1, y + 2) & F(x, y + 1))
                                + (F(x, y - 1) & F(x + 1, y - 2)) + (F(x - 1, y - 2) & F(x, y - 1))
                                + (F(x - 1, y) & F(x - 2, y + 1)) + (F(x - 2, y - 1) & F(x - 1, y))
                                + (F(x + 1, y) & F(x + 2, y + 1)) + (F(x + 2, y - 1) & F(x + 1, y));
                            patch = n < 2;
                        }
                    }
                }
                if (patch) { v = (ushort)(f & 0x7fff); patched++; }
                if (_dbg is not null && x >= _dbg[0] && x < _dbg[2] && y >= _dbg[1] && y < _dbg[3] && f != 0)
                    Console.Error.WriteLine($"[hp] ({x},{y}) raw {raw[(long)y * w + x]} flag base {f & 0x7fff} rb {((x ^ y) & 1) == rbClass} patch {patch} -> {v}");
                dst[dstRow + x] = v;
            }
        }
        return patched;
    }

    /// <summary>The two σ tables of one row parity in one table: entry 2·v is <paramref name="even"/>[v] (even columns), entry 2·v + 1
    /// is <paramref name="odd"/>[v] (odd columns).</summary>
    static float[] Interleave(float[] even, float[] odd)
    {
        var t = new float[even.Length * 2];
        for (int v = 0; v < even.Length; v++) { t[2 * v] = even[v]; t[2 * v + 1] = odd[v]; }
        return t;
    }

    /// <summary>Pass 1 for the pixels [<paramref name="x0"/>, <paramref name="x1"/>) of row <paramref name="y"/>, 16 at a time. The
    /// range starts at an even column, holds whole groups of 16, and has all taps inside the frame. Each lane does the steps of the
    /// scalar code: the integer network, base = min(centre, stat), excess = centre − base, the σ gather from the interleaved
    /// table, and the same float compare. Returns <paramref name="x1"/>, or the start of the first group with a base at or beyond the
    /// table length (the caller runs the scalar code from there).</summary>
    static unsafe int FlagRow256(ushort[] raw, int w, int y, int x0, int x1, float[] pairs, int rbParity, float k4, ushort[] flags, int flagOffset)
    {
        ref ushort r = ref MemoryMarshal.GetArrayDataReference(raw);
        ref ushort f = ref MemoryMarshal.GetArrayDataReference(flags);
        long rowC = (long)y * w, rowU2 = rowC - 2L * w, rowU1 = rowC - w, rowD1 = rowC + w, rowD2 = rowC + 2L * w;
        var parity = Vector256.Create((ushort)0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1);
        var rb = Vector256.Equals(parity, Vector256.Create((ushort)rbParity));   // R/B lanes take the diagonals at distance 2
        var limit = Vector256.Create((ushort)Math.Min(pairs.Length >> 1, ushort.MaxValue));
        var column = Vector256.Create(0, 1, 0, 1, 0, 1, 0, 1);   // column parity of the lanes of each half
        var k4v = Vector256.Create(k4);
        var flagBit = Vector256.Create((ushort)0x8000);
        fixed (float* p = pairs)
            for (int x = x0; x < x1; x += 16)
            {
                var up = Vector256.LoadUnsafe(ref r, (nuint)(rowU2 + x)); var down = Vector256.LoadUnsafe(ref r, (nuint)(rowD2 + x));
                var left = Vector256.LoadUnsafe(ref r, (nuint)(rowC + x - 2)); var right = Vector256.LoadUnsafe(ref r, (nuint)(rowC + x + 2));
                var d1 = Vector256.ConditionalSelect(rb, Vector256.LoadUnsafe(ref r, (nuint)(rowU2 + x - 2)), Vector256.LoadUnsafe(ref r, (nuint)(rowU1 + x - 1)));
                var d2 = Vector256.ConditionalSelect(rb, Vector256.LoadUnsafe(ref r, (nuint)(rowU2 + x + 2)), Vector256.LoadUnsafe(ref r, (nuint)(rowU1 + x + 1)));
                var d3 = Vector256.ConditionalSelect(rb, Vector256.LoadUnsafe(ref r, (nuint)(rowD2 + x - 2)), Vector256.LoadUnsafe(ref r, (nuint)(rowD1 + x - 1)));
                var d4 = Vector256.ConditionalSelect(rb, Vector256.LoadUnsafe(ref r, (nuint)(rowD2 + x + 2)), Vector256.LoadUnsafe(ref r, (nuint)(rowD1 + x + 1)));
                var centre = Vector256.LoadUnsafe(ref r, (nuint)(rowC + x));
                var bse = Vector256.Min(centre, Network(up, down, left, right, d1, d2, d3, d4));
                if (Vector256.GreaterThanOrEqualAny(bse, limit)) return x;
                var excess = centre - bse;
                var (bLo, bHi) = Vector256.Widen(bse); var (eLo, eHi) = Vector256.Widen(excess);
                var sLo = Simd.GatherVector256(p, (bLo.AsInt32() << 1) + column, 4);
                var sHi = Simd.GatherVector256(p, (bHi.AsInt32() << 1) + column, 4);
                var keepLo = Vector256.LessThanOrEqual(Vector256.ConvertToSingle(eLo.AsInt32()), sLo * k4v).AsInt32();
                var keepHi = Vector256.LessThanOrEqual(Vector256.ConvertToSingle(eHi.AsInt32()), sHi * k4v).AsInt32();
                var keep = Vector256.Narrow(keepLo, keepHi).AsUInt16();   // all ones where the pixel is not a candidate
                Vector256.AndNot(bse | flagBit, keep).StoreUnsafe(ref f, (nuint)(flagOffset + x));
            }
        return x1;
    }

    /// <summary><see cref="FlagRow256"/> with 8 pixels per group.</summary>
    static unsafe int FlagRow128(ushort[] raw, int w, int y, int x0, int x1, float[] pairs, int rbParity, float k4, ushort[] flags, int flagOffset)
    {
        ref ushort r = ref MemoryMarshal.GetArrayDataReference(raw);
        ref ushort f = ref MemoryMarshal.GetArrayDataReference(flags);
        long rowC = (long)y * w, rowU2 = rowC - 2L * w, rowU1 = rowC - w, rowD1 = rowC + w, rowD2 = rowC + 2L * w;
        var parity = Vector128.Create((ushort)0, 1, 0, 1, 0, 1, 0, 1);
        var rb = Vector128.Equals(parity, Vector128.Create((ushort)rbParity));
        var limit = Vector128.Create((ushort)Math.Min(pairs.Length >> 1, ushort.MaxValue));
        var column = Vector128.Create(0, 1, 0, 1);
        var k4v = Vector128.Create(k4);
        var flagBit = Vector128.Create((ushort)0x8000);
        fixed (float* p = pairs)
            for (int x = x0; x < x1; x += 8)
            {
                var up = Vector128.LoadUnsafe(ref r, (nuint)(rowU2 + x)); var down = Vector128.LoadUnsafe(ref r, (nuint)(rowD2 + x));
                var left = Vector128.LoadUnsafe(ref r, (nuint)(rowC + x - 2)); var right = Vector128.LoadUnsafe(ref r, (nuint)(rowC + x + 2));
                var d1 = Vector128.ConditionalSelect(rb, Vector128.LoadUnsafe(ref r, (nuint)(rowU2 + x - 2)), Vector128.LoadUnsafe(ref r, (nuint)(rowU1 + x - 1)));
                var d2 = Vector128.ConditionalSelect(rb, Vector128.LoadUnsafe(ref r, (nuint)(rowU2 + x + 2)), Vector128.LoadUnsafe(ref r, (nuint)(rowU1 + x + 1)));
                var d3 = Vector128.ConditionalSelect(rb, Vector128.LoadUnsafe(ref r, (nuint)(rowD2 + x - 2)), Vector128.LoadUnsafe(ref r, (nuint)(rowD1 + x - 1)));
                var d4 = Vector128.ConditionalSelect(rb, Vector128.LoadUnsafe(ref r, (nuint)(rowD2 + x + 2)), Vector128.LoadUnsafe(ref r, (nuint)(rowD1 + x + 1)));
                var centre = Vector128.LoadUnsafe(ref r, (nuint)(rowC + x));
                var bse = Vector128.Min(centre, Network(up, down, left, right, d1, d2, d3, d4));
                if (Vector128.GreaterThanOrEqualAny(bse, limit)) return x;
                var excess = centre - bse;
                var (bLo, bHi) = Vector128.Widen(bse); var (eLo, eHi) = Vector128.Widen(excess);
                var sLo = Simd.GatherVector128((uint*)p, (bLo.AsInt32() << 1) + column, 4).AsSingle();
                var sHi = Simd.GatherVector128((uint*)p, (bHi.AsInt32() << 1) + column, 4).AsSingle();
                var keepLo = Vector128.LessThanOrEqual(Vector128.ConvertToSingle(eLo.AsInt32()), sLo * k4v).AsInt32();
                var keepHi = Vector128.LessThanOrEqual(Vector128.ConvertToSingle(eHi.AsInt32()), sHi * k4v).AsInt32();
                var keep = Vector128.Narrow(keepLo, keepHi).AsUInt16();
                Vector128.AndNot(bse | flagBit, keep).StoreUnsafe(ref f, (nuint)(flagOffset + x));
            }
        return x1;
    }

    /// <summary><see cref="Network(uint, uint, uint, uint, uint, uint, uint, uint)"/> on 16 lanes. Integer min and max give the same
    /// value in each lane as in the scalar code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<ushort> Network(Vector256<ushort> up, Vector256<ushort> down, Vector256<ushort> left, Vector256<ushort> right, Vector256<ushort> d1, Vector256<ushort> d2, Vector256<ushort> d3, Vector256<ushort> d4)
    {
        var a = Vector256.Min(up, down); var A = Vector256.Max(up, down);
        var b = Vector256.Min(left, right); var B = Vector256.Max(left, right);
        var c = Vector256.Min(d1, d2); var C = Vector256.Max(d1, d2);
        var d = Vector256.Min(d3, d4); var Dd = Vector256.Max(d3, d4);
        var v32 = Vector256.Max(b, a);
        var v31 = Vector256.Min(A, B); var v38 = Vector256.Max(A, B);
        var v18 = Vector256.Max(c, d);
        var v34 = Vector256.Min(C, Dd); var v24 = Vector256.Max(C, Dd);
        var v40 = Vector256.Min(v31, v32); v31 = Vector256.Max(v31, v32);
        v32 = Vector256.Min(v34, v18); v34 = Vector256.Max(v34, v18);
        v32 = Vector256.Max(v32, v40);
        v34 = Vector256.Max(v34, v31);
        v24 = Vector256.Min(v38, v24);
        v24 = Vector256.Max(v24, v32);
        return Vector256.Min(v34, v24);
    }

    /// <summary><see cref="Network(uint, uint, uint, uint, uint, uint, uint, uint)"/> on 8 lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<ushort> Network(Vector128<ushort> up, Vector128<ushort> down, Vector128<ushort> left, Vector128<ushort> right, Vector128<ushort> d1, Vector128<ushort> d2, Vector128<ushort> d3, Vector128<ushort> d4)
    {
        var a = Vector128.Min(up, down); var A = Vector128.Max(up, down);
        var b = Vector128.Min(left, right); var B = Vector128.Max(left, right);
        var c = Vector128.Min(d1, d2); var C = Vector128.Max(d1, d2);
        var d = Vector128.Min(d3, d4); var Dd = Vector128.Max(d3, d4);
        var v32 = Vector128.Max(b, a);
        var v31 = Vector128.Min(A, B); var v38 = Vector128.Max(A, B);
        var v18 = Vector128.Max(c, d);
        var v34 = Vector128.Min(C, Dd); var v24 = Vector128.Max(C, Dd);
        var v40 = Vector128.Min(v31, v32); v31 = Vector128.Max(v31, v32);
        v32 = Vector128.Min(v34, v18); v34 = Vector128.Max(v34, v18);
        v32 = Vector128.Max(v32, v40);
        v34 = Vector128.Max(v34, v31);
        v24 = Vector128.Min(v38, v24);
        v24 = Vector128.Max(v24, v32);
        return Vector128.Min(v34, v24);
    }

    /// <summary>The 8-input min/max network of the tile lambda (scalar tail), literal order.</summary>
    public static uint Network(uint up, uint down, uint left, uint right, uint d1, uint d2, uint d3, uint d4)
    {
        uint a = Math.Min(up, down), A = Math.Max(up, down);
        uint b = Math.Min(left, right), B = Math.Max(left, right);
        uint c = Math.Min(d1, d2), C = Math.Max(d1, d2);
        uint d = Math.Min(d3, d4), Dd = Math.Max(d3, d4);
        uint v32 = Math.Max(b, a);
        uint v31 = Math.Min(A, B), v38 = Math.Max(A, B);
        uint v18 = Math.Max(c, d);
        uint v34 = Math.Min(C, Dd), v24 = Math.Max(C, Dd);
        uint v40 = Math.Min(v31, v32); v31 = Math.Max(v31, v32);
        v32 = Math.Min(v34, v18); v34 = Math.Max(v34, v18);
        v32 = Math.Max(v32, v40);
        v34 = Math.Max(v34, v31);
        v24 = Math.Min(v38, v24);
        v24 = Math.Max(v24, v32);
        v24 = Math.Min(v34, v24);
        return v24;
    }
}

/// <summary>Bayer-domain stage `HotPixelRemoval:default`.</summary>
public sealed class HotPixelRemovalStage : IStage
{
    public StageName Stage => StageName.HotPixelRemoval;
    public string TypeString => "default";
    public StageMeta Meta => new(HotPixelKernel.Pad, HotPixelKernel.Align, 1f);
    public void Apply(IspPayload p)
    {
        var src = p.Raw ?? throw new InvalidOperationException("HotPixelRemoval needs a Bayer ushort source");
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        var red = p.Context.Module.SensorBayerRedOverride;
        // the stats' sensor σ tables at THIS frame's gain
        var lut = p.Stats.Noise?.SigmaTables(p.Frame.AnalogGain) ?? p.Stats.NoiseSigma ?? throw new InvalidOperationException("HotPixelRemoval needs the sensor σ tables (Stats.Noise)");
        if (Environment.GetEnvironmentVariable("LUX_HP_DUMP") is string dumpPrefix)
            for (int c = 0; c < lut.Length; c++) { var b = new byte[lut[c].Length * 4]; Buffer.BlockCopy(lut[c], 0, b, 0, b.Length); File.WriteAllBytes($"{dumpPrefix}_sigma{c}.f32", b); }
        var outImg = new Image<ushort>(abs);
        HotPixelKernel.RunInto(src.Data, src.Stride, src.Data.Length / src.Stride, abs, red?.X ?? 0, red?.Y ?? 0, 1f, lut[0], lut[1], lut[2], outImg.Data, outImg.Stride, 0);
        p.Raw = outImg;   // the patched image replaces the payload source
    }
}
