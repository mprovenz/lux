using Lux.Engine.Pipeline;

namespace Lux.Engine.Session;

/// <summary>
/// The memory budget of a full-size (level-0) render (<see cref="ExportRequest.MemoryBudgetBytes"/>). It changes the schedule, never a
/// value: every tile is still a pure function of the capture, so the output is byte-identical with or without a budget.
///
/// <para>Where a full-size export's memory goes (24 threads, live heap after a full collection at each phase start):
/// the registration chain peaks at ~2.5 GB, of which nothing is needed after its geometry is extracted (if the whole
/// registration state stayed referenced for the rest of the export, it would add 2.3 GB live, and with the server collector's
/// budgets scaling with the heap the process peak would double to 11–14 GB); the level-0 inputs (fusion, reference and telephoto caches) ~1.0–1.7 GB live; the
/// level-0 tiles 0.35 GB; the JPEG's RGBA8 0.2 GB. Without a budget the process holds about twice the live heap in not-yet-collected
/// tile scratch.</para>
/// <para>Under a budget the level-0 tiles of the export are made first, in bands of tile rows, with a collection between bands whenever the
/// process is above 85 % of the budget; then the level-0 inputs are released (<see cref="ExportState.ReleaseLevel0Inputs"/>) before the
/// writers run; and a watchdog collects whenever the working set passes 90 % of the budget. The registration chain itself (~2.5 GB) is the
/// floor — the analysis cache's geometry tier removes it.</para>
/// </summary>
public static class MemoryBudget
{
    /// <summary>The unit of the budget and of every figure here: 2³⁰ bytes, as /usr/bin/time and the process status report them.</summary>
    public const double Gb = 1L << 30;

    /// <summary>Workers for the level-0 inputs and tiles under a budget: each tile in flight holds ~0.1 GB of scratch on top of the ~2.2 GB the
    /// inputs and the made tiles need (3 GB is met with 8 workers, 4 GB with 18).</summary>
    public static int Level0Threads(long budget, int threads) => Math.Clamp((int)((budget / Gb - 2.2) / 0.1), 2, Math.Max(2, threads));

    /// <summary>Collect (blocking, compacting, returning memory to the system) when the process is above <paramref name="fraction"/> of the budget.</summary>
    public static bool CollectIfOver(long budget, double fraction)
    {
        if (Environment.WorkingSet <= (long)(budget * fraction)) return false;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return true;
    }

    /// <summary>A background watchdog for the duration of a render: collects whenever the working set passes 90 % of the budget (at most four
    /// times a second, backing off when a collection does not get below it). The collector is process-wide, so this is for a process that renders one capture at a time (the CLI).</summary>
    public static IDisposable Watch(long budget)
    {
        var cts = new CancellationTokenSource();
        var t = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                int wait = 250;
                try
                {
                    // still over right after a collection = the live data itself is near the budget: back off rather than collect continuously
                    if (CollectIfOver(budget, 0.90) && Environment.WorkingSet > (long)(budget * 0.90)) wait = 2000;
                }
                catch (Exception) { }
                cts.Token.WaitHandle.WaitOne(wait);
            }
        }) { IsBackground = true, Name = "lux memory budget" };
        t.Start();
        return new Stop(cts);
    }

    sealed class Stop : IDisposable
    {
        readonly CancellationTokenSource _cts; public Stop(CancellationTokenSource c) { _cts = c; }
        public void Dispose() { _cts.Cancel(); }
    }

    /// <summary>Make every level-0 PipelineCache tile an export of <paramref name="s"/> can read — the level-0 export window grown by one tile on
    /// each side, which covers the float writers' resampling margins, the display render's grown tiles and the depth effect's fetch — in
    /// bands of tile rows, collecting between bands when over 85 % of the budget; then release the level-0 inputs. A tile the export later
    /// asks for outside that area is still made correctly (from freshly made inputs), only slower.</summary>
    public static void PrepareLevel0(ExportSession s, long budget, int threads, CancellationToken cancel, Action<string>? log)
    {
        // fewer workers when the budget is tight (Level0Threads)
        threads = Level0Threads(budget, threads);
        var st = s.State; var win = s.Grid.Window; var pd = win.PipelineDims[0];
        var (ox, oy) = win.Origins[0]; var (ew, eh) = win.ExportDims[0]; int m = Pipeline.Cache.PipelineCache.TileSize;
        var area = new RectI(Math.Max(0, ox - m), Math.Max(0, oy - m), Math.Min(pd.W, ox + ew + m), Math.Min(pd.H, oy + eh + m));
        var (tx0, tx1, ty0, ty1) = st.Cache.TileRange(win.BaseLevel, area);
        int ts = Pipeline.Cache.PipelineCache.TileSize, collections = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int ty = ty0; ty <= ty1; ty += 2)
        {
            cancel.ThrowIfCancellationRequested();
            int ty2 = Math.Min(ty + 1, ty1);
            var band = new RectI(tx0 * ts, ty * ts, tx1 * ts + 1, ty2 * ts + 1);
            st.Cache.Prefetch(win.BaseLevel, band, threads, cancel);
            if (CollectIfOver(budget, 0.85)) collections++;
        }
        st.ReleaseLevel0Inputs();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        log?.Invoke($"memory budget {budget / Gb:F1} GB: level-0 tiles made in bands on {threads} threads ({(ty1 - ty0 + 2) / 2} bands, {collections} collections) in {sw.Elapsed.TotalSeconds:F1}s, inputs released; working set now {Environment.WorkingSet / Gb:F2} GB");
    }
}
