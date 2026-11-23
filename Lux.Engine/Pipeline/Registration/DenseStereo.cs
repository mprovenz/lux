using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// Dense stereo of the StereoAsyncAPI (`StereoLayer&lt;0&gt;`):
/// plane-sweep truncated-SAD matching cost in YUVA over 3×3 box-filtered 8-bit images, 8-path SGM (two wavefront passes with
/// persistent line buffers), WTA. This file holds the layer parameters, the plane list, the box prefilter
/// (`ImageBoxFilter&lt;vec4x8ui&gt;`), the guidance image, the per-camera warps and the matching cost — each with Lumen's exact
/// operation order.
/// </summary>
public sealed class StereoParams
{
    public int Margin = 1, MinMaxWindow = 4; public float PlaneDensity = 2f; public int Scale = 32; public int BoxSize = 3;
    public short[] Caps = { 2, 6, 6, 0 }; public float[] Weights = { 2f, 0.5f, 0.5f, 0f };
    public int SkipMode = 0; public bool RunSgm = true; public ushort P1 = 1; public float P2Scale = 500f;
    public float[] Guidance = { BitConverter.Int32BitsToSingle(0x3ef6384f) / 6f, BitConverter.Int32BitsToSingle(0x3ef6384f) / 16f, BitConverter.Int32BitsToSingle(0x3ef6384f) / 16f, 0f };
    public int Median = 0; public int Tile = 4; public bool Confidence = false, PostFilter = false;

    /// <summary>The six layers of the dense stereo (scale 32…1, tiles 4…64, skip mode 2 on the last two; plane density 2.0 for the L16 (all-A captures would get 4.0)).</summary>
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
    public const float Near = 200f, Far = 640000f;   // module type 0

    static float RcpNr(float a) { float x0 = IntelApprox.ReciprocalScalar(Vector128.CreateScalar(a)).ToScalar(); return x0 + x0 * (1.0f - a * x0); }
    static Vector128<float> RcpNr4(Vector128<float> a) { var x0 = IntelApprox.Reciprocal(a); return x0 + x0 * (Vector128.Create(1.0f) - a * x0); }

    /// <summary>The metric plane depths (plane 0 = far … n−1 = near), n a multiple of align, ≤ 4096.</summary>
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
            if (!(d <= B)) B = d;   // MAXSS order: NaN gives d
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

