namespace Lux.Library.Core;

/// <summary>
/// The library's background work that is not a Queue job: each producer starts an activity named after its
/// photo, updates its phase and progress, and ends it; the Queue lists them with the jobs. Listeners hear of changes at most ten times a
/// second, the latest state always (a trailing event).
/// </summary>
internal sealed class ActivityRegistry
{
    sealed class Entry
    {
        public required ActivityInfo Info;
        public Action? Cancel;
    }

    readonly object _lock = new();
    readonly Dictionary<long, Entry> _entries = new();
    long _ids;
    long _raised;
    bool _trailing;

    public event EventHandler? Changed;

    public IReadOnlyList<ActivityInfo> Snapshot { get { lock (_lock) return _entries.Values.Select(e => e.Info).OrderBy(i => i.StartedUtc).ToList(); } }

    /// <summary>Start an activity; dispose the handle when it ends.</summary>
    public Handle Start(string? captureId, string title, string status, Action? cancel = null)
    {
        long id;
        lock (_lock)
        {
            id = ++_ids;
            _entries[id] = new Entry { Info = new ActivityInfo(id, captureId, title, status, null, cancel is not null, DateTime.UtcNow), Cancel = cancel };
        }
        Raise();
        return new Handle(this, id);
    }

    public void Update(long id, string? status, double? progress, bool? canCancel = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            var next = e.Info with { Status = status ?? e.Info.Status, Progress = progress, CanCancel = (canCancel ?? e.Info.CanCancel) && e.Cancel is not null };
            if (next == e.Info) return;
            e.Info = next;
        }
        Raise();
    }

    public void End(long id)
    {
        lock (_lock) if (!_entries.Remove(id)) return;
        Raise();
    }

    public void Cancel(long id)
    {
        Action? c;
        lock (_lock) c = _entries.TryGetValue(id, out var e) && e.Info.CanCancel ? e.Cancel : null;
        try { c?.Invoke(); } catch (Exception) { }
    }

    void Raise()
    {
        long wait;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            wait = 100 - (now - _raised);
            if (wait <= 0) _raised = now;
            else if (_trailing) return;
            else _trailing = true;
        }
        if (wait <= 0) { Fire(); return; }
        _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ =>
        {
            lock (_lock) { _trailing = false; _raised = Environment.TickCount64; }
            Fire();
        }, TaskScheduler.Default);
    }

    void Fire() { try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { } }

    /// <summary>An activity in progress: update it, end it by disposing.</summary>
    public sealed class Handle(ActivityRegistry owner, long id) : IDisposable
    {
        public long Id { get; } = id;
        int _ended;
        public void Update(string? status, double? progress = null, bool? canCancel = null) { if (Volatile.Read(ref _ended) == 0) owner.Update(Id, status, progress, canCancel); }
        public void Dispose() { if (Interlocked.Exchange(ref _ended, 1) == 0) owner.End(Id); }
    }
}
