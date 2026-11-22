using System.Runtime.InteropServices;
using System.Text;
using Lux.Engine.Pipeline.DepthRefine;

namespace Lux.DepthModel;

/// <summary>
/// The depth network on ONNX Runtime, through the runtime's C interface. The runtime library is loaded from the file the manager
/// installed (<see cref="NativeLibrary.Load(string)"/>): nothing is searched on the system, and Lux carries no binding package.
/// <para>The C interface is one table of function pointers (<c>OrtApi</c>). The positions used here are fixed for every runtime version
/// from <see cref="ApiVersion"/> on.</para>
/// <para>Inference runs on the processor with a fixed number of threads, so the same picture gives the same result on the same
/// computer. Results can differ in the last bits between processor types and operating systems.</para>
/// </summary>
public sealed unsafe class OnnxDepthNetwork : IDepthNetwork, IDisposable
{
    /// <summary>The interface version asked of the runtime (runtime 1.17 and later provide it).</summary>
    const uint ApiVersion = 17;

    // positions in the OrtApi table
    const int GetErrorMessage = 2, CreateEnv = 3, DisableTelemetryEvents = 6, CreateSession = 7, RunSession = 9, CreateSessionOptions = 10, SetSessionGraphOptimizationLevel = 23,
              SetIntraOpNumThreads = 24, SetInterOpNumThreads = 25, CreateTensorWithDataAsOrtValue = 49, GetTensorMutableData = 51,
              GetTensorShapeElementCount = 64, GetTensorTypeAndShape = 65, CreateCpuMemoryInfo = 69, ReleaseEnv = 92, ReleaseStatus = 93,
              ReleaseMemoryInfo = 94, ReleaseSession = 95, ReleaseValue = 96, ReleaseTensorTypeAndShapeInfo = 99, ReleaseSessionOptions = 100;

    /// <summary>The most threads one inference uses.</summary>
    public const int MaxThreads = 8;
    /// <summary>The threads one inference uses on this computer: the processor count, at most <see cref="MaxThreads"/>.</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount, 1, MaxThreads);

    readonly nint _library; readonly void** _api;
    nint _env, _session, _memory;
    readonly byte[] _input, _output;
    readonly object _lock = new();

    public string Id { get; }

    /// <summary>Check the installed files and open the network. Throws <see cref="DepthModelException"/> when the files are missing or
    /// damaged, or the runtime cannot start on this computer.</summary>
    public static OnnxDepthNetwork Open(DepthModelManager manager)
    {
        manager.VerifyInstalled();
        return new OnnxDepthNetwork(manager.LibraryPath!, manager.ModelPath, manager.Manifest.Model, manager.Manifest.ModelId + "/onnxruntime-" + manager.Manifest.RuntimeVersion);
    }

    public OnnxDepthNetwork(string libraryPath, string modelPath, ModelPackage model, string id)
    {
        Id = id;
        _input = Encoding.UTF8.GetBytes(model.Input + "\0"); _output = Encoding.UTF8.GetBytes(model.Output + "\0");
        try
        {
            _library = NativeLibrary.Load(libraryPath);
            // OrtApiBase { const OrtApi* (*GetApi)(uint32_t version); const char* (*GetVersionString)(void); }
            var apiBase = (void**)((delegate* unmanaged<void*>)NativeLibrary.GetExport(_library, "OrtGetApiBase"))();
            _api = (void**)((delegate* unmanaged<uint, void*>)apiBase[0])(ApiVersion);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new DepthModelException("The inference runtime could not start on this computer: " + e.Message, e);
        }
        if (_api is null) throw new DepthModelException("The inference runtime on this computer is too old.");
        try
        {
            nint env, options, session, memory;
            fixed (byte* logId = "lux\0"u8)
                Check(((delegate* unmanaged<int, byte*, nint*, nint>)_api[CreateEnv])(3 /* errors only */, logId, &env));
            _env = env;
            // The Windows builds of the runtime can send usage data to Microsoft. Lux turns that off before it makes a session.
            // On other systems the runtime sends nothing, and the call has no effect.
            Check(((delegate* unmanaged<nint, nint>)_api[DisableTelemetryEvents])(env));
            Check(((delegate* unmanaged<nint*, nint>)_api[CreateSessionOptions])(&options));
            try
            {
                Check(((delegate* unmanaged<nint, int, nint>)_api[SetIntraOpNumThreads])(options, Threads));
                Check(((delegate* unmanaged<nint, int, nint>)_api[SetInterOpNumThreads])(options, 1));
                Check(((delegate* unmanaged<nint, int, nint>)_api[SetSessionGraphOptimizationLevel])(options, 99 /* all */));
                // the path is wide characters on Windows, UTF-8 elsewhere
                byte[] path = OperatingSystem.IsWindows() ? Encoding.Unicode.GetBytes(modelPath + "\0") : Encoding.UTF8.GetBytes(modelPath + "\0");
                fixed (byte* p = path) Check(((delegate* unmanaged<nint, byte*, nint, nint*, nint>)_api[CreateSession])(_env, p, options, &session));
                _session = session;
            }
            finally { ((delegate* unmanaged<nint, void>)_api[ReleaseSessionOptions])(options); }
            Check(((delegate* unmanaged<int, int, nint*, nint>)_api[CreateCpuMemoryInfo])(0 /* device allocator */, 0 /* default memory */, &memory));
            _memory = memory;
        }
        catch { Dispose(); throw; }
    }

