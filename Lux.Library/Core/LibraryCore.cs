using System.Globalization;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Core;

/// <summary>The library implementation (<see cref="ILibrary"/>). Split over <c>LibraryCore.*.cs</c>: folders and lifetime here, queries,
/// organizing, scanning, the context mirror, thumbnails and the job runners in their own files.</summary>
internal sealed partial class LibraryCore : ILibrary
{
    internal readonly LibraryDb Db;
    internal readonly HashCache Hashes;
    internal readonly LibrarySettings SettingsImpl;
    internal readonly JobQueue Queue;
    internal readonly ThumbnailService Thumbs;
    internal readonly ChangeHub Changes;
    internal readonly LibraryOpenOptions Options;
    internal readonly Action<string>? Log;
    FolderWatcher? _watcher;
    Timer? _pendingTimer;
    Timer? _folderTimer;
    /// <summary>Whether each folder could be reached at the last check (<see cref="CheckFolders"/>).</summary>
    readonly Dictionary<long, bool> _reachable = new();
    int _checkingFolders;
    /// <summary>The time between two checks of the folders, in milliseconds.</summary>
    internal const int FolderCheckMs = 2000;
    /// <summary>Completes when the open-time pass over the local context store has finished (tests wait for it).</summary>
    internal readonly TaskCompletionSource _openSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _disposed;

    public string DatabasePath => Db.Path;
    public ILibrarySettings Settings => SettingsImpl;
    public IJobQueue Jobs => Queue;
    readonly Lazy<Lux.DepthModel.DepthModelManager> _depthModel;
    public Lux.DepthModel.IDepthModelService DepthModel => _depthModel.Value;
    /// <summary>The refined depth files of this library (the manager behind <see cref="DepthModel"/>).</summary>
    internal Lux.DepthModel.DepthModelManager DepthModelManager => _depthModel.Value;

    public event EventHandler<CapturesChangedEventArgs>? CapturesChanged;
    public event EventHandler? FoldersChanged;
    public event EventHandler? AlbumsChanged;

