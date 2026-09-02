using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>
/// Lumen 2.3 cp.dll super-resolution merge of one module 16×16 vec4 patch into the tile workspace:
/// <c>FUN_180442d60(ws, blk) → float</c> (disasm 1135519–1136777), plus its two leaf helpers
/// <c>FUN_180448020</c> (gain normalise) and <c>FUN_180448250</c> (8×8 LL SSIM).
/// Transcribed 1:1 from the disassembly: every lane op is one IEEE-single op in the machine's association order,
/// rcpps/rcpss/rsqrtss are the raw host approximations (no Newton step unless the code does one),
/// max/min follow Intel semantics (second operand returned when either is NaN).
/// <para>
/// <c>ws</c> is the 0x26e0-byte tile workspace as floats (index = byte offset / 4): 0x0000 reference 16×16 vec4 patch
/// (row stride 0x100 B), 0x1000 ref level-1 LL 8×8 (stride 0x80), 0x1400 level-2 LL 4×4 (stride 0x40), 0x1500 level-3 LL 2×2
/// (stride 0x20), 0x1540/50/60/70 e1_ref..e4_ref, 0x1580 accumulated weighted coefficients 16×16 vec4 (in-place interleaved
/// wavelet layout), 0x2580 five vec4 weight sums, 0x25d0 256-byte u8 slot table (read via <see cref="MemoryMarshal"/>),
/// 0x26d0 vec4 Σ|ref|. <c>blk</c> is the module's 16×16 vec4 patch (1024 floats, row stride 64), transformed in place
/// (after the call it holds the module's forward wavelet coefficients, gain-normalised).
/// </para>
/// </summary>
internal static class ModuleMerge
{
    // ---------------------------------------------------------------- constants (bit patterns read from the DLL)
    static readonly float A   = BitConverter.Int32BitsToSingle(0x3fcb0673);                 // 1806aebc0  a   =  1.5861343
    static readonly float A2  = BitConverter.Int32BitsToSingle(0x404b0673);                 // 1806aebd0  2a  =  3.1722686
    static readonly float NA2 = BitConverter.Int32BitsToSingle(unchecked((int)0xc04b0673)); // 1806dead0  -2a = -3.1722686
    static readonly float B   = BitConverter.Int32BitsToSingle(unchecked((int)0xbd5901ae)); // 1806aebe0  b   = -0.05298012
    static readonly float B2  = BitConverter.Int32BitsToSingle(unchecked((int)0xbdd901ae)); // 1806aebf0  2b  = -0.10596023
    static readonly float K   = BitConverter.Int32BitsToSingle(0x3f93263d);                 // 1806aec20  K   =  1.1496044
    static readonly float IK  = BitConverter.Int32BitsToSingle(0x3f5eaf6f);                 // 1806aec30  iK  =  0.8698644
    static readonly float D   = BitConverter.Int32BitsToSingle(unchecked((int)0xbfc49c7f)); // 1806dea80  d   = -1.5360259
    static readonly float D2  = BitConverter.Int32BitsToSingle(unchecked((int)0xbf449c7f)); // 1806dea90  d2  = -0.7680129
    static readonly float E   = BitConverter.Int32BitsToSingle(0x3f160ce8);                 // 1806deaa0  e   =  0.5861344
    static readonly float E2  = BitConverter.Int32BitsToSingle(0x3f960ce8);                 // 1806deab0  2e  =  1.1722689

    static readonly float Inv256 = BitConverter.Int32BitsToSingle(0x3b800000); // 1806aebb0  1/256 ×4
    static readonly float Inv192 = BitConverter.Int32BitsToSingle(0x3baaaaab); // 1806bbad0  1/192 (scalar)
    static readonly float Inv96  = BitConverter.Int32BitsToSingle(0x3c2aaaab); // 1806deb30  1/96  (lane 0 of (1/96,1/48,1/24,0))
    static readonly float Inv48  = BitConverter.Int32BitsToSingle(0x3caaaaab); // 1806deb34  1/48
    static readonly float Inv24  = BitConverter.Int32BitsToSingle(0x3d2aaaab); // 1806deb38  1/24
    static readonly float Inv64  = BitConverter.Int32BitsToSingle(0x3c800000); // 1806deb60  1/64 ×4
    static readonly float Inv16  = BitConverter.Int32BitsToSingle(0x3d800000); // 180682450  1/16 ×4
    static readonly float Quarter = BitConverter.Int32BitsToSingle(0x3e800000); // 180681eb0 (×4) / 180681ed0 (scalar) 0.25
    static readonly float Half   = BitConverter.Int32BitsToSingle(0x3f000000); // 180682404  0.5
    static readonly float Eighth = BitConverter.Int32BitsToSingle(0x3e000000); // 180685d40  0.125
    static readonly float Eight  = BitConverter.Int32BitsToSingle(0x41000000); // 180685d4c  8.0
    static readonly float P05    = BitConverter.Int32BitsToSingle(0x3d4ccccd); // 180681ed4  0.05
    static readonly float One    = BitConverter.Int32BitsToSingle(0x3f800000); // 180681c78  1.0
    static readonly float NegHalf = BitConverter.Int32BitsToSingle(unchecked((int)0xbf000000)); // 180681c7c -0.5
    static readonly float NegThree = BitConverter.Int32BitsToSingle(unchecked((int)0xc0400000)); // 180681c80 -3.0
    static readonly float Eps    = BitConverter.Int32BitsToSingle(0x3727c5ac); // 18068b2e0  1e-5 ×4

    static readonly V4 C1   = new(BitConverter.Int32BitsToSingle(0x3c23d70a), BitConverter.Int32BitsToSingle(0x3cf5c28f),
                                  BitConverter.Int32BitsToSingle(0x3cf5c28f), BitConverter.Int32BitsToSingle(0x3f800000)); // 1806deb00 (0.01, 0.03, 0.03, 1)
    static readonly V4 C2   = new(BitConverter.Int32BitsToSingle(unchecked((int)0xbf4ccccd)), BitConverter.Int32BitsToSingle(unchecked((int)0xbf4ccccd)),
                                  BitConverter.Int32BitsToSingle(unchecked((int)0xbf4ccccd)), BitConverter.Int32BitsToSingle(unchecked((int)0x80000000))); // 1806deb10 (-0.8, -0.8, -0.8, -0.0)
    static readonly V4 C3   = new(BitConverter.Int32BitsToSingle(0x40a86bca), BitConverter.Int32BitsToSingle(0x40a86bca),
                                  BitConverter.Int32BitsToSingle(0x40a86bca), BitConverter.Int32BitsToSingle(0x3f800000)); // 1806deb20 (5.2631578 ×3, 1)
    static readonly V4 ONE  = new(1f, 1f, 1f, 1f);                                                                      // 1806824a0
    static readonly V4 ZERO = new(0f, 0f, 0f, 0f);                                                                      // 18068c1d0 / xorps
    static readonly V4 BL   = new(0f, 1f, 1f, 1f);                                                                      // 18069fdb0

