using Lux.Engine.Session;
using Lux.Library.Edit;

namespace Lux.Library.Core;

/// <summary>The Develop page: open photos, the clipboard and presets.</summary>
internal sealed partial class LibraryCore
{
    readonly object _sessionsLock = new();

    /// <summary>The depth map of a capture comes from a Queue job: an unfinished analysis or cache job of the
    /// capture when there is one (moved to the front of the Queue), else a new analysis to the depth tier. Returns the job's id.</summary>
    internal long EnsureDepthJob(string captureId)
    {
        var existing = Queue.JobsOf(captureId).FirstOrDefault(j => j.Kind is JobKind.Analysis or JobKind.CacheBuild or JobKind.CacheUpgrade);
        if (existing is not null) { if (existing.State == JobState.Waiting) Queue.SetPriority(existing.Id, 0); return existing.Id; }
        return Queue.Enqueue(new AnalysisJobRequest(captureId, CacheLevel.Geometry) { Priority = 0 });
    }

    // ---- activities: background work that is not a Queue job
    internal readonly ActivityRegistry ActivityLog = new();
    public IReadOnlyList<ActivityInfo> Activities => ActivityLog.Snapshot;
    public event EventHandler? ActivitiesChanged { add => ActivityLog.Changed += value; remove => ActivityLog.Changed -= value; }
    public void CancelActivity(long id) => ActivityLog.Cancel(id);
    readonly List<PhotoSessionImpl> _sessions = new();
    EditClipboard? _clipboard;

    public async Task<IPhotoSession> OpenPhotoAsync(string captureId, PhotoOpenOptions? options = null, CancellationToken cancel = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(ILibrary));
        try { return await PhotoSessionImpl.OpenAsync(this, captureId, options, cancel).ConfigureAwait(false); }
        catch (Exception e) when (e is not LibraryException and not OperationCanceledException and not OutOfMemoryException and not ObjectDisposedException)
        {
            throw new LibraryException("This photo cannot be opened: " + Plain(e), e);
        }
    }

    public IEditClipboard Edits { get { lock (_sessionsLock) return _clipboard ??= new EditClipboard(this); } }

    internal void RegisterSession(PhotoSessionImpl s) { lock (_sessionsLock) _sessions.Add(s); }
    internal void UnregisterSession(PhotoSessionImpl s) { lock (_sessionsLock) _sessions.Remove(s); }

    /// <summary>An editable open session of the photo (the newest), or null.</summary>
    internal PhotoSessionImpl? OpenSession(string captureId)
    {
        lock (_sessionsLock) return _sessions.LastOrDefault(s => s.CaptureId == captureId && !s.Options.ReadOnly && !s.IsDisposed);
    }

    /// <summary>Whether exports of a photo shown the way the camera was held follow that view: the per-export choice, else the setting; only
    /// while the library shows photos as held (otherwise there is no upright view to follow).</summary>
    internal bool ExportsTurnUpright(bool? unturned) => SettingsImpl.ShowPhotosAsHeld && !(unturned ?? SettingsImpl.ExportUnturned);

    /// <summary>The held turn an export of <paramref name="captureId"/> adds (Lumen's rotate, as saved by "Rotate as the camera was held"), or
    /// null.</summary>
    internal Lux.Engine.Pipeline.Export.ExportTransform? UprightTransform(string captureId, bool? unturned, out int degrees)
    {
        degrees = 0;
        if (!ExportsTurnUpright(unturned)) return null;
        return ((EditClipboard)Edits).HeldTurnTransform(captureId, out degrees);
    }

    readonly List<Lux.Library.Animation.AnimationSourceImpl> _animSources = new();
    internal void RegisterAnimation(Lux.Library.Animation.AnimationSourceImpl a) { lock (_sessionsLock) _animSources.Add(a); }
    internal void UnregisterAnimation(Lux.Library.Animation.AnimationSourceImpl a) { lock (_sessionsLock) _animSources.Remove(a); }

    /// <summary>A job changed what the capture keeps (its analysis, a quality made or released): its open photos and animation sources use
    /// the cache as it is now, and the open photos' viewers render again for the finest level they may now show (an
    /// upgrade made while the photo was open was not read by the open photo, which kept generating full detail itself).</summary>
    internal void OnCacheChanged(string captureId)
    {
        List<PhotoSessionImpl> photos; List<Lux.Library.Animation.AnimationSourceImpl> anims;
        lock (_sessionsLock)
        {
            photos = _sessions.Where(s => s.CaptureId == captureId && !s.IsDisposed).ToList();
            anims = _animSources.Where(a => a.CaptureId == captureId && !a.IsDisposed).ToList();
        }
        foreach (var s in photos) { try { s.OnCacheChanged(); } catch (Exception e) when (e is not OutOfMemoryException) { Trace("cache refresh: " + e.Message); } }
        foreach (var a in anims) { try { a.OnCacheChanged(); } catch (Exception e) when (e is not OutOfMemoryException) { Trace("cache refresh: " + e.Message); } }
    }

    /// <summary>Close every open photo (library shutdown).</summary>
    internal void CloseSessions()
    {
        List<PhotoSessionImpl> all; lock (_sessionsLock) all = _sessions.ToList();
        foreach (var s in all) { try { s.Dispose(); } catch (Exception e) { Trace("closing a photo: " + e.Message); } }
    }
}
