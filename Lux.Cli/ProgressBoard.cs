using System.Runtime.InteropServices;
using System.Text;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;

namespace Lux.Cli;

/// <summary>
/// Console progress for `convert`: one line per input in flight — its phase, how far through it, elapsed and an estimate of
/// the time left — plus a line for the whole job, redrawn in place while the terminal allows it, and plain per-phase lines
/// otherwise (a redirected stderr, `LUX_VERBOSE=1`, `LUX_NO_PROGRESS=1`, a dumb terminal). The engine reports phases and
/// unit counts (<see cref="ProgressReporter"/>); the estimates come from a cost model of each phase — serial seconds plus
/// thread-seconds per unit, from the profile of a full-size export — scaled by how the phases already finished compared to
/// their nominal cost on this machine. Nothing here touches the pixels.
/// </summary>
internal sealed class ProgressBoard : IDisposable
{
    // ---- cost model ---------------------------------------------------------------------------------------------------
    /// <param name="Serial">seconds that do not scale with threads</param>
    /// <param name="ThreadSecondsPerUnit">CPU seconds per unit, divided by the render threads</param>
    /// <param name="SerialPerUnit">seconds per unit on one thread (the JPEG entropy coder)</param>
    /// <param name="NominalUnits">the unit count assumed before the phase reports its own</param>
    internal sealed record PhaseCost(double Serial, double ThreadSecondsPerUnit, double SerialPerUnit, int NominalUnits);

    // 2026-09-02 profile: L16_00466, 8320×6240, 24 render threads (see the vault's performance assessment)
    static readonly Dictionary<string, PhaseCost> Costs = new(StringComparer.Ordinal)
    {
        ["loading"] = new(2, 0, 0, 1),
        ["registration"] = new(46, 0, 0, 14),
        ["fusion set-up"] = new(2, 2.2, 0, 4),
        ["telephoto set-up"] = new(0, 0.6, 0, 5),
        ["level-0 inputs"] = new(13, 0.55, 0, 400),
        ["level-0 tiles"] = new(0, 1.1, 0, 234),
        ["render tiles"] = new(0, 0.05, 0, 200),
        ["dng"] = new(0.5, 0.03, 0, 825),
        ["jpeg tiles"] = new(0, 0.44, 0, 192),
        ["jpeg encode"] = new(0, 0, 0.015, 390),
        ["hdr write"] = new(5, 0, 0, 1),
        ["ppm write"] = new(4, 0, 0, 1),
        ["depth"] = new(2, 0, 0, 1),
        ["lens-frames"] = new(0, 2.5, 0, 10),
        ["parallax"] = new(0, 0, 10, 1),
    };
    /// <summary>A level ≥ 1 export makes its cache tiles lazily under the render: dearer per tile than a level-0 cache hit.</summary>
    static PhaseCost LazyTiles(int level) => new(0, 2.0, 0, level switch { 1 => 48, 2 => 12, 3 => 4, _ => 1 });

