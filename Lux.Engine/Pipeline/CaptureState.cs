using System.Runtime.Intrinsics;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline;

/// <summary>
/// Load-time state Lumen derives from the capture before any rendering (SoT §3): the AsShot neutral, the exposure
/// offset, the raw per-CFA-site histograms of the reference frame and the lens-shading multiplier computed from
/// them, and the tone-curve selection. Pure functions of the `.lri`; nothing here is tuned.
/// </summary>
public sealed class CaptureState
{

    /// <summary>Lumen's per-frame black-level estimate (`CapturedImage` `FUN_180125d10`, called from the stream loader
    /// `1802095e0` L~420 with the AsShot neutral, `black0 = 42.0` (`DAT_1806b5510`), `range = 1.2` (`DAT_1806b5514`), 40 steps):
    /// grey-world search over `b_i = black0 + i·(range/40)` minimising
    /// `|((B̄·gB + R̄·gR) − b·(gB + gR))·(−0.5) + ((Ḡ₁ + Ḡ₂)/(2·nG) − b·(1/nG))|`, g = rcpNR(n) of the AsShot neutral,
    /// site means in colour order (R, G, G, B); only when `black0 ≤ mean of the four site means`, else <paramref name="fallback"/>.
    /// Guarded to colour frames of sensor type AR1335 (Lumen: frame-info type == 2). Asm-exact float order.
    /// <para><paramref name="siteMeans"/> is in **raster** order and is reordered here; Lumen instead reads the shadow means
    /// already permuted by `FUN_180126b00` (see <see cref="LumenHistograms"/>), whose Gr/Gb pair is `raster[1^p]`/`raster[2^p]`
    /// while the ascending pick below yields the two greens in raster order. The two differ (Gr↔Gb swapped) for
    /// `p = redX+2·redY ∈ {2,3}`, which only permutes the addends of `avg` — and `avg` is used solely for the
    /// `black0 ≤ avg` guard, so it is a no-op unless `avg` lands within an ULP of 42.0.</para></summary>
    public static float EstimateFrameBlack(float[] siteMeans, int redX, int redY, float[] neutral, float fallback,
                                           float black0 = 42.0f, float range = 1.2000000476837158f, int steps = 40)
    {
        int rs = (redY & 1) * 2 + (redX & 1), bs = ((redY + 1) & 1) * 2 + ((redX + 1) & 1);
        float mR = siteMeans[rs], mB = siteMeans[bs];
        var gIdx = Enumerable.Range(0, 4).Where(i => i != rs && i != bs).ToArray();
        float mG1 = siteMeans[gIdx[0]], mG2 = siteMeans[gIdx[1]];
        // FUN_180125d10: m[0]=R, m[1]=G, m[2]=G, m[3]=B; avg = (m2 + m3 + (m1 + m0))·0.25
        float m0 = mR, m1 = mG1, m2 = mG2, m3 = mB;
        float avg = (m2 + m3 + (m1 + m0)) * 0.25f;
        if (!(black0 <= avg)) return fallback;
        float invG = 1.0f / neutral[1];
        float gSum = (m2 + m1) / (neutral[1] + neutral[1]);
        float step = range / (float)steps;
        float gR = RcpNr(neutral[0]), gB = RcpNr(neutral[2]);
        float gRB = gB + gR;
        float mRB = m3 * gB + m0 * gR;
        float best = 1000000f, bestB = black0;
        for (int i = 0; i < steps; i++)
        {
            float b = (float)i * step + black0;
            float t4 = gSum - b * invG;
            float t6 = mRB - b * gRB;
            float cost = MathF.Abs(t6 * -0.5f + t4);
            if (cost < best) bestB = b;
            if (cost <= best) best = cost;
        }
        return bestB;
    }
    private static float RcpNr(float x)
    {
        float r = System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.ReciprocalScalar(System.Runtime.Intrinsics.Vector128.CreateScalar(x)).GetElement(0) : 1f / x;
        return ((1f - x * r) * r + r) * 1f;
    }

