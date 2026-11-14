using System.Collections.Concurrent;

namespace Lux.Engine.Pipeline.Cache;

/// <summary>
/// A compute-once tile cache that is safe for concurrent requests: the first request for a key runs the factory, every
/// concurrent request for the same key waits for that one result, and a completed tile is never recomputed. Lumen's
/// `TileCache` has the same generate-on-miss contract on its render thread; this is that contract made re-entrant so an
/// export can ask for many tiles at once. Because a tile is a pure function of the capture, the contents do not depend
/// on which thread asked first. A factory exception stays with its entry, as with <see cref="Lazy{T}"/>.
/// </summary>
public sealed class TileStore<TKey, TValue> where TKey : notnull
{
    readonly ConcurrentDictionary<TKey, Lazy<TValue>> _entries = new();

    public TValue GetOrCreate(TKey key, Func<TKey, TValue> factory)
        => _entries.GetOrAdd(key, k => new Lazy<TValue>(() => factory(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>The tile if it has already been generated; never triggers a generation.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var l) && l.IsValueCreated) { value = l.Value; return true; }
        value = default!; return false;
    }

    public int Count => _entries.Count;

    /// <summary>Drop completed entries matching <paramref name="match"/> (an entry still being made is left alone). A dropped tile is
    /// made again, identically, by the next request for it. Returns how many were dropped.</summary>
    public int RemoveWhere(Func<TKey, bool> match)
    {
        int n = 0;
        foreach (var kv in _entries)
            if (kv.Value.IsValueCreated && match(kv.Key) && _entries.TryRemove(kv)) n++;
        return n;
    }

    /// <summary>The keys of the completed entries.</summary>
    public IEnumerable<TKey> CompletedKeys() { foreach (var kv in _entries) if (kv.Value.IsValueCreated) yield return kv.Key; }

    /// <summary>Drop every entry that is not completed — still being made, or failed (a factory exception stays with its entry). A caller that
    /// already waits on such an entry still gets its result; the next request makes the tile again. Used when an input of the factory changed
    /// while tiles were being made (the depth-edit snapshot swap of the depth-effect cache). Returns how many were dropped.</summary>
    public int RemovePending()
    {
        int n = 0;
        foreach (var kv in _entries)
            if (!kv.Value.IsValueCreated && _entries.TryRemove(kv)) n++;
        return n;
    }
}
