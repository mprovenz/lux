using Avalonia.Media.Imaging;

namespace Lux.App.Viewer;

/// <summary>
/// The viewer's tiles of one channel, bounded by a memory budget (least recently drawn go first; tiles drawn in the current frame are
/// never evicted, so the budget can be exceeded by at most one screen of tiles). UI thread only.
/// </summary>
public sealed class TileCache
{
    public sealed class Entry
    {
        public required TileKey Key { get; init; }
        public required Bitmap Bitmap { get; init; }
        /// <summary>The content version it shows (raised when the library says its region did not change).</summary>
        public required long Version { get; set; }
        public required long Bytes { get; init; }
        public long ReadyTicks { get; init; }
        public long LastFrame { get; set; }
        public bool Drawn { get; set; }
        /// <summary>When the cache took it (the cross-fade of a revealed tile starts here).</summary>
        public long AcceptedTicks { get; init; }
        /// <summary>The tile of the generation on screen that this one replaced, kept while the new generation is held back (the tile-swap
        /// policy); null otherwise.</summary>
        public Entry? Previous { get; internal set; }
        internal LinkedListNode<Entry>? Node;
    }

    readonly Dictionary<TileKey, Entry> _map = new();
    readonly LinkedList<Entry> _lru = new();
    long _bytes;

    public TileCache(long budgetBytes) => BudgetBytes = budgetBytes;

    public long BudgetBytes { get; set; }
    public long Bytes => _bytes;
    public int Count => _map.Count;
    /// <summary>The highest byte count reached after an eviction pass (tests: the memory bound).</summary>
    public long PeakBytes { get; private set; }
    /// <summary>Arrivals dropped because they were older than what the cache holds or than the source's floor.</summary>
    public int DroppedStale { get; private set; }
    public int Evicted { get; private set; }
    public int Accepted { get; private set; }

    public Entry? Get(TileKey key) => _map.GetValueOrDefault(key);

    /// <summary>Tiles of this version or older are kept as <see cref="Entry.Previous"/> when a newer one replaces them (the generation on
    /// screen, while the new one is held back); 0 = no keeping.</summary>
    public long KeepVersion { get; set; }
    /// <summary>Bytes held by kept previous tiles.</summary>
    public long PreviousBytes { get; private set; }

    /// <summary>Drop every kept previous tile (the new generation is on screen).</summary>
    public void ReleasePrevious()
    {
        foreach (var e in _map.Values)
            if (e.Previous is { } p) { e.Previous = null; PreviousBytes -= p.Bytes; p.Bitmap.Dispose(); }
        PreviousBytes = Math.Max(0, PreviousBytes);
    }
    public IEnumerable<Entry> Entries => _map.Values;

    /// <summary>Take a tile: dropped (and its bitmap disposed) when it is older than <paramref name="floor"/> or than the tile already held
    /// for its key; otherwise it replaces that tile. Returns whether it was taken.</summary>
    public bool Add(TileImage t, long floor, long frame)
    {
        var key = t.Key;
        _map.TryGetValue(key, out var old);
        if (t.Version < floor || (old != null && old.Version >= t.Version))
        {
            DroppedStale++;
            t.Bitmap.Dispose();
            return false;
        }
        Entry? previous = null;
        if (old != null)
        {
            if (KeepVersion > 0 && t.Version > KeepVersion && old.Version <= KeepVersion)
            {
                // keep the on-screen generation's tile under the new one (not disposed); an older kept one goes
                if (old.Previous is { } pp) { PreviousBytes -= pp.Bytes; pp.Bitmap.Dispose(); old.Previous = null; }
                Detach(old);
                previous = old;
                PreviousBytes += old.Bytes;
            }
            else
            {
                if (KeepVersion > 0 && old.Previous is { } kept) { previous = kept; old.Previous = null; }
                Remove(old);
            }
        }
        var size = t.Bitmap.PixelSize;
        var e = new Entry
        {
            Key = key, Bitmap = t.Bitmap, Version = t.Version, Bytes = (long)size.Width * size.Height * 4, ReadyTicks = t.ReadyTicks, LastFrame = frame,
            AcceptedTicks = System.Diagnostics.Stopwatch.GetTimestamp(), Previous = previous,
        };
        e.Node = _lru.AddFirst(e);
        _map[key] = e;
        _bytes += e.Bytes;
        Accepted++;
        return true;
    }

    /// <summary>The tile is drawn in <paramref name="frame"/>: most recently used, and pinned for that frame.</summary>
    public void Touch(Entry e, long frame)
    {
        e.LastFrame = frame;
        if (e.Node != null && e.Node != _lru.First)
        {
            _lru.Remove(e.Node);
            _lru.AddFirst(e.Node);
        }
    }

    /// <summary>Evict least recently drawn tiles until under budget, sparing those drawn in <paramref name="currentFrame"/>.</summary>
    public void Trim(long currentFrame)
    {
        var node = _lru.Last;
        while (_bytes > BudgetBytes && node != null)
        {
            var prev = node.Previous;
            if (node.Value.LastFrame < currentFrame) { Remove(node.Value); Evicted++; }
            node = prev;
        }
        PeakBytes = Math.Max(PeakBytes, _bytes);
    }

    /// <summary>Out of the map and the LRU, bitmap kept (it becomes a kept previous tile).</summary>
    void Detach(Entry e)
    {
        if (e.Node != null) _lru.Remove(e.Node);
        e.Node = null;
        _map.Remove(e.Key);
        _bytes -= e.Bytes;
    }

    void Remove(Entry e)
    {
        Detach(e);
        if (e.Previous is { } p) { e.Previous = null; PreviousBytes -= p.Bytes; p.Bitmap.Dispose(); }
        // the render scene keeps its own reference to a bitmap it is still drawing (Avalonia ref-counts the platform image)
        e.Bitmap.Dispose();
    }

    public void Clear()
    {
        foreach (var e in _map.Values.ToList()) Remove(e);
    }
}
