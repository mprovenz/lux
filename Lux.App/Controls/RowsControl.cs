using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Lux.App.ViewModels;

namespace Lux.App.Controls;

/// <summary>
/// The virtualising list of the browse views. Its containers are the rows themselves — a <see cref="RowCells"/> for a grid or card row,
/// the built row for a list item, the built heading for a timeline day — and the virtualising panel recycles them per kind (recycle key),
/// so scrolling re-binds existing controls instead of building new ones (a plain ItemsControl recycles its ContentPresenter containers but
/// rebuilds their content).
/// </summary>
public sealed class RowsControl : ItemsControl
{
    public static readonly StyledProperty<IDataTemplate?> CellTemplateProperty = AvaloniaProperty.Register<RowsControl, IDataTemplate?>(nameof(CellTemplate));
    public static readonly StyledProperty<IDataTemplate?> HeaderTemplateProperty = AvaloniaProperty.Register<RowsControl, IDataTemplate?>(nameof(HeaderTemplate));
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<RowsControl, double>(nameof(Gap), 8);
    public static readonly StyledProperty<Thickness> RowMarginProperty = AvaloniaProperty.Register<RowsControl, Thickness>(nameof(RowMargin));

    static readonly object RowKey = new(), HeaderKey = new(), ItemKey = new();

    /// <summary>Row containers built so far (tests: stays near the number on screen while scrolling).</summary>
    public static int ContainersBuilt { get; private set; }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <summary>The template of one capture: a grid cell / card inside a row, or a whole list row.</summary>
    public IDataTemplate? CellTemplate { get => GetValue(CellTemplateProperty); set => SetValue(CellTemplateProperty, value); }
    /// <summary>The template of a timeline day heading.</summary>
    public IDataTemplate? HeaderTemplate { get => GetValue(HeaderTemplateProperty); set => SetValue(HeaderTemplateProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public Thickness RowMargin { get => GetValue(RowMarginProperty); set => SetValue(RowMarginProperty, value); }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = item switch { TimelineHeaderViewModel or CameraDayHeaderViewModel => HeaderKey, CaptureItemViewModel => ItemKey, _ => RowKey };
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        ContainersBuilt++;
        if (recycleKey == HeaderKey && HeaderTemplate?.Build(item) is { } header) return header;
        if (recycleKey == ItemKey && CellTemplate?.Build(item) is { } row) return row;
        return new RowCells { ItemTemplate = CellTemplate, Gap = Gap, Margin = RowMargin };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        container.DataContext = item;
        if (container is RowCells cells)
        {
            switch (item)
            {
                case GridRowViewModel g: cells.Columns = g.Metrics.Columns; cells.Items = g.Cells; break;
                case TimelineRowViewModel t: cells.Columns = t.Metrics.Columns; cells.Items = t.Cells; break;
                case CameraRowViewModel c: cells.Columns = c.Metrics.Columns; cells.Items = c.Cells; break;
            }
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        // keep the row's controls for the next item; only its data context changes when it is prepared again
    }
}
