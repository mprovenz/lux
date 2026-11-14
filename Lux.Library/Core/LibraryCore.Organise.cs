using System.Globalization;
using Lux.Engine.Session;
using Lux.Store;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    // ------------------------------------------------------------------------------------------------ the context mirror

    sealed record CtxRow(string Id, string PrimaryPath, string? ContextDir, string? EditsStamp, string? CacheStamp, int Rating, RatingSource Source, string ThumbKey,
                         string? ThumbState, CacheLevel Level, List<string> Dirs, string LevelSizes = "");

    List<CtxRow> CtxRows(IReadOnlyCollection<string> ids) => Db.Read(c => ids.Select(id =>
        c.Rows(null, "SELECT context_dir, edits_stamp, cache_stamp, rating, rating_source, thumb_key, thumb_state, cache_level, primary_path, level_sizes FROM capture WHERE id=$i", r =>
        {
            var dirs = c.Rows(null, "SELECT dir FROM location WHERE capture_id=$i AND present=1 ORDER BY path", rr => rr.GetString(0), ("$i", id)).Distinct().ToList();
            string? pdir = Path.GetDirectoryName(r.GetString(8));
            if (pdir is not null && dirs.Remove(pdir)) dirs.Insert(0, pdir);
            return new CtxRow(id, r.GetString(8), r.Str(0), r.Str(1), r.Str(2), r.GetInt32(3), (RatingSource)r.GetInt32(4), r.GetString(5), r.Str(6), (CacheLevel)r.GetInt32(7), dirs, r.GetString(9));
        }, ("$i", id)).FirstOrDefault()).Where(x => x is not null).Select(x => x!).ToList());

    static string BesideRoot(string dir) => Path.Combine(dir, ImageContext.FolderName);

    /// <summary>Where a capture's context files are: the edit database and the cache, each next to a present location first (the primary one
    /// first), then in the local context store; and where the context lives as a whole (by the edit database, else the cache).</summary>
    (string? Edits, string? Cache, ContextLocation Place) ResolveContext(string id, List<string> dirs)
    {
        var roots = dirs.Select(BesideRoot).ToList();
        string? local = LocalContextStore.Current?.RootFor(id);
        if (local is not null && Directory.Exists(local)) roots.Add(local);
        string? edits = roots.Select(r => ContextProbe.EditsPathIn(r, id)).FirstOrDefault(File.Exists);
        string? cache = roots.Select(r => ContextProbe.CachePathIn(r, id)).FirstOrDefault(File.Exists);
        string? decisive = edits ?? cache;
        var place = decisive is null ? ContextLocation.None : local is not null && decisive.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? ContextLocation.LocalStore : ContextLocation.BesideCapture;
        return (edits, cache, place);
    }

    /// <summary>Data in the local store for a capture whose folder can now be written moves next to it; data in both places is settled (an
    /// interrupted move finished, a conflict resolved for the data next to the capture, the local copy kept aside). Never while a job of the
    /// capture runs (it may have the files open).</summary>
    void ReconcileLocal(CtxRow row)
    {
        if (LocalContextStore.Current is not { } store || row.Dirs.Count == 0 || !Directory.Exists(store.RootFor(row.Id))) return;
        if (Queue.ActiveKindOf(row.Id) is not null) return;
        string primary = row.PrimaryPath;
        if (!File.Exists(primary)) return;
        string dir = Path.GetDirectoryName(primary)!;
        bool writable = LocalContextStore.IsWritable(Directory.Exists(BesideRoot(dir)) ? BesideRoot(dir) : dir);
        // moving a photo's data next to it copies and checks its files (a full-detail copy is ~270 MB): shown in the Queue
        using var act = writable ? ActivityLog.Start(row.Id, $"Moving the data of {NameOf(row.Id)} next to the photo", "Copying and checking") : null;
        try
        {
            var r = store.Reconcile(ImageContext.ForKnownCapture(primary, row.Id, Hashes, store), writable, Log);
            if (r.Moved + r.Finished + r.Superseded > 0) Trace($"{row.Id[..12]}…: moved {r.Moved}, finished {r.Finished}, kept aside {r.Superseded} ({LibraryCore.Mb(r.Bytes)})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace($"{row.Id[..12]}…: its data stays in the local store for now: {e.Message}");
        }
    }

    /// <summary>Bring the mirror (rating, edit summary, cache level, thumbnail key, where the context lives, a superseded copy) up to date with
    /// what each capture's context holds; only files whose stamp changed are read. Local-store data of a capture whose folder can now be
    /// written is moved next to it first. Applies the rating rule (see <see cref="ILibrary.SetRatingAsync"/>). Returns the captures whose
    /// mirror changed.</summary>
    internal List<string> RefreshContexts(IReadOnlyCollection<string> ids, bool force = false)
    {
        var changed = new List<string>();
        var staleThumbs = new List<string>();
        var store = LocalContextStore.Current;
        foreach (var chunk in ids.Chunk(128))
        {
            var rows = CtxRows(chunk);
            var updates = new List<Action<SqliteConnection, SqliteTransaction>>();
            foreach (var row in rows)
            {
                string id = row.Id;
                if (row.Dirs.Count == 0) continue;   // missing: keep the last mirror
                ReconcileLocal(row);
                var (ePath, cPath, place) = ResolveContext(id, row.Dirs);
                string where = ePath is not null ? Path.GetDirectoryName(Path.GetDirectoryName(ePath))! : cPath is not null ? Path.GetDirectoryName(Path.GetDirectoryName(cPath))! : BesideRoot(row.Dirs[0]);
                string eStamp = ePath is null ? "-" : ePath + "|" + ContextProbe.Stamp(ePath), cStamp = cPath is null ? "-" : cPath + "|" + ContextProbe.Stamp(cPath);
                string? superseded = store?.SupersededOf(id);
                bool eChanged = force || eStamp != row.EditsStamp || where != row.ContextDir, cChanged = force || cStamp != row.CacheStamp;
                if (!eChanged && !cChanged)
                {
                    // the superseded copy can appear or go without a stamp change
                    string? had = Db.Read(c => c.Scalar(null, "SELECT superseded FROM capture WHERE id=$i", ("$i", id)) as string);
                    if (had != superseded) { updates.Add((c, tx) => c.Exec(tx, "UPDATE capture SET superseded=$s WHERE id=$i", ("$s", superseded), ("$i", id))); changed.Add(id); }
                    continue;
                }
                var sets = new List<string> { "context_dir=$dir", "context_location=$cl0", "superseded=$sup" };
                var ps = new List<(string, object?)> { ("$i", id), ("$dir", where), ("$cl0", (long)place), ("$sup", superseded) };
                if (eChanged)
                {
                    int rating = row.Rating; var source = row.Source;
                    if (ePath is not null)
                    {
                        var em = ContextProbe.ReadEdits(ePath, id);
                        if (em.Rating is int own) { rating = own; source = RatingSource.Capture; }
                        else if (row.Source == RatingSource.Library && row.Rating > 0 && em.Error is null)
                        {
                            // a library-only rating and a context database without one: the rating moves into the context database
                            if (TryWriteContextRating(ePath, id, row.Rating)) { source = RatingSource.Capture; eStamp = ePath + "|" + ContextProbe.Stamp(ePath); }
                        }
                        else if (row.Source == RatingSource.Capture) { rating = 0; source = RatingSource.None; }   // the context database no longer has one
                        var s = em.Summary;
                        sets.Add("rating=$r, rating_source=$rs, edit_variant=$ev, edit_ops=$eo, edit_source=$es, edit_variants=$evs, edit_settings=$est, edit_unreliable=$eu, thumb_key=$tk, error=$er");
                        ps.AddRange(new (string, object?)[] { ("$r", (long)rating), ("$rs", (long)source), ("$ev", s.Variant), ("$eo", (long)s.OperationCount), ("$es", (long)s.Source),
                            ("$evs", (long)s.VariantCount), ("$est", s.HasSettings ? 1L : 0L), ("$eu", s.HistoryUnreliable ? 1L : 0L), ("$tk", em.ThumbKey), ("$er", em.Error) });
                        if (em.ThumbKey != row.ThumbKey && row.ThumbState is not null) staleThumbs.Add(id);
                        // the saved settings in short (depth effect, focus, tone, crop / rotation, tone style) and the full-size size as Lumen makes it
                        var rs = em.Settings;   // the variant's settings with the capture's Lux recipe over them (null when neither exists)
                        var lv0 = ParseSizes(row.LevelSizes).FirstOrDefault();
                        var tr = rs?.ToExportTransform();
                        (int W, int H) outSize = (lv0.Width, lv0.Height);
                        if (tr is not null) outSize = ((int)((float)lv0.Width * (tr.CropX1 - tr.CropX0)), (int)((float)lv0.Height * (tr.CropY1 - tr.CropY0)));
                        bool cropRot = tr is not null && !(tr.CropX0 == 0f && tr.CropY0 == 0f && tr.CropX1 == 1f && tr.CropY1 == 1f && tr.Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }));
                        sets.Add("saved_fnumber=$sf, saved_focus=$sfo, adjustments=$adj, transform_orientation=$tor, transform_crop=$tcr, tone_v2=$tv2, out_w=$ow, out_h=$oh, geometry_decided=$gd");
                        ps.Add(("$gd", em.GeometryDecided ? 1L : 0L));
                        ps.AddRange(new (string, object?)[] {
                            ("$sf", rs is not null && rs.HasSettings && rs.Present[0] ? (double?)rs.Values[0] : null),
                            ("$sfo", rs is not null && rs.HasSettings && rs.Present[1] && rs.Values[1] > 0f ? (double?)rs.Values[1] : null),
                            ("$adj", rs is null ? null : AdjustmentsSummary(rs)), ("$tor", (long)OrientationOf(tr?.Matrix)), ("$tcr", cropRot ? 1L : 0L),
                            ("$tv2", rs?.ToneV2 == true ? 1L : 0L), ("$ow", (long)outSize.W), ("$oh", (long)outSize.H) });
                    }
                    else
                    {
                        // no context database (never had one, or it was removed): a rating that lived there is kept in the library and written
                        // into a new one by the next flush of pending ratings (ratings always go with the photo)
                        if (row.Source == RatingSource.Capture) source = rating > 0 ? RatingSource.Library : RatingSource.None;
                        sets.Add("rating_source=$rs, edit_variant=NULL, edit_ops=0, edit_source=0, edit_variants=0, edit_settings=0, edit_unreliable=0, thumb_key='clean', "
                               + "saved_fnumber=NULL, saved_focus=NULL, adjustments=NULL, transform_orientation=0, transform_crop=0, tone_v2=0, out_w=$ow, out_h=$oh, geometry_decided=0");
                        var lv0n = ParseSizes(row.LevelSizes).FirstOrDefault();
                        ps.Add(("$ow", (long)lv0n.Width)); ps.Add(("$oh", (long)lv0n.Height));
                        ps.Add(("$rs", (long)source));
                        if (row.ThumbKey != "clean" && row.ThumbState is not null) staleThumbs.Add(id);
                    }
                    sets.Add("edits_stamp=$estamp"); ps.Add(("$estamp", eStamp));
                }
                if (cChanged)
                {
                    var (level, bytes) = cPath is null ? (CacheLevel.None, 0L) : ContextProbe.ReadCache(cPath, id);
                    sets.Add("cache_level=$cl, cache_bytes=$cb, cache_stamp=$cstamp" + (level >= CacheLevel.Geometry ? ", depth_failed=0" : ""));
                    ps.AddRange(new (string, object?)[] { ("$cl", (long)level), ("$cb", bytes), ("$cstamp", cStamp) });
                }
                string sql = $"UPDATE capture SET {string.Join(", ", sets)} WHERE id=$i";
                updates.Add((c, tx) => c.Exec(tx, sql, ps.ToArray()));
                changed.Add(id);
            }
            if (updates.Count > 0) Db.Write((c, tx) => { foreach (var u in updates) u(c, tx); });
        }
        // search text carries the active variant
        if (changed.Count > 0) RecomputeSearch(changed);
        if (staleThumbs.Count > 0) Thumbs.Regenerate(staleThumbs);
        return changed;
    }

    void RecomputeSearch(List<string> ids) => Db.Write((c, tx) =>
    {
        foreach (var id in ids)
        {
            var row = c.Rows(tx, "SELECT name, ref_module, edit_variant FROM capture WHERE id=$i", r => (r.GetString(0), r.GetString(1), r.Str(2)), ("$i", id)).FirstOrDefault();
            if (row.Item1 is null) continue;
            var paths = c.Rows(tx, "SELECT path FROM location WHERE capture_id=$i ORDER BY path", r => r.GetString(0), ("$i", id));
            c.Exec(tx, "UPDATE capture SET search=$s WHERE id=$i", ("$s", SearchText(row.Item1, paths, row.Item2, row.Item3)), ("$i", id));
        }
    });

    bool TryWriteContextRating(string editsPath, string id, int rating)
    {
        try
        {
            using var s = DepthEditStore.Open(editsPath, id);
            s.SetCaptureRating(rating);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Trace($"rating of {id[..12]}… not written to its context database: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------ ratings

    /// <summary>Write one capture's rating into its context database (next to it, or in the local store when its folder cannot be written),
    /// creating the database unless the rating is 0. Never throws: returns where the rating now lives.</summary>
    (RatingSource Source, ContextLocation Place) WriteRating(string id, string? presentPath, int rating, List<string> dirs)
    {
        try
        {
            var (ePath, _, place) = ResolveContext(id, dirs);
            if (ePath is not null) return TryWriteContextRating(ePath, id, rating) ? (RatingSource.Capture, place) : (rating > 0 ? RatingSource.Library : RatingSource.None, place);
            if (rating == 0) return (RatingSource.None, place);                       // clearing never creates a context database
            if (!SettingsImpl.WriteRatingsToCaptureFolders || presentPath is null) return (RatingSource.Library, place);
            var ctx = ImageContext.ForKnownCapture(presentPath, id, Hashes, LocalContextStore.Current);
            using (var s = DepthEditStore.OpenOrCreate(ctx)) s.SetCaptureRating(rating);
            return (RatingSource.Capture, ctx.Place == ContextPlace.Local ? ContextLocation.LocalStore : ContextLocation.BesideCapture);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Trace($"rating of {id[..12]}… kept in the library for now: {e.Message}");
            return (rating > 0 ? RatingSource.Library : RatingSource.None, ContextLocation.None);
        }
    }

    public Task SetRatingAsync(string captureId, int rating, CancellationToken cancel = default) => SetRatingsAsync(new[] { captureId }, rating, cancel);

    public Task SetRatingsAsync(IReadOnlyCollection<string> captureIds, int rating, CancellationToken cancel = default) => Task.Run(() =>
    {
        if (rating < 0 || rating > 5) throw new LibraryException("A rating is between 0 (none) and 5.");
        var rows = CtxRows(captureIds.Distinct().ToList());
        var results = new List<(string Id, RatingSource Source, ContextLocation Place)>();
        foreach (var row in rows)
        {
            cancel.ThrowIfCancellationRequested();
            string? present = row.Dirs.Count > 0 && File.Exists(row.PrimaryPath) ? row.PrimaryPath : null;
            var (src, place) = WriteRating(row.Id, present, rating, row.Dirs);
            results.Add((row.Id, src, place));
        }
        Db.Write((c, tx) =>
        {
            foreach (var (id, src, place) in results)
                c.Exec(tx, "UPDATE capture SET rating=$r, rating_source=$s, rating_utc=$u, context_location=CASE WHEN $p > 0 THEN $p ELSE context_location END, edits_stamp=NULL WHERE id=$i",
                       ("$r", (long)rating), ("$s", (long)src), ("$u", DateTime.UtcNow.Ticks), ("$p", (long)place), ("$i", id));
        });
        Changes.Changed(results.Select(r => r.Id));
    }, cancel);

    /// <summary>Ratings kept in the library only (the context could not be written, or the switch was off) are written into context databases
    /// when that becomes possible: at the end of every scan, and every few minutes while the library is open.</summary>
    internal int FlushPendingRatings(IReadOnlyCollection<string>? ids = null)
    {
        if (!SettingsImpl.WriteRatingsToCaptureFolders) return 0;
        var pending = Db.Read(c => c.Rows(null, "SELECT id, rating FROM capture WHERE rating_source=2 AND rating > 0 AND missing=0 AND hidden=0", r => (r.GetString(0), r.GetInt32(1))));
        if (ids is not null) { var set = ids.ToHashSet(); pending = pending.Where(p => set.Contains(p.Item1)).ToList(); }
        if (pending.Count == 0) return 0;
        var rows = CtxRows(pending.Select(p => p.Item1).ToList()).ToDictionary(r => r.Id);
        int written = 0; var done = new List<(string, ContextLocation)>();
        foreach (var (id, rating) in pending)
        {
            if (!rows.TryGetValue(id, out var row) || row.Dirs.Count == 0 || !File.Exists(row.PrimaryPath)) continue;
            var (src, place) = WriteRating(id, row.PrimaryPath, rating, row.Dirs);
            if (src == RatingSource.Capture) { written++; done.Add((id, place)); }
        }
        if (done.Count > 0)
        {
            Db.Write((c, tx) => { foreach (var (id, place) in done) c.Exec(tx, "UPDATE capture SET rating_source=1, context_location=$p, edits_stamp=NULL WHERE id=$i", ("$p", (long)place), ("$i", id)); });
            Changes.Changed(done.Select(d => d.Item1));
            Trace($"{written} rating(s) kept in the library were written into the photos' context databases");
        }
        return written;
    }

    // ------------------------------------------------------------------------------------------------ albums

    public IReadOnlyList<Album> GetAlbums() => Db.Read(c => c.Rows(null, """
        SELECT a.id, a.name, (SELECT count(*) FROM album_item ai JOIN capture cp ON cp.id=ai.capture_id WHERE ai.album_id=a.id AND cp.hidden=0), a.created_utc
        FROM album a ORDER BY a.name COLLATE NOCASE
        """, r => new Album(r.GetInt64(0), r.GetString(1), r.GetInt32(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc))));

    public IReadOnlyList<Album> GetAlbumsOf(string captureId) => Db.Read(c => c.Rows(null, """
        SELECT a.id, a.name, (SELECT count(*) FROM album_item x JOIN capture cp ON cp.id=x.capture_id WHERE x.album_id=a.id AND cp.hidden=0), a.created_utc
        FROM album a JOIN album_item ai ON ai.album_id=a.id WHERE ai.capture_id=$i ORDER BY a.name COLLATE NOCASE
        """, r => new Album(r.GetInt64(0), r.GetString(1), r.GetInt32(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc)), ("$i", captureId)));

    static string AlbumName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new LibraryException("An album needs a name.");
        if (n.Length > 200) throw new LibraryException("That album name is too long.");
        return n;
    }

    public Task<Album> CreateAlbumAsync(string name, CancellationToken cancel = default) => Task.Run(() =>
    {
        string n = AlbumName(name);
        long id;
        try { id = Db.Write((c, tx) => c.Long(tx, "INSERT INTO album(name, created_utc) VALUES($n, $u) RETURNING id", ("$n", n), ("$u", DateTime.UtcNow.Ticks))); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new LibraryException($"There is already an album called “{n}”."); }
        RaiseAlbumsChanged();
        return GetAlbums().First(a => a.Id == id);
    }, cancel);

    public Task RenameAlbumAsync(long albumId, string newName, CancellationToken cancel = default) => Task.Run(() =>
    {
        string n = AlbumName(newName);
        try
        {
            if (Db.Write((c, tx) => c.Exec(tx, "UPDATE album SET name=$n WHERE id=$a", ("$n", n), ("$a", albumId))) == 0) throw new LibraryException("That album no longer exists.");
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new LibraryException($"There is already an album called “{n}”."); }
        RaiseAlbumsChanged();
    }, cancel);

    public Task DeleteAlbumAsync(long albumId, CancellationToken cancel = default) => Task.Run(() =>
    {
        var members = Db.Read(c => c.Rows(null, "SELECT capture_id FROM album_item WHERE album_id=$a", r => r.GetString(0), ("$a", albumId)));
        Db.Write((c, tx) => c.Exec(tx, "DELETE FROM album WHERE id=$a", ("$a", albumId)));
        RaiseAlbumsChanged();
        Changes.Changed(members);
    }, cancel);

    public Task AddToAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => Task.Run(() =>
    {
        Db.Write((c, tx) =>
        {
            if (c.Long(tx, "SELECT count(*) FROM album WHERE id=$a", ("$a", albumId)) == 0) throw new LibraryException("That album no longer exists.");
            foreach (var id in captureIds.Distinct())
                c.Exec(tx, "INSERT OR IGNORE INTO album_item(album_id, capture_id, added_utc) SELECT $a, id, $u FROM capture WHERE id=$i", ("$a", albumId), ("$i", id), ("$u", DateTime.UtcNow.Ticks));
        });
        RaiseAlbumsChanged();
        Changes.Changed(captureIds);
    }, cancel);

    public Task RemoveFromAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => Task.Run(() =>
    {
        Db.Write((c, tx) => { foreach (var id in captureIds.Distinct()) c.Exec(tx, "DELETE FROM album_item WHERE album_id=$a AND capture_id=$i", ("$a", albumId), ("$i", id)); });
        RaiseAlbumsChanged();
        Changes.Changed(captureIds);
    }, cancel);

    // ------------------------------------------------------------------------------------------------ remove / restore / delete

    public Task RemoveFromLibraryAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => Task.Run(() =>
    {
        Db.Write((c, tx) => { foreach (var id in captureIds.Distinct()) c.Exec(tx, "UPDATE capture SET hidden=1 WHERE id=$i", ("$i", id)); });
        Changes.Removed(captureIds);
        RaiseFoldersChanged(); RaiseAlbumsChanged();
    }, cancel);

    public IReadOnlyList<CaptureItem> GetRemoved()
        => Db.Read(c => c.Rows(null, $"SELECT {ItemColumns} FROM capture c WHERE c.hidden=1 ORDER BY c.name_key, c.id", ReadItem));

    public Task RestoreRemovedAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => Task.Run(() =>
    {
        Db.Write((c, tx) => { foreach (var id in captureIds.Distinct()) c.Exec(tx, "UPDATE capture SET hidden=0 WHERE id=$i", ("$i", id)); });
        Changes.Added(captureIds);
        RaiseFoldersChanged(); RaiseAlbumsChanged();
    }, cancel);

    public Task<DeleteResult> DeleteFilesAsync(IReadOnlyCollection<string> captureIds, DeleteOptions options, CancellationToken cancel = default) => Task.Run(() =>
    {
        var failures = new List<string>(); var deleted = new List<string>();
        foreach (var id in captureIds.Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            Queue.CancelForCapture(id);
            var locs = GetLocations(id).Where(l => l.Exists).ToList();
            bool ok = true;
            foreach (var l in locs)
            {
                string dir = Path.GetDirectoryName(l.Path)!, stem = Path.GetFileNameWithoutExtension(l.Path);
                try
                {
                    Trash.Remove(l.Path, options.MoveToTrash, Options.TrashDirectoryOverride);
                    string lris = Path.Combine(dir, stem + ".lris");
                    if (options.IncludeLumenSession && File.Exists(lris)) Trash.Remove(lris, options.MoveToTrash, Options.TrashDirectoryOverride);
                    if (options.IncludeCache)
                        foreach (var s in new[] { "", "-wal", "-shm" }) { var p = ContextProbe.CachePath(dir, id) + s; if (File.Exists(p)) File.Delete(p); }
                    if (options.IncludeEdits)
                        foreach (var s in new[] { "", "-journal" }) { var p = ContextProbe.EditsPath(dir, id) + s; if (File.Exists(p)) Trash.Remove(p, options.MoveToTrash, Options.TrashDirectoryOverride); }
                    // and its data in the local context store, by the same options
                    if (LocalContextStore.Current?.RootFor(id) is { } lr && Directory.Exists(lr))
                    {
                        if (options.IncludeCache) foreach (var s in new[] { "", "-wal", "-shm" }) { var p = ContextProbe.CachePathIn(lr, id) + s; if (File.Exists(p)) File.Delete(p); }
                        if (options.IncludeEdits) foreach (var s in new[] { "", "-journal" }) { var p = ContextProbe.EditsPathIn(lr, id) + s; if (File.Exists(p)) Trash.Remove(p, options.MoveToTrash, Options.TrashDirectoryOverride); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    ok = false; failures.Add($"{stem}: {Plain(e)}");
                }
            }
            if (ok)
            {
                Db.Write((c, tx) => c.Exec(tx, "DELETE FROM capture WHERE id=$i", ("$i", id)));
                deleted.Add(id);
            }
        }
        Changes.Removed(deleted);
        RaiseFoldersChanged(); RaiseAlbumsChanged();
        return new DeleteResult(deleted.Count, failures);
    }, cancel);

    // ------------------------------------------------------------------------------------------------ cache actions

    IReadOnlyList<long> EnqueueEach(IEnumerable<string> ids, Func<string, JobRequest?> make)
    {
        var o = new List<long>();
        foreach (var id in ids.Distinct()) if (make(id) is { } r) o.Add(Queue.Enqueue(r));
        return o;
    }

    internal (CacheLevel Level, long FolderId)? CacheState(string id)
        => Db.Read(c => c.Rows(null, "SELECT c.cache_level, (SELECT l.folder_id FROM location l WHERE l.capture_id=c.id ORDER BY l.present DESC, l.path LIMIT 1) FROM capture c WHERE c.id=$i",
                               r => ((CacheLevel, long)?)((CacheLevel)r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt64(1)), ("$i", id)).FirstOrDefault());

    public IReadOnlyList<long> AnalyseNow(IReadOnlyCollection<string> captureIds) => EnqueueEach(captureIds, id =>
    {
        if (CacheState(id) is not { } s) return null;
        var target = SettingsImpl.EffectiveCacheLevel(s.FolderId);
        if (target < CacheLevel.Geometry) target = CacheLevel.Geometry;
        return s.Level >= target ? null : new AnalysisJobRequest(id, target);
    });

    public IReadOnlyList<long> UpgradeToFullDetail(IReadOnlyCollection<string> captureIds) => SetCacheLevel(captureIds, CacheLevel.Full);

    public IReadOnlyList<long> ReleaseFullDetail(IReadOnlyCollection<string> captureIds) => EnqueueEach(captureIds, id =>
        CacheState(id) is { Level: CacheLevel.Full } ? new CacheJobRequest(id, CacheLevel.Linear13) { Release = true } : null);

    public IReadOnlyList<long> SetCacheLevel(IReadOnlyCollection<string> captureIds, CacheLevel level) => EnqueueEach(captureIds, id =>
    {
        if (CacheState(id) is not { } s || s.Level == level) return null;
        return level < s.Level ? new CacheJobRequest(id, level) { Release = true } : new CacheJobRequest(id, level);
    });

    /// <summary>Typical stored sizes per tier (measured: geometry 14–17 MB, 13 MP tier 49–57 MB, full detail 182–205 MB).</summary>
    internal static long TypicalTierBytes(CacheLevel l) => l switch { CacheLevel.Geometry => 15_500_000, CacheLevel.Linear13 => 53_000_000, CacheLevel.Full => 195_000_000, _ => 0 };
    static long TypicalBytes(CacheLevel l) { long s = 0; for (var x = CacheLevel.Geometry; x <= l; x++) s += TypicalTierBytes(x); return s; }

    public CacheEstimate EstimateCache(IReadOnlyCollection<string> captureIds, CacheLevel target)
    {
        var rows = Db.Read(c => captureIds.Distinct().Select(id => c.Rows(null, "SELECT cache_level, cache_bytes FROM capture WHERE id=$i", r => ((CacheLevel)r.GetInt32(0), r.GetInt64(1)), ("$i", id)).FirstOrDefault()).ToList());
        long add = 0, free = 0; int n = 0, measured = 0;
        foreach (var (level, bytes) in rows)
        {
            if (level == target) continue;
            n++;
            if (target > level) add += TypicalBytes(target) - TypicalBytes(level);
            else
            {
                // what goes is the part above the target: measured total minus the typical size of what stays
                if (bytes > 0) { measured++; free += Math.Max(0, bytes - TypicalBytes(target)); }
                else free += TypicalBytes(level) - TypicalBytes(target);
            }
        }
        return new CacheEstimate(add, free, n, measured);
    }

    // ------------------------------------------------------------------------------------------------ export and Lumen

    public IReadOnlyList<long> Export(IReadOnlyCollection<string> captureIds, ExportOptions options)
    {
        ExportJobs.Validate(options);
        var ids = captureIds.Distinct().ToList();
        // several captures exported together are one batch in the Queue (a group with a row per file)
        string? batch = ids.Count > 1 ? Guid.NewGuid().ToString("N")[..12] : null;
        string? title = batch is null ? null : $"Export of {ids.Count} photos · {DetailOf(new ExportJobRequest(ids[0], options))}";
        return EnqueueEach(ids, id => new ExportJobRequest(id, options) { BatchId = batch, BatchTitle = title });
    }

    public IReadOnlyList<ExportTarget> ExportTargets(IReadOnlyCollection<string> captureIds, ExportOptions options)
    {
        ExportJobs.Validate(options);
        var outDir = options.OutputFolder ?? SettingsImpl.ResolvedExportFolder;
        var list = new List<ExportTarget>();
        foreach (var id in captureIds.Distinct())
        {
            if (Get(id) is not { } item) continue;
            var files = ExportJobs.PlannedFiles(options, item, outDir);
            list.Add(new ExportTarget(id, files, files.Where(File.Exists).ToList()));
        }
        return list;
    }

    public IReadOnlyList<long> Animate(IReadOnlyCollection<string> captureIds, AnimationOptions options)
    {
        AnimationJobs.Validate(options, CanWriteVideo);
        var ids = captureIds.Distinct().Where(id => Get(id) is { } it && AnimationEffects.For(it).First(e => e.Effect == options.Effect).Available).ToList();
        string? batch = ids.Count > 1 ? Guid.NewGuid().ToString("N")[..12] : null;
        string? title = batch is null ? null : $"Animation of {ids.Count} photos · {AnimationJobs.Detail(options)}";
        return EnqueueEach(ids, id => new AnimationJobRequest(id, options) { BatchId = batch, BatchTitle = title });
    }

    public async Task<int> KeepHeldTurnAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        int n = 0;
        foreach (var id in captureIds.Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            if (Get(id) is not { DisplayRotation: not 0 }) continue;
            // the photo's open editing session when there is one (Develop shows the change at once), else a session of its own
            var open = OpenSession(id);
            IPhotoSession? own = open is null ? await OpenPhotoAsync(id, null, cancel).ConfigureAwait(false) : null;
            var ph = (IPhotoSession?)open ?? own!;
            try
            {
                if (!ph.Edit.CanRotateAsHeld) continue;
                ph.Edit.RotateAsHeld();
                await ph.Edit.SaveAsync(cancel).ConfigureAwait(false);
                n++;
            }
            finally { if (own is not null) await own.DisposeAsync().ConfigureAwait(false); }
        }
        var changed = RefreshContexts(captureIds.ToList(), force: true);
        Changes.Changed(changed.Count > 0 ? changed : captureIds.ToList());
        return n;
    }

    bool? _video;
    public bool CanWriteVideo => _video ??= Lux.Engine.Pipeline.Export.VideoEncoder.FindFfmpeg() is not null;

    public async Task<IAnimationSource> OpenAnimationAsync(string captureId, CancellationToken cancel = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(ILibrary));
        var item = Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        try { return await Task.Run(() => (IAnimationSource)new Lux.Library.Animation.AnimationSourceImpl(this, item), cancel).ConfigureAwait(false); }
        catch (Exception e) when (e is not LibraryException and not OperationCanceledException and not OutOfMemoryException)
        { throw new LibraryException("This photo cannot be opened: " + Plain(e), e); }
    }

    public LumenSessionInfo? GetLumenSession(string captureId)
    {
        foreach (var l in GetLocations(captureId).Where(l => l.Exists))
        {
            string p = Path.Combine(Path.GetDirectoryName(l.Path)!, Path.GetFileNameWithoutExtension(l.Path) + ".lris");
            if (!File.Exists(p)) continue;
            var fi = new FileInfo(p);
            bool edits = false, imported = false;
            try { edits = LumenEditImport.HasDepthEdits(p); } catch (Exception e) when (e is IOException or InvalidDataException or Google.Protobuf.InvalidProtocolBufferException) { }
            string? ePath = ImageContext.ForKnownCapture(l.Path, captureId, Hashes).ExistingEditsPath;
            if (ePath is not null)
            {
                try
                {
                    var sha = Lux.Store.PlaneCodec.Sha(File.ReadAllBytes(p));
                    using var s = DepthEditStore.Open(ePath, captureId, readOnly: true);
                    imported = s.Variants().Where(v => v.Kind == DepthEditStore.KindLumenImport).Any(v => s.ReadRenderSettings(v.Id)?.SourceSha is { } ss && ss.AsSpan().SequenceEqual(sha));
                }
                catch (Exception e) when (e is DepthEditStoreException or SqliteException or IOException) { }
            }
            var state = (LumenSessionState)Db.Read(c => c.Long(null, "SELECT lumen_class FROM capture WHERE id=$i", ("$i", captureId)));
            return new LumenSessionInfo(p, fi.Length, fi.LastWriteTimeUtc, edits, imported) { State = state, HasSettingsChanges = state == LumenSessionState.SettingsChanged || state == LumenSessionState.DepthEdited && !edits };
        }
        return null;
    }

    public IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds) => ImportLumenEdits(captureIds, false);

    public IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds, bool newerToneStyle) => EnqueueEach(captureIds, id =>
        Db.Read(c => c.Long(null, "SELECT has_lris FROM capture WHERE id=$i", ("$i", id))) == 1 ? new LumenImportJobRequest(id) { NewerToneStyle = newerToneStyle } : null);

    internal static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
