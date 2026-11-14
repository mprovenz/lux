using System.Collections.Immutable;
using System.Collections.Concurrent;
using Avalonia;
using PixelSize = Avalonia.PixelSize;
using Lux.App.Develop;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.Library;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Fake;

/// <summary>Options of the fake editor.</summary>
public sealed record FakeDevelopOptions
{
    public FakeTimings Timings { get; init; } = FakeTimings.Default;
    /// <summary>Time to open a capture, ms.</summary>
    public int OpenDelayMs { get; init; } = 120;
    /// <summary>Time from the last change to the automatic save, ms.</summary>
    public int SaveDelayMs { get; init; } = 150;
    /// <summary>Time to rebuild one saved depth step in the background, ms (× the timings' tool scale); null = 400.</summary>
    public int? RebuildStepMs { get; init; }
}

/// <summary>
/// The fake editor over <see cref="FakeLibrary"/>: per-capture recipes in memory (mirrored into the library's edit summary and thumbnail tint,
/// as the real store does), presets, the clipboard, pasting to captures that are not open (undoable batches).
/// </summary>
public sealed class FakeDevelopService : IDevelopService
{
    readonly FakeLibrary _lib;
    readonly List<PresetInfo> _presets = new();
    readonly Dictionary<long, List<(string Id, (Dictionary<string, double> Values, GeometryState Geometry)? Before)>> _batches = new();
    long _nextPreset = 1, _nextBatch = 1;
    internal readonly ConcurrentDictionary<string, (Dictionary<string, double> Values, GeometryState Geometry)> Recipes = new();
    /// <summary>The depth part of each capture's saved state (this run): reopening shows its edits as saved steps.</summary>
    internal readonly ConcurrentDictionary<string, FakeDepthState> DepthRecipes = new();

    public FakeDevelopService(FakeLibrary lib, FakeDevelopOptions? options = null)
    {
        _lib = lib;
        Options = options ?? new FakeDevelopOptions();
        void P(string name, EditGroups groups, Dictionary<string, double> v) =>
            _presets.Add(new PresetInfo(_nextPreset++, name, groups, new EditSet(groups, v, GeometryState.Identity)));
        P("Black and white", EditGroups.Tone | EditGroups.Colour, new() { [AdjustmentKeys.Saturation] = -100, [AdjustmentKeys.Contrast] = 15 });
        P("Crisp", EditGroups.Tone | EditGroups.Sharpening, new() { [AdjustmentKeys.Contrast] = 20, [AdjustmentKeys.Clarity] = 25, [AdjustmentKeys.Sharpening] = 30 });
        P("Warm evening", EditGroups.Tone | EditGroups.Colour, new() { [AdjustmentKeys.Temperature] = 6800, [AdjustmentKeys.Vibrance] = 18, [AdjustmentKeys.Shadows] = 12 });
    }

