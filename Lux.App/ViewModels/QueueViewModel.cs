using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>A group heading in the Queue list ("Running · 1").</summary>
public sealed class QueueGroupViewModel(string title, int count)
{
    public string Title { get; } = title;
    public int Count { get; } = count;
    public string Label => $"{Title} · {Count}";
}

/// <summary>A batch in the Queue (an export or animation of a selection): one heading with the files done, the progress of the whole, and
/// actions for all its jobs; its jobs follow as indented rows named after their files.</summary>
public sealed partial class QueueBatchViewModel : ObservableObject
{
    readonly QueueViewModel _owner;
    public QueueBatchViewModel(QueueViewModel owner, string id, string title, IReadOnlyList<JobViewModel> jobs) { _owner = owner; Id = id; Title = title; Jobs = jobs; }
    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<JobViewModel> Jobs { get; }
    public int Done => Jobs.Count(j => j.IsDone);
    public int Failed => Jobs.Count(j => j.IsFailed);
    public int Unfinished => Jobs.Count(j => !j.IsFinished);
    public double Progress => Jobs.Count == 0 ? 0 : Jobs.Average(j => j.IsFinished ? 1.0 : j.Progress);
    public bool IsRunning => Jobs.Any(j => j.IsRunning);
    public bool IsFinished => Unfinished == 0;
    public bool IsAllDone => Jobs.All(j => j.IsDone);
    public bool HasFailed => Failed > 0;
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Done} of {Jobs.Count} done" };
            if (Jobs.Count(j => j.IsRunning) is int r and > 0) parts.Add($"{r} running");
            if (Failed > 0) parts.Add($"{Failed} failed");
            if (Jobs.Count(j => j.IsCancelled) is int c and > 0) parts.Add($"{c} cancelled");
            if (!IsFinished) parts.Add(Text.Percent(Progress));
            return string.Join(" · ", parts);
        }
    }
    public string PrimaryAction => Unfinished > 0 ? "Cancel all" : HasFailed ? "Retry failed" : "Remove";
    [RelayCommand]
    void Primary()
    {
        switch (PrimaryAction)
        {
            case "Cancel all": foreach (var j in Jobs.Where(j => !j.IsFinished)) _owner.Cancel(j); break;
            case "Retry failed": foreach (var j in Jobs.Where(j => j.IsFailed || j.IsCancelled)) _owner.Retry(j); break;
            default: foreach (var j in Jobs) _owner.Remove(j); break;
        }
    }
    public bool HasSecondaryAction => IsFinished && HasFailed;
    [RelayCommand] void Secondary() { foreach (var j in Jobs) _owner.Remove(j); }
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One activity: background work that is not a Queue job (an open photo's depth reading, a version switch, the history rebuild, an
/// animation preview, data moved out of the local store …), named after its photo, with progress and a stop where one is possible.</summary>
public sealed partial class ActivityRowViewModel(QueueViewModel owner, ActivityInfo info, string name, int orientation) : ObservableObject
{
    public ActivityInfo Info { get; private set; } = info;
    public string Name { get; } = name;
    public int Orientation { get; } = orientation;
    public string? CaptureId => Info.CaptureId;
    public bool HasCapture => Info.CaptureId != null;
    public string Title => Info.Title;
    public string StatusText => Info.Progress is double p ? $"{Info.Status} · {Text.Percent(p)}" : Info.Status;
    public double Progress => Info.Progress ?? 0;
    public bool IsIndeterminate => Info.Progress is null;
    public bool CanCancel => Info.CanCancel;
    public void Update(ActivityInfo next) { Info = next; OnPropertyChanged(string.Empty); }
    [RelayCommand] void Stop() => owner.StopActivity(this);
}

/// <summary>One background job.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    readonly QueueViewModel _owner;
    JobInfo _info;

    public JobViewModel(QueueViewModel owner, JobInfo info, string name, string? description, int orientation = 0)
    {
        _owner = owner;
        _info = info;
        Name = name;
        Description = description;
        Orientation = orientation;
    }

    /// <summary>Clockwise rotation of the capture's thumbnail.</summary>
    public int Orientation { get; }

    public JobInfo Info => _info;
    public long Id => _info.Id;
    public string? CaptureId => _info.CaptureId;
    public string Name { get; }
    public string? Description { get; }
    public bool HasCapture => _info.CaptureId != null;
    public string KindInitial => Text.JobKindLabel(_info.Kind)[..1];

    public void Update(JobInfo info)
    {
        _info = info;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>"DNG + JPG · full size · f/2.0" — from the export panel when it queued the job, else the job's own title.</summary>
    public string What
    {
        get
        {
            if (Description != null) return Description;
            if (_info.Detail is { Length: > 0 } detail) return detail;          // the library's own short line
            string t = _info.Title;
            switch (_info.Kind)
            {
                case JobKind.Export when t.IndexOf(" as ", StringComparison.Ordinal) is int i and >= 0: return t[(i + 4)..];
                case JobKind.Analysis: return "Analysis · depth and the 13 MP working copy";
                case JobKind.CacheUpgrade: return "Upgrade to full detail";
                case JobKind.CacheRelease: return "Release full detail";
                case JobKind.CacheBuild: return "Working copy";
                case JobKind.LumenImport: return "Import edits from Lumen";
                case JobKind.ContextStoreMove: return t;
                case JobKind.Animation when t.IndexOf(" as ", StringComparison.Ordinal) is int ia and >= 0: return t[(ia + 4)..];
            }
            if (Name.Length > 0) t = t.Replace(" " + Name, "", StringComparison.Ordinal).Replace(Name + " ", "", StringComparison.Ordinal);
            return t;
        }
    }

    /// <summary>The job belongs to a batch shown as one group (its row is indented under the group's heading).</summary>
    public bool InBatch => _info.BatchId != null;

    /// <summary>The files it writes or wrote ("→ L16_00493.dng, L16_00493.jpg"): the result once finished, else the planned name.</summary>
    public string FileLine
    {
        get
        {
            var files = _info.ResultFiles.Count > 0 ? _info.ResultFiles : _info.PlannedFile is { } p ? new[] { p } : Array.Empty<string>();
            if (files.Count == 0) return "";
            var names = files.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToList();
            string shown = names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" +{names.Count - 3}";
            return "→ " + shown;
        }
    }
    public bool HasFileLine => FileLine.Length > 0;

    public string KindLabel => Text.JobKindLabel(_info.Kind);
    public double Progress => _info.Progress;
    public bool IsRunning => _info.State == JobState.Running;
    public bool IsWaiting => _info.State is JobState.Waiting or JobState.Paused;
    public bool IsPaused => _info.State == JobState.Paused;
    public bool IsDone => _info.State == JobState.Done;
    public bool IsFailed => _info.State == JobState.Failed;
    /// <summary>The reason of a failed job wraps, because it says what to do and must show in full.</summary>
    public Avalonia.Media.TextWrapping StatusWrapping => IsFailed ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
    public bool IsCancelled => _info.State == JobState.Cancelled;
    public bool IsFinished => _info.IsFinished;

    public string StatusText
    {
        get
        {
            var i = _info;
            switch (i.State)
            {
                case JobState.Running:
                    string items = i.ItemsTotal is int t && i.Kind != JobKind.Thumbnail ? $" · {i.ItemsDone ?? 0} of {t}" : "";
                    return $"{(i.Status.Length > 0 ? i.Status : "Working")}{items} · {Text.Percent(i.Progress)}";
                case JobState.Waiting: return i.Status is { Length: > 0 } s && s != "Waiting" ? s : "Waiting";
                case JobState.Paused: return "Paused";
                case JobState.Done:
                {
                    var parts = new List<string> { "Finished " + (i.FinishedUtc is { } f ? Text.Time(f) : "") };
                    if (i.StartedUtc is { } st && i.FinishedUtc is { } fi) parts.Add(Text.Duration(fi - st));
                    parts.AddRange(i.Notes);
                    if (_owner.ResultSize(i) is long bytes && bytes > 0) parts.Add(Text.Mb(bytes));
                    return string.Join(" · ", parts);
                }
                case JobState.Failed: return "Stopped: " + LowerFirst(i.Error ?? "the job failed");
                case JobState.Cancelled: return "Cancelled" + (i.FinishedUtc is { } c ? " " + Text.Time(c) : "");
                default: return i.Status;
            }
        }
    }

    static string LowerFirst(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) && !StartsWithName(s) ? char.ToLower(s[0], CultureInfo.InvariantCulture) + s[1..].TrimEnd('.') : s.TrimEnd('.');

    /// <summary>Names such as Lux and Lumen keep their capital letter after "Stopped: ".</summary>
    static bool StartsWithName(string s) => s.StartsWith("Lux", StringComparison.Ordinal) || s.StartsWith("Lumen", StringComparison.Ordinal) || s.StartsWith("Light", StringComparison.Ordinal)
                                            || s.StartsWith("Windows", StringComparison.Ordinal) || s.StartsWith("L16", StringComparison.Ordinal);

    /// <summary>The main action: Cancel (running), Remove (waiting), Resume (paused), Show file (done), Retry (failed / cancelled).</summary>
    public string PrimaryAction => _info.State switch
    {
        JobState.Running => "Cancel",
        JobState.Waiting => "Remove",
        JobState.Paused => "Resume",
        JobState.Done => _info.ResultPath != null ? "Show file" : "Remove",
        _ => "Retry",
    };

    /// <summary>A second action where one makes sense (Remove beside Show file / Retry / Resume).</summary>
    public bool HasSecondaryAction => _info.State is JobState.Paused or JobState.Failed or JobState.Cancelled || (_info.State == JobState.Done && _info.ResultPath != null);

    [RelayCommand]
    void Primary()
    {
        switch (PrimaryAction)
        {
            case "Cancel": _owner.Cancel(this); break;
            case "Remove": _owner.Remove(this); break;
            case "Resume": _owner.ResumeJob(this); break;
            case "Show file": _owner.ShowFile(this); break;
            case "Retry": _owner.Retry(this); break;
        }
    }

    [RelayCommand] void Secondary() => _owner.Remove(this);
}

/// <summary>The Queue page: every background job grouped running / waiting / finished / failed, with the tab's count.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly IJobQueue _q;
    readonly Dictionary<long, JobViewModel> _jobs = new();
    readonly Dictionary<long, string> _descriptions = new();
    readonly Dictionary<string, (JobState State, JobKind Kind, double Progress, long Id)> _active = new(StringComparer.Ordinal);
    readonly Dictionary<long, long> _resultSizes = new();
    JobQueueStatus _status;

    public QueueViewModel(AppServices s, LibraryEvents events)
    {
        _s = s;
        _q = s.Library.Jobs;
        _status = _q.Status;
        events.JobsChanged += OnJobsChanged;
        events.QueueStatusChanged += () => { _status = _q.Status; RaiseSummary(); };
        s.Library.ActivitiesChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshActivities);
    }

    // ---- activities: background work that is not a Queue job, listed first ("Working now")
    readonly Dictionary<long, ActivityRowViewModel> _activities = new();

    /// <summary>Read the library's activities again (also called by tests).</summary>
    public void RefreshActivities()
    {
        var now = _s.Library.Activities;
        bool structural = false;
        var seen = new HashSet<long>();
        foreach (var a in now)
        {
            seen.Add(a.Id);
            if (_activities.TryGetValue(a.Id, out var row)) row.Update(a);
            else
            {
                var cap = a.CaptureId != null ? _s.Library.Get(a.CaptureId) : null;
                _activities[a.Id] = new ActivityRowViewModel(this, a, cap?.Name ?? "", cap?.DisplayRotation ?? 0);
                structural = true;
            }
        }
        foreach (var id in _activities.Keys.Where(k => !seen.Contains(k)).ToList()) { _activities.Remove(id); structural = true; }
        if (structural) Rebuild();
        RaiseSummary();
    }

    public IReadOnlyCollection<ActivityRowViewModel> Activities => _activities.Values;
    internal void StopActivity(ActivityRowViewModel a) { long id = a.Info.Id; Run(() => _s.Library.CancelActivity(id)); }

    /// <summary>Flattened list for the virtualised view: group headings and jobs.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>Captures whose active job started or ended (their tags change).</summary>
    public event Action<IReadOnlyCollection<string>>? ActiveCapturesChanged;

    public async Task LoadAsync()
    {
        var all = await Task.Run(() => _q.GetJobs(includeFinished: true)).ConfigureAwait(true);
        foreach (var j in all) _jobs[j.Id] = Create(j);
        RebuildActive(out _);
        Rebuild();
        RaiseSummary();
    }

    /// <summary>Remember how the export panel described a job it queued ("DNG + JPG · full size · f/2.0").</summary>
    public void Describe(long jobId, string description)
    {
        _descriptions[jobId] = description;
        if (_jobs.TryGetValue(jobId, out var vm) && vm.Description == null)
        {
            _jobs[jobId] = new JobViewModel(this, vm.Info, vm.Name, description, vm.Orientation);
            Rebuild();
        }
    }

    JobViewModel Create(JobInfo j)
    {
        var cap = j.CaptureId != null ? _s.Library.Get(j.CaptureId) : null;
        string name = j.CaptureId != null ? cap?.Name ?? "" :
                      j.Kind == JobKind.FolderScan && j.FolderId is long f ? Path.GetFileName(_s.Library.GetFolders().FirstOrDefault(x => x.Id == f)?.Path ?? "Folder") :
                      j.Kind == JobKind.Thumbnail ? "Thumbnails" : j.Kind == JobKind.ContextStoreMove ? "Photo data" :
                      j.Kind == JobKind.Import ? j.Subject ?? "Camera" : "";
        _descriptions.TryGetValue(j.Id, out var d);
        return new JobViewModel(this, j, name, d, cap?.DisplayRotation ?? 0);
    }

    void OnJobsChanged(IReadOnlyCollection<JobChangedEventArgs> changes)
    {
        bool structural = false;
        foreach (var c in changes)
        {
            if (c.Removed) { structural |= _jobs.Remove(c.Job.Id); continue; }
            if (_jobs.TryGetValue(c.Job.Id, out var vm))
            {
                if (Group(vm.Info.State) != Group(c.Job.State)) structural = true;
                vm.Update(c.Job);
                if (c.Job.BatchId is { } bid && _batches.TryGetValue(bid, out var bvm)) bvm.Refresh();
                if (c.Job.State == JobState.Done && c.Job.ResultFiles.Count > 0) MeasureResult(c.Job);
            }
            else { _jobs[c.Job.Id] = Create(c.Job); structural = true; }
        }
        RebuildActive(out var touched);
        if (structural) Rebuild();
        RaiseSummary();
        if (touched.Count > 0) ActiveCapturesChanged?.Invoke(touched);
    }

    void MeasureResult(JobInfo j)
    {
        if (_resultSizes.ContainsKey(j.Id) || _s.IsFake) return;
        _resultSizes[j.Id] = 0;
        var files = j.ResultFiles.ToArray();
        _ = Task.Run(() => files.Where(File.Exists).Sum(f => new FileInfo(f).Length)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _resultSizes[j.Id] = t.Result;
                if (_jobs.TryGetValue(j.Id, out var vm)) vm.Update(vm.Info);
            });
        }, TaskScheduler.Default);
    }

    internal long? ResultSize(JobInfo j) => _resultSizes.TryGetValue(j.Id, out var b) ? b : null;

    void RebuildActive(out List<string> touched)
    {
        var next = new Dictionary<string, (JobState, JobKind, double, long)>(StringComparer.Ordinal);
        foreach (var j in _jobs.Values.Select(v => v.Info))
        {
            if (j.IsFinished || j.CaptureId == null || j.Kind == JobKind.Thumbnail) continue;
            if (!next.TryGetValue(j.CaptureId, out var cur) || (j.State == JobState.Running && cur.Item1 != JobState.Running))
                next[j.CaptureId] = (j.State, j.Kind, j.Progress, j.Id);
        }
        touched = new List<string>();
        foreach (var (id, v) in next) if (!_active.TryGetValue(id, out var old) || old != v) touched.Add(id);
        foreach (var id in _active.Keys) if (!next.ContainsKey(id)) touched.Add(id);
        _active.Clear();
        foreach (var (id, v) in next) _active[id] = v;
    }

    public JobState? ActiveStateOf(string captureId) => _active.TryGetValue(captureId, out var v) ? v.State : null;
    public JobKind? ActiveKindOf(string captureId) => _active.TryGetValue(captureId, out var v) ? v.Kind : null;
    public (JobKind Kind, JobState State, double Progress, long Id)? ActiveJobOf(string captureId) =>
        _active.TryGetValue(captureId, out var v) ? (v.Kind, v.State, v.Progress, v.Id) : null;

    static int Group(JobState s) => s switch { JobState.Running => 0, JobState.Waiting or JobState.Paused => 1, JobState.Failed => 3, _ => 2 };

    readonly Dictionary<string, QueueBatchViewModel> _batches = new(StringComparer.Ordinal);

    /// <summary>The section a batch shows in: running when any of its jobs runs, else waiting when any waits, else failed when any failed or was
    /// cancelled, else finished.</summary>
    static int BatchGroup(IReadOnlyList<JobViewModel> jobs) =>
        jobs.Any(j => j.IsRunning) ? 0 : jobs.Any(j => j.IsWaiting) ? 1 : jobs.Any(j => j.IsFailed) ? 3 : 2;

    void Rebuild()
    {
        Rows.Clear();
        _batches.Clear();
        if (_activities.Count > 0)
        {
            Rows.Add(new QueueGroupViewModel("Working now", _activities.Count));
            foreach (var a in _activities.Values.OrderBy(a => a.Info.StartedUtc)) Rows.Add(a);
        }
        // jobs of a batch are one group, placed in the section of its most active job; the others as before
        var batched = _jobs.Values.Where(j => j.Info.BatchId != null).GroupBy(j => j.Info.BatchId!)
                                  .Select(g => new QueueBatchViewModel(this, g.Key, g.First().Info.BatchTitle ?? "Several photos", g.OrderBy(j => j.Id).ToList())).ToList();
        foreach (var b in batched) _batches[b.Id] = b;
        var single = _jobs.Values.Where(j => j.Info.BatchId == null).GroupBy(j => Group(j.Info.State)).ToDictionary(g => g.Key, g => g.ToList());
        var batchBy = batched.GroupBy(b => BatchGroup(b.Jobs)).ToDictionary(g => g.Key, g => g.ToList());
        string[] titles = { "Running", "Waiting", "Finished", "Failed" };
        foreach (int g in new[] { 0, 1, 3, 2 })
        {
            single.TryGetValue(g, out var list); list ??= new();
            batchBy.TryGetValue(g, out var bl); bl ??= new();
            int count = list.Count + bl.Sum(b => b.Jobs.Count);
            if (count == 0) continue;
            Rows.Add(new QueueGroupViewModel(titles[g], count));
            foreach (var b in bl.OrderBy(b => b.Jobs.Min(j => j.Id)))
            {
                Rows.Add(b);
                foreach (var j in b.Jobs) Rows.Add(j);
            }
            IEnumerable<JobViewModel> ordered = g switch
            {
                0 => list.OrderBy(j => j.Info.StartedUtc).ThenBy(j => j.Id),
                1 => list.OrderBy(j => j.Info.Priority).ThenBy(j => j.Id),
                _ => list.OrderByDescending(j => j.Info.FinishedUtc).ThenByDescending(j => j.Id),
            };
            foreach (var j in ordered) Rows.Add(j);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    public IEnumerable<QueueBatchViewModel> Batches => _batches.Values;

    public bool IsEmpty => _jobs.Count == 0 && _activities.Count == 0;
    public IEnumerable<JobViewModel> Jobs => _jobs.Values;

    public int RunningCount => _jobs.Values.Count(j => j.IsRunning);
    public int WaitingCount => _jobs.Values.Count(j => j.IsWaiting);
    public int FailedCount => _jobs.Values.Count(j => j.IsFailed);
    public int UnfinishedCount => _jobs.Values.Count(j => !j.IsFinished);
    public int FinishedTodayCount => _jobs.Values.Count(j => j.IsDone && j.Info.FinishedUtc is { } f && f.ToLocalTime().Date == (_s.IsFake ? f.ToLocalTime().Date : DateTime.Now.Date));
    public bool IsPaused => _status.IsPaused;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    public string TabLabel => UnfinishedCount + _activities.Count > 0 ? $"Queue · {UnfinishedCount + _activities.Count}" : "Queue";

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{RunningCount} running", $"{WaitingCount} waiting", $"{FinishedTodayCount} finished today" };
            if (_activities.Count > 0) parts.Insert(0, $"{_activities.Count} working now");
            if (FailedCount > 0) parts.Add($"{FailedCount} failed");
            if (IsPaused) parts.Insert(0, "Paused");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Status bar on the Queue page: "Exporting L16_00495 · 62 %".</summary>
    public string RunningSummary
    {
        get
        {
            var r = _jobs.Values.Where(j => j.IsRunning).OrderBy(j => j.Info.StartedUtc).FirstOrDefault();
            if (r == null && _activities.Values.OrderBy(a => a.Info.StartedUtc).FirstOrDefault() is { } act)
                return act.Title + (act.Info.Progress is double ap ? $" · {Text.Percent(ap)}" : "") + (_activities.Count > 1 ? $" · {_activities.Count - 1} more" : "");
            if (r == null) return IsPaused ? "Queue paused" : UnfinishedCount > 0 ? "Waiting to start" : "Nothing running";
            string verb = r.Info.Kind switch
            {
                JobKind.Export => "Exporting", JobKind.Analysis => "Analysing", JobKind.CacheUpgrade => "Upgrading",
                JobKind.CacheRelease => "Releasing", JobKind.CacheBuild => "Building", JobKind.LumenImport => "Importing edits of",
                JobKind.FolderScan => "Scanning", JobKind.Thumbnail => "Making thumbnails", JobKind.ContextStoreMove => "Moving photo data", JobKind.Animation => "Animating",
                JobKind.Import => "Importing", _ => "Working on",
            };
            string more = RunningCount > 1 ? $" · {RunningCount - 1} more running" : "";
            return $"{verb} {r.Name} · {Text.Percent(r.Progress)}{more}".Replace("  ", " ");
        }
    }

    public string MemoryText => $"Memory in use {Text.GiB(_status.ReservedMemoryBytes / (double)(1L << 30))} of {Text.GiB(_status.MemoryBudgetBytes / (double)(1L << 30))} budget";

    /// <summary>"Analysing 3 of 15" for the library status bar; empty when no analysis is queued.</summary>
    public string AnalysisSummary
    {
        get
        {
            var a = _jobs.Values.Where(j => j.Info.Kind is JobKind.Analysis).ToList();
            int open = a.Count(j => !j.IsFinished);
            if (open == 0) return "";
            int doneToday = a.Count(j => j.IsDone);
            int running = a.Count(j => j.IsRunning);
            return $"Analysing {doneToday + running} of {a.Count(j => !j.IsCancelled && !j.IsFailed)}";
        }
    }

    void RaiseSummary()
    {
        OnPropertyChanged(nameof(RunningCount)); OnPropertyChanged(nameof(WaitingCount)); OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(UnfinishedCount)); OnPropertyChanged(nameof(FinishedTodayCount)); OnPropertyChanged(nameof(TabLabel));
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(RunningSummary)); OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(PauseLabel)); OnPropertyChanged(nameof(AnalysisSummary));
        OnPropertyChanged(nameof(HasFinished));
    }

    public bool HasFinished => _jobs.Values.Any(j => j.IsFinished);

    // --------------------------------------------------------------------------------------------------- actions (never block)

    internal void Cancel(JobViewModel j) => Run(() => _q.Cancel(j.Id));
    internal void Remove(JobViewModel j) => Run(() => _q.Remove(j.Id));
    internal void ResumeJob(JobViewModel j) => Run(() => _q.Resume(j.Id));
    internal void Retry(JobViewModel j)
    {
        var desc = j.Description;
        _ = Task.Run(() => _q.Retry(j.Id)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is long id && desc != null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Describe(id, desc));
        }, TaskScheduler.Default);
    }
    internal void ShowFile(JobViewModel j) { if (j.Info.ResultPath is { } p) _s.Platform.Reveal(p); }

    [RelayCommand]
    void TogglePause()
    {
        bool paused = IsPaused;
        Run(() => { if (paused) _q.ResumeAll(); else _q.PauseAll(); });
    }

    [RelayCommand] void ClearFinished() => Run(() => _q.ClearFinished());

    static void Run(Action a) => _ = Task.Run(a);
}
