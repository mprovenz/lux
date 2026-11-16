using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Lux.Library;

namespace Lux.App.Viewer;

// The depth tools' overlays: the focus pin and the picked reference point (Markers), the brush circle and the stroke being drawn, the
// lasso, the click-to-pick cursor (Cursor), and the pointer mirrored onto the depth view. Points are normalized in the picture as shown
// (ViewerPointer.Frame), so the overlays stay attached to the picture while it pans and zooms, and what they report is what the user saw.

/// <summary>Pins on the picture: the focus point (white ring) and match depth's picked reference point (accent ring with a dot).</summary>
public sealed class PinOverlay : ViewerOverlay
{
    static readonly IPen Ring = new Pen(new SolidColorBrush(Color.Parse("#ffffff")), 2);
    static readonly IPen RingOutline = new Pen(new SolidColorBrush(Color.FromArgb(0xcc, 0x1c, 0x1c, 0x1c)), 4);
    static readonly IPen RefRing = new Pen(new SolidColorBrush(Color.Parse("#a9dcff")), 2);
    static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#ffffff"));
    static readonly IBrush RefDot = new SolidColorBrush(Color.Parse("#a9dcff"));
    NormPoint? _focus, _reference;

    public override OverlayLayer Layer => OverlayLayer.Markers;

    /// <summary>The focus pin (null: none shown).</summary>
    public NormPoint? Focus { get => _focus; set { if (_focus == value) return; _focus = value; Invalidate(); } }
    /// <summary>Match depth's picked point (null: none shown).</summary>
    public NormPoint? Reference { get => _reference; set { if (_reference == value) return; _reference = value; Invalidate(); } }

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        if (_focus is { } f)
        {
            var c = t.FrameToView(new Point(f.X, f.Y));
            context.DrawEllipse(null, RingOutline, c, 10, 10);
            context.DrawEllipse(null, Ring, c, 10, 10);
            context.DrawEllipse(Dot, null, c, 2, 2);
        }
        if (_reference is { } r)
        {
            var c = t.FrameToView(new Point(r.X, r.Y));
            context.DrawEllipse(null, RingOutline, c, 7, 7);
            context.DrawEllipse(null, RefRing, c, 7, 7);
            context.DrawEllipse(RefDot, null, c, 2.5, 2.5);
        }
    }
}

