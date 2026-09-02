namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// Eigen 3.3 `ColPivHouseholderQR&lt;MatrixXd&gt;` — the decomposition cp.dll uses for the thin-plate-spline system of
/// `OptimizeHSVLut` (`FUN_180164940` allocates the object, `FUN_180165350` = `computeInPlace`, `FUN_180168190`/`FUN_180168240`
/// = `_solve_impl`). The object layout in `FUN_180164940` identifies the Eigen 3.3 flavour unambiguously: m_qr (ptr, rows, cols),
/// m_hCoeffs (min(rows,cols)), m_colsPermutation (**int32**, cols), m_colsTranspositions (int64, cols), m_temp, m_colNormsUpdated
/// and m_colNormsDirect (three double vectors of size cols) — the last two only exist in 3.3 (3.2 has a single m_colSqNorms).
///
/// The machine-level deviations of the same compiler on the fixed-size twin in this binary
/// (`ColPivHouseholderQR&lt;Matrix&lt;double,9,8&gt;&gt;::computeInPlace` at 0x1802a33d0, spec `a5552553660ca75d1.md`)
/// are reproduced here: `essential = tail·(1/(c0−beta))` as a reciprocal multiply, `temp2 = (u/d)·(u/d)·temp`,
/// `temp&lt;0 → 0` as a max, `beta` sign from `c0 &lt; 0`, strict `&gt;` for both the pivot search (first maximum wins) and
/// `m_maxpivot`, `tol` test `tailSqNorm &lt;= DBL_MIN`, and the `&lt;=` downdate test. What is *not* reproduced is the
/// compiler's SIMD re-association of the inner dot products (the gemv lane trees): those loops are written sequentially
/// here, which changes nothing at float precision (the grid is float and every one of the 2×3072 exported values matches).
/// </summary>
public sealed class EigenColPivQr
{
    const double Eps = 2.220446049250313e-16;          // NumTraits<double>::epsilon()
    const double DblMin = 2.2250738585072014e-308;     // (std::numeric_limits<double>::min)()

    readonly int _rows, _cols, _size;
    readonly double[] _qr;          // column-major copy of the input, overwritten in place
    readonly double[] _h;           // m_hCoeffs
    readonly int[] _perm;           // m_colsPermutation indices
    public int NonzeroPivots { get; }
    public double MaxPivot { get; }
    public int DetPq { get; }

