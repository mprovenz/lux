using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lux.App.Views;

/// <summary>A placeholder page: a heading and a plain-language explanation.</summary>
public sealed class PlaceholderView : UserControl
{
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<PlaceholderView, string?>(nameof(Heading));
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<PlaceholderView, string?>(nameof(Body));

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    readonly TextBlock _heading = new() { Classes = { "title" } };
    readonly TextBlock _body = new() { Classes = { "secondary", "wrap" }, FontSize = 14, LineHeight = 21 };

    public PlaceholderView()
    {
        Content = new StackPanel
        {
            MaxWidth = 640, Spacing = 10, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { _heading, _body },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeadingProperty) _heading.Text = Heading;
        else if (change.Property == BodyProperty) _body.Text = Body;
    }
}
