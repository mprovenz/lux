using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Lux.Engine.Session;

namespace Lux.Store;

/// <summary>
/// The analysis cache of one capture: <c>LuxImageContext/cache/&lt;sha-256 of the .lri&gt;.luxcache</c>, a SQLite database holding the
/// engine's cache blocks (<see cref="IAnalysisCache"/>; what the blocks mean is the engine's business — <c>Lux.Engine.Session.AnalysisCache</c>).
/// <code>
///   meta(key, value)             schema_version, capture_sha256, capture_bytes, engine_cache_version, created_utc
///   tier(name, complete, descriptor, updated_utc)
///   block(tier, key, raw_bytes, element_size, sha256, data)   one row per block; data = codec flag byte + payload
/// </code>
/// Every block is stored losslessly (byte planes by element size, a left-neighbour delta, Brotli) with the SHA-256 of its raw bytes, and
/// checked against it on every read: a block that does not unpack to its checksum is never handed out (<see cref="CacheCorruptException"/>).
/// A cache written for another engine cache version is deleted and started again — never read. Everything here can be deleted at any time;
/// the engine recomputes what is missing.
/// </summary>
public sealed class AnalysisCacheStore : IAnalysisCache
{
    public const int SchemaVersion = 1;
    public const int ApplicationId = 0x4C58434B;   // "LXCK"
    public const string Extension = ".luxcache";

    readonly SqliteConnection _db;
    readonly object _lock = new();
    public string Path { get; }
    public string Location => Path;
    public string CaptureHash { get; }
    public int EngineCacheVersion { get; }

    /// <summary>Brotli quality of new blocks (5: measured as the knee of size against time on half-float tiles).</summary>
    public static int BrotliQuality { get; set; } = 5;

    AnalysisCacheStore(string path, string captureHash, int engineCacheVersion, SqliteConnection db)
    {
        Path = path; CaptureHash = captureHash; EngineCacheVersion = engineCacheVersion; _db = db;
    }

    /// <summary>The cache database path of a capture.</summary>
    public static string PathOf(ImageContext ctx) => System.IO.Path.Combine(ctx.CacheFolder, ctx.ContentHash + Extension);

