using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.Fake;

/// <summary>
/// In-memory <see cref="ILibrarySettings"/> with the real library's limit descriptors (same keys, ranges, defaults, wording and
/// validation messages); optionally kept in a JSON file so window state and views survive a restart.
/// </summary>
public sealed class FakeSettings : ILibrarySettings
{
    sealed record Def(string Key, string Group, string Name, string Description, string Unit, LimitKind Kind, double Min, double Max, double Default, string Property);

    readonly object _gate = new();
    readonly string? _file;
    readonly Dictionary<string, string> _values = new();
    readonly Dictionary<long, CacheLevel> _folderLevels = new();
    readonly Dictionary<string, double> _limits = new(StringComparer.Ordinal);
    readonly List<Def> _defs;
    readonly Dictionary<string, Def> _byKey;
    CacheLevel _default = CacheLevel.Linear13;
    bool _analyseNew, _writeRatings = true, _watch = true;
    string? _exportFolder;
    string? _localFolder;

    /// <summary>Fixed so screenshots and tests do not depend on the machine.</summary>
    public const double FakePhysicalMemoryGb = 64;
    public const int FakeCores = 24;

    public FakeSettings(string databasePath, string? file = null)
    {
        DatabasePath = databasePath;
        _file = file;
        _defs = Definitions();
        _byKey = _defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
        if (_file != null && File.Exists(_file))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
                if (map != null) foreach (var (k, v) in map) _values[k] = v;
            }
            catch (Exception) { /* a damaged preferences file starts fresh */ }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DatabasePath { get; }

    static double DefaultPriority(JobKind k) => k switch
    {
        JobKind.Thumbnail or JobKind.FolderScan => 1, JobKind.LumenImport or JobKind.Import => 2, JobKind.Analysis => 3,
        JobKind.CacheBuild or JobKind.CacheUpgrade or JobKind.CacheRelease => 4, JobKind.ContextStoreMove => 1, _ => 5,
    };

    public static double DefaultReservationGb(JobReservation r) => r switch
    {
        JobReservation.Analysis => 5.5, JobReservation.AnalysisStacked => 7.5, JobReservation.FullDetail => 6.5, JobReservation.FullDetailStacked => 8.5,
        JobReservation.Export => 6.5, JobReservation.ExportStacked => 8.5, JobReservation.ExportSmall => 1.5, JobReservation.Thumbnail => 1.5, _ => 1,
    };

    static string ReservationName(JobReservation r) => r switch
    {
        JobReservation.Analysis => "Analysis", JobReservation.AnalysisStacked => "Analysis, low-light photo", JobReservation.FullDetail => "Keeping full detail",
        JobReservation.FullDetailStacked => "Keeping full detail, low-light photo", JobReservation.Export => "Full-size export",
        JobReservation.ExportStacked => "Full-size export, low-light photo", JobReservation.ExportSmall => "Smaller export", JobReservation.Thumbnail => "One thumbnail",
        _ => r.ToString(),
    };

    static string KindName(JobKind k) => k switch
    {
        JobKind.Thumbnail => "Thumbnails", JobKind.Analysis => "Analysis", JobKind.CacheBuild => "Keeping renders", JobKind.CacheUpgrade => "Keeping full detail",
        JobKind.CacheRelease => "Releasing full detail", JobKind.Export => "Exports", JobKind.Import => "Camera imports", JobKind.LumenImport => "Lumen imports",
        JobKind.FolderScan => "Folder scans", JobKind.ContextStoreMove => "Moving photo data", _ => k.ToString(),
    };

