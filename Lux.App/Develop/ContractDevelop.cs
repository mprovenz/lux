using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.Library;
using AvPixelSize = Avalonia.PixelSize;
using LibViewportRequest = Lux.Library.ViewportRequest;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Develop;

/// <summary>
/// The Develop page over the library's edit contract (<c>EditContracts.cs</c>): <see cref="ILibrary.OpenPhotoAsync"/> sessions, the
/// <see cref="ILibrary.Edits"/> clipboard and presets. Unavailable (with the reason) when the library does not implement them.
/// </summary>
public sealed class ContractDevelopService : IDevelopService
{
    readonly ILibrary _lib;
    readonly IEditClipboard? _edits;
    string? _clipboardName;

    ContractDevelopService(ILibrary lib, IEditClipboard? edits)
    {
        _lib = lib;
        _edits = edits;
        if (edits != null) edits.PresetsChanged += (_, _) => PresetsChanged?.Invoke();
    }

    /// <summary>The adapter, or the "not available" service when the library has no edit support.</summary>
    public static IDevelopService Create(ILibrary lib)
    {
        IEditClipboard? edits;
        try { edits = lib.Edits; }
        catch (NotSupportedException) { return new UnavailableDevelopService("Editing is not available with this library yet."); }
        return new ContractDevelopService(lib, edits);
    }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<AdjustmentSpec> DefaultSpecs => AdjustmentSpec.Standard();
    /// <summary>The clipboard pastes single sliders (its per-slider overload is the library's own).</summary>
    public bool SupportsPerSlider => _edits != null && Capability.Implements(_edits, typeof(IEditClipboard), "PasteAsync", 4);
    public event Action? PresetsChanged;
    public event Action? ClipboardChanged;
    /// <summary>The open session (a paste to its photo goes through it, so it becomes a history step there).</summary>
    internal ContractDevelopSession? OpenSession { get; set; }

    public async Task<IDevelopSession> OpenAsync(CaptureItem item, CancellationToken cancel)
    {
        IPhotoSession ps;
        try { ps = await _lib.OpenPhotoAsync(item.Id, new PhotoOpenOptions { PixelOrder = PixelOrder.Bgra, TileSize = 512 }, cancel).ConfigureAwait(false); }
        catch (NotSupportedException) { throw new LibraryException("Editing is not available with this library yet."); }
        var s = new ContractDevelopSession(this, ps, _lib);
        OpenSession = s;
        return s;
    }

    public EditSet? Clipboard => _edits?.Clipboard is { } v ? Convert.ToSet(v, null) : null;
    public string? ClipboardSource => _clipboardName ?? (_edits?.ClipboardSource is { } id ? _lib.Get(id)?.Name : null);

    public async Task<EditSet> CopyAsync(string captureId, string name, EditGroups groups, CancellationToken cancel)
    {
        var v = await Edits.CopyAsync(captureId, groups, cancel).ConfigureAwait(false);
        _clipboardName = name;
        ClipboardChanged?.Invoke();
        return Convert.ToSet(v, null);
    }

    public void SetClipboard(EditSet set, string sourceName, string? sourceCaptureId = null)
    {
        Edits.SetClipboard(Convert.ToValues(set), sourceCaptureId);
        _clipboardName = sourceName;
        ClipboardChanged?.Invoke();
    }

    IEditClipboard Edits => _edits ?? throw new LibraryException("Copy and paste are not available with this library yet.");

