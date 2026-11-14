using System.Globalization;
using System.Text.RegularExpressions;
using Lux.Engine.Session;
using Lux.Store;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Core;

/// <summary>
/// Copies of photos' data that the library set aside after a conflict. A conflict happens when the data of a photo moves from the local store
/// to beside the photo and a different copy is already there. The copy beside the photo wins. The local copy moves to
/// <c>&lt;store&gt;-superseded/&lt;id&gt;-&lt;UTC time&gt;/edits|cache/&lt;id&gt;.ext</c> (<see cref="LocalContextStore.Reconcile"/>), and the library never
/// deletes it. The folder name and the file names give all that a restore needs: the photo (its id), the kind of data and the time.
/// Settings lists the copies. "Restore this copy" swaps a copy with the current data of the photo. The current data is set aside in turn,
/// so a restore can be undone the same way. "Remove" sends the files of a copy to the trash.
/// </summary>
internal sealed partial class LibraryCore
{
    static readonly Regex SetAsideName = new(@"^(?<id>[0-9A-Za-z]+)-(?<t>\d{8}T\d{6}Z)$", RegexOptions.CultureInvariant);
    static readonly (string Sub, string Ext)[] ContextKinds = { (ImageContext.EditsFolderName, ImageContext.EditsExtension), (ImageContext.CacheFolderName, AnalysisCacheStore.Extension) };

