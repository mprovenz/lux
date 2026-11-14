using Avalonia;
using Avalonia.Controls;

namespace Lux.App.Views;

public partial class QualitySelector : UserControl
{
    /// <summary>The viewer bar's form: one line (label and options); the caption, progress and notice are the page's.</summary>
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<QualitySelector, bool>(nameof(Compact));
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    public QualitySelector() => InitializeComponent();
}
