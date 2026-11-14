using System.Diagnostics;
using System.Globalization;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>Video containers (through <c>ffmpeg</c>, used only when it is installed).</summary>
public enum VideoContainer
{
    /// <summary>MP4, H.264 (libx264, 4:2:0, quality 18), plays everywhere.</summary>
    Mp4,
    /// <summary>WebM, VP9 (libvpx-vp9, quality 30), for the web.</summary>
    WebM,
}

/// <summary>
/// Video output through an external <c>ffmpeg</c>, found on the PATH when the export runs — Lux does not ship a video encoder. The frames
/// are Lux's own; they are piped to ffmpeg as raw RGBA at a constant frame rate (an odd width or height is cropped by one pixel: 4:2:0 video
/// needs even sizes). Written under a temporary name and renamed when complete, so a cancelled or failed encode leaves no file.
/// </summary>
public static class VideoEncoder
{
    public static string Extension(VideoContainer c) => c == VideoContainer.Mp4 ? ".mp4" : ".webm";

    /// <summary>ffmpeg on the PATH (with the PATHEXT extensions on Windows), or null.</summary>
    public static string? FindFfmpeg()
    {
        string[] exts = { "" };
        if (OperatingSystem.IsWindows())
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT");
            exts = (string.IsNullOrWhiteSpace(pathext) ? ".EXE;.CMD;.BAT" : pathext).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var d = raw.Trim().Trim('"');
            if (d.Length == 0) continue;
            foreach (var ext in exts) { string c = Path.Combine(d, "ffmpeg" + ext); if (File.Exists(c)) return c; }
        }
        return null;
    }

    /// <summary>Encode the played frames (all the same size) at one frame every <paramref name="frameMs"/> ms.</summary>
    public static long Write(string path, VideoContainer container, IReadOnlyList<Rgba> frames, int frameMs, int loops = 0, CancellationToken cancel = default,
                             string? ffmpeg = null, Action<int, int>? progress = null)
    {
        ffmpeg ??= FindFfmpeg() ?? throw new InvalidOperationException("Video needs ffmpeg, which is not installed.");
        int w = frames[0].W, h = frames[0].H;
        string fps = (1000.0 / Math.Max(1, frameMs)).ToString("0.######", CultureInfo.InvariantCulture);
        string codec = container == VideoContainer.Mp4
            ? "-c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart"
            : "-c:v libvpx-vp9 -crf 30 -b:v 0 -pix_fmt yuv420p -row-mt 1";
        string part = path + ".part" + Extension(container);
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s {w}x{h} -framerate {fps} -i - -vf crop=trunc(iw/2)*2:trunc(ih/2)*2".Split(' ')) psi.ArgumentList.Add(a);
        foreach (var a in codec.Split(' ')) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(part);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started");
        var err = p.StandardError.ReadToEndAsync();
        try
        {
            using (var stdin = p.StandardInput.BaseStream)
            {
                int plays = Math.Max(1, loops == 0 ? 1 : loops);
                int total = frames.Count * plays, i = 0;
                for (int r = 0; r < plays; r++)
                    foreach (var f in frames)
                    {
                        cancel.ThrowIfCancellationRequested();
                        stdin.Write(f.P, 0, f.P.Length);
                        progress?.Invoke(++i, total);
                    }
            }
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg stopped with exit code {p.ExitCode}: {err.Result.Trim()}");
            File.Move(part, path, overwrite: true);
            return new FileInfo(path).Length;
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(); } catch (InvalidOperationException) { }
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }
}
