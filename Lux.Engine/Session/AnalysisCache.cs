using System.Globalization;
using System.Runtime.InteropServices;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Export;

namespace Lux.Engine.Session;

/// <summary>How much of a capture's analysis is kept in its cache. Each level includes the ones below it.</summary>
public enum CacheLevel
{
    /// <summary>Nothing kept.</summary>
    None = 0,
    /// <summary>The registration geometry and the full-resolution depth (<see cref="CaptureGeometry"/>): a later open skips the registration
    /// chain — about half of a full-size export — and every depth reader (depth effect, `jpg+depth`, depth map, focus) is immediate.</summary>
    Geometry = 1,
    /// <summary>Plus the 13-megapixel linear render: the level-1 fused tiles a 13 MP export reads, and the level-2–4 reference tiles of the
    /// previews. 13 MP exports and previews then run no fusion and no module ISP.</summary>
    Linear13 = 2,
    /// <summary>Plus the full-detail (level-0, super-resolved) linear tiles under the export window: a full-size export then runs only the
    /// output stage (vignetting, resampling, the output ISP, the writers).</summary>
    Full = 3,
}

/// <summary>
/// The engine's side of the analysis cache: what each tier holds, how its blocks are named and laid out, and how a build reads them back.
/// The bytes live in an <see cref="IAnalysisCache"/> (implemented by <c>Lux.Store</c>), which only stores, checksums and sizes them.
/// <para>Tiers and blocks (all values exactly as the pipeline produced them, so a render from the cache is the render itself):</para>
/// <list type="bullet">
///   <item><c>geometry</c>: <c>header</c> (<see cref="CaptureGeometry.SerializeHeader"/>) and <c>depth</c> (the W0×H0 float depth).</item>
///   <item><c>13mp</c>: PipelineCache tiles <c>L1/tx_ty</c> … <c>L4/tx_ty</c> (levels 1–4; none depends on the level the state was built for),
///   each the 512×512 round-toward-zero half RGB tile.</item>
///   <item><c>full</c>: PipelineCache tiles <c>L0/tx_ty</c> overlapping the level-0 export window.</item>
/// </list>
/// <para><see cref="EngineCacheVersion"/> names this layout and the pipeline that fills it: bump it whenever either changes in a way that
/// could change a stored value — every cache written before is then discarded instead of read.</para>
/// </summary>
public static class AnalysisCache
{
    /// <summary>The cache format and pipeline version. A cache recorded with another value is stale and is rebuilt, never rendered from.</summary>
    public const int EngineCacheVersion = 2;   // 2: level-1 tiles with the DB source-frame black at every build level (1 held a level-1 build's estimate tiles)

    public const string GeometryTier = "geometry", LinearTier = "13mp", FullTier = "full";

