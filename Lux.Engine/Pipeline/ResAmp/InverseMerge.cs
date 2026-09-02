using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>Bit-exact port of cp.dll <c>FUN_1804443f0(ws)</c> (Lumen 2.3, x86-64 SSE): normalise the accumulated wavelet
/// coefficients of one 16×16 vec4 tile by the per-slot weight sums and run the inverse CDF-9/7 (levels 4, 3, 2 fused/inlined,
/// level 1 via <see cref="Cdf97Kernels"/>). See spec <c>a-resamp.md</c> §5.2 / §5.5 / §5.6.
/// <para>Workspace <paramref name="ws"/> is the 0x26e0-byte tile workspace viewed as <c>float[]</c> (index = byte offset / 4):
/// 0x1580–0x257f = 16×16 vec4 coefficients (row stride 0x100 B), 0x2580–0x25cf = 5 vec4 weight sums,
/// 0x25d0–0x26cf = 256-byte slot table (one u8 per (row, col), read here via <see cref="MemoryMarshal.AsBytes{T}(Span{T})"/>,
/// i.e. the bytes must live in the <c>float[]</c>'s memory exactly as in the native workspace).</para>
/// <para>Fidelity: every lane op is one IEEE-single op in the machine's association; constants are loaded by bit pattern;
/// <c>rcpps</c> is the raw host <see cref="Sse.Reciprocal(Vector128{float})"/> with no Newton step.</para></summary>
internal static class InverseMerge
{
    // Lifting constants (each a 4-lane splat in the DLL; one lane shown).
    static readonly float A  = BitConverter.Int32BitsToSingle(0x3fcb0673); // 1806aebc0  a  =  1.5861343
    static readonly float A2 = BitConverter.Int32BitsToSingle(0x404b0673); // 1806aebd0  2a =  3.1722686
    static readonly float B  = BitConverter.Int32BitsToSingle(unchecked((int)0xbd5901ae)); // 1806aebe0  b  = -0.05298012
    static readonly float B2 = BitConverter.Int32BitsToSingle(unchecked((int)0xbdd901ae)); // 1806aebf0  2b = -0.10596023
    static readonly float C  = BitConverter.Int32BitsToSingle(unchecked((int)0xbf620676)); // 1806aec00  c  = -0.8829111
    static readonly float C2 = BitConverter.Int32BitsToSingle(unchecked((int)0xbfe20676)); // 1806aec10  2c = -1.7658222
    static readonly float K  = BitConverter.Int32BitsToSingle(0x3f93263d); // 1806aec20  K  =  1.1496044
    static readonly float IK = BitConverter.Int32BitsToSingle(0x3f5eaf6f); // 1806aec30  iK =  0.8698644
    static readonly float G  = BitConverter.Int32BitsToSingle(0x3f828605); // 1806deb40  g  =  1.0197150
    static readonly float G2 = BitConverter.Int32BitsToSingle(0x3f028605); // 1806deb50  g2 =  0.5098575

    const int Coef  = 0x1580 >> 2; // float index of the coefficient block (= return value)
    const int Wsum  = 0x2580 >> 2; // float index of the 5 vec4 weight sums
    const int SlotB = 0x25d0;      // byte offset of the 256-entry slot table

