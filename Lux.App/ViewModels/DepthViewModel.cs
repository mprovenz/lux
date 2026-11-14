using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.App.Viewer;
using Lux.Library;
using PendingArea = Lux.App.Develop.PendingArea;
using ToolOptions = Lux.App.Develop.ToolOptions;
using VersionKind = Lux.App.Develop.VersionKind;

namespace Lux.App.ViewModels;

/// <summary>The page's tools (one at a time, Lumen's exclusivity: focus, the three depth tools, crop).</summary>
public enum PageTool { None, Focus, RefineEdge, BlendArea, MatchDepth, Crop }

/// <summary>
/// The depth part of the Develop page: readiness and "Analyse now", the depth effect (aperture and focus sliders with typed values, the
/// focal-region shade, the in-focus read-out, the focus tool with its pin and entry hint), Lumen's depth tools (refine edge, blend area, match
/// depth with brush / lasso / quick select: option bar, brush size with <c>[</c> / <c>]</c>, the pending selection with its target depth typed
/// or picked from the photo, Apply / Clear / Cancel), the slow-tool progress and the "nothing changed" messages (with Lumen's shake), the depth
/// versions, the history's rebuild / unreliable states, and the pointer read-out. Strokes are forwarded to the session as the pointer moves;
/// nothing here waits for a tool.
/// </summary>
public sealed partial class DepthViewModel : ObservableObject
{
    public const string FocusHint = "Choose a focus point in the unshaded area. Shaded regions cannot be adjusted.";

    readonly IShell _shell;
    readonly Func<bool> _beforeTool;
    IDepthSession? _d;
    IToolStroke? _stroke;
    int _changedPosted;
    CancellationTokenSource? _messageCts, _hintCts;
    double _brushSize = ToolOptions.DefaultSize, _feather = 10;
    long _strokeStart;

    /// <param name="beforeTool">Called before a depth tool becomes active (the page applies an open crop first, as Lumen); false = not now.</param>
    public DepthViewModel(IShell shell, Func<bool> beforeTool)
    {
        _shell = shell;
        _beforeTool = beforeTool;
        Aperture = new ApertureRowViewModel(this);
        Focus = new FocusRowViewModel(this);
    }

    public IDepthSession? Session => _d;
    public ApertureRowViewModel Aperture { get; }
    public FocusRowViewModel Focus { get; }
    public ObservableCollection<VersionRowViewModel> Versions { get; } = new();

    /// <summary>A depth edit could not be made: the view shakes the picture (Lumen's "anomaly").</summary>
    public event Action? ShakeRequested;
    /// <summary>The pending selection was applied, cleared or cancelled: the view clears its strokes and lasso.</summary>
    public event Action? SelectionCleared;
    /// <summary>The tool, its mode or its options changed (the view shows the overlays that go with them).</summary>
    public event Action? ToolChanged;

    // ============================================================================================================ session link

    public void Attach(IDepthSession? d)
    {
        if (_d != null) _d.Changed -= OnChanged;
        _stroke?.Cancel();
        _stroke = null;
        _d = d;
        _shadeBeforeFocus = null;
        if (_d != null) _d.Changed += OnChanged;
        Tool = PageTool.None;
        IsPicking = false;
        Message = null;
        Versions.Clear();
        Refresh();
    }

