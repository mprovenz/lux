using System.Text.RegularExpressions;
using Lux.App.Viewer;
using Lux.Library;
using AppPending = Lux.App.Develop.PendingArea;
using AppStatus = Lux.App.Develop.DepthStatusInfo;
using AppToolOptions = Lux.App.Develop.ToolOptions;
using AppVersionKind = Lux.App.Develop.VersionKind;
using IAppStroke = Lux.App.Develop.IToolStroke;

namespace Lux.App.Develop;

/// <summary>
/// The page's depth over the library's depth contract (<c>DepthContracts.cs</c> v3.0): <see cref="IPhotoDepth"/> (readiness, the depth effect,
/// focus by click, the focal-region overlay, the depth at a point), <see cref="IDepthEdit"/> (Lumen's tools, the pending match-depth selection,
/// the worker's activity, the depth history state and versions) and the depth view (<see cref="ViewMode.Depth"/> tiles of the photo's own view).
/// The library draws the focal-region shade and the quick-select selection into the photo's tiles, so <see cref="ShadeLayer"/> and
/// <see cref="MaskLayer"/> are null here. Every depth command returns at once; jobs are awaited off the UI thread's work.
/// </summary>
public sealed class ContractDepthSession : IDepthSession, IDisposable
{
    /// <summary>The Studio accent (#a9dcff) for the selection ("Blue = selected"); Lumen's GUI used pink.</summary>
    static readonly float[] SelectionBlue = { 0xa9 / 255f, 0xdc / 255f, 1f, 0.45f };

    readonly IPhotoSession _ps;
    readonly IPhotoDepth _depth;
    readonly IDepthEdit _edit;
    readonly IPhotoHistory _history;
    readonly ContractTileSource _view;
    (double? F, double Focus)? _drag;
    (double? F, double Focus) _dragStart;
    bool _fDragging, _focusDragging;
    DepthToolKind? _tool;
    MatchMode _mode = MatchMode.Brush;
    AppToolOptions _options = new();
    double _opacity = 1;

    /// <summary>What the page reads, taken off the UI thread after every library event (the library's getters may wait while its depth
    /// worker runs a tool; the pointer and the page never do).</summary>
    sealed record Snap(DepthStatus Status, DepthEffectState Current, PendingSelection? Pending, IReadOnlyList<DepthVersion> Versions, DepthVersion Active,
                       DepthActivity Activity, DepthOverlayMode Overlay, HistoryInfo History, string? LumenPath, PixelSize SelectionSize);

    volatile Snap _snap;
    int _snapPending;
    readonly DepthEffectInfo _effect;
    readonly double _radiusPerSize;

    public ContractDepthSession(IPhotoSession ps, ILibrary lib)
    {
        _ps = ps;
        _depth = ps.Depth;
        _edit = ps.DepthEdit;
        _history = ps.History;
        _view = new ContractTileSource(ps.View, depth: true);
        try { _depth.SelectionColour = SelectionBlue; } catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { }
        _effect = _depth.Effect;
        _radiusPerSize = _edit.BrushRadiusPixels(100f) / 100.0;   // Lumen's radius is linear in the size
        _snap = Take();
        _depth.StatusChanged += OnAny;
        _depth.Changed += OnAny;
        _edit.Changed += OnAny;
        _depth.Changed += (_, e) => _view.DepthChanged(e);   // the depth view keeps its unchanged tiles
        _edit.Changed += (_, e) => _view.DepthChanged(e);
        _edit.ActivityChanged += OnAny;
        _history.Changed += OnAny;
    }

    Snap Take() => new(_depth.Status, _depth.Current, _edit.Pending, _edit.Versions, _edit.ActiveVersion, _edit.Activity, _depth.Overlay, _history.Info,
                       _edit.LumenSessionPath, _edit.SelectionSize);

    /// <summary>The combined history as of the last library event (the session's history list reads this, never the library directly).</summary>
    internal HistoryInfo HistorySnapshot => _snap.History;

    void OnAny(object? sender, object e) => Refresh();

