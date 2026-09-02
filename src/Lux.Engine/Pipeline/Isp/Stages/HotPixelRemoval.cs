namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::ImagePatchHotPixels` (`18039f0b0`, tile lambda `18039f7a0`), the body of `HotPixelRemoval = default` on the
/// Bayer-ushort domain (`setHotPixelRemoval` `180400880`: slot pad 9, align 2, scale 1; lambda_11 `180414920` calls it
/// with the CapturedImage red position, the frame's sensor gain, the `lt::Sensor` σ-tables and k = 1.0).
///
/// Pass 1 (per pixel, rows/cols of the rect ±4): take the 8 same-colour neighbours — the 4 axial ones at distance 2
/// and, on R/B sites, the 4 diagonals at distance 2 or, on G sites, the 4 diagonals at distance 1 — run Lumen's
/// min/max network on them (an order statistic near the 2nd maximum), `excess = sat(centre − stat)`,
/// `base = centre − excess`; the pixel is a candidate when `excess > σ[base] · k · 4` (`DAT_180682408`), stored as
/// `base | 0x8000`. Pass 2 (rect): a candidate is patched to `base` unless its flagged neighbours say it is part of
/// a structure — R/B sites: with exactly one flagged same-colour ring neighbour, patch only if no near neighbour and
/// no ring→far (distance 4) pair is flagged; with none, patch if fewer than 2 near neighbours are flagged; G sites:
/// the analogous rule with the axial-4 near set and the knight-position pairs. σ is the per-CFA-channel noise table
/// selected by sensor gain (`FUN_180120f50`), indexed by the raw DN.
/// </summary>
public static class HotPixelKernel
{
    public const int Pad = 9, Align = 2;
    static readonly int[]? _dbg = Environment.GetEnvironmentVariable("LUX_HP_DEBUG") is string d ? d.Split(',').Select(int.Parse).ToArray() : null;
    public const float ThresholdScale = 4f;   // DAT_180682408

    /// <summary>Run on a whole frame; returns a frame-sized buffer with the rect patched (zeros elsewhere), like the
    /// cp.dll tile lambda writing into a freshly allocated output image.</summary>
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
        // LUT per CFA site: row parity → (col parity 0, col parity 1), from the red position (ImagePatchHotPixels L20–40)
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
        // Frame-border rule (`FUN_18001a330(view, src, tileRect, 6)`, spec agent 2026-08-26): the per-tile source view is padded by 6 with a
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
        for (int by = 0; by < bh; by++)
        {
            int y = by0 + by;
            var lutPair0 = site[(y & 1) * 2]; var lutPair1 = site[(y & 1) * 2 + 1];
            for (int bx = 0; bx < bw; bx++)
            {
                int x = bx0 + bx;
                uint up = Raw(x, y - 2), down = Raw(x, y + 2), left = Raw(x - 2, y), right = Raw(x + 2, y);
                uint d1, d2, d3, d4;
                if (((x ^ y) & 1) == rbClass) { d1 = Raw(x - 2, y - 2); d2 = Raw(x + 2, y - 2); d3 = Raw(x - 2, y + 2); d4 = Raw(x + 2, y + 2); }
                else { d1 = Raw(x - 1, y - 1); d2 = Raw(x + 1, y - 1); d3 = Raw(x - 1, y + 1); d4 = Raw(x + 1, y + 1); }
                uint stat = Network(up, down, left, right, d1, d2, d3, d4);
                uint centre = Raw(x, y);
                uint excess = centre > stat ? centre - stat : 0;
                uint bse = centre - excess;
                float sigma = ((x & 1) == 0 ? lutPair0 : lutPair1)[bse] * k4;
                flags[by * bw + bx] = (float)excess <= sigma ? (ushort)0 : (ushort)(bse | 0x8000);
                if (_dbg is not null && x >= _dbg[0] && x < _dbg[2] && y >= _dbg[1] && y < _dbg[3] && excess > 0 && (float)excess > sigma * 0.7f)
                    Console.Error.WriteLine($"[hp1] ({x},{y}) centre {centre} stat {stat} excess {excess} sigma4k {sigma:R} lut[{bse}] {((x & 1) == 0 ? lutPair0 : lutPair1)[bse]:R} k4 {k4:R} flagged {(float)excess > sigma}");
            }
        }
        // ---- pass 2: decision + patch ----
        int patched = 0;
        int F(int x, int y) => (flags[(y - by0) * bw + (x - bx0)] & 0x8000) != 0 ? 1 : 0;
        for (int y = rect.Y0; y < rect.Y1; y++)
        {
            for (int x = rect.X0; x < rect.X1; x++)
            {
                ushort v = raw[(long)y * w + x];
                ushort f = flags[(y - by0) * bw + (x - bx0)];
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
                dst[dstOffset + (long)(y - rect.Y0) * dstStride + (x - rect.X0)] = v;
            }
        }
        return patched;
    }

    /// <summary>The 8-input min/max network of the tile lambda (scalar tail, `18039f7a0` L470–530), literal order.</summary>
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
        // lambda_11 180414920: FUN_180120f50(Stats+0x198 sensor, module gain FUN_1801255c0) — the stats' sensor at THIS frame's gain
        var lut = p.Stats.Noise?.SigmaTables(p.Frame.AnalogGain) ?? p.Stats.NoiseSigma ?? throw new InvalidOperationException("HotPixelRemoval needs the sensor σ tables (Stats.Noise)");
        if (Environment.GetEnvironmentVariable("LUX_HP_DUMP") is string dumpPrefix)
            for (int c = 0; c < lut.Length; c++) { var b = new byte[lut[c].Length * 4]; Buffer.BlockCopy(lut[c], 0, b, 0, b.Length); File.WriteAllBytes($"{dumpPrefix}_sigma{c}.f32", b); }
        var outImg = new Image<ushort>(abs);
        HotPixelKernel.RunInto(src.Data, src.Stride, src.Data.Length / src.Stride, abs, red?.X ?? 0, red?.Y ?? 0, 1f, lut[0], lut[1], lut[2], outImg.Data, outImg.Stride, 0);
        p.Raw = outImg;   // lambda_11: the patched image replaces the payload source
    }
}