    // ---------------------------------------------------------------- ws float indices
    const int RefL1  = 0x1000 >> 2; // ref level-1 LL 8×8, stride 0x80 B = 32 floats
    const int RefL2  = 0x1400 >> 2; // ref level-2 LL 4×4, stride 0x40 B = 16 floats
    const int RefL3  = 0x1500 >> 2; // ref level-3 LL 2×2, stride 0x20 B = 8 floats
    const int E1Ref  = 0x1540 >> 2;
    const int E2Ref  = 0x1550 >> 2;
    const int E3Ref  = 0x1560 >> 2;
    const int E4Ref  = 0x1570 >> 2;
    const int Acc    = 0x1580 >> 2; // accumulated weighted coefficients
    const int Wsum   = 0x2580 >> 2; // five vec4 weight sums
    const int SlotB  = 0x25d0;      // byte offset of the 256-entry slot table
    const int SumAbs = 0x26d0 >> 2; // vec4 Σ|ref|

    // ================================================================ FUN_180442d60(ws, blk) → float
    /// <summary>Diagnostics sink for the per-level statistics and weights of the patch being merged.
    /// <c>ImageResolutionAmp</c> points it at the console for the grid points listed in its <c>DebugPoints</c> set and
    /// leaves it null otherwise.</summary>
    public static Action<string>? Dbg;

