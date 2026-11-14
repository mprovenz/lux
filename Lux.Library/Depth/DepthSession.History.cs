using Lux.Engine.Pipeline;
using Lux.Store;

namespace Lux.Library.Depth;

/// <summary>The depth history (Lumen's undo / redo / reset depth edits, the 20-edit limit, the log kept by the store, the background rebuild)
/// and the combined history of the photo (<see cref="IPhotoHistory"/>).</summary>
internal sealed partial class DepthSession
{
    /// <summary>A depth step: label and tool, stamp in the session's combined order.</summary>
    internal sealed record Entry(string Label, DepthTool? Tool, MatchApplicator? App)
    {
        public long Stamp { get; set; }
        /// <summary>Read back from the saved log at open (an earlier session's step).</summary>
        public bool Earlier { get; init; }
    }

    // the depth stack: entry i = version i + 1 of the editor (worker writes, readers take a copy under _histLock)
    readonly object _histLock = new();
    readonly List<Entry> _entries = new();
    int _cursor, _base; bool _importBase; long _logCount;
    int _openCursor;                 // the cursor the version was opened at (its snapshot): steps up to it need the rebuilt history
    DepthHistoryState _histState = DepthHistoryState.Ready; string? _histReason; double? _histProgress; (int Done, int Of)? _histCounts;
    volatile int _depthRedoVisible;
    long _logStamp = long.MinValue / 2;

    /// <summary>Read the active version's history from its log (worker): the labels of its steps by simulating the log on the cursor it records
    /// (edits push, undo / redo move, reset clears, Lumen's 20-edit limit raises the floor).</summary>
    void ReadHistory()
    {
        var entries = new List<Entry>(); int cursor = 0, bas = 0; bool import = false; long count = 0;
        DepthHistoryState state = DepthHistoryState.Ready; string? reason = null;
        if (_p?.Variant is { } v && _store is not null)
        {
            try
            {
                var log = _store.ReadLog(v.Id); count = log.Count;
                foreach (var op in log)
                {
                    int after = (int)op.CursorAfter;
                    switch (op.Kind)
                    {
                        case EditOp.LumenImport: import = op.Outcome == EditOutcome.Done; break;
                        case EditOp.Reset: entries.Clear(); bas = 0; break;
                        case EditOp.Undo: case EditOp.Redo: break;
                        case EditOp.Brush: case EditOp.Lasso: case EditOp.SurfaceHeal: case EditOp.EdgeHeal: case EditOp.QuickApply:
                            if (op.Outcome == EditOutcome.Done && after == cursor + 1)
                            {
                                if (entries.Count > cursor) entries.RemoveRange(cursor, entries.Count - cursor);
                                entries.Add(new Entry(LabelOf(op), ToolOf(op), AppOf(op)) { Stamp = _logStamp++, Earlier = true });
                                if (after - bas > Lux.Engine.Pipeline.Dof.Edit.DepthEditor.MaxHistory) bas = after - Lux.Engine.Pipeline.Dof.Edit.DepthEditor.MaxHistory;
                            }
                            break;
                    }
                    cursor = after;
                }
                if (cursor != (int)v.Cursor) throw new DepthEditStoreException($"the saved log ends at step {cursor}, the saved result at step {v.Cursor}");
                if (v.History == Lux.Store.HistoryState.Unreliable || _p.Variant?.History == Lux.Store.HistoryState.Unreliable)
                { state = DepthHistoryState.Unreliable; reason = _p.Variant?.HistoryNote ?? v.HistoryNote; }
                else if (!_p.HistoryLoaded) state = DepthHistoryState.Rebuilding;
            }
            catch (DepthEditStoreException e) { state = DepthHistoryState.Unreliable; reason = e.Message; entries.Clear(); import = false; cursor = -1; }
            if (state == DepthHistoryState.Unreliable && (cursor != (int)v.Cursor || entries.Count < cursor))
            {
                // the saved steps cannot be read: the saved result's steps are listed as placeholders (done, not undoable)
                cursor = (int)v.Cursor; entries.Clear();
                for (int i = 0; i < cursor; i++) entries.Add(new Entry("Saved depth edit", null, null) { Stamp = _logStamp++, Earlier = true });
                bas = cursor;
            }
            if (state == DepthHistoryState.Unreliable) bas = Math.Max(bas, Math.Min(cursor, (int)(_ed?.Base ?? 0)));
        }
        lock (_histLock)
        {
            _entries.Clear(); _entries.AddRange(entries); _cursor = cursor; _base = bas; _importBase = import; _logCount = count; _cleared = 0;
            _openCursor = _p is not null && !_p.HistoryLoaded ? cursor : 0;
            _histState = state; _histReason = reason; _histProgress = null;
        }
        _depthRedoVisible = Math.Max(0, entries.Count - cursor);
    }

