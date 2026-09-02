using static Lux.Engine.Pipeline.ResAmp.SseOps;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>The image kernels called by `ImageResolutionAmp::lambda_0` (spec `a-resamp.md` §3–§6), each ported op-for-op from the
/// cp.dll disassembly/decompilation. All images are <see cref="ResImage"/> (4 floats/px unless noted); the kernels that clamp against
/// the source rect read real data outside the cropped view exactly as the originals do.</summary>
internal static class ResAmpKernels
{
    // ---------------------------------------------------------------- §1.5 √-domain converters
    /// <summary>`FUN_1804e4250`: dst = sqrtps(maxps(src, 0)) per lane (alpha included).</summary>
    public static void SqrtDomain(float[] data)
    {
        for (int i = 0; i < data.Length; i++) data[i] = MathF.Sqrt(Max(data[i], 0f));
    }
    /// <summary>`FUN_1804e4600`: dst = sqrtps(maxps(mulps(src, (g,g,g,1)), 0)).</summary>
    public static void GainSqrtDomain(float[] data, float g)
    {
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = MathF.Sqrt(Max(data[i] * g, 0f)); data[i + 1] = MathF.Sqrt(Max(data[i + 1] * g, 0f));
            data[i + 2] = MathF.Sqrt(Max(data[i + 2] * g, 0f)); data[i + 3] = MathF.Sqrt(Max(data[i + 3] * 1.0f, 0f));
        }
    }

    // ---------------------------------------------------------------- §3.1 FUN_18044b680 generator render with zero fill
    /// <summary>`FUN_18044b680(out, gen, rect)`: alloc rect.w×rect.h (rect (0,0,w,h)); the generator renders the part inside
    /// [0,genW)×[0,genH); everything outside is 0 in all 4 lanes.</summary>
    public static ResImage RenderGen(ImageGenerator gen, int x0, int y0, int x1, int y1)
    {
        int w = x1 - x0, h = y1 - y0;
        var img = new ResImage(w, h);      // zero-filled by .NET (the original memsets the bands outside the generator)
        int cx0 = Math.Max(x0, 0), cy0 = Math.Max(y0, 0), cx1 = Math.Min(x1, gen.W), cy1 = Math.Min(y1, gen.H);
        if (cx1 <= cx0 || cy1 <= cy0) return img;
        var view = new ResImage(img.Data, img.Idx(cx0 - x0, cy0 - y0), cx1 - cx0, cy1 - cy0, w, 4, -(cx0 - x0), -(cy0 - y0), w - (cx0 - x0), h - (cy0 - y0));
        gen.Render(view, cx0, cy0, cx1, cy1);
        return img;
    }

    // ---------------------------------------------------------------- §3.2 FUN_18044b380 dominant RGB covariance eigenvector
    public static void Eigenvector(ResImage img, int step, Span<float> outv)
    {
        float n = 0f, mx = 0f, my = 0f, mz = 0f, mw = 0f, M2x = 0f, M2y = 0f, M2z = 0f, M2w = 0f, Cxy = 0f, Cyz = 0f, Czx = 0f, Cww = 0f;
        const float one = 1.0f; float thr = F(0x3f733333);
        for (int y = 0; y < img.H; y += step)
            for (int x = 0; x < img.W; x += step)
            {
                int i = img.Idx(x, y);
                float px = img.Data[i], py = img.Data[i + 1], pz = img.Data[i + 2], pw = img.Data[i + 3];
                if (!(pw > thr)) continue;                                  // ucomiss: NaN → skip
                if (!(px < 1f && py < 1f && pz < 1f)) continue;             // cmpltps & 7 == 7
                float n1 = n + one; float inv = one / n1;
                float dx = px - mx, dy = py - my, dz = pz - mz, dw = pw - mw;
                float sx = inv * dx, sy = inv * dy, sz = inv * dz, sw = inv * dw;   // stp = bcast(inv)·delta
                float tx = n * sx, ty = n * sy, tz = n * sz, tw = n * sw;           // t = bcast(n)·stp
                mx += sx; my += sy; mz += sz; mw += sw;
                M2x += tx * dx; M2y += ty * dy; M2z += tz * dz; M2w += tw * dw;
                Cxy += dy * tx; Cyz += dz * ty; Czx += dx * tz; Cww += dw * tw;      // shufps(delta,0xC9)·t
                n = n1;
            }
        if ((int)n < 100) { outv[0] = outv[1] = outv[2] = F(0x3f13cd3a); return; }
        float s = one / n;
        float Vxx = M2x * s, Vyy = M2y * s, Vzz = M2z * s;
        float cxy = s * Cxy, cyz = s * Cyz, czx = s * Czx;
        // symmetric matrix [Vxx cxy czx; cxy Vyy cyz; czx cyz Vzz]; columns
        Span<float> c0 = stackalloc float[4] { Vxx, cxy, czx, 0f };
        Span<float> c1 = stackalloc float[4] { cxy, Vyy, cyz, 0f };
        Span<float> c2 = stackalloc float[4] { czx, cyz, Vzz, 0f };
        Span<float> v = stackalloc float[4] { 1f, 1f, 1f, 1f };
        Span<float> r = stackalloc float[4]; Span<float> hs = stackalloc float[4]; Span<float> rs = stackalloc float[4];
        for (int it = 0; it < 16; it++)
        {
            for (int l = 0; l < 4; l++)
            {
                float a = v[2] * c2[l], b = v[0] * c0[l];
                float t = a + b;
                float k = v[3] * (l == 3 ? 1f : 0f);
                t = k + t;
                r[l] = t + v[1] * c1[l];
            }
            float sqx = r[0] * r[0], sqy = r[1] * r[1], sqz = r[2] * r[2];   // lane3 → 0 (blendps 8)
            // shufpd swap + add: (z+x, w+y, x+z, y+w); shufps 0xb1 + add: lane0 = (w+y) + (z+x)
            float zx = sqz + sqx, wy = 0f + sqy, xz = sqx + sqz, yw = sqy + 0f;
            hs[0] = wy + zx; hs[1] = zx + wy; hs[2] = yw + xz; hs[3] = xz + yw;
            Rsqrtps(hs, rs);
            v[0] = rs[0] * r[0]; v[1] = rs[1] * r[1]; v[2] = rs[2] * r[2]; v[3] = r[3];
        }
        // sum = lane0 of (shufpd swap + add, shufps 0xb1 + add) over (x,y,z,0): (z+x, 0+y, ...) → (0+y) + (z+x)
        float zx2 = v[2] + v[0], y2 = 0f + v[1];
        float sum = y2 + zx2;
        if (!(sum >= 0f)) { v[0] = -v[0]; v[1] = -v[1]; v[2] = -v[2]; }
        outv[0] = v[0]; outv[1] = v[1]; outv[2] = v[2];
    }

    // ---------------------------------------------------------------- §3.3 lt::ImageBoxFilter<vec4x32f> / <float>
    /// <summary>`ImageBoxFilter&lt;T&gt;(out, src, size)` with the 256-px Tiler tiling reproduced (the running-sum order depends on it).
    /// Output has the source view size, rect (0,0,w,h). E = 4 (vec4, strictly sequential initial sum) or 1 (float, vectorised tree).</summary>
    public static ResImage BoxFilter(ResImage src, int W, int H)
    {
        int E = src.Elems, w = src.W, h = src.H;
        var outp = new ResImage(w, h, E);
        int nx = Math.Max(1, w / 256 + ((w % 256) * 2 > 256 ? 1 : 0)), ny = Math.Max(1, h / 256 + ((h % 256) * 2 > 256 ? 1 : 0));
        var B = new float[(w + W + 512) * E];
        for (int ty = 0; ty < ny; ty++)
            for (int tx = 0; tx < nx; tx++)
            {
                int x0, y0, x1, y1;
                if (nx * ny == 1) { x0 = 0; y0 = 0; x1 = w; y1 = h; }
                else
                {
                    x0 = 256 * tx; x1 = Math.Min(w, x0 + 256 * (tx == nx - 1 ? 2 : 1));
                    y0 = 256 * ty; y1 = Math.Min(h, y0 + 256 * (ty == ny - 1 ? 2 : 1));
                }
                BoxTile(src, outp, W, H, x0, y0, x1, y1, B);
            }
        return outp;
    }

    static void BoxTile(ResImage src, ResImage outp, int W, int H, int tx0, int ty0, int tx1, int ty1, float[] B)
    {
        int E = src.Elems; int hw = W >> 1, hh = H >> 1; int tw = tx1 - tx0;
        Array.Clear(B, 0, (tw + W) * E);
        int rx0 = src.RX0, ry0 = src.RY0, rx1 = src.RX1, ry1 = src.RY1;
        int cL = tx0 - hw - 1, cR = tx1 + W - 1 - hw;
        int jStart = Math.Max(0, rx0 - cL);
        int jEnd = (tw + W) - Math.Max(0, cR - rx1);
        float nrows = 0f;
        var d = src.Data;
        for (int i = 0; i < H; i++)
        {
            int r = ty0 - hh - 1 + i;
            if (r < ry0 || r >= ry1) continue;
            int rb = src.Idx(cL, r);
            for (int j = jStart; j < jEnd; j++)
                for (int e = 0; e < E; e++) B[j * E + e] = B[j * E + e] + d[rb + j * E + e];
            nrows += 1.0f;
        }
        int n0 = Math.Min(W, jEnd);
        int aL = Math.Min(jStart, tw), aR = tw - Math.Max(0, cR - rx1), aC = Math.Max(aR, aL);
        Span<float> S = stackalloc float[4];
        Span<float> A = stackalloc float[4]; Span<float> Bc = stackalloc float[4];
        for (int y = ty0; y < ty1; y++)
        {
            int rowAdd = y - hh + H - 1, rowSub = y - hh - 1;
            bool subOK = rowSub >= ry0, addOK = rowAdd < ry1;
            if (subOK && addOK)
            {
                int ra = src.Idx(cL, rowAdd), rs = src.Idx(cL, rowSub);
                for (int j = jStart; j < jEnd; j++)
                    for (int e = 0; e < E; e++) B[j * E + e] = (d[ra + j * E + e] - d[rs + j * E + e]) + B[j * E + e];
            }
            else if (subOK)
            {
                int rs = src.Idx(cL, rowSub);
                for (int j = jStart; j < jEnd; j++)
                    for (int e = 0; e < E; e++) B[j * E + e] = B[j * E + e] - d[rs + j * E + e];
                nrows += -1.0f;
            }
            else if (addOK)
            {
                int ra = src.Idx(cL, rowAdd);
                for (int j = jStart; j < jEnd; j++)
                    for (int e = 0; e < E; e++) B[j * E + e] = B[j * E + e] + d[ra + j * E + e];
                nrows += 1.0f;
            }
            // initial window sum S over B[jStart .. n0-1]
            S.Clear();
            if (jStart < n0)
            {
                int n = n0 - jStart;
                if (E == 4 || n <= 7)
                {
                    for (int j = jStart; j < n0; j++)
                        for (int e = 0; e < E; e++) S[e] = S[e] + B[j * E + e];
                }
                else
                {   // float variant: two 4-lane accumulators (spec §3.3)
                    A.Clear(); Bc.Clear();
                    int G = n >> 3; int j = jStart; int g = 0;
                    for (; g + 4 <= G; g += 4, j += 32)
                        for (int k = 0; k < 4; k++)
                        {
                            float t0 = B[j + k] + A[k];
                            float t1 = B[j + 4 + k] + Bc[k];
                            float t2 = B[j + 16 + k] + B[j + 8 + k];
                            float t3 = t2 + t0;
                            float t4 = B[j + 20 + k] + B[j + 12 + k];
                            float t5 = t4 + t1;
                            A[k] = B[j + 24 + k] + t3;
                            Bc[k] = B[j + 28 + k] + t5;
                        }
                    for (; g < G; g++, j += 8)
                        for (int k = 0; k < 4; k++) { A[k] = A[k] + B[j + k]; Bc[k] = Bc[k] + B[j + 4 + k]; }
                    float s0 = A[0] + Bc[0], s1 = A[1] + Bc[1], s2 = A[2] + Bc[2], s3 = A[3] + Bc[3];
                    float sum = (s2 + s0) + (s3 + s1);
                    for (; j < n0; j++) sum = sum + B[j];
                    S[0] = sum;
                }
            }
            int ob = outp.Idx(tx0, y);
            int i = 0;
            for (; i < aL; i++)
            {
                int ncols = Math.Min(tx0 - hw + i + W, rx1) - Math.Max(tx0 - hw + i, rx0);
                float norm = 1.0f / ((float)ncols * nrows);
                for (int e = 0; e < E; e++) { S[e] = S[e] + B[(W + i) * E + e]; outp.Data[ob + i * E + e] = norm * S[e]; }
            }
            if (aL < aR)
            {
                float norm = 1.0f / (nrows * (float)W);
                for (; i < aR; i++)
                    for (int e = 0; e < E; e++) { S[e] = S[e] + (B[(W + i) * E + e] - B[i * E + e]); outp.Data[ob + i * E + e] = S[e] * norm; }
            }
            for (i = aC; i < tw; i++)
            {
                int ncols = Math.Min(tx0 - hw + i + W, rx1) - Math.Max(tx0 - hw + i, rx0);
                float norm = 1.0f / ((float)ncols * nrows);
                for (int e = 0; e < E; e++) { S[e] = S[e] - B[i * E + e]; outp.Data[ob + i * E + e] = norm * S[e]; }
            }
        }
    }

    // ---------------------------------------------------------------- §3.4 expression evaluators → uint8
    /// <summary>`FUN_180445480`: mask = clamp(round_half_away(127·dot(A − B·rcpss(C.w + eps), w) + 127), 0, 255).</summary>
    public static byte[] MaskVec4(ResImage A, ResImage B, ReadOnlySpan<float> w, float eps, float m, float a, out int w_, out int h_)
    {
        int W = A.W, H = A.H; w_ = W; h_ = H; var outp = new byte[W * H];
        float w0 = w[0], w1 = w[1], w2 = w[2], w3 = w[3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int ia = A.Idx(x, y), ib = B.Idx(x, y);
                float r = Rcpss(B.Data[ib + 3] + eps);
                float dx = A.Data[ia] - r * B.Data[ib], dy = A.Data[ia + 1] - r * B.Data[ib + 1], dz = A.Data[ia + 2] - r * B.Data[ib + 2], dw = A.Data[ia + 3] - r * B.Data[ib + 3];
                float px = dx * w0, py = dy * w1, pz = dz * w2, pw = dw * w3;
                float zx = pz + px, wy = pw + py;
                float dot = wy + zx;
                float v = dot * m + a;
                outp[y * W + x] = RoundClampU8(v);
            }
        return outp;
    }

    /// <summary>`FUN_180447280`: v = (A − rcpss(C + eps)·B)·m + a → u8 (float images, A = luma, B = boxLuma, C = boxAlpha).</summary>
    public static byte[] FloatToU8(ResImage A, ResImage B, ResImage C, float eps, float m, float a)
    {
        int W = A.W, H = A.H; var outp = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float r = Rcpss(C.Data[C.Idx(x, y)] + eps);
                float v = (A.Data[A.Idx(x, y)] - r * B.Data[B.Idx(x, y)]) * m + a;
                outp[y * W + x] = RoundClampU8(v);
            }
        return outp;
    }

    static byte RoundClampU8(float v)
    {
        uint bits = BitConverter.SingleToUInt32Bits(v);
        float half = BitConverter.UInt32BitsToSingle((bits & 0x80000000u) | 0x3f000000u);
        float v2 = half + v;
        v2 = Max(v2, 0f);
        v2 = Min(v2, 255.0f);
        return (byte)Cvtt(v2);
    }

    // ---------------------------------------------------------------- §4.4 ImageConvSeparable2D<9,9,vec4x32f,float>
    /// <summary>Separable 9×9 conv: vertical pass first (columns c ∈ [minc,maxc) = [max(rect.x0,−4), min(rect.x1,w+4))) into a tmp row,
    /// then horizontal. Output = src view size, rect (0,0,w,h).</summary>
    public static ResImage Conv9(ResImage src, ReadOnlySpan<float> wV, ReadOnlySpan<float> wH)
    {
        int w = src.W, h = src.H; var outp = new ResImage(w, h);
        int minc = Math.Max(src.RX0, -4), maxc = Math.Min(src.RX1, w + 4);
        var tmp = new float[(maxc - minc) * 4]; int tb = -minc;   // tmp[(c + tb)*4]
        var d = src.Data; float v0 = wV[0], v1 = wV[1], v2 = wV[2], v3 = wV[3], v4 = wV[4], v5 = wV[5], v6 = wV[6], v7 = wV[7], v8 = wV[8];
        float h0 = wH[0], h1 = wH[1], h2 = wH[2], h3 = wH[3], h4 = wH[4], h5 = wH[5], h6 = wH[6], h7 = wH[7], h8 = wH[8];
        int leftEnd = Math.Clamp(minc + 4, 0, w), interiorEnd = Math.Clamp(maxc - 4, 0, w);
        Span<int> rows = stackalloc int[9];
        for (int y = 0; y < h; y++)
        {
            bool fast = y >= src.RY0 + 4 && y < src.RY1 - 4;
            for (int k = 0; k < 9; k++) rows[k] = fast ? y - 4 + k : Math.Clamp(y - 4 + k, src.RY0, src.RY1 - 1);
            for (int c = minc; c < maxc; c++)
            {
                int o = (c + tb) * 4;
                for (int e = 0; e < 4; e++)
                {
                    float r0 = d[src.Idx(c, rows[0]) + e], r1 = d[src.Idx(c, rows[1]) + e], r2 = d[src.Idx(c, rows[2]) + e], r3 = d[src.Idx(c, rows[3]) + e], r4 = d[src.Idx(c, rows[4]) + e];
                    float r5 = d[src.Idx(c, rows[5]) + e], r6 = d[src.Idx(c, rows[6]) + e], r7 = d[src.Idx(c, rows[7]) + e], r8 = d[src.Idx(c, rows[8]) + e];
                    float t;
                    if (fast)
                    {
                        float a = v1 * r1 + v0 * r0; float b = v3 * r3 + v2 * r2;
                        a = v4 * r4 + a; b = v5 * r5 + b; a = v6 * r6 + a; b = v7 * r7 + b; a = v8 * r8 + a;
                        t = a + b;
                    }
                    else
                    {
                        float a = v1 * r1 + v0 * r0; float b = v3 * r3 + v2 * r2; b = b + a;
                        float c5 = v5 * r5 + v4 * r4; float c6 = v6 * r6 + c5; c6 = c6 + b;
                        float c8 = v8 * r8 + v7 * r7; t = c8 + c6;
                    }
                    tmp[o + e] = t;
                }
            }
            int ob = outp.Idx(0, y);
            for (int x = 0; x < w; x++)
            {
                bool interior = x >= leftEnd && x < interiorEnd;
                for (int e = 0; e < 4; e++)
                {
                    float t0, t1, t2, t3, t4, t5, t6, t7, t8;
                    if (interior)
                    {
                        int b = (x - 4 + tb) * 4 + e;
                        t0 = tmp[b]; t1 = tmp[b + 4]; t2 = tmp[b + 8]; t3 = tmp[b + 12]; t4 = tmp[b + 16]; t5 = tmp[b + 20]; t6 = tmp[b + 24]; t7 = tmp[b + 28]; t8 = tmp[b + 32];
                        float a = h1 * t1 + h0 * t0; float bb = h3 * t3 + h2 * t2; bb = bb + a;
                        float c5 = h5 * t5 + h4 * t4; float c6 = h6 * t6 + c5; float c7 = h7 * t7 + c6;
                        float c8 = h8 * t8 + bb;
                        outp.Data[ob + x * 4 + e] = c8 + c7;
                    }
                    else
                    {
                        int Tap(int k) => (Math.Clamp(x - 4 + k, minc, maxc - 1) + tb) * 4 + e;
                        t0 = tmp[Tap(0)]; t1 = tmp[Tap(1)]; t2 = tmp[Tap(2)]; t3 = tmp[Tap(3)]; t4 = tmp[Tap(4)]; t5 = tmp[Tap(5)]; t6 = tmp[Tap(6)]; t7 = tmp[Tap(7)]; t8 = tmp[Tap(8)];
                        float a = h1 * t1 + h0 * t0; float bb = h3 * t3 + h2 * t2; bb = bb + a;
                        float c5 = h5 * t5 + h4 * t4; float c6 = h6 * t6 + c5; c6 = c6 + bb;
                        float c8 = h8 * t8 + h7 * t7;
                        outp.Data[ob + x * 4 + e] = c8 + c6;
                    }
                }
            }
        }
        return outp;
    }

    // ---------------------------------------------------------------- §4.8 A::BilinearResample<vec4x32f>
    /// <summary>16.16 bilinear: xpos = x·scx + offx; taps clamped to the source rect edges by region; vertical rows clamped.</summary>
    public static void BilinearResample(ResImage dst, ResImage src, double offX, double offY, double scX, double scY)
    {
        int offx = Cvtt(offX * 65536.0), scx = Cvtt(scX * 65536.0), offy = Cvtt(offY * 65536.0), scy = Cvtt(scY * 65536.0);
        int w = dst.W, h = dst.H;
        int xpos0 = 0 * scx + offx, xpos1 = w * scx + offx;
        int xLo = Math.Min(Math.Max(src.RX0 << 16, xpos0), xpos1);
        int xHi = Math.Min(Math.Max((src.RX1 << 16) - 0x10000, xpos0), xpos1);
        float inv16 = F(0x37800000);
        var row0 = new float[w * 4]; var row1 = new float[w * 4];
        void HRow(int iy, float[] row)
        {
            int r = Math.Clamp(iy, src.RY0, src.RY1 - 1);
            float fx = (float)(xpos0 & 0xffff) * inv16, fstep = (float)(scx & 0xffff) * inv16;
            int xpos = xpos0;
            for (int x = 0; x < w; x++)
            {
                int o = x * 4;
                if (xpos < xLo)
                {
                    int s = src.Idx(src.RX0, r);
                    row[o] = src.Data[s]; row[o + 1] = src.Data[s + 1]; row[o + 2] = src.Data[s + 2]; row[o + 3] = src.Data[s + 3];
                }
                else if (xpos < xHi)
                {
                    int ix = xpos >> 16; int a = src.Idx(ix, r), b = a + 4;
                    for (int e = 0; e < 4; e++) row[o + e] = (src.Data[b + e] - src.Data[a + e]) * fx + src.Data[a + e];
                }
                else
                {
                    int s = src.Idx(src.RX1 - 1, r);
                    row[o] = src.Data[s]; row[o + 1] = src.Data[s + 1]; row[o + 2] = src.Data[s + 2]; row[o + 3] = src.Data[s + 3];
                }
                xpos += scx;
                float nf = fx + fstep; fx = nf - MathF.Floor(nf);
            }
        }
        for (int y = 0; y < h; y++)
        {
            int ypos = y * scy + offy; int iy = ypos >> 16;
            HRow(iy, row0); HRow(iy + 1, row1);
            float fy = (float)(ypos & 0xffff) * inv16;
            int ob = dst.Idx(0, y);
            for (int i = 0; i < w * 4; i++) dst.Data[ob + i] = (row1[i] - row0[i]) * fy + row0[i];
        }
    }

    // ---------------------------------------------------------------- §6.2a ImageResample<4,vec4x32f> (cubic B-spline, 64 phases)
    static readonly float[] BSplineTable = BuildBSpline();
    static float[] BuildBSpline()
    {
        var t = new float[64 * 4];
        for (int i = 0; i < 64; i++)
        {
            float phi = (float)i * 0.015625f;
            t[i * 4] = BS(phi + 1.0f); t[i * 4 + 1] = BS(phi); t[i * 4 + 2] = BS(1.0f - phi); t[i * 4 + 3] = BS(2.0f - phi);
        }
        return t;
        static float BS(float x)
        {
            float a = x * x; float b = a * x;
            if (x < 1.0f) return ((a * -6.0f + 4.0f) + b * 3.0f) * F(0x3e2aaaab);
            if (x < 2.0f) return (((x * -12.0f + 8.0f) + a * 6.0f) - b) * F(0x3e2aaaab);
            return 0f;
        }
    }
    static readonly float[] CatmullTable = BuildCatmull();
    static float[] BuildCatmull()
    {
        var t = new float[64 * 4]; Span<float> k = stackalloc float[4];
        for (int i = 0; i < 64; i++) { Geometry.WarpResample.Kernel((float)i * 0.015625f, k); for (int j = 0; j < 4; j++) t[i * 4 + j] = k[j]; }
        return t;
    }

    /// <summary>`ImageResample&lt;4&gt;` (B-spline) or `ImageResample&lt;2&gt;` (Catmull-Rom): 16.16 fixed point, taps clamped to the source rect,
    /// `row[x] = (W1·s1 + W0·s0) + (W3·s3 + W2·s2)`, `dst = (r1·W1 + r0·W0) + (r3·W3 + r2·W2)`. Writes the whole dst view.</summary>
    public static void Resample(ResImage dst, ResImage src, double offX, double offY, double scX, double scY, bool bspline)
    {
        var T = bspline ? BSplineTable : CatmullTable;
        int offx = Cvtt(offX * 65536.0), scx = Cvtt(scX * 65536.0), offy = Cvtt(offY * 65536.0), scy = Cvtt(scY * 65536.0);
        int w = dst.W, h = dst.H;
        var cache = new Dictionary<int, float[]>();
        float[] Row(int r)
        {
            int sy = Math.Clamp(r, src.RY0, src.RY1 - 1);
            if (cache.TryGetValue(sy, out var row)) return row;
            row = new float[w * 4];
            int rx = offx;
            for (int x = 0; x < w; x++, rx += scx)
            {
                int ix = rx >> 16, ph = (rx >> 10) & 63;
                int i0 = src.Idx(Math.Clamp(ix - 1, src.RX0, src.RX1 - 1), sy), i1 = src.Idx(Math.Clamp(ix, src.RX0, src.RX1 - 1), sy);
                int i2 = src.Idx(Math.Clamp(ix + 1, src.RX0, src.RX1 - 1), sy), i3 = src.Idx(Math.Clamp(ix + 2, src.RX0, src.RX1 - 1), sy);
                float W0 = T[ph * 4], W1 = T[ph * 4 + 1], W2 = T[ph * 4 + 2], W3 = T[ph * 4 + 3];
                for (int e = 0; e < 4; e++)
                    row[x * 4 + e] = (W1 * src.Data[i1 + e] + W0 * src.Data[i0 + e]) + (W3 * src.Data[i3 + e] + W2 * src.Data[i2 + e]);
            }
            cache[sy] = row; return row;
        }
        for (int y = 0; y < h; y++)
        {
            int ry = scy * y + offy; int iy = ry >> 16, ph = (ry >> 10) & 63;
            var r0 = Row(iy - 1); var r1 = Row(iy); var r2 = Row(iy + 1); var r3 = Row(iy + 2);
            float W0 = T[ph * 4], W1 = T[ph * 4 + 1], W2 = T[ph * 4 + 2], W3 = T[ph * 4 + 3];
            int ob = dst.Idx(0, y);
            for (int i = 0; i < w * 4; i++) dst.Data[ob + i] = (r1[i] * W1 + r0[i] * W0) + (r3[i] * W3 + r2[i] * W2);
        }
    }

    // ---------------------------------------------------------------- §6.4a FUN_180449620 N×N 4-tap cubic sample
    /// <summary>`FUN_180449620(cubic16, dst N×N, src, pos)`: 16-phase Catmull-Rom, +1/32 bias, no clamping (pos in src data-pointer coords).</summary>
    public static void SampleNxN(float[] cubic16, float[] dst, int N, ResImage src, float posX, float posY)
    {
        float bias = F(0x3d000000);
        float px = posX + bias, py = bias + posY;
        int ix = FloorI(px), iy = FloorI(py);
        int phx = Cvtt((px - (float)ix) * 16.0f), phy = Cvtt((py - (float)iy) * 16.0f);
        float wx0 = cubic16[phx * 4], wx1 = cubic16[phx * 4 + 1], wx2 = cubic16[phx * 4 + 2], wx3 = cubic16[phx * 4 + 3];
        float wy0 = cubic16[phy * 4], wy1 = cubic16[phy * 4 + 1], wy2 = cubic16[phy * 4 + 2], wy3 = cubic16[phy * 4 + 3];
        var tmp = new float[(N + 3) * 4];
        for (int row = 0; row < N; row++)
        {
            int b0 = src.Idx(ix - 1, iy - 1 + row), b1 = b0 + src.Stride * 4, b2 = b1 + src.Stride * 4, b3 = b2 + src.Stride * 4;
            for (int c = 0; c < N + 3; c++)
                for (int e = 0; e < 4; e++)
                {
                    float t = src.Data[b0 + c * 4 + e] * wy0;
                    t = src.Data[b1 + c * 4 + e] * wy1 + t;
                    t = src.Data[b2 + c * 4 + e] * wy2 + t;
                    t = src.Data[b3 + c * 4 + e] * wy3 + t;
                    tmp[c * 4 + e] = t;
                }
            for (int x = 0; x < N; x++)
                for (int e = 0; e < 4; e++)
                {
                    float a = tmp[(x + 1) * 4 + e] * wx1 + tmp[x * 4 + e] * wx0;
                    float b = tmp[(x + 3) * 4 + e] * wx3 + tmp[(x + 2) * 4 + e] * wx2;
                    dst[(row * N + x) * 4 + e] = b + a;
                }
        }
    }

    // ---------------------------------------------------------------- §6.1 FUN_18044bb30 border mirror
    /// <summary>Mirror n px of the region [x0,x1)×[y0,y1) (data-pointer-relative) outwards into the allocation.</summary>
    public static void Mirror(ResImage img, int x0, int y0, int x1, int y1, int n)
    {
        if (!(img.W >= x1 && img.H >= y1 && x0 >= 0 && y0 >= 0 && n > 0)) throw new InvalidOperationException("mirror: bad region");
        int L = Math.Min(x0, n), R = Math.Min(img.W - x1, n), T = Math.Min(y0, n), Bm = Math.Min(img.H - y1, n);
        for (int y = y0; y < y1; y++)
        {
            for (int i = 0; i < L; i++) Copy(img, x0 + i, y, x0 - 1 - i, y);
            for (int i = 0; i < R; i++) Copy(img, x1 - 1 - i, y, x1 + i, y);
        }
        for (int x = x0 - L; x < x1 + R; x++)
        {
            for (int j = 0; j < T; j++) Copy(img, x, y0 + j, x, y0 - 1 - j);
            for (int j = 0; j < Bm; j++) Copy(img, x, y1 - 1 - j, x, y1 + j);
        }
        static void Copy(ResImage img, int sx, int sy, int dx, int dy)
        {
            int s = img.Idx(sx, sy), d = img.Idx(dx, dy);
            img.Data[d] = img.Data[s]; img.Data[d + 1] = img.Data[s + 1]; img.Data[d + 2] = img.Data[s + 2]; img.Data[d + 3] = img.Data[s + 3];
        }
    }
}

