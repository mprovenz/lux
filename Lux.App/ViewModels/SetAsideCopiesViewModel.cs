using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>One set-aside copy in Settings: the photo, the time, the size and the contents of the copy, with "Restore this copy" and "Remove…".</summary>
public sealed partial class SetAsideCopyRowViewModel(SetAsideCopiesViewModel owner, SetAsideCopy copy) : ObservableObject
{
    public SetAsideCopy Copy { get; } = copy;
    public string Name => Copy.PhotoName ?? "A photo no longer in the library";
    public string Detail
    {
        get
        {
            string what = Copy.HasEdits && Copy.HasAnalysis ? "edits, rating and analysis" : Copy.HasEdits ? "edits and rating" : "analysis";
            return $"Set aside {Copy.SetAsideUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} · {Text.Mb(Copy.Bytes)} · {what}";
        }
    }
    public bool CanRestore => Copy.CanRestore;
    public string RestoreTip => Copy.CanRestore
        ? "Use this copy for the photo again. The photo's current data is set aside in its place, so you can switch back the same way."
        : Copy.PhotoName is null ? "The photo is not in the library, so this copy cannot be put back." : "The photo's file is missing (its drive may not be connected).";
    [RelayCommand] Task Restore() => owner.RestoreAsync(this);
    [RelayCommand] void Remove() => owner.ConfirmRemove(this);
}

/// <summary>
/// Settings → Storage → "Copies set aside": the copies of photos' data that Lux set aside after a conflict. In a conflict, the data next to
/// the photo wins, and Lux keeps the other copy. Restore swaps a copy back in, and the current data is set aside in its place. Remove asks
/// first, then sends the files of the copy to the trash. This block has its own view model.
/// </summary>
public sealed partial class SetAsideCopiesViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly IShell _shell;

    public SetAsideCopiesViewModel(AppServices s, IShell shell) { _s = s; _shell = shell; }

    public ObservableCollection<SetAsideCopyRowViewModel> Copies { get; } = new();
    public bool HasCopies => Copies.Count > 0;
    public string Summary => Copies.Count switch
    {
        0 => "No copies are set aside.",
        1 => "1 copy is set aside.",
        _ => $"{Text.Number(Copies.Count)} copies are set aside.",
    };
    [ObservableProperty] string? _error;
    [ObservableProperty] bool _isBusy;

    public async Task RefreshAsync()
    {
        IReadOnlyList<SetAsideCopy> list;
        try { list = await Task.Run(() => _s.Library.GetSetAsideCopies()).ConfigureAwait(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibraryException) { Error = "The set-aside copies could not be listed: " + e.Message; return; }
        Copies.Clear();
        foreach (var c in list) Copies.Add(new SetAsideCopyRowViewModel(this, c));
        OnPropertyChanged(nameof(HasCopies)); OnPropertyChanged(nameof(Summary));
    }

    internal async Task RestoreAsync(SetAsideCopyRowViewModel row)
    {
        if (IsBusy) return;
        Error = null;
        IsBusy = true;
        try
        {
            await _s.Library.RestoreSetAsideCopyAsync(row.Copy.Path).ConfigureAwait(true);
            _shell.Notify($"{row.Name}: the set-aside copy is in use again; the data it replaced is set aside");
        }
        catch (LibraryException e) { Error = e.Message; }
        catch (NotSupportedException) { Error = "This library cannot put set-aside copies back."; }
        finally { IsBusy = false; }
        await RefreshAsync().ConfigureAwait(true);
    }

    internal void ConfirmRemove(SetAsideCopyRowViewModel row)
    {
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel($"Remove the set-aside copy of {row.Name}?",
            $"The copy set aside {row.Copy.SetAsideUtc.ToLocalTime().ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture)} ({Text.Mb(row.Copy.Bytes)}) goes to the trash. "
            + "The photo and the data it uses now are not touched.",
            "Remove copy",
            () => { _shell.CloseDialog(c!); _ = RemoveAsync(row); },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    internal async Task RemoveAsync(SetAsideCopyRowViewModel row)
    {
        Error = null;
        IsBusy = true;
        try { await _s.Library.RemoveSetAsideCopyAsync(row.Copy.Path).ConfigureAwait(true); }
        catch (LibraryException e) { Error = e.Message; }
        catch (NotSupportedException) { Error = "This library cannot remove set-aside copies."; }
        finally { IsBusy = false; }
        await RefreshAsync().ConfigureAwait(true);
    }
}