    /// <summary>The phases an <see cref="ExportRequest"/> is expected to go through, in order, with their costs.</summary>
    internal static List<(string Phase, PhaseCost Cost)> PlanFor(ExportRequest req, int buildLevel)
    {
        var plan = new List<(string, PhaseCost)> { ("loading", Costs["loading"]) };
        int renderLevel = Math.Max(0, req.Level ?? 0);   // the grid rendered; the build level is 0 whenever the depth is needed
        // nominal unit counts by level, from the full-size window (8320×6240, halved per level): 512-px export tiles, 16-row iMCUs
        int gw = 8320 >> renderLevel, gh = 6240 >> renderLevel;
        int tilesN = ((gw + 511) / 512) * ((gh + 511) / 512), rowsN = (gh + 15) / 16;
        PhaseCost Units(PhaseCost c, int n) => c with { NominalUnits = n };
        if (buildLevel == 0) foreach (var p in new[] { "registration", "fusion set-up", "telephoto set-up" }) plan.Add((p, Costs[p]));
        else if (buildLevel == 1) plan.Add(("fusion set-up", Costs["fusion set-up"]));
        bool tilesPlanned = false;
        void Tiles()   // the pipeline tiles are made once, by the first format that prefetches (dng) or renders lazily
        {
            if (tilesPlanned) return; tilesPlanned = true;
            if (renderLevel == 0) { plan.Add(("level-0 inputs", Costs["level-0 inputs"])); plan.Add(("level-0 tiles", Costs["level-0 tiles"])); }
            else plan.Add(($"level-{renderLevel} tiles", LazyTiles(renderLevel)));
        }
        var want = (req.Formats ?? Array.Empty<ExportImageFormat>()).Distinct().ToArray();
        bool jpegRendered = false, floatRendered = false;
        foreach (var f in want)
        {
            switch (f)
            {
                case ExportImageFormat.Dng:
                    Tiles();
                    plan.Add(("dng", Costs["dng"])); break;
                case ExportImageFormat.Jpeg:
                case ExportImageFormat.JpegGDepth:
                    if (!jpegRendered) { plan.Add(("jpeg tiles", Units(tilesPlanned ? Costs["jpeg tiles"] : Costs["jpeg tiles"] with { ThreadSecondsPerUnit = 1.5 }, tilesN))); jpegRendered = tilesPlanned = true; }
                    plan.Add(("jpeg encode", Units(Costs["jpeg encode"], rowsN))); break;
                case ExportImageFormat.Hdr:
                case ExportImageFormat.Ppm:
                    if (!floatRendered) { plan.Add(("render tiles", Units(tilesPlanned ? Costs["render tiles"] : Costs["render tiles"] with { ThreadSecondsPerUnit = 1.2 }, tilesN))); floatRendered = tilesPlanned = true; }
                    plan.Add((f == ExportImageFormat.Hdr ? "hdr write" : "ppm write", Costs[f == ExportImageFormat.Hdr ? "hdr write" : "ppm write"])); break;
            }
        }
        if (req.DepthMap) plan.Add(("depth", Costs["depth"]));
        if (req.LensFrames is not null) plan.Add(("lens-frames", Costs["lens-frames"]));
        int px = req.Parallax?.Formats.Count ?? 0;
        if (px > 0) plan.Add(("parallax", Costs["parallax"] with { NominalUnits = px }));
        return plan;
    }

    // ---- per-file state ------------------------------------------------------------------------------------------------
    internal sealed class FileState
    {
        public string Stem = ""; public int Index; public DateTime Start; public int Threads = 1;
        public List<(string Phase, PhaseCost Cost)> Plan = new();
        int _pos = -1;                       // index of the current phase in the plan, -1 before the first
        string _phase = ""; int _done, _total; DateTime _phaseStart; PhaseCost? _cost;
        int _unitsBefore;                    // units of the phases already finished, for the all-steps counter
        /// <summary>Every finished phase of this file with its measured duration and unit count — what the baseline learns from.</summary>
        public readonly List<(string Phase, double Seconds, int Units)> History = new();
        public string Phase => _phase; public int Done => _done; public int Total => _total;

        public void Apply(ProgressUpdate u, DateTime now)
        {
            bool newPhase = u.Phase != _phase || (u.Done == 0 && _done > 0);
            if (newPhase)
            {
                if (_phase.Length > 0)
                {
                    int units = _total > 0 ? _total : (_cost?.NominalUnits ?? 0);
                    _unitsBefore += units;
                    History.Add((_phase, (now - _phaseStart).TotalSeconds, units));
                }
                _phase = u.Phase; _phaseStart = now; _cost = null;
                for (int i = _pos + 1; i < Plan.Count; i++)
                    if (Plan[i].Phase == u.Phase) { _pos = i; _cost = Plan[i].Cost; break; }   // phases skipped by the run drop out
                if (_cost is null && Costs.TryGetValue(u.Phase, out var c)) _cost = c;          // an unplanned phase: its table cost, no plan advance
            }
            _done = u.Done; _total = u.Total;
        }

