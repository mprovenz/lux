using Avalonia;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Viewer;

/// <summary>
/// A display turn: first mirrored left–right (<see cref="Mirror"/>), then turned clockwise by <see cref="Quarter"/> × 90°. The eight values
/// form the symmetry group of the rectangle; rotate / flip compose with it as Lumen's buttons do (flips act on the picture as it is shown).
/// The library delivers the picture already turned; the viewer uses a turn for its stand-ins (a thumbnail drawn as held, the old picture
/// held through a geometry change) and the fake uses it to make its pictures.
/// </summary>
public readonly record struct Turn(int Quarter, bool Mirror)
{
    public static readonly Turn Identity = new(0, false);

    public Turn RotateRight() => new((Quarter + 1) & 3, Mirror);
    public Turn RotateLeft() => new((Quarter + 3) & 3, Mirror);
    /// <summary>Mirror the picture as shown left–right: M·R(q) = R(−q)·M.</summary>
    public Turn FlipHorizontal() => new((4 - Quarter) & 3, !Mirror);
    /// <summary>Mirror the picture as shown top–bottom = R(2)·M.</summary>
    public Turn FlipVertical() => new((6 - Quarter) & 3, !Mirror);
    /// <summary>The turn that undoes this one (a mirrored turn undoes itself).</summary>
    public Turn Inverse() => Mirror ? this : new Turn((4 - Quarter) & 3, false);
    /// <summary>A plain clockwise turn of <paramref name="degrees"/> (multiples of 90).</summary>
    public static Turn FromDegrees(int degrees) => new((((degrees / 90) % 4) + 4) % 4, false);
    /// <summary>This turn followed by <paramref name="then"/>.</summary>
    public Turn Then(Turn then)
    {
        // (R(q2)·M^m2)·(R(q1)·M^m1) = R(q2 ± q1)·M^(m1⊕m2): a mirror in the second reverses the first's turn
        int q = then.Mirror ? (then.Quarter - Quarter) & 3 : (then.Quarter + Quarter) & 3;
        return new Turn(q, Mirror ^ then.Mirror);
    }
    /// <summary>Width and height trade places.</summary>
    public bool Swaps => (Quarter & 1) == 1;
    public int Degrees => Quarter * 90;

    public Size Apply(Size s) => Swaps ? new Size(s.Height, s.Width) : s;

    /// <summary>Maps the unrotated <paramref name="w"/> × <paramref name="h"/> rectangle onto the oriented frame (origin top-left).</summary>
    public Matrix Matrix(double w, double h)
    {
        var m = Avalonia.Matrix.Identity;
        double cw = w, ch = h;
        if (Mirror) m *= new Avalonia.Matrix(-1, 0, 0, 1, cw, 0);
        for (int i = 0; i < (Quarter & 3); i++)
        {
            // (x, y) → (H − y, x) on the current frame W × H; the frame becomes H × W
            m *= new Avalonia.Matrix(0, 1, -1, 0, ch, 0);
            (cw, ch) = (ch, cw);
        }
        return m;
    }

    /// <summary>The library's orientation names ("held" orientation) as a turn; null when not a pure turn.</summary>
    public static Turn? FromLibrary(Lux.Library.PhotoOrientation o) => o switch
    {
        Lux.Library.PhotoOrientation.Normal => Identity,
        Lux.Library.PhotoOrientation.Rotate90 => new Turn(1, false),
        Lux.Library.PhotoOrientation.Rotate180 => new Turn(2, false),
        Lux.Library.PhotoOrientation.Rotate270 => new Turn(3, false),
        _ => null,
    };
}

