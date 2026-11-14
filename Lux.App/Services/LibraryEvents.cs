using Avalonia.Threading;
using Lux.Library;

namespace Lux.App.Services;

/// <summary>
/// Moves the library's events (raised on library threads) to the UI thread, coalesced: whatever arrives before the UI thread gets to
/// it is delivered as one batch. View models subscribe here, never to the library directly.
/// </summary>
public sealed class LibraryEvents : IDisposable
{
    readonly ILibrary _lib;
    readonly object _gate = new();
    readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    readonly Dictionary<long, JobChangedEventArgs> _jobs = new();
    bool _reset, _structure, _folders, _albums, _status, _scheduled;

    public LibraryEvents(ILibrary lib)
    {
        _lib = lib;
        lib.CapturesChanged += OnCaptures;
        lib.FoldersChanged += OnFolders;
        lib.AlbumsChanged += OnAlbums;
        lib.Jobs.JobChanged += OnJob;
        lib.Jobs.StatusChanged += OnStatus;
    }

    /// <summary>Captures changed: the ids (in place), or <c>structural</c> = added / removed / reset (re-query).</summary>
    public event Action<IReadOnlyCollection<string>, bool>? CapturesChanged;
    public event Action? FoldersChanged;
    public event Action? AlbumsChanged;
    public event Action<IReadOnlyCollection<JobChangedEventArgs>>? JobsChanged;
    public event Action? QueueStatusChanged;

    void OnCaptures(object? s, CapturesChangedEventArgs e)
    {
        lock (_gate)
        {
            if (e.Reset || e.Added.Count > 0 || e.Removed.Count > 0) _structure = true;
            if (e.Reset) _reset = true;
            foreach (var id in e.Changed) _changed.Add(id);
        }
        Schedule();
    }

    void OnFolders(object? s, EventArgs e) { lock (_gate) _folders = true; Schedule(); }
    void OnAlbums(object? s, EventArgs e) { lock (_gate) _albums = true; Schedule(); }
    void OnJob(object? s, JobChangedEventArgs e) { lock (_gate) _jobs[e.Job.Id] = e; Schedule(); }
    void OnStatus(object? s, JobQueueStatus e) { lock (_gate) _status = true; Schedule(); }

    void Schedule()
    {
        lock (_gate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>Deliver what is pending now (the UI thread; tests call it directly).</summary>
    public void Flush()
    {
        string[] changed; JobChangedEventArgs[] jobs; bool structure, folders, albums, status;
        lock (_gate)
        {
            _scheduled = false;
            changed = _changed.ToArray(); _changed.Clear();
            jobs = _jobs.Values.ToArray(); _jobs.Clear();
            structure = _structure || _reset; folders = _folders; albums = _albums; status = _status;
            _structure = _reset = _folders = _albums = _status = false;
        }
        if (jobs.Length > 0) JobsChanged?.Invoke(jobs);
        if (status || jobs.Length > 0) QueueStatusChanged?.Invoke();
        if (folders) FoldersChanged?.Invoke();
        if (albums) AlbumsChanged?.Invoke();
        if (structure || changed.Length > 0) CapturesChanged?.Invoke(changed, structure);
    }

    public void Dispose()
    {
        _lib.CapturesChanged -= OnCaptures;
        _lib.FoldersChanged -= OnFolders;
        _lib.AlbumsChanged -= OnAlbums;
        _lib.Jobs.JobChanged -= OnJob;
        _lib.Jobs.StatusChanged -= OnStatus;
    }
}
