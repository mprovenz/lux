using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Store;

namespace Lux.Library.Depth;

/// <summary>Lumen's depth tools (<see cref="IDepthEdit"/>): strokes in picture coordinates mapped to the capture's normalised coordinates, the
/// brush size mapped as Lumen's editor does, the operations run on the worker through the persistence (logged, saved, mirrored).</summary>
internal sealed partial class DepthSession
{
    readonly object _toolLock = new();
    DepthTool? _tool; MatchApplicator _app = MatchApplicator.Lasso; DepthToolOptions _opts = new();
    bool _showPreview = true;
    // the pending match-depth selection (capture coordinates, as the engine takes them)
    List<EditPoint>? _brushPts; readonly List<EditPoint> _lassoPts = new(); readonly List<NormPoint> _lassoPicture = new();
    float? _targetDepth; NormPoint? _refPoint;
    byte[]? _selectionMask;   // copy of the editor's quick-select mask (the overlay reads it)
    long _selectedPixels;
    byte[]? _previewMask;     // the pending stroke's area (Lux's preview), edited-level bytes 0 / 1

    // ================================================================================================================ tool and options

    public DepthTool? Tool { get { lock (_toolLock) return _tool; } }
    public MatchApplicator Applicator { get { lock (_toolLock) return _app; } }
    public DepthToolOptions Options { get { lock (_toolLock) return _opts; } }

    public void SetTool(DepthTool? tool, MatchApplicator applicator = MatchApplicator.Lasso)
    {
        bool wasQuick, isQuick, changed;
        lock (_toolLock)
        {
            wasQuick = _tool == DepthTool.MatchDepth && _app == MatchApplicator.QuickSelect;
            isQuick = tool == DepthTool.MatchDepth && applicator == MatchApplicator.QuickSelect;
            changed = _tool != tool || _app != applicator;
            _tool = tool; _app = applicator;
        }
        // Lumen applies an open crop before any depth tool
        if (tool is not null && _s.EditImpl.CropState != CropState.Hidden) _s.EditImpl.ApplyCrop();
        // the GUI-level pending selection is dropped; the engine's quick-select selection is kept (as Lumen keeps it until a reset)
        if (changed) DropPending(resetQuick: false);
        if (wasQuick != isQuick) UpdateOverlay(committed: true, null);
    }

    public void SetOptions(DepthToolOptions options)
    {
        if (options.RadiusOverride is float r && !(r > 0f && r < 1f)) throw new ArgumentOutOfRangeException(nameof(options), "the radius must lie between 0 and 1");
        if (options.FeatherOverride is float f && !(f >= 0f && f <= 1f)) throw new ArgumentOutOfRangeException(nameof(options), "the feather must lie between 0 and 1");
        lock (_toolLock) _opts = options with { BrushSize = ClampSize(options.BrushSize, _app == MatchApplicator.Lasso && _tool == DepthTool.MatchDepth) };
    }

    /// <summary>Lumen's setter: <c>(float)max(lasso ? 0 : 1, min(100, size))</c>.</summary>
    static float ClampSize(float size, bool lasso) => (float)Math.Max(lasso ? 0.0 : 1.0, Math.Min(100.0, (double)size));

    /// <summary>`brushSize × 0.001f`: a fraction of the capture's width.</summary>
    public float RadiusOf(float brushSize) => brushSize * 0.001f;
    /// <summary>`brushSize × 0.001f × 10.0f`.</summary>
    public float FeatherOf(float brushSize) => brushSize * 0.001f * 10.0f;
    public double BrushRadiusPixels(float brushSize) => (double)RadiusOf(brushSize) * _lv.ExportDims[0].W;

    float RadiusNow { get { lock (_toolLock) return _opts.RadiusOverride ?? RadiusOf(_opts.BrushSize); } }
    float FeatherNow { get { lock (_toolLock) return _opts.FeatherOverride ?? FeatherOf(_opts.BrushSize); } }

