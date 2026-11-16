using System.Diagnostics;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;
using Lux.Library.Core;
using Lux.Store;
using TAdj = Lux.Engine.Pipeline.Adjustments;

namespace Lux.Library.Edit;

/// <summary>What the picture is rendered from at one moment: the transform of the current layout, the render settings of both modes, the
/// versions and whether a slider is being dragged. Immutable; the edit makes a new one at every change.</summary>
internal sealed record RenderSpec(long EditedVersion, long OriginalVersion, long GeometryVersion, ExportTransform PictureTransform, PictureLayout Layout,
                                  int DisplayRotation, RenderSettings Edited, RenderSettings Original, AdjustmentKey? DragKey)
{
    /// <summary>The depth view's content version.</summary>
    public long DepthVersion { get; init; }
    /// <summary>The depth-effect slider is being dragged.</summary>
    public bool DepthDrag { get; init; }
    /// <summary>Any slider drag (tone or depth effect).</summary>
    public bool Dragging => DragKey is not null || DepthDrag;
    /// <summary>What caused the last geometry change, and the turn from the previous picture when it was a pure turn.</summary>
    public GeometryChangeCause Cause { get; init; }
    public int? TurnFromPrevious { get; init; }
    public RenderSettings For(ViewMode m) => m == ViewMode.Original ? Original : Edited;
    public long Version(ViewMode m) => m switch { ViewMode.Edited => EditedVersion, ViewMode.Depth => DepthVersion, _ => OriginalVersion };
}

/// <summary>One open photo (<see cref="IPhotoSession"/>): the engine session, the picture renderer, the view and the edit.</summary>
internal sealed class PhotoSessionImpl : IPhotoSession
{
    internal readonly LibraryCore Lib;
    internal readonly string Path;
    internal readonly CaptureSession Engine;
    internal readonly PictureRenderer Pictures;
    internal readonly PhotoOpenOptions Options;
    internal readonly ViewerSettings Settings;
    internal readonly ImageContext Context;
    internal readonly PhotoViewImpl ViewImpl;
    internal readonly PhotoEditImpl EditImpl;
    internal readonly Lux.Library.Depth.DepthSession DepthImpl;
    int _disposed;
    long _stamp;
    /// <summary>The next history stamp (one order for depth and non-depth steps of this session).</summary>
    internal long NextStamp() => Interlocked.Increment(ref _stamp);

    public string CaptureId { get; }
    public CaptureItem Item { get; }
    public IPhotoView View => ViewImpl;
    public IPhotoEdit Edit => EditImpl;
    public IPhotoDepth Depth => DepthImpl;
    public IDepthEdit DepthEdit => DepthImpl;
    public IPhotoHistory History => DepthImpl;

    PhotoSessionImpl(LibraryCore lib, CaptureItem item, string path, CaptureSession engine, StoreCaptureContext ctx, PhotoOpenOptions opt, ViewerSettings vs,
                     SavedRenderState? variant, CaptureRecipe? recipe, LumenProfile profile, WhiteBalance.CaptureWb wb)
    {
        Lib = lib; Item = item; CaptureId = item.Id; Path = path; Engine = engine; Options = opt; Settings = vs; Context = ctx.Context;
        Pictures = new PictureRenderer(engine);
        EditImpl = new PhotoEditImpl(this, variant, recipe, profile, wb);
        ViewImpl = new PhotoViewImpl(this);
        DepthImpl = new Lux.Library.Depth.DepthSession(this);
        EditImpl.Start();
        ViewImpl.Start();
        DepthImpl.Start();
        lib.SettingsImpl.PropertyChanged += OnSetting;
    }

    internal static async Task<PhotoSessionImpl> OpenAsync(LibraryCore lib, string id, PhotoOpenOptions? options, CancellationToken cancel)
    {
        var opt = options ?? new PhotoOpenOptions();
        if (opt.TileSize < 64 || opt.TileSize > 2048) throw new LibraryException("The tile size must be between 64 and 2048 pixels.");
        var item = lib.Get(id) ?? throw new LibraryException("The photo is no longer in the library.");
        if (item.Unreadable) throw new LibraryException(item.Error ?? "This photo cannot be read.");
        string path = lib.PresentPath(id);
        var vs = lib.SettingsImpl.Viewer;
        return await Task.Run(() =>
        {
            var ctx = lib.ContextFor(id, path, mayCreateFolder: false);
            Lux.Library.Depth.DepthSession? depthSink = null;
            var engine = CaptureSession.Open(path, new SessionOptions
            {
                Context = ctx, Threads = vs.Threads, KeepGeometry = true, DisplayCacheBytes = (long)vs.DisplayCacheMb << 20,
                Log = lib.Log is null ? null : m => lib.Log($"[view {id[..8]}] {m}"),
                Progress = u => depthSink?.OnEngineProgress(u),
            });
            try
            {
                cancel.ThrowIfCancellationRequested();
                var (variant, recipe) = SavedDepthEdits.LoadParts(ctx.Context);
                var profile = LumenProfile.Compute(engine.Lri);
                var wb = WhiteBalance.CaptureWb.From(engine.Lri, profile);
                var s = new PhotoSessionImpl(lib, item, path, engine, ctx, opt, vs, variant, recipe, profile, wb);
                depthSink = s.DepthImpl;
                lib.RegisterSession(s);
                return s;
            }
            catch { engine.Dispose(); throw; }
        }, cancel).ConfigureAwait(false);
    }

