using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Lux.Store;

namespace Lux.DepthModel;

/// <summary>Where the files of the refined depth feature stand.</summary>
public enum DepthModelState
{
    /// <summary>The files are not on this computer.</summary>
    NotInstalled = 0,
    /// <summary>A download is running (<see cref="DepthModelStatus.BytesDone"/> of <see cref="DepthModelStatus.BytesTotal"/>).</summary>
    Downloading = 1,
    /// <summary>The files are in place.</summary>
    Installed = 2,
    /// <summary>The last download or the last use failed (<see cref="DepthModelStatus.Reason"/>).</summary>
    Failed = 3,
    /// <summary>There is no runtime for this computer (<see cref="DepthModelStatus.Reason"/>).</summary>
    NotAvailable = 4,
}

/// <param name="Reason">Plain language, for <see cref="DepthModelState.Failed"/> and <see cref="DepthModelState.NotAvailable"/>.</param>
/// <param name="Version">The model and the runtime version, when installed.</param>
/// <param name="SizeOnDisk">Bytes the installed files take.</param>
public sealed record DepthModelStatus(DepthModelState State, string? Reason = null, long BytesDone = 0, long BytesTotal = 0, string? Version = null, long SizeOnDisk = 0);

/// <summary>The files of the refined depth feature on this computer: their state, their download and their removal.</summary>
public interface IDepthModelService
{
    DepthModelStatus Status { get; }
    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event Action? StatusChanged;
    /// <summary>The size of the download for this computer, in bytes; 0 when there is none.</summary>
    long DownloadBytes { get; }
    /// <summary>The folder that holds the files.</summary>
    string Folder { get; }
    /// <summary>Download and check the files. Completes when they are installed, when the download failed (see <see cref="Status"/>) or when
    /// it was canceled. A second call while one runs waits for the first.</summary>
    Task DownloadAsync();
    /// <summary>Stop a running download. The part already received stays and the next download continues from it.</summary>
    void CancelDownload();
    /// <summary>Delete the files (and any partly downloaded ones).</summary>
    void Remove();
}

/// <summary>Opens a file of the manifest for reading from a byte offset. The stream may end early: the caller checks the size.</summary>
public interface IDownloadSource
{
    /// <param name="length">The number of bytes the caller reads from <paramref name="offset"/>; null = to the end of the file. The
    /// stream can hold more: the caller stops after this many bytes.</param>
    /// <returns>The stream, and the offset it really starts at (0 when the source cannot start in the middle).</returns>
    Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, long? length, CancellationToken cancel);

    /// <summary>The place this source reads <paramref name="file"/> from, for a message: the name of the server, or a folder.</summary>
    string PlaceOf(DownloadFile file) => Uri.TryCreate(file.Url, UriKind.Absolute, out var url) ? url.Host : file.Name;
}

/// <summary>A source that does not answer or that refuses a file.</summary>
public sealed class DownloadSourceException : IOException
{
    /// <summary>The name of the server.</summary>
    public string Host { get; }
    /// <summary>The HTTP status of the answer; null when there was no answer.</summary>
    public int? Status { get; }
    public DownloadSourceException(string host, int? status, string message, Exception? inner = null) : base(message, inner) { Host = host; Status = status; }
}

/// <summary>A download that cannot be used: the wrong size or the wrong hash, or the source failed.</summary>
public sealed class DepthModelException : Exception
{
    public DepthModelException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The refined depth files in <c>&lt;app data&gt;/models/depth</c>: the depth network and the runtime library for this computer.
/// <para>A file is downloaded to a <c>.part</c> file, checked against the size and SHA-256 of the manifest, and only then renamed. A
/// broken download continues from the part already received. A file that fails its check is deleted and fetched once more from the
/// start; a second failure ends the download with an error.</para>
/// <para>The runtime library has several sources (<see cref="RuntimePackage.Sources"/>). The manager tries them in order and goes to
/// the next one when a source fails: it does not answer, it has no such file, it stops sending, or its file fails the check. The
/// download fails only when every source failed. The first source is a small archive with the one library. The next one is a zip
/// package that holds the libraries of all platforms: the manager reads the zip directory with byte range requests and downloads only
/// the one entry, or the whole package when the server cannot send a part of it. The library is checked against its SHA-256 whatever
/// the source.</para>
/// </summary>
public sealed class DepthModelManager : IDepthModelService
{
    public const int Attempts = 3;