    /// <summary>Paste to captures; the photo open in Develop takes it through its session (a history step there, so the page's history
    /// list stays in step with the library's undo stack).</summary>
    public async Task<PasteResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel)
    {
        var open = OpenSession;
        var rest = open == null ? captureIds : captureIds.Where(id => id != open.Native.CaptureId).ToList();
        int extra = 0, skipped = 0;
        if (open != null && rest.Count != captureIds.Count && Clipboard is { } clip)
        {
            bool ok = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => open.Apply(clip.Only(groups), "Paste adjustments"));
            extra = 1;
            if (!ok) skipped = 1;
        }
        if (rest.Count == 0) return new PasteResult(extra, skipped, Array.Empty<string>(), 0);
        var sliders = SupportsPerSlider ? Clipboard?.Sliders?.Select(AdjustmentKeys.ToContract).ToHashSet() : null;
        var r = Convert.ToResult(await (sliders == null ? Edits.PasteAsync(rest, groups, cancel) : Edits.PasteAsync(rest, groups, sliders, cancel)).ConfigureAwait(false), _lib);
        return r with { Written = r.Written + extra, CropSkipped = r.CropSkipped + skipped };
    }

    public Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel) => Edits.UndoBatchAsync(batchId, cancel);

    public IReadOnlyList<PresetInfo> Presets =>
        _edits == null ? Array.Empty<PresetInfo>() : _edits.GetPresets().Select(p => new PresetInfo(p.Id, p.Name, p.Groups, Convert.ToSet(p.Values, null) with { Groups = p.Groups })).ToList();

    public async Task<PresetInfo> CreatePresetAsync(string name, EditSet set, EditGroups groups, CancellationToken cancel)
    {
        var p = await Edits.CreatePresetAsync(name, Convert.ToValues(set), groups, cancel).ConfigureAwait(false);
        return new PresetInfo(p.Id, p.Name, p.Groups, Convert.ToSet(p.Values, null) with { Groups = p.Groups });
    }

    public Task DeletePresetAsync(long presetId, CancellationToken cancel) => Edits.DeletePresetAsync(presetId, cancel);

    public async Task<PresetInfo> RenamePresetAsync(long presetId, string newName, CancellationToken cancel)
    {
        var p = await Edits.RenamePresetAsync(presetId, newName, cancel).ConfigureAwait(false);
        return new PresetInfo(p.Id, p.Name, p.Groups, Convert.ToSet(p.Values, null) with { Groups = p.Groups });
    }

    /// <summary>Apply a preset to captures (one batch); the photo open in Develop takes it through its session (a history step there).</summary>
    public async Task<PasteResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel)
    {
        var open = OpenSession;
        var rest = open == null ? captureIds : captureIds.Where(id => id != open.Native.CaptureId).ToList();
        int extra = 0, skipped = 0;
        if (open != null && rest.Count != captureIds.Count && Presets.FirstOrDefault(p => p.Id == presetId) is { } preset)
        {
            bool ok = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => open.Apply(preset.Set, $"Preset “{preset.Name}”"));
            extra = 1;
            if (!ok) skipped = 1;
        }
        if (rest.Count == 0) return new PasteResult(extra, skipped, Array.Empty<string>(), 0);
        var r = Convert.ToResult(await Edits.ApplyPresetAsync(presetId, rest, cancel).ConfigureAwait(false), _lib);
        return r with { Written = r.Written + extra, CropSkipped = r.CropSkipped + skipped };
    }
}

/// <summary>Which optional contract members the library implements (its class, not the interface's default): v2.1's additions.</summary>
internal static class Capability
{
    /// <summary>The overload of <paramref name="member"/> with <paramref name="parameters"/> parameters is the library's own.</summary>
    public static bool Implements(object instance, Type iface, string member, int parameters)
    {
        try
        {
            var map = instance.GetType().GetInterfaceMap(iface);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == member && map.InterfaceMethods[i].GetParameters().Length == parameters)
                    return map.TargetMethods[i].DeclaringType != iface;
        }
        catch (ArgumentException) { }
        return false;
    }

    public static bool Implements(object instance, Type iface, string member)
    {
        try
        {
            var map = instance.GetType().GetInterfaceMap(iface);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == member) return map.TargetMethods[i].DeclaringType != iface;
        }
        catch (ArgumentException) { }
        return false;
    }
}

/// <summary>Conversions between the library's edit values and the page's.</summary>
internal static class Convert
{
    public static EditSet ToSet(EditValues v, IPhotoEdit? edit)
    {
        var a = v.Adjustments;
        var d = new Dictionary<string, double>
        {
            [AdjustmentKeys.Exposure] = a.Exposure, [AdjustmentKeys.Contrast] = a.Contrast, [AdjustmentKeys.Vibrance] = a.Vibrance,
            [AdjustmentKeys.Saturation] = a.Saturation, [AdjustmentKeys.Sharpening] = a.Sharpening, [AdjustmentKeys.Shadows] = a.Shadows,
            [AdjustmentKeys.Highlights] = a.Highlights, [AdjustmentKeys.Clarity] = a.Clarity,
        };
        // temperature / tint "as shot" (null) stay absent: the target photo keeps its own white balance
        if (a.Temperature is float t) d[AdjustmentKeys.Temperature] = t;
        else if (edit != null) d[AdjustmentKeys.Temperature] = edit.GetValue(AdjustmentKey.Temperature);
        if (a.Tint is float u) d[AdjustmentKeys.Tint] = u;
        else if (edit != null) d[AdjustmentKeys.Tint] = edit.GetValue(AdjustmentKey.Tint);
        var tr = v.Transform;
        var set = new EditSet(v.Groups, d, new GeometryState(tr.TurnDegrees, tr.Mirrored, tr.Crop), v) { DepthFNumber = v.FNumber, DepthFocusMm = v.FocusMm };
        return v.Sliders == null ? set : set.OnlySliders(v.Sliders.Select(AdjustmentKeys.FromContract).ToHashSet());
    }

