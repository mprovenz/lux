using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Lux.App.Controls;

/// <summary>
/// The "Processing image" spinner and label (user design) drawn over a blurred picture: shared by <see cref="ImageStateView"/> (thumbnails,
/// details preview) and the viewer. Callers keep animating by requesting frames while it shows.
/// </summary>
public sealed class ProcessingIndicator
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static readonly IBrush Scrim = new SolidColorBrush(Color.FromArgb(0x70, 0x1c, 0x1c, 0x1c));
    static readonly IBrush SpinnerTrack = new SolidColorBrush(Color.FromArgb(0x55, 0xf2, 0xf2, 0xf2));
    static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#f2f2f2"));
    static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#a9dcff"));

    FormattedText? _labelText;
    string? _labelKey;

    /// <summary>Draw the spinner centered in <paramref name="r"/> and, when <paramref name="label"/> is set, the text under it ("Processing
    /// image · 42 %" with a known progress).</summary>
    public void Draw(DrawingContext context, Rect r, string? label, double progress, FontFamily font, double maxRadius = 16)
    {
        double radius = Math.Clamp(Math.Min(r.Width, r.Height) * 0.08, 6, maxRadius);
        var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2 - (label != null ? 10 : 0));
        double thickness = radius > 10 ? 3 : 2;
        context.DrawEllipse(null, new Pen(SpinnerTrack, thickness), centre, radius, radius);
        double angle = Clock.Elapsed.TotalSeconds * 300 % 360;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            var a0 = angle * Math.PI / 180; var a1 = (angle + 100) * Math.PI / 180;
            g.BeginFigure(new Point(centre.X + radius * Math.Cos(a0), centre.Y + radius * Math.Sin(a0)), false);
            g.ArcTo(new Point(centre.X + radius * Math.Cos(a1), centre.Y + radius * Math.Sin(a1)), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(AccentBrush, thickness, lineCap: PenLineCap.Round), arc);
        if (label == null) return;
        string text = double.IsNaN(progress) || progress <= 0 ? label : $"{label} · {(int)Math.Round(progress * 100)} %";
        if (_labelKey != text || _labelText == null)
        {
            _labelKey = text;
            _labelText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font), 12, LabelBrush);
        }
        var ft = _labelText;
        context.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, centre.Y + radius + 8));
    }

    /// <summary>The label text last drawn (tests).</summary>
    public string? LastLabel => _labelKey;
}
