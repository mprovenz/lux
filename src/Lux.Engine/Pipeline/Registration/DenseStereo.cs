using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// Dense stereo of the StereoAsyncAPI (`StereoLayer&lt;0&gt;` `1803124a0`/`180315ee0`, spec `aafb79e583bc2256a.md`):
/// plane-sweep truncated-SAD matching cost in YUVA over 3×3 box-filtered 8-bit images, 8-path SGM (two wavefront passes with
/// persistent line buffers), WTA. This file holds the layer parameters, the plane list (`FUN_18033d960/de60`), the box prefilter
/// (`ImageBoxFilter&lt;vec4x8ui&gt;` `1800eb250`), the guidance image, the per-camera warps (`FUN_180301040`) and the matching cost
/// (`FUN_18031cd60`) — all op-exact per the disassembly notes in the spec.
/// </summary>
public sealed class StereoParams
{
    public int Margin = 1, MinMaxWindow = 4; public float PlaneDensity = 2f; public int Scale = 32; public int BoxSize = 3;
    public short[] Caps = { 2, 6, 6, 0 }; public float[] Weights = { 2f, 0.5f, 0.5f, 0f };
    public int SkipMode = 0; public bool RunSgm = true; public ushort P1 = 1; public float P2Scale = 500f;
    public float[] Guidance = { BitConverter.Int32BitsToSingle(0x3ef6384f) / 6f, BitConverter.Int32BitsToSingle(0x3ef6384f) / 16f, BitConverter.Int32BitsToSingle(0x3ef6384f) / 16f, 0f };
    public int Median = 0; public int Tile = 4; public bool Confidence = false, PostFilter = false;

    /// <summary>The six layers of `FUN_1804eb200` (scale 32…1, tiles 4…64, skip mode 2 on the last two; plane density `DAT_1806eb2f0[iVar13==0]` = 2.0 for the L16 (all-A captures would get 4.0)).</summary>
    public static StereoParams[] L16Pyramid(bool moduleTypeNonZero = false)
    {
        int[] scales = { 32, 16, 8, 4, 2, 1 }, tiles = { 4, 8, 16, 32, 64, 64 };
        var r = new StereoParams[6];
        for (int i = 0; i < 6; i++) r[i] = new StereoParams { Scale = scales[i], Tile = tiles[i], SkipMode = i >= 4 ? 2 : 0, PlaneDensity = moduleTypeNonZero ? 4f : 2f, PostFilter = i == 5 && moduleTypeNonZero };
        return r;
    }
}

public static class DenseStereo
{
    public const float Near = 200f, Far = 640000f;   // DAT_1806eb2f8/300 [module type 0]

    static float RcpNr(float a) { float x0 = Sse.ReciprocalScalar(Vector128.CreateScalar(a)).ToScalar(); return x0 + x0 * (1.0f - a * x0); }
    static Vector128<float> RcpNr4(Vector128<float> a) { var x0 = Sse.Reciprocal(a); return x0 + x0 * (Vector128.Create(1.0f) - a * x0); }

