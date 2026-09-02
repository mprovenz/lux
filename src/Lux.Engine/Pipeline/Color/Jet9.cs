namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// Forward-mode dual number with 9 partial derivatives — the equivalent of Ceres' <c>Jet&lt;double, 9&gt;</c> that
/// Lumen's <c>AutoDiffCostFunction&lt;LabCostFunction, 25, 9&gt;</c> evaluates the colour-matrix fit with (SoT §9.3).
/// Derivative rules follow ceres/jet.h.
/// </summary>
public readonly struct Jet9
{
    public readonly double V;
    public readonly double D0, D1, D2, D3, D4, D5, D6, D7, D8;

    public Jet9(double v) { V = v; D0 = D1 = D2 = D3 = D4 = D5 = D6 = D7 = D8 = 0; }
    public Jet9(double v, double d0, double d1, double d2, double d3, double d4, double d5, double d6, double d7, double d8)
    { V = v; D0 = d0; D1 = d1; D2 = d2; D3 = d3; D4 = d4; D5 = d5; D6 = d6; D7 = d7; D8 = d8; }

    /// <summary>Parameter jet: value v with ∂/∂p_k = 1.</summary>
    public static Jet9 Param(double v, int k) => new(v,
        k == 0 ? 1 : 0, k == 1 ? 1 : 0, k == 2 ? 1 : 0, k == 3 ? 1 : 0, k == 4 ? 1 : 0, k == 5 ? 1 : 0, k == 6 ? 1 : 0, k == 7 ? 1 : 0, k == 8 ? 1 : 0);

    public double this[int k] => k switch { 0 => D0, 1 => D1, 2 => D2, 3 => D3, 4 => D4, 5 => D5, 6 => D6, 7 => D7, _ => D8 };

    private static Jet9 Lin(double v, in Jet9 a, double sa) =>
        new(v, a.D0 * sa, a.D1 * sa, a.D2 * sa, a.D3 * sa, a.D4 * sa, a.D5 * sa, a.D6 * sa, a.D7 * sa, a.D8 * sa);
    private static Jet9 Lin2(double v, in Jet9 a, double sa, in Jet9 b, double sb) => new(v,
        a.D0 * sa + b.D0 * sb, a.D1 * sa + b.D1 * sb, a.D2 * sa + b.D2 * sb, a.D3 * sa + b.D3 * sb, a.D4 * sa + b.D4 * sb,
        a.D5 * sa + b.D5 * sb, a.D6 * sa + b.D6 * sb, a.D7 * sa + b.D7 * sb, a.D8 * sa + b.D8 * sb);

    public static implicit operator Jet9(double v) => new(v);

    public static Jet9 operator +(in Jet9 a, in Jet9 b) => Lin2(a.V + b.V, a, 1, b, 1);
    public static Jet9 operator -(in Jet9 a, in Jet9 b) => Lin2(a.V - b.V, a, 1, b, -1);
    public static Jet9 operator -(in Jet9 a) => Lin(-a.V, a, -1);
    /// <summary>ceres: `Jet(f.a·g.a, f.a·g.v + f.v·g.a)`.</summary>
    public static Jet9 operator *(in Jet9 f, in Jet9 g) => new(f.V * g.V,
        f.V * g.D0 + f.D0 * g.V, f.V * g.D1 + f.D1 * g.V, f.V * g.D2 + f.D2 * g.V, f.V * g.D3 + f.D3 * g.V, f.V * g.D4 + f.D4 * g.V,
        f.V * g.D5 + f.D5 * g.V, f.V * g.D6 + f.D6 * g.V, f.V * g.D7 + f.D7 * g.V, f.V * g.D8 + f.D8 * g.V);
    /// <summary>ceres jet.h: `g_a_inverse = 1/g.a; f_a_by_g_a = f.a·g_a_inverse; Jet(f.a·g_a_inverse, (f.v − f_a_by_g_a·g.v)·g_a_inverse)`.</summary>
    public static Jet9 operator /(in Jet9 f, in Jet9 g)
    {
        double gInv = 1.0 / g.V, fByG = f.V * gInv;
        return new(f.V * gInv,
            (f.D0 - fByG * g.D0) * gInv, (f.D1 - fByG * g.D1) * gInv, (f.D2 - fByG * g.D2) * gInv, (f.D3 - fByG * g.D3) * gInv, (f.D4 - fByG * g.D4) * gInv,
            (f.D5 - fByG * g.D5) * gInv, (f.D6 - fByG * g.D6) * gInv, (f.D7 - fByG * g.D7) * gInv, (f.D8 - fByG * g.D8) * gInv);
    }
    public static Jet9 operator +(in Jet9 a, double s) => new(a.V + s, a.D0, a.D1, a.D2, a.D3, a.D4, a.D5, a.D6, a.D7, a.D8);
    public static Jet9 operator +(double s, in Jet9 a) => a + s;
    public static Jet9 operator -(in Jet9 a, double s) => a + (-s);
    public static Jet9 operator -(double s, in Jet9 a) => Lin(s - a.V, a, -1);
    public static Jet9 operator *(in Jet9 a, double s) => Lin(a.V * s, a, s);
    public static Jet9 operator *(double s, in Jet9 a) => Lin(a.V * s, a, s);
    /// <summary>ceres: `s_inverse = 1/s; Jet(f.a·s_inverse, f.v·s_inverse)`.</summary>
    public static Jet9 operator /(in Jet9 f, double s) { double sInv = 1.0 / s; return Lin(f.V * sInv, f, sInv); }
    /// <summary>ceres: `minus_s_g_a_inverse2 = −s/(g.a·g.a); Jet(s/g.a, g.v·minus_s_g_a_inverse2)`.</summary>
    public static Jet9 operator /(double s, in Jet9 g) => Lin(s / g.V, g, -s / (g.V * g.V));

    public static bool operator <(in Jet9 a, double s) => a.V < s;
    public static bool operator >(in Jet9 a, double s) => a.V > s;
    public static bool operator <=(in Jet9 a, double s) => a.V <= s;
    public static bool operator >=(in Jet9 a, double s) => a.V >= s;
    public static bool operator <(in Jet9 a, in Jet9 b) => a.V < b.V;
    public static bool operator >(in Jet9 a, in Jet9 b) => a.V > b.V;
    public static bool operator <=(in Jet9 a, in Jet9 b) => a.V <= b.V;
    public static bool operator >=(in Jet9 a, in Jet9 b) => a.V >= b.V;

    /// <summary>ceres: `tmp = sqrt(f.a); two_a_inverse = 1/(2·tmp); Jet(tmp, f.v·two_a_inverse)`.</summary>
    public static Jet9 Sqrt(in Jet9 a) { double v = Math.Sqrt(a.V); return Lin(v, a, 1.0 / (2.0 * v)); }
    public static Jet9 Cbrt(in Jet9 a) { double v = Math.Cbrt(a.V); return Lin(v, a, v == 0 ? 0 : 1.0 / (3.0 * v * v)); }
    public static Jet9 Exp(in Jet9 a) { double v = Math.Exp(a.V); return Lin(v, a, v); }
    public static Jet9 Sin(in Jet9 a) => Lin(Math.Sin(a.V), a, Math.Cos(a.V));
    public static Jet9 Cos(in Jet9 a) => Lin(Math.Cos(a.V), a, -Math.Sin(a.V));
    public static Jet9 Abs(in Jet9 a) => a.V < 0 ? -a : a;
    /// <summary>pow(a, e) for a constant exponent (ceres: e·a^(e−1)·a').</summary>
    public static Jet9 Pow(in Jet9 a, double e) => Lin(Math.Pow(a.V, e), a, e * Math.Pow(a.V, e - 1));
    /// <summary>atan2(y, x): d = (x·y' − y·x') / (x² + y²).</summary>
    public static Jet9 Atan2(in Jet9 g, in Jet9 f)
    {
        // ceres: tmp = 1/(f.a² + g.a²); Jet(atan2(g.a, f.a), tmp·(−g.a·f.v + f.a·g.v))
        double tmp = 1.0 / (f.V * f.V + g.V * g.V);
        return new(Math.Atan2(g.V, f.V),
            tmp * (-g.V * f.D0 + f.V * g.D0), tmp * (-g.V * f.D1 + f.V * g.D1), tmp * (-g.V * f.D2 + f.V * g.D2), tmp * (-g.V * f.D3 + f.V * g.D3), tmp * (-g.V * f.D4 + f.V * g.D4),
            tmp * (-g.V * f.D5 + f.V * g.D5), tmp * (-g.V * f.D6 + f.V * g.D6), tmp * (-g.V * f.D7 + f.V * g.D7), tmp * (-g.V * f.D8 + f.V * g.D8));
    }
    public static Jet9 Hypot(in Jet9 a, in Jet9 b) => Sqrt(a * a + b * b);
}
