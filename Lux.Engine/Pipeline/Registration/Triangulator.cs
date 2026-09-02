using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>`lt::CalibData` as the geometry code consumes it: row-major K, t, R (`X_cam = R·X + t`), all float.</summary>
public sealed class CalibData
{
    public float[] K = new float[9], T = new float[3], R = new float[9];
    public static CalibData FromBytes(byte[] b, int off = 0)
    {
        var c = new CalibData();
        for (int i = 0; i < 9; i++) c.K[i] = BitConverter.ToSingle(b, off + 4 * i);
        for (int i = 0; i < 3; i++) c.T[i] = BitConverter.ToSingle(b, off + 0x24 + 4 * i);
        for (int i = 0; i < 9; i++) c.R[i] = BitConverter.ToSingle(b, off + 0x30 + 4 * i);
        return c;
    }
}

/// <summary>`lt::Triangulator` point (20 bytes): reference pixel (u,v) and world XYZ (−1 = unset / rejected).</summary>
public struct TriPoint { public float U, V, X, Y, Z; }

/// <summary>
/// `lt::Triangulator` closed-form pass (`FUN_1802877f0` → `FUN_180288440` per camera, `FUN_180302420` two-ray
/// intersection with the epipolar correction of `FUN_180309de0`), ported op-for-op: for every observing camera in
/// ascending id, every point seen (u,v &gt; 0) gets `s` = the law-of-sines multiplier of the reference ray, `Z = s / ray.z`
/// in the reference frame, world = `inv(R_ref)·(X − t_ref)`; the first positive-depth pair fixes XYZ, all pairs update
/// the inverse-depth interval; points whose interval is wider than 1e-4 are flagged (−1,−1,−1).
/// </summary>
public static class Triangulator
{
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x35a00000);
    static readonly float IntervalMax = BitConverter.Int32BitsToSingle(0x38d1b717);   // 1e-4

    /// <summary>`FUN_1800c2a00` (cofactor 3×3 inverse, float, exact op order).</summary>
    public static float[] Inv3(float[] m)
    {
        float c0 = m[8] * m[4] - m[5] * m[7], c1 = m[3] * m[8] - m[6] * m[5], c2 = m[3] * m[7] - m[6] * m[4];
        float inv = 1.0f / ((c2 * m[2] + c0 * m[0]) - c1 * m[1]);
        return new[] { c0 * inv, (m[2] * m[7] - m[1] * m[8]) * inv, (m[1] * m[5] - m[2] * m[4]) * inv, -(c1 * inv), (m[8] * m[0] - m[2] * m[6]) * inv, (m[2] * m[3] - m[5] * m[0]) * inv, c2 * inv, (m[6] * m[1] - m[7] * m[0]) * inv, (m[4] * m[0] - m[3] * m[1]) * inv };
    }

    /// <summary>`FUN_180309de0`: fundamental matrix F (row-major) such that `F·(u_ref, v_ref, 1)` is the epipolar line in `other`.</summary>
    public static float[] Fundamental(CalibData r, CalibData o)
    {
        float[] Rr = r.R, Ro = o.R, tr = r.T, to = o.T, Kr = r.K;
        var M = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[3 * i + j] = Ro[3 * i + 2] * Rr[3 * j + 2] + (Ro[3 * i + 1] * Rr[3 * j + 1] + Ro[3 * i] * Rr[3 * j]);
        var e = new float[3]; for (int i = 0; i < 3; i++) e[i] = (tr[2] * M[3 * i + 2] + (tr[1] * M[3 * i + 1] + tr[0] * M[3 * i])) - to[i];
        var A = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) A[3 * i + j] = Kr[3 * i + 2] * M[3 * j + 2] + (Kr[3 * i + 1] * M[3 * j + 1] + Kr[3 * i] * M[3 * j]);
        var v = new float[3]; for (int i = 0; i < 3; i++) v[i] = A[3 * i + 2] * e[2] + (A[3 * i + 1] * e[1] + A[3 * i] * e[0]);
        var Koi = Inv3(o.K);
        var B = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) B[3 * i + j] = Koi[6 + i] * M[6 + j] + (Koi[3 + i] * M[3 + j] + Koi[i] * M[j]);
        var C = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) C[3 * i + j] = Kr[3 * i + 2] * B[3 * j + 2] + (Kr[3 * i + 1] * B[3 * j + 1] + Kr[3 * i] * B[3 * j]);
        float[] S = { 0, -v[2], v[1], v[2], 0, -v[0], -v[1], v[0], 0 };
        var F = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) F[3 * i + j] = C[6 + i] * S[6 + j] + (C[3 + i] * S[3 + j] + C[i] * S[j]);
        return F;
    }

    static float[] Normalize3(float l0, float l1, float l2)
    {
        if (MathF.Abs(l0) > Eps || MathF.Abs(l1) > Eps)
        {
            float n = l1 * l1 + l0 * l0;
            float y = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(n)).ToScalar();
            float t = ((n * y) * y) + (-3.0f);
            float sc = (y * (-0.5f)) * t;
            return new[] { l0 * sc, l1 * sc, sc * l2 };
        }
        return new[] { l0, l1, l2 };
    }

    /// <summary>`FUN_180302420(ref, other, u, v, uo, vo)`: the multiplier `s` of the reference ray (§3 of the spec).</summary>
    public static float RayScale(CalibData rf, CalibData ot, float u, float v, float uo, float vo)
    {
        var F = Fundamental(rf, ot);
        float l0 = (F[1] * v + F[0] * u) + F[2], l1 = (F[4] * v + F[3] * u) + F[5], l2 = (F[7] * v + F[6] * u) + F[8];
        var n = Normalize3(l0, l1, l2);
        float d = (n[1] * vo + n[2]) + n[0] * uo;
        float uo2 = uo - n[0] * d, vo2 = vo - d * n[1];
        var Kio = Inv3(ot.K); var Kir = Inv3(rf.K);
        float rox = Kio[2] + (Kio[1] * vo2 + Kio[0] * uo2), roy = Kio[5] + (Kio[4] * vo2 + Kio[3] * uo2), roz = Kio[8] + (Kio[7] * vo2 + Kio[6] * uo2);
        float rrx = Kir[2] + (Kir[1] * v + Kir[0] * u), rry = Kir[5] + (Kir[4] * v + Kir[3] * u), rrz = Kir[8] + (Kir[7] * v + Kir[6] * u);
        float[] Rr = rf.R, Ro = ot.R, tr = rf.T, to = ot.T;
        var Ap = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Ap[3 * i + j] = Rr[3 * i + 2] * Ro[3 * j + 2] + (Rr[3 * i + 1] * Ro[3 * j + 1] + Rr[3 * i] * Ro[3 * j]);
        float box = to[0] - ((Ap[0] * tr[0] + Ap[3] * tr[1]) + Ap[6] * tr[2]);
        float boy = to[1] - (Ap[1] * tr[0] + (Ap[4] * tr[1] + Ap[7] * tr[2]));
        float boz = to[2] - (Ap[8] * tr[2] + (Ap[5] * tr[1] + Ap[2] * tr[0]));
        float brx = tr[0] - ((Ap[0] * to[0] + Ap[1] * to[1]) + Ap[2] * to[2]);
        float bry = tr[1] - (Ap[3] * to[0] + (Ap[4] * to[1] + Ap[5] * to[2]));
        float brz = tr[2] - (Ap[8] * to[2] + (Ap[6] * to[0] + Ap[7] * to[1]));
        // lane 0 = other, lane 1 = ref
        float doto = boz * roz + (boy * roy + box * rox), dotr = brz * rrz + (bry * rry + brx * rrx);
        float ro2 = roz * roz + (roy * roy + rox * rox), rr2 = rrz * rrz + (rry * rry + rrx * rrx);
        float bo2 = boz * boz + (boy * boy + box * box), br2 = brz * brz + (bry * bry + brx * brx);
        float Po = bo2 * ro2, Pr = br2 * rr2;
        var Pv = Vector128.Create(Po, Pr, 0f, 0f);
        var yv = Sse.ReciprocalSqrt(Pv);
        float yo = yv.GetElement(0), yr = yv.GetElement(1);
        float coso = ((yo * (-0.5f)) * doto) * (((Po * yo) * yo) + (-3.0f));
        float cosr = ((yr * (-0.5f)) * dotr) * (((Pr * yr) * yr) + (-3.0f));
        double co = coso, cr = cosr;
        double sino = Math.Sqrt(1.0 - co * co), sinr = Math.Sqrt(1.0 - cr * cr);
        double S = (co * sinr) + (cr * sino);
        float q = br2 / rr2;
        float yq = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(q)).ToScalar();
        float sq = (((q * yq) * yq) + (-3.0f)) * ((-0.5f) * (q * yq));
        if (q == 0f) sq = 0f;
        double ratio = sino / S;
        float res = (float)((double)sq * ratio);
        return 0.0 < S ? res : (float)S;
    }

    /// <summary>Run the closed-form pass. `obs[c]` = observations of camera `cams[c]` indexed by point (u,v &gt; 0 valid);
    /// cameras must be given in ascending id. Returns the points (XYZ filled / flagged), the intervals and (1/maxB, 1/minA).</summary>
    public static (TriPoint[] Points, (float A, float B)[] Intervals, float Near, float Far) Triangulate(TriPoint[] pts, CalibData refCam, CalibData[] cams, float[][] obs)
    {
        int N = pts.Length;
        var P = (TriPoint[])pts.Clone();
        for (int i = 0; i < N; i++) { P[i].X = -1f; P[i].Y = -1f; P[i].Z = -1f; }
        var iv = new (float A, float B)[N]; for (int i = 0; i < N; i++) iv[i] = (float.MaxValue, -1f);
        var Kinv = Inv3(refCam.K); var Rinv = Inv3(refCam.R); float[] tr = refCam.T;
        for (int c = 0; c < cams.Length; c++)
        {
            var o = obs[c];
            for (int i = 0; i < N; i++)
            {
                float ou = o[2 * i], ov = o[2 * i + 1];
                if (!(0.0f < ou && 0.0f < ov)) continue;
                float u = P[i].U, v = P[i].V;
                float s = RayScale(refCam, cams[c], u, v, ou, ov);
                float rx = (Kinv[1] * v + Kinv[0] * u) + Kinv[2], ry = (Kinv[4] * v + Kinv[3] * u) + Kinv[5], rz = (Kinv[7] * v + Kinv[6] * u) + Kinv[8];
                float f = s / rz;
                float dx = rx * f - tr[0], dy = ry * f - tr[1], dz = rz * f - tr[2];
                float Zw = Rinv[8] * dz + (Rinv[7] * dy + Rinv[6] * dx);
                if (!(0.0f < Zw)) continue;
                if (P[i].Z <= 0.0f && P[i].Z != 0.0f)
                {
                    P[i].X = Rinv[2] * dz + (Rinv[1] * dy + Rinv[0] * dx);
                    P[i].Y = Rinv[5] * dz + (Rinv[4] * dy + Rinv[3] * dx);
                    P[i].Z = Zw;
                }
                float invZ = 1.0f / Zw;
                iv[i].A = iv[i].A < invZ ? iv[i].A : invZ;
                iv[i].B = invZ > iv[i].B ? invZ : iv[i].B;
            }
        }
        float minA = float.MaxValue, maxB = -1f;
        for (int i = 0; i < N; i++)
        {
            float a = iv[i].A, b = iv[i].B;
            if (a < b)
            {
                if (b - a > IntervalMax) { P[i].X = -1f; P[i].Y = -1f; P[i].Z = -1f; }
                minA = minA < a ? minA : a;
                maxB = b > maxB ? b : maxB;
            }
        }
        return (P, iv, 1.0f / maxB, 1.0f / minA);
    }
}
