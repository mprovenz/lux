namespace Lux.Engine.Mtp;

/// <summary>A platform camera-transfer backend (libgphoto2 on Linux/macOS, WPD on Windows).</summary>
public interface IMtpBackend
{
    string Name { get; }

    /// <summary>True if this backend can run here (native lib present, right platform).</summary>
    bool IsAvailable { get; }

    /// <summary>Open all currently-connected cameras. Caller disposes each.</summary>
    IReadOnlyList<IMtpDevice> Detect();
}

/// <summary>An open connection to one camera.</summary>
public interface IMtpDevice : IDisposable
{
    /// <summary>Model / friendly name.</summary>
    string Name { get; }

    /// <summary>Backend port string (e.g. "usb:001,005") for diagnostics.</summary>
    string Port { get; }

    IReadOnlyList<MtpStorage> Storages { get; }

    /// <summary>Enumerate all file objects on the device (walks the folder tree).</summary>
    IEnumerable<MtpItem> EnumerateFiles();

    /// <summary>Download one item to a local path.</summary>
    void Download(MtpItem item, string destPath, IProgress<long>? progress = null);
}
