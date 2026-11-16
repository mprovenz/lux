namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// Float image with <see cref="C"/> lanes per pixel and Lumen's <c>lt::Image</c> sub-view semantics: a window of
/// <see cref="W"/>×<see cref="H"/> pixels whose rect fields (<see cref="X0"/>…<see cref="Y1"/>) are the allocation's frame expressed
/// in window coordinates. A fresh image has the rect (0,0,W,H); a view made by <see cref="View"/> keeps the parent's frame shifted by
/// the window origin, so a kernel that clamps to the rect reads real pixels beyond the window (the buffer crop of the depth effect,
/// the crop inside the band blur). Pixel (x,y) of the window is at <c>D[Off + (y·Stride + x)·C]</c>.
/// </summary>
public sealed class DofImage
{
    public readonly float[] D;
    public readonly int C, W, H, Stride, Off;
    public readonly int X0, Y0, X1, Y1;

    public DofImage(int w, int h, int c) : this(new float[(long)w * h * c], c, w, h, w, 0, 0, 0, w, h) { }
    public DofImage(float[] d, int c, int w, int h) : this(d, c, w, h, w, 0, 0, 0, w, h) { }
    public DofImage(float[] d, int c, int w, int h, int stride, int off, int x0, int y0, int x1, int y1)
    { D = d; C = c; W = w; H = h; Stride = stride; Off = off; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }

    public int Idx(int x, int y) => Off + (y * Stride + x) * C;
    public bool IsPlain => Off == 0 && Stride == W && X0 == 0 && Y0 == 0 && X1 == W && Y1 == H && D.LongLength == (long)W * H * C;

    /// <summary>Lumen's view construction: intersect the rect fields with <paramref name="r"/> (window coordinates); the window becomes the
    /// intersection and the rect fields are shifted by its origin. Null when the intersection is empty (Lumen: an empty image).</summary>
    public DofImage? View(RectI r)
    {
        int ix0 = Math.Max(X0, r.X0), iy0 = Math.Max(Y0, r.Y0), ix1 = Math.Min(X1, r.X1), iy1 = Math.Min(Y1, r.Y1);
        if (ix1 <= ix0 || iy1 <= iy0) return null;
        return new DofImage(D, C, ix1 - ix0, iy1 - iy0, Stride, Off + (iy0 * Stride + ix0) * C, X0 - ix0, Y0 - iy0, X1 - ix0, Y1 - iy0);
    }

    /// <summary>The window's pixels as a fresh plain image (rect (0,0,W,H)).</summary>
    public DofImage Compact()
    {
        var o = new DofImage(W, H, C);
        for (int y = 0; y < H; y++) Array.Copy(D, Idx(0, y), o.D, y * W * C, W * C);
        return o;
    }

    /// <summary>The window's pixels as a row-major array (the array itself when the image is already plain).</summary>
    public float[] ToArray() => IsPlain ? D : Compact().D;
}

/// <summary>Lumen's <c>DualLayerBuffer</c>: visible color and occluded color (vec4), visible depth,
/// occluded depth and matte (float). Absent planes are null (Lumen: empty images).</summary>
public sealed class DualLayerBuffer
{
    public DofImage? Vis, Occ, VisDepth, OccDepth, Matte;

    /// <summary>Every plane becomes a view of its intersection with <paramref name="r"/>.</summary>
    public DualLayerBuffer Crop(RectI r) => new()
    { Vis = Vis?.View(r), Occ = Occ?.View(r), VisDepth = VisDepth?.View(r), OccDepth = OccDepth?.View(r), Matte = Matte?.View(r) };
}

/// <summary>Per-<c>DeviceType</c> parameter block; the member comments give the values of the two device types.</summary>
public sealed record DofDevice(
    int MaxRadius,          // 512 / 32
    float FrontScale,       // 0.5 / 0.25
    float NoiseAmp,         // 0.0067 / 0.0023
    float Unused0C,         // 2.0
    int DownscaleDiameter,  // 13 / 65
    float Feather,          // 0.1 / 0.2
    int BandHalf,           // 0
    int LinearLength,       // 1
    float Unused20,         // 3.0 (additive ladder step, unused)
    float Ratio)            // 2.0
{
    static readonly DofDevice D0 = new(512, 0.5f, 0.0067f, 2f, 13, 0.1f, 0, 1, 3f, 2f), D1 = new(32, 0.25f, 0.0023f, 2f, 65, 0.2f, 0, 1, 3f, 2f);
    public static DofDevice For(int deviceType) => deviceType switch { 0 => D0, 1 => D1, _ => throw new ArgumentException("Wrong device type") };
}