/// <summary>
/// A freehand brush: the circle at the pointer (its diameter <see cref="BrushPx"/> picture pixels at full size, so it grows with the zoom),
/// and the stroke being drawn (a translucent band of the brush's width with a center line — white when adding, dark when removing). The press
/// starts a stroke, every move reports the new points at once (the page forwards them without waiting), the release ends it. While
/// <see cref="Enabled"/> is false the circle is drawn dimmed and a press is not taken (the tool explains why elsewhere).
/// </summary>
public sealed class BrushOverlay : ViewerOverlay
{
    static readonly IPen CircleOutline = new Pen(new SolidColorBrush(Color.FromArgb(0xcc, 0x1c, 0x1c, 0x1c)), 3);
    static readonly IPen Circle = new Pen(new SolidColorBrush(Color.Parse("#ffffff")), 1.5);
    static readonly IPen CircleOff = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xa8, 0xa8, 0xa8)), 1.5, dashStyle: new DashStyle(new double[] { 3, 3 }, 0));
    static readonly IBrush CentreDot = new SolidColorBrush(Color.Parse("#ffffff"));
    readonly List<NormPoint> _stroke = new();
    readonly List<List<NormPoint>> _kept = new();
    readonly List<bool> _keptDark = new();
    readonly List<int> _keptIds = new();
    int _nextId;
    Point? _hover;
    bool _drawing;
    double _brushPx = 60;

    public override OverlayLayer Layer => OverlayLayer.Cursor;
    /// <summary>Brush diameter in picture pixels at full size.</summary>
    public double BrushPx { get => _brushPx; set { if (Math.Abs(_brushPx - value) < 1e-9) return; _brushPx = value; Invalidate(); } }
    /// <summary>The stroke is drawn dark (quick select's "Remove").</summary>
    public bool Dark { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Keep finished strokes on screen (quick select: the strokes stay until Apply / Clear).</summary>
    public bool KeepStrokes { get; set; }
    public bool IsDrawing => _drawing;
    public IReadOnlyList<NormPoint> CurrentStroke => _stroke;
    public int KeptStrokes => _kept.Count;
    /// <summary>Pointer position over the picture (view units), for tests.</summary>
    public Point? HoverPoint => _hover;

    public event Action<NormPoint>? StrokeStarted;
    /// <summary>New points of the stroke (the ones added since the last call).</summary>
    public event Action<IReadOnlyList<NormPoint>>? StrokeMoved;
    public event Action? StrokeEnded;
    /// <summary>A press while disabled (the page explains why).</summary>
    public event Action? Refused;
    /// <summary>Alt + click: pick the depth at the point instead (match depth's shortcut for "Pick from photo").</summary>
    public Action<NormPoint>? AltPick { get; set; }

    public void ClearStrokes() { _kept.Clear(); _keptDark.Clear(); _keptIds.Clear(); _stroke.Clear(); Invalidate(); }

    /// <summary>The id of the stroke kept at the last release (−1 when it was not kept).</summary>
    public int LastKeptId { get; private set; } = -1;

    /// <summary>Remove one kept stroke (a quick-select stroke that was stopped).</summary>
    public void RemoveKept(int id)
    {
        int i = _keptIds.IndexOf(id);
        if (i < 0) return;
        _kept.RemoveAt(i); _keptDark.RemoveAt(i); _keptIds.RemoveAt(i);
        Invalidate();
    }

    /// <summary>The brush's radius in view units now.</summary>
    public static double ViewRadius(double brushPx, ViewTransform t) => Math.Max(1.5, brushPx / 2 * t.Scale);

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        double r = ViewRadius(_brushPx, t);
        for (int i = 0; i < _kept.Count; i++) DrawStroke(context, t, _kept[i], _keptDark[i], r);
        if (_stroke.Count > 0) DrawStroke(context, t, _stroke, Dark, r);
        if (_hover is { } h && (t.DisplayRect.Inflate(r).Contains(h) || _drawing))
        {
            if (!Enabled) { context.DrawEllipse(null, CircleOff, h, r, r); return; }
            context.DrawEllipse(null, CircleOutline, h, r, r);
            context.DrawEllipse(null, Circle, h, r, r);
            context.DrawEllipse(CentreDot, null, h, 1.2, 1.2);
        }
    }

    static void DrawStroke(DrawingContext context, ViewTransform t, IReadOnlyList<NormPoint> pts, bool dark, double r)
    {
        var band = new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x70, 0x1c, 0x1c, 0x1c) : Color.FromArgb(0x55, 0xff, 0xff, 0xff)), r * 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var line = new Pen(new SolidColorBrush(dark ? Color.Parse("#1c1c1c") : Color.Parse("#ffffff")), 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (pts.Count == 1)
        {
            var c = t.FrameToView(new Point(pts[0].X, pts[0].Y));
            context.DrawEllipse(band.Brush, null, c, r, r);
            context.DrawEllipse(line.Brush, null, c, 1.5, 1.5);
            return;
        }
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(t.FrameToView(new Point(pts[0].X, pts[0].Y)), false);
            for (int i = 1; i < pts.Count; i++) s.LineTo(t.FrameToView(new Point(pts[i].X, pts[i].Y)));
            s.EndFigure(false);
        }
        context.DrawGeometry(null, band, g);
        context.DrawGeometry(null, line, g);
    }

    public override bool OnPressed(ViewerPointer p, ViewTransform t)
    {
        if (!p.IsLeft) return false;
        if (!t.DisplayRect.Contains(p.View)) return false;
        if (p.Modifiers.HasFlag(KeyModifiers.Alt) && AltPick != null) { AltPick(new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1))); return true; }
        if (!Enabled) { Refused?.Invoke(); return true; }
        _drawing = true;
        _hover = p.View;
        _stroke.Clear();
        var n = new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1));
        _stroke.Add(n);
        StrokeStarted?.Invoke(n);
        Invalidate();
        return true;
    }

    public override void OnMoved(ViewerPointer p, ViewTransform t)
    {
        _hover = p.View;
        if (!_drawing) { Invalidate(); return; }
        var n = new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1));
        var last = _stroke[^1];
        // skip moves under half a screen pixel (the stroke stays light at any zoom)
        double dx = (n.X - last.X) * t.ImageSize.Width * t.Scale, dy = (n.Y - last.Y) * t.ImageSize.Height * t.Scale;
        if (dx * dx + dy * dy < 0.25) return;
        _stroke.Add(n);
        StrokeMoved?.Invoke(new[] { n });
        Invalidate();
    }

    public override void OnReleased(ViewerPointer p, ViewTransform t)
    {
        if (!_drawing) return;
        _drawing = false;
        LastKeptId = -1;
        if (KeepStrokes && _stroke.Count > 0) { _kept.Add(new List<NormPoint>(_stroke)); _keptDark.Add(Dark); _keptIds.Add(LastKeptId = _nextId++); }
        _stroke.Clear();
        StrokeEnded?.Invoke();
        Invalidate();
    }

    public override Cursor? CursorAt(Point view, ViewTransform t) => t.DisplayRect.Contains(view) ? Cursors.Get(StandardCursorType.None) : null;

    public override void OnHover(Point? view, ViewTransform t)
    {
        if (_hover == view) return;
        _hover = view;
        Invalidate();
    }
}

