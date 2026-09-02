using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>`lt::Vec2&lt;short&gt;` flow vector (x, y) in half-resolution (collapsed) pixels.</summary>
public readonly record struct Vec2S(short X, short Y);

/// <summary>
/// `PackedBayerFusion` block flow field (spec `a9fcabcdf0d786f37.md`, cp.dll Lumen 2.3): the float→ushort row
/// converter (`FUN_18001f080(3,7)` → `0x180093eb0`), `Internal::FastCollapse` (`1801d19e0`), the in-place sqrt LUT (`1801d6c80`), the
/// 7/11-tap separable pyramid (`FUN_1801d1be0`, `ImageConvSeparable2D&lt;7,7&gt;` / `&lt;11,11&gt;`, decimator `FUN_1801d3c70`) and
/// `ComputeFlowFieldWithOverlap&lt;ushort,16,2,0&gt;` (`1801d9f00`) for the 4-level case: top level `FUN_1801dd090`/`FUN_1801df760` (8×8, ±4,
/// sub-pixel), two `ComputeFlowField&lt;ushort,16,4,1&gt;` levels (`FUN_1801e1ee0` 3×3 candidate search + `FUN_1801e26b0` ±4 refine +
/// `FUN_1801d9910` quadratic fit), the level-0 lambda_1 (`1801e35e0`) with the integer ±2 refine `FUN_1801e3900` and the validity
/// callback (`1801f8d80`). All float arithmetic is float32 in the machine association (no FMA); `(int)` casts are `cvttss2si`.
/// </summary>
public static class BlockFlow
{
    // ---------------------------------------------------------------------------------------------------------------------------------
    // Constants (float32 bit patterns from .rdata)
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly float Half = BitConverter.Int32BitsToSingle(0x3f000000);            // 0x180683140
    static readonly float Max16 = BitConverter.Int32BitsToSingle(0x477fff00);           // 0x1806831f0 = 65535.0f
    static readonly float Sixteen = BitConverter.Int32BitsToSingle(0x41800000);         // DAT_1806876b4
    static readonly float One = BitConverter.Int32BitsToSingle(0x3f800000);             // DAT_180681c78
    static readonly float Four = BitConverter.Int32BitsToSingle(0x40800000);            // DAT_180682408
    static readonly float Two = BitConverter.Int32BitsToSingle(0x40000000);             // DAT_180682414
    static readonly float Eight = BitConverter.Int32BitsToSingle(0x41000000);           // DAT_1806b0c80 lanes
    static readonly float MinusTwo = BitConverter.Int32BitsToSingle(unchecked((int)0xc0000000));    // DAT_18068240c
    static readonly float MinusFour = BitConverter.Int32BitsToSingle(unchecked((int)0xc0800000));   // DAT_1806b0c90 lane 1
    static readonly float MinusSixteen = BitConverter.Int32BitsToSingle(unchecked((int)0xc1800000)); // DAT_180687600
    static readonly float MinusOne = BitConverter.Int32BitsToSingle(unchecked((int)0xbf800000));    // DAT_180687510
    static readonly float Reject = BitConverter.Int32BitsToSingle(unchecked((int)0xc9742400));      // DAT_1806b0ca0 = -1e6f
    static readonly float MinusHalf = BitConverter.Int32BitsToSingle(unchecked((int)0xbf000000));   // 0x180681c7c
    static readonly float MinusThree = BitConverter.Int32BitsToSingle(unchecked((int)0xc0400000));  // 0x180681c80
    static readonly float Thr7680 = BitConverter.Int32BitsToSingle(0x45f00000);         // DAT_1806b3568
    static readonly float ScaleHalf = BitConverter.Int32BitsToSingle(0x3f000000);       // DAT_180682404

    /// <summary>7-tap kernel `DAT_1806ae860` (forward order; reversed on use).</summary>
    static readonly float[] K7 = Bits(0x3c8fb86f, 0x3e04bdba, 0x3eb46b27, 0x3eb46b27, 0x3e04bdba, 0x3c8fb86f, 0x00000000);
    /// <summary>11-tap kernel `DAT_1806ae880` (forward order; reversed on use).</summary>
    static readonly float[] K11 = Bits(0x3c82eb6d, 0x3d31f03d, 0x3dbc58fc, 0x3e1b4430, 0x3e475dae, 0x3e475dae, 0x3e1b4430, 0x3dbc58fc, 0x3d31f03d, 0x3c82eb6d, 0x00000000);
    /// <summary>Per-level downsampling factors `UNK_1806ae7bc` (index = level).</summary>
    static readonly int[] Factors = { 0, 2, 4, 4, 4, 4, 2, 2 };

