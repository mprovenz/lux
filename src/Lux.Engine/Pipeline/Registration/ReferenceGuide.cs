using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// `lt::StereoISP::GetReferenceImage(img, raw, calib(img), view, neutral)` (`180324ef0`, spec `a-reference-guide.md`): the
/// reference capture through a fresh display SoftISP (defaults + the ten setters of §2), Stats on the whole frame, 256×256 tiles of the level-0
/// Bayer runner (`FUN_1803d88e0`) × 255 → `cvtps2dq` (RNE) → `packssdw/packuswb` into a vec4x8ui image of the capture size, then the 8-bit
/// `ImageWarp&lt;5,1,vec4x8ui,LensUndistortCRA&gt;` (`FUN_180326240` → `180329b90`) through the (view, module) aligned calibration (`FUN_180185030`,
/// M = I, crop (1,1), offset 0). Result = `StereoAsyncAPI+0x1f8`, the guide of the dense-depth `UpsampleLayer`.
/// </summary>
public static class ReferenceGuide
{
    /// <summary>The display tuning (`180324ef0` L20–140, in call order): `FUN_1803d8360` defaults then demosaicking default, hot_pixel_removal default,
    /// hot_pixel_leakage_removal none, auto_white_balance manual_color + neutral_color, tone_mapping default, color_correction default,
    /// cross_talk_correction ir_correction, lens_shading default, output.color_space srgb; after the Stats, hot_pixel_leakage_removal default
    /// when the capture flag `FUN_180125930(img)+0x98` is set (`FUN_180126100(img)` = the lazy decode).</summary>
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
        public Rgba8Image Guide;          // the warped RGBA8 image (api+0x1f8)
        public byte[] PreWarp = null!;    // the ISP'd RGBA8 image before the warp (Tiler output)
        public float[]? Float;            // LUX_GUIDE_DUMP: the float ISP output (w×h×4) before ×255
        public int W, H;
        public IspStats Stats = null!;
        public AlignedCalib Calib = null!;
        public Result(Rgba8Image g) { Guide = g; }
    }

    /// <summary>Build the guide. <paramref name="view"/> = `FUN_1802e1580(pose[ref], calib(img))`, <paramref name="module"/> = `FUN_180307b30(img)` (the reference
    /// module's CURRENT slot); <paramref name="keepFloat"/> keeps the pre-quantisation float image for comparisons.</summary>
    public static Result Build(CapturedFrame frame, LumenProfile profile, float[] neutral, CameraCalib view, CameraCalib module, StereoImageBuilder.Distortion dist,
                               Action<string>? log = null, bool keepFloat = false, int maxTiles = int.MaxValue)
    {
        int W = frame.Width, H = frame.Height;
        var isp = new SoftIsp(BuildTuning(neutral, frame.Info.HasHotPixelLeakageCalibration), profile);
        var stats = isp.ComputeStats(frame);   // FUN_1803d8700(isp, &stats, img, rect 0 → whole frame) + FUN_1803de110
        log?.Invoke($"guide: stats neutral [{string.Join(" ", stats.Neutral.Select(v => v.ToString("R")))}] xy ({stats.NeutralXy.X:R},{stats.NeutralXy.Y:R}) irBlend {stats.IrBlend:R} cc M [{string.Join(" ", stats.CcSpace.M.Select(v => v.ToString("R")))}] out space {stats.OutSpace.Space}");
        var pre = new byte[W * H * 4];
        float[]? flt = keepFloat ? new float[(long)W * H * 4] : null;
        var v255 = Vector128.Create(255.0f);
        int tiles = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var tile in Tiler.Rects(new RectI(0, 0, W, H), 256, 256))   // Tiler::Run(size, {256,256}) — lambda 180329610
        {
            if (tiles++ >= maxTiles) break;
            var img = isp.ProcessBayer(frame, tile, 0, log);   // FUN_1803d88e0(isp, dst(tile size), raw ∩ tile, img, tile, empty)
            if (img.Width != tile.Width || img.Height != tile.Height) throw new InvalidOperationException("guide tile size");
            for (int y = 0; y < img.Height; y++)
            {
                var row = img.Row(y);
                for (int x = 0; x < img.Width; x++)
                {
                    var v = row[x];
                    long o = ((long)(tile.Y0 + y) * W + tile.X0 + x) * 4;
                    if (flt is not null) { flt[o] = v.R; flt[o + 1] = v.G; flt[o + 2] = v.B; flt[o + 3] = v.A; }
                    var m = Vector128.Create(v.R, v.G, v.B, v.A) * v255;   // mulps (DAT_180682420)
                    var q = Sse2.ConvertToVector128Int32(m);              // cvtps2dq (RNE; NaN/overflow → 0x80000000)
                    var w16 = Sse2.PackSignedSaturate(q, q);              // packssdw
                    var b8 = Sse2.PackUnsignedSaturate(w16, w16);         // packuswb
                    pre[o] = b8.GetElement(0); pre[o + 1] = b8.GetElement(1); pre[o + 2] = b8.GetElement(2); pre[o + 3] = b8.GetElement(3);
                }
            }
            if (tiles % 16 == 0) log?.Invoke($"guide: {tiles} tiles {sw.Elapsed.TotalSeconds:F0}s");
        }
        var ac = AlignedCalib.Build(view, module, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);   // FUN_180185030(ac, module, view, I, img, (1,1), 0)
        var warped = Rgba8Warp.Warp(pre, W, H, W, H, ac);   // FUN_180326240(out, img8, &img8.size, &ac)
        log?.Invoke($"guide: {W}x{H} in {sw.Elapsed.TotalSeconds:F1}s");
        return new Result(new Rgba8Image(warped, W, H, W)) { PreWarp = pre, Float = flt, W = W, H = H, Stats = stats, Calib = ac };
    }
}

