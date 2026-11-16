using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class CameraView : UserControl
{
    readonly DispatcherTimer _tick;

    public CameraView()
    {
        InitializeComponent();
        CameraScroll.SizeChanged += (_, e) => Vm?.SetViewportWidth(e.NewSize.Width);
        CameraRows.AddHandler(PointerPressedEvent, OnCellPressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: false);
        // the first listing phase shows its elapsed seconds
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Vm?.Tick();
    }

    CameraViewModel? Vm => DataContext as CameraViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick.Stop();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm && CameraScroll.Bounds.Width > 0) vm.SetViewportWidth(CameraScroll.Bounds.Width);
    }

    /// <summary>A click on a cell (its picture, tick box or name) toggles it; Shift extends from the last click.</summary>
    void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        for (var v = e.Source as Visual; v != null && v != CameraRows; v = v.GetVisualParent())
        {
            if (v is Button) return;   // "Select day" handles itself
            if (v is Control { DataContext: CameraItemViewModel item } c && c is Border)
            {
                vm.Toggle(item, (e.KeyModifiers & KeyModifiers.Shift) != 0);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>The cell of a capture when it is realized (tests).</summary>
    public Control? CellOf(CameraItemViewModel item) =>
        CameraRows.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("camcell") && ReferenceEquals(b.DataContext, item));
}
