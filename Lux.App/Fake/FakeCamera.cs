using Lux.Engine.Mtp;

namespace Lux.App.Fake;

/// <summary>Options of the fake camera (design time, screenshots, tests).</summary>
public sealed record FakeCameraOptions
{
    /// <summary>Captures already in the fake library (the newest ones, with the library's names, sizes and days).</summary>
    public int Imported { get; init; } = 176;
    /// <summary>Captures not imported yet, on the last two days of the fake library's calendar.</summary>
    public int New { get; init; } = 38;
    /// <summary>Small files (40–80 KB) for the new captures, so imports in tests copy quickly; false = real sizes (≈ 163 MB, stacked ≈ 650 MB).</summary>
    public bool SmallFiles { get; init; }
    public string Serial { get; init; } = "LFCL0000DEMO0001";
    public int? Battery { get; init; } = 80;
    /// <summary>Simulated time of the camera's file list, ms (the real L16: ~17 s).</summary>
    public int FileListMs { get; init; }
    /// <summary>Simulated time of one capture's details, ms (the real L16: ~8 ms).</summary>
    public int DetailsMs { get; init; }
    /// <summary>Simulated copy speed, bytes per second (0 = as fast as the disk).</summary>
    public long BytesPerSecond { get; init; }
    /// <summary>Plugged in when created.</summary>
    public bool Plugged { get; init; } = true;
}

/// <summary>One file on the fake camera.</summary>
public sealed record FakeCameraFile(string Folder, string Name, long Size, DateTimeOffset Modified, int Scene, bool Stacked, bool Telephoto)
{
    /// <summary>How the camera was held: the degrees that show the capture upright (0, 90, 180 or 270), or −1 for no usable gravity.
    /// <see cref="FakeMtpDevice.ReadRange"/> returns it in the first header of the capture.</summary>
    public int Held { get; init; }
}

/// <summary>
/// A camera that behaves like the L16 over libgphoto2 for the camera page and its tests: the same folder tree (<c>DCIM/Camera</c> with the
/// captures, their preview JPEGs, a video and a test-app sidecar; <c>lightcal</c> with calibration <c>.lri</c> files that are not captures;
/// the camera app's <c>thumbnails</c>), plug / unplug, "busy" (held by another program), a pull of the cable in the middle of a copy, and
/// holds that freeze the listing in a phase for screenshots. Like the real one it offers no way to change anything on the camera.
/// </summary>
public sealed class FakeCamera
{
    public const string Store = "/store_00010001", CameraFolder = Store + "/DCIM/Camera";
    readonly object _l = new();
    readonly List<FakeCameraFile> _files = new();
    int _plugCount;
    long _lriBytesRead;

    public FakeCamera(FakeCameraOptions? options = null, FakeLibrary? library = null)
    {
        Options = options ?? new FakeCameraOptions();
        Plugged = Options.Plugged;
        Port = "usb:004,005";
        // the newest captures of the fake library, as they still are on the camera
        if (library is not null && Options.Imported > 0)
        {
            var page = library.Query(new Lux.Library.CaptureQuery { Sort = Lux.Library.CaptureSort.CaptureTime, Descending = true, Limit = Options.Imported, Missing = false });
            foreach (var c in page.Items.Where(c => c.CaptureTime is not null && !c.Unreadable))
            {
                var t = new DateTimeOffset(DateTime.SpecifyKind(c.CaptureTime!.Value, DateTimeKind.Local));
                int held = c.HeldOrientation switch { Lux.Library.PhotoOrientation.Rotate90 => 90, Lux.Library.PhotoOrientation.Rotate180 => 180, Lux.Library.PhotoOrientation.Rotate270 => 270, _ => 0 };
                _files.Add(new FakeCameraFile(CameraFolder, c.Name + ".lri", c.FileSize, t, library.SceneOf(c.Id).Index, c.IsStacked, c.IsTelephoto) { Held = held });
            }
        }
        // the new ones: 12 and 11 October 2026 (the fake library's last day and the one before), afternoon
        var rnd = new Random(551);
        for (int k = 0; k < Options.New; k++)
        {
            int dayBack = k < Math.Min(16, Options.New) ? 0 : 1;
            var local = new DateTime(2026, 10, 12 - dayBack, 13, 0, 0, DateTimeKind.Local).AddMinutes(7 * (k % 16) + rnd.Next(0, 5));
            bool stacked = k % 9 == 4;
            long size = Options.SmallFiles ? 40_000 + 1_000 * k : stacked ? 649_480_440 : k % 7 == 3 ? 178_858_232 : 162_625_784;
            // some captures are held turned (portrait, to either side), and one has no usable gravity
            int held = k % 5 == 2 ? 90 : k % 13 == 7 ? 270 : k == 20 ? -1 : 0;
            _files.Add(new FakeCameraFile(CameraFolder, $"L16_{1001 + k:00000}.lri", size, new DateTimeOffset(local), 1000 + k, stacked, k % 11 == 6) { Held = held });
        }
    }