    public static EditValues ToValues(EditSet s)
    {
        var sliders = s.Sliders?.Select(AdjustmentKeys.ToContract).ToHashSet();
        if (s.Native is EditValues native) return native with { Groups = s.Groups, Sliders = sliders };
        float V(string k, float dflt) => s.Values.TryGetValue(k, out var x) ? (float)x : dflt;
        return new EditValues
        {
            Groups = s.Groups,
            Adjustments = new Lux.Engine.Pipeline.Adjustments
            {
                Exposure = V(AdjustmentKeys.Exposure, 0), Contrast = V(AdjustmentKeys.Contrast, 0), Vibrance = V(AdjustmentKeys.Vibrance, 0),
                Saturation = V(AdjustmentKeys.Saturation, 0), Sharpening = V(AdjustmentKeys.Sharpening, Lux.Engine.Pipeline.Adjustments.GuiSharpening),
                Shadows = V(AdjustmentKeys.Shadows, 0), Highlights = V(AdjustmentKeys.Highlights, 0), Clarity = V(AdjustmentKeys.Clarity, 0),
                Temperature = s.Values.TryGetValue(AdjustmentKeys.Temperature, out var t) ? (float)t : null,
                Tint = s.Values.TryGetValue(AdjustmentKeys.Tint, out var u) ? (float)u : null,
            },
            Transform = new PhotoTransform { TurnDegrees = s.Geometry.TurnDegrees, Mirrored = s.Geometry.Mirrored, Crop = s.Geometry.Crop, IsIdentity = s.Geometry.IsIdentity },
            Sliders = sliders,
            FNumber = s.DepthFNumber is double f ? (float)f : null,
            FocusMm = s.DepthFocusMm is double mm ? (float)mm : null,
        };
    }

    public static PasteResult ToResult(EditBatchResult r, ILibrary lib) =>
        new(r.Written, r.CropSkipped, r.Failed.Select(f => $"{lib.Get(f.CaptureId)?.Name ?? f.CaptureId}: {f.Reason}").ToList(), r.BatchId);

    /// <summary>The page's specs from the library's slider descriptions (names shortened; temperature on a log scale).</summary>
    public static IReadOnlyList<AdjustmentSpec> Specs(IPhotoEdit edit)
    {
        var order = AdjustmentKeys.All;
        var list = new List<AdjustmentSpec>();
        foreach (var a in edit.Adjustments)
        {
            string key = AdjustmentKeys.FromContract(a.Key);
            string name = a.Key switch { AdjustmentKey.Temperature => "Temp", AdjustmentKey.Sharpening => "Sharpen", _ => a.Name };
            bool temp = a.Key == AdjustmentKey.Temperature;
            string tip = (a.LuxOnly ? $"{a.Name} (a Lux control; Lumen never offered it)" : a.Name) + (a.Note.Length > 0 ? $" · {a.Note}" : "");
            list.Add(new AdjustmentSpec(key, name, a.Min, a.Max, a.Default, a.Step, a.Decimals, temp ? " K" : "", Signed: !temp,
                temp ? SliderScale.Log : SliderScale.Linear, tip));
        }
        return list.OrderBy(s => order.ToList().IndexOf(s.Key) is var i && i < 0 ? 99 : i).ToList();
    }
}

/// <summary>The viewer's tile source over <see cref="IPhotoView"/>: requests mapped to the library's viewport requests, tiles made into
/// bitmaps on the library's thread, tiles of an older geometry dropped.</summary>
public sealed class ContractTileSource : ITileSource
{
    readonly IPhotoView _view;
    readonly object _gate = new();
    readonly bool _depth;
    PictureGeometry _geometry;
    ViewQuality? _cap;