    public static string TierName(CacheLevel level) => level switch
    {
        CacheLevel.Geometry => GeometryTier,
        CacheLevel.Linear13 => LinearTier,
        CacheLevel.Full => FullTier,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>The user-facing name of a level (`geometry`, `13mp`, `full`, `none`).</summary>
    public static string Name(CacheLevel level) => level == CacheLevel.None ? "none" : TierName(level);

    public static bool TryParse(string s, out CacheLevel level)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "none": case "0": level = CacheLevel.None; return true;
            case "geometry": case "depth": case "geometry+depth": case "1": level = CacheLevel.Geometry; return true;
            case "13mp": case "linear": case "2": level = CacheLevel.Linear13; return true;
            case "full": case "full-detail": case "3": level = CacheLevel.Full; return true;
            default: level = CacheLevel.None; return false;
        }
    }

    /// <summary>The highest level whose tier, and every tier below it, is complete.</summary>
    public static CacheLevel LevelOf(IAnalysisCache? cache)
    {
        if (cache is null) return CacheLevel.None;
        var level = CacheLevel.None;
        foreach (var l in new[] { CacheLevel.Geometry, CacheLevel.Linear13, CacheLevel.Full })
        {
            if (cache.CompleteDescriptor(TierName(l)) is null) break;
            level = l;
        }
        return level;
    }

    // ------------------------------------------------------------------------------------------------ geometry tier

    /// <summary>The cached geometry, or null when the tier is absent, incomplete or damaged (a damaged tier is dropped so it is rebuilt).</summary>
    public static CaptureGeometry? LoadGeometry(IAnalysisCache cache, Action<string>? log = null)
    {
        if (cache.CompleteDescriptor(GeometryTier) is null) return null;
        try
        {
            var header = cache.Read(GeometryTier, "header");
            if (header is null) return null;
            return CaptureGeometry.Deserialize(header, cache.Read(GeometryTier, "depth"));
        }
        catch (Exception e) when (e is CacheCorruptException or InvalidDataException)
        {
            log?.Invoke($"analysis cache: the geometry tier is damaged ({e.Message}); it is discarded and recomputed");
            cache.Drop(GeometryTier);
            return null;
        }
    }

    public static void SaveGeometry(IAnalysisCache cache, CaptureGeometry g)
    {
        cache.SetComplete(GeometryTier, null);
        cache.Write(GeometryTier, new[] { ("header", g.SerializeHeader()) }, 1);
        var d = g.SerializeDepth();
        if (d.Length > 0) cache.Write(GeometryTier, new[] { ("depth", d) }, 4);
        string desc = g.FullDepth is { } fd ? FormattableString.Invariant($"depth {fd.W}x{fd.H}, {g.Tele.Count} telephoto") : $"no depth, {g.Tele.Count} telephoto";
        cache.SetComplete(GeometryTier, desc);
    }

    // ------------------------------------------------------------------------------------------------ tile tiers

    /// <summary>The block key of a PipelineCache tile. No tile depends on the level its state was built for (the fusion uses the sensor DB
    /// black for the non-reference frames at every build level, as Lumen's export does), so <paramref name="buildLevel"/> is not part of it.</summary>
    public static string TileKey(int level, int tx, int ty, int buildLevel = 0) => FormattableString.Invariant($"L{level}/{tx}_{ty}");

    /// <summary>The tier a tile belongs to: level 0 → full detail, levels 1–4 → 13 MP.</summary>
    public static string? TierOfTile(int level, int buildLevel = 0) => level == 0 ? FullTier : LinearTier;

    /// <summary>The tiles a full-size export reads: every level-0 PipelineCache tile overlapping the level-0 export window.</summary>
    public static List<(int Tx, int Ty)> FullDetailTiles(ExportState st)
    {
        var win = Exporter.Resolve(st, new ExportRequest { Formats = Array.Empty<ExportImageFormat>() }).Window;
        var (ox, oy) = win.Origins[0]; var (w, h) = win.ExportDims[0];
        var (tx0, tx1, ty0, ty1) = st.Cache.TileRange(0, new RectI(ox, oy, ox + w, oy + h));
        var o = new List<(int, int)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) o.Add((tx, ty));
        return o;
    }

    /// <summary>Every tile of a level.</summary>
    public static List<(int Tx, int Ty)> AllTiles(PipelineCache pc, int level)
    {
        var (nx, ny) = pc.Grid[level]; var o = new List<(int, int)>();
        for (int ty = 0; ty < ny; ty++) for (int tx = 0; tx < nx; tx++) o.Add((tx, ty));
        return o;
    }

    /// <summary>Store tiles of a state that are in memory (generated or loaded); never generates. Returns how many were written.</summary>
    public static int SaveTiles(IAnalysisCache cache, ExportState st, int level, IEnumerable<(int Tx, int Ty)> tiles)
    {
        if (!st.Noise.IsLumen) throw new InvalidOperationException("the tiles of a state with noise controls are not Lumen's tiles: they cannot go into the analysis cache");
        string? tier = TierOfTile(level, st.BuildLevel);
        if (tier is null) return 0;
        var have = new HashSet<string>(cache.Keys(tier));
        var batch = new List<(string, byte[])>();
        int n = 0;
        void Flush() { if (batch.Count > 0) { cache.Write(tier, batch, 2); n += batch.Count; batch.Clear(); } }
        foreach (var (tx, ty) in tiles)
        {
            string key = TileKey(level, tx, ty, st.BuildLevel);
            if (have.Contains(key)) continue;
            if (st.Cache.TryGetTile(level, tx, ty) is not { } t) continue;
            batch.Add((key, MemoryMarshal.AsBytes(t.AsSpan()).ToArray()));
            if (batch.Count >= 24) Flush();
        }
        Flush();
        return n;
    }

    /// <summary>The kept tiles of the tile tiers, as a PipelineCache source for a state built at <paramref name="buildLevel"/>. A block that
    /// fails its checksum is reported once through <paramref name="log"/>, recorded in <see cref="CachedTiles.Damaged"/> and generated
    /// afresh by the caller — it is never rendered.</summary>
    public static CachedTiles TileSource(IAnalysisCache cache, int buildLevel, Action<string>? log = null) => new(cache, buildLevel, log);

    public sealed class CachedTiles : ITileSource
    {
        readonly IAnalysisCache _cache; readonly int _buildLevel; readonly Action<string>? _log;
        readonly HashSet<string> _linear, _full;
        readonly HashSet<string> _damagedTiers = new();
        public CachedTiles(IAnalysisCache cache, int buildLevel, Action<string>? log)
        {
            _cache = cache; _buildLevel = buildLevel; _log = log;
            _linear = new HashSet<string>(cache.Keys(LinearTier)); _full = new HashSet<string>(cache.Keys(FullTier));
        }
        /// <summary>Record that a tile has just been stored (so it is served from the cache if it is dropped from memory).</summary>
        public void Added(int level, int tx, int ty)
        {
            var set = SetOf(TierOfTile(level, _buildLevel));
            if (set is not null) lock (set) set.Add(TileKey(level, tx, ty, _buildLevel));
        }

        /// <summary>Read the kept tiles' keys again: tiers another session wrote (a library job) or released since this source was made are
        /// used from this call on. Pixels never depend on it — a kept tile holds the pipeline's own values and a tile not kept is generated.
        /// Without it, a photo that is open while its full detail is made keeps generating level 0.</summary>
        public void Refresh()
        {
            var lin = _cache.Keys(LinearTier); var full = _cache.Keys(FullTier);
            lock (_linear) { _linear.Clear(); _linear.UnionWith(lin); }
            lock (_full) { _full.Clear(); _full.UnionWith(full); }
        }

        /// <summary>Tiers in which a block failed its checksum during this session.</summary>
        public IReadOnlyCollection<string> Damaged { get { lock (_damagedTiers) return _damagedTiers.ToArray(); } }
        /// <summary>Tiles served from the cache.</summary>
        public int Served => _served;
        int _served;

        HashSet<string>? SetOf(string? tier) => tier == FullTier ? _full : tier == LinearTier ? _linear : null;

        public bool Has(int level, int tx, int ty)
        {
            var set = SetOf(TierOfTile(level, _buildLevel));
            if (set is null) return false;
            lock (set) return set.Contains(TileKey(level, tx, ty, _buildLevel));
        }

        public ushort[]? Load(int level, int tx, int ty)
        {
            string? tier = TierOfTile(level, _buildLevel); var set = SetOf(tier);
            if (tier is null || set is null) return null;
            string key = TileKey(level, tx, ty, _buildLevel);
            lock (set) if (!set.Contains(key)) return null;
            byte[]? b;
            try { b = _cache.Read(tier, key); }
            catch (CacheCorruptException e)
            {
                lock (set) set.Remove(key);
                bool first; lock (_damagedTiers) first = _damagedTiers.Add(tier);
                if (first) _log?.Invoke($"analysis cache: {e.Message}; the tile is recomputed and the {tier} tier will be rebuilt");
                return null;
            }
            if (b is null || b.Length % 6 != 0) return null;
            var t = new ushort[b.Length / 2];
            Buffer.BlockCopy(b, 0, t, 0, b.Length);
            Interlocked.Increment(ref _served);
            return t;
        }
    }

    /// <summary>A short description of the tile tiers' coverage, stored as the tier's completion descriptor.</summary>
    public static string Describe(string tier, int tiles) => string.Create(CultureInfo.InvariantCulture, $"{tier}: {tiles} tiles");
}
