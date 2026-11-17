using System.Collections.Immutable;
using Avalonia.Threading;
using Lux.App.Develop;
using Lux.App.Viewer;
using Lux.Library;
using DepthStatusInfo = Lux.App.Develop.DepthStatusInfo;
using ToolOptions = Lux.App.Develop.ToolOptions;
using PendingArea = Lux.App.Develop.PendingArea;
using VersionKind = Lux.App.Develop.VersionKind;

namespace Lux.App.Fake;

/// <summary>A depth version of the fake (clean, Lux edits, from Lumen).</summary>
internal sealed record FakeVersion(string Id, VersionKind Kind, string Name);

/// <summary>The depth part of a fake editing state (one per history step): the depth effect and each version's edits.</summary>
/// <param name="FocusCapture">The clicked focus point in capture coordinates (normalized), null for the default focus or a typed value.</param>
internal sealed record FakeDepthState(double? FNumber, double FocusMm, bool FocusDefault, NormPoint? FocusCapture, string Version,
                                      ImmutableDictionary<string, ImmutableList<FakeDepthOp>> Ops, ImmutableList<FakeVersion> Versions)
{
    public ImmutableList<FakeDepthOp> CurrentOps => Ops.TryGetValue(Version, out var l) ? l : ImmutableList<FakeDepthOp>.Empty;
    public FakeVersion CurrentVersion => Versions.FirstOrDefault(v => v.Id == Version) ?? Versions[0];
    public FakeDepthState WithOps(ImmutableList<FakeDepthOp> ops) => this with { Ops = Ops.SetItem(Version, ops) };
    /// <summary>The depth effect uses the Lux rendering (false = Lumen's renderer).</summary>
    public bool LuxRendering { get; init; }
}

/// <summary>
/// The fake's depth (design time and tests) behind <see cref="IDepthSession"/>, mirroring the library's depth contract: the depth map of
/// <see cref="FakeDepthMap"/>, the depth effect drawn by the fake tiles (<see cref="FakeEffect"/>), the depth view, the focal-region shade
/// (outside the focusable band) and the selection as tile layers, Lumen's tools with simulated times (<see cref="FakeTimings.ToolScale"/>),
/// progress, cancellation and "nothing changed" reasons, the pending match-depth selection with a typed or picked target, versions, and the
/// history steps through its <see cref="FakeDevelopSession"/> (one combined history). Readiness follows the library item's analysis.
/// </summary>
public sealed class FakeDepthSession : IDepthSession, IDisposable
{
    readonly FakeDevelopSession _s;
    readonly FakeDevelopService _svc;
    readonly FakeTileSource _tiles;
    readonly FakeTimings _t;
    readonly int _index, _orientation;
    readonly FakeLayerSource _view, _shade, _mask;
    readonly SemaphoreSlim _toolGate = new(1, 1);
    readonly List<(bool Add, IReadOnlyList<(float X, float Y)> Points, float Radius)> _strokes = new();
    readonly List<NormPoint> _lasso = new();
    readonly Dictionary<ImmutableList<FakeDepthOp>, float[]> _replayed = new(ReferenceEqualityComparer.Instance);
    FakeDepthMap? _map;
    float[]? _depth;
    ImmutableList<FakeDepthOp>? _depthOps;
    bool _shadeOn;
    (double? F, double Focus)? _drag;
    (double? F, double Focus) _dragStart;
    DepthToolKind? _tool;
    MatchMode _mode = MatchMode.Brush;
    ToolOptions _options = new();
    byte[]? _pendingMask;
    PendingArea? _pending;
    double? _targetMm;
    NormPoint? _reference;
    double _selectionOpacity = 1;
    ToolActivity _activity = ToolActivity.Idle;
    CancellationTokenSource? _activityCts;
    bool _disposed;

    internal FakeDepthSession(FakeDevelopSession s, FakeDevelopService svc, FakeTileSource tiles, FakeTimings timings)
    {
        _s = s; _svc = svc; _tiles = tiles; _t = timings;
        (_index, _orientation) = svc.Library.SceneOf(s.Item.Id);
        _view = new FakeLayerSource(tiles, (st, t) => st is LayerState l ? FakeLayerSource.PaintDepth(l.Depth, l.Map, l.Near, l.Far, t) : null, timings, "depth", sharesRequest: true);
        _shade = new FakeLayerSource(tiles, (st, t) => st is LayerState l ? FakeLayerSource.PaintShade(l.Depth, l.Map, l.Near, l.Far, t) : null, timings, "shade");
        _mask = new FakeLayerSource(tiles, (st, t) => st is MaskState m ? FakeLayerSource.PaintMask(m.Mask, m.Map, t) : null, timings, "mask");
        if (IsAnalysed) EnsureMap();
    }

