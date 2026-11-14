using Lux.Engine.Session;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    static CacheLevel LevelFor(ViewQuality q) => q switch { ViewQuality.FullDetail => CacheLevel.Full, ViewQuality.Linear13 => CacheLevel.Linear13, _ => CacheLevel.None };

    /// <summary>The viewer's quality readiness: Preview is always ready (levels 2–4 are made on demand from the reference camera); 13 MP and full
    /// detail are ready when the cache holds them, being processed while a job raising the cache to (at least) them waits or runs.</summary>
    public QualityState GetQualityState(string captureId, ViewQuality quality)
    {
        var item = Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var need = LevelFor(quality);
        var est = EstimateCache(new[] { captureId }, need == CacheLevel.None ? item.CacheLevel : need >= item.CacheLevel ? need : need);
        if (quality == ViewQuality.Preview || item.CacheLevel >= need)
            return new QualityState(captureId, quality, QualityReadiness.Ready, null, 1, null, item.BestQuality, new CacheEstimate(0, 0, 0, 0), false);
        var job = Queue.JobsOf(captureId).Where(j => Reaches(j, need)).OrderBy(j => j.State == JobState.Running ? 0 : 1).FirstOrDefault();
        var add = EstimateCache(new[] { captureId }, need);
        bool ask = add.BytesToAdd > 0 && !SettingsImpl.DontAskAboutExtraStorage;
        return job is null
            ? new QualityState(captureId, quality, QualityReadiness.NotStarted, null, 0, null, item.BestQuality, add, ask)
            : new QualityState(captureId, quality, QualityReadiness.Processing, job.Id, job.Progress, job.Status, item.BestQuality, add, false);
    }

    static bool Reaches(JobInfo j, CacheLevel need) => j.Kind switch
    {
        JobKind.Analysis => need <= CacheLevel.Linear13,
        JobKind.CacheBuild => need <= CacheLevel.Linear13,
        JobKind.CacheUpgrade => true,
        _ => false,
    };

    /// <summary>One-click quality for viewing: raise the cache with an interactive-priority job (an existing job is moved to the front), or lower
    /// it (release) when the quality is below what is kept.</summary>
    public QualityState RequestQuality(string captureId, ViewQuality quality)
    {
        var item = Get(captureId) ?? throw new LibraryException("The photo is no longer in the library.");
        var need = LevelFor(quality);
        if (quality != ViewQuality.Preview && item.CacheLevel >= need && item.CacheLevel > need)
        {
            Queue.Enqueue(new CacheJobRequest(captureId, need) { Release = true, Priority = 0 });   // downgrade: release what is above
            return GetQualityState(captureId, quality);
        }
        if (quality == ViewQuality.Preview)
        {
            if (item.CacheLevel > CacheLevel.Geometry) Queue.Enqueue(new CacheJobRequest(captureId, CacheLevel.Geometry) { Release = true, Priority = 0 });
            return GetQualityState(captureId, quality);
        }
        if (item.CacheLevel >= need) return GetQualityState(captureId, quality);
        var existing = Queue.JobsOf(captureId).FirstOrDefault(j => Reaches(j, need));
        if (existing is not null) { if (existing.State == JobState.Waiting) Queue.SetPriority(existing.Id, 0); }
        else Queue.Enqueue(need == CacheLevel.Full ? new CacheJobRequest(captureId, CacheLevel.Full) { Priority = 0 } : new AnalysisJobRequest(captureId, need) { Priority = 0 });
        return GetQualityState(captureId, quality);
    }
}
