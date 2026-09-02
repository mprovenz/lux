using System.Runtime.InteropServices;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>
/// The tele module's `SourceImageCache` (0x188 B, ctor `1804dd930`, spec `a-resamp.md` §7.4) = a one-level
/// `TileCache&lt;vec4x16f&gt;` (512×512 tiles) of the module reprojected into the B-cam *view* camera at canvas scale σ, size
/// `(trunc(validW·σ), trunc(validH·σ))`: tile generator `SourceImageCache::lambda_0` (`1804dddd0`) → `ImageLensUndistort&lt;2, vec4x32f,
/// LensUndistortCRA&gt;` (`FUN_180304790`: border AABB of the A.8 map, truncate, ±4, clamp to the frame) over the source lambda_1 (`1804de1a0`:
/// level-0 module SoftISP (config level 1) on the halo-grown rect, cropped back) with the `ImageWarpClamped&lt;2&gt;` kernel (`180305960` = the
/// `1804e49f0` kernel family, fill (0,0,0,0) ⇒ alpha = validity), stored as software round-toward-zero halves (`FUN_1800e8150`) and read back
/// exactly (`FUN_1800e86c0`). No neutral and no gain in the cache; the `initResAmp::lambda_3` generator (§1.1/§1.5) applies
/// `sqrt(max(src·(g,g,g,1), 0))` on the way out, and `FUN_18044b680` zero-fills whatever a render rect asks for outside the level.
/// </summary>
public sealed class TeleLevel0Cache
{
    public const int TileSize = 512;                    // ImageCaches+0x38 (DAT_1808378b0)
    public readonly int CamId; public readonly string Name;
    public readonly (int W, int H) Dims;                // levelDims[0] = FUN_1804f0e80: ((int)((float)validW·σx), (int)((float)validH·σy))
    public readonly (int X, int Y) Grid;                // max(1, (256 + W) / 512)
    public readonly AlignedCalib Calib;                 // cache+0xc0 LensUndistortCRA: H = (K_mod·inv(R_view·R_modᵀ))·inv(σ·K_view), CRA centre, polynomial LUT
    public readonly CapturedFrame Frame;
    public readonly SoftIsp Isp;                        // cache+0x110, FUN_18050cc30(profile, isp, cfgLevel 1, cap, (cct, tint))
    public readonly int Halo;                           // cache+0xb8 = FUN_18050cbf0(profile, sensor_analog_gain)
    public readonly float Gain;                         // FUN_18010fc80(camera, &id) — captured by lambda_3 (+0x10)
    public Action<string>? Log;
    /// <summary>Diagnostics: every grown-rect ISP output (the `FUN_1803dcd90` result) keyed by the grown rect.</summary>
    public Dictionary<RectI, Image<Vec4F>>? IspOutputs;
    /// <summary>Diagnostics: replace the module ISP by an externally supplied output for a grown rect (e.g. cp.dll's own output for that rect) to verify the tile/warp path alone.</summary>
    public Func<RectI, Image<Vec4F>?>? SourceOverride;
    readonly Dictionary<(int, int), ushort[]> _tiles = new();
    readonly float[] _table = WarpResample.BuildTable();

