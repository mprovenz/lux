using System.Collections.Concurrent;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.Fake;

/// <summary>
/// An in-memory <see cref="ILibrary"/> with synthetic captures, albums and jobs that tick — for design time, screenshots and the UI
/// tests. Behaves like the contract describes (events on a background thread, coalescing aside); nothing touches the disk except the
/// optional settings file.
/// </summary>
public sealed class FakeLibrary : ILibrary
{
    readonly object _gate = new();
    readonly FakeLibraryOptions _o;
    readonly FakeEventPump _pump = new();
    readonly List<FakeData.Capture> _all;
    readonly Dictionary<string, FakeData.Capture> _byId;
    readonly List<(long Id, string Name, DateTime Created)> _albums;
    readonly List<LibraryFolder> _folders = new();
    readonly ConcurrentDictionary<string, (JobKind Kind, long Id, JobState State, double Progress)> _activeJobs = new();
    long _generation = 1, _nextAlbum = 3, _nextFolder = 2;

    public FakeLibrary(FakeLibraryOptions? options = null)
    {
        _o = options ?? new FakeLibraryOptions();
        _all = _o.Empty ? new() : FakeData.Generate(_o, out _albums);
        _albums ??= new();
        if (_o.Empty) _albums = new();
        _byId = _all.ToDictionary(c => c.Item.Id);
        if (!_o.Empty)
            _folders.Add(new LibraryFolder { Id = 1, Path = FakeData.Root, Exists = true, AddedUtc = _o.NowUtc.AddDays(-30), LastScanUtc = _o.NowUtc.AddHours(-1) });
        if (!_o.Empty && _o.SecondFolder)
            _folders.Add(new LibraryFolder { Id = _nextFolder++, Path = FakeData.Root2, Exists = true, AddedUtc = _o.NowUtc.AddDays(-10), LastScanUtc = _o.NowUtc.AddHours(-2) });
        DatabasePath = "/fake/Lux/library.luxlib";
        FakeSettings = new FakeSettings(DatabasePath, _o.SettingsFile);
        FakeJobs = new FakeJobQueue(this, _pump, _o.AutoTick);
        FakeSettings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ILibrarySettings.ShowPhotosAsHeld)) RaiseReset(); };
        if (_o.SeedJobs && !_o.Empty) SeedJobs();
    }

    public FakeSettings FakeSettings { get; }
    public FakeJobQueue FakeJobs { get; }
    public string DatabasePath { get; }
    public ILibrarySettings Settings => FakeSettings;
    public IJobQueue Jobs => FakeJobs;
    internal DateTime Now => _o.NowUtc;

    public event EventHandler<CapturesChangedEventArgs>? CapturesChanged;
    public event EventHandler? FoldersChanged;
    public event EventHandler? AlbumsChanged;

    /// <summary>Wait until every event raised so far has been delivered (tests).</summary>
    public Task FlushEventsAsync() => _pump.FlushAsync();

    void SeedJobs()
    {
        var q = FakeJobs;
        string Id(int i) => _all[Math.Min(i, _all.Count - 1)].Item.Id;
        var full = new ExportOptions { Formats = new[] { "dng", "jpg" }, Level = 0, OutputFolder = "/fake/Pictures/Lux Export/2026-10" };
        var t = _o.NowUtc;
        q.Seed(new ExportJobRequest(Id(2), full with { DepthEffectFNumber = 2.0f }), JobState.Running, 0.62, "Rendering full detail · about 20 s left",
               title: $"Exporting {_all[2].Item.Name} as DNG + JPG · full size · f/2.0");
        q.Seed(new AnalysisJobRequest(Id(8)), JobState.Running, 0.35, "Lining up the modules · about 40 s left");
        q.Seed(new ExportJobRequest(Id(7), full), JobState.Waiting, 0, "Waiting", title: $"Exporting {_all[7].Item.Name} as DNG + JPG · full size");
        q.Seed(new AnalysisJobRequest(Id(5)), JobState.Waiting, 0, "Waiting");
        q.Seed(new ExportJobRequest(Id(0), full with { DepthEffectFNumber = 2.0f }), JobState.Done, 1, "Finished", t.AddMinutes(-13),
               title: $"Exporting {_all[0].Item.Name} as DNG + JPG · full size · f/2.0 · 4 depth edits",
               notes: new[] { "from the cached render" }, result: "/fake/Pictures/Lux Export/2026-10/L16_00493.dng");
        q.Seed(new ExportJobRequest(Id(23), full with { Formats = new[] { "jpg" }, Level = 1 }), JobState.Done, 1, "Finished", t.AddMinutes(-25),
               title: $"Exporting {_all[Math.Min(23, _all.Count - 1)].Item.Name} as JPG · 13 MP", result: "/fake/Pictures/Lux Export/2026-10/L16_00516.jpg");
        q.Seed(new ThumbnailJobRequest(_all.Take(Math.Min(146, _all.Count)).Select(c => c.Item.Id).ToList()), JobState.Done, 1, "Finished", t.AddMinutes(-34));
        q.Seed(new ExportJobRequest(Id(40), full with { Formats = new[] { "dng" } }), JobState.Failed, 0.4, "Stopped: not enough free space in the folder",
               t.AddMinutes(-47), error: "Not enough free space in the folder.", title: $"Exporting {_all[Math.Min(40, _all.Count - 1)].Item.Name} as DNG · full size");
    }

    // ---------------------------------------------------------------------------------------------------------------- folders

    public IReadOnlyList<LibraryFolder> GetFolders()
    {
        lock (_gate)
            return _folders.Select(f =>
            {
                var inside = _all.Where(c => !c.Removed && c.Item.Path.StartsWith(f.Path + "/", StringComparison.Ordinal)).ToList();
                return f with { CaptureCount = inside.Count, MissingCount = inside.Count(c => c.Item.Missing), DefaultCacheLevel = Settings.GetFolderCacheLevel(f.Id) };
            }).ToList();
    }

    internal string FolderPath(long id) { lock (_gate) return _folders.FirstOrDefault(f => f.Id == id)?.Path ?? "folder"; }

    /// <summary>Tests: the drive of a folder goes away (the folder cannot be found and its captures are missing) or comes back.</summary>
    public void SetFolderAvailableForTests(long id, bool available)
    {
        string path;
        lock (_gate)
        {
            int i = _folders.FindIndex(f => f.Id == id);
            if (i < 0) return;
            _folders[i] = _folders[i] with { Exists = available };
            path = _folders[i].Path;
        }
        var ids = Visible().Where(c => c.Item.Path.StartsWith(path + "/", StringComparison.Ordinal)).Select(c => c.Item.Id).ToList();
        Update(ids, c => c.Item with { Missing = !available });   // when the drive comes back, all captures are present
        _pump.Post(() => FoldersChanged?.Invoke(this, EventArgs.Empty));
    }

    public IReadOnlyList<SubfolderInfo> GetSubfolders(long folderId)
    {
        lock (_gate)
        {
            var f = _folders.FirstOrDefault(x => x.Id == folderId);
            if (f == null) return Array.Empty<SubfolderInfo>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in _all.Where(c => !c.Removed && c.Folder.StartsWith(f.Path, StringComparison.Ordinal)))
            {
                var rel = Path.GetRelativePath(f.Path, c.Folder).Replace('\\', '/');
                if (rel == ".") rel = "";
                counts[""] = counts.GetValueOrDefault("") + 1;
                var parts = rel.Length == 0 ? Array.Empty<string>() : rel.Split('/');
                for (int k = 1; k <= parts.Length; k++)
                {
                    var key = string.Join('/', parts.Take(k));
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            return counts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(kv => new SubfolderInfo(folderId, kv.Key.Length == 0 ? f.Path : f.Path + "/" + kv.Key, kv.Key, kv.Value)).ToList();
        }
    }

    public Task<LibraryFolder> AddFolderAsync(string path, CacheLevel? defaultCacheLevel = null, CancellationToken cancel = default)
    {
        LibraryFolder folder;
        lock (_gate)
        {
            var existing = _folders.FirstOrDefault(f => path == f.Path || path.StartsWith(f.Path + "/", StringComparison.Ordinal));
            if (existing != null) folder = existing;
            else
            {
                folder = new LibraryFolder { Id = _nextFolder++, Path = path.TrimEnd('/'), Exists = true, AddedUtc = _o.NowUtc, Scanning = true };
                _folders.Add(folder);
            }
        }
        if (defaultCacheLevel != null) Settings.SetFolderCacheLevel(folder.Id, defaultCacheLevel);
        long scan = FakeJobs.Enqueue(new FolderScanJobRequest(folder.Id));
        _pump.Post(() => FoldersChanged?.Invoke(this, EventArgs.Empty));
        return Task.FromResult(folder with { Scanning = true, ScanJobId = scan });
    }

    public Task RemoveFolderAsync(long folderId, CancellationToken cancel = default)
    {
        lock (_gate) _folders.RemoveAll(f => f.Id == folderId);
        _pump.Post(() => FoldersChanged?.Invoke(this, EventArgs.Empty));
        RaiseReset();
        return Task.CompletedTask;
    }

    public long RescanFolder(long folderId) => FakeJobs.Enqueue(new FolderScanJobRequest(folderId));
    public IReadOnlyList<long> RescanAll() => GetFolders().Select(f => RescanFolder(f.Id)).ToList();

    // ---------------------------------------------------------------------------------------------------------------- captures

    IEnumerable<FakeData.Capture> Visible()
    {
        var roots = _folders.Select(f => f.Path + "/").ToList();
        return _all.Where(c => !c.Removed && roots.Any(r => c.Item.Path.StartsWith(r, StringComparison.Ordinal)));
    }

    CaptureItem Snapshot(FakeData.Capture c)
    {
        var it = c.Item with
        {
            HasThumbnail = c.Item.HasThumbnail && !(_o.PendingThumbnailsEvery > 0 && c.Index % _o.PendingThumbnailsEvery == 1),
            DisplayRotation = FakeSettings.ShowPhotosAsHeld && c.Item.Orientation == PhotoOrientation.Normal ? c.Orientation : 0,
            BestQuality = c.Item.CacheLevel == CacheLevel.Full ? ViewQuality.FullDetail : c.Item.CacheLevel >= CacheLevel.Linear13 ? ViewQuality.Linear13 : ViewQuality.Preview,
        };
        return _activeJobs.TryGetValue(c.Item.Id, out var a)
            ? it with { ActiveJob = a.Kind, ActiveJobId = a.Id, ActiveJobState = a.State, ActiveJobProgress = a.Progress }
            : it;
    }

    IEnumerable<FakeData.Capture> Filter(CaptureQuery q, bool scopeOnly = false)
    {
        IEnumerable<FakeData.Capture> s = Visible();
        if (q.FolderId is long fid)
        {
            var root = _folders.FirstOrDefault(f => f.Id == fid)?.Path + "/";
            s = s.Where(c => c.Item.Path.StartsWith(root, StringComparison.Ordinal));
        }
        // separator-insensitive: the app builds prefixes with the platform's Path functions ('\' on Windows), the fake's paths use '/'
        if (q.PathPrefix is string pp) { string pre = FakePaths.Norm(pp).TrimEnd('/') + "/"; s = s.Where(c => FakePaths.Norm(c.Item.Path).StartsWith(pre, StringComparison.Ordinal)); }
        if (q.AlbumId is long aid) s = s.Where(c => c.Albums.Contains(aid));
        if (scopeOnly) return s;
        if (q.MinRating is > 0) s = s.Where(c => c.Item.Rating >= q.MinRating);
        if (q.DepthEdited is bool de) s = s.Where(c => c.Item.Edits.IsEdited == de);
        if (q.Stacked is bool st) s = s.Where(c => c.Item.IsStacked == st);
        if (q.Telephoto is bool tp) s = s.Where(c => c.Item.IsTelephoto == tp);
        if (q.Analysed is bool an) s = s.Where(c => (c.Item.DepthState == DepthState.Analysed) == an);
        if (q.HasLumenSession is bool hl) s = s.Where(c => c.Item.HasLumenSession == hl);
        if (q.EditedInLumen is bool el) s = s.Where(c => (c.Item.LumenSession >= LumenSessionState.SettingsChanged) == el);
        if (q.Missing is bool mi) s = s.Where(c => c.Item.Missing == mi);
        if (q.PossiblyBlurred is bool pb) s = s.Where(c => c.Item.PossiblyBlurred == pb);
        if (q.CacheLevel is CacheLevel cl) s = s.Where(c => c.Item.CacheLevel == cl);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var words = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            s = s.Where(c => words.All(w =>
                c.Item.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Item.Path.Contains(w, StringComparison.OrdinalIgnoreCase)
                || c.Item.ReferenceModule.Contains(w, StringComparison.OrdinalIgnoreCase)
                || (c.Item.Edits.Variant ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)
                || c.Albums.Any(a => _albums.First(x => x.Id == a).Name.Contains(w, StringComparison.OrdinalIgnoreCase))));
        }
        return s;
    }

    static IEnumerable<FakeData.Capture> Sort(IEnumerable<FakeData.Capture> s, CaptureQuery q)
    {
        IOrderedEnumerable<FakeData.Capture> o = q.Sort switch
        {
            CaptureSort.Name => q.Descending ? s.OrderByDescending(c => c.Item.Name, StringComparer.OrdinalIgnoreCase) : s.OrderBy(c => c.Item.Name, StringComparer.OrdinalIgnoreCase),
            CaptureSort.Rating => q.Descending ? s.OrderByDescending(c => c.Item.Rating).ThenByDescending(c => c.Item.CaptureTime) : s.OrderBy(c => c.Item.Rating).ThenBy(c => c.Item.CaptureTime),
            CaptureSort.FocalLength => q.Descending ? s.OrderByDescending(c => c.Item.FocalLengthMm).ThenByDescending(c => c.Item.CaptureTime) : s.OrderBy(c => c.Item.FocalLengthMm).ThenBy(c => c.Item.CaptureTime),
            CaptureSort.FileSize => q.Descending ? s.OrderByDescending(c => c.Item.FileSize) : s.OrderBy(c => c.Item.FileSize),
            // capture time: those without one last, either direction
            _ => q.Descending
                ? s.OrderBy(c => c.Item.CaptureTime == null).ThenByDescending(c => c.Item.CaptureTime).ThenByDescending(c => c.Item.Name)
                : s.OrderBy(c => c.Item.CaptureTime == null).ThenBy(c => c.Item.CaptureTime).ThenBy(c => c.Item.Name),
        };
        return o.ThenBy(c => c.Item.Id, StringComparer.Ordinal);
    }

    public CapturePage Query(CaptureQuery query)
    {
        lock (_gate)
        {
            var all = Sort(Filter(query), query).ToList();
            var page = all.Skip(query.Offset).Take(Math.Clamp(query.Limit, 1, 10_000)).Select(Snapshot).ToList();
            return new CapturePage(page, all.Count, query.Offset, _generation);
        }
    }

    public Task<CapturePage> QueryAsync(CaptureQuery query, CancellationToken cancel = default) => Task.Run(() => Query(query), cancel);

    public int Count(CaptureQuery query) { lock (_gate) return Filter(query).Count(); }

    public IReadOnlyList<DateGroup> GetDateGroups(CaptureQuery query)
    {
        lock (_gate)
        {
            var list = Sort(Filter(query), query).ToList();
            var groups = new List<DateGroup>();
            for (int i = 0; i < list.Count;)
            {
                var day = list[i].Item.CaptureTime is DateTime t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
                int j = i;
                while (j < list.Count && (list[j].Item.CaptureTime is DateTime u ? DateOnly.FromDateTime(u) : (DateOnly?)null) == day) j++;
                groups.Add(new DateGroup(day, j - i, i));
                i = j;
            }
            return groups;
        }
    }

    public CaptureItem? Get(string captureId) { lock (_gate) return _byId.TryGetValue(captureId, out var c) && !c.Removed ? Snapshot(c) : null; }

    public IReadOnlyList<CaptureLocation> GetLocations(string captureId)
    {
        var c = Get(captureId);
        return c == null ? Array.Empty<CaptureLocation>() : new[] { new CaptureLocation(1, c.Path, !c.Missing, c.FileSize, c.FileModifiedUtc, _o.NowUtc) };
    }

    public IReadOnlyDictionary<SmartFilter, int> GetSmartFilterCounts(CaptureQuery? scope = null)
    {
        lock (_gate)
        {
            var s = Filter(scope ?? new CaptureQuery(), scopeOnly: true).ToList();
            return new Dictionary<SmartFilter, int>
            {
                [SmartFilter.All] = s.Count,
                [SmartFilter.Rated] = s.Count(c => c.Item.Rating > 0),
                [SmartFilter.DepthEdited] = s.Count(c => c.Item.Edits.IsEdited),
                [SmartFilter.Stacked] = s.Count(c => c.Item.IsStacked),
                [SmartFilter.Telephoto] = s.Count(c => c.Item.IsTelephoto),
                [SmartFilter.NotAnalysed] = s.Count(c => c.Item.DepthState != DepthState.Analysed),
                [SmartFilter.HasLumenSession] = s.Count(c => c.Item.HasLumenSession),
                [SmartFilter.Missing] = s.Count(c => c.Item.Missing),
                [SmartFilter.FullDetail] = s.Count(c => c.Item.CacheLevel == CacheLevel.Full),
                [SmartFilter.EditedInLumen] = s.Count(c => c.Item.LumenSession >= LumenSessionState.SettingsChanged),
                [SmartFilter.PossiblyBlurred] = s.Count(c => c.Item.PossiblyBlurred),
            };
        }
    }

    public LibraryStats GetStats()
    {
        lock (_gate)
        {
            var s = Visible().ToList();
            return new LibraryStats
            {
                Captures = s.Count,
                Missing = s.Count(c => c.Item.Missing),
                Unreadable = s.Count(c => c.Item.Unreadable),
                Folders = _folders.Count,
                ByCacheLevel = s.GroupBy(c => c.Item.CacheLevel).ToDictionary(g => g.Key, g => g.Count()),
                CacheBytes = s.Sum(c => c.Item.CacheBytes),
                Thumbnails = s.Count(c => c.Item.HasThumbnail),
                ThumbnailBytes = s.Count(c => c.Item.HasThumbnail) * 24_000L,
                DatabaseBytes = 4_000_000 + s.Count * 2_000L,
            };
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- organizing

    void Update(IEnumerable<string> ids, Func<FakeData.Capture, CaptureItem> change)
    {
        var changed = new List<string>();
        lock (_gate)
        {
            foreach (var id in ids)
                if (_byId.TryGetValue(id, out var c)) { c.Item = change(c); changed.Add(id); }
            _generation++;
        }
        RaiseChanged(changed: changed);
    }

    void RaiseChanged(IReadOnlyList<string>? added = null, IReadOnlyList<string>? changed = null, IReadOnlyList<string>? removed = null)
    {
        long g; lock (_gate) g = _generation;
        var e = new CapturesChangedEventArgs { Added = added ?? Array.Empty<string>(), Changed = changed ?? Array.Empty<string>(), Removed = removed ?? Array.Empty<string>(), Generation = g };
        if (_o.CaptureEventDelayMs > 0) { _ = Task.Delay(_o.CaptureEventDelayMs).ContinueWith(_ => _pump.Post(() => CapturesChanged?.Invoke(this, e)), TaskScheduler.Default); return; }
        _pump.Post(() => CapturesChanged?.Invoke(this, e));
    }

    void RaiseReset()
    {
        long g; lock (_gate) g = ++_generation;
        _pump.Post(() => CapturesChanged?.Invoke(this, new CapturesChangedEventArgs { Reset = true, Generation = g }));
    }

    public Task SetRatingAsync(string captureId, int rating, CancellationToken cancel = default) => SetRatingsAsync(new[] { captureId }, rating, cancel);

    /// <summary>The synthetic picture's index and stored orientation (the fake editor paints the same scene as the thumbnail).</summary>
    internal (int Index, int Orientation) SceneOf(string captureId)
    {
        lock (_gate) return _byId.TryGetValue(captureId, out var c) ? (c.Index, c.Orientation) : (0, 0);
    }

    /// <summary>The fake editor saved a recipe: the edit summary (tone, crop / rotation) and the thumbnail's tint change, as the real
    /// library mirrors a capture's saved settings.</summary>
    /// <summary>The fake editor saved the depth part: the saved depth effect and the active depth version, mirrored into the edit summary.</summary>
    /// <summary>Tests: the capture's kept data changed (an analysis finished elsewhere).</summary>
    public void SetCacheLevelForTests(string captureId, CacheLevel level) =>
        Update(new[] { captureId }, c => c.Item with { CacheLevel = level, DepthState = level >= CacheLevel.Geometry ? DepthState.Analysed : c.Item.DepthState });

    internal void SetDepthForTesting(string captureId, float? fNumber, float? focusMm, string? variant, int operations, EditSource source, int variants)
    {
        lock (_gate) if (_byId.TryGetValue(captureId, out var c)) { c.SavedFNumber = fNumber; c.SavedFocusM = focusMm / 1000; }
        Update(new[] { captureId }, c => c.Item with
        {
            Edits = c.Item.Edits with
            {
                Variant = variant, OperationCount = operations, Source = source, VariantCount = variants, SavedFNumber = fNumber, SavedFocusMm = focusMm,
                HasSettings = c.Item.Edits.HasSettings || fNumber != null,
            },
            ContextLocation = c.Item.ContextLocation == ContextLocation.None ? ContextLocation.BesideCapture : c.Item.ContextLocation,
        });
    }

    internal void SetEditsForTesting(string captureId, string? adjustmentsSummary, bool cropOrRotation, SkiaSharp.SKColorFilter? tint)
    {
        lock (_gate) if (_byId.TryGetValue(captureId, out var c)) c.Tint = tint;
        Update(new[] { captureId }, c => c.Item with
        {
            Edits = c.Item.Edits with { AdjustmentsSummary = adjustmentsSummary, HasCropOrRotation = cropOrRotation, HasSettings = adjustmentsSummary != null || cropOrRotation || c.Item.Edits.HasSettings },
            ContextLocation = c.Item.ContextLocation == ContextLocation.None ? ContextLocation.BesideCapture : c.Item.ContextLocation,
            ThumbnailCurrent = false,
        });
        // the thumbnail is made again a moment later (a separate change, as the library's thumbnail job reports it)
        _ = Task.Delay(Math.Max(30, _o.ThumbnailDelayMs * 4)).ContinueWith(_ => Update(new[] { captureId }, c => c.Item with { ThumbnailCurrent = true }), TaskScheduler.Default);
    }

    public Task SetRatingsAsync(IReadOnlyCollection<string> captureIds, int rating, CancellationToken cancel = default)
    {
        rating = Math.Clamp(rating, 0, 5);
        Update(captureIds, c =>
        {
            if (rating == 0) return c.Item with { Rating = 0, RatingSource = RatingSource.None };
            if (c.Index == 6) return c.Item with { Rating = rating, RatingSource = RatingSource.Library };   // neither place writable
            return c.Item with
            {
                Rating = rating, RatingSource = RatingSource.Capture,
                ContextLocation = c.Item.ContextLocation == ContextLocation.None ? ContextLocation.BesideCapture : c.Item.ContextLocation,
            };
        });
        return Task.CompletedTask;
    }

    public IReadOnlyList<Album> GetAlbums()
    {
        lock (_gate)
            return _albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                          .Select(a => new Album(a.Id, a.Name, Visible().Count(c => c.Albums.Contains(a.Id)), a.Created)).ToList();
    }

    public IReadOnlyList<Album> GetAlbumsOf(string captureId)
    {
        lock (_gate)
            return _byId.TryGetValue(captureId, out var c) ? GetAlbums().Where(a => c.Albums.Contains(a.Id)).ToList() : Array.Empty<Album>();
    }

    public Task<Album> CreateAlbumAsync(string name, CancellationToken cancel = default)
    {
        Album a;
        lock (_gate)
        {
            if (_albums.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new LibraryException($"There is already an album called “{name}”.");
            var entry = (_nextAlbum++, name, _o.NowUtc);
            _albums.Add(entry);
            a = new Album(entry.Item1, name, 0, entry.NowUtc);
        }
        _pump.Post(() => AlbumsChanged?.Invoke(this, EventArgs.Empty));
        return Task.FromResult(a);
    }

    public Task RenameAlbumAsync(long albumId, string newName, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            int i = _albums.FindIndex(a => a.Id == albumId);
            if (i >= 0) _albums[i] = (albumId, newName, _albums[i].Created);
        }
        _pump.Post(() => AlbumsChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    public Task DeleteAlbumAsync(long albumId, CancellationToken cancel = default)
    {
        lock (_gate) { _albums.RemoveAll(a => a.Id == albumId); foreach (var c in _all) c.Albums.Remove(albumId); }
        _pump.Post(() => AlbumsChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    public Task AddToAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        lock (_gate) foreach (var id in captureIds) if (_byId.TryGetValue(id, out var c)) c.Albums.Add(albumId);
        _pump.Post(() => AlbumsChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    public Task RemoveFromAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        lock (_gate) foreach (var id in captureIds) if (_byId.TryGetValue(id, out var c)) c.Albums.Remove(albumId);
        _pump.Post(() => AlbumsChanged?.Invoke(this, EventArgs.Empty));
        RaiseChanged(changed: captureIds.ToList());
        return Task.CompletedTask;
    }

    public Task RemoveFromLibraryAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        lock (_gate) { foreach (var id in captureIds) if (_byId.TryGetValue(id, out var c)) c.Removed = true; _generation++; }
        RaiseChanged(removed: captureIds.ToList());
        _pump.Post(() => FoldersChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CaptureItem> GetRemoved() { lock (_gate) return _all.Where(c => c.Removed).Select(c => c.Item).ToList(); }

    public Task RestoreRemovedAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        lock (_gate) { foreach (var id in captureIds) if (_byId.TryGetValue(id, out var c)) c.Removed = false; _generation++; }
        RaiseChanged(added: captureIds.ToList());
        return Task.CompletedTask;
    }

    public Task<DeleteResult> DeleteFilesAsync(IReadOnlyCollection<string> captureIds, DeleteOptions options, CancellationToken cancel = default)
    {
        RemoveFromLibraryAsync(captureIds, cancel);
        return Task.FromResult(new DeleteResult(captureIds.Count, Array.Empty<string>()));
    }

    // ---------------------------------------------------------------------------------------------------------------- thumbnails

    public async Task<ThumbnailImage?> GetThumbnailAsync(string captureId, CancellationToken cancel = default)
    {
        FakeData.Capture? c;
        lock (_gate) _byId.TryGetValue(captureId, out c);
        if (c == null || c.Item.Unreadable) return null;
        if (_o.PendingThumbnailsEvery > 0 && c.Index % _o.PendingThumbnailsEvery == 1) await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false);
        if (_o.ThumbnailDelayMs > 0) await Task.Delay(_o.ThumbnailDelayMs, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        var tint = c.Tint;
        var data = await Task.Run(() => FakeThumbnails.MakeJpeg(c.Index, c.Item.IsStacked, c.Item.IsTelephoto, c.Orientation, tint), cancel).ConfigureAwait(false);
        return new ThumbnailImage(captureId, data, ThumbnailFormat.Jpeg, FakeThumbnails.Width, FakeThumbnails.Height, _o.NowUtc, false);
    }

    public ThumbnailImage? TryGetStoredThumbnail(string captureId) => null;

    public long? PrefetchThumbnails(IReadOnlyCollection<string> captureIds) =>
        captureIds.Count == 0 ? null : FakeJobs.Enqueue(new ThumbnailJobRequest(captureIds.ToList()));

    // ---------------------------------------------------------------------------------------------------------------- cache

    public IReadOnlyList<long> AnalyseNow(IReadOnlyCollection<string> captureIds) =>
        captureIds.Where(id => Get(id) is { DepthState: not DepthState.Analysed }).Select(id => FakeJobs.Enqueue(new AnalysisJobRequest(id))).ToList();

    public IReadOnlyList<long> UpgradeToFullDetail(IReadOnlyCollection<string> captureIds) =>
        captureIds.Where(id => Get(id) is { CacheLevel: not CacheLevel.Full }).Select(id => FakeJobs.Enqueue(new CacheJobRequest(id, CacheLevel.Full))).ToList();

    public IReadOnlyList<long> ReleaseFullDetail(IReadOnlyCollection<string> captureIds) =>
        captureIds.Where(id => Get(id) is { CacheLevel: CacheLevel.Full }).Select(id => FakeJobs.Enqueue(new CacheJobRequest(id, CacheLevel.Linear13) { Release = true })).ToList();

    public IReadOnlyList<long> SetCacheLevel(IReadOnlyCollection<string> captureIds, CacheLevel level)
    {
        if (level == CacheLevel.Full) return UpgradeToFullDetail(captureIds);
        var jobs = new List<long>();
        foreach (var id in captureIds)
        {
            if (Get(id) is not { } c || c.CacheLevel == level) continue;
            jobs.Add(level > c.CacheLevel
                ? FakeJobs.Enqueue(c.DepthState == DepthState.Analysed ? new CacheJobRequest(id, level) : new AnalysisJobRequest(id, level))
                : FakeJobs.Enqueue(new CacheJobRequest(id, level) { Release = true }));
        }
        return jobs;
    }

    static long SizeOf(CacheLevel l) => l switch { CacheLevel.Geometry => 15_000_000, CacheLevel.Linear13 => 68_000_000, CacheLevel.Full => 263_000_000, _ => 0 };

    public CacheEstimate EstimateCache(IReadOnlyCollection<string> captureIds, CacheLevel target)
    {
        long add = 0, free = 0; int n = 0, measured = 0;
        foreach (var id in captureIds)
        {
            var c = Get(id);
            if (c == null || c.CacheLevel == target) continue;
            n++;
            long Size(CacheLevel l) => l switch { CacheLevel.Geometry => 15_000_000, CacheLevel.Linear13 => 68_000_000, CacheLevel.Full => 263_000_000, _ => 0 };
            if (target > c.CacheLevel) add += Size(target) - Size(c.CacheLevel); else { free += c.CacheBytes - Size(target); measured++; }
        }
        return new CacheEstimate(add, free, n, measured);
    }

    // ---------------------------------------------------------------------------------------------------------------- export, imports

    public IReadOnlyList<long> Export(IReadOnlyCollection<string> captureIds, ExportOptions options)
    {
        var ids = captureIds.Distinct().ToList();
        string? batch = ids.Count > 1 ? Guid.NewGuid().ToString("N")[..12] : null;
        string? title = batch is null ? null : $"Export of {ids.Count} photos · {FakeJobQueue.FormatList(options)} · {(options.LongEdge is int le ? $"{le} px" : options.Level == 0 ? "full size" : options.Level == 1 ? "13 MP" : "smaller")}";
        return ids.Select(id => FakeJobs.Enqueue(new ExportJobRequest(id, options) { BatchId = batch, BatchTitle = title })).ToList();
    }

    /// <summary>Files that "exist" in the fake's export folder (tests of the collision question).</summary>
    public HashSet<string> ExistingExportFiles { get; } = new(FakePaths.Comparer);

    public IReadOnlyList<ExportTarget> ExportTargets(IReadOnlyCollection<string> captureIds, ExportOptions options) =>
        captureIds.Select(id => Get(id)).Where(c => c != null).Select(c =>
        {
            var files = options.Formats.Select(f => Path.Combine(options.OutputFolder ?? "/home/you/Pictures/Lux Export", options.FileNamePattern.Replace("{name}", c!.Name) + f switch { "dng" => ".dng", "hdr" => ".hdr", "ppm" => ".ppm", "jpg+depth" => "_gdepth.jpg", "depth" => "_depth.f32", "jpg-hdr" => "_hdr.jpg", "tiff16" => ".tif", "tiff-linear" => "_linear.tif", _ => ".jpg" })).ToList();
            return new ExportTarget(c!.Id, files, files.Where(ExistingExportFiles.Contains).ToList());
        }).ToList();

    public IReadOnlyList<long> Animate(IReadOnlyCollection<string> captureIds, AnimationOptions options)
    {
        var ids = captureIds.Distinct().Where(id => Get(id) is { } c && AnimationEffects.For(c).First(e => e.Effect == options.Effect).Available).ToList();
        string? batch = ids.Count > 1 ? Guid.NewGuid().ToString("N")[..12] : null;
        string? title = batch is null ? null : $"Animation of {ids.Count} photos · {AnimationEffects.NameOf(options.Effect)} · {options.LongEdge} px";
        return ids.Select(id => FakeJobs.Enqueue(new AnimationJobRequest(id, options) { BatchId = batch, BatchTitle = title })).ToList();
    }

    // ---- activities: background work that is not a Queue job; the fake starts them for tests and design time
    readonly List<(ActivityInfo Info, Action? Cancel)> _activities = new();
    long _activityIds;
    public IReadOnlyList<ActivityInfo> Activities { get { lock (_activities) return _activities.Select(a => a.Info).ToList(); } }
    public event EventHandler? ActivitiesChanged;
    public void CancelActivity(long id)
    {
        Action? c;
        lock (_activities) c = _activities.FirstOrDefault(a => a.Info.Id == id && a.Info.CanCancel).Cancel;
        c?.Invoke();
    }
    /// <summary>Start an activity (tests); update with <see cref="UpdateActivity"/>, end with <see cref="EndActivity"/>.</summary>
    public long StartActivity(string? captureId, string title, string status, Action? cancel = null)
    {
        long id;
        lock (_activities) { id = ++_activityIds; _activities.Add((new ActivityInfo(id, captureId, title, status, null, cancel != null, DateTime.UtcNow), cancel)); }
        ActivitiesChanged?.Invoke(this, EventArgs.Empty);
        return id;
    }
    public void UpdateActivity(long id, string status, double? progress)
    {
        lock (_activities) { int i = _activities.FindIndex(a => a.Info.Id == id); if (i < 0) return; _activities[i] = (_activities[i].Info with { Status = status, Progress = progress }, _activities[i].Cancel); }
        ActivitiesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void EndActivity(long id)
    {
        lock (_activities) _activities.RemoveAll(a => a.Info.Id == id);
        ActivitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<int> KeepHeldTurnAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default)
    {
        var ids = captureIds.Where(id => Get(id) is { DisplayRotation: not 0 }).ToList();
        Update(ids, c => c.Item with
        {
            Orientation = c.Orientation switch { 90 => PhotoOrientation.Rotate90, 180 => PhotoOrientation.Rotate180, 270 => PhotoOrientation.Rotate270, _ => PhotoOrientation.Normal },
            GeometryDecided = true,
        });
        return Task.FromResult(ids.Count);
    }

    /// <summary>Video export is offered (the fake pretends ffmpeg is installed unless told otherwise).</summary>
    public bool FakeVideo { get; set; } = true;
    public bool CanWriteVideo => FakeVideo;

    /// <summary>Preview frames take this long each (tests set 0).</summary>
    public int FakeFrameMs { get; set; } = 40;

    public Task<IAnimationSource> OpenAnimationAsync(string captureId, CancellationToken cancel = default)
        => Task.FromResult<IAnimationSource>(new FakeAnimationSource(this, Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.")));

    public LumenSessionInfo? GetLumenSession(string captureId) { lock (_gate) return _byId.TryGetValue(captureId, out var c) ? c.Lumen : null; }

    public long ChangeLocalContextFolder(string newFolder)
    {
        if (string.IsNullOrWhiteSpace(newFolder) || newFolder.Contains("readonly", StringComparison.OrdinalIgnoreCase))
            throw new LibraryException("Lux cannot write to that folder. Choose a folder you can write to.");
        if (newFolder.TrimEnd('/') == FakeSettings.LocalContextFolder.TrimEnd('/'))
            throw new LibraryException("That is already the folder in use.");
        return FakeJobs.Enqueue(new ContextStoreMoveJobRequest(newFolder));
    }

    /// <summary>Captures whose data is kept in the local store (the settings page shows the count).</summary>
    internal int InLocalStore() { lock (_gate) return Visible().Count(c => c.Item.ContextLocation == ContextLocation.LocalStore); }

    // ---- set-aside copies: the SupersededContextPath of the items, "<store>-superseded/<id16>-<yyyy-MM-ddTHHmmZ>"
    /// <summary>Tests: the next restore or remove fails with this message.</summary>
    public string? SetAsideFailure { get; set; }
    /// <summary>Tests: captures that count as open, so that a restore is refused.</summary>
    public HashSet<string> OpenForSetAside { get; } = new();

    public IReadOnlyList<SetAsideCopy> GetSetAsideCopies()
    {
        lock (_gate)
            return _all.Where(c => !c.Removed && c.Item.SupersededContextPath is not null).Select(c =>
            {
                string p = c.Item.SupersededContextPath!;
                var when = ParseFakeStamp(p);
                return new SetAsideCopy
                {
                    Path = p, CaptureId = c.Item.Id, PhotoName = c.Item.Name, SetAsideUtc = when, Bytes = 4_200_000 + c.Index * 1000L, HasEdits = true, HasAnalysis = c.Index % 2 == 0,
                    CanRestore = !c.Item.Missing,
                };
            }).OrderByDescending(s => s.SetAsideUtc).ToList();
    }

    static DateTime ParseFakeStamp(string p)
    {
        var m = System.Text.RegularExpressions.Regex.Match(p, @"(\d{4}-\d{2}-\d{2})T(\d{2})(\d{2})Z$");
        return m.Success ? DateTime.SpecifyKind(DateTime.Parse($"{m.Groups[1].Value} {m.Groups[2].Value}:{m.Groups[3].Value}", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
                         : new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);
    }

    CaptureItem SetAsideOwner(string path)
    {
        lock (_gate)
            return _all.FirstOrDefault(c => !c.Removed && c.Item.SupersededContextPath == path)?.Item ?? throw new LibraryException("That set-aside copy is no longer there.");
    }

    public async Task RestoreSetAsideCopyAsync(string path, CancellationToken cancel = default)
    {
        await Task.Delay(30, cancel).ConfigureAwait(false);
        var item = SetAsideOwner(path);
        if (SetAsideFailure is { } f) { SetAsideFailure = null; throw new LibraryException(f); }
        if (item.Missing) throw new LibraryException($"The file of {item.Name} is missing (its drive may not be connected), so the copy cannot be put back now.");
        if (OpenForSetAside.Contains(item.Id)) throw new LibraryException($"{item.Name} is open in Lux. Close it (go back to the Library), then restore the copy.");
        // swapped: the photo's data it replaced is set aside now (a new folder, newer)
        string folder = path[..(path.LastIndexOf('/') + 1)];
        Update(new[] { item.Id }, c => c.Item with { SupersededContextPath = $"{folder}{item.Id[..16]}-{DateTime.UtcNow:yyyy-MM-dd'T'HHmm}Z" });
    }

    public async Task RemoveSetAsideCopyAsync(string path, CancellationToken cancel = default)
    {
        await Task.Delay(30, cancel).ConfigureAwait(false);
        var item = SetAsideOwner(path);
        if (SetAsideFailure is { } f) { SetAsideFailure = null; throw new LibraryException(f); }
        Update(new[] { item.Id }, c => c.Item with { SupersededContextPath = null });
    }

    /// <summary>The tone-style choice of the last import, per capture (tests).</summary>
    public ConcurrentDictionary<string, bool> NewerToneStyleChosen { get; } = new();

    public IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds, bool newerToneStyle)
    {
        foreach (var id in captureIds) NewerToneStyleChosen[id] = newerToneStyle;
        return ImportLumenEdits(captureIds);
    }

    public IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds) =>
        captureIds.Where(id => GetLumenSession(id) != null).Select(id => FakeJobs.Enqueue(new LumenImportJobRequest(id))).ToList();

    // ---------------------------------------------------------------------------------------------------------------- the camera

    public IReadOnlyDictionary<string, string> FindByNameAndSize(IReadOnlyCollection<(string FileName, long Size)> files)
    {
        var want = files.Select(f => ILibrary.NameSizeKey(f.FileName, f.Size)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var c in _all.Where(c => !c.Removed))
            {
                string k = ILibrary.NameSizeKey(Path.GetFileName(c.Item.Path), c.Item.FileSize);
                if (want.Contains(k)) hits.TryAdd(k, c.Item.Id);
            }
        return hits;
    }

    public IReadOnlyDictionary<string, string> FindByPaths(IReadOnlyCollection<string> paths)
    {
        var want = paths.Select(p => FakePaths.Norm(Path.GetFullPath(p))).ToHashSet(StringComparer.Ordinal);
        var hits = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
            foreach (var c in _all.Where(c => !c.Removed))
                if (want.Contains(FakePaths.Norm(c.Item.Path))) hits[c.Item.Path] = c.Item.Id;
        return hits;
    }

    /// <summary>The camera-import runner registered by the camera service (the fake queue runs import jobs through it).</summary>
    public ICameraImporter? CameraImporter { get; private set; }
    public void SetCameraImporter(ICameraImporter? importer) => CameraImporter = importer;

    /// <summary>A folder scan of a folder that exists on disk (a camera import's destination in the tests): every <c>.lri</c> under it that the
    /// fake does not know yet becomes a capture (identity = SHA-256 of small files, as the real library's; its day = the file's time).</summary>
    void ScanRealFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var added = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.lri", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var fi = new FileInfo(path);
            string id;
            try
            {
                id = fi.Length <= 8 << 20
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                    : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path + fi.Length))).ToLowerInvariant();
            }
            catch (IOException) { continue; }
            string name = Path.GetFileNameWithoutExtension(path);
            lock (_gate)
            {
                if (_all.Any(c => FakePaths.Norm(c.Item.Path) == FakePaths.Norm(path))) continue;
                if (_byId.ContainsKey(id)) continue;   // the same content elsewhere: one capture (the fake keeps one location)
                int scene = int.TryParse(name.AsSpan(name.IndexOf('_') + 1), out int n) ? (n >= 1001 ? n - 1 : 1000 + n) : 1000;
                var t = fi.LastWriteTime;
                var item = new CaptureItem
                {
                    Id = id, Path = path, Name = name, CaptureTime = t, FocalLengthMm = 28, StackFrames = 1, ReferenceModule = "A1", ModuleCount = 10,
                    AllInFocusFNumber = 16f, FileSize = fi.Length, FileModifiedUtc = fi.LastWriteTimeUtc, AddedUtc = _o.NowUtc, HasThumbnail = true, ThumbnailCurrent = true,
                    Width = 8320, Height = 6240, Iso = 100, ExposureTimeSeconds = 1 / 500.0, Gain = 1f,
                    LevelSizes = new PixelSize[] { new(8320, 6240), new(4160, 3120), new(2080, 1560), new(1040, 780), new(520, 390) },
                };
                var c = new FakeData.Capture { Item = item, Index = scene, Folder = Path.GetDirectoryName(path)!, Iso = 100, Shutter = 1 / 500.0 };
                _all.Add(c);
                _byId[id] = c;
                added.Add(id);
                _generation++;
            }
        }
        if (added.Count > 0) RaiseChanged(added: added);
    }

    // ---------------------------------------------------------------------------------------------------------------- job effects

    internal void OnJobQueued(JobInfo job)
    {
        if (job.CaptureId == null || job.Kind == JobKind.Thumbnail) return;
        _activeJobs[job.CaptureId] = (job.Kind, job.Id, job.State, job.Progress);
        lock (_gate) _generation++;
        RaiseChanged(changed: new[] { job.CaptureId });
    }

    /// <summary>A job moved on: the item's active-job fields follow (an event only when it starts; progress is read with the item).</summary>
    internal void OnJobState(JobInfo job)
    {
        if (job.CaptureId == null || job.Kind == JobKind.Thumbnail || job.IsFinished) return;
        bool started = _activeJobs.TryGetValue(job.CaptureId, out var old) && old.Id == job.Id && old.State != job.State;
        if (_activeJobs.TryGetValue(job.CaptureId, out var cur) && cur.Id != job.Id && cur.State == JobState.Running && job.State != JobState.Running) return;
        _activeJobs[job.CaptureId] = (job.Kind, job.Id, job.State, job.Progress);
        if (started) { lock (_gate) _generation++; RaiseChanged(changed: new[] { job.CaptureId }); }
    }

    internal void OnJobFinished(JobInfo job, JobRequest? request)
    {
        if (job.Kind == JobKind.ContextStoreMove)
        {
            if (job.State == JobState.Done && request is ContextStoreMoveJobRequest m) FakeSettings.SwitchLocalContextFolder(m.NewFolder);
            return;
        }
        if (job.Kind == JobKind.FolderScan)
        {
            string? path = null;
            lock (_gate)
            {
                int i = _folders.FindIndex(f => f.Id == job.FolderId);
                if (i >= 0) { _folders[i] = _folders[i] with { Scanning = false, LastScanUtc = _o.NowUtc }; path = _folders[i].Path; }
            }
            if (path is not null && job.State == JobState.Done && !path.StartsWith("/fake/", StringComparison.Ordinal)) ScanRealFolder(path);
            _pump.Post(() => FoldersChanged?.Invoke(this, EventArgs.Empty));
            return;
        }
        if (job.CaptureId == null) return;
        _activeJobs.TryRemove(job.CaptureId, out _);
        if (job.State != JobState.Done) { lock (_gate) _generation++; RaiseChanged(changed: new[] { job.CaptureId }); return; }
        Update(new[] { job.CaptureId }, c => job.Kind switch
        {
            JobKind.Analysis => c.Item with { DepthState = DepthState.Analysed, CacheLevel = CacheLevel.Linear13, CacheBytes = 68_000_000 },
            JobKind.CacheUpgrade => c.Item with { DepthState = DepthState.Analysed, CacheLevel = CacheLevel.Full, CacheBytes = 263_000_000 },
            JobKind.CacheRelease when request is CacheJobRequest r => c.Item with { CacheLevel = r.Target, CacheBytes = SizeOf(r.Target) },
            JobKind.CacheBuild when request is CacheJobRequest b => c.Item with { CacheLevel = b.Target, CacheBytes = SizeOf(b.Target) },
            JobKind.LumenImport => Imported(c),
            _ => c.Item,
        });
    }

    static CaptureItem Imported(FakeData.Capture c)
    {
        if (c.Lumen != null) c.Lumen = c.Lumen with { Imported = true };
        return c.Lumen is { HasDepthEdits: true }
            ? c.Item with { Edits = new EditSummary("lumen-import", 3, EditSource.Lumen, 2, true, false) }
            : c.Item with { Edits = c.Item.Edits with { HasSettings = true } };
    }

    // ---------------------------------------------------------------------------------------------------------------- quality

    static CacheLevel LevelFor(ViewQuality q) => q switch { ViewQuality.FullDetail => CacheLevel.Full, ViewQuality.Linear13 => CacheLevel.Linear13, _ => CacheLevel.None };

    static bool Reaches(JobInfo j, CacheLevel need) => j.Kind switch
    {
        JobKind.Analysis or JobKind.CacheBuild => need <= CacheLevel.Linear13,
        JobKind.CacheUpgrade => true,
        _ => false,
    };

    public QualityState GetQualityState(string captureId, ViewQuality quality)
    {
        var item = Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var need = LevelFor(quality);
        if (quality == ViewQuality.Preview || item.CacheLevel >= need)
            return new QualityState(captureId, quality, QualityReadiness.Ready, null, 1, null, item.BestQuality, new CacheEstimate(0, 0, 0, 0), false);
        var job = FakeJobs.JobsOf(captureId).Where(j => Reaches(j, need)).OrderBy(j => j.State == JobState.Running ? 0 : 1).FirstOrDefault();
        var add = EstimateCache(new[] { captureId }, need);
        bool ask = add.BytesToAdd > 0 && !FakeSettings.DontAskAboutExtraStorage;
        return job is null
            ? new QualityState(captureId, quality, QualityReadiness.NotStarted, null, 0, null, item.BestQuality, add, ask)
            : new QualityState(captureId, quality, QualityReadiness.Processing, job.Id, job.Progress, job.Status, item.BestQuality, add, false);
    }

    public QualityState RequestQuality(string captureId, ViewQuality quality)
    {
        var item = Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var need = LevelFor(quality);
        if (quality != ViewQuality.Preview && item.CacheLevel > need)
        {
            FakeJobs.Enqueue(new CacheJobRequest(captureId, need) { Release = true, Priority = 0 });
            return GetQualityState(captureId, quality);
        }
        if (quality == ViewQuality.Preview)
        {
            if (item.CacheLevel > CacheLevel.Geometry) FakeJobs.Enqueue(new CacheJobRequest(captureId, CacheLevel.Geometry) { Release = true, Priority = 0 });
            return GetQualityState(captureId, quality);
        }
        if (item.CacheLevel >= need) return GetQualityState(captureId, quality);
        var existing = FakeJobs.JobsOf(captureId).FirstOrDefault(j => Reaches(j, need));
        if (existing is not null) { if (existing.State == JobState.Waiting) FakeJobs.SetPriority(existing.Id, 0); }
        else FakeJobs.Enqueue(need == CacheLevel.Full ? new CacheJobRequest(captureId, CacheLevel.Full) { Priority = 0 } : new AnalysisJobRequest(captureId, need) { Priority = 0 });
        return GetQualityState(captureId, quality);
    }

    public void Dispose()
    {
        FakeJobs.Dispose();
        _pump.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>The fake's paths compared whatever the separator (the fake writes '/', the app's Path functions write '\' on Windows).</summary>
internal static class FakePaths
{
    public static string Norm(string p) => p.Replace('\\', '/');
    public static readonly IEqualityComparer<string> Comparer = new SeparatorInsensitive();
    sealed class SeparatorInsensitive : IEqualityComparer<string>
    {
        public bool Equals(string? a, string? b) => a is null ? b is null : b is not null && string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
        public int GetHashCode(string s) => StringComparer.Ordinal.GetHashCode(Norm(s));
    }
}