    /// <param name="depth">The depth view of "Photo + depth" (<see cref="ViewMode.Depth"/>): same geometry and grid, one channel.</param>
    public ContractTileSource(IPhotoView view, bool depth = false)
    {
        _view = view;
        _depth = depth;
        _geometry = view.Geometry;
        view.GeometryChanged += (_, g) => { lock (_gate) _geometry = g; Changed?.Invoke(); };
        view.VersionChanged += (_, _) => Changed?.Invoke();
        view.StatusChanged += (_, _) => Changed?.Invoke();
        view.TileReady += OnTile;
    }

    PictureGeometry G { get { lock (_gate) return _geometry; } }
    public int LevelCount => G.LevelCount;
    public AvPixelSize LevelSize(int level) { var s = G.LevelSizes[level]; return new AvPixelSize(s.Width, s.Height); }
    public int TileSize => G.TileSize;
    public long GeometryVersion => G.Version;
    /// <summary>The library says the new picture is the previous one turned: the viewer holds the old one turned.</summary>
    public Turn? TurnFromPrevious => G.TurnFromPrevious is int deg ? Turn.FromDegrees(deg) : null;
    public int FinestLevel => _view.Status.FinestLevel;
    public bool HasBefore => !_depth;
    ViewMode ModeOf(int channel) => _depth ? ViewMode.Depth : channel == Channels.Before ? ViewMode.Original : ViewMode.Edited;
    public long Version(int channel) => _view.Version(ModeOf(channel));
    public long Floor(int channel) => _view.Floor(ModeOf(channel));
    public event Action<TileImage>? TileReady;
    public event Action? Changed;
    public int TilesReceived;

    public void SetQualityCap(ViewQuality? cap) { _cap = cap; Changed?.Invoke(); }

    /// <summary>Tiles held outside the changed regions are valid at the new version.</summary>
    public event Action<TileRevalidation>? Revalidated;

    /// <summary>A depth change: the photo's (or, for the depth view's source, the depth view's) changed regions per level.</summary>
    internal void DepthChanged(DepthChangedEventArgs e)
    {
        var g = G;
        if (_depth)
        {
            if (e.DepthRect is not { } dr || e.DepthVersion <= 0) return;
            Revalidated?.Invoke(new TileRevalidation(Channels.After, e.DepthVersion, g.Version, new[] { (-1, dr) }));
            return;
        }
        if (e.PhotoVersion <= 0) return;
        var regions = e.PhotoRegions is { } pr ? pr.Select(r => (r.Level, r.Rect)).ToArray()
            : e.PhotoRect is { } rect ? new[] { (-1, rect) } : Array.Empty<(int, NormRect)>();
        Revalidated?.Invoke(new TileRevalidation(Channels.After, e.PhotoVersion, g.Version, regions));
    }

    public void Request(TileRequest r)
    {
        var g = G;
        var full = g.FullSize;
        int coarsest = g.LevelCount - 1;
        // center of the view first (the viewer sorts its needs; the library is asked to keep the order)
        var centre = r.ImageRect.Center;
        var needed = r.Needed.Where(k => k.Level == r.Level)
            .OrderBy(k => { double tx = (k.Col + 0.5) * g.TileSize * full.Width / (double)g.LevelSizes[k.Level].Width - centre.X, ty = (k.Row + 0.5) * g.TileSize * full.Height / (double)g.LevelSizes[k.Level].Height - centre.Y; return tx * tx + ty * ty; })
            .Select(k => new TileId(k.Level, k.Col, k.Row)).ToList();
        bool hasCoarse = r.Level == coarsest || !r.Needed.Any(k => k.Level == coarsest);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _view.RequestViewport(new LibViewportRequest
        {
            Visible = new NormRect(r.ImageRect.X / full.Width, r.ImageRect.Y / full.Height, r.ImageRect.Width / full.Width, r.ImageRect.Height / full.Height),
            ScreenWidth = Math.Max(1, r.ScreenPixels.Width),
            ScreenHeight = Math.Max(1, r.ScreenPixels.Height),
            Level = r.Level,
            QualityCap = _cap,
            Mode = ModeOf(r.Channel),
            Interactive = r.Interactive,
            Needed = needed,
            HasCoarse = hasCoarse,
        });
        Interlocked.Add(ref RequestTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref RequestCount);
    }

    /// <summary>Time spent in the library's RequestViewport on the calling (UI) thread, all sources (measurements).</summary>
    public static long RequestTicks;
    public static int RequestCount;

