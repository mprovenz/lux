using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        TreeItems.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        TreeItems.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        TreeItems.AddHandler(DragDrop.DropEvent, OnDrop);
        // real interaction (not programmatic changes) tells the job queue the user is working
        AddHandler(PointerPressedEvent, (_, _) => Vm?.Interaction.Poke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => Vm?.Interaction.Poke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => Vm?.Interaction.Poke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        TreeItems.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Bubble);
        TreeItems.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Bubble);
    }

    LibraryViewModel? Vm => DataContext as LibraryViewModel;

    static TreeNodeViewModel? NodeAt(object? source) =>
        (source as Control)?.FindAncestorOfType<Panel>(includeSelf: true)?.DataContext as TreeNodeViewModel
        ?? (source as Control)?.FindAncestorOfType<Button>(includeSelf: true)?.DataContext as TreeNodeViewModel;

#pragma warning disable CS0618 // IDataObject: the 11.x drag-and-drop API
    static IReadOnlyList<string>? Ids(DragEventArgs e) =>
        e.Data.Get(BrowseView.DragFormat) is string s ? s.Split('\n', StringSplitOptions.RemoveEmptyEntries) : null;
#pragma warning restore CS0618

    /// <summary>Left / Right on a tree row: collapse / expand (Left on an open child moves to its parent).</summary>
    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || NodeAt(e.Source) is not { } node) return;
        if (e.Key == Key.Right && node.HasChildren && !node.IsExpanded) { vm.SetExpanded(node, true); e.Handled = true; }
        else if (e.Key == Key.Left && node.HasChildren && node.IsExpanded) { vm.SetExpanded(node, false); e.Handled = true; }
    }

    /// <summary>The tree's context menu: add a folder anywhere; rescan, show and remove a folder; albums.</summary>
    void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Control src) return;
        var node = NodeAt(e.Source);
        var menu = new ContextMenu();
        void Item(string header, System.Windows.Input.ICommand cmd, object? arg = null) => menu.Items.Add(new MenuItem { Header = header, Command = cmd, CommandParameter = arg });
        switch (node?.Kind)
        {
            case TreeNodeKind.Folder:
                Item("Rescan folder", vm.RescanNodeCommand, node);
                Item("Show in file manager", vm.RevealNodeCommand, node);
                menu.Items.Add(new Separator());
                Item("Add folder…", vm.AddFolderCommand);
                Item("Remove folder from library…", vm.RemoveFolderNodeCommand, node);
                break;
            case TreeNodeKind.Subfolder:
                Item("Rescan folder", vm.RescanNodeCommand, node);
                Item("Show in file manager", vm.RevealNodeCommand, node);
                menu.Items.Add(new Separator());
                Item("Add folder…", vm.AddFolderCommand);
                break;
            case TreeNodeKind.Album:
                Item("New album…", new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.NewAlbum(Array.Empty<string>())));
                Item("Delete album…", vm.DeleteAlbumNodeCommand, node);
                break;
            case TreeNodeKind.Heading when node.Key == "h:albums":
            case TreeNodeKind.NewAlbum:
                Item("New album…", new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.NewAlbum(Array.Empty<string>())));
                break;
            case TreeNodeKind.All:
                Item("Add folder…", vm.AddFolderCommand);
                Item("Rescan all folders", vm.RescanNodeCommand, null);
                break;
            default:
                Item("Add folder…", vm.AddFolderCommand);
                break;
        }
        if (node is { HasChildren: true })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = node.IsExpanded ? "Collapse" : "Expand", Command = vm.ToggleNodeCommand, CommandParameter = node });
        }
        menu.Open(src);
        e.Handled = true;
    }

    /// <summary>The menu the tree would show for a row (tests).</summary>
    internal static IReadOnlyList<string> MenuHeadersFor(TreeNodeViewModel? node) => node?.Kind switch
    {
        TreeNodeKind.Folder => new[] { "Rescan folder", "Show in file manager", "Add folder…", "Remove folder from library…" },
        TreeNodeKind.Subfolder => new[] { "Rescan folder", "Show in file manager", "Add folder…" },
        TreeNodeKind.Album => new[] { "New album…", "Delete album…" },
        TreeNodeKind.All => new[] { "Add folder…", "Rescan all folders" },
        _ => new[] { "Add folder…" },
    };

    TreeNodeViewModel? _over;

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var node = NodeAt(e.Source);
        bool ok = node is { IsAlbum: true } && Ids(e) != null;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (_over != null && _over != node) _over.IsDropTarget = false;
        _over = ok ? node : null;
        if (_over != null) _over.IsDropTarget = true;
        e.Handled = true;
    }

    void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (_over != null) _over.IsDropTarget = false;
        _over = null;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (_over != null) _over.IsDropTarget = false;
        _over = null;
        if (NodeAt(e.Source) is { IsAlbum: true } node && Ids(e) is { } ids && Vm is { } vm) vm.DropOnAlbum(node, ids);
        e.Handled = true;
    }
}