    public FakeCameraOptions Options { get; }
    public bool Plugged { get; private set; }
    /// <summary>Another program holds the camera: opening it fails as libgphoto2's "could not claim the USB device".</summary>
    public bool Busy { get; set; }
    /// <summary>The bus port; a new one at every plug (as USB device numbers change).</summary>
    public string Port { get; private set; }
    /// <summary>Unplug in the middle of a copy: after this many more bytes of captures are read.</summary>
    public long? UnplugAfterBytes { get; set; }
    /// <summary>The file list waits while this is set (screenshots of the first listing phase).</summary>
    public ManualResetEventSlim FileListGate { get; } = new(true);
    /// <summary>After this many captures' details, the details wait for <see cref="DetailsGate"/> (screenshots of "n of m").</summary>
    public int? HoldDetailsAfter { get; set; }
    public ManualResetEventSlim DetailsGate { get; } = new(true);
    public int DetailsRead;
    /// <summary>The next file list fails with the unspecified error of libgphoto2, while the camera stays on the bus. The page then shows its
    /// error state.</summary>
    public bool FailNextList { get; set; }

    // counters (tests)
    public int Opens, FileListCalls, PreviewReads, CaptureReads, AutodetectCalls, RangeReads;

    public IReadOnlyList<FakeCameraFile> Files { get { lock (_l) return _files.ToList(); } }
    public void Add(FakeCameraFile f) { lock (_l) _files.Add(f); }
    /// <summary>Removes a file from the camera, as when the camera deletes it while Lux is closed.</summary>
    public void Remove(string name) { lock (_l) _files.RemoveAll(f => f.Name == name); }
    /// <summary>Replaces a file with one of the same name and another size or time.</summary>
    public void Replace(string name, Func<FakeCameraFile, FakeCameraFile> change) { lock (_l) { int i = _files.FindIndex(f => f.Name == name); if (i >= 0) _files[i] = change(_files[i]); } }

    public void Unplug() { lock (_l) Plugged = false; }
    public void Plug() { lock (_l) { Plugged = true; _plugCount++; Port = $"usb:004,{5 + _plugCount:000}"; } }

    internal FakeCameraFile? Find(string folder, string name) { lock (_l) return _files.FirstOrDefault(f => f.Folder == folder.TrimEnd('/') && f.Name == name); }

    internal void CountRead(long bytes)
    {
        bool unplug;
        lock (_l)
        {
            _lriBytesRead += bytes;
            unplug = UnplugAfterBytes is long n && _lriBytesRead >= n;
            if (unplug) { UnplugAfterBytes = null; _lriBytesRead = 0; Plugged = false; }
        }
        if (unplug) throw new MtpException(MtpErrorKind.Disconnected, -7, "Reading from the camera: I/O problem (-7)");
    }

    internal void ResetReadCount() { lock (_l) _lriBytesRead = 0; }

    /// <summary>The bytes of a capture (deterministic, so a copy can be checked).</summary>
    public static byte ByteAt(int scene, long offset) => (byte)((offset * 31 + scene * 7) ^ (offset >> 9));