    void Check(nint status)
    {
        if (status == 0) return;
        string message = Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<nint, byte*>)_api[GetErrorMessage])(status)) ?? "unknown error";
        ((delegate* unmanaged<nint, void>)_api[ReleaseStatus])(status);
        throw new DepthModelException("The inference runtime reported an error: " + message);
    }

    public float[] Run(float[] planes, int width, int height)
    {
        if (planes.Length != 3L * width * height) throw new ArgumentException("the input is not three planes of the given size");
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_session == 0, this);
            nint input = 0, output = 0, info = 0;
            long* shape = stackalloc long[4] { 1, 3, height, width };
            try
            {
                fixed (float* data = planes)
                fixed (byte* inName = _input, outName = _output)
                {
                    Check(((delegate* unmanaged<nint, void*, nuint, long*, nuint, int, nint*, nint>)_api[CreateTensorWithDataAsOrtValue])(
                        _memory, data, (nuint)planes.Length * sizeof(float), shape, 4, 1 /* float */, &input));
                    byte* inNames = inName, outNames = outName;
                    Check(((delegate* unmanaged<nint, nint, byte**, nint*, nuint, byte**, nuint, nint*, nint>)_api[RunSession])(
                        _session, 0, &inNames, &input, 1, &outNames, 1, &output));
                }
                nuint count; float* result;
                Check(((delegate* unmanaged<nint, nint*, nint>)_api[GetTensorTypeAndShape])(output, &info));
                Check(((delegate* unmanaged<nint, nuint*, nint>)_api[GetTensorShapeElementCount])(info, &count));
                Check(((delegate* unmanaged<nint, float**, nint>)_api[GetTensorMutableData])(output, &result));
                if (count != (nuint)((long)width * height)) throw new DepthModelException($"The depth network returned {count} values for a {width}x{height} picture.");
                var dst = new float[(int)count];
                new ReadOnlySpan<float>(result, dst.Length).CopyTo(dst);
                return dst;
            }
            finally
            {
                if (info != 0) ((delegate* unmanaged<nint, void>)_api[ReleaseTensorTypeAndShapeInfo])(info);
                if (output != 0) ((delegate* unmanaged<nint, void>)_api[ReleaseValue])(output);
                if (input != 0) ((delegate* unmanaged<nint, void>)_api[ReleaseValue])(input);
            }
        }
    }

    /// <summary>Close the session. The runtime library stays loaded until the process ends: it does not support unloading.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_api is null) return;
            if (_memory != 0) { ((delegate* unmanaged<nint, void>)_api[ReleaseMemoryInfo])(_memory); _memory = 0; }
            if (_session != 0) { ((delegate* unmanaged<nint, void>)_api[ReleaseSession])(_session); _session = 0; }
            if (_env != 0) { ((delegate* unmanaged<nint, void>)_api[ReleaseEnv])(_env); _env = 0; }
        }
    }
}
