using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Lux.App.Controls;

/// <summary>What an image area shows.</summary>
public enum ImageState
{
    /// <summary>Nothing to show and nothing coming (a neutral square).</summary>
    None = 0,
    /// <summary>The picture at the requested quality.</summary>
    Ready = 1,
    /// <summary>The requested quality is being made: the best picture already available, blurred, with a spinner ("Processing image").</summary>
    Processing = 2,
}

/// <summary>
/// The image area of a capture (user design "Processing image"): the picture when it is ready; while the requested quality is being made,
/// a blurred version of the best picture already available (or a neutral square) with a spinner and "Processing image" (plus the progress
/// when known) — the label is left out below <see cref="LabelMinWidth"/>, so small grid cells show the spinner only. Portrait captures are
/// turned upright (<see cref="Orientation"/>) and fitted; landscape ones fill the box. Reused by the details panel, the browse cells and
/// the viewer.
/// </summary>
public class ImageStateView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<ImageStateView, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<ImageState> StateProperty = AvaloniaProperty.Register<ImageStateView, ImageState>(nameof(State));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<ImageStateView, double>(nameof(Progress), double.NaN);
    public static readonly StyledProperty<int> OrientationProperty = AvaloniaProperty.Register<ImageStateView, int>(nameof(Orientation));
    public static readonly StyledProperty<bool> DimmedProperty = AvaloniaProperty.Register<ImageStateView, bool>(nameof(Dimmed));
    public static readonly StyledProperty<IBrush?> PlaceholderProperty = AvaloniaProperty.Register<ImageStateView, IBrush?>(nameof(Placeholder));
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<ImageStateView, string>(nameof(Label), "Processing image");
    public static readonly StyledProperty<double> LabelMinWidthProperty = AvaloniaProperty.Register<ImageStateView, double>(nameof(LabelMinWidth), 120);
    /// <summary>Height ÷ width when only the width is given (the captures are 4:3).</summary>
    public static readonly StyledProperty<double> AspectProperty = AvaloniaProperty.Register<ImageStateView, double>(nameof(Aspect), 0.75);

    static readonly ConditionalWeakTable<Bitmap, Bitmap> Blurred = new();
    static readonly IBrush Scrim = ProcessingIndicator.Scrim;
    readonly ProcessingIndicator _indicator = new();
    bool _frameRequested;

    static ImageStateView()
    {
        AffectsRender<ImageStateView>(SourceProperty, StateProperty, ProgressProperty, OrientationProperty, DimmedProperty, PlaceholderProperty, LabelProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageStateView>(true);
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public ImageState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    /// <summary>0–1, or NaN when not known.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public int Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool Dimmed { get => GetValue(DimmedProperty); set => SetValue(DimmedProperty, value); }
    public IBrush? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double LabelMinWidth { get => GetValue(LabelMinWidthProperty); set => SetValue(LabelMinWidthProperty, value); }
    public double Aspect { get => GetValue(AspectProperty); set => SetValue(AspectProperty, value); }

    /// <summary>Whether the "Processing image" label is drawn at the current size (tests).</summary>
    public bool ShowsLabel => State == ImageState.Processing && Bounds.Width >= LabelMinWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width)) return new Size(availableSize.Width, Math.Round(availableSize.Width * Aspect));
        if (!double.IsInfinity(availableSize.Height)) return new Size(Math.Round(availableSize.Height / Aspect), availableSize.Height);
        return new Size(160, 120);
    }

    /// <summary>Blurred copies made so far (tests: one per thumbnail, never per frame).</summary>
    public static int BlurredMade { get; private set; }

    /// <summary>A small copy of the picture drawn large: a soft blur without an image filter. Made once per bitmap and kept with it.</summary>
    internal static Bitmap BlurredOf(Bitmap b) => Blurred.GetValue(b, src =>
    {
        BlurredMade++;
        var size = src.PixelSize;
        int w = Math.Max(4, size.Width / 24), h = Math.Max(3, size.Height / 24);
        return src.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.HighQuality);
    });

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        context.FillRectangle(Placeholder ?? Brushes.DimGray, r);
        bool processing = State == ImageState.Processing;
        if (Source is { } src && State != ImageState.None)
        {
            var bmp = processing ? BlurredOf(src) : src;
            using (context.PushOpacity(Dimmed ? 0.35 : 1))
                DrawOriented(context, bmp, src.Size, r, Orientation, highQuality: !processing);
            if (processing) context.FillRectangle(Scrim, r);
        }
        if (!processing) return;

        // spinner and label (shared with the viewer)
        bool label = r.Width >= LabelMinWidth;
        _indicator.Draw(context, r, label ? Label : null, Progress, Avalonia.Controls.Documents.TextElement.GetFontFamily(this));
        if (!_frameRequested && TopLevel.GetTopLevel(this) is { } top)
        {
            _frameRequested = true;
            top.RequestAnimationFrame(_ => { _frameRequested = false; if (State == ImageState.Processing) InvalidateVisual(); });
        }
    }

    /// <summary>Draw <paramref name="bmp"/> (whose full-size picture is <paramref name="srcSize"/>) turned by <paramref name="orientation"/>:
    /// landscape fills the box (cropped to its aspect), portrait is fitted.</summary>
    static void DrawOriented(DrawingContext context, Bitmap bmp, Size srcSize, Rect r, int orientation, bool highQuality)
    {
        var b = bmp.Size;
        bool quarter = orientation is 90 or 270;
        double dw = quarter ? srcSize.Height : srcSize.Width, dh = quarter ? srcSize.Width : srcSize.Height;
        using var _ = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = highQuality ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.HighQuality });
        if (!quarter && orientation != 180)
        {
            double scale = Math.Max(r.Width / dw, r.Height / dh);
            var crop = new Size(r.Width / scale, r.Height / scale);
            double kx = b.Width / srcSize.Width, ky = b.Height / srcSize.Height;
            var srcRect = new Rect((srcSize.Width - crop.Width) / 2 * kx, (srcSize.Height - crop.Height) / 2 * ky, crop.Width * kx, crop.Height * ky);
            context.DrawImage(bmp, srcRect, r);
            return;
        }
        double s = quarter ? Math.Min(r.Width / dw, r.Height / dh) : Math.Max(r.Width / dw, r.Height / dh);
        double w = srcSize.Width * s, h = srcSize.Height * s;
        var m = Matrix.CreateTranslation(-w / 2, -h / 2) * Matrix.CreateRotation(orientation * Math.PI / 180) * Matrix.CreateTranslation(r.Width / 2, r.Height / 2);
        using (context.PushTransform(m))
            context.DrawImage(bmp, new Rect(0, 0, b.Width, b.Height), new Rect(0, 0, w, h));
    }
}