    public bool ShowStrokePreview
    {
        get { lock (_toolLock) return _showPreview; }
        set { lock (_toolLock) { if (_showPreview == value) return; _showPreview = value; } UpdateOverlay(true, null); }
    }

    // ================================================================================================================ coordinates

    /// <summary>A picture point → the engine's point (normalised capture coordinates, as floats).</summary>
    EditPoint ToEngine(NormPoint p) { var (x, y) = _s.ViewImpl.ToCapture(p); return new EditPoint((float)x, (float)y); }

    /// <summary>The quick-select region of a scope: the bounding pixel rectangle at the scope's level of the capture region under the visible
    /// picture rectangle (floor / ceiling; values within 1e-6 of a whole pixel are that pixel), clamped to the level.</summary>
    internal RectI ScopeRect(QuickSelectScope scope)
    {
        var v = scope.Visible;
        double cx0 = double.MaxValue, cy0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue;
        foreach (var p in new[] { new NormPoint(v.X, v.Y), new NormPoint(v.Right, v.Y), new NormPoint(v.X, v.Bottom), new NormPoint(v.Right, v.Bottom) })
        {
            var (x, y) = _s.ViewImpl.ToCapture(p);
            cx0 = Math.Min(cx0, x); cy0 = Math.Min(cy0, y); cx1 = Math.Max(cx1, x); cy1 = Math.Max(cy1, y);
        }
        var (w, h) = _lv.ExportDims[scope.Level];
        static int Lo(double v) { double r = Math.Round(v); return Math.Abs(v - r) < 1e-6 ? (int)r : (int)Math.Floor(v); }
        static int Hi(double v) { double r = Math.Round(v); return Math.Abs(v - r) < 1e-6 ? (int)r : (int)Math.Ceiling(v); }
        int x0 = Math.Clamp(Lo(cx0 * w), 0, w), y0 = Math.Clamp(Lo(cy0 * h), 0, h), x1 = Math.Clamp(Hi(cx1 * w), 0, w), y1 = Math.Clamp(Hi(cy1 * h), 0, h);
        return new RectI(x0, y0, x1, y1);
    }

    /// <summary>The default scope: the viewer's last target level and visible part, at a level no finer than keeps the region under 1.2 MP.</summary>
    QuickSelectScope DefaultScope()
    {
        var vis = _s.ViewImpl.LastVisible ?? NormRect.Full;
        int level = Math.Clamp(_s.ViewImpl.Status.TargetLevel, 0, _lv.ExportDims.Length - 1);
        while (level < _lv.ExportDims.Length - 1)
        {
            var (w, h) = _lv.ExportDims[level];
            if (vis.Width * w * vis.Height * h <= 1.2e6) break;
            level++;
        }
        return new QuickSelectScope(level, vis);
    }

    // ================================================================================================================ strokes

    public IDepthStroke BeginStroke(QuickSelectScope? scope = null)
    {
        DepthTool tool; MatchApplicator app;
        lock (_toolLock)
        {
            tool = _tool ?? throw new InvalidOperationException("Choose a depth tool first.");
            app = _app;
        }
        if (Status.Availability != DepthAvailability.Ready) throw new InvalidOperationException("The depth of this photo is not ready yet.");
        return new Stroke(this, tool, app, tool == DepthTool.MatchDepth && app == MatchApplicator.QuickSelect ? scope ?? DefaultScope() : null);
    }

    sealed class Stroke : IDepthStroke
    {
        readonly DepthSession _d; readonly MatchApplicator _app; readonly QuickSelectScope? _scope;
        readonly List<NormPoint> _pts = new(); readonly List<EditPoint> _eng = new();
        bool _done; long _lastPreview;
        public DepthTool Tool { get; }
        public IReadOnlyList<NormPoint> Points { get { lock (_pts) return _pts.ToList(); } }
        public Stroke(DepthSession d, DepthTool tool, MatchApplicator app, QuickSelectScope? scope) { _d = d; Tool = tool; _app = app; _scope = scope; }

