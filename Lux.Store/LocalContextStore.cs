using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Lux.Store;

/// <summary>Where a capture's context data was found.</summary>
public enum ContextPlace
{
    /// <summary>No context data.</summary>
    None = 0,
    /// <summary>In <c>LuxImageContext</c> next to the capture.</summary>
    Beside = 1,
    /// <summary>In the local context store (the capture's folder could not be written).</summary>
    Local = 2,
}

/// <summary>
/// The local context store: a folder on this computer that holds, for captures whose own folder cannot be written (read-only drive,
/// permissions, a network share refusing writes), exactly what <c>LuxImageContext</c> would hold next to them —
/// <c>&lt;store&gt;/&lt;sha-256&gt;/edits/&lt;sha-256&gt;.luxedits</c> and <c>&lt;store&gt;/&lt;sha-256&gt;/cache/&lt;sha-256&gt;.luxcache</c>, same
/// names and formats. <see cref="ImageContext"/> resolves through it: reading looks next to the capture first, then here; a new context is
/// created next to the capture when its folder can be written (an actual write probe, cached per folder until <see cref="ForgetProbes"/>),
/// else here.
/// <para><see cref="Reconcile"/> brings the data next to the capture once its folder can be written (copy, verify the SHA-256, switch by an
/// atomic rename, then delete the local copy — an interrupted move leaves one complete copy and is finished or rolled back the next time),
/// and settles conflicts: when both places hold different data, the one next to the capture wins and the local one is kept under
/// <see cref="SupersededFolder"/>, never deleted.</para>
/// <para>Configuration: the desktop app sets <see cref="Current"/> from its settings (default <c>&lt;app data&gt;/contexts</c>) and mirrors that
/// setting into the small file <c>&lt;app data&gt;/context-store-folder.txt</c> (<see cref="WriteFolderPointer"/>); <c>lux-light</c> uses
/// <see cref="FromEnvironment"/>: <c>LUX_CONTEXT_STORE</c>, else that file, else (a library the app has not opened since) an immutable
/// read-only look into <c>&lt;app data&gt;/library.luxlib</c> that creates no <c>-shm</c> / <c>-wal</c> file and writes nothing, else
/// <c>&lt;app data&gt;/contexts</c>, with <c>&lt;app data&gt;</c> = <c>LUX_APP_DATA</c> or the platform's per-user folder.</para>
/// <para>Tests: with <c>LUX_FORBID_DEFAULT_APP_DATA=1</c> (set by the test hosts) and no <c>LUX_APP_DATA</c>, resolving the per-user folder
/// throws — a test can never reach the user's real library.</para>
/// </summary>
public sealed class LocalContextStore
{
    public const string EnvStore = "LUX_CONTEXT_STORE", EnvAppData = "LUX_APP_DATA", SettingKey = "contexts.folder", LibraryFileName = "library.luxlib";
    /// <summary>Set to <c>1</c> by test hosts: resolving the per-user app-data folder (no <c>LUX_APP_DATA</c>) then throws.</summary>
    public const string EnvForbidDefault = "LUX_FORBID_DEFAULT_APP_DATA";
    /// <summary>The file beside the library database that mirrors the store-folder setting for <c>lux-light</c> (one line: the folder, or
    /// empty for the default <c>&lt;app data&gt;/contexts</c>).</summary>
    public const string PointerFileName = "context-store-folder.txt";
    const string MovingSuffix = ".moving";

    /// <summary>The store folder (one sub-folder per capture, named by its content hash).</summary>
    public string Folder { get; }
    /// <summary>Where local data that lost a conflict is kept: the store folder's sibling <c>&lt;folder&gt;-superseded</c>.</summary>
    public string SupersededFolder => Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-superseded";

    public LocalContextStore(string folder) { Folder = Path.GetFullPath(folder); }

    /// <summary>The store every <see cref="ImageContext"/> uses unless given another; null = none (only <c>LuxImageContext</c> next to captures).</summary>
    public static LocalContextStore? Current { get; set; }