    /// <summary>
    /// Merge one module patch: gain-normalise <paramref name="blk"/> against the reference, forward-wavelet it
    /// (levels 1–4, in place), compute the per-level SSIM/detail-energy weights w1..w4 against the reference pyramid,
    /// accumulate <c>ws[0x2580 + (k−1)·16] += W_k</c> and <c>ws[0x1580 + pos] += W_k(slot(pos))·blk[pos]</c>, and return
    /// the module confidence <c>≈ sqrt(W1·W2)</c> (xmm0 lane 0).
    /// </summary>
    public static float Run(float[] ws, float[] blk)
    {
        // ---- A. gain normalise (FUN_180448020)
        GainNormalise(ws, blk);

        // ---- B. 16×16 SSIM vs the reference patch (ws+0), sequential row-major accumulation, 2 vec4 per iteration
        V4 sb = ZERO, sr = ZERO, sbb = ZERO, srr = ZERO, sbr = ZERO;
        for (int r = 0; r < 16; r++)
        {
            int row = r * 64;
            for (int c = 0; c < 16; c += 2)
            {
                // first vec4 of the pair
                V4 b = V4.Load(blk, row + c * 4);
                V4 rr = V4.Load(ws, row + c * 4);
                sb = sb + b;
                sr = sr + rr;
                V4 x6 = rr * b;
                V4 x5 = b * b;
                x5 = x5 + sbb;              // Sbb' = b·b + Sbb
                V4 b1 = V4.Load(blk, row + c * 4 + 4);
                x6 = x6 + sbr;              // Sbr' = r·b + Sbr
                V4 r1 = V4.Load(ws, row + c * 4 + 4);
                V4 x7 = rr * rr;
                x7 = x7 + srr;              // Srr' = r·r + Srr
                // second vec4 of the pair
                sb = sb + b1;
                sr = sr + r1;
                V4 x4 = r1 * b1;
                V4 x3 = b1 * b1;
                sbb = x3 + x5;
                V4 x1 = r1 * r1;
                srr = x1 + x7;
                sbr = x4 + x6;
            }
        }
        {
            V4 s = V4.Splat(Inv256);
            sb = sb * s; sr = sr * s; sbb = sbb * s; srr = srr * s; sbr = sbr * s;
        }
        V4 q1;
        {
            V4 vb = sbb - sb * sb;  vb = SseMax(vb, ZERO);
            V4 vr = srr - sr * sr;  vr = SseMax(vr, ZERO);
            V4 cov = sbr - sr * sb; cov = SseMax(cov, ZERO);
            V4 fac = V4.Splat(sb.W);                         // shufps 0xff
            V4 num = (cov + cov) + C1;
            num = num * fac;
            V4 den = (vb + C1) + vr;
            V4 q = Rcp(den) * num;                            // rcpps RAW
            q = (q + C2) * C3;
            Dbg?.Invoke($"L1 stats: sb ({sb.X:R},{sb.Y:R},{sb.Z:R},{sb.W:R}) sr ({sr.X:R},{sr.Y:R},{sr.Z:R},{sr.W:R}) vb ({vb.X:R},{vb.Y:R},{vb.Z:R},{vb.W:R}) vr ({vr.X:R},{vr.Y:R},{vr.Z:R},{vr.W:R}) cov ({cov.X:R},{cov.Y:R},{cov.Z:R},{cov.W:R}) rawq ({q.X:R},{q.Y:R},{q.Z:R},{q.W:R})");
            q1 = SseMin(SseMax(q, ZERO), ONE);                // [rsp+0x90]
        }

        // ---- C. level-1 forward 9/7 of blk (FUN_180447730 rows, FUN_180447b70 columns), then e1x and w1
        Cdf97Kernels.Forward16Rows(blk, 0);
        Cdf97Kernels.Forward16Cols(blk, 0);
        V4 w1;
        {
            V4 acc = ZERO;                                    // xmm7 (zeroed at 180442e8a)
            for (int i = 0; i < 8; i++)
            {
                int a = i * 128;      // even row 2i
                int bb = a + 64;      // odd row 2i+1
                V4 t0, t1, t2;
                t0 = Abs(V4.Load(blk, a + 4)) + acc;                          // |A1| + acc
                t1 = Abs(V4.Load(blk, bb + 4));                               // |B1|
                t2 = Abs(V4.Load(blk, bb + 0)) + t1;                          // |B0| + |B1|
                t2 = t2 + t0;
                t0 = Abs(V4.Load(blk, a + 12));                               // |A3|
                t1 = Abs(V4.Load(blk, bb + 12)) + t0;                         // |B3| + |A3|
                t0 = Abs(V4.Load(blk, bb + 8)) + t1;                          // |B2| + …
                t0 = t0 + t2;
                t1 = Abs(V4.Load(blk, a + 20));                               // |A5|
                t2 = Abs(V4.Load(blk, bb + 20)) + t1;                         // |B5| + |A5|
                t1 = Abs(V4.Load(blk, bb + 16)) + t2;                         // |B4| + …
                t2 = Abs(V4.Load(blk, a + 28)) + t1;                          // |A7| + …
                t2 = t2 + t0;
                t0 = Abs(V4.Load(blk, bb + 28));                              // |B7|
                t1 = Abs(V4.Load(blk, bb + 24)) + t0;                         // |B6| + |B7|
                t0 = Abs(V4.Load(blk, a + 36)) + t1;                          // |A9| + …
                t1 = Abs(V4.Load(blk, bb + 36)) + t0;                         // |B9| + …
                t0 = Abs(V4.Load(blk, bb + 32)) + t1;                         // |B8| + …
                t0 = t0 + t2;
                t1 = Abs(V4.Load(blk, a + 44));                               // |A11|
                t2 = Abs(V4.Load(blk, bb + 44)) + t1;                         // |B11| + |A11|
                t1 = Abs(V4.Load(blk, bb + 40)) + t2;                         // |B10| + …
                t2 = Abs(V4.Load(blk, a + 52)) + t1;                          // |A13| + …
                t1 = Abs(V4.Load(blk, bb + 52)) + t2;                         // |B13| + …
                t2 = Abs(V4.Load(blk, bb + 48)) + t1;                         // |B12| + …
                t2 = t2 + t0;
                t0 = Abs(V4.Load(blk, a + 60));                               // |A15|
                t1 = Abs(V4.Load(blk, bb + 60)) + t0;                         // |B15| + |A15|
                acc = Abs(V4.Load(blk, bb + 56)) + t1;                        // |B14| + …
                acc = acc + t2;
            }
            float e1ref = ws[E1Ref];
            float e1x = acc.X * Inv192;                       // mulss lane 0
            float d1 = e1x - e1ref;
            d1 = d1 * Eight;
            float s1 = RcpS(e1ref + P05);                     // rcpss RAW
            s1 = s1 * d1;
            s1 = s1 + One;
            V4 sv = SseMax(ZERO, V4.Splat(s1));               // maxps xmm1(0), xmm0(s1)
            sv = SseMin(sv, ONE);
            sv = Blend0xE(sv, BL);                            // (s1c, 1, 1, 1)
            w1 = sv * q1;                                     // [rsp+0x90]
            Dbg?.Invoke($"L1: q1 ({q1.X:R},{q1.Y:R},{q1.Z:R},{q1.W:R}) e1x {e1x:R} e1ref {e1ref:R} sv.x {sv.X:R} w1 ({w1.X:R},{w1.Y:R},{w1.Z:R})");
        }

        // ---- D. 8×8 LL SSIM (FUN_180448250): b = blk (even,even), r = ws+0x1000
        V4 q2;
        Ssim8x8Core(blk, ws, RefL1, out q2);                  // [rsp+0x20] / [rsp+0xa0]

        // ---- E. level-2 forward of the 8×8 LL in place (rows 180443130, cols 1804432c0 then 1804433d0)
        for (int i = 0; i < 8; i++)
        {
            int p = i * 128;                                  // row 2i; z_j at column 2j = +8j floats
            V4 z0 = V4.Load(blk, p), z1 = V4.Load(blk, p + 8), z2 = V4.Load(blk, p + 16), z3 = V4.Load(blk, p + 24);
            V4 z4 = V4.Load(blk, p + 32), z5 = V4.Load(blk, p + 40), z6 = V4.Load(blk, p + 48), z7 = V4.Load(blk, p + 56);
            V4 o3p = z7 - z6 * A2;
            V4 o0p = z1 - (z2 + z0) * A;
            V4 o1p = z3 - (z4 + z2) * A;
            V4 e1p = (o1p + o0p) * B + z2;
            V4 o2p = z5 - (z4 + z6) * A;
            V4 e2p = (o2p + o1p) * B + z4;
            V4 e3p = (o2p + o3p) * B + z6;
            V4 e0p = o0p * B2 + z0;
            V4 o3pp = o3p * IK - e3p * D;
            V4 o0pp = o0p * IK - (e1p + e0p) * D2;
            V4 o1pp = o1p * IK - (e2p + e1p) * D2;
            V4 e1pp = (o1pp + o0pp) * E + e1p * K;
            o3pp.Store(blk, p + 56);
            o0pp.Store(blk, p + 8);
            o1pp.Store(blk, p + 24);
            e1pp.Store(blk, p + 16);
            V4 o2pp = o2p * IK - (e3p + e2p) * D2;
            V4 e2pp = (o1pp + o2pp) * E + e2p * K;
            o2pp.Store(blk, p + 40);
            e2pp.Store(blk, p + 32);
            V4 e3pp = (o2pp + o3pp) * E + e3p * K;
            e3pp.Store(blk, p + 48);
            V4 e0pp = o0pp * E2 + e0p * K;
            e0pp.Store(blk, p);
        }
        for (int j = 0; j < 8; j++)                           // columns, step A (1804432c0)
        {
            int p = j * 8;                                    // column 2j; z_k at row 2k = +128k floats
            V4 z0 = V4.Load(blk, p), z1 = V4.Load(blk, p + 128), z2 = V4.Load(blk, p + 256), z3 = V4.Load(blk, p + 384);
            V4 z4 = V4.Load(blk, p + 512), z5 = V4.Load(blk, p + 640), z6 = V4.Load(blk, p + 768), z7 = V4.Load(blk, p + 896);
            V4 o3p = z7 - z6 * A2;                 o3p.Store(blk, p + 896);
            V4 o0p = z1 - (z2 + z0) * A;           o0p.Store(blk, p + 128);
            V4 o1p = z3 - (z4 + z2) * A;           o1p.Store(blk, p + 384);
            V4 e1p = (o1p + o0p) * B + z2;         e1p.Store(blk, p + 256);
            V4 o2p = z5 - (z4 + z6) * A;           o2p.Store(blk, p + 640);
            V4 e2p = (o1p + o2p) * B + z4;         e2p.Store(blk, p + 512);
            V4 e3p = (o2p + o3p) * B + z6;         e3p.Store(blk, p + 768);
            V4 e0p = o0p * B2 + z0;                e0p.Store(blk, p);
        }
        for (int j = 0; j < 8; j++)                           // columns, step B (1804433d0)
        {
            int p = j * 8;
            V4 e0p = V4.Load(blk, p), o0p = V4.Load(blk, p + 128), e1p = V4.Load(blk, p + 256), o1p = V4.Load(blk, p + 384);
            V4 e2p = V4.Load(blk, p + 512), o2p = V4.Load(blk, p + 640), e3p = V4.Load(blk, p + 768), o3p = V4.Load(blk, p + 896);
            V4 o3pp = o3p * IK - e3p * D;                 o3pp.Store(blk, p + 896);
            V4 o0pp = o0p * IK - (e1p + e0p) * D2;        o0pp.Store(blk, p + 128);
            V4 o1pp = o1p * IK - (e2p + e1p) * D2;        o1pp.Store(blk, p + 384);
            V4 e1pp = (o1pp + o0pp) * E + e1p * K;        e1pp.Store(blk, p + 256);
            V4 o2pp = o2p * IK - (e2p + e3p) * D2;        o2pp.Store(blk, p + 640);
            V4 e2pp = (o1pp + o2pp) * E + e2p * K;        e2pp.Store(blk, p + 512);
            V4 e3pp = (o2pp + o3pp) * E + e3p * K;        e3pp.Store(blk, p + 768);
            V4 e0pp = o0pp * E2 + e0p * K;                e0pp.Store(blk, p);
        }
        V4 w2;
        {
            V4 acc = ZERO;
            for (int k = 0; k < 4; k++)                       // 4-row groups: A = row 4k, C = row 4k+2; column c (0..15) = +4c floats (disasm 1804434c3: rax = blk+0x2e0, byte offsets)
            {
                int a = k * 256, c = a + 128;
                V4 t0, t1, t2;
                t1 = Abs(V4.Load(blk, a + 8)) + acc;                          // |A2| + acc          ([rax-0x2c0])
                t0 = Abs(V4.Load(blk, c + 8));                                // |C2|                ([rax-0xc0])
                t2 = Abs(V4.Load(blk, c + 0)) + t0;                           // |C0| + |C2|         ([rax-0xe0])
                t2 = t2 + t1;
                t0 = Abs(V4.Load(blk, a + 24));                               // |A6|                ([rax-0x280])
                t1 = Abs(V4.Load(blk, c + 24)) + t0;                          // |C6| + |A6|         ([rax-0x80])
                t0 = Abs(V4.Load(blk, c + 16)) + t1;                          // |C4| + …            ([rax-0xa0])
                t0 = t0 + t2;
                t1 = Abs(V4.Load(blk, a + 40));                               // |A10|               ([rax-0x240])
                t2 = Abs(V4.Load(blk, c + 40)) + t1;                          // |C10| + |A10|       ([rax-0x40])
                t1 = Abs(V4.Load(blk, c + 32)) + t2;                          // |C8| + …            ([rax-0x60])
                t2 = Abs(V4.Load(blk, a + 56)) + t1;                          // |A14| + …           ([rax-0x200])
                t2 = t2 + t0;
                t1 = Abs(V4.Load(blk, c + 56));                               // |C14|               ([rax])
                t0 = Abs(V4.Load(blk, c + 48)) + t1;                          // |C12| + |C14|       ([rax-0x20])
                acc = t0 + t2;
            }
            float e2ref = ws[E2Ref];
            float e2x = acc.X * Inv96;
            float d2 = e2x - e2ref;
            d2 = d2 * Eight;
            float s2 = RcpS(e2ref + P05);
            s2 = s2 * d2;
            s2 = s2 + One;
            V4 sv = SseMax(V4.Splat(s2), ZERO);               // maxps xmm2(s2), xmm1(0)
            sv = SseMin(sv, ONE);
            sv = Blend0xE(sv, BL);
            w2 = sv * q2;                                     // [rsp+0xa0]
            Dbg?.Invoke($"L2: q2 ({q2.X:R},{q2.Y:R},{q2.Z:R},{q2.W:R}) e2x {e2x:R} e2ref {e2ref:R} sv.x {sv.X:R} w2 ({w2.X:R},{w2.Y:R},{w2.Z:R})");
        }

        // ---- F. 4×4 LL SSIM: b = blk(4i,4j), r = ws+0x1400 + i·0x40 + j·0x10
        V4 q3;
        {
            sb = ZERO; sr = ZERO; sbb = ZERO; srr = ZERO; sbr = ZERO;
            for (int i = 0; i < 4; i++)
            {
                int p = i * 256, rp = RefL2 + i * 16;
                V4 b0 = V4.Load(blk, p), b1 = V4.Load(blk, p + 16), b2 = V4.Load(blk, p + 32), b3 = V4.Load(blk, p + 48);
                V4 r0 = V4.Load(ws, rp), r1 = V4.Load(ws, rp + 4), r2 = V4.Load(ws, rp + 8), r3 = V4.Load(ws, rp + 12);
                sb = sb + b0;
                sr = sr + r0;
                V4 p0 = r0 * b0;
                V4 bb0 = b0 * b0;  bb0 = bb0 + sbb;
                p0 = p0 + sbr;
                V4 rr0 = r0 * r0;  rr0 = rr0 + srr;
                V4 bb1 = b1 * b1;
                V4 rr1 = r1 * r1;
                V4 p1 = r1 * b1;
                V4 tb = b1 + b2;   sb = sb + tb;
                V4 tr = r1 + r2;   sr = sr + tr;
                V4 p2 = r2 * b2;
                V4 bb2 = b2 * b2;  bb2 = bb2 + bb1;  bb2 = bb2 + bb0;
                V4 rr2 = r2 * r2;  rr2 = rr2 + rr1;  rr2 = rr2 + rr0;
                p2 = p2 + p1;      p2 = p2 + p0;
                sb = sb + b3;
                sr = sr + r3;
                V4 p3 = r3 * b3;
                V4 bb3 = b3 * b3;  sbb = bb3 + bb2;
                V4 rr3 = r3 * r3;  srr = rr3 + rr2;
                sbr = p3 + p2;
            }
            V4 s = V4.Splat(Inv16);
            sb = sb * s; sr = sr * s; sbb = sbb * s; srr = srr * s; sbr = sbr * s;
            V4 vb = sbb - sb * sb;
            V4 vr = srr - sr * sr;
            V4 cov = sbr - sr * sb;
            cov = SseMax(cov, ZERO);
            V4 fac = V4.Splat(sb.W * Quarter);                // shufps 0xe7; mulss 0.25; shufps 0
            V4 num = (cov + cov) + C1;
            num = num * fac;
            vb = SseMax(vb, ZERO);
            vr = SseMax(vr, ZERO);
            V4 den = (vb + C1) + vr;
            V4 q = Rcp(den) * num;
            q = (q + C2) * C3;
            q3 = SseMin(SseMax(q, ZERO), ONE);                // [rsp+0x80] / [rsp+0xb0]
        }

        // ---- G. level-3 forward of the 4×4 LL (180443781–180443e3d), compiler-fused rows 0/4/8/12 then columns.
        //      w[r][c] = blk[256r + 16c]. Row-stage intermediate stores are dead (all 16 positions are rewritten by the
        //      column stage) and are omitted.
        V4 c0e0, c0o0, c0e1, c0o1, c1e0, c1o0, c1e1, c1o1, c2e0, c2o0, c2e1, c2o1, c3e0, c3o0, c3e1, c3o1;
        {
            // row 0
            V4 w00 = V4.Load(blk, 0), w01 = V4.Load(blk, 16), w02 = V4.Load(blk, 32), w03 = V4.Load(blk, 48);
            V4 r0o1p = w03 - w02 * A2;
            V4 r0o0p = w01 - (w00 + w02) * A;
            V4 r0e1p = (r0o0p + r0o1p) * B + w02;
            V4 r0e0p = r0o0p * B2 + w00;
            V4 r0o1 = r0o1p * IK - r0e1p * D;
            V4 r0o0 = r0o0p * IK - (r0e1p + r0e0p) * D2;
            V4 r0e1 = (r0o0 + r0o1) * E + r0e1p * K;
            V4 r0e0 = r0o0 * E2 + r0e0p * K;
            // row 1
            V4 w10 = V4.Load(blk, 256), w11 = V4.Load(blk, 272), w12 = V4.Load(blk, 288), w13 = V4.Load(blk, 304);
            V4 r1o1p = w13 - w12 * A2;
            V4 r1o0p = w11 - (w10 + w12) * A;
            V4 r1e1p = (r1o0p + r1o1p) * B + w12;
            V4 r1e0p = r1o0p * B2 + w10;
            V4 r1o1 = r1o1p * IK - r1e1p * D;
            V4 r1o0 = r1o0p * IK - (r1e1p + r1e0p) * D2;
            V4 r1e1 = (r1o0 + r1o1) * E + r1e1p * K;
            V4 r1e0 = r1o0 * E2 + r1e0p * K;
            // row 2
            V4 w20 = V4.Load(blk, 512), w21 = V4.Load(blk, 528), w22 = V4.Load(blk, 544), w23 = V4.Load(blk, 560);
            V4 r2o1p = w23 - w22 * A2;
            V4 r2o0p = w21 - (w20 + w22) * A;
            V4 r2e1p = (r2o0p + r2o1p) * B + w22;
            V4 r2e0p = r2o0p * B2 + w20;
            V4 r2o1 = r2o1p * IK - r2e1p * D;
            V4 r2o0 = r2o0p * IK - (r2e1p + r2e0p) * D2;
            V4 r2e1 = (r2o0 + r2o1) * E + r2e1p * K;
            // row 3
            V4 w30 = V4.Load(blk, 768), w31 = V4.Load(blk, 784), w32 = V4.Load(blk, 800), w33 = V4.Load(blk, 816);
            V4 r3o1p = w33 - w32 * A2;
            V4 r3o0p = w31 - (w30 + w32) * A;
            V4 r3e1p = (r3o0p + r3o1p) * B + w32;
            V4 r3e0p = r3o0p * B2 + w30;
            V4 r3o0 = r3o0p * IK - (r3e1p + r3e0p) * D2;
            // column 1 (o0'' values), stage A — interleaved with the tail of rows 2/3
            V4 c1o1p = r3o0 - r2o0 * A2;
            V4 c1o0p = r1o0 - (r2o0 + r0o0) * A;
            V4 r2e0K = r2e0p * K;
            V4 c1e1p = (c1o1p + c1o0p) * B + r2o0;
            V4 r2e0 = r2o0 * E2 + r2e0K;                       // deferred e0'' of row 2
            V4 r3o1 = r3o1p * IK - r3e1p * D;
            V4 r3e1 = (r3o0 + r3o1) * E + r3e1p * K;
            V4 r3e0 = r3o0 * E2 + r3e0p * K;
            // column 0 (e0'' values), stage A
            V4 c0o1p = r2e0 * NA2 + r3e0;                      // mulps by −2a, then addps
            V4 c0o0p = r1e0 - (r2e0 + r0e0) * A;
            V4 c0e1p = (c0o1p + c0o0p) * B + r2e0;
            V4 c0e0p = c0o0p * B2 + r0e0;
            V4 c1e0p = c1o0p * B2 + r0o0;
            // column 2 (e1'' values), stage A
            V4 c2o1p = r3e1 - r2e1 * A2;
            V4 c2o0p = r1e1 - (r2e1 + r0e1) * A;
            V4 c2e1p = (c2o1p + c2o0p) * B + r2e1;
            V4 c2e0p = c2o0p * B2 + r0e1;
            // column 3 (o1'' values), stage A
            V4 c3o1p = r3o1 - r2o1 * A2;
            V4 c3o0p = r1o1 - (r2o1 + r0o1) * A;
            V4 c3e1p = (c3o1p + c3o0p) * B + r2o1;
            V4 c3e0p = c3o0p * B2 + r0o1;
            // stage B, column 0
            c0o1 = c0o1p * IK - c0e1p * D;
            c0o0 = c0o0p * IK - (c0e1p + c0e0p) * D2;
            c0e1 = (c0o1 + c0o0) * E + c0e1p * K;
            c0e0 = c0o0 * E2 + c0e0p * K;
            // stage B, column 1
            c1o1 = c1o1p * IK - c1e1p * D;
            c1o0 = c1o0p * IK - (c1e1p + c1e0p) * D2;
            c1e1 = (c1o1 + c1o0) * E + c1e1p * K;
            c1e0 = c1o0 * E2 + c1e0p * K;
            // stage B, column 2
            c2o1 = c2o1p * IK - c2e1p * D;
            c2o0 = c2o0p * IK - (c2e1p + c2e0p) * D2;
            c2e1 = (c2o1 + c2o0) * E + c2e1p * K;
            c2e0 = c2o0 * E2 + c2e0p * K;
            // stage B, column 3
            c3o1 = c3o1p * IK - c3e1p * D;
            c3o0 = c3o0p * IK - (c3e1p + c3e0p) * D2;
            c3e1 = (c3o0 + c3o1) * E + c3e1p * K;
            c3e0 = c3o0 * E2 + c3e0p * K;
            // final stores: w[r][c] with r ∈ {e0'', o0'', e1'', o1''} rows 0/4/8/12 and c the column stage
            c0e0.Store(blk, 0);   c1e0.Store(blk, 16);  c2e0.Store(blk, 32);  c3e0.Store(blk, 48);
            c0o0.Store(blk, 256); c1o0.Store(blk, 272); c2o0.Store(blk, 288); c3o0.Store(blk, 304);
            c0e1.Store(blk, 512); c1e1.Store(blk, 528); c2e1.Store(blk, 544); c3e1.Store(blk, 560);
            c0o1.Store(blk, 768); c1o1.Store(blk, 784); c2o1.Store(blk, 800); c3o1.Store(blk, 816);
        }
        V4 w3;
        {
            // Σ of the 12 level-3 |details| (w(r,c) = blk row 4r col 4c):
            V4 t14 = Abs(c1o0) + Abs(c1e0);                   // |w11| + |w01|
            V4 t1 = Abs(c3e0) + Abs(c0o0);                    // |w03| + |w10|
            t1 = t1 + t14;
            V4 t2 = Abs(c2o0) + Abs(c3o0);                    // |w12| + |w13|
            t2 = t2 + t1;
            V4 t3 = t2;
            t1 = Abs(c1o1) + Abs(c1e1);                       // |w31| + |w21|
            t2 = Abs(c0o1) + t1;                              // |w30| + …
            t2 = t2 + t3;
            V4 t11 = Abs(c3o1) + Abs(c3e1);                   // |w33| + |w23|
            V4 t0 = Abs(c2o1) + t11;                          // |w32| + …
            t0 = t0 + t2;
            float e3ref = ws[E3Ref];
            float e3x = t0.X * Inv48;
            float d3 = e3x - e3ref;
            float s3 = RcpS(e3ref + P05);
            s3 = s3 * Eight;
            s3 = s3 * d3;
            s3 = s3 + One;
            V4 sv = SseMax(V4.Splat(s3), ZERO);               // maxps xmm1(s3), [18068c1d0]
            sv = SseMin(sv, ONE);
            sv = Blend0xE(sv, BL);
            w3 = sv * q3;                                     // [rsp+0xb0]
            Dbg?.Invoke($"L3: q3 ({q3.X:R},{q3.Y:R},{q3.Z:R},{q3.W:R}) e3x {e3x:R} e3ref {e3ref:R} sv.x {sv.X:R} w3 ({w3.X:R},{w3.Y:R},{w3.Z:R})");
        }

        // ---- H. 2×2 LL SSIM (b00 = blk[0], b01 = blk[0x80], b10 = blk[0x800], b11 = blk[0x880]; r = ws+0x1500..0x1530),
        //      fused with I. level-4 forward (180443f0e–180444162)
        V4 q4;
        V4 l0op, l1op, l0ep, l1ep;
        {
            V4 b00 = c0e0, b01 = c2e0, b10 = c0e1, b11 = c2e1;
            V4 r00 = V4.Load(ws, RefL3), r01 = V4.Load(ws, RefL3 + 4), r10 = V4.Load(ws, RefL3 + 8), r11 = V4.Load(ws, RefL3 + 12);
            V4 rr00 = r00 * r00;
            V4 srrA = r01 * r01;  srrA = srrA + rr00;           // r01² + r00²
            V4 srA = r01 + r00;
            V4 sbrA = r00 * b00;
            V4 p01 = r01 * b01;   sbrA = p01 + sbrA;             // r01·b01 + r00·b00
            V4 sbA = b10 + b00;   sbA = sbA + b01;               // (b10 + b00) + b01
            V4 t = b00 * A2;
            l0op = b01 - t;                                      // level-4 row 0: o' = b01 − b00·2a   [rsp+0x80]
            V4 sb4 = sbA + b11;
            V4 srB = r10 + r11;
            V4 p11 = r11 * b11;
            V4 t2 = b10 * A2;
            l1op = b11 - t2;                                     // level-4 row 1: o' = b11 − b10·2a
            l0ep = l0op * B2 + b00;                              // e' = o'·2b + b00
            V4 bb00 = b00 * b00;
            V4 p10 = r10 * b10;
            l1ep = l1op * B2 + b10;
            V4 sbbA = b10 * b10;  sbbA = sbbA + bb00;            // b10² + b00²
            V4 bb01 = b01 * b01;  sbbA = sbbA + bb01;            // (b10² + b00²) + b01²
            V4 sbrB = p10 + sbrA;                                // r10·b10 + (r01·b01 + r00·b00)
            V4 sr4 = srB + srA;                                  // (r10 + r11) + (r01 + r00)
            V4 sbb4 = b11 * b11;  sbb4 = sbb4 + sbbA;            // b11² + …
            V4 rr10 = r10 * r10;
            V4 srr4 = r11 * r11;  srr4 = srr4 + rr10;  srr4 = srr4 + srrA; // (r11² + r10²) + (r01² + r00²)
            V4 sbr4 = p11 + sbrB;                                // r11·b11 + …
            V4 s = V4.Splat(Quarter);
            sb4 = sb4 * s; sr4 = sr4 * s; sbb4 = sbb4 * s; srr4 = srr4 * s; sbr4 = sbr4 * s;
            V4 vb = sbb4 - sb4 * sb4;
            V4 vr = srr4 - sr4 * sr4;
            V4 cov = sbr4 - sr4 * sb4;
            cov = SseMax(cov, ZERO);
            V4 fac = V4.Splat(sb4.W * Eighth);
            V4 num = (cov + cov) + C1;
            num = num * fac;
            vr = SseMax(vr, ZERO);
            V4 den = vr + C1;
            vb = SseMax(vb, ZERO);
            den = den + vb;                                      // (vr + C1) + vb
            q4 = Rcp(den) * num;
            q4 = (q4 + C2) * C3;                                 // unclamped here; clamped at 1804441aa
        }
        V4 w4, q4c;
        {
            // level-4 forward of the 2×2 (rows then columns, fused)
            V4 l0o = l0op * IK - l0ep * D;
            V4 l0e = l0o * E2 + l0ep * K;
            V4 l1o = l1op * IK - l1ep * D;
            V4 l1e = l1o * E2 + l1ep * K;
            V4 c0op = l0e * NA2 + l1e;                           // column 0: o' = (−2a)·v0 + v1
            V4 c0ep = c0op * B2 + l0e;
            V4 c1op = l1o - l0o * A2;                            // column 1: o' = v1 − v0·2a
            V4 c1ep = c1op * B2 + l0o;
            V4 c0o = c0op * IK - c0ep * D;
            V4 c0e = c0o * E2 + c0ep * K;
            c0o.Store(blk, 512);                                 // blk[0x800] = (8,0)
            c0e.Store(blk, 0);                                   // blk[0]     = (0,0)  (level-4 LL)
            V4 c1o = c1op * IK - c1ep * D;
            V4 c1e = c1o * E2 + c1ep * K;
            c1o.Store(blk, 544);                                 // blk[0x880] = (8,8)
            c1e.Store(blk, 32);                                  // blk[0x80]  = (0,8)
            V4 e4 = Abs(c1o) + Abs(c1e);                         // |(8,8)| + |(0,8)|
            e4 = Abs(c0o) + e4;                                  // |(8,0)| + …
            float e4ref = ws[E4Ref];
            float e4x = e4.X * Inv24;
            float d4 = e4x - e4ref;
            float s4 = RcpS(e4ref + P05);
            s4 = s4 * Eight;
            s4 = s4 * d4;
            q4c = SseMax(q4, ZERO);
            q4c = SseMin(q4c, ONE);
            s4 = s4 + One;
            V4 sv = SseMax(V4.Splat(s4), ZERO);
            sv = SseMin(sv, ONE);
            sv = Blend0xE(sv, BL);
            w4 = sv * q4c;
            Dbg?.Invoke($"L4: q4 ({q4.X:R},{q4.Y:R},{q4.Z:R},{q4.W:R}) e4x {e4x:R} e4ref {e4ref:R} s4 {s4:R} w4 ({w4.X:R},{w4.Y:R},{w4.Z:R})");
        }

        // ---- J. W_k = min(w_k.x, w_k.y, w_k.z) (shufps 0x4a / minps / movshdup / minss), W_5 (DC) = 0; ws[0x2580 + (k−1)·16] += W_k
        float W4 = MinXYZ(w4);
        float W3 = MinXYZ(w3);
        float W2 = MinXYZ(w2);
        float W1 = MinXYZ(w1);
        for (int l = 0; l < 4; l++)
        {
            ws[Wsum + l]      = W1 + ws[Wsum + l];
            ws[Wsum + 4 + l]  = W2 + ws[Wsum + 4 + l];
            ws[Wsum + 8 + l]  = W3 + ws[Wsum + 8 + l];
            ws[Wsum + 12 + l] = W4 + ws[Wsum + 12 + l];
        }

        // ---- K. ws[0x1580 + pos] += W_slot(r,c) · blk[pos], slot read from the u8 table at ws+0x25d0 + r·16 + c
        {
            Span<float> wtab = stackalloc float[5] { W1, W2, W3, W4, 0f };   // [rsp+0x90..0xd0], splat vectors
            ReadOnlySpan<byte> slots = MemoryMarshal.AsBytes(ws.AsSpan(SlotB >> 2, 64));
            for (int r = 0; r < 16; r++)
            {
                for (int c = 0; c < 16; c++)
                {
                    float w = wtab[slots[r * 16 + c]];
                    int pos = r * 64 + c * 4;
                    int ap = Acc + pos;
                    ws[ap]     = w * blk[pos]     + ws[ap];
                    ws[ap + 1] = w * blk[pos + 1] + ws[ap + 1];
                    ws[ap + 2] = w * blk[pos + 2] + ws[ap + 2];
                    ws[ap + 3] = w * blk[pos + 3] + ws[ap + 3];
                }
            }
        }

        // ---- L. return ≈ sqrt(W2·W1): x = W2·W1 (xmm1 = W2 min result, xmm0 = W1 min result — NOT q4c·w4);
        //      r = rsqrtss(x); y = x·r; res = (y·r + (−3)) · ((−0.5)·y); if (x == 0) res = 0
        {
            float x = W2 * W1;
            float rs = RsqrtS(x);                                // rsqrtss RAW
            float y = x * rs;
            float h = NegHalf * y;
            float t = y * rs;
            t = t + NegThree;
            t = t * h;
            return x == 0f ? 0f : t;                             // cmpeqss / andnps
        }
    }