        /// <summary>Close the last phase into the history (called when the file finishes).</summary>
        public void Close(DateTime now)
        {
            if (_phase.Length == 0) return;
            History.Add((_phase, (now - _phaseStart).TotalSeconds, _total > 0 ? _total : (_cost?.NominalUnits ?? 0)));
            _phase = "";
        }

        public double Elapsed(DateTime now) => (now - Start).TotalSeconds;

        /// <summary>All steps of the file summed: finished phases at their real counts, the current one at its real count, the
        /// phases still to come at the plan's nominal counts (so the total settles as phases start). A count of work, not of
        /// time; the bar is drawn from it, the time left from the measured durations.</summary>
        public (int Done, int Total) Overall
        {
            get
            {
                int total = _unitsBefore + (_phase.Length > 0 ? (_total > 0 ? _total : (_cost?.NominalUnits ?? 0)) : 0);
                for (int i = _pos + 1; i < Plan.Count; i++) total += Plan[i].Cost.NominalUnits;
                return (_unitsBefore + Math.Min(_done, Math.Max(_total, _done)), total);
            }
        }

        /// <summary>Seconds left by the measured baseline, or null until a file has completed: the rest of the current phase at
        /// the baseline's duration for it, plus the baseline durations of the phases the finished files went through after it.
        /// The estimate only ever moves as work completes, never because a model was recalibrated.</summary>
        public double? Left(DateTime now, Baseline b)
        {
            if (b.Files == 0) return null;
            double left = 0;
            if (_phase.Length > 0)
            {
                int units = _total > 0 ? _total : (_cost?.NominalUnits ?? 0);
                double est = b.Estimate(_phase, units) ?? Fallback(_cost, units);
                double frac = _total > 0 ? Math.Min(1, _done / (double)_total) : 0;
                double el = (now - _phaseStart).TotalSeconds;
                // uniform tile phases: the fraction done is a faithful clock; the few-step phases (registration's states are very
                // uneven) count down the baseline's duration by elapsed time instead, so the figure only ever falls
                left += units >= 20 ? est * (1 - frac) : Math.Max(est - el, 0.05 * est);
            }
            foreach (var (phase, units) in b.After(_phase, Plan, _pos))
                left += b.Estimate(phase, units) ?? Fallback(Costs.TryGetValue(phase, out var c) ? c : null, units);
            return left;
        }

        double Fallback(PhaseCost? c, int units) => c is null ? 0 : c.Serial + c.SerialPerUnit * units + c.ThreadSecondsPerUnit * units / Math.Max(1, Threads);
    }

    /// <summary>What the finished files measured, per phase: mean duration, mean seconds per unit, the phase order they ran in,
    /// and the mean whole-file time. Every completed file folds into the running averages.</summary>
    internal sealed class Baseline
    {
        readonly Dictionary<string, (double Seconds, double Units, int N)> _phases = new(StringComparer.Ordinal);
        readonly List<string> _order = new();
        public int Files { get; private set; }
        public double MeanFileSeconds { get; private set; }

        public void Fold(FileState f, DateTime now)
        {
            f.Close(now);
            foreach (var (phase, seconds, units) in f.History)
            {
                var (sec, un, n) = _phases.TryGetValue(phase, out var v) ? v : (0, 0, 0);
                _phases[phase] = (sec + seconds, un + units, n + 1);
                if (!_order.Contains(phase)) _order.Add(phase);
            }
            MeanFileSeconds = (MeanFileSeconds * Files + f.Elapsed(now)) / (Files + 1);
            Files++;
        }

