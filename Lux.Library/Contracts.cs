// The contract between the library core (this project) and the desktop app's user interface (Lux.App).
//
// Rules for consumers:
//  * Every type here is UI-framework-free. Events are raised on library threads (never the UI thread): marshal before touching UI state.
//  * Reads (queries, counts, thumbnails already made) are cheap and never wait for a scan or a job; writes are async and return quickly.
//  * A capture's id is the SHA-256 of its .lri file (lower-case hex): it never changes when the file is renamed or moved.
//  * Changes to this file after its first version are additive where possible.

using System.ComponentModel;
using Lux.Engine.Pipeline;
using Lux.Engine.Session;

namespace Lux.Library;

// ====================================================================================================================== opening

/// <summary>How to open a library (<see cref="LuxLibrary.Open"/>).</summary>
public sealed record LibraryOpenOptions
{
    /// <summary>The library database file; null = the per-user default (<see cref="LibraryPaths.DefaultDatabasePath"/>). Created when missing.</summary>
    public string? DatabasePath { get; init; }
    /// <summary>Start the job workers at once (false: jobs wait until <see cref="IJobQueue.ResumeAll"/>; used by tests and a "start paused" option).</summary>
    public bool StartWorkers { get; init; } = true;
    /// <summary>Watch the library's folders for changes while open; null = the <see cref="ILibrarySettings.WatchFolders"/> setting.</summary>
    public bool? WatchFolders { get; init; }
    /// <summary>Where deleted captures go instead of the system trash (tests); null = the platform's trash / recycle bin.</summary>
    public string? TrashDirectoryOverride { get; init; }
    /// <summary>Diagnostic log (any thread); null = silent.</summary>
    public Action<string>? Log { get; init; }
    /// <summary>After a folder scan, queue thumbnails for its captures that have none (or an outdated one). On by default; tests switch it off
    /// to measure scanning alone.</summary>
    public bool MakeThumbnailsAfterScan { get; init; } = true;
    /// <summary>Overrides of the stored settings for this session only (tests, command-line switches); null = the stored value.</summary>
    public int? WorkerCount { get; init; }
    /// <inheritdoc cref="WorkerCount"/>
    public double? MemoryBudgetGb { get; init; }
    /// <inheritdoc cref="WorkerCount"/>
    public int? RenderThreads { get; init; }
    /// <summary>The folder of the refined depth files (the depth network and its runtime); null = <c>models/depth</c> in the app's data
    /// folder (<see cref="LibraryPaths.DefaultDataFolder"/>).</summary>
    public string? DepthModelFolder { get; init; }
}

/// <summary>The library's per-user locations.</summary>
public static class LibraryPaths
{
    /// <summary>The app's data folder: <c>LUX_APP_DATA</c> when set (tests, portable installs); else Linux <c>$XDG_DATA_HOME/Lux</c> (default
    /// <c>~/.local/share/Lux</c>), Windows <c>%APPDATA%\Lux</c>, macOS <c>~/Library/Application Support/Lux</c>.</summary>
    public static string DefaultDataFolder => Lux.Store.LocalContextStore.AppDataFolder;

    /// <summary>The default library database: <c>&lt;data folder&gt;/library.luxlib</c> (SQLite).</summary>
    public static string DefaultDatabasePath => Path.Combine(DefaultDataFolder, "library.luxlib");
}

// ====================================================================================================================== the library

/// <summary>
/// The user's library: the folders it indexes, the captures in them, and everything organized around them (ratings, albums, thumbnails,
/// cache levels, edit summaries), plus the background job queue. One instance per open database; thread-safe.
/// </summary>
public interface ILibrary : IDisposable, IAsyncDisposable
{
    /// <summary>The library database file.</summary>
    string DatabasePath { get; }
    /// <summary>The stored settings (and the generic key/value store for UI preferences).</summary>
    ILibrarySettings Settings { get; }
    /// <summary>The one queue of background work (the app's "Queue" tab).</summary>
    IJobQueue Jobs { get; }
    /// <summary>The files of the refined depth feature (the depth network and its runtime): their state, download and removal. They are
    /// not part of Lux; they are downloaded on first use.</summary>
    Lux.DepthModel.IDepthModelService DepthModel => Lux.DepthModel.UnavailableDepthModel.Instance;

    // ---------------------------------------------------------------------------------------------------------- folders

    /// <summary>The indexed folders, in the order they were added.</summary>
    IReadOnlyList<LibraryFolder> GetFolders();

    /// <summary>The directories under an indexed folder that hold captures (for a folder tree / date-folder list), with counts.
    /// Filter on one with <see cref="CaptureQuery.PathPrefix"/>.</summary>
    IReadOnlyList<SubfolderInfo> GetSubfolders(long folderId);

    /// <summary>Add a folder (recursively) and start scanning it: returns at once with the folder entry; the scan is a
    /// <see cref="JobKind.FolderScan"/> job and captures appear (<see cref="CapturesChanged"/>) as they are indexed. Adding a folder already
    /// indexed, or one inside an indexed folder, returns the existing entry and rescans it. Throws <see cref="LibraryException"/> when the
    /// path is not a readable folder.</summary>
    Task<LibraryFolder> AddFolderAsync(string path, CacheLevel? defaultCacheLevel = null, CancellationToken cancel = default);

    /// <summary>Stop indexing a folder. Captures that have no location left in another indexed folder leave the library (their thumbnails,
    /// album memberships and library-only ratings go with them); nothing on disk is touched — ratings and edits saved next to the
    /// captures stay there and come back if the folder is added again.</summary>
    Task RemoveFolderAsync(long folderId, CancellationToken cancel = default);

    /// <summary>Rescan a folder (a <see cref="JobKind.FolderScan"/> job); returns the job id. Renamed and moved files keep their identity
    /// (content hash); files gone are flagged <see cref="CaptureItem.Missing"/>, not dropped.</summary>
    long RescanFolder(long folderId);

    /// <summary>Rescan every folder; returns the job ids.</summary>
    IReadOnlyList<long> RescanAll();

    // ---------------------------------------------------------------------------------------------------------- captures

    /// <summary>One page of captures matching <paramref name="query"/>, sorted (ties broken by id, so paging is stable). Never blocks on
    /// a scan or a job (reads use their own connection). The page carries <see cref="CapturePage.Generation"/>: when a
    /// <see cref="CapturesChanged"/> event reports a newer generation, earlier pages may be out of date.</summary>
    CapturePage Query(CaptureQuery query);

    /// <inheritdoc cref="Query"/>
    Task<CapturePage> QueryAsync(CaptureQuery query, CancellationToken cancel = default);

    /// <summary>Number of captures matching <paramref name="query"/> (its paging is ignored).</summary>
    int Count(CaptureQuery query);

    /// <summary>The captures of <paramref name="query"/> grouped by capture day (local date of the capture time; captures without one form
    /// the last group, <see cref="DateGroup.Day"/> null), in the query's order when it sorts by capture time — for the date-grouped view.
    /// <see cref="DateGroup.FirstIndex"/> is the index of the group's first capture in the full (unpaged) result.</summary>
    IReadOnlyList<DateGroup> GetDateGroups(CaptureQuery query);

    /// <summary>One capture by id; null when it is not in the library.</summary>
    CaptureItem? Get(string captureId);

    /// <summary>Every place the capture is found (the same file content in two folders is one capture with two locations).</summary>
    IReadOnlyList<CaptureLocation> GetLocations(string captureId);

    /// <summary>Counts of the smart filters within a scope (a folder, a subfolder prefix, an album, or the whole library when the scope
    /// query is null). Only the scope members of the query are used (folder, path prefix, album).</summary>
    IReadOnlyDictionary<SmartFilter, int> GetSmartFilterCounts(CaptureQuery? scope = null);

    /// <summary>Library-wide figures for the status bar.</summary>
    LibraryStats GetStats();

    // ---------------------------------------------------------------------------------------------------------- organizing

    /// <summary>Set the rating (0 = none … 5) of one capture. Never fails because of the capture's folder.
    /// <para><b>Rule.</b> A rating always goes into the capture's context database
    /// (<c>LuxImageContext/edits/&lt;hash&gt;.luxedits</c> beside the capture), which is created when it does not exist; the library holds a
    /// mirror for searching, and a library rebuilt from scratch gets the ratings back from those files. Clearing a rating (0) on a capture
    /// without a context database creates none. When the capture's folder cannot be written (read-only drive, permissions), the context
    /// database is created in the local context store instead (<see cref="ILibrarySettings.LocalContextFolder"/>; the item shows
    /// <see cref="CaptureItem.ContextLocation"/> = <see cref="ContextLocation.LocalStore"/>) and moves next to the capture once its folder can be
    /// written. Only when neither place can be written is the rating kept in the library alone (<see cref="CaptureItem.RatingNotSavedWithPhoto"/>);
    /// it is written through automatically later (at every scan, and every few minutes while the library is open). A context database that
    /// changes on disk (another machine, a Lumen import) updates the mirror at the next scan or file-system event; its rating then wins.</para></summary>
    Task SetRatingAsync(string captureId, int rating, CancellationToken cancel = default);

    /// <summary>Set the same rating on several captures (one transaction in the library; the write-through follows the rule of
    /// <see cref="SetRatingAsync"/> per capture).</summary>
    Task SetRatingsAsync(IReadOnlyCollection<string> captureIds, int rating, CancellationToken cancel = default);

