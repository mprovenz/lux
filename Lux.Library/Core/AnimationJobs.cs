using System.Globalization;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Core;

/// <summary>The animated export job (<see cref="JobKind.Animation"/>): the engine's <see cref="AnimationRenderer"/> on a session of the capture
/// (saved edits and settings as an export applies them), then Lux's own writer — or ffmpeg for MP4 / WebM — into a temporary file renamed
/// when complete, so a canceled job leaves nothing.</summary>
internal static class AnimationJobs
{
    public static string FormatName(AnimationFormat f) => f switch
    {
        AnimationFormat.Gif => "GIF", AnimationFormat.Apng => "APNG", AnimationFormat.WebP => "WebP", AnimationFormat.Mp4 => "MP4", _ => "WebM",
    };

    public static void Validate(AnimationOptions o, bool canWriteVideo)
    {
        if (o.Frames is < 2 or > 240) throw new LibraryException("An animation has 2 to 240 frames.");
        if (o.LongEdge is < 32 or > 8320) throw new LibraryException("The animation's size must be 32 to 8320 pixels on the long edge.");
        if (o.FrameMs is < 0 or > 10000) throw new LibraryException("A frame lasts at most 10 seconds.");
        if (!(o.FNumber > 0)) throw new LibraryException("The f-number must be greater than 0.");
        if (o.FocusFromMm is float a && !(a > 0) || o.FocusToMm is float b && !(b > 0) || o.FocusMm is float c && !(c > 0))
            throw new LibraryException("Focus distances must be greater than 0.");
        if (AnimationOptions.IsVideo(o.Format) && !canWriteVideo)
            throw new LibraryException("Video needs ffmpeg, which is not installed. GIF, APNG and WebP need nothing extra.");
        if (string.IsNullOrWhiteSpace(o.FileNamePattern)) throw new LibraryException("The file name pattern is empty.");
    }

