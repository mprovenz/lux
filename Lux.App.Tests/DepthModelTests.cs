using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lux.DepthModel;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// The download manager of the refined depth files, against a scripted local source: a normal install, a file with the wrong hash, a
/// connection that breaks (the download continues from the part received), a source that cannot continue, cancel, remove, damaged
/// installed files, and a computer with no runtime. Nothing here reads from the network.
/// </summary>
public sealed class DepthModelTests : IDisposable
{
    readonly string _root = Path.Combine(Paths.AppData("depth-model-tests"), Guid.NewGuid().ToString("N")[..8]);
    public DepthModelTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    const string Rid = "test-x64";
    static readonly byte[] Library = Bytes(60_000, 1), Model = Bytes(300_000, 2);

    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

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

    static byte[] Zip(string entry, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open();
            s.Write(data);
        }
        return ms.ToArray();
    }

    /// <summary>A manifest with small stand-in files, and the bytes a source serves for it.</summary>
    static (DepthModelManifest Manifest, Dictionary<string, byte[]> Files) Fixture(bool zip = false)
    {
        const string entry = "rt/lib/lib.so.1";
        byte[] archive = zip ? Zip(entry, Library) : Tgz(entry, Library);
        string name = zip ? "runtime.zip" : "runtime.tgz";
        var manifest = new DepthModelManifest
        {
            RuntimeVersion = "9.9", ModelId = "test-model",
            Model = new ModelPackage(new DownloadFile("model.onnx", "http://localhost:9/none/model.onnx", Model.Length, Sha(Model)), "in", "out"),
            Runtimes = new[] { new RuntimePackage(Rid, new DownloadFile(name, "http://localhost:9/none/" + name, archive.Length, Sha(archive)), entry, "lib.so", Library.Length, Sha(Library)) },
        };
        return (manifest, new Dictionary<string, byte[]> { [name] = archive, ["model.onnx"] = Model });
    }

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

        public Task<(Stream Stream, long Offset)> OpenAsync(DownloadFile file, long offset, CancellationToken cancel)
        {
            lock (Requests) Requests.Add((file.Name, offset));
            if (!files.TryGetValue(file.Name, out var data)) throw new FileNotFoundException("no such file at the source", file.Name);
            long start = NoResume ? 0 : offset;
            int end = data.Length;
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

    DepthModelManager Manager(DepthModelManifest manifest, IDownloadSource source, string? rid = Rid) => new(Path.Combine(_root, "models"), manifest, rid, source);

    [Fact]
    public async Task Install_checks_the_files_and_reports_progress()
    {
        var (manifest, files) = Fixture();
        var src = new ScriptedSource(files);
        var m = Manager(manifest, src);
        Assert.Equal(DepthModelState.NotInstalled, m.Status.State);
        Assert.Equal(files["runtime.tgz"].Length + Model.Length, m.DownloadBytes);
        var seen = new List<DepthModelStatus>();
        m.StatusChanged += () => { lock (seen) seen.Add(m.Status); };
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library.Length + Model.Length, m.Status.SizeOnDisk);
        Assert.Contains("9.9", m.Status.Version);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
        Assert.Equal(Model, File.ReadAllBytes(m.ModelPath));
        Assert.Empty(Directory.GetFiles(m.Folder, "*.part", SearchOption.AllDirectories));   // also the archive is gone
        Assert.False(File.Exists(Path.Combine(m.Folder, "runtime.tgz")));
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

    [Fact]
    public async Task A_zip_archive_from_a_folder_source_installs()
    {
        var (manifest, files) = Fixture(zip: true);
        string folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in files) File.WriteAllBytes(Path.Combine(folder, name), data);
        var m = Manager(manifest, new Sources.FolderSource(folder));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Installed, m.Status.State);
        Assert.Equal(Library, File.ReadAllBytes(m.LibraryPath!));
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
        Assert.DoesNotContain(src.Requests, r => r.Name == "runtime.tgz");
    }

    [Fact]
    public async Task A_wrong_library_inside_a_good_archive_is_rejected()
    {
        var (manifest, files) = Fixture();
        var other = new DepthModelManifest
        {
            RuntimeVersion = manifest.RuntimeVersion, ModelId = manifest.ModelId, Model = manifest.Model,
            Runtimes = new[] { manifest.Runtimes[0] with { LibrarySha256 = Sha(Bytes(Library.Length, 7)) } },
        };
        var m = Manager(other, new ScriptedSource(files));
        await m.DownloadAsync();
        Assert.Equal(DepthModelState.Failed, m.Status.State);
        Assert.False(File.Exists(m.LibraryPath!));
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
        Assert.StartsWith("The download failed", m.Status.Reason);
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
        foreach (var f in d.Runtimes.Select(r => r.Archive).Append(d.Model.File))
        {
            Assert.StartsWith("https://", f.Url);
            Assert.Equal(64, f.Sha256.Length);
            Assert.True(f.Size > 1_000_000);
        }
        Assert.All(d.Runtimes, r => { Assert.Equal(64, r.LibrarySha256.Length); Assert.Contains(d.RuntimeVersion, r.Archive.Name); Assert.Contains("onnxruntime", r.Entry); });
        Assert.NotNull(d.RuntimeFor(DepthModelManifest.CurrentRid));                    // the computer the tests run on
        Assert.Null(d.RuntimeFor("freebsd-x64"));
    }
}