    public TeleLevel0Cache(LriFile lri, string moduleName, CameraCalib view, CameraCalib module, (int W, int H) validSize, (float X, float Y) scale,
                           RendererProfile profile, LumenProfile colour, float cct, float tint)
    {
        Name = moduleName; CamId = (int)lri.Modules[moduleName].Module.Id;
        Dims = ((int)((float)validSize.W * scale.X), (int)((float)validSize.H * scale.Y));
        Grid = (Math.Max(1, (TileSize / 2 + Dims.W) / TileSize), Math.Max(1, (TileSize / 2 + Dims.H) / TileSize));
        Frame = CapturedFrame.Load(lri, moduleName);
        // The tele (non-reference-group) frames are decoded by FUN_1804d3d00 → FUN_180126010 (decode only): the per-frame black estimate
        // (FUN_180125d10, run by the FUN_18020a6d0 loader path) never happens for them, so every reader of CapturedImage+0xb4 / Stats+0x198
        // sees the sensor-record black (42.0 on the AR1335). Verified on cp.dll's tele-ISP stage-skip reference runs t2/t3 (CNR/desat/LS with the 43.17
        // estimate: max|d| 6e-4; with the sensor black: ≤1e-5).
        Frame = new CapturedFrame { Raw = Frame.Raw, Width = Frame.Width, Height = Frame.Height, Info = Frame.Info with { FrameBlack = float.NaN }, Module = Frame.Module, Header = Frame.Header };
        if (!Frame.Info.IsColour) throw new NotSupportedException("Super-res does not support mono modules!");
        var dist = StereoImageBuilder.DistortionOf(lri, moduleName);
        Calib = AlignedCalib.Build(view, module, scale.X, scale.Y, Frame.Info.DataScaleX, Frame.Info.DataScaleY, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);
        Halo = global::Lux.Engine.Pipeline.BayerFusion.FusionSensorTuning.Halo(Frame.Info.AnalogGain);
        int cfg = Environment.GetEnvironmentVariable("LUX_TELE_CFG") is string cs ? int.Parse(cs) : 1;   // diagnostic override of the config level (Lumen: 1)
        var tuning = ModuleIspTuning.Build(cfg, profile, Frame.Info, cct, tint);
        if (Environment.GetEnvironmentVariable("LUX_TELE_SET") is string ovs) foreach (var ov in ovs.Split(';', StringSplitOptions.RemoveEmptyEntries)) tuning = tuning.Apply(ov);   // diagnostic tuning overrides key=value;…
        // the module's own SoftISP: manual_temp at the capture (cct, tint) through the MODULE'S colour calibration (lambda_21 on this CapturedImage)
        Isp = new SoftIsp(tuning, global::Lux.Engine.Pipeline.BayerFusion.PackedBayerFusion.CameraProfile(lri, Frame.Module.Id));
        Isp.ComputeStats(Frame);
        if (Environment.GetEnvironmentVariable("LUX_TELE_DEBUG") == "1")
        {
            var st = Isp.CurrentStats!; var refIsp = new SoftIsp(tuning, colour); var rs = refIsp.ComputeStats(Frame);
            Console.WriteLine($"  [tele {Name}] cct {cct:R} tint {tint:R} neutral(module profile) {string.Join(" ", st.Neutral.Select(v => v.ToString("R")))} neutral(ref profile) {string.Join(" ", rs.Neutral.Select(v => v.ToString("R")))} AsShot {string.Join(" ", lri.LumenNeutral.Select(v => v.ToString("R")))} black {st.SensorBlack:R} irBlend {st.IrBlend:R} grain_power {tuning.Num("tone_mapping.grain_power"):R} grain_sigma {tuning.Num("tone_mapping.grain_sigma"):R} sharpening {tuning.Num("tone_mapping.sharpening"):R} sharpening_scale {(tuning.Has("tone_mapping.sharpening_scale") ? tuning.Num("tone_mapping.sharpening_scale").ToString("R") : "-")} exposureRatio {Frame.Info.ExposureRatio:R}");
        }
        Gain = ModuleGain(lri, lri.ReferenceModule, moduleName);
    }

    /// <summary>`FUN_18010fc80(camera, &amp;moduleId)` from the LRI: `((g_ref·(float)e_ref) / (g_mod·(float)e_mod)) · (rb_mod / rb_ref)` (vignetting
    /// `relative_brightness` ratio only when both records exist and the module is colour).</summary>
    public static float ModuleGain(LriFile lri, string refName, string modName)
    {
        var rm = lri.Modules[refName].Module; var mm = lri.Modules[modName].Module;
        float? Rb(Ltpb.CameraID id) { foreach (var c in Calibration.ForModule(lri.Header, id)) if (c.Vignetting is not null) return c.Vignetting.HasRelativeBrightness ? c.Vignetting.RelativeBrightness : null; return null; }
        bool colour = mm.SensorBayerRedOverride is null || (mm.SensorBayerRedOverride.X | mm.SensorBayerRedOverride.Y) >= 0;
        return StereoColorTransfer.ExposureGain(rm.SensorAnalogGain, rm.SensorExposure, Rb(rm.Id), mm.SensorAnalogGain, mm.SensorExposure, Rb(mm.Id), colour);
    }