    /// <summary>`FUN_18033d960` + `FUN_18033de60`: the metric plane depths (plane 0 = far … n−1 = near), n a multiple of align, ≤ 4096.</summary>
    public static float[] Planes(float near, float far, IReadOnlyList<CalibData> calibs, float density, int align)
    {
        if (!(near < far)) throw new InvalidOperationException("The near end has to be smaller than the fard end!");
        if (!(near > 0f && far > 0f)) throw new InvalidOperationException("Both the near and far end have to be positive!");
        var C = new float[calibs.Count][];
        for (int k = 0; k < calibs.Count; k++)
        {
            float[] R = calibs[k].R, t = calibs[k].T;
            C[k] = new[] { (t[0] * R[0] + t[1] * R[3]) + t[2] * R[6], (t[0] * R[1] + t[1] * R[4]) + t[2] * R[7], (t[0] * R[2] + t[1] * R[5]) + t[2] * R[8] };
        }
        float B = 0f;
        for (int k = 0; k < calibs.Count; k++)
        {
            float dx = C[0][0] - C[k][0], dy = C[0][1] - C[k][1], dz = C[0][2] - C[k][2];
            float d2 = dz * dz + (dx * dx + dy * dy);
            float d = Homography.Sqrt(d2);
            if (!(d <= B)) B = d;   // maxss
        }
        var x1 = RcpNr4(Vector128.Create(near, far, 0f, 0f));
        float inv = x1.GetElement(0) - x1.GetElement(1);
        int n = (int)(((inv * density) * B) * calibs[0].K[0]);
        if (n > 0x1000) n = 0x1000;
        if (n <= 0) throw new InvalidOperationException("Baseline too small, check calibration.");
        n = ((n + align - 1) / align) * align;
        var planes = new float[n];
        float step = (x1.GetElement(0) - x1.GetElement(1)) / (float)(n - 1), bas = x1.GetElement(1);
        if (n - 1 <= 7)
        {
            float v = bas; for (int i = 0; i < n - 1; i++) { planes[i] = 1.0f / v; v += step; }
        }
        else
        {
            int m = (n - 1) & ~7;
            var v4 = Vector128.Create(step) * Vector128.Create(0f, 1f, 2f, 3f) + Vector128.Create(bas);
            var d4 = Vector128.Create(4.0f * step);
            for (int i = 0; i < m; i += 8)
            {
                var hi = v4 + d4;
                var a = RcpNr4(v4); var b = RcpNr4(hi);
                for (int l = 0; l < 4; l++) { planes[i + l] = a.GetElement(l); planes[i + 4 + l] = b.GetElement(l); }
                v4 = hi + d4;
            }
            float v = (float)m * step + bas;
            for (int i = m; i < n - 1; i++) { planes[i] = 1.0f / v; v += step; }
        }
        planes[n - 1] = near;
        return planes;
    }

