using Avalonia.Controls;
using Avalonia.Input;

namespace Lux.App.Views;

public partial class GalleryView : UserControl
{
    public GalleryView() => InitializeComponent();

    /// <summary>Give the sample button keyboard focus so the focus ring is visible (screenshots).</summary>
    public void ShowFocusSample() => FocusSample.Focus(NavigationMethod.Tab);
}
