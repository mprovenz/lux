using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lux.App.ViewModels;

namespace Lux.App.Controls;

/// <summary>
/// Draws the player's current frame fitted into the control (letterboxed), the picked focus points as small rings with their labels, and —
/// while picking — a crosshair cursor; a click inside the picture reports its normalized position (<see cref="PickedCommand"/>-style via
/// <see cref="Clicked"/>).
/// </summary>
public sealed class AnimationFrameView : Control
{
    public static readonly StyledProperty<Bitmap?> FrameProperty = AvaloniaProperty.Register<AnimationFrameView, Bitmap?>(nameof(Frame));
    public static readonly StyledProperty<IReadOnlyList<PlayerMarker>?> MarkersProperty = AvaloniaProperty.Register<AnimationFrameView, IReadOnlyList<PlayerMarker>?>(nameof(Markers));
    public static readonly StyledProperty<bool> IsPickingProperty = AvaloniaProperty.Register<AnimationFrameView, bool>(nameof(IsPicking));
    /// <summary>Degrees clockwise the frame is turned when drawn (the display-only "as the camera was held" turn); clicks and markers
    /// stay in the frame's own (unturned) coordinates.</summary>
    public static readonly StyledProperty<int> RotationProperty = AvaloniaProperty.Register<AnimationFrameView, int>(nameof(Rotation));
    /// <summary>"Preparing frames · 6 of 24": the player waits for frames — the spinner and the text are drawn over the frame it holds.</summary>
    public static readonly StyledProperty<string?> PreparingProperty = AvaloniaProperty.Register<AnimationFrameView, string?>(nameof(Preparing));
    readonly ProcessingIndicator _spinner = new();

    static readonly IBrush Ring = new SolidColorBrush(Color.Parse("#a9dcff"));
    static readonly IBrush RingShadow = new SolidColorBrush(Color.FromArgb(0xb0, 0x1c, 0x1c, 0x1c));
    static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(0xd0, 0x26, 0x26, 0x26));
    static readonly IBrush LabelText = new SolidColorBrush(Color.Parse("#f2f2f2"));

    static AnimationFrameView()
    {
        AffectsRender<AnimationFrameView>(FrameProperty, MarkersProperty, IsPickingProperty, RotationProperty, PreparingProperty);
        IsPickingProperty.Changed.AddClassHandler<AnimationFrameView>((v, _) => v.Cursor = v.IsPicking ? new Cursor(StandardCursorType.Cross) : Cursor.Default);
    }

    public Bitmap? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public IReadOnlyList<PlayerMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public bool IsPicking { get => GetValue(IsPickingProperty); set => SetValue(IsPickingProperty, value); }
    public int Rotation { get => GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public string? Preparing { get => GetValue(PreparingProperty); set => SetValue(PreparingProperty, value); }
    /// <summary>The spinner's text last drawn (tests).</summary>
    public string? PreparingDrawn => _spinner.LastLabel;
    int Turn => ((Rotation % 360) + 360) % 360;

    /// <summary>A frame point (normalized) → where it shows in the turned picture (normalized).</summary>
    public static (double U, double V) FrameToShown(double x, double y, int turn) => (((turn % 360) + 360) % 360) switch
    {
        90 => (1 - y, x), 180 => (1 - x, 1 - y), 270 => (y, 1 - x), _ => (x, y),
    };
    /// <summary>The inverse of <see cref="FrameToShown"/>.</summary>
    public static (double X, double Y) ShownToFrame(double u, double v, int turn) => (((turn % 360) + 360) % 360) switch
    {
        90 => (v, 1 - u), 180 => (1 - u, 1 - v), 270 => (1 - v, u), _ => (u, v),
    };

    /// <summary>A click inside the picture: normalized x, y of the frame (unturned).</summary>
    public event Action<double, double>? Clicked;

    /// <summary>Where the frame is drawn (letterboxed in the bounds).</summary>
    public Rect PictureRect
    {
        get
        {
            if (Frame is not { } f || Bounds.Width <= 0 || Bounds.Height <= 0) return default;
            double fw = f.PixelSize.Width, fh = f.PixelSize.Height;
            if (Turn is 90 or 270) (fw, fh) = (fh, fw);
            double k = Math.Min(Bounds.Width / fw, Bounds.Height / fh);
            double w = fw * k, h = fh * k;
            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }
    }

    public override void Render(DrawingContext context)
    {
        // a frame released by its preview between the binding and this render is simply not drawn (the next frame replaces it)
        try { RenderFrame(context); }
        catch (ObjectDisposedException) { }
    }

    void RenderFrame(DrawingContext context)
    {
        if (Frame is not { } f) return;
        var r = PictureRect;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            if (Turn == 0) context.DrawImage(f, new Rect(0, 0, f.PixelSize.Width, f.PixelSize.Height), r);
            else
            {
                // turned about the picture's center: the unturned rect (its sides swapped for 90 / 270) drawn under the rotation
                var c0 = r.Center;
                double w0 = Turn is 90 or 270 ? r.Height : r.Width, h0 = Turn is 90 or 270 ? r.Width : r.Height;
                var m = Matrix.CreateTranslation(-c0.X, -c0.Y) * Matrix.CreateRotation(Turn * Math.PI / 180) * Matrix.CreateTranslation(c0.X, c0.Y);
                using (context.PushTransform(m))
                    context.DrawImage(f, new Rect(0, 0, f.PixelSize.Width, f.PixelSize.Height), new Rect(c0.X - w0 / 2, c0.Y - h0 / 2, w0, h0));
            }
        }
        if (!string.IsNullOrEmpty(Preparing))
        {
            context.FillRectangle(ProcessingIndicator.Scrim, r);
            _spinner.Draw(context, r, Preparing, double.NaN, Avalonia.Controls.Documents.TextElement.GetFontFamily(this));
            if (TopLevel.GetTopLevel(this) is { } top && !_framePosted) { _framePosted = true; top.RequestAnimationFrame(_ => { _framePosted = false; InvalidateVisual(); }); }
        }
        if (Markers is not { Count: > 0 } ms) return;
        var face = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Medium);
        foreach (var m in ms)
        {
            var (mu, mv) = FrameToShown(m.X, m.Y, Turn);
            var c = new Point(r.X + mu * r.Width, r.Y + mv * r.Height);
            context.DrawEllipse(null, new Pen(RingShadow, 4), c, 9, 9);
            context.DrawEllipse(null, new Pen(Ring, 2), c, 9, 9);
            var ft = new FormattedText(m.Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 12, LabelText);
            var box = new Rect(c.X + 12, c.Y - ft.Height / 2 - 2, ft.Width + 10, ft.Height + 4);
            context.FillRectangle(LabelBack, box, 2);
            context.DrawText(ft, new Point(box.X + 5, box.Y + 2));
        }
    }

    bool _framePosted;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var r = PictureRect;
        if (r.Width <= 0) return;
        var p = e.GetPosition(this);
        if (!r.Contains(p)) return;
        var (fx, fy) = ShownToFrame((p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height, Turn);
        Clicked?.Invoke(fx, fy);
        e.Handled = true;
    }
}
