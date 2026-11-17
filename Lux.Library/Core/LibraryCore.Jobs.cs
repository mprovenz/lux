using System.Globalization;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    // ------------------------------------------------------------------------------------------------ titles and estimates

    internal string NameOf(string? captureId)
        => captureId is null ? "a photo" : Db.Read(c => c.Scalar(null, "SELECT name FROM capture WHERE id=$i", ("$i", captureId)) as string) ?? captureId[..12];

    /// <summary>The capture has no depth map kept yet (an upgrade then computes it first, in the same job).</summary>
    bool HasNoDepth(string? captureId) => captureId is not null
        && Db.Read(c => c.Scalar(null, "SELECT cache_level FROM capture WHERE id=$i", ("$i", captureId))) is { } v && Convert.ToInt32(v, CultureInfo.InvariantCulture) < (int)CacheLevel.Geometry;

    internal string TitleOf(JobRequest r) => r switch
    {
        // A quality upgrade of a photo without a depth map computes the depth map first, in the same job —
        // the title says so
        AnalysisJobRequest a => a.TargetLevel >= CacheLevel.Linear13 ? $"Analyzing {NameOf(a.CaptureId)}: depth map and 13 MP" : $"Analyzing {NameOf(a.CaptureId)}",
        CacheJobRequest { Release: true } c => c.Target == CacheLevel.Linear13 ? $"Releasing full detail of {NameOf(c.CaptureId)}" : $"Reducing the kept data of {NameOf(c.CaptureId)}",
        CacheJobRequest c => c.Target switch
        {
            CacheLevel.Full => $"Keeping full detail for {NameOf(c.CaptureId)}" + (HasNoDepth(c.CaptureId) ? " (depth map first)" : ""),
            CacheLevel.Linear13 => $"Keeping the 13 MP render of {NameOf(c.CaptureId)}" + (HasNoDepth(c.CaptureId) ? " (depth map first)" : ""),
            _ => $"Keeping the depth of {NameOf(c.CaptureId)}",
        },
        ExportJobRequest e => $"Exporting {NameOf(e.CaptureId)} as {string.Join(" + ", e.Options.Formats.Select(ExportJobs.DisplayName))}",
        AnimationJobRequest an => $"Animating {NameOf(an.CaptureId)} as {AnimationJobs.FormatName(an.Options.Format)}",
        LumenImportJobRequest l => $"Importing Lumen edits for {NameOf(l.CaptureId)}",
        FolderScanJobRequest f => $"Scanning {(FolderRow(f.FolderId) is { } fr ? Path.GetFileName(fr.Path) is { Length: > 0 } n ? n : fr.Path : "a folder")}",
        ThumbnailJobRequest => "Making thumbnails",
        CameraImportJobRequest i => CameraImportText.Title(i),
        ContextStoreMoveJobRequest m => $"Moving the data of photos on read-only drives to {m.NewFolder}",
        _ => "Background work",
    };

    /// <summary>The short line under a job's title (<see cref="JobInfo.Detail"/>).</summary>
    internal static string DetailOf(JobRequest r)
    {
        static string Lvl(int l) => l switch { 0 => "full size", 1 => "13 MP", _ => $"level {l}" };
        var inv = CultureInfo.InvariantCulture;
        return r switch
        {
            AnimationJobRequest an => AnimationJobs.Detail(an.Options),
            ExportJobRequest e => string.Join(" · ", new[] { string.Join(" + ", e.Options.Formats.Select(ExportJobs.DisplayName)), e.Options.LongEdge is int le ? $"{le} px" : Lvl(e.Options.Level) }
                .Concat(e.Options.DepthEffectOff ? new[] { "all in focus" } : e.Options.DepthEffectFNumber is float f ? new[] { string.Format(inv, "f/{0:0.#}", f) } : Array.Empty<string>())
                .Concat(e.Options.UseSavedEdits ? Array.Empty<string>() : new[] { "without saved edits" })),
            AnalysisJobRequest a => a.TargetLevel switch { CacheLevel.Full => "depth + full detail", CacheLevel.Geometry => "depth", _ => "depth + 13 MP render" },
            CacheJobRequest { Release: true } c => c.Target switch { CacheLevel.Linear13 => "back to the 13 MP render", CacheLevel.Geometry => "keep the depth only", _ => "release the kept data" },
            CacheJobRequest c => c.Target switch { CacheLevel.Full => "full detail (≈ 200 MB)", CacheLevel.Linear13 => "13 MP render (≈ 55 MB)", _ => "depth (≈ 15 MB)" },
            LumenImportJobRequest l => "depth edits and settings" + (l.NewerToneStyle ? " · newer tone style" : ""),
            FolderScanJobRequest => "find new, moved and missing photos",
            ContextStoreMoveJobRequest m => m.NewFolder,
            CameraImportJobRequest i => CameraImportText.Detail(i),
            _ => "",
        };
    }

    /// <summary>What a job that is not about one capture works on (<see cref="JobInfo.Subject"/>): a camera import's first and last photo.</summary>
    internal static string? SubjectOf(JobRequest r) => r is CameraImportJobRequest i ? CameraImportText.Subject(i) : null;

    (bool Stacked, CacheLevel Level) Shape(string? id)
        => id is null ? (false, CacheLevel.None)
         : Db.Read(c => c.Rows(null, "SELECT stack_frames, cache_level FROM capture WHERE id=$i", r => (r.GetInt32(0) > 1, (CacheLevel)r.GetInt32(1)), ("$i", id)).FirstOrDefault());

    static long G(double gb) => (long)(gb * JobQueue.Gb);

    /// <summary>The peak memory (process working set it adds) a job is expected to need, from measured process peaks:
    /// the registration ≈ 2.5–2.8 GB; up to the 13 MP tier ≈ 4.5 GB (stacked ≈ 7); full detail ≈ 6.2 GB
    /// (stacked ≈ 8); a full-size export without a full cache 5.4–7.9 GB, with one 3.2 GB; level 2–4 exports 0.6–1.4 GB.</summary>
    internal long EstimateMemory(JobRequest r)
    {
        var (stacked, level) = Shape(JobRequests.CaptureOf(r));
        double R(JobReservation x) => SettingsImpl.GetReservationGb(x);
        double analysis = R(stacked ? JobReservation.AnalysisStacked : JobReservation.Analysis);
        switch (r)
        {
            case AnalysisJobRequest:
            case CacheJobRequest { Release: false }:
                var target = r is AnalysisJobRequest aa ? aa.TargetLevel ?? CacheLevel.Linear13 : ((CacheJobRequest)r).Target;
                if (target == CacheLevel.Full) return G(R(stacked ? JobReservation.FullDetailStacked : JobReservation.FullDetail));
                // the 13 MP tier: the registration and the fusion (less by 1 GB when the depth is kept already); the depth alone: 2.5 GB less
                if (target == CacheLevel.Linear13) return G(Math.Max(0.25, level >= CacheLevel.Geometry ? analysis - 1.0 : analysis));
                return G(Math.Max(0.25, analysis - 2.5));
            case CacheJobRequest: return G(0.25);
            case ExportJobRequest e:
                bool depth = e.Options.Formats.Any(f => f is "jpg+depth" or "depth") || e.Options.DepthEffectFNumber is not null;
                double full = R(stacked ? JobReservation.ExportStacked : JobReservation.Export), small = R(JobReservation.ExportSmall) + (stacked ? 0.5 : 0);
                if (e.Options.Level == 0 || depth) return G(level == CacheLevel.Full && !depth ? Math.Min(3.5, full) : full);
                if (e.Options.Level == 1) return G(level >= CacheLevel.Linear13 ? small : Math.Max(small, analysis - (stacked ? 2.5 : 1.0)));
                return G(small);
            case AnimationJobRequest an:
            {
                // the depth effects and the parallax read the depth (the level-0 state, as a depth export); the lens effects only module renders
                bool depthAnim = an.Options.Effect is not (Lux.Engine.Session.AnimationEffect.Wigglegram or Lux.Engine.Session.AnimationEffect.LensTour);
                double fullA = R(stacked ? JobReservation.ExportStacked : JobReservation.Export), smallA = R(JobReservation.ExportSmall) + (stacked ? 0.5 : 0);
                return G(depthAnim ? (level >= CacheLevel.Geometry ? Math.Min(fullA, 5.0) : fullA) : Math.Max(smallA, 4.5));
            }
            case LumenImportJobRequest: return G(1.5);
            case FolderScanJobRequest: return G(0.4 * ScanParallelism);
            case ContextStoreMoveJobRequest: return G(0.25);
            case CameraImportJobRequest: return G(0.25);   // 8 MB buffers; the files stream to disk
            default: return G(0.5);
        }
    }

    // ------------------------------------------------------------------------------------------------ dispatch

    internal Task RunJobAsync(JobRequest r, JobContext ctx) => r switch
    {
        FolderScanJobRequest f => ScanFolderAsync(f.FolderId, ctx),
        AnalysisJobRequest a => CacheJobAsync(a.CaptureId, a.TargetLevel ?? (CacheState(a.CaptureId) is { } s ? SettingsImpl.EffectiveCacheLevel(s.FolderId) : CacheLevel.Linear13), false, true, ctx),
        CacheJobRequest c => CacheJobAsync(c.CaptureId, c.Target, c.Release, false, ctx),
        ExportJobRequest e => ExportJobs.RunAsync(this, e, ctx),
        AnimationJobRequest an => AnimationJobs.RunAsync(this, an, ctx),
        LumenImportJobRequest l => LumenImportAsync(l, ctx),
        ThumbnailJobRequest => Thumbs.RunAsync(ctx),
        CameraImportJobRequest i => RunCameraImportAsync(i, ctx),
        ContextStoreMoveJobRequest m => MoveContextStoreAsync(m, ctx),
        _ => throw new LibraryException("Lux does not know how to run this job."),
    };

    // ------------------------------------------------------------------------------------------------ camera imports

    volatile ICameraImporter? _cameraImporter;

    public void SetCameraImporter(ICameraImporter? importer) => _cameraImporter = importer;

    Task RunCameraImportAsync(CameraImportJobRequest r, JobContext ctx)
    {
        if (r.Files.Count == 0) throw new LibraryException("There is nothing to import: no photos were chosen.");
        var importer = _cameraImporter ?? throw new LibraryException("The camera is not available: connect it and import again from the Camera page.");
        return importer.RunAsync(r, new JobReporter(ctx));
    }

    /// <summary>A job's progress for code outside the library (<see cref="IJobReporter"/>).</summary>
    sealed class JobReporter(JobContext ctx) : IJobReporter
    {
        public CancellationToken Cancel => ctx.Cancel;
        public long JobId => ctx.JobId;
        public void Report(double progress, string? status = null) => ctx.Report(progress, status);
        public void Items(int done, int total) => ctx.Items(done, total);
        public void Note(string note) => ctx.Note(note);
        public void Result(string? path, IEnumerable<string>? files = null) => ctx.Result(path, files);
    }

    /// <summary>The main file a job will write (<see cref="JobInfo.PlannedFile"/>): exports and animations; null otherwise.</summary>
    internal string? PlannedFileOf(JobRequest r)
    {
        try
        {
            return r switch
            {
                // the name the collision rule gives now (a file that appears before the job starts may change it; the job says when it starts)
                ExportJobRequest e when Get(e.CaptureId) is { } it => ExportJobs.DecidedFiles(e.Options, it, e.Options.OutputFolder ?? SettingsImpl.ResolvedExportFolder).FirstOrDefault(),
                AnimationJobRequest a when Get(a.CaptureId) is { } it => AnimationJobs.DecidedFile(a.Options, it, a.Options.OutputFolder ?? SettingsImpl.ResolvedExportFolder),
                _ => null,
            };
        }
        catch (LibraryException) { return null; }
    }

    /// <summary>The capture's file to work on: the primary location when present, else any present one.</summary>
    internal string PresentPath(string id)
    {
        var p = Db.Read(c => c.Rows(null, """
            SELECT l.path FROM location l JOIN capture c ON c.id=l.capture_id WHERE l.capture_id=$i AND l.present=1
            ORDER BY (l.path = c.primary_path) DESC, l.path LIMIT 1
            """, r => r.GetString(0), ("$i", id)).FirstOrDefault());
        if (p is null || !File.Exists(p)) throw new LibraryException("The photo’s file cannot be found (it was moved, deleted, or its drive is not connected). Connect its drive, or rescan its folder in Settings.");
        return p;
    }

    /// <summary>A store context for the capture whose content is checked against the library's id (a file replaced since the scan is refused).</summary>
    internal StoreCaptureContext ContextFor(string id, string path, bool mayCreateFolder)
    {
        var ctx = new StoreCaptureContext(path, Hashes, mayCreateFolder);
        if (!string.Equals(ctx.Context.ContentHash, id, StringComparison.OrdinalIgnoreCase))
            throw new LibraryException("The file has changed since the folder was scanned. Rescan the folder and try again.");
        return ctx;
    }

    /// <summary>Plain-language status of an engine phase.</summary>
    internal static string PhaseText(string phase) => phase switch
    {
        "loading" => "Reading the photo",
        "registration" => "Computing depth",
        "fusion set-up" => "Preparing the 13 MP render",
        "telephoto set-up" => "Preparing telephoto detail",
        "level-0 inputs" => "Preparing full detail",
        "level-0 tiles" => "Rendering full detail",
        "render tiles" => "Rendering",
        "depth" => "Writing the depth map",
        "depth-effect tiles" => "Applying the depth effect",
        "dng" => "Writing DNG",
        "jpeg tiles" => "Rendering JPG",
        "jpeg encode" => "Writing JPG",
        "tiff16 tiles" or "tiff-linear tiles" => "Rendering TIFF",
        "tiff16 write" or "tiff-linear write" => "Writing TIFF",
        "jpg-hdr encode" => "Writing JPG + HDR gain map",
        "gain map tiles" => "Rendering the gain map",
        _ => char.ToUpperInvariant(phase[0]) + phase[1..],
    };

    /// <summary>Progress from engine phases: each expected phase has a weight; an unexpected phase keeps the bar where it is.</summary>
    internal sealed class PhaseProgress
    {
        readonly (string Phase, double Weight)[] _plan; readonly JobContext _job; readonly double _from, _to;
        public PhaseProgress(JobContext job, double from, double to, params (string, double)[] plan) { _job = job; _plan = plan; _from = from; _to = to; }
        public void On(ProgressUpdate u)
        {
            double total = _plan.Sum(p => p.Weight), before = 0; int i = Array.FindIndex(_plan, p => p.Phase == u.Phase);
            string text = PhaseText(u.Phase);
            if (u.Total > 0) text += string.Create(CultureInfo.InvariantCulture, $" ({100 * u.Done / u.Total}%)");
            if (i < 0 || total <= 0) { _job.Report(_job.Job.Progress, text); return; }
            for (int k = 0; k < i; k++) before += _plan[k].Weight;
            double f = (before + _plan[i].Weight * (u.Total > 0 ? (double)u.Done / u.Total : 0)) / total;
            _job.Report(Math.Max(_job.Job.Progress, _from + (_to - _from) * f), text);
        }
    }

    // ------------------------------------------------------------------------------------------------ analysis and cache levels

    async Task CacheJobAsync(string id, CacheLevel target, bool release, bool isAnalysis, JobContext job)
    {
        string path = PresentPath(id);
        job.Report(0, "Checking the photo");
        // a capture that the scan found unreadable fails with the message of the scan, not with the exception of the reader
        if (!release && Get(id) is { Unreadable: true } bad) throw new LibraryException(bad.Error ?? "This file is not a complete Light L16 capture.");
        var ctx = ContextFor(id, path, mayCreateFolder: !release);
        var pp = new PhaseProgress(job, 0.02, 0.9, ("loading", 1), ("registration", 30), ("fusion set-up", 15), ("telephoto set-up", 4), ("level-0 inputs", 20), ("level-0 tiles", 20));
        using var s = CaptureSession.Open(path, new SessionOptions
        {
            Context = ctx, Threads = SettingsImpl.RenderThreads, KeepGeometry = true, Progress = pp.On, DisplayCacheBytes = 0,
            Log = Log is null ? null : m => Log($"[{id[..8]}] {m}"),
        });
        var before = s.CacheLevel;
        try
        {
            if (release)
            {
                if (target < before)
                {
                    job.Report(0.5, "Releasing");
                    long was = Db.Read(c => c.Long(null, "SELECT cache_bytes FROM capture WHERE id=$i", ("$i", id)));
                    s.ReleaseCache(target + 1);
                    long now = ctx.Context.ExistingCachePath is { } cp ? ContextProbe.ReadCache(cp, id).Bytes : 0;
                    job.Note($"Released {Mb(Math.Max(0, was - now))}.");
                }
            }
            else if (target > before)
            {
                job.Report(0.02, before == CacheLevel.None ? "Computing depth" : "Rendering");
                try { await s.EnsureCacheAsync(target, job.Cancel).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // nothing half-made is kept: a tier that did not complete is removed (complete tiers stay)
                    var now = s.CacheLevel;
                    if (now < CacheLevel.Full && s.CacheSizes().Any(t => !t.Complete)) s.ReleaseCache(now + 1);
                    throw;
                }
                var sizes = s.CacheSizes();
                job.Note((before == CacheLevel.None ? "Computed the depth map; kept " : "Kept ") + $"{AnalysisCache.Name(s.CacheLevel)} ({Mb(sizes.Sum(t => t.StoredBytes))} on disk).");
            }
        }
        catch (Exception e) when (isAnalysis && e is not OperationCanceledException)
        {
            Db.Write((c, tx) => c.Exec(tx, "UPDATE capture SET depth_failed=1, error=$e WHERE id=$i", ("$e", "The analysis failed: " + (e is LibraryException ? e.Message : Plain(e))), ("$i", id)));
            Changes.Changed(new[] { id });
            throw;
        }
        finally
        {
            s.Dispose();
            var changed = RefreshContexts(new[] { id }, force: true);
            Changes.Changed(changed.Count > 0 ? changed : new List<string> { id });
            OnCacheChanged(id);
        }
        job.Report(1, "Done");
    }

    // ------------------------------------------------------------------------------------------------ Lumen import

    Task LumenImportAsync(LumenImportJobRequest r, JobContext job) => Task.Run(() =>
    {
        string path = PresentPath(r.CaptureId);
        string lris = r.SessionPath ?? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".lris");
        if (!File.Exists(lris)) throw new LibraryException("There is no Lumen session file beside this photo.");
        job.Report(0.1, "Reading the Lumen session");
        _ = ContextFor(r.CaptureId, path, mayCreateFolder: true);
        job.Cancel.ThrowIfCancellationRequested();
        LumenEditImport.Result res;
        try { res = LumenEditImport.Import(path, lris, Hashes, r.NewerToneStyle); }
        catch (Exception e) when (e is DepthEditStoreException or InvalidDataException or Google.Protobuf.InvalidProtocolBufferException)
        {
            Trace($"Lumen session {lris}: {e.Message}");
            throw new LibraryException("Lumen’s session for this photo cannot be read: the .lris file beside it is damaged or not a Lumen session. Nothing was changed; Lux keeps its own edits of the photo.");
        }
        job.Result(res.DatabasePath);
        if (res.AlreadyImported) job.Note($"Already imported (version “{res.Variant}”).");
        else if (res.Updated) job.Note($"Added the session’s settings to the version imported earlier (“{res.Variant}”).");
        else job.Note($"Imported as version “{res.Variant}”, now the active one: " + (res.Tiles > 0 ? $"{res.Tiles} edited depth tile{(res.Tiles == 1 ? "" : "s")}" : "no depth edits")
                      + (res.Settings is { } st ? "; settings " + st.Describe() : "") + ".");
        job.Note(SavedDepthEdits.LumenImportWarning.Replace("`--no-edits` renders without any edits", "the clean version is kept"));
        var changed = RefreshContexts(new[] { r.CaptureId }, force: true);
        Changes.Changed(changed.Count > 0 ? changed : new List<string> { r.CaptureId });
        job.Report(1, "Done");
    }, job.Cancel);
}