    /// <summary>"Rack focus · f/2.8 · 24 frames · 1040 px · GIF".</summary>
    public static string Detail(AnimationOptions o)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string> { AnimationEffects.NameOf(o.Effect) };
        if (o.Effect is AnimationEffect.RackFocus or AnimationEffect.ApertureSweep) parts.Add(string.Format(inv, "f/{0:0.#}", o.FNumber));
        if (o.Effect is not (AnimationEffect.Wigglegram or AnimationEffect.LensTour)) parts.Add($"{o.Frames} frames");
        parts.Add($"{o.LongEdge} px");
        parts.Add(FormatName(o.Format));
        return string.Join(" · ", parts);
    }

    static string Stem(AnimationOptions o, CaptureItem item)
    {
        var t = item.CaptureTime;
        var s = o.FileNamePattern.Replace("{name}", item.Name).Replace("{effect}", AnimationEffects.Key(o.Effect))
                                 .Replace("{date}", t?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nodate")
                                 .Replace("{time}", t?.ToString("HHmmss", CultureInfo.InvariantCulture) ?? "000000");
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? item.Name : s.Trim();
    }

    public static string PlannedFile(AnimationOptions o, CaptureItem item, string outDir) => Path.Combine(outDir, Stem(o, item) + AnimationOptions.Extension(o.Format));

    /// <summary>The file after the collision rule as the folder is now (rename: the first free -2, -3 …).</summary>
    public static string DecidedFile(AnimationOptions o, CaptureItem item, string outDir)
    {
        string file = PlannedFile(o, item, outDir);
        if (o.IfExists != ExistingFilePolicy.Rename || !File.Exists(file)) return file;
        string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        for (int i = 2; ; i++) { string f = Path.Combine(outDir, $"{stem}-{i}{ext}"); if (!File.Exists(f)) return f; }
    }

    public static async Task RunAsync(LibraryCore lib, AnimationJobRequest r, JobContext job)
    {
        var o = r.Options;
        Validate(o, lib.CanWriteVideo);
        var item = lib.Get(r.CaptureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var check = AnimationEffects.For(item).First(e => e.Effect == o.Effect);
        if (!check.Available) throw new LibraryException(check.Reason ?? "This photo cannot make this animation.");
        string path = lib.PresentPath(r.CaptureId);
        string outDir = o.OutputFolder ?? lib.SettingsImpl.ResolvedExportFolder;
        job.Report(0, "Checking the photo");
        var ctx = lib.ContextFor(r.CaptureId, path, mayCreateFolder: false);
        string file = PlannedFile(o, item, outDir);
        if (File.Exists(file))
        {
            switch (o.IfExists)
            {
                case ExistingFilePolicy.Skip: job.Note("Skipped: the file already exists in the export folder."); job.Report(1, "Done (skipped)"); return;
                case ExistingFilePolicy.Rename:
                    string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
                    for (int i = 2; ; i++) { string f = Path.Combine(outDir, $"{stem}-{i}{ext}"); if (!File.Exists(f)) { file = f; break; } }
                    break;
            }
        }
        job.Planned(file);
        var pp = new LibraryCore.PhaseProgress(job, 0.02, 0.35, ("loading", 1), ("registration", 30), ("fusion set-up", 10), ("telephoto set-up", 3), ("render tiles", 3));
        bool framing = false;   // once the frames render, the job's own status ("Rendering frame 7 of 24") replaces the engine's phases
        using var s = CaptureSession.Open(path, new SessionOptions
        {
            Context = ctx, Threads = lib.SettingsImpl.RenderThreads, Progress = u => { if (!Volatile.Read(ref framing)) pp.On(u); }, DisplayCacheBytes = 0, KeepGeometry = true,
            Log = lib.Log is null ? null : m => lib.Log($"[{r.CaptureId[..8]}] {m}"),
        });
        job.Report(0.02, o.Effect is AnimationEffect.Wigglegram or AnimationEffect.LensTour ? "Reading the lenses" : "Reading the depth");
        AnimationRenderer anim;
        // the photo's orientation as shown: the held turn unless asked for unturned, as the stills
        var spec = Lux.Library.Animation.AnimationSourceImpl.Oriented(lib, lib.Get(r.CaptureId) ?? item, o.ToSpec(), o.Unturned);
        try { anim = await AnimationRenderer.CreateAsync(s, spec, null, job.Cancel).ConfigureAwait(false); }
        catch (ExportRequestException e) { throw new LibraryException(e.Message); }
        var plan = anim.Plan;
        int n = plan.Frames.Count;
        var frames = new Rgba[n];
        Volatile.Write(ref framing, true);
        await Task.Run(() =>
        {
            for (int k = 0; k < n; k++)
            {
                job.Cancel.ThrowIfCancellationRequested();
                job.Report(0.35 + 0.55 * k / n, $"Rendering frame {k + 1} of {n}");
                frames[k] = anim.Render(k, job.Cancel);
            }
        }, job.Cancel).ConfigureAwait(false);
        var played = plan.Order.Select(i => frames[i]).ToList();
        Directory.CreateDirectory(outDir);
        job.Report(0.9, $"Writing {FormatName(o.Format)}");
        long bytes = await Task.Run(() =>
        {
            if (AnimationOptions.IsVideo(o.Format))
                return VideoEncoder.Write(file, o.Format == AnimationFormat.Mp4 ? VideoContainer.Mp4 : VideoContainer.WebM, played, plan.DelaysMs[0], o.Repeat, job.Cancel);
            var c = o.Format switch { AnimationFormat.Apng => AnimationContainer.Apng, AnimationFormat.WebP => AnimationContainer.WebP, _ => AnimationContainer.Gif };
            var enc = new AnimationEncodeOptions
            {
                Loops = o.Repeat, GifDither = o.GifDither, GifPalette = o.GifPalette, Cancel = job.Cancel,
                Progress = (d, t) => job.Report(0.9 + 0.1 * d / t, $"Writing {FormatName(o.Format)}"),
            };
            return AnimationEncoder.WriteFile(file, c, played, plan.DelaysMs, enc).Bytes;
        }, job.Cancel).ConfigureAwait(false);
        foreach (var note in plan.Notes) job.Note(note);
        job.Note($"{plan.Order.Count} frames, {plan.Size.W} × {plan.Size.H}, {plan.Duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        job.Result(file, new[] { file });
        // as an export does: the depth computed for the frames is kept when the capture already has a context folder
        if (plan.Level >= 0 && s.SavedState?.SessionGeometry is null && ctx.Context.ExistingRoot is not null && s.CacheLevel == CacheLevel.None)
        {
            try { await s.EnsureCacheAsync(CacheLevel.Geometry, job.Cancel).ConfigureAwait(false); job.Note("Kept the computed depth for later exports."); lib.OnCacheChanged(r.CaptureId); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
        var changed = lib.RefreshContexts(new[] { r.CaptureId });
        lib.Changes.Changed(changed);
        job.Report(1, $"Done: {bytes / 1e6:0.0} MB");
    }
}