    static List<Def> Definitions()
    {
        var d = new List<Def>
        {
            new("jobs.workers", "Jobs", "Jobs at once", "How many background jobs may run at the same time. Heavy jobs are also limited by the memory budget; thumbnails do not count.",
                "jobs", LimitKind.Integer, 1, 16, 4, nameof(WorkerCount)),
            new("jobs.render_threads", "Jobs", "Threads per job", "How many processor threads each running job may use. Lower it to keep the computer responsive while Lux works.",
                "threads", LimitKind.Integer, 1, 4 * FakeCores, FakeCores, nameof(RenderThreads)),
            new("jobs.pause_heavy_while_interacting", "Jobs", "Hold heavy jobs while I work",
                "While you scroll or edit, analysis, full-detail and large export jobs wait to start. Jobs already running continue.",
                "", LimitKind.Toggle, 0, 1, 0, nameof(PauseHeavyJobsWhileInteracting)),
            new("memory.budget_percent", "Memory", "Memory for background jobs (share)",
                "The share of this computer's memory that background jobs may plan to use together, when no fixed amount is set.",
                "%", LimitKind.Number, 10, 95, 60, nameof(MemoryBudgetPercentOfRam)),
            new("memory.budget_gb", "Memory", "Memory for background jobs (fixed)",
                "A fixed amount of memory for background jobs instead of a share. 0 = use the share above.",
                "GB", LimitKind.Number, 0, FakePhysicalMemoryGb, 0, nameof(MemoryBudgetFixedGb)),
            new("thumbnails.concurrency", "Thumbnails", "Thumbnails at once",
                "How many thumbnails are made at the same time. 0 = automatic (a quarter of the threads per job, at most 6, within the memory left).",
                "", LimitKind.Integer, 0, 16, 0, nameof(ThumbnailConcurrency)),
        };
        foreach (var r in Enum.GetValues<JobReservation>())
            d.Add(new("reserve." + r, "Memory per job", ReservationName(r),
                "The memory one such job is expected to need at most. A job starts only when this fits in the budget; the default is the measured peak.",
                "GB", LimitKind.Number, 0.25, 64, DefaultReservationGb(r), "Reservation"));
        foreach (var k in Enum.GetValues<JobKind>())
            d.Add(new("priority." + k, "Priorities", KindName(k), "Lower numbers run first. Thumbnails of what is on screen always come first.",
                "", LimitKind.Integer, 0, 9, DefaultPriority(k), "Priority"));
        return d;
    }

    Def DefOf(string key) => _byKey.TryGetValue(key, out var d) ? d : throw new LibraryException($"“{key}” is not a setting Lux knows.");

    public IReadOnlyList<LimitDescriptor> GetLimits()
    {
        var list = _defs.Select(d => new LimitDescriptor(d.Key, d.Group, d.Name, d.Description, d.Unit, d.Kind, d.Min, d.Max, d.Default, GetLimit(d.Key))).ToList();
        list.Add(new LimitDescriptor("contexts.folder", "Storage", "Folder for photos on read-only drives",
            "Where Lux keeps a photo's edits, rating and analysis when it cannot write next to the photo (a read-only drive, a shared folder). "
            + "They move next to the photo as soon as that folder can be written. Changing this folder moves what is in it.",
            "", LimitKind.Folder, 0, 0, 0, 0) { Text = LocalContextFolder, TextDefault = DefaultLocalContextFolder });
        return list;
    }

    public double GetLimit(string key)
    {
        var d = DefOf(key);
        lock (_gate) return _limits.TryGetValue(key, out var v) ? v : d.Default;
    }

    public void SetLimit(string key, double value)
    {
        var d = DefOf(key);
        string range = d.Kind == LimitKind.Toggle ? "on (1) or off (0)"
            : string.Create(CultureInfo.InvariantCulture, $"between {d.Min:0.##} and {d.Max:0.##}{(d.Unit.Length > 0 ? " " + d.Unit : "")}");
        if (double.IsNaN(value) || double.IsInfinity(value) || value < d.Min || value > d.Max) throw new LibraryException($"{d.Name} must be {range}.");
        if (d.Kind != LimitKind.Number && value != Math.Floor(value)) throw new LibraryException($"{d.Name} must be a whole number ({range}).");
        if (key == "memory.budget_gb" && value > 0 && value < 1) throw new LibraryException($"{d.Name} must be 0 (use the share) or at least 1 GB.");
        lock (_gate) _limits[key] = value;
        Raise(d.Property);
        Raise(key);
    }

    public void ResetLimit(string key)
    {
        var d = DefOf(key);
        lock (_gate) _limits.Remove(key);
        Raise(d.Property);
        Raise(key);
    }

    public void ResetLimitsToDefaults()
    {
        lock (_gate) _limits.Clear();
        foreach (var d in _defs) Raise(d.Property);
    }

