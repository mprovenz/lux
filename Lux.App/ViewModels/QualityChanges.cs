using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>
/// The extra-storage notice (going up) or the release confirmation (going down) for one capture or a selection: the total size, the
/// number of captures, and — going up — "Don't ask again" bound to <see cref="ILibrarySettings.DontAskAboutExtraStorage"/>. Shown inline
/// by the quality selector and as a dialog by the selection bar and the context menu.
/// </summary>
public sealed partial class StorageNoticeViewModel : ObservableObject
{
    readonly Action<bool> _done;

    public StorageNoticeViewModel(bool upgrade, string title, string text, string action, IReadOnlyList<string> ids, ViewQuality target, Action<bool> done)
    {
        IsUpgrade = upgrade; Title = title; Text = text; Action = action; Ids = ids; Target = target; _done = done;
    }

    public bool IsUpgrade { get; }
    public string Title { get; }
    public string Text { get; }
    public string Action { get; }
    public IReadOnlyList<string> Ids { get; }
    public ViewQuality Target { get; }
    [ObservableProperty] bool _dontAskAgain;

    [RelayCommand] void Confirm() => _done(true);
    [RelayCommand] void Cancel() => _done(false);
}

/// <summary>
/// Raising or lowering the kept quality of captures through <see cref="ILibrary.RequestQuality"/>, with the notice first when the library
/// says to ask (up) or always (down). One implementation for the quality selector, the selection bar and the context menu.
/// </summary>
public sealed class QualityChanges
{
    readonly AppServices _s;
    readonly Action<string> _notify;

    public QualityChanges(AppServices s, Action<string> notify)
    {
        _s = s;
        _notify = notify;
    }

    static CacheLevel LevelOf(ViewQuality q) => q switch { ViewQuality.FullDetail => CacheLevel.Full, ViewQuality.Linear13 => CacheLevel.Linear13, _ => CacheLevel.Geometry };

    /// <summary>Work out the change for <paramref name="items"/> (off the UI thread). Returns the notice to show, or null when the change was
    /// applied at once (going up with "Don't ask again" chosen) or there is nothing to change. <paramref name="present"/> shows the notice;
    /// it is called back with the user's answer through the notice's commands.</summary>
    public async Task<StorageNoticeViewModel?> PrepareAsync(IReadOnlyList<CaptureItem> items, ViewQuality target, Action<StorageNoticeViewModel?> closed,
                                                           CancellationToken cancel = default)
    {
        var lib = _s.Library;
        var movable = items.Where(i => !i.Missing && !i.Unreadable).ToList();
        var up = movable.Where(i => i.BestQuality < target).ToList();
        var down = movable.Where(i => i.BestQuality > target).ToList();
        bool upgrade = up.Count > 0;
        var change = upgrade ? up : down;
        if (change.Count == 0) { _notify(items.Count == 1 ? $"{items[0].Name} is already at {QualityRules.Name(target)}" : $"All {items.Count} are already at {QualityRules.Name(target)}"); return null; }
        var ids = change.Select(i => i.Id).ToList();
        (long bytes, bool ask) = await Task.Run(() =>
        {
            if (!upgrade) return (lib.EstimateCache(ids, LevelOf(target)).BytesToFree, true);
            long add = 0; bool any = false;
            foreach (var id in ids)
            {
                cancel.ThrowIfCancellationRequested();
                var st = lib.GetQualityState(id, target);
                add += st.Estimate.BytesToAdd;
                any |= st.AskAboutStorage;
            }
            return (add, any);
        }, cancel).ConfigureAwait(true);
        if (upgrade && !ask) { Apply(ids, target, change); return null; }

        int n = ids.Count;
        string name = n == 1 ? change[0].Name : $"{Text.Number(n)} captures";
        string title, text, action;
        if (upgrade)
        {
            title = target == ViewQuality.FullDetail ? (n == 1 ? "Keep full detail?" : $"Keep full detail of {name}?") : (n == 1 ? "Keep the 13 MP copy?" : $"Keep 13 MP copies of {name}?");
            text = target == ViewQuality.FullDetail
                ? (n == 1 ? $"Full detail keeps a larger copy of this photo: about {Text.Mb(bytes)} more on disk. Full-size exports and close-up views then start at once."
                          : $"Full detail keeps larger copies of these {Text.Number(n)} photos: about {Text.Mb(bytes)} more on disk in all. Full-size exports and close-up views then start at once.")
                : (n == 1 ? $"The 13 MP copy is kept after this photo is analyzed: about {Text.Mb(bytes)} on disk. The photo then opens straight away and the depth tools are ready."
                          : $"The 13 MP copies are kept after these {Text.Number(n)} photos are analyzed: about {Text.Mb(bytes)} on disk in all. They then open straight away and the depth tools are ready.");
            action = target == ViewQuality.FullDetail ? "Keep full detail" : "Keep 13 MP";
        }
        else
        {
            bool fromFull = change.All(i => i.BestQuality == ViewQuality.FullDetail) && target == ViewQuality.Linear13;
            title = n == 1 ? (fromFull ? "Release full detail?" : "Release the working copy?") : (fromFull ? $"Release full detail of {name}?" : $"Release the working copies of {name}?");
            text = fromFull
                ? (n == 1 ? $"Release the full-detail copy? It frees about {Text.Mb(bytes)}. Lux can make it again whenever it is needed (about a minute)."
                          : $"Release the full-detail copies of these {Text.Number(n)} photos? It frees about {Text.Mb(bytes)}. Lux can make them again whenever they are needed (about a minute each).")
                : (n == 1 ? $"Release the working copy? It frees about {Text.Mb(bytes)}. The depth and analysis are kept; the copy can be made again at any time."
                          : $"Release the working copies of these {Text.Number(n)} photos? It frees about {Text.Mb(bytes)}. The depth and analysis are kept; the copies can be made again at any time.");
            action = "Release";
        }
        StorageNoticeViewModel? notice = null;
        notice = new StorageNoticeViewModel(upgrade, title, text, action, ids, target, ok =>
        {
            if (ok)
            {
                if (upgrade && notice!.DontAskAgain) { var set = lib.Settings; _ = Task.Run(() => set.DontAskAboutExtraStorage = true); }
                Apply(ids, target, change);
            }
            closed(notice);
        });
        return notice;
    }

    void Apply(IReadOnlyList<string> ids, ViewQuality target, IReadOnlyList<CaptureItem> items)
    {
        var lib = _s.Library;
        var notify = _notify;
        _ = Task.Run(() => { foreach (var id in ids) lib.RequestQuality(id, target); }).ContinueWith(t =>
        {
            if (t.Exception?.InnerException is { } e) Avalonia.Threading.Dispatcher.UIThread.Post(() => notify(Text.Plain(e)));
        }, TaskScheduler.Default);
        bool up = items[0].BestQuality < target;
        string who = ids.Count == 1 ? items[0].Name : $"{Text.Number(ids.Count)} captures";
        _notify(up ? $"{QualityRules.Name(target)} for {who} — see Queue" : $"Releasing to {QualityRules.Name(target)} for {who}");
    }
}
