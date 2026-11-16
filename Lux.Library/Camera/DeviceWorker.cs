namespace Lux.Library.Camera;

/// <summary>
/// The one thread that talks to the camera. libgphoto2's camera handle is not thread-safe and the USB pipe is serial anyway, so every
/// device call — opening, listing, file information, previews, copies, polling the bus — is a small piece of work queued here and run in
/// priority order (lower first; within a priority oldest first, or newest first for work queued with <c>newestFirst</c> — the thumbnails
/// on screen now). Work whose token is canceled before it starts is dropped.
/// </summary>
internal sealed class DeviceWorker : IDisposable
{
    sealed class Work
    {
        public required Action Run;
        public required Action Cancelled;
        public required int Priority;
        public required long Seq;
        public CancellationToken Token;
        public CancellationTokenRegistration Registration;
    }

    readonly object _l = new();
    readonly List<Work> _queue = new();
    readonly Thread _thread;
    readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    long _seq;
    volatile bool _stop;
    Action? _final;

    public DeviceWorker(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>Pieces of work waiting at <paramref name="priority"/>.</summary>
    public int Waiting(int priority) { lock (_l) return _queue.Count(w => w.Priority == priority); }

    public Task Run(int priority, Action<CancellationToken> f, CancellationToken cancel = default, bool newestFirst = false)
        => Run<bool>(priority, t => { f(t); return true; }, cancel, newestFirst);

    public Task<T> Run<T>(int priority, Func<CancellationToken, T> f, CancellationToken cancel = default, bool newestFirst = false)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop) { tcs.TrySetException(new ObjectDisposedException(nameof(DeviceWorker))); return tcs.Task; }
        if (cancel.IsCancellationRequested) { tcs.TrySetCanceled(cancel); return tcs.Task; }
        long seq = Interlocked.Increment(ref _seq);
        var w = new Work
        {
            Priority = priority, Seq = newestFirst ? -seq : seq, Token = cancel,
            Run = () =>
            {
                try { tcs.TrySetResult(f(cancel)); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { tcs.TrySetCanceled(cancel); }
                catch (Exception e) { tcs.TrySetException(e); }
            },
            Cancelled = () => tcs.TrySetCanceled(cancel),
        };
        lock (_l) _queue.Add(w);
        if (cancel.CanBeCanceled)
            w.Registration = cancel.Register(() =>
            {
                bool removed;
                lock (_l) removed = _queue.Remove(w);
                if (removed) w.Cancelled();
            });
        _wake.Release();
        return tcs.Task;
    }

    void Loop()
    {
        while (true)
        {
            _wake.Wait();
            if (_stop) break;
            Work? w;
            lock (_l)
            {
                w = _queue.OrderBy(x => x.Priority).ThenBy(x => x.Seq).FirstOrDefault();
                if (w is not null) _queue.Remove(w);
            }
            if (w is null) continue;
            w.Registration.Dispose();
            if (w.Token.IsCancellationRequested) { w.Cancelled(); continue; }
            w.Run();
        }
        List<Work> left;
        lock (_l) { left = _queue.ToList(); _queue.Clear(); }
        foreach (var w in left) w.Cancelled();
        // closing the camera happens here, on this thread, after the call in progress returned (a file list can take ~17 s): never left open
        try { _final?.Invoke(); } catch (Exception) { /* closing must not throw out of the thread */ }
    }

    /// <summary>Stop after the piece of work in progress; queued work is canceled; then <paramref name="last"/> runs on the worker thread
    /// (closing the camera) — also when the call in progress outlasts <paramref name="wait"/> (it runs when that call returns).</summary>
    public void Dispose(Action? last, TimeSpan wait)
    {
        if (_stop) return;
        _final = last;
        _stop = true;
        _wake.Release();
        _thread.Join(wait);
    }

    public void Dispose() => Dispose(null, TimeSpan.FromSeconds(5));
}