    /// <summary>`computeInPlace()` on a square/rectangular column-major matrix (`a[i + rows·j]`).</summary>
    public EigenColPivQr(double[] a, int rows, int cols)
    {
        _rows = rows; _cols = cols; _size = Math.Min(rows, cols);
        _qr = (double[])a.Clone();
        _h = new double[_size]; _perm = new int[cols];
        var trans = new int[_size];
        var temp = new double[cols];
        var normsUpd = new double[cols];
        var normsDir = new double[cols];

        for (int k = 0; k < cols; k++) { normsDir[k] = ColNorm(k, 0); normsUpd[k] = normsDir[k]; }

        double mx = normsUpd[0];                                            // maxCoeff (visitor: first maximum wins)
        for (int k = 1; k < cols; k++) if (normsUpd[k] > mx) mx = normsUpd[k];
        double te = mx * Eps;
        double thresholdHelper = (te * te) / rows;                          // abs2(maxCoeff·eps)/rows
        double downdateThreshold = Math.Sqrt(Eps);                          // 2^-26

        int nonzero = _size, ntrans = 0; double maxpivot = 0.0;
        for (int k = 0; k < _size; k++)
        {
            int biggest = k; double bestNorm = normsUpd[k];                 // maxCoeff of the tail, strict >
            for (int j = k + 1; j < cols; j++) if (normsUpd[j] > bestNorm) { bestNorm = normsUpd[j]; biggest = j; }
            double bestSq = bestNorm * bestNorm;
            if (nonzero == _size && bestSq < thresholdHelper * (rows - k)) nonzero = k;

            trans[k] = biggest;
            if (k != biggest)
            {
                for (int i = 0; i < rows; i++) (_qr[i + rows * k], _qr[i + rows * biggest]) = (_qr[i + rows * biggest], _qr[i + rows * k]);
                (normsUpd[k], normsUpd[biggest]) = (normsUpd[biggest], normsUpd[k]);
                (normsDir[k], normsDir[biggest]) = (normsDir[biggest], normsDir[k]);
                ntrans++;
            }

            // makeHouseholderInPlace on qr.col(k).tail(rows−k)
            int tail = rows - k - 1;
            double tailSq = tail <= 0 ? 0.0 : SquaredNorm(k, k + 1);
            double c0 = _qr[k + rows * k], beta, tau;
            if (tailSq <= DblMin)
            {
                tau = 0.0; beta = c0;
                for (int i = 0; i < tail; i++) _qr[k + 1 + i + rows * k] = 0.0;
            }
            else
            {
                beta = Math.Sqrt(c0 * c0 + tailSq);
                if (c0 >= 0.0) beta = -beta;
                double r = 1.0 / (c0 - beta);                               // reciprocal multiply (0x1802a3945)
                for (int i = 0; i < tail; i++) _qr[k + 1 + i + rows * k] *= r;
                tau = (beta - c0) / beta;
            }
            _h[k] = tau;
            _qr[k + rows * k] = beta;
            if (Math.Abs(beta) > maxpivot) maxpivot = Math.Abs(beta);

            // applyHouseholderOnTheLeft on the bottom-right corner (rows−k)×(cols−k−1)
            if (tau != 0.0 && cols - k - 1 > 0)
            {
                for (int j = k + 1; j < cols; j++)                          // tmp = essentialᵀ·bottom
                {
                    double t = 0.0;
                    for (int i = 0; i < tail; i++) t += _qr[k + 1 + i + rows * k] * _qr[k + 1 + i + rows * j];
                    temp[j] = t;
                }
                for (int j = k + 1; j < cols; j++) temp[j] += _qr[k + rows * j];            // tmp += row0
                for (int j = k + 1; j < cols; j++) _qr[k + rows * j] -= tau * temp[j];      // row0 −= tau·tmp
                for (int j = k + 1; j < cols; j++)                                          // bottom −= (tau·essential)·tmp
                    for (int i = 0; i < tail; i++)
                        _qr[k + 1 + i + rows * j] -= (_qr[k + 1 + i + rows * k] * tau) * temp[j];
            }

            for (int j = k + 1; j < cols; j++)                              // LAPACK norm downdate (lawn176)
            {
                if (normsUpd[j] == 0.0) continue;
                double t = Math.Abs(_qr[k + rows * j]) / normsUpd[j];
                t = (1.0 + t) * (1.0 - t);
                t = Math.Max(t, 0.0);
                double q = normsUpd[j] / normsDir[j];
                double t2 = (q * q) * t;
                if (t2 <= downdateThreshold) { normsDir[j] = ColNorm(j, k + 1); normsUpd[j] = normsDir[j]; }
                else normsUpd[j] = Math.Sqrt(t) * normsUpd[j];
            }
        }

        for (int i = 0; i < cols; i++) _perm[i] = i;
        for (int k = 0; k < _size; k++) (_perm[k], _perm[trans[k]]) = (_perm[trans[k]], _perm[k]);
        NonzeroPivots = nonzero; MaxPivot = maxpivot; DetPq = (ntrans % 2) != 0 ? -1 : 1;
    }

    double SquaredNorm(int col, int firstRow)
    {
        int n = _rows - firstRow;
        int off = firstRow + _rows * col;
        return EigenRedux.Sum(n, i => _qr[off + i] * _qr[off + i]);
    }
    double ColNorm(int col, int firstRow) => Math.Sqrt(SquaredNorm(col, firstRow));

    /// <summary>`_solve_impl` for a single right-hand side: `c = Qᵀ·rhs` (the Householder sequence applied in order
    /// 0…nonzeroPivots−1), back-substitution on the leading upper-triangular block (Eigen's column-oriented
    /// `triangular_solve_vector`, panel width 8), then the column permutation.</summary>
    public double[] Solve(double[] rhs)
    {
        var dst = new double[_cols];
        int nz = NonzeroPivots;
        if (nz == 0) return dst;
        var c = (double[])rhs.Clone();
        for (int k = 0; k < nz; k++)
        {
            double tau = _h[k];
            if (tau == 0.0) continue;
            int tail = _rows - k - 1;
            double t = 0.0;
            for (int i = 0; i < tail; i++) t += _qr[k + 1 + i + _rows * k] * c[k + 1 + i];
            t += c[k];
            c[k] -= tau * t;
            for (int i = 0; i < tail; i++) c[k + 1 + i] -= (_qr[k + 1 + i + _rows * k] * tau) * t;
        }
        const int panel = 8;                                                // EIGEN_TUNE_TRIANGULAR_PANEL_WIDTH
        for (int pi = nz; pi > 0; pi -= panel)
        {
            int apw = Math.Min(pi, panel), startBlock = pi - apw;
            for (int k = 0; k < apw; k++)
            {
                int i = pi - k - 1;
                c[i] /= _qr[i + _rows * i];
                int r = apw - k - 1;
                for (int q = 0; q < r; q++) c[i - r + q] -= c[i] * _qr[i - r + q + _rows * i];
            }
            for (int j = startBlock; j < pi; j++)                           // gemv with alpha = −1 on the block above
                for (int i = 0; i < startBlock; i++) c[i] -= _qr[i + _rows * j] * c[j];
        }
        for (int i = 0; i < nz; i++) dst[_perm[i]] = c[i];
        for (int i = nz; i < _cols; i++) dst[_perm[i]] = 0.0;
        return dst;
    }
}
