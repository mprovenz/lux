using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Lux.App.ViewModels;

namespace Lux.App.Views;

public partial class AnimationPlayer : UserControl
{
    AnimationPlayerViewModel? _vm;
    bool _frameAsked;

    public AnimationPlayer()
    {
        InitializeComponent();
        FrameView.Clicked += (x, y) => (DataContext as AnimationPlayerViewModel)?.Click(x, y);
        FrameView.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) PushSize(); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnPlayerChanged;
        _vm = DataContext as AnimationPlayerViewModel;
        if (_vm != null) _vm.PropertyChanged += OnPlayerChanged;
        PushSize();
        Drive();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PushSize();
        Drive();
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnimationPlayerViewModel.IsRunning)) Drive();
    }

    /// <summary>The player makes its frames for the picture area's size in screen pixels.</summary>
    void PushSize()
    {
        if (_vm is null || FrameView.Bounds.Width <= 0 || FrameView.Bounds.Height <= 0) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _vm.SetDisplaySize(FrameView.Bounds.Width * scale, FrameView.Bounds.Height * scale);
    }

    /// <summary>While the player plays, ask it for the frame that is due once per rendered frame. The frame changes then follow the
    /// screen's own rhythm.</summary>
    void Drive()
    {
        if (_frameAsked || _vm is not { IsRunning: true } || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameAsked = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameAsked = false;
            FramesDriven++;
            if (_vm?.Tick() == true) Drive();
        });
    }

    /// <summary>Rendered frames on which this view asked the player for the frame that is due (tests).</summary>
    public int FramesDriven { get; private set; }

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