/// <summary>Helpers on the library's normalized rectangle (0–1 of the picture as shown).</summary>
public static class NormRects
{
    public static bool IsFull(this NormRect r) => Math.Abs(r.X) < 1e-9 && Math.Abs(r.Y) < 1e-9 && Math.Abs(r.Width - 1) < 1e-9 && Math.Abs(r.Height - 1) < 1e-9;
    public static Rect ToPixels(this NormRect r, Size frame) => new(r.X * frame.Width, r.Y * frame.Height, r.Width * frame.Width, r.Height * frame.Height);
    public static NormRect FromPixels(Rect r, Size frame) => new(r.X / frame.Width, r.Y / frame.Height, r.Width / frame.Width, r.Height / frame.Height);
    /// <summary>The same region after the frame turns clockwise: (x, y) → (1 − y, x).</summary>
    public static NormRect RotateRight(this NormRect r) => new(1 - r.Y - r.Height, r.X, r.Height, r.Width);
    /// <summary>After the frame turns counterclockwise: (x, y) → (y, 1 − x).</summary>
    public static NormRect RotateLeft(this NormRect r) => new(r.Y, 1 - r.X - r.Width, r.Height, r.Width);
    public static NormRect FlipHorizontal(this NormRect r) => new(1 - r.X - r.Width, r.Y, r.Width, r.Height);
    public static NormRect FlipVertical(this NormRect r) => new(r.X, 1 - r.Y - r.Height, r.Width, r.Height);
    public static NormRect Clamp(this NormRect r)
    {
        double w = Math.Clamp(r.Width, 0, 1), h = Math.Clamp(r.Height, 0, 1);
        return new NormRect(Math.Clamp(r.X, 0, 1 - w), Math.Clamp(r.Y, 0, 1 - h), w, h);
    }
    public static bool Near(this NormRect a, NormRect b, double eps = 1e-6) =>
        Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps && Math.Abs(a.Width - b.Width) < eps && Math.Abs(a.Height - b.Height) < eps;
}

/// <summary>
/// The level sizes and tile grid of a picture pyramid (a snapshot). "Image" coordinates are level-0 pixels; a level's pixels map to them by
/// its own scale (level sizes follow the library's export size rule, so they are not always exact halvings).
/// </summary>
public sealed class Pyramid
{
    readonly PixelSize[] _levels;

    public Pyramid(IReadOnlyList<PixelSize> levels, int tileSize)
    {
        if (levels.Count == 0) throw new ArgumentException("no levels", nameof(levels));
        _levels = levels.ToArray();
        TileSize = tileSize;
    }

    public static Pyramid Of(ITileSource s) => new(Enumerable.Range(0, s.LevelCount).Select(s.LevelSize).ToArray(), s.TileSize);

    /// <summary>Exact halvings (sizes rounded up), as the fake makes them.</summary>
    public static Pyramid Halving(PixelSize image, int levels, int tileSize) =>
        new(Enumerable.Range(0, levels).Select(l => new PixelSize((int)Math.Ceiling(image.Width / (double)(1 << l)), (int)Math.Ceiling(image.Height / (double)(1 << l)))).ToArray(), tileSize);

    public int TileSize { get; }
    public int LevelCount => _levels.Length;
    public int Coarsest => _levels.Length - 1;
    public PixelSize Image => _levels[0];
    public PixelSize Size(int level) => _levels[level];
    public Rect ImageRect => new(0, 0, Image.Width, Image.Height);

    /// <summary>Image pixels per level pixel, across and down.</summary>
    public (double X, double Y) ScaleOf(int level) => (Image.Width / (double)_levels[level].Width, Image.Height / (double)_levels[level].Height);

    public (int Cols, int Rows) Grid(int level)
    {
        var s = _levels[level];
        return ((s.Width + TileSize - 1) / TileSize, (s.Height + TileSize - 1) / TileSize);
    }

    /// <summary>A tile's rectangle in its level's pixels (smaller at the right and bottom edges).</summary>
    public Rect TileLevelRect(TileKey k)
    {
        var s = _levels[k.Level];
        int x = k.Col * TileSize, y = k.Row * TileSize;
        return new Rect(x, y, Math.Max(0, Math.Min(TileSize, s.Width - x)), Math.Max(0, Math.Min(TileSize, s.Height - y)));
    }

