// The camera part of the contract between the library core and the desktop app.
//
// Rules, as for Contracts.cs: UI-framework-free; events are raised on library threads (marshal before touching UI state); changes after
// the first version are additive.
//
// The camera is read-only. The service lists the camera's captures, reads their size and date, reads the camera's own preview JPEG of
// each capture for the thumbnails, and copies captures to a folder on the computer. Nothing on the camera is ever deleted, renamed,
// uploaded or changed, and there is no option to do so.

namespace Lux.Library;

/// <summary>Where the camera connection stands (<see cref="CameraStatus.State"/>).</summary>
public enum CameraState
{
    /// <summary>No camera support on this computer (no camera library installed, or a platform without a backend yet).</summary>
    Unavailable = 0,
    /// <summary>No camera on the bus; the service looks every few seconds.</summary>
    NotConnected = 1,
    /// <summary>A camera was found and is being opened.</summary>
    Connecting = 2,
    /// <summary>A camera is on the bus but another program holds it (often the desktop's file manager). Tried again every few seconds.</summary>
    Busy = 3,
    /// <summary>Open; its list of captures is being read (<see cref="CameraStatus.Phase"/>). Captures already read are available.</summary>
    Listing = 4,
    /// <summary>Open, and the list of captures is complete.</summary>
    Connected = 5,
    /// <summary>A camera is on the bus but could not be opened or read (<see cref="CameraStatus.Message"/>). Tried again every few seconds.</summary>
    Error = 6,
}

/// <summary>The two phases of reading a camera's captures.</summary>
public enum CameraListingPhase
{
    None = 0,
    /// <summary>The camera's list of file names (on the L16 one call of ~17 s for ~1100 files; no progress is reported by the camera).</summary>
    FileList = 1,
    /// <summary>Each capture's size and date (~8 ms each); captures appear as they are read.</summary>
    Details = 2,
}

/// <summary>A snapshot of the camera connection (<see cref="ICameraService.Status"/>).</summary>
public sealed record CameraStatus
{
    public CameraState State { get; init; }
    /// <summary>"Light L16" (from the camera's own device information).</summary>
    public string? DeviceName { get; init; }
    /// <summary>The camera's serial number.</summary>
    public string? Serial { get; init; }
    /// <summary>Battery level when the camera was opened.</summary>
    public int? BatteryPercent { get; init; }
    /// <summary>Plain-language detail for <see cref="CameraState.Busy"/>, <see cref="CameraState.Error"/> and <see cref="CameraState.Unavailable"/>.</summary>
    public string? Message { get; init; }
    /// <summary>The camera that is open is not a Light L16 (its captures, if any, are still listed).</summary>
    public bool NotLightL16 { get; init; }
    /// <summary>The captures shown are the list from an earlier connection, in this run of Lux or an earlier run. The service checks this list
    /// against the camera now (<see cref="CameraState.Listing"/>). Captures that are not on the camera leave the list. New captures are added.
    /// Changed captures are read again.</summary>
    public bool CheckingEarlierList { get; init; }
    /// <summary>The time (UTC) when the list shown was last complete and checked. Set while <see cref="CheckingEarlierList"/> is true.</summary>
    public DateTime? EarlierListUtc { get; init; }
    public CameraListingPhase Phase { get; init; }
    /// <summary>Captures whose size and date are read (<see cref="CameraListingPhase.Details"/>).</summary>
    public int Listed { get; init; }
    /// <summary>Captures found in the file list (0 until it is read).</summary>
    public int ListTotal { get; init; }
    /// <summary>When the present phase started (UTC).</summary>
    public DateTime? PhaseStartedUtc { get; init; }
    /// <summary>How long reading the file list is expected to take, seconds: the last time for this camera, else a typical figure.</summary>
    public double? ExpectedFileListSeconds { get; init; }
    /// <summary>Captures on the camera (read so far while listing).</summary>
    public int Captures { get; init; }
    /// <summary>Of those, how many are not in the library yet (no library location with the same file name and size).</summary>
    public int New { get; init; }
    /// <summary>An import from this camera is waiting or running (<see cref="ImportJobId"/>).</summary>
    public bool IsImporting { get; init; }
    public long? ImportJobId { get; init; }
    /// <summary>The camera is open (listing or listed).</summary>
    public bool IsOpen => State is CameraState.Listing or CameraState.Connected;
}