    // ================================================================ FUN_180448020(ws, blk): gain normalise
    /// <summary>
    /// <c>s = Σ|blk|</c> per lane (accumulator initialised to 1e-5, per-row tree as in §5.3.3);
    /// <c>f = rcpss(s.x) · ws[0x26d0].x</c> (raw rcpss); <c>blk ·= (f, f, f, 1.0)</c> for all 256 vec4.
    /// rcx = ws (only ws[0x26d0] is read), rdx = blk (modified in place).
    /// </summary>
    public static void GainNormalise(float[] ws, float[] blk)
    {
        V4 acc = V4.Splat(Eps);                                  // xmm0 = 1e-5 ×4
        for (int r = 0; r < 16; r++)
        {
            int p = r * 64;
            V4 x2 = Abs(V4.Load(blk, p + 0)) + acc;                           // |b0| + acc
            V4 x0 = Abs(V4.Load(blk, p + 4));                                 // |b1|
            V4 x3 = Abs(V4.Load(blk, p + 8)) + x0;                            // |b2| + |b1|
            x3 = x3 + x2;
            x0 = Abs(V4.Load(blk, p + 12));                                   // |b3|
            x2 = Abs(V4.Load(blk, p + 16)) + x0;                              // |b4| + |b3|
            x0 = Abs(V4.Load(blk, p + 20)) + x2;                              // |b5| + …
            x0 = x0 + x3;
            x2 = Abs(V4.Load(blk, p + 24));                                   // |b6|
            x3 = Abs(V4.Load(blk, p + 28)) + x2;                              // |b7| + |b6|
            x2 = Abs(V4.Load(blk, p + 32)) + x3;                              // |b8| + …
            x3 = Abs(V4.Load(blk, p + 36)) + x2;                              // |b9| + …
            x3 = x3 + x0;
            x0 = Abs(V4.Load(blk, p + 40));                                   // |b10|
            x2 = Abs(V4.Load(blk, p + 44)) + x0;                              // |b11| + |b10|
            x0 = Abs(V4.Load(blk, p + 48)) + x2;                              // |b12| + …
            x2 = Abs(V4.Load(blk, p + 52)) + x0;                              // |b13| + …
            V4 x4 = Abs(V4.Load(blk, p + 56)) + x2;                           // |b14| + …
            x4 = x4 + x3;
            acc = Abs(V4.Load(blk, p + 60)) + x4;                             // |b15| + …
        }
        float f = RcpS(acc.X);                                   // rcpss RAW
        f = f * ws[SumAbs];
        V4 g = new(f, f, f, One);                                // shufps 0xc0; insertps lane 3 = 1.0
        for (int i = 0; i < 1024; i += 4)
        {
            V4 v = V4.Load(blk, i) * g;
            v.Store(blk, i);
        }
    }

