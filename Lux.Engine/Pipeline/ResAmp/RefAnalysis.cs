using System;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>
/// cp.dll <c>FUN_180441840(ws, L1img, Vec2i* g)</c> — reference-patch analysis of the super-resolution merge
/// (spec `a-resamp.md` §5.3). Transcribed 1:1 from the decompilation, with every summation tree and every
/// compiler-fused level-3/4 expression taken from the disassembly (180441840–180442c3f).
/// <para><c>ws</c> is the 0x26e0-byte workspace as <c>float[2488]</c> (index = byte offset / 4). Layout (§5.2):
/// 0x0000 reference 16×16 vec4 patch · 0x1000 level-1 LL 8×8 (stride 0x80) · 0x1400 level-2 LL 4×4 (stride 0x40) ·
/// 0x1500 level-3 LL 2×2 (stride 0x20) · 0x1540/50/60/70 e1..e4_ref · 0x1580 0.2·(4-level transformed patch, in-place
/// interleaved layout) · 0x2580 five weight sums (init 0.2) · 0x26d0 Σ|patch|.</para>
/// </summary>
internal static class RefAnalysis
{
    static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

    /// <summary>1806cac80: 0.2 (splat ×4).</summary>
    static readonly float Fifth = F(0x3e4ccccd);
    /// <summary>1806dea70: 1/192 (splat ×4).</summary>
    static readonly float Inv192 = F(0x3baaaaab);
    /// <summary>1806deac0: 1/96 (splat ×4).</summary>
    static readonly float Inv96 = F(0x3c2aaaab);
    /// <summary>1806deae0: 1/48 (splat ×4).</summary>
    static readonly float Inv48 = F(0x3caaaaab);
    /// <summary>1806deaf0: 1/24 (splat ×4).</summary>
    static readonly float Inv24 = F(0x3d2aaaab);

    // float indices of the workspace regions (byte offset / 4)
    const int WsPatch = 0x0000 / 4;
    const int WsLL1 = 0x1000 / 4;
    const int WsLL2 = 0x1400 / 4;
    const int WsLL3 = 0x1500 / 4;
    const int WsE1 = 0x1540 / 4, WsE2 = 0x1550 / 4, WsE3 = 0x1560 / 4, WsE4 = 0x1570 / 4;
    const int WsAcc = 0x1580 / 4;
    const int WsWeights = 0x2580 / 4;
    const int WsSumAbs = 0x26d0 / 4;

    /// <summary>andps 0x7fffffff (180682600).</summary>
    static float Abs(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & 0x7fffffff);