    /// <summary><c>FUN_1804443f0(ws)</c>. Returns the float index of the fused patch (<c>ws + 0x1580</c> → 1376).</summary>
    public static int Run(float[] ws)
    {
        // (1) ws[0x2580 + k*16] = rcpps(ws[0x2580 + k*16]), k = 0..4 (raw reciprocal estimate, no refinement).
        for (int k = 0; k < 5; k++)
        {
            int i = Wsum + k * 4;
            Vector128<float> v = Sse.Reciprocal(Vector128.Create(ws[i], ws[i + 1], ws[i + 2], ws[i + 3]));
            v.CopyTo(ws, i);
        }

        // (2) ws[0x1580 + (r,c)] *= ws[0x2580 + slot(r,c)*16], slot(r,c) = byte at ws+0x25d0 + r*16 + c
        //     (1804444b0–1804446ba: movzx esi,[rax-0xf..0]; shl rsi,4; mulps xmm,[rcx+rsi+0x2580]; rax += 0x10, rdx += 0x100).
        {
            ReadOnlySpan<byte> slots = MemoryMarshal.AsBytes(ws.AsSpan(SlotB >> 2, 64));
            for (int r = 0; r < 16; r++)
            {
                int row = Coef + r * 64;
                for (int c = 0; c < 16; c++)
                {
                    int p = row + c * 4;
                    int w = Wsum + slots[r * 16 + c] * 4;
                    ws[p]     = ws[p]     * ws[w];
                    ws[p + 1] = ws[p + 1] * ws[w + 1];
                    ws[p + 2] = ws[p + 2] * ws[w + 2];
                    ws[p + 3] = ws[p + 3] * ws[w + 3];
                }
            }
        }

        // (3a) Inverse level 4 (2×2 at (0,0),(0,8),(8,0),(8,8)) fused with inverse level 3 (4×4 at rows/cols 0,4,8,12).
        //      Decomp 358–826, transcribed statement by statement; lanes are independent so lane 0's statements are run per lane.
        //      Variable names vNN = the decomp's fVarNN (lane 0).
        for (int l = 0; l < 4; l++)
        {
            float v103 = ws[(0x15c0 >> 2) + l];
            // level 4, row 0: e' , o', e''
            float v57 = ws[(0x1580 >> 2) + l] * IK - ws[(0x1600 >> 2) + l] * G;
            float v33 = v57 * C2 + ws[(0x1600 >> 2) + l] * K;
            v57 = v57 - v33 * B2;
            // level 4, row 8: e', o', e'', o''
            float v16 = ws[(0x1d80 >> 2) + l] * IK - ws[(0x1e00 >> 2) + l] * G;
            float v73 = v16 * C2 + ws[(0x1e00 >> 2) + l] * K;
            v16 = v16 - v73 * B2;
            v73 = v16 * A2 + v73;
            // level 4, column 0 (rows 0,8 even parts) and column 8 (odd parts)
            float v82 = v57 * IK - v16 * G;
            float v58 = v82 * C2 + v16 * K;
            v16 = (v57 * A2 + v33) * IK - v73 * G;
            float v39 = v16 * C2 + v73 * K;
            v82 = v82 - v58 * B2;
            v57 = v82 * A2;
            v16 = v16 - v39 * B2;
            float v74 = v16 * A2;
            // level 3, row 0: x0 = v82 (LL), x1 = [0x15c0], x2 = v16, x3 = [0x1640]
            float v83 = v82 * IK - v103 * G;
            float v99 = v16 * IK - (ws[(0x1640 >> 2) + l] + v103) * G2;
            float v88 = (v99 + v83) * C + v103 * K;
            float v34 = v99 * C2 + ws[(0x1640 >> 2) + l] * K;
            v83 = v83 - v88 * B2;
            v99 = v99 - (v88 + v34) * B;
            v88 = (v99 + v83) * A + v88;
            v34 = v99 * A2 + v34;
            ws[(0x1580 >> 2) + l] = v83;
            ws[(0x1600 >> 2) + l] = v99;
            ws[(0x15c0 >> 2) + l] = v88;
            ws[(0x1640 >> 2) + l] = v34;
            // level 3, row 4: 0x1980, 0x19c0, 0x1a00, 0x1a40
            v103 = ws[(0x19c0 >> 2) + l];
            v82 = ws[(0x1980 >> 2) + l] * IK - v103 * G;
            float v29 = ws[(0x1a00 >> 2) + l] * IK - (ws[(0x1a40 >> 2) + l] + v103) * G2;
            float v59 = (v29 + v82) * C + v103 * K;
            float v17 = v29 * C2 + ws[(0x1a40 >> 2) + l] * K;
            v82 = v82 - v59 * B2;
            v29 = v29 - (v59 + v17) * B;
            v59 = (v29 + v82) * A + v59;
            v17 = v29 * A2 + v17;
            ws[(0x1980 >> 2) + l] = v82;
            ws[(0x1a00 >> 2) + l] = v29;
            ws[(0x19c0 >> 2) + l] = v59;
            ws[(0x1a40 >> 2) + l] = v17;
            // level 3, row 8: x0 = v57 + v58 (level-4 (8,0)), x1 = [0x1dc0], x2 = v74 + v39 (level-4 (8,8)), x3 = [0x1e40]
            v103 = ws[(0x1dc0 >> 2) + l];
            v33 = (v57 + v58) * IK - v103 * G;
            v73 = (v74 + v39) * IK - (ws[(0x1e40 >> 2) + l] + v103) * G2;
            float v84 = (v73 + v33) * C + v103 * K;
            float v80 = v73 * C2 + ws[(0x1e40 >> 2) + l] * K;
            v33 = v33 - v84 * B2;
            v73 = v73 - (v84 + v80) * B;
            v84 = (v73 + v33) * A + v84;
            v80 = v73 * A2 + v80;
            ws[(0x1d80 >> 2) + l] = v33;
            ws[(0x1e00 >> 2) + l] = v73;
            ws[(0x1dc0 >> 2) + l] = v84;
            ws[(0x1e40 >> 2) + l] = v80;
            // level 3, row 12: 0x2180, 0x21c0, 0x2200, 0x2240 (results kept in registers; 0x2200/0x2240 stored below)
            v103 = ws[(0x21c0 >> 2) + l];
            float v95 = ws[(0x2180 >> 2) + l] * IK - v103 * G;
            float v60 = ws[(0x2200 >> 2) + l] * IK - (ws[(0x2240 >> 2) + l] + v103) * G2;
            v16 = (v60 + v95) * C + v103 * K;
            v57 = v60 * C2 + ws[(0x2240 >> 2) + l] * K;
            v95 = v95 - v16 * B2;
            v60 = v60 - (v16 + v57) * B;
            v16 = (v60 + v95) * A + v16;
            v57 = v60 * A2 + v57;
            // level 3, columns — step 1 (e', o') for columns 0, 4, 8, 12 (rows 0,4,8,12 from registers)
            float v35 = v83 * IK - v82 * G;                       // col 0
            float v89 = v33 * IK - (v95 + v82) * G2;
            float v61 = (v89 + v35) * C + v82 * K;
            float v65 = v89 * C2 + v95 * K;
            v103 = v88 * IK - v59 * G;                            // col 4
            float v96 = v84 * IK - (v16 + v59) * G2;
            v84 = (v96 + v103) * C + v59 * K;
            float v69 = v96 * C2 + v16 * K;
            v16 = v99 * IK - v29 * G;                             // col 8
            float v87 = v73 * IK - (v60 + v29) * G2;
            v99 = (v87 + v16) * C + v29 * K;
            ws[(0x2200 >> 2) + l] = v60;                          // interim store (overwritten below), as the machine does
            float v78 = v87 * C2 + v60 * K;
            v33 = v34 * IK - v17 * G;                             // col 12
            float v31 = v80 * IK - (v57 + v17) * G2;
            float v20 = (v31 + v33) * C + v17 * K;
            ws[(0x2240 >> 2) + l] = v57;                          // interim store (overwritten below)
            v88 = v31 * C2 + v57 * K;
            // level 3, columns — step 2 (e'', o'') and stores
            v35 = v35 - v61 * B2;                                 // col 0
            v89 = v89 - (v61 + v65) * B;
            ws[(0x1580 >> 2) + l] = v35;
            ws[(0x1d80 >> 2) + l] = v89;
            ws[(0x1980 >> 2) + l] = (v35 + v89) * A + v61;
            ws[(0x2180 >> 2) + l] = v89 * A2 + v65;
            v103 = v103 - v84 * B2;                               // col 4
            v96 = v96 - (v84 + v69) * B;
            ws[(0x15c0 >> 2) + l] = v103;
            ws[(0x1dc0 >> 2) + l] = v96;
            ws[(0x19c0 >> 2) + l] = (v96 + v103) * A + v84;
            ws[(0x21c0 >> 2) + l] = v96 * A2 + v69;
            v16 = v16 - v99 * B2;                                 // col 8
            v87 = v87 - (v99 + v78) * B;
            ws[(0x1600 >> 2) + l] = v16;
            ws[(0x1e00 >> 2) + l] = v87;
            ws[(0x1a00 >> 2) + l] = (v87 + v16) * A + v99;
            ws[(0x2200 >> 2) + l] = v87 * A2 + v78;
            v33 = v33 - v20 * B2;                                 // col 12
            v31 = v31 - (v20 + v88) * B;
            ws[(0x1640 >> 2) + l] = v33;
            ws[(0x1e40 >> 2) + l] = v31;
            ws[(0x1a40 >> 2) + l] = (v31 + v33) * A + v20;
            ws[(0x2240 >> 2) + l] = v31 * A2 + v88;
        }

        // (3b) Inverse level 2, rows: even rows 0,2,..,14 (8 iterations, pointer += 0x200 B), 8 samples at columns 0,2,..,14.
        //      Decomp 827–962. On the first iteration z2/z4/z6 come from registers holding the values just stored at
        //      0x15c0/0x1600/0x1640 (bit-identical to the memory reads used here); later iterations read pfVar8[0x90/0xa0/0xb0].
        for (int r = 0; r < 16; r += 2)
        {
            int p = Coef + r * 64;
            for (int l = 0; l < 4; l++)
            {
                float z2 = ws[p + 0x10 + l], z4 = ws[p + 0x20 + l], z6 = ws[p + 0x30 + l];
                float v74 = ws[p + 0x08 + l];
                float v78 = ws[p + 0x18 + l];
                float v17 = ws[p + 0x28 + l];
                float v29 = ws[p + l] * IK - v74 * G;                       // e0'
                float v19 = z2 * IK - (v78 + v74) * G2;                     // e1'
                v74 = (v19 + v29) * C + v74 * K;                            // o0'
                float v86 = z4 * IK - (v17 + v78) * G2;                     // e2'
                float v16 = (v86 + v19) * C + v78 * K;                      // o1'
                v78 = z6 * IK - (ws[p + 0x38 + l] + v17) * G2;              // e3'
                float v33 = (v78 + v86) * C + v17 * K;                      // o2'
                float v103 = v78 * C2 + ws[p + 0x38 + l] * K;               // o3'
                v29 = v29 - v74 * B2;                                       // e0''
                v19 = v19 - (v16 + v74) * B;                                // e1''
                ws[p + l] = v29;
                ws[p + 0x10 + l] = v19;
                ws[p + 0x08 + l] = (v29 + v19) * A + v74;                   // o0''
                v86 = v86 - (v33 + v16) * B;                                // e2''
                ws[p + 0x20 + l] = v86;
                ws[p + 0x18 + l] = (v19 + v86) * A + v16;                   // o1''
                v78 = v78 - (v33 + v103) * B;                               // e3''
                ws[p + 0x30 + l] = v78;
                ws[p + 0x28 + l] = (v86 + v78) * A + v33;                   // o2''
                ws[p + 0x38 + l] = v78 * A2 + v103;                         // o3''
            }
        }

        // (3c) Inverse level 2, columns, step 1 (e', o'): even columns 0,2,..,14 over rows 0,2,..,14 (decomp 963–1020).
        //      pfVar8 = ws+0x2380 (row 14) + 8 floats per column pair; rows at q-0x380 (row 0), -0x300 (2), -0x280 (4),
        //      -0x200 (6), -0x180 (8), -0x100 (10), -0x80 (12), 0 (14).
        for (int c = 0; c < 16; c += 2)
        {
            int q = (0x2380 >> 2) + c * 4;
            for (int l = 0; l < 4; l++)
            {
                float v33 = ws[q - 0x300 + l];
                float v17 = ws[q - 0x380 + l] * IK - v33 * G;
                ws[q - 0x380 + l] = v17;
                float v74 = ws[q - 0x200 + l];
                float v78 = ws[q - 0x280 + l] * IK - (v74 + v33) * G2;
                ws[q - 0x280 + l] = v78;
                ws[q - 0x300 + l] = (v17 + v78) * C + v33 * K;
                v33 = ws[q - 0x100 + l];
                v17 = ws[q - 0x180 + l] * IK - (v33 + v74) * G2;
                ws[q - 0x180 + l] = v17;
                ws[q - 0x200 + l] = (v78 + v17) * C + v74 * K;
                v74 = ws[q - 0x80 + l] * IK - (ws[q + l] + v33) * G2;
                ws[q - 0x80 + l] = v74;
                ws[q - 0x100 + l] = (v17 + v74) * C + v33 * K;
                ws[q + l] = v74 * C2 + ws[q + l] * K;
            }
        }

        // (3d) Inverse level 2, columns, step 2 (e'', o'') (decomp 1021–1078).
        for (int c = 0; c < 16; c += 2)
        {
            int q = (0x2380 >> 2) + c * 4;
            for (int l = 0; l < 4; l++)
            {
                float v3 = ws[q - 0x300 + l];
                float v11 = ws[q - 0x380 + l] - v3 * B2;
                ws[q - 0x380 + l] = v11;
                float v48 = ws[q - 0x200 + l];
                float v47 = ws[q - 0x280 + l] - (v48 + v3) * B;
                ws[q - 0x280 + l] = v47;
                ws[q - 0x300 + l] = (v11 + v47) * A + v3;
                v3 = ws[q - 0x100 + l];
                v11 = ws[q - 0x180 + l] - (v3 + v48) * B;
                ws[q - 0x180 + l] = v11;
                ws[q - 0x200 + l] = (v47 + v11) * A + v48;
                v48 = ws[q - 0x80 + l] - (ws[q + l] + v3) * B;
                ws[q - 0x80 + l] = v48;
                ws[q - 0x100 + l] = (v11 + v48) * A + v3;
                ws[q + l] = v48 * A2 + ws[q + l];
            }
        }

        // (4) Inverse level 1: FUN_180448510 (rows) then FUN_180448910 (columns) on ws+0x1580.
        Cdf97Kernels.Inverse16Rows(ws, Coef);
        Cdf97Kernels.Inverse16Cols(ws, Coef);
        return Coef;
    }
}
