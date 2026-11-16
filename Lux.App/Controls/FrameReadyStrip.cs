using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Lux.App.Controls;

/// <summary>The animation player's ready range: one segment per played position, accent where its frame is made, dim where it is still being
/// prepared. Drawn under the scrubber, aligned with its track.</summary>
public sealed class FrameReadyStrip : Control
{
    public static readonly StyledProperty<IReadOnlyList<bool>?> ReadyProperty = AvaloniaProperty.Register<FrameReadyStrip, IReadOnlyList<bool>?>(nameof(Ready));
    static readonly IBrush On = new SolidColorBrush(Color.Parse("#a9dcff"));
    static readonly IBrush Off = new SolidColorBrush(Color.Parse("#555555"));

    static FrameReadyStrip() => AffectsRender<FrameReadyStrip>(ReadyProperty);

    public IReadOnlyList<bool>? Ready { get => GetValue(ReadyProperty); set => SetValue(ReadyProperty, value); }

    /// <summary>Positions shown as ready (tests).</summary>
    public int ReadyCount => Ready?.Count(x => x) ?? 0;

    public override void Render(DrawingContext context)
    {
        if (Ready is not { Count: > 0 } r || Bounds.Width <= 0) return;
        // the slider's thumb center runs from half a thumb in from each end
        const double inset = 10;
        double w = Math.Max(1, Bounds.Width - 2 * inset), step = r.Count > 1 ? w / (r.Count - 1) : w;
        for (int i = 0; i < r.Count; i++)
        {
            double x0 = inset + (i - 0.5) * step, x1 = inset + (i + 0.5) * step;
            x0 = Math.Max(inset, x0); x1 = Math.Min(inset + w, x1);
            if (x1 <= x0) continue;
            context.FillRectangle(r[i] ? On : Off, new Rect(x0, 0, x1 - x0, Bounds.Height));
        }
    }
}
