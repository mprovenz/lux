using System.Diagnostics;

namespace Lux.Library.Depth;

/// <summary>
/// The photo's depth worker: one thread, one job at a time, in call order — the depth editor is not thread-safe and its history is a sequence.
/// User jobs go before background ones; a user job arriving while a background job (the history rebuild) runs stops that one (it is started
/// again later from the beginning). Every job gets a cancellation token and reports its phase / fraction (<see cref="Activity"/>).
/// </summary>
internal sealed class DepthWorker : IDisposable
{
    internal sealed class Ctx
    {
        readonly DepthWorker _w; internal readonly Item Item;
        internal Ctx(DepthWorker w, Item item) { _w = w; Item = item; }
        public CancellationToken Token => Item.Cts.Token;
        /// <summary>Report a phase and fraction (throttled to 20 updates a second, except phase changes).</summary>
        public void Report(string? phase, double? fraction = null) => _w.Report(Item, phase, fraction);
        /// <summary>Whether a cancel stops the running job now.</summary>
        public bool CanCancel { set { Item.CanCancel = value; _w.Report(Item, Item.Phase, Item.Fraction, force: true); } }
        public double? EstimatedMs { set { Item.Estimate = value; } }
    }

    internal sealed class Item
    {
        public long Id; public string Label = ""; public Func<Ctx, DepthJobResult> Body = null!; public bool Background; public bool CanCancel;
        public readonly CancellationTokenSource Cts = new(); public readonly TaskCompletionSource<DepthJobResult> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Phase; public double? Fraction; public double? Estimate; public Stopwatch? Clock;
    }

    readonly object _lock = new();
    readonly LinkedList<Item> _queue = new();
    readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    readonly Thread _thread;
    readonly Action<string> _trace;
    Item? _running; long _ids; volatile bool _stop;
    DepthActivity _activity = DepthActivity.Idle; long _lastReport;

    public event Action<DepthActivity>? ActivityChanged;
    /// <summary>The worker became idle (no job running or queued).</summary>
    public event Action? Idle;
    /// <summary>A job starts / ended (worker thread) — the session shows its background work in the Queue.</summary>
    public event Action<Item>? JobStarted;
    public event Action<Item>? JobEnded;

    /// <summary>Stop the job with this id when it is running and may be stopped, or drop it when queued.</summary>
    public void CancelJob(long id)
    {
        Item? it;
        lock (_lock) it = _running is { } r && r.Id == id ? r : _queue.FirstOrDefault(q => q.Id == id);
        if (it is not null) Cancel(it);
    }

