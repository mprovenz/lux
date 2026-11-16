using Lux.Engine.Pipeline.ResAmp;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>Intermediate images of one <see cref="QuickSelect.AddStrokes"/> call (verification).</summary>
public sealed class QuickSelectTrace
{
    public int W, H;
    public byte[]? PrevMask, Threshold, BeforeFilter, Filtered, Result;
    public float[]? Seeds, Input, Propagated, FilterFloat, FilterWA, FilterWB;
    public float[]? PosSamples, NegSamples, PosCentres, NegCentres, PosDist, NegDist;
    /// <summary>Called with the working guide (after the optional resample) when a stroke loads one.</summary>
    public Action<float[], int, int>? OnGuide;
}

/// <summary>
/// The quick-select object of the depth editor (Lumen creates it with (levelSize, 0, 1.0)).
/// It keeps, at the editor's level size, a byte <see cref="Mask"/> and a float seed map, and for the current ROI a
/// gray guide, its CIELAB image and the edge-avoiding-wavelet pyramid of the gray guide.
///
/// A stroke is stamped into the seed map with the brush kernel (hardness 0.9) scaled by ±100 000 (positive strokes
/// add, negative strokes overwrite), the seeds — with a two-pixel frame of negative values where nothing constrains the border — are
/// propagated through the wavelet pyramid, thresholded at 0.2, pruned with two k-means color models of the
/// positively and negatively stroked pixels, smoothed with a domain-transform filter guided by the gray image and
/// cleaned. When the stroke level is coarser than the editor's level the seeds / mask are resampled to and from the working size.
/// </summary>
public sealed class QuickSelect
{
    public readonly int LevelW, LevelH;
    /// <summary>The selection, 0 / 1 per pixel of the editor's level.</summary>
    public readonly byte[] Mask;
    /// <summary>The accumulated stroke seeds.</summary>
    public readonly float[] Seeds;
    /// <summary>0 = new, 1 = image (re)loaded after strokes, 2 = strokes present.</summary>
    public int State { get; private set; }
    /// <summary>The ROI in working (guide image) coordinates, and the same in the editor's level.</summary>
    public RectI WorkRect { get; private set; }
    public RectI LevelRect { get; private set; }
    public float[]? Grey { get; private set; }
    public float[]? Lab { get; private set; }
    public int ImageW { get; private set; }
    public int ImageH { get; private set; }
    public EawPyramid? Pyramid { get; private set; }
    /// <summary>`std::random_device` of the k-means steps (see <see cref="QuickSelectKMeans"/>).</summary>
    public Func<uint> RandomDevice;
    /// <summary>Keep the transformed pyramid level images (verification).</summary>
    public bool KeepPyramidImages;
    /// <summary>Stops a running <see cref="AddStrokes"/> before it writes: the stroke's work is on copies until its last step, so a
    /// stop leaves <see cref="Mask"/>, <see cref="Seeds"/> and <see cref="State"/> as they were. No effect on a stroke that runs to its end.</summary>
    public CancellationToken Cancel;

    /// <summary>The guide, rects and state (what <see cref="Init"/> replaces) — with <see cref="Restore"/> a stroke stopped after a new region's
    /// <see cref="Init"/> leaves the object as it was.</summary>
    public (float[]? Grey, float[]? Lab, EawPyramid? Pyramid, int W, int H, RectI Work, RectI Level, int State) Save() => (Grey, Lab, Pyramid, ImageW, ImageH, WorkRect, LevelRect, State);
    public void Restore((float[]? Grey, float[]? Lab, EawPyramid? Pyramid, int W, int H, RectI Work, RectI Level, int State) m)
    { Grey = m.Grey; Lab = m.Lab; Pyramid = m.Pyramid; ImageW = m.W; ImageH = m.H; WorkRect = m.Work; LevelRect = m.Level; State = m.State; }

    public const float PositiveSeed = 100000.0f, NegativeSeed = -100000.0f, SoftBorder = -10.0f;   // 0x47c35000, 0xc7c35000
    public const float MaskThreshold = 0.2f, StrokeLimit = 50000.0f, KeepAbove = 1.2f;              // 0x3e4ccccd
    public const float FilterSigmaS = 10.0f; public static readonly float FilterSigmaR = BitConverter.Int32BitsToSingle(0x3da0a0a1);