/// <summary>The export job: the same <see cref="ExportRequest"/> as <c>lux-light convert</c> builds for the same settings.</summary>
internal static class ExportJobs
{
    public static void Validate(ExportOptions o)
    {
        if (o.LongEdge is int le && le < 16) throw new LibraryException("The custom size must be at least 16 pixels on the long edge.");
        if (o.Formats.Count == 0) throw new LibraryException("Choose at least one format to export.");
        foreach (var f in o.Formats) _ = Parse(f);
        if (o.Level is < 0 or > 4) throw new LibraryException("The export size must be level 0 (full size) to 4.");
        if (o.DepthEffectFNumber is float fn && !(fn > 0)) throw new LibraryException("The f-number must be greater than 0.");
        if (o.FocusDepthMm is float fd && !(fd > 0)) throw new LibraryException("The focus distance must be greater than 0.");
        if (o.JpegQuality is int q && (q < 1 || q > 100)) throw new LibraryException("JPG quality is between 1 and 100.");
        if (string.IsNullOrWhiteSpace(o.FileNamePattern)) throw new LibraryException("The file name pattern is empty.");
        if (o.TiffColorSpace is string cs && !StillFormats.TryParseColorSpace(cs, out _)) throw new LibraryException($"“{cs}” is not a color space of the 16-bit TIFF (srgb, p3, adobergb).");
    }

