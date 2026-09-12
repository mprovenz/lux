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
    /// <summary>Handed to the mono fusion (its initialisation ticks the level-0 inputs phase).</summary>
    public ProgressReporter Progress { get => Mono?.Progress ?? ProgressReporter.None; set { if (Mono is not null) Mono.Progress = value; } }
    readonly Cache.TileStore<(int, int), (RectI Rect, float[] Fused, byte[] W8)> _tiles = new();
    readonly object _monoLock = new();

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
        RefStack = fusion.Stacks?.GetIfStacked(fusion.RefModuleName);   // FUN_18020b870(stream, ref): the reference module's gain map on a stacked capture
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
            Mono = new MonoFusion(lri, fusion.RefCamId, profile, row.Extra, flag, fusion.NStack, log) { Stacks = fusion.Stacks };
            Mono.SetNeutral(lri.LumenNeutral);   // FUN_180504100 L60 → FUN_1802010a0
        }
    }

    /// <summary>`FusionCacheBase+0x18`: the reference group has a mono camera (L16_00466: A2).</summary>
    public bool HasMono { get; }
    /// <summary>`FusionCacheBase+0x20` (null unless <see cref="HasMono"/>); initialised on first use (`FusionCacheBayer::initialize` 180507a20 runs it after the colour fusion).</summary>
    public MonoFusion? Mono { get; }
    public void EnsureMonoInitialized()
    {
        if (Mono is null) return;
        lock (_monoLock) if (!Mono.Initialized) Mono.Initialize();   // once, whichever tile asks first; the others wait
    }

    /// <summary>`lambda_0` (180508ed0): one 512×512 tile = `process(tile rect, 1.0)`; float tile into the float cache, `w8` into the uint8 cache.</summary>
    (RectI Rect, float[] Fused, byte[] W8) Tile(int tx, int ty) => _tiles.GetOrCreate((tx, ty), k =>
    {
        var rect = PackedBayerFusion.TileRect(k.Item1, k.Item2, Width, Height);
        var pr = Fusion.Process(rect, 1.0f);
        Log?.Invoke($"fusion cache: tile ({k.Item1},{k.Item2}) {rect.Width}x{rect.Height}");
        if (Environment.GetEnvironmentVariable("LUX_FUSION_DUMP") is string dpre && ResAmp.TeleLevel0Cache.RectWanted("LUX_FUSION_DUMP_TILES", rect))
        {   // twin of the oracle's fproc_wrap (`proc<i>_out` / `proc<i>_w`): one 512-tile of PackedBayerFusion::process (fused float Bayer + float weight)
            PackedBayerFusion.DumpFloat($"{dpre}_tile_{rect.X0}_{rect.Y0}_{rect.X1}_{rect.Y1}_out.bin", pr.Out, rect.Width, rect.Height);
            PackedBayerFusion.DumpFloat($"{dpre}_tile_{rect.X0}_{rect.Y0}_{rect.X1}_{rect.Y1}_w.bin", pr.Weight, rect.Width, rect.Height);
        }
        return (rect, pr.Out, PackedBayerFusion.WeightToByte(pr.Weight));
    });

    /// <summary>Generate (and keep) one tile ahead of any render that needs it — the unit of the export's dependency prefetch.</summary>
    public void EnsureTile(int tx, int ty) => Tile(tx, ty);
    public (int Nx, int Ny) TileGrid => PackedBayerFusion.TileGrid(Width, Height);

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
            // 180507b20 L157–230: nStack < 2 → FUN_180209010(W8, rsqrtNR(noise_scale)); else the reference's stack gain map over the grown rect
            // (FUN_18020b870) is the second input of FUN_1802090c0 with k = rsqrtNR((data_scale.y · data_scale.x) · noise_scale)
            var std = Fusion.NStack < 2 ? PackedBayerFusion.StdPlane(w8, NoiseScale) : MonoFusion.StdPlaneMono(w8, StackGain8(grown), StackStdK());
            DumpRender(grown, fused, std);
            var bayer = new Image<float>(grown, fused, grown.Width, 0);
            var stdImg = new Image<float>(grown, std, grown.Width, 0);
            return Isp.ProcessBayerFloat(RefFrame, Stats, bayer, stdImg, rect, 5, Log);
        }
        var m = RenderMono(rect, grown, fused, w8);
        return Isp.ProcessColorFloat(RefFrame, Stats, m.Rgb, m.Std, new RectF(rect.X0, rect.Y0, rect.X1, rect.Y1), 5, Log);
    }

    public sealed record MonoRender(Image<Vec4F> Rgb, Image<float> Std, MonoFusion.ProcessResult Fusion, byte[] W8, byte[] M8);
    int _dumped;
    /// <summary>Diagnostic twin of the oracle's fisp_wrap (`<prefix>_fus_render<i>_bayer/_std`): the fused float Bayer and the STD plane of a render's grown rect,
    /// written as `<LUX_FUSION_DUMP>_render_<x0>_<y0>_<x1>_<y1>_{bayer,std}.bin` (first 8 renders; the mono branch's std is the pre-vignetting plane).</summary>
    /// <summary>`LUX_FUSION_DUMP_RECT=x0,y0,x1,y1` restricts the render dumps to that grown rect (else the first 8 renders).</summary>
    bool WantDump(RectI grown)
    {
        if (Environment.GetEnvironmentVariable("LUX_FUSION_DUMP_RECT") is not null) return ResAmp.TeleLevel0Cache.RectWanted("LUX_FUSION_DUMP_RECT", grown);
        return System.Threading.Interlocked.Increment(ref _dumped) <= 8;
    }
    void DumpRender(RectI grown, float[] fused, float[] std)
    {
        if (Environment.GetEnvironmentVariable("LUX_FUSION_DUMP") is not string dpre) return;
        if (!WantDump(grown)) return;
        PackedBayerFusion.DumpFloat($"{dpre}_render_{grown.X0}_{grown.Y0}_{grown.X1}_{grown.Y1}_bayer.bin", fused, grown.Width, grown.Height);
        PackedBayerFusion.DumpFloat($"{dpre}_render_{grown.X0}_{grown.Y0}_{grown.X1}_{grown.Y1}_std.bin", std, grown.Width, grown.Height);
    }

    /// <summary>The reference module's `lt::StackFusion` (its uint8 gain map is `render`'s third STD input); null on a single-frame capture.</summary>
    public StackFusion? RefStack { get; }
    /// <summary>`FUN_18020b870(stream, ref)` cropped to the grown rect (180507b20 L167–222 / L326–380).</summary>
    byte[] StackGain8(RectI grown)
    {
        var rs = RefStack ?? throw new InvalidOperationException("Gain map not available in non-stack mode.");
        int W = rs.Width, gw = grown.Width; var o = new byte[gw * grown.Height];
        for (int y = 0; y < grown.Height; y++) Array.Copy(rs.Gain8, (y + grown.Y0) * W + grown.X0, o, y * gw, gw);
        return o;
    }
    /// <summary>The stacked branches' `k = rsqrtNR((data_scale.y · data_scale.x) · noise_scale)` (180507b20 L223 / L382: `FUN_180125640(ref)` +0x1c · +0x18 · `+0xa4`).</summary>
    float StackStdK() => PackedBayerFusion.StdK((RefFrame.Info.DataScaleY * RefFrame.Info.DataScaleX) * NoiseScale);
    /// <summary>`FUN_1802091b0(std, W8, m8, stackGain, k)`: `idx = ((g+1)·(m8+1)·(W8+1) &gt;&gt; 16) − 1`, floor 0, `std = DAT_1806b5110[idx]·k`.</summary>
    public static float[] StdPlane3(byte[] w8, byte[] m8, byte[] g8, float k)
    {
        var o = new float[w8.Length];
        for (int i = 0; i < o.Length; i++) { int idx = (((g8[i] + 1) * (m8[i] + 1) * (w8[i] + 1)) >> 16) - 1; if (idx < 0) idx = 0; o[i] = PackedBayerFusion.StdTable[idx] * k; }
        return o;
    }

    /// <summary>The mono branch of `render` (180507b20 L280–420, spec a-monofusion §7) up to the ISP call: the MonoFusion combine on the grown rect,
    /// `m8 = FUN_1802092b0(weight)`, `std = FUN_1802090c0(W8, m8, k) ⊙ vign_ref` (grown views; the halo lives in the rect fields).</summary>
    public MonoRender RenderMono(RectI rect, RectI grown, float[] fused, byte[] w8)
    {
        EnsureMonoInitialized();
        var mono = Mono!;
        var pr = mono.Process(grown, fused);
        var m8 = PackedBayerFusion.WeightToByte(pr.Weight);
        // 180507b20 L316–390: nStack < 2 → FUN_1802090c0(W8, m8, k); else FUN_1802091b0(W8, m8, stackGain, k') with the reference's gain map
        var std = Fusion.NStack < 2 ? MonoFusion.StdPlaneMono(w8, m8, PackedBayerFusion.StdK(NoiseScale))
                                    : StdPlane3(w8, m8, StackGain8(grown), StackStdK());
        DumpRender(grown, fused, std);
        int gw = grown.Width, gh = grown.Height;
        for (int y = 0; y < gh; y++)
        {
            int vrow = (y + grown.Y0) * mono.Width + grown.X0;
            for (int x = 0; x < gw; x++) std[y * gw + x] = mono.VignMap[vrow + x] * std[y * gw + x];   // FUN_1803887d0: vign · std
        }
        if (Environment.GetEnvironmentVariable("LUX_FUSION_DUMP") is string dpre && WantDump(grown))
        {   // twins of the oracle's mono combine / kernel dumps (mono_F = the fused Bayer, mono_out = the mono luma output, mono_w = the weight, mono_luma = L) and the final STD plane
            string tag = $"{dpre}_render_{grown.X0}_{grown.Y0}_{grown.X1}_{grown.Y1}";
            PackedBayerFusion.DumpFloat($"{tag}_mono_out.bin", pr.Mono, gw, gh); PackedBayerFusion.DumpFloat($"{tag}_mono_w.bin", pr.Weight, gw, gh);
            PackedBayerFusion.DumpFloat($"{tag}_mono_luma.bin", pr.Luma, gw, gh); PackedBayerFusion.DumpFloat($"{tag}_std_final.bin", std, gw, gh);
            var m8f = new float[m8.Length]; for (int i = 0; i < m8f.Length; i++) m8f[i] = m8[i]; PackedBayerFusion.DumpFloat($"{tag}_m8.bin", m8f, gw, gh);
            var w8f = new float[w8.Length]; for (int i = 0; i < w8f.Length; i++) w8f[i] = w8[i]; PackedBayerFusion.DumpFloat($"{tag}_w8.bin", w8f, gw, gh);
            if (RefStack is not null) { var g = StackGain8(grown); var gf = new float[g.Length]; for (int i = 0; i < gf.Length; i++) gf[i] = g[i]; PackedBayerFusion.DumpFloat($"{tag}_gain8.bin", gf, gw, gh); }
            var rgb = new float[gw * gh * 4]; for (int y = 0; y < gh; y++) { var row = pr.Rgb.Row(y); for (int x = 0; x < gw; x++) { var q = row[x]; int i = (y * gw + x) * 4; rgb[i] = q.R; rgb[i + 1] = q.G; rgb[i + 2] = q.B; rgb[i + 3] = q.A; } }
            var b = new byte[16 + (long)gw * gh * 16]; BitConverter.GetBytes(gw).CopyTo(b, 0); BitConverter.GetBytes(gh).CopyTo(b, 4); BitConverter.GetBytes(gw).CopyTo(b, 8); BitConverter.GetBytes(16).CopyTo(b, 12);
            System.Buffer.BlockCopy(rgb, 0, b, 16, gw * gh * 16); File.WriteAllBytes($"{tag}_mono_vec4.bin", b);
        }
        return new MonoRender(pr.Rgb, new Image<float>(grown, std, gw, 0), pr, w8, m8);
    }
}
