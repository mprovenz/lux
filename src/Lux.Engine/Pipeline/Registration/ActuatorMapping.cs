namespace Lux.Engine.Pipeline.Registration;

/// <summary>`MirrorActuatorMapping&lt;double&gt;` (180213470/180213730, `FUN_18024daf0`): hall code → mirror angle through the
/// quadratic model `a(x)·r² + b(x)·r + c(x) = 0` with `a = c0·x + c3`, `b = c1·x + c4`, `c = c2·x + c5`, `x = (hall − m_in)/s_in`,
/// root chosen by the segment flags, output `r·s_out + m_out` (type 0) or `atan(r)·360/π` (type 1).</summary>
public sealed class ActuatorMapping
{
    public double[] Coeffs = new double[6]; public bool UseRplusLeft, UseRplusRight; public double Inflection;
    public double MIn, SIn, MOut, SOut; public int XformType;

    public double Angle(double hall)
    {
        double x = (hall - MIn) / SIn;
        double a = Coeffs[0] * x + Coeffs[3], b = Coeffs[1] * x + Coeffs[4], c = Coeffs[2] * x + Coeffs[5];
        double disc = (-4.0 * a) * c + b * b;
        if (Math.Abs(disc) < 1e-5) disc = 0.0;
        double r0, r1;
        if (disc < 0.0) { r0 = r1 = b / (a * (-2.0)); }
        else { double s = Math.Sqrt(disc); r0 = (s - b) / (a + a); r1 = (-b - s) / (a + a); }
        bool flag = hall < Inflection ? UseRplusLeft : UseRplusRight;
        double r = (flag ? 1 : 0) == 1 ? r0 : r1;   // idx = flag ^ 1 → root[1] when the flag is clear
        return XformType == 1 ? Math.Atan(r) * (360.0 / Math.PI) : r * SOut + MOut;
    }
}