    /// <summary>The albums, by name.</summary>
    IReadOnlyList<Album> GetAlbums();
    /// <summary>The albums a capture is in.</summary>
    IReadOnlyList<Album> GetAlbumsOf(string captureId);
    /// <summary>Create an album. Names are unique (case-insensitive): an existing name throws <see cref="LibraryException"/>.</summary>
    Task<Album> CreateAlbumAsync(string name, CancellationToken cancel = default);
    Task RenameAlbumAsync(long albumId, string newName, CancellationToken cancel = default);
    /// <summary>Delete an album (the captures stay in the library).</summary>
    Task DeleteAlbumAsync(long albumId, CancellationToken cancel = default);
    Task AddToAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default);
    Task RemoveFromAlbumAsync(long albumId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default);

    /// <summary>Hide captures from the library without touching any file: they leave every view and a rescan does not bring them back
    /// until <see cref="RestoreRemovedAsync"/>. Their ratings, albums and thumbnails are kept in case they are restored.</summary>
    Task RemoveFromLibraryAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default);
    /// <summary>The captures hidden with <see cref="RemoveFromLibraryAsync"/>.</summary>
    IReadOnlyList<CaptureItem> GetRemoved();
    /// <summary>Bring hidden captures back.</summary>
    Task RestoreRemovedAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default);

    /// <summary>Delete captures from disk (every location): to the trash by default. See <see cref="DeleteOptions"/> for what goes with them.</summary>
    Task<DeleteResult> DeleteFilesAsync(IReadOnlyCollection<string> captureIds, DeleteOptions options, CancellationToken cancel = default);

    // ---------------------------------------------------------------------------------------------------------- thumbnails

    /// <summary>A capture's thumbnail. Immediate when one is stored and current; otherwise a request is queued at thumbnail priority
    /// (newest requests first — what is on screen now) and the task completes when it is made. Cancel the token when the item scrolls out
    /// of view: a request not started yet is then dropped. A stored but out-of-date thumbnail (edits changed since) is returned at once
    /// with <see cref="ThumbnailImage.IsStale"/> set while a new one is made; listen to <see cref="CapturesChanged"/> for the update.
    /// Null when the capture cannot be read (missing file, damaged capture).</summary>
    Task<ThumbnailImage?> GetThumbnailAsync(string captureId, CancellationToken cancel = default);

    /// <summary>The stored thumbnail, current or not, without making one; null when none is stored.</summary>
    ThumbnailImage? TryGetStoredThumbnail(string captureId);

    /// <summary>Make the thumbnails of these captures in the background (below on-screen requests), e.g. after a scan. Returns the job id
    /// of the batch (one <see cref="JobKind.Thumbnail"/> job in the queue for the whole batch), or null when all are current.</summary>
    long? PrefetchThumbnails(IReadOnlyCollection<string> captureIds);

    // ---------------------------------------------------------------------------------------------------------- the analysis cache

    /// <summary>Analyze captures now and build their cache to the folder's default level (<see cref="ILibrarySettings.EffectiveCacheLevel"/>):
    /// one <see cref="JobKind.Analysis"/> job each. Returns the job ids (captures already at that level get none).</summary>
    IReadOnlyList<long> AnalyseNow(IReadOnlyCollection<string> captureIds);

    /// <summary>Raise captures' cache to full detail (one <see cref="JobKind.CacheUpgrade"/> job each, analyzing first where needed).</summary>
    IReadOnlyList<long> UpgradeToFullDetail(IReadOnlyCollection<string> captureIds);

    /// <summary>Release captures' full-detail tier (back to the 13 MP tier; one <see cref="JobKind.CacheRelease"/> job each — quick).</summary>
    IReadOnlyList<long> ReleaseFullDetail(IReadOnlyCollection<string> captureIds);

    /// <summary>Set captures' cache to any level (raise: build jobs; lower: release jobs).</summary>
    IReadOnlyList<long> SetCacheLevel(IReadOnlyCollection<string> captureIds, CacheLevel level);

    /// <summary>The disk space moving these captures to <paramref name="target"/> would take or free, from measured sizes where the library
    /// has them and from typical sizes otherwise (geometry ≈ 15 MB, 13 MP tier ≈ 53 MB, full detail ≈ 195 MB per capture).</summary>
    CacheEstimate EstimateCache(IReadOnlyCollection<string> captureIds, CacheLevel target);

    // ---------------------------------------------------------------------------------------------------------- export and imports

    /// <summary>Export captures (one <see cref="JobKind.Export"/> job each) with their saved edits and settings unless the options say
    /// otherwise. Returns the job ids; each finished job's <see cref="JobInfo.ResultPath"/> is the first file written.</summary>
    IReadOnlyList<long> Export(IReadOnlyCollection<string> captureIds, ExportOptions options);

    /// <summary>The files an export of these captures would write now, per capture (the names before any renaming for an existing file) and
    /// which of them already exist — so the UI can ask before queueing when <see cref="ILibrarySettings.ExportIfExists"/> says so.</summary>
    IReadOnlyList<ExportTarget> ExportTargets(IReadOnlyCollection<string> captureIds, ExportOptions options) => Array.Empty<ExportTarget>();

    /// <summary>Animated exports (one <see cref="JobKind.Animation"/> job per capture; several captures form one batch in the Queue). Captures
    /// that cannot make the effect get no job; <see cref="AnimationEffects.For"/> says why.</summary>
    IReadOnlyList<long> Animate(IReadOnlyCollection<string> captureIds, AnimationOptions options) => throw new NotSupportedException();

    /// <summary>Open a capture for animated effects: what it can make, a fast progressive preview, picking focus distances, size estimates.
    /// Reuses the photo's open editing session when it is open (the Develop page).</summary>
    Task<IAnimationSource> OpenAnimationAsync(string captureId, CancellationToken cancel = default) => throw new NotSupportedException();

    /// <summary>Save the way the camera was held as each photo's rotation (as Develop's "Keep the turn for exports": the display-only held turn
    /// becomes a saved turn, so exports — stills and animations — come out the way the photo is shown). Photos shown unturned are left alone.
    /// Returns how many were turned.</summary>
    Task<int> KeepHeldTurnAsync(IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => throw new NotSupportedException();

    /// <summary>Video (MP4, WebM) can be written: ffmpeg was found when the library was opened (or last checked).</summary>
    bool CanWriteVideo => false;

    /// <summary>The Lumen session file (<c>.lris</c>) beside a capture, when there is one.</summary>
    LumenSessionInfo? GetLumenSession(string captureId);

    /// <summary>Move the local context store (the per-photo data of captures whose folders cannot be written, and the copies kept aside after a
    /// conflict) to another folder: validated at once (the folder must exist or be creatable, and be writable — else
    /// <see cref="LibraryException"/>), then a <see cref="JobKind.ContextStoreMove"/> job copies every file, verifies it, switches the setting and
    /// deletes the old copies. If the move cannot complete, Lux keeps using the old folder and the job says why. Returns the job id.</summary>
    long ChangeLocalContextFolder(string newFolder) => throw new NotSupportedException();

    /// <summary>Gets the copies of photos' data that the library set aside after a conflict, newest first. A conflict happens when the data of a
    /// photo moves from the local store to beside the photo and a different copy is already there. The library keeps the copy beside the
    /// photo and sets the local copy aside. It never deletes the local copy.</summary>
    IReadOnlyList<SetAsideCopy> GetSetAsideCopies() => Array.Empty<SetAsideCopy>();
    /// <summary>Puts a set-aside copy back in use. The current data of the same kind is set aside in its place, so the same call can undo it.
    /// Throws a <see cref="LibraryException"/> with a plain message when the photo is open or has work in the Queue. It also throws when the
    /// photo is not in the library or its file is missing.</summary>
    Task RestoreSetAsideCopyAsync(string path, CancellationToken cancel = default) => throw new NotSupportedException();
    /// <summary>Removes a set-aside copy. Its files go to the system trash.</summary>
    Task RemoveSetAsideCopyAsync(string path, CancellationToken cancel = default) => throw new NotSupportedException();

    /// <summary>The readiness of a quality for the viewer: ready, being made (job id, progress), or not started; the best quality available
    /// meanwhile; and the space reaching it takes.</summary>
    QualityState GetQualityState(string captureId, ViewQuality quality) => throw new NotSupportedException();

    /// <summary>Make <paramref name="quality"/> available for viewing (one click): raises the capture's cache (analysis + 13 MP tier, or full
    /// detail) with a job at interactive priority — an existing job for it is moved to the front instead — or lowers it (releasing full detail
    /// / the 13 MP tier) when the quality is below what is kept. Returns the new state (its <see cref="QualityState.JobId"/>). Ask about the
    /// storage first when <see cref="QualityState.AskAboutStorage"/> (see <see cref="ILibrarySettings.DontAskAboutExtraStorage"/>).</summary>
    QualityState RequestQuality(string captureId, ViewQuality quality) => throw new NotSupportedException();

    /// <summary>Background work that is not a Queue job — an open photo's depth reading, version switch, history rebuild, selection restore,
    /// depth kept for a first edit, a Lumen import made through the open photo, an animation preview, moves of photo data out of the local
    /// store — so the Queue can show every activity that uses the computer, named after its file, with progress and a stop where one is
    /// possible. Snapshots; <see cref="ActivitiesChanged"/> (any thread, at most ten a second) says when to
    /// read again.</summary>
    IReadOnlyList<ActivityInfo> Activities => Array.Empty<ActivityInfo>();
    event EventHandler? ActivitiesChanged { add { } remove { } }
    /// <summary>Stop an activity that offers it (<see cref="ActivityInfo.CanCancel"/>); otherwise nothing.</summary>
    void CancelActivity(long id) { }

    /// <summary>As <see cref="ImportLumenEdits(IReadOnlyCollection{string})"/>, with the choice of Lumen's newer tone style for these captures
    /// (kept in their imported settings so exports apply it).</summary>
    IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds, bool newerToneStyle) => ImportLumenEdits(captureIds);

    /// <summary>Import the depth edits and settings of the Lumen sessions beside these captures (one <see cref="JobKind.LumenImport"/>
    /// job each; captures without a session are skipped). The import keeps the clean Lux version and makes the imported one active.</summary>
    IReadOnlyList<long> ImportLumenEdits(IReadOnlyCollection<string> captureIds);

    // ---------------------------------------------------------------------------------------------------------- the Develop page (EditContracts.cs)

    /// <summary>Open a photo for viewing and editing (the Develop page): reads the capture and its saved edits; nothing is rendered yet. Throws
    /// <see cref="LibraryException"/> when the photo is missing or cannot be read.</summary>
    Task<IPhotoSession> OpenPhotoAsync(string captureId, PhotoOpenOptions? options = null, CancellationToken cancel = default) => throw new NotSupportedException();

    /// <summary>Copy / paste of edits and presets.</summary>
    IEditClipboard Edits => throw new NotSupportedException();

    // ---------------------------------------------------------------------------------------------------------- the camera (CameraContracts.cs)

    /// <summary>Which of these files the library already holds: for each (file name with extension, size in bytes) that matches a location of a
    /// capture in the library (same file name, case-insensitive, and the same size; a location on a drive not connected counts), the capture's
    /// id, keyed by "<c>name|size</c>" (<see cref="NameSizeKey"/>). The camera page's "new" marking. The default reads every page of
    /// the library.</summary>
    IReadOnlyDictionary<string, string> FindByNameAndSize(IReadOnlyCollection<(string FileName, long Size)> files)
    {
        var want = files.Select(f => NameSizeKey(f.FileName, f.Size)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int offset = 0; ; )
        {
            var page = Query(new CaptureQuery { Offset = offset, Limit = 5000 });
            foreach (var c in page.Items)
            {
                string k = NameSizeKey(System.IO.Path.GetFileName(c.Path), c.FileSize);
                if (want.Contains(k)) hits[k] = c.Id;
            }
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total) break;
        }
        return hits;
    }

    /// <summary>The key of <see cref="FindByNameAndSize"/>: "<c>L16_00500.lri|162625784</c>".</summary>
    static string NameSizeKey(string fileName, long size) => fileName + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The captures at these paths (absolute file paths of locations), path → capture id; paths the library does not know are left
    /// out. The default compares each capture's primary path.</summary>
    IReadOnlyDictionary<string, string> FindByPaths(IReadOnlyCollection<string> paths)
    {
        var want = paths.Select(System.IO.Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        var hits = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int offset = 0; ; )
        {
            var page = Query(new CaptureQuery { Offset = offset, Limit = 5000 });
            foreach (var c in page.Items) if (want.Contains(c.Path)) hits[c.Path] = c.Id;
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total) break;
        }
        return hits;
    }

    /// <summary>The code that runs camera-import jobs (<see cref="CameraImportJobRequest"/>); the camera service registers itself when it starts
    /// and passes null when it stops. A camera-import job that starts with none registered fails with a plain message.</summary>
    void SetCameraImporter(ICameraImporter? importer) { }

    // ---------------------------------------------------------------------------------------------------------- notifications

    /// <summary>Captures added, changed (any field of <see cref="CaptureItem"/>, including a new thumbnail) or removed. Coalesced: at most
    /// about ten events a second; a large change is reported as <see cref="CapturesChangedEventArgs.Reset"/> (re-query everything).</summary>
    event EventHandler<CapturesChangedEventArgs>? CapturesChanged;
    /// <summary>A folder was added, removed, started or finished scanning, or changed its counts.</summary>
    event EventHandler? FoldersChanged;
    /// <summary>Albums created, renamed, deleted, or their membership changed.</summary>
    event EventHandler? AlbumsChanged;
}

