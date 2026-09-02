using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// One module frame through the ported module ISP and Lux's display pass — **the** per-module render, shared by
/// `convert --formats lens-frames` (one JPEG per module), `parallax-wiggle` (the four A-group frames) and the
/// parallax formats' disocclusion donors. It used to exist as three transcriptions (`LensJpegExporter.ExportOne`,
/// `WiggleCmd.RenderModule`, `Lux.Parallax/Modules.Render`); this is the one copy.
///
/// The raw→linear-RGB half is the ported module ISP (<see cref="SoftIsp"/> + <see cref="ModuleIspTuning"/>, i.e.
/// Lumen's own <c>lt::SoftISP</c> stage list), so its output is linear camera RGB with the ISP's own
/// (<c>manual_temp</c>-derived, per-module) neutral already applied. The DISPLAY half (camera RGB → ForwardMatrix →
/// §7 tone curve → sRGB gamma) is Lux's own <see cref="ColorPipeline"/>: Lumen has no per-module export, so there is
/// no artefact to check that half against. Modules with no CFA are rendered greyscale.
///
/// The output is float sRGB in 0..1 at native module resolution; the caller quantises, downscales or resamples as
/// its format needs — the wigglegram box-averages the floats before quantising, the others quantise at native size.
/// </summary>
public static class ModuleRender
{
    /// <summary>The reconstruction luminance gain that maps normalized sensor values to the colour-pass scale.</summary>
    public const float ReconGain = 3.9f * 0.25f;

    /// <summary>A rendered module: display sRGB floats (0..1, row-major RGB, no padding) at native resolution.
    /// <see cref="IsMono"/> = no CFA; R = G = B then.</summary>
    public sealed record Image(string Module, int Width, int Height, float[] Rgb, bool IsMono);

    /// <summary>The exposure every module is normalised to: <paramref name="preferred"/>'s sensor exposure when that
    /// module is in the capture, else the longest exposure in it. (The ISP does not equalise exposure between modules.)</summary>
    public static ulong ExposureReference(LriFile lri, string preferred) =>
        lri.Modules.TryGetValue(preferred, out var m) ? m.Module.SensorExposure : lri.Modules.Values.Max(x => x.Module.SensorExposure);

    /// <param name="mref">the frame to render — <c>lri.Modules[name]</c> for frame 0, or another entry of
    /// <c>lri.Frames[name]</c> on a stacked capture</param>
    /// <param name="expRef">see <see cref="ExposureReference"/></param>
    /// <param name="ev">display exposure adjust in stops</param>
    /// <param name="level">module-ISP config level (0 = full-res, no denoise)</param>
    public static Image Render(LriFile lri, LumenProfile colour, WhiteBalance.CaptureWb wb, string name, LriFile.ModuleRef mref,
                               ulong expRef, float ev = 0.95f, int level = 0, RendererProfile profile = RendererProfile.Desktop)
    {
        var frame = CapturedFrame.Load(lri, mref);   // the ModuleRef overload: selects THIS frame of a stack
        var tuning = ModuleIspTuning.Build(level, profile, frame.Info, wb.Cct, wb.Tint);
        if (!frame.Info.IsColour)
        {
            // Lumen never runs a standalone module ISP on a mono module (its mono path is the fusion one, which ISPs
            // the *reference* colour frame), so the CFA-only stages have no meaning here and are turned off locally;
            // the ported tuning itself is untouched. cross_talk_correction needs an IR blend estimated from the CFA's
            // R/B site ratios, which a mono sensor has none of; color_noise_reduction is a CHROMA denoiser that
            // degenerates on R=G=B and blanks whole 32x32 tiles (measured on A2); hot_pixel_removal and
            // highlight_restore index by (redX, redY), and the mono sentinel (-1,-1) indexes out of range.
            tuning.Set("cross_talk_correction.type", "none").Set("color_noise_reduction.type", "none")
                  .Set("hot_pixel_removal.type", "none").Set("highlight_restore.type", "none");
        }
        var isp = new SoftIsp(tuning, colour);
        var img = isp.ProcessBayer(frame, new RectI(0, 0, frame.Width, frame.Height), level);

        // Per-module exposure normalisation + the reconstruction push.
        float expGain = (float)((double)expRef / frame.Module.SensorExposure) * ReconGain;
        int w = img.Width, h = img.Height;
        long n = (long)w * h;
        var rgb = new float[n * 3];

        if (!frame.Info.IsColour)   // no CFA → monochrome (the ISP's mono branch leaves R=G=B)
        {
            for (int y = 0; y < h; y++)
            {
                var row = img.Row(y);
                for (int x = 0; x < w; x++)
                {
                    long o = ((long)y * w + x) * 3;
                    float g = ColorPipeline.RenderMono(row[x].G * expGain, ev);
                    rgb[o] = g; rgb[o + 1] = g; rgb[o + 2] = g;
                }
            }
            return new Image(name, w, h, rgb, true);
        }

        for (int y = 0; y < h; y++)
        {
            var row = img.Row(y);
            for (int x = 0; x < w; x++)
            {
                long o = ((long)y * w + x) * 3;
                rgb[o] = row[x].R * expGain; rgb[o + 1] = row[x].G * expGain; rgb[o + 2] = row[x].B * expGain;
            }
        }
        var fm = Calibration.ForwardMatrix(lri.Header, frame.Module.Id)
                 ?? Calibration.ForwardMatrix(lri.Header, lri.Header.ImageReferenceCamera)
                 ?? Identity;
        // The ISP already white-balanced with its own neutral, so the display pass must not re-apply WB.
        ColorPipeline.Render(rgb, fm, Unit, ev);
        return new Image(name, w, h, rgb, false);
    }

