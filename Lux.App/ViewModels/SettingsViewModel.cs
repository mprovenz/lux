using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

public sealed record CacheChoice(string Label, CacheLevel? Level)
{
    public override string ToString() => Label;
}

/// <summary>One indexed folder in the settings page, with its own default cache level.</summary>
public sealed partial class FolderSettingViewModel : ObservableObject
{
    readonly SettingsViewModel _owner;
    readonly bool _loading = true;

    public FolderSettingViewModel(SettingsViewModel owner, LibraryFolder f, CacheLevel? level)
    {
        _owner = owner;
        Folder = f;
        _level = owner.FolderChoices.First(c => c.Level == level);
        _loading = false;
    }

    public LibraryFolder Folder { get; }
    public string Path => Folder.Path;
    public string CountText
    {
        get
        {
            var s = Text.Count(Folder.CaptureCount, "capture", "captures");
            if (Folder.MissingCount > 0) s += $" · {Text.Number(Folder.MissingCount)} missing";
            if (!Folder.Exists) s += " · folder not found";
            if (Folder.Scanning) s += " · scanning";
            return s;
        }
    }

    [ObservableProperty] CacheChoice _level;
    partial void OnLevelChanged(CacheChoice value) { if (!_loading) _owner.SetFolderLevel(Folder.Id, value.Level); }

    [RelayCommand] void Rescan() => _owner.Rescan(Folder.Id);
    [RelayCommand] void Remove() => _owner.RemoveFolder(this);
}

/// <summary>One limit of the job queue, rendered from its <see cref="LimitDescriptor"/>: a number field (applied on Enter or when the field
/// is left) or a switch (applied at once), its range and default, the library's validation message, and a reset.</summary>
public sealed partial class LimitRowViewModel : ObservableObject
{
    readonly SettingsViewModel _owner;
    LimitDescriptor _d;
    bool _loading;

    public LimitRowViewModel(SettingsViewModel owner, LimitDescriptor d)
    {
        _owner = owner;
        _d = d;
        Load(d);
    }

    public string Key => _d.Key;
    public string Name => _d.Name;
    public string Description => _d.Description;
    public string Unit => _d.Unit;
    public bool IsToggle => _d.Kind == LimitKind.Toggle;
    public bool IsNumber => _d.Kind is LimitKind.Integer or LimitKind.Number;
    public double Current => _d.Current;
    public bool IsDefault => Math.Abs(_d.Current - _d.Default) < 1e-9;
    public string RangeText => IsToggle ? (_d.Default > 0 ? "Default: on" : "Default: off")
        : string.Create(CultureInfo.InvariantCulture, $"{F(_d.Min)}–{F(_d.Max)}{(Unit.Length > 0 ? " " + Unit : "")} · default {F(_d.Default)}");

    static string F(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.##", CultureInfo.InvariantCulture);

    [ObservableProperty] string _text = "";
    [ObservableProperty] bool _isOn;
    [ObservableProperty] string? _error;

    internal void Load(LimitDescriptor d)
    {
        _loading = true;
        _d = d;
        if (Error == null) Text = F(d.Current);   // a row showing a message keeps what was typed
        IsOn = d.Current > 0;
        _loading = false;
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(Current));
    }

    partial void OnIsOnChanged(bool value) { if (!_loading && IsToggle) _owner.Apply(this, value ? 1 : 0); }

    /// <summary>Apply the typed value (Enter, leaving the field).</summary>
    [RelayCommand]
    public void Commit()
    {
        if (!IsNumber) return;
        if (Text.Trim() == F(_d.Current)) { Error = null; return; }
        if (!double.TryParse(Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            Error = $"{Name}: type a number ({RangeText.Split(" · ")[0]}).";
            return;
        }
        _owner.Apply(this, v);
    }

    [RelayCommand] void Reset() => _owner.Reset(this);
}

/// <summary>A titled group of limits.</summary>
public sealed class LimitGroupViewModel(string title, string note, IReadOnlyList<LimitRowViewModel> rows)
{
    public string Title { get; } = title;
    public string Note { get; } = note;
    public bool HasNote => Note.Length > 0;
    public IReadOnlyList<LimitRowViewModel> Rows { get; } = rows;
}

/// <summary>
/// Settings: library file and folders, what Lux keeps per photo, where a photo's data goes when its folder cannot be written (a move job),
/// the extra-storage question, and every scheduler limit from <see cref="ILibrarySettings.GetLimits"/>. Every change applies at once; the
/// library validates and its message is shown on the row.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly ILibrarySettings _set;
    readonly IShell _shell;
    readonly QueueViewModel _queue;
    readonly Func<Task> _addFolder;
    readonly Dictionary<string, LimitRowViewModel> _rows = new(StringComparer.Ordinal);
    bool _loading, _writing;
    long? _moveJobId;

