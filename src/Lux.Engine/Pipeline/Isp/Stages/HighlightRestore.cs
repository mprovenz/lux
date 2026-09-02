using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::RestoreHighlightsBayer` (dispatcher `1803c9b60`; phase kernels `1803c9e90/…cc0b0/…ce320/…d05a0` for the four
/// red positions; source row cache `FUN_1803d2910`, 3×3-max row `FUN_1803d2d20`) = `HighlightRestore:default` on the
/// Bayer-ushort domain. Per pixel: copied unless the 3×3 max ≥ (ushort)(int)(white·0.985); otherwise the missing
/// channels are estimated from the neighbourhood (green-guided Laplacian interpolation in 10.10 fixed point, weighted
/// by 1/(|ΔG|/(white−black) + 1/1024)), the white-balanced normalised triple is pulled toward the mean of the
/// unclipped channels by the per-channel clip weight ((v·n − 0.85)·6.667), toward grey by (Σw − 1)·(alignment with
/// the chroma direction) and toward the max channel by (Σw − 2); the site's channel is written back. Transcribed from
/// the assembly of kernel 0 (all SSE approximations — rcpps/rcpss/rsqrtps/rsqrtss — kept).
/// </summary>
public static class HighlightRestoreKernel
{
    public const int Pad = 9, Align = 2;   // live slot meta (cp.dll's live ISP-stage listing: slot 2 docall 4168d0 pad 9 align 2); the kernel itself is pointwise, the pad only sets the runner geometry

    static float RcpNr(float x) { var r = Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar(); return ((1f - x * r) * r + r) * 1f; }
    static float Rcp(float x) => Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();
    static float Rsqrt(float x) => Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
    static float MaxSs(float a, float b) => a > b ? a : b;   // maxss dst,src → dst > src ? dst : src
    static float MinSs(float a, float b) => a < b ? a : b;

    sealed class Frame
    {
        public float RR, RG, RB, RWB, SR, SG, SB, KRG, KBG, RatioGR, RatioGB, UR, UG, UB, ScaleR, ScaleG, ScaleB, Black, NegBlack;
        public float[] Thr85 = new float[4], K667 = new float[4];
        public int KGR, KGB, Floor; public ushort Thr;
    }

    static Frame Setup(float[] n, float black, float white)
    {
        var f = new Frame();
        // rcpps + Newton on (nG, nB) and (nR, white−black), ×1.0
        var v = Vector128.Create(n[1], n[2], 0f, 0f); var r = Sse.Reciprocal(v);
        var nr = Sse.Multiply(Sse.Add(Sse.Multiply(Sse.Subtract(Vector128.Create(1f), Sse.Multiply(v, r)), r), r), Vector128.Create(1f));
        f.RG = nr[0]; f.RB = nr[1];
        float wb = white - black;
        v = Vector128.Create(n[0], wb, 0f, 0f); r = Sse.Reciprocal(v);
        nr = Sse.Multiply(Sse.Add(Sse.Multiply(Sse.Subtract(Vector128.Create(1f), Sse.Multiply(v, r)), r), r), Vector128.Create(1f));
        f.RR = nr[0]; f.RWB = nr[1];
        f.SR = f.RR * f.RWB; f.SG = f.RG * f.RWB; f.SB = f.RWB * f.RB;
        f.Floor = (int)(-0.04f * white);
        f.KRG = f.RR * n[1]; f.KBG = f.RB * n[1];
        f.KGR = (int)(f.KRG * 1024f); f.KGB = (int)(1024f * f.KBG);
        float m = MinSs(MinSs(f.RR, f.RG), f.RB) * 0.9f;
        float dR = f.RR - m, dG = f.RG - m, dB = f.RB - m;
        float len2 = dB * dB + (dR * dR + dG * dG);
        float rs = Rsqrt(len2);
        float inv = (rs * -0.5f) * (len2 * rs * rs + -3f);
        f.UR = dR * inv; f.UG = dG * inv; f.UB = dB * inv;
        f.RatioGB = f.RG / f.RB; f.RatioGR = f.RG / f.RR;
        f.Thr85 = new[] { f.RR * 0.85f, f.RG * 0.85f, f.RB * 0.85f, 0f };
        f.K667 = new[] { n[0] * 6.666666507720947f, n[1] * 6.666666507720947f, 6.666666507720947f * n[2], 0f };
        f.ScaleR = n[0] * wb; f.ScaleG = n[1] * wb; f.ScaleB = wb * n[2];
        f.Thr = (ushort)(int)(white * 0.9850000143051147f);
        f.Black = black; f.NegBlack = -black;
        return f;
    }

    /// <summary>Green estimate (×4, 16-bit) at an R/B site from its same-colour ±2 neighbours and the four greens.</summary>
    static int EstG(Func<int, int, int> P, int cx, int cy, int k, int floor)
    {
        int c = P(cx, cy);
        int prodH = ((2 * c - P(cx - 2, cy)) - P(cx + 2, cy)) * k, prodV = ((2 * c - P(cx, cy - 2)) - P(cx, cy + 2)) * k;
        int lapH = prodH >> 10, lapV = prodV >> 10;
        int absH = (lapH ^ (prodH >> 31)) + (int)((uint)prodH >> 31), absV = (lapV ^ (prodV >> 31)) + (int)((uint)prodV >> 31);
        int gl = P(cx - 1, cy), gr = P(cx + 1, cy), gu = P(cx, cy - 1), gd = P(cx, cy + 1);
        int dH = gr - gl, dV = gd - gu;
        int gradH = ((dH ^ (dH >> 31)) + (int)((uint)dH >> 31)) + absH, gradV = ((dV ^ (dV >> 31)) + (int)((uint)dV >> 31)) + absV;
        int estH = Math.Max(lapH, floor) + (gr + gl) * 2, estV = Math.Max(lapV, floor) + (gd + gu) * 2;
        int est = gradH <= gradV ? estH : estV;
        return est <= 0 ? 0 : est & 0xffff;
    }

    static float Abs(float x) => MathF.Abs(x);

    /// <summary>The colour-space part shared by the four sites: v = (R, G, B, 0) white-balanced & normalised.</summary>
    static float Tail(Frame f, float vR, float vG, float vB, int lane)
    {
        // v += −black (all lanes), × (sR, sG, sB, 0)
        float v0 = (vR + f.NegBlack) * f.SR, v1 = (vG + f.NegBlack) * f.SG, v2 = (vB + f.NegBlack) * f.SB, v3 = (0f + f.NegBlack) * 0f;
        // clip weights
        float w0 = (v0 - f.Thr85[0]) * f.K667[0], w1 = (v1 - f.Thr85[1]) * f.K667[1], w2 = (v2 - f.Thr85[2]) * f.K667[2], w3 = (v3 - 0f) * 0f;
        w0 = MinSs(MaxSs(w0, 0f), 1f); w1 = MinSs(MaxSs(w1, 0f), 1f); w2 = MinSs(MaxSs(w2, 0f), 1f); w3 = MinSs(MaxSs(w3, 0f), 1f);
        float o0 = 1f - w0, o1 = 1f - w1, o2 = 1f - w2;
        float sumW = (0f + w1) + (w2 + w0);
        float q0 = o0 * v0, q1 = o1 * v1, q2 = o2 * v2;
        float sumQ = (0f + q1) + (q2 + q0);
        float mean = Rcp(3.0000100135803223f - sumW) * sumQ;
        float a0 = (mean - v0) * w0 + v0, a1 = (mean - v1) * w1 + v1, a2 = (mean - v2) * w2 + v2, a3 = (mean - v3) * w3 + v3;
        a0 = MaxSs(a0, v0); a1 = MaxSs(a1, v1); a2 = MaxSs(a2, v2); a3 = MaxSs(a3, v3);
        float t = MinSs(sumW, 1f);
        float b0 = t * (a0 - v0) + v0, b1 = t * (a1 - v1) + v1, b2 = t * (a2 - v2) + v2, b3 = t * (a3 - v3) + v3;
        // maxc and Σ over (b0,b1,b2,0)
        float maxc = MaxSs(MaxSs(b0, b2), MaxSs(b1, 0f));
        float sumB = (0f + b1) + (b2 + b0);
        // chroma alignment
        float c0 = v0 + -0.8999999761581421f, c1 = v1 + -0.8999999761581421f, c2 = v2 + -0.8999999761581421f, c3 = v3 + -0.8999999761581421f;
        float s0 = c0 * c0, s1 = c1 * c1, s2 = c2 * c2;
        float len = (0f + s1) + (s2 + s0);
        float rs = Rsqrt(len);
        float p0 = (rs * c0) * f.UR, p1 = (rs * c1) * f.UG, p2 = (rs * c2) * f.UB, p3 = c3 * 0f;
        float dot = (p3 + p1) + (p2 + p0);
        dot = MaxSs(dot, 0f);
        float k1 = MinSs(MaxSs(sumW + -1f, 0f), 1f) * dot;
        float mean3 = sumB * 0.3333333432674408f;
        float d0 = k1 * (mean3 - b0) + b0, d1 = k1 * (mean3 - b1) + b1, d2 = k1 * (mean3 - b2) + b2;
        float k2 = MaxSs(sumW + -2f, 0f);
        float e0 = k2 * (maxc - d0) + d0, e1 = k2 * (maxc - d1) + d1, e2 = k2 * (maxc - d2) + d2;
        return lane == 0 ? e0 : lane == 1 ? e1 : e2;
    }

    /// <summary>R or B site (block 1 / block 4): own channel + green estimate + the other colour from the four
    /// diagonals.</summary>
    static ushort SiteRB(Frame f, Func<int, int, int> P, int x, int y, bool isR)
    {
        int kOwn = isR ? f.KGR : f.KGB, kDiag = isR ? f.KGB : f.KGR;
        float diagToG = isR ? f.KBG : f.KRG, ratio = isR ? f.RatioGB : f.RatioGR;
        float own = (float)P(x, y);
        float gEst = (float)EstG(P, x, y, kOwn, f.Floor) * 0.25f;
        var dx = new[] { -1, 1, -1, 1 }; var dy = new[] { -1, -1, 1, 1 };
        var gD = new float[4]; var c = new float[4]; var w = new float[4];
        for (int i = 0; i < 4; i++)
        {
            gD[i] = (float)EstG(P, x + dx[i], y + dy[i], kDiag, f.Floor) * 0.25f;
            c[i] = (float)P(x + dx[i], y + dy[i]) * diagToG - gD[i];
        }
        var wv = Sse.Reciprocal(Vector128.Create(Abs(gD[0] - gEst) * f.RWB + 0.009765625f, Abs(gD[1] - gEst) * f.RWB + 0.009765625f, Abs(gD[2] - gEst) * f.RWB + 0.009765625f, Abs(gD[3] - gEst) * f.RWB + 0.009765625f));
        for (int i = 0; i < 4; i++) w[i] = wv[i];
        float sumW = (w[3] + w[1]) + (w[2] + w[0]);
        float invW = Rcp(sumW);
        float p0 = c[0] * w[0], p1 = c[1] * w[1], p2 = c[2] * w[2], p3 = c[3] * w[3];
        float sumP = (p3 + p1) + (p2 + p0);
        float other = (sumP * invW + gEst) * ratio;
        float r = isR ? Tail(f, own, gEst, other, 0) : Tail(f, other, gEst, own, 2);
        return (ushort)(int)(r * (isR ? f.ScaleR : f.ScaleB) + f.Black);
    }

    /// <summary>G site (blocks 2/3): R from the two R neighbours, B from the two B neighbours.</summary>
    static ushort SiteG(Frame f, Func<int, int, int> P, int x, int y, bool rHorizontal)
    {
        float g = (float)P(x, y);
        float Est(int cx, int cy, int k, float toG, out float wgt)
        {
            int e = EstG(P, cx, cy, k, f.Floor);
            float ge = (float)(ushort)(int)((float)e * 0.25f);
            wgt = Rcp(Abs(g - ge) * f.RWB + 0.009765625f);
            return ((float)P(cx, cy) * toG - ge) * wgt;
        }
        int rdx = rHorizontal ? 1 : 0, rdy = rHorizontal ? 0 : 1;
        float pRa = Est(x - rdx, y - rdy, f.KGR, f.KRG, out float wRa), pRb = Est(x + rdx, y + rdy, f.KGR, f.KRG, out float wRb);
        float rEst = ((pRb + pRa) * Rcp(wRb + wRa) + g) * f.RatioGR;
        float pBa = Est(x - rdy, y - rdx, f.KGB, f.KBG, out float wBa), pBb = Est(x + rdy, y + rdx, f.KGB, f.KBG, out float wBb);
        float bEst = ((pBb + pBa) * Rcp(wBb + wBa) + g) * f.RatioGB;
        float r = Tail(f, rEst, g, bEst, 1);
        return (ushort)(int)(r * f.ScaleG + f.Black);
    }

    /// <summary>Process <paramref name="rect"/> (view coords) of the ushort source; reads clamp by row pairs and
    /// replicate by column pairs at <paramref name="srcRect"/>.</summary>
    public static void Run(ushort[] src, int srcStride, int srcOffset, RectI srcRect, ushort[] dst, int dstStride, int dstOffset, RectI rect, int redX, int redY, float[] neutral, float black, float white)
    {
        var f = Setup(neutral, black, white);
        int Row(int y) { int half = Math.Clamp(y >> 1, srcRect.Y0 >> 1, (srcRect.Y1 >> 1) - 1); return (y & 1) + half * 2; }
        int Col(int x)
        {
            if (x < srcRect.X0) return srcRect.X0 + ((x - srcRect.X0) & 1);
            if (x >= srcRect.X1) return srcRect.X1 - 2 + ((x - srcRect.X1) & 1);
            return x;
        }
        int P(int x, int y) => src[srcOffset + Row(y) * srcStride + Col(x)];
        int rx = redX & 1, ry = redY & 1;
        for (int y = rect.Y0; y < rect.Y1; y++)
            for (int x = rect.X0; x < rect.X1; x++)
            {
                int raw = P(x, y);
                int m = 0;
                for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) { int v = P(x + i, y + j); if (v > m) m = v; }
                ushort o;
                if (m < f.Thr) o = (ushort)raw;
                else
                {
                    bool rowR = (y & 1) == ry, colR = (x & 1) == rx;
                    if (rowR && colR) o = SiteRB(f, P, x, y, true);
                    else if (!rowR && !colR) o = SiteRB(f, P, x, y, false);
                    else o = SiteG(f, P, x, y, rowR);   // G in the R row: R neighbours horizontal
                }
                dst[dstOffset + y * dstStride + x] = o;
            }
    }
}

/// <summary>Bayer-ushort stage `HighlightRestore:default` (setter pad 1 / align 1; lambda passes the payload view,
/// the red position, the Stats neutral and the sensor black/white).</summary>
public sealed class HighlightRestoreStage : IStage
{
    public StageName Stage => StageName.HighlightRestore;
    public string TypeString => "default";
    public StageMeta Meta => new(HighlightRestoreKernel.Pad, HighlightRestoreKernel.Align, 1f);
    public void Apply(IspPayload p)
    {
        var src = p.Raw ?? throw new InvalidOperationException("HighlightRestore needs the Bayer ushort source");
        var red = p.Context.Module.SensorBayerRedOverride ?? throw new InvalidOperationException("HighlightRestore needs the sensor red position");
        var noise = p.Frame.Noise ?? throw new InvalidOperationException("HighlightRestore needs the sensor black/white levels");
        var abs = p.ToAbsolute(p.IntRect).Intersect(src.Rect);
        int srcOffset = src.Offset + (abs.Y0 - src.Rect.Y0) * src.Stride + (abs.X0 - src.Rect.X0);
        var srcRect = new RectI(src.Rect.X0 - abs.X0, src.Rect.Y0 - abs.Y0, src.Rect.X1 - abs.X0, src.Rect.Y1 - abs.Y0);
        var dst = new Image<ushort>(abs);
        HighlightRestoreKernel.Run(src.Data, src.Stride, srcOffset, srcRect, dst.Data, dst.Stride, 0, new RectI(0, 0, abs.Width, abs.Height), red.X, red.Y, p.Stats.Neutral, float.IsNaN(p.Frame.FrameBlack) ? noise.Black : p.Frame.FrameBlack, noise.White);   // setHighlightRestore::lambda_31 L65-67: frame black/white floats (Stats+0x198 → +4/+8)
        p.Raw = dst;
    }
}
