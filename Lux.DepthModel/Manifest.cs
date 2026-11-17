using System.Runtime.InteropServices;

namespace Lux.DepthModel;

/// <summary>One file to download: its name, where it comes from, its size and its SHA-256.</summary>
public sealed record DownloadFile(string Name, string Url, long Size, string Sha256);

/// <summary>The inference runtime for one platform: the release archive and the one library inside it that Lux uses.</summary>
/// <param name="Rid">The platform: operating system and processor (linux-x64, win-arm64, …).</param>
/// <param name="Entry">The path of the library inside the archive.</param>
/// <param name="LibraryFile">The file name the library is stored under.</param>
public sealed record RuntimePackage(string Rid, DownloadFile Archive, string Entry, string LibraryFile, long LibrarySize, string LibrarySha256);

/// <summary>The depth network: one ONNX file, and the names of its input and output.</summary>
public sealed record ModelPackage(DownloadFile File, string Input, string Output);

/// <summary>
/// The files the refined depth feature downloads: the depth network and, per platform, the runtime that runs it. Every file has a fixed
/// size and SHA-256. A file that does not match is rejected.
/// </summary>
public sealed class DepthModelManifest
{
    /// <summary>The environment variable that replaces the download source: a folder or a URL that holds the files under their
    /// <see cref="DownloadFile.Name"/>. The sizes and hashes stay the same.</summary>
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
    const string OrtBase = "https://github.com/microsoft/onnxruntime/releases/download/v" + OrtVersion + "/";
    const string ModelRevision = "4472b7362082ad9968fee890ca0f1e5aca36b93d";

    static RuntimePackage Ort(string rid, string archive, long size, string sha, string entry, string library, long librarySize, string librarySha)
        => new(rid, new DownloadFile(archive, OrtBase + archive, size, sha), entry, library, librarySize, librarySha);

    /// <summary>
    /// The files Lux uses. The runtime is ONNX Runtime 1.23.2 from its GitHub releases, the newest version published for all six platforms
    /// (later versions have no build for macOS on Intel processors). The model is Depth Anything V2 Small as an ONNX file, from the
    /// `onnx-community/depth-anything-v2-small` repository on Hugging Face at a fixed revision.
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
            Ort("linux-x64", "onnxruntime-linux-x64-1.23.2.tgz", 8309231, "1fa4dcaef22f6f7d5cd81b28c2800414350c10116f5fdd46a2160082551c5f9b",
                "onnxruntime-linux-x64-1.23.2/lib/libonnxruntime.so.1.23.2", "libonnxruntime.so", 22326072, "13ab8084954fa4a47c777880180b90810d6020f021441395712b48a75b74c68b"),
            Ort("linux-arm64", "onnxruntime-linux-aarch64-1.23.2.tgz", 7254068, "7c63c73560ed76b1fac6cff8204ffe34fe180e70d6582b5332ec094810241e5c",
                "onnxruntime-linux-aarch64-1.23.2/lib/libonnxruntime.so.1.23.2", "libonnxruntime.so", 18693384, "648ffa64fbe027ae27139109410900cf776a030dec2dbbac51053318cc44c286"),
            Ort("win-x64", "onnxruntime-win-x64-1.23.2.zip", 78127794, "0b38df9af21834e41e73d602d90db5cb06dbd1ca618948b8f1d66d607ac9f3cd",
                "onnxruntime-win-x64-1.23.2/lib/onnxruntime.dll", "onnxruntime.dll", 14186016, "dec964ab1ee36cc9b0ae247d13b376627992fc57dec0454354017ab8fd84f1ea"),
            Ort("win-arm64", "onnxruntime-win-arm64-1.23.2.zip", 78932411, "1cfe88b6435df3b5fb0e9f6bd7d6f5df1e887b6174de7f6e2a47bab956f3f168",
                "onnxruntime-win-arm64-1.23.2/lib/onnxruntime.dll", "onnxruntime.dll", 14868512, "99a11bb077f723a81d100fd98099f332e4178bbb7025ac10fd40699a0b82c2f0"),
            Ort("osx-arm64", "onnxruntime-osx-arm64-1.23.2.tgz", 9999931, "b4d513ab2b26f088c66891dbbc1408166708773d7cc4163de7bdca0e9bbb7856",
                "onnxruntime-osx-arm64-1.23.2/lib/libonnxruntime.1.23.2.dylib", "libonnxruntime.dylib", 35138784, "d306d2bc768540766c7ed8a1e0ff05d2870c77a934ebeee4a7bafa1b732ef299"),
            Ort("osx-x64", "onnxruntime-osx-x86_64-1.23.2.tgz", 11676322, "d10359e16347b57d9959f7e80a225a5b4a66ed7d7e007274a15cae86836485a6",
                "onnxruntime-osx-x86_64-1.23.2/lib/libonnxruntime.1.23.2.dylib", "libonnxruntime.dylib", 39742608, "8c9c78de65ea3786f987c0d980e9c1b13a3a5fbc6b3e2965ba05b450e6e4c054"),
        },
    };
}
