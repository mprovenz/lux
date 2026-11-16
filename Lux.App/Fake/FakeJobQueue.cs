using System.Threading.Channels;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.Fake;

/// <summary>Raises events in order on one background thread, never on the caller's (the contract's "library threads").</summary>
internal sealed class FakeEventPump : IDisposable
{
    readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _reader;

    public FakeEventPump()
    {
        _reader = Task.Factory.StartNew(async () =>
        {
            await foreach (var a in _channel.Reader.ReadAllAsync())
            {
                try { a(); } catch (Exception) { /* a subscriber's failure must not stop the pump */ }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public void Post(Action a) => _channel.Writer.TryWrite(a);

    /// <summary>Wait until everything posted so far has been delivered (tests).</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult());
        return tcs.Task;
    }

    public void Dispose() => _channel.Writer.TryComplete();
}

/// <summary>
/// A job queue whose jobs tick: priorities, a worker limit and a memory budget as the real scheduler has them, with plain-language
/// status texts and a deterministic failure or two. <see cref="Tick"/> advances it (a timer calls it unless the options say otherwise).
/// </summary>
public sealed class FakeJobQueue : IJobQueue, IDisposable
{
    /// <summary>Tests: when this rule gives a message for a job, the job fails with that message at about 40 %, as a real job can fail.</summary>
    public Func<JobRequest, string?>? FailRule { get; set; }

    sealed class Job
    {
        public required JobInfo Info;
        public JobRequest? Request;
        public double Seconds;                 // total simulated duration
        public string? FailWith;               // fail at about 40 %
        public TaskCompletionSource<JobInfo> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Run by real code (a camera import through the registered importer), not by the ticks.</summary>
        public bool External;
        public CancellationTokenSource? Cts;
        public long LastEvent;
    }

    readonly FakeLibrary _lib;
    readonly FakeEventPump _pump;
    readonly object _gate = new();
    readonly List<Job> _jobs = new();
    readonly Timer? _timer;
    long _nextId = 1;
    bool _paused;

    internal FakeJobQueue(FakeLibrary lib, FakeEventPump pump, bool autoTick)
    {
        _lib = lib;
        _pump = pump;
        if (autoTick) _timer = new Timer(_ => Tick(0.1), null, 100, 100);
    }

    public event EventHandler<JobChangedEventArgs>? JobChanged;
    public event EventHandler<JobQueueStatus>? StatusChanged;

    DateTime Now => _lib.Now;

    public IReadOnlyList<JobInfo> GetJobs(bool includeFinished = true)
    {
        lock (_gate)
        {
            var open = _jobs.Where(j => !j.Info.IsFinished).OrderBy(j => j.Info.State == JobState.Running ? 0 : 1).ThenBy(j => j.Info.Priority).ThenBy(j => j.Info.Id);
            if (!includeFinished) return open.Select(j => j.Info).ToList();
            return open.Concat(_jobs.Where(j => j.Info.IsFinished).OrderByDescending(j => j.Info.FinishedUtc).ThenByDescending(j => j.Info.Id))
                       .Select(j => j.Info).ToList();
        }
    }

    public JobInfo? Get(long jobId) { lock (_gate) return _jobs.FirstOrDefault(j => j.Info.Id == jobId)?.Info; }

    public long Enqueue(JobRequest request)
    {
        Job job;
        lock (_gate)
        {
            string? capture = CaptureOf(request);
            var same = _jobs.FirstOrDefault(j => !j.Info.IsFinished && j.Info.Kind == request.Kind && j.Info.CaptureId == capture
                                                 && Equals(j.Request, request));
            if (same != null) return same.Info.Id;
            job = Create(request, capture);
            _jobs.Add(job);
        }
        _lib.OnJobQueued(job.Info);
        Changed(job.Info);
        return job.Info.Id;
    }

    static string? CaptureOf(JobRequest r) => r switch
    {
        AnalysisJobRequest a => a.CaptureId,
        CacheJobRequest c => c.CaptureId,
        ExportJobRequest e => e.CaptureId,
        LumenImportJobRequest l => l.CaptureId,
        AnimationJobRequest an => an.CaptureId,
        _ => null,
    };

    Job Create(JobRequest r, string? captureId)
    {
        var cap = captureId != null ? _lib.Get(captureId) : null;
        string name = cap?.Name ?? "";
        var kind = r is CacheJobRequest { Release: true } ? JobKind.CacheRelease : r.Kind;
        (string title, double secs, long mem) = r switch
        {
            AnalysisJobRequest a => (a.TargetLevel >= CacheLevel.Linear13 ? $"Analyzing {name}: depth map and 13 MP" : $"Analyzing {name}", 9.0, 5L << 30),
            CacheJobRequest { Release: true } => ($"Releasing full detail of {name}", 0.6, 64L << 20),
            CacheJobRequest c when c.Target == CacheLevel.Full => ($"Keeping full detail for {name}" + (cap is { CacheLevel: < CacheLevel.Geometry } ? " (depth map first)" : ""), 7.0, 6L << 30),
            CacheJobRequest => ($"Building the cache of {name}", 4.0, 3L << 30),
            ExportJobRequest e => ($"Exporting {name} as {FormatList(e.Options)}", e.Options.Level == 0 ? 6.0 : 2.0, e.Options.Level == 0 ? 6L << 30 : 2L << 30),
            AnimationJobRequest an => ($"Animating {name} as {an.Options.Format.ToString().ToUpperInvariant()}", 4.0, 5L << 30),
            LumenImportJobRequest => ($"Importing the Lumen edits of {name}", 2.0, 1L << 30),
            FolderScanJobRequest f => ($"Scanning {Path.GetFileName(_lib.FolderPath(f.FolderId))}", 2.0, 128L << 20),
            ThumbnailJobRequest t => ($"Making {t.CaptureIds.Count} thumbnails", 0.04 * t.CaptureIds.Count, 256L << 20),
            CameraImportJobRequest ci => (CameraImportText.Title(ci), 5.0, 256L << 20),
            ContextStoreMoveJobRequest mv => ($"Moving photo data to {mv.NewFolder}", 4.0, 256L << 20),
            _ => ("Working", 1.0, 0L),
        };
        var info = new JobInfo
        {
            Id = _nextId++,
            Kind = kind,
            CaptureId = captureId,
            FolderId = (r as FolderScanJobRequest)?.FolderId,
            Title = title,
            State = JobState.Waiting,
            Status = "Waiting",
            Priority = r.Priority ?? _lib.Settings.PriorityOf(kind),
            EstimatedMemoryBytes = mem,
            CreatedUtc = Now,
            ItemsTotal = r is ThumbnailJobRequest tr ? tr.CaptureIds.Count : null,
            Detail = r switch
            {
                ExportJobRequest ex => $"{FormatList(ex.Options)} · {(ex.Options.Level == 0 ? "full size" : ex.Options.Level == 1 ? "13 MP" : "smaller")}"
                                       + (ex.Options.DepthEffectOff ? " · all in focus" : ex.Options.DepthEffectFNumber is float fn ? $" · f/{fn:0.0}" : ""),
                AnalysisJobRequest => "Analysis · depth and the 13 MP working copy",
                CacheJobRequest { Release: true } => "Release full detail",
                CacheJobRequest c2 when c2.Target == CacheLevel.Full => "Upgrade to full detail",
                LumenImportJobRequest => "Import edits from Lumen",
                AnimationJobRequest an2 => $"{AnimationEffects.NameOf(an2.Options.Effect)} · {an2.Options.LongEdge} px · {an2.Options.Format.ToString().ToUpperInvariant()}",
                CameraImportJobRequest ci2 => CameraImportText.Detail(ci2),
                _ => "",
            },
            Subject = r is CameraImportJobRequest ci3 ? CameraImportText.Subject(ci3) : null,
            ItemsDone = r is ThumbnailJobRequest ? 0 : null,
            BatchId = r.BatchId, BatchTitle = r.BatchTitle,
            PlannedFile = r switch
            {
                ExportJobRequest pe when cap != null => Path.Combine(pe.Options.OutputFolder ?? "/home/you/Pictures/Lux Export", cap.Name + (pe.Options.Formats.FirstOrDefault() == "jpg" ? ".jpg" : ".dng")),
                AnimationJobRequest pa when cap != null => Path.Combine(pa.Options.OutputFolder ?? "/home/you/Pictures/Lux Export", $"{cap.Name}_{AnimationEffects.Key(pa.Options.Effect)}{AnimationOptions.Extension(pa.Options.Format)}"),
                _ => null,
            },
        };
        string? fail = null;
        if (r is ContextStoreMoveJobRequest m2 && m2.NewFolder.Contains("full-disk", StringComparison.Ordinal))
            fail = "Not enough free space in the new folder. Lux keeps using the old one.";
        if (cap is { Missing: true }) fail = "The capture file could not be found. Reconnect its drive or rescan the folder.";
        else if (cap is { Unreadable: true }) fail = cap.Error ?? "The capture could not be read.";
        else if (r is ExportJobRequest ex && (ex.Options.OutputFolder ?? "").Contains("full-disk", StringComparison.Ordinal))
            fail = "Not enough free space in the folder.";
        if (r is CameraImportJobRequest { Files.Count: 0 }) fail = "There is nothing to import: no photos were chosen.";
        fail ??= FailRule?.Invoke(r);
        return new Job { Info = info, Request = r, Seconds = secs, FailWith = fail, External = r is CameraImportJobRequest { Files.Count: > 0 } };
    }

    internal static string FormatList(ExportOptions o)
    {
        static string Name(string f) => f switch { "dng" => "DNG", "jpg" => "JPG", "jpg+depth" => "JPG + depth", "hdr" => "HDR", "ppm" => "PPM", "depth" => "depth map", _ => f };
        return string.Join(" + ", o.Formats.Select(Name));
    }

    /// <summary>Seed a finished, failed or running job directly (screenshots).</summary>
    internal long Seed(JobRequest r, JobState state, double progress, string status, DateTime? finished = null, string? error = null,
                       string? title = null, IReadOnlyList<string>? notes = null, string? result = null)
    {
        lock (_gate)
        {
            string? capture = CaptureOf(r);
            var job = Create(r, capture);
            job.Info = job.Info with
            {
                State = state, Progress = progress, Status = status, Error = error, Title = title ?? job.Info.Title,
                StartedUtc = state == JobState.Waiting ? null : (finished ?? Now).AddSeconds(-job.Seconds * Math.Max(progress, 0.1)),
                FinishedUtc = finished, Notes = notes ?? Array.Empty<string>(), ResultPath = result,
                ResultFiles = result != null ? new[] { result } : Array.Empty<string>(),
                CreatedUtc = (finished ?? Now).AddMinutes(-2),
            };
            if (state == JobState.Done || state == JobState.Failed) job.Done.TrySetResult(job.Info);
            _jobs.Add(job);
            if (!job.Info.IsFinished) _lib.OnJobQueued(job.Info);
            return job.Info.Id;
        }
    }

    public bool Cancel(long jobId)
    {
        Job? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
            if (job == null || job.Info.IsFinished) return false;
            if (job.External && job.Info.State == JobState.Running && job.Cts != null)
            {
                // the import stops at its next check and removes its partial file; the job ends when it has
                job.Info = job.Info with { Status = "Stopping" };
                job.Cts.Cancel();
                var info = job.Info;
                _pump.Post(() => JobChanged?.Invoke(this, new JobChangedEventArgs { Job = info }));
                return true;
            }
            job.Info = job.Info with { State = JobState.Cancelled, Status = "Cancelled", FinishedUtc = Now };
        }
        Finish(job);
        return true;
    }

    public int CancelAll(JobKind? kind = null)
    {
        List<long> ids;
        lock (_gate) ids = _jobs.Where(j => !j.Info.IsFinished && (kind == null || j.Info.Kind == kind)).Select(j => j.Info.Id).ToList();
        return ids.Count(Cancel);
    }

    public long? Retry(long jobId)
    {
        Job? old;
        lock (_gate) old = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
        if (old?.Request == null || old.Info.State is not (JobState.Failed or JobState.Cancelled)) return null;
        Job job;
        lock (_gate)
        {
            job = Create(old.Request, old.Info.CaptureId);
            job.Info = job.Info with { RetryOf = jobId };
            _jobs.Add(job);
        }
        _lib.OnJobQueued(job.Info);
        Changed(job.Info);
        return job.Info.Id;
    }

    public bool Remove(long jobId)
    {
        Job? job;
        lock (_gate) job = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
        if (job == null) return false;
        if (!job.Info.IsFinished) Cancel(jobId);
        lock (_gate) _jobs.Remove(job);
        var info = job.Info;
        _pump.Post(() => JobChanged?.Invoke(this, new JobChangedEventArgs { Job = info, Removed = true }));
        PostStatus();
        return true;
    }

    public int ClearFinished()
    {
        List<Job> gone;
        lock (_gate)
        {
            gone = _jobs.Where(j => j.Info.IsFinished).ToList();
            foreach (var j in gone) _jobs.Remove(j);
        }
        foreach (var j in gone)
        {
            var info = j.Info;
            _pump.Post(() => JobChanged?.Invoke(this, new JobChangedEventArgs { Job = info, Removed = true }));
        }
        PostStatus();
        return gone.Count;
    }

    public bool Pause(long jobId) => SetState(jobId, JobState.Waiting, JobState.Paused, "Paused");
    public bool Resume(long jobId) => SetState(jobId, JobState.Paused, JobState.Waiting, "Waiting");

    bool SetState(long id, JobState from, JobState to, string status)
    {
        JobInfo info;
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Info.Id == id);
            if (job == null || job.Info.State != from) return false;
            job.Info = job.Info with { State = to, Status = status };
            info = job.Info;
        }
        Changed(info);
        return true;
    }