    static string LabelOf(EditOp op) => op.Kind switch
    {
        EditOp.Brush => op.Adaptive == true ? "Match depth (adaptive brush)" : "Match depth (brush)",
        EditOp.Lasso => "Match depth (lasso)",
        EditOp.SurfaceHeal => "Blend area",
        EditOp.EdgeHeal => "Refine edge",
        EditOp.QuickApply => "Match depth (quick select)",
        _ => op.Kind,
    };
    static DepthTool? ToolOf(EditOp op) => op.Kind switch { EditOp.SurfaceHeal => DepthTool.BlendArea, EditOp.EdgeHeal => DepthTool.RefineEdge, _ => DepthTool.MatchDepth };
    static MatchApplicator? AppOf(EditOp op) => op.Kind switch
    {
        EditOp.Brush => MatchApplicator.Brush, EditOp.Lasso => MatchApplicator.Lasso, EditOp.QuickApply => MatchApplicator.QuickSelect, _ => null,
    };

    /// <summary>The variant is made at the first recorded operation of a clean version (worker).</summary>
    void EnsurePersistence()
    {
        if (_p is not null) return;
        // the first depth edit keeps the depth for the new version (about a second): shown in the Queue
        using var row = _s.Lib.ActivityLog.Start(_s.CaptureId, $"Keeping the depth of {(_s.Lib.Get(_s.CaptureId) ?? _s.Item).Name} for its depth edits", "Saving the depth map");
        _store ??= DepthEditStore.OpenOrCreate(_s.Context);
        _p = DepthEditPersistence.New(_store, _ed!);
        if (PinnedRandom is not null) _p.PinnedRandomDevice = PinnedRandom;
    }