/// <summary>Opens libraries.</summary>
public static class LuxLibrary
{
    /// <summary>Open (or create) a library database. The returned library is running: folders are watched and jobs left waiting when it
    /// was last closed are queued again (jobs that were running are queued again from the start; nothing they half-made is kept).</summary>
    public static ILibrary Open(LibraryOpenOptions? options = null) => Lux.Library.Core.LibraryCore.Open(options ?? new LibraryOpenOptions());
}

// ====================================================================================================================== items

/// <summary>How the capture's depth stands.</summary>
public enum DepthState
{
    /// <summary>Never analyzed: no depth kept (the depth effect, focus and depth tools need an analysis first).</summary>
    NotAnalysed = 0,
    /// <summary>Analyzed: the geometry and depth are kept in the capture's cache.</summary>
    Analysed = 1,
    /// <summary>The last analysis failed (see the job's error).</summary>
    Failed = 2,
}

/// <summary>Where the active depth edits came from.</summary>
public enum EditSource
{
    /// <summary>No saved edits (or none active).</summary>
    None = 0,
    /// <summary>Edits made in Lux.</summary>
    Lux = 1,
    /// <summary>Imported from a Lumen session.</summary>
    Lumen = 2,
}

/// <summary>A copy of a photo's data that the library set aside after a conflict (<see cref="ILibrary.GetSetAsideCopies"/>).</summary>
public sealed record SetAsideCopy
{
    /// <summary>The folder of the copy (<c>&lt;local store&gt;-superseded/&lt;id&gt;-&lt;UTC time&gt;</c>). The restore and remove calls use it as the key.</summary>
    public required string Path { get; init; }
    /// <summary>The photo it belongs to (its content hash).</summary>
    public required string CaptureId { get; init; }
    /// <summary>The name of the photo when it is in the library; otherwise null.</summary>
    public string? PhotoName { get; init; }
    /// <summary>The time (UTC) when the library set the copy aside.</summary>
    public DateTime SetAsideUtc { get; init; }
    public long Bytes { get; init; }
    /// <summary>The copy holds the edits and the rating of the photo (the edit database).</summary>
    public bool HasEdits { get; init; }
    /// <summary>The copy holds the analysis of the photo. Lux can make the analysis again.</summary>
    public bool HasAnalysis { get; init; }
    /// <summary>The photo is in the library and its file is present, so the copy can go back.</summary>
    public bool CanRestore { get; init; }
}

/// <summary>Where a capture's saved data lives (<see cref="CaptureItem.ContextLocation"/>).</summary>
public enum ContextLocation
{
    /// <summary>Nothing saved yet.</summary>
    None = 0,
    /// <summary>In <c>LuxImageContext</c> next to the capture.</summary>
    BesideCapture = 1,
    /// <summary>In the local context store on this computer (the capture's folder cannot be written); moves next to the capture when it can.</summary>
    LocalStore = 2,
}

/// <summary>Where the rating shown comes from (<see cref="ILibrary.SetRatingAsync"/>, mirror rule).</summary>
public enum RatingSource
{
    /// <summary>No rating.</summary>
    None = 0,
    /// <summary>Stored with the capture (its context database) and mirrored in the library.</summary>
    Capture = 1,
    /// <summary>Kept in the library only (the capture has no context database and creating one is not allowed by the settings).</summary>
    Library = 2,
}

/// <summary>A summary of a capture's saved edits (its context database), mirrored in the library.</summary>
/// <param name="Variant">The active variant's name; null = none active (renders use the clean depth).</param>
/// <param name="OperationCount">Operations in the active variant's history.</param>
/// <param name="Source">Where the active variant came from.</param>
/// <param name="VariantCount">Variants kept (clean / Lux / Lumen import …).</param>
/// <param name="HasSettings">The active variant carries saved render settings (f-number, focus, tone, crop / rotation).</param>
/// <param name="HistoryUnreliable">The history is marked unreliable (the app offers recovery).</param>
public sealed record EditSummary(string? Variant, int OperationCount, EditSource Source, int VariantCount, bool HasSettings, bool HistoryUnreliable)
{
    /// <summary>The saved f-number setting (the depth effect is on when it is below <see cref="CaptureItem.AllInFocusFNumber"/>); null = none
    /// saved.</summary>
    public float? SavedFNumber { get; init; }
    /// <summary>The saved focus distance, mm; null = Lumen's default focus.</summary>
    public float? SavedFocusMm { get; init; }
    /// <summary>The saved tone and color settings that differ from Lumen's, in short ("exposure +0.30, contrast 20"); null = none.</summary>
    public string? AdjustmentsSummary { get; init; }
    /// <summary>A saved crop, rotation or flip.</summary>
    public bool HasCropOrRotation { get; init; }
    /// <summary>Lumen's newer tone style was chosen for this version at import (exports apply it).</summary>
    public bool NewerToneStyle { get; init; }
    /// <summary>No saved edits.</summary>
    public static EditSummary None { get; } = new(null, 0, EditSource.None, 0, false, false);
    /// <summary>Whether depth edits are active (<see cref="Source"/> is not <see cref="EditSource.None"/>).</summary>
    public bool IsEdited => Source != EditSource.None;
}

