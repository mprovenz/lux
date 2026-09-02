using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// `lt::Internal::GetValueHistogram` `180420410` (tile lambda `1804221b0`) and its percentile reader
/// `FUN_180420cf0` → `FUN_180420920` — the `SoftISP::Stats` value histogram `Pipeline::initialize` `180410ac0`
/// builds whenever `tone_adjust.type != none`, and the only thing branch B of `FUN_1804af2f0` (§4.2) needs to
/// produce `lpyr_lower/higher/mid_percentile`. Spec `a-display-isp.md` §4.2/§4.3.
/// </summary>
public static class DisplayValueHistogram
{
    public const int Bins = 8192;

    /// <summary>8192 `uint64` bins over the **raw Bayer u16 CapturedImage**, one sample per 4×4 block: the four
    /// CFA sites of the block's 2×2 quad are black-subtracted and scaled by the AWB gains, their max `v` is mapped to
    /// `bin = trunc((double)((white − 1)·v) + 0.5)` clamped to [0, 8191]. The per-worker scratch bands are merged by
    /// integer adds, so the tiling is irrelevant.</summary>
    public static long[] Build(ushort[] raw, int width, int height, int stride, RectI roi, float black, float white, float[] gains, int redX, int redY)
    {
        var hist = new long[Bins];
        float range = white - black;
        float sR = 1.0f / (gains[0] * range), sG = 1.0f / (gains[1] * range), sB = 1.0f / (range * gains[2]);
        float[][] phases =
        {
            new[] { sR, sG, sG, sB }, new[] { sG, sR, sB, sG }, new[] { sG, sB, sR, sG }, new[] { sB, sG, sG, sR },
        };
        var s4 = (redX | redY) < 0 ? phases[0] : phases[(redY & 1) * 2 + (redX & 1)];
        float wm1 = white + -1.0f;
        for (int y = roi.Y0 + (roi.Y0 & 1); y + 1 < roi.Y1 && y + 1 < height; y += 4)
            for (int x = roi.X0 + (roi.X0 & 1); x + 1 < roi.X1 && x + 1 < width; x += 4)
            {
                int o = y * stride + x;
                float f0 = ((float)(short)raw[o] - black) * s4[0];
                float f1 = ((float)(short)raw[o + 1] - black) * s4[1];
                float f2 = ((float)(short)raw[o + stride] - black) * s4[2];
                float f3 = ((float)(short)raw[o + stride + 1] - black) * s4[3];
                float a = f0 > f1 ? f0 : f1, b = f2 > f3 ? f2 : f3;
                float v = a > b ? a : b;
                float t = wm1 * v;
                int bin = (int)((double)t + 0.5);          // cvtss2sd, addsd 0.5, cvttsd2si — TRUNCATE
                if (bin < 0) bin = 0;
                if (bin > Bins - 1) bin = Bins - 1;
                hist[bin]++;
            }
        return hist;
    }

    /// <summary>`FUN_180420cf0(hist, white, d)` (a thunk that forces `black = 0`) → `FUN_180420920`: a plain forward
    /// scan of the CDF for the first bin with `cdf ≥ (u64)((float)cdf[n−1]·d)`, returning `i / white`. No interpolation.</summary>
    public static float Percentile(long[] hist, float white, float d)
    {
        if (!(white >= 0)) throw new InvalidOperationException("Invalid white level");
        if (!((float)hist.Length > white)) throw new InvalidOperationException("Invalid white level");
        if (d < 0 || d > 1) throw new InvalidOperationException("Invalid density");
        var cdf = new ulong[hist.Length];
        ulong acc = 0;
        for (int i = 0; i < hist.Length; i++) { acc += (ulong)hist[i]; cdf[i] = acc; }
        ulong t = (ulong)((float)cdf[hist.Length - 1] * d);
        for (int i = 0; i < hist.Length; i++) if (cdf[i] >= t) return ((float)i - 0.0f) / (white - 0.0f);
        return 0.5f;
    }

    /// <summary>`FUN_1804af2f0` L~branch-B (0x1804afe55 / 0x1804aff09 / 0x1804affbd): `k = exp2f(ev)`,
    /// `lower = logf(max(P(0.005)·k, 1e-5))`, `higher = logf(P(1.0)·k)`, `mid = logf(P(0.45)·k)`, all **natural** logs,
    /// then `lpyr_samples = FUN_180398be0({lower, higher}, 0.5)`.</summary>
    public static DisplayIspTuning.BranchBLpyr BranchB(long[] hist, float white, float evOffset)
    {
        float k = MuslMath.Exp2f(evOffset);
        float p005 = Percentile(hist, white, 0.005f) * k;
        float lower = MuslMath.Logf(p005 > 1e-5f ? p005 : 1e-5f);
        float higher = MuslMath.Logf(Percentile(hist, white, 1.0f) * k);
        float mid = MuslMath.Logf(Percentile(hist, white, 0.45f) * k);
        return new DisplayIspTuning.BranchBLpyr(lower, higher, mid, DisplayIspTuning.BranchBSamples(lower, higher));
    }
}