    /// <summary>A download that receives no bytes for this long fails. The part already received stays.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    readonly DepthModelManifest _manifest; readonly RuntimePackage? _runtime; readonly IDownloadSource _source;
    readonly object _lock = new();
    Task? _running; CancellationTokenSource? _cancel;
    DepthModelStatus _status;

    public string Folder { get; }
    public DepthModelManifest Manifest => _manifest;

    /// <summary>The manager of this process: the built-in manifest, this computer's platform, the folder under the app's data folder, and
    /// the upstream sources unless <see cref="DepthModelManifest.SourceVariable"/> names another.</summary>
    public static DepthModelManager Default() => new(DefaultFolder, DepthModelManifest.Default, DepthModelManifest.CurrentRid, null);

    /// <summary><c>&lt;app data&gt;/models/depth</c>.</summary>
    public static string DefaultFolder => Path.Combine(LocalContextStore.AppDataFolder, "models", "depth");

    /// <param name="source">Null = <see cref="DepthModelManifest.SourceVariable"/> when set, else each file's own URL.</param>
    public DepthModelManager(string folder, DepthModelManifest manifest, string? rid, IDownloadSource? source)
    {
        Folder = Path.GetFullPath(folder); _manifest = manifest; _runtime = manifest.RuntimeFor(rid);
        _source = source ?? Sources.FromEnvironment();
        _status = _runtime is null
            ? new DepthModelStatus(DepthModelState.NotAvailable, rid is null
                ? "Refined depth is not available on this computer: Lux has no inference runtime for its operating system or processor."
                : $"Refined depth is not available on this computer: there is no inference runtime for {rid}.")
            : Probe();
    }

    public DepthModelStatus Status { get { lock (_lock) return _status; } }
    public event Action? StatusChanged;
    /// <summary>The size of the download from the first source of the runtime, and the model.</summary>
    public long DownloadBytes => _runtime is null ? 0 : _runtime.Sources[0].DownloadSize + _manifest.Model.File.Size;

    public string ModelPath => Path.Combine(Folder, _manifest.Model.File.Name);
    public string? LibraryPath => _runtime is null ? null : Path.Combine(Folder, $"onnxruntime-{_manifest.RuntimeVersion}-{_runtime.Rid}", _runtime.LibraryFile);
    string VersionText => $"{DepthModelManifest.ModelName}, {DepthModelManifest.RuntimeName} {_manifest.RuntimeVersion}";

    void Set(DepthModelStatus s)
    {
        lock (_lock) _status = s;
        StatusChanged?.Invoke();
    }

    /// <summary>Installed when both files are present with the sizes of the manifest. The hashes are checked before use
    /// (<see cref="VerifyInstalled"/>).</summary>
    DepthModelStatus Probe()
    {
        long model = Length(ModelPath), lib = Length(LibraryPath!);
        return model == _manifest.Model.File.Size && lib == _runtime!.LibrarySize
            ? new DepthModelStatus(DepthModelState.Installed, Version: VersionText, SizeOnDisk: model + lib)
            : new DepthModelStatus(DepthModelState.NotInstalled);
    }

    static long Length(string path) { try { var f = new FileInfo(path); return f.Exists ? f.Length : -1; } catch (IOException) { return -1; } }

