using Avalonia.Controls;
using Avalonia.Input;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class AnimationPlayer : UserControl
{
    public AnimationPlayer()
    {
        InitializeComponent();
        FrameView.Clicked += (x, y) => (DataContext as AnimationPlayerViewModel)?.Click(x, y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not AnimationPlayerViewModel vm || e.Handled) return;
        switch (e.Key)
        {
            case Key.Space: vm.TogglePlayCommand.Execute(null); e.Handled = true; break;
            case Key.Left: vm.StepBackCommand.Execute(null); e.Handled = true; break;
            case Key.Right: vm.StepForwardCommand.Execute(null); e.Handled = true; break;
        }
    }
}
