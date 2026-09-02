using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The homography machinery of `lt::SparseLNR`, reproduced at the instruction level: Eigen 3.4 float
/// `ColPivHouseholderQR` (4-point DLT solve, `FUN_1802f8ac0`) and `JacobiSVD` (least-squares, `FUN_1802f91b0`) as
/// compiled by clang with fast-math — `rcpps`/`rsqrtss` + one Newton step stand in for division/sqrt, and the dot-product
/// reductions follow the auto-vectorised loop shapes. Plus `FUN_1802f9930` (H change) and `FUN_1802ffcf0` (validity).
/// </summary>
public static class Homography
{
    // ---- approximate primitives (machine order) ----
    public static float Rcp(float x) { float r = Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar(); return ((1.0f - x * r) * r) + r; }
    public static float Rsqrt(float x) { float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar(); float A = x * r, C = A * r, D = C + (-3.0f), E = r * (-0.5f); return E * D; }
    public static float Sqrt(float x) { float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar(); float A = x * r, B = A * (-0.5f), C = A * r, D = C + (-3.0f), s = D * B; return x == 0.0f ? 0.0f : s; }

    const float FltMin = 1.17549435e-38f, FltEps = 1.1920929e-07f;
    static readonly float SqrtEps = BitConverter.Int32BitsToSingle(0x39b504f3);       // norm down-date threshold
    static readonly float Precision = BitConverter.Int32BitsToSingle(0x34800000);     // 2·FLT_EPSILON (Jacobi)
    static readonly float ConsiderAsZero = BitConverter.Int32BitsToSingle(0x00800000);

    /// <summary>Compiled squared-norm reduction of `v[0..n)` (§1b).</summary>
    static float SqNorm(float[] v, int off, int n)
    {
        float s = v[off] * v[off];
        if (n - 1 >= 8)
        {
            int m = (n - 1) & ~7;
            var A = new float[4]; var B = new float[4]; A[0] = s;
            for (int c = 0; c < m / 4; c++)
            {
                var acc = (c & 1) == 0 ? A : B;
                for (int l = 0; l < 4; l++) { float x = v[off + 1 + 4 * c + l]; acc[l] = acc[l] + x * x; }
            }
            var S = new float[4]; for (int l = 0; l < 4; l++) S[l] = A[l] + B[l];
            s = (S[0] + S[2]) + (S[1] + S[3]);
            for (int j = m + 1; j < n; j++) s = s + v[off + j] * v[off + j];
        }
        else for (int j = 1; j < n; j++) s = s + v[off + j] * v[off + j];
        return s;
    }

    /// <summary>Row-block kind of the compiled GEMV (8/4/2/1) for block row r among `rows` (stride heuristic never trips here).</summary>
    static int GemvKind(int rows, int r)
    {
        int i = 0, n8 = rows - 7;
        while (i < n8) { if (r < i + 8) return 8; i += 8; }
        i = (i + 7) & ~7;
        while (i < rows - 3) { if (r < i + 4) return 4; i += 4; }
        while (i < rows - 1) { if (r < i + 2) return 2; i += 2; }
        return 1;
    }

    /// <summary>Dot product `Σ_j lhs[j]·rhs[j]` (depth n) in the accumulation order of the given row-block kind (§1c).</summary>
    static float GemvDot(Func<int, float> lhs, Func<int, float> rhs, int n, int kind)
    {
        if (kind == 4)
        {
            if (n >= 4)
            {
                int m = n & ~3; var acc = new float[4];
                for (int c = 0; c < m / 4; c++) for (int l = 0; l < 4; l++) acc[l] = acc[l] + lhs(4 * c + l) * rhs(4 * c + l);
                float cc = (acc[0] + acc[2]) + (acc[1] + acc[3]);
                for (int j = m; j < n; j++) cc = cc + lhs(j) * rhs(j);
                return cc;
            }
            float s = 0f; for (int j = 0; j < n; j++) s = s + lhs(j) * rhs(j); return s;
        }
        if (n >= 8)
        {
            int m = n & ~7; var A = new float[4]; var B = new float[4];
            for (int c = 0; c < m / 4; c++) { var acc = (c & 1) == 0 ? A : B; for (int l = 0; l < 4; l++) acc[l] = acc[l] + lhs(4 * c + l) * rhs(4 * c + l); }
            var S = new float[4]; for (int l = 0; l < 4; l++) S[l] = A[l] + B[l];
            float cc = (S[0] + S[2]) + (S[1] + S[3]);
            for (int j = m; j < n; j++) cc = cc + lhs(j) * rhs(j);
            return cc;
        }
        float t = 0f; for (int j = 0; j < n; j++) t = t + lhs(j) * rhs(j); return t;
    }

