using Avalonia;
using Lux.App.Services;

namespace Lux.App.Controls;

/// <summary>
/// A camera capture's thumbnail on the Camera page (made from the camera's own preview JPEG). Like <see cref="ThumbnailView"/> it asks its
/// cache only while it is realised on screen, and cancels when it is recycled or scrolls away; while the preview is read from the camera it
/// shows the spinner; a capture without a preview stays a neutral square.
/// </summary>
public sealed class CameraThumbnailView : ImageStateView
{
    public static readonly StyledProperty<string?> KeyProperty = AvaloniaProperty.Register<CameraThumbnailView, string?>(nameof(Key));
    public static readonly StyledProperty<bool> HasPreviewProperty = AvaloniaProperty.Register<CameraThumbnailView, bool>(nameof(HasPreview), true);

    /// <summary>The cache every camera thumbnail uses (set by the Camera page).</summary>
    public static ThumbnailCache? Cache { get; set; }
    /// <summary>Views realised now (tests: virtualisation keeps this bounded).</summary>
    public static int Live { get; private set; }

    CancellationTokenSource? _cts;
    bool _attached;

    public string? Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public bool HasPreview { get => GetValue(HasPreviewProperty); set => SetValue(HasPreviewProperty, value); }

    public CameraThumbnailView() => Label = "Reading preview";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeyProperty || change.Property == HasPreviewProperty) { Source = null; Load(); }
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

    async void Load()
    {
        Cancel();
        var key = Key;
        var cache = Cache;
        if (key == null || cache == null || !_attached || !HasPreview) { State = Source != null ? ImageState.Ready : ImageState.None; return; }
        if (cache.TryGet(key) is { } hit) { Source = hit; State = ImageState.Ready; return; }
        if (cache.IsUnavailable(key)) { State = ImageState.None; return; }
        var cts = _cts = new CancellationTokenSource();
        State = ImageState.Processing;
        try
        {
            var bmp = await cache.RequestAsync(key, cts.Token);
            if (cts.IsCancellationRequested || key != Key) return;
            Source = bmp;
            State = bmp != null ? ImageState.Ready : ImageState.None;
        }
        catch (Exception) { if (key == Key) State = ImageState.None; }
    }
}
