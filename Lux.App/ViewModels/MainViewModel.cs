using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

public enum Page { Library, Develop, Queue, Camera, Settings }

/// <summary>The shell: tabs, the first-run state, dialogs, notices and the status bar.</summary>
public sealed partial class MainViewModel : ObservableObject, IShell, IAsyncDisposable
{
    readonly AppServices _s;
    readonly LibraryEvents _events;
    CancellationTokenSource? _noticeCts;
    bool _statsPending;

    public MainViewModel(AppServices s)
    {
        _s = s;
        _events = new LibraryEvents(s.Library);
        Queue = new QueueViewModel(s, _events);
        Library = new LibraryViewModel(s, _events, Queue, this);
        Develop = new DevelopViewModel(s, this, Library, Queue);
        Camera = new CameraViewModel(s, _events, Notify);
        Camera.StatusTextsChanged += RaiseStatus;
        Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CameraViewModel.SelectionSummary) or nameof(CameraViewModel.FreeSpaceText) or nameof(CameraViewModel.ImportProgressText)) RaiseStatus();
        };
        Library.ResultChanged += () => Develop.StripChanged();
        Develop.ViewInfoChanged += RaiseStatus;
        Develop.StatusPointerChanged += () => OnPropertyChanged(nameof(StatusPointer));
        Develop.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevelopViewModel.StatusZoom) or nameof(DevelopViewModel.StatusQuality) or nameof(DevelopViewModel.StatusSaved)
                or nameof(DevelopViewModel.HeaderText))
                RaiseStatus();
        };
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueueViewModel.TabLabel)) OnPropertyChanged(nameof(QueueTabLabel));
            if (e.PropertyName is nameof(QueueViewModel.AnalysisSummary) or nameof(QueueViewModel.RunningSummary) or nameof(QueueViewModel.MemoryText)) RaiseStatus();
        };
        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.SelectedCount)) RaiseStatus();
        };
        _events.CapturesChanged += (_, _) => ScheduleStats();
        _events.FoldersChanged += () => { ScheduleStats(); _ = Settings?.RefreshFoldersAsync(); };
        s.Device.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RaiseStatus);
        s.Updates.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RaiseUpdate);
        // an installed copy looks for a newer version once, at start, and downloads it in the background; it is applied
        // only when the user chooses "Restart to update"
        if (s.Updates.Status.State != UpdateState.NotAvailable) _ = s.Updates.CheckAsync();
    }

    void RaiseUpdate()
    {
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(CanRestartToUpdate));
    }

    /// <summary>The status bar's note about the app's own update: shown while a newer version downloads and when it is ready.</summary>
    public string? UpdateText => _s.Updates.Status switch
    {
        { State: UpdateState.Downloading } u => $"Downloading Lux {u.NewVersion} · {u.Percent} %",
        { State: UpdateState.ReadyToRestart } u => $"Lux {u.NewVersion} is ready",
        _ => null,
    };

    public bool CanRestartToUpdate => _s.Updates.Status.State == UpdateState.ReadyToRestart;

    /// <summary>Closes the app, applies the downloaded version and starts it again — not while the Queue is working.</summary>
    [RelayCommand]
    public void RestartToUpdate()
    {
        if (!CanRestartToUpdate) return;
        if (Queue.RunningCount > 0)
        {
            Notify("The Queue is still working. Let it finish or pause it, then restart to update.");
            return;
        }
        _s.Updates.RestartToUpdate();
    }

    public AppServices Services => _s;
    public LibraryEvents Events => _events;
    public LibraryViewModel Library { get; }
    public QueueViewModel Queue { get; }
    public DevelopViewModel Develop { get; }
    /// <summary>The Camera page.</summary>
    public CameraViewModel Camera { get; }
    public SettingsViewModel? Settings { get; private set; }

    /// <summary>Load everything the first screen needs (off the UI thread where it reads).</summary>
    public async Task InitializeAsync()
    {
        await Queue.LoadAsync().ConfigureAwait(true);
        await RefreshStatsAsync().ConfigureAwait(true);
        var sort = _s.Library.Settings.GetValue("browse.sort")?.Split(':');
        if (sort is { Length: 2 } && Enum.TryParse<CaptureSort>(sort[0], out var cs) && bool.TryParse(sort[1], out var desc))
        {
            Library.Sort = cs;
            Library.Descending = desc;
        }
        await Library.RefreshTreeAsync().ConfigureAwait(true);
        if (Enum.TryParse<Page>(_s.Library.Settings.GetValue("window.page"), out var p) && p != Page.Settings) CurrentPage = p;
    }

    // ============================================================================================================ pages

    [ObservableProperty] Page _currentPage = Page.Library;

    partial void OnCurrentPageChanged(Page value)
    {
        OnPropertyChanged(nameof(IsLibrary)); OnPropertyChanged(nameof(IsDevelop)); OnPropertyChanged(nameof(IsQueue));
        OnPropertyChanged(nameof(IsCamera)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(ShowLibraryTools));
        OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(ShowLibraryPage));
        RaiseStatus();
        _s.Library.Settings.SetValue("window.page", value.ToString());
        if (value == Page.Settings)
        {
            Settings ??= new SettingsViewModel(_s, this, Queue, AddFolderAsync);
            OnPropertyChanged(nameof(Settings));
            _ = Settings.RefreshFoldersAsync();
        }
    }

    public bool IsLibrary => CurrentPage == Page.Library;
    public bool IsDevelop => CurrentPage == Page.Develop;
    public bool IsQueue => CurrentPage == Page.Queue;
    public bool IsCamera => CurrentPage == Page.Camera;
    public bool IsSettings => CurrentPage == Page.Settings;
    public bool ShowLibraryTools => IsLibrary && !IsFirstRun;
    public bool ShowEmpty => IsLibrary && IsFirstRun;
    public bool ShowLibraryPage => IsLibrary && !IsFirstRun;
    public string QueueTabLabel => Queue.TabLabel;

    [RelayCommand] void ShowPage(Page page)
    {
        // the Develop tab with nothing open opens the library's current capture
        if (page == Page.Develop && Develop.Item == null && (Library.Details.Current ?? Library.CursorItem) is { } c) { OpenInDevelop(c); return; }
        CurrentPage = page;
    }
    public void ShowSettings() => CurrentPage = Page.Settings;
    public void ShowLibrary() => CurrentPage = Page.Library;

    // ============================================================================================================ develop

    /// <summary>Open a capture in Develop (double-click, Enter, "Open in Develop").</summary>
    public void OpenInDevelop(CaptureItemViewModel item)
    {
        CurrentPage = Page.Develop;
        _ = Develop.OpenAsync(item);
    }

    // ============================================================================================================ first run

    [ObservableProperty] bool _isFirstRun;
    partial void OnIsFirstRunChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLibraryTools)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(ShowLibraryPage));
        RaiseStatus();
    }

    [RelayCommand]
    public async Task AddFolderAsync()
    {
        var path = await _s.Platform.PickFolderAsync("Choose a folder with Light L16 captures").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path)) await AddFoldersAsync(new[] { path }).ConfigureAwait(true);
    }

    /// <summary>Add folders to the library (the picker, a folder dropped on the window). A path that is not a folder Lux can read is
    /// refused with the library's message.</summary>
    public async Task AddFoldersAsync(IReadOnlyList<string> paths)
    {
        var added = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var folder = await Task.Run(() => _s.Library.AddFolderAsync(path)).ConfigureAwait(true);
                added.Add(folder.Path);
            }
            catch (LibraryException e) { Notify(e.Message); return; }
        }
        if (added.Count == 0) return;
        Notify(added.Count == 1 ? $"Adding {added[0]} — captures appear as they are found" : $"Adding {added.Count} folders — captures appear as they are found");
        CurrentPage = Page.Library;
        await RefreshStatsAsync().ConfigureAwait(true);
        await Library.RefreshTreeAsync().ConfigureAwait(true);
        if (Settings != null) await Settings.RefreshFoldersAsync().ConfigureAwait(true);
    }

    /// <summary>A folder is being dragged over the window: the drop hint shows.</summary>
    [ObservableProperty] bool _isFolderDropTarget;

    [RelayCommand] void OpenCamera() => CurrentPage = Page.Camera;

    /// <summary>"Find Lumen edits…": choose a folder, add it if needed, wait for its scan, then the import dialog.</summary>
    [RelayCommand]
    async Task FindLumenEdits()
    {
        var path = await _s.Platform.PickFolderAsync("Choose your Lumen library folder").ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;
        var lib = _s.Library;
        OpenLumenImportCore(path, async ct =>
        {
            var folder = await lib.AddFolderAsync(path, cancel: ct).ConfigureAwait(false);
            var scans = folder.ScanJobId is long scan ? new[] { scan }
                : lib.Jobs.GetJobs(includeFinished: false).Where(j => j.Kind == JobKind.FolderScan && j.FolderId == folder.Id).Select(j => j.Id).ToArray();
            foreach (var id in scans) await lib.Jobs.WaitAsync(id, ct).ConfigureAwait(false);
            var full = Path.GetFullPath(path).TrimEnd('/', '\\');
            return full == folder.Path.TrimEnd('/', '\\') ? new CaptureQuery { FolderId = folder.Id } : new CaptureQuery { PathPrefix = full };
        }, null);
    }

    public void OpenLumenImport(CaptureQuery scope, string label, IReadOnlyCollection<string>? only = null) =>
        OpenLumenImportCore(label, _ => Task.FromResult(scope), only);

    void OpenLumenImportCore(string label, Func<CancellationToken, Task<CaptureQuery>> scope, IReadOnlyCollection<string>? only)
    {
        LumenImportViewModel? vm = null;
        vm = new LumenImportViewModel(_s, label, scope, only, () => CloseDialog(vm!), ids =>
        {
            CurrentPage = Page.Library;
            var node = Library.Tree.FirstOrDefault(n => n.Key == "all");
            if (node != null && Library.SelectedNode != node) Library.SelectedNode = node;
            _ = Library.RequeryAsync().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Library.SelectIds(ids)), TaskScheduler.Default);
        }, Notify);
        ShowDialog(vm);
    }

    // ============================================================================================================ dialogs and notices

    [ObservableProperty] object? _dialog;
    public bool IsDialogOpen => Dialog != null;
    partial void OnDialogChanged(object? value) => OnPropertyChanged(nameof(IsDialogOpen));

    public void ShowDialog(object dialog) => Dialog = dialog;
    public void CloseDialog(object dialog) { if (Dialog == dialog) Dialog = null; }

    [ObservableProperty] string? _notice;
    /// <summary>The notice's action ("Undo"), when it has one.</summary>
    [ObservableProperty] string? _noticeAction;
    Action? _noticeCallback;

    [RelayCommand]
    void RunNoticeAction()
    {
        var a = _noticeCallback;
        _noticeCallback = null;
        NoticeAction = null;
        Notice = null;
        a?.Invoke();
    }

    public void Notify(string message, string actionLabel, Action action)
    {
        Notify(message);
        NoticeAction = actionLabel;
        _noticeCallback = action;
    }

    public void Notify(string message)
    {
        NoticeAction = null;
        _noticeCallback = null;
        Notice = message;
        _noticeCts?.Cancel();
        var cts = _noticeCts = new CancellationTokenSource();
        _ = Task.Delay(5000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) { Notice = null; NoticeAction = null; _noticeCallback = null; } });
        }, TaskScheduler.Default);
    }

    // ============================================================================================================ status bar

    LibraryStats _stats = new();

    void ScheduleStats()
    {
        if (_statsPending) return;
        _statsPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _statsPending = false; _ = RefreshStatsAsync(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public async Task RefreshStatsAsync()
    {
        var (stats, folders) = await Task.Run(() => (_s.Library.GetStats(), _s.Library.GetFolders().Count)).ConfigureAwait(true);
        _stats = stats;
        IsFirstRun = folders == 0;
        RaiseStatus();
    }

    void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusLeft)); OnPropertyChanged(nameof(StatusAnalysis)); OnPropertyChanged(nameof(StatusCache));
        OnPropertyChanged(nameof(StatusDevice));
    }

    public string StatusLeft
    {
        get
        {
            if (IsQueue) return Queue.RunningSummary;
            if (IsDevelop) return Develop.Item == null ? "Nothing open" : Develop.StatusZoom;
            if (IsCamera) return Camera.SelectionSummary;
            if (IsFirstRun || _stats.Captures == 0) return "No captures yet";
            var s = Text.Count(_stats.Captures, "capture", "captures");
            if (Library.SelectedCount > 0) s += $" · {Text.Number(Library.SelectedCount)} selected";
            if (_stats.Missing > 0) s += $" · {Text.Number(_stats.Missing)} missing";
            return s;
        }
    }

    public string StatusAnalysis => IsDevelop ? Develop.StatusQuality : IsCamera ? (Camera.IsImporting ? Camera.ImportProgressText : Queue.AnalysisSummary)
                                  : IsQueue || IsFirstRun ? "" : Queue.AnalysisSummary;

    public string StatusCache
    {
        get
        {
            if (IsQueue) return Queue.MemoryText;
            if (IsDevelop) return Develop.StatusSaved;
            if (IsCamera) return Camera.FreeSpaceText;
            if (IsFirstRun) return "";
            int full = _stats.ByCacheLevel.GetValueOrDefault(CacheLevel.Full);
            return $"Cache {Text.Bytes(_stats.CacheBytes)} · {Text.Number(full)} at full detail";
        }
    }

    public string StatusDevice => IsQueue || IsDevelop ? "" : _s.Device.StatusText;

    /// <summary>Develop: the pointer's x, y and depth over the picture ("x 4326  y 3869  depth 3.18 m").</summary>
    public string StatusPointer => IsDevelop ? Develop.StatusPointer : "";

    public async ValueTask DisposeAsync()
    {
        // an open Animate panel stops its preview (no frame arrives for a closed window) and lets its capture go
        Library.CloseAnimate();
        Develop.AnimatePanel = null;
        await Develop.DisposeAsync().ConfigureAwait(false);
        _events.Dispose();
        await _s.DisposeAsync().ConfigureAwait(false);
    }
}