    // ================================================================ FUN_180448250(out, blk, ref8): 8×8 LL SSIM
    /// <summary>
    /// SSIM-style quality vec4 between the level-1 LL of <paramref name="blk"/> (the (even,even) positions of the 16×16
    /// interleaved layout) and the 8×8 reference LL at <paramref name="ref8"/>[<paramref name="refOff"/>..] (row stride 32 floats).
    /// rcx = destination vec4 pointer (written, also returned in rax), rdx = blk, r8 = ref LL (ws+0x1000 at the call site).
    /// Writes the 4 result lanes to <paramref name="dst"/>[<paramref name="dstOff"/>..+3].
    /// </summary>
    public static void Ssim8x8(float[] blk, float[] ref8, int refOff, float[] dst, int dstOff)
    {
        Ssim8x8Core(blk, ref8, refOff, out V4 q);
        q.Store(dst, dstOff);
    }

    static void Ssim8x8Core(float[] blk, float[] ref8, int refOff, out V4 q)
    {
        V4 sb = ZERO, sr = ZERO, sbb = ZERO, srr = ZERO, sbr = ZERO;
        for (int i = 0; i < 8; i++)
        {
            int p = i * 128;                                      // blk row 2i; b_j at column 2j = +8j
            int rp = refOff + i * 32;                             // ref row i; r_j at +4j
            V4 b0 = V4.Load(blk, p), b1 = V4.Load(blk, p + 8), b2 = V4.Load(blk, p + 16), b3 = V4.Load(blk, p + 24);
            V4 r0 = V4.Load(ref8, rp), r1 = V4.Load(ref8, rp + 4), r2 = V4.Load(ref8, rp + 8), r3 = V4.Load(ref8, rp + 12);
            sb = sb + b0;
            sr = sr + r0;
            V4 p0 = r0 * b0;
            V4 bb0 = b0 * b0;  bb0 = bb0 + sbb;
            V4 rr0 = r0 * r0;  rr0 = rr0 + srr;
            p0 = p0 + sbr;
            V4 bb1 = b1 * b1;
            V4 rr1 = r1 * r1;
            V4 p1 = r1 * b1;
            V4 tb = b1 + b2;   sb = tb + sb;                      // (b1 + b2) + (Sb + b0)
            V4 tr = r1 + r2;   sr = tr + sr;
            V4 p2 = r2 * b2;
            V4 bb2 = b2 * b2;  bb2 = bb2 + bb1;  bb2 = bb2 + bb0;
            V4 rr2 = r2 * r2;  rr2 = rr2 + rr1;  rr2 = rr2 + rr0;
            p2 = p2 + p1;      p2 = p2 + p0;
            V4 bb3 = b3 * b3;
            V4 rr3 = r3 * r3;
            V4 p3 = r3 * b3;
            V4 b4 = V4.Load(blk, p + 32), r4 = V4.Load(ref8, rp + 16);
            V4 tb4 = b3 + b4;
            V4 tr4 = r3 + r4;
            V4 p4 = r4 * b4;
            V4 bb4 = b4 * b4;  bb4 = bb4 + bb3;
            V4 rr4 = r4 * r4;  rr4 = rr4 + rr3;
            p4 = p4 + p3;
            V4 b5 = V4.Load(blk, p + 40), r5 = V4.Load(ref8, rp + 20);
            tb4 = tb4 + b5;    sb = tb4 + sb;                     // ((b3 + b4) + b5) + Sb
            tr4 = tr4 + r5;    sr = tr4 + sr;
            V4 p5 = r5 * b5;
            V4 bb5 = b5 * b5;  bb5 = bb5 + bb4;  bb5 = bb5 + bb2;
            V4 rr5 = r5 * r5;  rr5 = rr5 + rr4;  rr5 = rr5 + rr2;
            p5 = p5 + p4;      p5 = p5 + p2;
            V4 b6 = V4.Load(blk, p + 48), r6 = V4.Load(ref8, rp + 24);
            V4 bb6 = b6 * b6;
            V4 rr6 = r6 * r6;
            V4 p6 = r6 * b6;
            V4 b7 = V4.Load(blk, p + 56), r7 = V4.Load(ref8, rp + 28);
            V4 tb6 = b6 + b7;  sb = tb6 + sb;                     // (b6 + b7) + Sb
            V4 tr6 = r6 + r7;  sr = tr6 + sr;
            V4 p7 = r7 * b7;
            V4 bb7 = b7 * b7;  bb7 = bb7 + bb6;  sbb = bb7 + bb5;
            V4 rr7 = r7 * r7;  rr7 = rr7 + rr6;  srr = rr7 + rr5;
            p7 = p7 + p6;      sbr = p7 + p5;
        }
        V4 s = V4.Splat(Inv64);
        sb = sb * s; sr = sr * s; sbb = sbb * s; srr = srr * s; sbr = sbr * s;
        V4 vb = sbb - sb * sb;  vb = SseMax(vb, ZERO);
        V4 sbsr = sb * sr;
        V4 vr = srr - sr * sr;  vr = SseMax(vr, ZERO);
        V4 cov = sbr - sbsr;    cov = SseMax(cov, ZERO);
        V4 fac = V4.Splat(sb.W * Half);                           // shufps 0xe7; mulss 0.5; shufps 0
        V4 num = (cov + cov) + C1;
        num = num * fac;
        V4 den = (vb + C1) + vr;
        V4 r = Rcp(den) * num;                                    // rcpps RAW
        r = (r + C2) * C3;
        Dbg?.Invoke($"L2 stats: sb ({sb.X:R},{sb.Y:R},{sb.Z:R},{sb.W:R}) vb ({vb.X:R},{vb.Y:R},{vb.Z:R},{vb.W:R}) vr ({vr.X:R},{vr.Y:R},{vr.Z:R},{vr.W:R}) cov ({cov.X:R},{cov.Y:R},{cov.Z:R},{cov.W:R}) rawq ({r.X:R},{r.Y:R},{r.Z:R},{r.W:R})");
        q = SseMin(SseMax(r, ZERO), ONE);
    }