    void OnTile(object? sender, PictureTile t)
    {
        if (t.GeometryVersion != G.Version) return;
        if ((t.Mode == ViewMode.Depth) != _depth) return;   // the photo's source and the depth view's share the library's tile event
        Interlocked.Increment(ref TilesReceived);
        var handle = GCHandle.Alloc(t.Pixels, GCHandleType.Pinned);
        Bitmap bmp;
        try
        {
            bmp = new Bitmap(t.Order == PixelOrder.Bgra ? PixelFormat.Bgra8888 : PixelFormat.Rgba8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new AvPixelSize(t.Rect.Width, t.Rect.Height), new Avalonia.Vector(96, 96), t.Rect.Width * 4);
        }
        finally { handle.Free(); }
        TileReady?.Invoke(new TileImage
        {
            Channel = !_depth && t.Mode == ViewMode.Original ? Channels.Before : Channels.After,
            Level = t.Id.Level, Col = t.Id.Col, Row = t.Id.Row, Version = t.Version, GeometryVersion = t.GeometryVersion, Bitmap = bmp,
            IsDragPreview = t.IsDragPreview,
        });
    }
}

/// <summary>One open photo over <see cref="IPhotoSession"/>, with the page's history list (labels) on top of the library's undo stack.</summary>
public sealed class ContractDevelopSession : IDevelopSession
{
    readonly ContractDevelopService _svc;
    readonly IPhotoSession _ps;
    readonly ILibrary _lib;
    readonly ContractTileSource _tiles;
    double _dragStart;

    readonly ContractDepthSession? _depth;
    readonly IPhotoHistory? _history;
    /// <summary>The view's status changed: the library coalesces its status events itself (at most every 50 ms, the latest always),
    /// so the page hears of each one directly.</summary>
    void OnViewStatus() => Changed?.Invoke();

    internal ContractDevelopSession(ContractDevelopService svc, IPhotoSession ps, ILibrary lib)
    {
        _svc = svc;
        _ps = ps;
        _lib = lib;
        _tiles = new ContractTileSource(ps.View);
        Specs = Convert.Specs(ps.Edit);
        ps.Edit.Changed += (_, _) => Changed?.Invoke();
        // the library coalesces every status change to at most one event per 50 ms, the last one always
        ps.View.StatusChanged += (_, _) => OnViewStatus();
        // The depth contract when the library implements it; its combined history drives the panel and undo / redo
        if (ps.Depth is not NoPhotoDepth && ps.DepthEdit is not NoPhotoDepth)
        {
            _depth = new ContractDepthSession(ps, lib);
            _depth.Changed += () => Changed?.Invoke();
            // Tiles held outside the changed regions stay on screen, valid at the new version
            ps.Depth.Changed += (_, e) => _tiles.DepthChanged(e);
            ps.DepthEdit.Changed += (_, e) => _tiles.DepthChanged(e);
        }
        if (ps.History is not NoPhotoDepth)
        {
            _history = ps.History;
            _historyInfo = _history.Info;
            // the event carries the list: the page never asks the library for it on the UI thread
            _history.Changed += (_, info) => { _historyInfo = info; Changed?.Invoke(); };
        }
    }

    public IDepthSession? Depth => _depth;
    volatile HistoryInfo? _historyInfo;

    HistoryInfo HInfo => _historyInfo ?? HistoryInfo.Empty;

    IPhotoEdit E => _ps.Edit;

    public IPhotoSession Native => _ps;
    public CaptureItem Item => _lib.Get(_ps.CaptureId) ?? _ps.Item;
    public ITileSource Tiles => _tiles;
    public IReadOnlyList<AdjustmentSpec> Specs { get; }
    public event Action? Changed;

    static AdjustmentKey K(string key) => AdjustmentKeys.ToContract(key);
    AdjustmentSpec Spec(string key) => Specs.First(s => s.Key == key);
    public double Get(string key) => E.GetValue(K(key));

    /// <summary>The library keeps the history: the page only says something changed.</summary>
    void Step(string label) => Changed?.Invoke();

    public void BeginDrag(string key) { _dragStart = Get(key); E.BeginDrag(K(key)); }
    public void DragTo(string key, double value) => E.DragTo(K(key), (float)value);

    public void EndDrag(string key)
    {
        double before = _dragStart;
        E.EndDrag(K(key));
        double after = Get(key);
        if (Math.Abs(after - before) > 1e-9) Step($"{Spec(key).Name} {Spec(key).Format(after)}");
    }

