using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// `FusionCacheBayer` (spec `a4ce3d1abcbdfdc45.md` §1, §2, §5): the level-1 source of the `PipelineCache` — 512×512 tiles of
/// `PackedBayerFusion::process` (fused full-res float Bayer + uint8 weight), and `render(rect)` = the tiles over the halo-grown rect → STD plane
/// (`DAT_1806b5110[w8]·rsqrtNR(noise_scale)`) → the pipeline-5 BayerFloat ISP (`FUN_1803dc980`) on the un-grown rect.
/// </summary>
public sealed class FusionCacheBayer
{
    public PackedBayerFusion Fusion { get; }
    public CapturedFrame RefFrame { get; }
    public SoftIsp Isp { get; }
    public IspStats Stats { get; }
    public int Halo { get; }
    public float NoiseScale { get; }
    public int Width => RefFrame.Width;
    public int Height => RefFrame.Height;
    public Action<string>? Log { get; set; }
    readonly Dictionary<(int, int), (RectI Rect, float[] Fused, byte[] W8)> _tiles = new();

    /// <summary>`FusionCacheBase` ctor: pipeline-5 tuning (`ModuleIspTuning.Build(5)` incl. the sensor-tuning row overrides), Stats from the reference
    /// capture with the AsShot neutral (`setNeutral` `FUN_180504100`), halo by gain, noise scale from the sensor-tuning row (§2.1).</summary>
    public FusionCacheBayer(LriFile lri, CapturedFrame refFrame, PackedBayerFusion fusion, RendererProfile profile, float cct, float tint, Action<string>? log = null)
    {
        RefFrame = refFrame; Fusion = fusion; Log = log;
        var tuning = ModuleIspTuning.Build(5, profile, refFrame.Info, cct, tint);
        Isp = new SoftIsp(tuning, Color.LumenProfile.Compute(lri));
        var st = Isp.ComputeStats(refFrame);
        Array.Copy(lri.LumenNeutral, st.Neutral, 3);   // FUN_180504100: auto_white_balance.neutral_color = AsShot neutral, Stats neutral overwritten in place
        Stats = st;
        Isp.Set("auto_white_balance.neutral_color", new[] { (double)lri.LumenNeutral[0], lri.LumenNeutral[1], lri.LumenNeutral[2] }).UseStats(Stats);
        Halo = PackedBayerFusion.Halo(refFrame.Info.AnalogGain);
        var row = FusionSensorTuning.Select((int)profile, resAmpEnabled: RendererProfiles.IsDesktop(profile), fusion.StreamHalfScale, (int)refFrame.Info.Sensor, refFrame.Info.AnalogGain);
        NoiseScale = row.NoiseScale;
        // 1805018e0 step 1: +0x18 = a capture in the reference group with red position (x|y) < 0 (only when stream+0x14 == 0)
        if (!fusion.StreamHalfScale)
            foreach (var kv in lri.Modules)
            {
                var m = kv.Value.Module; var red = m.SensorBayerRedOverride;
                if (PackedBayerFusion.Group((int)m.Id) == PackedBayerFusion.Group(fusion.RefCamId) && red is not null && (red.X | red.Y) < 0) { HasMono = true; break; }
            }
        if (Environment.GetEnvironmentVariable("LUX_NO_MONO") == "1") { Console.Error.WriteLine("[diagnostic] LUX_NO_MONO: colour branch forced — output is NOT Lumen-faithful"); HasMono = false; }
        if (HasMono)
        {
            // 1805018e0 L695–761: the pipeline-5 ISP of the mono case has no demosaic / lens shading / phase fix / crosstalk / hot-pixel leakage
            // (the vec4 input is already demosaicked by the MonoFusion's own ISP; the Color-domain runner is used, spec a-monofusion §7)
            foreach (var key in new[] { "demosaicking.type", "lens_shading.type", "bayer_phase_fix.type", "cross_talk_correction.type", "hot_pixel_leakage_removal.type" }) Isp.Set(key, "none");
            Isp.UseStats(Stats);
            // 1805070a0 L100–108: MonoFusion(stream, demosaic(profile), "ir_correction", row[+0xb4], FUN_18050cbd0(profile) = (FUN_18050c640 == 1))
            bool flag = FusionSensorTuning.ProfileCode((int)profile, RendererProfiles.IsDesktop(profile)) == 1;
            Mono = new MonoFusion(lri, fusion.RefCamId, profile, row.Extra, flag, fusion.NStack, log);
            Mono.SetNeutral(lri.LumenNeutral);   // FUN_180504100 L60 → FUN_1802010a0
        }
    }

    /// <summary>`FusionCacheBase+0x18`: the reference group has a mono camera (L16_00466: A2).</summary>
    public bool HasMono { get; }
    /// <summary>`FusionCacheBase+0x20` (null unless <see cref="HasMono"/>); initialised on first use (`FusionCacheBayer::initialize` 180507a20 runs it after the colour fusion).</summary>
    public MonoFusion? Mono { get; }
    public void EnsureMonoInitialized() { if (Mono is not null && !Mono.Initialized) Mono.Initialize(); }

