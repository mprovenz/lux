using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static Lux.Engine.Pipeline.ResAmp.SseOps;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>`lt::ImageResolutionAmp` + its `lambda_0`: Lumen 2.3's level-0 super-resolution merge of the
/// wide reference with the tele modules.
/// The result is the √-domain canvas tile; `PipelineCache::processLevel0` squares it (<see cref="Square"/>) and multiplies by the neutral.</summary>
public sealed class ImageResolutionAmp
{
    public readonly ImageGenerator RefGen, L1Gen;
    public readonly IReadOnlyList<ResAmpModule> Modules;
    public readonly float Scale, InvScale;
    public readonly int N;
    public readonly float[] Window, Winv, Cubic16;
    public ResAmpTrace? Trace;
    /// <summary>Diagnostics: (record index, gcol, grow) coarse grid points to print ModuleMerge inputs/outputs for.</summary>
    public HashSet<(int, int, int)>? DebugPoints;

    /// <summary>The driver preamble: Hann window of length N = trunc(16·scale), its rsqrt/rcp inverse, the 16-phase Catmull-Rom table.</summary>
    public ImageResolutionAmp(ImageGenerator refGen, ImageGenerator l1Gen, IReadOnlyList<ResAmpModule> modules, float scale)
    {
        RefGen = refGen; L1Gen = l1Gen; Modules = modules; Scale = scale;
        Cubic16 = new float[16 * 4]; Span<float> k = stackalloc float[4];
        for (int i = 0; i < 16; i++) { Lux.Engine.Pipeline.Geometry.WarpResample.Kernel((float)i * 0.0625f, k); for (int j = 0; j < 4; j++) Cubic16[i * 4 + j] = k[j]; }
        float n16 = 16.0f * scale;
        if (CeilI(n16) >= 0x3e) throw new InvalidOperationException("patch size exceeds the limit!");
        N = Cvtt(n16);
        Window = new float[N]; Winv = new float[N];
        float c = F(0x40c90fdb) / (float)N;
        for (int i = 0; i < N; i++)
        {
            float v = ((float)i + 0.5f) * c;
            float w = WineCrt.Cosf(v);   // the CRT cosf (glibc's differs by an ulp on N = 34, musl's on 11 lengths the L16 never reaches)
            Window[i] = 0.5f - w * 0.5f;
        }
        for (int i = 0; i < N; i++)
        {
            float x = Window[i] + Window[i];
            float rs = Rsqrtss(x); float s = x * rs; float t = -0.5f * s; float q = ((s * rs) + -3.0f) * t;
            if (x == 0f) q = 0f;
            q += F(0x38d1b717);
            float r = Rcpss(q);
            Winv[i] = Min(r, 4.0f);
        }
        InvScale = 1.0f / scale;
    }