    /// <summary>Four 1024-bin histograms of the raw 10-bit DN, one per CFA site index = rowParity·2 + colParity
    /// (`FUN_180127dc0`: rows iterated bottom-up when <c>sensor_is_vertical_flip</c>; columns reversed when
    /// <c>sensor_is_horizontal_flip</c> — both false on every file seen).</summary>
    public long[][] RawHistograms { get; }
    /// <summary>The histograms as `CapturedImage+0x1d8` actually holds them — <see cref="LumenHistograms"/>: colour order
    /// [R, Gr, Gb, B] for a colour frame, a single summed histogram for mono. This is what `FUN_1801259a0` returns and what
    /// `FUN_180420fc0` / `FUN_180420e80` index.</summary>
    public long[][] Histograms { get; }
    public string Module { get; }
    public bool IsColour { get; }
    /// <summary>Lumen picks index 1 of `CapturedImage+0x1d8` for colour sensors (`(bayerRed.x|bayerRed.y) >= 0`), index 0 for
    /// mono (`FUN_180420fc0` disasm 0x180420fd8–0x180420ffc). Because `FUN_180126b00` stores that vector in **colour** order
    /// [R, Gr, Gb, B], index 1 is the GREEN site sharing the red row (Gr) — for an A1 frame (red at (1,0), GRBG) that is raster
    /// site 0, not the red site. For mono index 0 is the sum of all four sites.</summary>
    public int MultiplierSite => IsColour ? 1 : 0;
    /// <summary>The same index expressed in raster-site space (`MultiplierSite ^ (redX + 2·redY)`), or −1 for mono
    /// (where Lumen's single stored histogram is the sum of all four raster sites).</summary>
    public int MultiplierRasterSite { get; }
    /// <summary>Mean raw DN of the multiplier site: Σ v·h[v] / Σ h[v] in float (`0x180421040–0x1804210ae`).</summary>
    public float HistogramMean { get; }
    /// <summary>SoT §3.5: `m = min(1, max(0.1, mean·0.015517241 + (−0.5517241)))` (DAT_1806e3b50/b54, floor 0.1).</summary>
    public float LensShadingMultiplier { get; }
    public float[] Neutral { get; }
    public float ExposureRatio { get; }
    public float EvOffset { get; }

    public const float MultiplierSlope = 0.015517241f;     // DAT_1806e3b50
    public const float MultiplierOffset = -0.5517241f;     // DAT_1806e3b54
    public const float MultiplierFloor = 0.1f;             // DAT_1806a30dc

    private CaptureState(string module, bool colour, long[][] raster, long[][] hists, int rasterSite, float mean, float m,
                         float[] neutral, float ratio, float ev)
        => (Module, IsColour, RawHistograms, Histograms, MultiplierRasterSite, HistogramMean, LensShadingMultiplier, Neutral, ExposureRatio, EvOffset)
           = (module, colour, raster, hists, rasterSite, mean, m, neutral, ratio, ev);

    /// <summary>Derive the state from the reference module (the one Lumen uses for the multiplier, SoT §3.5).</summary>
    public static CaptureState FromReference(LriFile lri) => From(lri, lri.ReferenceModule);

    public static CaptureState From(LriFile lri, string module)
    {
        var mref = lri.Modules[module];
        var raster = RawSiteHistograms(lri, mref);
        var red = mref.Module.SensorBayerRedOverride;
        int rx = red?.X ?? -1, ry = red?.Y ?? -1;
        bool colour = (rx | ry) >= 0;                                // `or edi,ebx; shr edi,31` in cp.dll; unset Point2I = (−1,−1)
        var hists = LumenHistograms(raster, rx, ry);
        int site = colour ? 1 : 0;
        float mean = HistMean(hists[site]);
        float m = MultiplierOf(mean);
        float ratio = lri.LumenExposureRatio;
        return new CaptureState(module, colour, raster, hists, colour ? (site ^ (rx + 2 * ry)) : -1, mean, m,
                                lri.LumenNeutral, ratio, MathF.Log2(ratio));
    }

