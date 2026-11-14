using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;
using Lux.Library.Edit;
using Lux.Store;

namespace Lux.Library.Depth;

/// <summary>
/// The depth of one open photo (<see cref="IPhotoDepth"/>, <see cref="IDepthEdit"/>, <see cref="IPhotoHistory"/>): the engine's depth editor
/// (<see cref="DepthEditor"/>, one per open photo and version) bound to the photo's edit database (<see cref="DepthEditPersistence"/>), its
/// colour guide from the photo's own pipeline (<see cref="DepthEditColour"/>), and the render snapshot every depth reader of the photo's
/// engine session uses (<see cref="CaptureSession.UpdateDepth"/>). Everything that touches the editor runs on <see cref="DepthWorker"/>; readers
/// (renders, the depth view, read-outs) only ever see immutable snapshots.
/// </summary>
internal sealed partial class DepthSession : IPhotoDepth, IDepthEdit, IPhotoHistory
{
    readonly PhotoSessionImpl _s;
    readonly ExportLevels _lv;
    readonly DepthWorker _worker;
    readonly object _lock = new();
    readonly DofConstants _k;
    readonly DepthEffectInfo _info;
    readonly (int W, int H) _editSize;

    // ---- engine state: the worker thread only
    ExportState? _st; DepthEditSession? _ses; DepthEditor? _ed; DepthEditStore? _store; DepthEditPersistence? _p;
    VariantInfo? _variant;              // the active version's variant; null = clean
    RectI? _changed;                    // depth-cache rect written by the running operation
    CancellationToken _tok;             // the running job's token, read by the editor's providers
    Action<double>? _solveProgress;     // the running edge heal's matting progress
    (int W, int H) _cacheDims0;          // PipelineCache level dims at the base level (the canvas the point mapping normalises to)

    /// <summary>The render snapshot (any thread).</summary>
    internal sealed record Snapshot(DepthImageCache Cache, DepthEditLayer Layer, DepthViewMapping Map, DepthRange Scene, DepthOrigin Origin, long Version);
    volatile Snapshot? _snap;
    long _snapVersion;

    // ---- status
    DepthStatus _status = new() { Availability = DepthAvailability.NotAnalysed };
    TaskCompletionSource<bool>? _ready;   // completes when the depth is attached (true) or failed (false)
    volatile bool _stopped;

    public event EventHandler<DepthStatus>? StatusChanged;
    public event EventHandler<DepthChangedEventArgs>? Changed;
    public event EventHandler<DepthActivity>? ActivityChanged;

    /// <summary>Tests: the random values quick-select strokes draw (Lumen's recorded device values) instead of the derived ones.</summary>
    internal Func<uint>? PinnedRandom { get; set; }
    /// <summary>Tests: the edge-heal interval instead of the one from the focus on screen (Lumen's interval before its first render is (0, 0)).</summary>
    internal Func<(float Lo, float Hi)>? IntervalOverride { get; set; }
    /// <summary>Tests and timings: the live editor and render snapshot (read only on the worker or after a job completed).</summary>
    internal DepthEditor? Editor => _ed;
    internal Snapshot? CurrentSnapshot => _snap;
    internal ExportState? DepthStateForTests => _st;
    internal DepthEditPersistence? Persistence => _p;
    /// <summary>Run a test hook on the depth worker (after every queued job).</summary>
    internal Task<DepthJobResult> OnWorker(string label, Func<DepthJobResult> body) => _worker.Enqueue(label, _ => body(), cancellable: false).Completion;