/// <summary>One capture of the library (a snapshot; re-fetch after a <see cref="ILibrary.CapturesChanged"/>).</summary>
public sealed record CaptureItem
{
    /// <summary>The capture's identity: SHA-256 of the .lri file, lower-case hex.</summary>
    public required string Id { get; init; }
    /// <summary>The capture's primary location (the first that exists; the first known one when all are missing).</summary>
    public required string Path { get; init; }
    /// <summary>File name without extension (of <see cref="Path"/>).</summary>
    public required string Name { get; init; }
    /// <summary>Number of places the capture is found (<see cref="ILibrary.GetLocations"/>).</summary>
    public int LocationCount { get; init; } = 1;
    /// <summary>Capture time from the capture's header (camera clock, no time zone); null when the header has none.</summary>
    public DateTime? CaptureTime { get; init; }
    /// <summary>Focal length from the header, mm (35 mm equivalent; 0 = unknown).</summary>
    public int FocalLengthMm { get; init; }
    /// <summary>Frames per module (1 = single frame; more = a stacked low-light capture).</summary>
    public int StackFrames { get; init; } = 1;
    /// <summary>The reference module (e.g. "A1", "B4").</summary>
    public string ReferenceModule { get; init; } = "";
    /// <summary>Camera modules in the capture.</summary>
    public int ModuleCount { get; init; }
    /// <summary>All-in-focus f-number of the capture (the depth effect's upper end).</summary>
    public float AllInFocusFNumber { get; init; }
    /// <summary>Whether the capture is a telephoto one (made with the 70 mm and 150 mm modules only, no 28 mm module): it has no wide-angle
    /// parallax and cannot make the multi-view effects.</summary>
    public bool IsTelephoto { get; init; }
    /// <summary>Whether it is a stacked (multi-frame) capture.</summary>
    public bool IsStacked => StackFrames > 1;
    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }
    /// <summary>File modification time (UTC).</summary>
    public DateTime FileModifiedUtc { get; init; }
    /// <summary>When the capture entered the library (UTC).</summary>
    public DateTime AddedUtc { get; init; }
    /// <summary>Rating 0 (none) – 5.</summary>
    public int Rating { get; init; }
    /// <summary>Where the rating is kept.</summary>
    public RatingSource RatingSource { get; init; }
    /// <summary>The rating could not be saved in any context database (neither the capture's folder nor the local context store could be
    /// written): it is kept in the library and written through automatically later. Show it as "rating not saved with the photo".</summary>
    public bool RatingNotSavedWithPhoto => RatingSource == RatingSource.Library && Rating > 0;
    /// <summary>Where the capture's saved data (edits, settings, rating, analysis cache) lives.</summary>
    public ContextLocation ContextLocation { get; init; }
    /// <summary>A copy of this capture's data that lost a conflict (the folder was changed elsewhere while it could not be written here) and is
    /// kept aside instead of being deleted; null = none. The UI can offer it.</summary>
    public string? SupersededContextPath { get; init; }
    /// <summary>The analysis cache level the capture has now.</summary>
    public CacheLevel CacheLevel { get; init; }
    /// <summary>Bytes the capture's analysis cache takes on disk (0 = none).</summary>
    public long CacheBytes { get; init; }
    /// <summary>Depth state.</summary>
    public DepthState DepthState { get; init; }
    /// <summary>Saved edits.</summary>
    public EditSummary Edits { get; init; } = EditSummary.None;
    /// <summary>A Lumen session file (<c>.lris</c>) lies beside the capture.</summary>
    public bool HasLumenSession { get; init; }
    /// <summary>A thumbnail is stored (it may be out of date: <see cref="ThumbnailCurrent"/>).</summary>
    public bool HasThumbnail { get; init; }
    /// <summary>The stored thumbnail matches the capture's current edits and settings.</summary>
    public bool ThumbnailCurrent { get; init; }
    /// <summary>No location of the capture exists any more (moved outside the library, deleted, or its drive is not connected). Kept with
    /// its rating, albums and thumbnail until a rescan finds it again or it is removed.</summary>
    public bool Missing { get; init; }
    /// <summary>The capture could not be read (damaged or not a capture); <see cref="Error"/> says why.</summary>
    public bool Unreadable { get; init; }
    /// <summary>The reason a capture is unreadable or its last job failed (plain language); null otherwise.</summary>
    public string? Error { get; init; }
    /// <summary>The kind of the job working on this capture now (waiting or running), other than thumbnails; null = none.</summary>
    public JobKind? ActiveJob { get; init; }

    // ---- more capture facts and the readiness of the viewer's quality

    /// <summary>The job of <see cref="ActiveJob"/>: its id, state (waiting / running / paused) and progress 0–1.</summary>
    public long? ActiveJobId { get; init; }
    public JobState? ActiveJobState { get; init; }
    public double ActiveJobProgress { get; init; }
    /// <summary>ISO as Lumen writes it in its exports (100 × the capture's image gain, truncated); null when the capture records no gain.</summary>
    public int? Iso { get; init; }
    /// <summary>Exposure (integration) time, seconds, as Lumen writes it; null when unknown.</summary>
    public double? ExposureTimeSeconds { get; init; }
    /// <summary>The capture's image gain (the ISO's source).</summary>
    public float? Gain { get; init; }
    /// <summary>"1/640 s · ISO 100" (plus " · ×4 frames" for a stack); null when nothing is known.</summary>
    public string? ExposureText
    {
        get
        {
            var p = new List<string>();
            if (ExposureTimeSeconds is double t && t > 0)
                p.Add(t >= 0.3 ? t.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s" : "1/" + Math.Round(1 / t).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s");
            if (Iso is int iso) p.Add("ISO " + iso.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (StackFrames > 1) p.Add($"×{StackFrames} frames");
            return p.Count == 0 ? null : string.Join(" · ", p);
        }
    }
    /// <summary>The orientation the photo is shown and exported in, as Lumen does it: the saved photo transform (a Lumen session's or a
    /// Lux edit's crop / rotation / flips), else the capture's own view preference (the L16 writes none: normal). Thumbnails are made in it.</summary>
    public PhotoOrientation Orientation { get; init; }
    /// <summary>How the camera was held (from its accelerometer). Lumen ignores it — a portrait photo stays sideways until it is rotated — so it
    /// is information, applied only with <see cref="ILibrarySettings.ShowPhotosAsHeld"/>.</summary>
    public PhotoOrientation HeldOrientation { get; init; }
    /// <summary>The motion blur that the camera's gyroscope predicts, in full-size pixels at the center of the 70 mm modules. The value is
    /// the rotation of the camera during the exposure multiplied by the focal length. It does not see subject motion or focus errors.
    /// Null when the capture has no usable gyroscope samples, or when its header was read by an older version.</summary>
    public float? PredictedBlurPixels { get; init; }
    /// <summary>The predicted blur above which a photo is marked "possibly blurred". In a test on pairs of almost equal photos, the photo
    /// with more predicted blur was the softer one in most pairs when the blur was this large.</summary>
    public const float PossiblyBlurredPixels = 5f;
    /// <summary>The camera moved enough during the exposure to blur the photo visibly. This is a hint, not a measurement of the picture.</summary>
    public bool PossiblyBlurred => PredictedBlurPixels is float b && b > PossiblyBlurredPixels;
    /// <summary>Degrees clockwise the UI turns the photo when drawing it (thumbnail, preview): 0 unless <see cref="ILibrarySettings.ShowPhotosAsHeld"/>
    /// is on and the photo has no geometry decision of its own (<see cref="GeometryDecided"/>), then the held orientation's. Exports never
    /// turn by it.</summary>
    public int DisplayRotation { get; init; }
    /// <summary>The full-size export's pixel size as Lumen makes it (after a saved crop / rotation): what "Full size" means for this photo.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>The pixel size of each export level 0 (full size) … 4, before a saved crop / rotation.</summary>
    public IReadOnlyList<PixelSize> LevelSizes { get; init; } = Array.Empty<PixelSize>();
    /// <summary>The photo's geometry is a decision of its own (a saved crop / turn / flip, or the marker the first geometry edit sets):
    /// <see cref="DisplayRotation"/> is then 0 whatever the setting.</summary>
    public bool GeometryDecided { get; init; }
    /// <summary>What the Lumen session beside the capture did: nothing (no session), opened but unchanged, settings changed (f-number,
    /// focus, tone, crop / rotation, rating), depth edited.</summary>
    public LumenSessionState LumenSession { get; init; }
    /// <summary>The best quality the viewer can show now without waiting for a job (the cache level: full detail, the 13 MP render, else the
    /// preview levels). See <see cref="ILibrary.GetQualityState"/>.</summary>
    public ViewQuality BestQuality { get; init; }
}

/// <summary>A pixel size.</summary>
public readonly record struct PixelSize(int Width, int Height);

/// <summary>How a photo is turned (clockwise) and whether it is mirrored.</summary>
public enum PhotoOrientation
{
    Normal = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
    /// <summary>Mirrored (a flip, possibly with a turn).</summary>
    Mirrored = 4,
    /// <summary>Not known (no accelerometer data).</summary>
    Unknown = 5,
}

/// <summary>What a Lumen session did to its capture (<see cref="CaptureItem.LumenSession"/>).</summary>
public enum LumenSessionState
{
    /// <summary>No session beside the capture.</summary>
    None = 0,
    /// <summary>Opened in Lumen, nothing changed (the settings are a fresh renderer's).</summary>
    OpenedUnchanged = 1,
    /// <summary>Settings changed in Lumen (f-number, focus, tone or color, crop / rotation, rating) — no depth edits.</summary>
    SettingsChanged = 2,
    /// <summary>Depth edited in Lumen (settings may have changed too).</summary>
    DepthEdited = 3,
}

/// <summary>The image quality the viewer shows.</summary>
public enum ViewQuality
{
    /// <summary>The preview levels (2–4), made from the reference camera in a few seconds; always available.</summary>
    Preview = 0,
    /// <summary>The 13 MP render (needs the analysis and the 13 MP tier of the cache).</summary>
    Linear13 = 1,
    /// <summary>Full detail (needs the full-detail tier).</summary>
    FullDetail = 2,
}

/// <summary>Where a requested quality stands.</summary>
public enum QualityReadiness
{
    /// <summary>Available now.</summary>
    Ready = 0,
    /// <summary>A job is making it (see <see cref="QualityState.JobId"/>).</summary>
    Processing = 1,
    /// <summary>Not available and nothing is making it; <see cref="ILibrary.RequestQuality"/> starts it.</summary>
    NotStarted = 2,
}

/// <summary>The readiness of one quality of one capture (the viewer's "Processing image" placeholder).</summary>
/// <param name="JobId">The job making it (waiting or running), when <see cref="Readiness"/> is Processing.</param>
/// <param name="Progress">That job's progress 0–1 and status text.</param>
/// <param name="BestAvailable">The best quality shown meanwhile (blur it and show a spinner).</param>
/// <param name="Estimate">The disk space reaching this quality takes (or a downgrade frees) — for the storage notice.</param>
/// <param name="AskAboutStorage">Show the storage notice first (extra space needed and the user did not choose "don't ask again").</param>
public sealed record QualityState(string CaptureId, ViewQuality Quality, QualityReadiness Readiness, long? JobId, double Progress, string? Status,
                                  ViewQuality BestAvailable, CacheEstimate Estimate, bool AskAboutStorage);

/// <summary>One place a capture is found.</summary>
public sealed record CaptureLocation(long FolderId, string Path, bool Exists, long FileSize, DateTime FileModifiedUtc, DateTime LastSeenUtc);

/// <summary>An indexed folder.</summary>
public sealed record LibraryFolder
{
    public required long Id { get; init; }
    /// <summary>Absolute path as added.</summary>
    public required string Path { get; init; }
    /// <summary>The folder is reachable now (false: deleted, or a drive not connected — its captures show as missing).</summary>
    public bool Exists { get; init; }
    /// <summary>Captures with a location in this folder (missing ones included).</summary>
    public int CaptureCount { get; init; }
    /// <summary>Of those, how many are missing.</summary>
    public int MissingCount { get; init; }
    /// <summary>The folder's own default cache level; null = the app-wide default.</summary>
    public CacheLevel? DefaultCacheLevel { get; init; }
    public DateTime AddedUtc { get; init; }
    /// <summary>When the last complete scan finished (UTC); null = never completed.</summary>
    public DateTime? LastScanUtc { get; init; }
    /// <summary>A scan of the folder is waiting or running.</summary>
    public bool Scanning { get; init; }
    /// <summary>The id of that scan job (also right after <see cref="ILibrary.AddFolderAsync"/>).</summary>
    public long? ScanJobId { get; init; }
}