    /// <summary>`ImageResolutionAmp(out, …, rect)` for a non-empty rect: one lambda_0 call over the whole rect (canvas coordinates).
    /// Returns the rect.w×rect.h √-domain result (pre-square).</summary>
    public ResImage Run(RectI rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) throw new NotSupportedException("empty rect (Tiler path) is not used by PipelineCache");
        var outp = new ResImage(rect.Width, rect.Height);
        Lambda0(rect, outp);
        return outp;
    }

    /// <summary>The caller's √ decode — every lane squared (alpha too).</summary>
    public static void Square(ResImage img)
    {
        for (int y = 0; y < img.H; y++) { int b = img.Idx(0, y); for (int i = 0; i < img.W * 4; i++) img.Data[b + i] = img.Data[b + i] * img.Data[b + i]; }
    }

    // ------------------------------------------------------------------------------------------------------------------
    void Lambda0(RectI r, ResImage outp)
    {
        float invS = InvScale, s = Scale;
        // geometry in wide-reference coordinates
        TileGeometry(r, invS, out int x0c, out int y0c, out int x1c, out int y1c);
        int w = x1c - x0c, h = y1c - y0c;
        var accAlloc = new ResImage(w + 15, h + 15);
        var acc = accAlloc.Crop(8, 8, w + 8, h + 8);                         // w×h, rect (−8,−8,w+7,h+7)
        int gw = (w >> 3) + 1, gh = (h >> 3) + 1;
        var grid = new int[gw * gh * 2];
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) { grid[(j * gw + i) * 2] = x0c + 8 * i; grid[(j * gw + i) * 2 + 1] = y0c + 8 * j; }
        int refW = RefGen.W, refH = RefGen.H;

        // reference preparation
        var refImg = ResAmpKernels.RenderGen(RefGen, x0c - 24, y0c - 24, x1c + 24, y1c + 24);
        Trace?.Emit("ref", refImg);
        refImg = refImg.Crop(16, 16, w + 32, h + 32);                         // (w+16)×(h+16), rect (−16,−16,w+32,h+32)
        Span<float> v = stackalloc float[3];
        ResAmpKernels.Eigenvector(refImg, 4, v);
        Trace?.Emit("eigvec", new[] { v[0], v[1], v[2] });
        float k3 = F(0x3f13cd3a);
        var weights = new float[4] { v[0] * k3, v[1] * k3, k3 * v[2], 0f };
        var box = ResAmpKernels.BoxFilter(refImg, 32, 32);
        Trace?.Emit("box", box);
        var maskData = ResAmpKernels.MaskVec4(refImg, box, weights, F(0x30800000), 127.0f, 127.0f, out int mw, out int mh);
        var mask = new U8Image(maskData, 0, mw, mh, mw, 0, 0, mw, mh);
        Trace?.Emit("mask", mask);
        mask = mask.Crop(8, 8, w + 8, h + 8);                                 // w×h, rect (−8,−8,w+8,h+8)
        var l1 = ResAmpKernels.RenderGen(L1Gen, x0c - 8, y0c - 8, x1c + 8, y1c + 8);
        Trace?.Emit("l1", l1);
        var L1img = l1.Crop(8, 8, w + 8, h + 8);
        float[] hann16 = Hann16;

        // per-module preparation
        var records = new List<ModuleRecord>();
        for (int m = 0; m < Modules.Count; m++)
        {
            var mod = Modules[m]; var proj = new int[gw * gh * 2];
            bool ok = ModulePrep.Project(grid, gw, gh, mod.Warp, refW, refH, mod.Gen.W, mod.Gen.H, proj, out int minx, out int miny, out int maxx, out int maxy);
            Trace?.Emit($"m{m}_proj", (proj, gw, gh));
            if (!ok) { Trace?.Emit($"m{m}_skipped", (minx, miny, maxx, maxy)); continue; }
            records.Add(ModulePrep.Build(mod.Gen, s, weights, proj, gw, gh, minx, miny, maxx, maxy, m, Trace));
        }

        // After the module loop the L1 view is expanded back to its whole allocation, colour-decorrelated IN PLACE (M·p, alpha
        // kept, the same operation as in the module preparation) over all (w+16)×(h+16) px, then cropped
        // again to (8,8,w+8,h+8). The 8-px halo the ImageResample<2> taps read is therefore transformed too.
        {
            var Mr = ModulePrep.Mrow; var d = l1.Data;
            for (int o = 0; o < d.Length; o += 4)
            {
                float R = d[o], G = d[o + 1], B = d[o + 2];
                d[o] = (G * Mr[1] + R * Mr[0]) + B * Mr[2];
                d[o + 1] = (G * Mr[4] + R * Mr[3]) + B * Mr[5];
                d[o + 2] = (G * Mr[7] + R * Mr[6]) + B * Mr[8];
            }
        }

        // coarse per-grid-point alignment + wavelet merge
        bool prof = Diagnostics.PhaseLog.On; long tRef = 0, tAlign = 0, tMerge = 0, tInv = 0, tStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var ws = new float[0x26e0 / 4];
        WriteSlotTable(ws);
        var patch = new byte[256]; var blk = new float[1024];
        for (int grow = 0; grow < gh; grow++)
            for (int gcol = 0; gcol < gw; gcol++)
            {
                int idx = grow * gw + gcol;
                int gx_l = grid[idx * 2] - x0c, gy_l = grid[idx * 2 + 1] - y0c;
                for (int rr = 0; rr < 16; rr++) Array.Copy(mask.Data, mask.Idx(gx_l - 8, gy_l - 8 + rr), patch, rr * 16, 16);
                long tr0 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                RefAnalysis.Run(ws, L1img, gx_l, gy_l);
                if (prof) tRef += System.Diagnostics.Stopwatch.GetTimestamp() - tr0;
                foreach (var rec in records)
                {
                    int gmx = rec.Grid[idx * 2], gmy = rec.Grid[idx * 2 + 1];
                    if (gmx == int.MinValue) continue;
                    long ta0 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    bool aligned = CoarseAlign.Align(rec, gmx, gmy, invS, s, patch, blk, out float fx, out float fy);
                    if (prof) tAlign += System.Diagnostics.Stopwatch.GetTimestamp() - ta0;
                    if (!aligned)
                    { rec.Grid[idx * 2] = int.MinValue; rec.Grid[idx * 2 + 1] = int.MinValue; continue; }
                    float X = fx * s;
                    if (DebugPoints is not null && DebugPoints.Contains((records.IndexOf(rec), gcol, grow)))
                    {
                        float amin = float.MaxValue, amax = float.MinValue; for (int p = 3; p < 1024; p += 4) { amin = Math.Min(amin, blk[p]); amax = Math.Max(amax, blk[p]); }
                        Console.WriteLine($"[dbg] r{records.IndexOf(rec)} gp ({gcol},{grow}) gm ({gmx},{gmy}) fx {fx:R} fy {fy:R} blk alpha [{amin:R},{amax:R}] blk[0] ({blk[0]:R},{blk[1]:R},{blk[2]:R},{blk[3]:R}) sumAbsRef {ws[0x26d0 / 4]:R} e_ref {ws[0x1540 / 4]:R} {ws[0x1550 / 4]:R} {ws[0x1560 / 4]:R} {ws[0x1570 / 4]:R}");
                    }
                    // The shared static is written only when debugging is (or was) active: a write per block from every tile thread
                    // keeps its cache line bouncing between cores
                    if (DebugPoints is not null || ModuleMerge.Dbg is not null)
                        ModuleMerge.Dbg = DebugPoints is not null && DebugPoints.Contains((records.IndexOf(rec), gcol, grow)) ? m => Console.WriteLine("[dbg]   " + m) : null;
                    long tm0 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    float conf = ModuleMerge.Run(ws, blk);
                    if (prof) tMerge += System.Diagnostics.Stopwatch.GetTimestamp() - tm0;
                    if (DebugPoints is not null && DebugPoints.Contains((records.IndexOf(rec), gcol, grow))) Console.WriteLine($"[dbg]   conf {conf:R} ({BitConverter.SingleToUInt32Bits(conf):x8})");
                    float Y = fy * s;
                    rec.Res[idx * 3] = X; rec.Res[idx * 3 + 1] = Y; rec.Res[idx * 3 + 2] = conf;
                }
                long ti0 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                int o = InverseMerge.Run(ws);
                if (prof) tInv += System.Diagnostics.Stopwatch.GetTimestamp() - ti0;
                for (int j = 0; j < 16; j++)
                {
                    int ab = acc.Idx(gx_l - 8, gy_l - 8 + j);
                    for (int i = 0; i < 16; i++)
                    {
                        float wgt = hann16[i] * hann16[j];
                        int bo = o + j * 64 + i * 4, ao = ab + i * 4;
                        for (int e = 0; e < 4; e++) acc.Data[ao + e] = wgt * ws[bo + e] + acc.Data[ao + e];
                    }
                }
            }
        if (prof)
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency, now = Diagnostics.PhaseLog.Now;
            Diagnostics.PhaseLog.Add("ra.ref", now, tRef * f); Diagnostics.PhaseLog.Add("ra.align", now, tAlign * f); Diagnostics.PhaseLog.Add("ra.merge", now, tMerge * f);
            Diagnostics.PhaseLog.Add("ra.inverse", now, tInv * f); Diagnostics.PhaseLog.Add("ra.grid-loop", now, (System.Diagnostics.Stopwatch.GetTimestamp() - tStart) * f);
        }
        using var phRest = Diagnostics.PhaseLog.Time("ra.upsample+rest");
        Trace?.Emit("coarse", acc);
        foreach (var rec in records) { Trace?.Emit($"r{records.IndexOf(rec)}_grid_post", (rec.Grid, gw, gh)); Trace?.Emit($"r{records.IndexOf(rec)}_res", (rec.Res, gw, gh)); }

        // border mirror
        int dxm = x1c - refW, dym = y1c - refH;
        if (dxm > 0 || dym > 0)
        {
            int rw_ = w - Math.Max(dxm, 0), rh_ = h - Math.Max(dym, 0);
            ResAmpKernels.Mirror(acc, 0, 0, rw_, rh_, 2);
            ResAmpKernels.Mirror(L1img, 0, 0, rw_, rh_, 2);
        }
        // upsample the coarse results to canvas resolution
        int rw = r.Width, rh = r.Height;
        var outCrop = outp;
        double offX = (double)((float)r.X0 * invS - (float)x0c), offY = (double)((float)r.Y0 * invS - (float)y0c);
        ResAmpKernels.Resample(outCrop, acc, offX, offY, (double)invS, (double)invS, bspline: true);
        Trace?.Emit("O", outCrop);
        Trace?.Emit("l1crop", L1img);
        var L1up = new ResImage(rw, rh);
        ResAmpKernels.Resample(L1up, L1img, offX, offY, (double)invS, (double)invS, bspline: false);
        Trace?.Emit("U", L1up);

        // full-resolution accumulator
        int half = N >> 1;
        int X0 = FloorI((float)x0c * s), Y0 = FloorI((float)y0c * s);
        int Wg = FloorI((float)x1c * s) - X0, Hg = FloorI((float)y1c * s) - Y0;
        var accFAlloc = new ResImage(Wg + N - 1, Hg + N - 1);
        var accF = accFAlloc.Crop(Math.Max(0, half), Math.Max(0, half), Math.Min(Wg + half, accFAlloc.W), Math.Min(Hg + half, accFAlloc.H));
        var fpatch = new float[N * N * 4]; var render = new float[N * N * 4];

        // per-grid-point merge
        Span<uint> S = stackalloc uint[4];
        uint seed = (uint)(r.Y0 << 16 | r.X0);
        S[0] = seed ^ 0xb36534e5u; S[1] = seed ^ 0x93fc4795u; S[2] = seed ^ 0xa511e9b3u; S[3] = seed ^ 0xdf6e307fu;
        float c006 = F(0x3bc49ba6), c02 = F(0x3e4ccccd);
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                int idx = j * gw + i;
                float fx = (float)grid[idx * 2] * s, fy = (float)grid[idx * 2 + 1] * s;
                int gx = FloorI(fx), gy = FloorI(fy);
                // the four xorshift states advanced as one Vector128<uint> (the same integer steps per lane)
                var sv = Vector128.Create(S[0], S[1], S[2], S[3]);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        sv ^= sv << 13; sv ^= sv >>> 17; sv ^= sv << 5;
                        float u = BitConverter.UInt32BitsToSingle((sv.GetElement(0) >> 9) | 0x40000000u) + -3.0f;
                        float t1 = Winv[y] * c006; float t2 = Winv[x] * u; float vv = t2 * t1;
                        int po = (y * N + x) * 4;
                        fpatch[po] = vv; fpatch[po + 1] = 0f; fpatch[po + 2] = 0f; fpatch[po + 3] = 0f;
                    }
                S[0] = sv.GetElement(0); S[1] = sv.GetElement(1); S[2] = sv.GetElement(2); S[3] = sv.GetElement(3);
                float W = c02;
                foreach (var rec in records)
                {
                    if (rec.Grid[idx * 2] == int.MinValue) continue;
                    float f3x = rec.Res[idx * 3], f3y = rec.Res[idx * 3 + 1], conf = rec.Res[idx * 3 + 2];
                    float posX = (((float)gx - fx) - (float)half) + f3x, posY = (((float)gy - fy) - (float)half) + f3y;
                    CheckFootprint(rec, posX, posY, N);
                    ResAmpKernels.SampleNxN(Cubic16, render, N, rec.Hp, posX, posY);
                    if (N > 0)
                    {
                        float m = Max(conf + -0.5f, 0f); float wv0 = (m + m) + conf;
                        var wv = Vector128.Create(wv0, conf, conf, conf);   // a pixel's four lanes at once (lane 0 by wv0, the rest by conf)
                        for (int p = 0; p < N * N; p++)
                        {
                            int po = p * 4;
                            (Vector128.Create((ReadOnlySpan<float>)render.AsSpan(po, 4)) * wv + Vector128.Create((ReadOnlySpan<float>)fpatch.AsSpan(po, 4))).CopyTo(fpatch.AsSpan(po, 4));
                        }
                    }
                    W = W + conf;
                }
                float rW = Rcpss(W);
                for (int p = 0; p < N * N * 4; p++) fpatch[p] = fpatch[p] * rW;
                if (N > 0)
                {
                    float a = rW * c02;
                    int bx = (gx - X0) - half, by = (gy - Y0) - half;
                    for (int y = 0; y < N; y++)
                    {
                        int ab = accF.Idx(bx, by + y);
                        for (int x = 0; x < N; x++)
                        {
                            int po = (y * N + x) * 4, ao = ab + x * 4;
                            float wgt = Window[x] * Window[y];
                            // the four lanes at once (lane 3 takes a in place of the patch value)
                            var fp = Vector128.Create((ReadOnlySpan<float>)fpatch.AsSpan(po, 4)).WithElement(3, a);
                            (Vector128.Create(wgt) * fp + Vector128.Create((ReadOnlySpan<float>)accF.Data.AsSpan(ao, 4))).CopyTo(accF.Data.AsSpan(ao, 4));
                        }
                    }
                }
            }
        Trace?.Emit("fullacc", accF);

        // detail term
        {
            int cx0 = Math.Max(r.X0 - X0, accF.RX0), cy0 = Math.Max(r.Y0 - Y0, accF.RY0);
            int cx1 = Math.Min(r.X0 - X0 + rw, accF.RX1), cy1 = Math.Min(r.Y0 - Y0 + rh, accF.RY1);
            if (cx1 > cx0 && cy1 > cy0)
            {
                float K0 = 2.0f, LO = F(0xbdcccccd), HI = F(0x3dcccccd);
                for (int y = 0; y < cy1 - cy0; y++)
                    for (int x = 0; x < cx1 - cx0; x++)
                    {
                        int ai = accF.Idx(cx0 + x, cy0 + y), oi = outCrop.Idx(x, y), ui = L1up.Idx(x, y);
                        float Aw = accF.Data[ai + 3];
                        for (int e = 0; e < 4; e++)
                        {
                            float d = L1up.Data[ui + e] - outCrop.Data[oi + e];
                            float det = (Aw * (e == 0 ? K0 : 0f)) * d;
                            det = Max(det, LO); det = Min(det, HI);
                            outCrop.Data[oi + e] = (accF.Data[ai + e] + outCrop.Data[oi + e]) + det;
                        }
                    }
            }
        }
        Trace?.Emit("out65", outCrop);
        // decorrelation inverse (Mᵀ) + alpha 1
        var M = ModulePrep.Mrow;
        for (int y = 0; y < rh; y++)
        {
            int b = outCrop.Idx(0, y);
            for (int x = 0; x < rw; x++)
            {
                int o = b + x * 4; float px = outCrop.Data[o], py = outCrop.Data[o + 1], pz = outCrop.Data[o + 2];
                for (int e = 0; e < 3; e++)
                {
                    float q = py * M[3 + e] + px * M[e];
                    q = pz * M[6 + e] + q;
                    outCrop.Data[o + e] = q;
                }
                outCrop.Data[o + 3] = 1.0f;
            }
        }
        Trace?.Emit("amp_out", outCrop);
    }

    /// <summary>Tile rect (canvas) → 8-aligned wide-reference region with ≥ 2 px margin.</summary>
    public static void TileGeometry(RectI r, float invS, out int x0c, out int y0c, out int x1c, out int y1c)
    {
        int xs0 = Cvtt((float)r.X0 * invS), ys0 = Cvtt((float)r.Y0 * invS), xs1 = Cvtt((float)r.X1 * invS), ys1 = Cvtt((float)r.Y1 * invS);
        int ax0 = xs0 & ~7, ay0 = ys0 & ~7;
        int X1 = Ceil8(xs1), Y1 = Ceil8(ys1);
        x0c = ax0 + ((xs0 - ax0) > 1 ? 0 : -8); y0c = ay0 + ((ys0 - ay0) > 1 ? 0 : -8);
        x1c = (X1 | 1) + (((X1 | 1) - xs1) > 1 ? 0 : 8); y1c = (Y1 | 1) + (((Y1 | 1) - ys1) > 1 ? 0 : 8);
        static int Ceil8(int v) { int t = v + 7; return (t + (int)((uint)(t >> 31) >> 29)) & ~7; }
    }

    static void CheckFootprint(ModuleRecord rec, float posX, float posY, int N)
    {
        float bias = F(0x3d000000);
        int ix = FloorI(posX + bias), iy = FloorI(bias + posY);
        var img = rec.Hp;
        if (ix - 1 < img.RX0 || iy - 1 < img.RY0 || ix + N + 2 > img.RX1 || iy + N + 2 > img.RY1)
            throw new InvalidOperationException($"ResAmp: N×N footprint ({ix - 1}..{ix + N + 1}, {iy - 1}..{iy + N + 1}) leaves the module halo rect ({img.RX0},{img.RY0},{img.RX1},{img.RY1}) of record {rec.ModuleIndex}");
    }

    /// <summary>The 256-B slot table at ws+0x25d0, `slot(r,c)`.</summary>
    static void WriteSlotTable(float[] ws)
    {
        var bytes = MemoryMarshal.AsBytes(ws.AsSpan(0x25d0 / 4, 64));
        for (int r = 0; r < 16; r++)
            for (int c = 0; c < 16; c++)
            {
                int slot = ((r & 1) != 0 || (c & 1) != 0) ? 0 : ((r & 2) != 0 || (c & 2) != 0) ? 1 : ((r & 4) != 0 || (c & 4) != 0) ? 2 : ((r | c) == 8) ? 3 : 4;
                bytes[r * 16 + c] = (byte)slot;
            }
    }

    /// <summary>The 16-tap Hann window used by the coarse overlap-add (Lumen's exact values).</summary>
    public static readonly float[] Hann16 =
    {
        F(0x3c1d6840), F(0x3dac933c), F(0x3e638c4e), F(0x3ece0e91), F(0x3f18f8b8), F(0x3f471cee), F(0x3f6a6d99), F(0x3f7d8a60),
        F(0x3f7d8a5f), F(0x3f6a6d98), F(0x3f471ceb), F(0x3f18f8b9), F(0x3ece0e8e), F(0x3e638c46), F(0x3dac933c), F(0x3c1d6820),
    };
}