    internal DepthSession(PhotoSessionImpl s)
    {
        _s = s;
        _lv = s.Pictures.Window;
        _k = DofConstants.FromCapture(s.Engine.Lri);
        _info = new DepthEffectInfo(s.Engine.Info.AllInFocusFNumber, 2.0f, 1.0f, _k.NativeFNumber);
        int l = DepthEditor.EditLevel(_lv); _editSize = _lv.ExportDims[l];
        _worker = new DepthWorker($"depth {s.CaptureId[..8]}", m => s.Lib.Trace(m));
        _worker.ActivityChanged += a => { try { ActivityChanged?.Invoke(this, a); } catch (Exception e) { _s.Lib.Trace("depth listener: " + e.Message); } };
        _worker.ActivityChanged += a => { if (a.Busy) _queueRow?.Update(a.Phase, a.Fraction, a.CanCancel); };
        _worker.Idle += OnWorkerIdle;
        _worker.JobStarted += OnJobStarted;
        _worker.JobEnded += _ => { var r = _queueRow; _queueRow = null; r?.Dispose(); };
    }

    // ---- the background work of the photo's depth in the Queue: reading / computing the depth, version
    //      switches, a Lumen import through the photo, the history rebuild, the selection restore (the tools' own work shows in place)
    Core.ActivityRegistry.Handle? _queueRow;

    void OnJobStarted(DepthWorker.Item it)
    {
        string name = (_s.Lib.Get(_s.CaptureId) ?? _s.Item).Name;
        string? title = it.Label switch
        {
            "Reading the depth" => $"Reading the depth of {name}",
            "Switch version" => $"Switching {name} to another depth version",
            "Start a clean version" => $"Starting a clean depth version of {name}",
            "Import from Lumen" => $"Importing the Lumen edits of {name}",
            "Rebuilding history" => $"Rebuilding the history of {name}",
            "Restoring the selection" => $"Restoring the quick-select selection of {name}",
            "Reset depth edits" => $"Resetting the depth edits of {name}",
            _ => null,
        };
        if (title is null) return;
        // the background jobs start again by themselves later: no stop offered for them; a cancellable foreground job can be stopped
        long jobId = it.Id;
        _queueRow = _s.Lib.ActivityLog.Start(_s.CaptureId, title, it.Phase ?? "Working",
                                             it.CanCancel && !it.Background ? () => _worker.CancelJob(jobId) : null);
    }

    internal void Start()
    {
        _s.EditImpl.Changed += OnEditChanged;
        // a photo whose depth is at hand (analysed, or a version with its own pinned depth) reads it in the background at once
        var item = _s.Lib.Get(_s.CaptureId) ?? _s.Item;
        bool hasVersion = item.Edits.Variant is not null || _s.Engine.SavedState is not null;
        if (item.CacheLevel >= CacheLevel.Geometry || item.DepthState == DepthState.Analysed || hasVersion) StartAttach(analyse: false);
    }

    internal void Stop()
    {
        _stopped = true;
        _s.EditImpl.Changed -= OnEditChanged;
        _worker.Stop(TimeSpan.FromSeconds(15));
        lock (_mirrorLock) { _mirrorTimer?.Dispose(); _mirrorTimer = null; }
        try { _p?.Detach(); } catch { }
        try { _store?.Dispose(); } catch { }
        _ready?.TrySetResult(false);
    }

    // ================================================================================================================ status

    public DepthStatus Status { get { lock (_lock) return _status; } }

    void SetStatus(Func<DepthStatus, DepthStatus> f)
    {
        DepthStatus s;
        lock (_lock) { _status = f(_status); s = _status; }
        try { StatusChanged?.Invoke(this, s); } catch (Exception e) { _s.Lib.Trace("depth listener: " + e.Message); }
    }

    /// <summary>Engine progress (any thread): shown while the depth is being made.</summary>
    internal void OnEngineProgress(ProgressUpdate u)
    {
        if (Status.Availability != DepthAvailability.Analysing) return;
        string phase = u.Phase.Length == 0 ? "" : char.ToUpperInvariant(u.Phase[0]) + u.Phase[1..];
        double? f = u.Total > 0 ? (double)u.Done / u.Total : null;
        SetStatus(s => s with { ProgressText = "Analysing the photo: " + phase, Progress = f });
    }

