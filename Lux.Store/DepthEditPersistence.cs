using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;

namespace Lux.Store;

/// <summary>What a replay of a variant's operation log found.</summary>
public sealed class ReplayReport
{
    /// <summary>The replay reproduced every recorded outcome and every snapshot checksum.</summary>
    public bool Ok => Problems.Count == 0;
    public List<string> Problems { get; } = new();
    /// <summary>Per log entry: (sequence number, kind, milliseconds).</summary>
    public List<(long Seq, string Kind, double Ms)> Timings { get; } = new();
    /// <summary>Time spent on the per-entry result digests, the final comparison with the snapshot, and everything.</summary>
    public double DigestMs, VerifyMs, TotalMs;
    public int TilesChecked, PlanesChecked;
    /// <summary>The editor the log was replayed on (kept only for a successful replay).</summary>
    public DepthEditor? Replayed { get; internal set; }
}

/// <summary>
/// Binds a live <see cref="DepthEditor"/> to one variant of a <see cref="DepthEditStore"/>.
///
/// <b>Edit through this object</b> (the <c>Push…</c>, <c>Undo</c>, <c>Redo</c>, <c>Reset</c> and quick-select methods mirror the
/// editor's): each call runs on the editor, is appended to the operation log, and the tiles it changed are written to the result
/// snapshot — all in one transaction, so the database always holds exactly the editor's state after the last completed operation.
///
/// <b>Opening</b> an existing variant puts the snapshot back and nothing else: the depth-cache pixels, the tiles' present versions (with
/// their version numbers), the edit layer and the cursor. That is everything a render and every further edit needs. The undo history
/// is not in the snapshot; the first <see cref="Undo"/> / <see cref="Redo"/> (or an explicit <see cref="EnsureHistory"/>) rebuilds it
/// by replaying the log on a second editor from the pinned base depth, compares the result with the snapshot's checksums, and takes
/// the replayed state over only when they agree. When they do not, the snapshot stays as it is and the history is marked unreliable.
///
/// The editor handed in must be in its initial state on the depth the variant pins (use <see cref="DepthEditStore.ReadPinnedDepth"/>
/// to build it); a new variant pins the editor's source depth at the first recorded operation.
/// </summary>
public sealed class DepthEditPersistence
{
    public const string LumenRecordBlob = "lumen-depth-editor-record";
    /// <summary>The imported session's Stereo record message (`ltpb::Stereo`: the refined calibration a session render restores).</summary>
    public const string LumenStereoMessageBlob = "lumen-stereo-message";

    public DepthEditStore Store { get; }
    public DepthEditor Editor { get; }
    /// <summary>The variant this session writes; null until the first operation of a new variant.</summary>
    public VariantInfo? Variant { get; private set; }
    /// <summary>Whether the editor holds the full undo history (true for a new variant, after a successful <see cref="EnsureHistory"/>).</summary>
    public bool HistoryLoaded { get; private set; }
    /// <summary>The last replay made by <see cref="EnsureHistory"/>; null when none ran.</summary>
    public ReplayReport? LastReplay { get; private set; }
    /// <summary>When set, quick-select strokes draw their random values from here instead of deriving them (comparisons against a
    /// recorded Lumen sequence). The values drawn are recorded in the log either way.</summary>
    public Func<uint>? PinnedRandomDevice { get; set; }
    /// <summary>Timing of the last recorded operation: the editor call, and the database transaction that saved it.</summary>
    public double LastOperationMs { get; private set; }
    /// <summary>Called on the recording thread after an operation ran on the editor and before its result is saved (the app shows
    /// the new depth while the save runs). It must not change the editor.</summary>
    public Action? AfterApply { get; set; }
    public double LastSaveMs { get; private set; }
    public int LastSavedTiles { get; private set; }
    public long LastSavedBytes { get; private set; }

    readonly byte[] _captureSha; readonly string _newName, _newKind;
    readonly float[] _baseline; readonly int _cw, _ch;
    readonly ChangeTracker _changes;
    bool _quickPending, _historyTried;

    /// <summary>
    /// Which tiles an operation wrote: the tiles under every rect <see cref="DepthEditor.DepthChanged"/> reported, plus the tiles
    /// whose present version number is no longer the one last seen (cursor moves; tiles that only received layers). The live session
    /// and a replay use the same tracker, so both see the same tiles after the same operation.
    /// </summary>
    sealed class ChangeTracker
    {
        readonly DepthEditor _e; readonly HashSet<(int, int)> _dirty = new();
        public readonly Dictionary<(int, int), long> Seen = new();
        public ChangeTracker(DepthEditor e) { _e = e; e.DepthChanged += OnDepthChanged; }
        public void Detach() => _e.DepthChanged -= OnDepthChanged;
        public void Clear() => _dirty.Clear();

