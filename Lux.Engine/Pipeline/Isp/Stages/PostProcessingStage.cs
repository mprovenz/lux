using System.Runtime.InteropServices;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>`PostProcessing:default` — slot 11, installed by the Pipeline ctor (`Pipeline::lambda_1` 180413ea0 →
/// `Internal::PostProcessing` 180428810). Parameters are the pipeline fields the tuning setters write:
/// `tone_mapping.saturation` → +0x1b88 (180411100), `.vibrance` → +0x1b8c (180411110), `.sharpening` → +0x1bd0 (180411120),
/// `.sharpening_scale` → +0x1bd4 (180411130), `.grain_power`/`.grain_sigma` → +0x1bc8/cc (180411150); the gain is
/// `CapturedImage+0x40` = sensor_analog_gain. The body is <see cref="PostProcessingLumen.Run"/> (bit-exact vs cp.dll's own output).</summary>
public sealed class PostProcessingStage : IStage
{
    public StageName Stage => StageName.PostProcessing;
    public string TypeString => "default";
    public StageMeta Meta => new(7, 1, 1f);   // Pipeline ctor L172–173: slot 11 = (pad 7, align 1, scale 1) as observed live (cp.dll stereo-tile reference run, 2026-08-26)
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("PostProcessing needs the RGB working image");
        var t = p.Context.Tuning;
        float Get(string k, float d) { try { return (float)t.Num(k); } catch (KeyNotFoundException) { return d; } }
        float sat = Get("tone_mapping.saturation", 1f), vib = Get("tone_mapping.vibrance", 1f);
        float c = Get("tone_mapping.sharpening", 0f), d = Get("tone_mapping.sharpening_scale", 1f);
        float a = Get("tone_mapping.grain_power", 0f), b = Get("tone_mapping.grain_sigma", 1f);
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        if (abs.IsEmpty) return;
        // gather the payload rect as a dense RGBA float buffer, run the Lumen body, scatter back
        int w = abs.Width, h = abs.Height;
        var buf = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            MemoryMarshal.Cast<Vec4F, float>(img.Row(abs.Y0 - img.Rect.Y0 + y).Slice(abs.X0 - img.Rect.X0, w)).CopyTo(buf.AsSpan(y * w * 4, w * 4));
        float[]? comp = null;
        var ci = p.Companion;
        if (ci is not null)
        {   // the pre-denoise working image on the same rect (LDiff companion)
            comp = new float[w * h * 4];
            for (int y = 0; y < h; y++) MemoryMarshal.Cast<Vec4F, float>(ci.Row(abs.Y0 - ci.Rect.Y0 + y).Slice(abs.X0 - ci.Rect.X0, w)).CopyTo(comp.AsSpan(y * w * 4, w * 4));
        }
        // extent buffers: the parent image ∩ (region ± 3) — Lumen reads the input beyond the stage region where the previous stage produced data
        var er = new RectI(abs.X0 - 3, abs.Y0 - 3, abs.X1 + 3, abs.Y1 + 3).Intersect(img.Rect); if (ci is not null) er = er.Intersect(ci.Rect);
        (float[] Data, int W, int H, int VX, int VY)? ext = null; float[]? compExt = null;
        if (er != abs)
        {
            var ed = new float[er.Width * er.Height * 4];
            for (int y = 0; y < er.Height; y++) MemoryMarshal.Cast<Vec4F, float>(img.Row(er.Y0 - img.Rect.Y0 + y).Slice(er.X0 - img.Rect.X0, er.Width)).CopyTo(ed.AsSpan(y * er.Width * 4, er.Width * 4));
            ext = (ed, er.Width, er.Height, abs.X0 - er.X0, abs.Y0 - er.Y0);
            if (ci is not null)
            {
                compExt = new float[er.Width * er.Height * 4];
                for (int y = 0; y < er.Height; y++) MemoryMarshal.Cast<Vec4F, float>(ci.Row(er.Y0 - ci.Rect.Y0 + y).Slice(er.X0 - ci.Rect.X0, er.Width)).CopyTo(compExt.AsSpan(y * er.Width * 4, er.Width * 4));
            }
        }
        // Diagnostic: `LUX_PP_DUMP=<prefix>` writes the stage's input, companion and output of every call as raw RGBA float rows,
        // `{prefix}_pp_{src,comp,out}_er<exposureRatio>_<x0>_<y0>_{w}x{h}.f32`, and prints the parameters at full precision (twin of cp.dll's PostProcessing hook).
        string? ppd = Environment.GetEnvironmentVariable("LUX_PP_DUMP");
        if (ppd is not null)
        {
            Console.Error.WriteLine($"[pp] er{p.Frame.ExposureRatio:R} rect ({abs.X0} {abs.Y0} {abs.X1} {abs.Y1}) gain {p.Frame.AnalogGain:R} grain_power {a:R} grain_sigma {b:R} sharpening {c:R} sharpening_scale {d:R} saturation {sat:R} vibrance {vib:R} comp {(comp is null ? "none" : "yes")}");
            string key = $"er{p.Frame.ExposureRatio:R}_{abs.X0}_{abs.Y0}_{w}x{h}";   // the frame info carries no module id: gain + exposure ratio identify the module
            File.WriteAllBytes($"{ppd}_pp_src_{key}.f32", MemoryMarshal.AsBytes(buf.AsSpan()).ToArray());
            if (comp is not null) File.WriteAllBytes($"{ppd}_pp_comp_{key}.f32", MemoryMarshal.AsBytes(comp.AsSpan()).ToArray());
        }
        PostProcessingLumen.Run(buf, w, h, p.Frame.AnalogGain, a, b, c, d, sat, vib, comp, ext, compExt);
        if (ppd is not null) File.WriteAllBytes($"{ppd}_pp_out_er{p.Frame.ExposureRatio:R}_{abs.X0}_{abs.Y0}_{w}x{h}.f32", MemoryMarshal.AsBytes(buf.AsSpan()).ToArray());
        for (int y = 0; y < h; y++)
            buf.AsSpan(y * w * 4, w * 4).CopyTo(MemoryMarshal.Cast<Vec4F, float>(img.Row(abs.Y0 - img.Rect.Y0 + y).Slice(abs.X0 - img.Rect.X0, w)));
    }
}
