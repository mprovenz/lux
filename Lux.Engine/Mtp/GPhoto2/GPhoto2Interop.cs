using System.Runtime.InteropServices;

namespace Lux.Engine.Mtp.GPhoto2;

/// <summary>
/// P/Invoke bindings for libgphoto2 (Linux/macOS) — speaks PTP (the L16 on Linux) and MTP. Files are
/// addressed by (folder, name). Native lib resolved by soname via <see cref="Resolver"/>.
/// </summary>
internal static class Gp
{
    private const string Lib = "gphoto2";
    private const string PortLib = "gphoto2_port";
    public const int GP_OK = 0;
    public const int GP_FILE_TYPE_NORMAL = 1;

    // gphoto2-port-result.h / gphoto2-result.h (the ones the camera page tells apart)
    public const int GP_ERROR = -1, GP_ERROR_NOT_SUPPORTED = -6, GP_ERROR_IO = -7, GP_ERROR_TIMEOUT = -10, GP_ERROR_IO_INIT = -31,
                     GP_ERROR_IO_READ = -34, GP_ERROR_IO_WRITE = -35, GP_ERROR_IO_USB_CLEAR_HALT = -51, GP_ERROR_IO_USB_FIND = -52,
                     GP_ERROR_IO_USB_CLAIM = -53, GP_ERROR_IO_LOCK = -60, GP_ERROR_MODEL_NOT_FOUND = -105, GP_ERROR_FILE_NOT_FOUND = -108,
                     GP_ERROR_DIRECTORY_NOT_FOUND = -107, GP_ERROR_CAMERA_BUSY = -110, GP_ERROR_CANCEL = -112;

    static Gp() => NativeLibrary.SetDllImportResolver(typeof(Gp).Assembly, Resolver);

