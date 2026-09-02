using System;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>
/// CDF-9/7 lifting kernels of cp.dll's super-resolution merge (spec `a-resamp.md` §5.6), transcribed 1:1 from the
/// disassembly: <c>FUN_180447730</c> (forward, 16 rows), <c>FUN_180447b70</c> (forward, 16 columns),
/// <c>FUN_180448510</c> (inverse, 16 rows), <c>FUN_180448910</c> (inverse, 16 columns).
/// <para>All routines operate on a 16×16 grid of vec4 (4 floats per cell, row stride 0x100 B = 64 floats) starting at
/// float index <paramref name="o"/> of <paramref name="a"/>; byte offset B of the original pointer ↔ float index
/// <c>o + B/4</c>. Every SSE lane is independent, so each vec4 op is written as one scalar op per lane; the
/// mulps/addps/subps sequence and its association are exactly those of the machine code (no FMA in the binary).</para>
/// </summary>
internal static class Cdf97Kernels
{
    static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

    // ---- constants (all splat ×4 in the DLL); VA → bit pattern verified with rdconst ----
    /// <summary>1806aebc0: a = 1.5861343 (predict 1).</summary>
    public static readonly float A = F(0x3fcb0673);
    /// <summary>1806aebd0: 2a = 3.1722686.</summary>
    public static readonly float A2 = F(0x404b0673);
    /// <summary>1806aebe0: b = −0.05298012 (update 1).</summary>
    public static readonly float B = F(0xbd5901ae);
    /// <summary>1806aebf0: 2b = −0.10596023.</summary>
    public static readonly float B2 = F(0xbdd901ae);
    /// <summary>1806aec00: c = −0.8829111 (inverse predict).</summary>
    public static readonly float C = F(0xbf620676);
    /// <summary>1806aec10: 2c = −1.7658222.</summary>
    public static readonly float C2 = F(0xbfe20676);
    /// <summary>1806aec20: K = 1.1496044 (scale).</summary>
    public static readonly float K = F(0x3f93263d);
    /// <summary>1806aec30: 1/K = 0.8698644.</summary>
    public static readonly float IK = F(0x3f5eaf6f);
    /// <summary>1806dea80: d = −1.5360259 (forward predict 2, boundary).</summary>
    public static readonly float D = F(0xbfc49c7f);
    /// <summary>1806dea90: d/2 = −0.7680129 (forward predict 2).</summary>
    public static readonly float D2 = F(0xbf449c7f);
    /// <summary>1806deaa0: e = 0.5861344 (forward update 2).</summary>
    public static readonly float E = F(0x3f160ce8);
    /// <summary>1806deab0: 2e = 1.1722689.</summary>
    public static readonly float E2 = F(0x3f960ce8);
    /// <summary>1806dead0: −2a = −3.1722686 (used by the fused level-3/4 code of FUN_180441840).</summary>
    public static readonly float NEG2A = F(0xc04b0673);
    /// <summary>1806deb40: g = 1.0197150 (inverse update 2, boundary).</summary>
    public static readonly float G = F(0x3f828605);
    /// <summary>1806deb50: g/2 = 0.5098575 (inverse update 2).</summary>
    public static readonly float G2 = F(0x3f028605);

    const int RowStride = 64; // 0x100 B

    // =====================================================================================================
    // FUN_180447730: forward 9/7 on each of the 16 rows (rax = 0, 0x100, …, 0xf00); the 16 vec4 of a row are
    // x0..x15 at +0x00..+0xf0. Step A (o', e') is held in registers/stack and step B (o'', e'') is written back.
    // =====================================================================================================
    public static void Forward16Rows(float[] a, int o)
    {
        for (int r = 0; r < 16; r++)
        {
            int rb = o + r * RowStride;
            for (int l = 0; l < 4; l++)
            {
                FwdStepA(a, rb + l, 4);
                FwdStepB(a, rb + l, 4);
            }
        }
    }

    // =====================================================================================================
    // FUN_180447b70: forward 9/7 on each of the 16 columns (rax = −0x100 … −0x10 step 0x10, [rcx+rax+0x100·(k+1)]
    // = row k). Two loops: step A over all columns (o', e' written back in place), then step B over all columns.
    // =====================================================================================================
    public static void Forward16Cols(float[] a, int o)
    {
        for (int c = 0; c < 16; c++)
            for (int l = 0; l < 4; l++)
                FwdStepA(a, o + c * 4 + l, RowStride);
        for (int c = 0; c < 16; c++)
            for (int l = 0; l < 4; l++)
                FwdStepB(a, o + c * 4 + l, RowStride);
    }

