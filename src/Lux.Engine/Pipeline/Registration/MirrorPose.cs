namespace Lux.Engine.Pipeline.Registration;

/// <summary>`MirrorSystem` doubles as Lumen holds them (`FUN_1802516b0`): `R0` = real camera orientation (row-major), `Cv` =
/// real camera location, `P0` = point on the rotation axis, `Axis`, `N0` = mirror normal at 0°, `D` = plane distance,
/// `Flip` = flip_img_around_x.</summary>
public sealed class MirrorSystem
{
    public double[] R0 = new double[9], Cv = new double[3], P0 = new double[3], Axis = new double[3], N0 = new double[3];
    public double D; public bool Flip;
}

/// <summary>
/// Movable-mirror pose math of cp.dll, op-exact (spec A.4 §2): `FUN_180226770` (Rodrigues about the normalised axis,
/// reflection `I − 2nnᵀ`, `P = H·R0`, handedness column flip, reflected centre), `FUN_180226d90` (transpose + `t = −R·C'`),
/// `FUN_18020e7e0` (`Rz(δ)`), `FUN_18029bac0` (node pose = mirror pose ∘ Rz), `FUN_1803086a0` (principal-point shift).
/// </summary>
public static class MirrorPose
{
    const double DegToRad = 0.017453292519943295;   // DAT_1806b6c50 (π/180)

    /// <summary>`FUN_180226770`: (C', P') — reflected camera centre and camera→world rotation for mirror angle θ (degrees).</summary>
    public static (double[] Centre, double[] Rcw) Reflect(MirrorSystem s, double thetaDeg)
    {
        double ax = s.Axis[0], ay = s.Axis[1], az = s.Axis[2];
        double norm = Math.Sqrt((az * az + ax * ax) + ay * ay);
        ax /= norm; ay /= norm; az /= norm;
        double phi = thetaDeg * DegToRad, c = Math.Cos(phi), sn = Math.Sin(phi), oc = 1.0 - c;
        double r00 = c + ax * ax * oc, r10 = ax * ay * oc + az * sn, r01 = ax * ay * oc - az * sn, r11 = ay * ay * oc + c;
        double r02 = ax * az * oc + ay * sn, r12 = ay * az * oc - ax * sn, r20 = ax * az * oc - ay * sn, r21 = ay * az * oc + ax * sn, r22 = az * az * oc + c;
        double n0x = s.N0[0], n0y = s.N0[1], n0z = s.N0[2];
        double nx = (n0z * r02 + n0y * r01) + n0x * r00, ny = (n0z * r12 + n0y * r11) + n0x * r10, nz = (n0z * r22 + n0y * r21) + n0x * r20;
        double[] n = { nx, ny, nz };
        var H = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double m = n[i] * n[j];
                H[3 * i + j] = i == j ? 1.0 - (m + m) : m * (-2.0);
            }
        var P = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                P[3 * i + j] = (s.R0[j] * H[3 * i] + s.R0[3 + j] * H[3 * i + 1]) + s.R0[6 + j] * H[3 * i + 2];
        int col = s.Flip ? 1 : 0;
        P[col] = -P[col]; P[3 + col] = -P[3 + col]; P[6 + col] = -P[6 + col];
        double dx = (s.P0[0] + s.D * nx) - s.Cv[0], dy = (s.P0[1] + s.D * ny) - s.Cv[1], dz = (s.P0[2] + s.D * nz) - s.Cv[2];
        double dot = (dx * nx + dy * ny) + dz * nz, ss = dot + dot;
        double[] C = { ss * nx + s.Cv[0], ss * ny + s.Cv[1], ss * nz + s.Cv[2] };
        return (C, P);
    }

    /// <summary>`FUN_180226d90`: world→camera (R, t) for mirror angle θ: `R = P'ᵀ`, `t = −(R·C')`.</summary>
    public static (double[] R, double[] T) WorldToCamera(MirrorSystem s, double thetaDeg)
    {
        var (C, P) = Reflect(s, thetaDeg);
        var R = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) R[3 * i + j] = P[3 * j + i];
        // FUN_180226d90 transposes P in place and then reads the x-coefficient of the y row AFTER `R[1] = P[3]` was stored:
        // t_y uses R[1] (= R(0,1)) instead of R(1,0). Verified: reproduces Lumen's calibration slots bit-exactly (a −0.48 mm
        // t_y difference on B2 otherwise). t_x and t_z use the correct row entries.
        var t = new double[3];
        t[0] = -((C[2] * R[2] + C[1] * R[1]) + R[0] * C[0]);
        t[1] = -((C[2] * R[5] + C[1] * R[4]) + R[1] * C[0]);
        t[2] = -((C[2] * R[8] + R[7] * C[1]) + R[6] * C[0]);
        return (R, t);
    }

    /// <summary>`FUN_18020e7e0`: rotation about the optical axis by δ radians (row-major).</summary>
    public static double[] Rz(double delta) { double c = Math.Cos(delta), s = Math.Sin(delta); return new[] { c, -s, 0.0, s, c, 0.0, 0.0, 0.0, 1.0 }; }

    /// <summary>`FUN_18029bac0`: the candidate camera = base calibration with (R, t) from the mirror pose at θ, composed with Rz(δ) when δ ≠ 0.</summary>
    public static CalibData NodePose(MirrorSystem s, CalibData baseCam, double thetaDeg, double delta)
    {
        var (R, t) = WorldToCamera(s, thetaDeg);
        if (delta != 0.0)
        {
            double Cx = ((-t[0] * R[0]) - t[1] * R[3]) - t[2] * R[6];
            double Cy = ((-t[0] * R[1]) - t[1] * R[4]) - t[2] * R[7];
            double Cz = ((-t[0] * R[2]) - t[1] * R[5]) - t[2] * R[8];
            var z = Rz(delta);
            var Rp = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Rp[3 * i + j] = (z[3 * i + 2] * R[6 + j] + z[3 * i + 1] * R[3 + j]) + z[3 * i] * R[j];
            var tp = new double[3];
            for (int i = 0; i < 3; i++) tp[i] = -((Rp[3 * i + 2] * Cz + Rp[3 * i + 1] * Cy) + Rp[3 * i] * Cx);
            R = Rp; t = tp;
        }
        var outc = new CalibData { K = (float[])baseCam.K.Clone(), R = new float[9], T = new float[3] };
        for (int i = 0; i < 9; i++) outc.R[i] = (float)R[i];
        for (int i = 0; i < 3; i++) outc.T[i] = (float)t[i];
        return outc;
    }

    /// <summary>`FUN_1803086a0`: principal point shift (K[2] −= cx, K[5] −= cy; the second matrix and view offset are handled by the caller's CalibData copy).</summary>
    public static CalibData Shift(CalibData c, float cx, float cy)
    {
        var o = new CalibData { K = (float[])c.K.Clone(), R = (float[])c.R.Clone(), T = (float[])c.T.Clone() };
        o.K[2] -= cx; o.K[5] -= cy;
        return o;
    }
}