    void OnChanged()
    {
        if (Interlocked.Exchange(ref _changedPosted, 1) == 0)
            Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _changedPosted, 0); Refresh(); }, DispatcherPriority.Normal);
    }

    /// <summary>Bring the bound values up to the session (UI thread).</summary>
    public void Refresh()
    {
        Aperture.Refresh();
        Focus.Refresh();
        if (_d is { } d)
        {
            var v = d.Versions;
            if (Versions.Count != v.Count || Versions.Where((r, i) => r.Info != v[i]).Any())
            {
                Versions.Clear();
                foreach (var x in v) Versions.Add(new VersionRowViewModel(x));
            }
            if (_wasPending && d.Pending is not { HasArea: true }) SelectionCleared?.Invoke();
            _wasPending = d.Pending is { HasArea: true };
            // the field shows the session's target (the library drops it with an applied selection) unless it is being typed in
            if (!IsEditingTarget && !TargetError && TargetEdit != TargetText) TargetEdit = TargetText;
        }
        foreach (var p in AllProperties) OnPropertyChanged(p);
        Changed?.Invoke();
    }

    bool _wasPending;

    /// <summary>Anything shown changed (the page refreshes the history rows and the shell's status).</summary>
    public event Action? Changed;

    static readonly string[] AllProperties =
    {
        nameof(HasDepth), nameof(IsReady), nameof(IsNotReady), nameof(ReadinessTitle), nameof(ReadinessText), nameof(CanAnalyse), nameof(IsAnalysing), nameof(AnalysisProgress),
        nameof(InFocusText), nameof(ShadeOutsideFocus), nameof(CanShade), nameof(FocusPin), nameof(ReferencePin), nameof(IsEffectOn),
        nameof(Tool), nameof(Mode), nameof(IsFocusTool), nameof(IsBrushTool), nameof(IsMatchTool), nameof(IsMatchBrush), nameof(IsLasso), nameof(IsQuickSelect), nameof(HasOptionBar),
        nameof(ToolTitle), nameof(ToolHint), nameof(UsesBrush), nameof(SizeValue), nameof(SizeText), nameof(SizeLabel), nameof(SizeTip), nameof(BrushDiameterPx), nameof(FollowColour), nameof(AddToSelection),
        nameof(HasPending), nameof(CanApply), nameof(PendingText), nameof(TargetText), nameof(TargetIsPicked), nameof(IsPicking),
        nameof(IsBusy), nameof(ActivityText), nameof(ActivityProgress), nameof(ActivityIndeterminate), nameof(CanCancelActivity),
        nameof(HistoryStatus), nameof(IsRebuilding), nameof(RebuildText), nameof(RebuildProgress), nameof(IsUnreliable), nameof(UnreliableText), nameof(UnreliableShort),
        nameof(HasDepthEdits), nameof(CanImportLumen), nameof(CurrentVersionName), nameof(HasMessage), nameof(MessageText), nameof(MessageIsWarning), nameof(LineText),
        nameof(ShowLine), nameof(SelectionOpacity), nameof(ShowsSelectionTint), nameof(CanStartClean), nameof(IsApplyingStroke),
    };

    // ============================================================================================================ readiness

    public bool HasDepth => _d != null;
    DepthStatusInfo Status => _d?.Status ?? new DepthStatusInfo(DepthReadiness.Unavailable, Message: "Depth editing is not available with this library yet.");
    public bool IsReady => Status.IsReady;
    public bool IsNotReady => !IsReady;
    public bool IsAnalysing => Status.Readiness == DepthReadiness.Analysing;
    public double AnalysisProgress => double.IsNaN(Status.Progress) ? 0 : Status.Progress;
    public bool CanAnalyse => Status.Readiness is DepthReadiness.NotAnalysed or DepthReadiness.Failed;

    public string ReadinessTitle => Status.Readiness switch
    {
        DepthReadiness.Ready => "",
        DepthReadiness.NotAnalysed => "This photo's depth map isn't made yet",
        DepthReadiness.Analysing => double.IsNaN(Status.Progress) ? "Making the depth map · waiting in the Queue" : $"Making the depth map · {Text.Percent(Status.Progress)}",
        DepthReadiness.Failed => "The depth map could not be made",
        _ => "No depth for this photo",
    };

    public string ReadinessText => Status.Readiness switch
    {
        DepthReadiness.Ready => "",
        DepthReadiness.NotAnalysed => "The depth effect, focus and the depth tools need it. Making it takes about a minute and runs in the background; you can keep editing.",
        DepthReadiness.Analysing => "The depth effect, focus and the depth tools work as soon as it is ready.",
        DepthReadiness.Failed => Status.Message ?? "Try again, or look in the Queue for the reason.",
        _ => Status.Message ?? "This photo has no depth map.",
    };

    [RelayCommand]
    void AnalyseNow()
    {
        if (_d == null || !CanAnalyse) return;
        _d.AnalyseNow();
        _shell.Notify("Making the depth map — you can follow it in the Queue");
        Refresh();
    }

    // ============================================================================================================ depth effect

    public bool IsEffectOn => _d?.FNumber != null && IsReady;

    /// <summary>"2.13 m – 45.6 m", "Everything (the effect is off)".</summary>
    public string InFocusText => !IsReady ? "—" : _d!.InFocus is { } r ? $"{DepthText.Distance(r.NearMm)} – {DepthText.Distance(r.FarMm)}" : "Everything";

    public bool CanShade => IsReady;

    public bool ShadeOutsideFocus
    {
        get => _d?.ShadeOutsideFocus == true;
        set { if (_d == null || !IsReady) return; _d.ShadeOutsideFocus = value; OnPropertyChanged(); ToolChanged?.Invoke(); }
    }

    /// <summary>H: shade outside the focal region on / off.</summary>
    [RelayCommand] void ToggleShade() { if (IsReady) ShadeOutsideFocus = !ShadeOutsideFocus; }

    public NormPoint? FocusPin => IsReady ? _d?.FocusPoint : null;
    public NormPoint? ReferencePin => Tool == PageTool.MatchDepth ? _d?.Pending?.ReferencePoint : null;

    [RelayCommand] void DefaultFocus() { if (IsReady) { _d!.ResetFocus(); Refresh(); } }
    [RelayCommand] void ResetEffect() { if (IsReady) { _d!.ResetEffect(); Refresh(); } }

    /// <summary>A click with the focus tool (picture as shown).</summary>
    public async Task FocusAtAsync(NormPoint p)
    {
        if (_d is not { } d) return;
        if (!IsReady) { ShowMessage(ReadinessTitle + ". " + ReadinessText, warning: true); return; }
        var r = await d.FocusAtAsync(p).ConfigureAwait(true);
        if (r.Ok) ShowMessage($"Focused at {DepthText.Distance(r.DepthMm!.Value)}", warning: false, seconds: 3);
        else ShowMessage(r.Reason ?? "This point can't be focused on.", warning: true);
        Refresh();
    }

    // ============================================================================================================ tools

    [ObservableProperty] PageTool _tool;
    [ObservableProperty] bool _isPicking;
    partial void OnIsPickingChanged(bool value) { OnPropertyChanged(nameof(ToolHint)); OnPropertyChanged(nameof(LineText)); ToolChanged?.Invoke(); }

    public MatchMode Mode => _d?.Mode ?? MatchMode.Brush;
    public bool IsFocusTool => Tool == PageTool.Focus;
    public bool IsBrushTool => Tool is PageTool.RefineEdge or PageTool.BlendArea;
    public bool IsMatchTool => Tool == PageTool.MatchDepth;
    public bool IsMatchBrush => IsMatchTool && Mode == MatchMode.Brush;
    public bool IsLasso => IsMatchTool && Mode == MatchMode.Lasso;
    public bool IsQuickSelect => IsMatchTool && Mode == MatchMode.QuickSelect;
    /// <summary>A tool that paints with the brush circle.</summary>
    public bool UsesBrush => IsBrushTool || IsMatchBrush || IsQuickSelect;
    public bool HasOptionBar => Tool is PageTool.Focus or PageTool.RefineEdge or PageTool.BlendArea or PageTool.MatchDepth;

    public string ToolTitle => Tool switch
    {
        PageTool.Focus => "Focus",
        PageTool.RefineEdge => "Refine edge",
        PageTool.BlendArea => "Blend area",
        PageTool.MatchDepth => Mode == MatchMode.QuickSelect ? "Quick select" : "Match depth",
        _ => "",
    };

    public string ToolHint
    {
        get
        {
            if (IsPicking) return "Click the photo where the depth is right: that depth is used for the selection.";
            if (!IsReady && HasOptionBar) return ReadinessTitle + " — the tools work as soon as it is ready.";
            return Tool switch
            {
                PageTool.Focus => "Click the photo where it should be sharp. The pin marks the point; “Default focus” goes back to the camera's own.",
                PageTool.RefineEdge => "Paint along an edge whose depth looks wrong, covering a little of each side. It is applied when you let go.",
                PageTool.BlendArea => "Paint over a surface whose depth should run smoothly — a road, a wall, a table. It is applied when you let go.",
                PageTool.MatchDepth => Mode switch
                {
                    MatchMode.QuickSelect => "Selection updates as you paint. Nothing changes in the depth map until you press Apply.",
                    MatchMode.Lasso => "Click to place corners, or drag to draw. Nothing changes in the depth map until you press Apply.",
                    _ => _d?.Pending?.HasTarget == true
                        ? "Paint over the area: it takes the chosen depth when you let go."
                        : "Set the depth (type it or pick it from the photo), then paint over the area. Nothing changes until it is applied.",
                },
                _ => "",
            };
        }
    }

    /// <summary>Activate a tool from the list or a key (pressing the active one's key again leaves it, as Lumen's toggles).</summary>
    public void Activate(PageTool tool, MatchMode? mode = null)
    {
        if (tool == PageTool.Crop || tool == PageTool.None) { SetTool(PageTool.None); return; }
        bool same = Tool == tool && (mode == null || mode == Mode);
        if (same) { SetTool(PageTool.None); return; }
        if (!_beforeTool()) return;
        SetTool(tool, mode);
    }

    /// <summary>"Shade outside focal region" as it was before the focus tool switched it on (restored when the tool is left).</summary>
    bool? _shadeBeforeFocus;

    void SetTool(PageTool tool, MatchMode? mode = null)
    {
        _stroke?.Cancel();
        _stroke = null;
        IsPicking = false;
        var prev = Tool;
        Tool = tool;
        if (_d != null)
        {
            var m = mode ?? (tool == PageTool.MatchDepth ? (Mode == MatchMode.QuickSelect && mode == null && prev != PageTool.MatchDepth ? MatchMode.Brush : Mode) : Mode);
            _d.SetTool(tool switch
            {
                PageTool.RefineEdge => DepthToolKind.RefineEdge,
                PageTool.BlendArea => DepthToolKind.BlendArea,
                PageTool.MatchDepth => DepthToolKind.MatchDepth,
                _ => null,
            }, m);
            ApplyOptions();
        }
        if (tool != PageTool.Focus) { _hintCts?.Cancel(); ShowHint = false; }
        if (tool == PageTool.Focus && prev != PageTool.Focus && IsReady)
        {
            // Lumen: entering the focus tool checks "show focal region" and shows the hint for five seconds; what it was is kept so that
            // leaving the tool puts it back
            if (_d != null) { _shadeBeforeFocus = _d.ShadeOutsideFocus; _d.ShadeOutsideFocus = true; }
            ShowFocusHint();
        }
        else if (prev == PageTool.Focus && tool != PageTool.Focus && _shadeBeforeFocus is bool before)
        {
            _shadeBeforeFocus = null;
            if (_d != null && _d.ShadeOutsideFocus != before) { _d.ShadeOutsideFocus = before; OnPropertyChanged(nameof(ShadeOutsideFocus)); }
        }
        Message = null;
        Refresh();
        ToolChanged?.Invoke();
    }

    /// <summary>Match depth's way of selecting (the applicator switch).</summary>
    [RelayCommand]
    void SetMode(MatchMode mode)
    {
        if (Tool != PageTool.MatchDepth) { Activate(PageTool.MatchDepth, mode); return; }
        if (Mode == mode) return;
        _stroke?.Cancel();
        _stroke = null;
        _d?.SetTool(DepthToolKind.MatchDepth, mode);
        ApplyOptions();
        SelectionCleared?.Invoke();
        Refresh();
        ToolChanged?.Invoke();
    }

    [RelayCommand] void LeaveTool() => SetTool(PageTool.None);

    // ---- the entry hint (Lumen: five seconds)
    [ObservableProperty] bool _showHint;

    void ShowFocusHint()
    {
        _hintCts?.Cancel();
        var cts = _hintCts = new CancellationTokenSource();
        ShowHint = true;
        _ = Task.Delay(5000, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) ShowHint = false; }); }, TaskScheduler.Default);
    }

    // ---- options: size (brush) / feathering (lasso), follow colour, add / remove
    public double SizeValue
    {
        get => IsLasso ? _feather : _brushSize;
        set
        {
            double min = IsLasso ? 0 : ToolOptions.MinSize;
            double v = Math.Round(Math.Clamp(value, min, ToolOptions.MaxSize));
            if (IsLasso) { if (Math.Abs(v - _feather) < 1e-9) return; _feather = v; }
            else { if (Math.Abs(v - _brushSize) < 1e-9) return; _brushSize = v; }
            ApplyOptions();
            OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(BrushDiameterPx)); OnPropertyChanged(nameof(SizeTip));
            ToolChanged?.Invoke();
        }
    }

    public string SizeLabel => IsLasso ? "Feather" : "Size";
    /// <summary>The brush's diameter in picture pixels at full size (the cursor circle).</summary>
    public double BrushDiameterPx => _d?.BrushDiameterPx(_brushSize) ?? _brushSize * 16.64;
    public string SizeText => IsLasso ? $"{_feather:0} %" : $"{Math.Round(BrushDiameterPx):0} px";
    public string SizeTip => IsLasso
        ? "How softly the lasso's edge blends into its surroundings ([ and ] change it)"
        : $"Brush size {_brushSize:0} on Lumen's 1–100 scale: {Math.Round(BrushDiameterPx):0} pixels across in the full-size photo ([ and ] change it)";

    /// <summary>[ / ]: a step smaller or larger (about a quarter).</summary>
    public void StepSize(int direction)
    {
        if (!UsesBrush && !IsLasso) return;
        double v = SizeValue;
        SizeValue = direction > 0 ? Math.Max(v + 1, Math.Round(v * 1.25)) : Math.Min(v - 1, Math.Round(v / 1.25));
    }

    [ObservableProperty] bool _followColour;
    partial void OnFollowColourChanged(bool value) => ApplyOptions();

    public bool AddToSelection
    {
        get => _add;
        set { if (_add == value) return; _add = value; ApplyOptions(); OnPropertyChanged(); ToolChanged?.Invoke(); }
    }
    bool _add = true;

    [RelayCommand] void SetAdd(string add) => AddToSelection = add != "false";

    void ApplyOptions() => _d?.SetOptions(new ToolOptions(IsLasso ? _feather : _brushSize, FollowColour, _add));

    /// <summary>The selection is drawn over the photo (quick select, lasso): its tint can be set.</summary>
    public bool ShowsSelectionTint => IsQuickSelect || IsLasso;

    public double SelectionOpacity
    {
        get => _d?.SelectionOpacity ?? 1;
        set { if (_d == null) return; _d.SelectionOpacity = value; OnPropertyChanged(); ToolChanged?.Invoke(); }
    }

    // ---- strokes (the brush overlay's events; nothing waits)
    /// <summary>Time spent on the UI thread per stroke move, ms (pointer-to-stroke latency, measurements).</summary>
    public List<double> StrokeMoveMs { get; } = new();
    /// <summary>Time from release to the tool's result, ms, per tool (measurements).</summary>
    public List<(string Tool, double Ms, ToolResult Result)> ApplyTimes { get; } = new();

    public void StrokeStarted(NormPoint p)
    {
        if (_d == null) return;
        if (!IsReady) { RefuseNotReady(); return; }
        try { _stroke = _d.BeginStroke(); }
        catch (InvalidOperationException e) { ShowMessage(e.Message, warning: true); ShakeRequested?.Invoke(); _stroke = null; return; }
        _strokeStart = Stopwatch.GetTimestamp();
        _stroke.Add(new[] { p });
        Message = null;
    }

    public void StrokeMoved(IReadOnlyList<NormPoint> points)
    {
        if (_stroke == null) return;
        long t0 = Stopwatch.GetTimestamp();
        _stroke.Add(points);
        StrokeMoveMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        if (StrokeMoveMs.Count > 20000) StrokeMoveMs.RemoveRange(0, 10000);
    }

    public async void StrokeEnded(int keptStroke = -1) => await EndStrokeAsync(keptStroke).ConfigureAwait(true);

    /// <summary>A quick-select stroke that was stopped or failed leaves the picture (it changed nothing).</summary>
    public event Action<int>? StrokeDropped;

    /// <summary>The stroke ends: run it (refine edge, blend area), make it the selection (match depth), update the selection (quick select);
    /// a match-depth brush stroke with a depth chosen is applied at once ("paints the depth on release").</summary>
    public async Task<ToolOutcome> EndStrokeAsync(int keptStroke = -1)
    {
        if (_stroke is not { } st) return ToolOutcome.Cancelled;
        bool quick = IsQuickSelect;
        _stroke = null;
        var tool = ToolTitle;
        long t0 = Stopwatch.GetTimestamp();
        _applying++;
        ToolResult result;
        ToolOutcome r;
        try
        {
            r = await st.EndAsync().ConfigureAwait(true);
            if (r.Changed && IsMatchBrush && _d?.Pending is { CanApply: true }) r = await ApplyAsync().ConfigureAwait(true);
        }
        finally { _applying--; }
        result = r.Result;
        if (quick && keptStroke >= 0 && r.Result is ToolResult.Cancelled or ToolResult.Failed or ToolResult.NothingChanged) StrokeDropped?.Invoke(keptStroke);
        ApplyTimes.Add((tool, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, r.Result));
        Report(r);
        Refresh();
        return r;
    }

    /// <summary>A press while the depth is not ready: the reason (and the shake).</summary>
    public void RefuseNotReady()
    {
        ShowMessage(ReadinessTitle + ". " + (CanAnalyse ? "Use “Analyse now” to make it." : ReadinessText), warning: true);
        ShakeRequested?.Invoke();
    }

    void Report(ToolOutcome r)
    {
        switch (r.Result)
        {
            case ToolResult.NothingChanged:
            case ToolResult.Failed:
                ShowMessage(r.Message ?? "Nothing changed.", warning: true);
                ShakeRequested?.Invoke();
                break;
            case ToolResult.Cancelled:
                ShowMessage("Cancelled — nothing changed.", warning: false, seconds: 3);
                break;
        }
    }

    // ---- the lasso
    public void LassoCorner(NormPoint p)
    {
        if (_d == null) return;
        if (!IsReady) { RefuseNotReady(); return; }
        _d.AddLassoPoint(p);
        Refresh();
    }

    /// <summary>A lasso drawn freehand: one stroke of the lasso.</summary>
    public async Task LassoDrawnAsync(IReadOnlyList<NormPoint> points)
    {
        if (_d == null || points.Count < 3) return;
        if (!IsReady) { RefuseNotReady(); return; }
        IToolStroke st;
        try { st = _d.BeginStroke(); }
        catch (InvalidOperationException e) { ShowMessage(e.Message, warning: true); return; }
        st.Add(points);
        var r = await st.EndAsync().ConfigureAwait(true);
        Report(r);
        Refresh();
    }

    // ---- the target depth
    [ObservableProperty] string _targetEdit = "";
    [ObservableProperty] bool _targetError;
    /// <summary>The target field has the keyboard (refreshes leave the text alone).</summary>
    public bool IsEditingTarget { get; set; }

    public bool TargetIsPicked => _d?.Pending?.ReferencePoint != null;
    (NormPoint Point, double? Mm)? _picked;

    /// <summary>"3.20 m", "3.18 m (picked)", "" when none.</summary>
    public string TargetText
    {
        get
        {
            var p = _d?.Pending;
            if (p?.TargetMm is double mm) return DepthText.Distance(mm);
            // the picked depth as read when it was picked (refreshes never read the library on the UI thread)
            if (p?.ReferencePoint is { } rp) return _picked is { } pk && pk.Point == rp ? (pk.Mm is double d ? DepthText.Distance(d) : "no depth") : "picked";
            return "";
        }
    }

    partial void OnTargetEditChanged(string value) => TargetError = false;

    public bool CommitTarget()
    {
        if (_d == null) return false;
        if (string.IsNullOrWhiteSpace(TargetEdit)) { TargetEdit = TargetText; return true; }
        if (!DepthText.TryParseDistance(TargetEdit, out var mm)) { TargetError = true; return false; }
        _d.SetTarget(mm);
        TargetEdit = DepthText.Distance(Math.Max(100, mm));
        Refresh();
        return true;
    }

    public void RevertTarget() { TargetError = false; TargetEdit = TargetText; }

    /// <summary>"Pick from photo": the next click on the photo picks the depth (Esc leaves).</summary>
    [RelayCommand]
    void Pick()
    {
        if (!IsReady) { RefuseNotReady(); return; }
        IsPicking = !IsPicking;
    }

    public void Picked(NormPoint p)
    {
        if (_d == null) return;
        if (IsPicking)
        {
            IsPicking = false;
            if (_d.DepthAt(p) is not double d) { ShowMessage("There is no depth at that point (it is too far away). Pick another point, or type the depth.", warning: true); return; }
            _picked = (p, d);
            _d.PickTarget(p);
            TargetEdit = DepthText.Distance(d);
            ShowMessage($"Depth picked: {DepthText.Distance(d)}", warning: false, seconds: 3);
            Refresh();
            return;
        }
        if (Tool == PageTool.Focus) _ = FocusAtAsync(p);
    }

    /// <summary>Alt + click in match depth picks the depth too (a shortcut for "Pick from photo").</summary>
    public void AltPicked(NormPoint p) { if (IsMatchTool && IsReady) { IsPicking = true; Picked(p); } }

    // ---- the pending selection
    public bool HasPending => _d?.Pending is { HasArea: true };
    public bool CanApply => _d?.Pending is { CanApply: true } && !IsBusy;

    public string PendingText
    {
        get
        {
            var p = _d?.Pending;
            if (!IsMatchTool) return "";
            if (p == null || !p.HasArea)
                return Mode == MatchMode.Lasso && p?.LassoPoints.Count is > 0 and < 3 ? $"{p.LassoPoints.Count} of at least 3 corners" : "Nothing selected yet";
            string area = double.IsNaN(p.SelectedFraction) || p.SelectedFraction <= 0 ? "Area selected" : $"{Math.Max(0.1, p.SelectedFraction * 100):0.#} % of the photo selected";
            return p.HasTarget ? area : area + " · choose the depth";
        }
    }

    [RelayCommand]
    async Task Apply()
    {
        var r = await ApplyAsync().ConfigureAwait(true);
        Report(r);
        Refresh();
    }

    async Task<ToolOutcome> ApplyAsync()
    {
        if (_d == null) return ToolOutcome.Cancelled;
        if (_d.Pending is not { } p || !p.HasArea) return new ToolOutcome(ToolResult.Failed, "Nothing is selected yet: paint, draw or select the area first.");
        if (!p.HasTarget) return new ToolOutcome(ToolResult.Failed, "Choose the depth first: type it next to “Set depth to”, or pick it from the photo.");
        long t0 = Stopwatch.GetTimestamp();
        var r = await _d.ApplyPendingAsync().ConfigureAwait(true);
        ApplyTimes.Add(("Apply " + Mode, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, r.Result));
        if (r.Changed) { SelectionCleared?.Invoke(); ShowMessage("Depth applied — undo with Ctrl+Z", warning: false, seconds: 3); }
        return r;
    }

    /// <summary>Clear: the selection goes, the tool stays.</summary>
    [RelayCommand]
    void Clear()
    {
        _d?.CancelPending();
        SelectionCleared?.Invoke();
        Refresh();
    }

    /// <summary>Esc: picking › the pending selection › the tool. False when there was nothing to cancel.</summary>
    public bool Cancel()
    {
        if (IsPicking) { IsPicking = false; return true; }
        if (_stroke != null) { _stroke.Cancel(); _stroke = null; SelectionCleared?.Invoke(); return true; }
        if (_d?.Pending is { } p && (p.HasArea || p.LassoPoints.Count > 0)) { Clear(); return true; }
        if (Tool != PageTool.None) { SetTool(PageTool.None); return true; }
        return false;
    }

    // ---- activity (slow tools)
    public bool IsBusy => _d?.Activity.Running == true;
    public string ActivityText => _d?.Activity is { Running: true } a
        ? (double.IsNaN(a.Progress) ? a.Label : $"{a.Label} · {Text.Percent(a.Progress)}") + (a.Queued switch { 0 => "", 1 => " · 1 more waiting", var q => $" · {q} more waiting" })
        : "";
    public double ActivityProgress => _d?.Activity is { Running: true } a && !double.IsNaN(a.Progress) ? a.Progress * 100 : 0;
    public bool ActivityIndeterminate => _d?.Activity is { Running: true } a && double.IsNaN(a.Progress);
    public bool CanCancelActivity => _d?.Activity is { Running: true, CanCancel: true };
    [RelayCommand] void CancelActivity() => _d?.CancelActivity();

    // ---- the message line ("nothing changed because …")
    [ObservableProperty] string? _message;
    [ObservableProperty] bool _messageWarning;
    partial void OnMessageChanged(string? value) { OnPropertyChanged(nameof(HasMessage)); OnPropertyChanged(nameof(MessageText)); OnPropertyChanged(nameof(LineText)); OnPropertyChanged(nameof(ShowLine)); OnPropertyChanged(nameof(MessageIsWarning)); }
    public bool HasMessage => Message != null;
    public string MessageText => Message ?? "";
    public bool MessageIsWarning => Message != null && MessageWarning;
    /// <summary>The line under the option bar: a message when there is one, else the tool's hint.</summary>
    public string LineText => Message ?? ToolHint;
    public bool ShowLine => HasOptionBar;

    public void ShowMessage(string text, bool warning, int seconds = 7)
    {
        _messageCts?.Cancel();
        var cts = _messageCts = new CancellationTokenSource();
        MessageWarning = warning;
        Message = text;
        OnPropertyChanged(nameof(MessageIsWarning));
        _ = Task.Delay(seconds * 1000, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) Message = null; }); }, TaskScheduler.Default);
    }

    // ============================================================================================================ history and versions

    public HistoryStatus HistoryStatus => _d?.HistoryStatus ?? HistoryStatus.Ready;
    public bool IsRebuilding => HistoryStatus.State == HistoryState.Rebuilding;
    public bool IsUnreliable => HistoryStatus.State == HistoryState.Unreliable;
    public string RebuildText => !IsRebuilding ? "" : HistoryStatus.Total > 0 ? $"Rebuilding earlier steps · {HistoryStatus.Done} of {HistoryStatus.Total}"
        : double.IsNaN(HistoryStatus.Progress) ? "Rebuilding earlier steps" : $"Rebuilding earlier steps · {Text.Percent(HistoryStatus.Progress)}";
    public double RebuildProgress => !IsRebuilding ? 0 : HistoryStatus.Total > 0 ? 100.0 * HistoryStatus.Done / HistoryStatus.Total : double.IsNaN(HistoryStatus.Progress) ? 0 : HistoryStatus.Progress * 100;
    public string UnreliableShort => "Your photo is kept exactly as saved and editing works as usual — only the saved steps can't be undone one by one.";
    public string UnreliableText =>
        "Your photo and its depth are kept exactly as saved, and editing works as usual — new steps can be undone. Only the earlier saved depth steps can't be undone one by one. To go back further, start a clean version: these edits stay as a version of their own.";

    public bool HasDepthEdits => _d?.HasDepthEdits == true;
    /// <summary>"Start a clean version" makes sense once the photo has a version with edits (a never-edited photo is clean already).</summary>
    public bool CanStartClean => _d != null && IsReady && _d.Versions.Any(v => v.Kind != VersionKind.Clean);
    /// <summary>A stroke was released and its tool is still running (the view keeps the stroke drawn until then).</summary>
    public bool IsApplyingStroke => _applying > 0;
    int _applying;
    /// <summary>A Lumen import brings Lumen's own depth: it does not wait for an analysis.</summary>
    public bool CanImportLumen => _d?.CanImportLumen == true;
    public string CurrentVersionName => _d?.Versions.FirstOrDefault(v => v.IsCurrent)?.Name ?? "";

    [RelayCommand]
    async Task SwitchVersion(VersionRowViewModel? row)
    {
        if (_d == null || row == null || row.IsCurrent) return;
        if (!IsReady) { RefuseNotReady(); return; }
        Clear();
        var r = await _d.SwitchVersionAsync(row.Info.Id).ConfigureAwait(true);
        if (r.Changed) _shell.Notify($"Showing “{row.Name}” — the other versions keep their edits");
        else Report(r);
        Refresh();
    }

    [RelayCommand]
    async Task StartCleanVersion()
    {
        if (_d == null) return;
        if (!IsReady) { RefuseNotReady(); return; }
        Clear();
        var r = await _d.StartCleanVersionAsync().ConfigureAwait(true);
        if (r.Changed) _shell.Notify("A clean version started from Lux's own depth — your earlier versions are kept");
        else Report(r);
        Refresh();
    }

    /// <summary>Ctrl+R: Lumen's reset of the depth edits (it cannot be undone, so it asks first and offers a clean version instead).</summary>
    [RelayCommand]
    void ResetDepthEdits()
    {
        if (_d is not { HasDepthEdits: true } d) { _shell.Notify("This version has no depth edits to reset"); return; }
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel($"Reset the depth edits of “{CurrentVersionName}”?",
            "The depth goes back to the start of this version. As in Lumen, a reset can't be undone. To keep these edits and still start over, use “Start a clean version” instead — nothing is deleted then.",
            "Reset depth edits",
            async () =>
            {
                _shell.CloseDialog(c!);
                Clear();
                var r = await d.ResetDepthEditsAsync().ConfigureAwait(true);
                if (r.Changed) _shell.Notify("Depth edits reset");
                else Report(r);
                Refresh();
            },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    // ============================================================================================================ pointer read-out

    [ObservableProperty] string _pointerText = "";
    /// <summary>The depth read last under the pointer (the focus tool's cursor follows it).</summary>
    public (NormPoint Point, double? DepthMm)? LastPointerDepth { get; private set; }
    /// <summary>Depth read-outs made (tests).</summary>
    public int ReadOuts { get; private set; }

    /// <summary>The pointer over the picture (status bar): "x 4326  y 3869  depth 3.18 m". The library's depth read is lock-free,
    /// so it is read right here.</summary>
    public void PointerAt(NormPoint? p, Avalonia.PixelSize picture)
    {
        if (p is not { } n || picture.Width <= 0) { PointerText = ""; return; }
        int x = (int)Math.Clamp(Math.Floor(n.X * picture.Width), 0, picture.Width - 1), y = (int)Math.Clamp(Math.Floor(n.Y * picture.Height), 0, picture.Height - 1);
        if (!IsReady || _d == null) { PointerText = $"x {x}  y {y}  depth —"; return; }
        double? mm = _d.DepthAt(n);
        LastPointerDepth = (n, mm);
        ReadOuts++;
        PointerText = $"x {x}  y {y}  {Depth(mm)}";
    }

    static string Depth(double? mm) => mm is double d ? "depth " + DepthText.Distance(d) : "depth: far";
}

/// <summary>The aperture slider: f-numbers on a log scale from the widest (left) to all in focus = "Off" (right end, the default).</summary>
public sealed partial class ApertureRowViewModel(DepthViewModel owner) : ObservableObject, IAdjustmentSliderTarget
{
    IDepthSession? D => owner.Session;
    DepthEffectRange R => D?.EffectRange ?? DepthEffectRange.Placeholder;
    double _shown = 1;

    public double Position => _shown;
    public double DefaultPosition => 1;
    public double StepPosition => 1.0 / 30;
    public bool IsEnabled => owner.IsReady;
    public string ValueText => DepthText.FNumber(FOf(_shown));
    public bool IsOff => FOf(_shown) == null;
    public string Tip => "Aperture of the depth effect: smaller f-numbers blur more of what is out of focus. At the right end the effect is off (everything sharp) — double-click the name to turn it off, click the number to type one.";

    /// <summary>The f-number at a slider position (the right end and its last hair = off).</summary>
    public double? FOf(double p)
    {
        if (p >= 0.995) return null;
        double lo = R.WidestFNumber, hi = R.AllInFocusFNumber;
        return Math.Round(Math.Exp(Math.Log(lo) + Math.Clamp(p, 0, 1) * (Math.Log(hi) - Math.Log(lo))), 1);
    }

    public double PositionOf(double? f)
    {
        if (f is not double v) return 1;
        double lo = R.WidestFNumber, hi = R.AllInFocusFNumber;
        return Math.Clamp((Math.Log(v) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo)), 0, 0.99);
    }

    bool _dragging;
    public void BeginDrag() { if (D is { } d && owner.IsReady) { _dragging = true; d.BeginEffectDrag(); } }
    public void DragTo(double position)
    {
        if (D is not { } d || !owner.IsReady) return;
        if (!_dragging) BeginDrag();
        Show(position);
        d.DragEffect(FOf(position), d.FocusMm);
    }
    public void EndDrag() { if (!_dragging) return; _dragging = false; D?.EndEffectDrag(); owner.Refresh(); }

    [ObservableProperty] string _editText = "Off";
    [ObservableProperty] bool _hasError;
    public bool IsEditing { get; set; }

    public bool CommitText()
    {
        if (D is not { } d || !owner.IsReady) return false;
        if (!DepthText.TryParseFNumber(EditText, out var f)) { HasError = true; return false; }
        HasError = false;
        if (f is double v) f = Math.Clamp(v, R.WidestFNumber, R.AllInFocusFNumber);
        d.SetEffect(f, d.FocusMm);
        owner.Refresh();
        return true;
    }

    public void RevertText() { HasError = false; EditText = ValueText; }

    /// <summary>Double-click on the name: off (all in focus).</summary>
    public void TurnOff() { if (D is { } d && owner.IsReady && d.FNumber != null) { d.SetEffect(null, d.FocusMm); owner.Refresh(); } }

    public void Refresh()
    {
        if (_dragging) return;
        Show(PositionOf(D?.FNumber));
    }

    void Show(double p)
    {
        _shown = p;
        if (!HasError && !IsEditing) EditText = ValueText;
        OnPropertyChanged(nameof(Position)); OnPropertyChanged(nameof(ValueText)); OnPropertyChanged(nameof(IsOff)); OnPropertyChanged(nameof(IsEnabled));
    }
}