    /// <summary>`FUN_1804bcde0`-style tile dims: 512 unless the last tile of the row/column, which absorbs the remainder.</summary>
    public (int W, int H) TileDims(int tx, int ty)
    {
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = tx == Grid.X - 1 ? Math.Min(Dims.W, x0 + 2 * TileSize) - Math.Max(x0, 0) : TileSize;
        int h = ty == Grid.Y - 1 ? Math.Min(Dims.H, y0 + 2 * TileSize) - Math.Max(y0, 0) : TileSize;
        return (w, h);
    }

    /// <summary>`FUN_180304500`: min/max of the map over the border of `rect` (columns x0 and x1−1 for every row, then rows y0 and y1−1 for every column).</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) BorderAabb(RectI rect)
    {
        float minx = float.MaxValue, miny = float.MaxValue, maxx = -float.MaxValue, maxy = -float.MaxValue;
        void Acc(int x, int y)
        {
            var p = Calib.Map((float)x, (float)y, 0);
            if (p.X <= minx) minx = p.X;
            if (p.Y <= miny) miny = p.Y;
            if (maxx <= p.X) maxx = p.X;
            if (maxy <= p.Y) maxy = p.Y;
        }
        for (int y = rect.Y0; y < rect.Y1; y++) { Acc(rect.X0, y); Acc(rect.X1 - 1, y); }
        for (int x = rect.X0; x < rect.X1; x++) { Acc(x, rect.Y0); Acc(x, rect.Y1 - 1); }
        return (minx, miny, maxx, maxy);
    }

    /// <summary>`FUN_180304790` source rect: `cvttps2dq(aabb)`, then `(x0 − 4, y0 − 4, x1 + 4, y1 + 4)` with `x0,y0 = max(·, 0)` and `x1,y1 = min(·, frame dims)`.</summary>
    public RectI SourceRect(RectI rect)
    {
        var (minx, miny, maxx, maxy) = BorderAabb(rect);
        int x0 = (int)minx - 4, y0 = (int)miny - 4, x1 = (int)maxx + 4, y1 = (int)maxy + 4;
        if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
        if (x1 > Frame.Width) x1 = Frame.Width; if (y1 > Frame.Height) y1 = Frame.Height;
        return new RectI(x0, y0, x1, y1);
    }