    public Task EnsureDepthAsync(CancellationToken cancel = default)
    {
        TaskCompletionSource<bool> ready;
        lock (_lock)
        {
            if (_status.Availability == DepthAvailability.Ready) return Task.CompletedTask;
            if (_ready is null || _ready.Task.IsCompleted && !_ready.Task.Result) { _ready = null; }
        }
        ready = StartAttach(analyse: true);
        return Wait(ready.Task, cancel);

        async Task Wait(Task<bool> t, CancellationToken c)
        {
            if (!await t.WaitAsync(c).ConfigureAwait(false)) throw new LibraryException(Status.Error ?? "The depth of this photo could not be made.");
        }
    }

    /// <summary>Start reading (and with <paramref name="analyse"/>, making) the depth once; returns the readiness task.</summary>
    TaskCompletionSource<bool> StartAttach(bool analyse)
    {
        lock (_lock)
        {
            if (_ready is { } r && !(r.Task.IsCompleted && !r.Task.Result)) return r;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        SetStatus(s => s with { Availability = DepthAvailability.Analysing, Progress = null, Error = null,
                                ProgressText = analyse ? "Analysing the photo" : "Reading the depth" });
        var tcs = _ready;
        _worker.Enqueue("Reading the depth", c => { AttachJob(c, analyse); return new DepthJobResult(DepthJobOutcome.Done, null); }, cancellable: false);
        return tcs;
    }

    void AttachJob(DepthWorker.Ctx c, bool analyse)
    {
        try
        {
            var item = _s.Lib.Get(_s.CaptureId) ?? _s.Item;
            // the depth is at hand when the analysis is kept or the active version pins its own depth; otherwise the registration runs (once,
            // shared with any render that needs it) and its result is kept in the photo's cache
            // (the engine's own cache, not IsAnalysed: that builds the level-0 state, i.e. runs the registration here, out of the Queue's sight)
            bool hasDepth = item.CacheLevel >= CacheLevel.Geometry || item.Edits.Variant is not null || _s.Engine.SavedState?.SessionGeometry is not null
                            || _s.Engine.CacheLevel >= CacheLevel.Geometry;
            if (!hasDepth)
            {
                c.Report("Analysing the photo");
                if (!(_s.Engine.SavedState?.SessionGeometry is not null)) WaitForQueuedDepth(c);
            }
            c.Report("Reading the depth");
            LoadActive(c);
            _ready?.TrySetResult(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _s.Lib.Trace($"depth of {_s.CaptureId[..8]}: {e}");
            SetStatus(s => s with { Availability = DepthAvailability.Failed, Progress = null, ProgressText = null, Error = "The depth of this photo could not be made: " + Core.LibraryCore.Plain(e) });
            _ready?.TrySetResult(false);
        }
    }

    /// <summary>The depth map is computed by a Queue job named after the photo (it shows, can be stopped and
    /// follows the job limits) — before, the open photo computed it itself, out of sight (at the first depth tool, "Analyse now", the depth
    /// effect, a version switch). The job keeps it in the photo's cache and this session reads it from there. Worker thread.</summary>
    void WaitForQueuedDepth(DepthWorker.Ctx? c)
    {
        long job = _s.Lib.EnsureDepthJob(_s.CaptureId);
        c?.Report("Waiting for the depth map (in the Queue)");
        for (;;)
        {
            var info = _s.Lib.Queue.Get(job);
            if (info is null || info.IsFinished)
            {
                if (info is { State: JobState.Failed }) throw new LibraryException(info.Error ?? "The analysis failed.");
                if (info is { State: JobState.Cancelled }) throw new LibraryException("The analysis was stopped in the Queue.");
                break;
            }
            if (_stopped) throw new OperationCanceledException();
            bool running = info.State == JobState.Running;
            c?.Report(running ? $"Computing the depth map (in the Queue) · {(int)Math.Round(info.Progress * 100)} %" : "Waiting for the depth map (in the Queue)", running ? info.Progress : null);
            if (Status.Availability == DepthAvailability.Analysing)
                SetStatus(st => st with { ProgressText = running ? "Analysing the photo (in the Queue)" : "Waiting in the Queue to analyse the photo", Progress = running ? info.Progress : null });
            Thread.Sleep(200);
        }
        _s.Engine.RefreshCache();
    }

    /// <summary>The view needs the depth for what it renders (the depth effect with edits, the focal region): make sure the depth is attached
    /// first (so a render never shows the depth effect without the photo's edits). Returns false when there is none.</summary>
    internal bool WaitForDepth(CancellationToken cancel)
    {
        if (_snap is not null) return true;
        if (_stopped) return false;
        var r = StartAttach(analyse: !( (_s.Lib.Get(_s.CaptureId) ?? _s.Item).CacheLevel >= CacheLevel.Geometry));
        try { return r.Task.Wait(Timeout.Infinite, cancel) && r.Task.Result; }
        catch (AggregateException) { return false; }
    }

    internal bool DepthViewReady => _snap is not null;
    internal void EnsureDepthInBackground() { }

    // ================================================================================================================ attaching a version

    /// <summary>Build the editor for the photo's active version (worker): the version's pinned depth (or the computed one for the clean version)
    /// on the engine's depth state, the persistence restoring its snapshot, the render snapshot published, the history read.</summary>
    void LoadActive(DepthWorker.Ctx? c)
    {
        var ctx = _s.Context;
        try { _p?.Detach(); } catch { }
        _p = null; _ed = null; _ses = null;
        _store?.Dispose(); _store = null;
        using (DepthProfile.Time("attach.store")) _store = ctx.HasEdits ? DepthEditStore.Open(ctx.EditsDatabasePath, ctx.ContentHash) : null;
        string? active = _store?.ActiveVariant;
        _variant = active is null ? null : _store!.Find(active);
        // the version's pinned depth is read from the store while the engine makes its depth state (independent)
        var store = _store; var variant = _variant;
        Task<(float[] Depth, int W, int H)>? pinnedRead = variant is null ? null : Task.Run(() => { using (DepthProfile.Time("attach.pinned")) return store!.ReadPinnedDepth(variant); });
        // a version on the computed depth (not an imported session's own) needs the analysis: from the Queue when it is not kept yet
        if (_s.Engine.SavedState?.SessionGeometry is null && _s.Engine.CacheLevel < CacheLevel.Geometry) WaitForQueuedDepth(c);
        ExportState st; using (DepthProfile.Time("attach.depthstate")) st = _s.Engine.DepthState();
        string? openError = null;
        if (_variant is not null)
        {
            try
            {
                try { st.FullDepth = pinnedRead!.GetAwaiter().GetResult(); }
                catch (Exception e) when (e is not DepthEditStoreException and not OutOfMemoryException) { throw new DepthEditStoreException(e.Message); }
                DepthEditSession ses; using (DepthProfile.Time("attach.session")) ses = new DepthEditSession(st);
                using (DepthProfile.Time("attach.persistence")) _p = DepthEditPersistence.Open(_store!, ses.Editor, _variant.Name);
                _ses = ses;
            }
            catch (DepthEditStoreException e)
            {
                openError = $"The saved depth edits ({NameOf(_variant)}) cannot be used ({e.Message}); the original depth is shown.";
                _variant = null; st.ResetDepth(); _p = null;
            }
        }
        using (DepthProfile.Time("attach.session")) _ses ??= new DepthEditSession(st);
        _st = st; _ed = _ses.Editor;
        _cacheDims0 = st.Cache.LevelDims[_lv.BaseLevel];
        Wire(_ses);
        if (_p is not null && PinnedRandom is not null) _p.PinnedRandomDevice = PinnedRandom;
        _ed.DepthChanged += r => _changed = _changed is { } u ? Union(u, r) : r;
        // the render snapshot of this version, everything new
        var origin = _variant?.Kind == DepthEditStore.KindLumenImport ? DepthOrigin.LumenSession : DepthOrigin.Lux;
        DepthViewMapping map; DepthRange scene;
        using (DepthProfile.Time("attach.mapping")) { map = DepthViewRender.MappingOf(_ed.Cache.Current, _lv); scene = DepthViewRender.SceneRangeOf(_ed.Cache.Current, _lv); }
        using (DepthProfile.Time("attach.publish")) Publish(null, map, scene, origin, attach: true);
        using (DepthProfile.Time("attach.history")) ReadHistory();
        // a history found unreliable at open is written to the library's mirror at once (the app offers recovery from there)
        if (HistoryState == DepthHistoryState.Unreliable) { try { FlushMirror(); } catch (Exception e) { _s.Lib.Trace("mirror: " + e.Message); } }
        // the quick-select selection of the saved steps (strokes after the last reset) comes back with the photo
        // (in the background — replaying a saved whole-picture stroke takes seconds; the first picture does not need it, and a quick-select
        // call before it ran replays it itself)
        if (_p is not { QuickSelectPending: true }) UpdateSelection(); else ScheduleSelectionRestore();
        SetStatus(s => s with
        {
            Availability = DepthAvailability.Ready, Progress = null, ProgressText = null, Error = openError, Origin = origin,
            FocusableRange = FocusableRange(), SceneRange = scene, ViewMapping = map,
        });
        RefreshVersions();
        RaiseChanged(DepthChangeKind.DepthReady, NormRect.Full, NormRect.Full);
        RaiseHistory();
        ScheduleReplay();
    }

    /// <summary>The editor's providers, made stoppable: the colour guide and the matting check the running job's token (a provider may only
    /// stop an operation before it writes — the persistence then rolls the editor back, DepthEditor.RollBack).</summary>
    void Wire(DepthEditSession ses)
    {
        int level = ses.Editor.Level; var colour = ses.Colour;
        ses.Editor.GuideProvider = r => { _tok.ThrowIfCancellationRequested(); return colour.Guide(level, r); };
        ses.Editor.Matting = (g, t, w, h) => MattingLaplacian.Run(g, t, w, h, new MattingLaplacian.SolveControl { Cancel = _tok, Progress = _solveProgress });
    }

    static RectI Union(RectI a, RectI b) => new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));

