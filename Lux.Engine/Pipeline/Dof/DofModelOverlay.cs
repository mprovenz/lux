using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// The overlays the renderer's display tile generator draws (selected by the rendering mode). In every mode the tile is
/// first rendered as in mode 0 — fused tile from the PipelineCache or the DOFCache, the display ISP,
/// the crop of the grown rect — and the overlay is blended into that FLOAT RGBA tile: after the display ISP (tone, gamma, sharpening) and
/// before the 8-bit display store. An overlay applied to the stored bytes instead would not reproduce Lumen's pixels;
/// use <see cref="ToRgba8"/> after the overlay.
/// </summary>
public static class DofOverlay
{
    /// <summary>
    /// Mode 1, RefocusPoint (needs depth state ≥ 1, else "Refocus mode requested before depth available!"): every
    /// pixel whose depth is outside `[near, far]` is blended toward the overlay color. `colour` = ParamFloatArray 5
    /// (Lumen's GUI sets {0.2, 0.2, 0.2, 0.91}); its 4th component is the blend weight and is replaced by 1.0 in the color itself.
    /// `[near, far]` = <see cref="DofModel.DefaultInFocusRange"/> with ParamFloat 19. `depth` = the depth-cache fetch
    /// of the tile rect shifted by the level origin against the pipeline dims, same size as the tile.
    /// <c>a = depth &lt; near ? w : (far &lt; depth ? w : 0);  px = a·(r, g, b, 1) + (1 − a)·px</c> on all four lanes.
    /// </summary>
    public static void RefocusPoint(float[] rgba, int width, int height, int stride, ReadOnlySpan<float> depth, int depthStride, float near, float far, ReadOnlySpan<float> colour)
    {
        float w = colour[3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float d = depth[y * depthStride + x];
                float a = d < near ? w : far < d ? w : 0.0f;
                float b = 1.0f - a; int o = (y * stride + x) * 4;
                rgba[o] = a * colour[0] + b * rgba[o]; rgba[o + 1] = a * colour[1] + b * rgba[o + 1];
                rgba[o + 2] = a * colour[2] + b * rgba[o + 2]; rgba[o + 3] = a * 1.0f + b * rgba[o + 3];
            }
    }

    /// <summary>The fast `exp2` of the RefocusSlider weight: the argument clamped `minss(x, 128)`, `maxss(x, −126)`;
    /// `i = cvttss2si(x) + (x &lt; 0 ? −1 : 0)` by the sign BIT (so −0.0 and negative integers also step down); `fr = x − (float)i`;
    /// `p = ((fr·0.0780245215 + 0.226067156)·fr + 0.695833564)·fr + 0.999925196`; the result is `p` with `i` added to
    /// its exponent field (integer add of `i &lt;&lt; 23` to the float's bits).</summary>
    public static float FastExp2(float x)
    {
        x = x < 128.0f ? x : 128.0f;                                             // MINSS order: NaN gives 128
        x = x > -126.0f ? x : -126.0f;                                           // MAXSS order: NaN gives −126
        int i = DofModel.Cvtt(x) + (BitConverter.SingleToInt32Bits(x) >> 31);
        float fr = x - (float)i;
        float p = ((fr * BitConverter.UInt32BitsToSingle(0x3d9fcb52) + BitConverter.UInt32BitsToSingle(0x3e677e26)) * fr + BitConverter.UInt32BitsToSingle(0x3f322226)) * fr
                  + BitConverter.UInt32BitsToSingle(0x3f7ffb19);
        return BitConverter.Int32BitsToSingle(unchecked((i << 23) + BitConverter.SingleToInt32Bits(p)));
    }

    /// <summary>The `vec4x32f → float` pixel conversion (luma) of the slider overlay:
    /// `b·0.114 + (g·0.587 + r·0.299)`.</summary>
    public static float Luma(float r, float g, float b) => b * 0.114f + (g * 0.587f + r * 0.299f);

