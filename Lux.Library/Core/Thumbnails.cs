using System.Diagnostics;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Core;

/// <summary>
/// Thumbnails: the capture's level-4 display render (the smallest export level) through the session API, JPEG-encoded (quality 85) and
/// stored in the library database with the state key of the edits they were made from; a capture with saved settings (an imported Lumen
/// session's tone, crop / rotation, depth effect) is rendered through the export path at level 4 so that they apply. Requests on screen go
/// first, newest first (LIFO); a request whose caller no longer waits is dropped before it starts; background batches (after a scan, after an
/// edit) follow in order. All of it runs as one <see cref="JobKind.Thumbnail"/> job in the queue.
/// </summary>
internal sealed class ThumbnailService : IDisposable
{
    readonly LibraryCore _lib;
    readonly object _l = new();

    sealed class Waiter { public required TaskCompletionSource<ThumbnailImage?> Tcs; public CancellationTokenRegistration Reg; }
    sealed class Req { public required string Id; public readonly List<Waiter> Waiters = new(); public LinkedListNode<Req>? Node; }

    readonly LinkedList<Req> _interactive = new();          // front = newest
    readonly Dictionary<string, Req> _pending = new(StringComparer.Ordinal);
    readonly LinkedList<string> _batch = new();
    readonly HashSet<string> _batchSet = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Waiter>> _inFlight = new(StringComparer.Ordinal);
    long? _jobId;
    string? _lastName;   // the photo made last (the Queue row names it)
    int _done, _total;
    int _parallelism = 1;

    /// <summary>Peak memory of one thumbnail render in a process making several at once (level 4 from nothing: 0.56 GB single-frame, 1.1 GB
    /// stacked alone; 6 at once peaked at 8.0–9.1 GB, so 1.5 GB each with the collector's slack).</summary>
    public const long PerThumbnailDefault = 3L << 29;
    /// <summary>The reservation per thumbnail (setting <c>reserve.Thumbnail</c>, default 1.5 GB).</summary>
    public long PerThumbnail => (long)(_lib.SettingsImpl.GetReservationGb(JobReservation.Thumbnail) * (1L << 30));

    /// <summary>Statistics for tests: render times of the thumbnails made (ms) and the order they finished in.</summary>
    internal readonly List<(string Id, double Ms, bool Interactive, long FinishedTicks)> Made = new();

    public ThumbnailService(LibraryCore lib) { _lib = lib; }

    /// <summary>Thumbnails at once: the setting, or automatic (render threads ÷ 4, 1–6).</summary>
    int MaxParallel => _lib.SettingsImpl.ThumbnailConcurrency is int n && n > 0 ? n : Math.Clamp(_lib.SettingsImpl.RenderThreads / 4, 1, 6);
    int ThreadsEach => Math.Max(1, _lib.SettingsImpl.RenderThreads / Math.Max(1, _parallelism));

    /// <summary>Called by the dispatcher when the thumbnail job starts: how much memory it reserves (sets its parallelism).</summary>
    public long Reserve(long free)
    {
        _parallelism = (int)Math.Clamp(free / PerThumbnail, 1, MaxParallel);
        return _parallelism * PerThumbnail;
    }

    // ------------------------------------------------------------------------------------------------ the stored thumbnail

