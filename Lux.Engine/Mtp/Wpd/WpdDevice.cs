using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Lux.Engine.Mtp.Wpd;

/// <summary>An open camera through Windows Portable Devices. Only reading calls are made: object lists, object properties and object
/// contents. WPD gives each object an identifier and no path, so this class keeps the identifier of each folder and file that it
/// listed, by path. "/" is the device; its sub-folders are the storages.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WpdDevice : IMtpDevice
{
    IPortableDevice? _device;
    readonly IPortableDeviceContent _content;
    readonly IPortableDeviceProperties _properties;
    readonly IPortableDeviceResources _resources;
    readonly string _name, _port;
    MtpDeviceInfo? _info;
    /// <summary>False after a partial read through the MTP command interface failed.</summary>
    bool _partial = true;
    /// <summary>The object properties that a folder list needs.</summary>
    readonly IPortableDeviceKeyCollection _objectKeys = ObjectKeys();

    static IPortableDeviceKeyCollection ObjectKeys()
    {
        var keys = (IPortableDeviceKeyCollection)new PortableDeviceKeyCollectionClass();
        keys.Add(ref Wpd.ObjectName); keys.Add(ref Wpd.ObjectOriginalFileName); keys.Add(ref Wpd.ObjectContentType);
        keys.Add(ref Wpd.ObjectSize); keys.Add(ref Wpd.ObjectDateModified);
        return keys;
    }

    sealed record Entry(string Id, string Name, bool IsFolder, long Size, DateTimeOffset? Modified);
    /// <summary>The children of each folder that was listed, by folder path.</summary>
    readonly Dictionary<string, Dictionary<string, Entry>> _children = new(StringComparer.Ordinal);
    /// <summary>The object identifier of each folder path that is known.</summary>
    readonly Dictionary<string, string> _folderIds = new(StringComparer.Ordinal) { ["/"] = Wpd.DeviceObjectId };

    internal WpdDevice(IPortableDevice device, string name, string port)
    {
        (_device, _name, _port) = (device, name, port);
        Check(device.Content(out _content), "Opening the camera");
        Check(_content.Properties(out _properties), "Opening the camera");
        Check(_content.Transfer(out _resources), "Opening the camera");
    }

    public string Name => _name;
    public string Port => _port;

    /// <summary>The common Windows error codes, as the error kinds of the camera page.</summary>
    internal static MtpException Error(int hr, string what)
    {
        var kind = unchecked((uint)hr) switch
        {
            0x80070005 or 0x800700AA or 0x80070020 => MtpErrorKind.Busy,                       // access denied, busy, sharing violation
            0x80070490 or 0x80070002 or 0x80070003 or 0x802A0002 => MtpErrorKind.NotFound,     // not found
            0x8007001F or 0x8007048F or 0x800704C7 or 0x80070015 or 0x8007045D or 0x80070037 or 0x802A0006 => MtpErrorKind.Disconnected,
            _ => MtpErrorKind.Failed,
        };
        string why = kind switch
        {
            MtpErrorKind.Busy => "Windows refused access to the camera; another program may use it.",
            MtpErrorKind.NotFound => "The camera or the file is no longer there.",
            MtpErrorKind.Disconnected => "The camera is no longer connected.",
            _ => $"Windows error 0x{hr:X8}.",
        };
        return new MtpException(kind, hr, $"{what} failed. {why}");
    }
    static void Check(int hr, string what) { if (hr < 0) throw Error(hr, what); }

    static string Normal(string folder)
    {
        string f = "/" + folder.Replace('\\', '/').Trim('/');
        return f;
    }

    /// <summary>The children of a folder, read from the camera each time. One property read for each child gives its name, kind, size
    /// and date.</summary>
    Dictionary<string, Entry> ReadChildren(string folder)
    {
        folder = Normal(folder);
        string id = FolderId(folder);
        Check(_content.EnumObjects(0, id, IntPtr.Zero, out var objects), $"Listing {folder}");
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            var ids = new IntPtr[64];
            while (true)
            {
                int hr = objects.Next((uint)ids.Length, ids, out uint fetched);
                if (hr < 0) throw Error(hr, $"Listing {folder}");
                for (int i = 0; i < fetched; i++)
                {
                    string childId = Wpd.TakeString(ids[i]);
                    var e = ReadEntry(childId);
                    if (e != null) map[e.Name] = e;
                }
                if (fetched == 0 || hr != 0) break;   // S_FALSE: the list has no more objects
            }
        }
        finally { Marshal.ReleaseComObject(objects); }
        _children[folder] = map;
        foreach (var e in map.Values)
            if (e.IsFolder) _folderIds[folder.TrimEnd('/') + "/" + e.Name] = e.Id;
        return map;
    }

    Entry? ReadEntry(string id)
    {
        if (_properties.GetValues(id, _objectKeys, out var v) < 0) return null;
        try
        {
            string name = "";
            if (v.GetStringValue(ref Wpd.ObjectOriginalFileName, out var p) >= 0) name = Wpd.TakeString(p);
            if (name.Length == 0 && v.GetStringValue(ref Wpd.ObjectName, out p) >= 0) name = Wpd.TakeString(p);
            if (name.Length == 0) return null;
            bool folder = v.GetGuidValue(ref Wpd.ObjectContentType, out var type) >= 0
                          && (type == Wpd.ContentTypeFolder || type == Wpd.ContentTypeFunctionalObject);
            long size = !folder && v.GetUnsignedLargeIntegerValue(ref Wpd.ObjectSize, out ulong s) >= 0 ? (long)s : 0;
            DateTimeOffset? modified = null;
            var pv = new PropVariant();
            if (v.GetValue(ref Wpd.ObjectDateModified, ref pv) >= 0)
            {
                // The camera gives its local time without a time zone. This computer's time zone is used.
                if (pv.Type == PropVariant.VtDate && pv.Date > 1)
                    modified = new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(pv.Date), DateTimeKind.Local));
                Wpd.PropVariantClear(ref pv);
            }
            return new Entry(id, name, folder, size, modified);
        }
        finally { Marshal.ReleaseComObject(v); }
    }

    /// <summary>The object identifier of a folder. Lists the parent folders that are not known yet.</summary>
    string FolderId(string folder)
    {
        if (_folderIds.TryGetValue(folder, out var id)) return id;
        int cut = folder.LastIndexOf('/');
        string parent = cut <= 0 ? "/" : folder[..cut];
        ReadChildren(parent);
        return _folderIds.TryGetValue(folder, out id) ? id : throw new MtpException(MtpErrorKind.NotFound, 0, $"The camera has no folder {folder}.");
    }

    Entry FileEntry(string folder, string name)
    {
        folder = Normal(folder);
        if (_children.TryGetValue(folder, out var known) && known.TryGetValue(name, out var e) && !e.IsFolder) return e;
        if (ReadChildren(folder).TryGetValue(name, out e) && !e.IsFolder) return e;
        throw new MtpException(MtpErrorKind.NotFound, 0, $"The camera has no file {name} in {folder}.");
    }

    public IReadOnlyList<MtpStorage> Storages
        => ReadChildren("/").Values.Where(e => e.IsFolder).Select(e => new MtpStorage("/" + e.Name, e.Name)).ToList();

    public MtpDeviceInfo Info => _info ??= ReadInfo();
    MtpDeviceInfo ReadInfo()
    {
        if (_properties.GetValues(Wpd.DeviceObjectId, null, out var v) < 0) return new MtpDeviceInfo("", _name, "", "", null);
        try
        {
            string Text(ref PropertyKey key) => v.GetStringValue(ref key, out var p) >= 0 ? Wpd.TakeString(p) : "";
            string model = Text(ref Wpd.DeviceModel);
            int? battery = v.GetUnsignedIntegerValue(ref Wpd.DevicePowerLevel, out uint level) >= 0 && level <= 100 ? (int)level : null;
            return new MtpDeviceInfo(Text(ref Wpd.DeviceManufacturer), model.Length > 0 ? model : _name, Text(ref Wpd.DeviceSerialNumber),
                                     Text(ref Wpd.DeviceFirmwareVersion), battery);
        }
        finally { Marshal.ReleaseComObject(v); }
    }

    public IReadOnlyList<string> ListFolders(string folder) => ReadChildren(folder).Values.Where(e => e.IsFolder).Select(e => e.Name).ToList();
    public IReadOnlyList<string> ListFiles(string folder) => ReadChildren(folder).Values.Where(e => !e.IsFolder).Select(e => e.Name).ToList();

    public MtpItem GetItem(string folder, string name)
    {
        var e = FileEntry(folder, name);
        return new MtpItem(Normal(folder), name, e.Size, e.Modified);
    }

    public IEnumerable<MtpItem> EnumerateFiles() => EnumerateFiles((_, _) => true);

    public IEnumerable<MtpItem> EnumerateFiles(Func<string, string, bool> wanted)
    {
        // Depth-first walk of the folder tree. A folder that cannot be listed is skipped.
        var stack = new Stack<string>();
        stack.Push("/");
        while (stack.Count > 0)
        {
            string folder = stack.Pop();
            Dictionary<string, Entry> children;
            try { children = ReadChildren(folder); }
            catch (MtpException e) when (e.Kind is MtpErrorKind.Failed or MtpErrorKind.NotFound) { continue; }
            foreach (var e in children.Values)
            {
                if (e.IsFolder) stack.Push(folder.TrimEnd('/') + "/" + e.Name);
                else if (wanted(folder, e.Name)) yield return new MtpItem(folder, e.Name, e.Size, e.Modified);
            }
        }
    }

    IStream OpenStream(MtpItem item, out int chunk)
    {
        var e = FileEntry(item.Folder, item.Name);
        uint optimal = 0;
        Check(_resources.GetStream(e.Id, ref Wpd.ResourceDefault, 0 /* read */, ref optimal, out var stream), $"Reading {item.Name}");
        chunk = (int)Math.Clamp(optimal, 64 * 1024u, 8 * 1024 * 1024u);
        return stream;
    }

    /// <summary>Reads into the buffer until it is full or the stream ends. Returns the number of bytes read.</summary>
    static unsafe int ReadSome(IStream stream, byte[] buffer, int count, string name)
    {
        int got = 0;
        try { stream.Read(buffer, count, (IntPtr)(&got)); }
        catch (COMException e) { throw Error(e.HResult, $"Reading {name}"); }
        return got;
    }

    public void Read(MtpItem item, Stream destination, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        var stream = OpenStream(item, out int chunk);
        try
        {
            var buf = new byte[chunk];
            long total = 0;
            while (item.Size <= 0 || total < item.Size)
            {
                cancel.ThrowIfCancellationRequested();
                int got = ReadSome(stream, buf, buf.Length, item.Name);
                if (got == 0) break;
                destination.Write(buf, 0, got);
                total += got;
                progress?.Report(total);
            }
            if (item.Size > 0 && total != item.Size)
                throw new IOException($"{item.Name}: {total} of {item.Size} bytes arrived from the camera.");
        }
        finally { Marshal.ReleaseComObject(stream); }
    }

    /// <summary>Reads part of a file. The first choice is the MTP operation GetPartialObject, which reads only the bytes that are asked
    /// for. When that is not available, the file stream is used: Windows then reads all the bytes before the offset, which takes
    /// seconds in a large file.</summary>
    public byte[] ReadRange(MtpItem item, long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_partial)
        {
            var part = WpdPartial.Read(_device!, FileEntry(item.Folder, item.Name).Id, offset, count);
            if (part != null) return part;
            _partial = false;   // not available with this camera or driver: use the file stream from now on
        }
        var stream = OpenStream(item, out int chunk);
        try
        {
            if (offset > 0)
            {
                bool moved;
                try { stream.Seek(offset, 0 /* from the start */, IntPtr.Zero); moved = true; }
                catch (Exception e) when (e is COMException or NotImplementedException or NotSupportedException) { moved = false; }
                if (!moved)
                {
                    var skip = new byte[chunk];
                    long left = offset;
                    while (left > 0)
                    {
                        int got = ReadSome(stream, skip, (int)Math.Min(skip.Length, left), item.Name);
                        if (got == 0) return Array.Empty<byte>();
                        left -= got;
                    }
                }
            }
            var buf = new byte[count];
            int have = 0;
            while (have < count)
            {
                var part = have == 0 ? buf : new byte[count - have];
                int got = ReadSome(stream, part, count - have, item.Name);
                if (got == 0) break;   // the end of the file
                if (have > 0) Array.Copy(part, 0, buf, have, got);
                have += got;
            }
            return have == count ? buf : buf[..have];
        }
        finally { Marshal.ReleaseComObject(stream); }
    }

    public void Download(MtpItem item, string destPath, IProgress<long>? progress = null)
        => ((IMtpDevice)this).Download(item, destPath, progress, CancellationToken.None);

    public void Dispose()
    {
        if (_device is null) return;
        Marshal.ReleaseComObject(_objectKeys);
        Marshal.ReleaseComObject(_resources);
        Marshal.ReleaseComObject(_properties);
        Marshal.ReleaseComObject(_content);
        _device.Close();
        Marshal.ReleaseComObject(_device);
        _device = null;
    }
}