    /// <summary>The name of a format in a job title: the names of the export panel for the still formats of Lux, else upper case.</summary>
    internal static string DisplayName(string f) => f.Trim().ToLowerInvariant() switch
    {
        "tiff16" => "TIFF 16-bit", "tiff-linear" => "TIFF linear (HDR)", "jpg-hdr" => "JPG + HDR gain map",
        "stereo-sbs" => "Stereo side by side", "stereo-anaglyph" => "Stereo anaglyph", "stereo-wiggle" => "Stereo wiggle", "stereo-mpo" => "Stereo MPO",
        "mesh-glb" => "3D photo (GLB)", "pointcloud-ply" => "Point cloud (PLY)",
        _ => f.ToUpperInvariant(),
    };

    /// <summary>The still formats of Lux among the format names (tiff16, tiff-linear, jpg-hdr).</summary>
    static StillFormat[] Stills(ExportOptions o)
        => o.Formats.Select(f => StillFormats.TryParse(f.Trim(), out StillFormat s) ? s : (StillFormat?)null).Where(s => s is not null).Select(s => s!.Value).Distinct().ToArray();

    /// <summary>The stereo formats among the format names (stereo-sbs, stereo-anaglyph, stereo-wiggle, stereo-mpo).</summary>
    static StereoFormat[] Stereo(ExportOptions o)
        => o.Formats.Select(f => StereoExport.TryParse(f.Trim().ToLowerInvariant(), out StereoFormat s) ? s : (StereoFormat?)null).Where(s => s is not null).Select(s => s!.Value).Distinct().ToArray();