/// <summary>The focus slider: the focus distance on a log scale between the photo's nearest and farthest depth; the default focus marked.</summary>
public sealed partial class FocusRowViewModel(DepthViewModel owner) : ObservableObject, IAdjustmentSliderTarget
{
    IDepthSession? D => owner.Session;
    DepthEffectRange R => D?.EffectRange ?? DepthEffectRange.Placeholder;
    double _shown = 0.5;

    public double Position => _shown;
    public double DefaultPosition => PositionOf(R.DefaultFocusMm);
    public double StepPosition => 1.0 / 60;
    public bool IsEnabled => owner.IsReady;
    public string ValueText => owner.IsReady ? DepthText.Distance(MmOf(_shown)) : "—";
    public string Tip => "Focus distance of the depth effect (or click the photo with the focus tool, F). Double-click the name for the default focus; click the number to type a distance.";

    public double MmOf(double p) => Math.Exp(Math.Log(R.NearestMm) + Math.Clamp(p, 0, 1) * (Math.Log(R.FarthestMm) - Math.Log(R.NearestMm)));
    public double PositionOf(double mm) => Math.Clamp((Math.Log(Math.Max(1, mm)) - Math.Log(R.NearestMm)) / (Math.Log(R.FarthestMm) - Math.Log(R.NearestMm)), 0, 1);