    // ================================================================ SSE helpers
    /// <summary>Lane-wise Intel <c>maxps</c>: <c>dst = (dst &gt; src) ? dst : src</c> (returns the second operand on NaN / equal).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float SseMax(float a, float b) => a > b ? a : b;
    /// <summary>Lane-wise Intel <c>minps</c>: <c>dst = (dst &lt; src) ? dst : src</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float SseMin(float a, float b) => a < b ? a : b;
    static V4 SseMax(V4 a, V4 b) => new(SseMax(a.X, b.X), SseMax(a.Y, b.Y), SseMax(a.Z, b.Z), SseMax(a.W, b.W));
    static V4 SseMin(V4 a, V4 b) => new(SseMin(a.X, b.X), SseMin(a.Y, b.Y), SseMin(a.Z, b.Z), SseMin(a.W, b.W));

    /// <summary><c>andps</c> with 0x7fffffff (180682600): clear the sign bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float AbsBits(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & 0x7fffffff);
    static V4 Abs(V4 v) => new(AbsBits(v.X), AbsBits(v.Y), AbsBits(v.Z), AbsBits(v.W));

    /// <summary><c>rcpps</c> (4 lanes, raw host approximation).</summary>
    static V4 Rcp(V4 v)
    {
        Vector128<float> r = Sse.Reciprocal(Vector128.Create(v.X, v.Y, v.Z, v.W));
        return new(r.GetElement(0), r.GetElement(1), r.GetElement(2), r.GetElement(3));
    }
    /// <summary><c>rcpss</c> (raw host approximation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float RcpS(float x) => Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    /// <summary><c>rsqrtss</c> (raw host approximation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float RsqrtS(float x) => Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();

