using Lux.Store;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    public long ChangeLocalContextFolder(string newFolder)
    {
        string cur = Normalise(SettingsImpl.LocalContextFolder), nf;
        try { nf = Normalise(newFolder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new LibraryException($"“{newFolder}” is not a valid folder path."); }
        if (nf == cur) throw new LibraryException("That folder is already the one in use for photos on read-only drives.");
        if (IsUnder(nf, cur) || IsUnder(cur, nf)) throw new LibraryException("The new folder cannot be inside the current one, or the other way round.");
        try { Directory.CreateDirectory(nf); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new LibraryException($"Lux cannot create the folder “{nf}” ({e.Message})."); }
        LocalContextStore.ForgetProbes(nf);
        if (!LocalContextStore.IsWritable(nf)) throw new LibraryException($"Lux cannot write to “{nf}”. Choose a folder you can write to.");
        return Queue.Enqueue(new ContextStoreMoveJobRequest(nf) { FromFolder = cur });
    }

    static bool IsDbSideFile(string f) => f.EndsWith("-wal", StringComparison.Ordinal) || f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("-journal", StringComparison.Ordinal);

    /// <summary>The <see cref="JobKind.ContextStoreMove"/> job: copy every file of the local store (and of the superseded folder) to the new
    /// folder, each verified by SHA-256 (<see cref="LocalContextStore.CopyVerified"/>); copy again whatever changed meanwhile; then switch the
    /// setting and the process's store; then delete the old copies. Until the switch nothing old is touched: a failure or a cancel removes the
    /// copies made and Lux keeps the old folder; an interrupted run (Lux closed or killed) is queued again and finishes — files already copied
    /// and equal are skipped, and a run that finds the switch done only deletes the old copies. Runs alone (the queue starts nothing else
    /// meanwhile, except thumbnails, which only read).</summary>
    Task MoveContextStoreAsync(ContextStoreMoveJobRequest r, JobContext job) => Task.Run(() =>
    {
        var target = new LocalContextStore(r.NewFolder);
        string cur = Normalise(SettingsImpl.LocalContextFolder);
        var from = new LocalContextStore(r.FromFolder ?? cur);
        bool switched = cur == Normalise(target.Folder);
        List<(string Src, string Dst)> Files() => new[] { (from.Folder, target.Folder), (from.SupersededFolder, target.SupersededFolder) }
            .Where(p => Directory.Exists(p.Item1))
            .SelectMany(p => Directory.EnumerateFiles(p.Item1, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".moving", StringComparison.Ordinal) && !IsDbSideFile(f))
                                      .Select(f => (f, Path.Combine(p.Item2, Path.GetRelativePath(p.Item1, f)))))
            .ToList();
        var files = Files();
        if (!switched)
        {
            var created = new List<string>();
            long total = Math.Max(1, files.Sum(f => new FileInfo(f.Src).Length)), done = 0;
            job.Report(0, $"Copying {files.Count} file{(files.Count == 1 ? "" : "s")} ({Mb(total)})");
            try
            {
                var seen = new Dictionary<string, (long, DateTime)>();
                // copy, then check once more that nothing changed meanwhile (a rating, a job) — copy again whatever did
                for (int pass = 0; pass < 4; pass++)
                {
                    bool changedAny = false;
                    foreach (var (src, dst) in files)
                    {
                        job.Cancel.ThrowIfCancellationRequested();
                        LocalContextStore.SettleDatabase(src);
                        var fi = new FileInfo(src); var stamp = (fi.Length, fi.LastWriteTimeUtc);
                        if (seen.TryGetValue(src, out var was) && was == stamp && File.Exists(dst)) continue;
                        if (pass > 0) changedAny = true;
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        if (!(File.Exists(dst) && LocalContextStore.Sha(dst) == LocalContextStore.Sha(src)))
                        {
                            if (!File.Exists(dst)) created.Add(dst);
                            LocalContextStore.CopyVerified(src, dst);
                        }
                        seen[src] = stamp;
                        done += fi.Length;
                        job.Report(0.9 * Math.Min(1, (double)done / total), $"Copying ({Mb(done)} of {Mb(total)})");
                    }
                    var now = Files();
                    if (now.Count != files.Count) { files = now; changedAny = true; }
                    if (pass > 0 && !changedAny) break;
                }
                SettingsImpl.SetLocalContextFolder(target.Folder);
                LocalContextStore.Current = target;
                target.Touched();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                foreach (var d in created) try { if (File.Exists(d)) File.Delete(d); if (File.Exists(d + ".moving")) File.Delete(d + ".moving"); } catch (IOException) { }
                foreach (var root in new[] { target.Folder, target.SupersededFolder }) RemoveEmptyTree(root, keepRoot: root == target.Folder);
                if (e is OperationCanceledException) throw;
                throw new LibraryException($"The data could not be moved to “{target.Folder}”: {Plain(e)} Lux keeps using “{from.Folder}”.", e);
            }
        }
        job.Report(0.92, "Removing the old copies");
        foreach (var (src, _) in files)
            foreach (var s in new[] { "", "-wal", "-shm", "-journal" })
                try { if (File.Exists(src + s)) File.Delete(src + s); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace("could not delete " + src + s + ": " + e.Message); }
        RemoveEmptyTree(from.Folder, keepRoot: false);
        RemoveEmptyTree(from.SupersededFolder, keepRoot: false);
        var ids = Db.Read(c => c.Rows(null, "SELECT id FROM capture WHERE context_location=2 OR superseded IS NOT NULL", rr => rr.GetString(0)));
        Changes.Changed(RefreshContexts(ids, force: true));
        job.Note($"{files.Count} file{(files.Count == 1 ? "" : "s")} moved to {target.Folder}.");
        job.Result(target.Folder);
        job.Report(1, "Done");
    }, job.Cancel);

    static void RemoveEmptyTree(string root, bool keepRoot)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!keepRoot && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