        public void Add(params NormPoint[] points)
        {
            if (points.Length == 0) return;
            List<EditPoint> eng; int from;
            lock (_pts)
            {
                if (_done) return;
                from = Math.Max(0, _eng.Count - 1);
                foreach (var p in points) { _pts.Add(p); _eng.Add(_d.ToEngine(p)); }
                eng = _eng.ToList();
            }
            bool lasso = Tool == DepthTool.MatchDepth && _app == MatchApplicator.Lasso;
            long now = Environment.TickCount64;
            // the preview follows at most ~30 times a second (the caller is the UI thread): the brush mask keeps growing in between, the
            // lasso polygon is redrawn at the next update or on release
            if (now - _lastPreview < 33) { if (!lasso) _d.PreviewSegments(eng, from, final: false, throttled: true); return; }
            _lastPreview = now;
            if (lasso) _d.PreviewPolygon(eng); else _d.PreviewSegments(eng, from, final: false, throttled: false);
        }

        public DepthJob End()
        {
            List<EditPoint> eng; List<NormPoint> pic;
            lock (_pts) { if (_done) return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Cancelled, null)); _done = true; eng = _eng.ToList(); pic = _pts.ToList(); }
            if (eng.Count < 2) { _d.ClearPreview(); return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.NothingChanged, "A stroke needs at least two points.")); }
            switch (Tool)
            {
                case DepthTool.RefineEdge: return _d.EdgeHeal(eng);
                case DepthTool.BlendArea: return _d.SurfaceHeal(eng);
                default:
                    switch (_app)
                    {
                        case MatchApplicator.Brush: return _d.SetPendingBrush(eng);
                        case MatchApplicator.Lasso: return _d.SetPendingLasso(eng, pic);
                        default:
                            bool add; lock (_d._toolLock) add = _d._opts.AddToSelection;
                            _d.ClearPreview();
                            return _d.QuickStroke(eng, _scope!, add);
                    }
            }
        }

        public void Cancel() { lock (_pts) { if (_done) return; _done = true; } _d.ClearPreview(keepPending: true); }
        public void Dispose() => Cancel();
    }

    // ================================================================================================================ the stroke preview (Lux's own)

    /// <summary>Draw the discs of a stroke's segments from point <paramref name="from"/> on into the preview mask (radius = the engine's
    /// `cvtt(levelW · radius)` at the edited level) and refresh the region they cover.</summary>
    void PreviewSegments(List<EditPoint> pts, int from, bool final, bool throttled)
    {
        bool show; lock (_toolLock) show = _showPreview;
        if (!show) return;
        var (w, h) = _editSize; int r = Math.Max(1, (int)((float)w * RadiusNow));
        RectI box;
        lock (_toolLock)
        {
            _previewMask ??= new byte[(long)w * h];
            box = Rasterise(_previewMask, w, h, pts, from, r);
            if (box.Width > 0) _previewBox = _previewBox.Width > 0 ? Union(_previewBox, box) : box;
        }
        if (throttled || box.Width <= 0) return;
        UpdateOverlay(committed: false, EditRectToPicture(box));
    }

    static RectI Rasterise(byte[] m, int w, int h, List<EditPoint> pts, int from, int r)
    {
        int bx0 = w, by0 = h, bx1 = 0, by1 = 0;
        void Disc(double cx, double cy)
        {
            int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(w - 1, (int)(cx + r)), y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(h - 1, (int)(cy + r));
            for (int y = y0; y <= y1; y++)
            {
                double dy = y - cy; double dx2 = (double)r * r - dy * dy; if (dx2 < 0) continue; double dx = Math.Sqrt(dx2);
                int a = Math.Max(x0, (int)Math.Ceiling(cx - dx)), b = Math.Min(x1, (int)Math.Floor(cx + dx));
                if (a > b) continue;
                Array.Fill(m, (byte)1, y * w + a, b - a + 1);
                if (a < bx0) bx0 = a; if (b + 1 > bx1) bx1 = b + 1; if (y < by0) by0 = y; if (y + 1 > by1) by1 = y + 1;
            }
        }
        for (int i = Math.Max(0, from); i < pts.Count; i++)
        {
            double x1 = pts[i].X * w, y1 = pts[i].Y * h;
            if (i == 0) { Disc(x1, y1); continue; }
            double x0 = pts[i - 1].X * w, y0 = pts[i - 1].Y * h;
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); int n = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1, r * 0.5)));
            for (int k = 1; k <= n; k++) Disc(x0 + (x1 - x0) * k / n, y0 + (y1 - y0) * k / n);
        }
        return bx1 <= bx0 ? default : new RectI(bx0, by0, bx1, by1);
    }

    /// <summary>The lasso polygon (even-odd fill) as the preview.</summary>
    void PreviewPolygon(List<EditPoint> pts)
    {
        bool show; lock (_toolLock) show = _showPreview;
        if (!show || pts.Count < 2) return;
        var (w, h) = _editSize;
        var m = new byte[(long)w * h];
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var p in pts) { minx = Math.Min(minx, p.X * w); maxx = Math.Max(maxx, p.X * w); miny = Math.Min(miny, p.Y * h); maxy = Math.Max(maxy, p.Y * h); }
        int y0 = Math.Max(0, (int)miny), y1 = Math.Min(h - 1, (int)Math.Ceiling(maxy));
        var xs = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            double sy = y + 0.5; xs.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                double ay = a.Y * h, by = b.Y * h;
                if ((ay <= sy && sy < by) || (by <= sy && sy < ay)) xs.Add(a.X * w + (sy - ay) / (by - ay) * (b.X * w - a.X * w));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int a = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5)), b = Math.Min(w - 1, (int)Math.Floor(xs[k + 1] - 0.5));
                if (a <= b) Array.Fill(m, (byte)1, y * w + a, b - a + 1);
            }
        }
        RectI old;
        lock (_toolLock) { old = _previewBox; _previewMask = m; _previewBox = new RectI(Math.Max(0, (int)minx), y0, Math.Min(w, (int)Math.Ceiling(maxx) + 1), y1 + 1); }
        var box = old.Width > 0 ? Union(old, _previewBox) : _previewBox;
        UpdateOverlay(committed: false, EditRectToPicture(box));
    }
    RectI _previewBox;

    void ClearPreview(bool keepPending = false)
    {
        bool had; RectI box;
        lock (_toolLock)
        {
            if (keepPending && (_brushPts is not null || _lassoPts.Count > 0)) return;
            had = _previewMask is not null; box = _previewBox; _previewMask = null; _previewBox = default;
        }
        // only the preview's own region is drawn again
        if (had) UpdateOverlay(committed: box.Width <= 0, box.Width > 0 ? EditRectToPicture(box) : null);
    }

    /// <summary>A rect of the edited level → the picture.</summary>
    NormRect? EditRectToPicture(RectI r)
    {
        var (w, h) = _editSize;
        return _s.ViewImpl.CaptureRectToPicture((double)(r.X0 - 2) / w, (double)(r.Y0 - 2) / h, (double)(r.X1 + 2) / w, (double)(r.Y1 + 2) / h);
    }

    // ================================================================================================================ pending selection

    DepthJob SetPendingBrush(List<EditPoint> pts)
    {
        lock (_toolLock) { _brushPts = pts; }
        RaiseChanged(DepthChangeKind.Overlay, null, null);
        return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Done, null), "Brush selection");
    }

    DepthJob SetPendingLasso(List<EditPoint> pts, List<NormPoint> pic)
    {
        lock (_toolLock) { _lassoPts.Clear(); _lassoPts.AddRange(pts); _lassoPicture.Clear(); _lassoPicture.AddRange(pic); }
        PreviewPolygon(pts);
        return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Done, null), "Lasso selection");
    }

    public void AddLassoPoint(NormPoint picture)
    {
        List<EditPoint> pts;
        lock (_toolLock)
        {
            if (_tool != DepthTool.MatchDepth || _app != MatchApplicator.Lasso) throw new InvalidOperationException("Choose match depth with the lasso first.");
            _lassoPts.Add(ToEngine(picture)); _lassoPicture.Add(picture); pts = _lassoPts.ToList();
        }
        PreviewPolygon(pts);
    }

    public void SetTargetDepth(float depthMm)
    {
        if (!(depthMm > 100f)) throw new ArgumentOutOfRangeException(nameof(depthMm), "the depth must be more than 100 mm");
        lock (_toolLock) { _targetDepth = depthMm; _refPoint = null; }
    }

    public void PickTargetDepth(NormPoint picture)
    {
        if (InsidePoint(picture) is null) throw new ArgumentOutOfRangeException(nameof(picture), "the point is outside the photo");
        lock (_toolLock) { _refPoint = picture; _targetDepth = null; }
    }

    public PendingSelection? Pending
    {
        get
        {
            lock (_toolLock)
            {
                if (_tool != DepthTool.MatchDepth) return null;
                bool area = _app switch { MatchApplicator.Brush => _brushPts is { Count: >= 2 }, MatchApplicator.Lasso => _lassoPts.Count >= 3, _ => _selectedPixels > 0 };
                if (!area && _targetDepth is null && _refPoint is null && _lassoPts.Count == 0) return null;
                return new PendingSelection(_app, area, _targetDepth, _refPoint, _lassoPicture.ToList(), _selectedPixels);
            }
        }
    }

    public byte[]? SelectionMask() { lock (_toolLock) return (byte[]?)_selectionMask?.Clone(); }
    public PixelSize SelectionSize => new(_editSize.W, _editSize.H);

    public void CancelPending()
    {
        bool quick; lock (_toolLock) quick = _tool == DepthTool.MatchDepth && _app == MatchApplicator.QuickSelect;
        DropPending(resetQuick: quick);
    }

    void DropPending(bool resetQuick)
    {
        lock (_toolLock) { _brushPts = null; _lassoPts.Clear(); _lassoPicture.Clear(); _targetDepth = null; _refPoint = null; }
        ClearPreview();
        if (resetQuick && (_selectionMask is not null || _ed?.Quick is not null)) _worker.Enqueue("Clear the selection", c => RunEdit(c, null, () => { _p!.ResetQuickSelect(); return true; }, quickSelection: true), cancellable: false);
    }

    /// <summary>Lumen's Enter (`applyDepthEdit`): brush ≥ 2 points, lasso ≥ 3, quick select a selection; the target depth is the
    /// typed one or `getDepthAtPoint(reference point)` read now.</summary>
    public DepthJob Apply()
    {
        MatchApplicator app; List<EditPoint>? brush; List<EditPoint> lasso; float? depth; NormPoint? refPt; DepthToolOptions o;
        lock (_toolLock)
        {
            if (_tool != DepthTool.MatchDepth) return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Failed, "Choose match depth first."));
            app = _app; brush = _brushPts; lasso = _lassoPts.ToList(); depth = _targetDepth; refPt = _refPoint; o = _opts;
        }
        if (depth is null && refPt is null) return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Failed, "Choose the depth first: pick it from the photo or type it."));
        if (app == MatchApplicator.Brush && brush is not { Count: >= 2 }) return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Failed, "Paint the area first."));
        // Lumen's interface asks for three vertices (`hasClosedEditArea`); the engine takes two (`pushLassoDepthEdit`: a degenerate polygon) — so
        // does Apply, so that every session the engine accepts can be replayed here
        if (app == MatchApplicator.Lasso && lasso.Count < 2) return DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Failed, "Close the lasso first (three points or more)."));
        float radius = o.RadiusOverride ?? RadiusOf(o.BrushSize), feather = o.FeatherOverride ?? FeatherOf(o.BrushSize);
        string label = app switch { MatchApplicator.Brush => o.Adaptive ? "Match depth (adaptive brush)" : "Match depth (brush)", MatchApplicator.Lasso => "Match depth (lasso)", _ => "Match depth (quick select)" };
        var job = _worker.Enqueue(label, c =>
        {
            float d = depth ?? RefDepth(refPt!.Value) ?? throw new InvalidOperationException("There is no depth at the picked point.");
            if (app == MatchApplicator.Brush && o.Adaptive) { c.CanCancel = true; c.EstimatedMs = Estimate("adaptive", brush!, radius); }
            var r = RunEdit(c, new Entry(label, DepthTool.MatchDepth, app), app switch
            {
                MatchApplicator.Brush => () => { _p!.PushBrush(brush!, radius, d, o.Adaptive); return true; },
                MatchApplicator.Lasso => () => { _p!.PushLasso(lasso, d, feather); return true; },
                _ => () => { _p!.PushQuickSelect(d); return true; },
            });
            if (r.Outcome == DepthJobOutcome.Done) DropPending(resetQuick: false);
            return r;
        }, cancellable: app == MatchApplicator.Brush && o.Adaptive);
        return job;
    }

    /// <summary>`clearQuickSelectMask` (never called by Lumen's interface; scripted sessions use it): the selection emptied, its colour guide kept.</summary>
    internal DepthJob ClearSelectionMask() => _worker.Enqueue("Clear the selection mask", c => RunEdit(c, null, () => { _p!.ClearQuickSelectMask(); return true; }, quickSelection: true), cancellable: false);

    /// <summary>The eyedropper's depth: `getDepthAtPoint` on the editor's present depth (worker).</summary>
    float? RefDepth(NormPoint p)
    {
        var (x, y) = _s.ViewImpl.ToCapture(p);
        try { return _ed!.DepthAtPoint((float)x, (float)y); } catch (InvalidOperationException) { return null; }
    }

    // ================================================================================================================ the tools' jobs

    DepthJob EdgeHeal(List<EditPoint> pts)
    {
        float radius = RadiusNow;
        return _worker.Enqueue("Refine edge", c =>
        {
            c.CanCancel = true; c.EstimatedMs = Estimate("edge", pts, radius);
            c.Report("Following the edge", null);
            _solveProgress = f => c.Report("Following the edge", f);
            var (lo, hi) = IntervalOverride?.Invoke() ?? EdgeInterval();
            bool refined = false; (ulong In, ulong Out) counts = (0, 0);
            Lux.Engine.Pipeline.Dof.Edit.EdgeHeal.LastCounts = (0, 0);
            var r = RunEdit(c, new Entry("Refine edge", DepthTool.RefineEdge, null), () => { refined = _p!.PushEdgeHeal(pts, radius, lo, hi); counts = Lux.Engine.Pipeline.Dof.Edit.EdgeHeal.LastCounts; return refined; });
            ClearPreview();
            // "nothing changed" always says why, in plain words
            if (r.Outcome is DepthJobOutcome.Done or DepthJobOutcome.NothingChanged && !refined)
                return r with { Outcome = DepthJobOutcome.NothingChanged, Message = EdgeReason(counts) };
            if (r.Outcome == DepthJobOutcome.NothingChanged && r.Message is null)
                return r with { Message = "The edge under the stroke is already as refined as it can be: nothing changed." };
            return r;
        }, cancellable: true);
    }

    /// <summary>Why refine edge left the depth alone (the engine's count of the stroke's known pixels inside / outside the in-focus interval).</summary>
    static string EdgeReason((ulong In, ulong Out) c)
    {
        if (c.In == 0 && c.Out == 0)
            return "Nothing changed: the stroke is outside the photo or too close to its edge. Draw it along the boundary between a nearer and a farther object.";
        if (c.In == 0)
            return "Nothing changed: neither side of the stroke is at the focus distance. Refine edge needs one side in focus and the other side nearer or farther — set the focus on the object first, then draw along its outline.";
        if (c.Out == 0)
            return "Nothing changed: both sides of the stroke are at the focus distance, so there is no edge between near and far to refine. Draw along an outline where the background is out of focus.";
        return "Nothing changed: the stroke lies almost entirely on one side of the edge. Draw it along the boundary so that both the object and its background are under the brush.";
    }

    DepthJob SurfaceHeal(List<EditPoint> pts)
    {
        float radius = RadiusNow;
        return _worker.Enqueue("Blend area", c =>
        {
            c.EstimatedMs = Estimate("surface", pts, radius);
            var r = RunEdit(c, new Entry("Blend area", DepthTool.BlendArea, null), () => { _p!.PushSurfaceHeal(pts, radius); return true; });
            ClearPreview();
            return r;
        }, cancellable: false);
    }

    DepthJob QuickStroke(List<EditPoint> pts, QuickSelectScope scope, bool add)
    {
        float radius = RadiusNow; var rect = ScopeRect(scope);
        return _worker.Enqueue(add ? "Add to the selection" : "Remove from the selection", c =>
        {
            c.EstimatedMs = Estimate("quick", rect.Width * (double)rect.Height); c.CanCancel = true;
            // A stroke can be stopped while it runs; it then changes nothing (the editor rolls back, nothing is recorded)
            _ed!.QuickCancel = c.Token;
            try { return RunEdit(c, null, () => { _p!.AddQuickSelectStrokes(pts, radius, rect, scope.Level, add); return true; }, quickSelection: true); }
            finally { if (_ed is not null) _ed.QuickCancel = default; }
        }, cancellable: true);
    }

    /// <summary>The edge heal's depth interval: Lumen's in-focus range of the depth-effect cache with (2.2, 0.7) — the range around the focus distance the photo
    /// is shown with (the setting, or Lumen's default focus, which the renderer puts there when the depth is published).</summary>
    (float Lo, float Hi) EdgeInterval()
    {
        var (_, focus) = _s.EditImpl.DepthEffect;
        float f = focus is float x && x > 0f ? x : DofModel.DefaultFocusDepth(_s.Engine.Lri, _snap!.Cache.Current);
        var st = new DofState(_k); st.Set(Math.Max(1f, _s.EditImpl.DepthEffect.FNumber ?? _k.DefaultFNumber), f);
        return DofModel.CurrentInFocusRange(st, 2.2f, 0.7f);
    }

    // ---- measured costs (an average per tool and kilopixel of the stroke's region; seeds from measured replay times)
    readonly Dictionary<string, double> _msPerKpx = new() { ["edge"] = 4.0, ["adaptive"] = 2.5, ["surface"] = 0.05, ["quick"] = 0.35 };
    double? Estimate(string tool, List<EditPoint> pts, float radius)
    {
        var (w, h) = _editSize; double minx = 1, miny = 1, maxx = 0, maxy = 0;
        foreach (var p in pts) { minx = Math.Min(minx, p.X); maxx = Math.Max(maxx, p.X); miny = Math.Min(miny, p.Y); maxy = Math.Max(maxy, p.Y); }
        double rpx = radius * w, area = ((maxx - minx) * w + 2 * rpx) * ((maxy - miny) * h + 2 * rpx);
        return Estimate(tool, area);
    }
    double? Estimate(string tool, double pixels) { lock (_msPerKpx) return _msPerKpx.TryGetValue(tool, out var k) ? k * pixels / 1000.0 : null; }
}