    /// <summary>Check the installed files against the hashes of the manifest. Throws <see cref="DepthModelException"/> when the files are
    /// missing or do not match; the state then says so.</summary>
    public void VerifyInstalled()
    {
        if (_runtime is null) throw new DepthModelException(Status.Reason!);
        if (Probe().State != DepthModelState.Installed)
        {
            Set(Probe());
            throw new DepthModelException("The refined depth files are not installed.");
        }
        if (Sha256(ModelPath) != _manifest.Model.File.Sha256 || Sha256(LibraryPath!) != _runtime.LibrarySha256)
        {
            const string why = "The refined depth files on this computer are damaged. Remove them and download them again.";
            Set(new DepthModelStatus(DepthModelState.Failed, why));
            throw new DepthModelException(why);
        }
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public Task DownloadAsync()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false }) return _running;
            if (_runtime is null || _status.State == DepthModelState.Installed) return Task.CompletedTask;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            return _running = Task.Run(() => RunDownload(token));
        }
    }

    public void CancelDownload() { lock (_lock) _cancel?.Cancel(); }

    async Task RunDownload(CancellationToken cancel)
    {
        var model = _manifest.Model.File;
        long total = DownloadBytes, runtimeBytes = _runtime!.Sources[0].DownloadSize;
        Set(new DepthModelStatus(DepthModelState.Downloading, BytesTotal: total));
        try
        {
            Directory.CreateDirectory(Folder);
            string lib = LibraryPath!;
            if (Length(lib) != _runtime.LibrarySize)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
                string part = lib + ".part";
                var sources = _runtime.Sources;
                if (_source is Sources.FolderSource folder)
                {
                    // a folder holds one archive of the runtime: only the sources whose archive is there count
                    var present = sources.Where(s => folder.Has(s.Archive.Name)).ToList();
                    if (present.Count > 0) sources = present;
                    else if (_runtime.LocalArchive is { } local && folder.Has(local))
                    {
                        Extract(folder.PathOf(local), _runtime.LocalArchiveEntry!, part);
                        CheckLibrary(part);
                        sources = Array.Empty<RuntimeSource>();
                    }
                }
                await FromSources("runtime", sources, s => s.Archive, async s =>
                {
                    runtimeBytes = s.DownloadSize; total = runtimeBytes + model.Size;
                    await FetchLibrary(s, part, total, cancel).ConfigureAwait(false);
                }).ConfigureAwait(false);
                File.Move(part, lib, overwrite: true);
                foreach (var s in _runtime.Sources) { File.Delete(ArchivePart(s)); }   // parts of the sources that failed
            }
            if (Length(ModelPath) != model.Size)
            {
                string part = ModelPath + ".part";
                await FromSources("model", _manifest.Model.Sources.ToList(), f => f, f => Fetch(f, part, runtimeBytes, total, cancel)).ConfigureAwait(false);
                File.Move(part, ModelPath, overwrite: true);
            }
            Set(Probe());
        }
        catch (OperationCanceledException) { Set(Probe()); }
        catch (SourcesFailed e) { Set(new DepthModelStatus(DepthModelState.Failed, e.Message)); }
        catch (Exception e) when (e is DepthModelException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException)
        {
            Set(new DepthModelStatus(DepthModelState.Failed, "The download failed: " + e.Message));
        }
    }

    /// <summary>One source failed. The message is short and has no place in it: "HTTP 404", "no data arrived for 60 seconds".</summary>
    sealed class SourceFailed(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Every source of a file failed. The message is the whole reason, for the user.</summary>
    sealed class SourcesFailed(string message) : Exception(message);

    /// <summary>
    /// Get a file from the first source that works. A source that fails is noted and the next one is tried. When all fail, the error
    /// names the places tried and what each one did.
    /// </summary>
    /// <param name="what">The file in the words of a message: "runtime" or "model".</param>
    async Task FromSources<T>(string what, IReadOnlyList<T> sources, Func<T, DownloadFile> file, Func<T, Task> fetch)
    {
        if (sources.Count == 0) return;
        var failed = new List<(string Place, string Detail)>();
        foreach (var source in sources)
        {
            try { await fetch(source).ConfigureAwait(false); return; }
            catch (Exception e) when (e is SourceFailed or DepthModelException or IOException or HttpRequestException or InvalidDataException)
            {
                var entry = (_source.PlaceOf(file(source)), e.Message.TrimEnd('.'));
                if (!failed.Contains(entry)) failed.Add(entry);
            }
        }
        var places = failed.Select(f => f.Place).Distinct().ToList();
        string from = places.Count == 1 ? places[0] : string.Join(", ", places.Take(places.Count - 1)) + " or " + places[^1];
        string detail = failed.Count == 1 ? failed[0].Detail : string.Join("; ", failed.Select(f => f.Place + ": " + f.Detail));
        throw new SourcesFailed($"The {what} could not be downloaded from {from}. Try again later. ({detail})");
    }

    /// <summary>The source has no file with this name: a client error other than "try again" (408, 429).</summary>
    static bool Refused(Exception e) => e is DownloadSourceException { Status: >= 400 and < 500 and not (408 or 429) };

    static SourceFailed Failure(DownloadFile file, Exception? last) => last switch
    {
        DownloadSourceException { Status: { } code } => new SourceFailed("HTTP " + code, last),
        null => new SourceFailed($"{file.Name} could not be downloaded"),
        _ => new SourceFailed(last.Message, last),
    };

    SourceFailed Stalled() => new(FormattableString.Invariant($"no data arrived for {StallTimeout.TotalSeconds:0.#} seconds"));

    void CheckLibrary(string part)
    {
        if (Length(part) == _runtime!.LibrarySize && Sha256(part) == _runtime.LibrarySha256) return;
        File.Delete(part);
        throw new DepthModelException("The runtime library does not match its expected hash. The file was rejected.");
    }

    /// <summary>The file that holds the downloaded part of a source's archive.</summary>
    string ArchivePart(RuntimeSource source)
        => Path.Combine(Folder, source.Ranged ? $"{source.Archive.Name}.{_runtime!.Rid}.part" : source.Archive.Name + ".part");

    /// <summary>Get the runtime library from one source to <paramref name="to"/> and check it.</summary>
    async Task FetchLibrary(RuntimeSource source, string to, long total, CancellationToken cancel)
    {
        if (source.Ranged) { await FetchEntry(source, to, total, cancel).ConfigureAwait(false); return; }
        string archive = ArchivePart(source);
        await Fetch(source.Archive, archive, 0, total, cancel).ConfigureAwait(false);
        try { Extract(archive, source.Entry, to); CheckLibrary(to); }
        finally { File.Delete(archive); }
    }

    /// <summary>Download one file to <paramref name="part"/> and check it. A part already there is continued. A source error is tried
    /// again; a file that fails its check is deleted and fetched once more from the start.</summary>
    async Task Fetch(DownloadFile file, string part, long doneBefore, long total, CancellationToken cancel)
    {
        for (bool refetched = false; ; refetched = true)
        {
            await Receive(file, 0, file.Size, part, doneBefore, total, cancel).ConfigureAwait(false);
            if (Sha256(part) == file.Sha256) return;
            File.Delete(part);
            if (refetched) throw new DepthModelException($"{file.Name} does not match its expected hash. The file was rejected.");
        }
    }

    /// <summary>
    /// Download the runtime library as one entry of a package: read the zip directory with byte range requests, download the entry's
    /// compressed data to a <c>.part</c> file (a part already there is continued), unpack it to <paramref name="to"/> and check it.
    /// The source fails when it cannot send a part of the package, or when the package does not have the expected structure.
    /// </summary>
    async Task FetchEntry(RuntimeSource source, string to, long total, CancellationToken cancel)
    {
        var rt = _runtime!; var package = source.Archive;
        string part = ArchivePart(source);
        for (bool refetched = false; ; refetched = true)
        {
            try
            {
                var entry = await ZipRange.FindAsync((offset, length) => ReadRange(package, offset, length, cancel), package.Size, source.Entry).ConfigureAwait(false);
                if (entry.Size != rt.LibrarySize) throw new ZipStructureException("The library in the package does not have the expected size.");
                await Receive(package, entry.DataOffset, entry.CompressedSize, part, 0, total, cancel).ConfigureAwait(false);
                bool good;
                using (var data = File.OpenRead(part))
                using (var library = File.Create(to))
                    good = ZipRange.Unpack(entry, data, library);
                File.Delete(part);
                if (good && Sha256(to) == rt.LibrarySha256) return;
                // a damaged part or another package: one more try from the start
                File.Delete(to);
                if (refetched) throw new DepthModelException("The runtime library does not match its expected hash. The file was rejected.");
            }
            catch (Exception e) when (e is ZipStructureException or RangeNotServed)
            {
                File.Delete(part);
                throw new SourceFailed(e.Message, e);
            }
        }
    }

    /// <summary>The source answered a range request with the file from its start.</summary>
    sealed class RangeNotServed() : Exception("the server does not send a part of a file");

    /// <summary>Read a small part of a file into memory. A source error is tried again.</summary>
    async Task<byte[]> ReadRange(DownloadFile file, long offset, int length, CancellationToken cancel)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                stall.CancelAfter(StallTimeout);
                var (stream, start) = await _source.OpenAsync(file, offset, length, stall.Token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    if (start != offset) throw new RangeNotServed();
                    var bytes = new byte[length]; int have = 0, n;
                    while (have < length && (n = await stream.ReadAsync(bytes.AsMemory(have), stall.Token).ConfigureAwait(false)) > 0)
                    {
                        have += n;
                        stall.CancelAfter(StallTimeout);
                    }
                    if (have == length) return bytes;
                    throw new IOException($"the connection closed after {have} of {length} bytes of {file.Name}");
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw Stalled(); }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                last = e;
                if (Refused(e)) break;
            }
        }
        throw Failure(file, last);
    }

    /// <summary>
    /// Download <paramref name="size"/> bytes of a file from <paramref name="start"/> to <paramref name="part"/>. A part already there
    /// is continued. A source error is tried again. A connection that sends no bytes for <see cref="StallTimeout"/> ends the download;
    /// the part stays.
    /// </summary>
    async Task Receive(DownloadFile file, long start, long size, string part, long doneBefore, long total, CancellationToken cancel)
    {
        bool ranged = start != 0 || size != file.Size;
        Exception? last = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                long have = Math.Max(Length(part), 0);
                if (have > size) { File.Delete(part); have = 0; }
                if (have == size) return;
                stall.CancelAfter(StallTimeout);
                var (stream, offset) = await _source.OpenAsync(file, start + have, ranged ? size - have : null, stall.Token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    if (offset != start + have)
                    {
                        if (ranged) throw new RangeNotServed();
                        have = 0;   // the source starts at the beginning
                    }
                    await using var fs = new FileStream(part, have == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
                    var buf = new byte[1 << 16]; int n; long lastReport = 0;
                    while (have < size && (n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, ranged ? size - have : buf.Length)), stall.Token).ConfigureAwait(false)) > 0)
                    {
                        if (have + n > size) throw new DepthModelException($"{file.Name} is larger than expected.");
                        await fs.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
                        have += n;
                        stall.CancelAfter(StallTimeout);
                        if (have - lastReport >= 1 << 20 || have == size)
                        {
                            lastReport = have;
                            Set(new DepthModelStatus(DepthModelState.Downloading, BytesDone: Math.Min(doneBefore + have, total), BytesTotal: total));
                        }
                    }
                }
                if (have == size) return;
                throw new IOException($"the connection closed after {have} of {size} bytes of {file.Name}");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw Stalled(); }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                last = e;
                if (Refused(e)) break;
            }
        }
        throw Failure(file, last);
    }

    /// <summary>Copy one entry of a <c>.zip</c> or <c>.tgz</c> archive to a file.</summary>
    static void Extract(string archive, string entry, string to)
    {
        static string Norm(string n) => n.StartsWith("./", StringComparison.Ordinal) ? n[2..] : n;
        using var fs = File.OpenRead(archive);
        Span<byte> magic = stackalloc byte[2];
        int got = fs.Read(magic); fs.Position = 0;
        if (got == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K')
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var e = zip.Entries.FirstOrDefault(x => Norm(x.FullName) == entry) ?? throw new DepthModelException("The downloaded archive does not contain the runtime library.");
            using var src = e.Open(); using var dst = File.Create(to);
            src.CopyTo(dst);
            return;
        }
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } te)
        {
            if (te.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Norm(te.Name) != entry || te.DataStream is null) continue;
            using var dst = File.Create(to);
            te.DataStream.CopyTo(dst);
            return;
        }
        throw new DepthModelException("The downloaded archive does not contain the runtime library.");
    }

    public void Remove()
    {
        CancelDownload();
        Task? running; lock (_lock) running = _running;
        try { running?.Wait(); } catch (AggregateException) { }
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Set(new DepthModelStatus(DepthModelState.Failed, "The files could not be removed: " + e.Message));
            return;
        }
        if (_runtime is not null) Set(Probe());
    }
}

