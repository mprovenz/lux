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
    /// <returns>The stream, and the offset it really starts at (0 when the source cannot start in the middle).</returns>
    Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, CancellationToken cancel);
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
/// </summary>
public sealed class DepthModelManager : IDepthModelService
{
    public const int Attempts = 3;

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
    public long DownloadBytes => _runtime is null ? 0 : _runtime.Archive.Size + _manifest.Model.File.Size;

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
        long total = DownloadBytes;
        Set(new DepthModelStatus(DepthModelState.Downloading, BytesTotal: total));
        try
        {
            Directory.CreateDirectory(Folder);
            // the runtime: the archive, then the one library out of it
            string lib = LibraryPath!;
            if (Length(lib) != _runtime!.LibrarySize)
            {
                string archive = Path.Combine(Folder, _runtime.Archive.Name + ".part");
                await Fetch(_runtime.Archive, archive, 0, total, cancel).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
                string part = lib + ".part";
                Extract(archive, _runtime.Entry, part);
                if (Length(part) != _runtime.LibrarySize || Sha256(part) != _runtime.LibrarySha256)
                {
                    File.Delete(part); File.Delete(archive);
                    throw new DepthModelException("The runtime library in the downloaded archive does not match its expected hash.");
                }
                File.Move(part, lib, overwrite: true);
                File.Delete(archive);
            }
            // the model
            if (Length(ModelPath) != _manifest.Model.File.Size)
            {
                string part = ModelPath + ".part";
                await Fetch(_manifest.Model.File, part, _runtime.Archive.Size, total, cancel).ConfigureAwait(false);
                File.Move(part, ModelPath, overwrite: true);
            }
            Set(Probe());
        }
        catch (OperationCanceledException) { Set(Probe()); }
        catch (Exception e) when (e is DepthModelException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException)
        {
            Set(new DepthModelStatus(DepthModelState.Failed, "The download failed: " + e.Message));
        }
    }

    /// <summary>Download one file to <paramref name="part"/> and check it. A part already there is continued. A source error is tried
    /// again; a file that fails its check is deleted and fetched once more from the start.</summary>
    async Task Fetch(DownloadFile file, string part, long doneBefore, long total, CancellationToken cancel)
    {
        bool refetched = false; Exception? last = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                long have = Math.Max(Length(part), 0);
                if (have > file.Size) { File.Delete(part); have = 0; }
                if (have < file.Size)
                {
                    var (stream, offset) = await _source.OpenAsync(file, have, cancel).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        if (offset != have) { offset = 0; have = 0; }   // the source starts at the beginning
                        await using var fs = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
                        var buf = new byte[1 << 16]; int n; long lastReport = 0;
                        while ((n = await stream.ReadAsync(buf, cancel).ConfigureAwait(false)) > 0)
                        {
                            if (have + n > file.Size) throw new DepthModelException($"{file.Name} is larger than expected.");
                            await fs.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
                            have += n;
                            if (have - lastReport >= 1 << 20 || have == file.Size)
                            {
                                lastReport = have;
                                Set(new DepthModelStatus(DepthModelState.Downloading, BytesDone: doneBefore + have, BytesTotal: total));
                            }
                        }
                    }
                    if (have < file.Size) throw new IOException($"the connection closed after {have} of {file.Size} bytes of {file.Name}");
                }
                if (Sha256(part) == file.Sha256) return;
                File.Delete(part);
                if (refetched) throw new DepthModelException($"{file.Name} does not match its expected hash. The file was rejected.");
                refetched = true; attempt--;   // one clean fetch does not count as a source error
            }
            catch (Exception e) when (e is IOException or HttpRequestException && attempt + 1 < Attempts) { last = e; }
        }
        throw new DepthModelException($"{file.Name} could not be downloaded" + (last is null ? "." : ": " + last.Message), last);
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
        public Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, CancellationToken cancel)
        {
            var fs = new FileStream(Path.Combine(folder, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
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

        public async Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, CancellationToken cancel)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl is null ? file.Url : baseUrl + file.Name);
            if (offset > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
            var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode; resp.Dispose();
                throw new HttpRequestException($"the server answered {code} for {file.Name}");
            }
            // 206 = the range was honored; 200 = the whole file from the start
            long start = resp.StatusCode == System.Net.HttpStatusCode.PartialContent ? offset : 0;
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
