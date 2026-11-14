using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Lux.App.Develop;
using Lux.App.Viewer;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class DevelopView : UserControl
{
    readonly CropOverlay _crop = new() { IsVisible = false };
    // The depth overlays on the photo (Mask: shade, selection; Markers: pins; Cursor: brush, lasso, pick, the other view's pointer) …
    readonly TileOverlay _shade = new(OverlayLayer.Mask) { IsVisible = false };
    readonly TileOverlay _mask = new(OverlayLayer.Mask) { IsVisible = false };
    readonly PinOverlay _pins = new();
    readonly BrushOverlay _brush = new() { IsVisible = false };
    readonly LassoOverlay _lasso = new() { IsVisible = false };
    readonly PointPickOverlay _pick = new() { IsVisible = false };
    readonly MirrorOverlay _mirrorOnPhoto = new();
    readonly LabelOverlay _photoLabel = new() { IsVisible = false };
    // … and on the depth view of "Photo + depth"
    readonly MirrorOverlay _mirrorOnDepth = new();
    readonly PinOverlay _depthPins = new();
    readonly DepthLegendOverlay _legend = new();
    bool _syncingCrop, _syncingView;
    DevelopViewModel? _vm;

    public DevelopView()
    {
        InitializeComponent();
        Viewer.AddOverlay(_crop);
        foreach (var o in new ViewerOverlay[] { _shade, _mask, _pins, _brush, _lasso, _pick, _mirrorOnPhoto, _photoLabel }) Viewer.AddOverlay(o);
        foreach (var o in new ViewerOverlay[] { _mirrorOnDepth, _depthPins, _legend }) DepthViewer.AddOverlay(o);
        WireDepth();
        _crop.Changed += () => { if (!_syncingCrop) _vm?.CropFrameDragged(_crop.Rect, _crop.IsDragging); };
        _crop.DragEnded += () => _vm?.CropFrameDragged(_crop.Rect, false);
        Viewer.Interacted += () => _vm?.Interaction.Poke();
        Viewer.ViewChanged += UpdateCropRatio;
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, (_, _) => _vm?.Interaction.Poke(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public PhotoViewer PhotoViewer => Viewer;
    public PhotoViewer DepthPhotoViewer => DepthViewer;
    public CropOverlay CropOverlay => _crop;
    public BrushOverlay BrushOverlay => _brush;
    public LassoOverlay LassoOverlay => _lasso;
    public PointPickOverlay PickOverlay => _pick;
    public PinOverlay PinOverlay => _pins;
    public TileOverlay ShadeOverlay => _shade;
    public TileOverlay MaskOverlay => _mask;
    public DepthLegendOverlay LegendOverlay => _legend;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged; _vm.SyncCropFrame -= OnSyncCrop;
            _vm.Depth.PropertyChanged -= OnDepthPropertyChanged; _vm.Depth.ToolChanged -= UpdateDepthOverlays;
            _vm.Depth.ShakeRequested -= OnShake; _vm.Depth.SelectionCleared -= OnSelectionCleared; _vm.Depth.Changed -= UpdateDepthOverlays;
            _vm.Depth.StrokeDropped -= _brush.RemoveKept;
        }
        _vm = DataContext as DevelopViewModel;
        if (_vm == null) return;
        _vm.AttachViewer(Viewer);
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SyncCropFrame += OnSyncCrop;
        _vm.Depth.PropertyChanged += OnDepthPropertyChanged;
        _vm.Depth.ToolChanged += UpdateDepthOverlays;
        _vm.Depth.ShakeRequested += OnShake;
        _vm.Depth.SelectionCleared += OnSelectionCleared;
        _vm.Depth.StrokeDropped += _brush.RemoveKept;
        _vm.Depth.Changed += UpdateDepthOverlays;
        _vm.Services.ViewerPrefs.Changed -= ApplyViewerPrefs;
        _vm.Services.ViewerPrefs.Changed += ApplyViewerPrefs;
        ApplyViewerPrefs();
        UpdateSources();
        UpdateCrop();
    }

    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DevelopViewModel.Session):
                UpdateSources();
                Dispatcher.UIThread.Post(ScrollStripToCurrent, DispatcherPriority.Background);
                break;
            case nameof(DevelopViewModel.IsPhotoDepth):
            case nameof(DevelopViewModel.ShowDepthBeside):
            case nameof(DevelopViewModel.MainShowsDepth):
                UpdateSources();
                break;
            case nameof(DevelopViewModel.Item):
                Dispatcher.UIThread.Post(ScrollStripToCurrent, DispatcherPriority.Background);
                MarkStripCurrent();
                // the page's keys work at once (the library's button that opened it is hidden now)
                Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) Viewer.Focus(); }, DispatcherPriority.Background);
                break;
            case nameof(DevelopViewModel.IsCropping):
            case nameof(DevelopViewModel.CropAspectChoice):
                UpdateCrop();
                break;
        }
    }

    void OnSyncCrop(Lux.Library.NormRect r)
    {
        _syncingCrop = true;
        try { _crop.Rect = r; }
        finally { _syncingCrop = false; }
        UpdateCropRatio();
    }

    void UpdateCrop()
    {
        bool on = _vm?.IsCropping == true;
        if (_crop.IsVisible != on) _crop.IsVisible = on;
        if (on && _vm?.Session is { } s) OnSyncCrop(s.CropFrame);
        else UpdateCropRatio();
    }

    /// <summary>With an aspect preset the handles keep its shape (presets are in the picture as shown).</summary>
    void UpdateCropRatio()
    {
        _crop.Ratio = _vm?.Session is { IsCropping: true } s && s.CropAspect != Lux.Library.CropAspect.Free ? _vm.CropAspectChoice?.Preset.Ratio ?? 0 : 0;
    }

    // ---------------------------------------------------------------------------------------------------------------- depth

    void WireDepth()
    {
        _brush.StrokeStarted += p => _vm?.Depth.StrokeStarted(p);
        _brush.StrokeMoved += pts => _vm?.Depth.StrokeMoved(pts);
        _brush.StrokeEnded += () => _vm?.Depth.StrokeEnded(_brush.LastKeptId);
        _brush.Refused += () => _vm?.Depth.RefuseNotReady();
        _brush.AltPick = p => _vm?.Depth.AltPicked(p);
        _lasso.CornerAdded += p => _vm?.Depth.LassoCorner(p);
        _lasso.FreehandDrawn += pts => { if (_vm != null) _ = _vm.Depth.LassoDrawnAsync(pts); };
        _lasso.Restarted += () => _vm?.Depth.ClearCommand.Execute(null);
        _lasso.Refused += () => _vm?.Depth.RefuseNotReady();
        _lasso.AltPick = p => _vm?.Depth.AltPicked(p);
        _pick.Picked += p => _vm?.Depth.Picked(p);
        // the library's depth read is lock-free: the cursor reads it directly
        _pick.CanUse = p => _vm?.Depth is { } d && d.Session is { } s && d.IsReady && (d.IsPicking ? s.DepthAt(p) != null : s.CanFocusAt(p));
        Viewer.Hovered += OnPhotoHover;
        DepthViewer.Hovered += OnDepthHover;
        Viewer.ViewChanged += () => SyncView(Viewer, DepthViewer);
        DepthViewer.ViewChanged += () => SyncView(DepthViewer, Viewer);
        DepthViewer.Interacted += () => _vm?.Interaction.Poke();
    }

    /// <summary>The main viewer shows the photo (or, "Depth only", the depth map); the second one the depth map beside it.</summary>
    void UpdateSources()
    {
        var d = _vm?.Session?.Depth;
        bool beside = _vm?.ShowDepthBeside == true && d != null;
        Viewer.Source = _vm?.MainShowsDepth == true && d != null ? d.DepthView : _vm?.Session?.Tiles;
        DepthViewer.Source = beside ? d!.DepthView : null;
        DepthViewer.IsVisible = beside;
        Grid.SetColumnSpan(Viewer, beside ? 1 : 3);
        UpdateDepthOverlays();
        if (beside) Dispatcher.UIThread.Post(() => SyncView(Viewer, DepthViewer), DispatcherPriority.Background);
    }

    /// <summary>Pan and zoom locked together: the other viewer takes this one's view (both show pictures of the same size).</summary>
    void SyncView(PhotoViewer from, PhotoViewer to)
    {
        if (_syncingView || !to.IsVisible || !from.IsVisible || _vm?.ShowDepthBeside != true) return;
        _syncingView = true;
        try { to.SetViewState(from.ViewState); }
        finally { _syncingView = false; }
    }

    void OnPhotoHover(ViewerPointer? p)
    {
        var pt = p is { } v ? new Lux.Library.NormPoint(v.Frame.X, v.Frame.Y) : (Lux.Library.NormPoint?)null;
        _vm?.Depth.PointerAt(pt, Viewer.Pyramid?.Image ?? default);
        _mirrorOnDepth.At = pt;
        if (p != null) _mirrorOnPhoto.At = null;
    }

    void OnDepthHover(ViewerPointer? p)
    {
        var pt = p is { } v ? new Lux.Library.NormPoint(v.Frame.X, v.Frame.Y) : (Lux.Library.NormPoint?)null;
        _vm?.Depth.PointerAt(pt, DepthViewer.Pyramid?.Image ?? default);
        _mirrorOnPhoto.At = pt;
    }

    void OnDepthPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DepthViewModel.IsPicking) or nameof(DepthViewModel.Tool) or nameof(DepthViewModel.ShadeOutsideFocus)
            or nameof(DepthViewModel.FocusPin) or nameof(DepthViewModel.ReferencePin) or nameof(DepthViewModel.IsReady) or nameof(DepthViewModel.SizeValue)
            or nameof(DepthViewModel.AddToSelection) or nameof(DepthViewModel.SelectionOpacity))
            UpdateDepthOverlays();
    }

    /// <summary>The overlays that go with the active tool and the depth state.</summary>
    void UpdateDepthOverlays()
    {
        var d = _vm?.Depth;
        var s = d?.Session;
        bool ready = d?.IsReady == true;
        bool picking = d?.IsPicking == true;
        _pick.IsVisible = d != null && (d.IsFocusTool || picking);
        _brush.IsVisible = d != null && d.UsesBrush && !picking;
        _brush.Enabled = ready;
        if (d != null) _brush.BrushPx = d.BrushDiameterPx;
        _brush.Dark = d?.IsQuickSelect == true && !d.AddToSelection;
        // quick select keeps its strokes until Apply / Clear; the other brushes keep the released stroke while their tool runs
        _brush.KeepStrokes = d?.UsesBrush == true;
        if (d != null && !d.IsQuickSelect && !d.IsApplyingStroke && !d.HasPending && _brush.KeptStrokes > 0) _brush.ClearStrokes();
        _lasso.IsVisible = d?.IsLasso == true && !picking;
        _lasso.Enabled = ready;
        var focusPin = d?.FocusPin;
        _pins.Focus = d != null && (d.IsFocusTool || d.IsEffectOn) ? focusPin : null;
        _pins.Reference = d?.ReferencePin;
        _depthPins.Focus = _pins.Focus;
        _depthPins.Reference = _pins.Reference;
        _shade.Source = s?.ShadeLayer;
        _shade.IsVisible = ready && d!.ShadeOutsideFocus && _shade.Source != null;
        _mask.Source = s?.MaskLayer;
        _mask.IsVisible = d?.IsMatchTool == true && _mask.Source != null;
        _mask.Opacity = d?.SelectionOpacity ?? 1;
        _mirrorOnDepth.BrushPx = d?.UsesBrush == true ? d.BrushDiameterPx : 0;
        _mirrorOnPhoto.BrushPx = _mirrorOnDepth.BrushPx;
        bool beside = _vm?.ShowDepthBeside == true;
        _photoLabel.IsVisible = beside;
        _photoLabel.Text = s?.FNumber is double f ? $"Photo · {DepthText.FNumber(f)}" : "Photo · depth effect off";
        if (s != null)
        {
            var (near, far) = s.DepthViewRange;
            _legend.SetRange(DepthText.Distance(near), DepthText.Distance(far));
        }
        _legend.IsVisible = beside || _vm?.MainShowsDepth == true;
        if (_vm?.MainShowsDepth == true && _legend.Viewer != Viewer) { DepthViewer.RemoveOverlay(_legend); Viewer.AddOverlay(_legend); }
        else if (_vm?.MainShowsDepth != true && _legend.Viewer != DepthViewer) { Viewer.RemoveOverlay(_legend); DepthViewer.AddOverlay(_legend); }
    }

    void OnShake() { Viewer.Shake(); if (DepthViewer.IsVisible) DepthViewer.Shake(); }

    void ApplyViewerPrefs()
    {
        if (_vm == null) return;
        _vm.Services.ViewerPrefs.ApplyTo(Viewer);
        _vm.Services.ViewerPrefs.ApplyTo(DepthViewer);
    }

    void OnSelectionCleared() { _brush.ClearStrokes(); _lasso.Clear(); }

    void OnApertureLabelDoubleTapped(object? sender, TappedEventArgs e) => _vm?.Depth.Aperture.TurnOff();
    void OnFocusLabelDoubleTapped(object? sender, TappedEventArgs e) => _vm?.Depth.Focus.ToDefault();

    (Func<bool> Commit, Action Revert, Action<bool> Editing)? EffectRow(object? sender) => (sender as Control)?.Name switch
    {
        "ApertureBox" when _vm != null => (_vm.Depth.Aperture.CommitText, _vm.Depth.Aperture.RevertText, b => _vm.Depth.Aperture.IsEditing = b),
        "FocusBox" when _vm != null => (_vm.Depth.Focus.CommitText, _vm.Depth.Focus.RevertText, b => _vm.Depth.Focus.IsEditing = b),
        _ => null,
    };

    void OnEffectKeyDown(object? sender, KeyEventArgs e)
    {
        if (EffectRow(sender) is not { } row) return;
        if (e.Key == Key.Enter) { if (row.Commit()) Viewer.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { row.Revert(); Viewer.Focus(); e.Handled = true; }
    }

    void OnEffectGotFocus(object? sender, GotFocusEventArgs e) => EffectRow(sender)?.Editing(true);

    void OnEffectLostFocus(object? sender, RoutedEventArgs e)
    {
        if (EffectRow(sender) is not { } row) return;
        row.Editing(false);
        var box = (TextBox)sender!;
        string shown = box.Name == "ApertureBox" ? _vm!.Depth.Aperture.ValueText : _vm!.Depth.Focus.ValueText;
        if (box.Text != shown && !row.Commit()) row.Revert();
    }

    void OnTargetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter) { if (_vm.Depth.CommitTarget()) Viewer.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.Depth.RevertTarget(); Viewer.Focus(); e.Handled = true; }
    }

    void OnTargetGotFocus(object? sender, GotFocusEventArgs e) { if (_vm != null) _vm.Depth.IsEditingTarget = true; }

    void OnTargetLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Depth.IsEditingTarget = false;
        if (_vm.Depth.TargetEdit != _vm.Depth.TargetText && !_vm.Depth.CommitTarget()) _vm.Depth.RevertTarget();
    }

    // ---------------------------------------------------------------------------------------------------------------- filmstrip

    void ScrollStripToCurrent()
    {
        if (_vm?.Item is not { } item) return;
        int i = _vm.Strip.ToList().FindIndex(x => x.Id == item.Id);
        if (i >= 0) Strip.ScrollIntoView(i);
        MarkStripCurrent();
    }

    /// <summary>The open capture's cell gets the accent ring.</summary>
    void MarkStripCurrent()
    {
        var id = _vm?.Item?.Id;
        foreach (var cell in Strip.GetRealizedContainers())
            if (cell is ContentPresenter { Child: Border b } && b.DataContext is CaptureItemViewModel it) b.Classes.Set("current", it.Id == id);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Strip.ContainerPrepared += (_, a) => Dispatcher.UIThread.Post(MarkStripCurrent, DispatcherPriority.Background);
    }

    void OnStripTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CaptureItemViewModel item && _vm != null) _ = _vm.OpenFromStrip(item);
    }

    // ---------------------------------------------------------------------------------------------------------------- sliders

    void OnAdjustmentLabelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AdjustmentRowViewModel row && _vm?.IsEditable == true) row.ResetToDefault();
    }

    void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is not AdjustmentRowViewModel row) return;
        if (e.Key == Key.Enter)
        {
            if (row.CommitText()) Viewer.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.RevertText();
            Viewer.Focus();
            e.Handled = true;
        }
    }

    void OnValueGotFocus(object? sender, GotFocusEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is AdjustmentRowViewModel row) row.IsEditing = true;
    }

    void OnValueLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is not AdjustmentRowViewModel row) return;
        row.IsEditing = false;
        if (row.EditText != row.ValueText && !row.CommitText()) row.RevertText();
    }

    void OnAspectClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AspectChoice c && _vm != null) _vm.CropAspectChoice = c;
    }

    // ---------------------------------------------------------------------------------------------------------------- keyboard map

    /// <summary>
    /// The page's keys. Text fields keep their own keys; the viewer pans with the arrows when
    /// the picture is larger than the view, otherwise ← / → step through the captures.
    /// </summary>
    void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm || e.Handled) return;
        if (e.Source is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Z when ctrl && shift: vm.RedoCommand.Execute(null); break;
            case Key.Z when ctrl: vm.UndoCommand.Execute(null); break;
            case Key.Y when ctrl: vm.RedoCommand.Execute(null); break;
            case Key.C when ctrl: vm.CopyCommand.Execute(null); break;
            case Key.V when ctrl && shift: vm.PasteChooseCommand.Execute(null); break;
            case Key.V when ctrl: vm.PasteCommand.Execute(null); break;
            case Key.S when ctrl: vm.SaveNowCommand.Execute(null); break;
            case Key.E when ctrl: vm.OpenExportCommand.Execute(null); break;
            case Key.F when ctrl: vm.ToggleFilmstripCommand.Execute(null); break;
            case Key.I when ctrl: vm.ToggleInfoCommand.Execute(null); break;
            case Key.R when ctrl && shift: vm.RevertCommand.Execute(null); break;
            case Key.R when ctrl: vm.Depth.ResetDepthEditsCommand.Execute(null); break;
            case Key.R when shift && !ctrl: vm.ResetToneCommand.Execute(null); break;
            case Key.OemOpenBrackets when ctrl: vm.RotateLeftCommand.Execute(null); break;
            case Key.OemCloseBrackets when ctrl: vm.RotateRightCommand.Execute(null); break;
            case Key.OemOpenBrackets when !alt: vm.Depth.StepSize(-1); break;
            case Key.OemCloseBrackets when !alt: vm.Depth.StepSize(1); break;
            case Key.H when !ctrl && !alt: vm.Depth.ToggleShadeCommand.Execute(null); break;
            case Key.D when shift && !ctrl && !alt: vm.ToggleDepthOnlyCommand.Execute(null); break;
            case Key.D when !ctrl && !alt: vm.TogglePhotoDepthCommand.Execute(null); break;
            case Key.Enter when vm.Depth.IsLasso && !_lasso.IsClosed && _lasso.Points.Count >= 3 && !vm.Depth.CanApply: _lasso.Close(); break;
            case Key.Enter when vm.Depth.IsMatchTool && vm.Depth.CanApply: vm.Depth.ApplyCommand.Execute(null); break;
            case Key.D0 or Key.NumPad0 when ctrl && alt: vm.ZoomActualCommand.Execute(null); break;
            case Key.D0 or Key.NumPad0 when ctrl: vm.ZoomFitCommand.Execute(null); break;
            case Key.OemPlus or Key.Add when ctrl: vm.ZoomInCommand.Execute(null); break;
            case Key.OemMinus or Key.Subtract when ctrl: vm.ZoomOutCommand.Execute(null); break;
            case Key.OemPipe or Key.OemBackslash when ctrl: vm.CenterPictureCommand.Execute(null); break;
            case Key.OemPipe or Key.OemBackslash when !ctrl: vm.BackslashKey(); break;
            case Key.Enter when vm.IsCropping: vm.ApplyCrop(); break;
            case Key.Escape when vm.IsCropping: vm.CancelCrop(); break;
            case Key.Escape when vm.IsExportOpen: vm.ExportPanel = null; break;
            case Key.Escape when vm.IsAnimateOpen: vm.AnimatePanel = null; break;
            case Key.A when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): vm.OpenAnimateCommand.Execute(null); break;
            case Key.Escape when vm.Depth.Cancel(): break;
            case Key.Escape: vm.BackToLibraryCommand.Execute(null); break;
            case Key.X when vm.IsCropping && !ctrl: vm.SwapAspectCommand.Execute(null); break;
            case Key.C when !ctrl && !alt: vm.CropCommand.Execute(null); break;
            case Key.Z when !ctrl && !alt: vm.ToggleZoomCommand.Execute(null); break;
            case Key.F or Key.E or Key.B or Key.M or Key.Q when !ctrl && !alt && !shift:
                vm.ToolCommand.Execute(vm.Tools.FirstOrDefault(t => t.Key == e.Key.ToString()));
                break;
            case >= Key.D0 and <= Key.D5 when !ctrl && !alt && !shift: vm.Rate(((int)(e.Key - Key.D0)).ToString(CultureInfo.InvariantCulture)); break;
            case >= Key.NumPad0 and <= Key.NumPad5 when !ctrl && !alt: vm.Rate(((int)(e.Key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture)); break;
            case Key.Left when !ctrl && !alt && !Viewer.CanPan: vm.PreviousCommand.Execute(null); break;
            case Key.Right when !ctrl && !alt && !Viewer.CanPan: vm.NextCommand.Execute(null); break;
            case Key.PageUp: vm.PreviousCommand.Execute(null); break;
            case Key.PageDown: vm.NextCommand.Execute(null); break;
            case Key.Home when !vm.IsCropping: vm.FirstCommand.Execute(null); break;
            case Key.End when !vm.IsCropping: vm.LastCommand.Execute(null); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ---------------------------------------------------------------------------------------------------------------- converters

    public static readonly IValueConverter OrientationLabel = new FuncValueConverter<bool, string>(p => p ? "Portrait" : "Landscape");

    public static readonly IValueConverter StarOn = new StarConverter();

    sealed class StarConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is int r && int.TryParse(parameter as string, out int n) && r >= n;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    static readonly IBrush TempGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#4f7fd8"), 0), new GradientStop(Color.Parse("#c8c8c8"), 0.45), new GradientStop(Color.Parse("#e8c24a"), 1) },
    };
    static readonly IBrush TintGradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#5fbf5f"), 0), new GradientStop(Color.Parse("#c8c8c8"), 0.5), new GradientStop(Color.Parse("#d05fc8"), 1) },
    };

    /// <summary>Temperature blue → yellow, tint green → magenta (as Lumen colours them); the others plain.</summary>
    public static readonly IValueConverter TrackGradientOf = new FuncValueConverter<string?, IBrush?>(k => k switch
    {
        AdjustmentKeys.Temperature => TempGradient,
        AdjustmentKeys.Tint => TintGradient,
        _ => null,
    });
}