    public void PauseAll(bool interruptRunning = false)
    {
        lock (_gate)
        {
            _paused = true;
            if (interruptRunning)
                foreach (var j in _jobs.Where(j => j.Info.State == JobState.Running))
                    j.Info = j.Info with { State = JobState.Waiting, Progress = 0, Status = "Waiting" };
        }
        PostStatus();
    }

    public void ResumeAll()
    {
        lock (_gate) _paused = false;
        PostStatus();
    }

    public bool IsPaused { get { lock (_gate) return _paused; } }

    bool _interactive;
    /// <summary>Calls of <see cref="SetInteractive"/> (tests).</summary>
    public List<bool> InteractiveCalls { get; } = new();
    public bool Interactive { get { lock (_gate) return _interactive; } }
    public void SetInteractive(bool interacting) { lock (_gate) { _interactive = interacting; InteractiveCalls.Add(interacting); } }

    static bool IsHeavy(JobInfo j) => j.Kind is JobKind.Analysis or JobKind.CacheUpgrade || (j.Kind == JobKind.Export && j.EstimatedMemoryBytes >= 4L << 30);

    public bool SetPriority(long jobId, int priority)
    {
        JobInfo info;
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
            if (job == null || job.Info.State is not (JobState.Waiting or JobState.Paused)) return false;
            job.Info = job.Info with { Priority = priority };
            info = job.Info;
        }
        Changed(info);
        return true;
    }

    public Task<JobInfo> WaitAsync(long jobId, CancellationToken cancel = default)
    {
        Job? job;
        lock (_gate) job = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
        if (job == null) return Task.FromException<JobInfo>(new LibraryException("No such job."));
        return job.Done.Task.WaitAsync(cancel);
    }

    public JobQueueStatus Status
    {
        get
        {
            lock (_gate)
            {
                var running = _jobs.Where(j => j.Info.State == JobState.Running).ToList();
                return new JobQueueStatus(running.Count, _jobs.Count(j => j.Info.State == JobState.Waiting), _jobs.Count(j => j.Info.State == JobState.Paused),
                    _jobs.Count(j => j.Info.State == JobState.Failed), _paused, running.Sum(j => j.Info.EstimatedMemoryBytes),
                    (long)(_lib.Settings.MemoryBudgetGb * (1L << 30)));
            }
        }
    }

    /// <summary>Advance every running job by <paramref name="seconds"/> and start waiting ones that fit.</summary>
    public void Tick(double seconds = 0.1)
    {
        var changed = new List<JobInfo>();
        var finished = new List<Job>();
        var launch = new List<Job>();
        lock (_gate)
        {
            // advance
            foreach (var j in _jobs.Where(j => j.Info.State == JobState.Running && !j.External))
            {
                double p = Math.Min(1, j.Info.Progress + seconds / Math.Max(0.05, j.Seconds));
                if (j.FailWith != null && p >= 0.4)
                {
                    j.Info = j.Info with { State = JobState.Failed, Progress = p, Error = j.FailWith, Status = "Stopped: " + j.FailWith, FinishedUtc = Now };
                    finished.Add(j);
                    continue;
                }
                if (p >= 1)
                {
                    j.Info = j.Info with
                    {
                        State = JobState.Done, Progress = 1, FinishedUtc = Now, Status = "Finished",
                        ItemsDone = j.Info.ItemsTotal,
                        ResultPath = j.Request is ExportJobRequest e ? ExportPath(j, e) : j.Info.PlannedFile,
                        ResultFiles = j.Request is ExportJobRequest e2 ? e2.Options.Formats.Select(f => ExportPath(j, e2, f)).ToList()
                                    : j.Info.PlannedFile is { } pf ? new[] { pf } : Array.Empty<string>(),
                        Notes = j.Request is ExportJobRequest { Options.UseSavedEdits: true } && _lib.Get(j.Info.CaptureId!)?.Edits.IsEdited == true
                            ? new[] { "saved depth edits used" } : Array.Empty<string>(),
                    };
                    finished.Add(j);
                    continue;
                }
                j.Info = j.Info with
                {
                    Progress = p,
                    Status = StatusText(j, p),
                    ItemsDone = j.Info.ItemsTotal is int t ? (int)(t * p) : null,
                };
                changed.Add(j.Info);
            }
            // start
            if (!_paused)
            {
                long budget = (long)(_lib.Settings.MemoryBudgetGb * (1L << 30));
                int limit = _lib.Settings.WorkerCount;
                foreach (var j in _jobs.Where(j => j.Info.State == JobState.Waiting).OrderBy(j => j.Info.Priority).ThenBy(j => j.Info.Id).ToList())
                {
                    var running = _jobs.Where(x => x.Info.State == JobState.Running).ToList();
                    if (running.Count >= limit) break;
                    long reserved = running.Sum(x => x.Info.EstimatedMemoryBytes);
                    if (_interactive && _lib.Settings.PauseHeavyJobsWhileInteracting && IsHeavy(j.Info))
                    {
                        if (j.Info.Status != "Waiting while you work") { j.Info = j.Info with { Status = "Waiting while you work" }; changed.Add(j.Info); }
                        continue;
                    }
                    if (running.Count > 0 && reserved + j.Info.EstimatedMemoryBytes > budget)
                    {
                        if (j.Info.Status != "Waiting for memory") { j.Info = j.Info with { Status = "Waiting for memory" }; changed.Add(j.Info); }
                        continue;
                    }
                    j.Info = j.Info with { State = JobState.Running, StartedUtc = Now, Status = j.External ? "Starting" : StatusText(j, 0) };
                    changed.Add(j.Info);
                    if (j.External) launch.Add(j);
                }
            }
        }
        foreach (var j in finished) Finish(j);
        foreach (var info in changed) Changed(info, postStatus: false);
        if (changed.Count > 0 || finished.Count > 0) PostStatus();
        foreach (var j in launch) Launch(j);
    }

    /// <summary>Run an external job (a camera import) on a worker through the library's registered importer.</summary>
    void Launch(Job j)
    {
        var importer = _lib.CameraImporter;
        var req = (CameraImportJobRequest)j.Request!;
        j.Cts = new CancellationTokenSource();
        var reporter = new Reporter(this, j);
        _ = Task.Run(async () =>
        {
            Exception? err = null; bool cancelled = false;
            try
            {
                if (importer == null) throw new LibraryException("The camera is not available: connect it and import again from the Camera page.");
                await importer.RunAsync(req, reporter).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (j.Cts.IsCancellationRequested) { cancelled = true; }
            catch (Exception e) { err = e; }
            lock (_gate)
            {
                j.Info = cancelled ? j.Info with { State = JobState.Cancelled, Status = "Cancelled", FinishedUtc = Now }
                       : err != null ? j.Info with { State = JobState.Failed, Status = "Failed", Error = err is LibraryException ? err.Message : err.Message, FinishedUtc = Now }
                       : j.Info with { State = JobState.Done, Progress = 1, Status = j.Info.Status.StartsWith("Done", StringComparison.Ordinal) ? j.Info.Status : "Done", FinishedUtc = Now };
            }
            Finish(j);
            PostStatus();
        });
    }

    /// <summary>The progress of an external job (as the real queue's job context: progress events at most ten a second).</summary>
    sealed class Reporter(FakeJobQueue q, Job j) : IJobReporter
    {
        public CancellationToken Cancel => j.Cts!.Token;
        public long JobId => j.Info.Id;
        public void Report(double progress, string? status = null)
        {
            JobInfo info; bool raise;
            lock (q._gate)
            {
                j.Info = j.Info with { Progress = Math.Clamp(progress, 0, 1), Status = status != null && !j.Cts!.IsCancellationRequested ? status : j.Info.Status };
                long now = Environment.TickCount64;
                raise = now - j.LastEvent >= 100 || progress >= 1;
                if (raise) j.LastEvent = now;
                info = j.Info;
            }
            if (raise) q.Changed(info);
        }
        public void Items(int done, int total) { lock (q._gate) j.Info = j.Info with { ItemsDone = done, ItemsTotal = total }; }
        public void Note(string note) { lock (q._gate) j.Info = j.Info with { Notes = j.Info.Notes.Append(note).ToList() }; }
        public void Result(string? path, IEnumerable<string>? files = null) { lock (q._gate) j.Info = j.Info with { ResultPath = path, ResultFiles = files?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>() }; }
    }

    string ExportPath(Job j, ExportJobRequest e, string? format = null)
    {
        var cap = _lib.Get(e.CaptureId);
        string ext = (format ?? e.Options.Formats.FirstOrDefault() ?? "jpg") switch { "dng" => ".dng", "hdr" => ".hdr", "ppm" => ".ppm", "depth" => "_depth.png", "jpg+depth" => "_depth.jpg", _ => ".jpg" };
        return Path.Combine(e.Options.OutputFolder ?? "/home/you/Pictures/Lux Export", (cap?.Name ?? "capture") + ext);
    }

    static string StatusText(Job j, double p)
    {
        int left = (int)Math.Ceiling(j.Seconds * (1 - p) * 4);   // the fake runs about four times faster than a real job
        string eta = left > 1 ? $" · about {left} s left" : "";
        return j.Info.Kind switch
        {
            JobKind.Analysis => (p < 0.15 ? "Reading the modules" : p < 0.55 ? "Lining up the modules" : p < 0.85 ? "Computing depth" : "Building the 13 MP working copy") + eta,
            JobKind.CacheUpgrade => (p < 0.8 ? "Rendering full detail" : "Writing the cache") + eta,
            JobKind.CacheBuild => "Building the working copy" + eta,
            JobKind.CacheRelease => "Releasing full detail",
            JobKind.Export => (p < 0.7 ? (j.Request is ExportJobRequest { Options.Level: 0 } ? "Rendering full detail" : "Rendering") : "Writing files") + eta,
            JobKind.LumenImport => p < 0.5 ? "Reading the Lumen session" : "Storing the edits",
            JobKind.FolderScan => $"Looking for captures · {(int)(p * 146)} found",
            JobKind.Thumbnail => $"Making thumbnails · {(int)((j.Info.ItemsTotal ?? 0) * p)} of {j.Info.ItemsTotal}",
            JobKind.ContextStoreMove => $"Copying and checking · {(int)(p * 12)} of 12 files",
            _ => "Working",
        };
    }

    void Finish(Job j)
    {
        // the job's effects first (a folder scan's captures, an analysis's cache level), then the waiters: as in the real library, whoever
        // waits for a job sees what it did
        _lib.OnJobFinished(j.Info, j.Request);
        j.Done.TrySetResult(j.Info);
        Changed(j.Info);
    }

    /// <summary>The unfinished jobs of a capture.</summary>
    internal IReadOnlyList<JobInfo> JobsOf(string captureId)
    {
        lock (_gate) return _jobs.Where(j => !j.Info.IsFinished && j.Info.CaptureId == captureId).Select(j => j.Info).ToList();
    }

    void Changed(JobInfo info, bool postStatus = true)
    {
        _lib.OnJobState(info);
        _pump.Post(() => JobChanged?.Invoke(this, new JobChangedEventArgs { Job = info }));
        if (postStatus) PostStatus();
    }

    void PostStatus()
    {
        var s = Status;
        _pump.Post(() => StatusChanged?.Invoke(this, s));
    }

    public void Dispose() => _timer?.Dispose();
}
