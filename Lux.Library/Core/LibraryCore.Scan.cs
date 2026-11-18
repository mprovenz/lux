using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Core;

/// <summary>The capture facts read from its header (<see cref="CaptureSession.Info"/>).</summary>
internal sealed record CaptureMeta(DateTime? CaptureTime, int FocalLengthMm, int StackFrames, string ReferenceModule, IReadOnlyList<string> Modules,
                                   float AllInFocusFNumber, int SensorW, int SensorH)
{
    /// <summary>The version of what is read from a capture's header: 2 = with exposure, orientation and level sizes; 3 = with the predicted
    /// motion blur. Captures read by an older version are read again at the next scan.</summary>
    public const int Version = 3;
    public int Iso { get; init; } = 100;
    public double ExposureSeconds { get; init; }
    public float? Gain { get; init; }
    public PhotoOrientation Held { get; init; } = PhotoOrientation.Unknown;
    public PhotoOrientation ViewOrientation { get; init; }
    public string LevelSizes { get; init; } = "";
    /// <summary>The predicted motion blur in full-size pixels (<see cref="Lux.Engine.Lri.CaptureMotion.BlurOf"/>). Null when the capture
    /// has no usable gyroscope samples.</summary>
    public float? BlurPixels { get; init; }
    /// <summary>The Lumen session beside the capture: its classification and change stamp (null = none).</summary>
    public (LumenSessionState State, string Stamp)? Lumen { get; init; }

    public static CaptureMeta From(CaptureInfo i) => new(i.CaptureTime, i.FocalLengthMm, i.StackFrames, i.ReferenceModule, i.Modules, i.AllInFocusFNumber, i.SensorSize.W, i.SensorSize.H);

    /// <summary>Everything the library keeps from a capture's header: the facts of <see cref="CaptureSession.Info"/>, and the exposure as Lumen
    /// writes it into its exports (`Exporter.BuildTags`: ISO = 100 × image gain truncated; the integration time), the held orientation from the
    /// accelerometer, the view-preference orientation, and the export level sizes (the export window, `ExportWindow.Compute`).</summary>
    public static CaptureMeta Read(CaptureSession s, string path)
    {
        var lri = s.Lri; var vp = lri.ViewPreferencesBlock;
        float? gain = vp is not null && vp.HasImageGain ? vp.ImageGain : null;
        long ns = vp is not null && vp.HasImageIntegrationTimeNs ? (long)vp.ImageIntegrationTimeNs
                : vp is not null && vp.HasDisplayIntegrationTimeNs ? (long)vp.DisplayIntegrationTimeNs : (long)lri.Modules[lri.ReferenceModule].Module.SensorExposure;
        // the accelerometer's rule, shared with `lux-light convert --upright` (Lux.Store.HeldTurn)
        var held = Lux.Store.HeldTurn.Degrees(lri) switch
        {
            0 => PhotoOrientation.Normal, 90 => PhotoOrientation.Rotate90, 180 => PhotoOrientation.Rotate180, 270 => PhotoOrientation.Rotate270, _ => PhotoOrientation.Unknown,
        };
        var o = (vp is not null && vp.HasOrientation ? vp : lri.Header.ViewPreferences is { HasOrientation: true } hv ? hv : null)?.Orientation;
        var view = o switch
        {
            null => PhotoOrientation.Normal,
            Ltpb.ViewPreferences.Types.Orientation.Normal => PhotoOrientation.Normal,
            Ltpb.ViewPreferences.Types.Orientation.Rot90Cw => PhotoOrientation.Rotate90,
            Ltpb.ViewPreferences.Types.Orientation.Rot90Ccw => PhotoOrientation.Rotate270,
            Ltpb.ViewPreferences.Types.Orientation.Rot180 => PhotoOrientation.Rotate180,
            _ => PhotoOrientation.Mirrored,
        };
        var dims = Lux.Store.LumenEditImport.Levels(lri).ExportDims;
        var meta = From(s.Info) with
        {
            Iso = (int)(100f * (gain ?? 1f)), ExposureSeconds = (float)((float)ns * 1e-6f) / 1000f, Gain = gain, Held = held, ViewOrientation = view,
            LevelSizes = string.Join(";", dims.Select(d => $"{d.W}x{d.H}")),
            BlurPixels = Lux.Engine.Lri.CaptureMotion.BlurOf(lri).Pixels,
        };
        string lris = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".lris");
        if (File.Exists(lris)) meta = meta with { Lumen = (LibraryCore.ClassifyLumen(lri, lris), LibraryCore.LrisStamp(lris)) };
        return meta;
    }
    /// <summary>Telephoto = no 28 mm (A) module: a B + C capture (the camera picks A + B or B + C by zoom; only A modules give wide parallax).</summary>
    public bool IsTelephoto => Modules.Count > 0 && !Modules.Any(m => m.StartsWith('A'));
}