    /// <summary>
    /// Mode 2, RefocusSlider (compiled in, never selected by Lumen's GUI): the picture turns gray away from the
    /// focus distance and cyan near it. The base tile is always the PipelineCache's (no depth effect). `focus` = the DOFCache's current
    /// focus depth, `depth` as in mode 1 (sizes must match, else "Depth/image size mismatch in RefocusSlider mode").
    /// <c>k = 1 / ((focus·0.075)·(focus·0.075));  W = (1 − FastExp2(−(((d − focus)·(d − focus))·k)))·0.4;  G = Luma(px);
    /// px = W·(0, 0.75, 1, 1) + ((1 − W)·G)</c> on all four lanes, then alpha = 1.
    /// Note that <c>W</c> is 0 AT the focus distance and 0.4 far from it: the gray image is tinted where the scene is away from focus.
    /// </summary>
    public static void RefocusSlider(float[] rgba, int width, int height, int stride, ReadOnlySpan<float> depth, int depthStride, float focus)
    {
        float a = focus * 0.075f; a = a * a; float k = 1.0f / a;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float t = depth[y * depthStride + x] - focus; t = t * t; t = t * k;
                float w = (1.0f - FastExp2(-t)) * 0.4f;
                int o = (y * stride + x) * 4;
                float g = (1.0f - w) * Luma(rgba[o], rgba[o + 1], rgba[o + 2]);
                rgba[o] = w * 0.0f + g; rgba[o + 1] = w * 0.75f + g; rgba[o + 2] = w * 1.0f + g; rgba[o + 3] = 1.0f;
            }
    }

    /// <summary>
    /// Mode 4, DepthEditorSelection (needs a depth editor, else "DepthEditorSelection mode
    /// requested when DepthEditor does not exist!"): the quick-select mask (an 8-bit `CIAPI::Image`; nothing is drawn
    /// when it is null) is sampled nearest and blended with ParamFloatArray 4, 4th component = weight per mask unit
    /// (no division by 255):
    /// <c>mx = (int)((float)(tileX0 + x) · ((float)maskW / (float)exportW)); my likewise; a = (float)mask[my·stride + mx] · w;
    /// px = a·(r, g, b, 1) + (1 − a)·px</c>, with (tileX0, tileY0) the tile's origin in the export window of its level and
    /// (exportW, exportH) that level's export dims.
    /// </summary>
    public static void QuickSelect(float[] rgba, int width, int height, int stride, int tileX0, int tileY0, ReadOnlySpan<byte> mask, int maskW, int maskH, int maskStride,
        (int W, int H) exportDims, ReadOnlySpan<float> colour)
    {
        float sx = (float)maskW / (float)exportDims.W, sy = (float)maskH / (float)exportDims.H, w = colour[3];
        for (int y = 0; y < height; y++)
        {
            int my = DofModel.Cvtt((float)(tileY0 + y) * sy);
            for (int x = 0; x < width; x++)
            {
                int mx = DofModel.Cvtt((float)(tileX0 + x) * sx);
                float a = (float)mask[my * maskStride + mx] * w, b = 1.0f - a; int o = (y * stride + x) * 4;
                rgba[o] = a * colour[0] + b * rgba[o]; rgba[o + 1] = a * colour[1] + b * rgba[o + 1];
                rgba[o + 2] = a * colour[2] + b * rgba[o + 2]; rgba[o + 3] = a * 1.0f + b * rgba[o + 3];
            }
        }
    }

    /// <summary>The display store (dst, src, order): `clamp(roundHalfEven(255·v), 0, 255)` per lane, RGBA (order 0) or
    /// BGRA (order 1).</summary>
    public static byte[] ToRgba8(float[] rgba, int width, int height, int stride, int order = 0)
    {
        if (order >= 2) throw new InvalidOperationException("Unsupported channel order!");
        var dst = new byte[width * height * 4]; int k = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * stride + x) * 4; float a = rgba[o] * 255f, b = rgba[o + 1] * 255f, c = rgba[o + 2] * 255f, d = rgba[o + 3] * 255f;
                if (order == 1) (a, c) = (c, a);
                dst[k++] = DisplayOutput.DisplayByte(a); dst[k++] = DisplayOutput.DisplayByte(b); dst[k++] = DisplayOutput.DisplayByte(c); dst[k++] = DisplayOutput.DisplayByte(d);
            }
        return dst;
    }
}