    public QuickSelect((int W, int H) level, Func<uint> randomDevice)
    {
        LevelW = level.W; LevelH = level.H; RandomDevice = randomDevice;
        Mask = new byte[(long)level.W * level.H]; Seeds = new float[(long)level.W * level.H];
        WorkRect = LevelRect = new RectI(0, 0, level.W, level.H);
    }

    /// <summary>Load the image (qs, image, roi, scale): "Input image is too small for QuickSelection!" below 5×5; gray image, pyramid
    /// (<c>floor(log2(min side)) − 1</c> levels, distance type 0, parameter 1.0), CIELAB image; the two rects
    /// (<c>levelRect = (cvtt(x0·s), cvtt(y0·s), cvtt(w·s) + cvtt(x0·s), cvtt(h·s) + cvtt(y0·s))</c>);
    /// "ROI should have the same size as the input image!"; state 2 → 1.</summary>
    public void Init(float[] rgba, int w, int h, RectI roi, float scale)
    {
        if (w < 5 || h < 5) throw new InvalidOperationException("Input image is too small for QuickSelection!");
        // stops between the steps (the caller restores the object from its Save; the steps only replace references)
        Cancel.ThrowIfCancellationRequested();
        using (Diagnostics.PhaseLog.Time("qs.grey")) Grey = QuickSelectFilters.Grey(rgba, w, h);
        ImageW = w; ImageH = h;
        int levels = System.Numerics.BitOperations.Log2((uint)Math.Min(w, h)) - 1;
        Cancel.ThrowIfCancellationRequested();
        using (Diagnostics.PhaseLog.Time("qs.pyramid")) Pyramid = new EawPyramid(Grey, w, h, levels, 1.0f, KeepPyramidImages);
        Cancel.ThrowIfCancellationRequested();
        using (Diagnostics.PhaseLog.Time("qs.lab")) Lab = QuickSelectFilters.Lab(rgba, w, h);
        Cancel.ThrowIfCancellationRequested();
        if (roi.X0 < roi.X1 && roi.Y0 < roi.Y1)
        {
            WorkRect = roi;
            int bx = EditMath.Cvtt((float)roi.X0 * scale), by = EditMath.Cvtt((float)roi.Y0 * scale);
            LevelRect = new RectI(bx, by, EditMath.Cvtt((float)(roi.X1 - roi.X0) * scale) + bx, EditMath.Cvtt((float)(roi.Y1 - roi.Y0) * scale) + by);
            if (roi.Width != w || roi.Height != h) throw new InvalidOperationException("ROI should have the same size as the input image!");
        }
        if (State == 2) State = 1;
    }

    /// <summary>Clear: mask and seeds zeroed, both rects reset to the whole level. (The guide, the pyramid and the state stay.)</summary>
    public void Clear()
    {
        Array.Clear(Mask); Array.Clear(Seeds);
        WorkRect = LevelRect = new RectI(0, 0, LevelW, LevelH);
    }

    static T[] CropView<T>(T[] src, int sw, RectI r)
    {
        var o = new T[(long)r.Width * r.Height];
        for (int y = 0; y < r.Height; y++) Array.Copy(src, (long)(r.Y0 + y) * sw + r.X0, o, (long)y * r.Width, r.Width);
        return o;
    }
    static void StoreView<T>(T[] block, T[] dst, int dw, RectI r)
    {
        for (int y = 0; y < r.Height; y++) Array.Copy(block, (long)y * r.Width, dst, (long)(r.Y0 + y) * dw + r.X0, r.Width);
    }

