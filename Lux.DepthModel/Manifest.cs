using System.Runtime.InteropServices;

namespace Lux.DepthModel;

/// <summary>One file to download: its name, where it comes from, its size and its SHA-256.</summary>
public sealed record DownloadFile(string Name, string Url, long Size, string Sha256);

/// <summary>One place the runtime library of a platform comes from: an archive and the library inside it.</summary>
/// <param name="Archive">A zip file with a fixed size and SHA-256.</param>
/// <param name="Entry">The path of the library inside the archive.</param>
/// <param name="Ranged">False: Lux downloads the whole archive. True: the archive is large and holds more than the library, so Lux
/// downloads only the library, with byte range requests.</param>
/// <param name="EntrySize">The size of the library inside the archive, compressed. Used when <paramref name="Ranged"/> is true.</param>
public sealed record RuntimeSource(DownloadFile Archive, string Entry, bool Ranged = false, long EntrySize = 0)
{
    /// <summary>The bytes Lux downloads from this source.</summary>
    public long DownloadSize => Ranged ? EntrySize : Archive.Size;
}

/// <summary>The inference runtime for one platform: the one library that Lux uses and the places it comes from.</summary>
/// <param name="Rid">The platform: operating system and processor (linux-x64, win-arm64, …).</param>
/// <param name="Sources">The sources, in the order Lux tries them. Every source holds the same library.</param>
/// <param name="LibraryFile">The file name the library is stored under.</param>
/// <param name="LocalArchive">The name of another archive (<c>.tgz</c> or <c>.zip</c>) with the same library. Lux accepts it from a local
/// source folder that holds no archive of <paramref name="Sources"/>.</param>
/// <param name="LocalArchiveEntry">The path of the library inside <paramref name="LocalArchive"/>.</param>
public sealed record RuntimePackage(string Rid, IReadOnlyList<RuntimeSource> Sources, string LibraryFile, long LibrarySize, string LibrarySha256,
                                    string? LocalArchive = null, string? LocalArchiveEntry = null);

/// <summary>The depth network: one ONNX file, and the names of its input and output.</summary>
public sealed record ModelPackage(DownloadFile File, string Input, string Output)
{
    /// <summary>More addresses of the same file. Lux tries them in this order when the address of <see cref="File"/> fails.</summary>
    public IReadOnlyList<string> OtherUrls { get; init; } = Array.Empty<string>();

    /// <summary>The file at each of its addresses, in the order Lux tries them.</summary>
    public IEnumerable<DownloadFile> Sources => OtherUrls.Select(url => File with { Url = url }).Prepend(File);
}

/// <summary>
/// The files the refined depth feature downloads: the depth network and, per platform, the runtime that runs it. Every file has a fixed
/// size and SHA-256. A file that does not match is rejected.
/// </summary>
public sealed class DepthModelManifest
{
    /// <summary>The environment variable that replaces the download source: a folder or a URL that holds the files under their
    /// <see cref="DownloadFile.Name"/>. A folder needs one archive of the runtime: an archive of
    /// <see cref="RuntimePackage.Sources"/> or <see cref="RuntimePackage.LocalArchive"/>. The sizes and hashes stay the same.</summary>
    public const string SourceVariable = "LUX_DEPTH_MODEL_SOURCE";

    public const string ModelName = "Depth Anything V2 Small", ModelLicense = "Apache-2.0";
    public const string RuntimeName = "ONNX Runtime", RuntimeLicense = "MIT";

    public required string RuntimeVersion { get; init; }
    /// <summary>A short name of the model and its revision. It is recorded with stored refined depth.</summary>
    public required string ModelId { get; init; }
    public required ModelPackage Model { get; init; }
    public required IReadOnlyList<RuntimePackage> Runtimes { get; init; }

    /// <summary>The platform this process runs on, as a runtime identifier; null for a platform Lux has no name for.</summary>
    public static string? CurrentRid
    {
        get
        {
            string? os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
            string? arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
            return os is null || arch is null ? null : os + "-" + arch;
        }
    }

    public RuntimePackage? RuntimeFor(string? rid) => rid is null ? null : Runtimes.FirstOrDefault(r => r.Rid == rid);

    const string OrtVersion = "1.23.2";
    const string ModelRevision = "4472b7362082ad9968fee890ca0f1e5aca36b93d";

    /// <summary>The runtime package: one zip file with the libraries of all platforms.</summary>
    static readonly DownloadFile OrtPackage = new(
        "microsoft.ml.onnxruntime." + OrtVersion + ".nupkg",
        "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/" + OrtVersion + "/microsoft.ml.onnxruntime." + OrtVersion + ".nupkg",
        123371574, "25fe172f7fcdf34f2b5b02c8b997f6b88abc282956849d4af4335af23d0c4e4a");

    /// <summary>The release files of the Lux project: per platform one small zip file with the library at its root.</summary>
    const string OwnBase = "https://github.com/mprovenz/lux-depth-support/releases/download/v1/";

