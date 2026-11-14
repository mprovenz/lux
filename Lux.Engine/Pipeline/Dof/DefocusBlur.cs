using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Lux.Engine.Imaging;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Dof;

/// <summary>
/// Lumen's defocus blur object (construction, run and post stage):
/// signed circle-of-confusion radius maps of a <see cref="DualLayerBuffer"/>, a ladder of radius bands, one disc-blurred image per band
/// and a back-to-front premultiplied composite.
/// </summary>
public sealed class DefocusBlur
{
    /// <summary>One radius band (lo, hi, disc diameter, visible pixel list, occluded pixel
    /// list). Pixels are stored as y·w + x in the order Lumen appends them (row-major).</summary>
    public sealed class Layer
    {
        public float Lo, Hi; public int Diameter;
        internal int[] VisBuf = Array.Empty<int>(), OccBuf = Array.Empty<int>();
        internal int VisStart, VisCount, OccStart, OccCount;
        /// <summary>The band's visible pixels (row-major).</summary>
        public ReadOnlySpan<int> Vis => VisBuf.AsSpan(VisStart, VisCount);
        /// <summary>The band's occluded pixels (row-major).</summary>
        public ReadOnlySpan<int> Occ => OccBuf.AsSpan(OccStart, OccCount);
    }

    /// <summary>The scratch of a run whose temporaries are pooled (no observer): released by <see cref="Release"/>.</summary>
    bool _pooled;
    int[]? _visIdx, _occIdx;

    readonly DualLayerBuffer _buf; readonly RectI _in, _out; readonly float _f, _pitch, _n, _focus; readonly int _deviceType; readonly DofDevice _p;
    readonly bool _hasMatte, _hasOcc; readonly int _w, _h;

    public float[]? RadiusVis, RadiusOcc; public float MinRadius, MaxRadius; public List<Layer>? Layers;
    /// <summary>Test hook: called after each band with (band index, band image, blurred band, accumulation).</summary>
    public Action<int, DofImage, DofImage, DofImage>? BandObserver;

    /// <summary>The constructor (buffer, inRect, outRect, f, pitch, N, focus, device).</summary>
    public DefocusBlur(DualLayerBuffer buffer, RectI inRect, RectI outRect, float f, float pitch, float n, float focus, int deviceType)
    {
        _buf = buffer; _in = inRect; _out = outRect; _f = f; _pitch = pitch; _n = n; _focus = focus; _deviceType = deviceType;
        if (buffer.Vis == null || buffer.VisDepth == null) throw new InvalidOperationException("no visible image or depth in the dual layered buffer");
        _w = buffer.Vis.W; _h = buffer.Vis.H;
        if (_w != inRect.X1 - inRect.X0 || _h != inRect.Y1 - inRect.Y0) throw new InvalidOperationException("the input buffer and tile size are not the same");
        if (outRect.X0 < inRect.X0 || inRect.X1 <= outRect.X0 || outRect.Y0 < inRect.Y0 || inRect.Y1 <= outRect.Y0 || outRect.X1 <= inRect.X0 || inRect.X1 < outRect.X1 ||
            outRect.Y1 <= inRect.Y0 || inRect.Y1 < outRect.Y1) throw new InvalidOperationException("output tile is not a subset of the input tile ");
        _hasMatte = buffer.Matte != null;
        _hasOcc = buffer.Occ != null && buffer.OccDepth != null;
        if (_hasMatte && !_hasOcc) throw new InvalidOperationException("no occluded layer in matting");
        _p = DofDevice.For(deviceType);
    }

    // ---- circle of confusion ----

    /// <summary>The radius map (depth, focus, f, N, pitch, device): K = f·f / ((pitch + pitch)·N);
    /// r = rcpss(d·(focus − f)) · ((d − focus)·(d &lt; focus ? K·front : K)), then maxss(r, −(front·(float)maxR)), minss(·, (float)maxR).
    /// Raw `rcpss`, no Newton step.</summary>
    public static float[] RadiusMap(DofImage depth, float focus, float f, float pitch, float n, DofDevice p) => RadiusMap(depth, focus, f, pitch, n, p, null);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static float[] RadiusMap(DofImage depth, float focus, float f, float pitch, float n, DofDevice p, float[]? dst)
    {
        float k = (f * f) / ((pitch + pitch) * n), c = focus - f, kf = k * p.FrontScale, hi = (float)p.MaxRadius, lo = -(p.FrontScale * hi);
        int w = depth.W, h = depth.H; var r = dst ?? new float[w * h];
        var C = Vector256.Create(c); var F = Vector256.Create(focus); var K = Vector256.Create(k); var KF = Vector256.Create(kf);
        var LO = Vector256.Create(lo); var HI = Vector256.Create(hi);
        ref float db = ref MemoryMarshal.GetArrayDataReference(depth.D);
        ref float rb = ref MemoryMarshal.GetArrayDataReference(r);
        for (int y = 0; y < h; y++)
        {
            int b = depth.Idx(0, y), x = 0;
            // eight pixels at a time: the same operations per lane (rcpps model, two products, the select and the clamps as compares)
            if (Vector256.IsHardwareAccelerated)
                for (; x + 8 <= w; x += 8)
                {
                    var d = Vector256.LoadUnsafe(ref db, (nuint)(b + x));
                    var rc = IntelApprox.Reciprocal(d * C); var dm = d - F;
                    var v = Vector256.ConditionalSelect(Vector256.LessThan(d, F), rc * (dm * KF), rc * (dm * K));
                    v = Vector256.ConditionalSelect(Vector256.GreaterThan(v, LO), v, LO);
                    v = Vector256.ConditionalSelect(Vector256.LessThan(v, HI), v, HI);
                    v.StoreUnsafe(ref rb, (nuint)(y * w + x));
                }
            for (; x < w; x++)
            {
                float d = depth.D[b + x];
                float back = IntelApprox.Rcp(d * c) * ((d - focus) * k), front = IntelApprox.Rcp(d * c) * ((d - focus) * kf);
                float v = d < focus ? front : back;
                v = v > lo ? v : lo;
                v = v < hi ? v : hi;
                r[y * w + x] = v;
            }
        }
        return r;
    }