    public SettingsViewModel(AppServices s, IShell shell, QueueViewModel queue, Func<Task> addFolder)
    {
        _s = s;
        _set = s.Library.Settings;
        _shell = shell;
        _queue = queue;
        _addFolder = addFolder;
        SetAside = new SetAsideCopiesViewModel(s, shell);   // Storage → copies set aside
        Updates = new UpdatesViewModel(s.Updates, shell.RestartToUpdate);
        _set.PropertyChanged += OnSettingChanged;
        _queue.PropertyChanged += (_, _) => FollowMoveJob();
        Load();
        BuildLimits();
    }

    public IReadOnlyList<CacheChoice> AppChoices { get; } = new CacheChoice[]
    {
        new("Standard — 13 MP working copy (about 70 MB a photo)", CacheLevel.Linear13),
        new("Full detail (about 270 MB a photo)", CacheLevel.Full),
        new("Depth only — smallest (about 15 MB a photo)", CacheLevel.Geometry),
    };

    public IReadOnlyList<CacheChoice> FolderChoices { get; } = new CacheChoice[]
    {
        new("Same as the default", null), new("Standard (13 MP)", CacheLevel.Linear13), new("Full detail", CacheLevel.Full), new("Depth only", CacheLevel.Geometry),
    };

    public ObservableCollection<FolderSettingViewModel> Folders { get; } = new();
    public ObservableCollection<LimitGroupViewModel> LimitGroups { get; } = new();
    public IReadOnlyDictionary<string, LimitRowViewModel> LimitRows => _rows;

    public string LibraryFile => _set.DatabasePath;
    public string AppDataFolder => _s.AppDataDir;
    [ObservableProperty] string? _libraryNote;

    [ObservableProperty] CacheChoice _defaultLevel = null!;
    [ObservableProperty] bool _analyseNew;
    [ObservableProperty] bool _watchFolders;
    [ObservableProperty] bool _askExtraStorage;
    [ObservableProperty] bool _showAsHeld;
    [ObservableProperty] string _exportFolder = "";
    public IReadOnlyList<ChoiceOption> CollisionChoices { get; } = new ChoiceOption[]
    {
        new("Keep both: add a number to the new file", (int)ExportCollisionSetting.AddNumber), new("Ask before exporting", (int)ExportCollisionSetting.Ask),
        new("Replace the existing file", (int)ExportCollisionSetting.Replace), new("Skip that photo", (int)ExportCollisionSetting.Skip),
    };
    [ObservableProperty] ChoiceOption? _collision;
    /// <summary>Export photos unturned, as Lumen does (default off: exports follow the view shown as held).</summary>
    [ObservableProperty] bool _exportUnturned;
    partial void OnExportUnturnedChanged(bool value) => Write(s => s.ExportUnturned = value);
    partial void OnCollisionChanged(ChoiceOption? value) { if (value != null) { var v = (ExportCollisionSetting)value.Value; Write(s => s.ExportIfExists = v); } }
    // Viewer: how a changed picture is swapped in (the tile-swap policy)
    [ObservableProperty] bool _swapWhole = true;
    [ObservableProperty] string _swapWindowText = "300";
    [ObservableProperty] string _swapFadeText = "120";
    [ObservableProperty] string? _swapError;

