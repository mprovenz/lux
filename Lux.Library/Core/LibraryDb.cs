using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Core;

/// <summary>
/// The library database (SQLite, WAL). One writer connection serialized by a lock; readers open their own (pooled, read-only) connections,
/// so a query never waits for a scan or a job — WAL readers see the last committed state and are never blocked by the writer.
/// <para>Schema version <see cref="SchemaVersion"/> (PRAGMA user_version and meta.schema_version); <see cref="Migrations"/> entry i takes a
/// database from version i + 1 to i + 2. A database written by a newer Lux is refused (opened by nothing), never downgraded.</para>
/// </summary>
internal sealed class LibraryDb : IDisposable
{
    public const int SchemaVersion = 6;
    public const int ApplicationId = 0x4C584C42;   // "LXLB"

    public string Path { get; }
    readonly SqliteConnection _w;
    readonly object _wlock = new();
    readonly string _readCs;
    bool _disposed;

    /// <summary>Times as INTEGER .NET ticks: file times and bookkeeping in UTC; the capture time as the camera wrote it (no zone).</summary>
    const string SchemaSql = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value) WITHOUT ROWID;
        CREATE TABLE setting(key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
        CREATE TABLE folder(
            id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, added_utc INTEGER NOT NULL, last_scan_utc INTEGER,
            cache_level INTEGER, scan_generation INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE capture(
            id TEXT PRIMARY KEY,                       -- SHA-256 of the .lri, lower-case hex
            primary_path TEXT NOT NULL, name TEXT NOT NULL, name_key TEXT NOT NULL,
            location_count INTEGER NOT NULL DEFAULT 0,
            file_size INTEGER NOT NULL, file_mtime INTEGER NOT NULL DEFAULT 0, added_utc INTEGER NOT NULL,
            has_meta INTEGER NOT NULL DEFAULT 0,
            capture_time INTEGER, focal_mm INTEGER NOT NULL DEFAULT 0, stack_frames INTEGER NOT NULL DEFAULT 1,
            ref_module TEXT NOT NULL DEFAULT '', modules TEXT NOT NULL DEFAULT '', module_count INTEGER NOT NULL DEFAULT 0,
            aif_fnumber REAL NOT NULL DEFAULT 0, sensor_w INTEGER NOT NULL DEFAULT 0, sensor_h INTEGER NOT NULL DEFAULT 0,
            telephoto INTEGER NOT NULL DEFAULT 0,
            rating INTEGER NOT NULL DEFAULT 0, rating_source INTEGER NOT NULL DEFAULT 0, rating_utc INTEGER,
            cache_level INTEGER NOT NULL DEFAULT 0, cache_bytes INTEGER NOT NULL DEFAULT 0, cache_stamp TEXT,
            depth_failed INTEGER NOT NULL DEFAULT 0,
            edit_variant TEXT, edit_ops INTEGER NOT NULL DEFAULT 0, edit_source INTEGER NOT NULL DEFAULT 0,
            edit_variants INTEGER NOT NULL DEFAULT 0, edit_settings INTEGER NOT NULL DEFAULT 0, edit_unreliable INTEGER NOT NULL DEFAULT 0,
            edits_stamp TEXT, context_dir TEXT,
            thumb_key TEXT NOT NULL DEFAULT 'clean', thumb_state TEXT,
            has_lris INTEGER NOT NULL DEFAULT 0,
            missing INTEGER NOT NULL DEFAULT 0, unreadable INTEGER NOT NULL DEFAULT 0, error TEXT,
            hidden INTEGER NOT NULL DEFAULT 0,
            search TEXT NOT NULL DEFAULT '',
            context_location INTEGER NOT NULL DEFAULT 0, superseded TEXT,
            meta_version INTEGER NOT NULL DEFAULT 1, iso INTEGER, exposure_s REAL, gain REAL, held_orientation INTEGER NOT NULL DEFAULT 5,
            vp_orientation INTEGER NOT NULL DEFAULT 0, level_sizes TEXT NOT NULL DEFAULT '', saved_fnumber REAL, saved_focus REAL, adjustments TEXT,
            transform_orientation INTEGER NOT NULL DEFAULT 0, transform_crop INTEGER NOT NULL DEFAULT 0, tone_v2 INTEGER NOT NULL DEFAULT 0,
            out_w INTEGER NOT NULL DEFAULT 0, out_h INTEGER NOT NULL DEFAULT 0, lumen_class INTEGER NOT NULL DEFAULT 0, lris_stamp TEXT,
            geometry_decided INTEGER NOT NULL DEFAULT 0,
            blur_px REAL);                             -- predicted motion blur in full-size pixels (NULL = not known)
        CREATE INDEX capture_time_ix ON capture(capture_time, name_key, id);
        CREATE INDEX capture_name_ix ON capture(name_key, id);
        CREATE INDEX capture_rating_ix ON capture(rating, capture_time, id);
        CREATE INDEX capture_focal_ix ON capture(focal_mm, capture_time, id);
        CREATE INDEX capture_size_ix ON capture(file_size, capture_time, id);
        CREATE TABLE location(
            path TEXT PRIMARY KEY,
            capture_id TEXT NOT NULL REFERENCES capture(id) ON DELETE CASCADE,
            folder_id INTEGER NOT NULL REFERENCES folder(id) ON DELETE CASCADE,
            dir TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, fp TEXT NOT NULL DEFAULT '',
            present INTEGER NOT NULL DEFAULT 1, last_seen_utc INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX location_capture_ix ON location(capture_id);
        CREATE INDEX location_folder_ix ON location(folder_id, dir);
        CREATE TABLE hash_cache(path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, fp TEXT NOT NULL, sha256 TEXT NOT NULL,
                                utc INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE album(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, created_utc INTEGER NOT NULL);
        CREATE TABLE album_item(album_id INTEGER NOT NULL REFERENCES album(id) ON DELETE CASCADE,
                                capture_id TEXT NOT NULL REFERENCES capture(id) ON DELETE CASCADE, added_utc INTEGER NOT NULL,
                                PRIMARY KEY(album_id, capture_id)) WITHOUT ROWID;
        CREATE INDEX album_item_capture_ix ON album_item(capture_id);
        CREATE TABLE thumbnail(capture_id TEXT PRIMARY KEY REFERENCES capture(id) ON DELETE CASCADE,
                               format INTEGER NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
                               state_key TEXT NOT NULL, made_utc INTEGER NOT NULL, ms INTEGER NOT NULL DEFAULT 0, data BLOB NOT NULL);
        CREATE TABLE job(
            id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, capture_id TEXT, folder_id INTEGER, title TEXT NOT NULL,
            state INTEGER NOT NULL, progress REAL NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT '', priority INTEGER NOT NULL,
            request TEXT NOT NULL, est_memory INTEGER NOT NULL DEFAULT 0, held INTEGER NOT NULL DEFAULT 0,
            created_utc INTEGER NOT NULL, started_utc INTEGER, finished_utc INTEGER, error TEXT, result_path TEXT,
            result_files TEXT, notes TEXT, items_done INTEGER, items_total INTEGER, retry_of INTEGER);
        CREATE INDEX job_state_ix ON job(state);
        CREATE TABLE preset(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, groups INTEGER NOT NULL, json TEXT NOT NULL,
                            created_utc INTEGER NOT NULL, changed_utc INTEGER NOT NULL);
        """;

    /// <summary>Entry i migrates schema version i + 1 → i + 2 (2 → 3 below). 1 → 2: where a capture's context lives (beside / local store) and a superseded
    /// copy kept after a conflict.</summary>
    static readonly Action<SqliteConnection, SqliteTransaction>[] Migrations =
    {
        (c, tx) => c.Exec(tx, "ALTER TABLE capture ADD COLUMN context_location INTEGER NOT NULL DEFAULT 0; ALTER TABLE capture ADD COLUMN superseded TEXT; UPDATE capture SET edits_stamp=NULL, cache_stamp=NULL;"),
        // 2 → 3: exposure, orientation, level sizes, the saved settings summary, the Lumen session classification; existing captures
        // have their headers read again at the next scan (meta_version 1 < 2) and their contexts re-mirrored
        (c, tx) => c.Exec(tx, """
            ALTER TABLE capture ADD COLUMN meta_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE capture ADD COLUMN iso INTEGER;
            ALTER TABLE capture ADD COLUMN exposure_s REAL;
            ALTER TABLE capture ADD COLUMN gain REAL;
            ALTER TABLE capture ADD COLUMN held_orientation INTEGER NOT NULL DEFAULT 5;
            ALTER TABLE capture ADD COLUMN vp_orientation INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN level_sizes TEXT NOT NULL DEFAULT '';
            ALTER TABLE capture ADD COLUMN saved_fnumber REAL;
            ALTER TABLE capture ADD COLUMN saved_focus REAL;
            ALTER TABLE capture ADD COLUMN adjustments TEXT;
            ALTER TABLE capture ADD COLUMN transform_orientation INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN transform_crop INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN tone_v2 INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN out_w INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN out_h INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN lumen_class INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE capture ADD COLUMN lris_stamp TEXT;
            UPDATE capture SET edits_stamp=NULL, cache_stamp=NULL;
            """),
        // 3 → 4: the Develop page's presets; the mirror is read again (it now includes the capture's Lux recipe)
        (c, tx) => c.Exec(tx, """
            CREATE TABLE preset(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, groups INTEGER NOT NULL, json TEXT NOT NULL,
                                created_utc INTEGER NOT NULL, changed_utc INTEGER NOT NULL);
            UPDATE capture SET edits_stamp=NULL;
            """),
        // 4 → 5: the capture's geometry is a decision of its own (the display-only held turn then never applies); mirrors re-read
        (c, tx) => c.Exec(tx, "ALTER TABLE capture ADD COLUMN geometry_decided INTEGER NOT NULL DEFAULT 0; UPDATE capture SET edits_stamp=NULL;"),
        // 5 → 6: the predicted motion blur. The next scan reads the header of each capture again and fills the column.
        (c, tx) => c.Exec(tx, "ALTER TABLE capture ADD COLUMN blur_px REAL;"),
    };

    public LibraryDb(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        bool fresh = !File.Exists(Path) || new FileInfo(Path).Length == 0;
        _w = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30 }.ToString());
        _w.Open();
        _w.Exec(null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");
        _readCs = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = true, DefaultTimeout = 30 }.ToString();
        try { Migrate(fresh); }
        catch { _w.Dispose(); throw; }
    }

    void Migrate(bool fresh)
    {
        lock (_wlock)
        {
            long appId = Convert.ToInt64(_w.Scalar(null, "PRAGMA application_id"), CultureInfo.InvariantCulture);
            long ver = Convert.ToInt64(_w.Scalar(null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (fresh || (appId == 0 && ver == 0 && Convert.ToInt64(_w.Scalar(null, "SELECT count(*) FROM sqlite_master"), CultureInfo.InvariantCulture) == 0))
            {
                using var tx = _w.BeginTransaction();
                _w.Exec(tx, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};");
                _w.Exec(tx, SchemaSql);
                _w.Exec(tx, "INSERT INTO meta(key, value) VALUES('schema_version', $v), ('created_utc', $u)", ("$v", (long)SchemaVersion), ("$u", DateTime.UtcNow.Ticks));
                tx.Commit();
                return;
            }
            if (appId != ApplicationId) throw new LibraryException($"{Path} is not a Lux library database.");
            if (ver > SchemaVersion) throw new LibraryException($"The library database was written by a newer version of Lux (format {ver}; this version reads up to {SchemaVersion}). Update Lux to open it.");
            if (ver < SchemaVersion)
            {
                using var tx = _w.BeginTransaction();
                for (long i = ver; i < SchemaVersion; i++) Migrations[i - 1](_w, tx);
                _w.Exec(tx, $"PRAGMA user_version={SchemaVersion};");
                _w.Exec(tx, "UPDATE meta SET value=$v WHERE key='schema_version'", ("$v", (long)SchemaVersion));
                tx.Commit();
            }
        }
    }

    /// <summary>Run <paramref name="body"/> in one write transaction (serialized with every other write).</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        lock (_wlock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _w.BeginTransaction();
            var r = body(_w, tx);
            tx.Commit();
            return r;
        }
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> body) => Write<int>((c, t) => { body(c, t); return 0; });

    /// <summary>Run <paramref name="body"/> on a reader connection (its own snapshot; never blocked by the writer).</summary>
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var c = new SqliteConnection(_readCs);
        c.Open();
        return body(c);
    }

    /// <summary>Size of the database and its write-ahead log.</summary>
    public long FileBytes => new[] { Path, Path + "-wal", Path + "-shm" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);

    public void Checkpoint()
    {
        lock (_wlock) { if (!_disposed) try { _w.Exec(null, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { } }
    }

    public void Dispose()
    {
        lock (_wlock)
        {
            if (_disposed) return;
            try { _w.Exec(null, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            _w.Dispose();
            _disposed = true;
        }
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>Small command helpers.</summary>
internal static class Sql
{
    public static SqliteCommand Cmd(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(tx, sql, p); return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(tx, sql, p); var v = cmd.ExecuteScalar(); return v is DBNull ? null : v;
    }

    public static long Long(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] p)
        => Convert.ToInt64(c.Scalar(tx, sql, p) ?? 0L, CultureInfo.InvariantCulture);

    public static List<T> Rows<T>(this SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(tx, sql, p); using var r = cmd.ExecuteReader();
        var o = new List<T>(); while (r.Read()) o.Add(map(r)); return o;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? LongN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static DateTime? Utc(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : new DateTime(r.GetInt64(i), DateTimeKind.Utc);
}
