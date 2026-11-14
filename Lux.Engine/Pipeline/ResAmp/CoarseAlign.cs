using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static Lux.Engine.Pipeline.ResAmp.SseOps;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>Coarse alignment steps (2)–(5): the 16×16 SAD full search on the u8 phase-0 map (`mpsadbw`/`phminposuw`), the 5×5 1/3-px sub-phase
/// search, the 3×3 quadratic sub-step and the 16.16 bilinear 16×16 patch from the module's blurred image.</summary>
internal static class CoarseAlign
{
    /// <summary>Per-grid-point coarse alignment of one module record. Returns false when the match is rejected (grid point invalidated).
    /// On success `fx, fy` are the matched position in phase-map px (mask units) and `blk` holds the 16×16 vec4 patch (row stride 64 floats).</summary>
    public static bool Align(ModuleRecord rec, int gmx, int gmy, float invScale, float scale, ReadOnlySpan<byte> patch, float[] blk, out float fx, out float fy)
    {
        fx = fy = 0f;
        var mask0 = rec.Ph[0];
        int xs = Cvtt((float)(gmx - rec.MinX) * invScale + 0.5f) - 16;
        int ys = Cvtt((float)(gmy - rec.MinY) * invScale + 0.5f) - 16;
        xs = Math.Min(Math.Max(xs, mask0.RX0), mask0.RX1 - 32);
        ys = Math.Min(Math.Max(ys, mask0.RY0), mask0.RY1 - 32);
        // (2) 16×16 full search: dy, dx ∈ [0,16)
        uint best = 0xffffffff, bestVal = 0xffffffff; int bdx = 0, bdy = 0;
        Span<int> sum = stackalloc int[8];
        for (int sdy = 0; sdy < 16; sdy++)
        {
            int rowp = mask0.Idx(xs, ys + sdy);
            // pass 1: dx 0..7
            Sad8(mask0.Data, rowp, mask0.Stride, patch, sum);
            MinPos(sum, out uint v, out int i);
            if (v < best) { bestVal = v; bdy = sdy; }
            // pass 2: dx 8..15
            Sad8(mask0.Data, rowp + 8, mask0.Stride, patch, sum);
            MinPos(sum, out uint v2, out int i2);
            if (v < best) { bdx = i; best = v; }
            if (v2 < best) { bdx = i2 + 8; bdy = sdy; bestVal = v2; best = v2; }
        }
        int mx = xs + 8 + bdx, my = ys + 8 + bdy;
        if (!(mx > mask0.RX0 + 8 && mx + 1 < mask0.RX1 - 7 && my > mask0.RY0 + 8 && my + 1 < mask0.RY1 - 7)) return false;
        // (3) 5×5 sub-phase search
        Span<int> score = stackalloc int[49]; score.Fill(-1);
        score[3 * 7 + 3] = (int)bestVal;
        int bestS = (int)bestVal, bdx3 = 0, bdy3 = 0;
        for (int dy3 = -2; dy3 <= 2; dy3++)
            for (int dx3 = -2; dx3 <= 2; dx3++)
            {
                if ((dx3 | dy3) == 0) continue;
                int py = dy3 >> 31, phy = ((dy3 >> 31) & 3) + dy3, px = dx3 >> 31, phx = ((dx3 >> 31) & 3) + dx3;
                var mk = rec.Ph[phx + 3 * phy];
                int q = mk.Idx(mx - 8 + px, my - 8 + py);
                int sad = Sad16(mk.Data, q, mk.Stride, patch);
                score[(dy3 + 3) * 7 + (dx3 + 3)] = sad;
                if (sad < bestS) { bdx3 = dx3; bdy3 = dy3; }
                if (sad <= bestS) bestS = sad;
            }
        // 3×3 neighbourhood
        Span<int> S = stackalloc int[9];
        for (int rr = -1; rr <= 1; rr++)
            for (int cc = -1; cc <= 1; cc++)
            {
                int R = bdy3 + 3 + rr, C = bdx3 + 3 + cc;
                int s = score[R * 7 + C];
                if (s < 0)
                {
                    int k = (C % 3) + 3 * (R % 3);
                    var mk = rec.Ph[k];
                    int q = mk.Idx(C / 3 + (mx - 9), R / 3 + (my - 9));
                    s = Sad16(mk.Data, q, mk.Stride, patch);
                }
                S[(rr + 1) * 3 + (cc + 1)] = s;
            }
        // (4) quadratic sub-step
        int ecx = ((S[6] + S[2]) << 2) + (S[8] + S[0]) * 4 - (S[4] << 4);
        int A = ecx + 8 * ((S[5] + S[3]) - S[1] - S[7]); if (A < 0) A = 0; float fA = (float)A;
        int B = ecx + 8 * ((S[1] - (S[5] + S[3])) + S[7]); if (B < 0) B = 0; float fB = (float)B;
        float fC = (float)(4 * (S[8] + S[0]) - 4 * (S[6] + S[2]));
        float fAB = fB * fA; float fCC = fC * fC;
        float fCp = (0.0f < fAB - fCC) ? fC : 0.0f;
        float det = fAB - fCp * fCp;
        float dx = 0f, dy = 0f;
        if (det != 0.0f)
        {
            float Gx = (float)(((S[5] - S[3]) << 2) - 2 * S[0] + 2 * S[8] + 2 * (S[2] - S[6]));
            float Gy = (float)((2 * S[8] - 2 * S[0]) + 2 * (S[6] - S[2]) + 4 * (S[7] - S[1]));
            float t1 = fCp * Gx - fA * Gy;
            float t2 = fCp * Gy - fB * Gx;
            float inv = 1.0f / det;
            dx = t2 * inv; dy = inv * t1;
            if (!(MathF.Abs(dx) < 1.0f && MathF.Abs(dy) < 1.0f)) { dx = 0f; dy = 0f; }
        }
        float third = F(0x3eaaaaab);
        float phxf = (float)(((bdx3 >> 31) & 3) + bdx3), fx0 = (float)(mx + (bdx3 >> 31));
        float phyf = (float)(((bdy3 >> 31) & 3) + bdy3), fy0 = (float)(my + (bdy3 >> 31));
        fx = (dx + phxf) * third + fx0;
        fy = (dy + phyf) * third + fy0;
        // (5) 16×16 bilinear from the blurred module image, 16.16, no clamping
        float f16 = scale * 65536.0f; int step = Cvtt(f16); float neg8 = F(0xc1000000), inv16 = F(0x37800000);
        int y16 = Cvtt((fy + neg8) * f16); int x16s = Cvtt((fx + neg8) * f16);
        var bl = rec.Blur; var D = bl.Data;
        for (int j = 0; j < 16; j++)
        {
            int iy = y16 >> 16; float wy = (float)(y16 & 0xffff) * inv16;
            int row0 = bl.Idx(0, iy), row1 = bl.Idx(0, iy + 1);
            int x16 = x16s;
            for (int i = 0; i < 16; i++)
            {
                int ix = x16 >> 16; float wx = (float)(x16 & 0xffff) * inv16;
                int p00 = row0 + ix * 4, p01 = p00 + 4, p10 = row1 + ix * 4, p11 = p10 + 4;
                // the four lanes as one Vector128 (each lane its scalar steps)
                var d00 = Vector128.Create((ReadOnlySpan<float>)D.AsSpan(p00, 4)); var d01 = Vector128.Create((ReadOnlySpan<float>)D.AsSpan(p01, 4));
                var d10 = Vector128.Create((ReadOnlySpan<float>)D.AsSpan(p10, 4)); var d11 = Vector128.Create((ReadOnlySpan<float>)D.AsSpan(p11, 4));
                var WY = Vector128.Create(wy);
                var av = (d10 - d00) * WY + d00;
                var uv = d01 - av;
                var tv = (d11 - d01) * WY;
                uv = uv + tv; uv = uv * Vector128.Create(wx);
                (uv + av).CopyTo(blk.AsSpan((j * 16 + i) * 4, 4));
                x16 += step;
            }
            y16 += step;
        }
        return true;
    }