    /// <summary>The 3D formats among the format names (mesh-glb, pointcloud-ply).</summary>
    static SceneFormat[] Scene(ExportOptions o)
        => o.Formats.Select(f => SceneExport.TryParse(f.Trim().ToLowerInvariant(), out SceneFormat s) ? s : (SceneFormat?)null).Where(s => s is not null).Select(s => s!.Value).Distinct().ToArray();

    static (ExportImageFormat? Format, bool DepthMap) Parse(string f) => f.Trim().ToLowerInvariant() switch
    {
        "dng" => (ExportImageFormat.Dng, false),
        "jpg" or "jpeg" => (ExportImageFormat.Jpeg, false),
        "jpg+depth" or "jpeg+depth" => (ExportImageFormat.JpegGDepth, false),
        "hdr" => (ExportImageFormat.Hdr, false),
        "ppm" => (ExportImageFormat.Ppm, false),
        "depth" => (null, true),
        "tiff16" or "tiff-linear" or "jpg-hdr" => (null, false),   // the still formats of Lux: see Stills
        "stereo-sbs" or "stereo-anaglyph" or "stereo-wiggle" or "stereo-mpo" => (null, false),   // the stereo formats: see Stereo
        "mesh-glb" or "pointcloud-ply" => (null, false),   // the 3D formats: see Scene
        _ => throw new LibraryException($"“{f}” is not a format Lux can export (dng, jpg, jpg+depth, hdr, ppm, tiff16, tiff-linear, jpg-hdr, depth, stereo-sbs, stereo-anaglyph, stereo-wiggle, stereo-mpo, mesh-glb, pointcloud-ply)."),
    };

