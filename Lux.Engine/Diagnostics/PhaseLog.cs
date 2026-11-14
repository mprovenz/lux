using System.Collections.Concurrent;
using System.Diagnostics;

namespace Lux.Engine.Diagnostics;

/// <summary>Phase timings for latency work (tests only): off unless switched on; then each timed phase appends (label, ms since start, ms taken,
/// thread). Free when off.</summary>
public static class PhaseLog
{
    public static volatile bool On = EnvOn();

    /// <summary>`LUX_PHASELOG=1` (diagnostic): on from the start, and the sum / count / max of every label written to stderr at exit.</summary>
    static bool EnvOn()
    {
        if (Environment.GetEnvironmentVariable("LUX_PHASELOG") != "1") return false;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var g in Log.GroupBy(e => e.Label).OrderByDescending(g => g.Sum(e => e.Ms)))
                Console.Error.WriteLine($"  phaselog {g.Sum(e => e.Ms),10:0} ms  ×{g.Count(),-6} max {g.Max(e => e.Ms),8:0.0}  {g.Key}");
        };
        return true;
    }
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static readonly ConcurrentQueue<(string Label, double At, double Ms, int Thread)> Log = new();

    public static void Clear() { while (Log.TryDequeue(out _)) { } }
    public static double Now => Clock.Elapsed.TotalMilliseconds;

    public readonly struct Scope : IDisposable
    {
        readonly string? _label; readonly double _t0;
        internal Scope(string label) { _label = label; _t0 = Now; }
        public void Dispose() { if (_label is not null) Log.Enqueue((_label, _t0, Now - _t0, Environment.CurrentManagedThreadId)); }
    }

    /// <summary>`using (PhaseLog.Time("phase")) …`.</summary>
    public static Scope Time(string label) => On ? new Scope(label) : default;
    /// <summary>A label built only when on.</summary>
    public static Scope Time(Func<string> label) => On ? new Scope(label()) : default;
    public static void Add(string label, double at, double ms) { if (On) Log.Enqueue((label, at, ms, Environment.CurrentManagedThreadId)); }
}
