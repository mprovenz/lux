using Ltpb;

namespace Lux.Engine.Imaging;

/// <summary>Minimal 3×3 (row-major double[9]) and vec3 (double[3]) linear algebra for module geometry.</summary>
public static class Mat3
{
    public static readonly double[] I = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    public static double[] FromM3(Matrix3x3F m) =>
        new double[] { m.X00, m.X01, m.X02, m.X10, m.X11, m.X12, m.X20, m.X21, m.X22 };

    public static double[] FromP3(Point3F p) => new double[] { p.X, p.Y, p.Z };

    public static double[] MatVec(double[] a, double[] v) => new[]
    {
        a[0] * v[0] + a[1] * v[1] + a[2] * v[2],
        a[3] * v[0] + a[4] * v[1] + a[5] * v[2],
        a[6] * v[0] + a[7] * v[1] + a[8] * v[2],
    };

    public static double[] MatMul(double[] a, double[] b)
    {
        var m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    public static double[] Transpose(double[] a) =>
        new[] { a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8] };

    public static double[] Inverse(double[] a)
    {
        double det = a[0] * (a[4] * a[8] - a[5] * a[7])
                   - a[1] * (a[3] * a[8] - a[5] * a[6])
                   + a[2] * (a[3] * a[7] - a[4] * a[6]);
        double id = 1.0 / det;
        return new[]
        {
            (a[4] * a[8] - a[5] * a[7]) * id, (a[2] * a[7] - a[1] * a[8]) * id, (a[1] * a[5] - a[2] * a[4]) * id,
            (a[5] * a[6] - a[3] * a[8]) * id, (a[0] * a[8] - a[2] * a[6]) * id, (a[2] * a[3] - a[0] * a[5]) * id,
            (a[3] * a[7] - a[4] * a[6]) * id, (a[1] * a[6] - a[0] * a[7]) * id, (a[0] * a[4] - a[1] * a[3]) * id,
        };
    }

    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public static double[] Normalize(double[] v)
    {
        double n = Math.Sqrt(Dot(v, v));
        return new[] { v[0] / n, v[1] / n, v[2] / n };
    }

    /// <summary>Rodrigues rotation about a unit axis by <paramref name="deg"/> degrees.</summary>
    public static double[] Rodrigues(double[] axis, double deg)
    {
        double a = deg * Math.PI / 180.0, s = Math.Sin(a), c = Math.Cos(a);
        double[] K = { 0, -axis[2], axis[1], axis[2], 0, -axis[0], -axis[1], axis[0], 0 };
        double[] K2 = MatMul(K, K);
        var r = new double[9];
        for (int i = 0; i < 9; i++) r[i] = I[i] + s * K[i] + (1 - c) * K2[i];
        return r;
    }

    /// <summary>Householder reflection I − 2 n nᵀ for a unit normal n.</summary>
    public static double[] Reflection(double[] n) => new[]
    {
        1 - 2 * n[0] * n[0], -2 * n[0] * n[1], -2 * n[0] * n[2],
        -2 * n[1] * n[0], 1 - 2 * n[1] * n[1], -2 * n[1] * n[2],
        -2 * n[2] * n[0], -2 * n[2] * n[1], 1 - 2 * n[2] * n[2],
    };

    /// <summary>np.interp: piecewise-linear interpolation with endpoint clamping. xs must be ascending.</summary>
    public static double Interp(double x, double[] xs, double[] ys)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (int i = 1; i < xs.Length; i++)
            if (x < xs[i])
            {
                double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        return ys[^1];
    }
}
