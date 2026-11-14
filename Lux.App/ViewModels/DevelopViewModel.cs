using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Services;
using Lux.App.Viewer;
using Lux.Library;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.ViewModels;

/// <summary>The Develop page's view modes (the header).</summary>
public enum DevelopMode { Single, BeforeAfter, PhotoDepth }

/// <summary>How before / after compares.</summary>
public enum CompareStyle { Split, Toggle }

/// <summary>
/// The Develop page: one capture open in the viewer with its non-depth edits — header (name, size, lens, view modes), the quality selector,
/// histogram, info, the depth-effect placeholder, the ten tone and colour sliders, geometry (crop tool, aspect presets, rotation, flips,
/// rotate as held), revert, history with undo / redo, presets and copy / paste, the filmstrip with rating and next / previous, the export side
/// panel, and the status bar's zoom, quality on screen and save state. Opening, saving and everything heavy run off the UI thread; the
/// session's events are applied once per UI turn.
/// </summary>
public sealed partial class DevelopViewModel : ObservableObject, IAsyncDisposable
{
    readonly AppServices _s;
    readonly IShell _shell;
    readonly LibraryViewModel _library;
    readonly QueueViewModel _queue;
    CancellationTokenSource? _openCts;
    IViewerCommands? _viewer;
    int _changedPosted;
    GeometryState _shownGeometry = GeometryState.Identity;

