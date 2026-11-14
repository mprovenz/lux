using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragEnterEvent, OnFolderDragOver);
        AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { if (Vm != null) Vm.IsFolderDropTarget = false; });
        AddHandler(DragDrop.DropEvent, OnFolderDrop);
    }

#pragma warning disable CS0618 // IDataObject: the 11.x drag-and-drop API
    /// <summary>Folders among the dragged items (captures dragged inside the app are not).</summary>
    static IReadOnlyList<string> FoldersIn(DragEventArgs e)
    {
        if (e.Data.Contains(BrowseView.DragFormat)) return Array.Empty<string>();
        return e.Data.GetFiles()?.OfType<Avalonia.Platform.Storage.IStorageFolder>().Select(f => f.TryGetLocalPath()).OfType<string>().ToList()
               ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
#pragma warning restore CS0618

    void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || FoldersIn(e).Count == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        vm.IsFolderDropTarget = true;
        e.Handled = true;
    }

    void OnFolderDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsFolderDropTarget = false;
        var folders = FoldersIn(e);
        if (folders.Count == 0) return;
        e.Handled = true;
        _ = vm.AddFoldersAsync(folders);
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RestoreWindowState();
        if (Vm is { } vm) vm.PropertyChanged += OnVmPropertyChanged;
    }

    bool _relayoutPosted;

    /// <summary>
    /// After a page switch, Avalonia 11.3 can leave a horizontal panel whose own slot did not change arranged at its children's previous
    /// widths (a status text that grew stays clipped; seen in the headless tests). The status bar and the tab strip are re-measured
    /// whenever their texts or the page change — two small panels, so it costs nothing.
    /// </summary>
    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.StatusLeft) or nameof(MainViewModel.StatusAnalysis) or nameof(MainViewModel.StatusCache)
            or nameof(MainViewModel.StatusDevice) or nameof(MainViewModel.QueueTabLabel) or nameof(MainViewModel.CurrentPage) or nameof(MainViewModel.Notice)
            or nameof(MainViewModel.StatusPointer))) return;
        if (_relayoutPosted) return;
        _relayoutPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _relayoutPosted = false;
            StatusLeft.InvalidateMeasure();
            StatusRight.InvalidateMeasure();
            TabsPanel.InvalidateMeasure();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Escape && vm.Dialog != null)
        {
            switch (vm.Dialog)
            {
                case LumenImportViewModel l: l.CancelCommand.Execute(null); break;
                case PromptViewModel p: p.CancelCommand.Execute(null); break;
                case ConfirmViewModel c: c.CancelCommand.Execute(null); break;
                case StorageNoticeViewModel n: n.CancelCommand.Execute(null); break;
                case PatchDialogViewModel d: d.CancelCommand.Execute(null); break;
            }
            e.Handled = true;
            return;
        }
        if (vm.Dialog != null) return;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && vm.ShowLibraryTools)
        {
            FilterBox.Focus(NavigationMethod.Tab);
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------------------------------------------- window state (in the library's settings)

    void RestoreWindowState()
    {
        if (Vm?.Services.Library.Settings is not { } s) return;
        static double? D(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (D(s.GetValue("window.width")) is double w && D(s.GetValue("window.height")) is double h && w >= MinWidth && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }
        if (D(s.GetValue("window.x")) is double x && D(s.GetValue("window.y")) is double y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)x, (int)y);
        }
        if (s.GetValue("window.maximized") == "true") WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);
    }

    public void SaveWindowState()
    {
        if (Vm?.Services.Library.Settings is not { } s) return;
        bool max = WindowState == WindowState.Maximized;
        s.SetValue("window.maximized", max ? "true" : "false");
        if (!max && WindowState == WindowState.Normal)
        {
            s.SetValue("window.width", Width.ToString(CultureInfo.InvariantCulture));
            s.SetValue("window.height", Height.ToString(CultureInfo.InvariantCulture));
            s.SetValue("window.x", Position.X.ToString(CultureInfo.InvariantCulture));
            s.SetValue("window.y", Position.Y.ToString(CultureInfo.InvariantCulture));
        }
    }
}