    /// <summary>A tile's rectangle in image pixels.</summary>
    public Rect TileImageRect(TileKey k)
    {
        var r = TileLevelRect(k);
        var (fx, fy) = ScaleOf(k.Level);
        return new Rect(r.X * fx, r.Y * fy, r.Width * fx, r.Height * fy);
    }

    /// <summary>The tiles of <paramref name="level"/> that intersect <paramref name="imageRect"/>, row by row.</summary>
    public IEnumerable<TileKey> TilesIn(int level, Rect imageRect)
    {
        if (imageRect.Width <= 0 || imageRect.Height <= 0) yield break;
        var (cols, rows) = Grid(level);
        var (fx, fy) = ScaleOf(level);
        double span = TileSize;
        int c0 = Math.Max(0, (int)Math.Floor(imageRect.X / fx / span)), r0 = Math.Max(0, (int)Math.Floor(imageRect.Y / fy / span));
        int c1 = Math.Min(cols - 1, (int)Math.Ceiling(imageRect.Right / fx / span) - 1), r1 = Math.Min(rows - 1, (int)Math.Ceiling(imageRect.Bottom / fy / span) - 1);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                yield return new TileKey(level, c, r);
    }

    /// <summary>The coarsest level that still has as many pixels as the screen shows (physical pixels per image pixel), never finer than
    /// <paramref name="finest"/>. With halving levels that is ⌊log2(1 / scale)⌋: never upscaled, at most halved. A positive
    /// <paramref name="bias"/> (in levels) prefers coarser levels.</summary>
    public int LevelFor(double physicalScale, int finest, double bias = 0)
    {
        finest = Math.Clamp(finest, 0, Coarsest);
        if (physicalScale <= 0 || double.IsNaN(physicalScale)) return Coarsest;
        double need = physicalScale * Math.Pow(2, -bias) * (1 - 1e-9);
        for (int l = Coarsest; l > finest; l--)
            if (_levels[l].Width / (double)Image.Width >= need) return l;
        return finest;
    }
}

/// <summary>
/// The viewer's coordinate systems at one moment (immutable):
/// <list type="bullet">
/// <item><b>image</b> — level-0 pixels of the picture as the source delivers it (already turned, flipped and cropped; in crop mode the
/// uncropped picture); level L maps to it by <see cref="Pyramid.ScaleOf"/>;</item>
/// <item><b>picture</b> — the same, normalized 0–1 (crop frames, overlays, the library's <c>NormRect</c>);</item>
/// <item><b>view</b> — device-independent pixels of the control: view = image · <see cref="Scale"/> + <see cref="Offset"/>.</item>
/// </list>
/// <see cref="Scale"/> is view units per image pixel; times <see cref="RenderScaling"/> it is physical pixels per image pixel (1 = "1:1").
/// </summary>
public sealed class ViewTransform
{
    public ViewTransform(PixelSize imageSize, double scale, Vector offset, double renderScaling)
    {
        ImageSize = imageSize;
        Scale = scale;
        Offset = offset;
        RenderScaling = renderScaling;
        ImageToView = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y);
        ViewToImage = ImageToView.Invert();
    }

    public PixelSize ImageSize { get; }
    public Size FrameSize => new(ImageSize.Width, ImageSize.Height);
    public double Scale { get; }
    public Vector Offset { get; }
    public double RenderScaling { get; }
    public Matrix ImageToView { get; }
    public Matrix ViewToImage { get; }

    /// <summary>Physical pixels per image pixel (1 = 1:1).</summary>
    public double PhysicalScale => Scale * RenderScaling;
    /// <summary>Where the picture lies in the control.</summary>
    public Rect DisplayRect => new(Offset.X, Offset.Y, ImageSize.Width * Scale, ImageSize.Height * Scale);

    public Point ToView(Point image) => image.Transform(ImageToView);
    public Point ToImage(Point view) => view.Transform(ViewToImage);
    public Point FrameToView(Point normalised) => new(normalised.X * ImageSize.Width * Scale + Offset.X, normalised.Y * ImageSize.Height * Scale + Offset.Y);
    public Point ViewToFrame(Point view) => new((view.X - Offset.X) / Scale / ImageSize.Width, (view.Y - Offset.Y) / Scale / ImageSize.Height);
    public Rect FrameToView(NormRect r)
    {
        var a = FrameToView(new Point(r.X, r.Y));
        var b = FrameToView(new Point(r.Right, r.Bottom));
        return new Rect(a, b);
    }
}