    public DevelopViewModel(AppServices s, IShell shell, LibraryViewModel library, QueueViewModel queue)
    {
        _s = s;
        _shell = shell;
        _library = library;
        _queue = queue;
        Quality = new QualitySelectorViewModel(library.QualityChanges);
        Interaction = new InteractionHint(s.Library.Jobs);
        Depth = new DepthViewModel(shell, () => { if (IsCropping) ApplyCrop(); return Session != null; });
        Depth.Changed += () => { RefreshHistory(); SyncTools(); RaiseStatusPointer(); OnPropertyChanged(nameof(ShowDepthCard)); OnPropertyChanged(nameof(ShowDepthNotReady)); };
        Depth.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DepthViewModel.PointerText)) RaiseStatusPointer(); };
        Tools = new ObservableCollection<ToolViewModel>
        {
            new("Focus", "F", "Click the photo to focus there (F)", true, PageTool.Focus),
            new("Refine edge", "E", "Make the depth follow an edge of the photo: paint along it (E)", true, PageTool.RefineEdge),
            new("Blend area", "B", "Smooth the depth of a surface: paint over it (B)", true, PageTool.BlendArea),
            new("Match depth", "M", "Give an area one depth: brush, lasso or quick select, then Apply (M)", true, PageTool.MatchDepth),
            new("Quick select", "Q", "Match depth with quick select: paint to select along the photo's edges, then Apply (Q)", true, PageTool.MatchDepth),
            new("Crop", "C", "Crop, with aspect presets (C)", true, PageTool.Crop),
        };
        s.Develop.PresetsChanged += () => Dispatcher.UIThread.Post(RefreshPresets);
        s.Develop.ClipboardChanged += () => Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(CanPaste)); OnPropertyChanged(nameof(ClipboardText)); });
        queue.ActiveCapturesChanged += ids => { if (Item != null && ids.Contains(Item.Id)) Dispatcher.UIThread.Post(QualityMoved); };
        RefreshPresets();
        s.ViewerPrefs.Load(s.Library.Settings);
        // the render progress: re-read once a second while something is coming (notices a picture that stops moving)
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _renderTimer.Tick += (_, _) => RefreshRender();
        _renderTimer.Start();
    }

    // ============================================================================================================ render progress
    readonly DispatcherTimer _renderTimer;
    int _lastReady = -1; long _lastLevelKey = -1; double _lastJob = -1; DateTime _lastMove = DateTime.UtcNow;
    (string Text, double Fraction, bool Busy, bool Indeterminate, bool Stuck) _render = ("", 0, false, false, false);
    /// <summary>Seconds without a new tile or job progress before the indicator says what it waits for.</summary>
    public static double StuckAfterSeconds { get; set; } = 6;

    /// <summary>"Showing preview → 13 MP · 60 %", "Full detail on screen", "Waiting: Full detail is queued".</summary>
    public string RenderProgressText => _render.Text;
    public double RenderFraction => _render.Fraction;
    public bool IsRenderBusy => _render.Busy;
    public bool IsRenderIndeterminate => _render.Indeterminate;
    public bool IsRenderStuck => _render.Stuck;

    static string QName(ViewQuality q) => q == ViewQuality.Linear13 ? "13 MP" : q == ViewQuality.FullDetail ? "full detail" : "preview";
    static string Cap(string t) => t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
    static ViewQuality QualityOfLevel(int level) => level switch { 0 => ViewQuality.FullDetail, 1 => ViewQuality.Linear13, _ => ViewQuality.Preview };

    /// <summary>Work out what is on screen and what is coming (the viewer's tiles, the capture's quality job, the depth), and whether it moved.</summary>
    public void RefreshRender()
    {
        var r = ComputeRender();
        if (r == _render) return;
        _render = r;
        foreach (var n in new[] { nameof(RenderProgressText), nameof(RenderFraction), nameof(IsRenderBusy), nameof(IsRenderIndeterminate), nameof(IsRenderStuck), nameof(StatusQuality) })
            OnPropertyChanged(n);
        ViewInfoChanged?.Invoke();
        Quality.OnScreen = OnScreenQuality is { } q ? $"Showing {QName(q)}" : null;
    }

    (string, double, bool, bool, bool) ComputeRender()
    {
        if (Session == null || Item == null || _viewer == null) return ("", 0, false, false, false);
        var now = DateTime.UtcNow;
        string showing = OnScreenQuality is { } on ? $"Showing {QName(on)}" : "Nothing on screen yet";
        bool Stale(double secs) => (now - _lastMove).TotalSeconds >= secs;
        // the quality being made by a job (the viewer shows the blurred picture meanwhile)
        if (Item.Quality is { Target: { } t } st && t > st.Cached)
        {
            if (st.IsRunning)
            {
                if (Math.Abs(st.Progress - _lastJob) > 1e-6) { _lastJob = st.Progress; _lastMove = now; }
                return ($"{showing} → {QName(t)} · being made {Text.Percent(st.Progress)}", st.Progress, true, false, false);
            }
            _lastMove = now;
            return ($"Waiting: {QName(t)} is queued (see the Queue)", 0, true, true, false);
        }
        // the depth the picture needs (the depth effect) being made or read
        if (Depth.IsAnalysing)
        {
            if (Math.Abs(Depth.AnalysisProgress - _lastJob) > 1e-6) { _lastJob = Depth.AnalysisProgress; _lastMove = now; }
            return ($"Waiting: {Depth.ReadinessText.TrimEnd('.')}", Depth.AnalysisProgress, true, Depth.AnalysisProgress <= 0, false);
        }
        // the viewer's tiles of the level the zoom asks for
        int total = _viewer.TargetSlots, ready = _viewer.TargetSlotsReady;
        var target = QualityOfLevel(_viewer.TargetLevel);
        long key = _viewer.TargetLevel * 100_000L + total;
        if (ready != _lastReady || key != _lastLevelKey) { _lastReady = ready; _lastLevelKey = key; _lastMove = now; }
        if (_viewer.TargetComplete)
        {
            var kept = Item.Item.BestQuality;
            return (target < kept ? $"{Cap(QName(target))} on screen · {QName(kept)} on zoom" : $"{Cap(QName(target))} on screen", 1, false, false, false);
        }
        if (Session.Status.Error is { Length: > 0 } err) return (err, 0, false, false, true);
        double f = total > 0 ? (double)ready / total : 0;
        bool sameLevel = OnScreenQuality == target;
        string moving = sameLevel ? $"Making {QName(target)} · {Text.Percent(f)}" : $"{showing} → {QName(target)} · {Text.Percent(f)}";
        if (!Stale(StuckAfterSeconds)) return (moving, f, true, total == 0, false);
        string why = Session.Status.Rendering
            ? $"still making {QName(target)} (nothing new for {(int)(now - _lastMove).TotalSeconds} s: the first close-up of a photo takes longest, and other work in the Queue shares the computer)"
            : $"no {QName(target)} tiles are being made — zoom or move the picture to ask again";
        return ($"Waiting for {QName(target)} · {Text.Percent(f)}: {why}", f, true, false, true);
    }

    public AppServices Services => _s;
    /// <summary>The depth effect, the depth tools, versions and the pointer read-out.</summary>
    public DepthViewModel Depth { get; }
    public LibraryViewModel Library => _library;
    public QualitySelectorViewModel Quality { get; }
    public InteractionHint Interaction { get; }
    public ObservableCollection<ToolViewModel> Tools { get; }
    public ObservableCollection<AdjustmentRowViewModel> Adjustments { get; } = new();
    public ObservableCollection<HistoryRowViewModel> History { get; } = new();
    public ObservableCollection<PresetViewModel> Presets { get; } = new();
    public ObservableCollection<InfoRow> Info { get; } = new();

    // ============================================================================================================ the open capture

    [ObservableProperty] CaptureItemViewModel? _item;
    [ObservableProperty] IDevelopSession? _session;
    [ObservableProperty] bool _isOpening;
    [ObservableProperty] string? _openError;
    [ObservableProperty] Bitmap? _standIn;

    public bool HasItem => Item != null;
    public bool HasSession => Session != null;
    public bool IsEditable => Session != null && !IsCropping;
    public string EmptyText => _s.Develop.IsAvailable
        ? "Choose a capture in the Library and open it here: double-click it, press Enter, or use Open in Develop."
        : _s.Develop.UnavailableReason ?? "Editing is not available.";

    /// <summary>"L16_00493 · 8320 × 6240 · 28 mm" (the size as shown: after the crop and turn).</summary>
    public string HeaderText
    {
        get
        {
            if (Item == null) return "";
            var parts = new List<string> { Item.Name };
            if (Session?.Tiles is { } t) { var sz = t.LevelSize(0); parts.Add($"{sz.Width} × {sz.Height}"); }
            else if (Item.Item.Width > 0) parts.Add($"{Item.Item.Width} × {Item.Item.Height}");
            parts.Add(Text.Lens(Item.Item.FocalLengthMm));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Open a capture (from the library, the filmstrip, next / previous). The previous one is closed (its edits saved) off the UI
    /// thread; the thumbnail shows at once while the picture is made.</summary>
    public async Task OpenAsync(CaptureItemViewModel item)
    {
        if (Item?.Id == item.Id && (Session != null || IsOpening)) return;
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        CloseSession();
        Item = item;
        OpenError = null;
        IsOpening = true;
        Quality.Item = item;
        StandIn = _s.Thumbnails.TryGet(item.Id);
        if (StandIn == null) _ = LoadStandInAsync(item, cts.Token);
        RaiseAll();
        IDevelopSession session;
        try
        {
            var it = item.Item;
            session = await Task.Run(() => _s.Develop.OpenAsync(it, cts.Token), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is LibraryException or NotSupportedException or InvalidOperationException or IOException)
        {
            if (cts.IsCancellationRequested) return;
            IsOpening = false;
            OpenError = e.Message;
            RaiseAll();
            return;
        }
        if (cts.IsCancellationRequested) { var dead = session; _ = Task.Run(dead.Dispose); return; }
        IsOpening = false;
        Session = session;
        session.Changed += OnSessionChanged;
        Adjustments.Clear();
        foreach (var spec in session.Specs) Adjustments.Add(new AdjustmentRowViewModel(spec, () => Session));
        _shownGeometry = session.Geometry;
        Depth.Attach(session.Depth);
        Refresh();
    }

    async Task LoadStandInAsync(CaptureItemViewModel item, CancellationToken cancel)
    {
        var bmp = await _s.Thumbnails.RequestAsync(item.Id, cancel).ConfigureAwait(true);
        if (!cancel.IsCancellationRequested && Item == item && bmp != null) StandIn = bmp;
    }

    void CloseSession()
    {
        if (Session is not { } old) return;
        old.Changed -= OnSessionChanged;
        Depth.Attach(null);
        Session = null;
        ExportPanel = null;
        AnimatePanel = null;
        Adjustments.Clear();
        History.Clear();
        Info.Clear();
        _ = Task.Run(old.Dispose);   // saves a pending change first
    }

    public async ValueTask DisposeAsync()
    {
        _openCts?.Cancel();
        if (Session is { } s) { s.Changed -= OnSessionChanged; Session = null; await Task.Run(s.Dispose).ConfigureAwait(false); }
    }

    void OnSessionChanged()
    {
        if (Interlocked.Exchange(ref _changedPosted, 1) == 0)
            Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _changedPosted, 0); Refresh(); }, DispatcherPriority.Normal);
    }

    /// <summary>Bring every bound value up to the session's state (UI thread).</summary>
    /// <summary>Time spent in <see cref="Refresh"/> (measurements).</summary>
    public static long RefreshTicks;
    public static int RefreshCount;

    public void Refresh()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RefreshCore(); }
        finally { RefreshTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; RefreshCount++; }
    }

    void RefreshCore()
    {
        if (Session is { } s)
        {
            foreach (var row in Adjustments) row.Refresh(s.Get(row.Key));
            RefreshHistory();
            var info = s.Info;
            if (Info.Count != info.Count || Info.Where((r, i) => r.Label != info[i].Label || r.Value != info[i].Value).Any())
            {
                Info.Clear();
                foreach (var (l, v) in info) Info.Add(new InfoRow(l, v));
            }
            Histogram = s.Histogram;
            _shownGeometry = s.Geometry;
            if (s.IsCropping) SyncCropFrame?.Invoke(s.CropFrame);
        }
        Depth.Refresh();
        SyncTools();
        RaiseAll();
    }

    string _historySignature = "";

    /// <summary>The combined history (tone, geometry, depth): depth rows marked, rows that cannot be reached now dimmed with the reason.</summary>
    void RefreshHistory()
    {
        if (Session is not { } s) return;
        var items = s.HistoryItems;
        int pos = s.HistoryPosition;
        var status = s.Depth?.HistoryStatus ?? HistoryStatus.Ready;
        var reach = new bool[items.Count];
        for (int i = 0; i < items.Count; i++) reach[i] = s.CanGoTo(i);
        string first = s.FirstRowLabel; string? firstTip = s.FirstRowTip;
        var sig = string.Join("|", items.Select((h, i) => $"{h.Label}/{h.Kind}/{h.Saved}/{h.Undoable}/{reach[i]}")) + $"#{pos}#{status.State}#{first}#{firstTip}";
        if (sig == _historySignature) return;
        _historySignature = sig;
        History.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            string? why = reach[i] || i == pos ? null
                : status.State == HistoryState.Rebuilding ? "This step is still being rebuilt — it can be revisited in a moment"
                : status.State == HistoryState.Unreliable && items.Skip(i + 1).Take(Math.Max(0, pos - i)).Any(x => x.Saved) ? "Earlier saved depth steps can't be undone one by one (see the note below)"
                : "Steps before a depth reset (or more than 20 depth edits back) can't be undone";
            // the first row named for what it is: "Opened · original" / "Opened · with saved edits", its edits in the tip
            if (i == 0) why = why is null ? firstTip : $"{firstTip} · {why}";
            History.Add(new HistoryRowViewModel(i, i == 0 ? first : items[i].Label, i == pos, i > pos, items[i], reach[i] || i == pos, why));
        }
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo)); OnPropertyChanged(nameof(UndoTip)); OnPropertyChanged(nameof(RedoTip));
        OnPropertyChanged(nameof(CanRevert)); OnPropertyChanged(nameof(CanClearHistory));
    }

    /// <summary>The tools list follows the page: the active tool ringed, depth tools dimmed while the depth map is not ready.</summary>
    void SyncTools()
    {
        foreach (var t in Tools)
        {
            t.IsActive = t.Kind switch
            {
                PageTool.Crop => IsCropping,
                PageTool.MatchDepth => Depth.Tool == PageTool.MatchDepth && (t.IsQuickSelect == Depth.IsQuickSelect),
                _ => t.Kind == Depth.Tool && Depth.Tool != PageTool.None,
            };
            t.IsDimmed = t.Kind is not PageTool.Crop and not PageTool.None && Session != null && !Depth.IsReady;
        }
    }

    void RaiseStatusPointer() => StatusPointerChanged?.Invoke();

    /// <summary>The pointer read-out changed (the shell's status bar shows it).</summary>
    public event Action? StatusPointerChanged;
    public string StatusPointer => Item == null ? "" : Depth.PointerText;

    /// <summary>The Depth effect section's card: the depth map is not ready (with "Analyse now").</summary>
    public bool ShowDepthCard => Session != null && Depth.HasDepth && !Depth.IsReady && !Depth.HasOptionBar;
    /// <summary>The card over the picture: a depth tool is chosen but the depth map is not ready.</summary>
    public bool ShowDepthNotReady => Session != null && Depth.HasOptionBar && !Depth.IsReady;

    /// <summary>The page shows the crop frame: the view moves its overlay to this rectangle.</summary>
    public event Action<NormRect>? SyncCropFrame;

    void RaiseAll()
    {
        foreach (var p in new[]
                 {
                     nameof(HasItem), nameof(HasSession), nameof(IsEditable), nameof(HeaderText), nameof(CanUndo), nameof(CanRedo), nameof(UndoTip), nameof(RedoTip),
                     nameof(IsCropping), nameof(CropAspectChoice), nameof(CanRotateAsHeld), nameof(RotateAsHeldLabel), nameof(RotateAsHeldTip), nameof(GeometryText),
                     nameof(CanRevert), nameof(SaveText), nameof(StatusSaved), nameof(StatusQuality), nameof(StatusZoom), nameof(IsProcessing), nameof(ProcessingProgress),
                     nameof(ViewerState), nameof(OnScreenText), nameof(OnScreenQuality), nameof(IsPreviewOnScreen), nameof(Is13OnScreen), nameof(IsFullOnScreen),
                     nameof(CanPaste), nameof(ClipboardText), nameof(RatingStars), nameof(Rating), nameof(CanPrevious), nameof(CanNext), nameof(ShowEmpty),
                     nameof(PositionText), nameof(StandInTurn), nameof(CompareMode), nameof(IsSplit), nameof(IsToggle), nameof(BeforeAfterLabel),
                 })
            OnPropertyChanged(p);
    }

    /// <summary>The capture's job moved (a quality being made, finished): the selector, the processing state, the picture's finest level.</summary>
    void QualityMoved()
    {
        Quality.Refresh();
        RaiseAll();
        RefreshRender();
    }

    public bool ShowEmpty => Item == null;

    // ============================================================================================================ viewer link

    /// <summary>The view hands over its viewer (zoom commands, zoom read-outs).</summary>
    public void AttachViewer(IViewerCommands viewer)
    {
        if (_viewer != null) _viewer.ViewChanged -= OnViewChanged;
        _viewer = viewer;
        viewer.ViewChanged += OnViewChanged;
    }

    public IViewerCommands? Viewer => _viewer;

    void OnViewChanged()
    {
        RefreshRender();
        OnPropertyChanged(nameof(StatusZoom)); OnPropertyChanged(nameof(ZoomPercent)); OnPropertyChanged(nameof(ZoomSlider));
        OnPropertyChanged(nameof(OnScreenText)); OnPropertyChanged(nameof(OnScreenQuality)); OnPropertyChanged(nameof(StatusQuality));
        OnPropertyChanged(nameof(IsPreviewOnScreen)); OnPropertyChanged(nameof(Is13OnScreen)); OnPropertyChanged(nameof(IsFullOnScreen)); OnPropertyChanged(nameof(IsFitted));
        ViewInfoChanged?.Invoke();
    }

    /// <summary>The zoom or quality read-outs changed (the shell's status bar follows).</summary>
    public event Action? ViewInfoChanged;

    public double ZoomPercent => _viewer?.ZoomPercent ?? 100;
    public bool IsFitted => _viewer?.IsFitted ?? true;
    public string StatusZoom => Session == null ? "" : $"Zoom {Math.Round(ZoomPercent):0} %" + (IsFitted ? " · fitted" : "");

    /// <summary>The zoom slider, 0–100 on a log scale between the fitted view and 800 %.</summary>
    public double ZoomSlider
    {
        get
        {
            if (_viewer == null) return 0;
            var (min, max) = _viewer.PercentRange;
            return max <= min ? 0 : (Math.Log(ZoomPercent) - Math.Log(min)) / (Math.Log(max) - Math.Log(min)) * 100;
        }
        set
        {
            if (_viewer == null) return;
            var (min, max) = _viewer.PercentRange;
            double pct = Math.Exp(Math.Log(min) + Math.Clamp(value, 0, 100) / 100 * (Math.Log(max) - Math.Log(min)));
            if (Math.Abs(pct - ZoomPercent) / ZoomPercent < 1e-3) return;
            _viewer.ZoomToPercent(pct, animate: false);
        }
    }

    [RelayCommand] void ZoomFit() => _viewer?.ZoomToFit();
    [RelayCommand] void ZoomFill() => _viewer?.ZoomToFill();
    [RelayCommand] void ZoomActual() => _viewer?.ZoomToActual();
    [RelayCommand] void ZoomIn() => _viewer?.ZoomBy(1.25);
    [RelayCommand] void ZoomOut() => _viewer?.ZoomBy(0.8);
    [RelayCommand] void ToggleZoom() => _viewer?.ToggleFitActual();
    [RelayCommand] void CenterPicture() => _viewer?.CenterImage();

    /// <summary>The quality of what the viewer drew last: level 0 = full detail, 1 = the 13 MP render, 2–4 = the preview levels.</summary>
    public ViewQuality? OnScreenQuality => _viewer?.LevelOnScreen switch
    {
        null or < 0 => null,
        0 => ViewQuality.FullDetail,
        1 => ViewQuality.Linear13,
        _ => ViewQuality.Preview,
    };
    public bool IsPreviewOnScreen => OnScreenQuality == ViewQuality.Preview;
    public bool Is13OnScreen => OnScreenQuality == ViewQuality.Linear13;
    public bool IsFullOnScreen => OnScreenQuality == ViewQuality.FullDetail;

    public string OnScreenText => OnScreenQuality is { } q ? $"On screen: {QualityRules.Name(q)}" : Session == null ? "" : "On screen: making the picture";

    /// <summary>Status bar: "Showing 13 MP · full detail on zoom", "Processing image · 42 %".</summary>
    public string StatusQuality
    {
        get
        {
            if (Item == null) return "";
            if (OpenError != null) return "This capture can't be opened";   // not "Making the picture"
            if (IsProcessing) return double.IsNaN(ProcessingProgress) ? "Processing image · waiting in the Queue" : $"Processing image · {Text.Percent(ProcessingProgress)}";
            if (_render.Text.Length > 0 && Session?.Status.Note is not { Length: > 0 }) return _render.Text;
            if (Session?.Status.Note is { Length: > 0 } note) return note;
            if (OnScreenQuality is not { } q) return IsOpening ? "Opening" : "Making the picture";
            var kept = Item.Item.BestQuality;
            static string N(ViewQuality v) => v == ViewQuality.Linear13 ? "13 MP" : QualityRules.Name(v).ToLowerInvariant();
            if (q < kept) return $"Showing {N(q)} · {N(kept)} on zoom";
            return $"Showing {N(q)}";
        }
    }

    // ============================================================================================================ quality and processing

    /// <summary>The selected quality is being made (a job of this capture is bringing it up): the viewer shows the blurred picture with the
    /// spinner and "Processing image".</summary>
    public bool IsProcessing => Item?.Quality is { Target: { } t } st && t > st.Cached || Session?.Status.Processing == true;
    public double ProcessingProgress => Item?.Quality is { IsRunning: true } st ? st.Progress : Session?.Status is { Processing: true } ps ? ps.Progress : double.NaN;
    public ImageState ViewerState => IsProcessing ? ImageState.Processing : ImageState.Ready;

    /// <summary>The thumbnail is made as the photo is shown; turned by the display rotation like the picture.</summary>
    public Turn StandInTurn => Turn.FromDegrees(Item?.Orientation ?? 0);

    // ============================================================================================================ view modes

    [ObservableProperty] DevelopMode _mode = DevelopMode.Single;
    [ObservableProperty] CompareStyle _compareStyle = CompareStyle.Split;
    [ObservableProperty] bool _showingBefore;
    [ObservableProperty] double _splitPosition = 0.5;

    partial void OnModeChanged(DevelopMode value) { if (value != DevelopMode.BeforeAfter) ShowingBefore = false; RaiseModes(); }
    partial void OnCompareStyleChanged(CompareStyle value) { ShowingBefore = false; RaiseModes(); }
    partial void OnShowingBeforeChanged(bool value) => RaiseModes();

    void RaiseModes()
    {
        OnPropertyChanged(nameof(IsSingle)); OnPropertyChanged(nameof(IsBeforeAfter)); OnPropertyChanged(nameof(CompareMode)); OnPropertyChanged(nameof(IsPhotoDepth));
        OnPropertyChanged(nameof(ShowDepthBeside)); OnPropertyChanged(nameof(MainShowsDepth));
        OnPropertyChanged(nameof(IsSplit)); OnPropertyChanged(nameof(IsToggle)); OnPropertyChanged(nameof(BeforeAfterLabel));
    }

    public bool IsSingle => Mode == DevelopMode.Single;
    public bool IsBeforeAfter => Mode == DevelopMode.BeforeAfter;
    public bool IsSplit => IsBeforeAfter && CompareStyle == CompareStyle.Split;
    public bool IsToggle => IsBeforeAfter && CompareStyle == CompareStyle.Toggle;
    public string BeforeAfterLabel => ShowingBefore ? "Showing before — click or press \\ to show after" : "Showing after — click or press \\ to show before";

    public CompareMode CompareMode => Mode != DevelopMode.BeforeAfter || IsCropping ? CompareMode.Off
        : CompareStyle == CompareStyle.Split ? CompareMode.Split
        : ShowingBefore ? CompareMode.ToggleBefore : CompareMode.ToggleAfter;

    [RelayCommand] void SetMode(DevelopMode mode) { if (mode == DevelopMode.PhotoDepth && IsCropping) CancelCrop(); Mode = mode; }

    /// <summary>"Photo + depth": the photo beside its depth map (pan and zoom locked together).</summary>
    public bool IsPhotoDepth => Mode == DevelopMode.PhotoDepth;
    /// <summary>The depth map alone, full size (tools still work on it).</summary>
    [ObservableProperty] bool _depthOnly;
    partial void OnDepthOnlyChanged(bool value) { OnPropertyChanged(nameof(ShowDepthBeside)); OnPropertyChanged(nameof(MainShowsDepth)); }
    /// <summary>The second viewer (the depth map) is shown beside the photo.</summary>
    public bool ShowDepthBeside => IsPhotoDepth && !DepthOnly;
    /// <summary>The main viewer shows the depth map instead of the photo.</summary>
    public bool MainShowsDepth => IsPhotoDepth && DepthOnly;
    [RelayCommand] void ToggleDepthOnly() { if (!IsPhotoDepth) Mode = DevelopMode.PhotoDepth; DepthOnly = !DepthOnly; }
    /// <summary>D: single ↔ photo + depth.</summary>
    [RelayCommand] void TogglePhotoDepth() => SetMode(IsPhotoDepth ? DevelopMode.Single : DevelopMode.PhotoDepth);
    [RelayCommand] void SetCompareStyle(CompareStyle style) => CompareStyle = style;
    [RelayCommand] void ToggleBefore() { if (Mode != DevelopMode.BeforeAfter) { Mode = DevelopMode.BeforeAfter; CompareStyle = CompareStyle.Toggle; ShowingBefore = true; } else if (CompareStyle == CompareStyle.Toggle) ShowingBefore = !ShowingBefore; else Mode = DevelopMode.Single; }

    /// <summary>\ : single ↔ before / after (in the toggle style it flips before ↔ after).</summary>
    public void BackslashKey()
    {
        if (Mode != DevelopMode.BeforeAfter) Mode = DevelopMode.BeforeAfter;
        else if (CompareStyle == CompareStyle.Toggle) ShowingBefore = !ShowingBefore;
        else Mode = DevelopMode.Single;
    }

    // ============================================================================================================ tone and colour

    [RelayCommand]
    void ResetTone()
    {
        if (Session is not { } s || !IsEditable) return;
        s.ResetToneAndColour();
        Refresh();
    }

    [ObservableProperty] HistogramData? _histogram;
    [ObservableProperty] bool _infoExpanded;
    [ObservableProperty] bool _histogramExpanded = true;
    [RelayCommand] void ToggleInfo() => InfoExpanded = !InfoExpanded;
    [RelayCommand] void ToggleHistogram() => HistogramExpanded = !HistogramExpanded;

    // ============================================================================================================ geometry

    public bool IsCropping => Session?.IsCropping == true;
    [ObservableProperty] bool _portraitAspects;
    partial void OnPortraitAspectsChanged(bool value) { OnPropertyChanged(nameof(AspectChoices)); OnPropertyChanged(nameof(IsPortraitAspects)); OnPropertyChanged(nameof(CropAspectChoice)); }
    public bool IsPortraitAspects => PortraitAspects;

    readonly IReadOnlyList<AspectChoice> LandscapeChoices = AspectPreset.Landscape.Select(p => new AspectChoice(p)).ToList();
    readonly IReadOnlyList<AspectChoice> PortraitChoices = AspectPreset.Portrait.Select(p => new AspectChoice(p)).ToList();
    public IReadOnlyList<AspectChoice> AspectChoices => PortraitAspects ? PortraitChoices : LandscapeChoices;

    /// <summary>The aspect preset shown in the panel and the crop bar.</summary>
    public AspectChoice? CropAspectChoice
    {
        get
        {
            var a = AspectPreset.Of(Session?.CropAspect ?? CropAspect.Free);
            if (a.IsPortrait != PortraitAspects && !a.IsFree && Math.Abs(a.Ratio - 1) > 1e-9) a = a.Swapped();
            var chosen = AspectChoices.FirstOrDefault(c => c.Preset.Aspect == a.Aspect) ?? AspectChoices[0];
            foreach (var c in LandscapeChoices.Concat(PortraitChoices)) c.IsSelected = ReferenceEquals(c, chosen);
            return chosen;
        }
        set
        {
            // the combo box writes back when its list changes: only a real choice counts
            if (value == null || Session is not { } s || value == CropAspectChoice) return;
            if (!s.IsCropping) EnterCropCore();
            s.SetCropAspect(value.Preset.Aspect);
            Refresh();
        }
    }

    [RelayCommand]
    void SwapAspect()
    {
        PortraitAspects = !PortraitAspects;
        if (Session is { IsCropping: true } s)
        {
            var a = AspectPreset.Of(s.CropAspect).Swapped();
            s.SetCropAspect(a.Aspect);
            Refresh();
        }
    }

    public bool CanRotateAsHeld => Session?.CanRotateAsHeld == true && Session?.ShownAsHeld != true && !IsCropping;

    /// <summary>Shown as held, the button saves the turn without moving the picture (exports then follow); otherwise it turns the picture.</summary>
    // Exports follow the view shown as held, so the button is offered only when the photo is not shown that way (the
    // setting off) — it then turns the picture; "Keep the turn for exports" is gone
    public string RotateAsHeldLabel => "Rotate as the camera was held";
    public string RotateAsHeldTip => "Turn the photo the way the camera was held (a rotation step, as Rotate right / left)";

    /// <summary>"Cropped · 3 : 2 · turned 90°" under the geometry buttons.</summary>
    public string GeometryText
    {
        get
        {
            if (Session is not { } s) return "";
            var g = s.Geometry;
            var p = new List<string>();
            if (g.HasCrop) p.Add(g.Aspect == CropAspect.Free ? "Cropped" : $"Cropped {AspectPreset.Of(g.Aspect).Label}");
            if (g.TurnDegrees % 360 != 0) p.Add($"turned {g.TurnDegrees}°");
            if (g.Mirrored) p.Add("flipped");
            return p.Count == 0 ? "Not cropped or turned" : string.Join(" · ", p);
        }
    }

    void Hint(Turn turn, NormRect? source = null, NormRect? target = null) => _viewer?.ExpectGeometryChange(turn, source, target);

    /// <summary>Run a geometry operation and tell the viewer how the old picture maps onto the new one (it holds the old one, turned, until
    /// the new one is made). Only turns and flips are mapped; a changed crop is not.</summary>
    void GeometryOp(Action<IDevelopSession> op, bool mapCrop = true)
    {
        if (Session is not { } s) return;
        var before = s.Geometry;
        var t0 = s.PictureTurn;
        op(s);
        var after = s.Geometry;
        if (mapCrop || before.HasCrop == after.HasCrop) Hint(t0.Inverse().Then(s.PictureTurn));
        Refresh();
    }

    // a photo shown as the camera was held: the library bakes the held turn into the first geometry edit (one step named for it)
    [RelayCommand] void RotateLeft() { if (!IsCropping) GeometryOp(s => s.RotateLeft()); }
    [RelayCommand] void RotateRight() { if (!IsCropping) GeometryOp(s => s.RotateRight()); }
    [RelayCommand] void FlipHorizontal() { if (!IsCropping) GeometryOp(s => s.FlipHorizontal()); }
    [RelayCommand] void FlipVertical() { if (!IsCropping) GeometryOp(s => s.FlipVertical()); }

    [RelayCommand]
    void RotateAsHeld()
    {
        if (Session is not { CanRotateAsHeld: true }) return;
        GeometryOp(s => s.RotateAsHeld());
    }

    [RelayCommand]
    void ResetGeometry()
    {
        if (Session is not { } s) return;
        if (s.IsCropping) s.CancelCrop();
        s.ResetGeometry();
        Refresh();
    }

    /// <summary>The crop tool button / C: open it (or apply when open, as Lumen's toggle).</summary>
    [RelayCommand]
    void Crop()
    {
        if (Session is not { } s) return;
        if (s.IsCropping) { ApplyCrop(); return; }
        EnterCropCore();
        Refresh();
    }

    void EnterCropCore()
    {
        if (Session is not { } s) return;
        var crop = s.Geometry.Crop;
        Hint(Turn.Identity, NormRect.Full, crop);
        PortraitAspects = AspectPreset.Of(s.Geometry.Aspect).IsPortrait;
        if (Mode != DevelopMode.Single) Mode = DevelopMode.Single;
        if (Depth.Tool != PageTool.None) Depth.LeaveToolCommand.Execute(null);
        s.EnterCrop();
    }

    [RelayCommand]
    public void ApplyCrop()
    {
        if (Session is not { IsCropping: true } s) return;
        Hint(Turn.Identity, s.CropFrame, NormRect.Full);
        s.ApplyCrop();
        Refresh();
    }

    [RelayCommand]
    public void CancelCrop()
    {
        if (Session is not { IsCropping: true } s) return;
        Hint(Turn.Identity, s.Geometry.Crop, NormRect.Full);
        s.CancelCrop();
        Refresh();
    }

    /// <summary>The crop bar's Reset: the frame back to the whole picture (still in the tool).</summary>
    [RelayCommand]
    void ResetCropFrame()
    {
        if (Session is not { IsCropping: true } s) return;
        s.SetCropAspect(CropAspect.Free);
        s.SetCropFrame(NormRect.Full, false);
        Refresh();
    }

    /// <summary>The overlay was dragged.</summary>
    public void CropFrameDragged(NormRect frame, bool dragging) => Session?.SetCropFrame(frame, dragging);

    // ============================================================================================================ revert, history

    /// <summary>Revert to original: whenever the photo is not the original or there are steps (it then always adds its own step).
    /// Depth edits are not part of it ("Reset depth edits" is separate and cannot be undone).</summary>
    public bool CanRevert => Session is { } s && (!s.IsOriginal || s.HistoryPosition > 0) && !IsCropping;

    [RelayCommand]
    void Revert()
    {
        if (Session is not { } s || !CanRevert) return;
        if (s.IsCropping) s.CancelCrop();
        s.RevertToOriginal();
        Refresh();
        _shell.Notify(Depth.HasDepthEdits
            ? "Back to the original tone, crop and depth effect (depth edits stay: Reset depth edits is separate) — undo brings your edits back (Ctrl+Z)"
            : "Back to the original — undo brings your edits back (Ctrl+Z)");
    }

    /// <summary>"Clear history": the picture stays as it is, the steps go (asks first: they can't be undone afterwards).</summary>
    public bool CanClearHistory => Session?.CanClearHistory == true && !IsCropping;

    [RelayCommand]
    void ClearHistory()
    {
        if (Session is not { } s || !CanClearHistory) return;
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel("Clear the history?",
            "The photo stays exactly as it is now; the steps leave the list and can no longer be undone or revisited. Saved edits are not changed.",
            "Clear history",
            () =>
            {
                _shell.CloseDialog(c!);
                s.ClearHistory();
                _historySignature = null;
                Refresh();
                _shell.Notify("History cleared — the photo is unchanged");
            },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    public bool CanUndo => Session?.CanUndo == true && !IsCropping;
    public bool CanRedo => Session?.CanRedo == true && !IsCropping;
    public string UndoTip => Session is { CanUndo: true } s ? $"Undo {s.History[s.HistoryPosition]} (Ctrl+Z)" : "Nothing to undo";
    public string RedoTip => Session is { CanRedo: true } s ? $"Redo {s.History[s.HistoryPosition + 1]} (Ctrl+Shift+Z)" : "Nothing to redo";

    [RelayCommand] void Undo() { if (CanUndo) GeometryOp(s => s.Undo(), mapCrop: false); }
    [RelayCommand] void Redo() { if (CanRedo) GeometryOp(s => s.Redo(), mapCrop: false); }
    [RelayCommand]
    void GoToHistory(HistoryRowViewModel? row)
    {
        if (row == null || IsCropping || Session is not { } s) return;
        if (!row.IsReachable) { _shell.Notify(row.Tip); return; }
        GeometryOp(x => x.GoTo(row.Index), mapCrop: false);
    }

    // ============================================================================================================ saving

    public string SaveText => Session?.Save switch
    {
        null => "",
        { Status: SaveStatus.Saving } => "Saving edits…",
        { Status: SaveStatus.Failed, Error: var e } => "Edits not saved: " + (e ?? "the folder cannot be written"),
        { Status: SaveStatus.Saved, Where: var w } => "Edits saved · " + (w ?? "with the photo").ToLowerInvariant(),
        _ => Session.IsOriginal && Session.Depth is not { HasDepthEdits: true } && Session.Depth?.FNumber == null ? "No edits" : "Edits saved",
    };
    public string StatusSaved => SaveText;

    [RelayCommand]
    async Task SaveNow()
    {
        if (Session is not { } s) return;
        await s.SaveNowAsync().ConfigureAwait(true);
        _shell.Notify("Edits are saved automatically — saved now");
        Refresh();
    }

    // ============================================================================================================ presets, copy / paste

    void RefreshPresets()
    {
        Presets.Clear();
        var specs = Session?.Specs ?? _s.Develop.DefaultSpecs;
        foreach (var p in _s.Develop.Presets) Presets.Add(new PresetViewModel(p, p.Set.Describe(specs)));
        OnPropertyChanged(nameof(HasPresets));
    }

    public bool HasPresets => Presets.Count > 0;
    public bool CanPaste => _s.Develop.Clipboard != null && Session != null;
    public string ClipboardText => _s.Develop.Clipboard is { } c ? $"Copied from {_s.Develop.ClipboardSource}: {c.Describe(Session?.Specs ?? _s.Develop.DefaultSpecs)}" : "Nothing copied yet";

    [RelayCommand]
    void ApplyPreset(PresetViewModel? p)
    {
        if (p == null || Session is not { } s || !IsEditable) return;
        bool cropOk = s.Apply(p.Preset.Set, $"Preset “{p.Name}”");
        Refresh();
        _shell.Notify(cropOk ? $"Preset “{p.Name}” applied" : $"Preset “{p.Name}” applied — its crop was skipped (another shape of photo)");
    }

    /// <summary>Copy adjustments… (Ctrl+C): choose the groups, then they go to the clipboard.</summary>
    [RelayCommand]
    void Copy()
    {
        if (Session is not { } s || Item is not { } item) return;
        var set = s.Snapshot();
        PatchDialogViewModel? d = null;
        d = new PatchDialogViewModel(PatchKind.Copy, "Copy adjustments", $"Choose what to copy from {item.Name}", "Copy", set, s.Specs, EditGroups.ToneAndColour,
            vm =>
            {
                _s.Develop.SetClipboard(vm.ChosenSet, item.Name, item.Id);
                _shell.CloseDialog(d!);
                _shell.Notify("Adjustments copied");
                return Task.FromResult<string?>(null);
            }, () => _shell.CloseDialog(d!), _s.Develop.SupportsPerSlider);
        _shell.ShowDialog(d);
    }

    /// <summary>Paste (Ctrl+V): every group of the clipboard into the open photo (one history step).</summary>
    [RelayCommand]
    void Paste()
    {
        if (Session is not { } s || _s.Develop.Clipboard is not { } c || !IsEditable) return;
        bool cropOk = s.Apply(c, "Paste adjustments");
        Refresh();
        _shell.Notify(cropOk ? "Adjustments pasted" : "Adjustments pasted — the crop was skipped (another shape of photo)");
    }

    /// <summary>Paste… (Ctrl+Shift+V): choose which of the copied groups to paste.</summary>
    [RelayCommand]
    void PasteChoose()
    {
        if (Session is not { } s || _s.Develop.Clipboard is not { } c || Item is not { } item) return;
        PatchDialogViewModel? d = null;
        d = new PatchDialogViewModel(PatchKind.Paste, "Paste adjustments", $"Choose what to paste into {item.Name} (copied from {_s.Develop.ClipboardSource})", "Paste",
            c, s.Specs, c.Groups,
            vm =>
            {
                bool cropOk = s.Apply(vm.ChosenSet, "Paste adjustments");
                _shell.CloseDialog(d!);
                Refresh();
                _shell.Notify(cropOk ? "Adjustments pasted" : "Adjustments pasted — the crop was skipped (another shape of photo)");
                return Task.FromResult<string?>(null);
            }, () => _shell.CloseDialog(d!), _s.Develop.SupportsPerSlider);
        _shell.ShowDialog(d);
    }

    [RelayCommand]
    void SavePreset()
    {
        if (Session is not { } s) return;
        var set = s.Snapshot();
        var svc = _s.Develop;
        PatchDialogViewModel? d = null;
        d = new PatchDialogViewModel(PatchKind.Preset, "Save as preset", "Choose what the preset keeps", "Save preset", set, s.Specs, EditGroups.ToneAndColour,
            async vm =>
            {
                try
                {
                    var name = vm.Name.Trim();
                    var chosen = vm.Chosen;
                    var chosenSet = vm.ChosenSet;
                    await Task.Run(() => svc.CreatePresetAsync(name, chosenSet, chosen, CancellationToken.None)).ConfigureAwait(true);
                    _shell.CloseDialog(d!);
                    _shell.Notify($"Preset “{name}” saved");
                    return null;
                }
                catch (LibraryException e) { return e.Message; }
            }, () => _shell.CloseDialog(d!), svc.SupportsPerSlider);
        _shell.ShowDialog(d);
    }

    [RelayCommand]
    void RenamePreset(PresetViewModel? p)
    {
        if (p == null) return;
        var svc = _s.Develop;
        PromptViewModel? d = null;
        d = new PromptViewModel("Rename preset", "Name", "Rename", async name =>
        {
            try
            {
                await Task.Run(() => svc.RenamePresetAsync(p.Preset.Id, name, CancellationToken.None)).ConfigureAwait(true);
                _shell.CloseDialog(d!);
                _shell.Notify($"Preset renamed to “{name}”");
                return null;
            }
            catch (LibraryException e) { return e.Message; }
        }, () => _shell.CloseDialog(d!)) { Value = p.Name };
        _shell.ShowDialog(d);
    }

    [RelayCommand]
    void DeletePreset(PresetViewModel? p)
    {
        if (p == null) return;
        var svc = _s.Develop;
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel($"Delete the preset “{p.Name}”?", "Photos it was applied to keep their adjustments.", "Delete preset",
            () => { _shell.CloseDialog(c!); _ = Task.Run(() => svc.DeletePresetAsync(p.Preset.Id, CancellationToken.None)); },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    // ============================================================================================================ filmstrip, rating, next / previous

    [ObservableProperty] bool _filmstripVisible = true;
    [RelayCommand] void ToggleFilmstrip() => FilmstripVisible = !FilmstripVisible;

    public IReadOnlyList<CaptureItemViewModel> Strip => _library.Items;
    int Index => Item == null ? -1 : _library.Items.ToList().FindIndex(i => i.Id == Item.Id);
    public bool CanPrevious => Index > 0;
    public bool CanNext => Index >= 0 && Index < _library.Items.Count - 1;
    public string PositionText => Index < 0 ? "" : $"{Index + 1} of {_library.Items.Count}";

    /// <summary>The library's result changed (a re-query): the filmstrip follows.</summary>
    public void StripChanged()
    {
        OnPropertyChanged(nameof(Strip)); OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(PositionText));
        if (Item != null && _library.Items.FirstOrDefault(i => i.Id == Item.Id) is { } fresh && !ReferenceEquals(fresh, Item)) { Item = fresh; Quality.Item = fresh; }
    }

    [RelayCommand] Task Previous() => Step(-1);
    [RelayCommand] Task Next() => Step(1);
    [RelayCommand] Task First() => GoToIndex(0);
    [RelayCommand] Task Last() => GoToIndex(_library.Items.Count - 1);

    Task Step(int delta) => Index < 0 ? Task.CompletedTask : GoToIndex(Index + delta);

    Task GoToIndex(int i)
    {
        if (i < 0 || i >= _library.Items.Count || IsCropping) return Task.CompletedTask;
        var item = _library.Items[i];
        _library.Click(item, false, false);
        return OpenAsync(item);
    }

    [RelayCommand]
    public Task OpenFromStrip(CaptureItemViewModel? item)
    {
        if (item == null || IsCropping) return Task.CompletedTask;
        _library.Click(item, false, false);
        return OpenAsync(item);
    }

    public int Rating => Item?.Rating ?? 0;
    public string RatingStars => Item == null ? "" : Text.StarsOutOfFive(Item.Rating);

    /// <summary>Keys 0–5 and the stars: rate the open capture (saved with the photo).</summary>
    [RelayCommand]
    public void Rate(string rating)
    {
        if (Item is not { } item || !int.TryParse(rating, out int r)) return;
        _library.RateCapture(item, Math.Clamp(r, 0, 5));
        OnPropertyChanged(nameof(Rating)); OnPropertyChanged(nameof(RatingStars));
    }

    // ============================================================================================================ export

    [ObservableProperty] ExportPanelViewModel? _exportPanel;
    public bool IsExportOpen => ExportPanel != null;
    partial void OnExportPanelChanged(ExportPanelViewModel? value) { OnPropertyChanged(nameof(IsExportOpen)); OnPropertyChanged(nameof(IsSidePanelOpen)); }

    // ---- the Animate side panel (its preview plays over the picture)
    [ObservableProperty] AnimatePanelViewModel? _animatePanel;
    public bool IsAnimateOpen => AnimatePanel != null;
    public bool IsSidePanelOpen => IsExportOpen || IsAnimateOpen;
    partial void OnAnimatePanelChanged(AnimatePanelViewModel? oldValue, AnimatePanelViewModel? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(IsAnimateOpen)); OnPropertyChanged(nameof(IsSidePanelOpen));
    }

    /// <summary>Animate… for the open photo: its edits are saved first so the preview and the job use them.</summary>
    [RelayCommand]
    public void OpenAnimate()
    {
        if (Item is not { } item || Session is not { } s) return;
        ExportPanel = null;
        AnimatePanel = new AnimatePanelViewModel(_s, new[] { item }, _queue, () => AnimatePanel = null, _shell.Notify, () => s.SaveNowAsync());
    }

    /// <summary>Export… for the open capture: the side panel, with the same settings the viewer renders (saved first).</summary>
    [RelayCommand]
    public void OpenExport()
    {
        if (Item is not { } item || Session is not { } s) return;
        AnimatePanel = null;
        ExportPanel = new ExportPanelViewModel(_s, new[] { item }, _queue, () => ExportPanel = null, _shell.Notify, async options =>
        {
            await s.SaveNowAsync().ConfigureAwait(false);
            return s.ExportSettings(options);
        }, OpenAnimate) { AskAboutExisting = (n, total) => ExportQuestions.AskAsync(_shell, n, total) };
    }

    // ============================================================================================================ tools

    [RelayCommand]
    void Tool(ToolViewModel? tool)
    {
        if (tool == null || Session == null) return;
        if (tool.Kind == PageTool.Crop) { Crop(); SyncTools(); return; }
        if (Session.Depth == null) { _shell.Notify("Depth editing is not available with this library yet"); return; }
        if (tool.IsQuickSelect) Depth.Activate(PageTool.MatchDepth, MatchMode.QuickSelect);
        else if (tool.Kind == PageTool.MatchDepth) Depth.Activate(PageTool.MatchDepth, Depth.IsQuickSelect ? MatchMode.Brush : null);
        else Depth.Activate(tool.Kind);
        SyncTools();
    }

    /// <summary>"Import this photo's Lumen edits…": the import dialog for the open capture (fidelity warning, tone-style option); the import runs
    /// through the open photo, so the "From Lumen" version appears and is shown at once.</summary>
    [RelayCommand]
    void ImportLumenEdits()
    {
        if (Item is not { } item || Session?.Depth is not { } d) return;
        LumenImportViewModel? vm = null;
        var folder = System.IO.Path.GetDirectoryName(item.Item.Path) ?? "";
        vm = new LumenImportViewModel(_s, item.Name, _ => Task.FromResult(new CaptureQuery { PathPrefix = folder }), new[] { item.Id },
            () => _shell.CloseDialog(vm!), _ => { }, _shell.Notify, singleCapture: true)
        {
            ImportOverride = async (ids, tone) =>
            {
                var r = await d.ImportLumenAsync(tone).ConfigureAwait(true);
                _shell.Notify(r.Changed ? $"Lumen's edits of {item.Name} imported — shown as “From Lumen”; your other versions are kept" : r.Message ?? "Nothing was imported");
                Refresh();
            },
        };
        _shell.ShowDialog(vm);
    }

    /// <summary>Back to the library (Esc when nothing else is open).</summary>
    [RelayCommand] void BackToLibrary() => _shell.ShowLibrary();
}
