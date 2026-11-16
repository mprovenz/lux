using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lux.Engine.Mtp.Wpd;

/// <summary>Windows Portable Devices (WPD), the camera-transfer backend on Windows. It uses the MTP driver that Windows installs for the
/// camera, so no other driver is necessary. It only reads: it calls no operation that changes the camera.</summary>
[SupportedOSPlatform("windows")]
public sealed class WpdBackend : IMtpBackend
{
    public string Name => "wpd";

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { var m = (IPortableDeviceManager)new PortableDeviceManagerClass(); Marshal.ReleaseComObject(m); return true; }
            catch (Exception e) when (e is COMException or InvalidCastException or PlatformNotSupportedException or NotSupportedException) { return false; }
        }
    }

    /// <summary>The portable devices that Windows knows at this moment. The port of a candidate is the Windows device identifier.
    /// Nothing is opened.</summary>
    public IReadOnlyList<MtpCandidate> Autodetect()
    {
        var found = new List<MtpCandidate>();
        var manager = (IPortableDeviceManager)new PortableDeviceManagerClass();
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            if (manager.GetDevices(null, ref count) < 0 || count == 0) return found;
            var ids = new IntPtr[count];
            if (manager.GetDevices(ids, ref count) < 0) return found;
            for (int i = 0; i < count; i++)
            {
                string id = Wpd.TakeString(ids[i]);
                if (id.Length == 0) continue;
                found.Add(new MtpCandidate(FriendlyName(manager, id), id));
            }
        }
        finally { Marshal.ReleaseComObject(manager); }
        return found;
    }

    static string FriendlyName(IPortableDeviceManager manager, string id)
    {
        uint length = 0;
        if (manager.GetDeviceFriendlyName(id, null, ref length) >= 0 && length > 1)
        {
            var name = new char[length];
            if (manager.GetDeviceFriendlyName(id, name, ref length) >= 0) return new string(name).TrimEnd('\0');
        }
        return "camera";
    }

    public IReadOnlyList<IMtpDevice> Detect()
    {
        // The first camera that opens. A camera that does not open is left out; Open tells the cause.
        foreach (var c in Autodetect())
        {
            try { return new[] { Open(c) }; }
            catch (MtpException) { }
        }
        return Array.Empty<IMtpDevice>();
    }

    /// <summary>Opens the camera. The access asked for is read and write, because Windows refuses the MTP command interface to a
    /// client with read access only, and the partial read of a file needs that interface. Throws <see cref="MtpException"/>:
    /// NotFound when the camera went away, Busy when Windows refuses access.</summary>
    public IMtpDevice Open(MtpCandidate candidate)
    {
        var info = (IPortableDeviceValues)new PortableDeviceValuesClass();
        var device = (IPortableDevice)new PortableDeviceFtmClass();
        try
        {
            info.SetStringValue(ref Wpd.ClientName, "Lux");
            info.SetUnsignedIntegerValue(ref Wpd.ClientMajorVersion, 1);
            info.SetUnsignedIntegerValue(ref Wpd.ClientMinorVersion, 0);
            info.SetUnsignedIntegerValue(ref Wpd.ClientRevision, 0);
            info.SetUnsignedIntegerValue(ref Wpd.ClientDesiredAccess, Wpd.GenericReadWrite);
            int hr = device.Open(candidate.Port, info);
            if (hr < 0) throw WpdDevice.Error(hr, "Opening the camera");
            var opened = new WpdDevice(device, candidate.Model, candidate.Port);
            device = null!;   // the device object owns it now
            return opened;
        }
        finally
        {
            Marshal.ReleaseComObject(info);
            if (device != null) Marshal.ReleaseComObject(device);
        }
    }
}
