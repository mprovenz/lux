using System.Diagnostics;
using System.Globalization;
using Lux.Store;

namespace Lux.Cli;

/// <summary>
/// `lux-light cache &lt;lri…&gt; [--level none|geometry|13mp|full]` — a capture's analysis cache (the `cache/` folder of the
/// `LuxImageContext` next to it): show what is kept, or raise / lower it to a level. The cache only ever saves work: an export from a
/// cached capture is byte-identical to one computed from scratch.
/// </summary>
public static class CacheCmd
{
    static string Mb(long bytes) => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    public static int Run(List<string> files, Options o)
    {
        CacheLevel? target = null;
        if (o.LevelText is string lt)
        {
            if (!AnalysisCache.TryParse(lt, out var parsed) || !o.LevelIsName && lt is not ("0" or "1" or "2" or "3"))
            { Console.Error.WriteLine($"error: --level for cache takes none, geometry, 13mp or full (not '{lt}')"); return 2; }
            target = parsed;
        }
        int threads = o.RenderThreads ?? Environment.ProcessorCount;
        Lux.Engine.Pipeline.BayerFusion.BlockFlow.Threads = threads;
        int rc = 0;
        foreach (var path in files)
        {
            Console.WriteLine(Path.GetFileName(path));
            var ctx = new StoreCaptureContext(path, mayCreateFolder: target is not null and not CacheLevel.None);
            try
            {
                if (target is CacheLevel want)
                {
                    using var s = CaptureSession.Open(path, new SessionOptions { Context = ctx, Threads = threads, KeepGeometry = true, MemoryBudgetBytes = o.MemoryBudgetGb is double gb ? (long)(gb * (1L << 30)) : null });
                    var before = s.CacheLevel;
                    var sw = Stopwatch.StartNew();
                    if (want > before)
                    {
                        Console.WriteLine($"  raising the cache from {AnalysisCache.Name(before)} to {AnalysisCache.Name(want)} …");
                        s.EnsureCacheAsync(want).GetAwaiter().GetResult();
                    }
                    else if (want < before)
                    {
                        Console.WriteLine($"  lowering the cache from {AnalysisCache.Name(before)} to {AnalysisCache.Name(want)}");
                        s.ReleaseCache(want + 1);
                    }
                    else Console.WriteLine($"  the cache is already at {AnalysisCache.Name(want)}");
                    if (want != before) Console.WriteLine($"  done in {sw.Elapsed.TotalSeconds:F1}s");
                }
                Show(ctx);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or CacheCorruptException)
            {
                rc = 1; Console.WriteLine($"  error: {e.Message}");
            }
        }
        return rc;
    }

    static void Show(StoreCaptureContext ctx)
    {
        var ic = ctx.Context;
        if (!Directory.Exists(ic.CacheFolder) || !File.Exists(AnalysisCacheStore.PathOf(ic)))
        {
            int? stale = File.Exists(AnalysisCacheStore.PathOf(ic)) ? AnalysisCacheStore.RecordedEngineVersion(ic) : null;
            Console.WriteLine("  cache level  none (nothing kept)");
            return;
        }
        int? recorded = AnalysisCacheStore.RecordedEngineVersion(ic);
        if (recorded is int rv && rv != AnalysisCache.EngineCacheVersion)
        {
            Console.WriteLine($"  cache        written by an older version of the engine ({rv}); it is discarded and rebuilt when used");
            return;
        }
        using var c = ctx.OpenCache(AnalysisCache.EngineCacheVersion, create: false);
        if (c is null) { Console.WriteLine("  cache level  none (nothing kept)"); return; }
        var level = AnalysisCache.LevelOf(c);
        Console.WriteLine($"  cache level  {AnalysisCache.Name(level)}");
        Console.WriteLine($"  file         {c.Location} ({Mb(((AnalysisCacheStore)c).FileBytes)})");
        Console.WriteLine($"  capture      sha-256 {ic.ContentHash}");
        var stats = c.Stats().ToDictionary(t => t.Tier);
        foreach (var (lv, what) in new[] { (CacheLevel.Geometry, "registration geometry + full-resolution depth"), (CacheLevel.Linear13, "13 MP linear render (level 1) + previews (levels 2-4)"), (CacheLevel.Full, "full-detail linear render (level 0, export window)") })
        {
            string tier = AnalysisCache.TierName(lv);
            if (!stats.TryGetValue(tier, out var t)) { Console.WriteLine($"  {tier,-12} -  {what}: not kept"); continue; }
            double ratio = t.StoredBytes > 0 ? (double)t.RawBytes / t.StoredBytes : 0;
            Console.WriteLine($"  {tier,-12} {(t.Complete ? "complete" : "PARTIAL ")}  {what}: {t.Blocks} blocks, {Mb(t.StoredBytes)} stored for {Mb(t.RawBytes)} ({ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1)");
        }
    }
}
