using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lux.Library;

namespace Lux.App.Services;

/// <summary>
/// Decoded thumbnails for the browse views. Requests come only from realised (on-screen) items, newest first per the contract, and are
/// cancelled when the item scrolls away: a request is shared by every view showing the same capture and cancelled at the library when
/// its last requester lets go. Decoding runs off the UI thread; decoded bitmaps are kept in a least-recently-used cache under a memory
/// budget. Every member is called on the UI thread.
/// </summary>
public sealed class ThumbnailCache
{
    sealed class Entry
    {
        public required string Id;
        public required Bitmap Bitmap;
        public required long Bytes;
        public bool Stale;
        public LinkedListNode<Entry>? Node;
    }

    sealed class Pending
    {
        public required Task<Bitmap?> Task;
        public required CancellationTokenSource Cts;
        public int Waiters;
    }

    readonly ILibrary? _lib;
    readonly Func<string, CancellationToken, Task<byte[]?>>? _loader;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly LinkedList<Entry> _lru = new();
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
    long _bytes;

    public ThumbnailCache(ILibrary lib, long budgetBytes = 384L << 20, int decodeWidth = 400)
    {
        _lib = lib;
        BudgetBytes = budgetBytes;
        DecodeWidth = decodeWidth;
    }

    /// <summary>A cache of other pictures than the library's thumbnails (the camera page's previews): <paramref name="loader"/> gives a key's
    /// encoded image (null = none), decoded here at <paramref name="decodeWidth"/>.</summary>
    public ThumbnailCache(Func<string, CancellationToken, Task<byte[]?>> loader, long budgetBytes = 192L << 20, int decodeWidth = 400)
    {
        _loader = loader;
        BudgetBytes = budgetBytes;
        DecodeWidth = decodeWidth;
    }

    public long BudgetBytes { get; }
    public int DecodeWidth { get; }
    // figures for tests
    public int Requested { get; private set; }
    public int Delivered { get; private set; }
    public int CancelledAtLibrary { get; private set; }
    public int InFlight => _pending.Count;
    public int Cached => _entries.Count;
    public long CachedBytes => _bytes;

    /// <summary>The cached bitmap (current or stale), if any; touching it keeps it in the cache.</summary>
    public Bitmap? TryGet(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) return null;
        _lru.Remove(e.Node!);
        _lru.AddFirst(e.Node!);
        return e.Bitmap;
    }

    public bool IsCurrent(string id) => _entries.TryGetValue(id, out var e) && !e.Stale;
    public bool IsUnavailable(string id) => _unavailable.Contains(id);

    /// <summary>A newer thumbnail exists (the capture changed): the cached one stays on screen until the new one arrives.</summary>
    public void Invalidate(string id)
    {
        if (_entries.TryGetValue(id, out var e)) e.Stale = true;
        _unavailable.Remove(id);
    }

    /// <summary>Get the thumbnail; cancel <paramref name="cancel"/> when the item leaves the screen.</summary>
    public async Task<Bitmap?> RequestAsync(string id, CancellationToken cancel)
    {
        if (_entries.TryGetValue(id, out var cached) && !cached.Stale) return TryGet(id);
        if (_unavailable.Contains(id)) return null;
        if (!_pending.TryGetValue(id, out var p))
        {
            var cts = new CancellationTokenSource();
            Requested++;
            p = new Pending { Task = LoadAsync(id, cts.Token), Cts = cts };
            _pending[id] = p;
            _ = p.Task.ContinueWith(t => Dispatcher.UIThread.Post(() => Complete(id, p, t)), TaskScheduler.Default);
        }
        p.Waiters++;
        using var reg = cancel.Register(() => Dispatcher.UIThread.Post(() => Release(id, p)));
        try
        {
            var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var r2 = cancel.Register(() => tcs.TrySetCanceled(cancel));
            var done = await Task.WhenAny(p.Task, tcs.Task).ConfigureAwait(true);
            if (done != p.Task) return null;
            return p.Task.IsCompletedSuccessfully ? p.Task.Result : null;
        }
        catch (OperationCanceledException) { return null; }
    }

    void Release(string id, Pending p)
    {
        p.Waiters--;
        if (p.Waiters <= 0 && !p.Task.IsCompleted && _pending.TryGetValue(id, out var cur) && cur == p)
        {
            p.Cts.Cancel();
            _pending.Remove(id);
            CancelledAtLibrary++;
        }
    }

    void Complete(string id, Pending p, Task<Bitmap?> t)
    {
        if (_pending.TryGetValue(id, out var cur) && cur == p) _pending.Remove(id);
        p.Cts.Dispose();
        if (!t.IsCompletedSuccessfully) return;
        if (t.Result is not { } bmp) { _unavailable.Add(id); return; }
        Delivered++;
        if (_entries.TryGetValue(id, out var old)) Drop(old);
        var size = bmp.PixelSize;
        var e = new Entry { Id = id, Bitmap = bmp, Bytes = (long)size.Width * size.Height * 4 };
        e.Node = _lru.AddFirst(e);
        _entries[id] = e;
        _bytes += e.Bytes;
        while (_bytes > BudgetBytes && _lru.Last is { } last && last.Value != e) Drop(last.Value);
    }

    void Drop(Entry e)
    {
        // Not disposed: a view may still be drawing it; the collector frees it once no view holds it.
        _lru.Remove(e.Node!);
        _entries.Remove(e.Id);
        _bytes -= e.Bytes;
    }

    async Task<Bitmap?> LoadAsync(string id, CancellationToken cancel)
    {
        if (_loader is { } loader)
        {
            var bytes = await loader(id, cancel).ConfigureAwait(false);
            if (bytes is null) return null;
            cancel.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(ms, DecodeWidth, BitmapInterpolationMode.MediumQuality);
            }, cancel).ConfigureAwait(false);
        }
        var thumb = await _lib!.GetThumbnailAsync(id, cancel).ConfigureAwait(false);
        if (thumb == null) return null;
        cancel.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream(thumb.Data, writable: false);
            return thumb.Width > DecodeWidth ? Bitmap.DecodeToWidth(ms, DecodeWidth, BitmapInterpolationMode.MediumQuality) : new Bitmap(ms);
        }, cancel).ConfigureAwait(false);
    }

    public void Clear()
    {
        foreach (var p in _pending.Values) p.Cts.Cancel();
        _pending.Clear();
        _entries.Clear();
        _lru.Clear();
        _bytes = 0;
    }
}