    /// <summary>`SourceImageCache::lambda_0::lambda_1` (`1804de1a0`, single-frame path): `FUN_180011d50` rounds the requested rect outward to even
    /// (`(x0 &amp; ~1, y0 &amp; ~1, (x1 + 1) &amp; ~1, (y1 + 1) &amp; ~1)`, mask `0x180681da0`), grows it by `halo &gt;&gt; 1` (left/top) and `max(halo − 1, 0) &gt;&gt; 1`
    /// (right/bottom), each limited by the frame edge, runs the level-0 module SoftISP (`FUN_1803dcd90`) over the grown rect and crops the result
    /// back to the even rect and then to the requested rect (`FUN_180012530` copies the view).</summary>
    public Image<Vec4F> Source(RectI srcRect)
    {
        int ax0 = srcRect.X0 & ~1, ay0 = srcRect.Y0 & ~1, ax1 = (srcRect.X1 + 1) & ~1, ay1 = (srcRect.Y1 + 1) & ~1;
        int hl = Halo >> 1, hr = Math.Max(Halo - 1, 0) >> 1;
        int l = Math.Min(hl, ax0), t = Math.Min(hl, ay0);
        int r = Math.Min(hr, Frame.Width - ax1), b = Math.Min(hr, Frame.Height - ay1);
        var grown = new RectI(ax0 - l, ay0 - t, ax1 + r, ay1 + b);
        if (Environment.GetEnvironmentVariable("LUX_TELE_ONLYRECT") is string orr)   // diagnostic: run the ISP only for one grown rect (x0,y0,x1,y1), zeros elsewhere
        { var q = orr.Split(',').Select(int.Parse).ToArray(); if (grown != new RectI(q[0], q[1], q[2], q[3])) return new Image<Vec4F>(grown).View(srcRect); }
        var img = SourceOverride?.Invoke(grown) ?? Isp.ProcessBayer(Frame, grown, 0, Log);
        if (img.Rect != grown) throw new InvalidOperationException($"tele ISP output rect {img.Rect} != grown rect {grown}");
        IspOutputs?.TryAdd(grown, img);
        if (Environment.GetEnvironmentVariable("LUX_TELE_ISPDUMP") is string dp)   // diagnostic twin of cp.dll's tele-ISP intermediate hook (0x4de3f4): the whole grown-rect ISP output
        {
            int gw = img.Width, gh = img.Height; var mb = new byte[16 + gw * gh * 16];
            BitConverter.GetBytes(gw).CopyTo(mb, 0); BitConverter.GetBytes(gh).CopyTo(mb, 4); BitConverter.GetBytes(gw).CopyTo(mb, 8); BitConverter.GetBytes(16).CopyTo(mb, 12);
            for (int y = 0; y < gh; y++) MemoryMarshal.AsBytes(img.Row(y)).CopyTo(mb.AsSpan(16 + y * gw * 16, gw * 16));
            File.WriteAllBytes($"{dp}_{Name}_isp_{grown.X0}_{grown.Y0}_{grown.X1}_{grown.Y1}.bin", mb);
        }
        return img.View(new RectI(ax0, ay0, ax1, ay1)).View(srcRect);
    }

    /// <summary>`ImageLensUndistort&lt;2, vec4x32f, LensUndistortCRA&gt;(calib, rect, source)` (`FUN_180304790`): source rect → generator → `ImageWarpClamped&lt;2&gt;`
    /// with the `((cx + −1) + lu·dx) − x0` kernel (`180305960`), fill (0,0,0,0); an empty source rect yields zeros.</summary>
    public float[] LensUndistort(RectI rect)
    {
        int w = rect.Width, h = rect.Height;
        var dst = new float[w * h * 4];
        var src0 = SourceRect(rect);
        if (src0.Width <= 0 || src0.Height <= 0) return dst;
        var img = Source(src0);
        int sw = img.Width, sh = img.Height;
        var src = new float[sw * sh * 4];
        for (int y = 0; y < sh; y++) MemoryMarshal.AsBytes(img.Row(y)).CopyTo(MemoryMarshal.AsBytes(src.AsSpan(y * sw * 4, sw * 4)));
        WarpResample.Warp(Calib, 0, new WarpResample.Source(src, sw, 0, 0, sw, sh), src0.X0, src0.Y0, rect.X0, rect.Y0, dst, w, h, new float[4], _table, inlinedMap: true);
        Log?.Invoke($"tele L0 {Name}: tile {rect} source {src0}");
        return dst;
    }

    /// <summary>`SourceImageCache::lambda_0` (`1804dddd0`): the tile rect `(512·tx, 512·ty, +w, +h)`, the undistort, then `ImageConvertPixelType&lt;vec4x16f, vec4x32f&gt;`
    /// = software RTZ half in all 4 lanes.</summary>
    ushort[] Generate(int tx, int ty)
    {
        var (w, h) = TileDims(tx, ty);
        var f = LensUndistort(new RectI(tx * TileSize, ty * TileSize, tx * TileSize + w, ty * TileSize + h));
        var t = new ushort[f.Length];
        for (int i = 0; i < f.Length; i++) t[i] = Half16.FromFloat(f[i]);
        return t;
    }

    ushort[] Tile(int tx, int ty)
    {
        var key = (tx, ty);
        if (!_tiles.TryGetValue(key, out var t)) { t = Generate(tx, ty); _tiles[key] = t; }
        return t;
    }

