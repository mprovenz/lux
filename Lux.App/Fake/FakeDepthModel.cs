using Lux.DepthModel;

namespace Lux.App.Fake;

/// <summary>The refined depth files of the fake library: nothing is downloaded. A "download" counts up over <see cref="DownloadMs"/> and
/// then succeeds, or fails with <see cref="FailWith"/>. Tests set the fields before they use it.</summary>
public sealed class FakeDepthModel : IDepthModelService
{
    readonly object _lock = new();
    DepthModelStatus _status = new(DepthModelState.NotInstalled);
    Task? _running; CancellationTokenSource? _cancel;

    /// <summary>The size the fake reports for its download.</summary>
    public long DownloadBytes { get; set; } = 107_190_566;
    /// <summary>How long a fake download takes.</summary>
    public int DownloadMs { get; set; } = 3000;
    /// <summary>When set, the next download fails with this text.</summary>
    public string? FailWith { get; set; }
    public string Folder { get; set; } = Path.Combine("fake-app-data", "models", "depth");
    /// <summary>Downloads started so far.</summary>
    public int Downloads { get; private set; }

    public DepthModelStatus Status { get { lock (_lock) return _status; } }
    public event Action? StatusChanged;

    /// <summary>Put the fake in a state at once (tests, screenshots).</summary>
    public void Set(DepthModelStatus s)
    {
        lock (_lock) _status = s;
        StatusChanged?.Invoke();
    }

    public void SetInstalled() => Set(new DepthModelStatus(DepthModelState.Installed, Version: "Depth Anything V2 Small, ONNX Runtime 1.23.2", SizeOnDisk: 121_386_911));

    public Task DownloadAsync()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false }) return _running;
            if (_status.State is DepthModelState.Installed or DepthModelState.NotAvailable) return Task.CompletedTask;
            Downloads++;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            return _running = Task.Run(async () =>
            {
                const int steps = 20;
                try
                {
                    for (int i = 0; i <= steps; i++)
                    {
                        Set(new DepthModelStatus(DepthModelState.Downloading, BytesDone: DownloadBytes * i / steps, BytesTotal: DownloadBytes));
                        if (FailWith is { } why && i >= steps / 2) { Set(new DepthModelStatus(DepthModelState.Failed, why)); return; }
                        await Task.Delay(Math.Max(1, DownloadMs / steps), token).ConfigureAwait(false);
                    }
                    SetInstalled();
                }
                catch (OperationCanceledException) { Set(new DepthModelStatus(DepthModelState.NotInstalled)); }
            });
        }
    }

    public void CancelDownload() { lock (_lock) _cancel?.Cancel(); }

    public void Remove()
    {
        CancelDownload();
        Task? t; lock (_lock) t = _running;
        try { t?.Wait(); } catch (AggregateException) { }
        if (Status.State != DepthModelState.NotAvailable) Set(new DepthModelStatus(DepthModelState.NotInstalled));
    }
}