    void OnSetting(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ILibrarySettings.ShowPhotosAsHeld) && !IsDisposed) EditImpl.OnDisplaySettingChanged();
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>A job changed what the capture keeps: the engine reads its cache again, the viewer renders the last
    /// request again for the finest level the photo may now show, and a photo without depth reads a depth the capture now keeps.</summary>
    internal void OnCacheChanged()
    {
        if (IsDisposed) return;
        Engine.RefreshCache();
        ViewImpl.OnCacheChanged();
        DepthImpl.OnCacheChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { EditImpl.FlushSave(); } catch (Exception e) { Lib.Trace($"saving the edits of {CaptureId[..8]} at close: {e.Message}"); }
        Lib.SettingsImpl.PropertyChanged -= OnSetting;
        try { DepthImpl.Stop(); } catch (Exception e) { Lib.Trace($"closing the depth of {CaptureId[..8]}: {e.Message}"); }
        ViewImpl.Stop();
        EditImpl.Stop();
        Lib.UnregisterSession(this);
        Engine.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>The edit state of the open photo (<see cref="IPhotoEdit"/>).</summary>
internal sealed class PhotoEditImpl : IPhotoEdit
{
    readonly PhotoSessionImpl _s;
    readonly object _lock = new();
    SavedRenderState? _variant;   // what renders without a Lux recipe (the active variant's saved settings), or null; replaced by a version switch
    readonly LumenProfile _profile;
    readonly WhiteBalance.CaptureWb _wb;

    /// <summary>The editable state.</summary>
    internal sealed class State
    {
        public TAdj Adj = TAdj.Default;
        public LumenGeometryEdits Geo = null!;
        public float? FNumber, Focus;
        /// <summary>The geometry is a decision of its own: the display-only held turn does not apply.</summary>
        public bool Decided;
        public State Clone() => new() { Adj = Adj, Geo = Geo.Clone(), FNumber = FNumber, Focus = Focus, Decided = Decided };
    }

    State _st;
    State _base;     // the state without any Lux recipe (variant or capture defaults)
    State _opened;
    /// <summary>One history step: the state before it, its label, its stamp in the session's combined order, its kind.</summary>
    internal readonly record struct UStep(State Before, string Label, long Stamp, HistoryEntryKind Kind);
    readonly List<UStep> _undo = new(), _redo = new();
    State? _dragStart; AdjustmentKey? _dragKey; bool _depthDrag;
    IDisplayOverlay? _overlay;
    State? _cropBefore;   // the state before the crop tool opened (it bakes the held turn provisionally; cancel restores this)
    GeometryChangeCause _cause = GeometryChangeCause.Open; int? _turn;
    long _version = 1, _originalVersion = 1, _geometryVersion = 1, _floor = 1, _originalFloor = 1, _depthVersion = 1, _depthFloor = 1;
    RenderSpec _spec = null!;
    // saving
    Timer? _saveTimer; bool _dirty; string? _saveError; SaveState _saveState = SaveState.Saved; ContextLocation _savedTo;
    readonly object _saveLock = new();
    Histogram? _histogram;
    readonly IReadOnlyList<AdjustmentInfo> _infos;
    readonly LevelSuggestion _level;
    readonly PhotoInfo _info;

    public event EventHandler<EditChangedEventArgs>? Changed;

    internal PhotoEditImpl(PhotoSessionImpl s, SavedRenderState? variant, CaptureRecipe? recipe, LumenProfile profile, WhiteBalance.CaptureWb wb)
    {
        _s = s; _profile = profile; _wb = wb;
        (_base, _st) = InitStates(variant, recipe);
        _variant = variant;
        _opened = _st.Clone();
        _savedTo = s.Item.ContextLocation;
        var (t0, u0) = TAdj.CaptureWhiteBalance(wb);
        var dflt = TAdj.Default;
        const string note = "JPG and preview only";
        _infos = new[]
        {
            new AdjustmentInfo(AdjustmentKey.Exposure, EditGroups.Tone, "Exposure", "EV", TAdj.ExposureMin, TAdj.ExposureMax, 0f, 0.05f, 2, false, note),
            new AdjustmentInfo(AdjustmentKey.Contrast, EditGroups.Tone, "Contrast", "", -100f, 100f, 0f, 1f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Temperature, EditGroups.Colour, "Temperature", "K", TAdj.TemperatureMin, TAdj.TemperatureMax, t0, 50f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Tint, EditGroups.Colour, "Tint", "", TAdj.TintMin, TAdj.TintMax, u0, 1f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Vibrance, EditGroups.Colour, "Vibrance", "", -100f, 100f, 0f, 1f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Saturation, EditGroups.Colour, "Saturation", "", -100f, 100f, 0f, 1f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Sharpening, EditGroups.Sharpening, "Sharpening", "", -100f, 100f, dflt.Sharpening, 1f, 0, false, note),
            new AdjustmentInfo(AdjustmentKey.Shadows, EditGroups.Tone, "Shadows", "", -100f, 100f, 0f, 1f, 0, true, note),
            new AdjustmentInfo(AdjustmentKey.Highlights, EditGroups.Tone, "Highlights", "", -100f, 100f, 0f, 1f, 0, true, note),
            new AdjustmentInfo(AdjustmentKey.Clarity, EditGroups.Tone, "Clarity", "", -100f, 100f, 0f, 1f, 0, true, note),
        };
        var lri = s.Engine.Lri;
        var (levelDegrees, levelState) = Lux.Engine.Lri.CaptureMotion.LevelOf(lri.Header, Lux.Store.HeldTurn.Degrees(lri));
        _level = LevelSuggestion.From(levelDegrees, levelState);
        _info = new PhotoInfo
        {
            Name = s.Item.Name, CaptureTime = s.Item.CaptureTime, FocalLengthMm = s.Item.FocalLengthMm, ExposureText = s.Item.ExposureText, Iso = s.Item.Iso,
            ExposureTimeSeconds = s.Item.ExposureTimeSeconds, EvOffset = lri.ViewPreferencesBlock is { HasEvOffset: true } vp ? vp.EvOffset : null,
            StackFrames = s.Item.StackFrames, ReferenceModule = s.Item.ReferenceModule, ModuleCount = s.Item.ModuleCount,
            AsShotTemperature = t0, AsShotTint = u0, AllInFocusFNumber = s.Engine.Info.AllInFocusFNumber,
        };
        _spec = BuildSpec(null);
    }

    /// <summary>The base (the variant's settings, or the capture's own rendering) and the state with the recipe on top.</summary>
    (State Base, State Current) InitStates(SavedRenderState? variant, CaptureRecipe? recipe)
    {
        var win = _s.Pictures.Window;
        var id0 = ExportTransform.Identity(win.ExportDims[0]);
        var b = new State
        {
            Adj = variant?.Adjustments ?? TAdj.Default,
            Geo = new LumenGeometryEdits(variant?.Transform is { } vt ? LumenTransform.FromExport(vt) : LumenTransform.Identity(id0.AspectW, id0.AspectH)),
            FNumber = variant?.FNumber, Focus = variant?.FocusDepth,
            Decided = CaptureRecipe.IsGeometryDecided(null, variant?.Transform),
        };
        var st = b.Clone();
        st.Decided = CaptureRecipe.IsGeometryDecided(recipe, variant?.Transform);
        if (recipe is not null)
        {
            if (recipe.Adjustments is { } a) st.Adj = a;
            if (recipe.Transform is { } t) st.Geo = new LumenGeometryEdits(LumenTransform.FromExport(t));
            if (recipe.FNumber is float f) st.FNumber = f;
            if (recipe.FocusDepth is float fd) st.Focus = fd;
        }
        return (b, st);
    }

    /// <summary>The photo's depth version changed: the base is the new variant's saved settings with the saved recipe on top; the session's
    /// non-depth history is dropped (its steps were relative to the old base). Call after <see cref="FlushSave"/> and the engine's reload.</summary>
    internal void Rebase(SavedRenderState? variant, CaptureRecipe? recipe)
    {
        lock (_lock)
        {
            (_base, _st) = InitStates(variant, recipe);
            _variant = variant;
            _opened = _st.Clone();
            _undo.Clear(); _redo.Clear(); _dragStart = null; _dragKey = null; _depthDrag = false; _cropBefore = null;
        }
        Changed_(EditChangeKind.History, null, committed: true, geometry: true, save: false, cause: GeometryChangeCause.Unknown);
    }

    internal void Start() { }
    internal void Stop() { lock (_saveLock) { _saveTimer?.Dispose(); _saveTimer = null; } }

    void CheckWritable() { if (_s.Options.ReadOnly) throw new InvalidOperationException("This photo is open for viewing only."); }

    // ------------------------------------------------------------------------------------------------ the render spec

    /// <summary>The render spec now (lock-free: an immutable record, replaced under the lock).</summary>
    internal RenderSpec Spec => Volatile.Read(ref _spec);

    RenderSpec BuildSpec(AdjustmentKey? drag)
    {
        var t = _st.Geo.T;
        bool cropping = _st.Geo.CropState != 0;
        var layoutT = cropping ? LumenGeometryEdits.Uncropped(t).ToExport() : t.ToExport();
        bool toneV2 = _variant?.ToneV2 ?? false;
        var edited = new RenderSettings
        {
            Output = RenderOutput.Display, ToneV2 = toneV2, Adjustments = _st.Adj,
            DepthEffectFNumber = _st.FNumber, FocusDepthMm = _st.Focus is float f && f > 0f ? f : null,
        };
        var original = edited with { Adjustments = TAdj.Default };
        edited = edited with { Overlay = _overlay };
        return new RenderSpec(_version, _originalVersion, _geometryVersion, layoutT, cropping ? PictureLayout.Uncropped : PictureLayout.Picture,
                              DisplayRotationFor(_st), edited, original, drag) { DepthVersion = _depthVersion, DepthDrag = _depthDrag };
    }

    /// <summary>The held turn, degrees clockwise (0 when the camera was upright or the orientation is unknown).</summary>
    internal static int HeldDegrees(PhotoOrientation held) => held switch { PhotoOrientation.Rotate90 => 90, PhotoOrientation.Rotate180 => 180, PhotoOrientation.Rotate270 => 270, _ => 0 };

    /// <summary>The display-only turn: the held turn while the setting is on and the photo has no geometry decision of its own.</summary>
    int DisplayRotationFor(State s) => _s.Lib.SettingsImpl.ShowPhotosAsHeld && !s.Decided ? HeldDegrees(_s.Item.HeldOrientation) : 0;

    /// <summary>Turn a state's transform by <paramref name="degrees"/> clockwise with Lumen's own operations (rotate right = clockwise).</summary>
    internal static void Turn(State s, int degrees)
    {
        switch (((degrees % 360) + 360) % 360)
        {
            case 90: s.Geo.RotateRight(); break;
            case 180: s.Geo.RotateRight(); s.Geo.RotateRight(); break;
            case 270: s.Geo.RotateLeft(); break;
        }
    }

    /// <summary>The first geometry edit: the display-only held turn becomes a real rotation of the saved transform, and the geometry is
    /// decided from then on. No-op when already decided.</summary>
    void Bake(State s)
    {
        if (s.Decided) return;
        Turn(s, DisplayRotationFor(s));
        s.Decided = true;
    }

    /// <summary>Apply a change: new versions, a new spec, the view re-renders. <paramref name="committed"/> raises the floor and schedules a save;
    /// <paramref name="geometry"/> changes the picture's geometry (both modes).</summary>
    void Changed_(EditChangeKind kind, AdjustmentKey? key, bool committed, bool geometry, bool save = true, GeometryChangeCause cause = GeometryChangeCause.Unknown, string? step = null)
    {
        EditValues state;
        lock (_lock)
        {
            _version++;
            // a change of the picture's layout, display rotation or transform is a geometry change whatever caused it
            var old = _spec; var probe = BuildSpec(_dragKey);
            if (old is null || probe.DisplayRotation != old.DisplayRotation || probe.Layout != old.Layout || !SameTransform(probe.PictureTransform, old.PictureTransform)) geometry = true;
            if (geometry)
            {
                _geometryVersion++; _originalVersion++; _originalFloor = _originalVersion; _depthVersion++; _depthFloor = _depthVersion;
                _cause = cause; _turn = old is null ? null : PureTurn(old, probe);
            }
            else if (kind is EditChangeKind.Transform or EditChangeKind.History || (key is null && committed)) { _originalVersion++; _originalFloor = _originalVersion; }
            if (committed) _floor = _version;
            _spec = BuildSpec(_dragKey) with { Cause = _cause, TurnFromPrevious = _turn };
            state = Snapshot(_st);
        }
        if (committed && save) ScheduleSave();
        _s.ViewImpl.OnSpecChanged(geometry, committed);
        Raise(new EditChangedEventArgs { Kind = kind, Key = key, State = state, StepLabel = step is null ? null : Cap(step) });
        if (committed) _s.ViewImpl.RequestHistogram();
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>When the new picture is the old one turned (both unmirrored, the same layout and the same part of the photo): degrees clockwise.</summary>
    static int? PureTurn(RenderSpec a, RenderSpec b)
    {
        if (a.Layout != b.Layout) return null;
        var ta = LumenTransform.FromExport(a.PictureTransform); var tb = LumenTransform.FromExport(b.PictureTransform);
        var (aa, ma) = LumenGeometryEdits.Orientation(ta); var (ab, mb) = LumenGeometryEdits.Orientation(tb);
        if (ma || mb) return null;
        var fa = LumenGeometryEdits.RectFromCrop(ta); var fb = LumenGeometryEdits.RectFromCrop(tb);
        if (Math.Abs(fa.X - fb.X) > 1e-5 || Math.Abs(fa.Y - fb.Y) > 1e-5 || Math.Abs(fa.W - fb.W) > 1e-5 || Math.Abs(fa.H - fb.H) > 1e-5) return null;
        int d = (((ab + b.DisplayRotation) - (aa + a.DisplayRotation)) % 360 + 360) % 360;
        return d == 0 ? null : d;
    }

    static bool SameTransform(ExportTransform a, ExportTransform b)
        => a.AspectW == b.AspectW && a.AspectH == b.AspectH && a.CropX0.Equals(b.CropX0) && a.CropY0.Equals(b.CropY0) && a.CropX1.Equals(b.CropX1) && a.CropY1.Equals(b.CropY1)
           && a.Matrix.Zip(b.Matrix).All(p => BitConverter.SingleToUInt32Bits(p.First) == BitConverter.SingleToUInt32Bits(p.Second));

    /// <summary>The "show photos as held" setting changed: the display rotation follows.</summary>
    internal void OnDisplaySettingChanged() => Changed_(EditChangeKind.Transform, null, committed: true, geometry: false, save: false, cause: GeometryChangeCause.DisplaySetting);

    void Raise(EditChangedEventArgs e) { try { Changed?.Invoke(this, e); } catch (Exception x) { _s.Lib.Trace("edit listener: " + x.Message); } }

    internal (long Version, long Floor) Versions(ViewMode m)
    {
        lock (_lock) return m switch { ViewMode.Edited => (_version, _floor), ViewMode.Depth => (_depthVersion, _depthFloor), _ => (_originalVersion, _originalFloor) };
    }

    // ---- the depth effect (f-number, focus) as edits of this state; the overlay and depth-change versions

    /// <summary>Set the f-number and / or focus (one history step <paramref name="label"/>); returns false when nothing changed.</summary>
    internal bool SetDepthEffect(float? fNumber, float? focus, bool setF, bool setFocus, string label)
    {
        CheckWritable();
        lock (_lock)
        {
            var next = _st.Clone();
            if (setF) next.FNumber = fNumber;
            if (setFocus) next.Focus = focus;
            if (Nullable.Equals(next.FNumber, _st.FNumber) && Nullable.Equals(next.Focus, _st.Focus)) return false;
            Push(_st.Clone(), label, HistoryEntryKind.DepthEffect);
            _st = next;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: false, step: label);
        return true;
    }

    internal void BeginDepthDrag()
    {
        CheckWritable();
        lock (_lock) { _dragStart = _st.Clone(); _depthDrag = true; }
        _s.Lib.Queue.SetInteractive(true);
    }

    internal void DragDepth(float? fNumber)
    {
        CheckWritable();
        lock (_lock)
        {
            if (!_depthDrag) { _dragStart ??= _st.Clone(); _depthDrag = true; }
            if (Nullable.Equals(_st.FNumber, fNumber)) return;
            _st.FNumber = fNumber;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: false, geometry: false);
    }

    /// <summary>A focus slider move: the focus as a drag value (no step until <see cref="EndDepthDrag"/>).</summary>
    internal void DragDepthFocus(float focusMm)
    {
        CheckWritable();
        lock (_lock)
        {
            if (!_depthDrag) { _dragStart ??= _st.Clone(); _depthDrag = true; }
            if (Nullable.Equals(_st.Focus, focusMm)) return;
            _st.Focus = focusMm;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: false, geometry: false);
    }

    internal void EndDepthDrag()
    {
        CheckWritable();
        bool changed; string label = "depth effect";
        lock (_lock)
        {
            var start = _dragStart; _dragStart = null; _depthDrag = false;
            bool f = start is not null && !Nullable.Equals(start.FNumber, _st.FNumber), fo = start is not null && !Nullable.Equals(start.Focus, _st.Focus);
            changed = f || fo;
            if (fo && !f) label = "focus";
            if (changed) Push(start!, label, HistoryEntryKind.DepthEffect);
        }
        _s.Lib.Queue.SetInteractive(false);
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: false, save: changed, step: changed ? label : null);
    }

    /// <summary>The f-number and focus settings now.</summary>
    internal (float? FNumber, float? Focus) DepthEffect { get { lock (_lock) return (_st.FNumber, _st.Focus); } }

    /// <summary>Put an overlay in place. <paramref name="committed"/>: the whole picture changes (a mode switch: the floor rises, everything is
    /// rendered again); otherwise only <paramref name="rect"/> changed (a stroke preview, the selection after a stroke) and only its tiles are
    /// rendered again at a new version.</summary>
    internal void SetOverlay(IDisplayOverlay? overlay, bool committed, NormRect? rect)
    {
        RenderSpec spec;
        lock (_lock)
        {
            if (ReferenceEquals(_overlay, overlay)) return;
            _overlay = overlay;
            _version++;
            if (committed) _floor = _version;
            spec = _spec = BuildSpec(_dragKey) with { Cause = _cause, TurnFromPrevious = _turn };
        }
        if (committed) _s.ViewImpl.OnSpecChanged(false, true);
        else _s.ViewImpl.OnRegionsChanged(rect, null);
    }

    internal IDisplayOverlay? Overlay { get { lock (_lock) return _overlay; } }

    /// <summary>A depth change: new versions of the photo modes (when <paramref name="photo"/>) and of the depth view (when
    /// <paramref name="depth"/>), no floor raised — tiles outside the changed regions stay valid. Optionally a new overlay at the same time.
    /// Returns the new (photo, depth) versions.</summary>
    internal (long Photo, long Depth) BumpForDepth(bool photo, bool depth, IDisplayOverlay? overlay, bool setOverlay)
    {
        lock (_lock)
        {
            if (setOverlay) _overlay = overlay;
            if (photo || setOverlay) { _version++; _originalVersion++; }
            if (depth) _depthVersion++;
            _spec = BuildSpec(_dragKey) with { Cause = _cause, TurnFromPrevious = _turn };
            return (_version, _depthVersion);
        }
    }

    /// <summary>The undo and redo steps (stamps for the combined history).</summary>
    internal (UStep[] Undo, UStep[] Redo) Steps { get { lock (_lock) return (_undo.ToArray(), _redo.ToArray()); } }

    /// <summary>A new depth step discards this kind's undone steps too (one linear history).</summary>
    internal void ClearRedo() { lock (_lock) _redo.Clear(); }

    /// <summary>"Clear history": the picture stays as it is; this session's steps go and the state now is the list's start.</summary>
    internal void ClearSteps()
    {
        lock (_lock) { _undo.Clear(); _redo.Clear(); _opened = _st.Clone(); _cleared = true; }
        Changed_(EditChangeKind.History, null, committed: false, geometry: false, save: false);
    }

    /// <summary>The history was cleared in this session (the list's first row is then the state at that moment, not the opened one).</summary>
    internal bool Cleared { get { lock (_lock) return _cleared; } }
    bool _cleared;

    // ------------------------------------------------------------------------------------------------ values

    public IReadOnlyList<AdjustmentInfo> Adjustments => _infos;

    /// <summary>The capture facts and the sizes now: as shown (agrees with the view's level 0) and as exported.</summary>
    public PhotoInfo Info
    {
        get
        {
            ExportTransform t; int rot; bool dec;
            lock (_lock) { t = _st.Geo.T.ToExport(); rot = DisplayRotationFor(_st); dec = _st.Decided; }
            var e = _s.Pictures.SizeOf(t, 0); var shown = PictureRenderer.Rotated(e, rot);
            // exports follow the view shown as held (unless the setting keeps them as Lumen writes them)
            var exp = rot != 0 && _s.Lib.ExportsTurnUpright(null) ? shown : e;
            return _info with { FullSize = new PixelSize(shown.W, shown.H), ExportSize = new PixelSize(exp.W, exp.H), GeometryDecided = dec };
        }
    }

    public bool GeometryDecided { get { lock (_lock) return _st.Decided; } }
    public EditValues Current { get { lock (_lock) return Snapshot(_st); } }
    public EditValues Opened { get { lock (_lock) return Snapshot(_opened); } }

    internal EditValues Snapshot(State st) => new()
    {
        Groups = EditGroups.All, Adjustments = st.Adj, Transform = Describe(st.Geo.T), FNumber = st.FNumber, FocusMm = st.Focus,
    };

    internal static PhotoTransform Describe(LumenTransform t)
    {
        var (angle, mir) = LumenGeometryEdits.Orientation(t);
        var c = LumenGeometryEdits.ShownCrop(t);
        return new PhotoTransform
        {
            TurnDegrees = angle, Mirrored = mir, LevelDegrees = LumenGeometryEdits.LevelDegrees(t), Crop = new NormRect(c.X, c.Y, c.W, c.H), IsIdentity = t.IsIdentity, AspectWidth = t.AspectW, AspectHeight = t.AspectH,
            Matrix = (float[])t.M.Clone(), CropX0 = t.X0, CropY0 = t.Y0, CropX1 = t.X1, CropY1 = t.Y1,
        };
    }

    internal static LumenTransform ToLumen(PhotoTransform p)
    {
        var t = new LumenTransform { AspectW = p.AspectWidth, AspectH = p.AspectHeight, X0 = p.CropX0, Y0 = p.CropY0, X1 = p.CropX1, Y1 = p.CropY1 };
        for (int i = 0; i < 9; i++) t.M[i] = p.Matrix[i];
        return t;
    }

    static float Get(TAdj a, AdjustmentKey k) => k switch
    {
        AdjustmentKey.Exposure => a.Exposure, AdjustmentKey.Contrast => a.Contrast, AdjustmentKey.Temperature => a.Temperature ?? float.NaN,
        AdjustmentKey.Tint => a.Tint ?? float.NaN, AdjustmentKey.Vibrance => a.Vibrance, AdjustmentKey.Saturation => a.Saturation,
        AdjustmentKey.Sharpening => a.Sharpening, AdjustmentKey.Shadows => a.Shadows, AdjustmentKey.Highlights => a.Highlights, AdjustmentKey.Clarity => a.Clarity,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    internal static TAdj With(TAdj a, AdjustmentKey k, float? v) => k switch
    {
        AdjustmentKey.Exposure => a with { Exposure = v ?? 0f }, AdjustmentKey.Contrast => a with { Contrast = v ?? 0f },
        AdjustmentKey.Temperature => a with { Temperature = v }, AdjustmentKey.Tint => a with { Tint = v },
        AdjustmentKey.Vibrance => a with { Vibrance = v ?? 0f }, AdjustmentKey.Saturation => a with { Saturation = v ?? 0f },
        AdjustmentKey.Sharpening => a with { Sharpening = v ?? TAdj.GuiSharpening },
        AdjustmentKey.Shadows => a with { Shadows = v ?? 0f }, AdjustmentKey.Highlights => a with { Highlights = v ?? 0f }, AdjustmentKey.Clarity => a with { Clarity = v ?? 0f },
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public float GetValue(AdjustmentKey key)
    {
        lock (_lock)
        {
            float v = Get(_st.Adj, key);
            if (float.IsNaN(v)) v = key == AdjustmentKey.Temperature ? _wb.Cct : _wb.Tint;
            return v;
        }
    }

    public bool IsAsShot(AdjustmentKey key)
    {
        lock (_lock) return key switch { AdjustmentKey.Temperature => _st.Adj.Temperature is null, AdjustmentKey.Tint => _st.Adj.Tint is null, _ => false };
    }

    float Clamp(AdjustmentKey key, float v)
    {
        var i = _infos[(int)key];
        if (float.IsNaN(v)) throw new ArgumentException("not a number", nameof(v));
        return Math.Clamp(v, i.Min, i.Max);
    }

    static string Label(AdjustmentKey k) => k switch
    {
        AdjustmentKey.Exposure => "exposure", AdjustmentKey.Contrast => "contrast", AdjustmentKey.Temperature => "temperature", AdjustmentKey.Tint => "tint",
        AdjustmentKey.Vibrance => "vibrance", AdjustmentKey.Saturation => "saturation", AdjustmentKey.Sharpening => "sharpening", AdjustmentKey.Shadows => "shadows",
        AdjustmentKey.Highlights => "highlights", _ => "clarity",
    };

    void Push(State before, string label, HistoryEntryKind kind = HistoryEntryKind.Adjustment)
    {
        _undo.Add(new UStep(before, label, _s.NextStamp(), kind));
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        _s.DepthImpl?.OnNonDepthStep();
    }

    public void SetValue(AdjustmentKey key, float value)
    {
        CheckWritable();
        lock (_lock)
        {
            var v = Clamp(key, value);
            var next = With(_st.Adj, key, v);
            if (next == _st.Adj) return;
            Push(_st.Clone(), Label(key));
            _st.Adj = next;
        }
        Changed_(EditChangeKind.Adjustment, key, committed: true, geometry: false, step: Label(key));
    }

    public void BeginDrag(AdjustmentKey key)
    {
        CheckWritable();
        lock (_lock) { _dragStart = _st.Clone(); _dragKey = key; }
        _s.Lib.Queue.SetInteractive(true);
    }

    public void DragTo(AdjustmentKey key, float value)
    {
        CheckWritable();
        lock (_lock)
        {
            if (_dragKey != key) { _dragStart ??= _st.Clone(); _dragKey = key; }
            var next = With(_st.Adj, key, Clamp(key, value));
            if (next == _st.Adj) return;
            _st.Adj = next;
        }
        Changed_(EditChangeKind.Adjustment, key, committed: false, geometry: false);
    }

    public void EndDrag(AdjustmentKey key)
    {
        CheckWritable();
        bool changed;
        lock (_lock)
        {
            var start = _dragStart; _dragStart = null; _dragKey = null;
            changed = start is not null && start.Adj != _st.Adj;
            if (changed) Push(start!, Label(key));
        }
        _s.Lib.Queue.SetInteractive(false);
        // always a committed refresh: the picture is refined from the drag level (an unchanged drag adds no step and saves nothing)
        Changed_(EditChangeKind.Adjustment, key, committed: true, geometry: false, save: changed, step: changed ? Label(key) : null);
    }

    public void Reset(AdjustmentKey key)
    {
        CheckWritable();
        lock (_lock)
        {
            var next = With(_st.Adj, key, key is AdjustmentKey.Temperature or AdjustmentKey.Tint ? null : Get(TAdj.Default, key));
            if (next == _st.Adj) return;
            Push(_st.Clone(), "reset " + Label(key));
            _st.Adj = next;
        }
        Changed_(EditChangeKind.Adjustment, key, committed: true, geometry: false, step: "reset " + Label(key));
    }

    static TAdj ResetAdj(TAdj a, EditGroups g)
    {
        var d = TAdj.Default;
        if (g.HasFlag(EditGroups.Tone)) a = a with { Exposure = d.Exposure, Contrast = d.Contrast, Shadows = d.Shadows, Highlights = d.Highlights, Clarity = d.Clarity };
        if (g.HasFlag(EditGroups.Colour)) a = a with { Temperature = null, Tint = null, Vibrance = d.Vibrance, Saturation = d.Saturation, WhiteBalancePushed = false };
        if (g.HasFlag(EditGroups.Sharpening)) a = a with { Sharpening = d.Sharpening };
        return a;
    }

    /// <summary>The state with groups reset to the capture's own rendering (no tone, no crop / rotation; depth effect all in focus).</summary>
    internal State ResetGroupsOf(State s0, EditGroups g)
    {
        var s = s0.Clone();
        s.Adj = ResetAdj(s.Adj, g);
        if ((g & EditGroups.Geometry) == EditGroups.Geometry) { s.Geo.ResetGeometry(); s.Decided = false; }   // v2.1: the held display returns
        else if (g.HasFlag(EditGroups.Crop)) { s.Geo.SetCropState(0); LumenGeometryEdits.SetCropFromRect(s.Geo.T, new QRect(0, 0, 1, 1)); s.Geo.InitTransform(s.Geo.T); s.Geo.FitFrame(); }
        else if (g.HasFlag(EditGroups.RotationAndMirror))
        {
            var frame = LumenGeometryEdits.RectFromCrop(s.Geo.T);
            s.Geo.SetCropState(0);
            s.Geo.T.Reset(0);
            LumenGeometryEdits.SetCropFromRect(s.Geo.T, frame);
            s.Geo.InitTransform(s.Geo.T);
        }
        if (g.HasFlag(EditGroups.DepthEffect)) { s.FNumber = _base.FNumber is null ? null : Info.AllInFocusFNumber; s.Focus = _base.Focus is null ? null : -1f; }
        return s;
    }

    public void ResetGroups(EditGroups groups)
    {
        CheckWritable();
        bool geo;
        lock (_lock)
        {
            var next = ResetGroupsOf(_st, groups);
            geo = !next.Geo.T.SameAs(_st.Geo.T) || next.Geo.CropState != _st.Geo.CropState || next.Decided != _st.Decided;
            // nothing to reset: no step (as an unchanged drag) — except "Revert to original", which always shows as its own step (a revert
            // that left the selection on "Opened" would read as a jump back, not as a revert)
            if (!geo && next.Adj == _st.Adj && next.FNumber == _st.FNumber && next.Focus == _st.Focus && groups != EditGroups.All) return;
            Push(_st.Clone(), groups == EditGroups.All ? "revert to original" : "reset", groups == EditGroups.DepthEffect ? HistoryEntryKind.DepthEffect : HistoryEntryKind.Several);
            _st = next; _cropBefore = null;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: geo, cause: GeometryChangeCause.Reset, step: groups == EditGroups.All ? "revert to original" : "reset");
    }

    public void RevertToOriginal() => ResetGroups(EditGroups.All);

    public bool Apply(EditValues values, EditGroups groups)
    {
        CheckWritable();
        bool ok, geo;
        lock (_lock)
        {
            var (next, cropOk) = ApplyTo(_st, values, groups, DisplayRotationFor(_st));
            ok = cropOk;
            geo = !next.Geo.T.SameAs(_st.Geo.T) || next.Decided != _st.Decided;
            Push(_st.Clone(), "paste", HistoryEntryKind.Several);
            _st = next;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: geo, cause: GeometryChangeCause.Paste, step: "paste");
        return ok;
    }

    /// <summary>The groups of <paramref name="v"/> applied to a state (copy / paste and presets): tone and color values replace, rotation and
    /// mirroring are rebuilt with Lumen's own operations around the photo's crop frame, a crop is the source's frame on the photo's
    /// orientation. False when the crop could not be applied (another aspect ratio).</summary>
    internal static (State Next, bool CropOk) ApplyTo(State s0, EditValues v, EditGroups groups, int heldTurn)
    {
        var g = groups & v.Groups;
        var s = s0.Clone();
        var a = s.Adj; var src = v.Adjustments;
        if (v.Sliders is { } only)
        {   // exactly the chosen sliders
            foreach (var k in only)
                a = With(a, k, k == AdjustmentKey.Temperature ? src.Temperature : k == AdjustmentKey.Tint ? src.Tint : Get(src, k));
        }
        else
        {
            if (g.HasFlag(EditGroups.Tone)) a = a with { Exposure = src.Exposure, Contrast = src.Contrast, Shadows = src.Shadows, Highlights = src.Highlights, Clarity = src.Clarity };
            if (g.HasFlag(EditGroups.Colour)) a = a with { Temperature = src.Temperature, Tint = src.Tint, Vibrance = src.Vibrance, Saturation = src.Saturation };
            if (g.HasFlag(EditGroups.Sharpening)) a = a with { Sharpening = src.Sharpening };
        }
        s.Adj = a;
        if ((g & EditGroups.Geometry) != 0 && !s.Decided)
        {   // v2.1: a paste of geometry is a geometry edit — the held display turn is baked first
            Turn(s, heldTurn);
            s.Decided = true;
        }
        if (g.HasFlag(EditGroups.DepthEffect)) { s.FNumber = v.FNumber; s.Focus = v.FocusMm; }
        bool cropOk = true;
        var vt = ToLumen(v.Transform);
        bool sameAspect = vt.AspectW == s.Geo.T.AspectW && vt.AspectH == s.Geo.T.AspectH;
        if ((g & EditGroups.Geometry) != 0)
        {
            var t = s.Geo.T.Clone(); t.Ops = s.Geo.T.Ops;
            if ((g & EditGroups.Geometry) == EditGroups.Geometry && sameAspect)
            {
                LumenGeometryEdits.RemoveLevel(vt);   // the level angle belongs to the source capture: a paste does not carry it
                t = vt; t.Ops = s.Geo.T.Ops;
            }
            else
            {
                QRect frame = LumenGeometryEdits.RectFromCrop(s.Geo.T);
                if (g.HasFlag(EditGroups.Crop))
                {
                    if (sameAspect) frame = LumenGeometryEdits.RectFromCrop(vt); else cropOk = false;
                }
                if (g.HasFlag(EditGroups.RotationAndMirror))
                {
                    // the source's orientation (Lumen's canonical rebuild: reset, quarter turns, mirror), the photo's frame on it
                    var (angle, mir) = LumenGeometryEdits.Orientation(vt);
                    t.Reset(0);
                    if (angle != 0) { long p = (long)angle * 0x49f49f49; int e = (int)(p >> 32) - angle; int q = e >> 6; q += (int)((uint)q >> 31); t.Rotate90(q); }
                    if (mir) t.FlipHorizontal();
                }
                LumenGeometryEdits.SetCropFromRect(t, frame);
            }
            s.Geo = new LumenGeometryEdits(t);
            s.Geo.FitFrame();   // a pasted crop on a leveled picture: no empty corners
            s.Decided = true;
        }
        return (s, cropOk);
    }

    // ------------------------------------------------------------------------------------------------ geometry

    public PhotoTransform Transform { get { lock (_lock) return Describe(_st.Geo.T); } }
    public PhotoOrientation HeldOrientation => _s.Item.HeldOrientation;
    public bool CanRotateAsHeld
    {
        get { lock (_lock) return HeldDegrees(HeldOrientation) != 0 && !_st.Decided && LibraryCore.OrientationOf(_st.Geo.T.M) == PhotoOrientation.Normal; }
    }

    /// <summary>One geometry edit (one history step named for it): the held display turn is baked first when the geometry was not decided.</summary>
    void Geometry(string label, GeometryChangeCause cause, Action<LumenGeometryEdits> op, bool bake = true)
    {
        CheckWritable();
        lock (_lock)
        {
            if (_st.Geo.CropState != 0 && _cropBefore is not null) { _st = _cropBefore; }   // an open crop tool is left (as Lumen leaves it before other tools)
            _cropBefore = null;
            var before = _st.Clone();
            if (bake) Bake(_st);
            op(_st.Geo);
            _st.Decided = true;
            if (_st.Geo.T.SameAs(before.Geo.T) && _st.Geo.CropState == before.Geo.CropState && _st.Geo.Frame.Equals(before.Geo.Frame) && _st.Decided == before.Decided) return;
            Push(before, label, HistoryEntryKind.Geometry);
        }
        Changed_(EditChangeKind.Transform, null, committed: true, geometry: true, cause: cause, step: label);
    }

    public void RotateLeft() => Geometry("rotate left", GeometryChangeCause.Rotate, g => g.RotateLeft());
    public void RotateRight() => Geometry("rotate right", GeometryChangeCause.Rotate, g => g.RotateRight());
    public void FlipHorizontal() => Geometry("flip horizontal", GeometryChangeCause.FlipHorizontal, g => g.MirrorHorizontal());
    public void FlipVertical() => Geometry("flip vertical", GeometryChangeCause.FlipVertical, g => g.MirrorVertical());

    public LevelSuggestion LevelSuggestion => _level;

    /// <summary>Set the level rotation to the suggestion. The angle goes through the platform-independent sine and cosine, so the saved
    /// matrix has the same bits on every platform.</summary>
    public void Level()
    {
        if (!_level.Available) return;
        double radians = (double)_level.Degrees * (3.14159265358979323846 / 180.0);
        float c = (float)Lux.Engine.Pipeline.WineCrt.Cos(radians), s = (float)Lux.Engine.Pipeline.WineCrt.Sin(radians);
        Geometry("level", GeometryChangeCause.Rotate, g => g.Level(c, s));
    }

    /// <summary>Crop, rotation and flips back to none; the geometry is no longer decided (the held display turn returns).</summary>
    public void ResetGeometry()
    {
        CheckWritable();
        lock (_lock)
        {
            _cropBefore = null;
            var before = _st.Clone();
            _st.Geo.ResetGeometry(); _st.Decided = false;
            if (_st.Geo.T.SameAs(before.Geo.T) && _st.Decided == before.Decided && before.Geo.CropState == 0) return;
            Push(before, "reset crop and rotation", HistoryEntryKind.Geometry);
        }
        Changed_(EditChangeKind.Transform, null, committed: true, geometry: true, cause: GeometryChangeCause.Reset, step: "reset crop and rotation");
    }

    /// <summary>The held orientation as a real rotation (Lumen's rotate right / left by the held angle), whatever the display setting.</summary>
    public void RotateAsHeld()
    {
        if (!CanRotateAsHeld) return;
        int held = HeldDegrees(HeldOrientation);
        Geometry("rotate as held", GeometryChangeCause.Rotate, g => { var tmp = new State { Geo = g }; Turn(tmp, held); }, bake: false);
    }

    public CropState CropState { get { lock (_lock) return (CropState)_st.Geo.CropState; } }
    public CropAspect CropAspect { get { lock (_lock) return (CropAspect)_st.Geo.CropMode; } }

    public NormRect CropFrame
    {
        get
        {
            lock (_lock)
            {
                var s = LumenGeometryEdits.ToShown(_st.Geo.T, _st.Geo.Frame);
                return new NormRect(s.X, s.Y, s.W, s.H);
            }
        }
    }

    public void EnterCrop()
    {
        CheckWritable();
        lock (_lock)
        {
            if (_st.Geo.CropState != 0) return;
            // v2.1: the crop tool works in the picture as shown — the held display turn is baked provisionally (cancel restores the state)
            _cropBefore = _st.Clone();
            Bake(_st);
            _st.Geo.SetCropState(1);
        }
        Changed_(EditChangeKind.CropFrame, null, committed: false, geometry: true, save: false, cause: GeometryChangeCause.CropTool);
    }

    public void SetCropAspect(CropAspect aspect)
    {
        CheckWritable();
        lock (_lock) _st.Geo.SetCropMode((int)aspect);
        RaiseFrame();
    }

    public void SetCropFrame(NormRect frame, bool dragging = false)
    {
        CheckWritable();
        lock (_lock)
        {
            double x = Math.Clamp(frame.X, 0, 1), y = Math.Clamp(frame.Y, 0, 1);
            double w = Math.Clamp(frame.Width, 1e-4, 1 - x), h = Math.Clamp(frame.Height, 1e-4, 1 - y);
            _st.Geo.Frame = LumenGeometryEdits.FromShown(_st.Geo.T, new QRect(x, y, w, h));
            if (_st.Geo.CropState != 0) _st.Geo.CropState = dragging ? 2 : 1;
        }
        RaiseFrame();
    }

    void RaiseFrame()
    {
        EditValues st; lock (_lock) st = Snapshot(_st);
        Raise(new EditChangedEventArgs { Kind = EditChangeKind.CropFrame, State = st });
    }

    public NormRect FitCropFrame(NormRect frame)
    {
        lock (_lock)
        {
            var g = _st.Geo.Clone(); g.Frame = LumenGeometryEdits.FromShown(g.T, new QRect(frame.X, frame.Y, frame.Width, frame.Height));
            g.SetCropMode(g.CropMode);
            var s = LumenGeometryEdits.ToShown(g.T, g.Frame);
            return new NormRect(s.X, s.Y, s.W, s.H);
        }
    }

    public void ApplyCrop()
    {
        CheckWritable();
        lock (_lock)
        {
            if (_st.Geo.CropState == 0) return;
            State before;
            if (_cropBefore is not null) before = _cropBefore;
            else { before = _st.Clone(); before.Geo.SetCropState(0); }
            _cropBefore = null;
            _st.Geo.ApplyCrop(true);
            _st.Geo.FitFrame();   // a leveled picture: no empty corners in the crop
            _st.Decided = true;
            Push(before, "crop", HistoryEntryKind.Geometry);
        }
        Changed_(EditChangeKind.Transform, null, committed: true, geometry: true, cause: GeometryChangeCause.Crop, step: "crop");
    }

    public void CancelCrop()
    {
        CheckWritable();
        lock (_lock)
        {
            if (_st.Geo.CropState == 0) return;
            if (_cropBefore is not null) { _st = _cropBefore; _cropBefore = null; } else _st.Geo.SetCropState(0);
        }
        Changed_(EditChangeKind.CropFrame, null, committed: false, geometry: true, save: false, cause: GeometryChangeCause.CropTool);
    }

    // ------------------------------------------------------------------------------------------------ history

    public bool CanUndo { get { lock (_lock) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_lock) return _redo.Count > 0; } }
    public string? UndoLabel { get { lock (_lock) return _undo.Count == 0 ? null : "Undo " + _undo[^1].Label; } }
    public string? RedoLabel { get { lock (_lock) return _redo.Count == 0 ? null : "Redo " + _redo[^1].Label; } }

    public void Undo() => Step(_undo, _redo, GeometryChangeCause.Undo, "undo ");
    public void Redo() => Step(_redo, _undo, GeometryChangeCause.Redo, "redo ");

    void Step(List<UStep> from, List<UStep> to, GeometryChangeCause cause, string verb)
    {
        CheckWritable();
        bool geo; string label;
        lock (_lock)
        {
            if (from.Count == 0) return;
            if (_st.Geo.CropState != 0 && _cropBefore is not null) _st = _cropBefore;   // an open crop tool is left first
            _cropBefore = null;
            var step = from[^1]; from.RemoveAt(from.Count - 1);
            var st = step.Before; label = step.Label;
            to.Add(step with { Before = _st.Clone() });
            geo = !st.Geo.T.SameAs(_st.Geo.T) || st.Geo.CropState != _st.Geo.CropState || st.Decided != _st.Decided;
            _st = st.Clone();
        }
        Changed_(EditChangeKind.History, null, committed: true, geometry: geo, cause: cause, step: verb + label);
    }

    public IReadOnlyList<EditHistoryStep> History
    {
        get
        {
            lock (_lock)
            {
                var list = new List<EditHistoryStep>();
                foreach (var u in _undo) list.Add(new EditHistoryStep(list.Count, Cap(u.Label), true));
                for (int i = _redo.Count - 1; i >= 0; i--) list.Add(new EditHistoryStep(list.Count, Cap(_redo[i].Label), false));
                return list;
            }
        }
    }

    public int HistoryPosition { get { lock (_lock) return _undo.Count; } }

    public void GoToHistory(int position)
    {
        int n; lock (_lock) n = _undo.Count + _redo.Count;
        if (position < 0 || position > n) throw new ArgumentOutOfRangeException(nameof(position));
        while (HistoryPosition > position) Undo();
        while (HistoryPosition < position) Redo();
    }

    // ------------------------------------------------------------------------------------------------ persistence

    public bool IsDirty { get { lock (_saveLock) return _dirty; } }
    public SaveState SaveState { get { lock (_saveLock) return _saveState; } }
    public string? SaveError { get { lock (_saveLock) return _saveError; } }
    public ContextLocation SavedTo { get { lock (_saveLock) return _savedTo; } }

    void ScheduleSave()
    {
        if (_s.Options.ReadOnly) return;
        lock (_saveLock)
        {
            _dirty = true; _saveState = SaveState.Pending;
            _saveTimer ??= new Timer(_ => { try { FlushSave(); } catch (Exception e) { _s.Lib.Trace("save: " + e.Message); } });
            _saveTimer.Change(Math.Max(0, _s.Settings.SaveDelayMs), Timeout.Infinite);
        }
    }

    public Task SaveAsync(CancellationToken cancel = default) => Task.Run(FlushSave, cancel);

    /// <summary>The recipe of the current state: each part only when it differs from what renders without one.</summary>
    internal CaptureRecipe? RecipeOf(State st)
    {
        var adj = st.Adj == _base.Adj ? null : st.Adj;
        var tr = st.Geo.T.SameAs(_base.Geo.T) ? null : st.Geo.T.ToExport();
        float? fn = st.FNumber == _base.FNumber ? null : st.FNumber;
        float? fo = st.Focus == _base.Focus ? null : st.Focus;
        bool? decided = st.Decided == _base.Decided ? null : st.Decided;
        var r = new CaptureRecipe { Adjustments = adj, Transform = tr, FNumber = fn, FocusDepth = fo, GeometryDecided = decided };
        return r.IsEmpty ? null : r;
    }

    readonly object _flushLock = new();

    /// <summary>Write the current state now (on the caller's thread). Failures are recorded (<see cref="SaveError"/>), not thrown.</summary>
    internal void FlushSave()
    {
        if (_s.Options.ReadOnly) return;
        lock (_flushLock)
        {
            State st;
            lock (_saveLock) { if (!_dirty) return; _dirty = false; }
            lock (_lock) st = _st.Clone();
            try
            {
                var place = RecipeIo.Write(_s.Lib, _s.CaptureId, _s.Path, RecipeOf(st));
                _s.Engine.ReloadSavedState();
                lock (_saveLock) { _saveError = null; if (!_dirty) _saveState = SaveState.Saved; if (place != ContextLocation.None) _savedTo = place; }
                EditValues v; lock (_lock) v = Snapshot(_st);
                Raise(new EditChangedEventArgs { Kind = EditChangeKind.Saved, State = v });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                lock (_saveLock) { _dirty = true; _saveState = SaveState.Failed; _saveError = "The edits could not be saved: " + LibraryCore.Plain(e); }
                _s.Lib.Trace($"edits of {_s.CaptureId[..8]} not saved: {e.Message}");
            }
        }
    }

    /// <summary>Write the current state now even when nothing changed since the last save (after a batch took a change into this session).</summary>
    internal void FlushSaveForced()
    {
        if (_s.Options.ReadOnly) return;
        lock (_saveLock) { _dirty = true; _saveState = SaveState.Pending; }
        FlushSave();
    }

    /// <summary>A paste / preset / reset batch wrote this photo's recipe: the session takes the new state as one undo step (not saved again).</summary>
    internal void TakeExternal(EditValues v, EditGroups g)
    {
        bool geo;
        lock (_lock)
        {
            var (next, _) = ApplyTo(_st, v, g, DisplayRotationFor(_st));
            geo = !next.Geo.T.SameAs(_st.Geo.T) || next.Decided != _st.Decided;
            Push(_st.Clone(), "paste", HistoryEntryKind.Several);
            _st = next; _cropBefore = null;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: geo, save: false, cause: GeometryChangeCause.Paste, step: "paste");
    }

    /// <summary>Take a whole state from outside (a library batch undone while the photo is open): one undo step here, not saved again.</summary>
    internal void TakeState(State target, string label)
    {
        bool geo;
        lock (_lock)
        {
            var next = target.Clone();
            geo = !next.Geo.T.SameAs(_st.Geo.T) || next.Decided != _st.Decided;
            Push(_st.Clone(), label, HistoryEntryKind.Several);
            _st = next; _cropBefore = null;
        }
        Changed_(EditChangeKind.Adjustment, null, committed: true, geometry: geo, save: false, cause: GeometryChangeCause.Paste, step: label);
    }

    /// <summary>The display-only turn in effect for this photo now.</summary>
    internal int DisplayTurnNow { get { lock (_lock) return DisplayRotationFor(_st); } }

    internal State CurrentState { get { lock (_lock) return _st.Clone(); } }
    internal State BaseState => _base.Clone();

    // ------------------------------------------------------------------------------------------------ histogram

    public Histogram? Histogram { get { lock (_lock) return _histogram; } }

    internal void SetHistogram(Histogram h)
    {
        EditValues v;
        lock (_lock) { if (_histogram is not null && _histogram.Version > h.Version) return; _histogram = h; v = Snapshot(_st); }
        Raise(new EditChangedEventArgs { Kind = EditChangeKind.Histogram, State = v });
    }

    /// <summary>Lumen's histogram (`ParamFloatArray 3`): per pixel one count in R[r], G[g], B[b] and the luminance bin
    /// ⌊(r + g + b) / 3⌋ (the `· 0xaaaaaaab >> 32` division), over a whole picture (RGBA8).</summary>
    internal static (int[] R, int[] G, int[] B, int[] Y) Count(byte[] rgba)
    {
        var r = new int[256]; var g = new int[256]; var b = new int[256]; var y = new int[256];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int R = rgba[i], G = rgba[i + 1], B = rgba[i + 2];
            r[R]++; g[G]++; b[B]++; y[(R + G + B) / 3]++;
        }
        return (r, g, b, y);
    }
}

/// <summary>Writing a capture's recipe through the context resolver, and the library's mirror after it.</summary>
internal static class RecipeIo
{
    /// <summary>Store (or remove, with null) a capture's recipe in its context database — created when missing, next to the capture when its folder
    /// can be written, else in the local store — then refresh the library's mirror (summary, thumbnail key → the thumbnail is made again).
    /// Returns where the context lives.</summary>
    public static ContextLocation Write(LibraryCore lib, string id, string path, CaptureRecipe? recipe)
    {
        var ctx = ImageContext.ForKnownCapture(path, id, lib.Hashes, LocalContextStore.Current);
        if (recipe is null)
        {
            if (!ctx.HasEdits) return ContextLocation.None;
            using var s = DepthEditStore.Open(ctx.EditsDatabasePath, id);
            if (s.ReadLuxRecipe() is not null) s.WriteLuxRecipe(null);
        }
        else
        {
            using var s = DepthEditStore.OpenOrCreate(ctx);
            s.WriteLuxRecipe(recipe);
        }
        var changed = lib.RefreshContexts(new[] { id }, force: true);
        lib.Changes.Changed(changed.Count > 0 ? changed : new List<string> { id });
        return ctx.Place switch { ContextPlace.Local => ContextLocation.LocalStore, ContextPlace.Beside => ContextLocation.BesideCapture, _ => ContextLocation.None };
    }
}