/// <summary>A directory under an indexed folder that holds captures.</summary>
/// <param name="Path">Absolute path (use as <see cref="CaptureQuery.PathPrefix"/>).</param>
/// <param name="RelativePath">Path relative to the indexed folder ("" = the folder itself).</param>
/// <param name="CaptureCount">Captures directly or indirectly under it.</param>
public sealed record SubfolderInfo(long FolderId, string Path, string RelativePath, int CaptureCount);

/// <summary>An album.</summary>
public sealed record Album(long Id, string Name, int CaptureCount, DateTime CreatedUtc);

/// <summary>The thumbnail of a capture: a JPEG of the capture's level-4 display render (the smallest export level, about 520 px on the long
/// side), with its saved settings applied.</summary>
/// <param name="Data">The encoded image (<see cref="Format"/>).</param>
/// <param name="IsStale">The capture's edits or settings changed since it was made; a new one is on its way.</param>
public sealed record ThumbnailImage(string CaptureId, byte[] Data, ThumbnailFormat Format, int Width, int Height, DateTime MadeUtc, bool IsStale);

/// <summary>Encoding of <see cref="ThumbnailImage.Data"/>.</summary>
public enum ThumbnailFormat { Jpeg = 0 }

/// <summary>Library-wide figures.</summary>
public sealed record LibraryStats
{
    public int Captures { get; init; }
    public int Missing { get; init; }
    public int Unreadable { get; init; }
    public int Folders { get; init; }
    /// <summary>Captures per cache level.</summary>
    public IReadOnlyDictionary<CacheLevel, int> ByCacheLevel { get; init; } = new Dictionary<CacheLevel, int>();
    /// <summary>Disk space of all analysis caches, as last measured.</summary>
    public long CacheBytes { get; init; }
    public int Thumbnails { get; init; }
    public long ThumbnailBytes { get; init; }
    /// <summary>Size of the library database file(s).</summary>
    public long DatabaseBytes { get; init; }
}

// ====================================================================================================================== queries

/// <summary>Sort orders of <see cref="CaptureQuery"/>. Ties (and captures without a value) are ordered by id, so paging is stable.</summary>
public enum CaptureSort
{
    /// <summary>Capture time (captures without one last), then name.</summary>
    CaptureTime = 0,
    /// <summary>File name (ordinal, case-insensitive).</summary>
    Name = 1,
    /// <summary>Rating, then capture time.</summary>
    Rating = 2,
    /// <summary>Focal length, then capture time.</summary>
    FocalLength = 3,
    /// <summary>File size, then capture time.</summary>
    FileSize = 4,
}

/// <summary>A capture query: scope, filters (all must match; a null filter matches everything), order and page.</summary>
public sealed record CaptureQuery
{
    public CaptureSort Sort { get; init; } = CaptureSort.CaptureTime;
    public bool Descending { get; init; }

    // ---- scope
    /// <summary>Only captures with a location in this indexed folder.</summary>
    public long? FolderId { get; init; }
    /// <summary>Only captures with a location under this directory (absolute path; <see cref="SubfolderInfo.Path"/>).</summary>
    public string? PathPrefix { get; init; }
    /// <summary>Only captures in this album.</summary>
    public long? AlbumId { get; init; }

    // ---- filters
    /// <summary>Rating at least this (1–5); 0 or null = any.</summary>
    public int? MinRating { get; init; }
    /// <summary>Depth edits active (true) / none (false).</summary>
    public bool? DepthEdited { get; init; }
    /// <summary>Stacked (true) / single-frame (false).</summary>
    public bool? Stacked { get; init; }
    /// <summary>Telephoto (true) / wide (false).</summary>
    public bool? Telephoto { get; init; }
    /// <summary>Analyzed (true) / not analyzed (false).</summary>
    public bool? Analysed { get; init; }
    /// <summary>A Lumen session lies beside it.</summary>
    public bool? HasLumenSession { get; init; }
    /// <summary>Edited in Lumen: its session has depth edits or changed settings (<see cref="LumenSessionState.SettingsChanged"/> or
    /// <see cref="LumenSessionState.DepthEdited"/>).</summary>
    public bool? EditedInLumen { get; init; }
    /// <summary>Missing (true) / present (false).</summary>
    public bool? Missing { get; init; }
    /// <summary>Possibly blurred (true) / not marked (false). See <see cref="CaptureItem.PossiblyBlurred"/>.</summary>
    public bool? PossiblyBlurred { get; init; }
    /// <summary>Exactly this cache level.</summary>
    public CacheLevel? CacheLevel { get; init; }
    /// <summary>Free text: matches (case-insensitive substring) the name, any location's path, the reference module, the active edit variant
    /// and album names. Several words must all match.</summary>
    public string? Text { get; init; }

    // ---- page
    public int Offset { get; init; }
    /// <summary>Page size (1–10 000).</summary>
    public int Limit { get; init; } = 500;
}

/// <summary>One page of a query.</summary>
/// <param name="Total">Captures matching the query (all pages).</param>
/// <param name="Generation">The library's change counter when the page was read (<see cref="CapturesChangedEventArgs.Generation"/>).</param>
public sealed record CapturePage(IReadOnlyList<CaptureItem> Items, int Total, int Offset, long Generation);

/// <summary>A capture day of <see cref="ILibrary.GetDateGroups"/>.</summary>
public sealed record DateGroup(DateOnly? Day, int Count, int FirstIndex);

/// <summary>The smart filters, with counts from <see cref="ILibrary.GetSmartFilterCounts"/>.</summary>
public enum SmartFilter
{
    All = 0,
    /// <summary>Rating ≥ 1.</summary>
    Rated = 1,
    DepthEdited = 2,
    Stacked = 3,
    Telephoto = 4,
    NotAnalysed = 5,
    HasLumenSession = 6,
    Missing = 7,
    /// <summary>Cache at full detail.</summary>
    FullDetail = 8,
    /// <summary>Edited in Lumen (depth edits or changed settings in its session).</summary>
    EditedInLumen = 9,
    /// <summary>The camera moved during the exposure (<see cref="CaptureItem.PossiblyBlurred"/>).</summary>
    PossiblyBlurred = 10,
}

/// <summary>Helpers for <see cref="SmartFilter"/>.</summary>
public static class SmartFilters
{
    /// <summary>The query with the smart filter's condition added.</summary>
    public static CaptureQuery With(this CaptureQuery q, SmartFilter f) => f switch
    {
        SmartFilter.All => q,
        SmartFilter.Rated => q with { MinRating = Math.Max(1, q.MinRating ?? 0) },
        SmartFilter.DepthEdited => q with { DepthEdited = true },
        SmartFilter.Stacked => q with { Stacked = true },
        SmartFilter.Telephoto => q with { Telephoto = true },
        SmartFilter.NotAnalysed => q with { Analysed = false },
        SmartFilter.HasLumenSession => q with { HasLumenSession = true },
        SmartFilter.Missing => q with { Missing = true },
        SmartFilter.FullDetail => q with { CacheLevel = Lux.Engine.Session.CacheLevel.Full },
        SmartFilter.EditedInLumen => q with { EditedInLumen = true },
        SmartFilter.PossiblyBlurred => q with { PossiblyBlurred = true },
        _ => q,
    };

    /// <summary>Filters the UI hides unless asked for: "Has Lumen session" (every capture that was ever opened in Lumen has one — "Edited in
    /// Lumen" is the useful one).</summary>
    public static bool HiddenByDefault(SmartFilter f) => f == SmartFilter.HasLumenSession;

    /// <summary>A plain-language label ("Rated", "Depth edited", …).</summary>
    public static string Label(SmartFilter f) => f switch
    {
        SmartFilter.All => "All photos",
        SmartFilter.Rated => "Rated",
        SmartFilter.DepthEdited => "Depth edited",
        SmartFilter.Stacked => "Low light (stacked)",
        SmartFilter.Telephoto => "Telephoto",
        SmartFilter.NotAnalysed => "Not analyzed",
        SmartFilter.HasLumenSession => "Has Lumen session",
        SmartFilter.EditedInLumen => "Edited in Lumen",
        SmartFilter.Missing => "Missing",
        SmartFilter.FullDetail => "Full detail kept",
        SmartFilter.PossiblyBlurred => "Possibly blurred",
        _ => f.ToString(),
    };
}