        void OnDepthChanged(RectI cacheRect)
        {
            var e = _e;
            var r = new RectI(Math.Max(0, cacheRect.X0 - e.Origin.X), Math.Max(0, cacheRect.Y0 - e.Origin.Y),
                              Math.Min(e.LevelSize.W, cacheRect.X1 - e.Origin.X), Math.Min(e.LevelSize.H, cacheRect.Y1 - e.Origin.Y));
            if (r.X1 <= r.X0 || r.Y1 <= r.Y0) return;
            var (tx0, ty0, tx1, ty1) = e.TileRange(r);
            for (int tx = tx0; tx <= tx1; tx++) for (int ty = ty0; ty <= ty1; ty++) _dirty.Add((tx, ty));
        }

        /// <summary>The records of the tiles written since the last call, in (tx, ty) order; the tracker is reset.</summary>
        public List<TileRecord> Take()
        {
            var e = _e;
            for (int tx = 0; tx < e.Nx; tx++)
                for (int ty = 0; ty < e.Ny; ty++)
                {
                    long cur = e.CurrentVersion(tx, ty) is { } cv ? cv.Version : -1;
                    bool seen = Seen.TryGetValue((tx, ty), out long was);
                    if (seen ? was != cur : cur >= 0) _dirty.Add((tx, ty));
                }
            var records = _dirty.OrderBy(k => k).Select(k => BuildRecord(e, k.Item1, k.Item2)).ToList();
            foreach (var t in records) Seen[(t.Tx, t.Ty)] = t.Version;
            _dirty.Clear();
            return records;
        }
    }