    bool _dragging;
    public void BeginDrag() { if (D is { } d && owner.IsReady) { _dragging = true; d.BeginEffectDrag(); } }
    public void DragTo(double position)
    {
        if (D is not { } d || !owner.IsReady) return;
        if (!_dragging) BeginDrag();
        Show(position);
        d.DragEffect(d.FNumber, MmOf(position));
    }
    public void EndDrag() { if (!_dragging) return; _dragging = false; D?.EndEffectDrag(); owner.Refresh(); }

    [ObservableProperty] string _editText = "—";
    [ObservableProperty] bool _hasError;
    public bool IsEditing { get; set; }

    public bool CommitText()
    {
        if (D is not { } d || !owner.IsReady) return false;
        if (!DepthText.TryParseDistance(EditText, out var mm)) { HasError = true; return false; }
        HasError = false;
        d.SetEffect(d.FNumber, Math.Clamp(mm, R.NearestMm, R.FarthestMm));
        owner.Refresh();
        return true;
    }

    public void RevertText() { HasError = false; EditText = ValueText; }
    public void ToDefault() { if (D is { } d && owner.IsReady) { d.ResetFocus(); owner.Refresh(); } }

    public void Refresh()
    {
        if (_dragging) return;
        Show(PositionOf(D?.FocusMm ?? R.DefaultFocusMm));
    }

    void Show(double p)
    {
        _shown = p;
        if (!HasError && !IsEditing) EditText = ValueText;
        OnPropertyChanged(nameof(Position)); OnPropertyChanged(nameof(ValueText)); OnPropertyChanged(nameof(DefaultPosition)); OnPropertyChanged(nameof(IsEnabled));
    }
}

/// <summary>A row of the depth version list.</summary>
public sealed class VersionRowViewModel(DepthVersionInfo info)
{
    public DepthVersionInfo Info { get; } = info;
    public string Name => Info.Name;
    public bool IsCurrent => Info.IsCurrent;
    /// <summary>The count at the right ("4"), empty for the clean version.</summary>
    public string CountText => Info.Kind == VersionKind.Clean || Info.Steps == 0 ? "" : Info.Steps.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Tip => (Info.Note ?? Info.Name) + (Info.IsCurrent ? " — shown now" : " — click to show it (the others keep their edits)");
}