    /// <summary>Makes the first block header of a capture, its LightHeader message with the accelerometer of <see cref="FakeCameraFile.Held"/>,
    /// and the offset of the message. The L16 puts the message after the frame of the first module, about 81 MB into the file. For the small
    /// test files, the message is near the start. Only partial reads return these bytes. A whole-file read keeps <see cref="ByteAt"/>, because
    /// the copy checks use it.</summary>
    public static (byte[] Block, long MsgOffset, byte[] Message) HeaderOf(FakeCameraFile f)
    {
        var (gx, gy) = f.Held switch { 90 => (-1f, 0f), 180 => (0f, -1f), 270 => (1f, 0f), -1 => (0.05f, 0.1f), _ => (0f, 1f) };
        var imu = new Ltpb.IMUData { FrameIndex = 0 };
        for (int i = 0; i < 19; i++) imu.Accelerometer.Add(new Ltpb.IMUData.Types.Sample { RowIdx = (uint)i, Data = new Ltpb.Point3F { X = gx, Y = gy, Z = 0.02f } });
        var header = new Ltpb.LightHeader();
        header.ImuData.Add(imu);
        byte[] msg = Google.Protobuf.MessageExtensions.ToByteArray(header);
        long off = f.Size > 81_141_760 + msg.Length ? 81_141_760 : Math.Max(32, Math.Min(1024, f.Size - msg.Length));
        var block = new byte[32];
        "LELR"u8.CopyTo(block);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(4), (ulong)(off + msg.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(12), (ulong)off);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), (uint)msg.Length);
        return (block, off, msg);
    }
}

/// <summary>The backend over a <see cref="FakeCamera"/>.</summary>
public sealed class FakeMtpBackend(FakeCamera camera) : IMtpBackend
{
    public FakeCamera Camera { get; } = camera;
    public string Name => "fake camera";
    public bool IsAvailable => true;

    public IReadOnlyList<IMtpDevice> Detect() => Camera.Plugged && !Camera.Busy ? new IMtpDevice[] { new FakeMtpDevice(Camera, Camera.Port) } : Array.Empty<IMtpDevice>();

    public IReadOnlyList<MtpCandidate> Autodetect()
    {
        Interlocked.Increment(ref Camera.AutodetectCalls);
        return Camera.Plugged ? new[] { new MtpCandidate("USB PTP Class Camera", Camera.Port) } : Array.Empty<MtpCandidate>();
    }

    public IMtpDevice Open(MtpCandidate candidate)
    {
        if (!Camera.Plugged || candidate.Port != Camera.Port) throw new MtpException(MtpErrorKind.NotFound, -52, "Opening the camera: Could not find the requested device on the USB port (-52)");
        if (Camera.Busy) throw new MtpException(MtpErrorKind.Busy, -53, "Opening the camera: Could not claim the USB device (-53)");
        Interlocked.Increment(ref Camera.Opens);
        return new FakeMtpDevice(Camera, candidate.Port);
    }
}

/// <summary>An open fake camera: reads only.</summary>
public sealed class FakeMtpDevice(FakeCamera cam, string port) : IMtpDevice
{
    bool _closed;
    const int Chunk = 1 << 20;

    public string Name => "USB PTP Class Camera";
    public string Port => port;
    public IReadOnlyList<MtpStorage> Storages => new[] { new MtpStorage(FakeCamera.Store, "store_00010001") };
    public MtpDeviceInfo Info => new("Light", "L16", cam.Options.Serial, "1.0", cam.Options.Battery);

    void Alive()
    {
        if (_closed || !cam.Plugged || cam.Port != port) throw new MtpException(MtpErrorKind.Disconnected, -7, "Reading from the camera: I/O problem (-7)");
    }

    public IReadOnlyList<string> ListFolders(string folder)
    {
        Alive();
        return folder.TrimEnd('/') switch
        {
            "" => new[] { "store_00010001" },
            FakeCamera.Store => new[] { "DCIM", "Pictures" },
            FakeCamera.Store + "/DCIM" => new[] { "Camera", ".thumbnails" },
            FakeCamera.CameraFolder => new[] { "lightcal", "thumbnails", "presets" },
            _ => Array.Empty<string>(),
        };
    }