    private static IntPtr Resolver(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            Lib => new[] { "libgphoto2.so.6", "libgphoto2.so", "libgphoto2.6.dylib", "libgphoto2.dylib" },
            PortLib => new[] { "libgphoto2_port.so.12", "libgphoto2_port.so", "libgphoto2_port.12.dylib", "libgphoto2_port.dylib" },
            _ => Array.Empty<string>(),
        };
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }

    /// <summary>The backend's own text for an error number ("Could not claim the USB device").</summary>
    [DllImport(Lib)] static extern IntPtr gp_result_as_string(int result);
    public static string ResultText(int rc) { try { return Utf8(gp_result_as_string(rc)) ?? $"error {rc}"; } catch (EntryPointNotFoundException) { return $"error {rc}"; } }

    /// <summary>The kind of an error number, in the terms the user can act on.</summary>
    public static MtpErrorKind KindOf(int rc) => rc switch
    {
        GP_ERROR_IO_USB_CLAIM or GP_ERROR_IO_LOCK or GP_ERROR_CAMERA_BUSY => MtpErrorKind.Busy,
        GP_ERROR_IO or GP_ERROR_IO_READ or GP_ERROR_IO_WRITE or GP_ERROR_IO_USB_FIND or GP_ERROR_IO_INIT or GP_ERROR_IO_USB_CLEAR_HALT or GP_ERROR_TIMEOUT
            => MtpErrorKind.Disconnected,
        GP_ERROR_FILE_NOT_FOUND or GP_ERROR_DIRECTORY_NOT_FOUND or GP_ERROR_MODEL_NOT_FOUND => MtpErrorKind.NotFound,
        _ => MtpErrorKind.Failed,
    };

    public static MtpException Error(int rc, string what) => new(KindOf(rc), rc, $"{what}: {ResultText(rc)} ({rc})");

    // context
    [DllImport(Lib)] public static extern IntPtr gp_context_new();
    [DllImport(Lib)] public static extern void gp_context_unref(IntPtr context);

    // port selection (opens the camera on the port autodetect reported, not the first camera found)
    [DllImport(PortLib)] public static extern int gp_port_info_list_new(out IntPtr list);
    [DllImport(PortLib)] public static extern int gp_port_info_list_load(IntPtr list);
    [DllImport(PortLib)] public static extern int gp_port_info_list_lookup_path(IntPtr list, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(PortLib)] public static extern int gp_port_info_list_get_info(IntPtr list, int n, out IntPtr info);
    [DllImport(PortLib)] public static extern int gp_port_info_list_free(IntPtr list);
    [DllImport(Lib)] public static extern int gp_camera_set_port_info(IntPtr camera, IntPtr info);

    // the camera's summary text (PTP device info: manufacturer, model, serial number, properties such as the battery level)
    [DllImport(Lib)] public static extern int gp_camera_get_summary(IntPtr camera, byte[] text, IntPtr context);
    public const int CameraTextSize = 32 * 1024;

    // camera lifecycle
    [DllImport(Lib)] public static extern int gp_camera_new(out IntPtr camera);
    [DllImport(Lib)] public static extern int gp_camera_init(IntPtr camera, IntPtr context);
    [DllImport(Lib)] public static extern int gp_camera_exit(IntPtr camera, IntPtr context);
    [DllImport(Lib)] public static extern int gp_camera_unref(IntPtr camera);
    [DllImport(Lib)] public static extern int gp_camera_autodetect(IntPtr list, IntPtr context);

    // lists
    [DllImport(Lib)] public static extern int gp_list_new(out IntPtr list);
    [DllImport(Lib)] public static extern int gp_list_free(IntPtr list);
    [DllImport(Lib)] public static extern int gp_list_count(IntPtr list);
    [DllImport(Lib)] public static extern int gp_list_get_name(IntPtr list, int index, out IntPtr name);
    [DllImport(Lib)] public static extern int gp_list_get_value(IntPtr list, int index, out IntPtr value);

    // folder / file listing
    [DllImport(Lib)] public static extern int gp_camera_folder_list_folders(IntPtr camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, IntPtr list, IntPtr context);
    [DllImport(Lib)] public static extern int gp_camera_folder_list_files(IntPtr camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, IntPtr list, IntPtr context);

    // download (streaming — gp_camera_file_get whole-file is NOT_SUPPORTED for the .lri object format)
    [DllImport(Lib)] public static extern int gp_camera_file_read(IntPtr camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, int type, ulong offset, byte[] buf, ref ulong size, IntPtr context);
    [DllImport(Lib)] public static extern int gp_camera_file_get(IntPtr camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, int type, IntPtr cameraFile, IntPtr context);
    [DllImport(Lib)] public static extern int gp_file_new(out IntPtr file);
    [DllImport(Lib)] public static extern int gp_file_free(IntPtr file);
    [DllImport(Lib)] public static extern int gp_file_save(IntPtr file, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

    // per-file info (size, modification time): gp_camera_file_get_info(camera, folder, file, CameraFileInfo*, context)
    [DllImport(Lib)] public static extern int gp_camera_file_get_info(IntPtr camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, out CameraFileInfo info, IntPtr context);

    // CameraFileInfoFields bits (gphoto2-filesys.h): TYPE 1<<0, SIZE 1<<2, WIDTH 1<<3, HEIGHT 1<<4, PERMISSIONS 1<<5, STATUS 1<<6, MTIME 1<<7.
    // An .lri reports 0xa5 (type, size, permissions, mtime). With 1<<1 and 1<<6 as SIZE and MTIME, no file has a date and
    // `--since` matches nothing.
    public const uint GP_FILE_INFO_SIZE = 1 << 2, GP_FILE_INFO_MTIME = 1 << 7;

    /// <summary>libgphoto2 2.5 `CameraFileInfoPreview`: fields, status, uint64 size, char type[64], width, height (88 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraFileInfoPreview { public uint Fields; public int Status; public ulong Size; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Type; public uint Width, Height; }
    /// <summary>libgphoto2 2.5 `CameraFileInfoFile`: preview's fields + permissions (int) and `time_t mtime` (64-bit here; 104 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraFileInfoFile { public uint Fields; public int Status; public ulong Size; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Type; public uint Width, Height; public int Permissions; public long Mtime; }
    /// <summary>libgphoto2 2.5 `CameraFileInfoAudio`: fields, status, uint64 size, char type[64] (80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraFileInfoAudio { public uint Fields; public int Status; public ulong Size; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Type; }
    /// <summary>libgphoto2 2.5 `CameraFileInfo` = { preview, file, audio } (272 bytes on x86-64/arm64 Linux and macOS).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraFileInfo { public CameraFileInfoPreview Preview; public CameraFileInfoFile File; public CameraFileInfoAudio Audio; }

    /// <summary>The (size, modification time) of one file, or (0, null) for whatever the driver does not report.</summary>
    public static (long Size, DateTimeOffset? Modified) FileInfo(IntPtr camera, string folder, string file, IntPtr context)
    {
        if (gp_camera_file_get_info(camera, folder, file, out var info, context) != GP_OK) return (0, null);
        long size = (info.File.Fields & GP_FILE_INFO_SIZE) != 0 ? (long)info.File.Size : 0;
        DateTimeOffset? modified = (info.File.Fields & GP_FILE_INFO_MTIME) != 0 && info.File.Mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(info.File.Mtime) : null;
        return (size, modified);
    }

    public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>Read a CameraList into a managed string list.</summary>
    public static List<string> ListNames(IntPtr list)
    {
        int n = gp_list_count(list);
        var names = new List<string>(Math.Max(n, 0));
        for (int i = 0; i < n; i++)
            if (gp_list_get_name(list, i, out var np) == GP_OK) names.Add(Utf8(np) ?? "");
        return names;
    }
}
