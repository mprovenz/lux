namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>Mirror of cp.dll's <c>lt::Image&lt;T&gt;</c> header as used inside <c>ImageResolutionAmp</c> (spec `a-resamp.md` §0):
/// <c>Off</c> = element index of view pixel (0,0) (the "data pointer"), <c>W/H</c> = view size, <c>Stride</c> in pixels, and
/// <c>RX0..RY1</c> = the whole allocated valid area relative to the view origin (RX0 ≤ 0, RX1 ≥ W after a crop). Kernels that clamp
/// against the rect (conv, resamplers) read real data outside the view. <c>Elems</c> = floats per pixel (4 for vec4, 1 for float).</summary>
public sealed class ResImage
{
    public float[] Data;
    public int Off;
    public int W, H, Stride, Elems;
    public int RX0, RY0, RX1, RY1;

    public ResImage(int w, int h, int elems = 4)
    {
        W = w; H = h; Stride = w; Elems = elems; Data = new float[(long)w * h * elems]; Off = 0;
        RX0 = 0; RY0 = 0; RX1 = w; RY1 = h;
    }
    public ResImage(float[] data, int off, int w, int h, int stride, int elems, int rx0, int ry0, int rx1, int ry1)
    { Data = data; Off = off; W = w; H = h; Stride = stride; Elems = elems; RX0 = rx0; RY0 = ry0; RX1 = rx1; RY1 = ry1; }

    /// <summary>Element index of pixel (x, y) (view coordinates; may be negative inside the rect).</summary>
    public int Idx(int x, int y) => Off + (y * Stride + x) * Elems;

    /// <summary>§0 crop rule: view := rect ∩ [a,b,c,d]; data += (stride·y0 + x0); rect −= (x0, y0). Returns a new header sharing the buffer.</summary>
    public ResImage Crop(int a, int b, int c, int d)
    {
        int x0 = Math.Max(RX0, a), y0 = Math.Max(RY0, b), x1 = Math.Min(RX1, c), y1 = Math.Min(RY1, d);
        if (x1 <= x0 || y1 <= y0) throw new InvalidOperationException("empty crop");
        return new ResImage(Data, Off + (Stride * y0 + x0) * Elems, x1 - x0, y1 - y0, Stride, Elems, RX0 - x0, RY0 - y0, RX1 - x0, RY1 - y0);
    }
}