    public double PhysicalMemoryGb => FakePhysicalMemoryGb;
    public double? MemoryBudgetFixedGb { get => GetLimit("memory.budget_gb") is var v && v > 0 ? v : null; set => SetLimit("memory.budget_gb", value ?? 0); }
    public double MemoryBudgetPercentOfRam { get => GetLimit("memory.budget_percent"); set => SetLimit("memory.budget_percent", value); }
    public double GetReservationGb(JobReservation reservation) => GetLimit("reserve." + reservation);
    public void SetReservationGb(JobReservation reservation, double gb) => SetLimit("reserve." + reservation, gb);
    public int ThumbnailConcurrency { get => (int)GetLimit("thumbnails.concurrency"); set => SetLimit("thumbnails.concurrency", value); }
    public bool PauseHeavyJobsWhileInteracting { get => GetLimit("jobs.pause_heavy_while_interacting") > 0; set => SetLimit("jobs.pause_heavy_while_interacting", value ? 1 : 0); }

    bool _asHeld = true, _dontAsk;   // "show as held": on by default, as in the library
    public bool ShowPhotosAsHeld { get => _asHeld; set => Set(ref _asHeld, value); }
    public bool DontAskAboutExtraStorage { get => _dontAsk; set => Set(ref _dontAsk, value); }

    public string DefaultLocalContextFolder => "/fake/Lux/contexts";
    public string LocalContextFolder => _localFolder ?? DefaultLocalContextFolder;
    internal void SwitchLocalContextFolder(string folder) { _localFolder = folder == DefaultLocalContextFolder ? null : folder; Raise(nameof(LocalContextFolder)); }

    public CacheLevel DefaultCacheLevel { get => _default; set => Set(ref _default, value); }
    public CacheLevel? GetFolderCacheLevel(long folderId) { lock (_gate) return _folderLevels.TryGetValue(folderId, out var l) ? l : null; }
    public void SetFolderCacheLevel(long folderId, CacheLevel? level)
    {
        lock (_gate) { if (level is null) _folderLevels.Remove(folderId); else _folderLevels[folderId] = level.Value; }
        Raise("FolderCacheLevel");
    }
    public CacheLevel EffectiveCacheLevel(long folderId) => GetFolderCacheLevel(folderId) ?? DefaultCacheLevel;
    public bool AnalyseNewCaptures { get => _analyseNew; set => Set(ref _analyseNew, value); }
    public bool WriteRatingsToCaptureFolders { get => _writeRatings; set => Set(ref _writeRatings, value); }
    public bool WatchFolders { get => _watch; set => Set(ref _watch, value); }
    public int WorkerCount { get => (int)GetLimit("jobs.workers"); set => SetLimit("jobs.workers", value); }
    public int RenderThreads { get => (int)GetLimit("jobs.render_threads"); set => SetLimit("jobs.render_threads", value); }
    public double MemoryBudgetGb
    {
        get => MemoryBudgetFixedGb ?? Math.Max(1, PhysicalMemoryGb * MemoryBudgetPercentOfRam / 100);
        set => SetLimit("memory.budget_gb", value);
    }
    public int PriorityOf(JobKind kind) => (int)GetLimit("priority." + kind);
    public void SetPriority(JobKind kind, int priority) => SetLimit("priority." + kind, priority);
    public string? DefaultExportFolder { get => _exportFolder; set => Set(ref _exportFolder, value); }
    ExportCollisionSetting _ifExists;
    public ExportCollisionSetting ExportIfExists { get => _ifExists; set => Set(ref _ifExists, value); }
    bool _exportUnturned;
    public bool ExportUnturned { get => _exportUnturned; set => Set(ref _exportUnturned, value); }

    public string? GetValue(string key) { lock (_gate) return _values.TryGetValue(key, out var v) ? v : null; }
    public void SetValue(string key, string? value)
    {
        Dictionary<string, string> copy;
        lock (_gate)
        {
            if (value is null) _values.Remove(key); else _values[key] = value;
            copy = new Dictionary<string, string>(_values);
        }
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(copy));
        }
        catch (IOException) { /* preferences are best effort */ }
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