/// <summary>
/// Match depth's lasso: click to place corners (a line follows the pointer to the next one; click the first corner, double-click or press
/// Enter to close it; Backspace removes the last corner), or press and drag to draw it freehand (closed on release). Each placed corner and each
/// freehand outline is reported (the session takes them; three corners make an area). A click after the lasso was closed starts a new one.
/// </summary>
public sealed class LassoOverlay : ViewerOverlay
{
    static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#ffffff")), 1.5);
    static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(0xcc, 0x1c, 0x1c, 0x1c)), 3.5);
    static readonly IPen Rubber = new Pen(new SolidColorBrush(Color.FromArgb(0xcc, 0xff, 0xff, 0xff)), 1, dashStyle: new DashStyle(new double[] { 4, 3 }, 0));
    static readonly IBrush Corner = new SolidColorBrush(Color.Parse("#ffffff"));
    static readonly IBrush FirstCorner = new SolidColorBrush(Color.Parse("#a9dcff"));
    readonly List<NormPoint> _points = new();
    Point? _hover, _press;
    NormPoint _pressPoint;
    bool _dragging, _freehand, _pressAdds;

    public override OverlayLayer Layer => OverlayLayer.Cursor;
    public IReadOnlyList<NormPoint> Points => _points;
    public bool IsClosed { get; private set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Distance in view units within which a click on the first corner closes the polygon.</summary>
    public double CloseReach { get; set; } = 10;
    /// <summary>Alt + click: pick the depth at the point instead (match depth's shortcut for "Pick from photo").</summary>
    public Action<NormPoint>? AltPick { get; set; }

    /// <summary>A corner was placed by a click.</summary>
    public event Action<NormPoint>? CornerAdded;
    /// <summary>A freehand outline was drawn (closed on release).</summary>
    public event Action<IReadOnlyList<NormPoint>>? FreehandDrawn;
    /// <summary>A new lasso starts (the previous one, closed, is dropped).</summary>
    public event Action? Restarted;
    public event Action? Refused;

    public void Clear() { _points.Clear(); IsClosed = false; _dragging = false; _freehand = false; Invalidate(); }

    /// <summary>Close the polygon (Enter, a click on the first corner, a double-click); false when it has fewer than three corners.</summary>
    public bool Close()
    {
        if (IsClosed || _points.Count < 3) return false;
        IsClosed = true;
        Invalidate();
        return true;
    }

    public bool RemoveLast()
    {
        if (IsClosed || _points.Count == 0) return false;
        _points.RemoveAt(_points.Count - 1);
        Invalidate();
        return true;
    }

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        if (_points.Count == 0) return;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(V(t, _points[0]), false);
            for (int i = 1; i < _points.Count; i++) s.LineTo(V(t, _points[i]));
            s.EndFigure(IsClosed);
        }
        context.DrawGeometry(null, Outline, g);
        context.DrawGeometry(null, Line, g);
        if (!IsClosed && !_freehand && _hover is { } h) context.DrawLine(Rubber, V(t, _points[^1]), h);
        if (!_freehand && _points.Count < 400)
            for (int i = 0; i < _points.Count; i++)
                context.DrawRectangle(i == 0 ? FirstCorner : Corner, Outline, new Rect(V(t, _points[i]) - new Vector(3, 3), new Size(6, 6)));
    }

    static Point V(ViewTransform t, NormPoint p) => t.FrameToView(new Point(p.X, p.Y));

    public override bool OnPressed(ViewerPointer p, ViewTransform t)
    {
        if (!p.IsLeft || !t.DisplayRect.Contains(p.View)) return false;
        var n = new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1));
        if (p.Modifiers.HasFlag(KeyModifiers.Alt) && AltPick != null) { AltPick(n); return true; }
        if (!Enabled) { Refused?.Invoke(); return true; }
        _press = p.View;
        _pressPoint = n;
        _dragging = true;
        _pressAdds = false;
        if (IsClosed) { Clear(); Restarted?.Invoke(); }
        else if (_points.Count >= 3 && (V(t, _points[0]) - p.View) is var d && Math.Sqrt(d.X * d.X + d.Y * d.Y) <= CloseReach) { _dragging = false; Close(); return true; }
        else if (p.ClickCount >= 2 && _points.Count >= 3) { _dragging = false; Close(); return true; }
        _points.Add(n);
        _pressAdds = true;
        Invalidate();
        return true;
    }

    public override void OnMoved(ViewerPointer p, ViewTransform t)
    {
        _hover = p.View;
        if (_dragging && _press is { } start)
        {
            var d = p.View - start;
            // a press that moves more than a few pixels on an empty lasso draws it freehand (from the press point)
            if (!_freehand && Math.Sqrt(d.X * d.X + d.Y * d.Y) > 6 && _points.Count == 1) _freehand = true;
            if (_freehand)
            {
                var n = new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1));
                var last = _points[^1];
                double dx = (n.X - last.X) * t.ImageSize.Width * t.Scale, dy = (n.Y - last.Y) * t.ImageSize.Height * t.Scale;
                if (dx * dx + dy * dy >= 4) _points.Add(n);
            }
        }
        Invalidate();
    }

    public override void OnReleased(ViewerPointer p, ViewTransform t)
    {
        bool was = _dragging;
        _dragging = false;
        if (_freehand)
        {
            _freehand = false;
            if (_points.Count >= 3) { IsClosed = true; Invalidate(); FreehandDrawn?.Invoke(_points.ToList()); }
            else Clear();
            return;
        }
        if (was && _pressAdds) CornerAdded?.Invoke(_pressPoint);
    }

    public override void OnHover(Point? view, ViewTransform t)
    {
        _hover = view;
        if (_points.Count > 0 && !IsClosed) Invalidate();
    }

    public override Cursor? CursorAt(Point view, ViewTransform t) => t.DisplayRect.Contains(view) ? Cursors.Get(StandardCursorType.Cross) : null;

    public override bool OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None) return RemoveLast();
        return false;
    }
}