    /// <summary>`TileCache&lt;vec4x16f&gt;::renderROI&lt;vec4x32f&gt;(cache+8, out, rect, 0)` (`1804bdfe0`): gather the tiles overlapping `rect` (level-0 pixels,
    /// must lie inside the level) as float RGBA via the exact half→float conversion (alpha from the stored half).</summary>
    public float[] RenderRoi(RectI rect)
    {
        if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > Dims.W || rect.Y1 > Dims.H) throw new ArgumentException("Requested ROI is out-of-bounds!");
        int tx0 = Math.Min(rect.X0 / TileSize, Grid.X - 1), tx1 = Math.Min((rect.X1 - 1) / TileSize, Grid.X - 1);
        int ty0 = Math.Min(rect.Y0 / TileSize, Grid.Y - 1), ty1 = Math.Min((rect.Y1 - 1) / TileSize, Grid.Y - 1);
        if (tx1 < tx0 || ty1 < ty0) throw new InvalidOperationException("No tiles in ROI!");
        int rw = rect.Width, rh = rect.Height; var outp = new float[rw * rh * 4];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var t = Tile(tx, ty); var (tw, th) = TileDims(tx, ty);
                int x0 = Math.Max(rect.X0, tx * TileSize), y0 = Math.Max(rect.Y0, ty * TileSize), x1 = Math.Min(rect.X1, tx * TileSize + tw), y1 = Math.Min(rect.Y1, ty * TileSize + th);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int si = ((y - ty * TileSize) * tw + (x - tx * TileSize)) * 4, di = ((y - rect.Y0) * rw + (x - rect.X0)) * 4;
                        outp[di] = Half16.ToFloat(t[si]); outp[di + 1] = Half16.ToFloat(t[si + 1]); outp[di + 2] = Half16.ToFloat(t[si + 2]); outp[di + 3] = Half16.ToFloat(t[si + 3]);
                    }
            }
        return outp;
    }

    /// <summary>`initResAmp::lambda_3` (`1804e4550`): `renderROI` then `FUN_1804e4600`: `dst = sqrtps(maxps(mulps(src, (g,g,g,1)), 0))` (NaN → 0).</summary>
    public float[] Generator(RectI rect)
    {
        var v = RenderRoi(rect);
        float g = Gain;
        for (int i = 0; i < v.Length; i += 4)
        {
            float r = v[i] * g, gg = v[i + 1] * g, b = v[i + 2] * g, a = v[i + 3] * 1.0f;
            v[i] = MathF.Sqrt(r > 0f ? r : 0f); v[i + 1] = MathF.Sqrt(gg > 0f ? gg : 0f); v[i + 2] = MathF.Sqrt(b > 0f ? b : 0f); v[i + 3] = MathF.Sqrt(a > 0f ? a : 0f);
        }
        return v;
    }

    /// <summary>The `+0x258` entry of `initResAmp` for this module: an <see cref="ImageGenerator"/> of the level-0 dims wrapping <see cref="RenderRoi"/> with
    /// `FUN_1804e4600` (gain + √) — what `ImageResolutionAmp` renders through `FUN_18044b680`.</summary>
    public ImageGenerator ToGenerator() => ImageGenerator.GainSqrtOf(Dims.W, Dims.H, Gain, RenderRoi);

    /// <summary>`FUN_18044b680(out, gen, rect)` (§4.0): an image of `rect`'s size, the generator rendered over `rect ∩ [0,W)×[0,H)`, zero (RGBA) elsewhere.</summary>
    public float[] RenderGenerator(RectI rect)
    {
        int w = rect.Width, h = rect.Height; var outp = new float[w * h * 4];
        var c = new RectI(Math.Max(rect.X0, 0), Math.Max(rect.Y0, 0), Math.Min(rect.X1, Dims.W), Math.Min(rect.Y1, Dims.H));
        if (c.Width <= 0 || c.Height <= 0) return outp;
        var g = Generator(c); int cw = c.Width;
        for (int y = 0; y < c.Height; y++)
            Array.Copy(g, y * cw * 4, outp, ((y + c.Y0 - rect.Y0) * w + (c.X0 - rect.X0)) * 4, cw * 4);
        return outp;
    }
}
