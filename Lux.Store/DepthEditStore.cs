using System.Globalization;
using Microsoft.Data.Sqlite;
using Lux.Engine.Pipeline;

namespace Lux.Store;

/// <summary>Any failure of the depth-edit store that is about the data rather than a bug.</summary>
public class DepthEditStoreException : Exception { public DepthEditStoreException(string m) : base(m) { } }
/// <summary>The database belongs to a different capture (its recorded content hash is not the hash of the file it was opened for).</summary>
public sealed class CaptureMismatchException : DepthEditStoreException { public CaptureMismatchException(string m) : base(m) { } }
/// <summary>A stored blob or log entry does not match its checksum, or cannot be unpacked.</summary>
public sealed class StoreChecksumException : DepthEditStoreException { public StoreChecksumException(string m) : base(m) { } }
/// <summary>The database was written by a newer version of the store than this one understands.</summary>
public sealed class UnsupportedSchemaException : DepthEditStoreException { public UnsupportedSchemaException(string m) : base(m) { } }
/// <summary>The stored state does not fit the editor it is to be loaded into (another geometry, or another base depth).</summary>
public sealed class StoreGeometryException : DepthEditStoreException { public StoreGeometryException(string m) : base(m) { } }

/// <summary>History of a variant: whether replaying its log has been shown to give its snapshot.</summary>
public enum HistoryState { Unverified = 0, Verified = 1, Unreliable = 2 }

/// <summary>The geometry a variant's tiles are stored in: the edited level (the export window at the editor's level), its origin on
/// the depth cache, the cache size and the tile size.</summary>
public readonly record struct EditGeometry(int LevelW, int LevelH, int OriginX, int OriginY, int CacheW, int CacheH, int TileW, int TileH);

public sealed class VariantInfo
{
    public long Id; public string Name = ""; public string Kind = ""; public string CreatedUtc = ""; public string EngineVersion = "";
    public EditGeometry Geometry; public int DepthW, DepthH; public byte[] DepthSha = Array.Empty<byte>();
    public long Cursor, Base; public bool LayerAllocated; public List<RectI> LayerRects = new();
    public HistoryState History; public string? HistoryNote;
}

/// <summary>One edited tile of a snapshot, unpacked. <see cref="Version"/> is −1 when the tile has no version at or below the cursor
/// (it was edited and undone): then only <see cref="Cache"/> — and possibly the layer planes — are present.</summary>
public sealed class TileRecord
{
    public int Tx, Ty, W, H; public long Version = -1;
    /// <summary>The depth-cache pixels of the tile's rect — what a render reads.</summary>
    public float[] Cache = Array.Empty<float>();
    /// <summary>The tile's present version in the editor's tile store: depth (empty for a layers-only tile), background depth, layer mask.</summary>
    public float[]? Depth, Background; public byte[]? Mask;
    /// <summary>The edit layer's pixels of the tile's rect (null = all zero).</summary>
    public float[]? LayerBackground; public byte[]? LayerMask;

    byte[]?[]? _shas;
    /// <summary>SHA-256 of each plane's raw bytes, in the order cache, depth, background, mask, layer background, layer mask (null
    /// where the plane is absent). Computed once.</summary>
    public byte[]?[] Shas() => _shas ??= new[]
    {
        PlaneCodec.Sha(Cache), Depth == null ? null : PlaneCodec.Sha(Depth), Background == null ? null : PlaneCodec.Sha(Background), Mask == null ? null : PlaneCodec.Sha(Mask),
        LayerBackground == null ? null : PlaneCodec.Sha(LayerBackground), LayerMask == null ? null : PlaneCodec.Sha(LayerMask),
    };
}

/// <summary>The checksums of one stored tile (SHA-256 of each plane's raw bytes; null where the plane is absent) and blob sizes.</summary>
public sealed class TileChecksums
{
    public int Tx, Ty, W, H; public long Version;
    public byte[]? Cache, Depth, Background, Mask, LayerBackground, LayerMask;
    public long BlobBytes; public bool DepthPresent;
}

/// <summary>
/// The depth-edit database of one capture (<c>LuxImageContext/edits/&lt;content hash&gt;.luxedits</c>, SQLite).
/// Schema, version 2 (version 2 adds <c>render_settings</c>):
/// <code>
///   meta(key, value)                 schema_version, capture_sha256, capture_bytes, created_utc, active_variant
///   variant(id, name, kind, …)       one row per variant: geometry, pinned-depth fingerprint, cursor, edit-layer rects, history state
///   pinned_depth(variant_id, tx, ty) the depth the variant's edits were made on, in 512×512 tiles
///   tile(variant_id, tx, ty)         the result snapshot: one row per tile an edit ever touched
///   log(variant_id, seq)             the operation log
///   blob(variant_id, name)           verbatim imported data (a Lumen session's DepthEditor record, its Stereo record's message)
///   render_settings(variant_id)      the variant's per-photo render settings (f-number, focus, tone, transform, rating), checksummed
/// </code>
/// </summary>
public sealed class DepthEditStore : IDisposable
{
    public const int SchemaVersion = 2;
    public const int ApplicationId = 0x4C584445;   // "LXDE"
    public const string KindLux = "lux", KindLumenImport = "lumen-import";
    public const int PinnedTile = 512;

    readonly SqliteConnection _db;
    public string Path { get; }
    public string CaptureHash { get; private set; } = "";
    public long CaptureBytes { get; private set; }

    /// <summary>Opened read-only: nothing is ever written (not even the history flag).</summary>
    public bool ReadOnly { get; }