    LibraryCore(LibraryOpenOptions o)
    {
        Options = o; Log = o.Log;
        _depthModel = new(() => new Lux.DepthModel.DepthModelManager(o.DepthModelFolder ?? Lux.DepthModel.DepthModelManager.DefaultFolder,
                                                                     Lux.DepthModel.DepthModelManifest.Default, Lux.DepthModel.DepthModelManifest.CurrentRid, null));
        Db = new LibraryDb(o.DatabasePath ?? LibraryPaths.DefaultDatabasePath);
        Hashes = new HashCache(Db);
        SettingsImpl = new LibrarySettings(Db, o);
        Changes = new ChangeHub(e => CapturesChanged?.Invoke(this, e));
        // the display rotation of every item depends on this setting
        SettingsImpl.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ILibrarySettings.ShowPhotosAsHeld)) Changes.Reset(); };
        Thumbs = new ThumbnailService(this);
        Queue = new JobQueue(this);
    }

    internal static ILibrary Open(LibraryOpenOptions options)
    {
        var core = new LibraryCore(options);
        try
        {
            // the engine's stage registry and the fusion set-up's block-flow thread count are process-wide (as in lux-light)
            Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
            // renders wait on shared tiles while the depth effect splits a tile's rows over the pool: keep enough pool threads ready that a
            // tile never waits for the pool to grow (scheduling only)
            ThreadPool.GetMinThreads(out int minW, out int minIo);
            if (minW < Environment.ProcessorCount * 4) ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, minIo);
            Lux.Engine.Pipeline.BayerFusion.BlockFlow.Threads = core.SettingsImpl.RenderThreads;
            // the local context store every ImageContext of this process resolves through (also lux-light's, via the same setting)
            LocalContextStore.Current = new LocalContextStore(core.SettingsImpl.LocalContextFolder);
            // lux-light reads the setting from this small file (never from the database: no -shm / -wal, no time stamps touched)
            LocalContextStore.WriteFolderPointer(Path.GetDirectoryName(Path.GetFullPath(core.Db.Path))!, core.SettingsImpl.LocalContextFolder);
            LocalContextStore.ForgetProbes();   // folders are probed afresh for writing at every open
            core.Queue.Restore();
            if (options.WatchFolders ?? core.SettingsImpl.WatchFolders) core._watcher = new FolderWatcher(core);
            if (options.StartWorkers) core.Queue.Start(); else core.Queue.Start(paused: true);
            core.CheckFolders();   // the first check only records which folders can be reached now
            core._watcher?.Sync();
            core._folderTimer = new Timer(_ => core.CheckFolders(), null, FolderCheckMs, FolderCheckMs);
            // data kept in the local context store (or kept aside after a conflict): settled now — moved next to its capture where the folder
            // can be written at this open, conflicts resolved
            _ = Task.Run(() =>
            {
                try
                {
                    var ids = core.Db.Read(c => c.Rows(null, "SELECT id FROM capture WHERE context_location=2 OR superseded IS NOT NULL", r => r.GetString(0)));
                    if (LocalContextStore.Current is { } st && Directory.Exists(st.Folder))
                        ids.AddRange(Directory.EnumerateDirectories(st.Folder).Select(Path.GetFileName).Where(n => n is { Length: 64 }).Select(n => n!));
                    if (ids.Count > 0) core.Changes.Changed(core.RefreshContexts(ids.Distinct().ToList()));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { core.Trace("local context store: " + e.Message); }
                finally { core._openSettled.TrySetResult(); }
            });
            // ratings kept in the library only: written into context databases now, and every few minutes (a folder may become writable)
            core._pendingTimer = new Timer(_ => { try { if (!core.IsDisposed) core.FlushPendingRatings(); } catch (Exception e) { core.Trace("pending ratings: " + e.Message); } },
                                           null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
            return core;
        }
        catch { core.Dispose(); throw; }
    }

    internal void RaiseFoldersChanged() => FoldersChanged?.Invoke(this, EventArgs.Empty);
    internal void RaiseAlbumsChanged() => AlbumsChanged?.Invoke(this, EventArgs.Empty);
    internal void Trace(string s) => Log?.Invoke(s);

    // ------------------------------------------------------------------------------------------------ folders

    static string Normalise(string path)
    {
        var p = System.IO.Path.GetFullPath(path);
        return p.Length > 1 ? p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : p;
    }

    internal static bool IsUnder(string path, string root)
        => path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) && (path[root.Length] == System.IO.Path.DirectorySeparatorChar || root.EndsWith(System.IO.Path.DirectorySeparatorChar));

    public IReadOnlyList<LibraryFolder> GetFolders() => Db.Read(c =>
    {
        var counts = c.Rows(null, """
            SELECT l.folder_id, count(DISTINCT l.capture_id), count(DISTINCT CASE WHEN cp.missing=1 THEN l.capture_id END)
            FROM location l JOIN capture cp ON cp.id=l.capture_id WHERE cp.hidden=0 GROUP BY l.folder_id
            """, r => (r.GetInt64(0), r.GetInt32(1), r.GetInt32(2))).ToDictionary(t => t.Item1, t => (t.Item2, t.Item3));
        return (IReadOnlyList<LibraryFolder>)c.Rows(null, "SELECT id, path, added_utc, last_scan_utc, cache_level FROM folder ORDER BY id", r =>
        {
            long id = r.GetInt64(0);
            counts.TryGetValue(id, out var n);
            return new LibraryFolder
            {
                Id = id, Path = r.GetString(1), Exists = Directory.Exists(r.GetString(1)), CaptureCount = n.Item1, MissingCount = n.Item2,
                AddedUtc = new DateTime(r.GetInt64(2), DateTimeKind.Utc), LastScanUtc = r.Utc(3),
                DefaultCacheLevel = r.IsDBNull(4) ? null : (CacheLevel)r.GetInt32(4), Scanning = Queue.IsScanning(id), ScanJobId = Queue.ScanJobOf(id),
            };
        });
    });

    internal (long Id, string Path)? FolderRow(long id) => Db.Read(c => c.Rows(null, "SELECT id, path FROM folder WHERE id=$f", r => ((long, string)?)(r.GetInt64(0), r.GetString(1)), ("$f", id)).FirstOrDefault());

    /// <summary>Checks which folders can be reached. A folder can go away and come back while the library is open: for example, its drive
    /// is disconnected and connected again. A folder that comes back is watched again and scanned, so its photos stop showing as missing.
    /// A folder that goes away loses its watcher. Both changes raise <see cref="FoldersChanged"/>. A timer calls this every
    /// <see cref="FolderCheckMs"/> milliseconds. It never throws.</summary>
    internal void CheckFolders()
    {
        if (IsDisposed || Interlocked.Exchange(ref _checkingFolders, 1) != 0) return;
        try
        {
            var folders = Db.Read(c => c.Rows(null, "SELECT id, path FROM folder", r => (Id: r.GetInt64(0), Path: r.GetString(1))));
            var back = new List<(long Id, string Path)>();
            bool changed = false;
            lock (_reachable)
            {
                foreach (var id in _reachable.Keys.Except(folders.Select(f => f.Id)).ToList()) _reachable.Remove(id);
                foreach (var f in folders)
                {
                    bool now = Directory.Exists(f.Path);
                    if (_reachable.TryGetValue(f.Id, out bool was) && was != now)
                    {
                        changed = true;
                        if (now) back.Add(f);
                    }
                    _reachable[f.Id] = now;
                }
            }
            if (!changed) return;
            foreach (var f in back) LocalContextStore.ForgetProbes(f.Path);   // whether its folders can be written is probed afresh
            _watcher?.Sync();
            // with folder watching on, the scan starts by itself; with it off, the user chooses Rescan
            if (_watcher is not null)
                foreach (var f in back) Queue.EnqueueQuietScan(f.Id);
            RaiseFoldersChanged();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!IsDisposed) Trace("folder check: " + e.Message); }
        finally { Volatile.Write(ref _checkingFolders, 0); }
    }

    public IReadOnlyList<SubfolderInfo> GetSubfolders(long folderId)
    {
        var f = FolderRow(folderId) ?? throw new LibraryException("There is no such folder in the library.");
        var dirs = Db.Read(c => c.Rows(null, """
            SELECT l.dir, count(DISTINCT l.capture_id) FROM location l JOIN capture cp ON cp.id=l.capture_id
            WHERE l.folder_id=$f AND cp.hidden=0 GROUP BY l.dir
            """, r => (Dir: r.GetString(0), N: r.GetInt32(1)), ("$f", folderId)));
        // every directory on the way from the folder to a capture directory, with the captures under it
        var under = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ids = Db.Read(c => c.Rows(null, "SELECT l.dir, l.capture_id FROM location l JOIN capture cp ON cp.id=l.capture_id WHERE l.folder_id=$f AND cp.hidden=0",
                                      r => (r.GetString(0), r.GetString(1)), ("$f", folderId)));
        foreach (var (dir, id) in ids)
        {
            for (string? d = dir; d != null && (d == f.Path || IsUnder(d, f.Path)); d = System.IO.Path.GetDirectoryName(d))
            {
                if (!under.TryGetValue(d, out var set)) under[d] = set = new HashSet<string>();
                set.Add(id);
                if (d == f.Path) break;
            }
        }
        _ = dirs;
        return under.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new SubfolderInfo(folderId, kv.Key, kv.Key == f.Path ? "" : System.IO.Path.GetRelativePath(f.Path, kv.Key), kv.Value.Count)).ToList();
    }

    public Task<LibraryFolder> AddFolderAsync(string path, CacheLevel? defaultCacheLevel = null, CancellationToken cancel = default) => Task.Run(() =>
    {
        string p;
        try { p = Normalise(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new LibraryException($"“{path}” is not a valid folder path."); }
        if (!Directory.Exists(p)) throw new LibraryException($"The folder “{p}” does not exist or cannot be opened.");
        long id = Db.Write((c, tx) =>
        {
            var folders = c.Rows(tx, "SELECT id, path FROM folder", r => (Id: r.GetInt64(0), Path: r.GetString(1)));
            var same = folders.FirstOrDefault(f => f.Path == p || IsUnder(p, f.Path));
            if (same.Path is not null) return same.Id;
            long nid = c.Long(tx, "INSERT INTO folder(path, added_utc, cache_level) VALUES($p, $u, $l) RETURNING id", ("$p", p), ("$u", DateTime.UtcNow.Ticks), ("$l", defaultCacheLevel is null ? null : (long)defaultCacheLevel.Value));
            // folders inside the new one are absorbed: their locations move over (identity and everything else unchanged)
            foreach (var inner in folders.Where(f => IsUnder(f.Path, p)))
            {
                c.Exec(tx, "UPDATE location SET folder_id=$n WHERE folder_id=$o", ("$n", nid), ("$o", inner.Id));
                c.Exec(tx, "UPDATE folder SET cache_level=COALESCE((SELECT cache_level FROM folder WHERE id=$n), cache_level) WHERE id=$n", ("$n", nid));
                c.Exec(tx, "DELETE FROM folder WHERE id=$o", ("$o", inner.Id));
            }
            return nid;
        });
        _watcher?.Sync();
        RescanFolder(id);
        RaiseFoldersChanged();
        return GetFolders().First(f => f.Id == id);
    }, cancel);

    public Task RemoveFolderAsync(long folderId, CancellationToken cancel = default) => Task.Run(() =>
    {
        Queue.CancelFolderScans(folderId);
        var affected = Db.Read(c => c.Rows(null, "SELECT DISTINCT capture_id FROM location WHERE folder_id=$f", r => r.GetString(0), ("$f", folderId)));
        var removed = new List<string>();
        Db.Write((c, tx) =>
        {
            c.Exec(tx, "DELETE FROM location WHERE folder_id=$f", ("$f", folderId));
            c.Exec(tx, "DELETE FROM folder WHERE id=$f", ("$f", folderId));
            foreach (var id in affected)
                if (c.Long(tx, "SELECT count(*) FROM location WHERE capture_id=$i", ("$i", id)) == 0)
                {
                    c.Exec(tx, "DELETE FROM capture WHERE id=$i", ("$i", id));
                    removed.Add(id);
                }
        });
        var kept = affected.Except(removed).ToList();
        if (kept.Count > 0) RecomputeDerived(kept);
        _watcher?.Sync();
        Changes.Removed(removed); Changes.Changed(kept);
        RaiseFoldersChanged();
    }, cancel);

    public long RescanFolder(long folderId)
    {
        if (FolderRow(folderId) is null) throw new LibraryException("There is no such folder in the library.");
        return Queue.Enqueue(new FolderScanJobRequest(folderId));
    }

    public IReadOnlyList<long> RescanAll() => GetFolders().Select(f => RescanFolder(f.Id)).ToList();

    // ------------------------------------------------------------------------------------------------ lifetime

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseSessions();   // open photos save their pending edits first
        _folderTimer?.Dispose();
        _watcher?.Dispose();
        _pendingTimer?.Dispose();
        // running jobs stop at their next checkpoint (most within a second; a registration in progress only at its end): wait a little, then
        // close anyway — a job that had not stopped is still marked running in the database and starts again at the next open
        try { Queue.Shutdown(TimeSpan.FromSeconds(10)); } catch (Exception e) { Trace("queue shutdown: " + e.Message); }
        Thumbs.Dispose();
        Changes.Dispose();
        Db.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal static string Plain(Exception e) => e switch
    {
        FileNotFoundException => "The file is no longer there.",
        UnauthorizedAccessException => "Lux is not allowed to read or write this file.",
        // the drive is full (Linux and macOS: ENOSPC 28; Windows: ERROR_DISK_FULL 112, ERROR_HANDLE_DISK_FULL 39)
        IOException io when IsDiskFull(io) => "The drive is full. Free some space, or choose a folder on another drive, and try again.",
        IOException io => "The file could not be read or written (" + io.Message + ").",
        // the exceptions of the readers (a damaged or short file) and programming errors never show their raw text
        ArgumentException or IndexOutOfRangeException or InvalidDataException or FormatException or OverflowException or Google.Protobuf.InvalidProtocolBufferException
            => "The file could not be read: it may be damaged or incomplete.",
        NullReferenceException or InvalidCastException or KeyNotFoundException => "Lux ran into a problem it did not expect. Try again; if it happens again, restart Lux.",
        _ => e.Message,
    };

    internal static bool IsDiskFull(IOException e)
    {
        int code = e.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() ? code is 112 or 39 : code == 28 || e.HResult == 28;
    }

    internal static string Mb(long b) => (b / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>Collects capture changes and raises them at most about ten times a second (on a timer thread).</summary>
internal sealed class ChangeHub : IDisposable
{
    readonly object _l = new();
    readonly HashSet<string> _added = new(), _changed = new(), _removed = new();
    bool _reset;
    long _gen;
    readonly Action<CapturesChangedEventArgs> _raise;
    readonly Timer _timer;
    const int ResetAbove = 5000;

    public ChangeHub(Action<CapturesChangedEventArgs> raise)
    {
        _raise = raise;
        _timer = new Timer(_ => Flush(), null, 100, 100);
    }

    public long Generation => Interlocked.Read(ref _gen);

    public void Added(IEnumerable<string> ids) { lock (_l) { foreach (var i in ids) { _removed.Remove(i); _added.Add(i); } _gen++; Trim(); } }
    public void Changed(IEnumerable<string> ids) { lock (_l) { foreach (var i in ids) if (!_added.Contains(i)) _changed.Add(i); _gen++; Trim(); } }
    public void Removed(IEnumerable<string> ids) { lock (_l) { foreach (var i in ids) { _added.Remove(i); _changed.Remove(i); _removed.Add(i); } _gen++; Trim(); } }
    public void Reset() { lock (_l) { _reset = true; _gen++; } }

    void Trim()
    {
        if (_added.Count + _changed.Count + _removed.Count > ResetAbove) { _reset = true; _added.Clear(); _changed.Clear(); _removed.Clear(); }
    }

    public void Flush()
    {
        CapturesChangedEventArgs? e = null;
        lock (_l)
        {
            if (!_reset && _added.Count == 0 && _changed.Count == 0 && _removed.Count == 0) return;
            e = new CapturesChangedEventArgs { Added = _added.ToArray(), Changed = _changed.ToArray(), Removed = _removed.ToArray(), Reset = _reset, Generation = _gen };
            _added.Clear(); _changed.Clear(); _removed.Clear(); _reset = false;
        }
        try { _raise(e); } catch { /* a subscriber's failure is not the library's */ }
    }

    public void Dispose() { _timer.Dispose(); Flush(); }
}