    /// <summary>
    /// Run one recorded depth operation (worker): through the persistence (the editor call, the log entry, the saved tiles — one transaction),
    /// the editor's providers stoppable by the job's token; then the history stack, the selection, the published depth and the mirror follow.
    /// An operation the editor refuses is recorded as refused (as the store does) and reported as failed; a stopped one leaves no trace.
    /// </summary>
    DepthJobResult RunEdit(DepthWorker.Ctx c, Entry? entry, Func<bool> op, bool quickSelection = false, int undoRedo = 0)
    {
        EnsurePersistence();
        var ed = _ed!;
        uint before = ed.Cursor;
        _changed = null; _tok = c.Token;
        string? failed = null;
        // the new depth is shown as soon as the editor has it, while the persistence saves it
        bool published = false;
        _p!.AfterApply = () => { if (_changed is { } ch0) using (DepthProfile.Time("edit.publish")) { Publish(ch0); published = true; } };
        try { using (DepthProfile.Time("edit.op")) op(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { failed = Plain(e); }
        finally { _tok = default; _solveProgress = null; if (_p is not null) _p.AfterApply = null; }
        if (DepthProfile.On && _p is not null) { DepthProfile.Add($"  persist.editor", DepthProfile.Now, _p.LastOperationMs); DepthProfile.Add($"  persist.save ({_p.LastSavedTiles} tiles, {_p.LastSavedBytes / 1024} KB)", DepthProfile.Now, _p.LastSaveMs); }
        _variant = _p!.Variant;
        // the stack
        lock (_histLock)
        {
            int cur = (int)ed.Cursor;
            if (entry is not null && failed is null && cur == before + 1)
            {
                if (_entries.Count > (int)before) _entries.RemoveRange((int)before, _entries.Count - (int)before);
                entry.Stamp = _s.NextStamp();
                _entries.Add(entry);
            }
            if (undoRedo == 0 && entry is not null && cur == before + 1) _depthRedoVisible = 0;
            if (undoRedo < 0 && cur == before - 1) _depthRedoVisible++;
            if (undoRedo > 0 && cur == before + 1) _depthRedoVisible = Math.Max(0, _depthRedoVisible - 1);
            if (_p.HistoryLoaded) _base = (int)ed.Base;
            else if (cur - _base > Lux.Engine.Pipeline.Dof.Edit.DepthEditor.MaxHistory) _base = cur - Lux.Engine.Pipeline.Dof.Edit.DepthEditor.MaxHistory;
            _cursor = cur;
            if (_store is not null && _p.Variant is { } v) _logCount = _store.LogCount(v.Id);
        }
        if (entry is not null && failed is null && ed.Cursor == before + 1) _s.EditImpl.ClearRedo();
        if (quickSelection) UpdateSelection();
        NormRect? changedPic = null;
        if (_changed is { } ch) { if (!published) using (DepthProfile.Time("edit.publish")) Publish(ch); changedPic = CacheRectToPicture(ch, 0); }
        using (DepthProfile.Time("edit.after")) { ScheduleMirror(); RaiseHistory(); RefreshVersions(); }
        if (failed is not null) return new DepthJobResult(DepthJobOutcome.Failed, failed);
        return new DepthJobResult(_changed is null && !quickSelection ? DepthJobOutcome.NothingChanged : DepthJobOutcome.Done, null) { ChangedRect = changedPic };
    }

    static string Plain(Exception e) => e.Message.Length == 0 ? "The tool could not be applied." : char.ToUpperInvariant(e.Message[0]) + e.Message[1..].TrimEnd('!') + (e.Message.EndsWith('.') ? "" : ".");

    /// <summary>The quick-select selection after a stroke / reset: the overlay's copy and the region that changed.</summary>
    void UpdateSelection()
    {
        var m = _ed!.QuickSelectMask;
        byte[]? copy = m is null ? null : (byte[])m.Clone();
        long n = 0; if (copy is not null) foreach (var b in copy) n += b;
        RectI box = default; var old = _selectionMask; var (w, h) = _editSize;
        if (copy is not null || old is not null)
        {
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            for (int y = 0; y < h; y++)
            {
                long r = (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    byte a = copy?[r + x] ?? 0, b = old?[r + x] ?? 0;
                    if (a == b) continue;
                    if (x < x0) x0 = x; if (x + 1 > x1) x1 = x + 1; if (y < y0) y0 = y; if (y + 1 > y1) y1 = y + 1;
                }
            }
            if (x1 > x0) box = new RectI(x0, y0, x1, y1);
        }
        lock (_toolLock) { _selectionMask = copy; _selectedPixels = n; }
        if (box.Width > 0 && EffectiveOverlay == DepthOverlayMode.Selection) UpdateOverlay(committed: false, EditRectToPicture(box));
    }

    // ================================================================================================================ the rebuild (background replay)

    Timer? _replayTimer;
    /// <summary>When the version was opened from its saved result, its steps are rebuilt in the background once the worker has been idle for a
    /// moment (a user job stops the rebuild; it starts again later).</summary>
    void ScheduleReplay()
    {
        if (_stopped || _p is null || _p.HistoryLoaded) return;
        lock (_histLock)
        {
            if (_histState != DepthHistoryState.Rebuilding) return;
            _replayTimer ??= new Timer(_ =>
            {
                if (_stopped || !_worker.IsIdle || _worker.HasBackground("Rebuilding history")) return;
                _worker.Enqueue("Rebuilding history", c => { RebuildHistory(c); return new DepthJobResult(DepthJobOutcome.Done, null); }, cancellable: true, background: true);
            });
            _replayTimer.Change(ReplayDelayMs, Timeout.Infinite);
        }
    }
    /// <summary>The idle time before the rebuild starts (tests set it to 0).</summary>
    internal int ReplayDelayMs { get; set; } = 1500;

    /// <summary>Rebuild the history (worker; stoppable between entries and inside slow ones).</summary>
    bool RebuildHistory(DepthWorker.Ctx c)
    {
        if (_p is null || _p.HistoryLoaded) return true;
        _tok = c.Token;
        bool ok;
        try
        {
            ok = _p.EnsureHistory(c.Token, (i, n) =>
            {
                lock (_histLock) { _histProgress = n == 0 ? 1 : (double)i / n; _histCounts = (i, n); }
                c.Report($"Rebuilding history ({i} of {n})", n == 0 ? null : (double)i / n);
                RaiseHistory();
            });
        }
        finally { _tok = default; }
        lock (_histLock)
        {
            if (ok) { _histState = DepthHistoryState.Ready; _histReason = null; _base = (int)_ed!.Base; _openCursor = 0; }
            else { _histState = DepthHistoryState.Unreliable; _histReason = _p.Variant?.HistoryNote ?? "The saved steps do not reproduce the saved depth."; }
            _histProgress = null; _histCounts = null;
        }
        LastReplay = _p.LastReplay;
        if (ok) UpdateSelection();
        RaiseHistory(); RefreshVersions();
        if (!ok) ScheduleMirror();
        return ok;
    }

    /// <summary>The last rebuild's report (tests and timings).</summary>
    internal ReplayReport? LastReplay { get; private set; }

    /// <summary>Make the steps undoable before an undo / redo (worker): the rebuild runs now when it has not.</summary>
    void NeedHistory(DepthWorker.Ctx c)
    {
        if (_p is null || _p.HistoryLoaded) return;
        DepthHistoryState s; lock (_histLock) s = _histState;
        if (s == DepthHistoryState.Rebuilding) { c.CanCancel = true; RebuildHistory(c); c.CanCancel = false; }
    }

    // ================================================================================================================ depth undo / redo / reset

    /// <summary>Depth steps up to this one were dropped from the list by "Clear history" (this session; reset by a version switch or a reset).</summary>
    int _cleared;
    int Floor => Math.Max(_base, _cleared);
    public bool CanUndo { get { lock (_histLock) return _p is not null && _cursor > Floor && (_histState != DepthHistoryState.Unreliable || _cursor > _openCursor); } }
    public bool CanRedo { get { lock (_histLock) return _entries.Count > _cursor && _p is not null; } }
    public DepthHistoryState HistoryState { get { lock (_histLock) return _histState; } }

    public DepthJob Undo() => _worker.Enqueue("Undo", c =>
    {
        NeedHistory(c);
        if (_ed is null || !_ed.CanUndo) return new DepthJobResult(DepthJobOutcome.NothingChanged, "There is no depth edit to undo.");
        lock (_histLock) if (_cursor <= _cleared) return new DepthJobResult(DepthJobOutcome.NothingChanged, "The history was cleared: earlier depth edits cannot be undone now.");
        return RunEdit(c, null, () => _p!.Undo(), undoRedo: -1);
    }, cancellable: false);

    public DepthJob Redo() => _worker.Enqueue("Redo", c =>
    {
        NeedHistory(c);
        if (_ed is null || !_ed.CanRedo) return new DepthJobResult(DepthJobOutcome.NothingChanged, "There is no depth edit to redo.");
        return RunEdit(c, null, () => _p!.Redo(), undoRedo: +1);
    }, cancellable: false);

    public DepthJob Reset() => _worker.Enqueue("Reset depth edits", c =>
    {
        if (_ed is null) return new DepthJobResult(DepthJobOutcome.Failed, "The depth of this photo is not ready yet.");
        var r = RunEdit(c, null, () => { _p!.Reset(); return true; });
        lock (_histLock) { _entries.Clear(); _cursor = 0; _base = 0; _openCursor = 0; _cleared = 0; }
        _depthRedoVisible = 0;
        RaiseHistory();
        return r;
    }, cancellable: false);

    DepthJob IPhotoHistory.ClearHistory() => _worker.Enqueue("Clear history", c =>
    {
        _s.EditImpl.ClearSteps();
        lock (_histLock) _cleared = _cursor;
        _depthRedoVisible = 0;
        RaiseHistory();
        return new DepthJobResult(DepthJobOutcome.Done, null);
    }, cancellable: false);

    bool IPhotoHistory.Cleared => _s.EditImpl.Cleared;

    public DepthActivity Activity => _worker.Activity;
    public void CancelAll() => _worker.CancelAll();

    // ================================================================================================================ the combined history

    event EventHandler<HistoryInfo>? HistoryChangedEvent;
    event EventHandler<HistoryInfo>? IPhotoHistory.Changed { add => HistoryChangedEvent += value; remove => HistoryChangedEvent -= value; }

    void RaiseHistory()
    {
        HistoryInfo info;
        try { info = BuildHistory(); } catch (Exception e) { _s.Lib.Trace("history: " + e.Message); return; }
        try { HistoryChangedEvent?.Invoke(this, info); } catch (Exception e) { _s.Lib.Trace("history listener: " + e.Message); }
    }

    /// <summary>One step of the combined list with where it comes from.</summary>
    sealed record Item(HistoryEntry Entry, long Stamp, bool Depth);

    List<Item> Items(out int position)
    {
        var (undo, redo) = _s.EditImpl.Steps;
        var done = new List<Item>(); var undone = new List<Item>();
        foreach (var u in undo) done.Add(new Item(new HistoryEntry(0, Cap(u.Label), true, u.Kind, false, true), u.Stamp, false));
        foreach (var u in redo) undone.Add(new Item(new HistoryEntry(0, Cap(u.Label), false, u.Kind, false, true), u.Stamp, false));
        lock (_histLock)
        {
            bool unreliable = _histState == DepthHistoryState.Unreliable;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i]; int pos = i + 1;
                if (pos <= _cleared) continue;   // dropped by "Clear history"
                bool isDone = pos <= _cursor;
                bool undoable = pos > _base && !(unreliable && pos <= _openCursor);
                var he = new HistoryEntry(0, e.Label, isDone, HistoryEntryKind.DepthEdit, true, undoable) { Tool = e.Tool, Applicator = e.App, FromEarlierSession = e.Earlier };
                if (isDone) done.Add(new Item(he, e.Stamp, true));
                else if (pos - _cursor <= _depthRedoVisible) undone.Add(new Item(he, e.Stamp, true));
            }
        }
        done.Sort((a, b) => a.Stamp.CompareTo(b.Stamp)); undone.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));
        var all = new List<Item>();
        if (_importBase && _cleared == 0 && !_s.EditImpl.Cleared) all.Add(new Item(new HistoryEntry(0, "Imported from Lumen", true, HistoryEntryKind.DepthImport, true, false), long.MinValue, true));
        all.AddRange(done); position = all.Count; all.AddRange(undone);
        for (int i = 0; i < all.Count; i++) all[i] = all[i] with { Entry = all[i].Entry with { Index = i } };
        return all;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    HistoryInfo BuildHistory()
    {
        var items = Items(out int pos);
        lock (_histLock)
        {
            // The counts are of the steps the list shows from the earlier session (a saved log also holds selection
            // strokes, undo / redo and resets: 10 operations for 5 listed steps on L16_00493), in proportion to the operations replayed
            bool rebuilding = _histState == DepthHistoryState.Rebuilding;
            int listed = items.Count(i => i.Entry.FromEarlierSession);
            int ops = _histCounts?.Of ?? (int)_logCount, opsDone = _histCounts?.Done ?? 0;
            int steps = listed > 0 ? listed : ops;
            int stepsDone = ops > 0 ? (int)Math.Floor((double)opsDone / ops * steps) : 0;
            return new HistoryInfo(items.Select(i => i.Entry).ToList(), pos, _histState, _histProgress, _histReason, Lux.Engine.Pipeline.Dof.Edit.DepthEditor.MaxHistory, _logCount)
            {
                RebuiltSteps = rebuilding ? stepsDone : null,
                StepsToRebuild = rebuilding ? steps : null,
                OperationsReplayed = rebuilding ? opsDone : null,
                OperationsToReplay = rebuilding ? ops : null,
            };
        }
    }

    HistoryInfo IPhotoHistory.Info => BuildHistory();

    Item? NextUndo() { var it = Items(out int pos); return pos == 0 ? null : it[pos - 1]; }
    Item? NextRedo() { var it = Items(out int pos); return pos >= it.Count ? null : it[pos]; }

    bool IPhotoHistory.CanUndo => NextUndo() is { } i && i.Entry.Undoable && (!i.Depth || _p is not null);
    bool IPhotoHistory.CanRedo => NextRedo() is not null;
    string? IPhotoHistory.UndoLabel => NextUndo() is { Entry.Undoable: true } i ? "Undo " + Lower(i.Entry.Label) : null;
    string? IPhotoHistory.RedoLabel => NextRedo() is { } i ? "Redo " + Lower(i.Entry.Label) : null;
    static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    DepthJob IPhotoHistory.Undo() => _worker.Enqueue("Undo", c => CombinedStep(c, undo: true), cancellable: false);
    DepthJob IPhotoHistory.Redo() => _worker.Enqueue("Redo", c => CombinedStep(c, undo: false), cancellable: false);

    DepthJob IPhotoHistory.GoTo(int position) => _worker.Enqueue("Go to a step", c =>
    {
        DepthJobResult last = new(DepthJobOutcome.NothingChanged, null);
        for (int guard = 0; guard < 10000; guard++)
        {
            var it = Items(out int pos);
            if (position < 0 || position > it.Count) return new DepthJobResult(DepthJobOutcome.Failed, "There is no such step.");
            if (pos == position) return last;
            last = CombinedStep(c, undo: pos > position);
            if (last.Outcome is DepthJobOutcome.Failed or DepthJobOutcome.NothingChanged) return last;
        }
        return last;
    }, cancellable: false);

    /// <summary>One combined undo / redo (worker): the newest done step (or oldest undone one) — a non-depth one through the edit, a depth one
    /// through the editor.</summary>
    DepthJobResult CombinedStep(DepthWorker.Ctx c, bool undo)
    {
        var it = undo ? NextUndo() : NextRedo();
        if (it is null) return new DepthJobResult(DepthJobOutcome.NothingChanged, undo ? "Nothing to undo." : "Nothing to redo.");
        if (undo && !it.Entry.Undoable) return new DepthJobResult(DepthJobOutcome.NothingChanged, "This step can no longer be undone.");
        if (!it.Depth)
        {
            if (undo) _s.EditImpl.Undo(); else _s.EditImpl.Redo();
            return new DepthJobResult(DepthJobOutcome.Done, null);
        }
        NeedHistory(c);
        if (undo ? !_ed!.CanUndo : !_ed!.CanRedo) return new DepthJobResult(DepthJobOutcome.NothingChanged, "The depth history is not available.");
        return RunEdit(c, null, () => undo ? _p!.Undo() : _p!.Redo(), undoRedo: undo ? -1 : +1);
    }
}