    public FakeDevelopOptions Options { get; }
    public FakeLibrary Library => _lib;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<AdjustmentSpec> DefaultSpecs => AdjustmentSpec.Standard();
    public bool SupportsPerSlider => true;
    public EditSet? Clipboard { get; private set; }
    public string? ClipboardSource { get; private set; }
    public event Action? PresetsChanged;
    public event Action? ClipboardChanged;
    public IReadOnlyList<PresetInfo> Presets { get { lock (_presets) return _presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); } }
    /// <summary>Sessions opened (tests).</summary>
    public List<FakeDevelopSession> Opened { get; } = new();
    internal FakeDevelopSession? OpenSession { get; set; }

    internal static IReadOnlyList<AdjustmentSpec> SpecsFor(CaptureItem item) => AdjustmentSpec.Standard(item.IsStacked ? 4200 : 5600, 0);
    internal static Dictionary<string, double> Defaults(CaptureItem item) => SpecsFor(item).ToDictionary(s => s.Key, s => s.Default);

    public async Task<IDevelopSession> OpenAsync(CaptureItem item, CancellationToken cancel)
    {
        if (Options.OpenDelayMs > 0) await Task.Delay(Options.OpenDelayMs, cancel).ConfigureAwait(false);
        if (item.Missing) throw new LibraryException("The capture file is missing: reconnect its drive or rescan the folder.");
        if (item.Unreadable) throw new LibraryException(item.Error ?? "Lux can't read this capture.");
        var s = new FakeDevelopSession(this, item);
        lock (Opened) Opened.Add(s);
        OpenSession = s;
        return s;
    }

    (Dictionary<string, double> Values, GeometryState Geometry) RecipeOf(CaptureItem item) =>
        Recipes.TryGetValue(item.Id, out var r) ? (new Dictionary<string, double>(r.Values), r.Geometry) : (Defaults(item), GeometryState.Identity);

    public Task<EditSet> CopyAsync(string captureId, string name, EditGroups groups, CancellationToken cancel)
    {
        var item = _lib.Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var (values, geometry) = OpenSession?.Item.Id == captureId ? (new Dictionary<string, double>(OpenSession.Snapshot().Values), OpenSession.Geometry) : RecipeOf(item);
        var set = new EditSet(EditGroups.All, WithoutAsShot(values, item), geometry).Only(groups);
        SetClipboard(set, name);
        return Task.FromResult(set);
    }

    /// <summary>Temperature and tint at the photo's own white balance are left out of a copy: the target keeps its own ("as shot").</summary>
    internal static Dictionary<string, double> WithoutAsShot(IReadOnlyDictionary<string, double> values, CaptureItem item)
    {
        var d = new Dictionary<string, double>(values);
        foreach (var s in SpecsFor(item).Where(s => s.Key is AdjustmentKeys.Temperature or AdjustmentKeys.Tint))
            if (d.TryGetValue(s.Key, out var v) && Math.Abs(v - s.Default) < 1e-9) d.Remove(s.Key);
        return d;
    }

    public void SetClipboard(EditSet set, string sourceName, string? sourceCaptureId = null)
    {
        Clipboard = set;
        ClipboardSource = sourceName;
        ClipboardChanged?.Invoke();
    }

    public Task<PasteResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel) =>
        Clipboard is { } c ? ApplyToMany(captureIds, c.Only(groups), cancel) : Task.FromResult(new PasteResult(0, 0, Array.Empty<string>(), 0));

    async Task<PasteResult> ApplyToMany(IReadOnlyCollection<string> captureIds, EditSet set, CancellationToken cancel, string label = "Paste adjustments")
    {
        int n = 0, cropSkipped = 0;
        var failures = new List<string>();
        var undo = new List<(string, (Dictionary<string, double>, GeometryState)?)>();
        foreach (var id in captureIds)
        {
            cancel.ThrowIfCancellationRequested();
            var item = _lib.Get(id);
            if (item == null || item.Missing || item.Unreadable) { failures.Add($"{item?.Name ?? id}: the capture cannot be read"); continue; }
            if (OpenSession?.Item.Id == id) { if (!OpenSession.Apply(set, label)) cropSkipped++; n++; continue; }
            undo.Add((id, Recipes.TryGetValue(id, out var before) ? before : null));
            var (values, geometry) = RecipeOf(item);
            foreach (var (k, v) in set.Values) values[k] = v;
            if ((set.Groups & EditGroups.RotationAndMirror) != 0) geometry = geometry with { TurnDegrees = set.Geometry.TurnDegrees, Mirrored = set.Geometry.Mirrored };
            if ((set.Groups & EditGroups.Crop) != 0)
            {
                // a crop is only pasted onto a photo of the same shape (Lumen's "crop property was skipped")
                bool sameShape = item.IsTelephoto == false;
                if (sameShape) geometry = geometry with { Crop = set.Geometry.Crop, Aspect = set.Geometry.Aspect }; else cropSkipped++;
            }
            Store(item, values, geometry);
            if ((set.Groups & EditGroups.DepthEffect) != 0)
            {
                // the saved depth effect of a photo that is not open (mirrored in the library's summary as the real store does)
                if (DepthRecipes.TryGetValue(id, out var kept)) DepthRecipes[id] = kept with { FNumber = set.DepthFNumber, FocusMm = set.DepthFocusMm ?? kept.FocusMm, FocusDefault = set.DepthFocusMm == null };
                var e = item.Edits;
                _lib.SetDepthForTesting(id, set.DepthFNumber is double f ? (float)f : null, set.DepthFocusMm is double mm ? (float)mm : null, e.Variant, e.OperationCount, e.Source, e.VariantCount);
            }
            n++;
            await Task.Yield();
        }
        long batch;
        lock (_batches) { batch = _nextBatch++; _batches[batch] = undo; }
        return new PasteResult(n, cropSkipped, failures, batch);
    }

    public Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel)
    {
        List<(string Id, (Dictionary<string, double> Values, GeometryState Geometry)? Before)>? undo;
        lock (_batches) { if (!_batches.Remove(batchId, out undo)) return Task.FromResult(false); }
        foreach (var (id, before) in undo)
        {
            var item = _lib.Get(id);
            if (item == null) continue;
            if (before is { } b) Store(item, b.Values, b.Geometry);
            else { Recipes.TryRemove(id, out _); Store(item, Defaults(item), GeometryState.Identity, keep: false); }
        }
        return Task.FromResult(true);
    }

    /// <summary>Keep a recipe and mirror it into the library (edit summary, thumbnail tint).</summary>
    internal void Store(CaptureItem item, Dictionary<string, double> values, GeometryState geometry, bool keep = true)
    {
        if (keep) Recipes[item.Id] = (values, geometry);
        var specs = SpecsFor(item);
        var changed = specs.Where(s => Math.Abs(values.GetValueOrDefault(s.Key, s.Default) - s.Default) > 1e-9).ToList();
        string? summary = changed.Count == 0 ? null : string.Join(", ", changed.Select(s => $"{s.Name.ToLowerInvariant()} {s.Format(values[s.Key])}"));
        _lib.SetEditsForTesting(item.Id, summary, !geometry.IsIdentity, changed.Count == 0 ? null : FakeScene.Filter(values, specs));
    }

    public Task<PresetInfo> CreatePresetAsync(string name, EditSet set, EditGroups groups, CancellationToken cancel)
    {
        name = name.Trim();
        PresetInfo p;
        lock (_presets)
        {
            if (_presets.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new LibraryException($"A preset called “{name}” exists already.");
            p = new PresetInfo(_nextPreset++, name, groups, set.Only(groups));
            _presets.Add(p);
        }
        PresetsChanged?.Invoke();
        return Task.FromResult(p);
    }

    public Task DeletePresetAsync(long presetId, CancellationToken cancel)
    {
        lock (_presets) _presets.RemoveAll(p => p.Id == presetId);
        PresetsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task<PresetInfo> RenamePresetAsync(long presetId, string newName, CancellationToken cancel)
    {
        newName = newName.Trim();
        PresetInfo renamed;
        lock (_presets)
        {
            if (_presets.Any(x => x.Id != presetId && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new LibraryException($"A preset called “{newName}” exists already.");
            int i = _presets.FindIndex(x => x.Id == presetId);
            if (i < 0) throw new LibraryException("The preset no longer exists.");
            renamed = _presets[i] with { Name = newName };
            _presets[i] = renamed;
        }
        PresetsChanged?.Invoke();
        return Task.FromResult(renamed);
    }

    public Task<PasteResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel)
    {
        PresetInfo? p;
        lock (_presets) p = _presets.FirstOrDefault(x => x.Id == presetId);
        return p == null ? Task.FromResult(new PasteResult(0, 0, new[] { "The preset no longer exists." }, 0)) : ApplyToMany(captureIds, p.Set, cancel, $"Preset “{p.Name}”");
    }
}

/// <summary>One open capture in the fake editor: state-based history, the fake pyramid tinted and framed by the edits, a short save.</summary>
public sealed class FakeDevelopSession : IDevelopSession
{
    /// <param name="TurnSaved">The orientation is decided (a geometry edit was saved, even one that came back to 0°): the display rotation of
    /// "show photos as held" no longer applies (the geometry-decided marker).</param>
    sealed record State(IReadOnlyDictionary<string, double> Values, GeometryState Geometry, bool TurnSaved = false, FakeDepthState? Depth = null);

    readonly FakeDevelopService _svc;
    readonly FakeTileSource _tiles;
    readonly List<(string Label, HistoryKind Kind, bool Saved, State State)> _history = new();
    readonly FakeDepthSession _depth;
    int _saved, _rebuilt, _barrier;
    bool _unreliable, _rebuildHeld;
    CancellationTokenSource? _rebuildCts;
    readonly State _original;
    readonly object _gate = new();
    State _current;
    Dictionary<string, double>? _drag;
    int _pos;
    SaveInfo _save;
    CancellationTokenSource? _saveCts;
    CaptureItem _item;
    bool _cropping;
    NormRect _cropFrame = NormRect.Full;
    CropAspect _cropAspect;
    ViewQuality? _cap;

    internal FakeDevelopSession(FakeDevelopService svc, CaptureItem item)
    {
        _svc = svc;
        _item = item;
        Specs = FakeDevelopService.SpecsFor(item);
        _original = new State(FakeDevelopService.Defaults(item), GeometryState.Identity);
        _current = svc.Recipes.TryGetValue(item.Id, out var r) ? new State(new Dictionary<string, double>(r.Values), r.Geometry, r.Geometry.HasTurnOrFlip) : _original;
        var size = item.LevelSizes is { Count: > 0 } ls && ls[0].Width > 0 ? new PixelSize(ls[0].Width, ls[0].Height) : new PixelSize(8320, 6240);
        var (index, stored) = svc.Library.SceneOf(item.Id);
        _tiles = new FakeTileSource(index, item.IsStacked, item.IsTelephoto, stored, size, FinestFor(item.BestQuality), Specs, _current.Values, svc.Options.Timings);
        _tiles.HistogramChanged += () => Changed?.Invoke();
        // depth: the saved depth edits of the current version come first in the history (steps of earlier sessions, rebuilt in the background)
        _depth = new FakeDepthSession(this, svc, _tiles, svc.Options.Timings);
        _depth.Changed += () => Changed?.Invoke();
        var depthState = FakeDepthSession.Initial(svc, item, _depth.Map, out _saved);
        _original = _original with { Depth = depthState with { FNumber = null, FocusDefault = true, FocusCapture = null } };
        _current = _current with { Depth = depthState };
        _unreliable = item.Edits.HistoryUnreliable;
        BuildHistory(depthState, imported: false);
        PushGeometry();
        _depth.Refresh(depthState, drag: false);
        _save = new SaveInfo(_svc.Recipes.ContainsKey(item.Id) ? SaveStatus.Saved : SaveStatus.Nothing, WhereOf(item));
        svc.Library.CapturesChanged += OnLibraryChanged;
    }

    int FinestFor(ViewQuality q)
    {
        var capped = _cap is { } c && c < q ? c : q;
        return capped switch { ViewQuality.FullDetail => 0, ViewQuality.Linear13 => 1, _ => 2 };
    }

    static string WhereOf(CaptureItem item) => item.ContextLocation == ContextLocation.LocalStore ? "On this computer" : "With the photo";

    void OnLibraryChanged(object? sender, CapturesChangedEventArgs e)
    {
        if (!e.Reset && !e.Changed.Contains(_item.Id)) return;
        if (_svc.Library.Get(_item.Id) is not { } it) return;
        bool turn;
        lock (_gate) { turn = it.DisplayRotation != _item.DisplayRotation; _item = it; }
        _tiles.SetFinestLevel(FinestFor(it.BestQuality));
        if (turn) PushGeometry();
        Avalonia.Threading.Dispatcher.UIThread.Post(_depth.ItemChanged);
        Changed?.Invoke();
    }

    public CaptureItem Item { get { lock (_gate) return _item; } }
    public ITileSource Tiles => _tiles;
    public FakeTileSource FakeTiles => _tiles;
    public IReadOnlyList<AdjustmentSpec> Specs { get; }
    public GeometryState Geometry => _current.Geometry;
    public double Get(string key) => _drag != null && _drag.TryGetValue(key, out var lv) ? lv : _current.Values.TryGetValue(key, out var v) ? v : Specs.First(s => s.Key == key).Default;
    public IReadOnlyList<string> History => _history.Select(h => h.Label).ToList();

    // ---- the first row and "Clear history"
    bool _cleared;
    public string FirstRowLabel => (_cleared ? "History cleared" : "Opened") + (StartEdits() is null ? " · original" : _cleared ? " · with edits" : " · with saved edits");
    public string? FirstRowTip => StartEdits() is { } e ? (_cleared ? "The photo when the history was cleared: " : "Saved edits it opened with: ") + e
                                                        : (_cleared ? "The photo when the history was cleared: no edits" : "The photo as captured: no saved edits");
    string? StartEdits()
    {
        if (_history.Count == 0) return null;
        var st = _history[0].State; var parts = new List<string>();
        foreach (var sp in Specs)
            if (st.Values.TryGetValue(sp.Key, out var v) && Math.Abs(v - sp.Default) > sp.Step / 2) parts.Add($"{sp.Name.ToLowerInvariant()} {sp.Format(v)}");
        if (st.Geometry != GeometryState.Identity) parts.Add("crop or rotation");
        if (st.Depth is { FNumber: not null } d0) parts.Add("depth effect");
        if (_saved > 0 || st.Depth is { } dd && dd.CurrentOps.Count > 0) parts.Add("depth edits");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
    public bool CanClearHistory => _history.Count > 1;
    public void ClearHistory()
    {
        if (_history.Count <= 1) return;
        _rebuildCts?.Cancel();
        // the picture stays: the current state becomes the list's start, the steps go
        var cur = _current;
        _history.Clear();
        _history.Add(("Opened", HistoryKind.Opened, false, cur));
        _pos = 0; _barrier = 0; _saved = 0; _rebuilt = 0;
        _cleared = true;
        Changed?.Invoke();
    }
    public IReadOnlyList<HistoryItem> HistoryItems => _history.Select((h, i) => new HistoryItem(h.Label, h.Kind, h.Saved, Undoable(i))).ToList();
    public int HistoryPosition => _pos;
    public bool CanUndo => _pos > 0 && Undoable(_pos);
    public bool CanRedo => _pos < _history.Count - 1;
    public IDepthSession? Depth => _depth;
    public FakeDepthSession FakeDepth => _depth;

    /// <summary>A step can be undone: not one before a depth reset (Lumen's reset cannot be undone), and a saved step only once the background
    /// replay has rebuilt it (never when the history is unreliable). Row 0 ("Opened") is not a step.</summary>
    bool Undoable(int i) => i >= 1 && i < _history.Count && i > _barrier && (!_history[i].Saved || (!_unreliable && i <= _rebuilt)) && _history[i].Kind != HistoryKind.Opened
                            && !(_history[i].Kind == HistoryKind.Version && _history[i].Label.StartsWith("Imported", StringComparison.Ordinal));

    /// <summary>Whether a jump to row <paramref name="position"/> is possible now (going back undoes every step on the way).</summary>
    public bool CanGoTo(int position)
    {
        if (position < 0 || position >= _history.Count) return false;
        for (int i = position + 1; i <= _pos; i++) if (!Undoable(i)) return false;
        return true;
    }

    /// <summary>The history list for a version: "Opened" (the version's start), its saved depth steps (rebuilt in the background), or, right
    /// after an import, one step "Imported from Lumen" that cannot be undone.</summary>
    void BuildHistory(FakeDepthState depthState, bool imported)
    {
        _rebuildCts?.Cancel();
        _history.Clear();
        _barrier = 0;
        _cleared = false;
        var ops = depthState.CurrentOps;
        var v = depthState.CurrentVersion;
        var baseOps = v.Kind == VersionKind.Lumen ? ops : ImmutableList<FakeDepthOp>.Empty;
        _current = _current with { Depth = depthState };
        if (imported)
        {
            _history.Add(("Opened", HistoryKind.Opened, false, _current with { Depth = depthState.WithOps(ImmutableList<FakeDepthOp>.Empty) }));
            _history.Add(("Imported from Lumen", HistoryKind.Version, false, _current));
            _saved = 0; _rebuilt = 0;
            _pos = 1;
            return;
        }
        _history.Add(("Opened", HistoryKind.Opened, false, _current with { Depth = depthState.WithOps(baseOps) }));
        _saved = v.Kind == VersionKind.Lumen ? 0 : ops.Count;
        _rebuilt = 0;
        for (int i = 0; i < _saved; i++)
            _history.Add((ops[i].Label, HistoryKind.DepthEdit, true, _current with { Depth = depthState.WithOps(ops.GetRange(0, i + 1)) }));
        _pos = _history.Count - 1;
        _unreliable &= _saved > 0;
        if (_saved > 0 && !_unreliable) StartRebuild();
    }

    /// <summary>A version switch, a clean version, an import: the history starts again with that version's steps (the contract's rule).</summary>
    internal void RestartHistory(FakeDepthState depthState, bool imported = false)
    {
        _unreliable = false;
        BuildHistory(depthState, imported);
        _depth.Refresh(depthState, drag: false);
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>The depth state changes without a step (the default focus becomes known when an analysis finishes).</summary>
    internal void ReplaceDepthQuietly(FakeDepthState depthState)
    {
        for (int i = 0; i < _history.Count; i++)
            if (ReferenceEquals(_history[i].State.Depth, _current.Depth) || _history[i].State.Depth?.FocusDefault == true)
                _history[i] = (_history[i].Label, _history[i].Kind, _history[i].Saved, _history[i].State with { Depth = depthState with { Ops = _history[i].State.Depth!.Ops, Version = _history[i].State.Depth!.Version, Versions = _history[i].State.Depth!.Versions } });
        _current = _current with { Depth = depthState };
    }

    internal FakeDepthState DepthState => _current.Depth!;

    internal HistoryStatus DepthHistoryStatus => _unreliable
        ? new HistoryStatus(HistoryState.Unreliable, 0, _saved, double.NaN, "The saved depth edits of this photo no longer rebuild exactly, so they can't be undone one by one.")
        : _saved > 0 && _rebuilt < _saved ? new HistoryStatus(HistoryState.Rebuilding, _rebuilt, _saved, _rebuilt / (double)_saved) : HistoryStatus.Ready;

    /// <summary>A depth step (the effect, a tool, a reset — with <paramref name="barrier"/> the steps before it can no longer be undone).</summary>
    internal void PushDepth(string label, HistoryKind kind, FakeDepthState depth, bool barrier = false)
    {
        Push(label, _current with { Depth = depth }, kind);
        if (barrier) { _barrier = _pos; Changed?.Invoke(); }
    }

    /// <summary>Replays the saved steps one by one (a step every 400 ms × the tool scale), so earlier steps become available.</summary>
    void StartRebuild()
    {
        var cts = _rebuildCts = new CancellationTokenSource();
        int stepMs = (int)Math.Max(1, (_svc.Options.RebuildStepMs ?? 400) * _svc.Options.Timings.ToolScale);
        _ = Task.Run(async () =>
        {
            try
            {
                while (_rebuilt < _saved && !cts.IsCancellationRequested)
                {
                    await Task.Delay(stepMs, cts.Token).ConfigureAwait(false);
                    if (_rebuildHeld) continue;
                    Interlocked.Increment(ref _rebuilt);
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Tests and screenshots: hold the rebuild at <paramref name="done"/> steps (null releases it).</summary>
    public void HoldRebuild(int? done)
    {
        _rebuildHeld = done != null;
        if (done is int d) _rebuilt = Math.Min(d, _saved);
        else if (_rebuilt < _saved && !_unreliable) StartRebuild();
        Changed?.Invoke();
    }

    /// <summary>Tests and screenshots: the saved steps cannot be rebuilt.</summary>
    public void MarkHistoryUnreliable()
    {
        _rebuildCts?.Cancel();
        _unreliable = _saved > 0;
        Changed?.Invoke();
    }


    public SaveInfo Save => _save;
    public HistogramData? Histogram => _tiles.Histogram;
    public bool IsOriginal => SameAs(_current, _original);
    public bool IsCropping => _cropping;
    public NormRect CropFrame => _cropFrame;
    public CropAspect CropAspect => _cropping ? _cropAspect : _current.Geometry.Aspect;
    public int DragCalls { get; private set; }
    public event Action? Changed;

    /// <summary>The display rotation applies while the photo has no saved turn or flip (the library's rule).</summary>
    Turn DisplayTurn => _current.Geometry.HasTurnOrFlip || _current.TurnSaved ? Turn.Identity : Turn.FromDegrees(HeldDisplay);
    int HeldDisplay => _svc.Library.FakeSettings.ShowPhotosAsHeld ? Turn.FromLibrary(Item.HeldOrientation)?.Degrees ?? 0 : 0;
    public Turn PictureTurn => _current.Geometry.Turn.Then(DisplayTurn);
    void PushGeometry(Turn? fromPrevious = null) => _tiles.SetGeometry(PictureTurn, _current.Geometry.Crop, _cropping, fromPrevious);

    public PictureStatus Status => new(_cap is { } c && c < Item.BestQuality ? c : Item.BestQuality, false, double.NaN);

    public IReadOnlyList<(string Label, string Value)> Info
    {
        get
        {
            var it = Item;
            var size = _tiles.LevelSize(0);
            var rows = new List<(string, string)>
            {
                ("Captured", Text.LongDateTime(it.CaptureTime)),
                ("Size", $"{size.Width} × {size.Height}"),
                ("Lens", Text.Lens(it.FocalLengthMm) + (it.IsTelephoto ? " · telephoto" : "")),
            };
            if (it.ExposureTimeSeconds is double t && t > 0) rows.Add(("Exposure", t >= 0.3 ? $"{t:0.#} s" : $"1/{Math.Round(1 / t)} s"));
            if (it.Iso is int iso) rows.Add(("ISO", iso.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            rows.Add(("Frames", it.StackFrames > 1 ? $"{it.StackFrames} per module (low-light stack)" : "1"));
            rows.Add(("Modules", $"{(it.ModuleCount > 0 ? it.ModuleCount : 10)} · reference {(it.ReferenceModule.Length > 0 ? it.ReferenceModule : "A1")}"));
            rows.Add(("White balance", $"{Specs.First(s => s.Key == AdjustmentKeys.Temperature).Default:0} K as shot"));
            return rows;
        }
    }

    static bool SameAs(State a, State b) =>
        a.Geometry == b.Geometry && a.Values.Count == b.Values.Count && a.Values.All(kv => b.Values.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 1e-9);

    AdjustmentSpec Spec(string key) => Specs.First(s => s.Key == key);
    string Label(string key, double v) => $"{Spec(key).Name} {Spec(key).Format(v)}";

    // ---- tone and colour
    double _dragStart;

    public void BeginDrag(string key)
    {
        _dragStart = _current.Values.TryGetValue(key, out var v) ? v : Spec(key).Default;
        _drag = new Dictionary<string, double>(_current.Values);
    }

    public void DragTo(string key, double value)
    {
        DragCalls++;
        _drag ??= new Dictionary<string, double>(_current.Values);
        _drag[key] = Spec(key).Clamp(value);
        _tiles.SetAdjustments(new Dictionary<string, double>(_drag), drag: true);
        Changed?.Invoke();
    }

    public void EndDrag(string key)
    {
        if (_drag == null) return;
        double v = _drag.TryGetValue(key, out var x) ? x : _dragStart;
        _drag = null;
        if (Math.Abs(v - _dragStart) < 1e-12) { _tiles.SetAdjustments(_current.Values, drag: false); Changed?.Invoke(); return; }
        Push(Label(key, v), _current with { Values = new Dictionary<string, double>(_current.Values) { [key] = v } });
    }

    public void SetValue(string key, double value)
    {
        value = Spec(key).Clamp(value);
        if (_current.Values.TryGetValue(key, out var old) && Math.Abs(old - value) < 1e-12) return;
        Push(Label(key, value), _current with { Values = new Dictionary<string, double>(_current.Values) { [key] = value } });
    }

    public void Reset(string key)
    {
        var d = Spec(key).Default;
        if (_current.Values.TryGetValue(key, out var old) && Math.Abs(old - d) < 1e-12) return;
        Push($"Reset {Spec(key).Name.ToLowerInvariant()}", _current with { Values = new Dictionary<string, double>(_current.Values) { [key] = d } });
    }

    public void ResetToneAndColour() => Push("Reset tone and colour", _current with { Values = FakeDevelopService.Defaults(_item) });

    public void RevertToOriginal()
    {
        _cropping = false;
        // Lumen's revert also turns the depth effect off; the depth edits stay (the contract's rule)
        Push("Revert to original", _original with { Depth = _current.Depth! with { FNumber = null } }, HistoryKind.Mixed);
    }

    public bool Apply(EditSet set, string label)
    {
        var v = new Dictionary<string, double>(_current.Values);
        foreach (var (k, x) in set.Values) if (Specs.Any(s => s.Key == k)) v[k] = Spec(k).Clamp(x);
        var g = _current.Geometry;
        if ((set.Groups & EditGroups.RotationAndMirror) != 0) g = g with { TurnDegrees = set.Geometry.TurnDegrees, Mirrored = set.Geometry.Mirrored };
        bool cropOk = true;
        if ((set.Groups & EditGroups.Crop) != 0) { if (!_item.IsTelephoto) g = g with { Crop = set.Geometry.Crop, Aspect = set.Geometry.Aspect }; else cropOk = false; }
        var depth = DepthState;
        if ((set.Groups & EditGroups.DepthEffect) != 0 && _depth.Status.IsReady)
            depth = depth with
            {
                FNumber = set.DepthFNumber, FocusMm = set.DepthFocusMm ?? _depth.EffectRange.DefaultFocusMm, FocusDefault = set.DepthFocusMm == null, FocusCapture = null,
            };
        Push(label, new State(v, g, _current.TurnSaved || (set.Groups & EditGroups.RotationAndMirror) != 0, depth), (set.Groups & ~EditGroups.DepthEffect) == 0 ? HistoryKind.DepthEffect : HistoryKind.Mixed);
        return cropOk;
    }

    public EditSet Snapshot() => new(EditGroups.All, FakeDevelopService.WithoutAsShot(_current.Values, _item), _current.Geometry)
    {
        DepthFNumber = _depth.FNumber, DepthFocusMm = DepthState.FocusDefault ? null : DepthState.FocusMm,
    };

    // ---- geometry
    public bool CanRotateAsHeld => Turn.FromLibrary(Item.HeldOrientation) is { Quarter: > 0 } && !_current.Geometry.HasTurnOrFlip && !_current.TurnSaved;
    public bool GeometryDecided => _current.TurnSaved;
    public bool ShownAsHeld => !DisplayTurn.Equals(Turn.Identity);

    /// <summary>The first geometry edit of a photo shown as held starts from the held turn (baked into the saved transform),
    /// so the edit acts on the picture as shown; the crop is already in the picture as shown and keeps its place.</summary>
    GeometryState Baked => !_current.TurnSaved && !_current.Geometry.HasTurnOrFlip && HeldDisplay % 360 != 0
        ? _current.Geometry with { TurnDegrees = HeldDisplay }
        : _current.Geometry;

    /// <summary>One step named for the action; the orientation is decided from now on.</summary>
    void Geom(string label, Func<GeometryState, GeometryState> change) => Push(label, new State(_current.Values, change(Baked), TurnSaved: true));

    // the picture is the capture turned, then mirrored (M^m·R(t)); every operation acts on the picture as shown and, as in Lumen
    // (GeometryEdits keeps its frame in the capture's coordinates), the crop stays on the same part of the photo. Turning a mirrored
    // picture right is R(1)·M·R(t) = M·R(t − 1).
    public void RotateLeft() => Geom("Rotate left", g => g with
    {
        TurnDegrees = (g.TurnDegrees + (g.Mirrored ? 90 : 270)) % 360, Crop = g.Crop.RotateLeft(), Aspect = AspectPreset.Of(g.Aspect).Swapped().Aspect,
    });
    public void RotateRight() => Geom("Rotate right", g => g with
    {
        TurnDegrees = (g.TurnDegrees + (g.Mirrored ? 270 : 90)) % 360, Crop = g.Crop.RotateRight(), Aspect = AspectPreset.Of(g.Aspect).Swapped().Aspect,
    });
    // left ↔ right: H·M^m·R(t) toggles the mirror; top ↔ bottom: V = R(2)·M, so the mirror toggles and the turn gains a half turn
    public void FlipHorizontal() => Geom("Flip horizontal", g => g with { Mirrored = !g.Mirrored, Crop = g.Crop.FlipHorizontal() });
    public void FlipVertical() => Geom("Flip vertical", g => g with { Mirrored = !g.Mirrored, TurnDegrees = (g.TurnDegrees + 180) % 360, Crop = g.Crop.FlipVertical() });

    public void RotateAsHeld()
    {
        if (!CanRotateAsHeld) return;
        int held = Turn.FromLibrary(Item.HeldOrientation)!.Value.Degrees;
        Geom("Rotate as the camera was held", g => g with { TurnDegrees = held });
    }

    /// <summary>Crop, rotation and flips back to none: the orientation is undecided again (a photo shown as held is shown so again).</summary>
    public void ResetGeometry() { _cropping = false; Push("Reset crop and rotation", new State(_current.Values, GeometryState.Identity, TurnSaved: false)); }

    Size FrameSize => PictureTurn.Apply(new Size(_tiles.Capture.Width, _tiles.Capture.Height));

    public void EnterCrop()
    {
        if (_cropping) return;
        _cropping = true;
        _cropFrame = _current.Geometry.Crop;
        _cropAspect = _current.Geometry.Aspect;
        PushGeometry();
        Changed?.Invoke();
    }

    /// <summary>Lumen's reshaping: the same area, centred, kept inside.</summary>
    public void SetCropAspect(CropAspect aspect)
    {
        _cropAspect = aspect;
        double ratio = AspectPreset.Of(aspect).Ratio;
        if (ratio > 0)
        {
            var f = FrameSize;
            var px = _cropFrame.ToPixels(f);
            double area = px.Width * px.Height, w = Math.Sqrt(area * ratio), h = w / ratio;
            if (w > f.Width) { w = f.Width; h = w / ratio; }
            if (h > f.Height) { h = f.Height; w = h * ratio; }
            _cropFrame = NormRects.FromPixels(CropMath.ClampInto(new Rect(px.Center.X - w / 2, px.Center.Y - h / 2, w, h), f), f);
        }
        Changed?.Invoke();
    }

    public void SetCropFrame(NormRect frame, bool dragging)
    {
        _cropFrame = frame.Clamp();
        Changed?.Invoke();
    }

    public NormRect FitCropFrame(NormRect frame)
    {
        double ratio = AspectPreset.Of(_cropAspect).Ratio;
        if (ratio <= 0) return frame;
        var f = FrameSize;
        return NormRects.FromPixels(CropMath.Fit(frame.ToPixels(f), ratio), f);
    }

    public void ApplyCrop()
    {
        if (!_cropping) return;
        _cropping = false;
        if (_cropFrame.Near(_current.Geometry.Crop) && _cropAspect == _current.Geometry.Aspect) { PushGeometry(); Changed?.Invoke(); return; }
        var frame = _cropFrame; var aspect = _cropAspect;
        Geom(frame.IsFull() ? "Remove crop" : "Crop", g => g with { Crop = frame, Aspect = aspect });
    }

    public void CancelCrop()
    {
        if (!_cropping) return;
        _cropping = false;
        PushGeometry();
        Changed?.Invoke();
    }

    // ---- history
    void Push(string label, State s, HistoryKind kind = HistoryKind.Tone)
    {
        _drag = null;
        if (_pos < _history.Count - 1) _history.RemoveRange(_pos + 1, _history.Count - _pos - 1);
        if (kind == HistoryKind.DepthEdit && _history.Count(h => h.Kind == HistoryKind.DepthEdit) >= 20)
        {
            // Lumen keeps 20 undoable depth edits: the oldest one stays done but cannot be undone any more
            int oldest = Enumerable.Range(1, _history.Count - 1).First(i => _history[i].Kind == HistoryKind.DepthEdit && i > _barrier);
            _barrier = Math.Max(_barrier, oldest);
        }
        if (kind == HistoryKind.Tone && s.Geometry != _current.Geometry) kind = HistoryKind.Geometry;
        if (kind == HistoryKind.Tone && label.StartsWith("Revert", StringComparison.Ordinal)) kind = HistoryKind.Mixed;
        _history.Add((label, kind, false, s));
        _pos = _history.Count - 1;
        Show(s);
    }

    void Show(State s)
    {
        _drag = null;
        bool pixels = !ReferenceEquals(s.Values, _current.Values) || _tiles.IsDragging;
        bool geometry = s.Geometry != _current.Geometry || s.TurnSaved != _current.TurnSaved;
        var before = PictureTurn;
        bool sameFraming = !s.Geometry.HasCrop && !_current.Geometry.HasCrop;
        if (s.Depth == null) s = s with { Depth = _current.Depth };
        bool depth = !ReferenceEquals(s.Depth, _current.Depth);
        _current = s;
        if (geometry) PushGeometry(sameFraming ? before.Inverse().Then(PictureTurn) : null);
        else if (pixels) _tiles.SetAdjustments(s.Values, drag: false);
        if (geometry && pixels) _tiles.SetAdjustments(s.Values, drag: false);
        if (depth || geometry) _depth.Refresh(s.Depth!, drag: false);
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Undo() { if (CanUndo) { _cropping = false; _pos--; Show(_history[_pos].State); } }
    public void Redo() { if (_pos < _history.Count - 1) { _cropping = false; _pos++; Show(_history[_pos].State); } }
    public void GoTo(int position) { if (position != _pos && CanGoTo(position)) { _cropping = false; _pos = position; Show(_history[_pos].State); } }

    // ---- saving
    void ScheduleSave()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _save = new SaveInfo(SaveStatus.Saving, WhereOf(_item));
        var item = _item;
        var values = new Dictionary<string, double>(_current.Values);
        var geometry = _current.Geometry;
        var depth = _current.Depth;
        _ = Task.Delay(_svc.Options.SaveDelayMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _svc.Store(item, values, geometry);
            StoreDepth(item, depth);
            _save = new SaveInfo(SaveStatus.Saved, WhereOf(item));
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    public Task SaveNowAsync()
    {
        if (_save.Status != SaveStatus.Saving) return Task.CompletedTask;
        _saveCts?.Cancel();
        _svc.Store(_item, new Dictionary<string, double>(_current.Values), _current.Geometry);
        StoreDepth(_item, _current.Depth);
        _save = new SaveInfo(SaveStatus.Saved, WhereOf(_item));
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>The depth part is saved too: kept for the next open, mirrored into the library's summary (saved depth effect, version).</summary>
    void StoreDepth(CaptureItem item, FakeDepthState? d)
    {
        if (d == null) return;
        _svc.DepthRecipes[item.Id] = d;
        var v = d.CurrentVersion;
        var source = v.Kind switch { VersionKind.Lumen => EditSource.Lumen, VersionKind.Lux when !d.CurrentOps.IsEmpty => EditSource.Lux, _ => EditSource.None };
        float? f = d.FNumber is double fn && fn < (item.IsTelephoto ? 8.0 : 15.2) ? (float)fn : null;
        _svc.Library.SetDepthForTesting(item.Id, f, d.FocusDefault ? null : (float)d.FocusMm, source == EditSource.None ? null : v.Id, d.CurrentOps.Count, source, d.Versions.Count);
    }

    public void SetQualityCap(ViewQuality? cap)
    {
        _cap = cap;
        _tiles.SetFinestLevel(FinestFor(Item.BestQuality));
    }

    public ExportOptions ExportSettings(ExportOptions baseOptions) =>
        baseOptions with { Adjustments = AdjustmentMapping.ToEngine(_current.Values, Specs), UseSavedEdits = true };

    public void Dispose()
    {
        _svc.Library.CapturesChanged -= OnLibraryChanged;
        if (_svc.OpenSession == this) _svc.OpenSession = null;
        _rebuildCts?.Cancel();
        _ = SaveNowAsync();
        _depth.Dispose();
        _tiles.Dispose();
    }
}