    /// <summary>Eigen float `ColPivHouseholderQR::computeInPlace` on a column-major R×C matrix (§1a). Returns (qr, hCoeffs, perm, nonzeroPivots).</summary>
    public static (float[][] Qr, float[] H, int[] Perm, int Nz) ColPivQr(float[][] cols, int R)
    {
        int C = cols.Length, size = Math.Min(R, C);
        var qr = new float[C][]; for (int c = 0; c < C; c++) qr[c] = (float[])cols[c].Clone();
        var u = new float[C]; var d = new float[C]; var h = new float[size]; var trans = new int[size];
        for (int j = 0; j < C; j++) d[j] = u[j] = Sqrt(SqNorm(qr[j], 0, R));
        float maxNorm = u[0]; for (int j = 1; j < C; j++) if (u[j] > maxNorm) maxNorm = u[j];
        float thrHelper = ((maxNorm * FltEps) * (maxNorm * FltEps)) / (float)R;
        int nz = size;
        for (int k = 0; k < size; k++)
        {
            int b = k; float bn = u[k]; for (int j = k + 1; j < C; j++) if (u[j] > bn) { bn = u[j]; b = j; }
            float bsq = bn * bn;
            if (nz == size && bsq < (float)(R - k) * thrHelper) nz = k;
            trans[k] = b;
            if (b != k) { (qr[k], qr[b]) = (qr[b], qr[k]); (u[k], u[b]) = (u[b], u[k]); (d[k], d[b]) = (d[b], d[k]); }
            var col = qr[k]; float c0 = col[k], tau, beta; int n = R - k;
            if (n == 1) { tau = 0f; beta = c0; }
            else
            {
                float tailSq = SqNorm(col, k + 1, n - 1);
                if (tailSq <= FltMin) { tau = 0f; for (int r = k + 1; r < R; r++) col[r] = 0f; beta = c0; }
                else
                {
                    beta = Sqrt(c0 * c0 + tailSq);
                    if (!(c0 < 0f)) beta = -beta;
                    float inv = 1.0f / (c0 - beta);
                    for (int r = k + 1; r < R; r++) col[r] = col[r] * inv;
                    tau = (beta - c0) / beta;
                }
            }
            h[k] = tau; col[k] = beta;
            int blockRows = R - k, m = C - k - 1;
            if (m > 0)
            {
                if (blockRows == 1) { for (int j = 0; j < m; j++) qr[k + 1 + j][k] = (1.0f - tau) * qr[k + 1 + j][k]; }
                else if (tau != 0f)
                {
                    int depth = blockRows - 1; var tmp = new float[m];
                    for (int j = 0; j < m; j++)
                    {
                        var bc = qr[k + 1 + j]; int kk = k;
                        float cc = GemvDot(i => col[kk + 1 + i], i => bc[kk + 1 + i], depth, GemvKind(m, j));
                        tmp[j] = 0f + 1.0f * cc;
                    }
                    for (int j = 0; j < m; j++)
                    {
                        var bc = qr[k + 1 + j];
                        tmp[j] = tmp[j] + bc[k];
                        bc[k] = bc[k] - (tmp[j] * tau);
                        for (int i = 0; i < depth; i++) { float v = col[k + 1 + i] * tau; bc[k + 1 + i] = bc[k + 1 + i] - (v * tmp[j]); }
                    }
                }
            }
            for (int j = k + 1; j < C; j++)
            {
                if (u[j] == 0f) continue;
                float temp = MathF.Abs(qr[j][k]) / u[j];
                temp = (temp + 1.0f) * (1.0f - temp);
                temp = MathF.Max(temp, 0f);
                float q = u[j] / d[j]; float temp2 = (q * q) * temp;
                if (temp2 > SqrtEps) u[j] = Sqrt(temp) * u[j];
                else { d[j] = Sqrt(SqNorm(qr[j], k + 1, R - k - 1)); u[j] = d[j]; }
            }
        }
        var perm = new int[C]; for (int i = 0; i < C; i++) perm[i] = i;
        for (int k = 0; k < size; k++) (perm[k], perm[trans[k]]) = (perm[trans[k]], perm[k]);
        return (qr, h, perm, nz);
    }

