using Avalonia.Threading;
using Lux.Library;

namespace Lux.App.Services;

/// <summary>
/// Tells the job queue when the user is working (<see cref="IJobQueue.SetInteractive"/>): the first pointer, wheel, key or scroll event
/// sends true (renewed every 10 s while it goes on, the queue's hint expires after 30 s), and <see cref="IdleAfter"/> without any sends
/// false. With "Hold heavy jobs while I work" on, heavy jobs then wait to start. Calls go to the queue off the UI thread. UI thread only.
/// </summary>
public sealed class InteractionHint
{
    readonly IJobQueue _queue;
    readonly DispatcherTimer _idle;
    DateTime _lastSent;
    bool _active;

    public InteractionHint(IJobQueue queue, TimeSpan? idleAfter = null)
    {
        _queue = queue;
        IdleAfter = idleAfter ?? TimeSpan.FromSeconds(2);
        _idle = new DispatcherTimer { Interval = IdleAfter };
        _idle.Tick += (_, _) => End();
    }

    public TimeSpan IdleAfter { get; }
    public bool IsActive => _active;
    /// <summary>Hints sent (tests).</summary>
    public int Sent { get; private set; }

    /// <summary>The user did something.</summary>
    public void Poke()
    {
        _idle.Stop();
        _idle.Start();
        var now = DateTime.UtcNow;
        if (_active && now - _lastSent < TimeSpan.FromSeconds(10)) return;
        _active = true;
        _lastSent = now;
        Send(true);
    }

    /// <summary>Idle (the timer) or leaving the page.</summary>
    public void End()
    {
        _idle.Stop();
        if (!_active) return;
        _active = false;
        Send(false);
    }

    void Send(bool interacting)
    {
        Sent++;
        var q = _queue;
        ThreadPool.QueueUserWorkItem(_ => { try { q.SetInteractive(interacting); } catch (Exception) { /* a hint only */ } });
    }
}
