using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>
/// The quality selector (user design; the details panel now, the viewer next): the quality a capture can be shown at now — Preview, 13 MP
/// or Full detail — with one-click changes. The notice before a change (extra storage up, confirmation down) comes from the shared
/// <see cref="QualityChanges"/> (the selection bar and the context menu use the same) and is shown inline here. While a change runs, the
/// target shows its progress.
/// </summary>
public sealed partial class QualitySelectorViewModel : ObservableObject
{
    readonly QualityChanges _changes;
    CaptureItemViewModel? _item;
    CancellationTokenSource? _cts;

    public QualitySelectorViewModel(QualityChanges changes) => _changes = changes;

    public CaptureItemViewModel? Item
    {
        get => _item;
        set
        {
            if (_item == value) { Refresh(); return; }
            if (_item != null) _item.PropertyChanged -= OnItemChanged;
            _item = value;
            if (_item != null) _item.PropertyChanged += OnItemChanged;
            Notice = null;
            Refresh();
        }
    }

    /// <summary>The selector follows its capture: a new record from the library, or a job that starts, moves or ends. Each page has its own
    /// selector, and each one shows the same state.</summary>
    void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(CaptureItemViewModel.Quality)) Refresh();
    }

    QualityStatus? Status => _item?.Quality;

    public void Refresh()
    {
        foreach (var p in new[] { nameof(IsPreview), nameof(IsStandard), nameof(IsFull), nameof(StandardKept), nameof(FullKept), nameof(Selected),
                                  nameof(StatusText), nameof(Caption), nameof(IsRunning), nameof(Progress), nameof(CanChange), nameof(HasItem), nameof(IsPending),
                                  nameof(CompactLabel), nameof(CompactTip) })
            OnPropertyChanged(p);
    }

    public bool HasItem => _item != null;

    // ONE option reads as selected — what the user chose for this capture (the quality being made when a
    // change is under way, else the one kept). What is kept on disk is a small tick (only when it differs from the choice, i.e. while a change
    // runs), and what is on screen is said in words in the caption — never three marks on the control.
    /// <summary>The quality chosen for the capture: the one a change is bringing it to, else the one it keeps.</summary>
    public ViewQuality? Selected => Status is { } st ? st.Target ?? st.Cached : null;
    public bool IsPreview => Selected == ViewQuality.Preview;
    public bool IsStandard => Selected == ViewQuality.Linear13;
    public bool IsFull => Selected == ViewQuality.FullDetail;
    /// <summary>The kept copy's tick: on the tier still on disk while a change to another runs.</summary>
    public bool StandardKept => Status is { IsPending: true, Cached: ViewQuality.Linear13 };
    public bool FullKept => Status is { IsPending: true, Cached: ViewQuality.FullDetail };
    public bool IsPending => Status?.IsPending == true;
    public bool IsRunning => Status?.IsRunning == true;
    public double Progress => Status?.Progress ?? 0;
    public bool CanChange => _item is { Missing: false, Unreadable: false } && Status?.IsPending == false;

    /// <summary>What is kept and what is being made: "Full detail is being made · 40 % · 13 MP kept", "Full detail kept · 270 MB".</summary>
    public string StatusText
    {
        get
        {
            if (Status is not { } st || _item == null) return "";
            if (st.Target is { } t)
            {
                string kept = st.Cached == ViewQuality.Preview ? "" : $" · {QualityRules.Name(st.Cached)} kept";
                string what = t > st.Cached ? $"{QualityRules.Name(t)} is being made" : $"Going back to {QualityRules.Name(t)}";
                return (st.IsRunning ? $"{what} · {Text.Percent(st.Progress)}" : $"{what} · waiting in the Queue") + (t > st.Cached ? kept : "");
            }
            return st.Cached switch
            {
                ViewQuality.Preview => "Preview: made from the capture each time; no copy kept",
                _ => $"{QualityRules.Name(st.Cached)} kept · {Text.Mb(_item.Item.CacheBytes)}",
            };
        }
    }

    /// <summary>The compact selector's label (it has no caption): "Quality", or while a change runs what is being made ("Full detail is being
    /// made · 40 %", "Going back to 13 MP · waiting in the Queue"). The tick of the copy kept meanwhile then has its reason beside it.</summary>
    public string CompactLabel
    {
        get
        {
            if (Status is not { Target: { } t } st) return "Quality";
            string what = t > st.Cached ? $"{QualityRules.Name(t)} is being made" : $"Going back to {QualityRules.Name(t)}";
            return st.IsRunning ? $"{what} · {Text.Percent(st.Progress)}" : $"{what} · waiting in the Queue";
        }
    }

    /// <summary>The compact label's tooltip while a change runs: what is kept and what is being made, in full.</summary>
    public string? CompactTip => IsPending ? StatusText : null;

    /// <summary>The viewer's own words for what is on screen ("Showing 13 MP"), set by the page that shows the picture; null = no picture.</summary>
    public string? OnScreen { get => _onScreen; set { if (_onScreen == value) return; _onScreen = value; OnPropertyChanged(nameof(Caption)); } }
    string? _onScreen;

    /// <summary>The caption under the selector: what is on screen (when a picture is shown) and what is kept or being made.</summary>
    public string Caption => OnScreen is { Length: > 0 } on ? $"{on} · {StatusText}" : StatusText;

    // ---- the notice (shared with the selection bar and the context menu), shown inline
    [ObservableProperty] StorageNoticeViewModel? _notice;
    partial void OnNoticeChanged(StorageNoticeViewModel? value)
    {
        OnPropertyChanged(nameof(AskOpen)); OnPropertyChanged(nameof(ReleaseOpen)); OnPropertyChanged(nameof(AskText)); OnPropertyChanged(nameof(ReleaseText));
        OnPropertyChanged(nameof(HasNotice));
    }

    public bool HasNotice => Notice != null;
    public bool AskOpen => Notice is { IsUpgrade: true };
    public bool ReleaseOpen => Notice is { IsUpgrade: false };
    public string AskText => Notice is { IsUpgrade: true } n ? n.Text : "";
    public string ReleaseText => Notice is { IsUpgrade: false } n ? n.Text : "";
    public bool DontAskAgain { get => Notice?.DontAskAgain == true; set { if (Notice != null) Notice.DontAskAgain = value; } }

    [RelayCommand]
    async Task Choose(ViewQuality q)
    {
        if (_item == null || Status is not { } st || st.IsPending || q == st.Cached || !CanChange) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var item = _item;
        StorageNoticeViewModel? notice;
        try { notice = await _changes.PrepareAsync(new[] { item.Item }, q, n => { if (Notice == n) Notice = null; }, cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || item != _item) return;
        Notice = notice;
    }

    [RelayCommand] void ConfirmAsk() => Notice?.ConfirmCommand.Execute(null);
    [RelayCommand] void CancelAsk() => Notice?.CancelCommand.Execute(null);
    [RelayCommand] void ConfirmRelease() => Notice?.ConfirmCommand.Execute(null);
    [RelayCommand] void CancelRelease() => Notice?.CancelCommand.Execute(null);
}