    /// <summary>`FUN_180126b00` — how the four raster-site histograms are stored on the `CapturedImage` (`+0x1d8`), i.e. what
    /// `FUN_1801259a0` hands `FUN_180420fc0` (the mean behind `lens_shading.multiplier`) and `FUN_180420e80` (the NLM/IR median):
    /// <list type="bullet">
    /// <item>mono (`(bayerRed.x|bayerRed.y) &lt; 0`, L47–58): **one** histogram = h0+h1+h2+h3 (and one shadow mean = the average of
    /// the four);</item>
    /// <item>colour: `switch(bayerRed.x + 2·bayerRed.y)` permutes the four into **colour order [R, Gr, Gb, B]** —
    /// case 0 → [h0,h1,h2,h3], case 1 → [h1,h0,h3,h2], case 2 → [h2,h3,h0,h1], case 3 → [h3,h2,h1,h0], i.e.
    /// `stored[j] = raster[j ^ p]` with `p = redX + 2·redY` = the red site's own raster index. The shadow means are permuted the
    /// same way (`local_98 = puVar7[…]`).</item></list>
    /// This is why `FUN_180420fc0`'s "site 1 for colour" is the green in the red row, not the red site.</summary>
    public static long[][] LumenHistograms(long[][] raster, int redX, int redY)
    {
        if ((redX | redY) < 0)
        {
            var sum = new long[raster[0].Length];
            for (int s = 0; s < 4; s++) for (int v = 0; v < sum.Length; v++) sum[v] += raster[s][v];
            return new[] { sum };
        }
        int p = redX + 2 * redY;
        return new[] { raster[0 ^ p], raster[1 ^ p], raster[2 ^ p], raster[3 ^ p] };
    }

    /// <summary>`0x18048a997–0x18048a9b2`: `min(1, max(0.1, mean·0.015517241 + (−0.5517241)))`.</summary>
    public static float MultiplierOf(float mean) => MathF.Min(1f, MathF.Max(MultiplierFloor, mean * MultiplierSlope + MultiplierOffset));

    /// <summary>`FUN_180420fc0`: numerator Σ i·h[i] accumulated as float, denominator Σ h[i] converted to float, divided.</summary>
    public static float HistMean(long[] h)
    {
        float num = 0f; long den = 0;
        for (int i = 0; i < h.Length; i++) { num += (float)(h[i] * (long)i); den += h[i]; }
        return den == 0 ? 0f : num / (float)den;
    }

    public static long[][] RawSiteHistograms(LriFile lri, LriFile.ModuleRef mref)
    {
        var raw = lri.Frame(mref, out int w, out int h);
        return SiteStats(mref.Module, raw, w, h).Hists;
    }

    /// <summary>The raw decoder's statistics (`FUN_180127dc0` → packed-10-bit kernel `FUN_18012b030`): while unpacking, only rows
    /// with `(row &amp; 6) == 0` (row mod 8 ∈ {0,1}) run the accumulating kernel, and it samples the first two pixels of every
    /// 8-pixel (10-byte) group (col mod 8 ∈ {0,1}) — a 1/16 grid covering all four CFA sites. Histogram per site over that
    /// grid, plus the "shadow" accumulator: a sampled pair (v_even, v_odd) contributes to both sites' sums/counts only when
    /// `v_even + v_odd &lt; 124`. Returns site histograms in raster-site order [(0,0),(1,0),(0,1),(1,1)].</summary>
    public static long[][] RawSiteHistograms(ushort[] raw, int w, int h, bool vflip, bool hflip) => RawSiteStats(raw, w, h, vflip, hflip).Hists;

    public static (long[][] Hists, float[] ShadowMeans) RawSiteStats(ushort[] raw, int w, int h, bool vflip, bool hflip)
    {
        var hists = new long[4][];
        for (int i = 0; i < 4; i++) hists[i] = new long[1024];
        var sum = new ulong[4]; var cnt = new ulong[4];
        for (int y = 0; y < h; y++)
        {
            int ry = vflip ? (h - 1 - y) : y;                 // row index as cp.dll iterates it
            if ((ry & 6) != 0) continue;
            int rowSite = (ry & 1) * 2;
            long b = (long)y * w;
            for (int x8 = 0; x8 + 1 < w; x8 += 8)
            {
                int cx0 = hflip ? (w - 1 - x8) : x8, cx1 = hflip ? (w - 2 - x8) : x8 + 1;
                uint v0 = (uint)(raw[b + cx0] & 0x3ff), v1 = (uint)(raw[b + cx1] & 0x3ff);
                int s0 = rowSite + (cx0 & 1), s1 = rowSite + (cx1 & 1);
                hists[s0][v0]++; hists[s1][v1]++;
                if (v0 + v1 < 0x7c) { sum[s0] += v0; sum[s1] += v1; cnt[s0]++; cnt[s1]++; }
            }
        }
        var means = new float[4];
        for (int i = 0; i < 4; i++) means[i] = cnt[i] == 0 ? 0f : (float)sum[i] / (float)cnt[i];
        return (hists, means);
    }

