using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lux.App.Develop;

namespace Lux.App.Controls;

/// <summary>The histogram of the picture: red, green and blue drawn as translucent areas over each other (their overlap reads gray to
/// white), on the bar color, scaled to the largest bin capped at 85 % as Lumen draws it; "No picture yet" before the first one.</summary>
public sealed class HistogramView : Control
{
    public static readonly StyledProperty<HistogramData?> DataProperty = AvaloniaProperty.Register<HistogramView, HistogramData?>(nameof(Data));

    static readonly IBrush Ground = new SolidColorBrush(Color.Parse("#333333"));
    static readonly IBrush Red = new SolidColorBrush(Color.FromArgb(0x90, 0xe8, 0x5a, 0x5a));
    static readonly IBrush Green = new SolidColorBrush(Color.FromArgb(0x90, 0x6a, 0xd0, 0x7a));
    static readonly IBrush Blue = new SolidColorBrush(Color.FromArgb(0x90, 0x6a, 0x9a, 0xf0));
    static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xf2, 0xf2, 0xf2)), 1);
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#a8a8a8"));

    static HistogramView() => AffectsRender<HistogramView>(DataProperty);

    public HistogramData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 256 : availableSize.Width, 72);

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        context.FillRectangle(Ground, r);
        for (int i = 1; i < 4; i++) context.DrawLine(Grid, new Point(r.Width * i / 4, 0), new Point(r.Width * i / 4, r.Height));
        if (Data is not { } d)
        {
            var ft = new FormattedText("No picture yet", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this)), 11, Muted);
            context.DrawText(ft, new Point((r.Width - ft.Width) / 2, (r.Height - ft.Height) / 2));
            return;
        }
        // Lumen caps the scale at 0.85 of the largest bin (a spike at black or white does not flatten the rest)
        double peak = Math.Max(1, d.Peak * 0.85);
        Area(context, d.Red, Red, r, peak);
        Area(context, d.Green, Green, r, peak);
        Area(context, d.Blue, Blue, r, peak);
    }

    static void Area(DrawingContext context, int[] bins, IBrush brush, Rect r, double peak)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(0, r.Height), true);
            for (int i = 0; i < bins.Length; i++)
            {
                double x = i * r.Width / (bins.Length - 1);
                double y = r.Height - Math.Min(1, bins[i] / peak) * (r.Height - 2);
                s.LineTo(new Point(x, y));
            }
            s.LineTo(new Point(r.Width, r.Height));
            s.EndFigure(true);
        }
        context.DrawGeometry(brush, null, g);
    }
}