    /// <summary><c>blendps dst, src, 0xe</c>: lane 0 from <paramref name="a"/>, lanes 1..3 from <paramref name="b"/>.</summary>
    static V4 Blend0xE(V4 a, V4 b) => new(a.X, b.Y, b.Z, b.W);

    /// <summary>
    /// <c>shufps t,w,0x4a</c> → (w.z, w.z, w.x, w.y); <c>minps t,w</c>; <c>movshdup u,t</c>; <c>minss t,u</c>: lane 0 =
    /// <c>SseMin(SseMin(w.z, w.x), SseMin(w.z, w.y))</c>.
    /// </summary>
    static float MinXYZ(V4 w)
    {
        float l0 = SseMin(w.Z, w.X);
        float l1 = SseMin(w.Z, w.Y);
        return SseMin(l0, l1);
    }

    /// <summary>One XMM register of four IEEE singles; every operator is one lane-wise C# float op (never fused).</summary>
    internal readonly struct V4
    {
        public readonly float X, Y, Z, W;
        public V4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V4 Load(float[] a, int i) => new(a[i], a[i + 1], a[i + 2], a[i + 3]);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(float[] a, int i) { a[i] = X; a[i + 1] = Y; a[i + 2] = Z; a[i + 3] = W; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static V4 Splat(float s) => new(s, s, s, s);

        public static V4 operator +(V4 a, V4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        public static V4 operator -(V4 a, V4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        public static V4 operator *(V4 a, V4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);
        /// <summary>Multiply by a splat constant (mulps with a ×4 constant).</summary>
        public static V4 operator *(V4 a, float s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);
    }
}