    /// <summary>The mpsadbw accumulation: sum[k] = Σ_{r&lt;16} Σ_{j&lt;16} |M[r][k+j] − P[r][j]| for k = 0..7 (never saturates: ≤ 65280).</summary>
    static void Sad8(byte[] m, int rowp, int stride, ReadOnlySpan<byte> patch, Span<int> sum)
    {
        sum.Clear();
        if (Sse2.IsSupported)
        {
            // integer sums of absolute byte differences — any order gives the same value; psadbw per 16-byte row
            ref byte mb0 = ref MemoryMarshal.GetArrayDataReference(m); ref byte pb0 = ref MemoryMarshal.GetReference(patch);
            for (int r = 0; r < 16; r++)
            {
                var p = Vector128.LoadUnsafe(ref pb0, (nuint)(r * 16));
                nuint mb = (nuint)(rowp + r * stride);
                for (int k = 0; k < 8; k++)
                {
                    var d = Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref mb0, mb + (nuint)k), p).AsUInt64();
                    sum[k] += (int)(d.GetElement(0) + d.GetElement(1));
                }
            }
            return;
        }
        for (int r = 0; r < 16; r++)
        {
            int mb = rowp + r * stride, pb = r * 16;
            for (int k = 0; k < 8; k++)
            {
                int s = 0;
                for (int j = 0; j < 16; j++) s += Math.Abs(m[mb + k + j] - patch[pb + j]);
                sum[k] += s;
            }
        }
    }
    /// <summary>`phminposuw`: minimum u16 and its lowest index.</summary>
    static void MinPos(ReadOnlySpan<int> sum, out uint v, out int idx)
    {
        v = 0xffff; idx = 0;
        for (int k = 0; k < 8; k++) { uint s = (uint)Math.Min(sum[k], 65535); if (s < v) { v = s; idx = k; } }
    }
    /// <summary>|P − M| SAD over 16×16 (per byte max − min, unsigned saturating 16-bit lane sums, then horizontal adds; exact).</summary>
    static int Sad16(byte[] m, int q, int stride, ReadOnlySpan<byte> patch)
    {
        int s = 0;
        if (Sse2.IsSupported)
        {
            ref byte mb0 = ref MemoryMarshal.GetArrayDataReference(m); ref byte pb0 = ref MemoryMarshal.GetReference(patch);
            var acc = Vector128<ulong>.Zero;
            for (int r = 0; r < 16; r++)
                acc += Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref pb0, (nuint)(r * 16)), Vector128.LoadUnsafe(ref mb0, (nuint)(q + r * stride))).AsUInt64();
            return (int)(acc.GetElement(0) + acc.GetElement(1));
        }
        for (int r = 0; r < 16; r++)
        {
            int mb = q + r * stride, pb = r * 16;
            for (int j = 0; j < 16; j++) s += Math.Abs(patch[pb + j] - m[mb + j]);
        }
        return s;
    }
}
