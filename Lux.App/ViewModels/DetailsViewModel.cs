using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;

namespace Lux.App.ViewModels;

/// <summary>The details panel: the cursor's capture (or the first selected), its rows, the quality selector and the preview, which shows
/// "Processing image" while a better quality of the capture is being made.</summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    readonly LibraryViewModel _owner;
    readonly AppServices _s;

    public DetailsViewModel(LibraryViewModel owner, AppServices s)
    {
        _owner = owner;
        _s = s;
        Quality = new QualitySelectorViewModel(owner.QualityChanges);
    }

    [ObservableProperty] CaptureItemViewModel? _current;
    [ObservableProperty] string _albumsText = "";

    public QualitySelectorViewModel Quality { get; }
    public bool HasCurrent => Current != null;
    public bool CurrentIsFull => Current?.IsFull == true;
    public string ExportLabel => _owner.ExportLabel;
    public string EmptyText => _owner.TotalCount == 0 ? "No captures here" : "Select a capture to see its details";

    /// <summary>A better quality than the one kept is being made (or waits in the Queue): the preview shows the placeholder.</summary>
    public bool PreviewProcessing => Current?.Quality is { Target: { } t } st && t > st.Cached;
    public double PreviewProgress => Current?.Quality is { IsRunning: true } st ? st.Progress : double.NaN;
    public bool HasSuperseded => Current?.HasSuperseded == true;

    public void Refresh()
    {
        var cur = _owner.CursorItem is { IsSelected: true } c ? c : _owner.Items.FirstOrDefault(i => i.IsSelected);
        if (cur != Current) Current = cur;
        else OnPropertyChanged(nameof(Current));
        Quality.Item = cur;
        foreach (var p in new[] { nameof(HasCurrent), nameof(CurrentIsFull), nameof(ExportLabel), nameof(EmptyText), nameof(PreviewProcessing),
                                  nameof(PreviewProgress), nameof(HasSuperseded) })
            OnPropertyChanged(p);
        AlbumsText = cur == null ? "" : string.Join(", ", _s.Library.GetAlbumsOf(cur.Id).Select(a => a.Name));
    }

    /// <summary>The capture's job moved on (progress, start, end): the preview and the selector follow.</summary>
    public void JobProgress()
    {
        Quality.Refresh();
        OnPropertyChanged(nameof(PreviewProcessing));
        OnPropertyChanged(nameof(PreviewProgress));
    }

    /// <summary>"Show" on the kept-aside notice: the older copy in the file manager.</summary>
    [RelayCommand]
    void ShowSuperseded()
    {
        if (Current?.Item.SupersededContextPath is { } p) _s.Platform.Reveal(p);
    }
}
