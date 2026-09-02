namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>`lt::WarpField` (spec §1.4, 0x50 B): column-major 4×4 `M` (M[4c+r]), the 4160×3120 metric depth image and the sx/sy scale
/// applied to the wide-reference grid coordinates before the depth lookup (1.0 on the L16 after initResAmp step 8).</summary>
public sealed class WarpField
{
    public float[] M = new float[16];
    public float[] Depth = Array.Empty<float>();
    public int DepthW, DepthH, DepthStride;
    public float Sx = 1f, Sy = 1f;
}

/// <summary>A tele module as seen by `ImageResolutionAmp`: the generator (`initResAmp::lambda_3` = tile cache → gain → √) and its WarpField.</summary>
public sealed class ResAmpModule
{
    public ImageGenerator Gen;
    public WarpField Warp;
    public ResAmpModule(ImageGenerator gen, WarpField warp) { Gen = gen; Warp = warp; }
}

/// <summary>`lt::Image&lt;uint8_t&gt;` header (same conventions as <see cref="ResImage"/>).</summary>
public sealed class U8Image
{
    public byte[] Data;
    public int Off, W, H, Stride, RX0, RY0, RX1, RY1;
    public U8Image(int w, int h) { Data = new byte[w * h]; W = w; H = h; Stride = w; RX1 = w; RY1 = h; }
    public U8Image(byte[] data, int off, int w, int h, int stride, int rx0, int ry0, int rx1, int ry1)
    { Data = data; Off = off; W = w; H = h; Stride = stride; RX0 = rx0; RY0 = ry0; RX1 = rx1; RY1 = ry1; }
    public int Idx(int x, int y) => Off + y * Stride + x;
    public U8Image Crop(int a, int b, int c, int d)
    {
        int x0 = Math.Max(RX0, a), y0 = Math.Max(RY0, b), x1 = Math.Min(RX1, c), y1 = Math.Min(RY1, d);
        if (x1 <= x0 || y1 <= y0) throw new InvalidOperationException("empty crop");
        return new U8Image(Data, Off + Stride * y0 + x0, x1 - x0, y1 - y0, Stride, RX0 - x0, RY0 - y0, RX1 - x0, RY1 - y0);
    }
}

/// <summary>The per-module record pushed by §4.10 (0x280 B `Rec`).</summary>
public sealed class ModuleRecord
{
    public int MinX, MinY, MaxX, MaxY;               // +0x00..0x0c inclusive bbox of the projected grid points (module px)
    public int[] Grid = Array.Empty<int>();           // +0x10 Image<Vec2i> gw×gh (x,y pairs), 0x80000000 = invalid; updated by §5
    public int Gw, Gh;
    public float[] Res = Array.Empty<float>();        // +0x40 Image<Vec3f> (X = fx·scale, Y = fy·scale, conf)
    public ResImage Hp = null!;                       // +0x70 high-pass module image (bw×bh @ (minx,miny), (hh+Nm+4) halo)
    public ResImage Blur = null!;                     // +0xa0 Lanczos-blurred matrixed render (halo hh+Nm)
    public U8Image[] Ph = new U8Image[9];             // +0xd0 + 0x30·k, k = px + 3·py, 1/3-px phase maps (16-px halo)
    public int ModuleIndex;                           // loop index m (diagnostics)
}

/// <summary>Optional per-tile intermediate capture for comparison against the cp.dll reference.</summary>
public sealed class ResAmpTrace
{
    public Action<string, object>? OnImage;   // tag → ResImage / U8Image / byte[] / float[] ...
    public void Emit(string tag, object img) => OnImage?.Invoke(tag, img);
}