    /// <summary>`lambda_0` (180508ed0): one 512×512 tile = `process(tile rect, 1.0)`; float tile into the float cache, `w8` into the uint8 cache.</summary>
    (RectI Rect, float[] Fused, byte[] W8) Tile(int tx, int ty)
    {
        if (_tiles.TryGetValue((tx, ty), out var t)) return t;
        var rect = PackedBayerFusion.TileRect(tx, ty, Width, Height);
        var pr = Fusion.Process(rect, 1.0f);
        t = (rect, pr.Out, PackedBayerFusion.WeightToByte(pr.Weight));
        _tiles[(tx, ty)] = t;
        Log?.Invoke($"fusion cache: tile ({tx},{ty}) {rect.Width}x{rect.Height}");
        return t;
    }

    /// <summary>`TileCache::renderROI` over <paramref name="grown"/> (frame pixels) for both caches.</summary>
    public (float[] Fused, byte[] W8) RenderTiles(RectI grown)
    {
        if (grown.X0 < 0 || grown.Y0 < 0 || grown.X1 > Width || grown.Y1 > Height) throw new ArgumentException("Requested ROI is out-of-bounds!");
        var (nx, ny) = PackedBayerFusion.TileGrid(Width, Height);
        int tx0 = Math.Min(grown.X0 / PackedBayerFusion.TileSize, nx - 1), tx1 = Math.Min((grown.X1 - 1) / PackedBayerFusion.TileSize, nx - 1);
        int ty0 = Math.Min(grown.Y0 / PackedBayerFusion.TileSize, ny - 1), ty1 = Math.Min((grown.Y1 - 1) / PackedBayerFusion.TileSize, ny - 1);
        int rw = grown.Width, rh = grown.Height; var f = new float[rw * rh]; var w8 = new byte[rw * rh];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var (rect, fused, wt) = Tile(tx, ty);
                var c = rect.Intersect(grown);
                for (int y = c.Y0; y < c.Y1; y++)
                    for (int x = c.X0; x < c.X1; x++)
                    {
                        int si = (y - rect.Y0) * rect.Width + (x - rect.X0), di = (y - grown.Y0) * rw + (x - grown.X0);
                        f[di] = fused[si]; w8[di] = wt[si];
                    }
            }
        return (f, w8);
    }

    /// <summary>`render` (180507b20): halo-grown rect → fused float + weight tiles → STD plane → pipeline-5 ISP on <paramref name="rect"/>.</summary>
    public Image<Vec4F> Render(RectI rect)
    {
        var grown = PackedBayerFusion.GrownRect(rect, Halo, Width, Height);
        var (fused, w8) = RenderTiles(grown);
        Log?.Invoke($"fusion cache: render {rect} (grown {grown}, halo {Halo}, mono {HasMono})");
        if (!HasMono)
        {
            var std = PackedBayerFusion.StdPlane(w8, NoiseScale);
            var bayer = new Image<float>(grown, fused, grown.Width, 0);
            var stdImg = new Image<float>(grown, std, grown.Width, 0);
            return Isp.ProcessBayerFloat(RefFrame, Stats, bayer, stdImg, rect, 5, Log);
        }
        var m = RenderMono(rect, grown, fused, w8);
        return Isp.ProcessColorFloat(RefFrame, Stats, m.Rgb, m.Std, new RectF(rect.X0, rect.Y0, rect.X1, rect.Y1), 5, Log);
    }

    public sealed record MonoRender(Image<Vec4F> Rgb, Image<float> Std, MonoFusion.ProcessResult Fusion, byte[] W8, byte[] M8);

    /// <summary>The mono branch of `render` (180507b20 L280–420, spec a-monofusion §7) up to the ISP call: the MonoFusion combine on the grown rect,
    /// `m8 = FUN_1802092b0(weight)`, `std = FUN_1802090c0(W8, m8, k) ⊙ vign_ref` (grown views; the halo lives in the rect fields).</summary>
    public MonoRender RenderMono(RectI rect, RectI grown, float[] fused, byte[] w8)
    {
        EnsureMonoInitialized();
        var mono = Mono!;
        var pr = mono.Process(grown, fused);
        var m8 = PackedBayerFusion.WeightToByte(pr.Weight);
        if (Fusion.NStack >= 2) throw new NotSupportedException("stacked captures (FUN_1802091b0 std) are not ported");
        var std = MonoFusion.StdPlaneMono(w8, m8, PackedBayerFusion.StdK(NoiseScale));
        int gw = grown.Width, gh = grown.Height;
        for (int y = 0; y < gh; y++)
        {
            int vrow = (y + grown.Y0) * mono.Width + grown.X0;
            for (int x = 0; x < gw; x++) std[y * gw + x] = mono.VignMap[vrow + x] * std[y * gw + x];   // FUN_1803887d0: vign · std
        }
        return new MonoRender(pr.Rgb, new Image<float>(grown, std, gw, 0), pr, w8, m8);
    }
}