    public void SetValue(string key, double value)
    {
        double before = Get(key);
        E.SetValue(K(key), (float)value);
        if (Math.Abs(Get(key) - before) > 1e-9) Step($"{Spec(key).Name} {Spec(key).Format(Get(key))}");
    }

    public void Reset(string key)
    {
        double before = Get(key);
        E.Reset(K(key));
        if (Math.Abs(Get(key) - before) > 1e-9) Step($"Reset {Spec(key).Name.ToLowerInvariant()}");
    }

    public void ResetToneAndColour() { if (!IsToneDefault) { E.ResetGroups(EditGroups.ToneAndColour); Step("Reset tone and color"); } }
    bool IsToneDefault => Specs.All(s => Math.Abs(Get(s.Key) - s.Default) < s.Step / 2);
    public void RevertToOriginal() { E.RevertToOriginal(); Step("Revert to original"); }

    public bool Apply(EditSet set, string label)
    {
        bool ok = E.Apply(Convert.ToValues(set), set.Groups);
        Step(label);
        return ok;
    }

    public EditSet Snapshot() => Convert.ToSet(E.Current, E);
    public bool IsOriginal => IsToneDefault && E.Transform.IsIdentity && Specs.Where(s => s.Key is AdjustmentKeys.Temperature or AdjustmentKeys.Tint).All(s => E.IsAsShot(K(s.Key)));

    public GeometryState Geometry
    {
        get
        {
            var t = E.Transform;
            return new GeometryState(t.TurnDegrees, t.Mirrored, t.Crop, E.CropState == CropState.Hidden ? (t.Crop.IsFull() ? CropAspect.Free : E.CropAspect) : E.CropAspect) { LevelDegrees = t.LevelDegrees };
        }
    }

    public Turn PictureTurn
    {
        get
        {
            var t = E.Transform;
            return new GeometryState(t.TurnDegrees, t.Mirrored, t.Crop).Turn.Then(Turn.FromDegrees(_ps.View.Geometry.DisplayRotation));
        }
    }

    public bool GeometryDecided => E.GeometryDecided;
    public bool ShownAsHeld => _ps.View.Geometry.DisplayRotation % 360 != 0;

    public void RotateLeft() { E.RotateLeft(); Step("Rotate left"); }
    public void RotateRight() { E.RotateRight(); Step("Rotate right"); }
    public void FlipHorizontal() { E.FlipHorizontal(); Step("Flip horizontal"); }
    public void FlipVertical() { E.FlipVertical(); Step("Flip vertical"); }
    public bool CanRotateAsHeld => E.CanRotateAsHeld;
    public void RotateAsHeld() { E.RotateAsHeld(); Step("Rotate as the camera was held"); }
    public LevelSuggestion LevelSuggestion => E.LevelSuggestion;
    public void Level() { E.Level(); Step("Level"); }
    public void ResetGeometry() { if (!E.Transform.IsIdentity) { E.ResetGeometry(); Step("Reset crop and rotation"); } }
    public bool IsCropping => E.CropState != CropState.Hidden;
    public NormRect CropFrame => E.CropFrame;
    public CropAspect CropAspect => E.CropAspect;
    public void EnterCrop() => E.EnterCrop();
    public void SetCropAspect(CropAspect aspect) => E.SetCropAspect(aspect);
    public void SetCropFrame(NormRect frame, bool dragging) => E.SetCropFrame(frame, dragging);
    public NormRect FitCropFrame(NormRect frame) => E.FitCropFrame(frame);

    public void ApplyCrop()
    {
        var before = E.Transform.Crop;
        E.ApplyCrop();
        var after = E.Transform.Crop;
        if (!before.Near(after)) Step(after.IsFull() ? "Remove crop" : "Crop");
    }

    public void CancelCrop() => E.CancelCrop();

    /// <summary>"Opened" then the library's steps (done, then undone) — the combined list of the depth contract when there is one.</summary>
    public IReadOnlyList<string> History => _history != null
        ? new[] { "Opened" }.Concat(HInfo.Entries.Select(h => h.Label)).ToList()
        : new[] { "Opened" }.Concat(E.History.Select(h => h.Label)).ToList();