/// <summary>Change notification of <see cref="ILibrary.CapturesChanged"/>.</summary>
public sealed class CapturesChangedEventArgs : EventArgs
{
    public IReadOnlyList<string> Added { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Changed { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();
    /// <summary>Too much changed to list: re-query everything.</summary>
    public bool Reset { get; init; }
    /// <summary>The library's change counter after these changes.</summary>
    public long Generation { get; init; }
}

// ====================================================================================================================== actions

/// <summary>Space a cache-level change takes (positive) or frees.</summary>
/// <param name="BytesToAdd">Estimated bytes the change writes.</param>
/// <param name="BytesToFree">Bytes it releases.</param>
/// <param name="Captures">Captures whose level would change.</param>
/// <param name="Measured">Captures whose figure is measured (the rest use typical sizes).</param>
public sealed record CacheEstimate(long BytesToAdd, long BytesToFree, int Captures, int Measured);

/// <summary>What <see cref="ILibrary.DeleteFilesAsync"/> removes.</summary>
public sealed record DeleteOptions
{
    /// <summary>To the system trash / recycle bin (default); false deletes for good.</summary>
    public bool MoveToTrash { get; init; } = true;
    /// <summary>Also the Lumen session file beside the capture.</summary>
    public bool IncludeLumenSession { get; init; } = true;
    /// <summary>Also the capture's analysis cache (regenerable; default true).</summary>
    public bool IncludeCache { get; init; } = true;
    /// <summary>Also the capture's saved edits (user work; default false — they stay in <c>LuxImageContext/edits</c> and come back if the
    /// capture does).</summary>
    public bool IncludeEdits { get; init; }
}

/// <summary>Outcome of a delete.</summary>
public sealed record DeleteResult(int Deleted, IReadOnlyList<string> Failures);

/// <summary>The files an export of one capture would write (<see cref="ILibrary.ExportTargets"/>).</summary>
public sealed record ExportTarget(string CaptureId, IReadOnlyList<string> Files, IReadOnlyList<string> Existing);

/// <summary>What to do when an export's file already exists.</summary>
public enum ExistingFilePolicy
{
    /// <summary>Add -2, -3 … to the name.</summary>
    Rename = 0,
    Overwrite = 1,
    /// <summary>Skip the capture (the job finishes as done, with a note).</summary>
    Skip = 2,
}

/// <summary>
/// An export from the library — the same export as <c>lux-light convert</c> with the same settings (byte for byte, apart from the time
/// stamp in DNG / JPG). Saved edits and settings (depth effect, focus, tone, crop / rotation) are honored unless <see cref="UseSavedEdits"/>
/// is off; a value set here overrides the saved one.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>Formats, as <c>lux-light convert --formats</c> spells them: <c>dng</c>, <c>jpg</c>, <c>jpg+depth</c>, <c>hdr</c>, <c>ppm</c>,
    /// <c>tiff16</c>, <c>tiff-linear</c>, <c>jpg-hdr</c>, <c>depth</c>, <c>stereo-sbs</c>, <c>stereo-anaglyph</c>, <c>stereo-wiggle</c>,
    /// <c>stereo-mpo</c>, <c>mesh-glb</c>, <c>pointcloud-ply</c>.</summary>
    public IReadOnlyList<string> Formats { get; init; } = new[] { "dng", "jpg" };
    /// <summary>Export level: 0 = full size (Lumen's own export window) … 4; 1 = 13 MP.</summary>
    public int Level { get; init; }
    /// <summary>Output folder (created when missing); null = <see cref="ILibrarySettings.DefaultExportFolder"/>.</summary>
    public string? OutputFolder { get; init; }
    /// <summary>File name stem pattern: <c>{name}</c> = the capture's file name without extension; <c>{date}</c> = capture date
    /// (yyyy-MM-dd); <c>{time}</c> = HHmmss.</summary>
    public string FileNamePattern { get; init; } = "{name}";
    public ExistingFilePolicy IfExists { get; init; } = ExistingFilePolicy.Rename;
    /// <summary>Use the capture's saved depth edits and settings.</summary>
    public bool UseSavedEdits { get; init; } = true;
    /// <summary>The variant of the saved edits to use; null = the active one.</summary>
    public string? EditsVariant { get; init; }
    /// <summary>Depth effect f-number (overrides the saved one); null = as saved (or none).</summary>
    public float? DepthEffectFNumber { get; init; }
    /// <summary>No depth effect (all in focus), whatever is saved.</summary>
    public bool DepthEffectOff { get; init; }
    /// <summary>Depth effect focus distance, mm (overrides the saved one); null = as saved, else Lumen's default focus.</summary>
    public float? FocusDepthMm { get; init; }
    /// <summary>Tone and color (JPG only; overrides every saved tone setting); null = as saved (or none).</summary>
    public Adjustments? Adjustments { get; init; }
    /// <summary>JPG quality (1–100), for each format that is a JPG. The primary image of <c>jpg-hdr</c> uses it too, and its gain map does
    /// not. Null = Lumen's (98).</summary>
    public int? JpegQuality { get; init; }
    /// <summary>Read the capture's analysis cache (and keep the geometry it computes, as <c>convert</c> does). The cache never changes a pixel.</summary>
    public bool UseCache { get; init; } = true;
    /// <summary>A custom size: the output's long edge in pixels, the photo's own proportions (after its saved crop when edits are used); null =
    /// the level's size. Exported as <c>convert --level L --size W,H</c> would, L being the smallest level at least that size (only shrinking;
    /// a value above the full size is the full size).</summary>
    public int? LongEdge { get; init; }
    /// <summary>Lumen's tone style for JPG and DNG: null = as saved (an imported Lumen photo can carry the newer style), true = Lumen's newer
    /// style (as <c>convert --jpeg-v2 --dng-tone light_v2</c>), false = the standard style even where the newer one is saved.</summary>
    public bool? NewerToneStyle { get; init; }
    /// <summary>Orientation of a photo shown the way the camera was held (its geometry not edited): false = as shown — turned upright with
    /// Lumen's own rotate, exactly as if "Rotate as the camera was held" had been saved; true = as captured, exactly as Lumen exports it;
    /// null = the setting (<see cref="ILibrarySettings.ExportUnturned"/>). Photos with a saved crop or rotation always follow it.</summary>
    public bool? Unturned { get; init; }
    /// <summary>Embeds the color profile (sRGB) in JPG files. The default is off. Lumen's JPGs have no profile, and the default JPG does not
    /// change.</summary>
    public bool EmbedColorProfile { get; init; }
    /// <summary>The color space of the 16-bit TIFF, as <c>convert --tiff-cs</c> spells it: <c>srgb</c>, <c>p3</c> or <c>adobergb</c>.
    /// Null = sRGB.</summary>
    public string? TiffColorSpace { get; init; }
    /// <summary>Keeps the highlights of the DNG above white, as <c>convert --dng-highlights keep</c> does: the samples have half their
    /// value and BaselineExposure is one stop higher, so that a raw developer does not clip them. Some raw developers then open the
    /// picture one stop darker. The default is off, and the default DNG is Lumen's.</summary>
    public bool DngKeepHighlights { get; init; }
    /// <summary>Checks the alignment of each telephoto module after the registration and corrects a module that is clearly off, as
    /// <c>convert --tele-check</c> does. The merged picture can change slightly, and the export takes a little longer. The default
    /// is off, and the default picture is Lumen's.</summary>
    public bool TeleCheck { get; init; }
}

/// <summary>A Lumen session beside a capture.</summary>
/// <param name="HasDepthEdits">The session holds depth edits (not only settings).</param>
/// <param name="Imported">This session was already imported into the capture's edits.</param>
public sealed record LumenSessionInfo(string Path, long Bytes, DateTime ModifiedUtc, bool HasDepthEdits, bool Imported)
{
    /// <summary>The session's settings differ from a fresh renderer of the capture (f-number, focus, tone, crop / rotation, rating).</summary>
    public bool HasSettingsChanges { get; init; }
    /// <summary>The classification (<see cref="CaptureItem.LumenSession"/>).</summary>
    public LumenSessionState State { get; init; }
}

/// <summary>One activity (<see cref="ILibrary.Activities"/>): <paramref name="Title"/> names what and for which photo ("Reading the depth of
/// L16_00493"), <paramref name="Status"/> the present phase, <paramref name="Progress"/> 0–1 or null when unknown.</summary>
public sealed record ActivityInfo(long Id, string? CaptureId, string Title, string Status, double? Progress, bool CanCancel, DateTime StartedUtc);

/// <summary>A failure the user should read (plain language).</summary>
public sealed class LibraryException : Exception
{
    public LibraryException(string message) : base(message) { }
    public LibraryException(string message, Exception inner) : base(message, inner) { }
}

// ====================================================================================================================== settings

/// <summary>
/// The library's stored settings (kept in the library database). Changing a property takes effect at once (the queue picks up worker
/// count and memory budget for the next job it starts) and raises <see cref="INotifyPropertyChanged.PropertyChanged"/> on the caller's
/// thread.
/// </summary>
public interface ILibrarySettings : INotifyPropertyChanged
{
    /// <summary>The library database file (read-only: chosen when the library is opened).</summary>
    string DatabasePath { get; }

    /// <summary>The cache level every analyzed capture is brought to (app-wide default): <see cref="CacheLevel.Linear13"/> (geometry + depth
    /// and the 13 MP tier) unless changed.</summary>
    CacheLevel DefaultCacheLevel { get; set; }
    /// <summary>A folder's own default cache level; null = it follows <see cref="DefaultCacheLevel"/>.</summary>
    CacheLevel? GetFolderCacheLevel(long folderId);
    void SetFolderCacheLevel(long folderId, CacheLevel? level);
    /// <summary>The level that applies to a folder (its own, else the app-wide default).</summary>
    CacheLevel EffectiveCacheLevel(long folderId);

    /// <summary>Analyze new captures automatically after a scan (to the folder's cache level). Off by default: an analysis takes about a minute
    /// and 70 MB of disk per capture at the default level.</summary>
    bool AnalyseNewCaptures { get; set; }

    /// <summary>Create a capture's context database (<c>LuxImageContext/edits</c> next to the capture) to keep its rating there. <b>On</b>, and
    /// not a user-facing choice: ratings always go with the photo. Kept as a switch for tests; off = new ratings of
    /// captures without a context database stay in the library (<see cref="ILibrary.SetRatingAsync"/>).</summary>
    bool WriteRatingsToCaptureFolders { get; set; }

    /// <summary>Watch indexed folders for changes while the library is open.</summary>
    bool WatchFolders { get; set; }

    /// <summary>Background jobs that may run at once (key <c>jobs.workers</c>, 1–16, default 4; thumbnails are outside this count; heavy jobs
    /// are also limited by the memory budget). Out-of-range values throw <see cref="LibraryException"/>. Takes effect at once: lowering it
    /// stops new starts (running jobs finish), raising it starts waiting jobs.</summary>
    int WorkerCount { get; set; }
    /// <summary>Threads each running render may use (key <c>jobs.render_threads</c>, 1 – 4 × processors, default = processors). Applies to
    /// jobs that start after the change.</summary>
    int RenderThreads { get; set; }
    /// <summary>The memory budget in effect, GB (2³⁰ bytes): <see cref="MemoryBudgetFixedGb"/> when set, else
    /// <see cref="MemoryBudgetPercentOfRam"/> of <see cref="PhysicalMemoryGb"/>. A heavy job starts only when its reservation fits in what the
    /// running jobs leave; one job always may run. Setting it sets <see cref="MemoryBudgetFixedGb"/>.</summary>
    double MemoryBudgetGb { get; set; }

    /// <summary>The priority of each kind of job (keys <c>priority.&lt;Kind&gt;</c>, 0–9, lower runs first). Defaults: thumbnail batches 1
    /// (on screen always 0), folder scans 1, Lumen imports 2, camera imports 2, analysis 3, cache builds / upgrades / releases 4, exports 5.</summary>
    int PriorityOf(JobKind kind);
    void SetPriority(JobKind kind, int priority);

    // ---- the limits of the job queue. Default implementations keep other implementations of this interface compiling.

