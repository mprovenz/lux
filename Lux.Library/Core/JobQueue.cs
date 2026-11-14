using System.Text.Json;
using Lux.Engine.Session;

namespace Lux.Library.Core;

/// <summary>What a running job reports through (any thread).</summary>
internal sealed class JobContext
{
    internal readonly JobQueue.Job Job;
    readonly JobQueue _q;
    internal JobContext(JobQueue q, JobQueue.Job job) { _q = q; Job = job; }
    public CancellationToken Cancel => Job.Cts!.Token;
    public long JobId => Job.Id;
    public long EstimatedMemoryBytes => Job.EstMemory;
    public void Report(double progress, string? status = null) => _q.Progress(Job, progress, status);
    public void Items(int done, int total) { lock (Job) { Job.ItemsDone = done; Job.ItemsTotal = total; } }
    public void Note(string note) { lock (Job) Job.Notes.Add(note); }
    public void Result(string? path, IEnumerable<string>? files = null) { lock (Job) { Job.ResultPath = path; if (files is not null) Job.ResultFiles = files.ToList(); } }
    /// <summary>The file the job will write, as decided when it starts (after the collision rule): shown on its row at once.</summary>
    public void Planned(string? path) { lock (Job) Job.PlannedFile = path; _q.Progress(Job, Job.Progress, Job.Status); }
}

/// <summary>
/// The scheduler (<see cref="IJobQueue"/>): one dispatcher thread starts waiting jobs in priority order (lower first, then oldest) while
/// fewer than <see cref="ILibrarySettings.WorkerCount"/> run and their estimated peak memory fits in what the running jobs leave of the
/// budget; a heavy job that does not fit holds back every other heavy job behind it (no overtaking), light ones (≤ 1.5 GB) may pass; when
/// nothing runs, the first job always starts. The thumbnail batch is outside the worker count (it is what the screen waits for) but sizes its
/// parallelism to the memory left. While heavy jobs run, a watchdog collects garbage whenever the process passes 90 % of the budget.
/// Every job is a row of the <c>job</c> table: unfinished ones are queued again when the library is next opened.
/// </summary>
internal sealed class JobQueue : IJobQueue
{
    internal sealed class Job
    {
        public long Id; public JobRequest Request = null!; public JobKind Kind; public string? CaptureId; public long? FolderId;
        public string Title = ""; public JobState State; public double Progress; public string Status = ""; public int Priority; public long EstMemory;
        public DateTime Created; public DateTime? Started, Finished; public string? Error; public string? ResultPath;
        public List<string> ResultFiles = new(); public List<string> Notes = new(); public int? ItemsDone, ItemsTotal; public long? RetryOf;
        public bool Held, Interrupted, RemoveWhenDone; public CancellationTokenSource? Cts; public long Reserved; public string Detail = "";
        public string? PlannedFile;
        public string? Subject;
        public readonly TaskCompletionSource<JobInfo> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastEventTicks;
    }

    public const long Gb = 1L << 30;
    public const long LightBytes = 3 * Gb / 2;

    readonly LibraryCore _lib;
    readonly object _l = new();
    readonly List<Job> _jobs = new();
    readonly Dictionary<string, Job> _active = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    readonly List<Task> _tasks = new();
    Thread? _dispatcher;
    bool _paused, _shutdown;
    long _reserved;
    int _heavyRunning;
    IDisposable? _watchdog;

    public event EventHandler<JobChangedEventArgs>? JobChanged;
    public event EventHandler<JobQueueStatus>? StatusChanged;

    long _interactiveUntil;
    volatile bool _estimatesDirty;

    public JobQueue(LibraryCore lib)
    {
        _lib = lib;
        // every limit takes effect on the running queue at once: estimates of waiting jobs are recomputed, the dispatcher looks again, and the
        // memory watchdog follows a new budget (running jobs are never stopped by a lower limit; they finish)
        lib.SettingsImpl.PropertyChanged += (_, e) =>
        {
            _estimatesDirty = true;
            if (e.PropertyName is nameof(ILibrarySettings.MemoryBudgetGb) or nameof(ILibrarySettings.MemoryBudgetFixedGb) or nameof(ILibrarySettings.MemoryBudgetPercentOfRam))
                lock (_l) if (_watchdog is not null) { _watchdog.Dispose(); _watchdog = MemoryBudget.Watch(Budget); }
            Signal();
            try { StatusChanged?.Invoke(this, Status); } catch { }
        };
    }

    /// <summary>The UI's hint that the user is interacting (expires 30 s after the last true).</summary>
    public void SetInteractive(bool interacting)
    {
        Interlocked.Exchange(ref _interactiveUntil, interacting ? Environment.TickCount64 + 30_000 : 0);
        Signal();
    }

