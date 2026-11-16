using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using System.Runtime.Intrinsics;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// Lumen's `lt::DOFConstants`, filled from the capture's REFERENCE camera:
/// <list type="bullet">
/// <item>real focal length, mm: C 19.77, B 9.19, A by the reference's sensor (AR835 4.56, IMX386 3.95, AR1335 3.68).</item>
/// <item>35 mm-equivalent focal length: C 150, B 70, A 35 (AR835) or 28.</item>
/// <item>native f-number by group: {2.0, 2.0, 2.4}.</item>
/// <item>pixel pitch, mm: `(float)(size · s)` in DOUBLE, size by the reference's sensor type
/// (AR1335 0.0011, IMX386 0.0012, AR835 0.0014; anything else "Unexpected sensor type!"),
/// `s = (double)(1.0f / x)` (a float division, widened) when the reference image's `data_scale.x` is &gt; 0, else 1.0.</item>
/// <item>reference dims — the module frame size (4160×3120).</item>
/// </list>
/// </summary>
public readonly record struct DofConstants(float FocalLength, float FocalLength35, float NativeFNumber, float PixelPitch, int RefWidth, int RefHeight)
{
    /// <summary>`(N0 · f35) / f` — the f-number at which no synthetic blur is added (ParamFloat 16, and the initial ParamFloat 0
    /// and initial DOFCache f-number). 2·28/3.68 = 15.217391 for an AR1335 A reference.</summary>
    public float DefaultFNumber => (NativeFNumber * FocalLength35) / FocalLength;

    /// <summary>The constants of a loaded capture.</summary>
    public static DofConstants FromCapture(LriFile lri)
    {
        var h = lri.Header; int refId = (int)h.ImageReferenceCamera;
        var refModule = lri.Modules[lri.ReferenceModule].Module;
        return new DofConstants(ExportWindow.Focal(h, refId, false), ExportWindow.Focal(h, refId, true), ExportWindow.Aperture(refId),
            SensorPixelPitch(DofModel.SensorOf(h, refId), ModuleFrameInfo.From(lri, lri.ReferenceModule).DataScaleX),
            refModule.SensorDataSurface.Size.X, refModule.SensorDataSurface.Size.Y);
    }

    /// <summary>Double product of the sensor's pixel size and the inverse data scale, rounded to float once.</summary>
    public static float SensorPixelPitch(SensorType sensor, float dataScaleX)
    {
        double s = 1.0;
        if (0.0f < dataScaleX) s = (double)(1.0f / dataScaleX);
        double size = sensor switch
        {
            SensorType.SensorImx386 => BitConverter.Int64BitsToDouble(0x3f53a92a30553261),   // 0.0012
            SensorType.SensorAr1335 => BitConverter.Int64BitsToDouble(0x3f5205bc01a36e2f),   // 0.0011
            SensorType.SensorAr835 => BitConverter.Int64BitsToDouble(0x3f56f0068db8bac7),    // 0.0014
            _ => throw new NotSupportedException("Unexpected sensor type!"),
        };
        return (float)(size * s);
    }
}

/// <summary>The focus / aperture state of `lt::DOFCache`: the constants, the current f-number (initialized to
/// <see cref="DofConstants.DefaultFNumber"/> by the constructor) and focus depth in mm (not initialized by the constructor).</summary>
public sealed class DofState
{
    public DofConstants Constants { get; }
    public float FNumber { get; private set; }
    public float FocusDepth { get; private set; }

    public DofState(DofConstants constants) { Constants = constants; FNumber = constants.DefaultFNumber; }

    /// <summary>Set (fnum, focus, invalidate): each must be &gt; 0 (zero, negative and NaN raise). Lumen's
    /// caller only gets here when the settings pair (ParamFloat 0, ParamFloat 1) differs from the cached pair
    /// AND the focus depth is &gt; 0, so a non-positive focus setting is silently ignored there and never raises; `invalidate`
    /// (= rendering mode != 2) drops every cached tile — the caller owns that.</summary>
    public void Set(float fnum, float focus)
    {
        if (!(fnum > 0.0f)) throw new ArgumentException("Invalid fnumber requested!");
        if (!(0.0f < focus)) throw new ArgumentException("Invalid focus depth requested!");
        FNumber = fnum; FocusDepth = focus;
    }

    /// <summary>The renderer's choice in mode 0: the DOFCache is used only when the requested f-number is below the default one
    /// (`DefaultFNumber &lt;= req` → PipelineCache).</summary>
    public bool UsesDofCache(float requestedFNumber) => !(Constants.DefaultFNumber <= requestedFNumber);
}

