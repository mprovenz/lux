using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Lux.DepthModel;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The download manager of the refined depth files, against a scripted local source: a normal install, a file with the wrong hash, a
/// connection that breaks (the download continues from the part received), a source that cannot continue, cancel, remove, damaged
/// installed files, and a computer with no runtime.
/// <para>The runtime library is one entry of a zip package. The tests with a local HTTP server check that the manager downloads only
/// that entry with byte range requests, continues it, falls back to the whole package when the server serves no ranges, and reports a
/// missing file, a server that cannot be reached and a connection that stalls. The tests with two servers check the order of the
/// sources: the first source is used when it works, and the next one when it fails. Nothing here reads from the internet.</para>
/// </summary>
public sealed class DepthModelTests : IDisposable
{
    readonly string _root = Path.Combine(Paths.AppData("depth-model-tests"), Guid.NewGuid().ToString("N")[..8]);
    readonly ITestOutputHelper _output;
    public DepthModelTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    const string Rid = "test-x64", Package = "runtime.9.9.nupkg", Entry = "runtimes/test-x64/native/lib.so";
    const string OldArchive = "runtime-test-9.9.tgz", OldEntry = "rt/lib/lib.so.1", Own = "runtime-9.9-test-x64.zip";
    static readonly byte[] Library = LibraryBytes(), Model = Bytes(300_000, 2);

    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Random bytes with a run of zeros, so that the compressed form differs from the file.</summary>
    static byte[] LibraryBytes() { var b = Bytes(60_000, 1); Array.Clear(b, 20_000, 15_000); return b; }