/// <summary>One capture on the camera (a <c>.lri</c>; calibration files are never listed).</summary>
public sealed record CameraCapture
{
    /// <summary>Unique on its camera: "serial|folder/file name".</summary>
    public required string Key { get; init; }
    public required string Folder { get; init; }
    /// <summary>"L16_00500.lri".</summary>
    public required string FileName { get; init; }
    /// <summary>"L16_00500".</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(FileName);
    public long Size { get; init; }
    /// <summary>The file's time on the camera (UTC); its local date is the capture day.</summary>
    public DateTimeOffset? Modified { get; init; }
    /// <summary>The capture day: the local date of <see cref="Modified"/>; null when the camera gives no date.</summary>
    public DateOnly? Day => Modified is { } m ? DateOnly.FromDateTime(m.ToLocalTime().DateTime) : null;
    /// <summary>The camera's own JPEG of the capture beside it ("L16_00500.jpg"), the thumbnail's source; null when there is none.</summary>
    public string? PreviewFile { get; init; }
    /// <summary>The library already holds a file with this name and size.</summary>
    public bool InLibrary { get; init; }
    /// <summary>That library capture's id (content hash), when <see cref="InLibrary"/>.</summary>
    public string? CaptureId { get; init; }
    /// <summary>The camera's serial number (part of <see cref="Key"/>).</summary>
    public string Camera { get; init; } = "";
    /// <summary>How the camera was held: the degrees clockwise that show the capture upright (0, 90, 180 or 270). The value comes from the
    /// accelerometer rule of the library (<c>Lux.Store.HeldTurn</c>). −1 means that the capture records no usable gravity. Null means that
    /// the value is not read yet. The service reads it with the thumbnail (two small reads of the first header, about 30 ms) and keeps it with
    /// the thumbnail. The page turns the thumbnail by this value when <see cref="ILibrarySettings.ShowPhotosAsHeld"/> is on.</summary>
    public int? HeldDegrees { get; init; }
}

/// <summary>Captures added, updated (size / date read, library state changed) or removed; <see cref="Reset"/> = read <see cref="ICameraService.Captures"/> again.</summary>
public sealed class CameraCapturesChangedEventArgs : EventArgs
{
    public IReadOnlyList<CameraCapture> Added { get; init; } = Array.Empty<CameraCapture>();
    public IReadOnlyList<CameraCapture> Updated { get; init; } = Array.Empty<CameraCapture>();
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();
    public bool Reset { get; init; }
}

/// <summary>What happens after the copy (the import panel's "After copying").</summary>
public enum CameraAfterImport
{
    /// <summary>Analyze and keep the 13 MP render (the default cache level).</summary>
    Analyse13Mp = 0,
    /// <summary>Analyze and keep full detail.</summary>
    AnalyseFullDetail = 1,
    /// <summary>Only copy (and add to the library).</summary>
    CopyOnly = 2,
}

/// <summary>Where and how to import.</summary>
/// <param name="Destination">The folder to copy to (created when missing).</param>
/// <param name="OneFolderPerDay">Copy each capture into a sub-folder named after its capture day (<c>yyyy-MM-dd</c>).</param>
public sealed record CameraImportOptions(string Destination, bool OneFolderPerDay, CameraAfterImport After);

/// <summary>What an import of a selection would do (<see cref="ICameraService.PlanImport"/>), for the panel and its button.</summary>
public sealed record CameraImportPlan
{
    public int Count { get; init; }
    /// <summary>Bytes of the selection.</summary>
    public long Bytes { get; init; }
    /// <summary>Of the selection, files already at the destination with the same size (skipped).</summary>
    public int AlreadyThere { get; init; }
    /// <summary>Bytes still to copy.</summary>
    public long BytesToCopy { get; init; }
    /// <summary>Free space on the destination's drive (null when unknown).</summary>
    public long? FreeBytes { get; init; }
    /// <summary>The folders the captures go to (one per capture day, or the destination).</summary>
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
    /// <summary>Why the import cannot start (plain language); null when it can.</summary>
    public string? Problem { get; init; }
    public bool CanImport => Problem is null && Count > 0;
}

/// <summary>How an import ended (<see cref="CameraImportSummary.State"/>).</summary>
public enum CameraImportState { Running = 0, Complete = 1, Stopped = 2, Cancelled = 3 }

/// <summary>The last import (<see cref="ICameraService.LastImport"/>; kept in the library's settings, so it survives a restart).</summary>
public sealed record CameraImportSummary
{
    public DateTime StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public long? JobId { get; init; }
    public CameraImportState State { get; init; }
    public int Requested { get; init; }
    public int Copied { get; init; }
    /// <summary>Already at the destination with the same size.</summary>
    public int Skipped { get; init; }
    /// <summary>Why it stopped (plain language).</summary>
    public string? Problem { get; init; }
    public CameraAfterImport After { get; init; }
    public string Destination { get; init; } = "";
    /// <summary>The files at the destination (copied or already there).</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    /// <summary>The library captures they became (filled once the folder scan found them).</summary>
    public IReadOnlyList<string> CaptureIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The camera, for the desktop app's Camera page and status bar. Detects the camera (polling the bus every few seconds while nothing is
/// being copied), lists its captures in the background with progress and keeps the list for the session per camera, reads the camera's
/// preview JPEGs lazily for thumbnails (kept in the app's data folder), marks the captures already in the library, and imports a selection
/// as one Queue job. Read-only towards the camera. Thread-safe; events on library threads.
/// </summary>
public interface ICameraService : IDisposable
{
    CameraStatus Status { get; }
    event EventHandler? StatusChanged;

