using Avalonia;
using Avalonia.Controls;
using Lux.App.Services;

namespace Lux.App.Controls;

/// <summary>
/// A capture's thumbnail in an <see cref="ImageStateView"/>. It asks the <see cref="ThumbnailCache"/> only while it is in the visual tree
/// with a capture set — that is, while its item is realised on screen — and cancels the request when the item is recycled or scrolls away.
/// While the thumbnail is being made it shows the spinner (the "Processing image" placeholder at small size); with
/// <see cref="Processing"/> set (a better quality of the capture is being made) it shows the thumbnail blurred under the spinner.
/// </summary>
public sealed class ThumbnailView : ImageStateView
{
    public static readonly StyledProperty<string?> CaptureIdProperty = AvaloniaProperty.Register<ThumbnailView, string?>(nameof(CaptureId));
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<ThumbnailView, int>(nameof(Version));
    public static readonly StyledProperty<bool> ProcessingProperty = AvaloniaProperty.Register<ThumbnailView, bool>(nameof(Processing));
    /// <summary>The library has a thumbnail stored (false: it is being made — the spinner shows while waiting for it; true: it is only read
    /// and decoded, a neutral square meanwhile, so fast scrolling does not animate a spinner in every cell).</summary>
    public static readonly StyledProperty<bool> MadeProperty = AvaloniaProperty.Register<ThumbnailView, bool>(nameof(Made), true);

    /// <summary>The cache every thumbnail view uses (set once by the app).</summary>
    public static ThumbnailCache? Cache { get; set; }
    /// <summary>Views realised now (tests: virtualisation keeps this bounded).</summary>
    public static int Live { get; private set; }

    CancellationTokenSource? _cts;
    bool _attached;

    public string? CaptureId { get => GetValue(CaptureIdProperty); set => SetValue(CaptureIdProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    /// <summary>A better quality of this capture is being made: show the thumbnail blurred, with the spinner.</summary>
    public bool Processing { get => GetValue(ProcessingProperty); set => SetValue(ProcessingProperty, value); }
    public bool Made { get => GetValue(MadeProperty); set => SetValue(MadeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CaptureIdProperty)
        {
            Source = null;
            Load();
        }
        else if (change.Property == VersionProperty) Load(keepShown: true);
        else if (change.Property == ProcessingProperty || change.Property == MadeProperty) UpdateState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Live++;
        Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Live--;
        Cancel();
    }

    void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    void UpdateState()
    {
        var id = CaptureId;
        bool unavailable = id != null && Cache?.IsUnavailable(id) == true;
        State = Processing ? ImageState.Processing
              : Source != null ? ImageState.Ready
              : unavailable || id == null || Cache == null ? ImageState.None
              : Made ? ImageState.None : ImageState.Processing;
    }

    async void Load(bool keepShown = false)
    {
        Cancel();
        var id = CaptureId;
        var cache = Cache;
        if (!keepShown) Source = null;
        if (id == null || cache == null || !_attached) { UpdateState(); return; }
        var hit = cache.TryGet(id);
        if (hit != null) { Source = hit; if (cache.IsCurrent(id)) { UpdateState(); return; } }
        if (cache.IsUnavailable(id)) { UpdateState(); return; }
        var cts = _cts = new CancellationTokenSource();
        UpdateState();
        try
        {
            var bmp = await cache.RequestAsync(id, cts.Token);
            if (cts.IsCancellationRequested || id != CaptureId) return;
            if (bmp != null) Source = bmp;
            UpdateState();
        }
        catch (Exception) { UpdateState(); }
    }
}