/// <summary>The focus-depth sources, the in-focus range and the small renderer properties around the depth effect.</summary>
public static class DofModel
{
    /// <summary>ParamFloat 14: the constant 4.0 — the f-number of the in-focus range model.</summary>
    public const float RangeFNumber = 4.0f;
    /// <summary>ParamFloat 19 default, MaximumInFocusBlurPixels.</summary>
    public const float DefaultMaxInFocusBlurPixels = 1.0f;

    /// <summary>The sensor type of a camera (hw_info).</summary>
    public static SensorType SensorOf(LightHeader h, int camId)
    {
        if (h.HwInfo is not null) foreach (var c in h.HwInfo.Camera) if ((int)c.Id == camId) return c.Sensor;
        return SensorType.SensorUnknown;
    }

    /// <summary>The DeviceType of the blur parameter block — 1 only when EVERY captured image belongs to camera
    /// group 0 (an all-A capture), else 0. An L16 capture with B or C modules gives 0.</summary>
    public static int DeviceType(LriFile lri)
    {
        foreach (var b in lri.ModuleBlocks) if (ExportWindow.Group((int)b.Module.Id) != 0) return 0;
        return 1;
    }

    /// <summary>ParamFloat 19 setter (`RendererPrivate::setProperty`, id 0x13): the value must lie in [0.1,
    /// 10.0] and the rendering mode must not be 1 (RefocusPoint).</summary>
    public static float ValidateMaxInFocusBlurPixels(float value, int renderingMode)
    {
        if (!(value >= 0.1f) || value > 10.0f) throw new ArgumentException("Invalid value for MaximumInFocusBlurPixels!");
        if (renderingMode == 1) throw new InvalidOperationException("Cannot set MaximumInFocusBlurPixels in RefocusPoint rendering mode!");
        return value;
    }

    /// <summary>
    /// The in-focus range (focus, f, pitch, N, blur) — the depth interval whose blur stays under `blur` pixels at f-number `N`.
    /// Requires `0 &lt; blur &lt; 10` ("Invalid max infocus blur!"). Single precision, in this order:
    /// <c>K = (f·f) / (pitch·(N+N)); t = (1/K)·((focus − f)·blur); near = focus/(t + 1); far = focus/(1 − t)</c>,
    /// `far` replaced by 100000 when negative, then `min(focus·10, far)` (MINSS order: NaN gives `far`).
    /// </summary>
    public static (float Near, float Far) InFocusRange(float focus, float f, float pitch, float n, float blur)
    {
        if (0.0f >= blur || blur >= 10.0f) throw new ArgumentException("Invalid max infocus blur!");
        float a = focus - f;
        float k = (f * f) / (pitch * (n + n));
        a = a * blur;
        float t = (1.0f / k) * a;
        float near = focus / (t + 1.0f);
        float far = focus / (1.0f - t);
        if (far < 0.0f) far = 100000.0f;
        float cap = focus * 10.0f;
        return (near, cap < far ? cap : far);                                   // MINSS order: far on a tie or NaN
    }

    /// <summary>
    /// The capture's own focus distances (focus source types 0 and 1).
    /// Every camera id of the stream (ids in image-list order, first occurrence) is looked up at the stream's frame;
    /// an image without `af_info` is skipped. Type 1 reads
    /// `af_info.disparity_focus_distance`, type 0 `af_info.contrast_focus_distance`; when the
    /// optional is absent the value of the previous camera is reused (the local is not reset). Values with `|v| &lt; 1e-6`
    /// (double compare) or NaN are dropped; the rest go to list A when the camera is in the reference camera's group and to
    /// list B otherwise. Both are sorted ascending (`std::sort`); B is used when non-empty, else A. See
    /// <see cref="HarmonicMean"/> for the reduction.
    /// </summary>
    public static float FocusFromAf(LriFile lri, int type)
    {
        if (type != 0 && type != 1) throw new ArgumentException("Unexpected CaptureAutoFocusDepthType!");
        var records = new List<(int Id, bool HasAf, bool HasValue, float Value)>(); var seen = new HashSet<int>();
        foreach (var b in lri.ModuleBlocks)
        {
            int id = (int)b.Module.Id;
            if (!seen.Add(id)) continue;
            var af = lri.Modules[b.Module.Id.ToString()].Module.AfInfo;
            if (af is null) records.Add((id, false, false, 0.0f));
            else if (type == 1) records.Add((id, true, af.HasDisparityFocusDistance, af.DisparityFocusDistance));
            else records.Add((id, true, af.HasContrastFocusDistance, af.ContrastFocusDistance));
        }
        return FocusFromRecords(records, (int)lri.Header.ImageReferenceCamera);
    }

