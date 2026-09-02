namespace Lux.Engine.Pipeline.Registration;

/// <summary>Full `lt::CalibData` (0xa8): K, t, R plus the accumulated principal-point offset / scale, distortion and K2.</summary>
public sealed class CalibDataFull
{
    public float[] K = new float[9], T = new float[3], R = new float[9], K2 = new float[9];
    public float OffX, OffY, ScX = 1f, ScY = 1f;
    public float[] Dist = Array.Empty<float>();
    public CalibDataFull Clone() => new() { K = (float[])K.Clone(), T = (float[])T.Clone(), R = (float[])R.Clone(), K2 = (float[])K2.Clone(), OffX = OffX, OffY = OffY, ScX = ScX, ScY = ScY, Dist = (float[])Dist.Clone() };
    public static CalibDataFull FromBytes(byte[] b, int off = 0, float[]? dist = null)
    {
        var c = new CalibDataFull();
        for (int i = 0; i < 9; i++) c.K[i] = BitConverter.ToSingle(b, off + 4 * i);
        for (int i = 0; i < 3; i++) c.T[i] = BitConverter.ToSingle(b, off + 0x24 + 4 * i);
        for (int i = 0; i < 9; i++) c.R[i] = BitConverter.ToSingle(b, off + 0x30 + 4 * i);
        c.OffX = BitConverter.ToSingle(b, off + 0x54); c.OffY = BitConverter.ToSingle(b, off + 0x58); c.ScX = BitConverter.ToSingle(b, off + 0x5c); c.ScY = BitConverter.ToSingle(b, off + 0x60);
        for (int i = 0; i < 9; i++) c.K2[i] = BitConverter.ToSingle(b, off + 0x80 + 4 * i);
        if (dist != null) c.Dist = dist;
        return c;
    }
    public CalibData Basic() => new() { K = (float[])K.Clone(), T = (float[])T.Clone(), R = (float[])R.Clone() };
}

/// <summary>The 0x88-byte view-transform `Pose` consumed by `FUN_1802e1580`.</summary>
public sealed class ViewPose
{
    public float[] P = new float[9], U = new float[3], Q = new float[9];
    public (float X, float Y) Scale1 = (1, 1), Shift1, Scale2 = (1, 1), Shift2, Shift3, Scale3 = (1, 1);
    /// <summary>`FUN_1802e4290`: P = I, U = 0, unit scales, zero shifts, Q = I.</summary>
    public static ViewPose Identity() => new() { P = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Q = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 } };
    public static ViewPose FromBytes(byte[] b)
    {
        var p = new ViewPose();
        for (int i = 0; i < 9; i++) p.P[i] = BitConverter.ToSingle(b, 4 * i);
        for (int i = 0; i < 3; i++) p.U[i] = BitConverter.ToSingle(b, 0x24 + 4 * i);
        p.Scale1 = (BitConverter.ToSingle(b, 0x30), BitConverter.ToSingle(b, 0x34)); p.Shift1 = (BitConverter.ToSingle(b, 0x38), BitConverter.ToSingle(b, 0x3c));
        p.Scale2 = (BitConverter.ToSingle(b, 0x40), BitConverter.ToSingle(b, 0x44)); p.Shift2 = (BitConverter.ToSingle(b, 0x48), BitConverter.ToSingle(b, 0x4c));
        for (int i = 0; i < 9; i++) p.Q[i] = BitConverter.ToSingle(b, 0x50 + 4 * i);
        p.Shift3 = (BitConverter.ToSingle(b, 0x74), BitConverter.ToSingle(b, 0x78)); p.Scale3 = (BitConverter.ToSingle(b, 0x7c), BitConverter.ToSingle(b, 0x80));
        return p;
    }
}