    string NameOf(VariantInfo? v) => v is null ? "Clean" : VersionName(v, _store?.Variants() ?? new List<VariantInfo>());

    // ================================================================================================================ publishing the depth

    /// <summary>
    /// Make the editor's present depth the one every reader uses (worker): a copy of its depth cache and edit layer (immutable from then on)
    /// put in place in the engine (<see cref="CaptureSession.UpdateDepth"/>, which drops exactly the depth-effect tiles it changes), then the
    /// views refresh the regions that changed. <paramref name="changedCache"/> null = everything.
    /// </summary>
    void Publish(RectI? changedCache, DepthViewMapping? map = null, DepthRange? scene = null, DepthOrigin? origin = null, bool attach = false)
    {
        var ed = _ed!; var src = ed.Cache.Current;
        var tCopy = DepthProfile.Time("publish.copy");
        var copy = new DepthImageCache((src.W, src.H));
        // a plain copy, in row bands on several threads (it is memory bound: ~50 MB per edit)
        var dst = copy.Current.Data;
        Parallel.For(0, 16, b => { int y0 = src.H * b / 16, y1 = src.H * (b + 1) / 16; for (int y = y0; y < y1; y++) Array.Copy(src.Data, src.Offset + (long)y * src.Stride, dst, (long)y * src.W, src.W); });
        var layer = new DepthEditLayer(ed.Layer.W, ed.Layer.H);
        if (ed.Layer.Mask is not null)
        {
            var m = ed.Layer.Mask; var bg = ed.Layer.Background!;
            var mc = new byte[m.Length]; var bc = new float[bg.Length];
            Parallel.Invoke(() => Array.Copy(m, mc, m.Length), () => Array.Copy(bg, bc, bg.Length));
            layer.Mask = mc; layer.Background = bc;
        }
        layer.Rects.AddRange(ed.Layer.Rects);
        var prev = _snap;
        var kind = _variant?.Kind ?? "clean";
        int tiles = 0; for (int x = 0; x < ed.Nx; x++) for (int y = 0; y < ed.Ny; y++) if (ed.CurrentVersion(x, y) is not null) tiles++;
        tCopy.Dispose();
        Lux.Engine.Session.CaptureSession.DepthUpdate upd;
        using (DepthProfile.Time("publish.update")) upd = _s.Engine.UpdateDepth(_st!, new AppliedDepthEdits(_variant?.Name ?? "", kind, copy, layer, tiles), changedCache);
        var snap = new Snapshot(copy, layer, map ?? prev?.Map ?? DepthViewRender.MappingOf(copy.Current, _lv), scene ?? prev?.Scene ?? default,
                                origin ?? prev?.Origin ?? DepthOrigin.Lux, Interlocked.Increment(ref _snapVersion));
        _snap = snap;
        // the regions to refresh
        NormRect? depthRect = changedCache is { } ch ? CacheRectToPicture(ch, 3) : NormRect.Full;
        bool dof = DepthEffectOn;
        NormRect? photoRect = null; List<PictureRegion>? areas = null;
        if (dof)
        {
            if (upd.All || changedCache is null) photoRect = NormRect.Full;
            else
            {
                // each dropped depth-effect tile → the picture region of the view level that reads its cache level (the display render reads
                // cache level = base level + view level + the display profile's offset)
                areas = new List<PictureRegion>();
                int shift = _lv.BaseLevel + Lux.Engine.Pipeline.Isp.DisplayIspTuning.L16ProfileOffset;
                foreach (var (L, r) in upd.Tiles) if (DofTileToPicture(L, r) is { } a) { areas.Add(new PictureRegion(L - shift, a)); photoRect = UnionN(photoRect, a); }
            }
        }
        bool focal = EffectiveOverlay == DepthOverlayMode.FocalRegion;
        if (focal) { photoRect = UnionN(photoRect, depthRect); if (areas is not null && depthRect is { } dr) areas.Add(new PictureRegion(-1, dr)); }
        if (photoRect is null) areas = null;
        // attaching: every render that reads the depth waited for it (WaitForDepth), so no photo tile shows another depth — only a waiting
        // depth view (and a focal region) is drawn now
        if (attach) { photoRect = focal ? NormRect.Full : null; areas = null; }
        // a new focusable range moves the focal band (only on photos without autofocus distances)
        var fr = FocusableRange();
        bool rangeMoved = !Nullable.Equals(fr, Status.FocusableRange);
        if (rangeMoved) { SetStatus(s => s with { FocusableRange = fr }); if (focal) { photoRect = NormRect.Full; areas = null; } }
        // the focal region reads the depth snapshot: its overlay is made again on the new one (the others do not depend on the depth)
        var (pv, dv) = _s.EditImpl.BumpForDepth(photoRect is not null, depthRect is not null, focal ? BuildOverlay() : null, setOverlay: focal);
        LastRefresh = (upd.Tiles.Count, upd.Kept, photoRect);
        if (DepthProfile.On) DepthProfile.Add($"publish: dropped {upd.Tiles.Count} (" + string.Join(" ", upd.Tiles.GroupBy(t => t.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key}:{g.Count()}")) + $"), kept {upd.Kept}, areas {areas?.Count.ToString() ?? "-"}, region {photoRect}", DepthProfile.Now, 0);
        _s.ViewImpl.OnRegionsChanged(photoRect, depthRect, areas);
        RaiseChanged(DepthChangeKind.DepthEdit, photoRect, depthRect, pv, dv, areas);
    }

    /// <summary>The last publish: depth-effect tiles dropped / kept by the engine, and the photo region refreshed (tests).</summary>
    internal (int Dropped, int Kept, NormRect? Photo) LastRefresh { get; private set; }

    static NormRect? UnionN(NormRect? a, NormRect? b)
    {
        if (a is not { } x) return b; if (b is not { } y) return a;
        double x0 = Math.Min(x.X, y.X), y0 = Math.Min(x.Y, y.Y), x1 = Math.Max(x.Right, y.Right), y1 = Math.Max(x.Bottom, y.Bottom);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>A depth-cache rect (the pipeline frame of the edited level) → picture rect, grown by <paramref name="grow"/> edited-level pixels.</summary>
    NormRect? CacheRectToPicture(RectI r, int grow)
    {
        int l = DepthEditor.EditLevel(_lv); var o = _lv.Origins[l]; var (w, h) = _lv.ExportDims[l];
        double x0 = (double)(r.X0 - o.X - grow) / w, y0 = (double)(r.Y0 - o.Y - grow) / h, x1 = (double)(r.X1 - o.X + grow) / w, y1 = (double)(r.Y1 - o.Y + grow) / h;
        return _s.ViewImpl.CaptureRectToPicture(x0, y0, x1, y1);
    }

    /// <summary>A depth-effect tile (PipelineCache level <paramref name="cacheLevel"/>, rect in its pixels) → the picture region its change can
    /// reach: grown by the display tile's halo (32 pixels of its level) and the resamplers' taps.</summary>
    NormRect? DofTileToPicture(int cacheLevel, RectI r)
    {
        var dims = _st!.Cache.LevelDims[cacheLevel];
        const int grow = 40;
        double u0 = (double)(r.X0 - grow) / dims.W, v0 = (double)(r.Y0 - grow) / dims.H, u1 = (double)(r.X1 + grow) / dims.W, v1 = (double)(r.Y1 + grow) / dims.H;
        // canvas-normalised → export-window-normalised (level 1's window; the same frame at every level)
        int l = Math.Min(1, _lv.ExportDims.Length - 1); var pd = _lv.PipelineDims[l]; var o = _lv.Origins[l]; var (ew, eh) = _lv.ExportDims[l];
        double x0 = (u0 * pd.W - o.X) / ew, y0 = (v0 * pd.H - o.Y) / eh, x1 = (u1 * pd.W - o.X) / ew, y1 = (v1 * pd.H - o.Y) / eh;
        return _s.ViewImpl.CaptureRectToPicture(x0, y0, x1, y1);
    }

    bool DepthEffectOn => _s.EditImpl.DepthEffect.FNumber is float f && f < _info.AllInFocusFNumber;

    void RaiseChanged(DepthChangeKind kind, NormRect? photo, NormRect? depth, long? pv = null, long? dv = null, IReadOnlyList<PictureRegion>? photoRegions = null)
    {
        var e = new DepthChangedEventArgs
        {
            Kind = kind, PhotoRect = photo, DepthRect = depth, PhotoRegions = photoRegions,
            PhotoVersion = pv ?? _s.EditImpl.Versions(ViewMode.Edited).Version, DepthVersion = dv ?? _s.EditImpl.Versions(ViewMode.Depth).Version,
        };
        try { Changed?.Invoke(this, e); } catch (Exception x) { _s.Lib.Trace("depth listener: " + x.Message); }
    }

    // ================================================================================================================ the depth view

    /// <summary>The depth view's pixels of a picture rect of a plan (the export resampler over Lux's grey depth on the export grid).</summary>
    internal byte[] RenderDepthView(PictureRenderer.Plan p, RectI pr, CancellationToken tok)
    {
        var snap = _snap ?? throw new InvalidOperationException("the depth is not ready");
        return _s.Pictures.Render(p, pr, (lv, rect) => DepthViewRender.Render(snap.Cache.Current, _lv, lv, rect, snap.Map), snap, tok);
    }

    // ================================================================================================================ read-outs

    /// <summary>Picture point → the point `getDepthAtPoint` takes (normalised export window) as floats; null outside [0, 1).</summary>
    (float X, float Y)? InsidePoint(NormPoint p)
    {
        var (cx, cy) = _s.ViewImpl.ToCapture(p);
        float x = (float)cx, y = (float)cy;
        return x < 0f || x >= 1f || y < 0f || y >= 1f ? null : (x, y);
    }

    /// <summary>Lumen's 3×3 mean at a normalised export-window point of a depth-cache image.</summary>
    float? DepthAtCapture(DepthImage depth, float x, float y)
    {
        var q = DofModel.MapExportPoint(x, y, _lv.ExportDims[0], _lv.Origins[0], _cacheDims0);
        try { return DofModel.DepthAtPoint(depth, q.X, q.Y); } catch (InvalidOperationException) { return null; }
    }

    public float? DepthAt(NormPoint picture)
    {
        var snap = _snap; if (snap is null) return null;
        return InsidePoint(picture) is { } c ? DepthAtCapture(snap.Cache.Current, c.X, c.Y) : null;
    }

    /// <summary>Lumen's focusable range (`ParamFloatArray 0`, with ParamFloat 19) on the present depth.</summary>
    DepthRange? FocusableRange()
    {
        var snap = _snap; if (snap is null) return null;
        try
        {
            var (n, f) = DofDisplay.InFocusRange(_s.Engine.Lri, snap.Cache.Current, _maxBlur);
            return new DepthRange(n, f);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    // ================================================================================================================ edit hooks

    void OnEditChanged(object? sender, EditChangedEventArgs e)
    {
        if (e.Kind is EditChangeKind.History || e.StepLabel is not null) RaiseHistory();
        if (e.Kind == EditChangeKind.Adjustment && e.Key is null) RaiseChanged(DepthChangeKind.DepthEffect, NormRect.Full, null);
    }

    /// <summary>A non-depth step was made: the depth's undone steps leave the combined list (one linear history).</summary>
    internal void OnNonDepthStep() { _depthRedoVisible = 0; }

    // ================================================================================================================ mirror

    readonly object _mirrorLock = new(); Timer? _mirrorTimer;
    /// <summary>The library's mirror (depth-edited flag, version, operation count, thumbnail key) follows a committed depth change, debounced.</summary>
    void ScheduleMirror()
    {
        lock (_mirrorLock)
        {
            if (_stopped) return;
            _mirrorTimer ??= new Timer(_ =>
            {
                try
                {
                    var changed = _s.Lib.RefreshContexts(new[] { _s.CaptureId }, force: true);
                    _s.Lib.Changes.Changed(changed.Count > 0 ? changed : new List<string> { _s.CaptureId });
                }
                catch (Exception e) { _s.Lib.Trace($"mirror of {_s.CaptureId[..8]}: {e.Message}"); }
            });
            _mirrorTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>Write the mirror now (tests).</summary>
    internal void FlushMirror()
    {
        var changed = _s.Lib.RefreshContexts(new[] { _s.CaptureId }, force: true);
        _s.Lib.Changes.Changed(changed.Count > 0 ? changed : new List<string> { _s.CaptureId });
    }

    void OnWorkerIdle() { ScheduleSelectionRestore(); ScheduleReplay(); }

    /// <summary>The saved quick-select strokes (after the last reset) replayed on the worker as a background job, then the selection shown —
    /// stopped by any user job (nothing changes) and started again when the worker is idle.</summary>
    void ScheduleSelectionRestore()
    {
        if (_stopped || _p is not { QuickSelectPending: true } || _worker.HasBackground("Restoring the selection")) return;
        _worker.Enqueue("Restoring the selection", c =>
        {
            if (_p is not { QuickSelectPending: true } p) return new DepthJobResult(DepthJobOutcome.NothingChanged, null);
            try { p.EnsureQuickSelect(c.Token); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
            UpdateSelection();
            return new DepthJobResult(DepthJobOutcome.Done, null);
        }, cancellable: true, background: true);
    }
}