    /// <summary>`_solve_impl` for a square system (§1d): apply Qᵀ, back-substitute, un-permute.</summary>
    public static float[] QrSolve((float[][] Qr, float[] H, int[] Perm, int Nz) f, float[] b, int R)
    {
        var (qr, h, perm, nz) = f; int C = qr.Length;
        var x = new float[C];
        if (nz == 0) return x;
        var c = (float[])b.Clone();
        for (int k = 0; k < nz; k++)
        {
            int rows = R - k; float tau = h[k];
            if (rows == 1) c[k] = (1.0f - tau) * c[k];
            else if (tau != 0f)
            {
                float dot = qr[k][k + 1] * c[k + 1];
                for (int i = 1; i < rows - 1; i++) dot = dot + qr[k][k + 1 + i] * c[k + 1 + i];
                float tmp = dot + c[k];
                c[k] = c[k] - tau * tmp;
                float s = tmp * tau;
                for (int i = 0; i < rows - 1; i++) c[k + 1 + i] = c[k + 1 + i] - qr[k][k + 1 + i] * s;
            }
        }
        for (int i = nz - 1; i >= 0; i--)
        {
            c[i] = c[i] / qr[i][i];
            for (int j = 0; j < i; j++) c[j] = c[j] - qr[i][j] * c[i];
        }
        for (int i = 0; i < nz; i++) x[perm[i]] = c[i];
        return x;
    }

    /// <summary>`FUN_1802f8ac0`: 4-point DLT, H = [x0..x7, 1].</summary>
    public static float[] FromFourPairs((float X, float Y)[] src, (float X, float Y)[] dst)
    {
        if (src.Length != 4 || dst.Length != 4) throw new ArgumentException("require 4 point pairs");
        var A = new float[8][]; for (int c = 0; c < 8; c++) A[c] = new float[8];
        var b = new float[8];
        for (int i = 0; i < 4; i++)
        {
            float sx = src[i].X, sy = src[i].Y, dx = dst[i].X, dy = dst[i].Y;
            A[0][2 * i] = sx; A[1][2 * i] = sy; A[2][2 * i] = 1.0f; A[6][2 * i] = -(sx * dx); A[7][2 * i] = -(sy * dx); b[2 * i] = dx;
            A[3][2 * i + 1] = sx; A[4][2 * i + 1] = sy; A[5][2 * i + 1] = 1.0f; A[6][2 * i + 1] = -(sx * dy); A[7][2 * i + 1] = -(sy * dy); b[2 * i + 1] = dy;
        }
        var x = QrSolve(ColPivQr(A, 8), b, 8);
        var H = new float[9]; Array.Copy(x, H, 8); H[8] = 1.0f;
        return H;
    }

    /// <summary>`FUN_1802f91b0`: least-squares homography for ≥ 5 pairs = last right-singular vector of the 2N×9 system (unit norm, not divided by H[8]).</summary>
    public static float[] LeastSquares((float X, float Y)[] src, (float X, float Y)[] dst)
    {
        int N = src.Length; if (dst.Length != N) throw new ArgumentException("size mistmatch in computehomog"); if (N < 5) throw new ArgumentException("need more than 4 points");
        int R = 2 * N; var A = new float[9][]; for (int c = 0; c < 9; c++) A[c] = new float[R];
        for (int i = 0; i < N; i++)
        {
            float sx = src[i].X, sy = src[i].Y, dx = dst[i].X, dy = dst[i].Y;
            A[0][2 * i] = -sx; A[1][2 * i] = -sy; A[2][2 * i] = -1.0f; A[6][2 * i] = dx * sx; A[7][2 * i] = dx * sy; A[8][2 * i] = dx;
            A[3][2 * i + 1] = -sx; A[4][2 * i + 1] = -sy; A[5][2 * i + 1] = -1.0f; A[6][2 * i + 1] = dy * sx; A[7][2 * i + 1] = dy * sy; A[8][2 * i + 1] = dy;
        }
        var (V, sv) = JacobiSvdV(A, R);
        if (!(0.0f < sv[8])) throw new InvalidOperationException("smallest singular value must be positive");
        var H = new float[9]; for (int i = 0; i < 9; i++) H[i] = V[8][i];
        return H;
    }