    /// <summary>A custom long edge as `convert --level L --size W,H`: W,H from the photo's full size (after its saved crop when edits are used),
    /// L the smallest level at least that size (the export resamples from it). Null when the options give no custom size or it is not smaller.</summary>
    public static (int Level, (int W, int H) Size)? CustomSize(ExportOptions o, CaptureItem item, int turn = 0)
    {
        if (o.LongEdge is not int le || item.LevelSizes.Count < 5) return null;
        var full = o.UseSavedEdits && item.Width > 0 ? (item.Width, item.Height) : (item.LevelSizes[0].Width, item.LevelSizes[0].Height);
        if (turn is 90 or 270) full = (full.Item2, full.Item1);   // the held turn the export adds (a quarter turn swaps the sides)
        if (le >= Math.Max(full.Item1, full.Item2)) return null;
        double k = (double)le / Math.Max(full.Item1, full.Item2);
        (int W, int H) size = (Math.Max(1, (int)Math.Round(full.Item1 * k)), Math.Max(1, (int)Math.Round(full.Item2 * k)));
        int level = 0;
        for (int L = 4; L >= 0; L--)
        {
            var d = item.LevelSizes[L];
            if (Math.Max(d.Width, d.Height) >= le) { level = L; break; }
        }
        return (level, size);
    }