/// <summary>An `ImageGenerator&lt;vec4x32f&gt;` (std::function + dims): renders the clamped rect [x0,x1)×[y0,y1) (generator coordinates)
/// into the given view (whose (0,0) is (x0,y0)). The √-domain conversion (spec §1.5) is part of the generator (lambda_1/2/3).</summary>
public sealed class ImageGenerator
{
    public int W, H;
    public Action<ResImage, int, int, int, int> Render;
    public ImageGenerator(int w, int h, Action<ResImage, int, int, int, int> render) { W = w; H = h; Render = render; }

    /// <summary>Wrap a raw float-RGBA source (e.g. the reference/L1 render) with `FUN_1804e4250` (√ of max(·,0)).</summary>
    public static ImageGenerator SqrtOf(int w, int h, Func<RectI, float[]> raw) => new(w, h, (view, x0, y0, x1, y1) =>
    {
        var src = raw(new RectI(x0, y0, x1, y1)); ResAmpKernels.SqrtDomain(src); CopyInto(view, src, x1 - x0, y1 - y0);
    });
    /// <summary>Wrap a raw tele tile-cache source with gain + √ (`initResAmp::lambda_3` / `FUN_1804e4600`).</summary>
    public static ImageGenerator GainSqrtOf(int w, int h, float gain, Func<RectI, float[]> raw) => new(w, h, (view, x0, y0, x1, y1) =>
    {
        var src = raw(new RectI(x0, y0, x1, y1)); ResAmpKernels.GainSqrtDomain(src, gain); CopyInto(view, src, x1 - x0, y1 - y0);
    });
    static void CopyInto(ResImage view, float[] src, int w, int h)
    {
        if (view.W != w || view.H != h) throw new InvalidOperationException("generator view size mismatch");
        for (int y = 0; y < h; y++) Array.Copy(src, y * w * 4, view.Data, view.Idx(0, y), w * 4);
    }
}
