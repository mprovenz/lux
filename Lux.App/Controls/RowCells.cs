using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Lux.App.Controls;

/// <summary>
/// One row of equal-width cells (the grid and the timeline cards). Unlike an inner ItemsControl, it keeps its cell controls when the
/// virtualizing list recycles the row for other captures: only the cells' data contexts change, so scrolling builds no new controls once
/// the screen is filled. Spare cells (a short last row) are hidden, not removed.
/// </summary>
public sealed class RowCells : Panel
{
    public static readonly StyledProperty<IReadOnlyList<object>?> ItemsProperty =
        AvaloniaProperty.Register<RowCells, IReadOnlyList<object>?>(nameof(Items));
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<RowCells, int>(nameof(Columns), 1);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<RowCells, double>(nameof(Gap), 8);
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty = AvaloniaProperty.Register<RowCells, IDataTemplate?>(nameof(ItemTemplate));

    static RowCells() => AffectsMeasure<RowCells>(ColumnsProperty, GapProperty);

    /// <summary>Cell controls built so far (tests: they stay constant while scrolling).</summary>
    public static int Built { get; private set; }
    /// <summary>Measure passes and cell re-binds (diagnostics).</summary>
    public static int Measures { get; private set; }
    public static int Rebinds { get; private set; }

    public IReadOnlyList<object>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemTemplateProperty) { Children.Clear(); Sync(); }
        else if (change.Property == ItemsProperty) Sync();
    }

    void Sync()
    {
        var items = Items;
        var template = ItemTemplate;
        int n = items?.Count ?? 0;
        if (template == null) return;
        for (int i = 0; i < n; i++)
        {
            if (i < Children.Count)
            {
                var c = Children[i];
                if (!ReferenceEquals(c.DataContext, items![i])) Rebinds++;
                c.DataContext = items[i];
                c.IsVisible = true;
            }
            else if (template.Build(items![i]) is { } built)
            {
                built.DataContext = items[i];
                Children.Add(built);
                Built++;
            }
        }
        for (int i = n; i < Children.Count; i++)
        {
            Children[i].IsVisible = false;
            Children[i].DataContext = null;
        }
        InvalidateMeasure();
    }

    double CellWidth(double width)
    {
        int cols = Math.Max(1, Columns);
        return Math.Max(0, Math.Floor((width - Gap * (cols - 1)) / cols));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Measures++;
        double w = double.IsInfinity(availableSize.Width) ? 160 : CellWidth(availableSize.Width);
        double h = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(new Size(w, double.PositiveInfinity));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? w * Math.Max(1, Columns) : availableSize.Width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double w = CellWidth(finalSize.Width);
        int i = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Arrange(new Rect(i * (w + Gap), 0, w, finalSize.Height));
            i++;
        }
        return finalSize;
    }
}
