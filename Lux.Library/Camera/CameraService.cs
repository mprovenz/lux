using System.Globalization;
using System.Text.Json;
using Lux.Engine.Mtp;
using Lux.Engine.Session;

namespace Lux.Library.Camera;

/// <summary>Options of the <see cref="CameraService"/>.</summary>
public sealed record CameraServiceOptions
{
    /// <summary>Where thumbnails made from the camera's previews are kept (one folder per camera); null = not kept between requests. The held
    /// turn that the service reads with each thumbnail is kept beside it (<c>&lt;name&gt;_&lt;size&gt;.held</c>).</summary>
    public string? PreviewFolder { get; init; }
    /// <summary>Where the list of captures of each camera is kept between runs (one <c>&lt;serial&gt;.json</c> per camera). Null keeps the list
    /// for the session only.</summary>
    public string? ListingFolder { get; init; }
    /// <summary>Reads how the camera was held with each thumbnail (<see cref="CameraCapture.HeldDegrees"/>). This is two small reads of the
    /// first header of the capture, about 30 ms on the L16. These reads wait for the listing work.</summary>
    public bool ReadHeldTurn { get; init; } = true;
    /// <summary>Makes a thumbnail from the camera's preview JPEG (the app decodes it, scales it to about 480 px wide, turns it upright and
    /// encodes a small JPEG); null = the camera's JPEG is kept as it is. Runs off the camera's thread; return null for an unreadable one.</summary>
    public Func<byte[], byte[]?>? ShrinkPreview { get; init; }
    /// <summary>How often the bus is looked at for a camera plugged in or out (not while files are being copied).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>After a camera failed to open or read for a reason other than being busy, wait this long before trying again by itself.</summary>
    public TimeSpan ErrorRetry { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Start polling at once (tests may set false and call <see cref="CameraService.Poll"/>).</summary>
    public bool StartPolling { get; init; } = true;
    /// <summary>Folders never listed: the L16's calibration files (<c>lightcal</c>, whose <c>.lri</c> files are not captures) and the camera
    /// app's own caches. Folders starting with a dot are never listed either.</summary>
    public IReadOnlyCollection<string> SkipFolders { get; init; } = new[] { "lightcal", "thumbnails", "presets" };
    /// <summary>The suggested destination; null = <c>Pictures/Light</c> in the user's home.</summary>
    public string? DefaultDestination { get; init; }
    /// <summary>Captures read per step of the details phase (thumbnails can go between steps).</summary>
    public int DetailsChunk { get; init; } = 12;
    /// <summary>Diagnostic log (any thread).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The camera for the desktop app (<see cref="ICameraService"/>) and the runner of camera-import jobs (<see cref="ICameraImporter"/>). Every
/// call to the camera goes through one <see cref="DeviceWorker"/> thread. Read-only towards the camera: it lists folders and files, reads file
/// information, previews and files, and never deletes, renames, uploads or changes anything there.
/// </summary>
public sealed class CameraService : ICameraService, ICameraImporter
{
    // priorities of the device thread: copies and opening first, then thumbnails (newest request first), then the listing, then the held
    // turns of thumbnails, then polling. A held turn never goes before a listing step.
    const int PrioCopy = 0, PrioPreview = 1, PrioList = 2, PrioHeld = 3, PrioPoll = 4;
    /// <summary>A typical time for the L16's file list (16.9–18 s for 1087 files).</summary>
    const double TypicalFileListSeconds = 20;

    sealed class Listing
    {
        public required string Serial;
        public readonly List<CameraCapture> Items = new();
        public double? FileListSeconds;
        public bool Complete;
        /// <summary>The items are from an earlier connection. The service checks them against the camera now.</summary>
        public bool Checking;
        /// <summary>The time (UTC) when the list was last complete and checked.</summary>
        public DateTime? CheckedUtc;
        /// <summary>The list changed after the last write to the listing folder.</summary>
        public bool Dirty;
    }

    readonly ILibrary _lib;
    readonly IMtpBackend _backend;
    readonly CameraServiceOptions _o;
    readonly DeviceWorker _worker;
    readonly Timer? _poll;
    readonly Timer _libraryTimer;
    readonly object _gate = new();
    readonly Dictionary<string, Listing> _listings = new(StringComparer.Ordinal);
    readonly HashSet<long> _importJobs = new();
    readonly bool _available;

    // the open camera: touched on the device thread only
    IMtpDevice? _dev;
    MtpCandidate? _cand;

    // state (under _gate)
    CameraState _state;
    string? _message;
    MtpDeviceInfo? _info;
    Listing? _current;
    CameraListingPhase _phase;
    int _listed, _listTotal;
    DateTime? _phaseStarted;
    int _generation;
    DateTime _retryAfter = DateTime.MinValue;
    CameraImportSummary? _last;
    volatile bool _transferring, _pollQueued, _disposed;

    public event EventHandler? StatusChanged;
    public event EventHandler<CameraCapturesChangedEventArgs>? CapturesChanged;
    public event EventHandler? LastImportChanged;

    public CameraService(ILibrary library, IMtpBackend backend, CameraServiceOptions? options = null)
    {
        _lib = library;
        _backend = backend;
        _o = options ?? new CameraServiceOptions();
        _worker = new DeviceWorker("lux camera");
        _last = LoadLast();
        bool available;
        try { available = backend.IsAvailable; } catch (Exception) { available = false; }
        _available = available;
        _state = available ? CameraState.NotConnected : CameraState.Unavailable;
        if (!available)
            _message = OperatingSystem.IsWindows()
                ? "Importing from the camera is not available on Windows yet. Copy the captures to a folder and add the folder in the Library."
                : "Lux needs the libgphoto2 library to talk to the camera, and it was not found on this computer. Install it (on Ubuntu and Debian: the libgphoto2-6 package) and start Lux again.";
        _libraryTimer = new Timer(_ => RefreshInLibrary(), null, Timeout.Infinite, Timeout.Infinite);
        _lib.CapturesChanged += OnLibraryChanged;
        _lib.Jobs.JobChanged += OnJobChanged;
        _lib.SetCameraImporter(this);
        if (available && _o.StartPolling) _poll = new Timer(_ => Poll(), null, TimeSpan.Zero, _o.PollInterval);
    }

    void Log(string s) => _o.Log?.Invoke("camera: " + s);

    public string DefaultDestination => _o.DefaultDestination
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pics ? pics
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"), "Light");

    // ================================================================================================================ status

    public CameraStatus Status
    {
        get
        {
            lock (_gate)
            {
                var items = IsOpenLocked ? _current?.Items : null;
                return new CameraStatus
                {
                    State = _state, Message = _message, DeviceName = _info?.DisplayName, Serial = _info is { Serial.Length: > 0 } i ? i.Serial : null,
                    BatteryPercent = _info?.BatteryPercent, NotLightL16 = _info is { } inf && !inf.IsLightL16,
                    Phase = _phase, Listed = _listed, ListTotal = _listTotal, PhaseStartedUtc = _phaseStarted,
                    ExpectedFileListSeconds = _current?.FileListSeconds ?? TypicalFileListSeconds,
                    CheckingEarlierList = _state == CameraState.Listing && _current is { Checking: true },
                    EarlierListUtc = _state == CameraState.Listing && _current is { Checking: true } ? _current.CheckedUtc : null,
                    Captures = items?.Count ?? 0, New = items?.Count(c => !c.InLibrary) ?? 0,
                    IsImporting = _importJobs.Count > 0, ImportJobId = _importJobs.Count > 0 ? _importJobs.Max() : null,
                };
            }
        }
    }

    bool IsOpenLocked => _state is CameraState.Listing or CameraState.Connected;

    public IReadOnlyList<CameraCapture> Captures
    {
        get { lock (_gate) return IsOpenLocked && _current is { } l ? l.Items.ToList() : Array.Empty<CameraCapture>(); }
    }

    void RaiseStatus() { try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch (Exception e) { Log("status subscriber: " + e.Message); } }

    void RaiseCaptures(CameraCapturesChangedEventArgs e) { try { CapturesChanged?.Invoke(this, e); } catch (Exception x) { Log("captures subscriber: " + x.Message); } }

    void SetState(CameraState state, string? message = null, CameraListingPhase phase = CameraListingPhase.None)
    {
        lock (_gate)
        {
            if (_state == state && _message == message && _phase == phase) return;
            _state = state; _message = message;
            if (_phase != phase) { _phase = phase; _phaseStarted = phase == CameraListingPhase.None ? null : DateTime.UtcNow; }
        }
        RaiseStatus();
    }

    // ================================================================================================================ detection

    /// <summary>Look at the bus once (the timer calls this every <see cref="CameraServiceOptions.PollInterval"/>): open a camera that appeared,
    /// notice one that went away. Skipped while files are being copied.</summary>
    public void Poll()
    {
        if (_disposed || !_available || _transferring || _pollQueued) return;
        _pollQueued = true;
        _ = _worker.Run(PrioPoll, _ => { _pollQueued = false; PollOnWorker(force: false); });
    }

    public void Reconnect()
    {
        if (_disposed || !_available) return;
        _ = _worker.Run(PrioCopy, _ =>
        {
            if (_dev is not null) { StartListing(relist: true); return; }
            lock (_gate) _retryAfter = DateTime.MinValue;
            PollOnWorker(force: true);
        });
    }

    void PollOnWorker(bool force)
    {
        if (_disposed || (_transferring && !force)) return;
        IReadOnlyList<MtpCandidate> found;
        try { found = _backend.Autodetect(); }
        catch (Exception e) { Log("autodetect: " + e.Message); found = Array.Empty<MtpCandidate>(); }
        if (_dev is not null)
        {
            if (!found.Any(c => c.Port == _cand!.Port)) CloseDevice("the camera was unplugged");
            return;
        }
        if (found.Count == 0)
        {
            SetState(CameraState.NotConnected);
            return;
        }
        CameraState st; DateTime retry;
        lock (_gate) { st = _state; retry = _retryAfter; }
        if (st == CameraState.Error && !force && DateTime.UtcNow < retry) return;
        Connect(found);
    }

    void Connect(IReadOnlyList<MtpCandidate> found)
    {
        SetState(CameraState.Connecting);
        MtpException? busy = null; Exception? other = null;
        foreach (var c in found)
        {
            IMtpDevice dev;
            try { dev = _backend.Open(c); }
            catch (MtpException e) when (e.Kind == MtpErrorKind.Busy) { busy = e; continue; }
            catch (Exception e) when (e is MtpException or IOException or InvalidOperationException) { other = e; continue; }
            MtpDeviceInfo info;
            try { info = dev.Info; } catch (Exception e) when (e is MtpException or IOException) { info = new MtpDeviceInfo("", dev.Name, "", "", null); }
            _dev = dev; _cand = c;
            string serial = info.Serial.Length > 0 ? info.Serial : info.Model + "@" + c.Model;
            bool known; lock (_gate) known = _listings.ContainsKey(serial);
            // the list from an earlier run of Lux: it shows at once, then the service checks it against the camera
            var kept = known ? null : LoadListing(serial);
            if (kept is not null) { var marked = MarkInLibrary(kept.Items.ToList()); kept.Items.Clear(); kept.Items.AddRange(marked); }
            lock (_gate)
            {
                _info = info;
                _generation++;
                if (!_listings.TryGetValue(serial, out var l)) _listings[serial] = l = kept ?? new Listing { Serial = serial };
                _current = l;
                _listed = l.Items.Count; _listTotal = 0;
            }
            Log($"opened {info.DisplayName} {info.Serial} at {c.Port}");
            StartListing(relist: false);
            return;
        }
        if (busy is not null)
            SetState(CameraState.Busy, "Another program is using the camera, often the desktop's file manager, which opens cameras by itself. Close it there "
                                       + "or unmount the camera, and Lux connects by itself.");
        else
        {
            lock (_gate) _retryAfter = DateTime.UtcNow + _o.ErrorRetry;
            Log("could not open: " + (other?.Message ?? "no answer"));
            SetState(CameraState.Error, "The camera did not answer when Lux opened it. Lux tries again in a moment; switching the camera off "
                                        + "and on, or plugging it in again, often helps.");
        }
    }

    /// <summary>Close the open camera (device thread): the listing is kept for this session, the state goes back to "not connected".</summary>
    void CloseDevice(string why)
    {
        Log("closing: " + why);
        try { _dev?.Dispose(); } catch (Exception e) { Log("close: " + e.Message); }
        _dev = null; _cand = null;
        SaveListing(onlyIfDirty: true);
        lock (_gate) { _generation++; _info = null; _listed = 0; _listTotal = 0; }
        SetState(CameraState.NotConnected);
        RaiseCaptures(new CameraCapturesChangedEventArgs { Reset = true });
    }

    /// <summary>A camera call failed: when the camera is no longer on the bus it is closed (unplugged); otherwise the error is shown.</summary>
    void OnDeviceFailure(Exception e, string doing)
    {
        Log($"{doing}: {e.Message}");
        if (_dev is null) return;
        bool present;
        try { present = _backend.Autodetect().Any(c => c.Port == _cand?.Port); } catch (Exception) { present = false; }
        if (!present || e is MtpException { Kind: MtpErrorKind.Disconnected })
        {
            // a disconnect on a camera still listed: the session is unusable (libgphoto2 does not recover it); open it again on the next poll
            CloseDevice(present ? "the camera stopped answering" : "the camera was unplugged");
            return;
        }
        try { _dev.Dispose(); } catch (Exception) { }
        _dev = null; _cand = null;
        lock (_gate) { _generation++; _retryAfter = DateTime.UtcNow + _o.ErrorRetry; }
        SetState(CameraState.Error, $"The camera stopped answering while Lux was {doing}. Lux tries again in a moment; switching the camera off and on, or "
                                    + "plugging it in again, often helps.");
        RaiseCaptures(new CameraCapturesChangedEventArgs { Reset = true });
    }

    // ================================================================================================================ listing

    void StartListing(bool relist)
    {
        // a new generation: the steps of a listing that runs now stop (Reconnect while listing). A list from before shows, marked as being
        // checked.
        int gen; lock (_gate) { gen = ++_generation; _listTotal = 0; if (_current is { } cl) cl.Checking = cl.Items.Count > 0; }
        SetState(CameraState.Listing, null, CameraListingPhase.FileList);
        RaiseCaptures(new CameraCapturesChangedEventArgs { Reset = true });   // the cached list of this camera, shown at once
        _ = _worker.Run(PrioList, _ => ReadFileList(gen));
    }

    /// <summary>Phase 1: the folder tree and the file names (one call per folder; the L16's DCIM/Camera takes ~17 s).</summary>
    void ReadFileList(int gen)
    {
        if (!Current(gen) || _dev is not { } dev) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<(string Folder, string Lri, string? Jpg)>();
        try
        {
            var stack = new Stack<string>();
            stack.Push("/");
            while (stack.Count > 0)
            {
                string folder = stack.Pop();
                var subs = dev.ListFolders(folder);
                var files = dev.ListFiles(folder);
                var jpgs = files.Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
                foreach (var f in files.Where(f => f.EndsWith(".lri", StringComparison.OrdinalIgnoreCase)))
                    found.Add((folder, f, jpgs.GetValueOrDefault(Path.GetFileNameWithoutExtension(f))));
                foreach (var s in subs)
                {
                    if (s.StartsWith('.') || _o.SkipFolders.Contains(s, StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push(folder.TrimEnd('/') + "/" + s);
                }
            }
        }
        catch (Exception e) when (e is MtpException or IOException or NotSupportedException)
        {
            if (Current(gen)) OnDeviceFailure(e, "reading its list of files");
            return;
        }
        if (!Current(gen)) return;
        double secs = sw.Elapsed.TotalSeconds;
        Log($"file list: {found.Count} captures in {secs:0.0} s");
        // merge with the known list of this camera. Unknown files need their details. Files that are gone leave the list. The known files are
        // read again after the unknown ones, because a replaced file has another size or date. The known files stay on screen meanwhile.
        var removed = new List<string>();
        List<(string Folder, string Lri, string? Jpg)> need;
        string serial;
        lock (_gate)
        {
            var l = _current!;
            serial = l.Serial;
            l.FileListSeconds = secs;
            var keys = found.Select(f => KeyOf(serial, f.Folder, f.Lri)).ToHashSet(StringComparer.Ordinal);
            removed.AddRange(l.Items.Where(i => !keys.Contains(i.Key)).Select(i => i.Key));
            if (l.Items.RemoveAll(i => !keys.Contains(i.Key)) > 0) l.Dirty = true;
            var known = l.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
            bool IsKnown((string Folder, string Lri, string? Jpg) f) => known.TryGetValue(KeyOf(serial, f.Folder, f.Lri), out var k) && k.PreviewFile == f.Jpg;
            need = found.Where(f => !IsKnown(f)).OrderByDescending(f => f.Lri, StringComparer.OrdinalIgnoreCase)
                        .Concat(found.Where(IsKnown).OrderByDescending(f => f.Lri, StringComparer.OrdinalIgnoreCase)).ToList();
            _listTotal = found.Count;
            _listed = found.Count - need.Count;
        }
        if (removed.Count > 0) RaiseCaptures(new CameraCapturesChangedEventArgs { Removed = removed });
        SetState(CameraState.Listing, null, CameraListingPhase.Details);
        if (need.Count == 0) { FinishListing(gen); return; }
        foreach (var chunk in need.Chunk(Math.Max(1, _o.DetailsChunk)))
        {
            var part = chunk;
            _ = _worker.Run(PrioList, _ => ReadDetails(gen, serial, part));
        }
    }

    static string KeyOf(string serial, string folder, string name) => serial + "|" + folder.TrimEnd('/') + "/" + name;

    /// <summary>Phase 2: size and date of a few captures (~8 ms each), then they appear.</summary>
    void ReadDetails(int gen, string serial, IReadOnlyList<(string Folder, string Lri, string? Jpg)> part)
    {
        if (!Current(gen) || _dev is not { } dev) return;
        var read = new List<CameraCapture>();
        try
        {
            foreach (var (folder, lri, jpg) in part)
            {
                var it = dev.GetItem(folder, lri);
                read.Add(new CameraCapture { Key = KeyOf(serial, folder, lri), Folder = folder, FileName = lri, Size = it.Size, Modified = it.Modified, PreviewFile = jpg, Camera = serial });
            }
        }
        catch (Exception e) when (e is MtpException or IOException or NotSupportedException)
        {
            if (Current(gen)) OnDeviceFailure(e, "reading photo details");
            return;
        }
        // a known capture with the same size and date stays as it is, with its library mark and held turn. The others get a new mark.
        Dictionary<string, CameraCapture> before;
        lock (_gate) before = _current is { } cur ? cur.Items.Where(i => read.Any(r => r.Key == i.Key)).ToDictionary(i => i.Key, StringComparer.Ordinal) : new();
        static bool Same(CameraCapture a, CameraCapture b) => a.Size == b.Size && a.Modified == b.Modified && a.PreviewFile == b.PreviewFile;
        int count = read.Count;
        var unchanged = read.Where(c => before.TryGetValue(c.Key, out var b) && Same(b, c)).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        read = MarkInLibrary(read.Where(c => !unchanged.Contains(c.Key)).ToList());
        var added = new List<CameraCapture>(); var updated = new List<CameraCapture>();
        bool last;
        lock (_gate)
        {
            if (gen != _generation) return;
            var l = _current!;
            foreach (var c in read)
            {
                int i = l.Items.FindIndex(x => x.Key == c.Key);
                if (i >= 0) { l.Items[i] = c; updated.Add(c); } else { l.Items.Add(c); added.Add(c); }
            }
            if (read.Count > 0) l.Dirty = true;
            l.Items.Sort((a, b) => string.Compare(b.FileName, a.FileName, StringComparison.OrdinalIgnoreCase));
            _listed = Math.Min(_listTotal, _listed + count);
            last = _listed >= _listTotal;
        }
        if (added.Count + updated.Count > 0) RaiseCaptures(new CameraCapturesChangedEventArgs { Added = added, Updated = updated });
        RaiseStatus();
        if (last) FinishListing(gen);
    }

    void FinishListing(int gen)
    {
        lock (_gate)
        {
            if (gen != _generation) return;
            var l = _current!;
            l.Complete = true; l.Checking = false; l.CheckedUtc = DateTime.UtcNow; l.Dirty = true;
        }
        SetState(CameraState.Connected);
        SaveListing(onlyIfDirty: true);
    }

    // ================================================================================================================ the kept listing

    sealed record KeptItem(string F, string N, long S, long? M, string? P, int? H);
    sealed record KeptListing(int Version, string Serial, DateTime? CheckedUtc, double? FileListSeconds, List<KeptItem> Items);

    string? ListingPath(string serial)
        => _o.ListingFolder is { Length: > 0 } root ? Path.Combine(root, SafeName(serial) + ".json") : null;

    static string SafeName(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    /// <summary>Loads the list of a camera from an earlier run. Returns null when there is no list or it cannot be read.</summary>
    Listing? LoadListing(string serial)
    {
        var path = ListingPath(serial);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var k = JsonSerializer.Deserialize<KeptListing>(File.ReadAllText(path));
            if (k is null || k.Version != 1 || k.Serial != serial) return null;
            var l = new Listing { Serial = serial, FileListSeconds = k.FileListSeconds, CheckedUtc = k.CheckedUtc };
            foreach (var i in k.Items)
                l.Items.Add(new CameraCapture
                {
                    Key = KeyOf(serial, i.F, i.N), Folder = i.F, FileName = i.N, Size = i.S, Modified = i.M is long m ? DateTimeOffset.FromUnixTimeSeconds(m) : null,
                    PreviewFile = i.P, HeldDegrees = i.H, Camera = serial,
                });
            l.Items.Sort((a, b) => string.Compare(b.FileName, a.FileName, StringComparison.OrdinalIgnoreCase));
            Log($"kept list of {serial}: {l.Items.Count} captures, checked {k.CheckedUtc:u}");
            return l;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log("kept list: " + e.Message);
            return null;
        }
    }

    /// <summary>Writes the list of the present camera to the listing folder (an atomic replace). The service keeps only a list that was complete
    /// at least once.</summary>
    void SaveListing(bool onlyIfDirty)
    {
        KeptListing k; string? path;
        lock (_gate)
        {
            var l = _current;
            if (l is null || l.CheckedUtc is null || (onlyIfDirty && !l.Dirty) || (path = ListingPath(l.Serial)) is null) return;
            k = new KeptListing(1, l.Serial, l.CheckedUtc, l.FileListSeconds,
                                l.Items.Select(i => new KeptItem(i.Folder, i.FileName, i.Size, i.Modified?.ToUnixTimeSeconds(), i.PreviewFile, i.HeldDegrees)).ToList());
            l.Dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(tmp, JsonSerializer.Serialize(k));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("keeping the list: " + e.Message); }
    }

    bool Current(int gen) { lock (_gate) return gen == _generation && !_disposed; }

    // ================================================================================================================ library marking

    List<CameraCapture> MarkInLibrary(List<CameraCapture> items)
    {
        if (items.Count == 0) return items;
        IReadOnlyDictionary<string, string> hits;
        try { hits = _lib.FindByNameAndSize(items.Select(c => (c.FileName, c.Size)).ToList()); }
        catch (Exception e) when (e is not OutOfMemoryException) { Log("library lookup: " + e.Message); return items; }
        return items.Select(c =>
        {
            var id = hits.GetValueOrDefault(ILibrary.NameSizeKey(c.FileName, c.Size));
            return c with { InLibrary = id is not null, CaptureId = id };
        }).ToList();
    }

    void OnLibraryChanged(object? sender, CapturesChangedEventArgs e)
    {
        if (e.Reset || e.Added.Count > 0 || e.Removed.Count > 0) { try { _libraryTimer.Change(700, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    }

    /// <summary>Mark again which captures the library holds (after a scan or an import); raises the ones that changed.</summary>
    public void RefreshInLibrary()
    {
        if (_disposed) return;
        List<CameraCapture> items;
        lock (_gate) { if (_current is null) return; items = _current.Items.ToList(); }
        var marked = MarkInLibrary(items);
        var changed = new List<CameraCapture>();
        lock (_gate)
        {
            if (_current is null) return;
            foreach (var c in marked)
            {
                int i = _current.Items.FindIndex(x => x.Key == c.Key);
                if (i >= 0 && (_current.Items[i].InLibrary != c.InLibrary || _current.Items[i].CaptureId != c.CaptureId)) { _current.Items[i] = c; changed.Add(c); }
            }
        }
        if (changed.Count == 0) return;
        RaiseCaptures(new CameraCapturesChangedEventArgs { Updated = changed });
        RaiseStatus();
    }

    // ================================================================================================================ previews

    string? PreviewPath(CameraCapture c)
    {
        if (_o.PreviewFolder is not { Length: > 0 } root || c.PreviewFile is null) return null;
        string camera = string.Concat(c.Camera.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(root, camera, $"{c.Name}_{c.Size.ToString(CultureInfo.InvariantCulture)}.jpg");
    }

    public async Task<byte[]?> GetPreviewAsync(CameraCapture capture, CancellationToken cancel = default)
    {
        if (capture.PreviewFile is null) return null;
        var path = PreviewPath(capture);
        if (path is not null && File.Exists(path))
        {
            try { var kept = await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false); EnsureHeld(capture); return kept; }
            catch (IOException) { }
        }
        string? serial; lock (_gate) serial = IsOpenLocked ? _current?.Serial : null;
        if (serial is null || serial != capture.Camera) return null;
        byte[]? raw;
        try
        {
            raw = await _worker.Run(PrioPreview, t =>
            {
                if (_dev is not { } dev) return null;
                lock (_gate) if (_current?.Serial != capture.Camera) return null;
                using var ms = new MemoryStream();
                try { dev.Read(new MtpItem(capture.Folder, capture.PreviewFile, 0, null), ms, null, t); }
                catch (MtpException e) when (e.Kind == MtpErrorKind.NotFound) { return null; }
                catch (Exception e) when (e is MtpException or IOException) { OnDeviceFailure(e, "reading a preview"); return null; }
                return ms.ToArray();
            }, cancel, newestFirst: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
        if (raw is null || raw.Length == 0) return null;
        byte[]? thumb;
        try { thumb = _o.ShrinkPreview is { } shrink ? await Task.Run(() => shrink(raw), CancellationToken.None).ConfigureAwait(false) : raw; }
        catch (Exception e) when (e is not OutOfMemoryException) { Log("preview: " + e.Message); thumb = null; }
        if (thumb is null) return null;
        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
                await File.WriteAllBytesAsync(tmp, thumb, CancellationToken.None).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("preview cache: " + e.Message); }
        }
        EnsureHeld(capture);
        return thumb;
    }

    // ================================================================================================================ the held turn

    readonly HashSet<string> _heldQueued = new(StringComparer.Ordinal);

    /// <summary>The file beside the thumbnail that keeps the held turn.</summary>
    string? HeldPath(CameraCapture c) => PreviewPath(c) is { } p ? Path.ChangeExtension(p, ".held") : null;

    /// <summary>Makes sure that the held turn of the capture is known, because the page turns the thumbnail by it. The value comes from the file
    /// beside the thumbnail. If that file is not there, the service reads the first header of the capture on the camera (two small reads,
    /// about 30 ms on the L16). This read has a lower priority than the listing, so it never delays the list. The result is kept beside the
    /// thumbnail and in the list of the camera.</summary>
    void EnsureHeld(CameraCapture capture)
    {
        if (!_o.ReadHeldTurn || _disposed) return;
        CameraCapture now;
        lock (_gate)
        {
            if (!_listings.TryGetValue(capture.Camera, out var l)) return;
            var cur = l.Items.FirstOrDefault(i => i.Key == capture.Key);
            if (cur is null || cur.HeldDegrees is not null || cur.Size != capture.Size) return;
            now = cur;
        }
        var heldPath = HeldPath(now);
        if (heldPath is not null && File.Exists(heldPath))
        {
            try
            {
                if (int.TryParse(File.ReadAllText(heldPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kept) && kept is -1 or 0 or 90 or 180 or 270)
                {
                    SetHeld(now, kept);
                    return;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (_gate)
        {
            if (!IsOpenLocked || _current?.Serial != now.Camera || !_heldQueued.Add(now.Key)) return;
        }
        _ = _worker.Run(PrioHeld, _ =>
        {
            try
            {
                if (_dev is not { } dev) return;
                lock (_gate) if (_current?.Serial != now.Camera) return;
                int? deg = ReadHeldOnDevice(dev, now);
                if (deg is not int d) return;
                if (heldPath is not null)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(heldPath)!);
                        File.WriteAllText(heldPath, d.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("held turn file: " + e.Message); }
                }
                SetHeld(now, d);
            }
            finally { lock (_gate) _heldQueued.Remove(now.Key); }
        }, CancellationToken.None, newestFirst: true);
    }

    /// <summary>Reads the held turn of one capture on the device thread. It reads the header of the first block (32 bytes at offset 0, then the
    /// message at its offset) and applies the accelerometer rule of the library. −1 means no usable gravity, or a file that is not a capture.
    /// Null means that the camera cannot be read now. The service does not keep a null; it tries again with the next thumbnail request.</summary>
    int? ReadHeldOnDevice(IMtpDevice dev, CameraCapture c)
    {
        var item = new MtpItem(c.Folder, c.FileName, c.Size, c.Modified);
        try
        {
            var header = Lux.Engine.Lri.LriFile.ReadFirstBlockHeader((offset, count) => dev.ReadRange(item, offset, count), c.Size);
            return header is null ? -1 : Lux.Store.HeldTurn.Degrees(header);
        }
        catch (NotSupportedException) { return -1; }
        catch (MtpException e) when (e.Kind == MtpErrorKind.NotFound) { return null; }
        catch (MtpException e) when (e.Kind == MtpErrorKind.Disconnected) { OnDeviceFailure(e, "reading how a photo was held"); return null; }
        catch (Exception e) when (e is MtpException or IOException) { Log($"held turn of {c.Name}: {e.Message}"); return null; }
    }

    /// <summary>Records the held turn of a capture in the list of its camera, when the capture is still the same file. Then it tells the page.</summary>
    void SetHeld(CameraCapture c, int degrees)
    {
        CameraCapture? changed = null;
        lock (_gate)
        {
            if (!_listings.TryGetValue(c.Camera, out var l)) return;
            int i = l.Items.FindIndex(x => x.Key == c.Key);
            if (i < 0 || l.Items[i].Size != c.Size || l.Items[i].HeldDegrees == degrees) return;
            l.Items[i] = changed = l.Items[i] with { HeldDegrees = degrees };
            l.Dirty = true;
        }
        if (changed is not null && Captures.Any(x => x.Key == changed.Key)) RaiseCaptures(new CameraCapturesChangedEventArgs { Updated = new[] { changed } });
    }

    // ================================================================================================================ planning

    /// <summary>A destination on the camera itself (the desktop's camera mounts): refused, the camera is never written to.</summary>
    internal static bool IsOnCamera(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("/gvfs/gphoto2:", StringComparison.OrdinalIgnoreCase) || p.Contains("/gvfs/mtp:", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/gvfs/afc:", StringComparison.OrdinalIgnoreCase);
    }

    static string? DestinationProblem(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return "Choose a folder to copy to.";
        if (!Path.IsPathFullyQualified(destination)) return "Choose a folder to copy to: give its full path.";
        if (IsOnCamera(destination)) return "That folder is on the camera itself. Choose a folder on this computer.";
        if (File.Exists(destination)) return $"{destination} is a file, not a folder.";
        return null;
    }

    static string DayFolder(CameraImportOptions o, string? day) => o.OneFolderPerDay && day is not null ? Path.Combine(o.Destination, day) : o.Destination;

    /// <summary>Where a file goes: <c>name</c> in its folder; when a different file (another size) already has that name, <c>name-2</c>,
    /// <c>name-3</c> … (as exports keep both). Skipped = a file of the same name and size is already there.</summary>
    internal static (string Path, bool Skip) TargetOf(string folder, string name, long size)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 1; ; n++)
        {
            string p = Path.Combine(folder, n == 1 ? name : $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}{ext}");
            if (!File.Exists(p)) return (p, false);
            try { if (new FileInfo(p).Length == size) return (p, true); } catch (IOException) { }
        }
    }

    static string DayKey(CameraCapture c) => c.Day is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    public CameraImportPlan PlanImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options)
    {
        long bytes = captures.Sum(c => Math.Max(0, c.Size));
        string? problem = DestinationProblem(options.Destination);
        if (problem is not null) return new CameraImportPlan { Count = captures.Count, Bytes = bytes, BytesToCopy = bytes, Problem = problem };
        if (captures.Count == 0)
            return new CameraImportPlan { Count = 0, Problem = "Choose the photos to import.", FreeBytes = MtpPuller.FreeSpace(options.Destination) };
        var folders = captures.Select(c => DayFolder(options, c.Day is null ? null : DayKey(c))).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        int there = 0; long toCopy = 0;
        foreach (var c in captures)
        {
            var (_, skip) = TargetOf(DayFolder(options, c.Day is null ? null : DayKey(c)), c.FileName, c.Size);
            if (skip) there++; else toCopy += Math.Max(0, c.Size);
        }
        long? free = MtpPuller.FreeSpace(options.Destination);
        var st = Status;
        if (!st.IsOpen) problem = "Connect the camera to import.";
        else if (captures.Any(c => c.Camera != (Current())) ) problem = "These photos are on another camera.";
        else if (MtpPuller.CheckFreeSpace(options.Destination, toCopy) is { } space) problem = space;
        return new CameraImportPlan { Count = captures.Count, Bytes = bytes, AlreadyThere = there, BytesToCopy = toCopy, FreeBytes = free, Folders = folders, Problem = problem };
    }

    string? Current() { lock (_gate) return _current?.Serial; }

    public long StartImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options)
    {
        var plan = PlanImport(captures, options);
        if (!plan.CanImport) throw new LibraryException(plan.Problem ?? "There is nothing to import.");
        string? name; lock (_gate) name = _info?.DisplayName;
        var request = new CameraImportJobRequest(Path.GetFullPath(options.Destination))
        {
            Files = captures.OrderBy(c => c.Modified ?? DateTimeOffset.MaxValue).ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                            .Select(c => new CameraImportFile(c.Folder, c.FileName, c.Size, c.Day is null ? null : DayKey(c)) { Modified = c.Modified }).ToList(),
            OneFolderPerDay = options.OneFolderPerDay, After = options.After, CameraSerial = Current(), CameraName = name,
        };
        long id = _lib.Jobs.Enqueue(request);
        if (_lib.Jobs.Get(id) is { IsFinished: false }) lock (_gate) _importJobs.Add(id);
        RaiseStatus();
        return id;
    }

    void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        if (e.Job.Kind != JobKind.Import) return;
        bool changed;
        lock (_gate) changed = e.Job.IsFinished || e.Removed ? _importJobs.Remove(e.Job.Id) : _importJobs.Add(e.Job.Id);
        if (changed) RaiseStatus();
    }

    // ================================================================================================================ the import job

    /// <summary>Synchronous progress (the job throttles what it forwards): bytes of the file in hand added to the bytes done before it.</summary>
    sealed class FileProgress(IJobReporter job, long before, long total, string status) : IProgress<long>
    {
        public void Report(long value) => job.Report(total <= 0 ? 0 : Math.Min(1, (before + value) / (double)total), status);
    }

    public async Task RunAsync(CameraImportJobRequest request, IJobReporter job)
    {
        var cancel = job.Cancel;
        lock (_gate) _importJobs.Add(job.JobId);
        RaiseStatus();
        string? serial; bool open;
        lock (_gate) { serial = _current?.Serial; open = IsOpenLocked; }
        if (!open) throw new LibraryException("The camera is not connected. Connect it and continue the import: the photos already copied are skipped.");
        if (request.CameraSerial is { } want && serial != want)
            throw new LibraryException($"Another camera is connected ({serial}). This import is from the camera {want}.");
        if (DestinationProblem(request.Destination) is { } dp) throw new LibraryException(dp);

        // the service checks the chosen photos on the camera first. The list on screen can be from an earlier connection. A capture that is no
        // longer on the camera is not copied. A capture that was replaced under the same name is copied as it is now.
        job.Items(0, request.Files.Count);
        job.Report(0, "Checking the photos on the camera");
        List<CameraImportFile> present; List<string> gone;
        try
        {
            (present, gone) = await _worker.Run(PrioCopy, t =>
            {
                if (_dev is not { } dev) throw new MtpException(MtpErrorKind.Disconnected, 0, "The camera is not connected.");
                var ok = new List<CameraImportFile>(); var missing = new List<string>();
                foreach (var f in request.Files)
                {
                    t.ThrowIfCancellationRequested();
                    try
                    {
                        var it = dev.GetItem(f.Folder, f.Name);
                        ok.Add(it.Size > 0 && it.Size != f.Size ? f with { Size = it.Size, Modified = it.Modified ?? f.Modified } : f);
                    }
                    catch (MtpException e) when (e.Kind == MtpErrorKind.NotFound) { missing.Add(f.Name); }
                    catch (NotSupportedException) { ok.Add(f); }
                }
                return (ok, missing);
            }, cancel).ConfigureAwait(false);
        }
        catch (MtpException e) when (e.Kind is MtpErrorKind.Disconnected or MtpErrorKind.NotFound)
        {
            throw new LibraryException("The camera was disconnected before the copy started. Connect it and continue the import.");
        }
        catch (MtpException e)
        {
            Log("check before the copy: " + e.Message);
            throw new LibraryException("The camera did not answer when the photos were checked. Continue the import to try again.");
        }
        if (gone.Count > 0)
        {
            RemoveGone(serial!, request.Files.Where(f => gone.Contains(f.Name)).Select(f => KeyOf(serial!, f.Folder, f.Name)).ToList());
            job.Note(GoneNote(gone));
            if (present.Count == 0)
                throw new LibraryException(gone.Count == 1 ? $"{Path.GetFileNameWithoutExtension(gone[0])} is no longer on the camera, so nothing was copied."
                                                           : $"None of the {gone.Count} photos chosen are on the camera any more, so nothing was copied.");
        }

        // where each file goes, and which are there already
        var plan = new List<(CameraImportFile File, string Dest, bool Skip)>();
        foreach (var f in present)
        {
            string folder = request.OneFolderPerDay && f.Day is not null ? Path.Combine(request.Destination, f.Day) : request.Destination;
            var (dest, skip) = TargetOf(folder, f.Name, f.Size);
            plan.Add((f, dest, skip));
        }
        long total = plan.Where(p => !p.Skip).Sum(p => Math.Max(0, p.File.Size));
        if (MtpPuller.CheckFreeSpace(request.Destination, total) is { } space) throw new LibraryException(space);

        var summary = new CameraImportSummary
        {
            StartedUtc = DateTime.UtcNow, JobId = job.JobId, State = CameraImportState.Running, Requested = plan.Count, After = request.After, Destination = request.Destination,
        };
        SaveLast(summary);
        var files = new List<string>();
        int copied = 0, skipped = 0;
        long done = 0;
        string? problem = null;
        var state = CameraImportState.Complete;
        _transferring = true;   // polling stops while files are copied
        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var (f, dest, skip) = plan[i];
                job.Items(i, plan.Count);
                string stem = Path.GetFileNameWithoutExtension(f.Name);
                if (skip)
                {
                    skipped++; files.Add(dest);
                    job.Report(total <= 0 ? 1 : done / (double)total, $"{stem} is already there");
                    SaveLast(summary with { Copied = copied, Skipped = skipped, Files = files.ToList() });
                    continue;
                }
                job.Report(total <= 0 ? 0 : done / (double)total, $"Copying {stem}");
                var item = new MtpItem(f.Folder, f.Name, f.Size, null);
                var progress = new FileProgress(job, done, total, $"Copying {stem}");
                bool fileGone = await _worker.Run(PrioCopy, t =>
                {
                    if (_dev is not { } dev) throw new MtpException(MtpErrorKind.Disconnected, 0, "The camera is not connected.");
                    try { MtpPuller.PullOne(dev, item, dest, false, progress, t); return false; }
                    // the file left the camera after the check, but the camera is still there: skip this file and copy the others
                    catch (MtpException e) when (e.Kind == MtpErrorKind.NotFound && _backend.Autodetect().Any(c => c.Port == _cand?.Port)) { return true; }
                    catch (MtpException e) when (e.Kind is MtpErrorKind.Disconnected or MtpErrorKind.Failed) { OnDeviceFailure(e, "copying photos"); throw; }
                }, cancel).ConfigureAwait(false);
                if (fileGone)
                {
                    gone.Add(f.Name);
                    RemoveGone(serial!, new[] { KeyOf(serial!, f.Folder, f.Name) });
                    job.Note(GoneNote(new[] { f.Name }));
                    continue;
                }
                // the copy keeps the camera's file time (the capture time), as other importers do
                if (f.Modified is { } camTime) { try { File.SetLastWriteTimeUtc(dest, camTime.UtcDateTime); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                copied++; files.Add(dest);
                done += Math.Max(0, f.Size);
                SaveLast(summary with { Copied = copied, Skipped = skipped, Files = files.ToList() });
            }
            job.Items(plan.Count, plan.Count);
            job.Report(1, copied == 0 ? "Everything was already there" : "Copied");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            state = CameraImportState.Cancelled;
            throw;
        }
        catch (MtpException e) when (e.Kind is MtpErrorKind.Disconnected or MtpErrorKind.NotFound)
        {
            state = CameraImportState.Stopped;
            problem = "The camera was disconnected during the copy.";
            throw new LibraryException($"The camera was disconnected during the copy. {Photos(copied + skipped)} of {plan.Count} are copied and kept; "
                                       + "connect the camera and continue the import to copy the rest (the copied ones are skipped).");
        }
        catch (MtpException e)
        {
            state = CameraImportState.Stopped;
            problem = "The camera stopped answering during the copy.";
            Log("copy: " + e.Message);
            throw new LibraryException($"The camera stopped answering during the copy. {Photos(copied + skipped)} of {plan.Count} are copied and kept; "
                                       + "continue the import to copy the rest (switching the camera off and on first often helps).");
        }
        catch (UnauthorizedAccessException)
        {
            state = CameraImportState.Stopped;
            problem = "Lux is not allowed to write to the folder.";
            throw new LibraryException($"Lux is not allowed to write to {request.Destination}. Choose another folder.");
        }
        catch (IOException e)
        {
            state = CameraImportState.Stopped;
            bool full = Lux.Library.Core.LibraryCore.IsDiskFull(e);
            problem = full ? "The destination drive is full." : "A file could not be written.";
            Log("write: " + e.Message);
            throw new LibraryException(full
                ? $"The drive of {request.Destination} is full. {Photos(copied + skipped)} of {plan.Count} are copied and kept; free some space and continue the import."
                : $"A photo could not be written to {request.Destination}. {Photos(copied + skipped)} of {plan.Count} are copied and kept; check the drive and continue the import.");
        }
        finally
        {
            _transferring = false;
            if (copied > 0) job.Note($"{Photos(copied)} copied");
            if (skipped > 0) job.Note($"{Photos(skipped)} already there");
            if (state == CameraImportState.Cancelled && copied + skipped < plan.Count) job.Note($"{Photos(copied + skipped)} of {plan.Count} copied before it was cancelled; continue it to copy the rest");
            job.Result(files.FirstOrDefault(), files);
            summary = summary with { FinishedUtc = DateTime.UtcNow, State = state, Copied = copied, Skipped = skipped, Problem = problem, Files = files.ToList() };
            SaveLast(summary);
            if (files.Count > 0)
            {
                if (request.After != CameraAfterImport.CopyOnly && state == CameraImportState.Complete)
                    job.Note(request.After == CameraAfterImport.AnalyseFullDetail ? "analysis (full detail) queued after the folder scan" : "analysis queued after the folder scan");
                var s0 = summary;
                _ = Task.Run(() => AfterCopyAsync(request, s0));
            }
            lock (_gate) _importJobs.Remove(job.JobId);
            RaiseStatus();
        }
    }

    static string Photos(int n) => n == 1 ? "1 photo" : $"{n.ToString(CultureInfo.InvariantCulture)} photos";

    /// <summary>The job note for captures that are not on the camera any more, for one capture or for several.</summary>
    static string GoneNote(IReadOnlyCollection<string> names)
    {
        var stems = names.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return stems.Count == 1 ? $"{stems[0]} is no longer on the camera and was not copied"
            : $"{Photos(stems.Count)} are no longer on the camera and were not copied ({string.Join(", ", stems.Take(5))}{(stems.Count > 5 ? ", …" : "")})";
    }

    /// <summary>Removes captures from the list of the camera when the import finds that they are gone.</summary>
    void RemoveGone(string serial, IReadOnlyCollection<string> keys)
    {
        int n;
        lock (_gate)
        {
            if (!_listings.TryGetValue(serial, out var l)) return;
            n = l.Items.RemoveAll(i => keys.Contains(i.Key));
            if (n > 0) l.Dirty = true;
        }
        if (n > 0) { RaiseCaptures(new CameraCapturesChangedEventArgs { Removed = keys.ToList() }); RaiseStatus(); }
    }

    /// <summary>After the copy (outside the job, so a one-worker queue cannot wait on itself): add the destination to the library or rescan it,
    /// wait for the scan, find the captures the files became, queue the analysis chosen.</summary>
    async Task AfterCopyAsync(CameraImportJobRequest request, CameraImportSummary summary)
    {
        try
        {
            var folder = await _lib.AddFolderAsync(request.Destination).ConfigureAwait(false);
            Log($"import: destination in the library as folder {folder.Id} ({folder.Path}), scan {folder.ScanJobId}");
            var ids = new List<string>();
            for (int round = 0; round < 4; round++)
            {
                var scans = _lib.Jobs.GetJobs(includeFinished: false).Where(j => j.Kind == JobKind.FolderScan && j.FolderId == folder.Id).Select(j => j.Id).ToList();
                if (round == 0 && folder.ScanJobId is long s && !scans.Contains(s)) scans.Add(s);
                foreach (var id in scans)
                {
                    try { var end = await _lib.Jobs.WaitAsync(id).ConfigureAwait(false); Log($"import: folder scan {id} ended {end.State}"); }
                    catch (LibraryException e) { Log($"import: folder scan {id}: {e.Message}"); }
                }
                ids = _lib.FindByPaths(summary.Files).Values.Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count >= summary.Files.Count || _disposed) break;
                await Task.Delay(500).ConfigureAwait(false);   // a scan the folder watcher started meanwhile is picked up in the next round
            }
            Log($"import: {ids.Count} of {summary.Files.Count} files found in the library");
            // the analysis chosen follows a complete import (a stopped or cancelled one is in the library; continuing it queues the analysis)
            if (summary.State == CameraImportState.Complete)
            {
                var items = ids.Select(_lib.Get).Where(c => c is not null).Select(c => c!).ToList();
                switch (request.After)
                {
                    case CameraAfterImport.Analyse13Mp:
                        foreach (var c in items.Where(c => c.CacheLevel < CacheLevel.Linear13 && !c.Unreadable))
                            _lib.Jobs.Enqueue(new AnalysisJobRequest(c.Id, CacheLevel.Linear13));
                        break;
                    case CameraAfterImport.AnalyseFullDetail:
                        _lib.UpgradeToFullDetail(items.Where(c => c.CacheLevel < CacheLevel.Full && !c.Unreadable).Select(c => c.Id).ToList());
                        break;
                }
            }
            lock (_gate) { if (_last?.JobId == summary.JobId) _last = _last with { CaptureIds = ids }; }
            SaveLast(null);
            RefreshInLibrary();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log("after import: " + e);
            string why = e is LibraryException ? " " + e.Message : " Add the folder in the Library to see them.";
            lock (_gate) { if (_last?.JobId == summary.JobId) _last = _last with { Problem = "The copied photos could not be added to the library." + why }; }
            SaveLast(null);
        }
    }

    // ================================================================================================================ the last import

    const string LastKey = "camera.last_import";

    public CameraImportSummary? LastImport { get { lock (_gate) return _last; } }

    CameraImportSummary? LoadLast()
    {
        try { return _lib.Settings.GetValue(LastKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<CameraImportSummary>(json) : null; }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Keep <paramref name="s"/> as the last import (null: save the present one again after a change).</summary>
    void SaveLast(CameraImportSummary? s)
    {
        CameraImportSummary? now;
        lock (_gate) { if (s is not null) _last = s; now = _last; }
        try { _lib.Settings.SetValue(LastKey, now is null ? null : JsonSerializer.Serialize(now)); }
        catch (Exception e) when (e is not OutOfMemoryException) { Log("saving the last import: " + e.Message); }
        try { LastImportChanged?.Invoke(this, EventArgs.Empty); } catch (Exception e) { Log("last import subscriber: " + e.Message); }
    }

    public (int Analysed, int Total)? LastImportAnalysis()
    {
        var last = LastImport;
        if (last is null || last.After == CameraAfterImport.CopyOnly || last.CaptureIds.Count == 0) return null;
        var target = last.After == CameraAfterImport.AnalyseFullDetail ? CacheLevel.Full : CacheLevel.Linear13;
        int analysed = last.CaptureIds.Count(id => _lib.Get(id) is { } c && c.CacheLevel >= target);
        return (analysed, last.CaptureIds.Count);
    }

    // ================================================================================================================ lifetime

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveListing(onlyIfDirty: true);   // keeps the held turns that were read after the last write
        _poll?.Dispose();
        _libraryTimer.Dispose();
        _lib.CapturesChanged -= OnLibraryChanged;
        _lib.Jobs.JobChanged -= OnJobChanged;
        _lib.SetCameraImporter(null);
        _worker.Dispose(() => { try { _dev?.Dispose(); } catch (Exception) { } _dev = null; }, TimeSpan.FromSeconds(5));
    }
}
