using Lux.Engine.Mtp.GPhoto2;

namespace Lux.Engine.Mtp;

/// <summary>Selects the camera-transfer backend for the current platform.</summary>
public static class MtpFactory
{
    /// <summary>libgphoto2 (PTP+MTP) on Linux/macOS; WPD (stub for now) on Windows.</summary>
    public static IMtpBackend Backend { get; } =
        OperatingSystem.IsWindows() ? new WpdBackend() : new GPhoto2Backend();
}

/// <summary>
/// Windows Portable Devices (WPD) backend — the clean path on Windows (works with the built-in MTP
/// driver, no WinUSB swap). TODO: implement via the MediaDevices NuGet package for the Windows build.
/// </summary>
public sealed class WpdBackend : IMtpBackend
{
    public string Name => "wpd (not yet implemented)";
    public bool IsAvailable => false;
    public IReadOnlyList<IMtpDevice> Detect() =>
        throw new PlatformNotSupportedException("Windows WPD backend not implemented yet (use the MediaDevices package).");
}