    bool HoldHeavy => _lib.SettingsImpl.PauseHeavyJobsWhileInteracting && Environment.TickCount64 < Interlocked.Read(ref _interactiveUntil);

    long Budget => (long)(_lib.SettingsImpl.MemoryBudgetGb * Gb);

    // ------------------------------------------------------------------------------------------------ persistence

    /// <summary>Load the job table: finished jobs as history (newest 500), unfinished ones queued again from the start.</summary>
    public void Restore()
    {
        var rows = _lib.Db.Read(c => c.Rows(null, """
            SELECT id, kind, capture_id, folder_id, title, state, progress, status, priority, request, est_memory, held, created_utc, started_utc, finished_utc,
                   error, result_path, result_files, notes, items_done, items_total, retry_of FROM job ORDER BY id
            """, r => (Id: r.GetInt64(0), Kind: (JobKind)r.GetInt32(1), Capture: r.Str(2), Folder: r.LongN(3), Title: r.GetString(4), State: (JobState)r.GetInt32(5),
                       Progress: r.GetDouble(6), Status: r.GetString(7), Priority: r.GetInt32(8), Request: r.GetString(9), Est: r.GetInt64(10), Held: r.GetInt64(11) != 0,
                       Created: r.GetInt64(12), Started: r.Utc(13), Finished: r.Utc(14), Error: r.Str(15), Result: r.Str(16), Files: r.Str(17), Notes: r.Str(18),
                       ItemsDone: r.LongN(19), ItemsTotal: r.LongN(20), RetryOf: r.LongN(21))));
        var finished = rows.Where(r => r.State is JobState.Done or JobState.Failed or JobState.Cancelled).ToList();
        if (finished.Count > 500)
        {
            long cut = finished[^500].Id;
            _lib.Db.Write((c, tx) => c.Exec(tx, "DELETE FROM job WHERE id < $c AND state IN (3, 4, 5)", ("$c", cut)));
            finished = finished.Where(f => f.Id >= cut).ToList();
        }
        var requeue = new List<long>();
        var importsStopped = new List<Job>();
        lock (_l)
        {
            foreach (var r in rows)
            {
                bool done = r.State is JobState.Done or JobState.Failed or JobState.Cancelled;
                if (done && !finished.Any(f => f.Id == r.Id)) continue;
                JobRequest? req = JobRequests.Deserialize(r.Kind, r.Request);
                if (req is null) continue;
                var j = new Job
                {
                    Id = r.Id, Request = req, Kind = r.Kind, CaptureId = r.Capture, FolderId = r.Folder, Title = r.Title, State = r.State, Progress = done ? r.Progress : 0,
                    Status = done ? r.Status : "Waiting", Priority = r.Priority, EstMemory = r.Est, Held = r.Held && !done, Created = new DateTime(r.Created, DateTimeKind.Utc),
                    Started = done ? r.Started : null, Finished = r.Finished, Error = r.Error, ResultPath = r.Result,
                    ResultFiles = r.Files is null ? new() : JsonSerializer.Deserialize<List<string>>(r.Files) ?? new(),
                    Notes = r.Notes is null ? new() : JsonSerializer.Deserialize<List<string>>(r.Notes) ?? new(),
                    ItemsDone = (int?)r.ItemsDone, ItemsTotal = (int?)r.ItemsTotal, RetryOf = r.RetryOf, Detail = LibraryCore.DetailOf(req),
                    PlannedFile = _lib.PlannedFileOf(req), Subject = LibraryCore.SubjectOf(req),
                };
                if (!done && j.Kind == JobKind.Import)
                {
                    // a camera import is not started again by itself (the camera may not be there, nothing is attached yet to run it): it ends
                    // as stopped, keeps what it copied, and can be continued with Retry (files already copied are skipped)
                    j.State = JobState.Failed; j.Finished = DateTime.UtcNow; j.Status = "Failed";
                    j.Error = "Lux was closed during this import. The photos copied so far are kept; continue it to copy the rest.";
                    importsStopped.Add(j);
                    done = true;
                }
                if (!done)
                {
                    j.State = JobState.Waiting;
                    if (r.State == JobState.Running) { j.Notes.Add("Started again: Lux was closed while this was running."); requeue.Add(j.Id); }
                    if (j.Kind == JobKind.Thumbnail) continue;   // thumbnail batches are rebuilt from what is missing, not restored
                }
                if (j.Kind == JobKind.Thumbnail && !done) continue;
                if (j.Done.Task.IsCompleted == false && done) j.Done.TrySetResult(Snap(j));
                _jobs.Add(j);
            }
            RebuildActive();
        }
        foreach (var j in importsStopped) Persist(j);
        _lib.Db.Write((c, tx) =>
        {
            c.Exec(tx, "DELETE FROM job WHERE kind=$k AND state IN (0, 1, 2)", ("$k", (long)JobKind.Thumbnail));
            c.Exec(tx, "UPDATE job SET state=0, progress=0, status='Waiting', started_utc=NULL WHERE state=1");
        });
    }