    // =====================================================================================================
    // FUN_180448510: inverse 9/7 on each of the 16 rows (rax = 0 … 0xf00 step 0x100).
    // =====================================================================================================
    public static void Inverse16Rows(float[] a, int o)
    {
        for (int r = 0; r < 16; r++)
        {
            int rb = o + r * RowStride;
            for (int l = 0; l < 4; l++)
            {
                InvStepA(a, rb + l, 4);
                InvStepB(a, rb + l, 4);
            }
        }
    }

    // =====================================================================================================
    // FUN_180448910: inverse 9/7 on each of the 16 columns; two loops (step A over all columns, then step B).
    // =====================================================================================================
    public static void Inverse16Cols(float[] a, int o)
    {
        for (int c = 0; c < 16; c++)
            for (int l = 0; l < 4; l++)
                InvStepA(a, o + c * 4 + l, RowStride);
        for (int c = 0; c < 16; c++)
            for (int l = 0; l < 4; l++)
                InvStepB(a, o + c * 4 + l, RowStride);
    }

    // -----------------------------------------------------------------------------------------------------
    // One lane of a 16-sample signal x[k] = a[b + k·s]. Forward step A (FUN_180447730 up to the second
    // [rsp+0x10]/[rsp+0x20] stores; FUN_180447b70 first loop):
    //   o7' = x15 − x14·2a; o0' = x1 − (x2+x0)·a; o1' = x3 − (x4+x2)·a; e1' = (o1'+o0')·b + x2; …
    //   e7' = (o7'+o6')·b + x14; e0' = o0'·2b + x0.   Written in place: x[2k] ← e_k', x[2k+1] ← o_k'.
    // -----------------------------------------------------------------------------------------------------
    static void FwdStepA(float[] a, int b, int s)
    {
        float x0 = a[b], x1 = a[b + s], x2 = a[b + 2 * s], x3 = a[b + 3 * s];
        float x4 = a[b + 4 * s], x5 = a[b + 5 * s], x6 = a[b + 6 * s], x7 = a[b + 7 * s];
        float x8 = a[b + 8 * s], x9 = a[b + 9 * s], x10 = a[b + 10 * s], x11 = a[b + 11 * s];
        float x12 = a[b + 12 * s], x13 = a[b + 13 * s], x14 = a[b + 14 * s], x15 = a[b + 15 * s];

        float o7 = x15 - x14 * A2;            // mulps aebd0 ; subps
        float o0 = x1 - (x2 + x0) * A;        // addps ; mulps aebc0 ; subps
        float o1 = x3 - (x4 + x2) * A;
        float e1 = (o1 + o0) * B + x2;        // addps ; mulps aebe0 ; addps
        float o2 = x5 - (x6 + x4) * A;
        float e2 = (o2 + o1) * B + x4;
        float o3 = x7 - (x8 + x6) * A;
        float e3 = (o3 + o2) * B + x6;
        float o4 = x9 - (x10 + x8) * A;
        float e4 = (o4 + o3) * B + x8;
        float o5 = x11 - (x12 + x10) * A;
        float e5 = (o5 + o4) * B + x10;
        float o6 = x13 - (x14 + x12) * A;
        float e6 = (o6 + o5) * B + x12;
        float e7 = (o7 + o6) * B + x14;
        float e0 = o0 * B2 + x0;              // mulps aebf0 ; addps

        a[b] = e0; a[b + s] = o0; a[b + 2 * s] = e1; a[b + 3 * s] = o1;
        a[b + 4 * s] = e2; a[b + 5 * s] = o2; a[b + 6 * s] = e3; a[b + 7 * s] = o3;
        a[b + 8 * s] = e4; a[b + 9 * s] = o4; a[b + 10 * s] = e5; a[b + 11 * s] = o5;
        a[b + 12 * s] = e6; a[b + 13 * s] = o6; a[b + 14 * s] = e7; a[b + 15 * s] = o7;
    }