    /// <summary>Eigen float `JacobiSVD` (ColPivHouseholderQR preconditioner, rows &gt; cols = 9): returns V (column-major, V[c][r]) and the sorted singular values.</summary>
    public static (float[][] V, float[] Sv) JacobiSvdV(float[][] A, int R)
    {
        int C = A.Length;
        float scale = 0f; for (int c = 0; c < C; c++) for (int r = 0; r < R; r++) { float v = MathF.Abs(A[c][r]); if (v > scale) scale = v; }
        if (scale == 0f) scale = 1f;
        float inv = 1.0f / scale;
        var S = new float[C][]; for (int c = 0; c < C; c++) { S[c] = new float[R]; for (int r = 0; r < R; r++) S[c][r] = A[c][r] * inv; }
        var (qr, h, perm, nz) = ColPivQr(S, R);
        var W = new float[C][]; for (int c = 0; c < C; c++) { W[c] = new float[C]; for (int r = 0; r <= c; r++) W[c][r] = qr[c][r]; }
        var V = new float[C][]; for (int c = 0; c < C; c++) { V[c] = new float[C]; V[c][perm[c]] = 1.0f; }
        float maxDiag = 0f; for (int i = 0; i < C; i++) maxDiag = MathF.Max(maxDiag, MathF.Abs(W[i][i]));
        bool finished;
        do
        {
            finished = true;
            for (int p = 1; p < C; p++)
                for (int q = 0; q < p; q++)
                {
                    float thr = MathF.Max(Precision * maxDiag, ConsiderAsZero);
                    if (MathF.Abs(W[q][p]) > thr || MathF.Abs(W[p][q]) > thr)
                    {
                        finished = false;
                        var (jl, jr) = Real2x2(W[p][p], W[q][p], W[p][q], W[q][q]);
                        if (!(jl.C == 1f && jl.S == 0f))
                            for (int j = 0; j < C; j++) { float xp = W[j][p], xq = W[j][q]; W[j][p] = jl.C * xp + jl.S * xq; W[j][q] = jl.C * xq - jl.S * xp; }
                        if (!(jr.C == 1f && jr.S == 0f))
                        {
                            for (int i = 0; i < C; i++) { float xp = W[p][i], xq = W[q][i]; W[p][i] = (xq * -jr.S) + (xp * jr.C); W[q][i] = (xq * jr.C) - (xp * -jr.S); }
                            for (int i = 0; i < C; i++) { float xp = V[p][i], xq = V[q][i]; V[p][i] = (xq * -jr.S) + (xp * jr.C); V[q][i] = (xq * jr.C) - (xp * -jr.S); }
                        }
                        maxDiag = MathF.Max(maxDiag, MathF.Max(MathF.Abs(W[p][p]), MathF.Abs(W[q][q])));
                    }
                }
        } while (!finished);
        var sv = new float[C]; for (int i = 0; i < C; i++) sv[i] = MathF.Abs(W[i][i]) * scale;
        for (int i = 0; i < C; i++)
        {
            int pos = 0; float mx = sv[i]; for (int j = i + 1; j < C; j++) if (sv[j] > mx) { mx = sv[j]; pos = j - i; }
            if (mx == 0f) break;
            if (pos != 0) { (sv[i], sv[i + pos]) = (sv[i + pos], sv[i]); (V[i], V[i + pos]) = (V[i + pos], V[i]); }
        }
        return (V, sv);
    }