    /// <summary>The same statistics as <see cref="RawSiteStats"/>, but as the **Bayer-JPEG** surface decoder computes
    /// them (`FUN_180128550` `0x180128c40–0x1801291b0`, which inlines its own copy rather than calling `FUN_180127dc0`).
    /// The sampling grid is identical — rows `y = 0, 8, 16 …` taken in pairs `(y, y+1)`, columns `x = 0, 8, 16 …` taken
    /// in pairs `(x, x+1)`, i.e. the same 1/16 grid over all four CFA sites — but three things differ, and all three are
    /// visible in the disassembly:
    /// <list type="bullet">
    /// <item>the shadow sums are accumulated in <b>float</b> (`cvtsi2ss` + `addss` at `0x180128dc9…0x180128e77`) with
    ///   <b>int</b> counts, where the packed kernel accumulates both in 64-bit integers. The two agree on every
    ///   Bayer-JPEG capture measured (the `&lt; 124` shadow gate keeps ~80 k samples per site of dark values, so the
    ///   sums land near 4·10⁶ — comfortably inside float's exact-integer range; they would only diverge above 2²⁴).
    ///   Reproduced anyway, because it is what the code does;</item>
    /// <item>the value is <b>clamped</b> (`v &gt; 0x3fe → 0x3ff`) rather than masked — identical for any value the
    ///   dequantization LUT can produce (its maximum is 1023), but it is what the code does;</item>
    /// <item>`sensor_is_horizontal_flip` / `sensor_is_vertical_flip` are <b>not</b> honoured: the loop always runs rows
    ///   top-down and columns left-to-right. Both flags are false on every capture in the corpus, so this cannot be
    ///   distinguished on real data — it is recorded, not relied on.</item>
    /// </list>
    /// Sites are in raster order [(0,0), (1,0), (0,1), (1,1)], as <see cref="RawSiteStats"/> returns them.</summary>
    public static (long[][] Hists, float[] ShadowMeans) BayerJpegSiteStats(ushort[] raw, int w, int h, int stride)
    {
        var hists = new long[4][];
        for (int i = 0; i < 4; i++) hists[i] = new long[1024];
        var sum = new float[4]; var cnt = new int[4];
        for (int y = 0; y + 1 < h; y += 8)
        {
            long r0 = (long)y * stride, r1 = (long)(y + 1) * stride;
            for (int x = 0; x + 1 < w; x += 8)
            {
                uint v0 = raw[r0 + x], v1 = raw[r0 + x + 1], v2 = raw[r1 + x], v3 = raw[r1 + x + 1];
                hists[0][v0 > 0x3fe ? 0x3ff : v0]++;
                hists[1][v1 > 0x3fe ? 0x3ff : v1]++;
                hists[2][v2 > 0x3fe ? 0x3ff : v2]++;
                hists[3][v3 > 0x3fe ? 0x3ff : v3]++;
                if (v0 + v1 < 0x7c) { sum[0] += v0; cnt[0]++; sum[1] += v1; cnt[1]++; }
                if (v2 + v3 < 0x7c) { sum[2] += v2; cnt[2]++; sum[3] += v3; cnt[3]++; }
            }
        }
        var means = new float[4];
        for (int i = 0; i < 4; i++) means[i] = sum[i] / (float)cnt[i];       // `divss` with no zero guard, as compiled
        return (hists, means);
    }

    /// <summary>Pick the load-time statistics implementation the module's surface encoding selects.</summary>
    public static (long[][] Hists, float[] ShadowMeans) SiteStats(Ltpb.CameraModule m, ushort[] raw, int w, int h) =>
        m.SensorDataSurface.Format == Ltpb.CameraModule.Types.Surface.Types.FormatType.RawBayerJpeg
            ? BayerJpegSiteStats(raw, w, h, w)
            : RawSiteStats(raw, w, h, m.SensorIsVerticalFlip, m.SensorIsHorizontalFlip);
}

/// <summary>SoT §3.6 / §8.4: the four TMO_ACR curves by Lumen's tuning name; selection rule `FUN_1804aefa0`.</summary>
public static class ToneMappingSelection
{
    public const string Default = "light_v1";           // DAT_1808379d8
    public const string LowLight = "light_v1_lowlight";
    public const string V2 = "light_v2";

    /// <summary>`FUN_1804aefa0`: v2 flag → light_v2; else low-light byte → light_v1_lowlight; else light_v1.
    /// Every Lumen 2.3.0 export examined carries light_v2 (the renderer's v2 flag is set in normal use).</summary>
    public static string Select(bool rendererV2, bool lowLight) => rendererV2 ? V2 : (lowLight ? LowLight : Default);
}
