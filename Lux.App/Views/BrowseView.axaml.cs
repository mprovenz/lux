using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lux.App.ViewModels;

namespace Lux.App.Views;

/// <summary>
/// Pointer and keyboard handling of the three browse presentations: click / Ctrl / Shift selection, double-click to Develop, the context
/// menu, dragging captures onto an album, the keyboard cursor (arrows, Page Up / Down, Home / End, Space, Ctrl+A, Esc), rating keys 0–5,
/// and keeping the cursor in view.
/// </summary>
public partial class BrowseView : UserControl
{
    /// <summary>Clipboard / drag format of a list of capture ids (newline-separated).</summary>
    public const string DragFormat = "application/x-lux-captures";

    LibraryViewModel? _vm;
    Point? _pressAt;
    CaptureItemViewModel? _pressItem;
    PointerPressedEventArgs? _pressArgs;

    public BrowseView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, (_, _) => { _pressAt = null; _pressArgs = null; }, RoutingStrategies.Bubble);
        KeyDown += OnKeyDown;
        SizeChanged += (_, e) => _vm?.SetViewportWidth(e.NewSize.Width);
        ContextMenu = new ContextMenu();
        ContextMenu.Opening += (_, _) => BuildContextMenu(ContextMenu);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.ScrollToRowRequested -= ScrollToRow;
        _vm = DataContext as LibraryViewModel;
        if (_vm != null)
        {
            _vm.ScrollToRowRequested += ScrollToRow;
            if (Bounds.Width > 0) _vm.SetViewportWidth(Bounds.Width);
        }
    }

    /// <summary>The items control of the current presentation.</summary>
    public ItemsControl ActiveItems => _vm?.Mode switch { BrowseMode.Timeline => TimelineItems, BrowseMode.List => ListItems, _ => GridItems };
    public ScrollViewer ActiveScroll => _vm?.Mode switch { BrowseMode.Timeline => TimelineScroll, BrowseMode.List => ListScroll, _ => GridScroll };

    void ScrollToRow(int row)
    {
        if (row < 0) return;
        var items = ActiveItems;
        if (row < items.ItemCount) items.ScrollIntoView(row);
    }

    static CaptureItemViewModel? ItemAt(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is StyledElement { DataContext: CaptureItemViewModel item } se && se is Border) return item;
            if (v is BrowseView) break;
        }
        return null;
    }

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm == null) return;
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) != null) return;   // "Select day" etc.
        var item = ItemAt(e.Source);
        Focus(NavigationMethod.Pointer);
        var props = e.GetCurrentPoint(this).Properties;
        if (item == null)
        {
            if (props.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None && e.Source is not Avalonia.Controls.Primitives.ScrollBar) _vm.ClearSelection();
            return;
        }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (props.IsRightButtonPressed) { _vm.ContextClick(item); return; }
        if (!props.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { _vm.Click(item, false, false); _vm.OpenInDevelopCommand.Execute(null); e.Handled = true; return; }
        // A plain press on an already selected item keeps the selection (so it can be dragged); the release does not change it either.
        if (!(item.IsSelected && !ctrl && !shift)) _vm.Click(item, ctrl, shift);
        else _vm.ContextClick(item);
        _pressAt = e.GetPosition(this);
        _pressItem = item;
        _pressArgs = e;
        e.Handled = true;
    }

    async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm == null || _pressAt is not { } at || _pressItem == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressAt = null; return; }
        var d = e.GetPosition(this) - at;
        if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
        _pressAt = null;
        var ids = _vm.Targets();
        if (ids.Count == 0) return;
#pragma warning disable CS0618 // DataObject: the 11.x drag-and-drop API
        var data = new DataObject();
        data.Set(DragFormat, string.Join('\n', ids));
        data.Set(DataFormats.Text, string.Join('\n', ids.Select(id => _vm.Items.FirstOrDefault(i => i.Id == id)?.Name ?? id)));
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Link); }
        catch (Exception) { /* no drag support on this platform */ }