    void Load()
    {
        _loading = true;
        DefaultLevel = AppChoices.FirstOrDefault(c => c.Level == _set.DefaultCacheLevel) ?? AppChoices[0];
        AnalyseNew = _set.AnalyseNewCaptures;
        WatchFolders = _set.WatchFolders;
        AskExtraStorage = !_set.DontAskAboutExtraStorage;
        ShowAsHeld = _set.ShowPhotosAsHeld;
        ExportFolder = _set.DefaultExportFolder ?? ExportPanelViewModel.DefaultFolderDisplay;
        Collision = CollisionChoices.FirstOrDefault(c => c.Value == (int)_set.ExportIfExists) ?? CollisionChoices[0];
        ExportUnturned = _set.ExportUnturned;
        var vp = _s.ViewerPrefs;
        SwapWhole = vp.SwapWhole;
        SwapWindowText = vp.WindowMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        SwapFadeText = vp.FadeMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        LocalFolder = _set.LocalContextFolder;
        DefaultLocalFolder = _set.DefaultLocalContextFolder;
        _loading = false;
    }

    // ============================================================================================================ limits

    static readonly (string Group, string Title)[] GroupOrder =
    {
        ("Jobs", "Jobs and threads"), ("Memory", "Memory budget"), ("Memory per job", "Memory reserved per job type"),
        ("Priorities", "Priorities"), ("Thumbnails", "Thumbnails"),
    };

    void BuildLimits()
    {
        var all = _set.GetLimits().Where(d => d.Kind != LimitKind.Folder).ToList();
        LimitGroups.Clear();
        _rows.Clear();
        var groups = all.GroupBy(d => d.Group).ToDictionary(g => g.Key, g => g.ToList());
        var order = GroupOrder.Select(g => g.Group).Concat(groups.Keys.Where(k => GroupOrder.All(g => g.Group != k))).ToList();
        foreach (var key in order)
        {
            if (!groups.TryGetValue(key, out var list)) continue;
            var rows = list.Select(d => _rows[d.Key] = new LimitRowViewModel(this, d)).ToList();
            string title = GroupOrder.FirstOrDefault(g => g.Group == key).Title ?? key;
            LimitGroups.Add(new LimitGroupViewModel(title, GroupNote(key), rows));
        }
        OnPropertyChanged(nameof(HasLimits));
    }

    public bool HasLimits => LimitGroups.Count > 0;

    string GroupNote(string group) => group switch
    {
        "Memory" => $"In effect: {Text.GiB(_set.MemoryBudgetGb)} of {Text.GiB(_set.PhysicalMemoryGb)} installed.",
        "Memory per job" => "A heavy job starts only when what it reserves fits in what the running jobs leave of the budget.",
        "Priorities" => "Lower numbers run first; within a number, the oldest first.",
        _ => "",
    };

    void ReloadLimits()
    {
        var all = _set.GetLimits();
        foreach (var d in all) if (_rows.TryGetValue(d.Key, out var r)) r.Load(d);
        // the "in effect" note of the memory group
        var mem = LimitGroups.FirstOrDefault(g => g.Title == "Memory budget");
        if (mem != null)
        {
            int i = LimitGroups.IndexOf(mem);
            LimitGroups[i] = new LimitGroupViewModel(mem.Title, GroupNote("Memory"), mem.Rows);
        }
    }

