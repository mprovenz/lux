using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>`std::mt19937` (MSVC), 32-bit Mersenne twister — needed to reproduce Lumen's RANSAC samples exactly.</summary>
public sealed class Mt19937
{
    readonly uint[] _mt = new uint[624]; int _idx;
    public Mt19937(uint seed) { Seed(seed); }
    public void Seed(uint seed)
    {
        _mt[0] = seed;
        for (int i = 1; i < 624; i++) _mt[i] = (uint)(1812433253u * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + (uint)i);
        _idx = 624;
    }
    public uint Next()
    {
        if (_idx >= 624)
        {
            for (int i = 0; i < 624; i++)
            {
                uint y = (_mt[i] & 0x80000000u) | (_mt[(i + 1) % 624] & 0x7fffffffu);
                _mt[i] = _mt[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1u) != 0 ? 0x9908b0dfu : 0u);
            }
            _idx = 0;
        }
        uint r = _mt[_idx++];
        r ^= r >> 11; r ^= (r << 7) & 0x9d2c5680u; r ^= (r << 15) & 0xefc60000u; r ^= r >> 18;
        return r;
    }
    /// <summary>MSVC `uniform_int_distribution&lt;int&gt;(0, n−1)` (`_Rng_from_urng`, 32-bit, rejection).</summary>
    public int UniformInt(int n)
    {
        uint un = (uint)n;
        while (true)
        {
            uint r = Next();
            if (r / un < 0xffffffffu / un || 0xffffffffu % un == un - 1) return (int)(r % un);
        }
    }
}

