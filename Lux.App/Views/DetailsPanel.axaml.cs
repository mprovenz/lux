using Avalonia.Controls;
using Avalonia.Interactivity;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class DetailsPanel : UserControl
{
    public DetailsPanel() => InitializeComponent();

    void OnAddToAlbum(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var ids = vm.Targets();
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var a in vm.Albums())
        {
            var album = a;
            var mi = new MenuItem { Header = $"{a.Name}  ·  {a.CaptureCount}" };
            mi.Click += (_, _) => vm.AddToAlbum(album.Id, ids, album.Name);
            flyout.Items.Add(mi);
        }
        if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());
        var create = new MenuItem { Header = "New album…" };
        create.Click += (_, _) => vm.NewAlbum(ids);
        flyout.Items.Add(create);
        flyout.ShowAt(AlbumButton);
    }
}
