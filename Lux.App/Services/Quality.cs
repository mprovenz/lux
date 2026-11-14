using Lux.Library;

namespace Lux.App.Services;

/// <summary>Where a capture stands: the best quality available now (<see cref="CaptureItem.BestQuality"/>), and the one a waiting / running
/// job is bringing it to.</summary>
public sealed record QualityStatus(ViewQuality Cached, ViewQuality? Target, JobState? JobState, double Progress, long? JobId)
{
    public bool IsPending => Target != null;
    public bool IsRunning => Target != null && JobState == Lux.Library.JobState.Running;
}

/// <summary>The quality names and the pending target of a capture: from the queue's live job of the capture when the app has it (the
/// Queue's events, so progress moves smoothly), else from the item's own active-job fields.</summary>
public static class QualityRules
{
    public static string Name(ViewQuality q) => q switch { ViewQuality.FullDetail => "Full detail", ViewQuality.Linear13 => "13 MP", _ => "Preview" };

    public static QualityStatus Status(CaptureItem item, (JobKind Kind, JobState State, double Progress, long Id)? job)
    {
        var cached = item.BestQuality;
        job ??= item.ActiveJob is JobKind k && item.ActiveJobState is JobState st && item.ActiveJobId is long id ? (k, st, item.ActiveJobProgress, id) : null;
        ViewQuality? target = job?.Kind switch
        {
            JobKind.CacheUpgrade => ViewQuality.FullDetail,
            JobKind.Analysis or JobKind.CacheBuild => cached < ViewQuality.Linear13 ? ViewQuality.Linear13 : null,
            JobKind.CacheRelease => cached > ViewQuality.Linear13 ? ViewQuality.Linear13 : ViewQuality.Preview,
            _ => null,
        };
        return target == null ? new QualityStatus(cached, null, null, 0, null) : new QualityStatus(cached, target, job!.Value.State, job.Value.Progress, job.Value.Id);
    }
}
