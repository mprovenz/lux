using System.Text.RegularExpressions;

namespace Lux.Engine.Mtp.GPhoto2;

/// <summary>An open camera via libgphoto2. Only reading calls are made: folder and file lists, file information, file reads and the
/// summary (device information and properties).</summary>
internal sealed class GPhoto2Device : IMtpDevice
{
    private IntPtr _cam;
    private IntPtr _ctx;
    private readonly string _name;
    private readonly string _port;
    private MtpDeviceInfo? _info;

    internal GPhoto2Device(IntPtr cam, IntPtr ctx, string name, string port)
        => (_cam, _ctx, _name, _port) = (cam, ctx, name, port);

    public string Name => _name;
    public string Port => _port;

    public IReadOnlyList<MtpStorage> Storages
    {
        get
        {
            var list = new List<MtpStorage>();
            if (Gp.gp_list_new(out var l) != Gp.GP_OK) return list;
            try
            {
                if (Gp.gp_camera_folder_list_folders(_cam, "/", l, _ctx) == Gp.GP_OK)
                    foreach (var s in Gp.ListNames(l)) list.Add(new MtpStorage("/" + s, s));
            }
            finally { Gp.gp_list_free(l); }
            return list;
        }
    }

    /// <summary>From the camera's summary (gp_camera_get_summary): "Manufacturer: Light", "Model: L16", "Version: 1.0", "Serial Number: …" and
    /// the battery property "Battery Level (5001 …) … value: 80% (80)". Read once, when first asked.</summary>
    public MtpDeviceInfo Info => _info ??= ReadInfo();

