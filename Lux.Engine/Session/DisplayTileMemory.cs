using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Session;

/// <summary>
/// The display render's per-tile memory inside a <see cref="CaptureSession"/>: for each export tile (the 512 grid of the JPEG export, the last
/// tile of a row / column absorbing the remainder) its grown linear tile and the <see cref="TileStageCache"/> of <see cref="DisplayAdjust"/>,
/// so that a slider change re-runs only the output stages after the first one it affects. Bounded by bytes, least recently used first out.
/// Everything kept is a pure function of the capture, the tile and the settings, so keeping or dropping an entry never changes a pixel.
/// </summary>
sealed class DisplayTileMemory
{
    /// <summary>Everything a tile's linear input depends on besides the tile itself.</summary>
    /// <param name="DepthGen">The session's depth generation for a depth-effect tile (0 otherwise): a depth change makes the kept input of every
    /// depth-effect tile unreachable (it is fetched again from the depth-effect cache, which kept the unchanged tiles).</param>
    public readonly record struct Key(ExportState State, int Level, int Tx, int Ty, float? DepthFNumber, float? FocusMm, bool SavedEdits, bool ToneV2, long DepthGen = 0);

    public sealed class Entry
    {
        public Image<Vec4F>? Linear;
        public DisplayRender.TileGeometry Geometry;
        public readonly TileStageCache Stages = new();
        public readonly object Gate = new();
        internal long Bytes;
        internal LinkedListNode<Key>? Node;
    }

    readonly long _budget;
    readonly object _lock = new();
    readonly Dictionary<Key, Entry> _map = new();
    readonly LinkedList<Key> _lru = new();
    long _bytes;

    public DisplayTileMemory(long budgetBytes) { _budget = budgetBytes; }

    public long Bytes { get { lock (_lock) return _bytes; } }

    public Entry Get(Key k)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(k, out var e)) { _lru.Remove(e.Node!); _lru.AddFirst(e.Node!); return e; }
            e = new Entry(); e.Node = _lru.AddFirst(k); _map[k] = e;
            return e;
        }
    }

    /// <summary>Re-count an entry after a render and evict the least recently used others while over the budget.</summary>
    public void Account(Entry e)
    {
        long b = (e.Linear is { } l ? (long)l.Width * l.Height * 16 : 0) + e.Stages.Bytes;
        lock (_lock)
        {
            if (e.Node is null || e.Node.List is null) return;   // evicted while it rendered
            _bytes += b - e.Bytes; e.Bytes = b;
            while (_bytes > _budget && _lru.Last is { } last && last != e.Node)
            {
                var victim = _map[last.Value];
                _map.Remove(last.Value); _lru.RemoveLast(); _bytes -= victim.Bytes; victim.Node = null;
            }
        }
    }

    public void Clear() { lock (_lock) { _map.Clear(); _lru.Clear(); _bytes = 0; } }
}
