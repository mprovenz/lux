using Avalonia;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>The fake depth effect: an f-number and focus distance over a depth map snapshot.</summary>
/// <param name="OffFNumber">The f-number at which nothing is blurred (the "Off" end).</param>
internal sealed record FakeEffect(double FNumber, double OffFNumber, double FocusMm, float[] Depth, FakeDepthMap Map)
{
    public bool IsActive => FNumber < OffFNumber - 1e-6;

    /// <summary>Blur radius in full-size picture pixels at a depth (Lumen's synthetic aperture: zero at the "off" f-number).</summary>
    public double RadiusPx(double depthMm)
    {
        double s = FocusMm / 1000, inv = double.IsFinite(depthMm) ? 1000 / depthMm : 0;
        return 1200 * Math.Abs(1 / s - inv) * Math.Max(0, 1 / FNumber - 1 / OffFNumber);
    }
}

/// <summary>Paints a fake tile with the depth effect: the sharp tile with a margin, a ladder of blurred copies, and per pixel the copy whose
/// radius matches the depth's blur (blended between neighbouring rungs).</summary>
internal static class FakeEffectRender
{
    static readonly double[] Rungs = { 0, 1, 2, 4, 8, 16, 24 };

    public static SKBitmap Paint(FakeEffect e, int index, bool stacked, bool telephoto, int orientation, PixelSize capture, SKMatrix captureToPicture,
                                 double fx, Rect lr, int w, int h, SKColorFilter? filter, int octaves, CancellationToken cancel, PixelSize levelSize = default)
    {
        double rmax = Math.Min(24, Math.Max(e.RadiusPx(e.Map.NearestMm), e.RadiusPx(double.PositiveInfinity)) / fx);
        int rungs = 1;
        while (rungs < Rungs.Length && Rungs[rungs - 1] < rmax) rungs++;
        int margin = (int)Math.Ceiling(Rungs[rungs - 1] * 1.5) + 2;
        var bigRect = new SKRect((float)((lr.X - margin) * fx), (float)((lr.Y - margin) * fx), (float)((lr.Right + margin) * fx), (float)((lr.Bottom + margin) * fx));
        int bw = w + 2 * margin, bh = h + 2 * margin;
        var big = FakeScene.PaintTile(index, stacked, telephoto, orientation, capture.Width, capture.Height, captureToPicture, (float)fx, bigRect, bw, bh, filter, octaves);
        if (levelSize.Width > 0) ClampEdges(big, margin - (int)lr.X, margin - (int)lr.Y, levelSize);
        var copies = new SKBitmap[rungs];
        copies[0] = big;
        try
        {
            for (int k = 1; k < rungs; k++)
            {
                cancel.ThrowIfCancellationRequested();
                copies[k] = Blur(big, (float)(Rungs[k] / 2));
            }
            var toMap = Affine2.Scale(fx, fx).Then(new Affine2(1, 0, lr.X * fx, 0, 1, lr.Y * fx)).Then(Affine2.FromSk(captureToPicture).Invert())
                .Then(Affine2.Scale(e.Map.W / (double)capture.Width, e.Map.H / (double)capture.Height));
            var outBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            var src = new byte[rungs][];
            for (int k = 0; k < rungs; k++) src[k] = copies[k].Bytes;
            int srcStride = copies[0].RowBytes, dstStride = outBmp.RowBytes;
            var outBytes = new byte[outBmp.RowBytes * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var (mx, my) = toMap.Map(x + 0.5, y + 0.5);
                    double d = FakeDepthMap.Sample(e.Depth, e.Map.W, e.Map.H, mx, my);
                    double r = Math.Min(Rungs[rungs - 1], e.RadiusPx(d) / fx);
                    int k = 0;
                    while (k + 1 < rungs && Rungs[k + 1] <= r) k++;
                    int k2 = Math.Min(k + 1, rungs - 1);
                    double t = k2 == k ? 0 : (r - Rungs[k]) / (Rungs[k2] - Rungs[k]);
                    int so = (y + margin) * srcStride + (x + margin) * 4, o = y * dstStride + x * 4;
                    var a = src[k]; var b = src[k2];
                    for (int c = 0; c < 4; c++) outBytes[o + c] = (byte)(a[so + c] + (b[so + c] - a[so + c]) * t);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(outBytes, 0, outBmp.GetPixels(), outBytes.Length);
            outBmp.NotifyPixelsChanged();
            return outBmp;
        }
        finally
        {
            foreach (var c in copies) c?.Dispose();
        }
    }

    /// <summary>The margin outside the picture repeats the picture's edge pixels (so the blur does not pull black in at the picture's edges).</summary>
    static void ClampEdges(SKBitmap big, int ox, int oy, PixelSize level)
    {
        int bw = big.Width, bh = big.Height, stride = big.RowBytes;
        int x0 = Math.Clamp(ox, 0, bw - 1), y0 = Math.Clamp(oy, 0, bh - 1);
        int x1 = Math.Clamp(ox + level.Width, x0 + 1, bw), y1 = Math.Clamp(oy + level.Height, y0 + 1, bh);
        if (x0 == 0 && y0 == 0 && x1 == bw && y1 == bh) return;
        var px = big.Bytes;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = 0; x < x0; x++) Buffer.BlockCopy(px, row + x0 * 4, px, row + x * 4, 4);
            for (int x = x1; x < bw; x++) Buffer.BlockCopy(px, row + (x1 - 1) * 4, px, row + x * 4, 4);
        }
        for (int y = 0; y < y0; y++) Buffer.BlockCopy(px, y0 * stride, px, y * stride, stride);
        for (int y = y1; y < bh; y++) Buffer.BlockCopy(px, (y1 - 1) * stride, px, y * stride, stride);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, big.GetPixels(), px.Length);
        big.NotifyPixelsChanged();
    }

    static SKBitmap Blur(SKBitmap src, float sigma)
    {
        var dst = new SKBitmap(src.Info);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
        canvas.DrawBitmap(src, 0, 0, paint);
        canvas.Flush();
        return dst;
    }
}
