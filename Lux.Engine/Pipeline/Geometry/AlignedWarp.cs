using System.Runtime.InteropServices;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Geometry;

/// <summary>`ReferenceImageCache::processLevel` (`1804d86e0`): the per-module "aligned" image at pyramid level L for an output ROI —
/// source rectangle from the level-0 map's border scan, the level-L module ISP on that rectangle, then the clamped resample.</summary>
public static class AlignedWarp
{
    /// <summary>`FUN_1804d9eb0` (border AABB `FUN_180304500` of the level-0 map over the ROI, then even-aligned ±2 expansion):
    /// x0 = evenTowardZero(floor(minx)) − 2, x1 = 2·ceil(maxx) + 2 − evenTowardZero(ceil(maxx)); same for y. Level-0 coordinates.</summary>
    public static RectI SourceRect(AlignedCalib calib, RectI roi0)
    {
        float minx = float.MaxValue, miny = float.MaxValue, maxx = -float.MaxValue, maxy = -float.MaxValue;
        void Acc((float X, float Y) p)
        {
            if (p.X <= minx) minx = p.X;
            if (p.Y <= miny) miny = p.Y;
            if (maxx <= p.X) maxx = p.X;
            if (maxy <= p.Y) maxy = p.Y;
        }
        for (int y = roi0.Y0; y < roi0.Y1; y++)
        {
            Acc(calib.Map((float)roi0.X0, (float)y, 0));
            Acc(calib.Map((float)(roi0.X1 - 1), (float)y, 0));
        }
        for (int x = roi0.X0; x < roi0.X1; x++)
        {
            Acc(calib.Map((float)x, (float)roi0.Y0, 0));
            Acc(calib.Map((float)x, (float)(roi0.Y1 - 1), 0));
        }
        static int EvenTowardZero(int v) => (v - (v >> 31)) & ~1;
        int fx = (int)MathF.Floor(minx), fy = (int)MathF.Floor(miny), cx = (int)MathF.Ceiling(maxx), cy = (int)MathF.Ceiling(maxy);
        return new RectI(EvenTowardZero(fx) - 2, EvenTowardZero(fy) - 2, (cx * 2 + 2) - EvenTowardZero(cx), (cy * 2 + 2) - EvenTowardZero(cy));
    }

    /// <summary>`PipelineCache::processLevel1` (`1804e2eb0`, spec ab6d047c §3) source rectangle: the level-0 map's border AABB over the ROI
    /// (`FUN_180304500`) + `DAT_1806ea330` = (−2,−2,+2,+2), then `x0 = floorToEven((int)bb.x0)`, `x1 = ceilToEven((int)bb.x1)` (same for y),
    /// clamped to [0, frame). Level-0 (= full camera resolution) coordinates.</summary>
    public static RectI SourceRectLevel1(AlignedCalib calib, RectI roi, int frameW, int frameH)
    {
        float minx = float.MaxValue, miny = float.MaxValue, maxx = -float.MaxValue, maxy = -float.MaxValue;
        void Acc((float X, float Y) p)
        {
            if (p.X <= minx) minx = p.X;
            if (p.Y <= miny) miny = p.Y;
            if (maxx <= p.X) maxx = p.X;
            if (maxy <= p.Y) maxy = p.Y;
        }
        for (int y = roi.Y0; y < roi.Y1; y++) { Acc(calib.Map((float)roi.X0, (float)y, 0)); Acc(calib.Map((float)(roi.X1 - 1), (float)y, 0)); }
        for (int x = roi.X0; x < roi.X1; x++) { Acc(calib.Map((float)x, (float)roi.Y0, 0)); Acc(calib.Map((float)x, (float)(roi.Y1 - 1), 0)); }
        int bx0 = (int)(minx + -2.0f), by0 = (int)(miny + -2.0f), bx1 = (int)(maxx + 2.0f), by1 = (int)(maxy + 2.0f);
        static int FloorToEven(int v) { int sgn = v >> 31; return (((v - sgn) >> 1) - (v & -sgn)) * 2; }   // ((−sign + v) >> 1 − (v & −sign))·2
        static int CeilToEven(int v) { int h = v / 2; if ((v & 1) != 0) h += (-(v >> 31)) ^ 1; return h * 2; }
        int x0 = Math.Max(FloorToEven(bx0), 0), y0 = Math.Max(FloorToEven(by0), 0);
        int x1 = Math.Min(CeilToEven(bx1), frameW), y1 = Math.Min(CeilToEven(by1), frameH);
        return new RectI(x0, y0, x1, y1);
    }

