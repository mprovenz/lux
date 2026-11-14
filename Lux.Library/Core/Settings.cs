using System.ComponentModel;
using System.Globalization;
using Lux.Engine.Session;

namespace Lux.Library.Core;

/// <summary><see cref="ILibrarySettings"/> over the <c>setting</c> table (key → text) and <c>folder.cache_level</c>. Values are cached in
/// memory; session overrides (<see cref="LibraryOpenOptions"/>) win over stored values without being stored. Every queue limit is a
/// <see cref="LimitDef"/> (key, range, default) so the settings page can render them from <see cref="GetLimits"/>.</summary>
internal sealed class LibrarySettings : ILibrarySettings
{
    readonly LibraryDb _db;
    readonly Dictionary<string, string?> _cache = new();
    readonly LibraryOpenOptions _over;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One limit: its key, how it is shown, its range and default, and the property name raised when it changes.</summary>
    internal sealed record LimitDef(string Key, string Group, string Name, string Description, string Unit, LimitKind Kind, double Min, Func<double> Max,
                                    Func<double> Default, string Property);

    readonly List<LimitDef> _defs;
    readonly Dictionary<string, LimitDef> _byKey;

    public LibrarySettings(LibraryDb db, LibraryOpenOptions over)
    {
        _db = db; _over = over;
        foreach (var (k, v) in db.Read(c => c.Rows(null, "SELECT key, value FROM setting", r => (r.GetString(0), r.Str(1))))) _cache[k] = v;
        _defs = Definitions();
        _byKey = _defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
    }

    public string DatabasePath => _db.Path;