    sealed record LayerState(float[] Depth, FakeDepthMap Map, double Near, double Far);
    sealed record MaskState(byte[] Mask, FakeDepthMap Map);

    bool IsAnalysed => _s.Item.DepthState == DepthState.Analysed;
    internal FakeDepthMap? Map => _map;
    /// <summary>The edited depth of the state shown (tests).</summary>
    internal float[]? CurrentDepth => _depth;

    FakeDepthMap EnsureMap()
    {
        if (_map != null) return _map;
        var it = _s.Item;
        var size = _tiles.Capture;
        _map = FakeDepthMap.For(_index, it.IsStacked, it.IsTelephoto, _orientation, size.Width, size.Height);
        return _map;
    }

    /// <summary>The library item changed (an analysis finished): readiness follows.</summary>
    internal void ItemChanged()
    {
        if (IsAnalysed && _map == null)
        {
            EnsureMap();
            var st = _s.DepthState;
            if (st.FocusDefault) st = st with { FocusMm = _map!.DefaultFocusMm };
            _s.ReplaceDepthQuietly(st);
            Refresh(st, drag: false);
        }
        Changed?.Invoke();
    }

    // ================================================================================================================ readiness

    public DepthStatusInfo Status
    {
        get
        {
            var it = _s.Item;
            if (it.DepthState == DepthState.Analysed) return _map == null ? new DepthStatusInfo(DepthReadiness.Analysing, 1) : DepthStatusInfo.Ready;
            if (it.ActiveJob == JobKind.Analysis) return new DepthStatusInfo(DepthReadiness.Analysing, it.ActiveJobState == JobState.Running ? it.ActiveJobProgress : double.NaN);
            if (it.DepthState == DepthState.Failed) return new DepthStatusInfo(DepthReadiness.Failed, Message: it.Error ?? "The analysis of this photo failed.");
            return new DepthStatusInfo(DepthReadiness.NotAnalysed);
        }
    }

    public void AnalyseNow()
    {
        var id = _s.Item.Id;
        var lib = _svc.Library;
        _ = Task.Run(() => lib.AnalyseNow(new[] { id }));
    }

    // ================================================================================================================ the effect

    double OffF => _s.Item.IsTelephoto ? 8.0 : 15.2;

    public DepthEffectRange EffectRange => _map is { } m
        ? new DepthEffectRange(2.0, OffF, Math.Floor(m.NearestMm * 0.9 / 100) * 100, m.FarthestMm, m.DefaultFocusMm)
        : DepthEffectRange.Placeholder with { AllInFocusFNumber = OffF };

    /// <summary>The focusable band (Lumen reports one per photo): the fake's runs from the nearest depth to 50 m, so the sky and the far hills
    /// are shaded and refused.</summary>
    public (double NearMm, double FarMm)? FocusableRange => _map is { } m ? (EffectRange.NearestMm, Math.Min(m.FarthestMm, 50_000)) : null;

    double? Norm(double? f) => f is double v && v < OffF - 1e-6 ? Math.Max(2.0, v) : null;

    public double? FNumber => _drag is { } d ? d.F : Norm(_s.DepthState.FNumber);
    public double FocusMm => _drag is { } d ? d.Focus : _s.DepthState.FocusMm;
    public bool FocusIsDefault => _drag == null && _s.DepthState.FocusDefault;

    public NormPoint? FocusPoint
    {
        get
        {
            if (_drag != null || _s.DepthState.FocusCapture is not { } c) return null;
            var p = _tiles.CaptureToPicture(c);
            return p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1 ? p : null;
        }
    }

    public (double NearMm, double FarMm)? InFocus => FNumber is double f && Status.IsReady ? Range(f, FocusMm) : null;

    /// <summary>The sharp range (Lumen's in-focus formula, simplified): t grows with the f-number; the far end is open past t = 1.</summary>
    internal static (double, double) Range(double f, double focus)
    {
        double t = 0.27 * f / 2;
        double near = focus / (1 + t), far = t >= 1 ? double.PositiveInfinity : Math.Min(focus / (1 - t), focus * 10);
        return (near, far);
    }

    public void BeginEffectDrag() { _dragStart = (FNumber, FocusMm); _drag = _dragStart; }

