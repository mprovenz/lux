using Lux.Engine.Pipeline.Cache;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// The color images of the depth editor, from the capture's own pipeline: the fused tile of the PipelineCache followed by the editor's
/// color conversion (in Lumen: the depth editor's own SoftISP with the settings of <see cref="BuildTuning"/>, run as
/// the Color-domain runner over the whole fetched image with a zero rect). With that tuning the Color block holds: slot 2 (÷ neutral),
/// color_correction default, the constructor's
/// PostProcessing, and in the tone-mapping slot the working-space → output-space conversion that `tone_mapping.type = none` installs.
/// </summary>
public sealed class DepthEditColour
{
    readonly PipelineCache _cache; readonly SoftIsp _isp; readonly IspStats _stats; readonly CapturedFrame _frame; readonly ExportLevels _lv;

    /// <summary>The editor's tuning: Lumen's default tuning with demosaicking.type = none, color_correction.type = default,
    /// auto_white_balance.type = manual_color, output.color_space = srgb and auto_white_balance.neutral_color = the capture's AsShot
    /// neutral.</summary>
    public static Tuning BuildTuning(float[] neutral)
    {
        var t = Tuning.LumenDefaults();
        t.Set("demosaicking.type", "none");
        t.Set("color_correction.type", "default");
        t.Set("auto_white_balance.type", "manual_color");
        t.Set("output.color_space", "srgb");
        t.Set("auto_white_balance.neutral_color", neutral.Select(v => (double)v).ToArray());
        return t;
    }

    /// <param name="cache">the renderer's PipelineCache</param>
    /// <param name="frame">the reference capture</param>
    /// <param name="neutral">the capture's AsShot neutral (`LriFile.LumenNeutral`)</param>
    /// <param name="lv">the renderer's level tables</param>
    public DepthEditColour(PipelineCache cache, CapturedFrame frame, LumenProfile colour, float[] neutral, ExportLevels lv)
    {
        _cache = cache; _frame = frame; _lv = lv;
        _isp = new SoftIsp(BuildTuning(neutral), colour);
        _stats = _isp.ComputeStats(frame);
    }

    /// <summary>The level that the tile fetch picks for a negative level argument: the first cache level whose dims do not exceed the
    /// requested ones (<c>max(w·(1/reqW), h·(1/reqH)) ≤ 1</c>), else the last.</summary>
    public static int AutoLevel(PipelineCache cache, (int W, int H) req)
    {
        int n = cache.LevelDims.Length, l = 0;
        if (n == 0) throw new InvalidOperationException("the PipelineCache has no levels");
        do
        {
            float a = (float)cache.LevelDims[l].W * (1.0f / (float)req.W), b = (float)cache.LevelDims[l].H * (1.0f / (float)req.H);
            if (b <= a) b = a;
            if (!(1.0f < b)) break;
            l++;
        } while (l < n);
        return Math.Min(l, n - 1);
    }

    float[] Convert(float[] rgba, int w, int h)
    {
        var img = new Image<Vec4F>(new RectI(0, 0, w, h));
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vec4F>(rgba.AsSpan()).CopyTo(img.Data);
        var o = _isp.ProcessColorFloat(_frame, _stats, img, null, default, 0, null, roi: img.Rect);
        if (o.Width != w || o.Height != h) throw new InvalidOperationException("the color conversion changed the image size");
        // tone_mapping.type = none still fills the last slot of the Color block: the tone-mapping setter installs a conversion from the
        // working space (ProPhoto D50) to the Stats output space when the two differ — here linear ProPhoto → sRGB.
        var working = ColorSpace.ProPhotoD50;
        if (!working.SameAs(_stats.OutSpace)) ColorSpaceConvert.Convert(o, o, working, _stats.OutSpace, 1);
        var res = new float[(long)w * h * 4];
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.MemoryMarshal.Cast<Vec4F, float>(o.Row(y)).CopyTo(res.AsSpan(y * w * 4, w * 4));
        return res;
    }

    /// <summary>The color ROI of a level: <paramref name="rect"/> in that level's pipeline frame (level rect + origin) —
    /// the tile fetch (pipelineCache, out, rect, pipeline dims of the level, −1), then the conversion. The guide of the adaptive brush and
    /// the edge heal (<see cref="DepthEditor.GuideProvider"/>) is this at the edited level; quick select uses any level
    /// (<see cref="DepthEditor.LevelGuideProvider"/>).</summary>
    public float[] Guide(int level, RectI rect)
    {
        var req = _lv.PipelineDims[level];
        var px = _cache.Render(AutoLevel(_cache, req), rect, req);
        return Convert(px, rect.Width, rect.Height);
    }

    /// <summary>The reference window of the adaptive brush: `TileCache::renderROI&lt;vec4x32f&gt;(pipelineCache, out, rect, 1)`
    /// then the conversion (<see cref="DepthEditor.ReferenceProvider"/>).</summary>
    public float[] Reference(RectI rect)
        => Convert(_cache.RenderRoi(Math.Min(1, _cache.LevelDims.Length - 1), rect), rect.Width, rect.Height);

    /// <summary>Install the three color providers on an editor.</summary>
    public void Attach(DepthEditor editor)
    {
        int level = editor.Level;
        editor.GuideProvider = r => Guide(level, r);
        editor.ReferenceProvider = Reference;
        editor.LevelGuideProvider = Guide;
    }
}