    /// <summary>
    /// Runs the reference analysis for the 16×16 patch of <paramref name="l1"/> whose top-left pixel is
    /// (<paramref name="gx"/>−8, <paramref name="gy"/>−8).
    /// </summary>
    public static void Run(float[] ws, ResImage l1, int gx, int gy)
    {
        // ---- 1. ws[0x2580..0x25cf] = 0.2 (five vec4) ----
        for (int i = 0; i < 20; i++) ws[WsWeights + i] = Fifth;

        // ---- 2. copy the 16×16 vec4 patch at L1 + (g.x−8) + (g.y−8)·stride into the local P (rsp+0xc0) ----
        // P[r][c] lane l is p[r*64 + c*4 + l] (row stride 0x100 B).
        var p = new float[1024];
        float[] src = l1.Data;
        for (int r = 0; r < 16; r++)
        {
            int si = l1.Idx(gx - 8, gy - 8 + r);
            int di = r * 64;
            for (int i = 0; i < 64; i++) p[di + i] = src[si + i];
        }

        // ---- 3. ws+0x26d0 = Σ|P| (andps 180682600), per row:
        //      acc = |x15| + ((|x14| + (|x13| + (|x12| + (|x11| + |x10|)))) + ((|x9| + (|x8| + (|x7| + |x6|)))
        //            + ((|x5| + (|x4| + |x3|)) + ((|x2| + |x1|) + (|x0| + acc)))))
        for (int l = 0; l < 4; l++)
        {
            float acc = 0f;
            for (int r = 0; r < 16; r++)
            {
                int b = r * 64 + l;
                float t = Abs(p[b]) + acc;
                t = (Abs(p[b + 8]) + Abs(p[b + 4])) + t;
                t = (Abs(p[b + 20]) + (Abs(p[b + 16]) + Abs(p[b + 12]))) + t;
                t = (Abs(p[b + 36]) + (Abs(p[b + 32]) + (Abs(p[b + 28]) + Abs(p[b + 24])))) + t;
                t = (Abs(p[b + 56]) + (Abs(p[b + 52]) + (Abs(p[b + 48]) + (Abs(p[b + 44]) + Abs(p[b + 40]))))) + t;
                acc = Abs(p[b + 60]) + t;
            }
            ws[WsSumAbs + l] = acc;
        }

        // ---- ws+0 ← P (untransformed reference patch) ----
        Array.Copy(p, 0, ws, WsPatch, 1024);

        // ---- 4. level-1 forward 9/7 on the LOCAL copy: FUN_180447730(P) rows, FUN_180447b70(P) columns ----
        Cdf97Kernels.Forward16Rows(p, 0);
        Cdf97Kernels.Forward16Cols(p, 0);

        // ---- 5. e1_ref = Σ|level-1 details|·(1/192); per row pair (A = row 2k, B = row 2k+1):
        //      acc = (|B14| + (|B15| + |A15|)) + ((|B12| + (|B13| + (|A13| + (|B10| + (|B11| + |A11|)))))
        //            + ((|B8| + (|B9| + (|A9| + (|B6| + |B7|)))) + ((|A7| + (|B4| + (|B5| + |A5|)))
        //            + ((|B2| + (|B3| + |A3|)) + ((|B0| + |B1|) + (|A1| + acc))))))
        for (int l = 0; l < 4; l++)
        {
            float acc = 0f;
            for (int k = 0; k < 8; k++)
            {
                int ra = (2 * k) * 64 + l, rb = ra + 64;
                float t = Abs(p[ra + 4]) + acc;
                t = (Abs(p[rb]) + Abs(p[rb + 4])) + t;
                t = (Abs(p[rb + 8]) + (Abs(p[rb + 12]) + Abs(p[ra + 12]))) + t;
                t = (Abs(p[ra + 28]) + (Abs(p[rb + 16]) + (Abs(p[rb + 20]) + Abs(p[ra + 20])))) + t;
                t = (Abs(p[rb + 32]) + (Abs(p[rb + 36]) + (Abs(p[ra + 36]) + (Abs(p[rb + 24]) + Abs(p[rb + 28]))))) + t;
                t = (Abs(p[rb + 48]) + (Abs(p[rb + 52]) + (Abs(p[ra + 52]) + (Abs(p[rb + 40]) + (Abs(p[rb + 44]) + Abs(p[ra + 44])))))) + t;
                acc = (Abs(p[rb + 56]) + (Abs(p[rb + 60]) + Abs(p[ra + 60]))) + t;
            }
            ws[WsE1 + l] = acc * Inv192;
        }

        // ---- 6. ws+0x1000 ← level-1 LL (even row, even col), packed 8×8 with row stride 0x80 ----
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                for (int l = 0; l < 4; l++)
                    ws[WsLL1 + r * 32 + c * 4 + l] = p[(2 * r) * 64 + (2 * c) * 4 + l];

        // ---- 7. level-2 forward (8-sample kernel) on the LL in place: rows 0,2,..,14 then columns 0,2,..,14 ----
        for (int r = 0; r < 16; r += 2)
            for (int l = 0; l < 4; l++)
                Fwd8Row(p, r * 64 + l);
        for (int c = 0; c < 16; c += 2)
            for (int l = 0; l < 4; l++)
                Fwd8ColA(p, c * 4 + l);
        for (int c = 0; c < 16; c += 2)
            for (int l = 0; l < 4; l++)
                Fwd8ColB(p, c * 4 + l);

        // ---- 8. e2_ref = Σ|level-2 details|·(1/96); per 4-row group (A = row 4k, C = row 4k+2):
        //      acc = (|C12| + |C14|) + ((|A14| + (|C8| + (|C10| + |A10|))) + ((|C4| + (|C6| + |A6|)) + ((|C0| + |C2|) + (|A2| + acc))))
        for (int l = 0; l < 4; l++)
        {
            float acc = 0f;
            for (int k = 0; k < 4; k++)
            {
                int ra = (4 * k) * 64 + l, rc = ra + 128;
                float t = Abs(p[ra + 8]) + acc;
                t = (Abs(p[rc]) + Abs(p[rc + 8])) + t;
                t = (Abs(p[rc + 16]) + (Abs(p[rc + 24]) + Abs(p[ra + 24]))) + t;
                t = (Abs(p[ra + 56]) + (Abs(p[rc + 32]) + (Abs(p[rc + 40]) + Abs(p[ra + 40])))) + t;
                acc = (Abs(p[rc + 48]) + Abs(p[rc + 56])) + t;
            }
            ws[WsE2 + l] = acc * Inv96;
        }

        // ---- 9. ws+0x1400 ← level-2 LL (rows/cols 0,4,8,12), packed 4×4 with row stride 0x40.
        //      (The binary interleaves these stores with the level-3 arithmetic, but each is stored before it is modified.)
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                for (int l = 0; l < 4; l++)
                    ws[WsLL2 + r * 16 + c * 4 + l] = p[(4 * r) * 64 + (4 * c) * 4 + l];

        // ---- level 3 (rows 0,4,8,12 then columns; compiler-fused) + e3_ref + ws+0x1500 + level 4 + e4_ref ----
        for (int l = 0; l < 4; l++)
        {
            float e3, e4;
            Level3And4(p, l, out e3, out e4, ws, WsLL3 + l);
            ws[WsE3 + l] = e3;
            ws[WsE4 + l] = e4;
        }

        // ---- 12. ws[0x1580 + pos] = P'[pos]·0.2 for all 256 vec4 (P' = the fully transformed local patch) ----
        for (int i = 0; i < 1024; i++) ws[WsAcc + i] = p[i] * Fifth;
    }