    /// <summary>`ImageBoxFilter&lt;vec4x8ui&gt;` 3×3 (`1800eb250`, lambda `1800ef9c0`): per channel `trunc(float(Σ)·(1/(cols·rows)))` over the
    /// in-image window, `cvtps2dq` round-to-nearest-even, saturated to a byte (the spec's trunc reading was wrong: verified RNE on the live images).</summary>
    public static Rgba8Image BoxFilter3(Rgba8Image src)
    {
        int W = src.W, H = src.H; var dst = new Rgba8Image(new byte[W * H * 4], W, H, W);
        for (int y = 0; y < H; y++)
        {
            int y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, H - 1); int rows = y1 - y0 + 1;
            for (int x = 0; x < W; x++)
            {
                int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, W - 1); int cols = x1 - x0 + 1;
                float inv = 1.0f / ((float)cols * (float)rows);
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int yy = y0; yy <= y1; yy++) for (int xx = x0; xx <= x1; xx++) sum += src.Data[(yy * W + xx) * 4 + c];
                    float v = (float)sum * inv;
                    int r = (int)MathF.Round(v, MidpointRounding.ToEven);   // cvtps2dq (RNE) — verified 100 % on Lumen's filtered images
                    dst.Data[(y * W + x) * 4 + c] = (byte)Math.Clamp(r, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>`FUN_180314160`: the guidance image — the (box-filtered) reference itself at scale 1, else the subsample
    /// `img0[min(scale·x + scale/2, W−1), min(scale·y + scale/2, H−1)]`.</summary>
    public static Rgba8Image Guidance(Rgba8Image img0, int w, int h, int scale)
    {
        if (img0.W == w && img0.H == h) return img0;
        var g = new Rgba8Image(new byte[w * h * 4], w, h, w); int half = (scale + (scale >> 31)) >> 1;
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(scale * y + half, img0.H - 1);
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(scale * x + half, img0.W - 1);
                for (int c = 0; c < 4; c++) g.Data[(y * w + x) * 4 + c] = img0.Data[(sy * img0.W + sx) * 4 + c];
            }
        }
        return g;
    }

    /// <summary>`FUN_180301040`: the reference→camera projective map `M = P_k·P_0⁻¹` as float columns (W0..W3).</summary>
    public static float[] Warp(CalibData refCam, CalibData cam) => Mat4D.FlowMatrix(refCam, cam);   // column-major float

    /// <summary>The cost context of one layer: filtered images, warps, the valid rects `img.rect + {1,1,−2,−2}` and the packed weights.</summary>
    public sealed class CostContext
    {
        public Rgba8Image Ref; public Rgba8Image[] Others = null!; public float[][] Warps = null!; public float[] Planes = null!;
        public ushort[][] Wq = null!;   // per other camera: {8160, 680, 680, 0} colour / {12240, 0, 0, 0} gray
        public byte[] Caps = { 2, 6, 6, 0 };
    }

    public static CostContext BuildContext(Rgba8Image[] images, bool[] gray, IReadOnlyList<CalibData> calibs, float[] planes)
    {
        var ctx = new CostContext { Ref = images[0], Others = new Rgba8Image[images.Length - 1], Warps = new float[images.Length - 1][], Planes = planes, Wq = new ushort[images.Length - 1][] };
        for (int k = 1; k < images.Length; k++)
        {
            ctx.Others[k - 1] = images[k]; ctx.Warps[k - 1] = Warp(calibs[0], calibs[k]);
            ctx.Wq[k - 1] = gray[k] ? new ushort[] { 12240, 0, 0, 0 } : new ushort[] { 8160, 680, 680, 0 };
        }
        return ctx;
    }

    static byte Avg(byte a, byte b) => (byte)((a + b + 1) >> 1);   // pavgb

    /// <summary>`FUN_18031cd60`: the per-plane matching cost of the reference pixel (x,y) (image coordinates of the half-res reference)
    /// for planes `[start, start+count)` of the range; returns u16 costs (wrapping sum over the other cameras).</summary>
    public static void MatchingCost(CostContext ctx, int x, int y, int start, int count, ushort[] cost)
    {
        Array.Clear(cost, 0, count);
        // reference 3×3 block (FUN_18030ee50), rows/cols clamped to the image
        var R = ctx.Ref; int W = R.W, H = R.H;
        Span<byte> refBlock = stackalloc byte[3 * 12];
        for (int r = -1; r <= 1; r++)
        {
            int yy = Math.Clamp(y + r, 0, H - 1);
            for (int c = -1; c <= 1; c++)
            {
                int xx = Math.Clamp(x + c, 0, W - 1);
                for (int ch = 0; ch < 4; ch++) refBlock[(r + 1) * 12 + (c + 1) * 4 + ch] = R.Data[(yy * W + xx) * 4 + ch];
            }
        }
        Span<byte> T = stackalloc byte[4 * 16];
        Span<int> S = stackalloc int[4];
        for (int k = 0; k < ctx.Others.Length; k++)
        {
            var img = ctx.Others[k]; var Wm = ctx.Warps[k]; var wq = ctx.Wq[k];
            int x0 = 1, y0 = 1, x1 = img.W - 2, y1 = img.H - 2;   // rect + {1,1,−2,−2}
            int lix = int.MinValue, liy = 0, lhx = 0, lhy = 0; int lastCost = 0; bool haveLast = false;
            for (int d = 0; d < count; d++)
            {
                float z = ctx.Planes[start + d];
                float xz = ((float)x * z) * 1.0f, yz = ((float)y * z) * 1.0f;
                // p = ((z·W2 + W3) + xz·W0) + yz·W1  (columns of the flow matrix)
                float px = ((z * Wm[8] + Wm[12]) + xz * Wm[0]) + yz * Wm[4];
                float py = ((z * Wm[9] + Wm[13]) + xz * Wm[1]) + yz * Wm[5];
                float pz = ((z * Wm[10] + Wm[14]) + xz * Wm[2]) + yz * Wm[6];
                float inv = 1.0f / pz;
                float fx = px * inv + 0.25f, fy = py * inv + 0.25f;
                int ix, iy, hx, hy;
                if (d == 0)
                {
                    float cx = Math.Min(Math.Max(fx, (float)(x0)), (float)(x1 - 1)), cy = Math.Min(Math.Max(fy, (float)(y0)), (float)(y1 - 1));
                    ix = (int)cx; iy = (int)cy; hx = (int)(cx + cx) & 1; hy = (int)(cy + cy) & 1;
                }
                else
                {
                    ix = (int)fx; iy = (int)fy; hx = (int)(fx + fx) & 1; hy = (int)(fy + fy) & 1;
                    if (haveLast && ix == lix && iy == liy && hx == lhx && hy == lhy) { cost[d] = (ushort)(cost[d] + lastCost); continue; }
                    if (ix < x0 || ix >= x1 || iy < y0 || iy >= y1) { cost[d] = (ushort)(cost[d] + lastCost); continue; }
                }
                // 4 rows × 4 pixels of the target
                for (int q = 0; q < 4; q++)
                {
                    int yy = iy - 1 + q;
                    for (int pI = 0; pI < 4; pI++) { int xx = ix - 1 + pI; for (int ch = 0; ch < 4; ch++) T[q * 16 + pI * 4 + ch] = img.Data[(yy * img.W + xx) * 4 + ch]; }
                }
                S.Clear();
                Span<int> A = stackalloc int[8]; Span<int> Bw = stackalloc int[8]; A.Clear(); Bw.Clear();
                for (int q = 0; q < 3; q++)
                {
                    Span<byte> row = stackalloc byte[16];
                    for (int b = 0; b < 16; b++) row[b] = hy != 0 ? Avg(T[q * 16 + b], T[(q + 1) * 16 + b]) : T[q * 16 + b];
                    if (hx != 0) { for (int b = 0; b < 12; b++) row[b] = Avg(row[b], row[b + 4]); for (int b = 12; b < 16; b++) row[b] = Avg(row[b], 0); }
                    for (int b = 0; b < 16; b++)
                    {
                        int rb = b < 12 ? refBlock[q * 12 + b] : 0;
                        int diff = Math.Max(row[b], rb) - Math.Min(row[b], rb);
                        int dd = Math.Min(diff, (int)ctx.Caps[b & 3]);
                        if (b < 8) A[b] = Math.Min(A[b] + dd, 65535); else Bw[b - 8] = Math.Min(Bw[b - 8] + dd, 65535);
                    }
                }
                // S = A + (B.low64 | 0); S = S + swap64(S) → channel sums over pixels 0..2
                for (int ch = 0; ch < 4; ch++)
                {
                    int s0 = Math.Min(A[ch] + Bw[ch], 65535), s1 = Math.Min(A[4 + ch] + 0, 65535);
                    S[ch] = Math.Min(s0 + s1, 65535);
                }
                int cY = (S[0] * wq[0] + 16) >> 5, cU = (S[1] * wq[1] + 16) >> 5, cV = (S[2] * wq[2] + 16) >> 5, cA = (S[3] * wq[3] + 16) >> 5;
                int ck = (cY + cU) + (cV + cA);
                float cf = Math.Min((float)ck, 65535.0f); int costK = (int)cf;
                cost[d] = (ushort)(cost[d] + costK);
                lix = ix; liy = iy; lhx = hx; lhy = hy; lastCost = costK; haveLast = true;
            }
        }
    }

    /// <summary>§6.5: `raw[d] = trunc(float(cost[d]) · (1/27f / nOthers))`; planes in [count, cap) = 255.</summary>
    public static void Normalise(ushort[] cost, int count, int cap, int nOthers, byte[] raw)
    {
        float f = BitConverter.Int32BitsToSingle(0x3d17b426) / (float)nOthers;
        for (int d = 0; d < count; d++) raw[d] = (byte)(int)((float)cost[d] * f);
        for (int d = count; d < cap; d++) raw[d] = 255;
    }
}