        /// <summary>The baseline's duration for a phase of <paramref name="units"/> units: the measured seconds per unit scaled to
        /// this count when the phase is a tile-like one (20 units or more), else the mean measured duration. Null when unseen.</summary>
        public double? Estimate(string phase, int units)
        {
            if (!_phases.TryGetValue(phase, out var v) || v.N == 0) return null;
            double meanUnits = v.Units / v.N, meanSeconds = v.Seconds / v.N;
            if (meanUnits >= 20 && units > 0) return meanSeconds / meanUnits * units;
            return meanSeconds;
        }

        /// <summary>The phases that follow <paramref name="current"/> in the measured order, with the unit count to estimate them at
        /// (the plan's nominal count when it names the phase, else the measured mean). Before the current phase is known to the
        /// baseline the plan's own remainder is used.</summary>
        public IEnumerable<(string Phase, int Units)> After(string current, List<(string Phase, PhaseCost Cost)> plan, int planPos)
        {
            int at = _order.IndexOf(current);
            if (at < 0)
            {
                for (int i = planPos + 1; i < plan.Count; i++) yield return (plan[i].Phase, plan[i].Cost.NominalUnits);
                yield break;
            }
            for (int i = at + 1; i < _order.Count; i++)
            {
                string ph = _order[i];
                var planned = plan.FindIndex(planPos + 1, e => e.Phase == ph);
                int units = planned >= 0 ? plan[planned].Cost.NominalUnits : (int)Math.Round(_phases[ph].Units / Math.Max(1, _phases[ph].N));
                yield return (ph, units);
            }
        }
    }

    // ---- the board -----------------------------------------------------------------------------------------------------
    readonly object _gate = new();
    readonly List<FileState> _active = new();
    readonly Baseline _baseline = new();
    readonly int _total, _atOnce; int _started, _finished, _drawn;
    readonly bool _interactive; readonly DateTime _start = DateTime.UtcNow; DateTime _lastDraw = DateTime.MinValue;
    readonly Timer? _timer;
    readonly TextWriter _err = Console.Error;

    public ProgressBoard(int totalFiles, int atOnce, bool interactive)
    {
        _total = totalFiles; _atOnce = Math.Max(1, atOnce); _interactive = interactive;
        if (interactive) _timer = new Timer(_ => Redraw(force: true), null, 500, 500);
    }

