using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// The IR-correction database and blend estimate that feed `CrossTalkCorrection:ir_correction`
/// (`FUN_1801386a0` cell prep, `FUN_180133db0` → `FUN_1801318c0` ratio maps, `FUN_180131ad0` masked node
/// averages, `FUN_180132630` fit; histogram quantile `FUN_180420920`). The database is nine 17×13 vec4 gain maps
/// baked into cp.dll (.rdata `DAT_18068c680…18069bf20`), three variants (A/B/C) × {AR835/AR1335 per camera group
/// A/B/C with a "spectral" flag variant, IMX386 single}. Node gain = (p − 1)·0.75 + 1 per site (`_DAT_18068c230`,
/// `_DAT_180687510`), p = B·(1−blend) + A·blend, or the C map when blend = −1.
/// </summary>
public static class IrCorrection
{
    public const int Cols = 17, Rows = 13;
    static readonly float[] Tables = Load();
    static float[] Load()
    {
        using var s = typeof(IrCorrection).Assembly.GetManifestResourceStream("IrCorrectionTables.bin") ?? throw new InvalidOperationException("IrCorrectionTables.bin resource missing");
        var b = new byte[s.Length]; s.ReadExactly(b);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray();
    }
    // layout of the resource: [A,flagA,B,flagB,C,flagC] × 3 groups × 221 vec4 (types 1/2), then [A,B,C] × 221 vec4 (type 4)
    const int NodeCount = Cols * Rows, GroupFloats = NodeCount * 4;
    static readonly int[] GroupOfCamera = { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2 };   // DAT_18068be60 (FUN_18012a380)

    /// <summary>Variant 0 = A (`FUN_1801337e0`), 1 = B (`FUN_1801339d0`), 2 = C (`FUN_180133bc0`).</summary>
    public static ReadOnlySpan<float> Table(int variant, int sensorType, int cameraId, bool flag)
    {
        if (sensorType == 4) return Tables.AsSpan(6 * 3 * GroupFloats + variant * GroupFloats, GroupFloats);
        if (sensorType != 1 && sensorType != 2) throw new InvalidOperationException("Unsupported sensor type for IR correction!");
        if (cameraId < 0 || cameraId >= 16) throw new InvalidOperationException("Unsupported camera group for IR correction!");
        int g = GroupOfCamera[cameraId];
        return Tables.AsSpan(((variant * 2 + (flag ? 1 : 0)) * 3 + g) * GroupFloats, GroupFloats);
    }

    /// <summary>`FUN_1801386a0` for a 17×13 model: the per-node vec4 gain map p = (R, G, B, ·) (blend ≥ 0: B·(1−blend) + A·blend,
    /// else the C map) and the 4×4 diagonal cell matrices diag(f(p_R), f(p_G), f(p_G), f(p_B)) with f(x) = (x + (−1))·0.75 + 1
    /// (L266–303: lane 1 is stored to both green diagonals, `extractps(…,2)` to the blue one).</summary>
    public static float[] CellMatrices(float blend, int sensorType, int cameraId, bool flag)
    {
        var p = new float[GroupFloats];
        if (0f <= blend)
        {
            var a = Table(0, sensorType, cameraId, flag); var b = Table(1, sensorType, cameraId, flag);
            float q = 1f - blend;
            for (int i = 0; i < GroupFloats; i++) p[i] = b[i] * q + a[i] * blend;
        }
        else Table(2, sensorType, cameraId, flag).CopyTo(p);
        var cv = new float[NodeCount * 16];
        for (int n = 0; n < NodeCount; n++)
        {
            float fR = (p[n * 4] + -1f) * 0.75f + 1f, fG = (p[n * 4 + 1] + -1f) * 0.75f + 1f, fB = (p[n * 4 + 2] + -1f) * 0.75f + 1f;
            cv[n * 16] = fR; cv[n * 16 + 5] = fG; cv[n * 16 + 10] = fG; cv[n * 16 + 15] = fB;
        }
        return cv;
    }