    /// <summary>
    /// Add strokes (qs, points, radiusPx, hardness, positive). Points are pixels of the working image.
    /// Same-size case: merge the stroke into the seed view, propagate, build the mask, copy it into the mask view.
    /// Otherwise the seed view and the mask view are first brought to the working size by nearest picks;
    /// afterwards the mask goes back through `ImageResample&lt;1, u8&gt;` and the merged seeds through
    /// the nearest pick.
    /// </summary>
    public void AddStrokes(IReadOnlyList<(int X, int Y)> points, int radiusPx, float hardness, bool positive, QuickSelectTrace? trace = null)
    {
        if (Pyramid == null || Grey == null || Lab == null) throw new InvalidOperationException("quick select has no guide image");
        var view = LevelRect.Intersect(new RectI(0, 0, LevelW, LevelH));
        if (view.IsEmpty) throw new InvalidOperationException("the quick-select region lies outside the depth map");
        int ww = WorkRect.Width, wh = WorkRect.Height;
        if (ww != ImageW || wh != ImageH)
            throw new InvalidOperationException("the quick-select region no longer matches its guide image (cleared without a new region)");
        var seedView = CropView(Seeds, LevelW, view); var maskView = CropView(Mask, LevelW, view);
        bool same = LevelRect.Width == ww && LevelRect.Height == wh;
        float[] seeds; byte[] prev;
        if (same) { seeds = seedView; prev = maskView; }
        else
        {
            seeds = EditResample.Nearest(seedView, view.Width, view.Height, 1, ww, wh);
            prev = EditResample.Nearest(maskView, view.Width, view.Height, 1, ww, wh);
        }
        if (seeds.LongLength != (long)ww * wh) throw new InvalidOperationException("image size mismatch!");
        if (trace != null) { trace.W = ww; trace.H = wh; trace.PrevMask = (byte[])prev.Clone(); }
        Cancel.ThrowIfCancellationRequested();
        float[] input; using (Diagnostics.PhaseLog.Time("qs.merge")) input = Merge(seeds, prev, ww, wh, points, radiusPx, hardness, positive);
        Cancel.ThrowIfCancellationRequested();
        if (trace != null) { trace.Seeds = (float[])seeds.Clone(); trace.Input = input; }
        float[] prop; using (Diagnostics.PhaseLog.Time("qs.diffuse")) prop = Pyramid.Diffuse(input, ww, wh);
        if (trace != null) trace.Propagated = prop;
        Cancel.ThrowIfCancellationRequested();
        byte[] mask; using (Diagnostics.PhaseLog.Time("qs.mask")) mask = BuildMask(input, prop, ww, wh, trace);
        Cancel.ThrowIfCancellationRequested();   // the last stop: from here on the object is written
        if (same) StoreView(mask, Mask, LevelW, view);
        else
        {
            StoreView(EditResample.BilinearU8(mask, ww, wh, LevelRect.Width, LevelRect.Height), Mask, LevelW, view);
            StoreView(EditResample.Nearest(seeds, ww, wh, 1, LevelRect.Width, LevelRect.Height), Seeds, LevelW, view);
        }
        if (same) StoreView(seeds, Seeds, LevelW, view);
        State = 2;
    }

    /// <summary>
    /// The seed merge: stroke mask M (the brush stamp); positive: <c>seed = M·100000 + seed</c>,
    /// negative: <c>seed = 0 &lt; M ? M·−100000 : seed</c>. Frame image: state 0 → the two outermost rows / columns
    /// −100000; state 1 / 2 → −10 on those pixels where the previous mask is 0. The diffusion input
    /// is <c>seed == 0 ? frame : seed</c>.
    /// </summary>
    float[] Merge(float[] seeds, byte[] prev, int w, int h, IReadOnlyList<(int X, int Y)> points, int radiusPx, float hardness, bool positive)
    {
        var m = BrushStroke.MaskPixels(points, w, h, radiusPx, hardness);
        // per-pixel loops in row bands (each element on its own)
        if (positive) QuickSelectFilters.ForRows(h, (ya, yb) => { for (long i = (long)ya * w; i < (long)yb * w; i++) seeds[i] = m[i] * PositiveSeed + seeds[i]; });
        else QuickSelectFilters.ForRows(h, (ya, yb) => { for (long i = (long)ya * w; i < (long)yb * w; i++) seeds[i] = 0.0f < m[i] ? m[i] * NegativeSeed : seeds[i]; });
        var frame = new float[(long)w * h];
        void Set(int x, int y) { long i = (long)y * w + x; if (State == 0) frame[i] = NegativeSeed; else if (prev[i] == 0) frame[i] = SoftBorder; }
        for (int x = 0; x < w; x++) { Set(x, 0); Set(x, 1); Set(x, h - 2); Set(x, h - 1); }
        for (int y = 2; y < h - 2; y++) { Set(0, y); Set(1, y); Set(w - 2, y); Set(w - 1, y); }
        var input = new float[(long)w * h];
        QuickSelectFilters.ForRows(h, (ya, yb) => { for (long i = (long)ya * w; i < (long)yb * w; i++) input[i] = 0.0f == seeds[i] ? frame[i] : seeds[i]; });
        return input;
    }

