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
            try { return Gp.gp_context_new() != IntPtr.Zero; }
            catch { return false; }
        }
    }

    public IReadOnlyList<IMtpDevice> Detect()
    {
        var devices = new List<IMtpDevice>();
        IntPtr ctx = Gp.gp_context_new();

        // enumerate (model, port) pairs
        var found = new List<(string Model, string Port)>();
        if (Gp.gp_list_new(out var list) == Gp.GP_OK)
        {
            try
            {
                int n = Gp.gp_camera_autodetect(list, ctx); // returns count (>=0)
                for (int i = 0; i < n; i++)
                {
                    Gp.gp_list_get_name(list, i, out var np);
                    Gp.gp_list_get_value(list, i, out var vp);
                    found.Add((Gp.Utf8(np) ?? "camera", Gp.Utf8(vp) ?? ""));
                }
            }
            finally { Gp.gp_list_free(list); }
        }
        if (found.Count == 0) return devices;

        // MVP: open the first camera (gp_camera_init auto-selects the sole USB camera).
        // TODO multi-camera: set each camera's port via gp_camera_set_port_info before init.
        var (model, port) = found[0];
        if (Gp.gp_camera_new(out var cam) == Gp.GP_OK && Gp.gp_camera_init(cam, ctx) == Gp.GP_OK)
            devices.Add(new GPhoto2Device(cam, ctx, model, port));
        else
            Gp.gp_camera_unref(cam);
        return devices;
    }
}