/// <summary>The viewer's arithmetic, separate from the control so it can be tested on its own.</summary>
public static class ViewerMath
{
    /// <summary>Largest zoom, physical pixels per image pixel.</summary>
    public const double MaxPhysicalScale = 8;

    public static double FitScale(Size viewport, Size display) =>
        display.Width <= 0 || display.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0 ? 1 : Math.Min(viewport.Width / display.Width, viewport.Height / display.Height);

    public static double FillScale(Size viewport, Size display) =>
        display.Width <= 0 || display.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0 ? 1 : Math.Max(viewport.Width / display.Width, viewport.Height / display.Height);

    /// <summary>The zoom range: from the fitted picture (or 1:1 when the picture is smaller than the view) to 8 physical pixels per image pixel.</summary>
    public static (double Min, double Max) ScaleRange(Size viewport, Size display, double renderScaling)
    {
        double fit = FitScale(viewport, display), one = 1 / renderScaling;
        double min = Math.Min(fit, one), max = MaxPhysicalScale / renderScaling;
        return (min, Math.Max(min, max));
    }

    /// <summary>Keep the picture in view: centered on an axis where it is smaller than the view, otherwise no empty margin.</summary>
    public static Vector ClampOffset(Vector offset, double scale, Size display, Size viewport)
    {
        static double Axis(double o, double content, double view) => content <= view ? (view - content) / 2 : Math.Clamp(o, view - content, 0);
        return new Vector(Axis(offset.X, display.Width * scale, viewport.Width), Axis(offset.Y, display.Height * scale, viewport.Height));
    }

    /// <summary>The offset that keeps the picture point under <paramref name="anchor"/> (view) fixed while the scale changes.</summary>
    public static Vector ZoomAbout(double scale, Vector offset, double newScale, Point anchor) =>
        new(anchor.X - (anchor.X - offset.X) * (newScale / scale), anchor.Y - (anchor.Y - offset.Y) * (newScale / scale));

    /// <summary>The image-pixel rectangle visible through <paramref name="viewRect"/> (the control or a clip of it), clipped to the picture
    /// and the displayed part of the frame.</summary>
    public static Rect VisibleImageRect(ViewTransform t, Rect viewRect)
    {
        var v = viewRect.Intersect(t.DisplayRect);
        if (v.Width <= 0 || v.Height <= 0) return default;
        var r = v.TransformToAABB(t.ViewToImage);
        return r.Intersect(new Rect(0, 0, t.ImageSize.Width, t.ImageSize.Height));
    }

    /// <summary>The library's normalized rectangle of an image-pixel rectangle.</summary>
    public static NormRect ToNorm(Rect imageRect, PixelSize image) =>
        new(imageRect.X / image.Width, imageRect.Y / image.Height, imageRect.Width / image.Width, imageRect.Height / image.Height);

