using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;

namespace Lux.App.ViewModels;

/// <summary>Settings → Updates: the version of this copy, the state of the app's own update, and the two actions.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    readonly IUpdateService _updates;
    readonly Action _restart;

    /// <param name="restart">Restarts the app to apply a downloaded version. The shell owns this action, because it refuses while the Queue works.</param>
    public UpdatesViewModel(IUpdateService updates, Action restart)
    {
        _updates = updates;
        _restart = restart;
        _updates.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Raise);
    }

    void Raise()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanRestart));
    }

    public string VersionText => "Lux " + _updates.Status.CurrentVersion;

    /// <summary>False for a copy that cannot update itself (a plain archive or a development build).</summary>
    public bool IsAvailable => _updates.Status.State != UpdateState.NotAvailable;

    public string StateText => _updates.Status switch
    {
        { State: UpdateState.NotAvailable } => "This copy does not update itself. Download newer versions from the Lux releases page.",
        { State: UpdateState.Checking } => "Looking for a newer version…",
        { State: UpdateState.Downloading } u => $"Downloading Lux {u.NewVersion} · {u.Percent} %",
        { State: UpdateState.ReadyToRestart } u => $"Lux {u.NewVersion} is downloaded. It is installed when you restart Lux.",
        { State: UpdateState.Failed } u => u.Message ?? "Could not check for a newer version.",
        _ => "This is the newest version.",
    };

    public bool CanCheck => _updates.Status.State is UpdateState.UpToDate or UpdateState.Failed;
    public bool CanRestart => _updates.Status.State == UpdateState.ReadyToRestart;

    [RelayCommand]
    Task CheckNow() => CanCheck ? _updates.CheckAsync() : Task.CompletedTask;

    [RelayCommand]
    void Restart() => _restart();
}