    /// <summary>
    /// The digest recorded with a log entry: SHA-256 over the cursor, the edit layer (allocated?, rect list) and, for every tile the
    /// operation wrote in (tx, ty) order, its index, present version number and the SHA-256 of each of its six planes (a zero byte
    /// for an absent plane). It ties every entry to the pixels it produced, so a replay is checked step by step — not only at the end,
    /// where a later edit may have painted over a difference that undo would bring back.
    /// </summary>
    public static byte[] ResultDigest(DepthEditor e, IReadOnlyList<TileRecord> written)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var b = new byte[8];
        void I(long v) { BitConverter.TryWriteBytes(b, v); h.AppendData(b); }
        I(e.Cursor); I(e.Layer.Mask != null ? 1 : 0); I(e.Layer.Rects.Count);
        foreach (var r in e.Layer.Rects) { I(r.X0); I(r.Y0); I(r.X1); I(r.Y1); }
        I(written.Count);
        foreach (var t in written)
        {
            I(t.Tx); I(t.Ty); I(t.Version);
            foreach (var sha in t.Shas()) { if (sha == null) h.AppendData(new byte[] { 0 }); else { h.AppendData(new byte[] { 1 }); h.AppendData(sha); } }
        }
        return h.GetHashAndReset();
    }

    DepthEditPersistence(DepthEditStore store, DepthEditor editor, string newName, string newKind)
    {
        Store = store; Editor = editor; _newName = newName; _newKind = newKind;
        _captureSha = Convert.FromHexString(store.CaptureHash);
        if (editor.Cursor != 0 || editor.Base != 0 || editor.Rois.Count != 0 || editor.Layer.Rects.Count != 0 || AnyVersion(editor))
            throw new InvalidOperationException("the editor must be in its initial state when it is bound to the store");
        var img = editor.Cache.Current; _cw = img.W; _ch = img.H;
        _baseline = img.ToDense();
        _changes = new ChangeTracker(editor);
    }

    static bool AnyVersion(DepthEditor e)
    {
        for (int x = 0; x < e.Nx; x++) for (int y = 0; y < e.Ny; y++) if (e.Versions(x, y).Count != 0) return true;
        return false;
    }

    /// <summary>Stop recording (the editor stays usable, unrecorded).</summary>
    public void Detach() => _changes.Detach();

    // ------------------------------------------------------------------------------------------------ opening

    /// <summary>Open an existing variant: restore its snapshot into the editor.</summary>
    public static DepthEditPersistence Open(DepthEditStore store, DepthEditor editor, string variant)
    {
        var v = store.Find(variant) ?? throw new DepthEditStoreException($"there is no variant '{variant}'");
        var p = new DepthEditPersistence(store, editor, v.Name, v.Kind);
        try { p.Restore(v); } catch { p.Detach(); throw; }
        return p;
    }

    /// <summary>A new variant of the given kind, created (with the editor's source depth pinned) at the first recorded operation.
    /// The name is <paramref name="name"/>, or name-2, name-3, … when taken: an existing variant is never replaced.</summary>
    public static DepthEditPersistence New(DepthEditStore store, DepthEditor editor, string name = DepthEditStore.KindLux, string kind = DepthEditStore.KindLux)
        => new(store, editor, store.FreeName(name), kind) { HistoryLoaded = true };

    /// <summary>A new, clean native variant created now — pinned depth, no edits — and made the active one. Other variants stay.</summary>
    public static DepthEditPersistence StartClean(DepthEditStore store, DepthEditor editor)
    {
        var p = New(store, editor);
        p.EnsureVariant();
        return p;
    }

    /// <summary>A new variant of the given kind created now — the editor's source depth pinned, no edits, the active one (a Lumen session
    /// without depth edits is imported this way). Other variants stay.</summary>
    public static DepthEditPersistence CreateNow(DepthEditStore store, DepthEditor editor, string name, string kind)
    {
        var p = New(store, editor, name, kind);
        p.EnsureVariant();
        return p;
    }

    /// <summary>The store's active variant when it has one, else a new native variant (created at the first edit).</summary>
    public static DepthEditPersistence OpenActiveOrNew(DepthEditStore store, DepthEditor editor)
        => store.ActiveVariant is string a ? Open(store, editor, a) : New(store, editor);

    EditGeometry GeometryOf(DepthEditor e) => new(e.LevelSize.W, e.LevelSize.H, e.Origin.X, e.Origin.Y, _cw, _ch, DepthEditor.TileW, DepthEditor.TileH);

    /// <summary>Create the variant: checks that the depth cache is the plain cache of the source depth (so that it can be rebuilt from
    /// the pinned depth alone), pins the source depth, makes the variant active.</summary>
    void EnsureVariant()
    {
        if (Variant != null) return;
        var src = Editor.Source; var dense = src.ToDense();
        var plain = new DepthImageCache((_cw, _ch)); plain.Set(new DepthImage(dense, src.W, src.H, src.W, new RectI(0, 0, src.W, src.H)));
        if (!MemoryMarshal.AsBytes(plain.Current.Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(_baseline.AsSpan())))
            throw new StoreGeometryException("the editor's depth cache is not the cache of its source depth; such a session cannot be stored");
        Store.InTransaction(() =>
        {
            Variant = Store.CreateVariant(_newName, _newKind, GeometryOf(Editor), dense, src.W, src.H);
            Store.ActiveVariant = Variant.Name;
        });
    }

    float[] BaselineWindow(int tx, int ty, int w, int h)
    {
        int x0 = Editor.Origin.X + tx * DepthEditor.TileW, y0 = Editor.Origin.Y + ty * DepthEditor.TileH;
        if (x0 < 0 || y0 < 0 || x0 + w > _cw || y0 + h > _ch) throw new StoreChecksumException("a stored tile lies outside the depth cache");
        var o = new float[w * h];
        for (int y = 0; y < h; y++) Array.Copy(_baseline, (long)(y0 + y) * _cw + x0, o, y * w, w);
        return o;
    }

    void Restore(VariantInfo v)
    {
        if (v.Geometry != GeometryOf(Editor))
            throw new StoreGeometryException($"variant '{v.Name}' was edited on a different geometry ({v.Geometry}) than this session's ({GeometryOf(Editor)})");
        var src = Editor.Source;
        if (src.W != v.DepthW || src.H != v.DepthH || !DepthEditStore.DepthFingerprint(src.ToDense(), src.W, src.H).AsSpan().SequenceEqual(v.DepthSha))
            throw new StoreGeometryException($"the editor was not opened on the depth variant '{v.Name}' pins");
        var tiles = Store.ReadTiles(v, BaselineWindow);
        foreach (var t in tiles)
        {
            if (t.Tx < 0 || t.Ty < 0 || t.Tx >= Editor.Nx || t.Ty >= Editor.Ny) throw new StoreChecksumException($"tile ({t.Tx}, {t.Ty}) of variant '{v.Name}' lies outside the tile grid");
            var r = Editor.TileRect(t.Tx, t.Ty);
            if (r.Width != t.W || r.Height != t.H) throw new StoreChecksumException($"tile ({t.Tx}, {t.Ty}) of variant '{v.Name}' has the wrong size");
        }
        var img = Editor.Cache.Current; var layer = Editor.Layer;
        if (v.LayerAllocated) { layer.Mask = new byte[(long)_cw * _ch]; layer.Background = new float[(long)_cw * _ch]; }
        foreach (var t in tiles)
        {
            int x0 = Editor.Origin.X + t.Tx * DepthEditor.TileW, y0 = Editor.Origin.Y + t.Ty * DepthEditor.TileH;
            for (int y = 0; y < t.H; y++) Array.Copy(t.Cache, y * t.W, img.Data, img.Offset + (long)(y0 + y) * img.Stride + x0, t.W);
            if (t.Version >= 0) Editor.RestoreTile(t.Tx, t.Ty, (uint)t.Version, t.Depth, t.Background, t.Mask);
            if (t.LayerBackground != null || t.LayerMask != null)
            {
                if (layer.Mask == null) throw new StoreChecksumException($"tile ({t.Tx}, {t.Ty}) of variant '{v.Name}' has edit-layer planes but the variant has no edit layer");
                for (int y = 0; y < t.H; y++)
                {
                    if (t.LayerBackground != null) Array.Copy(t.LayerBackground, y * t.W, layer.Background!, (long)(y0 + y) * _cw + x0, t.W);
                    if (t.LayerMask != null) Array.Copy(t.LayerMask, y * t.W, layer.Mask, (long)(y0 + y) * _cw + x0, t.W);
                }
            }
            _changes.Seen[(t.Tx, t.Ty)] = t.Version;
        }
        layer.Rects.Clear(); layer.Rects.AddRange(v.LayerRects);
        Editor.RestoreCursor((uint)v.Cursor);
        Variant = v; _changes.Clear();
        // the log is only read here (never replayed): an empty log means there is no history to rebuild, and strokes after the last
        // quick-select reset mean the selection has to be rebuilt before the next quick-select call
        try
        {
            var log = Store.ReadLog(v.Id);
            HistoryLoaded = log.Count == 0;
            _quickPending = PendingQuick(log).Count != 0;
        }
        catch (StoreChecksumException e) { MarkUnreliable(e.Message); }
    }

    static List<EditOp> PendingQuick(List<EditOp> log)
    {
        int last = log.FindLastIndex(o => o.Kind == EditOp.QuickReset);
        return log.Skip(last + 1).Where(o => o.Kind is EditOp.QuickStroke or EditOp.QuickClear).ToList();
    }

    void MarkUnreliable(string why)
    {
        _historyTried = true; HistoryLoaded = false; _quickPending = false;
        if (Variant != null) { Variant.History = HistoryState.Unreliable; Variant.HistoryNote = why; Store.SetHistoryState(Variant.Id, HistoryState.Unreliable, why); }
    }

    // ------------------------------------------------------------------------------------------------ snapshot

    /// <summary>The present state of one tile of an editor, as the snapshot stores it.</summary>
    static TileRecord BuildRecord(DepthEditor e, int tx, int ty)
    {
        var r = e.TileRect(tx, ty); int w = r.Width, h = r.Height, x0 = r.X0 + e.Origin.X, y0 = r.Y0 + e.Origin.Y;
        var t = new TileRecord { Tx = tx, Ty = ty, W = w, H = h, Cache = DepthEditStore.Window(e.Cache.Current, x0, y0, w, h) };
        if (e.CurrentVersion(tx, ty) is { } v) { t.Version = v.Version; t.Depth = v.Depth; t.Background = v.Background; t.Mask = v.Mask; }
        var layer = e.Layer;
        if (layer.Mask != null)
        {
            var lb = new float[w * h]; var lm = new byte[w * h]; bool anyB = false, anyM = false;
            for (int y = 0; y < h; y++)
            {
                Array.Copy(layer.Background!, (long)(y0 + y) * layer.W + x0, lb, y * w, w);
                Array.Copy(layer.Mask, (long)(y0 + y) * layer.W + x0, lm, y * w, w);
            }
            foreach (uint b in MemoryMarshal.Cast<float, uint>(lb.AsSpan())) if (b != 0) { anyB = true; break; }
            foreach (byte b in lm) if (b != 0) { anyM = true; break; }
            if (anyB) t.LayerBackground = lb;
            if (anyM) t.LayerMask = lm;
        }
        return t;
    }

    void Save(EditOp op)
    {
        var sw = Stopwatch.StartNew(); var e = Editor; var v = Variant!;
        var records = _changes.Take();
        op.Result = ResultDigest(e, records);
        long bytes = 0;
        Store.InTransaction(() =>
        {
            Store.AppendLog(v.Id, op);
            foreach (var t in records) bytes += Store.WriteTile(v.Id, t, BaselineWindow(t.Tx, t.Ty, t.W, t.H));
            // the undo floor is only known while the editor holds the full history (a session opened from a snapshot has its floor
            // at the cursor until the log has been replayed): −1 = not recorded
            Store.WriteVariantState(v.Id, e.Cursor, HistoryLoaded ? e.Base : -1, e.Layer.Mask != null, e.Layer.Rects);
        });
        v.Cursor = e.Cursor; v.Base = HistoryLoaded ? e.Base : -1; v.LayerAllocated = e.Layer.Mask != null; v.LayerRects = e.Layer.Rects.ToList();
        LastSaveMs = sw.Elapsed.TotalMilliseconds; LastSavedTiles = records.Count; LastSavedBytes = bytes;
    }

    // ------------------------------------------------------------------------------------------------ running and recording operations

    /// <summary>Run one logged operation on an editor. Returns what happened; the errors the editor raises for a call it refuses
    /// (<see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>) are an outcome, not a failure of the replay.</summary>
    (EditOutcome Outcome, string? Message, Exception? Error) Apply(DepthEditor ed, EditOp op, Func<uint>? random, QuickSelectTrace? trace = null)
    {
        var saved = ed.RandomDevice;
        try
        {
            switch (op.Kind)
            {
                case EditOp.Brush: ed.PushBrush(op.PointList(), EditOp.Float(op.Radius), EditOp.Float(op.Depth), op.Adaptive ?? false); return (EditOutcome.Done, null, null);
                case EditOp.Lasso: ed.PushLasso(op.PointList(), EditOp.Float(op.Depth), EditOp.Float(op.Feather)); return (EditOutcome.Done, null, null);
                case EditOp.SurfaceHeal: ed.PushSurfaceHeal(op.PointList(), EditOp.Float(op.Radius)); return (EditOutcome.Done, null, null);
                case EditOp.EdgeHeal:
                    return (ed.PushEdgeHeal(op.PointList(), EditOp.Float(op.Radius), EditOp.Float(op.Lo), EditOp.Float(op.Hi)) ? EditOutcome.Done : EditOutcome.Nothing, null, null);
                case EditOp.QuickStroke:
                    if (random != null) ed.RandomDevice = random;
                    ed.AddQuickSelectStrokes(op.PointList(), EditOp.Float(op.Radius), op.RectValue(), op.Level ?? throw new InvalidDataException("a quick-select stroke lacks its level"), op.Positive ?? true, trace);
                    return (EditOutcome.Done, null, null);
                case EditOp.QuickApply: ed.PushQuickSelect(EditOp.Float(op.Depth)); return (EditOutcome.Done, null, null);
                case EditOp.QuickReset: ed.ResetQuickSelect(); return (EditOutcome.Done, null, null);
                case EditOp.QuickClear: ed.ClearQuickSelectMask(); return (EditOutcome.Done, null, null);
                case EditOp.Undo: return (ed.Undo() ? EditOutcome.Done : EditOutcome.Nothing, null, null);
                case EditOp.Redo: return (ed.Redo() ? EditOutcome.Done : EditOutcome.Nothing, null, null);
                case EditOp.Reset: ed.Reset(); return (EditOutcome.Done, null, null);
                case EditOp.LumenImport:
                {
                    var raw = Store.ReadBlob(Variant!.Id, LumenRecordBlob) ?? throw new StoreChecksumException("the imported Lumen record is missing from the database");
                    var state = LumenSessionReader.Read(raw).DepthEditor ?? throw new StoreChecksumException("the stored Lumen record holds no depth edits");
                    return (ed.ImportLumenState(state) ? EditOutcome.Done : EditOutcome.Nothing, null, null);
                }
                default: throw new StoreChecksumException($"unknown operation '{op.Kind}' in the log");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return (EditOutcome.Failed, e.Message, e); }
        finally { ed.RandomDevice = saved; }
    }

    /// <summary>Run <paramref name="op"/> on the live editor, record it and save the tiles it changed. An error the editor raised is
    /// recorded as the operation's outcome and then rethrown.</summary>
    EditOutcome Record(EditOp op, Func<uint>? random = null, QuickSelectTrace? trace = null)
    {
        EnsureVariant();
        var sw = Stopwatch.StartNew();
        // a provider may stop a slow operation (OperationCanceledException) before it writes anything: the editor is then put back exactly
        // as it was (DepthEditor.RollBack) and nothing is recorded
        var checkpoint = Editor.TakeCheckpoint();
        (EditOutcome outcome, string? message, Exception? error) r;
        try { r = Apply(Editor, op, random, trace); }
        catch (OperationCanceledException) { Editor.RollBack(checkpoint); throw; }
        var (outcome, message, error) = r;
        LastOperationMs = sw.Elapsed.TotalMilliseconds;
        op.Outcome = outcome; op.Message = message; op.CursorAfter = Editor.Cursor;
        // the editor holds the result now: readers may be given it before the save (the save writes exactly the same state)
        AfterApply?.Invoke();
        Save(op);
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return outcome;
    }

    public void PushBrush(IReadOnlyList<EditPoint> points, float radius, float depth, bool adaptive = false)
        => Record(new EditOp { Kind = EditOp.Brush, Points = EditOp.PointBits(points), Radius = EditOp.Bits(radius), Depth = EditOp.Bits(depth), Adaptive = adaptive });

    public void PushLasso(IReadOnlyList<EditPoint> points, float depth, float feather)
        => Record(new EditOp { Kind = EditOp.Lasso, Points = EditOp.PointBits(points), Depth = EditOp.Bits(depth), Feather = EditOp.Bits(feather) });

    public void PushSurfaceHeal(IReadOnlyList<EditPoint> points, float radius)
        => Record(new EditOp { Kind = EditOp.SurfaceHeal, Points = EditOp.PointBits(points), Radius = EditOp.Bits(radius) });

    /// <summary>The edge heal with the in-focus interval of the depth-effect settings currently shown (see
    /// <see cref="DepthEditor.PushEdgeHeal"/>); false = the heal found nothing to refine.</summary>
    public bool PushEdgeHeal(IReadOnlyList<EditPoint> points, float radius, float lo, float hi)
        => Record(new EditOp { Kind = EditOp.EdgeHeal, Points = EditOp.PointBits(points), Radius = EditOp.Bits(radius), Lo = EditOp.Bits(lo), Hi = EditOp.Bits(hi) }) == EditOutcome.Done;

    /// <summary>Undo. Rebuilds the history first when the session was opened from a snapshot.</summary>
    public bool Undo()
    {
        EnsureHistory();
        if (!Editor.CanUndo) return false;
        return Record(new EditOp { Kind = EditOp.Undo }) == EditOutcome.Done;
    }

    public bool Redo()
    {
        EnsureHistory();
        if (!Editor.CanRedo) return false;
        return Record(new EditOp { Kind = EditOp.Redo }) == EditOutcome.Done;
    }

    public void Reset() => Record(new EditOp { Kind = EditOp.Reset });

    /// <summary>A quick-select stroke. Its random values are derived from the capture and the stroke (<see cref="QuickSelectSeed"/>)
    /// unless <see cref="PinnedRandomDevice"/> is set, and recorded in the log.</summary>
    public void AddQuickSelectStrokes(IReadOnlyList<EditPoint> points, float radius, RectI rect, int level, bool positive = true, QuickSelectTrace? trace = null)
    {
        // the saved strokes a selection needs are replayed whole (a stop applies to the new stroke only)
        var stop = Editor.QuickCancel; Editor.QuickCancel = default;
        try { EnsureVariant(); EnsureQuickSelect(); } finally { Editor.QuickCancel = stop; }
        var op = new EditOp { Kind = EditOp.QuickStroke, Points = EditOp.PointBits(points), Radius = EditOp.Bits(radius), Rect = new[] { rect.X0, rect.Y0, rect.X1, rect.Y1 }, Level = level, Positive = positive };
        var drawn = new List<uint>(); uint k = 0;
        var seed = QuickSelectSeed.Seed(_captureSha, (uint)Store.LogCount(Variant!.Id, EditOp.QuickStroke), points, rect, level, positive);
        var pinned = PinnedRandomDevice;
        uint Draw() { uint v = pinned != null ? pinned() : QuickSelectSeed.Value(seed, k++); drawn.Add(v); op.Random = drawn.ToArray(); return v; }
        op.Random = Array.Empty<uint>();
        Record(op, Draw, trace);
    }

    public void PushQuickSelect(float depth) { EnsureQuickSelect(); Record(new EditOp { Kind = EditOp.QuickApply, Depth = EditOp.Bits(depth) }); }
    public void ResetQuickSelect() { _quickPending = false; Record(new EditOp { Kind = EditOp.QuickReset }); }
    public void ClearQuickSelectMask() { EnsureQuickSelect(); Record(new EditOp { Kind = EditOp.QuickClear }); }
    /// <summary>The current selection (see <see cref="DepthEditor.QuickSelectMask"/>).</summary>
    public byte[]? QuickSelectMask { get { EnsureQuickSelect(); return Editor.QuickSelectMask; } }

    /// <summary>The Lumen session's depth-editor record is stored verbatim and its tiles are loaded (the first entry of an imported
    /// variant's log).</summary>
    public bool ImportLumen(ReadOnlySpan<byte> depthEditorRecord, string note)
    {
        EnsureVariant();
        if (Store.LogCount(Variant!.Id) != 0) throw new DepthEditStoreException("a Lumen session can only be imported into a new variant");
        Store.WriteBlob(Variant.Id, LumenRecordBlob, depthEditorRecord);
        return Record(new EditOp { Kind = EditOp.LumenImport, Note = note }) == EditOutcome.Done;
    }

    /// <summary>
    /// The quick-select object is working state, not part of the snapshot. When the log holds strokes after the last reset, the
    /// selection they built is restored here — by running just those strokes again with their recorded random values (strokes do not
    /// read the depth) — before the next quick-select call.
    /// </summary>
    public void EnsureQuickSelect() => EnsureQuickSelect(default);

    /// <summary>Whether the saved quick-select strokes still have to be replayed (<see cref="EnsureQuickSelect()"/>).</summary>
    public bool QuickSelectPending => _quickPending;

    /// <summary><see cref="EnsureQuickSelect()"/> that can be stopped (the app restores the selection in the background): a stopped
    /// replay puts the editor back as it was (checkpoint) and stays pending, to be run again later or by the next quick-select call.</summary>
    public void EnsureQuickSelect(CancellationToken cancel)
    {
        if (!_quickPending) return;
        _quickPending = false;
        List<EditOp> log;
        try { log = Store.ReadLog(Variant!.Id); } catch (StoreChecksumException e) { MarkUnreliable(e.Message); return; }
        var checkpoint = Editor.TakeCheckpoint(); var stop = Editor.QuickCancel; Editor.QuickCancel = cancel;
        try
        {
            foreach (var op in PendingQuick(log))
            {
                cancel.ThrowIfCancellationRequested();
                var q = new Queue<uint>(op.Random ?? Array.Empty<uint>());
                var (outcome, message, _) = Apply(Editor, op, () => q.Count != 0 ? q.Dequeue() : throw new InvalidOperationException("a quick-select stroke drew more random values than its log entry holds"));
                if (outcome != op.Outcome || message != op.Message || q.Count != 0)
                { MarkUnreliable($"log entry {op.Seq} ({op.Kind}) did not repeat as recorded"); Editor.ResetQuickSelect(); return; }
            }
        }
        catch (OperationCanceledException) { Editor.RollBack(checkpoint); _quickPending = true; throw; }
        finally { Editor.QuickCancel = stop; }
    }

    // ------------------------------------------------------------------------------------------------ history: replay and verification

    /// <summary>Make sure the editor holds the full undo history: replay the log, verify, adopt. False when the replay did not
    /// reproduce the snapshot (the snapshot is untouched, the history is marked unreliable and the undo floor stays where it is).</summary>
    public bool EnsureHistory() => EnsureHistory(default, null);

    /// <summary><see cref="EnsureHistory()"/> that can be stopped between log entries (and inside them, by the editor's providers): a stopped
    /// replay changes nothing and can be started again. <paramref name="progress"/> gets (entries replayed, entries).</summary>
    public bool EnsureHistory(CancellationToken cancel, Action<int, int>? progress)
    {
        if (HistoryLoaded) return true;
        if (_historyTried || Variant == null) return false;
        var rep = Replay(cancel, progress);
        _historyTried = true;
        LastReplay = rep;
        if (!rep.Ok) { MarkUnreliable(rep.Problems[0]); return false; }
        Editor.AdoptState(rep.Replayed!);
        HistoryLoaded = true; _quickPending = false;
        Variant.History = HistoryState.Verified; Variant.HistoryNote = null;
        Store.SetHistoryState(Variant.Id, HistoryState.Verified, null);
        return true;
    }

    /// <summary>
    /// Replay the whole log on a second editor that starts from the unedited depth cache, and compare with the snapshot: every
    /// entry's recorded outcome, the cursor and floor, the edit layer's rect list, and for every tile of the grid the SHA-256 of each
    /// plane against the stored checksum (a tile without a stored row must be untouched). Changes nothing — neither the live editor
    /// nor the database.
    /// </summary>
    public ReplayReport Replay() => Replay(default, null);

    /// <summary><see cref="Replay()"/>, stoppable between entries (<see cref="OperationCanceledException"/>), with progress (entries done, entries).</summary>
    public ReplayReport Replay(CancellationToken cancel, Action<int, int>? progress)
    {
        var rep = new ReplayReport(); var total = Stopwatch.StartNew();
        if (Variant == null) return rep;
        List<EditOp> log;
        try { log = Store.ReadLog(Variant.Id); }
        catch (StoreChecksumException e) { rep.Problems.Add(e.Message); return rep; }
        var cache = new DepthImageCache((_cw, _ch));
        Array.Copy(_baseline, cache.Current.Data, _baseline.Length);
        var fork = Editor.Fork(cache, Editor.Source);
        var changes = new ChangeTracker(fork);
        int done = 0;
        foreach (var op in log)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(done++, log.Count);
            var sw = Stopwatch.StartNew();
            var q = new Queue<uint>(op.Random ?? Array.Empty<uint>());
            EditOutcome outcome; string? message;
            try
            {
                (outcome, message, _) = Apply(fork, op, () => q.Count != 0 ? q.Dequeue() : throw new InvalidOperationException("a quick-select stroke drew more random values than its log entry holds"));
            }
            catch (Exception e) when (e is DepthEditStoreException or InvalidDataException)
            { rep.Problems.Add($"log entry {op.Seq} ({op.Kind}) cannot be replayed: {e.Message}"); break; }
            rep.Timings.Add((op.Seq, op.Kind, sw.Elapsed.TotalMilliseconds));
            var dsw = Stopwatch.StartNew(); var result = ResultDigest(fork, changes.Take()); rep.DigestMs += dsw.Elapsed.TotalMilliseconds;
            if (outcome != op.Outcome || message != op.Message)
                rep.Problems.Add($"log entry {op.Seq} ({op.Kind}): recorded {op.Outcome}{(op.Message != null ? $" (\"{op.Message}\")" : "")}, the replay gave {outcome}{(message != null ? $" (\"{message}\")" : "")}");
            else if (q.Count != 0) rep.Problems.Add($"log entry {op.Seq} ({op.Kind}): the replay drew fewer random values than recorded");
            else if (fork.Cursor != op.CursorAfter) rep.Problems.Add($"log entry {op.Seq} ({op.Kind}): cursor {fork.Cursor} after the replay, {op.CursorAfter} recorded");
            else if (!result.AsSpan().SequenceEqual(op.Result)) rep.Problems.Add($"log entry {op.Seq} ({op.Kind}): the replay does not reproduce the result recorded with the entry");
            if (rep.Problems.Count != 0) break;
        }
        var vsw = Stopwatch.StartNew();
        if (rep.Problems.Count == 0) Verify(fork, rep);
        rep.VerifyMs = vsw.Elapsed.TotalMilliseconds; rep.TotalMs = total.Elapsed.TotalMilliseconds;
        changes.Detach();
        if (rep.Ok) rep.Replayed = fork;
        return rep;
    }

    void Verify(DepthEditor fork, ReplayReport rep)
    {
        var v = Store.Find(Variant!.Name)!;
        if (fork.Cursor != v.Cursor) rep.Problems.Add($"cursor {fork.Cursor} after the replay, {v.Cursor} in the snapshot");
        if (v.Base >= 0 && fork.Base != v.Base) rep.Problems.Add($"undo floor {fork.Base} after the replay, {v.Base} in the snapshot");
        if ((fork.Layer.Mask != null) != v.LayerAllocated || !fork.Layer.Rects.SequenceEqual(v.LayerRects)) rep.Problems.Add("the edit layer's rect list differs from the snapshot");
        var sums = Store.ReadTileChecksums(v.Id).ToDictionary(t => (t.Tx, t.Ty));
        static bool Same(byte[]? stored, byte[]? got) => stored == null ? got == null : got != null && stored.AsSpan().SequenceEqual(got);
        for (int tx = 0; tx < fork.Nx; tx++)
            for (int ty = 0; ty < fork.Ny; ty++)
            {
                var t = BuildRecord(fork, tx, ty); rep.TilesChecked++;
                if (!sums.TryGetValue((tx, ty), out var s))
                {
                    bool untouched = t.Version < 0 && t.LayerBackground == null && t.LayerMask == null
                        && MemoryMarshal.AsBytes(t.Cache.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(BaselineWindow(tx, ty, t.W, t.H).AsSpan()));
                    if (!untouched) rep.Problems.Add($"tile ({tx}, {ty}): changed by the replay, absent from the snapshot");
                    continue;
                }
                var bad = new List<string>();
                if (s.Version != t.Version) bad.Add($"version {t.Version} vs {s.Version}");
                if (!Same(s.Cache, PlaneCodec.Sha(t.Cache))) bad.Add("depth");
                if (!Same(s.Depth, t.Depth == null ? null : PlaneCodec.Sha(t.Depth))) bad.Add("tile-store depth");
                if (!Same(s.Background, t.Background == null ? null : PlaneCodec.Sha(t.Background))) bad.Add("background depth");
                if (!Same(s.Mask, t.Mask == null ? null : PlaneCodec.Sha(t.Mask))) bad.Add("layer mask");
                if (!Same(s.LayerBackground, t.LayerBackground == null ? null : PlaneCodec.Sha(t.LayerBackground))) bad.Add("edit-layer background");
                if (!Same(s.LayerMask, t.LayerMask == null ? null : PlaneCodec.Sha(t.LayerMask))) bad.Add("edit-layer mask");
                rep.PlanesChecked += 6;
                if (bad.Count != 0) rep.Problems.Add($"tile ({tx}, {ty}): the replay differs from the snapshot in {string.Join(", ", bad)}");
            }
        foreach (var k in sums.Keys) if (k.Tx >= fork.Nx || k.Ty >= fork.Ny) rep.Problems.Add($"tile ({k.Tx}, {k.Ty}) of the snapshot lies outside the grid");
    }

    // ------------------------------------------------------------------------------------------------ re-applying edits to another depth

    /// <summary>
    /// "Re-apply the edits to the current depth": run this variant's log, entry by entry, through <paramref name="target"/> — a session
    /// bound to a new variant on an editor that was built on the other depth. Every operation is recorded afresh there with the outcome
    /// it has on that depth (quick-select strokes reuse their recorded random values); this variant is not changed. Returns the number
    /// of entries whose outcome differs from the original.
    /// </summary>
    public int ReapplyTo(DepthEditPersistence target)
    {
        if (Variant == null) return 0;
        if (target.Variant != null && target.Store.LogCount(target.Variant.Id) != 0) throw new DepthEditStoreException("edits can only be re-applied into a new, empty variant");
        int differ = 0;
        foreach (var op in Store.ReadLog(Variant.Id))
        {
            var copy = EditOp.FromJson(op.Kind, op.ToJson());
            EditOutcome outcome;
            try
            {
                if (op.Kind == EditOp.LumenImport)
                {
                    var raw = Store.ReadBlob(Variant.Id, LumenRecordBlob) ?? throw new StoreChecksumException("the imported Lumen record is missing from the database");
                    outcome = target.ImportLumen(raw, op.Note ?? "") ? EditOutcome.Done : EditOutcome.Nothing;
                }
                else if (op.Kind == EditOp.QuickStroke)
                {
                    var q = new Queue<uint>(op.Random ?? Array.Empty<uint>()); uint k = 0x80000000;
                    var seed = QuickSelectSeed.Seed(_captureSha, (uint)op.Seq, op.PointList(), op.RectValue(), op.Level ?? 0, op.Positive ?? true);
                    var drawn = new List<uint>(); copy.Random = Array.Empty<uint>();
                    target.EnsureVariant();
                    outcome = target.Record(copy, () => { uint v = q.Count != 0 ? q.Dequeue() : QuickSelectSeed.Value(seed, k++); drawn.Add(v); copy.Random = drawn.ToArray(); return v; });
                }
                else if (op.Kind is EditOp.Undo or EditOp.Redo)
                {
                    // on another depth an earlier operation may have had another outcome, so an undo may have nothing to undo
                    bool can = op.Kind == EditOp.Undo ? target.Editor.CanUndo : target.Editor.CanRedo;
                    outcome = can ? target.Record(copy) : EditOutcome.Nothing;
                }
                else outcome = target.Record(copy);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { outcome = EditOutcome.Failed; }
            if (outcome != op.Outcome) differ++;
        }
        return differ;
    }
}
