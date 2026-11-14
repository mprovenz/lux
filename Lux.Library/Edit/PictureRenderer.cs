using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Session;

namespace Lux.Library.Edit;

/// <summary>
/// The picture of a capture as the JPG export of a level makes it, rendered by region: Lumen's export size rule and `GetExportTransformOutput`
/// for the whole picture (as `exportImage` computes it once for the JPG), the display tiles of the source level through the session's stage
/// cache (<see cref="CaptureSession.Render"/>), and the export's own 8-bit resampler evaluated only for the requested output pixels
/// (<see cref="JpegExportRenderer.WarpClamped2U8Window"/>). So a region here is, byte for byte, that region of the export's pre-encode RGBA8.
/// <para>Two shortcuts, both exactly equal to the general path: an "exact shift" plan (matrix part 1/0/0/1 and integral offsets — every
/// identity transform at a level size) copies the source pixels (phase-0 Catmull-Rom has weights 0, 1, 0, 0); and the zero / clamp rule at the
/// source border is reproduced. The Lanczos branch (an output more than 1.5× smaller than its source level, never met at level sizes) renders the
/// whole picture once per settings and cuts regions from it.</para>
/// </summary>
internal sealed class PictureRenderer
{
    readonly CaptureSession _s;
    public ExportLevels Window { get; }

    public PictureRenderer(CaptureSession s) { _s = s; Window = s.Window; }

    /// <summary>One level of one transform.</summary>
    internal sealed record Plan(int Level, (int W, int H) Size, TransformOutput To, bool ExactShift, int ShiftX, int ShiftY, bool Lanczos, ExportTransform Transform);

    /// <summary>The picture size of a level under a transform — `Exporter.Resolve`'s rule (Lumen's rule: the level window × the crop size,
    /// each `(int)(float · float)`).</summary>
    public (int W, int H) SizeOf(ExportTransform t, int level)
    {
        var dims = Window.ExportDims[level];
        var id0 = ExportTransform.Identity(Window.ExportDims[0]);
        if ((t.AspectW, t.AspectH) != (id0.AspectW, id0.AspectH)) throw new InvalidOperationException($"the photo transform has the aspect ratio {t.AspectW}:{t.AspectH}, this capture {id0.AspectW}:{id0.AspectH}");
        float cw = t.CropX1 - t.CropX0, ch = t.CropY1 - t.CropY0;
        return ((int)((float)dims.W * cw), (int)((float)dims.H * ch));
    }

    public Plan PlanFor(ExportTransform t, int level)
    {
        var size = SizeOf(t, level);
        if (size.W < 1 || size.H < 1) throw new InvalidOperationException("the crop leaves no picture");
        var to = ExportTransformOutput.Compute(t, size, new RectI(0, 0, size.W, size.H), Window.ExportDims, false);
        bool lanczos = 1.5f < to.ScaleX || 1.5f < to.ScaleY;
        bool shift = !lanczos && to.A == 1f && to.B == 0f && to.D == 0f && to.E == 1f && to.C == MathF.Floor(to.C) && to.F == MathF.Floor(to.F)
                     && MathF.Abs(to.C) < 1 << 20 && MathF.Abs(to.F) < 1 << 20;
        return new Plan(level, size, to, shift, shift ? (int)to.C : 0, shift ? (int)to.F : 0, lanczos, t);
    }

    /// <summary>The RGBA8 pixels of <paramref name="r"/> (picture pixels of the plan's level) with the given render settings.</summary>
    public byte[] Render(Plan p, RectI r, RenderSettings rs, CancellationToken cancel)
        => Render(p, r, (lv, rect) => _s.Render(lv, rect, rs, cancel).Rgba8!, rs, cancel);

