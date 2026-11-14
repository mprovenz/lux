using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>A limit is applied when its field is left (as well as on Enter).</summary>
    void OnLimitLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LimitRowViewModel row) row.Commit();
    }

    void OnSwapKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is SettingsViewModel vm) { vm.CommitSwap(); e.Handled = true; }
    }

    void OnSwapLostFocus(object? sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.CommitSwap(); }

    /// <summary>Scroll a section into view (screenshots, links from elsewhere): "storage" or "limits".</summary>
    public void ShowSection(string name)
    {
        Control? target = name == "storage" ? StorageSection : name == "viewer" ? ViewerSection : LimitsSection;
        if (target == null) return;
        var p = target.TranslatePoint(new Avalonia.Point(0, 0), (Control)Scroll.Content!);
        if (p is { } pt) Scroll.Offset = new Avalonia.Vector(0, Math.Max(0, pt.Y - 12));
    }
}