/// <summary>A small label at the picture's top left ("Photo · f/2.0" in Photo + depth).</summary>
public sealed class LabelOverlay : ViewerOverlay
{
    static readonly IBrush Ground = new SolidColorBrush(Color.Parse("#262626"));
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#f2f2f2"));
    string _text = "";
    public override OverlayLayer Layer => OverlayLayer.Guides;
    public string Text { get => _text; set { if (_text == value) return; _text = value; Invalidate(); } }

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        if (_text.Length == 0) return;
        var photo = t.DisplayRect.Intersect(bounds);
        var ft = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(Viewer!)), 12, TextBrush);
        var r = new Rect(photo.Left + 8, photo.Top + 8, ft.Width + 12, ft.Height + 4);
        context.FillRectangle(Ground, r);
        context.DrawText(ft, new Point(r.X + 6, r.Y + 2));
    }
}

/// <summary>
/// Click to pick a point (the focus tool, match depth's "Pick from photo"): a crosshair where the point can be used, the "not allowed" cursor
/// where it cannot (<see cref="CanUse"/>, from the depth already in memory), and the click reported in picture coordinates.
/// </summary>
public sealed class PointPickOverlay : ViewerOverlay
{
    public override OverlayLayer Layer => OverlayLayer.Cursor;
    public override void Render(DrawingContext context, ViewTransform t, Rect bounds) { }
    /// <summary>Whether a picture point can be used (null: any).</summary>
    public Func<NormPoint, bool>? CanUse { get; set; }
    public event Action<NormPoint>? Picked;