    internal void Apply(LimitRowViewModel row, double value)
    {
        var set = _set;
        string key = row.Key, typed = row.Text;
        _writing = true;
        _ = Task.Run(() => set.SetLimit(key, value)).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _writing = false;
            string? error = t.Exception?.InnerException is LibraryException e ? e.Message : t.IsFaulted ? "That value could not be saved." : null;
            row.Error = error;
            ReloadLimits();
            if (error != null && row.IsNumber) row.Text = typed;   // keep what was typed next to the message
        }), TaskScheduler.Default);
    }

    internal void Reset(LimitRowViewModel row)
    {
        var set = _set;
        string key = row.Key;
        _ = Task.Run(() => set.ResetLimit(key)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { row.Error = null; ReloadLimits(); }), TaskScheduler.Default);
    }

    [RelayCommand]
    void ResetAllLimits()
    {
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel("Put every background-work setting back to its default?",
            "Jobs at once, threads, the memory budget, the memory reserved per job type, the priorities and the thumbnail settings go back to Lux's defaults.",
            "Reset to defaults",
            () =>
            {
                _shell.CloseDialog(c!);
                var set = _set;
                _ = Task.Run(set.ResetLimitsToDefaults).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var r in _rows.Values) r.Error = null;
                    ReloadLimits();
                }), TaskScheduler.Default);
            },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    // ============================================================================================================ the local data folder

    [ObservableProperty] string _localFolder = "";
    [ObservableProperty] string _defaultLocalFolder = "";
    [ObservableProperty] string _localCountText = "";
    [ObservableProperty] string? _localError;
    [ObservableProperty] JobViewModel? _moveJob;

    public bool LocalIsDefault => LocalFolder == DefaultLocalFolder;
    public string LocalDefaultText => LocalIsDefault ? "This is the default folder." : $"Default: {DefaultLocalFolder}";
    public bool IsMoving => MoveJob is { IsFinished: false };
    partial void OnLocalFolderChanged(string value) { OnPropertyChanged(nameof(LocalIsDefault)); OnPropertyChanged(nameof(LocalDefaultText)); }
    partial void OnMoveJobChanged(JobViewModel? value) => OnPropertyChanged(nameof(IsMoving));

    public async Task RefreshLocalCountAsync()
    {
        var lib = _s.Library;
        int n = await Task.Run(async () =>
        {
            int count = 0, offset = 0;
            while (true)
            {
                var page = await lib.QueryAsync(new CaptureQuery { Offset = offset, Limit = 5000 }).ConfigureAwait(false);
                count += page.Items.Count(i => i.ContextLocation == ContextLocation.LocalStore);
                offset += page.Items.Count;
                if (page.Items.Count == 0 || offset >= page.Total) return count;
            }
        }).ConfigureAwait(true);
        LocalCountText = n switch
        {
            0 => "No photo keeps its data here now.",
            1 => "1 photo keeps its data here now.",
            _ => $"{Text.Number(n)} photos keep their data here now.",
        };
    }

    [RelayCommand]
    async Task ChooseLocalFolder()
    {
        var p = await _s.Platform.PickFolderAsync("Folder for the data of photos on read-only drives").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(p)) await MoveLocalFolderAsync(p).ConfigureAwait(true);
    }

    [RelayCommand] Task UseDefaultLocalFolder() => MoveLocalFolderAsync(DefaultLocalFolder);

    /// <summary>Start moving the local data folder (a Queue job); a folder Lux cannot use is refused at once with the library's message.</summary>
    public async Task MoveLocalFolderAsync(string folder)
    {
        LocalError = null;
        var lib = _s.Library;
        try
        {
            long id = await Task.Run(() => lib.ChangeLocalContextFolder(folder)).ConfigureAwait(true);
            _moveJobId = id;
            FollowMoveJob();
            _shell.Notify("Moving photo data — see Queue");
        }
        catch (LibraryException e) { LocalError = e.Message; }
        catch (NotSupportedException) { LocalError = "This library cannot move its photo-data folder yet."; }
    }

    void FollowMoveJob()
    {
        if (_moveJobId is not long id) return;
        var job = _queue.Jobs.FirstOrDefault(j => j.Id == id);
        if (job == null) return;
        if (MoveJob != job) MoveJob = job;
        OnPropertyChanged(nameof(IsMoving));
        if (job.IsFinished)
        {
            if (job.IsFailed) LocalError = "The data stayed where it was: " + (job.Info.Error ?? "the move did not finish.");
            _moveJobId = null;
            LocalFolder = _set.LocalContextFolder;
            _ = RefreshLocalCountAsync();
        }
    }

    // ============================================================================================================ the rest

    public async Task RefreshFoldersAsync()
    {
        var folders = await Task.Run(() => _s.Library.GetFolders().Select(f => (f, _set.GetFolderCacheLevel(f.Id))).ToList()).ConfigureAwait(true);
        Folders.Clear();
        foreach (var (f, l) in folders) Folders.Add(new FolderSettingViewModel(this, f, l));
        OnPropertyChanged(nameof(HasFolders));
        _ = RefreshLocalCountAsync();
        _ = SetAside.RefreshAsync();
    }

    /// <summary>Storage → the copies of photos' data that Lux set aside after a conflict. This block has its own view model.</summary>
    public SetAsideCopiesViewModel SetAside { get; }
    /// <summary>The Updates section.</summary>
    public UpdatesViewModel Updates { get; }

    public bool HasFolders => Folders.Count > 0;

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_writing) return;
            Load();
            ReloadLimits();
        });

    void Write(Action<ILibrarySettings> a)
    {
        if (_loading) return;
        var set = _set;
        _writing = true;
        _ = Task.Run(() => a(set)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _writing = false), TaskScheduler.Default);
    }

    partial void OnDefaultLevelChanged(CacheChoice value) => Write(s => s.DefaultCacheLevel = value.Level ?? CacheLevel.Linear13);
    partial void OnAnalyseNewChanged(bool value) => Write(s => s.AnalyseNewCaptures = value);
    partial void OnWatchFoldersChanged(bool value) => Write(s => s.WatchFolders = value);
    partial void OnAskExtraStorageChanged(bool value) => Write(s => s.DontAskAboutExtraStorage = !value);
    partial void OnShowAsHeldChanged(bool value) => Write(s => s.ShowPhotosAsHeld = value);
    partial void OnSwapWholeChanged(bool value) { if (!_loading) CommitSwap(); }

    /// <summary>The Viewer section's two numbers (Enter or leaving the field); an unreadable one keeps the value and says why.</summary>
    public bool CommitSwap()
    {
        var en = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(SwapWindowText.Replace("ms", "").Trim(), System.Globalization.NumberStyles.Float, en, out var w) || w < 0 || w > 5000)
        { SwapError = "The wait is a number of milliseconds from 0 to 5000."; return false; }
        if (!double.TryParse(SwapFadeText.Replace("ms", "").Trim(), System.Globalization.NumberStyles.Float, en, out var f) || f < 0 || f > 2000)
        { SwapError = "The fade is a number of milliseconds from 0 to 2000."; return false; }
        SwapError = null;
        _s.ViewerPrefs.Set(_set, SwapWhole, w, f);
        return true;
    }

    [RelayCommand]
    void ResetSwap()
    {
        SwapWhole = true;
        SwapWindowText = ViewerPreferences.DefaultWindowMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        SwapFadeText = ViewerPreferences.DefaultFadeMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        CommitSwap();
    }

    internal void SetFolderLevel(long folderId, CacheLevel? level) => Write(s => s.SetFolderCacheLevel(folderId, level));
    internal void Rescan(long folderId) { _ = Task.Run(() => _s.Library.RescanFolder(folderId)); _shell.Notify("Rescan queued"); }

    internal void RemoveFolder(FolderSettingViewModel f)
    {
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel("Stop using this folder?",
            $"{f.Path} leaves the library. Nothing on disk is touched: the captures, their edits and working copies stay where they are and come back if you add the folder again.",
            "Remove folder",
            () =>
            {
                _shell.CloseDialog(c!);
                _ = Task.Run(() => _s.Library.RemoveFolderAsync(f.Folder.Id)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshFoldersAsync()), TaskScheduler.Default);
            },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    [RelayCommand] Task AddFolder() => _addFolder();

    [RelayCommand]
    async Task ChooseExportFolder()
    {
        var p = await _s.Platform.PickFolderAsync("Default export folder").ConfigureAwait(true);
        if (string.IsNullOrEmpty(p)) return;
        ExportFolder = p;
        Write(s => s.DefaultExportFolder = p);
    }

    [RelayCommand]
    async Task ChooseLibraryLocation()
    {
        var p = await _s.Platform.PickFolderAsync("Folder for the library file").ConfigureAwait(true);
        if (string.IsNullOrEmpty(p)) return;
        string file = Path.Combine(p, "library.luxlib");
        string cfgFile = _s.ConfigFile;
        await Task.Run(() =>
        {
            var cfg = AppConfig.Load(cfgFile);
            cfg.LibraryFile = file;
            cfg.Save(cfgFile);
        }).ConfigureAwait(true);
        LibraryNote = $"Lux will use {file} the next time it starts. The current library stays where it is.";
    }
}