/// <summary>What the display tile lambda reads besides the capture: the rendering mode and the settings of the overlays.</summary>
public sealed class DofDisplaySettings
{
    /// <summary>The rendering mode: 0 normal, 1 RefocusPoint, 2 RefocusSlider, 4 DepthEditorSelection (3 = debug view, not ported).</summary>
    public int Mode;
    /// <summary>ParamFloat 19, MaximumInFocusBlurPixels (mode 1).</summary>
    public float MaxInFocusBlurPixels = DofModel.DefaultMaxInFocusBlurPixels;
    /// <summary>The DOFCache's current focus depth, mm (mode 2).</summary>
    public float FocusDepth;
    /// <summary>ParamFloatArray 5 (mode 1) and 4 (mode 4), four floats each.</summary>
    public float[] RefocusColour = { 0.2f, 0.2f, 0.2f, 0.91f }, SelectionColour = { 0f, 0f, 0f, 0f };
    /// <summary>The quick-select mask (mode 4); null = nothing drawn.</summary>
    public byte[]? Mask; public int MaskW, MaskH, MaskStride;
}

/// <summary>The display tile generator for modes 0, 1, 2 and 4, on the ported display path.</summary>
public static class DofDisplay
{
    /// <summary>
    /// One display tile as float RGBA (`width·height·4`, row-major): <paramref name="tile"/> in pixels of the level's export window.
    /// <paramref name="depthEffect"/> = the DOFCache when the f-number setting is below the default (modes 0, 1, 4 take the fused tile
    /// from it; mode 2 never does), else null. <paramref name="depth"/> is needed by modes 1 and 2.
    /// </summary>
    public static float[] RenderTile(LriFile lri, PipelineCache colour, DofTileCache? depthEffect, DepthImageCache? depth, ExportLevels lv, int level, RectI tile,
        SoftIsp isp, IspStats stats, CapturedFrame frame, DofDisplaySettings s)
    {
        if (s.Mode is < 0 or > 4 or 3) throw new NotSupportedException("Unexpected rendering mode!");
        if (s.Mode is 1 or 2 && depth is null) throw new InvalidOperationException("Refocus mode requested before depth available!");
        var pd = lv.PipelineDims[level];
        var geo = DisplayRender.Geometry(tile, pd, lv.Origins[level], lv.CacheDims, level);
        var px = depthEffect is not null && s.Mode != 2 ? depthEffect.Render(lv.BaseLevel + geo.CacheLevel, geo.Grown, pd)
                                                        : colour.Render(lv.BaseLevel + geo.CacheLevel, geo.Grown, pd);
        var grown = new Image<Vec4F>(geo.Grown.Width, geo.Grown.Height);
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(grown.Data);
        var img = DisplayRender.Run(isp, frame, stats, grown, geo, level);
        int w = img.Width, h = img.Height; var rgba = new float[w * h * 4];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, float>(img.Row(y)).CopyTo(rgba.AsSpan(y * w * 4, w * 4));
        if (s.Mode is 1 or 2)
        {
            var o = lv.Origins[level];
            var d = DofGeometry.FetchDepth(depth!.Current, new RectI(tile.X0 + o.X, tile.Y0 + o.Y, tile.X1 + o.X, tile.Y1 + o.Y), pd);
            if (s.Mode == 2 && (d.W != w || d.H != h)) throw new InvalidOperationException("Depth/image size mismatch in RefocusSlider mode");
            var dd = d.ToDense();
            if (s.Mode == 1)
            {
                var (near, far) = InFocusRange(lri, depth.Current, s.MaxInFocusBlurPixels);
                DofOverlay.RefocusPoint(rgba, w, h, w, dd, d.W, near, far, s.RefocusColour);
            }
            else DofOverlay.RefocusSlider(rgba, w, h, w, dd, d.W, s.FocusDepth);
        }
        else if (s.Mode == 4 && s.Mask is not null)
            DofOverlay.QuickSelect(rgba, w, h, w, tile.X0, tile.Y0, s.Mask, s.MaskW, s.MaskH, s.MaskStride, lv.ExportDims[level], s.SelectionColour);
        return rgba;
    }

    /// <summary>ParamFloatArray 0 — the depth range the RefocusPoint overlay leaves unshaded.</summary>
    public static (float Near, float Far) InFocusRange(LriFile lri, DepthImage depth, float maxInFocusBlurPixels)
        => DofModel.DefaultInFocusRange(DofConstants.FromCapture(lri), DofModel.FocusFromAf(lri, 0), DofModel.FocusFromAf(lri, 1),
                                        () => { var p = DofModel.AfPoint(lri); return DofModel.DepthAtPoint(depth, p.X, p.Y); }, maxInFocusBlurPixels);
}