    static float[] Bits(params int[] b) { var f = new float[b.Length]; for (int i = 0; i < b.Length; i++) f[i] = BitConverter.Int32BitsToSingle(b[i]); return f; }
    static float Abs(float a) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(a) & 0x7fffffff);
    static float Neg(float a) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(a) ^ unchecked((int)0x80000000));
    /// <summary>`maxss/maxps dst=a, src=b`: a &gt; b ? a : b (NaN → b).</summary>
    static float Max(float a, float b) => a > b ? a : b;
    /// <summary>`minss/minps dst=a, src=b`: a &lt; b ? a : b (NaN → b).</summary>
    static float Min(float a, float b) => a < b ? a : b;
    static int Clamp(int v, int lo, int hi) { if (v < lo) v = lo; if (v > hi) v = hi; return v; }
    static float Rsqrt(float v) => Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(v)).ToScalar();

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 1. Float → ushort row converter FUN_18001f080(3,7) → 0x180093eb0 (called per row by FUN_1801f8380)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`t = v + (sign(v) | 0.5f); t = max(t, 0); t = min(t, 65535); dst = (ushort)cvttps2dq(t)` — round half away from zero, clamp.</summary>
    public static ushort[] ToUshort(float[] src, int w, int h)
    {
        if (src.Length < w * h) throw new ArgumentException("source too small", nameof(src));
        var dst = new ushort[w * h];
        for (int i = 0; i < w * h; i++)
        {
            float v = src[i];
            float bias = BitConverter.Int32BitsToSingle((BitConverter.SingleToInt32Bits(v) & unchecked((int)0x80000000)) | BitConverter.SingleToInt32Bits(Half));
            float t = v + bias;
            t = Max(t, 0f);
            t = Min(t, Max16);
            dst[i] = (ushort)(int)t;
        }
        return dst;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 2. Internal::FastCollapse (1801d19e0, lambda_0 1801d2690): 2×2 → 1, Tiler 256×256 over the output
    // ---------------------------------------------------------------------------------------------------------------------------------
    static short Sat16(int v) => v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;

    /// <summary>Output `(w&gt;&gt;1, h&gt;&gt;1)`. SIMD path (8 output lanes, `paddsw`/`phaddsw`/`psrlw`, int16 saturation) for the first
    /// `(x1−x0) &amp; ~7` columns of each tile row, uint32 scalar tail for the rest.</summary>
    public static ushort[] FastCollapse(ushort[] src, int w, int h, out int w2, out int h2)
    {
        w2 = w >> 1; h2 = h >> 1;
        var dst = new ushort[w2 * h2];
        if (w2 <= 0 || h2 <= 0) return dst;
        foreach (var tile in Tiler.Rects(new RectI(0, 0, w2, h2), 256, 256))
        {
            for (int y = tile.Y0; y < tile.Y1; y++)
            {
                int r0 = (2 * y) * w, r1 = (2 * y + 1) * w, o = y * w2;
                int nSimd = (tile.X1 - tile.X0) & ~7;
                int xEnd = tile.X0 + nSimd;
                for (int x = tile.X0; x < xEnd; x += 8)
                {
                    // a = paddsw(row1[2x..2x+7], row0[2x..2x+7]); b = same for 2x+8..2x+15; s = phaddsw(a, b); out = (ushort)s >> 2
                    for (int j = 0; j < 8; j++)
                    {
                        int c = 2 * (x + j);
                        short e = Sat16((short)src[r1 + c] + (short)src[r0 + c]);
                        short f = Sat16((short)src[r1 + c + 1] + (short)src[r0 + c + 1]);
                        short s = Sat16(e + f);
                        dst[o + x + j] = (ushort)((ushort)s >> 2);
                    }
                }
                for (int x = xEnd; x < tile.X1; x++)
                {
                    int c = 2 * x;
                    uint sum = (uint)src[r1 + c + 1] + src[r1 + c] + src[r0 + c + 1] + src[r0 + c];
                    dst[o + x] = (ushort)(sum >> 2);
                }
            }
        }
        return dst;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 3. In-place sqrt LUT FUN_1801d6c80: v = LUT[min(v, 4095)], LUT = DAT_1806aec40
    // ---------------------------------------------------------------------------------------------------------------------------------
    static readonly ushort[] SqrtLut = BuildSqrtLut();
    static ushort[] BuildSqrtLut()
    {
        var lut = new ushort[4096];
        for (int i = 0; i < 4096; i++) lut[i] = (ushort)(Math.Sqrt(i / 4096.0) * 2047.0);
        return lut;
    }

    public static void ApplySqrtLut(ushort[] img)
    {
        for (int i = 0; i < img.Length; i++) { uint v = img[i]; if (v > 0xffe) v = 0xfff; img[i] = SqrtLut[v]; }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 4. Pyramid FUN_1801d9e20 → FUN_1801d1be0(levels)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`level[0] = copy(input)`; level i: factor 2 → 7-tap conv + `FUN_1801d3c70` decimate to `(w&gt;&gt;1, h&gt;&gt;1)`; factor 4 → 11-tap conv,
    /// `level[i](x,y) = conv(min(4x+1, w−1), min(4y+1, h−1))` of size `(w&gt;&gt;2, h&gt;&gt;2)`.</summary>
    public static ushort[][] Pyramid(ushort[] level0, int w, int h, int levels, out (int W, int H)[] dims)
    {
        if (level0.Length < w * h) throw new ArgumentException("source too small", nameof(level0));
        if (w < 1 || h < 1) throw new ArgumentException("bayer_image is empty.");
        if (levels < 1 || levels > 6) throw new NotSupportedException("Unsupported pyramid depth!");
        var lv = new ushort[levels][];
        dims = new (int W, int H)[levels];
        lv[0] = new ushort[w * h]; Array.Copy(level0, lv[0], w * h); dims[0] = (w, h);
        for (int i = 1; i < levels; i++)
        {
            int f = Factors[i], pw = dims[i - 1].W, ph = dims[i - 1].H;
            if (f == 4)
            {
                var conv = ConvSeparable(lv[i - 1], pw, ph, K11, 5);
                int dw = pw >> 2, dh = ph >> 2;
                var o = new ushort[dw * dh];
                for (int y = 0; y < dh; y++)
                {
                    int sy = y * 4 + 1; if (sy > ph - 1) sy = ph - 1;
                    for (int x = 0; x < dw; x++)
                    {
                        int sx = x * 4 + 1; if (sx > pw - 1) sx = pw - 1;
                        o[y * dw + x] = conv[sy * pw + sx];
                    }
                }
                lv[i] = o; dims[i] = (dw, dh);
            }
            else if (f == 2)
            {
                var conv = ConvSeparable(lv[i - 1], pw, ph, K7, 3);
                int dw = pw >> 1, dh = ph >> 1;
                lv[i] = Decimate(conv, pw, ph, dw, dh); dims[i] = (dw, dh);
            }
            else throw new NotSupportedException("Unsupported downsampling factor!");
        }
        return lv;
    }

    /// <summary>`FUN_1801d3c70`: nearest resample in 16.16 fixed point, `out(x,y) = src((x·xstep)&gt;&gt;16, (y·ystep)&gt;&gt;16)`.</summary>
    static ushort[] Decimate(ushort[] src, int sw, int sh, int dw, int dh)
    {
        var o = new ushort[Math.Max(dw, 0) * Math.Max(dh, 0)];
        if (dw <= 0 || dh <= 0) return o;
        int xstep = (int)(((double)sw * 65536.0) / (double)dw);
        int ystep = (int)(((double)sh * 65536.0) / (double)dh);
        int yy = 0;
        for (int y = 0; y < dh; y++, yy += ystep)
        {
            int row = (yy >> 16) * sw, xx = 0;
            for (int x = 0; x < dw; x++, xx += xstep) o[y * dw + x] = src[row + (xx >> 16)];
        }
        return o;
    }

    /// <summary>`ImageConvSeparable2D&lt;N,N,ushort,float&gt;` with N = 2R+1 (R = 3: `1801d2b60`, R = 5: `1801d3ed0`). The kernel is reversed
    /// (`kk[i] = K[N−1−i]`, tap i at offset i−R). Vertical pass (`1801d31e0`/`1801d45a0`) into a float row with clamped rows on the
    /// R border rows, horizontal pass (`1801d3850`/`1801d47b0`) with clamped columns on the R border columns; `ushort(cvttss2si(sum))`.
    /// Associations per region are the machine ones (spec table).</summary>
    static ushort[] ConvSeparable(ushort[] src, int W, int H, float[] K, int R)
    {
        int N = 2 * R + 1;
        var kk = new float[N];
        for (int i = 0; i < N; i++) kk[i] = K[N - 1 - i];
        var dst = new ushort[W * H];
        var temp = new float[W];
        var p = new float[N];
        for (int y = 0; y < H; y++)
        {
            bool border = y < R || y >= H - R;
            for (int x = 0; x < W; x++)
            {
                float s;
                if (border)
                {
                    for (int i = 0; i < N; i++) { int r = Clamp(y - R + i, 0, H - 1); p[i] = (float)src[r * W + x] * kk[i]; }
                    if (R == 3) s = (p[6] + (p[5] + p[4])) + ((p[3] + p[2]) + (p[1] + p[0]));
                    else { s = 0f; for (int i = 0; i < N; i++) s = s + p[i]; }
                }
                else
                {
                    for (int i = 0; i < N; i++) p[i] = (float)src[(y - R + i) * W + x] * kk[i];
                    if (R == 3) s = (p[6] + (p[4] + (p[1] + p[0]))) + (p[5] + (p[3] + p[2]));
                    else s = (p[10] + (p[8] + (p[6] + (p[4] + (p[1] + p[0]))))) + (p[9] + (p[7] + (p[5] + (p[3] + p[2]))));
                }
                temp[x] = s;
            }
            for (int x = 0; x < W; x++)
            {
                bool right = x >= W - R, left = x < R;
                float s;
                if (left || right)
                {
                    for (int i = 0; i < N; i++) p[i] = kk[i] * temp[Clamp(x - R + i, 0, W - 1)];
                    if (R == 3) s = (p[6] + (p[5] + p[4])) + ((p[3] + p[2]) + (p[1] + p[0]));
                    else s = (p[10] + (p[9] + p[8])) + ((p[7] + (p[6] + (p[5] + p[4]))) + ((p[3] + p[2]) + (p[1] + p[0])));
                }
                else
                {
                    for (int i = 0; i < N; i++) p[i] = kk[i] * temp[x - R + i];
                    if (R == 3) s = (p[6] + (p[5] + p[4])) + ((p[3] + p[2]) + (p[1] + p[0]));
                    else s = (p[10] + (p[9] + (p[8] + p[7]))) + ((p[6] + (p[5] + p[4])) + ((p[3] + p[2]) + (p[1] + p[0])));
                }
                dst[y * W + x] = (ushort)(int)s;
            }
        }
        return dst;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 5. ComputeFlowFieldWithOverlap<ushort,16,2,0> (1801d9f00), 4-level path
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>Flow of `srcCollapsed` (same size as `refPyr[0]`) against the reference pyramid; output `(W/8 − 1, H/8 − 1)` of
    /// `Vec2&lt;short&gt;((short)cvttss2si(x), (short)cvttss2si(y))`. `validity(score, npos)` (true ⇒ block rejected, flow = i2 − 1e6) is only
    /// consulted at level 0 (levels 3..1 get an empty std::function); may be null.</summary>
    /// <summary>Diagnostics: intermediate per-level float flow fields / source pyramid levels of the last <see cref="ComputeFlow"/> call.</summary>
    public static Action<string, (float X, float Y)[], int, int>? TraceFlow;
    public static Action<string, ushort[], int, int>? TracePyramid;

    public static Vec2S[] ComputeFlow(ushort[][] refPyr, (int W, int H)[] dims, ushort[] srcCollapsed, int levels, Func<float, (float X, float Y), bool>? validity, out int fw, out int fh)
        => ComputeFlow(refPyr, dims, srcCollapsed, dims[0].W, dims[0].H, levels, validity, out fw, out fh);

    /// <summary>As above with the source's own size (`srcPyr = FUN_1801d1be0(copy(srcImg), refLevels)` is built from the source image, which
    /// differs from the reference by the CFA-phase crop, e.g. 2079×1559 vs 2080×1560); the output grid stays that of the reference.</summary>
    public static Vec2S[] ComputeFlow(ushort[][] refPyr, (int W, int H)[] dims, ushort[] srcCollapsed, int srcW, int srcH, int levels, Func<float, (float X, float Y), bool>? validity, out int fw, out int fh)
    {
        if (refPyr.Length != levels || dims.Length != levels) throw new ArgumentException("Ref and src pyramids must have same number of levels!");
        if (levels < 3 || levels > 6) throw new NotSupportedException("ComputeFlowField only configured for 3-6 pyramid levels!");
        if (levels != 4 && levels != 5) throw new NotSupportedException($"BlockFlow.ComputeFlow: only the 4- and 5-level paths are ported (got {levels})");
        var srcPyr = Pyramid(srcCollapsed, srcW, srcH, levels, out var sdims);
        if (TracePyramid is not null) for (int i = 0; i < levels; i++) TracePyramid($"L{i}", srcPyr[i], sdims[i].W, sdims[i].H);
        if (levels == 5)
        {
            // 1801d9f00 L200–300 (levels ≥ 5, spec a-monofusion §2.5): L4 FUN_1801dc7d0 (8-block, ±8 refine FUN_1801dd7e0) → L3 <8,4,1> (1801dcc10) →
            // L2 <16,8,1> (1801dcd90) → L1 <16,4,1> (1801dcf10) → L0 tile lambda <16,2,0>. Prior scales UNK_1806ae7bc[level].
            var f4 = TopLevelB(8, 8, refPyr[4], dims[4], srcPyr[4], sdims[4], out int w4, out int h4);
            var f3 = LevelFieldB(8, 4, refPyr[3], dims[3], srcPyr[3], sdims[3], f4, w4, h4, Factors[4], out int w3b, out int h3b);
            var f2 = LevelFieldB(16, 8, refPyr[2], dims[2], srcPyr[2], sdims[2], f3, w3b, h3b, Factors[3], out int w2b, out int h2b);
            var f1 = LevelFieldB(16, 4, refPyr[1], dims[1], srcPyr[1], sdims[1], f2, w2b, h2b, Factors[2], out int w1b, out int h1b);
            var f0 = Level0(refPyr[0], dims[0], srcPyr[0], sdims[0], f1, w1b, h1b, Factors[1], validity, out fw, out fh, radius: 2);   // <16,2,0> lambda_1 1801e35e0 → FUN_1801e3900 (±2)
            TraceFlow?.Invoke("flow4", f4, w4, h4); TraceFlow?.Invoke("flow3", f3, w3b, h3b); TraceFlow?.Invoke("flow2", f2, w2b, h2b); TraceFlow?.Invoke("flow1", f1, w1b, h1b); TraceFlow?.Invoke("flow0f", f0, fw, fh);
            var o5 = new Vec2S[fw * fh];
            for (int i = 0; i < o5.Length; i++) o5[i] = new Vec2S(unchecked((short)(int)f0[i].X), unchecked((short)(int)f0[i].Y));
            return o5;
        }

        var flow3 = TopLevel8(refPyr[3], dims[3], srcPyr[3], sdims[3], out int w3, out int h3);
        var flow2 = LevelField16(refPyr[2], dims[2], srcPyr[2], sdims[2], flow3, w3, h3, Factors[3], out int w2, out int h2);
        var flow1 = LevelField16(refPyr[1], dims[1], srcPyr[1], sdims[1], flow2, w2, h2, Factors[2], out int w1, out int h1);
        var flow0 = Level0(refPyr[0], dims[0], srcPyr[0], sdims[0], flow1, w1, h1, Factors[1], validity, out fw, out fh);
        TraceFlow?.Invoke("flow3", flow3, w3, h3); TraceFlow?.Invoke("flow2", flow2, w2, h2); TraceFlow?.Invoke("flow1", flow1, w1, h1); TraceFlow?.Invoke("flow0f", flow0, fw, fh);

        var o = new Vec2S[fw * fh];
        for (int i = 0; i < o.Length; i++) o[i] = new Vec2S(unchecked((short)(int)flow0[i].X), unchecked((short)(int)flow0[i].Y));
        return o;
    }

    /// <summary>Block view `(bx0, by0, bx0+B, by0+B) ∩ (0,0,W,H)` → origin of the B×B reference block at the view centre
    /// (`rows [h/2−B/2, h/2+B/2)`, `cols [w/2−B/2, w/2+B/2)`), or null for an empty view.</summary>
    static (int X, int Y)? RefBlock(int bx0, int by0, int B, int W, int H)
    {
        int x0 = Math.Max(0, bx0), y0 = Math.Max(0, by0), x1 = Math.Min(W, bx0 + B), y1 = Math.Min(H, by0 + B);
        if (x1 <= x0 || y1 <= y0) return null;
        int vw = x1 - x0, vh = y1 - y0;
        return (x0 + vw / 2 - B / 2, y0 + vh / 2 - B / 2);
    }

    static uint Sad(ushort[] a, int aOff, int aStride, ushort[] b, int bOff, int bStride, int B)
    {
        uint s = 0;
        for (int y = 0; y < B; y++)
        {
            int ra = aOff + y * aStride, rb = bOff + y * bStride;
            for (int x = 0; x < B; x++) { int d = a[ra + x] - b[rb + x]; s += (uint)(d < 0 ? -d : d); }
        }
        return s;
    }

    /// <summary>`FUN_1801dc7d0` (B = 8, refine `FUN_1801dd7e0` ±8) and the B/R-generalised form of `FUN_1801dd090`: out `(w/B, h/B)`,
    /// per block the ±R refine with sub-pixel fit, no candidate search.</summary>
    static (float X, float Y)[] TopLevelB(int B, int R, ushort[] refImg, (int W, int H) rd, ushort[] src, (int W, int H) sd, out int fw, out int fh)
    {
        fw = rd.W / B; fh = rd.H / B;
        var o = new (float X, float Y)[fw * fh];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                var rb = RefBlock(B * bx, B * by, B, rd.W, rd.H);
                o[by * fw + bx] = rb is null ? (0f, 0f) : RefineSub(B, refImg, rd.W, rb.Value, src, sd.W, sd.H, B * bx, B * by, null, R);
            }
        return o;
    }

    /// <summary>`ComputeFlowField&lt;ushort,B,R,1&gt;` (`1801dca90` &lt;8,8,1&gt;, `1801dcc10` &lt;8,4,1&gt;, `1801dcd90` &lt;16,8,1&gt;, `1801dcf10` &lt;16,4,1&gt;):
    /// out `(w/B, h/B)`; per block the 3×3 candidate search on the prior (B-block SAD, `ipos = trunc(pos·B)`, candidates clamped to `[0, src−B−1]`
    /// — `FUN_1801deff0` for B = 8, `FUN_1801e1ee0` for 16), then the ±R refine with sub-pixel fit.</summary>
    static (float X, float Y)[] LevelFieldB(int B, int R, ushort[] refImg, (int W, int H) rd, ushort[] src, (int W, int H) sd, (float X, float Y)[] prev, int pw, int ph, int scale, out int fw, out int fh)
    {
        fw = rd.W / B; fh = rd.H / B;
        var o = new (float X, float Y)[fw * fh];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                var rb = RefBlock(B * bx, B * by, B, rd.W, rd.H);
                if (rb is null) { o[by * fw + bx] = (0f, 0f); continue; }
                var i2 = Candidate(refImg, rd.W, rb.Value, prev, pw, ph, ((float)bx, (float)by), src, sd.W, sd.H, scale, B);
                var sub = RefineSub(B, refImg, rd.W, rb.Value, src, sd.W, sd.H, B * bx + i2.X, B * by + i2.Y, null, R);
                o[by * fw + bx] = ((float)i2.X + sub.X, (float)i2.Y + sub.Y);
            }
        return o;
    }

    /// <summary>5.1 `FUN_1801dd090`: out `(w/8, h/8)`, per block `FUN_1801df760(view, src, (8bx, 8by), emptyFn)`.</summary>
    static (float X, float Y)[] TopLevel8(ushort[] refImg, (int W, int H) rd, ushort[] src, (int W, int H) sd, out int fw, out int fh)
    {
        fw = rd.W / 8; fh = rd.H / 8;
        var o = new (float X, float Y)[fw * fh];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                var rb = RefBlock(8 * bx, 8 * by, 8, rd.W, rd.H);
                o[by * fw + bx] = rb is null ? (0f, 0f) : RefineSub(8, refImg, rd.W, rb.Value, src, sd.W, sd.H, 8 * bx, 8 * by, null);
            }
        return o;
    }

    /// <summary>5.2 `ComputeFlowField&lt;ushort,16,4,1&gt;` (`1801dcf10`, lambda `1801e23c0`): out `(w/16, h/16)`; per block the 3×3 candidate
    /// search on the prior, the ±4 refine with sub-pixel fit, `out = ((float)i2.x + sub.x, (float)i2.y + sub.y)`.</summary>
    static (float X, float Y)[] LevelField16(ushort[] refImg, (int W, int H) rd, ushort[] src, (int W, int H) sd, (float X, float Y)[] prev, int pw, int ph, int scale, out int fw, out int fh)
    {
        fw = rd.W / 16; fh = rd.H / 16;
        var o = new (float X, float Y)[fw * fh];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                var rb = RefBlock(16 * bx, 16 * by, 16, rd.W, rd.H);
                if (rb is null) { o[by * fw + bx] = (0f, 0f); continue; }
                var i2 = Candidate(refImg, rd.W, rb.Value, prev, pw, ph, ((float)bx, (float)by), src, sd.W, sd.H, scale);
                var sub = RefineSub(16, refImg, rd.W, rb.Value, src, sd.W, sd.H, 16 * bx + i2.X, 16 * by + i2.Y, null);
                o[by * fw + bx] = ((float)i2.X + sub.X, (float)i2.Y + sub.Y);
            }
        return o;
    }

    /// <summary>5.6 level-0 lambda_1 (`1801e35e0`): grid `(W/8 − 1, H/8 − 1)`, view `(8bx, 8by, 8bx+16, 8by+16)`, candidate search with
    /// `pos = (bx·0.5f, by·0.5f)`, then the integer ±2 refine `FUN_1801e3900` with the validity callback.</summary>
    static (float X, float Y)[] Level0(ushort[] refImg, (int W, int H) rd, ushort[] src, (int W, int H) sd, (float X, float Y)[] prev, int pw, int ph, int scale, Func<float, (float X, float Y), bool>? validity, out int fw, out int fh, int radius = 1)
    {
        fw = rd.W / 8 - 1; fh = rd.H / 8 - 1;
        var o = new (float X, float Y)[Math.Max(fw, 0) * Math.Max(fh, 0)];
        for (int by = 0; by < fh; by++)
            for (int bx = 0; bx < fw; bx++)
            {
                var rb = RefBlock(8 * bx, 8 * by, 16, rd.W, rd.H);
                if (rb is null) { o[by * fw + bx] = (0f, 0f); continue; }
                var i2 = Candidate(refImg, rd.W, rb.Value, prev, pw, ph, ((float)bx * ScaleHalf, (float)by * ScaleHalf), src, sd.W, sd.H, scale);
                var sub = Refine2(refImg, rd.W, rb.Value, src, sd.W, sd.H, 8 * bx + i2.X, 8 * by + i2.Y, validity, radius);
                o[by * fw + bx] = ((float)i2.X + sub.X, (float)i2.Y + sub.Y);
            }
        return o;
    }

    /// <summary>5.3 `FUN_1801e1ee0`: 16×16 SAD over the 9 prior cells around `pos/scale` (clamped), candidate `trunc(prev·scale) + ipos`
    /// clamped to `[0, src−17]`; first strict minimum wins; returns the offset relative to `ipos = trunc(pos·16)`.</summary>
    static (int X, int Y) Candidate(ushort[] refImg, int rs, (int X, int Y) rb, (float X, float Y)[] prev, int pw, int ph, (float X, float Y) pos, ushort[] src, int sw, int sh, int scale, int B = 16)
    {
        float fB = (float)B;   // DAT_1806876b4 = 16.0 (FUN_1801e1ee0) / DAT_180685d4c = 8.0 (FUN_1801deff0)
        int ix = (int)(pos.X * fB), iy = (int)(fB * pos.Y);
        float fscale = (float)scale;
        float inv = One / fscale;
        int refOff = rb.Y * rs + rb.X;
        uint best = uint.MaxValue; (int X, int Y) i2 = (0, 0);
        int cxMax = sw - B - 1, cyMax = sh - B - 1;   // FUN_1801e1ee0: w − 17; FUN_1801deff0: w − 9
        for (int dy = -1; dy <= 1; dy++)
        {
            int py = Clamp(dy + (int)(pos.Y * inv), 0, ph - 1);
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = Clamp(dx + (int)(inv * pos.X), 0, pw - 1);
                var p = prev[py * pw + px];
                int cx = (int)(p.X * fscale) + ix, cy = (int)(p.Y * fscale) + iy;
                if (cx < 0) cx = 0; if (cy < 0) cy = 0;
                if (cx > cxMax) cx = cxMax; if (cy > cyMax) cy = cyMax;
                uint sad = Sad(refImg, refOff, rs, src, cy * sw + cx, sw, B);
                if (sad < best) { best = sad; i2 = (cx - ix, cy - iy); }
            }
        }
        return i2;
    }

    /// <summary>5.1 / 5.4 `FUN_1801df760` (B = 8) and `FUN_1801e26b0` (B = 16): ±4 window search with the 11×11 cost table (−1 outside),
    /// strict-&lt; first minimum (initial `bdx = bdy = 4`), optional validity (`fn((float)best, (px/W, py/H))` true ⇒ (−1e6, −1e6)), then the
    /// 3×3 negative check → integer offset or `+ FUN_1801d9910(c)`.</summary>
    static (float X, float Y) RefineSub(int B, ushort[] refImg, int rs, (int X, int Y) rb, ushort[] src, int sw, int sh, int px, int py, Func<float, (float X, float Y), bool>? fn, int R = 4)
    {
        // R = 4: 9×9 window, 11×11 cost table (FUN_1801df760 / FUN_1801e26b0); R = 8: 17×17 window, 19×19 table (FUN_1801dd7e0, <16,8,1> lambda 1801e08c0)
        int T = 2 * R + 3, N = 2 * R + 1;
        var C = new float[T * T];
        for (int i = 0; i < C.Length; i++) C[i] = MinusOne;
        int refOff = rb.Y * rs + rb.X;
        uint best = uint.MaxValue; int bdx = R, bdy = R;
        for (int dy = 0; dy < N; dy++)
        {
            int oy = py - R + dy;
            for (int dx = 0; dx < N; dx++)
            {
                int ox = px - R + dx;
                if (ox >= 0 && ox + B <= sw && oy >= 0 && oy + B <= sh)
                {
                    uint sad = Sad(refImg, refOff, rs, src, oy * sw + ox, sw, B);
                    C[(dy + 1) * T + dx + 1] = (float)sad;
                    if (sad < best) { best = sad; bdx = dx; bdy = dy; }
                }
            }
        }
        if (fn is not null)
        {
            float score = (float)best;
            (float X, float Y) npos = ((float)px / (float)sw, (float)py / (float)sh);
            if (fn(score, npos)) return (Reject, Reject);
        }
        var c = new float[9];
        bool neg = false;
        for (int r = 0; r < 3; r++)
            for (int k = 0; k < 3; k++) { float v = C[(bdy + r) * T + bdx + k]; c[r * 3 + k] = v; if (v < 0f) neg = true; }
        if (neg) return ((float)(bdx - R), (float)(bdy - R));
        var sub = SubPixel(c);
        return ((float)(bdx - R) + sub.X, (float)(bdy - R) + sub.Y);
    }

    /// <summary>5.6 `FUN_1801e3900`: integer ±2 refine (16×16), `bdx = bdy = 2` initial, then `validity((float)best, (px/W, py/H))`
    /// (true ⇒ (−1e6, −1e6)); no sub-pixel step.</summary>
    /// <summary>The level-0 integer refine: `FUN_1801e45c0` (±1, the `&lt;16,1,0&gt;` tile lambda `1801e42a0` used by the 4-level colour flow) or `FUN_1801e3900`
    /// (±2, the `&lt;16,2,0&gt;` lambda `1801e35e0` of the 5-level mono flow — the earlier "two bodies of FUN_1801e3900" reading was these two functions;
    /// the mono L0 field is bit-exact only with ±2, 2026-08-27).</summary>
    static (float X, float Y) Refine2(ushort[] refImg, int rs, (int X, int Y) rb, ushort[] src, int sw, int sh, int px, int py, Func<float, (float X, float Y), bool>? fn, int radius)
    {
        int r = radius, n = 2 * r + 1;
        int refOff = rb.Y * rs + rb.X;
        uint best = uint.MaxValue; int bdx = r, bdy = r;
        for (int dy = 0; dy < n; dy++)
        {
            int oy = py - r + dy;
            for (int dx = 0; dx < n; dx++)
            {
                int ox = px - r + dx;
                if (ox >= 0 && ox + 16 <= sw && oy >= 0 && oy + 16 <= sh)
                {
                    uint sad = Sad(refImg, refOff, rs, src, oy * sw + ox, sw, 16);
                    if (sad < best) { best = sad; bdx = dx; bdy = dy; }
                }
            }
        }
        if (fn is null) return ((float)(bdx - r), (float)(bdy - r));
        float score = (float)best;
        (float X, float Y) npos = ((float)px / (float)sw, (float)py / (float)sh);
        if (fn(score, npos)) return (Reject, Reject);
        return ((float)(bdx - r), (float)(bdy - r));
    }

    /// <summary>5.5 `FUN_1801d9910`: least-squares quadratic on the 3×3 costs `c0..c8` (row-major, `c4` = best), exact machine order.</summary>
    static (float X, float Y) SubPixel(float[] c)
    {
        float c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7], c8 = c[8];
        float A = (((c6 + c8) + (c2 + c0)) * Four) + (c4 * MinusSixteen);
        float L0 = ((Neg(c3) - c5) + c1) + c7;
        float L1 = ((c5 + c3) - c1) - c7;
        float P0 = Max(A + L0 * Eight, 0f);
        float P1 = Max(A + L1 * Eight, 0f);
        float fxy = ((c2 + c6) * MinusFour) + ((c0 + c8) * Four);
        float prod = P1 * P0;
        float det = prod - fxy * fxy;
        float fxy2 = (0f < det) ? fxy : 0f;
        float det2 = prod - fxy2 * fxy2;
        if (det2 == 0f || float.IsNaN(det2)) return (0f, 0f);         // ucomiss/je (ZF on equal or unordered)
        float t = c6 - c2;
        float G0 = ((t + t) + ((c7 - c1) * Four)) + ((c0 * MinusTwo) + (c8 * Two));
        float G1 = ((c8 * Two) + ((c2 - c6) * Two)) + ((c0 * MinusTwo) + ((c5 - c3) * Four));
        float inv = One / det2;
        float S0 = inv * ((fxy2 * G0) - (P0 * G1));
        float S1 = inv * ((fxy2 * G1) - (P1 * G0));
        bool ok = !(Abs(S0) >= One) && !(Abs(S1) >= One);              // ucomiss/setb: below or unordered
        return ok ? (S0, S1) : (0f, 0f);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 6. Validity lambda ColorFusionBayer::initialize lambda_0 (1801f8d80)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`x = clamp(trunc(w8·p.x), 0, w8−1)`, `y = clamp(trunc(h8·p.y), 0, h8−1)`, `v = map[y·stride + x]`; `r = rsqrtss(v)`,
    /// `y1 = v·r`, `k = ((y1·r) + (−3)) · ((−0.5)·y1)` (0 when v == 0); returns `k·7680 &lt; score` (true ⇒ block rejected).</summary>
    public static Func<float, (float X, float Y), bool> ValidityFromGainMap(float[] map, int w8, int h8, int stride)
    {
        return (score, p) =>
        {
            int x = (int)((float)w8 * p.X), y = (int)((float)h8 * p.Y);
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > w8 - 1) x = w8 - 1;
            if (y > h8 - 1) y = h8 - 1;
            float v = map[y * stride + x];
            float r = Rsqrt(v);
            float y1 = v * r;
            float t = MinusHalf * y1;
            float k = ((y1 * r) + MinusThree) * t;
            if (v == 0f) k = 0f;
            return !((k * Thr7680) >= score);                            // ucomiss/setb: below or unordered
        };
    }
}