    /// <summary>The captures of the camera that are read so far, newest first by name. The service keeps the list of each camera after it is
    /// unplugged: for the session, and across restarts when the service has a listing folder. When the same camera returns, the list shows
    /// at once. <see cref="CameraStatus.CheckingEarlierList"/> is true while the service checks the list against the camera.</summary>
    IReadOnlyList<CameraCapture> Captures { get; }
    event EventHandler<CameraCapturesChangedEventArgs>? CapturesChanged;

    /// <summary>The capture's thumbnail (a JPEG made from the camera's own preview, about 480 px wide), from the app's data folder when it was
    /// read before, else read from the camera (thumbnails go before the listing; newest request first). Cancel when the item leaves the
    /// screen. Null when the camera has no preview for it or is not connected.</summary>
    Task<byte[]?> GetPreviewAsync(CameraCapture capture, CancellationToken cancel = default);

    /// <summary>What importing these captures would do: sizes, files already there, the folders, free space, and a problem that prevents it
    /// (no folder, a folder on the camera itself, not enough space, no camera). Cheap (file checks only).</summary>
    CameraImportPlan PlanImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options);

    /// <summary>Queue an import of these captures (one Queue job; returns its id). Throws <see cref="LibraryException"/> with the plan's problem.
    /// The job copies each file to <c>.part</c> and renames it when complete, skips files already there with the same size, keeps the
    /// finished files when it stops (canceled, camera unplugged), then adds the destination to the library (or rescans it) and queues the
    /// analysis chosen.</summary>
    long StartImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options);

    /// <summary>The last import (null when there was none).</summary>
    CameraImportSummary? LastImport { get; }
    event EventHandler? LastImportChanged;
    /// <summary>Of the last import's captures, how many are analyzed to the level chosen (null when it only copied or found none yet).</summary>
    (int Analysed, int Total)? LastImportAnalysis();

    /// <summary>Try again now: open a busy / failed camera, or read the list of an open camera again.</summary>
    void Reconnect();

    /// <summary>The suggested destination (<c>Pictures/Light</c> in the user's home).</summary>
    string DefaultDestination { get; }
}

/// <summary>Progress of a job run by code outside the library (the camera import).</summary>
public interface IJobReporter
{
    /// <summary>Canceled when the user cancels the job (or the library closes).</summary>
    CancellationToken Cancel { get; }
    long JobId { get; }
    void Report(double progress, string? status = null);
    void Items(int done, int total);
    void Note(string note);
    void Result(string? path, IEnumerable<string>? files = null);
}

/// <summary>Runs camera-import jobs (<see cref="CameraImportJobRequest"/>) for the library's queue: the camera service registers itself with
/// <see cref="ILibrary.SetCameraImporter"/>. Throw <see cref="LibraryException"/> for a failure the user should read; an
/// <see cref="OperationCanceledException"/> after <see cref="IJobReporter.Cancel"/> ends the job as canceled.</summary>
public interface ICameraImporter
{
    Task RunAsync(CameraImportJobRequest request, IJobReporter job);
}

/// <summary>One file of a camera import (<see cref="CameraImportJobRequest.Files"/>).</summary>
/// <param name="Day">The capture day (<c>yyyy-MM-dd</c>, local), for one folder per day; null when the camera gave no date.</param>
public sealed record CameraImportFile(string Folder, string Name, long Size, string? Day)
{
    /// <summary>The file's time on the camera: the copy gets it as its modification time.</summary>
    public DateTimeOffset? Modified { get; init; }
}

/// <summary>The plain-language title, detail line and subject of a camera-import job (shared by the library and any other queue that runs
/// one, so every Queue shows the same words).</summary>
public static class CameraImportText
{
    /// <summary>"Importing 14 photos from the camera" / "Importing L16_00551 from the camera".</summary>
    public static string Title(CameraImportJobRequest i) => i.Files.Count == 1
        ? $"Importing {System.IO.Path.GetFileNameWithoutExtension(i.Files[0].Name)} from the camera"
        : i.Files.Count == 0 ? "Importing from the camera" : $"Importing {i.Files.Count} photos from the camera";

    /// <summary>"14 photos · 2.3 GB → /home/…/Light · a folder per day · then analysis (13 MP)".</summary>
    public static string Detail(CameraImportJobRequest i)
    {
        if (i.Files.Count == 0) return "no photos chosen";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        long bytes = i.Files.Sum(f => Math.Max(0, f.Size));
        string size = bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", inv) + " GB" : (bytes / 1e6).ToString("0", inv) + " MB";
        var parts = new List<string> { i.Files.Count == 1 ? "1 photo" : $"{i.Files.Count} photos", size, "→ " + i.Destination };
        if (i.OneFolderPerDay) parts.Add("a folder per day");
        parts.Add(i.After switch { CameraAfterImport.AnalyseFullDetail => "then analysis (full detail)", CameraAfterImport.CopyOnly => "copy only", _ => "then analysis (13 MP)" });
        return string.Join(" · ", parts);
    }

    /// <summary>The first and last photo ("L16_00551 – L16_00564"); null for a request without files.</summary>
    public static string? Subject(CameraImportJobRequest i)
    {
        if (i.Files.Count == 0) return null;
        var names = i.Files.Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 1 ? names[0] : $"{names[0]} – {names[^1]}";
    }
}