    /// <summary>`ImageBoxFilter&lt;vec4x8ui&gt;` 3×3: per channel `float(Σ)·(1/(cols·rows))` over the in-image window,
    /// `cvtps2dq` round-to-nearest-even, saturated to a byte. The window sum is the sum of three row sums of three taps each.
    /// The sums are integers, so their order does not change the result.</summary>
    public static unsafe Rgba8Image BoxFilter3(Rgba8Image src)
    {
        int W = src.W, H = src.H;
        if (!Vector128.IsHardwareAccelerated || W < 2 || src.Data.Length < (long)W * H * 4) return BoxFilter3Scalar(src);
        var dst = new Rgba8Image(new byte[W * H * 4], W, H, W);
        // Rows 0 to 2 hold the row sums of the last three source rows (source row y in row y mod 3). Row 3 stays zero and
        // stands in for a row outside the image. Each row has room for an eight-lane load at the last pixel.
        int rowLen = W * 4 + 8; var sums = new ushort[4 * rowLen];
        int have = -1;
        fixed (byte* ps = src.Data, pd = dst.Data) fixed (ushort* pr = sums)
        {
            ushort* zero = pr + 3 * rowLen;
            for (int y = 0; y < H; y++)
            {
                int y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, H - 1); int rows = y1 - y0 + 1;
                while (have < y1) { have++; RowSums3(ps + (long)have * W * 4, pr + (have % 3) * rowLen, W); }
                ushort* r0 = pr + (y0 % 3) * rowLen, r1 = rows >= 2 ? pr + ((y0 + 1) % 3) * rowLen : zero, r2 = rows == 3 ? pr + (y1 % 3) * rowLen : zero;
                var invEdge = Vector128.Create(1.0f / ((float)2 * (float)rows)); var invInner = Vector128.Create(1.0f / ((float)3 * (float)rows));
                byte* o = pd + (long)y * W * 4;
                for (int x = 0; x < W; x++)
                {
                    var sum = Vector128.WidenLower(Vector128.Load(r0 + x * 4) + Vector128.Load(r1 + x * 4) + Vector128.Load(r2 + x * 4)).AsInt32();
                    var v = Vector128.ConvertToSingle(sum) * (x == 0 || x == W - 1 ? invEdge : invInner);
                    var q = Simd.ConvertToVector128Int32(v);   // cvtps2dq (RNE)
                    var w16 = Simd.PackSignedSaturate(q, q);
                    Unsafe.WriteUnaligned(o + x * 4, Simd.PackUnsignedSaturate(w16, w16).AsUInt32().ToScalar());
                }
            }
        }
        return dst;
    }

    /// <summary>The per-channel sums of the in-image pixels x−1, x, x+1 for one row of `w` pixels (`w` ≥ 2).</summary>
    static unsafe void RowSums3(byte* row, ushort* sums, int w)
    {
        int last = (w - 1) * 4;
        for (int c = 0; c < 4; c++)
        {
            sums[c] = (ushort)(row[c] + row[4 + c]);
            sums[last + c] = (ushort)(row[last - 4 + c] + row[last + c]);
        }
        for (int i = 4; i < last; i++) sums[i] = (ushort)(row[i - 4] + row[i] + row[i + 4]);
    }

    /// <summary>The box filter, one channel at a time.</summary>
    static Rgba8Image BoxFilter3Scalar(Rgba8Image src)
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
                    int r = (int)MathF.Round(v, MidpointRounding.ToEven);   // cvtps2dq (RNE)
                    dst.Data[(y * W + x) * 4 + c] = (byte)Math.Clamp(r, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>The guidance image — the (box-filtered) reference itself at scale 1, else the subsample
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

    /// <summary>The reference→camera projective map `M = P_k·P_0⁻¹` as float columns (W0..W3).</summary>
    public static float[] Warp(CalibData refCam, CalibData cam) => Mat4D.FlowMatrix(refCam, cam);   // column-major float

    /// <summary>The cost context of one layer: filtered images, warps, the valid rects `img.rect + {1,1,−2,−2}` and the packed weights.</summary>
    public sealed class CostContext
    {
        public Rgba8Image Ref; public Rgba8Image[] Others = null!; public float[][] Warps = null!; public float[] Planes = null!;
        public ushort[][] Wq = null!;   // per other camera: {8160, 680, 680, 0} color / {12240, 0, 0, 0} gray
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

    /// <summary>The largest cap for the vector form of the matching cost: nine capped differences of a channel must fit one byte.</summary>
    const int MaxVectorCap = 28;

    /// <summary>The pixels 1, 2 and 3 of a row of four 4-channel pixels in the places of pixels 0, 1 and 2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> NextPixel(Vector128<byte> row) => Vector128.Shuffle(row.AsUInt32(), Vector128.Create(1u, 2u, 3u, 3u)).AsByte();

    /// <summary>The capped absolute differences of two rows of bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> CappedDifference(Vector128<byte> a, Vector128<byte> b, Vector128<byte> caps) => Vector128.Min(Vector128.Max(a, b) - Vector128.Min(a, b), caps);

    /// <summary>The per-plane matching cost of the reference pixel (x,y) (image coordinates of the half-res reference)
    /// for planes `[start, start+count)` of the range; returns u16 costs (wrapping sum over the other cameras).
    /// A row of the 3×3 block is one 16-byte vector of four 4-channel pixels. The fourth pixel does not count.</summary>
    public static unsafe void MatchingCost(CostContext ctx, int x, int y, int start, int count, ushort[] cost)
    {
        var capsA = ctx.Caps;
        if (!Vector128.IsHardwareAccelerated || capsA[0] > MaxVectorCap || capsA[1] > MaxVectorCap || capsA[2] > MaxVectorCap || capsA[3] > MaxVectorCap
            || start < 0 || count < 0 || start + count > ctx.Planes.Length)
        {
            MatchingCostScalar(ctx, x, y, start, count, cost); return;
        }
        Array.Clear(cost, 0, count);
        // reference 3×3 block, rows/cols clamped to the image; the fourth pixel of each row is zero
        var R = ctx.Ref; int W = R.W, H = R.H;
        Span<byte> refBlock = stackalloc byte[3 * 16]; refBlock.Clear();
        for (int r = -1; r <= 1; r++)
        {
            int yy = Math.Clamp(y + r, 0, H - 1);
            for (int c = -1; c <= 1; c++)
            {
                int xx = Math.Clamp(x + c, 0, W - 1);
                R.Data.AsSpan((yy * W + xx) * 4, 4).CopyTo(refBlock.Slice((r + 1) * 16 + (c + 1) * 4, 4));
            }
        }
        Vector128<byte> r0 = Vector128.Create<byte>(refBlock.Slice(0, 16)), r1 = Vector128.Create<byte>(refBlock.Slice(16, 16)), r2 = Vector128.Create<byte>(refBlock.Slice(32, 16));
        var caps = Vector128.Create(capsA[0], capsA[1], capsA[2], capsA[3], capsA[0], capsA[1], capsA[2], capsA[3], capsA[0], capsA[1], capsA[2], capsA[3], capsA[0], capsA[1], capsA[2], capsA[3]);
        float xf = (float)x, yf = (float)y;
        fixed (float* planes = ctx.Planes) fixed (ushort* pc = cost)
        for (int k = 0; k < ctx.Others.Length; k++)
        {
            var img = ctx.Others[k]; var Wm = ctx.Warps[k]; var wq = ctx.Wq[k];
            int iw = img.W, ih = img.H; long stride = (long)iw * 4;
            if (img.Data.Length < stride * ih) { MatchingCostScalar(ctx, x, y, start, count, cost); return; }
            int x0 = 1, y0 = 1, x1 = iw - 2, y1 = ih - 2;   // rect + {1,1,−2,−2}
            float m0 = Wm[0], m1 = Wm[1], m2 = Wm[2], m4 = Wm[4], m5 = Wm[5], m6 = Wm[6], m8 = Wm[8], m9 = Wm[9], m10 = Wm[10], m12 = Wm[12], m13 = Wm[13], m14 = Wm[14];
            int wq0 = wq[0], wq1 = wq[1], wq2 = wq[2], wq3 = wq[3];
            int lix = int.MinValue, liy = 0, lhx = 0, lhy = 0; int lastCost = 0; bool haveLast = false;
            fixed (byte* pi = img.Data)
            for (int d = 0; d < count; d++)
            {
                float z = planes[start + d];
                float xz = (xf * z) * 1.0f, yz = (yf * z) * 1.0f;
                // p = ((z·W2 + W3) + xz·W0) + yz·W1  (columns of the flow matrix)
                float px = ((z * m8 + m12) + xz * m0) + yz * m4;
                float py = ((z * m9 + m13) + xz * m1) + yz * m5;
                float pz = ((z * m10 + m14) + xz * m2) + yz * m6;
                float inv = 1.0f / pz;
                float fx = px * inv + 0.25f, fy = py * inv + 0.25f;
                int ix, iy, hx, hy;
                if (d == 0)
                {
                    float cx = Math.Min(Math.Max(fx, (float)(x0)), (float)(x1 - 1)), cy = Math.Min(Math.Max(fy, (float)(y0)), (float)(y1 - 1));
                    ix = (int)cx; iy = (int)cy; hx = (int)(cx + cx) & 1; hy = (int)(cy + cy) & 1;
                    // A position whose 4×4 block is not inside the image goes to the scalar form.
                    if (ix < x0 || ix >= x1 || iy < y0 || iy >= y1) { MatchingCostScalar(ctx, x, y, start, count, cost); return; }
                }
                else
                {
                    ix = (int)fx; iy = (int)fy; hx = (int)(fx + fx) & 1; hy = (int)(fy + fy) & 1;
                    if (haveLast && ix == lix && iy == liy && hx == lhx && hy == lhy) { pc[d] = (ushort)(pc[d] + lastCost); continue; }
                    if (ix < x0 || ix >= x1 || iy < y0 || iy >= y1) { pc[d] = (ushort)(pc[d] + lastCost); continue; }
                }
                // 4 rows × 4 pixels of the target; a half-pixel position averages a row with the next row, a pixel with the next pixel
                byte* t = pi + ((long)(iy - 1) * iw + (ix - 1)) * 4;
                Vector128<byte> a0 = Vector128.Load(t), a1 = Vector128.Load(t + stride), a2 = Vector128.Load(t + 2 * stride);
                if (hy != 0) { var a3 = Vector128.Load(t + 3 * stride); a0 = Simd.Average(a0, a1); a1 = Simd.Average(a1, a2); a2 = Simd.Average(a2, a3); }
                if (hx != 0) { a0 = Simd.Average(a0, NextPixel(a0)); a1 = Simd.Average(a1, NextPixel(a1)); a2 = Simd.Average(a2, NextPixel(a2)); }
                // channel sums over the three rows, then over pixels 0..2: each sum stays in its byte
                var rowSum = (CappedDifference(a0, r0, caps) + CappedDifference(a1, r1, caps) + CappedDifference(a2, r2, caps)).AsUInt32();
                uint s = rowSum.GetElement(0) + rowSum.GetElement(1) + rowSum.GetElement(2);
                int S0 = (int)(s & 0xff), S1 = (int)((s >> 8) & 0xff), S2 = (int)((s >> 16) & 0xff), S3 = (int)(s >> 24);
                int cY = (S0 * wq0 + 16) >> 5, cU = (S1 * wq1 + 16) >> 5, cV = (S2 * wq2 + 16) >> 5, cA = (S3 * wq3 + 16) >> 5;
                int ck = (cY + cU) + (cV + cA);
                float cf = Math.Min((float)ck, 65535.0f); int costK = (int)cf;
                pc[d] = (ushort)(pc[d] + costK);
                lix = ix; liy = iy; lhx = hx; lhy = hy; lastCost = costK; haveLast = true;
            }
        }
    }

    /// <summary>The matching cost, one byte at a time. It has no limit on the caps.</summary>
    static void MatchingCostScalar(CostContext ctx, int x, int y, int start, int count, ushort[] cost)
    {
        Array.Clear(cost, 0, count);
        // reference 3×3 block, rows/cols clamped to the image
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

    /// <summary>`raw[d] = trunc(float(cost[d]) · (1/27f / nOthers))`; planes in [count, cap) = 255.</summary>
    public static void Normalise(ushort[] cost, int count, int cap, int nOthers, byte[] raw)
    {
        float f = BitConverter.Int32BitsToSingle(0x3d17b426) / (float)nOthers;
        for (int d = 0; d < count; d++) raw[d] = (byte)(int)((float)cost[d] * f);
        for (int d = count; d < cap; d++) raw[d] = 255;
    }
}
