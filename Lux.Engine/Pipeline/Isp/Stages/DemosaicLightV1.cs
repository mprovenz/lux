using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::Internal::A::DemosaickLightV1&lt;rx,ry&gt;` (dispatcher `1803a2400`, tile lambda `1803a57f0` for &lt;1,0&gt;,
/// row functions `FUN_1803a3d50` (source rows × per-site neutral), `FUN_1803a34d0` (guide planes) and
/// `FUN_1803a5250` (colour-difference plane)). Halide-style pipeline, ported plane by plane with the binary's
/// float operation order and SSE `rcpss/rcpps` approximations (`Sse.Reciprocal*`, i.e. this CPU's table — Lumen's
/// own output is CPU-dependent at that level):
///   S(x,y)  = src(clamp-by-parity) · neutral[site]                       (FUN_1803a3d50)
///   C       = 5×5 filter of S at G sites (weights 56, 6, −4, −2, 1; /64)   (FUN_1803a34d0 inner)
///   B       = C at G sites; at R/B sites the gradient-inverse blend of C±1 minus the S mid-points, eps₂
///   A       = B at R/B sites; at G sites the refined blend of (B − S) differences, eps₂
///   D       = raw − A (raw un-scaled; replicated by parity outside the source) (FUN_1803a5250)
///   out     = A + directional gradient-inverse blend of D (eps₁), alpha 1  (tile lambda)
/// eps₁ = 0.009765625·max(neutral) (`DAT_1806d4f60`), eps₂ = 0.0009765625·max(neutral) (`DAT_1806d4f58`).
/// </summary>
public static class DemosaicLightV1
{
    public const float Eps1Scale = 0.009765625f, Eps2Scale = 0.0009765625f;
    private const float K56 = 56f, K6 = 6f, Km4 = -4f, K1_64 = 0.015625f, Half = 0.5f;

    private static float Rcp(float x) => Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar() : 1f / x;
    private static Vector128<float> Rcp4(Vector128<float> v) => Sse.IsSupported ? Sse.Reciprocal(v) : Vector128.Create(1f / v[0], 1f / v[1], 1f / v[2], 1f / v[3]);
    private static float Abs(float x) => MathF.Abs(x);

    /// <summary>Demosaic <paramref name="src"/> (w×h float Bayer, 0..1 normalised) over <paramref name="roi"/> into
    /// <paramref name="dst"/> (RGBA, roi-sized, stride roi width). Red at (rx, ry).</summary>
    public static void Run(float[] src, int w, int h, RectI roi, int rx, int ry, float[] neutral, Vec4F[] dst)
    {
        if (neutral[0] <= 0f || neutral[1] <= 0f || neutral[2] <= 0f) throw new InvalidOperationException("invalid neutral white!");
        if (((w | h) & 1) != 0) throw new InvalidOperationException("invalid bayer image size!");
        float mx = neutral[0]; if (mx <= neutral[1]) mx = neutral[1]; if (mx <= neutral[2]) mx = neutral[2];
        float eps1 = mx * Eps1Scale, eps2 = Eps2Scale * mx;
        int gSum = 1 - ((rx + ry) & 1);   // (x+y)&1 of the G sites (R/B sites share the red position parity)

        // per-site neutral: [rowParity][colParity]
        float[] nsite = new float[4];
        for (int p = 0; p < 4; p++)
        {
            int yp = p >> 1, xp = p & 1;
            nsite[p] = (xp == rx && yp == ry) ? neutral[0] : (xp != rx && yp != ry) ? neutral[2] : neutral[1];
        }
        int W = w, H = h;
        float S(int x, int y)
        {
            int cx = x < 0 ? (x & 1) : x >= W ? W - 2 + (x & 1) : x;
            int cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            return src[cy * W + cx] * nsite[((y & 1) << 1) | (x & 1)];
        }
        // extended rect for the planes
        int ex0 = roi.X0 - 8, ey0 = roi.Y0 - 8, ew = roi.Width + 16, eh = roi.Height + 16;
        var C = new float[ew * eh]; var B = new float[ew * eh]; var A = new float[ew * eh];
        float Cg(int x, int y) => C[(y - ey0) * ew + (x - ex0)];
        float Bg(int x, int y) => B[(y - ey0) * ew + (x - ex0)];
        float Ag(int x, int y) => A[(y - ey0) * ew + (x - ex0)];
        // ---- C: 5×5 filter at G sites ----
        for (int y = ey0 + 2; y < ey0 + eh - 2; y++)
            for (int x = ex0 + 2; x < ex0 + ew - 2; x++)
            {
                if (((x + y) & 1) != gSum) continue;
                float c = S(x, y);
                float diag = ((S(x + 1, y - 1) + S(x - 1, y - 1)) + S(x - 1, y + 1)) + S(x + 1, y + 1);
                float axial = ((S(x, y + 1) + S(x, y - 1)) + S(x - 1, y)) + S(x + 1, y);
                float far = ((S(x - 2, y) + S(x + 2, y)) + S(x, y - 2)) + S(x, y + 2);
                float v = ((diag * Km4 + (axial * K6 + c * K56)) + S(x - 1, y - 2)) - (far + far);   // asm 1803a3740–37d9 order
                v = ((((((v + S(x + 1, y - 2)) + S(x - 2, y - 1)) + S(x + 2, y - 1)) + S(x - 2, y + 1)) + S(x + 2, y + 1)) + S(x - 1, y + 2)) + S(x + 1, y + 2);
                C[(y - ey0) * ew + (x - ex0)] = v * K1_64;
            }
        // ---- B ----
        for (int y = ey0 + 3; y < ey0 + eh - 3; y++)
            for (int x = ex0 + 3; x < ex0 + ew - 3; x++)
            {
                if (((x + y) & 1) == gSum) { B[(y - ey0) * ew + (x - ex0)] = Cg(x, y); continue; }
                float s = S(x, y);
                float cl = Cg(x - 1, y), cr = Cg(x + 1, y), cu = Cg(x, y - 1), cd = Cg(x, y + 1);
                float sl = S(x - 2, y), sr = S(x + 2, y), su = S(x, y - 2), sd = S(x, y + 2);
                float gh = Abs(cl - cr), gv = Abs(cu - cd);
                // asm 1803a3990–3a3d: den = |ΔC| + (|S_d − S| + eps); hsum = (l3 + l1) + (l2 + l0)
                var wv = Rcp4(Vector128.Create(gh + (Abs(sl - s) + eps2), gh + (Abs(sr - s) + eps2), gv + (Abs(su - s) + eps2), gv + (Abs(sd - s) + eps2)));
                float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
                float norm = Rcp((w3 + w1) + (w2 + w0));
                float t0 = (cl - (s + sl) * Half) * w0, t1 = (cr - (s + sr) * Half) * w1, t2 = (cu - (s + su) * Half) * w2, t3 = (cd - (s + sd) * Half) * w3;
                B[(y - ey0) * ew + (x - ex0)] = ((t3 + t1) + (t2 + t0)) * norm + s;
            }
        // ---- A ----
        for (int y = ey0 + 5; y < ey0 + eh - 5; y++)
            for (int x = ex0 + 5; x < ex0 + ew - 5; x++)
            {
                if (((x + y) & 1) != gSum) { A[(y - ey0) * ew + (x - ex0)] = Bg(x, y); continue; }   // R/B sites keep B; G sites are refined (verified against cp.dll's plane)
                float s = S(x, y), b = Bg(x, y);
                float sl = S(x - 2, y), sr = S(x + 2, y), su = S(x, y - 2), sd = S(x, y + 2);
                float bl2 = Bg(x - 2, y), br2 = Bg(x + 2, y), bu2 = Bg(x, y - 2), bd2 = Bg(x, y + 2);
                float bl = Bg(x - 1, y), br = Bg(x + 1, y), bu = Bg(x, y - 1), bd = Bg(x, y + 1);
                // asm 1803a3bf0–3cc2: den = |B_near − B| + (|S_far − S| + eps); t = (w·0.5)·(d + (B_far − S_far)); hsum = (t3 + t1) + (t2 + t0)
                var wv = Rcp4(Vector128.Create(Abs(bl - b) + (Abs(sl - s) + eps2), Abs(br - b) + (Abs(sr - s) + eps2), Abs(bu - b) + (Abs(su - s) + eps2), Abs(bd - b) + (Abs(sd - s) + eps2)));
                float w0 = wv[0], w1 = wv[1], w2 = wv[2], w3 = wv[3];
                float norm = Rcp((w3 + w1) + (w2 + w0));
                float d = b - s;
                float t0 = (w0 * Half) * (d + (bl2 - sl)), t1 = (w1 * Half) * (d + (br2 - sr)), t2 = (w2 * Half) * (d + (bu2 - su)), t3 = (w3 * Half) * (d + (bd2 - sd));
                A[(y - ey0) * ew + (x - ex0)] = ((t3 + t1) + (t2 + t0)) * norm + s;
            }
        // ---- D = S − A with parity replication outside the source ----
        // FUN_1803a5250: D = raw source (row/col clamped by parity, NOT neutral-scaled) − A at the requested row
        float Dg(int x, int y)
        {
            // left margin (1 px) replicates D[0] (the pair fill at 1803a5250 L1803a54xx writes D[0],D[1] at x=−1,0); right by parity
            int cx = x < 0 ? 0 : x >= W ? W - 2 + (x & 1) : x;
            int cy = y < 0 ? (y & 1) : y >= H ? H - 2 + (y & 1) : y;
            return src[cy * W + cx] - Ag(cx, y);
        }
        if (Environment.GetEnvironmentVariable("LUX_DEMOSAIC_DUMP") is string dumpDir)
        {
            File.WriteAllBytes(Path.Combine(dumpDir, "C.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(C).ToArray());
            File.WriteAllBytes(Path.Combine(dumpDir, "B.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(B).ToArray());
            File.WriteAllBytes(Path.Combine(dumpDir, "A.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(A).ToArray());
        }
        // ---- output quads ----
        int rw = roi.Width;
        for (int y = roi.Y0; y < roi.Y1; y++)
            for (int x = roi.X0; x < roi.X1; x++)
            {
                float a = Ag(x, y), d = Dg(x, y);
                bool isG = ((x + y) & 1) == gSum;
                float r, g, b;
                if (isG)
                {
                    // G site: horizontal neighbours are R when the row parity is ry
                    float wl = Rcp(Abs(a - Ag(x - 1, y)) + eps1), wr = Rcp(Abs(a - Ag(x + 1, y)) + eps1);
                    float wu = Rcp(Abs(a - Ag(x, y - 1)) + eps1), wd = Rcp(Abs(a - Ag(x, y + 1)) + eps1);
                    float nh = Rcp(wr + wl), nv = Rcp(wd + wu);
                    float hv = nh * (Dg(x + 1, y) * wr + Dg(x - 1, y) * wl) + a;
                    float vv = nv * (Dg(x, y + 1) * wd + Dg(x, y - 1) * wu) + a;
                    g = a + d;
                    if ((y & 1) == ry) { r = hv; b = vv; } else { b = hv; r = vv; }
                }
                else
                {
                    bool isR = (x & 1) == rx && (y & 1) == ry;
                    // axial 4 taps: (x−1,y), (x+1,y), (x,y−1), (x,y+1)
                    var wa = Rcp4(Vector128.Create(Abs(Ag(x - 1, y) - a) + eps1, Abs(Ag(x + 1, y) - a) + eps1, Abs(Ag(x, y - 1) - a) + eps1, Abs(Ag(x, y + 1) - a) + eps1));
                    float t0 = Dg(x - 1, y) * wa[0], t1 = Dg(x + 1, y) * wa[1], t2 = Dg(x, y - 1) * wa[2], t3 = Dg(x, y + 1) * wa[3];
                    float na = Rcp((wa[3] + wa[1]) + (wa[2] + wa[0]));   // shufpd/shufps horizontal sum
                    float gv = ((t3 + t1) + (t2 + t0)) * na + a;
                    // diagonal 4 taps: (x−1,y−1), (x+1,y−1), (x−1,y+1), (x+1,y+1)
                    var wd4 = Rcp4(Vector128.Create(Abs(Ag(x - 1, y - 1) - a) + eps1, Abs(Ag(x + 1, y - 1) - a) + eps1, Abs(Ag(x - 1, y + 1) - a) + eps1, Abs(Ag(x + 1, y + 1) - a) + eps1));
                    float nd = Rcp((wd4[3] + wd4[1]) + (wd4[2] + wd4[0]));
                    float dv = ((Dg(x + 1, y + 1) * wd4[3] + Dg(x + 1, y - 1) * wd4[1]) + (Dg(x - 1, y + 1) * wd4[2] + Dg(x - 1, y - 1) * wd4[0])) * nd + a;
                    g = gv;
                    if (isR) { r = a + d; b = dv; } else { b = a + d; r = dv; }
                }
                dst[(y - roi.Y0) * rw + (x - roi.X0)] = new Vec4F { R = r, G = g, B = b, A = 1f };
            }
    }
}