    DepthEditStore(string path, SqliteOpenMode mode)
    {
        Path = path; ReadOnly = mode == SqliteOpenMode.ReadOnly;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        _db = new SqliteConnection(cs); _db.Open();
        if (mode != SqliteOpenMode.ReadOnly) Exec("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
    }

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------------------------------------ plumbing

    SqliteCommand Cmd(string sql, params (string, object?)[] p)
    {
        var c = _db.CreateCommand(); c.CommandText = sql; c.Transaction = _tx;
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return c;
    }
    void Exec(string sql, params (string, object?)[] p) { using var c = Cmd(sql, p); c.ExecuteNonQuery(); }
    object? Scalar(string sql, params (string, object?)[] p) { using var c = Cmd(sql, p); var v = c.ExecuteScalar(); return v is DBNull ? null : v; }

    SqliteTransaction? _tx;
    /// <summary>Run <paramref name="body"/> in one transaction: everything it writes is committed together or not at all.</summary>
    public void InTransaction(Action body)
    {
        if (_tx != null) { body(); return; }
        using var tx = _db.BeginTransaction(); _tx = tx;
        try { body(); tx.Commit(); }
        catch { try { tx.Rollback(); } catch { } throw; }
        finally { _tx = null; }
    }

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    /// <summary>The engine build recorded with a variant.</summary>
    public static string EngineVersion =>
        typeof(Lux.Engine.Pipeline.Dof.Edit.DepthEditor).Assembly.GetName().Version?.ToString() ?? "0";

    // ------------------------------------------------------------------------------------------------ create / open

    const string SchemaSql = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value) WITHOUT ROWID;
        CREATE TABLE variant(
            id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, created_utc TEXT NOT NULL, engine_version TEXT NOT NULL,
            level_w INTEGER NOT NULL, level_h INTEGER NOT NULL, origin_x INTEGER NOT NULL, origin_y INTEGER NOT NULL,
            cache_w INTEGER NOT NULL, cache_h INTEGER NOT NULL, tile_w INTEGER NOT NULL, tile_h INTEGER NOT NULL,
            depth_w INTEGER NOT NULL, depth_h INTEGER NOT NULL, depth_sha256 BLOB NOT NULL,
            cursor INTEGER NOT NULL DEFAULT 0, base INTEGER NOT NULL DEFAULT 0,
            layer_allocated INTEGER NOT NULL DEFAULT 0, layer_rects BLOB,
            history_state INTEGER NOT NULL DEFAULT 0, history_note TEXT);
        CREATE TABLE pinned_depth(
            variant_id INTEGER NOT NULL REFERENCES variant(id), tx INTEGER NOT NULL, ty INTEGER NOT NULL,
            x0 INTEGER NOT NULL, y0 INTEGER NOT NULL, w INTEGER NOT NULL, h INTEGER NOT NULL,
            data BLOB NOT NULL, sha256 BLOB NOT NULL, PRIMARY KEY(variant_id, tx, ty)) WITHOUT ROWID;
        CREATE TABLE tile(
            variant_id INTEGER NOT NULL REFERENCES variant(id), tx INTEGER NOT NULL, ty INTEGER NOT NULL, w INTEGER NOT NULL, h INTEGER NOT NULL,
            version INTEGER,
            cache_depth BLOB NOT NULL, cache_depth_sha256 BLOB NOT NULL,
            tile_depth BLOB, tile_depth_sha256 BLOB,
            tile_background BLOB, tile_background_sha256 BLOB,
            tile_mask BLOB, tile_mask_sha256 BLOB,
            layer_background BLOB, layer_background_sha256 BLOB,
            layer_mask BLOB, layer_mask_sha256 BLOB,
            PRIMARY KEY(variant_id, tx, ty)) WITHOUT ROWID;
        CREATE TABLE log(
            variant_id INTEGER NOT NULL REFERENCES variant(id), seq INTEGER NOT NULL, kind TEXT NOT NULL, params TEXT NOT NULL,
            outcome INTEGER NOT NULL, message TEXT, cursor_after INTEGER NOT NULL, result_sha256 BLOB NOT NULL, utc TEXT NOT NULL, sha256 BLOB NOT NULL,
            PRIMARY KEY(variant_id, seq)) WITHOUT ROWID;
        CREATE TABLE blob(
            variant_id INTEGER NOT NULL REFERENCES variant(id), name TEXT NOT NULL, raw_bytes INTEGER NOT NULL,
            data BLOB NOT NULL, sha256 BLOB NOT NULL, PRIMARY KEY(variant_id, name)) WITHOUT ROWID;
        """ + RenderSettingsSql;

    /// <summary>Schema version 2: the per-photo render settings of a variant (an imported Lumen session's f-number, focus, tone and colour
    /// settings, transform and rating). One row per variant that has them; <c>json</c> holds every float as its bit pattern
    /// (<see cref="StoredSettings"/>), <c>sha256</c> = SHA-256 of <c>json</c>.</summary>
    const string RenderSettingsSql = """
        CREATE TABLE render_settings(
            variant_id INTEGER PRIMARY KEY REFERENCES variant(id), source TEXT NOT NULL, source_sha256 BLOB,
            json TEXT NOT NULL, utc TEXT NOT NULL, sha256 BLOB NOT NULL);
        """;

    /// <summary>Migrations: entry i takes a database from schema version i + 1 to i + 2. 1 → 2 adds <c>render_settings</c>.</summary>
    static readonly Action<DepthEditStore>[] Migrations = { s => s.Exec(RenderSettingsSql) };

    /// <summary>The schema version of the file as found. A database opened read-only is never migrated: an older one is read as it is
    /// (a version-1 database simply has no render settings).</summary>
    public int SchemaOnDisk { get; private set; } = SchemaVersion;

    /// <summary>Create the database of a capture (the folder must exist).</summary>
    public static DepthEditStore Create(string path, string captureHashHex, long captureBytes)
    {
        if (File.Exists(path)) throw new DepthEditStoreException($"the edit database already exists: {path}");
        var s = new DepthEditStore(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            s.InTransaction(() =>
            {
                s.Exec($"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};");
                s.Exec(SchemaSql);
                s.SetMeta("schema_version", (long)SchemaVersion); s.SetMeta("capture_sha256", captureHashHex); s.SetMeta("capture_bytes", captureBytes);
                s.SetMeta("created_utc", Now());
            });
            s.CaptureHash = captureHashHex; s.CaptureBytes = captureBytes;
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    /// <summary>
    /// Open a database. Checks, in this order: that it is an edit database at all, its schema version (older: migrated in place when
    /// opened for writing; newer: <see cref="UnsupportedSchemaException"/>) and — when <paramref name="expectedCaptureHash"/> is given —
    /// that it was written for that capture (<see cref="CaptureMismatchException"/>).
    /// </summary>
    public static DepthEditStore Open(string path, string? expectedCaptureHash, bool readOnly = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("no edit database", path);
        var s = new DepthEditStore(path, readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite);
        try
        {
            object? ver;
            try { ver = s.Scalar("SELECT value FROM meta WHERE key='schema_version'"); }
            catch (SqliteException e) { throw new DepthEditStoreException($"{System.IO.Path.GetFileName(path)} is not a depth-edit database ({e.Message})"); }
            int v = ver == null ? 0 : Convert.ToInt32(ver, CultureInfo.InvariantCulture);
            if (v > SchemaVersion)
                throw new UnsupportedSchemaException($"the edit database has schema version {v}; this build reads up to version {SchemaVersion} — it was written by a newer Lux");
            if (v < 1) throw new DepthEditStoreException("the edit database has no valid schema version");
            s.SchemaOnDisk = v;
            if (v < SchemaVersion && !readOnly)
            {
                s.SchemaOnDisk = SchemaVersion;
                s.InTransaction(() =>
                {
                    for (int i = v; i < SchemaVersion; i++) Migrations[i - 1](s);
                    s.SetMeta("schema_version", (long)SchemaVersion); s.Exec($"PRAGMA user_version={SchemaVersion};");
                });
            }
            s.CaptureHash = (string?)s.Scalar("SELECT value FROM meta WHERE key='capture_sha256'") ?? "";
            s.CaptureBytes = Convert.ToInt64(s.Scalar("SELECT value FROM meta WHERE key='capture_bytes'") ?? 0L, CultureInfo.InvariantCulture);
            if (expectedCaptureHash != null && !string.Equals(s.CaptureHash, expectedCaptureHash, StringComparison.OrdinalIgnoreCase))
                throw new CaptureMismatchException($"the edit database belongs to another capture (content hash {Short(s.CaptureHash)}…, this file is {Short(expectedCaptureHash)}…)");
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    static string Short(string h) => h.Length > 12 ? h[..12] : h;

    /// <summary>The capture's database: opened when it exists, null otherwise. Never creates anything.</summary>
    public static DepthEditStore? OpenFor(ImageContext ctx, bool readOnly = false)
        => ctx.HasEdits ? Open(ctx.EditsDatabasePath, ctx.ContentHash, readOnly) : null;

    /// <summary>The capture's database, created (with the <c>LuxImageContext</c> folders) when it does not exist yet.</summary>
    public static DepthEditStore OpenOrCreate(ImageContext ctx)
    {
        if (ctx.HasEdits) return Open(ctx.EditsDatabasePath, ctx.ContentHash);
        ctx.EnsureFolders();
        return Create(ctx.EditsDatabasePath, ctx.ContentHash, ctx.CaptureBytes);
    }

    void SetMeta(string key, object? value) => Exec("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    // ------------------------------------------------------------------------------------------------ capture rating

    /// <summary>The capture's rating (0–5) as the app set it (meta key <c>rating</c>, capture-wide, independent of the variants); null when
    /// none was ever set here. A rating that came with an imported Lumen session lives in that variant's render settings
    /// (<see cref="StoredSettings.Rating"/>); this one, when present, takes precedence. No schema change: <c>meta</c> is key/value.</summary>
    public int? CaptureRating
    {
        get
        {
            var v = Scalar("SELECT value FROM meta WHERE key='rating'");
            return v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Set (or with null, clear) the capture rating.</summary>
    public void SetCaptureRating(int? rating)
    {
        if (ReadOnly) throw new DepthEditStoreException("the edit database is opened read-only");
        if (rating is int r && (r < 0 || r > 5)) throw new ArgumentOutOfRangeException(nameof(rating));
        InTransaction(() =>
        {
            if (rating is null) Exec("DELETE FROM meta WHERE key IN ('rating', 'rating_utc')");
            else { SetMeta("rating", (long)rating.Value); SetMeta("rating_utc", Now()); }
        });
    }

    // ------------------------------------------------------------------------------------------------ variants

    /// <summary>The variant renders use; null = none (the capture renders from its clean, unedited depth).</summary>
    public string? ActiveVariant
    {
        get => (string?)Scalar("SELECT value FROM meta WHERE key='active_variant'");
        set
        {
            if (value != null && Find(value) == null) throw new DepthEditStoreException($"there is no variant '{value}'");
            SetMeta("active_variant", value);
        }
    }

    public List<VariantInfo> Variants()
    {
        var o = new List<VariantInfo>();
        using var c = Cmd("SELECT id, name, kind, created_utc, engine_version, level_w, level_h, origin_x, origin_y, cache_w, cache_h, tile_w, tile_h, depth_w, depth_h, depth_sha256, cursor, base, layer_allocated, layer_rects, history_state, history_note FROM variant ORDER BY id");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var v = new VariantInfo
            {
                Id = r.GetInt64(0), Name = r.GetString(1), Kind = r.GetString(2), CreatedUtc = r.GetString(3), EngineVersion = r.GetString(4),
                Geometry = new EditGeometry(r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12)),
                DepthW = r.GetInt32(13), DepthH = r.GetInt32(14), DepthSha = (byte[])r[15], Cursor = r.GetInt64(16), Base = r.GetInt64(17),
                LayerAllocated = r.GetInt64(18) != 0, History = (HistoryState)r.GetInt32(20), HistoryNote = r.IsDBNull(21) ? null : r.GetString(21),
            };
            if (!r.IsDBNull(19))
            {
                var b = (byte[])r[19];
                for (int i = 0; i + 16 <= b.Length; i += 16)
                    v.LayerRects.Add(new RectI(BitConverter.ToInt32(b, i), BitConverter.ToInt32(b, i + 4), BitConverter.ToInt32(b, i + 8), BitConverter.ToInt32(b, i + 12)));
            }
            o.Add(v);
        }
        return o;
    }

    public VariantInfo? Find(string name) => Variants().FirstOrDefault(v => v.Name == name);

    /// <summary>A name not taken yet: <paramref name="wanted"/>, else wanted-2, wanted-3, …</summary>
    public string FreeName(string wanted)
    {
        var names = Variants().Select(v => v.Name).ToHashSet();
        if (!names.Contains(wanted)) return wanted;
        for (int i = 2; ; i++) if (!names.Contains($"{wanted}-{i}")) return $"{wanted}-{i}";
    }

    /// <summary>Add a variant with its pinned base depth (stored as 512×512 tiles, each with its checksum; the whole image's SHA-256
    /// over "w, h, floats" is the fingerprint). Existing variants are not touched.</summary>
    public VariantInfo CreateVariant(string name, string kind, EditGeometry g, ReadOnlySpan<float> depth, int depthW, int depthH)
    {
        if (depth.Length != depthW * depthH) throw new ArgumentException("depth size");
        if (Find(name) != null) throw new DepthEditStoreException($"a variant '{name}' exists already");
        var fp = DepthFingerprint(depth, depthW, depthH);
        var tiles = new List<(int tx, int ty, int x0, int y0, int w, int h, byte[] blob, byte[] sha)>();
        for (int ty = 0, y0 = 0; y0 < depthH; ty++, y0 += PinnedTile)
            for (int tx = 0, x0 = 0; x0 < depthW; tx++, x0 += PinnedTile)
            {
                int w = Math.Min(PinnedTile, depthW - x0), h = Math.Min(PinnedTile, depthH - y0); var t = new float[w * h];
                for (int y = 0; y < h; y++) depth.Slice((y0 + y) * depthW + x0, w).CopyTo(t.AsSpan(y * w));
                tiles.Add((tx, ty, x0, y0, w, h, PlaneCodec.EncodeFloats(t), PlaneCodec.Sha(t)));
            }
        long id = 0;
        InTransaction(() =>
        {
            Exec("""
                INSERT INTO variant(name, kind, created_utc, engine_version, level_w, level_h, origin_x, origin_y, cache_w, cache_h, tile_w, tile_h, depth_w, depth_h, depth_sha256)
                VALUES($n, $k, $c, $e, $lw, $lh, $ox, $oy, $cw, $ch, $tw, $th, $dw, $dh, $fp)
                """, ("$n", name), ("$k", kind), ("$c", Now()), ("$e", EngineVersion), ("$lw", g.LevelW), ("$lh", g.LevelH), ("$ox", g.OriginX), ("$oy", g.OriginY),
                ("$cw", g.CacheW), ("$ch", g.CacheH), ("$tw", g.TileW), ("$th", g.TileH), ("$dw", depthW), ("$dh", depthH), ("$fp", fp));
            id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            foreach (var t in tiles)
                Exec("INSERT INTO pinned_depth(variant_id, tx, ty, x0, y0, w, h, data, sha256) VALUES($v, $tx, $ty, $x, $y, $w, $h, $d, $s)",
                    ("$v", id), ("$tx", t.tx), ("$ty", t.ty), ("$x", t.x0), ("$y", t.y0), ("$w", t.w), ("$h", t.h), ("$d", t.blob), ("$s", t.sha));
        });
        return Variants().First(v => v.Id == id);
    }

    /// <summary>The fingerprint of a base depth: SHA-256 of (int32 w, int32 h, the floats' bytes).</summary>
    public static byte[] DepthFingerprint(ReadOnlySpan<float> depth, int w, int h)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> hdr = stackalloc byte[8]; BitConverter.TryWriteBytes(hdr, w); BitConverter.TryWriteBytes(hdr[4..], h);
        sha.AppendData(hdr); sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(depth));
        return sha.GetHashAndReset();
    }

    /// <summary>The variant's pinned base depth, every tile checked against its checksum and the whole against the fingerprint.</summary>
    public (float[] Depth, int W, int H) ReadPinnedDepth(VariantInfo v)
    {
        var d = new float[v.DepthW * v.DepthH]; long covered = 0;
        using (var c = Cmd("SELECT tx, ty, x0, y0, w, h, data, sha256 FROM pinned_depth WHERE variant_id=$v", ("$v", v.Id)))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                int x0 = r.GetInt32(2), y0 = r.GetInt32(3), w = r.GetInt32(4), h = r.GetInt32(5);
                var t = Unpack(() => PlaneCodec.DecodeFloats((byte[])r[6], w * h), (byte[])r[7], $"pinned depth tile ({r.GetInt32(0)}, {r.GetInt32(1)}) of variant '{v.Name}'");
                if (x0 < 0 || y0 < 0 || x0 + w > v.DepthW || y0 + h > v.DepthH) throw new StoreChecksumException($"pinned depth tile of variant '{v.Name}' lies outside the image");
                for (int y = 0; y < h; y++) Array.Copy(t, y * w, d, (y0 + y) * v.DepthW + x0, w);
                covered += (long)w * h;
            }
        if (covered != d.LongLength) throw new StoreChecksumException($"the pinned depth of variant '{v.Name}' is incomplete ({covered} of {d.LongLength} pixels)");
        if (!DepthFingerprint(d, v.DepthW, v.DepthH).AsSpan().SequenceEqual(v.DepthSha)) throw new StoreChecksumException($"the pinned depth of variant '{v.Name}' does not match its fingerprint");
        return (d, v.DepthW, v.DepthH);
    }

    static T Unpack<T>(Func<T> decode, byte[] sha, string what) where T : class
    {
        T v;
        try { v = decode(); }
        catch (Exception e) when (e is not OutOfMemoryException)   // whatever the decompressor makes of damaged bytes
        { throw new StoreChecksumException($"{what} cannot be unpacked ({e.Message})"); }
        byte[] got = v is float[] f ? PlaneCodec.Sha(f) : PlaneCodec.Sha((byte[])(object)v);
        if (!got.AsSpan().SequenceEqual(sha)) throw new StoreChecksumException($"{what} does not match its checksum");
        return v;
    }

    /// <summary>Cursor, floor and the edit layer's rect list / allocation flag of a variant.</summary>
    public void WriteVariantState(long variantId, long cursor, long @base, bool layerAllocated, IReadOnlyList<RectI> layerRects)
    {
        var b = new byte[layerRects.Count * 16];
        for (int i = 0; i < layerRects.Count; i++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(i * 16), layerRects[i].X0); BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 4), layerRects[i].Y0);
            BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 8), layerRects[i].X1); BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 12), layerRects[i].Y1);
        }
        Exec("UPDATE variant SET cursor=$c, base=$b, layer_allocated=$a, layer_rects=$r WHERE id=$v", ("$c", cursor), ("$b", @base), ("$a", layerAllocated ? 1 : 0), ("$r", b), ("$v", variantId));
    }

    public void SetHistoryState(long variantId, HistoryState state, string? note)
    {
        if (ReadOnly) return;
        Exec("UPDATE variant SET history_state=$s, history_note=$n WHERE id=$v", ("$s", (int)state), ("$n", note), ("$v", variantId));
    }

    // ------------------------------------------------------------------------------------------------ tiles (the result snapshot)

    /// <summary>Write (replace) one tile of a snapshot. <paramref name="baseline"/> = the unedited depth cache's pixels of the tile's
    /// rect, the reference the cache plane may be stored against.</summary>
    public long WriteTile(long variantId, TileRecord t, ReadOnlySpan<float> baseline)
    {
        byte[] cache = PlaneCodec.EncodeFloats(t.Cache, baseline);
        // a layers-only tile (an imported Lumen tile of type "occlusion only") has an empty depth image: stored as the flag byte alone
        byte[]? depth = t.Depth == null ? null : t.Depth.Length == 0 ? new[] { PlaneCodec.Stored } : PlaneCodec.EncodeFloats(t.Depth, t.Depth.Length == t.Cache.Length ? t.Cache : default);
        byte[]? bg = t.Background == null ? null : PlaneCodec.EncodeFloats(t.Background);
        byte[]? mask = t.Mask == null ? null : PlaneCodec.EncodeBytes(t.Mask);
        byte[]? lbg = t.LayerBackground == null ? null : PlaneCodec.EncodeFloats(t.LayerBackground, t.Background ?? default);
        byte[]? lmask = t.LayerMask == null ? null : PlaneCodec.EncodeBytes(t.LayerMask, t.Mask ?? default);
        var sha = t.Shas();
        Exec("""
            INSERT INTO tile(variant_id, tx, ty, w, h, version, cache_depth, cache_depth_sha256, tile_depth, tile_depth_sha256, tile_background, tile_background_sha256,
                             tile_mask, tile_mask_sha256, layer_background, layer_background_sha256, layer_mask, layer_mask_sha256)
            VALUES($v, $tx, $ty, $w, $h, $ver, $c, $cs, $d, $ds, $b, $bs, $m, $ms, $lb, $lbs, $lm, $lms)
            ON CONFLICT(variant_id, tx, ty) DO UPDATE SET w=excluded.w, h=excluded.h, version=excluded.version,
                cache_depth=excluded.cache_depth, cache_depth_sha256=excluded.cache_depth_sha256, tile_depth=excluded.tile_depth, tile_depth_sha256=excluded.tile_depth_sha256,
                tile_background=excluded.tile_background, tile_background_sha256=excluded.tile_background_sha256, tile_mask=excluded.tile_mask, tile_mask_sha256=excluded.tile_mask_sha256,
                layer_background=excluded.layer_background, layer_background_sha256=excluded.layer_background_sha256, layer_mask=excluded.layer_mask, layer_mask_sha256=excluded.layer_mask_sha256
            """,
            ("$v", variantId), ("$tx", t.Tx), ("$ty", t.Ty), ("$w", t.W), ("$h", t.H), ("$ver", t.Version < 0 ? null : t.Version),
            ("$c", cache), ("$cs", sha[0]), ("$d", depth), ("$ds", sha[1]), ("$b", bg), ("$bs", sha[2]), ("$m", mask), ("$ms", sha[3]),
            ("$lb", lbg), ("$lbs", sha[4]), ("$lm", lmask), ("$lms", sha[5]));
        return cache.Length + (depth?.Length ?? 0) + (bg?.Length ?? 0) + (mask?.Length ?? 0) + (lbg?.Length ?? 0) + (lmask?.Length ?? 0);
    }

    /// <summary>Every tile of a variant's snapshot, unpacked and checked against its checksums. <paramref name="baseline"/> returns the
    /// unedited depth cache's pixels of a tile (tx, ty, w, h).</summary>
    public List<TileRecord> ReadTiles(VariantInfo v, Func<int, int, int, int, float[]> baseline)
    {
        var o = new List<TileRecord>();
        using var c = Cmd("""
            SELECT tx, ty, w, h, version, cache_depth, cache_depth_sha256, tile_depth, tile_depth_sha256, tile_background, tile_background_sha256,
                   tile_mask, tile_mask_sha256, layer_background, layer_background_sha256, layer_mask, layer_mask_sha256
            FROM tile WHERE variant_id=$v ORDER BY tx, ty
            """, ("$v", v.Id));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var t = new TileRecord { Tx = r.GetInt32(0), Ty = r.GetInt32(1), W = r.GetInt32(2), H = r.GetInt32(3), Version = r.IsDBNull(4) ? -1 : r.GetInt64(4) };
            int n = t.W * t.H; string at = $"tile ({t.Tx}, {t.Ty}) of variant '{v.Name}'";
            if (n <= 0 || n > 4 * v.Geometry.TileW * v.Geometry.TileH) throw new StoreChecksumException($"{at} has an impossible size");
            byte[]? B(int i) => r.IsDBNull(i) ? null : (byte[])r[i];
            var bl = baseline(t.Tx, t.Ty, t.W, t.H);
            t.Cache = Unpack(() => PlaneCodec.DecodeFloats(B(5)!, n, bl), B(6)!, $"the depth of {at}");
            if (B(7) is { } d)
            {
                // a layers-only tile stores an empty depth image: flag byte + nothing
                bool empty = d.Length == 1 && d[0] == PlaneCodec.Stored;
                t.Depth = Unpack(() => empty ? Array.Empty<float>() : PlaneCodec.DecodeFloats(d, n, t.Cache), B(8)!, $"the tile-store depth of {at}");
            }
            if (B(9) is { } bg) t.Background = Unpack(() => PlaneCodec.DecodeFloats(bg, n), B(10)!, $"the background depth of {at}");
            if (B(11) is { } m) t.Mask = Unpack(() => PlaneCodec.DecodeBytes(m, n), B(12)!, $"the layer mask of {at}");
            if (B(13) is { } lb) t.LayerBackground = Unpack(() => PlaneCodec.DecodeFloats(lb, n, t.Background ?? default), B(14)!, $"the edit-layer background of {at}");
            if (B(15) is { } lm) t.LayerMask = Unpack(() => PlaneCodec.DecodeBytes(lm, n, t.Mask ?? default), B(16)!, $"the edit-layer mask of {at}");
            if (t.Version < 0 && (t.Depth != null || t.Background != null || t.Mask != null)) throw new StoreChecksumException($"{at} has tile-store planes but no version");
            o.Add(t);
        }
        return o;
    }

    /// <summary>The checksums of a variant's stored tiles, without unpacking anything.</summary>
    public List<TileChecksums> ReadTileChecksums(long variantId)
    {
        var o = new List<TileChecksums>();
        using var c = Cmd("""
            SELECT tx, ty, w, h, version, cache_depth_sha256, tile_depth_sha256, tile_background_sha256, tile_mask_sha256, layer_background_sha256, layer_mask_sha256,
                   length(cache_depth) + ifnull(length(tile_depth), 0) + ifnull(length(tile_background), 0) + ifnull(length(tile_mask), 0)
                   + ifnull(length(layer_background), 0) + ifnull(length(layer_mask), 0), tile_depth IS NOT NULL
            FROM tile WHERE variant_id=$v ORDER BY tx, ty
            """, ("$v", variantId));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            byte[]? B(int i) => r.IsDBNull(i) ? null : (byte[])r[i];
            o.Add(new TileChecksums { Tx = r.GetInt32(0), Ty = r.GetInt32(1), W = r.GetInt32(2), H = r.GetInt32(3), Version = r.IsDBNull(4) ? -1 : r.GetInt64(4),
                Cache = B(5), Depth = B(6), Background = B(7), Mask = B(8), LayerBackground = B(9), LayerMask = B(10), BlobBytes = r.GetInt64(11), DepthPresent = r.GetInt64(12) != 0 });
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------------ log

    public long LogCount(long variantId, string kind) => Convert.ToInt64(Scalar("SELECT count(*) FROM log WHERE variant_id=$v AND kind=$k", ("$v", variantId), ("$k", kind)), CultureInfo.InvariantCulture);
    public long LogCount(long variantId) => Convert.ToInt64(Scalar("SELECT count(*) FROM log WHERE variant_id=$v", ("$v", variantId)), CultureInfo.InvariantCulture);

    /// <summary>Append an entry; returns its sequence number (1, 2, … per variant).</summary>
    public long AppendLog(long variantId, EditOp op)
    {
        long seq = Convert.ToInt64(Scalar("SELECT ifnull(max(seq), 0) + 1 FROM log WHERE variant_id=$v", ("$v", variantId)), CultureInfo.InvariantCulture);
        string json = op.ToJson(); op.Seq = seq; op.Utc = Now();
        Exec("INSERT INTO log(variant_id, seq, kind, params, outcome, message, cursor_after, result_sha256, utc, sha256) VALUES($v, $s, $k, $p, $o, $m, $c, $r, $u, $h)",
            ("$v", variantId), ("$s", seq), ("$k", op.Kind), ("$p", json), ("$o", (int)op.Outcome), ("$m", op.Message), ("$c", op.CursorAfter), ("$r", op.Result), ("$u", op.Utc),
            ("$h", EditOp.RowSha(seq, op.Kind, json, (int)op.Outcome, op.Message, op.CursorAfter, op.Result)));
        return seq;
    }

    /// <summary>The whole log of a variant in order; every row is checked against its checksum and the sequence for gaps.</summary>
    public List<EditOp> ReadLog(long variantId)
    {
        var o = new List<EditOp>();
        using var c = Cmd("SELECT seq, kind, params, outcome, message, cursor_after, utc, sha256, result_sha256 FROM log WHERE variant_id=$v ORDER BY seq", ("$v", variantId));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long seq = r.GetInt64(0); string kind = r.GetString(1), json = r.GetString(2); int outcome = r.GetInt32(3); string? msg = r.IsDBNull(4) ? null : r.GetString(4);
            if (seq != o.Count + 1) throw new StoreChecksumException($"the operation log has a gap before entry {seq}");
            long cursorAfter = r.GetInt64(5); var result = (byte[])r[8];
            if (!EditOp.RowSha(seq, kind, json, outcome, msg, cursorAfter, result).AsSpan().SequenceEqual((byte[])r[7])) throw new StoreChecksumException($"log entry {seq} does not match its checksum");
            EditOp op;
            try { op = EditOp.FromJson(kind, json); }
            catch (System.Text.Json.JsonException e) { throw new StoreChecksumException($"log entry {seq} cannot be read ({e.Message})"); }
            op.Seq = seq; op.Outcome = (EditOutcome)outcome; op.Message = msg; op.CursorAfter = cursorAfter; op.Result = result; op.Utc = r.GetString(6);
            o.Add(op);
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------------ verbatim blobs

    public void WriteBlob(long variantId, string name, ReadOnlySpan<byte> raw)
        => Exec("INSERT INTO blob(variant_id, name, raw_bytes, data, sha256) VALUES($v, $n, $r, $d, $s)",
            ("$v", variantId), ("$n", name), ("$r", raw.Length), ("$d", PlaneCodec.EncodeBytes(raw)), ("$s", PlaneCodec.Sha(raw)));

    public byte[]? ReadBlob(long variantId, string name)
    {
        using var c = Cmd("SELECT raw_bytes, data, sha256 FROM blob WHERE variant_id=$v AND name=$n", ("$v", variantId), ("$n", name));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        int n = r.GetInt32(0); var data = (byte[])r[1]; var sha = (byte[])r[2];
        return Unpack(() => PlaneCodec.DecodeBytes(data, n), sha, $"the stored '{name}' data");
    }

    public byte[]? BlobSha(long variantId, string name)
        => (byte[]?)Scalar("SELECT sha256 FROM blob WHERE variant_id=$v AND name=$n", ("$v", variantId), ("$n", name));

    // ------------------------------------------------------------------------------------------------ render settings (schema 2)

    /// <summary>Store (replace) the render settings of a variant.</summary>
    public void WriteRenderSettings(long variantId, StoredSettings rs)
    {
        if (SchemaOnDisk < 2) throw new UnsupportedSchemaException("the edit database is opened read-only at schema version 1; it has no render settings");
        string json = rs.ToJson();
        Exec("""
            INSERT INTO render_settings(variant_id, source, source_sha256, json, utc, sha256) VALUES($v, $s, $ss, $j, $u, $h)
            ON CONFLICT(variant_id) DO UPDATE SET source=excluded.source, source_sha256=excluded.source_sha256, json=excluded.json, utc=excluded.utc, sha256=excluded.sha256
            """, ("$v", variantId), ("$s", rs.Source), ("$ss", rs.SourceSha), ("$j", json), ("$u", Now()), ("$h", PlaneCodec.Sha(System.Text.Encoding.UTF8.GetBytes(json))));
    }

    /// <summary>The render settings of a variant, checked against their checksum; null when it has none (or the database predates them).</summary>
    public StoredSettings? ReadRenderSettings(long variantId)
    {
        if (SchemaOnDisk < 2) return null;
        using var c = Cmd("SELECT source, source_sha256, json, sha256 FROM render_settings WHERE variant_id=$v", ("$v", variantId));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        string json = r.GetString(2);
        if (!PlaneCodec.Sha(System.Text.Encoding.UTF8.GetBytes(json)).AsSpan().SequenceEqual((byte[])r[3])) throw new StoreChecksumException($"the render settings of variant {variantId} do not match their checksum");
        try { return StoredSettings.FromJson(r.GetString(0), r.IsDBNull(1) ? null : (byte[])r[1], json); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidDataException or KeyNotFoundException) { throw new StoreChecksumException($"the render settings of variant {variantId} cannot be read ({e.Message})"); }
    }

    // ------------------------------------------------------------------------------------------------ the capture's Lux recipe (no schema change)

    /// <summary>The capture's recipe of non-depth edits made in Lux (<see cref="CaptureRecipe"/>: tone and colour, crop / rotation, depth-effect
    /// f-number and focus), capture-wide like <see cref="CaptureRating"/> — meta keys <c>lux_recipe</c> (JSON), <c>lux_recipe_sha256</c> (hex
    /// SHA-256 of the JSON, checked on every read) and <c>lux_recipe_utc</c>. Null when none was ever saved. Earlier Lux versions ignore it.</summary>
    public CaptureRecipe? ReadLuxRecipe()
    {
        if (Scalar("SELECT value FROM meta WHERE key='lux_recipe'") is not string json) return null;
        string want = (Scalar("SELECT value FROM meta WHERE key='lux_recipe_sha256'") as string) ?? "";
        string have = Convert.ToHexString(PlaneCodec.Sha(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        if (!string.Equals(want, have, StringComparison.OrdinalIgnoreCase)) throw new StoreChecksumException("the saved edits of this photo do not match their checksum");
        try { return CaptureRecipe.FromJson(json); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidDataException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        { throw new StoreChecksumException($"the saved edits of this photo cannot be read ({e.Message})"); }
    }

    /// <summary>Store (or with null, remove) the capture's Lux recipe, in one transaction.</summary>
    public void WriteLuxRecipe(CaptureRecipe? recipe)
    {
        if (ReadOnly) throw new DepthEditStoreException("the edit database is opened read-only");
        InTransaction(() =>
        {
            if (recipe is null) { Exec("DELETE FROM meta WHERE key IN ('lux_recipe', 'lux_recipe_sha256', 'lux_recipe_utc')"); return; }
            string json = recipe.ToJson();
            SetMeta("lux_recipe", json);
            SetMeta("lux_recipe_sha256", Convert.ToHexString(PlaneCodec.Sha(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
            SetMeta("lux_recipe_utc", Now());
        });
    }

    // ------------------------------------------------------------------------------------------------ sizes, validation

    public sealed record VariantSizes(long PinnedBlobBytes, long PinnedRawBytes, int Tiles, int VersionedTiles, int LayeredTiles, long TileBlobBytes, long TileRawBytes, long LogEntries, long LogBytes, long ImportBytes);

    public VariantSizes Sizes(VariantInfo v)
    {
        long L(string sql) => Convert.ToInt64(Scalar(sql, ("$v", v.Id)) ?? 0L, CultureInfo.InvariantCulture);
        var t = ReadTileChecksums(v.Id); long raw = 0;
        foreach (var x in t)
        {
            long n = (long)x.W * x.H;
            raw += n * 4 + (x.Depth != null ? n * 4 : 0) + (x.Background != null ? n * 4 : 0) + (x.Mask != null ? n : 0) + (x.LayerBackground != null ? n * 4 : 0) + (x.LayerMask != null ? n : 0);
        }
        return new VariantSizes(L("SELECT ifnull(sum(length(data)), 0) FROM pinned_depth WHERE variant_id=$v"), (long)v.DepthW * v.DepthH * 4,
            t.Count, t.Count(x => x.Version >= 0), t.Count(x => x.Mask != null || x.LayerMask != null), t.Sum(x => x.BlobBytes), raw,
            L("SELECT count(*) FROM log WHERE variant_id=$v"), L("SELECT ifnull(sum(length(params)), 0) FROM log WHERE variant_id=$v"),
            L("SELECT ifnull(sum(length(data)), 0) FROM blob WHERE variant_id=$v"));
    }

    /// <summary>
    /// Check everything the database can check about itself: SQLite's own integrity check, every pinned-depth tile and the fingerprint,
    /// every snapshot plane against its checksum (the unedited depth cache the planes may be stored against is rebuilt from the pinned
    /// depth), every log entry and every verbatim blob. Returns the problems found; empty = sound.
    /// </summary>
    public List<string> Validate()
    {
        var bad = new List<string>();
        try
        {
            using var c = Cmd("PRAGMA integrity_check"); using var r = c.ExecuteReader();
            while (r.Read()) { string s = r.GetString(0); if (s != "ok") bad.Add("database: " + s); }
        }
        catch (SqliteException e) { bad.Add("database: " + e.Message); return bad; }
        List<VariantInfo> vs;
        try { vs = Variants(); } catch (Exception e) { bad.Add("variants: " + e.Message); return bad; }
        foreach (var v in vs)
        {
            try
            {
                var (d, w, h) = ReadPinnedDepth(v);
                var cache = new Lux.Engine.Pipeline.Export.DepthImageCache((v.Geometry.CacheW, v.Geometry.CacheH));
                cache.Set(new Lux.Engine.Pipeline.Export.DepthImage(d, w, h, w, new RectI(0, 0, w, h)));
                var img = cache.Current;
                ReadTiles(v, (tx, ty, tw, th) => Window(img, v.Geometry.OriginX + tx * v.Geometry.TileW, v.Geometry.OriginY + ty * v.Geometry.TileH, tw, th));
            }
            catch (DepthEditStoreException e) { bad.Add(e.Message); }
            try { ReadLog(v.Id); } catch (DepthEditStoreException e) { bad.Add($"variant '{v.Name}': {e.Message}"); }
            try
            {
                var names = new List<string>();
                using (var c = Cmd("SELECT name FROM blob WHERE variant_id=$v", ("$v", v.Id))) using (var r = c.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
                foreach (var n in names) ReadBlob(v.Id, n);
            }
            catch (DepthEditStoreException e) { bad.Add($"variant '{v.Name}': {e.Message}"); }
            try { ReadRenderSettings(v.Id); } catch (DepthEditStoreException e) { bad.Add($"variant '{v.Name}': {e.Message}"); }
        }
        return bad;
    }

    /// <summary>A dense copy of a window of a depth image; throws <see cref="StoreChecksumException"/> when it lies outside.</summary>
    public static float[] Window(Lux.Engine.Pipeline.Export.DepthImage img, int x0, int y0, int w, int h)
    {
        if (x0 < 0 || y0 < 0 || w <= 0 || h <= 0 || x0 + w > img.W || y0 + h > img.H) throw new StoreChecksumException("a stored tile lies outside the depth cache");
        var o = new float[w * h];
        for (int y = 0; y < h; y++) Array.Copy(img.Data, img.Offset + (long)(y0 + y) * img.Stride + x0, o, y * w, w);
        return o;
    }
}