    /// <summary>`PipelineCache::processLevel1`: render the fusion cache over <see cref="SourceRectLevel1"/> and warp it with the inlined
    /// level-0 map (`ImageWarpClamped&lt;2&gt;`, fill (0,0,0,0)). <paramref name="render"/> = `FusionCacheBayer::render(rect)` (an image of `rect.size`).</summary>
    public static Image<Vec4F> ProcessLevel1(AlignedCalib calib, RectI roi, int frameW, int frameH, Func<RectI, Image<Vec4F>> render, Action<string>? log = null)
    {
        var src0 = SourceRectLevel1(calib, roi, frameW, frameH);
        if (src0.X0 >= src0.X1 || src0.Y0 >= src0.Y1) throw new InvalidOperationException("processLevel1 source rectangle is empty");
        log?.Invoke($"processLevel1: roi {roi} → source {src0}");
        var img = render(src0);
        int w = img.Width, h = img.Height;
        var src = new float[w * h * 4];
        for (int y = 0; y < h; y++) MemoryMarshal.AsBytes(img.Row(y)).CopyTo(MemoryMarshal.AsBytes(src.AsSpan(y * w * 4, w * 4)));
        var dst = new float[roi.Width * roi.Height * 4];
        WarpResample.Warp(calib, 0, new WarpResample.Source(src, w, 0, 0, w, h), src0.X0, src0.Y0, roi.X0, roi.Y0, dst, roi.Width, roi.Height, new float[4], null, inlinedMap: true);
        var outImg = new Image<Vec4F>(new RectI(0, 0, roi.Width, roi.Height));
        MemoryMarshal.AsBytes(dst.AsSpan()).CopyTo(MemoryMarshal.AsBytes(outImg.Data.AsSpan()));
        return outImg;
    }

    /// <summary>Warp `ispImage` (the level-L ISP result whose pixel (0,0) is level-L coordinate `srcOff`) onto the level-L output ROI.</summary>
    public static Image<Vec4F> WarpToRoi(AlignedCalib calib, int level, Image<Vec4F> ispImage, (int X, int Y) srcOff, RectI roiL, ReadOnlySpan<float> fill)
    {
        int w = ispImage.Width, h = ispImage.Height;
        var src = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            MemoryMarshal.AsBytes(ispImage.Row(y)).CopyTo(MemoryMarshal.AsBytes(src.AsSpan(y * w * 4, w * 4)));
        var dst = new float[roiL.Width * roiL.Height * 4];
        WarpResample.Warp(calib, level, new WarpResample.Source(src, w, 0, 0, w, h), srcOff.X, srcOff.Y, roiL.X0, roiL.Y0, dst, roiL.Width, roiL.Height, fill);
        var outImg = new Image<Vec4F>(new RectI(0, 0, roiL.Width, roiL.Height));
        MemoryMarshal.AsBytes(dst.AsSpan()).CopyTo(MemoryMarshal.AsBytes(outImg.Data.AsSpan()));
        return outImg;
    }

    /// <summary>
    /// The stacked-capture source of `processLevel` (`1804d86e0` L145): what `FUN_18020a6d0` returns for a module whose
    /// stack has more than one frame — the fused float Bayer frame — together with the uint8 gain map
    /// (`FUN_18020b870`) the STD plane comes from, and the ISP tile margin `ReferenceImageCache+0xbc`.
    /// </summary>
    public sealed record StackedSource(Image<float> Bayer, Func<RectI, Image<float>> Std, int Margin);