    /// <summary>The selection loop of <see cref="FocusFromAf"/> (types 0/1) over per-camera records in stream order.</summary>
    public static float FocusFromRecords(IEnumerable<(int Id, bool HasAf, bool HasValue, float Value)> records, int refId)
    {
        int refGroup = ExportWindow.Group(refId);
        var same = new List<float>(); var other = new List<float>();
        float v = 0.0f;
        foreach (var r in records)
        {
            if (!r.HasAf) continue;
            if (r.HasValue) v = r.Value;
            if (!((double)MathF.Abs(v) >= 1e-6)) continue;                       // below 1e-6 or NaN skips
            (ExportWindow.Group(r.Id) == refGroup ? same : other).Add(v);
        }
        same.Sort(); other.Sort();
        return HarmonicMean((other.Count != 0 ? other : same).ToArray());
    }

    /// <summary>
    /// The tail of the focus-distance reduction: empty → 0; one value → that value; otherwise `count / Σ(1/v)` over `[begin + k, end − k)` with
    /// `k = (n &lt; 3) ? 1 : 0` — so EXACTLY TWO values trim to an empty range and return `(float)0 / 0.0f` = NaN (the count 0 as a float,
    /// divided by the zero sum; no guard). Three or more are all used. The sum takes the first `count &amp; ~7` values in blocks of 8 through
    /// `rcpps` + one Newton step into two 4-lane accumulators (pairs of blocks: `acc0 += r; acc0 += (1 − v·r)·r` for the first two quads, a
    /// pre-added `(1 − v·r)·r + r` for the last two; a single trailing block always pre-added), reduced as `(a0+a2) + (a1+a3)` of
    /// `acc0 + acc1`, and the remaining values by true division added in order.
    /// </summary>
    public static float HarmonicMean(ReadOnlySpan<float> sorted)
    {
        int n = sorted.Length;
        if (n == 0) return 0.0f;
        if (n == 1) return sorted[0];
        int k = n < 3 ? 1 : 0;
        var v = sorted.Slice(k, n - 2 * k); int m = v.Length;
        float sum = 0.0f;
        if (m != 0)
        {
            int i = 0;
            if (m >= 8)
            {
                int vec = m & ~7, blocks = vec / 8;
                var acc0 = Vector128<float>.Zero; var acc1 = Vector128<float>.Zero; var one = Vector128.Create(1.0f);
                Vector128<float> Q(ReadOnlySpan<float> s, int o) => Vector128.Create(s[o], s[o + 1], s[o + 2], s[o + 3]);
                for (int p = 0; p < blocks / 2; p++, i += 16)
                {
                    var v0 = Q(v, i); var v1 = Q(v, i + 4); var v2 = Q(v, i + 8); var v3 = Q(v, i + 12);
                    var r0 = IntelApprox.Reciprocal(v0); var n0 = (one - v0 * r0) * r0;
                    var r1 = IntelApprox.Reciprocal(v1); var n1 = (one - v1 * r1) * r1;
                    acc0 += r0; acc0 += n0; acc1 += r1; acc1 += n1;
                    var r2 = IntelApprox.Reciprocal(v2); acc0 += (one - v2 * r2) * r2 + r2;
                    var r3 = IntelApprox.Reciprocal(v3); acc1 += (one - v3 * r3) * r3 + r3;
                }
                if ((blocks & 1) != 0)
                {
                    var lo = Q(v, i); var hi = Q(v, i + 4);
                    var rh = IntelApprox.Reciprocal(hi); acc1 += (one - hi * rh) * rh + rh;
                    var rl = IntelApprox.Reciprocal(lo); acc0 += (one - lo * rl) * rl + rl;
                    i += 8;
                }
                acc0 += acc1;
                sum = (acc0.GetElement(2) + acc0.GetElement(0)) + (acc0.GetElement(3) + acc0.GetElement(1));
            }
            for (; i < m; i++) sum += 1.0f / v[i];
        }
        return (float)m / sum;
    }

    /// <summary>The point that focus source type 2 samples: the reference image's `af_info.roi_center` when present,
    /// else (0.5, 0.5).</summary>
    public static (float X, float Y) AfPoint(LriFile lri)
    {
        var af = lri.Modules[lri.ReferenceModule].Module.AfInfo;
        return af?.RoiCenter is { } roi ? (roi.X, roi.Y) : (0.5f, 0.5f);
    }