    static int Cores => Environment.ProcessorCount;
    public double PhysicalMemoryGb => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30);

    public static double DefaultReservationGb(JobReservation r) => r switch
    {
        JobReservation.Analysis => 5.5,
        JobReservation.AnalysisStacked => 7.5,
        JobReservation.FullDetail => 6.5,
        JobReservation.FullDetailStacked => 8.5,
        JobReservation.Export => 6.5,
        JobReservation.ExportStacked => 8.5,
        JobReservation.ExportSmall => 1.5,
        JobReservation.Thumbnail => 1.5,
        _ => 1.0,
    };

    static string ReservationName(JobReservation r) => r switch
    {
        JobReservation.Analysis => "Analysis",
        JobReservation.AnalysisStacked => "Analysis, low-light photo",
        JobReservation.FullDetail => "Keeping full detail",
        JobReservation.FullDetailStacked => "Keeping full detail, low-light photo",
        JobReservation.Export => "Full-size export",
        JobReservation.ExportStacked => "Full-size export, low-light photo",
        JobReservation.ExportSmall => "Smaller export",
        JobReservation.Thumbnail => "One thumbnail",
        _ => r.ToString(),
    };

    static string KindName(JobKind k) => k switch
    {
        JobKind.Thumbnail => "Thumbnails", JobKind.Analysis => "Analysis", JobKind.CacheBuild => "Keeping renders", JobKind.CacheUpgrade => "Keeping full detail",
        JobKind.CacheRelease => "Releasing full detail", JobKind.Export => "Exports", JobKind.Import => "Camera imports", JobKind.LumenImport => "Lumen imports",
        JobKind.FolderScan => "Folder scans", _ => k.ToString(),
    };

    List<LimitDef> Definitions()
    {
        var d = new List<LimitDef>
        {
            new("jobs.workers", "Jobs", "Jobs at once", "How many background jobs may run at the same time. Heavy jobs are also limited by the memory budget; thumbnails do not count.",
                "jobs", LimitKind.Integer, 1, () => 16, () => 4, nameof(WorkerCount)),
            new("jobs.render_threads", "Jobs", "Threads per job", "How many processor threads each running job may use. Lower it to keep the computer responsive while Lux works.",
                "threads", LimitKind.Integer, 1, () => 4 * Cores, () => Cores, nameof(RenderThreads)),
            new("jobs.pause_heavy_while_interacting", "Jobs", "Hold heavy jobs while I work",
                "While you scroll or edit, analysis, full-detail and large export jobs wait to start. Jobs already running continue.",
                "", LimitKind.Toggle, 0, () => 1, () => 0, nameof(PauseHeavyJobsWhileInteracting)),
            new("memory.budget_percent", "Memory", "Memory for background jobs (share)",
                "The share of this computer's memory that background jobs may plan to use together, when no fixed amount is set.",
                "%", LimitKind.Number, 10, () => 95, () => 60, nameof(MemoryBudgetPercentOfRam)),
            new("memory.budget_gb", "Memory", "Memory for background jobs (fixed)",
                "A fixed amount of memory for background jobs instead of a share. 0 = use the share above.",
                "GB", LimitKind.Number, 0, () => Math.Floor(PhysicalMemoryGb * 10) / 10, () => 0, nameof(MemoryBudgetFixedGb)),
            new("thumbnails.concurrency", "Thumbnails", "Thumbnails at once",
                "How many thumbnails are made at the same time. 0 = automatic (a quarter of the threads per job, at most 6, within the memory left).",
                "", LimitKind.Integer, 0, () => 16, () => 0, nameof(ThumbnailConcurrency)),
            // ---- the Develop page's viewer
            new("viewer.drag_budget_ms", "Viewer", "Time per picture while dragging a slider",
                "While a slider moves, the picture is shown at a lower resolution so each update takes about this long; it sharpens again when you let go.",
                "ms", LimitKind.Integer, 15, () => 500, () => 60, nameof(Viewer)),
            new("viewer.drag_coarsest_level", "Viewer", "Lowest resolution while dragging",
                "The smallest picture size used while a slider moves: 2 = a quarter of the 13 MP picture, 4 = the smallest preview.",
                "", LimitKind.Integer, 2, () => 4, () => 4, nameof(Viewer)),
            new("viewer.save_delay_ms", "Viewer", "Save edits after",
                "How long Lux waits after your last change before saving the edits with the photo. Edits are always saved when the photo is closed.",
                "ms", LimitKind.Integer, 0, () => 10000, () => 800, nameof(Viewer)),
            new("viewer.display_cache_mb", "Viewer", "Memory for fast slider changes",
                "Memory kept for the photo being edited so that a slider change only redoes the processing after it. About 30 MB per 512-pixel tile on screen.",
                "MB", LimitKind.Integer, 128, () => 16384, () => 1024, nameof(Viewer)),
            new("viewer.threads", "Viewer", "Threads for the editor",
                "How many processor threads the editor's picture may use.",
                "threads", LimitKind.Integer, 1, () => 4 * Cores, () => Cores, nameof(Viewer)),
        };
        foreach (var r in Enum.GetValues<JobReservation>())
            d.Add(new("reserve." + r, "Memory per job", ReservationName(r),
                      "The memory one such job is expected to need at most. A job starts only when this fits in the budget; the default is the measured peak.",
                      "GB", LimitKind.Number, 0.25, () => 64, () => DefaultReservationGb(r), "Reservation"));
        foreach (var k in Enum.GetValues<JobKind>())
            d.Add(new("priority." + k, "Priorities", KindName(k), "Lower numbers run first. Thumbnails of what is on screen always come first.",
                      "", LimitKind.Integer, 0, () => 9, () => DefaultPriority(k), "Priority"));
        return d;
    }

    // ------------------------------------------------------------------------------------------------ storage

    string? Raw(string key) { lock (_cache) return _cache.TryGetValue(key, out var v) ? v : null; }

    void Store(string key, string? value, string? prop)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var old) && old == value) return;
            if (!_cache.ContainsKey(key) && value is null) return;
            _cache[key] = value;
        }
        _db.Write((c, tx) =>
        {
            if (value is null) c.Exec(tx, "DELETE FROM setting WHERE key=$k", ("$k", key));
            else c.Exec(tx, "INSERT INTO setting(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
        });
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        if (prop != key) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
    }

    bool Bool(string key, bool dflt) => Raw(key) is string s ? s == "1" : dflt;
    static string S(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------ limits

    LimitDef Def(string key) => _byKey.TryGetValue(key, out var d) ? d : throw new LibraryException($"“{key}” is not a setting Lux knows.");

    double Stored(LimitDef d) => double.TryParse(Raw(d.Key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d.Default();

    public double GetLimit(string key)
    {
        var d = Def(key);
        return key switch
        {
            "jobs.workers" when _over.WorkerCount is int w => w,
            "jobs.render_threads" when _over.RenderThreads is int t => t,
            "memory.budget_gb" when _over.MemoryBudgetGb is double g => g,
            _ => Stored(d),
        };
    }

    public void SetLimit(string key, double value)
    {
        var d = Def(key);
        double max = d.Max();
        string range = d.Kind == LimitKind.Toggle ? "on (1) or off (0)" : string.Create(CultureInfo.InvariantCulture, $"between {d.Min:0.##} and {max:0.##}{(d.Unit.Length > 0 ? " " + d.Unit : "")}");
        if (double.IsNaN(value) || double.IsInfinity(value) || value < d.Min || value > max) throw new LibraryException($"{d.Name} must be {range}.");
        if (d.Kind != LimitKind.Number && value != Math.Floor(value)) throw new LibraryException($"{d.Name} must be a whole number ({range}).");
        if (key == "memory.budget_gb" && value > 0 && value < 1) throw new LibraryException($"{d.Name} must be 0 (use the share) or at least 1 GB.");
        Store(key, S(value), d.Property);
    }

    public void ResetLimit(string key) { var d = Def(key); Store(key, null, d.Property); }

    public void ResetLimitsToDefaults() { foreach (var d in _defs) Store(d.Key, null, d.Property); }

    public IReadOnlyList<LimitDescriptor> GetLimits()
        => _defs.Select(d => new LimitDescriptor(d.Key, d.Group, d.Name, d.Description, d.Unit, d.Kind, d.Min, d.Max(), d.Default(), GetLimit(d.Key)))
                .Append(new LimitDescriptor(Lux.Store.LocalContextStore.SettingKey, "Storage", "Folder for photos on read-only drives",
                    "Where Lux keeps a photo's edits, rating and analysis when it cannot write next to the photo (a read-only drive, a shared folder). "
                    + "They move next to the photo as soon as that folder can be written. Changing this folder moves what is in it.",
                    "", LimitKind.Folder, 0, 0, 0, 0) { Text = LocalContextFolder, TextDefault = DefaultLocalContextFolder })
                .ToList();

    // ------------------------------------------------------------------------------------------------ the local context store

    /// <summary>Default: <c>&lt;folder of the library database&gt;/contexts</c> (= <c>&lt;app data&gt;/contexts</c> for the per-user library; a test
    /// library elsewhere keeps its store beside it, never in the real per-user folder).</summary>
    public string DefaultLocalContextFolder => Path.Combine(Path.GetDirectoryName(_db.Path)!, "contexts");
    public string LocalContextFolder => Raw(Lux.Store.LocalContextStore.SettingKey) is { Length: > 0 } f ? f : DefaultLocalContextFolder;
    /// <summary>Switch the store folder (the move job does this once every file is copied and verified).</summary>
    internal void SetLocalContextFolder(string folder)
    {
        Store(Lux.Store.LocalContextStore.SettingKey, folder == DefaultLocalContextFolder ? null : folder, nameof(LocalContextFolder));
        Lux.Store.LocalContextStore.WriteFolderPointer(Path.GetDirectoryName(Path.GetFullPath(_db.Path))!, LocalContextFolder);
    }

    // ------------------------------------------------------------------------------------------------ properties

    public CacheLevel DefaultCacheLevel
    {
        get => (CacheLevel)Math.Clamp(int.TryParse(Raw("cache.default_level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int)CacheLevel.Linear13, 0, 3);
        set => Store("cache.default_level", ((int)value).ToString(CultureInfo.InvariantCulture), nameof(DefaultCacheLevel));
    }

    public CacheLevel? GetFolderCacheLevel(long folderId)
        => _db.Read(c => c.Scalar(null, "SELECT cache_level FROM folder WHERE id=$f", ("$f", folderId))) is { } v ? (CacheLevel)Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;

    public void SetFolderCacheLevel(long folderId, CacheLevel? level)
    {
        _db.Write((c, tx) => c.Exec(tx, "UPDATE folder SET cache_level=$l WHERE id=$f", ("$l", level is null ? null : (long)level.Value), ("$f", folderId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FolderCacheLevel"));
    }

    public CacheLevel EffectiveCacheLevel(long folderId) => GetFolderCacheLevel(folderId) ?? DefaultCacheLevel;

    public bool AnalyseNewCaptures { get => Bool("analyse_new", false); set => Store("analyse_new", value ? "1" : "0", nameof(AnalyseNewCaptures)); }
    /// <summary>On by default (ratings always go with the photo); a switch for tests and read-only cases only.</summary>
    public bool WriteRatingsToCaptureFolders { get => Bool("ratings.write_to_capture_folders", true); set => Store("ratings.write_to_capture_folders", value ? "1" : "0", nameof(WriteRatingsToCaptureFolders)); }
    public bool WatchFolders { get => Bool("watch_folders", true); set => Store("watch_folders", value ? "1" : "0", nameof(WatchFolders)); }
    public bool ShowPhotosAsHeld { get => Bool("view.show_as_held", true); set => Store("view.show_as_held", value ? "1" : "0", nameof(ShowPhotosAsHeld)); }
    public bool DontAskAboutExtraStorage { get => Bool("cache.dont_ask_storage", false); set => Store("cache.dont_ask_storage", value ? "1" : "0", nameof(DontAskAboutExtraStorage)); }

    public int WorkerCount { get => (int)GetLimit("jobs.workers"); set => SetLimit("jobs.workers", value); }
    public int RenderThreads { get => (int)GetLimit("jobs.render_threads"); set => SetLimit("jobs.render_threads", value); }

    public double MemoryBudgetGb
    {
        get
        {
            if (_over.MemoryBudgetGb is double o) return Math.Max(1, o);
            double fixedGb = Stored(Def("memory.budget_gb"));
            return fixedGb > 0 ? fixedGb : Math.Max(1, PhysicalMemoryGb * Stored(Def("memory.budget_percent")) / 100);
        }
        set => SetLimit("memory.budget_gb", value);
    }

    public double? MemoryBudgetFixedGb
    {
        get { double v = GetLimit("memory.budget_gb"); return v > 0 ? v : null; }
        set => SetLimit("memory.budget_gb", value ?? 0);
    }

    public double MemoryBudgetPercentOfRam { get => GetLimit("memory.budget_percent"); set => SetLimit("memory.budget_percent", value); }

    public double GetReservationGb(JobReservation reservation) => GetLimit("reserve." + reservation);
    public void SetReservationGb(JobReservation reservation, double gb) => SetLimit("reserve." + reservation, gb);

    public int ThumbnailConcurrency { get => (int)GetLimit("thumbnails.concurrency"); set => SetLimit("thumbnails.concurrency", value); }
    public bool PauseHeavyJobsWhileInteracting { get => GetLimit("jobs.pause_heavy_while_interacting") != 0; set => SetLimit("jobs.pause_heavy_while_interacting", value ? 1 : 0); }

    public static int DefaultPriority(JobKind k) => k switch
    {
        JobKind.ContextStoreMove => 0,
        JobKind.Thumbnail => 1,
        JobKind.FolderScan => 1,
        JobKind.LumenImport => 2,
        JobKind.Import => 2,
        JobKind.Analysis => 3,
        JobKind.CacheBuild or JobKind.CacheUpgrade or JobKind.CacheRelease => 4,
        JobKind.Export => 5,
        _ => 5,
    };

    public int PriorityOf(JobKind kind) => (int)GetLimit("priority." + kind);
    public void SetPriority(JobKind kind, int priority) => SetLimit("priority." + kind, priority);

    public ViewerSettings Viewer => new()
    {
        DragBudgetMs = GetLimit("viewer.drag_budget_ms"), DragCoarsestLevel = (int)GetLimit("viewer.drag_coarsest_level"),
        SaveDelayMs = (int)GetLimit("viewer.save_delay_ms"), DisplayCacheMb = (int)GetLimit("viewer.display_cache_mb"), Threads = (int)GetLimit("viewer.threads"),
    };

    public string? DefaultExportFolder { get => Raw("export.default_folder"); set => Store("export.default_folder", value, nameof(DefaultExportFolder)); }

    public bool ExportUnturned { get => Bool("export.unturned", false); set => Store("export.unturned", value ? "1" : "0", nameof(ExportUnturned)); }

    public ExportCollisionSetting ExportIfExists
    {
        get => Raw("export.if_exists") switch { "ask" => ExportCollisionSetting.Ask, "replace" => ExportCollisionSetting.Replace, "skip" => ExportCollisionSetting.Skip, _ => ExportCollisionSetting.AddNumber };
        set => Store("export.if_exists", value switch { ExportCollisionSetting.Ask => "ask", ExportCollisionSetting.Replace => "replace", ExportCollisionSetting.Skip => "skip", _ => "number" }, nameof(ExportIfExists));
    }

    /// <summary>The export folder when none is set: <c>Pictures/Lux Export</c>.</summary>
    public string ResolvedExportFolder => DefaultExportFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Lux Export");

    public string? GetValue(string key) => Raw("ui." + key);
    public void SetValue(string key, string? value) => Store("ui." + key, value, "Value");
}