    /// <summary>Full `processLevel` for a raw capture: ROI (level-L coords) → level-0 → source rect (clamped to the frame) → level-L
    /// ISP → warp. Without a calibration the ISP image is returned as-is (Lumen: `+0xb8 == 0`).</summary>
    public static Image<Vec4F> ProcessLevel(SoftIsp isp, CapturedFrame frame, AlignedCalib? calib, int level, RectI roiL, ReadOnlySpan<float> fill, Action<string>? log = null, StackedSource? stacked = null)
    {
        if (level is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(level));
        int fc = 1 << level;
        var roi0 = new RectI(roiL.X0 << level, roiL.Y0 << level, (roiL.X0 << level) + (roiL.Width << level), (roiL.Y0 << level) + (roiL.Height << level));
        var src0 = calib is null ? roi0 : SourceRect(calib, roi0);
        src0 = new RectI(Math.Max(src0.X0, 0), Math.Max(src0.Y0, 0), Math.Min(src0.X1, frame.Width), Math.Min(src0.Y1, frame.Height));
        if (src0.X0 >= src0.X1 || src0.Y0 >= src0.Y1) throw new InvalidOperationException("aligned source rectangle is empty");
        log?.Invoke($"processLevel L{level}: roi0 {roi0} → source {src0}");
        Image<Vec4F> ispImg;
        (int X, int Y) srcOff;
        if (stacked is null)
        {
            ispImg = isp.ProcessBayer(frame, src0, level, log);
            srcOff = (src0.X0 / fc, src0.Y0 / fc);
        }
        else
        {
            var stats = isp.ComputeStats(frame);
            // L155–240: the `denoising.type == "none"` branch (cache level 0 only) runs the BayerFloat runner on the plain ROI
            // with an EMPTY std image; every other level takes the gain-map branch below.
            if (isp.Tuning.Type("denoising") == "none")
            {
                ispImg = isp.ProcessBayerFloat(frame, stats, stacked.Bayer.View(src0), null, src0, level, log);
                srcOff = (src0.X0 / fc, src0.Y0 / fc);
            }
            else
            {
                // L242–290: grow the ROI by the tile margin `+0xbc`, run on the grown rect with the gain map as the STD plane,
                // then cut the halo back off in LEVEL pixels (`local_e8 / (1 << level)`).
                int m = stacked.Margin >> 1, m2 = Math.Max(stacked.Margin - 1, 0) >> 1;
                int padL = Math.Min(src0.X0, m), padT = Math.Min(src0.Y0, m);
                int padR = Math.Min(frame.Width - src0.X1, m2), padB = Math.Min(frame.Height - src0.Y1, m2);
                var grown = new RectI(src0.X0 - padL, src0.Y0 - padT, src0.X1 + padR, src0.Y1 + padB);
                // `FUN_180012530` copies exactly the grown window, so the runner's available region IS the ROI — no stage can
                // grow past it. (Falsified alternatives, all far worse on 00551: ROI = the un-grown rect with the halo as growth
                // room, 99.698 %; an edge-replicated pad so growth reads clamped values, 17.5 %; a whole-frame ROI, 19.1 %.)
                var res = isp.ProcessBayerFloat(frame, stats, stacked.Bayer.View(grown), stacked.Std(grown), grown, level, log);
                // `local_168` is the runner's result in ITS own frame (0,0,w,h); the crop is [pad, pad + roi) / fc
                int cx0 = Math.Max(0, padL / fc), cy0 = Math.Max(0, padT / fc);
                int cx1 = Math.Min(res.Width, (padL - src0.X0 + src0.X1) / fc), cy1 = Math.Min(res.Height, (padT - src0.Y0 + src0.Y1) / fc);
                if (cx1 <= cx0 || cy1 <= cy0) throw new InvalidOperationException("processLevel: empty halo crop");
                ispImg = res.View(new RectI(res.Rect.X0 + cx0, res.Rect.Y0 + cy0, res.Rect.X0 + cx1, res.Rect.Y0 + cy1));
                srcOff = (ispImg.Rect.X0, ispImg.Rect.Y0);
                log?.Invoke($"processLevel L{level}: stacked grown {grown} (margin {stacked.Margin}) → crop ({cx0},{cy0})-({cx1},{cy1}) at {srcOff}");
            }
        }
        if (calib is null) return ispImg;
        return WarpToRoi(calib, level, ispImg, srcOff, roiL, fill);
    }
}