    /// <summary>The request `convert` makes from the same options (ConvertCmd.TryPlan): settings given here override the saved ones.</summary>
    public static ExportRequest BuildRequest(ExportOptions o, string outDir, string stem, CaptureItem? item = null, int turn = 0)
    {
        var parsed = o.Formats.Select(Parse).ToList();
        var formats = parsed.Where(p => p.Format is not null).Select(p => p.Format!.Value).Distinct().ToArray();
        bool depth = parsed.Any(p => p.DepthMap);
        var overridden = new HashSet<string>();
        if (o.DepthEffectFNumber is not null || o.DepthEffectOff) overridden.Add("f-number");
        if (o.FocusDepthMm is not null || o.DepthEffectOff) overridden.Add("focus");
        if (o.Adjustments is not null) foreach (var id in new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "13" }) overridden.Add(id);
        if (o.NewerToneStyle is not null) overridden.Add("tone-v2");
        var custom = item is null ? null : CustomSize(o, item, turn);
        var req = new ExportRequest
        {
            OverriddenSettings = overridden,
            Formats = formats,
            DepthMap = depth,
            Level = o.Level,
            DepthEffectFNumber = o.DepthEffectOff ? null : o.DepthEffectFNumber,
            FocusDepthMm = o.DepthEffectOff ? null : o.FocusDepthMm,
            UseSavedEdits = o.UseSavedEdits,
            EditsVariant = o.UseSavedEdits ? o.EditsVariant : null,
            CacheUse = o.UseCache ? CacheUse.ReadWriteGeometry : CacheUse.None,
            JpegQuality = o.JpegQuality,
            DngKeepHighlights = o.DngKeepHighlights,
            TeleCheck = o.TeleCheck,
            JpegEmbedIcc = o.EmbedColorProfile,
            Stills = Stills(o),
            Stereo = Stereo(o) is { Length: > 0 } stereo ? new StereoRequest { Formats = stereo } : null,
            Scene = Scene(o) is { Length: > 0 } scene ? new SceneRequest { Formats = scene } : null,
            Tiff = new TiffOptions { ColorSpace = o.TiffColorSpace is string tcs && StillFormats.TryParseColorSpace(tcs, out var tiffCs) ? tiffCs : TiffColorSpace.Srgb },
            JpegSharpening = DisplayIspTuning.GuiSharpeningProperty,
            Adjustments = o.Adjustments?.Validate(),
            OutDirectory = outDir,
            Stem = stem,
        };
        if (custom is { } c) req = req with { Level = c.Level, Size = c.Size };
        if (o.NewerToneStyle == true) req = req with { JpegV2 = true, ToneMappingType = "light_v2" };
        return req;
    }

    /// <summary>The files an export of <paramref name="item"/> writes into <paramref name="outDir"/> after the collision rule as the folder is now
    /// (rename: the first free -2, -3 …; skip / replace: the plain names).</summary>
    public static IReadOnlyList<string> DecidedFiles(ExportOptions o, CaptureItem item, string outDir)
    {
        string stem = Stem(o, item);
        var probe = BuildRequest(o, outDir, stem, item);
        if (o.IfExists == ExistingFilePolicy.Rename && FilesOf(probe, outDir, stem).Any(File.Exists))
            for (int i = 2; ; i++) { string s = $"{stem}-{i}"; if (!FilesOf(probe, outDir, s).Any(File.Exists)) { stem = s; break; } }
        return FilesOf(BuildRequest(o, outDir, stem, item), outDir, stem).ToList();
    }

    /// <summary>The files an export of <paramref name="item"/> writes into <paramref name="outDir"/> (before renaming for an existing file).</summary>
    public static IReadOnlyList<string> PlannedFiles(ExportOptions o, CaptureItem item, string outDir)
    {
        string stem = Stem(o, item);
        return FilesOf(BuildRequest(o, outDir, stem, item), outDir, stem).ToList();
    }

    internal static string Stem(ExportOptions o, CaptureItem item)
    {
        var t = item.CaptureTime;
        var s = o.FileNamePattern.Replace("{name}", item.Name)
                                 .Replace("{date}", t?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nodate")
                                 .Replace("{time}", t?.ToString("HHmmss", CultureInfo.InvariantCulture) ?? "000000");
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? item.Name : s.Trim();
    }

    static IEnumerable<string> FilesOf(ExportRequest r, string dir, string stem)
    {
        foreach (var f in r.Formats) yield return Path.Combine(dir, stem + ExportFormats.Extension(f));
        foreach (var f in r.Stills) yield return Path.Combine(dir, stem + StillFormats.Suffix(f));
        if (r.Stereo is { } st) foreach (var f in st.Formats) yield return Path.Combine(dir, stem + StereoExport.Suffix(f, st));
        if (r.Scene is { } sc) foreach (var f in sc.Formats) yield return Path.Combine(dir, stem + SceneExport.Suffix(f));
        if (r.DepthMap) { yield return Path.Combine(dir, stem + "_depth.f32"); yield return Path.Combine(dir, stem + "_depth.jpg"); }
    }

    /// <summary>The free space that an export needs before it starts. A full-size DNG is about 100 MB and a JPEG is a few MB. With less space,
    /// the export fails at once, not after the render.</summary>
    const long MinExportFreeBytes = 128L << 20;

    /// <summary>Checks the export folder before the render starts. Lux must be able to make the folder and write to it, and the drive must have
    /// some free space. Otherwise the job fails at once with a plain message.</summary>
    internal static void CheckExportFolder(string outDir)
    {
        try { Directory.CreateDirectory(outDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new LibraryException($"Lux cannot create the export folder {outDir}. Choose another folder in the export settings.");
        }
        LocalContextStore.ForgetProbes(outDir);
        if (!LocalContextStore.IsWritable(outDir))
            throw new LibraryException($"Lux is not allowed to write to {outDir}. Choose another export folder, or allow writing to this one.");
        if (Lux.Engine.Mtp.MtpPuller.FreeSpace(outDir) is long free && free < MinExportFreeBytes)
            throw new LibraryException($"The drive of {outDir} is full ({LibraryCore.Mb(Math.Max(0, free))} free). Free some space or choose another export folder.");
    }

    public static async Task RunAsync(LibraryCore lib, ExportJobRequest r, JobContext job)
    {
        var o = r.Options;
        Validate(o);
        var item = lib.Get(r.CaptureId) ?? throw new LibraryException("The photo is no longer in the library.");
        string path = lib.PresentPath(r.CaptureId);
        if (item.Unreadable) throw new LibraryException(item.Error ?? "This file is not a complete Light L16 capture.");
        string outDir = o.OutputFolder ?? lib.SettingsImpl.ResolvedExportFolder;
        job.Report(0, "Checking the photo");
        CheckExportFolder(outDir);
        var ctx = lib.ContextFor(r.CaptureId, path, mayCreateFolder: false);
        string stem = Stem(o, item);
        var probe = BuildRequest(o, outDir, stem, item);
        if (FilesOf(probe, outDir, stem).Any(File.Exists))
        {
            switch (o.IfExists)
            {
                case ExistingFilePolicy.Skip:
                    job.Note("Skipped: the files already exist in the export folder.");
                    job.Report(1, "Done (skipped)");
                    return;
                case ExistingFilePolicy.Rename:
                    for (int i = 2; ; i++) { string s = $"{stem}-{i}"; if (!FilesOf(probe, outDir, s).Any(File.Exists)) { stem = s; break; } }
                    break;
            }
        }
        job.Planned(FilesOf(BuildRequest(o, outDir, stem, item), outDir, stem).FirstOrDefault());
        var phases = new List<(string, double)> { ("loading", 1), ("registration", 30), ("fusion set-up", 10), ("telephoto set-up", 3),
                                                   ("level-0 inputs", 15), ("level-0 tiles", 15), ("render tiles", 5), ("depth-effect tiles", 5), ("dng", 8), ("depth", 3), ("jpeg tiles", 6), ("jpeg encode", 4) };
        // the still formats of Lux come after the others, in this order
        foreach (var f in Stills(o))
            phases.AddRange(f switch
            {
                StillFormat.Tiff16 => new[] { ("tiff16 tiles", 6.0), ("tiff16 write", 4.0) },
                StillFormat.TiffLinear => new[] { ("tiff-linear tiles", 6.0), ("tiff-linear write", 4.0) },
                _ => new[] { ("jpg-hdr encode", 4.0), ("gain map tiles", 8.0) },
            });
        var pp = new LibraryCore.PhaseProgress(job, 0.02, 0.98, phases.ToArray());
        // a canceled export leaves nothing of this run behind: the exporter removes the file it was writing, the job the ones it finished
        var startedUtc = DateTime.UtcNow;
        var existed = FilesOf(BuildRequest(o, outDir, stem, item), outDir, stem).Where(File.Exists).ToHashSet();
        try { await ExportOnce(lib, r, job, o, ctx, path, outDir, stem, pp, item).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            foreach (var f in FilesOf(BuildRequest(o, outDir, stem, item), outDir, stem))
                try { if (File.Exists(f) && (!existed.Contains(f) || File.GetLastWriteTimeUtc(f) >= startedUtc)) File.Delete(f); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>One export through the session API (<see cref="CaptureSession.ExportAsync"/>), which applies the capture's saved settings and a
    /// Lumen session's own geometry exactly as `convert` does — the viewer and the export use one path.</summary>
    static async Task ExportOnce(LibraryCore lib, ExportJobRequest r, JobContext job, ExportOptions o, StoreCaptureContext ctx,
                                 string path, string outDir, string stem, LibraryCore.PhaseProgress pp, CaptureItem item)
    {
        int threads = lib.SettingsImpl.RenderThreads;
        using var s = CaptureSession.Open(path, new SessionOptions { Context = ctx, Threads = threads, Progress = pp.On, DisplayCacheBytes = 0, KeepGeometry = true, TeleCheck = o.TeleCheck,
                                                                     Log = lib.Log is null ? null : m => lib.Log($"[{r.CaptureId[..8]}] {m}") });
        if (o.UseSavedEdits && o.EditsVariant is not null && !ctx.HasSavedEdits) throw new LibraryException($"This photo has no saved version “{o.EditsVariant}”.");
        // a photo shown the way the camera was held is exported that way, unless the export (or the setting) asks for
        // the capture as recorded: the held turn as Lumen's own rotate — the transform "Rotate as the camera was held" would save
        var upright = lib.UprightTransform(r.CaptureId, o.Unturned, out int turn);
        var req = BuildRequest(o, outDir, stem, item, turn) with { Context = ctx, Progress = pp.On };
        // a stereo pair needs to know how the camera was held: turned, the two wide lenses are one above the other
        if (req.Stereo is { } stereoReq) req = req with { Stereo = stereoReq with { HeldTurn = Math.Max(0, Lux.Store.HeldTurn.Degrees(s.Lri)) } };
        if (upright is not null) req = req with { Transform = upright, OverriddenSettings = new HashSet<string>(req.OverriddenSettings ?? new HashSet<string>()) { "transform" } };
        Exporter.Result res;
        try { res = await s.ExportAsync(req, job.Cancel).ConfigureAwait(false); }
        catch (ExportRequestException e) { throw new LibraryException(e.Message.Replace("--edits ", "version ")); }
        // as `convert` does: a level-0 build's geometry + depth is kept when the capture already has a context (not for a Lumen-session render,
        // whose geometry is the session's)
        if (o.UseCache && s.SavedState?.SessionGeometry is null && Exporter.BuildLevelFor(req) == 0 && ctx.Context.ExistingRoot is not null && (o.TeleCheck ? !s.HasKeptGeometry : s.CacheLevel == CacheLevel.None))
        {
            try { await s.EnsureCacheAsync(CacheLevel.Geometry, job.Cancel).ConfigureAwait(false); job.Note("Kept the computed depth for later exports."); lib.OnCacheChanged(r.CaptureId); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
        var files = res.Outputs.Select(x => x.Path).ToList();
        job.Result(files.FirstOrDefault(), files);
        foreach (var n in res.Notes) job.Note(n);
        foreach (var n in res.Refusals) job.Note("Not written: " + n);
        var changed = lib.RefreshContexts(new[] { r.CaptureId });
        lib.Changes.Changed(changed);
        job.Report(1, $"Done: {files.Count} file{(files.Count == 1 ? "" : "s")}");
    }
}
