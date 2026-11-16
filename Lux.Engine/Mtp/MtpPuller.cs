namespace Lux.Engine.Mtp;

/// <summary>What happened to one file of a pull (<see cref="MtpPuller.PullOne"/>).</summary>
public enum PullOutcome
{
    /// <summary>Copied (to <c>.part</c>, then renamed).</summary>
    Downloaded = 0,
    /// <summary>A complete local copy was already there (same size): left alone.</summary>
    Skipped = 1,
}

/// <summary>Filter + download matching files from an MTP device (single device pipe = serial). Shared by <c>lux-light pull</c> and the desktop
/// app's camera import: the same skip rule, the same safe write (<c>.part</c>, then rename; a canceled or failed file leaves nothing behind)
/// and the same free-space check.</summary>
public static class MtpPuller
{
    /// <summary>Enumerate + filter (no download). Name filters (extension, glob) are applied before a file's information is read.</summary>
    public static IReadOnlyList<MtpItem> List(IMtpDevice dev, PullFilter filter)
        => dev.EnumerateFiles((_, name) => filter.MatchesName(name)).Where(filter.Matches).OrderBy(i => i.Name).ToList();

    /// <summary>A complete local copy of <paramref name="item"/> is at <paramref name="dest"/>: the file exists and has the item's size (any
    /// existing file when the size is not known — the downloader writes to <c>.part</c> and renames, so an existing file is a finished one).</summary>
    public static bool IsComplete(MtpItem item, string dest)
        => File.Exists(dest) && (item.Size <= 0 || new FileInfo(dest).Length == item.Size);

    /// <summary>Copy one item to <paramref name="dest"/> unless a complete copy is already there (<see cref="IsComplete"/>; with
    /// <paramref name="overwrite"/> it is copied again). Cancel-safe: the bytes go to <c>dest.part</c> and are renamed when complete; on
    /// cancellation or failure the partial file is removed and the exception propagates.</summary>
    public static PullOutcome PullOne(IMtpDevice dev, MtpItem item, string dest, bool overwrite = false, IProgress<long>? progress = null,
                                      CancellationToken cancel = default)
    {
        if (!overwrite && IsComplete(item, dest)) return PullOutcome.Skipped;
        cancel.ThrowIfCancellationRequested();
        dev.Download(item, dest, progress, cancel);
        return PullOutcome.Downloaded;
    }

    /// <summary>Free space on the drive holding <paramref name="folder"/> (or its nearest existing parent); null when it cannot be told.</summary>
    public static long? FreeSpace(string folder)
    {
        try
        {
            string? d = Path.GetFullPath(folder);
            while (d is not null && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
            if (d is null) return null;
            return new DriveInfo(d).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Space kept free on the destination drive besides what the files need.</summary>
    public const long FreeSpaceReserve = 256L << 20;

    /// <summary>Null when <paramref name="bytes"/> fit on the drive of <paramref name="folder"/> with <see cref="FreeSpaceReserve"/> to spare;
    /// otherwise a plain sentence saying what is needed and what is free.</summary>
    public static string? CheckFreeSpace(string folder, long bytes)
    {
        if (bytes <= 0 || FreeSpace(folder) is not long free) return null;
        if (bytes + FreeSpaceReserve <= free) return null;
        return $"Not enough free space in {folder}: the files need {Gb(bytes)} and {Gb(Math.Max(0, free))} is free.";
    }

    /// <summary>Decimal units, as the desktop app shows sizes (1 GB = 10⁹ bytes).</summary>
    static string Gb(long b) => b >= 1_000_000_000
        ? (b / 1e9).ToString("#,0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, b / 1e6).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Download all matching items into <paramref name="outDir"/>. Existing local files of the same
    /// size are skipped (incremental) unless <paramref name="overwrite"/> is set.
    /// <paramref name="onFile"/> reports (item, index, total, skipped) after each file.
    /// </summary>
    public static PullResult Pull(IMtpDevice dev, PullFilter filter, string outDir, bool overwrite = false,
        Action<MtpItem, int, int, bool>? onFile = null, IProgress<long>? byteProgress = null)
        => Pull(dev, List(dev, filter), outDir, overwrite, onFile, byteProgress);

    /// <summary>Download these items into <paramref name="outDir"/> (see <see cref="Pull(IMtpDevice, PullFilter, string, bool, Action{MtpItem, int, int, bool}?, IProgress{long}?)"/>).
    /// A file that fails is counted and the next one is tried.</summary>
    public static PullResult Pull(IMtpDevice dev, IReadOnlyList<MtpItem> matches, string outDir, bool overwrite = false,
        Action<MtpItem, int, int, bool>? onFile = null, IProgress<long>? byteProgress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(outDir);
        int done = 0, skipped = 0, failed = 0;
        long bytes = 0;
        var files = new List<string>();

        for (int i = 0; i < matches.Count; i++)
        {
            var item = matches[i];
            string dest = Path.Combine(outDir, item.Name);
            PullOutcome outcome;
            try { outcome = PullOne(dev, item, dest, overwrite, byteProgress, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
                onFile?.Invoke(item, i + 1, matches.Count, false);
                continue;
            }
            if (outcome == PullOutcome.Skipped)
            {
                skipped++;
                onFile?.Invoke(item, i + 1, matches.Count, true);
                continue;
            }
            done++; bytes += File.Exists(dest) ? new FileInfo(dest).Length : item.Size; files.Add(dest);
            onFile?.Invoke(item, i + 1, matches.Count, false);
        }
        return new PullResult(done, skipped, failed, bytes, files);
    }
}