    // -----------------------------------------------------------------------------------------------------
    // Forward step B (rest of FUN_180447730; FUN_180447b70 second loop). Input x[2k] = e_k', x[2k+1] = o_k':
    //   o7'' = o7'·iK − e7'·d; o_k'' = o_k'·iK − (e_{k+1}' + e_k')·d2 (k = 0..6);
    //   e_k'' = (o_k'' + o_{k−1}'')·e + e_k'·K (k = 1..6); e7'' = (o6'' + o7'')·e + e7'·K; e0'' = o0''·2e + e0'·K.
    // -----------------------------------------------------------------------------------------------------
    static void FwdStepB(float[] a, int b, int s)
    {
        float e0 = a[b], o0 = a[b + s], e1 = a[b + 2 * s], o1 = a[b + 3 * s];
        float e2 = a[b + 4 * s], o2 = a[b + 5 * s], e3 = a[b + 6 * s], o3 = a[b + 7 * s];
        float e4 = a[b + 8 * s], o4 = a[b + 9 * s], e5 = a[b + 10 * s], o5 = a[b + 11 * s];
        float e6 = a[b + 12 * s], o6 = a[b + 13 * s], e7 = a[b + 14 * s], o7 = a[b + 15 * s];

        float O7 = o7 * IK - e7 * D;                  // mulps aec30 ; mulps dea80 ; subps
        float O0 = o0 * IK - (e1 + e0) * D2;          // mulps aec30 ; addps ; mulps dea90 ; subps
        float O1 = o1 * IK - (e2 + e1) * D2;
        float E1 = (O1 + O0) * E + e1 * K;            // addps ; mulps deaa0 ; addps (e1'·K via mulps aec20)
        float O2 = o2 * IK - (e3 + e2) * D2;
        float E2 = (O1 + O2) * E + e2 * K;
        float O3 = o3 * IK - (e4 + e3) * D2;
        float E3 = (O2 + O3) * E + e3 * K;
        float O4 = o4 * IK - (e5 + e4) * D2;
        float E4 = (O3 + O4) * E + e4 * K;
        float O5 = o5 * IK - (e6 + e5) * D2;
        float E5 = (O4 + O5) * E + e5 * K;
        float O6 = o6 * IK - (e7 + e6) * D2;
        float E6 = (O5 + O6) * E + e6 * K;
        float E7 = (O6 + O7) * E + e7 * K;
        float E0 = O0 * Cdf97Kernels.E2 + e0 * K;     // mulps deab0 ; mulps aec20 ; addps

        a[b] = E0; a[b + s] = O0; a[b + 2 * s] = E1; a[b + 3 * s] = O1;
        a[b + 4 * s] = E2; a[b + 5 * s] = O2; a[b + 6 * s] = E3; a[b + 7 * s] = O3;
        a[b + 8 * s] = E4; a[b + 9 * s] = O4; a[b + 10 * s] = E5; a[b + 11 * s] = O5;
        a[b + 12 * s] = E6; a[b + 13 * s] = O6; a[b + 14 * s] = E7; a[b + 15 * s] = O7;
    }