/// <summary>`FUN_1802e1580` (view transform), `FUN_1803086a0` (shift), `FUN_1803081a0` (scale), op-exact per the decompilation.</summary>
public static class ViewTransform
{
    public static CalibDataFull Shift(CalibDataFull c, float dx, float dy)
    {
        var o = c.Clone();
        o.K[2] = c.K[2] - dx; o.K[5] = c.K[5] - dy; o.K2[2] = c.K2[2] - dx; o.K2[5] = c.K2[5] - dy;
        o.OffX = c.OffX + dx; o.OffY = c.OffY + dy;
        return o;
    }

    public static CalibDataFull Scale(CalibDataFull c, float sx, float sy)
    {
        if (sx == 1.0f && sy == 1.0f) return c.Clone();
        if (!(sx > 0f) || !(sy > 0f)) throw new ArgumentException("Scale has to be a positive value.");
        var o = c.Clone();
        o.K[0] = c.K[0] * sx; o.K[4] = c.K[4] * sy; o.K[2] = sx * c.K[2]; o.K[5] = sy * c.K[5];
        o.K2[0] = c.K2[0] * sx; o.K2[4] = c.K2[4] * sy; o.K2[2] = c.K2[2] * sx; o.K2[5] = c.K2[5] * sy;
        o.OffX = sx * c.OffX; o.OffY = sy * c.OffY; o.ScX = sx * c.ScX; o.ScY = sy * c.ScY;
        return o;
    }

    /// <summary>`out.R = Q·(R·Pᵀ)`, `out.t = t − (R·Pᵀ)·u`, K/K2/off/scale through shift1→scale1→shift2→scale2→shift3→scale3.</summary>
    public static CalibDataFull Apply(ViewPose p, CalibDataFull b)
    {
        var L = b.Clone();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                L.R[3 * i + j] = (b.R[3 * i] * p.P[3 * j] + b.R[3 * i + 1] * p.P[3 * j + 1]) + b.R[3 * i + 2] * p.P[3 * j + 2];
        for (int i = 0; i < 3; i++) L.T[i] = b.T[i] - ((L.R[3 * i] * p.U[0] + L.R[3 * i + 1] * p.U[1]) + L.R[3 * i + 2] * p.U[2]);
        var F = Scale(Shift(Scale(Shift(Scale(Shift(L, p.Shift1.X, p.Shift1.Y), p.Scale1.X, p.Scale1.Y), p.Shift2.X, p.Shift2.Y), p.Scale2.X, p.Scale2.Y), p.Shift3.X, p.Shift3.Y), p.Scale3.X, p.Scale3.Y);
        var R = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                R[3 * i + j] = (p.Q[3 * i] * L.R[j] + p.Q[3 * i + 1] * L.R[3 + j]) + p.Q[3 * i + 2] * L.R[6 + j];
        F.R = R;
        return F;
    }
}

/// <summary>4×4 double helpers of the flow/projection matrices: `FUN_1800c2ea0` (scalar adjugate inverse, exact op list),
/// `FUN_1803014c0` (`((k3+k2)+(k1+k0))` product), `FUN_1803010a0` (`M = (K_B·E_B)·(K_A·E_A)⁻¹` rounded to float, column-major).</summary>
public static class Mat4D
{
    public static double[] Mul(double[] a, double[] b)
    {
        var o = new double[16];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                o[4 * i + j] = ((a[4 * i + 3] * b[12 + j] + a[4 * i + 2] * b[8 + j]) + (a[4 * i + 1] * b[4 + j] + a[4 * i] * b[j]));
        return o;
    }