    public IReadOnlyList<SetAsideCopy> GetSetAsideCopies()
    {
        if (LocalContextStore.Current is not { } store || !Directory.Exists(store.SupersededFolder)) return Array.Empty<SetAsideCopy>();
        var list = new List<SetAsideCopy>();
        foreach (var dir in Directory.EnumerateDirectories(store.SupersededFolder))
            if (Describe(dir) is { } c) list.Add(c);
        return list.OrderByDescending(c => c.SetAsideUtc).ThenBy(c => c.PhotoName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Describes one set-aside folder. Returns null for a folder that is not a set-aside copy or that holds no data file.</summary>
    SetAsideCopy? Describe(string dir)
    {
        var m = SetAsideName.Match(Path.GetFileName(dir));
        if (!m.Success) return null;
        string id = m.Groups["id"].Value;
        if (!DateTime.TryParseExact(m.Groups["t"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) return null;
        bool edits = File.Exists(ContextProbe.EditsPathIn(dir, id)), cache = File.Exists(ContextProbe.CachePathIn(dir, id));
        if (!edits && !cache) return null;
        long bytes = 0;
        try { bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var row = CtxRows(new[] { id }).FirstOrDefault();
        string? name = row is null ? null : Db.Read(c => c.Scalar(null, "SELECT name FROM capture WHERE id=$i", ("$i", id)) as string);
        return new SetAsideCopy
        {
            Path = dir, CaptureId = id, PhotoName = name, SetAsideUtc = DateTime.SpecifyKind(when, DateTimeKind.Utc), Bytes = bytes, HasEdits = edits, HasAnalysis = cache,
            CanRestore = row is not null && row.Dirs.Count > 0 && File.Exists(row.PrimaryPath),
        };
    }

    /// <summary>Gets the set-aside copy at <paramref name="path"/>. The path must be a folder directly in the set-aside folder of the current store.</summary>
    SetAsideCopy CopyAt(string path)
    {
        if (LocalContextStore.Current is not { } store) throw new LibraryException("There is no folder for data kept on this computer.");
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new LibraryException("That set-aside copy is no longer there."); }
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(store.SupersededFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new LibraryException("That folder is not a set-aside copy.");
        return (Directory.Exists(full) ? Describe(full) : null) ?? throw new LibraryException("That set-aside copy is no longer there.");
    }

    /// <summary>Says why the data of the photo cannot be swapped now: the photo is open, or work on it runs or waits in the Queue. Returns null
    /// when the swap can go ahead.</summary>
    string? BusyReason(string id, string name)
    {
        bool open;
        lock (_sessionsLock) open = _sessions.Any(s => s.CaptureId == id && !s.IsDisposed) || _animSources.Any(a => a.CaptureId == id && !a.IsDisposed);
        if (open) return $"{name} is open in Lux. Close it (go back to the Library), then restore the copy.";
        if (Queue.ActiveKindOf(id) is not null || Queue.JobsOf(id).Any(j => !j.IsFinished))
            return $"{name} has work in the Queue. Restore the copy when that work is done, or cancel it in the Queue.";
        return null;
    }

    public Task RestoreSetAsideCopyAsync(string path, CancellationToken cancel = default) => Task.Run(() =>
    {
        var copy = CopyAt(path);
        string id = copy.CaptureId, name = copy.PhotoName ?? "This photo";
        var row = CtxRows(new[] { id }).FirstOrDefault();
        if (row is null || row.Dirs.Count == 0 || !File.Exists(row.PrimaryPath))
            throw new LibraryException(copy.PhotoName is null
                ? "The photo this copy belongs to is not in the library, so the copy cannot be put back. You can remove it."
                : $"The file of {copy.PhotoName} is missing (its drive may not be connected), so the copy cannot be put back now.");
        if (BusyReason(id, name) is { } busy) throw new LibraryException(busy);
        var store = LocalContextStore.Current!;
        cancel.ThrowIfCancellationRequested();

        // find where each kind of data is in use now. A kind without data goes where the other data of the photo is. If the photo has no data,
        // it goes beside the photo when its folder can be written, else into the local store.
        var (ePath, cPath, _) = ResolveContext(id, row.Dirs);
        string primaryDir = Path.GetDirectoryName(row.PrimaryPath)!;
        string? inUseRoot = (ePath ?? cPath) is { } any ? Path.GetDirectoryName(Path.GetDirectoryName(any)!) : null;
        string defaultRoot = inUseRoot ?? (LocalContextStore.IsWritable(Directory.Exists(BesideRoot(primaryDir)) ? BesideRoot(primaryDir) : primaryDir) ? BesideRoot(primaryDir) : store.RootFor(id));

        // set the current data of the same kinds aside in a new set-aside folder, so that the same swap can undo this one
        var stamp = DateTime.UtcNow;
        string newAside;
        do { newAside = Path.Combine(store.SupersededFolder, $"{id}-{stamp:yyyyMMdd'T'HHmmss'Z'}"); stamp = stamp.AddSeconds(1); } while (Directory.Exists(newAside));
        var done = new List<(string From, string To)>();   // the moves made, for a roll-back
        SqliteConnection.ClearAllPools();
        try
        {
            foreach (var (sub, ext) in ContextKinds)
            {
                string kept = Path.Combine(copy.Path, sub, id + ext);
                if (!File.Exists(kept)) continue;
                string? current = sub == ImageContext.EditsFolderName ? ePath : cPath;
                string target = current ?? Path.Combine(defaultRoot, sub, id + ext);
                if (current is not null && File.Exists(current))
                {
                    LocalContextStore.SettleDatabase(current);
                    string aside = Path.Combine(newAside, sub, id + ext);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    LocalContextStore.MoveVerified(current, aside);
                    done.Add((current, aside));
                }
                LocalContextStore.SettleDatabase(kept);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                LocalContextStore.MoveVerified(kept, target);
                done.Add((kept, target));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // put everything back as it was
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(done[i].From)!); LocalContextStore.MoveVerified(done[i].To, done[i].From); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { Trace($"restore roll-back of {done[i].To}: {e2.Message}"); }
            }
            throw new LibraryException($"The copy of {name} could not be put back ({Plain(e)}). Nothing was changed.", e);
        }
        RemoveEmptyFolder(copy.Path);
        Trace($"{id[..12]}…: restored the copy set aside {copy.SetAsideUtc:u}; the data it replaced is in {newAside}");
        var changed = RefreshContexts(new[] { id }, force: true);
        Changes.Changed(changed.Count > 0 ? changed : new List<string> { id });
    }, cancel);

    public Task RemoveSetAsideCopyAsync(string path, CancellationToken cancel = default) => Task.Run(() =>
    {
        var copy = CopyAt(path);
        try
        {
            foreach (var f in Directory.EnumerateFiles(copy.Path, "*", SearchOption.AllDirectories).ToList())
                Trash.Remove(f, toTrash: true, Options.TrashDirectoryOverride);
            RemoveEmptyFolder(copy.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"The set-aside copy could not be removed ({Plain(e)}).", e);
        }
        var changed = RefreshContexts(new[] { copy.CaptureId }, force: false);
        if (changed.Count > 0) Changes.Changed(changed);
    }, cancel);

    static void RemoveEmptyFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir)) if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