    /// <summary>`FUN_1801318c0`: half-resolution R/G and B/G ratio maps of the raw frame (quad greens averaged).</summary>
    public static (float[] A, float[] B, int W2, int H2) RatioMaps(ushort[] raw, int w, int h, int stride, int redX, int redY)
    {
        int w2 = w / 2, h2 = h / 2;
        var A = new float[w2 * h2]; var B = new float[w2 * h2];
        for (int y = 0; y < h; y += 2)
        {
            int rowR = redY + y, rowO = y + 1 - redY;
            for (int x = 0; x < w; x += 2)
            {
                int colR = redX + x, colO = x - redX + 1;
                uint g1 = raw[rowO * stride + colR], g2 = raw[rowR * stride + colO];
                float f = 1f / ((float)(g1 + g2) * 0.5f);
                A[(y >> 1) * w2 + (x >> 1)] = (float)raw[rowR * stride + colR] * f;
                B[(y >> 1) * w2 + (x >> 1)] = (float)raw[rowO * stride + colO] * f;
            }
        }
        return (A, B, w2, h2);
    }

    const float GradientLimit = 0.02f;   // DAT_18068c1e4

    /// <summary>`FUN_180131ad0`: gradient masks (√(dx²+dy²) where the summed gradient energy ≤ 0.02, else 0; first
    /// row/column 0) and per-node averages of the ratio maps over mask &gt; 0 (1.0 when a node has no sample).
    /// Returns cols×rows vec4 (R/G, 0, B/G, 0).</summary>
    public static float[] NodeRatios(float[] A, float[] B, int w2, int h2, int cols, int rows)
    {
        var mA = new float[w2 * h2]; var mB = new float[w2 * h2];
        for (int y = 1; y < h2; y++)
            for (int x = 1; x < w2; x++)
            {
                int i = y * w2 + x;
                float dxA = A[i] - A[i - 1], dyA = A[i] - A[i - w2];
                float dxB = B[i] - B[i - 1], dyB = B[i] - B[i - w2];
                float magA = dyA * dyA + dxA * dxA, dxB2 = dxB * dxB, dyB2 = dyB * dyB;
                float rA = 0f, rB = 0f;
                if (magA + dxB2 + dyB2 <= GradientLimit)
                {
                    float magB = dyB2 + dxB2;
                    var v = Vector128.Create(magA, magB, 0f, 0f);
                    var r = Sse.IsSupported ? Sse.ReciprocalSqrt(v) : Vector128.Create(1f / MathF.Sqrt(magA), 1f / MathF.Sqrt(magB), 0f, 0f);
                    float tA = magA * r[0], tB = magB * r[1];
                    rA = magA != 0f ? ((tA * r[0] + -3f) * tA) * -0.5f : 0f;
                    rB = magB != 0f ? ((tB * r[1] + -3f) * tB) * -0.5f : 0f;
                }
                mA[i] = rA; mB[i] = rB;
            }
        var nodes = new float[cols * rows * 4];
        float sy = (float)h2 / (float)rows, sx = (float)w2 / (float)cols;
        for (int j = 0; j < rows; j++)
        {
            int y0 = (int)((float)j * sy), y1 = (int)((float)(j + 1) * sy);
            for (int i = 0; i < cols; i++)
            {
                int x0 = (int)((float)i * sx), x1 = (int)((float)(i + 1) * sx);
                float sumA = 0f, sumB = 0f; int nA = 0, nB = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int k = y * w2 + x;
                        if (0f < mA[k]) { sumA += A[k]; nA++; }
                        if (0f < mB[k]) { sumB += B[k]; nB++; }
                    }
                nodes[(j * cols + i) * 4] = nA < 1 ? 1f : sumA / (float)nA;
                nodes[(j * cols + i) * 4 + 2] = nB < 1 ? 1f : sumB / (float)nB;
            }
        }
        return nodes;
    }

    const float Step = 0.052631579f;   // DAT_18068c1ec = 1/19
    const float InitialBest = 1e9f;    // DAT_18068c1e8
    const float LightHi = 6504070f, LightLo = 2504070f, CctLo = 3000f, CctHi = 5000f;   // DAT_18068c1f0/1fc/1f4/1f8

    static float RcpNr(int n)
    {
        var v = Vector128.CreateScalar((float)n);
        var r = Sse.IsSupported ? Sse.ReciprocalScalar(v) : Vector128.CreateScalar(1f / (float)n);
        float rr = r.ToScalar(); return (1f - (float)n * rr) * rr + rr;
    }

    /// <summary>Score of a gain map against the measured node ratios: Σ over nodes of (mean − map⊙ratio)² for the
    /// R/G and B/G lanes, each × 1/N (rcp+Newton), summed.</summary>
    static float Score(float[] nodes, ReadOnlySpan<float> map, int cols, int rows)
    {
        int n = cols * rows;
        var p0 = new float[n]; var p2 = new float[n];
        for (int i = 0; i < n; i++) { p0[i] = nodes[i * 4] * map[i * 4]; p2[i] = nodes[i * 4 + 2] * map[i * 4 + 2]; }
        float s0 = 0f, s2 = 0f;
        for (int j = 0; j < rows; j++)
        {
            int i = 0;
            for (; i + 4 <= cols; i += 4) { int k = j * cols + i; s0 = s0 + p0[k] + p0[k + 1] + p0[k + 2] + p0[k + 3]; s2 = s2 + p2[k] + p2[k + 1] + p2[k + 2] + p2[k + 3]; }
            for (; i < cols; i++) { int k = j * cols + i; s0 = s0 + p0[k]; s2 = s2 + p2[k]; }
        }
        float inv = RcpNr(n);
        float m0 = s0 * inv, m2 = s2 * inv;
        float v0 = 0f, v2 = 0f;
        for (int j = 0; j < rows; j++)
        {
            int i = 0;
            for (; i + 2 <= cols; i += 2)
            {
                int k = j * cols + i;
                float a0 = m0 - p0[k], a2 = m2 - p2[k], b0 = m0 - p0[k + 1], b2 = m2 - p2[k + 1];
                v0 = b0 * b0 + a0 * a0 + v0; v2 = b2 * b2 + a2 * a2 + v2;
            }
            if (i < cols) { int k = j * cols + i; float a0 = m0 - p0[k], a2 = m2 - p2[k]; v0 = v0 + a0 * a0; v2 = v2 + a2 * a2; }
        }
        return v2 * inv + v0 * inv;
    }

    /// <summary>`FUN_180132630`: the blend p ∈ {k/19} minimising the score, or −1 when the C map scores better under
    /// 3000 ≤ CCT &lt; 5000 and 2504070 ≤ light &lt; 6504070.</summary>
    public static float FitBlend(float[] nodes, int cols, int rows, int cameraId, float cct, float light, int sensorType, bool flag)
    {
        var a = Table(0, sensorType, cameraId, flag); var b = Table(1, sensorType, cameraId, flag); var c = Table(2, sensorType, cameraId, flag);
        int n = cols * rows;
        float best = InitialBest, bestP = 0f;
        var map = new float[n * 4];
        for (int k = 0; k < 20; k++)
        {
            float p = (float)k * Step, q = 1f - p;
            for (int i = 0; i < n * 4; i++) map[i] = b[i] * q + a[i] * p;
            float s = Score(nodes, map, cols, rows);
            if (s < best) bestP = p;
            best = s < best ? s : best;
        }
        if (light < LightHi && CctLo <= cct && cct < CctHi && LightLo <= light)
        {
            float s = Score(nodes, c, cols, rows);
            if (s < best) return -1f;
        }
        return bestP;
    }

    /// <summary>`FUN_180420920(hist, black, white, density)`: (first bin whose cumulative count ≥ density·total − black)/(white − black).</summary>
    public static float HistogramQuantile(long[] hist, float black, float white, float density)
    {
        ulong total = 0; var cum = new ulong[hist.Length];
        for (int i = 0; i < hist.Length; i++) { total += (ulong)hist[i]; cum[i] = total; }
        float target = (float)total * density;
        ulong t = (ulong)target;
        int idx = 0;
        while (idx < hist.Length && cum[idx] < t) idx++;
        return ((float)idx - black) / (white - black);
    }

    /// <summary>`FUN_180410ac0` L200–213: light = quantile(hist site, 0.5)·analog gain·(float)exposure_ns.</summary>
    public static float LightLevel(long[] hist, float black, float white, float analogGain, ulong exposureNs)
        => HistogramQuantile(hist, black, white, 0.5f) * analogGain * (float)exposureNs;
}