    public override bool OnPressed(ViewerPointer p, ViewTransform t)
    {
        if (!p.IsLeft || !t.DisplayRect.Contains(p.View) || p.ClickCount >= 2) return p.ClickCount >= 2 && t.DisplayRect.Contains(p.View);
        Picked?.Invoke(new NormPoint(Math.Clamp(p.Frame.X, 0, 1), Math.Clamp(p.Frame.Y, 0, 1)));
        return true;
    }

    public override Cursor? CursorAt(Point view, ViewTransform t)
    {
        if (!t.DisplayRect.Contains(view)) return null;
        var f = t.ViewToFrame(view);
        bool ok = CanUse?.Invoke(new NormPoint(f.X, f.Y)) ?? true;
        return Cursors.Get(ok ? StandardCursorType.Cross : StandardCursorType.No);
    }
}

/// <summary>The pointer of the other view of "Photo + depth" (a ring of the brush's size, or a small cross), so both pictures show where
/// the pointer is.</summary>
public sealed class MirrorOverlay : ViewerOverlay
{
    static readonly IPen Ring = new Pen(new SolidColorBrush(Color.FromArgb(0xdd, 0xa9, 0xdc, 0xff)), 1.5);
    static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x1c, 0x1c, 0x1c)), 3);
    NormPoint? _at;
    double _brushPx;

    public override OverlayLayer Layer => OverlayLayer.Cursor;
    public NormPoint? At { get => _at; set { if (_at == value) return; _at = value; Invalidate(); } }
    /// <summary>The brush diameter (picture pixels) to draw; 0 = a small cross.</summary>
    public double BrushPx { get => _brushPx; set { if (Math.Abs(_brushPx - value) < 1e-9) return; _brushPx = value; Invalidate(); } }

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        if (_at is not { } a) return;
        var c = t.FrameToView(new Point(a.X, a.Y));
        if (_brushPx > 0)
        {
            double r = BrushOverlay.ViewRadius(_brushPx, t);
            context.DrawEllipse(null, Outline, c, r, r);
            context.DrawEllipse(null, Ring, c, r, r);
            return;
        }
        context.DrawLine(Outline, c - new Vector(7, 0), c + new Vector(7, 0));
        context.DrawLine(Outline, c - new Vector(0, 7), c + new Vector(0, 7));
        context.DrawLine(Ring, c - new Vector(7, 0), c + new Vector(7, 0));
        context.DrawLine(Ring, c - new Vector(0, 7), c + new Vector(0, 7));
    }
}

/// <summary>The depth view's legend (bottom left): a ramp from near (light) to far (dark) with the two distances, and "Depth · near is light".</summary>
public sealed class DepthLegendOverlay : ViewerOverlay
{
    static readonly IBrush Ground = new SolidColorBrush(Color.FromArgb(0xe6, 0x26, 0x26, 0x26));
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#f2f2f2"));
    static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#d6d6d6"));
    static readonly IPen Frame = new Pen(new SolidColorBrush(Color.Parse("#6a6a6a")), 1);
    string _near = "", _far = "";

    public override OverlayLayer Layer => OverlayLayer.Guides;
    public void SetRange(string near, string far) { _near = near; _far = far; Invalidate(); }
    public string Text => $"Depth · near is light · {_near} – {_far}";

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        var photo = t.DisplayRect.Intersect(bounds);
        if (photo.Width < 160 || photo.Height < 80) return;
        var face = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(Viewer!));
        var mono = new Typeface(Viewer?.MonoFont ?? FontFamily.Default);
        var title = new FormattedText("Depth · near is light", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12, TextBrush);
        var near = new FormattedText(_near, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, 11, Secondary);
        var far = new FormattedText(_far, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, 11, Secondary);
        double w = Math.Max(150, title.Width + 12), x = photo.Left + 8, y = photo.Top + 8;
        var box = new Rect(x, y, w, 50);
        context.FillRectangle(Ground, box);
        context.DrawText(title, new Point(x + 6, y + 4));
        var ramp = new Rect(x + 6, y + 22, w - 12, 8);
        var g = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#f2f2f2"), 0), new GradientStop(Color.Parse("#262626"), 1) },
        };
        context.FillRectangle(g, ramp);
        context.DrawRectangle(null, Frame, ramp);
        context.DrawText(near, new Point(ramp.Left, ramp.Bottom + 2));
        context.DrawText(far, new Point(ramp.Right - far.Width, ramp.Bottom + 2));
    }
}
