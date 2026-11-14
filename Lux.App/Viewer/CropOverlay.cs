using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Viewer;

/// <summary>
/// The crop frame (crop mode): the whole photo fitted with the area outside the crop shaded, the frame with corner and edge handles, the
/// rule-of-thirds guides, and the crop size while dragging. Drag a handle to resize (keeping <see cref="Ratio"/> when set), drag inside to
/// move. The rectangle is normalised in the picture shown (the uncropped, turned photo); the page applies it on Enter and drops it on Esc.
/// </summary>
public sealed class CropOverlay : ViewerOverlay
{
    static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(0x99, 0x1c, 0x1c, 0x1c));
    static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#f2f2f2")), 1);
    static readonly IPen FrameOutline = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3);
    static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(0x8c, 0xf2, 0xf2, 0xf2)), 1);
    static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#a9dcff")), 3, lineCap: PenLineCap.Square);
    static readonly IBrush LabelGround = new SolidColorBrush(Color.Parse("#262626"));
    static readonly IBrush LabelText = new SolidColorBrush(Color.Parse("#f2f2f2"));

    NormRect _rect = NormRect.Full;
    double _ratio;
    CropMath.Handle _drag = CropMath.Handle.None;
    Rect _dragStart;
    Point _pressFrame;

    public override OverlayLayer Layer => OverlayLayer.Guides;
    public override bool LocksView => IsVisible;
    /// <summary>Handle reach in view units.</summary>
    public double Reach { get; set; } = 12;
    public bool IsDragging => _drag != CropMath.Handle.None;
    public CropMath.Handle ActiveHandle => _drag;

    /// <summary>The crop, normalised in the oriented frame.</summary>
    public NormRect Rect
    {
        get => _rect;
        set { var v = value.Clamp(); if (_rect.Near(v, 1e-9)) return; _rect = v; Invalidate(); Changed?.Invoke(); }
    }

    /// <summary>Aspect lock, width ÷ height in pixels; 0 = free.</summary>
    public double Ratio
    {
        get => _ratio;
        set { _ratio = value; Invalidate(); }
    }

    /// <summary>The rectangle changed (dragging or set).</summary>
    public event Action? Changed;
    /// <summary>A drag ended.</summary>
    public event Action? DragEnded;

    public override void Render(DrawingContext context, ViewTransform t, Rect bounds)
    {
        var photo = t.DisplayRect;
        var r = t.FrameToView(_rect);
        // shade outside the crop
        context.FillRectangle(Shade, new Rect(photo.Left, photo.Top, photo.Width, Math.Max(0, r.Top - photo.Top)));
        context.FillRectangle(Shade, new Rect(photo.Left, r.Bottom, photo.Width, Math.Max(0, photo.Bottom - r.Bottom)));
        context.FillRectangle(Shade, new Rect(photo.Left, r.Top, Math.Max(0, r.Left - photo.Left), r.Height));
        context.FillRectangle(Shade, new Rect(r.Right, r.Top, Math.Max(0, photo.Right - r.Right), r.Height));
        // thirds
        for (int i = 1; i <= 2; i++)
        {
            double x = r.Left + r.Width * i / 3, y = r.Top + r.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, r.Top), new Point(x, r.Bottom));
            context.DrawLine(ThirdsPen, new Point(r.Left, y), new Point(r.Right, y));
        }
        context.DrawRectangle(null, FrameOutline, r);
        context.DrawRectangle(null, FramePen, r);
        // corner brackets and edge ticks
        double len = Math.Min(18, Math.Min(r.Width, r.Height) / 3);
        void L(Point a, Point b) => context.DrawLine(HandlePen, a, b);
        L(r.TopLeft, r.TopLeft + new Vector(len, 0)); L(r.TopLeft, r.TopLeft + new Vector(0, len));
        L(r.TopRight, r.TopRight + new Vector(-len, 0)); L(r.TopRight, r.TopRight + new Vector(0, len));
        L(r.BottomLeft, r.BottomLeft + new Vector(len, 0)); L(r.BottomLeft, r.BottomLeft + new Vector(0, -len));
        L(r.BottomRight, r.BottomRight + new Vector(-len, 0)); L(r.BottomRight, r.BottomRight + new Vector(0, -len));
        double tick = len / 2;
        L(new Point(r.Center.X - tick, r.Top), new Point(r.Center.X + tick, r.Top));
        L(new Point(r.Center.X - tick, r.Bottom), new Point(r.Center.X + tick, r.Bottom));
        L(new Point(r.Left, r.Center.Y - tick), new Point(r.Left, r.Center.Y + tick));
        L(new Point(r.Right, r.Center.Y - tick), new Point(r.Right, r.Center.Y + tick));
        // size while dragging
        if (IsDragging)
        {
            var px = _rect.ToPixels(t.FrameSize);
            var text = new FormattedText($"{Math.Round(px.Width)} × {Math.Round(px.Height)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Viewer?.MonoFont ?? FontFamily.Default), 12, LabelText);
            var at = new Point(r.Left + 8, r.Top + 8);
            context.FillRectangle(LabelGround, new Rect(at.X - 6, at.Y - 2, text.Width + 12, text.Height + 4));
            context.DrawText(text, at);
        }
    }

    public override bool OnPressed(ViewerPointer p, ViewTransform t)
    {
        if (!p.IsLeft) return false;
        var h = CropMath.HitTest(t.FrameToView(_rect), p.View, Reach);
        if (h == CropMath.Handle.None) return false;
        _drag = h;
        _dragStart = _rect.ToPixels(t.FrameSize);
        _pressFrame = new Point(p.Frame.X * t.FrameSize.Width, p.Frame.Y * t.FrameSize.Height);
        Invalidate();
        return true;
    }

    public override void OnMoved(ViewerPointer p, ViewTransform t)
    {
        if (!IsDragging) return;
        var now = new Point(p.Frame.X * t.FrameSize.Width, p.Frame.Y * t.FrameSize.Height);
        double min = Math.Max(16, Math.Min(t.FrameSize.Width, t.FrameSize.Height) * 0.02);
        var r = CropMath.Drag(_dragStart, _drag, now - _pressFrame, _ratio, t.FrameSize, min);
        Rect = NormRects.FromPixels(r, t.FrameSize);
    }

    public override void OnReleased(ViewerPointer p, ViewTransform t)
    {
        if (!IsDragging) return;
        _drag = CropMath.Handle.None;
        Invalidate();
        DragEnded?.Invoke();
    }

    public override Cursor? CursorAt(Point view, ViewTransform t) => CropMath.HitTest(t.FrameToView(_rect), view, Reach) switch
    {
        CropMath.Handle.TopLeft => Cursors.Get(StandardCursorType.TopLeftCorner),
        CropMath.Handle.TopRight => Cursors.Get(StandardCursorType.TopRightCorner),
        CropMath.Handle.BottomLeft => Cursors.Get(StandardCursorType.BottomLeftCorner),
        CropMath.Handle.BottomRight => Cursors.Get(StandardCursorType.BottomRightCorner),
        CropMath.Handle.Top => Cursors.Get(StandardCursorType.TopSide),
        CropMath.Handle.Bottom => Cursors.Get(StandardCursorType.BottomSide),
        CropMath.Handle.Left => Cursors.Get(StandardCursorType.LeftSide),
        CropMath.Handle.Right => Cursors.Get(StandardCursorType.RightSide),
        CropMath.Handle.Move => Cursors.Get(StandardCursorType.SizeAll),
        _ => null,
    };
}

/// <summary>Standard cursors made once (UI thread).</summary>
public static class Cursors
{
    static readonly Dictionary<StandardCursorType, Cursor> Made = new();
    public static Cursor Get(StandardCursorType type)
    {
        if (!Made.TryGetValue(type, out var c)) Made[type] = c = new Cursor(type);
        return c;
    }
}