    /// <summary>
    /// The mean depth of a 3×3 window on the depth-cache image:
    /// `x = (int)((float)w · px)`, `y = (int)((float)h · py)` (truncation), window `[x−1, x+2) × [y−1, y+2)` clipped to the image's rect,
    /// values summed row-major in single precision, divided in single precision by the number of samples. An empty window or a sum that
    /// is not &gt; 0 raises "Bad depth value!". This is both focus source type 2 (at <see cref="AfPoint"/>) and
    /// `CIAPI::DepthEditor::getDepthAtPoint` (after <see cref="MapExportPoint"/>).
    /// </summary>
    public static float DepthAtPoint(DepthImage depth, float px, float py)
    {
        int x = Cvtt((float)depth.W * px), y = Cvtt((float)depth.H * py);
        int x0 = Math.Max(x - 1, depth.Rect.X0), y0 = Math.Max(y - 1, depth.Rect.Y0), x1 = Math.Min(x + 2, depth.Rect.X1), y1 = Math.Min(y + 2, depth.Rect.Y1);
        float sum = 0.0f; int count = 0;
        if (x1 > x0 && y1 > y0)
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) { sum += depth[xx, yy]; count++; }
        if (count == 0 || !(sum > 0.0f)) throw new InvalidOperationException("Bad depth value!");
        return sum / (float)count;
    }

    /// <summary>`cvttss2si`: truncation, with the integer-indefinite value for NaN and out-of-range inputs.</summary>
    internal static int Cvtt(float v) => v >= -2147483648.0f && v < 2147483648.0f ? (int)v : int.MinValue;
    /// <summary>`cvttsd2si`.</summary>
    internal static int Cvtt(double v) => v > -2147483649.0 && v < 2147483648.0 ? (int)v : int.MinValue;

    /// <summary>The point that `getDepthAtPoint` hands to <see cref="DepthAtPoint"/>: a point
    /// normalized to the export window becomes a point normalized to the pipeline canvas,
    /// <c>((float)origin0 + (float)exportDims0 · p) / (float)pipelineCacheDims[baseLevel]</c> per axis (level-0 origin, level-0 export dims,
    /// PipelineCache level dims at the base level).</summary>
    public static (float X, float Y) MapExportPoint(float px, float py, (int W, int H) exportDims0, (int X, int Y) origin0, (int W, int H) cacheDims)
        => (((float)origin0.X + (float)exportDims0.W * px) / (float)cacheDims.W, ((float)origin0.Y + (float)exportDims0.H * py) / (float)cacheDims.H);

    /// <summary>
    /// DefaultFocusDepth (ParamFloat 15, and what the renderer writes into ParamFloat 1 when depth becomes available):
    /// with `a` = type 0 and `b` = type 1, both &gt; 0 → `(b + a)·0.5`; only `a` &gt; 0 → `a`; only `b` &gt; 0 → `b`; neither → type 2
    /// (<paramref name="type2"/>, evaluated only then). The compares are ordered, so a NaN source (the two-value case of
    /// <see cref="HarmonicMean"/>) counts as "not available".
    /// </summary>
    public static float DefaultFocusDepth(float a, float b, Func<float> type2)
    {
        if (a > 0.0f && b > 0.0f) return (b + a) * 0.5f;
        if (a > 0.0f) return a;
        if (b > 0.0f) return b;
        return type2();
    }

    /// <summary>DefaultFocusDepth of a capture with its depth-cache image.</summary>
    public static float DefaultFocusDepth(LriFile lri, DepthImage depth)
        => DefaultFocusDepth(FocusFromAf(lri, 0), FocusFromAf(lri, 1), () => { var p = AfPoint(lri); return DepthAtPoint(depth, p.X, p.Y); });

    /// <summary>
    /// ParamFloatArray 0 (with blur = ParamFloat 19) and the RefocusPoint
    /// overlay's range: <see cref="InFocusRange"/> at N = 4.0 with the constants' focal length and SENSOR pixel pitch, around the DEFAULT focus
    /// sources (never the current focus depth): both type 0 and type 1 &gt; 0 → the union `(minss(near0, near1), maxss(far0, far1))`;
    /// only one → its range; neither → the range of type 2.
    /// </summary>
    public static (float Near, float Far) DefaultInFocusRange(DofConstants c, float a, float b, Func<float> type2, float blur)
    {
        if (a > 0.0f && b > 0.0f)
        {
            var r0 = InFocusRange(a, c.FocalLength, c.PixelPitch, RangeFNumber, blur);
            var r1 = InFocusRange(b, c.FocalLength, c.PixelPitch, RangeFNumber, blur);
            return (r0.Near < r1.Near ? r0.Near : r1.Near, r0.Far > r1.Far ? r0.Far : r1.Far);
        }
        float focus = a > 0.0f ? a : b > 0.0f ? b : type2();
        return InFocusRange(focus, c.FocalLength, c.PixelPitch, RangeFNumber, blur);
    }

    /// <summary>The range around the CURRENT focus depth with a caller N clamped to ≥ 2.2; the
    /// edge-heal tool calls it with (2.2, 0.7), the same literals the tile lambda uses.</summary>
    public static (float Near, float Far) CurrentInFocusRange(DofState s, float n, float blur)
        => InFocusRange(s.FocusDepth, s.Constants.FocalLength, s.Constants.PixelPitch, n > 2.2f ? n : 2.2f, blur);
}
