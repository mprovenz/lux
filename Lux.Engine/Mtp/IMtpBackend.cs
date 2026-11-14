namespace Lux.Engine.Mtp;

/// <summary>A platform camera-transfer backend (libgphoto2 on Linux/macOS, WPD on Windows). Read-only by design: it lists, reads file
/// information and reads file contents; there is no operation that changes anything on the camera.</summary>
public interface IMtpBackend
{
    string Name { get; }

    /// <summary>True if this backend can run here (native lib present, right platform).</summary>
    bool IsAvailable { get; }

    /// <summary>Open all currently-connected cameras. Caller disposes each.</summary>
    IReadOnlyList<IMtpDevice> Detect();

    /// <summary>The cameras on the bus, without opening them (cheap: tens of milliseconds, so it can be polled for hot-plug). Added for the
    /// desktop app's camera page; the default opens and closes each camera through <see cref="Detect"/>.</summary>
    IReadOnlyList<MtpCandidate> Autodetect()
    {
        var found = Detect();
        var list = found.Select(d => new MtpCandidate(d.Name, d.Port)).ToList();
        foreach (var d in found) d.Dispose();
        return list;
    }

    /// <summary>Open one camera found by <see cref="Autodetect"/>. Throws <see cref="MtpException"/> (<see cref="MtpErrorKind.Busy"/> when another
    /// program holds it, <see cref="MtpErrorKind.NotFound"/> when it went away). Caller disposes.</summary>
    IMtpDevice Open(MtpCandidate candidate)
    {
        var found = Detect();
        IMtpDevice? hit = null;
        foreach (var d in found)
        {
            if (hit is null && d.Port == candidate.Port) hit = d;
            else d.Dispose();
        }
        return hit ?? throw new MtpException(MtpErrorKind.NotFound, 0, "The camera is no longer connected.");
    }
}

/// <summary>An open connection to one camera. Not thread-safe: use it from one thread at a time.</summary>
public interface IMtpDevice : IDisposable
{
    /// <summary>Model / friendly name.</summary>
    string Name { get; }

    /// <summary>Backend port string (e.g. "usb:001,005") for diagnostics.</summary>
    string Port { get; }

    IReadOnlyList<MtpStorage> Storages { get; }

    /// <summary>Enumerate all file objects on the device (walks the folder tree).</summary>
    IEnumerable<MtpItem> EnumerateFiles();

    /// <summary>Enumerate the file objects whose (folder, name) <paramref name="wanted"/> accepts; the file information (size, date) is read
    /// only for those (on the L16 that is ~8 ms a file, so filtering by name first saves seconds).</summary>
    IEnumerable<MtpItem> EnumerateFiles(Func<string, string, bool> wanted) => EnumerateFiles().Where(i => wanted(i.Folder, i.Name));

    /// <summary>Download one item to a local path.</summary>
    void Download(MtpItem item, string destPath, IProgress<long>? progress = null);

    /// <summary>What the camera says about itself (manufacturer, model, serial number, battery).</summary>
    MtpDeviceInfo Info => new("", Name, "", "", null);

    /// <summary>The sub-folders of a folder ("/" is the root). Throws <see cref="MtpException"/> when the camera cannot be read.</summary>
    IReadOnlyList<string> ListFolders(string folder) => throw new NotSupportedException("This camera backend cannot list folders.");

    /// <summary>The file names in a folder (no information read yet). Throws <see cref="MtpException"/> when the camera cannot be read.</summary>
    IReadOnlyList<string> ListFiles(string folder) => throw new NotSupportedException("This camera backend cannot list files.");

    /// <summary>One file's information (size, modification time). Throws <see cref="MtpException"/> when the camera cannot be read.</summary>
    MtpItem GetItem(string folder, string name) => throw new NotSupportedException("This camera backend cannot read file information.");

    /// <summary>Read a file's whole contents into <paramref name="destination"/>, in chunks; <paramref name="cancel"/> is checked between chunks.
    /// <paramref name="progress"/> reports the bytes read so far. Throws <see cref="MtpException"/> when the camera cannot be read (a
    /// <see cref="MtpErrorKind.Disconnected"/> one when it went away) and <see cref="IOException"/> when fewer bytes arrive than the file has.</summary>
    void Read(MtpItem item, Stream destination, IProgress<long>? progress = null, CancellationToken cancel = default)
        => throw new NotSupportedException("This camera backend cannot read files into a stream.");

    /// <summary>Reads <paramref name="count"/> bytes of a file from <paramref name="offset"/> (PTP GetPartialObject). Returns fewer bytes only
    /// at the end of the file. Throws <see cref="MtpException"/> when the camera cannot be read. Throws <see cref="NotSupportedException"/>
    /// when the backend cannot read at an offset.</summary>
    byte[] ReadRange(MtpItem item, long offset, int count)
        => throw new NotSupportedException("This camera backend cannot read part of a file.");

    /// <summary>Download one item to a local path, safely: the bytes go to <c>destPath.part</c>, which is renamed to <paramref name="destPath"/>
    /// only when complete (and of the item's size when that is known); on cancellation or any failure the partial file is removed.</summary>
    void Download(MtpItem item, string destPath, IProgress<long>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        string part = destPath + ".part";
        try
        {
            long written;
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                Read(item, fs, progress, cancel);
                fs.Flush(flushToDisk: true);
                written = fs.Length;
            }
            if (item.Size > 0 && written != item.Size)
                throw new IOException($"{item.Name}: {written} of {item.Size} bytes arrived.");
            cancel.ThrowIfCancellationRequested();
            File.Move(part, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