    public DepthWorker(string name, Action<string> trace)
    {
        _trace = trace;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public DepthActivity Activity { get { lock (_lock) return _activity; } }
    public bool IsIdle { get { lock (_lock) return _running is null && _queue.Count == 0; } }
    public bool BackgroundRunning { get { lock (_lock) return _running is { Background: true }; } }

    /// <summary>Queue a job. <paramref name="cancellable"/>: whether it may be stopped while running (it must then only throw
    /// <see cref="OperationCanceledException"/> before it changed anything).</summary>
    public DepthJob Enqueue(string label, Func<Ctx, DepthJobResult> body, bool cancellable, bool background = false, double? estimateMs = null)
    {
        var it = new Item { Id = Interlocked.Increment(ref _ids), Label = label, Body = body, Background = background, CanCancel = cancellable, Estimate = estimateMs };
        lock (_lock)
        {
            if (_stop) { it.Tcs.TrySetResult(new DepthJobResult(DepthJobOutcome.Cancelled, "The photo was closed.")); }
            else
            {
                if (background) _queue.AddLast(it);
                else
                {
                    // before the first background job; a running background job is stopped (it is queued again by its owner)
                    var n = _queue.First; while (n is not null && !n.Value.Background) n = n.Next;
                    if (n is null) _queue.AddLast(it); else _queue.AddBefore(n, it);
                    if (_running is { Background: true } bg) bg.Cts.Cancel();
                }
                _signal.Release();
            }
        }
        PublishActivity(force: true);
        return new DepthJob { Id = it.Id, Label = label, Completion = it.Tcs.Task, Cancel = () => Cancel(it) };
    }

    void Cancel(Item it)
    {
        lock (_lock)
        {
            if (_queue.Remove(it)) { it.Tcs.TrySetResult(new DepthJobResult(DepthJobOutcome.Cancelled, null)); }
            else if (ReferenceEquals(_running, it) && it.CanCancel) it.Cts.Cancel();
        }
        PublishActivity(force: true);
    }

    /// <summary>Drop the queued jobs and stop the running one when it can be stopped.</summary>
    public void CancelAll(bool includingBackground = true)
    {
        lock (_lock)
        {
            foreach (var it in _queue.ToList())
                if (includingBackground || !it.Background) { _queue.Remove(it); it.Tcs.TrySetResult(new DepthJobResult(DepthJobOutcome.Cancelled, null)); }
            if (_running is { } r && r.CanCancel && (includingBackground || !r.Background)) r.Cts.Cancel();
        }
        PublishActivity(force: true);
    }

    /// <summary>Whether a background job with this label is queued or running.</summary>
    public bool HasBackground(string label) { lock (_lock) return (_running is { Background: true } r && r.Label == label) || _queue.Any(i => i.Background && i.Label == label); }

    void Loop()
    {
        while (true)
        {
            _signal.Wait();
            Item? it;
            lock (_lock)
            {
                if (_stop && _queue.Count == 0) return;
                it = _queue.First?.Value;
                if (it is null) continue;
                _queue.RemoveFirst();
                _running = it; it.Clock = Stopwatch.StartNew();
            }
            PublishActivity(force: true);
            try { JobStarted?.Invoke(it); } catch (Exception e) { _trace("depth job listener: " + e.Message); }
            DepthJobResult res;
            var prio = Thread.CurrentThread.Priority;
            try
            {
                if (it.Background) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                it.Cts.Token.ThrowIfCancellationRequested();
                res = it.Body(new Ctx(this, it));
                res = res with { WorkMs = it.Clock.Elapsed.TotalMilliseconds };
            }
            catch (OperationCanceledException) { res = new DepthJobResult(DepthJobOutcome.Cancelled, null) { WorkMs = it.Clock.Elapsed.TotalMilliseconds }; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _trace($"depth job '{it.Label}': {e}");
                res = new DepthJobResult(DepthJobOutcome.Failed, $"{it.Label} failed: {Core.LibraryCore.Plain(e)}") { WorkMs = it.Clock.Elapsed.TotalMilliseconds };
            }
            finally { Thread.CurrentThread.Priority = prio; }
            bool idle;
            lock (_lock) { _running = null; idle = _queue.Count == 0; }
            try { JobEnded?.Invoke(it); } catch (Exception e) { _trace("depth job listener: " + e.Message); }
            it.Tcs.TrySetResult(res);
            PublishActivity(force: true);
            if (idle) { try { Idle?.Invoke(); } catch (Exception e) { _trace("depth idle: " + e.Message); } }
        }
    }

    void Report(Item it, string? phase, double? fraction, bool force = false)
    {
        bool phaseChanged = phase != it.Phase;
        // Within one phase the fraction never goes back (the edge heal's solver reported 1 %, 0 %, 6 % …)
        if (!phaseChanged && fraction is double f && it.Fraction is double was && f < was) fraction = was;
        it.Phase = phase; it.Fraction = fraction;
        PublishActivity(force || phaseChanged);
    }

    void PublishActivity(bool force)
    {
        DepthActivity a;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastReport < 50) return;
            _lastReport = now;
            var r = _running;
            a = r is null ? DepthActivity.Idle with { Queued = _queue.Count }
                : new DepthActivity(true, r.Label, r.Phase, r.Fraction, r.Clock?.Elapsed.TotalMilliseconds ?? 0, r.Estimate, r.CanCancel, _queue.Count);
            if (a == _activity) return;
            _activity = a;
        }
        try { ActivityChanged?.Invoke(a); } catch (Exception e) { _trace("depth activity listener: " + e.Message); }
    }

    /// <summary>Stop: queued jobs are cancelled, the running one is asked to stop, and the thread ends after it (waits up to
    /// <paramref name="wait"/>).</summary>
    public void Stop(TimeSpan wait)
    {
        lock (_lock)
        {
            _stop = true;
            foreach (var it in _queue) it.Tcs.TrySetResult(new DepthJobResult(DepthJobOutcome.Cancelled, "The photo was closed."));
            _queue.Clear();
            if (_running is { CanCancel: true } r) r.Cts.Cancel();
            _signal.Release();
        }
        _thread.Join(wait);
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(10));
}