#pragma warning restore CS0618
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool horizontal = _vm.Mode != BrowseMode.List;
        int page = _vm.PageItems(ActiveScroll.Viewport.Height);
        switch (e.Key)
        {
            case Key.Left when horizontal: _vm.MoveCursor(-1, shift, ctrl); break;
            case Key.Right when horizontal: _vm.MoveCursor(1, shift, ctrl); break;
            case Key.Up: _vm.MoveVertical(-1, shift, ctrl); break;
            case Key.Down: _vm.MoveVertical(1, shift, ctrl); break;
            case Key.PageUp: _vm.MoveVertical(-page, shift, ctrl); break;
            case Key.PageDown: _vm.MoveVertical(page, shift, ctrl); break;
            case Key.Home: _vm.MoveTo(0, shift, ctrl); break;
            case Key.End: _vm.MoveTo(int.MaxValue, shift, ctrl); break;
            case Key.Space: _vm.ToggleCursor(); break;
            case Key.A when ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift): _vm.SelectAll(); break;
            case Key.E when ctrl: _vm.OpenExport(); break;
            case Key.C when ctrl: _vm.CopyAdjustmentsCommand.Execute(null); break;
            case Key.V when ctrl: _vm.PasteAdjustmentsCommand.Execute(null); break;
            case Key.Escape when _vm.IsExportOpen: _vm.CloseExport(); break;
            case Key.Escape when _vm.IsAnimateOpen: _vm.CloseAnimate(); break;
            case Key.A when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _vm.OpenAnimate(); break;
            case Key.Escape: _vm.ClearSelection(); break;
            case Key.Enter: _vm.OpenInDevelopCommand.Execute(null); break;
            case Key.G when e.KeyModifiers == KeyModifiers.None: _vm.Mode = BrowseMode.Grid; break;
            case Key.T when e.KeyModifiers == KeyModifiers.None: _vm.Mode = BrowseMode.Timeline; break;
            case Key.L when e.KeyModifiers == KeyModifiers.None: _vm.Mode = BrowseMode.List; break;
            case >= Key.D0 and <= Key.D5 when !ctrl: _vm.SetRating(e.Key - Key.D0); break;
            case >= Key.NumPad0 and <= Key.NumPad5 when !ctrl: _vm.SetRating(e.Key - Key.NumPad0); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------------------------------------ context menu

    void BuildContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_vm == null) return;
        var ids = _vm.Targets();
        if (ids.Count == 0) { menu.Items.Add(new MenuItem { Header = "Select all", Command = _vm.SelectAllCommand, InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) }); return; }
        bool one = ids.Count == 1;
        var first = _vm.Items.FirstOrDefault(i => i.Id == ids[0]);
        menu.Items.Add(new MenuItem { Header = "Open in Develop", Command = _vm.OpenInDevelopCommand, InputGesture = new KeyGesture(Key.Enter) });
        menu.Items.Add(new MenuItem { Header = one ? "Export…" : $"Export {ids.Count}…", Command = _vm.OpenExportCommand, InputGesture = new KeyGesture(Key.E, KeyModifiers.Control) });
        menu.Items.Add(new MenuItem { Header = one ? "Animate…" : $"Animate {ids.Count}…", Command = _vm.OpenAnimateCommand, InputGesture = new KeyGesture(Key.A, KeyModifiers.Control | KeyModifiers.Shift) });
        menu.Items.Add(new MenuItem { Header = "Copy adjustments…", Command = _vm.CopyAdjustmentsCommand, IsEnabled = _vm.CanCopyAdjustments, InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) });
        menu.Items.Add(new MenuItem { Header = one ? "Paste adjustments" : $"Paste adjustments to {ids.Count}", Command = _vm.PasteAdjustmentsCommand, IsEnabled = _vm.CanPasteAdjustments, InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) });
        if (_vm.HasPresets)
        {
            var presets = new MenuItem { Header = one ? "Apply preset" : $"Apply preset to {ids.Count}" };
            foreach (var p in _vm.Presets) presets.Items.Add(new MenuItem { Header = p.Name, Command = _vm.ApplyPresetToSelectionCommand, CommandParameter = p });
            menu.Items.Add(presets);
        }
        menu.Items.Add(new Separator());
        var rating = new MenuItem { Header = "Rating" };
        for (int r = 0; r <= 5; r++)
            rating.Items.Add(new MenuItem { Header = r == 0 ? "No rating" : Text.Stars(r), Command = _vm.RateCommand, CommandParameter = r.ToString(), InputGesture = new KeyGesture(Key.D0 + r) });
        menu.Items.Add(rating);
        var album = new MenuItem { Header = "Add to album" };
        foreach (var a in _vm.Albums())
        {
            var aa = a;
            var mi = new MenuItem { Header = a.Name };
            mi.Click += (_, _) => _vm.AddToAlbum(aa.Id, ids, aa.Name);
            album.Items.Add(mi);
        }
        if (album.Items.Count > 0) album.Items.Add(new Separator());
        var newAlbum = new MenuItem { Header = "New album…" };
        newAlbum.Click += (_, _) => _vm.NewAlbum(ids);
        album.Items.Add(newAlbum);
        menu.Items.Add(album);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Analyse now", Command = _vm.AnalyseSelectedCommand });
        menu.Items.Add(new MenuItem { Header = "Upgrade to full detail", Command = _vm.UpgradeSelectedCommand });
        menu.Items.Add(new MenuItem { Header = "Release full detail", Command = _vm.ReleaseSelectedCommand });
        menu.Items.Add(new MenuItem { Header = "Import edits from Lumen…", Command = _vm.ImportLumenForSelectionCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Show in folder", Command = _vm.RevealSelectedCommand, IsEnabled = one && first is { Missing: false } });
        menu.Items.Add(new MenuItem { Header = one ? "Remove from library…" : $"Remove {ids.Count} from library…", Command = _vm.RemoveFromLibraryCommand });
    }
}