    void Persist(Job j, bool insert = false)
    {
        JobInfo s; string req;
        lock (j) { s = Snap(j); req = JobRequests.Serialize(j.Request); }
        object?[] v = { s.Id, (long)s.Kind, s.CaptureId, s.FolderId, s.Title, (long)s.State, s.Progress, s.Status, (long)s.Priority, req, s.EstimatedMemoryBytes,
                        j.Held ? 1L : 0L, s.CreatedUtc.Ticks, s.StartedUtc?.Ticks, s.FinishedUtc?.Ticks, s.Error, s.ResultPath,
                        JsonSerializer.Serialize(s.ResultFiles), JsonSerializer.Serialize(s.Notes), (long?)s.ItemsDone, (long?)s.ItemsTotal, s.RetryOf };
        string[] n = { "id", "kind", "capture_id", "folder_id", "title", "state", "progress", "status", "priority", "request", "est_memory", "held", "created_utc",
                       "started_utc", "finished_utc", "error", "result_path", "result_files", "notes", "items_done", "items_total", "retry_of" };
        try
        {
            _lib.Db.Write((c, tx) => c.Exec(tx,
                $"INSERT INTO job({string.Join(", ", n)}) VALUES({string.Join(", ", n.Select(x => "$" + x))}) ON CONFLICT(id) DO UPDATE SET {string.Join(", ", n.Skip(1).Select(x => $"{x}=excluded.{x}"))}",
                n.Select((x, i) => ("$" + x, v[i])).ToArray()));
        }
        catch (ObjectDisposedException) { }
    }

    long NewId(JobRequest req, JobKind kind, string title, int prio)
        => _lib.Db.Write((c, tx) => c.Long(tx, "INSERT INTO job(kind, title, state, priority, request, created_utc) VALUES($k, $t, 0, $p, $r, $u) RETURNING id",
                                            ("$k", (long)kind), ("$t", title), ("$p", (long)prio), ("$r", JobRequests.Serialize(req)), ("$u", DateTime.UtcNow.Ticks)));

    // ------------------------------------------------------------------------------------------------ snapshots and events

    internal static JobInfo Snap(Job j) => new()
    {
        Id = j.Id, Kind = j.Kind, CaptureId = j.CaptureId, FolderId = j.FolderId, Title = j.Title, Detail = j.Detail, State = j.State, Progress = j.Progress, Status = j.Status,
        Priority = j.Priority, EstimatedMemoryBytes = j.EstMemory, CreatedUtc = j.Created, StartedUtc = j.Started, FinishedUtc = j.Finished, Error = j.Error,
        ResultPath = j.ResultPath, ResultFiles = j.ResultFiles.ToArray(), Notes = j.Notes.ToArray(), ItemsDone = j.ItemsDone, ItemsTotal = j.ItemsTotal, RetryOf = j.RetryOf,
        BatchId = j.Request.BatchId, BatchTitle = j.Request.BatchTitle, PlannedFile = j.PlannedFile, Subject = j.Subject,
    };

    JobInfo Info(Job j)
    {
        lock (j)
        {
            var s = Snap(j);
            if (s.State == JobState.Waiting && (j.Held || _paused)) s = s with { State = JobState.Paused, Status = j.Held ? "Paused" : "Paused (the queue is paused)" };
            return s;
        }
    }

    void Raise(Job j, bool removed = false)
    {
        var info = Info(j);
        try { JobChanged?.Invoke(this, new JobChangedEventArgs { Job = info, Removed = removed }); } catch { }
        try { StatusChanged?.Invoke(this, Status); } catch { }
    }

    internal void Progress(Job j, double p, string? status)
    {
        bool raise;
        lock (j)
        {
            j.Progress = Math.Clamp(p, 0, 1);
            if (status is not null && !(j.Cts?.IsCancellationRequested ?? false)) j.Status = status;
            long now = Environment.TickCount64;
            raise = now - j.LastEventTicks >= 100 || p >= 1;
            if (raise) j.LastEventTicks = now;
        }
        if (raise) Raise(j);
    }

    void RebuildActive()
    {
        _active.Clear();
        foreach (var j in _jobs)
            if (!IsFinished(j.State) && j.CaptureId is { } c && j.Kind != JobKind.Thumbnail && (!_active.TryGetValue(c, out var have) || have.State != JobState.Running && j.State == JobState.Running)) _active[c] = j;
    }

