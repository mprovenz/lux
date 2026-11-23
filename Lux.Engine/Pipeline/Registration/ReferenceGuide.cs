using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoISP::GetReferenceImage(img, raw, calib(img), view, neutral)`: the reference capture through a fresh display SoftISP
/// (defaults + the ten setters of <c>BuildTuning</c>), Stats on the whole frame, 256×256 tiles of the level-0 Bayer runner × 255
/// → `cvtps2dq` (RNE) → `packssdw/packuswb` into a vec4x8ui image of the capture size, then the 8-bit
/// `ImageWarp&lt;5,1,vec4x8ui,LensUndistortCRA&gt;` (<c>Rgba8Warp</c>) through the (view, module) aligned calibration
/// (M = I, crop (1,1), offset 0). The result is the guide of the dense-depth `UpsampleLayer`.
/// </summary>
public static class ReferenceGuide
{
    /// <summary>The display tuning (in call order): defaults then demosaicking default, hot_pixel_removal default,
    /// hot_pixel_leakage_removal none, auto_white_balance manual_color + neutral_color, tone_mapping default, color_correction default,
    /// cross_talk_correction ir_correction, lens_shading default, output.color_space srgb; after the Stats, hot_pixel_leakage_removal default
    /// when the capture flag <paramref name="hotPixelLeakage"/> is set.</summary>
    public static Tuning BuildTuning(float[] neutral, bool hotPixelLeakage)
    {
        var t = Tuning.LumenDefaults();
        t.Set("demosaicking.type", "default");
        t.Set("hot_pixel_removal.type", "default");
        t.Set("hot_pixel_leakage_removal.type", "none");
        t.Set("auto_white_balance.type", "manual_color");
        t.Set("auto_white_balance.neutral_color", new[] { (double)neutral[0], neutral[1], neutral[2] });
        t.Set("tone_mapping.type", "default");
        t.Set("color_correction.type", "default");
        t.Set("cross_talk_correction.type", "ir_correction");
        t.Set("lens_shading.type", "default");
        t.Set("output.color_space", "srgb");
        if (hotPixelLeakage) t.Set("hot_pixel_leakage_removal.type", "default");
        foreach (var k in (Environment.GetEnvironmentVariable("LUX_GUIDE_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) t.Set(k + ".type", "none");   // diagnostic
        return t;
    }

    public sealed class Result
    {
        public Rgba8Image Guide;          // the warped RGBA8 image
        public byte[] PreWarp = null!;    // the ISP'd RGBA8 image before the warp (Tiler output)
        public float[]? Float;            // LUX_GUIDE_DUMP: the float ISP output (w×h×4) before ×255
        public int W, H;
        public IspStats Stats = null!;
        public AlignedCalib Calib = null!;
        public Result(Rgba8Image g) { Guide = g; }
    }

    /// <summary>Build the guide. <paramref name="view"/> = `Apply(pose[ref], calib(img))`, <paramref name="module"/> = the reference
    /// module's CURRENT slot; <paramref name="keepFloat"/> keeps the pre-quantization float image for comparisons.</summary>
    public static Result Build(CapturedFrame frame, LumenProfile profile, float[] neutral, CameraCalib view, CameraCalib module, StereoImageBuilder.Distortion dist,
                               Action<string>? log = null, bool keepFloat = false, int maxTiles = int.MaxValue, int threads = 1)
    {
        int W = frame.Width, H = frame.Height;
        var isp = new SoftIsp(BuildTuning(neutral, frame.Info.HasHotPixelLeakageCalibration), profile);
        var stats = isp.ComputeStats(frame);   // rect 0 → whole frame
        log?.Invoke($"guide: stats neutral [{string.Join(" ", stats.Neutral.Select(v => v.ToString("R")))}] xy ({stats.NeutralXy.X:R},{stats.NeutralXy.Y:R}) irBlend {stats.IrBlend:R} cc M [{string.Join(" ", stats.CcSpace.M.Select(v => v.ToString("R")))}] out space {stats.OutSpace.Space}");
        var pre = new byte[W * H * 4];
        float[]? flt = keepFloat ? new float[(long)W * H * 4] : null;
        var v255 = Vector128.Create(255.0f);
        int tiles = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // the tiles are independent (disjoint output regions; SoftIsp serializes its lazy stage graph internally), so on threads > 1 they run in
        // parallel — the pixels are identical, only the "guide: N tiles" progress lines are dropped
        var tileList = Tiler.Rects(new RectI(0, 0, W, H), 256, 256).Take(maxTiles).ToList();   // Tiler::Run(size, {256,256})
        void Tile(RectI tile, bool quiet)
        {
            var img = isp.ProcessBayer(frame, tile, 0, quiet ? null : log);   // level-0 Bayer runner on raw ∩ tile
            if (img.Width != tile.Width || img.Height != tile.Height) throw new InvalidOperationException("guide tile size");
            for (int y = 0; y < img.Height; y++)
            {
                var row = img.Row(y);
                for (int x = 0; x < img.Width; x++)
                {
                    var v = row[x];
                    long o = ((long)(tile.Y0 + y) * W + tile.X0 + x) * 4;
                    if (flt is not null) { flt[o] = v.R; flt[o + 1] = v.G; flt[o + 2] = v.B; flt[o + 3] = v.A; }
                    var m = Vector128.Create(v.R, v.G, v.B, v.A) * v255;
                    var q = Simd.ConvertToVector128Int32(m);              // cvtps2dq (RNE; NaN/overflow → 0x80000000)
                    var w16 = Simd.PackSignedSaturate(q, q);
                    var b8 = Simd.PackUnsignedSaturate(w16, w16);
                    pre[o] = b8.GetElement(0); pre[o + 1] = b8.GetElement(1); pre[o + 2] = b8.GetElement(2); pre[o + 3] = b8.GetElement(3);
                }
            }
            if (!quiet) { tiles++; if (tiles % 16 == 0) log?.Invoke($"guide: {tiles} tiles {sw.Elapsed.TotalSeconds:F0}s"); }
        }
        if (threads > 1 && tileList.Count > 1) Parallel.ForEach(tileList, new ParallelOptions { MaxDegreeOfParallelism = threads }, t => Tile(t, true));
        else foreach (var t in tileList) Tile(t, false);
        var ac = AlignedCalib.Build(view, module, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix, M: new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f });   // M = I₃, crop (1,1), offset 0
        var warped = Rgba8Warp.Warp(pre, W, H, W, H, ac);
        log?.Invoke($"guide: {W}x{H} in {sw.Elapsed.TotalSeconds:F1}s");
        return new Result(new Rgba8Image(warped, W, H, W)) { PreWarp = pre, Float = flt, W = W, H = H, Stats = stats, Calib = ac };
    }
}

/// <summary>`ImageWarp&lt;5,1,vec4x8ui,LensUndistortCRA const&amp;,ExprConstScalar&lt;vec4x8ui&gt;&gt;` (128×128 tiles):
/// the same 64-phase 6-tap Lanczos-3 table and `LensUndistortCRA` geometry as the float warp (`StereoImage.Warp`), but (a) every tap is fetched with
/// clamp-to-edge (no fill value is ever written — the template's boundary mode 1), (b) the byte taps are converted to float per row and the six
/// column sums accumulate row by row `((((P0·ty0 + P1·ty1) + P2·ty2) + P3·ty3) + P4·ty4) + P5·ty5`, (c) the row combination is
/// `(C5·tx5 + C4·tx4) + ((C3·tx3 + C2·tx2) + (C1·tx1 + C0·tx0))`, (d) the store is `cvtps2dq` (RNE) → `packssdw` → `packuswb`.
/// "boundary size does match destination size!" when the size argument differs from the destination.</summary>
public static class Rgba8Warp
{
    public static unsafe byte[] Warp(byte[] src, int sw, int sh, int dw, int dh, AlignedCalib ac)
    {
        if (sw < 1 || sh < 1) throw new InvalidOperationException("empty source image!");
        if (src.Length < (long)sw * sh * 4) throw new IndexOutOfRangeException();   // the taps are read through pointers
        var tbl = StereoImage.LanczosTable();   // identical construction (sinf, (s2·(s1·3))/((x·x)·π²), sequential sum, ×(1/sum))
        var H = ac.H; var lut = ac.Lut; float cx = ac.Cx, cy = ac.Cy, sx = ac.Sx, sy = ac.Sy;
        float h0 = H[0], h1 = H[1], h2 = H[2], h3 = H[3], h4 = H[4], h5 = H[5], h6 = H[6], h7 = H[7], h8 = H[8];
        var dst = new byte[(long)dw * dh * 4];
        long* ro = stackalloc long[6]; int* co = stackalloc int[6];   // byte offsets of the six tap rows and the six tap columns
        fixed (byte* ps = src, pd = dst) fixed (float* pt = tbl)
        {
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float Xn = h2 + (h0 * x + h1 * y), Yn = h5 + (h3 * x + h4 * y);
                    float den = (h7 * y + h6 * x) + h8, w = 1.0f / den;
                    float dx = w * Xn - cx, dy = w * Yn - cy;
                    float r2 = (sy * dy) * (sy * dy) + (sx * dx) * (sx * dx);
                    float r;
                    if (r2 == 0f) r = 0f; else { float rs = IntelApprox.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar(); float S = r2 * rs; r = ((S * rs) + (-3.0f)) * (S * (-0.5f)); }
                    int idx = (int)r; if (idx >= 0x1000) idx = 0xfff;
                    float lu = lut[idx];
                    int px = (int)(((cx + (-2.0f)) + lu * dx) * 64.0f), py = (int)(((cy + (-2.0f)) + dy * lu) * 64.0f);
                    int i = px >> 6, fx = px & 63, j = py >> 6, fy = py & 63;
                    bool interior = i >= 0 && i <= sw - 6 && j >= 0 && j <= sh - 6;
                    // taps: interior → the 6×6 block at (i, j); else every row/column index clamped to the image
                    if (interior) for (int k = 0; k < 6; k++) { ro[k] = (long)(j + k) * sw * 4; co[k] = (i + k) * 4; }
                    else for (int k = 0; k < 6; k++) { ro[k] = (long)Math.Clamp(j + k, 0, sh - 1) * sw * 4; co[k] = Math.Clamp(i + k, 0, sw - 1) * 4; }
                    // column sums, row by row: bytes to float, times the broadcast y-tap, then added
                    float* ty = pt + fy * 6, tx = pt + fx * 6;
                    byte* p = ps + ro[0]; var t = Vector128.Create(ty[0]);
                    Vector128<float> c0 = Lanes(p + co[0]) * t, c1 = Lanes(p + co[1]) * t, c2 = Lanes(p + co[2]) * t, c3 = Lanes(p + co[3]) * t, c4 = Lanes(p + co[4]) * t, c5 = Lanes(p + co[5]) * t;
                    for (int rr = 1; rr < 6; rr++)
                    {
                        p = ps + ro[rr]; t = Vector128.Create(ty[rr]);
                        c0 = c0 + Lanes(p + co[0]) * t; c1 = c1 + Lanes(p + co[1]) * t; c2 = c2 + Lanes(p + co[2]) * t;
                        c3 = c3 + Lanes(p + co[3]) * t; c4 = c4 + Lanes(p + co[4]) * t; c5 = c5 + Lanes(p + co[5]) * t;
                    }
                    var acc = (c5 * Vector128.Create(tx[5]) + c4 * Vector128.Create(tx[4]))
                            + ((c3 * Vector128.Create(tx[3]) + c2 * Vector128.Create(tx[2]))
                               + (c1 * Vector128.Create(tx[1]) + c0 * Vector128.Create(tx[0])));
                    var q = Simd.ConvertToVector128Int32(acc);
                    var w16 = Simd.PackSignedSaturate(q, q);
                    var b8 = Simd.PackUnsignedSaturate(w16, w16);
                    Unsafe.WriteUnaligned(pd + ((long)y * dw + x) * 4, b8.AsUInt32().ToScalar());
                }
        }
        return dst;
    }

    /// <summary>The four bytes of a pixel as four floats.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe Vector128<float> Lanes(byte* p)
        => Vector128.ConvertToSingle(Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(p)).AsByte())).AsInt32());
}