    /// <param name="ownSize">The size of the Lux project's archive for the platform.</param>
    /// <param name="entrySize">The size of the library inside the package, compressed.</param>
    /// <param name="localName">The platform's name in the file name of the separate ONNX Runtime release archive.</param>
    static RuntimePackage Ort(string rid, string library, long ownSize, string ownSha, long entrySize, long librarySize, string librarySha,
                              string localName, string localLibrary)
    {
        string own = "onnxruntime-" + OrtVersion + "-" + rid + ".zip", local = "onnxruntime-" + localName + "-" + OrtVersion;
        string entry = "runtimes/" + rid + "/native/" + library;
        var sources = new[]
        {
            new RuntimeSource(new DownloadFile(own, OwnBase + own, ownSize, ownSha), library),   // the Lux project's archive
            new RuntimeSource(OrtPackage, entry, Ranged: true, EntrySize: entrySize),             // the one library out of the package
            new RuntimeSource(OrtPackage, entry),                                                 // the whole package
        };
        return new(rid, sources, library, librarySize, librarySha,
                   local + (rid.StartsWith("win", StringComparison.Ordinal) ? ".zip" : ".tgz"), local + "/lib/" + localLibrary);
    }

    /// <summary>
    /// The files Lux uses. The runtime is ONNX Runtime 1.23.2, the newest version published for all six platforms (later versions have
    /// no build for macOS on Intel processors). Its first source is the release files of the Lux project
    /// (github.com/mprovenz/lux-depth-support): per platform one small archive with the unchanged library of the
    /// <c>Microsoft.ML.OnnxRuntime</c> package. Its second source is that package on nuget.org. The package holds the libraries of all
    /// platforms; Lux downloads only the one library of this computer, or the whole package when the server cannot send a part of it.
    /// The model is Depth Anything V2 Small as an ONNX file, from the `onnx-community/depth-anything-v2-small` repository on Hugging
    /// Face at a fixed revision.
    /// </summary>
    public static DepthModelManifest Default { get; } = new()
    {
        RuntimeVersion = OrtVersion,
        ModelId = "depth-anything-v2-small@" + ModelRevision[..8],
        Model = new ModelPackage(
            new DownloadFile("depth-anything-v2-small.onnx",
                             "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/" + ModelRevision + "/onnx/model.onnx",
                             99060839, "afb6a5c28f3b6bf1618c6e43f02073ef9dfdc70e937502d51603e57b0a1df10c"),
            "pixel_values", "predicted_depth"),
        Runtimes = new[]
        {
            Ort("linux-x64", "libonnxruntime.so", 8129727, "ffb0a167d7e09babc412ca4bce7e3c2edd85c6cf07cc8c91f49666f30def1974",
                8127488, 22326072, "13ab8084954fa4a47c777880180b90810d6020f021441395712b48a75b74c68b",
                "linux-x64", "libonnxruntime.so.1.23.2"),
            Ort("linux-arm64", "libonnxruntime.so", 7094151, "0cdc510437807884e79c86ec43431b1d37feb13c83ffc4982c37d1ba7d31b95a",
                7071758, 18693384, "648ffa64fbe027ae27139109410900cf776a030dec2dbbac51053318cc44c286",
                "linux-aarch64", "libonnxruntime.so.1.23.2"),
            Ort("win-x64", "onnxruntime.dll", 5392953, "920060a5b587ec49d3d6328a1145719397930415c1c4740129c6ad1b37ebf7ef",
                5378414, 14186016, "dec964ab1ee36cc9b0ae247d13b376627992fc57dec0454354017ab8fd84f1ea",
                "win-x64", "onnxruntime.dll"),
            Ort("win-arm64", "onnxruntime.dll", 5408923, "90895efbe8aed3f053f8ac43f0b9ce30bc02932f900b06d62a542b9ebdf7dbff",
                5387260, 14868512, "99a11bb077f723a81d100fd98099f332e4178bbb7025ac10fd40699a0b82c2f0",
                "win-arm64", "onnxruntime.dll"),
            Ort("osx-arm64", "libonnxruntime.dylib", 9823197, "6c04efc7da5ee4de6fe674d9c8353f354693bd3b03596501135d8a8c31ad81e8",
                9821051, 35138784, "d306d2bc768540766c7ed8a1e0ff05d2870c77a934ebeee4a7bafa1b732ef299",
                "osx-arm64", "libonnxruntime.1.23.2.dylib"),
            Ort("osx-x64", "libonnxruntime.dylib", 11470661, "ff8f3652bd59e5b791aadd6d12fae1dc7021aa4b449d45644d7fd04f422a76d3",
                11498106, 39742608, "8c9c78de65ea3786f987c0d980e9c1b13a3a5fbc6b3e2965ba05b450e6e4c054",
                "osx-x86_64", "libonnxruntime.1.23.2.dylib"),
        },
    };
}
