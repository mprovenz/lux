using System.Diagnostics;
using System.Globalization;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Cli;

/// <summary>
/// Animation encoding for the `parallax-*` formats, delegated to <c>ffmpeg</c>.
///
/// GIF, animated WebP and AVIF are VP8/AV1-class containers that cannot be hand-written the way this project's JPEG
/// and DNG writers were, and ffmpeg is the one tool that covers all of them. The frames handed to it are produced
/// entirely by Lux. ffmpeg is therefore a hard requirement of every animated format: `convert` checks for it before
/// any rendering starts and stops with exit 2 if it is missing (<see cref="ConvertCmd.TryPlan"/>).
///
/// The recipes are not ffmpeg's defaults, and the differences were measured on this material by the session that
/// built the wigglegram (spec `a-wigglegram.md`):
/// * GIF gets **one global palette** (`palettegen=stats_mode=full` over the whole sequence). Per-frame palettes shift
///   the colours between frames, which on a short loop reads as flicker rather than as parallax.
/// * GIF gets **Bayer dithering**, not error diffusion. Error diffusion is dithered per frame, so its pattern crawls:
///   measured bayer 33.6 dB vs sierra 31.6 dB, a smaller file, and bayer *reduced* the frame-to-frame delta by 0.19
///   where sierra *added* 0.37.
/// * AVIF is `libaom-av1 -crf 18 -b:v 0 -cpu-used 4 -pix_fmt yuv420p`. 4:4:4 bought 1.7 dB for a bigger file — the
///   ceiling is the subsampling, not AV1.
///
/// Reader caveats, not bugs to fix: `ffprobe` reports 1 frame for an AVIF and 0 for an animated WebP (it reads only
/// the still primary item of an AVIF and cannot read animated WebP at all); and the GIF frames are
/// `diff_mode=rectangle` deltas, so extract them with `magick file.gif -coalesce`, never `file.gif[n]`.
/// </summary>
public static class Animation
{
    /// <summary>The containers `--parallax-format` accepts. `.png` is deliberately not one — an animated PNG is `.apng`.</summary>
    public static readonly string[] Containers = { "gif", "webp", "avif", "apng" };

    public static string? Ffmpeg() => Which("ffmpeg");

    public static string? Which(string tool)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (d.Length == 0) continue;
            string c = Path.Combine(d, tool);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>Write the frames as PPM into a temporary directory and encode them into <paramref name="outPath"/>,
    /// whose extension selects the container. Throws on an ffmpeg failure (the pipeline is fine; the encode is not).</summary>
    public static void Encode(string ffmpeg, IReadOnlyList<Rgba> frames, string outPath, int ms, int? quality, int? crf)
    {
        string ext = Path.GetExtension(outPath).ToLowerInvariant();
        string tmp = Path.Combine(Path.GetTempPath(), "lux-anim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            for (int i = 0; i < frames.Count; i++) Ppm.Write(Path.Combine(tmp, $"f_{i:D3}.ppm"), frames[i]);
            double fps = 1000.0 / Math.Max(ms, 1);
            string inp = $"-framerate {fps.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{Path.Combine(tmp, "f_%03d.ppm")}\"";
            string q = (quality ?? 88).ToString(CultureInfo.InvariantCulture), c = (crf ?? 18).ToString(CultureInfo.InvariantCulture);
            string args = ext switch
            {
                ".gif" => $"{inp} -lavfi \"split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle\" -loop 0",
                ".webp" => $"{inp} -c:v libwebp_anim -lossless 0 -q:v {q} -compression_level 5 -loop 0 -pix_fmt yuv420p",
                ".avif" => $"{inp} -c:v libaom-av1 -crf {c} -b:v 0 -cpu-used 4 -pix_fmt yuv420p -loop 0",
                ".apng" => $"{inp} -plays 0 -pix_fmt rgb24",
                _ => throw new ArgumentException($"unsupported animation container '{ext}' (gif, webp, avif, apng)"),
            };
            var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -y {args} \"{outPath}\"")
            { RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited {p.ExitCode} encoding {Path.GetFileName(outPath)}{(err.Length > 0 ? ": " + err.Trim() : "")}");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    /// <summary>Ping-pong: play out and back, minus the repeated endpoints. A straight loop snaps across the whole
    /// baseline in one frame, which is the artefact a sweep order exists to avoid.</summary>
    public static List<Rgba> Boomerang(IReadOnlyList<Rgba> f)
    {
        if (f.Count < 3) return f.ToList();
        var o = new List<Rgba>(f);
        for (int i = f.Count - 2; i >= 1; i--) o.Add(f[i]);
        return o;
    }

    /// <summary>A wigglegram frame (tightly packed RGB) as the encoder's RGBA currency.</summary>
    public static Rgba ToRgba(Wigglegram.Frame f)
    {
        var o = new Rgba(f.Width, f.Height);
        for (long i = 0, p = 0, q = 0; i < (long)f.Width * f.Height; i++, p += 4, q += 3)
        { o.P[p] = f.Rgb[q]; o.P[p + 1] = f.Rgb[q + 1]; o.P[p + 2] = f.Rgb[q + 2]; o.P[p + 3] = 255; }
        return o;
    }
}
