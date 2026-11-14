namespace Lux.Engine.Mtp.GPhoto2;

/// <summary>Camera backend using libgphoto2 (PTP + MTP) — Linux/macOS.</summary>
public sealed class GPhoto2Backend : IMtpBackend
{
    public string Name => "libgphoto2";

    public bool IsAvailable
    {
        get
        {
            if (OperatingSystem.IsWindows()) return false; // WPD is the clean path on Windows
            try
            {
                IntPtr ctx = Gp.gp_context_new();
                if (ctx == IntPtr.Zero) return false;
                Gp.gp_context_unref(ctx);
                return true;
            }
            catch { return false; }
        }
    }

    public IReadOnlyList<IMtpDevice> Detect()
    {
        var devices = new List<IMtpDevice>();
        var found = Autodetect();
        if (found.Count == 0) return devices;
        // the first camera that opens (a camera another program holds is left out here; Open says why)
        foreach (var c in found)
        {
            try { devices.Add(Open(c)); break; }
            catch (MtpException) { }
        }
        return devices;
    }

    /// <summary>gp_camera_autodetect: the (model, port) pairs on the bus. About 20 ms on the L16's machine; nothing is opened or claimed.</summary>
    public IReadOnlyList<MtpCandidate> Autodetect()
    {
        var found = new List<MtpCandidate>();
        IntPtr ctx = Gp.gp_context_new();
        try
        {
            if (Gp.gp_list_new(out var list) != Gp.GP_OK) return found;
            try
            {
                int n = Gp.gp_camera_autodetect(list, ctx); // returns count (>=0)
                for (int i = 0; i < n; i++)
                {
                    Gp.gp_list_get_name(list, i, out var np);
                    Gp.gp_list_get_value(list, i, out var vp);
                    found.Add(new MtpCandidate(Gp.Utf8(np) ?? "camera", Gp.Utf8(vp) ?? ""));
                }
            }
            finally { Gp.gp_list_free(list); }
        }
        finally { Gp.gp_context_unref(ctx); }
        return found;
    }

    /// <summary>Open the camera on <paramref name="candidate"/>'s port (gp_camera_init on that port only). Throws <see cref="MtpException"/>:
    /// Busy when another program has claimed the device (gvfs, a second connection), NotFound when it went away.</summary>
    public IMtpDevice Open(MtpCandidate candidate)
    {
        IntPtr ctx = Gp.gp_context_new();
        if (Gp.gp_camera_new(out var cam) != Gp.GP_OK) { Gp.gp_context_unref(ctx); throw new MtpException(MtpErrorKind.Failed, Gp.GP_ERROR, "The camera library could not start."); }
        IntPtr portList = IntPtr.Zero;
        try
        {
            if (candidate.Port.Length > 0 && Gp.gp_port_info_list_new(out portList) == Gp.GP_OK && Gp.gp_port_info_list_load(portList) >= Gp.GP_OK)
            {
                int idx = Gp.gp_port_info_list_lookup_path(portList, candidate.Port);
                if (idx < 0) throw new MtpException(MtpErrorKind.NotFound, idx, "The camera is no longer connected.");
                if (Gp.gp_port_info_list_get_info(portList, idx, out var info) == Gp.GP_OK) Gp.gp_camera_set_port_info(cam, info);
            }
            int rc = Gp.gp_camera_init(cam, ctx);
            if (rc != Gp.GP_OK) throw Gp.Error(rc, "Opening the camera");
            var dev = new GPhoto2Device(cam, ctx, candidate.Model, candidate.Port);
            cam = IntPtr.Zero; ctx = IntPtr.Zero;   // owned by the device now
            return dev;
        }
        finally
        {
            if (portList != IntPtr.Zero) Gp.gp_port_info_list_free(portList);
            if (cam != IntPtr.Zero) Gp.gp_camera_unref(cam);
            if (ctx != IntPtr.Zero) Gp.gp_context_unref(ctx);
        }
    }
}