    public static double[] Inverse(double[] m)
    {
        double m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7], m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15];
        var adj = new double[16];
        adj[0] = (m11 * (m22 * m33 - m23 * m32)) + ((m12 * (m23 * m31 - m33 * m21)) + (m13 * (m32 * m21 - m31 * m22)));
        adj[1] = (m01 * (m23 * m32 - m22 * m33)) + ((m02 * (m33 * m21 - m23 * m31)) + (m03 * (m31 * m22 - m32 * m21)));
        adj[2] = (((m02 * m13 - m12 * m03) * m31) + ((m12 * m01) * m33 + (m11 * m03) * m32)) - ((m01 * m13) * m32 + (m11 * m02) * m33);
        adj[3] = ((m23 * (m11 * m02 - m12 * m01)) + (m21 * (m12 * m03 - m02 * m13))) + (m22 * (m01 * m13 - m11 * m03));
        adj[4] = ((m12 * (m33 * m20 - m23 * m30)) + (m13 * (m22 * m30 - m32 * m20))) + (m10 * (m23 * m32 - m22 * m33));
        adj[5] = (m00 * (m22 * m33 - m23 * m32)) + ((m02 * (m23 * m30 - m33 * m20)) + (m03 * (m32 * m20 - m22 * m30)));
        adj[6] = (m33 * (m02 * m10 - m00 * m12)) + ((m30 * (m12 * m03 - m02 * m13)) + (m32 * (m00 * m13 - m03 * m10)));
        adj[7] = (((m02 * m13 - m12 * m03) * m20) + ((m03 * m10) * m22 + (m00 * m12) * m23)) - (m23 * (m02 * m10) + (m00 * m13) * m22);
        adj[8] = (m11 * (m23 * m30 - m33 * m20)) + ((m13 * (m31 * m20 - m21 * m30)) + (m10 * (m33 * m21 - m23 * m31)));
        adj[9] = (m00 * (m23 * m31 - m33 * m21)) + (((m33 * m20 - m23 * m30) * m01) + (m03 * (m21 * m30 - m31 * m20)));
        adj[10] = (((m01 * m13 - m11 * m03) * m30) + ((m00 * m11) * m33 + (m03 * m10) * m31)) - ((m00 * m13) * m31 + m33 * (m01 * m10));
        adj[11] = (m23 * (m01 * m10 - m00 * m11)) + ((m20 * (m11 * m03 - m01 * m13)) + (m21 * (m00 * m13 - m03 * m10)));
        adj[12] = (m11 * (m32 * m20 - m22 * m30)) + ((m12 * (m21 * m30 - m31 * m20)) + (m10 * (m31 * m22 - m32 * m21)));
        adj[13] = (m00 * (m32 * m21 - m31 * m22)) + (((m22 * m30 - m32 * m20) * m01) + (m02 * (m31 * m20 - m21 * m30)));
        adj[14] = (m32 * (m01 * m10 - m00 * m11)) + (((m11 * m02 - m12 * m01) * m30) + (m31 * (m00 * m12 - m02 * m10)));
        adj[15] = (((m12 * m01 - m11 * m02) * m20) + ((m02 * m10) * m21 + (m00 * m11) * m22)) - ((m01 * m10) * m22 + (m00 * m12) * m21);
        double det = ((adj[12] * m03 + m02 * adj[8]) + (adj[4] * m01 + m00 * adj[0]));
        double rd = 1.0 / det;
        var o = new double[16]; for (int k = 0; k < 16; k++) o[k] = adj[k] * rd;
        return o;
    }

    static double[] Kd(CalibData c) { var k = new double[16]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) k[4 * i + j] = c.K[3 * i + j]; k[15] = 1.0; return k; }
    static double[] Ed(CalibData c) { var e = new double[16]; for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) e[4 * i + j] = c.R[3 * i + j]; e[4 * i + 3] = c.T[i]; } e[15] = 1.0; return e; }

    /// <summary>`FUN_1803010a0`: column-major float `M = (K_B·E_B)·(K_A·E_A)⁻¹` (A → B projective map).</summary>
    public static float[] FlowMatrix(CalibData a, CalibData b)
    {
        var P1 = Mul(Kd(b), Ed(b)); var P2 = Mul(Kd(a), Ed(a));
        var M = Mul(P1, Inverse(P2));
        var o = new float[16];
        for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) o[4 * c + r] = (float)M[4 * r + c];
        return o;
    }
}