    /// <summary>In-place drawing needs a terminal on stderr that understands cursor movement, and no other writer on it.</summary>
    public static bool CanDrawInPlace()
    {
        if (Console.IsErrorRedirected) return false;
        if (Environment.GetEnvironmentVariable("LUX_NO_PROGRESS") == "1" || Environment.GetEnvironmentVariable("LUX_VERBOSE") == "1") return false;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)) return false;
        return !OperatingSystem.IsWindows() || EnableWindowsVt();
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint mode);
    static bool EnableWindowsVt()
    {
        try
        {
            var h = GetStdHandle(-12);   // STD_ERROR_HANDLE
            if (!GetConsoleMode(h, out uint m)) return false;
            const uint vt = 0x0004;      // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            return (m & vt) != 0 || SetConsoleMode(h, m | vt);
        }
        catch { return false; }
    }

    public FileState Start(string stem, List<(string, PhaseCost)> plan, int threads)
    {
        var f = new FileState { Stem = stem, Start = DateTime.UtcNow, Plan = plan, Threads = threads };
        lock (_gate) { f.Index = ++_started; _active.Add(f); }
        if (_interactive) Redraw(force: true);
        return f;
    }

    static readonly bool Trace = Environment.GetEnvironmentVariable("LUX_PROGRESS_TRACE") == "1";   // every update, one line, for debugging the flow

    public void Update(FileState f, ProgressUpdate u)
    {
        var now = DateTime.UtcNow;
        if (Trace) lock (_gate) _err.WriteLine($"  trace [{f.Stem}] {u.Phase} {u.Done}/{u.Total}");
        bool phaseStarted;
        lock (_gate) { string before = f.Phase; f.Apply(u, now); phaseStarted = f.Phase != before; }
        if (_interactive) Redraw(force: phaseStarted);
        else if (phaseStarted) lock (_gate) _err.WriteLine($"  [{f.Stem}] {u.Phase}{(u.Total > 0 ? $" ({u.Total} units)" : "")}");
    }

    public void Finish(FileState f)
    {
        lock (_gate) { _active.Remove(f); _finished++; }
        if (_interactive) Redraw(force: true);
    }

    /// <summary>A file that finished in full: its measured phases become (part of) the baseline the estimates use.</summary>
    public void Completed(FileState f)
    {
        lock (_gate) _baseline.Fold(f, DateTime.UtcNow);
    }

    /// <summary>A permanent line (a file's completion) written above the transient ones.</summary>
    public void Print(string line, bool error)
    {
        lock (_gate)
        {
            if (_interactive) Erase();
            var prev = Console.ForegroundColor;
            if (error) Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(line);
            if (error) Console.ForegroundColor = prev;
            Console.Out.Flush();
        }
        if (_interactive) Redraw(force: true);
    }

    void Erase()
    {
        if (_drawn == 0) return;
        _err.Write($"\x1b[{_drawn}A\x1b[J");
        _drawn = 0;
    }

    void Redraw(bool force)
    {
        if (!_interactive) return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastDraw).TotalMilliseconds < 100) return;
            _lastDraw = now;
            int width = 120; try { int ww = Console.WindowWidth; if (ww >= 40) width = ww - 1; } catch { }   // a pty without a size reports 0
            var sb = new StringBuilder();
            Erase();
            int w = _total.ToString().Length;
            foreach (var f in _active.OrderBy(a => a.Index))
            {
                string left = f.Left(now, _baseline) is double l ? $"~{Fmt(l)} left" : "";
                string count = f.Total > 0 ? $"{Math.Min(f.Done, f.Total)}/{f.Total}" : (f.Phase.Length > 0 ? "…" : "");
                var (od, ot) = f.Overall;
                string all = ot > 0 ? $"all {od}/{ot}" : "";
                sb.Append(Trim($"[{f.Index.ToString().PadLeft(w)}/{_total}] {Pad(f.Stem, 12)} {Pad(f.Phase, 16)} {count,9} {all,-14} {Bar(od, ot, 20)} {Fmt(f.Elapsed(now)),7} {left}", width)).Append('\n');
            }
            double? jobLeft = JobLeft(now);
            string tail = jobLeft is double jl ? $", ~{Fmt(jl)} left" : (_baseline.Files == 0 && _total > 1 ? ", estimates after the first file completes" : "");
            sb.Append(Trim($"job: {_finished}/{_total} done, {_active.Count} in flight, {Fmt((now - _start).TotalSeconds)} elapsed{tail}", width)).Append('\n');
            _drawn = _active.Count + 1;
            _err.Write(sb.ToString()); _err.Flush();
        }
    }

    /// <summary>Whole-job time left, from the measured baseline only: the longest in-flight estimate plus the files not started
    /// yet at the mean measured file time, in waves of the batch width.</summary>
    double? JobLeft(DateTime now)
    {
        if (_baseline.Files == 0) return null;
        int notStarted = _total - _started;
        double inflight = _active.Count == 0 ? 0 : _active.Max(f => f.Left(now, _baseline) ?? 0);
        return inflight + Math.Ceiling(notStarted / (double)_atOnce) * _baseline.MeanFileSeconds;
    }

    static string Bar(int done, int total, int width)
    {
        if (total <= 0) return "[" + new string(' ', width) + "]";
        int n = (int)Math.Round(width * Math.Clamp(done / (double)total, 0, 1));
        return "[" + new string('=', n) + (n < width ? ">" : "") + new string(' ', Math.Max(0, width - n - 1)) + "]";
    }
    static string Pad(string s, int n) => s.Length > n ? s[..n] : s.PadRight(n);
    static string Trim(string s, int width) => s.Length > width ? s[..width] : s;
    internal static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate) { if (_interactive) { Erase(); _err.Flush(); } }
    }
}