    static readonly float[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    static readonly float[] Unit = { 1, 1, 1 };

    /// <summary>8-bit quantisation, the one every consumer uses: clamp to 0..1, ×255, round half up.</summary>
    public static byte Q(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    /// <summary>Quantise at native size to tightly packed RGB (what the lens JPEG is written from).</summary>
    public static byte[] ToRgb8(Image img)
    {
        var o = new byte[img.Rgb.Length];
        for (long i = 0; i < o.LongLength; i++) o[i] = Q(img.Rgb[i]);
        return o;
    }

    /// <summary>Quantise at native size to the parallax formats' RGBA currency (the donor frames).</summary>
    public static Parallax.Rgba ToRgba(Image img)
    {
        var o = new Parallax.Rgba(img.Width, img.Height);
        for (long i = 0, p = 0, q = 0; i < (long)img.Width * img.Height; i++, p += 4, q += 3)
        { o.P[p] = Q(img.Rgb[q]); o.P[p + 1] = Q(img.Rgb[q + 1]); o.P[p + 2] = Q(img.Rgb[q + 2]); o.P[p + 3] = 255; }
        return o;
    }

    /// <summary>Box-average the floats down so the long edge fits <paramref name="longEdge"/> (0 = native), then
    /// quantise. Rendering at native resolution first and reducing after keeps the detail the modules actually
    /// resolve, which a `draft`-style decode would throw away. Integer step, so the reduction is exact averaging.</summary>
    public static Wigglegram.Frame ToFrame(Image img, int longEdge)
    {
        int w = img.Width, h = img.Height; var rgb = img.Rgb;
        int ow = w, oh = h, step = 1;
        if (longEdge > 0 && Math.Max(w, h) > longEdge)
        {
            step = (int)Math.Ceiling(Math.Max(w, h) / (double)longEdge);
            ow = w / step; oh = h / step;
        }
        var outp = new byte[(long)ow * oh * 3];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                float r = 0, g = 0, b = 0; int n = 0;
                for (int sy = y * step; sy < Math.Min((y + 1) * step, h); sy++)
                    for (int sx = x * step; sx < Math.Min((x + 1) * step, w); sx++)
                    { long o = ((long)sy * w + sx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; n++; }
                long d = ((long)y * ow + x) * 3; float k = n > 0 ? 1f / n : 0f;
                outp[d] = Q(r * k); outp[d + 1] = Q(g * k); outp[d + 2] = Q(b * k);
            }
        return new Wigglegram.Frame(img.Module, ow, oh, outp);
    }
}