    static bool IsFinished(JobState s) => s is JobState.Done or JobState.Failed or JobState.Cancelled;

    public JobKind? ActiveKindOf(string captureId) { lock (_l) return _active.TryGetValue(captureId, out var k) ? k.Kind : null; }

    /// <summary>The job working on a capture now (running first, else the oldest waiting), for <see cref="CaptureItem.ActiveJob"/>.</summary>
    public (JobKind Kind, long Id, JobState State, double Progress)? ActiveOf(string captureId)
    {
        Job? j; lock (_l) if (!_active.TryGetValue(captureId, out j)) return null;
        lock (j) return (j.Kind, j.Id, j.State == JobState.Waiting && (j.Held || _paused) ? JobState.Paused : j.State, j.Progress);
    }

    /// <summary>The unfinished scan job of a folder.</summary>
    public long? ScanJobOf(long folderId) { lock (_l) return _jobs.FirstOrDefault(j => j.Kind == JobKind.FolderScan && j.FolderId == folderId && !IsFinished(j.State))?.Id; }

    /// <summary>Unfinished jobs of a capture.</summary>
    internal List<JobInfo> JobsOf(string captureId) { List<Job> js; lock (_l) js = _jobs.Where(j => j.CaptureId == captureId && !IsFinished(j.State)).ToList(); return js.Select(Info).ToList(); }

    public bool IsScanning(long folderId) { lock (_l) return _jobs.Any(j => j.Kind == JobKind.FolderScan && j.FolderId == folderId && !IsFinished(j.State)); }

    // ------------------------------------------------------------------------------------------------ IJobQueue

    public IReadOnlyList<JobInfo> GetJobs(bool includeFinished = true)
    {
        List<Job> js; lock (_l) js = _jobs.ToList();
        return js.Where(j => includeFinished || !IsFinished(j.State))
                 .OrderBy(j => IsFinished(j.State) ? 1 : 0).ThenBy(j => j.State == JobState.Running ? 0 : 1)
                 .ThenBy(j => IsFinished(j.State) ? -(j.Finished ?? j.Created).Ticks : j.Priority).ThenBy(j => IsFinished(j.State) ? -j.Id : j.Id)
                 .Select(Info).ToList();
    }