    /// <summary>The capture's folder in the store.</summary>
    public string RootFor(string contentHash) => Path.Combine(Folder, contentHash);

    long _mayHoldCheckedTicks; bool _mayHold;
    /// <summary>Whether the store holds anything at all (checked at most once a second): when it does not, nothing has to be hashed to look.</summary>
    public bool MayHoldAny
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref _mayHoldCheckedTicks) > 1000)
            {
                _mayHold = Directory.Exists(Folder) && Directory.EnumerateDirectories(Folder).Any();
                Volatile.Write(ref _mayHoldCheckedTicks, now);
            }
            return _mayHold;
        }
    }

    /// <summary>Note that something was written to the store (so <see cref="MayHoldAny"/> is true at once).</summary>
    public void Touched() { _mayHold = true; Volatile.Write(ref _mayHoldCheckedTicks, Environment.TickCount64); }

    // ------------------------------------------------------------------------------------------------ configuration

    /// <summary>The app's data folder: <c>LUX_APP_DATA</c> when set; else Linux <c>$XDG_DATA_HOME/Lux</c> (default <c>~/.local/share/Lux</c>),
    /// Windows <c>%APPDATA%\Lux</c>, macOS <c>~/Library/Application Support/Lux</c>.</summary>
    public static string AppDataFolder
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(EnvAppData);
            if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
            if (Environment.GetEnvironmentVariable(EnvForbidDefault) is "1" or "true" or "yes")
                throw new InvalidOperationException($"{EnvForbidDefault} is set: this process may not use the per-user Lux app-data folder (the user's own library). Set {EnvAppData} to a scratch folder.");
            if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lux");
            if (OperatingSystem.IsMacOS()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Lux");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "Lux");
        }
    }

    /// <summary>The store a command-line run uses: <c>LUX_CONTEXT_STORE</c>; else the folder the app's setting names — read from
    /// <see cref="PointerFileName"/> in <c>&lt;app data&gt;</c>, or, when that file is missing, from <c>&lt;app data&gt;/library.luxlib</c> opened
    /// immutable and read-only (no lock, no <c>-shm</c> / <c>-wal</c>, nothing written, no time stamp changed); else
    /// <c>&lt;app data&gt;/contexts</c>. Nothing is created.</summary>
    public static LocalContextStore FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable(EnvStore);
        if (!string.IsNullOrEmpty(env)) return new LocalContextStore(env);
        string appData = AppDataFolder;
        string fallback = Path.Combine(appData, "contexts");
        if (ReadFolderPointer(appData) is { } pointed) return new LocalContextStore(pointed.Length > 0 ? pointed : fallback);
        if (ReadSettingImmutable(Path.Combine(appData, LibraryFileName)) is { Length: > 0 } s) return new LocalContextStore(s);
        return new LocalContextStore(fallback);
    }

    /// <summary>The pointer file's folder (empty = the default), or null when there is no readable pointer file.</summary>
    public static string? ReadFolderPointer(string appData)
    {
        try
        {
            var f = Path.Combine(appData, PointerFileName);
            if (!File.Exists(f)) return null;
            var line = File.ReadAllText(f).Split('\n')[0].Trim();
            return line.Length == 0 ? "" : Path.IsPathRooted(line) ? line : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Mirror the store-folder setting next to the library database (the desktop app, at open and whenever the setting changes):
    /// written only when its content differs (atomic replace), so an unchanged setting never touches the file.</summary>
    public static void WriteFolderPointer(string databaseFolder, string? folder)
    {
        try
        {
            var f = Path.Combine(databaseFolder, PointerFileName);
            string text = (folder ?? "") + "\n";
            if (File.Exists(f) && File.ReadAllText(f) == text) return;
            var tmp = f + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, text);
            File.Move(tmp, f, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One setting from a library database without changing anything on disk: SQLite's <c>immutable=1</c> URI (read-only, no locks,
    /// no <c>-shm</c> / <c>-wal</c> files). A value still only in the app's write-ahead log is not seen (the pointer file covers that case).</summary>
    static string? ReadSettingImmutable(string lib)
    {
        if (!File.Exists(lib)) return null;
        try
        {
            var uri = "file:" + Uri.EscapeDataString(Path.GetFullPath(lib)).Replace("%2F", "/") + "?immutable=1";
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM setting WHERE key=$k"; cmd.Parameters.AddWithValue("$k", SettingKey);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }
    }

    // ------------------------------------------------------------------------------------------------ write probes

    static readonly ConcurrentDictionary<string, bool> Probes = new(StringComparer.Ordinal);

    /// <summary>Whether a folder can be written: an actual probe (create and delete a small file), cached per folder until
    /// <see cref="ForgetProbes"/>.</summary>
    public static bool IsWritable(string folder) => Probes.GetOrAdd(Path.GetFullPath(folder), Probe);

    static bool Probe(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        string p = Path.Combine(folder, $".lux-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using (var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) fs.WriteByte(0);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        finally { try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Forget the cached probes (all, or those under <paramref name="folder"/>) — done when the library opens and when a folder is scanned.</summary>
    public static void ForgetProbes(string? folder = null)
    {
        if (folder is null) { Probes.Clear(); return; }
        string f = Path.GetFullPath(folder);
        foreach (var k in Probes.Keys) if (k == f || k.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.Ordinal)) Probes.TryRemove(k, out _);
    }

    // ------------------------------------------------------------------------------------------------ moving and conflicts

    /// <summary>What <see cref="Reconcile"/> did.</summary>
    public sealed record ReconcileResult(int Moved, int Finished, int RolledBack, int Superseded, string? SupersededPath, long Bytes);

    /// <summary>
    /// Settle a capture's context between the local store and the place next to it:
    /// <list type="bullet">
    /// <item>leftovers of an interrupted move (<c>*.moving</c> next to the capture) are removed (rolled back) — the local copy is still whole;</item>
    /// <item>a file in both places with the same SHA-256 is a move that stopped after its switch: the local copy is deleted (finished);</item>
    /// <item>a file in both places with different content is a conflict: the one next to the capture wins and stays; the local one is moved to
    /// <c>&lt;superseded&gt;/&lt;hash&gt;-&lt;UTC time&gt;/edits|cache/</c> — never deleted;</item>
    /// <item>a file only in the store, when the capture's folder can be written now (<paramref name="mayMove"/>): copied next to the capture
    /// under a temporary name, flushed, its SHA-256 compared with the source, renamed into place (the switch), then the local copy deleted.</item>
    /// </list>
    /// The edit database (user work) is never lost; the analysis cache (regenerable) is moved the same way, because rebuilding it costs about a
    /// minute and several GB of memory per capture, and a copy is seconds — a cache that cannot be read as one is dropped instead.
    /// The caller makes sure nothing has the capture's context open meanwhile.
    /// </summary>
    public ReconcileResult Reconcile(ImageContext ctx, bool mayMove, Action<string>? log = null)
    {
        string hash = ctx.ContentHash, localRoot = RootFor(hash), besideRoot = ctx.BesideRoot;
        int moved = 0, finished = 0, rolledBack = 0, superseded = 0; long bytes = 0; string? supersededPath = null;
        foreach (var (sub, ext) in new[] { (ImageContext.EditsFolderName, ImageContext.EditsExtension), (ImageContext.CacheFolderName, AnalysisCacheStore.Extension) })
        {
            string local = Path.Combine(localRoot, sub, hash + ext), beside = Path.Combine(besideRoot, sub, hash + ext);
            // an interrupted move's half-made copy: roll back (the local file is complete)
            if (File.Exists(beside + MovingSuffix))
            {
                try { File.Delete(beside + MovingSuffix); rolledBack++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (!File.Exists(local)) continue;
            Settle(local, sub == ImageContext.EditsFolderName);
            if (File.Exists(beside))
            {
                if (Sha(local) == Sha(beside)) { DeleteDb(local); finished++; log?.Invoke($"finished an interrupted move of {sub} for {hash[..12]}…"); continue; }
                // conflict: the data next to the capture wins; the local one is kept aside
                supersededPath ??= Path.Combine(SupersededFolder, $"{hash}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");
                string dst = Path.Combine(supersededPath, sub, hash + ext);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                MoveVerified(local, dst);
                superseded++;
                log?.Invoke($"{sub} of {hash[..12]}… exists next to the capture and differs from the local copy: kept the one next to the capture, the local one is in {supersededPath}");
                continue;
            }
            if (!mayMove) continue;
            if (sub == ImageContext.CacheFolderName && !IsReadableDb(local)) { DeleteDb(local); log?.Invoke($"dropped an unreadable local cache of {hash[..12]}…"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(beside)!);
            bytes += new FileInfo(local).Length;
            CopyVerified(local, beside, "copied");
            TestPause("switched");
            DeleteDb(local);
            moved++;
            log?.Invoke($"moved {sub} of {hash[..12]}… next to the capture");
        }
        RemoveEmpty(localRoot);
        if (moved + superseded > 0) Touched();
        return new ReconcileResult(moved, finished, rolledBack, superseded, supersededPath, bytes);
    }

    /// <summary>The newest superseded copy of a capture's context, or null.</summary>
    public string? SupersededOf(string contentHash)
        => Directory.Exists(SupersededFolder) ? Directory.EnumerateDirectories(SupersededFolder, contentHash + "-*").OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault() : null;

    /// <summary>Bring a SQLite file to rest before it is copied: roll back a hot journal (edits) / fold the write-ahead log in (cache).</summary>
    public static void SettleDatabase(string db) => Settle(db, db.EndsWith(ImageContext.EditsExtension, StringComparison.Ordinal));

    static void Settle(string db, bool edits)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 10 }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = edits ? "SELECT count(*) FROM sqlite_master" : "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteScalar();
        }
        catch (SqliteException) { }
        SqliteConnection.ClearAllPools();
    }

    static bool IsReadableDb(string db)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA quick_check"; return cmd.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException) { return false; }
    }

    static void DeleteDb(string db)
    {
        foreach (var s in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(db + s)) File.Delete(db + s);
    }

    static void RemoveEmpty(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root)) if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string Sha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/> crash-safely: write <c>dst.moving</c>, flush it to disk, compare SHA-256 with
    /// the source, rename into place. Throws <see cref="IOException"/> when the copy does not verify (nothing is switched).</summary>
    public static void CopyVerified(string src, string dst, string? pauseStep = null)
    {
        string tmp = dst + MovingSuffix;
        using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
        using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            i.CopyTo(o);
            o.Flush(flushToDisk: true);
        }
        if (Sha(src) != Sha(tmp)) { File.Delete(tmp); throw new IOException($"the copy of {Path.GetFileName(src)} does not match the original"); }
        if (pauseStep is not null) TestPause(pauseStep);
        File.Move(tmp, dst, overwrite: true);
    }

    /// <summary>Move a file: a rename on the same file system, else a verified copy and a delete.</summary>
    public static void MoveVerified(string src, string dst)
    {
        try { File.Move(src, dst); return; }
        catch (IOException) when (!File.Exists(dst)) { }
        CopyVerified(src, dst);
        DeleteDb(src);
    }

    /// <summary>Test hook for the crash tests only (`lib-test`): with <c>LUX_TEST_CONTEXT_MOVE_PAUSE</c> = a step name ("copied", "switched"),
    /// the mover writes the file <c>LUX_TEST_CONTEXT_MOVE_MARKER</c> and stops there until the process is killed.</summary>
    static void TestPause(string step)
    {
        if (Environment.GetEnvironmentVariable("LUX_TEST_CONTEXT_MOVE_PAUSE") != step) return;
        if (Environment.GetEnvironmentVariable("LUX_TEST_CONTEXT_MOVE_MARKER") is { Length: > 0 } m) File.WriteAllText(m, step);
        Thread.Sleep(Timeout.Infinite);
    }

    public override string ToString() => Folder;
    internal static string Stamp() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