    public IReadOnlyList<string> ListFiles(string folder)
    {
        Alive();
        switch (folder.TrimEnd('/'))
        {
            case FakeCamera.CameraFolder:
                Interlocked.Increment(ref cam.FileListCalls);
                if (cam.FailNextList) { cam.FailNextList = false; throw new MtpException(MtpErrorKind.Failed, -1, "Listing /store_00010001/DCIM/Camera: Unspecified error (-1)"); }
                cam.FileListGate.Wait();
                if (cam.Options.FileListMs > 0) Thread.Sleep(cam.Options.FileListMs);
                Alive();
                var names = new List<string>();
                foreach (var f in cam.Files.Where(f => f.Folder == FakeCamera.CameraFolder))
                {
                    names.Add(f.Name);
                    names.Add(Path.ChangeExtension(f.Name, ".jpg"));
                }
                names.Add("L16_00621.mp4");
                names.Add("L16T_00001.txt");
                return names;
            case FakeCamera.CameraFolder + "/lightcal": return new[] { "calibration.lri", "calibration.lri.md5", "asic_calib_v1.lri" };
            case FakeCamera.CameraFolder + "/thumbnails": return new[] { "L16_00621_thumbnail.jpg" };
            default: return Array.Empty<string>();
        }
    }

    public MtpItem GetItem(string folder, string name)
    {
        Alive();
        if (cam.HoldDetailsAfter is int n && Interlocked.Increment(ref cam.DetailsRead) > n) cam.DetailsGate.Wait();
        if (cam.Options.DetailsMs > 0) Thread.Sleep(cam.Options.DetailsMs);
        Alive();
        var f = cam.Find(folder, name) ?? throw new MtpException(MtpErrorKind.NotFound, -108, $"Reading the information of {name}: File not found (-108)");
        return new MtpItem(folder, name, f.Size, f.Modified);
    }

    public IEnumerable<MtpItem> EnumerateFiles()
    {
        foreach (var f in cam.Files) yield return new MtpItem(f.Folder, f.Name, f.Size, f.Modified);
    }

    public void Read(MtpItem item, Stream destination, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        Alive();
        if (item.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            var f = cam.Find(item.Folder, Path.ChangeExtension(item.Name, ".lri")) ?? throw new MtpException(MtpErrorKind.NotFound, -108, "File not found (-108)");
            Interlocked.Increment(ref cam.PreviewReads);
            var jpeg = FakeThumbnails.MakeJpeg(f.Scene, f.Stacked, f.Telephoto, f.Held is 90 or 270 ? f.Held : 0);   // the JPG of the camera is as captured, so it is sideways when the camera was held turned
            destination.Write(jpeg);
            progress?.Report(jpeg.Length);
            return;
        }
        var lri = cam.Find(item.Folder, item.Name) ?? throw new MtpException(MtpErrorKind.NotFound, -108, $"Reading {item.Name}: File not found (-108)");
        Interlocked.Increment(ref cam.CaptureReads);
        var buf = new byte[Chunk];
        long offset = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (offset < lri.Size)
        {
            cancel.ThrowIfCancellationRequested();
            Alive();
            int n = (int)Math.Min(Chunk, lri.Size - offset);
            for (int i = 0; i < n; i++) buf[i] = FakeCamera.ByteAt(lri.Scene, offset + i);
            cam.CountRead(n);
            destination.Write(buf, 0, n);
            offset += n;
            progress?.Report(offset);
            if (cam.Options.BytesPerSecond > 0)
            {
                double due = offset / (double)cam.Options.BytesPerSecond * 1000 - sw.ElapsedMilliseconds;
                if (due > 1) Thread.Sleep((int)Math.Min(due, 2000));
            }
        }
    }

    /// <summary>Reads part of a capture: the first header (<see cref="FakeCamera.HeaderOf"/>) at its place, and the fixed test bytes elsewhere.</summary>
    public byte[] ReadRange(MtpItem item, long offset, int count)
    {
        Alive();
        var f = cam.Find(item.Folder, item.Name) ?? throw new MtpException(MtpErrorKind.NotFound, -108, $"Reading {item.Name}: File not found (-108)");
        Interlocked.Increment(ref cam.RangeReads);
        int n = (int)Math.Max(0, Math.Min(count, f.Size - offset));
        var (block, msgOff, msg) = FakeCamera.HeaderOf(f);
        var buf = new byte[n];
        for (int i = 0; i < n; i++)
        {
            long o = offset + i;
            buf[i] = o < block.Length ? block[o] : o >= msgOff && o < msgOff + msg.Length ? msg[o - msgOff] : FakeCamera.ByteAt(f.Scene, o);
        }
        return buf;
    }

    public void Download(MtpItem item, string destPath, IProgress<long>? progress = null)
        => ((IMtpDevice)this).Download(item, destPath, progress, CancellationToken.None);

    public void Dispose() => _closed = true;
}