    /// <summary>Snap a view rectangle to whole device pixels (no seams between tiles).</summary>
    public static Rect Snap(Rect r, double scaling)
    {
        double x0 = Math.Round(r.X * scaling) / scaling, y0 = Math.Round(r.Y * scaling) / scaling;
        double x1 = Math.Round(r.Right * scaling) / scaling, y1 = Math.Round(r.Bottom * scaling) / scaling;
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Zoom in percent as the status bar shows it (physical pixels per image pixel × 100).</summary>
    public static double Percent(double scale, double renderScaling) => scale * renderScaling * 100;
}

/// <summary>Aspect presets of the crop (Lumen's: landscape free, 4:3, 16:9, 3:2, 2:1, 1:1, and their portrait forms), with the library's
/// numbering (<see cref="Lux.Library.CropAspect"/>).</summary>
public sealed record AspectPreset(Lux.Library.CropAspect Aspect, string Label, double Ratio)
{
    /// <summary>Free (no aspect lock).</summary>
    public bool IsFree => Ratio <= 0;
    public bool IsPortrait => Ratio > 0 && Ratio < 1;
    public static readonly AspectPreset Free = new(Lux.Library.CropAspect.Free, "Free", 0);

    public static readonly IReadOnlyList<AspectPreset> Landscape = new[]
    {
        Free, new AspectPreset(Lux.Library.CropAspect.Ratio4x3, "4 : 3", 4 / 3.0), new AspectPreset(Lux.Library.CropAspect.Ratio16x9, "16 : 9", 16 / 9.0),
        new AspectPreset(Lux.Library.CropAspect.Ratio3x2, "3 : 2", 3 / 2.0), new AspectPreset(Lux.Library.CropAspect.Ratio2x1, "2 : 1", 2.0),
        new AspectPreset(Lux.Library.CropAspect.Ratio1x1, "1 : 1", 1.0),
    };

    public static readonly IReadOnlyList<AspectPreset> Portrait = new[]
    {
        Free, new AspectPreset(Lux.Library.CropAspect.Ratio3x4, "3 : 4", 3 / 4.0), new AspectPreset(Lux.Library.CropAspect.Ratio9x16, "9 : 16", 9 / 16.0),
        new AspectPreset(Lux.Library.CropAspect.Ratio2x3, "2 : 3", 2 / 3.0), new AspectPreset(Lux.Library.CropAspect.Ratio1x2, "1 : 2", 0.5),
        Landscape[5],
    };

    /// <summary>The same preset in the other orientation (4:3 ↔ 3:4); free and 1:1 stay.</summary>
    public AspectPreset Swapped()
    {
        if (IsFree || Math.Abs(Ratio - 1) < 1e-9) return this;
        var list = Ratio > 1 ? Portrait : Landscape;
        return list.FirstOrDefault(p => Math.Abs(p.Ratio - 1 / Ratio) < 1e-6) ?? this;
    }

    public static AspectPreset Of(Lux.Library.CropAspect a) => Landscape.Concat(Portrait).FirstOrDefault(p => p.Aspect == a) ?? Free;
    public override string ToString() => Label;
}

/// <summary>Crop rectangle arithmetic in frame pixels (aspect locks, handles, clamping).</summary>
public static class CropMath
{
    /// <summary>Handle positions: corners and edge midpoints.</summary>
    public enum Handle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    /// <summary>The largest rectangle of <paramref name="ratio"/> (width ÷ height) centered in <paramref name="within"/>.</summary>
    public static Rect Fit(Rect within, double ratio)
    {
        if (ratio <= 0) return within;
        double w = within.Width, h = w / ratio;
        if (h > within.Height) { h = within.Height; w = h * ratio; }
        return new Rect(within.Center.X - w / 2, within.Center.Y - h / 2, w, h);
    }

    /// <summary>Apply an aspect preset to a crop: the largest rectangle of that aspect centered inside the current crop (on an uncropped
    /// photo: the largest centered crop of that aspect).</summary>
    public static Rect ApplyAspect(Rect crop, Size frame, double ratio) => ratio <= 0 ? crop : ClampInto(Fit(crop, ratio), frame);

