using Lux.Engine.Mtp.GPhoto2;
using Lux.Engine.Mtp.Wpd;

namespace Lux.Engine.Mtp;

/// <summary>Selects the camera-transfer backend for the current platform.</summary>
public static class MtpFactory
{
    /// <summary>libgphoto2 on Linux and macOS; Windows Portable Devices on Windows.</summary>
    public static IMtpBackend Backend { get; } =
        OperatingSystem.IsWindows() ? new WpdBackend() : new GPhoto2Backend();
}