    public void DragEffect(double? fNumber, double focusMm)
    {
        if (!Status.IsReady) return;
        _drag = (Norm(fNumber), Math.Clamp(focusMm, EffectRange.NearestMm, EffectRange.FarthestMm));
        PushEffect(drag: true);
        Changed?.Invoke();
    }

    public void EndEffectDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        if (d.F == _dragStart.F && Math.Abs(d.Focus - _dragStart.Focus) < 1e-6) { PushEffect(drag: false); Changed?.Invoke(); return; }
        SetEffect(d.F, d.Focus);
    }

    public void SetEffect(double? fNumber, double focusMm)
    {
        if (!Status.IsReady) return;
        var st = _s.DepthState;
        double? f = Norm(fNumber);
        double focus = Math.Clamp(focusMm, EffectRange.NearestMm, EffectRange.FarthestMm);
        bool fChanged = f != Norm(st.FNumber), focusChanged = Math.Abs(focus - st.FocusMm) > 0.5;
        if (!fChanged && !focusChanged) { PushEffect(false); Changed?.Invoke(); return; }
        // the library makes one step per value ("Depth effect", "Focus")
        if (fChanged) { _s.PushDepth(f == null ? "Depth effect off" : $"Depth effect {DepthText.FNumber(f)}", HistoryKind.DepthEffect, st = st with { FNumber = f }); }
        if (focusChanged) _s.PushDepth($"Focus {DepthText.Distance(focus)}", HistoryKind.DepthEffect, st with { FocusMm = focus, FocusDefault = false, FocusCapture = null });
    }

    public bool LuxRendering => _s.DepthState.LuxRendering;

    public void SetLuxRendering(bool on)
    {
        if (!Status.IsReady) return;
        var st = _s.DepthState;
        if (st.LuxRendering == on) return;
        _s.PushDepth(on ? "Lux rendering" : "Lumen rendering", HistoryKind.DepthEffect, st with { LuxRendering = on });
    }

    public bool CanFocusAt(NormPoint picture) => DepthAt(picture) is double d && FocusableRange is { } r && d >= r.NearMm && d <= r.FarMm;

    public async Task<PointResult> FocusAtAsync(NormPoint picture, CancellationToken cancel = default)
    {
        if (!Status.IsReady) return new PointResult(false, null, "The depth map is not ready yet.");
        if (_t.ToolScale > 0) await Task.Delay(TimeSpan.FromMilliseconds(40 * _t.ToolScale), cancel);
        var d = DepthAt(picture);
        if (d is not double mm || FocusableRange is not { } r || mm < r.NearMm || mm > r.FarMm)
            return new PointResult(false, d, "This area cannot be brought into focus. Choose a point in the unshaded area.");
        var st = _s.DepthState;
        _s.PushDepth($"Focus {DepthText.Distance(mm)}", HistoryKind.DepthEffect, st with { FocusMm = mm, FocusDefault = false, FocusCapture = _tiles.PictureToCapture(picture) });
        return new PointResult(true, mm);
    }

    public void ResetFocus()
    {
        if (!Status.IsReady) return;
        var st = _s.DepthState;
        if (st.FocusDefault) return;
        _s.PushDepth("Default focus", HistoryKind.DepthEffect, st with { FocusMm = EffectRange.DefaultFocusMm, FocusDefault = true, FocusCapture = null });
    }

    public void ResetEffect()
    {
        if (!Status.IsReady) return;
        var st = _s.DepthState;
        if (st.FNumber == null && st.FocusDefault && !st.LuxRendering) return;
        _s.PushDepth("Reset depth effect", HistoryKind.DepthEffect, st with { FNumber = null, FocusMm = EffectRange.DefaultFocusMm, FocusDefault = true, FocusCapture = null, LuxRendering = false });
    }

    /// <summary>The fake tiles' effect follows the state shown (and the drag).</summary>
    void PushEffect(bool drag)
    {
        if (_map == null || _depth == null) { _tiles.SetEffect(null, drag); return; }
        _tiles.SetEffect(FNumber is double f ? new FakeEffect(f, OffF, FocusMm, _depth, _map) : null, drag);
    }

    LayerState? ShadeState() => _map != null && _depth != null && _shadeOn && FocusableRange is { } r ? new LayerState(_depth, _map, r.NearMm, r.FarMm) : null;

    /// <summary>A history state is shown (a step, an undo, a jump): the edited depth for its edits (replayed, kept per edit list), then the
    /// effect and the layers.</summary>
    internal void Refresh(FakeDepthState st, bool drag)
    {
        if (_map == null) { _tiles.SetEffect(null, drag); return; }
        var ops = st.CurrentOps;
        if (!ReferenceEquals(ops, _depthOps))
        {
            if (!_replayed.TryGetValue(ops, out var d))
            {
                d = FakeDepthOps.Replay(_map, ops);
                Remember(ops, d);
            }
            _depth = d;
            _depthOps = ops;
            _view.Bump(new LayerState(d, _map, EffectRange.NearestMm, EffectRange.FarthestMm));
            _shade.Bump(ShadeState());
        }
        PushEffect(drag);
    }

    void Remember(ImmutableList<FakeDepthOp> ops, float[] depth)
    {
        if (_replayed.Count > 24) _replayed.Clear();
        _replayed[ops] = depth;
    }

    // ================================================================================================================ views

    public bool ShadeOutsideFocus
    {
        get => _shadeOn;
        set { if (_shadeOn == value) return; _shadeOn = value; _shade.Bump(ShadeState()); Changed?.Invoke(); }
    }

    public ITileSource? ShadeLayer => _shade;
    public ITileSource DepthView => _view;
    internal FakeLayerSource MaskSource => _mask;

    (double X, double Y)? ToMap(NormPoint picture)
    {
        if (_map == null) return null;
        var c = _tiles.PictureToCapture(picture);
        if (c.X < 0 || c.X > 1 || c.Y < 0 || c.Y > 1) return null;
        return (c.X * _map.W, c.Y * _map.H);
    }

    public double? DepthAt(NormPoint picture)
    {
        if (_depth == null || ToMap(picture) is not { } m) return null;
        float d = FakeDepthMap.Sample(_depth, _map!.W, _map.H, m.X, m.Y);
        return float.IsFinite(d) ? d : null;
    }

    public (double NearMm, double FarMm) DepthViewRange => (EffectRange.NearestMm, EffectRange.FarthestMm);

    // ================================================================================================================ tools

    public DepthToolKind? Tool => _tool;
    public MatchMode Mode => _mode;
    public ToolOptions Options => _options;

    public void SetTool(DepthToolKind? tool, MatchMode mode = MatchMode.Brush)
    {
        if (_tool == tool && _mode == mode) return;
        if (_tool != tool || _mode != mode) CancelPending();
        _tool = tool;
        _mode = mode;
        Changed?.Invoke();
    }

    public void SetOptions(ToolOptions options)
    {
        double min = _tool == DepthToolKind.MatchDepth && _mode == MatchMode.Lasso ? 0 : ToolOptions.MinSize;
        _options = options with { BrushSize = Math.Clamp(options.BrushSize, min, ToolOptions.MaxSize) };
        Changed?.Invoke();
    }

    /// <summary>Lumen's radius: size × 0.001 of the capture's width.</summary>
    public double BrushDiameterPx(double size) => 2 * size * 0.001 * _tiles.Capture.Width;

    public IToolStroke BeginStroke()
    {
        if (!Status.IsReady || _map == null) throw new InvalidOperationException("The depth map is not ready yet: analyze the photo first.");
        if (_tool == null) throw new InvalidOperationException("Choose a depth tool first.");
        return new Stroke(this, _tool.Value, _mode, _options);
    }

    /// <summary>The next tool apply fails with this reason (tests and the progress screenshot).</summary>
    public string? FailNext { get; set; }
    /// <summary>Multiplies the simulated tool times (the slow-tool screenshot).</summary>
    public double SlowFactor { get; set; } = 1;

    sealed class Stroke(FakeDepthSession d, DepthToolKind tool, MatchMode mode, ToolOptions options) : IToolStroke
    {
        readonly List<(float X, float Y)> _points = new();
        readonly List<NormPoint> _picture = new();
        bool _done;

        public void Add(IReadOnlyList<NormPoint> points)
        {
            if (_done) return;
            foreach (var p in points)
            {
                _picture.Add(p);
                if (d.ToMap(p) is { } m) _points.Add(((float)m.X, (float)m.Y));
            }
        }

        public void Cancel() => _done = true;

        public Task<ToolOutcome> EndAsync()
        {
            if (_done) return Task.FromResult(ToolOutcome.Cancelled);
            _done = true;
            if (_points.Count == 0) return Task.FromResult(new ToolOutcome(ToolResult.NothingChanged, "The stroke was outside the photo."));
            return d.FinishStrokeAsync(tool, mode, options, _points.ToImmutableArray(), _picture);
        }
    }

    float RadiusMap(double size) => (float)(size * 0.001 * _tiles.Capture.Width / FakeDepthMap.Divisor);

    async Task<ToolOutcome> FinishStrokeAsync(DepthToolKind tool, MatchMode mode, ToolOptions options, ImmutableArray<(float X, float Y)> pts, IReadOnlyList<NormPoint> picture)
    {
        var map = _map!;
        if (tool == DepthToolKind.MatchDepth)
        {
            switch (mode)
            {
                case MatchMode.QuickSelect:
                {
                    var entry = (options.AddToSelection, (IReadOnlyList<(float X, float Y)>)pts, RadiusMap(options.BrushSize));
                    _strokes.Add(entry);
                    // strokes run in order; each sees every stroke drawn before it (a newer one waits its turn, it does not abandon this one)
                    var (r, mask) = await RunAsync("Updating the selection", 250, _ => FakeDepthOps.QuickSelect(map, _strokes.TakeWhile(x => !ReferenceEquals(x.Points, entry.Item2)).Append(entry).ToList())).ConfigureAwait(true);
                    if (r.Result != ToolResult.Applied) { _strokes.Remove(entry); return r; }
                    SetPending(MatchMode.QuickSelect, mask, Array.Empty<NormPoint>());
                    return ToolOutcome.Applied;
                }
                case MatchMode.Lasso:
                {
                    _lasso.Clear();
                    _lasso.AddRange(picture);
                    UpdateLasso();
                    return ToolOutcome.Applied;
                }
                default:
                {
                    if (pts.Length < 2) return new ToolOutcome(ToolResult.NothingChanged, "Paint a stroke (a single click is not enough).");
                    var (r, mask) = await RunAsync(options.FollowColour ? "Following the colors" : "Painting the area", options.FollowColour ? 900 : 40,
                        _ => FakeDepthOps.BrushMask(map, pts, RadiusMap(options.BrushSize), options.FollowColour)).ConfigureAwait(true);
                    if (r.Result != ToolResult.Applied) return r;
                    SetPending(MatchMode.Brush, mask, Array.Empty<NormPoint>());
                    return ToolOutcome.Applied;
                }
            }
        }
        if (pts.Length < 2) return new ToolOutcome(ToolResult.NothingChanged, "Paint a stroke along the area (a single click is not enough).");
        var kind = tool == DepthToolKind.RefineEdge ? FakeStroke.RefineEdge : FakeStroke.BlendArea;
        var op = new StrokeDepthOp(kind, pts, RadiusMap(options.BrushSize), 0, false, tool == DepthToolKind.RefineEdge ? "Refine edge" : "Blend area");
        return await ApplyOpAsync(op, tool == DepthToolKind.RefineEdge ? "Refining the edge" : "Blending the area", tool == DepthToolKind.RefineEdge ? 1800 : 700).ConfigureAwait(true);
    }

    async Task<ToolOutcome> ApplyOpAsync(FakeDepthOp op, string doing, int ms)
    {
        var map = _map!;
        var depth = _depth!;
        var (r, result) = await RunAsync(doing, ms, _ =>
        {
            var d = (float[])depth.Clone();
            return (Depth: d, Reason: FakeDepthOps.Apply(map, d, op));
        }).ConfigureAwait(true);
        if (r.Result != ToolResult.Applied) return r;
        if (result.Reason != null) return new ToolOutcome(ToolResult.NothingChanged, result.Reason);
        await Dispatcher.UIThread.InvokeAsync(() => Commit(op, result.Depth));
        return ToolOutcome.Applied;
    }

    /// <summary>One history step for a tool; editing the clean version starts a new Lux version (the clean one stays clean).</summary>
    void Commit(FakeDepthOp op, float[] depth)
    {
        var st = _s.DepthState;
        if (st.CurrentVersion.Kind == VersionKind.Clean)
        {
            var v = NewLuxVersion(st);
            st = st with { Versions = st.Versions.Add(v), Version = v.Id, Ops = st.Ops.SetItem(v.Id, ImmutableList<FakeDepthOp>.Empty) };
        }
        var ops = st.CurrentOps.Add(op);
        Remember(ops, depth);
        _s.PushDepth(op.Label, HistoryKind.DepthEdit, st.WithOps(ops));
    }

    static FakeVersion NewLuxVersion(FakeDepthState st)
    {
        int n = st.Versions.Count(v => v.Kind == VersionKind.Lux) + 1;
        return new FakeVersion(n == 1 ? "lux" : $"lux-{n}", VersionKind.Lux, n == 1 ? "Lux edits" : $"Lux edits {n}");
    }

    /// <summary>Run a job off the UI thread with the simulated time (progress every 50 ms, cancelable); one at a time, in call order.</summary>
    int _queued, _cancelGeneration;

    async Task<(ToolOutcome Outcome, T Result)> RunAsync<T>(string label, int ms, Func<CancellationToken, T> work)
    {
        int generation = _cancelGeneration;
        Interlocked.Increment(ref _queued);
        if (_activity.Running) SetActivity(_activity with { Queued = _queued });   // the running job left the count when it took the gate
        await _toolGate.WaitAsync().ConfigureAwait(true);
        Interlocked.Decrement(ref _queued);
        // stopped while it waited (CancelActivity drops the queued jobs as the library's CancelAll does)
        if (generation != _cancelGeneration) { _toolGate.Release(); return (ToolOutcome.Cancelled, default!); }
        var cts = _activityCts = new CancellationTokenSource();
        try
        {
            double total = ms * _t.ToolScale * SlowFactor;
            SetActivity(new ToolActivity(true, label, 0, CanCancel: true, EstimatedMs: total, Queued: _queued));
            var token = cts.Token;
            var result = await Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalMilliseconds < total)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(Math.Min(50, Math.Max(1, (int)(total - sw.Elapsed.TotalMilliseconds))), token).ConfigureAwait(false);
                    SetActivity(new ToolActivity(true, label, Math.Min(0.99, sw.Elapsed.TotalMilliseconds / Math.Max(1, total)), CanCancel: true, EstimatedMs: total, Queued: _queued));
                }
                token.ThrowIfCancellationRequested();
                return work(token);
            }, token).ConfigureAwait(true);
            if (FailNext is { } reason) { FailNext = null; return (new ToolOutcome(ToolResult.Failed, reason), default!); }
            return (ToolOutcome.Applied, result);
        }
        catch (OperationCanceledException) { return (ToolOutcome.Cancelled, default!); }
        finally
        {
            SetActivity(ToolActivity.Idle);
            if (_activityCts == cts) _activityCts = null;
            cts.Dispose();
            _toolGate.Release();
        }
    }

    void SetActivity(ToolActivity a)
    {
        _activity = a;
        if (!_disposed) Changed?.Invoke();
    }

    public ToolActivity Activity => _activity;
    public void CancelActivity() { Interlocked.Increment(ref _cancelGeneration); try { _activityCts?.Cancel(); } catch (ObjectDisposedException) { } }

    void SetPending(MatchMode mode, byte[]? mask, IReadOnlyList<NormPoint> lasso)
    {
        _pendingMask = mask;
        int area = mask?.Count(b => b > 127) ?? 0;
        _pending = new PendingArea(mode, area > 0, _targetMm, _reference, lasso.ToList(), mask == null ? 0 : area / (double)mask.Length);
        _mask.Bump(mask != null && _map != null ? new MaskState(mask, _map) : null);
        Changed?.Invoke();
    }

    void UpdateLasso()
    {
        if (_map == null) return;
        var pts = _lasso.Select(ToMap).Where(m => m != null).Select(m => ((float)m!.Value.X, (float)m.Value.Y)).ToList();
        double feather = Math.Clamp(_options.BrushSize * 0.001 * 10, 0, 1);
        var mask = pts.Count >= 3 ? FakeDepthOps.Polygon(_map, pts, (float)(feather * 0.05 * _map.W)) : null;
        SetPending(MatchMode.Lasso, mask, _lasso);
    }

    public void AddLassoPoint(NormPoint picture)
    {
        if (_map == null || _tool != DepthToolKind.MatchDepth || _mode != MatchMode.Lasso) return;
        _lasso.Add(picture);
        UpdateLasso();
    }

    public void SetTarget(double depthMm)
    {
        _targetMm = Math.Max(100, depthMm);
        _reference = null;
        if (_pending is { } p) _pending = p with { TargetMm = _targetMm, ReferencePoint = null };
        Changed?.Invoke();
    }

    public void PickTarget(NormPoint picture)
    {
        _reference = picture;
        _targetMm = null;
        if (_pending is { } p) _pending = p with { TargetMm = null, ReferencePoint = picture };
        Changed?.Invoke();
    }

    /// <summary>The target as typed or picked (null before one is chosen); picked depths are read at the point now.</summary>
    internal double? TargetDepth => _targetMm ?? (_reference is { } r ? DepthAt(r) : null);

    public PendingArea? Pending => _pending ?? (_targetMm != null || _reference != null ? new PendingArea(_mode, false, _targetMm, _reference, Array.Empty<NormPoint>()) : null);
    public ITileSource? MaskLayer => _mask;
    public double SelectionOpacity { get => _selectionOpacity; set { _selectionOpacity = Math.Clamp(value, 0.1, 1); Changed?.Invoke(); } }

    public async Task<ToolOutcome> ApplyPendingAsync()
    {
        if (_pending is not { HasArea: true } p || _pendingMask is not { } mask)
            return new ToolOutcome(ToolResult.Failed, "Nothing is selected yet: paint, draw or select the area first.");
        if (!Status.IsReady) return new ToolOutcome(ToolResult.Failed, "The depth map is not ready yet.");
        if (TargetDepth is not double depthMm)
            return new ToolOutcome(ToolResult.Failed, _reference != null ? "There is no depth at the picked point: pick another one, or type the depth." : "Choose the depth first: type it, or pick it from the photo.");
        string d = DepthText.Distance(depthMm);
        var op = new MaskDepthOp(mask, (float)depthMm, p.Mode switch { MatchMode.Lasso => $"Match depth {d} · lasso", MatchMode.QuickSelect => $"Quick select · {d}", _ => $"Match depth {d}" });
        var r = await ApplyOpAsync(op, "Setting the depth", 120).ConfigureAwait(true);
        if (r.Changed) DropSelection();
        return r;
    }

    void DropSelection()
    {
        _strokes.Clear();
        _lasso.Clear();
        _pendingMask = null;
        _pending = null;
        _mask.Bump(null);
        Changed?.Invoke();
    }

    public void CancelPending() => DropSelection();

    // ================================================================================================================ history and versions

    public HistoryStatus HistoryStatus => _s.DepthHistoryStatus;
    public bool HasDepthEdits => !_s.DepthState.CurrentOps.IsEmpty;

    public Task<ToolOutcome> ResetDepthEditsAsync()
    {
        var st = _s.DepthState;
        if (st.CurrentOps.IsEmpty) return Task.FromResult(new ToolOutcome(ToolResult.NothingChanged, "This version has no depth edits."));
        DropSelection();
        _s.PushDepth("Reset depth edits", HistoryKind.DepthEdit, st.WithOps(ImmutableList<FakeDepthOp>.Empty), barrier: true);
        return Task.FromResult(ToolOutcome.Applied);
    }

    public IReadOnlyList<DepthVersionInfo> Versions
    {
        get
        {
            var st = _s.DepthState;
            return st.Versions.Select(v =>
            {
                int n = st.Ops.TryGetValue(v.Id, out var l) ? l.Count : 0;
                string note = v.Kind switch
                {
                    VersionKind.Clean => "Lux's own depth, with no depth edits",
                    VersionKind.Lumen => n == 1 ? "1 depth edit imported from Lumen" : $"{n} depth edits imported from Lumen",
                    VersionKind.Refined => Lux.App.Develop.ContractDepthSession.RefinedNote + (n == 0 ? "" : n == 1 ? " · 1 depth edit" : $" · {n} depth edits"),
                    _ => n == 0 ? "No depth edits yet" : n == 1 ? "1 depth edit" : $"{n} depth edits",
                };
                return new DepthVersionInfo(v.Id, v.Kind, v.Name, n, v.Id == st.Version, note, v.Id == st.Version && _s.DepthHistoryStatus.State == HistoryState.Unreliable);
            }).ToList();
        }
    }

    public async Task<ToolOutcome> SwitchVersionAsync(string id)
    {
        var st = _s.DepthState;
        if (st.Version == id || st.Versions.All(v => v.Id != id)) return new ToolOutcome(ToolResult.NothingChanged);
        DropSelection();
        var (r, _) = await RunAsync("Switching the depth version", 150, _ => 0).ConfigureAwait(true);
        if (r.Result != ToolResult.Applied) return r;
        _s.RestartHistory(st with { Version = id });
        return ToolOutcome.Applied;
    }

    public async Task<ToolOutcome> StartCleanVersionAsync()
    {
        var st = _s.DepthState;
        DropSelection();
        var (r, _) = await RunAsync("Starting a clean version", 150, _ => 0).ConfigureAwait(true);
        if (r.Result != ToolResult.Applied) return r;
        var v = NewLuxVersion(st);
        _s.RestartHistory(st with { Versions = st.Versions.Add(v), Version = v.Id, Ops = st.Ops.SetItem(v.Id, ImmutableList<FakeDepthOp>.Empty) });
        return ToolOutcome.Applied;
    }

    public bool CanImportLumen => _s.Item.HasLumenSession && _s.DepthState.Versions.All(v => v.Kind != VersionKind.Lumen);

    public string LumenImportWarning => "Lumen saved its depth map in a compact, rounded form. An imported capture uses that depth map throughout, so it looks the way it did in Lumen, but further editing and processing of it starts from less precise depth than a clean Lux start. The rounding is under 1 mm at 2 m, about 15 mm at 10 m and about 0.4 m at 50 m.";

    public async Task<ToolOutcome> ImportLumenAsync(bool newerToneStyle)
    {
        if (!_s.Item.HasLumenSession) return new ToolOutcome(ToolResult.Failed, "There is no Lumen session beside this photo.");
        var st = _s.DepthState;
        if (st.Versions.Any(v => v.Kind == VersionKind.Lumen)) return await SwitchVersionAsync(st.Versions.First(v => v.Kind == VersionKind.Lumen).Id).ConfigureAwait(true);
        if (_map == null) return new ToolOutcome(ToolResult.Failed, "The depth map is not ready yet.");
        DropSelection();
        var map = _map;
        var (r, ops) = await RunAsync("Importing the Lumen session", 600, _ => FakeDepthOps.Synthetic(map, 3, 3200)).ConfigureAwait(true);
        if (r.Result != ToolResult.Applied) return r;
        var v = new FakeVersion("lumen-import", VersionKind.Lumen, "From Lumen");
        _s.RestartHistory(st with { Versions = st.Versions.Add(v), Version = v.Id, Ops = st.Ops.SetItem(v.Id, ops) }, imported: true);
        return ToolOutcome.Applied;
    }

    /// <summary>The refined version of the fake: a version of its own with no edits, made after a few seconds of "network" work.</summary>
    public async Task<ToolOutcome> RefineDepthAsync()
    {
        var st = _s.DepthState;
        if (st.Versions.FirstOrDefault(v => v.Kind == VersionKind.Refined) is { } have) return await SwitchVersionAsync(have.Id).ConfigureAwait(true);
        if (_map == null) return new ToolOutcome(ToolResult.Failed, "The depth map is not ready yet.");
        DropSelection();
        var (r, _) = await RunAsync("Refining the depth", RefineMs, _ => 0).ConfigureAwait(true);
        if (r.Result != ToolResult.Applied) return r;
        var v = new FakeVersion("refined", VersionKind.Refined, "Refined");
        _s.RestartHistory(st with { Versions = st.Versions.Add(v), Version = v.Id, Ops = st.Ops.SetItem(v.Id, ImmutableList<FakeDepthOp>.Empty) });
        return ToolOutcome.Applied;
    }
    /// <summary>The simulated time of the refinement, in ms (scaled like every fake tool time).</summary>
    const int RefineMs = 2500;

    public event Action? Changed;

    public void Dispose()
    {
        _disposed = true;
        CancelActivity();
        _view.Dispose(); _shade.Dispose(); _mask.Dispose();
    }

    /// <summary>The initial depth state of a capture: what was saved (this run's earlier sessions, or the fake library's summary).</summary>
    internal static FakeDepthState Initial(FakeDevelopService svc, CaptureItem item, FakeDepthMap? map, out int savedSteps)
    {
        savedSteps = 0;
        if (svc.DepthRecipes.TryGetValue(item.Id, out var kept)) { savedSteps = kept.CurrentOps.Count; return kept; }
        var versions = ImmutableList.Create(new FakeVersion("clean", VersionKind.Clean, "Clean"));
        var ops = ImmutableDictionary<string, ImmutableList<FakeDepthOp>>.Empty.Add("clean", ImmutableList<FakeDepthOp>.Empty);
        string current = "clean";
        var e = item.Edits;
        if (map != null && e.Source != EditSource.None && e.OperationCount > 0)
        {
            bool lumen = e.Source == EditSource.Lumen;
            var v = lumen ? new FakeVersion("lumen-import", VersionKind.Lumen, "From Lumen") : new FakeVersion("lux", VersionKind.Lux, "Lux edits");
            versions = versions.Insert(0, v);
            ops = ops.SetItem(v.Id, FakeDepthOps.Synthetic(map, e.OperationCount, 3200));
            current = v.Id;
            savedSteps = e.OperationCount;
        }
        double focus = e.SavedFocusMm is float fm ? fm : map?.DefaultFocusMm ?? 4000;
        return new FakeDepthState(e.SavedFNumber, focus, e.SavedFocusMm == null, null, current, ops, versions);
    }
}