    public ThumbnailImage? Stored(string id)
        => _lib.Db.Read(c => c.Rows(null, """
            SELECT t.format, t.width, t.height, t.made_utc, t.state_key, c.thumb_key, t.data FROM thumbnail t JOIN capture c ON c.id=t.capture_id WHERE t.capture_id=$i
            """, r => new ThumbnailImage(id, (byte[])r.GetValue(6), (ThumbnailFormat)r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc),
                                         r.GetString(4) != r.GetString(5)), ("$i", id)).FirstOrDefault());

    // ------------------------------------------------------------------------------------------------ requests

    public Task<ThumbnailImage?> Get(string id, CancellationToken cancel)
    {
        var stored = Stored(id);
        if (stored is not null && !stored.IsStale) return Task.FromResult<ThumbnailImage?>(stored);
        var state = _lib.Db.Read(c => c.Rows(null, "SELECT missing, unreadable FROM capture WHERE id=$i", r => (Missing: r.GetInt64(0) != 0, Unreadable: r.GetInt64(1) != 0), ("$i", id)).FirstOrDefault());
        if (state == default && !_lib.Db.Read(c => c.Long(null, "SELECT count(*) FROM capture WHERE id=$i", ("$i", id)) > 0)) return Task.FromResult<ThumbnailImage?>(null);
        if (state.Missing || state.Unreadable) return Task.FromResult(stored);
        if (stored is not null) { Regenerate(new[] { id }); return Task.FromResult<ThumbnailImage?>(stored); }
        if (cancel.IsCancellationRequested) return Task.FromCanceled<ThumbnailImage?>(cancel);
        var w = new Waiter { Tcs = new TaskCompletionSource<ThumbnailImage?>(TaskCreationOptions.RunContinuationsAsynchronously) };
        lock (_l)
        {
            if (_inFlight.TryGetValue(id, out var fl)) { fl.Add(w); }
            else
            {
                if (!_pending.TryGetValue(id, out var req)) { req = new Req { Id = id }; _pending[id] = req; _total++; }
                if (req.Node is not null) _interactive.Remove(req.Node);
                req.Node = _interactive.AddFirst(req);
                req.Waiters.Add(w);
            }
        }
        if (cancel.CanBeCanceled) w.Reg = cancel.Register(() => Drop(id, w, cancel));
        EnsureJob(0);
        return w.Tcs.Task;
    }

    void Drop(string id, Waiter w, CancellationToken cancel)
    {
        lock (_l)
        {
            if (_pending.TryGetValue(id, out var req) && req.Waiters.Remove(w) && req.Waiters.Count == 0)
            {
                if (req.Node is not null) _interactive.Remove(req.Node);
                _pending.Remove(id);
                if (!_batchSet.Contains(id)) _total--;
            }
            else if (_inFlight.TryGetValue(id, out var fl)) fl.Remove(w);
        }
        w.Tcs.TrySetCanceled(cancel);
    }

    /// <summary>Queue background thumbnails; returns the thumbnail job's id (null when nothing needs making).</summary>
    public long? AddBatch(IEnumerable<string> ids)
    {
        int added = 0;
        lock (_l)
            foreach (var id in ids)
                if (_batchSet.Add(id)) { _batch.AddLast(id); if (!_pending.ContainsKey(id) && !_inFlight.ContainsKey(id)) { _total++; added++; } }
        if (added == 0) { lock (_l) return _jobId; }
        return EnsureJob(_lib.SettingsImpl.PriorityOf(JobKind.Thumbnail));
    }

    /// <summary>Thumbnails whose capture's edits changed: made again in the background (the old one is shown meanwhile).</summary>
    public void Regenerate(IEnumerable<string> ids) => AddBatch(ids);

    long EnsureJob(int priority)
    {
        lock (_l)
        {
            if (_jobId is long id) { if (priority < (_lib.Queue.Get(id)?.Priority ?? int.MaxValue)) _lib.Queue.SetJobPriority(id, priority); return id; }
            _done = 0;
            _jobId = -1;   // being created
        }
        long nid = _lib.Queue.EnqueueThumbnailJob(priority);
        lock (_l) _jobId = nid;
        return nid;
    }

    /// <summary>The job ended (done, cancelled, or the queue was paused / closed).</summary>
    public void JobEnded(long jobId, bool cancelled)
    {
        List<Waiter> fail = new();
        lock (_l)
        {
            if (_jobId != jobId) return;
            _jobId = null;
            if (cancelled)
            {
                // the user cancelled the thumbnail job: drop what is queued
                foreach (var r in _pending.Values) fail.AddRange(r.Waiters);
                _pending.Clear(); _interactive.Clear(); _batch.Clear(); _batchSet.Clear(); _total = 0; _done = 0;
            }
        }
        foreach (var w in fail) w.Tcs.TrySetResult(null);
        bool more; lock (_l) more = _pending.Count > 0 || _batch.Count > 0;
        if (more && !cancelled) EnsureJob(_pending.Count > 0 ? 0 : _lib.SettingsImpl.PriorityOf(JobKind.Thumbnail));
    }

    // ------------------------------------------------------------------------------------------------ the job

    (string Id, bool Interactive)? Next()
    {
        lock (_l)
        {
            while (_interactive.First is { } n)
            {
                var req = n.Value; _interactive.RemoveFirst(); req.Node = null; _pending.Remove(req.Id);
                if (_batchSet.Remove(req.Id)) _batch.Remove(req.Id);
                _inFlight[req.Id] = req.Waiters;
                return (req.Id, true);
            }
            while (_batch.First is { } b)
            {
                _batch.RemoveFirst(); _batchSet.Remove(b.Value);
                if (_inFlight.ContainsKey(b.Value)) { _total--; continue; }
                _inFlight[b.Value] = new List<Waiter>();
                return (b.Value, false);
            }
            return null;
        }
    }

    public async Task RunAsync(JobContext job)
    {
        var running = new List<Task>();
        try
        {
            while (true)
            {
                job.Cancel.ThrowIfCancellationRequested();
                if (running.Count >= Math.Min(_parallelism, MaxParallel)) { await Task.WhenAny(running).ConfigureAwait(false); running.RemoveAll(t => t.IsCompleted); continue; }
                var next = Next();
                if (next is null)
                {
                    if (running.Count == 0)
                    {
                        lock (_l) if (_pending.Count == 0 && _batch.Count == 0) { job.Report(1, _done == 1 && _lastName is { } n ? $"Done: {n}" : $"Done: {_done} made" + (_lastName is { } l ? $", the last {l}" : "")); return; }
                        continue;
                    }
                    await Task.WhenAny(running.Append(Task.Delay(50))).ConfigureAwait(false);
                    running.RemoveAll(t => t.IsCompleted);
                    continue;
                }
                var (id, interactive) = next.Value;
                running.Add(Task.Run(() => MakeOne(id, interactive, job)));
                // a new on-screen request makes the job interactive (priority 0)
                if (interactive && job.Job.Priority > 0) _lib.Queue.SetJobPriority(job.JobId, 0);
            }
        }
        finally
        {
            try { await Task.WhenAll(running).ConfigureAwait(false); } catch { }
        }
    }

    void MakeOne(string id, bool interactive, JobContext job)
    {
        ThumbnailImage? img = null;
        bool requeue = false;
        try
        {
            job.Cancel.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            img = Make(id, job.Cancel);
            sw.Stop();
            _lib.CollectAfter(170_000_000);
            lock (Made) Made.Add((id, sw.Elapsed.TotalMilliseconds, interactive, DateTime.UtcNow.Ticks));
        }
        catch (OperationCanceledException) { requeue = true; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _lib.Trace($"thumbnail of {id[..12]}…: {e.Message}");
            if (e is LibraryException or IOException or InvalidDataException or KeyNotFoundException or ExportRequestException)
                _lib.Db.Write((c, tx) => c.Exec(tx, "UPDATE capture SET error=$e WHERE id=$i AND error IS NULL", ("$e", "No preview could be made: " + LibraryCore.Plain(e)), ("$i", id)));
        }
        List<Waiter> waiters;
        lock (_l)
        {
            waiters = _inFlight.TryGetValue(id, out var w) ? w : new();
            _inFlight.Remove(id);
            if (requeue)
            {
                // interrupted (pause / close): back to the front so it is made first next time
                if (waiters.Count > 0) { var req = new Req { Id = id }; req.Waiters.AddRange(waiters); _pending[id] = req; req.Node = _interactive.AddFirst(req); waiters = new(); }
                else if (_batchSet.Add(id)) _batch.AddFirst(id);
            }
            else _done++;
            job.Items(_done, Math.Max(_total, _done));
            // the Queue row names the photo
            _lastName = _lib.NameOf(id);
            job.Report(_total > 0 ? Math.Min(0.99, (double)_done / _total) : 0, $"{_lastName} · {_done} of {Math.Max(_total, _done)}");
        }
        foreach (var w in waiters) { w.Reg.Dispose(); w.Tcs.TrySetResult(img); }
        if (img is not null) _lib.Changes.Changed(new[] { id });
    }

    /// <summary>Render, encode and store one thumbnail.</summary>
    ThumbnailImage? Make(string id, CancellationToken cancel)
    {
        string key = _lib.Db.Read(c => c.Scalar(null, "SELECT thumb_key FROM capture WHERE id=$i", ("$i", id)) as string) ?? "clean";
        string path;
        try { path = _lib.PresentPath(id); } catch (LibraryException) { return null; }
        var sw = Stopwatch.StartNew();
        var ctx = new StoreCaptureContext(path, _lib.Hashes, mayCreateFolder: false);
        byte[] jpeg; int w, h;
        int threads = ThreadsEach;
        using var s = CaptureSession.Open(path, new SessionOptions { Context = ctx, Threads = threads, DisplayCacheBytes = 0, KeepGeometry = false });
        if (key == "clean" || s.SavedState is null)
        {
            (w, h) = s.LevelSize(4);
            var tile = s.RenderAsync(4, new RectI(0, 0, w, h), new RenderSettings(), cancel).GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            JpegEncoder.Encode(ms, tile.Rgba8!, w, h, w * 4, false, new JpegEncoder { Quality = 85, SubsamplingId = 2 });
            jpeg = ms.ToArray();
        }
        else
        {
            // with saved settings: the level-4 JPG of the session's export (saved crop / rotation, tone, depth effect, a Lumen session's own
            // geometry — the one path the viewer and the exports use)
            string tmp = Path.Combine(Path.GetDirectoryName(_lib.Db.Path)!, "tmp", Guid.NewGuid().ToString("N"));
            try
            {
                var req = new ExportRequest { Formats = new[] { ExportImageFormat.Jpeg }, Level = 4, OutDirectory = tmp, Stem = "thumb", Context = ctx, CacheUse = CacheUse.Read, JpegQuality = 85 };
                var res = s.ExportAsync(req, cancel).GetAwaiter().GetResult();
                jpeg = File.ReadAllBytes(res.PathOf(ExportImageFormat.Jpeg)!);
                (w, h) = (res.Width, res.Height);
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { } }
        }
        var now = DateTime.UtcNow;
        _lib.Db.Write((c, tx) =>
        {
            c.Exec(tx, """
                INSERT INTO thumbnail(capture_id, format, width, height, state_key, made_utc, ms, data) VALUES($i, 0, $w, $h, $k, $u, $ms, $d)
                ON CONFLICT(capture_id) DO UPDATE SET format=0, width=$w, height=$h, state_key=$k, made_utc=$u, ms=$ms, data=$d
                """, ("$i", id), ("$w", (long)w), ("$h", (long)h), ("$k", key), ("$u", now.Ticks), ("$ms", (long)sw.ElapsedMilliseconds), ("$d", jpeg));
            c.Exec(tx, "UPDATE capture SET thumb_state=$k WHERE id=$i", ("$k", key), ("$i", id));
        });
        // edits changed while it was being made: make it again
        string after = _lib.Db.Read(c => c.Scalar(null, "SELECT thumb_key FROM capture WHERE id=$i", ("$i", id)) as string) ?? "clean";
        if (after != key) Regenerate(new[] { id });
        return new ThumbnailImage(id, jpeg, ThumbnailFormat.Jpeg, w, h, now, after != key);
    }

    public void Dispose()
    {
        List<Waiter> all = new();
        lock (_l) { foreach (var r in _pending.Values) all.AddRange(r.Waiters); foreach (var f in _inFlight.Values) all.AddRange(f); _pending.Clear(); _interactive.Clear(); }
        foreach (var w in all) w.Tcs.TrySetResult(null);
    }
}

internal sealed partial class LibraryCore
{
    public Task<ThumbnailImage?> GetThumbnailAsync(string captureId, CancellationToken cancel = default) => Thumbs.Get(captureId, cancel);
    public ThumbnailImage? TryGetStoredThumbnail(string captureId) => Thumbs.Stored(captureId);
    public long? PrefetchThumbnails(IReadOnlyCollection<string> captureIds)
    {
        var need = Db.Read(c => captureIds.Distinct().Where(id => c.Long(null, "SELECT count(*) FROM capture WHERE id=$i AND missing=0 AND unreadable=0 AND (thumb_state IS NULL OR thumb_state <> thumb_key)", ("$i", id)) > 0).ToList());
        return need.Count == 0 ? null : Thumbs.AddBatch(need);
    }
}
