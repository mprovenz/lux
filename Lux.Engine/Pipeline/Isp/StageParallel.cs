namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// Row parallelism inside one output-stage run. The display path renders its tiles in parallel already; when only a few
/// stage runs are in progress (a slider drag at a coarse level, one tile, the last tiles of a pass) the free threads take row bands of the
/// run instead. Every caller computes each output row from its inputs alone (or each band from a disjoint set of independent values), so the
/// pixels never depend on how the rows are split.
/// </summary>
public static class StageParallel
{
    static int _active;

    /// <summary>Mark a stage run as in progress for the duration of the returned scope.</summary>
    public static Scope Enter() { Interlocked.Increment(ref _active); return new Scope(); }
    public readonly struct Scope : IDisposable { public void Dispose() => Interlocked.Decrement(ref _active); }

    /// <summary>Whether a run should split its work now: fewer stage runs in progress than half the cores (the display renders its tiles in
    /// parallel; when they are fewer than the cores the idle threads take row bands; when every core has a run of its own nothing splits).</summary>
    public static bool Spread => Environment.ProcessorCount >= 4 && Volatile.Read(ref _active) <= Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary><paramref name="body"/>(y0, y1) over bands of [0, <paramref name="n"/>): on several threads when <see cref="Spread"/> and the
    /// work (<paramref name="n"/>·<paramref name="costPerRow"/>) is worth it, else in one call.</summary>
    public static void For(int n, long costPerRow, Action<int, int> body, int minRows = 8)
    {
        if (n < 2 * minRows || (long)n * costPerRow < 1 << 16 || !Spread) { body(0, n); return; }
        int bands = Math.Min(Environment.ProcessorCount * 2, Math.Max(1, n / minRows)), per = (n + bands - 1) / bands;
        if (bands < 2) { body(0, n); return; }
        Parallel.For(0, bands, b => { int y0 = b * per, y1 = Math.Min(n, y0 + per); if (y0 < y1) body(y0, y1); });
    }
}