    /// <summary>Open (or, with <paramref name="create"/>, create) the cache of a capture for an engine cache version. A cache of another
    /// version — or one that cannot be read as a cache at all — is deleted: it is regenerable data. Returns null when there is no cache and
    /// <paramref name="create"/> is false, or the folder cannot be written.</summary>
    public static IAnalysisCache? Open(ImageContext ctx, int engineCacheVersion, bool create)
    {
        if (!create && !ctx.MayHaveContext) return null;   // nothing cached anywhere for it: do not even hash the capture
        // the existing cache (next to the capture, else in the local context store), else where a new one goes (ImageContext.WriteRoot)
        string? found = ctx.ExistingCachePath;
        if (!create && found is null) return null;
        string path = found ?? PathOf(ctx);
        if (File.Exists(path))
        {
            var existing = TryOpenExisting(path, ctx.ContentHash, engineCacheVersion, out string? why);
            if (existing != null) return existing;
            // stale (another engine cache version) or unreadable: discard
            DeleteFiles(path);
            if (!create) return null;
            _ = why;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            if (ctx.Store is { } st && !path.StartsWith(ctx.BesideRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) st.Touched();
            var db = Connect(path, SqliteOpenMode.ReadWriteCreate);
            var s = new AnalysisCacheStore(path, ctx.ContentHash, engineCacheVersion, db);
            s.CreateSchema(ctx.CaptureBytes);
            return s;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SqliteException) { return null; }
    }

    /// <summary>What <see cref="Open"/> found (for `cache` reports): the engine cache version recorded in an existing cache file, or null.</summary>
    public static int? RecordedEngineVersion(ImageContext ctx)
    {
        string path = PathOf(ctx);
        if (!File.Exists(path)) return null;
        try
        {
            using var db = Connect(path, SqliteOpenMode.ReadOnly);
            using var c = db.CreateCommand(); c.CommandText = "SELECT value FROM meta WHERE key='engine_cache_version'";
            return c.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
        }
        catch (SqliteException) { return null; }
    }

    static AnalysisCacheStore? TryOpenExisting(string path, string hash, int engineCacheVersion, out string? why)
    {
        why = null;
        SqliteConnection? db = null;
        try
        {
            db = Connect(path, SqliteOpenMode.ReadWrite);
            string? Meta(string k) { using var c = db.CreateCommand(); c.CommandText = "SELECT value FROM meta WHERE key=$k"; c.Parameters.AddWithValue("$k", k); return c.ExecuteScalar()?.ToString(); }
            if (Meta("schema_version") != SchemaVersion.ToString(CultureInfo.InvariantCulture)) { why = "schema"; db.Dispose(); return null; }
            if (Meta("capture_sha256") != hash) { why = "capture"; db.Dispose(); return null; }
            if (Meta("engine_cache_version") != engineCacheVersion.ToString(CultureInfo.InvariantCulture)) { why = "engine cache version"; db.Dispose(); return null; }
            return new AnalysisCacheStore(path, hash, engineCacheVersion, db);
        }
        catch (SqliteException e) { why = e.Message; db?.Dispose(); return null; }
    }

    static void DeleteFiles(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var db = new SqliteConnection(cs); db.Open();
        if (mode != SqliteOpenMode.ReadOnly)
            // auto_vacuum first: it only takes effect before the database's first page is written, which switching to WAL does
            using (var c = db.CreateCommand()) { c.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; c.ExecuteNonQuery(); }
        return db;
    }

    void CreateSchema(long captureBytes)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            Exec($"PRAGMA application_id={ApplicationId};", tx);
            Exec("""
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS tier(name TEXT PRIMARY KEY, complete INTEGER NOT NULL DEFAULT 0, descriptor TEXT, updated_utc TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS block(
                    id INTEGER PRIMARY KEY, tier TEXT NOT NULL, key TEXT NOT NULL, raw_bytes INTEGER NOT NULL, element_size INTEGER NOT NULL,
                    sha256 BLOB NOT NULL, data BLOB NOT NULL, UNIQUE(tier, key));
                """, tx);
            void M(string k, object v) => Exec("INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)", tx, ("$k", k), ("$v", v));
            M("schema_version", (long)SchemaVersion); M("capture_sha256", CaptureHash); M("capture_bytes", captureBytes);
            M("engine_cache_version", (long)EngineCacheVersion); M("created_utc", Now());
            tx.Commit();
        }
    }

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] p)
    {
        using var c = _db.CreateCommand(); c.CommandText = sql; c.Transaction = tx;
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            _db.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------ blocks

    public byte[]? Read(string tier, string key)
    {
        long raw; int elem; byte[] sha, data;
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT raw_bytes, element_size, sha256, data FROM block WHERE tier=$t AND key=$k";
            c.Parameters.AddWithValue("$t", tier); c.Parameters.AddWithValue("$k", key);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            raw = r.GetInt64(0); elem = r.GetInt32(1); sha = (byte[])r.GetValue(2); data = (byte[])r.GetValue(3);
        }
        byte[] bytes;
        try { bytes = BlockCodec.Decode(data, checked((int)raw), elem); }
        catch (Exception e) when (e is not OutOfMemoryException)   // Brotli reports damaged input as InvalidOperationException
        { throw new CacheCorruptException($"cache block {tier}/{key} cannot be unpacked ({e.Message})"); }
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(sha)) throw new CacheCorruptException($"cache block {tier}/{key} does not match its checksum");
        return bytes;
    }

    public IReadOnlyCollection<string> Keys(string tier)
    {
        var keys = new List<string>();
        lock (_lock)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT key FROM block WHERE tier=$t"; c.Parameters.AddWithValue("$t", tier);
            using var r = c.ExecuteReader();
            while (r.Read()) keys.Add(r.GetString(0));
        }
        return keys;
    }

    public void Write(string tier, IReadOnlyList<(string Key, byte[] Data)> blocks, int elementSize)
    {
        if (blocks.Count == 0) return;
        // pack outside the lock, one block per worker
        var packed = new (string Key, long Raw, byte[] Sha, byte[] Blob)[blocks.Count];
        Parallel.For(0, blocks.Count, i =>
        {
            var (k, d) = blocks[i];
            packed[i] = (k, d.Length, SHA256.HashData(d), BlockCodec.Encode(d, elementSize));
        });
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (k, raw, sha, blob) in packed)
                Exec("INSERT OR REPLACE INTO block(tier, key, raw_bytes, element_size, sha256, data) VALUES($t, $k, $r, $e, $s, $d)", tx,
                     ("$t", tier), ("$k", k), ("$r", raw), ("$e", elementSize), ("$s", sha), ("$d", blob));
            Exec("INSERT INTO tier(name, complete, descriptor, updated_utc) VALUES($t, 0, NULL, $u) ON CONFLICT(name) DO UPDATE SET updated_utc=$u", tx, ("$t", tier), ("$u", Now()));
            tx.Commit();
        }
    }

    public void SetComplete(string tier, string? descriptor)
    {
        lock (_lock)
            Exec("INSERT INTO tier(name, complete, descriptor, updated_utc) VALUES($t, $c, $d, $u) ON CONFLICT(name) DO UPDATE SET complete=$c, descriptor=$d, updated_utc=$u",
                 null, ("$t", tier), ("$c", descriptor == null ? 0 : 1), ("$d", descriptor), ("$u", Now()));
    }

    public string? CompleteDescriptor(string tier)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT complete, descriptor FROM tier WHERE name=$t"; c.Parameters.AddWithValue("$t", tier);
            using var r = c.ExecuteReader();
            if (!r.Read() || r.GetInt64(0) == 0 || r.IsDBNull(1)) return null;
            return r.GetString(1);
        }
    }

    public void Drop(string tier)
    {
        lock (_lock)
        {
            using (var tx = _db.BeginTransaction())
            {
                Exec("DELETE FROM block WHERE tier=$t", tx, ("$t", tier));
                Exec("DELETE FROM tier WHERE name=$t", tx, ("$t", tier));
                tx.Commit();
            }
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            // give the pages back: incremental vacuum when the file was created with it, else a full vacuum (rewrites the file once)
            bool incremental;
            using (var c = _db.CreateCommand()) { c.CommandText = "PRAGMA auto_vacuum"; incremental = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) == 2; }
            Exec(incremental ? "PRAGMA incremental_vacuum;" : "VACUUM;");
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public IReadOnlyList<CacheTierStats> Stats()
    {
        var o = new List<CacheTierStats>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                SELECT b.tier, COUNT(*), SUM(b.raw_bytes), SUM(length(b.data)), COALESCE(t.complete, 0)
                FROM block b LEFT JOIN tier t ON t.name = b.tier GROUP BY b.tier ORDER BY b.tier
                """;
            using var r = c.ExecuteReader();
            while (r.Read()) o.Add(new CacheTierStats(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0));
        }
        return o;
    }

    /// <summary>Bytes the cache occupies on disk (the database and its write-ahead log).</summary>
    public long FileBytes => new[] { Path, Path + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
}

/// <summary>
/// Lossless block packing for the analysis cache: one flag byte, then the payload.
/// <code>
///   bits 0–1  compressor: 0 = stored, 2 = brotli
///   bit 6     left-neighbour delta on the elements (wrapping), stride = byte 1 (1 for a plane, 3 for half RGB)
///   bit 4     elements split into byte planes before compression
/// </code>
/// </summary>
public static class BlockCodec
{
    public const byte Stored = 0, Brotli = 2, CompressorMask = 3, Shuffle = 0x10, Delta = 0x40;

    /// <summary>The delta stride for an element size: half-float tiles are RGB triplets, everything else a plain sequence.</summary>
    static int StrideFor(int elementSize) => elementSize == 2 ? 3 : 1;

    public static byte[] Encode(byte[] raw, int elementSize)
    {
        if (elementSize is not (1 or 2 or 4)) throw new ArgumentException("element size must be 1, 2 or 4");
        if (raw.Length % elementSize != 0) throw new ArgumentException("block length is not a whole number of elements");
        byte flags = 0; byte[] prepared = raw; int stride = StrideFor(elementSize);
        if (elementSize > 1)
        {
            prepared = Transform(raw, elementSize, stride, forward: true);
            flags |= Shuffle | Delta;
        }
        byte[] c;
        using (var ms = new MemoryStream(raw.Length / 2 + 64))
        {
            using (var z = new BrotliStream(ms, new BrotliCompressionOptions { Quality = AnalysisCacheStore.BrotliQuality }, leaveOpen: true)) z.Write(prepared);
            c = ms.ToArray();
        }
        if (c.Length + 2 >= raw.Length) { var s = new byte[raw.Length + 2]; s[0] = Stored; raw.CopyTo(s, 2); return s; }
        var o = new byte[c.Length + 2]; o[0] = (byte)(flags | Brotli); o[1] = (byte)stride; c.CopyTo(o, 2);
        return o;
    }

    public static byte[] Decode(byte[] blob, int rawLength, int elementSize)
    {
        if (blob.Length < 2) throw new InvalidDataException("empty cache block");
        byte flags = blob[0]; int stride = blob[1];
        if ((flags & CompressorMask) == Stored)
        {
            if (blob.Length - 2 != rawLength) throw new InvalidDataException("stored cache block has the wrong length");
            return blob.AsSpan(2).ToArray();
        }
        if ((flags & CompressorMask) != Brotli) throw new InvalidDataException("unknown compressor in a cache block");
        var bytes = new byte[rawLength];
        using (var ms = new MemoryStream(blob, 2, blob.Length - 2, writable: false))
        using (var z = new BrotliStream(ms, CompressionMode.Decompress))
        {
            z.ReadExactly(bytes);
            if (z.ReadByte() != -1) throw new InvalidDataException("cache block is longer than expected");
        }
        if ((flags & (Shuffle | Delta)) == 0) return bytes;
        if ((flags & (Shuffle | Delta)) != (Shuffle | Delta) || stride < 1) throw new InvalidDataException("unsupported cache block transform");
        return Transform(bytes, elementSize, stride, forward: false);
    }

    /// <summary>Forward: delta on the elements (each minus the element <paramref name="stride"/> before it), then byte planes. Inverse:
    /// the reverse. Elements are little-endian unsigned integers of <paramref name="elementSize"/> bytes, wrapping.</summary>
    static byte[] Transform(byte[] src, int elementSize, int stride, bool forward)
    {
        int n = src.Length / elementSize; var o = new byte[src.Length];
        if (forward)
        {
            var d = new uint[n];
            for (int i = 0; i < n; i++) d[i] = Get(src, i, elementSize);
            for (int i = n - 1; i >= stride; i--) d[i] = unchecked(d[i] - d[i - stride]);
            for (int i = 0; i < n; i++) for (int b = 0; b < elementSize; b++) o[b * n + i] = (byte)(d[i] >> (8 * b));
        }
        else
        {
            var d = new uint[n];
            for (int i = 0; i < n; i++) { uint v = 0; for (int b = 0; b < elementSize; b++) v |= (uint)src[b * n + i] << (8 * b); d[i] = v; }
            uint mask = elementSize == 4 ? 0xFFFFFFFFu : (1u << (8 * elementSize)) - 1;
            for (int i = stride; i < n; i++) d[i] = unchecked(d[i] + d[i - stride]) & mask;
            for (int i = 0; i < n; i++) Put(o, i, elementSize, d[i]);
        }
        return o;
    }

    static uint Get(byte[] b, int i, int size) => size switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4)),
        _ => b[i],
    };

    static void Put(byte[] b, int i, int size, uint v)
    {
        if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)v);
        else if (size == 4) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v);
        else b[i] = (byte)v;
    }
}
