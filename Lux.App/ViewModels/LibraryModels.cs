using CommunityToolkit.Mvvm.ComponentModel;
using Lux.Library;

namespace Lux.App.ViewModels;

public enum BrowseMode { Grid, Timeline, List }

public enum TreeNodeKind { All, Folder, Subfolder, Heading, Album, Smart, NewAlbum }

/// <summary>
/// A row of the folder tree: the root ("All captures"), an indexed folder, a sub-folder, a group heading (Albums, Smart filters) or an
/// item of a group. Levels are indented 16 px each; a row with children has an expand / collapse chevron, and a collapsed row hides
/// everything below it.
/// </summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    public const double IndentStep = 16, IndentBase = 8, ChevronWidth = 20;
    bool _isSelected, _isDropTarget, _isExpanded = true, _isShown = true;

    public required TreeNodeKind Kind { get; init; }
    public required string Label { get; init; }
    public string CountText { get; init; } = "";
    /// <summary>0 = the root and the group headings, 1 = indexed folders and group items, 2 + = sub-folders.</summary>
    public int Depth { get; init; }
    public bool IsHeading { get; init; }
    /// <summary>Settings key of the scope (view style per folder, expand state).</summary>
    public string Key { get; init; } = "";
    /// <summary>The scope's query (scope members and, for smart filters, the filter).</summary>
    public CaptureQuery? Scope { get; init; }
    public long? AlbumId { get; init; }
    public long? FolderId { get; init; }
    public string? Path { get; init; }
    public TreeNodeViewModel? Parent { get; init; }
    /// <summary>An extra line for the tooltip. For a folder that cannot be found, it says why and what to do.</summary>
    public string? Note { get; init; }
    public string ToolTipText => Note is null ? Path ?? Label : (Path ?? Label) + "\n" + Note;

    public bool HasChildren { get; internal set; }
    public bool IsSelectable => Kind is not (TreeNodeKind.Heading or TreeNodeKind.NewAlbum);
    public bool IsGroup => Kind == TreeNodeKind.Heading;
    public bool IsRoot => Kind == TreeNodeKind.All;
    public bool IsFolder => Kind == TreeNodeKind.Folder;
    public bool IsAlbum => Kind == TreeNodeKind.Album;
    public bool IsNewAlbum => Kind == TreeNodeKind.NewAlbum;
    /// <summary>The row's text starts after the indent and the chevron column.</summary>
    public Avalonia.Thickness Padding => new(IndentBase + Depth * IndentStep + ChevronWidth, 0, 12, 0);
    public Avalonia.Thickness ChevronMargin => new(IndentBase + Depth * IndentStep, 0, 0, 0);
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsDropTarget { get => _isDropTarget; set => SetProperty(ref _isDropTarget, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    /// <summary>Every row above it is expanded.</summary>
    public bool IsShown { get => _isShown; internal set => SetProperty(ref _isShown, value); }
    public string AccessibleName => (CountText.Length > 0 ? $"{Label}, {CountText} captures" : Label) + (HasChildren ? (IsExpanded ? ", expanded" : ", collapsed") : "");
}

/// <summary>A filter chip above the browse views (exclusive: one is on).</summary>
public sealed class ChipViewModel : ObservableObject
{
    bool _isOn;
    string _label;

    public ChipViewModel(SmartFilter? filter, string label, int? minRating = null)
    {
        Filter = filter;
        _label = label;
        MinRating = minRating;
    }

    public SmartFilter? Filter { get; }
    public int? MinRating { get; }
    public string BaseLabel => Filter switch
    {
        SmartFilter.NotAnalysed => "Not analysed",
        _ => _label,
    };
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public bool IsOn { get => _isOn; set => SetProperty(ref _isOn, value); }

    public CaptureQuery Apply(CaptureQuery q) =>
        MinRating is int r ? q with { MinRating = r } : Filter is SmartFilter f ? q.With(f) : q;
}

/// <summary>Geometry shared by every realised row of a view (a width change updates cells without rebuilding rows).</summary>
public sealed class CellMetrics : ObservableObject
{
    int _columns = 1;
    double _cellWidth = 120, _imageHeight = 82;

    public int Columns { get => _columns; set => SetProperty(ref _columns, value); }
    public double CellWidth { get => _cellWidth; set => SetProperty(ref _cellWidth, value); }
    public double ImageHeight { get => _imageHeight; set => SetProperty(ref _imageHeight, value); }
}

/// <summary>One row of the compact grid.</summary>
public sealed class GridRowViewModel(IReadOnlyList<CaptureItemViewModel> cells, CellMetrics metrics, int row)
{
    public IReadOnlyList<CaptureItemViewModel> Cells { get; } = cells;
    public CellMetrics Metrics { get; } = metrics;
    public int Row { get; } = row;
}

/// <summary>A day heading of the timeline.</summary>
public sealed class TimelineHeaderViewModel(LibraryViewModel owner, string title, string sub, int firstIndex, int count)
{
    public string Title { get; } = title;
    public string Sub { get; } = sub;
    public int FirstIndex { get; } = firstIndex;
    public int Count { get; } = count;
    public bool IsFirst => FirstIndex == 0;
    public CommunityToolkit.Mvvm.Input.IRelayCommand SelectDayCommand { get; } =
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => owner.SelectRange(firstIndex, count));
}

/// <summary>A row of cards of one day.</summary>
public sealed class TimelineRowViewModel(IReadOnlyList<CaptureItemViewModel> cells, CellMetrics metrics, bool lastOfDay)
{
    public IReadOnlyList<CaptureItemViewModel> Cells { get; } = cells;
    public CellMetrics Metrics { get; } = metrics;
    public bool LastOfDay { get; } = lastOfDay;
}

/// <summary>A sort choice of the sort menu.</summary>
public sealed record SortChoice(string Label, CaptureSort Sort, bool Descending);
