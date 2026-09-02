namespace Lux.Engine.Mtp;

/// <summary>Filter + download matching files from an MTP device (single device pipe = serial).</summary>
public static class MtpPuller
{
    /// <summary>Enumerate + filter (no download).</summary>
    public static IReadOnlyList<MtpItem> List(IMtpDevice dev, PullFilter filter)
        => dev.EnumerateFiles().Where(filter.Matches).OrderBy(i => i.Name).ToList();

    /// <summary>
    /// Download all matching items into <paramref name="outDir"/>. Existing local files of the same
    /// size are skipped (incremental) unless <paramref name="overwrite"/> is set.
    /// <paramref name="onFile"/> reports (item, index, total, skipped) after each file.
    /// </summary>
    public static PullResult Pull(IMtpDevice dev, PullFilter filter, string outDir, bool overwrite = false,
        Action<MtpItem, int, int, bool>? onFile = null, IProgress<long>? byteProgress = null)
    {
        var matches = List(dev, filter);
        Directory.CreateDirectory(outDir);
        int done = 0, skipped = 0, failed = 0;
        long bytes = 0;
        var files = new List<string>();

        for (int i = 0; i < matches.Count; i++)
        {
            var item = matches[i];
            string dest = Path.Combine(outDir, item.Name);
            // skip if a complete local copy exists (size match when known; else any existing file — the
            // downloader writes to .part then renames, so an existing dest means a finished prior pull)
            bool skip = !overwrite && File.Exists(dest) && (item.Size <= 0 || new FileInfo(dest).Length == item.Size);
            if (skip)
            {
                skipped++;
                onFile?.Invoke(item, i + 1, matches.Count, true);
                continue;
            }
            try
            {
                dev.Download(item, dest, byteProgress);
                done++; bytes += File.Exists(dest) ? new FileInfo(dest).Length : item.Size; files.Add(dest);
            }
            catch
            {
                failed++;
            }
            onFile?.Invoke(item, i + 1, matches.Count, false);
        }
        return new PullResult(done, skipped, failed, bytes, files);
    }
}
