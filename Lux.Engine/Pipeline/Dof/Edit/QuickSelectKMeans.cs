namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>`std::mt19937` as Lumen instantiates it (seeding `mt[i] = i + 1812433253 · (mt[i−1] ^ (mt[i−1] >> 30))`).</summary>
public sealed class Mt19937
{
    readonly uint[] _mt = new uint[624]; int _i = 624;
    public Mt19937(uint seed)
    {
        _mt[0] = seed;
        for (int i = 1; i < 624; i++) _mt[i] = unchecked((uint)i + 1812433253u * (_mt[i - 1] ^ (_mt[i - 1] >> 30)));
    }
    public uint Next()
    {
        if (_i >= 624)
        {
            for (int k = 0; k < 624; k++)
            {
                uint y = (_mt[k] & 0x80000000u) | (_mt[(k + 1) % 624] & 0x7fffffffu);
                _mt[k] = _mt[(k + 397) % 624] ^ (y >> 1) ^ ((y & 1u) != 0 ? 0x9908b0dfu : 0u);
            }
            _i = 0;
        }
        uint v = _mt[_i++];
        v ^= v >> 11; v ^= (v << 7) & 0x9d2c5680u; v ^= (v << 15) & 0xefc60000u; v ^= v >> 18;
        return v;
    }
    /// <summary>MSVC `uniform_int_distribution&lt;unsigned&gt;(0, n − 1)` (`_Rng_from_urng`): one draw, redrawn while it falls into the
    /// incomplete last bucket; n = 1 draws nothing.</summary>
    public uint Below(uint n)
    {
        uint ret, mask;
        do
        {
            if (n - 1 != 0) { ret = Next(); mask = 0xffffffffu; } else { ret = 0; mask = 0; }
        } while (mask / n <= ret / n && mask % n != n - 1);
        return ret % n;
    }
    /// <summary>MSVC `generate_canonical&lt;double, 53&gt;` on a 32-bit engine: two draws, <c>(a + b · 2³²) / 2⁶⁴</c>.</summary>
    public double Canonical()
    {
        double sum = 0.0, factor = 1.0;
        for (int k = 0; k < 2; k++) { sum += (double)Next() * factor; factor *= 4294967296.0; }
        return sum / factor;
    }
}

/// <summary>
/// `KMeans&lt;float,1&gt;` on vec4 samples (Lumen's parameters {0, init 1, 100 iterations, 1 attempt},
/// tolerance 1e-4): k-means++ seeding and Lloyd iterations (`quantize&lt;unsigned,4&gt;`).
/// Both steps construct a `std::random_device` and seed an `mt19937` from it — Lumen's result is therefore not reproducible from run to
/// run; here the device is a delegate. Each construction calls the device twice (the constructor's own call is discarded, the second
/// value is the seed).
/// A Lumen quirk is kept: before the centres are re-accumulated only the first <c>k</c> floats (centre 0) are zeroed (`memset` of
/// k·4 bytes on a vector of 4·k floats), so centres 1 … k−1 are the mean of <i>their previous value plus</i> their samples.
/// </summary>
public sealed class QuickSelectKMeans
{
    public const int MaxIterations = 100; public const float Tolerance = 1e-4f;
    public readonly int K; public readonly float[] Centres; public double Cost;

    static float Dist2(float[] s, long so, float[] c, int co)
    {
        float d0 = s[so] - c[co], d1 = s[so + 1] - c[co + 1], d2 = s[so + 2] - c[co + 2], d3 = s[so + 3] - c[co + 3];
        return (d3 * d3 + d2 * d2) + (d1 * d1 + d0 * d0);
    }

    public QuickSelectKMeans(float[] samples, int n, int k, Func<uint> randomDevice)
    {
        K = k; Centres = new float[k * 4];
        // ---- k-means++ seeding
        var minDist = new float[n]; Array.Fill(minDist, float.MaxValue);
        randomDevice(); var mt = new Mt19937(randomDevice());
        int idx = (int)mt.Below((uint)n);
        Array.Copy(samples, (long)idx * 4, Centres, 0, 4);
        for (int c = 1; c < k; c++)
        {
            double total = 0.0; int latest = (c - 1) * 4;
            for (int i = 0; i < n; i++)
            {
                float d = Dist2(samples, (long)i * 4, Centres, latest);
                if (d <= minDist[i]) minDist[i] = d;
                total += (double)minDist[i];
            }
            double thr = mt.Canonical() * total;
            int j = 0;
            if (n - 1 != 0)
            {
                double acc = 0.0;
                do { acc = (double)minDist[j] + acc; if (thr <= acc) break; j++; } while (j < n - 1);
            }
            Array.Copy(samples, (long)j * 4, Centres, c * 4, 4);
        }
        // ---- Lloyd iterations
        randomDevice(); var mt2 = new Mt19937(randomDevice());
        var labels = new int[n]; var dists = new float[n]; var counts = new uint[k];
        double prev = double.MaxValue, first = double.MaxValue, cost = double.MaxValue;
        for (int it = 0; it < MaxIterations; it++)
        {
            Assign(samples, n, labels, dists);
            cost = Sum(dists, n);
            if (cost == prev) break;
            double keep = cost;
            if (it != 0)
            {
                keep = first;
                if (!((prev - cost) / (first - cost) >= (double)Tolerance)) break;
            }
            Array.Clear(counts);
            for (int e = 0; e < k; e++) Centres[e] = 0f;          // k·4 bytes: centre 0 only (k = 4)
            for (int i = 0; i < n; i++)
            {
                int c = labels[i]; counts[c]++;
                for (int e = 0; e < 4; e++) Centres[c * 4 + e] = Centres[c * 4 + e] + samples[(long)i * 4 + e];
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0) Array.Copy(samples, (long)mt2.Below((uint)n) * 4, Centres, c * 4, 4);
                else
                {
                    double inv = 1.0 / (double)counts[c];
                    for (int e = 0; e < 4; e++) Centres[c * 4 + e] = (float)((double)Centres[c * 4 + e] * inv);
                }
            }
            first = keep; prev = cost;
        }
        Cost = cost;
    }

    /// <summary>The nearest centre (first minimum) and its squared distance <c>(d3² + d2²) + (d1² + d0²)</c>.</summary>
    void Assign(float[] samples, int n, int[]? labels, float[] dists)
    {
        for (int i = 0; i < n; i++)
        {
            int best = 0; float bd = Dist2(samples, (long)i * 4, Centres, 0);
            for (int c = 1; c < K; c++) { float d = Dist2(samples, (long)i * 4, Centres, c * 4); if (d < bd) { bd = d; best = c; } }
            if (labels != null) labels[i] = best;
            dists[i] = bd;
        }
    }

    /// <summary>The cost: two 2-lane double accumulators over groups of four, <c>(a2 + a0) + (a3 + a1)</c>, then the remainder in order.</summary>
    static double Sum(float[] d, int n)
    {
        double cost = 0.0; int i = 0;
        if (n >= 4)
        {
            double a0 = 0, a1 = 0, a2 = 0, a3 = 0; int m = n - (n & 3);
            for (; i < m; i += 4) { a0 += (double)d[i]; a1 += (double)d[i + 1]; a2 += (double)d[i + 2]; a3 += (double)d[i + 3]; }
            cost = (a2 + a0) + (a3 + a1);
        }
        for (; i < n; i++) cost += (double)d[i];
        return cost;
    }

    /// <summary>Each sample's squared distance to its nearest centre.</summary>
    public float[] Distances(float[] samples, int n)
    {
        var d = new float[n];
        Assign(samples, n, null, d);
        return d;
    }
}
