namespace Lux.Engine.Mtp.GPhoto2;

/// <summary>An open camera via libgphoto2.</summary>
internal sealed class GPhoto2Device : IMtpDevice
{
    private IntPtr _cam;
    private readonly IntPtr _ctx;
    private readonly string _name;
    private readonly string _port;

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

    public IEnumerable<MtpItem> EnumerateFiles()
    {
        // depth-first walk of the folder tree
        var stack = new Stack<string>();
        stack.Push("/");
        while (stack.Count > 0)
        {
            string folder = stack.Pop();
            foreach (var file in ListFolder(folder, files: true))
                yield return new MtpItem(folder, file, 0, null); // size/mtime deferred (see interop notes)
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

    private const int ChunkSize = 8 * 1024 * 1024;

    public void Download(MtpItem item, string destPath, IProgress<long>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string part = destPath + ".part";
        var buf = new byte[ChunkSize];
        ulong offset = 0;
        using (var fs = File.Create(part))
        {
            while (true)
            {
                ulong size = ChunkSize;
                int rc = Gp.gp_camera_file_read(_cam, item.Folder, item.Name, Gp.GP_FILE_TYPE_NORMAL, offset, buf, ref size, _ctx);
                if (rc != Gp.GP_OK) throw new IOException($"gp_camera_file_read failed (rc={rc}) at offset {offset} for {item.FullPath}");
                if (size == 0) break; // EOF
                fs.Write(buf, 0, (int)size);
                offset += size;
                progress?.Report((long)offset);
                if (size < ChunkSize) break; // final short chunk
            }
        }
        File.Move(part, destPath, overwrite: true);
    }

    public void Dispose()
    {
        if (_cam != IntPtr.Zero)
        {
            Gp.gp_camera_exit(_cam, _ctx);
            Gp.gp_camera_unref(_cam);
            _cam = IntPtr.Zero;
        }
    }
}