    // -----------------------------------------------------------------------------------------------------
    // Inverse step A (FUN_180448510 first half; FUN_180448910 first loop). Input x[2k] = e_k, x[2k+1] = o_k:
    //   e0' = x0·iK − x1·g; e_k' = x[2k]·iK − (x[2k+1] + x[2k−1])·g2 (k = 1..7);
    //   o_k' = (e_{k+1}' + e_k')·c + x[2k+1]·K (k = 0..6); o7' = e7'·2c + x15·K.
    // -----------------------------------------------------------------------------------------------------
    static void InvStepA(float[] a, int b, int s)
    {
        float x0 = a[b], x1 = a[b + s], x2 = a[b + 2 * s], x3 = a[b + 3 * s];
        float x4 = a[b + 4 * s], x5 = a[b + 5 * s], x6 = a[b + 6 * s], x7 = a[b + 7 * s];
        float x8 = a[b + 8 * s], x9 = a[b + 9 * s], x10 = a[b + 10 * s], x11 = a[b + 11 * s];
        float x12 = a[b + 12 * s], x13 = a[b + 13 * s], x14 = a[b + 14 * s], x15 = a[b + 15 * s];

        float e0 = x0 * IK - x1 * G;                  // mulps aec30 ; mulps deb40 ; subps
        float e1 = x2 * IK - (x3 + x1) * G2;          // mulps aec30 ; addps ; mulps deb50 ; subps
        float o0 = (e1 + e0) * C + x1 * K;            // addps ; mulps aec00 ; addps (x1·K via mulps aec20)
        float e2 = x4 * IK - (x5 + x3) * G2;
        float o1 = (e2 + e1) * C + x3 * K;
        float e3 = x6 * IK - (x7 + x5) * G2;
        float o2 = (e3 + e2) * C + x5 * K;
        float e4 = x8 * IK - (x9 + x7) * G2;
        float o3 = (e4 + e3) * C + x7 * K;
        float e5 = x10 * IK - (x11 + x9) * G2;
        float o4 = (e5 + e4) * C + x9 * K;
        float e6 = x12 * IK - (x13 + x11) * G2;
        float o5 = (e6 + e5) * C + x11 * K;
        float e7 = x14 * IK - (x15 + x13) * G2;
        float o6 = (e7 + e6) * C + x13 * K;
        float o7 = e7 * C2 + x15 * K;                 // mulps aec10 ; mulps aec20 ; addps

        a[b] = e0; a[b + s] = o0; a[b + 2 * s] = e1; a[b + 3 * s] = o1;
        a[b + 4 * s] = e2; a[b + 5 * s] = o2; a[b + 6 * s] = e3; a[b + 7 * s] = o3;
        a[b + 8 * s] = e4; a[b + 9 * s] = o4; a[b + 10 * s] = e5; a[b + 11 * s] = o5;
        a[b + 12 * s] = e6; a[b + 13 * s] = o6; a[b + 14 * s] = e7; a[b + 15 * s] = o7;
    }

    // -----------------------------------------------------------------------------------------------------
    // Inverse step B (FUN_180448510 second half; FUN_180448910 second loop). Input x[2k] = e_k', x[2k+1] = o_k':
    //   e0'' = e0' − o0'·2b; e_k'' = e_k' − (o_k' + o_{k−1}')·b (k = 1..7);
    //   o_k'' = (e_k'' + e_{k+1}'')·a + o_k' (k = 0..6); o7'' = e7''·2a + o7'.
    // -----------------------------------------------------------------------------------------------------
    static void InvStepB(float[] a, int b, int s)
    {
        float e0 = a[b], o0 = a[b + s], e1 = a[b + 2 * s], o1 = a[b + 3 * s];
        float e2 = a[b + 4 * s], o2 = a[b + 5 * s], e3 = a[b + 6 * s], o3 = a[b + 7 * s];
        float e4 = a[b + 8 * s], o4 = a[b + 9 * s], e5 = a[b + 10 * s], o5 = a[b + 11 * s];
        float e6 = a[b + 12 * s], o6 = a[b + 13 * s], e7 = a[b + 14 * s], o7 = a[b + 15 * s];

        float E0 = e0 - o0 * B2;                      // mulps aebf0 ; subps
        float E1 = e1 - (o1 + o0) * B;                // addps ; mulps aebe0 ; subps
        float O0 = (E0 + E1) * A + o0;                // addps ; mulps aebc0 ; addps
        float E2 = e2 - (o2 + o1) * B;
        float O1 = (E1 + E2) * A + o1;
        float E3 = e3 - (o3 + o2) * B;
        float O2 = (E2 + E3) * A + o2;
        float E4 = e4 - (o4 + o3) * B;
        float O3 = (E3 + E4) * A + o3;
        float E5 = e5 - (o5 + o4) * B;
        float O4 = (E4 + E5) * A + o4;
        float E6 = e6 - (o6 + o5) * B;
        float O5 = (E5 + E6) * A + o5;
        float E7 = e7 - (o7 + o6) * B;
        float O6 = (E6 + E7) * A + o6;
        float O7 = E7 * A2 + o7;                      // mulps aebd0 ; addps

        a[b] = E0; a[b + s] = O0; a[b + 2 * s] = E1; a[b + 3 * s] = O1;
        a[b + 4 * s] = E2; a[b + 5 * s] = O2; a[b + 6 * s] = E3; a[b + 7 * s] = O3;
        a[b + 8 * s] = E4; a[b + 9 * s] = O4; a[b + 10 * s] = E5; a[b + 11 * s] = O5;
        a[b + 12 * s] = E6; a[b + 13 * s] = O6; a[b + 14 * s] = E7; a[b + 15 * s] = O7;
    }
}