/// <summary>The download sources: each file's own URL, or one folder or URL that holds every file under its name.</summary>
public static class Sources
{
    /// <summary>The source of this process: <see cref="DepthModelManifest.SourceVariable"/> when set, else the upstream URLs.</summary>
    public static IDownloadSource FromEnvironment()
    {
        string? over = Environment.GetEnvironmentVariable(DepthModelManifest.SourceVariable);
        if (string.IsNullOrWhiteSpace(over)) return new HttpSource(null);
        return over.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || over.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new HttpSource(over.TrimEnd('/') + "/") : new FolderSource(over);
    }

    /// <summary>Files of a local folder, by name.</summary>
    public sealed class FolderSource(string folder) : IDownloadSource
    {
        public string PathOf(string name) => Path.Combine(folder, name);
        public string PlaceOf(DownloadFile file) => folder;
        public bool Has(string name) => File.Exists(PathOf(name));

        public Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, long? length, CancellationToken cancel)
        {
            var fs = new FileStream(PathOf(file.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (offset > fs.Length) offset = 0;
            fs.Position = offset;
            return Task.FromResult<(Stream, long)>((fs, offset));
        }
    }

    /// <summary>HTTP downloads. A request carries the URL, a byte range and a generic client name: nothing about the user, the computer
    /// or the photos.</summary>
    public sealed class HttpSource(string? baseUrl) : IDownloadSource
    {
        static readonly HttpClient Client = Create();

        static HttpClient Create()
        {
            var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Lux");
            return c;
        }

        Uri UrlOf(DownloadFile file) => new(baseUrl is null ? file.Url : baseUrl + file.Name);
        public string PlaceOf(DownloadFile file) => UrlOf(file).Host;

        public async Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, long? length, CancellationToken cancel)
        {
            var url = UrlOf(file);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0 || length is not null) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, length is { } n ? offset + n - 1 : null);
            HttpResponseMessage resp;
            try { resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new DownloadSourceException(url.Host, null, e.Message, e); }
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode; resp.Dispose();
                throw new DownloadSourceException(url.Host, code, $"the server answered {code} for {file.Name}");
            }
            // 206 = the range was honored; 200 = the whole file from the start
            long start = resp.StatusCode == System.Net.HttpStatusCode.PartialContent ? resp.Content.Headers.ContentRange?.From ?? offset : 0;
            return (await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false), start);
        }
    }
}

/// <summary>The service of a library that has no refined depth files and cannot get them.</summary>
public sealed class UnavailableDepthModel : IDepthModelService
{
    public static UnavailableDepthModel Instance { get; } = new();
    public DepthModelStatus Status { get; } = new(DepthModelState.NotAvailable, "Refined depth is not available here.");
    public event Action? StatusChanged { add { } remove { } }
    public long DownloadBytes => 0;
    public string Folder => "";
    public Task DownloadAsync() => Task.CompletedTask;
    public void CancelDownload() { }
    public void Remove() { }
}
