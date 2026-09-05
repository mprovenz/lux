namespace Lux.Engine.Pipeline;

/// <summary>One progress observation: the phase the export is in and how far it is through it. <c>Done == Total</c> closes the phase.</summary>
public readonly record struct ProgressUpdate(string Phase, int Done, int Total);

/// <summary>
/// The export's progress channel: the pipeline calls <see cref="Begin"/> when a phase starts and <see cref="Tick"/> per unit of
/// work done, from any thread; the sink (a console board, a GUI) gets one <see cref="ProgressUpdate"/> per call. Phases are the
/// coarse steps of a render — registration, the fusion set-up, the level-0 inputs and tiles, then one per written format — and the
/// units are whatever that phase naturally counts (cameras, tiles, MCU rows). Reporting never changes what is computed.
/// </summary>
public sealed class ProgressReporter
{
    /// <summary>A reporter nobody listens to: every call is a no-op.</summary>
    public static ProgressReporter None { get; } = new(null);

    readonly Action<ProgressUpdate>? _sink;
    readonly object _sync = new();
    string _phase = ""; int _done, _total;

    public ProgressReporter(Action<ProgressUpdate>? sink) => _sink = sink;
    public bool IsEnabled => _sink is not null;
    public string Phase { get { lock (_sync) return _phase; } }

    /// <summary>Start a phase of <paramref name="total"/> units (0 for a phase that only reports its end).</summary>
    public void Begin(string phase, int total)
    {
        if (_sink is null) return;
        lock (_sync) { _phase = phase; _done = 0; _total = Math.Max(0, total); }
        _sink(new ProgressUpdate(phase, 0, Math.Max(0, total)));
    }

    /// <summary>Count <paramref name="n"/> more units of the current phase done.</summary>
    public void Tick(int n = 1)
    {
        if (_sink is null) return;
        string ph; int d, t;
        lock (_sync) { _done += n; ph = _phase; d = _done; t = _total; }
        _sink(new ProgressUpdate(ph, d, t));
    }

    /// <summary>Count <paramref name="n"/> units only while <paramref name="phase"/> is the current phase. For work that a phase
    /// plans for but that may also run lazily somewhere else (an initialiser the first render triggers): outside its phase the
    /// tick is dropped rather than inflating whatever phase happens to be open.</summary>
    public void Tick(string phase, int n = 1)
    {
        if (_sink is null) return;
        string ph; int d, t;
        lock (_sync) { if (_phase != phase) return; _done += n; ph = _phase; d = _done; t = _total; }
        _sink(new ProgressUpdate(ph, d, t));
    }

    /// <summary>Raise the current phase's unit count (work discovered after <see cref="Begin"/>).</summary>
    public void Extend(int moreUnits)
    {
        if (_sink is null) return;
        string ph; int d, t;
        lock (_sync) { _total += moreUnits; ph = _phase; d = _done; t = _total; }
        _sink(new ProgressUpdate(ph, d, t));
    }

    /// <summary>Close the current phase whatever its count.</summary>
    public void End()
    {
        if (_sink is null) return;
        string ph; int t;
        lock (_sync) { _done = _total; ph = _phase; t = _total; }
        _sink(new ProgressUpdate(ph, t, t));
    }
}