    /// <summary>Both radius maps and their joint min / max. Occluded pixels without an occluded depth (depth ≤ 0) get the
    /// sentinel −65535 and enter the min / max as 0.5·(max + min) of the visible map.</summary>
    void ComputeRadii()
    {
        int np = _w * _h;
        RadiusVis = RadiusMap(_buf.VisDepth!, _focus, _f, _pitch, _n, _p, _pooled ? ScratchPool.Rent<float>(np) : null);
        float mn = float.MaxValue, mx = -float.MaxValue;
        foreach (float v in RadiusVis.AsSpan(0, np)) { if (v <= mn) mn = v; if (mx <= v) mx = v; }
        MinRadius = mn; MaxRadius = mx;
        var od = _buf.OccDepth;
        if (od == null) { RadiusOcc = null; return; }
        if (_pooled && !AnyPositive(od))
        {
            // every occluded pixel takes the sentinel (no band) and enters the min / max as mid: the loop below reduces to one step
            float m0 = (mx + mn) * 0.5f, a0 = float.MaxValue, b0 = -float.MaxValue;
            if (m0 <= a0) a0 = m0;
            if (b0 <= m0) b0 = m0;
            if (mn <= a0) a0 = mn;
            if (b0 <= mx) b0 = mx;
            MinRadius = a0; MaxRadius = b0; RadiusOcc = null;
            return;
        }
        var ro = RadiusMap(od, _focus, _f, _pitch, _n, _p, _pooled ? ScratchPool.Rent<float>(np) : null);
        float mid = (mx + mn) * 0.5f, omn = float.MaxValue, omx = -float.MaxValue;
        for (int y = 0; y < od.H; y++)
        {
            int b = od.Idx(0, y);
            for (int x = 0; x < od.W; x++)
            {
                bool has = 0f < od.D[b + x]; int i = y * od.W + x;
                if (!has) ro[i] = -65535f;
                float v = has ? ro[i] : mid;
                if (v <= omn) omn = v;
                if (omx <= v) omx = v;
            }
        }
        if (mn <= omn) omn = mn;
        if (omx <= mx) omx = mx;
        MinRadius = omn; MaxRadius = omx; RadiusOcc = ro;
    }

    /// <summary>Whether any pixel of the image is &gt; 0 (`0 &lt; d`, so NaN counts as not).</summary>
    static bool AnyPositive(DofImage img)
    {
        for (int y = 0; y < img.H; y++)
        {
            var row = img.D.AsSpan(img.Idx(0, y), img.W);
            foreach (float v in row) if (0f < v) return true;
        }
        return false;
    }

    // ---- band ladder ----

    /// <summary>The geometric ladder (the ladder type with flag 1): bands that intersect [floor(min), ceil(max)].
    /// With B = (float)(2·bandHalf)·0.5 and L = (float)linear + B: positive [e, e+1) from B while below L, then [e, e·ratio) from L while the upper
    /// edge is below ceil(max); the centre [−B, B] when B &gt; 0; the mirror on the negative side stored as [e', e) with e running −B, −B−1 … then ·ratio.
    /// Disc diameter (int)(2·roundf(|x − B|) + 1) with x the far edge on the positive side and the NEAR edge on the negative side
    /// ([−1,−0) → 1, [−2,−1) → 3, [−4,−2) → 5). The list is sorted by lo and one more band [first.lo − 1, first.lo) is put in front.</summary>
    public static List<Layer> Ladder(float minRadius, float maxRadius, DofDevice p)
    {
        float lo = MathF.Floor(minRadius), hi = MathF.Ceiling(maxRadius);
        float bHalf = (float)(p.BandHalf * 2) * 0.5f, lin = (float)p.LinearLength + bHalf, q = p.Ratio;
        var list = new List<Layer>();
        static int Diam(float v) { float r = DofCrt.Roundf(MathF.Abs(v)); return (int)(r + r + 1f); }
        if (bHalf < lin)
        {
            float e = bHalf;
            do
            {
                float e2 = e + 1f;
                if (e <= hi && lo <= e2) list.Add(new Layer { Lo = e, Hi = e2, Diameter = Diam(e2 - bHalf) });
                e = e2;
            } while (e < lin);
        }
        if (lin < hi)
        {
            float e = lin;
            do
            {
                float e2 = e * q;
                if (e <= hi && lo <= e2) list.Add(new Layer { Lo = e, Hi = e2, Diameter = Diam(e2 - bHalf) });
                e = e2;
            } while (e < hi);
        }
        if (0f < bHalf && -bHalf <= hi && lo <= bHalf) list.Add(new Layer { Lo = -bHalf, Hi = bHalf, Diameter = 1 });
        float negLin = -lin;
        float en = negLin;
        if (bHalf < lin)
        {
            float e = -bHalf;
            do
            {
                float e2 = e + -1f;
                if (lo <= e && e2 <= hi) list.Add(new Layer { Lo = e2, Hi = e, Diameter = Diam(e + bHalf) });
                e = e2;
            } while (negLin < e);
        }
        while (lo < en)
        {
            float e2 = en * q;
            if (lo <= en && e2 <= hi) list.Add(new Layer { Lo = e2, Hi = en, Diameter = Diam(en + bHalf) });
            en = e2;
        }
        var sorted = list.OrderBy(l => l.Lo).ToList();   // a stable merge sort on lo (all lo values are distinct)
        float first = sorted[0].Lo; int d = 1;
        if (bHalf < MathF.Abs(first)) { float r = DofCrt.Roundf(MathF.Abs(first) - bHalf); d = (int)(r + r + 1f); }
        sorted.Insert(0, new Layer { Lo = -1f + first, Hi = first, Diameter = d });
        return sorted;
    }

