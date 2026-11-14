using Velopack;
using Velopack.Sources;

namespace Lux.App.Services;

/// <summary>Where the app's own update stands.</summary>
public enum UpdateState
{
    /// <summary>This copy cannot update itself (a plain archive or a development build): nothing is checked.</summary>
    NotAvailable,
    /// <summary>Nothing known yet, or the last check found nothing newer.</summary>
    UpToDate,
    Checking,
    Downloading,
    /// <summary>A newer version is downloaded; it is applied when the user chooses to restart (never by itself).</summary>
    ReadyToRestart,
    /// <summary>The last check or download failed (no network, the release page unreachable); the app carries on as it is.</summary>
    Failed,
}

/// <summary>A snapshot of the update state for the interface.</summary>
/// <param name="CurrentVersion">The running version ("2.0.0").</param>
/// <param name="NewVersion">The version found, while downloading or ready.</param>
/// <param name="Percent">Download progress, 0–100.</param>
/// <param name="Message">A plain sentence for <see cref="UpdateState.Failed"/>.</param>
public sealed record UpdateStatus(UpdateState State, string CurrentVersion, string? NewVersion = null, int Percent = 0, string? Message = null);

/// <summary>The app's own updates: check, download in the background, apply only on request.</summary>
public interface IUpdateService
{
    UpdateStatus Status { get; }
    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event Action? StatusChanged;
    /// <summary>Look for a newer version and download it if there is one. Does nothing while a check runs or when updates are not available.</summary>
    Task CheckAsync();
    /// <summary>Close the app, apply the downloaded version and start it again. Only in <see cref="UpdateState.ReadyToRestart"/>.</summary>
    void RestartToUpdate();
}

/// <summary>The update service of an installed copy (Windows installer, Linux AppImage): releases are read from the project's
/// GitHub releases. In any other copy <see cref="UpdateManager.IsInstalled"/> is false and the service reports
/// <see cref="UpdateState.NotAvailable"/> without touching the network.</summary>
public sealed class VelopackUpdateService : IUpdateService
{
    public const string RepositoryUrl = "https://github.com/mprovenz/lux";

    readonly object _lock = new();
    readonly UpdateManager? _manager;
    readonly string _current;
    UpdateInfo? _ready;
    bool _busy;

    public VelopackUpdateService()
    {
        _current = AppVersion.Current;
        try
        {
            // LUX_UPDATE_SOURCE (a folder or a URL holding the release files) replaces the project's releases: for trying an
            // update before it is published
            string? over = Environment.GetEnvironmentVariable("LUX_UPDATE_SOURCE");
            var m = string.IsNullOrWhiteSpace(over)
                ? new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false))
                : new UpdateManager(over);
            if (m.IsInstalled)
            {
                _manager = m;
                _current = m.CurrentVersion?.ToString() ?? _current;
            }
        }
        catch (Exception)
        {
            // not an installed copy, or the install is damaged: the app runs without updates
        }
        Status = new UpdateStatus(_manager == null ? UpdateState.NotAvailable : UpdateState.UpToDate, _current);
        // a version downloaded in an earlier session and not yet applied
        if (_manager?.UpdatePendingRestart is { } pending)
            Status = new UpdateStatus(UpdateState.ReadyToRestart, _current, pending.Version.ToString());
    }

    public UpdateStatus Status { get; private set; }
    public event Action? StatusChanged;

    void Set(UpdateStatus s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (_manager == null) return;
        lock (_lock)
        {
            if (_busy) return;
            _busy = true;
        }
        try
        {
            if (_manager.UpdatePendingRestart is { } pending && _ready == null)
            {
                Set(new UpdateStatus(UpdateState.ReadyToRestart, _current, pending.Version.ToString()));
                return;
            }
            Set(new UpdateStatus(UpdateState.Checking, _current));
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                Set(new UpdateStatus(UpdateState.UpToDate, _current));
                return;
            }
            string version = info.TargetFullRelease.Version.ToString();
            Set(new UpdateStatus(UpdateState.Downloading, _current, version));
            int last = -1;
            await _manager.DownloadUpdatesAsync(info, p =>
            {
                if (p == last) return;
                last = p;
                Set(new UpdateStatus(UpdateState.Downloading, _current, version, p));
            }).ConfigureAwait(false);
            _ready = info;
            Set(new UpdateStatus(UpdateState.ReadyToRestart, _current, version, 100));
        }
        catch (Exception e)
        {
            Set(new UpdateStatus(UpdateState.Failed, _current, Message: "Could not check for a newer version: " + e.Message));
        }
        finally
        {
            lock (_lock) _busy = false;
        }
    }

    public void RestartToUpdate()
    {
        if (_manager == null || Status.State != UpdateState.ReadyToRestart) return;
        if (_ready != null) _manager.ApplyUpdatesAndRestart(_ready);
        else if (_manager.UpdatePendingRestart is { } pending) _manager.ApplyUpdatesAndRestart(pending);
    }
}

/// <summary>No self-update (tests, the fake library, copies that are not installed).</summary>
public sealed class NoUpdateService : IUpdateService
{
    public UpdateStatus Status { get; } = new(UpdateState.NotAvailable, AppVersion.Current);
    public event Action? StatusChanged { add { } remove { } }
    public Task CheckAsync() => Task.CompletedTask;
    public void RestartToUpdate() { }
}

/// <summary>The running build's version (the project <c>Version</c>, without the commit suffix).</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    static string Read()
    {
        var asm = typeof(AppVersion).Assembly;
        string? info = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
