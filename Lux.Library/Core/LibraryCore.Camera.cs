namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    /// <summary>The camera page's "already imported" marking: every location of a capture still in the library (hidden captures excluded;
    /// locations on a drive not connected included), matched by file name (case-insensitive) and size.</summary>
    public IReadOnlyDictionary<string, string> FindByNameAndSize(IReadOnlyCollection<(string FileName, long Size)> files)
    {
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return hits;
        var want = files.Select(f => ILibrary.NameSizeKey(f.FileName, f.Size)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = Db.Read(c => c.Rows(null, "SELECT l.path, l.size, l.capture_id FROM location l JOIN capture c ON c.id=l.capture_id WHERE c.hidden=0",
                                       r => (Path: r.GetString(0), Size: r.GetInt64(1), Id: r.GetString(2))));
        foreach (var (path, size, id) in rows)
        {
            string k = ILibrary.NameSizeKey(Path.GetFileName(path), size);
            if (want.Contains(k)) hits.TryAdd(k, id);
        }
        return hits;
    }

    public IReadOnlyDictionary<string, string> FindByPaths(IReadOnlyCollection<string> paths)
    {
        var hits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in paths.Select(Path.GetFullPath).Distinct().Chunk(200))
        {
            var found = Db.Read(c => chunk.Select(p => (Path: p, Id: c.Scalar(null, "SELECT capture_id FROM location WHERE path=$p", ("$p", p)) as string)).ToList());
            foreach (var (p, id) in found) if (id is not null) hits[p] = id;
        }
        return hits;
    }
}