/// <summary>
/// `lt::FundamentalMatrixFilter::filter` (18029f300) — 8-point RANSAC over the (ref, module) correspondences of one
/// camera: 500 pre-drawn samples from `std::mt19937(0)`, un-normalised 8-point estimate via the last column of the
/// Householder Q of the scaled design matrix (Eigen `JacobiSVD` preconditioner, reproduced op-by-op in double),
/// scoring = count of module points farther than 2 px from their epipolar line (one-sided, `rsqrtss` normalisation),
/// earliest best wins; outliers are overwritten with (−1,−1). Nothing happens with fewer than 8 valid matches.
/// </summary>
public static class FundamentalMatrixFilter
{
    const float Threshold = 2.0f;                                  // DAT_180682414
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x35a00000);   // DAT_1806b8f6c
    const int Iterations = 500;

    /// <summary>Filters `mod` in place (pairs of floats, stride 2; (−1,−1) sentinel). `refPts` = (x,y) per reference feature.</summary>
    public static void Filter(ReadOnlySpan<float> refPts, Span<float> mod)
    {
        int nRef = refPts.Length / 2, nMod = mod.Length / 2;
        var valid = new List<int>();
        for (int i = 0; i < nMod; i++) if (mod[2 * i] > 0.0f && mod[2 * i + 1] > 0.0f) valid.Add(i);
        if (valid.Count < 8) return;

        var rng = new Mt19937(0);
        var perm = valid.ToArray();
        var samples = new int[Iterations][];
        for (int it = 0; it < Iterations; it++)
        {
            for (int k = 0; k < 8; k++) { int j = rng.UniformInt(perm.Length); (perm[k], perm[j]) = (perm[j], perm[k]); }
            samples[it] = perm.AsSpan(0, 8).ToArray();
        }

        var best = new float[9]; int bestScore = int.MaxValue; bool haveBest = false;
        var F = new float[9]; var lines = new float[nRef * 3];
        for (int it = 0; it < Iterations; it++)
        {
            Estimate(refPts, mod, samples[it], F);
            int score = Score(refPts, mod, valid, F, lines, null);
            if (score < bestScore) { bestScore = score; Array.Copy(F, best, 9); haveBest = true; }
        }
        if (!haveBest) return;
        Score(refPts, mod, valid, best, lines, mod);
    }

    /// <summary>lambda_1 (1802a0390): 8×9 design matrix in double from float products, null vector = Q.col(8) of the QR of Msᵀ.</summary>
    public static void Estimate(ReadOnlySpan<float> refPts, ReadOnlySpan<float> mod, int[] sample, float[] F)
    {
        var A = new double[8, 9];
        for (int r = 0; r < 8; r++)
        {
            int i = sample[r];
            float x1 = refPts[2 * i], y1 = refPts[2 * i + 1], x2 = mod[2 * i], y2 = mod[2 * i + 1];
            A[r, 0] = (double)(x1 * x2); A[r, 1] = (double)(y1 * x2); A[r, 2] = (double)x2;
            A[r, 3] = (double)(x1 * y2); A[r, 4] = (double)(y1 * y2); A[r, 5] = (double)y2;
            A[r, 6] = (double)x1; A[r, 7] = (double)y1; A[r, 8] = 1.0;
        }
        var f = NullVector(A);
        // Matrix<float,3,3> column-major: F[0]=f0 F[1]=f3 F[2]=f6 F[3]=f1 F[4]=f4 F[5]=f7 F[6]=f2 F[7]=f5 F[8]=f8
        F[0] = (float)f[0]; F[1] = (float)f[3]; F[2] = (float)f[6]; F[3] = (float)f[1]; F[4] = (float)f[4]; F[5] = (float)f[7]; F[6] = (float)f[2]; F[7] = (float)f[5]; F[8] = (float)f[8];
    }

    /// <summary>JacobiSVD&lt;Matrix&lt;double,8,9&gt;&gt;(A, ComputeFullV).matrixV().col(8): scale by max|A|, ColPivHouseholderQR of the
    /// 9×8 transpose, then column 8 of the Householder sequence — the Jacobi sweeps never touch it.</summary>
    public static double[] NullVector(double[,] A)
    {
        double scale = 0.0;
        for (int r = 0; r < 8; r++) for (int c = 0; c < 9; c++) { double v = Math.Abs(A[r, c]); if (v > scale) scale = v; }
        if (scale == 0.0) scale = 1.0;
        double inv = 1.0 / scale;
        // B = Msᵀ : 9 rows × 8 cols, column-major storage qr[c][r]
        var qr = new double[8][]; for (int c = 0; c < 8; c++) { qr[c] = new double[9]; for (int r = 0; r < 9; r++) qr[c][r] = A[c, r] * inv; }
        var h = new double[8]; var u = new double[8]; var d = new double[8];
        for (int c = 0; c < 8; c++)
        {
            var x = qr[c];
            double s = (x[0] * x[0] + x[1] * x[1]) + ((x[2] * x[2] + (x[3] * x[3] + x[4] * x[4])) + ((x[5] * x[5] + x[6] * x[6]) + (x[7] * x[7] + x[8] * x[8])));
            d[c] = u[c] = Math.Sqrt(s);
        }
        const double DblMin = 2.2250738585072014e-308;
        const double SqrtEps = 1.4901161193847656e-08;
        for (int k = 0; k < 8; k++)
        {
            int big = k; double bu = u[k];
            for (int c = k + 1; c < 8; c++) if (u[c] > bu) { bu = u[c]; big = c; }
            if (big != k) { (qr[k], qr[big]) = (qr[big], qr[k]); (u[k], u[big]) = (u[big], u[k]); (d[k], d[big]) = (d[big], d[k]); }
            // makeHouseholder on qr[k][k..8]
            var col = qr[k];
            double c0 = col[k], tsn = 0.0;
            for (int r = k + 1; r < 9; r++) tsn += col[r] * col[r];
            double beta;
            if (tsn <= DblMin) { h[k] = 0.0; for (int r = k + 1; r < 9; r++) col[r] = 0.0; beta = c0; }
            else
            {
                beta = Math.Sqrt(c0 * c0 + tsn);
                if (!(c0 < 0.0)) beta = -beta;
                double rcp = 1.0 / (c0 - beta);
                for (int r = k + 1; r < 9; r++) col[r] = col[r] * rcp;
                h[k] = (beta - c0) / beta;
            }
            col[k] = beta;
            // apply to the block qr[k+1..7][k..8]
            int m = 8 - k, R = 7 - k;
            if (R > 0 && h[k] != 0.0 && !double.IsNaN(h[k]))
            {
                var e = new double[m]; for (int i = 0; i < m; i++) e[i] = col[k + 1 + i];
                var tmp = new double[R];
                var p = new double[m];
                for (int j = 0; j < R; j++)
                {
                    var bc = qr[k + 1 + j];
                    for (int i = 0; i < m; i++) p[i] = bc[k + 1 + i] * e[i];
                    tmp[j] = 0.0 + 1.0 * BlockDot(p, m, BlockKind(R, j));
                }
                for (int j = 0; j < R; j++)
                {
                    var bc = qr[k + 1 + j];
                    tmp[j] = tmp[j] + bc[k];
                    bc[k] = bc[k] - (tmp[j] * h[k]);
                    for (int i = 0; i < m; i++) { double w = e[i] * h[k]; bc[k + 1 + i] = bc[k + 1 + i] - (w * tmp[j]); }
                }
            }
            // column-norm downdate
            for (int j = k + 1; j < 8 && k < 7; j++)
            {
                if (u[j] == 0.0) continue;
                double t = Math.Abs(qr[j][k]) / u[j];
                t = (t + 1.0) * (1.0 - t);
                t = Math.Max(t, 0.0);
                double q = u[j] / d[j];
                double t2 = (q * q) * t;
                if (t2 <= SqrtEps)
                {
                    double s = 0.0; for (int r = k + 1; r < 9; r++) s += qr[j][r] * qr[j][r];
                    d[j] = Math.Sqrt(s); u[j] = d[j];
                }
                else u[j] = Math.Sqrt(t) * u[j];
            }
        }
        // Q.col(8) = H_0 H_1 … H_7 e_8, applied k = 7 … 0 on the corner rows k..8 (block column index j = 8 − k)
        var cvec = new double[9]; cvec[8] = 1.0;
        for (int k = 7; k >= 0; k--)
        {
            if (h[k] == 0.0 || double.IsNaN(h[k])) continue;
            int m = 8 - k, n = 9 - k;
            var p = new double[m];
            for (int i = 0; i < m; i++) p[i] = cvec[k + 1 + i] * qr[k][k + 1 + i];
            double tmp = 0.0 + 1.0 * BlockDot(p, m, BlockKind(n, n - 1));
            tmp = tmp + cvec[k];
            cvec[k] = cvec[k] - (tmp * h[k]);
            for (int i = 0; i < m; i++) { double w = qr[k][k + 1 + i] * h[k]; cvec[k + 1 + i] = cvec[k + 1 + i] - (w * tmp); }
        }
        return cvec;
    }

    /// <summary>Eigen gemv row-block kind (8/4/2/1) of block column j among n columns.</summary>
    static int BlockKind(int n, int j)
    {
        int i = 0;
        while (i < n - 7) { if (j < i + 8) return 8; i += 8; }
        while (i < n - 3) { if (j < i + 4) return 4; i += 4; }
        while (i < n - 1) { if (j < i + 2) return 2; i += 2; }
        return 1;
    }

    /// <summary>Dot-product accumulation order of the gemv kernel for a column in an 8/4-row block (even/odd lanes) or a 2/1-row block (4 lanes).</summary>
    static double BlockDot(double[] p, int m, int kind)
    {
        if (kind >= 4)
        {
            double E = 0.0, O = 0.0; int lim = m & ~1;
            for (int i = 0; i < lim; i += 2) E += p[i];
            for (int i = 1; i < lim; i += 2) O += p[i];
            double cc = E + O;
            if ((m & 1) != 0) cc += p[m - 1];
            return cc;
        }
        if (m < 4) { double cc = 0.0; for (int i = 0; i < m; i++) cc += p[i]; return cc; }
        double L0 = 0.0, L1 = 0.0, L2 = 0.0, L3 = 0.0; int lim4 = m & ~3;
        for (int i = 0; i < lim4; i += 4) { L0 += p[i]; L1 += p[i + 1]; L2 += p[i + 2]; L3 += p[i + 3]; }
        double r = (L0 + L2) + (L1 + L3);
        for (int i = lim4; i < m; i++) r += p[i];
        return r;
    }

    /// <summary>lambda_0 (1802a0df0): lines `F·(x,y,1)` for all reference points (SIMD association for groups of 4, scalar
    /// tail), then the outlier count over the valid matches. When `mark` is given, outliers are overwritten with (−1,−1).</summary>
    static int Score(ReadOnlySpan<float> refPts, ReadOnlySpan<float> modIn, List<int> valid, float[] F, float[] lines, Span<float> mark)
    {
        int nRef = refPts.Length / 2, n4 = nRef & ~3;
        for (int i = 0; i < nRef; i++)
        {
            float x = refPts[2 * i], y = refPts[2 * i + 1], z = 1.0f;
            if (i < n4)
            {
                lines[3 * i] = (x * F[0] + z * F[6]) + y * F[3];
                lines[3 * i + 1] = (x * F[1] + z * F[7]) + y * F[4];
                lines[3 * i + 2] = (x * F[2] + z * F[8]) + y * F[5];
            }
            else
            {
                lines[3 * i] = x * F[0] + (y * F[3] + z * F[6]);
                lines[3 * i + 1] = x * F[1] + (y * F[4] + z * F[7]);
                lines[3 * i + 2] = x * F[2] + (y * F[5] + z * F[8]);
            }
        }
        int outliers = 0;
        foreach (int i in valid)
        {
            float l0 = lines[3 * i], l1 = lines[3 * i + 1], l2 = lines[3 * i + 2];
            bool outlier;
            if (!(MathF.Abs(l0) > Eps) && !(MathF.Abs(l1) > Eps)) outlier = true;
            else
            {
                float s2 = (l1 * l1) + (l0 * l0);
                float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(s2)).ToScalar();
                float t = ((s2 * r) * r) + (-3.0f);
                r = (r * -0.5f) * t;
                float n0 = l0 * r, n1 = l1 * r, n2 = r * l2;
                float dd = ((n0 * modIn[2 * i]) + n2) + (n1 * modIn[2 * i + 1]);
                outlier = MathF.Abs(dd) > Threshold;
            }
            if (outlier)
            {
                outliers++;
                if (!mark.IsEmpty) { mark[2 * i] = -1.0f; mark[2 * i + 1] = -1.0f; }
            }
        }
        return outliers;
    }
}