    /// <summary>Every pixel goes to the last band with lo ≤ radius (upper-bound binary search on lo); occluded pixels whose
    /// radius is below every lo (the −65535 sentinel) are skipped.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    void Assign()
    {
        var ls = Layers!; int n = ls.Count; var lo = new float[n];
        for (int i = 0; i < n; i++) lo[i] = ls[i].Lo;
        int Find(float r) { int a = 0, cnt = n; while (cnt > 0) { int half = cnt >> 1; if (r < lo[a + half]) cnt = half; else { a += half + 1; cnt = cnt - 1 - half; } } return a - 1; }
        int np = _w * _h; var band = ScratchPool.Rent<int>(np);
        // a counting sort by band: each band's pixels end up contiguous, in increasing (row-major) order — the order Lumen appends them
        // the upper-bound search on the sorted lo values = the number of k with !(r < lo[k]), minus one (NaN: all n, as the search) — eight at a time
        void Bands(float[] radius)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float rb = ref MemoryMarshal.GetArrayDataReference(radius); ref int bb = ref MemoryMarshal.GetArrayDataReference(band);
                var los = new Vector256<float>[n]; for (int k = 0; k < n; k++) los[k] = Vector256.Create(lo[k]);
                for (; i + 8 <= np; i += 8)
                {
                    var r = Vector256.LoadUnsafe(ref rb, (nuint)i); var c = Vector256.Create(-1);
                    for (int k = 0; k < n; k++) c -= ~Vector256.LessThan(r, los[k]).AsInt32();   // ~mask = −1 where !(r < lo): c += 1
                    c.StoreUnsafe(ref bb, (nuint)i);
                }
            }
            for (; i < np; i++) band[i] = Find(radius[i]);
        }
        int[] Sort(float[] radius, bool skipBelow, out int[] idx, out int[] start, out int[] count)
        {
            count = new int[n]; start = new int[n]; int total = 0;
            Bands(radius);
            for (int i = 0; i < np; i++) { int k = band[i]; if (k >= 0) { count[k]++; total++; } else if (!skipBelow) throw new InvalidOperationException("radius below every band"); }
            for (int k = 1; k < n; k++) start[k] = start[k - 1] + count[k - 1];
            idx = _pooled ? ScratchPool.Rent<int>(Math.Max(1, total)) : new int[total];
            var pos = (int[])start.Clone();
            for (int i = 0; i < np; i++) { int k = band[i]; if (k >= 0) idx[pos[k]++] = i; }
            return idx;
        }
        _visIdx = Sort(RadiusVis!, false, out var vi, out var vs, out var vc);
        for (int k = 0; k < n; k++) { ls[k].VisBuf = vi; ls[k].VisStart = vs[k]; ls[k].VisCount = vc[k]; }
        if (RadiusOcc != null)
        {
            _occIdx = Sort(RadiusOcc, true, out var oi, out var os, out var oc);
            for (int k = 0; k < n; k++) { ls[k].OccBuf = oi; ls[k].OccStart = os[k]; ls[k].OccCount = oc[k]; }
        }
        ScratchPool.Return(band);
    }

    /// <summary>Give the pooled radius maps and pixel lists back (after a run without observers); the maps and layers are unset.</summary>
    void Release()
    {
        if (!_pooled) return;
        ScratchPool.Return(RadiusVis); ScratchPool.Return(RadiusOcc); ScratchPool.Return(_visIdx); ScratchPool.Return(_occIdx);
        RadiusVis = RadiusOcc = null; _visIdx = _occIdx = null; Layers = null; _pooled = false;
    }

    /// <summary>Radius maps, ladder, assignment. (Lumen's last loop rebuilds the pixel list of bands with diameter &lt; 1 from the
    /// matte; no band the ladder makes has a diameter below 1, so that branch never runs.)</summary>
    public void Prepare()
    {
        ComputeRadii();
        Layers = Ladder(MinRadius, MaxRadius, _p);
        Assign();
    }

    // ---- band images ----

    /// <summary>Write band <paramref name="src"/>'s pixels into the image blurred for band <paramref name="dst"/>.
    /// t = max(1 − (float)(uint)(long)log2((double)dst.diameter)·feather, 0.1); w = (src.hi − dst.hi)·t; a pixel takes its colour with alpha forced
    /// to 1, times the matte when one exists and is &gt; 0 (visible list only), times max((w + dst.hi) − radius, 0)·(1/w) when src.hi &gt; dst.hi.
    /// The occluded list is written first, then the visible list.</summary>
    void FillLayer(DofImage img, Layer src, Layer dst) => FillLayer(img, src, dst, 0, _h);

    /// <summary><see cref="FillLayer(DofImage, Layer, Layer)"/> restricted to the pixels of rows [<paramref name="ya"/>, <paramref name="yb"/>)
    /// (the lists are row-major, so that is one contiguous run of each).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    void FillLayer(DofImage img, Layer src, Layer dst, int ya, int yb)
    {
        float t = 1f - (float)(uint)(long)Math.Log2((double)dst.Diameter) * _p.Feather;
        t = t > 0.1f ? t : 0.1f;
        float wd = (src.Hi - dst.Hi) * t, inv = 1f / wd, edge = wd + dst.Hi;
        bool feather = src.Hi > dst.Hi;
        ref float ob = ref MemoryMarshal.GetArrayDataReference(img.D);
        var one = Vector128.Create(0f, 0f, 0f, 1f); var rgbMask = Vector128.Create(-1, -1, -1, 0).AsSingle();
        if (_hasOcc)
        {
            var occ = _buf.Occ!; ref float sb = ref MemoryMarshal.GetArrayDataReference(occ.D); var ro = RadiusOcc!;
            int y = ya, rowStart = ya * _w;
            foreach (int i in Range(src.Occ, ya, yb))
            {
                while (i >= rowStart + _w) { y++; rowStart += _w; }
                int s = occ.Off + (y * occ.Stride + (i - rowStart)) * 4;
                var px = (Vector128.LoadUnsafe(ref sb, (nuint)s) & rgbMask) | one;   // colour, alpha lane := 1.0
                if (feather) { float m = edge - ro[i]; m = m > 0f ? m : 0f; m *= inv; px *= Vector128.Create(m); }
                px.StoreUnsafe(ref ob, (nuint)(img.Off + i * 4));
            }
        }
        {
            var vis = _buf.Vis!; var matte = _hasMatte ? _buf.Matte : null; ref float sb = ref MemoryMarshal.GetArrayDataReference(vis.D); var rv = RadiusVis!;
            int y = ya, rowStart = ya * _w;
            foreach (int i in Range(src.Vis, ya, yb))
            {
                while (i >= rowStart + _w) { y++; rowStart += _w; }
                int x = i - rowStart, s = vis.Off + (y * vis.Stride + x) * 4;
                var px = (Vector128.LoadUnsafe(ref sb, (nuint)s) & rgbMask) | one;
                if (matte != null) { float mt = matte.D[matte.Idx(x, y)]; if (0f < mt) px *= Vector128.Create(mt); }
                if (feather) { float m = edge - rv[i]; m = m > 0f ? m : 0f; m *= inv; px *= Vector128.Create(m); }
                px.StoreUnsafe(ref ob, (nuint)(img.Off + i * 4));
            }
        }
    }

    /// <summary>Whether bands i+1, i−1 and i (the ones band i's image holds) have no pixel in rows [ya, yb).</summary>
    bool EmptyRows(List<Layer> ls, int i, int ya, int yb)
    {
        for (int k = Math.Max(0, i - 1); k <= Math.Min(ls.Count - 1, i + 1); k++)
            if (Range(ls[k].Vis, ya, yb).Length != 0 || Range(ls[k].Occ, ya, yb).Length != 0) return false;
        return true;
    }

    /// <summary>The entries of a row-major pixel list that lie in rows [ya, yb).</summary>
    ReadOnlySpan<int> Range(ReadOnlySpan<int> span, int ya, int yb)
    {
        if (ya <= 0 && yb >= _h) return span;
        int lo = LowerBound(span, ya * _w), hi = LowerBound(span, yb * _w);
        return span[lo..hi];
    }
    static int LowerBound(ReadOnlySpan<int> a, int v) { int lo = 0, hi = a.Length; while (lo < hi) { int m = (lo + hi) >>> 1; if (a[m] < v) lo = m + 1; else hi = m; } return lo; }

    // ---- per-band blur ----

    static readonly float[] Kernel5 = DofFilters.GaussianKernel(5, 1f, 1f);

    /// <summary>The band blur (band image, inRect, outRect, diameter, device). Diameter &lt; 3: the band image itself. Below the device's
    /// downscale diameter: disc filter of radius (diameter − 1)/2, then the 5×5 separable Gaussian. Otherwise the band is resampled
    /// (`ImageResample&lt;4&gt;`) by s = max(threshold/diameter, min(w,10)/w, min(h,10)/h) (the two size terms through `rcpps` + one Newton step),
    /// cropped to the scaled output rect ± 4 px, disc-filtered with radius ((int)(diameter·s) − 1)/2 (the crop is a view: the disc reads the
    /// whole resampled image), Gaussian-filtered, and resampled back by 1/s. When the crop is smaller than the resampled image the part of the
    /// intermediate outside it is uninitialised memory in Lumen (zero here); it only reaches output pixels outside the output rect.</summary>
    public static DofImage BlurLayer(DofImage layer, RectI rin, RectI rout, int diameter, DofDevice p) => BlurLayer(layer, rin, rout, diameter, p, null);

    /// <summary><see cref="BlurLayer(DofImage, RectI, RectI, int, DofDevice)"/> with the intermediates of the downscaled path taken from the
    /// <see cref="ScratchPool"/> and the result written into <paramref name="dst"/> (when given; at least the layer's size).</summary>
    static DofImage BlurLayer(DofImage layer, RectI rin, RectI rout, int diameter, DofDevice p, float[]? dst)
    {
        if (diameter < 3) return layer;
        bool pool = dst != null;
        if (diameter < p.DownscaleDiameter)
        {
            if (!pool) return DofFilters.Conv5(DofFilters.CircleFilter(layer, (diameter - 1) / 2), Kernel5, Kernel5);
            var c1 = ScratchPool.Rent<float>(layer.W * layer.H * 4);
            var res = DofFilters.Conv5(DofFilters.CircleFilter(layer, (diameter - 1) / 2, c1), Kernel5, Kernel5, dst);
            ScratchPool.Return(c1);
            return res;
        }
        float s = (float)p.DownscaleDiameter / (float)diameter;
        float fw = (float)layer.W, fh = (float)layer.H;
        float rw = IntelApprox.Rcp(fw), rh = IntelApprox.Rcp(fh);
        float sx = (fw < 10f ? fw : 10f) * ((1f - fw * rw) * rw + rw), sy = (fh < 10f ? fh : 10f) * ((1f - fh * rh) * rh + rh);
        s = sx > s ? sx : s;
        s = s > sy ? s : sy;
        int sw = (int)((float)(rin.X1 - rin.X0) * s), sh = (int)((float)(rin.Y1 - rin.Y0) * s);
        int ox = (int)((float)rin.X0 * s), oy = (int)((float)rin.Y0 * s);
        float inv = 1f / s;
        float[]? bSmall = pool ? ScratchPool.Rent<float>(sw * sh * 4) : null, bDisc = null, bConv = null, bBack = null;
        var small = DofFilters.Resample4(layer, sw, sh, (double)((float)ox * inv - (float)rin.X0), (double)((float)oy * inv - (float)rin.Y0), (double)inv, (double)inv, bSmall);
        int bx = (int)((float)rout.X0 * s), by = (int)((float)rout.Y0 * s);
        int cx0 = bx - 4, cy0 = by - 4, cx1 = (int)((float)(rout.X1 - rout.X0) * s) + 4 + bx, cy1 = (int)((float)(rout.Y1 - rout.Y0) * s) + 4 + by;
        if (cx0 < ox) cx0 = ox;
        if (cy0 < oy) cy0 = oy;
        if (sw + ox < cx1) cx1 = sw + ox;
        if (sh + oy < cy1) cy1 = sh + oy;
        var crop = new RectI(cx0 - ox, cy0 - oy, cx1 - ox, cy1 - oy);
        var view = small.View(crop) ?? throw new InvalidOperationException("Empty image data!");
        int d2 = (int)((float)diameter * s);
        if (pool) { bDisc = ScratchPool.Rent<float>(view.W * view.H * 4); bConv = ScratchPool.Rent<float>(view.W * view.H * 4); }
        var disc = DofFilters.CircleFilter(view, (d2 - 1) / 2, bDisc);
        var conv = DofFilters.Conv5(disc, Kernel5, Kernel5, bConv);
        DofImage back;
        if (view.W == sw && view.H == sh) back = conv;
        else
        {
            back = pool ? new DofImage(bBack = ScratchPool.RentZeroed<float>(sw * sh * 4), 4, sw, sh) : new DofImage(sw, sh, 4); int vx = -view.X0, vy = -view.Y0;
            for (int y = 0; y < conv.H; y++) Array.Copy(conv.D, y * conv.W * 4, back.D, ((y + vy) * sw + vx) * 4, conv.W * 4);
        }
        var outp = DofFilters.Resample4(back, layer.W, layer.H, (double)((float)rin.X0 * s - (float)ox), (double)((float)rin.Y0 * s - (float)oy), (double)s, (double)s, dst);
        if (pool) { ScratchPool.Return(bSmall); ScratchPool.Return(bDisc); ScratchPool.Return(bConv); ScratchPool.Return(bBack); }
        return outp;
    }

    // ---- run ----

    /// <summary>The run: bands from the highest radius index down; the band image holds the band's own pixels and those of the next
    /// nearer band at full weight plus the next farther band's pixels feathered; out = (1 − blurred.alpha)·out + blurred.</summary>
    public DofImage Run()
    {
        Prepare();
        var ls = Layers!; int n = ls.Count;
        var outp = new DofImage(_w, _h, 4); var o = outp.D;
        if (BandObserver != null)
        {
            // the test path: every band image and blurred band kept as an image of its own for the observer
            for (int i = n - 1; i >= 0; i--)
            {
                var img = new DofImage(_w, _h, 4);
                if (i < n - 1) FillLayer(img, ls[i + 1], ls[i]);
                if (i > 0) FillLayer(img, ls[i - 1], ls[i]);
                FillLayer(img, ls[i], ls[i]);
                var bl = BlurLayer(img, _in, _out, ls[i].Diameter, _p); var b = bl.D;
                DofFilters.Rows(_h, _w * 4, (ya, yb) => DofFilters.Composite(o, ya * _w * 4, b, ya * _w * 4, (yb - ya) * _w * 4));
                BandObserver?.Invoke(i, img, bl, outp);
            }
            return outp;
        }
        return RunStrips(outp, normalize: false);
    }

    /// <summary>Rows per strip of <see cref="RunStrips"/>.</summary>
    const int StripRows = 32;

    /// <summary>
    /// The band loop of <see cref="Run()"/> done strip by strip — for each strip of output rows every band in turn (n−1 … 0):
    /// its band image rows (the strip plus the rows the disc and the 5×5 pass read, cleared and filled from the same pixel lists in the same
    /// order), the disc filter rows the 5×5 pass reads, the 5×5 rows and the composite into the strip of the accumulation. Each output pixel
    /// gets exactly the band values and the composite sequence of the whole-image loop — every step is a function of rows the strip holds —
    /// but the working set of a strip stays in the cache instead of eleven full-image passes per band. Bands on the downscaled path
    /// (diameter ≥ the device's threshold; their crop and resample need the whole image) are blurred over the whole image first.
    /// With <paramref name="normalize"/> the strip is normalised (<see cref="Normalize"/>, per pixel) once its bands are done.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    DofImage RunStrips(DofImage outp, bool normalize)
    {
        var ls = Layers!; int n = ls.Count, w = _w, h = _h, np = w * h * 4; var o = outp.D;
        var pre = new float[]?[n];
        float[]? full = null;
        for (int i = n - 1; i >= 0; i--)
        {
            if (ls[i].Diameter < _p.DownscaleDiameter) continue;
            full ??= ScratchPool.Rent<float>(np);
            Array.Clear(full, 0, np);
            var img = new DofImage(full, 4, w, h);
            if (i < n - 1) FillLayer(img, ls[i + 1], ls[i]);
            if (i > 0) FillLayer(img, ls[i - 1], ls[i]);
            FillLayer(img, ls[i], ls[i]);
            pre[i] = ScratchPool.Rent<float>(np);
            BlurLayer(img, _in, _out, ls[i].Diameter, _p, pre[i]);
        }
        ScratchPool.Return(full);
        int maxR = 0; var discs = new (int[] Hw, float Weight)[n];
        for (int i = 0; i < n; i++)
        {
            int d = ls[i].Diameter;
            if (d >= 3 && pre[i] is null) { int r = (d - 1) / 2; discs[i] = DofFilters.CircleDisc(r); if (r > maxR) maxR = r; }
        }
        DofFilters.Rows(h, (long)w * n * 24, (ya, yb) =>
        {
            int bandRows = Math.Min(h, StripRows + 4 + 2 * maxR), discRows = Math.Min(h, StripRows + 4);
            var bandBuf = ScratchPool.Rent<float>(bandRows * w * 4); var discBuf = ScratchPool.Rent<float>(discRows * w * 4);
            var tmp = ScratchPool.Rent<float>(w * 4); var row = ScratchPool.Rent<float>(w * 4);
            for (int y0 = ya; y0 < yb; y0 += StripRows)
            {
                int y1 = Math.Min(yb, y0 + StripRows);
                for (int i = n - 1; i >= 0; i--)
                {
                    int d = ls[i].Diameter;
                    if (pre[i] is { } pb) { DofFilters.Composite(o, y0 * w * 4, pb, y0 * w * 4, (y1 - y0) * w * 4); continue; }
                    int r = d >= 3 ? (d - 1) / 2 : 0;
                    int ba = d >= 3 ? Math.Max(0, y0 - 2 - r) : y0, bb = d >= 3 ? Math.Min(h, y1 + 2 + r) : y1;
                    if (EmptyRows(ls, i, ba, bb))
                    {
                        // no pixel of the three bands in the rows this strip reads: the band image is +0 there, so are its disc and 5×5 rows
                        // (+0 sums, +0·k), and the composite is acc = (1 − 0)·acc + 0
                        DofFilters.CompositeZero(o, y0 * w * 4, (y1 - y0) * w * 4);
                        continue;
                    }
                    bandBuf.AsSpan(0, (bb - ba) * w * 4).Clear();
                    var bimg = new DofImage(bandBuf, 4, w, h, w, -ba * w * 4, 0, 0, w, h);
                    if (i < n - 1) FillLayer(bimg, ls[i + 1], ls[i], ba, bb);
                    if (i > 0) FillLayer(bimg, ls[i - 1], ls[i], ba, bb);
                    FillLayer(bimg, ls[i], ls[i], ba, bb);
                    if (d < 3) { DofFilters.Composite(o, y0 * w * 4, bandBuf, (y0 - ba) * w * 4, (y1 - y0) * w * 4); continue; }
                    int da = Math.Max(0, y0 - 2), db = Math.Min(h, y1 + 2);
                    DofFilters.CircleRows4(bimg, r, 2 * r + 1, discs[i].Hw, discs[i].Weight, discBuf, -da * w * 4, da, db);
                    var dimg = new DofImage(discBuf, 4, w, h, w, -da * w * 4, 0, 0, w, h);
                    for (int y = y0; y < y1; y++) { DofFilters.Conv5Row(dimg, Kernel5, Kernel5, y, tmp, row, 0); DofFilters.Composite(o, y * w * 4, row, 0, w * 4); }
                }
                if (normalize) NormalizeRows(o, y0 * w * 4, y1 * w * 4);
            }
            ScratchPool.Return(bandBuf); ScratchPool.Return(discBuf); ScratchPool.Return(tmp); ScratchPool.Return(row);
        }, minRows: StripRows);
        foreach (var b in pre) ScratchPool.Return(b);
        return outp;
    }

    /// <summary>Run, then when <paramref name="post"/> is set normalise, soften edges and add noise.</summary>
    public DofImage Run(bool post, Action<string, DofImage>? observer = null) => Run(post, observer, pooledOutput: false);

    /// <summary>With <paramref name="pooledOutput"/> (and no observers) the result's array comes from the <see cref="ScratchPool"/>: it may be
    /// longer than W·H·4 (index it by the image's own size) and the caller gives it back.</summary>
    internal DofImage Run(bool post, Action<string, DofImage>? observer, bool pooledOutput)
    {
        if (observer is null && BandObserver is null)
        {
            // no observer — the temporaries are pooled and, with the post stage, the strip is normalised right after its last band
            // (per pixel: the same values as the separate pass)
            _pooled = true;
            try
            {
                using (Diagnostics.PhaseLog.Time("dof.blur.prepare")) Prepare();
                var target = pooledOutput ? new DofImage(ScratchPool.RentZeroed<float>(_w * _h * 4), 4, _w, _h) : new DofImage(_w, _h, 4);
                DofImage o;
                using (Diagnostics.PhaseLog.Time("dof.blur.bands")) o = RunStrips(target, normalize: post);
                if (!post) return o;
                using (Diagnostics.PhaseLog.Time("dof.blur.edge")) EdgeSoften(o, RadiusVis!);
                using (Diagnostics.PhaseLog.Time("dof.blur.noise")) AddNoise(o, _p, 0);
                return o;
            }
            finally { Release(); }
        }
        var outp = Run();
        observer?.Invoke("pre", outp);
        if (!post) return outp;
        Normalize(outp); observer?.Invoke("norm", outp);
        EdgeSoften(outp, RadiusVis!); observer?.Invoke("edge", outp);
        AddNoise(outp, _p, 0);
        return outp;
    }

    // ---- post stage ----

    /// <summary>`lt::NormalizeImage`: px = max(0, px)·r', r = rcpps(alpha), r' = (1 − alpha·r)·r + r.</summary>
    public static void Normalize(DofImage img)
    {
        var d = img.D;
        DofFilters.Rows(img.H, img.W * 2, (ya, yb) => NormalizeRows(d, ya * img.W * 4, yb * img.W * 4));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static void NormalizeRows(float[] d, int q0, int q1)
    {
        ref float db = ref MemoryMarshal.GetArrayDataReference(d);
        var zero = Vector128<float>.Zero;
        for (int q = q0; q < q1; q += 4)
        {
            float a = d[q + 3], r = IntelApprox.Rcp(a), m = (1f - a * r) * r + r;
            var v = Vector128.LoadUnsafe(ref db, (nuint)q);
            (Vector128.Create(m) * Vector128.ConditionalSelect(Vector128.GreaterThan(zero, v), zero, v)).StoreUnsafe(ref db, (nuint)q);
        }
    }

    /// <summary>Edge softening (out, blur): L = |(((4·r − up) − down) − left) − right| on the visible radius map (interior only — the
    /// 1-px border of that freshly allocated image is never written in Lumen, zero here); M = 4 &lt; L ? 1 : 0;
    /// M = ImageCircleFilter&lt;float&gt;(M, 7); M = 0 &lt; M ? 1 : M; M = conv(M, k, k) with k = the Gaussian kernel (5, 1.1, 1.0);
    /// G = conv(out, k, k); out = (G − out)·M + out.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void EdgeSoften(DofImage img, float[] radius)
    {
        int w = img.W, h = img.H, np = w * h;
        // M = 4 < L on the interior, 0 on the 1-px border (as bytes)
        var mask = ScratchPool.Rent<byte>(np);
        DofFilters.Rows(h, w, (ya, yb) =>
        {
            for (int y = ya; y < yb; y++)
            {
                int b = y * w;
                if (y == 0 || y == h - 1) { mask.AsSpan(b, w).Clear(); continue; }
                mask[b] = 0; mask[b + w - 1] = 0;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = b + x;
                    float l = MathF.Abs((((radius[i] * 4f - radius[i - w]) - radius[i + w]) - radius[i - 1]) - radius[i + 1]);
                    mask[i] = 4f < l ? (byte)1 : (byte)0;
                }
            }
        });
        // M = ImageCircleFilter<float>(M, 7) then 0 < M ? 1 : M. On a 0/1 image every partial sum of the disc filter is an exact small
        // integer, so the filter output is weight·count with count = the number of set pixels under the (clamped) disc, and the second step
        // makes it 1 exactly where count > 0: a dilation by the disc. Computed as such (per source row the distance to the nearest set pixel,
        // the disc row j covering it when that distance ≤ its half-width) — the same 0/1 values, bit for bit.
        const int R = 7; const int N = 2 * R + 1;
        var hw = new int[N]; for (int j = 0; j < N; j++) { int dy = j - R; hw[j] = (int)Math.Sqrt((double)(R * R - dy * dy)); }
        var dist = ScratchPool.Rent<int>(np);
        var rowAny = new bool[h];
        DofFilters.Rows(h, w, (ya, yb) =>
        {
            const int Far = 1 << 20;
            for (int y = ya; y < yb; y++)
            {
                int b = y * w, last = -Far; bool any = false;
                for (int x = 0; x < w; x++) { if (mask[b + x] != 0) { last = x; any = true; } dist[b + x] = x - last; }
                last = 2 * Far;
                for (int x = w - 1; x >= 0; x--) { if (mask[b + x] != 0) last = x; int dr = last - x; if (dr < dist[b + x]) dist[b + x] = dr; }
                rowAny[y] = any;
            }
        });
        var m2 = ScratchPool.Rent<float>(np);
        var m2Any = new bool[h];
        DofFilters.Rows(h, w * 4, (ya, yb) =>
        {
            ref int dbase = ref MemoryMarshal.GetArrayDataReference(dist);
            ref float mb2 = ref MemoryMarshal.GetArrayDataReference(m2);
            var rows = new int[N];
            for (int y = ya; y < yb; y++)
            {
                bool anyRow = false;
                for (int j = 0; j < N; j++) { int yy = Math.Clamp(y - R + j, 0, h - 1); rows[j] = yy * w; anyRow |= rowAny[yy]; }
                int ob = y * w;
                if (!anyRow) { m2.AsSpan(ob, w).Clear(); m2Any[y] = false; continue; }
                m2Any[y] = true;
                int x = 0;
                if (Vector256.IsHardwareAccelerated)
                {
                    var oneF = Vector256.Create(1f);
                    for (; x + 8 <= w; x += 8)
                    {
                        var hit = Vector256<int>.Zero;
                        for (int j = 0; j < N; j++) hit |= Vector256.LessThanOrEqual(Vector256.LoadUnsafe(ref dbase, (nuint)(rows[j] + x)), Vector256.Create(hw[j]));
                        (hit.AsSingle() & oneF).StoreUnsafe(ref mb2, (nuint)(ob + x));
                    }
                }
                for (; x < w; x++)
                {
                    bool hit = false;
                    for (int j = 0; j < N && !hit; j++) hit = dist[rows[j] + x] <= hw[j];
                    m2[ob + x] = hit ? 1f : 0f;
                }
            }
        });
        ScratchPool.Return(mask); ScratchPool.Return(dist);
        // rows whose 5-row window holds no set pixel: the separable Gaussian gives exactly +0 there (+0·k = +0, +0 + +0 = +0)
        var mbZero = new bool[h];
        for (int y = 0; y < h; y++) { bool z = true; for (int k = -2; k <= 2 && z; k++) z = !m2Any[Math.Clamp(y + k, 0, h - 1)]; mbZero[y] = z; }
        var k5 = DofFilters.GaussianKernel(5, 1.1f, 1f);
        var mb = DofFilters.ConvSeparable(m2, w, h, 1, k5, k5, ScratchPool.Rent<float>(np), y => mbZero[y]);
        var d = img.D;
        // out = (G − out)·M + out. Where M is +0: (G − out)·(+0) is ±0, and out + ±0 = out unless out is −0 (or not finite) — such rows are
        // computed in full; the others are left as they are (bit for bit what the formula gives).
        var skip = new bool[h];
        for (int y = 0; y < h; y++)
        {
            if (!mbZero[y]) continue;
            bool plain = true; int b = y * w * 4;
            for (int i = 0; i < w * 4 && plain; i++) { uint u = BitConverter.SingleToUInt32Bits(d[b + i]); plain = u != 0x80000000u && (u & 0x7f800000u) != 0x7f800000u; }
            skip[y] = plain;
        }
        var g = DofFilters.ConvSeparable(d, w, h, 4, k5, k5, ScratchPool.Rent<float>(np * 4), y => skip[y]);
        DofFilters.Rows(h, w * 4, (ya, yb) =>
        {
            ref float gb = ref MemoryMarshal.GetArrayDataReference(g);
            ref float db2 = ref MemoryMarshal.GetArrayDataReference(d);
            for (int y = ya; y < yb; y++)
            {
                if (skip[y]) continue;
                for (int x = 0; x < w; x++)
                {
                    int q = (y * w + x) * 4;
                    var mm = Vector128.Create(mbZero[y] ? 0f : mb[y * w + x]);
                    var D = Vector128.LoadUnsafe(ref db2, (nuint)q);
                    ((Vector128.LoadUnsafe(ref gb, (nuint)q) - D) * mm + D).StoreUnsafe(ref db2, (nuint)q);
                }
            }
        });
        ScratchPool.Return(m2); ScratchPool.Return(mb); ScratchPool.Return(g);
    }

    /// <summary>`lt::AddNoise(image, device, level)`: amp = noise / exp2f(level) (level 2 → /4);
    /// `Tiler::Run` over (0,0,w,h) in 512×512 tiles, each seeded s = (y0·65536 + x0) ^ 0xb36534e5 and advanced once per pixel, row-major
    /// (xorshift32: s ^= s &lt;&lt; 13; s ^= s &gt;&gt; 17; s ^= s &lt;&lt; 5). m = ((R + B) + G)·0.33333334; q = rsqrtss(m);
    /// sq = ((m·q)·q − 3)·((m·q)·−0.5), 0 when m == 0; n = (((amp·−4.656613e-10)·(float)(int)s)·rcpss(m))·sq; RGB = max(n·px + px, 0).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void AddNoise(DofImage img, DofDevice p, int level)
    {
        float div = level == 2 ? 4f : MuslMath.Exp2f((float)level), amp = p.NoiseAmp / div;
        var d = img.D; int w = img.W, h = img.H;
        float ac = amp * -4.656612873077393e-10f;   // the first product of `amp · −2^−31 · (float)s · rcp(m) · sq` (left to right)
        Span<int> ss = stackalloc int[8];
        foreach (var t in Tiler.Rects(new RectI(0, 0, w, h), 512, 512))
        {
            uint s = (uint)(t.Y0 * 0x10000 + t.X0) ^ 0xb36534e5u;
            for (int y = t.Y0; y < t.Y1; y++)
            {
                int x = t.X0;
                // eight pixels per step — the xorshift states drawn in order, then each lane the per-pixel operations below
                if (Isp.Soa8.Supported)
                {
                    ref float db = ref MemoryMarshal.GetArrayDataReference(d);
                    var zero = Vector256<float>.Zero; var third = Vector256.Create(0.33333334f); var half = Vector256.Create(-0.5f); var m3 = Vector256.Create(-3f); var acv = Vector256.Create(ac);
                    for (; x + 8 <= t.X1; x += 8)
                    {
                        for (int k = 0; k < 8; k++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; ss[k] = (int)s; }
                        ref float pp = ref Unsafe.Add(ref db, (y * w + x) * 4);
                        Isp.Soa8.Load(ref pp, out var R, out var G, out var B, out var A);
                        var m = ((B + R) + (zero + G)) * third;
                        var rs = IntelApprox.ReciprocalSqrt(m); var a = m * rs; var hlf = a * half; var sq = (a * rs + m3) * hlf;
                        sq = Vector256.ConditionalSelect(Vector256.Equals(m, zero), zero, sq);
                        var nz = ((acv * Vector256.ConvertToSingle(Vector256.Create((ReadOnlySpan<int>)ss))) * IntelApprox.Reciprocal(m)) * sq;
                        var v0 = nz * R + R; var v1 = nz * G + G; var v2 = nz * B + B;
                        Isp.Soa8.Store(ref pp, Vector256.ConditionalSelect(Vector256.GreaterThan(v0, zero), v0, zero), Vector256.ConditionalSelect(Vector256.GreaterThan(v1, zero), v1, zero),
                                       Vector256.ConditionalSelect(Vector256.GreaterThan(v2, zero), v2, zero), A);
                    }
                }
                for (; x < t.X1; x++)
                {
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    int q = (y * w + x) * 4;
                    float r = d[q], g = d[q + 1], b = d[q + 2];
                    float m = ((b + r) + (0f + g)) * 0.33333334f;
                    float rs = IntelApprox.Rsqrt(m), a = m * rs, hlf = a * -0.5f, sq = (a * rs + -3f) * hlf;
                    if (m == 0f) sq = 0f;
                    float nz = amp * -4.656612873077393e-10f * (float)(int)s * IntelApprox.Rcp(m) * sq;
                    float v0 = nz * r + r, v1 = nz * g + g, v2 = nz * b + b;
                    d[q] = v0 > 0f ? v0 : 0f; d[q + 1] = v1 > 0f ? v1 : 0f; d[q + 2] = v2 > 0f ? v2 : 0f;
                }
            }
        }
    }
}