    /// <summary>Move a rectangle inside the frame (size kept when it fits, else shrunk keeping its aspect).</summary>
    public static Rect ClampInto(Rect r, Size frame)
    {
        double w = r.Width, h = r.Height;
        if (w > frame.Width) { h *= frame.Width / w; w = frame.Width; }
        if (h > frame.Height) { w *= frame.Height / h; h = frame.Height; }
        double x = Math.Clamp(r.X, 0, frame.Width - w), y = Math.Clamp(r.Y, 0, frame.Height - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// Drag <paramref name="handle"/> of <paramref name="start"/> by <paramref name="delta"/> (frame pixels), keeping <paramref name="ratio"/>
    /// when it is positive, never smaller than <paramref name="minSize"/>, never outside the frame.
    /// </summary>
    public static Rect Drag(Rect start, Handle handle, Vector delta, double ratio, Size frame, double minSize)
    {
        if (handle == Handle.Move)
            return ClampInto(start.Translate(delta), frame);
        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        bool left = handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool right = handle is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool top = handle is Handle.TopLeft or Handle.Top or Handle.TopRight;
        bool bottom = handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;
        if (left) l = Math.Clamp(l + delta.X, 0, r - minSize);
        if (right) r = Math.Clamp(r + delta.X, l + minSize, frame.Width);
        if (top) t = Math.Clamp(t + delta.Y, 0, b - minSize);
        if (bottom) b = Math.Clamp(b + delta.Y, t + minSize, frame.Height);
        if (ratio <= 0) return new Rect(l, t, r - l, b - t);

        // aspect lock: corners follow the larger change from the fixed corner; edges grow the other side about the center
        double w = r - l, h = b - t;
        bool corner = (left || right) && (top || bottom);
        if (corner)
        {
            if (w / h > ratio) w = h * ratio; else h = w / ratio;
            double ax = left ? start.Right : start.Left, ay = top ? start.Bottom : start.Top;
            double maxW = left ? ax : frame.Width - ax, maxH = top ? ay : frame.Height - ay;
            if (w > maxW) { w = maxW; h = w / ratio; }
            if (h > maxH) { h = maxH; w = h * ratio; }
            if (w < minSize) { w = minSize; h = w / ratio; }
            double x = left ? ax - w : ax, y = top ? ay - h : ay;
            return new Rect(x, y, w, h);
        }
        if (left || right)
        {
            h = w / ratio;
            double cy = start.Center.Y;
            if (h > frame.Height) { h = frame.Height; w = h * ratio; if (left) l = r - w; else r = l + w; }
            double y = Math.Clamp(cy - h / 2, 0, frame.Height - h);
            return new Rect(left ? r - w : l, y, w, h);
        }
        w = h * ratio;
        double cx = start.Center.X;
        if (w > frame.Width) { w = frame.Width; h = w / ratio; if (top) t = b - h; else b = t + h; }
        double xx = Math.Clamp(cx - w / 2, 0, frame.Width - w);
        return new Rect(xx, top ? b - h : t, w, h);
    }

    /// <summary>The handle under a view point (corners first), within <paramref name="reach"/> view units; Move inside; None outside.</summary>
    public static Handle HitTest(Rect viewRect, Point p, double reach)
    {
        bool nearL = Math.Abs(p.X - viewRect.Left) <= reach, nearR = Math.Abs(p.X - viewRect.Right) <= reach;
        bool nearT = Math.Abs(p.Y - viewRect.Top) <= reach, nearB = Math.Abs(p.Y - viewRect.Bottom) <= reach;
        bool inX = p.X >= viewRect.Left - reach && p.X <= viewRect.Right + reach, inY = p.Y >= viewRect.Top - reach && p.Y <= viewRect.Bottom + reach;
        if (nearL && nearT) return Handle.TopLeft;
        if (nearR && nearT) return Handle.TopRight;
        if (nearL && nearB) return Handle.BottomLeft;
        if (nearR && nearB) return Handle.BottomRight;
        if (nearT && inX) return Handle.Top;
        if (nearB && inX) return Handle.Bottom;
        if (nearL && inY) return Handle.Left;
        if (nearR && inY) return Handle.Right;
        return viewRect.Contains(p) ? Handle.Move : Handle.None;
    }
}