internal sealed partial class LibraryCore
{
    /// <summary>Files read at once while scanning (each read is a whole capture, ~160 MB, when it has to be hashed).</summary>
    internal int ScanParallelism => Math.Clamp(SettingsImpl.RenderThreads / 6, 1, 4);

    sealed record Found(string Path, FileFacts Facts, string Fp, string Hash, CaptureMeta? Meta, string? Error);
    long _readSinceCollect;

    /// <summary>Every capture the engine opens is read whole into one large array (~160 MB) that is garbage soon after: request a background
    /// (non-blocking) full collection after every ~1 GB read, so that scans and thumbnails stay near what is in flight instead of letting the
    /// collector's budget grow by several GB. Non-blocking: the user interface is not paused for it.</summary>
    internal void CollectAfter(long bytes)
    {
        if (Interlocked.Add(ref _readSinceCollect, bytes) <= (1L << 30)) return;
        Interlocked.Exchange(ref _readSinceCollect, 0);
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    /// <summary>Every <c>.lri</c> under <paramref name="root"/> (recursively, following linked folders once, skipping <c>LuxImageContext</c>).</summary>
    internal static List<string> EnumerateCaptures(string root, CancellationToken cancel)
    {
        var o = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            string real;
            try { real = new DirectoryInfo(dir).ResolveLinkTarget(true)?.FullName ?? dir; } catch (IOException) { real = dir; }
            if (!visited.Add(real) || depth > 32) continue;
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
                {
                    if (e is DirectoryInfo d)
                    {
                        if (d.Name == ImageContext.FolderName || d.Name.StartsWith('.')) continue;
                        stack.Push((d.FullName, depth + 1));
                    }
                    else if (e.Name.EndsWith(".lri", StringComparison.OrdinalIgnoreCase)) o.Add(e.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        o.Sort(StringComparer.Ordinal);
        return o;
    }

    /// <summary>Scan one indexed folder: identity by content hash.</summary>
    internal async Task ScanFolderAsync(long folderId, JobContext job)
    {
        var folder = FolderRow(folderId) ?? throw new LibraryException("The folder is no longer in the library.");
        job.Report(0, "Looking for photos");
        LocalContextStore.ForgetProbes(folder.Path);   // whether its folders can be written is probed afresh at every scan
        var cancel = job.Cancel;
        var known = Db.Read(c => c.Rows(null, "SELECT path, capture_id, size, mtime, fp, present FROM location WHERE folder_id=$f",
                                        r => (Path: r.GetString(0), Id: r.GetString(1), Size: r.GetInt64(2), Mtime: r.GetInt64(3), Fp: r.GetString(4), Present: r.GetInt64(5) != 0),
                                        ("$f", folderId))).ToDictionary(k => k.Path, StringComparer.Ordinal);
        bool reachable = Directory.Exists(folder.Path);
        var files = reachable ? EnumerateCaptures(folder.Path, cancel) : new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = new List<string>();
        var work = new List<(string Path, FileFacts Facts)>();
        foreach (var path in files)
        {
            cancel.ThrowIfCancellationRequested();
            var f = FileFacts.Stat(path);
            if (!f.Exists) continue;
            seen.Add(path);
            if (known.TryGetValue(path, out var k) && k.Size == f.Size && k.Mtime == f.MtimeTicks)
            {
                string fp;
                try { fp = FileFacts.QuickFingerprint(path, f.Size); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (fp == k.Fp) { unchanged.Add(path); continue; }
            }
            work.Add((path, f));
        }
        // captures whose header was read by an older version of the library (v1.6 added exposure, orientation, level sizes): read again
        var stale = Db.Read(c => c.Rows(null, "SELECT id FROM capture WHERE has_meta=1 AND meta_version < $v", r => r.GetString(0), ("$v", (long)CaptureMeta.Version))).ToHashSet();
        if (stale.Count > 0)
            foreach (var p in unchanged.Where(p => stale.Remove(known[p].Id)).ToList()) { unchanged.Remove(p); work.Add((p, FileFacts.Stat(p))); }
        var now = DateTime.UtcNow.Ticks;
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var added = new ConcurrentBag<string>();
        // unchanged files: seen now (a location that was missing and is back is present again)
        Db.Write((c, tx) =>
        {
            foreach (var p in unchanged)
            {
                var k = known[p];
                if (!k.Present) { c.Exec(tx, "UPDATE location SET present=1, last_seen_utc=$u WHERE path=$p", ("$u", now), ("$p", p)); affected.Add(k.Id); }
                else c.Exec(tx, "UPDATE location SET last_seen_utc=$u WHERE path=$p", ("$u", now), ("$p", p));
            }
        });

        int total = work.Count, done = 0;
        job.Items(0, total);
        job.Report(0, total == 0 ? "Checking for changes" : $"Reading {total} photo{(total == 1 ? "" : "s")}");
        var pending = new List<Found>();
        var pendingLock = new object();
        void Flush(bool force)
        {
            List<Found> batch;
            lock (pendingLock)
            {
                if (pending.Count == 0 || (!force && pending.Count < 16)) return;
                batch = pending.ToList(); pending.Clear();
            }
            var (aff, news) = WriteFound(folderId, batch);
            lock (affected) foreach (var a in aff) affected.Add(a);
            foreach (var n in news) added.Add(n);
            Changes.Added(news);
        }
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = cancel }, (w, ct) =>
        {
            var found = Identify(w.Path, w.Facts, ct);
            if (found is not null) lock (pendingLock) pending.Add(found);
            int d = Interlocked.Increment(ref done);
            job.Items(d, total);
            job.Report(total == 0 ? 1 : 0.9 * d / total, $"Reading photos ({d} of {total})");
            Flush(false);
            return ValueTask.CompletedTask;
        });
        Flush(true);

        // files gone from this folder: their locations are flagged, not dropped
        var gone = known.Values.Where(k => k.Present && !seen.Contains(k.Path)).ToList();
        if (gone.Count > 0)
            Db.Write((c, tx) => { foreach (var g in gone) { c.Exec(tx, "UPDATE location SET present=0 WHERE path=$p", ("$p", g.Path)); affected.Add(g.Id); } });

        job.Report(0.92, "Updating the library");
        RecomputeDerived(affected);
        // Lumen sessions added, changed or removed beside captures that did not change themselves
        ClassifyLumenSessions(Db.Read(c => c.Rows(null, "SELECT DISTINCT capture_id FROM location WHERE folder_id=$f", r => r.GetString(0), ("$f", folderId))), cancel);
        // the context mirror of everything in the folder (cheap: a stat per file unless something changed)
        var all = Db.Read(c => c.Rows(null, "SELECT DISTINCT capture_id FROM location WHERE folder_id=$f", r => r.GetString(0), ("$f", folderId)));
        var changedCtx = RefreshContexts(all);
        // ratings kept in the library only (their context could not be written before): written now where possible
        if (FlushPendingRatings(all) > 0) changedCtx.AddRange(RefreshContexts(all));
        Db.Write((c, tx) => c.Exec(tx, "UPDATE folder SET last_scan_utc=$u, scan_generation=scan_generation+1 WHERE id=$f", ("$u", DateTime.UtcNow.Ticks), ("$f", folderId)));
        Changes.Changed(affected.Concat(changedCtx).Except(added));
        RaiseFoldersChanged();
        int missing = gone.Count;
        job.Note($"{files.Count} photo{(files.Count == 1 ? "" : "s")} found, {added.Count} new, {total - added.Count} changed or moved, {missing} no longer there"
                 + (reachable ? "" : " (the folder cannot be reached)"));
        job.Report(1, "Done");

        // follow-up work: thumbnails for what has none (or an outdated one), analysis when the setting asks for it
        if (Options.MakeThumbnailsAfterScan)
        {
            var need = Db.Read(c => c.Rows(null, """
                SELECT DISTINCT c.id FROM capture c JOIN location l ON l.capture_id=c.id
                WHERE l.folder_id=$f AND c.hidden=0 AND c.missing=0 AND c.unreadable=0 AND (c.thumb_state IS NULL OR c.thumb_state <> c.thumb_key)
                """, r => r.GetString(0), ("$f", folderId)));
            if (need.Count > 0) PrefetchThumbnails(need);
        }
        if (SettingsImpl.AnalyseNewCaptures && !added.IsEmpty) AnalyseNow(added.ToList());
    }

    /// <summary>Hash (from the hash cache when path, size, time and fingerprint match) and, for a capture not known yet, read its header.</summary>
    Found? Identify(string path, FileFacts facts, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string fp;
        try { fp = FileFacts.QuickFingerprint(path, facts.Size); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        string? hash = Hashes.Lookup(path, facts.Size, facts.MtimeTicks, fp);
        if (hash is not null && Db.Read(c => c.Long(null, "SELECT count(*) FROM capture WHERE id=$i AND ((has_meta=1 AND meta_version >= $v) OR unreadable=1)", ("$i", hash), ("$v", (long)CaptureMeta.Version))) > 0)
            return new Found(path, facts, fp, hash, null, null);
        CaptureMeta? meta = null; string? err = null;
        try
        {
            using (var s = CaptureSession.Open(path, new SessionOptions { Threads = 1 }))
            {
                hash ??= Convert.ToHexString(SHA256.HashData(s.Lri.Data)).ToLowerInvariant();
                meta = CaptureMeta.Read(s, path);
            }
            // each read is a whole capture (~160 MB, large-object heap) that is garbage at once: collect after every ~1 GB read so that a scan
            // stays near (files in flight × 160 MB) instead of letting the collector's budget grow to several GB
            CollectAfter(facts.Size);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // the exception text of the reader goes to the log only; the user sees a plain message
            Trace($"{Path.GetFileName(path)} cannot be read as a capture: {e.GetType().Name}: {e.Message}");
            err = e is IOException or UnauthorizedAccessException ? Plain(e)
                : "This file is not a complete Light L16 capture: it may be damaged or cut short (a copy that did not finish). Copy it again from the camera or a backup, then rescan the folder.";
            try { hash ??= ImageContext.HashFile(path); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { return null; }
        }
        Hashes.Put(path, facts.Size, facts.MtimeTicks, fp, hash);
        return new Found(path, facts, fp, hash, meta, err);
    }

    (List<string> Affected, List<string> Added) WriteFound(long folderId, List<Found> batch)
    {
        var affected = new List<string>(); var added = new List<string>();
        long now = DateTime.UtcNow.Ticks;
        Db.Write((c, tx) =>
        {
            foreach (var f in batch)
            {
                bool exists = c.Long(tx, "SELECT count(*) FROM capture WHERE id=$i", ("$i", f.Hash)) > 0;
                string name = Path.GetFileNameWithoutExtension(f.Path);
                if (!exists)
                {
                    var m = f.Meta;
                    c.Exec(tx, """
                        INSERT INTO capture(id, primary_path, name, name_key, location_count, file_size, file_mtime, added_utc, has_meta, capture_time, focal_mm,
                            stack_frames, ref_module, modules, module_count, aif_fnumber, sensor_w, sensor_h, telephoto, unreadable, error, search)
                        VALUES($i, $p, $n, $nk, 1, $s, $m, $u, $hm, $ct, $fl, $sf, $rm, $mods, $mc, $af, $sw, $sh, $te, $ur, $er, $se)
                        """, ("$i", f.Hash), ("$p", f.Path), ("$n", name), ("$nk", name.ToLowerInvariant()), ("$s", f.Facts.Size), ("$m", f.Facts.MtimeTicks), ("$u", now),
                        ("$hm", m is null ? 0L : 1L), ("$ct", m?.CaptureTime?.Ticks), ("$fl", (long)(m?.FocalLengthMm ?? 0)), ("$sf", (long)(m?.StackFrames ?? 1)),
                        ("$rm", m?.ReferenceModule ?? ""), ("$mods", m is null ? "" : string.Join(",", m.Modules)), ("$mc", (long)(m?.Modules.Count ?? 0)),
                        ("$af", (double)(m?.AllInFocusFNumber ?? 0f)), ("$sw", (long)(m?.SensorW ?? 0)), ("$sh", (long)(m?.SensorH ?? 0)), ("$te", m is not null && m.IsTelephoto ? 1L : 0L),
                        ("$ur", m is null ? 1L : 0L), ("$er", m is null ? f.Error : null), ("$se", SearchText(name, new[] { f.Path }, m?.ReferenceModule ?? "", null)));
                    added.Add(f.Hash);
                }
                else if (f.Meta is { } m)
                {
                    c.Exec(tx, """
                        UPDATE capture SET has_meta=1, capture_time=$ct, focal_mm=$fl, stack_frames=$sf, ref_module=$rm, modules=$mods, module_count=$mc,
                            aif_fnumber=$af, sensor_w=$sw, sensor_h=$sh, telephoto=$te, unreadable=0, error=NULL WHERE id=$i AND (has_meta=0 OR meta_version < $v)
                        """, ("$i", f.Hash), ("$ct", m.CaptureTime?.Ticks), ("$fl", (long)m.FocalLengthMm), ("$sf", (long)m.StackFrames), ("$rm", m.ReferenceModule),
                        ("$mods", string.Join(",", m.Modules)), ("$mc", (long)m.Modules.Count), ("$af", (double)m.AllInFocusFNumber), ("$sw", (long)m.SensorW), ("$sh", (long)m.SensorH),
                        ("$te", m.IsTelephoto ? 1L : 0L), ("$v", (long)CaptureMeta.Version));
                }
                if (f.Meta is { } mx)
                {
                    var d0 = mx.LevelSizes.Split(';')[0].Split('x');
                    c.Exec(tx, """
                        UPDATE capture SET meta_version=$v, iso=$iso, exposure_s=$ex, gain=$g, held_orientation=$ho, vp_orientation=$vo, level_sizes=$ls,
                            out_w=CASE WHEN transform_crop=0 AND transform_orientation=0 THEN $w ELSE out_w END, out_h=CASE WHEN transform_crop=0 AND transform_orientation=0 THEN $h ELSE out_h END,
                            lumen_class=$lc, lris_stamp=$lst, edits_stamp=NULL, blur_px=$bp
                        WHERE id=$i
                        """, ("$i", f.Hash), ("$v", (long)CaptureMeta.Version), ("$iso", (long)mx.Iso), ("$ex", mx.ExposureSeconds), ("$g", (double?)mx.Gain),
                        ("$ho", (long)mx.Held), ("$vo", (long)mx.ViewOrientation), ("$ls", mx.LevelSizes), ("$w", long.Parse(d0[0], CultureInfo.InvariantCulture)),
                        ("$h", long.Parse(d0[1], CultureInfo.InvariantCulture)), ("$lc", (long)(mx.Lumen?.State ?? LumenSessionState.None)), ("$lst", mx.Lumen?.Stamp),
                        ("$bp", (double?)mx.BlurPixels));
                }
                string? old = c.Scalar(tx, "SELECT capture_id FROM location WHERE path=$p", ("$p", f.Path)) as string;
                c.Exec(tx, """
                    INSERT INTO location(path, capture_id, folder_id, dir, size, mtime, fp, present, last_seen_utc) VALUES($p, $i, $f, $d, $s, $m, $fp, 1, $u)
                    ON CONFLICT(path) DO UPDATE SET capture_id=excluded.capture_id, folder_id=excluded.folder_id, dir=excluded.dir, size=excluded.size,
                        mtime=excluded.mtime, fp=excluded.fp, present=1, last_seen_utc=excluded.last_seen_utc
                    """, ("$p", f.Path), ("$i", f.Hash), ("$f", folderId), ("$d", Path.GetDirectoryName(f.Path) ?? ""), ("$s", f.Facts.Size), ("$m", f.Facts.MtimeTicks), ("$fp", f.Fp), ("$u", now));
                affected.Add(f.Hash);
                if (old is not null && old != f.Hash) affected.Add(old);
            }
        });
        return (affected, added);
    }

    // ------------------------------------------------------------------------------------------------ derived fields

    /// <summary>Recompute what follows from a capture's locations: primary path and name, location count, missing, Lumen session, search
    /// text; a capture moved to another directory takes its <c>LuxImageContext</c> data along; stale locations of a capture that is present
    /// elsewhere are forgotten; a capture left with no location at all leaves the library.</summary>
    internal void RecomputeDerived(IEnumerable<string> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return;
        var removed = new List<string>();
        foreach (var chunk in list.Chunk(64))
        {
            var rows = Db.Read(c => chunk.Select(id => (Id: id,
                Locs: c.Rows(null, "SELECT path, dir, present, size, mtime FROM location WHERE capture_id=$i ORDER BY path", r => (Path: r.GetString(0), Dir: r.GetString(1), Present: r.GetInt64(2) != 0, Size: r.GetInt64(3), Mtime: r.GetInt64(4)), ("$i", id)),
                Meta: c.Rows(null, "SELECT ref_module, edit_variant FROM capture WHERE id=$i", r => (Ref: r.GetString(0), Variant: r.Str(1)), ("$i", id)).FirstOrDefault())).ToList());
            var updates = new List<Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>>();
            foreach (var (id, locs, meta) in rows)
            {
                if (meta.Ref is null) continue;   // not a capture row (gone meanwhile)
                if (locs.Count == 0)
                {
                    // its only file now holds another capture (or its location was forgotten): missing, not dropped — rating, albums and
                    // thumbnail stay until the content turns up again anywhere in the library
                    updates.Add((c, tx) => c.Exec(tx, "UPDATE capture SET missing=1, location_count=0 WHERE id=$i", ("$i", id)));
                    continue;
                }
                var present = locs.Where(l => l.Present).ToList();
                var stale = locs.Where(l => !l.Present).ToList();
                if (present.Count > 0 && stale.Count > 0)
                {
                    CarryContext(id, stale.Select(s => s.Dir).Distinct().ToList(), present.Select(p => p.Dir).Distinct().ToList());
                    updates.Add((c, tx) => { foreach (var s in stale) c.Exec(tx, "DELETE FROM location WHERE path=$p", ("$p", s.Path)); });
                }
                var use = present.Count > 0 ? present : locs;
                var primary = use.FirstOrDefault(l => File.Exists(ContextProbe.EditsPath(l.Dir, id)));
                if (primary.Path is null) primary = use.FirstOrDefault(l => File.Exists(ContextProbe.CachePath(l.Dir, id)));
                if (primary.Path is null) primary = use[0];
                string name = Path.GetFileNameWithoutExtension(primary.Path);
                bool lris = present.Any(l => File.Exists(Path.Combine(l.Dir, Path.GetFileNameWithoutExtension(l.Path) + ".lris")));
                string search = SearchText(name, use.Select(l => l.Path), meta.Ref, meta.Variant);
                updates.Add((c, tx) => c.Exec(tx, """
                    UPDATE capture SET primary_path=$p, name=$n, name_key=$nk, location_count=$lc, missing=$mi, has_lris=$hl, search=$se, file_size=$s, file_mtime=$m
                    WHERE id=$i
                    """, ("$i", id), ("$p", primary.Path), ("$n", name), ("$nk", name.ToLowerInvariant()), ("$lc", (long)use.Count), ("$mi", present.Count == 0 ? 1L : 0L),
                    ("$hl", lris ? 1L : 0L), ("$se", search), ("$s", primary.Size), ("$m", primary.Mtime)));
            }
            Db.Write((c, tx) => { foreach (var u in updates) u(c, tx); });
        }
        if (removed.Count > 0) Changes.Removed(removed);
    }

    /// <summary>A capture whose file moved to another directory finds its <c>LuxImageContext</c> data there: the edit database and the analysis
    /// cache are moved from the old directory when the new one has none of its own (never overwritten, never copied over user work).</summary>
    void CarryContext(string id, List<string> oldDirs, List<string> newDirs)
    {
        if (oldDirs.All(newDirs.Contains)) return;
        string target = newDirs[0];
        foreach (var (pathOf, files) in new (Func<string, string, string>, string[])[]
                 { (ContextProbe.EditsPath, new[] { "", "-journal", "-wal", "-shm" }), (ContextProbe.CachePath, new[] { "", "-wal", "-shm" }) })
        {
            if (newDirs.Any(d => File.Exists(pathOf(d, id)))) continue;
            var from = oldDirs.Where(d => !newDirs.Contains(d)).FirstOrDefault(d => File.Exists(pathOf(d, id)));
            if (from is null) continue;
            string src = pathOf(from, id), dst = pathOf(target, id);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                foreach (var suffix in files)
                    if (File.Exists(src + suffix)) File.Move(src + suffix, dst + suffix);
                Trace($"moved the LuxImageContext data of {id[..12]}… from {from} to {target}");
                // leave no empty LuxImageContext behind
                var d = Path.GetDirectoryName(src);   // LuxImageContext/edits (or cache), then LuxImageContext
                for (int k = 0; k < 2 && d is not null; k++, d = Path.GetDirectoryName(d))
                {
                    if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) break;
                    Directory.Delete(d);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace($"could not move the LuxImageContext data of {id[..12]}… to {target}: {e.Message}");
            }
        }
    }
}

/// <summary>Watches the indexed folders while the library is open: a changed set of captures rescans the folder (one waiting scan per
/// folder, after a quiet period); a changed edit database or analysis cache refreshes only that capture's mirror.</summary>
internal sealed class FolderWatcher : IDisposable
{
    readonly LibraryCore _lib;
    readonly Dictionary<long, FileSystemWatcher> _w = new();
    readonly Dictionary<long, Timer> _scanTimers = new();
    readonly HashSet<string> _ctx = new(StringComparer.Ordinal);
    readonly Timer _ctxTimer;
    bool _disposed;
    const int QuietMs = 1500;

    public FolderWatcher(LibraryCore lib)
    {
        _lib = lib;
        _ctxTimer = new Timer(_ => FlushContexts(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Match the watchers to the folder list. A folder that cannot be reached has no watcher: a watcher does not follow a folder
    /// that goes away and comes back, so it is made again when the folder is back.</summary>
    public void Sync()
    {
        var folders = _lib.Db.Read(c => c.Rows(null, "SELECT id, path FROM folder", r => (r.GetInt64(0), r.GetString(1))));
        lock (_w)
        {
            if (_disposed) return;
            foreach (var id in _w.Keys.Except(folders.Where(f => Directory.Exists(f.Item2)).Select(f => f.Item1)).ToList()) { _w[id].Dispose(); _w.Remove(id); }
            foreach (var (id, path) in folders)
            {
                if (_w.ContainsKey(id) || !Directory.Exists(path)) continue;
                try
                {
                    var fsw = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    long fid = id;
                    fsw.Created += (_, e) => On(fid, e.FullPath);
                    fsw.Deleted += (_, e) => On(fid, e.FullPath);
                    fsw.Changed += (_, e) => On(fid, e.FullPath);
                    fsw.Renamed += (_, e) => { On(fid, e.OldFullPath); On(fid, e.FullPath); };
                    fsw.Error += (_, _) => Scan(fid);
                    fsw.EnableRaisingEvents = true;
                    _w[id] = fsw;
                }
                catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException) { _lib.Trace("cannot watch " + path + ": " + e.Message); }
            }
        }
    }

    void On(long folderId, string path)
    {
        string name = Path.GetFileName(path);
        string? parent = Path.GetFileName(Path.GetDirectoryName(path));
        string? grand = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
        if (grand == Lux.Store.ImageContext.FolderName && (parent == Lux.Store.ImageContext.EditsFolderName || parent == Lux.Store.ImageContext.CacheFolderName))
        {
            // LuxImageContext/edits/<hash>.luxedits[-journal] or cache/<hash>.luxcache[-wal]: refresh that capture's mirror only
            string id = name.Split('.')[0];
            if (id.Length == 64) lock (_ctx) { _ctx.Add(id); _ctxTimer.Change(QuietMs, Timeout.Infinite); }
            return;
        }
        if (path.Contains(Path.DirectorySeparatorChar + Lux.Store.ImageContext.FolderName + Path.DirectorySeparatorChar, StringComparison.Ordinal) || name == Lux.Store.ImageContext.FolderName) return;
        if (name.EndsWith(".lri", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lris", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(name))
            Scan(folderId);
    }

    void Scan(long folderId)
    {
        lock (_w)
        {
            if (_disposed) return;
            if (!_scanTimers.TryGetValue(folderId, out var t))
                _scanTimers[folderId] = t = new Timer(_ => { try { if (!_lib.IsDisposed) _lib.Queue.EnqueueQuietScan(folderId); } catch (Exception e) { _lib.Trace("watch: " + e.Message); } }, null, Timeout.Infinite, Timeout.Infinite);
            t.Change(QuietMs, Timeout.Infinite);
        }
    }

    void FlushContexts()
    {
        List<string> ids;
        lock (_ctx) { ids = _ctx.ToList(); _ctx.Clear(); }
        if (ids.Count == 0 || _lib.IsDisposed) return;
        try
        {
            var known = _lib.Db.Read(c => ids.Where(id => c.Long(null, "SELECT count(*) FROM capture WHERE id=$i", ("$i", id)) > 0).ToList());
            var changed = _lib.RefreshContexts(known);
            _lib.Changes.Changed(changed);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { _lib.Trace("context refresh: " + e.Message); }
    }

    public void Dispose()
    {
        lock (_w)
        {
            _disposed = true;
            foreach (var w in _w.Values) w.Dispose();
            foreach (var t in _scanTimers.Values) t.Dispose();
            _w.Clear(); _scanTimers.Clear();
        }
        _ctxTimer.Dispose();
    }
}