    // =====================================================================================================
    // Level-2 forward on one lane of one even row (decomp 593–700, disasm 180441e00): z_k = P[r][2k], k = 0..7.
    // Steps A and B are register-resident; written back at the end.
    // =====================================================================================================
    static void Fwd8Row(float[] p, int b)
    {
        float z0 = p[b], z1 = p[b + 8], z2 = p[b + 16], z3 = p[b + 24];
        float z4 = p[b + 32], z5 = p[b + 40], z6 = p[b + 48], z7 = p[b + 56];
        float A = Cdf97Kernels.A, A2 = Cdf97Kernels.A2, B = Cdf97Kernels.B, B2 = Cdf97Kernels.B2;
        float IK = Cdf97Kernels.IK, D = Cdf97Kernels.D, D2 = Cdf97Kernels.D2, E = Cdf97Kernels.E, E2 = Cdf97Kernels.E2, K = Cdf97Kernels.K;

        float o3 = z7 - z6 * A2;
        float o0 = z1 - (z2 + z0) * A;
        float o1 = z3 - (z4 + z2) * A;
        float e1 = (o1 + o0) * B + z2;
        float o2 = z5 - (z4 + z6) * A;
        float e2 = (o2 + o1) * B + z4;
        float e3 = (o2 + o3) * B + z6;
        float e0 = o0 * B2 + z0;
        float O3 = o3 * IK - e3 * D;
        float O0 = o0 * IK - (e1 + e0) * D2;
        float O1 = o1 * IK - (e2 + e1) * D2;
        float E1 = (O1 + O0) * E + e1 * K;
        float O2 = o2 * IK - (e3 + e2) * D2;
        float E2v = (O1 + O2) * E + e2 * K;
        float E3 = (O2 + O3) * E + e3 * K;
        float E0 = O0 * E2 + e0 * K;

        p[b] = E0; p[b + 8] = O0; p[b + 16] = E1; p[b + 24] = O1;
        p[b + 32] = E2v; p[b + 40] = O2; p[b + 48] = E3; p[b + 56] = O3;
    }