    public JobInfo? Get(long jobId) { Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId); return j is null ? null : Info(j); }

    public long Enqueue(JobRequest request) => Enqueue(request, null);

    internal long Enqueue(JobRequest request, long? retryOf)
    {
        if (request is ThumbnailJobRequest tr) return _lib.Thumbs.AddBatch(tr.CaptureIds) ?? 0;
        var kind = request.Kind;
        string key = JobRequests.Serialize(request);
        lock (_l)
        {
            // the same request already waiting (or running, except folder scans: a change seen during a scan needs another one)
            var same = _jobs.FirstOrDefault(j => j.Kind == kind && !IsFinished(j.State) && (j.State != JobState.Running || kind != JobKind.FolderScan)
                                                 && JobRequests.Serialize(j.Request) == key);
            if (same is not null) return same.Id;
        }
        string title = _lib.TitleOf(request);
        int prio = request.Priority ?? _lib.SettingsImpl.PriorityOf(kind);
        long id = NewId(request, kind, title, prio);
        var job = new Job
        {
            Id = id, Request = request, Kind = kind, CaptureId = JobRequests.CaptureOf(request), FolderId = (request as FolderScanJobRequest)?.FolderId,
            Title = title, State = JobState.Waiting, Status = "Waiting", Priority = prio, EstMemory = _lib.EstimateMemory(request), Created = DateTime.UtcNow, RetryOf = retryOf,
            Detail = LibraryCore.DetailOf(request), PlannedFile = _lib.PlannedFileOf(request), Subject = LibraryCore.SubjectOf(request),
        };
        if (request is CameraImportJobRequest { Files.Count: 0 })
        {
            job.State = JobState.Failed; job.Error = "There is nothing to import: no photos were chosen."; job.Finished = DateTime.UtcNow; job.Status = "Failed";
            job.Done.TrySetResult(Snap(job));
        }
        lock (_l) { _jobs.Add(job); RebuildActive(); }
        Persist(job);
        Raise(job);
        if (job.CaptureId is { } cid) _lib.Changes.Changed(new[] { cid });
        if (kind == JobKind.FolderScan) _lib.RaiseFoldersChanged();
        Signal();
        return id;
    }

    /// <summary>A rescan asked for by the folder watcher.</summary>
    internal void EnqueueQuietScan(long folderId)
    {
        if (_lib.FolderRow(folderId) is null) return;
        Enqueue(new FolderScanJobRequest(folderId));
    }

    /// <summary>The thumbnail batch job (created by the thumbnail service; one at a time).</summary>
    internal long EnqueueThumbnailJob(int priority)
    {
        var req = new ThumbnailJobRequest(Array.Empty<string>()) { Priority = priority };
        long id = NewId(req, JobKind.Thumbnail, "Making thumbnails", priority);
        var job = new Job { Id = id, Request = req, Kind = JobKind.Thumbnail, Title = "Making thumbnails", State = JobState.Waiting, Status = "Waiting", Priority = priority, Created = DateTime.UtcNow, Detail = "Previews for the library" };
        lock (_l) _jobs.Add(job);
        Raise(job);
        Signal();
        return id;
    }

    internal void SetJobPriority(long id, int priority)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == id);
        if (j is null) return;
        lock (j) if (j.Priority == priority) return; else j.Priority = priority;
        Signal();
    }

    public bool Cancel(long jobId)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null) return false;
        bool finishNow = false;
        lock (_l)
        {
            if (IsFinished(j.State)) return false;
            if (j.State == JobState.Running) { j.Interrupted = false; lock (j) j.Status = "Stopping"; j.Cts?.Cancel(); }
            else { j.State = JobState.Cancelled; j.Finished = DateTime.UtcNow; j.Status = "Cancelled"; finishNow = true; RebuildActive(); }
        }
        if (finishNow)
        {
            if (j.Kind == JobKind.Thumbnail) _lib.Thumbs.JobEnded(j.Id, cancelled: true);
            j.Done.TrySetResult(Snap(j)); Persist(j);
            if (j.CaptureId is { } c) _lib.Changes.Changed(new[] { c });
            if (j.Kind == JobKind.FolderScan) _lib.RaiseFoldersChanged();
        }
        Raise(j);
        Signal();
        return true;
    }

    public int CancelAll(JobKind? kind = null)
    {
        List<long> ids; lock (_l) ids = _jobs.Where(j => !IsFinished(j.State) && (kind is null || j.Kind == kind)).Select(j => j.Id).ToList();
        return ids.Count(Cancel);
    }

    internal void CancelFolderScans(long folderId) { List<long> ids; lock (_l) ids = _jobs.Where(j => j.FolderId == folderId && !IsFinished(j.State)).Select(j => j.Id).ToList(); foreach (var i in ids) Cancel(i); }
    internal void CancelForCapture(string id) { List<long> ids; lock (_l) ids = _jobs.Where(j => j.CaptureId == id && !IsFinished(j.State)).Select(j => j.Id).ToList(); foreach (var i in ids) Cancel(i); }

    public long? Retry(long jobId)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null || j.State is not (JobState.Failed or JobState.Cancelled) || j.Kind is JobKind.Thumbnail) return null;
        if (j.Request is CameraImportJobRequest { Files.Count: 0 }) return null;
        return Enqueue(j.Request, j.Id);
    }

    public bool Remove(long jobId)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null) return false;
        if (!IsFinished(j.State)) { j.RemoveWhenDone = true; Cancel(jobId); lock (_l) if (!IsFinished(j.State)) return true; }
        lock (_l) _jobs.Remove(j);
        try { _lib.Db.Write((c, tx) => c.Exec(tx, "DELETE FROM job WHERE id=$i", ("$i", jobId))); } catch (ObjectDisposedException) { }
        Raise(j, removed: true);
        return true;
    }

    public int ClearFinished()
    {
        List<Job> gone; lock (_l) { gone = _jobs.Where(j => IsFinished(j.State)).ToList(); foreach (var g in gone) _jobs.Remove(g); }
        _lib.Db.Write((c, tx) => c.Exec(tx, "DELETE FROM job WHERE state IN (3, 4, 5)"));
        foreach (var g in gone) Raise(g, removed: true);
        return gone.Count;
    }

    public bool Pause(long jobId) => SetHeld(jobId, true);
    public bool Resume(long jobId) => SetHeld(jobId, false);

    bool SetHeld(long jobId, bool held)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null || IsFinished(j.State)) return false;
        lock (j) j.Held = held;
        Persist(j); Raise(j); Signal();
        return true;
    }

    public void PauseAll(bool interruptRunning = false)
    {
        List<Job> running;
        lock (_l)
        {
            _paused = true;
            running = interruptRunning ? _jobs.Where(j => j.State == JobState.Running).ToList() : new();
            foreach (var j in running) { j.Interrupted = true; lock (j) j.Status = "Pausing"; j.Cts?.Cancel(); }
        }
        foreach (var j in GetSnapshotJobs()) Raise(j);
    }

    List<Job> GetSnapshotJobs() { lock (_l) return _jobs.Where(j => !IsFinished(j.State)).ToList(); }

    public void ResumeAll()
    {
        lock (_l) _paused = false;
        foreach (var j in GetSnapshotJobs()) Raise(j);
        Signal();
    }

    public bool IsPaused { get { lock (_l) return _paused; } }

    public bool SetPriority(long jobId, int priority)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null || j.State != JobState.Waiting) return false;
        lock (j) j.Priority = priority;
        Persist(j); Raise(j); Signal();
        return true;
    }

    public async Task<JobInfo> WaitAsync(long jobId, CancellationToken cancel = default)
    {
        Job? j; lock (_l) j = _jobs.FirstOrDefault(x => x.Id == jobId);
        if (j is null)
        {
            // a finished job removed from the list: read its row
            var row = _lib.Db.Read(c => c.Rows(null, "SELECT state, error FROM job WHERE id=$i", r => ((JobState)r.GetInt32(0), r.Str(1)), ("$i", jobId)).FirstOrDefault());
            throw new LibraryException(row.Item1 == 0 && row.Item2 is null ? "There is no such job." : "The job is no longer in the queue.");
        }
        return await j.Done.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    public JobQueueStatus Status
    {
        get
        {
            lock (_l)
                return new JobQueueStatus(_jobs.Count(j => j.State == JobState.Running), _jobs.Count(j => j.State == JobState.Waiting && !j.Held && !_paused),
                                          _jobs.Count(j => j.State == JobState.Waiting && (j.Held || _paused)), _jobs.Count(j => j.State == JobState.Failed), _paused,
                                          Interlocked.Read(ref _reserved), Budget);
        }
    }

    // ------------------------------------------------------------------------------------------------ dispatching

    public void Start(bool paused = false)
    {
        lock (_l) _paused = paused;
        _dispatcher = new Thread(Loop) { IsBackground = true, Name = "lux library jobs" };
        _dispatcher.Start();
    }

    internal void Signal() { try { _wake.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } }

    void Loop()
    {
        while (true)
        {
            _wake.Wait(500);
            lock (_l) if (_shutdown) return;
            try { Dispatch(); } catch (Exception e) { _lib.Trace("dispatcher: " + e); }
        }
    }

    void Dispatch()
    {
        var start = new List<Job>();
        var waitingForMemory = new List<Job>();
        var waitingForUser = new List<Job>();
        if (_estimatesDirty)
        {
            _estimatesDirty = false;
            List<Job> waiting; lock (_l) waiting = _jobs.Where(j => j.State == JobState.Waiting && j.Kind != JobKind.Thumbnail).ToList();
            foreach (var j in waiting) { long e = _lib.EstimateMemory(j.Request); lock (j) j.EstMemory = e; }
        }
        bool holdHeavy = HoldHeavy;
        lock (_l)
        {
            if (_paused || _shutdown) return;
            // moving the local context store runs alone: it waits for running jobs, and nothing else starts before or during it
            bool moveRunning = _jobs.Any(j => j.State == JobState.Running && j.Kind == JobKind.ContextStoreMove);
            bool moveWaiting = _jobs.Any(j => j.State == JobState.Waiting && !j.Held && j.Kind == JobKind.ContextStoreMove);
            long budget = Budget;
            int workers = _lib.SettingsImpl.WorkerCount;
            int running = _jobs.Count(j => j.State == JobState.Running && j.Kind != JobKind.Thumbnail);
            long reserved = Interlocked.Read(ref _reserved);
            bool heavyBlocked = false;
            foreach (var j in _jobs.Where(j => j.State == JobState.Waiting && !j.Held).OrderBy(j => j.Priority).ThenBy(j => j.Id).ToList())
            {
                if (j.Kind != JobKind.Thumbnail && moveRunning) continue;
                if (j.Kind == JobKind.ContextStoreMove && running > 0) continue;
                if (j.Kind != JobKind.Thumbnail && j.Kind != JobKind.ContextStoreMove && moveWaiting) continue;
                if (j.Kind == JobKind.Thumbnail)
                {
                    // sized to what is left (at least one thumbnail at a time)
                    long free = Math.Max(0, budget - reserved);
                    j.EstMemory = _lib.Thumbs.Reserve(free);
                    reserved += j.EstMemory;
                    start.Add(j);
                    continue;
                }
                if (running >= workers) continue;
                bool heavy = j.EstMemory > LightBytes;
                if (heavy && heavyBlocked) continue;
                if (heavy && holdHeavy) { waitingForUser.Add(j); continue; }
                bool anything = reserved > 0 || running > 0;
                bool fits = reserved + j.EstMemory <= budget && (!heavy || !anything || Environment.WorkingSet + j.EstMemory <= budget * 1.05 + reserved);
                if (anything && !fits)
                {
                    if (heavy) heavyBlocked = true;
                    waitingForMemory.Add(j);
                    continue;
                }
                reserved += j.EstMemory;
                running++;
                start.Add(j);
            }
        }
        foreach (var j in waitingForMemory) { bool raise; lock (j) { raise = j.Status != "Waiting for memory"; j.Status = "Waiting for memory"; } if (raise) Raise(j); }
        foreach (var j in waitingForUser) { bool raise; lock (j) { raise = j.Status != "Waiting while you work"; j.Status = "Waiting while you work"; } if (raise) Raise(j); }
        foreach (var j in start) Launch(j);
    }

    void Launch(Job j)
    {
        lock (_l)
        {
            if (j.State != JobState.Waiting) return;
            j.State = JobState.Running; j.Started = DateTime.UtcNow; j.Status = "Starting"; j.Progress = 0; j.Error = null;
            j.Cts = new CancellationTokenSource(); j.Interrupted = false; j.Reserved = j.EstMemory;
            Interlocked.Add(ref _reserved, j.Reserved);
            if (j.Reserved > LightBytes && _heavyRunning++ == 0) _watchdog = MemoryBudget.Watch(Budget);
            RebuildActive();
        }
        Persist(j); Raise(j);
        if (j.CaptureId is { } c) _lib.Changes.Changed(new[] { c });
        var ctx = new JobContext(this, j);
        var t = Task.Run(async () =>
        {
            Exception? err = null; bool cancelled = false;
            try { await _lib.RunJobAsync(j.Request, ctx).ConfigureAwait(false); }
            catch (OperationCanceledException) when (j.Cts!.IsCancellationRequested) { cancelled = true; }
            catch (Exception e) { err = e; }
            Finish(j, err, cancelled);
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
    }

    void Finish(Job j, Exception? err, bool cancelled)
    {
        bool requeued = false;
        lock (_l)
        {
            Interlocked.Add(ref _reserved, -j.Reserved);
            if (j.Reserved > LightBytes && --_heavyRunning == 0) { _watchdog?.Dispose(); _watchdog = null; }
            j.Reserved = 0;
            if (cancelled && (j.Interrupted || _shutdown))
            {
                // paused (or the library closed): back in the queue, to start again from the beginning
                j.State = JobState.Waiting; j.Started = null; j.Progress = 0; j.Status = _shutdown ? "Waiting" : "Paused";
                requeued = true;
            }
            else if (cancelled) { j.State = JobState.Cancelled; j.Status = "Cancelled"; j.Finished = DateTime.UtcNow; }
            else if (err is not null)
            {
                j.State = JobState.Failed; j.Finished = DateTime.UtcNow; j.Status = "Failed";
                j.Error = err is LibraryException or ExportRequestException ? err.Message : LibraryCore.Plain(err);
                _lib.Trace($"job {j.Id} ({j.Title}) failed: {err}");
            }
            else { j.State = JobState.Done; j.Progress = 1; j.Finished = DateTime.UtcNow; if (j.Status is "Starting" or "" || !j.Status.StartsWith("Done")) j.Status = "Done"; }
            j.Cts?.Dispose(); j.Cts = null;
            RebuildActive();
        }
        if (j.Kind == JobKind.Thumbnail && !requeued) _lib.Thumbs.JobEnded(j.Id, cancelled);   // a paused thumbnail job keeps its queue
        if (!requeued) j.Done.TrySetResult(Snap(j));
        if (!_shutdown || requeued) Persist(j);
        Raise(j);
        if (j.CaptureId is { } c) _lib.Changes.Changed(new[] { c });
        if (j.Kind == JobKind.FolderScan) _lib.RaiseFoldersChanged();
        if (j.RemoveWhenDone && !requeued) Remove(j.Id);
        Signal();
    }

    /// <summary>Stop: no new job starts; running ones are interrupted (queued again for the next open) and awaited up to the timeout.</summary>
    public void Shutdown(TimeSpan wait)
    {
        List<Job> running;
        lock (_l)
        {
            _shutdown = true;
            running = _jobs.Where(j => j.State == JobState.Running).ToList();
            foreach (var j in running) { j.Interrupted = true; j.Cts?.Cancel(); }
        }
        Signal();
        Task[] ts; lock (_tasks) ts = _tasks.ToArray();
        try { Task.WaitAll(ts, wait); } catch (AggregateException) { }
        _dispatcher?.Join(TimeSpan.FromSeconds(2));
        _watchdog?.Dispose();
    }
}

/// <summary>(De)serialisation of job requests for the job table.</summary>
internal static class JobRequests
{
    static readonly JsonSerializerOptions Opt = new() { IncludeFields = false, WriteIndented = false };

    public static string? CaptureOf(JobRequest r) => r switch
    {
        AnalysisJobRequest a => a.CaptureId, CacheJobRequest c => c.CaptureId, ExportJobRequest e => e.CaptureId, LumenImportJobRequest l => l.CaptureId,
        AnimationJobRequest an => an.CaptureId, _ => null,
    };

    public static string Serialize(JobRequest r) => r switch
    {
        ThumbnailJobRequest t => JsonSerializer.Serialize(new { t.CaptureIds, t.Priority }, Opt),
        AnalysisJobRequest a => JsonSerializer.Serialize(new { a.CaptureId, a.TargetLevel, a.Priority }, Opt),
        CacheJobRequest c => JsonSerializer.Serialize(new { c.CaptureId, c.Target, c.Release, c.Priority }, Opt),
        ExportJobRequest e => JsonSerializer.Serialize(new { e.CaptureId, e.Options, e.Priority, e.BatchId, e.BatchTitle }, Opt),
        AnimationJobRequest an => JsonSerializer.Serialize(new { an.CaptureId, an.Options, an.Priority, an.BatchId, an.BatchTitle }, Opt),
        LumenImportJobRequest l => JsonSerializer.Serialize(new { l.CaptureId, l.SessionPath, l.NewerToneStyle, l.Priority }, Opt),
        FolderScanJobRequest f => JsonSerializer.Serialize(new { f.FolderId, f.Priority }, Opt),
        CameraImportJobRequest i => JsonSerializer.Serialize(new { i.Destination, i.Priority, i.Files, i.OneFolderPerDay, i.After, i.CameraSerial, i.CameraName }, Opt),
        ContextStoreMoveJobRequest m => JsonSerializer.Serialize(new { m.NewFolder, m.FromFolder, m.Priority }, Opt),
        _ => "{}",
    };

    public static JobRequest? Deserialize(JobKind kind, string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var o = d.RootElement;
            int? prio = o.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
            string S(string n) => o.GetProperty(n).GetString()!;
            string? Opt2(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return kind switch
            {
                JobKind.Thumbnail => new ThumbnailJobRequest(o.GetProperty("CaptureIds").EnumerateArray().Select(x => x.GetString()!).ToList()) { Priority = prio },
                JobKind.Analysis => new AnalysisJobRequest(S("CaptureId"), o.GetProperty("TargetLevel").ValueKind == JsonValueKind.Null ? null : (CacheLevel)o.GetProperty("TargetLevel").GetInt32()) { Priority = prio },
                JobKind.CacheBuild or JobKind.CacheUpgrade or JobKind.CacheRelease =>
                    new CacheJobRequest(S("CaptureId"), (CacheLevel)o.GetProperty("Target").GetInt32()) { Release = o.GetProperty("Release").GetBoolean(), Priority = prio },
                JobKind.Export => new ExportJobRequest(S("CaptureId"), o.GetProperty("Options").Deserialize<ExportOptions>(Opt)!) { Priority = prio, BatchId = Opt2("BatchId"), BatchTitle = Opt2("BatchTitle") },
                JobKind.Animation => new AnimationJobRequest(S("CaptureId"), o.GetProperty("Options").Deserialize<AnimationOptions>(Opt)!) { Priority = prio, BatchId = Opt2("BatchId"), BatchTitle = Opt2("BatchTitle") },
                JobKind.LumenImport => new LumenImportJobRequest(S("CaptureId"), o.GetProperty("SessionPath").GetString())
                    { Priority = prio, NewerToneStyle = o.TryGetProperty("NewerToneStyle", out var nt) && nt.ValueKind == JsonValueKind.True },
                JobKind.FolderScan => new FolderScanJobRequest(o.GetProperty("FolderId").GetInt64()) { Priority = prio },
                JobKind.Import => new CameraImportJobRequest(S("Destination"))
                {
                    Priority = prio,
                    Files = o.TryGetProperty("Files", out var fl) && fl.ValueKind == JsonValueKind.Array ? fl.Deserialize<List<CameraImportFile>>(Opt) ?? new() : new List<CameraImportFile>(),
                    OneFolderPerDay = o.TryGetProperty("OneFolderPerDay", out var pd) && pd.ValueKind == JsonValueKind.True,
                    After = o.TryGetProperty("After", out var af) && af.ValueKind == JsonValueKind.Number ? (CameraAfterImport)af.GetInt32() : CameraAfterImport.Analyse13Mp,
                    CameraSerial = Opt2("CameraSerial"), CameraName = Opt2("CameraName"),
                },
                JobKind.ContextStoreMove => new ContextStoreMoveJobRequest(S("NewFolder")) { FromFolder = o.GetProperty("FromFolder").GetString(), Priority = prio },
                _ => null,
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
