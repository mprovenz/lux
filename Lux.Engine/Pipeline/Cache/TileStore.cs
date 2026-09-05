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
}