    public IReadOnlyList<HistoryItem> HistoryItems
    {
        get
        {
            if (_history == null) return History.Select((l, i) => new HistoryItem(l, i == 0 ? HistoryKind.Opened : HistoryKind.Tone)).ToList();
            var info = HInfo;
            var list = new List<HistoryItem> { new("Opened", HistoryKind.Opened) };
            foreach (var e in info.Entries)
                list.Add(new HistoryItem(e.Label, e.Kind switch
                {
                    HistoryEntryKind.Adjustment => HistoryKind.Tone,
                    HistoryEntryKind.Geometry => HistoryKind.Geometry,
                    HistoryEntryKind.DepthEffect => HistoryKind.DepthEffect,
                    HistoryEntryKind.DepthEdit => HistoryKind.DepthEdit,
                    HistoryEntryKind.DepthImport => HistoryKind.Version,
                    _ => HistoryKind.Mixed,
                }, Saved: e.FromEarlierSession, Undoable: e.Undoable));
            return list;
        }
    }


    public int HistoryPosition => _history != null ? HInfo.Position : E.HistoryPosition;

    // ---- the first row and "Clear history"
    bool Cleared => _history?.Cleared == true;
    public string FirstRowLabel => (Cleared ? "History cleared" : "Opened") + (FirstRowEdits() is null ? " · original" : Cleared ? " · with edits" : " · with saved edits");
    public string? FirstRowTip => FirstRowEdits() is { } e ? (Cleared ? "The photo when the history was cleared: " : "Saved edits it opened with: ") + e
                                                         : (Cleared ? "The photo when the history was cleared: no edits" : "The photo as captured: no saved edits");
    public bool CanClearHistory => (_history != null ? HInfo.Entries.Count : E.History.Count) > 0;
    public void ClearHistory()
    {
        if (_history != null) { _ = Track(_history.ClearHistory()); return; }
        Changed?.Invoke();
    }

