using static Lux.Engine.Pipeline.ResAmp.SseOps;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>§4 of `a-resamp.md`: per tele module — WarpField projection of the 8-px coarse grid, module render (gain+√ by the generator),
/// colour decorrelation, Lanczos-2 9-tap blur, luma/alpha, 81×81 box-normalised u8 luma, the nine 1/3-px phase maps, bilinear↓ +
/// B-spline↑ high-pass, and the record push.</summary>
internal static class ModulePrep
{
    /// <summary>The decorrelation matrix (static initializer list @0x1806defb0, copied to DAT_180836de8..e08): rows M[0]=(de8,dec,df0), M[1]=(df4,df8,dfc), M[2]=(e00,e04,e08).</summary>
    public static readonly float[] Mrow =
    {
        F(0x3f13cd36), F(0x3f13cd36), F(0x3f13cd36),
        F(0x3f350529), 0f,            F(0xbf350529),
        F(0x3ed10625), F(0xbf510625), F(0x3ed10625),
    };

    /// <summary>§4.1: project the coarse grid through the WarpField. Returns false (module skipped) unless bw &gt; 0 &amp;&amp; bh &gt; 0.</summary>
    public static bool Project(int[] grid, int gw, int gh, WarpField wf, int refW, int refH, int Wm, int Hm, int[] proj, out int minx, out int miny, out int maxx, out int maxy)
    {
        minx = int.MaxValue; miny = int.MaxValue; maxx = int.MinValue; maxy = int.MinValue;
        var M = wf.M; float neg8 = F(0xc1000000), half = 0.5f;
        float fH7 = (float)(Hm + 7), fW7 = (float)(Wm + 7);
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                int k = (j * gw + i) * 2; int gx = grid[k], gy = grid[k + 1];
                proj[k] = int.MinValue; proj[k + 1] = int.MinValue;
                if (((gx | gy) >> 31) != 0 || gx >= refW || gy >= refH) continue;
                float fx = (float)gx * wf.Sx, fy = (float)gy * wf.Sy;
                int dix = Cvtt(fx), diy = Cvtt(fy);
                float d = wf.Depth[diy * wf.DepthStride + dix];
                float Y = fy * d; float X = fx * d;
                float vx = ((d * M[8] + M[12]) + X * M[0]) + Y * M[4];
                float vy = ((d * M[9] + M[13]) + X * M[1]) + Y * M[5];
                float vz = ((d * M[10] + M[14]) + X * M[2]) + Y * M[6];
                float inv = 1.0f / vz;
                float py = vy * inv;
                if (!(py < fH7) || !(py >= neg8)) continue;
                float px = inv * vx;
                if (!(px < fW7) || !(px >= neg8)) continue;
                int ix = Cvtt(px + half), iy = Cvtt(py + half);
                proj[k] = ix; proj[k + 1] = iy;
                if (ix < minx) minx = ix; if (iy < miny) miny = iy; if (ix > maxx) maxx = ix; if (iy > maxy) maxy = iy;
            }
        int bw = maxx - minx, bh = maxy - miny;
        return bw > 0 && minx != int.MaxValue && bh > 0;
    }

    /// <summary>§4.2–§4.10 for one module whose projection produced the bbox. Returns the record.</summary>
    public static ModuleRecord Build(ImageGenerator gen, float scale, ReadOnlySpan<float> sWeights, int[] proj, int gw, int gh,
                                     int minx, int miny, int maxx, int maxy, int moduleIndex, ResAmpTrace? trace)
    {
        string tp = $"m{moduleIndex}_";
        int bw = maxx - minx, bh = maxy - miny;
        float s16 = scale * 16.0f; int Nm = CeilI(s16);
        int s32i = Cvtt(scale * 32.0f); int hh = s32i >> 1; int m1 = hh + Nm; int m2 = hh + Nm + 4;
        var render = ResAmpKernels.RenderGen(gen, minx - m2, miny - m2, maxx + 4 + m1, maxy + 4 + m1);
        trace?.Emit(tp + "render", render);
        int boxSize = s32i | 1;
        // §4.3 colour decorrelation in place: out_k = (G·M[k][1] + R·M[k][0]) + B·M[k][2]
        {
            var d = render.Data;
            for (int i = 0; i < d.Length; i += 4)
            {
                float R = d[i], G = d[i + 1], B = d[i + 2];
                d[i] = (G * Mrow[1] + R * Mrow[0]) + B * Mrow[2];
                d[i + 1] = (G * Mrow[4] + R * Mrow[3]) + B * Mrow[5];
                d[i + 2] = (G * Mrow[7] + R * Mrow[6]) + B * Mrow[8];
            }
        }
        // §4.4 Lanczos-2 taps
        float invS = 1.0f / scale; var wk = new float[9]; float sum = 0f;
        float pi = F(0x40490fdb), pi2 = F(0x3fc90fdb), pisq = F(0x411de9e7);
        for (int k = 0; k < 9; k++)
        {
            float x = (float)(k - 4) * invS; float w;
            if (x == 0.0f) w = 1.0f;
            else if (MathF.Abs(x) >= 2.0f) w = 0.0f;
            else { float a = MathF.Sin(x * pi); float b = MathF.Sin(x * pi2); w = ((b + b) * a) / ((x * x) * pisq); }
            wk[k] = w; sum = sum + w;
        }
        float invSum = 1.0f / sum; for (int k = 0; k < 9; k++) wk[k] *= invSum;
        trace?.Emit(tp + "wk", wk);
        int W = render.W, H = render.H;
        int ex = bw + 2 * m1 + 4, ey = bh + 2 * m1 + 4;
        render = render.Crop(4, 4, ex, ey);                       // (W−8)×(H−8), rect (−4,−4,W−4,H−4)
        var blur = ResAmpKernels.Conv9(render, wk, wk);
        // §4.5 luma / alpha
        float s0 = sWeights[0], s1 = sWeights[1], s2 = sWeights[2];
        float L0 = (Mrow[1] * s1 + Mrow[0] * s0) + Mrow[2] * s2;
        float L1 = (Mrow[4] * s1 + Mrow[3] * s0) + Mrow[5] * s2;
        float L2 = (Mrow[7] * s1 + Mrow[6] * s0) + Mrow[8] * s2;
        var luma = new ResImage(blur.W, blur.H, 1); var alpha = new ResImage(blur.W, blur.H, 1);
        for (int y = 0; y < blur.H; y++)
            for (int x = 0; x < blur.W; x++)
            {
                int i = blur.Idx(x, y);
                float m0 = blur.Data[i] * L0, mm1 = blur.Data[i + 1] * L1, mm2 = blur.Data[i + 2] * L2, m3 = blur.Data[i + 3] * 0f;
                luma.Data[luma.Idx(x, y)] = (m3 + mm1) + (mm2 + m0);
                alpha.Data[alpha.Idx(x, y)] = blur.Data[i + 3];
            }
        int cx1 = bw + hh + 2 * Nm, cy1 = bh + hh + 2 * Nm;
        luma = luma.Crop(hh, hh, cx1, cy1); alpha = alpha.Crop(hh, hh, cx1, cy1);
        render = render.Crop(hh, hh, cx1, cy1); blur = blur.Crop(hh, hh, cx1, cy1);
        int uw = bw + 2 * Nm, uh = bh + 2 * Nm;
        // §4.6 normalised-luma u8 map
        var boxLuma = ResAmpKernels.BoxFilter(luma, boxSize, boxSize);
        var boxAlpha = ResAmpKernels.BoxFilter(alpha, boxSize, boxSize);
        var u8data = ResAmpKernels.FloatToU8(luma, boxLuma, boxAlpha, F(0x30800000), 127.0f, 127.0f);
        var u8 = new U8Image(u8data, 0, uw, uh, uw, 0, 0, uw, uh);
        trace?.Emit(tp + "u8", u8);
        // §4.7 the nine 1/3-phase maps
        int cw = CeilI((float)bw * invS), ch = CeilI((float)bh * invS);
        float fN = (float)Nm; float fOff = fN - s16;
        int step = Cvtt(scale * F(0x46aaaaab));
        int start = Cvtt(fOff * 65536.0f);
        int step3 = 3 * step;
        var ph = new U8Image[9];
        for (int k = 0; k < 9; k++) ph[k] = new U8Image(cw + 32, ch + 32);
        {
            int X0 = u8.RX0, X1m = u8.RX1 - 1, Y0 = u8.RY0, Y1m = u8.RY1 - 1;
            int ypos = start; int pw = cw + 32;
            for (int j = 0; j < ch + 32; j++)
            {
                for (int p = 0; p < 3; p++)
                {
                    int yp = ypos + p * step; int iy = yp >> 16; int fy = (int)((uint)yp >> 2) & 0x3fff;
                    int r0 = u8.Idx(0, Math.Clamp(iy, Y0, Y1m)), r1 = u8.Idx(0, Math.Clamp(iy + 1, Y0, Y1m));
                    int xpos = start;
                    for (int i = 0; i < pw; i++)
                    {
                        for (int q = 0; q < 3; q++)
                        {
                            int xq = xpos + q * step; int ix = xq >> 16; int fx = (int)((uint)xq >> 2) & 0x3fff;
                            int c0 = Math.Clamp(ix, X0, X1m), c1 = Math.Clamp(ix + 1, X0, X1m);
                            int a = u8.Data[r0 + c0], b = u8.Data[r1 + c0], c = u8.Data[r0 + c1], d = u8.Data[r1 + c1];
                            int v0 = (b - a) * fy + (a << 14);
                            int v1 = (d - c) * fy + (c << 14);
                            int v = ((v1 - v0) >> 14) * fx + v0;
                            ph[3 * p + q].Data[j * pw + i] = (byte)(v >> 14);
                        }
                        xpos += step3;
                    }
                }
                ypos += step3;
            }
        }
        for (int k = 0; k < 9; k++) ph[k] = ph[k].Crop(16, 16, cw + 16, ch + 16);
        // §4.8 bilinear down-sample of the blurred module
        var bil = new ResImage(cw + 32, ch + 32);
        ResAmpKernels.BilinearResample(bil, blur, (double)fOff, (double)fOff, (double)scale, (double)scale);
        trace?.Emit(tp + "bil", bil);
        bil = bil.Crop(16, 16, cw + 16, ch + 16);
        // §4.9 B-spline up-sample and high-pass
        var up = new ResImage(uw, uh);
        double upOff = (double)(-(fN * invS));
        ResAmpKernels.Resample(up, bil, upOff, upOff, (double)invS, (double)invS, bspline: true);
        trace?.Emit(tp + "up", up);
        for (int y = 0; y < uh; y++)
        {
            int ri = render.Idx(0, y), ui = up.Idx(0, y);
            for (int x = 0; x < uw * 4; x++) render.Data[ri + x] = render.Data[ri + x] - up.Data[ui + x];
        }
        render = render.Crop(Nm, Nm, bw + Nm, bh + Nm);
        blur = blur.Crop(Nm, Nm, bw + Nm, bh + Nm);
        var rec = new ModuleRecord
        {
            MinX = minx, MinY = miny, MaxX = maxx, MaxY = maxy, Grid = proj, Gw = gw, Gh = gh,
            Res = new float[gw * gh * 3], Hp = render, Blur = blur, Ph = ph, ModuleIndex = moduleIndex,
        };
        trace?.Emit(tp + "hp", render); trace?.Emit(tp + "blur", blur);
        for (int k = 0; k < 9; k++) trace?.Emit(tp + "ph" + k, ph[k]);
        return rec;
    }
}