    /// <summary>The same resampling of another RGBA8 source of export-level pixels (the depth view): <paramref name="source"/>(level, rect) →
    /// rect.Width × rect.Height × 4 bytes; <paramref name="key"/> identifies its content for the whole-picture cache of the Lanczos branch.</summary>
    public byte[] Render(Plan p, RectI r, Func<int, RectI, byte[]> source, object key, CancellationToken cancel)
    {
        var to = p.To; int sw = to.Source.Width, sh = to.Source.Height;
        if (p.Lanczos) return Cut(WholeLanczos(p, source, key, cancel), p.Size.W, r);
        if (p.ExactShift)
        {
            // u = x + C exactly; Catmull-Rom at phase 0 returns the second tap of the clamped 4×4 block: the source pixel clamp(u), for
            // ix = u − 1 ∈ [−3, sw − 1] (else zero), the same on y.
            int ux0 = r.X0 + p.ShiftX, ux1 = r.X1 + p.ShiftX, vy0 = r.Y0 + p.ShiftY, vy1 = r.Y1 + p.ShiftY;
            int sx0 = Math.Clamp(ux0, 0, sw - 1), sx1 = Math.Clamp(ux1 - 1, 0, sw - 1) + 1, sy0 = Math.Clamp(vy0, 0, sh - 1), sy1 = Math.Clamp(vy1 - 1, 0, sh - 1) + 1;
            var dst = new byte[(long)r.Width * r.Height * 4];
            bool inside = ux0 >= 0 && ux1 <= sw && vy0 >= 0 && vy1 <= sh;
            var src = source(to.Level, new RectI(to.Source.X0 + sx0, to.Source.Y0 + sy0, to.Source.X0 + sx1, to.Source.Y0 + sy1));
            int ww = sx1 - sx0;
            if (inside && ww == r.Width) return src;
            for (int yy = 0; yy < r.Height; yy++)
            {
                int v = vy0 + yy;
                if (v < -2 || v > sh) continue;   // iy = v − 1 outside [−3, sh − 1]: zero
                int cy = Math.Clamp(v, 0, sh - 1) - sy0;
                for (int xx = 0; xx < r.Width; xx++)
                {
                    int u = ux0 + xx;
                    if (u < -2 || u > sw) continue;
                    int cx = Math.Clamp(u, 0, sw - 1) - sx0;
                    Buffer.BlockCopy(src, (cy * ww + cx) * 4, dst, (yy * r.Width + xx) * 4, 4);
                }
            }
            return dst;
        }
        // the general path: the source window under the output rect's corners (the map is affine, the tap index monotone in it), one tap of margin
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var (x, y) in new[] { (r.X0, r.Y0), (r.X1 - 1, r.Y0), (r.X0, r.Y1 - 1), (r.X1 - 1, r.Y1 - 1) })
        {
            var (u, v) = to.Map((float)x, (float)y);
            int ix = (int)((u + -1f) * 64f) >> 6, iy = (int)((v + -1f) * 64f) >> 6;
            minX = Math.Min(minX, ix); maxX = Math.Max(maxX, ix); minY = Math.Min(minY, iy); maxY = Math.Max(maxY, iy);
        }
        int wx0 = Math.Clamp(minX - 1, 0, sw), wx1 = Math.Clamp(maxX + 5, 0, sw), wy0 = Math.Clamp(minY - 1, 0, sh), wy1 = Math.Clamp(maxY + 5, 0, sh);
        if (wx0 >= wx1 || wy0 >= wy1) return new byte[(long)r.Width * r.Height * 4];
        var win = new RectI(wx0, wy0, wx1, wy1);
        var winSrc = source(to.Level, new RectI(to.Source.X0 + wx0, to.Source.Y0 + wy0, to.Source.X0 + wx1, to.Source.Y0 + wy1));
        return JpegExportRenderer.WarpClamped2U8Window(winSrc, win, sw, sh, r, to);
    }

    static byte[] Cut(byte[] whole, int w, RectI r)
    {
        var dst = new byte[(long)r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++) Buffer.BlockCopy(whole, ((r.Y0 + y) * w + r.X0) * 4, dst, y * r.Width * 4, r.Width * 4);
        return dst;
    }

    // ---- the Lanczos branch (Lumen's path for vec4x8ui with a scale above 1.5): the whole picture, kept for the last settings
    readonly object _lzLock = new();
    (Plan P, object S, byte[] Px)? _lz;

    byte[] WholeLanczos(Plan p, Func<int, RectI, byte[]> source, object key, CancellationToken cancel)
    {
        lock (_lzLock) if (_lz is { } h && h.P == p && Equals(h.S, key)) return h.Px;
        var to = p.To; int sw = to.Source.Width, sh = to.Source.Height;
        var src = source(to.Level, to.Source);
        int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);
        var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
        var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
        var f = new float[src.Length]; for (int i = 0; i < src.Length; i++) f[i] = src[i];
        var conv = ExportResample.ConvSeparable(f, sw, sh, kernelX, kernelY);
        var blurred = new byte[conv.Length]; for (int i = 0; i < conv.Length; i++) blurred[i] = DisplayOutput.DisplayByte(conv[i]);
        var px = JpegExportRenderer.WarpBilinearU8(blurred, sw, sh, p.Size.W, p.Size.H, to);
        lock (_lzLock) _lz = (p, key, px);
        return px;
    }

    // ---- display rotation (display only: a pixel permutation)

    /// <summary>The displayed size of a picture under a display rotation.</summary>
    public static (int W, int H) Rotated((int W, int H) s, int rot) => rot is 90 or 270 ? (s.H, s.W) : s;

    /// <summary>The picture rect a displayed rect shows (display rotation <paramref name="rot"/> degrees clockwise, picture size <paramref name="pic"/>).</summary>
    public static RectI PictureRectOf(RectI d, int rot, (int W, int H) pic) => rot switch
    {
        90 => new RectI(d.Y0, pic.H - d.X1, d.Y1, pic.H - d.X0),
        180 => new RectI(pic.W - d.X1, pic.H - d.Y1, pic.W - d.X0, pic.H - d.Y0),
        270 => new RectI(pic.W - d.Y1, d.X0, pic.W - d.Y0, d.X1),
        _ => d,
    };

    /// <summary>Turn the pixels of a picture rect (w × h) by <paramref name="rot"/> degrees clockwise.</summary>
    public static byte[] Turn(byte[] px, int w, int h, int rot)
    {
        if (rot == 0) return px;
        var o = new byte[px.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx, dy, dw;
                switch (rot)
                {
                    case 90: dx = h - 1 - y; dy = x; dw = h; break;
                    case 180: dx = w - 1 - x; dy = h - 1 - y; dw = w; break;
                    default: dx = y; dy = w - 1 - x; dw = h; break;   // 270
                }
                Buffer.BlockCopy(px, (y * w + x) * 4, o, (dy * dw + dx) * 4, 4);
            }
        return o;
    }

    /// <summary>RGBA → BGRA in place.</summary>
    public static void SwapRb(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
    }
}