    /// <summary>Every limit as a descriptor (key, plain-language name and description, unit, range, default, current value) — the settings
    /// page renders these instead of hard-coding them. Keys: <c>jobs.workers</c>, <c>jobs.render_threads</c>, <c>memory.budget_gb</c>
    /// (0 = follow the percentage), <c>memory.budget_percent</c>, <c>priority.&lt;Kind&gt;</c>, <c>reserve.&lt;JobReservation&gt;</c> (GB),
    /// <c>thumbnails.concurrency</c> (0 = automatic), <c>jobs.pause_heavy_while_interacting</c> (0/1).</summary>
    IReadOnlyList<LimitDescriptor> GetLimits() => Array.Empty<LimitDescriptor>();
    /// <summary>The current value of a limit (see <see cref="GetLimits"/>).</summary>
    double GetLimit(string key) => throw new NotSupportedException();
    /// <summary>Set a limit; a value outside its range (or an unknown key) throws <see cref="LibraryException"/> with a plain message. Takes
    /// effect on the running queue at once.</summary>
    void SetLimit(string key, double value) => throw new NotSupportedException();
    /// <summary>Back to the default.</summary>
    void ResetLimit(string key) => throw new NotSupportedException();
    /// <summary>Every limit (and every reservation and priority) back to its default.</summary>
    void ResetLimitsToDefaults() { }
    /// <summary>The installed memory, GB.</summary>
    double PhysicalMemoryGb => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30);
    /// <summary>A fixed memory budget, GB (key <c>memory.budget_gb</c>, 1 – installed memory); null = <see cref="MemoryBudgetPercentOfRam"/>
    /// applies (the default).</summary>
    double? MemoryBudgetFixedGb { get => null; set { } }
    /// <summary>The memory budget as a share of the installed memory, % (key <c>memory.budget_percent</c>, 10–95, default 60); used when no
    /// fixed budget is set.</summary>
    double MemoryBudgetPercentOfRam { get => 60; set { } }
    /// <summary>The memory a job of this kind reserves while it runs, GB (keys <c>reserve.&lt;JobReservation&gt;</c>, 0.25–64; defaults are the
    /// measured peaks).</summary>
    double GetReservationGb(JobReservation reservation) => throw new NotSupportedException();
    void SetReservationGb(JobReservation reservation, double gb) => throw new NotSupportedException();
    /// <summary>Turn photos the way the camera was held when showing them (<see cref="CaptureItem.DisplayRotation"/>; thumbnails and previews
    /// only — exports stay as Lumen makes them). On by default (Lumen itself shows them unturned).</summary>
    bool ShowPhotosAsHeld { get => true; set { } }
    /// <summary>The user chose "don't ask again" on the extra-storage notice of <see cref="ILibrary.RequestQuality"/>.</summary>
    bool DontAskAboutExtraStorage { get => false; set { } }

    /// <summary>The local context store in use (key <c>contexts.folder</c>; default <c>&lt;folder of the library database&gt;/contexts</c>, i.e.
    /// <c>&lt;app data&gt;/contexts</c>): per-photo data of captures whose own folder cannot be written. Change it with
    /// <see cref="ILibrary.ChangeLocalContextFolder"/> (the data moves with it).</summary>
    string LocalContextFolder => "";
    /// <summary>The default of <see cref="LocalContextFolder"/>.</summary>
    string DefaultLocalContextFolder => "";

    /// <summary>Thumbnails made at once (key <c>thumbnails.concurrency</c>, 0–16; 0 = automatic: render threads ÷ 4, at most 6, within the
    /// memory left). Lowering it takes effect at once; raising it with the next thumbnail batch.</summary>
    int ThumbnailConcurrency { get => 0; set { } }
    /// <summary>While the user is interacting (<see cref="IJobQueue.SetInteractive"/>), heavy jobs (analysis, full-detail builds, large
    /// exports) do not start (key <c>jobs.pause_heavy_while_interacting</c>; default off: heavy jobs keep running). Running ones are not
    /// interrupted — an engine render cannot be suspended without losing its work.</summary>
    bool PauseHeavyJobsWhileInteracting { get => false; set { } }

    /// <summary>The viewer's tunables (drag frame budget, coarsest drag level, save delay, display memory, threads) — keys <c>viewer.*</c> of
    /// <see cref="GetLimits"/>.</summary>
    ViewerSettings Viewer => new();

    /// <summary>Where exports go when <see cref="ExportOptions.OutputFolder"/> is not given; null = a <c>Lux Export</c> folder in the user's
    /// Pictures folder.</summary>
    string? DefaultExportFolder { get; set; }

    /// <summary>What an export does when its file already exists (key <c>export.if_exists</c>): add a number (default: both kept), ask before
    /// queueing (<see cref="ExportCollisionSetting.Ask"/>: the app asks, the job then follows the answer), replace, or skip. Never silent
    /// replacement unless chosen.</summary>
    ExportCollisionSetting ExportIfExists { get => ExportCollisionSetting.AddNumber; set { } }
    /// <summary>Export photos unturned, as Lumen does (key <c>export.unturned</c>, default off): a photo shown the way the camera was held is
    /// then exported as the camera recorded it, not turned upright. Each export can still choose (<see cref="ExportOptions.Unturned"/>).</summary>
    bool ExportUnturned { get => false; set { } }

    /// <summary>A UI preference by key (view style per folder, window layout, …); null when unset. Stored in the library database.</summary>
    string? GetValue(string key);
    /// <summary>Store (null = remove) a UI preference.</summary>
    void SetValue(string key, string? value);
}

/// <summary>The export-collision setting (<see cref="ILibrarySettings.ExportIfExists"/>).</summary>
public enum ExportCollisionSetting
{
    /// <summary>Keep both: the new file gets -2, -3 … (default).</summary>
    AddNumber = 0,
    /// <summary>Ask before queueing when a file already exists.</summary>
    Ask = 1,
    Replace = 2,
    Skip = 3,
}

/// <summary>The memory reservations of <see cref="ILibrarySettings.GetReservationGb"/> (measured process peaks).</summary>
public enum JobReservation
{
    /// <summary>Analysis to the 13 MP tier, single-frame capture (default 5.5 GB; 1 GB less when the depth is already kept).</summary>
    Analysis = 0,
    /// <summary>The same for a stacked (low-light) capture (default 7.5 GB).</summary>
    AnalysisStacked = 1,
    /// <summary>Full-detail build, single-frame (default 6.5 GB).</summary>
    FullDetail = 2,
    /// <summary>Full-detail build, stacked (default 8.5 GB).</summary>
    FullDetailStacked = 3,
    /// <summary>Full-size export (or one that reads the depth), single-frame (default 6.5 GB; at most 3.5 GB from a full-detail cache).</summary>
    Export = 4,
    /// <summary>The same, stacked (default 8.5 GB).</summary>
    ExportStacked = 5,
    /// <summary>A smaller export (levels 2–4, no depth; default 1.5 GB, 2 GB stacked).</summary>
    ExportSmall = 6,
    /// <summary>One thumbnail being made (default 1.5 GB).</summary>
    Thumbnail = 7,
}

/// <summary>How a limit is edited.</summary>
public enum LimitKind
{
    /// <summary>A whole number.</summary>
    Integer = 0,
    /// <summary>A number (one decimal is enough).</summary>
    Number = 1,
    /// <summary>On (1) / off (0).</summary>
    Toggle = 2,
    /// <summary>A folder (<see cref="LimitDescriptor.Text"/>); changed through its own action (the local context folder:
    /// <see cref="ILibrary.ChangeLocalContextFolder"/>).</summary>
    Folder = 3,
}

/// <summary>One configurable limit of the job queue (<see cref="ILibrarySettings.GetLimits"/>).</summary>
/// <param name="Key">Stable key for <see cref="ILibrarySettings.SetLimit"/>.</param>
/// <param name="Group">Section of the settings page ("Jobs", "Memory", "Priorities", "Memory per job", "Thumbnails").</param>
/// <param name="Name">Plain-language label.</param>
/// <param name="Description">One or two plain sentences: what it does, when to change it.</param>
/// <param name="Unit">"jobs", "threads", "GB", "%", "" (priorities, toggles).</param>
public sealed record LimitDescriptor(string Key, string Group, string Name, string Description, string Unit, LimitKind Kind,
                                     double Min, double Max, double Default, double Current)
{
    /// <summary>The current value of a <see cref="LimitKind.Folder"/> setting.</summary>
    public string? Text { get; init; }
    /// <summary>Its default.</summary>
    public string? TextDefault { get; init; }
}

// ====================================================================================================================== jobs

/// <summary>Kinds of background job.</summary>
public enum JobKind
{
    /// <summary>Thumbnails (a batch; on-screen requests are folded into one running entry).</summary>
    Thumbnail = 0,
    /// <summary>Analysis (registration + depth) and the cache to the folder's default level.</summary>
    Analysis = 1,
    /// <summary>Raise a capture's cache to a level below full detail.</summary>
    CacheBuild = 2,
    /// <summary>Raise a capture's cache to full detail.</summary>
    CacheUpgrade = 3,
    /// <summary>Lower a capture's cache (release full detail).</summary>
    CacheRelease = 4,
    Export = 5,
    /// <summary>Camera import: copies from the camera, then the folder scan and the analysis chosen.</summary>
    Import = 6,
    /// <summary>Import of a Lumen session's depth edits and settings.</summary>
    LumenImport = 7,
    /// <summary>Scan of an indexed folder.</summary>
    FolderScan = 8,
    /// <summary>Moving the local context store to another folder (<see cref="ILibrary.ChangeLocalContextFolder"/>).</summary>
    ContextStoreMove = 9,
    /// <summary>An animated export (<see cref="ILibrary.Animate"/>).</summary>
    Animation = 10,
}