/// <summary>`ImageWarp&lt;5,1,vec4x8ui,LensUndistortCRA const&amp;,ExprConstScalar&lt;vec4x8ui&gt;&gt;` (`180329b90`, lambda_1 `18032a5e0`, 128×128 tiles):
/// the same 64-phase 6-tap Lanczos-3 table and `LensUndistortCRA` geometry as the float warp (`StereoImage.Warp`), but (a) every tap is fetched with
/// clamp-to-edge (no fill value is ever written — the template's boundary mode 1), (b) the byte taps are converted to float per row and the six
/// column sums accumulate row by row `((((P0·ty0 + P1·ty1) + P2·ty2) + P3·ty3) + P4·ty4) + P5·ty5`, (c) the row combination is
/// `(C5·tx5 + C4·tx4) + ((C3·tx3 + C2·tx2) + (C1·tx1 + C0·tx0))`, (d) the store is `cvtps2dq` (RNE) → `packssdw` → `packuswb`.
/// "boundary size does match destination size!" when the size argument differs from the destination.</summary>
public static class Rgba8Warp
{
    public static byte[] Warp(byte[] src, int sw, int sh, int dw, int dh, AlignedCalib ac)
    {
        if (sw < 1 || sh < 1) throw new InvalidOperationException("empty source image!");
        var tbl = StereoImage.LanczosTable();   // identical construction (sinf, (s2·(s1·3))/((x·x)·π²), sequential sum, ×(1/sum))
        var H = ac.H; var lut = ac.Lut; float cx = ac.Cx, cy = ac.Cy, sx = ac.Sx, sy = ac.Sy;
        var dst = new byte[(long)dw * dh * 4];
        var gather = new uint[36];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float Xn = H[2] + (H[0] * x + H[1] * y), Yn = H[5] + (H[3] * x + H[4] * y);
                float den = (H[7] * y + H[6] * x) + H[8], w = 1.0f / den;
                float dx = w * Xn - cx, dy = w * Yn - cy;
                float r2 = (sy * dy) * (sy * dy) + (sx * dx) * (sx * dx);
                float r;
                if (r2 == 0f) r = 0f; else { float rs = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(r2)).ToScalar(); float S = r2 * rs; r = ((S * rs) + (-3.0f)) * (S * (-0.5f)); }
                int idx = (int)r; if (idx >= 0x1000) idx = 0xfff;
                float lu = lut[idx];
                int px = (int)(((cx + (-2.0f)) + lu * dx) * 64.0f), py = (int)(((cy + (-2.0f)) + dy * lu) * 64.0f);
                int i = px >> 6, fx = px & 63, j = py >> 6, fy = py & 63;
                bool interior = i >= 0 && i <= sw - 6 && j >= 0 && j <= sh - 6;
                // taps: interior → the 6×6 block at (i, j); else every row/column index clamped to the image
                for (int rr = 0; rr < 6; rr++)
                {
                    int yy = interior ? j + rr : Math.Clamp(j + rr, 0, sh - 1);
                    long ro = (long)yy * sw;
                    for (int cc = 0; cc < 6; cc++)
                    {
                        int xx = interior ? i + cc : Math.Clamp(i + cc, 0, sw - 1);
                        long o = (ro + xx) * 4;
                        gather[rr * 6 + cc] = (uint)(src[o] | (src[o + 1] << 8) | (src[o + 2] << 16) | (src[o + 3] << 24));
                    }
                }
                // column sums, row by row (pmovzxbw/pmovzxwd/cvtdq2ps then mulps by the broadcast y-tap, addps)
                var C = new Vector128<float>[6];
                for (int cc = 0; cc < 6; cc++) C[cc] = Lanes(gather[cc]) * Vector128.Create(tbl[fy * 6]);
                for (int rr = 1; rr < 6; rr++)
                {
                    var ty = Vector128.Create(tbl[fy * 6 + rr]);
                    for (int cc = 0; cc < 6; cc++) C[cc] = C[cc] + Lanes(gather[rr * 6 + cc]) * ty;
                }
                var acc = (C[5] * Vector128.Create(tbl[fx * 6 + 5]) + C[4] * Vector128.Create(tbl[fx * 6 + 4]))
                        + ((C[3] * Vector128.Create(tbl[fx * 6 + 3]) + C[2] * Vector128.Create(tbl[fx * 6 + 2]))
                           + (C[1] * Vector128.Create(tbl[fx * 6 + 1]) + C[0] * Vector128.Create(tbl[fx * 6])));
                var q = Sse2.ConvertToVector128Int32(acc);
                var w16 = Sse2.PackSignedSaturate(q, q);
                var b8 = Sse2.PackUnsignedSaturate(w16, w16);
                long d = ((long)y * dw + x) * 4;
                dst[d] = b8.GetElement(0); dst[d + 1] = b8.GetElement(1); dst[d + 2] = b8.GetElement(2); dst[d + 3] = b8.GetElement(3);
            }
        return dst;
    }

    static Vector128<float> Lanes(uint p) => Vector128.Create((float)(p & 0xff), (float)((p >> 8) & 0xff), (float)((p >> 16) & 0xff), (float)(p >> 24));
}