    /// <summary>`real_2x2_jacobi_svd` + `makeJacobi` in machine order (§2b). m01 = W(p,q) (row p, col q), m10 = W(q,p).</summary>
    static ((float C, float S) Left, (float C, float S) Right) Real2x2(float m00, float m01, float m10, float m11)
    {
        float d = m10 - m01, c1, s1;
        if (MathF.Abs(d) < FltMin) { c1 = 1f; s1 = 0f; }
        else { float t = m00 + m11, u = t / d, tmp = Sqrt(u * u + 1.0f), r = Rcp(tmp); c1 = u * r; s1 = 1.0f * r; }
        float x, y, z;
        if (!(c1 == 1f && s1 == 0f)) { x = c1 * m00 + s1 * m10; y = s1 * m11 + c1 * m01; z = m11 * c1 - m01 * s1; }
        else { x = m00; y = m01; z = m11; }
        float deno = MathF.Abs(y) + MathF.Abs(y), cr, sr;
        if (deno < FltMin) { cr = 1f; sr = 0f; }
        else
        {
            float tau = (x - z) / deno, w = Sqrt(tau * tau + 1.0f);
            float t = 1.0f / (tau + (tau > 0f ? w : -w));
            float n = Rsqrt(t * t + 1.0f);
            sr = ((MathF.Abs(t) * (y / MathF.Abs(y))) * (t > 0f ? -1.0f : 1.0f)) * n; cr = n;
        }
        return ((s1 * sr + cr * c1, cr * s1 - c1 * sr), (cr, sr));
    }

    /// <summary>`FUN_1802f9930`: max over i &lt; 7 of |Hn[i]·rcp(Hn[8]) − Ho[i]·rcp(Ho[8])| (H[7] is never compared).</summary>
    public static float Change(float[] Hn, float[] Ho)
    {
        float r0 = Rcp(Hn[8]) * 1.0f, r1 = Rcp(Ho[8]) * 1.0f, m = 0f;
        for (int i = 0; i < 7; i++) m = MathF.Max(MathF.Abs(Hn[i] * r0 - Ho[i] * r1), m);
        return m;
    }

    /// <summary>`FUN_1802ffcf0`: the mapped unit square must stay convex with each edge within 20° of its axis.</summary>
    public static bool IsValid(float[] H)
    {
        float a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], hh = H[7], i = H[8];
        float u = (a + b) * 0.5f, v = (d + e) * 0.5f, wh = (g + hh) * 0.5f;
        float r1 = Rcp(i - wh), r2 = Rcp(((g - hh) * 0.5f) + i), r3 = Rcp(wh + i), r4 = Rcp(((hh - g) * 0.5f) + i);
        var P1 = ((c - u) * r1, (f - v) * r1); var P2 = ((((a - b) * 0.5f) + c) * r2, (((d - e) * 0.5f) + f) * r2);
        var P3 = ((u + c) * r3, (v + f) * r3); var P4 = ((((b - a) * 0.5f) + c) * r4, (((e - d) * 0.5f) + f) * r4);
        var E1 = (P2.Item1 - P1.Item1, P2.Item2 - P1.Item2); var E2 = (P3.Item1 - P2.Item1, P3.Item2 - P2.Item2);
        var E3 = (P4.Item1 - P3.Item1, P4.Item2 - P3.Item2); var E4 = (P1.Item1 - P4.Item1, P1.Item2 - P4.Item2);
        float c12 = E1.Item1 * E2.Item2 - E1.Item2 * E2.Item1, c23 = E2.Item1 * E3.Item2 - E2.Item2 * E3.Item1;
        float c34 = E3.Item1 * E4.Item2 - E4.Item1 * E3.Item2, c41 = E4.Item1 * E1.Item2 - E4.Item2 * E1.Item1;
        if ((c23 > 0f) != (c12 > 0f)) return false; if ((c12 > 0f) != (c34 > 0f)) return false; if ((c41 > 0f) != (c12 > 0f)) return false;
        double Deg((float X, float Y) E, float comp, float sign)
        {
            float len2 = E.X * E.X + E.Y * E.Y;
            float q = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(len2)).ToScalar();
            float D = ((len2 * q) * q) + (-3.0f);
            float cos = ((q * sign) * D) * comp;
            return (double)MathF.Acos(cos) * 57.295780490442965;
        }
        double d1 = Deg(E1, E1.Item1, -0.5f), d2 = Deg(E2, E2.Item2, -0.5f), d3 = Deg(E3, E3.Item1, 0.5f), d4 = Deg(E4, E4.Item2, 0.5f);
        return d4 < 20.0 && d3 < 20.0 && d2 < 20.0 && d1 < 20.0;
    }
}