/// <summary>A job's state.</summary>
public enum JobState
{
    Waiting = 0,
    Running = 1,
    /// <summary>Held (by <see cref="IJobQueue.Pause"/> or <see cref="IJobQueue.PauseAll"/>); continues after a resume.</summary>
    Paused = 2,
    Done = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>A snapshot of a job.</summary>
public sealed record JobInfo
{
    public required long Id { get; init; }
    public required JobKind Kind { get; init; }
    /// <summary>The capture the job works on; null for batches and folder scans.</summary>
    public string? CaptureId { get; init; }
    /// <summary>The indexed folder (folder scans).</summary>
    public long? FolderId { get; init; }
    /// <summary>Plain-language title ("Analyzing L16_00493", "Exporting L16_00493 as DNG + JPG").</summary>
    public required string Title { get; init; }
    /// <summary>A short line under the title ("DNG + JPG · full size · f/2.0", "depth + 13 MP render").</summary>
    public string Detail { get; init; } = "";
    public JobState State { get; init; }
    /// <summary>0–1.</summary>
    public double Progress { get; init; }
    /// <summary>What it is doing now, in plain language ("Computing depth", "Writing JPG", "Waiting for memory").</summary>
    public string Status { get; init; } = "";
    /// <summary>Lower runs first (<see cref="ILibrarySettings.PriorityOf"/>).</summary>
    public int Priority { get; init; }
    /// <summary>Memory the scheduler reserves for the job while it runs (estimate).</summary>
    public long EstimatedMemoryBytes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    /// <summary>Why it failed (plain language).</summary>
    public string? Error { get; init; }
    /// <summary>The main file a finished job produced (first export file, …).</summary>
    public string? ResultPath { get; init; }
    /// <summary>Every file a finished export wrote.</summary>
    public IReadOnlyList<string> ResultFiles { get; init; } = Array.Empty<string>();
    /// <summary>Notes for the user (saved edits used, a format refused, …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Items done / total for batch jobs (thumbnails, scans); null otherwise.</summary>
    public int? ItemsDone { get; init; }
    public int? ItemsTotal { get; init; }
    /// <summary>The job this one retries, if any.</summary>
    public long? RetryOf { get; init; }
    /// <summary>Jobs queued together (an export or animation of a selection) share a batch: the Queue shows them as one group with a row per
    /// file. Null = a job of its own.</summary>
    public string? BatchId { get; init; }
    /// <summary>The batch's title ("Export of 12 photos · DNG + JPG · full size").</summary>
    public string? BatchTitle { get; init; }
    /// <summary>The file the job will write (its main file, before any renaming for an existing file); exports and animations.</summary>
    public string? PlannedFile { get; init; }
    /// <summary>What a job that is not about one capture works on, named after its photos: a camera import's "L16_00551 – L16_00564". Null for
    /// other jobs.</summary>
    public string? Subject { get; init; }
    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;
}

/// <summary>What to queue (<see cref="IJobQueue.Enqueue"/>). Most callers use the <see cref="ILibrary"/> methods, which build these.</summary>
public abstract record JobRequest
{
    public abstract JobKind Kind { get; }
    /// <summary>Priority override (lower runs first); null = the kind's (<see cref="ILibrarySettings.PriorityOf"/>).</summary>
    public int? Priority { get; init; }
    /// <summary>The batch this job belongs to (<see cref="JobInfo.BatchId"/>); set by <see cref="ILibrary.Export"/> / <see cref="ILibrary.Animate"/>
    /// for several captures.</summary>
    public string? BatchId { get; init; }
    public string? BatchTitle { get; init; }
}

/// <summary>Thumbnails of a batch of captures.</summary>
public sealed record ThumbnailJobRequest(IReadOnlyList<string> CaptureIds) : JobRequest { public override JobKind Kind => JobKind.Thumbnail; }

/// <summary>Analyze a capture and bring its cache to <paramref name="TargetLevel"/> (null = the folder's default level).</summary>
public sealed record AnalysisJobRequest(string CaptureId, CacheLevel? TargetLevel = null) : JobRequest { public override JobKind Kind => JobKind.Analysis; }

/// <summary>Set a capture's cache level (raise: <see cref="JobKind.CacheBuild"/> / <see cref="JobKind.CacheUpgrade"/> for full detail;
/// lower: <see cref="JobKind.CacheRelease"/>).</summary>
public sealed record CacheJobRequest(string CaptureId, CacheLevel Target) : JobRequest
{
    public override JobKind Kind => Release ? JobKind.CacheRelease : Target == CacheLevel.Full ? JobKind.CacheUpgrade : JobKind.CacheBuild;
    /// <summary>Lower the cache to <see cref="Target"/> (a release) instead of raising it.</summary>
    public bool Release { get; init; }
}

/// <summary>Export a capture.</summary>
public sealed record ExportJobRequest(string CaptureId, ExportOptions Options) : JobRequest { public override JobKind Kind => JobKind.Export; }

/// <summary>An animated export of a capture.</summary>
public sealed record AnimationJobRequest(string CaptureId, AnimationOptions Options) : JobRequest { public override JobKind Kind => JobKind.Animation; }

/// <summary>Import the Lumen session beside a capture (or <paramref name="SessionPath"/>).</summary>
public sealed record LumenImportJobRequest(string CaptureId, string? SessionPath = null) : JobRequest
{
    public override JobKind Kind => JobKind.LumenImport;
    /// <summary>Use Lumen's newer tone style for this capture (the GUI renderer's v2 tone gate, which a session does not record): kept in the
    /// imported version's settings, so exports (and `lux-light convert`) apply it.</summary>
    public bool NewerToneStyle { get; init; }
}

/// <summary>Scan an indexed folder.</summary>
public sealed record FolderScanJobRequest(long FolderId) : JobRequest { public override JobKind Kind => JobKind.FolderScan; }

/// <summary>Move the local context store to <paramref name="NewFolder"/> (<see cref="ILibrary.ChangeLocalContextFolder"/>).</summary>
public sealed record ContextStoreMoveJobRequest(string NewFolder) : JobRequest
{
    public override JobKind Kind => JobKind.ContextStoreMove;
    /// <summary>The folder it moves from (set when queued).</summary>
    public string? FromFolder { get; init; }
}

/// <summary>Camera import: copy <see cref="Files"/> from the camera to <paramref name="Destination"/> (run by the registered
/// <see cref="ICameraImporter"/>; queued by <see cref="ICameraService.StartImport"/>). A request without files fails at once.</summary>
public sealed record CameraImportJobRequest(string Destination) : JobRequest
{
    public override JobKind Kind => JobKind.Import;
    /// <summary>The captures to copy, in order.</summary>
    public IReadOnlyList<CameraImportFile> Files { get; init; } = Array.Empty<CameraImportFile>();
    /// <summary>One sub-folder per capture day (<c>yyyy-MM-dd</c>).</summary>
    public bool OneFolderPerDay { get; init; }
    public CameraAfterImport After { get; init; }
    /// <summary>The camera the files are on (its serial number); the job refuses to copy from another camera.</summary>
    public string? CameraSerial { get; init; }
    /// <summary>"Light L16", for the job's title.</summary>
    public string? CameraName { get; init; }
}

/// <summary>The queue as a whole.</summary>
/// <param name="ReservedMemoryBytes">Memory reserved by the running jobs' estimates.</param>
/// <param name="MemoryBudgetBytes">The budget (<see cref="ILibrarySettings.MemoryBudgetGb"/>).</param>
public sealed record JobQueueStatus(int Running, int Waiting, int Paused, int Failed, bool IsPaused, long ReservedMemoryBytes, long MemoryBudgetBytes);

/// <summary>A job changed (state, progress or status). Progress changes are throttled to about ten per second per job.</summary>
public sealed class JobChangedEventArgs : EventArgs
{
    public required JobInfo Job { get; init; }
    /// <summary>The job left the list (<see cref="IJobQueue.Remove"/> / <see cref="IJobQueue.ClearFinished"/>).</summary>
    public bool Removed { get; init; }
}

/// <summary>
/// The one scheduler of all background work: priorities (lower first; within a priority, oldest first — except on-screen thumbnails, newest
/// first), a concurrency limit (<see cref="ILibrarySettings.WorkerCount"/>) and a memory budget (<see cref="ILibrarySettings.MemoryBudgetGb"/>:
/// heavy jobs reserve their estimated peak and wait while it does not fit). Finished and failed entries are kept (in the library database)
/// until removed or cleared. Thread-safe.
/// </summary>
public interface IJobQueue
{
    /// <summary>Jobs, waiting / running first then finished (newest first).</summary>
    IReadOnlyList<JobInfo> GetJobs(bool includeFinished = true);
    JobInfo? Get(long jobId);
    /// <summary>Queue a job; returns its id. A request equal to one already waiting or running (same kind, capture and options) returns that
    /// job's id instead of queueing it twice.</summary>
    long Enqueue(JobRequest request);
    /// <summary>Cancel a waiting, paused or running job. A running job stops at its next checkpoint and keeps nothing half-made (the capture's
    /// cache and edits stay as they were before it; finished cache tiers stay). False when it had already finished.</summary>
    bool Cancel(long jobId);
    /// <summary>Cancel every unfinished job (of one kind); returns how many.</summary>
    int CancelAll(JobKind? kind = null);
    /// <summary>Queue a failed or canceled job again; returns the new job's id (null when the job cannot be retried).</summary>
    long? Retry(long jobId);
    /// <summary>Remove a finished job from the list (an unfinished one is canceled first).</summary>
    bool Remove(long jobId);
    /// <summary>Remove every finished job (done, failed, canceled); returns how many.</summary>
    int ClearFinished();
    /// <summary>Hold a waiting job (it is not started until resumed). A running job is not interrupted.</summary>
    bool Pause(long jobId);
    bool Resume(long jobId);
    /// <summary>Start no new job until <see cref="ResumeAll"/>. With <paramref name="interruptRunning"/>, running jobs are stopped at their next
    /// checkpoint and wait (they start again from the beginning — nothing half-made is kept; batch jobs continue where they were).</summary>
    void PauseAll(bool interruptRunning = false);
    void ResumeAll();
    bool IsPaused { get; }
    /// <summary>Change a waiting job's priority (lower runs first).</summary>
    bool SetPriority(long jobId, int priority);
    /// <summary>Completes when the job has finished (any final state).</summary>
    Task<JobInfo> WaitAsync(long jobId, CancellationToken cancel = default);
    JobQueueStatus Status { get; }
    /// <summary>A hint from the UI: the user is interacting (scrolling, editing, dragging a slider) — call with true when it starts and false
    /// when it ends; a true hint expires by itself after 30 s. With <see cref="ILibrarySettings.PauseHeavyJobsWhileInteracting"/> on, heavy
    /// jobs do not start meanwhile (status "Waiting while you work"). The default implementation ignores it.</summary>
    void SetInteractive(bool interacting) { }
    event EventHandler<JobChangedEventArgs>? JobChanged;
    event EventHandler<JobQueueStatus>? StatusChanged;
}