    /// <summary>
    /// The mask build (qs, out, input, P): <c>mask = P &gt; 0.2</c>; color samples from the CIELAB image, classified by
    /// the <b>diffusion input</b> (seeds with the frame, so the −100000 frame of a first stroke counts as negative): above 50000 →
    /// positive set, below −50000 → negative set, the rest where the mask is set → candidates. With more than four samples in
    /// both sets, a 4-means model of each (positive first) and each candidate's distance to both: a candidate nearer to the negative
    /// model is dropped unless P ≥ 1.2. Then `ImageFilterDT` (σs 10, σr 20/255, one pass, gray guide) and the clean-up (<see cref="QuickSelectFilters.Cleanup"/>).
    /// </summary>
    byte[] BuildMask(float[] seeds, float[] P, int w, int h, QuickSelectTrace? trace)
    {
        long n = (long)w * h; var mask = new byte[n];
        QuickSelectFilters.ForRows(h, (ya, yb) => { for (long i = (long)ya * w; i < (long)yb * w; i++) mask[i] = MaskThreshold < P[i] ? (byte)1 : (byte)0; });
        if (trace != null) trace.Threshold = (byte[])mask.Clone();
        var pos = new List<float>(); var neg = new List<float>(); var cand = new List<float>(); var where = new List<long>();
        var lab = Lab!;
        for (long i = 0; i < n; i++)
        {
            float s = seeds[i];
            if (s <= StrokeLimit)
            {
                if (-StrokeLimit <= s) { if (mask[i] != 0) { cand.Add(lab[i * 4]); cand.Add(lab[i * 4 + 1]); cand.Add(lab[i * 4 + 2]); cand.Add(lab[i * 4 + 3]); where.Add(i); } }
                else { neg.Add(lab[i * 4]); neg.Add(lab[i * 4 + 1]); neg.Add(lab[i * 4 + 2]); neg.Add(lab[i * 4 + 3]); }
            }
            else { pos.Add(lab[i * 4]); pos.Add(lab[i * 4 + 1]); pos.Add(lab[i * 4 + 2]); pos.Add(lab[i * 4 + 3]); }
        }
        if (pos.Count / 4 > 4 && neg.Count / 4 > 4)
        {
            var ps = pos.ToArray(); var ns = neg.ToArray(); var cs = cand.ToArray();
            var phk = Diagnostics.PhaseLog.Time("qs.kmeans");
            var kp = new QuickSelectKMeans(ps, ps.Length / 4, 4, RandomDevice);
            var kn = new QuickSelectKMeans(ns, ns.Length / 4, 4, RandomDevice);
            var dp = kp.Distances(cs, where.Count); var dn = kn.Distances(cs, where.Count);
            phk.Dispose();
            if (trace != null) { trace.PosSamples = ps; trace.NegSamples = ns; trace.PosCentres = kp.Centres; trace.NegCentres = kn.Centres; trace.PosDist = dp; trace.NegDist = dn; }
            for (int k = 0; k < where.Count; k++)
                if (dn[k] < dp[k] && P[where[k]] < KeepAbove) mask[where[k]] = 0;
        }
        if (trace != null) trace.BeforeFilter = (byte[])mask.Clone();
        var phf = Diagnostics.PhaseLog.Time("qs.filter");
        var filtered = QuickSelectFilters.DomainTransform(mask, Grey!, w, h, FilterSigmaS, FilterSigmaR, 1,
            trace == null ? null : (f, wa, wb) => { trace.FilterFloat = (float[])f.Clone(); trace.FilterWA = wa; trace.FilterWB = wb; });
        phf.Dispose();
        if (trace != null) trace.Filtered = (byte[])filtered.Clone();
        using (Diagnostics.PhaseLog.Time("qs.cleanup")) QuickSelectFilters.Cleanup(filtered, w, h);
        if (trace != null) trace.Result = (byte[])filtered.Clone();
        return filtered;
    }

    /// <summary>The guide resample (out, in, {w, h}): `A::BilinearResample&lt;vec4x32f&gt;` with offset 0 and scale
    /// <c>(double)src / (double)dst</c> per axis (the guide of a stroke level finer than the editor's).</summary>
    public static float[] ResampleGuide(float[] rgba, int sw, int sh, int dw, int dh)
    {
        if (dw < 1 || dh < 1) throw new InvalidOperationException("invalid size!");
        var src = new ResImage(rgba, 0, sw, sh, sw, 4, 0, 0, sw, sh); var dst = new ResImage(dw, dh, 4);
        ResAmpKernels.BilinearResample(dst, src, 0.0, 0.0, (double)sw / (double)dw, (double)sh / (double)dh);
        return dst.Data;
    }
}