    long _lastSnap;
    /// <summary>Snapshots taken (measurements).</summary>
    public static int Snapshots;

    /// <summary>A new snapshot on the thread pool, coalesced and at most every 50 ms (the library raises events for every preview tile and
    /// progress tick; the page needs about a frame's worth), then <see cref="Changed"/>.</summary>
    void Refresh()
    {
        if (Interlocked.Exchange(ref _snapPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            double since = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSnap)).TotalMilliseconds;
            if (since < 50) await Task.Delay(TimeSpan.FromMilliseconds(50 - since)).ConfigureAwait(false);
            Interlocked.Exchange(ref _snapPending, 0);
            try { _snap = Take(); Interlocked.Increment(ref Snapshots); }
            catch (Exception) { }
            Interlocked.Exchange(ref _lastSnap, System.Diagnostics.Stopwatch.GetTimestamp());
            Changed?.Invoke();
        });
    }

    public event Action? Changed;

    // ================================================================================================================ readiness

    public AppStatus Status
    {
        get
        {
            var s = _snap.Status;
            return s.Availability switch
            {
                DepthAvailability.Ready => AppStatus.Ready,
                DepthAvailability.Analysing => new AppStatus(DepthReadiness.Analysing, s.Progress ?? double.NaN, s.ProgressText),
                DepthAvailability.NotAnalysed => new AppStatus(DepthReadiness.NotAnalysed),
                DepthAvailability.Failed => new AppStatus(DepthReadiness.Failed, Message: s.Error),
                _ => new AppStatus(DepthReadiness.Unavailable, Message: s.Error ?? "This photo has no depth."),
            };
        }
    }

    public void AnalyseNow()
    {
        var d = _depth;
        _ = Task.Run(async () =>
        {
            try { await d.EnsureDepthAsync().ConfigureAwait(false); }
            catch (Exception) { }   // the status says why
            Refresh();
        });
    }

    // ================================================================================================================ the depth effect

    public DepthEffectRange EffectRange
    {
        get
        {
            var e = _effect;
            var st = _snap.Status;
            var cur = _snap.Current;
            double near = st.SceneRange?.NearMm ?? DepthEffectRange.Placeholder.NearestMm, far = st.SceneRange?.FarMm ?? DepthEffectRange.Placeholder.FarthestMm;
            if (far <= near) far = near * 10;
            return new DepthEffectRange(e.LumenMinimumFNumber, e.AllInFocusFNumber, near, far, cur.DefaultFocusMm ?? Math.Sqrt(near * far));
        }
    }

    public (double NearMm, double FarMm)? FocusableRange => _snap.Status.FocusableRange is { } r ? (r.NearMm, r.FarMm) : null;

    public double? FNumber => _drag is { } d ? d.F : _snap.Current is { IsOn: true, FNumber: float f } ? f : null;
    public double FocusMm => _drag is { } d ? d.Focus : _snap.Current.EffectiveFocusMm ?? EffectRange.DefaultFocusMm;
    public bool FocusIsDefault => _drag == null && _snap.Current.FocusMm == null;
    public NormPoint? FocusPoint => _drag == null ? _snap.Current.FocusPoint : null;
    public (double NearMm, double FarMm)? InFocus => _snap.Current is { IsOn: true, InFocus: { } r } ? (r.NearMm, r.FarMm) : null;

    public void BeginEffectDrag()
    {
        _dragStart = (FNumber, FocusMm);
        _drag = _dragStart;
        _fDragging = false;
        _focusDragging = false;
    }

    public void DragEffect(double? fNumber, double focusMm)
    {
        _drag = (fNumber, focusMm);
        if (fNumber != _dragStart.F || _fDragging)
        {
            if (!_fDragging) { _depth.BeginFNumberDrag(); _fDragging = true; }
            _depth.DragFNumber((float?)fNumber);
        }
        // The focus previews live too (drag level, no step until the end)
        if (Math.Abs(focusMm - _dragStart.Focus) > 0.5 || _focusDragging)
        {
            if (!_focusDragging) { _depth.BeginFocusDrag(); _focusDragging = true; }
            _depth.DragFocus((float)focusMm);
        }
        Changed?.Invoke();
    }

    public void EndEffectDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        if (_fDragging) { _fDragging = false; _depth.EndFNumberDrag(); }
        if (_focusDragging) { _focusDragging = false; _depth.EndFocusDrag(); }
        Refresh();
        Changed?.Invoke();
    }

    public void SetEffect(double? fNumber, double focusMm)
    {
        if (fNumber != FNumber) _depth.SetFNumber((float?)fNumber);
        if (Math.Abs(focusMm - FocusMm) > 0.5) _depth.SetFocus((float)focusMm);
    }

    public async Task<PointResult> FocusAtAsync(NormPoint picture, CancellationToken cancel = default)
    {
        var d = _depth;
        var r = await Task.Run(() => d.FocusAtAsync(picture, commit: true, cancel), cancel).ConfigureAwait(true);
        Refresh();
        return new PointResult(r.Accepted, r.DepthMm, r.Reason ?? (r.Accepted ? null : "This area cannot be brought into focus. Choose a point in the unshaded area."));
    }

    public bool CanFocusAt(NormPoint picture) => DepthAt(picture) is double d && FocusableRange is { } r && d >= r.NearMm && d <= r.FarMm;
    public void ResetFocus() => _depth.SetFocus(null);
    public void ResetEffect() => _depth.ResetDepthEffect();

    // ================================================================================================================ views and overlays

    public bool ShadeOutsideFocus
    {
        get => _snap.Overlay == DepthOverlayMode.FocalRegion;
        set
        {
            var mode = value ? DepthOverlayMode.FocalRegion
                : _snap.Overlay != DepthOverlayMode.FocalRegion ? _snap.Overlay
                : _mode == MatchMode.QuickSelect && _tool == DepthToolKind.MatchDepth ? DepthOverlayMode.Selection : DepthOverlayMode.None;
            _snap = _snap with { Overlay = mode };
            var d = _depth;
            _ = Task.Run(() => { d.Overlay = mode; Refresh(); });
            Changed?.Invoke();
        }
    }

    public ITileSource? ShadeLayer => null;
    public ITileSource DepthView => _view;
    public double? DepthAt(NormPoint picture) => _depth.DepthAt(picture) is float d && float.IsFinite(d) ? d : null;
    public (double NearMm, double FarMm) DepthViewRange => _snap.Status.ViewMapping is { } m ? (m.NearMm, m.FarMm) : (EffectRange.NearestMm, EffectRange.FarthestMm);

    // ================================================================================================================ tools

    public DepthToolKind? Tool => _tool;
    public MatchMode Mode => _mode;
    public AppToolOptions Options => _options;

    public void SetTool(DepthToolKind? tool, MatchMode mode = MatchMode.Brush)
    {
        _tool = tool;
        _mode = mode;
        _edit.SetTool(tool switch
        {
            DepthToolKind.RefineEdge => DepthTool.RefineEdge,
            DepthToolKind.BlendArea => DepthTool.BlendArea,
            DepthToolKind.MatchDepth => DepthTool.MatchDepth,
            _ => null,
        }, mode switch { MatchMode.Lasso => MatchApplicator.Lasso, MatchMode.QuickSelect => MatchApplicator.QuickSelect, _ => MatchApplicator.Brush });
        Changed?.Invoke();
    }

    public void SetOptions(AppToolOptions options)
    {
        _options = options;
        _edit.SetOptions(new Lux.Library.DepthToolOptions((float)options.BrushSize, options.FollowColour, options.AddToSelection));
    }

    public double BrushDiameterPx(double size) => 2 * _radiusPerSize * size;

    public IAppStroke BeginStroke()
    {
        if (!Status.IsReady) throw new InvalidOperationException("The depth map is not ready yet: analyze the photo first.");
        return new Stroke(this, _edit.BeginStroke());
    }

    /// <summary>A job's result, with the snapshot taken again before it is returned (what the page reads next is the state after the job).</summary>
    async Task<ToolOutcome> Run(DepthJob job)
    {
        var r = await Outcome(job).ConfigureAwait(true);
        try { _snap = await Task.Run(Take).ConfigureAwait(true); } catch (Exception) { }
        Changed?.Invoke();
        return r;
    }

    sealed class Stroke(ContractDepthSession owner, IDepthStroke s) : IAppStroke
    {
        bool _done;
        public void Add(IReadOnlyList<NormPoint> points) { if (!_done) s.Add(points as NormPoint[] ?? points.ToArray()); }
        public void Cancel() { if (_done) return; _done = true; s.Cancel(); s.Dispose(); }
        public async Task<ToolOutcome> EndAsync()
        {
            if (_done) return ToolOutcome.Cancelled;
            _done = true;
            var job = s.End();
            var r = await owner.Run(job).ConfigureAwait(true);
            s.Dispose();
            return r;
        }
    }

    /// <summary>A job's result in the page's words.</summary>
    internal static async Task<ToolOutcome> Outcome(DepthJob job)
    {
        DepthJobResult r;
        try { r = await job.Completion.ConfigureAwait(true); }
        catch (OperationCanceledException) { return ToolOutcome.Cancelled; }
        catch (Exception e) { return new ToolOutcome(ToolResult.Failed, Lux.App.ViewModels.Text.Plain(e)); }
        return r.Outcome switch
        {
            DepthJobOutcome.Done => ToolOutcome.Applied,
            DepthJobOutcome.NothingChanged => new ToolOutcome(ToolResult.NothingChanged, r.Message ?? "Nothing changed."),
            DepthJobOutcome.Cancelled => ToolOutcome.Cancelled,
            _ => new ToolOutcome(ToolResult.Failed, r.Message ?? "The depth edit could not be made."),
        };
    }

    public void AddLassoPoint(NormPoint picture) => _edit.AddLassoPoint(picture);
    public void SetTarget(double depthMm)
    {
        _edit.SetTargetDepth((float)depthMm);
        if (_snap.Pending is { } p) _snap = _snap with { Pending = p with { TargetDepthMm = (float)depthMm, ReferencePoint = null } };   // at once; the next snapshot confirms
        Refresh();
    }

    public void PickTarget(NormPoint picture)
    {
        _edit.PickTargetDepth(picture);
        if (_snap.Pending is { } p) _snap = _snap with { Pending = p with { TargetDepthMm = null, ReferencePoint = picture } };
        Refresh();
    }

    public AppPending? Pending => _snap is { Pending: { } p } sn
        ? new AppPending(p.Applicator switch { MatchApplicator.Lasso => MatchMode.Lasso, MatchApplicator.QuickSelect => MatchMode.QuickSelect, _ => MatchMode.Brush },
                         p.HasArea, p.TargetDepthMm, p.ReferencePoint, p.LassoPoints,
                         p.SelectedPixels > 0 && sn.SelectionSize.Width > 0 ? p.SelectedPixels / ((double)sn.SelectionSize.Width * sn.SelectionSize.Height) : double.NaN)
        : null;

    public ITileSource? MaskLayer => null;

    /// <summary>The selection tint's strength (its alpha: 0.45 at 1).</summary>
    public double SelectionOpacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0.1, 1);
            try { _depth.SelectionColour = new[] { SelectionBlue[0], SelectionBlue[1], SelectionBlue[2], (float)(0.45 * _opacity) }; }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { }
            Changed?.Invoke();
        }
    }

    public Task<ToolOutcome> ApplyPendingAsync() => Run(_edit.Apply());
    public void CancelPending() { _edit.CancelPending(); Refresh(); }

    public ToolActivity Activity
    {
        get
        {
            var a = _snap.Activity;
            if (!a.Busy) return ToolActivity.Idle;
            double p = a.Fraction ?? (a.EstimatedMs is double est && est > 0 ? Math.Min(0.99, a.ElapsedMs / est) : double.NaN);
            return new ToolActivity(true, a.Phase ?? a.Label ?? "Working", p, a.CanCancel, a.EstimatedMs, a.Queued);
        }
    }

    public void CancelActivity() => _edit.CancelAll();

    // ================================================================================================================ history and versions

    static readonly Regex OfCount = new(@"(\d+)\s+of\s+(\d+)", RegexOptions.CultureInvariant);

    public HistoryStatus HistoryStatus
    {
        get
        {
            var info = _snap.History;
            switch (info.State)
            {
                case DepthHistoryState.Rebuilding:
                {
                    // The counts come with the list
                    int done = info.RebuiltSteps ?? 0, total = info.StepsToRebuild ?? 0;
                    if (total == 0 && _snap.Activity is { Busy: true, Phase: { } phase } && OfCount.Match(phase) is { Success: true } m)
                    { done = int.Parse(m.Groups[1].Value); total = int.Parse(m.Groups[2].Value); }
                    // the library counts the steps the panel lists from the earlier session; the bar follows its fraction
                    double frac = info.Progress ?? (total > 0 ? Math.Clamp((double)done / total, 0, 1) : double.NaN);
                    return new HistoryStatus(HistoryState.Rebuilding, done, total, frac);
                }
                case DepthHistoryState.Unreliable:
                    return new HistoryStatus(HistoryState.Unreliable, Message: info.Reason);
                default:
                    return HistoryStatus.Ready;
            }
        }
    }

    public bool HasDepthEdits => _snap.Active.EditCount > 0;
    public Task<ToolOutcome> ResetDepthEditsAsync() => Run(_edit.Reset());

    /// <summary>The page's names from the stable ids: "Clean", "Lux edits", "Lux edits 2", "From Lumen".</summary>
    internal static string NameOf(DepthVersion v) => v.Kind switch
    {
        DepthVersionKind.Clean => "Clean",
        DepthVersionKind.LumenImport => "From Lumen",
        _ => v.Id.StartsWith("lux-", StringComparison.Ordinal) && int.TryParse(v.Id[4..], out int n) ? $"Lux edits {n}" : v.Id == "lux" ? "Lux edits" : v.Name,
    };

    public IReadOnlyList<DepthVersionInfo> Versions => _snap.Versions.Select(v => new DepthVersionInfo(v.Id,
        v.Kind switch { DepthVersionKind.Clean => AppVersionKind.Clean, DepthVersionKind.LumenImport => AppVersionKind.Lumen, _ => AppVersionKind.Lux },
        NameOf(v), v.EditCount, v.Active,
        v.Kind switch
        {
            DepthVersionKind.Clean => "Lux's own depth, with no depth edits",
            DepthVersionKind.LumenImport => (v.EditCount == 1 ? "1 depth edit" : $"{v.EditCount} depth edits") + " · imported from Lumen" + (v.NewerToneStyle ? " · Lumen's newer tone style" : ""),
            _ => v.EditCount == 0 ? "No depth edits yet" : v.EditCount == 1 ? "1 depth edit" : $"{v.EditCount} depth edits",
        }, v.HistoryUnreliable)).ToList();

    public Task<ToolOutcome> SwitchVersionAsync(string id) => Run(_edit.SwitchVersion(id));
    public Task<ToolOutcome> StartCleanVersionAsync() => Run(_edit.StartCleanVersion());
    public bool CanImportLumen => _snap.LumenPath != null && _snap.Versions.All(v => v.Kind != DepthVersionKind.LumenImport);
    public string LumenImportWarning => _edit.LumenImportWarning;
    public Task<ToolOutcome> ImportLumenAsync(bool newerToneStyle) => Run(_edit.ImportLumenSession(newerToneStyle));

    public void Dispose()
    {
        _depth.StatusChanged -= OnAny;
        _depth.Changed -= OnAny;
        _edit.Changed -= OnAny;
        _edit.ActivityChanged -= OnAny;
        _history.Changed -= OnAny;
    }
}