    static byte[] Tgz(string entry, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.NoCompression, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./rt/README") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("a runtime")) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "./" + entry + ".link") { LinkName = "lib.so" });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + entry) { DataStream = new MemoryStream(data) });
        }
        return ms.ToArray();
    }

    /// <summary>A package like the real one: a zip file with the libraries of several platforms and other files around them.</summary>
    static byte[] PackageZip(byte[] library, bool stored = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data, CompressionLevel level) { using var s = zip.CreateEntry(name, level).Open(); s.Write(data); }
            Add("LICENSE", Encoding.ASCII.GetBytes("a license"), CompressionLevel.Optimal);
            Add("runtimes/other-arm64/native/lib.so", Bytes(400_000, 3), CompressionLevel.Optimal);
            Add(Entry, library, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            Add("runtimes/third-x64/native/lib.dll", Bytes(300_000, 4), CompressionLevel.Optimal);
            Add("notices.txt", Bytes(20_000, 5), CompressionLevel.NoCompression);
        }
        return ms.ToArray();
    }

    static long CompressedSize(byte[] zip, string entry)
    {
        using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return z.GetEntry(entry)!.CompressedLength;
    }

    /// <summary>A small archive with the one library at its root, like the first source of the real runtime.</summary>
    static byte[] OwnZip(byte[] library)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("lib.so", CompressionLevel.Optimal).Open()) s.Write(library);
            using (var s = zip.CreateEntry("LICENSE", CompressionLevel.Optimal).Open()) s.Write(Encoding.ASCII.GetBytes("a license"));
        }
        return ms.ToArray();
    }

    /// <summary>A manifest with small stand-in files, and the bytes a source serves for it. The runtime comes from the package: the one
    /// entry, then the whole package. With <paramref name="own"/>, a small archive of its own is the first source.</summary>
    static (DepthModelManifest Manifest, Dictionary<string, byte[]> Files) Fixture(bool stored = false, bool own = false)
    {
        byte[] package = PackageZip(Library, stored);
        var files = new Dictionary<string, byte[]> { [Package] = package, ["model.onnx"] = Model };
        var file = new DownloadFile(Package, "http://localhost:9/none/" + Package, package.Length, Sha(package));
        var sources = new List<RuntimeSource> { new(file, Entry, Ranged: true, EntrySize: CompressedSize(package, Entry)), new(file, Entry) };
        if (own)
        {
            byte[] zip = OwnZip(Library);
            files[Own] = zip;
            sources.Insert(0, new RuntimeSource(new DownloadFile(Own, "http://localhost:9/none/" + Own, zip.Length, Sha(zip)), "lib.so"));
        }
        var manifest = new DepthModelManifest
        {
            RuntimeVersion = "9.9", ModelId = "test-model",
            Model = new ModelPackage(new DownloadFile("model.onnx", "http://localhost:9/none/model.onnx", Model.Length, Sha(Model)), "in", "out"),
            Runtimes = new[] { new RuntimePackage(Rid, sources, "lib.so", Library.Length, Sha(Library), OldArchive, OldEntry) },
        };
        return (manifest, files);
    }

    /// <summary>The manifest with other addresses: the small archive at one server, the package and the model at another.</summary>
    static DepthModelManifest At(DepthModelManifest m, string ownBase, string packageBase)
    {
        DownloadFile Move(DownloadFile f) => f with { Url = (f.Name == Own ? ownBase : packageBase) + f.Name };
        return new()
        {
            RuntimeVersion = m.RuntimeVersion, ModelId = m.ModelId, Model = m.Model with { File = Move(m.Model.File) },
            Runtimes = new[] { m.Runtimes[0] with { Sources = m.Runtimes[0].Sources.Select(s => s with { Archive = Move(s.Archive) }).ToList() } },
        };
    }

    static DepthModelManifest WithLibraryHash(DepthModelManifest m, string sha) => new()
    {
        RuntimeVersion = m.RuntimeVersion, ModelId = m.ModelId, Model = m.Model,
        Runtimes = new[] { m.Runtimes[0] with { LibrarySha256 = sha } },
    };

    /// <summary>A source over bytes in memory. It records every request and can break, slow down or alter what it serves.</summary>
    sealed class ScriptedSource(Dictionary<string, byte[]> files) : IDownloadSource
    {
        public List<(string Name, long Offset)> Requests { get; } = new();
        /// <summary>The first request for this file ends after this many bytes.</summary>
        public (string Name, int Bytes)? BreakOnce { get; set; }
        /// <summary>Every request starts at the beginning, whatever offset is asked.</summary>
        public bool NoResume { get; set; }
        /// <summary>A pause before every 16 KB, in ms.</summary>
        public int SlowMs { get; set; }

        public Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, long? length, CancellationToken cancel)
        {
            lock (Requests) Requests.Add((file.Name, offset));
            if (!files.TryGetValue(file.Name, out var data)) throw new FileNotFoundException("no such file at the source", file.Name);
            long start = NoResume ? 0 : offset;
            int end = length is { } n && !NoResume ? (int)Math.Min(data.Length, start + n) : data.Length;
            if (BreakOnce is { } b && b.Name == file.Name) { end = Math.Min(end, (int)start + b.Bytes); BreakOnce = null; }
            Stream s = new MemoryStream(data, (int)start, end - (int)start, writable: false);
            if (SlowMs > 0) s = new SlowStream(s, SlowMs);
            return Task.FromResult((s, start));
        }
    }

    sealed class SlowStream(Stream inner, int ms) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            await Task.Delay(ms, cancel).ConfigureAwait(false);
            return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 16384)], cancel).ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 16384));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    DepthModelManager Manager(DepthModelManifest manifest, IDownloadSource source, string? rid = Rid, int stallMs = 60_000)
        => new(Path.Combine(_root, "models"), manifest, rid, source) { StallTimeout = TimeSpan.FromMilliseconds(stallMs) };

    [Fact]
    public async Task Install_checks_the_files_and_reports_progress()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files);
        var m = Manager(manifest, src);
        Assert.Equal(DepthModelState.NotInstalled, m.Status.State);
        Assert.Equal(manifest.Runtimes[0].Sources[0].EntrySize + Model.Length, m.DownloadBytes);   // the one library, compressed, and the model
        Assert.True(manifest.Runtimes[0].Sources[0].EntrySize < Library.Length);
        var seen = new List<DepthModelStatus>();
        m.StatusChanged += () => { lock (seen) seen.Add(m.Status); };
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library.Length + Model.Length, m.Status.SizeOnDisk);
        Assert.Contains("9.9", m.Status.Version);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));   // also the part of the package is gone
        Assert.Equal(2, Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories).Length);
        Assert.Contains(seen, s => s.State == DepthModelState.Downloading && s.BytesTotal == m.DownloadBytes);
        Assert.Equal(m.DownloadBytes, seen.Where(s => s.State == DepthModelState.Downloading).Max(s => s.BytesDone));
        m.VerifyInstalled();
        // a second manager on the same folder finds the files; a second download does nothing
        var again = Manager(manifest, src);
        Assert.Equal(DepthModelState.Installed, again.Status.State);
        int requests = src.Requests.Count;
        await again.DownloadAsync();
        Assert.Equal(requests, src.Requests.Count);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("stored")]
    [InlineData("own archive")]
    [InlineData("old archive")]
    public async Task A_folder_source_installs_from_the_package_or_from_an_old_archive(string kind)
    {
        // the folder holds one archive of the runtime; the manifest names all of them
        var (manifest, files) = Fixture(stored: kind == "stored", own: true);
        string folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), files["model.onnx"]);
        if (kind == "old archive") File.WriteAllBytes(Path.Combine(folder, OldArchive), Tgz(OldEntry, Library));
        else if (kind == "own archive") File.WriteAllBytes(Path.Combine(folder, Own), files[Own]);
        else File.WriteAllBytes(Path.Combine(folder, Package), files[Package]);
        var m = Manager(manifest, new Sources.FolderSource(folder));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_old_archive_with_another_library_is_rejected()
    {
        var (manifest, files) = Fixture();
        string folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), files["model.onnx"]);
        File.WriteAllBytes(Path.Combine(folder, OldArchive), Tgz(OldEntry, Bytes(Library.Length, 7)));
        var m = Manager(manifest, new Sources.FolderSource(folder));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Contains("does not match its expected hash", m.Status.Reason);
        Assert.False(File.Exists(m.LibraryPath!));
    }

    [Fact]
    public async Task A_file_with_the_wrong_hash_is_rejected()
    {
        var (manifest, files) = Fixture();
        var bad = (byte[])Model.Clone(); bad[1000] ^= 0x40;
        files["model.onnx"] = bad;
        var src = new ScriptedSource(files);
        var m = Manager(manifest, src);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Contains("does not match its expected hash", m.Status.Reason);
        Assert.False(File.Exists(m.ModelPath));
        Assert.False(File.Exists(m.ModelPath + ".part"));                              // the rejected file is not kept
        Assert.Equal(2, src.Requests.Count(r => r.Name == "model.onnx"));               // fetched once more from the start, then given up
        Assert.All(src.Requests.Where(r => r.Name == "model.onnx"), r => Assert.Equal(0, r.Offset));
        Assert.Throws<DepthModelException>(m.VerifyInstalled);
        // the runtime was fine and is kept: a new try with a good model only fetches the model
        files["model.onnx"] = Model;
        src.Requests.Clear();
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.DoesNotContain(src.Requests, r => r.Name == Package);
    }

    [Fact]
    public async Task A_wrong_library_inside_a_good_archive_is_rejected()
    {
        var (manifest, files) = Fixture();
        var m = Manager(WithLibraryHash(manifest, Sha(Bytes(Library.Length, 7))), new ScriptedSource(files));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Contains("does not match its expected hash", m.Status.Reason);
        Assert.False(File.Exists(m.LibraryPath!));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_broken_connection_continues_from_the_part_received()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files) { BreakOnce = ("model.onnx", 120_000) };
        var m = Manager(manifest, src);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(new long[] { 0, 120_000 }, src.Requests.Where(r => r.Name == "model.onnx").Select(r => r.Offset));
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
    }

    [Fact]
    public async Task A_source_that_cannot_continue_starts_again()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files) { BreakOnce = ("model.onnx", 120_000), NoResume = true };
        var m = Manager(manifest, src);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
    }

    [Fact]
    public async Task A_missing_source_fails_with_a_reason()
    {
        var (manifest, _) = Fixture();
        var m = Manager(manifest, new ScriptedSource(new Dictionary<string, byte[]>()));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Equal("The runtime could not be downloaded from localhost. Try again later. (no such file at the source)", m.Status.Reason);
    }

    [Fact]
    public async Task Cancel_keeps_the_part_and_the_next_download_continues()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files) { SlowMs = 20 };
        var m = Manager(manifest, src);
        var run = m.DownloadAsync();
        Assert.Same(run, m.DownloadAsync());                                            // one download at a time
        string part = m.ModelPath + ".part";
        for (int i = 0; i < 400 && !(File.Exists(part) && new FileInfo(part).Length > 0); i++) await Task.Delay(10);
        m.CancelDownload();
        await run;
        Assert.Equal(DepthModelState.NotInstalled, m.Status.State);
        long kept = new FileInfo(part).Length;
        Assert.InRange(kept, 1, Model.Length - 1);
        src.SlowMs = 0; src.Requests.Clear();
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(new[] { ("model.onnx", kept) }, src.Requests);                      // the runtime is there already; the model continues
    }

    [Fact]
    public async Task Remove_deletes_the_files_also_during_a_download()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files);
        var m = Manager(manifest, src);
        await m.DownloadAsync();
        m.Remove();
        Assert.Equal(DepthModelState.NotInstalled, m.Status.State);
        Assert.False(Directory.Exists(m.Folder));
        src.SlowMs = 20;
        var run = m.DownloadAsync();
        for (int i = 0; i < 400 && m.Status.BytesDone == 0; i++) await Task.Delay(10);
        m.Remove();
        await run;
        Assert.Equal(DepthModelState.NotInstalled, m.Status.State);
        Assert.False(Directory.Exists(m.Folder));
    }

    [Fact]
    public async Task Damaged_installed_files_are_found_before_use()
    {
        var (manifest, files) = Fixture();
        var m = Manager(manifest, new ScriptedSource(files));
        await m.DownloadAsync();
        var bytes = File.ReadAllBytes(m.ModelPath); bytes[5] ^= 1;
        File.WriteAllBytes(m.ModelPath, bytes);                                          // same size, other content
        Assert.Equal(DepthModelState.Installed, Manager(manifest, new ScriptedSource(files)).Status.State);
        var e = Assert.Throws<DepthModelException>(m.VerifyInstalled);
        Assert.Contains("damaged", e.Message);
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Throws<DepthModelException>(() => OnnxDepthNetwork.Open(m));
    }

    [Fact]
    public async Task A_computer_without_a_runtime_reports_not_available()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files);
        var m = Manager(manifest, src, rid: "plan9-mips");
        Assert.Equal(DepthModelState.NotAvailable, m.Status.State);
        Assert.Contains("plan9-mips", m.Status.Reason);
        Assert.Equal(0, m.DownloadBytes);
        await m.DownloadAsync();
        Assert.Empty(src.Requests);
        Assert.Equal(DepthModelState.NotAvailable, m.Status.State);
        Assert.Throws<DepthModelException>(m.VerifyInstalled);
        Assert.Equal(DepthModelState.NotAvailable, Manager(manifest, src, rid: null).Status.State);
        m.Remove();
        Assert.Equal(DepthModelState.NotAvailable, m.Status.State);
    }

    [Fact]
    public void The_built_in_manifest_covers_six_platforms()
    {
        var d = DepthModelManifest.Default;
        Assert.Equal(new[] { "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64" }, d.Runtimes.Select(r => r.Rid).Order());
        foreach (var f in d.Runtimes.SelectMany(r => r.Sources).Select(x => x.Archive).Append(d.Model.File))
        {
            Assert.StartsWith("https://", f.Url);
            Assert.Equal(64, f.Sha256.Length);
            Assert.True(f.Size > 1_000_000);
        }
        Assert.Single(d.Model.Sources);
        Assert.All(d.Runtimes, r =>
        {
            Assert.Equal(64, r.LibrarySha256.Length);
            // the sources in order: the small archive of the platform, the one library out of the package, the whole package
            Assert.Equal(3, r.Sources.Count);
            var (own, entry, package) = (r.Sources[0], r.Sources[1], r.Sources[2]);
            Assert.Equal(($"onnxruntime-{d.RuntimeVersion}-{r.Rid}.zip", r.LibraryFile, false), (own.Archive.Name, own.Entry, own.Ranged));
            Assert.Equal("github.com", new Uri(own.Archive.Url).Host);
            Assert.InRange(own.DownloadSize, 1_000_000, r.LibrarySize);
            Assert.Equal(($"runtimes/{r.Rid}/native/{r.LibraryFile}", true), (entry.Entry, entry.Ranged));
            Assert.InRange(entry.DownloadSize, 1_000_000, r.LibrarySize);
            Assert.Equal((entry.Archive, entry.Entry, false), (package.Archive, package.Entry, package.Ranged));
            Assert.Equal("api.nuget.org", new Uri(package.Archive.Url).Host);
            Assert.Contains(d.RuntimeVersion, package.Archive.Name);
            Assert.StartsWith("onnxruntime-", r.LocalArchive);
            Assert.StartsWith(Path.GetFileNameWithoutExtension(r.LocalArchive!) + "/lib/", r.LocalArchiveEntry);
        });
        Assert.Single(d.Runtimes.Select(r => r.Sources[2].Archive).Distinct());          // one package for all platforms
        Assert.NotNull(d.RuntimeFor(DepthModelManifest.CurrentRid));                    // the computer the tests run on
        Assert.Null(d.RuntimeFor("freebsd-x64"));
    }

    /// <summary>The built-in manifest against a copy of the real package (<c>LUX_TEST_ORT_PACKAGE</c>, a file): every platform's entry is
    /// in the package with the sizes of the manifest, and the range reader finds it where the zip library finds it.</summary>
    [Fact]
    public async Task The_built_in_manifest_matches_the_real_package()
    {
        string path = Environment.GetEnvironmentVariable("LUX_TEST_ORT_PACKAGE") ?? "";
        if (path.Length == 0 || !File.Exists(path)) { _output.WriteLine("LUX_TEST_ORT_PACKAGE names no copy of the runtime package: skipped"); return; }
        var d = DepthModelManifest.Default;
        Assert.Equal(d.Runtimes[0].Sources[1].Archive.Size, new FileInfo(path).Length);
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        using var ranges = File.OpenRead(path);
        long asked = 0;
        Task<byte[]> Read(long offset, int length)
        {
            var b = new byte[length]; ranges.Position = offset; ranges.ReadExactly(b); asked += length;
            return Task.FromResult(b);
        }
        foreach (var r in d.Runtimes)
        {
            var source = r.Sources[1];
            var e = zip.GetEntry(source.Entry);
            Assert.NotNull(e);
            Assert.Equal(source.EntrySize, e.CompressedLength);
            Assert.Equal(r.LibrarySize, e.Length);
            var found = await ZipRange.FindAsync(Read, ranges.Length, source.Entry);
            Assert.Equal((source.EntrySize, r.LibrarySize, e.Crc32, 8), (found.CompressedSize, found.Size, found.Crc32, found.Method));
            // the first bytes of the entry, unpacked, are the first bytes the zip library gives
            var head = new byte[4096]; ranges.Position = found.DataOffset;
            using var inflate = new DeflateStream(ranges, CompressionMode.Decompress, leaveOpen: true);
            inflate.ReadExactly(head);
            var expect = new byte[4096];
            using (var s = e.Open()) s.ReadExactly(expect);
            Assert.Equal(expect, head);
        }
        Assert.InRange(asked, 1, 6 * 70_000);                                            // the directory and six headers, not the package
        _output.WriteLine($"{d.Runtimes.Count} entries found; {asked} bytes read for the zip directory and the headers");
    }

    /// <summary>The built-in manifest against copies of the first source's archives (<c>LUX_TEST_ORT_ARCHIVES</c>, a folder): every
    /// platform's archive has the size and the SHA-256 of the manifest, and the library in it is the expected one.</summary>
    [Fact]
    public void The_built_in_manifest_matches_the_archives_of_the_first_source()
    {
        string folder = Environment.GetEnvironmentVariable("LUX_TEST_ORT_ARCHIVES") ?? "";
        if (folder.Length == 0 || !Directory.Exists(folder)) { _output.WriteLine("LUX_TEST_ORT_ARCHIVES names no folder with the runtime archives: skipped"); return; }
        foreach (var r in DepthModelManifest.Default.Runtimes)
        {
            var source = r.Sources[0];
            string path = Path.Combine(folder, source.Archive.Name);
            Assert.True(File.Exists(path), path);
            Assert.Equal(source.Archive.Size, new FileInfo(path).Length);
            Assert.Equal(source.Archive.Sha256, DepthModelManager.Sha256(path));
            using var zip = ZipFile.OpenRead(path);
            var e = zip.GetEntry(source.Entry);
            Assert.NotNull(e);
            Assert.Equal(r.LibrarySize, e.Length);
            using var s = e.Open();
            Assert.Equal(r.LibrarySha256, Convert.ToHexStringLower(SHA256.HashData(s)));
            _output.WriteLine($"{source.Archive.Name}: {source.Archive.Size} bytes, {source.Entry} is the expected library");
        }
    }

    // ---- the range reader ----

    [Fact]
    public async Task The_range_reader_reads_a_Zip64_file_and_a_file_with_a_comment()
    {
        byte[] data = Bytes(5000, 11);
        foreach (int commentLength in new[] { 0, 40_000 })
        {
            byte[] zip = Zip64(("first.bin", Bytes(3000, 10)), ("dir/wanted.bin", data), commentLength);
            int requests = 0;
            Task<byte[]> Read(long offset, int length) { requests++; return Task.FromResult(zip.AsSpan((int)offset, length).ToArray()); }
            var e = await ZipRange.FindAsync(Read, zip.Length, "dir/wanted.bin");
            Assert.Equal((0, (long)data.Length, (long)data.Length), (e.Method, e.Size, e.CompressedSize));
            Assert.Equal(data, zip.AsSpan((int)e.DataOffset, data.Length).ToArray());
            using var to = new MemoryStream();
            Assert.True(ZipRange.Unpack(e, new MemoryStream(zip, (int)e.DataOffset, data.Length), to));
            Assert.Equal(data, to.ToArray());
            Assert.Equal(commentLength == 0 ? 2 : 3, requests);                           // the tail (a larger one for the comment) and the header
            await Assert.ThrowsAsync<ZipStructureException>(() => ZipRange.FindAsync(Read, zip.Length, "dir/missing.bin"));
        }
        // not a zip file; a damaged entry
        byte[] noise = Bytes(100_000, 12);
        await Assert.ThrowsAsync<ZipStructureException>(() => ZipRange.FindAsync((o, n) => Task.FromResult(noise.AsSpan((int)o, n).ToArray()), noise.Length, "x"));
        byte[] package = PackageZip(Library);
        var entry = await ZipRange.FindAsync((o, n) => Task.FromResult(package.AsSpan((int)o, n).ToArray()), package.Length, Entry);
        Assert.Equal(8, entry.Method);
        byte[] packed = package.AsSpan((int)entry.DataOffset, (int)entry.CompressedSize).ToArray();
        Assert.True(ZipRange.Unpack(entry, new MemoryStream(packed), new MemoryStream()));
        packed[packed.Length / 2] ^= 0x10;
        Assert.False(ZipRange.Unpack(entry, new MemoryStream(packed), new MemoryStream()));
    }

    /// <summary>A zip file in the Zip64 form: stored entries whose sizes and positions are in the Zip64 extra field, then the Zip64 end
    /// record, its locator and the end record with a comment.</summary>
    static byte[] Zip64((string Name, byte[] Data) a, (string Name, byte[] Data) b, int commentLength)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var directory = new MemoryStream(); var dw = new BinaryWriter(directory);
        foreach (var (name, data) in new[] { a, b })
        {
            byte[] n = Encoding.UTF8.GetBytes(name); uint crc = Crc(data); long at = ms.Position;
            w.Write(0x04034b50u); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(crc);
            w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)n.Length); w.Write((ushort)20);
            w.Write(n); w.Write((ushort)1); w.Write((ushort)16); w.Write((long)data.Length); w.Write((long)data.Length);
            w.Write(data);
            dw.Write(0x02014b50u); dw.Write((ushort)45); dw.Write((ushort)45); dw.Write((ushort)0); dw.Write((ushort)0); dw.Write(0u); dw.Write(crc);
            dw.Write(0xFFFFFFFFu); dw.Write(0xFFFFFFFFu); dw.Write((ushort)n.Length); dw.Write((ushort)32); dw.Write((ushort)0);
            dw.Write((ushort)0); dw.Write((ushort)0); dw.Write(0u); dw.Write(0xFFFFFFFFu);
            dw.Write(n); dw.Write((ushort)0x7075); dw.Write((ushort)0);                  // another extra field first
            dw.Write((ushort)1); dw.Write((ushort)24); dw.Write((long)data.Length); dw.Write((long)data.Length); dw.Write(at);
        }
        long directoryAt = ms.Position; w.Write(directory.ToArray());
        long end64 = ms.Position;
        w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write(2L); w.Write(2L); w.Write(directory.Length); w.Write(directoryAt);
        w.Write(0x07064b50u); w.Write(0u); w.Write(end64); w.Write(1u);
        w.Write(0x06054b50u); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)commentLength); w.Write(new byte[commentLength]);
        w.Flush();
        return ms.ToArray();
    }

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in data) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return ~c;
    }

    // ---- a local HTTP server ----

    /// <summary>A small HTTP server on this computer. It serves files with or without byte ranges, records every request, and can
    /// answer "not found", break a connection or stop sending.</summary>
    sealed class LocalServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Dictionary<string, byte[]> _files;
        long _sent;

        public LocalServer(Dictionary<string, byte[]> files)
        {
            _files = files;
            _listener.Start();
            _ = Task.Run(Accept);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        /// <summary>The same server under another name, for a test that needs two server names.</summary>
        public string LocalhostUrl => $"http://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        public Sources.HttpSource Source => new(Url);
        /// <summary>Every request: the file, the first byte and the number of bytes asked (null = no range), and the header names.</summary>
        public List<(string Name, long? From, long? Count, string[] Headers)> Requests { get; } = new();
        public List<string> UserAgents { get; } = new();
        /// <summary>The body bytes sent, of all answers.</summary>
        public long BytesSent => Interlocked.Read(ref _sent);
        /// <summary>Answer every request with the whole file and status 200.</summary>
        public bool NoRanges { get; set; }
        /// <summary>Answer every request with status 404.</summary>
        public bool NotFound { get; set; }
        /// <summary>The first answer of at least 20,000 bytes closes the connection after this many bytes.</summary>
        public int? BreakOnceAfter { get; set; }
        /// <summary>Every answer of at least 20,000 bytes stops after this many bytes and keeps the connection open.</summary>
        public int? StallAfter { get; set; }

        async Task Accept()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        async Task Serve(TcpClient client)
        {
            try
            {
                using var _ = client;
                var stream = client.GetStream();
                var head = new StringBuilder(); var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    head.Append((char)one[0]);
                }
                string[] lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                string name = lines[0].Split(' ')[1].TrimStart('/');
                var headers = lines.Skip(1).Select(l => (Name: l[..l.IndexOf(':')], Value: l[(l.IndexOf(':') + 1)..].Trim())).ToList();
                string? range = headers.FirstOrDefault(h => h.Name.Equals("Range", StringComparison.OrdinalIgnoreCase)).Value;
                long? from = null, count = null;
                bool known = _files.TryGetValue(name, out var data) && !NotFound;
                if (range is not null)
                {
                    string[] ends = range["bytes=".Length..].Split('-');
                    from = long.Parse(ends[0]);
                    if (ends[1].Length > 0) count = long.Parse(ends[1]) - from + 1;
                }
                lock (Requests)
                {
                    Requests.Add((name, from, count, headers.Select(h => h.Name).ToArray()));
                    UserAgents.AddRange(headers.Where(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
                }
                async Task Send(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
                if (!known) { await Send("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
                long start = 0, length = data!.Length;
                if (range is not null && !NoRanges)
                {
                    start = from!.Value; length = Math.Min(count ?? long.MaxValue, data.Length - start);
                    await Send($"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{start + length - 1}/{data.Length}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
                }
                else await Send($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
                long send = length; bool stall = false;
                if (length >= 20_000)
                {
                    if (BreakOnceAfter is { } b) { send = b; BreakOnceAfter = null; }
                    else if (StallAfter is { } s) { send = s; stall = true; }
                }
                for (long done = 0; done < send;)
                {
                    int n = (int)Math.Min(8192, send - done);
                    await stream.WriteAsync(data.AsMemory((int)(start + done), n), _stop.Token);
                    done += n; Interlocked.Add(ref _sent, n);
                }
                await stream.FlushAsync(_stop.Token);
                if (stall) await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    [Fact]
    public async Task Over_HTTP_only_the_one_library_of_the_package_is_downloaded()
    {
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files);
        var m = Manager(manifest, server.Source);
        var seen = new List<DepthModelStatus>();
        m.StatusChanged += () => { lock (seen) seen.Add(m.Status); };
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
        m.VerifyInstalled();
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));

        // the package: the end of the file with the directory, the header of the entry, the entry. Far less than the package.
        var package = server.Requests.Where(r => r.Name == Package).ToList();
        long entrySize = manifest.Runtimes[0].Sources[0].EntrySize;
        Assert.Equal(3, package.Count);
        Assert.All(package, r => Assert.NotNull(r.Count));
        Assert.Equal(entrySize, package[2].Count);
        long packageBytes = server.BytesSent - Model.Length;
        Assert.InRange(packageBytes, entrySize, entrySize + 20_000);
        Assert.True(packageBytes < files[Package].Length / 10, $"{packageBytes} bytes of a package of {files[Package].Length}");
        // the model: one request for the whole file
        Assert.Equal(new[] { ("model.onnx", (long?)null, (long?)null) }, server.Requests.Where(r => r.Name == "model.onnx").Select(r => (r.Name, r.From, r.Count)));
        // a request carries the address, the byte range and the client name, and nothing else
        Assert.All(server.Requests, r => Assert.Empty(r.Headers.Except(new[] { "Host", "Range", "User-Agent" }, StringComparer.OrdinalIgnoreCase)));
        Assert.All(server.UserAgents, a => Assert.Equal("Lux", a));
        Assert.Equal(server.Requests.Count, server.UserAgents.Count);
        // the progress counts the real download
        Assert.Equal(entrySize + Model.Length, m.DownloadBytes);
        Assert.All(seen.Where(s => s.State == DepthModelState.Downloading), s => Assert.Equal(m.DownloadBytes, s.BytesTotal));
        Assert.Equal(m.DownloadBytes, seen.Where(s => s.State == DepthModelState.Downloading).Max(s => s.BytesDone));
    }

    [Fact]
    public async Task Over_HTTP_a_broken_connection_continues_in_the_middle_of_the_entry()
    {
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files) { BreakOnceAfter = 12_000 };
        var m = Manager(manifest, server.Source);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        long entrySize = manifest.Runtimes[0].Sources[0].EntrySize;
        var entry = server.Requests.Where(r => r.Name == Package && r.Count >= 20_000).ToList();
        Assert.Equal(2, entry.Count);                                                    // the entry, and the rest of it
        Assert.Equal(entrySize, entry[0].Count);
        Assert.Equal((entry[0].From + 12_000, (long?)(entrySize - 12_000)), (entry[1].From, entry[1].Count));
        Assert.InRange(server.BytesSent - Model.Length, entrySize, entrySize + 30_000);  // no byte of the entry twice
    }

    [Fact]
    public async Task Over_HTTP_a_stopped_download_of_the_entry_continues()
    {
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files) { StallAfter = 9_000 };
        var m = Manager(manifest, server.Source, stallMs: 400);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await m.DownloadAsync();
        // a connection that sends nothing more: the download fails after the time limit, not after the tries of a broken connection
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Equal("The runtime could not be downloaded from 127.0.0.1. Try again later. (no data arrived for 0.4 seconds)", m.Status.Reason);
        Assert.InRange(clock.ElapsedMilliseconds, 300, 5000);
        string part = Path.Combine(m.Folder, $"{Package}.{Rid}.part");
        Assert.Equal(9_000, new FileInfo(part).Length);                                   // the part stays
        Assert.Single(server.Requests, r => r.Name == Package && r.Count >= 20_000);
        // the next download continues from the part
        server.StallAfter = null;
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));   // also the part of the whole package
        var entry = server.Requests.Where(r => r.Name == Package && r.Count >= 20_000).ToList();
        Assert.Equal(2, entry.Count);
        Assert.Equal((entry[0].From + 9_000, entry[0].Count - 9_000), (entry[1].From, entry[1].Count));
    }

    [Fact]
    public async Task Over_HTTP_a_server_without_byte_ranges_gives_the_whole_package()
    {
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files) { NoRanges = true };
        var m = Manager(manifest, server.Source);
        var seen = new List<DepthModelStatus>();
        m.StatusChanged += () => { lock (seen) seen.Add(m.Status); };
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories).Length);
        var package = server.Requests.Where(r => r.Name == Package).ToList();
        Assert.Equal(2, package.Count);                                                  // one range request, then the whole file
        Assert.NotNull(package[0].Count);
        Assert.Null(package[1].From);
        // the progress then counts the whole package
        Assert.Equal(files[Package].Length + Model.Length, seen.Where(s => s.State == DepthModelState.Downloading).Max(s => s.BytesTotal));
    }

    [Fact]
    public async Task Over_HTTP_a_package_that_is_not_the_expected_one_is_rejected()
    {
        // another library under the same name: the zip file is good, the library is not the expected one
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files);
        var m = Manager(WithLibraryHash(manifest, Sha(Bytes(Library.Length, 7))), server.Source);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Contains("does not match its expected hash", m.Status.Reason);
        Assert.False(File.Exists(m.LibraryPath!));
        Assert.Empty(Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories));
        Assert.Equal(2, server.Requests.Count(r => r.Name == Package && r.Count >= 20_000));   // fetched once more, then given up
        Assert.DoesNotContain(server.Requests, r => r.Name == "model.onnx");

        // a file that is not a zip file: the whole file is downloaded and fails its hash
        var noise = Bytes(files[Package].Length, 9);
        using var other = new LocalServer(new Dictionary<string, byte[]> { [Package] = noise, ["model.onnx"] = Model });
        var m2 = Manager(manifest, other.Source);
        await m2.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m2.Status.State);
        Assert.Contains("does not match its expected hash", m2.Status.Reason);
        Assert.Empty(Directory.GetFiles(m2.Folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Over_HTTP_a_missing_file_and_a_missing_server_fail_with_a_clear_reason()
    {
        var (manifest, files) = Fixture();
        using var server = new LocalServer(files) { NotFound = true };
        var m = Manager(manifest, server.Source);
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Equal("The runtime could not be downloaded from 127.0.0.1. Try again later. (HTTP 404)", m.Status.Reason);
        Assert.Equal(2, server.Requests.Count);                                          // once for each source: "not found" is not tried again

        // the runtime is there, the model is not
        server.NotFound = false;
        files.Remove("model.onnx");
        await m.DownloadAsync();
        Assert.Equal("The model could not be downloaded from 127.0.0.1. Try again later. (HTTP 404)", m.Status.Reason);
        Assert.True(File.Exists(m.LibraryPath!));

        // no server at the address
        string url = server.Url;
        server.Dispose();
        m.Remove();
        var gone = Manager(manifest, new Sources.HttpSource(url));
        await gone.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, gone.Status.State);
        Assert.StartsWith("The runtime could not be downloaded from 127.0.0.1. Try again later. (", gone.Status.Reason);
        Assert.DoesNotContain("HTTP", gone.Status.Reason);
    }

    // ---- the order of the sources ----

    /// <summary>Two servers: the small archive of the runtime at the first, the package and the model at the second.</summary>
    (LocalServer Own, LocalServer Package, DepthModelManifest Manifest, Dictionary<string, byte[]> Files) TwoServers()
    {
        var (manifest, files) = Fixture(own: true);
        var own = new LocalServer(new Dictionary<string, byte[]> { [Own] = files[Own] });
        var package = new LocalServer(new Dictionary<string, byte[]> { [Package] = files[Package], ["model.onnx"] = files["model.onnx"] });
        return (own, package, At(manifest, own.Url, package.LocalhostUrl), files);
    }

    [Fact]
    public async Task The_first_source_is_used_when_it_works()
    {
        var (own, package, manifest, files) = TwoServers();
        using var _1 = own; using var _2 = package;
        var m = Manager(manifest, new Sources.HttpSource(null));
        Assert.Equal(files[Own].Length + Model.Length, m.DownloadBytes);                  // the size of the first source, and the model
        var seen = new List<DepthModelStatus>();
        m.StatusChanged += () => { lock (seen) seen.Add(m.Status); };
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        m.VerifyInstalled();
        Assert.Equal(2, Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(new[] { (Own, (long?)null) }, own.Requests.Select(r => (r.Name, r.From)));   // one request for the whole small archive
        Assert.Equal(new[] { "model.onnx" }, package.Requests.Select(r => r.Name));        // the package is never asked for
        Assert.All(seen.Where(s => s.State == DepthModelState.Downloading), s => Assert.Equal(m.DownloadBytes, s.BytesTotal));
        Assert.Equal(m.DownloadBytes, seen.Where(s => s.State == DepthModelState.Downloading).Max(s => s.BytesDone));
    }

    [Fact]
    public async Task A_first_source_without_the_file_gives_the_entry_of_the_package()
    {
        var (own, package, manifest, files) = TwoServers();
        using var _1 = own; using var _2 = package;
        own.NotFound = true;
        var m = Manager(manifest, new Sources.HttpSource(null));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        m.VerifyInstalled();
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));
        Assert.Single(own.Requests);
        // the second source: the end of the package, the header of the entry, the entry
        var asked = package.Requests.Where(r => r.Name == Package).ToList();
        Assert.Equal(3, asked.Count);
        Assert.All(asked, r => Assert.NotNull(r.Count));
        Assert.True(package.BytesSent - Model.Length < files[Package].Length / 10);

        // the same with a first server that is not there
        m.Remove();
        string gone = own.Url;
        own.Dispose();
        package.Requests.Clear();
        var m2 = Manager(At(manifest, gone, package.LocalhostUrl), new Sources.HttpSource(null));
        await m2.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m2.Status.State);
        Assert.Equal(3, package.Requests.Count(r => r.Name == Package));
    }

    [Fact]
    public async Task A_first_source_with_another_archive_is_rejected_and_the_next_source_is_used()
    {
        var (manifest, files) = Fixture(own: true);
        var other = (byte[])files[Own].Clone(); other[other.Length / 2] ^= 0x04;          // the same size, another hash
        using var own = new LocalServer(new Dictionary<string, byte[]> { [Own] = other });
        using var package = new LocalServer(new Dictionary<string, byte[]> { [Package] = files[Package], ["model.onnx"] = Model });
        var m = Manager(At(manifest, own.Url, package.LocalhostUrl), new Sources.HttpSource(null));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        m.VerifyInstalled();
        Assert.Equal(2, own.Requests.Count);                                              // fetched once more from the start, then given up
        Assert.Equal(3, package.Requests.Count(r => r.Name == Package));
        Assert.Equal(2, Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories).Length);   // the rejected archive is not kept
    }

    [Fact]
    public async Task When_every_source_fails_the_reason_names_the_servers()
    {
        var (own, package, manifest, files) = TwoServers();
        using var _1 = own; using var _2 = package;
        own.NotFound = true; package.NotFound = true;
        var m = Manager(manifest, new Sources.HttpSource(null));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.Equal("The runtime could not be downloaded from 127.0.0.1 or localhost. Try again later. (127.0.0.1: HTTP 404; localhost: HTTP 404)", m.Status.Reason);
        Assert.Single(own.Requests);
        Assert.Equal(2, package.Requests.Count);                                          // the entry of the package, then the whole package
        Assert.Empty(Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories));

        // a first source with a wrong archive and a second one that stops sending
        var bad = new Dictionary<string, byte[]> { [Own] = Bytes(files[Own].Length, 21) };
        using var wrong = new LocalServer(bad);
        package.NotFound = false; package.StallAfter = 5_000;
        var m2 = Manager(At(manifest, wrong.Url, package.LocalhostUrl), new Sources.HttpSource(null), stallMs: 300);
        await m2.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m2.Status.State);
        Assert.Equal("The runtime could not be downloaded from 127.0.0.1 or localhost. Try again later. "
                     + $"(127.0.0.1: {Own} does not match its expected hash. The file was rejected; localhost: no data arrived for 0.3 seconds)", m2.Status.Reason);
    }

    [Fact]
    public async Task A_URL_in_place_of_the_sources_serves_the_small_archive_first()
    {
        var (manifest, files) = Fixture(own: true);
        using var server = new LocalServer(files);
        var m = Manager(manifest, server.Source);                                         // every file under its name at one address
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(new[] { Own, "model.onnx" }, server.Requests.Select(r => r.Name));
        // without the small archive there, the package
        m.Remove();
        files.Remove(Own); server.Requests.Clear();
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(new[] { Own, Package, Package, Package, "model.onnx" }, server.Requests.Select(r => r.Name));
    }
}
