using System.Runtime.InteropServices;

namespace Lux.Engine.Pipeline;

/// <summary>cp.dll's <c>vec4x32f</c> pixel: R,G,B + a 4th lane (alpha / weight, kept at 1.0 by every stage).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vec4F
{
    public float R, G, B, A;
    public Vec4F(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
}

/// <summary>Integer rectangle [X0,X1)×[Y0,Y1) in some parent coordinate frame (cp.dll <c>Rectangle&lt;int&gt;</c>).</summary>
public readonly record struct RectI(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    public RectI Intersect(RectI o) => new(Math.Max(X0, o.X0), Math.Max(Y0, o.Y0), Math.Min(X1, o.X1), Math.Min(Y1, o.Y1));
    public RectI Inflate(int px) => new(X0 - px, Y0 - px, X1 + px, Y1 + px);
    public RectI Scale(double s) => new((int)Math.Floor(X0 * s), (int)Math.Floor(Y0 * s), (int)Math.Ceiling(X1 * s), (int)Math.Ceiling(Y1 * s));
}

/// <summary>
/// Mirror of <c>lt::Image&lt;T&gt;</c> (rect @0, width @0x10, height @0x14, stride @0x18, data @0x20): a row-major
/// buffer with a stride and the rectangle it covers in its parent (level) frame, so ROI/tile semantics match cp.dll.
/// </summary>
public sealed class Image<T> where T : unmanaged
{
    public RectI Rect { get; }
    public int Width => Rect.Width;
    public int Height => Rect.Height;
    public int Stride { get; }
    public T[] Data { get; }
    public int Offset { get; }

    public Image(RectI rect) : this(rect, new T[(long)rect.Width * rect.Height], rect.Width, 0) { }
    public Image(int width, int height) : this(new RectI(0, 0, width, height)) { }
    public Image(RectI rect, T[] data, int stride, int offset) { Rect = rect; Data = data; Stride = stride; Offset = offset; }

    public ref T At(int x, int y) => ref Data[Offset + (long)y * Stride + x];
    public Span<T> Row(int y) => Data.AsSpan(Offset + y * Stride, Width);

    /// <summary>A view of a sub-rectangle (parent coordinates) sharing this buffer.</summary>
    public Image<T> View(RectI r)
    {
        var c = r.Intersect(Rect);
        if (c.IsEmpty) throw new ArgumentException("view rectangle does not intersect the image");
        return new Image<T>(c, Data, Stride, Offset + (c.Y0 - Rect.Y0) * Stride + (c.X0 - Rect.X0));
    }

    public Image<T> Copy()
    {
        var o = new Image<T>(Rect);
        for (int y = 0; y < Height; y++) Row(y).CopyTo(o.Row(y));
        return o;
    }
}