    MtpDeviceInfo ReadInfo()
    {
        var buf = new byte[Gp.CameraTextSize];
        if (Gp.gp_camera_get_summary(_cam, buf, _ctx) != Gp.GP_OK) return new MtpDeviceInfo("", _name, "", "", null);
        int end = Array.IndexOf(buf, (byte)0);
        return ParseSummary(System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end), _name);
    }

    internal static MtpDeviceInfo ParseSummary(string text, string fallbackModel)
    {
        string Field(string key)
        {
            var m = Regex.Match(text, @"^\s*" + Regex.Escape(key) + @":\s*(.*?)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "";
        }
        int? battery = null;
        var b = Regex.Match(text, @"Battery Level.*?value:\s*(\d+)\s*%", RegexOptions.IgnoreCase);
        if (b.Success && int.TryParse(b.Groups[1].Value, out int pct)) battery = pct;
        string model = Field("Model");
        return new MtpDeviceInfo(Field("Manufacturer"), model.Length > 0 ? model : fallbackModel, Field("Serial Number"), Field("Version"), battery);
    }

    public IEnumerable<MtpItem> EnumerateFiles() => EnumerateFiles((_, _) => true);

    public IEnumerable<MtpItem> EnumerateFiles(Func<string, string, bool> wanted)
    {
        // depth-first walk of the folder tree
        var stack = new Stack<string>();
        stack.Push("/");
        while (stack.Count > 0)
        {
            string folder = stack.Pop();
            foreach (var file in ListFolder(folder, files: true))
            {
                if (!wanted(folder, file)) continue;
                // size and modification time from gp_camera_file_get_info (the PTP object info's size and date); the `--since`/`--until`
                // filters need the date — without it every file is filtered out
                var (size, modified) = Gp.FileInfo(_cam, folder, file, _ctx);
                yield return new MtpItem(folder, file, size, modified);
            }
            foreach (var sub in ListFolder(folder, files: false))
                stack.Push(folder.TrimEnd('/') + "/" + sub);
        }
    }

    private List<string> ListFolder(string folder, bool files)
    {
        if (Gp.gp_list_new(out var l) != Gp.GP_OK) return new List<string>();
        try
        {
            int rc = files
                ? Gp.gp_camera_folder_list_files(_cam, folder, l, _ctx)
                : Gp.gp_camera_folder_list_folders(_cam, folder, l, _ctx);
            return rc == Gp.GP_OK ? Gp.ListNames(l) : new List<string>();
        }
        finally { Gp.gp_list_free(l); }
    }

    private List<string> ListOrThrow(string folder, bool files)
    {
        if (Gp.gp_list_new(out var l) != Gp.GP_OK) throw new MtpException(MtpErrorKind.Failed, Gp.GP_ERROR, "The camera library ran out of memory.");
        try
        {
            int rc = files
                ? Gp.gp_camera_folder_list_files(_cam, folder, l, _ctx)
                : Gp.gp_camera_folder_list_folders(_cam, folder, l, _ctx);
            if (rc != Gp.GP_OK) throw Gp.Error(rc, $"Listing {folder}");
            return Gp.ListNames(l);
        }
        finally { Gp.gp_list_free(l); }
    }

    public IReadOnlyList<string> ListFolders(string folder) => ListOrThrow(folder, files: false);

    public IReadOnlyList<string> ListFiles(string folder) => ListOrThrow(folder, files: true);

    public MtpItem GetItem(string folder, string name)
    {
        int rc = Gp.gp_camera_file_get_info(_cam, folder, name, out var info, _ctx);
        if (rc != Gp.GP_OK) throw Gp.Error(rc, $"Reading the information of {name}");
        long size = (info.File.Fields & Gp.GP_FILE_INFO_SIZE) != 0 ? (long)info.File.Size : 0;
        DateTimeOffset? modified = (info.File.Fields & Gp.GP_FILE_INFO_MTIME) != 0 && info.File.Mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(info.File.Mtime) : null;
        return new MtpItem(folder, name, size, modified);
    }

    private const int ChunkSize = 8 * 1024 * 1024;

    /// <summary>Streaming read (gp_camera_file_read with offsets — gp_camera_file_get of a whole file is not supported for the .lri object
    /// format). With the size known it reads until that many bytes arrived (a short chunk before the end is not taken as the end); with the
    /// size unknown, until a short or empty chunk.</summary>
    public void Read(MtpItem item, Stream destination, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        var buf = new byte[ChunkSize];
        ulong offset = 0;
        while (item.Size <= 0 || (long)offset < item.Size)
        {
            cancel.ThrowIfCancellationRequested();
            ulong size = ChunkSize;
            int rc = Gp.gp_camera_file_read(_cam, item.Folder, item.Name, Gp.GP_FILE_TYPE_NORMAL, offset, buf, ref size, _ctx);
            if (rc != Gp.GP_OK) throw Gp.Error(rc, $"Reading {item.Name} at byte {offset}");
            if (size == 0) break; // EOF
            destination.Write(buf, 0, (int)size);
            offset += size;
            progress?.Report((long)offset);
            if (item.Size <= 0 && size < ChunkSize) break; // final short chunk (size unknown)
        }
        if (item.Size > 0 && (long)offset != item.Size)
            throw new IOException($"{item.Name}: {offset} of {item.Size} bytes arrived from the camera.");
    }

    /// <summary>Reads part of a file with gp_camera_file_read at an offset (PTP GetPartialObject). On the L16, a read of 32 bytes or 1.5 KB
    /// takes 6–29 ms at any offset.</summary>
    public byte[] ReadRange(MtpItem item, long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            ulong size = (ulong)(count - got);
            var chunk = got == 0 ? buf : new byte[count - got];
            int rc = Gp.gp_camera_file_read(_cam, item.Folder, item.Name, Gp.GP_FILE_TYPE_NORMAL, (ulong)(offset + got), chunk, ref size, _ctx);
            if (rc != Gp.GP_OK) throw Gp.Error(rc, $"Reading {item.Name} at byte {offset + got}");
            if (size == 0) break;   // end of the file
            if (got > 0) Array.Copy(chunk, 0, buf, got, (int)size);
            got += (int)size;
        }
        return got == count ? buf : buf[..got];
    }

    public void Download(MtpItem item, string destPath, IProgress<long>? progress = null)
        => ((IMtpDevice)this).Download(item, destPath, progress, CancellationToken.None);

    public void Dispose()
    {
        if (_cam != IntPtr.Zero)
        {
            Gp.gp_camera_exit(_cam, _ctx);
            Gp.gp_camera_unref(_cam);
            _cam = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero) { Gp.gp_context_unref(_ctx); _ctx = IntPtr.Zero; }
    }
}