    // Level-2 forward, column pass, step A (decomp 701–760, disasm 180441fa0): z_k = P[2k][c], in place.
    static void Fwd8ColA(float[] p, int b)
    {
        const int S = 128; // two rows
        float A = Cdf97Kernels.A, A2 = Cdf97Kernels.A2, B = Cdf97Kernels.B, B2 = Cdf97Kernels.B2;
        float z0 = p[b], z2 = p[b + 2 * S], z4 = p[b + 4 * S], z6 = p[b + 6 * S];

        float o3 = p[b + 7 * S] - z6 * A2;
        float o0 = p[b + S] - (z2 + z0) * A;
        float o1 = p[b + 3 * S] - (z4 + z2) * A;
        float e1 = (o1 + o0) * B + z2;
        float o2 = p[b + 5 * S] - (z4 + z6) * A;
        float e2 = (o1 + o2) * B + z4;
        float e3 = (o2 + o3) * B + z6;
        float e0 = o0 * B2 + z0;

        p[b] = e0; p[b + S] = o0; p[b + 2 * S] = e1; p[b + 3 * S] = o1;
        p[b + 4 * S] = e2; p[b + 5 * S] = o2; p[b + 6 * S] = e3; p[b + 7 * S] = o3;
    }

    // Level-2 forward, column pass, step B (decomp 761–822, disasm 1804420b0): in place on the step-A output.
    static void Fwd8ColB(float[] p, int b)
    {
        const int S = 128;
        float IK = Cdf97Kernels.IK, D = Cdf97Kernels.D, D2 = Cdf97Kernels.D2, E = Cdf97Kernels.E, E2 = Cdf97Kernels.E2, K = Cdf97Kernels.K;
        float e0 = p[b], o0 = p[b + S], e1 = p[b + 2 * S], o1 = p[b + 3 * S];
        float e2 = p[b + 4 * S], o2 = p[b + 5 * S], e3 = p[b + 6 * S], o3 = p[b + 7 * S];

        float O3 = o3 * IK - e3 * D;
        float O0 = o0 * IK - (e1 + e0) * D2;
        float O1 = o1 * IK - (e2 + e1) * D2;
        float E1 = (O1 + O0) * E + e1 * K;
        float O2 = o2 * IK - (e3 + e2) * D2;
        float E2v = (O1 + O2) * E + e2 * K;
        float E3 = (O2 + O3) * E + e3 * K;
        float E0 = O0 * E2 + e0 * K;

        p[b] = E0; p[b + S] = O0; p[b + 2 * S] = E1; p[b + 3 * S] = O1;
        p[b + 4 * S] = E2v; p[b + 5 * S] = O2; p[b + 6 * S] = E3; p[b + 7 * S] = O3;
    }