    /// <summary>The edits of the list's starting state in words, or null when it is the photo as captured.</summary>
    string? FirstRowEdits()
    {
        var v = E.Opened; var d = Lux.Engine.Pipeline.Adjustments.Default; var a = v.Adjustments;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>();
        void Num(string name, float x, float x0, string fmt) { if (Math.Abs(x - x0) > 1e-6f) parts.Add($"{name} {(x > 0 ? "+" : "")}{x.ToString(fmt, inv)}"); }
        Num("exposure", a.Exposure, d.Exposure, "0.00"); Num("contrast", a.Contrast, d.Contrast, "0"); Num("highlights", a.Highlights, d.Highlights, "0");
        Num("shadows", a.Shadows, d.Shadows, "0"); Num("clarity", a.Clarity, d.Clarity, "0"); Num("vibrance", a.Vibrance, d.Vibrance, "0");
        Num("saturation", a.Saturation, d.Saturation, "0");
        if (Math.Abs(a.Sharpening - d.Sharpening) > 1e-6f) parts.Add($"sharpening {a.Sharpening.ToString("0", inv)}");
        if (a.Temperature is float t) parts.Add($"temperature {t.ToString("0", inv)} K");
        if (a.Tint is float u) parts.Add($"tint {u.ToString("0.#", inv)}");
        if (!v.Transform.IsIdentity)
        {
            if (v.Transform.TurnDegrees != 0) parts.Add($"turned {v.Transform.TurnDegrees}°");
            if (v.Transform.Mirrored) parts.Add("mirrored");
            if (v.Transform.Crop.Width < 0.999 || v.Transform.Crop.Height < 0.999) parts.Add("cropped");
        }
        if (v.FNumber is float f && f < E.Info.AllInFocusFNumber) parts.Add($"depth effect f/{f.ToString("0.#", inv)}");
        // depth edits the start already had: saved steps of an earlier session, or a version imported from Lumen
        if (_history != null && !Cleared && (HInfo.Entries.Any(e => e.FromEarlierSession) || HInfo.Entries.Any(e => e.Kind == HistoryEntryKind.DepthImport))) parts.Add("depth edits");
        else if (_history != null && Cleared && _depth?.Versions.FirstOrDefault(x => x.IsCurrent) is { Steps: > 0 } or { Kind: VersionKind.Lumen }) parts.Add("depth edits");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
    // from the list the library last sent (its own CanUndo / CanRedo may wait while the depth worker runs)
    public bool CanUndo => _history != null ? HInfo.Position > 0 && HInfo.Position <= HInfo.Entries.Count && HInfo.Entries[HInfo.Position - 1].Undoable : E.CanUndo;
    public bool CanRedo => _history != null ? HInfo.Position < HInfo.Entries.Count : E.CanRedo;

    public void Undo()
    {
        if (_history != null) { if (CanUndo) _ = Track(_history.Undo()); return; }
        if (!E.CanUndo) return; E.Undo(); Changed?.Invoke();
    }

    public void Redo()
    {
        if (_history != null) { if (CanRedo) _ = Track(_history.Redo()); return; }
        if (!E.CanRedo) return; E.Redo(); Changed?.Invoke();
    }

    public void GoTo(int position)
    {
        if (_history != null) { _ = Track(_history.GoTo(Math.Clamp(position, 0, HInfo.Entries.Count))); return; }
        E.GoToHistory(Math.Clamp(position, 0, E.History.Count)); Changed?.Invoke();
    }

    /// <summary>Going back undoes every step on the way: each must be undoable now (row i = entry i − 1).</summary>
    public bool CanGoTo(int position)
    {
        if (_history == null) return position >= 0 && position <= E.History.Count;
        var info = HInfo;
        if (position < 0 || position > info.Entries.Count) return false;
        for (int i = position; i < info.Position; i++) if (!info.Entries[i].Undoable) return false;
        return true;
    }

    async Task Track(DepthJob job)
    {
        try { await job.Completion.ConfigureAwait(false); } catch (Exception) { }
        Changed?.Invoke();
    }

    public SaveInfo Save
    {
        get
        {
            string where = E.SavedTo == ContextLocation.LocalStore ? "On this computer" : "With the photo";
            return E.SaveState switch
            {
                SaveState.Pending => new SaveInfo(SaveStatus.Saving, where),
                SaveState.Failed => new SaveInfo(SaveStatus.Failed, where, E.SaveError),
                _ => new SaveInfo(E.IsDirty ? SaveStatus.Saving : SaveStatus.Saved, where),
            };
        }
    }

    public Task SaveNowAsync() => E.SaveAsync();
    public HistogramData? Histogram => E.Histogram is { } h ? new HistogramData(h.Red, h.Green, h.Blue) : null;

    public IReadOnlyList<(string Label, string Value)> Info
    {
        get
        {
            var i = E.Info;
            var rows = new List<(string, string)> { ("Captured", Text.LongDateTime(i.CaptureTime)), ("Size", $"{i.FullSize.Width} × {i.FullSize.Height}") };
            if (i.ExportSize.Width > 0 && i.ExportSize != i.FullSize) rows.Add(("Exported as", $"{i.ExportSize.Width} × {i.ExportSize.Height} (as captured, as Lumen does)"));
            rows.Add(("Lens", Text.Lens(i.FocalLengthMm)));
            if (i.ExposureTimeSeconds is double t && t > 0) rows.Add(("Exposure", t >= 0.3 ? $"{t:0.#} s" : $"1/{Math.Round(1 / t)} s"));
            if (i.Iso is int iso) rows.Add(("ISO", iso.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (i.EvOffset is float ev) rows.Add(("EV", ev.ToString("+0.0;−0.0;0", System.Globalization.CultureInfo.InvariantCulture)));
            rows.Add(("Frames", i.StackFrames > 1 ? $"{i.StackFrames} per module (low-light stack)" : "1"));
            rows.Add(("Modules", $"{i.ModuleCount} · reference {i.ReferenceModule}"));
            rows.Add(("White balance", $"{i.AsShotTemperature:0} K, tint {i.AsShotTint:0} as shot"));
            return rows;
        }
    }

    public PictureStatus Status
    {
        get
        {
            var st = _ps.View.Status;
            bool processing = st.Quality is { Readiness: QualityReadiness.Processing };
            return new PictureStatus(st.OnScreen, processing, processing ? st.Quality!.Progress : double.NaN, st.Error, st.Rendering) { Note = st.QualityNote };
        }
    }

    public void SetQualityCap(ViewQuality? cap) => _tiles.SetQualityCap(cap);

    /// <summary>The export of what is on screen: the saved state (saved first) with this session's tone settings, the same object.</summary>
    public ExportOptions ExportSettings(ExportOptions baseOptions) => baseOptions with { Adjustments = E.Current.Adjustments, UseSavedEdits = true };

    public void Dispose()
    {
        if (_svc.OpenSession == this) _svc.OpenSession = null;
        _depth?.Dispose();
        _ps.Dispose();
    }
}
