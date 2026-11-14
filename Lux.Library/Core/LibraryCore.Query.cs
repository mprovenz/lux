using System.Text;
using Lux.Engine.Session;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    /// <summary>The columns every capture read selects (one order, mapped by <see cref="ReadItem"/>).</summary>
    internal const string ItemColumns = """
        c.id, c.primary_path, c.name, c.location_count, c.capture_time, c.focal_mm, c.stack_frames, c.ref_module, c.module_count,
        c.aif_fnumber, c.telephoto, c.file_size, c.file_mtime, c.added_utc, c.rating, c.rating_source, c.cache_level, c.cache_bytes,
        c.depth_failed, c.edit_variant, c.edit_ops, c.edit_source, c.edit_variants, c.edit_settings, c.edit_unreliable, c.has_lris,
        c.thumb_state, c.thumb_key, c.missing, c.unreadable, c.error, c.context_location, c.superseded,
        c.iso, c.exposure_s, c.gain, c.held_orientation, c.vp_orientation, c.level_sizes, c.saved_fnumber, c.saved_focus, c.adjustments,
        c.transform_orientation, c.transform_crop, c.tone_v2, c.out_w, c.out_h, c.lumen_class, c.has_meta, c.geometry_decided
        """;

    internal CaptureItem ReadItem(SqliteDataReader r)
    {
        string id = r.GetString(0);
        var level = (CacheLevel)r.GetInt32(16);
        string? thumbState = r.Str(26);
        var active = Queue.ActiveOf(id);
        bool hasMeta = r.GetInt64(48) != 0;
        var held = (PhotoOrientation)r.GetInt32(36);
        var shown = r.GetInt32(42) != 0 ? (PhotoOrientation)r.GetInt32(42) : (PhotoOrientation)r.GetInt32(37);
        var edits = r.GetInt32(21) == 0 && r.GetInt32(22) == 0 && r.GetInt64(23) == 0 ? EditSummary.None   // (a Lux recipe without depth edits: settings only)
                  : new EditSummary(r.Str(19), r.GetInt32(20), (EditSource)r.GetInt32(21), r.GetInt32(22), r.GetInt64(23) != 0, r.GetInt64(24) != 0)
                  {
                      SavedFNumber = r.IsDBNull(39) ? null : (float)r.GetDouble(39), SavedFocusMm = r.IsDBNull(40) ? null : (float)r.GetDouble(40),
                      AdjustmentsSummary = r.Str(41), HasCropOrRotation = r.GetInt64(43) != 0, NewerToneStyle = r.GetInt64(44) != 0,
                  };
        return new CaptureItem
        {
            Id = id, Path = r.GetString(1), Name = r.GetString(2), LocationCount = r.GetInt32(3),
            CaptureTime = r.IsDBNull(4) ? null : new DateTime(r.GetInt64(4), DateTimeKind.Unspecified),
            FocalLengthMm = r.GetInt32(5), StackFrames = r.GetInt32(6), ReferenceModule = r.GetString(7), ModuleCount = r.GetInt32(8),
            AllInFocusFNumber = (float)r.GetDouble(9), IsTelephoto = r.GetInt64(10) != 0, FileSize = r.GetInt64(11),
            FileModifiedUtc = new DateTime(r.GetInt64(12), DateTimeKind.Utc), AddedUtc = new DateTime(r.GetInt64(13), DateTimeKind.Utc),
            Rating = r.GetInt32(14), RatingSource = (RatingSource)r.GetInt32(15), CacheLevel = level, CacheBytes = r.GetInt64(17),
            DepthState = level >= CacheLevel.Geometry ? DepthState.Analysed : r.GetInt64(18) != 0 ? DepthState.Failed : DepthState.NotAnalysed,
            Edits = edits,
            HasLumenSession = r.GetInt64(25) != 0,
            HasThumbnail = thumbState is not null, ThumbnailCurrent = thumbState is not null && thumbState == r.GetString(27),
            Missing = r.GetInt64(28) != 0, Unreadable = r.GetInt64(29) != 0, Error = r.Str(30),
            ContextLocation = (ContextLocation)r.GetInt32(31), SupersededContextPath = r.Str(32),
            ActiveJobId = active?.Id, ActiveJobState = active?.State, ActiveJobProgress = active?.Progress ?? 0,
            Iso = hasMeta && !r.IsDBNull(33) ? r.GetInt32(33) : null, ExposureTimeSeconds = r.IsDBNull(34) ? null : r.GetDouble(34), Gain = r.IsDBNull(35) ? null : (float)r.GetDouble(35),
            HeldOrientation = held, Orientation = shown,
            // the held turn is shown only while the photo has no geometry decision of its own
            GeometryDecided = r.GetInt64(49) != 0,
            DisplayRotation = SettingsImpl.ShowPhotosAsHeld && shown == PhotoOrientation.Normal && r.GetInt64(49) == 0 ? held switch { PhotoOrientation.Rotate90 => 90, PhotoOrientation.Rotate180 => 180, PhotoOrientation.Rotate270 => 270, _ => 0 } : 0,
            LevelSizes = ParseSizes(r.GetString(38)), Width = r.GetInt32(45), Height = r.GetInt32(46), LumenSession = (LumenSessionState)r.GetInt32(47),
            BestQuality = level == CacheLevel.Full ? ViewQuality.FullDetail : level >= CacheLevel.Linear13 ? ViewQuality.Linear13 : ViewQuality.Preview,
            ActiveJob = active?.Kind,
        };
    }

    // ------------------------------------------------------------------------------------------------ query building

    sealed class Where
    {
        public readonly List<string> Conds = new() { "c.hidden=0" };
        public readonly List<(string, object?)> Params = new();
        int _n;
        public string P(object? v) { string n = "$q" + _n++; Params.Add((n, v)); return n; }
        public string Sql => string.Join(" AND ", Conds);
    }

    internal static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    static Where BuildWhere(CaptureQuery q, bool scopeOnly = false)
    {
        var w = new Where();
        if (q.FolderId is long fid) w.Conds.Add($"c.id IN (SELECT l.capture_id FROM location l WHERE l.folder_id={w.P(fid)})");
        if (!string.IsNullOrEmpty(q.PathPrefix))
        {
            string pp = Normalise(q.PathPrefix);
            string pps = pp.EndsWith(System.IO.Path.DirectorySeparatorChar) ? pp : pp + System.IO.Path.DirectorySeparatorChar;
            w.Conds.Add($"c.id IN (SELECT l.capture_id FROM location l WHERE l.dir={w.P(pp)} OR substr(l.dir, 1, {w.P((long)pps.Length)})={w.P(pps)})");
        }
        if (q.AlbumId is long aid) w.Conds.Add($"c.id IN (SELECT ai.capture_id FROM album_item ai WHERE ai.album_id={w.P(aid)})");
        if (scopeOnly) return w;
        if (q.MinRating is int mr && mr > 0) w.Conds.Add($"c.rating >= {w.P((long)mr)}");
        if (q.DepthEdited is bool de) w.Conds.Add(de ? "c.edit_source <> 0" : "c.edit_source = 0");
        if (q.Stacked is bool st) w.Conds.Add(st ? "c.stack_frames > 1" : "c.stack_frames <= 1");
        if (q.Telephoto is bool te) w.Conds.Add(te ? "c.telephoto = 1" : "c.telephoto = 0");
        if (q.Analysed is bool an) w.Conds.Add(an ? "c.cache_level >= 1" : "c.cache_level = 0");
        if (q.HasLumenSession is bool hl) w.Conds.Add(hl ? "c.has_lris = 1" : "c.has_lris = 0");
        if (q.EditedInLumen is bool el) w.Conds.Add(el ? "c.lumen_class >= 2" : "c.lumen_class < 2");
        if (q.Missing is bool mi) w.Conds.Add(mi ? "c.missing = 1" : "c.missing = 0");
        if (q.CacheLevel is CacheLevel cl) w.Conds.Add($"c.cache_level = {w.P((long)cl)}");
        if (!string.IsNullOrWhiteSpace(q.Text))
            foreach (var word in q.Text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = w.P("%" + Escape(word) + "%");
                w.Conds.Add($"(c.search LIKE {p} ESCAPE '\\' OR EXISTS(SELECT 1 FROM album_item ai2 JOIN album a2 ON a2.id=ai2.album_id WHERE ai2.capture_id=c.id AND lower(a2.name) LIKE {p} ESCAPE '\\'))");
            }
        return w;
    }

    static string OrderBy(CaptureQuery q)
    {
        string d = q.Descending ? "DESC" : "ASC";
        string time = $"(c.capture_time IS NULL) ASC, c.capture_time {d}";
        return q.Sort switch
        {
            CaptureSort.Name => $"c.name_key {d}, c.id {d}",
            CaptureSort.Rating => $"c.rating {d}, {time}, c.id {d}",
            CaptureSort.FocalLength => $"c.focal_mm {d}, {time}, c.id {d}",
            CaptureSort.FileSize => $"c.file_size {d}, {time}, c.id {d}",
            _ => $"{time}, c.name_key {d}, c.id {d}",
        };
    }

    // ------------------------------------------------------------------------------------------------ ILibrary reads

    public CapturePage Query(CaptureQuery query)
    {
        long gen = Changes.Generation;
        int limit = Math.Clamp(query.Limit, 1, 10_000), offset = Math.Max(0, query.Offset);
        var w = BuildWhere(query);
        return Db.Read(c =>
        {
            int total = (int)c.Long(null, $"SELECT count(*) FROM capture c WHERE {w.Sql}", w.Params.ToArray());
            var p = w.Params.Concat(new (string, object?)[] { ("$lim", (long)limit), ("$off", (long)offset) }).ToArray();
            var items = c.Rows(null, $"SELECT {ItemColumns} FROM capture c WHERE {w.Sql} ORDER BY {OrderBy(query)} LIMIT $lim OFFSET $off", ReadItem, p);
            return new CapturePage(items, total, offset, gen);
        });
    }

    public Task<CapturePage> QueryAsync(CaptureQuery query, CancellationToken cancel = default) => Task.Run(() => Query(query), cancel);

    public int Count(CaptureQuery query)
    {
        var w = BuildWhere(query);
        return Db.Read(c => (int)c.Long(null, $"SELECT count(*) FROM capture c WHERE {w.Sql}", w.Params.ToArray()));
    }

    public IReadOnlyList<DateGroup> GetDateGroups(CaptureQuery query)
    {
        var w = BuildWhere(query);
        var times = Db.Read(c => c.Rows(null, $"SELECT c.capture_time FROM capture c WHERE {w.Sql} ORDER BY {OrderBy(query)}", r => r.LongN(0), w.Params.ToArray()));
        var o = new List<DateGroup>();
        DateOnly? cur = null; int start = 0, n = 0; bool any = false;
        for (int i = 0; i < times.Count; i++)
        {
            DateOnly? day = times[i] is long t ? DateOnly.FromDateTime(new DateTime(t)) : null;
            if (any && day == cur) { n++; continue; }
            if (any) o.Add(new DateGroup(cur, n, start));
            cur = day; start = i; n = 1; any = true;
        }
        if (any) o.Add(new DateGroup(cur, n, start));
        return o;
    }

    public CaptureItem? Get(string captureId)
        => Db.Read(c => c.Rows(null, $"SELECT {ItemColumns} FROM capture c WHERE c.id=$i", ReadItem, ("$i", captureId)).FirstOrDefault());

    internal CaptureItem? GetAny(string captureId) => Get(captureId);

    public IReadOnlyList<CaptureLocation> GetLocations(string captureId)
        => Db.Read(c => c.Rows(null, "SELECT folder_id, path, present, size, mtime, last_seen_utc FROM location WHERE capture_id=$i ORDER BY present DESC, path",
                               r => new CaptureLocation(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3), new DateTime(r.GetInt64(4), DateTimeKind.Utc), new DateTime(r.GetInt64(5), DateTimeKind.Utc)),
                               ("$i", captureId)));

    public IReadOnlyDictionary<SmartFilter, int> GetSmartFilterCounts(CaptureQuery? scope = null)
    {
        var w = BuildWhere(scope ?? new CaptureQuery(), scopeOnly: true);
        return Db.Read(c =>
        {
            using var cmd = c.Cmd(null, $"""
                SELECT count(*), sum(c.rating >= 1), sum(c.edit_source <> 0), sum(c.stack_frames > 1), sum(c.telephoto = 1), sum(c.cache_level = 0),
                       sum(c.has_lris = 1), sum(c.missing = 1), sum(c.cache_level = 3), sum(c.lumen_class >= 2)
                FROM capture c WHERE {w.Sql}
                """, w.Params.ToArray());
            using var r = cmd.ExecuteReader();
            r.Read();
            int V(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            return (IReadOnlyDictionary<SmartFilter, int>)new Dictionary<SmartFilter, int>
            {
                [SmartFilter.All] = V(0), [SmartFilter.Rated] = V(1), [SmartFilter.DepthEdited] = V(2), [SmartFilter.Stacked] = V(3),
                [SmartFilter.Telephoto] = V(4), [SmartFilter.NotAnalysed] = V(5), [SmartFilter.HasLumenSession] = V(6), [SmartFilter.Missing] = V(7),
                [SmartFilter.FullDetail] = V(8), [SmartFilter.EditedInLumen] = V(9),
            };
        });
    }

    public LibraryStats GetStats() => Db.Read(c =>
    {
        var byLevel = c.Rows(null, "SELECT cache_level, count(*) FROM capture WHERE hidden=0 GROUP BY cache_level", r => ((CacheLevel)r.GetInt32(0), r.GetInt32(1)))
                       .ToDictionary(t => t.Item1, t => t.Item2);
        foreach (CacheLevel l in Enum.GetValues<CacheLevel>()) byLevel.TryAdd(l, 0);
        return new LibraryStats
        {
            Captures = (int)c.Long(null, "SELECT count(*) FROM capture WHERE hidden=0"),
            Missing = (int)c.Long(null, "SELECT count(*) FROM capture WHERE hidden=0 AND missing=1"),
            Unreadable = (int)c.Long(null, "SELECT count(*) FROM capture WHERE hidden=0 AND unreadable=1"),
            Folders = (int)c.Long(null, "SELECT count(*) FROM folder"),
            ByCacheLevel = byLevel,
            CacheBytes = c.Long(null, "SELECT coalesce(sum(cache_bytes), 0) FROM capture"),
            Thumbnails = (int)c.Long(null, "SELECT count(*) FROM thumbnail"),
            ThumbnailBytes = c.Long(null, "SELECT coalesce(sum(length(data)), 0) FROM thumbnail"),
            DatabaseBytes = Db.FileBytes,
        };
    });

    /// <summary>The lower-cased free-text field of a capture.</summary>
    internal static string SearchText(string name, IEnumerable<string> paths, string refModule, string? variant)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append('\n');
        foreach (var p in paths) sb.Append(p).Append('\n');
        sb.Append(refModule).Append('\n').Append(variant ?? "");
        return sb.ToString().ToLowerInvariant();
    }
}