    // =====================================================================================================
    // Level 3 (4-sample kernel on rows 0,4,8,12 then on columns 0,4,8,12) — the compiler fused/reordered the two
    // passes (decomp 872–1229, disasm 1804423f0–180442a20); statements below follow the machine order and
    // association. Then e3_ref, ws+0x1500 (level-3 LL), level 4 (2-sample kernel, fused; decomp 1258–1326) and
    // e4_ref. One lane; P[R][C] is p[R*64 + C*4 + l].
    // Naming: r{R}o{k}/r{R}e{k} = row pass step A of row R; r{R}O{k}/r{R}E{k} = step B; c{C}… = column pass on column C.
    // =====================================================================================================
    static void Level3And4(float[] p, int l, out float e3Ref, out float e4Ref, float[] ws, int ll3)
    {
        float A = Cdf97Kernels.A, A2 = Cdf97Kernels.A2, B = Cdf97Kernels.B, B2 = Cdf97Kernels.B2;
        float IK = Cdf97Kernels.IK, D = Cdf97Kernels.D, D2 = Cdf97Kernels.D2, E = Cdf97Kernels.E, E2 = Cdf97Kernels.E2;
        float K = Cdf97Kernels.K, NEG2A = Cdf97Kernels.NEG2A;

        int i00 = l, i04 = 16 + l, i08 = 32 + l, i012 = 48 + l;
        int i40 = 256 + l, i44 = 272 + l, i48 = 288 + l, i412 = 304 + l;
        int i80 = 512 + l, i84 = 528 + l, i88 = 544 + l, i812 = 560 + l;
        int i120 = 768 + l, i124 = 784 + l, i128 = 800 + l, i1212 = 816 + l;

        // ---- row 0 (w0..w3 = P[0][0], P[0][4], P[0][8], P[0][12]) ----
        float r0o1 = p[i012] - p[i08] * A2;
        float r0o0 = p[i04] - (p[i08] + p[i00]) * A;
        float r0e1 = (r0o0 + r0o1) * B + p[i08];
        float r0e0 = r0o0 * B2 + p[i00];
        float r0O1 = r0o1 * IK - r0e1 * D;
        float r0O0 = r0o0 * IK - (r0e1 + r0e0) * D2;
        float r0E1 = (r0O0 + r0O1) * E + r0e1 * K;

        // ---- row 4 ----
        float r4o1 = p[i412] - p[i48] * A2;
        float r4o0 = p[i44] - (p[i48] + p[i40]) * A;
        float r4e1 = (r4o0 + r4o1) * B + p[i48];
        float r4e0 = r4o0 * B2 + p[i40];
        float r4O0 = r4o0 * IK - (r4e1 + r4e0) * D2;

        // ---- row 8 ----
        float r8o1 = p[i812] - p[i88] * A2;
        float r8o0 = p[i84] - (p[i88] + p[i80]) * A;
        float r8e1 = (r8o0 + r8o1) * B + p[i88];
        float r8e0 = r8o0 * B2 + p[i80];
        float r8O0 = r8o0 * IK - (r8e1 + r8e0) * D2;

        // column 4 step A begins (needs rows 0, 4, 8 of column 4 = the o0'' values)
        float c4o0 = r4O0 - (r8O0 + r0O0) * A;
        float c4e0 = c4o0 * B2 + r0O0;
        float r0E0 = r0O0 * E2 + r0e0 * K;
        float r4O1 = r4o1 * IK - r4e1 * D;
        float r4E1 = (r4O0 + r4O1) * E + r4e1 * K;
        float r4E0 = r4O0 * E2 + r4e0 * K;
        float r8O1 = r8o1 * IK - r8e1 * D;
        float r8E1 = (r8O0 + r8O1) * E + r8e1 * K;

        // ---- row 12 ----
        float r12o1 = p[i1212] - p[i128] * A2;
        float r12o0 = p[i124] - (p[i128] + p[i120]) * A;
        float r12e1 = (r12o0 + r12o1) * B + p[i128];
        float r12e0 = r12o0 * B2 + p[i120];
        float r12O0 = r12o0 * IK - (r12e1 + r12e0) * D2;
        float c4o1 = r12O0 - r8O0 * A2;
        float c4e1 = (c4o1 + c4o0) * B + r8O0;
        float r8E0 = r8O0 * E2 + r8e0 * K;
        float r12O1 = r12o1 * IK - r12e1 * D;
        float r12E1 = (r12O0 + r12O1) * E + r12e1 * K;
        float r12E0 = r12O0 * E2 + r12e0 * K;

        // ---- column passes, step A ----
        // column 0 (values e0'' of rows 0,4,8,12): o1' computed as (row8·(−2a)) + row12  [mulps dead0 ; addps]
        float c0o1 = r8E0 * NEG2A + r12E0;
        float c0o0 = r4E0 - (r8E0 + r0E0) * A;
        float c0e1 = (c0o1 + c0o0) * B + r8E0;
        float c0e0 = c0o0 * B2 + r0E0;
        // column 8 (values e1'')
        float c8o1 = r12E1 - r8E1 * A2;
        float c8o0 = r4E1 - (r8E1 + r0E1) * A;
        float c8e1 = (c8o1 + c8o0) * B + r8E1;
        float c8e0 = c8o0 * B2 + r0E1;
        // column 12 (values o1'')
        float c12o1 = r12O1 - r8O1 * A2;
        float c12o0 = r4O1 - (r8O1 + r0O1) * A;
        float c12e1 = (c12o1 + c12o0) * B + r8O1;
        float c12e0 = c12o0 * B2 + r0O1;

        // ---- column passes, step B ----
        float c0O1 = c0o1 * IK - c0e1 * D;              // → P[12][0]
        float c0O0 = c0o0 * IK - (c0e1 + c0e0) * D2;    // → P[4][0]
        float c0E1 = (c0O1 + c0O0) * E + c0e1 * K;      // → P[8][0]
        float c0E0 = c0O0 * E2 + c0e0 * K;              // → P[0][0]

        float c4O1 = c4o1 * IK - c4e1 * D;              // → P[12][4]
        float c4O0 = c4o0 * IK - (c4e1 + c4e0) * D2;    // → P[4][4]
        float c4E1 = (c4O1 + c4O0) * E + c4e1 * K;      // → P[8][4]
        float c4E0 = c4O0 * E2 + c4e0 * K;              // → P[0][4]

        float c8O1 = c8o1 * IK - c8e1 * D;              // → P[12][8]
        float c8O0 = c8o0 * IK - (c8e1 + c8e0) * D2;    // → P[4][8]
        float c8E1 = (c8O1 + c8O0) * E + c8e1 * K;      // → P[8][8]
        float c8E0 = c8O0 * E2 + c8e0 * K;              // → P[0][8]

        float c12O1 = c12o1 * IK - c12e1 * D;           // → P[12][12]
        float c12O0 = c12o0 * IK - (c12e1 + c12e0) * D2; // → P[4][12]
        float c12E1 = (c12O0 + c12O1) * E + c12e1 * K;  // → P[8][12]
        float c12E0 = c12O0 * E2 + c12e0 * K;           // → P[0][12]

        p[i120] = c0O1; p[i40] = c0O0; p[i80] = c0E1; p[i00] = c0E0;
        p[i124] = c4O1; p[i44] = c4O0; p[i84] = c4E1; p[i04] = c4E0;
        p[i128] = c8O1; p[i48] = c8O0; p[i88] = c8E1; p[i08] = c8E0;
        p[i1212] = c12O1; p[i412] = c12O0; p[i812] = c12E1; p[i012] = c12E0;

        // ---- 10. e3_ref = (Σ 12 |level-3 details|)·(1/48):
        //   (|(12,8)| + (|(12,12)| + |(8,12)|)) + ((|(12,0)| + (|(12,4)| + |(8,4)|))
        //     + ((|(4,8)| + |(4,12)|) + ((|(0,12)| + |(4,0)|) + (|(4,4)| + |(0,4)|))))
        {
            float t = Abs(c4O0) + Abs(c4E0);
            t = (Abs(c12E0) + Abs(c0O0)) + t;
            t = (Abs(c8O0) + Abs(c12O0)) + t;
            t = (Abs(c0O1) + (Abs(c4O1) + Abs(c4E1))) + t;
            t = (Abs(c8O1) + (Abs(c12O1) + Abs(c12E1))) + t;
            e3Ref = t * Inv48;
        }

        // ---- 11. ws+0x1500 ← level-3 LL: (0,0), (0,8), (8,0), (8,8) ----
        ws[ll3] = c0E0; ws[ll3 + 4] = c8E0; ws[ll3 + 8] = c0E1; ws[ll3 + 12] = c8E1;

        // ---- level 4 (2-sample kernel; rows 0 and 8 on (·,0),(·,8), then columns 0 and 8; fused) ----
        float v00 = c0E0, v08 = c8E0, v80 = c0E1, v88 = c8E1;
        float q0o = v08 - v00 * A2;
        float q0e = q0o * B2 + v00;
        float q0O = q0o * IK - q0e * D;
        float q0E = q0O * E2 + q0e * K;
        float q8o = v88 - v80 * A2;
        float q8e = q8o * B2 + v80;
        float q8O = q8o * IK - q8e * D;
        float q8E = q8O * E2 + q8e * K;
        // column 0: o' = (row0·(−2a)) + row8   [mulps dead0 ; addps]
        float k0o = NEG2A * q0E + q8E;
        float k0e = k0o * B2 + q0E;
        // column 8
        float k8o = q8O - A2 * q0O;
        float k8e = B2 * k8o + q0O;
        float k0O = k0o * IK - k0e * D;        // → P[8][0]
        float k0E = k0O * E2 + k0e * K;        // → P[0][0]
        float k8O = k8o * IK - D * k8e;        // → P[8][8]
        float k8E = E2 * k8O + k8e * K;        // → P[0][8]
        p[i80] = k0O; p[i00] = k0E; p[i88] = k8O; p[i08] = k8E;

        // e4_ref = (|(8,0)| + (|(8,8)| + |(0,8)|))·(1/24)
        e4Ref = (Abs(k0O) + (Abs(k8O) + Abs(k8E))) * Inv24;
    }
}
