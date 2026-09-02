using System.Runtime.InteropServices;

namespace Lux.Engine.Mtp.GPhoto2;

/// <summary>
/// P/Invoke bindings for libgphoto2 (Linux/macOS) — speaks PTP (the L16 on Linux) and MTP. Files are
/// addressed by (folder, name). Native lib resolved by soname via <see cref="Resolver"/>.
/// </summary>
internal static class Gp
{
    private const string Lib = "gphoto2";
    public const int GP_OK = 0;
    public const int GP_FILE_TYPE_NORMAL = 1;

    static Gp() => NativeLibrary.SetDllImportResolver(typeof(Gp).Assembly, Resolver);

    private static IntPtr Resolver(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var c in new[] { "libgphoto2.so.6", "libgphoto2.so", "libgphoto2.6.dylib", "libgphoto2.dylib" })
            if (NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }

    // context
    [DllImport(Lib)] public static extern IntPtr gp_context_new();

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
